using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf;

// UI programme Phase 0.2 (second strangler slice): the dashboard's logic moved to the platform-neutral
// GridSim.Ui.Dashboard.DashboardModel (src/GridSim.Ui/Dashboard/DashboardModel.cs), line for line, with each WPF Brush
// replaced by a neutral colour token (UiColour) and each Visibility by a bool. This file is now the thin WPF side of
// that boundary: the view-model and its rows derive from the neutral ones and add, under the OLD property names the
// XAML binds, a frozen brush for every token (Palette.Frozen: the same colour bytes, the same cached brush instances)
// and a Visibility for every bool (true = Visible, false = Collapsed). It also owns the WPF per-tab view-models and
// feeds, resets and clears them through the neutral model's hooks, at exactly the points the old code did. What the
// user sees is unchanged.

/// <summary>One transmission-boundary corridor row in the side panel (the WPF row: adds the <see cref="Bar"/> brush).</summary>
public sealed class BoundaryRow : BoundaryRowModel
{
    /// <summary>The bar's brush: the frozen brush of <see cref="BoundaryRowModel.BarColour"/>.</summary>
    public Brush Bar => Palette.Frozen(BarColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
    }
}

/// <summary>One voltage-band dial on the NETWORK telemetry column (the WPF row: adds the <see cref="Brush"/>).</summary>
public sealed class VoltageBandDial : VoltageBandDialModel
{
    /// <summary>The dial's brush: the frozen brush of <see cref="VoltageBandDialModel.Colour"/>.</summary>
    public Brush Brush => Palette.Frozen(Colour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(Colour)) base.OnPropertyChanged(nameof(Brush));
    }
}

/// <summary>Bound state for the whole control suite: the neutral <see cref="DashboardModel"/> plus its WPF brushes,
/// visibilities and per-tab view-models.</summary>
public sealed class DashboardViewModel : DashboardModel
{
    // ---- the per-tab view-models (still WPF classes; fed and reset through the DashboardModel hooks below) ----

    /// <summary>The ASSETS tab  -  its own page stack, fed the same solved frames.</summary>
    public Views.Assets.AssetExplorerVm Assets { get; } = new();

    /// <summary>THE MACHINE tab  -  the layman's one-rotating-machine view of the same frames.</summary>
    public Views.MachineVm Machine { get; } = new();

    /// <summary>The SCADA tab  -  a simulated supervisory console over the same solved frames.</summary>
    public Views.ScadaVm Scada { get; } = new();

    /// <summary>The OPERATOR tab  -  control-room view: boundaries, generation stack, and lake-streamed BM/constraints.</summary>
    public Views.OperatorVm Operator { get; } = new();

    /// <summary>The MARKET tab  -  wholesale/balancing view: prices, imbalance, interconnectors, carbon.</summary>
    public Views.MarketVm Market { get; } = new();

    /// <summary>The NESO RP1 tab  -  situational-awareness control-room page: purchased time, credible loss, inertia split.</summary>
    public Views.NesoRp1Vm NesoRp1 { get; } = new();

    /// <summary>The MECHANICAL tab  -  per-machine / plant-engineer view: state, protection envelope, asset-level inertia.</summary>
    public Views.MachineEngineerVm Mechanical { get; } = new();

    /// <summary>The SPEED DOMAINS tab  -  experimental distributed-inertia decomposition + GVad dominance (unvalidated at scale).</summary>
    public Views.DomainsVm Domains { get; } = new();

    /// <summary>The INVERTER FLEET tab  -  authored per-asset dynamics (sample->compute->actuate); unvalidated at scale.</summary>
    public Views.InverterFleetVm InverterFleet { get; } = new();

    /// <summary>THE HUMAN SCALE tab  -  the same frames re-expressed in relatable units (ft.lb, hp, kettles); for fun, not operational.</summary>
    public Views.HumanScaleVm HumanScale { get; } = new();

