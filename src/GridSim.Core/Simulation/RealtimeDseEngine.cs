using System.Threading;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.Model;

namespace GridSim.Core.Simulation;

/// <summary>One emitted real-time dynamic-state estimate, stamped at its window's (present/past) state time.</summary>
public sealed record DseFrame(DateTime StateTimeUtc, DseEstimate Estimate, int Sequence);

/// <summary>
/// The end-goal artifact in one place: a <b>guard-safe, real-time, dynamic (tracking) state estimator</b>. It
/// streams dated measurement windows through a recursive <see cref="KalmanStateEstimator"/>, which carries the
/// state and its covariance across windows - so each estimate is informed by the whole history, not a lone
/// snapshot. It is the streaming resilience of <see cref="RealtimeEstimatorEngine"/> married to the tracking
/// filter of Phase 2.
///
/// <para>It stays inside the lines of <c>UnderstandingRestrictions.md</c>: every window's timestamp is checked
/// against the horizon before it is estimated (a fresh window is recorded and skipped, never processed), the
/// filter's review gate is asserted at construction, and every emitted state is the <b>filtered present</b>,
/// stamped at the window time - a filter, not a predictor, on permitted-age or replayed data.</para>
/// </summary>
public sealed class RealtimeDseEngine
{
    // The filter's predict-update step. The EKF constructor binds it to a KalmanStateEstimator exactly as before (the
    // engine used to hold the filter object itself; calling Step through a delegate is the same call). The UKF and
    // EnKF factories below bind it to their filters behind a per-window size guard (R062).
    private readonly Func<MeasurementSet, double, DseEstimate> _step;

    /// <summary>The method the engine runs: <c>Ekf</c>, <c>Ukf</c> or <c>EnKf</c> (the reviewed method names).</summary>
    public string MethodName { get; }

    public RealtimeDseEngine(GridModel model, IProcessModel? process = null)
    {
        var filter = new KalmanStateEstimator(model, process);
        _step = filter.Step;
        MethodName = KalmanStateEstimator.MethodName;
    }

    private RealtimeDseEngine(Func<MeasurementSet, double, DseEstimate> step, string methodName)
    {
        _step = step;
        MethodName = methodName;
    }

    /// <summary>
    /// A streaming engine driven by the <see cref="UnscentedKalmanStateEstimator"/> (R062: reachable from
    /// <c>serve --method ukf</c>). The unscented filter holds a dense s x s covariance, propagates 2s + 1 sigma points
    /// and inverts a dense m x m innovation covariance every window, and has no size check of its own, so this factory
    /// refuses a network above <see cref="DenseEstimationLimits.RecursiveFilterMaxState"/> states at construction and
    /// each window refuses more than <see cref="DenseEstimationLimits.InnovationFilterMaxMeasurements"/> measurements.
    /// Both refusals are <see cref="NotSupportedException"/> (fail loud; never a silent fallback to another filter).
    /// </summary>
    public static RealtimeDseEngine Unscented(GridModel model, IProcessModel? process = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        GuardStateSize(model, UnscentedKalmanStateEstimator.MethodName);
        var filter = new UnscentedKalmanStateEstimator(model, process);
        return new RealtimeDseEngine((w, dt) =>
        {
            GuardMeasurementCount(w, UnscentedKalmanStateEstimator.MethodName);
            return filter.Step(w, dt);
        }, UnscentedKalmanStateEstimator.MethodName);
    }

    /// <summary>
    /// A streaming engine driven by the <see cref="EnsembleKalmanStateEstimator"/> (R062: reachable from
    /// <c>serve --method enkf</c>), with the filter's own defaults (80 members, fixed seed, so a run is repeatable).
    /// The same two size guards as <see cref="Unscented"/> apply (the ensemble filter inverts a dense m x m
    /// innovation covariance every window).
    /// </summary>
    public static RealtimeDseEngine Ensemble(GridModel model, IProcessModel? process = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        GuardStateSize(model, EnsembleKalmanStateEstimator.MethodName);
        var filter = new EnsembleKalmanStateEstimator(model, process);
        return new RealtimeDseEngine((w, dt) =>
        {
            GuardMeasurementCount(w, EnsembleKalmanStateEstimator.MethodName);
            return filter.Step(w, dt);
        }, EnsembleKalmanStateEstimator.MethodName);
    }

