using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// Standard normal deviates by the inverse CDF: Wichura's algorithm AS241 (PPND16), Applied Statistics 37(3), 1988,
/// accurate to about 1 part in 10^16. The coefficients were transcribed from the AS241 implementation in the R project
/// file <c>src/nmath/qnorm.c</c> (read 2026-11-17) and are independently confirmed by the tests (known quantiles and a
/// round trip against a forward CDF built in the test). Only IEEE add, multiply, divide, <c>Math.Sqrt</c> and
/// <see cref="DetMath.Log"/> are used.
///
/// <para>A uniform of exactly 0 (the one end point <see cref="Uniform.FromU64"/> can return) is nudged to 2^-54, half the
/// grid spacing, so the result is finite; 1 is never produced. The mapping is monotone and uses one deviate per
/// uniform, so a quasi-Monte Carlo point set keeps its structure.</para>
/// </summary>
public static class GaussianInverse
{
    private const double Nudge = 1.0 / 18014398509481984.0; // 2^-54

    /// <summary>The standard normal quantile of <paramref name="p"/> in (0, 1); refuses values outside it.</summary>
    public static double Quantile(double p)
    {
        if (!(p > 0.0 && p < 1.0))
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(p), p, "The probability must lie strictly between 0 and 1."));
        double q = p - 0.5;
        double r, val;
        if (Math.Abs(q) <= 0.425)
        {
            r = 0.180625 - q * q;
            return q * (((((((r * 2509.0809287301226727 +
                       33430.575583588128105) * r + 67265.770927008700853) * r +
                     45921.953931549871457) * r + 13731.693765509461125) * r +
                   1971.5909503065514427) * r + 133.14166789178437745) * r +
                 3.387132872796366608)
            / (((((((r * 5226.495278852854561 +
                     28729.085735721942674) * r + 39307.89580009271061) * r +
                   21213.794301586595867) * r + 5394.1960214247511077) * r +
                 687.1870074920579083) * r + 42.313330701600911252) * r + 1.0);
        }
        double lp = DetMath.Log(q > 0 ? 1.0 - p : p);
        r = Math.Sqrt(-lp);
        if (r <= 5.0)
        {
            r -= 1.6;
            val = (((((((r * 7.7454501427834140764e-4 +
                       0.0227238449892691845833) * r + 0.24178072517745061177) *
                     r + 1.27045825245236838258) * r +
                    3.64784832476320460504) * r + 5.7694972214606914055) *
                  r + 4.6303378461565452959) * r +
                 1.42343711074968357734)
                / (((((((r *
                         1.05075007164441684324e-9 + 5.475938084995344946e-4) *
                        r + 0.0151986665636164571966) * r +
                       0.14810397642748007459) * r + 0.68976733498510000455) *
                     r + 1.6763848301838038494) * r +
                    2.05319162663775882187) * r + 1.0);
        }
        else
        {
            r -= 5.0;
            val = (((((((r * 2.01033439929228813265e-7 +
                       2.71155556874348757815e-5) * r +
                      0.0012426609473880784386) * r + 0.026532189526576123093) *
                    r + 0.29656057182850489123) * r +
                   1.7848265399172913358) * r + 5.4637849111641143699) *
                 r + 6.6579046435011037772)
                / (((((((r *
                         2.04426310338993978564e-15 + 1.4215117583164458887e-7) *
                        r + 1.8463183175100546818e-5) * r +
                       7.868691311456132591e-4) * r + 0.0148753612908506148525)
                     * r + 0.13692988092273580531) * r +
                    0.59983220655588793769) * r + 1.0);
        }
        return q < 0 ? -val : val;
    }

    /// <summary>The deviate for a uniform in [0, 1): 0 is nudged to 2^-54.</summary>
    public static double FromUniform(double u) => Quantile(u == 0.0 ? Nudge : u);
}
