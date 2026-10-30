using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the complex-solver seam contract fix: singular systems must return
/// <c>null</c> from ALL THREE <see cref="IComplexLinearSolver"/> implementations, so a near-singular system
/// can never "validate" against a fabricated answer on the cross-validation seam. The dense solver
/// previously clamped tiny pivots and never failed; its interface path now reports singular honestly, while
/// the fault-study entry point (<see cref="DenseComplexLuSolver.SolveDense"/>) keeps its historical clamping.
/// </summary>
public class ComplexSolverSingularContractTests
{
    // A structurally singular complex system: row 2 is an exact duplicate of row 1 (rank-deficient), with
    // an INCONSISTENT right-hand side (rhs[2] != rhs[1]) so no solution exists at all. A consistent
    // singular RHS would let iterative BiCGSTAB legitimately land on one of the infinitely many solutions;
    // inconsistency forces a genuine failure from all three paths.
    private static (ComplexCsr A, Complex[] B) SingularSystem()
    {
        const int n = 4;
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = new Complex((i + 1) * (j + 2) * 0.5, (i - j) * 0.25);
        for (int j = 0; j < n; j++) m[2, j] = m[1, j]; // duplicate row -> singular

        var b = new ComplexCsr.Builder(n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (m[i, j] != Complex.Zero) b.Add(i, j, m[i, j].Real, m[i, j].Imaginary);
        var rhs = new Complex[n];
        for (int i = 0; i < n; i++) rhs[i] = new Complex(1.0 + i, 0.5); // rhs[2] != rhs[1]: inconsistent
        return (b.Build(), rhs);
    }

    [Fact]
    public void All_three_solvers_return_null_on_a_singular_system()
    {
        (ComplexCsr a, Complex[] b) = SingularSystem();

        Assert.Null(new DenseComplexLuSolver().Solve(a, b));
        Assert.Null(new RealExpandedComplexSolver().Solve(a, b));
        Assert.Null(new SparseComplexBiCgStabSolver().Solve(a, b));
    }

    [Fact]
    public void SolveDense_keeps_its_historical_clamping_and_never_returns_null()
    {
        // The fault-study entry point must remain bit-compatible: it clamps rather than failing, so it
        // always produces an array (the fault regressions depend on this behaviour, not on the value here).
        (ComplexCsr a, Complex[] b) = SingularSystem();
        int n = a.N;
        var dense = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                dense[i, a.ColIdx[k]] = new Complex(a.G[k], a.B[k]);

        Complex[] x = DenseComplexLuSolver.SolveDense(dense, b, n);
        Assert.Equal(n, x.Length); // returns a result, does not throw or null
    }

    [Fact]
    public void A_well_conditioned_system_still_solves_on_the_seam()
    {
        // Guard against over-eager null: a well-conditioned system must still solve on the interface path.
        const int n = 5;
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
        {
            m[i, i] = new Complex(8.0 + i, 3.0);
            if (i > 0) m[i, i - 1] = new Complex(-1.0, -0.2);
            if (i < n - 1) m[i, i + 1] = new Complex(0.4, -0.1);
        }
        var xTrue = new Complex[n];
        for (int i = 0; i < n; i++) xTrue[i] = new Complex(1.0 - 0.1 * i, 0.2 * i);
        var rhs = new Complex[n];
        for (int i = 0; i < n; i++) { Complex s = Complex.Zero; for (int j = 0; j < n; j++) s += m[i, j] * xTrue[j]; rhs[i] = s; }

        var b = new ComplexCsr.Builder(n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (m[i, j] != Complex.Zero) b.Add(i, j, m[i, j].Real, m[i, j].Imaginary);
        ComplexCsr a = b.Build();

        Complex[]? x = new DenseComplexLuSolver().Solve(a, rhs);
        Assert.NotNull(x);
        for (int i = 0; i < n; i++) Assert.True((x![i] - xTrue[i]).Magnitude < 1e-9);
    }

    [Fact] // GR-467: a non-finite right-hand side is a failed solve (null / false), never a NaN "solution"
    public void Real_expanded_solver_refuses_a_non_finite_right_hand_side()
    {
        var b = new ComplexCsr.Builder(2);
        b.Add(0, 0, 2.0, 0.5);
        b.Add(0, 1, 0.1, 0.0);
        b.Add(1, 0, 0.1, 0.0);
        b.Add(1, 1, 3.0, -0.25);
        ComplexCsr a = b.Build();
        var solver = new RealExpandedComplexSolver();

        // The finite system still solves.
        Assert.NotNull(solver.Solve(a, new[] { new Complex(1.0, 0.0), new Complex(0.0, 1.0) }));

        var nanRhs = new[] { new Complex(double.NaN, 0.0), new Complex(0.0, 1.0) };
        var infRhs = new[] { new Complex(1.0, 0.0), new Complex(0.0, double.PositiveInfinity) };
        Assert.Null(solver.Solve(a, nanRhs));
        Assert.Null(solver.Solve(a, infRhs));

        IComplexLinearFactorization? lu = solver.Factor(a);
        Assert.NotNull(lu);
        Assert.Null(lu!.Solve(nanRhs));
        Assert.False(lu.SolveInto(infRhs, new Complex[2]));
    }

    [Fact] // GR-478: the clamping dense LU must refuse an infinite entry, not return the zero vector
    public void Clamping_dense_complex_lu_refuses_an_infinite_matrix_entry()
    {
        var m = new Complex[2, 2];
        m[0, 0] = new Complex(double.PositiveInfinity, 0.0);
        m[0, 1] = new Complex(1.0, 0.0);
        m[1, 0] = new Complex(1.0, 0.0);
        m[1, 1] = new Complex(2.0, 0.0);
        var rhs = new[] { Complex.One, Complex.One };

        Assert.Throws<ArgumentException>(() => DenseComplexLuSolver.SolveDense(m, rhs, 2));
        Assert.Throws<ArgumentException>(() => DenseComplexLuSolver.FactorDense(m, 2));
    }
}
