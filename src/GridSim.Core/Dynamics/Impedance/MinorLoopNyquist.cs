using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Impedance;

/// <summary>The verdict of a minor-loop / generalised-Nyquist test.</summary>
/// <param name="Encirclements">Net encirclements of (-1,0) by the return ratio over the full contour
/// (positive grid + conjugate mirror). With both subsystems individually stable (P = 0) this equals the
/// number of unstable closed-loop modes.</param>
/// <param name="UnstableModeCount">The RHP closed-loop mode count implied by the encirclements
/// (<c>|Encirclements|</c> under the P = 0 assumption).</param>
/// <param name="Stable">No net encirclement of (-1,0).</param>
/// <param name="GainMarginDb">Gain margin (dB) at the phase-crossover (arg L = -180); +inf if none.</param>
/// <param name="PhaseMarginDeg">Phase margin (deg) at the gain-crossover (|L| = 1); +inf if none.</param>
/// <param name="CriticalFreqHz">The phase-crossover frequency (Hz); NaN if none.</param>
/// <param name="MinDistToMinusOne">Minimum <c>|1 + L|</c> over the grid - the robustness margin to
/// instability.</param>
public sealed record NyquistVerdict(
    int Encirclements, int UnstableModeCount, bool Stable,
    double GainMarginDb, double PhaseMarginDeg, double CriticalFreqHz, double MinDistToMinusOne)
{
    /// <summary>GR-096: the largest single phase step (rad, absolute) of the return difference between two
    /// consecutive contour samples. The winding count takes the smallest-angle step between samples, so a step near
    /// pi is ambiguous (the true path may have turned the other way). 0 when the contour has fewer than 2 points.</summary>
    public double MaxPhaseStepRad { get; init; }

    /// <summary>GR-096: true when <see cref="MaxPhaseStepRad"/> exceeds
    /// <see cref="MinorLoopNyquist.MaxTrustedPhaseStepRad"/>: the grid is too coarse somewhere (typically at a sharp
    /// resonance or where the contour passes close to the origin) for the encirclement count to be trusted. The
    /// verdict is still reported; the caller must say it is unreliable and ask for a finer grid.</summary>
    public bool PhaseStepGuardTripped => MaxPhaseStepRad > MinorLoopNyquist.MaxTrustedPhaseStepRad;
}

/// <summary>
/// The minor-loop Nyquist test (SISO Middlebrook) and the generalised (MIMO) Nyquist test.
///
/// <para><b>P = 0.</b> Both the grid and the converter are individually stable in these models (the SRF-PLL
/// closed loop is Hurwitz, with NO free integrator, so the converter admittance has no open right-half-plane
/// pole and no pole on the imaginary axis). The return ratio therefore has <c>P = 0</c> open-loop RHP poles,
/// and by the argument principle the unstable closed-loop mode count equals the net encirclements of (-1,0) -
/// no Nyquist-contour indentation is required. If a future model introduces a legitimate imaginary-axis pole,
/// this class must indent the contour and count P explicitly rather than assume P = 0.</para>
///
/// <para><b>Counting.</b> Both criteria reduce to winding a scalar return-difference around the origin: SISO
/// winds <c>1 + L(jw)</c> with <c>L = Z_grid * Y_conv</c>; MIMO winds the return-difference determinant
/// <c>det(I + T(jw))</c> with <c>T = Y_conv * Z_grid</c> (the generalised Nyquist criterion is the
/// encirclements of the return-difference determinant, equivalent to summing the characteristic loci but free
/// of eigenvalue branch-swap). The negative-frequency arm is the conjugate mirror
/// (<c>L(-jw) = conj(L(jw))</c>).</para>
/// </summary>
public static class MinorLoopNyquist
{
    /// <summary>GR-096: the largest phase step (rad) between consecutive contour samples for which the
    /// encirclement count is trusted: a quarter turn. A smooth return difference sampled finely enough turns well
    /// under this between neighbours; a larger step means the smallest-angle unwrap could have gone the wrong way.</summary>
    public const double MaxTrustedPhaseStepRad = Math.PI / 2.0;

    /// <summary>SISO minor loop: <c>L(jw) = Z_grid(jw) * Y_conv(jw)</c>. Winds <c>1 + L</c>.</summary>
    public static NyquistVerdict Siso(IReadOnlyList<double> fHz, Complex[] zGrid, Complex[] yConv)
    {
        int m = fHz.Count;
        var l = new Complex[m];
        for (int i = 0; i < m; i++) l[i] = zGrid[i] * yConv[i];
        return FromReturnRatio(fHz, l);
    }

