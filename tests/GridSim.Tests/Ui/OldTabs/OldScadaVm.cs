// Test oracle for UiTabsParityTests (UI programme Phase 1.6, first tab batch). NOT production code.
//
// Below is the WPF ScadaVm exactly as it was before its logic moved to src/GridSim.Ui/Tabs (src/GridSim.Wpf/Views/ScadaVm.cs
// at 2.0.22), copied mechanically from git. The only edits are:
//   - the line "using System.Windows.Media;" removed (the shims in OldTabsShims.cs stand in) (1x);
//   - the line "using GridSim.Wpf.Visuals;" removed (Palette and Fuels are shims) (1x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabs (1x);
//   - "System.Windows.Visibility" written "Visibility" (the shim) (6x);
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

using System.Collections.ObjectModel;
using System.Windows.Input;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldTabs;

/// <summary>A command that takes its bound row as the parameter  -  used by the per-row ACK button.</summary>
public sealed class RowCommand(Action<object?> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec(p);
}

/// <summary>
/// One telemetry point in the SCADA register  -  a single RTU analog/status reading.
/// The row object is reused across frames (keyed by <see cref="Tag"/>); only the live
/// columns (value, quality, timestamp) churn. <see cref="Vis"/> carries the cached filter
/// decision so the grid can collapse non-matching rows without rebinding the collection.
/// </summary>
public sealed class ScadaPoint : Notify
{
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string Units { get; init; } = "";

    private string _value = " - "; public string Value { get => _value; set => Set(ref _value, value); }
    private string _quality = "GOOD"; public string Quality { get => _quality; set => Set(ref _quality, value); }
    private Brush _qualityBrush = Brushes.Gray; public Brush QualityBrush { get => _qualityBrush; set => Set(ref _qualityBrush, value); }
    private string _stamp = ""; public string Stamp { get => _stamp; set => Set(ref _stamp, value); }

    // Loading vs rating - only meaningful for branch flow points; other kinds show a dash.
    private string _load = " - "; public string Load { get => _load; set => Set(ref _load, value); }
    private Brush _loadBrush = Brushes.Gray; public Brush LoadBrush { get => _loadBrush; set => Set(ref _loadBrush, value); }

    private Visibility _vis = Visibility.Visible;
    public Visibility Vis { get => _vis; set => Set(ref _vis, value); }

    // What kind of point this is and where it reads from in the solved frame  -  fixed at build time.
    public PointKind Kind { get; init; }
    public int Index { get; init; }   // bus row index, branch index, or oltc index depending on Kind
}

/// <summary>Where a <see cref="ScadaPoint"/> sources its live value in the solved frame.</summary>
public enum PointKind { BusVoltage, BranchP, BranchQ, OltcTap, SystemFreq }

/// <summary>One row in the event/alarm log  -  a raised condition with an operator acknowledgement.</summary>
public sealed class ScadaEvent : Notify
{
    public string Stamp { get; init; } = "";
    public string Priority { get; init; } = ScadaThresholds.PriorityInformation;
    public Brush PriorityBrush { get; init; } = Brushes.Gray;
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";

    private bool _acked; public bool Acked { get => _acked; set { Set(ref _acked, value); Raise(nameof(RowOpacity)); Raise(nameof(AckLabel)); } }
    public double RowOpacity => _acked ? 0.45 : 1.0;
    public string AckLabel => _acked ? "ACKED" : "ACK";
}

/// <summary>
/// THE SCADA TAB  -  a simulated supervisory console over the live grid: a telemetry
/// register of tag-named points, and an event/alarm log raised off the same solved
/// frames. The data shapes (tags, qualities, priorities, timestamps) mirror what a real
/// EMS/SCADA front-end would carry, so the streams are a sane prediction target for ML.
/// Nothing here drives the grid  -  it only observes it.
/// </summary>
public sealed class ScadaVm : Notify
{
    public ObservableCollection<ScadaPoint> Points { get; } = new();
    public ObservableCollection<ScadaEvent> Events { get; } = new();

    // P+Q on the heaviest corridors - count shared with the web ScadaService via ScadaThresholds.TopCorridors.
    private const int MaxEvents = 200;

    // ---- register summary ----
    private string _pointCount = " - "; public string PointCount { get => _pointCount; set => Set(ref _pointCount, value); }
    private string _goodCount = " - "; public string GoodCount { get => _goodCount; set => Set(ref _goodCount, value); }

