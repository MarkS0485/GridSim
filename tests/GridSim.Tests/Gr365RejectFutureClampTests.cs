using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-365: ForwardInferenceGuard.RejectFuture took the caller's "now" as given, so a "now" later than the real clock
/// moved the horizon forward and admitted recent data; only callers that first ran RequireNotAfterRealClock were safe.
/// A supplied now later than the real clock is now clamped to the real clock. The explicit DateTime.MaxValue deferral
/// (the live RTU path, re-checked by the consuming engine) is kept, and a now at or before the real clock is used as
/// given.
/// </summary>
public class Gr365RejectFutureClampTests
{
    [Fact]
    public void Gr365_a_now_later_than_the_real_clock_cannot_admit_recent_data()
    {
        DateTime recent = DateTime.UtcNow.AddHours(-1);   // inside the horizon by the real clock
        DateTime farFuture = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Against a 2100 "now" this stamp is 74 years old; clamped to the real clock it is one hour old and refused.
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RejectFuture(recent, farFuture, "test"));
        Assert.Throws<ForwardInferenceException>(() =>
            ForwardInferenceGuard.AssertHistorical(new[] { recent }, farFuture, "series"));
    }

    [Fact]
    public void Gr365_the_horizon_predicates_clamp_the_same_way_as_the_guard()
    {
        DateTime recent = DateTime.UtcNow.AddHours(-1);
        DateTime farFuture = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(GridSim.Core.Synchrophasor.PmuLiveQuarantine.IsNewerThanHorizon(recent, farFuture));
        Assert.Throws<RestrictionViolationException>(() => RestrictionReview.AssertOutputHistorical(recent, farFuture, "test"));
        Assert.True(ForwardInferenceGuard.EffectiveNow(farFuture) <= DateTime.UtcNow);
        var past = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(past, ForwardInferenceGuard.EffectiveNow(past));
        Assert.Equal(DateTime.MaxValue, ForwardInferenceGuard.EffectiveNow(DateTime.MaxValue));
    }

    [Fact]
    public void Gr365_the_max_value_deferral_is_kept()
    {
        // The live RTU path's explicit deferral: the check is handed to the consuming engine.
        ForwardInferenceGuard.RejectFuture(DateTime.UtcNow.AddHours(-1), DateTime.MaxValue, "deferred");
    }

    [Fact]
    public void Gr365_a_now_before_the_real_clock_is_used_as_given()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        // On the boundary of that earlier now: passes; one tick inside: refused (as ForwardInferenceGuardTests pins).
        DateTime exactly = now.AddHours(-ForwardInferenceGuard.HorizonHours);
        ForwardInferenceGuard.RejectFuture(exactly, now);
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RejectFuture(exactly.AddTicks(1), now));
    }
}
