using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R043: the asset fleet is built once from the base model, so a Loss-of-Mains trip booked the base-case Pg while the
/// live loop runs the load-scaled dispatch (GridScenario.AtLoad, factor 0.60 to 1.00). Every bound unit now books the
/// output its generator is dispatched to on the current tick (IActiveAssetDynamics.SetDispatch, fed by
/// AssetDynamicsFleet.SetDispatch from LiveSim).
/// </summary>
public sealed partial class B2LiveLoopTests
{
    /// <summary>A RoCoF-tripping grid-following class (0.05 Hz/s, no confirm) for the trip-size tests.</summary>
    private static AssetClass TrippingGfl() => AssetTestHelpers.GflClass("lom", rocofTripHzPerS: 0.05);

    [Fact]
    public void R043_an_inverter_books_the_dispatch_it_was_last_given()
    {
        var gfl = new GridFollowingInverter(TrippingGfl(), bus: 1, pgMw: 100, ratedMw: 400);
        Assert.Equal(100.0, gfl.DispatchMw);
        gfl.SetDispatch(60.0);
        Assert.Equal(60.0, gfl.DispatchMw);
        AssetActuation trip = gfl.Update(new AssetStepContext(49.9, -0.5, 0.0, 0.02, 0.0));
        Assert.True(trip.TrippedNow);
        Assert.Equal(60.0, trip.TrippedInfeedMw);   // was the construction-time 100 MW whatever the dispatch

        AssetClass gfmCls = new(
            "gfm-lom", AssetKind.GridFormingInverter, "grid-forming",
            new Dictionary<string, double> { [AssetParams.RocofTripHzPerS] = 0.05, [AssetParams.RocofTripConfirmS] = 0.0 },
            new Dictionary<string, AssetClassProvenance>());
        var gfm = new GridFormingInverter(gfmCls, bus: 1, pgMw: 100, ratedMw: 400);
        gfm.SetDispatch(-25.0);   // a storage unit charging: losing it is a surplus
        AssetActuation gfmTrip = gfm.Update(new AssetStepContext(49.9, -0.5, 0.0, 0.02, 0.0));
        Assert.True(gfmTrip.TrippedNow);
        Assert.Equal(-25.0, gfmTrip.TrippedInfeedMw);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void R043_a_non_finite_dispatch_is_refused(double pg)
    {
        var gfl = new GridFollowingInverter(TrippingGfl(), bus: 1, pgMw: 100, ratedMw: 400);
        Assert.Equal("pgMw", Assert.Throws<ArgumentOutOfRangeException>(() => gfl.SetDispatch(pg)).ParamName);
        Assert.Equal(100.0, gfl.DispatchMw);
    }

    [Fact]
    public void R043_the_fleet_takes_the_dispatch_of_an_operating_point_and_books_it_on_a_trip()
    {
        GridModel m = OneGflModel(TrippingGfl(), pgMw: 100, ratedMw: 400);
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);
        Assert.Equal(new[] { 100.0 }, fleet.DispatchMw);

        // The operating point at 60 % load: every non-slack Pg scaled (here by hand, as GridScenario.AtLoad does).
        GridModel scaled = m.CopyWith(generators: m.Generators.Select(g => g.Type == "storage" ? g with { Pg = g.Pg * 0.6 } : g).ToList());
        fleet.SetDispatch(scaled);
        Assert.Equal(new[] { 100.0 * 0.6 }, fleet.DispatchMw);

        AssetFleetStep step = fleet.StepCoi(49.9, -0.5, 0.0, 0.02, 0.0);
        Assert.Equal(100.0 * 0.6, step.TrippedDeficitMw);    // was 100 MW, 1 / 0.6 = 1.67 times what was produced
        Assert.Equal(100.0 * 0.6, fleet.InverterTrippedMw);
    }