    // ---- filter box (TwoWay)  -  a plain Contains over tag + description ----
    private string _filter = ""; public string Filter { get => _filter; set { Set(ref _filter, value); ApplyFilter(); } }

    // ---- event log summary ----
    private string _unacked = "0"; public string Unacked { get => _unacked; set => Set(ref _unacked, value); }

    public ICommand AckAllCmd { get; }
    public ICommand AckCmd { get; }

    /// <summary>Click-through for a register point: the host (MainWindow) wires this to switch to the
    /// ASSETS tab and open the point's substation/circuit/transformer in the explorer.</summary>
    public Action<ScadaPoint>? Navigate { get; set; }
    public ICommand OpenPointCmd { get; }

    public ScadaVm()
    {
        AckAllCmd = new RowCommand(_ => AckAll());
        AckCmd = new RowCommand(o => { if (o is ScadaEvent e) { e.Acked = true; RefreshUnacked(); } });
        OpenPointCmd = new RowCommand(o => { if (o is ScadaPoint p) Navigate?.Invoke(p); });
    }

    // ---- point register, built once per model ----
    // LiveSim.Step hands us a FRESH GridModel clone every tick (post-solve tap ratios), so
    // reference identity is useless for "same network" detection - it is never stable across
    // ticks. Track a clone-stable identity instead (model name + topology sizes); it changes
    // only on a genuine network switch, which is exactly when the register must be rebuilt.
    private string? _builtName;
    private int _builtBusCount = -1;
    private int _builtBranchCount = -1;

    // Per-frame condition tracking for event de-duplication / tap-move detection.
    private readonly Dictionary<string, double> _lastTap = new();
    private bool _lastVoltageInBand = true;
    private bool _lastFreqInBand = true;
    private bool _lastConverged = true;
    // The register's top priority (P1, red, the voltage-band priority's level) for a power flow that did not converge.
    private const string PriorityStateInvalid = ScadaThresholds.PriorityVoltageOutOfBand;

    /// <summary>
    /// Forget the network the register was built for, so the next frame rebuilds it. Called on every model switch
    /// (local model, replay, bridge (re)start): the clone-stable identity below (name plus bus and branch counts) cannot
    /// tell apart two different networks with the same name and sizes, so it is only the per-tick guard, not the
    /// model-switch detector.
    /// </summary>
    public void ForgetModel()
    {
        _builtName = null;
        _builtBusCount = -1;
        _builtBranchCount = -1;
    }
    private readonly HashSet<string> _liveBranchAlarms = new();   // corridor tags currently over the 90% P2 threshold
    private readonly HashSet<string> _liveBranchWatch = new();    // corridor tags currently over the 70% watch threshold

    public void Update(SimFrame frame, string clock)
    {
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // Detect a genuine model swap (network change) by CLONE-STABLE identity: the model
        // name plus its topology sizes. These survive LiveSim's per-tick cloning, so the
        // register - and the _lastTap / alarm / watch condition state below - now persist
        // across ticks of the same network and reset only when the network actually changes.
        if (!string.Equals(m.Name, _builtName, StringComparison.Ordinal)
            || m.BusCount != _builtBusCount
            || m.Branches.Count != _builtBranchCount)
        {
            BuildRegister(m, r);
            _builtName = m.Name;
            _builtBusCount = m.BusCount;
            _builtBranchCount = m.Branches.Count;
            _lastTap.Clear();
            _liveBranchAlarms.Clear();
            _liveBranchWatch.Clear();
            _lastVoltageInBand = true;
            _lastFreqInBand = true;
            _lastConverged = true;
        }

        RefreshValues(frame, clock);
        RaiseEvents(frame, clock);
    }

    // =================================================================================
    // Register construction  -  ONCE per model. Picks a representative ~150-point subset:
    // every EHV (>= 275 kV) bus voltage, the heaviest-loaded corridors' P/Q, all OLTC
    // taps on the visible subset, and system frequency. Tag names are derived from the
    // substation / circuit names so they read like real EMS points.
    // =================================================================================
    private void BuildRegister(GridModel m, PowerFlowResult r)
    {
        Points.Clear();

        // Parallel circuits and identically-named substations can collide on a tag; a real
        // SCADA database never carries a duplicate key. Uniquify by appending a circuit suffix.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Uniq(string tag)
        {
            if (used.Add(tag)) return tag;
            for (int c = 2; ; c++)
            {
                string t = $"{tag}-{c}";
                if (used.Add(t)) return t;
            }
        }

        // Bus id -> its row index in the solved Vm/Va arrays (index-aligned with BusIds).
        var rowOf = new Dictionary<int, int>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) rowOf[r.BusIds[i]] = i;

