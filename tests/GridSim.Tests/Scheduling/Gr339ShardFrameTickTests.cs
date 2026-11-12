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
/// GR-339: a resumed N-1 shard keeps the outages it screened before the deadline and finishes on a later frame, so one
/// shard can mix frames. Each result now carries the tick of the frame it was screened on (ContingencyResult.FrameTick)
/// and AnalysisJobLibrary.FrameRange / FrameRangeText report the shard's range. The screened numbers are unchanged.
/// </summary>
public class Gr339ShardFrameTickTests
{
    private sealed class TrippingSolver : IPowerFlowSolver
    {
        private readonly IPowerFlowSolver _inner = new NewtonRaphsonPowerFlow();
        private readonly CancellationTokenSource? _trip;
        private readonly int _tripAfter;
        public int Calls;
        public TrippingSolver(CancellationTokenSource? trip = null, int tripAfter = int.MaxValue)
        { _trip = trip; _tripAfter = tripAfter; }
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
        {
            PowerFlowResult r = _inner.Solve(model, options);
            if (++Calls == _tripAfter) _trip?.Cancel();
            return r;
        }
    }

    private static SimFrame Frame(GridModel m, int tick)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        return new SimFrame(m, r, 0, tick, 50.0, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    }

    private static CycleContext Ctx(SimFrame f, IPowerFlowSolver solver) =>
        new() { Frame = f, CycleIndex = 0, SolverFactory = () => solver };

    [Fact]
    public void Gr339_a_shard_resumed_on_a_later_frame_labels_each_outage_with_its_own_frame()
    {
        GridModel m = IeeeCases.Case9();
        AnalysisJob shard = AnalysisJobLibrary.NMinus1Shards(m, new CycleOptions { NMinus1Shards = 1 }).Single();

        using var deadline = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(
            () => shard.Run(Ctx(Frame(m, 11), new TrippingSolver(deadline, tripAfter: 4)), deadline.Token));
        object? payload = shard.Run(Ctx(Frame(m, 12), new TrippingSolver()), CancellationToken.None);
        var results = (IReadOnlyList<ContingencyResult>)payload!;

        Assert.Equal(9, results.Count);
        Assert.All(results.Take(4), c => Assert.Equal(11, c.FrameTick));
        Assert.All(results.Skip(4), c => Assert.Equal(12, c.FrameTick));
        Assert.Equal<(int, int)?>((11, 12), AnalysisJobLibrary.FrameRange(payload));
        Assert.Equal("frames 11..12", AnalysisJobLibrary.FrameRangeText(payload));

        // The numbers are those of an uninterrupted screen, bit for bit (same model on both frames).
        IReadOnlyList<ContingencyResult> direct = ContingencyAnalysis.ScreenBranchesParallel(
            m, () => new NewtonRaphsonPowerFlow(), maxDegreeOfParallelism: 1);
        Assert.Equal(direct.Count, results.Count);
        for (int i = 0; i < direct.Count; i++)
        {
            Assert.Null(direct[i].FrameTick);
            Assert.Equal(direct[i].Outage, results[i].Outage);
            Assert.Equal(direct[i].Converged, results[i].Converged);
            Assert.Equal(direct[i].MaxBranchLoadingPercent, results[i].MaxBranchLoadingPercent);
            Assert.Equal(direct[i].WorstBoundaryLoadingPercent, results[i].WorstBoundaryLoadingPercent);
            Assert.Equal(direct[i].MinVm, results[i].MinVm);
            Assert.Equal(direct[i].MaxVm, results[i].MaxVm);
            Assert.Equal(direct[i].Violations, results[i].Violations);
        }
    }

    [Fact]
    public void Gr339_a_shard_completed_on_one_frame_reports_that_single_frame()
    {
        GridModel m = IeeeCases.Case9();
        AnalysisJob shard = AnalysisJobLibrary.NMinus1Shards(m, new CycleOptions { NMinus1Shards = 1 }).Single();
        object? payload = shard.Run(Ctx(Frame(m, 7), new TrippingSolver()), CancellationToken.None);
        Assert.All((IReadOnlyList<ContingencyResult>)payload!, c => Assert.Equal(7, c.FrameTick));
        Assert.Equal<(int, int)?>((7, 7), AnalysisJobLibrary.FrameRange(payload));
        Assert.Equal("frame 7", AnalysisJobLibrary.FrameRangeText(payload));
    }

    [Fact]
    public void Gr339_the_web_cycle_projection_carries_each_n1_jobs_frame_range()
    {
        ContingencyResult At(string outage, int tick) =>
            new ContingencyResult(outage, true, 50, 40, 0.98, 1.02, Array.Empty<string>()) { FrameTick = tick };
        JobResult Job(string id, JobPriority p, object? payload) => new(id, id, p, JobOutcome.Completed, 1.0, payload, false);
        var jobs = new List<JobResult>
        {
            Job("n-1:hot", JobPriority.High, new[] { At("L1", 20), At("L2", 20) }),
            Job("n-1:lp:0", JobPriority.Low, new[] { At("L3", 18), At("L4", 19), At("L5", 20) }),
            Job("rocof", JobPriority.Low, null),
        };
        var r = new CycleResult(7, 3.25, 41.5, 2, 2, 1, 1, 0, 0, 400, 123.4, 0, true, jobs);

        GridSim.Web.Projection.CycleDto dto = GridSim.Web.Projection.CycleProjection.Project(r, 1, 1, 2);

        Assert.Equal("frame 20", dto.Jobs.Single(j => j.Name == "n-1:hot").Frames);
        Assert.Equal("frames 18..20", dto.Jobs.Single(j => j.Name == "n-1:lp:0").Frames);
        Assert.Null(dto.Jobs.Single(j => j.Name == "rocof").Frames);
    }

    [Fact]
    public void Gr339_a_payload_without_contingencies_or_ticks_has_no_range()
    {
        Assert.Null(AnalysisJobLibrary.FrameRange(null));
        Assert.Null(AnalysisJobLibrary.FrameRange("not a contingency list"));
        Assert.Null(AnalysisJobLibrary.FrameRange(Array.Empty<ContingencyResult>()));
        var untagged = new[] { new ContingencyResult("x", true, 0, 0, 1, 1, Array.Empty<string>()) };
        Assert.Null(AnalysisJobLibrary.FrameRange(untagged));
        Assert.Null(AnalysisJobLibrary.FrameRangeText(untagged));
    }
}
