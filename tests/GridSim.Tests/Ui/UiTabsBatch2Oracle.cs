using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Presentation;
using Color = GridSim.Ui.Presentation.UiColour;
using DashboardViewModel = GridSim.Tests.Ui.OldDashboard.DashboardViewModel;
using FuelInfo = GridSim.Tests.Ui.OldDashboard.FuelInfo;
using Fuels = GridSim.Tests.Ui.OldDashboard.Fuels;

// Test oracle for the UI programme Phase 1.6 tab parity tests (UiTabsBatch2ParityTests). NOT production code.
//
// Below are four WPF view-models exactly as they were before their logic moved to src/GridSim.Ui/Tabs (the
// src/GridSim.Wpf/Views files InspectorVm.cs, MachineVm.cs, MachineEngineerVm.cs and InverterFleetVm.cs at 2.0.25).
// The text was copied mechanically from git by a script (each file's using and namespace lines dropped, nothing else
// touched); the only edits are:
//   - the WPF namespaces (System.Windows, System.Windows.Media, GridSim.Wpf.Visuals, GridSim.Wpf.Views.Assets) are
//     replaced by the small shims at the top of this file, which carry exactly what the old code read from them: a
//     Brush is a colour (a SolidColorBrush holds one UiColour), Palette.Frozen caches one brush per colour as the real
//     one does, Palette's ramps are the neutral ColourRamps (UiColourRampsTests pins those byte for byte against the
//     old WPF arithmetic), Brushes.Gray / White / Transparent carry the WPF named colours' bytes, Visibility is the two
//     values the views used, and RelayCommand / RelayCommand<T> are the WPF asset pages' two commands, copied;
//   - the old WPF Fuels (verbatim in UiDashboardOracle.cs, pinned against UiFuels there) and the old dashboard's
//     KvLabel are used from that oracle;
//   - five lines marked "ORACLE:": MachineEngineerVm's fully qualified RelayCommand<MachineRow> names the shim, and the four
//     InverterFleetVm brushes write Color.FromRgb(r, g, b) as the opaque colour it is (alpha 0xFF).
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

namespace GridSim.Tests.Ui.OldTabsBatch2;

// ------------------------------------------------ shims ------------------------------------------------

/// <summary>Shim of the WPF brush: the old code only ever read a brush's colour.</summary>
public abstract class Brush { }

/// <summary>Shim of the WPF solid brush: one colour.</summary>
public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color c) { Color = c; }
    public Color Color { get; }
}

/// <summary>Shim of the WPF named brushes the tabs used, with the WPF colours' exact bytes.</summary>
public static class Brushes
{
    public static readonly SolidColorBrush Gray = new(new Color(0xFF, 0x80, 0x80, 0x80));          // Colors.Gray
    public static readonly SolidColorBrush White = new(new Color(0xFF, 0xFF, 0xFF, 0xFF));         // Colors.White
    public static readonly SolidColorBrush Transparent = new(new Color(0x00, 0xFF, 0xFF, 0xFF));   // Colors.Transparent
}

/// <summary>Shim of the WPF Visibility values the views used.</summary>
public enum Visibility { Visible, Collapsed }

/// <summary>Shim of the WPF Palette at 2.0.25: the ramps were already the neutral ColourRamps, converted byte for byte;
/// Frozen returns one cached brush per colour.</summary>
public static class Palette
{
    public static Color FromHex(string hex) => ColourRamps.FromHex(hex);
    public static Color Voltage(double vm) => ColourRamps.Voltage(vm);
    public static Color Loading(double frac) => ColourRamps.Loading(frac);

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

/// <summary>The WPF asset pages' minimal ICommand (src/GridSim.Wpf/Views/Assets/AssetPages.cs), copied.</summary>
public sealed class RelayCommand(Action exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec();
}

/// <summary>The WPF asset pages' parameter-passing ICommand, copied.</summary>
public sealed class RelayCommand<T>(Action<T> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) { if (p is T t) exec(t); }
}

/// <summary>The WPF minimal INotifyPropertyChanged base (src/GridSim.Wpf/DashboardViewModel.cs), copied.</summary>
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

// ------------------------------------------------ the old Views/InspectorVm.cs ------------------------------------------------

/// <summary>
/// A reusable drill-down overlay for the RP1 / MECHANICAL tabs: click an aggregate (a fuel, a
/// boundary, an interconnector, a machine) and this shows how it is made up - the constituent items,
/// plus combined totals and averages - for the currently-displayed frame. Rows can themselves drill
/// further (a back-stack keeps the trail), so a paused frame becomes fully explorable.
/// </summary>
public sealed class InspectorVm : Notify
{
    public ObservableCollection<InspectStat> Stats { get; } = new();
    public ObservableCollection<InspectRow> Rows { get; } = new();

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set { Set(ref _isOpen, value); Raise(nameof(Visibility)); } }
    public Visibility Visibility => _isOpen ? Visibility.Visible : Visibility.Collapsed;

    private string _title = ""; public string Title { get => _title; set => Set(ref _title, value); }
    private string _subtitle = ""; public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    private bool _canBack; public bool CanBack { get => _canBack; private set { Set(ref _canBack, value); Raise(nameof(BackVisibility)); } }
    public Visibility BackVisibility => _canBack ? Visibility.Visible : Visibility.Collapsed;

    private readonly Stack<Snapshot> _back = new();
    private sealed record Snapshot(string Title, string Subtitle, IReadOnlyList<InspectStat> Stats, IReadOnlyList<InspectRow> Rows);

    public ICommand CloseCommand { get; }
    public ICommand BackCommand { get; }

    public InspectorVm()
    {
        CloseCommand = new RelayCommand(Close);
        BackCommand = new RelayCommand(Back);
    }

    /// <summary>Open (or push a new page onto) the inspector with a fresh title, stat tiles and rows.</summary>
    public void Show(string title, string subtitle, IEnumerable<InspectStat> stats, IEnumerable<InspectRow> rows)
    {
        if (_isOpen)
            _back.Push(new Snapshot(Title, Subtitle, new List<InspectStat>(Stats), new List<InspectRow>(Rows)));
        CanBack = _back.Count > 0;
        LoadPage(title, subtitle, stats, rows);
        IsOpen = true;
    }

    private void LoadPage(string title, string subtitle, IEnumerable<InspectStat> stats, IEnumerable<InspectRow> rows)
    {
        Title = title; Subtitle = subtitle;
        Stats.Clear(); foreach (InspectStat s in stats) Stats.Add(s);
        Rows.Clear(); foreach (InspectRow r in rows) Rows.Add(r);
    }

    private void Back()
    {
        if (_back.Count == 0) { Close(); return; }
        Snapshot s = _back.Pop();
        CanBack = _back.Count > 0;
        LoadPage(s.Title, s.Subtitle, s.Stats, s.Rows);
    }

    public void Close()
    {
        _back.Clear();
        CanBack = false;
        IsOpen = false;
    }
}

/// <summary>A headline combined/averaged figure shown as a tile at the top of an inspector page.</summary>
public sealed class InspectStat : Notify
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public Brush ValueBrush { get; init; } = Brushes.White;
}

/// <summary>One constituent row in an inspector page; may carry a command to drill further.</summary>
public sealed class InspectRow : Notify
{
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public Brush Swatch { get; init; } = Brushes.Transparent;
    public Brush ValueBrush { get; init; } = Brushes.White;
    public ICommand? Drill { get; init; }
    public Visibility Chevron => Drill is null ? Visibility.Collapsed : Visibility.Visible;
}

// ------------------------------------------------ the old Views/MachineVm.cs ------------------------------------------------

/// <summary>One energy stream in or out of the machine (a fuel feeding it, or a region drawing from it).</summary>
public sealed class StreamRow : Notify
{
    /// <summary>The row's identity (a fuel bucket name, or "bus:&lt;id&gt;" for a demand row). Display names repeat
    /// (two buses called "Bolney 132kV"), so rows are never keyed by <see cref="Name"/>.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public Brush Color { get; init; } = Brushes.Gray;
    private string _gw = ""; public string Gw { get => _gw; set => Set(ref _gw, value); }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Inertia this stream contributes  -  fuel buckets only (demand rows leave it " - ").
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
}

