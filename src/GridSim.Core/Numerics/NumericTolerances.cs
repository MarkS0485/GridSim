namespace GridSim.Core.Numerics;

/// <summary>
/// The ONE table of numerical tolerances shared by GridSim's linear solvers, eigen-solvers and state
/// estimator backends (October 2026, workstream WS5: R034, R049, R141-R144, R181, R183, R184, R280, R301).
///
/// <para><b>Why one table.</b> Before this table the same question ("is this pivot numerically zero?") was
/// answered differently at every site: a relative 1e-12 in the dense LU, an absolute 1e-300 in the sparse LU,
/// exact zero in the Householder QR and the ROCm LU, an absolute 1e-14 clamp in the complex LU, a relative
/// 1e-14 in the dynamic-estimator inverse, and so on. Absolute floors are scale dependent: a per-unit Y-bus
/// with entries of 1e3, or an estimator gain matrix with entries of 1e8, never trips them, so a matrix that
/// is singular to working precision "factorised successfully" and returned garbage as an answer. Each
/// constant below names ONE concept, says what it is relative to, and is the only place that value lives.</para>
///
/// <para><b>Bit-identity.</b> A pivot floor only decides what is DECLARED singular. It never enters the
/// arithmetic of a non-singular solve, so moving a site onto this table leaves every converged answer on a
/// non-singular system bit-for-bit unchanged; it changes only which near-singular systems are rejected.</para>
/// </summary>
public static class NumericTolerances
{
    /// <summary>
    /// Relative singular-pivot floor. During any elimination (dense LU, sparse LU, complex LU, Householder QR,
    /// Gauss-Jordan inverse, the GPU and ROCm LUs, the estimator's gain factorisations) a pivot whose magnitude
    /// is at or below <c>RelativePivotFloor * max|A_ij|</c> is numerically singular: the matrix has lost about
    /// twelve of its sixteen significant digits in that direction, so any answer built on it is noise. The
    /// reference scale is the largest magnitude anywhere in the ORIGINAL matrix (see <see cref="PivotFloor"/>),
    /// which makes the test scale-independent; an exact-zero pivot is the <c>max|A_ij| = 0</c> subcase.
    /// Every linear-system elimination site that used 1e-12, 1e-13, 1e-14 (relative or absolute), 1e-300 or exact zero
    /// for this concept now uses this one value. Not every Numerics threshold is this concept: the bounded simplex keeps
    /// its own absolute EtaPivotFloor (1e-12) and RefactorPivotClamp (1e-14), Complex2x2 a relative 1e-15, and
    /// GeneralEigen.InvertComplexWith a unit scale for a zero V (GR-465).
    /// </summary>
    public const double RelativePivotFloor = 1e-12;

    /// <summary>The absolute pivot floor for a matrix whose largest entry magnitude is
    /// <paramref name="matrixScale"/>: <c>RelativePivotFloor * matrixScale</c>. A pivot whose magnitude is
    /// at or below this value is singular (callers compare with <c>&lt;=</c>, so an exact zero is always
    /// caught, including on the all-zero matrix where the floor itself is zero).</summary>
    public static double PivotFloor(double matrixScale)
    {
        if (double.IsNaN(matrixScale) || matrixScale < 0.0)
            throw new ArgumentOutOfRangeException(nameof(matrixScale), matrixScale,
                "The matrix scale for a pivot floor must be a non-negative number (the largest |A_ij|).");
        return matrixScale * RelativePivotFloor;
    }

    /// <summary>The largest absolute entry of a dense matrix (the reference scale for
    /// <see cref="PivotFloor"/>). NaN entries make the result NaN so a corrupted matrix fails loud.</summary>
    public static double MaxAbs(double[,] a)
    {
        double s = 0.0;
        foreach (double v in a)
        {
            if (double.IsNaN(v)) return double.NaN;
            double av = Math.Abs(v);
            if (av > s) s = av;
        }
        return s;
    }

    /// <summary>The largest absolute entry of a flat array (the reference scale for
    /// <see cref="PivotFloor"/>). NaN entries make the result NaN so a corrupted matrix fails loud.</summary>
    public static double MaxAbs(ReadOnlySpan<double> values)
    {
        double s = 0.0;
        foreach (double v in values)
        {
            if (double.IsNaN(v)) return double.NaN;
            double av = Math.Abs(v);
            if (av > s) s = av;
        }
        return s;
    }

