using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics.Planning.Flicker;

/// <summary>
/// The parameters of the IEC 61000-4-15 flickermeter signal chain. IEC 61000-4-15 is a PAID standard: its filter coefficients and its
/// smoothing of the percentiles are not available from a source GridSim can cite, so NONE is built in. Each value is a required input;
/// without them the meter has no result and says <see cref="Flickermeter.AwaitingStandardText"/>. The block structure (what the code
/// does with the numbers) follows the open description of the meter: input adaptor, squaring demodulator, a band-limiting and
/// weighting filter chain, squaring and a first-order sliding mean, and a statistical evaluation of the instantaneous flicker level.
/// </summary>
/// <param name="SampleRateHz">The sample rate of the voltage record (Hz).</param>
/// <param name="NominalRmsVolts">The nominal r.m.s. voltage the input adaptor scales to 1 pu (block 1).</param>
/// <param name="HighPassCornerHz">The first-order high-pass corner that removes the d.c. term after the demodulator (block 3).</param>
/// <param name="LowPassOrder">The order of the Butterworth low-pass of block 3.</param>
/// <param name="LowPassCornerHz">Its corner frequency (Hz).</param>
/// <param name="K">The gain of the weighting filter (block 3).</param>
/// <param name="Lambda">The weighting filter's band-pass damping parameter (rad/s).</param>
/// <param name="Omega1">The band-pass natural frequency (rad/s).</param>
/// <param name="Omega2">The numerator zero (rad/s).</param>
/// <param name="Omega3">The first denominator pole (rad/s).</param>
/// <param name="Omega4">The second denominator pole (rad/s).</param>
/// <param name="SlidingMeanTauSeconds">The time constant of block 4's first-order low-pass (s).</param>
/// <param name="P1sPercentiles">The percentile points (in % of time exceeded) averaged to the smoothed P1s.</param>
/// <param name="P3sPercentiles">The points averaged to the smoothed P3s.</param>
/// <param name="P10sPercentiles">The points averaged to the smoothed P10s.</param>
/// <param name="P50sPercentiles">The points averaged to the smoothed P50s.</param>
public sealed record FlickermeterParameters(
    double SampleRateHz,
    double NominalRmsVolts,
    double HighPassCornerHz,
    int LowPassOrder,
    double LowPassCornerHz,
    double K,
    double Lambda,
    double Omega1,
    double Omega2,
    double Omega3,
    double Omega4,
    double SlidingMeanTauSeconds,
    IReadOnlyList<double> P1sPercentiles,
    IReadOnlyList<double> P3sPercentiles,
    IReadOnlyList<double> P10sPercentiles,
    IReadOnlyList<double> P50sPercentiles);

/// <summary>A flickermeter result: Pst, the five percentile levels it combined, and the verification status of the whole.</summary>
public sealed record FlickermeterResult(
    double Pst,
    double P01,
    double P1s,
    double P3s,
    double P10s,
    double P50s,
    int Samples,
    double PeakInstantaneousFlicker,
    string VerificationStatus);

/// <summary>
/// RC4 R4-21: the IEC 61000-4-15 flickermeter signal chain (structure). Blocks: (1) input adaptor, u1 = u / (sqrt(2) U_nom); (2) squaring
/// demodulator, u2 = u1^2; (3) filters: first-order high-pass (d.c. removal), Butterworth low-pass, then the weighting filter
/// <c>H(s) = [K w1 s / (s^2 + 2 lambda s + w1^2)] [(1 + s/w2) / ((1 + s/w3)(1 + s/w4))]</c>; (4) squaring then the first-order sliding
/// mean <c>y' = (x - y)/tau</c>, giving the instantaneous flicker sensation; (5) the statistical evaluation, here the exact percentiles
/// (the standard's 64-class histogram is a quantisation of the same cumulative curve) and the combination
/// <c>Pst = sqrt(0.0314 P0.1 + 0.0525 P1s + 0.0657 P3s + 0.28 P10s + 0.08 P50s)</c>, the weights of which are published openly
/// (powermonitors.com, "Understanding IEEE/IEC flicker processing"; they agree with the standard as far as that source states).
///
/// <para>The filters are realised by the bilinear transform (s = 2 f_s (1 - z^-1)/(1 + z^-1)) of each analogue section: at the sample rates
/// of a real record (a few kHz) the warping error at the 35 Hz band edge is of the order of one part in 10^4.</para>
///
/// <para><b>Verification: AWAITING STANDARD TEXT.</b> The coefficients, the smoothing combinations and the standard's own test points
/// (sinusoidal and rectangular modulation giving Pst = 1) are in the paid text. The tests check the structure by independent derivation
/// (the filter's frequency response against its analytic transfer function, d.c. rejection, linearity in modulation depth, and the Pst
/// combination on a distribution worked by hand); they do not check the numbers against the standard.</para>
/// </summary>
public static class Flickermeter
{
    public const string AwaitingStandardText = "AWAITING STANDARD TEXT (IEC 61000-4-15 coefficients, smoothing and test points are not freely available)";