/// <summary>One voltage level of the transmission "shaft"  -  how much power rides each layer,
/// and how far that layer's solved voltage sits above/below its nominal kV.</summary>
public sealed class VoltageLayer : Notify
{
    public string Label { get; init; } = "";
    private Brush _color = Brushes.Gray; public Brush Color { get => _color; set => Set(ref _color, value); }
    private string _gw = ""; public string Gw { get => _gw; set => Set(ref _gw, value); }
    private string _share = ""; public string Share { get => _share; set => Set(ref _share, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Actual-vs-nominal voltage on this tier (the grid is never perfectly nominal).
    private string _kvActual = " - "; public string KvActual { get => _kvActual; set => Set(ref _kvActual, value); } // "402 kV"
    private string _kvDev = " - "; public string KvDev { get => _kvDev; set => Set(ref _kvDev, value); }             // "+0.5%  +2 kV"
    private Brush _devBrush = Brushes.Gray; public Brush DevBrush { get => _devBrush; set => Set(ref _devBrush, value); }
    private double _devBar = MachineVm.BarTrack / 2; public double DevBar { get => _devBar; set => Set(ref _devBar, value); }
}

/// <summary>One on-load tap changer in the fleet  -  where its tap sits relative to nominal,
/// and how well it is holding its controlled bus to target.</summary>
public sealed class TapRow : Notify
{
    /// <summary>The row's identity: the OLTC record's index (two OLTCs may name the same transformer).</summary>
    public int OltcIndex { get; init; }
    public string Name { get; init; } = "";                                                       // transformer name
    private string _offNom = " - "; public string OffNom { get => _offNom; set => Set(ref _offNom, value); } // "+3.8% off nominal"
    private string _step = " - "; public string Step { get => _step; set => Set(ref _step, value); }         // "tap +3 of +/-8"
    private string _err = " - "; public string Err { get => _err; set => Set(ref _err, value); }             // "holds 33 kV  -0.4% vs target"
    private Brush _color = Brushes.Gray; public Brush Color { get => _color; set => Set(ref _color, value); }
    private double _barWidth = MachineVm.BarTrack / 2; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private bool _atLimit; public bool AtLimit { get => _atLimit; set => Set(ref _atLimit, value); }
}

/// <summary>
/// THE MACHINE  -  the whole grid reduced to one rotating machine for the layman.
/// Fuel pushes torque in on the left, regions draw it off on the right, and the
/// transmission system is the shaft between them, layered by voltage. The frequency
/// story is told as torque: the torque needed to hold 50.00 Hz versus the torque the
/// fuel is actually delivering. The gap is why frequency moves; its running integral
/// is the over/under-spin the system has banked  -  when the grid runs above 50 Hz
/// that surplus is a regeneration window, and the BESS charges on it.
/// </summary>
public sealed class MachineVm : Notify
{
    public const double BarTrack = 170.0;
    private const int TraceLength = 120;

    public ObservableCollection<StreamRow> FuelIn { get; } = new();
    public ObservableCollection<VoltageLayer> Layers { get; } = new();
    public ObservableCollection<StreamRow> DemandOut { get; } = new();
    public ObservableCollection<TapRow> Taps { get; } = new();
    private readonly Dictionary<string, StreamRow> _fuelByName = new();
    private readonly Dictionary<string, VoltageLayer> _layerByLabel = new();
    private readonly Dictionary<string, StreamRow> _demandByName = new();   // keyed by StreamRow.Key ("bus:<id>")
    private readonly Dictionary<int, TapRow> _tapByOltc = new();             // keyed by OLTC record index

    // ---- headline + machine state ----
    private string _totalIn = " - "; public string TotalIn { get => _totalIn; set => Set(ref _totalIn, value); }
    private string _totalOut = " - "; public string TotalOut { get => _totalOut; set => Set(ref _totalOut, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    private string _freq = "50.00"; public string Freq { get => _freq; set => Set(ref _freq, value); }
    private string _rpm = "3000"; public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
    private double _rpmValue = 3000; public double RpmValue { get => _rpmValue; set => Set(ref _rpmValue, value); }
    // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the GRID SPEED gauge spans the
    // statutory band about the case's own nominal as a two-pole speed: 2970 - 3030 rpm at 50 Hz (as the XAML had).
    private double _rpmGaugeMin = 2970; public double RpmGaugeMin { get => _rpmGaugeMin; set => Set(ref _rpmGaugeMin, value); }
    private double _rpmGaugeMax = 3030; public double RpmGaugeMax { get => _rpmGaugeMax; set => Set(ref _rpmGaugeMax, value); }
    private string _freqTrend = " - "; public string FreqTrend { get => _freqTrend; set => Set(ref _freqTrend, value); }
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _spinEnergy = " - "; public string SpinEnergy { get => _spinEnergy; set => Set(ref _spinEnergy, value); }
    private string _syncSplit = " - "; public string SyncSplit { get => _syncSplit; set => Set(ref _syncSplit, value); }
    private string _co2 = " - "; public string Co2 { get => _co2; set => Set(ref _co2, value); }

    private IReadOnlyList<double> _freqTrace = Array.Empty<double>();
    public IReadOnlyList<double> FreqTrace { get => _freqTrace; private set => Set(ref _freqTrace, value); }

    // ---- the torque story: dials read live doubles, the captions stay as text ----
    private double _torqueCmdValue; public double TorqueCmdValue { get => _torqueCmdValue; set => Set(ref _torqueCmdValue, value); }
    private double _torqueActValue; public double TorqueActValue { get => _torqueActValue; set => Set(ref _torqueActValue, value); }
    private string _torqueCmdText = " - "; public string TorqueCmdText { get => _torqueCmdText; set => Set(ref _torqueCmdText, value); }
    private string _torqueActText = " - "; public string TorqueActText { get => _torqueActText; set => Set(ref _torqueActText, value); }
    private double _stressPct; public double StressPct { get => _stressPct; set => Set(ref _stressPct, value); }
    private string _stressText = " - "; public string StressText { get => _stressText; set => Set(ref _stressText, value); }
    private string _torqueDiv = " - "; public string TorqueDiv { get => _torqueDiv; set => Set(ref _torqueDiv, value); }
    private string _divMw = " - "; public string DivMw { get => _divMw; set => Set(ref _divMw, value); }
    private Brush _divBrush = Brushes.Gray; public Brush DivBrush { get => _divBrush; set => Set(ref _divBrush, value); }
    private string _divNote = " - "; public string DivNote { get => _divNote; set => Set(ref _divNote, value); }

    // ---- mains clock divergence ----
    private string _accum = " - "; public string Accum { get => _accum; set => Set(ref _accum, value); }
    private string _timeError = " - "; public string TimeError { get => _timeError; set => Set(ref _timeError, value); }
    private string _timeErrorNote = " - "; public string TimeErrorNote { get => _timeErrorNote; set => Set(ref _timeErrorNote, value); }
    private string _accumNote = " - "; public string AccumNote { get => _accumNote; set => Set(ref _accumNote, value); }
    private Brush _accumBrush = Brushes.Gray; public Brush AccumBrush { get => _accumBrush; set => Set(ref _accumBrush, value); }

    // ---- potential missed regen  -  "saving if taken" ----
    private string _regenMwhText = " - "; public string RegenMwhText { get => _regenMwhText; set => Set(ref _regenMwhText, value); }
    private string _regenMoney = " - "; public string RegenMoney { get => _regenMoney; set => Set(ref _regenMoney, value); }
    private string _regenHomes = " - "; public string RegenHomes { get => _regenHomes; set => Set(ref _regenHomes, value); }

    // ---- the BESS ----
    private string _bessPower = " - "; public string BessPower { get => _bessPower; set => Set(ref _bessPower, value); }
    private string _bessMode = "STANDBY"; public string BessMode { get => _bessMode; set => Set(ref _bessMode, value); }
    private Brush _bessBrush = Brushes.Gray; public Brush BessBrush { get => _bessBrush; set => Set(ref _bessBrush, value); }
    private string _bessSocText = "50%"; public string BessSocText { get => _bessSocText; set => Set(ref _bessSocText, value); }
    private double _bessSocBar = BarTrack / 2; public double BessSocBar { get => _bessSocBar; set => Set(ref _bessSocBar, value); }
    private string _bessEnergy = " - "; public string BessEnergy { get => _bessEnergy; set => Set(ref _bessEnergy, value); }
    private string _bessThroughput = " - "; public string BessThroughput { get => _bessThroughput; set => Set(ref _bessThroughput, value); }

    // ================= the electrical story (voltage / reactive / taps) =================
    // The machine is never perfectly nominal: voltage rides above/below 1.0 pu just as
    // speed rides off 50 Hz, and the tap-changer fleet is the inertia of voltage  - 
    // the standing correction that holds it.

    // ---- system voltage ----
    private string _voltAvg = " - "; public string VoltAvg { get => _voltAvg; set => Set(ref _voltAvg, value); }            // "1.012 pu"
    private double _voltAvgValue = 1.0; public double VoltAvgValue { get => _voltAvgValue; set => Set(ref _voltAvgValue, value); } // dial
    private string _voltDev = " - "; public string VoltDev { get => _voltDev; set => Set(ref _voltDev, value); }            // "+1.2% above nominal"
    private string _voltSpread = " - "; public string VoltSpread { get => _voltSpread; set => Set(ref _voltSpread, value); }      // "0.981-1.043 pu"
    private string _voltSpreadKv = " - "; public string VoltSpreadKv { get => _voltSpreadKv; set => Set(ref _voltSpreadKv, value); } // "lowest 129 kV  highest 421 kV"
    private Brush _voltBrush = Brushes.Gray; public Brush VoltBrush { get => _voltBrush; set => Set(ref _voltBrush, value); }
    private IReadOnlyList<double> _voltTrace = Array.Empty<double>();
    public IReadOnlyList<double> VoltTrace { get => _voltTrace; private set => Set(ref _voltTrace, value); }

    // ---- reactive balance: the Q story, parallel to torque ----
    private double _qGenValue; public double QGenValue { get => _qGenValue; set => Set(ref _qGenValue, value); }
    private string _qGenText = " - "; public string QGenText { get => _qGenText; set => Set(ref _qGenText, value); }
    private string _qAbsText = " - "; public string QAbsText { get => _qAbsText; set => Set(ref _qAbsText, value); }
    private string _qNet = " - "; public string QNet { get => _qNet; set => Set(ref _qNet, value); }
    private string _qNote = " - "; public string QNote { get => _qNote; set => Set(ref _qNote, value); }
    private Brush _qBrush = Brushes.Gray; public Brush QBrush { get => _qBrush; set => Set(ref _qBrush, value); }
    private double _qScaleMax = 2000; public double QScaleMax { get => _qScaleMax; set => Set(ref _qScaleMax, value); }

    // ---- tap-changer fleet: the standing voltage correction ----
    private string _tapAvg = " - "; public string TapAvg { get => _tapAvg; set => Set(ref _tapAvg, value); }
    private string _tapLimits = " - "; public string TapLimits { get => _tapLimits; set => Set(ref _tapLimits, value); }
    private string _tapRegErr = " - "; public string TapRegErr { get => _tapRegErr; set => Set(ref _tapRegErr, value); }
    private string _tapSummary = " - "; public string TapSummary { get => _tapSummary; set => Set(ref _tapSummary, value); }
    private double _tapPosValue; public double TapPosValue { get => _tapPosValue; set => Set(ref _tapPosValue, value); }   // avg % off nominal, dial
    private string _tapPosText = " - "; public string TapPosText { get => _tapPosText; set => Set(ref _tapPosText, value); }
    private double _regStressPct; public double RegStressPct { get => _regStressPct; set => Set(ref _regStressPct, value); }
    private string _regStressText = " - "; public string RegStressText { get => _regStressText; set => Set(ref _regStressText, value); }

    private readonly List<double> _voltHistory = new();
    private readonly List<double> _freqHistory = new();
    private double _accumHzS;          // (f - 50)dt over sim time
    private double _regenMwh;          //  max(0, surplus)dt  -  the harvestable over-spin
    // ---- named narrative constants (R207: were literals; the same names and values as the web PresentationLimits,
    // pinned equal by PresentationConstantsAntiDriftTests) ----
    private const double NominalHz = GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
    /// <summary>A two-pole synchronous machine's speed (rpm) at nominal frequency.</summary>
    public const double TwoPoleSyncRpm = GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpm;   // N023: the one shared copy (was the literal 3000.0)
    /// <summary>Droop-implied surplus or deficit (MW) beyond which the torque story leaves "in balance".</summary>
    public const double TorqueNarrativeBandMw = GridSim.Core.Presentation.PresentationConstants.TorqueNarrativeBandMw;   // N023: the one shared copy (was the literal 200.0)
    /// <summary>Small weight (MW) added to each bus's demand in the demand-weighted average voltage.</summary>
    public const double VoltageWeightEpsilonMw = GridSim.Core.Presentation.PresentationConstants.VoltageWeightEpsilonMw;   // N023: the one shared copy (was the literal 1e-6)
    /// <summary>OLTC deadband (pu) assumed for the regulation dial when the model has no OLTCs.</summary>
    public const double DefaultOltcDeadbandPu = GridSim.Core.Presentation.PresentationConstants.DefaultOltcDeadbandPu;   // N023: the one shared copy (was the literal 0.01)
    /// <summary>A tap within this fraction of one step of its travel limit counts as AT LIMIT.</summary>
    public const double TapAtLimitStepFraction = GridSim.Core.Presentation.PresentationConstants.TapAtLimitStepFraction;   // N023: the one shared copy (was the literal 0.5)
    /// <summary>The narrative grid battery's state of charge (%) at the start of a run and after a timeline break.</summary>
    public const double BessInitialSocPct = GridSim.Core.Presentation.PresentationConstants.BessInitialSocPct;   // N023: the one shared copy (was the literal 50.0)

    private double _bessSoc = BessInitialSocPct;      // % of the nominal battery
    private double _bessMovedMwh;      // total energy through the battery, both directions
    private double _lastHour = -1, _lastHz = NominalHz;
    private int _lastTick = int.MinValue;   // monotonic in both live and replay; a decrease = backward/discontinuity

    // BESS cap / response, governor droop (4%), stress band, and the potential-missed-regen
    // valuation (price + per-home figures, both stated as assumptions in the view) now live in
    // GridSim.Core.Presentation.EnergyEconomicsConstants - the single source shared with the web MachineService.

    // Dial ranges live in the XAML (GRID SPEED 2970-3030, torque 0-200 MNm  -  peak
    // demand ~60 GW at 50 Hz is 60e9 / (2pi50) ~= 190 MNm, so 200 leaves headroom).

    /// <summary>Layman fuel buckets  -  coarser than the Generation tab's mix.</summary>
    private static (string name, Color col) Bucket(FuelInfo fi) => fi.Name switch
    {
        "CCGT" or "OCGT" or "Gas" or "CHP" or "Coal" => ("GAS & THERMAL", Palette.FromHex("#FF8A3D")),
        "Nuclear" => ("NUCLEAR", Palette.FromHex("#7E57C2")),
        "Wind (offshore)" or "Wind (onshore)" => ("WIND", Palette.FromHex("#00B8D4")),
        "Solar" => ("SOLAR", Palette.FromHex("#FFD54F")),
        "Hydro" or "Pumped storage" or "Tidal" => ("HYDRO", Palette.FromHex("#29B6F6")),
        "Biomass" => ("BIOMASS", Palette.FromHex("#8BC34A")),
        "Interconnector" or "Grid infeed" => ("IMPORTS", Palette.FromHex("#90A4AE")),
        "Storage" => ("STORAGE", Palette.FromHex("#EC407A")),
        _ => ("OTHER", Palette.FromHex("#9E9E9E")),
    };

    public void Update(SimFrame f)
    {
        GridModel m = f.Model;
        double hz = f.FrequencyHz;
        var busById = m.Buses.ToDictionary(b => b.Id);
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the frame's own nominal (50.0, the
        // constant, for every GB case, so every number below is unchanged there); a fresh or reset history starts its
        // trend at it.
        double nominalHz = m.FrequencyHz;
        if (_lastHour < 0) _lastHz = nominalHz;
        GridSim.Core.Dynamics.FrequencyBands bands = GridSim.Core.Dynamics.FrequencyBands.ForNominal(nominalHz);
        RpmGaugeMin = 60.0 * bands.StatutoryLowHz;
        RpmGaugeMax = 60.0 * bands.StatutoryHighHz;

        // Sim time elapsed since the last frame (the day wraps at midnight). Replay's step-back / scrubber-drag /
        // restart move Tick (and Hour) backwards; the old `(delta+24)%24` wrap treated that as ~24h of FORWARD time and
        // banked hours of phantom Hz*s / regen GBP / BESS throughput. A Tick decrease (or the first frame) is a
        // discontinuity: no time elapsed, don't integrate.
        bool discontinuity = _lastHour < 0 || f.Tick < _lastTick;
        double dtHours = discontinuity ? 0 : (f.Hour - _lastHour + 24) % 24;
        _lastHour = f.Hour;
        _lastTick = f.Tick;

        UpdateFuel(m, f.GenerationMw);
        UpdateLayers(m, f, busById);
        UpdateDemand(m, f.DemandMw);

        // Losses: use the solved power-flow figure (I^2R + shunt) so this agrees with the
        // NETWORK tab. The old max(0, Gen - Demand) read 0 whenever generation trailed demand.
        double losses = f.Result.TotalLossMw;
        TotalIn = $"{f.GenerationMw / 1000.0:F1}";
        TotalOut = $"{f.DemandMw / 1000.0:F1}";
        Losses = $"{losses:F0} MW lost as heat in the wires";

        // The machine itself: speed, stored spin, what's actually spinning.
        double rpm = hz / nominalHz * GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);
        Freq = hz.ToString("F2");
        Rpm = rpm.ToString("F0");
        RpmValue = rpm;                 // the GRID SPEED dial reads this directly
        FreqTrend = $"{hz - _lastHz:+0.00;-0.00;0.00} Hz since last tick";
        _lastHz = hz;
        // N325 (DOC-17): the same rotating-mass total the Inertia read-out on the next line shows (SystemInertiaGvaS is this
        // divided by 1000); it used to sum H x MBase over EVERY unit, so the two figures could disagree.
        double inertiaMwS = m.SystemInertiaMwS;
        Inertia = $"{m.SystemInertiaGvaS:F0}";
        SpinEnergy = $"~= {inertiaMwS / 3600.0:F0} MWh held as rotation";
        double sync = 0, conv = 0, total = 0, co2 = 0;
        foreach (Generator g in m.Generators)
        {
            if (g.Pg <= 0) continue;
            total += g.Pg;
            FuelInfo fi = Fuels.Classify(g.Type);
            if (fi.Synchronous) sync += g.Pg; else conv += g.Pg;
            co2 += g.Pg * CarbonFactors.CombustionTonnesPerMwh(fi.Name);   // per-fuel, not a flat blend
        }
        SyncSplit = $"{sync / 1000.0:F1} GW spinning iron  {conv / 1000.0:F1} GW electronic";
        Co2 = $"{co2:F0} tCO2/h";

        _freqHistory.Add(hz);
        if (_freqHistory.Count > TraceLength) _freqHistory.RemoveAt(0);
        FreqTrace = _freqHistory.ToArray();

        // Torque, the layman way: P = Tomega, so at 3000 rpm one MW is ~3183 Nm.
        // Commanded = what holding exactly 50.00 Hz needs (demand + losses).
        // Actual = commanded plus the surplus/deficit the frequency deviation implies
        // through governor droop  -  the gap IS the reason the speed is off 50.
        double cmdMw = f.DemandMw + losses;
        double surplusMw = (hz - nominalHz) / nominalHz / EnergyEconomicsConstants.GovernorDroopFraction * f.DemandMw;
        double omega = 2 * Math.PI * nominalHz * (hz / nominalHz);
        // T = P/omega. Commanded uses the nominal 50 Hz omega; actual uses the live omega.
        double cmdMnm = cmdMw * 1e6 / (2 * Math.PI * nominalHz) / 1e6;
        double actMnm = (cmdMw + surplusMw) * 1e6 / omega / 1e6;
        TorqueCmdValue = cmdMnm;
        TorqueActValue = actMnm;
        TorqueCmdText = $"{cmdMnm:F0}";
        TorqueActText = $"{actMnm:F0}";
        double divMnm = surplusMw * 1e6 / omega / 1e6;
        TorqueDiv = $"{divMnm:+0.00;-0.00;0.00}";
        DivMw = $"= {surplusMw:+#,0;-#,0;0} MW";
        DivBrush = Palette.Frozen(Math.Abs(surplusMw) < TorqueNarrativeBandMw ? Palette.Voltage(1.0)
                                 : surplusMw > 0 ? Palette.Loading(0.7) : Palette.Loading(1.05));
        DivNote = surplusMw > TorqueNarrativeBandMw ? "too much push  -  the machine speeds up"
                : surplusMw < -TorqueNarrativeBandMw ? "not enough push  -  the machine slows down"
                : "supply and demand in balance";

        // STRESS: how hard the machine is being shoved. 100% when the torque gap
        // reaches 5% of commanded. cmdMnm is 0 only at a dead-cold startup frame  - 
        // guard the divide so the dial reads 0, not NaN.
        double stress = cmdMnm > 1e-6 ? Math.Abs(divMnm) / (EnergyEconomicsConstants.StressBand * cmdMnm) * 100.0 : 0.0;
        StressPct = Math.Clamp(stress, 0, 100);
        StressText = $"{StressPct:F0}%";

        // Accumulated divergence: the net over/under-spin banked so far. Positive =
        // the grid has run fast on average  -  energy regeneration could harvest.
        _accumHzS += (hz - nominalHz) * dtHours * 3600.0;
        _regenMwh += Math.Max(0, surplusMw) * dtHours;
        double secondsOff = Math.Abs(_accumHzS) / nominalHz;   // (f-50)dt  50 = clock error in seconds
        bool fast = _accumHzS >= 0;
        Accum = $"{_accumHzS:+0.0;-0.0;0.0} Hzs banked";
        TimeError = $"{secondsOff:F2} s {(fast ? "fast" : "slow")}";
        TimeErrorNote = fast
            ? "mains-driven clocks are running ahead of real time"
            : "mains-driven clocks are running behind real time";
        AccumNote = _accumHzS > 0.5 ? "net fast  -  regeneration window open"
                  : _accumHzS < -0.5 ? "net slow  -  the grid owes energy"
                  : "running true to 50 Hz";
        AccumBrush = Palette.Frozen(_accumHzS > 0.5 ? Palette.Voltage(1.0)
                    : _accumHzS < -0.5 ? Palette.Loading(0.9) : Palette.Voltage(0.95));

        // Potential missed regen: the over-spin we *could* have harvested, valued for
        // the layman. Price and per-home figures are assumptions, flagged in the view.
        RegenMwhText = $"{_regenMwh:F0}";
        RegenMoney = $"~= GBP{_regenMwh * EnergyEconomicsConstants.RegenPricePerMwh:#,0} at GBP{EnergyEconomicsConstants.RegenPricePerMwh:F0}/MWh wholesale (assumed)";
        RegenHomes = $"~= {_regenMwh * 1000.0 / EnergyEconomicsConstants.HomeKwhPerDay:#,0} homes powered for a day";

        // The BESS leans against the divergence: charges off surplus spin (f high),
        // gives it back when the machine runs slow.
        double bessMw = Math.Clamp(surplusMw * EnergyEconomicsConstants.BessResponseFraction,
            -EnergyEconomicsConstants.BessCapMw, EnergyEconomicsConstants.BessCapMw);
        if (_bessSoc >= EnergyEconomicsConstants.BessSocChargeCeilingPct && bessMw > 0) bessMw = 0;
        if (_bessSoc <= EnergyEconomicsConstants.BessSocDischargeFloorPct && bessMw < 0) bessMw = 0;
        _bessSoc = Math.Clamp(_bessSoc + bessMw * dtHours / EnergyEconomicsConstants.BessCapMwh * 100.0, 0, 100);
        _bessMovedMwh += Math.Abs(bessMw) * dtHours;
        double bessDead = EnergyEconomicsConstants.BessModeDeadbandMw;
        BessPower = bessMw > bessDead ? $"{bessMw:F0} MW in" : bessMw < -bessDead ? $"{-bessMw:F0} MW out" : "holding";
        BessMode = bessMw > bessDead ? "CHARGING" : bessMw < -bessDead ? "DISCHARGING" : "STANDBY";
        BessBrush = Palette.Frozen(bessMw > bessDead ? Palette.Voltage(1.0) : bessMw < -bessDead ? Palette.Loading(0.8) : Palette.Voltage(0.9));
        BessSocText = $"{_bessSoc:F0}%";
        BessSocBar = _bessSoc / 100.0 * BarTrack;
        BessEnergy = $"{_bessSoc / 100.0 * EnergyEconomicsConstants.BessCapMwh:F0} MWh stored of {EnergyEconomicsConstants.BessCapMwh:F0}";
        BessThroughput = $"{_bessMovedMwh:F0} MWh cycled today";

        // The electrical story rides alongside the rotational one.
        UpdateVoltage(f, busById);
        UpdateReactive(m, f);
        UpdateTaps(m, f, busById);
    }

    /// <summary>
    /// Clear every time-integrated quantity and history. Must be called whenever the timeline is broken:
    /// switching model, loading a replay run, or restarting playback  -  otherwise the integrators carry a
    /// previous run's accumulated Hz*s / regen / BESS state into the new one.
    /// </summary>
    public void Reset()
    {
        _accumHzS = 0;
        _regenMwh = 0;
        _bessSoc = BessInitialSocPct;
        _bessMovedMwh = 0;
        _lastHour = -1;
        _lastTick = int.MinValue;
        _lastHz = NominalHz;
        _freqHistory.Clear();
        _voltHistory.Clear();
    }

    /// <summary>System voltage as a deviation from nominal  -  the average pu (demand-weighted),
    /// the spread across the network, and a recent-history trace. The grid is never perfectly 1.0.</summary>
    private void UpdateVoltage(SimFrame f, Dictionary<int, Bus> busById)
    {
        PowerFlowResult r = f.Result;
        double wSum = 0, wV = 0, vmin = double.PositiveInfinity, vmax = double.NegativeInfinity, kvAtMin = 0, kvAtMax = 0;
        for (int i = 0; i < r.Vm.Length; i++)
        {
            double v = r.Vm[i];
            if (!busById.TryGetValue(r.BusIds[i], out Bus? b)) continue;
            double w = Math.Max(b.Pd, 0) + VoltageWeightEpsilonMw;   // demand-weighted; +eps so zero-load buses still count faintly
            wSum += w; wV += w * v;
            if (v < vmin) { vmin = v; kvAtMin = v * b.BaseKv; }
            if (v > vmax) { vmax = v; kvAtMax = v * b.BaseKv; }
        }
        if (wSum <= 0) { VoltAvg = " - "; VoltDev = " - "; VoltSpread = " - "; VoltSpreadKv = " - "; return; }

        double vavg = wV / wSum, devPct = (vavg - 1.0) * 100.0;
        VoltAvg = $"{vavg:F3} pu";
        VoltAvgValue = vavg;
        VoltDev = $"{devPct:+0.0;-0.0;0.0}% {(devPct >= 0 ? "above" : "below")} nominal";
        VoltSpread = $"{vmin:F3}-{vmax:F3} pu";
        VoltSpreadKv = $"lowest {kvAtMin:F0} kV  highest {kvAtMax:F0} kV";
        VoltBrush = Palette.Frozen(Palette.Voltage(vavg));

        _voltHistory.Add(vavg);
        if (_voltHistory.Count > TraceLength) _voltHistory.RemoveAt(0);
        VoltTrace = _voltHistory.ToArray();
    }

    /// <summary>The reactive balance: VARs produced (generators over-excited + line charging) versus
    /// absorbed (load + under-excited generators + series reactance). The net is why voltage drifts.</summary>
    private void UpdateReactive(GridModel m, SimFrame f)
    {
        double qGen = m.Generators.Where(g => g.Qg > 0).Sum(g => g.Qg);
        double qGenAbsorb = m.Generators.Where(g => g.Qg < 0).Sum(g => -g.Qg);
        double qLoad = m.Buses.Sum(b => Math.Max(0, b.Qd));
        double qLineLoss = f.Result.LineFlows.Sum(l => l.QLossMvar);     // XI^2 absorbed - BV^2 charging
        double qProduced = qGen + Math.Max(0, -qLineLoss);              // charging is a source when QLoss < 0
        double qAbsorbed = qLoad + qGenAbsorb + Math.Max(0, qLineLoss);
        double qNet = qProduced - qAbsorbed;

        QGenValue = qProduced;
        QGenText = $"{qProduced:#,0}";
        QAbsText = $"{qAbsorbed:#,0}";
        QNet = $"{qNet:+#,0;-#,0;0} Mvar {(Math.Abs(qNet) < 1 ? "balanced" : qNet > 0 ? "surplus" : "deficit")}";
        QNote = qNet > 300 ? "surplus VARs push voltage up  -  the grid runs high"
              : qNet < -300 ? "VARs in short supply  -  voltage sags"
              : "reactive supply and demand in balance";
        QBrush = Palette.Frozen(qNet > 300 ? Palette.Voltage(1.04) : qNet < -300 ? Palette.Voltage(0.95) : Palette.Voltage(1.0));
        QScaleMax = Math.Max(2000, Math.Ceiling(qProduced * 1.2 / 500) * 500);   // adaptive dial scale
    }

    /// <summary>The on-load tap-changer fleet: where each tap sits relative to its 1.0 nominal, how
    /// well it holds its controlled bus, how many are out of travel, and the overall regulation strain.</summary>
    private void UpdateTaps(GridModel m, SimFrame f, Dictionary<int, Bus> busById)
    {
        var vmByBus = new Dictionary<int, double>();
        for (int i = 0; i < f.Result.BusIds.Count; i++) vmByBus[f.Result.BusIds[i]] = f.Result.Vm[i];
        // The branch each OLTC acts on, from the SOLVER's own mapping (by name AND controlled-bus end, then the first of
        // the name). This read the LAST branch of the name, so with parallel same-named transformers the tab could
        // show the tap of a unit the solver does not regulate.
        int[] branchOfOltc = SteadyStateSolver.MapOltcRecordsToBranches(m);

        double offSum = 0, errSum = 0, ratioMin = double.PositiveInfinity, ratioMax = double.NegativeInfinity;
        int atLimit = 0, count = 0;
        double deadband = m.Oltcs.Count > 0 ? m.Oltcs[0].DeadbandPu : DefaultOltcDeadbandPu;
        var seen = new HashSet<int>();

        for (int k = 0; k < m.Oltcs.Count; k++)
        {
            Oltc o = m.Oltcs[k];
            if (branchOfOltc[k] < 0) continue;   // no branch carries the name: nothing to show (the solver skips it too)
            Branch br = m.Branches[branchOfOltc[k]];
            count++;
            double ratio = br.TapRatio, offPct = (ratio - 1.0) * 100.0;
            int step = (int)Math.Round((ratio - 1.0) / o.StepPu);
            int maxStep = (int)Math.Round((o.MaxTap - 1.0) / o.StepPu);
            bool limit = ratio <= o.MinTap + o.StepPu * TapAtLimitStepFraction || ratio >= o.MaxTap - o.StepPu * TapAtLimitStepFraction;
            double v = vmByBus.GetValueOrDefault(o.ControlsBus, o.TargetVpu);
            double errPu = v - o.TargetVpu;
            double ctrlKv = v * (busById.TryGetValue(o.ControlsBus, out Bus? cb) ? cb.BaseKv : 0);

            offSum += offPct; errSum += Math.Abs(errPu); if (limit) atLimit++;
            ratioMin = Math.Min(ratioMin, ratio); ratioMax = Math.Max(ratioMax, ratio);

            // One row per OLTC record (its index), not per transformer name: two OLTCs on a shared name keep two rows.
            seen.Add(k);
            if (_tapByOltc.TryGetValue(k, out TapRow? row) && row.Name != o.Transformer)
            {
                Taps.Remove(row); _tapByOltc.Remove(k); row = null;   // a different model's OLTC at this index
            }
            if (row is null) { row = new TapRow { OltcIndex = k, Name = o.Transformer }; _tapByOltc[k] = row; Taps.Add(row); }
            row.OffNom = $"{offPct:+0.0;-0.0;0.0}% off nominal";
            row.Step = $"tap {step:+0;-0;0} of +/-{maxStep}";
            row.Err = $"holds {ctrlKv:F0} kV  {errPu * 100:+0.0;-0.0;0.0}% vs target";
            row.AtLimit = limit;
            row.Color = Palette.Frozen(limit ? Palette.Loading(1.05) : Palette.Voltage(1.0 + offPct / 100.0));
            row.BarWidth = Math.Clamp(0.5 + offPct / 20.0, 0, 1) * BarTrack;   // centre = nominal, +/-10% spans the track
        }
        PruneTaps(seen);

        if (count == 0)
        {
            TapAvg = " - "; TapLimits = " - "; TapRegErr = " - ";
            TapSummary = "no tap changers in this model";
            TapPosValue = 0; TapPosText = " - "; RegStressPct = 0; RegStressText = " - ";
            return;
        }

        double avgOff = offSum / count, avgErr = errSum / count;
        TapAvg = $"{avgOff:+0.0;-0.0;0.0}% off nominal on average";
        TapLimits = $"{atLimit} of {count} at travel limit";
        TapRegErr = $"avg {avgErr * 100:F1}% from target ({avgErr / deadband:F1}x deadband)";
        TapSummary = $"{count} OLTCs  ratios {ratioMin:F3}-{ratioMax:F3}"
            // N054 (October 2026): say when the solve's tap changer loop did not finish (SimFrame.TapsSettled).
            + (f.TapsSettled ? "" : (f.TapsAtLimit?.Count ?? 0) > 0
                ? $"  NOT SETTLED: {f.TapsAtLimit!.Count} out of range, bus out of band"
                : "  NOT SETTLED: taps still moving");
        TapPosValue = avgOff;
        TapPosText = $"{avgOff:+0.0;-0.0;0.0}%";

        double limitFrac = (double)atLimit / count;
        double errStress = deadband > 0 ? avgErr / deadband : 0;          // 1.0 = at deadband edge on average
        RegStressPct = Math.Clamp(Math.Max(limitFrac, Math.Min(1.0, errStress / 2.0)) * 100.0, 0, 100);
        RegStressText = $"{RegStressPct:F0}%";
    }

    private void PruneTaps(HashSet<int> seen)
    {
        for (int i = Taps.Count - 1; i >= 0; i--)
            if (!seen.Contains(Taps[i].OltcIndex)) { _tapByOltc.Remove(Taps[i].OltcIndex); Taps.RemoveAt(i); }
    }

    private void UpdateFuel(GridModel m, double totalMw)
    {
        var by = new Dictionary<string, (Color col, double mw, int units, double inertiaMwS)>();
        foreach (Generator g in m.Generators)
        {
            if (g.Pg <= 0) continue;
            (string name, Color col) = Bucket(Fuels.Classify(g.Type));
            (Color c, double mw, int units, double inertiaMwS) cur = by.GetValueOrDefault(name, (col, 0, 0, 0));
            // N325 (DOC-17): the rotating-mass share (N289), so the per-bucket inertia agrees with the total.
            by[name] = (col, cur.mw + g.Pg, cur.units + 1, cur.inertiaMwS + GridModel.RotatingInertiaMwS(g));
        }

        double total = Math.Max(1, totalMw);
        var seen = new HashSet<string>();
        foreach ((string key, (Color col, double mw, int units, double inertiaMwS)) in by.OrderByDescending(kv => kv.Value.mw))
        {
            seen.Add(key);
            if (!_fuelByName.TryGetValue(key, out StreamRow? row))
            {
                row = new StreamRow { Key = key, Name = key, Color = Palette.Frozen(col) };
                _fuelByName[key] = row;
                FuelIn.Add(row);
            }
            row.Gw = $"{mw / 1000.0:F1} GW";
            row.Detail = $"{mw / total * 100:F0}% of the push  {units} {(units == 1 ? "plant" : "plants")}";
            row.BarWidth = Math.Min(1.0, mw / total) * BarTrack;
            double gvas = inertiaMwS / 1000.0;     // MWs -> GVAs
            row.Inertia = gvas >= 0.05 ? $"{gvas:F1} GVAs spin" : "no inertia";
        }
        Prune(_fuelByName, FuelIn, seen);
    }

    private void UpdateDemand(GridModel m, double totalMw)
    {
        Color col = Palette.FromHex("#21C7E6");
        double total = Math.Max(1, totalMw);
        var zones = m.Buses.Where(b => b.Pd > 0).OrderByDescending(b => b.Pd).ToList();
        var seen = new HashSet<string>();

        // Rows are keyed by BUS ID and only display the cleaned name. They were keyed by the cleaned name, so two buses
        // of the same name (or names differing only by " 132kV") wrote one row: the second overwrote the first, whose
        // demand was then counted neither in its row nor in EVERYWHERE ELSE, and the column no longer summed to demand.
        void Row(string key, string name, double mw)
        {
            seen.Add(key);
            if (_demandByName.TryGetValue(key, out StreamRow? row) && row.Name != name)
            {
                DemandOut.Remove(row); _demandByName.Remove(key); row = null;   // the id now names a different bus
            }
            if (row is null)
            {
                row = new StreamRow { Key = key, Name = name, Color = Palette.Frozen(col) };
                _demandByName[key] = row;
                DemandOut.Add(row);
            }
            row.Gw = $"{mw / 1000.0:F1} GW";
            row.Detail = $"{mw / total * 100:F0}% of the draw";
            row.BarWidth = Math.Min(1.0, mw / total) * BarTrack;
        }

        foreach (Bus b in zones.Take(8)) Row($"bus:{b.Id}", CleanZone(b.Name ?? $"zone {b.Id}"), b.Pd);
        double rest = zones.Skip(8).Sum(b => b.Pd);
        if (rest > 0) Row("rest", "EVERYWHERE ELSE", rest);
        Prune(_demandByName, DemandOut, seen);
    }

    private static string CleanZone(string name) =>
        name.Replace(" 132kV", "").Replace(" 132 kV", "").ToUpperInvariant();

    /// <summary>Power riding each voltage level of the shaft, plus how far that level's solved
    /// voltage sits above/below its nominal kV  -  lines grouped by their operating kV.</summary>
    private void UpdateLayers(GridModel m, SimFrame f, Dictionary<int, Bus> busById)
    {
        var kvOf = m.Buses.ToDictionary(b => b.Id, b => b.BaseKv);
        var by = new Dictionary<double, double>();
        double all = 0;
        for (int i = 0; i < m.Branches.Count && i < f.Result.LineFlows.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.IsTransformer) continue;
            double kv = Math.Max(kvOf.GetValueOrDefault(br.From), kvOf.GetValueOrDefault(br.To));
            double mw = Math.Max(Math.Abs(f.Result.LineFlows[i].PFromMw), Math.Abs(f.Result.LineFlows[i].PToMw));
            by[kv] = by.GetValueOrDefault(kv) + mw;
            all += mw;
        }

        // Average solved voltage (pu) of the buses on each kV band, for the actual-vs-nominal read.
        var vByBand = new Dictionary<double, (double sumPu, int n)>();
        for (int i = 0; i < f.Result.Vm.Length; i++)
        {
            if (!busById.TryGetValue(f.Result.BusIds[i], out Bus? b)) continue;
            (double sumPu, int n) cur = vByBand.GetValueOrDefault(b.BaseKv, (0, 0));
            vByBand[b.BaseKv] = (cur.sumPu + f.Result.Vm[i], cur.n + 1);
        }

        double max = Math.Max(1, by.Values.DefaultIfEmpty(1).Max());
        var seen = new HashSet<string>();
        foreach ((double kv, double mw) in by.OrderByDescending(kv => kv.Key))
        {
            // Labelled with enough digits to keep distinct levels apart (0.4 and 0.415 kV used to share "0 kV" and one
            // row, the later overwriting the earlier); the same format as the NETWORK tab's voltage-level dials.
            string label = DashboardViewModel.KvLabel(kv);
            seen.Add(label);
            if (!_layerByLabel.TryGetValue(label, out VoltageLayer? layer))
            {
                layer = new VoltageLayer { Label = label, Color = Palette.Frozen(Palette.Voltage(1.0 + (kv >= 300 ? 0.0 : 0.02))) };
                _layerByLabel[label] = layer;
                Layers.Add(layer);
            }
            layer.Gw = $"{mw / 1000.0:F1} GW";
            layer.Share = all > 0 ? $"{mw / all * 100:F0}%" : "";
            layer.BarWidth = mw / max * BarTrack;

            if (vByBand.TryGetValue(kv, out (double sumPu, int n) agg) && agg.n > 0)
            {
                double puAvg = agg.sumPu / agg.n, kvAct = puAvg * kv, dPct = (puAvg - 1.0) * 100.0;
                layer.KvActual = $"{kvAct:F0} kV";
                layer.KvDev = $"{dPct:+0.0;-0.0;0.0}%  {kvAct - kv:+0;-0;0} kV";
                layer.DevBrush = Palette.Frozen(Palette.Voltage(puAvg));
                layer.Color = Palette.Frozen(Palette.Voltage(puAvg));     // tint the MW bar by tier voltage
                layer.DevBar = Math.Clamp(0.5 + dPct / 12.0, 0, 1) * BarTrack; // centre = nominal, +/-6% spans
            }
        }
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (!seen.Contains(Layers[i].Label)) { _layerByLabel.Remove(Layers[i].Label); Layers.RemoveAt(i); }
    }

    private static void Prune(Dictionary<string, StreamRow> index, ObservableCollection<StreamRow> rows, HashSet<string> seen)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(rows[i].Key)) { index.Remove(rows[i].Key); rows.RemoveAt(i); }
    }
}