    // The state size is fixed by the network: the non-slack angles plus every magnitude (the same count the EKF checks).
    private static void GuardStateSize(GridModel model, string method)
    {
        int stateSize = model.Buses.Count(b => b.Type != BusType.Slack) + model.BusCount;
        if (stateSize > DenseEstimationLimits.RecursiveFilterMaxState)
            throw new NotSupportedException(
                $"The {method} filter is dense (s x s covariance every window) and is limited to " +
                $"{DenseEstimationLimits.RecursiveFilterMaxState} states; this case has {stateSize} ({model.BusCount} buses). " +
                "Use the snapshot WLS estimator, which switches to the sparse backend at this size.");
    }

    // The innovation covariance is m x m and is inverted densely every window, so the window's measurement count is capped.
    private static void GuardMeasurementCount(MeasurementSet window, string method)
    {
        int m = window.Measurements.Count;
        if (m > DenseEstimationLimits.InnovationFilterMaxMeasurements)
            throw new NotSupportedException(
                $"The {method} filter inverts a dense m x m innovation covariance every window and is limited to " +
                $"{DenseEstimationLimits.InnovationFilterMaxMeasurements} measurements per window; the window at " +
                $"{window.TakenUtc:o} has {m}. Use --method ekf (information form, no m x m inverse) or wls.");
    }

    /// <summary>Run the tracking filter over a stream of dated windows until exhausted or cancelled.</summary>
    public RealtimeRunSummary Run(
        IEnumerable<MeasurementSet> windows,
        RealtimeEstimatorOptions? options = null,
        Action<DseFrame>? onFrame = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(windows);
        RealtimeEstimatorOptions opt = options ?? new RealtimeEstimatorOptions();
        // D-DOC-018: an override 'now' may only move the horizon back, never past the real clock (fails loud with a
        // ForwardInferenceException before any window is read). Defence in depth behind the CLI's own --utc-now check.
        // The checked, UTC-normalised now is the one every guard below uses (the raw option used to be passed on, so a
        // Local-kind now moved the horizon forward by the UTC offset). Null keeps the real clock at each check.
        DateTime? guardNow = opt.UtcNow is null ? null : ForwardInferenceGuard.RequireNotAfterRealClock(opt.UtcNow, "RealtimeEstimatorOptions.UtcNow");

        int processed = 0, converged = 0, rejectedFresh = 0, failed = 0, sequence = 0;
        DateTime? firstState = null, lastState = null;
        DateTime? previousState = null;

        foreach (MeasurementSet window in windows)
        {
            if (ct.IsCancellationRequested) break;

            if (window.TakenUtc is not DateTime stateTime)
                throw new ForwardInferenceException(
                    "real-time measurement window carries no TakenUtc; an undated window is refused so it cannot bypass the horizon.",
                    ForwardInferenceReason.Undated);

            try
            {
                ForwardInferenceGuard.RejectFuture(stateTime, guardNow, "real-time DSE window");
            }
            catch (ForwardInferenceException)
            {
                rejectedFresh++;
                continue;
            }

            // Elapsed time since the previous processed window drives the process-noise growth. The first window has
            // no predecessor and uses the filter's 1 s default step.
            // N025 (R256 follow-up): equal timestamps used to be coerced to a full 1 s of process noise, and an
            // out-of-order (earlier) window too. Now an equal timestamp passes dt = 0 (no growth: the process model
            // accepts it since R256), and an earlier one is refused: it fails the run, or with ContinueOnError counts
            // as failed and is skipped.
            double dtSeconds = previousState is DateTime prev ? (stateTime - prev).TotalSeconds : 1.0;

            DseEstimate estimate;
            try
            {
                if (dtSeconds < 0)
                    throw new InvalidDataException(
                        $"real-time DSE window at {stateTime:o} is earlier than the previous processed window " +
                        $"({previousState:o}); windows must arrive in time order.");
                estimate = _step(window, dtSeconds);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                if (!opt.ContinueOnError) throw;
                failed++;
                continue;
            }

            processed++;
            firstState ??= stateTime;
            lastState = stateTime;
            previousState = stateTime;
            if (estimate.Converged) converged++;

            // Mechanical output guard: the emitted estimate must be stamped present/past. The predict step
            // integrates the model forward internally; this proves no future-dated state reaches a frame.
            RestrictionReview.AssertOutputHistorical(stateTime, guardNow, "real-time DSE estimate");
            onFrame?.Invoke(new DseFrame(stateTime, estimate, sequence++));
        }

        return new RealtimeRunSummary(processed, converged, rejectedFresh, failed, firstState, lastState);
    }
}