    /// <summary>
    /// Sequence-domain SISO minor loop (the scan's SISO criterion). <c>L_pp = Z_pp * Y_pp</c> and
    /// <c>L_nn = Z_nn * Y_nn</c> are the positive- and negative-sequence return ratios at dq perturbation frequency w
    /// (cross-sequence coupling neglected: that is what makes it the scalar criterion). For real-coefficient dq
    /// dynamics <c>L_pp(-jw) = conj(L_nn(jw))</c>, so <c>L_pp</c> is NOT conjugate-symmetric and the plain mirror of
    /// <see cref="Siso"/> would wind the wrong negative arm. The product <c>(1 + L_pp)(1 + L_nn)</c> IS
    /// conjugate-symmetric, so it is wound with the usual mirror; its encirclement count is twice that of
    /// <c>1 + L_pp</c> over the full axis: each closed-loop pp/nn pole pair (one oscillatory mode of the real system)
    /// counts as two right-half-plane eigenvalues, the convention of the MIMO determinant and the modal check. The
    /// margins are those of <c>L_pp</c> on the positive arm; the distance to -1 is the nearer of the two loops.
    /// </summary>
    public static NyquistVerdict SisoSequence(IReadOnlyList<double> fHz, Complex[] zPos, Complex[] yPos, Complex[] zNeg, Complex[] yNeg)
    {
        int m = fHz.Count;
        var lpp = new Complex[m];
        var diff = new Complex[m];
        double minDist = double.PositiveInfinity;
        for (int i = 0; i < m; i++)
        {
            lpp[i] = zPos[i] * yPos[i];
            Complex dp = Complex.One + lpp[i];
            Complex dn = Complex.One + (zNeg[i] * yNeg[i]);
            diff[i] = dp * dn;
            minDist = Math.Min(minDist, Math.Min(dp.Magnitude, dn.Magnitude));
        }
        (int enc, double maxStep) = WindingCount(diff);
        (double gmDb, double pmDeg, double fcrit) = Margins(fHz, lpp);
        return new NyquistVerdict(enc, Math.Abs(enc), enc == 0, gmDb, pmDeg, fcrit, minDist) { MaxPhaseStepRad = maxStep };
    }

    /// <summary>MIMO generalised Nyquist: return ratio <c>T = Y_conv * Z_grid</c> (2x2); winds
    /// <c>det(I + T)</c>.</summary>
    public static NyquistVerdict Mimo(IReadOnlyList<double> fHz, Complex2x2[] zGrid, Complex2x2[] yConv)
    {
        int m = fHz.Count;
        var scalar = new Complex[m];
        for (int i = 0; i < m; i++)
        {
            Complex2x2 t = yConv[i].Multiply(zGrid[i]);
            Complex2x2 ret = Complex2x2.Identity.Add(t);   // I + T
            scalar[i] = ret.Det;                            // det(I + T)
        }
        return FromScalar(fHz, scalar, offsetToMinusOne: false);
    }

    /// <summary>Verdict from a precomputed SISO return ratio L (winds 1 + L). Exposed for the analytic oracle.</summary>
    public static NyquistVerdict FromReturnRatio(IReadOnlyList<double> fHz, Complex[] l)
        => FromScalar(fHz, l, offsetToMinusOne: true);

    // Wind a scalar around the origin. When offsetToMinusOne, the input is L and we wind (1+L); otherwise the
    // input is already the return difference (e.g. det(I+T)) and we wind it directly.
    private static NyquistVerdict FromScalar(IReadOnlyList<double> fHz, Complex[] scalar, bool offsetToMinusOne)
    {
        int m = fHz.Count;
        var w = new Complex[m];        // return difference at each positive frequency
        double minDist = double.PositiveInfinity;
        for (int i = 0; i < m; i++)
        {
            w[i] = offsetToMinusOne ? Complex.One + scalar[i] : scalar[i];
            if (w[i].Magnitude < minDist) minDist = w[i].Magnitude;
        }

        (int enc, double maxStep) = WindingCount(w);
        int unstable = Math.Abs(enc);

        // Margins are defined for the SISO return ratio L; when the caller passed a return difference (MIMO
        // determinant) the L-based margins are not meaningful, so report them only for the SISO path.
        double gmDb = double.PositiveInfinity, pmDeg = double.PositiveInfinity, fcrit = double.NaN;
        if (offsetToMinusOne) (gmDb, pmDeg, fcrit) = Margins(fHz, scalar);

        return new NyquistVerdict(enc, unstable, enc == 0, gmDb, pmDeg, fcrit, minDist) { MaxPhaseStepRad = maxStep };
    }

