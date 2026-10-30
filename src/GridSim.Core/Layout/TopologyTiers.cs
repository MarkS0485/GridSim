namespace GridSim.Core.Layout;

/// <summary>
/// Synthesises a <b>voltage hierarchy from pure graph topology</b> for coordinate-/voltage-less
/// imported cases (IEEE / MATPOWER). A real grid layers 400/275 kV transmission -> 132 kV
/// sub-transmission -> 33/11 kV distribution; a bare test case has no such tiers, so the map draws
/// every bus identically and reads flat. This assigns each bus a <i>display tier</i> (0 = backbone,
/// higher = deeper into distribution) from its structural role, so the renderer can size roundels,
/// weight rings and reveal detail exactly as it does for the real GB network.
///
/// <para><b>This is a visualisation aid, not electrical data.</b> The power-flow solve is untouched
/// and tooltips keep each bus's true nominal kV. The tier only drives how the topology is drawn.</para>
///
/// <para>Two structural signals, both deterministic:</para>
/// <list type="bullet">
///   <item><b>Radial feeders</b> (a tree): the downstream subtree size rooted at the source. The
///   trunk carries everything (large subtree) -> backbone; twigs carry little -> distribution. This
///   is monotonic from source to leaf, i.e. a clean feeder hierarchy.</item>
///   <item><b>Meshed networks</b>: betweenness centrality (Brandes). The backbone that most
///   shortest paths traverse scores highest -> transmission; the periphery -> distribution.</item>
/// </list>
/// Scores are ranked and bucketed into a pyramid (few backbone buses, many distribution buses),
/// and the source bus is always pinned to the top tier.
/// </summary>
public static class TopologyTiers
{
    /// <summary>Nominal kV a tier maps to (index = tier; 0 = backbone). Mirrors the GB voltage set
    /// so the renderer's existing kV-driven sizing/level-of-detail produces a familiar hierarchy.</summary>
    public static readonly double[] TierKv = { 400, 275, 132, 33, 11 };

    /// <summary>Cumulative rank fraction at which each tier ends - a pyramid: ~6% backbone,
    /// then 275/132/33, with the ~40% tail as 11 kV distribution.</summary>
    private static readonly double[] CumulativeFraction = { 0.06, 0.16, 0.34, 0.62, 1.00 };

    /// <summary>The display kV for a tier index (clamped to the table).</summary>
    public static double KvForTier(int tier) => TierKv[Math.Clamp(tier, 0, TierKv.Length - 1)];

    /// <summary>
    /// Assign a display tier (0..<c>TierKv.Length-1</c>) to every node.
    /// </summary>
    /// <param name="n">Node count.</param>
    /// <param name="adjacency">Undirected adjacency (each edge appears in both lists).</param>
    /// <param name="sourceIndex">The source/slack node, pinned to the top tier; -1 if none.</param>
    public static int[] Assign(int n, IReadOnlyList<IReadOnlyList<int>> adjacency, int sourceIndex)
    {
        var tier = new int[n];
        if (n == 0) return tier;
        if (n == 1) { tier[0] = 0; return tier; }

        int edgeCount = 0;
        for (int i = 0; i < n; i++) edgeCount += adjacency[i].Count;
        edgeCount /= 2;   // undirected

        int root = sourceIndex is >= 0 && sourceIndex < n ? sourceIndex : HighestDegree(adjacency, n);

        // Tree => subtree size (monotonic feeder hierarchy); otherwise betweenness centrality.
        double[] score = edgeCount == n - 1
            ? SubtreeSizes(n, adjacency, root)
            : Betweenness(n, adjacency);

        // Rank by score (desc), ties broken by index for determinism.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = score[b].CompareTo(score[a]);
            return c != 0 ? c : a.CompareTo(b);
        });

        // Bucket ranks into the pyramid tiers.
        for (int rank = 0; rank < n; rank++)
        {
            double frac = (rank + 1) / (double)n;
            int t = 0;
            while (t < CumulativeFraction.Length - 1 && frac > CumulativeFraction[t]) t++;
            tier[order[rank]] = t;
        }

        tier[root] = 0;   // the source is always the backbone
        return tier;
    }

    private static int HighestDegree(IReadOnlyList<IReadOnlyList<int>> adj, int n)
    {
        int best = 0, bestDeg = -1;
        for (int i = 0; i < n; i++)
            if (adj[i].Count > bestDeg) { bestDeg = adj[i].Count; best = i; }
        return best;
    }

    /// <summary>Downstream subtree size for every node when the tree is rooted at <paramref name="root"/>.</summary>
    private static double[] SubtreeSizes(int n, IReadOnlyList<IReadOnlyList<int>> adj, int root)
    {
        var size = new double[n];
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = -2;   // -2 = unvisited
        // Iterative post-order over the tree.
        var order = new List<int>(n);
        var stack = new Stack<int>();
        stack.Push(root); parent[root] = -1;
        while (stack.Count > 0)
        {
            int v = stack.Pop();
            order.Add(v);
            foreach (int w in adj[v])
                if (parent[w] == -2) { parent[w] = v; stack.Push(w); }
        }
        for (int i = 0; i < n; i++) size[i] = 1;
        // Accumulate child sizes into parents in reverse discovery order (children before parents).
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int v = order[i];
            if (parent[v] >= 0) size[parent[v]] += size[v];
        }
        return size;
    }

    /// <summary>Unweighted betweenness centrality (Brandes 2001). Deterministic; O(n*(n+m)).</summary>
    private static double[] Betweenness(int n, IReadOnlyList<IReadOnlyList<int>> adj)
    {
        var cb = new double[n];
        // R123: the per-source buffers are allocated ONCE and reset per source (the old code allocated n predecessor
        // lists plus sigma / dist / delta arrays for every source: about 12.5M List allocations on gb-full). Each
        // predecessor list is cleared, not replaced, so it is refilled in the same order and every accumulation below
        // runs in the same order: the result is bit-identical.
        var stack = new Stack<int>();
        var pred = new List<int>[n];
        for (int i = 0; i < n; i++) pred[i] = new List<int>();
        var sigma = new double[n];
        var dist = new int[n];
        var delta = new double[n];
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            for (int i = 0; i < n; i++) { pred[i].Clear(); dist[i] = -1; sigma[i] = 0; delta[i] = 0; }
            stack.Clear();
            q.Clear();
            sigma[s] = 1; dist[s] = 0;
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
}
