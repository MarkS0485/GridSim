using System.Numerics;
using GridSim.Core.Dynamics.Distributed;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// Governor-in-the-loop transient stability: the classical multi-machine swing (<see
/// cref="ClassicalMultiMachine"/>) with each machine's mechanical power supplied by a standard turbine-
/// governor (<see cref="Tgov1"/>) instead of being held constant. This is the first study that closes a
/// standard control model around the machine dynamics, and it is built with no second kernel - it drives
/// the trusted classical integrator through its per-step mechanical-power hook, so:
/// <list type="bullet">
/// <item>when every governor is absent the result is <b>bit-for-bit</b> the constant-Pm classical swing
///   (the reduction identity, pinned by test);</item>
/// <item>the machine-epsilon numerics of the classical kernel are inherited unchanged - this layer adds
///   only the governor state advance.</item>
/// </list>
/// Each present governor is initialised to its machine's initial mechanical power <paramref name="pm0"/>
/// so an undisturbed run is stationary; on a disturbance it acts on the machine's own speed deviation. For
/// an islanded machine set the steady speed deviation settles to the analytic droop
/// <c>omegabar_ss = deltaP / sum(1/R)</c>, the second, independent oracle this layer is validated against.
/// </summary>
public static class GovernedMultiMachine
{
    /// <summary>
    /// Integrate the governed swing. Arguments mirror <see cref="ClassicalMultiMachine.Simulate"/>;
    /// <paramref name="governors"/> is one entry per machine (aligned to <paramref name="pm0"/>), a null
    /// entry meaning that machine keeps its constant mechanical power. When all entries are null the call
    /// is exactly the classical swing.
    ///
    /// Each non-null governor is initialised at its machine's <paramref name="pm0"/> (every state at that
    /// equilibrium, Pm = Pe at <paramref name="delta0"/>); when <paramref name="mechanicalReference"/> is given its
    /// load reference (Pref) is then moved to that value. A reference equal to <paramref name="pm0"/> is an
    /// undisturbed stationary run; one that differs is a step in mechanical set-point at t = 0, the sustained
    /// imbalance whose steady speed deviation is the analytic droop.
    ///
    /// <para>R246 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): the step now
    /// travels through the governor's own dynamics. The governor used to be INITIALISED at the reference, which preset
    /// the valve and reheat states there, so the mechanical power jumped to the new set-point on the first step with
    /// no valve (T1) or reheat (T2 / T3) lag: only the steady droop was right, the transient was not. It is now
    /// initialised at <paramref name="pm0"/> and the reference is changed with <see cref="Tgov1.SetReference"/>, which
    /// is how a set-point change enters the TGOV1 block diagram (PSS/E TGOV1: Pref joins the summing junction ahead of
    /// the valve lag). With no reference the call is exactly what it was.</para>
    /// <para>GR-560: a TGOV1 is parameterised on its machine's own MVA base (R, the valve limits), while this layer's
    /// powers (<paramref name="pm0"/>, <paramref name="mechanicalReference"/>, the electrical power) are on the system base.
    /// Pass <paramref name="machineToSystemBase"/> (MBase / system base MVA, one per machine) and the governor runs in its
    /// machine's per unit: it is initialised at pm0 / k, its reference is set at reference / k, and its output is scaled
    /// back by k. Left null, the governor reads the system-base powers directly, the behaviour before GR-560 (correct only
    /// when every machine is rated at the system base). A ratio of exactly 1 is the null path bit for bit.</para>
    /// </summary>
    public static ClassicalMultiMachine.Result Simulate(
        double[] h, double[] d, double[] pm0, double[] eq, double[] delta0,
        Complex[,] yReduced,
        Tgov1?[] governors,
        double f0, double stepSeconds, double durationSeconds,
        ClassicalMultiMachine.Fault? fault = null,
        double slipLimitRad = 2.0 * System.Math.PI,
        double[]? mechanicalReference = null,
        double[]? machineToSystemBase = null)
    {
        int n = h.Length;
        if (governors.Length != n)
            throw new ArgumentException("governors must have one entry per machine.", nameof(governors));
        if (mechanicalReference is not null && mechanicalReference.Length != n)
            throw new ArgumentException("mechanicalReference must have one entry per machine.", nameof(mechanicalReference));
        if (machineToSystemBase is not null)
        {
            if (machineToSystemBase.Length != n)
                throw new ArgumentException("machineToSystemBase must have one entry per machine.", nameof(machineToSystemBase));
            for (int i = 0; i < n; i++)
                if (!(double.IsFinite(machineToSystemBase[i]) && machineToSystemBase[i] > 0.0))
                    throw new ArgumentOutOfRangeException(nameof(machineToSystemBase), machineToSystemBase[i],
                        $"machine {i}: the MBase / system-base ratio must be finite and above 0.");
        }
        // GR-560: k = 1 (the system base) when no ratios are given; x / 1.0 and x * 1.0 are x exactly.
        double K(int i) => machineToSystemBase is null ? 1.0 : machineToSystemBase[i];

        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (governors[i] is { } gov)
            {
                // R246: equilibrium at the machine's own initial power, then the set-point step (if any) on the reference.
                gov.Initialize(pm0[i] / K(i));
                if (mechanicalReference is not null) gov.SetReference(mechanicalReference[i] / K(i));
                any = true;
            }
        }

        // No governor anywhere => the classical swing verbatim (no hook), so the reduction is exact.
        Func<int, double, double>? hook = any
            ? (i, speedPu) => governors[i] is { } gov ? gov.Step(speedPu, stepSeconds) * K(i) : pm0[i]
            : null;

        return ClassicalMultiMachine.Simulate(
            h, d, pm0, eq, delta0, yReduced, f0, stepSeconds, durationSeconds, fault, slipLimitRad, hook);
    }
}
