using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N120 (October 2026): the distributed slack refused every model with more than one reference bus. A model whose
/// reference buses each sit in their own network island is now solved with one imbalance per island (each island's
/// distributed slack, as if solved alone); two reference buses in one connected network stay refused, now naming them.
/// </summary>
public class DistributedSlackIslandsTests
{
    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 100, EnforceQLimits = true };

    /// <summary>IEEE 9 and a second, disconnected copy of it with every bus id raised by <paramref name="offset"/> and
    /// its demand scaled by <paramref name="demandScale"/> (so the two islands have different imbalances).</summary>
    private static GridModel TwoIslands(int offset = 100, double demandScale = 1.1)
    {
        GridModel a = Core.IO.IeeeCases.Case9();
        var buses = a.Buses.Concat(a.Buses.Select(b => b with { Id = b.Id + offset, Pd = b.Pd * demandScale, Qd = b.Qd * demandScale, Name = b.Name + "'" })).ToList();
        var gens = a.Generators.Concat(a.Generators.Select(g => g with { Bus = g.Bus + offset, Name = (g.Name ?? "G") + "'" })).ToList();
        var branches = a.Branches.Concat(a.Branches.Select(br => br with { From = br.From + offset, To = br.To + offset, Name = br.Name is null ? null : br.Name + "'" })).ToList();
        return a.CopyWith(buses: buses, generators: gens, branches: branches, name: "two IEEE 9 islands");
    }

    private static GridModel Island(GridModel two, bool second, int offset = 100)
    {
        bool In(int bus) => second ? bus > offset : bus <= offset;
        return two.CopyWith(
            buses: two.Buses.Where(b => In(b.Id)).ToList(),
            generators: two.Generators.Where(g => In(g.Bus)).ToList(),
            branches: two.Branches.Where(br => In(br.From)).ToList());
    }

    [Fact]
    public void One_reference_per_island_gives_each_island_its_own_distributed_slack()
    {
        GridModel two = TwoIslands();
        Assert.Equal(2, two.Buses.Count(b => b.Type == BusType.Slack));
        var ds = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        PowerFlowResult r = ds.Solve(two, Opt);
        Assert.True(r.Converged, ds.LastNote);
        Assert.Null(ds.LastNote);
        Assert.Equal(2, ds.LastIslandImbalancesMw.Count);

        // Factors are normalised within each island: three units each, equal shares of 1/3.
        for (int i = 0; i < 6; i++) Assert.Equal(1.0 / 3.0, ds.LastFactors[i], 15);

        // Each island against the same island solved alone (a single-reference model, the unchanged path): the same
        // imbalance and dispatch to within the loop tolerance (the joint Newton solve iterates until BOTH islands meet
        // the mismatch tolerance, so the last bits may differ; the loop itself stops at 1e-4 MW).
        int[] refs = two.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).ToArray();
        for (int isl = 0; isl < 2; isl++)
        {
            GridModel alone = Island(two, second: isl == 1);
            var dsAlone = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Equal);
            Assert.True(dsAlone.Solve(alone, Opt).Converged);
            Assert.True(Math.Abs(ds.LastIslandImbalancesMw[refs[isl]] - dsAlone.LastImbalanceMw) < 1e-3,
                $"island {isl}: K {ds.LastIslandImbalancesMw[refs[isl]]:R} vs alone {dsAlone.LastImbalanceMw:R}");
            for (int j = 0; j < 3; j++)
                Assert.True(Math.Abs(ds.LastDispatchMw[isl * 3 + j] - dsAlone.LastDispatchMw[j]) < 1e-3);
        }
        // The two islands' imbalances differ (the second carries 10 % more demand), so they really were shared apart.
        Assert.True(ds.LastIslandImbalancesMw[refs[1]] > ds.LastIslandImbalancesMw[refs[0]] + 1.0);
        Assert.Equal(ds.LastIslandImbalancesMw[refs[0]] + ds.LastIslandImbalancesMw[refs[1]], ds.LastImbalanceMw);
    }

    [Fact]
    public void Dense_and_sparse_engines_give_the_same_bits_on_the_island_path()
    {
        GridModel two = TwoIslands();
        var dense = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        var sparse = new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        PowerFlowResult rd = dense.Solve(two, Opt), rs = sparse.Solve(two, Opt);
        Assert.True(rd.Converged && rs.Converged);
        Assert.Equal(dense.LastPasses, sparse.LastPasses);
        Assert.Equal(dense.LastDispatchMw, sparse.LastDispatchMw);
        Assert.Equal(rd.Vm, rs.Vm);
        Assert.Equal(rd.VaDeg, rs.VaDeg);
    }

    [Fact]
    public void Two_reference_buses_in_one_network_are_refused_naming_them()
    {
        GridModel m = Core.IO.IeeeCases.Case9();
        var buses = m.Buses.Select(b => b.Id == 2 ? b with { Type = BusType.Slack } : b).ToList();
        var ds = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        var ex = Assert.Throws<InvalidOperationException>(() => ds.Solve(m.CopyWith(buses: buses), Opt));
        Assert.Contains("2 reference (slack) buses", ex.Message, StringComparison.Ordinal);
        Assert.Contains("buses 1 and 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("one per network island", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_island_whose_units_all_have_zero_factors_is_refused()
    {
        // Explicit factors: island 1's units take part, island 2's are all zero: island 2's imbalance has nowhere to go.
        var ds = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), new double[] { 1, 1, 1, 0, 0, 0 });
        var ex = Assert.Throws<InvalidOperationException>(() => ds.Solve(TwoIslands(), Opt));
        Assert.Contains("no generator takes part in the island of reference bus 101", ex.Message, StringComparison.Ordinal);
    }
}
