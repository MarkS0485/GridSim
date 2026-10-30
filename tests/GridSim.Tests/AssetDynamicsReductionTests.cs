using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Fleet;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The reduction gate for the authored per-asset dynamics fleet: with nothing active bound, the
/// trajectory is bit-for-bit the existing lumped baseline (the <c>pAssetFleet</c> term is appended
/// last as <c>0.0</c>). Plus the "generalises, not forks" check: one pure-FFR grid-following unit
/// reproduces a <see cref="FleetUnit"/> device.
/// </summary>
public class AssetDynamicsReductionTests
{
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };

    private static FrequencyResponse Baseline(GridModel m, DynamicsConfig cfg)
        => FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, AssetTestHelpers.BaseDemandMw, Loss, cfg);

    [Fact]
    public void Flag_off_is_bit_identical_to_the_lumped_baseline()
    {
        // A bundle IS present and would bind, but the flag is OFF => the fleet is never built.
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { AssetTestHelpers.GflClass("bess") },
            new AssetBinding("storage", "bess"));
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: 500, MBase: 500, H: 0, Type: "storage"));
        var cfg = new DynamicsConfig { EnableAssetDynamics = false };

        FrequencyResponse baseline = Baseline(m, cfg);
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, cfg);

        Assert.True(baseline.Points.SequenceEqual(asset.Response.Points), "flag-off trajectory must be identical");
        Assert.Equal(0, asset.TrippedCount);
    }

    [Fact] // Wave 3: the FORMING branch must also reduce bit-for-bit (it now sub-steps like the baseline)
    public void Forming_flag_reduces_to_the_lumped_forming_baseline()
    {
        GridModel m = AssetTestHelpers.Model(assetDynamics: null);   // empty fleet
        var cfg = new DynamicsConfig
        {
            EnableAssetDynamics = true,
            Forming = FormingMode.Vsm,
            VirtualInertiaMwS = 30_000,
            FormingDampingMwPerHz = 500,     // Dv > 0 exercises both the equilibrium and sub-stepping fixes
            DampingPctPerHz = 1.0,
            ResponsiveFraction = 0.2,
            FastResponseMw = 500,
            DurationSeconds = 30.0,
            StepSeconds = 0.02,
        };

        FrequencyResponse baseline = Baseline(m, cfg);
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, cfg);

        Assert.True(baseline.Points.SequenceEqual(asset.Response.Points),
            "forming empty-fleet trajectory must be bit-identical to the lumped forming baseline");
    }

    [Fact]
    public void Enabled_but_nothing_bound_reduces_to_baseline()
    {
        // Flag ON, no bundle at all => empty fleet => identical.
        GridModel m = AssetTestHelpers.Model(assetDynamics: null);
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };

        FrequencyResponse baseline = Baseline(m, cfg);
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, cfg);

        Assert.True(baseline.Points.SequenceEqual(asset.Response.Points));
    }

    [Fact]
    public void Enabled_with_only_unmatched_assets_reduces_to_baseline()
    {
        // Flag ON, a bundle, but the rule matches no asset technology present => empty active fleet => identical.
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { AssetTestHelpers.GflClass("bess") },
            new AssetBinding("nonexistent-tech", "bess"));
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: 500, MBase: 500, H: 0, Type: "storage"));
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };

        FrequencyResponse baseline = Baseline(m, cfg);
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, cfg);

        Assert.True(baseline.Points.SequenceEqual(asset.Response.Points));
    }

    [Fact]
    public void Single_zero_latency_grid_following_unit_reproduces_a_fleet_unit()
    {
        // Pure-FFR regime: no latency, no df/dt leg, symmetric headroom, SoC/ramp/trip disabled,
        // full delivery at 0.5 Hz (droopGain = 2*headroom). One 200 MVA unit => 100 MW FFR cap.
        const double ffrMw = 100.0, ratedMw = 200.0, headroom = ffrMw / ratedMw; // 0.5
        AssetClass cls = AssetTestHelpers.GflClass("ffr",
            headroomUpFraction: headroom, headroomDownFraction: headroom,
            droopGain: 2.0 * headroom, deadbandHz: 0.015, actuateTauS: 0.5);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { cls }, new AssetBinding("storage", "ffr"));
        GridModel m = AssetTestHelpers.Model(bundle,
            new Generator(1, Pg: 0, MBase: ratedMw, H: 0, Type: "storage"));
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };

        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, AssetTestHelpers.BaseDemandMw, Loss, cfg);

        double baseInertia = m.SystemInertiaGvaS * 1000.0;
        FleetSpec fleet = new(new[] { new FleetUnit(0.0, ffrMw, 0.5, 0.015, 0.0) });
        FleetResult fr = FleetDynamics.Simulate(baseInertia, AssetTestHelpers.BaseDemandMw, Loss, fleet);

        // Same physics up to imbalance-term association: match to a tight numeric tolerance.
        Assert.Equal(fr.Response.NadirHz, asset.Response.NadirHz, 6);
        Assert.Equal(fr.Response.InitialRocofHzPerS, asset.Response.InitialRocofHzPerS, 6);
        Assert.Equal(fr.Response.SettleHz, asset.Response.SettleHz, 6);
    }
}
