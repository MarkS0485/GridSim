using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R289: <see cref="GridInertiaTransforms.SetGfmFraction(GridModel, double, string, string)"/> tags whole converter units
/// largest first until the tagged MW reaches the target, so it can overshoot by up to one unit, and the H=0 study recorded
/// only the REQUESTED fraction. The transform now returns the realised share and every cell carries it
/// (<see cref="H0StudyCell.RealisedGfmFraction"/>); the tagging, and so every computed number, is unchanged.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    /// <summary>The H0StudyEngineTests fleet: sync 20 GW (H 4 s, 10 GVA), wind 2,000 / 1,500 MW, storage 800 / 600 MW.</summary>
    private static GridModel R289Fleet()
    {
        var gens = new List<Generator>
        {
            new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),
            new(1, Pg: 2_000, MBase: 3_000, H: 0, Name: "w1", Type: "wind"),
            new(1, Pg: 1_500, MBase: 2_500, H: 0, Name: "w2", Type: "wind"),
            new(1, Pg: 800, MBase: 1_500, H: 0, Name: "s1", Type: "storage"),
            new(1, Pg: 600, MBase: 1_000, H: 0, Name: "s2", Type: "storage"),
        };
        AssetClass gfl = AssetTestHelpers.GflClass("gfl-test", rocofTripHzPerS: 1.0);
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-test", synInertiaMwsPerHz: 1600.0, dampingMwPerHz: 200.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { gfl, gfm }, new AssetBinding("wind*", "gfl-test"), new AssetBinding("storage", "gfm-test"));
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = gens,
            Branches = Array.Empty<Branch>(),
            AssetDynamics = bundle,
        };
    }

    [Theory]
    // Converter MW 4,900 (2000 + 1500 + 800 + 600), filled largest first while the tagged MW is below the target.
    [InlineData(0.0, 0.0)]                    // nothing tagged
    [InlineData(0.25, 2000.0 / 4900.0)]       // target 1,225: the 2,000 MW unit, realised 0.408 (the overshoot R289 names)
    [InlineData(0.5, 3500.0 / 4900.0)]        // target 2,450: 2,000 then 1,500
    [InlineData(0.75, 4300.0 / 4900.0)]       // target 3,675: 2,000, 1,500, 800
    [InlineData(1.0, 1.0)]                    // everything; the same sum in the same order, so exactly 1
    public void R289_the_realised_gfm_fraction_is_the_share_actually_tagged(double requested, double realised)
    {
        GridModel m = R289Fleet();
        GridModel plain = GridInertiaTransforms.SetGfmFraction(m, requested, "gfm-test", "gfl-test");
        GridModel withOut = GridInertiaTransforms.SetGfmFraction(m, requested, "gfm-test", "gfl-test", out double got);
        AssertBits(realised, got, $"realised at {requested}");

        // The tagging itself is unchanged: both overloads tag the same units the same way.
        Assert.Equal(plain.Generators.Select(g => g.DynamicsClass), withOut.Generators.Select(g => g.DynamicsClass));
    }

    [Fact]
    public void R289_every_h0_cell_carries_the_requested_and_the_realised_fraction()
    {
        // The H0StudyEngineTests self-consistent single-loss event.
        var cfg = new DynamicsConfig();
        FrequencyResponse truth = FrequencyDynamics.Simulate(155_000, 32_000, new[] { new FrequencyEvent(1.0, -1320.0) }, cfg);
        var ev = new GridEvent(
            "syn", "self-consistent", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 155_000, 32_000,
            new List<PowerStep> { new(1.0, -1320.0, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
        var spec = new H0StudySpec(ev, R289Fleet(), new[] { 1.0 }, new[] { 0.0, 0.25, 1.0 }, new[] { Corner.Best },
            "gfm-test", "gfl-test", new DynamicsConfig { EnableAssetDynamics = true }, ReplayTolerance.Default);
        H0StudyResult r = H0StudyEngine.Run(spec);

        Assert.Equal(0.0, r.Cells.Single(c => c.GfmFraction == 0.0).RealisedGfmFraction);
        AssertBits(2000.0 / 4900.0, r.Cells.Single(c => c.GfmFraction == 0.25).RealisedGfmFraction, "0.25 cell");
        Assert.Equal(1.0, r.Cells.Single(c => c.GfmFraction == 1.0).RealisedGfmFraction);
        Assert.Contains("realisedGfmFraction = c.RealisedGfmFraction", Src("src", "GridSim.Core", "IO", "H0StudyExport.cs"));
    }
}
