using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Scheduling;

/// <summary>
/// Factories that wrap GridSim's existing post-solve analyses as schedulable <see cref="AnalysisJob"/>s.
/// Nothing here re-implements analysis - each job calls the validated engine (contingency, harmonics,
/// RoCoF, physics conformance) against the cycle's shared immutable frame, on a single worker (the
/// scheduler owns the cross-job parallelism, so every re-solving job runs serial internally with
/// <c>maxDegreeOfParallelism: 1</c> to avoid nested fan-out).
/// </summary>
public static class AnalysisJobLibrary
{
    // ---- Seed costs (October 2026 review, WS13 R194). The cost model starts each job at its seed (core
    // ---- milliseconds) and then tracks the measured cost; the seeds only order the very first cycle. Values
    // ---- unchanged.

    /// <summary>Seed cost (core ms) of the hot-branch N-1 job.</summary>
    public const double SeedNMinus1HotMs = 50;
    /// <summary>Seed cost (core ms) per branch of a rotating N-1 shard.</summary>
    public const double SeedNMinus1PerBranchMs = 5.0;
    /// <summary>Seed cost (core ms) of the harmonic penetration job.</summary>
    public const double SeedHarmonicsMs = 40;
    /// <summary>Seed cost (core ms) of the RoCoF N-1 screen.</summary>
    public const double SeedRocofMs = 20;
    /// <summary>Seed cost (core ms) of the physics conformance audit.</summary>
    public const double SeedPhysicsAuditMs = 5;
    /// <summary>Seed cost (core ms) of the operator watch list.</summary>
    public const double SeedWatchListMs = 2;

    /// <summary>Fundamental current (pu) of the 6-pulse source the cyclic harmonics job places at the
    /// largest-demand bus.</summary>
    public const double HarmonicsSourceCurrentPu = 0.5;

    /// <summary>
    /// High-tier N-1: screen the branches that are currently hot (loading ≥
    /// <see cref="CycleOptions.HotBranchLoadingPercent"/>) every cycle - the security-critical subset an
    /// operator must always see current. The rest are covered by the rotating Low shards below.
    /// </summary>
    public static AnalysisJob NMinus1Hot(CycleOptions opt, PowerFlowOptions? solveOpt = null) => new()
    {
        Id = "n-1:hot",
        Name = "N-1 (hot branches)",
        Priority = JobPriority.High,
        SeedCoreMs = SeedNMinus1HotMs,
        Run = (ctx, ct) =>
        {
            GridModel m = ctx.Frame.Model;
            PowerFlow.PowerFlowResult r = ctx.Frame.Result;
            var hot = new List<int>();
            int n = Math.Min(r.LineFlows.Count, m.Branches.Count);
            for (int i = 0; i < n; i++)
                if (r.LineFlows[i].LoadingPercent >= opt.HotBranchLoadingPercent) hot.Add(i);
            if (hot.Count == 0) return Array.Empty<ContingencyResult>();
            return ContingencyAnalysis.ScreenBranchesParallel(
                m, ctx.SolverFactory, WithCancellation(solveOpt, ct), maxDegreeOfParallelism: 1,
                branchIndices: hot, ct: ct);
        },
    };

