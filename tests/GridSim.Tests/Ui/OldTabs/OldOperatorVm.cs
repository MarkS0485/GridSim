// Test oracle for UiTabsParityTests (UI programme Phase 1.6, first tab batch). NOT production code.
//
// Below is the WPF OperatorVm exactly as it was before its logic moved to src/GridSim.Ui/Tabs (src/GridSim.Wpf/Views/OperatorVm.cs
// at 2.0.22), copied mechanically from git. The only edits are:
//   - the line "using System.Windows.Media;" removed (the shims in OldTabsShims.cs stand in) (1x);
//   - the line "using GridSim.Wpf.Visuals;" removed (Palette and Fuels are shims) (1x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabs (1x);
//   - "System.Windows.Visibility" written "Visibility" (the shim) (6x);
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldTabs;

/// <summary>
/// THE OPERATOR TAB  -  the control-room / system-operator view of the streamed grid state.
/// It fuses what GridSim solves each frame (boundary transfers vs their SCOTEX/FLOWSTH limits,
/// frequency &amp; RoCoF, generation mix, synchronous-vs-converter split, voltage band, losses) with
/// the operator-domain time series streamed from the GDA lake (balancing-mechanism dispatch,
/// active network constraints, and system warnings). Everything is read-only and updated per frame.
/// </summary>
public sealed class OperatorVm : Notify
{
    // ---- boundary transfers (NESO constraint monitoring) ----
    public ObservableCollection<OpBoundaryRow> Boundaries { get; } = new();

    // ---- generation by fuel (the dispatch stack right now) ----
    public ObservableCollection<OpFuelRow> Fuel { get; } = new();

    // ---- lake-streamed operator series (filled by SetOperatorData) ----
    public ObservableCollection<OpActionRow> BalancingActions { get; } = new();
    public ObservableCollection<OpConstraintRow> Constraints { get; } = new();
    public ObservableCollection<OpWarningRow> Warnings { get; } = new();

