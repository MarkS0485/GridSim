using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class LossAllocationTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    private static (GridModel Model, PowerFlowResult Result) SolveUk()
    {
        var m = Uk();
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return (m, r);
    }

    [Fact]
    public void Branch_losses_sum_to_total()
    {
        var (m, r) = SolveUk();
        var alloc = LossAllocation.Allocate(m, r);
        Assert.Equal(r.LineFlows.Count, alloc.BranchLosses.Count);
        Assert.InRange(Math.Abs(alloc.ReconciliationResidualMw), 0, 1e-6);
    }

    [Fact]
    public void Zone_losses_plus_other_equal_total()
    {
        var (m, r) = SolveUk();
        var alloc = LossAllocation.Allocate(m, r);
        double zoneSum = alloc.ZoneLosses.Sum(z => z.PLossMw);
        Assert.InRange(Math.Abs(zoneSum - alloc.TotalPLossMw), 0, 1e-6);
    }

    [Fact]
    public void Every_branch_loss_is_non_negative()
    {
        var (m, r) = SolveUk();
        var alloc = LossAllocation.Allocate(m, r);
        foreach (var bl in alloc.BranchLosses)
            Assert.True(bl.PLossMw >= -1e-6, $"Negative loss on {bl.Name}: {bl.PLossMw}");
    }
}

public class LossContingencyTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    [Fact]
    public void Sweep_covers_every_branch()
    {
        var m = Uk();
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        var results = LossContingency.Sweep(m, r);
        Assert.Equal(m.Branches.Count, results.Count);
    }

    [Fact]
    public void Converged_outages_report_positive_base_loss()
    {
        var m = Uk();
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        var results = LossContingency.Sweep(m, r);
        foreach (var lr in results.Where(x => x.Converged))
            Assert.True(lr.BaseLossMw > 0);
    }

    [Fact]
    public void Parallel_agrees_with_sequential()
    {
        var m = Uk();
        var r = new NewtonRaphsonPowerFlow().Solve(m);
        var seq = LossContingency.Sweep(m, r);
        var par = LossContingency.SweepParallel(m, r, maxDegreeOfParallelism: 2);
        Assert.Equal(seq.Count, par.Count);
        for (int i = 0; i < seq.Count; i++)
        {
            Assert.Equal(seq[i].Outage, par[i].Outage);
            Assert.Equal(seq[i].Converged, par[i].Converged);
            if (seq[i].Converged)
                Assert.InRange(Math.Abs(seq[i].DeltaLossMw - par[i].DeltaLossMw), 0, 1e-6);
        }
    }
}
