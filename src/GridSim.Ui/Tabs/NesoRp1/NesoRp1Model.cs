using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;
using Proto = GridSim.Core.IO.BridgeProtocol;

namespace GridSim.Ui.Tabs.NesoRp1;

/// <summary>
/// THE NESO RP1 TAB - a situational-awareness control-room page, not a dashboard. Every element
/// answers one of: where am I now / where am I going / how long do I have. The hero quantity is
/// <b>purchased time</b> - seconds of frequency ride-through on real intrinsic mass - computed from
/// the streamed inertia against the live credible loss, with the 3,600 MW Dogger Bank correlated cluster (an
/// unsecured simultaneous-event scenario, TSGB Policy 0021) shown alongside. Physics via <see cref="StabilityMetrics"/>.
/// <para>N040 (S3, October 2026): the 1,800 MW figure used to be shown struck through as "superseded". NESO's FRCR 2025
/// (docs/reference S052, s.4.1 p.10: "The minimum inertia of 102 GVA.s is based on a future largest loss of 1800 MW";
/// s.7.1) treats 1,800 MW as the future largest loss, so it is now shown plainly as that, and the 3,600 MW cluster is
/// labelled "correlated cluster (not secured)" instead of "design basis". Labels only: every number is unchanged.</para>
/// <para>Q1 (2026-10, Mark: "ground it in the real rules"): the credible loss and the LFDD grade ladder now come from
/// the ONE Core presenter <see cref="FrequencyRiskPresenter"/>, shared with the web RP1 / OPERATOR projections. The
/// credible loss is the largest single secured unit: NESO's Frequency Risk and Control Report 2025 (March 2025,
/// Executive summary p.4, s.6.2 and s.7.1) secures all BMU-only events and does NOT secure simultaneous events. This
/// tab used to take the larger of that unit and the largest correlated interconnector group; the correlated group
/// is a simultaneous event, so it is now shown as unsecured context and flagged when it is the bigger risk.</para>
/// <para>UI programme Phase 1.6 (third tab batch): this is the logic of the WPF <c>Views/NesoRp1Vm.cs</c>, moved here
/// line for line so the Avalonia app shows the same tab from the same code. Every displayed string and number is
/// produced by the same formatting call, in the same culture, in the same order. The changes are at the framework
/// boundary only:</para>
/// <list type="bullet">
/// <item>each WPF <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes):
/// <c>PurchasedBrush</c> is now <see cref="PurchasedColour"/>, <c>UnitBrush</c> <see cref="UnitColour"/>,
/// <c>GroupBrush</c> <see cref="GroupColour"/>, <c>LfddBrush</c> <see cref="LfddColour"/>, and each row's brush its
/// <c>...Colour</c>;</item>
/// <item>the two WPF <c>Visibility</c> values became bools: <c>PageVisibility</c> is <see cref="IsPageVisible"/> and
/// <c>NotApplicableVisibility</c> is <see cref="IsNotApplicableVisible"/> (true = Visible, false = Collapsed);</item>
/// <item>the three WPF commands became the plain methods <see cref="InspectFuel"/>, <see cref="InspectBm"/> and
/// <see cref="InspectIc"/> (the WPF adapter keeps <c>InspectFuelCommand</c> and the others over them);</item>
/// <item>the drill-down pages, which the WPF code handed straight to its <c>InspectorVm</c>, are built here as neutral
/// <see cref="Rp1InspectPage"/>s (each row's drill-through a plain <see cref="Action"/>) and handed to
/// <see cref="ShowInspector"/>: the WPF adapter overrides it to show them in its <c>InspectorVm</c> exactly as before;
/// by default it raises <see cref="InspectRequested"/>, which a front end's inspector overlay handles.</item>
/// </list>
/// <para>The WPF <c>NesoRp1Vm</c> derives from this class and maps all of these back under the old names, so its XAML
/// is unchanged.</para>
/// </summary>
public class NesoRp1Model : ObservableModel
{
    // ---- the correlated-trip corridor map: infeeds that can be lost together (Policy 0021 s.4;
    // GB connection sites from the interconnector register). Dogger Bank's own 3,600 MW correlated
    // offshore-wind cluster is carried as the design-basis figure below (not yet named live in the model). ----
    // The table and the name matching live in GridSim.Core.Presentation.InterconnectorCorridors, shared with the
    // web RP1 projection: a link counts as a member by canonical id, so "NS Link interconnector" or "North Sea
    // Link" is NSL, not only the exact short id (R053).
    private static IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Corridors => InterconnectorCorridors.All;
    // The 3,600 MW Dogger Bank cluster and the 1,800 MW FRCR 2025 future largest loss now live in
    // GridSim.Core.Presentation.CredibleLossBasis - the single source shared with the web RP1/OPERATOR projections.

    // ---- inputs latched from the two update paths, recombined in Recompute() ----
    private double _currentHz = 50.0;
    private double _largestUnitMw;
    private string _largestUnitName = "-";
    private double _intrinsicModelGvaS;
    private double? _intrinsicLakeGvaS, _outturnGvaS, _marketGvaS;
    private readonly Dictionary<string, double> _icFlows = new();
    // The rolling purchased-time trace (Core, shared with the web session): the last 60 solved state frames, one point
    // per frame (D-DOC-148: the old "~30 min at half-hourly periods" was wrong; its span in time depends on the step).
    private readonly FrequencyRiskPresenter.PurchasedTimeTrace _trace = new();

    // ---- named thresholds (R212: were literals; values unchanged) ----
    // Q1 (2026-10): the purchased-time ladder, cap and trend delta are owned by Core FrequencyRiskPresenter and the
    // logic below calls it. These literal mirrors stay only because PresentationConstantsAntiDriftTests reads them
    // from this source file; FrequencyRiskPresenterTests pins each one equal to its Core value, so they cannot drift.

    /// <summary>Purchased time (s) shown when it is infinite or longer: the gauge, trace and ">10 s" text cap.</summary>
    public const double PurchasedTimeDisplayCapS = 10.0;

    /// <summary>Purchased time (s) at or above which the hero figure is green (the web LFDD GREEN cut is the same 5 s).</summary>
    public const double PurchasedTimeGreenS = 5.0;

    /// <summary>Purchased time (s) at or above which the hero figure is amber (the web LFDD AMBER cut is the same 2 s).</summary>
    public const double PurchasedTimeAmberS = 2.0;

    /// <summary>LFDD grade ladder (s of purchased time): NORMAL at or above 5 s, WATCH at or above 3 s, ADVISORY at
    /// or above 2 s, else WARNING; a tightening trend below the NORMAL cut escalates it. Mirrors of
    /// FrequencyRiskPresenter.GradeNormalS / GradeWatchS / GradeAdvisoryS.</summary>
    public const double LfddNormalS = 5.0, LfddWatchS = 3.0, LfddAdvisoryS = 2.0;

    /// <summary>Drop in purchased time (s) over the last three trace points that counts as a tightening trend.</summary>
    public const double TighteningDeltaS = 0.05;

