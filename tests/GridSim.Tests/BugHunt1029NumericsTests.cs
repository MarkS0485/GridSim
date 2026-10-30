using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), numerics findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH01 / BH02: the sparse simplex never returns an Optimal point that breaks its own constraints.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH01_a_slightly_infeasible_lp_is_not_returned_as_optimal()
    {
        // x0 = 1000 (row 0), x1 = 0 (row 1), x1 = 5e-6 (row 2): rows 1 and 2 contradict each other by 5e-6. The phase-one
        // threshold is 1000 x 1e-9 x (1 + max |b|) = about 1e-3, so the 5e-6 least infeasibility passes phase one, and the
        // point phase two returns breaks row 1 or row 2 by 5e-6, five times its 1e-6 post-solve allowance. It used to
        // come back Optimal with that broken row.
        var a = new List<(int, int, double)> { (0, 0, 1.0), (1, 1, 1.0), (2, 1, 1.0) };
        var lp = new SparseLpProblem(3, 2, a, new[] { 1000.0, 0.0, 5e-6 }, new[] { 0.0, 0.0 },
            new[] { 0.0, 0.0 }, new[] { 2000.0, 1.0 });
        SparseSimplexResult r = SparseBoundedSimplex.Solve(lp);
        Assert.NotEqual(SparseSimplexStatus.Optimal, r.Status);
        Assert.Equal(SparseSimplexStatus.NumericalFailure, r.Status);
        Assert.Contains("breaks row", r.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH03: a node whole within tolerance whose rounding is infeasible is branched on, not a reason to abort.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH03_a_nearly_whole_node_whose_rounding_is_infeasible_is_branched_not_aborted()
    {
        // min x, x integer in [0, 5], y continuous in [0, 2000], 1000 x - y = 0.0009. The relaxation's optimum is
        // x = 9e-7, y = 0: whole within the 1e-6 integrality tolerance, so it was "polished" by fixing x = 0, which needs
        // y = -0.0009, outside its bounds (infeasible by far more than phase one tolerates). That aborted the whole
        // search with LpFailure. The true optimum is x = 1, y = 999.9991.
        var a = new List<(int, int, double)> { (0, 0, 1000.0), (0, 1, -1.0) };
        var lp = new SparseLpProblem(1, 2, a, new[] { 0.0009 }, new[] { 1.0, 0.0 }, new[] { 0.0, 0.0 }, new[] { 5.0, 2000.0 });
        MilpResult r = MilpBranchAndBound.Solve(new MilpProblem(lp, new[] { true, false }));
        Assert.Equal(MilpStatus.Optimal, r.Status);
        Assert.NotNull(r.X);
        Assert.Equal(1.0, r.X![0]);
        Assert.Equal(1.0, r.Objective);
    }

    [Fact]
    public void BH01_a_feasible_lp_is_still_optimal_and_unchanged()
    {
        // min -x0 - x1, x0 + x1 + s = 4, x0 in [0, 3], x1 in [0, 3], s >= 0: optimum 4 on the face x0 + x1 = 4.
        var a = new List<(int, int, double)> { (0, 0, 1.0), (0, 1, 1.0), (0, 2, 1.0) };
        var lp = new SparseLpProblem(1, 3, a, new[] { 4.0 }, new[] { -1.0, -1.0, 0.0 },
            new[] { 0.0, 0.0, 0.0 }, new[] { 3.0, 3.0, double.PositiveInfinity });
        SparseSimplexResult r = SparseBoundedSimplex.Solve(lp);
        Assert.Equal(SparseSimplexStatus.Optimal, r.Status);
        Assert.Equal(-4.0, r.Objective);
    }
}
