using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The first <b>varying-<c>E'q</c></b> synchronous machine of the standard model library - a one-axis
/// (flux-decay) machine on a single-machine-infinite-bus (SMIB) network, the step up from the classical
/// constant-<c>E'</c> model (<see cref="Distributed.ClassicalMultiMachine"/>). The transient EMF <c>E'q</c>
/// is now a state driven by the field voltage <c>Efd</c> through the field flux-linkage decay, so an AVR
/// finally has a field circuit to act on (see the <see cref="Sexs"/> coupling below).
///
/// <para>Three states per machine (per-unit, angles in rad, times in seconds):</para>
/// <code>
///   ddelta/dt   = omega_s * omega                                  (rotor angle)
///   domega/dt   = (Pm - Pe - D*omega) / (2 H)                      (swing, omega = pu speed deviation)
///   T'do dE'q/dt = Efd - E'q - (Xd - X'd) * Id                     (field flux-linkage decay)
/// </code>
/// The stator is algebraic (Rs = 0, no stator transients): <c>Vd = Xq*Iq</c>, <c>Vq = E'q - X'd*Id</c>,
/// and against an infinite bus <c>Vinf&#8736;0</c> through the external reactance <c>Xe</c> the dq currents
/// have the closed form
/// <code>
///   Id = (E'q - Vinf*cos(delta)) / (X'd + Xe)
///   Iq =        Vinf*sin(delta)  / (Xq  + Xe)
/// </code>
/// so there is <b>no iterative network solve</b> - the SMIB interface is a two-line linear solve, exact and
/// deterministic. The air-gap power is <c>Pe = E'q*Iq + (Xq - X'd)*Id*Iq</c>, the second term the salient-
/// pole reluctance power (zero for a round rotor <c>Xq = X'd</c>).
///
/// <para>The field flux-decay equation is, over a step with <c>Id</c> and <c>Efd</c> held, exactly a
/// first-order lag <c>T'do dE'q/dt = u - E'q</c> with input <c>u = Efd - (Xd - X'd)*Id</c>, so it advances
/// on the same <b>exact zero-order-hold</b> <see cref="FirstOrderLag"/> as the rest of the library - the
/// open-circuit field response is machine-epsilon exact. The rotor swing advances by RK4 with <c>E'q</c>
/// frozen across the sub-steps (operator splitting, the same discipline the governor uses in
/// <see cref="GovernedMultiMachine"/>).</para>
///
/// <para>It collapses onto the classical model as a reduction identity: with <c>Xd = X'd</c> the flux-decay
/// driver term vanishes, so a held field keeps <c>E'q</c> constant, and with <c>Xq = X'd</c> as well the
/// reluctance term vanishes and <c>Pe = E'q*Vinf*sin(delta)/(X'd + Xe)</c> is the classical SMIB power-angle
/// curve. That constant-<c>E'</c>, undamped swing conserves its energy and oscillates at the analytic mode
/// frequency, the oracles this model is validated against.</para>
/// </summary>
public static class OneAxisMachine
{
    /// <summary>
    /// Machine and SMIB-network parameters, all per-unit on the machine base (times in seconds). Single-field
    /// constraints are enforced on construction (fail-loud); the derived reactance sums <c>X'd + Xe</c> and
    /// <c>Xq + Xe</c> are then positive by construction.
    /// </summary>
    public sealed record Parameters(
        double InertiaSeconds, double DampingPu,
        double Xd, double Xdp, double Xq, double Tdop,
        double ExternalReactance, double InfiniteBusVoltagePu)
    {
        /// <summary>Inertia constant H (s); must be &gt; 0 (the swing divides by 2H).</summary>
        public double InertiaSeconds { get; } = InertiaSeconds > 0.0
            ? InertiaSeconds : throw new ArgumentOutOfRangeException(nameof(InertiaSeconds), "H must be > 0.");
        /// <summary>
        /// Synchronous d-axis reactance Xd (pu); must be finite, &gt; 0 and at least <see cref="Xdp"/>.
        /// <para>N321 (LF7, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the D-DOC-056 rule that
        /// SubtransientMachine applies and N266 gave TwoAxisMachine, now here too. Xd was the one reactance of this model
        /// that was not validated, although it drives the field equation as <c>Xd - X'd</c> (the flux-decay driver
        /// <c>(Xd - X'd) Id</c>, and the equilibrium field <c>Efd = E'q + (Xd - X'd) Id</c>): a zero, negative, NaN or
        /// infinite value, or one below X'd, flips or poisons that driver and gives a machine that is not a synchronous
        /// machine, with no error. The order Xd &gt;= X'd is the standard one (IEEE Std 1110; the GENROU / GENSAL data
        /// rules); equality is allowed, which is how the classical reduction identity Xd = X'd is written (this model's
        /// own reduction tests use it). Every valid machine is built exactly as before, so no number moves.</para>
        /// </summary>
        public double Xd { get; } = double.IsFinite(Xd) && Xd > 0.0 && Xd >= Xdp
            ? Xd : throw new ArgumentOutOfRangeException(nameof(Xd), Xd,
                $"Xd must be a finite number > 0 and at least X'd (got Xd = {Xd}, X'd = {Xdp}).");
        /// <summary>Transient d-axis reactance X'd (pu); must be &gt; 0.</summary>
        public double Xdp { get; } = Xdp > 0.0
            ? Xdp : throw new ArgumentOutOfRangeException(nameof(Xdp), "X'd must be > 0.");
        /// <summary>Synchronous q-axis reactance Xq (pu); must be &gt; 0.</summary>
        public double Xq { get; } = Xq > 0.0
            ? Xq : throw new ArgumentOutOfRangeException(nameof(Xq), "Xq must be > 0.");
        /// <summary>Open-circuit field time constant T'do (s); must be &gt; 0.</summary>
        public double Tdop { get; } = Tdop > 0.0
            ? Tdop : throw new ArgumentOutOfRangeException(nameof(Tdop), "T'do must be > 0.");
        /// <summary>External reactance Xe to the infinite bus (pu); must be &gt;= 0.</summary>
        public double ExternalReactance { get; } = ExternalReactance >= 0.0
            ? ExternalReactance : throw new ArgumentOutOfRangeException(nameof(ExternalReactance), "Xe must be >= 0.");
    }

