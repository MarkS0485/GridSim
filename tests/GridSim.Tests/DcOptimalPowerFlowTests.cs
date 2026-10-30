using GridSim.Core.Analysis;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DC optimal power flow. Pins merit-order dispatch (cheapest generation first, unconstrained), the
/// power balance, and that a binding line limit forces a redispatch onto a more expensive generator to
/// relieve the overload.
/// </summary>
public class DcOptimalPowerFlowTests
{
    [Fact]
    public void Unconstrained_dispatch_follows_merit_order()
    {
        // Three generators (cost 10/50/90, each 0..60 MW) meet 100 MW; no rated lines -> pure merit order.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 100) },
            Generators = new[]
            {
                new Generator(1, Pg: 0, Pmax: 60), new Generator(2, Pg: 0, Pmax: 60), new Generator(3, Pg: 0, Pmax: 60),
            },
            Branches = new[] { new Branch(1, 3, 0.0, 0.1), new Branch(2, 3, 0.0, 0.1) },
        };
        var cost = new[] { 10.0, 50.0, 90.0 };
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, cost, enforceLineLimits: false);

        Assert.True(r.Feasible);
        Assert.Equal(60.0, r.Dispatch.Single(d => d.GenBus == 1).Pg, 3); // cheapest fully loaded
        Assert.Equal(40.0, r.Dispatch.Single(d => d.GenBus == 2).Pg, 3); // next fills the rest
        Assert.Equal(0.0, r.Dispatch.Single(d => d.GenBus == 3).Pg, 3);  // most expensive idle
        Assert.Equal(100.0, r.TotalGenMw, 3);                            // power balance
    }

    [Fact]
    public void A_binding_line_forces_redispatch_onto_a_costlier_generator()
    {
        // Cheap gen at bus 1, costly gen at bus 2, all load at bus 3. Line 1-3 is rated too low to carry
        // the whole cheap dispatch, so the costlier gen at bus 2 must pick up to relieve it.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 100) },
            Generators = new[] { new Generator(1, Pg: 0, Pmax: 200), new Generator(2, Pg: 0, Pmax: 200) },
            Branches = new[]
            {
                new Branch(1, 3, 0.0, 0.1) { Name = "L13", RateAMva = 60 }, // the binding line
                new Branch(2, 3, 0.0, 0.1) { Name = "L23", RateAMva = 200 },
                new Branch(1, 2, 0.0, 0.1) { Name = "L12", RateAMva = 200 },
            },
        };
        var cost = new[] { 10.0, 90.0 };

        DcOptimalPowerFlow.DcOpfResult unc = DcOptimalPowerFlow.Solve(m, cost, enforceLineLimits: false);
        Assert.Equal(100.0, unc.Dispatch.Single(d => d.GenBus == 1).Pg, 3); // merit order overloads L13

        DcOptimalPowerFlow.DcOpfResult sec = DcOptimalPowerFlow.Solve(m, cost, enforceLineLimits: true);
        Assert.True(sec.Feasible);
        Assert.True(sec.NetworkConstrained);
        Assert.Equal(100.0, sec.TotalGenMw, 3);                                  // balance preserved
        Assert.True(sec.Dispatch.Single(d => d.GenBus == 2).Pg > 1.0, "costly gen must pick up to relieve L13");
        Assert.True(sec.Dispatch.Single(d => d.GenBus == 1).Pg < 100.0, "cheap gen backed off the binding line");
    }
}