// ------------------------------------------------ the old Views/MachineEngineerVm.cs ------------------------------------------------

/// <summary>
/// THE MECHANICAL / PLANT ENGINEER TAB - per-machine, asset-level. "The operator view tells you what
/// the SYSTEM is doing; the mechanical view tells you what the MACHINES are doing." Every synchronous
/// machine online, its electrical state and how close it sits to its protection envelope, its inertia
/// contribution (the asset-level GVA.s(i) that sums to the system number), and where the fleet is going.
///
/// Data provenance is explicit: MW / MVAr / power factor, the P-Q capability envelope (from Qmin/Qmax/
/// Pmax), per-unit stored energy (H*MBase), ramp trajectory and the RoCoF-relay margin are REAL from
/// the streamed model. Excitation %, AVR/governor mode, shaft torsional stress, stator/rotor temps,
/// certified J + type-test dates and committed starts are plant SCADA the lake does not carry - they are
/// shown as flagged proxies until a plant feed is wired (never presented as measured).
/// </summary>
public sealed class MachineEngineerVm : Notify
{
    public ObservableCollection<MachineRow> Machines { get; } = new();
    public ObservableCollection<StartRow> CommittedStarts { get; } = new();

    // ---- drill-down inspector (click a machine to see its full state broken out) ----
    public InspectorVm Inspector { get; } = new();
    public System.Windows.Input.ICommand InspectMachineCommand { get; }

