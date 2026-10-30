using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Dashboard;

/// <summary>
/// Bound state for the whole control suite, without a user-interface framework (UI programme Phase 0.2, the second
/// strangler slice). It turns each solved frame into every value the dashboard shows: the header clock and lamp, the
/// frequency and rpm dials, the headline tiles and their limit notes, the boundary corridors, the voltage-band dials,
/// the generation mix and import links, the alarm list and event log, the selection fly-out, the bridge query panel and
/// HUD, the colour legend and the TSGB trip compare.
///
/// <para>This is the logic of the WPF <c>DashboardViewModel</c>, moved here line for line so the planned Avalonia app
/// binds to the same code. Every displayed string and number is produced by the same formatting call, in the same
/// culture, in the same order as before. The only changes are at the framework boundary:</para>
/// <list type="bullet">
/// <item>a WPF <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes; for example
/// <c>StatusBrush</c> is now <see cref="StatusColour"/>), and the colour legend's gradient brush became the mode it
/// shows (<see cref="Legend"/>, with the gradient stops from <see cref="LegendRampStops"/>);</item>
/// <item>a WPF <c>Visibility</c> became a bool (true = visible; for example <c>InvalidStateVis</c> is now
/// <see cref="InvalidState"/>).</item>
/// </list>
/// <para>The WPF <c>DashboardViewModel</c> derives from this class and maps the tokens back to its frozen brushes and
/// the bools back to <c>Visibility</c> under the old property names, so its XAML bindings are unchanged. The per-tab
/// view-models (ASSETS, MACHINE, SCADA, OPERATOR, MARKET, ...) are still WPF classes; the front end updates and resets
/// them through the overridable hooks below (<see cref="UpdateTabs"/>, <see cref="ResetTabs"/>,
/// <see cref="ForgetModelInTabs"/>, <see cref="ClearStreamTabs"/>), each called at exactly the point the WPF code
/// called them before.</para>
/// </summary>
public class DashboardModel : ObservableModel
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
    /// <summary>The frequency read-out's colour (was the WPF <c>FreqBrush</c>, default <c>Brushes.LightGreen</c>).</summary>
    private UiColour _freqColour = DashboardColours.LightGreen; public UiColour FreqColour { get => _freqColour; set => Set(ref _freqColour, value); }

    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _generation = " - "; public string Generation { get => _generation; set => Set(ref _generation, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _voltage = " - "; public string Voltage { get => _voltage; set => Set(ref _voltage, value); }

    // ---- headline-tile framing: raw numbers alone carry no limit context, so each headline tile
    // also exposes a headroom/limit sub-line (and, where a lamp is warranted, a colour).
    private string _demandNote = " - "; public string DemandNote { get => _demandNote; set => Set(ref _demandNote, value); }
    private string _generationNote = " - "; public string GenerationNote { get => _generationNote; set => Set(ref _generationNote, value); }
    private string _lossesNote = " - "; public string LossesNote { get => _lossesNote; set => Set(ref _lossesNote, value); }

    // Inertia vs the secure minimum for the current largest credible loss  -  the app's whole thesis.
    private string _inertiaNote = " - "; public string InertiaNote { get => _inertiaNote; set => Set(ref _inertiaNote, value); }
    /// <summary>The inertia note's colour (was the WPF <c>InertiaNoteBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _inertiaNoteColour = DashboardColours.Gray; public UiColour InertiaNoteColour { get => _inertiaNoteColour; set => Set(ref _inertiaNoteColour, value); }
    /// <summary>Secure-minimum inertia (GVAs) that holds RoCoF at the limit for the current largest loss.</summary>
    private double _secureMinInertiaGva; public double SecureMinInertiaGva { get => _secureMinInertiaGva; set => Set(ref _secureMinInertiaGva, value); }
    /// <summary>Live inertia headroom above that secure minimum (GVAs); negative = below secure.</summary>
    private double _inertiaHeadroomGva; public double InertiaHeadroomGva { get => _inertiaHeadroomGva; set => Set(ref _inertiaHeadroomGva, value); }

    private string _status = "INITIALISING"; public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>True while the frame being shown did not converge: drives the shared STATE NOT VALID banner over every
    /// tab and the grey veil over the map (was the WPF <c>InvalidStateVis</c>: true = Visible, false = Collapsed).</summary>
    private bool _invalidState; public bool InvalidState { get => _invalidState; set => Set(ref _invalidState, value); }
    /// <summary>The header lamp's colour (was the WPF <c>StatusBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _statusColour = DashboardColours.Gray; public UiColour StatusColour { get => _statusColour; set => Set(ref _statusColour, value); }

    public ObservableCollection<BoundaryRowModel> Boundaries { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<AlarmEntry> Alarms { get; } = new();

    /// <summary>One dial per voltage band in the model  -  how close that band sits to nominal (1.0 pu).</summary>
    public ObservableCollection<VoltageBandDialModel> VoltageBands { get; } = new();
    private readonly Dictionary<double, VoltageBandDialModel> _bandByKv = new();   // keyed by the exact BaseKv, not its label

    private readonly Dictionary<string, BoundaryRowModel> _boundaryByName = new();
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
        // 2026-10-24: a GDA-bridge topology carries no machine inertia constants, so the model's own total reads 0 while
        // the stream's reconstructed inertia (the HUD) is known; the tile then shows the stream's figure, and " - " when
        // the stream has none, rather than a false 0. A model that carries inertia is shown exactly as before.
        double? inertiaGva = EffectiveInertiaGvaS(f.Model);
        Inertia = inertiaGva is double gva ? gva.ToString("F0") : " - ";
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
        // UI programme 0.2: a bool in place of the WPF Visibility (true = Visible, false = Collapsed); the WPF adapter
        // property InvalidStateVis maps it back for the two XAML bindings.
        InvalidState = !converged;
        if (!converged)
        {
            Status = "NOT CONVERGED";
            StatusColour = ColourRamps.Loading(1.1);
        }
        else if (!FreqLocked)
        {
            if (freqEvent) { Status = " FREQUENCY EVENT"; StatusColour = ColourRamps.Loading(1.1); }
            else if (!inBand) { Status = "VOLTAGE ALERT"; StatusColour = ColourRamps.Loading(1.1); }
            else if (overload) { Status = "CORRIDOR ALERT"; StatusColour = ColourRamps.Loading(0.8); }
            else { Status = "SYSTEM NORMAL"; StatusColour = ColourRamps.Voltage(1.0); }
        }

        UpdateBoundaries(boundaries);
        UpdateGeneration(f.Model, f.FrequencyHz, f.GenerationMw);
        UpdateVoltageBands(f);
        // The per-tab view-models (ASSETS, MACHINE, SCADA, OPERATOR, ... CYCLE) are fed here, in the front end, at the
        // point the WPF Update fed them: after the dashboard's own panels, before the alarms and the event log.
        UpdateTabs(f, boundaries, _frameBaseSolveMs);

        // Raise an alarm on the onset of each overload / voltage excursion / non-converged solve (once per onset).
        var cur = new HashSet<string>();
        if (!converged)
        {
            cur.Add("NC");
            if (!_raised.Contains("NC")) AddAlarm("POWER FLOW DID NOT CONVERGE - state not valid", ColourRamps.Loading(1.1));
        }
        foreach (BoundaryFlow b in boundaries)
            if (b.Overloaded)
            {
                string k = "BND:" + b.Name;
                cur.Add(k);
                if (!_raised.Contains(k)) AddAlarm($"{b.Name} OVERLOAD {b.LoadingPercent:F0}%", ColourRamps.Loading(1.1));
            }
        if (!inBand)
        {
            cur.Add("V");
            if (!_raised.Contains("V")) AddAlarm($"VOLTAGE {vmin:F2}-{vmax:F2} pu", ColourRamps.Loading(0.8));
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
    /// Feed the per-tab view-models the frame just applied. Called once per <see cref="Update(SimFrame, IReadOnlyList{BoundaryFlow})"/>,
    /// after the header, tiles, boundaries, generation mix and voltage bands are set (so <see cref="Clock"/> is this
    /// frame's) and before the alarms and log. <paramref name="baseSolveMs"/> is the measured base-solve time (R101; 0
    /// for replay and bridge frames). The neutral dashboard has no tabs of its own, so the default does nothing.
    /// </summary>
    protected virtual void UpdateTabs(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, double baseSolveMs) { }

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
        if (EffectiveInertiaGvaS(model) is not double currentGva)
        {
            // Bridge mode with no inertia on the stream: unknown, so no margin is claimed either way.
            SecureMinInertiaGva = 0;
            InertiaHeadroomGva = 0;
            InertiaNote = " - ";
            InertiaNoteColour = DashboardColours.Gray;
            return;
        }
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
        InertiaNoteColour = secure ? ColourRamps.Voltage(1.0) : ColourRamps.Loading(1.1);
    }

    private void UpdateBoundaries(IReadOnlyList<BoundaryFlow> boundaries)
    {
        var seen = new HashSet<string>();
        foreach (BoundaryFlow b in boundaries)
        {
            seen.Add(b.Name);
            if (!_boundaryByName.TryGetValue(b.Name, out BoundaryRowModel? row))
            {
                row = CreateBoundaryRow(b.Name);
                _boundaryByName[b.Name] = row;
                Boundaries.Add(row);
            }
            double frac = b.LimitMw > 0 ? Math.Abs(b.FlowMw) / b.LimitMw : 0;
            row.Flow = $"{b.FlowMw / 1000.0:+0.00;-0.00;0.00} / {b.LimitMw / 1000.0:F1} GW";
            row.Pct = $"{frac * 100:F0}%";
            row.BarWidth = Math.Min(1.2, frac) * BarTrack;
            row.BarColour = ColourRamps.Loading(frac);
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
            if (!_bandByKv.TryGetValue(kv, out VoltageBandDialModel? d))
            {
                d = CreateVoltageBandDial(kv, KvLabel(kv));
                _bandByKv[kv] = d;
                VoltageBands.Add(d);
            }
            d.Vpu = pu;
            d.Dev = $"{devPct:+0.0;-0.0;0.0}%";
            // The live kV: whole kV as before for transmission and primary levels; up to three decimals below 10 kV so a
            // 0.4 kV level does not read "0 kV".
            double liveKv = pu * kv;
            d.Kv = liveKv >= 10 ? $"{liveKv:F0} kV" : KvLabel(liveKv);
            d.Colour = ColourRamps.Voltage(pu);
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
        FreqColour = Math.Abs(dev) < FreqNominalColourBandHz ? ColourRamps.Voltage(1.0)
                     : !frequencyEvent ? ColourRamps.Loading(0.7) : ColourRamps.Loading(1.1);
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
    /// <summary>True while the detail fly-out is open (was the WPF <c>SelectionVis</c>, default Collapsed).</summary>
    private bool _selectionVisible; public bool SelectionVisible { get => _selectionVisible; set => Set(ref _selectionVisible, value); }
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
        SelectionVisible = true;
    }

    public void ClearSelection() => SelectionVisible = false;

    // ---- on-demand physics query (GDA bridge "what-if"): scenario / feasibility / inertia / condition / post_event ----
    /// <summary>True while the query results panel is open (was the WPF <c>QueryVis</c>, default Collapsed).</summary>
    private bool _queryVisible; public bool QueryVisible { get => _queryVisible; set => Set(ref _queryVisible, value); }
    private string _queryTitle = ""; public string QueryTitle { get => _queryTitle; set => Set(ref _queryTitle, value); }
    private string _querySub = ""; public string QuerySub { get => _querySub; set => Set(ref _querySub, value); }
    private string _queryVerdict = ""; public string QueryVerdict { get => _queryVerdict; set => Set(ref _queryVerdict, value); }
    /// <summary>The verdict badge's colour (was the WPF <c>QueryVerdictBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _queryVerdictColour = DashboardColours.Gray; public UiColour QueryVerdictColour { get => _queryVerdictColour; set => Set(ref _queryVerdictColour, value); }
    private string _queryBinding = ""; public string QueryBinding { get => _queryBinding; set => Set(ref _queryBinding, value); }
    public ObservableCollection<ViolationEntry> QueryViolations { get; } = new();
    /// <summary>True while the violations list is empty (was the WPF <c>QueryViolationsEmptyVis</c>, default Visible).</summary>
    private bool _queryViolationsEmpty = true; public bool QueryViolationsEmpty { get => _queryViolationsEmpty; set => Set(ref _queryViolationsEmpty, value); }

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
        QueryVerdictColour = solveNotConverged ? ColourRamps.Loading(1.1) : VerdictColour(qr.Verdict);
        QueryBinding = string.IsNullOrWhiteSpace(qr.BindingConstraint)
            ? "no binding constraint reported" : $"binding constraint: {qr.BindingConstraint}";
        string at = qr.At == DateTime.MinValue ? "" : qr.At.ToString("yyyy-MM-dd HH:mm") + "Z  *  ";
        QuerySub = at + qr.Op + (hasOverlay ? "  *  what-if state shown on map"
            : solveNotConverged ? "  *  the what-if solve did NOT converge: no valid state, nothing shown on the map"
            : "  *  scalars only (no load-flow overlay)");

        QueryViolations.Clear();
        foreach (Violation v in qr.Violations) QueryViolations.Add(CreateViolation(v));
        QueryViolationsEmpty = QueryViolations.Count == 0;
        QueryVisible = true;
    }

    public void ClearQueryResult()
    {
        QueryVisible = false;
        QueryViolations.Clear();
    }

    // ---- live system HUD (GDA-bridge stream scalars: inertia, fragility, RoCoF headroom) ----
    /// <summary>True while the live HUD card shows (was the WPF <c>HudVis</c>, default Collapsed).</summary>
    private bool _hudVisible; public bool HudVisible { get => _hudVisible; set => Set(ref _hudVisible, value); }
    private string _inertiaGvaS = " - "; public string InertiaGvaS { get => _inertiaGvaS; set => Set(ref _inertiaGvaS, value); }
    private string _fragilityIndex = " - "; public string FragilityIndex { get => _fragilityIndex; set => Set(ref _fragilityIndex, value); }
    /// <summary>The fragility index's colour (was the WPF <c>FragilityBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _fragilityColour = DashboardColours.Gray; public UiColour FragilityColour { get => _fragilityColour; set => Set(ref _fragilityColour, value); }
    private string _rocofHeadroomHzS = " - "; public string RocofHeadroomHzS { get => _rocofHeadroomHzS; set => Set(ref _rocofHeadroomHzS, value); }
    // Tick-over-tick fragility trend: an arrow + word, coloured (rising fragility = worse -> red, easing -> green).
    private string _fragilityTrend = "-"; public string FragilityTrend { get => _fragilityTrend; set => Set(ref _fragilityTrend, value); }
    /// <summary>The trend's colour (was the WPF <c>FragilityTrendBrush</c>, default the muted slate).</summary>
    private UiColour _fragilityTrendColour = DashboardColours.Muted; public UiColour FragilityTrendColour { get => _fragilityTrendColour; set => Set(ref _fragilityTrendColour, value); }
    private double? _prevCondition;   // last fragility index, for the trend delta
    private double? _hudInertiaMwS;   // the bridge stream's system inertia while the HUD shows (null: none, or no HUD)

    /// <summary>
    /// The system inertia the headline tiles show, in GVA.s: the model's own total, unless the model carries no machine
    /// inertia (a GDA-bridge topology has no H constants) while the bridge HUD is showing, in which case the stream's
    /// reconstructed figure, or null (unknown) when the stream has none. Null only in that bridge case.
    /// </summary>
    private double? EffectiveInertiaGvaS(GridModel model)
    {
        double own = model.SystemInertiaGvaS;
        if (own != 0 || !HudVisible) return own;
        return _hudInertiaMwS is double mws ? mws / 1000.0 : null;
    }

    /// <summary>
    /// Feed the compact live HUD from the GDA-bridge <c>state</c> scalars: system inertia (GVA.s = inertiaMwS/1000),
    /// the system fragility/condition index (tinted by <see cref="ColourRamps.Condition"/>) with a tick-over-tick trend,
    /// and the RoCoF headroom (Hz/s to the limit). Also the numeric surface for the scalars-only query ops
    /// (inertia / condition). Shows the card.
    /// </summary>
    public void UpdateHud(double? inertiaMwS, double? conditionIndex, double? rocofHeadroomHzS)
    {
        _hudInertiaMwS = inertiaMwS;
        InertiaGvaS = inertiaMwS is double mw ? (mw / 1000.0).ToString("F1") : " - ";
        if (conditionIndex is double ci)
        {
            FragilityIndex = ci.ToString("F2");
            FragilityColour = ColourRamps.Condition(ci);
            // Trend: worsening fragility is the alarming direction, so tint a rise red and an ease green.
            if (_prevCondition is double p)
            {
                double d = ci - p;
                if (d > 0.005) { FragilityTrend = "^ rising"; FragilityTrendColour = ColourRamps.Condition(Math.Min(1.0, ci + 0.2)); }
                else if (d < -0.005) { FragilityTrend = "v easing"; FragilityTrendColour = ColourRamps.Voltage(1.0); }
                else { FragilityTrend = "= steady"; FragilityTrendColour = DashboardColours.Muted; }
            }
            else { FragilityTrend = "= steady"; FragilityTrendColour = DashboardColours.Muted; }
            _prevCondition = ci;
        }
        else { FragilityIndex = " - "; FragilityColour = DashboardColours.Gray; FragilityTrend = "-"; FragilityTrendColour = DashboardColours.Muted; }
        RocofHeadroomHzS = rocofHeadroomHzS is double hr ? hr.ToString("F3") : " - ";
        HudVisible = true;
    }

    /// <summary>Hide the live HUD (leaving bridge mode / switching to a local sim or replay source).</summary>
    public void HideHud() { HudVisible = false; _prevCondition = null; _hudInertiaMwS = null; }

    /// <summary>
    /// The bridge stream has stopped for good (a socket fault or a horizon rejection): no more frames will arrive.
    /// Fail loud rather than leave the last frame looking live: the header lamp reads STREAM LOST (it used to keep its
    /// last verdict, such as SYSTEM NORMAL), the bridge-only HUD is hidden, the bridge-fed OPERATOR, MARKET and NESO RP1
    /// blocks are cleared (they used to keep their last values indefinitely; see <see cref="ClearStreamTabs"/>), and one
    /// alarm says what the map shows.
    /// </summary>
    public void MarkStreamLost(string reason)
    {
        UiColour red = ColourRamps.Loading(1.1);
        Status = "STREAM LOST";
        StatusColour = red;
        HideHud();
        ClearStreamTabs();
        AddAlarm($"{reason}: STREAM LOST - the map and tabs show the last frame received, not live data", red);
    }

    /// <summary>Clear the bridge-fed tabs (OPERATOR, MARKET, NESO RP1) when the stream is lost. Called by
    /// <see cref="MarkStreamLost"/> after the HUD is hidden and before the alarm; the default does nothing.</summary>
    protected virtual void ClearStreamTabs() { }

    // ---- colour-mode legend (makes the map tint interpretable: ramp swatch + min/max labels) ----
    private string _legendTitle = "LINE LOADING"; public string LegendTitle { get => _legendTitle; set => Set(ref _legendTitle, value); }
    /// <summary>Which ramp the legend swatch shows (was the WPF <c>LegendBrush</c>, default the loading ramp). A front end
    /// draws the swatch from <see cref="LegendRampStops"/>.</summary>
    private MapColourMode _legend = MapColourMode.Loading; public MapColourMode Legend { get => _legend; set => Set(ref _legend, value); }
    private string _legendMin = "0%"; public string LegendMin { get => _legendMin; set => Set(ref _legendMin, value); }
    private string _legendMax = "100%"; public string LegendMax { get => _legendMax; set => Set(ref _legendMax, value); }

    /// <summary>The number of steps in a legend swatch (13 stops, t = 0, 1/12 ... 1), as the WPF ramps were built.</summary>
    public const int LegendRampSteps = 12;

    /// <summary>
    /// The gradient stops of one legend swatch, left to right, built from the same <see cref="ColourRamps"/> functions
    /// the map colours with, so the swatch and the tint agree. Stop i sits at offset t = i / n and carries the ramp's
    /// colour at t (the voltage ramp maps t to 0.94 + t * 0.12 p.u.). This is the old WPF <c>BuildRamp</c>, stop for stop.
    /// </summary>
    public static IReadOnlyList<(UiColour Colour, double Offset)> LegendRampStops(MapColourMode mode, int n = LegendRampSteps)
    {
        Func<double, UiColour> ramp = mode switch
        {
            MapColourMode.Voltage => t => ColourRamps.Voltage(0.94 + t * 0.12),    // 0.94 .. 1.06
            MapColourMode.Condition => t => ColourRamps.Condition(t),            // healthy .. critical
            MapColourMode.Stress => t => ColourRamps.Stress(t),                  // cool .. hot
            _ => t => ColourRamps.Loading(t),                                     // 0 .. 1.0
        };
        var stops = new (UiColour, double)[n + 1];
        for (int i = 0; i <= n; i++) { double t = i / (double)n; stops[i] = (ramp(t), t); }
        return stops;
    }

    /// <summary>Point the legend swatch + min/max labels at the map's current colour mode.</summary>
    public void SetColorLegend(MapColourMode mode)
    {
        (LegendTitle, Legend, LegendMin, LegendMax) = mode switch
        {
            MapColourMode.Voltage   => ("VOLTAGE (p.u.)",  MapColourMode.Voltage,   "0.94", "1.06"),
            MapColourMode.Condition => ("ASSET CONDITION", MapColourMode.Condition, "healthy", "critical"),
            MapColourMode.Stress    => ("BRANCH STRESS",   MapColourMode.Stress,    "cool", "hot"),
            _                       => ("LINE LOADING",    MapColourMode.Loading,   "0%", "100%"),
        };
    }

    // ---- bridge status surfacing (connection state, horizon rejections, query errors - not just console) ----
    /// <summary>True while the bridge status pill shows (was the WPF <c>BridgeStatusVis</c>, default Collapsed).</summary>
    private bool _bridgeStatusVisible; public bool BridgeStatusVisible { get => _bridgeStatusVisible; set => Set(ref _bridgeStatusVisible, value); }
    private string _bridgeStatus = ""; public string BridgeStatus { get => _bridgeStatus; set => Set(ref _bridgeStatus, value); }
    /// <summary>The pill's colour (was the WPF <c>BridgeStatusBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _bridgeStatusColour = DashboardColours.Gray; public UiColour BridgeStatusColour { get => _bridgeStatusColour; set => Set(ref _bridgeStatusColour, value); }

    /// <summary>Surface the live-bridge state on the map (connecting / live / fault / query error), shown as a small pill.</summary>
    public void SetBridgeStatus(string text, UiColour colour)
    {
        BridgeStatus = text;
        BridgeStatusColour = colour;
        BridgeStatusVisible = true;
    }

    /// <summary>Hide the bridge status pill (leaving bridge mode).</summary>
    public void ClearBridgeStatus() => BridgeStatusVisible = false;

    /// <summary>Colour a verdict badge: infeasible/violation = red, feasible/secure = green, otherwise amber.</summary>
    private static UiColour VerdictColour(string? verdict)
    {
        string v = (verdict ?? "").ToUpperInvariant();
        bool bad = v is "INFEASIBLE" or "VIOLATION" or "FAIL" or "INSECURE" or "BREACH" or "UNSECURE";
        bool ok = v is "FEASIBLE" or "SECURE" or "OK" or "PASS";
        return bad ? ColourRamps.Loading(1.1) : ok ? ColourRamps.Voltage(1.0) : ColourRamps.Loading(0.7);
    }

    /// <summary>
    /// A different model is about to be shown (a local model switch, a replay load, a bridge connection or stream
    /// restart). Views that cache per-model structures and detect a switch by a guessed identity (name plus counts)
    /// are told explicitly, because two different networks can share a name and sizes: the SCADA register and the
    /// resident CYCLE service are rebuilt on the next frame (<see cref="ForgetModelInTabs"/>), and the per-frame bus
    /// lookup is dropped.
    /// </summary>
    public void ModelChanged()
    {
        ForgetModelInTabs();
        _busById = null;
        ResetTimeline();
    }

    /// <summary>Tell the tabs that cache per-model structures that the model changed. Called first by
    /// <see cref="ModelChanged"/>; the default does nothing.</summary>
    protected virtual void ForgetModelInTabs() { }

    /// <summary>
    /// Break the timeline: called on model switch and on entering replay so time-integrated state doesn't
    /// carry a previous run's accumulation into the new one. THE MACHINE's integrators are the load-bearing
    /// case (reset by the front end in <see cref="ResetTabs"/>, first); also reset the trip-status lamp and per-onset
    /// alarm de-dup so a stale " FREQUENCY EVENT"/OVERLOAD lamp can't survive the switch.
    /// </summary>
    public void ResetTimeline()
    {
        ResetTabs();
        FreqLocked = false;
        InvalidState = false;   // the next frame decides again
        _raised.Clear();
        _lastInBand = true;
        _lastConverged = true;
    }

    /// <summary>Reset the tabs' time-integrated and bridge-latched state. Called first by <see cref="ResetTimeline"/>;
    /// the default does nothing.</summary>
    protected virtual void ResetTabs() { }

    // ================= Generation & inertia =================
    public const double FuelBarTrack = 210.0;

    public ObservableCollection<FuelRowModel> FuelMix { get; } = new();
    private readonly Dictionary<string, FuelRowModel> _fuelByName = new();

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
    /// <summary>The stability note's colour (was the WPF <c>StabilityBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _stabilityColour = DashboardColours.Gray; public UiColour StabilityColour { get => _stabilityColour; set => Set(ref _stabilityColour, value); }

    public ObservableCollection<ImportLinkRowModel> ImportLinks { get; } = new();
    // Keyed by generator INDEX, not name: gb-full has two "Auchencrosh (interconnector CCT) interconnector"
    // links (125.1 and 220.2 MW) and name keying collapsed them into one row showing only the last (R055).
    private readonly Dictionary<int, ImportLinkRowModel> _linkByIndex = new();
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
            if (!FuelTaxonomy.IsInterconnectorLink(g.Type)) continue;   // the one link rule (bug #28)
            string name = g.Name ?? $"link @ bus {g.Bus}";
            net += g.Pg;

            if (StatusLampRules.IsIdleInterconnector(g.Pg)) continue;   // shared Q10 idle cut-off (0.5 MW)
            seen[gi] = name;

            // A row whose index now holds a differently named link (model swap) is replaced, not reused.
            if (_linkByIndex.TryGetValue(gi, out ImportLinkRowModel? row) && row.Name != name)
            {
                ImportLinks.Remove(row);
                _linkByIndex.Remove(gi);
                row = null;
            }
            if (row is null)
            {
                row = CreateImportLinkRow(gi, name, InterconnectorRoutes.RouteOf(g.Name), $"{g.Pmax:F0} MW");
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
            UiColour colour = importing ? ColourRamps.Voltage(1.0)
                              : exporting ? ColourRamps.Loading(0.8) : ColourRamps.Voltage(0.9);
            row.BarColour = colour;
            row.BadgeColour = colour;
        }

        for (int i = ImportLinks.Count - 1; i >= 0; i--)
            if (!seen.ContainsKey(ImportLinks[i].Index)) { _linkByIndex.Remove(ImportLinks[i].Index); ImportLinks.RemoveAt(i); }

        NetImport = ImportLinks.Count == 0 ? " - "
            : $"{net / 1000.0:+0.00;-0.00;0.00} GW net {(net >= 0 ? "import" : "export")}";
    }

    private void UpdateGeneration(GridModel model, double freqHz, double genMw)
    {
        UpdateImportLinks(model);

        var byFuel = new Dictionary<string, (double mw, UiColour col)>();
        var inertiaByFuel = new Dictionary<string, double>();   //  HMBase per fuel (MWs)
        double sync = 0, conv = 0, fossil = 0, renew = 0, gas = 0, total = 0, co2 = 0;

        foreach (Generator g in model.Generators)
        {
            if (g.Pg <= 0) continue;
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            total += g.Pg;
            if (fi.Synchronous) sync += g.Pg; else conv += g.Pg;
            if (fi.IsFossil) fossil += g.Pg;
            if (fi.IsRenewable) renew += g.Pg;
            if (fi.Name is "CCGT" or "OCGT" or "Gas") gas += g.Pg;
            co2 += g.Pg * CarbonFactors.CombustionTonnesPerMwh(fi.Name);   // per-fuel intensity, not a flat blend
            (double mw, UiColour col) cur = byFuel.GetValueOrDefault(fi.Name);
            byFuel[fi.Name] = (cur.mw + g.Pg, fi.Colour);
            // N325 (DOC-17): the rotating-mass share (N289), so the per-fuel bars agree with SystemInertiaGvaS.
            inertiaByFuel[fi.Name] = inertiaByFuel.GetValueOrDefault(fi.Name) + GridModel.RotatingInertiaMwS(g);
        }
        if (total <= 0) total = 1;
        double maxInertiaGva = inertiaByFuel.Values.DefaultIfEmpty(0).Max() / 1000.0;

        // Fuel rows, biggest first, reusing row objects.
        var seen = new HashSet<string>();
        foreach ((string name, (double mw, UiColour col)) in byFuel.OrderByDescending(kv => kv.Value.mw))
        {
            seen.Add(name);
            if (!_fuelByName.TryGetValue(name, out FuelRowModel? row))
            {
                row = CreateFuelRow(name, col);
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
        StabilityColour = gasForStability ? ColourRamps.Loading(0.85)
            : syncFrac < LowInertiaSyncFraction ? ColourRamps.Loading(1.05) : ColourRamps.Voltage(1.0);
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
    /// <summary>The trip verdict's colour (was the WPF <c>TripVerdictBrush</c>, default <c>Brushes.Transparent</c>).</summary>
    private UiColour _tripVerdictColour = DashboardColours.Transparent; public UiColour TripVerdictColour { get => _tripVerdictColour; set => Set(ref _tripVerdictColour, value); }
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
        TripVerdictColour = helps ? ColourRamps.Voltage(1.0) : ColourRamps.Loading(0.8);
        Log(Clock, $" TSGB compare  trip {label} {Math.Abs(lostMw):F0} MW  RoCoF {baseResp.InitialRocofHzPerS:F2}->{tsgbResp.InitialRocofHzPerS:F2} Hz/s");
        TripCompared?.Invoke();
    }

    /// <summary>True while the alarm list is empty (was the WPF <c>AlarmsEmptyVis</c>, default Visible).</summary>
    private bool _alarmsEmpty = true;
    public bool AlarmsEmpty { get => _alarmsEmpty; set => Set(ref _alarmsEmpty, value); }

    public void AddAlarm(string text, UiColour colour)
    {
        Alarms.Insert(0, CreateAlarm(Clock, text, colour));
        while (Alarms.Count > AlarmListCapacity) Alarms.RemoveAt(Alarms.Count - 1);
        AlarmsEmpty = false;
    }

    public void ClearAlarms()
    {
        Alarms.Clear();
        _raised.Clear();
        AlarmsEmpty = true;
    }

    // ---- row factories: a front end overrides these to put its own row types (with brush properties) in the lists ----

    /// <summary>A new boundary-corridor row for <paramref name="name"/>.</summary>
    protected virtual BoundaryRowModel CreateBoundaryRow(string name) => new() { Name = name };

    /// <summary>A new voltage-band dial for the exact level <paramref name="baseKv"/>, labelled <paramref name="label"/>.</summary>
    protected virtual VoltageBandDialModel CreateVoltageBandDial(double baseKv, string label) => new() { BaseKv = baseKv, Label = label };

    /// <summary>A new generation-mix row for fuel <paramref name="name"/> in <paramref name="colour"/>.</summary>
    protected virtual FuelRowModel CreateFuelRow(string name, UiColour colour) => new() { Name = name, Colour = colour };

    /// <summary>A new import-link row for the interconnector at generator index <paramref name="index"/>.</summary>
    protected virtual ImportLinkRowModel CreateImportLinkRow(int index, string name, string route, string capacity) =>
        new() { Index = index, Name = name, Route = route, Capacity = capacity };

    /// <summary>A new alarm row raised at <paramref name="time"/>.</summary>
    protected virtual AlarmEntry CreateAlarm(string time, string text, UiColour colour) => new(time, text, colour);

    /// <summary>A new query-violation row.</summary>
    protected virtual ViolationEntry CreateViolation(Violation v) => new(v);
}