        var busById = new Dictionary<int, Bus>(m.Buses.Count);
        foreach (Bus b in m.Buses) busById[b.Id] = b;

        // ---- EHV bus voltages ----
        // On the big GB model "all EHV buses" is already a sane cap; on the small reduced
        // model nearly everything is EHV, so this is the bulk of the register either way.
        foreach (Bus b in m.Buses)
        {
            if (b.BaseKv < ScadaThresholds.EhvCutoffKv) continue;
            if (!rowOf.TryGetValue(b.Id, out int row)) continue;
            Points.Add(new ScadaPoint
            {
                Tag = Uniq($"{Site(b.Name, b.Id)}.{Kv(b.BaseKv)}.V_PU"),
                Description = $"{Clean(b.Name) ?? $"BUS {b.Id}"} {Kv(b.BaseKv)} kV busbar voltage",
                Units = "pu",
                Kind = PointKind.BusVoltage,
                Index = row,
            });
        }

        // ---- heaviest corridors: P and Q on each (index-aligned with Branches) ----
        int n = Math.Min(m.Branches.Count, r.LineFlows.Count);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        // Partial-ish: a full sort of <=2,268 branches once per model is cheap and clear.
        Array.Sort(order, (a, b) => r.LineFlows[b].LoadingPercent.CompareTo(r.LineFlows[a].LoadingPercent));

        int taken = 0;
        foreach (int i in order)
        {
            if (taken >= ScadaThresholds.TopCorridors) break;
            Branch br = m.Branches[i];
            if (br.IsTransformer) continue;   // corridors only; transformers carry taps instead
            string tag = Corridor(busById, br);
            string kv = Kv(MaxKv(busById, br));
            string pTag = Uniq($"{tag}.{kv}.P_MW");
            Points.Add(new ScadaPoint
            {
                Tag = pTag,
                Description = $"{tag} active power flow",
                Units = "MW", Kind = PointKind.BranchP, Index = i,
            });
            Points.Add(new ScadaPoint
            {
                Tag = Uniq($"{tag}.{kv}.Q_MVAR"),
                Description = $"{tag} reactive power flow",
                Units = "MVAr", Kind = PointKind.BranchQ, Index = i,
            });
            taken++;
        }

        // ---- OLTC tap positions ----
        // The branch each OLTC drives comes from the solver's own mapping (name AND controlled-bus end, then the first
        // of the name), so with parallel same-named transformers TAP_POS reads the unit the solver regulates. This used
        // the first branch of the name.
        int[] branchOfOltc = GridSim.Core.PowerFlow.SteadyStateSolver.MapOltcRecordsToBranches(m);

        for (int k = 0; k < m.Oltcs.Count; k++)
        {
            Oltc o = m.Oltcs[k];
            int bi = branchOfOltc[k];
            if (bi < 0) continue;
            string site = Site(busById.GetValueOrDefault(o.ControlsBus)?.Name, o.ControlsBus);
            Points.Add(new ScadaPoint
            {
                Tag = Uniq($"{site}-SGT.TAP_POS"),
                Description = $"{Clean(o.Transformer) ?? o.Transformer} on-load tap ratio",
                Units = "pu", Kind = PointKind.OltcTap, Index = bi,
            });
        }

        // ---- system frequency ----
        Points.Add(new ScadaPoint
        {
            Tag = "SYS.F_HZ",
            Description = "System frequency (synchronous area)",
            Units = "Hz", Kind = PointKind.SystemFreq, Index = -1,
        });

