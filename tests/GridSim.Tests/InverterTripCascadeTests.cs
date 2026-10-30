using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The load-bearing mechanism: a grid-following fleet whose RoCoF trip threshold sits below the event
/// RoCoF trips on Loss-of-Mains, its dispatched output becomes deficit, and the nadir goes DEEPER than
/// the same fleet that rides through - the 2019-08-09 embedded-generation feedback, in miniature.
/// </summary>
public class InverterTripCascadeTests
{
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };
    private static readonly DynamicsConfig Cfg = new() { EnableAssetDynamics = true };

    private static AssetFrequencyResult Run(double rocofTripHzPerS, double pgMw)
    {
        AssetClass cls = AssetTestHelpers.GflClass("embedded-dg", rocofTripHzPerS: rocofTripHzPerS);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", "embedded-dg"));
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: pgMw, MBase: pgMw, H: 0, Type: "storage"));
        return AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, Cfg);
    }

    [Fact]
    public void A_low_rocof_threshold_trips_and_deepens_the_nadir()
    {
        AssetFrequencyResult tripped = Run(rocofTripHzPerS: 0.05, pgMw: 2000.0);   // trips on the event RoCoF
        AssetFrequencyResult rideThrough = Run(rocofTripHzPerS: 10.0, pgMw: 2000.0); // never reaches threshold

        Assert.True(tripped.TrippedCount > 0, "the low-threshold fleet must trip");
        Assert.Equal(0, rideThrough.TrippedCount);
        Assert.True(tripped.TotalTrippedMw > 0, "tripped output must be booked as deficit");
        Assert.True(tripped.Response.NadirHz < rideThrough.Response.NadirHz,
            $"trip nadir {tripped.Response.NadirHz} should be deeper than ride-through {rideThrough.Response.NadirHz}");
    }

    [Fact]
    public void A_fleet_below_its_limit_does_not_trip()
    {
        AssetFrequencyResult r = Run(rocofTripHzPerS: 10.0, pgMw: 1000.0);
        Assert.Equal(0, r.TrippedCount);
        Assert.Equal(0.0, r.TotalTrippedMw);
    }

    [Fact]
    public void Local_rocof_uplift_makes_a_relay_trip_that_the_coi_value_would_not()
    {
        // Event RoCoF ~0.625 Hz/s (1000 MW on 40 GVA.s); threshold 0.8 Hz/s. At the COI value the relay
        // rides through; with the grounded sub-cycle uplift (~1.16x, here 1.5x for headroom) the LOCAL
        // RoCoF the relay actually sees (0.94 Hz/s) exceeds the threshold and it trips.
        AssetFrequencyResult coi = RunUplift(rocofTripHzPerS: 0.8, uplift: 1.0);
        AssetFrequencyResult local = RunUplift(rocofTripHzPerS: 0.8, uplift: 1.5);

        Assert.Equal(0, coi.TrippedCount);
        Assert.True(local.TrippedCount > 0, "the uplifted local RoCoF should trip the relay");
        Assert.True(local.Response.NadirHz < coi.Response.NadirHz);
    }

    private static AssetFrequencyResult RunUplift(double rocofTripHzPerS, double uplift)
    {
        AssetClass cls = AssetTestHelpers.GflClass("dg", rocofTripHzPerS: rocofTripHzPerS,
            headroomUpFraction: 0.0, headroomDownFraction: 0.0, localRocofUpliftFactor: uplift);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", "dg"));
        GridModel m = AssetTestHelpers.Model(bundle, new Generator(1, Pg: 1500, MBase: 1500, H: 0, Type: "storage"));
        return AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, Cfg);
    }
}