    /// <summary>Trajectory: one entry per sample of each state and the derived terminal quantities.</summary>
    public sealed record Trace(
        IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double> DeltaRad,
        IReadOnlyList<double> SpeedPu,
        IReadOnlyList<double> EqpPu,
        IReadOnlyList<double> TerminalVoltagePu,
        IReadOnlyList<double> ElectricalPowerPu);

    /// <summary>Closed-form dq stator currents at a state (Rs = 0), from the SMIB algebraic network.</summary>
    public static (double Id, double Iq) Currents(Parameters p, double deltaRad, double eqp)
    {
        double id = (eqp - p.InfiniteBusVoltagePu * DetMath.Cos(deltaRad)) / (p.Xdp + p.ExternalReactance);
        double iq = (p.InfiniteBusVoltagePu * DetMath.Sin(deltaRad)) / (p.Xq + p.ExternalReactance);
        return (id, iq);
    }

    /// <summary>Air-gap electrical power <c>Pe = E'q*Iq + (Xq - X'd)*Id*Iq</c> (pu) at a state.</summary>
    public static double ElectricalPower(Parameters p, double deltaRad, double eqp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqp);
        return eqp * iq + (p.Xq - p.Xdp) * id * iq;
    }

    /// <summary>Terminal-voltage magnitude <c>sqrt(Vd^2 + Vq^2)</c> (pu), <c>Vd = Xq*Iq</c>,
    /// <c>Vq = E'q - X'd*Id</c> - the signal an AVR regulates.</summary>
    public static double TerminalVoltage(Parameters p, double deltaRad, double eqp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqp);
        double vd = p.Xq * iq;
        double vq = eqp - p.Xdp * id;
        return Math.Sqrt(vd * vd + vq * vq);
    }

    /// <summary>
    /// The field voltage and mechanical power that make <c>(delta0, omega = 0, E'q = eqp0)</c> an exact
    /// equilibrium: <c>Efd = E'q + (Xd - X'd)*Id</c> (so <c>dE'q/dt = 0</c>) and <c>Pm = Pe</c> (so
    /// <c>domega/dt = 0</c>). An undisturbed run started here is stationary, matching the classical-model
    /// initialisation contract.
    /// </summary>
    public static (double MechanicalPowerPu, double FieldVoltagePu) Equilibrium(Parameters p, double delta0, double eqp0)
    {
        (double id, _) = Currents(p, delta0, eqp0);
        double efd = eqp0 + (p.Xd - p.Xdp) * id;
        double pm = ElectricalPower(p, delta0, eqp0);
        return (pm, efd);
    }

    /// <summary>
    /// Integrate the machine from <c>(delta0, omega0, eqp0)</c> under mechanical power <paramref
    /// name="mechanicalPowerPu"/>. The field voltage is held at <paramref name="fieldVoltagePu"/> unless an
    /// <paramref name="exciter"/> is supplied, in which case it is driven each step by
    /// <c>Efd = exciter(voltageReference - Vt)</c> - the AVR closed around the field circuit (the exciter is
    /// initialised to <paramref name="fieldVoltagePu"/> so a matched setpoint is stationary). Per step the
    /// field held across it, <c>E'q</c> advances by the exact zero-order hold (Id frozen) and the rotor swing
    /// by RK4 (E'q frozen) - operator splitting.
    /// </summary>
    public static Trace Simulate(
        Parameters p,
        double delta0, double omega0, double eqp0,
        double mechanicalPowerPu, double fieldVoltagePu,
        double f0, double stepSeconds, double durationSeconds,
        Sexs? exciter = null, double voltageReferencePu = double.NaN)
    {
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "the step must be a finite number of seconds above 0 (NaN used to pass this check).");
        if (!(durationSeconds >= 0.0) || !double.IsFinite(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "the duration must be a finite number of seconds, 0 or more.");
        if (exciter is not null && double.IsNaN(voltageReferencePu))
            throw new ArgumentException("An exciter needs a finite voltageReference.", nameof(voltageReferencePu));

        double ws = 2.0 * Math.PI * f0;
        int steps = (int)Math.Round(durationSeconds / stepSeconds);

        // E'q lives in the exact-ZOH first-order lag: T'do dE'q/dt = u - E'q, u = Efd - (Xd - X'd)*Id.
        var flux = new FirstOrderLag(gain: 1.0, timeConstantSeconds: p.Tdop);
        flux.Initialize(eqp0);
        exciter?.Initialize(fieldVoltagePu);

        double delta = delta0, omega = omega0;

        var tS = new List<double>(steps + 1);
        var dS = new List<double>(steps + 1);
        var wS = new List<double>(steps + 1);
        var eS = new List<double>(steps + 1);
        var vS = new List<double>(steps + 1);
        var pS = new List<double>(steps + 1);

        for (int k = 0; k <= steps; k++)
        {
            double eqp = flux.Output;
            tS.Add(k * stepSeconds);
            dS.Add(delta);
            wS.Add(omega);
            eS.Add(eqp);
            vS.Add(TerminalVoltage(p, delta, eqp));
            pS.Add(ElectricalPower(p, delta, eqp));

            if (k == steps) break;

            // Field: the AVR reads the terminal voltage at the start of the step; held across it.
            double efd = exciter is { } ex
                ? ex.Step(voltageReferencePu - vS[k], stepSeconds)
                : fieldVoltagePu;

            // Flux decay: Id frozen at the start-of-step state; E'q advances by the exact zero-order hold.
            (double id0, _) = Currents(p, delta, eqp);
            flux.Step(efd - (p.Xd - p.Xdp) * id0, stepSeconds);

            // Rotor swing: RK4 with E'q frozen at its start-of-step value (operator splitting).
            (delta, omega) = SwingStep(p, delta, omega, eqp, mechanicalPowerPu, ws, stepSeconds);
        }

        return new Trace(tS, dS, wS, eS, vS, pS);
    }

    /// <summary>One RK4 step of the two-state rotor swing (delta, omega) with E'q held constant.</summary>
    private static (double Delta, double Omega) SwingStep(
        Parameters p, double delta, double omega, double eqp, double pm, double ws, double dt)
    {
        SwingDeriv(p, delta, omega, eqp, pm, ws, out double k1d, out double k1w);
        SwingDeriv(p, delta + 0.5 * dt * k1d, omega + 0.5 * dt * k1w, eqp, pm, ws, out double k2d, out double k2w);
        SwingDeriv(p, delta + 0.5 * dt * k2d, omega + 0.5 * dt * k2w, eqp, pm, ws, out double k3d, out double k3w);
        SwingDeriv(p, delta + dt * k3d, omega + dt * k3w, eqp, pm, ws, out double k4d, out double k4w);
        return (delta + dt / 6.0 * (k1d + 2 * k2d + 2 * k3d + k4d),
                omega + dt / 6.0 * (k1w + 2 * k2w + 2 * k3w + k4w));
    }

    private static void SwingDeriv(
        Parameters p, double delta, double omega, double eqp, double pm, double ws,
        out double dDelta, out double dOmega)
    {
        double pe = ElectricalPower(p, delta, eqp);
        dDelta = ws * omega;
        dOmega = (pm - pe - p.DampingPu * omega) / (2.0 * p.InertiaSeconds);
    }
}
