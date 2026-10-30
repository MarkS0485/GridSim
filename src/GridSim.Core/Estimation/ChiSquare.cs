namespace GridSim.Core.Estimation;

/// <summary>
/// Dependency-free chi-square quantiles for the bad-data consistency test. Uses the
/// Wilson-Hilferty cube approximation chi-square(k,p) ~ k*(1 - 2/(9k) + z_p*sqrt(2/(9k)))^3 with an
/// Acklam-style rational approximation for the standard-normal quantile z_p. Accurate to well
/// under 1% for k >= 3 - ample for a detection gate. At k = 1 and k = 2, where Wilson-Hilferty sags
/// (2.5 % and 0.9 % low at p = 0.95) and which reduced-redundancy sets such as `--prior solve` meet,
/// the exact closed forms are used instead (GR-539): chi-square(1,p) = z_((1+p)/2)^2 and
/// chi-square(2,p) = -2 ln(1 - p).
/// Deterministic by construction (pure arithmetic, no tables loaded at runtime).
/// </summary>
internal static class ChiSquare
{
    /// <summary>The p-quantile of the chi-square distribution with <paramref name="dof"/> degrees of
    /// freedom; NaN when dof <= 0 (no redundancy - the test is meaningless). Exact at 1 and 2 degrees of freedom; from 3
    /// on it is the Wilson-Hilferty cube approximation. Its relative error against the exact quantile (bug hunt
    /// 2026-10-29, finding 74, measured): -0.51 %, -0.33 %, -0.24 % at p = 0.95 and +0.21 %, +0.22 %, +0.20 % at p = 0.99,
    /// for 3, 4 and 5 degrees of freedom (7.7750 against 7.8147 at 3 and 0.95), falling as the degrees of freedom grow.
    /// Fine for a bad-data verdict; a threshold compared with a printed table differs at the third significant digit.</summary>
    public static double Quantile(int dof, double p)
    {
        if (dof <= 0 || p <= 0 || p >= 1) return double.NaN;
        // GR-539: exact quantiles at one and two degrees of freedom. chi-square(1) is the square of a standard
        // normal, so its p-quantile is the square of the normal's (1 + p) / 2 quantile (accurate to Acklam's
        // 1.15e-9 relative). chi-square(2) is exponential with mean 2, so its quantile is -2 ln(1 - p), through
        // the deterministic log. Wilson-Hilferty was 2.5 % and 0.9 % low there at p = 0.95.
        if (dof == 1)
        {
            double z = NormalQuantile(0.5 + (0.5 * p));
            return z * z;
        }
        if (dof == 2) return -2.0 * DetMath.Log(1.0 - p);
        double t = 2.0 / (9.0 * dof);
        double cube = 1.0 - t + NormalQuantile(p) * Math.Sqrt(t);
        return dof * cube * cube * cube;
    }

    /// <summary>Standard-normal quantile (inverse CDF), Acklam's rational approximation
    /// (relative error &lt; 1.15e-9 across (0,1)).</summary>
    public static double NormalQuantile(double p)
    {
        // Coefficients from Peter Acklam's algorithm (2003), a public-domain staple.
        ReadOnlySpan<double> a = stackalloc double[]
        {
            -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
            1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
        };
        ReadOnlySpan<double> b = stackalloc double[]
        {
            -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
            6.680131188771972e+01, -1.328068155288572e+01,
        };
        ReadOnlySpan<double> c = stackalloc double[]
        {
            -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
            -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
        };
        ReadOnlySpan<double> d = stackalloc double[]
        {
            7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
            3.754408661907416e+00,
        };
        const double pLow = 0.02425;

        if (p < pLow)
        {
            double q = Math.Sqrt(-2 * DetMath.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                 / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - pLow)
        {
            double q = Math.Sqrt(-2 * DetMath.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                  / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        double r = p - 0.5;
        double s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r
             / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
