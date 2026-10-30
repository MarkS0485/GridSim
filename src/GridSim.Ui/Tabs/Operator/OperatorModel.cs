using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Operator;

/// <summary>
/// THE OPERATOR TAB  -  the control-room / system-operator view of the streamed grid state.
/// It fuses what GridSim solves each frame (boundary transfers vs their SCOTEX/FLOWSTH limits,
/// frequency &amp; RoCoF, generation mix, synchronous-vs-converter split, voltage band, losses) with
/// the operator-domain time series streamed from the GDA lake (balancing-mechanism dispatch,
/// active network constraints, and system warnings). Everything is read-only and updated per frame.
///
/// <para>UI programme Phase 1.6: this is the logic of the WPF <c>Views/OperatorVm.cs</c>, moved here line for line so
/// the Avalonia app shows the same tab from the same code. Every displayed string and number is produced by the same
/// formatting call, in the same culture, in the same order. The only changes are at the framework boundary: each WPF
/// <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes; <c>FrequencyBrush</c> is
/// now <see cref="FrequencyColour"/>, <c>WarningBrush</c> is <see cref="WarningColour"/>), and the WPF
/// <c>Visibility</c> of the warning banner became a bool (<see cref="IsWarningVisible"/>, true = visible). The WPF
/// <c>OperatorVm</c> derives from this class and maps them back under the old names, so its XAML is unchanged.</para>
/// </summary>
public class OperatorModel : ObservableModel
{
    // ---- boundary transfers (NESO constraint monitoring) ----
    public ObservableCollection<OpBoundaryRowModel> Boundaries { get; } = new();

    // ---- generation by fuel (the dispatch stack right now) ----
    public ObservableCollection<OpFuelRowModel> Fuel { get; } = new();

    // ---- lake-streamed operator series (filled by SetOperatorData) ----
    public ObservableCollection<OpActionRowModel> BalancingActions { get; } = new();
    public ObservableCollection<OpConstraintRowModel> Constraints { get; } = new();
    public ObservableCollection<OpWarningRowModel> Warnings { get; } = new();

    // ---- headline system scalars ----
    private string _frequency = "50.00"; public string Frequency { get => _frequency; set => Set(ref _frequency, value); }
    /// <summary>The frequency read-out's colour (was the WPF <c>FrequencyBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _frequencyColour = DashboardColours.Gray; public UiColour FrequencyColour { get => _frequencyColour; set => Set(ref _frequencyColour, value); }
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
    /// <summary>The warning banner's colour (was the WPF <c>WarningBrush</c>, default <c>Brushes.Transparent</c>).</summary>
    private UiColour _warningColour = DashboardColours.Transparent; public UiColour WarningColour { get => _warningColour; set => Set(ref _warningColour, value); }
    /// <summary>True while the warning banner is shown (was the WPF <c>WarningVisible</c>: true = Visible, false = Collapsed).</summary>
    private bool _isWarningVisible; public bool IsWarningVisible { get => _isWarningVisible; set => Set(ref _isWarningVisible, value); }

