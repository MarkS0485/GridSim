using System.Numerics;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Numerics;

/// <summary>
/// The modified Bessel functions I0, I1, K0 and K1 of a complex argument z with a positive real part, written natively
/// (no third-party library). The cable-constants solver needs them at m r, where m = sqrt(j w mu / rho) has phase
/// pi/4, so every argument it passes lies in the right half plane.
///
/// <para><b>Method.</b> For |z| &lt;= 2 the power series (Abramowitz and Stegun 9.6.10, 9.6.11 and 9.6.13), summed until
/// a term falls below 1e-18 of the sum. For |z| &gt; 2, Steed's continued fraction CF2 in Temme's normalisation gives
/// K0 and K1 (Temme 1975, J. Comput. Phys. 19; the same algorithm as Numerical Recipes' bessik), the modified Lentz
/// form of the continued fraction CF1 gives the ratio I1/I0, and the Wronskian I0 K1 + I1 K0 = 1/z gives I0. Both
/// continued fractions converge for every z with a positive real part. The power series is not used above |z| = 2
/// because, away from the real axis, its terms cancel; the asymptotic expansion is not used at all because for I it
/// omits a term of relative size exp(-2 Re z), which is not negligible at moderate |z|.</para>
///
/// <para><b>Scaling.</b> <see cref="Scaled"/> returns exp(-z) I and exp(z) K, which stay finite however large |z| is
/// (high frequency, thick conductors); the unscaled functions multiply back and overflow only when the true value does.
/// Accuracy, checked against mpmath at 40 digits over |z| from 0.01 to 1e4 and phases from -89 to 89 degrees: under
/// 1e-14 relative. Only IEEE arithmetic, <see cref="Complex"/> arithmetic and <see cref="DetMath"/> are used, so the
/// bits are the same on every platform.</para>
/// </summary>
public static class ComplexBessel
{
    /// <summary>Euler's constant (the one in the series; Wedepohl's gamma is its exponential, 1.7811).</summary>
    public const double EulerGamma = 0.57721566490153286061;

    /// <summary>The |z| up to which the power series is used.</summary>
    public const double SeriesRadius = 2.0;

    private const double Eps = 1.1102230246251565e-16;
    private const double Tiny = 1e-300;
    private const int MaxSeriesTerms = 200;
    private const int MaxCf2Iterations = 100_000;
    private const int MaxCf1Iterations = 10_000_000;

    /// <summary>The four functions at one argument.</summary>
    public readonly record struct Values(Complex I0, Complex I1, Complex K0, Complex K1);

