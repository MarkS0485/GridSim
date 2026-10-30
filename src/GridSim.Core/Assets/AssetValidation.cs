using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Frequency;
using GridSim.Core.Replay;

namespace GridSim.Core.Assets;

/// <summary>A recorded frequency trace (e.g. NESO 1 s / Gridradar 100 ms) to back-test a simulation against.</summary>
/// <remarks>Times are seconds on the SIMULATION's clock (t = 0 at the start of the simulated window), so the recorded
/// nadir time and the simulated nadir time can be compared directly. <see cref="FromSamples"/> builds one from a
/// recorded trace aligned on the event instant.</remarks>
public sealed record RecordedFrequencyTrace(IReadOnlyList<(double TimeS, double Hz)> Points)
{
    public double NadirHz => Points.Count == 0 ? double.NaN : Points.Min(p => p.Hz);
    public double NadirTimeS => Points.Count == 0 ? double.NaN : Points.OrderBy(p => p.Hz).First().TimeS;

    /// <summary>Peak |RoCoF| from first differences (Hz/s) - the measured initial fall, at the trace's own cadence.</summary>
    public double PeakRocofHzPerS()
    {
        double peak = 0.0;
        for (int i = 1; i < Points.Count; i++)
        {
            double dt = Points[i].TimeS - Points[i - 1].TimeS;
            if (dt <= 0) continue;
            double r = (Points[i].Hz - Points[i - 1].Hz) / dt;
            if (Math.Abs(r) > Math.Abs(peak)) peak = r;
        }
        return peak;
    }

    /// <summary>
    /// D-DOC-037: the trace's sampling interval, the median of its positive consecutive gaps (s); 0 when there are fewer
    /// than two points. This is the window its first-difference RoCoF is measured over (1 s for a NESO trace).
    /// </summary>
    public double SampleIntervalS()
    {
        var gaps = new List<double>(Math.Max(0, Points.Count - 1));
        for (int i = 1; i < Points.Count; i++)
        {
            double dt = Points[i].TimeS - Points[i - 1].TimeS;
            if (dt > 0) gaps.Add(dt);
        }
        if (gaps.Count == 0) return 0.0;
        gaps.Sort();   // plain doubles, no ties of different values: the order is total and the median well defined
        return gaps[gaps.Count / 2];
    }

    /// <summary>
    /// D-DOC-037: a recorded trace on the simulation clock. A sample at time <c>t</c> lands at
    /// <c>(t - eventInstantUtc) + firstLossAtS</c> seconds, so the recorded event instant lines up with the simulation's
    /// first loss (the alignment the per-sample RMSE uses), and only samples inside <c>[0, windowS]</c> are kept, so the
    /// recorded nadir is looked for over the same window the simulation covers.
    /// </summary>
    public static RecordedFrequencyTrace FromSamples(
        IEnumerable<GridSim.Core.Frequency.FrequencySample> samples, DateTime eventInstantUtc, double firstLossAtS, double windowS)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var pts = new List<(double, double)>();
        foreach (GridSim.Core.Frequency.FrequencySample s in samples)
        {
            double tS = (s.Time - eventInstantUtc).TotalSeconds + firstLossAtS;
            if (tS >= 0.0 && tS <= windowS) pts.Add((tS, s.Hz));
        }
        return new RecordedFrequencyTrace(pts);
    }
}

/// <summary>Acceptance tolerances for a validation back-test.</summary>
public sealed record ValidationTolerance(double NadirHz = 0.05, double RocofHzPerS = 0.05, double NadirTimeS = 2.0)
{
    public static ValidationTolerance Default { get; } = new();
}

/// <summary>The residuals of a back-test and whether it passes the acceptance gate.</summary>
/// <remarks>D-DOC-037: <see cref="SimRocofHzPerS"/> and <see cref="RecordedRocofHzPerS"/> are the two figures compared,
/// on the same measurement basis; <see cref="SimNadirTimeS"/> and <see cref="RecordedNadirTimeS"/> the two nadir times
/// on the simulation clock. Zero when not recorded (the old three-residual form).</remarks>
public sealed record ValidationReport(
    bool Passed, double NadirErrHz, double RocofErrHzPerS, double NadirTimeErrS, string Note,
    double SimRocofHzPerS = 0.0, double RecordedRocofHzPerS = 0.0,
    double SimNadirTimeS = 0.0, double RecordedNadirTimeS = 0.0);

