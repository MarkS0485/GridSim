using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R044: LiveSim restored only its own manual trip deficit (300 s secondary restoration); a trip from the asset fleet is a
/// cumulative deficit the asset-aware integrator subtracted every sub-step for good, and an LFDD shed stayed applied, so
/// after one Loss-of-Mains cascade the live frequency sat off nominal for the rest of the session. The integrator now
/// restores the fleet's standing deficit (trips net of held protection blocks) through the same first-order secondary
/// restoration when asked; LiveSim asks, with the same 300 s time constant. The default (none) is the old integrator.
/// </summary>
public sealed partial class B2LiveLoopTests
{
    private const double RestoreTauS = 300.0;

    /// <summary>A fleet of one storage unit (500 MW dispatched) that trips on RoCoF over 0.05 Hz/s.</summary>
    private static AssetDynamicsFleet TrippingFleet() =>
        AssetDynamicsFleet.Build(OneGflModel(AssetTestHelpers.GflClass("lom", rocofTripHzPerS: 0.05), pgMw: 500, ratedMw: 1000), 50.0);

    /// <summary>A fleet of one placed LFDD relay: sheds a 400 MW block below 49.8 Hz, no dwell.</summary>
    private static AssetDynamicsFleet LfddFleet()
    {
        var lfdd = new AssetClass("lfdd", AssetKind.Protection, "protection",
            new Dictionary<string, double> { [AssetParams.LfddStageHz] = 49.8, [AssetParams.LfddBlockMw] = 400.0, [AssetParams.LfddDwellS] = 0.0 },
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test LFDD block [VERIFY]") });
        GridModel one = AssetTestHelpers.Model(null);
        var m = new GridModel
        {
            BaseMva = one.BaseMva,
            Buses = one.Buses,
            Generators = one.Generators,
            Branches = one.Branches,
            Protections = new ProtectionModel(new AssetClassLibrary(new[] { lfdd }), new[] { new ProtectionInstance("lfdd1", "lfdd", MonitorBus: 1, CapacityMw: 400) }),
        };
        return AssetDynamicsFleet.Build(m, 50.0);
    }

    /// <summary>Drive an integrator through a loss of <paramref name="lossMw"/> held until the fleet has reacted (a trip
    /// or a shed), then with no external event for <paramref name="afterS"/> seconds at 1 s steps (the manual deficit
    /// imagined restored at once, so only the fleet's standing change remains). Returns the final frequency.</summary>
    private static double LossThenQuiet(AssetAwareFrequencyIntegrator integ, double lossMw, double afterS)
    {
        // "Reacted": a trip booked (the inverter fleet) or a block held shed (the protection fleet). Not the inverter's
        // fast response, which starts as soon as the frequency leaves its deadband.
        bool Reacted() => integ.Fleet.InverterTrippedMw != 0.0 || integ.Fleet.Assets.Any(a => a.Kind == AssetKind.Protection && a.CurrentMw != 0.0);
        for (int i = 0; i < 1000 && !Reacted(); i++) integ.Step(0.02, -lossMw);
        Assert.True(Reacted(), "the fleet must react to the loss");
        for (int i = 0; i < (int)afterS; i++) integ.Step(1.0, 0.0);
        return integ.FrequencyHz;
    }

    [Fact]
    public void R044_without_restoration_an_asset_trip_holds_the_frequency_off_nominal_for_good()
    {
        // The pre-B2 live behaviour, kept as the integrator's default: the documented defect, pinned so the contrast with
        // the restored run below is explicit.
        var integ = new AssetAwareFrequencyIntegrator(40_000, AssetTestHelpers.BaseDemandMw, TrippingFleet());
        double f = LossThenQuiet(integ, 1320, afterS: 3600);
        Assert.Equal(500.0, integ.TrippedDeficitMw);
        Assert.True(f < 49.99, $"the 500 MW tripped infeed is never restored (f = {f:R} Hz after an hour)");
        Assert.Equal(0.0, integ.RestoredMw);
    }

    [Fact]
    public void R044_with_restoration_an_asset_trip_is_redispatched_over_the_time_constant()
    {
        var integ = new AssetAwareFrequencyIntegrator(40_000, AssetTestHelpers.BaseDemandMw, TrippingFleet(),
            secondaryRestorationTauS: RestoreTauS);
        // Moved 2026-10 (C1, final batch; B2 R044, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the test's
        // own arithmetic. A first-order lag with tau 300 s leaves 500 e^-12 = 0.00307 MW after 3600 s (12 tau), above the
        // 1e-3 MW tolerance: measured RestoredMw 499.99692809862296 = 500 (1 - e^-12) exactly. Run 15 tau instead
        // (500 e^-15 = 1.5e-4 MW, inside 1e-3). Before -> after: afterS 3600 -> 4500; tolerances unchanged.
        double f = LossThenQuiet(integ, 1320, afterS: 4500);   // 15 time constants
        Assert.Equal(500.0, integ.TrippedDeficitMw);           // the units stay tripped
        Assert.InRange(integ.RestoredMw, 500.0 - 1e-3, 500.0 + 1e-3);
        Assert.True(Math.Abs(f - 50.0) < 1e-3, $"the balance is restored and the frequency returns to nominal (f = {f:R} Hz)");
    }

    [Fact]
    public void R044_with_restoration_an_lfdd_shed_is_redispatched_down_too()
    {
        var off = new AssetAwareFrequencyIntegrator(40_000, AssetTestHelpers.BaseDemandMw, LfddFleet());
        var on = new AssetAwareFrequencyIntegrator(40_000, AssetTestHelpers.BaseDemandMw, LfddFleet(), secondaryRestorationTauS: RestoreTauS);
        // Moved 2026-10 (C1, final batch; B2 R044, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): as above,
        // 400 e^-12 = 0.002458 MW after 12 tau (measured -399.99754247889985); 15 tau gives 400 e^-15 = 1.2e-4 MW. Before ->
        // after: afterS 3600 -> 4500 for both runs (the run without restoration holds its shed block either way).
        double fOff = LossThenQuiet(off, 3000, afterS: 4500), fOn = LossThenQuiet(on, 3000, afterS: 4500);
        Assert.Equal(400.0, off.NetFleetMw);    // the block stays shed (no auto-restore set)
        Assert.Equal(400.0, on.NetFleetMw);
        Assert.True(fOff > 50.01, $"without restoration the shed block holds the frequency high (f = {fOff:R} Hz)");
        Assert.InRange(on.RestoredMw, -400.0 - 1e-3, -400.0 + 1e-3);   // generation redispatched down by the shed block
        Assert.True(Math.Abs(fOn - 50.0) < 1e-3, $"with restoration the frequency returns to nominal (f = {fOn:R} Hz)");
    }

    [Fact]
    public void R044_restoration_changes_nothing_until_something_trips_or_is_shed()
    {
        // Same fleet, restoration on and off, an event that stays well short of the 0.05 Hz/s trip: bit-identical.
        var off = new AssetAwareFrequencyIntegrator(400_000, AssetTestHelpers.BaseDemandMw, TrippingFleet());
        var on = new AssetAwareFrequencyIntegrator(400_000, AssetTestHelpers.BaseDemandMw, TrippingFleet(), secondaryRestorationTauS: RestoreTauS);
        for (int i = 0; i < 3000; i++)
        {
            double ev = i < 1500 ? -100.0 : 50.0;
            double dt = i % 2 == 0 ? 0.02 : 0.5;
            off.Step(dt, ev);
            on.Step(dt, ev);
            AssertBits(off.FrequencyHz, on.FrequencyHz, $"step {i} f");
            AssertBits(off.RocofHzPerS, on.RocofHzPerS, $"step {i} RoCoF");
        }
        Assert.Equal(0.0, on.TrippedDeficitMw);
        AssertBits(0.0, on.RestoredMw, "restored");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-300.0)]
    public void R044_a_restoration_time_constant_that_is_not_positive_is_refused(double tau)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AssetAwareFrequencyIntegrator(40_000, AssetTestHelpers.BaseDemandMw, TrippingFleet(), secondaryRestorationTauS: tau));
        Assert.Equal("secondaryRestorationTauS", ex.ParamName);
    }

    [Fact]
    public void R044_the_fleet_reports_the_protection_injection_separately_and_the_net_sum_unchanged()
    {
        AssetDynamicsFleet fleet = LfddFleet();
        fleet.Reset();
        AssetFleetStep above = fleet.StepCoi(49.9, 0.0, 0.0, 0.02, 0.0);
        Assert.Equal(0.0, above.ProtectionInjectionMw);
        AssetFleetStep below = fleet.StepCoi(49.7, 0.0, 0.0, 0.02, 0.02);
        Assert.Equal(400.0, below.NetActivePowerMw);
        Assert.Equal(400.0, below.ProtectionInjectionMw);
    }

    [Fact]
    public void R044_LiveSim_restores_an_asset_trip_like_a_manual_trip()
    {
        GridModel m = TwoBusLiveModel(TrippingGfl(), storagePgMw: 100);
        var sim = new LiveSim(m, holdSteady: true, dynamicsConfig: LiveSim.AuthoredBundleConfig(m));
        sim.Step(1, 1.0 / 60.0);
        sim.TripInfeed(100);   // RoCoF about 50 / (2 x 40 000) x 100 = 0.0625 Hz/s, over the 0.05 Hz/s setting
        SimFrame f = sim.Step(2, 1.0 / 60.0);
        for (int t = 3; t <= 30 && f.AssetTrippedMw == 0.0; t++) f = sim.Step(t, 1.0 / 60.0);
        Assert.Equal(100.0, f.AssetTrippedMw);   // hold-steady: the base dispatch
        for (int t = 31; t <= 130; t++) f = sim.Step(t, 1.0);   // 100 minutes, 20 time constants
        Assert.Equal(100.0, f.AssetTrippedMw);                   // still tripped
        Assert.InRange(sim.AssetRestoredMw, 100.0 - 1e-6, 100.0 + 1e-6);
        // Recovered to nominal up to the ambient load jitter (+/-0.4 MW on a 500 MW case, a few hundredths of a hertz at a
        // settled 1-minute step); without restoration the 100 MW would hold it several hertz low.
        Assert.True(Math.Abs(f.FrequencyHz - 50.0) < 0.05, $"the live frequency recovers (f = {f.FrequencyHz:R} Hz)");
    }
}
