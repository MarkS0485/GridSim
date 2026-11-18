namespace GridSim.Core.Numerics;

/// <summary>
/// Exact minimum-degree fill-reducing ordering on the symmetrised pattern of a square sparse matrix (the pattern of
/// A + A^T, diagonal ignored). It replaced reverse Cuthill-McKee as the default LU column ordering: RCM reduces profile
/// and fills badly on meshed grids (ACTIVSg2000: nnz(L+U) 390k with RCM against 83k with minimum degree; 5x to 8x faster
/// Newton solves on the 2000 to 3400 bus cases). Any permutation solves the same system, so the answer moves by rounding
/// only.
///
/// <para>Method: the elimination graph is kept explicitly. Eliminating a node joins all its remaining neighbours into a
/// clique. A bucket queue (doubly linked lists indexed by current degree) picks the minimum-degree node. The degree is
/// exact, not an approximation. This is the classic algorithm, not quotient-graph AMD: on very large or very dense
/// patterns (GDA scale) the explicit cliques can cost time and memory, which is why a quotient-graph AMD is the follow-up.</para>
///
/// <para>Deterministic: no hashing, no randomness, no dependence on iteration order of any unordered collection.
/// Tie-break: among nodes of equal degree the most recently inserted one is chosen (LIFO bucket); the initial insertion is
/// in descending node index so the lowest index wins the first ties. The result depends only on the CSR structure
/// (RowPtr, ColIdx), never on the values.</para>
/// </summary>
internal static class MinimumDegreeOrdering
{
    /// <summary>The elimination order (position s holds the node eliminated s-th), a permutation of 0..N-1.</summary>
    internal static int[] Order(CsrMatrix a)
    {
        int n = a.N;
        if (n == 0) return Array.Empty<int>();
        int[] rowPtr = a.RowPtr, colIdx = a.ColIdx;

        // Symmetrised, de-duplicated adjacency in flat CSR-like arrays (start offsets, counts), built with two counting passes.
        var cnt = new int[n];
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                int j = colIdx[k];
                if (j != i) { cnt[i]++; cnt[j]++; }
            }
        var adj = new int[n][];
        for (int i = 0; i < n; i++) adj[i] = new int[cnt[i]];
        var len = new int[n];
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                int j = colIdx[k];
                if (j == i) continue;
                adj[i][len[i]++] = j;
                adj[j][len[j]++] = i;
            }
        var stamp = new int[n];
        int st = 0;
        for (int i = 0; i < n; i++)
        {
            st++;
            int w = 0;
            int[] ai = adj[i];
            for (int p = 0; p < len[i]; p++)
            {
                int j = ai[p];
                if (stamp[j] != st) { stamp[j] = st; ai[w++] = j; }
            }
            len[i] = w;
        }

        // Bucket queue by degree (degrees are at most n - 1).
        var head = new int[n + 1];
        Array.Fill(head, -1);
        var next = new int[n];
        var prev = new int[n];
        var deg = new int[n];
        for (int i = n - 1; i >= 0; i--) Insert(i, len[i], head, next, prev, deg);

        var order = new int[n];
        var dead = new bool[n];
        var nb = new int[n];
        int minDeg = 0;
        for (int s = 0; s < n; s++)
        {
            while (head[minDeg] < 0) minDeg++;
            int v = head[minDeg];
            Remove(v, head, next, prev, deg);
            dead[v] = true;
            order[s] = v;

            int nbCount = 0;
            int[] av = adj[v];
            for (int p = 0; p < len[v]; p++)
            {
                int u = av[p];
                if (!dead[u]) nb[nbCount++] = u;
            }
            st++;
            for (int t = 0; t < nbCount; t++) stamp[nb[t]] = st;
            int nbStamp = st;

            for (int t = 0; t < nbCount; t++)
            {
                int u = nb[t];
                Remove(u, head, next, prev, deg);
                int[] au = adj[u];
                int w = 0;
                for (int p = 0; p < len[u]; p++)
                {
                    int x = au[p];
                    if (x != v && !dead[x] && stamp[x] != nbStamp) au[w++] = x;
                }
                int need = w + nbCount - 1;
                if (au.Length < need)
                {
                    Array.Resize(ref au, Math.Max(need, au.Length * 2));
                    adj[u] = au;
                }
                for (int q = 0; q < nbCount; q++)
                    if (nb[q] != u) au[w++] = nb[q];
                len[u] = w;
                Insert(u, w, head, next, prev, deg);
            }
            adj[v] = Array.Empty<int>();
            len[v] = 0;
            if (minDeg > 0) minDeg--;   // a neighbour's degree can drop by at most one
        }
        return order;
    }

    private static void Insert(int v, int d, int[] head, int[] next, int[] prev, int[] deg)
    {
        deg[v] = d;
        prev[v] = -1;
        next[v] = head[d];
        if (head[d] >= 0) prev[head[d]] = v;
        head[d] = v;
    }

    private static void Remove(int v, int[] head, int[] next, int[] prev, int[] deg)
    {
        int d = deg[v];
        if (prev[v] >= 0) next[prev[v]] = next[v]; else head[d] = next[v];
        if (next[v] >= 0) prev[next[v]] = prev[v];
    }
}