    /// <summary>The weights of the Pst combination (see the class remarks for the source).</summary>
    public static readonly double[] PstWeights = [0.0314, 0.0525, 0.0657, 0.28, 0.08];

    /// <summary>One second-order section in direct form I: y[n] = b0 x[n] + b1 x[n-1] + b2 x[n-2] - a1 y[n-1] - a2 y[n-2].</summary>
    internal sealed class Biquad
    {
        public readonly double B0, B1, B2, A1, A2;
        private double _x1, _x2, _y1, _y2;

        public Biquad(double b0, double b1, double b2, double a1, double a2) { B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2; }

        public double Step(double x)
        {
            double y = B0 * x + B1 * _x1 + B2 * _x2 - A1 * _y1 - A2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        /// <summary>The complex frequency response at angular frequency <paramref name="w"/> (rad/s) for sample rate <paramref name="fs"/>.</summary>
        public Complex Response(double w, double fs)
        {
            double th = w / fs;
            Complex z1 = new(DetMath.Cos(th), -DetMath.Sin(th));   // z^-1
            Complex z2 = z1 * z1;
            return (B0 + B1 * z1 + B2 * z2) / (1.0 + A1 * z1 + A2 * z2);
        }
    }

    /// <summary>Bilinear transform of (n2 s^2 + n1 s + n0) / (d2 s^2 + d1 s + d0) at sample rate fs.</summary>
    internal static Biquad Bilinear(double n2, double n1, double n0, double d2, double d1, double d0, double fs)
    {
        double c = 2.0 * fs, c2 = c * c;
        double a0 = d2 * c2 + d1 * c + d0;
        double a1 = 2.0 * (d0 - d2 * c2);
        double a2 = d2 * c2 - d1 * c + d0;
        double b0 = n2 * c2 + n1 * c + n0;
        double b1 = 2.0 * (n0 - n2 * c2);
        double b2 = n2 * c2 - n1 * c + n0;
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>The analogue transfer function of the block 3 weighting filter at angular frequency w (rad/s).</summary>
    public static Complex WeightingAnalogue(FlickermeterParameters p, double w)
    {
        Complex s = new(0.0, w);
        Complex band = p.K * p.Omega1 * s / (s * s + 2.0 * p.Lambda * s + p.Omega1 * p.Omega1);
        Complex shape = (1.0 + s / p.Omega2) / ((1.0 + s / p.Omega3) * (1.0 + s / p.Omega4));
        return band * shape;
    }

    /// <summary>The block 3 weighting filter as digital sections: the band-pass (numerator K w1 s) and the lead-lag (1 + s/w2)/((1 + s/w3)(1 + s/w4)) as two second-order sections.</summary>
    internal static IReadOnlyList<Biquad> WeightingSections(FlickermeterParameters p)
    {
        double fs = p.SampleRateHz;
        Biquad band = Bilinear(0.0, p.K * p.Omega1, 0.0, 1.0, 2.0 * p.Lambda, p.Omega1 * p.Omega1, fs);
        // (1 + s/w2) / ((1 + s/w3)(1 + s/w4)): numerator (1/w2) s + 1; denominator (1/(w3 w4)) s^2 + (1/w3 + 1/w4) s + 1.
        Biquad shape = Bilinear(0.0, 1.0 / p.Omega2, 1.0, 1.0 / (p.Omega3 * p.Omega4), 1.0 / p.Omega3 + 1.0 / p.Omega4, 1.0, fs);
        return [band, shape];
    }

    /// <summary>The block 3 Butterworth low-pass of the given order as biquads (and a first-order section when odd), from the analogue poles.</summary>
    internal static IReadOnlyList<Biquad> ButterworthLowPass(int order, double cornerHz, double fs)
    {
        double wc = 2.0 * Math.PI * cornerHz;
        var sections = new List<Biquad>();
        for (int k = 0; k < order / 2; k++)
        {
            // Pole pair at angle (2k + 1) pi / (2 order) from the negative real axis: s^2 + 2 sin(theta) wc s + wc^2.
            double theta = (2 * k + 1) * Math.PI / (2.0 * order);
            sections.Add(Bilinear(0.0, 0.0, wc * wc, 1.0, 2.0 * DetMath.Sin(theta) * wc, wc * wc, fs));
        }
        if (order % 2 == 1) sections.Add(Bilinear(0.0, 0.0, wc, 0.0, 1.0, wc, fs));
        return sections;
    }

    private static void Validate(FlickermeterParameters p)
    {
        void Pos(double v, string n) { if (!(v > 0.0) || !double.IsFinite(v)) throw new ArgumentException($"Flickermeter parameter {n} must be finite and above 0; got {v:R}."); }
        Pos(p.SampleRateHz, nameof(p.SampleRateHz)); Pos(p.NominalRmsVolts, nameof(p.NominalRmsVolts)); Pos(p.HighPassCornerHz, nameof(p.HighPassCornerHz));
        Pos(p.LowPassCornerHz, nameof(p.LowPassCornerHz)); Pos(p.K, nameof(p.K)); Pos(p.Lambda, nameof(p.Lambda)); Pos(p.Omega1, nameof(p.Omega1));
        Pos(p.Omega2, nameof(p.Omega2)); Pos(p.Omega3, nameof(p.Omega3)); Pos(p.Omega4, nameof(p.Omega4)); Pos(p.SlidingMeanTauSeconds, nameof(p.SlidingMeanTauSeconds));
        if (p.LowPassOrder < 1 || p.LowPassOrder > 12) throw new ArgumentException("The low-pass order must be 1 to 12.");
        if (p.LowPassCornerHz >= p.SampleRateHz / 2.0) throw new ArgumentException("The low-pass corner must be below half the sample rate.");
        foreach ((string name, IReadOnlyList<double> list) in new[] { ("P1sPercentiles", p.P1sPercentiles), ("P3sPercentiles", p.P3sPercentiles), ("P10sPercentiles", p.P10sPercentiles), ("P50sPercentiles", p.P50sPercentiles) })
        {
            if (list is null || list.Count == 0) throw new ArgumentException($"{name} must list at least one percentile point.");
            foreach (double q in list) if (!(q > 0.0 && q < 100.0)) throw new ArgumentException($"{name}: a percentile point must be above 0 and below 100 (% of time exceeded); got {q:R}.");
        }
    }

    /// <summary>
    /// The instantaneous flicker sensation (the block 4 output) of a voltage record. The first <paramref name="settleSeconds"/> are
    /// discarded by the caller (<see cref="Run"/> does so). Deterministic: a fixed sequence of recursive filters.
    /// </summary>
    public static double[] InstantaneousFlicker(FlickermeterParameters p, IReadOnlyList<double> voltage)
    {
        Validate(p);
        ArgumentNullException.ThrowIfNull(voltage);
        double fs = p.SampleRateHz;
        double scale = 1.0 / (Math.Sqrt(2.0) * p.NominalRmsVolts);
        Biquad hp = Bilinear(0.0, 1.0, 0.0, 0.0, 1.0, 2.0 * Math.PI * p.HighPassCornerHz, fs);   // s / (s + w): first order held in a biquad with zero second terms
        IReadOnlyList<Biquad> lp = ButterworthLowPass(p.LowPassOrder, p.LowPassCornerHz, fs);
        IReadOnlyList<Biquad> wt = WeightingSections(p);
        Biquad mean = Bilinear(0.0, 0.0, 1.0, 0.0, p.SlidingMeanTauSeconds, 1.0, fs);            // 1 / (tau s + 1)
        var y = new double[voltage.Count];
        for (int i = 0; i < y.Length; i++)
        {
            double u1 = voltage[i] * scale;
            double u2 = u1 * u1;
            double x = hp.Step(u2);
            foreach (Biquad b in lp) x = b.Step(x);
            foreach (Biquad b in wt) x = b.Step(x);
            y[i] = mean.Step(x * x);
        }
        return y;
    }

    /// <summary>The level exceeded for at most <paramref name="percentExceeded"/> % of the samples (nearest rank on the sorted record).</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentExceeded)
    {
        int n = sortedAscending.Count;
        if (n == 0) throw new ArgumentException("No samples.", nameof(sortedAscending));
        int rank = (int)Math.Ceiling((1.0 - percentExceeded / 100.0) * n);   // 1-based rank from the bottom
        return sortedAscending[Math.Clamp(rank - 1, 0, n - 1)];
    }

    /// <summary>The Pst combination of the five levels with <see cref="PstWeights"/>.</summary>
    public static double CombinePst(double p01, double p1s, double p3s, double p10s, double p50s)
        => Math.Sqrt(PstWeights[0] * p01 + PstWeights[1] * p1s + PstWeights[2] * p3s + PstWeights[3] * p10s + PstWeights[4] * p50s);

    private static double Smoothed(IReadOnlyList<double> sorted, IReadOnlyList<double> points)
    {
        double s = 0.0;
        foreach (double q in points) s += Percentile(sorted, q);
        return s / points.Count;
    }

    /// <summary>
    /// Run the meter on a voltage record (volts, instantaneous) and return Pst over the whole record after discarding the first
    /// <paramref name="settleSeconds"/> (the filters' start-up). The verification status is always <see cref="AwaitingStandardText"/>.
    /// </summary>
    public static FlickermeterResult Run(FlickermeterParameters p, IReadOnlyList<double> voltage, double settleSeconds = 0.0)
    {
        double[] y = InstantaneousFlicker(p, voltage);
        int skip = (int)Math.Round(settleSeconds * p.SampleRateHz);
        if (!(settleSeconds >= 0.0) || skip >= y.Length) throw new ArgumentException("The settling time must leave samples to evaluate.", nameof(settleSeconds));
        double[] use = y[skip..];
        double peak = 0.0;
        foreach (double v in use) if (v > peak) peak = v;
        Array.Sort(use);
        double p01 = Percentile(use, 0.1);
        double p1s = Smoothed(use, p.P1sPercentiles), p3s = Smoothed(use, p.P3sPercentiles), p10s = Smoothed(use, p.P10sPercentiles), p50s = Smoothed(use, p.P50sPercentiles);
        return new FlickermeterResult(CombinePst(p01, p1s, p3s, p10s, p50s), p01, p1s, p3s, p10s, p50s, use.Length, peak, AwaitingStandardText);
    }

    /// <summary>
    /// A test signal: a sinusoidal carrier (r.m.s. <paramref name="nominalRmsVolts"/>, <paramref name="carrierHz"/>) amplitude-modulated by a
    /// sinusoid (<c>modulation = "sine"</c>) or a rectangle (<c>"rectangular"</c>) of relative depth <paramref name="depthFraction"/> (peak
    /// change of the amplitude as a fraction, e.g. 0.01 for 1 %) at <paramref name="modulationHz"/>.
    /// </summary>
    public static double[] ModulatedVoltage(double sampleRateHz, double durationSeconds, double nominalRmsVolts, double carrierHz, string modulation, double modulationHz, double depthFraction)
    {
        if (!(sampleRateHz > 0.0) || !(durationSeconds > 0.0) || !(nominalRmsVolts > 0.0) || !(carrierHz > 0.0) || !(modulationHz > 0.0) || !double.IsFinite(depthFraction) || depthFraction < 0.0)
            throw new ArgumentException("Test signal parameters must be positive and finite (depth not negative).");
        int n = checked((int)Math.Round(sampleRateHz * durationSeconds));
        var u = new double[n];
        double amp = Math.Sqrt(2.0) * nominalRmsVolts;
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRateHz;
            double m = modulation switch
            {
                "sine" => DetMath.Sin(2.0 * Math.PI * modulationHz * t),
                "rectangular" => (t * modulationHz - Math.Floor(t * modulationHz)) < 0.5 ? 1.0 : -1.0,
                _ => throw new ArgumentException("modulation must be sine or rectangular."),
            };
            u[i] = amp * (1.0 + depthFraction * m) * DetMath.Sin(2.0 * Math.PI * carrierHz * t);
        }
        return u;
    }
}
