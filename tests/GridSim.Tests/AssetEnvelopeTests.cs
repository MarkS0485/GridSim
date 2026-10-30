using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The envelope discipline: sweeping the unmeasurable RoCoF trip threshold over its plausible range
/// brackets BOTH "the embedded fleet trips" and "it rides through". That bracketing is the honest
/// finding (the data can't resolve it), the tornado attributes the leverage, and the result is tagged
/// UnvalidatedAtScale.
/// </summary>
public class AssetEnvelopeTests
{
    private static readonly IReadOnlyList<FrequencyEvent> Loss = new[] { new FrequencyEvent(1.0, -1000.0) };

    private static GridModel ModelWithEmbeddedDg()
    {
        AssetClass dg = AssetTestHelpers.GflClass("embedded-dg", rocofTripHzPerS: 0.5, headroomUpFraction: 0.0, headroomDownFraction: 0.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { dg }, new AssetBinding("storage", "embedded-dg"));
        return AssetTestHelpers.Model(bundle, new Generator(1, Pg: 2000, MBase: 2000, H: 0, Type: "storage"));
    }

    [Fact]
    public void Sweeping_the_trip_threshold_brackets_cascade_and_no_cascade()
    {
        GridModel m = ModelWithEmbeddedDg();
        var sweep = new[] { new AssetSweepParam("embedded-dg", AssetParams.RocofTripHzPerS, Low: 0.05, High: 10.0) };

        AssetEnvelopeResult env = AssetEnvelope.Sweep(m, AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig(), sweep);

        Assert.Equal(ProvenanceTag.UnvalidatedAtScale, env.Provenance);
        Assert.True(env.CascadeIsPossible, "the band must bracket both trip and ride-through");
        Assert.True(env.NadirMinHz < env.NadirMaxHz, "a real nadir band, not a point");
        Assert.Equal(2, env.Samples.Count);   // 2^1 corners

        // The trip threshold is the dominant lever here.
        TornadoEntry top = env.Tornado[0];
        Assert.Equal(AssetParams.RocofTripHzPerS, top.ParamName);
        Assert.True(top.NadirSwingHz > 0.01, "the swept threshold must move the nadir");
    }

    [Fact]
    public void A_ride_through_only_range_reports_no_cascade()
    {
        GridModel m = ModelWithEmbeddedDg();
        // Both ends of the range are well above the event RoCoF => never trips.
        var sweep = new[] { new AssetSweepParam("embedded-dg", AssetParams.RocofTripHzPerS, Low: 5.0, High: 10.0) };

        AssetEnvelopeResult env = AssetEnvelope.Sweep(m, AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig(), sweep);

        Assert.False(env.CascadeIsPossible);
        Assert.All(env.Samples, s => Assert.Equal(0, s.TrippedCount));
    }
}
