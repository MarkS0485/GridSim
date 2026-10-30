using System.Numerics;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R001 (critical, October 2026 review): the harmonic Y(h) had no generator source impedance, and every
/// harmonic solver turned a singular Y(h) into a ZERO voltage vector - reported as 0% THD and IEEE-519
/// "Compliant". These tests pin the fix:
///   1. a singular Y(h) is an error naming the order, in every harmonic study, never a result;
///   2. machine source impedance is stamped exactly (1/(j h X), X = X2 + unit transformer on the system base,
///      the fault studies' reactance), only for physical synchronous machines;
///   3. the production study model (<see cref="HarmonicModelOptions.Study"/>) grounds a radial feeder that
///      the bare network model cannot solve;
///   4. the default builder is untouched (Y(1) == YBus stays pinned elsewhere; here: no machine terms by default);
///   5. the export records every input the independent checker needs to rebuild the same Y(h).
/// </summary>
public class HarmonicSourceImpedanceTests
{
    /// <summary>A three-bus radial feeder with NO line charging and NO shunts: its network-only Y(h) is a pure
    /// Laplacian (rows sum to zero), so it is singular at every order. A synchronous machine at bus 1, a
    /// 6-pulse load at bus 3.</summary>
    private static GridModel RadialFeeder(IReadOnlyList<Generator>? generators = null) => new()
    {
        Name = "radial-3",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 33),
            new Bus(2, BusType.Pq, Pd: 5, Qd: 1, BaseKv: 33),
            new Bus(3, BusType.Pq, Pd: 10, Qd: 3, BaseKv: 33),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.05),
            new Branch(2, 3, 0.02, 0.08),
        },
        Generators = generators ?? new[]
        {
            new Generator(1, Pg: 15, MBase: 200, SubtransientXdPu: 0.25, UnitTransformerXPu: 0.1, Name: "G1"),
        },
    };

    private static IReadOnlyList<HarmonicSource> SixPulseAtBus3() =>
        new[] { HarmonicSourceLibrary.Make("6-pulse", 3, fundamentalCurrentPu: 0.5) };

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static (double G, double B) Diag(ComplexCsr y, int i)
    {
        int p = y.DiagPtr[i];
        return (y.G[p], y.B[p]);
    }

    // ---- 1. Singular Y(h) fails loud ------------------------------------------------------------

    [Fact]
    public void Penetration_on_an_ungrounded_radial_feeder_throws_naming_the_lowest_order()
    {
        // Before the fix this returned all-zero voltages: 0% THD, every bus "compliant".
        var opt = new HarmonicModelOptions { Orders = new[] { 7, 5, 11 } };
        var ex = Assert.Throws<SingularHarmonicNetworkException>(
            () => HarmonicPenetration.Solve(RadialFeeder(), SixPulseAtBus3(), opt));
        Assert.Equal(5.0, ex.Order);                    // the LOWEST singular order, whatever ran first
        Assert.Equal("Harmonic penetration", ex.Study);
        Assert.Contains("singular at order 5", ex.Message);
    }

    [Fact]
    public void Penetration_singular_order_is_deterministic_on_the_sequential_path_too()
    {
        // A caller-supplied solver runs the orders sequentially; it must report the same order as the
        // parallel default path.
        var opt = new HarmonicModelOptions { Orders = new[] { 7, 5, 11 } };
        var ex = Assert.Throws<SingularHarmonicNetworkException>(
            () => HarmonicPenetration.Solve(RadialFeeder(), SixPulseAtBus3(), opt, new RealExpandedComplexSolver()));
        Assert.Equal(5.0, ex.Order);
    }

    [Fact]
    public void Impedance_scan_on_an_ungrounded_radial_feeder_throws()
    {
        // Before the fix a singular order read as Z = 0 ("no resonance here").
        var opt = new HarmonicModelOptions { Orders = new[] { 3, 5, 7 } };
        var ex = Assert.Throws<SingularHarmonicNetworkException>(
            () => HarmonicImpedanceScan.DrivingPoint(RadialFeeder(), 3, opt));
        Assert.Equal(3.0, ex.Order);
        Assert.Equal("Harmonic impedance scan", ex.Study);
    }

    [Fact]
    public void Coupled_harmonic_flow_on_an_ungrounded_radial_feeder_throws()
    {
        // Before the fix: a zero response, every update zero, reported Converged = true.
        var opt = new HarmonicModelOptions { Orders = new[] { 5 } };
        var ex = Assert.Throws<SingularHarmonicNetworkException>(
            () => CoupledHarmonicPowerFlow.Solve(RadialFeeder(), SixPulseAtBus3(), new NortonCouplingModel(0.0, 0.0), opt));
        Assert.Equal(5.0, ex.Order);
    }

    // ---- 2. Exact machine stamp -----------------------------------------------------------------

    [Fact]
    public void Machine_source_reactance_is_x2_plus_unit_transformer_on_the_system_base()
    {
        // X2 defaults to X''d (round rotor): (0.25 + 0.1) pu on 200 MVA -> 0.35 * 100/200 on the 100 MVA base.
        var g = new Generator(1, Pg: 15, MBase: 200, SubtransientXdPu: 0.25, UnitTransformerXPu: 0.1);
        Assert.Equal(Bits(0.35 * 100.0 / 200.0), Bits(FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(g, 100, 0.2)));

        // An explicit X2 wins over X''d.
        var g2 = g with { NegativeSeqXPu = 0.3 };
        Assert.Equal(Bits(0.4 * 100.0 / 200.0), Bits(FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(g2, 100, 0.2)));

        // No X''d carried: the fault studies' fallback (0.2 pu).
        var g3 = new Generator(1, Pg: 15, MBase: 100);
        Assert.Equal(Bits(0.2), Bits(FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(g3, 100, 0.2)));

        // No MBase: rated at the system base.
        var g4 = new Generator(1, Pg: 15, MBase: 0, SubtransientXdPu: 0.3);
        Assert.Equal(Bits(0.3), Bits(FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(g4, 100, 0.2)));
    }

    [Fact]
    public void A_zero_source_reactance_fails_loudly_naming_the_unit()
    {
        var g = new Generator(1, Pg: 15, MBase: 100, Name: "ZeroX");
        var ex = Assert.Throws<InvalidOperationException>(
            () => FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(g, 100, fallbackXdPu: 0.0));
        Assert.Contains("ZeroX", ex.Message);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(13.0)]
    public void Machine_stamp_adds_exactly_minus_one_over_h_x_to_the_bus_susceptance(double h)
    {
        GridModel m = RadialFeeder();
        ComplexCsr bare = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions());
        ComplexCsr with = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions { IncludeGeneratorSources = true });

        double x = 0.35 * 100.0 / 200.0;
        (double g0, double b0) = Diag(bare, 0);
        (double g1, double b1) = Diag(with, 0);
        Assert.Equal(Bits(g0), Bits(g1));                         // a pure reactance: no conductance
        Assert.Equal(Bits(b0 + (-1.0 / (h * x))), Bits(b1));      // one addition, last in the row

        // Every other cell is untouched, to the bit.
        Assert.Equal(bare.ColIdx, with.ColIdx);
        for (int p = 0; p < bare.G.Length; p++)
        {
            if (p == bare.DiagPtr[0]) continue;
            Assert.Equal(Bits(bare.G[p]), Bits(with.G[p]));
            Assert.Equal(Bits(bare.B[p]), Bits(with.B[p]));
        }
    }

    [Fact]
    public void Converter_and_power_flow_only_units_are_not_stamped()
    {
        // Same rule as the fault studies: a converter is a controlled current source (no EMF behind a
        // reactance); a power-flow-only device is not physical plant.
        GridModel m = RadialFeeder(new[]
        {
            new Generator(1, Pg: 5, MBase: 100, Type: "solar", Name: "PV"),
            new Generator(1, Pg: 5, MBase: 100, ConverterInterfaced: true, Name: "BESS"),
            new Generator(1, Pg: 5, MBase: 100, SubtransientXdPu: 0.2, PowerFlowOnly: true, Name: "Balancer"),
        });
        ComplexCsr bare = FrequencyDependentYBus.Build(m, 5.0, new HarmonicModelOptions());
        ComplexCsr with = FrequencyDependentYBus.Build(m, 5.0, new HarmonicModelOptions { IncludeGeneratorSources = true });
        for (int p = 0; p < bare.G.Length; p++)
        {
            Assert.Equal(Bits(bare.G[p]), Bits(with.G[p]));
            Assert.Equal(Bits(bare.B[p]), Bits(with.B[p]));
        }
    }

    // ---- 3. The study model grounds the feeder ----------------------------------------------------

    [Fact]
    public void Study_model_solves_the_radial_feeder_with_real_distortion()
    {
        GridModel m = RadialFeeder();
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, SixPulseAtBus3(), HarmonicModelOptions.Study);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);

        // The source bus sees real distortion (it was 0% before the fix), and every value is finite.
        Assert.True(idx.WorstThdPct > 0.0, $"worst THD {idx.WorstThdPct}");
        Assert.Equal(3, idx.WorstBusId);
        foreach (Complex[] order in pen.Voltage)
            foreach (Complex v in order)
                Assert.True(double.IsFinite(v.Real) && double.IsFinite(v.Imaginary));

        // The scan now finds a finite driving-point impedance at every order.
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(m, 3, HarmonicModelOptions.Study);
        Assert.All(scan.Z, z => Assert.True(z.Magnitude > 0.0 && double.IsFinite(z.Magnitude)));
    }

    [Fact]
    public void Study_preset_turns_on_machines_and_loads_and_nothing_else()
    {
        HarmonicModelOptions s = HarmonicModelOptions.Study;
        Assert.True(s.IncludeGeneratorSources);
        Assert.True(s.IncludeLoadShunts);
        Assert.Equal(0.0, s.SkinCoefficient);
        Assert.Equal(0.0, s.CableSkinCoefficient);
        Assert.False(s.LongLineCorrection);
        Assert.Equal(HarmonicModelOptions.DefaultOrders, s.Orders);
        Assert.Equal(GridSim.Core.Protection.FaultAnalysis.DefaultSubtransientXdPu, s.GeneratorFallbackXdPu);
    }

    // ---- 4. Defaults untouched ----------------------------------------------------------------------

    [Fact]
    public void Default_options_add_no_machine_terms()
    {
        var d = new HarmonicModelOptions();
        Assert.False(d.IncludeGeneratorSources);
        Assert.False(d.IncludeLoadShunts);
    }

    // ---- 5. Export records the model ----------------------------------------------------------------

    [Fact]
    public void Export_records_the_machine_reactances_and_loads_the_study_used()
    {
        GridModel m = RadialFeeder();
        var sources = new HarmonicSourceSet { Case = m.Name, Sources = SixPulseAtBus3() };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, sources.Sources, HarmonicModelOptions.Study);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        var meta = new SolveExportMeta(m.Name, "harmonics", "test", "2026-10-02T00:00:00Z", 0.0, "test");

        using JsonDocument doc = JsonDocument.Parse(
            HarmonicExport.ToJson(m, sources, pen, idx, meta, modelOptions: HarmonicModelOptions.Study));
        JsonElement hm = doc.RootElement.GetProperty("harmonicModel");
        Assert.True(hm.GetProperty("includeGeneratorSources").GetBoolean());
        Assert.True(hm.GetProperty("includeLoadShunts").GetBoolean());

        JsonElement gen = Assert.Single(hm.GetProperty("generatorSources").EnumerateArray());
        Assert.Equal(1, gen.GetProperty("bus").GetInt32());
        Assert.Equal(Bits(0.35 * 100.0 / 200.0), Bits(gen.GetProperty("xSystemPu").GetDouble()));

        var loads = hm.GetProperty("loads").EnumerateArray().ToList();
        Assert.Equal(2, loads.Count);
        Assert.Equal(3, loads[1].GetProperty("bus").GetInt32());
        Assert.Equal(10.0, loads[1].GetProperty("pdMw").GetDouble());
        Assert.Equal(3.0, loads[1].GetProperty("qdMvar").GetDouble());
    }

    [Fact]
    public void Export_without_model_options_has_no_harmonic_model_block()
    {
        // Existing callers that pass no options keep the /1 document shape exactly.
        GridModel m = RadialFeeder();
        var sources = new HarmonicSourceSet { Case = m.Name, Sources = SixPulseAtBus3() };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, sources.Sources, HarmonicModelOptions.Study);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        var meta = new SolveExportMeta(m.Name, "harmonics", "test", "2026-10-02T00:00:00Z", 0.0, "test");

        using JsonDocument doc = JsonDocument.Parse(HarmonicExport.ToJson(m, sources, pen, idx, meta));
        Assert.False(doc.RootElement.TryGetProperty("harmonicModel", out _));
    }
}
