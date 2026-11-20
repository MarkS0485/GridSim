using System;
using GridSim.Core.Numerics;

namespace GridSim.Core.Protection.ShortCircuit.Ena;

/// <summary>
/// The classical two-exponential decay of the AC envelope of a synchronous machine's short-circuit current:
/// <c>I(t) = (I'' - I') e^{-t/T''d} + (I' - Ik) e^{-t/T'd} + Ik</c>, with <c>I'' = E/X''d</c>, <c>I' = E/X'd</c>,
/// <c>Ik = E/Xd</c> for an unchanged internal voltage E (textbook; for example Kundur, Power System Stability and
/// Control, section 4.x, and IEEE Std 1110). This is the CLOSED FORM the R4-06 oracle uses. It is NOT a quotation of
/// ENA ER G74 (Issue 2:2021), whose text was not obtainable: any G74-specific decay treatment (clause numbers, the
/// time dependence G74 prescribes, its treatment of AVR action and external impedance) is AWAITING STANDARD TEXT.
/// </summary>
public static class G74MachineDecay
{
    /// <summary>The ratio <c>I(t) / I''</c> for reactances <paramref name="xdSub"/> &lt; <paramref name="xdTrans"/> &lt;
    /// <paramref name="xdSync"/> (all on one base, any external series reactance already added to each) and short-circuit
    /// time constants <paramref name="tdSub"/> &lt; <paramref name="tdTrans"/> (s). At t = 0 it is exactly 1; as t grows
    /// it tends to <c>xdSub / xdSync</c>.</summary>
    public static double Ratio(double xdSub, double xdTrans, double xdSync, double tdSub, double tdTrans, double tSeconds)
    {
        if (!(tSeconds >= 0.0) || !double.IsFinite(tSeconds)) throw new ArgumentOutOfRangeException(nameof(tSeconds), tSeconds, "time must be a finite number of seconds >= 0");
        double a = 1.0 - xdSub / xdTrans;             // (I'' - I') / I''
        double b = xdSub / xdTrans - xdSub / xdSync;  // (I' - Ik) / I''
        double c = xdSub / xdSync;                    // Ik / I''
        return a * DetMath.Exp(-tSeconds / tdSub) + b * DetMath.Exp(-tSeconds / tdTrans) + c;
    }

    /// <summary>The steady-state ratio <c>Ik / I'' = X''d / Xd</c>.</summary>
    public static double SteadyRatio(double xdSub, double xdSync) => xdSub / xdSync;
}
