namespace GridSim.Core.Scheduling;

/// <summary>
/// One unit of post-solve analysis the cycle scheduler can dispatch onto a worker. A job is a stable
/// <em>description</em> of work (identified by <see cref="Id"/>) executed against whatever
/// <see cref="CycleContext.Frame"/> is current when it runs - so a Low job that is deferred and picked
/// up two cycles later re-screens the same elements against the fresh state, never a stale one.
/// <para>
/// Divisible work (a full N-1 sweep) is expressed as many small jobs with stable ids
/// (<c>n-1:lp:0</c>, <c>n-1:lp:1</c>, ...); the scheduler's Low-tier rotation then rolls coverage
/// across cycles. There is no per-job internal fan-out: a job runs on a single worker and does any
/// re-solving serially, because the scheduler already owns the cross-job parallelism.
/// </para>
/// </summary>
public sealed class AnalysisJob
{
    /// <summary>Stable identity across cycles (drives cost EWMA and Low-tier rotation). Must be unique in a set.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label for telemetry.</summary>
    public required string Name { get; init; }

    /// <summary>Which tier this job runs in.</summary>
    public required JobPriority Priority { get; init; }

    /// <summary>
    /// Seed cost in core-milliseconds, used for admission until the <see cref="JobCostModel"/> has
    /// observed a real runtime. A rough over-estimate is safer than an under-estimate (it just makes
    /// admission conservative for the first cycle or two).
    /// </summary>
    public double SeedCoreMs { get; init; } = 1.0;

    /// <summary>
    /// The work. Runs on a single worker thread against <paramref name="ctx"/>'s immutable frame. The
    /// <see cref="System.Threading.CancellationToken"/> is <c>None</c> for High jobs and the cycle's
    /// deadline token for Low jobs - a cooperative job should honour it (the solver core takes one through
    /// PowerFlowOptions.Cancellation, so a long re-solve given it aborts at the deadline). Returns the analysis output (cast by the
    /// consumer) or null.
    /// </summary>
    public required Func<CycleContext, System.Threading.CancellationToken, object?> Run { get; init; }
}
