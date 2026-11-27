namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// GENCLS, the PSS/E classical machine (data H D, with X'd from the RAW record's source reactance ZSORCE): a constant
/// voltage E' behind the transient reactance X'd, carried by the RMS engine's six-state machine.
///
/// <para>The machine data put every reactance at X'd (Xd = X'd = X''d = Xq = X'q = X''q), so the network sees E''
/// behind X'd, the rotor angle is the angle of Vt + jX'd It (the classical internal angle), Id and Iq drop out of every
/// flux equation, and E''d starts at 0. Every open-circuit time constant is <see cref="HeldFluxTimeSeconds"/>, so each flux
/// lag's factor a = exp(-dt / T) rounds to exactly 1 and 1 - a to exactly 0 for any step up to 1e13 s: the flux states
/// are held bit for bit, the constant-E' classical model. A classical machine has no field to drive, so the dynamics
/// file refuses an exciter or stabiliser on it; a governor still drives its mechanical power.</para>
/// </summary>
public static class Gencls
{
    /// <summary>The open-circuit time constant (s) that holds the classical machine's flux states: 1e30 s.</summary>
    public const double HeldFluxTimeSeconds = 1e30;

    /// <summary>The RMS machine data of a GENCLS unit on its own base: inertia <paramref name="inertiaSeconds"/> (above
    /// 0), damping <paramref name="damping"/> (pu power per pu speed on the machine base) and transient reactance
    /// <paramref name="xdpPu"/> (above 0), with an optional governor.</summary>
    public static SubtransientInit.MachineData Data(double inertiaSeconds, double damping, double xdpPu, GovernorData? governor = null)
    {
        if (!(inertiaSeconds > 0) || !double.IsFinite(inertiaSeconds))
            throw new ArgumentOutOfRangeException(nameof(inertiaSeconds), inertiaSeconds, "GENCLS: H must be a finite number above 0 (PSS/E's H = 0 infinite bus is not a GridSim machine).");
        if (!double.IsFinite(damping)) throw new ArgumentOutOfRangeException(nameof(damping), damping, "GENCLS: D must be finite.");
        if (!(xdpPu > 0) || !double.IsFinite(xdpPu))
            throw new ArgumentOutOfRangeException(nameof(xdpPu), xdpPu, "GENCLS: X'd must be a finite number above 0.");
        const double t = HeldFluxTimeSeconds;
        return new SubtransientInit.MachineData(inertiaSeconds, damping, xdpPu, xdpPu, xdpPu, xdpPu, xdpPu, xdpPu, t, t, t, t,
            Governor: governor);
    }
}
