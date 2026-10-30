using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse bounded revised simplex (<see cref="SparseBoundedSimplex"/>): textbook LPs with known optima and duals,
/// Chvatal's cycling example, agreement with the dense <see cref="BoundedSimplex"/> on generated bounded LPs (including runs
/// long enough to rebuild the factors), infeasibility proven, unboundedness found, free and fixed columns, an LP with no
/// interior point (which the interior point method cannot solve), bound overrides, bit-for-bit repeatability and the
/// refusals.
/// </summary>
public class SparseBoundedSimplexTests
{
    private const double Inf = double.PositiveInfinity;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static SparseLpProblem Dense(double[,] a, double[] b, double[] c, double[] lo, double[] hi)
    {
        var e = new List<(int, int, double)>();
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                if (a[i, j] != 0) e.Add((i, j, a[i, j]));
        return new SparseLpProblem(a.GetLength(0), a.GetLength(1), e, b, c, lo, hi);
    }

    [Fact]
    public void The_textbook_production_lp_reaches_36_at_2_6_with_its_shadow_prices()
    {
        // Wyndor Glass (as in SparseInteriorPointLpTests): optimum -36 at x1 = 2, x2 = 6; duals 0, -1.5, -1.
        var a = new double[,] { { 1, 0, 1, 0, 0 }, { 0, 2, 0, 1, 0 }, { 3, 2, 0, 0, 1 } };
        SparseSimplexResult r = SparseBoundedSimplex.Solve(Dense(a, new double[] { 4, 12, 18 }, new double[] { -3, -5, 0, 0, 0 },
            new double[5], new[] { Inf, Inf, Inf, Inf, Inf }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-36.0, r.Objective, 12);
        Assert.Equal(2.0, r.X[0], 12);
        Assert.Equal(6.0, r.X[1], 12);
        Assert.Equal(0.0, r.Y[0], 12);
        Assert.Equal(-1.5, r.Y[1], 12);
        Assert.Equal(-1.0, r.Y[2], 12);
    }

    [Fact]
    public void Chvatals_cycling_example_reaches_its_optimum()
    {
        // Chvatal, Linear Programming (1983), ch. 3: with the largest-coefficient entering rule and the lowest-index
        // leaving rule the textbook simplex cycles on this LP. maximise 10 x1 - 57 x2 - 9 x3 - 24 x4 subject to
        //   0.5 x1 - 5.5 x2 - 2.5 x3 + 9 x4 <= 0,  0.5 x1 - 1.5 x2 - 0.5 x3 + x4 <= 0,  x1 <= 1,  x >= 0.
        // Optimum 1 at x = (1, 0, 1, 0). As a minimisation with slacks x5, x6, x7: -1.
        var a = new double[,]
        {
            { 0.5, -5.5, -2.5, 9, 1, 0, 0 },
            { 0.5, -1.5, -0.5, 1, 0, 1, 0 },
            { 1, 0, 0, 0, 0, 0, 1 },
        };
        SparseSimplexResult r = SparseBoundedSimplex.Solve(Dense(a, new double[] { 0, 0, 1 }, new double[] { -10, 57, 9, 24, 0, 0, 0 },
            new double[7], Enumerable.Repeat(Inf, 7).ToArray()));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-1.0, r.Objective, 12);
        Assert.Equal(1.0, r.X[0], 12);
        Assert.Equal(1.0, r.X[2], 12);
    }

