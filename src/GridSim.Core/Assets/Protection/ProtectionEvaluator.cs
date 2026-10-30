namespace GridSim.Core.Assets.Protection;

/// <summary>The edge a stage produced this substep.</summary>
public enum ProtectionEdge { None, Fired, Restored }

/// <summary>
/// The stateful dwell/definite-time edge detector for ONE <see cref="ProtectionStage"/> - the extracted,
/// generalised form of the <see cref="GridFollowingInverter"/> RoCoF-trip block: accumulate time over
/// threshold, emit <see cref="ProtectionEdge.Fired"/> exactly once when the dwell is satisfied, and
/// (if a finite auto-restore delay is set) emit <see cref="ProtectionEdge.Restored"/> after it. The
/// caller supplies <paramref name="measuredValue"/> already selected/uplifted for the stage's quantity,
/// so this class is quantity-agnostic.
/// </summary>
public sealed class ProtectionEvaluator
{
    private readonly ProtectionStage _stage;
    // D-DOC-095 (October 2026): the time spent over the threshold is counted in whole substeps, not summed. It used to
    // be accumulated by repeated floating-point addition (_dwellS += dt), and the rounding of that sum ran short of the
    // dwell: 0.2 s at 20 ms substeps summed to 0.19999999999999998 after 10 steps, so the stage operated after 11
    // steps (220 ms), one substep late. Now the elapsed time is (whole steps) x (substep), one multiplication, and a
    // substep length that changes mid-dwell banks the steps taken so far in _dwellCarryS.
    private long _dwellSteps;      // consecutive over-threshold substeps taken at _dwellStepS
    private double _dwellStepS;    // the substep length those steps were taken at
    private double _dwellCarryS;   // over-threshold time already banked at an earlier, different substep length
    private bool _operated;
    private double _restoreAtS = double.PositiveInfinity;

    /// <summary>
    /// Wrap one stage. Throws for <see cref="ProtectionAction.RestoreBranch"/> (R073, October 2026): no consumer acts
    /// on it (the controller treated it as a no-op), so a stage built with it would operate and do nothing. A
    /// recloser restores its branch through the auto-restore delay of an <see cref="ProtectionAction.OpenBranch"/>
    /// stage instead.
    /// </summary>
    public ProtectionEvaluator(ProtectionStage stage)
    {
        if (stage.Action == ProtectionAction.RestoreBranch)
            throw new NotSupportedException(
                "ProtectionAction.RestoreBranch is reserved and not implemented: nothing acts on it, so the stage would " +
                "operate and change nothing. Use an OpenBranch stage with a finite AutoRestoreDelayS to reclose a branch.");
        _stage = stage;
    }

    public ProtectionStage Stage => _stage;
    public bool Operated => _operated;

    public void Reset() { ClearDwell(); _operated = false; _restoreAtS = double.PositiveInfinity; }

    private void ClearDwell() { _dwellSteps = 0; _dwellStepS = 0.0; _dwellCarryS = 0.0; }

    /// <summary>
    /// D-DOC-095: the relative slack, in units of the dwell, within which a dwell counts as reached. The elapsed time
    /// (steps x substep) and the authored dwell are each the nearest double to a decimal (0.02 s, 0.2 s), so when the
    /// decimals agree exactly the two doubles can still differ by a few units in the last place either way. 8 machine
    /// epsilons (8 x 2^-52 = 2^-49, about 1.8e-15 of the dwell, under a femtosecond for any dwell below 0.5 s) absorbs that
    /// representation error and nothing physical. Plain double arithmetic, so it is identical on every CPU.
    /// </summary>
    internal const double DwellRelativeSlack = 1.7763568394002505e-15;   // 8 x 2^-52 = 2^-49, exactly representable

    /// <summary>D-DOC-095: whether <paramref name="elapsedS"/> of over-threshold time satisfies the dwell
    /// <paramref name="dwellS"/> (see <see cref="DwellRelativeSlack"/>).
    /// DOC-5 (new finding, fixed with N235): the slack term is applied only to a FINITE dwell. For an infinite dwell
    /// (a stage that must never time out) it read inf - elapsed &lt;= slack x inf, that is inf &lt;= inf, which is true, so
    /// the stage operated on its first over-threshold step instead of never. A finite dwell is unchanged, bit for bit.</summary>
    internal static bool DwellReached(double elapsedS, double dwellS) =>
        elapsedS >= dwellS || (double.IsFinite(dwellS) && dwellS - elapsedS <= DwellRelativeSlack * dwellS);

    /// <summary>Advance one substep against the pre-selected measured value; return the edge, if any.</summary>
    public ProtectionEdge Step(in AssetStepContext ctx, double measuredValue)
    {
        double dt = ctx.DtSeconds;

        if (_operated)
        {
            // Auto-reclose: reset once the restore delay elapses (finite AutoRestoreDelayS only).
            if (ctx.ClockSeconds >= _restoreAtS)
            {
                _operated = false;
                ClearDwell();
                _restoreAtS = double.PositiveInfinity;
                return ProtectionEdge.Restored;
            }
            return ProtectionEdge.None;   // latched / operated
        }

        if (ctx.ClockSeconds < _stage.ArmDelayS) return ProtectionEdge.None;   // not yet armed

        bool over = _stage.Sense == ThresholdSense.Above
            ? measuredValue > _stage.Threshold
            : measuredValue < _stage.Threshold;
        double elapsedS = 0.0;
        if (over)
        {
            // Count this substep. A substep of a different length from the ones counted so far banks those first.
            if (_dwellSteps > 0 && dt != _dwellStepS) { _dwellCarryS += _dwellSteps * _dwellStepS; _dwellSteps = 0; }
            _dwellStepS = dt;
            _dwellSteps++;
            elapsedS = _dwellCarryS + _dwellSteps * _dwellStepS;
        }
        else ClearDwell();

        if (over && DwellReached(elapsedS, _stage.DwellSeconds))
        {
            _operated = true;
            _restoreAtS = double.IsPositiveInfinity(_stage.AutoRestoreDelayS)
                ? double.PositiveInfinity
                : ctx.ClockSeconds + _stage.AutoRestoreDelayS;
            return ProtectionEdge.Fired;
        }
        return ProtectionEdge.None;
    }
}
