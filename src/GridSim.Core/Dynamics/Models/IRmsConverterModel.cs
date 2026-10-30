using System;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// A converter-interfaced unit as the RMS kernel (<see cref="MultiMachineSubtransient"/>) drives it each step: the
/// current it injects at a trial terminal voltage (inside the converter / network fixed point, controls held), the
/// current it commits at the converged voltage, its terminal current, and the advance of its controls over the step
/// from the start-of-step terminal voltage (and, for a model that measures them, the voltages of the other retained
/// converter nodes, such as a plant controller's regulated bus).
///
/// <para>Two implementations: <see cref="LegacyRmsConverterModel"/>, the historical REGC_A/REEC-style converter
/// (<see cref="RmsConverterState"/>), forwarded call for call with exactly the arguments the kernel passed before
/// this interface existed, so its numbers are bit for bit unchanged; and
/// <see cref="Wecc.WeccConverterState"/>, the WECC second-generation model chain bound from a DYR file.</para>
/// </summary>
public interface IRmsConverterModel
{
    /// <summary>The injected current phasor (system per unit, network frame) at trial terminal voltage
    /// <paramref name="v"/>, committing nothing.</summary>
    Complex TrialCurrent(Complex v);

    /// <summary>The injected current at the converged terminal voltage <paramref name="v"/>; locks the angle and records
    /// the quantities the traces and the next control step read.</summary>
    Complex Commit(Complex v);

    /// <summary>The unit's terminal current for a node <paramref name="injection"/> at terminal voltage
    /// <paramref name="v"/> (they differ only for a unit with a Norton admittance in the network).</summary>
    Complex TerminalCurrent(Complex injection, Complex v);

    /// <summary>Advance the controls one step of <paramref name="dt"/> from the start-of-step terminal voltage
    /// <paramref name="v"/>. <paramref name="nodeVoltages"/> are the start-of-step voltages of every retained converter
    /// node, in the network's converter-node order.</summary>
    void Step(double t, Complex v, Complex[] nodeVoltages, double dt);

    /// <summary>Active current injected (system pu) at the last commit.</summary>
    double ActiveCurrent { get; }

    /// <summary>Reactive current injected, exporting positive (system pu), at the last commit.</summary>
    double ReactiveCurrent { get; }

    /// <summary>True while the unit's voltage-dip (fault ride-through) logic is active.</summary>
    bool InDip { get; }
}

/// <summary>
/// A converter model that solves its own network interface exactly against the Thevenin view of its node, so the kernel's
/// converter / network fixed point converges where plain substitution would not (a voltage-source unit with a Norton
/// admittance whose current limit or blocking acts on a weak node: DECISIONS D36 of the WECC expansion).
///
/// <para>From the second pass of each step's iteration on, the kernel passes each such unit
/// <c>a = V - Zdd I_own</c> (the node voltage of the last solve with the unit's own last injection removed) and
/// <c>Zdd</c> (the driving-point impedance of the network at the unit's node, from one solve with a unit injection, once
/// per network); the unit returns the injection that satisfies <c>V = a + Zdd I(V)</c> for its own model. Other units in
/// the same pass are held (block Jacobi), and the kernel's convergence test is unchanged. A model whose
/// <see cref="SolvesWithNetwork"/> is false takes the historical path, call for call.</para>
/// </summary>
public interface IRmsNetworkSolvedConverterModel : IRmsConverterModel
{
    /// <summary>True when the kernel should use <see cref="NetworkSolvedCurrent"/> for this unit at every step.</summary>
    bool SolvesWithNetwork { get; }

    /// <summary>True when <see cref="NetworkSolvedCurrent"/> works for this unit even though <see cref="SolvesWithNetwork"/>
    /// is false: the kernel then uses it only to rescue a step whose plain substitution hit the iteration cap (D38), so
    /// every step that converges keeps the historical numbers.</summary>
    bool CanSolveWithNetwork { get; }

    /// <summary>The injection (system per unit, network frame) that satisfies <c>V = a + zdd I(V)</c> exactly, committing
    /// nothing but the frame choice the next trial and commit use.</summary>
    Complex NetworkSolvedCurrent(Complex a, Complex zdd);
}

/// <summary>
/// The historical converter (<see cref="RmsConverterState"/>) behind <see cref="IRmsConverterModel"/>: each call is the
/// call the kernel made before, with the same arguments (the magnitude and the DetMath phase of the same phasor), so
/// every number is the same bits.
/// </summary>
public sealed class LegacyRmsConverterModel : IRmsConverterModel
{
    private readonly RmsConverterState _s;

    public LegacyRmsConverterModel(RmsConverterState state) => _s = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>The wrapped state.</summary>
    public RmsConverterState State => _s;

    public Complex TrialCurrent(Complex v) => _s.TrialCurrent(v.Magnitude, DetMath.Phase(v));

    public Complex Commit(Complex v) => _s.Current(v.Magnitude, DetMath.Phase(v), lockAngle: true);

    public Complex TerminalCurrent(Complex injection, Complex v) => _s.TerminalCurrent(injection, v);

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt) => _s.Step(t, v, dt);

    public double ActiveCurrent => _s.ActiveCurrent;

    public double ReactiveCurrent => _s.ReactiveCurrent;

    public bool InDip => _s.InDip;
}