    /// <summary>
    /// Low-tier N-1: split the branch list into <see cref="CycleOptions.NMinus1Shards"/> fixed chunks,
    /// each a rotating job with a stable id (<c>n-1:lp:k</c>). The scheduler's Low rotation rolls one (or a
    /// few) chunks per cycle so full N-1 coverage is achieved over several cycles without ever blowing the
    /// window - the code comment's "sample subset to avoid the time budget" made into a real mechanism.
    ///
    /// <para><b>Resumable (R042).</b> A shard screens its outages one at a time and remembers how far it got.
    /// When the cycle deadline cancels it, the outages already screened are KEPT and the next cycle resumes
    /// from the next one; the shard completes (and returns every result) once its whole chunk is done. It used
    /// to discard all partial work on cancellation, so on a large case (gb-full: about 740 outages per shard
    /// against a 1,000 ms window) every shard was cancelled every cycle and rolling N-1 coverage never reached
    /// even one shard. A resumed shard's results were each screened against the frame current when that
    /// outage was reached - the same frame mixing that rolling coverage already has across shards.</para>
    ///
    /// <para><b>Keyed by branch identity (R288).</b> The chunk is fixed at construction, but a frame's model can
    /// have fewer branches (IslandPartition drops every branch of a dead island once a recloser holds a spur
    /// open), which shifted fixed indices onto the wrong outages and pushed the tail out of range. Each chunk
    /// entry is therefore a <see cref="BranchKey"/> (endpoints plus occurrence among same-endpoint branches),
    /// resolved to the current frame's index on every run. A branch absent from the frame is de-energised, so
    /// there is nothing to outage: it is skipped rather than mis-attributed.</para>
    /// </summary>
    public static IEnumerable<AnalysisJob> NMinus1Shards(GridModel model, CycleOptions opt, PowerFlowOptions? solveOpt = null)
    {
        int total = model.Branches.Count;
        int shards = Math.Max(1, opt.NMinus1Shards);
        int size = Math.Max(1, (total + shards - 1) / shards);
        BranchKey[] keys = BranchKey.ForEach(model);
        for (int s = 0; s < shards; s++)
        {
            int start = s * size;
            if (start >= total) yield break;
            int end = Math.Min(total, start + size);
            BranchKey[] chunk = keys[start..end];
            int shard = s;
            var progress = new ShardProgress();
            yield return new AnalysisJob
            {
                Id = $"n-1:lp:{shard}",
                Name = $"N-1 shard {shard} [{start}..{end})",
                Priority = JobPriority.Low,
                SeedCoreMs = chunk.Length * SeedNMinus1PerBranchMs,
                Run = (ctx, ct) => RunResumableShard(ctx, chunk, progress, solveOpt, ct),
            };
        }
    }

    /// <summary>
    /// A branch's identity that survives index shifts (R288): its endpoints and its occurrence number among the
    /// branches with the same (From, To) in model order. Dead-island removal takes every branch on a pair
    /// together, and topology changes keep branches in place (InService flag), so the occurrence is stable.
    /// </summary>
    internal readonly record struct BranchKey(int From, int To, int Occurrence)
    {
        /// <summary>The key of every branch of <paramref name="m"/>, by index.</summary>
        public static BranchKey[] ForEach(GridModel m)
        {
            var seen = new Dictionary<(int, int), int>();
            var keys = new BranchKey[m.Branches.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                Branch b = m.Branches[i];
                int k = seen.GetValueOrDefault((b.From, b.To));
                seen[(b.From, b.To)] = k + 1;
                keys[i] = new BranchKey(b.From, b.To, k);
            }
            return keys;
        }

        /// <summary>Key -> index in <paramref name="m"/>.</summary>
        public static Dictionary<BranchKey, int> IndexOf(GridModel m)
        {
            BranchKey[] keys = ForEach(m);
            var index = new Dictionary<BranchKey, int>(keys.Length);
            for (int i = 0; i < keys.Length; i++) index[keys[i]] = i;
            return index;
        }
    }

    /// <summary>How far a resumable shard has got: the next chunk position and the results so far (R042).</summary>
    private sealed class ShardProgress
    {
        public int Next;
        public readonly List<ContingencyResult> Done = new();
    }

    /// <summary>
    /// Run (or resume) one N-1 shard. Screens each remaining outage in order, checking the deadline between
    /// outages (and inside each solve, through the solver's cancellation token). On cancellation the progress
    /// is kept and <see cref="OperationCanceledException"/> propagates, so the scheduler records the run as
    /// Cancelled and keeps the shard at the front for the next cycle. On completion every result is returned
    /// and the progress resets for the next pass. Any other failure resets the progress before re-throwing, so
    /// a faulted shard starts clean rather than half-resumed.
    /// </summary>
    private static IReadOnlyList<ContingencyResult> RunResumableShard(
        CycleContext ctx, BranchKey[] chunk, ShardProgress progress, PowerFlowOptions? solveOpt, CancellationToken ct)
    {
        lock (progress)   // the scheduler never runs one job twice at once; the lock makes that a guarantee
        {
            try
            {
                GridModel m = ctx.Frame.Model;
                Dictionary<BranchKey, int> index = BranchKey.IndexOf(m);
                IPowerFlowSolver solver = ctx.SolverFactory?.Invoke() ?? SolverSelector.CreateNewton(m);
                PowerFlowOptions? options = WithCancellation(solveOpt, ct);
                while (progress.Next < chunk.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    if (index.TryGetValue(chunk[progress.Next], out int bi))
                        progress.Done.Add(ContingencyAnalysis.ScreenOutage(m, new[] { bi }, solver, options));
                    // else: the branch is not in this frame (a de-energised dead island) - nothing to outage.
                    progress.Next++;
                }
                ContingencyResult[] all = progress.Done.ToArray();
                progress.Next = 0;
                progress.Done.Clear();
                return all;
            }
            catch (OperationCanceledException)
            {
                throw;   // keep the progress: the next cycle resumes from here
            }
            catch
            {
                progress.Next = 0;
                progress.Done.Clear();
                throw;
            }
        }
    }

