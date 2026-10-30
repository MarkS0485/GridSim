using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The load-bearing invariant: GridSim is a historical state estimator and refuses to process any timestamp
/// newer than <see cref="ForwardInferenceGuard.HorizonHours"/> behind real time. A stamp inside the horizon
/// throws; a stamp beyond it passes. This is what makes forward inference structurally impossible.
/// </summary>
public class ForwardInferenceGuardTests
{
    // A fixed "now" so the test is deterministic regardless of wall-clock.
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_recent_stamp_inside_the_horizon_is_rejected()
    {
        DateTime oneHourAgo = Now.AddHours(-1);
        var ex = Assert.Throws<ForwardInferenceException>(
            () => ForwardInferenceGuard.RejectFuture(oneHourAgo, Now, "test"));
        Assert.Contains("historical horizon", ex.Message);
    }

    [Fact]
    public void A_future_stamp_is_rejected()
    {
        DateTime tomorrow = Now.AddHours(+24);
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RejectFuture(tomorrow, Now));
    }

    [Fact]
    public void A_stamp_beyond_the_horizon_passes()
    {
        DateTime pastTheHorizon = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 1));
        ForwardInferenceGuard.RejectFuture(pastTheHorizon, Now); // must not throw
    }

    [Fact]
    public void The_horizon_boundary_is_exactly_HorizonHours()
    {
        // Just inside (24h59m) throws; just past (25h01m) passes - the const is the only knob.
        Assert.Throws<ForwardInferenceException>(
            () => ForwardInferenceGuard.RejectFuture(Now.AddHours(-ForwardInferenceGuard.HorizonHours).AddMinutes(1), Now));
        ForwardInferenceGuard.RejectFuture(Now.AddHours(-ForwardInferenceGuard.HorizonHours).AddMinutes(-1), Now);
    }

    [Fact]
    public void AssertHistorical_throws_on_the_first_future_row()
    {
        var series = new List<DateTime> { Now.AddHours(-300), Now.AddHours(-200), Now.AddHours(-1) };
        Assert.Throws<ForwardInferenceException>(
            () => ForwardInferenceGuard.AssertHistorical(series, Now, "series"));
    }

    [Fact]
    public void AssertHistorical_passes_an_all_historical_series()
    {
        var series = new List<DateTime> { Now.AddHours(-300), Now.AddHours(-250), Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 1)) };
        ForwardInferenceGuard.AssertHistorical(series, Now); // must not throw
    }

    [Fact]
    public void Unspecified_kind_is_treated_as_utc()
    {
        // A naive timestamp (no zone) beyond the horizon must still pass, not be shifted into it.
        var naive = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Unspecified); // ~11 days before Now
        ForwardInferenceGuard.RejectFuture(naive, Now);
    }

    [Fact]
    public void The_exact_horizon_instant_passes_and_one_tick_inside_throws()
    {
        // The comparison is strict (stamp > horizon), so a stamp landing EXACTLY on the horizon is historical
        // and must pass; one tick newer is inside and must throw. Pins the boundary the +/-1-minute tests skip over.
        DateTime exactly = Now.AddHours(-ForwardInferenceGuard.HorizonHours);
        ForwardInferenceGuard.RejectFuture(exactly, Now);                 // on the boundary => passes
        Assert.Throws<ForwardInferenceException>(
            () => ForwardInferenceGuard.RejectFuture(exactly.AddTicks(1), Now));   // one tick inside => throws
    }

    [Fact]
    public void A_local_kind_stamp_is_compared_by_true_instant_not_naive_ticks()
    {
        // A Local-zone stamp is converted to UTC (ToUniversalTime), so it's judged by the real instant it names,
        // regardless of the machine's time zone. A Local stamp for the same instant as Now is "now" => rejected;
        // one for 30 h ago => passes. (Round-tripping Now through Local then back to UTC is offset-independent.)
        DateTime localNow = Now.ToLocalTime();                  // same instant, DateTimeKind.Local
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RejectFuture(localNow, Now));
        DateTime localPast = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5)).ToLocalTime();
        ForwardInferenceGuard.RejectFuture(localPast, Now);     // 30 h ago => passes
    }
}
