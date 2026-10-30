using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R231: BatteryDcSource.Reset replaced the pack with the look-ahead clone, which has ageing tracking OFF, and both the
/// batch asset solve and the live asset-aware integrator reset the fleet before the first step, so no inverter battery
/// ever aged; the swap also left any holder of the old pack with a stale object. Reset now restores the pack in place from
/// a snapshot that kept its ageing state.
/// </summary>
public sealed partial class B2LiveLoopTests
{
    private static AssetClass ChemistryBess(double chemistryCode = 2) => new(
        "bess", AssetKind.GridFollowingInverter, "",
        new Dictionary<string, double>
        {
            [AssetParams.CapacityMwh] = 1.0,
            [AssetParams.SocInit] = 0.9,
            [AssetParams.HeadroomUpFraction] = 1.0,
            [AssetParams.HeadroomDownFraction] = 1.0,
            [AssetParams.DroopGainMwPerHzPerMw] = 5.0,
            [AssetParams.DeadbandHz] = 0.015,
            [AssetParams.ActuateTauS] = 0.1,
            [AssetParams.BatteryChemistry] = chemistryCode,
        },
        new Dictionary<string, AssetClassProvenance>());

    /// <summary>Step an inverter through a sustained 49.5 Hz event; returns each step's output (MW).</summary>
    private static List<double> Deliver(GridFollowingInverter inv, int steps, double dt = 0.1)
    {
        var trace = new List<double>(steps);
        for (int k = 0; k < steps; k++) trace.Add(inv.Update(new AssetStepContext(49.5, 0.0, 0.0, dt, k * dt)).ActivePowerMw);
        return trace;
    }

    [Fact]
    public void R231_reset_keeps_the_same_pack_object_with_ageing_on()
    {
        var inv = new GridFollowingInverter(ChemistryBess(), bus: 1, pgMw: 0, ratedMw: 2.0);
        var src = Assert.IsType<BatteryDcSource>(inv.DcSource);
        PackModel pack = src.Pack;
        Assert.NotNull(pack.Ageing);
        Deliver(inv, 300);
        Assert.True(pack.Cell.CapacityFade > 0.0, "the pack ages while it delivers");
        inv.Reset();
        Assert.Same(pack, src.Pack);                    // restored in place: holders of the pack see the reset
        Assert.NotNull(src.Pack.Ageing);                // ageing NOT switched off (it was, before R231)
        Assert.Equal(0.0, src.Pack.Cell.CapacityFade);  // fade back to the value at construction
        Assert.Equal(0.0, src.Pack.Ageing!.TotalFade);
        Assert.Equal(0.9, src.SocFraction, 12);
        Deliver(inv, 300);
        Assert.True(src.Pack.Cell.CapacityFade > 0.0, "after a reset the pack ages again");
    }

    [Fact]
    public void R231_a_reset_inverter_runs_bit_for_bit_like_a_fresh_one()
    {
        // The definition of a reset: the run after it is the run of a newly built unit. Before R231 the reset unit ran
        // without ageing and the fresh one with it, so the two drifted apart (capacity and resistance follow the fade).
        var fresh = new GridFollowingInverter(ChemistryBess(), bus: 1, pgMw: 0, ratedMw: 2.0);
        var reused = new GridFollowingInverter(ChemistryBess(), bus: 1, pgMw: 0, ratedMw: 2.0);
        Deliver(reused, 500);
        reused.Reset();
        List<double> a = Deliver(fresh, 3000), b = Deliver(reused, 3000);
        for (int i = 0; i < a.Count; i++) AssertBits(a[i], b[i], $"step {i} output");
        AssertBits(fresh.SocFraction, reused.SocFraction, "SoC");
        var fa = (BatteryDcSource)fresh.DcSource!;
        var fb = (BatteryDcSource)reused.DcSource!;
        AssertBits(fa.Pack.Cell.CapacityFade, fb.Pack.Cell.CapacityFade, "capacity fade");
    }

    [Fact]
    public void R231_the_fleet_reset_at_integrator_start_leaves_inverter_packs_ageing()
    {
        // The path R231 names: AssetAwareFrequencyIntegrator resets the fleet in its constructor.
        GridModel m = OneGflModel(ChemistryBess(), pgMw: 0, ratedMw: 2.0);
        AssetDynamicsFleet fleet = AssetDynamicsFleet.Build(m, 50.0);
        var integ = new AssetAwareFrequencyIntegrator(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, fleet);
        for (int i = 0; i < 200; i++) integ.Step(0.1, -1320.0);
        var inv = Assert.IsType<GridFollowingInverter>(fleet.Assets[0]);
        PackModel pack = ((BatteryDcSource)inv.DcSource!).Pack;
        Assert.NotNull(pack.Ageing);
        Assert.True(pack.Cell.CapacityFade > 0.0, "an inverter battery ages in the live integrator");
    }

    [Fact]
    public void R231_the_look_ahead_clone_is_unchanged_and_the_keep_ageing_clone_is_a_deep_copy()
    {
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.8);
        for (int i = 0; i < 100; i++) pack.Step(5e5, 1.0);
        double fade = pack.Ageing!.TotalFade;
        Assert.True(fade > 0.0);

        Assert.Null(pack.Clone().Ageing);                       // look-ahead copy: no ageing, as before
        Assert.Null(pack.Clone(keepAgeing: false).Ageing);
        PackModel kept = pack.Clone(keepAgeing: true);
        Assert.NotNull(kept.Ageing);
        Assert.NotSame(pack.Ageing, kept.Ageing);
        AssertBits(fade, kept.Ageing!.TotalFade, "copied fade");
        kept.Step(5e5, 10.0);                                   // the copy ages on its own
        AssertBits(fade, pack.Ageing.TotalFade, "original fade untouched by the copy");
        Assert.True(kept.Ageing.TotalFade > fade);
    }

    [Fact]
    public void R231_restore_refuses_a_snapshot_of_another_pack()
    {
        PackModel nmc = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.8);
        PackModel lfp = PackModel.Sized(BatteryChemistryLibrary.Lfp, 1e6, 1000, 0.8);
        Assert.Throws<ArgumentException>(() => nmc.RestoreFrom(lfp));
        Assert.Throws<ArgumentNullException>(() => nmc.RestoreFrom(null!));
    }
}
