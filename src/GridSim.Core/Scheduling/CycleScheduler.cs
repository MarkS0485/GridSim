using System.Diagnostics;
using GridSim.Core.Abstractions;
using GridSim.Core.Simulation;

namespace GridSim.Core.Scheduling;

/// <summary>
/// Fans a fixed set of post-solve analysis jobs out across a worker pool once per cycle, against one
/// shared immutable frame. The contract the whole design rests on:
/// <list type="bullet">
///   <item><b>High always completes.</b> Every High job runs to completion each cycle, never cancelled
///   at the deadline - so the essential view is always current even when the cycle runs long.</item>
///   <item><b>Low is best-effort and rotates.</b> Low jobs are admitted only while the core-ms budget
///   lasts, run under the wall-clock deadline, and any not served this cycle keep their place at the
///   front of the rotating pool so a later cycle covers them - no starvation.</item>
/// </list>
/// <para><b>Not re-entrant.</b> <see cref="RunCycle"/> owns the rotating Low pool and must be called
/// serially (one cycle at a time) - which is exactly how <see cref="CycleRunner"/> drives it. The
/// fan-out <em>within</em> a cycle is parallel; the cycles themselves are sequential.</para>
/// </summary>
public sealed class CycleScheduler
{
    private readonly CycleOptions _opt;
    private readonly JobCostModel _costs;
    private readonly List<AnalysisJob> _high = new();
    private List<AnalysisJob> _low = new();   // rotating pool: front = served-next, back = most-recently-served
    private long _cycle;

