namespace GridSim.Core.Simulation;

// F stream (R095 rest, Ruling R1): internal, no user until the hybrid SCADA + PMU estimator below exists; kept, tested.
/// <summary>One scheduled estimator update at an instant, tagged SCADA (the slow robust WLS) or PMU (the fast
/// linear estimator).</summary>
/// <param name="Time">The update instant.</param>
/// <param name="IsScada">True for a SCADA-rate robust-WLS update; false for a PMU-rate linear update.</param>
internal sealed record ScheduledUpdate(DateTime Time, bool IsScada);

// F stream (R095 rest, October 2026; Ruling R1): INTERNAL. A schedule with no estimator to drive has no user, so it is not
// public Core API; it is kept (with its tests, through InternalsVisibleTo) for the hybrid estimator described below.
/// <summary>
/// EXPERIMENTAL (R095). The hybrid multi-rate scheduler for a SCADA + PMU state estimator. SCADA telemetry
/// arrives slowly (~5 s) and PMU synchrophasors fast (~0.5 s), so the intended estimator runs a robust nonlinear
/// WLS at each SCADA instant and a fast linear PMU estimate at each PMU instant between them (about nine linear
/// updates per SCADA cycle). This interleaves the two rates onto one ordered schedule - SCADA wins a coincident
/// instant. Deterministic (derived from the periods, no wall-clock read).
///
/// <para>Status: the schedule itself is tested (<c>HybridMultiRateSchedulerTests</c>), but NOTHING drives it yet:
/// no engine, CLI verb or front end calls <see cref="Schedule"/> (this summary used to say "which the resident
/// engine drives"; <see cref="RealtimeEstimatorEngine"/> does not use it). It is a SCADA/PMU estimator utility, not
/// part of the frequency / inertia dynamics. To leave experimental it needs: (1) a caller that runs the robust WLS
/// at each SCADA update and <see cref="Estimation.LinearPmuStateEstimator"/> at each PMU update, seeded from the
/// last SCADA state; (2) the one PMU path prerequisite still open (it was listed in a "technical chapter 18.6" that no
/// longer exists, D-DOC-155; the mapper is described in technical ch. 33, C37.118 synchrophasor ingest), the
/// mapper-to-estimator adapter (the C37.118 decoder fixes R046 / R293 / N061 and the angle re-referencing to the
/// estimator's slack R295 are done; D-DOC-219 corrected this list); (3) an end-to-end test of the
/// interleaved run on a replayed stream, including the no-future invariant (no update reads telemetry stamped after
/// its instant); (4) a CLI or resident-engine surface. Also unhandled today: an <c>end</c> before
/// <c>start</c> silently gives an empty schedule, and mixed <see cref="DateTimeKind"/> values are not
/// checked.</para>
/// </summary>
internal static class HybridMultiRateScheduler
{
    /// <summary>The ordered interleaved schedule of SCADA and PMU updates over [start, end]. At a coincident
    /// instant only the SCADA update is emitted (it supersedes the linear step).</summary>
    public static IReadOnlyList<ScheduledUpdate> Schedule(DateTime start, DateTime end, TimeSpan scadaPeriod, TimeSpan pmuPeriod)
    {
        if (scadaPeriod <= TimeSpan.Zero || pmuPeriod <= TimeSpan.Zero)
            throw new ArgumentException("periods must be positive.");

        var scada = new HashSet<long>();
        for (DateTime t = start; t <= end; t += scadaPeriod) scada.Add(t.Ticks);

        var byTick = new SortedDictionary<long, bool>(); // tick -> isScada
        foreach (long tick in scada) byTick[tick] = true;
        for (DateTime t = start; t <= end; t += pmuPeriod)
            if (!byTick.ContainsKey(t.Ticks)) byTick[t.Ticks] = false; // PMU only where no SCADA

        var schedule = new List<ScheduledUpdate>(byTick.Count);
        foreach (KeyValuePair<long, bool> kv in byTick)
            schedule.Add(new ScheduledUpdate(new DateTime(kv.Key, start.Kind), kv.Value));
        return schedule;
    }
}