    /// <summary>Change in boundary loading (percentage points) since the last frame inside which its trend is flat.</summary>
    public const double BoundaryTrendBandPct = 0.5;

    /// <summary>How many imbalance-price samples the price trace keeps (one day of half-hourly periods).</summary>
    public const int PriceTraceLen = 48;

    /// <summary>Most constraint rows and balancing-action rows listed on the panel.</summary>
    public const int ConstraintRowCap = 12, BalancingActionRowCap = 30;

    /// <summary>Hours in a day, to turn the lake's daily constraint cost into an hourly rate.</summary>
    private const double HoursPerDay = 24.0;

    // The tab's fixed colours: the WPF frozen brushes of the same hex (Palette.Frozen(Palette.FromHex(...))).
    private static readonly UiColour GreenB = ColourRamps.FromHex("#2FD27F");
    private static readonly UiColour AmberB = ColourRamps.FromHex("#F3C13A");
    private static readonly UiColour RedB = ColourRamps.FromHex("#FF5B5B");
    private static readonly UiColour MutedB = ColourRamps.FromHex("#8492A0");
    private static readonly UiColour WhiteB = ColourRamps.FromHex("#E8F0F6");

    // ---- hero: purchased time ----
    private double _purchasedValue; public double PurchasedValue { get => _purchasedValue; set => Set(ref _purchasedValue, value); }
    private string _purchasedText = "-"; public string PurchasedText { get => _purchasedText; set => Set(ref _purchasedText, value); }
    /// <summary>The hero figure's colour (was the WPF <c>PurchasedBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _purchasedColour = DashboardColours.Gray; public UiColour PurchasedColour { get => _purchasedColour; set => Set(ref _purchasedColour, value); }
    private string _purchasedDesign = "-"; public string PurchasedDesign { get => _purchasedDesign; set => Set(ref _purchasedDesign, value); }

    // ---- three inertia gauges (fuel gauge: total liquid vs petrol vs water; MIF 0001 s.10) ----
    private double _inertiaTotalValue; public double InertiaTotalValue { get => _inertiaTotalValue; set => Set(ref _inertiaTotalValue, value); }
    private string _inertiaTotalText = "-"; public string InertiaTotalText { get => _inertiaTotalText; set => Set(ref _inertiaTotalText, value); }
    private double _inertiaIntrinsicValue; public double InertiaIntrinsicValue { get => _inertiaIntrinsicValue; set => Set(ref _inertiaIntrinsicValue, value); }
    private string _inertiaIntrinsicText = "-"; public string InertiaIntrinsicText { get => _inertiaIntrinsicText; set => Set(ref _inertiaIntrinsicText, value); }
    private double _inertiaProxyValue; public double InertiaProxyValue { get => _inertiaProxyValue; set => Set(ref _inertiaProxyValue, value); }
    private string _inertiaProxyText = "-"; public string InertiaProxyText { get => _inertiaProxyText; set => Set(ref _inertiaProxyText, value); }

    // ---- live credible loss (largest-possible-now + the unsecured 3,600 MW cluster + the FRCR 2025 1,800 MW) ----
    private string _largestUnit = "-"; public string LargestUnit { get => _largestUnit; set => Set(ref _largestUnit, value); }
    private string _largestUnitLabel = "largest unit online"; public string LargestUnitLabel { get => _largestUnitLabel; set => Set(ref _largestUnitLabel, value); }
    private string _largestGroup = "-"; public string LargestGroup { get => _largestGroup; set => Set(ref _largestGroup, value); }
    private string _largestGroupLabel = "largest correlated group"; public string LargestGroupLabel { get => _largestGroupLabel; set => Set(ref _largestGroupLabel, value); }
    private string _designBasis = "3,600 MW"; public string DesignBasis { get => _designBasis; set => Set(ref _designBasis, value); }
    private string _fiction = "1,800 MW"; public string Fiction { get => _fiction; set => Set(ref _fiction, value); }
    private bool _unitBinding; public bool UnitBinding { get => _unitBinding; set => Set(ref _unitBinding, value); }
    private bool _groupBinding; public bool GroupBinding { get => _groupBinding; set => Set(ref _groupBinding, value); }
    /// <summary>The largest unit's colour (was the WPF <c>UnitBrush</c>, default the muted grey).</summary>
    private UiColour _unitColour = MutedB; public UiColour UnitColour { get => _unitColour; set => Set(ref _unitColour, value); }
    /// <summary>The largest correlated group's colour (was the WPF <c>GroupBrush</c>, default the muted grey).</summary>
    private UiColour _groupColour = MutedB; public UiColour GroupColour { get => _groupColour; set => Set(ref _groupColour, value); }

    // ---- inertia by source (per-fuel contribution, in seconds of purchased time) ----
    public ObservableCollection<Rp1InertiaRowModel> InertiaBySource { get; } = new();

    // ---- LEFT column: the dispatch stack (per-fuel output / headroom / inertia, sync vs converter) ----
    public ObservableCollection<Rp1GenRowModel> GenStack { get; } = new();
    private string _synchronousGw = "-"; public string SynchronousGw { get => _synchronousGw; set => Set(ref _synchronousGw, value); }
    private string _converterGw = "-"; public string ConverterGw { get => _converterGw; set => Set(ref _converterGw, value); }
    private double _synchronousFrac; public double SynchronousFrac { get => _synchronousFrac; set => Set(ref _synchronousFrac, value); }

    // ---- stability trend + derived readouts ----
    /// <summary>D-DOC-148: the trend panel's header, from Core's FrequencyRiskPresenter.StabilityTrendTitle (one
    /// honest wording for the trace's real span, "last 60 frames"). The XAML used to say "30 min" over the
    /// 60-frame trace.</summary>
    public string StabilityTrendTitle { get; } = FrequencyRiskPresenter.StabilityTrendTitle;
    private IReadOnlyList<double> _stabilityTrace = System.Array.Empty<double>();
    public IReadOnlyList<double> StabilityTrace { get => _stabilityTrace; set => Set(ref _stabilityTrace, value); }
    private string _dynamicMif = "-"; public string DynamicMif { get => _dynamicMif; set => Set(ref _dynamicMif, value); }
    private string _rocof = "-"; public string Rocof { get => _rocof; set => Set(ref _rocof, value); }
    private string _lfddGrade = "-"; public string LfddGrade { get => _lfddGrade; set => Set(ref _lfddGrade, value); }
    /// <summary>The LFDD grade chip's colour (was the WPF <c>LfddBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _lfddColour = DashboardColours.Gray; public UiColour LfddColour { get => _lfddColour; set => Set(ref _lfddColour, value); }

    // ---- RIGHT column: network constraint & coherence cost ----
    public ObservableCollection<Rp1BoundaryRowModel> BoundaryBars { get; } = new();
    public ObservableCollection<Rp1ConstraintRowModel> ConstraintCosts { get; } = new();
    private readonly Dictionary<string, double> _boundaryPrev = new();
    private string _boundaryNote = ""; public string BoundaryNote { get => _boundaryNote; set => Set(ref _boundaryNote, value); }

