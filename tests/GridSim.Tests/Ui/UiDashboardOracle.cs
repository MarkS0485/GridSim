using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Presentation;
using Color = GridSim.Ui.Presentation.UiColour;

// Test oracle for UiDashboardParityTests (UI programme Phase 0.2, second slice). NOT production code.
//
// Below is the WPF dashboard view-model exactly as it was before its logic moved to src/GridSim.Ui/Dashboard
// (src/GridSim.Wpf/DashboardViewModel.cs at 2.0.6), together with the two WPF helpers it called (Visuals/Fuels.cs and
// Visuals/Interconnectors.cs at 2.0.6). The text was copied mechanically from git; the only edits are:
//   - the WPF namespaces (System.Windows, System.Windows.Media, GridSim.Wpf.Visuals) are replaced by the small shims
//     at the top of this file, which carry exactly what the old code read from them: a Brush is a colour (a
//     SolidColorBrush holds one UiColour, a LinearGradientBrush its stops), Palette.Frozen caches one brush per colour
//     as the real one does, Palette's ramps are the neutral ColourRamps (UiColourRampsTests pins those byte for byte
//     against the old WPF arithmetic), Brushes.Gray / LightGreen / Transparent carry the WPF named colours' bytes, and
//     Visibility is the two values the dashboard used;
//   - the twelve per-tab view-models (Assets .. Cycle) and the lines that fed, reset or cleared them are left out,
//     each marked "(ORACLE: ...)": they are WPF tabs that read the frame but write nothing back into the dashboard, so
//     no dashboard value depends on them.
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

namespace GridSim.Tests.Ui.OldDashboard;

// ------------------------------------------------ shims ------------------------------------------------

/// <summary>Shim of the WPF brush: the old code only ever read a brush's colour (or a gradient's stops).</summary>
public abstract class Brush { }

/// <summary>Shim of the WPF solid brush: one colour.</summary>
public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color c) { Color = c; }
    public Color Color { get; }
}

/// <summary>Shim of the WPF gradient stop.</summary>
public sealed class GradientStop
{
    public GradientStop(Color color, double offset) { Color = color; Offset = offset; }
    public Color Color { get; }
    public double Offset { get; }
}

/// <summary>Shim of the WPF gradient stop collection.</summary>
public sealed class GradientStopCollection : List<GradientStop> { }

/// <summary>Shim of the WPF point (the gradient's start and end).</summary>
public readonly record struct Point(double X, double Y);

/// <summary>Shim of the WPF linear gradient brush: its stops, start and end.</summary>
public sealed class LinearGradientBrush : Brush
{
    public LinearGradientBrush(GradientStopCollection stops, Point start, Point end) { Stops = stops; Start = start; End = end; }
    public GradientStopCollection Stops { get; }
    public Point Start { get; }
    public Point End { get; }
    public bool IsFrozen { get; private set; }
    public void Freeze() => IsFrozen = true;
}

/// <summary>Shim of the WPF named brushes the dashboard used, with the WPF colours' exact bytes.</summary>
public static class Brushes
{
    public static readonly SolidColorBrush Gray = new(new Color(0xFF, 0x80, 0x80, 0x80));          // Colors.Gray
    public static readonly SolidColorBrush LightGreen = new(new Color(0xFF, 0x90, 0xEE, 0x90));    // Colors.LightGreen
    public static readonly SolidColorBrush Transparent = new(new Color(0x00, 0xFF, 0xFF, 0xFF));   // Colors.Transparent
}

/// <summary>Shim of the WPF Visibility values the dashboard used.</summary>
public enum Visibility { Visible, Collapsed }

/// <summary>Shim of the WPF Palette at 2.0.6: the ramps were already the neutral ColourRamps, converted byte for byte;
/// Frozen returns one cached brush per colour.</summary>
public static class Palette
{
    public static Color FromHex(string hex) => ColourRamps.FromHex(hex);
    public static Color Voltage(double vm) => ColourRamps.Voltage(vm);
    public static Color Loading(double frac) => ColourRamps.Loading(frac);
    public static Color Condition(double idx01) => ColourRamps.Condition(idx01);
    public static Color Stress(double frac) => ColourRamps.Stress(frac);

    private static readonly Dictionary<Color, SolidColorBrush> FrozenCache = new();

    public static SolidColorBrush Frozen(Color c)
    {
        lock (FrozenCache)
        {
            if (FrozenCache.TryGetValue(c, out SolidColorBrush? cached)) return cached;
            var b = new SolidColorBrush(c);
            FrozenCache[c] = b;
            return b;
        }
    }
}

/// <summary>Shim of the map's colour-mode enum (the old FastSchematic.ColorModeKind, same members, same order).</summary>
public static class FastSchematic
{
    public enum ColorModeKind { Loading, Voltage, Condition, Stress }
}

// ------------------------------------------------ the old Visuals/Fuels.cs ------------------------------------------------

/// <summary>How a generator's fuel sits in the inertia / decarbonisation picture.</summary>
public enum FuelClass { Fossil, Nuclear, Renewable, Storage, Interconnector, Other }

/// <summary>A classified fuel: display name, colour, and whether it's a spinning (inertial) machine.</summary>
public readonly record struct FuelInfo(string Name, Color Color, FuelClass Class, bool Synchronous)
{
    public bool IsFossil => Class == FuelClass.Fossil;
    public bool IsRenewable => Class == FuelClass.Renewable;
}

/// <summary>
/// The WPF view of the shared fuel taxonomy. The keyword table lives in ONE place, Core's
/// <see cref="FuelTaxonomy"/> (N016 / R305 remainder, October 2026): this class used to keep its own copy, which had
/// drifted from the web and Core one - it read "hydrogen" as synchronous Hydro (the substring "hydro") and left
/// reciprocating engines ("recip", "diesel", "engine") as non-synchronous "Other". It now only translates Core's
/// <see cref="FuelCategory"/> and hex colour into the WPF <see cref="FuelClass"/> and <see cref="Color"/>, exactly as
/// the web <c>Fuels</c> does, so existing WPF callers are unchanged.
/// <para>Behaviour change, stated: a generator typed "hydrogen" / "fuel cell" / "electrolyser" now reads as
/// Hydrogen (storage, converter-connected, no inertia) instead of Hydro (renewable, synchronous); "recip" / "diesel"
/// / "engine" now reads as Reciprocating engine (fossil, synchronous) instead of Other. Every other classification
/// and colour is identical to the old WPF table.</para>
/// Spinning (synchronous) plant contributes rotational inertia; wind, solar, storage and interconnectors are
/// converter-connected and contribute none.
/// </summary>
public static class Fuels
{
    public static FuelInfo Classify(string? type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        return new FuelInfo(d.Name, Palette.FromHex(d.Color), ToFuelClass(d.Category), d.Synchronous);
    }

    /// <summary>One-to-one translation of Core's category; an unknown value is a programming error, so it throws.</summary>
    public static FuelClass ToFuelClass(FuelCategory c) => c switch
    {
        FuelCategory.Fossil => FuelClass.Fossil,
        FuelCategory.Nuclear => FuelClass.Nuclear,
        FuelCategory.Renewable => FuelClass.Renewable,
        FuelCategory.Storage => FuelClass.Storage,
        FuelCategory.Interconnector => FuelClass.Interconnector,
        FuelCategory.Other => FuelClass.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown fuel category from the Core taxonomy."),
    };
}

// ------------------------------------------------ the old Visuals/Interconnectors.cs ------------------------------------------------

/// <summary>
/// Display catalogue for the GB interconnector fleet  -  what each link joins and where it
/// lands. Pure presentation metadata: the electrical model carries the links as
/// <c>type: "interconnector"</c> generators; this just labels the dials.
/// </summary>
public static class Interconnectors
{
    // Keyed by the NORMALISED link name (see Normalise). The models spell the same link differently: the reduced GB
    // cases say "North Sea Link", gb-full says "NS Link interconnector" or "IFA Interconnector interconnector". An exact
    // lookup on the short name missed every gb-full spelling, so IMPORT LINKS and the interconnector pages showed no
    // route on gb-full. Each link lists the spellings seen in the shipped models and the bridge, matched exactly after
    // normalising, so "IFA2" stays distinct from "IFA" and an unrelated name cannot match by accident (the same rule
    // as Core's InterconnectorCorridors.CanonicalMember).
    private static readonly Dictionary<string, string> Route = Build(
        // gb-full names the Moyle link by its GB landing: "Auchencrosh (interconnector CCT) interconnector".
        ("N. Ireland  Auchencrosh", new[] { "moyle", "auchencrosh", "auchencroshcct" }),
        ("Isle of Man  Bispham", new[] { "isleofman" }),
        ("Norway  Blyth", new[] { "northsealink", "nslink", "nsl" }),
        ("Ireland  Deeside", new[] { "ewic", "eastwest" }),
        ("Denmark  Bicker Fen", new[] { "vikinglink", "vikinglinkdenmark", "viking" }),
        ("France  Sellindge", new[] { "ifa" }),
        ("France  Folkestone", new[] { "eleclink" }),
        ("Belgium  Richborough", new[] { "nemolink", "nemo" }),
        ("Netherlands  Grain", new[] { "britned" }),
        ("France  Chilling", new[] { "ifa2" }),
        ("Ireland  Pembroke", new[] { "greenlink" }));

