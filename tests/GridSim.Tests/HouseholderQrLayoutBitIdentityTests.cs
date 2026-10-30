using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS11 R119: the Householder QR used by the QR estimation method now keeps its working
/// copy column-major (contiguous column walks) and one reused Householder vector. Only storage changed, so the
/// solution must be bit-identical to the original routine, kept here verbatim.
/// </summary>
public class HouseholderQrLayoutBitIdentityTests
{
    /// <summary>The pre-R119 SolveLeastSquares, verbatim.</summary>
    private static double[] Reference(double[,] a, double[] b)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        if (m < n) throw new ArgumentException($"least squares needs m >= n (got {m} x {n}).");

        var r = (double[,])a.Clone();
        var c = (double[])b.Clone();
        double aNorm = NumericTolerances.MaxAbs(a);
        if (double.IsNaN(aNorm))
            throw new ArgumentException("least squares: the matrix contains a NaN entry.", nameof(a));
        double pivotFloor = NumericTolerances.PivotFloor(aNorm);

        for (int k = 0; k < n; k++)
        {
            double norm = 0;
            for (int i = k; i < m; i++) norm += r[i, k] * r[i, k];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;

            double alpha = r[k, k] > 0 ? -norm : norm;
            var v = new double[m];
            for (int i = k; i < m; i++) v[i] = r[i, k];
            v[k] -= alpha;
            double vnorm2 = 0;
            for (int i = k; i < m; i++) vnorm2 += v[i] * v[i];
            if (vnorm2 <= 0) continue;

            for (int j = k; j < n; j++)
            {
                double s = 0;
                for (int i = k; i < m; i++) s += v[i] * r[i, j];
                s = 2.0 * s / vnorm2;
                for (int i = k; i < m; i++) r[i, j] -= s * v[i];
            }
            double sb = 0;
            for (int i = k; i < m; i++) sb += v[i] * c[i];
            sb = 2.0 * sb / vnorm2;
            for (int i = k; i < m; i++) c[i] -= sb * v[i];
        }

        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = c[i];
            for (int j = i + 1; j < n; j++) sum -= r[i, j] * x[j];
            if (Math.Abs(r[i, i]) <= pivotFloor)
                throw new InvalidOperationException("rank-deficient least-squares system");
            x[i] = sum / r[i, i];
        }
        return x;
    }

    [Fact]
    public void Column_major_qr_is_bit_identical_to_the_original_routine()
    {
        var rng = new Random(119);
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 1 + rng.Next(25);
            int m = n + rng.Next(40);
            var a = new double[m, n];
            var b = new double[m];
            for (int i = 0; i < m; i++)
            {
                b[i] = rng.NextDouble() * 2 - 1;
                for (int j = 0; j < n; j++)
                    if (rng.NextDouble() < 0.5) a[i, j] = (rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.Next(-3, 4));
            }
            for (int j = 0; j < n; j++) a[j, j] += 1.0;   // mostly full rank
            if (trial % 7 == 0 && n > 1)
                for (int i = 0; i < m; i++) a[i, n - 1] = a[i, 0];   // exactly rank-deficient: both must throw

            double[]? expected = null;
            try { expected = Reference(a, b); }
            catch (InvalidOperationException) { }
            if (expected is null)
            {
                Assert.Throws<InvalidOperationException>(() => HouseholderQr.SolveLeastSquares(a, b));
                continue;
            }
            double[] actual = HouseholderQr.SolveLeastSquares(a, b);
            Assert.Equal(expected.Length, actual.Length);
            for (int j = 0; j < n; j++)
                Assert.True(BitConverter.DoubleToInt64Bits(expected[j]) == BitConverter.DoubleToInt64Bits(actual[j]),
                    $"trial {trial} x[{j}]: expected {expected[j]:R}, got {actual[j]:R}");
        }
    }
}