    // ---- BOTTOM CENTRE: market / balancing / BM stability signal ----
    private string _imbalancePrice = "-"; public string ImbalancePrice { get => _imbalancePrice; set => Set(ref _imbalancePrice, value); }
    private IReadOnlyList<double> _priceTrace = System.Array.Empty<double>();
    public IReadOnlyList<double> PriceTrace { get => _priceTrace; set => Set(ref _priceTrace, value); }
    // D-DOC-200: one sample per settlement period (Core ImbalancePriceTrace, the MARKET rule), keeping PriceTraceLen
    // periods (one day). It used to add a sample on every tick, so it held 48 ticks where MARKET held 96 periods.
    private readonly Queue<double> _price = new();
    private long? _pricePeriod;   // the settlement period of the newest sample (null before the first)
    public ObservableCollection<Rp1BmRowModel> BmDelta { get; } = new();
    public ObservableCollection<Rp1IcRowModel> Interconnectors { get; } = new();
    private string _bmSummary = "-"; public string BmSummary { get => _bmSummary; set => Set(ref _bmSummary, value); }

    // ---- drill-down inspector (click a fuel / BM unit / interconnector to see how it is made up) ----
    // The pages are built here and handed to ShowInspector (see the class summary); the generator snapshot of the last
    // frame is what a fuel / unit page is built from, so a paused frame stays explorable.
    private readonly List<GenLite> _lastGens = new();
    private readonly record struct GenLite(string Name, string Fuel, double Mw, double Cap, double H, double KeGvaS, bool Synchronous, UiColour Swatch);

    /// <summary>
    /// Raised with each drill-down page (by the default <see cref="ShowInspector"/>): a front end's inspector overlay
    /// shows it, pushing the page it was showing onto its back-stack, as the WPF <c>InspectorVm.Show</c> does.
    /// </summary>
    public event Action<Rp1InspectPage>? InspectRequested;

    // ---- system narrative (the handover sentence) ----
    private string _narrative = "-"; public string Narrative { get => _narrative; set => Set(ref _narrative, value); }
    private string _clock = "-"; public string Clock { get => _clock; set => Set(ref _clock, value); }

    // ---- N301 (October 2026, stream LF3; Ruling R1): where the page applies ----
    // RP1 is a NESO GB 50 Hz product page (purchased time to the absolute 49.2 Hz, GB RoCoF / MIF). On a case whose
    // nominal is not 50 Hz the page body is collapsed and the shared one-line reason shown instead (the same rule and
    // text the web Rp1Tab uses, FrequencyRiskPresenter.Rp1AppliesAt / Rp1NotApplicableReason). Every figure below is
    // still computed exactly as before; only what the view shows changes. Starts visible (the GB default) until the
    // first frame says otherwise.
    private bool _isPageVisible = true;
    /// <summary>N301: true (shown) when RP1 applies to the current case (nominal exactly 50 Hz), else false (was the
    /// WPF <c>PageVisibility</c>: Visible / Collapsed).</summary>
    public bool IsPageVisible { get => _isPageVisible; private set => Set(ref _isPageVisible, value); }
    private bool _isNotApplicableVisible;
    /// <summary>N301: true (shown, with <see cref="NotApplicableReason"/>) when RP1 does not apply to the current case
    /// (was the WPF <c>NotApplicableVisibility</c>: Visible / Collapsed).</summary>
    public bool IsNotApplicableVisible { get => _isNotApplicableVisible; private set => Set(ref _isNotApplicableVisible, value); }
    private string _notApplicableReason = "";
    /// <summary>N301: the one-line reason shown in place of the page (empty while the page applies).</summary>
    public string NotApplicableReason { get => _notApplicableReason; private set => Set(ref _notApplicableReason, value); }

    /// <summary>N301: show or hide the page for a case of this nominal frequency (fails loud on a non-finite nominal).</summary>
    private void ApplyNominal(double nominalHz)
    {
        bool applies = FrequencyRiskPresenter.Rp1AppliesAt(nominalHz);
        IsPageVisible = applies;
        IsNotApplicableVisible = !applies;
        NotApplicableReason = applies ? "" : FrequencyRiskPresenter.Rp1NotApplicableReason(nominalHz);
    }

    /// <summary>
    /// Break the timeline (model switch / entering replay): drop all bridge-sourced lake state and rolling
    /// histories. Without this, leaving bridge mode for a local case (e.g. case9) leaves GB-scale lake
    /// inertia, the BM/constraint/interconnector panels and the price/purchased-time traces on screen, graded
    /// against a 9-bus model and indistinguishable from live. Model-derived panels
    /// (GenStack/InertiaBySource/BoundaryBars) refresh from the next Update() and don't need clearing here.
    /// </summary>
    public void Reset()
    {
        _intrinsicLakeGvaS = _outturnGvaS = _marketGvaS = null;
        _icFlows.Clear();
        _trace.Clear();
        _price.Clear();
        _pricePeriod = null;   // D-DOC-200: a new timeline starts a new run of settlement periods
        _boundaryPrev.Clear();
        _largestUnitMw = 0;
        _largestUnitName = "-";
        // Without this, the narrative kept quoting the PREVIOUS model's top
        // boundary after switching to a case with no boundary model.
        _topBoundaryName = "";
        _topBoundaryLoading = 0;
        PriceTrace = System.Array.Empty<double>();
        StabilityTrace = System.Array.Empty<double>();
        ImbalancePrice = "-";
        BmSummary = "-";
        ConstraintCosts.Clear();
        BmDelta.Clear();
        Interconnectors.Clear();
    }

    /// <summary>
    /// Hand a drill-down page to the inspector. By default it raises <see cref="InspectRequested"/>; the WPF adapter
    /// overrides it to show the page in its <c>InspectorVm</c> (which keeps the back-stack), exactly as the WPF code did.
    /// </summary>
    protected virtual void ShowInspector(Rp1InspectPage page) => InspectRequested?.Invoke(page);

    // The WPF IR(...) helper: a row with the given values, the transparent swatch and the off-white value colour by
    // default (the WPF Brushes.Transparent and #E8F0F6).
    private static Rp1InspectRow IR(string primary, string value, UiColour? swatch = null, string sub = "",
        UiColour? valueBrush = null, Action? drill = null) => new()
    {
        Primary = primary, Value = value, Sub = sub,
        Swatch = swatch ?? DashboardColours.Transparent,
        ValueColour = valueBrush ?? WhiteB,
        Drill = drill,
    };