    private static Dictionary<string, string> Build(params (string Route, string[] Keys)[] links)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string route, string[] keys) in links)
            foreach (string k in keys) map.Add(k, route);   // Add, not indexer: a key listed twice is a defect here
        return map;
    }

    /// <summary>
    /// The lookup key of a link name: lower case (invariant), letters and digits only, with every "interconnector"
    /// removed (the gb-full export repeats it).
    /// </summary>
    internal static string Normalise(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace("interconnector", "", StringComparison.Ordinal);
    }

    /// <summary>"Country  landing substation" for a known link; empty for anything else.</summary>
    public static string RouteOf(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Route.TryGetValue(Normalise(name), out string? r) ? r : "";
}

// ------------------------------------------------ the old DashboardViewModel.cs ------------------------------------------------

/// <summary>One transmission-boundary corridor row in the side panel.</summary>
public sealed class BoundaryRow : Notify
{
    public string Name { get; init; } = "";
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _pct = ""; public string Pct { get => _pct; set => Set(ref _pct, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }
}

/// <summary>One voltage-band dial on the NETWORK telemetry column: live average pu for that kV
/// level, shown against a nominal-centred dial (1.0 pu in the middle).</summary>
public sealed class VoltageBandDial : Notify
{
    /// <summary>The level's exact base voltage: the dial's identity (two levels may round to the same label).</summary>
    public double BaseKv { get; init; }
    public string Label { get; init; } = "";                                                    // "400 kV", "0.415 kV"
    private double _vpu = 1.0; public double Vpu { get => _vpu; set => Set(ref _vpu, value); }   // dial value (pu)
    private string _dev = " - "; public string Dev { get => _dev; set => Set(ref _dev, value); }   // "+0.4%"
    private string _kv = " - "; public string Kv { get => _kv; set => Set(ref _kv, value); }       // "417 kV" live
    private Brush _brush = Brushes.Gray; public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}

/// <summary>Bound state for the whole control suite.</summary>
public sealed class DashboardViewModel : Notify
{
    public const double BarTrack = 150.0;

    private string _clock = "08:00"; public string Clock { get => _clock; set => Set(ref _clock, value); }
    private string _tick = "0"; public string Tick { get => _tick; set => Set(ref _tick, value); }

    private string _freq = "50.00"; public string Freq { get => _freq; set => Set(ref _freq, value); }
    private string _freqDev = "+0.00"; public string FreqDev { get => _freqDev; set => Set(ref _freqDev, value); }
    private double _freqAngle; public double FreqAngle { get => _freqAngle; set => Set(ref _freqAngle, value); }
    // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the frequency dial's end labels, the
    // full-scale deviation (+/-0.5 Hz) about the case's own nominal: "49.5" / "50.5" for every GB case.
    private string _freqDialLowText = "49.5"; public string FreqDialLowText { get => _freqDialLowText; set => Set(ref _freqDialLowText, value); }
    private string _freqDialHighText = "50.5"; public string FreqDialHighText { get => _freqDialHighText; set => Set(ref _freqDialHighText, value); }
    private Brush _freqBrush = Brushes.LightGreen; public Brush FreqBrush { get => _freqBrush; set => Set(ref _freqBrush, value); }

    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _generation = " - "; public string Generation { get => _generation; set => Set(ref _generation, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _voltage = " - "; public string Voltage { get => _voltage; set => Set(ref _voltage, value); }

    // ---- headline-tile framing: raw numbers alone carry no limit context, so each headline tile
    // also exposes a headroom/limit sub-line (and, where a lamp is warranted, a brush).
    private string _demandNote = " - "; public string DemandNote { get => _demandNote; set => Set(ref _demandNote, value); }
    private string _generationNote = " - "; public string GenerationNote { get => _generationNote; set => Set(ref _generationNote, value); }
    private string _lossesNote = " - "; public string LossesNote { get => _lossesNote; set => Set(ref _lossesNote, value); }

    // Inertia vs the secure minimum for the current largest credible loss  -  the app's whole thesis.
    private string _inertiaNote = " - "; public string InertiaNote { get => _inertiaNote; set => Set(ref _inertiaNote, value); }
    private Brush _inertiaNoteBrush = Brushes.Gray; public Brush InertiaNoteBrush { get => _inertiaNoteBrush; set => Set(ref _inertiaNoteBrush, value); }
    /// <summary>Secure-minimum inertia (GVAs) that holds RoCoF at the limit for the current largest loss.</summary>
    private double _secureMinInertiaGva; public double SecureMinInertiaGva { get => _secureMinInertiaGva; set => Set(ref _secureMinInertiaGva, value); }
    /// <summary>Live inertia headroom above that secure minimum (GVAs); negative = below secure.</summary>
    private double _inertiaHeadroomGva; public double InertiaHeadroomGva { get => _inertiaHeadroomGva; set => Set(ref _inertiaHeadroomGva, value); }

    private string _status = "INITIALISING"; public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>Visible while the frame being shown did not converge: drives the shared STATE NOT VALID banner over every
    /// tab and the grey veil over the map.</summary>
    private Visibility _invalidStateVis = Visibility.Collapsed; public Visibility InvalidStateVis { get => _invalidStateVis; set => Set(ref _invalidStateVis, value); }
    private Brush _statusBrush = Brushes.Gray; public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public ObservableCollection<BoundaryRow> Boundaries { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<AlarmRow> Alarms { get; } = new();

    /// <summary>One dial per voltage band in the model  -  how close that band sits to nominal (1.0 pu).</summary>
    public ObservableCollection<VoltageBandDial> VoltageBands { get; } = new();
    private readonly Dictionary<double, VoltageBandDial> _bandByKv = new();   // keyed by the exact BaseKv, not its label

    // (ORACLE: the twelve per-tab view-model properties, Assets .. Cycle, are WPF tabs and are left out.)

    private readonly Dictionary<string, BoundaryRow> _boundaryByName = new();
    private HashSet<string> _raised = new();
    private bool _lastInBand = true;
    private bool _lastConverged = true;

    // ---- display thresholds (R209: named, values unchanged unless stated) ----

    /// <summary>Half-width (Hz) of the frequency band around nominal before the header lamp reads FREQUENCY EVENT
    /// (strictly greater than this). The GB operational +/-0.2 Hz band, <see cref="FrequencyLimits.OperationalLowHz"/>
    /// to <see cref="FrequencyLimits.OperationalHighHz"/>, written as a deviation; kept as the literal 0.2 because
    /// <c>NominalHz - OperationalLowHz</c> rounds to 0.20000000000000284. The web lamp uses the same band.
    /// Q10 (2026-10): the lamp now calls the shared Core rule <see cref="StatusLampRules.IsFrequencyDeviationEvent"/>;
    /// this literal mirror stays because PresentationConstantsAntiDriftTests reads it from this file (it is pinned equal
    /// to the web / Core value there). N302 (LF3, October 2026): the dial no longer reads it either; it colours by the
    /// lamp's band-edge rule (see <see cref="SetFrequency"/>).</summary>
    public const double FrequencyEventBandHz = 0.2;

    /// <summary>Frequency deviation (Hz) at which the frequency dial (and the GRID SPEED rpm dial) reaches full
    /// scale.</summary>
    public const double FreqDialFullScaleHz = 0.5;

    /// <summary>Needle sweep (degrees either side of centre) of the frequency and rpm dials at full scale.</summary>
    public const double FreqDialSweepDeg = 120.0;

    /// <summary>Frequency deviation (Hz) inside which the frequency read-out is coloured nominal (green).</summary>
    public const double FreqNominalColourBandHz = 0.1;

    /// <summary>Synchronous share of generation below which the GENERATION tab reads LOW INERTIA REGIME.</summary>
    public const double LowInertiaSyncFraction = GridSim.Core.Presentation.PresentationConstants.SyncShareModerateFraction;   // N023: the one shared copy (was the literal 0.4)

    // Idle interconnector cut-off: Q10 (2026-10, ruled) - ONE shared value, StatusLampRules.IdleInterconnectorMw =
    // 0.5 MW (the web value; the smaller threshold hides less), via StatusLampRules.IsIdleInterconnector. The WPF
    // used its own 5 MW here (IdleLinkMw) and the difference was reported for a ruling; that constant is removed.

    /// <summary>A two-pole synchronous machine's speed (rpm) at nominal frequency: 60 x 50 Hz / 1 pole pair.</summary>
    public const double TwoPoleSyncRpm = GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpm;   // N023: the one shared copy (was the literal 3000.0)

    /// <summary>How many lines the event log keeps (newest first).</summary>
    public const int EventLogCapacity = 14;

    /// <summary>How many rows the alarm list keeps (newest first).</summary>
    public const int AlarmListCapacity = 12;

    // R101: the measured base-solve time of the frame being applied (0 for replay / bridge frames), handed to the
    // CYCLE tab. Set by the overload below for the duration of one Update.
    private double _frameBaseSolveMs;

    /// <summary>Apply a locally solved frame together with its measured base-solve time (R101).</summary>
    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, double baseSolveMs)
    {
        _frameBaseSolveMs = baseSolveMs;
        try { Update(f, boundaries); }
        finally { _frameBaseSolveMs = 0; }
    }

    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries)
    {
        _lastModel = f.Model;
        _lastDemand = f.DemandMw;
        int h = (int)f.Hour, m = (int)((f.Hour % 1) * 60);
        Clock = $"{h:00}:{m:00}";
        Tick = f.Tick.ToString();

        if (!FreqLocked) SetFrequency(f.FrequencyHz);   // a trip in progress drives the dial instead

        Demand = (f.DemandMw / 1000.0).ToString("F2");
        Generation = (f.GenerationMw / 1000.0).ToString("F2");
        Losses = f.Result.TotalLossMw.ToString("F0");
        Inertia = f.Model.SystemInertiaGvaS.ToString("F0");
        UpdateHeadlineFraming(f);

        double vmin = f.Result.Vm.Min(), vmax = f.Result.Vm.Max();
        Voltage = $"{vmin:F3}-{vmax:F3}";

        // VOLTAGE ALERT on the lowest / highest bus voltage: the shared Core rule (Q10, 2026-10), same as the web lamp.
        bool inBand = !StatusLampRules.IsVoltageAlert(vmin, vmax);
        bool overload = boundaries.Any(b => b.Overloaded);
        // Frequency excursion is the highest-priority alert (a live trip drives f through the frames). Folding
        // it into the status here means the " FREQUENCY EVENT" lamp reflects the live frequency every tick and
        // self-clears on recovery, instead of being set once by TripAtBus and overwritten next tick.
        // GB operational limit +/-0.2 Hz, strictly outside: the shared Core rule (Q10, 2026-10), same as the web lamp.
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): about the frame's own nominal
        // (FrequencyBands.ForModel: the GB constants themselves for every 50 Hz case).
        // N041 (B4, October 2026; Ruling R1; source EREC G99 Issue 2 Table 10.1 and Ofgem's DC0079 decision, see
        // StatusLampRules.RocofEventHzPerS): a RoCoF above the GB loss-of-mains setting (1 Hz/s) is a FREQUENCY EVENT
        // too, the same rule as the web lamp. Before: the desktop lamp had no RoCoF test (the web used 0.125 Hz/s).
        bool freqEvent = StatusLampRules.IsFrequencyDeviationEvent(f.FrequencyHz, GridSim.Core.Dynamics.FrequencyBands.ForModel(f.Model))
                         || StatusLampRules.IsRocofEvent(f.RocofHzPerS);

        // R051: a power flow that stopped without converging outranks every other verdict - including a trip in
        // progress (FreqLocked) - because the voltages, flows and boundaries read below come from an unconverged
        // iterate, not an operating point. This used to be missing: Converged was never read here, so a Newton solve
        // that hit its iteration cap with voltages still inside the band showed SYSTEM NORMAL. Fail loud.
        bool converged = f.Result.Converged;
        // Every tab below (and the map) is still fed this frame, so the shared STATE NOT VALID banner and the map's grey
        // veil are raised for as long as the frames do not converge (the lamp alone used to be the only marking).
        InvalidStateVis = converged ? Visibility.Collapsed : Visibility.Visible;
        if (!converged)
        {
            Status = "NOT CONVERGED";
            StatusBrush = Palette.Frozen(Palette.Loading(1.1));
        }
        else if (!FreqLocked)
        {
            if (freqEvent) { Status = " FREQUENCY EVENT"; StatusBrush = Palette.Frozen(Palette.Loading(1.1)); }
            else if (!inBand) { Status = "VOLTAGE ALERT"; StatusBrush = Palette.Frozen(Palette.Loading(1.1)); }
            else if (overload) { Status = "CORRIDOR ALERT"; StatusBrush = Palette.Frozen(Palette.Loading(0.8)); }
            else { Status = "SYSTEM NORMAL"; StatusBrush = Palette.Frozen(Palette.Voltage(1.0)); }
        }

        UpdateBoundaries(boundaries);
        UpdateGeneration(f.Model, f.FrequencyHz, f.GenerationMw);
        UpdateVoltageBands(f);
        // (ORACLE: the twelve tab updates, Assets.Update .. Cycle.Update, are WPF tabs and are left out.)

        // Raise an alarm on the onset of each overload / voltage excursion / non-converged solve (once per onset).
        var cur = new HashSet<string>();
        if (!converged)
        {
            cur.Add("NC");
            if (!_raised.Contains("NC")) AddAlarm("POWER FLOW DID NOT CONVERGE - state not valid", Palette.Frozen(Palette.Loading(1.1)));
        }
        foreach (BoundaryFlow b in boundaries)
            if (b.Overloaded)
            {
                string k = "BND:" + b.Name;
                cur.Add(k);
                if (!_raised.Contains(k)) AddAlarm($"{b.Name} OVERLOAD {b.LoadingPercent:F0}%", Palette.Frozen(Palette.Loading(1.1)));
            }
        if (!inBand)
        {
            cur.Add("V");
            if (!_raised.Contains("V")) AddAlarm($"VOLTAGE {vmin:F2}-{vmax:F2} pu", Palette.Frozen(Palette.Loading(0.8)));
        }
        _raised = cur;

        if (converged != _lastConverged)
        {
            Log(Clock, converged ? "Power flow converged again" : "Power flow did not converge - displayed state is not a valid operating point");
            _lastConverged = converged;
        }

        if (inBand != _lastInBand)
        {
            Log(Clock, inBand ? "Voltages returned to band" : $"Voltage out of band ({vmin:F3}-{vmax:F3} pu)");
            _lastInBand = inBand;
        }
    }

