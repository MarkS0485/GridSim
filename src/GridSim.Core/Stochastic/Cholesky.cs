using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// A standalone, deterministic Cholesky factorisation for correlated sampling (R4-09). Cholesky-Banachiewicz in fixed
/// row order: every sum runs left to right over the already-built columns, so the factor is bit-identical on every run,
/// thread count and platform (IEEE add, multiply, divide and <c>Math.Sqrt</c> only). The existing Cholesky code in
/// <c>Estimation/Dse</c> and <c>Diagnostics/DomainRefusal.cs</c> was not reused: the first is private to the Kalman
/// estimators and the second is not a factorisation (verified when R4-09 was written).
/// </summary>
public static class Cholesky
{
    /// <summary>
    /// The lower-triangular factor L with <c>L L^T = a</c>. Refuses a non-square, non-finite, non-symmetric (relative
    /// tolerance 1e-12) or not positive definite matrix.
    /// </summary>
    public static double[,] Factor(double[,] a)
    {
        ArgumentNullException.ThrowIfNull(a);
        int n = a.GetLength(0);
        if (n == 0 || a.GetLength(1) != n)
            throw DomainRefusal.Mark(new ArgumentException("The matrix must be square and non-empty.", nameof(a)));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (!double.IsFinite(a[i, j]))
                    throw DomainRefusal.Mark(new ArgumentException("The matrix has a non-finite entry.", nameof(a)));
                double scale = Math.Max(1.0, Math.Max(Math.Abs(a[i, j]), Math.Abs(a[j, i])));
                if (Math.Abs(a[i, j] - a[j, i]) > 1e-12 * scale)
                    throw DomainRefusal.Mark(new ArgumentException($"The matrix is not symmetric at ({i}, {j}).", nameof(a)));
            }
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (!(s > 0.0))
                        throw DomainRefusal.Mark(new ArgumentException(
                            $"The matrix is not positive definite (pivot {i} is {s:R}); a correlation matrix must be positive definite.", nameof(a)));
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        }
        return l;
    }

    /// <summary>y = L z for a lower-triangular L, each row summed left to right.</summary>
    public static void MultiplyLower(double[,] l, ReadOnlySpan<double> z, Span<double> y)
    {
        ArgumentNullException.ThrowIfNull(l);
        int n = l.GetLength(0);
        if (z.Length < n || y.Length < n) throw new ArgumentException("The vectors are shorter than the factor.");
        for (int i = 0; i < n; i++)
        {
            double s = 0.0;
            for (int k = 0; k <= i; k++) s += l[i, k] * z[k];
            y[i] = s;
        }
    }
}
