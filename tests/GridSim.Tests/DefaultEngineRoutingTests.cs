using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R004 / R109 (October 2026 review): the analysis entry points (contingency screens, loss contingency, AC-OPF,
/// continuation, tap optimiser, restoration) and the parameterless <see cref="SteadyStateSolver"/> (REST /solve,
/// viz export) defaulted to the DENSE Newton engine for every model, so gb-full ran dense. They now default to
/// the shared rule (<see cref="SolverSelector.CreateNewton"/>: sparse past 200 buses). The engines are
/// bit-identical on the CPU, so the change must be number-neutral: these tests prove it on case300 (300 buses,
/// so the default is now the SPARSE engine) against an explicit dense engine.
/// </summary>
public class DefaultEngineRoutingTests
{
    private static GridModel Case300() => IeeeCases.Case300();

    private static void SameBits(double a, double b, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), $"{what}: {a:R} vs {b:R}");

    private static void SameResult(PowerFlowResult a, PowerFlowResult b, string what)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        SameBits(a.MaxMismatch, b.MaxMismatch, what + " mismatch");
        for (int i = 0; i < a.Vm.Length; i++)
        {
            SameBits(a.Vm[i], b.Vm[i], $"{what} Vm[{i}]");
            SameBits(a.VaDeg[i], b.VaDeg[i], $"{what} Va[{i}]");
        }
        SameBits(a.TotalLossMw, b.TotalLossMw, what + " loss");
    }

    private static void SameContingency(ContingencyResult a, ContingencyResult b)
    {
        Assert.Equal(a.Outage, b.Outage);
        Assert.Equal(a.Converged, b.Converged);
        SameBits(a.MaxBranchLoadingPercent, b.MaxBranchLoadingPercent, a.Outage + " loading");
        SameBits(a.WorstBoundaryLoadingPercent, b.WorstBoundaryLoadingPercent, a.Outage + " boundary");
        SameBits(a.MinVm, b.MinVm, a.Outage + " minVm");
        SameBits(a.MaxVm, b.MaxVm, a.Outage + " maxVm");
        Assert.Equal(a.Violations, b.Violations);
    }

    [Fact]
    public void Case300_now_defaults_to_the_sparse_engine()
    {
        Assert.Equal(SolverKind.SparseNewton, SolverSelector.ResolveNewton(Case300()));
    }

    [Fact]
    public void Parameterless_steady_state_solver_matches_the_dense_engine_bit_for_bit()
    {
        GridModel m = Case300();
        var opt = new PowerFlowOptions { EnforceQLimits = true };
        PowerFlowResult byDefault = new SteadyStateSolver().Solve(m, opt);
        PowerFlowResult dense = new SteadyStateSolver(new NewtonRaphsonPowerFlow()).Solve(m, opt);
        SameResult(byDefault, dense, "SteadyStateSolver()");
    }

    [Fact]
    public void Contingency_screens_default_engine_matches_the_dense_engine_bit_for_bit()
    {
        GridModel m = Case300();
        int[] outages = { 0, 7, 50, 120, 300 };

        foreach (int k in outages)
            SameContingency(
                ContingencyAnalysis.ScreenOutage(m, new[] { k }),
                ContingencyAnalysis.ScreenOutage(m, new[] { k }, new NewtonRaphsonPowerFlow()));

        IReadOnlyList<ContingencyResult> par = ContingencyAnalysis.ScreenBranchesParallel(m, branchIndices: outages);
        IReadOnlyList<ContingencyResult> parDense = ContingencyAnalysis.ScreenBranchesParallel(
            m, () => new NewtonRaphsonPowerFlow(), branchIndices: outages);
        Assert.Equal(par.Count, parDense.Count);
        for (int i = 0; i < par.Count; i++) SameContingency(par[i], parDense[i]);
    }

    [Fact]
    public void Loss_contingency_default_engine_matches_the_dense_engine_bit_for_bit()
    {
        GridModel m = Case300();   // above the threshold: the default is the sparse engine
        PowerFlowResult baseCase = new NewtonRaphsonPowerFlow().Solve(m);
        // P0 (October 2026, suite runtime): the same two serial sweeps as before (the default engine and the dense
        // engine, each LossContingency.Sweep on its own solver instance), but run side by side on two threads rather
        // than one after the other; the comparison below is unchanged. Each sweep only reads the model and the base
        // case (both immutable) and owns its solver, so its results do not depend on the other running. The dense
        // sweep over case300's 411 outages took about 100 seconds and ended this class's run of tests.
        // Parallel.Invoke returns only when both sweeps have finished and rethrows any failure (fail loud).
        IReadOnlyList<LossContingencyResult> a = null!, b = null!;
        Parallel.Invoke(
            () => a = LossContingency.Sweep(m, baseCase),
            () => b = LossContingency.Sweep(m, baseCase, new NewtonRaphsonPowerFlow()));
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Outage, b[i].Outage);
            Assert.Equal(a[i].Converged, b[i].Converged);
            SameBits(a[i].PostLossMw, b[i].PostLossMw, a[i].Outage + " post loss");
            SameBits(a[i].MaxBranchLoadingPercent, b[i].MaxBranchLoadingPercent, a[i].Outage + " loading");
        }
    }
}