    /// <summary>
    /// Frame the NETWORK/GENERATION headline tiles against their limits rather than showing raw
    /// numbers. The load-bearing one is <b>inertia vs the secure minimum</b>: the floor inertia that
    /// still holds RoCoF at the limit for the current largest credible loss (largest online unit),
    /// via <see cref="InertiaEstimator"/>  -  exactly the margin the TSGB device defends.
    /// </summary>
    private void UpdateHeadlineFraming(SimFrame f)
    {
        GridModel model = f.Model;

        // Losses as a share of generation  -  a fraction, not a bare MW figure.
        double lossFrac = f.GenerationMw > 0 ? f.Result.TotalLossMw / f.GenerationMw : 0;
        LossesNote = $"{lossFrac * 100:F1}% of net injection";   // the GENERATION tile's figure (see GenerationNote)

        // Demand vs online capacity: how much headroom of dispatchable plant is left.
        double capacityMw = model.Generators.Where(g => g.Pg > 0).Sum(g => g.Pmax);
        double marginMw = capacityMw - f.DemandMw;
        DemandNote = capacityMw > 0
            ? $"{marginMw / 1000.0:+0.0;-0.0;0.0} GW headroom to {capacityMw / 1000.0:F1} GW online capacity"
            : " - ";
        // SimFrame.GenerationMw is the sum of POSITIVE NET BUS INJECTIONS (Core LiveSim), not the sum of generator
        // outputs: generation at a bus with local demand is counted net of that demand. The note says so, so the tile is
        // not read as total generator output (carrying the solved generator output in the frame is a Core change).
        GenerationNote = capacityMw > 0
            ? $"net bus injection (local load netted)  *  {f.GenerationMw / capacityMw * 100:F0}% of {capacityMw / 1000.0:F1} GW online capacity"
            : "net bus injection (local load netted)";

        // Inertia vs secure minimum for the current largest loss.
        // N039 (B4, 2026-10): the same credible loss the RP1 / OPERATOR tabs use, from the ONE Core presenter (every
        // single infeed, any technology; a grid infeed at a model boundary is not a unit). Before: the largest positive
        // Pg of ANY generator, grid infeed included; equal to the presenter's figure on every model without a grid
        // infeed (both take the largest strictly positive Pg, 0 when none).
        double largestLossMw = FrequencyRiskPresenter.LargestSecuredUnit(model.Generators).Mw;
        double currentGva = model.SystemInertiaGvaS;
        double secureMinGva = largestLossMw > 0
            ? InertiaEstimator.EstimateInertiaGvaS(largestLossMw, FrequencyLimits.RocofLimitHzPerS)
            : 0;
        double headroom = currentGva - secureMinGva;
        SecureMinInertiaGva = secureMinGva;
        InertiaHeadroomGva = headroom;
        bool secure = headroom >= 0;
        InertiaNote = secureMinGva > 0
            ? $"{currentGva:F0} GVA.s ({headroom:+0;-0;0} vs secure min {secureMinGva:F0})"
            : $"{currentGva:F0} GVA.s";
        InertiaNoteBrush = Palette.Frozen(secure ? Palette.Voltage(1.0) : Palette.Loading(1.1));
    }

    private void UpdateBoundaries(IReadOnlyList<BoundaryFlow> boundaries)
    {
        var seen = new HashSet<string>();
        foreach (BoundaryFlow b in boundaries)
        {
            seen.Add(b.Name);
            if (!_boundaryByName.TryGetValue(b.Name, out BoundaryRow? row))
            {
                row = new BoundaryRow { Name = b.Name };
                _boundaryByName[b.Name] = row;
                Boundaries.Add(row);
            }
            double frac = b.LimitMw > 0 ? Math.Abs(b.FlowMw) / b.LimitMw : 0;
            row.Flow = $"{b.FlowMw / 1000.0:+0.00;-0.00;0.00} / {b.LimitMw / 1000.0:F1} GW";
            row.Pct = $"{frac * 100:F0}%";
            row.BarWidth = Math.Min(1.2, frac) * BarTrack;
            row.Bar = Palette.Frozen(Palette.Loading(frac));
        }
        // Prune corridors no longer present (model switch, or replay which passes an empty list) so the
        // panel can't show a previous model's flows frozen and indistinguishable from live data.
        for (int i = Boundaries.Count - 1; i >= 0; i--)
            if (!seen.Contains(Boundaries[i].Name)) { _boundaryByName.Remove(Boundaries[i].Name); Boundaries.RemoveAt(i); }
    }

