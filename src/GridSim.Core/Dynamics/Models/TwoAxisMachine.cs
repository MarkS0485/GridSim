using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The <b>two-axis</b> synchronous machine on a single-machine-infinite-bus (SMIB) network - the step up from
/// the one-axis flux-decay model (<see cref="OneAxisMachine"/>). It adds the <b>q-axis transient EMF</b>
/// <c>E'd</c> as a fourth state, driven by the q-axis flux-linkage decay, so both axes now carry transient
/// dynamics and the stator sees the transient reactances on both axes (<c>X'd</c>, <c>X'q</c>) rather than
/// <c>X'd</c> on the d-axis and the synchronous <c>Xq</c> on the q-axis.
///
/// <para>Four states per machine (per-unit, angles in rad, times in seconds):</para>
/// <code>
///   ddelta/dt    = omega_s * omega                                  (rotor angle)
///   domega/dt    = (Pm - Pe - D*omega) / (2 H)                      (swing, omega = pu speed deviation)
///   T'do dE'q/dt = Efd - E'q - (Xd - X'd) * Id                      (d-axis field flux-linkage decay)
///   T'qo dE'd/dt = -E'd + (Xq - X'q) * Iq                           (q-axis flux-linkage decay)
/// </code>
/// The stator is algebraic (Rs = 0, no stator transients), <c>Vd = E'd + X'q*Iq</c>, <c>Vq = E'q - X'd*Id</c>
/// (Anderson &amp; Fouad two-axis form), and against an infinite bus <c>Vinf&#8736;0</c> through the external
/// reactance <c>Xe</c> the dq currents have the closed form
/// <code>
///   Id = (E'q  - Vinf*cos(delta)) / (X'd + Xe)
///   Iq = (Vinf*sin(delta) - E'd)  / (X'q + Xe)
/// </code>
/// so there is <b>no iterative network solve</b> - the SMIB interface is a two-line linear solve, exact and
/// deterministic. The air-gap power is <c>Pe = E'd*Id + E'q*Iq + (X'q - X'd)*Id*Iq</c>, the last term the
/// transient-saliency reluctance power.
///
/// <para>Each flux state advances, over a step with <c>Id</c>/<c>Iq</c> and <c>Efd</c> held, exactly as a
/// first-order lag on the same <b>exact zero-order-hold</b> <see cref="FirstOrderLag"/> as the rest of the
/// library: <c>E'q</c> with input <c>Efd - (Xd - X'd)*Id</c>, and <c>E'd</c> with input
/// <c>(Xq - X'q)*Iq</c>. The rotor swing advances by RK4 with both EMFs frozen across the sub-steps (operator
/// splitting, the same discipline the governor and one-axis models use).</para>
///
/// <para>It collapses onto <see cref="OneAxisMachine"/> as a reduction identity: with <c>X'q = Xq</c> the
/// q-axis flux-decay driver vanishes, so an <c>E'd</c> started at its equilibrium <c>0</c> stays <c>0</c>, the
/// stator q-axis reduces to <c>Vd = Xq*Iq</c>, and both the currents and <c>Pe = E'q*Iq + (Xq - X'd)*Id*Iq</c>
/// are exactly the one-axis machine's. Its steady state reproduces the one-axis synchronous q-axis:
/// <c>E'd_eq = (Xq - X'q)*Vinf*sin(delta)/(Xq + Xe)</c> makes <c>Iq = Vinf*sin(delta)/(Xq + Xe)</c>, the same
/// current the one-axis model carries.</para>
/// </summary>
public static class TwoAxisMachine
{
    /// <summary>
    /// Machine and SMIB-network parameters, all per-unit on the machine base (times in seconds). Constraints
    /// are enforced on construction (fail-loud); the derived reactance sums <c>X'd + Xe</c> and <c>X'q + Xe</c>
    /// are then positive by construction.
    /// </summary>
    public sealed record Parameters(
        double InertiaSeconds, double DampingPu,
        double Xd, double Xdp, double Xq, double Xqp, double Tdop, double Tqop,
        double ExternalReactance, double InfiniteBusVoltagePu)
    {
        /// <summary>Inertia constant H (s); must be &gt; 0 (the swing divides by 2H).</summary>
        public double InertiaSeconds { get; } = InertiaSeconds > 0.0
            ? InertiaSeconds : throw new ArgumentOutOfRangeException(nameof(InertiaSeconds), "H must be > 0.");
        /// <summary>
        /// Synchronous d-axis reactance Xd (pu); must be finite, &gt; 0 and at least <see cref="Xdp"/>.
        /// <para>N266 (LF5, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the D-DOC-056 rule, which
        /// SubtransientMachine already applies, now applied here too. Xd and Xq were the only reactances not validated,
        /// although both drive the flux equations as <c>Xd - X'd</c> and <c>Xq - X'q</c> (and Xq the equilibrium's
        /// <c>Iq = Vinf sin(delta) / (Xq + Xe)</c>): a zero, negative or NaN value, or one below its transient reactance,
        /// flips the sign of the flux-decay driver and gives a machine that is not a synchronous machine, with no error.
        /// The order Xd &gt;= X'd and Xq &gt;= X'q is the standard one (IEEE Std 1110; the GENROU / GENSAL data rules);
        /// equality is allowed, which is how the one-axis reduction identity X'q = Xq is written. Every valid machine is
        /// built exactly as before, so no number moves.</para>
        /// </summary>
        public double Xd { get; } = double.IsFinite(Xd) && Xd > 0.0 && Xd >= Xdp
            ? Xd : throw new ArgumentOutOfRangeException(nameof(Xd), Xd,
                $"Xd must be a finite number > 0 and at least X'd (got Xd = {Xd}, X'd = {Xdp}).");
        /// <summary>Synchronous q-axis reactance Xq (pu); must be finite, &gt; 0 and at least <see cref="Xqp"/>
        /// (N266; see <see cref="Xd"/>).</summary>
        public double Xq { get; } = double.IsFinite(Xq) && Xq > 0.0 && Xq >= Xqp
            ? Xq : throw new ArgumentOutOfRangeException(nameof(Xq), Xq,
                $"Xq must be a finite number > 0 and at least X'q (got Xq = {Xq}, X'q = {Xqp}).");
        /// <summary>Transient d-axis reactance X'd (pu); must be &gt; 0.</summary>
        public double Xdp { get; } = Xdp > 0.0
            ? Xdp : throw new ArgumentOutOfRangeException(nameof(Xdp), "X'd must be > 0.");
        /// <summary>Transient q-axis reactance X'q (pu); must be &gt; 0.</summary>
        public double Xqp { get; } = Xqp > 0.0
            ? Xqp : throw new ArgumentOutOfRangeException(nameof(Xqp), "X'q must be > 0.");
        /// <summary>Open-circuit d-axis field time constant T'do (s); must be &gt; 0.</summary>
        public double Tdop { get; } = Tdop > 0.0
            ? Tdop : throw new ArgumentOutOfRangeException(nameof(Tdop), "T'do must be > 0.");
        /// <summary>Open-circuit q-axis time constant T'qo (s); must be &gt; 0.</summary>
        public double Tqop { get; } = Tqop > 0.0
            ? Tqop : throw new ArgumentOutOfRangeException(nameof(Tqop), "T'qo must be > 0.");
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
        IReadOnlyList<double> EdpPu,
        IReadOnlyList<double> TerminalVoltagePu,
        IReadOnlyList<double> ElectricalPowerPu);

