using System;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit.Ansi;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-20a: the ANSI/IEEE C37-style E/X duty (<see cref="AnsiDuty"/>). The IEEE C37 texts are paid and were not read, so every oracle is
/// derived here: hand-computed small networks (E/X, parallel reactances, current divider, separate R network), the DC-offset waveform
/// scanned numerically for the multiplying factors, and closed-form NACD. No remembered table value is used; the one published constant (1.6)
/// is cross-checked against the half-cycle rms formula, not assumed.
/// </summary>
public class AnsiShortCircuitTests
{
    private const double BaseMva = 100.0, Kv = 13.8;
    private static double IBaseKa => BaseMva / (Math.Sqrt(3.0) * Kv);

    private static string Src(int bus, string? name, double xr, bool local, double m1, string m2) =>
        "{\"bus\":" + bus + (name is null ? "" : ",\"name\":\"" + name + "\"") + ",\"class\":\"test\",\"xOverR\":" + xr + ",\"local\":" + (local ? "true" : "false")
        + ",\"firstCycleMultiplier\":" + m1 + ",\"interruptingMultiplier\":" + m2 + "}";

    private static AnsiData Data(params string[] sources) =>
        AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"sources\":[" + string.Join(",", sources) + "]}");

    /// <summary>Bus 1 (generator, X''d xd1) --branch(r,x)-- bus 2 (fault bus).</summary>
    private static GridModel Radial(double xd1, double r, double x) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xd1, Name: "G1") },
        Branches = new[] { new Branch(1, 2, r, x) },
    };

    /// <summary>Generators at 1 and 3, branches 1-2 (x1) and 3-2 (x3), fault at bus 2. All branch R zero.</summary>
    private static GridModel Two(double xd1, double xd3, double x1, double x3) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pv, BaseKv: Kv) },
        Generators = new[]
        {
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xd1, Name: "G1"),
            new Generator(Bus: 3, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xd3, Name: "G3"),
        },
        Branches = new[] { new Branch(1, 2, 0.0, x1), new Branch(3, 2, 0.0, x3) },
    };

    [Fact]
    public void Radial_network_by_hand_separate_R_and_X_networks()
    {
        // X: 0.2 + 0.10 = 0.30 ; R: 0.2/40 + 0.01 = 0.015 ; X/R = 20.
        AnsiBusResult r = AnsiDuty.Compute(Radial(0.2, 0.01, 0.10), 2, Data(Src(1, "G1", 40, false, 1.0, "1.5")), new AnsiOptions(PartingCycles: 3.0));
        Assert.Equal(AnsiStatus.Ok, r.Status);
        Assert.Equal(0.30, r.MomentaryXPu, 12);
        Assert.Equal(0.015, r.RPu, 12);
        Assert.Equal(20.0, r.MomentaryXoverR, 9);
        double sym = 1.0 / 0.30 * IBaseKa;
        Assert.Equal(sym, r.MomentarySymKa, 9);
        Assert.Equal(1.6, r.MomentaryMf, 12);                       // X/R <= 25: the published constant
        Assert.Equal(1.6 * sym, r.MomentaryRmsKa, 9);
        Assert.Equal(Math.Sqrt(2.0) * (1.0 + Math.Exp(-Math.PI / 20.0)) * sym, r.PeakKa, 9);
        // Interrupting network: X = 1.5 * 0.2 + 0.10 = 0.40 ; same R ; X/R = 26.667 ; remote MF at 3 cycles.
        Assert.Equal(0.40, r.InterruptingXPu, 12);
        double xr = 0.40 / 0.015;
        Assert.Equal(xr, r.InterruptingXoverR, 9);
        double mf = Math.Sqrt(1.0 + 2.0 * Math.Exp(-4.0 * Math.PI * 3.0 / xr));
        Assert.Equal(0.0, r.Nacd, 12);
        Assert.Equal(mf, r.InterruptingMf, 9);
        Assert.Equal(mf * (1.0 / 0.40) * IBaseKa, r.InterruptingKa, 9);
        Assert.True(r.InterruptingInScope);
    }

    [Fact]
    public void First_cycle_multiplier_scales_only_the_machine_reactance()
    {
        // 0.5 * 0.2 + 0.10 = 0.20 ; unit transformer would stay unscaled (none here).
        AnsiBusResult r = AnsiDuty.Compute(Radial(0.2, 0.01, 0.10), 2, Data(Src(1, "G1", 40, false, 0.5, "null")), new AnsiOptions());
        Assert.Equal(0.20, r.MomentaryXPu, 12);
        Assert.Equal(1.0 / 0.20 * IBaseKa, r.MomentarySymKa, 9);
    }

    [Fact]
    public void Unit_transformer_reactance_is_not_multiplied()
    {
        GridModel m = Radial(0.2, 0.0, 0.10);
        m = m.CopyWith(generators: new[] { m.Generators[0] with { UnitTransformerXPu = 0.05 } });
        AnsiBusResult r = AnsiDuty.Compute(m, 2, Data(Src(1, "G1", 40, false, 2.0, "null")), new AnsiOptions());
        Assert.Equal(2.0 * 0.2 + 0.05 + 0.10, r.MomentaryXPu, 12);
    }

    [Fact]
    public void Two_sources_current_divider_and_parallel_reactance_by_hand()
    {
        // G1 branch: 0.2 + 0.1 = 0.3 ; G3 branch: 0.4 + 0.1 = 0.5 ; parallel = 0.1875 ; E/X = 5.3333 pu.
        AnsiBusResult r = AnsiDuty.Compute(Two(0.2, 0.4, 0.1, 0.1), 2,
            Data(Src(1, "G1", 40, false, 1.0, "1.0"), Src(3, "G3", 40, false, 1.0, "1.0")), new AnsiOptions(PartingCycles: 2.0));
        Assert.Equal(0.1875, r.MomentaryXPu, 12);
        Assert.Equal(1.0 / 0.3 * IBaseKa, r.Sources[0].MomentaryKa, 9);
        Assert.Equal(1.0 / 0.5 * IBaseKa, r.Sources[1].MomentaryKa, 9);
        Assert.Equal(r.MomentarySymKa, r.Sources.Sum(s => s.MomentaryKa), 9);
        // R network: branch R is zero (floored at 1e-9 pu), machine R = Xd/40 : 0.005 and 0.010 in parallel.
        double rth = 1.0 / (1.0 / 0.005 + 1.0 / 0.010);
        Assert.Equal(rth, r.RPu, 6);
        double xr = 0.1875 / rth;
        Assert.Equal(xr, r.MomentaryXoverR, 3);
        // X/R above 25: the half-cycle rms expression replaces the constant.
        Assert.Equal(Math.Sqrt(1.0 + 2.0 * Math.Exp(-2.0 * Math.PI / r.MomentaryXoverR)), r.MomentaryMf, 12);
    }

    [Fact]
    public void Nacd_is_the_local_share_and_local_generation_blocks_the_interrupting_figure()
    {
        // Interrupting: G1 side 1.5*0.2 + 0.1 = 0.4 ; G3 side 1.5*0.4 + 0.1 = 0.7 ; share of G1 = X_par / 0.4.
        double xpar = 0.4 * 0.7 / (0.4 + 0.7);
        AnsiBusResult r = AnsiDuty.Compute(Two(0.2, 0.4, 0.1, 0.1), 2,
            Data(Src(1, "G1", 40, true, 1.0, "1.5"), Src(3, "G3", 40, false, 1.0, "1.5")), new AnsiOptions(PartingCycles: 3.0));
        Assert.Equal(AnsiStatus.Ok, r.Status);
        Assert.Equal(xpar / 0.4, r.Nacd, 12);
        Assert.False(r.InterruptingInScope);
        Assert.StartsWith("OUT OF SCOPE", r.InterruptingNote);
        Assert.True(r.ReferenceInterruptingKa > 0.0);               // reference only
        Assert.Equal(AnsiStatus.Ok, r.Status);
        Assert.True(r.Sources[0].MomentaryKa > 0.0);                // the momentary duty is still given
    }

    [Fact]
    public void Missing_inputs_give_incomplete_or_out_of_scope_never_a_default()
    {
        GridModel m = Radial(0.2, 0.01, 0.10);
        AnsiBusResult none = AnsiDuty.Compute(m, 2, AnsiData.Empty, new AnsiOptions());
        Assert.Equal(AnsiStatus.Incomplete, none.Status);
        Assert.True(double.IsNaN(none.MomentarySymKa));
        Assert.Contains("no ANSI data", string.Join(" ", none.Reasons));

        AnsiBusResult noInt = AnsiDuty.Compute(m, 2, Data(Src(1, "G1", 40, false, 1.0, "null")), new AnsiOptions(PartingCycles: 3.0));
        Assert.Equal(AnsiStatus.Ok, noInt.Status);
        Assert.False(noInt.InterruptingInScope);
        Assert.Contains("interrupting multiplier", noInt.InterruptingNote);

        AnsiBusResult noTime = AnsiDuty.Compute(m, 2, Data(Src(1, "G1", 40, false, 1.0, "1.0")), new AnsiOptions());
        Assert.False(noTime.InterruptingInScope);
        Assert.Contains("contact parting time", noTime.InterruptingNote);

        Assert.Equal(AnsiStatus.Failed, AnsiDuty.Compute(m, 99, Data(Src(1, "G1", 40, false, 1.0, "1.0")), new AnsiOptions()).Status);
        Assert.Equal(AnsiStatus.Incomplete, AnsiDuty.Compute(m, 2, Data(Src(1, "G1", 40, false, 1.0, "1.0"), Src(7, "ZZ", 40, false, 1.0, "1.0")), new AnsiOptions()).Status);
        // A case X''d of zero would be the synthesised default: refused.
        AnsiBusResult synth = AnsiDuty.Compute(Radial(0.0, 0.01, 0.10), 2, Data(Src(1, "G1", 40, false, 1.0, "1.0")), new AnsiOptions());
        Assert.Equal(AnsiStatus.Incomplete, synth.Status);
    }

    [Fact]
    public void Motor_is_an_extra_source_on_its_own_base_and_a_disconnected_island_is_ignored()
    {
        GridModel m = Radial(0.2, 0.0, 0.10);
        // Motor at the fault bus: 5 MVA, X''d 0.2 pu on its base = 0.2 * 100/5 = 4.0 pu on 100 MVA; multiplier 1.0 first cycle.
        AnsiData d = AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"sources\":[" + Src(1, "G1", 40, false, 1.0, "1.0")
            + "],\"motors\":[{\"name\":\"M1\",\"bus\":2,\"ratedMva\":5,\"xdppPu\":0.2,\"class\":\"motor\",\"xOverR\":10,\"local\":false,"
            + "\"firstCycleMultiplier\":1.0,\"interruptingMultiplier\":1.5}]}");
        AnsiBusResult r = AnsiDuty.Compute(m, 2, d, new AnsiOptions());
        double xpar = 0.30 * 4.0 / (0.30 + 4.0);
        Assert.Equal(xpar, r.MomentaryXPu, 12);
        Assert.Equal(1.0 / 4.0 * IBaseKa, r.Sources.Single(s => s.Kind == "motor").MomentaryKa, 9);
        // Interrupting: 1.5 * 4.0 = 6.0 for the motor.
        Assert.Equal(0.30 * 6.0 / 6.30, r.InterruptingXPu, 12);
        // An island with its own machine, not connected to the fault: ignored and needs no data.
        GridModel two = new()
        {
            BaseMva = BaseMva,
            Buses = m.Buses.Concat(new[] { new Bus(8, BusType.Pq, BaseKv: Kv), new Bus(9, BusType.Pq, BaseKv: Kv) }).ToList(),
            Generators = m.Generators.Concat(new[] { new Generator(Bus: 9, Pg: 0.0, MBase: 50.0, SubtransientXdPu: 0.3, Name: "Far") }).ToList(),
            Branches = m.Branches.Concat(new[] { new Branch(8, 9, 0.0, 0.1) }).ToList(),
        };
        AnsiBusResult r2 = AnsiDuty.Compute(two, 2, Data(Src(1, "G1", 40, false, 1.0, "1.0")), new AnsiOptions());
        Assert.Equal(AnsiStatus.Ok, r2.Status);
        Assert.Equal(0.30, r2.MomentaryXPu, 12);
    }

    [Fact]
    public void Breaker_check_signed_margins()
    {
        AnsiBusResult r = AnsiDuty.Compute(Radial(0.2, 0.01, 0.10), 2, Data(Src(1, "G1", 40, false, 1.0, "1.0")),
            new AnsiOptions(PartingCycles: 3.0, Breaker: new AnsiBreaker(15.0, 40.0, 20.0)));
        Assert.NotNull(r.Breaker);
        Assert.Equal(40.0 - r.MomentaryRmsKa, r.Breaker!.ClosingMarginKa, 12);
        Assert.Equal(20.0 - r.InterruptingKa, r.Breaker.InterruptingMarginKa, 12);
        Assert.Equal(r.Breaker.ClosingOk && r.Breaker.InterruptingOk && r.Breaker.VoltageOk, r.Breaker.Adequate);
        AnsiBusResult weak = AnsiDuty.Compute(Radial(0.2, 0.01, 0.10), 2, Data(Src(1, "G1", 40, false, 1.0, "1.0")),
            new AnsiOptions(PartingCycles: 3.0, Breaker: new AnsiBreaker(5.0, 10.0, 5.0)));
        Assert.False(weak.Breaker!.VoltageOk);
        Assert.False(weak.Breaker.ClosingOk);
        Assert.False(weak.Breaker.Adequate);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        GridModel m = Two(0.2, 0.4, 0.1, 0.1);
        AnsiData d = Data(Src(1, "G1", 40, false, 1.0, "1.5"), Src(3, "G3", 25, false, 1.0, "1.5"));
        AnsiBusResult a = AnsiDuty.Compute(m, 2, d, new AnsiOptions(PartingCycles: 3.0));
        AnsiBusResult b = AnsiDuty.Compute(m, 2, d, new AnsiOptions(PartingCycles: 3.0));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.InterruptingKa), BitConverter.DoubleToInt64Bits(b.InterruptingKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MomentaryRmsKa), BitConverter.DoubleToInt64Bits(b.MomentaryRmsKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PeakKa), BitConverter.DoubleToInt64Bits(b.PeakKa));
    }

    // ---- multiplying factors: the DC-offset waveform scanned numerically ----

    /// <summary>The worst-case offset current in per unit of the symmetrical rms: i(theta) = sqrt2 [ e^(-theta/xr) - cos(theta) ].</summary>
    private static double Wave(double theta, double xr) => Math.Sqrt(2.0) * (Math.Exp(-theta / xr) - Math.Cos(theta));

    [Theory]
    [InlineData(5.0, 0.5)]
    [InlineData(17.0, 1.5)]
    [InlineData(40.0, 3.0)]
    [InlineData(100.0, 4.0)]
    public void Asymmetrical_rms_factor_equals_ac_and_dc_parts_of_the_waveform(double xr, double cycles)
    {
        // At the instant n cycles, DC part = sqrt2 e^(-2 pi n / xr) (read off the waveform), AC rms = 1: total rms = sqrt(1 + dc^2).
        double theta = 2.0 * Math.PI * cycles;
        double dc = Wave(theta, xr) + Math.Sqrt(2.0) * Math.Cos(theta);   // the non-cosine part of the waveform
        Assert.Equal(Math.Sqrt(1.0 + dc * dc), MultiplyingFactors.AsymmetricalRmsFactor(xr, cycles), 12);
    }

    [Fact]
    public void Limits_of_the_factors()
    {
        Assert.Equal(Math.Sqrt(3.0), MultiplyingFactors.AsymmetricalRmsFactor(double.PositiveInfinity, 3.0), 12);   // no DC decay
        Assert.Equal(Math.Sqrt(3.0), MultiplyingFactors.AsymmetricalRmsFactor(10.0, 0.0), 12);                       // inception
        Assert.True(MultiplyingFactors.AsymmetricalRmsFactor(5.0, 20.0) < 1.0 + 1e-12);                              // fully decayed
        Assert.True(MultiplyingFactors.RemoteInterruptingMf(20.0, 1.5) > MultiplyingFactors.RemoteInterruptingMf(20.0, 4.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MultiplyingFactors.AsymmetricalRmsFactor(0.0, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MultiplyingFactors.AsymmetricalRmsFactor(5.0, -1.0));
    }

    [Fact]
    public void Published_momentary_constant_agrees_with_the_half_cycle_rms_expression_at_x_over_r_25()
    {
        double derived = MultiplyingFactors.AsymmetricalRmsFactor(MultiplyingFactors.MomentaryMfXrLimit, 0.5);
        Assert.InRange(derived, MultiplyingFactors.MomentaryMfPublished - 0.005, MultiplyingFactors.MomentaryMfPublished + 0.005);
        Assert.Equal(1.6, MultiplyingFactors.MomentaryMf(25.0), 12);
        Assert.Equal(derived, MultiplyingFactors.MomentaryMf(25.0001), 3);
        Assert.True(MultiplyingFactors.MomentaryMf(80.0) > 1.6);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(17.0)]
    [InlineData(60.0)]
    public void Half_cycle_crest_factor_is_within_a_fraction_of_a_percent_of_the_scanned_waveform_peak(double xr)
    {
        double peak = 0.0;
        for (double th = 0.0; th <= 2.0 * Math.PI; th += 1e-5) peak = Math.Max(peak, Math.Abs(Wave(th, xr)));
        double formula = Math.Sqrt(2.0) * MultiplyingFactors.HalfCyclePeakFactor(xr);
        Assert.Equal(peak, formula, peak * 5e-3);
        Assert.True(peak >= formula - 1e-9);                         // the closed form never exceeds the true peak
    }

    // ---- data file ----

    [Fact]
    public void Data_parser_is_strict()
    {
        Assert.Throws<FormatException>(() => AnsiData.Parse("{"));
        Assert.Throws<FormatException>(() => AnsiData.Parse("{\"schema\":\"x\"}"));
        Assert.Throws<FormatException>(() => AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"extra\":1}"));
        Assert.Throws<FormatException>(() => AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"sources\":[{\"bus\":1,\"class\":\"t\",\"xOverR\":10,\"local\":true,\"firstCycleMultiplier\":1}]}"));   // no interrupting key
        Assert.Throws<FormatException>(() => AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"sources\":[{\"bus\":1,\"class\":\"t\",\"xOverR\":-1,\"local\":true,\"firstCycleMultiplier\":1,\"interruptingMultiplier\":1}]}"));
        Assert.Throws<FormatException>(() => AnsiData.Parse("{\"schema\":\"gridsim-ansi-data/1\",\"sources\":[{\"bus\":1,\"class\":\"t\",\"xOverR\":10,\"firstCycleMultiplier\":1,\"interruptingMultiplier\":1}]}"));   // no local
        AnsiData ok = Data(Src(1, "G1", 40, true, 1.0, "null"));
        Assert.Null(ok.Sources[0].InterruptingMultiplier);
    }
}
