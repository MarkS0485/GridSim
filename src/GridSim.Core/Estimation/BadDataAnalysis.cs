using GridSim.Core.Numerics;

namespace GridSim.Core.Estimation;

/// <summary>
/// Largest-normalized-residual bad-data machinery. At a converged estimate the residual
/// covariance diagonal is Omega_i_i = sigma_i^2 - h_i^T*G^-1*h_i (h_i = the measurement's Jacobian row,
/// G = H^TWH), and the normalized residual r^N_i = |r_i|/sqrtOmega_i_i identifies the gross error the
/// chi-square test detected. A measurement with Omega_i_i ~ 0 is <b>critical</b>: it is the only observation
/// of some state direction, its residual is structurally zero, and removing it would make the
/// system unobservable - so it is flagged, never removed. The gain matrix is factored once and
/// back-substituted per measurement (O(s^3) + m*O(s^2)), the dense-M1 cost profile.
/// </summary>
internal static class BadDataAnalysis
{
    /// <summary>Omega_i_i at or below sigma_i^2*this counts as critical (no redundancy behind the measurement).
    /// The value lives in the shared tolerance table so hypothesis-testing identification uses the same one.</summary>
    internal const double CriticalFloor = NumericTolerances.CriticalResidualVarianceFraction;

    /// <summary>
    /// Normalized residuals (pu residuals <paramref name="residualPu"/> against Omega) and criticality
    /// flags for every measurement at the state where <paramref name="jac"/>/<paramref name="gain"/>
    /// were evaluated. <paramref name="gain"/> is factored in place (caller's buffer is consumed).
    /// </summary>
    public static (double[] Normalized, bool[] Critical) NormalizedResiduals(
        MeasurementModel mm, double[,] jac, double[,] gain, double[] w, double[] residualPu)
    {
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        var lu = new LuFactorization(gain, s);

        var normalized = new double[m];
        var critical = new bool[m];
        var hRow = new double[s];
        var t = new double[s];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < s; c++) hRow[c] = jac[r, c];
            lu.Solve(hRow, t);
            double hGh = SimdKernels.Dot(hRow, 0, t, 0, s);

