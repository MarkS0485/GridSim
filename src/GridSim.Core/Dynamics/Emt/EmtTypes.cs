namespace GridSim.Core.Dynamics.Emt;

/// <summary>The discretization rule for a companion model. Trapezoidal is second-order accurate but only
/// marginally stable (it rings at Nyquist on a step discontinuity); backward Euler is first-order but
/// L-stable, so it is used for the two half-steps of a Critical Damping Adjustment after a switching event.</summary>
public enum IntegrationRule
{
    /// <summary>Second-order trapezoidal (Dommel default).</summary>
    Trapezoidal,
    /// <summary>First-order backward Euler (L-stable; used for the CDA burst around a discontinuity).</summary>
    BackwardEuler,
}

/// <summary>What a probe records at each timestep.</summary>
public enum EmtProbeKind
{
    /// <summary>The node voltage at a bus (per-unit).</summary>
    NodeVoltage,
    /// <summary>The branch current through a series element (per-unit), positive From-&gt;To.</summary>
    BranchCurrent,
}

/// <summary>One recorded channel: a name and what it reads.</summary>
/// <param name="Name">Column label in the output (e.g. <c>v:4</c>, <c>i:1-&gt;2</c>).</param>
/// <param name="Kind">Node voltage or branch current.</param>
/// <param name="Target">Bus id (voltage) or element id (branch current).</param>
public sealed record EmtProbe(string Name, EmtProbeKind Kind, int Target);

/// <summary>One timestep of the trajectory: the instant and the probe values in probe order.</summary>
public readonly record struct EmtPoint(double TSeconds, double[] Probe);

/// <summary>Headline run statistics carried alongside the trajectory.</summary>
/// <param name="Steps">Number of timesteps taken.</param>
/// <param name="Refactorizations">How many times the nodal matrix was factored: once when the engine was built (or
/// reset), then, at each step where a switching event really changed a conductance, twice with the Critical Damping
/// Adjustment (into the backward-Euler half-steps and back) or once without it (CDA off, or a backward-Euler run).
/// So a trapezoidal run with CDA and k effective events reports 1 + 2k. (D-DOC-184: this said "once + one per
/// switching event", which was true only without CDA.)</param>
/// <param name="MaxProbeAbs">Largest absolute probe value seen (a quick transient-magnitude readout).</param>
/// <param name="WallMs">Wall-clock milliseconds spent in the integration loop.</param>
public readonly record struct EmtRunStats(long Steps, int Refactorizations, double MaxProbeAbs, double WallMs);

/// <summary>The result of an EMT run: the probe names, the full trajectory, and the run statistics. It is a
/// <b>model trajectory</b>, not a claim about the real grid (see the governing-constraint note on
/// <see cref="EmtEngine"/>).</summary>
public sealed record EmtResult(IReadOnlyList<string> ProbeNames, IReadOnlyList<EmtPoint> Points, EmtRunStats Stats);

/// <summary>Engine options.</summary>
public sealed record EmtOptions
{
    /// <summary>The base integration rule (Trapezoidal unless a whole-run backward-Euler is requested).</summary>
    public IntegrationRule Rule { get; init; } = IntegrationRule.Trapezoidal;

    /// <summary>Apply Critical Damping Adjustment at switching events (recommended): replace the event step with
    /// backward-Euler half-steps to annihilate the spurious trapezoidal Nyquist oscillation.</summary>
    public bool UseCda { get; init; } = true;

    /// <summary>D-DOC-024: refuse a network with a non-passive element (a series arm with a negative resistance or a
    /// shunt with a negative conductance), naming each, instead of running it. Off by default, because
    /// network-reduction equivalents carry such elements on purpose and the engine's existing callers build them (see
    /// <see cref="EmtEngine.NonPassiveElements"/>, which lists them either way). The <c>gridsim emt</c> verb turns it
    /// on unless <c>--allow-non-passive</c> is given.</summary>
    public bool RefuseNonPassive { get; init; }
}

