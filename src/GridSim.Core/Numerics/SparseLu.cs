namespace GridSim.Core.Numerics;

/// <summary>
/// A sparse direct linear solver  -  Gilbert-Peierls left-looking LU with partial
/// pivoting, over a fill-reducing reverse Cuthill-McKee column ordering. Unlike an
/// iterative method it solves Jx = b exactly in one pass regardless of how stiff the
/// power-flow Jacobian is (short cables -> huge admittances -> ill-conditioning), which
/// is what the full node-level GDA replica needs.
///
/// The numeric factorisation lives in <see cref="SparseLuFactorization"/>: for each
/// column, a single sparse triangular solve whose nonzero pattern is found by a
/// depth-first reach, so the work tracks the true post-fill nonzeros. This replaced an
/// older dynamic scheme built on <c>Dictionary&lt;int,double&gt;</c> rows that paid hash
/// overhead and re-derived the sparsity pattern on every solve.
/// </summary>
public static class SparseLu
{
    /// <summary>Solve Ax = b. Returns null if A is structurally/numerically singular.</summary>
    public static double[]? Solve(CsrMatrix a, double[] b)
    {
        if (a.N == 0) return Array.Empty<double>();
        return Factor(a)?.Solve(b);
    }

    /// <summary>
    /// Factor A once (CSR->CSC conversion + RCM ordering + Gilbert-Peierls LU) for repeated
    /// right-hand sides - the state estimator's residual-covariance path solves the same gain
    /// matrix against one column per measurement. Returns null when singular.
    /// </summary>
    internal static SparseLuFactorization? Factor(CsrMatrix a)
    {
        if (a.N == 0) return null;
        return FactorWithOrdering(a, ReverseCuthillMcKee(a));
    }

    /// <summary><see cref="Factor(CsrMatrix)"/> under the OPT-IN diagonal-preference pivot rule
    /// (<see cref="SparseLuFactorization.Factor"/>); 0 is exactly <see cref="Factor(CsrMatrix)"/>.</summary>
    internal static SparseLuFactorization? Factor(CsrMatrix a, double diagonalPreference)
    {
        if (a.N == 0) return null;
        (int[] ap, int[] ai, double[] ax) = ToCsc(a);
        return SparseLuFactorization.Factor(a.N, ap, ai, ax, ReverseCuthillMcKee(a), diagonalPreference: diagonalPreference);
    }

    /// <summary>
    /// Numeric factorisation reusing a PRECOMPUTED column ordering. The reverse Cuthill-McKee
    /// ordering depends only on the sparsity PATTERN, which is fixed across the Newton iterations of
    /// one power-flow pass (same topology, same bus types) - so a caller that solves the same
    /// structure repeatedly (the NR loop) computes <see cref="Ordering"/> once and calls this each
    /// iteration, skipping the RCM re-derivation. The result is bit-identical to <see cref="Factor"/>
    /// because RCM is a pure function of the structure. Returns null when singular.
    /// </summary>
    internal static SparseLuFactorization? FactorWithOrdering(CsrMatrix a, int[] q)
    {
        int n = a.N;
        if (n == 0) return null;
        (int[] ap, int[] ai, double[] ax) = ToCsc(a);
        return SparseLuFactorization.Factor(n, ap, ai, ax, q);
    }