    // ---- headline system scalars ----
    private string _frequency = "50.00"; public string Frequency { get => _frequency; set => Set(ref _frequency, value); }
    private Brush _frequencyBrush = Brushes.Gray; public Brush FrequencyBrush { get => _frequencyBrush; set => Set(ref _frequencyBrush, value); }
    private string _rocof = " - "; public string Rocof { get => _rocof; set => Set(ref _rocof, value); }
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _generation = " - "; public string Generation { get => _generation; set => Set(ref _generation, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    private string _voltageBand = " - "; public string VoltageBand { get => _voltageBand; set => Set(ref _voltageBand, value); }
    private string _synchronousGw = " - "; public string SynchronousGw { get => _synchronousGw; set => Set(ref _synchronousGw, value); }
    private string _converterGw = " - "; public string ConverterGw { get => _converterGw; set => Set(ref _converterGw, value); }
    private double _synchronousFrac; public double SynchronousFrac { get => _synchronousFrac; set => Set(ref _synchronousFrac, value); }

    // ---- lake-streamed headline operator scalars (response/reserve, largest loss, interconnect) ----
    // LargestLoss is the LIVE credible loss (largest single infeed online, any technology since N039), not the lake's 1,800 MW figure.
    // DesignBasisLoss carries the 3,600 MW Dogger Bank correlated cluster (an unsecured simultaneous-event scenario,
    // TSGB Policy 0021). N040 (S3, October 2026): 1,800 MW is not "stale" - NESO FRCR 2025 (docs/reference S052)
    // s.4.1 treats it as the future largest loss. Both agree with the NESO RP1 tab's credible-loss panel.
    private string _largestLoss = " - "; public string LargestLoss { get => _largestLoss; set => Set(ref _largestLoss, value); }
    private string _designBasisLoss = $"{CredibleLossBasis.DesignBasisLossMw:N0} MW * Dogger Bank cluster (not secured)"; public string DesignBasisLoss { get => _designBasisLoss; set => Set(ref _designBasisLoss, value); }
    private string _responseMw = " - "; public string ResponseMw { get => _responseMw; set => Set(ref _responseMw, value); }
    private string _deratedMargin = " - "; public string DeratedMargin { get => _deratedMargin; set => Set(ref _deratedMargin, value); }
    private string _reserveMw = " - "; public string ReserveMw { get => _reserveMw; set => Set(ref _reserveMw, value); }
    private string _netInterconnector = " - "; public string NetInterconnector { get => _netInterconnector; set => Set(ref _netInterconnector, value); }
    private string _warningBanner = ""; public string WarningBanner { get => _warningBanner; set => Set(ref _warningBanner, value); }
    private Brush _warningBrush = Brushes.Transparent; public Brush WarningBrush { get => _warningBrush; set => Set(ref _warningBrush, value); }
    private Visibility _warningVisible = Visibility.Collapsed;
    public Visibility WarningVisible { get => _warningVisible; set => Set(ref _warningVisible, value); }

    private static readonly Brush GreenB = Palette.Frozen(Palette.FromHex("#2FD27F"));
    private static readonly Brush AmberB = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush RedB = Palette.Frozen(Palette.FromHex("#FF5B5B"));
    private static readonly Brush MutedB = Palette.Frozen(Palette.FromHex("#8492A0"));

    /// <summary>
    /// Clear the LAKE-FED panels and scalars. SetOperatorData deliberately latches values across
    /// null ticks, so leaving bridge mode for a local case would otherwise keep showing the last GB
    /// BM actions/constraints/reserve/warnings against an unrelated IEEE case - call this on every
    /// source switch (ResetTimeline), like NesoRp1Vm.Reset.
    /// </summary>
    public void Reset()
    {
        BalancingActions.Clear();
        Constraints.Clear();
        Warnings.Clear();
        ResponseMw = " - ";
        ReserveMw = " - ";
        DeratedMargin = " - ";
        NetInterconnector = " - ";
        LargestLoss = " - ";
        WarningBanner = "";
        WarningBrush = Brushes.Transparent;
        WarningVisible = Visibility.Collapsed;
    }

    /// <summary>Per-frame refresh from the solved frame + the boundary evaluation the dashboard already computes.</summary>
    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, string clock)
    {
        // R196 (S5, October 2026): the deviation is from the model's own nominal frequency, not a literal 50 Hz (every
        // bundled and imported model is 50 Hz, so no colour changes; a 60 Hz case no longer reads as 10 Hz high).
        double devHz = f.FrequencyHz - f.Model.FrequencyHz;
        Frequency = f.FrequencyHz.ToString("F3");
        // Red is the header lamp's own rule (StatusLampRules.IsFrequencyDeviationEvent against the band edges about the
        // case nominal), as the NETWORK dial uses: the subtraction form |dev| > 0.2 was true at exactly 49.8 / 50.2 Hz
        // (the double difference is 0.20000000000000284), so this tile read red where the lamp read in band. A
        // non-finite frequency reads red. The amber band (beyond 0.1 Hz) is unchanged.
        bool freqEvent = !double.IsFinite(f.FrequencyHz)
            || GridSim.Core.Presentation.StatusLampRules.IsFrequencyDeviationEvent(f.FrequencyHz, GridSim.Core.Dynamics.FrequencyBands.ForModel(f.Model));
        FrequencyBrush = freqEvent ? RedB : Math.Abs(devHz) > 0.1 ? AmberB : GreenB;
        Rocof = $"{f.RocofHzPerS:+0.000;-0.000;0.000} Hz/s";
        Inertia = $"{f.Model.SystemInertiaGvaS:F0} GVA.s";
        Demand = $"{f.DemandMw / 1000.0:F2} GW";
        Generation = $"{f.GenerationMw / 1000.0:F2} GW";
        Losses = $"{f.Result.TotalLossMw:F0} MW";
        double vmin = f.Result.Vm.Length > 0 ? f.Result.Vm.Min() : 1, vmax = f.Result.Vm.Length > 0 ? f.Result.Vm.Max() : 1;
        VoltageBand = $"{vmin:F3}-{vmax:F3} pu";

        SyncBoundaries(boundaries);
        SyncFuel(f.Model);
    }

    private void SyncBoundaries(IReadOnlyList<BoundaryFlow> boundaries)
    {
        for (int i = 0; i < boundaries.Count; i++)
        {
            BoundaryFlow b = boundaries[i];
            OpBoundaryRow row = i < Boundaries.Count ? Boundaries[i] : Add(Boundaries, new OpBoundaryRow());
            row.Name = b.Name;
            row.Flow = FormatGw(b.FlowMw);
            row.Limit = FormatGw(b.LimitMw);
            row.LoadingPercent = b.LoadingPercent;
            row.Loading = $"{b.LoadingPercent:F0}%";
            row.LoadingBrush = Palette.Frozen(Palette.Loading(b.LoadingPercent / 100.0));
            row.Headroom = FormatGw(Math.Max(0, b.LimitMw - Math.Abs(b.FlowMw)));
        }
        while (Boundaries.Count > boundaries.Count) Boundaries.RemoveAt(Boundaries.Count - 1);
    }

    private void SyncFuel(GridModel m)
    {
        // Aggregate dispatched output + capacity + inertia by fuel class, synchronous vs converter.
        var agg = new Dictionary<string, (double mw, FuelInfo fi)>();
        double syncMw = 0, convMw = 0;
        foreach (Generator g in m.Generators)
        {
            FuelInfo fi = Fuels.Classify(g.Type);
            double mw = Math.Max(0, g.Pg);
            agg.TryGetValue(fi.Name, out (double mw, FuelInfo fi) cur);
            agg[fi.Name] = (cur.mw + mw, fi);
            if (fi.Synchronous) syncMw += mw; else convMw += mw;
        }
        double total = syncMw + convMw;
        // The LIVE largest credible loss: the largest online single infeed (any technology since N039, B4 2026-10), from
        // the ONE Core presenter the web OPERATOR / RP1 projections and the NESO RP1 tab also call (Q1, 2026-10; FRCR
        // 2025 secures BMU-only events).
        double largestUnit = FrequencyRiskPresenter.LargestSecuredUnit(m.Generators).Mw;
        LargestLoss = largestUnit > 0 ? $"{largestUnit:N0} MW" : " - ";
        SynchronousGw = $"{syncMw / 1000.0:F1} GW";
        ConverterGw = $"{convMw / 1000.0:F1} GW";
        SynchronousFrac = total > 0 ? syncMw / total : 0;

        var ordered = agg.OrderByDescending(kv => kv.Value.mw).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            (string name, (double mw, FuelInfo fi)) = ordered[i];
            OpFuelRow row = i < Fuel.Count ? Fuel[i] : Add(Fuel, new OpFuelRow());
            row.Fuel = name;
            row.Swatch = Palette.Frozen(fi.Color);
            row.Mw = $"{mw:F0} MW";
            row.SharePercent = total > 0 ? mw / total * 100 : 0;
            row.Share = total > 0 ? $"{mw / total * 100:F0}%" : " - ";
        }
        while (Fuel.Count > ordered.Count) Fuel.RemoveAt(Fuel.Count - 1);
    }

