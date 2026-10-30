using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N034 (October 2026, best-judgement ruling): the DC distribution factors and the security DC-OPF handle a network
/// made of several DC islands with one reference bus per island, chosen as the AC power flow's island pre-pass chooses
/// one (the island's slack, else its largest generator by |Pg|), instead of throwing "DC susceptance is singular".
/// An island with no generator is reported, never dropped. A connected network is unchanged bit for bit (pinned by
/// <see cref="DcOpfSecuritySimplexParityTests"/> and <see cref="DistributionFactorsTests"/>, which are untouched).
/// </summary>
public class DcIslandTests
{
    // Island A: buses 1 (slack, gen) - 2 - 3 (load), a triangle of equal reactances.
    // Island B: buses 11 - 12 - 13, a triangle of equal reactances, with NO slack: generators at 11 (Pg 10) and
    // 12 (Pg 50), load at 13. The AC rule elects bus 12 (largest |Pg|) as B's reference.
    private static GridModel TwoIslands(bool deadIsland = false, double deadLoad = 7.0)
    {
        var buses = new List<Bus>
        {
            new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq, Pd: 80),
            new(11, BusType.Pv), new(12, BusType.Pv), new(13, BusType.Pq, Pd: 40),
        };
        var gens = new List<Generator>
        {
            new(1, Pg: 80, Pmax: 200), new(11, Pg: 10, Pmax: 100), new(12, Pg: 50, Pmax: 100),
        };
        var branches = new List<Branch>
        {
            new(1, 2, 0.0, 0.1) { Name = "A12", RateAMva = 500 }, new(2, 3, 0.0, 0.1) { Name = "A23", RateAMva = 500 },
            new(1, 3, 0.0, 0.1) { Name = "A13", RateAMva = 500 },
            new(11, 12, 0.0, 0.1) { Name = "B12", RateAMva = 500 }, new(12, 13, 0.0, 0.1) { Name = "B23", RateAMva = 500 },
            new(11, 13, 0.0, 0.1) { Name = "B13", RateAMva = 500 },
        };
        if (deadIsland)
        {
            // Island C: two load buses joined by one line, no generator at all.
            buses.Add(new Bus(21, BusType.Pq, Pd: deadLoad)); buses.Add(new Bus(22, BusType.Pq));
            branches.Add(new Branch(21, 22, 0.0, 0.2) { Name = "C12", RateAMva = 100 });
        }
        return new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = branches };
    }

    [Fact]
    public void A_connected_network_has_one_island_referenced_at_the_slack()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Pq), new Bus(2, BusType.Slack), new Bus(3, BusType.Pq, Pd: 10) },
            Generators = new[] { new Generator(2, Pg: 10) },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1), new Branch(2, 3, 0.0, 0.1) },
        };
        DistributionFactors df = DistributionFactors.Build(m);
        DcIsland only = Assert.Single(df.Islands);
        Assert.Equal(2, only.ReferenceBusId);
        Assert.Equal(2, df.SlackBusId);
        Assert.Equal(new[] { 1, 2, 3 }, only.BusIds);
        Assert.True(only.HasGeneration);
        Assert.Equal(10.0, only.LoadMw);
        // The override still moves the single reference, exactly as before.
        Assert.Equal(3, Assert.Single(DistributionFactors.Build(m, slackBusId: 3).Islands).ReferenceBusId);
    }

    [Fact]
    public void Each_island_gets_its_own_reference_by_the_AC_island_rule()
    {
        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(TwoIslands(deadIsland: true));
        Assert.Equal(3, islands.Count);
        Assert.Equal(1, islands[0].ReferenceBusId);    // the slack
        Assert.Equal(12, islands[1].ReferenceBusId);   // no slack: the largest generator (Pg 50 beats Pg 10)
        Assert.Equal(21, islands[2].ReferenceBusId);   // dead island: its first bus, only to keep B' non-singular
        Assert.True(islands[0].HasGeneration);
        Assert.True(islands[1].HasGeneration);
        Assert.False(islands[2].HasGeneration);
        Assert.Equal(new[] { 11, 12, 13 }, islands[1].BusIds);
        Assert.Equal(7.0, islands[2].LoadMw);
    }

    [Fact]
    public void Ptdf_withdraws_at_the_bus_islands_reference_and_leaves_other_islands_untouched()
    {
        GridModel m = TwoIslands();
        DistributionFactors df = DistributionFactors.Build(m);   // used to throw "DC susceptance is singular"
        Assert.Equal(2, df.Islands.Count);
        Assert.Equal(1, df.SlackBusId);

        // +1 at bus 11, withdrawn at B's reference 12, in a triangle of equal reactances: 2/3 direct, 1/3 via 13.
        double[] col = df.PtdfColumn(11);
        Assert.Equal(0.0, col[0]); Assert.Equal(0.0, col[1]); Assert.Equal(0.0, col[2]);   // island A untouched
        Assert.Equal(2.0 / 3.0, col[3], 14);    // B12 carries 2/3 from 11 to 12
        Assert.Equal(-1.0 / 3.0, col[4], 14);   // B23 (12 -> 13) carries 1/3 from 13 to 12
        Assert.Equal(1.0 / 3.0, col[5], 14);    // B13 carries 1/3 from 11 to 13

        // Both references have identically zero columns.
        Assert.All(df.PtdfColumn(1), v => Assert.Equal(0.0, v));
        Assert.All(df.PtdfColumn(12), v => Assert.Equal(0.0, v));

        // Island A behaves as island A alone (same reference, same triangle).
        double[] colA = df.PtdfColumn(2);
        Assert.Equal(-2.0 / 3.0, colA[0], 14);
        Assert.Equal(1.0 / 3.0, colA[1], 14);
        Assert.Equal(-1.0 / 3.0, colA[2], 14);   // A13 (1 -> 3): the third that goes 2 -> 3 -> 1 runs 3 to 1
        Assert.Equal(0.0, colA[3]); Assert.Equal(0.0, colA[4]); Assert.Equal(0.0, colA[5]);

        // Flows: each island's reference absorbs its own imbalance. Island B: 10 at 11, -40 at 13 (12 balances).
        double[] f = df.DcFlows(new Dictionary<int, double> { [11] = 10.0, [13] = -40.0, [3] = -80.0 }, 100.0);
        Assert.Equal(80.0 * 2.0 / 3.0, f[2], 10);   // A13 carries 2/3 of the 80 MW from 1 to 3
        // B12 (11 -> 12), superposed: +2/3 of the 10 MW from 11, and -1/3 of the 40 MW drawn at 13 from 12.
        Assert.Equal(10.0 * 2.0 / 3.0 - 40.0 / 3.0, f[3], 10);
    }

    [Fact]
    public void A_zero_reactance_branch_that_alone_joins_two_islands_fails_loud()
    {
        GridModel m = TwoIslands();
        var branches = m.Branches.ToList();
        branches.Add(new Branch(3, 13, 0.0, 0.0) { Name = "COUPLER" });
        var ex = Assert.Throws<InvalidOperationException>(() => DistributionFactors.Build(m.CopyWith(branches: branches)));
        Assert.Contains("COUPLER", ex.Message);
        Assert.Contains("zero series reactance", ex.Message);
    }

    [Fact]
    public void Dcopf_dispatches_each_island_on_its_own_balance()
    {
        GridModel m = TwoIslands();
        var cost = new[] { 30.0, 10.0, 20.0 };
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, cost, enforceLineLimits: true);

        Assert.True(r.Feasible);
        Assert.NotNull(r.Islands);
        Assert.Equal(2, r.Islands!.Count);
        // Island A: its only generator meets its 80 MW. Island B: cheapest first, gen 11 (cost 10) takes all 40 MW.
        // A single global merit order would have pushed the cheap island-B units into island A's demand.
        Assert.Equal(80.0, r.Dispatch[0].Pg);
        Assert.Equal(40.0, r.Dispatch[1].Pg);
        Assert.Equal(0.0, r.Dispatch[2].Pg);
        Assert.Equal(80.0, r.Islands[0].GenMw);
        Assert.Equal(40.0, r.Islands[1].GenMw);
        Assert.Equal(1, r.Islands[0].ReferenceBus);
        Assert.Equal(12, r.Islands[1].ReferenceBus);
        Assert.Equal(120.0, r.TotalLoadMw);
        Assert.Equal(120.0, r.TotalGenMw);
        Assert.Equal(80.0 * 30.0 + 40.0 * 10.0, r.TotalCost);
        Assert.Equal(LpStatus.Optimal, r.SecurityLpStatus);
    }

    [Fact]
    public void Each_island_is_solved_exactly_as_the_island_alone()
    {
        // The island answers are the connected-case answers of each island model, bit for bit (the same code path).
        GridModel m = TwoIslands();
        var cost = new[] { 30.0, 10.0, 20.0 };
        DcOptimalPowerFlow.DcOpfResult whole = DcOptimalPowerFlow.Solve(m, cost);

        GridModel islandB = m.CopyWith(
            buses: m.Buses.Where(b => b.Id > 10).Select(b => b.Id == 12 ? b with { Type = BusType.Slack } : b).ToList(),
            generators: m.Generators.Where(g => g.Bus > 10).ToList(),
            branches: m.Branches.Where(b => b.From > 10).ToList());
        DcOptimalPowerFlow.DcOpfResult alone = DcOptimalPowerFlow.Solve(islandB, new[] { 10.0, 20.0 });
        Assert.Null(alone.Islands);
        Assert.Equal(BitConverter.DoubleToInt64Bits(alone.Dispatch[0].Pg), BitConverter.DoubleToInt64Bits(whole.Dispatch[1].Pg));
        Assert.Equal(BitConverter.DoubleToInt64Bits(alone.Dispatch[1].Pg), BitConverter.DoubleToInt64Bits(whole.Dispatch[2].Pg));
    }

    [Fact]
    public void A_binding_line_in_a_second_island_is_reported_with_its_full_model_index()
    {
        GridModel m = TwoIslands();
        var branches = m.Branches.ToList();
        branches[5] = branches[5] with { RateAMva = 20 };   // B13: the cheap unit at 11 would push 26.7 MW over it
        var cost = new[] { 30.0, 10.0, 20.0 };
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m.CopyWith(branches: branches), cost);
        Assert.True(r.Feasible);
        Assert.True(r.NetworkConstrained);
        DcOptimalPowerFlow.LineBinding b = Assert.Single(r.BindingLines);
        Assert.Equal(5, b.Branch);
        Assert.Equal("B13", b.Name);
        Assert.Equal(20.0, Math.Abs(b.FlowMw), 6);
        Assert.Equal(40.0, r.Dispatch[1].Pg + r.Dispatch[2].Pg, 9);   // island B still balances on its own
    }

    [Fact]
    public void An_island_with_no_generation_is_reported_and_its_load_makes_the_dispatch_infeasible()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(TwoIslands(deadIsland: true), new[] { 30.0, 10.0, 20.0 });
        Assert.False(r.Feasible);
        Assert.Equal(3, r.Islands!.Count);
        DcOptimalPowerFlow.IslandDispatch dead = r.Islands[2];
        Assert.False(dead.HasGeneration);
        Assert.False(dead.Feasible);
        Assert.Equal(7.0, dead.LoadMw);
        Assert.Equal(2, dead.BusCount);
        Assert.Null(dead.SecurityLpStatus);
        Assert.Equal(127.0, r.TotalLoadMw);   // the unsupplied load still counts in the demand
        Assert.Equal(120.0, r.TotalGenMw);
        // The energised islands are still dispatched and still feasible on their own.
        Assert.True(r.Islands[0].Feasible);
        Assert.True(r.Islands[1].Feasible);
    }

    [Fact]
    public void An_island_with_no_generation_and_no_load_is_reported_but_harmless()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(TwoIslands(deadIsland: true, deadLoad: 0.0), new[] { 30.0, 10.0, 20.0 });
        Assert.True(r.Feasible);
        Assert.False(r.Islands![2].HasGeneration);
        Assert.True(r.Islands[2].Feasible);
    }

    [Fact]
    public void A_cost_vector_that_does_not_match_the_generators_fails_loud_with_islands()
    {
        Assert.Throws<ArgumentException>(() => DcOptimalPowerFlow.Solve(TwoIslands(), new[] { 1.0, 2.0 }));
    }

    [Fact]
    public void Gb_full_has_two_DC_islands_each_with_its_own_slack_and_generation()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data", "gb-full"));
        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(m);
        Assert.Equal(2, islands.Count);
        Assert.Equal(3536, islands[0].BusIds.Count);
        Assert.Equal(3, islands[1].BusIds.Count);
        Assert.All(islands, i => Assert.True(i.HasGeneration));
        Assert.All(islands, i => Assert.Equal(BusType.Slack, m.Buses.Single(b => b.Id == i.ReferenceBusId).Type));
        Assert.Equal(6773, islands[1].BusIds[0]);
        Assert.Equal(194, islands[0].ReferenceBusId);
        Assert.Equal(6774, islands[1].ReferenceBusId);
        // The factors now build (they threw "DC susceptance is singular" before N034). SlackBusId keeps the rule it
        // always had, the FIRST Slack bus in model order, which on gb-full is 6774, in the 3-bus island.
        DistributionFactors df = DistributionFactors.Build(m);
        Assert.Equal(6774, df.SlackBusId);
        Assert.Equal(6774, m.Buses.First(b => b.Type == BusType.Slack).Id);
    }
}
