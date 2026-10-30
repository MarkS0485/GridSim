using System.Threading;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Simulation;

/// <summary>
/// A resident, guard-safe streaming state estimator. It pulls a measurement window, refuses anything newer
/// than the historical horizon, warm-starts from the previous estimate, runs the validated
/// <see cref="WlsStateEstimator"/>, and emits a frame - continuously, for the lifetime of the process.
///
/// <para>This is real-time <b>engineering</b> - streaming ingest, a resident loop, warm-started sub-cycle
/// cadence, resilience - on <b>guard-permitted-age or replayed</b> data, exactly as
/// <c>UnderstandingRestrictions.md</c> permits. It is a <b>filter, not a predictor</b>: every emitted state is
/// stamped at its window's time (present/past), never a future-dated claim about the real grid. Wall-clock
/// pacing (the sub-cycle clock via <see cref="Frequency.ReplayController"/> / <see cref="Frequency.RocofUpsampler"/>)
/// is a host concern; the engine itself processes each window as it arrives so it stays deterministic and
/// testable. Every window's timestamp passes <see cref="ForwardInferenceGuard.RejectFuture"/> before it is
/// ever estimated - a window newer than the horizon is recorded and skipped, never processed, so the resident
/// service is structurally incapable of nowcasting.</para>
/// </summary>
public sealed class RealtimeEstimatorEngine
{
    private readonly GridModel _model;
    private readonly WlsStateEstimator _estimator;

    public RealtimeEstimatorEngine(GridModel model, WlsStateEstimator? estimator = null)
    {
        _model = model;
        _estimator = estimator ?? new WlsStateEstimator();
    }

    /// <summary>
    /// Run the estimator over a stream of dated measurement windows until it is exhausted or cancelled,
    /// invoking <paramref name="onFrame"/> for each accepted estimate. Returns a summary of what happened.
    /// </summary>
    public RealtimeRunSummary Run(
        IEnumerable<MeasurementSet> windows,
        RealtimeEstimatorOptions? options = null,
        Action<RealtimeEstimateFrame>? onFrame = null,
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
        PowerFlowResult? prior = null;

        foreach (MeasurementSet window in windows)
        {
            if (ct.IsCancellationRequested) break;

            // A real-time window MUST be dated - an undated window could trivially pass the horizon
            // (DateTime.MinValue), so refuse it as a protocol violation, exactly as the bridge does.
            if (window.TakenUtc is not DateTime stateTime)
                throw new ForwardInferenceException(
                    "real-time measurement window carries no TakenUtc; an undated window is refused so it cannot bypass the horizon.",
                    ForwardInferenceReason.Undated);

            // Invariant #1/#4: never process a window newer than the horizon. Record and skip - a resident
            // service must not crash on a briefly-fresh window, but it must never estimate one.
            try
            {
                ForwardInferenceGuard.RejectFuture(stateTime, guardNow, "real-time window");
            }
            catch (ForwardInferenceException)
            {
                rejectedFresh++;
                continue;
            }

            EstimationOptions estOpt = opt.WarmStartFromPrior && prior is not null
                ? opt.Estimation with { WarmStart = prior, FlatStart = false }
                : opt.Estimation;

            EstimationResult result;
            try
            {
                result = _estimator.Estimate(_model, window, estOpt);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // A malformed / unsupported window is recorded and skipped, not fatal to the resident loop.
                if (!opt.ContinueOnError) throw;
                failed++;
                continue;
            }

            processed++;
            firstState ??= stateTime;
            lastState = stateTime;

            if (result.Converged)
            {
                converged++;
                // Chain the next window's warm start from this (converged) state - the natural per-window
                // continuity a tracking estimator wants. A non-converged state is not trusted as a prior.
                prior = result.ToPowerFlowResult(_model);
            }
            else if (!opt.ContinueOnError)
            {
                throw new InvalidOperationException(
                    $"real-time estimate for window @ {stateTime:o} did not converge ({result.Status}).");
            }

            // Mechanical output guard: an emitted estimate must be stamped present/past, never future-dated.
            RestrictionReview.AssertOutputHistorical(stateTime, guardNow, "real-time estimate");
            onFrame?.Invoke(new RealtimeEstimateFrame(stateTime, result, sequence++));
        }

        return new RealtimeRunSummary(processed, converged, rejectedFresh, failed, firstState, lastState);
    }
}

/// <summary>Knobs for a <see cref="RealtimeEstimatorEngine"/> run.</summary>
public sealed record RealtimeEstimatorOptions
{
    /// <summary>Estimation knobs handed to the inner <see cref="WlsStateEstimator"/>.</summary>
    public EstimationOptions Estimation { get; init; } = new();

    /// <summary>The "now" the horizon guard compares against. Null uses the wall clock; set it for
    /// deterministic replay/testing so the guard boundary is fixed.</summary>
    public DateTime? UtcNow { get; init; }

    /// <summary>Seed each window's estimate from the previous converged estimate (tracking continuity).</summary>
    public bool WarmStartFromPrior { get; init; } = true;

    /// <summary>
    /// Resilience. With true (the default), a window the estimator cannot take (the static engine: an
    /// <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/> from the estimator; the recursive
    /// <see cref="RealtimeDseEngine"/>: an <see cref="InvalidDataException"/> or <see cref="InvalidOperationException"/>
    /// from the filter step, including a window earlier than the previous one) is counted in
    /// <see cref="RealtimeRunSummary.WindowsFailed"/> and skipped: no frame is emitted for it. A window that IS estimated
    /// but does not converge is NOT skipped: it is counted as processed (not converged), its frame is still emitted with
    /// its status, and the static engine does not use it as the next window's warm start. With false, the first such
    /// failure, or the first non-converged static estimate, throws and ends the run. A window newer than the horizon is
    /// always skipped (counted as rejected-fresh, never estimated) whatever this says.
    /// <para>D-DOC-228 (October 2026): this comment used to say that a non-converged estimate is "recorded and skipped",
    /// which is not what either engine does (it is emitted).</para>
    /// </summary>
    public bool ContinueOnError { get; init; } = true;
}

/// <summary>One emitted real-time estimate, stamped at its window's (present/past) state time.</summary>
public sealed record RealtimeEstimateFrame(DateTime StateTimeUtc, EstimationResult Result, int Sequence);

/// <summary>What a <see cref="RealtimeEstimatorEngine.Run"/> did over its stream.</summary>
public sealed record RealtimeRunSummary(
    int WindowsProcessed,
    int WindowsConverged,
    int WindowsRejectedFresh,
    int WindowsFailed,
    DateTime? FirstStateTimeUtc,
    DateTime? LastStateTimeUtc);
