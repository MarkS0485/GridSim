using System.Collections.ObjectModel;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs.Inspector;

namespace GridSim.Ui.Tabs.MachineEngineer;

// UI programme Phase 1.6 (second tab batch): the MECHANICAL / PLANT ENGINEER tab's logic, moved line for line from the
// WPF src/GridSim.Wpf/Views/MachineEngineerVm.cs so the Avalonia app shows the same tab from the same code. Every
// displayed string and number is produced by the same formatting call, in the same culture, in the same order as
// before. The only changes are at the framework boundary: each WPF Brush is a neutral colour token (UiColour, the same
// four bytes; for example ProjBrush is now ProjColour), the WPF InspectMachineCommand is the method InspectMachine (the
// WPF adapter wraps it in its command), and the rows are made through overridable factories so the WPF adapter (which
// derives from this class and keeps the old brush property names) puts its own rows, with their brushes, in the lists.
// UiTabsBatch2ParityTests compares every display property, frame for frame, with a verbatim copy of the old code.

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
/// <para>Fed by <see cref="Update"/> once per solved frame with the dashboard's clock for that frame (WPF: from the
/// dashboard's tab hook), broken by <see cref="Reset"/> on every timeline break (WPF: the dashboard's ResetTabs hook),
/// and told the lake's bottom-up inertia by <see cref="SetIntrinsic"/> (WPF: the GDA bridge only).</para>
/// </summary>
public class MachineEngineerModel : ObservableModel
{
    public ObservableCollection<MachineRowModel> Machines { get; } = new();
    public ObservableCollection<StartRowModel> CommittedStarts { get; } = new();

    // ---- drill-down inspector (click a machine to see its full state broken out) ----
    /// <summary>The drill-down overlay. A front end that wants its own inspector type (the WPF <c>InspectorVm</c>, with
    /// its visibilities and commands) passes it to the constructor.</summary>
    public InspectorModel Inspector { get; }

    /// <summary>A tab with the neutral inspector.</summary>
    public MachineEngineerModel() : this(new InspectorModel()) { }

    /// <summary>A tab whose drill-down overlay is <paramref name="inspector"/>.</summary>
    protected MachineEngineerModel(InspectorModel inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        Inspector = inspector;
    }

    /// <summary>
    /// Open the inspector on one machine's full state (a click on its row; was the WPF <c>InspectMachineCommand</c>).
    /// A null row (no row under the click) does nothing, as before.
    /// </summary>
    public void InspectMachine(MachineRowModel? m)
    {
        if (m is null) return;
        var stats = new[]
        {
            Inspector.CreateStat("OUTPUT", $"{m.Mw} MW"),
            Inspector.CreateStat("STORED KE", $"{m.KeGvaS} GVA.s", Green),
            Inspector.CreateStat("RISK", m.RiskLabel, m.RiskColour),
            Inspector.CreateStat("RAMP", $"{m.RampMwMin}/min", m.RampColour),
        };
        var rows = new[]
        {
            Row("Real power", $"{m.Mw} MW", m.SwatchColour),
            Row("Reactive power", m.Mvar == "-" ? "- (no reactive telemetry)" : $"{m.Mvar} MVAr"),
            Row("Power factor", m.Pf),
            Row("Excitation (proxy)", m.ExcPct),
            Row("AVR mode (synthetic)", m.Avr),         // assumed by fuel class: no AVR telemetry
            Row("Governor mode (synthetic)", m.Gov),    // assumed: no governor telemetry
            Row("Stored kinetic energy", $"{m.KeGvaS} GVA.s  *  H {m.HseSec}", m.SwatchColour, Green),
            Row("Torsional pulses (proxy)", $"{m.Pulses}  *  peak {m.PeakStress}"),
            Row("Stator / rotor temp (synthetic)", $"{m.StatorTemp} / {m.RotorTemp}"),
            Row("MW ramp (last period)", $"{m.RampMwMin} MW/min", m.RampColour, m.RampColour),
            Row("Type-test date", $"{m.TestDate} (asset register not wired)"),
        };
        Inspector.Show(m.Name.ToUpperInvariant(),
            $"{m.Fuel}  *  {m.RiskLabel}  *  per-machine state this frame",
            stats, rows);
    }

