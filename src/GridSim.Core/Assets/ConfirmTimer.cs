using GridSim.Core.Assets.Protection;

namespace GridSim.Core.Assets;

/// <summary>
/// N235 (October 2026, found by DOC-4, fixed by DOC-5): how long a condition has held, counted in whole substeps.
///
/// <para>The grid-following and grid-forming inverters timed their RoCoF trip confirm by adding the substep length to a
/// running total every step (<c>dwell += dt</c>) and comparing the total with the confirm time. Repeated addition of a
/// decimal step drifts: ten additions of 0.02 s give 0.19999999999999998, so a 0.2 s confirm at 20 ms substeps was met
/// on the 11th step (220 ms), not the 10th. D-DOC-095 fixed exactly this in the protection relays
/// (<see cref="ProtectionEvaluator"/>); this is the same rule for the inverters, so the two can never disagree.</para>
///
/// <para>The rule: the time over threshold is (whole substeps) x (substep), one multiplication, not a running sum. A
/// substep of a different length from the ones counted so far first banks those (steps x their length) into a carried
/// total, so a run whose substep changes mid-confirm (the live loop's variable tick) is still exact per run of equal
/// steps. The confirm is met when the elapsed time reaches it, or falls short of it by at most
/// <see cref="ProtectionEvaluator.DwellRelativeSlack"/> of it (2^-49: the representation error of two decimal doubles,
/// under a femtosecond for any confirm below 0.5 s). Plain double arithmetic, identical on every CPU.</para>
///
/// <para>Effect (steps to trip, before -> after): 0.2 s at 0.02 s 11 -> 10; 0.25 s at 0.02 s 13 -> 13 (0.25 / 0.02 is
/// 12.5, so the 13th step is the first to reach it either way); unchanged for every bundled confirm at the substeps
/// GridSim uses: 0.5 s and 1.0 s at 0.02, 0.01, 0.005 and 0.001 s; 0 s (the first over-threshold step). The exact
/// cases are pinned in Doc5FrequencyEvidenceAssetTests.</para>
/// </summary>
internal struct ConfirmTimer
{
    private long _steps;      // whole substeps counted at the current substep length
    private double _stepS;    // the current substep length (s)
    private double _carryS;   // time banked from earlier runs of a different substep length (s)

    /// <summary>Forget everything counted (the condition stopped holding, or the asset was reset).</summary>
    public void Clear()
    {
        _steps = 0;
        _stepS = 0.0;
        _carryS = 0.0;
    }

    /// <summary>
    /// Count one more substep of length <paramref name="dtSeconds"/> during which the condition held, and return the
    /// total time it has held: carried + steps x step.
    /// </summary>
    public double Count(double dtSeconds)
    {
        if (_steps > 0 && dtSeconds != _stepS)
        {
            _carryS += _steps * _stepS;   // bank the run of the old length, exactly as ProtectionEvaluator does
            _steps = 0;
        }
        _stepS = dtSeconds;
        _steps++;
        return _carryS + _steps * _stepS;
    }

    /// <summary>Whether <paramref name="elapsedS"/> satisfies a confirm of <paramref name="confirmS"/> (the D-DOC-095 rule).</summary>
    public static bool Reached(double elapsedS, double confirmS) => ProtectionEvaluator.DwellReached(elapsedS, confirmS);
}