    /// <summary>exp(-z) I0(z), exp(-z) I1(z), exp(z) K0(z) and exp(z) K1(z). Refuses a z without a positive real part.</summary>
    public static Values Scaled(Complex z)
    {
        if (!(double.IsFinite(z.Real) && double.IsFinite(z.Imaginary) && z.Real > 0.0))
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(z),
                "Complex Bessel functions: the argument must be finite with a positive real part."));
        return Complex.Abs(z) <= SeriesRadius ? Series(z) : ContinuedFractions(z);
    }

    /// <summary>I0, I1, K0 and K1 at z, unscaled (the scaled values times exp(z) or exp(-z)).</summary>
    public static Values Unscaled(Complex z)
    {
        Values s = Scaled(z);
        Complex ep = Exp(z), em = Exp(-z);
        return new Values(s.I0 * ep, s.I1 * ep, s.K0 * em, s.K1 * em);
    }

    /// <summary>exp(z) through the deterministic real functions.</summary>
    public static Complex Exp(Complex z) => DetMath.FromPolar(DetMath.Exp(z.Real), z.Imaginary);

    private static Values Series(Complex z)
    {
        Complex q = z * z / 4.0;
        Complex t = Complex.One, u = Complex.One;
        Complex i0 = t, i1s = u, k0s = Complex.Zero;
        // k = 0 term of the K1 sum: psi(1) + psi(2) = -2 gamma + 1.
        Complex k1s = (1.0 - 2.0 * EulerGamma) * u;
        double h = 0.0;
        for (int k = 1; k <= MaxSeriesTerms; k++)
        {
            t = t * q / ((double)k * k);
            u = u * q / ((double)k * (k + 1));
            double hk = h + 1.0 / k;
            double hk1 = hk + 1.0 / (k + 1);
            h = hk;
            i0 += t;
            i1s += u;
            k0s += t * hk;
            k1s += (hk + hk1 - 2.0 * EulerGamma) * u;
            if (Complex.Abs(t) < Eps * 0.01 * Complex.Abs(i0) && Complex.Abs(u) < Eps * 0.01 * Complex.Abs(i1s)) break;
        }
        Complex i1 = z / 2.0 * i1s;
        Complex lz = new(DetMath.Log(Complex.Abs(z) / 2.0), DetMath.Phase(z));
        Complex k0 = -(lz + EulerGamma) * i0 + k0s;
        Complex k1 = Complex.One / z + lz * i1 - z / 4.0 * k1s;
        Complex e = Exp(-z);
        return new Values(i0 * e, i1 * e, k0 / e, k1 / e);
    }

    private static Values ContinuedFractions(Complex z)
    {
        // CF2 (Steed, Temme normalisation), order 0: exp(z) K0 = sqrt(pi / (2 z)) / s, K1 = K0 (z + 1/2 - h) / z.
        Complex b = 2.0 * (1.0 + z);
        Complex d = Complex.One / b;
        Complex h = d, delh = d;
        Complex q1 = Complex.Zero, q2 = Complex.One;
        const double a1 = 0.25;
        Complex q = a1;
        double c = a1, a = -a1;
        Complex s = 1.0 + q * delh;
        bool done = false;
        for (int i = 1; i <= MaxCf2Iterations; i++)
        {
            a -= 2 * i;
            c = -a * c / (i + 1.0);
            Complex qn = (q1 - b * q2) / a;
            q1 = q2;
            q2 = qn;
            q += c * qn;
            b += 2.0;
            d = Complex.One / (b + a * d);
            delh = (b * d - 1.0) * delh;
            h += delh;
            Complex dels = q * delh;
            s += dels;
            if (Complex.Abs(dels) < Eps * Complex.Abs(s)) { done = true; break; }
        }
        if (!done) throw NotConverged("CF2 (K0, K1)");
        h = a1 * h;
        Complex k0 = Complex.Sqrt(Math.PI / (2.0 * z)) / s;
        Complex k1 = k0 * (z + 0.5 - h) / z;

        // CF1, modified Lentz: f = I1 / I0 = 1 / (2/z + 1 / (4/z + 1 / (6/z + ...))).
        Complex f = Tiny, cc = f, dd = Complex.Zero;
        done = false;
        for (int k = 1; k <= MaxCf1Iterations; k++)
        {
            Complex bk = 2.0 * k / z;
            dd = bk + dd;
            if (dd == Complex.Zero) dd = Tiny;
            cc = bk + Complex.One / cc;
            if (cc == Complex.Zero) cc = Tiny;
            dd = Complex.One / dd;
            Complex delta = cc * dd;
            f *= delta;
            if (Complex.Abs(delta - 1.0) < Eps) { done = true; break; }
        }
        if (!done) throw NotConverged("CF1 (I1/I0)");
        // Wronskian I0 K1 + I1 K0 = 1/z; with the scaled K this gives exp(-z) I0 directly.
        Complex i0 = Complex.One / (z * (k1 + f * k0));
        return new Values(i0, f * i0, k0, k1);
    }

    private static InvalidOperationException NotConverged(string which) =>
        DomainRefusal.Mark(new InvalidOperationException($"Complex Bessel functions: the continued fraction {which} did not converge."));
}