    public MachineEngineerVm()
    {
        InspectMachineCommand = new RelayCommand<MachineRow>(InspectMachine);   // ORACLE: the shim below (was GridSim.Wpf.Views.Assets.RelayCommand<MachineRow>)
    }

    private void InspectMachine(MachineRow m)
    {
        if (m is null) return;
        var stats = new[]
        {
            new InspectStat { Label = "OUTPUT", Value = $"{m.Mw} MW" },
            new InspectStat { Label = "STORED KE", Value = $"{m.KeGvaS} GVA.s", ValueBrush = GreenB },
            new InspectStat { Label = "RISK", Value = m.RiskLabel, ValueBrush = m.RiskBrush },
            new InspectStat { Label = "RAMP", Value = $"{m.RampMwMin}/min", ValueBrush = m.RampBrush },
        };
        var rows = new[]
        {
            Row("Real power", $"{m.Mw} MW", m.Swatch),
            Row("Reactive power", m.Mvar == "-" ? "- (no reactive telemetry)" : $"{m.Mvar} MVAr"),
            Row("Power factor", m.Pf),
            Row("Excitation (proxy)", m.ExcPct),
            Row("AVR mode (synthetic)", m.Avr),         // assumed by fuel class: no AVR telemetry
            Row("Governor mode (synthetic)", m.Gov),    // assumed: no governor telemetry
            Row("Stored kinetic energy", $"{m.KeGvaS} GVA.s  *  H {m.HseSec}", m.Swatch, GreenB),
            Row("Torsional pulses (proxy)", $"{m.Pulses}  *  peak {m.PeakStress}"),
            Row("Stator / rotor temp (synthetic)", $"{m.StatorTemp} / {m.RotorTemp}"),
            Row("MW ramp (last period)", $"{m.RampMwMin} MW/min", m.RampBrush, m.RampBrush),
            Row("Type-test date", $"{m.TestDate} (asset register not wired)"),
        };
        Inspector.Show(m.Name.ToUpperInvariant(),
            $"{m.Fuel}  *  {m.RiskLabel}  *  per-machine state this frame",
            stats, rows);
    }

