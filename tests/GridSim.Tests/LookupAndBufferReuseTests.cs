using System.Reflection;
using GridSim.Core.IO;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R123 and R128 (October 2026 review): two orchestration-only performance fixes, each proven bit-identical to the
/// code it replaced (reproduced verbatim here as the reference).
/// </summary>
public class LookupAndBufferReuseTests
{
    /// <summary>The pre-R123 Brandes betweenness, verbatim: fresh buffers for every source.</summary>
    private static double[] BetweennessReference(int n, IReadOnlyList<IReadOnlyList<int>> adj)
    {
        var cb = new double[n];
        for (int s = 0; s < n; s++)
        {
            var stack = new Stack<int>();
            var pred = new List<int>[n];
            var sigma = new double[n];
            var dist = new int[n];
            for (int i = 0; i < n; i++) { pred[i] = new List<int>(); dist[i] = -1; }
            sigma[s] = 1; dist[s] = 0;
            var q = new Queue<int>();
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                stack.Push(v);
                foreach (int w in adj[v])
                {
                    if (dist[w] < 0) { dist[w] = dist[v] + 1; q.Enqueue(w); }
                    if (dist[w] == dist[v] + 1) { sigma[w] += sigma[v]; pred[w].Add(v); }
                }
            }
            var delta = new double[n];
            while (stack.Count > 0)
            {
                int w = stack.Pop();
                foreach (int v in pred[w])
                    delta[v] += sigma[v] / sigma[w] * (1 + delta[w]);
                if (w != s) cb[w] += delta[w];
            }
        }
        return cb;
    }

    private static double[] BetweennessNow(int n, IReadOnlyList<IReadOnlyList<int>> adj)
    {
        MethodInfo m = typeof(TopologyTiers).GetMethod("Betweenness", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TopologyTiers.Betweenness not found");
        return (double[])m.Invoke(null, new object[] { n, adj })!;
    }

    private static IReadOnlyList<IReadOnlyList<int>> Adjacency(GridModel model)
    {
        var index = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) index[model.Buses[i].Id] = i;
        var adj = Enumerable.Range(0, model.Buses.Count).Select(_ => new List<int>()).ToArray();
        foreach (Branch b in model.Branches)
        {
            if (!b.InService) continue;
            adj[index[b.From]].Add(index[b.To]);
            adj[index[b.To]].Add(index[b.From]);
        }
        return adj;
    }

    [Theory]
    [InlineData("118")]
    [InlineData("300")]
    public void Betweenness_with_reused_buffers_is_bit_identical(string name)
    {
        GridModel model = name == "118" ? IeeeCases.Case118() : IeeeCases.Case300();
        var adj = Adjacency(model);
        double[] want = BetweennessReference(model.Buses.Count, adj);
        double[] got = BetweennessNow(model.Buses.Count, adj);
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(want[i]), BitConverter.DoubleToInt64Bits(got[i]));
    }

    [Fact]
    public void Betweenness_is_bit_identical_on_a_disconnected_graph_with_parallel_edges()
    {
        // Two components, a doubled edge and an isolated node: the reset must leave no state between sources.
        var adj = new List<int>[] { new() { 1, 1, 2 }, new() { 0, 0, 2 }, new() { 0, 1 }, new() { 4 }, new() { 3 }, new() };
        double[] want = BetweennessReference(adj.Length, adj);
        double[] got = BetweennessNow(adj.Length, adj);
        for (int i = 0; i < want.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(want[i]), BitConverter.DoubleToInt64Bits(got[i]));
    }

    [Fact]
    public void Bus_names_match_the_old_first_match_scan_for_every_bus_and_an_unknown_id()
    {
        GridModel model = IeeeCases.Case118();
        PowerFlowResult pf = new SparseNewtonRaphsonPowerFlow().Solve(model);
        var ctx = new ProtectionContext(model, pf, new ProtectionSettings(), trajectory: null);
        foreach (Bus b in model.Buses)
        {
            string old = model.Buses.First(x => x.Id == b.Id).Name ?? $"bus {b.Id}";
            Assert.Equal(old, ctx.BusName(b.Id));
        }
        Assert.Equal("bus 999999", ctx.BusName(999999));
    }
}
