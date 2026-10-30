using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the varying-E'q one-axis (flux-decay) SMIB machine (<see cref="OneAxisMachine"/>) against
/// oracles that hold to the machine-epsilon standard:
///   - EQUILIBRIUM: a machine (and an AVR closed around it) started at a fixed point stays stationary;
///   - EXACT FLUX DECAY: on open circuit with Xd = X'd the field-voltage step response is the analytic
///     exponential to machine epsilon (the field state rides the exact zero-order-hold first-order lag);
///   - REDUCTION TO CLASSICAL: with Xd = X'd = Xq and a held field, E'q is constant and the machine is the
///     classical SMIB - its undamped swing conserves energy and oscillates at the analytic mode frequency;
///   - SALIENCY: the reluctance-power term is present and matches its closed form;
///   - DETERMINISM: two identical runs are bit-for-bit equal.
/// </summary>
public class OneAxisMachineTests
{
    private const double F0 = 50.0;
    private static readonly double Ws = 2.0 * Math.PI * F0;

    // A representative salient-pole machine on a stiff SMIB network.
    private static OneAxisMachine.Parameters Salient() => new(
        InertiaSeconds: 4.0, DampingPu: 0.0,
        Xd: 1.8, Xdp: 0.3, Xq: 1.7, Tdop: 6.0,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    // Round-rotor, no flux-decay driver (Xd = X'd) => collapses to the classical constant-E' SMIB.
    private static OneAxisMachine.Parameters RoundClassical() => new(
        InertiaSeconds: 4.0, DampingPu: 0.0,
        Xd: 0.3, Xdp: 0.3, Xq: 0.3, Tdop: 6.0,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    // ---- Equilibrium: started at a fixed point, the machine is stationary ----
    [Fact]
    public void Started_in_equilibrium_the_machine_is_stationary()
    {
        var p = Salient();
        const double delta0 = 0.5, eqp0 = 1.1;
        (double pm, double efd) = OneAxisMachine.Equilibrium(p, delta0, eqp0);

        OneAxisMachine.Trace tr = OneAxisMachine.Simulate(
            p, delta0, omega0: 0.0, eqp0, pm, efd, F0, stepSeconds: 0.005, durationSeconds: 10.0);

        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-9);        // no speed drift
        Assert.Equal(delta0, tr.DeltaRad[last], 9);            // no angle drift
        Assert.Equal(eqp0, tr.EqpPu[last], 10);               // field flux at rest
    }

    // ---- Exact flux decay: open-circuit field step follows the analytic exponential to epsilon ----
    [Fact]
    public void Open_circuit_field_step_follows_the_exact_flux_decay_exponential()
    {
        // Vinf = 0 (dead bus => Iq = 0 => Pe = 0, rotor pinned) and Xd = X'd (the (Xd-X'd)Id driver vanishes),
        // so E'q obeys T'do dE'q/dt = Efd - E'q exactly: a pure first-order lag on the field voltage.
        var p = new OneAxisMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 0.3, Xdp: 0.3, Xq: 0.3, Tdop: 5.0,
            ExternalReactance: 1.0, InfiniteBusVoltagePu: 0.0);

        const double eqp0 = 1.0, efdStep = 1.5;   // field stepped above the initial flux
        (double pm, _) = OneAxisMachine.Equilibrium(p, delta0: 0.0, eqp0);   // pm = 0 here (Vinf = 0)

        OneAxisMachine.Trace tr = OneAxisMachine.Simulate(
            p, delta0: 0.0, omega0: 0.0, eqp0, pm, fieldVoltagePu: efdStep,
            F0, stepSeconds: 0.01, durationSeconds: 15.0);

        for (int k = 0; k < tr.EqpPu.Count; k++)
        {
            double t = tr.TimeSeconds[k];
            double analytic = efdStep + (eqp0 - efdStep) * Math.Exp(-t / p.Tdop);
            Assert.Equal(analytic, tr.EqpPu[k], 10);          // machine-epsilon exact at every sample
        }
        int last = tr.DeltaRad.Count - 1;
        Assert.Equal(0.0, tr.DeltaRad[last], 12);             // rotor pinned (Pe = 0, Pm = 0)
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-12);
    }

    // ---- Reduction to classical: round-rotor, constant field => energy-conserving classical swing ----
    [Fact]
    public void Round_rotor_constant_field_swing_conserves_energy()
    {
        var p = RoundClassical();
        const double eqp0 = 1.1, field = 1.1, pm = 1.0;       // held field keeps E'q = 1.1 (Xd = X'd)
        double xTot = p.Xdp + p.ExternalReactance;            // 0.5
        double pMax = eqp0 * p.InfiniteBusVoltagePu / xTot;    // 2.2

        // Released from an angle away from equilibrium => a genuine (nonlinear) swing.
        OneAxisMachine.Trace tr = OneAxisMachine.Simulate(
            p, delta0: 0.2, omega0: 0.0, eqp0, pm, field, F0, stepSeconds: 0.001, durationSeconds: 5.0);

        // Conserved swing energy V = H*ws*omega^2 - Pm*delta - Pmax*cos(delta) (D = 0).
        double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
        for (int k = 0; k < tr.DeltaRad.Count; k++)
        {
            double w = tr.SpeedPu[k], del = tr.DeltaRad[k];
            double v = p.InertiaSeconds * Ws * w * w - pm * del - pMax * Math.Cos(del);
            vMin = Math.Min(vMin, v);
            vMax = Math.Max(vMax, v);
        }
        Assert.True(vMax - vMin < 1e-4, $"energy drift {vMax - vMin:E3} exceeds tolerance");

        // And E'q really did stay put (the reduction premise), to near machine epsilon.
        Assert.Equal(eqp0, tr.EqpPu[tr.EqpPu.Count - 1], 10);
    }

    // ---- Reduction to classical: small-signal oscillation matches the analytic SMIB mode ----
    [Fact]
    public void Small_signal_oscillation_matches_the_analytic_SMIB_mode()
    {
        var p = RoundClassical();
        const double eqp0 = 1.1, deltaEq = 0.5;
        (double pm, double efd) = OneAxisMachine.Equilibrium(p, deltaEq, eqp0);
        double xTot = p.Xdp + p.ExternalReactance;
        double pMax = eqp0 * p.InfiniteBusVoltagePu / xTot;

        // Analytic linearised mode: wn^2 = ws * Pmax * cos(delta_eq) / (2H).
        double wn = Math.Sqrt(Ws * pMax * Math.Cos(deltaEq) / (2.0 * p.InertiaSeconds));
        double period = 2.0 * Math.PI / wn;

        const double dt = 0.0005;
        OneAxisMachine.Trace tr = OneAxisMachine.Simulate(
            p, delta0: deltaEq + 1e-3, omega0: 0.0, eqp0, pm, efd, F0, dt, durationSeconds: 2.0);

        // t = 0 is a peak (released from rest above equilibrium); the first interior maximum is one period on.
        double measured = double.NaN;
        for (int k = 2; k < tr.DeltaRad.Count - 1; k++)
        {
            double a = tr.DeltaRad[k - 1], b = tr.DeltaRad[k], c = tr.DeltaRad[k + 1];
            if (b > a && b > c)
            {
                double offset = 0.5 * (a - c) / (a - 2.0 * b + c);   // parabolic sub-sample peak
                measured = (k + offset) * dt;
                break;
            }
        }
        Assert.False(double.IsNaN(measured), "no oscillation peak found");
        Assert.True(Math.Abs(measured - period) < 0.01 * period,
            $"measured period {measured:F4}s vs analytic {period:F4}s");
    }

    // ---- Saliency: the reluctance-power term is present and matches its closed form ----
    [Fact]
    public void Salient_pole_reluctance_power_matches_the_closed_form()
    {
        var p = Salient();
        const double delta = 0.6, eqp = 0.0;   // zero field: only the reluctance term remains
        (double id, double iq) = OneAxisMachine.Currents(p, delta, eqp);
        double expected = (p.Xq - p.Xdp) * id * iq;

        Assert.True(expected == OneAxisMachine.ElectricalPower(p, delta, eqp));  // bit-exact algebraic identity
        Assert.True(Math.Abs(expected) > 1e-3);                                  // and non-trivially present
    }

    // ---- AVR in the loop: SEXS closed around the field circuit holds the regulated setpoint ----
    [Fact]
    public void Avr_holds_the_machine_stationary_at_its_regulated_setpoint()
    {
        var p = Salient();
        const double delta0 = 0.5, eqp0 = 1.1, k = 100.0;
        (double pm, double efd0) = OneAxisMachine.Equilibrium(p, delta0, eqp0);
        double vt0 = OneAxisMachine.TerminalVoltage(p, delta0, eqp0);

        var exciter = new Sexs(leadSeconds: 0.1, lagSeconds: 1.0, gain: k, exciterTimeSeconds: 0.02);
        double vref = vt0 + efd0 / k;   // steady Efd = clamp(K*Verr) => Verr0 = Efd0/K holds the setpoint

        OneAxisMachine.Trace tr = OneAxisMachine.Simulate(
            p, delta0, omega0: 0.0, eqp0, pm, efd0, F0, stepSeconds: 0.005, durationSeconds: 10.0,
            exciter, vref);

        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-6);
        Assert.Equal(delta0, tr.DeltaRad[last], 6);
        Assert.Equal(vt0, tr.TerminalVoltagePu[last], 5);     // terminal voltage held at the regulated point
    }

    // ---- Determinism: two identical runs are bit-for-bit equal ----
    [Fact]
    public void Two_identical_runs_are_bit_for_bit_equal()
    {
        var p = Salient();
        const double delta0 = 0.35, eqp0 = 1.05, k = 50.0;
        (double pm, double efd0) = OneAxisMachine.Equilibrium(p, delta0, eqp0);
        double vt0 = OneAxisMachine.TerminalVoltage(p, delta0, eqp0);
        double vref = vt0 + efd0 / k;

        OneAxisMachine.Trace RunOnce() => OneAxisMachine.Simulate(
            p, delta0, omega0: 0.01, eqp0, pm, efd0, F0, stepSeconds: 0.002, durationSeconds: 3.0,
            new Sexs(0.1, 1.0, k, 0.02), vref);

        OneAxisMachine.Trace a = RunOnce(), b = RunOnce();
        for (int i = 0; i < a.DeltaRad.Count; i++)
        {
            Assert.True(a.DeltaRad[i] == b.DeltaRad[i]);
            Assert.True(a.SpeedPu[i] == b.SpeedPu[i]);
            Assert.True(a.EqpPu[i] == b.EqpPu[i]);
        }
    }
}
