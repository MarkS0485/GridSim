using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// R042: a Low N-1 shard cut off at the deadline keeps its screened outages and resumes next cycle, so it
/// eventually completes (it used to restart from nothing every cycle and never finish on a large case), and a
/// cancelled run is not learned as a cost. R288: shard outages are keyed by branch identity, so a frame that
/// has lost branches (a dead island) never shifts results onto the wrong outage or runs off the end.
/// </summary>
public class ResumableShardTests
{
    // A solver that counts its solves and trips a cancellation source after the n-th one has RETURNED, so the
    // shard records exactly n results and then hits the deadline check before the next outage.
    private sealed class CountingSolver : IPowerFlowSolver
    {
        private readonly IPowerFlowSolver _inner = new NewtonRaphsonPowerFlow();
        private readonly CancellationTokenSource? _trip;
        private readonly int _tripAfter;
        public int Calls;
        public CountingSolver(CancellationTokenSource? trip = null, int tripAfter = int.MaxValue)
        { _trip = trip; _tripAfter = tripAfter; }
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
        {
            PowerFlowResult r = _inner.Solve(model, options);
            if (++Calls == _tripAfter) _trip?.Cancel();
            return r;
        }
    }

    private static SimFrame Frame(GridModel m)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        return new SimFrame(m, r, 0, 0, 50.0, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    }

    private static CycleContext Ctx(SimFrame f, IPowerFlowSolver solver) =>
        new() { Frame = f, CycleIndex = 0, SolverFactory = () => solver };

    private static void AssertSameResults(IReadOnlyList<ContingencyResult> expected, IReadOnlyList<ContingencyResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            ContingencyResult e = expected[i], a = actual[i];
            Assert.Equal(e.Outage, a.Outage);
            Assert.Equal(e.Converged, a.Converged);
            Assert.Equal(e.MaxBranchLoadingPercent, a.MaxBranchLoadingPercent);   // bit-identical
            Assert.Equal(e.WorstBoundaryLoadingPercent, a.WorstBoundaryLoadingPercent);
            Assert.Equal(e.MinVm, a.MinVm);
            Assert.Equal(e.MaxVm, a.MaxVm);
            Assert.Equal(e.Violations, a.Violations);
        }
    }

    [Fact]
    public void A_cancelled_shard_resumes_where_it_stopped_and_completes_with_identical_results()
    {
        GridModel m = IeeeCases.Case9();
        SimFrame frame = Frame(m);
        AnalysisJob shard = AnalysisJobLibrary.NMinus1Shards(m, new CycleOptions { NMinus1Shards = 1 }).Single();

        // Cycle 1: the deadline trips after 4 of the 9 outages.
        using var deadline = new CancellationTokenSource();
        var first = new CountingSolver(deadline, tripAfter: 4);
        Assert.ThrowsAny<OperationCanceledException>(() => shard.Run(Ctx(frame, first), deadline.Token));
        Assert.Equal(4, first.Calls);

        // Cycle 2: no deadline pressure. Only the remaining 5 outages are solved, and all 9 results come back.
        var second = new CountingSolver();
        var results = (IReadOnlyList<ContingencyResult>)shard.Run(Ctx(frame, second), CancellationToken.None)!;
        Assert.Equal(5, second.Calls);   // before R042: all 9 again, and on a big case never finishing

        // Bit-identical to an uninterrupted full screen.
        AssertSameResults(ContingencyAnalysis.ScreenBranchesParallel(m, () => new NewtonRaphsonPowerFlow(),
            maxDegreeOfParallelism: 1), results);

        // A completed shard starts its next pass from the beginning.
        var third = new CountingSolver();
        shard.Run(Ctx(frame, third), CancellationToken.None);
        Assert.Equal(9, third.Calls);
    }

    [Fact]
    public void A_cancelled_run_is_not_learned_as_the_jobs_cost()
    {
        var jobs = new List<AnalysisJob>
        {
            new()
            {
                Id = "lp", Name = "lp", Priority = JobPriority.Low, SeedCoreMs = 1,
                Run = (_, ct) => { while (true) { ct.ThrowIfCancellationRequested(); Thread.SpinWait(1000); } },
            },
        };
        var sched = new CycleScheduler(new CycleOptions { Workers = 1, WindowMs = 100 }, jobs);
        GridModel m = IeeeCases.Case9();
        CycleResult r = sched.RunCycle(Frame(m), () => new NewtonRaphsonPowerFlow(), 0);

        Assert.Equal(JobOutcome.Cancelled, r.Results.Single().Outcome);
        // Before R042 the ~100 ms truncated run was folded into the EWMA as if it were the job's real cost.
        Assert.Equal(1.0, sched.Costs.Estimate(jobs[0]));
    }

    [Fact]
    public void Shard_outages_follow_branch_identity_when_the_frame_has_lost_branches()
    {
        // Base model: case9 plus a radial spur 5-10 inserted in the MIDDLE of the branch list.
        GridModel c9 = IeeeCases.Case9();
        var buses = c9.Buses.ToList();
        buses.Add(new Bus(10, BusType.Pq, Pd: 5, Qd: 1, BaseKv: 345));
        var branches = c9.Branches.ToList();
        branches.Insert(2, new Branch(5, 10, 0.01, 0.05, 0, RateAMva: 100, Name: "spur"));
        GridModel withSpur = c9.CopyWith(buses: buses, branches: branches);

        // Shards are built from the base model (as CycleRunner does once), then the frame loses the spur's dead
        // island (what IslandPartition.Prepare does after a recloser holds the spur open).
        AnalysisJob shard = AnalysisJobLibrary.NMinus1Shards(withSpur, new CycleOptions { NMinus1Shards = 1 }).Single();
        GridModel frameModel = c9;   // the same branches, minus the spur: every index after 2 has shifted by one
        var results = (IReadOnlyList<ContingencyResult>)shard.Run(
            Ctx(Frame(frameModel), new NewtonRaphsonPowerFlow()), CancellationToken.None)!;

        // Before R288: fixed indices 0..9 on a 9-branch frame - results shifted onto the wrong outages, and
        // index 9 out of range faulted the shard. Now: each surviving branch screened once, correctly labelled.
        AssertSameResults(ContingencyAnalysis.ScreenBranchesParallel(frameModel, () => new NewtonRaphsonPowerFlow(),
            maxDegreeOfParallelism: 1), results);
    }

    [Fact]
    public void Parallel_twins_keep_distinct_identities()
    {
        // Two identical circuits on one pair are told apart by their occurrence number.
        GridModel c9 = IeeeCases.Case9();
        var branches = c9.Branches.ToList();
        branches.Add(c9.Branches[1]);   // a second, identical 4-5 circuit
        GridModel twinned = c9.CopyWith(branches: branches);
        AnalysisJob shard = AnalysisJobLibrary.NMinus1Shards(twinned, new CycleOptions { NMinus1Shards = 1 }).Single();
        var results = (IReadOnlyList<ContingencyResult>)shard.Run(
            Ctx(Frame(twinned), new NewtonRaphsonPowerFlow()), CancellationToken.None)!;
        Assert.Equal(10, results.Count);
        AssertSameResults(ContingencyAnalysis.ScreenBranchesParallel(twinned, () => new NewtonRaphsonPowerFlow(),
            maxDegreeOfParallelism: 1), results);
    }
}
