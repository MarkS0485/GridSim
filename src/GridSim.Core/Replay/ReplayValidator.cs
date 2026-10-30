using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Frequency;

namespace GridSim.Core.Replay;

/// <summary>How well a no-device replay reproduced the recorded event.</summary>
public enum ReplayVerdict { Reproduced, Marginal, Failed }

/// <summary>Tolerances for the credibility gate (relative on RoCoF, absolute Hz on nadir).</summary>
public sealed record ReplayTolerance(
    double RocofRelTol = 0.15,       // +/-15% on |RoCoF|
    double NadirAbsToleranceHz = 0.15,
    double RmseWarnHz = 0.05,        // per-sample RMSE above this caps the verdict at Marginal
    // N237 (LF4): how far apart (s) the simulated and recorded nadir TIMES may be before the replay says the timing is
    // not reproduced (reporting only; the verdict is unchanged). 2.0 s is the back-test gate's own nadir-time tolerance
    // (Assets.ValidationTolerance.NadirTimeS), so the two never disagree on what "the same instant" means.
    double NadirTimeToleranceS = 2.0)
{
    public static readonly ReplayTolerance Default = new();
}

/// <summary>The outcome of validating an event replay against its recorded headline metrics.</summary>
public sealed record ReplayResult(
    string EventId,
    ReplayVerdict Verdict,
    double RecordedRocofHzPerS, double SimulatedRocofHzPerS, double RocofRelError,
    double RecordedNadirHz, double SimulatedNadirHz, double NadirErrorHz,
    double InferredInertiaMwS, double EventInertiaMwS,
    double? PerSampleRmseHz,
    FrequencyAssessment SimAssessment,
    string Summary,
    FrequencyResponse Simulated,
    // The nadir at the model's own declared-uncertainty corners (ScenarioCorners Best/Worst scale the
    // documented-but-unfitted constants: load damping, responsive fraction, governor lag). The verdict
    // above is NOMINAL-ONLY - the bracket is honest reporting, never a way to pass the gate: a recorded
    // nadir inside [worst, best] says the record is consistent with the model within its own error
    // bars; outside it says something the corners don't cover (e.g. the inertia basis) is wrong.
    double NadirBestCornerHz = 0.0,
    double NadirWorstCornerHz = 0.0,
    // The config the validator ACTUALLY simulated with (recorded services
    // applied, device stripped, window extended) - exporters must declare
    // this one, not a fresh default, or the export contradicts Simulated.
    DynamicsConfig? EffectiveConfig = null,
    // How the attached trace covered the scored window (docs-programme finding: a trace shorter than the window
    // used to be compared against its LAST sample, because the nearest-sample lookup clamps at the end, so a
    // 10-second trace was scored against a 90-second simulation as if frequency had frozen). Simulated samples
    // whose aligned recorded time lies past the trace end are now left out of the RMSE and counted here, and
    // TraceNote says so in words. Zero / null when no trace is attached or the trace covers the whole window.
    int TraceSamplesScored = 0,
    int TraceSamplesPastEnd = 0,
    string? TraceNote = null,
    // D-DOC-035: how InferredInertiaMwS was obtained when the recorded RoCoF is a windowed figure (the window-to-
    // instantaneous correction and its ratio, or why no figure could be inferred: then InferredInertiaMwS is NaN).
    // Null for an instantaneous record, which is inverted directly as before.
    string? InertiaCrossCheckNote = null,
    // D-DOC-037: the back-test acceptance gate (AssetValidation.Compare) against the attached trace, on the simulation
    // clock; null when no trace is attached or the trace does not cover the window. A failed back-test caps Reproduced at
    // Marginal.
    Assets.ValidationReport? BackTest = null,
    // N237 (October 2026, stream LF4): the recorded nadir time (RecordedOutcome.NadirSeconds; null when the record gives
    // none), the simulated one (FrequencyResponse.NadirSeconds, on the same event clock) and, when they are more than
    // ReplayTolerance.NadirTimeToleranceS apart, a plain-English statement that the timing is not reproduced and the
    // verdict rests on the nadir value only. Null note = no recorded time, or the times agree. Reporting only: the
    // verdict does not change (a trace-backed back-test already caps it).
    double? RecordedNadirSeconds = null,
    double SimulatedNadirSeconds = double.NaN,
    string? NadirTimingNote = null)
{
    /// <summary>Whether the recorded nadir lies inside the declared-uncertainty corner envelope.</summary>
    public bool RecordedNadirInEnvelope =>
        RecordedNadirHz >= Math.Min(NadirWorstCornerHz, NadirBestCornerHz) - 1e-9 &&
        RecordedNadirHz <= Math.Max(NadirWorstCornerHz, NadirBestCornerHz) + 1e-9;
}