    private static InspectRow Row(string label, string value, Brush? swatch = null, Brush? valueBrush = null) => new()
    {
        Primary = label, Value = value,
        Swatch = swatch ?? Brushes.Transparent,
        ValueBrush = valueBrush ?? Palette.Frozen(Palette.FromHex("#E8F0F6")),
    };

    // Per-unit state keyed by generator INDEX (Core UnitRampHistory checks bus + name), not by name: gb-full
    // names are not unique, and name keying diffed namesakes against each other every frame, giving phantom
    // ramps and a torsional-pulse count that grew for ever (the Bolney pair differs by over 40 MW) (R054).
    private readonly UnitRampHistory _prevPg = new();
    private readonly Dictionary<int, int> _pulses = new();

    // ---- RoCoF relay margin (Section 3 headline) ----
    private string _rocofNow = "-"; public string RocofNow { get => _rocofNow; set => Set(ref _rocofNow, value); }
    private string _rocofLegacyMargin = "-"; public string RocofLegacyMargin { get => _rocofLegacyMargin; set => Set(ref _rocofLegacyMargin, value); }
    private double _rocofLegacyPct; public double RocofLegacyPct { get => _rocofLegacyPct; set => Set(ref _rocofLegacyPct, value); }
    private Brush _rocofLegacyBrush = Brushes.Gray; public Brush RocofLegacyBrush { get => _rocofLegacyBrush; set => Set(ref _rocofLegacyBrush, value); }
    private string _rocofModernMargin = "-"; public string RocofModernMargin { get => _rocofModernMargin; set => Set(ref _rocofModernMargin, value); }
    private double _rocofModernPct; public double RocofModernPct { get => _rocofModernPct; set => Set(ref _rocofModernPct, value); }
    private Brush _rocofModernBrush = Brushes.Gray; public Brush RocofModernBrush { get => _rocofModernBrush; set => Set(ref _rocofModernBrush, value); }