    /// <param name="options">Window, worker count, budget knobs.</param>
    /// <param name="jobs">The full job set (High and Low). Fixed for the scheduler's lifetime.</param>
    /// <param name="costs">Optional shared cost model (else one is created from the options' alpha).</param>
    public CycleScheduler(CycleOptions options, IEnumerable<AnalysisJob> jobs, JobCostModel? costs = null)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        // GR-841: the options are checked once, here, rather than clamped or misread where they are used.
        if (double.IsNaN(_opt.WindowMs) || _opt.WindowMs < 0)
            throw new ArgumentOutOfRangeException(nameof(options), _opt.WindowMs, "CycleOptions.WindowMs must be 0 (no deadline) or above.");
        if (_opt.Workers < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _opt.Workers, "CycleOptions.Workers must be 1 or more.");
        if (_opt.NMinus1Shards < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _opt.NMinus1Shards, "CycleOptions.NMinus1Shards must be 1 or more.");
        if (!(_opt.HotBranchLoadingPercent >= 0) || double.IsInfinity(_opt.HotBranchLoadingPercent))
            throw new ArgumentOutOfRangeException(nameof(options), _opt.HotBranchLoadingPercent, "CycleOptions.HotBranchLoadingPercent must be a finite number, 0 or above.");
        _costs = costs ?? new JobCostModel(options.CostEwmaAlpha);
        // Job ids must be unique: the Low rotation and BacklogDepth key on JobResult.Id, so two Low jobs sharing an id
        // would both rotate to the back when one completes and the backlog would be under-counted. The library builds
        // unique ids; a caller-supplied list is checked here and refused by name rather than miscounted later.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AnalysisJob j in jobs ?? throw new ArgumentNullException(nameof(jobs)))
        {
            if (!seenIds.Add(j.Id))
                throw new ArgumentException($"Two analysis jobs share the id '{j.Id}'; every job id must be unique.", nameof(jobs));
            if (j.Priority == JobPriority.High) _high.Add(j);
            else _low.Add(j);
        }
    }

    /// <summary>
    /// The window the compute budget is formed from. <see cref="CycleOptions.WindowMs"/> = 0 means "no deadline", so it
    /// must also mean "no budget limit": the budget is Workers x WindowMs, and 0 used to give a ZERO core-ms budget, so
    /// the Low admission loop let exactly one Low job through per cycle (the slowest possible rotation, the opposite of
    /// what "no deadline" asks for). 0 now forms an infinite budget, exactly as +Infinity always did; every positive
    /// window is used as given.
    /// </summary>
    private double BudgetWindowMs => _opt.WindowMs == 0 ? double.PositiveInfinity : _opt.WindowMs;

    /// <summary>Number of Low jobs still owed a run in the current rotation round (telemetry).</summary>
    public int BacklogDepth { get; private set; }

    /// <summary>The rolling cost model, exposed so a host can pre-seed or inspect estimates.</summary>
    public JobCostModel Costs => _costs;

    /// <summary>
    /// Run one cycle's fan-out against <paramref name="frame"/>. The base solve has already happened
    /// (its cost is passed in for telemetry only); this schedules everything downstream of it.
    /// </summary>
    /// <param name="frame">The solved instant every job reads (immutable).</param>
    /// <param name="solverFactory">Produces a fresh, thread-local power-flow engine for a job that re-solves.</param>
    /// <param name="baseSolveMs">Wall-clock the single-threaded base solve took, for the report.</param>
    /// <param name="fromStaleFrame">True if draining a backlog against a prior frame (pipelined mode).</param>
    /// <param name="outerCt">Caller cancellation (session teardown), linked into the deadline token.</param>
    public CycleResult RunCycle(
        SimFrame frame, Func<IPowerFlowSolver> solverFactory, double baseSolveMs,
        bool fromStaleFrame = false, CancellationToken outerCt = default)
    {
        long cycle = _cycle++;
        var wall = Stopwatch.StartNew();

        // Every job reads the base state, so a base solve that did not converge has no state to analyse: N-1 verdicts,
        // hot-branch selection and the audits of a garbage iterate used to be published as a completed cycle with
        // HighTierMet true. No job runs; every one is reported Deferred with the reason, so HighTierMet is false and the
        // Low rotation is untouched (the owed jobs run on the next converged frame).
        if (!frame.Result.Converged)
        {
            string reason = $"base solve did not converge ({frame.Result.Status}); no analysis ran on this frame.";
            var skipped = new List<JobResult>(_high.Count + _low.Count);
            foreach (AnalysisJob j in _high) skipped.Add(Deferred(j, fromStaleFrame) with { Error = reason });
            foreach (AnalysisJob j in _low) skipped.Add(Deferred(j, fromStaleFrame) with { Error = reason });
            wall.Stop();
            return new CycleResult(
                CycleIndex: cycle, BaseSolveMs: baseSolveMs, WallMs: wall.Elapsed.TotalMilliseconds,
                HighTotal: _high.Count, HighCompleted: 0, LowAdmitted: 0, LowCompleted: 0, LowDeferred: _low.Count,
                LowCancelled: 0, BudgetTotalCoreMs: new CycleBudget(_opt.Workers, BudgetWindowMs).TotalCoreMs, BudgetSpentCoreMs: 0,
                BacklogDepth: BacklogDepth, FromStaleFrame: fromStaleFrame, Results: skipped);
        }

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        if (_opt.WindowMs > 0 && double.IsFinite(_opt.WindowMs))
            deadlineCts.CancelAfter(TimeSpan.FromMilliseconds(_opt.WindowMs));

        var ctx = new CycleContext
        {
            Frame = frame,
            CycleIndex = cycle,
            SolverFactory = solverFactory,
            FromStaleFrame = fromStaleFrame,
        };
        var budget = new CycleBudget(_opt.Workers, BudgetWindowMs);

        // ---- Admission ------------------------------------------------------
        // High: all admitted, unconditionally. Reserve their estimated cost first so Low can only take
        // what is genuinely left over.
        double reserved = 0;
        foreach (AnalysisJob j in _high) reserved += _costs.Estimate(j);

        // Low: walk the rotating pool front-to-back (most-owed first), admit while the running estimate
        // still fits the total budget. The rest are deferred to a later cycle.
        var admittedLow = new List<AnalysisJob>();
        double running = reserved;
        foreach (AnalysisJob j in _low)
        {
            double est = _costs.Estimate(j);
            if (running + est > budget.TotalCoreMs && admittedLow.Count > 0) break;
            admittedLow.Add(j);
            running += est;
        }

        // ---- Execute --------------------------------------------------------
        var highSlots = new JobResult?[_high.Count];
        Drain(_high, ctx, respectDeadline: false, budget, highSlots, CancellationToken.None);

        var lowSlots = new JobResult?[admittedLow.Count];
        Drain(admittedLow, ctx, respectDeadline: true, budget, lowSlots, deadlineCts.Token);

        // ---- Classify + rotate ---------------------------------------------
        var results = new List<JobResult>(_high.Count + _low.Count);
        int highCompleted = 0;
        for (int i = 0; i < highSlots.Length; i++)
        {
            JobResult r = highSlots[i] ?? Deferred(_high[i], fromStaleFrame); // High should never defer, but be total
            if (r.Outcome == JobOutcome.Completed) highCompleted++;
            results.Add(r);
        }

        var rotateToBack = new HashSet<string>(StringComparer.Ordinal);
        int lowCompleted = 0, lowCancelled = 0, lowFaulted = 0;
        for (int i = 0; i < admittedLow.Count; i++)
        {
            JobResult r = lowSlots[i] ?? Deferred(admittedLow[i], fromStaleFrame); // admitted but never pulled (deadline)
            switch (r.Outcome)
            {
                case JobOutcome.Completed: lowCompleted++; rotateToBack.Add(r.Id); break;
                case JobOutcome.Faulted: lowFaulted++; rotateToBack.Add(r.Id); break; // attempted; don't retry-storm
                case JobOutcome.Cancelled: lowCancelled++; break;                     // keep front: retry soon
                default: break;                                                       // Deferred: keep front
            }
            results.Add(r);
        }
        // Non-admitted Low jobs are deferred (not attempted this cycle) - report them too.
        for (int i = admittedLow.Count; i < _low.Count; i++)
            results.Add(Deferred(_low[i], fromStaleFrame));

        // Rotate: everything attempted-to-completion goes to the back; everything still owed keeps its
        // relative order at the front. Guarantees each Low job is eventually covered (round-robin).
        if (rotateToBack.Count > 0)
        {
            var kept = new List<AnalysisJob>(_low.Count);
            var back = new List<AnalysisJob>(rotateToBack.Count);
            foreach (AnalysisJob j in _low)
                (rotateToBack.Contains(j.Id) ? back : kept).Add(j);
            kept.AddRange(back);
            _low = kept;
        }

        BacklogDepth = _low.Count - rotateToBack.Count;
        wall.Stop();

        return new CycleResult(
            CycleIndex: cycle,
            BaseSolveMs: baseSolveMs,
            WallMs: wall.Elapsed.TotalMilliseconds,
            HighTotal: _high.Count,
            HighCompleted: highCompleted,
            LowAdmitted: admittedLow.Count,
            LowCompleted: lowCompleted,
            // Everything Low that neither completed, was cancelled at the deadline, nor faulted = deferred
            // (covers both admitted-but-never-pulled and never-admitted jobs). Count is rotation-invariant.
            LowDeferred: _low.Count - lowCompleted - lowCancelled - lowFaulted,
            LowCancelled: lowCancelled,
            BudgetTotalCoreMs: budget.TotalCoreMs,
            BudgetSpentCoreMs: budget.Spent,
            BacklogDepth: BacklogDepth,
            FromStaleFrame: fromStaleFrame,
            Results: results);
    }

    /// <summary>
    /// Up to <see cref="CycleOptions.Workers"/> workers pull jobs from <paramref name="jobs"/> by a
    /// shared atomic cursor and run them. When <paramref name="respectDeadline"/>, a worker stops pulling
    /// NEW jobs once the token trips (job-level cancellation) - a job already running finishes unless it
    /// itself honours the token (the solver core already takes one: PowerFlowOptions.Cancellation, so a job that
    /// passes it to its re-solves aborts at the deadline). Fills <paramref name="slots"/>
    /// at each pulled job's index; unpulled slots stay null (the caller marks them Deferred).
    /// </summary>
    private void Drain(
        IReadOnlyList<AnalysisJob> jobs, CycleContext ctx, bool respectDeadline,
        CycleBudget budget, JobResult?[] slots, CancellationToken token)
    {
        if (jobs.Count == 0) return;
        int next = -1;
        int workers = Math.Max(1, Math.Min(_opt.Workers, jobs.Count));

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            while (true)
            {
                if (respectDeadline && token.IsCancellationRequested) return;
                int i = Interlocked.Increment(ref next);
                if (i >= jobs.Count) return;

                AnalysisJob job = jobs[i];
                var sw = Stopwatch.StartNew();
                JobOutcome outcome;
                object? payload = null;
                string? error = null;
                try
                {
                    payload = job.Run(ctx, token);
                    outcome = JobOutcome.Completed;
                }
                catch (OperationCanceledException)
                {
                    outcome = JobOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    outcome = JobOutcome.Faulted;
                    error = ex.Message;
                }
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                budget.Record(ms);   // the core time was spent either way
                // R042: a run cut off at the deadline lasted about the window, not the job's real cost, so it
                // must not be learned as if it were a completion (that taught admission nothing). Only
                // completed or faulted runs feed the cost estimate.
                if (outcome != JobOutcome.Cancelled) _costs.Observe(job.Id, ms);
                slots[i] = new JobResult(job.Id, job.Name, job.Priority, outcome, ms, payload, ctx.FromStaleFrame, error);
            }
        });
    }

    private static JobResult Deferred(AnalysisJob j, bool stale) =>
        new(j.Id, j.Name, j.Priority, JobOutcome.Deferred, 0, null, stale);
}
