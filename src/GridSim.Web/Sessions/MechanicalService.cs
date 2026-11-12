using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Visuals;

namespace GridSim.Web.Sessions;

/// <summary>
/// Per-session MECHANICAL projection. Stateful because per-machine <b>ramp</b> is a frame-to-frame delta
/// (needs the previous MW), so it can't be a pure static projection. Mirrors the WPF
/// <c>MachineEngineerVm</c>: risk-sorted synchronous machines with loading / inertia / ramp, and the dual
/// RoCoF-relay margin (legacy 0.125 vs modern 1.0 Hz/s). Reactive/thermal/torsional detail the presolved
/// feed doesn't carry is left out rather than fabricated.
/// </summary>
public sealed class MechanicalService
{
    // Per-unit previous output, keyed by generator INDEX (checked against bus + name), not by name: gb-full names
    // are not unique, and name keying diffed one "- chp" unit against another every frame (R054).
    private readonly UnitRampHistory _history = new();
    private double _dtMinutes;

    // F-047: a timestamped (bridge) feed has no fixed step. The server decides how far apart its ticks are (a
    // stored lake steps one settlement period, a raw feed one second, a seek or a loop restart jumps anywhere), so
    // the divisor is taken from the gap between the ticks' own UTC instants, never from a constructor default.
    private readonly bool _timestamped;
    private DateTime? _lastTickUtc;

    /// <summary>A fixed-step service (live and replay sessions). The step must be a finite, positive number of
    /// minutes: F-047 removed the silent 1.0 minute fallback, which gave every bridge session a made-up divisor.</summary>
    public MechanicalService(double dtMinutes)
    {
        if (!double.IsFinite(dtMinutes) || dtMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(dtMinutes), dtMinutes,
                "Step must be a finite, positive number of minutes. A timestamped feed uses ForTimestampedFeed().");
        _dtMinutes = dtMinutes;
    }

    private MechanicalService()
    {
        _timestamped = true;
        _dtMinutes = double.NaN;   // unknown until two increasing ticks have been seen
    }

    /// <summary>F-047: a service for a feed whose frames carry their own UTC instant (the GDA bridge). Each frame is
    /// projected with <see cref="ProjectAtTick"/>, and the ramp divides by the real minutes since the previous tick.</summary>
    public static MechanicalService ForTimestampedFeed() => new();

    /// <summary>True for a <see cref="ForTimestampedFeed"/> service.</summary>
    public bool IsTimestamped => _timestamped;

    /// <summary>The frame-to-frame interval (minutes) the MW/min ramp divides by. On a timestamped feed this is the
    /// gap between the last two increasing ticks, and NaN until there have been two (no ramp can be shown yet).</summary>
    public double StepMinutes => _dtMinutes;