            double sigmaSq = 1.0 / w[r];
            double omega = sigmaSq - hGh;
            if (omega <= sigmaSq * CriticalFloor)
            {
                critical[r] = true;
                normalized[r] = double.NaN;
            }
            else
            {
                normalized[r] = Math.Abs(residualPu[r]) / Math.Sqrt(omega);
            }
        }
        return (normalized, critical);
    }

    /// <summary>
    /// Sparse-backend residual covariance: ONE Gilbert-Peierls factorization of the gain (done
    /// by the caller, which owns the prepared structure) serves all m per-measurement quadratic
    /// forms h_i^T G^-1 h_i; the Omega/criticality logic is identical to the dense path. H rows arrive
    /// as the estimator's sparse Jacobian rows.
    ///
    /// <para>October 2026 review (R010/R117): this stage was about 95% of a default gb-full estimate
    /// (122 s of 128 s on the i3 reference machine). Each row used to run a FULL forward and back solve
    /// with two fresh s-length allocations (3.9 GB of garbage per gb-full pass). It now uses
    /// <see cref="SparseLuFactorization.InverseQuadraticForm"/>, which does only the part of each solve
    /// that can reach the row's own entries, solves eight rows per pass over L and U (one vector multiply
    /// and one vector subtract per factor entry, never fused), and works in reused per-worker workspaces;
    /// every one of those steps is proved bit-identical to the full solve (see that type). The blocks are
    /// independent, so they are spread over the cores: each row's arithmetic is the same whichever thread
    /// or block lane runs it, and each writes only its own output slot, so the result does not depend on
    /// the thread count, the scheduling or the grouping (pinned by tests against the original full-solve
    /// loop, serial and parallel, at every SIMD tier).</para>
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Worker cap; 0 or less means one per logical processor. Any value
    /// gives the same bits.</param>
    public static (double[] Normalized, bool[] Critical) NormalizedResiduals(
        MeasurementModel mm, SparseJacobianRows jac, SparseLuFactorization lu, double[] residualPu, double[] w,
        int maxDegreeOfParallelism = 0)
    {
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        if (jac.Rows.Length != m || residualPu.Length != m || w.Length != m)
            throw new ArgumentException(
                $"Residual covariance: {m} measurements but {jac.Rows.Length} Jacobian rows, {residualPu.Length} residuals and {w.Length} weights.");

        var normalized = new double[m];
        var critical = new bool[m];
        SparseLuFactorization.InverseQuadraticForm form = lu.CreateInverseQuadraticForm();
        if (form.Order != s)
            throw new ArgumentException($"Residual covariance: the gain factor has order {form.Order}, the state size is {s}.");

        // Group rows whose back solves stop at similar slots into blocks of eight right-hand sides, so each
        // block's shared sweep range stays tight. The key (stop slot, then row index) is a strict total order,
        // so the grouping is the same on every run; and it affects only speed - every row's bits are fixed by
        // its own arithmetic (see EvaluateBlock).
        var keys = new long[m];
        var order = new int[m];
        for (int r = 0; r < m; r++)
        {
            keys[r] = (long)form.BackSolveStop(jac.Rows[r]) * m + r;
            order[r] = r;
        }
        Array.Sort(keys, order);

        const int K = SparseLuFactorization.InverseQuadraticForm.BlockWidth;
        int blocks = (m + K - 1) / K;
        var hGh = new double[m];
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism,
        };
        // One all-zero lane-interleaved workspace per worker (the evaluator leaves it all-zero after each block).
        Parallel.For(0, blocks, po, () => new double[s * K], (b, _, work) =>
        {
            int start = b * K;
            form.EvaluateBlock(jac.Rows, order, start, Math.Min(K, m - start), hGh, work);
            return work;
        }, _ => { });

        for (int r = 0; r < m; r++) ClassifyRow(r, hGh[r], w, residualPu, normalized, critical);
        return (normalized, critical);
    }

    /// <summary>Omega_ii = sigma_i^2 - h_i^T G^-1 h_i, then either the critical flag (Omega at or below the
    /// floor) or the normalised residual |r_i| / sqrt(Omega_ii). The exact expressions of the original loop.</summary>
    private static void ClassifyRow(int r, double hGh, double[] w, double[] residualPu, double[] normalized, bool[] critical)
    {
        double sigmaSq = 1.0 / w[r];
        double omega = sigmaSq - hGh;
        if (omega <= sigmaSq * CriticalFloor)
        {
            critical[r] = true;
            normalized[r] = double.NaN;
        }
        else
        {
            normalized[r] = Math.Abs(residualPu[r]) / Math.Sqrt(omega);
        }
    }

    /// <summary>
    /// The state error-covariance diagonal diag(G^-1) at the estimate (G = H^T W H, the gain matrix). It is
    /// the estimator's own confidence - the 1-sigma^2 variance of each state component the measurement
    /// redundancy implies - consumed by the real-time confidence output and, later, a recursive filter's
    /// prior covariance. One O(s^3) factorization plus s unit-vector back-substitutions; the passed
    /// <paramref name="gain"/> buffer is consumed by the factorization. Standard WLS covariance: G is formed
    /// with the DECLARED measurement weights (1/sigma^2), so diag(G^-1) is the classical estimate variance.
    /// </summary>
    public static double[] StateCovarianceDiagonal(double[,] gain, int s)
    {
        var lu = new LuFactorization(gain, s);
        var e = new double[s];
        var y = new double[s];
        var diag = new double[s];
        for (int j = 0; j < s; j++)
        {
            e[j] = 1.0;
            lu.Solve(e, y);
            diag[j] = y[j];   // e_j^T G^-1 e_j
            e[j] = 0.0;
        }
        return diag;
    }

    /// <summary>
    /// Plain LU with partial pivoting that keeps its pivot record, so one O(s^3) factorization
    /// serves the m per-measurement back-substitutions. Throws like the LU solvers on a pivot
    /// collapsed below the relative floor (callers only reach here at a converged, observable
    /// estimate, so this is a genuine anomaly).
    /// </summary>
    private sealed class LuFactorization
    {
        private readonly double[] _m;
        private readonly int[] _pivot;
        private readonly int _n;

        public LuFactorization(double[,] a, int n)
        {
            _n = n;
            _m = new double[n * n];
            Buffer.BlockCopy(a, 0, _m, 0, n * n * sizeof(double));
            _pivot = new int[n];

            double norm = 0;
            foreach (double v in _m) { double av = Math.Abs(v); if (av > norm) norm = av; }
            double floor = Numerics.NumericTolerances.PivotFloor(norm);   // the shared relative floor (R141)

            for (int k = 0; k < n; k++)
            {
                int p = k;
                double max = Math.Abs(_m[k * n + k]);
                for (int i = k + 1; i < n; i++)
                {
                    double v = Math.Abs(_m[i * n + k]);
                    if (v > max) { max = v; p = i; }
                }
                if (max <= floor)
                    throw new InvalidOperationException(
                        $"Near-singular gain matrix in residual-covariance factorization (pivot {max:E3} at column {k}).");
                _pivot[k] = p;
                if (p != k)
                    for (int j = 0; j < n; j++)
                        (_m[k * n + j], _m[p * n + j]) = (_m[p * n + j], _m[k * n + j]);

                double pivotVal = _m[k * n + k];
                int tail = n - (k + 1); // trailing columns k+1..n-1
                for (int i = k + 1; i < n; i++)
                {
                    double factor = _m[i * n + k] / pivotVal;
                    _m[i * n + k] = factor; // store the multiplier (unit-lower L)
                    if (factor == 0) continue;
                    // dst[j] -= factor*src[j] over the contiguous trailing row - the O(s^3) hot spot.
                    Numerics.SimdKernels.SubtractScaledRow(_m, i * n + k + 1, k * n + k + 1, tail, factor);
                }
            }
        }

        /// <summary>Solve G*x = b (b untouched).</summary>
        public void Solve(double[] b, double[] x)
        {
            int n = _n;
            Array.Copy(b, x, n);
            // Apply ALL row interchanges to the RHS first (LAPACK dgetrs order). The factorization
            // swaps full rows - including already-stored multipliers - so the final L is expressed
            // in fully-permuted row order and an interleaved swap-and-eliminate walk reads the
            // wrong multipliers against the wrong entries (verified against scipy.lu_solve).
            for (int k = 0; k < n; k++)
            {
                int p = _pivot[k];
                if (p != k) (x[k], x[p]) = (x[p], x[k]);
            }
            for (int k = 0; k < n; k++)
            {
                double xk = x[k];
                if (xk == 0) continue;
                for (int i = k + 1; i < n; i++) x[i] -= _m[i * n + k] * xk;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                // sum = x[i] - dot(U row i, x) over the contiguous upper span j = i+1..n-1.
                double sum = x[i] - Numerics.SimdKernels.Dot(_m, i * n + i + 1, x, i + 1, n - (i + 1));
                x[i] = sum / _m[i * n + i];
            }
        }
    }
}
