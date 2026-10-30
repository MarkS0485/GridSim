using GridSim.Core.Assets;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Fixes for limitations that technical chapter 36 used to list (October 2026): a detection latency longer than the
/// measurement history is refused (GR-620), a battery converter efficiency outside [0.5, 1] is refused (GR-627), the
/// IEC 60909 corrections leave an element on a bus with no base voltage uncorrected and listed (GR-126), and the CIM
/// export uses one newline on every operating system (GR-637).
/// </summary>
public sealed class Fix3LimitationFixesTests
{
    // ================================================================ GR-620: measurement delay ================

    [Fact]
    public void GR620_the_delay_reports_a_latency_only_once_the_full_history_cannot_hold_it()
    {
        var d = new MeasurementDelay();
        for (int i = 0; i < 100; i++) d.Push(i * 0.02, 50.0, 0.0, 0.0);
        // Not full: the start of a run, whatever the latency.
        Assert.False(d.TruncatesLatency(99 * 0.02, 1000.0));
        for (int i = 100; i < 600; i++) d.Push(i * 0.02, 50.0, 0.0, 0.0);
        // Full (512 samples, oldest push 88 at 1.76 s): a latency inside the stored span is fine, one beyond it is not.
        Assert.False(d.TruncatesLatency(599 * 0.02, 0.1));
        Assert.False(d.TruncatesLatency(599 * 0.02, (599 - 88) * 0.02));
        Assert.True(d.TruncatesLatency(599 * 0.02, 1000.0));
    }

    [Fact]
    public void GR620_an_inverter_whose_latency_outruns_the_history_is_refused_not_shortened()
    {
        // 20 s of latency at a 20 ms substep needs 1,000 samples; the history holds 512. The first 511 steps are the
        // ordinary start of a run (the history is not yet full); the 512th fills it and is refused.
        AssetClass cls = AssetTestHelpers.GflClass("long-latency", detectionLatencyS: 20.0);
        var gfl = new GridFollowingInverter(cls, bus: 1, pgMw: 10, ratedMw: 100);
        for (int k = 0; k < 511; k++) gfl.Update(new AssetStepContext(50.0, 0.0, 0.0, 0.02, k * 0.02));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => gfl.Update(new AssetStepContext(50.0, 0.0, 0.0, 0.02, 511 * 0.02)));
        Assert.Contains("detection latency", ex.Message, StringComparison.Ordinal);

        // A bundled-size latency (0.1 s) runs far past the wrap without a word.
        var ok = new GridFollowingInverter(AssetTestHelpers.GflClass("bundled-like", detectionLatencyS: 0.1), bus: 1, pgMw: 10, ratedMw: 100);
        for (int k = 0; k < 1500; k++) ok.Update(new AssetStepContext(50.0, 0.0, 0.0, 0.02, k * 0.02));
    }

    // ================================================================ GR-627: converter efficiency =============

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.0)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void GR627_a_converter_efficiency_outside_the_band_is_refused_not_clamped(double eta)
    {
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.8);
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new BatteryDcSource(pack, eta));
        Assert.Equal("converterEfficiency", ex.ParamName);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.98)]
    [InlineData(1.0)]
    public void GR627_a_converter_efficiency_inside_the_band_is_accepted(double eta)
    {
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.8);
        var src = new BatteryDcSource(pack, eta);
        Assert.Equal(pack.Soc, src.SocFraction);
    }

    // ================================================================ GR-126: c_max with no base voltage =======

    [Fact]
    public void GR126_an_element_on_a_bus_with_no_base_voltage_is_listed_not_corrected_as_low_voltage()
    {
        // Bus 1 has no base voltage. Its generator and the transformer 1-2 cannot choose a c_max, so neither is
        // corrected; the 2-3 transformer, on 132 kV buses, is.
        var m = new GridModel
        {
            Name = "no-kv",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 0), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, SubtransientXdPu: 0.2) },
            Branches = new[]
            {
                new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true, RateAMva: 100),
                new Branch(2, 3, 0, 0.1, Name: "T2", IsTransformer: true, RateAMva: 100),
            },
        };
        Iec60909.CorrectionReport cr = Iec60909.ApplyCorrectionFactors(m, 0.85);
        Assert.Equal(2, cr.NotApplied.Count);
        Assert.Contains(cr.NotApplied, s => s.Contains("K_G not applied", StringComparison.Ordinal));
        Assert.Contains(cr.NotApplied, s => s.StartsWith("T1:", StringComparison.Ordinal));
        Iec60909.CorrectionApplied t2 = Assert.Single(cr.Applied);
        Assert.Equal("K_T", t2.Factor);
        // Uncorrected elements are carried unchanged (same record values).
        Assert.Equal(m.Generators[0], cr.Model.Generators[0]);
        Assert.Equal(m.Branches[0], cr.Model.Branches[0]);
    }

    // ================================================================ GR-637: CIM export newline ==============

    [Fact]
    public void GR637_the_cim_export_uses_a_line_feed_newline_on_every_operating_system()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "N1", 132.0), new CimBus("n2", "N2", 132.0) },
            new[] { new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0) },
            Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 132.0, VoltageControl: true) },
            Array.Empty<CimTransformer>());
        string xml = CimExporter.Export(net);
        Assert.Equal(-1, xml.IndexOf((char)13));      // no carriage return, on Windows as on Linux
        Assert.NotEqual(-1, xml.IndexOf((char)10));   // still indented, one element per line
        Assert.StartsWith("<rdf:RDF", xml, StringComparison.Ordinal);   // no XML declaration, as before
        CimNetwork back = CimImporter.Parse(xml);
        Assert.Equal(net.Branches.Single(), back.Branches.Single());
    }
}
