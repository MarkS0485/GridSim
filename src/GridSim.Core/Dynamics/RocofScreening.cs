using GridSim.Core.Model;

namespace GridSim.Core.Dynamics;

/// <summary>One credible single loss: a name, the MW it sheds, and the spinning energy it takes with it.</summary>
public sealed record Contingency(string Name, double LostMw, double InertiaContributionMwS);

/// <summary>How one contingency plays out against the GB limits.</summary>
/// <param name="InitialRocofHzPerS">PEAK RoCoF (Hz/s, sign kept) of the contingency's simulated response (a single step
/// loss, so it is the step straight after the loss). N141: the name says "initial" for historical reasons (see
/// <see cref="FrequencyResponse.InitialRocofHzPerS"/>).</param>
public sealed record RocofScreeningResult(
    string Name,
    double LostMw,
    double RemainingInertiaGvaS,
    double InitialRocofHzPerS,
    double NadirHz,
    bool RocofBreach,
    bool StatutoryBreach,
    bool LfddRisk,
    // ---- dual-threshold RoCoF reporting (added; defaulted so the historical 8-arg ctor still compiles) ----
    // RocofBreach (above) is retained and tracks the LEGACY 0.125 Hz/s setting for back-compat.
    bool RocofBreachLegacy = false,
    bool RocofBreachModern = false,
    bool RocofBreachActive = false,
    double RocofLimitAppliedHzPerS = FrequencyLimits.RocofLimitModernHzPerS,
    // False when the remaining inertia is too small for the swing to be integrated stably at the screen's step: the
    // initial RoCoF is the closed form f0*P/(2E), the nadir is not known (NaN), and every breach is reported, because
    // nothing shows the loss is survivable. True (the default) for every integrated row.
    bool Integrated = true);

/// <summary>
/// N-1 frequency screening: lose each credible single infeed in turn and rank them by the
/// resulting RoCoF. A tripped unit hurts twice  -  its output becomes the deficit AND its
/// rotating mass leaves the system, so the remaining inertia is lower exactly when it's
/// needed most. This is the security view: which single loss is the binding RoCoF case, and
/// does it breach 0.125 Hz/s or the 49.5 Hz floor.
/// </summary>
public static class RocofScreening
{
    public static IReadOnlyList<RocofScreeningResult> Screen(
        double systemInertiaMwS, double demandMw, IEnumerable<Contingency> contingencies, DynamicsConfig? config = null)
        => Screen(systemInertiaMwS, demandMw, contingencies, FrequencyLimits.ActiveRocofLimitHzPerS, config);

    /// <summary>
    /// As <see cref="Screen(double, double, IEnumerable{Contingency}, DynamicsConfig?)"/>, but the
    /// "active" RoCoF breach is scored against <paramref name="activeRocofLimitHzPerS"/>. The retained
    /// <see cref="RocofScreeningResult.RocofBreach"/> still tracks the legacy 0.125 Hz/s setting; the
    /// legacy/modern/active flags are all reported.
    /// </summary>
    public static IReadOnlyList<RocofScreeningResult> Screen(
        double systemInertiaMwS, double demandMw, IEnumerable<Contingency> contingencies,
        double activeRocofLimitHzPerS, DynamicsConfig? config = null)
    {
        // N297 (B4): the bands of the system the swing runs at: FrequencyBands.Gb itself for every 50 Hz config (so every
        // 50 Hz result is the old one, bit for bit), the GB bands about the nominal otherwise.
        DynamicsConfig cfg = config ?? new DynamicsConfig();
        return Screen(systemInertiaMwS, demandMw, contingencies, activeRocofLimitHzPerS, cfg, FrequencyBands.ForNominal(cfg.NominalHz));
    }

