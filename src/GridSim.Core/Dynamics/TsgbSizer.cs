namespace GridSim.Core.Dynamics;

/// <summary>The device the sizer chose, and what it achieves on the design event.</summary>
/// <param name="SyntheticInertiaMwS">Device rotational inertia (MW.s) that holds |RoCoF| at the target.</param>
/// <param name="FastResponseMw">Device fast frequency response (MW) the search settled on.</param>
/// <param name="AchievedRocofHzPerS">Peak RoCoF (Hz/s) of the design event with the device fitted.</param>
/// <param name="AchievedNadirHz">Nadir (Hz) of the design event with the device fitted.</param>
/// <param name="Feasible">R254 (October 2026 review): true when the device actually holds the nadir target
/// (<see cref="AchievedNadirHz"/> &gt;= the target). The fast-response search grows its upper bracket a bounded number
/// of times (<see cref="TsgbSizer.MaxBracketGrowths"/>); when even the largest bracket cannot lift the nadir to the
/// target, the bisection settles on that bracket and the sizes are what the search tried, NOT a device that secures
/// the loss. Callers must show this; before the flag the result looked like a valid size in every case.</param>
public sealed record TsgbSizing(
    double SyntheticInertiaMwS,
    double FastResponseMw,
    double AchievedRocofHzPerS,
    double AchievedNadirHz,
    bool Feasible);

/// <summary>
/// Inverts the TSGB question: instead of "given a device, how much does it help?", it asks
/// "how big a device do I need to keep a given loss inside the limits?".
///
/// Synthetic inertia is closed-form  -  the initial RoCoF depends only on total spinning
/// energy, so the energy needed to hold |RoCoF| at the target is exact, and the device must
/// supply the shortfall. Fast frequency response (which arrests the nadir) has no closed
/// form here, so it's found by binary search  -  the nadir rises monotonically with FFR.
/// </summary>
public static class TsgbSizer
{
    // ---- Named search constants (October 2026 review, R173, H hygiene stream). Values unchanged. ----

    /// <summary>Smallest starting upper bracket (MW) of the fast-response search; the bracket starts at
    /// max(this, loss).</summary>
    public const double InitialFfrBracketMw = 100.0;

    /// <summary>How many times the upper bracket may grow (each by <see cref="BracketGrowthFactor"/>) while it still
    /// cannot hold the nadir target. After that the search gives up growing; see <see cref="TsgbSizing.Feasible"/>.</summary>
    public const int MaxBracketGrowths = 8;

    /// <summary>Factor the upper bracket grows by each time (8 growths of 1.8 reach about 110 times the start).</summary>
    public const double BracketGrowthFactor = 1.8;

    /// <summary>Number of bisection halvings of the fast-response bracket (2^-44 of the bracket: far below a watt for
    /// any GB-sized loss).</summary>
    public const int FfrBisectionIterations = 44;

    /// <summary>A RoCoF target at or below this (Hz/s) means "no RoCoF target": no synthetic inertia is sized (it would
    /// otherwise divide by zero).</summary>
    public const double MinRocofTargetHzPerS = 1e-9;

    public static TsgbSizing SizeForTargets(
        double systemInertiaMwS, double demandMw, double lossMw,
        double targetMaxRocofHzPerS, double targetMinNadirHz, DynamicsConfig? baseConfig = null)
    {
        DynamicsConfig baseCfg = baseConfig ?? new DynamicsConfig();
        double f0 = baseCfg.NominalHz;
        double loss = Math.Abs(lossMw);

        // 1) Synthetic inertia: E_total >= f0loss / (2targetRoCoF); supply the shortfall.
        double synthetic = 0;
        if (targetMaxRocofHzPerS > MinRocofTargetHzPerS)
        {
            double eRequired = f0 * loss / (2.0 * targetMaxRocofHzPerS);
            synthetic = Math.Max(0.0, eRequired - systemInertiaMwS);
        }

        // 2) Fast response: binary-search the smallest MW that lifts the nadir to target.
        double lo = 0, hi = Math.Max(InitialFfrBracketMw, loss);
        for (int grow = 0; grow < MaxBracketGrowths && NadirWith(synthetic, hi) < targetMinNadirHz; grow++) hi *= BracketGrowthFactor;
        for (int it = 0; it < FfrBisectionIterations; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (NadirWith(synthetic, mid) >= targetMinNadirHz) hi = mid; else lo = mid;
        }
        double ffr = hi;

        FrequencyResponse final = Run(synthetic, ffr);
        // R254: say whether the size found actually holds the nadir target (the same >= test the bisection uses). The
        // numbers are exactly what they were; only the flag is new.
        bool feasible = final.NadirHz >= targetMinNadirHz;
        return new TsgbSizing(synthetic, ffr, final.InitialRocofHzPerS, final.NadirHz, feasible);

        double NadirWith(double synth, double ffrMw) => Run(synth, ffrMw).NadirHz;
        FrequencyResponse Run(double synth, double ffrMw) =>
            FrequencyDynamics.Simulate(systemInertiaMwS, demandMw,
                new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -loss) },
                baseCfg with { DeviceRotationalInertiaMwS = synth, FastResponseMw = ffrMw });
    }
}
