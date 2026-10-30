using System.Globalization;

namespace GridSim.Core.Distribution;

/// <summary>
/// N264 (October 2026): the measurement-sigma check shared by the three-phase estimators
/// (<see cref="ThreePhaseWlsEstimator"/> and <see cref="BranchCurrentDsse"/>).
/// <para>Both weight a measurement by 1 / sigma^2. A sigma of 0 gave an infinite weight (and NaN everywhere it met a
/// zero), a negative sigma a valid-looking positive weight from a meaningless input, a NaN sigma a NaN weight, and an
/// infinite one a weight of 0 (the measurement silently ignored). The JSON loader (MultiPhaseJsonLoader) already refuses
/// a sigmaPu that is not above 0, so only Core callers could pass such a value; they are now refused here too, by
/// name, before any arithmetic. A sigma so small or so large that 1 / sigma^2 is not a finite positive number (below
/// about 1e-154 or above about 1e154) is refused for the same reason: its weight would be infinite or 0.</para>
/// <para>Every sigma that worked before is accepted unchanged and nothing is computed differently, so every accepted
/// estimate is bit for bit what it was.</para>
/// </summary>
internal static class ThreePhaseSigmaChecks
{
    /// <summary>The most offending measurements named in one refusal; the count of the rest is given.</summary>
    private const int MaxListed = 20;

    /// <summary>True when <paramref name="sigma"/> is a usable measurement sigma: finite, above 0, and with a finite,
    /// positive weight 1 / sigma^2.</summary>
    internal static bool IsUsable(double sigma)
    {
        if (!double.IsFinite(sigma) || !(sigma > 0.0)) return false;
        double w = 1.0 / (sigma * sigma);
        return double.IsFinite(w) && w > 0.0;
    }

    /// <summary>Refuse, listing every offender, when any voltage-magnitude or current sigma is not usable
    /// (<see cref="IsUsable"/>). <paramref name="estimator"/> names the caller in the message.</summary>
    internal static void Require(string estimator, IReadOnlyList<ThreePhaseVMag>? vmags, IReadOnlyList<ThreePhaseCurrent>? currents)
    {
        // GR-583: a phase index outside 0..2 is refused by name here, before any estimator indexes a phase array with
        // it (it used to surface as an IndexOutOfRangeException, or as a raw number in this method's own message).
        var badPhase = new List<string>();
        if (vmags is not null)
            for (int i = 0; i < vmags.Count; i++)
                if (vmags[i].Phase is < 0 or > 2)
                    badPhase.Add($"voltage magnitude #{i + 1} (bus {vmags[i].BusId}) phase {vmags[i].Phase.ToString(CultureInfo.InvariantCulture)}");
        if (currents is not null)
            for (int i = 0; i < currents.Count; i++)
                if (currents[i].Phase is < 0 or > 2)
                    badPhase.Add($"current #{i + 1} (branch {currents[i].FromBusId}->{currents[i].ToBusId}) phase {currents[i].Phase.ToString(CultureInfo.InvariantCulture)}");
        if (badPhase.Count > 0)
            throw new ArgumentException(
                $"{estimator}: {badPhase.Count} measurement(s) name a phase that is not 0, 1 or 2 (a, b, c): " +
                $"{string.Join("; ", badPhase.Take(MaxListed))}" + (badPhase.Count > MaxListed ? $"; and {badPhase.Count - MaxListed} more." : "."));

        var bad = new List<string>();
        if (vmags is not null)
            for (int i = 0; i < vmags.Count; i++)
                if (!IsUsable(vmags[i].Sigma))
                    bad.Add($"voltage magnitude #{i + 1} (bus {vmags[i].BusId}, phase {Phase(vmags[i].Phase)}) sigma {Fmt(vmags[i].Sigma)}");
        if (currents is not null)
            for (int i = 0; i < currents.Count; i++)
                if (!IsUsable(currents[i].Sigma))
                    bad.Add($"current #{i + 1} (branch {currents[i].FromBusId}->{currents[i].ToBusId}, phase {Phase(currents[i].Phase)}) sigma {Fmt(currents[i].Sigma)}");
        if (bad.Count == 0) return;
        throw new ArgumentException(
            $"{estimator}: {bad.Count} measurement sigma(s) unusable (each must be a finite number above 0 pu whose weight " +
            $"1 / sigma^2 is finite and above 0): {string.Join("; ", bad.Take(MaxListed))}" +
            (bad.Count > MaxListed ? $"; and {bad.Count - MaxListed} more." : "."));
    }

    /// <summary>The phase letter, or the raw index when it is not 0, 1 or 2 (so a bad phase cannot hide the sigma).</summary>
    private static string Phase(int p) => p is >= 0 and <= 2 ? "abc"[p].ToString() : p.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
