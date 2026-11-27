using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Lines.Cables;

/// <summary>
/// The earth-return impedance between two buried conductors in homogeneous earth (resistivity rho_e, mu_r = 1,
/// displacement currents neglected), per metre, with m_e = sqrt(j w mu0 / rho_e), depths h_i and h_j, horizontal
/// separation x, direct distance d = sqrt(x^2 + (h_i - h_j)^2) (the cable's outer radius for a self term) and image
/// distance D = sqrt(x^2 + (h_i + h_j)^2).
/// <list type="bullet">
/// <item>Pollaczek (1931), exact: z = j w mu0 / (2 pi) [K0(m_e d) - K0(m_e D) + 2 J], with
/// J = integral_0^inf exp(-(h_i + h_j) sqrt(l^2 + m_e^2)) cos(x l) / (l + sqrt(l^2 + m_e^2)) dl.</item>
/// <item>Wedepohl and Wilcox (Proc. IEE 120(2), 1973), the small-argument form of the same integral:
/// z = j w mu0 / (2 pi) [-ln(gamma m_e d / 2) + 1/2 - (2/3) m_e (h_i + h_j)], gamma = exp(Euler's constant) = 1.7811.
/// For a self term this is the familiar -(4/3) m_e h.</item>
/// </list>
/// The Wedepohl form's error against the integral grows like the square of u = |m_e| max(d, h_i + h_j): measured
/// about 3e-4 relative at u = 0.056 and 1.5e-2 at u = 0.18. <see cref="WedepohlLimit"/> is where the solver warns.
///
/// <para>Quadrature of J: composite 20-point Gauss-Legendre (the <see cref="EarthReturn"/> rule and nodes) on
/// [0, 45 / (h_i + h_j)], the first panel 1e-4 min(|m_e|, 1/(h_i + h_j)) wide and each next one twice as wide, every panel
/// split so that x times its width stays within 2 radians. The integrand decays at least like exp(-(h_i + h_j) l), so
/// the tail beyond the upper limit is below exp(-45) of the integral (<see cref="EarthReturn.TailBound"/>). A fixed
/// panel sequence and IEEE arithmetic make the result bit-repeatable. Checked against mpmath's adaptive quadrature at 30
/// digits for 50 Hz to 100 kHz, 1 to 1e4 ohm m, depth sums 1 to 4 m and separations 0 to 5 m: under 2e-15 relative.</para>
/// </summary>
public static class BuriedEarthReturn
{
    /// <summary>exp(Euler's constant), Wedepohl's gamma.</summary>
    public const double WedepohlGamma = 1.781072417990198;

    /// <summary>The value of |m_e| max(d, h_i + h_j) above which the solver warns that the Wedepohl form is outside its range (error above about 1e-3).</summary>
    public const double WedepohlLimit = 0.05;

    private const int Order = 20;
    private static readonly Lazy<(double[] X, double[] W)> Gl = new(() => GaussLegendre20());

    /// <summary>The earth wave number m_e = sqrt(j w mu0 / rho_e).</summary>
    public static Complex WaveNumber(double omega, double earthResistivityOhmM) =>
        Complex.Sqrt(new Complex(0.0, omega * EarthReturn.Mu0 / earthResistivityOhmM));

    /// <summary>|m_e| max(d, h_i + h_j), the argument that decides whether the Wedepohl form applies.</summary>
    public static double WedepohlArgument(double omega, double earthResistivityOhmM, double sumDepthM, double directDistanceM) =>
        Complex.Abs(WaveNumber(omega, earthResistivityOhmM)) * Math.Max(sumDepthM, directDistanceM);

    /// <summary>
    /// The earth-return impedance per metre between conductors at depths <paramref name="depthI"/> and <paramref name="depthJ"/>,
    /// <paramref name="xDistance"/> apart horizontally; <paramref name="directDistance"/> is their distance, or the cable's
    /// outer radius for a self term (pass xDistance 0).
    /// </summary>
    public static Complex ImpedancePerMetre(CableEarthMethod method, double omega, double earthResistivityOhmM,
        double depthI, double depthJ, double xDistance, double directDistance)
    {
        Complex me = WaveNumber(omega, earthResistivityOhmM);
        double hs = depthI + depthJ;
        Complex k = new(0.0, omega * EarthReturn.Mu0 / (2.0 * Math.PI));
        if (method == CableEarthMethod.Wedepohl)
        {
            Complex g = WedepohlGamma * me * directDistance / 2.0;
            Complex lg = new(DetMath.Log(Complex.Abs(g)), DetMath.Phase(g));
            return k * (-lg + 0.5 - 2.0 / 3.0 * me * hs);
        }
        double image = Math.Sqrt(xDistance * xDistance + hs * hs);
        Complex k0d = ComplexBessel.Unscaled(me * directDistance).K0;
        Complex k0D = ComplexBessel.Unscaled(me * image).K0;
        return k * (k0d - k0D + 2.0 * Integral(hs, xDistance, omega * EarthReturn.Mu0 / earthResistivityOhmM));
    }

    /// <summary>The Pollaczek integral J (dimensionless): sumDepthM = h_i + h_j, xM the horizontal spacing, alpha = w mu0 / rho_e (1/m^2).</summary>
    public static Complex Integral(double sumDepthM, double xM, double alpha)
    {
        double hi = 45.0 / sumDepthM;
        double s0 = Math.Min(Math.Sqrt(alpha), 1.0 / sumDepthM) * 1e-4;
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
                    // Principal sqrt(l^2 + j alpha) (= sqrt(l^2 + m_e^2)), by hand: modulus, then real and imaginary parts.
                    double mag = Math.Sqrt(l2 * l2 + alpha * alpha);
                    double re = Math.Sqrt(0.5 * (mag + l2));
                    double im = alpha / (2.0 * re);
                    var root = new Complex(re, im);
                    Complex ex = DetMath.FromPolar(DetMath.Exp(-sumDepthM * re), -sumDepthM * im);
                    double c = ax == 0.0 ? 1.0 : DetMath.Cos(ax * l);
                    sum += ex * c / (l + root) * nw[j];
                }
                total += sum * half;
            }
            lo = up;
            width *= 2.0;
        }
        return total;
    }

    // The same Newton construction of the 20-point Gauss-Legendre nodes and weights as EarthReturn.
    private static (double[], double[]) GaussLegendre20()
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