    /// <summary>Open the drill-down page of one fuel: its units this frame (the WPF <c>InspectFuelCommand</c>). A fuel
    /// with no unit online this frame does nothing, as before.</summary>
    public void InspectFuel(string fuel)
    {
        var gens = _lastGens.Where(g => g.Fuel == fuel).OrderByDescending(g => g.Mw).ToList();
        if (gens.Count == 0) return;
        double totMw = gens.Sum(g => g.Mw);
        double totKe = gens.Sum(g => g.KeGvaS);
        double avgLoad = gens.Where(g => g.Cap > 0).Select(g => g.Mw / g.Cap).DefaultIfEmpty(0).Average();
        double avgMw = totMw / gens.Count;
        bool sync = gens[0].Synchronous;
        var stats = new[]
        {
            new Rp1InspectStat { Label = "UNITS", Value = $"{gens.Count}" },
            new Rp1InspectStat { Label = "TOTAL OUTPUT", Value = $"{totMw:N0} MW" },
            new Rp1InspectStat { Label = "MEAN", Value = $"{avgMw:N0} MW" },
            new Rp1InspectStat { Label = "AVG LOADING", Value = $"{avgLoad * 100:F0}%" },
            new Rp1InspectStat { Label = "INERTIA", Value = sync ? $"{totKe:F1} GVA.s" : "0 * converter",
                              ValueColour = sync ? GreenB : MutedB },
        };
        // The WPF code handed the inspector a lazy Select, which its Show enumerated at once; the rows are built here
        // in the same order (largest output first), with the same text, and each drills into its unit.
        var rows = gens.Select(g => new Rp1InspectRow
        {
            Primary = g.Name,
            Value = $"{g.Mw:N0} MW",
            Sub = sync ? $"{g.KeGvaS:F2} GVA.s * H {g.H:F1}s * {(g.Cap > 0 ? g.Mw / g.Cap * 100 : 0):F0}% loaded"
                       : $"{(g.Cap > 0 ? g.Mw / g.Cap * 100 : 0):F0}% loaded * converter (0 inertia)",
            Swatch = g.Swatch,
            Drill = () => InspectUnit(g),   // drill into the individual unit
        }).ToList();
        ShowInspector(new Rp1InspectPage(fuel.ToUpperInvariant(),
            $"{(sync ? "synchronous - real intrinsic inertia" : "converter - zero intrinsic inertia")}  *  {gens.Count} units this frame",
            stats, rows));
    }

    private void InspectUnit(GenLite g)
    {
        double load = g.Cap > 0 ? g.Mw / g.Cap * 100 : 0;
        var stats = new[]
        {
            new Rp1InspectStat { Label = "OUTPUT", Value = $"{g.Mw:N0} MW" },
            new Rp1InspectStat { Label = "HEADROOM", Value = $"{System.Math.Max(0, g.Cap - g.Mw):N0} MW" },
            new Rp1InspectStat { Label = "LOADING", Value = $"{load:F0}%" },
            new Rp1InspectStat { Label = "INERTIA", Value = g.Synchronous ? $"{g.KeGvaS:F2} GVA.s" : "0",
                              ValueColour = g.Synchronous ? GreenB : MutedB },
        };
        var rows = new[]
        {
            IR("Fuel", g.Fuel, g.Swatch, drill: () => InspectFuel(g.Fuel)),   // back up to the group
            IR("Real power", $"{g.Mw:N0} MW"),
            IR("Headroom to capacity", $"{System.Math.Max(0, g.Cap - g.Mw):N0} MW"),
            IR("Inertia constant H", $"{g.H:F1} s"),
            IR("Stored kinetic energy", g.Synchronous ? $"{g.KeGvaS:F2} GVA.s" : "0 (converter)",
               valueBrush: g.Synchronous ? GreenB : MutedB),
            IR("Intrinsic?", g.Synchronous ? "yes - real synchronous mass" : "no - converter, zero inertia"),
        };
        ShowInspector(new Rp1InspectPage(g.Name.ToUpperInvariant(), $"{g.Fuel} unit  *  this frame", stats, rows));
    }

    /// <summary>Open the drill-down page of one balancing-mechanism acceptance (the WPF <c>InspectBmCommand</c>; a null
    /// row does nothing, as before).</summary>
    public void InspectBm(Rp1BmRowModel b)
    {
        if (b is null) return;
        var stats = new[]
        {
            new Rp1InspectStat { Label = "LEVEL", Value = b.Mw },
            new Rp1InspectStat { Label = "ACTION", Value = b.Kind, ValueColour = b.DirColour },
            new Rp1InspectStat { Label = "STABILITY", Value = b.Direction, ValueColour = b.DirColour },
        };
        var rows = new[]
        {
            IR("BM unit", b.Unit),
            IR("Fuel", b.Fuel),
            IR("Acceptance", b.Kind, valueBrush: b.DirColour),
            IR("Level", $"{b.Mw}"),
            IR("Inertia effect", b.Direction, valueBrush: b.DirColour),
            IR("Why", b.Direction.Contains("inertia")
                ? "a synchronous unit turning up/down changes real intrinsic inertia"
                : "a non-synchronous action changes MW but not intrinsic inertia"),
        };
        ShowInspector(new Rp1InspectPage(b.Unit.ToUpperInvariant(), $"balancing-mechanism acceptance  *  {b.Fuel}", stats, rows));
    }

    /// <summary>Open the drill-down page of one interconnector and its correlated-trip corridor (the WPF
    /// <c>InspectIcCommand</c>; a null row does nothing, as before).</summary>
    public void InspectIc(Rp1IcRowModel ic)
    {
        if (ic is null) return;
        string? icMember = InterconnectorCorridors.CanonicalMember(ic.Name);
        var corridors = Corridors.Where(c => icMember is not null && c.Members.Contains(icMember)).ToList();
        var stats = new[]
        {
            new Rp1InspectStat { Label = "FLOW", Value = ic.Flow, ValueColour = ic.FlowColour },
            new Rp1InspectStat { Label = "DIRECTION", Value = ic.Importing ? "IMPORT" : "EXPORT" },
            new Rp1InspectStat { Label = "CORRELATED", Value = ic.Correlated ? "yes" : "no",
                              ValueColour = ic.Correlated ? RedB : MutedB },
        };
        var rows = new List<Rp1InspectRow>
        {
            IR("Interconnector", ic.Name),
            IR("Instantaneous flow", ic.Flow, valueBrush: ic.FlowColour),
            IR("Infeed loss risk", ic.Importing ? "importing - a trip is a loss of infeed" : "exporting - not an infeed loss"),
        };
        foreach (var (name, members) in corridors)
        {
            rows.Add(IR($"Correlated group", name, valueBrush: RedB));
            foreach (string m in members.Where(m => m != icMember))
            {
                // Find the co-member's row by canonical id (its display name may be a long model name).
                Rp1IcRowModel? mem = Interconnectors.FirstOrDefault(x => InterconnectorCorridors.CanonicalMember(x.Name) == m);
                rows.Add(IR(m, mem?.Flow ?? "-", valueBrush: mem?.FlowColour,
                    drill: mem is null ? null : () => InspectIc(mem)));   // hop to the co-member
            }
        }
        ShowInspector(new Rp1InspectPage(ic.Name.ToUpperInvariant(),
            corridors.Count > 0 ? "shares a correlated-trip corridor" : "no shared corridor", stats, rows));
    }

