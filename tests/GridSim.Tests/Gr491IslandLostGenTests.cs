using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-491: IslandReport.LostGenMw is always zero by construction (an island is blacked out only when none of its units is
/// scheduled and none can produce real power). It is kept as a documented zero; these pin that invariant, so a change to
/// the blackout rule that starts booking lost generation shows up here.
/// </summary>
public class Gr491IslandLostGenTests
{
    /// <summary>Bus 1-2 energised (slack + load); bus 3-4 a separate island with load and, optionally, units.</summary>
    private static GridModel TwoIslands(params Generator[] islandUnits) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 50, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 20, BaseKv: 132), new Bus(4, BusType.Pq, Pd: 10, BaseKv: 132),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
        Generators = new[] { new Generator(1, 50) }.Concat(islandUnits).ToArray(),
    };

    [Fact]
    public void Gr491_a_dead_island_with_no_units_loses_its_load_and_no_generation()
    {
        (_, IslandReport r) = IslandPartition.Prepare(TwoIslands());
        Assert.Equal(1, r.BlackedOutIslands);
        Assert.Equal(30.0, r.LostLoadMw);
        Assert.Equal(0.0, r.LostGenMw);
    }

    [Fact]
    public void Gr491_a_dead_island_whose_only_unit_cannot_produce_real_power_books_zero_lost_generation()
    {
        // A synchronous condenser (Pg 0, Pmax 0) cannot hold an island: blacked out, and its 0 MW is all it "loses".
        (_, IslandReport r) = IslandPartition.Prepare(TwoIslands(new Generator(3, 0.0, Pmax: 0.0, Type: "SYNC_COMP")));
        Assert.Equal(1, r.BlackedOutIslands);
        Assert.Equal(0.0, r.LostGenMw);
    }

    [Fact]
    public void Gr491_an_island_with_a_scheduled_unit_is_not_blacked_out_so_nothing_is_lost()
    {
        (_, IslandReport r) = IslandPartition.Prepare(TwoIslands(new Generator(3, 25.0, Pmax: 40.0)));
        Assert.Equal(0, r.BlackedOutIslands);
        Assert.Equal(0.0, r.LostLoadMw);
        Assert.Equal(0.0, r.LostGenMw);
    }
}