    /// <summary>EXPERIMENTAL tab  -  physical-law conformance audit + the opt-in experimental models (reciprocity, continuum wave, flow tracing, carbon).</summary>
    public Views.ExperimentalVm Experimental { get; } = new();

    /// <summary>The CYCLE tab  -  the prioritised parallel analysis cycle over the same solved frames (single-core
    /// base solve then a thread-pool fan-out of every downstream analysis, with a live budget/coverage readout).</summary>
    public Views.CycleVm Cycle { get; } = new();

    /// <summary>Feed every tab the frame the dashboard just applied: the WPF Update's tab block, in its order, with this
    /// frame's clock and (R101) its measured base-solve time for the CYCLE tab.</summary>
    protected override void UpdateTabs(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, double baseSolveMs)
    {
        Assets.Update(f, Clock);
        Machine.Update(f);
        Scada.Update(f, Clock);
        Operator.Update(f, boundaries, Clock);
        Market.Update(f, Clock);
        NesoRp1.Update(f, boundaries, Clock);
        Mechanical.Update(f, Clock);
        Domains.Update(f);
        InverterFleet.Update(f);
        HumanScale.Update(f, Clock);
        Experimental.Update(f);
        Cycle.Update(f, baseSolveMs);
    }

    /// <summary>The model changed: the SCADA register and the resident CYCLE service are rebuilt on the next frame.</summary>
    protected override void ForgetModelInTabs()
    {
        Scada.ForgetModel();
        Cycle.ForgetModel();
    }

    /// <summary>
    /// The tab half of <see cref="DashboardModel.ResetTimeline"/> (run first, as before): THE MACHINE's integrators are
    /// the load-bearing case, and the bridge-sourced lake state of NESO RP1, OPERATOR, MARKET and MECHANICAL is dropped.
    /// </summary>
    protected override void ResetTabs()
    {
        Machine.Reset();
        NesoRp1.Reset();      // drop stale bridge-sourced lake state
        Operator.Reset();     // drop stale BM actions/constraints/reserve/warnings (SetOperatorData latches)
        Market.Reset();       // D-DOC-143: drop stale bridge prices, NIV, interconnectors and the price trace
        Mechanical.Reset();   // drop stale ramp/pulse accumulators + lake intrinsic
    }

    /// <summary>The stream is lost: the bridge-fed OPERATOR, MARKET and NESO RP1 blocks are cleared (see
    /// <see cref="DashboardModel.MarkStreamLost"/>).</summary>
    protected override void ClearStreamTabs()
    {
        Operator.Reset();
        Market.Reset();
        NesoRp1.Reset();
    }

    /// <summary>
    /// Push the per-tick OPERATOR/MARKET series streamed from the GDA lake into the Operator/Market tabs.
    /// Side-channel from the bridge state handler (mirrors <see cref="DashboardModel.UpdateHud"/>): the fixed
    /// <c>SimFrame</c> doesn't carry balancing-mechanism / price data, so it rides the wire in its own blocks. Also feeds
    /// the Operator headline largest-loss into the HUD-adjacent scalars where useful.
    /// </summary>
    /// <remarks>N144: <paramref name="tickUtc"/> is the bridge tick's UTC instant, so the MARKET price trace takes one
    /// sample per settlement period.</remarks>
    public void UpdateMarketData(GridSim.Core.IO.BridgeProtocol.OperatorBlock? op, GridSim.Core.IO.BridgeProtocol.MarketBlock? mk,
        System.DateTime? tickUtc = null)
    {
        Operator.SetOperatorData(op, Clock);
        Market.SetMarketData(mk, tickUtc);
        NesoRp1.SetRp1Data(op, mk, tickUtc);   // D-DOC-200: one price per settlement period, as MARKET
        Mechanical.SetIntrinsic(op?.SynchronousInertiaGvaS);
    }

    // ---- the WPF brushes: each the cached frozen brush of its neutral colour token ----

