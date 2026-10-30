using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The island pre-pass that makes an opened network solvable: no-op when connected, slack election for a
/// generating island, blackout for a dead island. Foundation for the topology-aware recloser.
/// </summary>
public class IslandPartitionTests
{
    // buses: 1 slack+gen, 2 load, 3 gen(no slack), 4 load-only. Line 1-2-3, spur 3-4.
    private static GridModel FourBusChain(string? openBranch = null)
    {
        var buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132),
            new(2, BusType.Pq, Pd: 50, BaseKv: 132),
            new(3, BusType.Pv, BaseKv: 132),
            new(4, BusType.Pq, Pd: 30, BaseKv: 132),
        };
        var gens = new List<Generator> { new(1, Pg: 60, H: 4, MBase: 100), new(3, Pg: 40, H: 3, MBase: 100) };
        var branches = new List<Branch>
        {
            new(1, 2, 0.01, 0.1, Name: "L12", InService: openBranch != "L12"),
            new(2, 3, 0.01, 0.1, Name: "L23", InService: openBranch != "L23"),
            new(3, 4, 0.01, 0.1, Name: "L34", InService: openBranch != "L34"),
        };
        return new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = branches };
    }

    [Fact]
    public void All_in_service_is_a_no_op()
    {
        (GridModel m, IslandReport r) = IslandPartition.Prepare(FourBusChain());
        Assert.Equal(1, r.Islands);
        Assert.False(r.AnyChange);
        Assert.Equal(4, m.Buses.Count);
    }

    [Fact]
    public void Islanding_a_generating_region_elects_a_slack()
    {
        // Open L12: {2,3,4} splits from the slack at bus 1. That island has a generator (bus 3) but no
        // slack -> bus 3 is elected the reference; nothing is blacked out (bus 4 rides on bus 3's island).
        (GridModel adj, IslandReport rep) = IslandPartition.Prepare(FourBusChain(openBranch: "L12"));

        Assert.Equal(2, rep.Islands);
        Assert.Equal(2, rep.EnergizedIslands);
        Assert.Equal(0, rep.BlackedOutIslands);
        Assert.Contains(3, rep.ElectedSlackBuses);
        Assert.Equal(2, adj.Buses.Count(b => b.Type == BusType.Slack));   // original + elected
    }

    [Fact]
    public void An_elected_island_reports_its_imbalance_and_whether_the_elected_units_can_carry_it()
    {
        // GR-055: open L12; island {2,3,4} carries 80 MW of load against 40 MW scheduled at bus 3. With no Pmax the
        // elected unit can carry the 40 MW imbalance; capped at 50 MW it cannot (it would need 80 MW).
        (_, IslandReport rep) = IslandPartition.Prepare(FourBusChain(openBranch: "L12"));
        ElectedIsland e = Assert.Single(rep.ElectedIslands);
        Assert.Equal(3, e.SlackBus);
        Assert.Equal(80.0, e.LoadMw);
        Assert.Equal(40.0, e.ScheduledGenMw);
        Assert.Equal(40.0, e.ImbalanceMw);
        Assert.Equal(80.0, e.RequiredPgMw);
        Assert.False(e.BeyondCapability);
        Assert.False(rep.AnyElectedBeyondCapability);

        GridModel capped = FourBusChain(openBranch: "L12");
        capped = capped.CopyWith(generators: capped.Generators.Select(g => g.Bus == 3 ? g with { Pmax = 50 } : g).ToArray());
        (_, IslandReport rep2) = IslandPartition.Prepare(capped);
        Assert.True(Assert.Single(rep2.ElectedIslands).BeyondCapability);
        Assert.True(rep2.AnyElectedBeyondCapability);
    }

    [Fact]
    public void Islanding_a_dead_region_blacks_it_out_and_books_the_lost_load()
    {
        // Open L34: bus 4 (load only, no generation) islands -> blacked out; its 30 MW load is lost.
        (GridModel adj, IslandReport rep) = IslandPartition.Prepare(FourBusChain(openBranch: "L34"));

        Assert.Equal(1, rep.BlackedOutIslands);
        Assert.Contains(4, rep.BlackedOutBuses);
        Assert.Equal(30.0, rep.LostLoadMw, 6);
        Assert.DoesNotContain(adj.Buses, b => b.Id == 4);   // dead bus removed from the solve
        Assert.DoesNotContain(adj.Branches, b => b.Name == "L34");
    }

    [Fact]
    public void The_adjusted_islanded_model_solves()
    {
        (GridModel adj, _) = IslandPartition.Prepare(FourBusChain(openBranch: "L12"));
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(adj);
        Assert.True(r.Converged, "each island has a reference, so the partitioned model must solve");
    }
}
