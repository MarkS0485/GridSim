using GridSim.Core.Diagnostics;

namespace GridSim.Core.Estimation.Dse;

/// <summary>One filtered frequency estimate: the present frequency and its rate of change.</summary>
/// <param name="FreqHz">Estimated frequency (Hz).</param>
/// <param name="RocofHzPerS">Estimated rate of change of frequency (Hz/s).</param>
/// <param name="FreqSigma">1-sigma uncertainty of the frequency estimate (Hz).</param>
/// <param name="RocofSigma">1-sigma uncertainty of the RoCoF estimate (Hz/s).</param>
public sealed record FrequencyRocofEstimate(double FreqHz, double RocofHzPerS, double FreqSigma, double RocofSigma);

/// <summary>
/// A two-state Kalman filter tracking system frequency and its rate of change (RoCoF) as dynamic states from
/// noisy frequency measurements, using the kinematic process model freq' = freq + RoCoF*dt, RoCoF' = RoCoF (a
/// random walk). Estimating RoCoF as a filter state is more reliable than differentiating a PLL frequency -
/// erroneous PLL RoCoF is what mis-tripped ~6,200 MW of solar in the 2016 Blue Cut event - and it degrades
/// gracefully under measurement noise.
///
/// <para>It is a recursive filter with a predict step, so it is gated by <see cref="RestrictionReview"/> and
/// asserted at construction; the emitted (frequency, RoCoF) is the FILTERED PRESENT, never a future-dated
/// claim, and frequency measurements arrive guard-limited upstream.</para>
///
/// <para><b>Experimental</b> (R062, October 2026): Core API and tests only; no CLI verb or production caller. Still
/// needs: a frequency feed (GDA system frequency, or PMU FREQ/DFREQ once the decoder applies FNOM scaling), a
/// CLI or replay surface, and validation of its tuning against a recorded event.</para>
/// </summary>
public sealed class FrequencyRocofFilter
{
    /// <summary>The reviewed method name.</summary>
    public const string MethodName = "FrequencyRocofKf";

    private double _f, _r;                       // state [freq, rocof]
    private double _p00, _p01, _p10, _p11;       // covariance
    private readonly double _freqProcessVar, _rocofProcessVar;

    /// <param name="initialFreqHz">Initial frequency estimate (Hz).</param>
    /// <param name="initialFreqVar">Initial variance on frequency.</param>
    /// <param name="initialRocofVar">Initial variance on RoCoF (diffuse - RoCoF is unknown at start).</param>
    /// <param name="freqProcessStd">Frequency process-noise std per sqrt-second.</param>
    /// <param name="rocofProcessStd">RoCoF process-noise std per sqrt-second.</param>
    public FrequencyRocofFilter(
        double initialFreqHz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz, double initialFreqVar = 1.0, double initialRocofVar = 1.0,
        double freqProcessStd = 0.001, double rocofProcessStd = 0.05)
    {
        RestrictionReview.AssertPermitted(MethodName, usesDynamicsModel: true);
        // GR-519: the filter's numbers must be usable: a finite starting frequency, positive initial variances and finite,
        // non-negative process-noise standard deviations (a NaN or negative value corrupted the covariance for good).
        if (!(double.IsFinite(initialFreqHz) && initialFreqHz > 0))
            throw new ArgumentOutOfRangeException(nameof(initialFreqHz), initialFreqHz, "The initial frequency must be a finite number of Hz above 0.");
        if (!(double.IsFinite(initialFreqVar) && initialFreqVar > 0))
            throw new ArgumentOutOfRangeException(nameof(initialFreqVar), initialFreqVar, "The initial frequency variance must be a finite number above 0.");
        if (!(double.IsFinite(initialRocofVar) && initialRocofVar > 0))
            throw new ArgumentOutOfRangeException(nameof(initialRocofVar), initialRocofVar, "The initial RoCoF variance must be a finite number above 0.");
        if (!(double.IsFinite(freqProcessStd) && freqProcessStd >= 0))
            throw new ArgumentOutOfRangeException(nameof(freqProcessStd), freqProcessStd, "The frequency process-noise std must be a finite number, 0 or above.");
        if (!(double.IsFinite(rocofProcessStd) && rocofProcessStd >= 0))
            throw new ArgumentOutOfRangeException(nameof(rocofProcessStd), rocofProcessStd, "The RoCoF process-noise std must be a finite number, 0 or above.");
        _f = initialFreqHz;
        _r = 0.0;
        _p00 = initialFreqVar; _p11 = initialRocofVar; _p01 = _p10 = 0.0;
        _freqProcessVar = freqProcessStd * freqProcessStd;
        _rocofProcessVar = rocofProcessStd * rocofProcessStd;
    }

    /// <summary>Advance one step: predict through the kinematic model over <paramref name="dtSeconds"/>, then
    /// correct with the measured frequency <paramref name="measuredFreqHz"/> (measurement sigma
    /// <paramref name="sigmaHz"/>). Returns the filtered present frequency and RoCoF.</summary>
    public FrequencyRocofEstimate Step(double measuredFreqHz, double sigmaHz, double dtSeconds = 1.0)
    {
        // R256: dt = 0 (a repeated sample at the same instant) is a pure update - F = I and zero process noise -
        // instead of the old coercion to a full second of drift. A negative or non-finite dt (an out-of-order
        // sample) is an error, not a step.
        if (!double.IsFinite(dtSeconds) || dtSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dtSeconds), dtSeconds,
                "The elapsed time since the previous sample must be a finite number >= 0 (a negative value means the samples are out of order).");
        double dt = dtSeconds;
        // GR-519: a NaN or infinite measurement, or a sigma that is not a finite number above 0, would corrupt the state
        // and covariance for every later step; refused before anything changes.
        if (!double.IsFinite(measuredFreqHz))
            throw new ArgumentOutOfRangeException(nameof(measuredFreqHz), measuredFreqHz, "The measured frequency must be a finite number of Hz.");
        if (!(double.IsFinite(sigmaHz) && sigmaHz > 0))
            throw new ArgumentOutOfRangeException(nameof(sigmaHz), sigmaHz, "The measurement sigma must be a finite number of Hz above 0.");

        // Predict: F = [[1, dt],[0,1]]. x^- = F x.
        _f += _r * dt;
        // P^- = F P F^T + Q. With F = [[1,dt],[0,1]]:
        double p00 = _p00 + dt * (_p10 + _p01) + dt * dt * _p11 + _freqProcessVar * dt;
        double p01 = _p01 + dt * _p11;
        double p10 = _p10 + dt * _p11;
        double p11 = _p11 + _rocofProcessVar * dt;
        _p00 = p00; _p01 = p01; _p10 = p10; _p11 = p11;

        // Update with H = [1, 0], measurement variance r.
        double r = sigmaHz * sigmaHz;
        double s = _p00 + r;                 // innovation variance
        double k0 = _p00 / s;                // Kalman gain
        double k1 = _p10 / s;
        double innov = measuredFreqHz - _f;
        _f += k0 * innov;
        _r += k1 * innov;

        // P = (I - K H) P^-.
        double n00 = (1 - k0) * _p00;
        double n01 = (1 - k0) * _p01;
        double n10 = _p10 - k1 * _p00;
        double n11 = _p11 - k1 * _p01;
        _p00 = n00; _p01 = n01; _p10 = n10; _p11 = n11;

        return new FrequencyRocofEstimate(_f, _r, Math.Sqrt(Math.Max(_p00, 0)), Math.Sqrt(Math.Max(_p11, 0)));
    }
}