    /// <summary>The CSC form of <paramref name="a"/> the factorisation walks, explicit zeros dropped (entries within a
    /// column row-sorted). Shared by <see cref="FactorWithOrdering"/> and the plan-reusing solver path.</summary>
    internal static (int[] Ap, int[] Ai, double[] Ax) ToCsc(CsrMatrix a)
    {
        int n = a.N;
        // Convert CSR -> CSC (the left-looking factorisation walks columns of A).
        // Explicit zeros are dropped, matching the old dynamic solver's semantics and
        // avoiding spurious structural fill.
        int[] rowPtr = a.RowPtr, colIdx = a.ColIdx;
        double[] val = a.Val;

        var ap = new int[n + 1];
        for (int k = 0; k < colIdx.Length; k++)
            if (val[k] != 0.0) ap[colIdx[k] + 1]++;
        for (int c = 0; c < n; c++) ap[c + 1] += ap[c];

        int nnz = ap[n];
        var ai = new int[nnz];
        var ax = new double[nnz];
        var fill = (int[])ap.Clone();
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                double v = val[k];
                if (v == 0.0) continue;
                int dst = fill[colIdx[k]]++;
                ai[dst] = i;   // row index (entries within a column end up row-sorted)
                ax[dst] = v;
            }
        return (ap, ai, ax);
    }

    /// <summary>The CSC form of <paramref name="a"/> with EVERY stored entry kept, explicit zeros included, so the CSC
    /// pattern is a function of the CSR structure alone and never of the values. Only the opt-in fixed-pattern solver
    /// (<see cref="PowerFlow.FixedPatternSparseLuSolver"/>) uses it: the default path (<see cref="ToCsc"/>) drops exact
    /// zeros, and keeping them changes the depth-first reach order and so the last bits of a solve.</summary>
    internal static (int[] Ap, int[] Ai, double[] Ax) ToCscKeepingZeros(CsrMatrix a)
    {
        int n = a.N;
        int[] rowPtr = a.RowPtr, colIdx = a.ColIdx;
        double[] val = a.Val;
        var ap = new int[n + 1];
        int stored = rowPtr[n];
        for (int k = 0; k < stored; k++) ap[colIdx[k] + 1]++;
        for (int c = 0; c < n; c++) ap[c + 1] += ap[c];
        var ai = new int[stored];
        var ax = new double[stored];
        var fill = (int[])ap.Clone();
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                int dst = fill[colIdx[k]]++;
                ai[dst] = i;   // row index (entries within a column end up row-sorted)
                ax[dst] = val[k];
            }
        return (ap, ai, ax);
    }

    /// <summary>The fill-reducing column ordering alone - for callers whose matrix STRUCTURE is
    /// fixed across many numeric factorizations (the estimator's gain matrix per pass): compute
    /// once, then refactor values with <see cref="FactorPrepared"/> each iteration.</summary>
    internal static int[] Ordering(CsrMatrix a) => ReverseCuthillMcKee(a);

    /// <summary>Numeric factorization of an already-prepared CSC matrix with a precomputed
    /// ordering - skips the CSR->CSC conversion and RCM that <see cref="Factor"/> redoes per call.
    /// Returns null when singular.</summary>
    internal static SparseLuFactorization? FactorPrepared(int n, int[] ap, int[] ai, double[] ax, int[] q)
    {
        // GR-462: the caller prepares the CSC arrays and the ordering itself, so check them (O(n + nnz), small beside
        // the factorisation): a repeated ordering entry or an out-of-range row index factored the wrong structure and
        // was reported late as "singular" instead of as the programming error it is.
        ValidatePrepared(n, ap, ai, ax, q);
        return SparseLuFactorization.Factor(n, ap, ai, ax, q);
    }

    private static void ValidatePrepared(int n, int[] ap, int[] ai, double[] ax, int[] q)
    {
        ArgumentNullException.ThrowIfNull(ap);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(ax);
        ArgumentNullException.ThrowIfNull(q);
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (ap.Length != n + 1 || ap[0] != 0)
            throw new ArgumentException($"sparse LU: the column pointers must hold {n + 1} entries starting at 0.", nameof(ap));
        for (int c = 0; c < n; c++)
            if (ap[c + 1] < ap[c])
                throw new ArgumentException($"sparse LU: column pointer {c + 1} is below column pointer {c}.", nameof(ap));
        int nnz = ap[n];
        if (ai.Length < nnz || ax.Length < nnz)
            throw new ArgumentException($"sparse LU: the column pointers declare {nnz} entries but the arrays hold fewer.", nameof(ai));
        for (int k = 0; k < nnz; k++)
            if ((uint)ai[k] >= (uint)n)
                throw new ArgumentException($"sparse LU: row index {ai[k]} at entry {k} is outside 0..{n - 1}.", nameof(ai));
        if (q.Length != n)
            throw new ArgumentException($"sparse LU: the ordering holds {q.Length} entries for {n} columns.", nameof(q));
        var seen = new bool[n];
        for (int k = 0; k < n; k++)
        {
            if ((uint)q[k] >= (uint)n || seen[q[k]])
                throw new ArgumentException($"sparse LU: the ordering is not a permutation of 0..{n - 1} (entry {k} is {q[k]}).", nameof(q));
            seen[q[k]] = true;
        }
    }

    /// <summary>Reverse Cuthill-McKee ordering of the (structurally symmetrised) matrix graph  -  shrinks fill.</summary>
    private static int[] ReverseCuthillMcKee(CsrMatrix a)
    {
        int n = a.N;
        int[] rowPtr = a.RowPtr, colIdx = a.ColIdx;

        // Build a deduplicated, symmetric adjacency in CSR-like arrays (no per-node LINQ).
        // Count upper bound on undirected edges (each off-diagonal contributes to both ends).
        var deg = new int[n];
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                int j = colIdx[k];
                if (j != i) { deg[i]++; deg[j]++; }
            }

        var adjPtr = new int[n + 1];
        for (int i = 0; i < n; i++) adjPtr[i + 1] = adjPtr[i] + deg[i];
        var adj = new int[adjPtr[n]];
        var pos = (int[])adjPtr.Clone();
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
            {
                int j = colIdx[k];
                if (j != i) { adj[pos[i]++] = j; adj[pos[j]++] = i; }
            }

        // Deduplicate each adjacency list (sort + unique) and record the true degree.
        var degree = new int[n];
        for (int i = 0; i < n; i++)
        {
            int s = adjPtr[i], e = adjPtr[i + 1];
            if (e - s > 1) Array.Sort(adj, s, e - s);
            int w = s;
            for (int p = s; p < e; p++)
                if (p == s || adj[p] != adj[p - 1]) adj[w++] = adj[p];
            degree[i] = w - s;
            for (int p = w; p < e; p++) adj[p] = -1; // pad tail (skipped below)
        }

        var visited = new bool[n];
        var order = new int[n];
        int oi = 0;
        var queue = new int[n];

        // Process components, starting each from a minimum-degree node.
        // Equal-degree ties are common in grid graphs, and the degree comparer calls them equal, so WHICH tied node
        // comes first is decided by the sort algorithm itself. It is GridSim's frozen copy of the .NET 10 introsort
        // (FrozenIntroSort), not Array.Sort: the order is then GridSim's own on every runtime, and on .NET 10 it is
        // exactly the order Array.Sort gave (checked by FrozenIntroSortTests), so no permutation and no sparse result
        // moved. A runtime that retuned its introsort used to be able to change every sparse LU's pivot order.
        Comparison<int> byDegreeCmp = (x, y) => degree[x].CompareTo(degree[y]);
        var byDegree = new int[n];
        for (int i = 0; i < n; i++) byDegree[i] = i;
        FrozenIntroSort.Sort(byDegree, byDegreeCmp);

        // Reusable buffer for sorting a node's unvisited neighbours by degree (one comparer for the whole call).
        var nbr = new int[n];

        foreach (int start in byDegree)
        {
            if (visited[start]) continue;
            visited[start] = true;
            int qh = 0, qt = 0;
            queue[qt++] = start;
            while (qh < qt)
            {
                int u = queue[qh++];
                order[oi++] = u;
                int cnt = 0;
                int s = adjPtr[u];
                for (int p = 0; p < degree[u]; p++)
                {
                    int v = adj[s + p];
                    if (!visited[v]) { visited[v] = true; nbr[cnt++] = v; }
                }
                if (cnt > 1) FrozenIntroSort.Sort(nbr, 0, cnt, byDegreeCmp);
                for (int p = 0; p < cnt; p++) queue[qt++] = nbr[p];
            }
        }

        Array.Reverse(order, 0, oi); // reverse Cuthill-McKee
        return order;
    }
}
