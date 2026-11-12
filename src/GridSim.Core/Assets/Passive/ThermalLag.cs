namespace GridSim.Core.Assets;

/// <summary>
/// The first-order thermal lag shared by the passive plant models (<see cref="LineThermalDynamics"/>,
/// <see cref="TransformerDynamics"/>): <c>tau dx/dt = drive - x</c>, stepped by its EXACT discretisation over a tick
/// of <c>dt</c> seconds with the drive held constant across the tick:
/// <code>
///   x(t + dt) = drive + (x(t) - drive) * exp(-dt / tau)
/// </code>
/// <para>Bug hunt 2026-11-01 (#190): both models used explicit Euler with <c>tau = max(dt, 60 * tauMin)</c>. At the
/// loop tick sizes (LiveSim's default 20 min = 1200 s, GdaReplayLoop's 1800 s) <c>dt / tau</c> was then 1 whenever
/// <c>dt &gt;= tau</c>, so the state jumped to the drive in one tick and the lag vanished: a 20 min transformer from
/// cold at loading 1 read 1.000 after one 1200 s tick against the exact 1 - e^-1 = 0.632 (0.777 at 1800 s), and a
/// 15 min line read 1.000 against 0.736 (0.865 at 1800 s), so a 1000 MVA line overloaded for one tick was derated to
/// 900 MVA instead of 926.4 MVA; cooling was equally instant. The exact step has no stability limit, so the clamp is
/// gone; it is exact for any tick while the drive is constant over the tick, and it moves every thermal value at
/// every tick size (Euler was first-order accurate even at small ticks).</para>
/// </summary>
internal static class ThermalLag
{
    /// <summary>
    /// The time constant in seconds from a class's <paramref name="tauMinutes"/>. Fails loud on a negative or
    /// non-finite value (before, it was silently replaced by one tick). Zero is allowed, as the kind schemas allow it:
    /// a zero time constant is no lag at all, the state following the drive each tick.
    /// </summary>
    public static double TauSeconds(double tauMinutes, string className, string branchName)
    {
        if (!(tauMinutes >= 0.0) || !double.IsFinite(tauMinutes))
            throw new ArgumentOutOfRangeException(nameof(tauMinutes), tauMinutes,
                $"Asset class '{className}' (branch '{branchName}'): thermalTauMin must be a finite number of minutes, 0 or more.");
        return tauMinutes * 60.0;
    }

    /// <summary>One exact step of the lag (see the class summary). <paramref name="dtSeconds"/> 0 leaves the state as it
    /// is; <paramref name="tauSeconds"/> 0 (no lag) gives the drive.</summary>
    public static double Step(double state, double drive, double dtSeconds, double tauSeconds)
    {
        if (tauSeconds == 0.0) return drive;
        return drive + (state - drive) * DetMath.Exp(-dtSeconds / tauSeconds);
    }
}
