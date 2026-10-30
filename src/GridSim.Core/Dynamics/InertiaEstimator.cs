namespace GridSim.Core.Dynamics;

/// <summary>
/// The inverse of the swing equation: infer how much spinning energy the system was
/// carrying from how fast frequency moved after a known power step.
///
///   df/dt = f / (2E)  deltaP      =>      E = f  |deltaP| / (2  |RoCoF|)
///
/// This is what NESO does in reverse after every large loss  -  the measured initial RoCoF
/// plus the known lost infeed gives the system inertia at that instant (the GB "inertia
/// monitoring" the RoCoF work targets). Because the very first RoCoF sample is taken before
/// governors or fast response have moved, the estimate is clean: at t=0 the only imbalance
/// is deltaP itself.
/// </summary>
public static class InertiaEstimator
{
    /// <summary>
    /// System spinning energy E (MWs) from a known power step and the measured initial RoCoF.
    /// <paramref name="deltaPMw"/> is the size of the step (sign ignored); <paramref name="rocofHzPerS"/>
    /// is the initial rate of change (sign ignored). Returns + if RoCoF is ~0 (infinite inertia).
    /// </summary>
    public static double EstimateInertiaMwS(double deltaPMw, double rocofHzPerS, double nominalHz = FrequencyLimits.NominalHz)
    {
        double rocof = Math.Abs(rocofHzPerS);
        if (rocof < 1e-9) return double.PositiveInfinity;
        return nominalHz * Math.Abs(deltaPMw) / (2.0 * rocof);
    }

    /// <summary>System inertia in GVAs (E / 1000)  -  the unit the GB inertia figures are quoted in.</summary>
    public static double EstimateInertiaGvaS(double deltaPMw, double rocofHzPerS, double nominalHz = FrequencyLimits.NominalHz) =>
        EstimateInertiaMwS(deltaPMw, rocofHzPerS, nominalHz) / 1000.0;

    /// <summary>
    /// Estimate spinning energy (MWs) from a simulated/observed <see cref="FrequencyResponse"/> and the
    /// known lost infeed, using its initial RoCoF. Inverts <see cref="FrequencyDynamics.Simulate"/>.
    /// </summary>
    public static double EstimateInertiaMwS(FrequencyResponse response, double deltaPMw, double nominalHz = FrequencyLimits.NominalHz) =>
        EstimateInertiaMwS(deltaPMw, response.InitialRocofHzPerS, nominalHz);

    /// <summary>
    /// Equivalent system inertia constant H (seconds) on a given MVA base: H = E / S_base. A base that is not a finite
    /// number of MVA above 0 is refused (bug hunt 2026-10-29, finding 19): it used to return NaN for a base of 0 or below
    /// (and NaN for a NaN base, which passed the old "&lt;= 0" test), and a NaN H silently poisons whatever is built on it.
    /// </summary>
    public static double EstimateHSeconds(double deltaPMw, double rocofHzPerS, double baseMva, double nominalHz = FrequencyLimits.NominalHz)
    {
        if (!(baseMva > 0) || !double.IsFinite(baseMva))
            throw new ArgumentOutOfRangeException(nameof(baseMva), baseMva,
                "The MVA base of an inertia constant H = E / S_base must be a finite number above 0.");
        return EstimateInertiaMwS(deltaPMw, rocofHzPerS, nominalHz) / baseMva;
    }

    /// <summary>
    /// Forward check: the initial RoCoF (Hz/s) a system with spinning energy <paramref name="inertiaMwS"/>
    /// would show for a step <paramref name="deltaPMw"/> (negative deltaP => negative RoCoF).
    /// </summary>
    public static double PredictRocof(double inertiaMwS, double deltaPMw, double nominalHz = FrequencyLimits.NominalHz)
    {
        double e = Math.Max(SwingKernel.MinInertiaMwS, inertiaMwS);
        return nominalHz / (2.0 * e) * deltaPMw;
    }
}
