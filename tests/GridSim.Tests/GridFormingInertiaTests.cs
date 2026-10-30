using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Grid-forming synthetic inertia is a POWER injection proportional to df/dt: it lowers the initial
/// RoCoF WITHOUT changing E - the codebase's non-negotiable separation of real (1/2)Jw^2 energy from
/// df/dt emulation. Same inertia in, lower RoCoF out.
/// </summary>
public class GridFormingInertiaTests
{
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };
    private static readonly DynamicsConfig Cfg = new() { EnableAssetDynamics = true };

    [Fact]
    public void Synthetic_inertia_lowers_initial_rocof_without_touching_E()
    {
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-bess", synInertiaMwsPerHz: 800.0);
        var storage = new Generator(1, Pg: 0, MBase: 2000, H: 0, Type: "storage");

        // Baseline: same generators (so identical E), but the fleet binds nothing.
        GridModel baseModel = AssetTestHelpers.Model(
            AssetTestHelpers.Bundle(new[] { gfm }, new AssetBinding("none", "gfm-bess")), storage);
        GridModel gfmModel = AssetTestHelpers.Model(
            AssetTestHelpers.Bundle(new[] { gfm }, new AssetBinding("storage", "gfm-bess")), storage);

        // E is unchanged - the GFM asset carries H = 0.
        Assert.Equal(baseModel.SystemInertiaGvaS, gfmModel.SystemInertiaGvaS);

        AssetFrequencyResult baseline = AssetFrequencyDynamics.Simulate(baseModel, AssetTestHelpers.BaseDemandMw, Loss, Cfg);
        AssetFrequencyResult withGfm = AssetFrequencyDynamics.Simulate(gfmModel, AssetTestHelpers.BaseDemandMw, Loss, Cfg);

        Assert.Equal(0, baseline.TrippedCount);
        Assert.True(System.Math.Abs(withGfm.Response.InitialRocofHzPerS) < System.Math.Abs(baseline.Response.InitialRocofHzPerS),
            $"GFM peak RoCoF {withGfm.Response.InitialRocofHzPerS} should be smaller in magnitude than baseline {baseline.Response.InitialRocofHzPerS}");
    }
}
