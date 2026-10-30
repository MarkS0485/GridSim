using System;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Converter-interfaced plant (battery, PV, wind, VSC HVDC) in the short-circuit studies. Such a unit has no EMF
/// behind a subtransient reactance, so it must enter the fault network as a <b>current-limited source</b>, not
/// as an X''d shunt, which claimed several times rated current. The tests pin:
///   - the IEC 60909-0:2016 current-source term <c>sum_j |Z_kj| I_skPF,j / |Z_kk|</c> against hand networks
///     (three-phase, partial currents, branch currents, line-to-line);
///   - the operating-point study (<see cref="FaultAnalysis"/>) holding each converter at or below
///     <c>k * I_r</c> with reactive priority and the K-factor voltage-support characteristic;
///   - that synchronous-only models are unchanged, and the new factor-once LU reproduces the historical solve.
/// </summary>
public class ConverterFaultContributionTests
{
    private const double BaseMva = 100.0;
    private const double Kv = 132.0;
    private const double Xg = 0.20;   // synchronous grid source X''d (pu on 100 MVA)
    private const double Xl = 0.10;   // line reactance (pu)

    // Synchronous source at bus 1, a reactive line to bus 2, a 100 MVA unit of the given type at bus 2.
    private static GridModel Radial(string unitType, bool? converter = null, double? kPu = null, double pg = 0, double qg = 0) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pv, BaseKv: Kv) },
        Generators = new[]
        {
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Name: "grid", Type: "ccgt"),
            new Generator(Bus: 2, Pg: pg, Qg: qg, MBase: 100.0, Name: "unit", Type: unitType,
                ConverterInterfaced: converter, ConverterFaultCurrentPu: kPu),
        },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: Xl) },
    };

    private static GridModel WithGenerators(params Generator[] gens) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pv, BaseKv: Kv) },
        Generators = gens,
        Branches = new[] { new Branch(1, 2, R: 0.0, X: Xl) },
    };

    private static double IBaseKa => BaseMva / (Math.Sqrt(3.0) * Kv);

    // ---- classification ----

    [Theory]
    [InlineData("battery", true)]
    [InlineData("storage", true)]
    [InlineData("BESS", true)]
    [InlineData("wind_offshore", true)]
    [InlineData("wind-onshore", true)]
    [InlineData("solar", true)]
    [InlineData("interconnector", true)]
    [InlineData("pumped-storage", false)]
    [InlineData("pumpedstorage", false)]
    [InlineData("ccgt", false)]
    [InlineData("nuclear", false)]
    [InlineData("", false)]
    public void Converter_types_are_classified(string type, bool expected)
        => Assert.Equal(expected, ConverterPlant.IsConverterType(type));

    [Fact]
    public void Explicit_flag_overrides_the_type_and_k_defaults()
    {
        Assert.False(new Generator(1, 0, Type: "battery", ConverterInterfaced: false).IsConverterInterfaced);
        Assert.True(new Generator(1, 0, Type: "ccgt", ConverterInterfaced: true).IsConverterInterfaced);
        Assert.Equal(Generator.DefaultConverterFaultCurrentPu, new Generator(1, 0).EffectiveConverterFaultCurrentPu);
        Assert.Equal(1.5, new Generator(1, 0, ConverterFaultCurrentPu: 1.5).EffectiveConverterFaultCurrentPu);
    }

    // ---- IEC 60909 ----

    [Fact]
    public void Iec_three_phase_adds_exactly_k_times_rated_for_a_converter_at_the_fault_bus()
    {
        // Converter at the fault bus: Z_kj = Z_kk, so its share is exactly k * Ir = 1.2 pu.
        Iec60909.Duty duty = Iec60909.ThreePhase(Radial("battery"), faultBusId: 2);
        double sync = 1.10 / (Xg + Xl);
        Assert.Equal(sync + 1.2, duty.IkppPu, 10);
        Assert.Equal(1.2 * IBaseKa, duty.ConverterIkppKa, 10);
        Assert.Equal(Xg + Xl, duty.TheveninXPu, 12);           // the converter is not a shunt in Zk
        // No DC offset or AC decay on the converter share.
        Assert.Equal(duty.Kappa * Math.Sqrt(2) * sync * IBaseKa + Math.Sqrt(2) * 1.2 * IBaseKa, duty.PeakIpKa, 9);
        // Moved 2026-10 (R095, authorised by Mark's best-judgement ruling): this asserted Ib == Ik'' (10 dp), the mu = 1
        // reduction ThreePhase used to apply to every fault. The grid machine here feeds Ik''_G / I_rG = 1.1 / 0.3 =
        // 3.67 times its rating, which IEC 60909-0 counts as near to the generator (> 2), so its share now decays by
        // mu, weighted by its terminal voltage dip |dU''_G| / c = Xg / (Xg + Xl) = 2/3 (the multiple-fed formula,
        // IEC 60909-0:2016 9.1.7). The converter share still has no AC decay (mu = 1). Before:
        // Assert.Equal(duty.IkppKa, duty.BreakingIbKa, 10). After: the hand formula below.
        // Moved 2026-10 (END-PASS VERIFY row 4, end pass; IEC 60909-0:2016 contents, read in endpass-v, Ruling R1 and
        // Mark's go on the end-pass moves 2026-10-11): comment-only citation correction, no number moves. Before -> after:
        // "IEC 60909-0:2016 10.1.6" -> "IEC 60909-0:2016 9.1.7" (meshed breaking current).
        double mu = Iec60909.BreakingFactorMu(sync / 1.0, 0.10);   // the grid machine is rated 100 MVA = 1 pu
        double expectedIb = (sync + 1.2) * IBaseKa - (Xg / (Xg + Xl)) * (1.0 - mu) * sync * IBaseKa;
        Assert.Equal(expectedIb, duty.BreakingIbKa, 10);
        Assert.True(duty.BreakingIbKa < duty.IkppKa);
    }

    [Fact]
    public void Iec_converter_share_is_far_below_the_old_xd_treatment()
    {
        Iec60909.Duty asConverter = Iec60909.ThreePhase(Radial("battery"), 2);
        Iec60909.Duty asMachine = Iec60909.ThreePhase(Radial("battery", converter: false), 2);
        // As an X''d = 0.2 source the 100 MVA unit alone claimed 1.1/0.2 = 5.5 pu; as a converter 1.2 pu.
        double machineShare = asMachine.IkppPu - 1.10 / (Xg + Xl);
        double converterShare = asConverter.IkppPu - 1.10 / (Xg + Xl);
        Assert.True(machineShare > 3.0 * converterShare, $"machine {machineShare}, converter {converterShare}");
        Assert.Equal(0.0, asMachine.ConverterIkppKa);
    }

    [Fact]
    public void Iec_remote_converter_share_scales_by_transfer_over_driving_point_impedance()
    {
        // Fault at bus 1. Bus 2 has no path to earth except through bus 1, so Z_12 = Z_11 = j Xg: all of the
        // converter's current reaches the fault.
        Iec60909.Duty duty = Iec60909.ThreePhase(Radial("solar", kPu: 1.1), faultBusId: 1);
        Assert.Equal(1.10 / Xg + 1.1, duty.IkppPu, 10);
    }

    [Fact]
    public void Iec_partial_currents_mark_the_converter_and_sum_to_the_total()
    {
        Iec60909.FaultContribution fc = Iec60909.PartialThreePhase(Radial("battery"), faultBusId: 2);
        Iec60909.MachinePartialCurrent conv = Assert.Single(fc.Machines, m => m.IsConverter);
        Assert.Equal(1.2, conv.IkppPu, 10);
        Assert.Equal(1.0, conv.Mu);
        Assert.Equal(conv.IkppKa, conv.BreakingIbKa);
        Assert.Equal(fc.Machines.Sum(m => m.IkppPu), fc.TotalIkppPu, 10);
        Assert.Equal(Iec60909.ThreePhase(Radial("battery"), 2).IkppPu, fc.TotalIkppPu, 10);
    }

    [Fact]
    public void Iec_branch_current_carries_the_converter_infeed_to_a_remote_fault()
    {
        // Fault at bus 1: the only current in the line is the converter's own k * Ir.
        var line = Assert.Single(Iec60909.BranchFaultCurrents(Radial("battery"), faultBusId: 1));
        Assert.Equal(1.2, line.FromEndPu, 9);
        Assert.Equal(1.2, line.ToEndPu, 9);
        // And with no converter the line carries nothing (bus 2 has no source).
        GridModel noUnit = WithGenerators(new Generator(Bus: 1, Pg: 0.0, MBase: 100.0));
        var none = Assert.Single(Iec60909.BranchFaultCurrents(noUnit, faultBusId: 1));
        Assert.Equal(0.0, none.FromEndPu, 12);
    }

    [Fact]
    public void Iec_line_to_line_adds_the_sequence_formula_driven_by_the_converter_term()
    {
        // Z1 = Z2 = j(Xg + Xl) (converter open in both); share = sqrt(3) * |Z_kk| * I_PF / |Z1 + Z2| = sqrt(3)/2 * 1.2.
        Iec60909.Duty duty = Iec60909.LineToLine(Radial("battery"), faultBusId: 2);
        double sync = Math.Sqrt(3) * 1.10 / (2 * (Xg + Xl));
        Assert.Equal(sync + Math.Sqrt(3) / 2 * 1.2, duty.IkppPu, 10);
    }

    [Fact]
    public void Iec_converter_only_model_is_refused_with_a_clear_reason()
    {
        GridModel m = WithGenerators(new Generator(Bus: 2, Pg: 0, MBase: 100, Type: "battery"));
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.ThreePhase(m, 2));
        Assert.Contains("converter-interfaced", ex.Message);
    }

    [Fact]
    public void Zero_k_unit_feeds_no_fault_current_in_either_study()
    {
        // A line-commutated HVDC link (or a power-flow-only control unit): neither a source impedance nor a source.
        GridModel m = Radial("interconnector", kPu: 0.0);
        Assert.Equal(1.10 / (Xg + Xl), Iec60909.ThreePhase(m, 2).IkppPu, 10);
        Assert.Equal(1.0 / (Xg + Xl), FaultAnalysis.ThreePhase(m, Flat(m), 2).FaultCurrentPu, 9);
    }

    [Fact]
    public void Power_flow_only_unit_is_no_fault_source()
    {
        GridModel m = WithGenerators(
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Type: "ccgt"),
            new Generator(Bus: 2, Pg: 0.0, MBase: 500.0, Type: "grid", PowerFlowOnly: true));
        Assert.Equal(1.10 / (Xg + Xl), Iec60909.ThreePhase(m, 2).IkppPu, 10);
        Assert.Equal(1.0 / (Xg + Xl), FaultAnalysis.ThreePhase(m, Flat(m), 2).FaultCurrentPu, 9);
    }

    [Fact]
    public void Unit_transformer_is_in_series_with_the_machine()
    {
        // X''d 0.2 + unit transformer 0.1 on a 100 MVA unit: the source reactance is 0.3 pu.
        GridModel m = WithGenerators(new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Type: "ccgt", UnitTransformerXPu: 0.1));
        Assert.Equal(1.10 / (0.3 + Xl), Iec60909.ThreePhase(m, 2).IkppPu, 10);
        Assert.Equal(1.0 / (0.3 + Xl), FaultAnalysis.ThreePhase(m, Flat(m), 2).FaultCurrentPu, 9);
    }

    [Fact]
    public void Json_cases_carry_the_converter_fault_fields()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "uk");
        GridModel m = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(dir);
        Generator moyle = m.Generators.Single(g => g.Name == "Moyle");            // LCC: tagged k = 0
        Generator nsl = m.Generators.Single(g => g.Name == "North Sea Link");     // VSC: default k
        Assert.True(moyle.IsConverterInterfaced);
        Assert.Equal(0.0, moyle.EffectiveConverterFaultCurrentPu);
        Assert.Equal(Generator.DefaultConverterFaultCurrentPu, nsl.EffectiveConverterFaultCurrentPu);
    }

    // ---- operating-point fault study ----

    private static PowerFlowResult Flat(GridModel m) => new()
    {
        Converged = true, Iterations = 0, MaxMismatch = 0,
        BusIds = m.Buses.Select(b => b.Id).ToArray(),
        Vm = m.Buses.Select(_ => 1.0).ToArray(),
        VaDeg = m.Buses.Select(_ => 0.0).ToArray(),
        PInjMw = new double[m.BusCount], QInjMvar = new double[m.BusCount],
        TotalLossMw = 0, TotalLossMvar = 0, LineFlows = Array.Empty<LineFlow>(),
    };

    [Fact]
    public void Operating_point_bolted_fault_at_a_converter_holds_it_at_its_limit_all_reactive()
    {
        GridModel m = Radial("battery", pg: 50.0);
        FaultResult r = FaultAnalysis.ThreePhase(m, Flat(m), faultBusId: 2);

        ConverterFaultInfeed c = Assert.Single(r.Converters!);
        Assert.True(r.ConvertersConverged);
        Assert.True(c.AtCurrentLimit);
        Assert.Equal(1.2, c.CurrentPerRated, 6);
        Assert.Equal(1.2, c.ReactiveCurrentPu, 6);            // full dip * K=2 saturates: reactive priority
        Assert.Equal(0.0, c.ActiveCurrentPu, 6);              // ... so the 0.5 pu active current gives way
        // Superposition injects the CHANGE from the prefault current: dI = -j1.2 - 0.5 at the fault bus, so
        // If = V0/Zkk + dI = -j/(Xg+Xl) - 0.5 - j1.2.
        Assert.Equal(new Complex(-0.5, -(1.0 / (Xg + Xl) + 1.2)).Magnitude, r.FaultCurrentPu, 6);
    }

    [Fact]
    public void Operating_point_converter_follows_the_k_factor_below_its_limit()
    {
        // Fault at bus 1 through a fault reactance so the converter's terminal only dips partly.
        GridModel m = Radial("battery", kPu: 3.0);
        FaultResult r = FaultAnalysis.ThreePhase(m, Flat(m), faultBusId: 1, faultReactancePu: 0.05);
        ConverterFaultInfeed c = Assert.Single(r.Converters!);
        Assert.True(r.ConvertersConverged);
        Assert.False(c.AtCurrentLimit);
        double dip = 1.0 - c.TerminalVoltagePu;
        Assert.True(dip > 0.05 && dip < 0.9, $"dip {dip}");
        Assert.Equal(FaultAnalysis.DefaultConverterReactiveGain * dip, c.ReactiveCurrentPu, 6);
    }

    [Fact]
    public void Operating_point_converter_contributes_far_less_than_an_xd_machine()
    {
        GridModel conv = Radial("battery");
        GridModel mach = Radial("battery", converter: false);
        double iConv = FaultAnalysis.ThreePhase(conv, Flat(conv), 2).FaultCurrentPu;
        double iMach = FaultAnalysis.ThreePhase(mach, Flat(mach), 2).FaultCurrentPu;
        double sync = 1.0 / (Xg + Xl);
        Assert.Equal(sync + 5.0, iMach, 6);                  // X''d 0.2 => 1/0.2 = 5 pu from the unit
        Assert.Equal(sync + 1.2, iConv, 6);
    }

    [Fact]
    public void Synchronous_only_models_report_no_converters()
    {
        GridModel m = Radial("ccgt");
        FaultResult r = FaultAnalysis.ThreePhase(m, Flat(m), 2);
        Assert.Null(r.Converters);
        Assert.Equal(1.0 / (Xg + Xl) + 1.0 / Xg, r.FaultCurrentPu, 9);
    }

    // ---- solver ----

    [Fact]
    public void Factor_once_lu_is_bit_identical_to_the_one_shot_solve()
    {
        var rng = new Random(7);
        const int n = 23;
        var a = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = rng.NextDouble() < 0.3 ? Complex.Zero : new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        DenseComplexLu lu = DenseComplexLuSolver.FactorDense(a, n);
        for (int t = 0; t < 4; t++)
        {
            var b = new Complex[n];
            for (int i = 0; i < n; i++) b[i] = new Complex(rng.NextDouble(), rng.NextDouble());
            Complex[] x1 = DenseComplexLuSolver.SolveDense(a, b, n);
            Complex[] x2 = lu.Solve(b);
            for (int i = 0; i < n; i++) Assert.Equal(x1[i], x2[i]);   // exact, not approximate
        }
    }
}
