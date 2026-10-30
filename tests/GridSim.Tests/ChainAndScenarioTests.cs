using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tests for the consecutive-solve (warm start) plumbing and the parallel N-1 screen that the
/// server test regime relies on.
/// </summary>
public class ChainAndScenarioTests
{
    [Fact]
    public void WithSolvedVoltages_copies_solved_state_onto_buses()
    {
        GridModel m = IeeeCases.Case14();
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);

        GridModel warm = GridScenario.WithSolvedVoltages(m, r);
        var vm = new Dictionary<int, double>();
        var va = new Dictionary<int, double>();
        for (int i = 0; i < r.BusIds.Count; i++) { vm[r.BusIds[i]] = r.Vm[i]; va[r.BusIds[i]] = r.VaDeg[i]; }

        foreach (Bus b in warm.Buses)
        {
            Assert.Equal(vm[b.Id], b.Vm, 9);
            Assert.Equal(va[b.Id], b.VaDeg, 9);
        }
    }

    [Theory]
    [InlineData("14")]
    [InlineData("118")]
    public void Warm_started_resolve_converges_in_one_iteration_without_drift(string name)
    {
        GridModel m = name == "14" ? IeeeCases.Case14() : IeeeCases.Case118();
        var solver = new NewtonRaphsonPowerFlow();
        // Q-limits off so we measure the NR core's warm-start behaviour, not the PV->PQ outer
        // passes (which re-run regardless of the starting point).
        var opt = new PowerFlowOptions { EnforceQLimits = false };

        var first = solver.Solve(m, opt);
        Assert.True(first.Converged);

        // Feed the solved voltages back in and re-solve the identical operating point.
        var second = solver.Solve(GridScenario.WithSolvedVoltages(m, first), opt);
        Assert.True(second.Converged);
        Assert.True(second.Iterations <= 1, $"warm-started resolve took {second.Iterations} iterations");

        // No drift: the solution must be unchanged.
        for (int i = 0; i < first.Vm.Length; i++)
        {
            Assert.Equal(first.Vm[i], second.Vm[i], 8);
            Assert.Equal(first.VaDeg[i], second.VaDeg[i], 6);
        }
    }

    [Fact]
    public void ScreenBranchesParallel_matches_sequential()
    {
        GridModel m = IeeeCases.Case118();
        var seq = ContingencyAnalysis.ScreenBranches(m);
        var par = ContingencyAnalysis.ScreenBranchesParallel(m, () => new NewtonRaphsonPowerFlow());

        Assert.Equal(seq.Count, par.Count);
        // Results are returned in branch order by both, so compare positionally.
        for (int i = 0; i < seq.Count; i++)
        {
            Assert.Equal(seq[i].Outage, par[i].Outage);
            Assert.Equal(seq[i].Converged, par[i].Converged);
            Assert.Equal(seq[i].Secure, par[i].Secure);
        }
    }

    [Fact]
    public void ScreenBranchesParallel_sample_screens_only_requested_indices()
    {
        GridModel m = IeeeCases.Case118();
        int[] idx = { 0, 5, 10, 20 };
        var sampled = ContingencyAnalysis.ScreenBranchesParallel(m, () => new NewtonRaphsonPowerFlow(), branchIndices: idx);
        Assert.Equal(idx.Length, sampled.Count);
    }
}