    /// <summary>One dial per distinct voltage level: the average solved voltage of that band's
    /// buses, so each dial shows at a glance how close that level sits to its nominal kV (1.0 pu).</summary>
    private Dictionary<int, Bus> GetBusById(GridModel m)
    {
        // The cache is dropped by ModelChanged, which every model switch, replay load and bridge (re)start calls, so a
        // different model with the same name and bus count no longer reuses the previous model's map. The name and
        // count test below only spares the rebuild between frames of ONE timeline (LiveSim clones the model per tick).
        if (_busById is not null && m.BusCount == _busByIdBusCount
            && string.Equals(m.Name, _busByIdModelName, StringComparison.Ordinal))
            return _busById;
        _busById = m.Buses.ToDictionary(b => b.Id);
        _busByIdModelName = m.Name;
        _busByIdBusCount = m.BusCount;
        return _busById;
    }

    private void UpdateVoltageBands(SimFrame f)
    {
        GridModel m = f.Model;
        var busById = GetBusById(m);
        var agg = new Dictionary<double, (double sum, int n)>();
        for (int i = 0; i < f.Result.Vm.Length; i++)
        {
            if (!busById.TryGetValue(f.Result.BusIds[i], out Bus? b) || b.BaseKv <= 0) continue;
            (double sum, int n) cur = agg.GetValueOrDefault(b.BaseKv, (0, 0));
            agg[b.BaseKv] = (cur.sum + f.Result.Vm[i], cur.n + 1);
        }

        // One dial per EXACT BaseKv (the aggregation key). Dials used to be keyed by the label "{kv:F0} kV", so distinct
        // levels that round to the same integer (0.4 and 0.415 kV both "0 kV"; 11.5 and 12 kV both "12 kV") shared one
        // dial, the later level overwrote the earlier one's reading in the same frame, and one level vanished. The label
        // now carries enough digits to tell such levels apart.
        var seen = new HashSet<double>();
        foreach ((double kv, (double sum, int n)) in agg.OrderByDescending(k => k.Key))
        {
            if (n == 0) continue;
            double pu = sum / n, devPct = (pu - 1.0) * 100.0;
            seen.Add(kv);
            if (!_bandByKv.TryGetValue(kv, out VoltageBandDial? d))
            {
                d = new VoltageBandDial { BaseKv = kv, Label = KvLabel(kv) };
                _bandByKv[kv] = d;
                VoltageBands.Add(d);
            }
            d.Vpu = pu;
            d.Dev = $"{devPct:+0.0;-0.0;0.0}%";
            // The live kV: whole kV as before for transmission and primary levels; up to three decimals below 10 kV so a
            // 0.4 kV level does not read "0 kV".
            double liveKv = pu * kv;
            d.Kv = liveKv >= 10 ? $"{liveKv:F0} kV" : KvLabel(liveKv);
            d.Brush = Palette.Frozen(Palette.Voltage(pu));
        }
        for (int i = VoltageBands.Count - 1; i >= 0; i--)
            if (!seen.Contains(VoltageBands[i].BaseKv)) { _bandByKv.Remove(VoltageBands[i].BaseKv); VoltageBands.RemoveAt(i); }
    }

    /// <summary>A kV figure with as many decimals as it needs (up to three), invariant culture: "400 kV", "0.415 kV",
    /// "11.5 kV". Used for the voltage-level dials and the MACHINE shaft layers, which are keyed by the exact value.</summary>
    public static string KvLabel(double kv) =>
        kv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " kV";

    public void Log(string clock, string message)
    {
        Events.Insert(0, $"{clock}  {message}");
        while (Events.Count > EventLogCapacity) Events.RemoveAt(Events.Count - 1);
    }

    /// <summary>
    /// Reserved: meant to be true while a trip transient drives the dial, suppressing the steady-state frequency and
    /// status updates. NOTHING sets it true at present (a trip now plays out through the live frequency integrator,
    /// and the lamp follows each frame), so it is always false and the branches that read it always take the normal
    /// path; ResetTimeline clears it. Kept because source-contract tests pin the lamp's branch order around it.
    /// </summary>
    public bool FreqLocked { get; set; }

    /// <summary>Drive the frequency dial directly (used by the live wander and by trip playback).</summary>
    public void SetFrequency(double hz)
    {
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the deviation is taken from the
        // current case's nominal (the last frame's model; the GB 50 Hz before the first frame), and the dial's end labels
        // follow it. For every 50 Hz case this is the old arithmetic on the same 50.0.
        double nominalHz = _lastModel?.FrequencyHz ?? FrequencyLimits.NominalHz;
        FreqDialLowText = GridSim.Core.Dynamics.FrequencyBands.Label(nominalHz - FreqDialFullScaleHz);
        FreqDialHighText = GridSim.Core.Dynamics.FrequencyBands.Label(nominalHz + FreqDialFullScaleHz);
        double dev = hz - nominalHz;
        Freq = hz.ToString("F2");
        FreqDev = (dev >= 0 ? "+" : "") + dev.ToString("F2");
        FreqAngle = Math.Clamp(dev / FreqDialFullScaleHz, -1, 1) * FreqDialSweepDeg;   // +/-0.5 Hz -> +/-120 deg
        // N302 (LF3, October 2026; B4 NF-3): amber vs red is the header lamp's own rule,
        // StatusLampRules.IsFrequencyDeviationEvent against the band edges about the case nominal, so the dial and the
        // lamp agree at the edges. Before, the dial compared |dev| < 0.2 Hz strictly, and dev at exactly 50.2 Hz is
        // 0.20000000000000284 in doubles, so at exactly 49.8 / 50.2 Hz the dial was red while the lamp read normal. Now
        // amber there (the lamp's "in band"). A non-finite frequency (no solved state) stays red, as it was before
        // (Math.Abs(NaN) < x is false), instead of throwing inside the UI update.
        bool frequencyEvent = !double.IsFinite(hz)
            || StatusLampRules.IsFrequencyDeviationEvent(hz, GridSim.Core.Dynamics.FrequencyBands.ForNominal(nominalHz));
        FreqBrush = Palette.Frozen(Math.Abs(dev) < FreqNominalColourBandHz ? Palette.Voltage(1.0)
                                  : !frequencyEvent ? Palette.Loading(0.7) : Palette.Loading(1.1));
    }

    // ---- map hint line (the mouse actions and keys the map accepts, for the current feed) ----
    private const string MapHintCommon =
        "F = fit  M = minimap  C = coast  B = boundaries  G = GSP areas  D = DNO  R = GSP regions  A = assets  Esc = close detail";

    /// <summary>The hint for a locally solved model: right-click and T trip, '.' and ',' change the time step.</summary>
    public const string MapHintLocal =
        "scroll = zoom  drag = pan  click = detail  double-click = drill  right-click = TRIP at bus  T = trip largest unit  " +
        ". / , = finer / coarser step  " + MapHintCommon;

    /// <summary>The hint for a replay: pre-solved, so there is no trip and no time-step key.</summary>
    public const string MapHintReplay =
        "scroll = zoom  drag = pan  click = detail  double-click = drill  (no trips on a replay)  " + MapHintCommon;

    /// <summary>The hint for a bridge feed: right-click opens the server-side what-if menu instead of a local trip.</summary>
    public const string MapHintBridge =
        "scroll = zoom  drag = pan  click = detail  double-click = drill  right-click = what-if menu  " + MapHintCommon;

    private string _mapHint = MapHintLocal; public string MapHint { get => _mapHint; set => Set(ref _mapHint, value); }

    // ---- selection (detail fly-out) ----
    private Visibility _selVis = Visibility.Collapsed; public Visibility SelectionVis { get => _selVis; set => Set(ref _selVis, value); }
    private string _selKind = "SUBSTATION"; public string SelKind { get => _selKind; set => Set(ref _selKind, value); }
    private string _selName = ""; public string SelName { get => _selName; set => Set(ref _selName, value); }
    private string _selSub = ""; public string SelSub { get => _selSub; set => Set(ref _selSub, value); }
    private string _selConnections = ""; public string SelConnections { get => _selConnections; set => Set(ref _selConnections, value); }
    private string _selLines = ""; public string SelLines { get => _selLines; set => Set(ref _selLines, value); }
    public ObservableCollection<KeyVal> SelRows { get; } = new();

    public void Select(string kind, string name, string sub, IEnumerable<KeyVal> rows, string connections, string lines)
    {
        SelKind = kind; SelName = name; SelSub = sub; SelConnections = connections; SelLines = lines;
        SelRows.Clear();
        foreach (KeyVal r in rows) SelRows.Add(r);
        SelectionVis = Visibility.Visible;
    }

    public void ClearSelection() => SelectionVis = Visibility.Collapsed;

