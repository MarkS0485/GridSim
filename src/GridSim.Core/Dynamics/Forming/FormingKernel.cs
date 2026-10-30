using System.Runtime.CompilerServices;

namespace GridSim.Core.Dynamics.Forming;

/// <summary>
/// The single source of truth for the grid-forming (voltage-source) swing step - the forming twin of
/// <see cref="Dynamics.SwingKernel"/>. A grid-forming converter imposes its internal angle on the network,
/// so the grid frequency <c>f</c> IS the converter's virtual-rotor frequency; the unified forming swing
/// over the real synchronous COI mass <c>E</c> and the virtual former <c>Ev</c> (both locked to the same
/// <c>f</c>) is
/// <code>
///   (2*(E+Ev)/f0) * df/dt = pForcing - Dtot*(f - f0)
/// </code>
/// with <c>pForcing</c> = every imbalance term NOT proportional to <c>(f-f0)</c> and <c>Dtot</c> = load
/// damping + former damping Dv. The <c>(f-f0)</c> damping term is integrated implicitly (A-stable, closed
/// form); the explicitly-integrated proportional responders (governor / FFR / services) remain in
/// <c>pForcing</c>, so callers bound the internal step by <see cref="SubStepCount"/> to keep fast,
/// high-gain responders inside their stability region at small <c>E+Ev</c>.
/// </summary>
public static class FormingKernel
{
    /// <summary>Largest internal integration step (s), matching the FrequencyIntegrator family.</summary>
    public const double MaxInternalDt = 0.02;

    /// <summary>Bound on the per-substep dimensionless gain a = h*f0/(2*(E+Ev)); shrinks h as E+Ev -> 0
    /// so the explicit proportional responders stay inside their stability region at tiny inertia.</summary>
    public const double MaxStepGain = 0.02;

    /// <summary>Per-call sub-step ceiling (cost bound for a huge outer dt or a tiny E+Ev).</summary>
    public const int MaxSubSteps = 4000;

    /// <summary>
    /// Semi-implicit (A-stable on the damping term) forming step.
    /// <paramref name="eForming"/> = E_real + E_virtual (MW.s, &gt; 0, guaranteed by the caller via
    /// <see cref="EffectiveFormingInertiaMwS"/>). <paramref name="pForcing"/> = all imbalance terms NOT
    /// proportional to (f-f0). <paramref name="dampTotalMwPerHz"/> = load damping + virtual former damping Dv.
    /// Returns the advanced frequency and the start-of-step RoCoF (the existing reporting convention).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double fNext, double rocofReported) SemiImplicitStep(
        double f, double f0, double eForming, double pForcing, double dampTotalMwPerHz, double dt)
    {
        double df = f - f0;
        double gain = f0 / (2.0 * eForming);
        double rocof = gain * (pForcing - dampTotalMwPerHz * df);   // start-of-step, matches today's convention
        double a = dt * gain;
        double fNext = (f + a * (pForcing + dampTotalMwPerHz * f0)) / (1.0 + a * dampTotalMwPerHz);
        return (fNext, rocof);
    }

    /// <summary>
    /// The forming denominator. Fails loud instead of the fake <c>Math.Max(1,E)</c> floor: a grid-forming
    /// request with zero real AND zero virtual inertia forms nothing. Ev is virtual CONTROL inertia and is
    /// NEVER real stored (1/2)Jw^2 - it lives here as a numeric denominator convenience only.
    /// </summary>
    public static double EffectiveFormingInertiaMwS(double eRealMwS, double eVirtualMwS)
    {
        double e = eRealMwS + eVirtualMwS;
        if (!(e > 0.0))
            throw new InvalidOperationException(
                "Grid-forming requested with zero real and zero virtual inertia - nothing forms the frequency. " +
                "Set DynamicsConfig.VirtualInertiaMwS > 0 (or --forming-inertia).");
        return e;
    }

    /// <summary>
    /// Number of internal sub-steps for an outer step <paramref name="dt"/>: bounded both by
    /// <see cref="MaxInternalDt"/> and by the small-inertia gain limit <see cref="MaxStepGain"/>. Mirrors the
    /// FrequencyIntegrator sub-stepping so the batch forming path (which has none of its own) stays stable for fast
    /// responders at small E+Ev.
    /// <para>The count is never silently clamped below what the gain bound needs. It used to be capped at
    /// <see cref="MaxSubSteps"/> with no word, so a tiny E+Ev or a long outer step ran with a per-substep gain far above
    /// <see cref="MaxStepGain"/> (750 times it for Ev = 0.5 MW.s at a 20-minute step), exactly the regime the bound exists
    /// to keep the explicit responders out of. Needing more than <see cref="MaxSubSteps"/> now throws, naming the
    /// inertia and the step, so the caller shortens its step or the case gets more inertia.</para>
    /// </summary>
    public static int SubStepCount(double dt, double f0, double eForming)
    {
        double gain = f0 / (2.0 * eForming);
        double hMax = gain > 0.0 ? Math.Min(MaxInternalDt, MaxStepGain / gain) : MaxInternalDt;
        double needed = Math.Ceiling(dt / Math.Min(dt, hMax));
        if (!(needed <= MaxSubSteps))
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Grid-forming step: an outer step of {dt:R} s with E + Ev = {eForming:R} MW.s needs {needed:R} internal " +
                $"sub-steps to keep the explicit responders stable (gain bound {MaxStepGain:R}), above the {MaxSubSteps} cap. " +
                $"Use a shorter step or more (real or virtual) inertia."));
        return Math.Max(1, (int)needed);
    }
}