/// <summary>
/// The credibility gate. Runs <see cref="FrequencyDynamics.Simulate"/> on an event's <b>recorded</b> pre-event
/// inertia/demand and losses+actions <b>with NO device</b>, then checks the simulated RoCoF/nadir against the
/// <see cref="RecordedOutcome"/>. When a 1-second NESO trace is attached it also computes a per-sample RMSE
/// (aligned on the loss instant; the path is data-gated - dormant until a real trace is dropped in). It
/// cross-checks the implied inertia via <see cref="InertiaEstimator"/> and scores with
/// <see cref="FrequencyLimits.Assess"/>. Only a <see cref="ReplayVerdict.Reproduced"/> baseline should unlock a
/// counterfactual: reproduce reality first.
/// </summary>
public static class ReplayValidator
{
    /// <summary>
    /// Seconds a replay or study window runs past the LAST staged event (R176, R194): long enough for the
    /// governor and reserve response to the final step to show its nadir and settle. One value for every place
    /// that extends a window to cover a recorded sequence (this validator, the evidence dossier, the scenario
    /// sweep and the H0 study), so they can never disagree on what "covers the event" means.
    /// </summary>
    public const double PostEventTailSeconds = 30.0;

    /// <summary>
    /// <paramref name="cfg"/> with its duration extended, if needed, to cover every event in
    /// <paramref name="events"/> plus <see cref="PostEventTailSeconds"/>: <c>max(Duration, last + tail)</c>, where
    /// "last" is the LATEST event time (Max, so the list need not be sorted) and 0 for an empty list. Never
    /// shortens a window.
    /// </summary>
    public static DynamicsConfig ExtendToCover(DynamicsConfig cfg, IReadOnlyList<FrequencyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(events);
        double lastAt = events.Count > 0 ? events.Max(e => e.AtSeconds) : 0.0;
        return cfg with { DurationSeconds = Math.Max(cfg.DurationSeconds, lastAt + PostEventTailSeconds) };
    }

