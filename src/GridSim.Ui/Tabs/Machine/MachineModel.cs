using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Machine;

// UI programme Phase 1.6 (second tab batch): THE MACHINE tab's logic, moved line for line from the WPF
// src/GridSim.Wpf/Views/MachineVm.cs so the Avalonia app shows the same tab from the same code. Every displayed string
// and number is produced by the same formatting call, in the same culture, in the same order as before. The only
// changes are at the framework boundary: each WPF Brush is a neutral colour token (UiColour, the same four bytes; for
// example DivBrush is now DivColour), and the rows are made through overridable factories so the WPF adapter (which
// derives from this class and keeps the old brush property names) puts its own rows, with their brushes, in the lists.
// UiTabsBatch2ParityTests compares every display property, frame for frame, with a verbatim copy of the old WPF code.

/// <summary>One energy stream in or out of the machine (a fuel feeding it, or a region drawing from it).</summary>
public class StreamRowModel : ObservableModel
{
    /// <summary>The row's identity (a fuel bucket name, or "bus:&lt;id&gt;" for a demand row). Display names repeat
    /// (two buses called "Bolney 132kV"), so rows are never keyed by <see cref="Name"/>.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The row's colour (was the WPF <c>Color</c> brush, default <c>Brushes.Gray</c>; set once, at creation).</summary>
    public UiColour Colour { get; init; } = DashboardColours.Gray;
    private string _gw = ""; public string Gw { get => _gw; set => Set(ref _gw, value); }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Inertia this stream contributes  -  fuel buckets only (demand rows leave it " - ").
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
}

