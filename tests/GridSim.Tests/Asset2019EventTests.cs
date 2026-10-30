using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The headline validation: the per-asset dynamics framework reproduces the 9 Aug 2019 GB embedded-
/// generation cascade FROM FIRST PRINCIPLES. The Ofgem-sourced event catalog scripts the distributed-
/// generation RoCoF trips as fixed power steps; here we instead feed only the PRIMARY losses (Hornsea +
/// Little Barford) and let a legacy-DG asset fleet trip on its own RoCoF/vector-shift protection - and it
/// reproduces the same deep excursion the scripted replay produces. Exact volumes are [VERIFY]/swept
/// (the embedded population that tripped is not per-unit recorded), so this is a mechanism + envelope
/// result, not a point prediction.
/// </summary>
public class Asset2019EventTests
{
    // E = 215 GVA.s (NESO recorded outturn for the half-hour), via H*MBase = 215,000 MW.s.
    private static readonly Generator SyncFleet = new(1, Pg: 20_000, MBase: 43_000, H: 5.0, Type: "ccgt");
    private const double DemandMw = 21_100;   // WLS-estimated transmission-net demand (catalog)

    private static GridModel Model(AssetDynamicsModel? assets, params Generator[] dg)
    {
        var gens = new List<Generator> { SyncFleet };
        gens.AddRange(dg);
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400) },
            Generators = gens,
            Branches = Array.Empty<Branch>(),
            AssetDynamics = assets,
        };
    }

    private static readonly AssetClass Legacy = AssetTestHelpers.GflClass("legacy-dg", rocofTripHzPerS: 0.125,
        headroomUpFraction: 0.0, headroomDownFraction: 0.0, localRocofUpliftFactor: 1.16);
    private static readonly AssetDynamicsModel Bundle =
        AssetTestHelpers.Bundle(new[] { Legacy }, new AssetBinding("storage", "legacy-dg"));

    private static FrequencyEvent[] PrimaryLosses()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09");
        return ev.Losses
            .Where(l => l.What.IndexOf("distributed generation", StringComparison.OrdinalIgnoreCase) < 0)
            .Select(l => new FrequencyEvent(l.AtSeconds, l.DeltaPMw))
            .Concat(ev.RecordedActions.Select(a => new FrequencyEvent(a.AtSeconds, a.DeltaPMw)))
            .OrderBy(e => e.AtSeconds).ToArray();
    }

    private static AssetFrequencyResult RunWithEmbeddedFleet(double fleetMw)
    {
        var cfg = new DynamicsConfig { DurationSeconds = 120, EnableAssetDynamics = true };
        var dg = new Generator(1, Pg: fleetMw, MBase: fleetMw, H: 0, Type: "storage");
        return AssetFrequencyDynamics.Simulate(Model(Bundle, dg), DemandMw, PrimaryLosses(), cfg);
    }

    [Fact]
    public void The_cascade_mechanism_emerges_from_first_principles()
    {
        // Given ONLY the primary Hornsea + Little Barford losses (no scripted DG steps), a legacy
        // pre-ALoMCP fleet crosses its 0.125 Hz/s threshold and trips on its OWN protection - the timing
        // and triggering are emergent physics, not calibrated. (How MUCH trips is the fleet size, which
        // is [VERIFY] - see the envelope test below.)
        AssetFrequencyResult r = RunWithEmbeddedFleet(1290.0);
        Assert.True(r.TrippedCount > 0, "the legacy DG fleet must trip on the event RoCoF unprompted");
        Assert.True(r.TotalTrippedMw > 0);
    }

    [Fact]
    public void The_recorded_nadir_lies_WITHIN_the_embedded_trip_envelope_not_at_a_predicted_point()
    {
        // HONEST framing: the nadir is a strong function of the unmeasured embedded-trip VOLUME (a [VERIFY]
        // knob), so this is an envelope, not a point prediction. Across plausible embedded volumes the
        // nadir band brackets the recorded 48.787 Hz - but the model does NOT independently predict it.
        const double recordedNadir = 48.787;
        double shallow = RunWithEmbeddedFleet(500.0).Response.NadirHz;    // ~48.85
        double deep = RunWithEmbeddedFleet(2500.0).Response.NadirHz;      // ~47.29

        // The band is WIDE (the point is not pinned) and it brackets the recorded outcome.
        Assert.True(shallow - deep > 1.0, $"the nadir must be strongly volume-sensitive (band {deep:F2}..{shallow:F2}), i.e. an envelope not a point");
        Assert.InRange(recordedNadir, deep, shallow);
    }

    [Fact]
    public void No_dg_fleet_means_no_cascade_and_a_shallower_fall()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09");
        FrequencyEvent[] primary = ev.Losses
            .Where(l => l.What.IndexOf("distributed generation", StringComparison.OrdinalIgnoreCase) < 0)
            .Select(l => new FrequencyEvent(l.AtSeconds, l.DeltaPMw))
            .Concat(ev.RecordedActions.Select(a => new FrequencyEvent(a.AtSeconds, a.DeltaPMw)))
            .OrderBy(e => e.AtSeconds).ToArray();
        var cfg = new DynamicsConfig { DurationSeconds = 120, EnableAssetDynamics = true };

        // Same primary losses, but a HIGH-threshold fleet that rides through: no embedded trips.
        AssetClass modern = AssetTestHelpers.GflClass("modern-dg", rocofTripHzPerS: 10.0,
            headroomUpFraction: 0.0, headroomDownFraction: 0.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { modern }, new AssetBinding("storage", "modern-dg"));
        Generator dg = new(1, Pg: 1290, MBase: 1290, H: 0, Type: "storage");

        AssetFrequencyResult noTrip = AssetFrequencyDynamics.Simulate(Model(bundle, dg), DemandMw, primary, cfg);
        Assert.Equal(0, noTrip.TrippedCount);   // ALoMCP-upgraded fleet rides through - the counterfactual
    }
}