    public static ReplayResult Validate(GridEvent ev, DynamicsConfig? config = null, ReplayTolerance? tol = null)
    {
        tol ??= ReplayTolerance.Default;
        // N327 part 2 (CF5): an output tag (Counterfactual, UnvalidatedAtScale) is never a replay input. Every tag the
        // event carries is checked before anything is simulated; an event with none of them replays exactly as before.
        RequireReplayInputs(ev);
        // CF6 (main's ruling on N359): the gate itself accepts a placeholder or synthetic event (a design-basis study input);
        // only output presented as a replay of history refuses one (RequireRecordedReplay, called by replay-event), and every
        // study output inherits the weaker tag (EffectiveProvenance).

        // The baseline reproduces reality, so it must carry NO device - strip any device terms defensively.
        // The window must cover the WHOLE recorded sequence: real events play out over minutes (9 Aug 2019:
        // GT trips at ~60/90 s, LFDD at ~76 s), so a sim clipped at the 30 s default would replay a
        // different event than the record describes.
        IReadOnlyList<FrequencyEvent> events = ev.ToFrequencyEvents();
        DynamicsConfig cfg = (config ?? new DynamicsConfig()) with
        {
            DeviceRotationalInertiaMwS = 0.0,
            FastResponseMw = 0.0,
        };
        // R176: the shared helper takes the Max event time. ToFrequencyEvents() is sorted by time, so this is the
        // same value the old events[^1] gave, bit for bit; the helper simply no longer depends on the sort.
        cfg = ExtendToCover(cfg, events);
        // The device is stripped; the RECORDED response services are kept - they are what the real
        // system held (procurement record), part of the reality the baseline must reproduce.
        cfg = ev.ApplyRecordedServices(cfg);

        FrequencyResponse sim = FrequencyDynamics.Simulate(
            ev.PreEventInertiaMwS, ev.PreEventDemandMw, events, cfg);

        double recRocof = ev.Outcome.InitialRocofHzPerS;
        // Score the sim on the SAME measurement basis as the recorded figure: an instantaneous
        // record compares against the integrator-resolution maximum; a windowed record (e.g. the
        // max 1-second delta off the public 1 Hz trace) compares against the sim's maximum
        // same-window average slope. (RocofOnRecordBasis is the one definition, shared with the evidence dossier.)
        double simRocof = RocofOnRecordBasis(ev.Outcome, sim);
        // GR-802: a recorded RoCoF of (almost) zero gives the relative test no reference. It used to switch to the absolute
        // simulated RoCoF in Hz/s, judged against the same 0.15 tolerance meant for a fraction, so a figure in other units
        // decided the verdict. Every catalogued event records a RoCoF; an event without one is refused, naming it.
        if (!(Math.Abs(recRocof) >= 1e-9))
            throw new InvalidOperationException(
                $"event '{ev.Id}' records an initial RoCoF of {recRocof.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} Hz/s; " +
                "the replay scores the simulated RoCoF relative to the recorded one, so it needs a non-zero recorded figure.");
        double rocofRelError = Math.Abs(Math.Abs(simRocof) - Math.Abs(recRocof)) / Math.Abs(recRocof);

        double nadirError = Math.Abs(sim.NadirHz - ev.Outcome.NadirHz);

        // Cross-check: what inertia does the RECORDED RoCoF imply? The RoCoF is set by the losses inside the
        // measurement window, not a staged sequence's 2-minute total (9 Aug 2019: GT trips at 58/90 s must not
        // count against the initial slope). 5 s covers the near-instant losses plus fast LoM cascades.
        // R287 (B4, October 2026): the rule now lives once on the event (GridEvent.RocofSettingLossMw, window
        // GridEvent.RocofSettingWindowSeconds = 5.0 s), shared with the H=0 study's analytic RoCoF. Same expression,
        // same order, same fallback: bit-identical.
        double rocofSettingLossMw = ev.RocofSettingLossMw();
        // D-DOC-035 (October 2026, docs-session finding): the swing-equation inversion E = f0 |dP| / (2 |RoCoF|) needs the
        // INSTANTANEOUS RoCoF at the loss. A windowed record (gb-2023-12-22: the largest 1-second delta on the 1 Hz public
        // trace, -0.102 Hz/s) averages the fall over a window in which the response services are already delivering, so it
        // is shallower than the instantaneous slope, and inverting it as instantaneous overstated the inertia (245.1 GVA.s
        // against the recorded 161.0). The windowed record is now scaled to an instantaneous equivalent with the replay's
        // OWN ratio of its instantaneous peak to its same-window figure (both from the simulation just run, on the
        // record's window): recInstantaneous = rec x (simInstantaneous / simWindowed). This assumes the true system's
        // window-to-instantaneous ratio is the replay's (first order: the same losses, services and window), which is
        // stated in InertiaCrossCheckNote. An instantaneous record (window 0) is inverted exactly as before, bit for bit.
        double inferredE;
        string? inertiaNote = null;
        if (ev.Outcome.RocofMeasurementWindowS > 0)
        {
            double simInstantaneous = sim.InitialRocofHzPerS;
            if (Math.Abs(simRocof) < 1e-9 || Math.Abs(simInstantaneous) < 1e-9)
            {
                // The replay barely moves on one of the two bases: there is no ratio to correct with. Report no figure
                // rather than a meaningless one.
                inferredE = double.NaN;
                inertiaNote = System.FormattableString.Invariant($"recorded RoCoF is a {ev.Outcome.RocofMeasurementWindowS:0.###} s windowed figure and the replay's ") +
                              "windowed or instantaneous RoCoF is zero, so it cannot be corrected to an instantaneous slope; no inertia inferred";
            }
            else
            {
                double ratio = Math.Abs(simInstantaneous) / Math.Abs(simRocof);
                inferredE = InertiaEstimator.EstimateInertiaMwS(rocofSettingLossMw, recRocof * ratio, cfg.NominalHz);
                inertiaNote = System.FormattableString.Invariant($"recorded RoCoF is a {ev.Outcome.RocofMeasurementWindowS:0.###} s windowed figure; inverted after scaling ") +
                              System.FormattableString.Invariant($"by the replay's instantaneous / windowed ratio {ratio:F3}");
            }
        }
        else
        {
            inferredE = InertiaEstimator.EstimateInertiaMwS(rocofSettingLossMw, recRocof, cfg.NominalHz);
        }

        // Data-gated per-sample RMSE vs the real NESO trace (dormant unless the file is present).
        TraceComparison trace = TryPerSampleRmse(ev, sim, cfg);
        double? rmse = trace.Rmse;

        // The declared-uncertainty envelope: the same no-device replay at the model's Best/Worst
        // corners (Scenarios.ScenarioCorners scales the unfitted constants). Reporting only - the
        // verdict below stays nominal.
        FrequencyResponse best = FrequencyDynamics.Simulate(
            ev.PreEventInertiaMwS, ev.PreEventDemandMw, events, Scenarios.ScenarioCorners.Apply(cfg, Scenarios.Corner.Best));
        FrequencyResponse worst = FrequencyDynamics.Simulate(
            ev.PreEventInertiaMwS, ev.PreEventDemandMw, events, Scenarios.ScenarioCorners.Apply(cfg, Scenarios.Corner.Worst));

        // ---- verdict ----
        // The RoCoF score compares magnitudes, so it must also see the same SIGN: a simulated recovery slope of +0.17
        // against a recorded fall of -0.17 Hz/s scored a 0 % error and "Reproduced". Both non-zero and of opposite sign
        // fails the RoCoF test outright (neither OK nor marginal).
        bool rocofSignAgrees = !(Math.Abs(recRocof) >= 1e-9 && Math.Abs(simRocof) >= 1e-9 && Math.Sign(recRocof) != Math.Sign(simRocof));
        bool rocofOk = rocofSignAgrees && rocofRelError <= tol.RocofRelTol;
        bool nadirOk = nadirError <= tol.NadirAbsToleranceHz;
        bool rocofMarginal = rocofSignAgrees && rocofRelError <= 2 * tol.RocofRelTol;
        bool nadirMarginal = nadirError <= 2 * tol.NadirAbsToleranceHz;

        ReplayVerdict verdict =
            rocofOk && nadirOk ? ReplayVerdict.Reproduced
            : rocofMarginal && nadirMarginal ? ReplayVerdict.Marginal
            : ReplayVerdict.Failed;

        if (verdict == ReplayVerdict.Reproduced && rmse is > 0 && rmse > tol.RmseWarnHz)
            verdict = ReplayVerdict.Marginal; // headline metrics match but the trace shape diverges
        // D-DOC-037 (October 2026, docs-session finding): the back-test acceptance gate (AssetValidation.Compare: nadir
        // 0.05 Hz, RoCoF 0.05 Hz/s on the trace's own basis, nadir time 2.0 s) had no caller. With a trace attached it now
        // runs here, and a back-test failure caps a Reproduced verdict at Marginal, the same rule as the RMSE above (the
        // headline figures match the record, but the recorded trajectory does not). Without a trace nothing changes.
        if (verdict == ReplayVerdict.Reproduced && trace.BackTest is { Passed: false })
            verdict = ReplayVerdict.Marginal;

        FrequencyAssessment assess = FrequencyLimits.Assess(sim);

        // N237 (October 2026, stream LF4; Ruling R1): the headline gate scores the nadir VALUE. gb-2019-08-09 matched it
        // (48.82 vs 48.80 Hz) with the simulated nadir at about 14 s, the model's first arrest, while the recorded 48.8 Hz
        // is the LFDD dip at 75.9 s (the real first arrest was a shallower 49.1 Hz at about 25 s). Reporting that as
        // "Reproduced" without a word presented a value coincidence as a reproduced trajectory. When the record gives the
        // nadir time, the replay now states any timing gap beyond the tolerance, in the summary and in NadirTimingNote.
        // N317 (LF6, October 2026): the 2019 event now carries the recorded response (ESO delivered services and demand
        // effect), and its simulated nadir is the LFDD instant at 76.0 s (48.84 Hz), within the 2.0 s tolerance, so no
        // note is printed for it; the rule stays for any record whose time the model misses.
        string? timingNote = NadirTimingNote(ev.Outcome.NadirSeconds, sim.NadirSeconds, nadirOk, tol.NadirTimeToleranceS);

        // Invariant culture throughout: the summary goes into result files, whose bytes must not depend on the machine.
        string summary =
            System.FormattableString.Invariant($"{verdict}: RoCoF sim {simRocof:+0.000;-0.000} vs rec {recRocof:+0.000;-0.000} Hz/s ") +
            System.FormattableString.Invariant($"({rocofRelError:P0} err); nadir sim {sim.NadirHz:F2} vs rec {ev.Outcome.NadirHz:F2} Hz ") +
            System.FormattableString.Invariant($"({nadirError:F2} Hz err)") + (rmse is { } r ? System.FormattableString.Invariant($"; RMSE {r:F3} Hz") : "; RMSE n/a (no trace)")
            + (trace.BackTest is { } bt ? System.FormattableString.Invariant($"; back-test {(bt.Passed ? "PASS" : "FAIL")} (nadir {bt.NadirErrHz:F3} Hz, RoCoF {bt.RocofErrHzPerS:F3} Hz/s, ") +
                                         System.FormattableString.Invariant($"nadir time {bt.NadirTimeErrS:F1} s)") : "")
            + (trace.Note is { } note ? $" [{note}]" : "")
            + (timingNote is { } tn ? $" [{tn}]" : "");

        return new ReplayResult(
            ev.Id, verdict, recRocof, simRocof, rocofRelError,
            ev.Outcome.NadirHz, sim.NadirHz, nadirError,
            inferredE, ev.PreEventInertiaMwS, rmse, assess, summary, sim,
            NadirBestCornerHz: best.NadirHz, NadirWorstCornerHz: worst.NadirHz,
            EffectiveConfig: cfg,
            TraceSamplesScored: trace.Scored, TraceSamplesPastEnd: trace.PastEnd, TraceNote: trace.Note,
            InertiaCrossCheckNote: inertiaNote, BackTest: trace.BackTest,
            RecordedNadirSeconds: ev.Outcome.NadirSeconds, SimulatedNadirSeconds: sim.NadirSeconds, NadirTimingNote: timingNote);
    }

