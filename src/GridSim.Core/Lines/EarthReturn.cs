using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Lines;

/// <summary>
/// The earth-return term of the series impedance of overhead conductors over a uniform earth, per metre, as the exact
/// integral (Pollaczek 1926; Carson series is a power-series evaluation of the same integral):
/// <c>Z_ik = j w mu0 / (2 pi) ln(D_ik / d_ik) + j w mu0 / pi * I</c>, with
/// <c>I = integral_0^inf exp(-(h_i + h_k) l) cos(x_ik l) / (l + sqrt(l^2 + j w mu0 / rho)) dl</c>.
/// D is the distance to the image, d the direct distance. As rho tends to 0 the integral vanishes like sqrt(rho)
/// (perfect ground); at low frequency over high resistivity and low height its real part tends to w mu0 / 8 (the classical
/// pi^2 f 1e-4 ohm/km earth resistance). Both limits are checked in the tests.
///
/// <para>Quadrature: composite 20-point Gauss-Legendre on [0, 45/H] (H = h_i + h_k; the integrand is below exp(-45), about
/// 3e-20 relative, beyond it, which is the reported truncation), the first panel [0, s0] then panels doubling in width, each
/// split further so that x times its width stays within 2 radians. Only IEEE arithmetic and <see cref="DetMath"/> are
/// used, so the result is bit-repeatable. The series forms (carson-series, carson-modified) are not implemented.</para>
/// </summary>
public static class EarthReturn
{
    /// <summary>Permeability of free space, 4 pi 1e-7 H/m.</summary>
    public const double Mu0 = 4.0e-7 * Math.PI;
    /// <summary>Permittivity of free space, F/m.</summary>
    public const double Eps0 = 8.8541878128e-12;
    /// <summary>The relative truncation bound of the integral upper limit (exp(-45)).</summary>
    public const double TailBound = 2.9e-20;

    private const int Order = 20;
    private static readonly Lazy<(double[] X, double[] W)> Gl = new(BuildGaussLegendre);

    /// <summary>The integral I (dimensionless): alpha = w mu0 / rho in 1/m^2, sumHeightM = h_i + h_k, xM the horizontal spacing.</summary>
    public static Complex Integral(double sumHeightM, double xM, double alpha)
    {
        double hi = 45.0 / sumHeightM;
        double s0 = Math.Min(Math.Sqrt(alpha), 1.0 / sumHeightM) * 1e-4;
        var (nx, nw) = Gl.Value;
        Complex total = Complex.Zero;
        double lo = 0.0, width = s0;
        double ax = Math.Abs(xM);
        while (lo < hi)
        {
            double up = Math.Min(lo + width, hi);
            int split = ax * (up - lo) > 2.0 ? (int)Math.Ceiling(ax * (up - lo) / 2.0) : 1;
            double w = (up - lo) / split;
            for (int s = 0; s < split; s++)
            {
                double a = lo + s * w, b = a + w;
                double half = 0.5 * (b - a), mid = 0.5 * (a + b);
                Complex sum = Complex.Zero;
                for (int j = 0; j < Order; j++)
                {
                    double l = mid + half * nx[j];
                    double l2 = l * l;
                    // Principal sqrt(l^2 + j alpha), by hand: modulus, then real and imaginary parts.
                    double mag = Math.Sqrt(l2 * l2 + alpha * alpha);
                    double re = Math.Sqrt(0.5 * (mag + l2));
                    double im = alpha / (2.0 * re);
                    var den = new Complex(l + re, im);
                    double f = DetMath.Exp(-sumHeightM * l) * (ax == 0.0 ? 1.0 : DetMath.Cos(ax * l));
                    sum += f / den * nw[j];
                }
                total += sum * half;
            }
            lo = up;
            width *= 2.0;
        }
        return total;
    }

    /// <summary>
    /// Series impedance per metre between two conductors. For a self term pass xDistance 0 and directDistance = GMR; for a
    /// mutual term the direct distance between them.
    /// </summary>
    public static Complex ImpedancePerMetre(EarthReturnMethod method, double omega, double rho,
        double heightI, double heightK, double xDistance, double directDistance)
    {
        double sy = heightI + heightK;
        double image = Math.Sqrt(xDistance * xDistance + sy * sy);
        double k = omega * Mu0;
        Complex z = new(0.0, k / (2.0 * Math.PI) * DetMath.Log(image / directDistance));
        if (method == EarthReturnMethod.EarthIntegral)
            z += Complex.ImaginaryOne * (k / Math.PI) * Integral(sy, xDistance, k / rho);
        return z;
    }

    private static (double[], double[]) BuildGaussLegendre()
    {
        var x = new double[Order]; var w = new double[Order];
        for (int i = 0; i < Order; i++)
        {
            double z = DetMath.Cos(Math.PI * (i + 0.75) / (Order + 0.5));
            double pp = 1.0;
            for (int it = 0; it < 100; it++)
            {
                double p1 = 1.0, p2 = 0.0;
                for (int j = 1; j <= Order; j++)
                {
                    double p3 = p2; p2 = p1;
                    p1 = ((2.0 * j - 1.0) * z * p2 - (j - 1.0) * p3) / j;
                }
                pp = Order * (z * p1 - p2) / (z * z - 1.0);
                double dz = p1 / pp;
                z -= dz;
                if (Math.Abs(dz) < 1e-16) break;
            }
            x[i] = -z; w[i] = 2.0 / ((1.0 - z * z) * pp * pp);
        }
        return (x, w);
    }
}