    /// <summary>
    /// Bug #40 (October 2026): the index of the first entry of <paramref name="values"/> that is NaN or infinite, or -1
    /// when every entry is finite. The direct solvers call it on the right-hand side before solving: a non-finite
    /// right-hand side can only give a non-finite solution, which they used to return as if it were an answer. It reads
    /// the values only, so it never changes the arithmetic of a solve on finite input.
    /// </summary>
    public static int FirstNonFinite(ReadOnlySpan<double> values)
    {
        for (int i = 0; i < values.Length; i++)
            if (!double.IsFinite(values[i])) return i;
        return -1;
    }

    /// <summary>The message the direct solvers refuse a non-finite right-hand side with: "&lt;solver&gt;: the right-hand
    /// side has a non-finite entry (value at row i), so the solution could not be finite." (bug #40).</summary>
    public static string NonFiniteRightHandSide(string solver, double value, int row) =>
        $"{solver}: the right-hand side has a non-finite entry ({value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} at row {row}), so the solution could not be finite.";

    /// <summary>
    /// Index of the first diagonal entry of a factored (LU) matrix whose magnitude is at or below the
    /// relative pivot floor for <paramref name="matrixScale"/>, or -1 when every pivot clears it. Used by
    /// backends whose factorisation runs in a vendor library (ROCm hipSOLVER getrf) to apply the same
    /// near-singular test the CPU LU applies during elimination: with partial pivoting the k-th pivot the
    /// CPU tests IS |U(k,k)|, so testing the diagonal afterwards rejects exactly the same matrices.
    /// <paramref name="luColumnMajor"/> is the n x n factored matrix in column-major order (LAPACK layout).
    /// </summary>
    public static int FirstSingularPivot(ReadOnlySpan<double> luColumnMajor, int n, double matrixScale)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "The matrix order must be non-negative.");
        if (luColumnMajor.Length < (long)n * n)
            throw new ArgumentException($"The factored matrix holds {luColumnMajor.Length} values but an order-{n} matrix needs {(long)n * n}.", nameof(luColumnMajor));
        double floor = PivotFloor(matrixScale);
        for (int k = 0; k < n; k++)
        {
            double u = luColumnMajor[k * n + k];
            // NaN fails every comparison, so test it explicitly: a NaN pivot is never a valid factorisation.
            if (double.IsNaN(u) || Math.Abs(u) <= floor) return k;
        }
        return -1;
    }

    /// <summary>
    /// Value an ILU(0) PRECONDITIONER pivot is pushed out to when it falls strictly below
    /// <c>RelativePivotFloor * max|A(i,:)|</c> (the largest magnitude in that row of the original matrix).
    /// Regularising a preconditioner pivot is legitimate - the preconditioner only steers the Krylov iteration,
    /// whose own residual test against the TRUE matrix decides success - but the guard must be relative to the
    /// row's scale, not the old absolute 1e-12 (which a Jacobian with entries of 1e4 never tripped and one with
    /// entries of 1e-14 always tripped). This is the same relative floor as every other pivot test.
    /// </summary>
    public static double Ilu0PivotGuard(double rowScale)
    {
        if (double.IsNaN(rowScale) || rowScale < 0.0)
            throw new ArgumentOutOfRangeException(nameof(rowScale), rowScale,
                "The row scale for the ILU(0) pivot guard must be a non-negative number.");
        // An all-zero row means A itself is singular; the guard must still be a finite non-zero number so the
        // preconditioner stays finite, and the Krylov loop then reports the singular system as non-convergence.
        return RelativePivotFloor * (rowScale > 0.0 ? rowScale : 1.0);
    }

    /// <summary>
    /// Underflow guard for a Krylov (BiCGSTAB) breakdown scalar such as rho = (r_hat, r). This is NOT a
    /// scale tolerance: it only stops a division by a number so close to zero that the quotient overflows.
    /// Convergence and stagnation are judged by the relative residual tests, which are scale-independent.
    /// Also the Jacobi eigen-solver's "nothing to rotate" skip for an off-diagonal that has underflowed.
    /// </summary>
    public const double UnderflowGuard = 1e-300;

    /// <summary>Default relative residual tolerance (||r|| / ||b||) of the real ILU0/Jacobi BiCGSTAB
    /// solvers (CPU <see cref="BiCgStab"/>, the ILGPU and ROCm sparse solvers).</summary>
    public const double DefaultKrylovTolerance = 1e-10;

    /// <summary>Default relative residual tolerance (||r|| / ||b||) of the complex Jacobi BiCGSTAB
    /// (<see cref="SparseComplexBiCgStabSolver"/>), which cross-validates the direct complex solvers and so
    /// is held two orders tighter than the real one.</summary>
    public const double DefaultComplexKrylovTolerance = 1e-12;

    /// <summary>Default iteration cap of every BiCGSTAB in GridSim for an order-<paramref name="n"/> system:
    /// <c>4n + 200</c>. In exact arithmetic a Krylov method finishes within n steps; the factor four and the
    /// fixed 200 leave room for the loss of orthogonality rounding causes on stiff Jacobians.</summary>
    public static int DefaultKrylovMaxIterations(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "The system order must be non-negative.");
        return 4 * n + 200;
    }

    /// <summary>
    /// Relative convergence tolerance of the cyclic Jacobi symmetric eigen-solver: iteration stops when the
    /// sum of squared off-diagonal entries is at or below <c>(JacobiRelativeOffTolerance * ||A||_F)^2</c>,
    /// i.e. when every off-diagonal is at the rounding level of the matrix. Machine epsilon, because rotations
    /// cannot reduce the off-diagonal mass below rounding of the diagonal. The old absolute 1e-30 never
    /// triggered for a matrix scaled around 1e2-1e3 (it ran all its sweeps) and triggered at once, without a
    /// single rotation, for a matrix with entries near 1e-16.
    /// </summary>
    public const double JacobiRelativeOffTolerance = MachineEpsilon;

    /// <summary>IEEE 754 double machine epsilon, 2^-52 (the gap between 1.0 and the next double). Note that
    /// .NET's <see cref="double.Epsilon"/> is NOT this: it is the smallest subnormal, about 4.9e-324.</summary>
    public const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>Sweep cap of the cyclic Jacobi eigen-solver. Jacobi converges quadratically, so a symmetric
    /// matrix of any size GridSim forms reaches <see cref="JacobiRelativeOffTolerance"/> in well under twenty
    /// sweeps; reaching this cap means the input was not a symmetric matrix and the solver throws.</summary>
    public const int JacobiMaxSweeps = 100;

    /// <summary>
    /// Relative tolerance under which two computed eigenvalues of <see cref="GeneralEigen"/> are treated as
    /// copies of ONE repeated eigenvalue: <c>|lambda_j - lambda_k| &lt;= EigenClusterRelativeTolerance *
    /// max(matrix scale, |lambda_k|)</c>. A semisimple (non-defective) repeated eigenvalue is perturbed only
    /// linearly by rounding (about 1e-16 relative), so 1e-6 catches every copy with a wide margin while
    /// staying far from genuinely distinct modes.
    /// </summary>
    public const double EigenClusterRelativeTolerance = 1e-6;

    /// <summary>
    /// Two unit eigenvectors of one eigenvalue cluster whose inner product magnitude is at or above
    /// <c>1 - EigenParallelTolerance</c> are the SAME direction: plain inverse iteration has returned one
    /// eigenvector twice, and the cluster must be recomputed with deflation to recover a basis of the
    /// eigenspace.
    /// </summary>
    public const double EigenParallelTolerance = 1e-6;

    /// <summary>
    /// Relative eigen-equation residual a deflated eigenvector must meet: <c>||A v - lambda v|| &lt;=
    /// EigenResidualRelativeTolerance * matrix scale</c>. Inverse iteration lands at about 1e-10 of the matrix
    /// scale (the shift offset), so 1e-6 accepts every genuine eigenvector; a defective (Jordan) cluster has
    /// no second eigenvector, the deflated vector leaves a residual of the order of the Jordan coupling, and
    /// the decomposition then fails loud as defective.
    /// </summary>
    public const double EigenResidualRelativeTolerance = 1e-6;

    /// <summary>
    /// Pseudo-weight multiplier that turns an equality-constrained estimator row (a virtual zero injection)
    /// into a "hard" row wherever a plain weighted gain is needed (the observability gate and the
    /// residual-covariance approximation): the constraint row is weighted <c>HardConstraintWeightScale *
    /// max(w_free)</c>, ten thousand times the strongest free measurement. Relative, so it always dominates
    /// whatever the metering mix (a PMU angle row at 1e-4 rad sigma already has w = 1e8), and ONE value, so
    /// the rank test runs on the same matrix whose covariance is reported. Replaced an absolute 1e6 in the
    /// gate and an absolute 1e8 in the covariance.
    /// </summary>
    public const double HardConstraintWeightScale = 1e4;

    /// <summary>
    /// A measurement whose residual variance Omega_ii is at or below <c>CriticalResidualVarianceFraction *
    /// sigma_i^2</c> is CRITICAL: its residual is structurally (near) zero, so no bad-data test can detect an
    /// error on it. Shared by the largest-normalised-residual test and hypothesis-testing identification.
    /// </summary>
    public const double CriticalResidualVarianceFraction = 1e-8;
}