    /// <summary>
    /// N237: the plain-English statement that the simulated nadir does not happen when the recorded one did, or null when
    /// the record gives no time (<paramref name="recordedNadirSeconds"/> null) or the two times are within
    /// <paramref name="toleranceS"/>. A recorded time that is not a finite number of seconds >= 0 is refused (bad catalog
    /// data must not be reported as a comparison). <paramref name="valueMatched"/> says whether the nadir VALUE passed the
    /// gate, so the note can say the verdict rests on the value alone.
    /// </summary>
    internal static string? NadirTimingNote(double? recordedNadirSeconds, double simulatedNadirSeconds, bool valueMatched, double toleranceS)
    {
        if (recordedNadirSeconds is not { } rec) return null;
        if (!double.IsFinite(rec) || rec < 0.0)
            throw new ArgumentOutOfRangeException(nameof(recordedNadirSeconds), rec,
                "RecordedOutcome.NadirSeconds (when the recorded nadir happened, s on the event clock) must be a finite number >= 0.");
        double gap = Math.Abs(simulatedNadirSeconds - rec);
        if (gap <= toleranceS) return null;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        string where = string.Create(inv, $"simulated nadir at {simulatedNadirSeconds:F1} s, recorded at {rec:F1} s, {gap:F1} s apart (tolerance {toleranceS:F1} s)");
        return valueMatched
            ? $"nadir time NOT reproduced: {where}; the nadir value matches but at a different point of the event, so the verdict rests on the value only"
            : $"nadir time NOT reproduced: {where}";
    }

