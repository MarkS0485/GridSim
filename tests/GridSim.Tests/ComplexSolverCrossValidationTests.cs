using System.Numerics;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The three complex linear solvers (dense LU, real 2n-expansion over SparseLu, native Jacobi complex
/// BiCGSTAB) all solve the same A x = b and MUST agree - the independent-implementation cross-check the
/// harmonic engine's Y(h) V = I solve relies on. Built on a diagonally-dominant nonsymmetric complex system
/// (well-conditioned so BiCGSTAB converges; the direct solvers are exact).
/// </summary>
public class ComplexSolverCrossValidationTests
{
    private static ComplexCsr FromDense(Complex[,] m, int n)
    {
        var b = new ComplexCsr.Builder(n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (m[i, j] != Complex.Zero) b.Add(i, j, m[i, j].Real, m[i, j].Imaginary);
        return b.Build();
    }

    private static Complex[] Multiply(Complex[,] m, Complex[] x, int n)
    {
        var y = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex s = Complex.Zero;
            for (int j = 0; j < n; j++) s += m[i, j] * x[j];
            y[i] = s;
        }
        return y;
    }

    private static (Complex[,] m, Complex[] b, int n) System()
    {
        const int n = 12;
        var m = new Complex[n, n];
        var b = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            m[i, i] = new Complex(10.0 + i % 4, 4.0);              // strong, non-uniform diagonal
            if (i > 0) m[i, i - 1] = new Complex(-1.0, -0.3);       // sub-diagonal
            if (i < n - 1) m[i, i + 1] = new Complex(0.5, -0.2);    // super-diagonal (asymmetric -> nonsymmetric)
            if (i >= 3) m[i, i - 3] = new Complex(0.2, 0.1);        // a longer-range coupling
            b[i] = new Complex(i + 1, n - i);
        }
        return (m, b, n);
    }

    [Fact]
    public void All_three_complex_solvers_agree_and_satisfy_Ax_equals_b()
    {
        (Complex[,] m, Complex[] b, int n) = System();
        ComplexCsr a = FromDense(m, n);

        Complex[]? dense = new DenseComplexLuSolver().Solve(a, b);
        Complex[]? real = new RealExpandedComplexSolver().Solve(a, b);
        Complex[]? bicg = new SparseComplexBiCgStabSolver().Solve(a, b);

        Assert.NotNull(dense);
        Assert.NotNull(real);
        Assert.NotNull(bicg);

        // The two direct solvers agree to (near) machine precision; the iterative one to its tolerance.
        for (int i = 0; i < n; i++)
        {
            Assert.True((dense![i] - real![i]).Magnitude < 1e-9, $"dense vs real-expanded row {i}");
            Assert.True((dense[i] - bicg![i]).Magnitude < 1e-8, $"dense vs bicgstab row {i}");
        }

        // And each is an actual solution: A x = b.
        foreach (Complex[]? x in new[] { dense, real, bicg })
        {
            Complex[] ax = Multiply(m, x!, n);
            for (int i = 0; i < n; i++)
                Assert.True((ax[i] - b[i]).Magnitude < 1e-8, $"residual row {i}");
        }
    }

    [Fact]
    public void Dense_solver_lifted_from_the_fault_study_solves_a_2x2()
    {
        // A trivial hand-checkable system: [[2,0],[0,1j]] x = [4, 2j]  ->  x = [2, 2].
        var m = new Complex[2, 2] { { new Complex(2, 0), Complex.Zero }, { Complex.Zero, new Complex(0, 1) } };
        var b = new[] { new Complex(4, 0), new Complex(0, 2) };
        Complex[] x = DenseComplexLuSolver.SolveDense(m, b, 2);
        Assert.True((x[0] - new Complex(2, 0)).Magnitude < 1e-12);
        Assert.True((x[1] - new Complex(2, 0)).Magnitude < 1e-12);
    }
}
