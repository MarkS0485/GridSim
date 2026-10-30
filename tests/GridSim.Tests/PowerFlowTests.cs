using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class PowerFlowTests
{
    // Published MATPOWER case9 solution (Vm in p.u., Va in degrees), bus order 1..9.
    private static readonly double[] ExpectedVm =
        { 1.0400, 1.0250, 1.0250, 1.0258, 1.0127, 1.0324, 1.0159, 1.0258, 0.9956 };
    private static readonly double[] ExpectedVaDeg =
        { 0.000, 9.280, 4.665, -2.217, -3.687, 1.967, 0.728, 3.720, -3.989 };

    [Fact]
    public void Case9_converges()
    {
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9());
        Assert.True(r.Converged);
        Assert.Equal(PowerFlowStatus.Converged, r.Status);
        Assert.True(r.Iterations <= 6, $"took {r.Iterations} iterations");
        Assert.True(r.MaxMismatch < 1e-8);
    }

    [Fact]
    public void Hitting_the_iteration_cap_reports_MaxIterations_not_converged()
    {
        // One iteration is nowhere near enough for case9: the solver must report an honest
        // non-converged MaxIterations outcome, never a false Converged with a stale mismatch.
        var r = new NewtonRaphsonPowerFlow().Solve(
            IeeeCases.Case9(), new PowerFlowOptions { MaxIterations = 1, FlatStart = true });
        Assert.False(r.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
    }

    [Fact]
    public void Case9_bus_voltages_match_published_solution()
    {
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9());
        for (int i = 0; i < r.Vm.Length; i++)
        {
            Assert.Equal(ExpectedVm[i], r.Vm[i], 3);       // within 1e-3 p.u.
            Assert.Equal(ExpectedVaDeg[i], r.VaDeg[i], 2); // within 1e-2 degrees
        }
    }

    [Fact]
    public void Case9_slack_and_losses_match_known_values()
    {
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9());
        // Slack (bus 1) generation and total real losses are well-known for case9.
        Assert.Equal(71.64, r.PInjMw[0], 1);     // ~71.64 MW
        Assert.Equal(4.641, r.TotalLossMw, 2);   // ~4.64 MW
    }

    [Fact]
    public void Generation_balances_load_plus_losses()
    {
        var model = IeeeCases.Case9();
        var r = new NewtonRaphsonPowerFlow().Solve(model);

        double totalLoad = model.Buses.Sum(b => b.Pd);
        double totalGen = r.PInjMw.Sum() + totalLoad; // net injections sum to losses
        Assert.Equal(totalLoad + r.TotalLossMw, totalGen, 6);
    }

    [Fact]
    public void Line_losses_sum_to_total_losses()
    {
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9());
        double sumLine = r.LineFlows.Sum(f => f.PLossMw);
        Assert.Equal(r.TotalLossMw, sumLine, 6);
    }

    [Fact]
    public void Flat_start_reaches_the_same_solution()
    {
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { FlatStart = true });
        Assert.True(r.Converged);
        Assert.Equal(0.9956, r.Vm[8], 3);
    }

    [Fact]
    public void A_slack_bus_with_no_generator_holds_its_stored_Vm_whatever_the_start_option()
    {
        // GR-046: the slack's Vm is a specification. With no generator on the slack it used to start (and so stay) at
        // 1.0 under FlatStart and at the stored value otherwise, so the two starts gave two different solutions.
        GridModel c9 = IeeeCases.Case9();
        GridModel m = c9.CopyWith(
            buses: c9.Buses.Select(b => b.Type == BusType.Slack ? b with { Vm = 1.04 } : b).ToArray(),
            generators: c9.Generators.Where(g => c9.Buses.First(b => b.Id == g.Bus).Type != BusType.Slack).ToArray());
        foreach (IPowerFlowSolver engine in new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() })
        {
            PowerFlowResult warm = engine.Solve(m, new PowerFlowOptions { FlatStart = false });
            PowerFlowResult flat = engine.Solve(m, new PowerFlowOptions { FlatStart = true });
            Assert.True(warm.Converged);
            Assert.True(flat.Converged);
            Assert.Equal(1.04, warm.Vm[0]);
            Assert.Equal(1.04, flat.Vm[0]);
            for (int i = 0; i < m.BusCount; i++) Assert.Equal(warm.Vm[i], flat.Vm[i], 6);
        }
    }

    [Fact]
    public void A_slack_bus_with_no_generator_and_no_usable_stored_Vm_is_refused()
    {
        // GR-046: with no generator the stored Vm is the set point, so a non-positive one cannot be solved around.
        GridModel c9 = IeeeCases.Case9();
        GridModel m = c9.CopyWith(
            buses: c9.Buses.Select(b => b.Type == BusType.Slack ? b with { Vm = 0.0 } : b).ToArray(),
            generators: c9.Generators.Where(g => c9.Buses.First(b => b.Id == g.Bus).Type != BusType.Slack).ToArray());
        var ex = Assert.Throws<InvalidOperationException>(() => new SparseNewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { FlatStart = true }));
        Assert.Contains("GR-046", ex.Message, StringComparison.Ordinal);
    }
}