    // ---- Section 4: asset-level inertia roll-up ----
    private string _sumKe = "-"; public string SumKe { get => _sumKe; set => Set(ref _sumKe, value); }
    private string _reportedTotal = "-"; public string ReportedTotal { get => _reportedTotal; set => Set(ref _reportedTotal, value); }
    private string _syntheticGap = "-"; public string SyntheticGap { get => _syntheticGap; set => Set(ref _syntheticGap, value); }

    // ---- Section 5: fleet trajectory roll-up ----
    private string _projInertia = "-"; public string ProjInertia { get => _projInertia; set => Set(ref _projInertia, value); }
    private Brush _projBrush = Brushes.Gray; public Brush ProjBrush { get => _projBrush; set => Set(ref _projBrush, value); }
    private string _fleetTrend = "-"; public string FleetTrend { get => _fleetTrend; set => Set(ref _fleetTrend, value); }

    // ---- P-Q diagram for the most-stressed machine (Section 3) ----
    private MachineRow? _selected; public MachineRow? Selected { get => _selected; set => Set(ref _selected, value); }
    private string _clock = "-"; public string Clock { get => _clock; set => Set(ref _clock, value); }

    private double? _lakeIntrinsicGvaS;

    private static readonly Brush GreenB = Palette.Frozen(Palette.FromHex("#2FD27F"));
    private static readonly Brush AmberB = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush RedB = Palette.Frozen(Palette.FromHex("#FF5B5B"));
    private static readonly Brush MutedB = Palette.Frozen(Palette.FromHex("#8492A0"));

    /// <summary>Optional: the lake's bottom-up system GVA.s(i), so the asset-sum-vs-reported gap is meaningful.</summary>
    public void SetIntrinsic(double? lakeIntrinsicGvaS) { _lakeIntrinsicGvaS = lakeIntrinsicGvaS; }

    /// <summary>
    /// Break the timeline (model switch / replay restart): drop the per-machine ramp and torsional-pulse
    /// accumulators and the streamed lake intrinsic figure, so frame-0 isn't diffed against the end of the
    /// previous run (phantom ramp / never-decaying, Nx-counted pulses) and GB-lake inertia can't leak onto a
    /// local case.
    /// </summary>
    public void Reset()
    {
        _prevPg.Reset();
        _pulses.Clear();
        _lakeIntrinsicGvaS = null;
        _prevHour = double.NaN;
        _periodMin = DefaultPeriodMin;
    }

