using System;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>The Householder QR least-squares solver: it matches the normal-equations solution on a
/// well-conditioned overdetermined system to machine precision, and stays accurate where forming A^T A would
/// lose precision (the point of the orthogonal path).</summary>
public class HouseholderQrTests
{
    [Fact]
    public void Matches_the_normal_equations_on_a_line_fit()
    {
        // Fit y = x0 + x1*t through (1,6),(2,5),(3,7),(4,10); the least-squares solution is (3.5, 1.4).
        var a = new double[4, 2] { { 1, 1 }, { 1, 2 }, { 1, 3 }, { 1, 4 } };
        var b = new double[] { 6, 5, 7, 10 };

        double[] x = HouseholderQr.SolveLeastSquares(a, b);
        Assert.Equal(3.5, x[0], 12);
        Assert.Equal(1.4, x[1], 12);
    }

    [Fact]
    public void Stays_accurate_on_an_ill_conditioned_system_where_normal_equations_lose_precision()
    {
        // A near-collinear design: columns [1, 1+eps]. A^T A is nearly singular (condition ~ 1/eps^2), but QR
        // conditions on 1/eps. With a known exact solution, QR recovers it far better than normal equations.
        double eps = 1e-6;
        var a = new double[3, 2]
        {
            { 1.0, 1.0 },
            { 1.0, 1.0 + eps },
            { 1.0, 1.0 + 2 * eps },
        };
        double[] xTrue = { 2.0, -3.0 };
        var b = new double[3];
        for (int i = 0; i < 3; i++) b[i] = a[i, 0] * xTrue[0] + a[i, 1] * xTrue[1];

        double[] x = HouseholderQr.SolveLeastSquares(a, b);

        // Normal equations for reference: solve (A^T A) x = A^T b directly.
        double a00 = 0, a01 = 0, a11 = 0, g0 = 0, g1 = 0;
        for (int i = 0; i < 3; i++)
        {
            a00 += a[i, 0] * a[i, 0]; a01 += a[i, 0] * a[i, 1]; a11 += a[i, 1] * a[i, 1];
            g0 += a[i, 0] * b[i]; g1 += a[i, 1] * b[i];
        }
        double det = a00 * a11 - a01 * a01;
        double ne0 = (a11 * g0 - a01 * g1) / det, ne1 = (a00 * g1 - a01 * g0) / det;

        double qrErr = Math.Abs(x[0] - xTrue[0]) + Math.Abs(x[1] - xTrue[1]);
        double neErr = Math.Abs(ne0 - xTrue[0]) + Math.Abs(ne1 - xTrue[1]);
        Assert.True(qrErr <= neErr + 1e-15, $"QR error {qrErr} should not exceed normal-equations error {neErr}");
    }

    [Fact] // GR-460: a column whose squares underflow must still be reflected, not skipped
    public void Solves_a_uniformly_tiny_system_whose_column_squares_underflow()
    {
        // Every entry is 1e-170, so each square (1e-340) underflows to zero and the old routine skipped both
        // reflections, returning (1, 3) for this system; the true solution is (1, 2). The system is perfectly
        // conditioned, only its scale is tiny, so the answer must be accurate to round-off.
        const double s = 1e-170;
        var a = new double[3, 2] { { s, 0 }, { s, s }, { 0, s } };
        var xTrue = new[] { 1.0, 2.0 };
        var b = new double[3];
        for (int i = 0; i < 3; i++) b[i] = (a[i, 0] * xTrue[0]) + (a[i, 1] * xTrue[1]);

        double[] x = HouseholderQr.SolveLeastSquares(a, b);
        Assert.True(Math.Abs(x[0] - 1.0) < 1e-14, $"x0 = {x[0]}");
        Assert.True(Math.Abs(x[1] - 2.0) < 1e-14, $"x1 = {x[1]}");
    }
}