    /// <summary>
    /// Feed the operator-domain series streamed from the GDA lake (balancing-mechanism acceptances,
    /// active constraints, warnings, and the response/reserve/largest-loss headline scalars). Called
    /// side-channel from the bridge state handler, like the HUD scalars - null leaves the last values.
    /// </summary>
    public void SetOperatorData(GridSim.Core.IO.BridgeProtocol.OperatorBlock? op, string clock)
    {
        if (op is null) return;
        // NOTE: op.LargestLossMw (the lake's 1,800 MW figure, FRCR 2025's future largest loss) is NOT shown here -
        // LargestLoss is set to the live largest online unit in SyncFuel; DesignBasisLoss carries 3,600 MW.
        if (op.ResponseMw is double rp) ResponseMw = $"{rp:F0} MW";
        if (op.ReserveMw is double rs) ReserveMw = $"{rs / 1000.0:F1} GW";
        if (op.DeratedMarginMw is double dm) DeratedMargin = $"{dm / 1000.0:F1} GW";
        if (op.NetInterconnectorMw is double ic) NetInterconnector = $"{ic / 1000.0:+0.00;-0.00;0.00} GW";

        if (op.Warning is { Length: > 0 } w)
        {
            // syswarn text carries literal "\n" escapes and is multi-line; collapse to a single
            // banner line and cap the length so it doesn't swallow the panel.
            string clean = w.Replace("\\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            if (clean.Length > 160) clean = clean[..160] + "...";
            WarningBanner = clean;
            WarningBrush = RedB;
            WarningVisible = Visibility.Visible;
        }
        else { WarningBanner = ""; WarningBrush = Brushes.Transparent; WarningVisible = Visibility.Collapsed; }

        SyncActions(op.BalancingActions, clock);
        SyncConstraints(op.Constraints);
        SyncWarnings(op.Warnings, clock);
    }

    private void SyncActions(List<GridSim.Core.IO.BridgeProtocol.BalancingActionDto>? actions, string clock)
    {
        BalancingActions.Clear();
        if (actions is null) return;
        foreach (var a in actions.Take(40))
            BalancingActions.Add(new OpActionRow
            {
                Unit = a.BmUnit ?? " - ",
                Kind = a.Kind ?? " - ",
                KindBrush = (a.Kind ?? "").StartsWith("BID", StringComparison.OrdinalIgnoreCase) ? AmberB : GreenB,
                Mw = $"{a.LevelMw ?? 0:F0}",
                Price = a.PriceGbpMwh is double p ? $"GBP {p:F2}" : " - ",
                Stamp = clock,
            });
    }

    private void SyncConstraints(List<GridSim.Core.IO.BridgeProtocol.ConstraintDto>? cons)
    {
        Constraints.Clear();
        if (cons is null) return;
        foreach (var c in cons.Take(30))
            Constraints.Add(new OpConstraintRow
            {
                Name = c.Name ?? " - ",
                Flow = FormatGw(c.FlowMw ?? 0),
                Limit = FormatGw(c.LimitMw ?? 0),
                CostGbp = c.CostGbp is double cost ? $"GBP {cost / 1000.0:F0}k" : " - ",
                Binding = c.Binding ?? false,
                BindingBrush = (c.Binding ?? false) ? RedB : MutedB,
            });
    }

    private void SyncWarnings(List<string>? warnings, string clock)
    {
        Warnings.Clear();
        if (warnings is null) return;
        foreach (string w in warnings.Take(20))
            Warnings.Add(new OpWarningRow { Message = w, Stamp = clock, Brush = AmberB });
    }

    private static T Add<T>(ObservableCollection<T> col, T item) { col.Add(item); return item; }
    private static string FormatGw(double mw) => Math.Abs(mw) >= 1000 ? $"{mw / 1000.0:F2} GW" : $"{mw:F0} MW";
}

public sealed class OpBoundaryRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _headroom = ""; public string Headroom { get => _headroom; set => Set(ref _headroom, value); }
    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private double _loadingPercent; public double LoadingPercent { get => _loadingPercent; set => Set(ref _loadingPercent, value); }
    private Brush _loadingBrush = Brushes.Gray; public Brush LoadingBrush { get => _loadingBrush; set => Set(ref _loadingBrush, value); }
}

