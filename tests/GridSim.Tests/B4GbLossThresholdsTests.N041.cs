using GridSim.Core.Dynamics;
using GridSim.Core.Presentation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N041: the web FREQUENCY EVENT lamp also fired at RoCoF &gt;= 0.125 Hz/s (the legacy loss-of-mains setting, replaced by
/// 1 Hz/s: EREC G99 Issue 2 Table 10.1, Ofgem's DC0079 decision and its retrospective application, the ALoMCP), and the
/// desktop lamp had no RoCoF test. Both shells now share <see cref="StatusLampRules.IsRocofEvent"/>: strictly above
/// 1 Hz/s (by more than the shared breach tolerance).
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    private static HeadlineDto N041Headline(double rocof) =>
        new(FrequencyHz: 50.0, RocofHzPerS: rocof, GenerationMw: 1000, DemandMw: 990, LossMw: 10,
            InertiaGvaS: 100, AvgVoltagePu: 1.0, Converged: true, MinVoltagePu: 1.0, MaxVoltagePu: 1.0);

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.125, false)]        // the legacy setting: an event before N041, not now
    [InlineData(-0.5, false)]
    [InlineData(1.0, false)]          // exactly the setting: a relay set to 1 Hz/s does not operate
    [InlineData(1.0 + 1e-9, false)]   // within the shared breach tolerance
    [InlineData(1.01, true)]
    [InlineData(-1.5, true)]          // magnitude
    public void N041_the_rocof_event_rule_is_the_post_alomcp_setting(double rocof, bool expected)
    {
        Assert.Equal(1.0, StatusLampRules.RocofEventHzPerS);
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, StatusLampRules.RocofEventHzPerS);
        Assert.Equal(expected, StatusLampRules.IsRocofEvent(rocof));
        Assert.Equal(expected ? "FREQUENCY EVENT" : "SYSTEM NORMAL", StatusLamp.Evaluate(N041Headline(rocof), false).Text);
    }

    [Fact]
    public void N041_a_non_finite_rocof_fails_loud()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusLampRules.IsRocofEvent(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusLampRules.IsRocofEvent(double.PositiveInfinity));
    }

    [Fact]
    public void N041_both_shells_call_the_one_rule()
    {
        Assert.Contains("StatusLampRules.IsRocofEvent(headline.RocofHzPerS)", Src("src", "GridSim.Web", "Projection", "StatusLamp.cs"));
        Assert.DoesNotContain("rocof >= FrequencyLimits.RocofLimitLegacyHzPerS", Src("src", "GridSim.Web", "Projection", "StatusLamp.cs"));
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop lamp moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertion unchanged, and strengthened: the WPF view-model must
        // derive from that model, so the WPF lamp is the one pinned here.
        Assert.Contains("StatusLampRules.IsRocofEvent(f.RocofHzPerS)", Src("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs"));
        Assert.Contains("public sealed class DashboardViewModel : DashboardModel", Src("src", "GridSim.Wpf", "DashboardViewModel.cs"));
    }
}
