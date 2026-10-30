using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;

namespace GridSim.Web.Sessions;

/// <summary>
/// Per-session SCADA point register - the web analogue of the WPF <c>ScadaVm</c>. Builds a fixed set of
/// telemetry points once per model (EHV bus voltages, the heaviest corridors' real/reactive flow, OLTC tap
/// positions, and system frequency), then refreshes their live values each frame from the solved result.
/// Stateful: the register is cached against the model shape, and the point set is stable across frames so a
/// client can bind rows once and watch values move.
/// </summary>
public sealed class ScadaService
{
    // EHV telemetry cutoff and heaviest-corridor count now live in
    // GridSim.Core.Presentation.ScadaThresholds - the single source shared with the WPF ScadaVm.

    // A point's identity + how to read its live value from a frame.
    private enum Kind { BusVoltageKv, BranchP, BranchQ, OltcTap, SystemFreq }
    private sealed record Point(string Tag, string Description, string Units, Kind Kind, int Index);

    private List<Point>? _register;
    private string _builtFor = "";

    // Alarm-event log: remember which tags are currently in alarm so we can log onsets (rising edges).
    private readonly HashSet<string> _inAlarm = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inWatch = new(StringComparer.Ordinal);
    private readonly List<ScadaEventDto> _events = new();

    public void Reset() { _inAlarm.Clear(); _inWatch.Clear(); _events.Clear(); }

    public ScadaDto Project(SimFrame f)
    {
        EnsureRegister(f.Model, f.Result);
        PowerFlowResult r = f.Result;

        var pts = new List<ScadaPointDto>(_register!.Count);
        int good = 0, alarms = 0;

        // Bus id -> solved index, for voltage points.
        var idIndex = new Dictionary<int, int>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) idIndex[r.BusIds[i]] = i;

        // Every web point is refreshed on every frame (no STALE state), so each reading's stamp is this frame's clock.
        string stamp = FrameProjector.FormatClock(f.Hour);

        foreach (Point p in _register)
        {
            double value = 0, loadPct = 0;
            bool alarm = false;
            string quality = "GOOD";

            switch (p.Kind)
            {
                case Kind.BusVoltageKv:
                    Bus b = f.Model.Buses[p.Index];
                    int si = idIndex.GetValueOrDefault(b.Id, -1);
                    double vm = si >= 0 && si < r.Vm.Length ? r.Vm[si] : 1.0;
                    value = vm * b.BaseKv;
                    alarm = vm < ScadaThresholds.VoltageBandLowPu || vm > ScadaThresholds.VoltageBandHighPu;   // outside a generous operational band
                    break;
                case Kind.BranchP:
                case Kind.BranchQ:
                    if (p.Index < r.LineFlows.Count)
                    {
                        LineFlow lf = r.LineFlows[p.Index];
                        value = p.Kind == Kind.BranchP ? lf.PFromMw : lf.QFromMvar;
                        loadPct = lf.RatingMva > 0 ? lf.LoadingPercent : Math.Min(100, Math.Abs(lf.PFromMw) / PresentationLimits.UnratedBranchNominalMw * 100);
                        // Shared ScadaThresholds: P2 alarm above 90%, P3 watch above 70%
                        // (the old >=100% only alarmed when the corridor was ALREADY overloaded).
                        alarm = loadPct > ScadaThresholds.CorridorAlarmPct;
                        if (p.Kind == Kind.BranchP)
                        {
                            bool watch = loadPct > ScadaThresholds.CorridorWatchPct && !alarm;
                            bool wasWatch = _inWatch.Contains(p.Tag);
                            if (watch && !wasWatch)
                            {
                                _inWatch.Add(p.Tag);
                                LogEvent(FrameProjector.FormatClock(f.Hour), ScadaThresholds.PriorityInformation, p.Tag, $"corridor loading {loadPct:F0}% - watch");
                            }
                            else if (!watch && wasWatch) _inWatch.Remove(p.Tag);
                        }
                    }
                    break;
                case Kind.OltcTap:
                    value = p.Index < f.Model.Branches.Count ? f.Model.Branches[p.Index].TapRatio : 1.0;
                    break;
                case Kind.SystemFreq:
                    value = f.FrequencyHz;
                    // Normal band 49.9-50.1 (shared ScadaThresholds P2; R139 / R149: FrequencyLimits.NormalBand*, not the
                    // +/-0.2 Hz operational band), not the wider statutory band. N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04):
                    // the band about the frame's own nominal (the constants themselves at 50 Hz).
                    alarm = ScadaThresholds.IsFrequencyAlarm(f.FrequencyHz, f.Model.FrequencyHz);
                    break;
            }

            if (alarm) { quality = "ALARM"; alarms++; } else good++;

            // Log rising/falling edges into the alarm event log.
            bool wasAlarmed = _inAlarm.Contains(p.Tag);
            if (alarm && !wasAlarmed)
            {
                _inAlarm.Add(p.Tag);
                LogEvent(FrameProjector.FormatClock(f.Hour), PriorityOf(p.Kind), p.Tag, $"{p.Description} entered alarm ({value:0.00} {p.Units})");
            }
            else if (!alarm && wasAlarmed)
            {
                _inAlarm.Remove(p.Tag);
                LogEvent(FrameProjector.FormatClock(f.Hour), ScadaThresholds.PriorityInformation, p.Tag, $"{p.Description} cleared");
            }

            (ScadaTarget target, int targetKey) = TargetOf(p.Kind, p.Index, f.Model);
            pts.Add(new ScadaPointDto(p.Tag, p.Description, p.Units, value, quality, loadPct, alarm, stamp, target, targetKey));
        }

