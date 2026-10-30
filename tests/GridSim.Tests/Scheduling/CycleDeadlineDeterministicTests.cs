using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests.Scheduling;

/// <summary>
/// N033 (October 2026): a load-independent form of SolverCancellationTests.Low_job_that_honours_the_deadline_is_cancelled_high_is_not.
///
/// That test arms the scheduler's 150 ms wall-clock deadline and expects the spinning Low job to be Cancelled. It failed
/// once under heavy machine load: the scheduler drains the High jobs first and only then the Low ones, so when the High
/// drain (thread-pool start-up included) took longer than 150 ms the deadline had already passed before the Low job was
/// pulled, and the Low job came back Deferred (never started), not Cancelled. The scheduler did the right thing; the
/// test raced the wall clock. As agreed with Mark, that test is left exactly as it is.
///
/// These tests prove the same contract with no wall clock at all. The scheduler links the caller's token into its
/// deadline token (RunCycle's outerCt), so cancelling that token is exactly what the deadline timer does, at a moment
/// the test controls. The window is 0 (no timer armed), so nothing depends on how fast the machine is:
/// <list type="bullet">
/// <item>a Low job that honours the token, already running when the cycle is cancelled, comes back Cancelled, while a
/// High job gets an uncancellable token and completes;</item>
/// <item>a cancellation that arrives while the High jobs are still running does not stop them (High always completes),
/// and no Low job is started after it (the Low job comes back Deferred): the exact behaviour behind the original race.</item>
/// </list>
/// </summary>
public class CycleDeadlineDeterministicTests
{
    private static SimFrame Frame()
    {
        GridModel m = IeeeCases.Case9();
        (IPowerFlowSolver pf, _, _) = SolverSelector.Create(SolverKind.Auto, m);
        PowerFlowResult r = pf.Solve(m);
        return new SimFrame(m, r, 0, 0, 50, r.PInjMw.Where(p => p > 0).Sum(), m.Buses.Sum(b => b.Pd));
    }

    private static readonly Func<IPowerFlowSolver> Factory = () => new NewtonRaphsonPowerFlow();

    [Fact]
    public async Task Running_low_job_is_cancelled_by_the_cycle_token_high_job_is_not()
    {
        using var lowStarted = new ManualResetEventSlim();
        using var cycleCts = new CancellationTokenSource();
        bool highSawCancellable = true;
        var jobs = new List<AnalysisJob>
        {
            new()
            {
                Id = "hp", Name = "hp", Priority = JobPriority.High, SeedCoreMs = 1,
                Run = (_, ct) => { highSawCancellable = ct.CanBeCanceled; return "hp-ok"; },   // High gets None
            },
            new()
            {
                Id = "lp", Name = "lp", Priority = JobPriority.Low, SeedCoreMs = 1,
                Run = (_, ct) =>
                {
                    lowStarted.Set();
                    // Wait (not spin) for the cancellation; the 60 s cap only stops a broken scheduler hanging the run.
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(60));
                    ct.ThrowIfCancellationRequested();
                    return "lp-finished-without-cancellation";
                },
            },
        };
        // The "deadline": cancel the cycle token once, and only once, the Low job is running.
        Task canceller = Task.Run(() =>
        {
            if (lowStarted.Wait(TimeSpan.FromSeconds(60))) cycleCts.Cancel();
        });

        var sched = new CycleScheduler(new CycleOptions { Workers = 2, WindowMs = 0 }, jobs);
        CycleResult cr = sched.RunCycle(Frame(), Factory, 0, outerCt: cycleCts.Token);
        await canceller.WaitAsync(TimeSpan.FromSeconds(60));   // throws TimeoutException if the canceller never ran

        Assert.False(highSawCancellable);                    // High was handed CancellationToken.None
        Assert.True(cr.HighTierMet);                         // and completed
        Assert.Equal(JobOutcome.Completed, cr.Results.First(x => x.Id == "hp").Outcome);
        JobResult lp = cr.Results.First(x => x.Id == "lp");
        Assert.Equal(JobOutcome.Cancelled, lp.Outcome);      // Low aborted at the (test-controlled) deadline
        Assert.Equal(1, cr.LowCancelled);
    }

    [Fact]
    public void Cancellation_during_the_high_drain_does_not_stop_high_and_starts_no_low_job()
    {
        using var cycleCts = new CancellationTokenSource();
        bool lowRan = false;
        var jobs = new List<AnalysisJob>
        {
            new()
            {
                Id = "hp", Name = "hp", Priority = JobPriority.High, SeedCoreMs = 1,
                // The deadline passes while the High job is still running (the case that raced in the original test).
                Run = (_, ct) => { cycleCts.Cancel(); Assert.False(ct.IsCancellationRequested); return "hp-ok"; },
            },
            new()
            {
                Id = "lp", Name = "lp", Priority = JobPriority.Low, SeedCoreMs = 1,
                Run = (_, _) => { lowRan = true; return "lp"; },
            },
        };

        var sched = new CycleScheduler(new CycleOptions { Workers = 2, WindowMs = 0 }, jobs);
        CycleResult cr = sched.RunCycle(Frame(), Factory, 0, outerCt: cycleCts.Token);

        Assert.True(cr.HighTierMet);                                                   // High always completes
        Assert.Equal(JobOutcome.Completed, cr.Results.First(x => x.Id == "hp").Outcome);
        Assert.False(lowRan);                                                          // no Low job started after the deadline
        Assert.Equal(JobOutcome.Deferred, cr.Results.First(x => x.Id == "lp").Outcome); // kept owed for a later cycle
        Assert.Equal(0, cr.LowCancelled);
    }
}
