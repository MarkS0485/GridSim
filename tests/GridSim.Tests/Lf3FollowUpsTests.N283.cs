using GridSim.Core.Dynamics;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N283 (LF3): tools/crossval/crossval_h0.py capped the responsive fraction on EVERY scenario corner (min(1.0, ...)),
/// while GridSim (ScenarioCorners.Apply) caps only the best corner; the export publishes each corner's cap as
/// <c>responsiveCap</c> (R178). The sidecar now reads that field. The sidecar is Python, so its use of the field is pinned
/// by source text; the C# rule it must match is pinned by value.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    [Fact]
    public void N283_crossval_h0_reads_the_per_corner_cap_instead_of_capping_every_corner()
    {
        string py = Src("tools", "crossval", "crossval_h0.py");
        Assert.Contains("def responsive_fraction(base_fraction, cf, corner):", py);
        Assert.Contains("if \"responsiveCap\" in cf:", py);
        Assert.Contains("presp = responsive_fraction(cfg[\"responsiveFraction\"], cf, corner) * demand", py);
        Assert.DoesNotContain("min(1.0, cfg[\"responsiveFraction\"] * cf[\"responsive\"])", py);
    }

    [Fact]
    public void N283_the_code_caps_only_the_best_corner()
    {
        // A base responsive fraction above 1 (allowed by the config) shows the difference the sidecar had.
        var cfg = new DynamicsConfig { ResponsiveFraction = 1.2 };
        Assert.Equal(1.2, ScenarioCorners.Apply(cfg, Corner.Nominal).ResponsiveFraction);
        Assert.Equal(ScenarioCorners.MaxResponsiveFraction, ScenarioCorners.Apply(cfg, Corner.Best).ResponsiveFraction);
        Assert.Equal(1.2 * ScenarioCorners.WorstResponsiveScale, ScenarioCorners.Apply(cfg, Corner.Worst).ResponsiveFraction);
    }
}
