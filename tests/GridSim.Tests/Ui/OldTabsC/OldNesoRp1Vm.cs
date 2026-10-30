// Test oracle for UiTabsThirdBatchParityTests (UI programme Phase 1.6, third tab batch). NOT production code.
//
// Below is the WPF NesoRp1Vm exactly as it was before its logic moved to src/GridSim.Ui (src/GridSim.Wpf/Views/NesoRp1Vm.cs at 2.0.28), copied
// mechanically from git. The only edits are:
//   - the lines "using System.Windows.Media;", "using GridSim.Wpf.Views.Assets;   // RelayCommand" and
//     "using GridSim.Wpf.Visuals;" removed (the shims in OldTabsCShims.cs stand in) (3x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabsC (1x);
//   - "System.Windows.Visibility" written "Visibility" (the shim) (10x).
// "using System.Windows.Input;" stays: ICommand is the base class library one. Every formatting call, culture, branch
// and ordering is the old one, so it is the oracle for every displayed value.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using Proto = GridSim.Core.IO.BridgeProtocol;

namespace GridSim.Tests.Ui.OldTabsC;

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
/// </summary>
public sealed class NesoRp1Vm : Notify
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
    private double _rocofFrame;
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

    private static readonly Brush GreenB = Palette.Frozen(Palette.FromHex("#2FD27F"));
    private static readonly Brush AmberB = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush RedB = Palette.Frozen(Palette.FromHex("#FF5B5B"));
    private static readonly Brush MutedB = Palette.Frozen(Palette.FromHex("#8492A0"));
    private static readonly Brush WhiteB = Palette.Frozen(Palette.FromHex("#E8F0F6"));

    // ---- hero: purchased time ----
    private double _purchasedValue; public double PurchasedValue { get => _purchasedValue; set => Set(ref _purchasedValue, value); }
    private string _purchasedText = "-"; public string PurchasedText { get => _purchasedText; set => Set(ref _purchasedText, value); }
    private Brush _purchasedBrush = Brushes.Gray; public Brush PurchasedBrush { get => _purchasedBrush; set => Set(ref _purchasedBrush, value); }
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
    private Brush _unitBrush = MutedB; public Brush UnitBrush { get => _unitBrush; set => Set(ref _unitBrush, value); }
    private Brush _groupBrush = MutedB; public Brush GroupBrush { get => _groupBrush; set => Set(ref _groupBrush, value); }

    // ---- inertia by source (per-fuel contribution, in seconds of purchased time) ----
    public ObservableCollection<Rp1InertiaRow> InertiaBySource { get; } = new();

    // ---- LEFT column: the dispatch stack (per-fuel output / headroom / inertia, sync vs converter) ----
    public ObservableCollection<Rp1GenRow> GenStack { get; } = new();
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
    private Brush _lfddBrush = Brushes.Gray; public Brush LfddBrush { get => _lfddBrush; set => Set(ref _lfddBrush, value); }

    // ---- RIGHT column: network constraint & coherence cost ----
    public ObservableCollection<Rp1BoundaryRow> BoundaryBars { get; } = new();
    public ObservableCollection<Rp1ConstraintRow> ConstraintCosts { get; } = new();
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
    public ObservableCollection<Rp1BmRow> BmDelta { get; } = new();
    public ObservableCollection<Rp1IcRow> Interconnectors { get; } = new();
    private string _bmSummary = "-"; public string BmSummary { get => _bmSummary; set => Set(ref _bmSummary, value); }

    // ---- drill-down inspector (click a fuel / BM unit / interconnector to see how it is made up) ----
    public InspectorVm Inspector { get; } = new();
    private readonly List<GenLite> _lastGens = new();
    private readonly record struct GenLite(string Name, string Fuel, double Mw, double Cap, double H, double KeGvaS, bool Synchronous, Brush Swatch);
    public ICommand InspectFuelCommand { get; }
    public ICommand InspectBmCommand { get; }
    public ICommand InspectIcCommand { get; }

    // ---- system narrative (the handover sentence) ----
    private string _narrative = "-"; public string Narrative { get => _narrative; set => Set(ref _narrative, value); }
    private string _clock = "-"; public string Clock { get => _clock; set => Set(ref _clock, value); }

    // ---- N301 (October 2026, stream LF3; Ruling R1): where the page applies ----
    // RP1 is a NESO GB 50 Hz product page (purchased time to the absolute 49.2 Hz, GB RoCoF / MIF). On a case whose
    // nominal is not 50 Hz the page body is collapsed and the shared one-line reason shown instead (the same rule and
    // text the web Rp1Tab uses, FrequencyRiskPresenter.Rp1AppliesAt / Rp1NotApplicableReason). Every figure below is
    // still computed exactly as before; only what the view shows changes. Starts visible (the GB default) until the
    // first frame says otherwise.
    private Visibility _pageVisibility = Visibility.Visible;
    /// <summary>N301: Visible when RP1 applies to the current case (nominal exactly 50 Hz), else Collapsed.</summary>
    public Visibility PageVisibility { get => _pageVisibility; private set => Set(ref _pageVisibility, value); }
    private Visibility _notApplicableVisibility = Visibility.Collapsed;
    /// <summary>N301: Visible (with <see cref="NotApplicableReason"/>) when RP1 does not apply to the current case.</summary>
    public Visibility NotApplicableVisibility { get => _notApplicableVisibility; private set => Set(ref _notApplicableVisibility, value); }
    private string _notApplicableReason = "";
    /// <summary>N301: the one-line reason shown in place of the page (empty while the page applies).</summary>
    public string NotApplicableReason { get => _notApplicableReason; private set => Set(ref _notApplicableReason, value); }

    /// <summary>N301: show or hide the page for a case of this nominal frequency (fails loud on a non-finite nominal).</summary>
    private void ApplyNominal(double nominalHz)
    {
        bool applies = FrequencyRiskPresenter.Rp1AppliesAt(nominalHz);
        PageVisibility = applies ? Visibility.Visible : Visibility.Collapsed;
        NotApplicableVisibility = applies ? Visibility.Collapsed : Visibility.Visible;
        NotApplicableReason = applies ? "" : FrequencyRiskPresenter.Rp1NotApplicableReason(nominalHz);
    }

    public NesoRp1Vm()
    {
        InspectFuelCommand = new RelayCommand<string>(InspectFuel);
        InspectBmCommand = new RelayCommand<Rp1BmRow>(InspectBm);
        InspectIcCommand = new RelayCommand<Rp1IcRow>(InspectIc);
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

    private static InspectRow IR(string primary, string value, Brush? swatch = null, string sub = "",
        Brush? valueBrush = null, ICommand? drill = null) => new()
    {
        Primary = primary, Value = value, Sub = sub,
        Swatch = swatch ?? Brushes.Transparent,
        ValueBrush = valueBrush ?? WhiteB,
        Drill = drill,
    };

    private void InspectFuel(string fuel)
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
            new InspectStat { Label = "UNITS", Value = $"{gens.Count}" },
            new InspectStat { Label = "TOTAL OUTPUT", Value = $"{totMw:N0} MW" },
            new InspectStat { Label = "MEAN", Value = $"{avgMw:N0} MW" },
            new InspectStat { Label = "AVG LOADING", Value = $"{avgLoad * 100:F0}%" },
            new InspectStat { Label = "INERTIA", Value = sync ? $"{totKe:F1} GVA.s" : "0 * converter",
                              ValueBrush = sync ? GreenB : MutedB },
        };
        var rows = gens.Select(g => new InspectRow
        {
            Primary = g.Name,
            Value = $"{g.Mw:N0} MW",
            Sub = sync ? $"{g.KeGvaS:F2} GVA.s * H {g.H:F1}s * {(g.Cap > 0 ? g.Mw / g.Cap * 100 : 0):F0}% loaded"
                       : $"{(g.Cap > 0 ? g.Mw / g.Cap * 100 : 0):F0}% loaded * converter (0 inertia)",
            Swatch = g.Swatch,
            Drill = new RelayCommand(() => InspectUnit(g)),   // drill into the individual unit
        });
        Inspector.Show(fuel.ToUpperInvariant(),
            $"{(sync ? "synchronous - real intrinsic inertia" : "converter - zero intrinsic inertia")}  *  {gens.Count} units this frame",
            stats, rows);
    }

    private void InspectUnit(GenLite g)
    {
        double load = g.Cap > 0 ? g.Mw / g.Cap * 100 : 0;
        var stats = new[]
        {
            new InspectStat { Label = "OUTPUT", Value = $"{g.Mw:N0} MW" },
            new InspectStat { Label = "HEADROOM", Value = $"{System.Math.Max(0, g.Cap - g.Mw):N0} MW" },
            new InspectStat { Label = "LOADING", Value = $"{load:F0}%" },
            new InspectStat { Label = "INERTIA", Value = g.Synchronous ? $"{g.KeGvaS:F2} GVA.s" : "0",
                              ValueBrush = g.Synchronous ? GreenB : MutedB },
        };
        var rows = new[]
        {
            IR("Fuel", g.Fuel, g.Swatch, drill: new RelayCommand(() => InspectFuel(g.Fuel))),   // back up to the group
            IR("Real power", $"{g.Mw:N0} MW"),
            IR("Headroom to capacity", $"{System.Math.Max(0, g.Cap - g.Mw):N0} MW"),
            IR("Inertia constant H", $"{g.H:F1} s"),
            IR("Stored kinetic energy", g.Synchronous ? $"{g.KeGvaS:F2} GVA.s" : "0 (converter)",
               valueBrush: g.Synchronous ? GreenB : MutedB),
            IR("Intrinsic?", g.Synchronous ? "yes - real synchronous mass" : "no - converter, zero inertia"),
        };
        Inspector.Show(g.Name.ToUpperInvariant(), $"{g.Fuel} unit  *  this frame", stats, rows);
    }

    private void InspectBm(Rp1BmRow b)
    {
        if (b is null) return;
        var stats = new[]
        {
            new InspectStat { Label = "LEVEL", Value = b.Mw },
            new InspectStat { Label = "ACTION", Value = b.Kind, ValueBrush = b.DirBrush },
            new InspectStat { Label = "STABILITY", Value = b.Direction, ValueBrush = b.DirBrush },
        };
        var rows = new[]
        {
            IR("BM unit", b.Unit),
            IR("Fuel", b.Fuel),
            IR("Acceptance", b.Kind, valueBrush: b.DirBrush),
            IR("Level", $"{b.Mw}"),
            IR("Inertia effect", b.Direction, valueBrush: b.DirBrush),
            IR("Why", b.Direction.Contains("inertia")
                ? "a synchronous unit turning up/down changes real intrinsic inertia"
                : "a non-synchronous action changes MW but not intrinsic inertia"),
        };
        Inspector.Show(b.Unit.ToUpperInvariant(), $"balancing-mechanism acceptance  *  {b.Fuel}", stats, rows);
    }

    private void InspectIc(Rp1IcRow ic)
    {
        if (ic is null) return;
        string? icMember = InterconnectorCorridors.CanonicalMember(ic.Name);
        var corridors = Corridors.Where(c => icMember is not null && c.Members.Contains(icMember)).ToList();
        var stats = new[]
        {
            new InspectStat { Label = "FLOW", Value = ic.Flow, ValueBrush = ic.FlowBrush },
            new InspectStat { Label = "DIRECTION", Value = ic.Importing ? "IMPORT" : "EXPORT" },
            new InspectStat { Label = "CORRELATED", Value = ic.Correlated ? "yes" : "no",
                              ValueBrush = ic.Correlated ? RedB : MutedB },
        };
        var rows = new List<InspectRow>
        {
            IR("Interconnector", ic.Name),
            IR("Instantaneous flow", ic.Flow, valueBrush: ic.FlowBrush),
            IR("Infeed loss risk", ic.Importing ? "importing - a trip is a loss of infeed" : "exporting - not an infeed loss"),
        };
        foreach (var (name, members) in corridors)
        {
            rows.Add(IR($"Correlated group", name, valueBrush: RedB));
            foreach (string m in members.Where(m => m != icMember))
            {
                // Find the co-member's row by canonical id (its display name may be a long model name).
                Rp1IcRow? mem = Interconnectors.FirstOrDefault(x => InterconnectorCorridors.CanonicalMember(x.Name) == m);
                rows.Add(IR(m, mem?.Flow ?? "-", valueBrush: mem?.FlowBrush,
                    drill: mem is null ? null : new RelayCommand(() => InspectIc(mem))));   // hop to the co-member
            }
        }
        Inspector.Show(ic.Name.ToUpperInvariant(),
            corridors.Count > 0 ? "shares a correlated-trip corridor" : "no shared corridor", stats, rows);
    }

    // boundary context for the narrative (top boundary + trend); the RIGHT column fills the rest later.
    private string _topBoundaryName = ""; private double _topBoundaryLoading;

    /// <summary>Per-frame refresh from the solved frame + evaluated boundaries.</summary>
    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, string clock)
    {
        Clock = clock;
        ApplyNominal(f.Model.FrequencyHz);   // N301: hide the GB page on a case whose nominal is not 50 Hz
        _currentHz = f.FrequencyHz;
        _rocofFrame = f.RocofHzPerS;

        // one pass over generators: per-fuel intrinsic mass (inertia-by-source) and the full dispatch stack (LEFT
        // column). The largest online single infeed (the secured credible loss; any technology since N039) comes from the shared Core
        // presenter below, the same call the web FuelBreakdown makes (Q1, 2026-10).
        var perFuel = new Dictionary<string, (double eMwS, FuelInfo fi)>();
        var stack = new Dictionary<string, (double outMw, double capMw, double eMwS, FuelInfo fi)>();
        double intrinsicMwS = 0, syncMw = 0, convMw = 0;
        _lastGens.Clear();
        foreach (Generator g in f.Model.Generators)
        {
            FuelInfo fi = Fuels.Classify(g.Type);
            double mw = System.Math.Max(0, g.Pg);
            double cap = double.IsInfinity(g.Pmax) ? mw : System.Math.Max(g.Pmax, mw);
            // The rotating mass this unit contributes, by the ONE model rule the header INERTIA tile uses
            // (GridModel.RotatingInertiaMwS, N289: every unit not of a KNOWN converter type, so an untyped MATPOWER unit
            // with H counts). This tab used FuelInfo.Synchronous, which is false for "Other" (untyped) units, so on a
            // case with H data and no type strings INERTIA read the full fleet while RP1's intrinsic inertia read 0.
            double e = GridModel.RotatingInertiaMwS(g);      // MW.s of stored rotating energy (0 for a converter)
            if (mw > 0)
                _lastGens.Add(new GenLite(g.Name ?? fi.Name, fi.Name, mw, cap, g.H,
                    e / 1000.0, fi.Synchronous, Palette.Frozen(fi.Color)));
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

    private Dictionary<string, (double eMwS, FuelInfo fi)> _perFuelIntrinsic = new();

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
            Rp1BoundaryRow row = i < BoundaryBars.Count ? BoundaryBars[i] : Add(BoundaryBars, new Rp1BoundaryRow());
            row.Name = b.Name;
            row.Transfer = FormatGwLocal(b.FlowMw);
            row.Limit = FormatGwLocal(b.LimitMw);
            row.LoadingPercent = b.LoadingPercent;
            row.Loading = $"{b.LoadingPercent:F0}%";
            row.LoadingBrush = Palette.Frozen(Palette.Loading(b.LoadingPercent / 100.0));
            // trend arrow from the previous loading for this boundary
            double prev = _boundaryPrev.TryGetValue(b.Name, out double pv) ? pv : b.LoadingPercent;
            row.Trend = b.LoadingPercent > prev + BoundaryTrendBandPct ? "^" : b.LoadingPercent < prev - BoundaryTrendBandPct ? "v" : "=";
            row.TrendBrush = b.LoadingPercent > prev + BoundaryTrendBandPct ? RedB : b.LoadingPercent < prev - BoundaryTrendBandPct ? GreenB : MutedB;
            _boundaryPrev[b.Name] = b.LoadingPercent;
        }
        while (BoundaryBars.Count > ordered.Count) BoundaryBars.RemoveAt(BoundaryBars.Count - 1);
    }

    private void SyncConstraintCosts(List<Proto.ConstraintDto>? cons)
    {
        ConstraintCosts.Clear();
        if (cons is null) return;
        foreach (Proto.ConstraintDto c in cons.Take(ConstraintRowCap))
            ConstraintCosts.Add(new Rp1ConstraintRow
            {
                Name = c.Name ?? "-",
                // the lake carries the day's constraint cost; show it as an hourly rate.
                CostPerHour = c.CostGbp is double cost ? $"GBP {cost / HoursPerDay / 1000.0:F1}k/h" : "-",
                Binding = c.Binding ?? false,
                BindingBrush = (c.Binding ?? false) ? RedB : MutedB,
            });
    }

    private void SyncBmDelta(List<Proto.BalancingActionDto>? actions)
    {
        BmDelta.Clear();
        int improving = 0, reducing = 0, neutral = 0;
        if (actions is not null)
            foreach (Proto.BalancingActionDto a in actions.Take(BalancingActionRowCap))
            {
                bool offer = (a.Kind ?? "").StartsWith("OFFER", System.StringComparison.OrdinalIgnoreCase);
                string dir; Brush brush;
                if (a.Synchronous == true) { if (offer) { dir = "^ inertia"; brush = GreenB; improving++; } else { dir = "v inertia"; brush = RedB; reducing++; } }
                else if (a.Synchronous == false) { dir = "neutral"; brush = MutedB; neutral++; }
                else { dir = "watch"; brush = AmberB; }
                BmDelta.Add(new Rp1BmRow
                {
                    Unit = a.BmUnit ?? "-",
                    Fuel = a.FuelType ?? "-",
                    Kind = a.Kind ?? "-",
                    Mw = $"{a.LevelMw ?? 0:F0} MW",
                    Direction = dir,
                    DirBrush = brush,
                });
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
            Interconnectors.Add(new Rp1IcRow
            {
                Name = ic.Name ?? "-",
                Flow = $"{flow:+0;-0;0} MW",
                Importing = flow > 0,
                FlowBrush = flow > 0 ? AmberB : MutedB,   // importing = an infeed that can be lost
                Correlated = correlated,
                FlagBrush = correlated ? RedB : Brushes.Transparent,
            });
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
        UnitBrush = _unitBinding ? WhiteB : MutedB;
        GroupBrush = _groupBinding ? WhiteB : MutedB;
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
        PurchasedBrush = LevelBrush(FrequencyRiskPresenter.GradePurchasedTime(tLive, tightening: false).Level);
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
            Rp1InertiaRow row = i < InertiaBySource.Count ? InertiaBySource[i] : Add(InertiaBySource, new Rp1InertiaRow());
            double share = eTotal > 0 ? val.eMwS / eTotal : 0;
            row.Fuel = name;
            row.Swatch = Palette.Frozen(val.fi.Color);
            row.Seconds = $"{share * secs:F1} s";
            row.SharePercent = share * 100;
        }
        while (InertiaBySource.Count > ordered.Count) InertiaBySource.RemoveAt(InertiaBySource.Count - 1);
    }

    private void SyncGenStack(Dictionary<string, (double outMw, double capMw, double eMwS, FuelInfo fi)> stack, double totalMw)
    {
        var ordered = stack.OrderByDescending(kv => kv.Value.outMw).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (name, v) = (ordered[i].Key, ordered[i].Value);
            Rp1GenRow row = i < GenStack.Count ? GenStack[i] : Add(GenStack, new Rp1GenRow());
            row.Fuel = name;
            row.Swatch = Palette.Frozen(v.fi.Color);
            row.Output = $"{v.outMw:N0} MW";
            row.OutputPercent = totalMw > 0 ? v.outMw / totalMw * 100 : 0;
            double headroom = v.capMw - v.outMw;
            row.Headroom = headroom > 1 ? $"+{headroom:N0}" : "-";
            row.Inertia = v.fi.Synchronous ? $"{v.eMwS / 1000.0:F1} GVA.s" : "0";
            row.Kind = v.fi.Synchronous ? "SYNC" : "CONV";
            row.KindBrush = v.fi.Synchronous ? GreenB : MutedB;
        }
        while (GenStack.Count > ordered.Count) GenStack.RemoveAt(GenStack.Count - 1);
    }

    private void SetLfdd(double purchasedSeconds, bool tightening)
    {
        // normal / watch / advisory / warning, from purchased time against the human-response floor: the ONE Core
        // ladder shared with the web RP1 tab (Q1, 2026-10). A falling trend below NORMAL escalates it to red " down".
        FrequencyRiskPresenter.Grade g = FrequencyRiskPresenter.GradePurchasedTime(purchasedSeconds, tightening);
        LfddGrade = g.Text; LfddBrush = LevelBrush(g.Level);
    }

    /// <summary>The brush for a Core grade level. An unknown level is a programming error, so it throws.</summary>
    private static Brush LevelBrush(FrequencyRiskPresenter.GradeLevel level) => level switch
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
}

