using GridSim.Core.Layout;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// TopologyTiers synthesises a display voltage hierarchy from graph structure for flat imported
/// cases. These lock the contract the renderer relies on: source pinned to the backbone tier, a
/// monotonic feeder hierarchy on a tree, betweenness on a mesh, and determinism.
/// </summary>
public class TopologyTiersTests
{
    // Build an undirected adjacency from an edge list.
    private static IReadOnlyList<IReadOnlyList<int>> Adj(int n, params (int a, int b)[] edges)
    {
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (var (a, b) in edges) { adj[a].Add(b); adj[b].Add(a); }
        return adj;
    }

    [Fact]
    public void Source_is_always_the_backbone_tier()
    {
        // A star: centre 0 connected to 1..4. Whatever the scoring, the declared source is tier 0.
        var adj = Adj(5, (0, 1), (0, 2), (0, 3), (0, 4));
        int[] tier = TopologyTiers.Assign(5, adj, sourceIndex: 0);
        Assert.Equal(0, tier[0]);
    }

    [Fact]
    public void On_a_feeder_the_trunk_outranks_the_leaves()
    {
        // A 6-node chain 0-1-2-3-4-5 rooted at 0. Subtree size decreases along the trunk, so the
        // tier must be non-decreasing from source to the far leaf (backbone -> distribution).
        var adj = Adj(6, (0, 1), (1, 2), (2, 3), (3, 4), (4, 5));
        int[] tier = TopologyTiers.Assign(6, adj, sourceIndex: 0);
        for (int i = 0; i < 5; i++)
            Assert.True(tier[i] <= tier[i + 1], $"tier[{i}]={tier[i]} should be <= tier[{i + 1}]={tier[i + 1]}");
        Assert.Equal(0, tier[0]);                       // trunk root = backbone
        Assert.True(tier[5] >= tier[0]);                // far leaf no higher than the root
    }

    [Fact]
    public void On_a_mesh_the_bridge_hub_outranks_the_periphery()
    {
        // Two triangles {0,1,2} and {3,4,5} joined by a single bridge 2-3. Nodes 2 and 3 carry every
        // cross-cluster path (highest betweenness) so they must land in a higher tier than a leaf-ish
        // corner like 0. (No source given -> highest-degree fallback, still deterministic.)
        var adj = Adj(6, (0, 1), (1, 2), (2, 0), (3, 4), (4, 5), (5, 3), (2, 3));
        int[] tier = TopologyTiers.Assign(6, adj, sourceIndex: -1);
        Assert.True(tier[2] < tier[0], "bridge node 2 should be a higher (lower-index) tier than corner 0");
        Assert.True(tier[3] < tier[5], "bridge node 3 should be a higher tier than corner 5");
    }

    [Fact]
    public void Tiers_stay_within_the_kv_table()
    {
        var adj = Adj(6, (0, 1), (1, 2), (2, 3), (3, 4), (4, 5));
        int[] tier = TopologyTiers.Assign(6, adj, sourceIndex: 0);
        foreach (int t in tier)
        {
            Assert.InRange(t, 0, TopologyTiers.TierKv.Length - 1);
            Assert.Contains(TopologyTiers.KvForTier(t), TopologyTiers.TierKv);
        }
    }

    [Fact]
    public void Assignment_is_deterministic()
    {
        var adj = Adj(6, (0, 1), (1, 2), (2, 0), (3, 4), (4, 5), (5, 3), (2, 3));
        int[] a = TopologyTiers.Assign(6, adj, 0);
        int[] b = TopologyTiers.Assign(6, adj, 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Trivial_graphs_do_not_throw()
    {
        Assert.Empty(TopologyTiers.Assign(0, Adj(0), -1));
        Assert.Equal(new[] { 0 }, TopologyTiers.Assign(1, Adj(1), 0));
    }
}