        // Snapshot (copy) the event log - AsReadOnly would expose the live list the next frame mutates,
        // so a serializer enumerating it during an alarm onset throws "collection was modified".
        return new ScadaDto(pts.Count, good, alarms, pts, _events.ToList());
    }

    /// <summary>The asset page a point opens (R099 click-through): a busbar voltage opens its substation (keyed by
    /// bus id), a corridor P/Q point its circuit and an OLTC tap its transformer (both keyed by model branch index,
    /// the same index the asset graph uses). System frequency has no asset page. An index outside the model gives
    /// None rather than a route to a page that cannot exist.</summary>
    private static (ScadaTarget Target, int Key) TargetOf(Kind kind, int index, GridModel m) => kind switch
    {
        Kind.BusVoltageKv when index >= 0 && index < m.Buses.Count => (ScadaTarget.Substation, m.Buses[index].Id),
        Kind.BranchP or Kind.BranchQ when index >= 0 && index < m.Branches.Count => (ScadaTarget.Circuit, index),
        Kind.OltcTap when index >= 0 && index < m.Branches.Count => (ScadaTarget.Transformer, index),
        _ => (ScadaTarget.None, -1),
    };

    private static string PriorityOf(Kind k) => k switch
    {
        Kind.SystemFreq => ScadaThresholds.PriorityFrequencyOutOfBand,   // D-DOC-147: P2 on both consoles (was P1 here)
        Kind.BusVoltageKv => ScadaThresholds.PriorityVoltageOutOfBand,
        Kind.BranchP or Kind.BranchQ => ScadaThresholds.PriorityCorridorAlarm,
        _ => ScadaThresholds.PriorityInformation,
    };

    private void LogEvent(string stamp, string priority, string tag, string message)
    {
        _events.Insert(0, new ScadaEventDto(stamp, priority, tag, message));   // newest first
        if (_events.Count > 60) _events.RemoveAt(_events.Count - 1);
    }

    private void EnsureRegister(GridModel m, PowerFlowResult r)
    {
        string key = $"{m.Name}|{m.BusCount}|{m.Branches.Count}";
        if (_register is not null && _builtFor == key) return;

        var reg = new List<Point>();

        // System frequency.
        reg.Add(new Point("SYS.FREQ", "System frequency", "Hz", Kind.SystemFreq, 0));

        // EHV bus voltages.
        for (int i = 0; i < m.Buses.Count; i++)
        {
            Bus b = m.Buses[i];
            if (b.BaseKv < ScadaThresholds.EhvCutoffKv) continue;
            string site = Clean(b.Name) ?? $"BUS{b.Id}";
            reg.Add(new Point($"{site}.V", $"{site} {b.BaseKv:0} kV busbar voltage", "kV", Kind.BusVoltageKv, i));
        }

        // The heaviest corridors' P and Q - by SOLVED loading on the first frame,
        // excluding transformers (they carry taps instead), matching ScadaVm.
        // Static-rating selection surfaced unloaded big transformers instead of
        // the corridors that are actually working.
        var heaviest = Enumerable.Range(0, m.Branches.Count)
            .Where(i => !m.Branches[i].IsTransformer)
            .OrderByDescending(i => i < r.LineFlows.Count ? r.LineFlows[i].LoadingPercent : 0.0)
            .Take(ScadaThresholds.TopCorridors);
        foreach (int i in heaviest)
        {
            Branch br = m.Branches[i];
            string name = Clean(br.Name) ?? $"{br.From}-{br.To}";
            reg.Add(new Point($"{name}.P", $"{name} real power", "MW", Kind.BranchP, i));
            reg.Add(new Point($"{name}.Q", $"{name} reactive power", "MVAr", Kind.BranchQ, i));
        }

        // OLTC tap positions (by transformer branch index).
        foreach (Oltc o in m.Oltcs)
        {
            int idx = -1;
            for (int i = 0; i < m.Branches.Count; i++)
                if (string.Equals(m.Branches[i].Name, o.Transformer, StringComparison.Ordinal)) { idx = i; break; }
            if (idx >= 0)
            {
                string name = Clean(o.Transformer) ?? $"TX{idx}";
                reg.Add(new Point($"{name}.TAP", $"{name} tap ratio", "pu", Kind.OltcTap, idx));
            }
        }

        _register = reg;
        _builtFor = key;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().Replace(' ', '_').ToUpperInvariant();
    }
}
