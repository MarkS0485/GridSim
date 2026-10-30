// Test oracle for UiTabsParityTests (UI programme Phase 1.6, first tab batch). NOT production code.
//
// Below is the WPF MarketVm exactly as it was before its logic moved to src/GridSim.Ui/Tabs (src/GridSim.Wpf/Views/MarketVm.cs
// at 2.0.22), copied mechanically from git. The only edits are:
//   - the line "using System.Windows.Media;" removed (the shims in OldTabsShims.cs stand in) (1x);
//   - the line "using GridSim.Wpf.Visuals;" removed (Palette and Fuels are shims) (1x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabs (1x);
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldTabs;

/// <summary>
/// THE MARKET TAB  -  the trading / market-operator view of the same streamed instant. Frame data gives the
/// physical backdrop (demand, generation, the fuel stack that sets the marginal plant); the GDA lake streams the
/// market series on top: imbalance price (SBP/SSP), the market index, net imbalance volume, day-ahead price,
/// grid carbon intensity, and interconnector flows. Read-only, updated per frame; a rolling price trace animates.
/// </summary>
public sealed class MarketVm : Notify
{
    // The price trace keeps ImbalancePriceTrace.HistoryLength (96) samples: about two days of settlement periods.

    public ObservableCollection<MktInterconnectorRow> Interconnectors { get; } = new();

    // ---- headline prices (lake) ----
    private string _imbalancePrice = " - "; public string ImbalancePrice { get => _imbalancePrice; set => Set(ref _imbalancePrice, value); }
    private Brush _imbalanceBrush = Brushes.Gray; public Brush ImbalanceBrush { get => _imbalanceBrush; set => Set(ref _imbalanceBrush, value); }
    private string _systemSellPrice = " - "; public string SystemSellPrice { get => _systemSellPrice; set => Set(ref _systemSellPrice, value); }
    private string _marketIndex = " - "; public string MarketIndex { get => _marketIndex; set => Set(ref _marketIndex, value); }
    private string _dayAhead = " - "; public string DayAhead { get => _dayAhead; set => Set(ref _dayAhead, value); }
    private string _bsuos = " - "; public string Bsuos { get => _bsuos; set => Set(ref _bsuos, value); }
    private string _niv = " - "; public string Niv { get => _niv; set => Set(ref _niv, value); }
    private Brush _nivBrush = Brushes.Gray; public Brush NivBrush { get => _nivBrush; set => Set(ref _nivBrush, value); }
    private string _constraintCost = " - "; public string ConstraintCost { get => _constraintCost; set => Set(ref _constraintCost, value); }

    // ---- carbon + demand ----
    private string _carbon = " - "; public string Carbon { get => _carbon; set => Set(ref _carbon, value); }
    private Brush _carbonBrush = Brushes.Gray; public Brush CarbonBrush { get => _carbonBrush; set => Set(ref _carbonBrush, value); }
    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _demandForecast = " - "; public string DemandForecast { get => _demandForecast; set => Set(ref _demandForecast, value); }
    private string _renewableShare = " - "; public string RenewableShare { get => _renewableShare; set => Set(ref _renewableShare, value); }
    private double _renewableFrac; public double RenewableFrac { get => _renewableFrac; set => Set(ref _renewableFrac, value); }

    // ---- rolling price trace (for a Sparkline) ----
    // N144 (October 2026): one sample per settlement period, by Core's ImbalancePriceTrace (the rule the web has had since
    // N037). It used to add a sample on every tick, so the 1 s raw replay filled the 96-sample trace with up to 1,800
    // copies of one half-hourly price.
    private readonly Queue<double> _priceHist = new();
    private long? _pricePeriod;   // the settlement period of the newest sample (null before the first)
    private IReadOnlyList<double> _priceTrace = System.Array.Empty<double>();
    public IReadOnlyList<double> PriceTrace { get => _priceTrace; set => Set(ref _priceTrace, value); }

    private static readonly Brush GreenB = Palette.Frozen(Palette.FromHex("#2FD27F"));
    private static readonly Brush AmberB = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush RedB = Palette.Frozen(Palette.FromHex("#FF5B5B"));

    /// <summary>Per-frame refresh of the frame-derived backdrop (demand, renewable share, indicative carbon).</summary>
    public void Update(SimFrame f, string clock)
    {
        Demand = $"{f.DemandMw / 1000.0:F2} GW";

        // Renewable share + an indicative grid carbon from the dispatched fuel mix (lake carbon overrides in SetMarketData).
        double renew = 0, total = 0, co2 = 0;
        foreach (Generator g in f.Model.Generators)
        {
            double mw = System.Math.Max(0, g.Pg);
            total += mw;
            FuelInfo fi = Fuels.Classify(g.Type);
            if (fi.IsRenewable) renew += mw;
            co2 += mw * CarbonFactors.IntensityKgPerMwh(fi.Name, fi.IsFossil);   // kg/MWh * MW
        }
        RenewableFrac = total > 0 ? renew / total : 0;
        RenewableShare = total > 0 ? $"{renew / total * 100:F0}%" : " - ";
        if (total > 0) { Carbon = $"{co2 / total:F0} gCO2/kWh"; CarbonBrush = CarbonColour(co2 / total); }
    }