public sealed class Rp1InertiaRow : Notify
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private Brush _swatch = Brushes.Gray; public Brush Swatch { get => _swatch; set => Set(ref _swatch, value); }
    private string _seconds = ""; public string Seconds { get => _seconds; set => Set(ref _seconds, value); }
    private double _sharePercent; public double SharePercent { get => _sharePercent; set => Set(ref _sharePercent, value); }
}

public sealed class Rp1GenRow : Notify
{
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private Brush _swatch = Brushes.Gray; public Brush Swatch { get => _swatch; set => Set(ref _swatch, value); }
    private string _output = ""; public string Output { get => _output; set => Set(ref _output, value); }
    private double _outputPercent; public double OutputPercent { get => _outputPercent; set => Set(ref _outputPercent, value); }
    private string _headroom = ""; public string Headroom { get => _headroom; set => Set(ref _headroom, value); }
    private string _inertia = ""; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private Brush _kindBrush = Brushes.Gray; public Brush KindBrush { get => _kindBrush; set => Set(ref _kindBrush, value); }
}

public sealed class Rp1BoundaryRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _transfer = ""; public string Transfer { get => _transfer; set => Set(ref _transfer, value); }
    private string _limit = ""; public string Limit { get => _limit; set => Set(ref _limit, value); }
    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private double _loadingPercent; public double LoadingPercent { get => _loadingPercent; set => Set(ref _loadingPercent, value); }
    private Brush _loadingBrush = Brushes.Gray; public Brush LoadingBrush { get => _loadingBrush; set => Set(ref _loadingBrush, value); }
    private string _trend = ""; public string Trend { get => _trend; set => Set(ref _trend, value); }
    private Brush _trendBrush = Brushes.Gray; public Brush TrendBrush { get => _trendBrush; set => Set(ref _trendBrush, value); }
}