    // boundary context for the narrative (top boundary + trend); the RIGHT column fills the rest later.
    private string _topBoundaryName = ""; private double _topBoundaryLoading;

    /// <summary>Per-frame refresh from the solved frame + evaluated boundaries.</summary>
    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, string clock)
    {
        Clock = clock;
        ApplyNominal(f.Model.FrequencyHz);   // N301: hide the GB page on a case whose nominal is not 50 Hz
        _currentHz = f.FrequencyHz;
        // (2026-10-24: the frame's RoCoF was latched here into a field nothing read; the dead state is removed.)

        // one pass over generators: per-fuel intrinsic mass (inertia-by-source) and the full dispatch stack (LEFT
        // column). The largest online single infeed (the secured credible loss; any technology since N039) comes from the shared Core
        // presenter below, the same call the web FuelBreakdown makes (Q1, 2026-10).
        var perFuel = new Dictionary<string, (double eMwS, UiFuelInfo fi)>();
        var stack = new Dictionary<string, (double outMw, double capMw, double eMwS, UiFuelInfo fi)>();
        double intrinsicMwS = 0, syncMw = 0, convMw = 0;
        _lastGens.Clear();
        foreach (Generator g in f.Model.Generators)
        {
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            double mw = System.Math.Max(0, g.Pg);
            double cap = double.IsInfinity(g.Pmax) ? mw : System.Math.Max(g.Pmax, mw);
            // The rotating mass this unit contributes, by the ONE model rule the header INERTIA tile uses
            // (GridModel.RotatingInertiaMwS, N289: every unit not of a KNOWN converter type, so an untyped MATPOWER unit
            // with H counts). This tab used FuelInfo.Synchronous, which is false for "Other" (untyped) units, so on a
            // case with H data and no type strings INERTIA read the full fleet while RP1's intrinsic inertia read 0.
            double e = GridModel.RotatingInertiaMwS(g);      // MW.s of stored rotating energy (0 for a converter)
            if (mw > 0)
                _lastGens.Add(new GenLite(g.Name ?? fi.Name, fi.Name, mw, cap, g.H,
                    e / 1000.0, fi.Synchronous, fi.Colour));
            stack.TryGetValue(fi.Name, out var s);
            stack[fi.Name] = (s.outMw + mw, s.capMw + cap, s.eMwS + e, fi);
            if (e > 0)
            {
                intrinsicMwS += e;
                perFuel.TryGetValue(fi.Name, out var cur);
                perFuel[fi.Name] = (cur.eMwS + e, fi);
            }
            // The MW split stays by the fuel's own synchronous flag (it is an output split, not an inertia figure).
            if (fi.Synchronous) syncMw += mw;
            else convMw += mw;
        }
        (_largestUnitMw, _largestUnitName) = FrequencyRiskPresenter.LargestSecuredUnit(f.Model.Generators);
        _intrinsicModelGvaS = intrinsicMwS / 1000.0;
        _perFuelIntrinsic = perFuel;
        SynchronousGw = $"{syncMw / 1000.0:F1} GW";
        ConverterGw = $"{convMw / 1000.0:F1} GW";
        SynchronousFrac = syncMw + convMw > 0 ? syncMw / (syncMw + convMw) * 100 : 0;
        SyncGenStack(stack, syncMw + convMw);

        if (boundaries.Count > 0)
        {
            BoundaryFlow top = boundaries.OrderByDescending(b => b.LoadingPercent).First();
            _topBoundaryName = top.Name; _topBoundaryLoading = top.LoadingPercent;
        }
        else
        {
            // No boundary model on this case: clear the handover sentence so
            // the narrative can't contradict SyncBoundaryBars' "no boundary
            // model" note with the previous case's figures.
            _topBoundaryName = ""; _topBoundaryLoading = 0;
        }
        SyncBoundaryBars(boundaries);

        Recompute();
    }

    private Dictionary<string, (double eMwS, UiFuelInfo fi)> _perFuelIntrinsic = new();

    /// <summary>
    /// Feed the lake-streamed blocks (side-channel, like the OPERATOR tab): the intrinsic/market inertia
    /// split and the interconnector flows that drive the correlated credible loss.
    /// <paramref name="tickUtc"/> is the bridge tick's UTC instant (D-DOC-200): with it the price trace takes one
    /// sample per settlement period, as MARKET does; without it (no dated tick) every price is a sample.
    /// </summary>
    public void SetRp1Data(Proto.OperatorBlock? op, Proto.MarketBlock? mk, System.DateTime? tickUtc = null)
    {
        if (op is not null)
        {
            _intrinsicLakeGvaS = op.SynchronousInertiaGvaS;
            _outturnGvaS = op.OutturnInertiaGvaS;
            _marketGvaS = op.MarketInertiaGvaS;
        }
        if (mk?.Interconnectors is { } ics)
        {
            _icFlows.Clear();
            foreach (Proto.InterconnectorDto ic in ics)
                // Idle links (|flow| below the shared 0.5 MW Q10 cut-off) are no infeed to lose: left out, as on the web.
                if (ic.Name is { } n && ic.FlowMw is double flow && !StatusLampRules.IsIdleInterconnector(flow)) _icFlows[n] = flow;
        }
        SyncConstraintCosts(op?.Constraints);
        SyncBmDelta(op?.BalancingActions);
        SyncInterconnectors(mk?.Interconnectors);
        if (mk?.ImbalancePriceGbpMwh is double px)
        {
            ImbalancePrice = $"GBP {px:F2}";
            bool changed = tickUtc is System.DateTime at
                ? ImbalancePriceTrace.Append(_price, px, at, ref _pricePeriod, PriceTraceLen)
                : ImbalancePriceTrace.Append(_price, px, PriceTraceLen);
            if (changed) PriceTrace = _price.ToArray();
        }
        Recompute(advanceTrace: false);   // the state path (Update) owns the trace cadence; don't double-fill it
    }

    private void SyncBoundaryBars(IReadOnlyList<BoundaryFlow> boundaries)
    {
        if (boundaries.Count == 0)
        {
            BoundaryBars.Clear();
            BoundaryNote = "no boundary model for this case - constraint cost from the lake below";
            return;
        }
        BoundaryNote = "";
        var ordered = boundaries.OrderByDescending(b => b.LoadingPercent).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            BoundaryFlow b = ordered[i];
            Rp1BoundaryRowModel row = i < BoundaryBars.Count ? BoundaryBars[i] : Add(BoundaryBars, CreateBoundaryRow());
            row.Name = b.Name;
            row.Transfer = FormatGwLocal(b.FlowMw);
            row.Limit = FormatGwLocal(b.LimitMw);
            row.LoadingPercent = b.LoadingPercent;
            row.Loading = $"{b.LoadingPercent:F0}%";
            row.LoadingColour = ColourRamps.Loading(b.LoadingPercent / 100.0);
            // trend arrow from the previous loading for this boundary
            double prev = _boundaryPrev.TryGetValue(b.Name, out double pv) ? pv : b.LoadingPercent;
            row.Trend = b.LoadingPercent > prev + BoundaryTrendBandPct ? "^" : b.LoadingPercent < prev - BoundaryTrendBandPct ? "v" : "=";
            row.TrendColour = b.LoadingPercent > prev + BoundaryTrendBandPct ? RedB : b.LoadingPercent < prev - BoundaryTrendBandPct ? GreenB : MutedB;
            _boundaryPrev[b.Name] = b.LoadingPercent;
        }
        while (BoundaryBars.Count > ordered.Count) BoundaryBars.RemoveAt(BoundaryBars.Count - 1);
    }

    private void SyncConstraintCosts(List<Proto.ConstraintDto>? cons)
    {
        ConstraintCosts.Clear();
        if (cons is null) return;
        foreach (Proto.ConstraintDto c in cons.Take(ConstraintRowCap))
        {
            // The WPF code built the row with an object initialiser and then added it; the same values are set here, in
            // the same order, on the row the front end makes, before it is added.
            Rp1ConstraintRowModel row = CreateConstraintRow();
            row.Name = c.Name ?? "-";
            // the lake carries the day's constraint cost; show it as an hourly rate.
            row.CostPerHour = c.CostGbp is double cost ? $"GBP {cost / HoursPerDay / 1000.0:F1}k/h" : "-";
            row.Binding = c.Binding ?? false;
            row.BindingColour = (c.Binding ?? false) ? RedB : MutedB;
            ConstraintCosts.Add(row);
        }
    }

    private void SyncBmDelta(List<Proto.BalancingActionDto>? actions)
    {
        BmDelta.Clear();
        int improving = 0, reducing = 0, neutral = 0;
        if (actions is not null)
            foreach (Proto.BalancingActionDto a in actions.Take(BalancingActionRowCap))
            {
                bool offer = (a.Kind ?? "").StartsWith("OFFER", System.StringComparison.OrdinalIgnoreCase);
                string dir; UiColour brush;
                if (a.Synchronous == true) { if (offer) { dir = "^ inertia"; brush = GreenB; improving++; } else { dir = "v inertia"; brush = RedB; reducing++; } }
                else if (a.Synchronous == false) { dir = "neutral"; brush = MutedB; neutral++; }
                else { dir = "watch"; brush = AmberB; }
                Rp1BmRowModel row = CreateBmRow();
                row.Unit = a.BmUnit ?? "-";
                row.Fuel = a.FuelType ?? "-";
                row.Kind = a.Kind ?? "-";
                row.Mw = $"{a.LevelMw ?? 0:F0} MW";
                row.Direction = dir;
                row.DirColour = brush;
                BmDelta.Add(row);
            }
        BmSummary = $"{improving} improving * {reducing} reducing * {neutral} neutral";
    }

    private void SyncInterconnectors(List<Proto.InterconnectorDto>? ics)
    {
        Interconnectors.Clear();
        if (ics is null) return;
        foreach (Proto.InterconnectorDto ic in ics.OrderByDescending(x => System.Math.Abs(x.FlowMw ?? 0)))
        {
            double flow = ic.FlowMw ?? 0;
            if (StatusLampRules.IsIdleInterconnector(flow)) continue;   // shared Q10 idle cut-off (0.5 MW), as on the web
            bool correlated = InterconnectorCorridors.CorridorOf(ic.Name) is not null;   // canonical-id match (Core)
            Rp1IcRowModel row = CreateIcRow();
            row.Name = ic.Name ?? "-";
            row.Flow = $"{flow:+0;-0;0} MW";
            row.Importing = flow > 0;
            row.FlowColour = flow > 0 ? AmberB : MutedB;   // importing = an infeed that can be lost
            row.Correlated = correlated;
            row.FlagColour = correlated ? RedB : DashboardColours.Transparent;
            Interconnectors.Add(row);
        }
    }

    private static string FormatGwLocal(double mw) => System.Math.Abs(mw) >= 1000 ? $"{mw / 1000.0:F2} GW" : $"{mw:F0} MW";

    private void Recompute(bool advanceTrace = true)
    {
        double intrinsic = _intrinsicLakeGvaS ?? _intrinsicModelGvaS;   // prefer the lake's bottom-up GVA.s(i)
        double market = _marketGvaS ?? 0;
        double total = intrinsic + market;   // fuel gauge: total liquid = petrol (i) + water (p), always >= each

        // ---- credible loss: the largest single secured unit (Core presenter, shared with the web) ----
        // FRCR 2025 (NESO, March 2025) secures all BMU-only events and does not secure simultaneous events (Executive
        // summary p.4, s.6.2, s.7.1), so the correlated HVDC group is live context, never the credible loss.
        // _icFlows is keyed by the lake's link names; fold them onto canonical corridor member ids first.
        StabilityMetrics.GroupLoss grp = StabilityMetrics.LargestCorrelatedGroup(
            Corridors, InterconnectorCorridors.MemberFlows(_icFlows.Select(kv => (kv.Key, kv.Value))));
        FrequencyRiskPresenter.CredibleLossView loss = FrequencyRiskPresenter.CredibleLoss(_largestUnitMw, _largestUnitName, grp);
        double liveLoss = loss.SecuredLossMw;
        // The unit is the credible loss whenever one is online; the group is highlighted only when it would be the
        // bigger (unsecured) loss, as the web RP1 card does.
        _unitBinding = loss.SecuredLossMw > 0;
        _groupBinding = loss.CorrelatedGroupExceedsSecured;
        RaiseAll(nameof(UnitBinding), nameof(GroupBinding));
        UnitColour = _unitBinding ? WhiteB : MutedB;
        GroupColour = _groupBinding ? WhiteB : MutedB;
        LargestUnit = _largestUnitMw > 0 ? $"{_largestUnitMw:N0} MW" : "-";
        LargestUnitLabel = _largestUnitName == "-" ? "largest unit online" : $"largest unit  *  {_largestUnitName}";
        LargestGroup = grp.Mw > 0 ? $"{grp.Mw:N0} MW" : "-";
        LargestGroupLabel = string.IsNullOrEmpty(grp.Name) ? "largest correlated group (not secured)" : $"{grp.Name} (not secured)";
        DesignBasis = $"{CredibleLossBasis.DesignBasisLossMw:N0} MW  *  {CredibleLossBasis.DesignBasisSource}";
        Fiction = $"{CredibleLossBasis.FictionLossMw:N0} MW";   // N040: FRCR 2025 future largest loss, not a "convention"

        // ---- hero: purchased time against the live loss; design-basis figure alongside ----
        double tLive = StabilityMetrics.PurchasedTimeSeconds(intrinsic, liveLoss, _currentHz);
        double tDesign = StabilityMetrics.PurchasedTimeSeconds(intrinsic, CredibleLossBasis.DesignBasisLossMw, _currentHz);
        PurchasedValue = double.IsInfinity(tLive) ? PurchasedTimeDisplayCapS : System.Math.Min(tLive, PurchasedTimeDisplayCapS);
        PurchasedText = FormatSeconds(tLive);
        // Hero colour: the Core ladder's level without the trend escalation (green >= 5 s, amber >= 2 s, else red).
        PurchasedColour = LevelBrush(FrequencyRiskPresenter.GradePurchasedTime(tLive, tightening: false).Level);
        // R212: formatted from the design-basis constant two lines above, not a hard-coded "3,600 MW".
        PurchasedDesign = $"{FormatSeconds(tDesign)}  @ {CredibleLossBasis.DesignBasisLossMw:N0} MW";

        // ---- inertia gauges (GVA.s) ----
        InertiaTotalValue = total; InertiaTotalText = $"{total:F0}";
        InertiaIntrinsicValue = intrinsic; InertiaIntrinsicText = $"{intrinsic:F0}";
        InertiaProxyValue = market; InertiaProxyText = $"{market:F0}";

        // ---- dynamic MIF + RoCoF readouts ----
        double mif = StabilityMetrics.DynamicMifGvaS(liveLoss, FrequencyLimits.RocofLimitLegacyHzPerS);
        DynamicMif = $"{mif:F0} GVA.s";
        double rocof = StabilityMetrics.RocofFor(intrinsic, liveLoss);
        Rocof = $"{rocof:F3} Hz/s";

        // ---- inertia by source (each fuel's share of intrinsic -> its seconds of purchased time) ----
        SyncInertiaBySource(intrinsic, tLive);

        // ---- stability trend (rolling purchased time) ----
        // Advance the rolling trace only from the state path. In bridge mode BOTH Update and
        // SetRp1Data fire per tick and each called Recompute, so the trace filled twice per tick -
        // halving the trend/LFDD window. Recompute from SetRp1Data refreshes derived values without
        // advancing the trace.
        if (advanceTrace) _trace.Add(tLive);   // Core trace: +infinity is stored at the 10 s display cap
        StabilityTrace = _trace.Points.ToArray();

        // ---- LFDD risk (the shared Core ladder, escalated by a tightening trend) ----
        SetLfdd(tLive, _trace.IsTightening);

        // ---- the handover sentence ----
        BuildNarrative(total, intrinsic, tLive, liveLoss);
    }

    private void SyncInertiaBySource(double intrinsicGvaS, double purchasedSeconds)
    {
        double eTotal = _perFuelIntrinsic.Values.Sum(v => v.eMwS);
        double secs = double.IsInfinity(purchasedSeconds) ? PurchasedTimeDisplayCapS : purchasedSeconds;
        var ordered = _perFuelIntrinsic.OrderByDescending(kv => kv.Value.eMwS).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (name, val) = (ordered[i].Key, ordered[i].Value);
            Rp1InertiaRowModel row = i < InertiaBySource.Count ? InertiaBySource[i] : Add(InertiaBySource, CreateInertiaRow());
            double share = eTotal > 0 ? val.eMwS / eTotal : 0;
            row.Fuel = name;
            row.SwatchColour = val.fi.Colour;
            row.Seconds = $"{share * secs:F1} s";
            row.SharePercent = share * 100;
        }
        while (InertiaBySource.Count > ordered.Count) InertiaBySource.RemoveAt(InertiaBySource.Count - 1);
    }

    private void SyncGenStack(Dictionary<string, (double outMw, double capMw, double eMwS, UiFuelInfo fi)> stack, double totalMw)
    {
        var ordered = stack.OrderByDescending(kv => kv.Value.outMw).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (name, v) = (ordered[i].Key, ordered[i].Value);
            Rp1GenRowModel row = i < GenStack.Count ? GenStack[i] : Add(GenStack, CreateGenRow());
            row.Fuel = name;
            row.SwatchColour = v.fi.Colour;
            row.Output = $"{v.outMw:N0} MW";
            row.OutputPercent = totalMw > 0 ? v.outMw / totalMw * 100 : 0;
            double headroom = v.capMw - v.outMw;
            row.Headroom = headroom > 1 ? $"+{headroom:N0}" : "-";
            row.Inertia = v.fi.Synchronous ? $"{v.eMwS / 1000.0:F1} GVA.s" : "0";
            row.Kind = v.fi.Synchronous ? "SYNC" : "CONV";
            row.KindColour = v.fi.Synchronous ? GreenB : MutedB;
        }
        while (GenStack.Count > ordered.Count) GenStack.RemoveAt(GenStack.Count - 1);
    }

    private void SetLfdd(double purchasedSeconds, bool tightening)
    {
        // normal / watch / advisory / warning, from purchased time against the human-response floor: the ONE Core
        // ladder shared with the web RP1 tab (Q1, 2026-10). A falling trend below NORMAL escalates it to red " down".
        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(purchasedSeconds, tightening);
        LfddGrade = g.Text; LfddColour = LevelBrush(g.Level);
    }

    /// <summary>The colour for a Core grade level. An unknown level is a programming error, so it throws.</summary>
    private static UiColour LevelBrush(FrequencyRiskPresenter.GradeLevel level) => level switch
    {
        FrequencyRiskPresenter.GradeLevel.Green => GreenB,
        FrequencyRiskPresenter.GradeLevel.Amber => AmberB,
        FrequencyRiskPresenter.GradeLevel.Red => RedB,
        _ => throw new System.ArgumentOutOfRangeException(nameof(level), level, "Unknown grade level."),
    };

    private void BuildNarrative(double total, double intrinsic, double purchased, double loss)
    {
        string t = double.IsInfinity(purchased) ? $">{PurchasedTimeDisplayCapS:F0} s" : $"{purchased:F1} s";
        string boundary = string.IsNullOrEmpty(_topBoundaryName)
            ? ""
            : $" {_topBoundaryName} loading {_topBoundaryLoading:F0}%.";
        Narrative =
            $"{Clock} - System carrying {total:F0} GVA.s ({intrinsic:F0} intrinsic), purchased time {t} " +
            $"against a {loss:N0} MW credible loss.{boundary} " +
            $"Correlated cluster {CredibleLossBasis.DesignBasisLossMw:N0} MW (Dogger Bank, not secured); " +
            $"FRCR 2025 future largest loss {CredibleLossBasis.FictionLossMw:N0} MW.";
    }

    private void RaiseAll(params string[] names) { foreach (string n in names) Raise(n); }
    private static T Add<T>(ObservableCollection<T> col, T item) { col.Add(item); return item; }
    private static string FormatSeconds(double s) => double.IsInfinity(s) ? $">{PurchasedTimeDisplayCapS:F0} s" : $"{s:F1} s";

    // ---- the rows (a front end overrides these to make its own row types) ----

    protected virtual Rp1InertiaRowModel CreateInertiaRow() => new();
    protected virtual Rp1GenRowModel CreateGenRow() => new();
    protected virtual Rp1BoundaryRowModel CreateBoundaryRow() => new();
    protected virtual Rp1ConstraintRowModel CreateConstraintRow() => new();
    protected virtual Rp1BmRowModel CreateBmRow() => new();
    protected virtual Rp1IcRowModel CreateIcRow() => new();
}