    private double _prevHour = double.NaN;   // frame clock of the previous Update (hours)
    private double _periodMin = DefaultPeriodMin;   // last known frame-to-frame interval (minutes)

    // ---- named thresholds (R211: were literals; values unchanged). The ramp deadband and stress bands are the same
    // values as the web PresentationLimits (pinned by PresentationConstantsAntiDriftTests). ----

    /// <summary>Frame-to-frame interval (minutes) assumed before two frames have been seen: one settlement period.</summary>
    public const double DefaultPeriodMin = 30.0;

    /// <summary>Unit ramp (MW/min, either way) inside which a machine counts as holding, not ramping.</summary>
    public const double RampDeadbandMwPerMin = GridSim.Core.Presentation.PresentationConstants.RampDeadbandMwPerMin;   // N023: the one shared copy (was the literal 0.5)

    /// <summary>A step in output (MW) between frames larger than this counts as one torsional pulse.</summary>
    public const double TorsionalPulseStepMw = 40.0;

    /// <summary>Stress (fraction of capability) above which a machine's risk reads LIMIT.</summary>
    public const double StressLimitFraction = GridSim.Core.Presentation.PresentationConstants.StressLimitFraction;   // N023: the one shared copy (was the literal 0.95)

    /// <summary>Stress (fraction of capability) above which a machine's risk reads NEAR.</summary>
    public const double StressNearFraction = GridSim.Core.Presentation.PresentationConstants.StressNearFraction;   // N023: the one shared copy (was the literal 0.85)

    /// <summary>Horizon (minutes) of the "projected inertia if the current ramp holds" read-out.</summary>
    public const double InertiaProjectionMinutes = 15.0;

    // SYNTHETIC display proxies (no plant telemetry is wired; the row says so): excitation % = base + slope x Q/Qmax,
    // stator / rotor temperature (degC) = base + slope x loading.
    private const double ExcitationBasePct = 90.0, ExcitationSlopePct = 30.0;
    private const double StatorTempBaseC = 40.0, StatorTempSlopeC = 60.0;
    private const double RotorTempBaseC = 50.0, RotorTempSlopeC = 45.0;

    public void Update(SimFrame f, string clock)
    {
        Clock = clock;
        // Period length for ramp: derived from the frame clock (like MachineVm), NOT a constant -
        // replay steps a settlement period (30 min) but the LIVE sim step is user-selectable from
        // 1 ms to 20 min, and a hardcoded 30 min misstated every MW/min figure, the ramp arrows,
        // the pulse context and the 15-min inertia projection in live mode.
        if (!double.IsNaN(_prevHour) && f.Hour > _prevHour + 1e-12)
            _periodMin = (f.Hour - _prevHour) * 60.0;
        _prevHour = f.Hour;
        double periodMin = _periodMin;

        // NOTE on reactive: the presolved feed carries no per-generator reactive DISPATCH (Generator.Qg = 0),
        // and per-bus net reactive injection cannot be truthfully attributed to a co-located machine (it is
        // dominated by line charging / shunt compensation / load - an 11 MW unit would read hundreds of MVAr).
        // So MVAr / power factor / excitation are shown as unavailable rather than fabricated; the thermal
        // (MW vs Pmax), inertia, ramp and RoCoF-margin dimensions are real. Wire plant telemetry to fill them.
        var rows = new List<MachineRow>();
        double sumKeGvaS = 0, rampInertiaMwSPerMin = 0;
        int up = 0, down = 0;
        IReadOnlyList<Generator> gens = f.Model.Generators;
        for (int gi = 0; gi < gens.Count; gi++)
        {
            Generator g = gens[gi];
            FuelInfo fi = Fuels.Classify(g.Type);
            if (!fi.Synchronous) continue;           // the mechanical view is about spinning machines
            double mw = g.Pg;
            if (mw < MechanicalThresholds.MinSyncMachineMw) continue;   // real transmission units, not sub-MW embedded plant (R211: the shared Core threshold the web uses)
            string id = g.Name ?? $"{fi.Name}@{g.Bus}";   // display name only; per-unit state is keyed by gi

            bool hasReactive = g.Qg != 0;             // presolved feed has none; true only if a real feed is wired
            double qg = g.Qg;
            double s = System.Math.Sqrt(mw * mw + qg * qg);
            double pf = s > 1e-6 ? mw / s : 1.0;
            bool leading = qg < 0;                    // absorbing VARs = under-excited = leading

            // limit proximity: MW vs Pmax is REAL; reactive proximity only when reactive telemetry exists
            double pOut = double.IsInfinity(g.Pmax) || g.Pmax <= 0 ? 0 : mw / g.Pmax;
            double qUp = hasReactive && !double.IsInfinity(g.Qmax) && g.Qmax > 0 ? System.Math.Max(0, qg) / g.Qmax : 0;
            double qDn = hasReactive && !double.IsInfinity(g.Qmin) && g.Qmin < 0 ? System.Math.Max(0, -qg) / -g.Qmin : 0;
            double stress = System.Math.Max(pOut, System.Math.Max(qUp, qDn));

            // ramp trajectory (REAL): deltaPg since last frame
            // against THIS unit's own previous output (index identity); no history = no ramp (prev = mw).
            double? known = _prevPg.Exchange(gi, g);
            double prev = known ?? mw;
            double rampMwMin = (mw - prev) / periodMin;
            if (rampMwMin > RampDeadbandMwPerMin) up++; else if (rampMwMin < -RampDeadbandMwPerMin) down++;

            // torsional pulse proxy (REAL-derived, flagged): a large step is a torque transient. A unit with no
            // history (new, or a different unit now at this index) starts its count from zero.
            int pulses = known is not null && _pulses.TryGetValue(gi, out int pc) ? pc : 0;
            if (System.Math.Abs(mw - prev) > TorsionalPulseStepMw) pulses++;
            _pulses[gi] = pulses;

            double keGvaS = g.H * g.MBase / 1000.0;
            sumKeGvaS += keGvaS;
            // a de-loading synchronous machine sheds inertia with its MW (approx, at fixed H/MBase online):
            // dropping below its dispatch doesn't remove H until it desyncs, but the operator wants the trend proxy.
            if (rampMwMin < -RampDeadbandMwPerMin && g.Pmax > 0)
                rampInertiaMwSPerMin += rampMwMin / g.Pmax * (g.H * g.MBase);

            double load = double.IsInfinity(g.Pmax) || g.Pmax <= 0 ? 0 : mw / g.Pmax;
            rows.Add(new MachineRow
            {
                Name = id,
                Fuel = fi.Name,
                Swatch = Palette.Frozen(fi.Color),
                Mw = $"{mw:N0}",
                Mvar = hasReactive ? $"{qg:+0;-0;0}" : "-",
                Pf = hasReactive ? $"{pf:F2} {(leading ? "lead" : "lag")}" : "-",
                Leading = hasReactive && leading,
                ExcPct = hasReactive ? $"{System.Math.Clamp(ExcitationBasePct + ExcitationSlopePct * (g.Qmax > 0 ? qg / g.Qmax : 0), 40, 130):F0}%" : "-",
                // The AVR and governor modes are assumed by fuel class (no AVR or governor telemetry exists), so the
                // Inspector labels both rows "(synthetic)".
                Avr = fi.Name is "Nuclear" or "CCGT" or "Biomass" ? "AUTO+PSS" : "AUTO",
                Gov = "AUTO",
                Stress = stress,
                // AT OR ABOVE each edge, as the shared constants say and the web tab does (>= in per cent): a unit at
                // exactly 95 % reads LIMIT. This compared strictly, so exactly 95 % read NEAR on the desktop.
                RiskBrush = stress >= StressLimitFraction ? RedB : stress >= StressNearFraction ? AmberB : GreenB,
                RiskLabel = stress >= StressLimitFraction ? "LIMIT" : stress >= StressNearFraction ? "NEAR" : "OK",
                Pulses = pulses,
                PeakStress = pulses > 0 ? $"~{System.Math.Min(15 + pulses * 3, 85)}%" : "-",
                StatorTemp = $"{StatorTempBaseC + StatorTempSlopeC * load:F0} degC",
                RotorTemp = $"{RotorTempBaseC + RotorTempSlopeC * load:F0} degC",
                ThermalTrend = load > 0.9 ? "^" : "=",
                KeGvaS = $"{keGvaS:F2}",
                HseSec = $"{g.H:F1}s",
                TestDate = "-",           // SYNTHETIC: no asset register wired
                RampMwMin = $"{rampMwMin:+0.0;-0.0;0.0}",
                RampArrow = rampMwMin > RampDeadbandMwPerMin ? "^" : rampMwMin < -RampDeadbandMwPerMin ? "v" : "=",
                RampBrush = rampMwMin > RampDeadbandMwPerMin ? GreenB : rampMwMin < -RampDeadbandMwPerMin ? AmberB : MutedB,
                // P-Q envelope (REAL)
                Pg = mw, Qg = qg,
                Qmin = double.IsInfinity(g.Qmin) ? -mw : g.Qmin,
                Qmax = double.IsInfinity(g.Qmax) ? mw : g.Qmax,
                Pmax = double.IsInfinity(g.Pmax) || g.Pmax <= 0 ? mw : g.Pmax,
            });
        }

        // sort most-stressed first (Section 1 requirement)
        rows.Sort((a, b) => b.Stress.CompareTo(a.Stress));
        Machines.Clear();
        foreach (MachineRow r in rows) Machines.Add(r);
        Selected = rows.FirstOrDefault();

        // ---- Section 4: asset-level inertia roll-up ----
        SumKe = $"{sumKeGvaS:F0} GVA.s";
        double reported = _lakeIntrinsicGvaS ?? sumKeGvaS;
        ReportedTotal = $"{reported:F0} GVA.s";
        double gap = reported - sumKeGvaS;
        SyntheticGap = $"{gap:+0;-0;0} GVA.s";

        // ---- Section 5: projected inertia in 15 min if the current ramp holds ----
        double projGvaS = System.Math.Max(0, sumKeGvaS + rampInertiaMwSPerMin * InertiaProjectionMinutes / 1000.0);
        ProjInertia = $"{projGvaS:F0} GVA.s";
        ProjBrush = projGvaS < sumKeGvaS - 1 ? RedB : projGvaS > sumKeGvaS + 1 ? GreenB : MutedB;
        FleetTrend = $"{up} ramping up * {down} de-loading";

        // ---- Section 3: RoCoF relay margin (REAL) - current RoCoF vs both the original (0.125) and
        // relaxed (1.0) protection thresholds. The gap between them is the "companion crime" (Policy 0021 s.10). ----
        double rocof = System.Math.Abs(f.RocofHzPerS);
        RocofNow = $"{rocof:F3} Hz/s";
        double legacyPct = rocof / FrequencyLimits.RocofLimitLegacyHzPerS * 100;
        RocofLegacyPct = System.Math.Min(legacyPct, 100);
        RocofLegacyMargin = $"{legacyPct:F0}% of 0.125 Hz/s (original)";
        RocofLegacyBrush = legacyPct > 100 ? RedB : legacyPct > 80 ? AmberB : GreenB;
        double modernPct = rocof / FrequencyLimits.RocofLimitModernHzPerS * 100;
        RocofModernPct = System.Math.Min(modernPct, 100);
        RocofModernMargin = $"{modernPct:F0}% of 1.0 Hz/s (relaxed)";
        RocofModernBrush = modernPct > 100 ? RedB : modernPct > 80 ? AmberB : GreenB;

        // committed starts: SYNTHETIC placeholder (no unit-commitment feed in the lake)
        if (CommittedStarts.Count == 0)
            CommittedStarts.Add(new StartRow { Note = "no unit-commitment feed - awaiting plant/BOA data" });
    }
}