    /// <summary>Closed-form dq stator currents at a state (Rs = 0), from the SMIB algebraic network.</summary>
    public static (double Id, double Iq) Currents(Parameters p, double deltaRad, double eqp, double edp)
    {
        double id = (eqp - p.InfiniteBusVoltagePu * DetMath.Cos(deltaRad)) / (p.Xdp + p.ExternalReactance);
        double iq = (p.InfiniteBusVoltagePu * DetMath.Sin(deltaRad) - edp) / (p.Xqp + p.ExternalReactance);
        return (id, iq);
    }

    /// <summary>Air-gap electrical power <c>Pe = E'd*Id + E'q*Iq + (X'q - X'd)*Id*Iq</c> (pu) at a state.</summary>
    public static double ElectricalPower(Parameters p, double deltaRad, double eqp, double edp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqp, edp);
        return edp * id + eqp * iq + (p.Xqp - p.Xdp) * id * iq;
    }

    /// <summary>Terminal-voltage magnitude <c>sqrt(Vd^2 + Vq^2)</c> (pu), <c>Vd = E'd + X'q*Iq</c>,
    /// <c>Vq = E'q - X'd*Id</c> - the signal an AVR regulates.</summary>
    public static double TerminalVoltage(Parameters p, double deltaRad, double eqp, double edp)
    {
        (double id, double iq) = Currents(p, deltaRad, eqp, edp);
        double vd = edp + p.Xqp * iq;
        double vq = eqp - p.Xdp * id;
        return Math.Sqrt(vd * vd + vq * vq);
    }

    /// <summary>
    /// The q-axis transient EMF <c>E'd</c> that makes the q-axis flux decay stationary at <paramref
    /// name="delta0"/>: <c>E'd = (Xq - X'q)*Vinf*sin(delta)/(Xq + Xe)</c> (the closed-form fixed point of
    /// <c>E'd = (Xq - X'q)*Iq</c> with <c>Iq</c> depending on <c>E'd</c>). Starting <c>E'd</c> here makes an
    /// undisturbed run stationary on the q-axis.
    /// </summary>
    public static double EquilibriumEdp(Parameters p, double delta0)
    {
        double iqSync = p.InfiniteBusVoltagePu * DetMath.Sin(delta0) / (p.Xq + p.ExternalReactance);
        return (p.Xq - p.Xqp) * iqSync;
    }

    /// <summary>
    /// The field voltage, mechanical power and initial <c>E'd</c> that make
    /// <c>(delta0, omega = 0, E'q = eqp0, E'd = E'd_eq)</c> an exact equilibrium: <c>E'd_eq</c> from
    /// <see cref="EquilibriumEdp"/> (so <c>dE'd/dt = 0</c>), <c>Efd = E'q + (Xd - X'd)*Id</c> (so
    /// <c>dE'q/dt = 0</c>) and <c>Pm = Pe</c> (so <c>domega/dt = 0</c>). An undisturbed run started here is
    /// stationary.
    /// </summary>
    public static (double MechanicalPowerPu, double FieldVoltagePu, double InitialEdpPu) Equilibrium(
        Parameters p, double delta0, double eqp0)
    {
        double edp = EquilibriumEdp(p, delta0);
        (double id, _) = Currents(p, delta0, eqp0, edp);
        double efd = eqp0 + (p.Xd - p.Xdp) * id;
        double pm = ElectricalPower(p, delta0, eqp0, edp);
        return (pm, efd, edp);
    }

    /// <summary>
    /// Integrate the machine from <c>(delta0, omega0, eqp0, edp0)</c> under mechanical power <paramref
    /// name="mechanicalPowerPu"/>. The field voltage is held at <paramref name="fieldVoltagePu"/> unless an
    /// <paramref name="exciter"/> is supplied, in which case it is driven each step by
    /// <c>Efd = exciter(voltageReference - Vt)</c>. Per step, with the field held across it, <c>E'q</c> and
    /// <c>E'd</c> advance by the exact zero-order hold (currents frozen) and the rotor swing by RK4 (both EMFs
    /// frozen) - operator splitting.
    /// </summary>
    public static Trace Simulate(
        Parameters p,
        double delta0, double omega0, double eqp0, double edp0,
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

        // Each transient EMF lives in an exact-ZOH first-order lag: T dE/dt = u - E.
        var fluxQ = new FirstOrderLag(gain: 1.0, timeConstantSeconds: p.Tdop);   // E'q, input Efd - (Xd-X'd)Id
        var fluxD = new FirstOrderLag(gain: 1.0, timeConstantSeconds: p.Tqop);   // E'd, input (Xq-X'q)Iq
        fluxQ.Initialize(eqp0);
        fluxD.Initialize(edp0);
        exciter?.Initialize(fieldVoltagePu);

        double delta = delta0, omega = omega0;

        var tS = new List<double>(steps + 1);
        var dS = new List<double>(steps + 1);
        var wS = new List<double>(steps + 1);
        var eqS = new List<double>(steps + 1);
        var edS = new List<double>(steps + 1);
        var vS = new List<double>(steps + 1);
        var pS = new List<double>(steps + 1);

        for (int k = 0; k <= steps; k++)
        {
            double eqp = fluxQ.Output, edp = fluxD.Output;
            tS.Add(k * stepSeconds);
            dS.Add(delta);
            wS.Add(omega);
            eqS.Add(eqp);
            edS.Add(edp);
            vS.Add(TerminalVoltage(p, delta, eqp, edp));
            pS.Add(ElectricalPower(p, delta, eqp, edp));

            if (k == steps) break;

            // Field: the AVR reads the terminal voltage at the start of the step; held across it.
            double efd = exciter is { } ex
                ? ex.Step(voltageReferencePu - vS[k], stepSeconds)
                : fieldVoltagePu;

            // Flux decay: currents frozen at the start-of-step state; each EMF advances by the exact ZOH.
            (double id0, double iq0) = Currents(p, delta, eqp, edp);
            fluxQ.Step(efd - (p.Xd - p.Xdp) * id0, stepSeconds);
            fluxD.Step((p.Xq - p.Xqp) * iq0, stepSeconds);

            // Rotor swing: RK4 with both EMFs frozen at their start-of-step values (operator splitting).
            (delta, omega) = SwingStep(p, delta, omega, eqp, edp, mechanicalPowerPu, ws, stepSeconds);
        }

        return new Trace(tS, dS, wS, eqS, edS, vS, pS);
    }

    /// <summary>One RK4 step of the two-state rotor swing (delta, omega) with both EMFs held constant.</summary>
    private static (double Delta, double Omega) SwingStep(
        Parameters p, double delta, double omega, double eqp, double edp, double pm, double ws, double dt)
    {
        SwingDeriv(p, delta, omega, eqp, edp, pm, ws, out double k1d, out double k1w);
        SwingDeriv(p, delta + 0.5 * dt * k1d, omega + 0.5 * dt * k1w, eqp, edp, pm, ws, out double k2d, out double k2w);
        SwingDeriv(p, delta + 0.5 * dt * k2d, omega + 0.5 * dt * k2w, eqp, edp, pm, ws, out double k3d, out double k3w);
        SwingDeriv(p, delta + dt * k3d, omega + dt * k3w, eqp, edp, pm, ws, out double k4d, out double k4w);
        return (delta + dt / 6.0 * (k1d + 2 * k2d + 2 * k3d + k4d),
                omega + dt / 6.0 * (k1w + 2 * k2w + 2 * k3w + k4w));
    }

    private static void SwingDeriv(
        Parameters p, double delta, double omega, double eqp, double edp, double pm, double ws,
        out double dDelta, out double dOmega)
    {
        double pe = ElectricalPower(p, delta, eqp, edp);
        dDelta = ws * omega;
        dOmega = (pm - pe - p.DampingPu * omega) / (2.0 * p.InertiaSeconds);
    }
}