    // The tab's fixed colours: the WPF frozen brushes of the same hex (Palette.Frozen(Palette.FromHex(...))).
    private static readonly UiColour Green = ColourRamps.FromHex("#2FD27F");
    private static readonly UiColour Amber = ColourRamps.FromHex("#F3C13A");
    private static readonly UiColour Red = ColourRamps.FromHex("#FF5B5B");
    private static readonly UiColour Muted = ColourRamps.FromHex("#8492A0");

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
        WarningColour = DashboardColours.Transparent;
        IsWarningVisible = false;
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
        FrequencyColour = freqEvent ? Red : Math.Abs(devHz) > 0.1 ? Amber : Green;
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
            OpBoundaryRowModel row = i < Boundaries.Count ? Boundaries[i] : Add(Boundaries, CreateBoundaryRow());
            row.Name = b.Name;
            row.Flow = FormatGw(b.FlowMw);
            row.Limit = FormatGw(b.LimitMw);
            row.LoadingPercent = b.LoadingPercent;
            row.Loading = $"{b.LoadingPercent:F0}%";
            row.LoadingColour = ColourRamps.Loading(b.LoadingPercent / 100.0);
            row.Headroom = FormatGw(Math.Max(0, b.LimitMw - Math.Abs(b.FlowMw)));
        }
        while (Boundaries.Count > boundaries.Count) Boundaries.RemoveAt(Boundaries.Count - 1);
    }

    private void SyncFuel(GridModel m)
    {
        // Aggregate dispatched output + capacity + inertia by fuel class, synchronous vs converter.
        var agg = new Dictionary<string, (double mw, UiFuelInfo fi)>();
        double syncMw = 0, convMw = 0;
        foreach (Generator g in m.Generators)
        {
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            double mw = Math.Max(0, g.Pg);
            agg.TryGetValue(fi.Name, out (double mw, UiFuelInfo fi) cur);
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
            (string name, (double mw, UiFuelInfo fi)) = ordered[i];
            OpFuelRowModel row = i < Fuel.Count ? Fuel[i] : Add(Fuel, CreateFuelRow());
            row.Fuel = name;
            row.SwatchColour = fi.Colour;
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
            WarningColour = Red;
            IsWarningVisible = true;
        }
        else { WarningBanner = ""; WarningColour = DashboardColours.Transparent; IsWarningVisible = false; }

        SyncActions(op.BalancingActions, clock);
        SyncConstraints(op.Constraints);
        SyncWarnings(op.Warnings, clock);
    }

    private void SyncActions(List<GridSim.Core.IO.BridgeProtocol.BalancingActionDto>? actions, string clock)
    {
        BalancingActions.Clear();
        if (actions is null) return;
        foreach (var a in actions.Take(40))
        {
            // The WPF object initialiser, property for property in its order, on a row the front end makes.
            OpActionRowModel row = CreateActionRow();
            row.Unit = a.BmUnit ?? " - ";
            row.Kind = a.Kind ?? " - ";
            row.KindColour = (a.Kind ?? "").StartsWith("BID", StringComparison.OrdinalIgnoreCase) ? Amber : Green;
            row.Mw = $"{a.LevelMw ?? 0:F0}";
            row.Price = a.PriceGbpMwh is double p ? $"GBP {p:F2}" : " - ";
            row.Stamp = clock;
            BalancingActions.Add(row);
        }
    }

    private void SyncConstraints(List<GridSim.Core.IO.BridgeProtocol.ConstraintDto>? cons)
    {
        Constraints.Clear();
        if (cons is null) return;
        foreach (var c in cons.Take(30))
        {
            OpConstraintRowModel row = CreateConstraintRow();
            row.Name = c.Name ?? " - ";
            row.Flow = FormatGw(c.FlowMw ?? 0);
            row.Limit = FormatGw(c.LimitMw ?? 0);
            row.CostGbp = c.CostGbp is double cost ? $"GBP {cost / 1000.0:F0}k" : " - ";
            row.Binding = c.Binding ?? false;
            row.BindingColour = (c.Binding ?? false) ? Red : Muted;
            Constraints.Add(row);
        }
    }

    private void SyncWarnings(List<string>? warnings, string clock)
    {
        Warnings.Clear();
        if (warnings is null) return;
        foreach (string w in warnings.Take(20))
        {
            OpWarningRowModel row = CreateWarningRow();
            row.Message = w;
            row.Stamp = clock;
            row.Colour = Amber;
            Warnings.Add(row);
        }
    }

    private static T Add<T>(ObservableCollection<T> col, T item) { col.Add(item); return item; }
    private static string FormatGw(double mw) => Math.Abs(mw) >= 1000 ? $"{mw / 1000.0:F2} GW" : $"{mw:F0} MW";

    // ---- the rows: a front end that binds brushes (WPF) makes its own row classes, derived from these ----

    /// <summary>A new boundary row (the WPF tab makes its <c>OpBoundaryRow</c>).</summary>
    protected virtual OpBoundaryRowModel CreateBoundaryRow() => new();

    /// <summary>A new generation-stack row (the WPF tab makes its <c>OpFuelRow</c>).</summary>
    protected virtual OpFuelRowModel CreateFuelRow() => new();

    /// <summary>A new balancing-action row (the WPF tab makes its <c>OpActionRow</c>).</summary>
    protected virtual OpActionRowModel CreateActionRow() => new();

    /// <summary>A new constraint row (the WPF tab makes its <c>OpConstraintRow</c>).</summary>
    protected virtual OpConstraintRowModel CreateConstraintRow() => new();

    /// <summary>A new warning row (the WPF tab makes its <c>OpWarningRow</c>).</summary>
    protected virtual OpWarningRowModel CreateWarningRow() => new();
}

