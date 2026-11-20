using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

/// <summary>
/// Naturally sampled carrier-comparison pulse-width modulation. The carrier is a symmetric triangle of unit amplitude that
/// starts at -1 at time zero (plus the stated <see cref="CarrierPhaseFraction"/> of a period) and peaks at +1 half a period later;
/// a phase's upper switch is on while its reference exceeds the carrier. The carrier phase is an input, never random, so a run is
/// bit-repeatable. The gate is a pure function of time; the nodal solver samples it at the end of each step, so an edge is
/// quantised up to the step grid (error at most one step).
/// <para>Closed-form oracle for the pole voltage (phase to DC mid-point) with <c>m</c> the amplitude of a sinusoidal reference
/// inside +/-1 (linear range): fundamental <c>m Vdc / 2</c>; harmonics at <c>n mf +/- k</c> times the fundamental for <c>n + k</c> odd
/// (double Fourier series of naturally sampled PWM), amplitudes from Bessel functions of the first kind; the tests derive them
/// independently.</para>
/// </summary>
public sealed class SinePwm
{
    /// <summary>Create the modulator. <paramref name="reference"/> is called with (time, phase index 0..2) and returns the reference in -1..1.</summary>
    public SinePwm(double carrierHz, double carrierPhaseFraction, Func<double, int, double> reference)
    {
        if (!(carrierHz > 0) || !double.IsFinite(carrierHz)) throw new ArgumentException("The carrier frequency must be finite and positive.", nameof(carrierHz));
        if (!double.IsFinite(carrierPhaseFraction)) throw new ArgumentException("The carrier phase must be finite.", nameof(carrierPhaseFraction));
        CarrierHz = carrierHz; CarrierPhaseFraction = carrierPhaseFraction;
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
    }

    /// <summary>Carrier frequency, hertz.</summary>
    public double CarrierHz { get; }
    /// <summary>Carrier phase as a fraction of a period (deterministic; 0 starts at the carrier valley).</summary>
    public double CarrierPhaseFraction { get; }
    /// <summary>The per-phase reference.</summary>
    public Func<double, int, double> Reference { get; }

    /// <summary>The triangle carrier at time <paramref name="t"/>, -1..1 (plain arithmetic, no transcendental).</summary>
    public double Carrier(double t)
    {
        double x = t * CarrierHz + CarrierPhaseFraction;
        x -= Math.Floor(x);
        return x < 0.5 ? 4.0 * x - 1.0 : 3.0 - 4.0 * x;
    }

    /// <summary>True when the upper switch of <paramref name="phase"/> is on at time <paramref name="t"/>.</summary>
    public bool UpperOn(double t, int phase) => Reference(t, phase) > Carrier(t);

    /// <summary>A balanced sinusoidal reference <c>m sin(2 pi f t + phi - 120 deg k)</c> for phase k.</summary>
    public static Func<double, int, double> SineReference(double fundamentalHz, double modulationIndex, double phaseRad)
    {
        double w = 2.0 * Math.PI * fundamentalHz;
        return (t, k) => modulationIndex * DetMath.Sin(w * t + phaseRad - k * (2.0 * Math.PI / 3.0));
    }
}
