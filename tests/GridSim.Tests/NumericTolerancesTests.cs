using System.Numerics;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins workstream WS5, "one tolerance table" (R034, R049, R141-R144, R181, R183, R184, R280, R301), plus the
/// complex BiCGSTAB inner-product fix (R278). Every solver now decides "is this pivot numerically zero?" with
/// the ONE relative floor in <see cref="NumericTolerances"/>, so a matrix singular to working precision is
/// rejected whatever its scale, and a well-conditioned matrix is accepted whatever its scale. Each test below
/// would have failed against the pre-October-2026 code (absolute 1e-300 / 1e-14 / 1e-12 floors, exact-zero
/// tests, bilinear complex inner products).
/// </summary>
public class NumericTolerancesTests
{
    // ---------------------------------------------------------------- the table itself

    [Fact]
    public void The_shared_pivot_floor_is_relative_1e_minus_12()
    {
        Assert.Equal(1e-12, NumericTolerances.RelativePivotFloor);
        Assert.Equal(1e-12 * 7.5, NumericTolerances.PivotFloor(7.5));
        Assert.Equal(0.0, NumericTolerances.PivotFloor(0.0)); // all-zero matrix: <= 0 still catches the zero pivot
    }

    [Fact]
    public void A_negative_or_NaN_matrix_scale_fails_loud()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.PivotFloor(-1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.PivotFloor(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.Ilu0PivotGuard(-1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.DefaultKrylovMaxIterations(-1));
    }

    [Fact]
    public void MaxAbs_returns_NaN_for_a_corrupted_matrix()
    {
        Assert.Equal(3.0, NumericTolerances.MaxAbs(new double[,] { { 1, -3 }, { 2, 0 } }));
        Assert.True(double.IsNaN(NumericTolerances.MaxAbs(new double[,] { { 1, double.NaN }, { 2, 0 } })));
        Assert.True(double.IsNaN(NumericTolerances.MaxAbs(new[] { 1.0, double.NaN })));
    }

    [Fact]
    public void Krylov_defaults_are_the_named_values()
    {
        Assert.Equal(1e-10, NumericTolerances.DefaultKrylovTolerance);
        Assert.Equal(1e-12, NumericTolerances.DefaultComplexKrylovTolerance);
        Assert.Equal(4 * 10 + 200, NumericTolerances.DefaultKrylovMaxIterations(10));
    }

    [Fact]
    public void FirstSingularPivot_finds_the_collapsed_diagonal_and_rejects_bad_input()
    {
        // Column-major 3x3 with U(1,1) at 1e-13 of the scale 2.0 (below the 2e-12 floor).
        double[] lu = { 2.0, 0, 0, 0, 2e-13, 0, 0, 0, 1.0 };
        Assert.Equal(1, NumericTolerances.FirstSingularPivot(lu, 3, 2.0));
        lu[4] = 1e-3;
        Assert.Equal(-1, NumericTolerances.FirstSingularPivot(lu, 3, 2.0));
        lu[8] = double.NaN;
        Assert.Equal(2, NumericTolerances.FirstSingularPivot(lu, 3, 2.0)); // a NaN pivot is never valid
        Assert.Throws<ArgumentException>(() => NumericTolerances.FirstSingularPivot(new double[3], 2, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericTolerances.FirstSingularPivot(lu, -1, 1.0));
    }

    // ---------------------------------------------------------------- sparse LU (R034 / R280)

    private static CsrMatrix Csr(double[,] a)
    {
        int n = a.GetLength(0);
        var b = new CsrMatrix.Builder(n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (a[i, j] != 0.0) b.Add(i, j, a[i, j]);
        return b.Build();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1e8)]      // a gain matrix scale: the old absolute 1e-300 never tripped here
    [InlineData(1e-200)]
    public void Sparse_LU_returns_null_for_a_matrix_singular_to_working_precision_at_any_scale(double scale)
    {
        // Second row = first row plus a rounding-level perturbation: the second pivot is ~1e-15 of the scale.
        // Before R034 the sparse LU accepted any pivot above an absolute 1e-300 and returned huge garbage.
        var a = new double[,] { { scale, scale }, { scale, scale * (1 + 4.440892098500626e-16) } };
        Assert.Null(SparseLu.Solve(Csr(a), new[] { 1.0, 2.0 }));
    }

    [Fact]
    public void Sparse_LU_solves_a_well_conditioned_matrix_at_a_tiny_scale()
    {
        // Entries of 1e-301: well-conditioned, but every pivot is below the OLD absolute 1e-300 floor, so the
        // old code declared it singular. The relative floor accepts it and the answer is exact.
        var a = new double[,] { { 2e-301, 0 }, { 0, 4e-301 } };
        double[]? x = SparseLu.Solve(Csr(a), new[] { 2e-301, 4e-301 });
        Assert.NotNull(x);
        Assert.Equal(1.0, x![0]);
        Assert.Equal(1.0, x[1]);
    }

    [Fact]
    public void Sparse_LU_fails_loud_on_a_NaN_entry()
    {
        var a = new double[,] { { 1.0, 0 }, { 0, double.NaN } };
        Assert.Throws<ArgumentException>(() => SparseLu.Solve(Csr(a), new[] { 1.0, 1.0 }));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Sparse_LU_fails_loud_on_an_infinite_entry_rather_than_calling_it_singular(double inf)
    {
        // GR-479: the infinite pivot floor used to make the first column "singular" (null).
        var a = new double[,] { { 1.0, 0 }, { 0, inf } };
        ArgumentException ex = Assert.Throws<ArgumentException>(() => SparseLu.Solve(Csr(a), new[] { 1.0, 1.0 }));
        Assert.Contains("infinite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sparse_and_dense_LU_reject_the_same_near_singular_matrix()
    {
        // The two direct real solvers now share the floor: a 3x3 with a rank-deficient third row (to rounding).
        var a = new double[,] { { 4, 1, 2 }, { 1, 3, 0.5 }, { 5, 4, 2.5 * (1 + 2.220446049250313e-16) } };
        Assert.Null(SparseLu.Solve(Csr(a), new[] { 1.0, 1.0, 1.0 }));
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace((double[,])a.Clone(), new[] { 1.0, 1.0, 1.0 }));
    }

    // ---------------------------------------------------------------- Householder QR (R280)

    [Fact]
    public void Householder_QR_rejects_a_column_dependent_to_rounding()
    {
        // Third column = first + second to the last bit: rank 2 in exact arithmetic, and R(2,2) comes out at
        // rounding level, not exactly zero, so the old "== 0" test let it through.
        var a = new double[4, 3];
        for (int i = 0; i < 4; i++)
        {
            a[i, 0] = 1.0 + 0.3 * i;
            a[i, 1] = 0.7 - 0.2 * i * i;
            a[i, 2] = a[i, 0] + a[i, 1];
        }
        var ex = Assert.Throws<InvalidOperationException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, 2.0, 3.0, 4.0 }));
        Assert.Contains("rank-deficient", ex.Message);
    }

    [Fact]
    public void Householder_QR_still_solves_a_full_rank_system_at_a_tiny_scale()
    {
        var a = new double[,] { { 1e-200, 0 }, { 0, 2e-200 }, { 1e-200, 1e-200 } };
        double[] x = HouseholderQr.SolveLeastSquares(a, new[] { 1e-200, 2e-200, 2e-200 });
        Assert.Equal(1.0, x[0], 12);
        Assert.Equal(1.0, x[1], 12);
    }

    [Fact]
    public void Householder_QR_fails_loud_on_NaN()
    {
        var a = new double[,] { { 1.0, 0 }, { 0, double.NaN }, { 1, 1 } };
        Assert.Throws<ArgumentException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, 1.0, 1.0 }));
    }

    [Fact]
    public void Householder_QR_fails_loud_on_a_non_finite_or_short_right_hand_side()
    {
        // GR-460: a NaN or infinite b used to return a NaN solution with no error; a short b threw an index error.
        var a = new double[,] { { 1.0, 0 }, { 0, 1.0 }, { 1, 1 } };
        ArgumentException nan = Assert.Throws<ArgumentException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, double.NaN, 1.0 }));
        Assert.Contains("not finite", nan.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, double.NegativeInfinity, 1.0 }));
        Assert.Throws<ArgumentException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, 1.0 }));
    }

    [Fact]
    public void Householder_QR_fails_loud_on_an_infinite_entry_rather_than_calling_it_rank_deficient()
    {
        // GR-479: it used to throw the "rank-deficient" InvalidOperationException.
        var a = new double[,] { { 1.0, 0 }, { 0, double.PositiveInfinity }, { 1, 1 } };
        ArgumentException ex = Assert.Throws<ArgumentException>(() => HouseholderQr.SolveLeastSquares(a, new[] { 1.0, 1.0, 1.0 }));
        Assert.Contains("infinite", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- complex dense LU (R144 / R181)

    [Fact]
    public void Complex_SolveDense_no_longer_zeroes_a_well_conditioned_small_scale_system()
    {
        // diag(1e-20, 2e-20) x = (1e-20, 2e-20): x = (1, 1). The old ABSOLUTE 1e-14 clamp treated both pivots as
        // singular and silently returned x = (0, 0). The relative floor solves it exactly.
        var m = new Complex[,] { { new(1e-20, 0), Complex.Zero }, { Complex.Zero, new(0, 2e-20) } };
        var b = new[] { new Complex(1e-20, 0), new Complex(0, 2e-20) };
        Complex[] x = DenseComplexLuSolver.SolveDense(m, b, 2);
        Assert.Equal(Complex.One, x[0]);
        Assert.Equal(Complex.One, x[1]);

        // The factor-once handle agrees bit-for-bit with the one-shot solve.
        Complex[] xf = DenseComplexLuSolver.FactorDense(m, 2).Solve(b);
        Assert.Equal(x[0], xf[0]);
        Assert.Equal(x[1], xf[1]);
    }

    [Fact]
    public void Complex_seam_solver_returns_null_on_a_near_singular_system_at_a_large_scale()
    {
        // Admittance-like scale 1e6 with a rank-deficient (to rounding) second row.
        const double s = 1e6;
        var bld = new ComplexCsr.Builder(2);
        bld.Add(0, 0, s, s); bld.Add(0, 1, -s, 0.5 * s);
        bld.Add(1, 0, s, s); bld.Add(1, 1, -s * (1 + 4.440892098500626e-16), 0.5 * s);
        ComplexCsr a = bld.Build();
        var rhs = new[] { Complex.One, new Complex(2, 0) };
        Assert.Null(new DenseComplexLuSolver().Solve(a, rhs));
        Assert.Null(new RealExpandedComplexSolver().Solve(a, rhs));
    }

    [Fact]
    public void Complex_dense_LU_fails_loud_on_NaN()
    {
        var m = new Complex[,] { { new(double.NaN, 0), Complex.Zero }, { Complex.Zero, Complex.One } };
        Assert.Throws<ArgumentException>(() => DenseComplexLuSolver.SolveDense(m, new[] { Complex.One, Complex.One }, 2));
        Assert.Throws<ArgumentException>(() => DenseComplexLuSolver.FactorDense(m, 2));
    }

    [Fact]
    public void Complex_SolveDense_all_zero_matrix_stays_finite()
    {
        // The historical clamping path must never emit NaN, even for the all-zero matrix (floor 0).
        var m = new Complex[2, 2];
        Complex[] x = DenseComplexLuSolver.SolveDense(m, new[] { Complex.One, Complex.One }, 2);
        Assert.All(x, c => Assert.Equal(Complex.Zero, c));
    }

    // ---------------------------------------------------------------- complex BiCGSTAB (R278)

    [Fact]
    public void Complex_BiCgStab_solves_the_system_whose_bilinear_rho_is_zero()
    {
        // A = I, b = [1, i]. The old bilinear inner product gave rho0 = 1 + i^2 = 0 exactly and the solver
        // reported breakdown (null) on this perfectly valid system. With conjugated products it solves it.
        var bld = new ComplexCsr.Builder(2);
        bld.Add(0, 0, 1.0, 0.0);
        bld.Add(1, 1, 1.0, 0.0);
        var b = new[] { Complex.One, Complex.ImaginaryOne };
        Complex[]? x = new SparseComplexBiCgStabSolver().Solve(bld.Build(), b);
        Assert.NotNull(x);
        Assert.True((x![0] - Complex.One).Magnitude < 1e-12);
        Assert.True((x[1] - Complex.ImaginaryOne).Magnitude < 1e-12);
    }

    [Fact]
    public void Complex_BiCgStab_agrees_with_the_direct_solver_on_a_nonsymmetric_system()
    {
        const int n = 12;
        var bld = new ComplexCsr.Builder(n);
        var rhs = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            bld.Add(i, i, 6.0 + i % 3, 2.0 - 0.1 * i);
            if (i > 0) bld.Add(i, i - 1, -1.0, 0.4);
            if (i < n - 1) bld.Add(i, i + 1, 0.3, -0.7);
            rhs[i] = new Complex(i % 2 == 0 ? 1.0 : 0.0, i % 2 == 0 ? 0.0 : 1.0); // alternating 1, i entries
        }
        ComplexCsr a = bld.Build();
        Complex[]? direct = new DenseComplexLuSolver().Solve(a, rhs);
        Complex[]? iter = new SparseComplexBiCgStabSolver().Solve(a, rhs);
        Assert.NotNull(direct);
        Assert.NotNull(iter);
        for (int i = 0; i < n; i++) Assert.True((direct![i] - iter![i]).Magnitude < 1e-10, $"row {i}");
    }

    [Fact]
    public void Complex_BiCgStab_rejects_invalid_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseComplexBiCgStabSolver(tol: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseComplexBiCgStabSolver(tol: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseComplexBiCgStabSolver(maxIter: -1));
    }

    // ---------------------------------------------------------------- ILU0 / BiCGSTAB (R183)

    [Fact]
    public void Ilu0_guard_is_relative_so_a_small_scale_matrix_keeps_its_exact_factors()
    {
        // For a 2x2 matrix ILU(0) is the full LU, so Apply must solve A z = r exactly. At scale 1e-14 every
        // pivot sat below the OLD absolute 1e-12 guard and was overwritten, so Apply returned a wrong z (by a
        // factor of about 25 here). The relative guard leaves genuine pivots alone.
        const double s = 1e-14;
        CsrMatrix a = Csr(new double[,] { { 4 * s, 1 * s }, { 1 * s, 3 * s } });
        Ilu0 p = Ilu0.Factor(a);
        var r = new[] { 5 * s, 4 * s };    // A [1, 1] = [5s, 4s]
        var z = new double[2];
        p.Apply(r, z);
        Assert.True(Math.Abs(z[0] - 1.0) < 1e-12, $"z0 = {z[0]}");
        Assert.True(Math.Abs(z[1] - 1.0) < 1e-12, $"z1 = {z[1]}");
    }

    [Fact]
    public void Ilu0_guard_still_regularises_a_genuinely_zero_pivot()
    {
        // A structurally zero second pivot: the guard pushes it to 1e-12 x row scale, keeping Apply finite.
        CsrMatrix a = Csr(new double[,] { { 1, 1 }, { 1, 1 } });
        var z = new double[2];
        Ilu0.Factor(a).Apply(new[] { 1.0, 1.0 }, z);
        Assert.All(z, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void BiCgStab_default_cap_is_the_shared_one_and_settings_are_validated()
    {
        CsrMatrix a = Csr(new double[,] { { 4, 1 }, { 1, 3 } });
        var x = new double[2];
        Assert.True(BiCgStab.Solve(a, new[] { 5.0, 4.0 }, x, Ilu0.Factor(a)));
        Assert.Equal(1.0, x[0], 10);
        Assert.Equal(1.0, x[1], 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => BiCgStab.Solve(a, new[] { 1.0, 1.0 }, x, Ilu0.Factor(a), tol: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BiCgStab.Solve(a, new[] { 1.0, 1.0 }, x, Ilu0.Factor(a), maxIter: -5));
    }

    // ---------------------------------------------------------------- symmetric Jacobi (R184)

    [Fact]
    public void Jacobi_rotates_a_tiny_scale_matrix_instead_of_returning_its_diagonal()
    {
        // 1e-16 * [[2,1],[1,2]] has eigenvalues 1e-16 and 3e-16. The old absolute 1e-30 test saw
        // off = 1e-32 and stopped before any rotation, returning the diagonal (2e-16, 2e-16).
        var a = new double[,] { { 2e-16, 1e-16 }, { 1e-16, 2e-16 } };
        double[] e = SymmetricEigen.Eigenvalues(a);
        Assert.True(Math.Abs(e[0] - 1e-16) < 1e-30, $"e0 = {e[0]}");
        Assert.True(Math.Abs(e[1] - 3e-16) < 1e-30, $"e1 = {e[1]}");
    }

    [Fact]
    public void Jacobi_converges_on_a_large_scale_matrix_and_the_vectors_solve_the_eigen_equation()
    {
        const int n = 6;
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double v = 1e3 * (i == j ? 10.0 + i : 1.0 / (1 + i + j));
                a[i, j] = v; a[j, i] = v;
            }
        (double[] values, double[,] vectors) = SymmetricEigen.EigenDecomposition(a);
        for (int k = 0; k < n; k++)
            for (int i = 0; i < n; i++)
            {
                double av = 0.0;
                for (int j = 0; j < n; j++) av += a[i, j] * vectors[j, k];
                Assert.True(Math.Abs(av - values[k] * vectors[i, k]) < 1e-9, $"pair {k} row {i}");
            }
    }

    [Fact]
    public void Jacobi_fails_loud_on_a_non_finite_matrix()
    {
        Assert.Throws<ArgumentException>(() => SymmetricEigen.Eigenvalues(new double[,] { { 1, double.NaN }, { double.NaN, 1 } }));
        Assert.Throws<ArgumentException>(() => SymmetricEigen.Eigenvalues(new double[,] { { double.PositiveInfinity, 0 }, { 0, 1 } }));
    }

    // ---------------------------------------------------------------- general eigen (R033)

    private static void AssertValidDecomposition(double[,] a, IReadOnlyList<GeneralEigen.Eigenpair> pairs, double tol)
    {
        int n = a.GetLength(0);
        Assert.Equal(n, pairs.Count);
        for (int k = 0; k < n; k++)
        {
            GeneralEigen.Eigenpair p = pairs[k];
            for (int i = 0; i < n; i++)
            {
                Complex av = Complex.Zero;
                for (int j = 0; j < n; j++) av += a[i, j] * p.Right[j];
                Assert.True((av - p.Value * p.Right[i]).Magnitude < tol, $"A v = lambda v failed for pair {k} row {i}");
            }
            // Biorthonormality against every right vector: psi_k^H phi_j = delta_kj.
            for (int j = 0; j < n; j++)
            {
                Complex c = Complex.Zero;
                for (int i = 0; i < n; i++) c += Complex.Conjugate(p.Left[i]) * pairs[j].Right[i];
                Complex expected = j == k ? Complex.One : Complex.Zero;
                Assert.True((c - expected).Magnitude < 1e-9, $"psi_{k}^H phi_{j} = {c}");
            }
        }
    }

    [Fact]
    public void A_repeated_non_defective_eigenvalue_decomposes_instead_of_throwing()
    {
        // diag(-1, -1): diagonalisable, eigenspace all of C^2. Before R033 both inverse iterations returned the
        // same vector, V was rank 1 and Decompose threw DefectiveEigenproblemException.
        var a = new double[,] { { -1, 0 }, { 0, -1 } };
        AssertValidDecomposition(a, GeneralEigen.Decompose(a), 1e-8);
    }

    [Fact]
    public void Three_identical_units_give_a_full_eigenbasis()
    {
        // A non-normal matrix S diag(-2, -2, -2, -0.5) S^-1 (a triple repeated semisimple eigenvalue, the
        // "three identical co-located machines" shape), built in floating point.
        double[,] s = { { 1, 0.5, 0, 0.2 }, { 0, 1, 0.3, 0 }, { 0.4, 0, 1, 0.1 }, { 0, 0.2, 0, 1 } };
        double[] d = { -2, -2, -2, -0.5 };
        double[,] a = SimilarityTransform(s, d);
        AssertValidDecomposition(a, GeneralEigen.Decompose(a), 1e-7);
    }

    [Fact]
    public void A_repeated_complex_pair_decomposes()
    {
        // Two identical damped oscillators: eigenvalues -0.1 +/- 1j, each twice.
        var a = new double[,]
        {
            { -0.1, 1, 0, 0 }, { -1, -0.1, 0, 0 },
            { 0, 0, -0.1, 1 }, { 0, 0, -1, -0.1 },
        };
        AssertValidDecomposition(a, GeneralEigen.Decompose(a), 1e-8);
    }

    [Fact]
    public void A_defective_block_inside_a_larger_cluster_still_fails_loud()
    {
        // Eigenvalue 2 three times but only two independent eigenvectors (one 2x2 Jordan block): defective.
        var a = new double[,] { { 2, 1, 0 }, { 0, 2, 0 }, { 0, 0, 2 } };
        Assert.Throws<GeneralEigen.DefectiveEigenproblemException>(() => GeneralEigen.Decompose(a));
    }

    [Fact]
    public void Distinct_eigenvalues_are_unaffected_by_the_cluster_logic()
    {
        // No eigenvalue has a twin, so the plain inverse iteration result is returned untouched: the
        // decomposition is identical across repeated calls (deterministic) and valid.
        var a = new double[,] { { -1, 2, 0 }, { -3, -1, 0.5 }, { 0.2, 0, -4 } };
        IReadOnlyList<GeneralEigen.Eigenpair> p1 = GeneralEigen.Decompose(a);
        IReadOnlyList<GeneralEigen.Eigenpair> p2 = GeneralEigen.Decompose(a);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(p1[k].Right[i], p2[k].Right[i]);
                Assert.Equal(p1[k].Left[i], p2[k].Left[i]);
            }
        AssertValidDecomposition(a, p1, 1e-8);
    }

    private static double[,] SimilarityTransform(double[,] s, double[] d)
    {
        int n = d.Length;
        double[,] sInv = InvertByGaussJordan(s);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < n; k++) sum += s[i, k] * d[k] * sInv[k, j];
                a[i, j] = sum;
            }
        return a;
    }

    private static double[,] InvertByGaussJordan(double[,] src)
    {
        int n = src.GetLength(0);
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = src[i, j]; m[i, n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            for (int j = 0; j < 2 * n; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
            double dv = m[c, c];
            for (int j = 0; j < 2 * n; j++) m[c, j] /= dv;
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = m[r, c];
                for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[c, j];
            }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    // ---------------------------------------------------------------- estimator backends (R141 / R142 / R143)

    [Fact]
    public void DSE_inverse_uses_the_shared_floor()
    {
        // Second pivot ~1e-13 of the scale: accepted by the old local 1e-14 floor, rejected by the shared 1e-12.
        var a = new double[,] { { 1, 1 }, { 1, 1 + 1e-13 } };
        Assert.Throws<InvalidOperationException>(() => DseLinAlg.Invert(a, 2));
        // A well-conditioned matrix still inverts.
        double[,] inv = DseLinAlg.Invert(new double[,] { { 2, 0 }, { 0, 4 } }, 2);
        Assert.Equal(0.5, inv[0, 0]);
        Assert.Equal(0.25, inv[1, 1]);
    }

    [Fact]
    public void DSE_Cholesky_fails_loud_on_an_indefinite_covariance()
    {
        // [[1,2],[2,1]] is indefinite (eigenvalues 3 and -1). The old code replaced the negative pivot with an
        // absolute 1e-12 and returned a fabricated factor.
        var ex = Assert.Throws<InvalidOperationException>(() => DseLinAlg.Cholesky(new double[,] { { 1, 2 }, { 2, 1 } }, 2));
        Assert.Contains("not positive definite", ex.Message);
        // A genuine SPD matrix factors exactly.
        double[,] l = DseLinAlg.Cholesky(new double[,] { { 4, 2 }, { 2, 2 } }, 2);
        Assert.Equal(2.0, l[0, 0]);
        Assert.Equal(1.0, l[1, 0]);
        Assert.Equal(1.0, l[1, 1]);
    }

    [Fact]
    public void Critical_residual_floor_is_one_shared_value()
    {
        Assert.Equal(1e-8, NumericTolerances.CriticalResidualVarianceFraction);
        Assert.Equal(NumericTolerances.CriticalResidualVarianceFraction, BadDataAnalysis.CriticalFloor);
    }

    private static (GridModel Model, MeasurementSet Set) Case14WithVirtuals()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        var ms = set.Measurements
            .Where(m => !(m.Bus == 7 && m.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar))
            .ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 10.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 20.0, Bus: 7, Source: "virtual-zero-injection"));
        return (model, set with { Measurements = ms });
    }

    private static double[] Weights(MeasurementModel mm)
    {
        var w = new double[mm.MeasurementCount];
        for (int r = 0; r < w.Length; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        return w;
    }

    [Fact]
    public void Constrained_WLS_uses_one_constraint_weight_relative_to_the_strongest_free_row()
    {
        (GridModel model, MeasurementSet set) = Case14WithVirtuals();
        var mm = new MeasurementModel(model, set);
        double[] w = Weights(mm);
        double maxFree = 0.0;
        for (int r = 0; r < w.Length; r++)
            if (!string.Equals(mm.Measurements[r].Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal))
                maxFree = Math.Max(maxFree, w[r]);

        var step = new ConstrainedDenseWlsStep(new DenseLuSolver());
        step.Begin(model, mm, w);
        Assert.Equal(NumericTolerances.HardConstraintWeightScale * maxFree, step.ConstraintWeight);

        // Relative, not absolute: scaling every free weight by 1e4 (e.g. PMU-grade metering) scales the
        // constraint weight with it, so it always dominates. The old absolute 1e6 / 1e8 did not.
        double[] w2 = w.Select(x => x * 1e4).ToArray();
        var step2 = new ConstrainedDenseWlsStep(new DenseLuSolver());
        step2.Begin(model, mm, w2);
        Assert.Equal(NumericTolerances.HardConstraintWeightScale * maxFree * 1e4, step2.ConstraintWeight);
    }

    [Fact]
    public void Constrained_WLS_fails_loud_with_no_free_telemetry_or_a_bad_weight()
    {
        (GridModel model, MeasurementSet set) = Case14WithVirtuals();
        var onlyVirtuals = set with
        {
            Measurements = set.Measurements
                .Where(m => string.Equals(m.Source, "virtual-zero-injection", StringComparison.Ordinal)).ToList(),
        };
        var mmV = new MeasurementModel(model, onlyVirtuals);
        Assert.Throws<InvalidOperationException>(() => new ConstrainedDenseWlsStep(new DenseLuSolver()).Begin(model, mmV, Weights(mmV)));

        var mm = new MeasurementModel(model, set);
        double[] w = Weights(mm);
        int free = Enumerable.Range(0, w.Length).First(r =>
            !string.Equals(mm.Measurements[r].Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal));
        w[free] = 0.0;
        Assert.Throws<InvalidOperationException>(() => new ConstrainedDenseWlsStep(new DenseLuSolver()).Begin(model, mm, w));
    }

    // ---------------------------------------------------------------- ROCm dense (R049 / R301)

    [Fact]
    public void ROCm_pivot_check_rejects_exactly_what_the_CPU_LU_rejects()
    {
        // A near-singular matrix the CPU DenseLuSolver throws on. Factor it on the host with the same partial
        // pivoting hipSOLVER getrf performs and run the ROCm post-factorisation check on the result: it must
        // throw too (before R049 ROCm only rejected an exactly zero U(i,i) and returned garbage as success).
        var a = new double[,] { { 4, 1, 2 }, { 1, 3, 0.5 }, { 5, 4, 2.5 * (1 + 2.220446049250313e-16) } };
        Assert.Throws<InvalidOperationException>(() => new DenseLuSolver().SolveInPlace((double[,])a.Clone(), new[] { 1.0, 1.0, 1.0 }));
        (double[] lu, double scale) = HostLuColumnMajor(a);
        Assert.Throws<InvalidOperationException>(() => RocmDenseLinearSolver.ThrowIfNearSingular(lu, 3, scale));

        // A well-conditioned matrix passes both.
        var good = new double[,] { { 4, 1, 2 }, { 1, 3, 0.5 }, { 2, 0.5, 5 } };
        new DenseLuSolver().SolveInPlace((double[,])good.Clone(), new[] { 1.0, 1.0, 1.0 });
        (double[] luGood, double scaleGood) = HostLuColumnMajor(good);
        RocmDenseLinearSolver.ThrowIfNearSingular(luGood, 3, scaleGood);
    }

    // Plain LU with partial pivoting (the getrf algorithm), returned column-major with the matrix scale.
    private static (double[] Lu, double Scale) HostLuColumnMajor(double[,] src)
    {
        int n = src.GetLength(0);
        var m = (double[,])src.Clone();
        double scale = NumericTolerances.MaxAbs(src);
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[p, k])) p = i;
            for (int j = 0; j < n; j++) (m[k, j], m[p, j]) = (m[p, j], m[k, j]);
            if (m[k, k] == 0.0) continue;
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                m[i, k] = f;
                for (int j = k + 1; j < n; j++) m[i, j] -= f * m[k, j];
            }
        }
        var col = new double[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) col[j * n + i] = m[i, j];
        return (col, scale);
    }
}
