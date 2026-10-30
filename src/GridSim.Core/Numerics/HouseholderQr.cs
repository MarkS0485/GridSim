namespace GridSim.Core.Numerics;

/// <summary>
/// Householder QR least-squares solver: minimises ||A x - b|| for an overdetermined system by orthogonal
/// triangularisation, without forming the normal equations A^T A. Forming A^T A squares the condition number;
/// the orthogonal factorization does not, so this is the numerically-robust path for an ill-conditioned
/// weighted least-squares estimate (weight the rows of A and b by sqrt(w) and the solution is the WLS state).
/// </summary>
public static class HouseholderQr
{
    /// <summary>GR-460: a column's sum of squares below this has underflowed or lost digits in the subnormals
    /// (every entry below about 1e-145), so its Householder vector is built from the column scaled by its largest
    /// magnitude. Far below any per-unit estimation column; at 1e-290 the sum is still far above the smallest
    /// normal double (about 4e17 times), so a column above it keeps its full precision.</summary>
    internal const double TinySumOfSquares = 1e-290;

    /// <summary>Solve min ||A x - b|| for A (m x n, m &gt;= n) via Householder QR. Returns x (length n).</summary>
    public static double[] SolveLeastSquares(double[,] a, double[] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        if (m < n) throw new ArgumentException($"least squares needs m >= n (got {m} x {n}).");

        // The working copy of A is held COLUMN-major (rt[j, i] = A[i, j]). Every inner loop below walks one
        // column over rows i = k..m-1, which in the old row-major copy strode across memory by n doubles per
        // step (case 300 with QR: 98 s, almost all cache misses). October 2026 review (R119): the transposed
        // layout makes those walks contiguous. Only the storage changed: every value is computed by the same
        // operations in the same order, so the result is bit-identical (pinned by a test against the original
        // routine).
        var rt = new double[n, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) rt[j, i] = a[i, j];
        var c = (double[])b.Clone();

        // Reference scale for the rank test: the largest magnitude anywhere in A. A diagonal entry of R at or
        // below NumericTolerances.RelativePivotFloor times this is a collapsed (rank-deficient) direction - the
        // same relative floor every LU in GridSim uses. Before October 2026 (R280) only an EXACT zero was
        // rejected, so a system rank-deficient to working precision returned huge finite garbage.
        double aNorm = NumericTolerances.MaxAbs(a);
        if (double.IsNaN(aNorm))
            throw new ArgumentException("least squares: the matrix contains a NaN entry.", nameof(a));
        // GR-479: an infinite entry is corrupt input, not a rank-deficient matrix (the infinite floor said the latter).
        if (double.IsInfinity(aNorm))
            throw new ArgumentException("least squares: the matrix contains an infinite entry.", nameof(a));
        double pivotFloor = NumericTolerances.PivotFloor(aNorm);

        // GR-460: the right-hand side is checked as the matrix is. A NaN or infinite b used to flow through the
        // reflections into a NaN solution with no error; the caller then had to notice the NaN step. Refusing here
        // names the fault at its source (the WLS step reads an ArgumentException as a failed step, as before).
        if (b.Length != m)
            throw new ArgumentException($"least squares: the right-hand side has {b.Length} entries for {m} rows.", nameof(b));
        for (int i = 0; i < m; i++)
            if (!double.IsFinite(b[i]))
                throw new ArgumentException($"least squares: right-hand side entry {i} is not finite ({b[i]}).", nameof(b));

        // One Householder-vector buffer for all columns (R119: was a fresh m-length array per column, m x n
        // doubles of garbage per solve). Column k writes and reads only v[k..m-1], so the stale entries below
        // k are never used and the arithmetic is unchanged to the bit.
        var v = new double[m];
        for (int k = 0; k < n; k++)
        {
            // Householder vector v for column k over rows k..m-1.
            double norm = 0;
            for (int i = k; i < m; i++) norm += rt[k, i] * rt[k, i];
            if (norm < TinySumOfSquares)
            {
                // GR-460: the plain sum of squares underflows (to zero, or into the subnormals where it loses its
                // digits) for a column whose entries are all below about 1e-145. The reflection used to be skipped
                // then, leaving the rows below k un-eliminated, and the back-substitution returned a wrong answer
                // with no error. H = I - 2 v v^T / (v^T v) does not change when v is scaled, so the vector is built
                // from the column divided by its largest magnitude instead; applying H below still writes the true
                // R(k, k) into the column. A column of exact zeros is still skipped (the rank test refuses it). Only
                // such tiny columns take this branch, so every other column is computed exactly as before.
                double colMax = 0;
                for (int i = k; i < m; i++) colMax = Math.Max(colMax, Math.Abs(rt[k, i]));
                if (colMax == 0) continue;
                double scaledNorm = 0;
                for (int i = k; i < m; i++) { v[i] = rt[k, i] / colMax; scaledNorm += v[i] * v[i]; }
                scaledNorm = Math.Sqrt(scaledNorm);
                v[k] -= rt[k, k] > 0 ? -scaledNorm : scaledNorm;   // -sign(R[k,k]) * ||.||, in the scaled units
            }
            else
            {
                norm = Math.Sqrt(norm);
                double alpha = rt[k, k] > 0 ? -norm : norm; // -sign(R[k,k]) * ||.||
                for (int i = k; i < m; i++) v[i] = rt[k, i];
                v[k] -= alpha;
            }
            double vnorm2 = 0;
            for (int i = k; i < m; i++) vnorm2 += v[i] * v[i];
            if (vnorm2 <= 0) continue;

            // Apply H = I - 2 v v^T / (v^T v) to R[:, k..n-1] and to c.
            for (int j = k; j < n; j++)
            {
                double s = 0;
                for (int i = k; i < m; i++) s += v[i] * rt[j, i];
                s = 2.0 * s / vnorm2;
                for (int i = k; i < m; i++) rt[j, i] -= s * v[i];
            }
            double sb = 0;
            for (int i = k; i < m; i++) sb += v[i] * c[i];
            sb = 2.0 * sb / vnorm2;
            for (int i = k; i < m; i++) c[i] -= sb * v[i];
        }

        // Back-substitute the upper-triangular R (n x n) against the transformed rhs.
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = c[i];
            for (int j = i + 1; j < n; j++) sum -= rt[j, i] * x[j];
            if (Math.Abs(rt[i, i]) <= pivotFloor)
                throw new InvalidOperationException(
                    $"rank-deficient least-squares system: |R({i},{i})| = {Math.Abs(rt[i, i]):E3} is at or below the tolerance {pivotFloor:E3} (matrix scale {aNorm:E3}).");
            x[i] = sum / rt[i, i];
        }
        return x;
    }
}