    [Fact]
    public void Generated_bounded_lps_agree_with_the_dense_simplex()
    {
        // 60 generated LPs (equality rows with slack columns, finite and infinite bounds, some infeasible); both solvers
        // must agree on the status and the optimum. The larger ones run past the 50-change refactorisation interval.
        var rng = new Random(20261025);
        int optimal = 0, infeasible = 0, longRuns = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            int m = 2 + rng.Next(trial < 40 ? 6 : 40), nx = m + 1 + rng.Next(trial < 40 ? 6 : 40);
            int n = nx + m;
            var a = new double[m, n];
            var b = new double[m];
            var c = new double[n];
            var lo = new double[n];
            var hi = new double[n];
            for (int j = 0; j < nx; j++)
            {
                c[j] = rng.Next(-9, 10);
                lo[j] = rng.Next(3) == 0 ? -rng.Next(1, 4) : 0;
                hi[j] = rng.Next(4) == 0 ? Inf : lo[j] + rng.Next(1, 8);
            }
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < nx; j++) a[i, j] = rng.Next(4) == 0 ? rng.Next(-5, 6) : 0;
                a[i, nx + i] = 1;              // a slack: rows are "<=" constraints
                hi[nx + i] = Inf;
                b[i] = rng.Next(-4, 20);
            }
            for (int j = 0; j < nx; j++) if (hi[j] == Inf && c[j] < 0) c[j] = -c[j];   // keep most LPs bounded
            SparseSimplexResult r = SparseBoundedSimplex.Solve(Dense(a, b, c, lo, hi));
            (LpStatus st, double[] _, double obj) = BoundedSimplex.Solve(a, b, c, lo, hi);
            if (st == LpStatus.Optimal)
            {
                optimal++;
                Assert.True(r.Optimal, $"trial {trial}: {r.Message}");
                Assert.True(Math.Abs(r.Objective - obj) <= 1e-8 * (1 + Math.Abs(obj)), $"trial {trial}: {r.Objective:R} vs dense {obj:R}");
                if (r.Iterations > SparseBoundedSimplex.RefactorInterval) longRuns++;
            }
            else if (st == LpStatus.Infeasible)
            {
                infeasible++;
                Assert.Equal(SparseSimplexStatus.Infeasible, r.Status);
            }
            else if (st == LpStatus.Unbounded)
            {
                Assert.Equal(SparseSimplexStatus.Unbounded, r.Status);
            }
        }
        Assert.True(optimal >= 30, $"only {optimal} optimal trials");
        Assert.True(infeasible >= 1, "the generator should include an infeasible LP");
        Assert.True(longRuns >= 1, "at least one run should pass the refactorisation interval");
    }

    [Fact]
    public void Infeasibility_is_proven_and_unboundedness_found()
    {
        // x + y = 5 with x, y in [0, 1]: least infeasibility 3.
        var inf = SparseBoundedSimplex.Solve(Dense(new double[,] { { 1, 1 } }, new double[] { 5 }, new double[] { 1, 1 }, new double[2], new double[] { 1, 1 }));
        Assert.Equal(SparseSimplexStatus.Infeasible, inf.Status);
        Assert.Contains("least sum of infeasibilities is 3", inf.Message);
        // minimise -x subject to x - y = 0, x, y >= 0: unbounded.
        var unb = SparseBoundedSimplex.Solve(Dense(new double[,] { { 1, -1 } }, new double[] { 0 }, new double[] { -1, 0 }, new double[2], new[] { Inf, Inf }));
        Assert.Equal(SparseSimplexStatus.Unbounded, unb.Status);
    }

    [Fact]
    public void Free_and_fixed_columns_and_an_lp_with_no_interior_point_are_solved()
    {
        // A free column (both bounds infinite), a fixed column (lo = hi), and a feasible set that is a single point:
        // x0 free, x1 fixed at 2, x2 in [0, 3]; x0 + x1 + x2 = 5 and x2 - x1 = 1 force x2 = 3 = its upper bound, x0 = 0.
        var a = new double[,] { { 1, 1, 1 }, { 0, -1, 1 } };
        SparseSimplexResult r = SparseBoundedSimplex.Solve(Dense(a, new double[] { 5, 1 }, new double[] { 1, 1, -1 },
            new[] { double.NegativeInfinity, 2, 0 }, new[] { Inf, 2, 3 }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 0.0, 2.0, 3.0 }, r.X);
        Assert.Equal(-1.0, r.Objective);
    }

    [Fact]
    public void Bound_overrides_replace_the_problems_bounds()
    {
        var a = new double[,] { { 1, 1 } };
        SparseLpProblem lp = Dense(a, new double[] { 4 }, new double[] { 1, 2 }, new double[2], new[] { Inf, Inf });
        Assert.Equal(4.0, SparseBoundedSimplex.Solve(lp).Objective);                                      // x0 = 4
        Assert.Equal(7.0, SparseBoundedSimplex.Solve(lp, new double[2], new double[] { 1, Inf }).Objective);   // x0 = 1, x1 = 3
    }

    [Fact]
    public void The_solve_is_bit_identical_from_run_to_run()
    {
        var rng = new Random(7);
        int m = 30, n = 70;
        var e = new List<(int, int, double)>();
        for (int i = 0; i < m; i++) { e.Add((i, n - m + i, 1.0)); for (int j = 0; j < n - m; j++) if (rng.Next(3) == 0) e.Add((i, j, rng.NextDouble() * 4 - 1)); }
        var b = Enumerable.Range(0, m).Select(_ => rng.NextDouble() * 10).ToArray();
        var c = Enumerable.Range(0, n).Select(j => j < n - m ? -rng.NextDouble() : 0).ToArray();
        var hi = Enumerable.Range(0, n).Select(j => j < n - m ? 1 + rng.NextDouble() : Inf).ToArray();
        var lp = new SparseLpProblem(m, n, e, b, c, new double[n], hi);
        SparseSimplexResult r1 = SparseBoundedSimplex.Solve(lp), r2 = SparseBoundedSimplex.Solve(lp);
        Assert.True(r1.Optimal, r1.Message);
        Assert.Equal(r1.Iterations, r2.Iterations);
        Assert.Equal(Bits(r1.Objective), Bits(r2.Objective));
        Assert.Equal(r1.X.Select(Bits), r2.X.Select(Bits));
        Assert.Equal(r1.Y.Select(Bits), r2.Y.Select(Bits));
    }

    [Fact]
    public void Malformed_problems_are_refused()
    {
        var e = new List<(int, int, double)> { (0, 0, 1.0) };
        Assert.Throws<ArgumentException>(() => SparseBoundedSimplex.Solve(new SparseLpProblem(1, 1, e, new double[] { 1 }, new double[] { 1 }, new double[] { 2 }, new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseBoundedSimplex.Solve(new SparseLpProblem(1, 1, new List<(int, int, double)> { (1, 0, 1.0) }, new double[] { 1 }, new double[] { 1 }, new double[1], new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseBoundedSimplex.Solve(new SparseLpProblem(1, 1, e, new double[] { double.NaN }, new double[] { 1 }, new double[1], new double[] { 1 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseBoundedSimplex.Solve(new SparseLpProblem(1, 1, e, new double[] { 1 }, new double[] { 1 }, new double[1], new double[] { 1 }), tolerance: 0));
    }
}