/// <summary>One voltage level of the transmission "shaft"  -  how much power rides each layer,
/// and how far that layer's solved voltage sits above/below its nominal kV.</summary>
public class VoltageLayerModel : ObservableModel
{
    public string Label { get; init; } = "";
    /// <summary>The MW bar's colour (was the WPF <c>Color</c> brush, default <c>Brushes.Gray</c>).</summary>
    private UiColour _colour = DashboardColours.Gray; public UiColour Colour { get => _colour; set => Set(ref _colour, value); }
    private string _gw = ""; public string Gw { get => _gw; set => Set(ref _gw, value); }
    private string _share = ""; public string Share { get => _share; set => Set(ref _share, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Actual-vs-nominal voltage on this tier (the grid is never perfectly nominal).
    private string _kvActual = " - "; public string KvActual { get => _kvActual; set => Set(ref _kvActual, value); } // "402 kV"
    private string _kvDev = " - "; public string KvDev { get => _kvDev; set => Set(ref _kvDev, value); }             // "+0.5%  +2 kV"
    /// <summary>The deviation marker's colour (was the WPF <c>DevBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _devColour = DashboardColours.Gray; public UiColour DevColour { get => _devColour; set => Set(ref _devColour, value); }
    private double _devBar = MachineModel.BarTrack / 2; public double DevBar { get => _devBar; set => Set(ref _devBar, value); }
}

/// <summary>One on-load tap changer in the fleet  -  where its tap sits relative to nominal,
/// and how well it is holding its controlled bus to target.</summary>
public class TapRowModel : ObservableModel
{
    /// <summary>The row's identity: the OLTC record's index (two OLTCs may name the same transformer).</summary>
    public int OltcIndex { get; init; }
    public string Name { get; init; } = "";                                                       // transformer name
    private string _offNom = " - "; public string OffNom { get => _offNom; set => Set(ref _offNom, value); } // "+3.8% off nominal"
    private string _step = " - "; public string Step { get => _step; set => Set(ref _step, value); }         // "tap +3 of +/-8"
    private string _err = " - "; public string Err { get => _err; set => Set(ref _err, value); }             // "holds 33 kV  -0.4% vs target"
    /// <summary>The row's colour (was the WPF <c>Color</c> brush, default <c>Brushes.Gray</c>).</summary>
    private UiColour _colour = DashboardColours.Gray; public UiColour Colour { get => _colour; set => Set(ref _colour, value); }
    private double _barWidth = MachineModel.BarTrack / 2; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
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
/// <para>Fed by <see cref="Update"/> once per solved frame (WPF: from the dashboard's tab hook, after its own panels)
/// and broken by <see cref="Reset"/> on every timeline break (WPF: the dashboard's ResetTabs hook, first).</para>
/// </summary>
public class MachineModel : ObservableModel
{
    public const double BarTrack = 170.0;
    private const int TraceLength = 120;

    public ObservableCollection<StreamRowModel> FuelIn { get; } = new();
    public ObservableCollection<VoltageLayerModel> Layers { get; } = new();
    public ObservableCollection<StreamRowModel> DemandOut { get; } = new();
    public ObservableCollection<TapRowModel> Taps { get; } = new();
    private readonly Dictionary<string, StreamRowModel> _fuelByName = new();
    private readonly Dictionary<string, VoltageLayerModel> _layerByLabel = new();
    private readonly Dictionary<string, StreamRowModel> _demandByName = new();   // keyed by StreamRowModel.Key ("bus:<id>")
    private readonly Dictionary<int, TapRowModel> _tapByOltc = new();             // keyed by OLTC record index

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
    /// <summary>The torque gap's colour (was the WPF <c>DivBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _divColour = DashboardColours.Gray; public UiColour DivColour { get => _divColour; set => Set(ref _divColour, value); }
    private string _divNote = " - "; public string DivNote { get => _divNote; set => Set(ref _divNote, value); }

    // ---- mains clock divergence ----
    private string _accum = " - "; public string Accum { get => _accum; set => Set(ref _accum, value); }
    private string _timeError = " - "; public string TimeError { get => _timeError; set => Set(ref _timeError, value); }
    private string _timeErrorNote = " - "; public string TimeErrorNote { get => _timeErrorNote; set => Set(ref _timeErrorNote, value); }
    private string _accumNote = " - "; public string AccumNote { get => _accumNote; set => Set(ref _accumNote, value); }
    /// <summary>The clock error's colour (was the WPF <c>AccumBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _accumColour = DashboardColours.Gray; public UiColour AccumColour { get => _accumColour; set => Set(ref _accumColour, value); }

    // ---- potential missed regen  -  "saving if taken" ----
    private string _regenMwhText = " - "; public string RegenMwhText { get => _regenMwhText; set => Set(ref _regenMwhText, value); }
    private string _regenMoney = " - "; public string RegenMoney { get => _regenMoney; set => Set(ref _regenMoney, value); }
    private string _regenHomes = " - "; public string RegenHomes { get => _regenHomes; set => Set(ref _regenHomes, value); }

    // ---- the BESS ----
    private string _bessPower = " - "; public string BessPower { get => _bessPower; set => Set(ref _bessPower, value); }
    private string _bessMode = "STANDBY"; public string BessMode { get => _bessMode; set => Set(ref _bessMode, value); }
    /// <summary>The battery's colour (was the WPF <c>BessBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _bessColour = DashboardColours.Gray; public UiColour BessColour { get => _bessColour; set => Set(ref _bessColour, value); }
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
    /// <summary>The system voltage's colour (was the WPF <c>VoltBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _voltColour = DashboardColours.Gray; public UiColour VoltColour { get => _voltColour; set => Set(ref _voltColour, value); }
    private IReadOnlyList<double> _voltTrace = Array.Empty<double>();
    public IReadOnlyList<double> VoltTrace { get => _voltTrace; private set => Set(ref _voltTrace, value); }

    // ---- reactive balance: the Q story, parallel to torque ----
    private double _qGenValue; public double QGenValue { get => _qGenValue; set => Set(ref _qGenValue, value); }
    private string _qGenText = " - "; public string QGenText { get => _qGenText; set => Set(ref _qGenText, value); }
    private string _qAbsText = " - "; public string QAbsText { get => _qAbsText; set => Set(ref _qAbsText, value); }
    private string _qNet = " - "; public string QNet { get => _qNet; set => Set(ref _qNet, value); }
    private string _qNote = " - "; public string QNote { get => _qNote; set => Set(ref _qNote, value); }
    /// <summary>The reactive net's colour (was the WPF <c>QBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _qColour = DashboardColours.Gray; public UiColour QColour { get => _qColour; set => Set(ref _qColour, value); }
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

    // Dial ranges live in the view (GRID SPEED 2970-3030, torque 0-200 MNm  -  peak
    // demand ~60 GW at 50 Hz is 60e9 / (2pi50) ~= 190 MNm, so 200 leaves headroom).

    /// <summary>The colour of the demand rows (the app's accent cyan, as the WPF tab wrote it).</summary>
    public static readonly UiColour DemandColour = ColourRamps.FromHex("#21C7E6");

    /// <summary>Layman fuel buckets  -  coarser than the Generation tab's mix (the WPF <c>Bucket</c>, colour for colour).</summary>
    public static (string name, UiColour col) Bucket(UiFuelInfo fi) => fi.Name switch
    {
        "CCGT" or "OCGT" or "Gas" or "CHP" or "Coal" => ("GAS & THERMAL", ColourRamps.FromHex("#FF8A3D")),
        "Nuclear" => ("NUCLEAR", ColourRamps.FromHex("#7E57C2")),
        "Wind (offshore)" or "Wind (onshore)" => ("WIND", ColourRamps.FromHex("#00B8D4")),
        "Solar" => ("SOLAR", ColourRamps.FromHex("#FFD54F")),
        "Hydro" or "Pumped storage" or "Tidal" => ("HYDRO", ColourRamps.FromHex("#29B6F6")),
        "Biomass" => ("BIOMASS", ColourRamps.FromHex("#8BC34A")),
        "Interconnector" or "Grid infeed" => ("IMPORTS", ColourRamps.FromHex("#90A4AE")),
        "Storage" => ("STORAGE", ColourRamps.FromHex("#EC407A")),
        _ => ("OTHER", ColourRamps.FromHex("#9E9E9E")),
    };

    /// <summary>Show one solved frame (and integrate the time-banked quantities since the last one).</summary>
    public void Update(SimFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
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
            UiFuelInfo fi = UiFuels.Classify(g.Type);
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
        DivColour = Math.Abs(surplusMw) < TorqueNarrativeBandMw ? ColourRamps.Voltage(1.0)
                    : surplusMw > 0 ? ColourRamps.Loading(0.7) : ColourRamps.Loading(1.05);
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
        AccumColour = _accumHzS > 0.5 ? ColourRamps.Voltage(1.0)
                      : _accumHzS < -0.5 ? ColourRamps.Loading(0.9) : ColourRamps.Voltage(0.95);

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
        BessColour = bessMw > bessDead ? ColourRamps.Voltage(1.0) : bessMw < -bessDead ? ColourRamps.Loading(0.8) : ColourRamps.Voltage(0.9);
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
        VoltColour = ColourRamps.Voltage(vavg);

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
        QColour = qNet > 300 ? ColourRamps.Voltage(1.04) : qNet < -300 ? ColourRamps.Voltage(0.95) : ColourRamps.Voltage(1.0);
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
            if (_tapByOltc.TryGetValue(k, out TapRowModel? row) && row.Name != o.Transformer)
            {
                Taps.Remove(row); _tapByOltc.Remove(k); row = null;   // a different model's OLTC at this index
            }
            if (row is null) { row = CreateTapRow(k, o.Transformer); _tapByOltc[k] = row; Taps.Add(row); }
            row.OffNom = $"{offPct:+0.0;-0.0;0.0}% off nominal";
            row.Step = $"tap {step:+0;-0;0} of +/-{maxStep}";
            row.Err = $"holds {ctrlKv:F0} kV  {errPu * 100:+0.0;-0.0;0.0}% vs target";
            row.AtLimit = limit;
            row.Colour = limit ? ColourRamps.Loading(1.05) : ColourRamps.Voltage(1.0 + offPct / 100.0);
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
        var by = new Dictionary<string, (UiColour col, double mw, int units, double inertiaMwS)>();
        foreach (Generator g in m.Generators)
        {
            if (g.Pg <= 0) continue;
            (string name, UiColour col) = Bucket(UiFuels.Classify(g.Type));
            (UiColour c, double mw, int units, double inertiaMwS) cur = by.GetValueOrDefault(name, (col, 0, 0, 0));
            // N325 (DOC-17): the rotating-mass share (N289), so the per-bucket inertia agrees with the total.
            by[name] = (col, cur.mw + g.Pg, cur.units + 1, cur.inertiaMwS + GridModel.RotatingInertiaMwS(g));
        }

        double total = Math.Max(1, totalMw);
        var seen = new HashSet<string>();
        foreach ((string key, (UiColour col, double mw, int units, double inertiaMwS)) in by.OrderByDescending(kv => kv.Value.mw))
        {
            seen.Add(key);
            if (!_fuelByName.TryGetValue(key, out StreamRowModel? row))
            {
                row = CreateStreamRow(key, key, col);
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
        UiColour col = DemandColour;
        double total = Math.Max(1, totalMw);
        var zones = m.Buses.Where(b => b.Pd > 0).OrderByDescending(b => b.Pd).ToList();
        var seen = new HashSet<string>();

        // Rows are keyed by BUS ID and only display the cleaned name. They were keyed by the cleaned name, so two buses
        // of the same name (or names differing only by " 132kV") wrote one row: the second overwrote the first, whose
        // demand was then counted neither in its row nor in EVERYWHERE ELSE, and the column no longer summed to demand.
        void Row(string key, string name, double mw)
        {
            seen.Add(key);
            if (_demandByName.TryGetValue(key, out StreamRowModel? row) && row.Name != name)
            {
                DemandOut.Remove(row); _demandByName.Remove(key); row = null;   // the id now names a different bus
            }
            if (row is null)
            {
                row = CreateStreamRow(key, name, col);
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
            string label = DashboardModel.KvLabel(kv);
            seen.Add(label);
            if (!_layerByLabel.TryGetValue(label, out VoltageLayerModel? layer))
            {
                layer = CreateVoltageLayer(label, ColourRamps.Voltage(1.0 + (kv >= 300 ? 0.0 : 0.02)));
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
                layer.DevColour = ColourRamps.Voltage(puAvg);
                layer.Colour = ColourRamps.Voltage(puAvg);     // tint the MW bar by tier voltage
                layer.DevBar = Math.Clamp(0.5 + dPct / 12.0, 0, 1) * BarTrack; // centre = nominal, +/-6% spans
            }
        }
        for (int i = Layers.Count - 1; i >= 0; i--)
            if (!seen.Contains(Layers[i].Label)) { _layerByLabel.Remove(Layers[i].Label); Layers.RemoveAt(i); }
    }

    private static void Prune(Dictionary<string, StreamRowModel> index, ObservableCollection<StreamRowModel> rows, HashSet<string> seen)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(rows[i].Key)) { index.Remove(rows[i].Key); rows.RemoveAt(i); }
    }

    // ---- row factories: a front end overrides these to put its own row types (with brush properties) in the lists ----

    /// <summary>A new fuel-in or power-out row with identity <paramref name="key"/>, shown as <paramref name="name"/>.</summary>
    protected virtual StreamRowModel CreateStreamRow(string key, string name, UiColour colour) =>
        new() { Key = key, Name = name, Colour = colour };

    /// <summary>A new shaft layer for the voltage level labelled <paramref name="label"/>.</summary>
    protected virtual VoltageLayerModel CreateVoltageLayer(string label, UiColour colour) =>
        new() { Label = label, Colour = colour };

    /// <summary>A new tap-changer row for the OLTC record at <paramref name="oltcIndex"/>.</summary>
    protected virtual TapRowModel CreateTapRow(int oltcIndex, string name) =>
        new() { OltcIndex = oltcIndex, Name = name };
}