/// <summary>One transmission boundary on the OPERATOR tab (the WPF <c>OpBoundaryRow</c>; <c>LoadingBrush</c> is now
/// the colour token <see cref="LoadingColour"/>).</summary>
public class OpBoundaryRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _headroom = ""; public string Headroom { get => _headroom; set => Set(ref _headroom, value); }
    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private double _loadingPercent; public double LoadingPercent { get => _loadingPercent; set => Set(ref _loadingPercent, value); }
    private UiColour _loadingColour = DashboardColours.Gray; public UiColour LoadingColour { get => _loadingColour; set => Set(ref _loadingColour, value); }
}

/// <summary>One fuel of the OPERATOR generation stack (the WPF <c>OpFuelRow</c>; <c>Swatch</c> is now the colour token
/// <see cref="SwatchColour"/>).</summary>
public class OpFuelRowModel : ObservableModel
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private UiColour _swatchColour = DashboardColours.Gray; public UiColour SwatchColour { get => _swatchColour; set => Set(ref _swatchColour, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _share = ""; public string Share { get => _share; set => Set(ref _share, value); }
    private double _sharePercent; public double SharePercent { get => _sharePercent; set => Set(ref _sharePercent, value); }
}

/// <summary>One balancing-mechanism acceptance (the WPF <c>OpActionRow</c>; <c>KindBrush</c> is now
/// <see cref="KindColour"/>).</summary>
public class OpActionRowModel : ObservableModel
{
    private string _unit = ""; public string Unit { get => _unit; set => Set(ref _unit, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private UiColour _kindColour = DashboardColours.Gray; public UiColour KindColour { get => _kindColour; set => Set(ref _kindColour, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _price = ""; public string Price { get => _price; set => Set(ref _price, value); }
    private string _stamp = ""; public string Stamp { get => _stamp; set => Set(ref _stamp, value); }
}

/// <summary>One active network constraint (the WPF <c>OpConstraintRow</c>; <c>BindingBrush</c> is now
/// <see cref="BindingColour"/>).</summary>
public class OpConstraintRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _costGbp = ""; public string CostGbp { get => _costGbp; set => Set(ref _costGbp, value); }
    private bool _binding; public bool Binding { get => _binding; set => Set(ref _binding, value); }
    private UiColour _bindingColour = DashboardColours.Gray; public UiColour BindingColour { get => _bindingColour; set => Set(ref _bindingColour, value); }
}

/// <summary>One system warning (the WPF <c>OpWarningRow</c>; <c>Brush</c> is now <see cref="Colour"/>).</summary>
public class OpWarningRowModel : ObservableModel
{
    private string _message = ""; public string Message { get => _message; set => Set(ref _message, value); }
    private string _stamp = ""; public string Stamp { get => _stamp; set => Set(ref _stamp, value); }
    private UiColour _colour = DashboardColours.Gray; public UiColour Colour { get => _colour; set => Set(ref _colour, value); }
}