    public Brush FreqBrush => Palette.Frozen(FreqColour);
    public Brush InertiaNoteBrush => Palette.Frozen(InertiaNoteColour);
    /// <summary>The header lamp's brush. The window also sets it directly (a SOLVE FAULT), so it has a setter: it must
    /// be a plain solid brush (every caller passes a <see cref="Palette.Frozen(Color)"/> brush), stored as its token.</summary>
    public Brush StatusBrush { get => Palette.Frozen(StatusColour); set => StatusColour = TokenOf(value); }
    public Brush QueryVerdictBrush => Palette.Frozen(QueryVerdictColour);
    public Brush FragilityBrush => Palette.Frozen(FragilityColour);
    public Brush FragilityTrendBrush => Palette.Frozen(FragilityTrendColour);
    public Brush BridgeStatusBrush => Palette.Frozen(BridgeStatusColour);
    public Brush StabilityBrush => Palette.Frozen(StabilityColour);
    public Brush TripVerdictBrush => Palette.Frozen(TripVerdictColour);

    /// <summary>The colour legend's swatch: the gradient of the ramp <see cref="DashboardModel.Legend"/> names.</summary>
    public Brush LegendBrush => Legend switch
    {
        MapColourMode.Voltage => VoltageRamp,
        MapColourMode.Condition => ConditionRamp,
        MapColourMode.Stress => StressRamp,
        _ => LoadingRamp,
    };

    // The four ramps, built once from the same neutral stops the map colours with, so the swatch and the tint agree.
    private static readonly Brush LoadingRamp   = BuildRamp(MapColourMode.Loading);     // 0 .. 1.0
    private static readonly Brush VoltageRamp   = BuildRamp(MapColourMode.Voltage);     // 0.94 .. 1.06
    private static readonly Brush ConditionRamp = BuildRamp(MapColourMode.Condition);   // healthy .. critical
    private static readonly Brush StressRamp    = BuildRamp(MapColourMode.Stress);      // cool .. hot

    private static Brush BuildRamp(MapColourMode mode)
    {
        var stops = new GradientStopCollection();
        foreach ((UiColour colour, double offset) in LegendRampStops(mode)) stops.Add(new GradientStop(Palette.ToColor(colour), offset));
        var b = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
        b.Freeze();
        return b;
    }

    // ---- the WPF visibilities: each bool as Visible (true) or Collapsed (false) ----

