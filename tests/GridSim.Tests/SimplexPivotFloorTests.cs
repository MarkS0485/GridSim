using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N112 (October 2026): BoundedSimplex and the structured DcOpfSecuritySimplex refuse a pivot below the eta floor
/// (1e-12) before the basis changes, instead of accepting it and refactorising the near-singular basis. The ratio test
/// admits only rows with |alpha| above the tolerance, so with the default tolerance (1e-9, every GridSim caller) the
/// refusal cannot trip and every pivot path is exactly the old one: the removed refactorise branch was unreachable. The
/// bundled LPs' pinned results (DcOpfSecuritySimplexParityTests, GeneratorCostsAndPricesTests, ...) are the bit check.
/// </summary>
public class SimplexPivotFloorTests
{
    // minimise -x1  s.t.  1e-13 x1 + x2 = 1,  x1, x2 >= 0. Phase I brings x2 in (pivot 1). Phase II: x1 enters with
    // alpha = 1e-13, the only row limiting it; the true optimum is x1 = 1e13.
    private static readonly double[,] A = { { 1e-13, 1.0 } };
    private static readonly double[] B = { 1.0 };
    private static readonly double[] C = { -1.0, 0.0 };
    private static readonly double[] Lo = { 0.0, 0.0 };
    private static readonly double[] Hi = { double.PositiveInfinity, double.PositiveInfinity };

    [Fact]
    public void A_tolerance_below_the_floor_that_chooses_a_tiny_pivot_is_refused_by_name()
    {
        // tol 1e-15 admits the 1e-13 row; the old code accepted the pivot and refactorised. Now it is refused.
        var ex = Assert.Throws<InvalidOperationException>(() => BoundedSimplex.Solve(A, B, C, Lo, Hi, tol: 1e-15));
        Assert.Contains("BoundedSimplex: the ratio test chose a pivot of magnitude 1.000E-013, below the eta floor 1E-012", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_tolerance_never_chooses_a_pivot_below_the_floor()
    {
        // With tol 1e-9 the 1e-13 row is not a ratio-test candidate at all, so x1 is unbounded in the solver's metric.
        // This is the old behaviour, unchanged: the floor plays no part at the default tolerance.
        (LpStatus status, double[] _, double _) = BoundedSimplex.Solve(A, B, C, Lo, Hi);
        Assert.Equal(LpStatus.Unbounded, status);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_tolerance_that_is_not_a_positive_finite_number_is_refused(double tol)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedSimplex.Solve(A, B, C, Lo, Hi, tol: tol));
    }

    [Theory]
    [InlineData(1e-12, false)]
    [InlineData(-1e-12, false)]
    [InlineData(1.0, false)]
    [InlineData(9.99e-13, true)]
    [InlineData(-1e-13, true)]
    [InlineData(0.0, true)]
    public void The_shared_guard_refuses_exactly_the_pivots_below_the_floor(double pivot, bool refused)
    {
        if (refused)
            Assert.Throws<InvalidOperationException>(() => BoundedSimplex.RequirePivotAboveFloor(pivot, 1e-15, "test"));
        else
            BoundedSimplex.RequirePivotAboveFloor(pivot, 1e-15, "test");
    }

    [Fact]
    public void Both_solvers_check_the_pivot_before_the_step_and_no_longer_refactorise_around_it()
    {
        foreach (string rel in new[] { "src/GridSim.Core/Numerics/BoundedSimplex.cs", "src/GridSim.Core/Analysis/DcOpfSecuritySimplex.cs" })
        {
            string src = File.ReadAllText(Path.Combine(CliProcess.Root(), rel));
            int guard = src.IndexOf("RequirePivotAboveFloor(alpha[leave], tol,", StringComparison.Ordinal);
            Assert.True(guard >= 0, $"{rel}: the pivot guard is missing");
            // The guard precedes the step that moves the basic values.
            int step = src.IndexOf("-= qDir * tMax * alpha[i];", StringComparison.Ordinal);
            Assert.True(step > guard, $"{rel}: the guard must come before the step");
            Assert.DoesNotContain("EtaPivotFloor) { Refactorize", src, StringComparison.Ordinal);
        }
    }
}