    /// <summary>Harmonic penetration study (a six-pulse source at the largest-demand bus: the G5 Issue 5 Table B.3 profile
    /// since N131), gated by bus count. High tier.</summary>
    /// <remarks>D-DOC-202: this took a <c>priority</c> parameter that no caller ever set (<see cref="Build"/> always
    /// passed the default, High), so it suggested a configurability the cycle does not have. Removed; the tier is fixed
    /// High here, where it is stated.</remarks>
    public static AnalysisJob Harmonics(CycleOptions opt) => new()
    {
        Id = "harmonics",
        Name = "Harmonic penetration",
        Priority = JobPriority.High,
        SeedCoreMs = SeedHarmonicsMs,
        Run = (ctx, ct) =>
        {
            GridModel m = ctx.Frame.Model;
            if (m.BusCount == 0 || m.BusCount > opt.HarmonicsBusLimit) return null; // gate: never block a huge case
            int srcBus = m.Buses.OrderByDescending(b => b.Pd).FirstOrDefault()?.Id ?? m.Buses[0].Id;
            // N131 (Ruling R1, Mark delegated 2026-10-04): the default synthetic source is the cited G5 Issue 5 Table B.3
            // six-pulse profile (it was the idealised 1/h six-pulse spectrum, which understates it).
            var sources = new[] { HarmonicSourceLibrary.Make(HarmonicSourceLibrary.DefaultSyntheticSourceType, srcBus, fundamentalCurrentPu: HarmonicsSourceCurrentPu) };
            // N075: fail loud if the source ever carries an order the study does not analyse (the G5 six-pulse orders
            // 5-49, odd, all lie inside the Study orders 3-49, as the 1/h orders 5-25 did, so this never throws).
            HarmonicPenetration.RefuseComponentsOutsideOrders(sources, HarmonicModelOptions.Study);
            // The physical study model, the same as the CLI 'harmonics' verb (R001).
            return HarmonicPenetration.Solve(m, sources, HarmonicModelOptions.Study);
        },
    };

    /// <summary>RoCoF N-1 screen over every dispatched generator (ODE swing integration; no power-flow re-solve).</summary>
    public static AnalysisJob RocofScreen() => new()
    {
        Id = "rocof",
        Name = "RoCoF N-1 screen",
        Priority = JobPriority.Low,
        SeedCoreMs = SeedRocofMs,
        Run = (ctx, ct) => RocofScreening.Screen(ctx.Frame.Model),
    };

    /// <summary>Physics-conformance audit of the solved state (Kirchhoff/energy laws; no re-solve, cheap).</summary>
    public static AnalysisJob PhysicsAudit() => new()
    {
        Id = "physics",
        Name = "Physics conformance",
        Priority = JobPriority.Low,
        SeedCoreMs = SeedPhysicsAuditMs,
        Run = (ctx, ct) => PhysicsLaws.Audit(ctx.Frame.Model, ctx.Frame.Result),
    };

    /// <summary>Operator watch list - the headline overload/voltage/convergence conditions (no re-solve). High tier.</summary>
    /// <remarks>D-DOC-202: the unused <c>priority</c> parameter is removed, as on <see cref="Harmonics"/>.</remarks>
    public static AnalysisJob WatchList() => new()
    {
        Id = "watch",
        Name = "Watch list",
        Priority = JobPriority.High,
        SeedCoreMs = SeedWatchListMs,
        Run = (ctx, ct) => Analysis.WatchList.Assess(ctx.Frame.Model, ctx.Frame.Result),
    };