    public Visibility InvalidStateVis => InvalidState ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionVis => SelectionVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QueryVis => QueryVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QueryViolationsEmptyVis => QueryViolationsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HudVis => HudVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BridgeStatusVis => BridgeStatusVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AlarmsEmptyVis => AlarmsEmpty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Each neutral property and the WPF adapter property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(StringComparer.Ordinal)
    {
        [nameof(FreqColour)] = nameof(FreqBrush),
        [nameof(InertiaNoteColour)] = nameof(InertiaNoteBrush),
        [nameof(StatusColour)] = nameof(StatusBrush),
        [nameof(QueryVerdictColour)] = nameof(QueryVerdictBrush),
        [nameof(FragilityColour)] = nameof(FragilityBrush),
        [nameof(FragilityTrendColour)] = nameof(FragilityTrendBrush),
        [nameof(BridgeStatusColour)] = nameof(BridgeStatusBrush),
        [nameof(StabilityColour)] = nameof(StabilityBrush),
        [nameof(TripVerdictColour)] = nameof(TripVerdictBrush),
        [nameof(Legend)] = nameof(LegendBrush),
        [nameof(InvalidState)] = nameof(InvalidStateVis),
        [nameof(SelectionVisible)] = nameof(SelectionVis),
        [nameof(QueryVisible)] = nameof(QueryVis),
        [nameof(QueryViolationsEmpty)] = nameof(QueryViolationsEmptyVis),
        [nameof(HudVisible)] = nameof(HudVis),
        [nameof(BridgeStatusVisible)] = nameof(BridgeStatusVis),
        [nameof(AlarmsEmpty)] = nameof(AlarmsEmptyVis),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- brush-taking entry points the window calls (its brushes are Palette.Frozen brushes) ----

    /// <summary>Surface the live-bridge state on the map, from a WPF brush.</summary>
    public void SetBridgeStatus(string text, Brush brush) => SetBridgeStatus(text, TokenOf(brush));

    /// <summary>Raise an alarm, from a WPF brush.</summary>
    public void AddAlarm(string text, Brush brush) => AddAlarm(text, TokenOf(brush));

    /// <summary>
    /// The neutral colour token of a WPF brush: the four bytes of a plain solid brush at full brush opacity. Anything
    /// else (a gradient, or a solid brush with its own Opacity) has no single token, and the dashboard never received
    /// one, so it is refused loudly rather than drawn in a different colour.
    /// </summary>
    private static UiColour TokenOf(Brush brush) =>
        brush is SolidColorBrush { Opacity: 1.0 } s
            ? new UiColour(s.Color.A, s.Color.R, s.Color.G, s.Color.B)
            : throw new ArgumentException($"The dashboard takes a plain solid brush (a Palette.Frozen brush); got {brush}.", nameof(brush));

    // ---- the WPF rows ----

    protected override BoundaryRowModel CreateBoundaryRow(string name) => new BoundaryRow { Name = name };
    protected override VoltageBandDialModel CreateVoltageBandDial(double baseKv, string label) => new VoltageBandDial { BaseKv = baseKv, Label = label };
    protected override FuelRowModel CreateFuelRow(string name, UiColour colour) => new FuelRow { Name = name, Colour = colour };
    protected override ImportLinkRowModel CreateImportLinkRow(int index, string name, string route, string capacity) =>
        new ImportLinkRow { Index = index, Name = name, Route = route, Capacity = capacity };
    protected override AlarmEntry CreateAlarm(string time, string text, UiColour colour) => new AlarmRow(time, text, colour);
    protected override ViolationEntry CreateViolation(Violation v) => ViolationRow.From(v);
}

/// <summary>One row in the alarms panel (the WPF row: adds the <see cref="Brush"/>).</summary>
public sealed record AlarmRow(string Time, string Text, UiColour Colour) : AlarmEntry(Time, Text, Colour)
{
    /// <summary>The row's brush: the frozen brush of its colour token.</summary>
    public Brush Brush => Palette.Frozen(Colour);
}

/// <summary>One constraint violation from an on-demand GDA-bridge query, projected for the results panel (the WPF row:
/// adds the <see cref="Brush"/>).</summary>
public sealed class ViolationRow : ViolationEntry
{
    private ViolationRow(Violation v) : base(v) { }

    /// <summary>The row's brush: the frozen brush of its colour token.</summary>
    public Brush Brush => Palette.Frozen(Colour);

    public static ViolationRow From(Violation v) => new(v);
}

/// <summary>One interconnector's row in the import-links panel (the WPF row: adds <see cref="Bar"/> and
/// <see cref="BadgeBrush"/>).</summary>
public sealed class ImportLinkRow : ImportLinkRowModel
{
    public Brush Bar => Palette.Frozen(BarColour);
    public Brush BadgeBrush => Palette.Frozen(BadgeColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(BarColour)) base.OnPropertyChanged(nameof(Bar));
        else if (name == nameof(BadgeColour)) base.OnPropertyChanged(nameof(BadgeBrush));
    }
}

/// <summary>One fuel's contribution in the generation-mix panel (the WPF row: adds the <see cref="Color"/> brush).</summary>
public sealed class FuelRow : FuelRowModel
{
    /// <summary>The fuel's brush: the frozen brush of <see cref="FuelRowModel.Colour"/> (set once, at creation).</summary>
    public Brush Color => Palette.Frozen(Colour);
}

/// <summary>Minimal INotifyPropertyChanged base.</summary>
public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Notify a derived/computed property alongside a <see cref="Set{T}"/>.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