public sealed class MachineRow : Notify
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private Brush _swatch = Brushes.Gray; public Brush Swatch { get => _swatch; set => Set(ref _swatch, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _mvar = ""; public string Mvar { get => _mvar; set => Set(ref _mvar, value); }
    private string _pf = ""; public string Pf { get => _pf; set => Set(ref _pf, value); }
    private bool _leading; public bool Leading { get => _leading; set => Set(ref _leading, value); }
    private string _excPct = ""; public string ExcPct { get => _excPct; set => Set(ref _excPct, value); }
    private string _avr = ""; public string Avr { get => _avr; set => Set(ref _avr, value); }
    private string _gov = ""; public string Gov { get => _gov; set => Set(ref _gov, value); }
    private double _stress; public double Stress { get => _stress; set => Set(ref _stress, value); }
    private Brush _riskBrush = Brushes.Gray; public Brush RiskBrush { get => _riskBrush; set => Set(ref _riskBrush, value); }
    private string _riskLabel = ""; public string RiskLabel { get => _riskLabel; set => Set(ref _riskLabel, value); }
    // torsional + thermal (Section 2; pulse count REAL-derived, temps SYNTHETIC)
    private int _pulses; public int Pulses { get => _pulses; set => Set(ref _pulses, value); }
    private string _peakStress = ""; public string PeakStress { get => _peakStress; set => Set(ref _peakStress, value); }
    private string _statorTemp = ""; public string StatorTemp { get => _statorTemp; set => Set(ref _statorTemp, value); }
    private string _rotorTemp = ""; public string RotorTemp { get => _rotorTemp; set => Set(ref _rotorTemp, value); }
    private string _thermalTrend = ""; public string ThermalTrend { get => _thermalTrend; set => Set(ref _thermalTrend, value); }
    // inertia (Section 4; KE + H REAL, cert/test SYNTHETIC)
    private string _keGvaS = ""; public string KeGvaS { get => _keGvaS; set => Set(ref _keGvaS, value); }
    private string _hseSec = ""; public string HseSec { get => _hseSec; set => Set(ref _hseSec, value); }
    private string _testDate = ""; public string TestDate { get => _testDate; set => Set(ref _testDate, value); }
    // trajectory (Section 5; REAL)
    private string _rampMwMin = ""; public string RampMwMin { get => _rampMwMin; set => Set(ref _rampMwMin, value); }
    private string _rampArrow = ""; public string RampArrow { get => _rampArrow; set => Set(ref _rampArrow, value); }
    private Brush _rampBrush = Brushes.Gray; public Brush RampBrush { get => _rampBrush; set => Set(ref _rampBrush, value); }
    // P-Q envelope (REAL)
    public double Pg { get; set; }
    public double Qg { get; set; }
    public double Qmin { get; set; }
    public double Qmax { get; set; }
    public double Pmax { get; set; }
}

public sealed class StartRow : Notify
{
    private string _note = ""; public string Note { get => _note; set => Set(ref _note, value); }
}

// ------------------------------------------------ the old Views/InverterFleetVm.cs ------------------------------------------------

/// <summary>One authored asset class shown on the INVERTER FLEET tab.</summary>
public sealed class InverterFleetRow : Notify
{
    public string ClassName { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Count { get; init; } = "";
    public string OnlineMw { get; init; } = "";
    public string Tripped { get; init; } = "";
    public string Soc { get; init; } = "";
    public Brush ModeBrush { get; init; } = Brushes.Gray;
    public Brush TrippedBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// INVERTER FLEET tab - the authored per-asset dynamics view. Shows the fleet's net response, per-class
/// online MW / SoC / trips, and the Loss-of-Mains deficit when embedded generation trips. What the
/// synchronous fleet cannot account for: sample->compute->actuate inverter behaviour. Every control
/// parameter is literature-approximated [VERIFY]; the response is UNVALIDATED AT SCALE. Populated only
/// when the session runs asset dynamics (a case that ships asset-classes.json); otherwise shows OFF.
/// </summary>
public sealed class InverterFleetVm : Notify
{
    private static readonly Brush Following = new SolidColorBrush(new Color(0xFF, 0x3F, 0xA9, 0xF5));   // ORACLE: Color.FromRgb is opaque
    private static readonly Brush Forming = new SolidColorBrush(new Color(0xFF, 0x2F, 0xD2, 0x7F));   // ORACLE: Color.FromRgb is opaque
    private static readonly Brush Alarm = new SolidColorBrush(new Color(0xFF, 0xF5, 0x61, 0x5C));   // ORACLE: Color.FromRgb is opaque
    private static readonly Brush Muted = new SolidColorBrush(new Color(0xFF, 0x6A, 0x76, 0x83));   // ORACLE: Color.FromRgb is opaque

    public ObservableCollection<InverterFleetRow> Classes { get; } = new();

    private bool _active;
    public bool Active { get => _active; set => Set(ref _active, value); }

    private string _status = "Inverter-fleet dynamics are OFF for this session. Load a case with an authored asset-class library to model the inverter fleet.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _netFleetMw = " - "; public string NetFleetMw { get => _netFleetMw; set => Set(ref _netFleetMw, value); }
    private string _classCount = "0"; public string ClassCount { get => _classCount; set => Set(ref _classCount, value); }
    private string _totalUnits = "0"; public string TotalUnits { get => _totalUnits; set => Set(ref _totalUnits, value); }
    private string _totalTripped = "0"; public string TotalTripped { get => _totalTripped; set => Set(ref _totalTripped, value); }
    private Brush _trippedBrush = Muted; public Brush TrippedBrush { get => _trippedBrush; set => Set(ref _trippedBrush, value); }

    private string _lomWarning = ""; public string LomWarning { get => _lomWarning; set => Set(ref _lomWarning, value); }

    public string Provenance { get; } =
        "Every control parameter (droop/df-dt gains, detection/actuate timings, relay settings) is vendor-proprietary and literature-approximated [VERIFY]; the fleet response is UNVALIDATED AT SCALE. Capacity & location are grounded; the curve shape is assumption-driven.";

    public void Update(SimFrame f)
    {
        if (f.AssetFleet is not { Count: > 0 } rollups)
        {
            if (Active) { Active = false; Classes.Clear(); }
            return;
        }

        Active = true;
        Status = "Authored sample->compute->actuate inverter dynamics on the live network.";
        NetFleetMw = f.AssetFleetNetMw.ToString("+0;-0;0") + " MW";

        int units = 0, tripped = 0;
        Classes.Clear();
        foreach (var r in rollups)
        {
            units += r.Count;
            tripped += r.TrippedCount;
            Classes.Add(new InverterFleetRow
            {
                ClassName = r.ClassName,
                Mode = r.Mode,
                Count = r.Count.ToString(),
                OnlineMw = r.OnlineMw.ToString("+0.0;-0.0;0"),
                Tripped = r.TrippedCount.ToString(),
                Soc = r.SocPercent > 0 ? r.SocPercent.ToString("0") + "%" : "-",
                ModeBrush = r.Mode == "grid-forming" ? Forming : Following,
                TrippedBrush = r.TrippedCount > 0 ? Alarm : Muted,
            });
        }

        ClassCount = rollups.Count.ToString();
        TotalUnits = units.ToString();
        TotalTripped = tripped.ToString();
        TrippedBrush = tripped > 0 ? Alarm : Muted;
        LomWarning = f.AssetTrippedMw > 0
            ? $"! {f.AssetTrippedMw:0} MW of embedded generation has tripped on RoCoF/vector-shift protection and been added to the deficit - the 2019-08-09 feedback loop."
            : "";
    }
}