    // ---- on-demand physics query (GDA bridge "what-if"): scenario / feasibility / inertia / condition / post_event ----
    private Visibility _queryVis = Visibility.Collapsed; public Visibility QueryVis { get => _queryVis; set => Set(ref _queryVis, value); }
    private string _queryTitle = ""; public string QueryTitle { get => _queryTitle; set => Set(ref _queryTitle, value); }
    private string _querySub = ""; public string QuerySub { get => _querySub; set => Set(ref _querySub, value); }
    private string _queryVerdict = ""; public string QueryVerdict { get => _queryVerdict; set => Set(ref _queryVerdict, value); }
    private Brush _queryVerdictBrush = Brushes.Gray; public Brush QueryVerdictBrush { get => _queryVerdictBrush; set => Set(ref _queryVerdictBrush, value); }
    private string _queryBinding = ""; public string QueryBinding { get => _queryBinding; set => Set(ref _queryBinding, value); }
    public ObservableCollection<ViolationRow> QueryViolations { get; } = new();
    private Visibility _queryViolationsEmptyVis = Visibility.Visible; public Visibility QueryViolationsEmptyVis { get => _queryViolationsEmptyVis; set => Set(ref _queryViolationsEmptyVis, value); }

    /// <summary>
    /// Bind the results+violations panel to the latest on-demand query result. The verdict and binding constraint
    /// are shown prominently; the violations feed the list. When the payload carried a <c>solveExport</c>
    /// (<paramref name="hasOverlay"/>) the map is showing the solved what-if state; otherwise (inertia/condition/
    /// post_event - scalars-only) the panel notes there is no load-flow overlay for this op.
    /// </summary>
    /// <param name="solveNotConverged">True when the payload carried a solve that did NOT converge: the map does not show
    /// it (it is not an operating point), and the panel says so instead of "scalars only".</param>
    public void ShowQueryResult(QueryResult qr, string title, bool hasOverlay, bool solveNotConverged = false)
    {
        QueryTitle = title;
        QueryVerdict = string.IsNullOrWhiteSpace(qr.Verdict) ? "-" : qr.Verdict!;
        // A non-converged solve is never shown as a good verdict, whatever text the server sent.
        QueryVerdictBrush = solveNotConverged ? Palette.Frozen(Palette.Loading(1.1)) : VerdictBrush(qr.Verdict);
        QueryBinding = string.IsNullOrWhiteSpace(qr.BindingConstraint)
            ? "no binding constraint reported" : $"binding constraint: {qr.BindingConstraint}";
        string at = qr.At == DateTime.MinValue ? "" : qr.At.ToString("yyyy-MM-dd HH:mm") + "Z  *  ";
        QuerySub = at + qr.Op + (hasOverlay ? "  *  what-if state shown on map"
            : solveNotConverged ? "  *  the what-if solve did NOT converge: no valid state, nothing shown on the map"
            : "  *  scalars only (no load-flow overlay)");

        QueryViolations.Clear();
        foreach (Violation v in qr.Violations) QueryViolations.Add(ViolationRow.From(v));
        QueryViolationsEmptyVis = QueryViolations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueryVis = Visibility.Visible;
    }

    public void ClearQueryResult()
    {
        QueryVis = Visibility.Collapsed;
        QueryViolations.Clear();
    }

    // ---- live system HUD (GDA-bridge stream scalars: inertia, fragility, RoCoF headroom) ----
    private static readonly Brush MutedBrush = Palette.Frozen(Palette.FromHex("#8492A0"));
    private Visibility _hudVis = Visibility.Collapsed; public Visibility HudVis { get => _hudVis; set => Set(ref _hudVis, value); }
    private string _inertiaGvaS = " - "; public string InertiaGvaS { get => _inertiaGvaS; set => Set(ref _inertiaGvaS, value); }
    private string _fragilityIndex = " - "; public string FragilityIndex { get => _fragilityIndex; set => Set(ref _fragilityIndex, value); }
    private Brush _fragilityBrush = Brushes.Gray; public Brush FragilityBrush { get => _fragilityBrush; set => Set(ref _fragilityBrush, value); }
    private string _rocofHeadroomHzS = " - "; public string RocofHeadroomHzS { get => _rocofHeadroomHzS; set => Set(ref _rocofHeadroomHzS, value); }
    // Tick-over-tick fragility trend: an arrow + word, coloured (rising fragility = worse -> red, easing -> green).
    private string _fragilityTrend = "-"; public string FragilityTrend { get => _fragilityTrend; set => Set(ref _fragilityTrend, value); }
    private Brush _fragilityTrendBrush = MutedBrush; public Brush FragilityTrendBrush { get => _fragilityTrendBrush; set => Set(ref _fragilityTrendBrush, value); }
    private double? _prevCondition;   // last fragility index, for the trend delta

    /// <summary>
    /// Feed the compact live HUD from the GDA-bridge <c>state</c> scalars: system inertia (GVA.s = inertiaMwS/1000),
    /// the system fragility/condition index (tinted by <see cref="Palette.Condition"/>) with a tick-over-tick trend,
    /// and the RoCoF headroom (Hz/s to the limit). Also the numeric surface for the scalars-only query ops
    /// (inertia / condition). Shows the card.
    /// </summary>
    public void UpdateHud(double? inertiaMwS, double? conditionIndex, double? rocofHeadroomHzS)
    {
        InertiaGvaS = inertiaMwS is double mw ? (mw / 1000.0).ToString("F1") : " - ";
        if (conditionIndex is double ci)
        {
            FragilityIndex = ci.ToString("F2");
            FragilityBrush = Palette.Frozen(Palette.Condition(ci));
            // Trend: worsening fragility is the alarming direction, so tint a rise red and an ease green.
            if (_prevCondition is double p)
            {
                double d = ci - p;
                if (d > 0.005) { FragilityTrend = "^ rising"; FragilityTrendBrush = Palette.Frozen(Palette.Condition(Math.Min(1.0, ci + 0.2))); }
                else if (d < -0.005) { FragilityTrend = "v easing"; FragilityTrendBrush = Palette.Frozen(Palette.Voltage(1.0)); }
                else { FragilityTrend = "= steady"; FragilityTrendBrush = MutedBrush; }
            }
            else { FragilityTrend = "= steady"; FragilityTrendBrush = MutedBrush; }
            _prevCondition = ci;
        }
        else { FragilityIndex = " - "; FragilityBrush = Brushes.Gray; FragilityTrend = "-"; FragilityTrendBrush = MutedBrush; }
        RocofHeadroomHzS = rocofHeadroomHzS is double hr ? hr.ToString("F3") : " - ";
        HudVis = Visibility.Visible;
    }

    /// <summary>Hide the live HUD (leaving bridge mode / switching to a local sim or replay source).</summary>
    public void HideHud() { HudVis = Visibility.Collapsed; _prevCondition = null; }

    /// <summary>
    /// The bridge stream has stopped for good (a socket fault or a horizon rejection): no more frames will arrive.
    /// Fail loud rather than leave the last frame looking live: the header lamp reads STREAM LOST (it used to keep its
    /// last verdict, such as SYSTEM NORMAL), the bridge-only HUD is hidden, the bridge-fed OPERATOR, MARKET and NESO RP1
    /// blocks are cleared (they used to keep their last values indefinitely), and one alarm says what the map shows.
    /// </summary>
    public void MarkStreamLost(string reason)
    {
        SolidColorBrush red = Palette.Frozen(Palette.Loading(1.1));
        Status = "STREAM LOST";
        StatusBrush = red;
        HideHud();
        // (ORACLE: Operator.Reset, Market.Reset, NesoRp1.Reset are WPF tabs and are left out.)
        AddAlarm($"{reason}: STREAM LOST - the map and tabs show the last frame received, not live data", red);
    }

    // (ORACLE: UpdateMarketData feeds only WPF tabs and is left out.)

    // ---- colour-mode legend (makes the map tint interpretable: ramp swatch + min/max labels) ----
    private string _legendTitle = "LINE LOADING"; public string LegendTitle { get => _legendTitle; set => Set(ref _legendTitle, value); }
    private Brush _legendBrush = LoadingRamp; public Brush LegendBrush { get => _legendBrush; set => Set(ref _legendBrush, value); }
    private string _legendMin = "0%"; public string LegendMin { get => _legendMin; set => Set(ref _legendMin, value); }
    private string _legendMax = "100%"; public string LegendMax { get => _legendMax; set => Set(ref _legendMax, value); }

    // The four ramps, built once from the same Palette functions the map colours with, so the swatch and the tint agree.
    private static readonly Brush LoadingRamp   = BuildRamp(t => Palette.Loading(t));                  // 0 .. 1.0
    private static readonly Brush VoltageRamp   = BuildRamp(t => Palette.Voltage(0.94 + t * 0.12));    // 0.94 .. 1.06
    private static readonly Brush ConditionRamp = BuildRamp(t => Palette.Condition(t));                // healthy .. critical
    private static readonly Brush StressRamp    = BuildRamp(t => Palette.Stress(t));                   // cool .. hot