    // Net encirclements of the origin by the return difference over the FULL contour, using the conjugate
    // mirror for the negative-frequency arm. The ordered sequence in ascending w is:
    //   [-fmax .. -fmin]  = conj(w) reversed,   then   [+fmin .. +fmax] = w.
    // A strictly-/marginally-proper return ratio approaches a constant at infinity, so the closing arc adds no
    // net winding. N = round(total unwrapped d(arg)/2pi). GR-096: also returns the largest absolute step, so the
    // caller can tell when the grid was too coarse for the smallest-angle unwrap to be trusted.
    private static (int Count, double MaxStep) WindingCount(Complex[] wPositive)
    {
        int m = wPositive.Length;
        if (m < 2) return (0, 0.0);

        double total = 0.0, maxStep = 0.0;
        Complex prev = Complex.Conjugate(wPositive[m - 1]);  // start at -fmax
        // Negative arm: fmax down to fmin, conjugated.
        for (int i = m - 1; i >= 0; i--)
        {
            Complex cur = Complex.Conjugate(wPositive[i]);
            double d = DeltaArg(prev, cur);
            total += d;
            maxStep = Math.Max(maxStep, Math.Abs(d));
            prev = cur;
        }
        // Positive arm: fmin up to fmax.
        for (int i = 0; i < m; i++)
        {
            Complex cur = wPositive[i];
            double d = DeltaArg(prev, cur);
            total += d;
            maxStep = Math.Max(maxStep, Math.Abs(d));
            prev = cur;
        }
        return ((int)Math.Round(total / (2.0 * Math.PI)), maxStep);
    }

    // The signed smallest-angle step from a to b (both nonzero), i.e. arg(b) - arg(a) wrapped to (-pi, pi].
    private static double DeltaArg(Complex a, Complex b)
    {
        if (a == Complex.Zero || b == Complex.Zero) return 0.0;
        double d = DetMath.Phase(b) - DetMath.Phase(a);
        while (d > Math.PI) d -= 2.0 * Math.PI;
        while (d <= -Math.PI) d += 2.0 * Math.PI;
        return d;
    }

    // Gain margin (dB) at the phase crossover (unwrapped arg L crosses an odd multiple of -180 deg) and phase
    // margin (deg) at the gain crossover (|L| crosses 1), by linear interpolation on the positive-frequency
    // grid over the CONTINUOUSLY UNWRAPPED phase (so a crossing is unambiguous).
    private static (double GmDb, double PmDeg, double Fcrit) Margins(IReadOnlyList<double> fHz, Complex[] l)
    {
        int m = fHz.Count;
        var phase = new double[m];
        var mag = new double[m];
        phase[0] = DetMath.Phase(l[0]); mag[0] = l[0].Magnitude;
        for (int i = 1; i < m; i++)
        {
            mag[i] = l[i].Magnitude;
            double d = DetMath.Phase(l[i]) - DetMath.Phase(l[i - 1]);
            while (d > Math.PI) d -= 2.0 * Math.PI;
            while (d <= -Math.PI) d += 2.0 * Math.PI;
            phase[i] = phase[i - 1] + d;
        }

        double gmDb = double.PositiveInfinity, pmDeg = double.PositiveInfinity, fcrit = double.NaN;
        for (int i = 1; i < m; i++)
        {
            // Phase crossover: unwrapped phase passes an odd multiple of -pi.
            double target = NearestOddPiCrossing(phase[i - 1], phase[i]);
            if (!double.IsNaN(target))
            {
                double t = (target - phase[i - 1]) / (phase[i] - phase[i - 1]);
                double g = Lerp(mag[i - 1], mag[i], t);
                if (g > 0)
                {
                    double cand = -20.0 * DetMath.Log10(g);
                    if (double.IsPositiveInfinity(gmDb) || Math.Abs(cand) < Math.Abs(gmDb))
                    {
                        gmDb = cand; fcrit = Lerp(fHz[i - 1], fHz[i], t);
                    }
                }
            }
            // Gain crossover: |L| passes through 1.
            double c0 = mag[i - 1] - 1.0, c1 = mag[i] - 1.0;
            if (c0 == 0.0 || c0 * c1 < 0.0)
            {
                double t = c0 == 0.0 ? 0.0 : c0 / (c0 - c1);
                double ph = Lerp(phase[i - 1], phase[i], t);
                // deg from the nearest -180 multiple (phase margin)
                double k = Math.Round((ph + Math.PI) / (2.0 * Math.PI));
                double pm = (ph - ((2.0 * k - 1.0) * Math.PI)) * 180.0 / Math.PI;
                if (double.IsPositiveInfinity(pmDeg) || Math.Abs(pm) < Math.Abs(pmDeg)) pmDeg = pm;
            }
        }
        return (gmDb, pmDeg, fcrit);
    }

    // If the interval [p0,p1] of unwrapped phase crosses an odd multiple of pi (i.e. -180 deg mod 360),
    // return that multiple; else NaN.
    private static double NearestOddPiCrossing(double p0, double p1)
    {
        double lo = Math.Min(p0, p1), hi = Math.Max(p0, p1);
        // odd multiples of pi are (2j+1)*pi
        double jLo = Math.Ceiling((lo / Math.PI - 1.0) / 2.0);
        double cand = (2.0 * jLo + 1.0) * Math.PI;
        return cand >= lo && cand <= hi ? cand : double.NaN;
    }

    private static double Lerp(double a, double b, double t) => a + (t * (b - a));
}