/// <summary>One fuel's share of intrinsic inertia, in seconds of purchased time (the WPF <c>Rp1InertiaRow</c>, with
/// <c>Swatch</c> now the colour token <see cref="SwatchColour"/>, default <c>Brushes.Gray</c>).</summary>
public class Rp1InertiaRowModel : ObservableModel
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private UiColour _swatchColour = DashboardColours.Gray; public UiColour SwatchColour { get => _swatchColour; set => Set(ref _swatchColour, value); }
    private string _seconds = ""; public string Seconds { get => _seconds; set => Set(ref _seconds, value); }
    private double _sharePercent; public double SharePercent { get => _sharePercent; set => Set(ref _sharePercent, value); }
}

/// <summary>One fuel of the dispatch stack (the WPF <c>Rp1GenRow</c>, with <c>Swatch</c> and <c>KindBrush</c> now the
/// colour tokens <see cref="SwatchColour"/> and <see cref="KindColour"/>, default <c>Brushes.Gray</c>).</summary>
public class Rp1GenRowModel : ObservableModel
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private UiColour _swatchColour = DashboardColours.Gray; public UiColour SwatchColour { get => _swatchColour; set => Set(ref _swatchColour, value); }
    private string _output = ""; public string Output { get => _output; set => Set(ref _output, value); }
    private double _outputPercent; public double OutputPercent { get => _outputPercent; set => Set(ref _outputPercent, value); }
    private string _headroom = ""; public string Headroom { get => _headroom; set => Set(ref _headroom, value); }
    private string _inertia = ""; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private UiColour _kindColour = DashboardColours.Gray; public UiColour KindColour { get => _kindColour; set => Set(ref _kindColour, value); }
}