    /// <summary>The value colour of an inspector row left at its default (the WPF tab's #E8F0F6).</summary>
    public static readonly UiColour RowValueDefault = ColourRamps.FromHex("#E8F0F6");

    // The WPF Row helper: a swatch left unset is transparent, a value colour left unset is #E8F0F6.
    private InspectRowModel Row(string label, string value, UiColour? swatch = null, UiColour? valueColour = null) =>
        Inspector.CreateRow(label, value, swatch ?? DashboardColours.Transparent, valueColour ?? RowValueDefault);

    // Per-unit state keyed by generator INDEX (Core UnitRampHistory checks bus + name), not by name: gb-full
    // names are not unique, and name keying diffed namesakes against each other every frame, giving phantom
    // ramps and a torsional-pulse count that grew for ever (the Bolney pair differs by over 40 MW) (R054).
    private readonly UnitRampHistory _prevPg = new();
    private readonly Dictionary<int, int> _pulses = new();

    // ---- RoCoF relay margin (Section 3 headline) ----
    private string _rocofNow = "-"; public string RocofNow { get => _rocofNow; set => Set(ref _rocofNow, value); }
    private string _rocofLegacyMargin = "-"; public string RocofLegacyMargin { get => _rocofLegacyMargin; set => Set(ref _rocofLegacyMargin, value); }
    private double _rocofLegacyPct; public double RocofLegacyPct { get => _rocofLegacyPct; set => Set(ref _rocofLegacyPct, value); }
    /// <summary>The legacy margin's colour (was the WPF <c>RocofLegacyBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _rocofLegacyColour = DashboardColours.Gray; public UiColour RocofLegacyColour { get => _rocofLegacyColour; set => Set(ref _rocofLegacyColour, value); }
    private string _rocofModernMargin = "-"; public string RocofModernMargin { get => _rocofModernMargin; set => Set(ref _rocofModernMargin, value); }
    private double _rocofModernPct; public double RocofModernPct { get => _rocofModernPct; set => Set(ref _rocofModernPct, value); }
    /// <summary>The relaxed margin's colour (was the WPF <c>RocofModernBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _rocofModernColour = DashboardColours.Gray; public UiColour RocofModernColour { get => _rocofModernColour; set => Set(ref _rocofModernColour, value); }

    // ---- Section 4: asset-level inertia roll-up ----
    private string _sumKe = "-"; public string SumKe { get => _sumKe; set => Set(ref _sumKe, value); }
    private string _reportedTotal = "-"; public string ReportedTotal { get => _reportedTotal; set => Set(ref _reportedTotal, value); }
    private string _syntheticGap = "-"; public string SyntheticGap { get => _syntheticGap; set => Set(ref _syntheticGap, value); }

    // ---- Section 5: fleet trajectory roll-up ----
    private string _projInertia = "-"; public string ProjInertia { get => _projInertia; set => Set(ref _projInertia, value); }
    /// <summary>The projection's colour (was the WPF <c>ProjBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _projColour = DashboardColours.Gray; public UiColour ProjColour { get => _projColour; set => Set(ref _projColour, value); }
    private string _fleetTrend = "-"; public string FleetTrend { get => _fleetTrend; set => Set(ref _fleetTrend, value); }

    // ---- P-Q diagram for the most-stressed machine (Section 3) ----
    private MachineRowModel? _selected; public MachineRowModel? Selected { get => _selected; set => Set(ref _selected, value); }
    private string _clock = "-"; public string Clock { get => _clock; set => Set(ref _clock, value); }

    private double? _lakeIntrinsicGvaS;

    /// <summary>The tab's fixed colours (the WPF GreenB / AmberB / RedB / MutedB brushes, byte for byte).</summary>
    public static readonly UiColour Green = ColourRamps.FromHex("#2FD27F");
    public static readonly UiColour Amber = ColourRamps.FromHex("#F3C13A");
    public static readonly UiColour Red = ColourRamps.FromHex("#FF5B5B");
    public static readonly UiColour Muted = ColourRamps.FromHex("#8492A0");

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

