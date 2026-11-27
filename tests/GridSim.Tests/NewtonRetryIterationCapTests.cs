using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The DC-start retry and the iteration cap (2.1.074). <see cref="PowerFlowOptions.MaxIterations"/> is the caller's budget:
/// a Newton run that the cap stops while it is still converging is not solved a second time. Before 2.1.074 only the
/// thousandfold cut from the start point (<see cref="NewtonShell.RetryStallFactor"/>) said "still converging", so a run
/// stopped after 0 or 2 steps was retried: case9 with MaxIterations 2 and Tolerance 1e-4 was reported Converged after four
/// Newton steps. A run that has taken at least two steps and halved its mismatch on the last one
/// (<see cref="NewtonShell.RetryLastStepFactor"/>) is now judged still converging, and a cap of 0 (no step allowed) is
/// never retried.
/// </summary>
public class NewtonRetryIterationCapTests
{
    private static IEnumerable<IPowerFlowSolver> Engines()
    {
        yield return new NewtonRaphsonPowerFlow();
        yield return new SparseNewtonRaphsonPowerFlow();
    }

    private static void SameBits(PowerFlowResult a, PowerFlowResult b)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MaxMismatch), BitConverter.DoubleToInt64Bits(b.MaxMismatch));
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    /// <summary>The audit scenario: case9 from its (flat) file start, MaxIterations 2, Tolerance 1e-4. The ordinary solve
    /// ends at a mismatch of about 2.15e-3 pu, cut about 85-fold by its last step; the DC-start retry would converge within a
    /// second budget of 2 steps. The result must be the ordinary solve's: DidNotConverge (status MaxIterations), 2 steps,
    /// no retry.</summary>
    [Fact]
    public void Case9_capped_at_two_steps_with_a_loose_tolerance_reports_did_not_converge_without_a_retry()
    {
        GridModel m = IeeeCases.Case9();
        foreach (IPowerFlowSolver s in Engines())
            foreach (PowerFlowOptions o in new[]
                     {
                         new PowerFlowOptions { MaxIterations = 2, Tolerance = 1e-4 },
                         new PowerFlowOptions { MaxIterations = 2, Tolerance = 1e-4, FlatStart = true, EnforceQLimits = false },
                     })
            {
                PowerFlowResult r = s.Solve(m, o);
                Assert.False(r.Converged);
                Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
                Assert.Equal(2, r.Iterations);
                Assert.True(r.MaxMismatch > 1e-4);
                Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
                Assert.Null(r.NewtonRetryNote);
                SameBits(s.Solve(m, o with { RetryFromDcStart = false }), r);
            }
    }

    /// <summary>Caps of 0 (no step allowed: the start point is only evaluated) and 2 on case9 and case14, at the default and
    /// a loose tolerance: never retried, the ordinary solve's bits. (A cap of 1 keeps its earlier classification: one step
    /// is all the history there is, so only the start-ratio test applies.)</summary>
    [Theory]
    [InlineData(9, 0)]
    [InlineData(9, 2)]
    [InlineData(14, 0)]
    [InlineData(14, 2)]
    public void Caps_of_zero_and_two_steps_are_never_retried(int bus, int cap)
    {
        GridModel m = bus == 9 ? IeeeCases.Case9() : IeeeCases.Case14();
        foreach (IPowerFlowSolver s in Engines())
            foreach (double tol in new[] { new PowerFlowOptions().Tolerance, 1e-4 })
            {
                var o = new PowerFlowOptions { MaxIterations = cap, Tolerance = tol, FlatStart = true };
                PowerFlowResult r = s.Solve(m, o);
                Assert.False(r.Converged);
                Assert.Equal(cap, r.Iterations);
                Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
                Assert.Null(r.NewtonRetryNote);
                SameBits(s.Solve(m, o with { RetryFromDcStart = false }), r);
            }
    }

    /// <summary>The run-end stall rule, case by case.</summary>
    [Fact]
    public void The_run_end_stall_rule_is_the_start_ratio_plus_the_last_step_ratio_at_the_cap()
    {
        Assert.Equal(0.5, NewtonShell.RetryLastStepFactor);
        // A step failed (singular or non-finite) before the cap: the start-ratio test alone, as before.
        Assert.True(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.01, 3, CapReached: false), 0.001001));
        Assert.False(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.01, 3, CapReached: false), 0.001));
        // Stopped by a cap of 0: no step was allowed, nothing to retry within the caller's budget.
        Assert.False(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, double.NaN, 0, CapReached: true), 1.0));
        // Stopped by a cap of 1: the last step is the first, its ratio is the start ratio; the start-ratio test alone.
        Assert.True(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 1.0, 1, CapReached: true), 0.115));
        // Stopped by the cap after two or more steps: still converging when the last step at least halved the mismatch.
        Assert.False(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.63, 0.1875, 2, CapReached: true), 2.15e-3));
        Assert.False(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.2, 50, CapReached: true), 0.1));
        Assert.True(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.2, 50, CapReached: true), 0.1000001));
        Assert.True(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.6, 50, CapReached: true), 0.5));
        Assert.True(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 0.6, 50, CapReached: true), double.NaN));
        // A thousandfold cut from the start is still converging, whatever the last step did.
        Assert.False(NewtonShell.Stalled(new NewtonShell.NewtonRunEnd(1.0, 1e-4, 50, CapReached: true), 1e-3));
    }

    /// <summary>The bundled failed solves the retry is for (flat start, Tolerance 1e-8, 50 steps) are still judged stalled
    /// and retried: their last step cuts the mismatch by far less than half.</summary>
    [Theory]
    [InlineData("nesta/pglib_opf_case162_ieee_dtc.m")]
    [InlineData("nesta/pglib_opf_case240_pserc.m")]
    [InlineData("nesta/pglib_opf_case3_lmbd.m")]
    public void The_documented_stalled_cases_are_still_retried(string file)
    {
        GridModel m = MatpowerParser.ParseFile(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data", "matpower", file));
        var o = new PowerFlowOptions { FlatStart = true, EnforceQLimits = false, Tolerance = 1e-8, MaxIterations = 50 };
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowResult off = s.Solve(m, o with { RetryFromDcStart = false });
            Assert.False(off.Converged);
            PowerFlowResult r = s.Solve(m, o);
            Assert.NotEqual(NewtonRetryOutcome.NotTried, r.NewtonRetry);
            Assert.NotNull(r.NewtonRetryNote);
        }
    }
}
