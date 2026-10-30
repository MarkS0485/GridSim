using System.Diagnostics;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.Scheduling;

/// <summary>
/// Ties the pieces together into the real-time cycle: each cycle does one single-threaded Newton-Raphson
/// base solve (via <see cref="LiveSim"/>), then hands the resulting immutable frame to a
/// <see cref="CycleScheduler"/> that fans every downstream analysis out across the worker pool. Cycles are
/// sequential (the base solve is inherently single-core and the scheduler's rotation is stateful); the
/// parallelism is entirely inside each cycle's fan-out.
/// </summary>
public sealed class CycleRunner
{
    private readonly LiveSim _sim;
    private readonly CycleScheduler _scheduler;
    private readonly Func<IPowerFlowSolver> _solverFactory;
    private readonly double _stepMinutes;
    private int _tick;

    /// <param name="model">The network to drive.</param>
    /// <param name="options">Window / worker / budget configuration.</param>
    /// <param name="jobs">The analysis job set; null builds the standard set via <see cref="AnalysisJobLibrary.Build"/>.</param>
    /// <param name="holdSteady">Hold loads constant and warm-start (IEEE/test cases), vs the GB day/night curve.</param>
    /// <param name="stepMinutes">Integration timestep each base solve advances.</param>
    public CycleRunner(
        GridModel model, CycleOptions options, IReadOnlyList<AnalysisJob>? jobs = null,
        bool holdSteady = false, double stepMinutes = 1.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PipelineBacklog)
            throw new NotSupportedException(
                "CycleOptions.PipelineBacklog: the pipelined backlog drain is not implemented; every job runs on the current frame (R096).");

        // D-DOC-079: the shared rule (asset-dynamics OR protections bundle); it tested AssetDynamics only, so a
        // protections-only case ran with its protections idle.
        DynamicsConfig? dyn = LiveSim.AuthoredBundleConfig(model);
        _sim = new LiveSim(model, holdSteady, SolverKind.Auto, dyn);
        _stepMinutes = stepMinutes;

        // One engine kind for the whole run; the factory hands each worker its own instance (the engines
        // and SteadyStateSolver are single-instance-per-thread). Resolved once - cheap, and topology-stable.
        _solverFactory = SolverSelector.Resolve(model) switch
        {
            SolverKind.SparseNewton => () => new SparseNewtonRaphsonPowerFlow(),
            SolverKind.DistributionSweep => () => new BackwardForwardSweep(),
            _ => () => new NewtonRaphsonPowerFlow(),
        };

        _scheduler = new CycleScheduler(options, jobs ?? AnalysisJobLibrary.Build(model, options));
    }

    /// <summary>The scheduler, for telemetry (backlog depth, cost model).</summary>
    public CycleScheduler Scheduler => _scheduler;

    /// <summary>The resolved base-solve engine label (e.g. "Newton-Raphson (sparse)").</summary>
    public string ActiveSolver => _sim.ActiveSolver;

    /// <summary>Advance one cycle: base solve, then the prioritised parallel fan-out. Returns the cycle telemetry.</summary>
    public CycleResult RunOnce(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        SimFrame frame = _sim.Step(_tick++, _stepMinutes);
        sw.Stop();
        return _scheduler.RunCycle(frame, _solverFactory, sw.Elapsed.TotalMilliseconds, fromStaleFrame: false, ct);
    }

    /// <summary>Run <paramref name="cycles"/> cycles (or until cancelled), invoking <paramref name="onCycle"/> after each.</summary>
    public void Run(int cycles, Action<CycleResult>? onCycle = null, CancellationToken ct = default)
    {
        for (int c = 0; c < cycles && !ct.IsCancellationRequested; c++)
            onCycle?.Invoke(RunOnce(ct));
    }
}
