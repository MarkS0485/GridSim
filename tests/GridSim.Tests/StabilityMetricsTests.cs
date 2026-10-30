using System.Collections.Generic;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The NESO RP1 situational-awareness physics: purchased time, the dynamic MIF floor, and the
/// correlated credible-loss selection. The purchased-time and floor figures are pinned to the
/// published corpus tables (MIF 0001 s.5, Policy 0021 s.6) so the panel agrees with the papers.
/// </summary>
public class StabilityMetricsTests
{
    // Policy 0021 "The 1,800 Megawatt Fiction" s.6 - purchased time to 49.2 Hz for a REAL 3,600 MW
    // Dogger Bank credible loss, across the operating points the framework contemplates.
    [Theory]
    [InlineData(720, 6.4)]   // the honest floor at 0.125 Hz/s
    [InlineData(360, 3.2)]   // the floor the framework computes FOR 1,800 MW
    [InlineData(155, 1.4)]   // GB system average, 2025
    [InlineData(102, 0.9)]   // the FRCR floor NESO is drifting toward
    [InlineData(50, 0.45)]   // a low-wind, low-demand future night
    public void Purchased_time_reproduces_the_1800_fiction_table_at_3600MW(double intrinsicGvaS, double expectedSeconds)
    {
        double t = StabilityMetrics.PurchasedTimeSeconds(intrinsicGvaS, 3600);
        Assert.Equal(expectedSeconds, t, 1);
    }

    // MIF 0001 s.5.4 - the same relationship at the (legacy) 1,800 MW design-basis loss.
    [Theory]
    [InlineData(360, 6.4)]
    [InlineData(240, 4.3)]
    [InlineData(155, 2.8)]
    [InlineData(102, 1.8)]
    public void Purchased_time_reproduces_the_MIF_table_at_1800MW(double intrinsicGvaS, double expectedSeconds)
    {
        double t = StabilityMetrics.PurchasedTimeSeconds(intrinsicGvaS, 1800);
        Assert.Equal(expectedSeconds, t, 1);
    }

    [Fact]
    public void Purchased_time_uses_the_live_frequency_margin()
    {
        // Starting already 0.2 Hz low leaves a 0.6 Hz margin to 49.2 - purchased time shrinks pro-rata.
        double full = StabilityMetrics.PurchasedTimeSeconds(360, 3600, currentHz: 50.0);   // 0.8 Hz margin -> 3.2 s
        double low = StabilityMetrics.PurchasedTimeSeconds(360, 3600, currentHz: 49.8);    // 0.6 Hz margin
        Assert.Equal(3.2, full, 1);
        Assert.Equal(full * 0.6 / 0.8, low, 3);
        Assert.Equal(0.0, StabilityMetrics.PurchasedTimeSeconds(360, 3600, currentHz: 49.2)); // at threshold
    }

    // MIF 0001 s.5.2 / Policy 0021 s.6 - the floor doubles with the loss and the S_base terms cancel.
    [Theory]
    [InlineData(3600, 0.125, 720)]
    [InlineData(1800, 0.125, 360)]
    [InlineData(3600, 1.0, 90)]     // relax the ceiling eightfold -> 90 GVA.s at the real loss
    public void Dynamic_MIF_floor_matches_the_swing_equation(double lossMw, double rocofLimit, double expectedGvaS)
    {
        Assert.Equal(expectedGvaS, StabilityMetrics.DynamicMifGvaS(lossMw, rocofLimit), 1);
    }

    [Fact]
    public void Rocof_for_a_loss_is_the_swing_equation()
    {
        // 360 GVA.s against 3,600 MW -> 0.25 Hz/s (double the 0.125 ceiling), per Policy 0021.
        Assert.Equal(0.25, StabilityMetrics.RocofFor(360, 3600), 4);
    }

    [Fact]
    public void Largest_unit_picks_the_biggest_online_infeed()
    {
        Assert.Equal(1200, StabilityMetrics.LargestUnitMw(new[] { 660.0, 1200.0, 0.0, 810.0 }));
        Assert.Equal(0.0, StabilityMetrics.LargestUnitMw(System.Array.Empty<double>()));
    }

    [Fact]
    public void Correlated_group_sums_only_importing_members_and_picks_the_worst()
    {
        var groups = new (string, IReadOnlyList<string>)[]
        {
            ("Sellindge (IFA+ElecLink)", new[] { "IFA", "ElecLink" }),
            ("North Sea (NSL+Viking)", new[] { "NSL", "Viking" }),
        };
        var flows = new Dictionary<string, double>
        {
            ["IFA"] = 1900, ["ElecLink"] = 900,   // both importing -> 2800 MW correlated loss
            ["NSL"] = 1400, ["Viking"] = -200,    // Viking exporting -> not an infeed loss
        };
        var worst = StabilityMetrics.LargestCorrelatedGroup(groups, flows);
        Assert.Equal("Sellindge (IFA+ElecLink)", worst.Name);
        Assert.Equal(2800, worst.Mw, 1);
    }
}