        ApplyFilter();
    }

    // =================================================================================
    // Per-frame value refresh  -  touches only the live columns. Quality goes STALE for a
    // few ticks on a pseudo-random point (seeded off the tick, so it's deterministic yet
    // looks alive) the way a real RTU drops out and the value freezes.
    // =================================================================================
    private void RefreshValues(SimFrame frame, string clock)
    {
        PowerFlowResult r = frame.Result;
        int tick = frame.Tick;
        // Fail loud: an unconverged power flow is not telemetry of an operating point, so every point that reads the
        // solve (bus voltages, branch P/Q, tap ratios after the tap loop) is flagged INVALID in red, not GOOD. System
        // frequency comes from the swing integrator, not the power flow, so it keeps its own quality.
        bool solveValid = r.Converged;

        // One point goes stale per 3-tick window, chosen deterministically from the tick  -
        // the way a real RTU briefly drops out and its value freezes (count==0 => never).
        int count = Points.Count;
        int staleAt = count > 0 ? Hash(tick / 3) % count : -1;

        int good = 0, visible = 0, invalid = 0;
        for (int p = 0; p < count; p++)
        {
            ScadaPoint pt = Points[p];
            bool stale = p == staleAt;

            if (!stale && !solveValid && pt.Kind != PointKind.SystemFreq)
            {
                // The unconverged iterate is still shown (it is what the solver stopped on) but never as good data.
                pt.Value = pt.Kind switch
                {
                    PointKind.BusVoltage => Read(r.Vm, pt.Index).ToString("F4"),
                    PointKind.BranchP => SignedP(r, pt.Index).ToString("F1"),
                    PointKind.BranchQ => SignedQ(r, pt.Index).ToString("F1"),
                    PointKind.OltcTap => frame.Model.Branches[pt.Index].TapRatio.ToString("F4"),
                    _ => pt.Value,
                };
                pt.Quality = "INVALID";
                pt.QualityBrush = RedBrush;
                pt.Stamp = clock;
                invalid++;
            }
            else if (!stale)
            {
                pt.Value = pt.Kind switch
                {
                    PointKind.BusVoltage => Read(r.Vm, pt.Index).ToString("F4"),
                    PointKind.BranchP => SignedP(r, pt.Index).ToString("F1"),
                    PointKind.BranchQ => SignedQ(r, pt.Index).ToString("F1"),
                    PointKind.OltcTap => frame.Model.Branches[pt.Index].TapRatio.ToString("F4"),
                    PointKind.SystemFreq => frame.FrequencyHz.ToString("F3"),
                    _ => pt.Value,
                };
                // Loading vs rating for branch flow points; other kinds have no line rating.
                if (pt.Kind is PointKind.BranchP or PointKind.BranchQ
                    && (uint)pt.Index < (uint)r.LineFlows.Count)
                {
                    double loadPct = r.LineFlows[pt.Index].LoadingPercent;
                    pt.Load = $"{loadPct:F0}%";
                    pt.LoadBrush = Palette.Frozen(Palette.Loading(loadPct / 100.0));
                }
                pt.Quality = "GOOD";
                pt.QualityBrush = GoodBrush;
                pt.Stamp = clock;
                good++;
            }
            else
            {
                // Value frozen at its last reading; quality flagged, timestamp not advanced.
                pt.Quality = "STALE";
                pt.QualityBrush = StaleBrush;
            }

            if (pt.Vis == Visibility.Visible) visible++;
        }

        PointCount = $"{visible} / {count} points";
        GoodCount = invalid > 0 ? $"{good} GOOD  {invalid} INVALID (power flow did not converge)" : $"{good} GOOD";
    }

    private static double Read(double[] a, int i) => (uint)i < (uint)a.Length ? a[i] : 0;
    private static double SignedP(PowerFlowResult r, int i) =>
        (uint)i < (uint)r.LineFlows.Count ? r.LineFlows[i].PFromMw : 0;
    private static double SignedQ(PowerFlowResult r, int i) =>
        (uint)i < (uint)r.LineFlows.Count ? r.LineFlows[i].QFromMvar : 0;

    // =================================================================================
    // Event/alarm generation  -  raised off real conditions in the frame, most-recent
    // first, deduplicated on the onset of each condition (so a sustained excursion logs
    // once, not every tick). Priorities follow operator convention:
    //   P1 (Red)    -  bus voltage out of the 0.94-1.06 statutory band
    //   P2 (Amber)  -  corridor loading > 90%, or frequency outside 49.9-50.1 Hz
    //   P3 (Muted)  -  corridor loading > 70%, or an OLTC tap movement (info)
    // The priority strings are Core's ScadaThresholds.Priority* constants, shared with the web console (D-DOC-147:
    // the web raised the frequency excursion as P1; both now raise it as P2, the reason is on the constant).
    // =================================================================================
    private void RaiseEvents(SimFrame frame, string clock)
    {
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // --- power flow validity: one top-priority (P1, red) event on the onset of non-convergence, one information
        // event on recovery. The register used to stay silent while every solved point read GOOD. ---
        bool converged = r.Converged;
        if (!converged && _lastConverged)
            Add(clock, PriorityStateInvalid, RedBrush, "SYS.PF", "POWER FLOW DID NOT CONVERGE  -  solved points are INVALID");
        else if (converged && !_lastConverged)
            Add(clock, ScadaThresholds.PriorityInformation, MutedBrush, "SYS.PF", "Power flow converged again");
        _lastConverged = converged;

        // --- voltage band, summarised once on the band edge (avoids 2,000 events/tick) ---
        double vmin = double.PositiveInfinity, vmax = double.NegativeInfinity;
        int worstBus = 0;
        for (int i = 0; i < r.Vm.Length; i++)
        {
            double v = r.Vm[i];
            if (v < vmin) { vmin = v; worstBus = i; }
            if (v > vmax) vmax = v;
        }
        bool vInBand = vmin >= ScadaThresholds.VoltageBandLowPu && vmax <= ScadaThresholds.VoltageBandHighPu;
        if (!vInBand && _lastVoltageInBand)
        {
            string src = worstBus < r.BusIds.Count ? $"BUS-{r.BusIds[worstBus]}" : "SYS";
            Add(clock, ScadaThresholds.PriorityVoltageOutOfBand, RedBrush, src, $"VOLTAGE OUT OF BAND {vmin:F3}-{vmax:F3} pu");
        }
        else if (vInBand && !_lastVoltageInBand)
            Add(clock, ScadaThresholds.PriorityInformation, MutedBrush, "SYS", "Voltages returned to band");
        _lastVoltageInBand = vInBand;

        // --- frequency band ---
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the alarm band about the frame's
        // own nominal (ScadaThresholds.IsFrequencyAlarm: the 49.9 / 50.1 constants themselves at 50 Hz, the same text).
        double nominalHz = frame.Model.FrequencyHz;
        bool fInBand = !ScadaThresholds.IsFrequencyAlarm(frame.FrequencyHz, nominalHz);
        if (!fInBand && _lastFreqInBand)
            Add(clock, ScadaThresholds.PriorityFrequencyOutOfBand, AmberBrush, "SYS.F_HZ", $"FREQUENCY {frame.FrequencyHz:F3} Hz OUTSIDE {ScadaThresholds.FreqAlarmBandText(nominalHz)}");
        else if (fInBand && !_lastFreqInBand)
            Add(clock, ScadaThresholds.PriorityInformation, MutedBrush, "SYS.F_HZ", $"Frequency recovered {frame.FrequencyHz:F3} Hz");
        _lastFreqInBand = fInBand;

        // --- corridor loading, only on the registered (heaviest) points to bound cost ---
        // Each threshold logs once on its crossing (tracked per corridor), not every tick.
        var nowAlarm = new HashSet<string>();
        var nowWatch = new HashSet<string>();
        foreach (ScadaPoint pt in Points)
        {
            if (pt.Kind != PointKind.BranchP) continue;   // one event-bearing point per corridor
            int i = pt.Index;
            if ((uint)i >= (uint)r.LineFlows.Count) continue;
            double load = r.LineFlows[i].LoadingPercent;
            string corridor = pt.Tag;   // already the corridor tag with .P_MW suffix

            if (load > ScadaThresholds.CorridorAlarmPct)
            {
                nowAlarm.Add(corridor);
                nowWatch.Add(corridor);
                if (!_liveBranchAlarms.Contains(corridor))
                    Add(clock, ScadaThresholds.PriorityCorridorAlarm, AmberBrush, corridor, $"CORRIDOR LOADING {load:F0}% OF RATING");
            }
            else if (load > ScadaThresholds.CorridorWatchPct)
            {
                nowWatch.Add(corridor);
                if (!_liveBranchWatch.Contains(corridor))
                    Add(clock, ScadaThresholds.PriorityInformation, MutedBrush, corridor, $"corridor loading {load:F0}%  -  watch");
            }
        }
        Swap(_liveBranchAlarms, nowAlarm);
        Swap(_liveBranchWatch, nowWatch);

        // --- OLTC tap movements (information, P3) ---
        foreach (ScadaPoint pt in Points)
        {
            if (pt.Kind != PointKind.OltcTap) continue;
            double tap = m.Branches[pt.Index].TapRatio;
            if (_lastTap.TryGetValue(pt.Tag, out double prev))
            {
                if (Math.Abs(tap - prev) > 1e-6)
                    Add(clock, ScadaThresholds.PriorityInformation, MutedBrush, pt.Tag, $"TAP MOVED {prev:F4} -> {tap:F4} pu");
            }
            _lastTap[pt.Tag] = tap;
        }

        RefreshUnacked();
    }

    private void Add(string clock, string priority, Brush brush, string source, string message)
    {
        Events.Insert(0, new ScadaEvent
        {
            Stamp = clock, Priority = priority, PriorityBrush = brush, Source = source, Message = message,
        });
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }

    private void AckAll()
    {
        foreach (ScadaEvent e in Events) e.Acked = true;
        RefreshUnacked();
    }

    private void RefreshUnacked()
    {
        int n = 0;
        foreach (ScadaEvent e in Events) if (!e.Acked) n++;
        Unacked = n.ToString();
    }

    private static void Swap(HashSet<string> live, HashSet<string> now)
    {
        live.Clear();
        foreach (string s in now) live.Add(s);
    }

    // ---- filter ----
    private void ApplyFilter()
    {
        string f = _filter?.Trim() ?? "";
        int visible = 0;
        foreach (ScadaPoint pt in Points)
        {
            bool show = f.Length == 0
                || pt.Tag.Contains(f, StringComparison.OrdinalIgnoreCase)
                || pt.Description.Contains(f, StringComparison.OrdinalIgnoreCase);
            pt.Vis = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) visible++;
        }
        PointCount = $"{visible} / {Points.Count} points";
    }

    // =================================================================================
    // Tag-naming helpers  -  turn the model's free-text names into EMS-style point tags.
    // =================================================================================

    /// <summary>A short uppercase site code from a substation name, falling back to the bus id.</summary>
    private static string Site(string? name, int busId)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"BUS{busId}";
        // Take the leading word(s), strip voltage suffixes, squash to an alnum code.
        string s = Clean(name) ?? name;
        int sp = s.IndexOf(' ');
        if (sp > 0) s = s[..sp];
        s = new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length == 0 ? $"BUS{busId}" : s.Length > 8 ? s[..8] : s;
    }

    /// <summary>Strip kV suffixes / trailing noise so a name reads cleanly in a tag or description.</summary>
    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Replace(" 400kV", "").Replace(" 400 kV", "")
                   .Replace(" 275kV", "").Replace(" 275 kV", "")
                   .Replace(" 132kV", "").Replace(" 132 kV", "").Trim();
    }

    private static string Kv(double kv) => kv >= 1 ? ((int)Math.Round(kv)).ToString() : "EHV";

    private static double MaxKv(Dictionary<int, Bus> byId, Branch br) =>
        Math.Max(byId.GetValueOrDefault(br.From)?.BaseKv ?? 0, byId.GetValueOrDefault(br.To)?.BaseKv ?? 0);

    /// <summary>A corridor tag "FROM-TO" built from the two endpoint site codes.</summary>
    private static string Corridor(Dictionary<int, Bus> byId, Branch br)
    {
        string a = Site(byId.GetValueOrDefault(br.From)?.Name, br.From);
        string b = Site(byId.GetValueOrDefault(br.To)?.Name, br.To);
        return $"{a}-{b}";
    }

    // ---- frozen brushes (built once) ----
    private static readonly Brush GoodBrush = Palette.Frozen(Palette.FromHex("#2FD27F"));
    private static readonly Brush StaleBrush = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush RedBrush = Palette.Frozen(Palette.FromHex("#FF5B5B"));
    private static readonly Brush AmberBrush = Palette.Frozen(Palette.FromHex("#F3C13A"));
    private static readonly Brush MutedBrush = Palette.Frozen(Palette.FromHex("#8492A0"));

    /// <summary>A cheap, stable scramble so the stale point hops around deterministically per tick.</summary>
    private static int Hash(int x)
    {
        unchecked
        {
            x = (x ^ 61) ^ (x >> 16);
            x *= 9; x ^= x >> 4; x *= 0x27d4eb2d; x ^= x >> 15;
            return x & 0x7fffffff;
        }
    }
}
