using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests.Scheduling;

/// <summary>
/// Phase 2: the power-flow engines honour a cancellation token cooperatively, and - the invariant that
/// matters most - a default (None) token leaves the solve bit-for-bit identical to no token at all.
/// </summary>
public class SolverCancellationTests
{
    [Theory]
    [InlineData("9")]
    [InlineData("118")]
    public void Default_token_is_bit_for_bit_identical(string which)
    {
        GridModel m = which == "9" ? IeeeCases.Case9() : IeeeCases.Case118();
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);

        PowerFlowResult baseline = pf.Solve(m); // no options at all
        PowerFlowResult withDefault = pf.Solve(m, new PowerFlowOptions()); // Cancellation == None

        Assert.Equal(baseline.Converged, withDefault.Converged);
        Assert.Equal(baseline.Iterations, withDefault.Iterations);
        Assert.Equal(baseline.Vm, withDefault.Vm);       // exact array equality: no numerical drift
        Assert.Equal(baseline.VaDeg, withDefault.VaDeg);
    }

    [Fact]
    public void Cancelled_token_aborts_the_solve()
    {
        GridModel m = IeeeCases.Case118();
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled before the first iteration

        Assert.Throws<OperationCanceledException>(
            () => { pf.Solve(m, new PowerFlowOptions { Cancellation = cts.Token }); });
    }

    [Fact]
    public void Low_job_that_honours_the_deadline_is_cancelled_high_is_not()
    {
        // A Low job that spins on the token must be cancelled at the window; a High job (token None) must not.
        var lowStarted = new ManualResetEventSlim();
        var jobs = new List<AnalysisJob>
        {
            new()
            {
                Id = "hp", Name = "hp", Priority = JobPriority.High, SeedCoreMs = 1,
                Run = (_, ct) => { Assert.False(ct.CanBeCanceled); return "hp-ok"; }, // High gets None
            },
            new()
            {
                Id = "lp", Name = "lp", Priority = JobPriority.Low, SeedCoreMs = 1,
                Run = (_, ct) =>
                {
                    lowStarted.Set();
                    while (true) { ct.ThrowIfCancellationRequested(); Thread.SpinWait(1000); }
                },
            },
        };

        GridModel m = IeeeCases.Case9();
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);
        PowerFlowResult r = pf.Solve(m);
        var frame = new SimFrame(m, r, 0, 0, 50, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
        Func<IPowerFlowSolver> factory = () => new NewtonRaphsonPowerFlow();

        var opt = new CycleOptions { Workers = 2, WindowMs = 150 };
        var sched = new CycleScheduler(opt, jobs);
        CycleResult cr = sched.RunCycle(frame, factory, 0);

        Assert.True(cr.HighTierMet); // HP completed, never cancelled
        JobResult lp = cr.Results.First(x => x.Id == "lp");
        Assert.Equal(JobOutcome.Cancelled, lp.Outcome); // LP aborted at the deadline
    }
}
