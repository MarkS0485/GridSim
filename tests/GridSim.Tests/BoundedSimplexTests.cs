using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The self-contained bounded-variable simplex (<see cref="BoundedSimplex"/>): correctness on textbook
/// LPs with known optima, native variable bounds (bound flips), and the infeasible/unbounded verdicts.
/// </summary>
public class BoundedSimplexTests
{
    [Fact]
    public void Solves_a_textbook_LP_to_its_known_optimum()
    {
        // max 3x + 2y  s.t.  x + y <= 4,  x + 3y <= 6,  x,y >= 0.  The vertex (4,0) gives 12, beating the
        // constraint intersection (3,1)=11, so the optimum is (x,y)=(4,0), value 12.
        // As a minimisation of -3x-2y with slacks s1,s2 >= 0 (unbounded above):  x+y+s1=4, x+3y+s2=6.
        var A = new double[,] { { 1, 1, 1, 0 }, { 1, 3, 0, 1 } };
        var b = new[] { 4.0, 6.0 };
        var c = new[] { -3.0, -2.0, 0.0, 0.0 };
        var lo = new[] { 0.0, 0.0, 0.0, 0.0 };
        var hi = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };

        (LpStatus status, double[] x, double obj) = BoundedSimplex.Solve(A, b, c, lo, hi);

        Assert.Equal(LpStatus.Optimal, status);
        Assert.Equal(4.0, x[0], 6);
        Assert.Equal(0.0, x[1], 6);
        Assert.Equal(-12.0, obj, 6);
    }

    [Fact]
    public void Respects_finite_upper_bounds_via_a_merit_order()
    {
        // min x0 + 2 x1 + 3 x2  s.t.  x0+x1+x2 = 1,  each in [0, 0.6].  Cheapest-first: x0=0.6, x1=0.4.
        var A = new double[,] { { 1, 1, 1 } };
        var b = new[] { 1.0 };
        var c = new[] { 1.0, 2.0, 3.0 };
        var lo = new[] { 0.0, 0.0, 0.0 };
        var hi = new[] { 0.6, 0.6, 0.6 };

        (LpStatus status, double[] x, double obj) = BoundedSimplex.Solve(A, b, c, lo, hi);

        Assert.Equal(LpStatus.Optimal, status);
        Assert.Equal(0.6, x[0], 6);
        Assert.Equal(0.4, x[1], 6);
        Assert.Equal(0.0, x[2], 6);
        Assert.Equal(0.6 + 0.8, obj, 6);
    }

    [Fact]
    public void Honours_nonzero_lower_bounds()
    {
        // min x0 + x1  s.t.  x0 + x1 = 2,  x0 in [0.5,2], x1 in [0.5,2]. Objective is 2 for any split;
        // the point must at least respect both floors and the balance.
        var A = new double[,] { { 1, 1 } };
        var b = new[] { 2.0 };
        var c = new[] { 1.0, 1.0 };
        var lo = new[] { 0.5, 0.5 };
        var hi = new[] { 2.0, 2.0 };

        (LpStatus status, double[] x, double obj) = BoundedSimplex.Solve(A, b, c, lo, hi);

        Assert.Equal(LpStatus.Optimal, status);
        Assert.True(x[0] >= 0.5 - 1e-9 && x[1] >= 0.5 - 1e-9);
        Assert.Equal(2.0, x[0] + x[1], 6);
        Assert.Equal(2.0, obj, 6);
    }

    [Fact]
    public void Reports_infeasible_when_bounds_cannot_meet_the_equality()
    {
        // x0 + x1 = 5 but each <= 2, so the most they sum to is 4 -> infeasible.
        var A = new double[,] { { 1, 1 } };
        var b = new[] { 5.0 };
        var c = new[] { 1.0, 1.0 };
        var lo = new[] { 0.0, 0.0 };
        var hi = new[] { 2.0, 2.0 };

        (LpStatus status, double[] _, double _) = BoundedSimplex.Solve(A, b, c, lo, hi);

        Assert.Equal(LpStatus.Infeasible, status);
    }

    [Fact]
    public void Reports_unbounded_when_the_objective_falls_without_limit()
    {
        // min -x0  s.t.  x0 - x1 = 0, both >= 0 and unbounded above: x0 can grow forever.
        var A = new double[,] { { 1, -1 } };
        var b = new[] { 0.0 };
        var c = new[] { -1.0, 0.0 };
        var lo = new[] { 0.0, 0.0 };
        var hi = new[] { double.PositiveInfinity, double.PositiveInfinity };

        (LpStatus status, double[] _, double _) = BoundedSimplex.Solve(A, b, c, lo, hi);

        Assert.Equal(LpStatus.Unbounded, status);
    }
}
