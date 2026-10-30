using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the two-axis SMIB machine (<see cref="TwoAxisMachine"/>) against oracles that hold to the
/// machine-epsilon standard:
///   - REDUCTION TO ONE-AXIS: with X'q = Xq the q-axis transient vanishes (E'd stays 0) and the machine is
///     exactly the one-axis model (<see cref="OneAxisMachine"/>) - same delta, omega, E'q trajectory;
///   - EQUILIBRIUM: started at a fixed point (with E'd at its equilibrium) the machine is stationary;
///   - EXACT Q-AXIS DECAY: with Xq = X'q the E'd equation is a pure first-order lag, so E'd follows the
///     analytic exponential to machine epsilon (the exact zero-order hold), even while the rotor moves;
///   - EQUILIBRIUM E'd: the closed-form fixed point satisfies E'd = (Xq - X'q) Iq exactly;
///   - DETERMINISM: two identical runs are bit-for-bit equal.
/// </summary>
public class TwoAxisMachineTests
{
    private const double F0 = 50.0;

    // A representative salient two-axis machine on a stiff SMIB network.
    private static TwoAxisMachine.Parameters Salient() => new(
        InertiaSeconds: 4.0, DampingPu: 0.0,
        Xd: 1.8, Xdp: 0.3, Xq: 1.7, Xqp: 0.55, Tdop: 6.0, Tqop: 0.4,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    // ---- Reduction to the one-axis model: X'q = Xq => E'd = 0 throughout, same swing and field ----
    [Fact]
    public void With_transient_q_reactance_equal_to_synchronous_it_reduces_to_the_one_axis_machine()
    {
        // Matched parameters, X'q = Xq so the q-axis transient driver (Xq - X'q) vanishes.
        var two = new TwoAxisMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xq: 1.7, Xqp: 1.7, Tdop: 6.0, Tqop: 0.4,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);
        var one = new OneAxisMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xq: 1.7, Tdop: 6.0,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

        const double delta0 = 0.4, eqp0 = 1.1;
        (double pm, double efd, double edp0) = TwoAxisMachine.Equilibrium(two, delta0, eqp0);
        Assert.Equal(0.0, edp0, 12);   // X'q = Xq => equilibrium E'd is exactly 0
        (double pm1, double efd1) = OneAxisMachine.Equilibrium(one, delta0, eqp0);
        Assert.Equal(pm1, pm, 12);
        Assert.Equal(efd1, efd, 12);

        const double dt = 0.005, dur = 4.0;
        TwoAxisMachine.Trace a = TwoAxisMachine.Simulate(two, delta0, omega0: 0.02, eqp0, edp0, pm, efd, F0, dt, dur);
        OneAxisMachine.Trace b = OneAxisMachine.Simulate(one, delta0, omega0: 0.02, eqp0, pm1, efd1, F0, dt, dur);

        for (int k = 0; k < a.DeltaRad.Count; k++)
        {
            Assert.Equal(b.DeltaRad[k], a.DeltaRad[k], 10);
            Assert.Equal(b.SpeedPu[k], a.SpeedPu[k], 10);
            Assert.Equal(b.EqpPu[k], a.EqpPu[k], 10);
            Assert.Equal(0.0, a.EdpPu[k], 12);           // E'd never leaves zero
        }
    }

    // ---- Equilibrium: started at a fixed point, the machine is stationary ----
    [Fact]
    public void Started_in_equilibrium_the_machine_is_stationary()
    {
        var p = Salient();
        const double delta0 = 0.5, eqp0 = 1.1;
        (double pm, double efd, double edp0) = TwoAxisMachine.Equilibrium(p, delta0, eqp0);

        TwoAxisMachine.Trace tr = TwoAxisMachine.Simulate(p, 
            delta0, omega0: 0.0, eqp0, edp0, pm, efd, F0, stepSeconds: 0.005, durationSeconds: 10.0);

        int last = tr.SpeedPu.Count - 1;
        Assert.True(Math.Abs(tr.SpeedPu[last]) < 1e-9);      // no speed drift
        Assert.Equal(delta0, tr.DeltaRad[last], 8);          // no angle drift
        Assert.Equal(eqp0, tr.EqpPu[last], 9);               // d-axis field flux at rest
        Assert.Equal(edp0, tr.EdpPu[last], 9);               // q-axis flux at rest
    }

    // ---- Exact q-axis flux decay: with Xq = X'q the E'd equation is a pure lag => analytic exponential ----
    [Fact]
    public void Q_axis_flux_decays_on_the_exact_exponential()
    {
        // Xq = X'q makes the E'd driver (Xq - X'q)Iq vanish, so T'qo dE'd/dt = -E'd exactly - a pure
        // first-order lag decaying E'd to 0, independent of the network (and of the moving rotor).
        var p = new TwoAxisMachine.Parameters(
            InertiaSeconds: 4.0, DampingPu: 0.0,
            Xd: 1.8, Xdp: 0.3, Xq: 0.55, Xqp: 0.55, Tdop: 6.0, Tqop: 0.7,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

        const double edp0 = 0.5;
        TwoAxisMachine.Trace tr = TwoAxisMachine.Simulate(p, 
            delta0: 0.3, omega0: 0.0, eqp0: 1.1, edp0, mechanicalPowerPu: 0.0, fieldVoltagePu: 1.1,
            F0, stepSeconds: 0.01, durationSeconds: 8.0);

        for (int k = 0; k < tr.EdpPu.Count; k++)
        {
            double analytic = edp0 * Math.Exp(-tr.TimeSeconds[k] / p.Tqop);
            Assert.Equal(analytic, tr.EdpPu[k], 10);          // machine-epsilon exact at every sample
        }
    }

    // ---- The equilibrium E'd is the exact fixed point E'd = (Xq - X'q) Iq ----
    [Fact]
    public void Equilibrium_q_axis_emf_is_the_exact_fixed_point()
    {
        var p = Salient();
        const double delta0 = 0.6, eqp0 = 1.05;
        double edp = TwoAxisMachine.EquilibriumEdp(p, delta0);
        (_, double iq) = TwoAxisMachine.Currents(p, delta0, eqp0, edp);
        Assert.Equal((p.Xq - p.Xqp) * iq, edp, 12);           // E'd = (Xq - X'q) Iq at the fixed point
    }

    // ---- Determinism: two identical runs are bit-for-bit equal ----
    [Fact]
    public void Two_identical_runs_are_bit_for_bit_equal()
    {
        var p = Salient();
        const double delta0 = 0.35, eqp0 = 1.05;
        (double pm, double efd, double edp0) = TwoAxisMachine.Equilibrium(p, delta0, eqp0);

        TwoAxisMachine.Trace RunOnce() => TwoAxisMachine.Simulate(p, 
            delta0, omega0: 0.02, eqp0, edp0, pm, efd, F0, stepSeconds: 0.002, durationSeconds: 3.0);

        TwoAxisMachine.Trace a = RunOnce(), b = RunOnce();
        for (int i = 0; i < a.DeltaRad.Count; i++)
        {
            Assert.True(a.DeltaRad[i] == b.DeltaRad[i]);
            Assert.True(a.SpeedPu[i] == b.SpeedPu[i]);
            Assert.True(a.EqpPu[i] == b.EqpPu[i]);
            Assert.True(a.EdpPu[i] == b.EdpPu[i]);
        }
    }
}