    /// <summary>
    /// D-DOC-030 (October 2026, docs-session finding): the ONE definition of "the simulated RoCoF, on the recorded
    /// figure's measurement basis". An instantaneous record (<see cref="RecordedOutcome.RocofMeasurementWindowS"/> 0)
    /// compares with the integrator-resolution peak <see cref="FrequencyResponse.InitialRocofHzPerS"/>; a windowed record
    /// compares with the median-over-logger-phase maximum slope on that window (<see cref="MaxWindowedRocof"/>). The
    /// credibility gate scores with it, and every column the evidence dossier sets beside the recorded figure uses it, so
    /// a recorded, simulated and counterfactual RoCoF in one table are always the same quantity. (The dossier used the
    /// instantaneous peak for its simulated and counterfactual columns: gb-2023-12-22 showed -0.155 Hz/s where the gate had
    /// scored -0.123 against the recorded -0.102.)
    /// </summary>
    public static double RocofOnRecordBasis(RecordedOutcome outcome, FrequencyResponse sim)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(sim);
        return outcome.RocofMeasurementWindowS > 0
            ? MaxWindowedRocof(sim, outcome.RocofMeasurementWindowS)
            : sim.InitialRocofHzPerS;
    }

    /// <summary>
    /// Per-sample RMSE between the simulated trajectory and the recorded NESO trace, aligned so the sim's first
    /// loss instant lines up with the sample nearest <see cref="GridEvent.WhenUtc"/>. Returns null when no trace
    /// is attached (the data-gated path). Reading the trace enforces the horizon guard via <see cref="NesoFrequencyCsv"/>.
    /// </summary>
    /// <summary>The outcome of comparing the simulation with an attached trace: the RMSE (null when none could be
    /// computed), how many simulated samples were scored, how many fell past the end of the trace (not scored), and a
    /// plain-English note when the trace did not cover the whole scored window (null when it did).</summary>
    private readonly record struct TraceComparison(double? Rmse, int Scored, int PastEnd, string? Note,
        Assets.ValidationReport? BackTest = null)
    {
        public static readonly TraceComparison None = new(null, 0, 0, null);
    }

    private static TraceComparison TryPerSampleRmse(GridEvent ev, FrequencyResponse sim, DynamicsConfig cfg)
    {
        string? path = ev.Outcome.NesoTraceCsvPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return TraceComparison.None;

        // D-DOC-027: the reader refuses a damaged row (InvalidDataException, naming the line) instead of skipping it, so a
        // trace is never silently shortened; the exception propagates to the caller, which reports an input error.
        // D-DOC-037: the raw samples are kept (for the back-test at the trace's own cadence); upsampling the list gives the
        // same points, in the same order, as upsampling the reader directly, so the RMSE is bit-identical.
        List<FrequencySample> raw = NesoFrequencyCsv.Read(path).ToList();
        // GR-832: the upsampling factor is the integrator steps per second; a step of more than 2 s rounded it to 0.
        int upsample = (int)Math.Round(1.0 / cfg.StepSeconds);
        if (upsample < 1)
            throw new ArgumentOutOfRangeException(nameof(cfg), cfg.StepSeconds,
                "The replay step must be at most 2 s so the 1 s trace can be upsampled to it (at least one step per second).");
        List<FrequencyPoint> recorded = RocofUpsampler
            .Upsample(raw, upsample)
            .ToList();
        // D-DOC-027: an attached trace with no data rows (a header only) used to give "RMSE n/a" with no word of why,
        // the same text as no trace at all. Say so in the note; still no RMSE.
        if (recorded.Count == 0)
            return new TraceComparison(null, 0, 0, $"trace '{Path.GetFileName(path)}' holds no data rows; no RMSE");
        if (sim.Points.Count == 0) return TraceComparison.None;

        // Half a sample of slack at each end of the trace: a time within it is "on" the first or last sample, not
        // outside the trace. (The trace has been upsampled to the simulation step, so a sample is StepSeconds long.)
        double halfStepS = 0.5 * cfg.StepSeconds;
        DateTime traceFirst = recorded[0].Time, traceLast = recorded[^1].Time;

        // The event instant must lie inside the trace. NearestIndex clamps, so an event outside the trace used to
        // anchor on the first or last sample and produce an RMSE against frequency that was never recorded at that
        // time. Now no RMSE is computed and the note says why (fail loud: reported, never silently scored).
        if ((ev.WhenUtc - traceFirst).TotalSeconds < -halfStepS || (ev.WhenUtc - traceLast).TotalSeconds > halfStepS)
            return new TraceComparison(null, 0, 0,
                System.FormattableString.Invariant($"trace {traceFirst:yyyy-MM-dd HH:mm:ss} to {traceLast:yyyy-MM-dd HH:mm:ss} does not contain the event instant ") +
                System.FormattableString.Invariant($"{ev.WhenUtc:yyyy-MM-dd HH:mm:ss}; no RMSE"));

        // Align the recorded loss instant (nearest sample to WhenUtc) to the sim's first event time.
        double firstLossAt = ev.ToFrequencyEvents().FirstOrDefault()?.AtSeconds ?? 0.0;
        DateTime simEventTime = sim.Points[0].Time.AddSeconds(firstLossAt);
        // R286 (October 2026 review, S5): the nearest-sample lookup was a linear scan of the whole upsampled trace for
        // every simulated sample (O(N_sim x N_rec): 7,926 x about 15,000 on gb-2023-12-22). On a trace whose times
        // never decrease (every NESO trace, checked here once) it is a binary search that returns provably the same
        // index (see NearestIndexSorted); an out-of-order trace keeps the linear scan.
        // GR-785: a recorded trace runs strictly forward in time. Times that go backwards or repeat (two files joined
        // with an overlap) are damaged data and used to be scored anyway (the earliest duplicate winning); refused,
        // naming the first offending sample.
        for (int i = 1; i < recorded.Count; i++)
            if (recorded[i].Time <= recorded[i - 1].Time)
                throw new InvalidDataException(
                    $"Recorded trace sample {i} ({recorded[i].Time:O}) is not after sample {i - 1} ({recorded[i - 1].Time:O}); " +
                    "a trace must be strictly increasing in time.");
        bool sortedTrace = IsNonDecreasing(recorded);
        int Nearest(DateTime t) => sortedTrace ? NearestIndexSorted(recorded, t) : NearestIndex(recorded, t);
        int recAnchor = Nearest(ev.WhenUtc);

        double sumSq = 0; int n = 0, pastEnd = 0;
        foreach (FrequencyPoint p in sim.Points)
        {
            double tOff = (p.Time - simEventTime).TotalSeconds;
            if (tOff < 0) continue;
            DateTime recTime = recorded[recAnchor].Time.AddSeconds(tOff);
            // Past the end of the trace there is no recording to compare with. NearestIndex would clamp to the last
            // sample and score the simulation against a frozen frequency; count the sample instead and leave it out.
            // For a trace that covers the whole window this branch is never taken, so the RMSE is bit-identical to
            // the old one (same samples, same order).
            if ((recTime - traceLast).TotalSeconds > halfStepS) { pastEnd++; continue; }
            int j = Nearest(recTime);
            double diff = p.Hz - recorded[j].Hz;
            sumSq += diff * diff; n++;
        }
        string? note = pastEnd == 0 ? null
            : System.FormattableString.Invariant($"trace ends {pastEnd * cfg.StepSeconds:F2} s before the scored window: {pastEnd} sample(s) past its end not scored, ") +
              System.FormattableString.Invariant($"RMSE over the {n} covered sample(s); no back-test");

        // D-DOC-037: the back-test (AssetValidation.Compare) against the raw trace on the simulation clock, aligned on the
        // same anchor sample the RMSE uses (recorded[recAnchor] <-> the first loss). Only when the trace covers the whole
        // window: a nadir looked for in a truncated trace would be a different statistic.
        Assets.ValidationReport? backTest = null;
        if (pastEnd == 0)
        {
            Assets.RecordedFrequencyTrace rt = Assets.RecordedFrequencyTrace.FromSamples(
                raw, recorded[recAnchor].Time, firstLossAt, cfg.DurationSeconds);
            if (rt.Points.Count >= 2) backTest = Assets.AssetValidation.Compare(sim, rt);
        }
        return new TraceComparison(n == 0 ? null : Math.Sqrt(sumSq / n), n, pastEnd, note, backTest);
    }

    /// <summary>
    /// The sim statistic comparable to a "max 1-second delta" read off a <paramref name="windowS"/>-sampled
    /// logger: sample the sim on that grid and take the largest consecutive-sample slope. The logger's
    /// sub-second phase relative to the event is unknown, so this evaluates every phase and returns the
    /// MEDIAN of the per-phase maxima - the unbiased single figure for what a logger of unknown alignment
    /// would have recorded. (A continuous sliding-window max is NOT comparable: it always finds the
    /// worst-aligned window, a systematically steeper read than any real sampled trace can produce.)
    /// </summary>
    internal static double MaxWindowedRocof(FrequencyResponse sim, double windowS)
    {
        IReadOnlyList<FrequencyPoint> pts = sim.Points;
        if (pts.Count < 2) return 0.0;
        double dt = (pts[1].Time - pts[0].Time).TotalSeconds;
        // GR-800: the stride below assumes a uniform grid, taken from the first step. Every step is now checked against
        // it (to 1e-6 of a step or two 100 ns ticks, whichever is larger, which absorbs the rounding of DateTime.AddSeconds
        // to whole ticks), and an irregular trajectory is
        // refused, naming the step, rather than read with a wrong stride. A uniform grid, the only kind the swing
        // integrators produce, gives the same figure as before, bit for bit.
        if (!(dt > 0))
            throw new InvalidOperationException(System.FormattableString.Invariant(
                $"MaxWindowedRocof: the first step of the simulated trajectory is {dt:R} s; the samples must advance in time."));
        for (int i = 2; i < pts.Count; i++)
        {
            double step = (pts[i].Time - pts[i - 1].Time).TotalSeconds;
            if (!(Math.Abs(step - dt) <= Math.Max(1e-6 * dt, 2e-7)))
                throw new InvalidOperationException(System.FormattableString.Invariant(
                    $"MaxWindowedRocof: the simulated trajectory is not on a uniform grid (step {i - 1} to {i} is {step:R} s, the first step {dt:R} s); the windowed RoCoF needs one step size."));
        }
        int stride = Math.Max(1, (int)Math.Round(windowS / dt)); // samples per logger tick
        var perPhase = new List<double>(stride);
        for (int phase = 0; phase < stride; phase++)
        {
            double best = 0.0;
            for (int i = phase; i + stride < pts.Count; i += stride)
            {
                double slope = (pts[i + stride].Hz - pts[i].Hz) / (stride * dt);
                if (Math.Abs(slope) > Math.Abs(best)) best = slope;
            }
            perPhase.Add(best);
        }
        GridSim.Core.Numerics.FrozenIntroSort.Sort(perPhase, (a, b) => Math.Abs(a).CompareTo(Math.Abs(b)));   // GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)
        return perPhase[perPhase.Count / 2];
    }

    /// <summary>The first index whose time is nearest <paramref name="t"/> (the reference linear scan: the earliest of
    /// equally near samples wins, by the strict comparison).</summary>
    internal static int NearestIndex(IReadOnlyList<FrequencyPoint> pts, DateTime t)
    {
        int best = 0; double bestAbs = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            double d = Math.Abs((pts[i].Time - t).TotalSeconds);
            if (d < bestAbs) { bestAbs = d; best = i; }
        }
        return best;
    }

    /// <summary>True when the sample times never decrease (equal neighbours allowed).</summary>
    internal static bool IsNonDecreasing(IReadOnlyList<FrequencyPoint> pts)
    {
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].Time < pts[i - 1].Time) return false;
        return true;
    }

    /// <summary>
    /// <see cref="NearestIndex"/> for samples whose times never decrease, in O(log N) (R286). The same index, by this
    /// argument. Let L be the first sample at or after t (binary search on the times). From L on, the distance
    /// d(i) = |(Time_i - t).TotalSeconds| never decreases with i (the tick difference grows from 0, and the conversion
    /// to double seconds is monotone), so the earliest nearest sample at or after t is L itself. Before L the distance
    /// never increases with i (the difference is negative and shrinks), so the nearest samples before t are a run
    /// ending at L - 1; walking back while the distance (computed by the same expression) stays equal finds the run's
    /// first index. The scan keeps the earliest index of the smallest distance, so it returns the run's first index
    /// when that distance is at most d(L), and L otherwise. That is exactly what is returned here.
    /// </summary>
    internal static int NearestIndexSorted(IReadOnlyList<FrequencyPoint> pts, DateTime t)
    {
        int count = pts.Count;
        if (count == 0) return 0;   // the scan's answer for an empty list
        int lo = 0, hi = count;    // invariant: [0, lo) are before t, [hi, count) are at or after t
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (pts[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        int after = lo;            // first sample at or after t (count when none)
        if (after == 0) return 0;
        int before = after - 1;
        double dBefore = Math.Abs((pts[before].Time - t).TotalSeconds);
        while (before > 0 && Math.Abs((pts[before - 1].Time - t).TotalSeconds) == dBefore) before--;
        if (after == count) return before;
        double dAfter = Math.Abs((pts[after].Time - t).TotalSeconds);
        return dBefore <= dAfter ? before : after;
    }

    /// <summary>
    /// CF6 (main's ruling on N359 / CF6-F3): every provenance tag a replay reads from the event, in a fixed order: the
    /// event's own tag, its recorded outcome, each loss, each recorded action, each response service and the demand effect.
    /// </summary>
    public static IEnumerable<GridSim.Core.Diagnostics.ProvenanceTag> ProvenanceParts(GridEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        yield return ev.Provenance;
        yield return ev.Outcome.Provenance;
        foreach (PowerStep l in ev.Losses) yield return l.Provenance;
        foreach (RecordedAction a in ev.RecordedActions) yield return a.Provenance;
        if (ev.ResponseServices is { } services)
            foreach (ResponseService s in services) yield return s.Provenance;
        if (ev.DemandEffect is { } de) yield return de.Provenance;
    }

    /// <summary>
    /// CF6 (main's ruling on N359 / CF6-F3, Ruling R1): the event's EFFECTIVE provenance, the weakest tag of all its parts
    /// (<see cref="ProvenanceParts"/>, <see cref="GridSim.Core.Diagnostics.ProvenanceTrust.Weakest"/>): a figure computed
    /// from the event can carry no more trust than its weakest input. A Recorded event whose loss steps are placeholders is
    /// therefore effectively CuratedPlaceholder. The built-in recorded events (gb-2019-08-09, gb-2023-12-22) carry Recorded
    /// and DerivedFromRecorded parts, so they are effectively DerivedFromRecorded; the screening event gb-largest-loss-1320
    /// (its own tag and outcome CuratedPlaceholder) is effectively CuratedPlaceholder. An output tag on any part is refused
    /// first by name (<see cref="RequireReplayInputs"/>).
    /// </summary>
    public static GridSim.Core.Diagnostics.ProvenanceTag EffectiveProvenance(GridEvent ev)
    {
        RequireReplayInputs(ev);
        return GridSim.Core.Diagnostics.ProvenanceTrust.Weakest(ProvenanceParts(ev), $"event '{ev.Id}'");
    }

    /// <summary>True when <see cref="EffectiveProvenance"/> is Recorded or DerivedFromRecorded: the event may be presented as
    /// a replay (a reproduction) of what happened.</summary>
    public static bool IsRecordedEvent(GridEvent ev) =>
        EffectiveProvenance(ev) is GridSim.Core.Diagnostics.ProvenanceTag.Recorded or GridSim.Core.Diagnostics.ProvenanceTag.DerivedFromRecorded;

    /// <summary>
    /// CF6 (main's ruling on N359, Ruling R1; narrows CF6's first form of this rule, which refused placeholder events in
    /// EVERY event verb): output presented as a REPLAY, a reproduction of a recorded event (<c>replay-event</c>), needs an
    /// event sourced from a record. An event whose effective provenance (<see cref="EffectiveProvenance"/>, the weakest of
    /// its parts) is not Recorded or DerivedFromRecorded (CuratedPlaceholder, Synthetic or Interpolated, or a Recorded event
    /// with a placeholder part) is refused here by name and weakest part (<see cref="InvalidDataException"/>, exit 1 on
    /// the CLI). The study verbs (scenario, fleet, evidence, h0-study) do NOT call this: a design-basis event such as the
    /// SQSS normal infeed loss is a legitimate study input, and their outputs inherit the weaker tag instead.
    /// <see cref="Validate"/> (the credibility gate those studies share) does not call it either.
    /// </summary>
    public static void RequireRecordedReplay(GridEvent ev)
    {
        GridSim.Core.Diagnostics.ProvenanceTag eff = EffectiveProvenance(ev);
        if (eff is GridSim.Core.Diagnostics.ProvenanceTag.Recorded or GridSim.Core.Diagnostics.ProvenanceTag.DerivedFromRecorded) return;
        string weakestPart = WeakestPartName(ev, eff);
        throw new InvalidDataException(
            $"event '{ev.Id}' ({ev.Title}) is effectively {eff} (its weakest part: {weakestPart}): a replay presents its output as " +
            "a reproduction of a recorded event, so only an event sourced from a record (Recorded or DerivedFromRecorded throughout) " +
            "is replayed (technical ch. 8, trust order). Study it with scenario, fleet, evidence or h0-study instead (their outputs " +
            "carry the weaker tag), or source its figures and re-tag it.");
    }

    // The first part (in ProvenanceParts order) whose tag is the weakest; for a mixed Synthetic / Interpolated event (whose
    // effective tag is CuratedPlaceholder although no part carries it) the first part of rank 2.
    private static string WeakestPartName(GridEvent ev, GridSim.Core.Diagnostics.ProvenanceTag eff)
    {
        int target = GridSim.Core.Diagnostics.ProvenanceTrust.Rank(eff);
        string Name(int index)
        {
            int k = index;
            if (k == 0) return $"the event itself ({ev.Provenance})";
            if (k == 1) return $"the recorded outcome ({ev.Outcome.Provenance})";
            k -= 2;
            if (k < ev.Losses.Count) return $"loss {k} ({ev.Losses[k].What}, {ev.Losses[k].Provenance})";
            k -= ev.Losses.Count;
            if (k < ev.RecordedActions.Count) return $"recorded action {k} ({ev.RecordedActions[k].What}, {ev.RecordedActions[k].Provenance})";
            k -= ev.RecordedActions.Count;
            int sc = ev.ResponseServices?.Count ?? 0;
            if (k < sc) return $"response service {k} ({ev.ResponseServices![k].What}, {ev.ResponseServices[k].Provenance})";
            return $"the demand effect ({ev.DemandEffect!.Provenance})";
        }
        int i = 0, fallback = -1;
        foreach (GridSim.Core.Diagnostics.ProvenanceTag t in ProvenanceParts(ev))
        {
            int r = GridSim.Core.Diagnostics.ProvenanceTrust.Rank(t);
            if (t == eff) return Name(i);
            if (fallback < 0 && r <= target + 1 && r < 3) fallback = i;
            i++;
        }
        return fallback >= 0 ? Name(fallback) + ", mixed with another tag of the same rank" : "unknown";
    }

    /// <summary>
    /// N327 part 2 (CF5): refuse (<see cref="InvalidDataException"/>) an event that carries an
    /// output tag anywhere a replay reads it: the event itself, its recorded outcome, each loss, each recorded action, each
    /// response service and the demand effect. A counterfactual is never an input to another replay (technical ch. 8).
    /// </summary>
    public static void RequireReplayInputs(GridEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(ev.Provenance, $"event '{ev.Id}'");
        GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(ev.Outcome.Provenance, $"event '{ev.Id}' recorded outcome");
        for (int k = 0; k < ev.Losses.Count; k++)
            GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(ev.Losses[k].Provenance, $"event '{ev.Id}' loss {k} ({ev.Losses[k].What})");
        for (int k = 0; k < ev.RecordedActions.Count; k++)
            GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(ev.RecordedActions[k].Provenance, $"event '{ev.Id}' recorded action {k} ({ev.RecordedActions[k].What})");
        if (ev.ResponseServices is { } services)
            for (int k = 0; k < services.Count; k++)
                GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(services[k].Provenance, $"event '{ev.Id}' response service {k} ({services[k].What})");
        if (ev.DemandEffect is { } de)
            GridSim.Core.Diagnostics.ProvenanceTrust.RequireInput(de.Provenance, $"event '{ev.Id}' demand effect");
    }
}
