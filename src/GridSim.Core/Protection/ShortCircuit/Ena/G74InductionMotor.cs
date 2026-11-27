using System;
using GridSim.Core.Numerics;

namespace GridSim.Core.Protection.ShortCircuit.Ena;

/// <summary>How an induction motor's fault contribution decays.</summary>
public enum MotorDecayModel
{
    /// <summary><c>I(t) = I'' e^{-t/T}</c> with a time constant the data file supplies.</summary>
    Exponential,
    /// <summary><c>I(t) = mu(t) q(t) I''</c>, the IEC 60909-0 asynchronous-motor breaking current: mu
    /// (<see cref="Iec60909.BreakingFactorMu"/>, from the motor's I''/IrM) times q
    /// (<see cref="Iec60909.MotorBreakingFactorQ"/>, from the rated power per pole pair). <c>I(0) = I''</c>. IEC gives
    /// the factors from 0.02 s, so a time between 0 and 0.02 s is refused.</summary>
    Iec60909Q,
}

/// <summary>
/// An induction motor's fault contribution. The equivalent impedance <c>Z_M = 1 / (I_LR / I_rM)</c> per unit on the
/// motor's own base is the IEC 60909-0 equivalent used by the incumbent standard; the motor is stamped as an X''d
/// source of that magnitude (resistance neglected: it keeps the current magnitude, not its angle). The time decay is
/// either a supplied time constant or the incumbent IEC q factor. G74's own motor treatment (which motors count,
/// the decay it prescribes, any aggregation rules) is AWAITING STANDARD TEXT.
/// </summary>
public static class G74InductionMotor
{
    /// <summary>Equivalent impedance, pu on the motor's rating: <c>1 / lockedRotorCurrentRatio</c>.</summary>
    public static double EquivalentImpedancePu(double lockedRotorCurrentRatio)
    {
        if (!(lockedRotorCurrentRatio > 0.0) || !double.IsFinite(lockedRotorCurrentRatio))
            throw new ArgumentOutOfRangeException(nameof(lockedRotorCurrentRatio), lockedRotorCurrentRatio, "locked-rotor current ratio must be > 0");
        return 1.0 / lockedRotorCurrentRatio;
    }

    /// <summary>The time factor for the chosen model: <c>e^{-t/T}</c> for the exponential model (1 at t = 0), and q alone
    /// for <see cref="MotorDecayModel.Iec60909Q"/>. The study (<see cref="G74Method"/>) multiplies q by the motor's mu,
    /// which needs its I''/IrM, and uses I'' itself at t = 0.</summary>
    public static double Ratio(MotorDecayModel model, double timeConstantS, double ratedMwPerPolePair, double tSeconds)
    {
        if (!(tSeconds >= 0.0) || !double.IsFinite(tSeconds)) throw new ArgumentOutOfRangeException(nameof(tSeconds), tSeconds, "time must be >= 0");
        return model switch
        {
            MotorDecayModel.Exponential => DetMath.Exp(-tSeconds / timeConstantS),
            MotorDecayModel.Iec60909Q => Iec60909.MotorBreakingFactorQ(ratedMwPerPolePair, tSeconds),
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
    }
}