public sealed class OpFuelRow : Notify
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private Brush _swatch = Brushes.Gray; public Brush Swatch { get => _swatch; set => Set(ref _swatch, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _share = ""; public string Share { get => _share; set => Set(ref _share, value); }
    private double _sharePercent; public double SharePercent { get => _sharePercent; set => Set(ref _sharePercent, value); }
}

public sealed class OpActionRow : Notify
{
    private string _unit = ""; public string Unit { get => _unit; set => Set(ref _unit, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private Brush _kindBrush = Brushes.Gray; public Brush KindBrush { get => _kindBrush; set => Set(ref _kindBrush, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _price = ""; public string Price { get => _price; set => Set(ref _price, value); }
    private string _stamp = ""; public string Stamp { get => _stamp; set => Set(ref _stamp, value); }
}

public sealed class OpConstraintRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _costGbp = ""; public string CostGbp { get => _costGbp; set => Set(ref _costGbp, value); }
    private bool _binding; public bool Binding { get => _binding; set => Set(ref _binding, value); }
    private Brush _bindingBrush = Brushes.Gray; public Brush BindingBrush { get => _bindingBrush; set => Set(ref _bindingBrush, value); }
}

public sealed class OpWarningRow : Notify
{
    private string _message = ""; public string Message { get => _message; set => Set(ref _message, value); }
    private string _stamp = ""; public string Stamp { get => _stamp; set => Set(ref _stamp, value); }
    private Brush _brush = Brushes.Gray; public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}
