using System;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the salient/subtransient multi-machine integrator (<see cref="MultiMachineSubtransient"/>):
///   - REDUCTION TO CLASSICAL: with every reactance equal to X'd and a matched held field the subtransient
///     EMFs stay at their initial E'q angle delta, so a faulted run matches the trusted
///     <see cref="ClassicalMultiMachine"/> swing to machine epsilon;
///   - EQUILIBRIUM: a fleet started at a fixed point (Pm = Pe(delta0)) is stationary;
///   - DETERMINISM: two identical runs are bit-for-bit equal.
/// </summary>
public class MultiMachineSubtransientTests
{
    private const double F0 = 50.0;

    // A two-machine internal-node network: the machines coupled through a tie reactance (no ground path,
    // so they swing against each other). X'' = X'd is already folded into this reduced admittance.
    private static Complex[,] Tie(double xTot)
    {
        Complex y = Complex.One / new Complex(0.0, xTot);
        return new[,] { { y, -y }, { -y, y } };
    }

    // Machine parameters with every reactance equal (all flux drivers vanish) - the classical-reduction case.
    private static MultiMachineSubtransient.Machine Flat() => new(
        Xd: 0.3, Xdp: 0.3, Xdpp: 0.3, Xq: 0.3, Xqp: 0.3, Xqpp: 0.3,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.4, Tqopp: 0.06);

    // ---- Reduction to the classical swing over a genuine (fault-free) transient ----
    [Fact]
    public void Flat_reactances_and_held_field_reduce_to_the_classical_swing()
    {
        var h = new[] { 4.0, 5.0 };
        var d = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        Complex[,] y = Tie(0.5);

        // Mechanical powers set at an equilibrium angle, then the fleet is RELEASED from a displaced angle -
        // a genuine nonlinear swing, driven identically for both integrators (no fault-window boundary, whose
        // sub-step handling differs, so the only difference is Pe rounding: Inject vs the classical sum).
        var deltaEq = new[] { 0.20, 0.0 };
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, deltaEq, y);
        var delta0 = new[] { 0.40, 0.0 };

        const double dt = 0.002, dur = 3.0;

        ClassicalMultiMachine.Result cls = ClassicalMultiMachine.Simulate(
            h, d, pm, eq0, delta0, y, F0, dt, dur);

        var machines = new[] { Flat(), Flat() };
        MultiMachineSubtransient.Result sub = MultiMachineSubtransient.Simulate(
            machines, h, d, pm, efd: eq0,           // held field = |E'| keeps E'q constant
            delta0, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, dt, dur);