    /// <summary>Show one solved frame. <paramref name="clock"/> is the dashboard's clock for this frame
    /// (<see cref="TabClock.Of(SimFrame)"/> gives the same string without a dashboard).</summary>
    public void Update(SimFrame f, string clock)
    {
        ArgumentNullException.ThrowIfNull(f);
        Clock = clock;
        // Period length for ramp: derived from the frame clock (like the MACHINE tab), NOT a constant -
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
        var rows = new List<MachineRowModel>();
        double sumKeGvaS = 0, rampInertiaMwSPerMin = 0;
        int up = 0, down = 0;
        IReadOnlyList<Generator> gens = f.Model.Generators;
        for (int gi = 0; gi < gens.Count; gi++)
        {
            Generator g = gens[gi];
            UiFuelInfo fi = UiFuels.Classify(g.Type);
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
            MachineRowModel row = CreateMachineRow();
            row.Name = id;
            row.Fuel = fi.Name;
            row.SwatchColour = fi.Colour;
            row.Mw = $"{mw:N0}";
            row.Mvar = hasReactive ? $"{qg:+0;-0;0}" : "-";
            row.Pf = hasReactive ? $"{pf:F2} {(leading ? "lead" : "lag")}" : "-";
            row.Leading = hasReactive && leading;
            row.ExcPct = hasReactive ? $"{System.Math.Clamp(ExcitationBasePct + ExcitationSlopePct * (g.Qmax > 0 ? qg / g.Qmax : 0), 40, 130):F0}%" : "-";
            // The AVR and governor modes are assumed by fuel class (no AVR or governor telemetry exists), so the
            // Inspector labels both rows "(synthetic)".
            row.Avr = fi.Name is "Nuclear" or "CCGT" or "Biomass" ? "AUTO+PSS" : "AUTO";
            row.Gov = "AUTO";
            row.Stress = stress;
            // AT OR ABOVE each edge, as the shared constants say and the web tab does (>= in per cent): a unit at
            // exactly 95 % reads LIMIT. This compared strictly, so exactly 95 % read NEAR on the desktop.
            row.RiskColour = stress >= StressLimitFraction ? Red : stress >= StressNearFraction ? Amber : Green;
            row.RiskLabel = stress >= StressLimitFraction ? "LIMIT" : stress >= StressNearFraction ? "NEAR" : "OK";
            row.Pulses = pulses;
            row.PeakStress = pulses > 0 ? $"~{System.Math.Min(15 + pulses * 3, 85)}%" : "-";
            row.StatorTemp = $"{StatorTempBaseC + StatorTempSlopeC * load:F0} degC";
            row.RotorTemp = $"{RotorTempBaseC + RotorTempSlopeC * load:F0} degC";
            row.ThermalTrend = load > 0.9 ? "^" : "=";
            row.KeGvaS = $"{keGvaS:F2}";
            row.HseSec = $"{g.H:F1}s";
            row.TestDate = "-";           // SYNTHETIC: no asset register wired
            row.RampMwMin = $"{rampMwMin:+0.0;-0.0;0.0}";
            row.RampArrow = rampMwMin > RampDeadbandMwPerMin ? "^" : rampMwMin < -RampDeadbandMwPerMin ? "v" : "=";
            row.RampColour = rampMwMin > RampDeadbandMwPerMin ? Green : rampMwMin < -RampDeadbandMwPerMin ? Amber : Muted;
            // P-Q envelope (REAL)
            row.Pg = mw; row.Qg = qg;
            row.Qmin = double.IsInfinity(g.Qmin) ? -mw : g.Qmin;
            row.Qmax = double.IsInfinity(g.Qmax) ? mw : g.Qmax;
            row.Pmax = double.IsInfinity(g.Pmax) || g.Pmax <= 0 ? mw : g.Pmax;
            rows.Add(row);
        }

        // sort most-stressed first (Section 1 requirement)
        rows.Sort((a, b) => b.Stress.CompareTo(a.Stress));
        Machines.Clear();
        foreach (MachineRowModel r in rows) Machines.Add(r);
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
        ProjColour = projGvaS < sumKeGvaS - 1 ? Red : projGvaS > sumKeGvaS + 1 ? Green : Muted;
        FleetTrend = $"{up} ramping up * {down} de-loading";

        // ---- Section 3: RoCoF relay margin (REAL) - current RoCoF vs both the original (0.125) and
        // relaxed (1.0) protection thresholds. The gap between them is the "companion crime" (Policy 0021 s.10). ----
        double rocof = System.Math.Abs(f.RocofHzPerS);
        RocofNow = $"{rocof:F3} Hz/s";
        double legacyPct = rocof / FrequencyLimits.RocofLimitLegacyHzPerS * 100;
        RocofLegacyPct = System.Math.Min(legacyPct, 100);
        RocofLegacyMargin = $"{legacyPct:F0}% of 0.125 Hz/s (original)";
        RocofLegacyColour = legacyPct > 100 ? Red : legacyPct > 80 ? Amber : Green;
        double modernPct = rocof / FrequencyLimits.RocofLimitModernHzPerS * 100;
        RocofModernPct = System.Math.Min(modernPct, 100);
        RocofModernMargin = $"{modernPct:F0}% of 1.0 Hz/s (relaxed)";
        RocofModernColour = modernPct > 100 ? Red : modernPct > 80 ? Amber : Green;

        // committed starts: SYNTHETIC placeholder (no unit-commitment feed in the lake)
        if (CommittedStarts.Count == 0)
            CommittedStarts.Add(CreateStartRow("no unit-commitment feed - awaiting plant/BOA data"));
    }

