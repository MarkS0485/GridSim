using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class NumericsTests
{
    [Fact]
    public void DenseLu_solves_a_known_system()
    {
        // 2x + y = 5 ; x + 3y = 10  ->  x = 1, y = 3
        var a = new double[,] { { 2, 1 }, { 1, 3 } };
        var b = new double[] { 5, 10 };
        double[] x = new DenseLuSolver().Solve(a, b);
        Assert.Equal(1.0, x[0], 9);
        Assert.Equal(3.0, x[1], 9);
    }

    [Fact]
    public void DenseLu_needs_pivoting()
    {
        // Zero pivot in position [0,0] forces a row swap.
        var a = new double[,] { { 0, 2 }, { 1, 1 } };
        var b = new double[] { 4, 3 };           // 2y = 4 -> y = 2 ; x + y = 3 -> x = 1
        double[] x = new DenseLuSolver().Solve(a, b);
        Assert.Equal(1.0, x[0], 9);
        Assert.Equal(2.0, x[1], 9);
    }

    [Fact]
    public void DenseLu_throws_on_singular_matrix()
    {
        var a = new double[,] { { 1, 2 }, { 2, 4 } };
        var b = new double[] { 1, 2 };
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().Solve(a, b));
    }

    [Fact]
    public void DenseLu_throws_on_a_near_singular_pivot_not_just_an_exact_zero()
    {
        // Diagonal but ill-conditioned to ~1e15 (cond*eps ~ 1 - no reliable digits left). The pivot
        // 1e-9 is a genuine NON-zero, so the old exact-zero guard would have solved it and returned a
        // number precision can't support; the relative floor (1e6*1e-12 = 1e-6) rejects it instead.
        var a = new double[,] { { 1e6, 0.0 }, { 0.0, 1e-9 } };
        var b = new double[] { 1.0, 1.0 };
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().Solve(a, b));
    }

    [Fact]
    public void YBus_is_symmetric_for_a_plain_line()
    {
        // Two buses joined by one line: off-diagonals equal -series admittance,
        // diagonals equal series + half-charging.
        YBus y = YBus.Build(IeeeCases.Case9());
        Assert.Equal(9, y.N);
        for (int i = 0; i < y.N; i++)
            for (int j = 0; j < y.N; j++)
                Assert.True((y.Y[i, j] - y.Y[j, i]).Magnitude < 1e-12); // symmetric (no phase shifters)
    }

    [Fact]
    public void YBus_branch_stamp_matches_hand_calc()
    {
        // Branch 1-4 of case9: r=0, x=0.0576, b=0  ->  y = 1/(j0.0576) = -j17.3611
        YBus y = YBus.Build(IeeeCases.Case9());
        int f = y.IndexOf(1), t = y.IndexOf(4);
        Complex expectedOff = -(Complex.One / new Complex(0, 0.0576));
        Assert.True((y.Y[f, t] - expectedOff).Magnitude < 1e-9);
    }
}