/// <summary>
/// The parameter set frozen at the ANCHOR event of a blind-prediction protocol (D-DOC-037): every model constant shared
/// across events. A blind prediction of a second event must use exactly these values; <see cref="AssetValidation.BlindPredict"/>
/// refuses anything else. The per-event inputs are not part of it, because they come from the event's own record
/// (<see cref="GridEvent.ApplyRecordedServices"/>): the response services held, the pre-event frequency, the recorded
/// demand effect where the record gives one (N317, LF6 October 2026: it sets the event's DampingPctPerHz, so a frozen
/// damping is compared on the shared set, before the event's record is applied), and the window length (extended to
/// cover the recorded sequence).
/// </summary>
public sealed record FrozenParameterSet(string AnchorEventId, DynamicsConfig Shared)
{
    /// <summary>Freeze <paramref name="shared"/> as the constants calibrated (or chosen) on <paramref name="anchorEventId"/>.</summary>
    public static FrozenParameterSet Freeze(string anchorEventId, DynamicsConfig shared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorEventId);
        ArgumentNullException.ThrowIfNull(shared);
        return new FrozenParameterSet(anchorEventId, shared);
    }

    /// <summary>
    /// The frozen constants that <paramref name="candidate"/> changes, as "name (frozen -> candidate)" lines; empty when
    /// none does. Exact comparison (a blind prediction may not re-tune by any amount). Not compared: DurationSeconds,
    /// SystemResponseServices and InitialFrequencyHz (per-event inputs from the record, see the type comment).
    /// </summary>
    public IReadOnlyList<string> Differences(DynamicsConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var d = new List<string>();
        void Num(string name, double frozen, double now) { if (!frozen.Equals(now)) d.Add($"{name} ({frozen:R} -> {now:R})"); }
        void Same<T>(string name, T frozen, T now) { if (!EqualityComparer<T>.Default.Equals(frozen, now)) d.Add($"{name} ({frozen} -> {now})"); }
        DynamicsConfig f = Shared;
        Num(nameof(DynamicsConfig.NominalHz), f.NominalHz, candidate.NominalHz);
        Num(nameof(DynamicsConfig.DroopPu), f.DroopPu, candidate.DroopPu);
        Num(nameof(DynamicsConfig.DampingPctPerHz), f.DampingPctPerHz, candidate.DampingPctPerHz);
        Num(nameof(DynamicsConfig.ResponsiveFraction), f.ResponsiveFraction, candidate.ResponsiveFraction);
        Num(nameof(DynamicsConfig.GovernorTimeConstantS), f.GovernorTimeConstantS, candidate.GovernorTimeConstantS);
        Num(nameof(DynamicsConfig.StepSeconds), f.StepSeconds, candidate.StepSeconds);
        Num(nameof(DynamicsConfig.DeviceRotationalInertiaMwS), f.DeviceRotationalInertiaMwS, candidate.DeviceRotationalInertiaMwS);
        Num(nameof(DynamicsConfig.FastResponseMw), f.FastResponseMw, candidate.FastResponseMw);
        Num(nameof(DynamicsConfig.FastResponseTimeConstantS), f.FastResponseTimeConstantS, candidate.FastResponseTimeConstantS);
        Num(nameof(DynamicsConfig.FastResponseDeadbandHz), f.FastResponseDeadbandHz, candidate.FastResponseDeadbandHz);
        Num(nameof(DynamicsConfig.VirtualInertiaMwS), f.VirtualInertiaMwS, candidate.VirtualInertiaMwS);
        Num(nameof(DynamicsConfig.FormingDampingMwPerHz), f.FormingDampingMwPerHz, candidate.FormingDampingMwPerHz);
        Num(nameof(DynamicsConfig.FormingSetpointMw), f.FormingSetpointMw, candidate.FormingSetpointMw);
        Same(nameof(DynamicsConfig.Forming), f.Forming, candidate.Forming);
        Same(nameof(DynamicsConfig.InertiaModel), f.InertiaModel, candidate.InertiaModel);
        Same(nameof(DynamicsConfig.Distributed), f.Distributed, candidate.Distributed);
        Same(nameof(DynamicsConfig.EnableAssetDynamics), f.EnableAssetDynamics, candidate.EnableAssetDynamics);
        Same(nameof(DynamicsConfig.EnablePassivePlant), f.EnablePassivePlant, candidate.EnablePassivePlant);
        return d;
    }
}

