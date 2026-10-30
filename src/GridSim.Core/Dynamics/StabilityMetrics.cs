namespace GridSim.Core.Dynamics;

/// <summary>
/// The forward "situational awareness" quantities the NESO RP1 control-room panel is built on,
/// derived from the swing equation and the TSGB Minimum Intrinsic Floor (MIF) framework.
///
/// The single most important quantity is <b>purchased time</b>: the seconds a system will ride a
/// credible loss, inside its frequency band, on real stored energy alone. From the swing equation
///   RoCoF = f0 . dP / (2 . E),  and  t = (f_now - f_threshold) / RoCoF
/// so purchased time is (proportional to) the intrinsic energy and inversely proportional to the
/// loss. See <c>Proposals/MIF/0001 - The Minimum Intrinsic Floor (MIF).md</c> s.5, and the
/// largest-credible-loss correction in <c>Policy/0021 - The 1,800 Megawatt Fiction.md</c> s.6.
///
/// These are pure numeric helpers. The generator -> intrinsic/proxy inertia reduction (which needs
/// the synchronous-vs-converter fuel classifier) is done by the caller and fed in as GVA.s.
/// </summary>
public static class StabilityMetrics
{
    /// <summary>
    /// The working low-frequency-demand-disconnection set-point the MIF papers use for
    /// time-to-disconnection: a fall of 0.8 Hz from nominal, i.e. 49.2 Hz. Confirmed as the
    /// corpus convention in <c>Policy/0021 - The 1,800 Megawatt Fiction.md</c> s.6. (The LFDD
    /// stage-1 relay itself arms at <see cref="FrequencyLimits.LfddFirstStageHz"/> = 48.8 Hz.)
    /// </summary>
    public const double LfddWorkingSetpointHz = 49.2;

    /// <summary>
    /// Initial RoCoF magnitude (Hz/s) a system carrying <paramref name="inertiaGvaS"/> of stored
    /// energy would show for a credible loss of <paramref name="credibleLossMw"/>. Wraps
    /// <see cref="InertiaEstimator.PredictRocof"/> (which works in MW.s), returned as a positive rate.
    /// </summary>
    public static double RocofFor(double inertiaGvaS, double credibleLossMw, double nominalHz = FrequencyLimits.NominalHz)
    {
        double eMwS = Math.Max(0.0, inertiaGvaS) * 1000.0;
        return Math.Abs(InertiaEstimator.PredictRocof(eMwS, Math.Abs(credibleLossMw), nominalHz));
    }

    /// <summary>
    /// <b>Purchased time</b> (seconds): how long frequency takes to fall from
    /// <paramref name="currentHz"/> to <paramref name="thresholdHz"/> given
    /// <paramref name="intrinsicGvaS"/> of intrinsic (real, synchronous) stored energy and a
    /// <paramref name="credibleLossMw"/> loss, on the initial-RoCoF (governor-free) approximation the
    /// MIF tables use: t = (f_now - f_threshold) / RoCoF. Returns +infinity for a vanishing loss and
    /// 0 when already at/below the threshold.
    ///
    /// Reproduces the MIF/Policy tables, e.g. at a 3,600 MW loss to 49.2 Hz: 720 GVA.s -> 6.4 s,
    /// 360 -> 3.2 s, 155 -> 1.4 s, 102 -> 0.9 s (Policy 0021 s.6).
    /// </summary>
    public static double PurchasedTimeSeconds(
        double intrinsicGvaS,
        double credibleLossMw,
        double currentHz = FrequencyLimits.NominalHz,
        double thresholdHz = LfddWorkingSetpointHz,
        double nominalHz = FrequencyLimits.NominalHz)
    {
        double margin = currentHz - thresholdHz;
        if (margin <= 0) return 0.0;
        double rocof = RocofFor(intrinsicGvaS, credibleLossMw, nominalHz);
        if (rocof < 1e-9) return double.PositiveInfinity;
        return margin / rocof;
    }

    /// <summary>
    /// The intrinsic-energy floor (GVA.s) required to hold <paramref name="rocofLimitHzPerS"/> for a
    /// <paramref name="credibleLossMw"/> loss - the (static or dynamic) MIF:
    ///   GVA.s = f0 . dP / (2 . RoCoF_limit)   (the S_base terms cancel; MIF 0001 s.5.2 &amp; s.6).
    /// e.g. 3,600 MW at the 0.125 Hz/s protection-design ceiling -> 720 GVA.s; 1,800 MW -> 360.
    /// </summary>
    public static double DynamicMifGvaS(
        double credibleLossMw,
        double rocofLimitHzPerS = FrequencyLimits.RocofLimitLegacyHzPerS,
        double nominalHz = FrequencyLimits.NominalHz)
    {
        if (rocofLimitHzPerS <= 0) return double.PositiveInfinity;
        return nominalHz * Math.Abs(credibleLossMw) / (2.0 * rocofLimitHzPerS) / 1000.0;
    }

    /// <summary>The largest single online unit (MW) - the classic N-1 largest-infeed component of a
    /// credible loss. Caller filters to the units that count (e.g. synchronous, Pg &gt; 0).</summary>
    public static double LargestUnitMw(IEnumerable<double> onlineUnitMw)
    {
        double max = 0.0;
        foreach (double mw in onlineUnitMw)
            if (mw > max) max = mw;
        return max;
    }

    /// <summary>A named cluster of infeeds that can be lost together (shared converter/landfall/route),
    /// e.g. Sellindge = IFA+ElecLink, or the North-Sea corridor = NSL+Viking.</summary>
    public readonly record struct GroupLoss(string Name, double Mw);

    /// <summary>
    /// The largest <b>correlated</b> infeed loss: for each group, the summed live import of its member
    /// infeeds (only positive import counts - an exporting link is not an infeed loss), returning the
    /// worst group. This is the multi-infeed credible loss the 1,800 MW convention never contemplated
    /// (Policy 0021 s.4). Members absent from <paramref name="infeedMwByName"/> are skipped.
    /// </summary>
    public static GroupLoss LargestCorrelatedGroup(
        IEnumerable<(string Name, IReadOnlyList<string> Members)> groups,
        IReadOnlyDictionary<string, double> infeedMwByName)
    {
        var best = new GroupLoss("", 0.0);
        foreach (var (name, members) in groups)
        {
            double sum = 0.0;
            foreach (string m in members)
                if (infeedMwByName.TryGetValue(m, out double mw) && mw > 0) sum += mw;
            if (sum > best.Mw) best = new GroupLoss(name, sum);
        }
        return best;
    }
}