/// <summary>One boundary transfer against its limit, with its trend (the WPF <c>Rp1BoundaryRow</c>, with
/// <c>LoadingBrush</c> and <c>TrendBrush</c> now the colour tokens <see cref="LoadingColour"/> and
/// <see cref="TrendColour"/>, default <c>Brushes.Gray</c>).</summary>
public class Rp1BoundaryRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _transfer = ""; public string Transfer { get => _transfer; set => Set(ref _transfer, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private double _loadingPercent; public double LoadingPercent { get => _loadingPercent; set => Set(ref _loadingPercent, value); }
    private UiColour _loadingColour = DashboardColours.Gray; public UiColour LoadingColour { get => _loadingColour; set => Set(ref _loadingColour, value); }
    private string _trend = ""; public string Trend { get => _trend; set => Set(ref _trend, value); }
    private UiColour _trendColour = DashboardColours.Gray; public UiColour TrendColour { get => _trendColour; set => Set(ref _trendColour, value); }
}

/// <summary>One lake-streamed constraint and its hourly cost (the WPF <c>Rp1ConstraintRow</c>, with
/// <c>BindingBrush</c> now the colour token <see cref="BindingColour"/>, default <c>Brushes.Gray</c>).</summary>
public class Rp1ConstraintRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _costPerHour = ""; public string CostPerHour { get => _costPerHour; set => Set(ref _costPerHour, value); }
    private bool _binding; public bool Binding { get => _binding; set => Set(ref _binding, value); }
    private UiColour _bindingColour = DashboardColours.Gray; public UiColour BindingColour { get => _bindingColour; set => Set(ref _bindingColour, value); }
}

