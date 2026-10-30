using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), scheduling findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    private static SimFrame BHSchedFrame()
    {
        GridModel m = IeeeCases.Case9();
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);
        PowerFlowResult r = pf.Solve(m);
        return new SimFrame(m, r, 0, 0, 50, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH60: WindowMs = 0 ("no deadline") must not starve the Low rotation with a zero budget.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH60_a_zero_window_admits_every_low_job_in_one_cycle()
    {
        // Five Low jobs, each seeded at 10 core-ms. With the zero budget the old code formed, only the first was
        // admitted per cycle (the "at least one" rule) and the other four were deferred.
        var jobs = Enumerable.Range(0, 5).Select(i => new AnalysisJob
        {
            Id = $"low{i}", Name = $"low{i}", Priority = JobPriority.Low, SeedCoreMs = 10,
            Run = (_, _) => "ok",
        }).ToList();
        var sched = new CycleScheduler(new CycleOptions { Workers = 2, WindowMs = 0 }, jobs);
        CycleResult cr = sched.RunCycle(BHSchedFrame(), () => new NewtonRaphsonPowerFlow(), 0);

        Assert.Equal(5, cr.LowAdmitted);
        Assert.Equal(5, cr.LowCompleted);
        Assert.Equal(0, cr.LowDeferred);
        Assert.True(double.IsPositiveInfinity(cr.BudgetTotalCoreMs));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH61: duplicate job ids are refused by name (the rotation keys on the id).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH61_two_jobs_sharing_an_id_are_refused()
    {
        var jobs = new List<AnalysisJob>
        {
            new() { Id = "dup", Name = "a", Priority = JobPriority.Low, SeedCoreMs = 1, Run = (_, _) => "a" },
            new() { Id = "dup", Name = "b", Priority = JobPriority.Low, SeedCoreMs = 1, Run = (_, _) => "b" },
        };
        var ex = Assert.Throws<ArgumentException>(() => new CycleScheduler(new CycleOptions(), jobs));
        Assert.Contains("'dup'", ex.Message, StringComparison.Ordinal);
    }
}
