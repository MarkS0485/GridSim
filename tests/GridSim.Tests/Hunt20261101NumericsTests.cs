using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the numerics, protection and estimation findings of the 2026-11-01 bug hunt (bug store ids in
/// each test name). Each test fails on the code before its fix and passes after it.
/// </summary>
public class Hunt20261101NumericsTests
{
    private const double BaseMva = 100.0;

    // ================================================================================================================
    // #163: IEC 60909 double-line-to-earth at a bus with no earth path is the line-to-line fault, not an all-zero duty
    // ================================================================================================================

    // A generator (X''d 0.1 on 100 MVA) at a 33 kV bus with a lone line and no transformer windings: no earthed neutral
    // anywhere, so the fault bus is ungrounded in the zero sequence. Z1 = Z2 = j0.1 pu at bus 1.
    private static GridModel UngroundedGenerator33kV() => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33.0), new Bus(2, BusType.Pq, BaseKv: 33.0) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.1) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.10, R0: 0.0, X0: 0.20) },
    };

    [Fact]
    public void B163_ungrounded_double_line_to_earth_is_the_line_to_line_duty()
    {
        GridModel m = UngroundedGenerator33kV();
        Assert.False(Iec60909.ZeroSequenceThevenin(m, 1).Grounded);

        Iec60909.Duty dlg = Iec60909.DoubleLineToGround(m, 1);
        Iec60909.Duty ll = Iec60909.LineToLine(m, 1);

        // The worked case of the finding: sqrt(3) x 1.1 / (0.1 + 0.1) = 9.526 pu line current, on the 33 kV current base
        // 100 / (sqrt(3) x 33) kA: 1.1 / 0.2 x 100 / 33 = 16.667 kA. Before the fix the duty was all zero.
        double expectedKa = Math.Sqrt(3.0) * 1.1 / 0.2 * (BaseMva / (Math.Sqrt(3.0) * 33.0));
        Assert.Equal(expectedKa, dlg.IkppKa, 12);
        Assert.Equal(16.666666666666668, dlg.IkppKa, 12);

        // The duty IS the line-to-line duty, bit for bit, in every current the breaker check reads.
        Assert.Equal(BitConverter.DoubleToInt64Bits(ll.IkppKa), BitConverter.DoubleToInt64Bits(dlg.IkppKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ll.PeakIpKa), BitConverter.DoubleToInt64Bits(dlg.PeakIpKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ll.BreakingIbKa), BitConverter.DoubleToInt64Bits(dlg.BreakingIbKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ll.ThermalIthKa), BitConverter.DoubleToInt64Bits(dlg.ThermalIthKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ll.DcComponentKa), BitConverter.DoubleToInt64Bits(dlg.DcComponentKa));
        Assert.True(double.IsNaN(dlg.DlgLineToEarthRatio));   // no earth current: the line current is judged as it stands
        Assert.Equal(SteadyStateBasis.UnbalancedNoDecay, dlg.SteadyBasis);

        // Method C takes the same line-to-line connection.
        Iec60909.Duty dlgC = Iec60909.DoubleLineToGround(m, 1, peakFactorMethod: PeakFactorMethod.MethodC);
        Iec60909.Duty llC = Iec60909.LineToLine(m, 1, peakFactorMethod: PeakFactorMethod.MethodC);
        Assert.Equal(BitConverter.DoubleToInt64Bits(llC.PeakIpKa), BitConverter.DoubleToInt64Bits(dlgC.PeakIpKa));

        // The single-line-to-earth fault on the same bus still has no current at all (no return path, one phase).
        Assert.Equal(0.0, Iec60909.SingleLineToGround(m, 1).IkppKa);
    }

    [Fact]
    public void B163_ungrounded_double_line_to_earth_breaker_below_the_line_current_is_not_adequate()
    {
        GridModel m = UngroundedGenerator33kV();
        Iec60909.Duty dlg = Iec60909.DoubleLineToGround(m, 1);
        // A 36 kV breaker rated for 10 kA breaking: below the 16.67 kA line current. Before the fix the all-zero duty
        // made this read ADEQUATE with margins equal to the ratings.
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 36.0, MakingPeakKa: 1e6, BreakingSymKa: 10.0, ShortTimeWithstandKa: 1e6);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(dlg, rating, 33.0);
        Assert.False(a.BreakingOk);
        Assert.False(a.Adequate);
        Assert.Equal(10.0 - dlg.BreakingIbKa, a.BreakingMarginKa);
    }

    // ================================================================================================================
    // #165: the multiple-fed breaking-current weight is the drop across X''d alone, not X''d plus the unit transformer
    // ================================================================================================================

    [Fact]
    public void B165_breaking_weight_excludes_the_unit_transformer_drop()
    {
        // X''d 0.2 + unit transformer 0.15 on a 100 MVA unit, faulted at its own (HV) bus, 132 kV. Ik'' = 1.1 / 0.35 pu,
        // 3.142857 x rated, so mu at tmin 0.10 s is 0.62 + 0.72 e^(-0.32 x 3.142857). The weight dU''_G / (c Un / sqrt 3)
        // is |I''_kG| X''d / c = (1.1 / 0.35) x 0.2 / 1.1 = 0.2 / 0.35, the drop across X''d alone (IEC 60909-0:2016
        // 9.1.7, dU''_G = j X''_dK I''_kG). Before the fix the weight was |V_G| / c = 1 (the HV bus voltage, which holds
        // the transformer drop too), so Ib came out mu Ik'', too low by (1 - 0.2 / 0.35)(1 - mu) Ik''.
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.2, UnitTransformerXPu: 0.15) },
            Branches = Array.Empty<Branch>(),
        };
        Iec60909.Duty d = Iec60909.ThreePhase(m, 1);
        double ikppPu = 1.1 / 0.35;
        Assert.Equal(ikppPu, d.IkppPu, 12);
        double mu = 0.62 + 0.72 * Math.Exp(-0.32 * ikppPu);
        Assert.Equal(mu, Iec60909.BreakingFactorMu(ikppPu, 0.10), 12);
        double weight = 0.2 / 0.35;
        Assert.Equal(d.IkppKa * (1.0 - weight * (1.0 - mu)), d.BreakingIbKa, 12);
        // The old (transformer-inclusive) answer is mu Ik''; the fix raises Ib above it (the conservative side).
        Assert.True(d.BreakingIbKa > mu * d.IkppKa + 1e-6);
        // PartialThreePhase's fleet breaking current is the same number (one code path).
        Assert.Equal(d.BreakingIbKa, Iec60909.PartialThreePhase(m, 1).TotalBreakingIbKa);
    }

    [Fact]
    public void B165_without_a_unit_transformer_the_weight_is_the_bus_voltage_ratio_unchanged()
    {
        // X''d 0.2 behind a 0.3 line, fault at the far end: the machine's voltage dip ratio is 0.2 / 0.5 = 0.4, exactly
        // as before the fix (no unit transformer: X''d / (X''d + 0) is 1.0 and the weight is |V_G| / c).
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pq, BaseKv: 132.0) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.30) },
        };
        Iec60909.Duty d = Iec60909.ThreePhase(m, 2);
        double mu = Iec60909.BreakingFactorMu(2.2, 0.10);
        Assert.Equal(d.IkppKa * (1.0 - 0.4 * (1.0 - mu)), d.BreakingIbKa, 12);
    }

    // ================================================================================================================
    // #167: FromSequenceImpedances checks f0, tb, Tk and the loop reactance as FromThevenin does
    // ================================================================================================================

    [Theory]
    [InlineData(UnbalancedFaultType.SingleLineToGround)]
    [InlineData(UnbalancedFaultType.LineToLine)]
    [InlineData(UnbalancedFaultType.DoubleLineToGround)]
    public void B167_sequence_duty_refuses_bad_time_frequency_and_reactance(UnbalancedFaultType type)
    {
        var z = new Complex(0.01, 0.2);
        Iec60909.Duty Call(double f0 = 50.0, double tb = 0.1, double tk = 1.0, Complex? z1 = null)
            => Iec60909.FromSequenceImpedances(type, 1.1, 132.0, z1 ?? z, z1 ?? z, z1 ?? z, BaseMva, f0, tb, tk);

        // The good call works and every current is finite.
        Iec60909.Duty ok = Call();
        Assert.True(double.IsFinite(ok.ThermalIthKa) && ok.ThermalIthKa > 0.0);

        // Tk = 0 used to give m = NaN and a NaN Ith returned as a valid duty.
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(tk: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(tk: double.NaN));
        // A negative breaker time used to put idc above sqrt(2) Ik''.
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(tb: -0.05));
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(f0: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(f0: double.PositiveInfinity));
        // A loop with no (or negative) reactance is not a fault loop.
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(z1: new Complex(0.1, 0.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(z1: new Complex(0.1, -0.2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(z1: new Complex(double.NaN, 0.2)));
    }

    // ================================================================================================================
    // #169: the thermal DC factor m keeps machine precision near kappa = 2
    // ================================================================================================================

    // m = (e^x - 1) / D with D = 2 f Tk ln(kappa - 1) and x = 2D, evaluated for |x| tiny by its Taylor series (exact to
    // far below one ulp at |x| <= 1e-3), as an independent reference.
    private static double ReferenceM(double kappa, double f0, double tk)
    {
        double l = Math.Log(kappa - 1.0);
        double d = 2.0 * f0 * tk * l;
        double x = 2.0 * d;
        double num = x * (1.0 + x / 2.0 * (1.0 + x / 3.0 * (1.0 + x / 4.0 * (1.0 + x / 5.0 * (1.0 + x / 6.0)))));
        return num / d;
    }

    [Theory]
    [InlineData(1e-12)]
    [InlineData(5e-10)]
    [InlineData(1e-9)]
    [InlineData(1e-8)]
    [InlineData(3e-7)]
    [InlineData(1e-6)]
    public void B169_thermal_dc_factor_is_accurate_near_kappa_2(double gap)
    {
        // kappa = 2 - gap, so ln(kappa - 1) is about -gap: R/X of about gap / 2.94. The old code returned exactly 2 for
        // |ln(kappa - 1)| < 1e-9 (true m about 2 + 2D, e.g. relative error 1e-7 at the finding's l = 1e-9, f 50 Hz,
        // Tk 1 s) and above that lost about eps / |x| to cancellation in Exp(x) - 1.
        double kappa = 2.0 - gap;
        double m = Iec60909.ThermalDcFactor(kappa, 50.0, 1.0);
        double reference = ReferenceM(kappa, 50.0, 1.0);
        double ulp = Math.BitIncrement(reference) - reference;
        Assert.True(Math.Abs(m - reference) <= 4.0 * ulp,
            $"m = {m.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, reference "
            + $"{reference.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, gap {gap.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.True(m < 2.0);   // R > 0: the DC decays, so m is strictly below its non-decaying limit 2
    }

    [Fact]
    public void B169_thermal_dc_factor_is_unchanged_bit_for_bit_away_from_kappa_2()
    {
        // |x| = |4 f Tk ln(kappa - 1)| >= 1 keeps the Exp(x) - 1 form exactly: a typical transmission R/X of 0.07 and the
        // breaker-check durations.
        foreach (double rOverX in new[] { 0.02, 0.07, 0.15, 0.5 })
            foreach (double tk in new[] { 0.5, 1.0, 3.0 })
            {
                double kappa = Iec60909.PeakFactor(rOverX);
                double l = DetMath.Log(kappa - 1.0);
                double denom = 2.0 * 50.0 * tk * l;
                Assert.True(Math.Abs(2.0 * denom) >= 1.0);
                double old = (DetMath.Exp(2.0 * denom) - 1.0) / denom;
                Assert.Equal(BitConverter.DoubleToInt64Bits(old), BitConverter.DoubleToInt64Bits(Iec60909.ThermalDcFactor(kappa, 50.0, tk)));
            }
        Assert.Equal(2.0, Iec60909.ThermalDcFactor(2.0, 50.0, 1.0));   // exactly kappa = 2: the limit
    }

    // ================================================================================================================
    // #164: a converter's prefault current comes from the solved output where the power flow sets it
    // ================================================================================================================

    // Slack synchronous machine at bus 1, converter (100 MVA, k 1.2) at bus 2, line X 0.2. Scheduled Qg = 0.
    private static GridModel ConverterAt(BusType bus2Type) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, bus2Type, BaseKv: 132.0) },
        Generators = new[]
        {
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Name: "grid", Type: "ccgt"),
            new Generator(Bus: 2, Pg: 0.0, Qg: 0.0, MBase: 100.0, Name: "unit", Type: "battery"),
        },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.2) },
    };

    // A converged prefault state at 1 pu everywhere in which the converter's bus exports qInjMvar (its solved Q).
    private static PowerFlowResult PrefaultWithQ(GridModel m, double qInjMvar) => new()
    {
        Converged = true, Iterations = 1, MaxMismatch = 0,
        BusIds = m.Buses.Select(b => b.Id).ToArray(),
        Vm = m.Buses.Select(_ => 1.0).ToArray(),
        VaDeg = m.Buses.Select(_ => 0.0).ToArray(),
        PInjMw = new double[m.BusCount], QInjMvar = new[] { -qInjMvar, qInjMvar },
        TotalLossMw = 0, TotalLossMvar = 0, LineFlows = Array.Empty<LineFlow>(),
    };

    [Fact]
    public void B164_pv_converter_prefault_reactive_current_is_the_solved_q()
    {
        // The PV converter scheduled Qg = 0 solved 50 MVAr: iq0 must be 0.5 pu (100 MVA base, 1 pu volts), so its
        // fault reactive current is 0.5 + K x dip (K = 2, rated 1 pu) while below the 1.2 pu limit. Before the fix
        // iq0 was 0 and the reported current was K x dip alone. A remote, high-impedance fault keeps the dip small.
        GridModel m = ConverterAt(BusType.Pv);
        FaultResult r = FaultAnalysis.ThreePhase(m, PrefaultWithQ(m, 50.0), faultBusId: 1, faultReactancePu: 0.5);
        Assert.True(r.ConvertersConverged);
        ConverterFaultInfeed c = Assert.Single(r.Converters!);
        Assert.False(c.AtCurrentLimit);
        double dip = 1.0 - c.TerminalVoltagePu;
        Assert.True(dip > 0.01);
        Assert.Equal(0.5 + FaultAnalysis.DefaultConverterReactiveGain * dip, c.ReactiveCurrentPu, 8);
    }

    [Fact]
    public void B164_pq_converter_keeps_its_schedule()
    {
        // At a PQ bus the power flow honours the schedule, so the schedule (Qg = 0) is the prefault output; the same fake
        // 50 MVAr in the result is not read.
        GridModel m = ConverterAt(BusType.Pq);
        FaultResult r = FaultAnalysis.ThreePhase(m, PrefaultWithQ(m, 50.0), faultBusId: 1, faultReactancePu: 0.5);
        ConverterFaultInfeed c = Assert.Single(r.Converters!);
        double dip = 1.0 - c.TerminalVoltagePu;
        Assert.Equal(FaultAnalysis.DefaultConverterReactiveGain * dip, c.ReactiveCurrentPu, 8);
    }

    // ================================================================================================================
    // #166: a fault source with MBase <= 0 is refused by the operating-point fault study and the harmonic Y-bus
    // ================================================================================================================

    private static GridModel WithUnit(Generator unit) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pv, BaseKv: 132.0) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Name: "grid"), unit },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.2) },
    };

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void B166_unrated_fault_source_is_refused_naming_the_unit(double mBase)
    {
        // A synchronous unit with no rating: it used to be rated at the system base (a 1000 MVA unit with X''d 0.2 fed
        // 5 pu instead of 50 pu at its terminals).
        GridModel sync = WithUnit(new Generator(Bus: 2, Pg: 0.0, MBase: mBase, Name: "Unrated", SubtransientXdPu: 0.2));
        var e1 = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(sync, PrefaultWithQ(sync, 0.0), 1));
        Assert.Contains("Unrated", e1.Message, StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.Unbalanced(sync, PrefaultWithQ(sync, 0.0), 1, FaultType.LineToLine));
        Assert.Contains("Unrated", e2.Message, StringComparison.Ordinal);
        var e3 = Assert.Throws<InvalidOperationException>(() => FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(sync.Generators[1], BaseMva, 0.2));
        Assert.Contains("Unrated", e3.Message, StringComparison.Ordinal);

        // A converter with no rating: its k x Ir limit has no Ir.
        GridModel conv = WithUnit(new Generator(Bus: 2, Pg: 0.0, MBase: mBase, Name: "UnratedBess", Type: "battery"));
        var e4 = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(conv, PrefaultWithQ(conv, 0.0), 1));
        Assert.Contains("UnratedBess", e4.Message, StringComparison.Ordinal);
    }

    // ================================================================================================================
    // #168: a repeated harmonic order is refused by HarmonicSource
    // ================================================================================================================

    [Fact]
    public void B168_harmonic_source_refuses_a_repeated_order()
    {
        var dup = new[] { new HarmonicComponent(5, 20.0), new HarmonicComponent(5, 10.0) };
        var ex = Assert.Throws<ArgumentException>(() => new HarmonicSource(7, "test", 0.1, dup));
        Assert.Contains("order 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 7", ex.Message, StringComparison.Ordinal);

        var good = new[] { new HarmonicComponent(5, 20.0), new HarmonicComponent(7, 10.0) };
        var src = new HarmonicSource(7, "test", 0.1, good);
        Assert.Same(good, src.Spectrum);   // a distinct-order spectrum is kept as it is
        Assert.Equal(DetMath.FromPolar(0.2 * 0.1, 0.0), src.InjectionAt(5));
        // A with-expression is checked too.
        Assert.Throws<ArgumentException>(() => src with { Spectrum = dup });
    }

    // ================================================================================================================
    // #184: unit commitment refuses contingencies when line limits are off (they would be secured against nothing)
    // ================================================================================================================

    [Fact]
    public void B184_unit_commitment_refuses_contingencies_without_line_limits()
    {
        GridModel m = new()
        {
            Name = "uc-two-bus",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Pd: 150) },
            Generators = new[]
            {
                new Generator(1, 0, Pmin: 0, Pmax: 500, Name: "cheap"),
                new Generator(2, 0, Pmin: 0, Pmax: 500, Name: "dear"),
            },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1, RateAMva: 100, Name: "L1"), new Branch(1, 2, 0.0, 0.1, RateAMva: 100, Name: "L2") },
        };
        var units = new[] { new GridSim.Core.Analysis.UcUnitData { MarginalCost = 10 }, new GridSim.Core.Analysis.UcUnitData { MarginalCost = 30 } };
        // Before the fix: Optimal, no N-1 rows, no note.
        var ex = Assert.Throws<ArgumentException>(() => GridSim.Core.Analysis.UnitCommitment.Solve(m,
            new GridSim.Core.Analysis.UcOptions { Periods = 1, Units = units, EnforceLineLimits = false, Contingencies = new[] { 0 } }));
        Assert.Contains("line limits are off", ex.Message, StringComparison.Ordinal);
        // With line limits on the same contingency is secured; with limits off and no contingencies it still solves.
        Assert.True(GridSim.Core.Analysis.UnitCommitment.Solve(m,
            new GridSim.Core.Analysis.UcOptions { Periods = 1, Units = units, Contingencies = new[] { 0 } }).Optimal);
        Assert.True(GridSim.Core.Analysis.UnitCommitment.Solve(m,
            new GridSim.Core.Analysis.UcOptions { Periods = 1, Units = units, EnforceLineLimits = false }).Optimal);
    }

    // ================================================================================================================
    // #186: SCOPF bindings skip the (outage, branch) pairs with LODF 0
    // ================================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B186_scopf_lists_a_radial_base_binding_once(bool constraintGeneration)
    {
        // Triangle 1-2-3 (generous ratings) with a radial line 3-4 rated exactly its 50 MW load. No triangle outage moves
        // the radial flow (LODF 0), and the radial outage islands bus 4, so the only binding is L34's pre-outage one.
        // Before the fix it was listed four times: once pre-outage and once per triangle outage.
        GridModel m = new()
        {
            Name = "scopf-radial",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 150), new Bus(4, BusType.Pq, Pd: 50) },
            Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 500, Name: "A"), new Generator(2, 0, Pmin: 0, Pmax: 500, Name: "B") },
            Branches = new[]
            {
                new Branch(1, 2, 0, 0.1, RateAMva: 1000, Name: "L12"),
                new Branch(1, 3, 0, 0.1, RateAMva: 1000, Name: "L13"),
                new Branch(2, 3, 0, 0.1, RateAMva: 1000, Name: "L23"),
                new Branch(3, 4, 0, 0.1, RateAMva: 50, Name: "L34"),
            },
        };
        GridSim.Core.Analysis.ScopfResult r = GridSim.Core.Analysis.DcScopf.Solve(m,
            new GridSim.Core.Analysis.ScopfOptions { CostPerGen = new[] { 10.0, 30.0 }, ConstraintGeneration = constraintGeneration });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 0, 1, 2 }, r.Contingencies);
        Assert.Equal(new[] { 3 }, r.IslandingOutages);
        GridSim.Core.Analysis.ScopfBinding b = Assert.Single(r.Bindings);
        Assert.Equal(3, b.Branch);
        Assert.Equal(-1, b.Contingency);
    }

    // ================================================================================================================
    // #185: the Kalman family holds each slack at its OWN angle, as WLS does
    // ================================================================================================================

    [Fact]
    public void B185_kalman_filters_hold_each_slack_at_its_own_angle()
    {
        // case9 solved as published (bus 1 the slack at 0 deg). Bus 2 is then made a second slack held at its solved
        // angle, so the exact measurements stay consistent. Before the fix the filters pinned bus 2 at the FIRST slack's
        // 0 deg each window.
        GridModel published = GridSim.Core.IO.IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(published);
        int i2 = published.Buses.ToList().FindIndex(b => b.Id == 2);
        double theta2 = truth.VaDeg[i2];
        Assert.True(Math.Abs(theta2) > 1.0);   // the premise: a second slack angle well away from 0
        GridModel twoSlack = published.CopyWith(buses: published.Buses
            .Select(b => b.Id == 2 ? b with { Type = BusType.Slack, VaDeg = theta2, Vm = truth.Vm[i2] } : b).ToArray());
        GridSim.Core.Estimation.MeasurementSet exact = WlsEstimatorTests.ExactFullSet(twoSlack, truth);
        double expectedDeg = theta2 * Math.PI / 180.0 * 180.0 / Math.PI;   // the filters' own rad -> deg round trip

        var ekf = new GridSim.Core.Estimation.Dse.KalmanStateEstimator(twoSlack);
        GridSim.Core.Estimation.Dse.DseEstimate e = null!;
        for (int step = 0; step < 25; step++) e = ekf.Step(exact);
        Assert.Equal(expectedDeg, e.VaDeg[i2], 12);
        // With both references right, the EKF tracks the truth everywhere (it could not with bus 2 held at 0 deg).
        for (int i = 0; i < twoSlack.BusCount; i++)
            Assert.True(Math.Abs(e.VaDeg[i] - truth.VaDeg[i]) < 1e-2, $"bus {truth.BusIds[i]}: EKF Va {e.VaDeg[i]} vs truth {truth.VaDeg[i]}");

        var ukf = new GridSim.Core.Estimation.Dse.UnscentedKalmanStateEstimator(twoSlack);
        Assert.Equal(expectedDeg, ukf.Step(exact).VaDeg[i2], 12);
        var enkf = new GridSim.Core.Estimation.Dse.EnsembleKalmanStateEstimator(twoSlack);
        Assert.Equal(expectedDeg, enkf.Step(exact).VaDeg[i2], 12);
    }

    // ================================================================================================================
    // #183: redundant injection meters at one bus are combined by their weighted mean in the prior, not summed
    // ================================================================================================================

    [Fact]
    public void B183_measured_injection_prior_combines_redundant_meters()
    {
        GridModel model = GridSim.Core.IO.IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        GridSim.Core.Estimation.MeasurementSet set = GridSim.Core.Estimation.SyntheticMeasurements.Sample(model, truth,
            new GridSim.Core.Estimation.SyntheticMeasurementOptions { NoiseScale = 0 });
        var rows = set.Measurements.ToList();
        int k = rows.FindIndex(r => r.Kind == GridSim.Core.Estimation.MeasurementKind.PInjMw && r.Bus == 2);
        Assert.True(k >= 0);
        GridSim.Core.Estimation.Measurement p2 = rows[k];

        // Two redundant meters of bus 2's injection, 300 MW at sigma 1 and 200 MW at sigma 2: the weighted mean is
        // (300 x 1 + 200 x 0.25) / 1.25 = 280 MW exactly. Before the fix the prior summed them to 500 MW.
        var dup = new List<GridSim.Core.Estimation.Measurement>(rows);
        dup[k] = p2 with { Value = 300.0, Sigma = 1.0 };
        dup.Insert(k + 1, p2 with { Value = 200.0, Sigma = 2.0 });
        var single = new List<GridSim.Core.Estimation.Measurement>(rows);
        single[k] = p2 with { Value = 280.0, Sigma = 1.0 };

        var solver = new NewtonRaphsonPowerFlow();
        PowerFlowResult fromDup = GridSim.Core.Estimation.MeasuredInjectionPrior.Solve(model, set with { Measurements = dup.ToArray() }, solver);
        PowerFlowResult fromSingle = GridSim.Core.Estimation.MeasuredInjectionPrior.Solve(model, set with { Measurements = single.ToArray() }, solver);
        Assert.True(fromSingle.Converged);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(fromSingle.Vm[i]), BitConverter.DoubleToInt64Bits(fromDup.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(fromSingle.VaDeg[i]), BitConverter.DoubleToInt64Bits(fromDup.VaDeg[i]));
        }

        // Two identical redundant meters give exactly the one meter's operating point (sigma 0.5: weight 4, a power of
        // two, so the weighted mean of the two equal values is the value exactly).
        var twin = new List<GridSim.Core.Estimation.Measurement>(rows);
        twin[k] = p2 with { Sigma = 0.5 };
        twin.Insert(k + 1, p2 with { Sigma = 0.5 });
        PowerFlowResult fromTwin = GridSim.Core.Estimation.MeasuredInjectionPrior.Solve(model, set with { Measurements = twin.ToArray() }, solver);
        PowerFlowResult fromOne = GridSim.Core.Estimation.MeasuredInjectionPrior.Solve(model, set, solver);
        for (int i = 0; i < model.BusCount; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(fromOne.VaDeg[i]), BitConverter.DoubleToInt64Bits(fromTwin.VaDeg[i]));
    }

    // ================================================================================================================
    // #192: fleet rollups name protection controllers as protection, and say when a class has no state of charge
    // ================================================================================================================

    [Fact]
    public void B192_fleet_rollups_label_protection_and_mark_soc_not_applicable()
    {
        Assert.Equal("grid-forming", GridSim.Core.Assets.AssetDynamicsFleet.ModeLabel(GridSim.Core.Assets.AssetKind.GridFormingInverter));
        Assert.Equal("grid-following", GridSim.Core.Assets.AssetDynamicsFleet.ModeLabel(GridSim.Core.Assets.AssetKind.GridFollowingInverter));
        Assert.Equal("protection", GridSim.Core.Assets.AssetDynamicsFleet.ModeLabel(GridSim.Core.Assets.AssetKind.Protection));
        Assert.Equal("synchronous", GridSim.Core.Assets.AssetDynamicsFleet.ModeLabel(GridSim.Core.Assets.AssetKind.SynchronousMachine));

        // gb-synthetic binds LFDD protection classes: before the fix they read "grid-following" with 0 % SoC.
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "gb-synthetic");
        GridModel m = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(dir);
        var fleet = GridSim.Core.Assets.AssetDynamicsFleet.Build(m, 50.0);
        var rollups = fleet.Rollups();
        var lfdd = rollups.Where(r => r.ClassName.StartsWith("prot-lfdd", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lfdd);
        foreach (var r in lfdd)
        {
            Assert.Equal("protection", r.Mode);
            Assert.False(r.SocApplicable);
            Assert.Equal(0.0, r.SocPercent);   // the JSON-safe placeholder, flagged as not applicable
        }
    }

    // ================================================================================================================
    // #194: a bus absent from the live solve reads "not known" (NaN) to the protections, not a healthy 1.0 pu
    // ================================================================================================================

    [Fact]
    public void B194_live_voltage_of_an_absent_bus_is_not_known()
    {
        // A result that dropped bus 3 (as IslandPartition drops a dead island's buses) and whose bus 2 row has no
        // voltage. Before the fix both read 1.0 pu, so an under-voltage or LVRT stage there never operated on a
        // voltage that nobody had computed.
        var r = new PowerFlowResult
        {
            Converged = true, Iterations = 1, MaxMismatch = 0,
            BusIds = new[] { 1, 2 }, Vm = new[] { 0.987 }, VaDeg = new[] { 0.0 },
            PInjMw = new double[2], QInjMvar = new double[2],
            TotalLossMw = 0, TotalLossMvar = 0, LineFlows = Array.Empty<LineFlow>(),
        };
        Func<int, double> v = GridSim.Core.Simulation.LiveSim.BuildVoltageOf(r);
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.987), BitConverter.DoubleToInt64Bits(v(1)));   // solved: as before
        Assert.True(double.IsNaN(v(2)));
        Assert.True(double.IsNaN(v(3)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(GridSim.Core.Assets.AssetStepContext.NoNetworkVoltagePu), BitConverter.DoubleToInt64Bits(v(3)));
    }

    // ================================================================================================================
    // #100: a bridge state block with no converged flag is vouched for only when it carries a full, finite state
    // ================================================================================================================

    private static GridSim.Core.IO.BridgeProtocol.StateMessage BridgeState(bool? converged, params (int Id, double Vm)[] buses) => new()
    {
        Type = "state",
        T = "2024-01-01T00:00:00Z",
        Result = new GridSim.Core.IO.BridgeProtocol.StateResultBlock
        {
            Converged = converged,
            Buses = buses.Select(b => new GridSim.Core.IO.BridgeProtocol.ResBusDto { Id = b.Id, Vm = b.Vm }).ToList(),
            LineFlows = new(),
        },
    };

    [Fact]
    public void B100_bridge_state_without_converged_flag_is_checked()
    {
        GridModel topo = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pq, BaseKv: 132.0) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.1) },
        };
        // A full, finite state with no flag: the server's vouch, as before.
        Assert.True(BridgeState(null, (1, 1.0), (2, 0.98)).ToPowerFlowResult(topo).Converged);
        // No buses at all (the bridge's "no solve" frame): not a solved state. Before the fix: converged.
        Assert.False(BridgeState(null).ToPowerFlowResult(topo).Converged);
        // A NaN or non-positive voltage magnitude: not a solved state.
        Assert.False(BridgeState(null, (1, 1.0), (2, double.NaN)).ToPowerFlowResult(topo).Converged);
        Assert.False(BridgeState(null, (1, 1.0), (2, 0.0)).ToPowerFlowResult(topo).Converged);
        // An explicit flag is honoured either way.
        Assert.False(BridgeState(false, (1, 1.0), (2, 0.98)).ToPowerFlowResult(topo).Converged);
        Assert.True(BridgeState(true, (1, 1.0), (2, 0.98)).ToPowerFlowResult(topo).Converged);
    }

    // ================================================================================================================
    // #159: a pinned PV bus that breaks PV/PQ complementarity is put back to PV even with the opt-in revert off
    // ================================================================================================================

    [Fact]
    public void B159_broken_complementarity_is_reverted_with_the_opt_in_off()
    {
        // Four PV buses, all pinned (PQ). 0: at Qmin with V below its set point (broken: absorbing more than needed).
        // 1: at Qmax with V above its set point (broken). 2: at Qmin with V above its set point (binding: consistent).
        // 3: at Qmin and broken, but already reverted once this solve (the once-per-solve guard holds it).
        var type0 = new[] { BusType.Pv, BusType.Pv, BusType.Pv, BusType.Pv };
        var eff = new[] { BusType.Pq, BusType.Pq, BusType.Pq, BusType.Pq };
        var vm = new[] { 0.95, 1.05, 1.03, 0.95 };
        var vg = new[] { 1.02, 1.02, 1.02, 1.02 };
        var pinned = new[] { NewtonShell.QPin.Min, NewtonShell.QPin.Max, NewtonShell.QPin.Min, NewtonShell.QPin.Min };
        var rc = new[] { 0, 0, 0, 1 };
        Assert.True(NewtonShell.RevertBrokenPins(4, type0, eff, vm, vg, pinned, rc));
        Assert.Equal(new[] { BusType.Pv, BusType.Pv, BusType.Pq, BusType.Pq }, eff);
        Assert.Equal(new[] { 1.02, 1.02, 1.03, 0.95 }, vm);   // set point re-imposed as the start point
        Assert.Equal(new[] { 1, 1, 0, 1 }, rc);
        // A second call finds nothing (the loop terminates).
        Assert.False(NewtonShell.RevertBrokenPins(4, type0, eff, vm, vg, pinned, rc));
    }

    // ================================================================================================================
    // #190 / #191: passive plant thermal lag stepped exactly; the transformer is driven by loading squared
    // ================================================================================================================

    private static GridSim.Core.Assets.AssetClass ThermalClass(GridSim.Core.Assets.AssetKind kind, double tauMin, double derateFull) =>
        new("thermal", kind, kind == GridSim.Core.Assets.AssetKind.Line ? "line" : "transformer",
            new System.Collections.Generic.Dictionary<string, double>
            {
                [GridSim.Core.Assets.AssetParams.ThermalTauMin] = tauMin,
                [GridSim.Core.Assets.AssetParams.DerateAtFullThermal] = derateFull,
            },
            new System.Collections.Generic.Dictionary<string, GridSim.Core.Assets.AssetClassProvenance>
            {
                [GridSim.Core.Assets.AssetClass.DefaultKey] = new(GridSim.Core.Diagnostics.ProvenanceTag.CuratedPlaceholder, "test class"),
            });

    private static GridSim.Core.Assets.AssetStepContext Tick(double dt, double loading) =>
        new(50, 0, 0, DtSeconds: dt, ClockSeconds: 0, LoadingFraction: loading);

    [Fact]
    public void B190_line_thermal_lag_holds_at_a_1200_s_tick()
    {
        // 15 min line, derate 0.1 at full thermal, overloaded at 1 pu for one 1200 s tick from cold. Exact first-order
        // response: 1 - e^(-1200 / 900) = 0.7364, derate 0.07364, so a 1000 MVA line keeps 926.4 MVA. Before the fix
        // tau was raised to the tick and Euler jumped to the drive: state 1.000, 900 MVA.
        var line = new GridSim.Core.Assets.LineThermalDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Line, 15.0, 0.1), "L1");
        GridSim.Core.Assets.PlantStateUpdate u = line.Advance(Tick(1200.0, 1.0));
        double expected = 1.0 - Math.Exp(-1200.0 / 900.0);
        Assert.Equal(expected, u.ThermalStatePu, 14);
        Assert.Equal(926.4, 1000.0 * (1.0 - u.RatingDerateFraction), 1);
        // Cooling is a lag too: one more 1200 s tick at no load keeps e^(-1200 / 900) of the state.
        Assert.Equal(expected * Math.Exp(-1200.0 / 900.0), line.Advance(Tick(1200.0, 0.0)).ThermalStatePu, 14);
        // One 1200 s tick equals two 600 s ticks (the exact step composes; Euler did not).
        var a = new GridSim.Core.Assets.LineThermalDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Line, 15.0, 0.1), "L2");
        a.Advance(Tick(600.0, 1.0));
        Assert.Equal(expected, a.Advance(Tick(600.0, 1.0)).ThermalStatePu, 14);
        // A negative or non-finite time constant is refused at build; 0 is no lag.
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridSim.Core.Assets.LineThermalDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Line, -1.0, 0.1), "L3"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridSim.Core.Assets.LineThermalDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Line, double.NaN, 0.1), "L4"));
        var instant = new GridSim.Core.Assets.LineThermalDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Line, 0.0, 0.1), "L5");
        Assert.Equal(0.81, instant.Advance(Tick(60.0, 0.9)).ThermalStatePu, 15);
    }

    [Fact]
    public void B190_B191_transformer_thermal_lag_and_loading_squared_drive()
    {
        // 20 min transformer from cold at K = 1 for one 1200 s tick: 1 - e^-1 = 0.632 (was 1.000).
        var tx = new GridSim.Core.Assets.TransformerDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Transformer, 20.0, 0.5), "T1");
        Assert.Equal(1.0 - Math.Exp(-1.0), tx.Advance(Tick(1200.0, 1.0)).ThermalStatePu, 14);

        // #191: at K = 0.5 the state settles at K^2 = 0.25 (was 0.50, twice the heating), so the derate is 0.5 x 0.25.
        var tx2 = new GridSim.Core.Assets.TransformerDynamics(ThermalClass(GridSim.Core.Assets.AssetKind.Transformer, 20.0, 0.5), "T2");
        GridSim.Core.Assets.PlantStateUpdate u = default;
        for (int i = 0; i < 200; i++) u = tx2.Advance(Tick(1200.0, 0.5));
        Assert.Equal(0.25, u.ThermalStatePu, 12);
        Assert.Equal(0.125, u.RatingDerateFraction, 12);
    }
}