/// <summary>One balancing-mechanism acceptance and its effect on inertia (the WPF <c>Rp1BmRow</c>, with
/// <c>DirBrush</c> now the colour token <see cref="DirColour"/>, default <c>Brushes.Gray</c>).</summary>
public class Rp1BmRowModel : ObservableModel
{
    private string _unit = ""; public string Unit { get => _unit; set => Set(ref _unit, value); }
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _direction = ""; public string Direction { get => _direction; set => Set(ref _direction, value); }
    private UiColour _dirColour = DashboardColours.Gray; public UiColour DirColour { get => _dirColour; set => Set(ref _dirColour, value); }
}

/// <summary>One interconnector's flow and correlated-trip flag (the WPF <c>Rp1IcRow</c>, with <c>FlowBrush</c> and
/// <c>FlagBrush</c> now the colour tokens <see cref="FlowColour"/> (default <c>Brushes.Gray</c>) and
/// <see cref="FlagColour"/> (default <c>Brushes.Transparent</c>)).</summary>
public class Rp1IcRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private bool _importing; public bool Importing { get => _importing; set => Set(ref _importing, value); }
    private UiColour _flowColour = DashboardColours.Gray; public UiColour FlowColour { get => _flowColour; set => Set(ref _flowColour, value); }
    private bool _correlated; public bool Correlated { get => _correlated; set => Set(ref _correlated, value); }
    private UiColour _flagColour = DashboardColours.Transparent; public UiColour FlagColour { get => _flagColour; set => Set(ref _flagColour, value); }
}

/// <summary>
/// A headline combined / averaged figure on a drill-down page (the WPF <c>InspectStat</c> the RP1 tab built, with
/// <c>ValueBrush</c> now the colour token <see cref="ValueColour"/>, default the WPF <c>Brushes.White</c>).
/// </summary>
public sealed class Rp1InspectStat
{
    /// <summary>The WPF <c>Brushes.White</c>: the default value colour of a stat tile and of a row made directly.</summary>
    public static readonly UiColour White = new(0xFF, 0xFF, 0xFF, 0xFF);

    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public UiColour ValueColour { get; init; } = White;
}

/// <summary>
/// One constituent row on a drill-down page (the WPF <c>InspectRow</c> the RP1 tab built, with <c>Swatch</c> and
/// <c>ValueBrush</c> now the colour tokens <see cref="Swatch"/> (default the WPF <c>Brushes.Transparent</c>) and
/// <see cref="ValueColour"/> (default <c>Brushes.White</c>), and its WPF <c>Drill</c> command a plain
/// <see cref="Action"/>; <see cref="HasDrill"/> stands for the WPF <c>Chevron</c> Visibility).
/// </summary>
public sealed class Rp1InspectRow
{
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public UiColour Swatch { get; init; } = DashboardColours.Transparent;
    public UiColour ValueColour { get; init; } = Rp1InspectStat.White;

    /// <summary>What a click on the row does (drill further), or null when the row is not a link.</summary>
    public Action? Drill { get; init; }

    /// <summary>True when the row drills further (the WPF chevron was Visible), false when not (Collapsed).</summary>
    public bool HasDrill => Drill is not null;
}

/// <summary>One drill-down page: its title, subtitle, stat tiles and rows, in the order the WPF inspector showed them.</summary>
public sealed record Rp1InspectPage(string Title, string Subtitle, IReadOnlyList<Rp1InspectStat> Stats, IReadOnlyList<Rp1InspectRow> Rows);