    private static Brush BuildRamp(Func<double, Color> ramp, int n = 12)
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i <= n; i++) { double t = i / (double)n; stops.Add(new GradientStop(ramp(t), t)); }
        var b = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
        b.Freeze();
        return b;
    }

    /// <summary>Point the legend swatch + min/max labels at the map's current colour mode.</summary>
    public void SetColorLegend(FastSchematic.ColorModeKind mode)
    {
        (LegendTitle, LegendBrush, LegendMin, LegendMax) = mode switch
        {
            FastSchematic.ColorModeKind.Voltage   => ("VOLTAGE (p.u.)",  VoltageRamp,   "0.94", "1.06"),
            FastSchematic.ColorModeKind.Condition => ("ASSET CONDITION", ConditionRamp, "healthy", "critical"),
            FastSchematic.ColorModeKind.Stress    => ("BRANCH STRESS",   StressRamp,    "cool", "hot"),
            _                                     => ("LINE LOADING",    LoadingRamp,   "0%", "100%"),
        };
    }

    // ---- bridge status surfacing (connection state, horizon rejections, query errors - not just console) ----
    private Visibility _bridgeStatusVis = Visibility.Collapsed; public Visibility BridgeStatusVis { get => _bridgeStatusVis; set => Set(ref _bridgeStatusVis, value); }
    private string _bridgeStatus = ""; public string BridgeStatus { get => _bridgeStatus; set => Set(ref _bridgeStatus, value); }
    private Brush _bridgeStatusBrush = Brushes.Gray; public Brush BridgeStatusBrush { get => _bridgeStatusBrush; set => Set(ref _bridgeStatusBrush, value); }

    /// <summary>Surface the live-bridge state on the map (connecting / live / fault / query error), shown as a small pill.</summary>
    public void SetBridgeStatus(string text, Brush brush)
    {
        BridgeStatus = text;
        BridgeStatusBrush = brush;
        BridgeStatusVis = Visibility.Visible;
    }

    /// <summary>Hide the bridge status pill (leaving bridge mode).</summary>
    public void ClearBridgeStatus() => BridgeStatusVis = Visibility.Collapsed;

    /// <summary>Colour a verdict badge: infeasible/violation = red, feasible/secure = green, otherwise amber.</summary>
    private static Brush VerdictBrush(string? verdict)
    {
        string v = (verdict ?? "").ToUpperInvariant();
        bool bad = v is "INFEASIBLE" or "VIOLATION" or "FAIL" or "INSECURE" or "BREACH" or "UNSECURE";
        bool ok = v is "FEASIBLE" or "SECURE" or "OK" or "PASS";
        return Palette.Frozen(bad ? Palette.Loading(1.1) : ok ? Palette.Voltage(1.0) : Palette.Loading(0.7));
    }

    /// <summary>
    /// Break the timeline: called on model switch and on entering replay so time-integrated state doesn't
    /// carry a previous run's accumulation into the new one. THE MACHINE's integrators are the load-bearing
    /// case; also reset the trip-status lamp and per-onset alarm de-dup so a stale
    /// " FREQUENCY EVENT"/OVERLOAD lamp can't survive the switch.
    /// </summary>
    /// <summary>
    /// A different model is about to be shown (a local model switch, a replay load, a bridge connection or stream
    /// restart). Views that cache per-model structures and detect a switch by a guessed identity (name plus counts)
    /// are told explicitly, because two different networks can share a name and sizes: the SCADA register and the
    /// resident CYCLE service are rebuilt on the next frame, and the per-frame bus lookup is dropped.
    /// </summary>
    public void ModelChanged()
    {
        // (ORACLE: Scada.ForgetModel, Cycle.ForgetModel are WPF tabs and are left out.)
        _busById = null;
        ResetTimeline();
    }

    public void ResetTimeline()
    {
        // (ORACLE: Machine, NesoRp1, Operator, Market, Mechanical .Reset are WPF tabs and are left out.)
        FreqLocked = false;
        InvalidStateVis = Visibility.Collapsed;   // the next frame decides again
        _raised.Clear();
        _lastInBand = true;
        _lastConverged = true;
    }

    // ================= Generation & inertia =================
    public const double FuelBarTrack = 210.0;

    public ObservableCollection<FuelRow> FuelMix { get; } = new();
    private readonly Dictionary<string, FuelRow> _fuelByName = new();

    private string _genTotal = " - "; public string GenTotal { get => _genTotal; set => Set(ref _genTotal, value); }
    private string _syncShare = " - "; public string SyncShare { get => _syncShare; set => Set(ref _syncShare, value); }
    private double _syncBar; public double SyncBar { get => _syncBar; set => Set(ref _syncBar, value); }
    private double _converterBar; public double ConverterBar { get => _converterBar; set => Set(ref _converterBar, value); }
    private string _syncMw = " - "; public string SyncMw { get => _syncMw; set => Set(ref _syncMw, value); }
    private string _converterMw = " - "; public string ConverterMw { get => _converterMw; set => Set(ref _converterMw, value); }
    private string _fossilMw = " - "; public string FossilMw { get => _fossilMw; set => Set(ref _fossilMw, value); }
    private string _renewableMw = " - "; public string RenewableMw { get => _renewableMw; set => Set(ref _renewableMw, value); }
    private string _inertiaGva = " - "; public string InertiaGva { get => _inertiaGva; set => Set(ref _inertiaGva, value); }
    private string _rpm = "3000"; public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
    private double _rpmAngle; public double RpmAngle { get => _rpmAngle; set => Set(ref _rpmAngle, value); }
    // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the GRID SPEED dial's end labels (the frequency dial's ends as a two-pole speed): "2970" / "3030" at 50 Hz.
    private string _rpmDialLowText = "2970"; public string RpmDialLowText { get => _rpmDialLowText; set => Set(ref _rpmDialLowText, value); }
    private string _rpmDialHighText = "3030"; public string RpmDialHighText { get => _rpmDialHighText; set => Set(ref _rpmDialHighText, value); }
    private string _torque = " - "; public string Torque { get => _torque; set => Set(ref _torque, value); }
    private string _co2 = " - "; public string Co2 { get => _co2; set => Set(ref _co2, value); }
    private string _stabilityNote = " - "; public string StabilityNote { get => _stabilityNote; set => Set(ref _stabilityNote, value); }
    private Brush _stabilityBrush = Brushes.Gray; public Brush StabilityBrush { get => _stabilityBrush; set => Set(ref _stabilityBrush, value); }

    public ObservableCollection<ImportLinkRow> ImportLinks { get; } = new();
    // Keyed by generator INDEX, not name: gb-full has two "Auchencrosh (interconnector CCT) interconnector"
    // links (125.1 and 220.2 MW) and name keying collapsed them into one row showing only the last (R055).
    private readonly Dictionary<int, ImportLinkRow> _linkByIndex = new();
    private string _netImport = " - "; public string NetImport { get => _netImport; set => Set(ref _netImport, value); }

    /// <summary>
    /// The import-links panel: every interconnector in the model, shown individually with its
    /// signed flow (positive = importing into GB) against link capacity.
    /// Idle links (|Pg| below the shared 0.5 MW cut-off, StatusLampRules.IsIdleInterconnector) are hidden to keep the
    /// table compact.
    /// </summary>
    private void UpdateImportLinks(GridModel model)
    {
        double net = 0;
        var seen = new Dictionary<int, string>();   // index -> name of every link shown this frame
        IReadOnlyList<Generator> gens = model.Generators;
        for (int gi = 0; gi < gens.Count; gi++)
        {
            Generator g = gens[gi];
            if (Fuels.Classify(g.Type).Class != FuelClass.Interconnector) continue;
            string name = g.Name ?? $"link @ bus {g.Bus}";
            net += g.Pg;

            if (StatusLampRules.IsIdleInterconnector(g.Pg)) continue;   // shared Q10 idle cut-off (0.5 MW)
            seen[gi] = name;

            // A row whose index now holds a differently named link (model swap) is replaced, not reused.
            if (_linkByIndex.TryGetValue(gi, out ImportLinkRow? row) && row.Name != name)
            {
                ImportLinks.Remove(row);
                _linkByIndex.Remove(gi);
                row = null;
            }
            if (row is null)
            {
                row = new ImportLinkRow
                {
                    Index = gi,
                    Name = name,
                    Route = Interconnectors.RouteOf(g.Name),
                    Capacity = $"{g.Pmax:F0} MW",
                };
                _linkByIndex[gi] = row;
                ImportLinks.Add(row);
            }

            // Past the idle test above, the link is active: the sign alone says which way it flows.
            bool importing = g.Pg > 0, exporting = g.Pg < 0;
            row.Flow = $"{g.Pg:+#,0;-#,0;0} MW";
            row.Badge = importing ? "IMPORTING" : exporting ? "EXPORTING" : "IDLE";
            // Centred diverging bar: zero at the track centre, import pulls right, export pulls left.
            double signedFrac = g.Pmax > 0 ? Math.Clamp(g.Pg / g.Pmax, -1.0, 1.0) : 0;
            double half = FuelBarTrack / 2.0, mag = Math.Abs(signedFrac) * half;
            row.BarWidth = mag;
            row.BarOffset = signedFrac >= 0 ? half : half - mag;
            Brush brush = Palette.Frozen(importing ? Palette.Voltage(1.0)
                          : exporting ? Palette.Loading(0.8) : Palette.Voltage(0.9));
            row.Bar = brush;
            row.BadgeBrush = brush;
        }

        for (int i = ImportLinks.Count - 1; i >= 0; i--)
            if (!seen.ContainsKey(ImportLinks[i].Index)) { _linkByIndex.Remove(ImportLinks[i].Index); ImportLinks.RemoveAt(i); }

        NetImport = ImportLinks.Count == 0 ? " - "
            : $"{net / 1000.0:+0.00;-0.00;0.00} GW net {(net >= 0 ? "import" : "export")}";
    }

    private void UpdateGeneration(GridModel model, double freqHz, double genMw)
    {
        UpdateImportLinks(model);

        var byFuel = new Dictionary<string, (double mw, Color col)>();
        var inertiaByFuel = new Dictionary<string, double>();   //  HMBase per fuel (MWs)
        double sync = 0, conv = 0, fossil = 0, renew = 0, gas = 0, total = 0, co2 = 0;

        foreach (Generator g in model.Generators)
        {
            if (g.Pg <= 0) continue;
            FuelInfo fi = Fuels.Classify(g.Type);
            total += g.Pg;
            if (fi.Synchronous) sync += g.Pg; else conv += g.Pg;
            if (fi.IsFossil) fossil += g.Pg;
            if (fi.IsRenewable) renew += g.Pg;
            if (fi.Name is "CCGT" or "OCGT" or "Gas") gas += g.Pg;
            co2 += g.Pg * CarbonFactors.CombustionTonnesPerMwh(fi.Name);   // per-fuel intensity, not a flat blend
            (double mw, Color col) cur = byFuel.GetValueOrDefault(fi.Name);
            byFuel[fi.Name] = (cur.mw + g.Pg, fi.Color);
            // N325 (DOC-17): the rotating-mass share (N289), so the per-fuel bars agree with SystemInertiaGvaS.
            inertiaByFuel[fi.Name] = inertiaByFuel.GetValueOrDefault(fi.Name) + GridModel.RotatingInertiaMwS(g);
        }
        if (total <= 0) total = 1;
        double maxInertiaGva = inertiaByFuel.Values.DefaultIfEmpty(0).Max() / 1000.0;

        // Fuel rows, biggest first, reusing row objects.
        var seen = new HashSet<string>();
        foreach ((string name, (double mw, Color col)) in byFuel.OrderByDescending(kv => kv.Value.mw))
        {
            seen.Add(name);
            if (!_fuelByName.TryGetValue(name, out FuelRow? row))
            {
                row = new FuelRow { Name = name, Color = Palette.Frozen(col) };
                _fuelByName[name] = row;
                FuelMix.Add(row);
            }
            double frac = mw / total;
            row.Mw = $"{mw / 1000.0:F2} GW";
            row.Pct = $"{frac * 100:F0}%";
            row.BarWidth = frac * FuelBarTrack;
            double inGva = inertiaByFuel.GetValueOrDefault(name) / 1000.0;
            row.InertiaGva = inGva > 0.05 ? $"{inGva:F1} GVAs" : " - ";
            row.InertiaBar = maxInertiaGva > 0 ? inGva / maxInertiaGva * FuelBarTrack : 0;
        }
        for (int i = FuelMix.Count - 1; i >= 0; i--)
            if (!seen.Contains(FuelMix[i].Name)) { _fuelByName.Remove(FuelMix[i].Name); FuelMix.RemoveAt(i); }
        // keep biggest-first order, ordering on the underlying MW (not the formatted string)
        var ordered = FuelMix.OrderByDescending(r => byFuel.GetValueOrDefault(r.Name).mw).ToList();
        for (int i = 0; i < ordered.Count; i++) if (!ReferenceEquals(FuelMix[i], ordered[i])) FuelMix.Move(FuelMix.IndexOf(ordered[i]), i);

        double syncFrac = sync / total, convFrac = conv / total;
        GenTotal = $"{total / 1000.0:F2}";
        SyncShare = $"{syncFrac * 100:F0}% synchronous";
        SyncBar = syncFrac * FuelBarTrack;
        ConverterBar = convFrac * FuelBarTrack;
        SyncMw = $"{sync / 1000.0:F2} GW";
        ConverterMw = $"{conv / 1000.0:F2} GW";
        FossilMw = $"{fossil / 1000.0:F2} GW";
        RenewableMw = $"{renew / 1000.0:F2} GW";
        InertiaGva = $"{model.SystemInertiaGvaS:F1}";

        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the case's own nominal and its
        // two-pole synchronous speed (50.0 Hz and exactly 3000.0 rpm for every GB case: the old numbers).
        double nominalHz = model.FrequencyHz;
        double syncRpm = GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);
        double rpm = freqHz / nominalHz * syncRpm;
        Rpm = rpm.ToString("F0");
        RpmAngle = Math.Clamp((freqHz - nominalHz) / FreqDialFullScaleHz, -1, 1) * FreqDialSweepDeg;
        RpmDialLowText = (60.0 * (nominalHz - FreqDialFullScaleHz)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        RpmDialHighText = (60.0 * (nominalHz + FreqDialFullScaleHz)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        Torque = FormatTorqueMnm(total, freqHz);
        Co2 = $"{co2:F0}";                                // tCO2/h from per-fuel intensity (0 for nuclear/wind/solar/hydro)

        // Gas-for-stability: renewables outweigh fossil, yet gas is still spinning  -  kept on
        // for inertia/stability, not energy. The exact thing the TSGB device targets.
        bool gasForStability = renew > fossil && gas > 0;
        StabilityNote = gasForStability
            ? $"GAS FOR STABILITY  {gas / 1000.0:F1} GW spinning"
            : sync / total < LowInertiaSyncFraction ? "LOW INERTIA REGIME" : "synchronous-backed";
        StabilityBrush = Palette.Frozen(gasForStability ? Palette.Loading(0.85)
            : syncFrac < LowInertiaSyncFraction ? Palette.Loading(1.05) : Palette.Voltage(1.0));
    }

    /// <summary>
    /// The GB fleet's shaft torque (MNm) as one machine: T = P / omega with omega = 2 pi f, the LIVE electrical
    /// frequency (a two-pole machine turns at the same angular speed). R209: this used to be <c>P x 3183 Nm/MW</c>,
    /// which is P / omega at exactly 50 Hz (3183 rounds 1e6 / (2 pi 50) = 3183.0989), while the rpm read-out beside
    /// it used the live frequency. Value change, stated: at 50 Hz and 30,000 MW the torque goes from 95.490 to
    /// 95.493 MNm (the F1 read-out stays "95.5"); off nominal it now follows the frequency as the rpm does.
    /// A non-finite or non-positive frequency has no torque, so it shows " - " rather than a number.
    /// </summary>
    public static string FormatTorqueMnm(double totalMw, double freqHz)
    {
        if (!double.IsFinite(freqHz) || freqHz <= 0.0 || !double.IsFinite(totalMw)) return " - ";
        double omegaRadPerS = 2.0 * Math.PI * freqHz;
        double torqueMnm = totalMw * 1e6 / omegaRadPerS / 1e6;   // MW -> W, / omega -> Nm, -> MNm
        return $"{torqueMnm:F1}";
    }

    // Whole-plant carbon intensity (t CO2/MWh) per fuel now lives in
    // GridSim.Core.Presentation.CarbonFactors.CombustionTonnesPerMwh - the single source shared with
    // THE MACHINE tab (MachineVm) and the web MachineService.

    // ================= TSGB device (synthetic inertia + fast response) =================
    private GridModel? _lastModel;
    private double _lastDemand;
    private Dictionary<int, Bus>? _busById;
    private string? _busByIdModelName;
    private int _busByIdBusCount;

    private double _tsgbInertia = 30; public double TsgbInertiaGva { get => _tsgbInertia; set => Set(ref _tsgbInertia, value); }
    private double _tsgbFfr = 2; public double TsgbFfrGw { get => _tsgbFfr; set => Set(ref _tsgbFfr, value); }
    private string _tripBaseline = "Run a largest-unit trip to compare."; public string TripBaseline { get => _tripBaseline; set => Set(ref _tripBaseline, value); }
    private string _tripTsgb = ""; public string TripTsgb { get => _tripTsgb; set => Set(ref _tripTsgb, value); }
    private string _tripVerdict = ""; public string TripVerdict { get => _tripVerdict; set => Set(ref _tripVerdict, value); }
    private Brush _tripVerdictBrush = Brushes.Transparent; public Brush TripVerdictBrush { get => _tripVerdictBrush; set => Set(ref _tripVerdictBrush, value); }
    private string _tripAssessment = ""; public string TripAssessment { get => _tripAssessment; set => Set(ref _tripAssessment, value); }
    private string _tripSizing = ""; public string TripSizing { get => _tripSizing; set => Set(ref _tripSizing, value); }

    /// <summary>The two simulated responses from the last <see cref="TripCompare"/> (for the trace plot).</summary>
    public FrequencyResponse? BaselineResponse { get; private set; }
    public FrequencyResponse? TsgbResponse { get; private set; }
    /// <summary>N297 (B4, 2026-10): the bands the last compare was graded against (the GB bands about the case's
    /// nominal), so the trace plot draws its limit lines about the same nominal.</summary>
    public GridSim.Core.Dynamics.FrequencyBands TripBands { get; private set; } = GridSim.Core.Dynamics.FrequencyBands.Gb;
    /// <summary>Raised after <see cref="TripCompare"/> so the view can refresh the trace plot.</summary>
    public event Action? TripCompared;

    /// <summary>Simulate the largest-unit trip with and without TSGB and report the RoCoF/nadir improvement.</summary>
    public void TripCompare()
    {
        if (_lastModel is null) return;
        GridModel m = _lastModel;
        // N302 (LF3, October 2026; B4 NF-2): trip the largest SECURED unit, the one the NESO RP1 / OPERATOR credible loss
        // names (FrequencyRiskPresenter, N039: any single infeed, never a grid infeed). This used to take the largest Pg
        // of any generator, grid infeed included, so on a model whose upstream network equivalent carried more than every
        // unit the compare tripped the rest of the grid. Same unit as before on every model without a grid infeed
        // (largest strictly positive Pg, first of equal units: OrderByDescending was stable too).
        Generator? gen = FrequencyRiskPresenter.LargestSecuredGenerator(m.Generators);
        if (gen is null) return;

        // N325 (DOC-17): the N289 rotating-mass total and the tripped unit's own share of it, as gridsim trip uses since
        // N314 (this used to sum H x MBase over EVERY unit). Bit for bit the same on every model without a converter-typed
        // unit with H > 0 (every bundled case).
        double totalInertia = m.SystemInertiaMwS;
        double remaining = Math.Max(1.0, totalInertia - GridModel.RotatingInertiaMwS(gen));
        double demand = Math.Max(1.0, _lastDemand);
        AnalyseTrip(remaining, demand, gen.Pg, gen.Name ?? $"unit @ bus {gen.Bus}");
    }

    /// <summary>
    /// Run a trip (a <paramref name="lostMw"/> deficit on <paramref name="remainingInertiaMwS"/> of
    /// spinning energy) with and without the TSGB, score it against the GB limits, size the device
    /// to secure it, and publish the two traces. Shared by the Generation-tab compare and a
    /// network-map right-click trip, so either drives the same plot + read-outs.
    /// </summary>
    public void AnalyseTrip(double remainingInertiaMwS, double demand, double lostMw, string label)
    {
        // N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the compare runs at the current case's
        // nominal (the last frame's model; GB 50 Hz before the first frame) and grades against the GB bands about it, as
        // the web TRIP / DEVICE and TSGB tabs do (TripWhatIf). At 50 Hz the config is the default one it always used.
        double nominalHz = _lastModel?.FrequencyHz ?? FrequencyLimits.NominalHz;
        GridSim.Core.Dynamics.FrequencyBands bands = GridSim.Core.Dynamics.FrequencyBands.ForNominal(nominalHz);
        DynamicsConfig studyCfg = TripWhatIf.StudyConfig(nominalHz);
        var ev = new[] { new FrequencyEvent(1.0, -Math.Abs(lostMw)) };
        FrequencyResponse baseResp = FrequencyDynamics.Simulate(remainingInertiaMwS, demand, ev, studyCfg);
        var tsgbCfg = studyCfg with { DeviceRotationalInertiaMwS = TsgbInertiaGva * 1000.0, FastResponseMw = TsgbFfrGw * 1000.0 };
        FrequencyResponse tsgbResp = FrequencyDynamics.Simulate(remainingInertiaMwS, demand, ev, tsgbCfg);

        BaselineResponse = baseResp;
        TsgbResponse = tsgbResp;
        TripBands = bands;

        TripBaseline = $"{label} -{Math.Abs(lostMw):F0} MW";
        TripTsgb = $"Baseline RoCoF {baseResp.InitialRocofHzPerS:F3} Hz/s  nadir {baseResp.NadirHz:F2} Hz   ->   +TSGB {tsgbResp.InitialRocofHzPerS:F3} Hz/s  {tsgbResp.NadirHz:F2} Hz";

        // Score the baseline against the GB limits (about the case's nominal), and size the device that would secure
        // this loss (R148: the one pair of targets, TripWhatIf: RoCoF <= 0.125 Hz/s and nadir held at the statutory floor).
        FrequencyAssessment a = FrequencyLimits.Assess(baseResp, FrequencyLimits.ActiveRocofLimitHzPerS, bands);
        double estGva = InertiaEstimator.EstimateInertiaGvaS(lostMw, baseResp.InitialRocofHzPerS, nominalHz);
        TripAssessment = $"Baseline vs limits: {a.Summary}    inertia from RoCoF ~= {estGva:F0} GVAs";
        TsgbSizing sizing = TsgbSizer.SizeForTargets(
            remainingInertiaMwS, demand, lostMw, TripWhatIf.SizingMaxRocofHzPerS, TripWhatIf.SizingMinNadirHz(bands), studyCfg);
        // R254: say so when the sizer could not hold the nadir target (it used to print the largest size it tried as if
        // it secured the loss); the text is unchanged when the target is met.
        TripSizing = $"To secure this loss ({TripWhatIf.SizingTargetsText(bands)}): +{sizing.SyntheticInertiaMwS / 1000:F0} GVAs inertia  {sizing.FastResponseMw / 1000:F1} GW fast response"
            + (sizing.Feasible ? "" : $"  (NOT ACHIEVED: nadir {sizing.AchievedNadirHz:F2} Hz at the largest size searched)");

        double rocofCut = (1 - Math.Abs(tsgbResp.InitialRocofHzPerS) / Math.Max(1e-6, Math.Abs(baseResp.InitialRocofHzPerS))) * 100;
        double nadirLift = tsgbResp.NadirHz - baseResp.NadirHz;
        bool helps = rocofCut > 0.5 || nadirLift > 0.005;
        TripVerdict = helps
            ? $"TSGB cuts RoCoF {rocofCut:F0}% and lifts the nadir +{nadirLift:F2} Hz"
            : "Add TSGB capacity to see the effect.";
        TripVerdictBrush = Palette.Frozen(helps ? Palette.Voltage(1.0) : Palette.Loading(0.8));
        Log(Clock, $" TSGB compare  trip {label} {Math.Abs(lostMw):F0} MW  RoCoF {baseResp.InitialRocofHzPerS:F2}->{tsgbResp.InitialRocofHzPerS:F2} Hz/s");
        TripCompared?.Invoke();
    }

    private Visibility _alarmsEmptyVis = Visibility.Visible;
    public Visibility AlarmsEmptyVis { get => _alarmsEmptyVis; set => Set(ref _alarmsEmptyVis, value); }

    public void AddAlarm(string text, Brush brush)
    {
        Alarms.Insert(0, new AlarmRow(Clock, text, brush));
        while (Alarms.Count > AlarmListCapacity) Alarms.RemoveAt(Alarms.Count - 1);
        AlarmsEmptyVis = Visibility.Collapsed;
    }

    public void ClearAlarms()
    {
        Alarms.Clear();
        _raised.Clear();
        AlarmsEmptyVis = Visibility.Visible;
    }
}