/// <summary>
/// Named numerical constants of the single-phase and three-phase EMT engines (October 2026 review, WS13: R160,
/// R161). Each one replaced a bare literal of exactly the same value, so no trajectory moves.
/// </summary>
public static class EmtConstants
{
    /// <summary>An event (switching, fault on or off) is applied at the first step whose time is at or past the
    /// event time minus <c>EventTimeToleranceFraction * dt</c>. The tolerance absorbs the rounding in
    /// <c>t = step * dt</c>, so an event authored exactly on a step boundary is not deferred by one step. Shared
    /// by <see cref="EmtEngine"/> and <see cref="ThreePhaseEmtEngine"/>.</summary>
    public const double EventTimeToleranceFraction = 1e-6;

    /// <summary>Default closed resistance of a <see cref="SwitchElement"/> (pu).</summary>
    public const double DefaultSwitchOnResistancePu = 1e-6;

    /// <summary>Default open resistance of a <see cref="SwitchElement"/> (pu). An open switch stays in the matrix
    /// pattern as this tiny conductance, so a switching event changes only numbers, never the ordering. Note
    /// the on/off conductance ratio with the default on-resistance is 1e15, which sets the dynamic range the LU
    /// must carry across one switching event; a much larger ratio would start to cost significant digits.</summary>
    public const double DefaultSwitchOffResistancePu = 1e9;

    /// <summary>Conductance (pu) of a three-phase shunt fault while it is OPEN (before application or after
    /// clearing). The fault stays in the pattern at this tiny value for the same reason as an open switch.</summary>
    public const double OpenFaultConductance = 1e-9;

    /// <summary>Floor (pu) on a converter's terminal-voltage magnitude where it is used as a divisor: the PLL gain
    /// normalisation at start-up and the measured-current readout <c>I = P / V</c> of a grid-forming unit.</summary>
    public const double MinVoltageForControlPu = 1e-3;

    /// <summary>A grid-forming converter's current limit is armed only after <c>LimitArmingRampMultiple *
    /// StartupRampSeconds + LimitArmingMarginSeconds</c>, so the start-up transient never trips the limit.</summary>
    public const double LimitArmingRampMultiple = 2.0;

    /// <summary>See <see cref="LimitArmingRampMultiple"/> (seconds).</summary>
    public const double LimitArmingMarginSeconds = 0.05;

    /// <summary>Hysteresis of a grid-forming converter's current limit: once limited, the unit returns to
    /// voltage-source mode only when the unlimited current falls below this fraction of Imax.</summary>
    public const double GfmLimitReleaseFraction = 0.95;

    /// <summary>An unlimited current phasor at or below this magnitude (pu) has no direction to scale onto the
    /// limit, so the limited current is set to zero.</summary>
    public const double ZeroCurrentMagnitudePu = 1e-9;
}

/// <summary>
/// D-DOC-024: thrown by <see cref="EmtEngine"/> when <see cref="EmtOptions.RefuseNonPassive"/> is set and the network
/// holds a non-passive element (a series arm with a negative resistance, or a shunt with a negative conductance). It is
/// an <see cref="ArgumentException"/> like every other refused network, so existing handlers still catch it; a caller
/// that wants to offer the way round (the CLI's <c>--allow-non-passive</c>) can tell it apart.
/// </summary>
public sealed class EmtNonPassiveNetworkException : ArgumentException
{
    /// <summary>Create the refusal; <paramref name="elements"/> are the offending elements in plain words.</summary>
    public EmtNonPassiveNetworkException(string message, IReadOnlyList<string> elements, string paramName)
        : base(message, paramName)
    {
        Elements = elements;
    }

    /// <summary>Every non-passive element, in plain words (the message names at most the first ten).</summary>
    public IReadOnlyList<string> Elements { get; }
}