    /// <summary>Feed the market series streamed from the GDA lake (side-channel from the bridge state handler).
    /// <paramref name="tickUtc"/> is the bridge tick's own UTC instant (N144): with it the price trace takes one sample
    /// per settlement period; without it (no dated tick) every price is a sample, as before.</summary>
    public void SetMarketData(GridSim.Core.IO.BridgeProtocol.MarketBlock? mk, System.DateTime? tickUtc = null)
    {
        if (mk is null) return;
        if (mk.ImbalancePriceGbpMwh is double ip)
        {
            ImbalancePrice = $"GBP {ip:F2}";
            ImbalanceBrush = ip < 0 ? GreenB : ip > 150 ? RedB : ip > 80 ? AmberB : Palette.Frozen(Palette.FromHex("#C7D2DD"));
            bool changed = tickUtc is System.DateTime at
                ? ImbalancePriceTrace.Append(_priceHist, ip, at, ref _pricePeriod)
                : ImbalancePriceTrace.Append(_priceHist, ip);
            if (changed) PriceTrace = _priceHist.ToArray();
        }
        if (mk.SystemSellPriceGbpMwh is double sp) SystemSellPrice = $"GBP {sp:F2}";
        if (mk.MarketIndexGbpMwh is double mi) MarketIndex = $"GBP {mi:F2}";
        if (mk.DayAheadGbpMwh is double da) DayAhead = $"GBP {da:F2}";
        if (mk.BsuosGbpMwh is double bs) Bsuos = $"GBP {bs:F2}";
        if (mk.ConstraintCostGbp is double cc)
            ConstraintCost = cc >= 1e6 ? $"GBP {cc / 1e6:F1}m" : cc >= 1e3 ? $"GBP {cc / 1e3:F0}k" : $"GBP {cc:F0}";
        if (mk.NetImbalanceVolumeMwh is double niv)
        {
            Niv = $"{niv:+0;-0;0} MWh";
            NivBrush = niv < 0 ? RedB : GreenB;   // system short (-) vs long (+)
        }
        if (mk.CarbonIntensityGco2PerKwh is double c) { Carbon = $"{c:F0} gCO2/kWh"; CarbonBrush = CarbonColour(c); }
        if (mk.DemandForecastMw is double df) DemandForecast = $"{df / 1000.0:F2} GW";

        Interconnectors.Clear();
        if (mk.Interconnectors is { } ics)
            foreach (var ic in ics)
                Interconnectors.Add(new MktInterconnectorRow
                {
                    Name = ic.Name ?? " - ",
                    Flow = $"{(ic.FlowMw ?? 0) / 1000.0:+0.00;-0.00;0.00} GW",
                    FlowBrush = (ic.FlowMw ?? 0) >= 0 ? GreenB : AmberB,   // import +ve
                    Direction = (ic.FlowMw ?? 0) >= 0 ? "IMPORT" : "EXPORT",
                    Price = ic.PriceGbpMwh is double p ? $"GBP {p:F2}" : " - ",
                });
    }

    /// <summary>
    /// D-DOC-143: break the timeline (model switch, entering replay, a new bridge stream): drop every value the GDA
    /// lake streamed. MarketVm had no Reset, so after leaving the bridge for a local case the bridge's imbalance,
    /// sell, index, day-ahead and BSUoS prices, NIV, constraint cost, demand forecast, interconnector rows and price
    /// trace stayed on the MARKET tab beside the local case's numbers, indistinguishable from live. Called from
    /// <c>DashboardViewModel.ResetTimeline</c> beside the NESO RP1 and OPERATOR resets. The frame-derived values
    /// (demand, renewable share, indicative carbon) are cleared too and refill from the next frame's
    /// <see cref="Update"/>, so a lake carbon figure cannot linger over a local case either.
    /// </summary>
    public void Reset()
    {
        // Lake-sourced headline prices and volumes (every property SetMarketData writes).
        ImbalancePrice = " - "; ImbalanceBrush = Brushes.Gray;
        SystemSellPrice = " - ";
        MarketIndex = " - ";
        DayAhead = " - ";
        Bsuos = " - ";
        ConstraintCost = " - ";
        Niv = " - "; NivBrush = Brushes.Gray;
        DemandForecast = " - ";
        Interconnectors.Clear();

        // The price trace and its settlement-period marker: a new timeline starts a new continuous run.
        _priceHist.Clear();
        _pricePeriod = null;
        PriceTrace = System.Array.Empty<double>();

        // Frame-derived (Update) values, refilled by the next frame. Carbon is also written by the lake.
        Carbon = " - "; CarbonBrush = Brushes.Gray;
        Demand = " - ";
        RenewableShare = " - ";
        RenewableFrac = 0;
    }

    // Indicative direct-combustion carbon factors now live in GridSim.Core.Presentation.CarbonFactors
    // (IntensityKgPerMwh) - the single source shared with the web MarketService/FuelBreakdown carbon line.
    // The lake's measured carbon intensity overrides this whenever a tick carries it.

    private static Brush CarbonColour(double gco2) =>
        gco2 < 100 ? GreenB : gco2 < 250 ? AmberB : RedB;
}

public sealed class MktInterconnectorRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private Brush _flowBrush = Brushes.Gray; public Brush FlowBrush { get => _flowBrush; set => Set(ref _flowBrush, value); }
    private string _direction = ""; public string Direction { get => _direction; set => Set(ref _direction, value); }
    private string _price = ""; public string Price { get => _price; set => Set(ref _price, value); }
}
