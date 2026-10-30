using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 docs-programme finding: <see cref="SolverSelector.LooksRadial"/> counted branches (n-1 in service, one
/// slack) but never checked that they CONNECT every bus. A model in two islands can still carry n-1 in-service
/// branches (a loop in one island, a free bus in the other); it was routed to the backward/forward sweep, which
/// returned NonFinite. A graph with n-1 edges is a tree only if it is connected, so the test now checks that too.
/// </summary>
public class LooksRadialConnectivityTests
{
    private static GridModel Model(Bus[] buses, Branch[] branches) => new()
    {
        Name = "radial-connectivity",
        BaseMva = 100,
        Buses = buses,
        Branches = branches,
        Generators = new[] { new Generator(1, Pg: 0) },
    };

    /// <summary>Four buses, three in-service branches: a loop 1-2-3 plus a disconnected bus 4.</summary>
    private static GridModel LoopPlusIsland() => Model(
        new[]
        {
            new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10), new Bus(3, BusType.Pq, Pd: 10), new Bus(4, BusType.Pq, Pd: 5),
        },
        new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(3, 1, 0.01, 0.1) });

    [Fact]
    public void A_disconnected_model_with_n_minus_1_branches_is_not_radial()
    {
        GridModel m = LoopPlusIsland();
        Assert.Equal(m.BusCount - 1, m.Branches.Count(b => b.InService));   // passes the old count-only test
        Assert.False(SolverSelector.LooksRadial(m));
        // So Auto routes it to Newton, never to the sweep.
        Assert.NotEqual(SolverKind.DistributionSweep, SolverSelector.Resolve(m));
    }

    [Fact]
    public void A_connected_chain_is_still_radial()
    {
        GridModel chain = Model(
            new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10), new Bus(3, BusType.Pq, Pd: 10), new Bus(4, BusType.Pq, Pd: 5) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(2, 4, 0.01, 0.1) });
        Assert.True(SolverSelector.LooksRadial(chain));
        Assert.Equal(SolverKind.DistributionSweep, SolverSelector.Resolve(chain));
    }

    [Fact]
    public void An_open_branch_does_not_count_as_a_connection()
    {
        // Chain 1-2-3-4 with an extra OPEN tie 4-1 (still radial), versus a chain whose middle link is open and a
        // closed tie elsewhere keeps the count at n-1 but leaves bus 3 cut off (not radial).
        Bus[] buses = { new(1, BusType.Slack), new(2, BusType.Pq, Pd: 10), new(3, BusType.Pq, Pd: 10), new(4, BusType.Pq, Pd: 5) };
        GridModel withOpenTie = Model(buses, new[]
        {
            new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1),
            new Branch(4, 1, 0.01, 0.1, InService: false),
        });
        Assert.True(SolverSelector.LooksRadial(withOpenTie));

        GridModel cutOff = Model(buses, new[]
        {
            new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1, InService: false), new Branch(2, 4, 0.01, 0.1),
            new Branch(4, 1, 0.01, 0.1),
        });
        Assert.Equal(cutOff.BusCount - 1, cutOff.Branches.Count(b => b.InService));
        Assert.False(SolverSelector.LooksRadial(cutOff));
    }

    [Fact]
    public void A_branch_to_a_bus_not_in_the_model_is_not_radial()
    {
        GridModel m = Model(
            new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10), new Bus(3, BusType.Pq, Pd: 10) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 99, 0.01, 0.1) });
        Assert.False(SolverSelector.LooksRadial(m));
    }
}