/// <summary>
/// Back-tests a frequency simulation against a recorded event: the acceptance gate that output must pass before it is
/// called evidence. The gate compares three things with a recorded trace: the nadir (Hz), the initial RoCoF (Hz/s, on
/// the trace's own measurement basis) and the time of the nadir (s), each within <see cref="ValidationTolerance"/>
/// (0.05 Hz, 0.05 Hz/s, 2.0 s by default).
///
/// <para>A credible protocol ANCHORS on one event and then BLIND-PREDICTS a second event with the parameter set frozen at
/// the anchor; only the blind prediction counts as evidence. <see cref="BlindPredict"/> enforces the freeze: it refuses a
/// prediction made with any constant changed since the anchor (<see cref="FrozenParameterSet.Differences"/>), and a
/// "blind" prediction of the anchor event itself.</para>
///
/// <para>Where it is applied (D-DOC-037, October 2026: it had no caller outside the tests): the credibility gate
/// (<see cref="ReplayValidator.Validate"/>) runs <see cref="Compare"/> whenever a recorded trace is attached, reports it
/// as <see cref="ReplayResult.BackTest"/>, and caps a Reproduced verdict at Marginal when the back-test fails, the same
/// rule the per-sample RMSE applies.</para>
///
/// <para>SPATIAL (per-node) validation against the four-node Gridradar PMU data (London, Buckingham, Manchester,
/// Strathclyde) - the vector-shift path and the local-vs-COI RoCoF spread - needs the distributed model to carry the
/// asset fleet, and stays UnvalidatedAtScale until that integration and the PMU comparison exist.</para>
/// </summary>
public static class AssetValidation
{
    /// <summary>
    /// Compare a simulated response to a recorded trace and apply the acceptance gate.
    /// D-DOC-037: the RoCoF is compared on ONE basis. The recorded figure is the largest first difference at the trace's
    /// cadence (1 s for NESO), so the simulation is read on that same window (<see cref="ReplayValidator.MaxWindowedRocof"/>,
    /// the definition the credibility gate uses) whenever the trace is coarser than the simulation step. A trace sampled
    /// at (or finer than) the simulation step is compared with the instantaneous peak, exactly as before.
    /// </summary>
    public static ValidationReport Compare(FrequencyResponse sim, RecordedFrequencyTrace recorded, ValidationTolerance? tol = null)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(recorded);
        tol ??= ValidationTolerance.Default;
        double simStepS = sim.Points.Count >= 2 ? (sim.Points[1].Time - sim.Points[0].Time).TotalSeconds : 0.0;
        double cadenceS = recorded.SampleIntervalS();
        // "Coarser" means by more than half a step, so a trace sampled AT the simulation step (whose gaps differ from the
        // step by an ulp after subtraction) keeps the instantaneous comparison.
        double simRocof = simStepS > 0 && cadenceS > 1.5 * simStepS
            ? ReplayValidator.MaxWindowedRocof(sim, cadenceS)
            : sim.InitialRocofHzPerS;
        double recRocof = recorded.PeakRocofHzPerS();
        double nadirErr = Math.Abs(sim.NadirHz - recorded.NadirHz);
        double rocofErr = Math.Abs(Math.Abs(simRocof) - Math.Abs(recRocof));
        double timeErr = Math.Abs(sim.NadirSeconds - recorded.NadirTimeS);
        bool passed = nadirErr <= tol.NadirHz && rocofErr <= tol.RocofHzPerS && timeErr <= tol.NadirTimeS;
        string note = passed
            ? "within tolerance of the recorded event"
            : $"OUTSIDE tolerance (nadir {nadirErr:F3}/{tol.NadirHz}, rocof {rocofErr:F3}/{tol.RocofHzPerS}, t {timeErr:F1}/{tol.NadirTimeS})";
        return new ValidationReport(passed, nadirErr, rocofErr, timeErr, note,
            simRocof, recRocof, sim.NadirSeconds, recorded.NadirTimeS);
    }

    /// <summary>
    /// D-DOC-037: the BLIND step of the protocol, with the freeze enforced. Replays <paramref name="blindEvent"/> (its
    /// recorded losses and actions, its recorded services and pre-event frequency, the window extended to cover the
    /// sequence, no device) with <paramref name="shared"/>, and back-tests it against <paramref name="recorded"/>.
    /// Throws <see cref="InvalidOperationException"/>, naming every difference, when <paramref name="shared"/> changes
    /// any constant frozen at the anchor, and when the blind event IS the anchor event (that is a fit, not a prediction).
    /// </summary>
    public static ValidationReport BlindPredict(FrozenParameterSet frozen, DynamicsConfig shared, GridEvent blindEvent,
        RecordedFrequencyTrace recorded, ValidationTolerance? tol = null)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(blindEvent);
        ArgumentNullException.ThrowIfNull(recorded);
        if (string.Equals(blindEvent.Id, frozen.AnchorEventId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Blind prediction refused: '{blindEvent.Id}' is the anchor event the parameters were frozen on. Predicting it " +
                "again is a fit, not a blind test; predict a different event.");
        IReadOnlyList<string> changed = frozen.Differences(shared);
        if (changed.Count > 0)
            throw new InvalidOperationException(
                $"Blind prediction refused: the parameter set was frozen on '{frozen.AnchorEventId}' and has been changed since: " +
                string.Join("; ", changed) + ". A blind prediction must use the frozen set unchanged.");

        IReadOnlyList<FrequencyEvent> events = blindEvent.ToFrequencyEvents();
        DynamicsConfig cfg = ReplayValidator.ExtendToCover(blindEvent.ApplyRecordedServices(shared), events);
        FrequencyResponse sim = FrequencyDynamics.Simulate(blindEvent.PreEventInertiaMwS, blindEvent.PreEventDemandMw, events, cfg);
        return Compare(sim, recorded, tol);
    }
}
