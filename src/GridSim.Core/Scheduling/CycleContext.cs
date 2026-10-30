using GridSim.Core.Abstractions;
using GridSim.Core.Simulation;

namespace GridSim.Core.Scheduling;

/// <summary>
/// The read-only input shared by every job in one cycle: the solved instant plus the means to
/// re-solve. The <see cref="Frame"/> (<see cref="SimFrame"/> = regulated <c>GridModel</c> +
/// <c>PowerFlowResult</c>) is immutable-by-convention, so "copy the result to each job" is a shared
/// reference with zero deep-copy. The one sharp edge: <c>PowerFlowResult.Vm/VaDeg/PInjMw/QInjMvar</c>
/// are bare <c>double[]</c> with no clone - jobs MUST treat them as read-only (every existing analysis
/// already does); a job that mutates them corrupts every sibling job in the fan-out.
/// </summary>
public sealed class CycleContext
{
    /// <summary>The solved instant every job in this cycle reads. Immutable; safe to share across threads.</summary>
    public required SimFrame Frame { get; init; }

    /// <summary>Monotonic cycle counter (0-based), for rotation/staleness bookkeeping and telemetry.</summary>
    public required long CycleIndex { get; init; }

    /// <summary>
    /// Produces a fresh <see cref="IPowerFlowSolver"/> for a worker that needs to re-solve (N-1, tap
    /// studies). The power-flow engines and <c>SteadyStateSolver</c> are NOT thread-safe (the OLTC
    /// wrapper's re-entrancy guard throws on concurrent use), so a job must never share a solver across
    /// threads - it takes its own from here. Mirrors the <c>ThreadLocal&lt;IPowerFlowSolver&gt;</c>
    /// pattern of <see cref="Analysis.ContingencyAnalysis.ScreenBranchesParallel"/>.
    /// </summary>
    public required Func<IPowerFlowSolver> SolverFactory { get; init; }

    /// <summary>
    /// True when this cycle's jobs are draining a backlog against the PREVIOUS cycle's frame (the
    /// pipelined-backlog optimisation that fills the cores idle during the serial base solve). Stale by
    /// exactly one cycle - correct for deferrable Low work by definition, but surfaced so a consumer
    /// never reads a one-cycle-old N-1 verdict as current.
    /// </summary>
    public bool FromStaleFrame { get; init; }
}
