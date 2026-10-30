using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R138 (the PLL remainder after D2): the three representations of one grid-following converter fell back to three
/// differently tuned PLLs (EMT 20 Hz / 0.707, impedance 5 Hz / 0.707, small-signal 5 Hz / 1.0), and the small-signal
/// block ignored an authored PLL damping. All three now take one default, ConverterDefaults.PllBandwidthHz (20 Hz) at
/// ConverterDefaults.PllDampingRatio (0.707); the small-signal block reads the class's PLL damping; its stand-in for the
/// time-domain controller's detection delay (every bundled class) keeps its critical damping, so those blocks are
/// bit-identical.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    private static AssetClass R138Gfl(params (string Key, double Value)[] extra)
    {
        AssetClass baseClass = AssetTestHelpers.GflClass("gfl-r138");
        var p = new Dictionary<string, double>(baseClass.Params);
        foreach ((string k, double v) in extra) p[k] = v;
        return baseClass with { Params = p };
    }

    [Fact]
    public void R138_the_three_models_fall_back_to_one_pll()
    {
        Assert.Equal(20.0, ConverterDefaults.PllBandwidthHz);
        Assert.Equal(0.707, ConverterDefaults.PllDampingRatio);

        // EMT: the record default.
        var unit = new RmsConverterUnit(0, "u", 1.0, 0.5, 0.0, System.Numerics.Complex.One, new RmsConverterParameters());
        Assert.Equal(ConverterDefaults.PllBandwidthHz, new ThreePhaseConverter(0, unit).PllBandwidthHz);

        // Impedance: a class with no PLL parameters is the flag-built model at the one default, gain for gain.
        var op = new ConverterOperatingPoint(1.0, 0.0, 0.8, -0.1, 0.8, 0.1);
        AssetClass noPll = R138Gfl();   // detection latency 0: no delay stand-in either
        GridFollowingImpedance fromClass = GridFollowingImpedance.FromAssetClass(noPll, 2, op, ImpedanceModelDetail.PllOuter);
        GridFollowingImpedance reference = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 20.0, 0.707, 0.0);
        AssertBits(reference.PllGains.Kp, fromClass.PllGains.Kp, "impedance Kp");
        AssertBits(reference.PllGains.Ki, fromClass.PllGains.Ki, "impedance Ki");

        // Small-signal: the same class gives the estimator wn = 2 pi 20, zeta = 0.707 (was 2 pi 5 and 1.0).
        InverterLinearBlock b = LinearInverterModel.ForGfl(noPll, 2, 100.0, 50.0);
        double wn = 2.0 * Math.PI * 20.0;
        AssertBits(-wn * wn, b.Aself[1, 0], "small-signal -wn^2");
        AssertBits(-2.0 * 0.707 * wn, b.Aself[1, 1], "small-signal -2 zeta wn");
    }

    [Fact]
    public void R138_a_class_with_a_detection_latency_keeps_its_critically_damped_delay_stand_in_bit_for_bit()
    {
        // Every bundled grid-following class: a latency, no PLL keys (gfl-wind-2024 0.1 s, gfl-solar-2024 0.12 s,
        // hvdc-2024 0.05 s). The block is the pre-R138 one: wn = 2 pi max(1e-3, 1 / (2 pi latency)), zeta = 1.
        foreach (double latency in new[] { 0.1, 0.12, 0.05 })
        {
            AssetClass cls = AssetTestHelpers.GflClass("bundled-like", detectionLatencyS: latency);
            InverterLinearBlock b = LinearInverterModel.ForGfl(cls, 4, 250.0, 50.0);
            double wn = 2.0 * Math.PI * Math.Max(1e-3, 1.0 / (2.0 * Math.PI * latency));
            AssertBits(-wn * wn, b.Aself[1, 0], $"latency {latency} -wn^2");
            AssertBits(-2.0 * 1.0 * wn, b.Aself[1, 1], $"latency {latency} -2 zeta wn");
            AssertBits(wn * wn, b.FreqInputRow[1], $"latency {latency} input");
        }
    }

    [Fact]
    public void R138_the_small_signal_block_reads_an_authored_pll_as_the_impedance_model_does()
    {
        // An authored bandwidth and damping: both models use them (the small-signal block ignored the damping).
        AssetClass pll = R138Gfl((AssetParams.PllBandwidthHz, 12.0), (AssetParams.PllDampingRatio, 0.5),
            (AssetParams.DetectionLatencyS, 0.1));   // an authored PLL wins over the latency
        InverterLinearBlock b = LinearInverterModel.ForGfl(pll, 2, 100.0, 50.0);
        double wn = 2.0 * Math.PI * 12.0;
        AssertBits(-wn * wn, b.Aself[1, 0], "authored -wn^2");
        AssertBits(-2.0 * 0.5 * wn, b.Aself[1, 1], "authored -2 zeta wn");

        // An authored bandwidth alone: the one default damping (it was 1.0).
        InverterLinearBlock bw = LinearInverterModel.ForGfl(R138Gfl((AssetParams.PllBandwidthHz, 12.0)), 2, 100.0, 50.0);
        AssertBits(-2.0 * 0.707 * wn, bw.Aself[1, 1], "authored bandwidth, default damping");

        // A latency with an authored damping: the delay stand-in's bandwidth at the authored damping.
        InverterLinearBlock ld = LinearInverterModel.ForGfl(R138Gfl((AssetParams.DetectionLatencyS, 0.1), (AssetParams.PllDampingRatio, 0.6)), 2, 100.0, 50.0);
        double wnD = 2.0 * Math.PI * Math.Max(1e-3, 1.0 / (2.0 * Math.PI * 0.1));
        AssertBits(-2.0 * 0.6 * wnD, ld.Aself[1, 1], "latency, authored damping");
    }

    [Fact]
    public void R138_every_pll_bandwidth_code_default_is_the_one_value()
    {
        JsonElement p = KindFileSet.Load("gridFollowingInverter").GetProperty("params").GetProperty("pllBandwidthHz");
        var entries = p.GetProperty("codeDefaults").EnumerateArray().ToList();
        // Moved 2026-10 (D1, final batch; B5 R138 / D2, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the kind
        // file format allows one codeDefaults entry per reference ("one entry per consumer, never repeated"), and three of
        // the four entries named the same ConverterDefaults.PllBandwidthHz, which broke the structure test. They are one
        // entry now that names every consumer of the shared constant, plus the EMT constructor parameter. The rule this
        // test checks (every PLL bandwidth default is the one 20 Hz value) is unchanged. Before -> after: 4 -> 2 entries.
        Assert.Equal(2, entries.Count);
        foreach (JsonElement c in entries)
        {
            Assert.Equal(20.0, c.GetProperty("value").GetDouble());
            Assert.Null(CodeDefaultRefResolver.Mismatch(c.GetProperty("ref").GetString()!, c.GetProperty("value")));
        }
        JsonElement d = KindFileSet.Load("gridFollowingInverter").GetProperty("params").GetProperty("pllDampingRatio");
        // Moved 2026-10 (D1, final batch; B5 R138 / D2, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): as above,
        // the two ConverterDefaults.PllDampingRatio entries are one shared entry. Before -> after: 4 -> 3 entries.
        Assert.Equal(3, d.GetProperty("codeDefaults").GetArrayLength());   // shared PLL default, delay stand-in, CLI
    }

    [Fact]
    public void R138_the_cli_placeholder_is_the_core_default_and_the_gfm_flag_model_keeps_its_own_loop_default()
    {
        string cliDefaults = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "CliDefaults.cs");
        Assert.Contains("PlaceholderPllBandwidthHz = GridSim.Core.Dynamics.ConverterDefaults.PllBandwidthHz", cliDefaults);
        string program = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "Program.cs");
        Assert.Contains("pllBw ?? GridSim.Core.Dynamics.ConverterDefaults.GfmPowerLoopBandwidthHz, 0.0, 0.0, mBase)", program);
    }
}
