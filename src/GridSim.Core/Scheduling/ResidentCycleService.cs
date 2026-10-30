using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.Scheduling;

/// <summary>
/// A resident, host-agnostic driver for the prioritised parallel analysis cycle. A UI's step loop produces
/// one solved <see cref="SimFrame"/> per tick and hands it here via <see cref="Offer"/>; this service runs
/// the <see cref="CycleScheduler"/> fan-out <b>off the caller's thread</b> and publishes the latest
/// <see cref="CycleResult"/> plus the N-1 shard coverage so far (cumulative, see <see cref="ShardsCovered"/>), raising <see cref="Updated"/> when a cycle
/// completes. Only the newest offered frame is ever processed (intermediate frames coalesce away) - telemetry
/// is a monitor, not a queue. Both the Blazor and WPF control rooms wrap this so the analysis engine and its
/// telemetry are shared, not twinned.
/// </summary>
public sealed class ResidentCycleService : IDisposable
{
    private readonly CycleScheduler _scheduler;
    private readonly Func<IPowerFlowSolver> _factory;
    private readonly HashSet<string> _coveredShards = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    // The token is read once, here, so the loop never touches the source itself: Dispose may cancel while a cycle is
    // between iterations, and reading _cts.Token of a disposed source threw ObjectDisposedException inside the task.
    private readonly CancellationToken _token;
    private readonly object _lock = new();
    private SimFrame? _pending;
    private double _pendingBaseMs;
    private bool _running;
    private Task? _loop;
    private CycleResult? _latest;
    private int _shardsCovered;
    private string? _lastError;

    public ResidentCycleService(GridModel model, CycleOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<AnalysisJob> jobs = AnalysisJobLibrary.Build(model, options);
        ShardsTotal = jobs.Count(j => j.Id.StartsWith("n-1:lp:", StringComparison.Ordinal));
        Workers = options.Workers;
        _scheduler = new CycleScheduler(options, jobs);
        _token = _cts.Token;
        _factory = SolverSelector.Resolve(model) switch
        {
            SolverKind.SparseNewton => () => new SparseNewtonRaphsonPowerFlow(),
            SolverKind.DistributionSweep => () => new BackwardForwardSweep(),
            _ => () => new NewtonRaphsonPowerFlow(),
        };
    }

    /// <summary>Telemetry of the most recently completed cycle (null until the first completes). Published with a
    /// memory barrier: the loop writes it on a pool thread and a UI reads it on its own.</summary>
    public CycleResult? Latest => Volatile.Read(ref _latest);

    /// <summary>Why the analysis loop stopped, or null while it is healthy. A cycle that throws (a solver-factory or
    /// frame error) used to end the fire-and-forget task unobserved, so the UI went on showing the last cycle's verdict
    /// for ever; the failure is now published here, <see cref="Updated"/> is raised, and no further cycle runs.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// How many distinct Low N-1 shards have completed at least once since this service was built (the coverage
    /// numerator). CUMULATIVE: it only grows, and a shard completed many frames ago still counts, so it says "the
    /// rotation has reached every shard", not "every shard is fresh". D-DOC-144: the desktop and web labels said
    /// "rolling coverage", which this is not; they now read "N-1 shards covered since load". The service is rebuilt
    /// on a case switch (desktop: a change of bus or branch count; web: a new session), which restarts the count.
    /// </summary>
    public int ShardsCovered => Volatile.Read(ref _shardsCovered);

    /// <summary>Total Low N-1 shards in the rotation (coverage denominator).</summary>
    public int ShardsTotal { get; }

    /// <summary>Worker count the budget was sized for (for the telemetry header).</summary>
    public int Workers { get; }

    /// <summary>Raised when a new cycle completes (so a UI can re-render between frames).</summary>
    public event Action? Updated;

    /// <summary>
    /// Hand the latest solved frame to the scheduler (non-blocking). Stashes it and, if no cycle is in
    /// flight, kicks one on the thread pool. Frames offered while a cycle runs coalesce to the newest.
    /// </summary>
    public void Offer(SimFrame frame, double baseSolveMs)
    {
        lock (_lock)
        {
            _pending = frame;
            _pendingBaseMs = baseSolveMs;
            if (_running || _token.IsCancellationRequested || _lastError is not null) return;
            _running = true;
            _loop = Task.Run(RunLoop);
        }
    }

    private void RunLoop()
    {
        try
        {
            while (true)
            {
                SimFrame frame;
                double baseMs;
                lock (_lock)
                {
                    if (_pending is null || _token.IsCancellationRequested) { _running = false; return; }
                    frame = _pending;
                    baseMs = _pendingBaseMs;
                    _pending = null;
                }

                CycleResult r;
                try
                {
                    r = _scheduler.RunCycle(frame, _factory, baseMs, fromStaleFrame: false, _token);
                }
                catch (OperationCanceledException)
                {
                    lock (_lock) _running = false;
                    return;
                }
                catch (Exception ex)
                {
                    // Fail loud: publish the failure and stop, rather than let the task die unobserved with the last
                    // cycle's result still on show.
                    Volatile.Write(ref _lastError, $"analysis cycle failed: {ex.GetType().Name}: {ex.Message}");
                    lock (_lock) _running = false;
                    try { Updated?.Invoke(); } catch { /* a dead subscriber must not hide the failure further */ }
                    return;
                }

                foreach (JobResult jr in r.Results)
                    if (jr.Outcome == JobOutcome.Completed && jr.Id.StartsWith("n-1:lp:", StringComparison.Ordinal))
                        _coveredShards.Add(jr.Id);

                Volatile.Write(ref _shardsCovered, _coveredShards.Count);
                Volatile.Write(ref _latest, r);
                try { Updated?.Invoke(); } catch { /* a dead subscriber must not kill the loop */ }
            }
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    /// <summary>Stop the loop: cancel, and dispose the token source only once the loop has finished (it is never
    /// disposed under a running loop). Does not block the caller (a UI thread) for the rest of a cycle.</summary>
    public void Dispose()
    {
        Task? loop;
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;   // already disposed
            _cts.Cancel();
            loop = _loop;
        }
        if (loop is null || loop.IsCompleted) _cts.Dispose();
        else loop.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
