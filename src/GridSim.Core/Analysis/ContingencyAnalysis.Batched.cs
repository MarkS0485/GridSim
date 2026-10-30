using System.Runtime.ExceptionServices;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

public static partial class ContingencyAnalysis
{
    /// <summary>R071: the most lanes (one dedicated thread each) <see cref="ScreenBranchesBatched"/> accepts. A batch
    /// wider than this buys nothing on a CPU and would need that many threads at once; a GPU batch is formed per Newton
    /// iteration of the lane set, so this also bounds one dispatch at 1,024 systems.</summary>
    public const int MaxBatchedLanes = 1024;

    /// <summary>
    /// R071 (October 2026): the per-branch N-1 screen with every Newton step of every outage solved through a BATCHED
    /// linear solver (<see cref="IBatchedSparseLinearSolver"/>), the workload the batched seam was built for.
    /// <para>
    /// <b>How the batch is formed.</b> The sweep runs <paramref name="lanes"/> outages at a time, one per lane (a
    /// dedicated thread). Each lane runs an ordinary Newton solve of its outage; whenever it needs a step solved it
    /// posts its Jacobian and mismatch to a shared hub and waits. When every live lane has posted, the hub hands all
    /// the posted systems to the batched solver in one call (lane order), gives each lane its own answer back, and
    /// the lanes carry on. A lane that runs out of outages leaves the hub, so the others never wait for it. This is
    /// the lock-step shape a GPU wants: one dispatch per Newton iteration of the whole lane set instead of one per
    /// outage per iteration.
    /// </para>
    /// <para>
    /// <b>Bit-identical to the one-at-a-time sweep.</b> Each outage's Newton solve is the same code in the same order as
    /// in <see cref="ScreenBranches"/> / <see cref="ScreenBranchesParallel"/>; only WHERE the step's linear solve runs
    /// changes. With the default backend, <see cref="CpuBatchedSparseLuSolver"/>, that solve is the canonical
    /// <see cref="CpuSparseLuSolver"/> on the very same matrix and right-hand side, so every step, every iterate and every
    /// reported number is the same bits. The systems of one batch never interact (each is solved on its own), so which
    /// outages happen to share a batch cannot change any result either. A step the backend reports singular (null)
    /// takes the engine's fall-back exactly as before (the engine's direct-LU retry then repeats the same deterministic
    /// singular verdict, and the ILU0 / BiCGSTAB path runs on the same inputs), and a step that throws is rethrown on
    /// the lane, so it is classified exactly as the one-at-a-time sweep classifies it.
    /// <c>Cf4BatchedContingencySweepTests</c> proves it bit for bit on case9, case14 and case118.
    /// </para>
    /// <para>
    /// <paramref name="oltcLoop"/> true (default) runs the study engine (<see cref="CreateStudySolver"/>: the sparse
    /// Newton engine wrapped in the OLTC loop) and is bit-identical to
    /// <c>ScreenBranches(model, CreateStudySolver(model), options)</c>; false runs the bare sparse Newton engine and is
    /// bit-identical to <c>ScreenBranches(model, null, options)</c> (N057: every CPU Newton routes sparse). A dense
    /// <see cref="IBatchedLinearSolver"/> (the GPU batched LU) is used through <see cref="DenseBatchSparseAdapter"/>;
    /// that route is outside the CPU bit-identity contract, as every GPU solver is (see the adapter).
    /// </para>
    /// <para>
    /// Fail loud: a backend that throws as a whole, or returns a malformed answer (wrong count, wrong length), stops the
    /// sweep with <see cref="InvalidOperationException"/> naming the backend; it is never reported as a set of
    /// non-converged outages. Cancellation through <paramref name="ct"/> stops the sweep between steps or outages with
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenBranchesBatched(
        GridModel model, IBatchedSparseLinearSolver? batch = null, PowerFlowOptions? options = null,
        int lanes = -1, IReadOnlyList<int>? branchIndices = null, bool oltcLoop = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        batch ??= new CpuBatchedSparseLuSolver();

        // Which branches to outage: all of them, or a caller-supplied subset (as ScreenBranchesParallel). A subset
        // index outside the branch list is refused by name here, before any lane starts.
        int[] idx = branchIndices?.ToArray() ?? Enumerable.Range(0, model.Branches.Count).ToArray();
        foreach (int i in idx)
            if (i < 0 || i >= model.Branches.Count)
                throw new ArgumentOutOfRangeException(nameof(branchIndices), i,
                    $"branch index {i} is outside the model's {model.Branches.Count} branches.");
        var results = new ContingencyResult[idx.Length];
        if (idx.Length == 0) return results;

        // One lane per outage at most: an idle lane would only join the hub and leave again. Each lane is a dedicated
        // thread, so an absurd lane count is refused by name rather than starting thousands of threads.
        if (lanes > MaxBatchedLanes)
            throw new ArgumentOutOfRangeException(nameof(lanes), lanes,
                $"the batched sweep runs one thread per lane; at most {MaxBatchedLanes} lanes are allowed.");
        int laneCount = lanes <= 0 ? Environment.ProcessorCount : lanes;
        if (laneCount > idx.Length) laneCount = idx.Length;

        using var hub = new BatchedStepHub(batch, laneCount, ct);
        int next = -1;   // the shared outage cursor (Interlocked): which lane takes which outage changes no number

        void RunLane(int lane)
        {
            try
            {
                // The lane's engine: the same engine the sequential sweep uses, with its step solve routed to the hub.
                var engine = new SparseNewtonRaphsonPowerFlow(hub.Lane(lane));
                IPowerFlowSolver solver = oltcLoop ? WithOltcLoop(engine) : engine;
                while (!hub.Faulted)
                {
                    ct.ThrowIfCancellationRequested();
                    int k = Interlocked.Increment(ref next);
                    if (k >= idx.Length) break;
                    results[k] = ScreenOutageCore(model, new[] { idx[k] }, solver, options);
                }
            }
            finally
            {
                hub.Leave(lane);   // always: a lane that stops (done, cancelled or failed) must never hold the others
            }
        }

        // Dedicated threads (LongRunning), never Parallel.For: every lane must be running at once, because a phase of
        // the hub completes only when every live lane has posted, and a pool that ran fewer lanes than the hub expects
        // would wait for ever.
        var tasks = new Task[laneCount];
        for (int l = 0; l < laneCount; l++)
        {
            int lane = l;
            tasks[l] = Task.Factory.StartNew(() => RunLane(lane), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        try
        {
            // Not the sweep's token: the lanes watch it themselves, and the sweep must wait for every lane to leave the
            // hub (and stop touching the result array) before it returns or throws.
            Task.WaitAll(tasks, CancellationToken.None);
        }
        catch (AggregateException agg)
        {
            // A cancelled sweep reads as cancelled; otherwise the first lane failure is rethrown as itself.
            Exception[] inner = agg.Flatten().InnerExceptions.ToArray();
            Exception? cancelled = inner.FirstOrDefault(e => e is OperationCanceledException);
            if (cancelled is not null) ExceptionDispatchInfo.Capture(cancelled).Throw();
            if (hub.Fault is not null) throw hub.FaultException();
            ExceptionDispatchInfo.Capture(inner[0]).Throw();
        }

        // A backend fault stops the sweep loud even when every lane swallowed it into an outage's "solver error" line
        // (ScreenStateCore records a solver exception as that outage's failure, which is right for a real solver error
        // but would hide a broken batched backend as a column of non-converged outages).
        if (hub.Fault is not null) throw hub.FaultException();
        return results;
    }

    /// <summary>
    /// R071: the rendezvous that turns the lanes' one-at-a-time step solves into batches (see
    /// <see cref="ScreenBranchesBatched"/>). Built on <see cref="Barrier"/>: each live lane is a participant; a lane that
    /// needs a step posts its system and signals; the barrier's post-phase action (run once, on one thread, after every
    /// live lane has signalled and before any is released) solves the whole batch; a lane that is done removes itself.
    /// The barrier's phase boundaries are full memory fences, so the posted systems and the answers are visible across
    /// threads without further locking.
    /// </summary>
    private sealed class BatchedStepHub : IDisposable
    {
        private readonly IBatchedSparseLinearSolver _batch;
        private readonly CancellationToken _ct;
        private readonly Barrier _barrier;

        // Per-lane slots: the posted system, and the answer the post-phase action leaves for it.
        private readonly CsrMatrix?[] _a;
        private readonly double[]?[] _b;
        private readonly double[]?[] _x;
        private readonly Exception?[] _error;

        private volatile Exception? _fault;

        public BatchedStepHub(IBatchedSparseLinearSolver batch, int lanes, CancellationToken ct)
        {
            _batch = batch;
            _ct = ct;
            _a = new CsrMatrix?[lanes];
            _b = new double[]?[lanes];
            _x = new double[]?[lanes];
            _error = new Exception?[lanes];
            _barrier = new Barrier(lanes, _ => Dispatch());
        }

        /// <summary>The first whole-batch failure (backend threw, or answered malformed), or null.</summary>
        public Exception? Fault => _fault;

        public bool Faulted => _fault is not null;

        public InvalidOperationException FaultException() => new(
            $"batched contingency sweep: the batched linear solver '{_batch.Name}' failed: {_fault!.Message}", _fault);

        /// <summary>The <see cref="ISparseLinearSolver"/> a lane's Newton engine calls for each step.</summary>
        public ISparseLinearSolver Lane(int lane) => new LaneSolver(this, lane);

        /// <summary>Post one step system from <paramref name="lane"/>, wait for its batch, return its answer.</summary>
        private double[]? Post(int lane, CsrMatrix a, double[] b)
        {
            if (_fault is not null) throw FaultException();   // the backend is broken: stop this outage at once
            _a[lane] = a;
            _b[lane] = b;
            try
            {
                _barrier.SignalAndWait(_ct);
            }
            catch (OperationCanceledException)
            {
                // A cancelled wait withdraws this lane's signal, so its system must not be solved by a later phase.
                _a[lane] = null;
                _b[lane] = null;
                throw;
            }
            if (_fault is not null) throw FaultException();

            Exception? error = _error[lane];
            double[]? x = _x[lane];
            _error[lane] = null;
            _x[lane] = null;
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();   // as the one-at-a-time solve threw
            return x;
        }

        /// <summary>A lane is done: stop counting it (completes the phase if it was the last one awaited).</summary>
        public void Leave(int lane)
        {
            _a[lane] = null;
            _b[lane] = null;
            _barrier.RemoveParticipant();
        }

        /// <summary>The post-phase action: solve every posted system in one batched call, in lane order. Never throws
        /// (a throw here would surface in every lane as a barrier exception); a failure is recorded in
        /// <see cref="Fault"/> and rethrown by each lane and by the sweep.</summary>
        private void Dispatch()
        {
            var lanes = new List<int>(_a.Length);
            for (int l = 0; l < _a.Length; l++) if (_a[l] is not null) lanes.Add(l);
            if (lanes.Count == 0) return;   // the last lane left with nothing posted

            var matrices = new CsrMatrix[lanes.Count];
            var rhs = new double[lanes.Count][];
            for (int i = 0; i < lanes.Count; i++) { matrices[i] = _a[lanes[i]]!; rhs[i] = _b[lanes[i]]!; }
            var errors = new Exception?[lanes.Count];
            try
            {
                double[]?[] answers = _batch.SolveBatch(matrices, rhs, errors);
                if (answers is null || answers.Length != lanes.Count)
                    throw new InvalidOperationException(
                        $"it returned {(answers is null ? "no answer" : answers.Length + " answers")} for {lanes.Count} systems.");
                for (int i = 0; i < lanes.Count; i++)
                    if (answers[i] is double[] x && x.Length != matrices[i].N)
                        throw new InvalidOperationException(
                            $"it returned a solution of length {x.Length} for a {matrices[i].N} x {matrices[i].N} system.");
                for (int i = 0; i < lanes.Count; i++)
                {
                    int l = lanes[i];
                    _x[l] = answers[i];
                    _error[l] = errors[i];
                }
            }
            catch (Exception ex)
            {
                _fault ??= ex;
            }
            finally
            {
                foreach (int l in lanes) { _a[l] = null; _b[l] = null; }
            }
        }

        public void Dispose() => _barrier.Dispose();

        /// <summary>One lane's view of the hub, handed to that lane's Newton engine as its step solver.</summary>
        private sealed class LaneSolver(BatchedStepHub hub, int lane) : ISparseLinearSolver
        {
            public string Name => $"{hub._batch.Name} (lane {lane})";

            public double[]? Solve(CsrMatrix a, double[] b) => hub.Post(lane, a, b);
        }
    }
}