/// <summary>A label/value pair for the detail fly-out.</summary>
public sealed record KeyVal(string Key, string Value);

/// <summary>One row in the alarms panel.</summary>
public sealed record AlarmRow(string Time, string Text, Brush Brush);

/// <summary>One constraint violation from an on-demand GDA-bridge query, projected for the results panel.</summary>
public sealed class ViolationRow
{
    public string Kind { get; init; } = "";
    public string Element { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Message { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Gray;

    public static ViolationRow From(Violation v)
    {
        string detail =
            v.Value is double val && v.Limit is double lim ? $"{val:0.###} / {lim:0.###}"
            : v.Value is double only ? $"{only:0.###}"
            : v.Limit is double lo ? $"limit {lo:0.###}"
            : "";
        return new ViolationRow
        {
            Kind = v.Kind ?? "",
            Element = string.IsNullOrWhiteSpace(v.Element) ? (v.Kind ?? "violation") : v.Element!,
            Detail = detail,
            Message = v.Message ?? "",
            Brush = Palette.Frozen(Palette.Loading(1.1)),
        };
    }
}

/// <summary>One interconnector's row in the import-links panel: signed flow against capacity.</summary>
public sealed class ImportLinkRow : Notify
{
    /// <summary>The link's index in the model's generator list: the row's identity (link names repeat).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Route { get; init; } = "";
    public string Capacity { get; init; } = "";
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _badge = ""; public string Badge { get => _badge; set => Set(ref _badge, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private double _barOffset; public double BarOffset { get => _barOffset; set => Set(ref _barOffset, value); }   // px from track left to bar start (centred diverging bar)
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }
    private Brush _badgeBrush = Brushes.Gray; public Brush BadgeBrush { get => _badgeBrush; set => Set(ref _badgeBrush, value); }
}

/// <summary>One fuel's contribution in the generation-mix panel  -  its MW share and the inertia
/// it spins (so the gen-mix and inertia-by-class boxes share one ordered list).</summary>
public sealed class FuelRow : Notify
{
    public string Name { get; init; } = "";
    public Brush Color { get; init; } = Brushes.Gray;
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _pct = ""; public string Pct { get => _pct; set => Set(ref _pct, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private string _inertiaGva = " - "; public string InertiaGva { get => _inertiaGva; set => Set(ref _inertiaGva, value); }
    private double _inertiaBar; public double InertiaBar { get => _inertiaBar; set => Set(ref _inertiaBar, value); }
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