    /// <summary>
    /// N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; the band convention is B2's, see
    /// <see cref="FrequencyBands"/>): as above, with the statutory and LFDD flags graded against <paramref name="bands"/>,
    /// the bands of the screened system (normally <see cref="FrequencyBands.ForNominal"/> at the model's nominal). Before,
    /// every screen graded against the absolute GB edges (49.5 / 48.8 Hz), so a 60 Hz case's nadirs (about 60 Hz) could
    /// never breach them however deep they fell. With <see cref="FrequencyBands.Gb"/> (what every older overload passes)
    /// each result is exactly the old one. The RoCoF limits are relay settings in Hz/s and do not depend on the nominal.
    /// </summary>
    public static IReadOnlyList<RocofScreeningResult> Screen(
        double systemInertiaMwS, double demandMw, IEnumerable<Contingency> contingencies,
        double activeRocofLimitHzPerS, DynamicsConfig? config, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        DynamicsConfig cfg = config ?? new DynamicsConfig();
        // Fail loud: grading a response integrated at one nominal against the bands of another would mix two systems.
        if (cfg.NominalHz != bands.NominalHz)
            throw new ArgumentException(
                $"RocofScreening.Screen: the bands are for a {bands.NominalHz:R} Hz system but the swing runs at {cfg.NominalHz:R} Hz.",
                nameof(bands));
        double demand = Math.Max(SwingKernel.MinDemandMw, demandMw);

        var results = new List<RocofScreeningResult>();
        foreach (Contingency c in contingencies)
        {
            double remaining = Math.Max(SwingKernel.MinInertiaMwS, systemInertiaMwS - c.InertiaContributionMwS);
            // The mass the swing actually integrates on: FrequencyDynamics.Simulate adds the device's real rotational
            // inertia to what it is given and floors the sum (SwingKernel.SwingInertia). The pre-check and the
            // closed-form RoCoF below must judge THAT mass (bug hunt 2026-10-29, finding 12): with only `remaining` a
            // fitted device could flag a contingency as not integrable (nadir NaN, every breach set) although the
            // simulate would have run stably, and its closed-form RoCoF ignored the device. With no device fitted
            // (DeviceRotationalInertiaMwS = 0) swingE is `remaining` exactly, so every such result is unchanged.
            double swingE = Math.Max(SwingKernel.MinInertiaMwS, remaining + cfg.DeviceRotationalInertiaMwS);
            // Below the explicit-Euler stability bound of the swing's damping term (dt*f0*D/(2E) < 2) the trajectory
            // cannot be integrated at this step: report the closed-form initial RoCoF and flag the row, instead of
            // integrating on the 1 MW.s floor into Infinity and NaN and presenting the result.
            double dampingMwPerHz = cfg.DampingPctPerHz / 100.0 * demand;
            double stableBelow = cfg.StepSeconds * cfg.NominalHz * dampingMwPerHz / 4.0;
            if (!(swingE > stableBelow) && cfg.Forming == FormingMode.None)
            {
                double initialRocof = -cfg.NominalHz * Math.Abs(c.LostMw) / (2.0 * swingE);
                results.Add(new RocofScreeningResult(
                    c.Name, Math.Abs(c.LostMw), remaining / 1000.0, initialRocof, double.NaN,
                    RocofBreach: true, StatutoryBreach: true, LfddRisk: true,
                    RocofBreachLegacy: true, RocofBreachModern: true, RocofBreachActive: true,
                    RocofLimitAppliedHzPerS: activeRocofLimitHzPerS, Integrated: false));
                continue;
            }
            FrequencyResponse resp = FrequencyDynamics.Simulate(
                remaining, demand, new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -Math.Abs(c.LostMw)) }, cfg);
            FrequencyAssessment a = FrequencyLimits.Assess(resp, activeRocofLimitHzPerS, bands);
            results.Add(new RocofScreeningResult(
                c.Name, Math.Abs(c.LostMw), remaining / 1000.0,
                resp.InitialRocofHzPerS, resp.NadirHz, a.RocofExceedsLimit, a.BelowStatutory, a.LfddAtRisk,
                a.RocofExceedsLegacyLimit, a.RocofExceedsModernLimit, a.RocofExceedsActiveLimit,
                a.RocofLimitAppliedHzPerS));
        }

        // Worst RoCoF first, in ONE defined order (D-DOC-119, October 2026). List.Sort is an introsort and is not stable:
        // two contingencies with the same |RoCoF| (two identical units, very common) could come out in either order, and
        // which one depended on the list length and the sort's internal partitioning, not on the input. Equal |RoCoF| now
        // keeps the order the contingencies were given in (for Screen(model): the model's generator order), by breaking
        // the tie on the input position. The values are untouched; only the order among exact ties is now defined.
        return SortWorstFirst(results);
    }

    /// <summary>D-DOC-119: <paramref name="results"/> by |initial RoCoF| descending (the comparison the screen always used,
    /// double.CompareTo, so NaN sorts last), equal magnitudes in their input order. A new list; the input is not changed.</summary>
    internal static List<RocofScreeningResult> SortWorstFirst(IReadOnlyList<RocofScreeningResult> results)
    {
        // Pair each result with its input position, so the comparison below never reports two entries as equal.
        var keyed = new List<(RocofScreeningResult Result, int Position)>(results.Count);
        for (int i = 0; i < results.Count; i++) keyed.Add((results[i], i));
        keyed.Sort((x, y) =>
        {
            int byRocof = Math.Abs(y.Result.InitialRocofHzPerS).CompareTo(Math.Abs(x.Result.InitialRocofHzPerS));
            return byRocof != 0 ? byRocof : x.Position.CompareTo(y.Position);   // tie: the earlier input first
        });
        var sorted = new List<RocofScreeningResult>(keyed.Count);
        foreach ((RocofScreeningResult r, int _) in keyed) sorted.Add(r);
        return sorted;
    }

    /// <summary>
    /// Screen every dispatched generator in a model. Each unit's inertia contribution is its rotating mass,
    /// H x MBase (MWs), or 0 for a known converter technology (N289 / N321); losing it removes that spinning energy and
    /// sheds its output Pg.
    /// </summary>
    public static IReadOnlyList<RocofScreeningResult> Screen(GridModel model, DynamicsConfig? config = null)
    {
        // N275 (October 2026, stream B1): screening a model runs at the model's nominal frequency, as the trip verb has since
        // D2 (no config: the defaults at it; a config at another nominal: refused). For a 50 Hz model with no config this is
        // the default config, bit for bit; the 60 Hz WSCC case9 now screens at 60 Hz (every RoCoF 1.2 times the old 50 Hz
        // figure for the same loss and inertia). N297 (B4): its nadirs are now graded against the GB bands about 60 Hz
        // (statutory 59.5, LFDD 58.8 Hz); before, against 49.5 / 48.8 Hz, which a 60 Hz nadir could never reach.
        config = SwingKernel.ConfigForModel(model, config, "RocofScreening.Screen");
        // N321 (LF7, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the system inertia is the N289 total,
        // GridModel.SystemInertiaMwS (only rotating mass counts: a unit whose type is a KNOWN converter technology adds
        // nothing, whatever H its data carries), and each loss takes out only what that unit put in
        // (GridModel.RotatingInertiaMwS: H x MBase, or exactly 0 for a converter). trip has done the same since N314; this
        // screen used to sum H x MBase over EVERY unit, so a converter-typed unit with H > 0 gave the screen more inertia
        // than the trip, the lumped model and the distributed model, and its own loss removed inertia it never had. For a
        // model with no converter-typed unit carrying H > 0 (every bundled case, checked by type at R239 / N289; the
        // MATPOWER cases carry no types, so every unit counts) both sums run over exactly the same terms in the same
        // order, so every screened number is bit-for-bit unchanged. The loss itself (Pg) is unchanged: a converter's
        // output is still lost infeed.
        double total = model.SystemInertiaMwS;
        double demand = model.Buses.Sum(b => b.Pd);
        IEnumerable<Contingency> conts = model.Generators
            .Where(g => g.Pg > 0)
            .Select(g => new Contingency(g.Name ?? $"gen@{g.Bus}", g.Pg, GridModel.RotatingInertiaMwS(g)));
        return Screen(total, demand, conts, FrequencyLimits.ActiveRocofLimitHzPerS, config, FrequencyBands.ForModel(model));
    }
}