    /// <summary>
    /// Follow a change of the live sim's step (the transport STEP picker, via <c>SimSession.SetStepMinutes</c>).
    /// The NEXT frame is integrated over the new step, so the next ramp (Pg now minus Pg at the previous frame)
    /// spans exactly <paramref name="minutes"/>; the history is therefore kept, not reset. Before this the divisor
    /// stayed at the construction-time step (R067): after switching 20 min -> 1 s every MW/min ramp was
    /// understated 1,200 times. A non-finite or non-positive step is a caller bug and throws.
    /// </summary>
    public void SetStepMinutes(double minutes)
    {
        if (_timestamped)
            throw new InvalidOperationException(
                "A timestamped feed takes its step from the tick instants; it cannot be set by hand.");
        if (!double.IsFinite(minutes) || minutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Step must be a finite, positive number of minutes.");
        _dtMinutes = minutes;
    }

    /// <summary>Drop the ramp history - call on a replay seek / timeline discontinuity.</summary>
    public void Reset() => _history.Reset();

    /// <summary>
    /// F-047: project a frame from a timestamped feed. <paramref name="tickUtc"/> is the frame's own UTC instant
    /// (the bridge client refuses an undated state frame, so a missing one here is a wiring bug and throws).
    /// <list type="bullet">
    /// <item>First tick: recorded; no previous output exists, so every ramp is 0 (as on any first frame).</item>
    /// <item>A later tick: the divisor becomes the exact minutes since the previous tick, so a 30-minute lake tick
    /// divides by 30 and a 1-second raw tick by 1/60.</item>
    /// <item>A tick at or before the previous one (a seek back, a loop restart, a repeat): a timeline
    /// discontinuity, not a ramp. The history is dropped and the step goes back to unknown, as on a replay seek.</item>
    /// </list>
    /// </summary>
    public MechanicalDto ProjectAtTick(SimFrame f, DateTime? tickUtc)
    {
        if (!_timestamped)
            throw new InvalidOperationException("ProjectAtTick is only for a ForTimestampedFeed service; use Project.");
        DateTime at = tickUtc is DateTime t && t != DateTime.MinValue
            ? t
            : throw new InvalidOperationException(
                "A bridge frame reached the MECHANICAL projection without its tick instant, so its ramp interval is unknown.");
        if (_lastTickUtc is DateTime prev && at > prev)
        {
            _dtMinutes = (at - prev).TotalMinutes;   // exact tick gap; a TimeSpan of whole ticks, so > 0 here
        }
        else
        {
            // First tick, or a discontinuity: nothing to diff against.
            _history.Reset();
            _dtMinutes = double.NaN;
        }
        _lastTickUtc = at;
        return ProjectCore(f);
    }

    /// <summary>Project a frame of a fixed-step feed (live or replay).</summary>
    public MechanicalDto Project(SimFrame f)
    {
        if (_timestamped)
            throw new InvalidOperationException(
                "A timestamped feed must be projected with ProjectAtTick, so its ramp divides by the real tick gap.");
        return ProjectCore(f);
    }

    private MechanicalDto ProjectCore(SimFrame f)
    {
        var rows = new List<MachineRowDto>();
        double sumInertia = 0;

        IReadOnlyList<Generator> gens = f.Model.Generators;
        for (int gi = 0; gi < gens.Count; gi++)
        {
            Generator g = gens[gi];
            FuelInfo fi = Fuels.Classify(g.Type);
            if (!fi.Synchronous || g.Pg < MechanicalThresholds.MinSyncMachineMw) continue;

            string key = g.Name ?? $"{fi.Name}@{g.Bus}";
            double pmax = g.Pmax is > 0 and < double.PositiveInfinity ? g.Pmax : Math.Max(g.Pg, g.MBase);
            double loading = pmax > 0 ? g.Pg / pmax * 100.0 : 0;

            // P-Q capability limits for the protection-envelope diagram: real when the model carries
            // finite reactive limits; fall back to +/-Pg (a unit-PF square) when infinite, mirroring the
            // WPF MachineEngineerVm so PqDiagram never receives +/-Infinity. (Qg itself is 0 in the
            // presolved feed - reactive DISPATCH isn't carried - so the operating dot sits on Q=0.)
            double qminLim = double.IsInfinity(g.Qmin) ? -g.Pg : g.Qmin;
            double qmaxLim = double.IsInfinity(g.Qmax) ? g.Pg : g.Qmax;
            double inertia = g.H * g.MBase / 1000.0;
            sumInertia += inertia;

            // Ramp against THIS unit's own previous output (index identity), 0 when it has no history yet.
            double ramp = 0;
            if (_history.Exchange(gi, g) is double prev)
            {
                // F-047: a ramp is only ever divided by a known, positive step. On a timestamped feed the history is
                // dropped whenever the step is unknown, so reaching here without one is a bug and throws.
                if (!double.IsFinite(_dtMinutes) || _dtMinutes <= 0)
                    throw new InvalidOperationException(
                        $"MECHANICAL ramp has a previous output but no valid step ({_dtMinutes} min).");
                ramp = (g.Pg - prev) / _dtMinutes;
            }

            // Stress = worst of real-power loading and reactive-headroom proximity (when limits are finite).
            double stress = pmax > 0 ? g.Pg / pmax : 0;
            if (g.Qmax is > 0 and < double.PositiveInfinity && g.Qg > 0) stress = Math.Max(stress, g.Qg / g.Qmax);

            rows.Add(new MachineRowDto(
                key, fi.Name, fi.Color,
                g.Pg, pmax, loading,
                g.Qg, qminLim, qmaxLim,
                inertia, g.H,
                ramp, Math.Clamp(stress, 0, 1) * 100.0, fi.Synchronous));
        }

        rows.Sort((a, b) => b.StressPercent.CompareTo(a.StressPercent));   // risk-sorted

        double rocof = Math.Abs(f.RocofHzPerS);
        double legacy = FrequencyLimits.RocofLimitLegacyHzPerS;
        double modern = FrequencyLimits.RocofLimitModernHzPerS;

        return new MechanicalDto(
            f.RocofHzPerS,
            (legacy - rocof) / legacy * 100.0, rocof > legacy,
            (modern - rocof) / modern * 100.0, rocof > modern,
            sumInertia, rows.Count, rows);
    }
}
