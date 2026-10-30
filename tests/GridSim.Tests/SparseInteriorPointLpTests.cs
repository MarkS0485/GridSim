using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse interior point LP (<see cref="SparseInteriorPointLp"/>): textbook LPs with known optima, agreement with the
/// dense bounded simplex (<see cref="BoundedSimplex"/>) on generated LPs, the duals (strong duality and the shadow price of
/// a constraint), free variables, a rank-deficient A, infeasible and unbounded LPs reported as such, bit-for-bit
/// repeatability, and malformed problems refused.
/// </summary>
public class SparseInteriorPointLpTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static SparseLpProblem Dense(double[,] a, double[] b, double[] c, double[] lo, double[] hi)
    {
        var e = new List<(int, int, double)>();
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                if (a[i, j] != 0) e.Add((i, j, a[i, j]));
        return new SparseLpProblem(a.GetLength(0), a.GetLength(1), e, b, c, lo, hi);
    }

    private const double Inf = double.PositiveInfinity;

    [Fact]
    public void The_textbook_production_lp_reaches_36_at_2_6()
    {
        // Hillier and Lieberman's Wyndor Glass: maximise 3 x1 + 5 x2 s.t. x1 <= 4, 2 x2 <= 12, 3 x1 + 2 x2 <= 18, x >= 0.
        // As a minimisation with slacks x3, x4, x5: optimum -36 at x1 = 2, x2 = 6.
        var a = new double[,] { { 1, 0, 1, 0, 0 }, { 0, 2, 0, 1, 0 }, { 3, 2, 0, 0, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 4, 12, 18 }, new double[] { -3, -5, 0, 0, 0 },
            new double[5], new[] { Inf, Inf, Inf, Inf, Inf }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-36.0, r.Objective, 7);
        Assert.Equal(2.0, r.X[0], 7);
        Assert.Equal(6.0, r.X[1], 7);
        // The shadow prices: the second and third constraints bind (y = -1.5 and -1 for the minimisation), the first does not.
        Assert.Equal(0.0, r.Y[0], 7);
        Assert.Equal(-1.5, r.Y[1], 7);
        Assert.Equal(-1.0, r.Y[2], 7);
    }

    [Fact]
    public void Strong_duality_holds_at_the_optimum()
    {
        var a = new double[,] { { 1, 1, 1, 0 }, { 1, 3, 0, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 4, 6 }, new double[] { -1, -2, 0, 0 },
            new double[4], new[] { Inf, Inf, Inf, Inf }));
        Assert.True(r.Optimal, r.Message);
        double dual = 4 * r.Y[0] + 6 * r.Y[1];   // all lower bounds are 0 and no upper bound is finite
        Assert.Equal(r.Objective, dual, 7);
        Assert.Equal(-5.0, r.Objective, 7);       // x = (3, 1)
    }

    [Fact]
    public void Upper_bounds_are_native_and_their_duals_price_them()
    {
        // minimise -x1 - x2 s.t. x1 + x2 + s = 3, 0 <= x1 <= 1, 0 <= x2 <= 1.5: both upper bounds bind (optimum -2.5).
        var a = new double[,] { { 1, 1, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 3 }, new double[] { -1, -1, 0 },
            new double[3], new[] { 1.0, 1.5, Inf }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-2.5, r.Objective, 7);
        Assert.Equal(1.0, r.UpperDual[0], 6);
        Assert.Equal(1.0, r.UpperDual[1], 6);
        Assert.Equal(0.0, r.UpperDual[2], 7);
    }

    [Fact]
    public void A_free_variable_is_solved()
    {
        // minimise x1 + 2 x2 s.t. x1 - x2 = -1, x2 + x3 = 3, x1 free, x2, x3 >= 0: x2 = 0, x1 = -1, objective -1.
        var a = new double[,] { { 1, -1, 0 }, { 0, 1, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { -1, 3 }, new double[] { 1, 2, 0 },
            new[] { double.NegativeInfinity, 0, 0 }, new[] { Inf, Inf, Inf }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(-1.0, r.Objective, 7);
        Assert.Equal(-1.0, r.X[0], 7);
    }

    [Fact]
    public void A_repeated_row_is_handled()
    {
        // The same constraint twice (rank-deficient A): the regularisation keeps the system solvable.
        var a = new double[,] { { 1, 1, 1 }, { 1, 1, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 2, 2 }, new double[] { 1, 2, 3 },
            new double[3], new[] { Inf, Inf, Inf }));
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(2.0, r.Objective, 6);
    }

    [Theory]
    [InlineData(1, 6, 10)]
    [InlineData(2, 12, 30)]
    [InlineData(3, 25, 60)]
    [InlineData(4, 40, 100)]
    public void Generated_lps_agree_with_the_dense_bounded_simplex(int seed, int m, int n)
    {
        // A sparse random A (about 3 entries per column), a point x0 inside the box giving b = A x0 (so the LP is
        // feasible), finite boxes (so it is bounded) and a random c. Both methods must reach the same optimal objective.
        ulong state = 0x2545F4914F6CDD1DUL * (ulong)seed;
        double Next() { state = state * 6364136223846793005UL + 1442695040888963407UL; return (state >> 11) * (1.0 / 9007199254740992.0); }
        var a = new double[m, n];
        var lo = new double[n];
        var hi = new double[n];
        var x0 = new double[n];
        var c = new double[n];
        for (int j = 0; j < n; j++)
        {
            for (int k = 0; k < 3; k++) a[(int)(Next() * m), j] += Math.Round(Next() * 20 - 10);
            lo[j] = -Math.Round(Next() * 5);
            hi[j] = Math.Round(Next() * 5) + 1;
            x0[j] = lo[j] + (hi[j] - lo[j]) * Next();
            c[j] = Math.Round(Next() * 20 - 10);
        }
        var b = new double[m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) b[i] += a[i, j] * x0[j];

        SparseLpResult ipm = SparseInteriorPointLp.Solve(Dense(a, b, c, lo, hi));
        (LpStatus st, double[] xs, double objS) = BoundedSimplex.Solve(a, b, c, lo, hi);
        Assert.Equal(LpStatus.Optimal, st);
        Assert.True(ipm.Optimal, ipm.Message);
        Assert.True(Math.Abs(ipm.Objective - objS) <= 1e-7 * (1 + Math.Abs(objS)), $"interior point {ipm.Objective:R}, simplex {objS:R}");
        // The interior point optimum is feasible.
        for (int i = 0; i < m; i++)
        {
            double ax = 0;
            for (int j = 0; j < n; j++) ax += a[i, j] * ipm.X[j];
            Assert.True(Math.Abs(ax - b[i]) <= 1e-7 * (1 + Math.Abs(b[i])), $"row {i}: {ax:R} vs {b[i]:R}");
        }
        for (int j = 0; j < n; j++) Assert.InRange(ipm.X[j], lo[j] - 1e-7, hi[j] + 1e-7);
        _ = xs;
    }

    [Fact]
    public void An_infeasible_lp_is_not_reported_optimal()
    {
        // x1 + x2 = 5 with 0 <= x <= 1: no feasible point.
        var a = new double[,] { { 1, 1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 5 }, new double[] { 1, 1 }, new double[2], new[] { 1.0, 1.0 }));
        Assert.False(r.Optimal);
        Assert.NotEqual(SparseLpStatus.Optimal, r.Status);
    }

    [Fact]
    public void An_unbounded_lp_is_not_reported_optimal()
    {
        // minimise -x1 s.t. x1 - x2 = 0, x >= 0: x1 can grow without bound.
        var a = new double[,] { { 1, -1 } };
        SparseLpResult r = SparseInteriorPointLp.Solve(Dense(a, new double[] { 0 }, new double[] { -1, 0 }, new double[2], new[] { Inf, Inf }));
        Assert.False(r.Optimal);
        Assert.Equal(SparseLpStatus.InfeasibleOrUnbounded, r.Status);
    }

    [Fact]
    public void Two_solves_are_bit_identical()
    {
        var a = new double[,] { { 1, 0, 1, 0, 0 }, { 0, 2, 0, 1, 0 }, { 3, 2, 0, 0, 1 } };
        SparseLpProblem lp = Dense(a, new double[] { 4, 12, 18 }, new double[] { -3, -5, 0, 0, 0 }, new double[5], new[] { Inf, Inf, Inf, Inf, Inf });
        SparseLpResult p = SparseInteriorPointLp.Solve(lp);
        SparseLpResult q = SparseInteriorPointLp.Solve(lp);
        Assert.Equal(p.Iterations, q.Iterations);
        Assert.Equal(Bits(p.Objective), Bits(q.Objective));
        Assert.Equal(p.X.Select(Bits), q.X.Select(Bits));
        Assert.Equal(p.Y.Select(Bits), q.Y.Select(Bits));
    }

    [Fact]
    public void Malformed_problems_are_refused()
    {
        var e = new List<(int, int, double)> { (0, 0, 1.0) };
        Assert.Throws<ArgumentException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[2], new double[1], new double[1], new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, new List<(int, int, double)> { (1, 0, 1.0) }, new double[1], new double[1], new double[1], new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[1], new double[1], new double[] { 2 }, new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[] { double.NaN }, new double[1], new double[1], new double[] { 1 })));
        Assert.Throws<ArgumentException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[1], new double[1], new[] { Inf }, new[] { Inf })));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[1], new double[1], new double[1], new double[] { 1 }), tolerance: 0));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 5.0)]
    public void An_infeasible_lp_whose_bound_slack_reaches_zero_reports_a_status_instead_of_throwing(double lo, double hi)
    {
        // 2 x = 1 with x in [lo, hi] excluding 0.5 (the two children of a branch on x): a bound slack reached 0 before the
        // iterates passed the divergence limit, so the Newton matrix held an infinite entry and the sparse LU threw
        // ("the matrix contains an infinite entry"). It now stops with a status that is not Optimal.
        var e = new List<(int, int, double)> { (0, 0, 2.0) };
        SparseLpResult r = SparseInteriorPointLp.Solve(new SparseLpProblem(1, 1, e, new double[] { 1 }, new double[] { 1 }, new[] { lo }, new[] { hi }));
        Assert.False(r.Optimal, r.Message);
    }
}
