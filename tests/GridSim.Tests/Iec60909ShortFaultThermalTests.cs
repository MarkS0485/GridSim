using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The breaker short-time withstand is referred to the fault duration on an equal-energy basis only when the fault
/// lasts at least the rated duration (IEC 60865-1 clause 7): for a shorter fault the criterion is Ith &lt;= I_thr, the
/// rating is never raised. Before, a 40 kA / 3 s rating was compared at a 0.5 s fault with 40 x sqrt(6) = 98 kA.
/// </summary>
public class Iec60909ShortFaultThermalTests
{
    private const double Kv = 132.0, BaseMva = 100.0;

    [Fact]
    public void A_fault_shorter_than_the_rated_duration_is_checked_against_the_unscaled_rating()
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva,
            f0Hz: 50.0, faultDurationSeconds: 0.5);
        Assert.Equal(0.5, d.FaultDurationSeconds);

        const double iThr = 40.0;
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: iThr, ShortTimeSeconds: 3.0);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, rating, 132.0);

        // Exactly the rating minus the duty: sqrt(min(1, 6)) = sqrt(1) = 1, so no scaling at all.
        Assert.Equal(iThr - d.ThermalIthKa, a.ThermalMarginKa);
    }

    [Fact]
    public void A_short_fault_with_a_duty_above_the_rating_is_not_adequate()
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva,
            f0Hz: 50.0, faultDurationSeconds: 0.5);

        // A rating just below the duty: the old upward referral (x sqrt(6)) would have passed it with a large margin.
        double iThr = d.ThermalIthKa * 0.9;
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: iThr, ShortTimeSeconds: 3.0);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, rating, 132.0);

        Assert.False(a.ThermalOk);
        Assert.False(a.Adequate);
        Assert.True(a.ThermalMarginKa < 0.0);
    }

    [Fact]
    public void Equal_durations_need_no_scaling()
    {
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva,
            f0Hz: 50.0, faultDurationSeconds: 1.0);
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: 30.0, ShortTimeSeconds: 1.0);
        Assert.Equal(30.0 - d.ThermalIthKa, Iec60909.CheckBreaker(d, rating, 132.0).ThermalMarginKa);
    }
}