        int count = cls.Machines[0].DeltaRad.Count;
        for (int k = 0; k < count; k++)
        {
            // Compare the (COI-invariant) inter-machine angle and each speed to machine epsilon.
            double clsRel = cls.Machines[0].DeltaRad[k] - cls.Machines[1].DeltaRad[k];
            double subRel = sub.Machines[0].DeltaRad[k] - sub.Machines[1].DeltaRad[k];
            Assert.Equal(clsRel, subRel, 8);
            Assert.Equal(cls.Machines[0].SpeedPu[k], sub.Machines[0].SpeedPu[k], 9);
            Assert.Equal(cls.Machines[1].SpeedPu[k], sub.Machines[1].SpeedPu[k], 9);

            // And the subtransient EMFs really did stay put (the reduction premise).
            Assert.Equal(eq0[0], sub.Machines[0].EqppPu[k], 9);
            Assert.Equal(0.0, sub.Machines[0].EddpPu[k], 9);
        }
    }

    // ---- Equilibrium: started at a fixed point, the fleet is stationary ----
    [Fact]
    public void Started_in_equilibrium_the_fleet_is_stationary()
    {
        var h = new[] { 4.0, 5.0 };
        var d = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        var delta0 = new[] { 0.20, 0.0 };
        Complex[,] y = Tie(0.5);
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, delta0, y);

        var machines = new[] { Flat(), Flat() };
        MultiMachineSubtransient.Result sub = MultiMachineSubtransient.Simulate(
            machines, h, d, pm, efd: eq0,
            delta0, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, stepSeconds: 0.005, durationSeconds: 10.0);

        Assert.True(sub.StayedInSynchronism);
        int last = sub.Machines[0].DeltaRad.Count - 1;
        for (int i = 0; i < 2; i++)
        {
            Assert.True(Math.Abs(sub.Machines[i].SpeedPu[last]) < 1e-9);
            Assert.Equal(delta0[i], sub.Machines[i].DeltaRad[last], 8);
        }
    }

    // ---- Multi-machine AVR: SEXS per machine holds each terminal voltage at its regulated setpoint ----
    [Fact]
    public void Per_machine_avr_holds_the_terminal_voltages_stationary()
    {
        var h = new[] { 4.0, 5.0 };
        var d = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        var delta0 = new[] { 0.20, 0.0 };
        Complex[,] y = Tie(0.5);
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, delta0, y);

        // Flat reactances => held field at equilibrium is Efd = E'q = eq0; X'' = 0.3 for the Vt recovery.
        var xpp = new[] { 0.3, 0.3 };
        double[] vt0 = SubtransientNetworkInterface.TerminalVoltageMagnitudes(eq0, new[] { 0.0, 0.0 }, delta0, y, xpp);

        const double k = 100.0;
        var exciters = new[] { new Sexs(0.1, 1.0, k, 0.02), new Sexs(0.1, 1.0, k, 0.02) };
        var vref = new[] { vt0[0] + eq0[0] / k, vt0[1] + eq0[1] / k };   // matched: steady Efd = eq0

        MultiMachineSubtransient.Result sub = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, d, pm, efd: eq0,
            delta0, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, stepSeconds: 0.005, durationSeconds: 10.0,
            fault: null, slipLimitRad: 2.0 * Math.PI, exciters: exciters, voltageReferencePu: vref);

        int last = sub.Machines[0].DeltaRad.Count - 1;
        var eqppEnd = new[] { sub.Machines[0].EqppPu[last], sub.Machines[1].EqppPu[last] };
        var eddpEnd = new[] { sub.Machines[0].EddpPu[last], sub.Machines[1].EddpPu[last] };
        var deltaEnd = new[] { sub.Machines[0].DeltaRad[last], sub.Machines[1].DeltaRad[last] };
        double[] vtEnd = SubtransientNetworkInterface.TerminalVoltageMagnitudes(eqppEnd, eddpEnd, deltaEnd, y, xpp);

        for (int i = 0; i < 2; i++)
        {
            Assert.True(Math.Abs(sub.Machines[i].SpeedPu[last]) < 1e-6);   // no drift under the closed AVR loop
            Assert.Equal(delta0[i], deltaEnd[i], 5);
            Assert.Equal(vt0[i], vtEnd[i], 5);                             // terminal voltage held at the setpoint
        }
    }

    // ---- A network-stage schedule reproduces a fault window (reduction identity) ----
    [Fact]
    public void A_two_stage_schedule_reproduces_a_fault_window()
    {
        var h = new[] { 4.0, 5.0 };
        var dz = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        var delta0 = new[] { 0.20, 0.0 };
        Complex[,] y = Tie(0.5);
        Complex[,] yF = Tie(5.0);
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, delta0, y);
        const double dt = 0.002, dur = 3.0, on = 0.1, clr = 0.25;

        MultiMachineSubtransient.Result byFault = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, dz, pm, eq0,
            delta0, eq0, new[] { 0.0, 0.0 }, eq0, new[] { 0.0, 0.0 }, y, F0, dt, dur,
            new MultiMachineSubtransient.Fault(on, clr, yF));

        // The same fault expressed as a schedule: faulted network at onset, base network at clear.
        var stages = new[]
        {
            new MultiMachineSubtransient.NetworkStage(on, yF),
            new MultiMachineSubtransient.NetworkStage(clr, y),
        };
        MultiMachineSubtransient.Result byStages = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, dz, pm, eq0,
            delta0, eq0, new[] { 0.0, 0.0 }, eq0, new[] { 0.0, 0.0 }, y, F0, dt, dur,
            fault: null, slipLimitRad: 2.0 * Math.PI, exciters: null, voltageReferencePu: null,
            admittanceStages: stages);

        for (int i = 0; i < 2; i++)
            for (int k = 0; k < byFault.Machines[i].DeltaRad.Count; k++)
                Assert.True(byFault.Machines[i].DeltaRad[k] == byStages.Machines[i].DeltaRad[k]);
    }

    // ---- A governor hook arrests the speed after a mechanical-power imbalance ----
    [Fact]
    public void A_governor_hook_arrests_the_speed()
    {
        var h = new[] { 4.0, 5.0 };
        var dz = new[] { 0.0, 0.0 };
        var eq0 = new[] { 1.10, 1.00 };
        var delta0 = new[] { 0.20, 0.0 };
        Complex[,] y = Tie(0.5);
        (double[] pmEq, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, delta0, y);

        // Governors: TGOV1 per machine, referenced to the equilibrium power. A held speed rise cuts power
        // back by the droop, so the reference-tracking mechanical power responds to speed.
        // valveMin below zero so the receiving machine (negative Pe on the lossless tie) can hold its point.
        var govs = new[]
        {
            new Tgov1(0.05, 0.2, 0.0, 7.0, valveMinPu: -10.0),
            new Tgov1(0.05, 0.2, 0.0, 7.0, valveMinPu: -10.0),
        };
        for (int i = 0; i < 2; i++) govs[i].Initialize(pmEq[i]);
        const double dt = 0.005;
        double Gov(int i, double speed) => govs[i].Step(speed, dt);

        MultiMachineSubtransient.Result r = MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, dz, pmEq, eq0,
            delta0, eq0, new[] { 0.0, 0.0 }, eq0, new[] { 0.0, 0.0 }, y, F0, dt, durationSeconds: 5.0,
            fault: null, slipLimitRad: 2.0 * Math.PI, exciters: null, voltageReferencePu: null,
            admittanceStages: null, mechanicalPower: Gov);

        // At the equilibrium the governed run is stationary (Pm = Pe, speed stays ~0).
        int last = r.Machines[0].DeltaRad.Count - 1;
        Assert.True(Math.Abs(r.Machines[0].SpeedPu[last]) < 1e-6);
        Assert.True(r.StayedInSynchronism);
    }

    // ---- Determinism: two identical runs are bit-for-bit equal ----
    [Fact]
    public void Two_identical_runs_are_bit_for_bit_equal()
    {
        var h = new[] { 4.0, 5.0 };
        var d = new[] { 0.1, 0.1 };
        var eq0 = new[] { 1.10, 1.00 };
        var delta0 = new[] { 0.20, 0.0 };
        Complex[,] y = Tie(0.5);
        (double[] pm, _, _) = SubtransientNetworkInterface.Inject(eq0, new[] { 0.0, 0.0 }, delta0, y);

        MultiMachineSubtransient.Result Run() => MultiMachineSubtransient.Simulate(
            new[] { Flat(), Flat() }, h, d, pm, efd: eq0,
            delta0, eqp0: eq0, edp0: new[] { 0.0, 0.0 }, eqpp0: eq0, edpp0: new[] { 0.0, 0.0 },
            y, F0, stepSeconds: 0.002, durationSeconds: 2.0,
            new MultiMachineSubtransient.Fault(0.1, 0.25, Tie(5.0)));

        MultiMachineSubtransient.Result a = Run(), b = Run();
        for (int i = 0; i < 2; i++)
            for (int k = 0; k < a.Machines[i].DeltaRad.Count; k++)
            {
                Assert.True(a.Machines[i].DeltaRad[k] == b.Machines[i].DeltaRad[k]);
                Assert.True(a.Machines[i].SpeedPu[k] == b.Machines[i].SpeedPu[k]);
                Assert.True(a.Machines[i].EqppPu[k] == b.Machines[i].EqppPu[k]);
            }
    }
}