    [Fact]
    public void R043_the_fleet_refuses_an_operating_point_of_another_model()
    {
        GridModel m = OneGflModel(TrippingGfl(), pgMw: 100, ratedMw: 400);
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);
        GridModel extra = m.CopyWith(generators: m.Generators.Append(new Generator(1, Pg: 5, Type: "ccgt")).ToList());
        Assert.Throws<ArgumentException>(() => fleet.SetDispatch(extra));
        GridModel moved = m.CopyWith(generators: m.Generators.Select(g => g.Type == "storage" ? g with { Bus = 7 } : g).ToList());
        Assert.Throws<ArgumentException>(() => fleet.SetDispatch(moved));
        Assert.Equal(new[] { 100.0 }, fleet.DispatchMw);   // nothing applied
    }

    [Fact]
    public void R043_the_same_dispatch_changes_nothing()
    {
        // The hold-steady path and every batch study: the dispatch handed over is the base one, so every unit keeps the
        // value it had and a whole run is bit-identical with or without the call.
        GridModel m = OneGflModel(TrippingGfl(), pgMw: 100, ratedMw: 400);
        AssetDynamicsFleet a = AssetDynamicsFleet.Build(m, 50.0), b = AssetDynamicsFleet.Build(m, 50.0);
        for (int i = 0; i < 300; i++)
        {
            b.SetDispatch(m);
            double hz = 50.0 - 0.001 * i, rocof = -0.001 * i;
            AssetFleetStep sa = a.StepCoi(hz, rocof, 0.0, 0.02, i * 0.02), sb = b.StepCoi(hz, rocof, 0.0, 0.02, i * 0.02);
            AssertBits(sa.NetActivePowerMw, sb.NetActivePowerMw, $"step {i} net");
            AssertBits(sa.TrippedDeficitMw, sb.TrippedDeficitMw, $"step {i} tripped");
        }
        Assert.True(a.InverterTrippedMw > 0.0);   // the ramp did reach the trip
    }

    /// <summary>Two buses (a synchronous slack and a load bus hosting a bound storage unit), a GB-like 50 Hz case the
    /// live loop scales to its daily curve.</summary>
    private static GridModel TwoBusLiveModel(AssetClass cls, double storagePgMw)
    {
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", cls.Name));
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "A"), new(2, BusType.Pq, Pd: 500, Qd: 50, BaseKv: 400, Name: "B") },
            Branches = new List<Branch> { new(1, 2, 0.001, 0.01) },
            Generators = new List<Generator>
            {
                new(1, Pg: 400, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),
                new(2, Pg: storagePgMw, MBase: 400, H: 0, Name: "bess", Type: "storage"),
            },
            AssetDynamics = bundle,
        };
    }

    [Fact]
    public void R043_a_live_trip_on_the_load_scaled_path_books_the_scaled_dispatch()
    {
        GridModel m = TwoBusLiveModel(TrippingGfl(), storagePgMw: 100);
        var sim = new LiveSim(m, holdSteady: false, dynamicsConfig: LiveSim.AuthoredBundleConfig(m));
        sim.Step(1, 1.0 / 60.0);
        sim.TripInfeed(300);   // RoCoF about 50 / (2 x 40 000) x 300 = 0.19 Hz/s, over the 0.05 Hz/s LoM setting
        SimFrame? tripFrame = null;
        for (int t = 2; t <= 20 && tripFrame is null; t++)
        {
            SimFrame f = sim.Step(t, 1.0 / 60.0);
            if (f.AssetTrippedMw != 0.0) tripFrame = f;
        }
        Assert.NotNull(tripFrame);
        double factor = LoadProfile.DailyFactor(tripFrame!.Hour);
        double expected = GridScenario.AtLoad(m, factor).Generators[1].Pg;   // this tick's dispatch of the bess
        Assert.NotEqual(100.0, expected);                                    // the curve is not at 1.0 at 08:00
        Assert.Equal(expected, tripFrame.AssetTrippedMw);
    }
}
