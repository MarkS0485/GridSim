using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// The standard normal cumulative distribution function and density, for the Gaussian copula of correlated sampling and
/// for the convolution method (R4-09). Deterministic: only IEEE add, multiply, divide and <see cref="DetMath.Exp"/>.
///
/// <para>|z| at most 3: Marsaglia's series <c>Phi(z) = 1/2 + phi(z) (z + z^3/3 + z^5/(3.5) + z^7/(3.5.7) + ...)</c>
/// (all terms of one sign for z &gt; 0, so no cancellation), summed until a term is below 1e-18 of the sum. |z| above 3:
/// the lower tail by the continued fraction <c>Q(x) = phi(x) / (x + 1/(x + 2/(x + 3/(x + ...))))</c> evaluated backwards
/// from a fixed depth of 120, so the tail keeps its relative accuracy. The tests check both against an independent
/// series and against the round trip with <see cref="GaussianInverse"/>.</para>
/// </summary>
public static class GaussianCdf
{
    private const double InvSqrt2Pi = 0.3989422804014327; // 1 / sqrt(2 pi)

    /// <summary>The standard normal density.</summary>
    public static double Pdf(double z) => InvSqrt2Pi * DetMath.Exp(-0.5 * z * z);

    /// <summary>The standard normal CDF; refuses NaN.</summary>
    public static double Cdf(double z)
    {
        if (double.IsNaN(z)) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(z), z, "The argument must not be NaN."));
        if (z > 8.5) return 1.0;
        if (z < -38.5) return 0.0;
        if (Math.Abs(z) <= 3.0)
        {
            double term = z, sum = z, z2 = z * z;
            for (int k = 3; k < 400; k += 2)
            {
                term *= z2 / k;
                sum += term;
                if (Math.Abs(term) < 1e-18 * Math.Abs(sum)) break;
            }
            return 0.5 + Pdf(z) * sum;
        }
        double x = Math.Abs(z);
        double cf = x;
        for (int k = 120; k >= 1; k--) cf = x + k / cf;
        double tail = Pdf(x) / cf;     // Q(x) = 1 - Phi(x)
        return z < 0 ? tail : 1.0 - tail;
    }
}