    /// <summary>
    /// The job keys <see cref="Build"/> understands, in the order the jobs are built: <c>watch</c>; <c>n-1</c> (alias
    /// <c>nminus1</c>) for the hot-branch job and the rotating shards; <c>harmonics</c>; <c>rocof</c>; <c>physics</c>
    /// (alias <c>conformance</c>). Matched ignoring case.
    /// </summary>
    public static readonly IReadOnlyList<string> JobKeys =
        new[] { "watch", "n-1", "nminus1", "harmonics", "rocof", "physics", "conformance" };

    /// <summary>
    /// Read a comma-separated job list (the CLI's <c>cycle --jobs</c>) into the key set <see cref="Build"/> takes.
    /// D-DOC-081: <c>cycle --jobs bogus</c> used to build ZERO jobs, run every cycle with nothing to do, print "HP met
    /// every cycle: YES" and exit 0, a vacuous pass. Now an unknown key, or a list with no key at all, is refused with
    /// an <see cref="ArgumentException"/> naming the bad keys and the valid ones.
    /// </summary>
    public static IReadOnlySet<string> ParseJobKeys(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var keys = new HashSet<string>(
            csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        RequireKnownKeys(keys, nameof(csv));
        return keys;
    }

    /// <summary>Refuse an empty key set or any key outside <see cref="JobKeys"/> (D-DOC-081).</summary>
    private static void RequireKnownKeys(IReadOnlySet<string> keys, string paramName)
    {
        if (keys.Count == 0)
            throw new ArgumentException(
                $"No analysis job selected. Choose one or more of: {string.Join(", ", JobKeys)}.", paramName);
        string[] unknown = keys.Where(k => !JobKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"Unknown analysis job{(unknown.Length == 1 ? "" : "s")} {string.Join(", ", unknown.Select(k => $"'{k}'"))}. " +
                $"Choose from: {string.Join(", ", JobKeys)}.", paramName);
    }

    /// <summary>
    /// Assemble the standard job set for a case. <paramref name="enabled"/> filters by key (see <see cref="JobKeys"/>);
    /// null = all on. A non-null set must name at least one job and only known keys, else <see cref="ArgumentException"/>
    /// (D-DOC-081: an unknown key used to build an empty job set, which every cycle then "passed").
    /// </summary>
    public static IReadOnlyList<AnalysisJob> Build(
        GridModel model, CycleOptions opt, IReadOnlySet<string>? enabled = null, PowerFlowOptions? solveOpt = null)
    {
        if (enabled is not null) RequireKnownKeys(enabled, nameof(enabled));
        bool On(string key) => enabled is null || enabled.Contains(key);
        var jobs = new List<AnalysisJob>();
        if (On("watch")) jobs.Add(WatchList());
        if (On("n-1") || On("nminus1"))
        {
            jobs.Add(NMinus1Hot(opt, solveOpt));
            jobs.AddRange(NMinus1Shards(model, opt, solveOpt));
        }
        if (On("harmonics")) jobs.Add(Harmonics(opt));
        if (On("rocof")) jobs.Add(RocofScreen());
        if (On("physics") || On("conformance")) jobs.Add(PhysicsAudit());
        // Belt and braces for D-DOC-081: known keys in a set with a case-SENSITIVE comparer ("N-1") pass the check above
        // but match no job here. An empty job set is never a valid cycle, so refuse it rather than "pass" every cycle.
        if (jobs.Count == 0)
            throw new ArgumentException(
                "The job selection built no analysis job (the keys are matched with the set's own comparer; build the set " +
                "with StringComparer.OrdinalIgnoreCase, as ParseJobKeys does).", nameof(enabled));
        return jobs;
    }

    /// <summary>
    /// Fold the cycle's deadline token into the solve options so a single long solve inside a deferred
    /// job also aborts mid-iteration (not just between outages). Returns the options unchanged when the
    /// token can't be cancelled (High jobs pass None), so their solves stay allocation-free and bit-for-bit.
    /// </summary>
    private static PowerFlowOptions? WithCancellation(PowerFlowOptions? opt, System.Threading.CancellationToken ct)
        => ct.CanBeCanceled ? (opt ?? new PowerFlowOptions()) with { Cancellation = ct } : opt;
}
