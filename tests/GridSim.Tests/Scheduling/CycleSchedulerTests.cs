using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests.Scheduling;

/// <summary>
/// Validates the cycle scheduler's contract: High always completes, Low is budget-gated and rotates
/// to full coverage without starvation, results are worker-count-independent, and the shared frame is
/// never mutated by the fan-out.
/// </summary>
public class CycleSchedulerTests
{
    // ---- a deterministic frame built from a solved case (no LiveSim RNG) --------------------------
    private static (SimFrame frame, Func<IPowerFlowSolver> factory) SolvedFrame(GridModel m)
    {
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);
        PowerFlowResult r = pf.Solve(m);
        double gen = r.PInjMw.Where(p => p > 0).Sum();
        double demand = m.Buses.Sum(b => b.Pd);
        var frame = new SimFrame(m, r, 0, 0, 50.0, gen, demand);
        SolverKind kind = SolverSelector.Resolve(m);
        Func<IPowerFlowSolver> factory = kind == SolverKind.SparseNewton
            ? () => new SparseNewtonRaphsonPowerFlow()
            : () => new NewtonRaphsonPowerFlow();
        return (frame, factory);
    }

    // ---- synthetic jobs with known seed costs (admission is deterministic from seeds) -------------
    private static AnalysisJob Synthetic(string id, JobPriority p, double seedMs, Action? work = null) => new()
    {
        Id = id,
        Name = id,
        Priority = p,
        SeedCoreMs = seedMs,
        Run = (_, _) => { work?.Invoke(); return id; },
    };

    [Fact]
    public void Budget_is_workers_times_window_core_ms()
    {
        var b = new CycleBudget(workers: 28, windowMs: 1000);
        Assert.Equal(28_000, b.TotalCoreMs);
        b.Record(500);
        Assert.Equal(27_500, b.Remaining);
        Assert.True(b.CanAdmit(1000));
    }

    [Fact]
    public void High_always_admitted_low_gated_by_budget()
    {
        // workers=1, window=100ms => 100 core-ms budget. HP seed 50, five LP seed 30 each.
        // Admission reserves 50 for HP, then admits LP while running+30 <= 100 => exactly one LP.
        var jobs = new List<AnalysisJob> { Synthetic("hp", JobPriority.High, 50) };
        for (int i = 0; i < 5; i++) jobs.Add(Synthetic($"lp{i}", JobPriority.Low, 30));

        var (frame, factory) = SolvedFrame(IeeeCases.Case9());
        var opt = new CycleOptions { Workers = 1, WindowMs = 100 };
        var sched = new CycleScheduler(opt, jobs);

        CycleResult r = sched.RunCycle(frame, factory, baseSolveMs: 0);

        Assert.True(r.HighTierMet);
        Assert.Equal(1, r.HighCompleted);
        Assert.Equal(1, r.LowAdmitted);
        Assert.Equal(1, r.LowCompleted);
        Assert.Equal(4, r.LowDeferred);
    }

    [Fact]
    public void Low_tier_rotates_to_full_coverage_without_starvation()
    {
        // Eight LP shards, budget admits ~one per cycle; over enough cycles every shard must run.
        var jobs = new List<AnalysisJob> { Synthetic("hp", JobPriority.High, 50) };
        for (int i = 0; i < 8; i++) jobs.Add(Synthetic($"lp{i}", JobPriority.Low, 30));

        var (frame, factory) = SolvedFrame(IeeeCases.Case9());
        var opt = new CycleOptions { Workers = 1, WindowMs = 100 }; // ~1 LP admitted per cycle
        var sched = new CycleScheduler(opt, jobs);

        var everCompleted = new HashSet<string>();
        for (int c = 0; c < 20; c++)
        {
            CycleResult r = sched.RunCycle(frame, factory, 0);
            Assert.True(r.HighTierMet); // HP never starves, every cycle
            foreach (JobResult jr in r.Results)
                if (jr.Priority == JobPriority.Low && jr.Outcome == JobOutcome.Completed)
                    everCompleted.Add(jr.Id);
        }

        for (int i = 0; i < 8; i++)
            Assert.Contains($"lp{i}", everCompleted); // full coverage achieved by rotation
    }

    [Fact]
    public void Results_are_independent_of_worker_count()
    {
        GridModel m = IeeeCases.Case118();
        var opt = new CycleOptions { WindowMs = 60_000, NMinus1Shards = 8, HotBranchLoadingPercent = 200 };
        var enabled = new HashSet<string> { "n-1" };

        Dictionary<string, bool> Run(int workers)
        {
            var (frame, factory) = SolvedFrame(m);
            var o = opt with { Workers = workers };
            var sched = new CycleScheduler(o, AnalysisJobLibrary.Build(m, o, enabled));
            CycleResult r = sched.RunCycle(frame, factory, 0);
            var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (JobResult jr in r.Results)
                if (jr.PayloadAs<IReadOnlyList<ContingencyResult>>() is { } list)
                    foreach (ContingencyResult cr in list) verdicts[cr.Outage] = cr.Secure;
            return verdicts;
        }

        Dictionary<string, bool> one = Run(1);
        Dictionary<string, bool> many = Run(8);

        Assert.NotEmpty(one);
        Assert.Equal(one.Count, many.Count);
        foreach (KeyValuePair<string, bool> kv in one)
            Assert.Equal(kv.Value, many[kv.Key]); // same verdict regardless of worker count / completion order
    }

    [Fact]
    public void Fan_out_never_mutates_the_shared_frame()
    {
        GridModel m = IeeeCases.Case118();
        var (frame, factory) = SolvedFrame(m);
        double[] vmBefore = (double[])frame.Result.Vm.Clone();
        int branchesBefore = frame.Model.Branches.Count;

        var opt = new CycleOptions { Workers = 8, WindowMs = 60_000 };
        var sched = new CycleScheduler(opt, AnalysisJobLibrary.Build(m, opt));
        sched.RunCycle(frame, factory, 0);

        Assert.Equal(vmBefore, frame.Result.Vm); // byte-identical: no job wrote through the shared arrays
        Assert.Equal(branchesBefore, frame.Model.Branches.Count);
    }

    [Fact]
    public void Faulting_job_is_isolated_and_the_cycle_continues()
    {
        var jobs = new List<AnalysisJob>
        {
            Synthetic("ok", JobPriority.High, 1),
            new()
            {
                Id = "boom", Name = "boom", Priority = JobPriority.High, SeedCoreMs = 1,
                Run = (_, _) => throw new InvalidOperationException("kaboom"),
            },
        };
        var (frame, factory) = SolvedFrame(IeeeCases.Case9());
        var opt = new CycleOptions { Workers = 2, WindowMs = 1000 };
        var sched = new CycleScheduler(opt, jobs);

        CycleResult r = sched.RunCycle(frame, factory, 0);

        Assert.Contains(r.Results, x => x.Id == "ok" && x.Outcome == JobOutcome.Completed);
        JobResult boom = r.Results.First(x => x.Id == "boom");
        Assert.Equal(JobOutcome.Faulted, boom.Outcome);
        Assert.Contains("kaboom", boom.Error);
    }
}
