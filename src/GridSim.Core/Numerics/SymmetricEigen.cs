namespace GridSim.Core.Numerics;

/// <summary>
/// Eigenvalues and eigenvectors of a small dense SYMMETRIC matrix by the cyclic Jacobi rotation
/// method. Robust and self-contained (no BLAS/LAPACK dependency), and exact for the small symmetric
/// systems the experimental continuum-wave model forms - the generalised swing eigenproblem over a
/// network's generators/domains is a handful to a few dozen states. Not intended for large matrices.
/// </summary>
public static class SymmetricEigen
{
    /// <summary>
    /// Eigenvalues of the symmetric matrix <paramref name="matrix"/> (n x n), ascending. The input is
    /// not modified.
    /// </summary>
    public static double[] Eigenvalues(double[,] matrix)
    {
        (double[] values, _) = Decompose(matrix, wantVectors: false);
        return values;
    }

    /// <summary>
    /// Full eigendecomposition: ascending eigenvalues and the matching eigenvectors as the COLUMNS of
    /// <c>Vectors</c> (Vectors[i, j] is component i of the j-th eigenvector). The input is not modified.
    /// </summary>
    public static (double[] Values, double[,] Vectors) EigenDecomposition(double[,] matrix)
        => Decompose(matrix, wantVectors: true);

    private static (double[] Values, double[,] Vectors) Decompose(double[,] matrix, bool wantVectors)
    {
        int n = matrix.GetLength(0);
        if (matrix.GetLength(1) != n) throw new ArgumentException("matrix must be square.", nameof(matrix));
        var a = (double[,])matrix.Clone();

        // Accumulate the rotations into v (starts as identity); its columns become the eigenvectors.
        var v = new double[n, n];
        if (wantVectors) for (int i = 0; i < n; i++) v[i, i] = 1.0;

        // Scale-independent stopping rule (R184): stop when the off-diagonal mass is at the rounding level of
        // the matrix, sum_{p<q} a_pq^2 <= (eps * ||A||_F)^2. The Frobenius norm is invariant under the
        // orthogonal rotations, so it is computed once from the input. The old absolute 1e-30 never tripped
        // for a matrix scaled around 1e2-1e3 (all 100 sweeps ran) and tripped at once, with no rotation at
        // all, for a matrix whose entries are around 1e-16. A non-finite entry fails loud instead of "converging".
        double frob2 = 0.0;
        foreach (double x in a) frob2 += x * x;
        if (!double.IsFinite(frob2))
            throw new ArgumentException("matrix contains a NaN or infinite entry (or entries so large their squares overflow).", nameof(matrix));
        double tolNorm = NumericTolerances.JacobiRelativeOffTolerance * Math.Sqrt(frob2);
        double offTolerance = tolNorm * tolNorm;

        // Symmetry is a precondition, checked: the rotations read the upper triangle for a[p,q] and the lower for a[i,p]
        // when i > p, so a non-symmetric input was symmetrised in an order-dependent way, still converged, and returned
        // the eigenvalues of neither A nor (A + A^T)/2 (the sweep cap the old comment relied on never caught it).
        // Allowed asymmetry: 1e-12 of the Frobenius norm (round-off of a symmetric construction, nothing structural).
        double asymBound = 1e-12 * Math.Sqrt(frob2);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (!(Math.Abs(a[i, j] - a[j, i]) <= asymBound))
                    throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"SymmetricEigen: the matrix is not symmetric (|a[{i},{j}] - a[{j},{i}]| = {Math.Abs(a[i, j] - a[j, i]):R}, " +
                        $"allowed {asymBound:R}); use the general eigen-solver."), nameof(matrix));

        bool converged = false;
        // The convergence test runs once more after the last allowed sweep, so a matrix that reaches the
        // tolerance on exactly the final sweep is still reported as converged.
        for (int sweep = 0; ; sweep++)
        {
            double off = 0.0;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                    off += a[p, q] * a[p, q];
            if (off <= offTolerance) { converged = true; break; }
            if (sweep == NumericTolerances.JacobiMaxSweeps) break;

            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p, q];
                    // Underflow guard only (not a tolerance): an off-diagonal this small has nothing to rotate,
                    // and theta = (aqq - app) / (2 apq) would overflow.
                    if (Math.Abs(apq) < NumericTolerances.UnderflowGuard) continue;

                    double app = a[p, p], aqq = a[q, q];
                    double theta = (aqq - app) / (2.0 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0) t = 1.0;
                    double c = 1.0 / Math.Sqrt(t * t + 1.0);
                    double s = t * c;
                    double tau = s / (1.0 + c);

                    a[p, p] = app - t * apq;
                    a[q, q] = aqq + t * apq;
                    a[p, q] = 0.0;
                    a[q, p] = 0.0;
                    for (int i = 0; i < n; i++)
                    {
                        if (i == p || i == q) continue;
                        double aip = a[i, p], aiq = a[i, q];
                        a[i, p] = a[p, i] = aip - s * (aiq + tau * aip);
                        a[i, q] = a[q, i] = aiq + s * (aip - tau * aiq);
                    }
                    if (wantVectors)
                        for (int i = 0; i < n; i++)
                        {
                            double vip = v[i, p], viq = v[i, q];
                            v[i, p] = vip - s * (viq + tau * vip);
                            v[i, q] = viq + s * (vip - tau * viq);
                        }
                }
        }
        // Fail loud rather than return a half-diagonalised matrix as eigenvalues. Cyclic Jacobi converges
        // quadratically on any symmetric matrix, so exhausting the sweep cap means the input was not symmetric
        // (or held non-finite values).
        if (!converged)
            throw new InvalidOperationException(
                $"Jacobi eigen-solver did not converge in {NumericTolerances.JacobiMaxSweeps} sweeps (off-diagonal mass still above "
                + $"(eps * ||A||_F)^2 = {offTolerance:E3}); the input must be a finite symmetric matrix.");

        // Sort eigenvalues ascending, carrying the eigenvector columns along.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var diag = new double[n];
        for (int i = 0; i < n; i++) diag[i] = a[i, i];
        // Degenerate eigenvalues tie; which eigenvector column comes first is then the sort algorithm's, so it is
        // GridSim's frozen copy of the .NET 10 introsort (the same order on every runtime; on .NET 10 exactly Array.Sort's).
        FrozenIntroSort.Sort(order, (x, y) => diag[x].CompareTo(diag[y]));

        var values = new double[n];
        var vectors = wantVectors ? new double[n, n] : new double[0, 0];
        for (int j = 0; j < n; j++)
        {
            values[j] = diag[order[j]];
            if (wantVectors) for (int i = 0; i < n; i++) vectors[i, j] = v[i, order[j]];
        }
        return (values, vectors);
    }
}