    // ---- row factories: a front end overrides these to put its own row types (with brush properties) in the lists ----

    /// <summary>A new, empty machine row (the tab fills every property).</summary>
    protected virtual MachineRowModel CreateMachineRow() => new();

    /// <summary>A new committed-starts row carrying <paramref name="note"/>.</summary>
    protected virtual StartRowModel CreateStartRow(string note) => new() { Note = note };
}

/// <summary>One synchronous machine's row: electrical state, protection margin, inertia, trajectory, P-Q envelope.</summary>
public class MachineRowModel : ObservableModel
{
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    /// <summary>The fuel swatch's colour (was the WPF <c>Swatch</c> brush, default <c>Brushes.Gray</c>).</summary>
    private UiColour _swatchColour = DashboardColours.Gray; public UiColour SwatchColour { get => _swatchColour; set => Set(ref _swatchColour, value); }
    private string _mw = ""; public string Mw { get => _mw; set => Set(ref _mw, value); }
    private string _mvar = ""; public string Mvar { get => _mvar; set => Set(ref _mvar, value); }
    private string _pf = ""; public string Pf { get => _pf; set => Set(ref _pf, value); }
    private bool _leading; public bool Leading { get => _leading; set => Set(ref _leading, value); }
    private string _excPct = ""; public string ExcPct { get => _excPct; set => Set(ref _excPct, value); }
    private string _avr = ""; public string Avr { get => _avr; set => Set(ref _avr, value); }
    private string _gov = ""; public string Gov { get => _gov; set => Set(ref _gov, value); }
    private double _stress; public double Stress { get => _stress; set => Set(ref _stress, value); }
    /// <summary>The risk lamp's colour (was the WPF <c>RiskBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _riskColour = DashboardColours.Gray; public UiColour RiskColour { get => _riskColour; set => Set(ref _riskColour, value); }
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
    /// <summary>The ramp's colour (was the WPF <c>RampBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _rampColour = DashboardColours.Gray; public UiColour RampColour { get => _rampColour; set => Set(ref _rampColour, value); }
    // P-Q envelope (REAL)
    public double Pg { get; set; }
    public double Qg { get; set; }
    public double Qmin { get; set; }
    public double Qmax { get; set; }
    public double Pmax { get; set; }
}

/// <summary>One committed-start line (a placeholder until a unit-commitment feed is wired).</summary>
public class StartRowModel : ObservableModel
{
    private string _note = ""; public string Note { get => _note; set => Set(ref _note, value); }
}