public sealed class Rp1ConstraintRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _costPerHour = ""; public string CostPerHour { get => _costPerHour; set => Set(ref _costPerHour, value); }
    private bool _binding; public bool Binding { get => _binding; set => Set(ref _binding, value); }
    private Brush _bindingBrush = Brushes.Gray; public Brush BindingBrush { get => _bindingBrush; set => Set(ref _bindingBrush, value); }
}

public sealed class Rp1BmRow : Notify
{
    private string _unit = ""; public string Unit { get => _unit; set => Set(ref _unit, value); }
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private string _kind = ""; public string Kind { get => _kind; set => Set(ref _kind, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _direction = ""; public string Direction { get => _direction; set => Set(ref _direction, value); }
    private Brush _dirBrush = Brushes.Gray; public Brush DirBrush { get => _dirBrush; set => Set(ref _dirBrush, value); }
}

public sealed class Rp1IcRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private bool _importing; public bool Importing { get => _importing; set => Set(ref _importing, value); }
    private Brush _flowBrush = Brushes.Gray; public Brush FlowBrush { get => _flowBrush; set => Set(ref _flowBrush, value); }
    private bool _correlated; public bool Correlated { get => _correlated; set => Set(ref _correlated, value); }
    private Brush _flagBrush = Brushes.Transparent; public Brush FlagBrush { get => _flagBrush; set => Set(ref _flagBrush, value); }
}
