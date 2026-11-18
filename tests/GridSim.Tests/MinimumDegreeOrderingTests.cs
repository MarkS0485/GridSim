using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The default LU column ordering is exact minimum degree (MinimumDegreeOrdering), replacing reverse Cuthill-McKee.
/// It must be a deterministic permutation (the dense/sparse bit-identity contract and every digest rest on it) and
/// must not fill worse than RCM on a meshed network.
/// </summary>
public class MinimumDegreeOrderingTests
{
    private sealed class Capture : ISparseLinearSolver
    {
        private readonly CpuSparseLuSolver _inner = new();
        public CsrMatrix? Last;
        public string Name => "capture";
        public double[]? Solve(CsrMatrix a, double[] b) { Last = a; return _inner.Solve(a, b); }
    }

    private static CsrMatrix Case300Jacobian()
    {
        var cap = new Capture();
        Assert.True(new SparseNewtonRaphsonPowerFlow(cap).Solve(IeeeCases.Case300()).Converged);
        return cap.Last!;
    }

    private static CsrMatrix RandomPattern(int n, int seed)
    {
        var rng = new Random(seed);
        var bld = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++)
        {
            bld.Add(i, i, 4.0);
            for (int k = 0; k < 3; k++) { int j = rng.Next(n); if (j != i) bld.Add(i, j, 1.0); }
        }
        return bld.Build();
    }

    private static void AssertPermutation(int[] q, int n)
    {
        Assert.Equal(n, q.Length);
        var seen = new bool[n];
        foreach (int v in q) { Assert.InRange(v, 0, n - 1); Assert.False(seen[v]); seen[v] = true; }
    }

    [Fact]
    public void Ordering_is_a_permutation_and_identical_on_repeated_calls()
    {
        foreach (CsrMatrix a in new[] { Case300Jacobian(), RandomPattern(500, 11), RandomPattern(1, 3) })
        {
            int[] first = SparseLu.Ordering(a);
            int[] second = SparseLu.Ordering(a);
            AssertPermutation(first, a.N);
            Assert.True(first.AsSpan().SequenceEqual(second), "the ordering changed between two calls on the same input");
        }
    }

    [Fact]
    public void Ordering_depends_on_the_pattern_only_not_the_values()
    {
        CsrMatrix a = Case300Jacobian();
        var bld = new CsrMatrix.Builder(a.N);
        for (int i = 0; i < a.N; i++)
            for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++) bld.Add(i, a.ColIdx[k], a.Val[k] * 3.7 + 0.125);
        CsrMatrix scaled = bld.Build();
        Assert.True(SparseLu.Ordering(a).AsSpan().SequenceEqual(SparseLu.Ordering(scaled)));
    }

    [Fact]
    public void Minimum_degree_does_not_fill_worse_than_reverse_cuthill_mckee_on_a_meshed_case()
    {
        CsrMatrix j = Case300Jacobian();
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCsc(j);
        SparseLuFactorization md = SparseLuFactorization.Factor(j.N, ap, ai, ax, SparseLu.Ordering(j))!;
        SparseLuFactorization rcm = SparseLuFactorization.Factor(j.N, ap, ai, ax, SparseLu.ReverseCuthillMcKee(j))!;
        long mdFill = md.LValues.Length + md.UValues.Length, rcmFill = rcm.LValues.Length + rcm.UValues.Length;
        Assert.True(mdFill <= rcmFill, $"minimum degree nnz(L+U) {mdFill} exceeds RCM {rcmFill}");
    }

    [Fact]
    public void Minimum_degree_adds_no_fill_beyond_reverse_cuthill_mckee_on_a_path_graph()
    {
        // A tridiagonal matrix has a perfect elimination order (RCM finds one too), so minimum degree must match it exactly.
        const int n = 50;
        var bld = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++) { bld.Add(i, i, 4.0); if (i > 0) bld.Add(i, i - 1, -1.0); if (i < n - 1) bld.Add(i, i + 1, -1.0); }
        CsrMatrix a = bld.Build();
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCsc(a);
        SparseLuFactorization md = SparseLuFactorization.Factor(n, ap, ai, ax, SparseLu.Ordering(a))!;
        SparseLuFactorization rcm = SparseLuFactorization.Factor(n, ap, ai, ax, SparseLu.ReverseCuthillMcKee(a))!;
        Assert.Equal(rcm.LValues.Length + rcm.UValues.Length, md.LValues.Length + md.UValues.Length);
    }
}
