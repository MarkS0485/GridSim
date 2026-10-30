using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N037: the MARKET imbalance-price sparkline takes one sample per GB settlement period (30 minutes), not one per
/// streamed tick. Before, the 1 s raw replay added the same half-hourly price up to 1,800 times per period, so the
/// 96-sample window showed 96 seconds of one flat line.
/// </summary>
public sealed class MarketSparklinePerPeriodTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Settlement_period_key_floors_to_the_utc_half_hour()
    {
        long k = TabProjections.SettlementPeriodKey(T0);
        Assert.Equal(k, TabProjections.SettlementPeriodKey(T0.AddMinutes(29).AddSeconds(59)));
        Assert.Equal(k + 1, TabProjections.SettlementPeriodKey(T0.AddMinutes(30)));
        Assert.Equal(k - 1, TabProjections.SettlementPeriodKey(T0.AddTicks(-1)));
        Assert.Equal(TimeSpan.FromMinutes(30), TabProjections.SettlementPeriodLength);
        // BST day (UK local = UTC + 1 h): local 12:30 BST is 11:30 UTC, still a period start.
        DateTime bst = new(2026, 7, 1, 11, 30, 0, DateTimeKind.Utc);
        Assert.Equal(TabProjections.SettlementPeriodKey(bst), TabProjections.SettlementPeriodKey(bst.AddMinutes(29)));
        Assert.NotEqual(TabProjections.SettlementPeriodKey(bst), TabProjections.SettlementPeriodKey(bst.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => TabProjections.SettlementPeriodKey(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void One_second_ticks_over_one_period_give_one_sample()
    {
        var q = new Queue<double>();
        long? last = null;
        for (int s = 0; s < 1800; s++)
            Assert.True(TabProjections.AppendImbalancePrice(q, 72.5, T0.AddSeconds(s), ref last));
        Assert.Equal(new[] { 72.5 }, q);
        Assert.Equal(TabProjections.SettlementPeriodKey(T0), last);

        // Next period: a second sample.
        Assert.True(TabProjections.AppendImbalancePrice(q, 80.0, T0.AddMinutes(30), ref last));
        Assert.Equal(new[] { 72.5, 80.0 }, q);
    }

    [Fact]
    public void A_price_revised_within_its_period_replaces_that_periods_sample()
    {
        var q = new Queue<double>();
        long? last = null;
        TabProjections.AppendImbalancePrice(q, 10.0, T0, ref last);
        TabProjections.AppendImbalancePrice(q, 20.0, T0.AddMinutes(30), ref last);
        TabProjections.AppendImbalancePrice(q, 25.0, T0.AddMinutes(45), ref last);   // same period, revised
        Assert.Equal(new[] { 10.0, 25.0 }, q);
    }

    [Fact]
    public void Missing_or_non_finite_prices_add_nothing_and_keep_the_period()
    {
        var q = new Queue<double>();
        long? last = null;
        Assert.False(TabProjections.AppendImbalancePrice(q, null, T0, ref last));
        Assert.False(TabProjections.AppendImbalancePrice(q, double.NaN, T0, ref last));
        Assert.False(TabProjections.AppendImbalancePrice(q, double.NegativeInfinity, T0, ref last));
        Assert.Empty(q);
        Assert.Null(last);
        Assert.True(TabProjections.AppendImbalancePrice(q, -5.0, T0, ref last));   // negative prices are real
        Assert.False(TabProjections.AppendImbalancePrice(q, null, T0.AddHours(1), ref last));
        Assert.Equal(TabProjections.SettlementPeriodKey(T0), last);
    }

    [Fact]
    public void Two_days_of_periods_fill_the_96_window_and_roll()
    {
        var q = new Queue<double>();
        long? last = null;
        for (int p = 0; p < 100; p++)
            for (int s = 0; s < 1800; s += 60)   // one tick a minute within each period
                TabProjections.AppendImbalancePrice(q, p, T0.AddMinutes(30 * p).AddSeconds(s), ref last);
        Assert.Equal(96, q.Count);
        Assert.Equal(4.0, q.First());
        Assert.Equal(99.0, q.Last());
    }

    [Fact]
    public void Going_back_in_time_starts_a_fresh_run()
    {
        var q = new Queue<double>();
        long? last = null;
        TabProjections.AppendImbalancePrice(q, 1.0, T0.AddHours(5), ref last);
        TabProjections.AppendImbalancePrice(q, 2.0, T0.AddHours(6), ref last);
        // Seek back (or a stored lake looping): the old run is not spliced onto the new one.
        TabProjections.AppendImbalancePrice(q, 3.0, T0, ref last);
        Assert.Equal(new[] { 3.0 }, q);
        Assert.Equal(TabProjections.SettlementPeriodKey(T0), last);
    }

    [Fact]
    public void The_per_sample_overload_is_unchanged()
    {
        // The original overload (kept for callers without a timestamp) still appends every finite price.
        var q = new Queue<double>();
        for (int i = 0; i < 3; i++) Assert.True(TabProjections.AppendImbalancePrice(q, 7.0));
        Assert.Equal(new[] { 7.0, 7.0, 7.0 }, q);
    }
}
