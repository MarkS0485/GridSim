using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>Binding resolution: inline wins over a rule, an unknown class fails loud, unmatched stays passive.</summary>
public class AssetBindingTests
{
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };
    private static readonly DynamicsConfig Cfg = new() { EnableAssetDynamics = true };

    [Fact]
    public void Inline_class_overrides_a_matching_rule()
    {
        var set = new AssetBindingSet(new[] { new AssetBinding("storage", "rule-class") });
        Assert.Equal("inline-class", set.Resolve(inlineClass: "inline-class", assetType: "storage"));
        Assert.Equal("rule-class", set.Resolve(inlineClass: null, assetType: "storage"));
        Assert.Null(set.Resolve(inlineClass: null, assetType: "wind"));   // unmatched => passive
    }

    [Fact]
    public void Prefix_wildcard_matches_the_hyphenated_gb_full_vocabulary()
    {
        // gb-full emits wind-onshore / wind-offshore, not "wind" - a "wind*" rule must catch both.
        var set = new AssetBindingSet(new[] { new AssetBinding("wind*", "gfl-wind") });
        Assert.Equal("gfl-wind", set.Resolve(null, "wind-onshore"));
        Assert.Equal("gfl-wind", set.Resolve(null, "wind-offshore"));
        Assert.Null(set.Resolve(null, "solar"));   // wildcard doesn't over-match
    }

    [Fact]
    public void Exact_rule_beats_a_wildcard_regardless_of_order()
    {
        // A broad wildcard plus a specific override; the specific one wins even though it is listed later.
        var set = new AssetBindingSet(new[]
        {
            new AssetBinding("wind*", "gfl-generic"),
            new AssetBinding("wind-offshore", "gfl-offshore-special"),
        });
        Assert.Equal("gfl-offshore-special", set.Resolve(null, "wind-offshore"));
        Assert.Equal("gfl-generic", set.Resolve(null, "wind-onshore"));
    }

    [Fact]
    public void Gb_full_vocabulary_binds_the_inverter_fleet_and_excludes_synchronous_and_synthetic()
    {
        // The exact generator `type` vocabulary GDA's assembler writes (assemble/fuelmap.py) + the
        // asset-bindings.json rules build_asset_classes.py emits. Locks the GDA<->GridSim contract.
        var rules = new[]
        {
            new AssetBinding("wind*", "gfl-wind"),
            new AssetBinding("solar", "gfl-solar"),
            new AssetBinding("storage", "gfm-bess"),
            new AssetBinding("interconnector", "hvdc"),
            new AssetBinding("tidal", "gfl-wind"),
        };
        var set = new AssetBindingSet(rules);

        // Inverter-based resources bind...
        Assert.Equal("gfl-wind", set.Resolve(null, "wind-onshore"));
        Assert.Equal("gfl-wind", set.Resolve(null, "wind-offshore"));
        Assert.Equal("gfl-solar", set.Resolve(null, "solar"));
        Assert.Equal("gfm-bess", set.Resolve(null, "storage"));
        Assert.Equal("hvdc", set.Resolve(null, "interconnector"));
        Assert.Equal("gfl-wind", set.Resolve(null, "tidal"));

        // ...synchronous plant and synthetic sources stay passive (must NOT get an inverter FFR loop).
        foreach (string sync in new[] { "ccgt", "ocgt", "gas", "chp", "coal", "biomass", "hydro", "nuclear", "recip", "pumped-storage", "grid", "other" })
            Assert.Null(set.Resolve(null, sync));
    }

    [Fact]
    public void An_unknown_bound_class_fails_loud_when_the_fleet_is_built()
    {
        // The generator names an inline class that the library does not define.
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { AssetTestHelpers.GflClass("known") });
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: 100, MBase: 100, H: 0, Type: "storage", DynamicsClass: "does-not-exist"));

        Assert.Throws<System.IO.InvalidDataException>(
            () => AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, Cfg));
    }

    [Fact]
    public void Unmatched_assets_stay_passive_and_do_not_contribute()
    {
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { AssetTestHelpers.GflClass("bess") },
            new AssetBinding("battery", "bess"));   // matches "battery", not "storage"
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: 500, MBase: 500, H: 0, Type: "storage"));

        AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, Cfg);
        FrequencyResponse baseline = FrequencyDynamics.Simulate(
            m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, Loss, Cfg);
        Assert.True(baseline.Points.SequenceEqual(r.Response.Points));
    }
}
