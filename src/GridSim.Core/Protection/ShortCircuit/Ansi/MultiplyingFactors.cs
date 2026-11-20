using System;
using GridSim.Core.Numerics;

namespace GridSim.Core.Protection.ShortCircuit.Ansi;

/// <summary>
/// Multiplying factors of the ANSI/IEEE C37 "E/X" short-circuit method, from elementary DC-offset physics.
/// <para>
/// A fault current through a single R-L path from a sinusoidal source is i = Im [ sin(wt + a - phi) - sin(a - phi) e^(-t R/L) ]. Worst case
/// (a - phi = -90 degrees) the DC term starts at the AC peak Im and decays as e^(-w t / (X/R)). With t in cycles n, w t = 2 pi n, so
/// DC(n) / (sqrt(2) I_sym) = e^(-2 pi n / (X/R)) and the rms of the whole waveform is
/// I_sym sqrt(1 + 2 e^(-4 pi n / (X/R))). (The same S factor is published by Siemens TechTopics for breaker applications; see the manual.)
/// </para>
/// <para>
/// What the standard itself tabulates or plots (the local-source curves against NACD, the tables of breaker contact parting time against
/// rated interrupting time, the exact C37.010 expression for a first-cycle X/R above 25) is NOT reproduced: AWAITING STANDARD TEXT.
/// </para>
/// </summary>
public static class MultiplyingFactors
{
    /// <summary>Published first-cycle (momentary) multiplying factor for a first-cycle X/R of 25 or less (EC&amp;M "Short Circuit Current
    /// Duties of Circuit Breakers and Fuses, Part 1": "MF = 1.6 is applicable whenever the first-cycle short circuit X/R ratio is less than or
    /// equal to 25"). It is also what the half-cycle rms formula gives at X/R = 25 (1.598), checked in the tests.</summary>
    public const double MomentaryMfPublished = 1.6;

    /// <summary>The first-cycle X/R up to which the published constant <see cref="MomentaryMfPublished"/> is used.</summary>
    public const double MomentaryMfXrLimit = 25.0;

    /// <summary>Total (AC + DC) rms current over the symmetrical rms current, <c>sqrt(1 + 2 e^(-4 pi n / (X/R)))</c>, at <paramref name="cycles"/>
    /// cycles after inception, worst-case offset, no AC decay. X/R may be +infinity (a purely reactive path).</summary>
    public static double AsymmetricalRmsFactor(double xOverR, double cycles)
    {
        if (!(xOverR > 0.0)) throw new ArgumentOutOfRangeException(nameof(xOverR), "X/R must be above 0");
        if (!(cycles >= 0.0) || !double.IsFinite(cycles)) throw new ArgumentOutOfRangeException(nameof(cycles), "cycles must be finite and 0 or more");
        double e = double.IsPositiveInfinity(xOverR) ? 1.0 : DetMath.Exp(-4.0 * Math.PI * cycles / xOverR);
        return Math.Sqrt(1.0 + 2.0 * e);
    }

    /// <summary>The remote-source interrupting multiplying factor at the contact parting time: the asymmetrical rms factor at
    /// <paramref name="partingCycles"/>. For a fault fed entirely from remote sources (NACD = 0) the AC component does not decay, so this is
    /// the whole factor.</summary>
    public static double RemoteInterruptingMf(double xOverR, double partingCycles) => AsymmetricalRmsFactor(xOverR, partingCycles);

    /// <summary>Momentary (first-cycle, close-and-latch) rms multiplying factor: the published 1.6 for X/R up to 25; above that the half-cycle
    /// rms expression <c>sqrt(1 + 2 e^(-2 pi / (X/R)))</c> (n = 1/2). The exact C37.010 expression above 25 is AWAITING STANDARD TEXT.</summary>
    public static double MomentaryMf(double xOverR) =>
        xOverR <= MomentaryMfXrLimit ? MomentaryMfPublished : AsymmetricalRmsFactor(xOverR, 0.5);

    /// <summary>Half-cycle crest factor over the symmetrical PEAK, <c>1 + e^(-pi / (X/R))</c> (the peak of the offset wave falls near half a
    /// cycle). Multiply by sqrt(2) for the crest over the symmetrical rms current.</summary>
    public static double HalfCyclePeakFactor(double xOverR) =>
        1.0 + (double.IsPositiveInfinity(xOverR) ? 1.0 : DetMath.Exp(-Math.PI / xOverR));
}
