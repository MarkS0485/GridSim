using GridSim.Core.Presentation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N144 (October 2026): the WPF MARKET sparkline added a sample on every tick (the N037 defect the web had). The
/// one-sample-per-settlement-period rule now lives in Core (<see cref="ImbalancePriceTrace"/>), the web's
/// <c>TabProjections</c> wraps it, and the WPF <c>MarketVm</c> calls it with the bridge tick's instant.
/// </summary>
public class ImbalancePriceTraceTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_whole_settlement_period_of_one_second_ticks_is_one_sample()
    {
        // The 1 s raw replay: 1,800 ticks inside one half-hour used to give 1,800 samples (capped at 96).
        var h = new Queue<double>();
        long? period = null;
        for (int s = 0; s < 1800; s++) ImbalancePriceTrace.Append(h, 55.0 + s * 1e-3, T0.AddSeconds(s), ref period);
        Assert.Single(h);
        Assert.Equal(55.0 + 1799 * 1e-3, h.Single());   // the latest price of the period
        ImbalancePriceTrace.Append(h, 60.0, T0.AddMinutes(30), ref period);
        Assert.Equal(new[] { 55.0 + 1799 * 1e-3, 60.0 }, h.ToArray());
    }

    [Fact]
    public void Going_back_in_time_starts_a_new_run_and_non_finite_prices_add_nothing()
    {
        var h = new Queue<double>();
        long? period = null;
        ImbalancePriceTrace.Append(h, 50.0, T0, ref period);
        ImbalancePriceTrace.Append(h, 51.0, T0.AddMinutes(30), ref period);
        Assert.False(ImbalancePriceTrace.Append(h, double.NaN, T0.AddMinutes(60), ref period));
        Assert.False(ImbalancePriceTrace.Append(h, null, T0.AddMinutes(60), ref period));
        Assert.Equal(2, h.Count);
        ImbalancePriceTrace.Append(h, 40.0, T0.AddMinutes(-30), ref period);   // a seek backwards
        Assert.Equal(new[] { 40.0 }, h.ToArray());
        Assert.Throws<ArgumentException>(() => ImbalancePriceTrace.SettlementPeriodKey(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void The_window_holds_96_periods()
    {
        var h = new Queue<double>();
        long? period = null;
        for (int k = 0; k < 200; k++) ImbalancePriceTrace.Append(h, k, T0.AddMinutes(30 * k), ref period);
        Assert.Equal(ImbalancePriceTrace.HistoryLength, h.Count);
        Assert.Equal(200 - 96, h.Peek());
    }

    [Fact]
    public void The_web_wrappers_are_the_core_rule()
    {
        Assert.Equal(ImbalancePriceTrace.HistoryLength, TabProjections.ImbalancePriceHistoryLength);
        Assert.Equal(ImbalancePriceTrace.SettlementPeriodLength, TabProjections.SettlementPeriodLength);
        var a = new Queue<double>();
        var b = new Queue<double>();
        long? pa = null, pb = null;
        for (int s = 0; s < 5000; s += 7)
        {
            DateTime t = T0.AddSeconds(s);
            double p = 30 + (s % 97);
            Assert.Equal(ImbalancePriceTrace.Append(a, p, t, ref pa), TabProjections.AppendImbalancePrice(b, p, t, ref pb));
        }
        Assert.Equal(a.ToArray(), b.ToArray());
        Assert.Equal(pa, pb);
    }

    [Fact]
    public void The_wpf_market_view_takes_one_sample_per_period_with_the_bridge_tick()
    {
        // Source contract (the WPF project is not loaded by the test host): MarketVm calls the Core rule with the tick,
        // the dashboard passes the tick through, and the main window reads it from RawDataReceived.
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/MarketVm.cs moved to
        // src/GridSim.Ui/Tabs/Market/MarketModel.cs; assertions unchanged, and strengthened: the WPF view-model must
        // derive from that model and keep no price history of its own.
        string vm = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Market", "MarketModel.cs");
        Assert.Contains("ImbalancePriceTrace.Append(_priceHist, ip, at, ref _pricePeriod)", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("_priceHist.Add(", vm, StringComparison.Ordinal);
        string adapter = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "MarketVm.cs");
        Assert.Contains("public sealed class MarketVm : MarketModel", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("_priceHist", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ImbalancePriceTrace.Append(", adapter, StringComparison.Ordinal);
        string dash = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "DashboardViewModel.cs");
        Assert.Contains("Market.SetMarketData(mk, tickUtc);", dash, StringComparison.Ordinal);
        string win = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "MainWindow.xaml.cs");
        Assert.Contains("client.RawDataReceived += (f, mix, wx, ics, ts)", win, StringComparison.Ordinal);
        Assert.Contains("_vm.UpdateMarketData(op, mk, _bridgeTickUtc);", win, StringComparison.Ordinal);
    }
}
