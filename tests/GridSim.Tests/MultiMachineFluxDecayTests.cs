using System;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the multi-machine varying-<c>E'q</c> (flux-decay) model (<see cref="MultiMachineFluxDecay"/>)
/// - the network generalisation of the SMIB <see cref="OneAxisMachine"/> - against oracles at the
/// machine-epsilon standard:
///   - EXACT FLUX DECAY: an isolated (disconnected) machine's field-voltage step response is the analytic
///     exponential to machine epsilon (the field state rides the exact zero-order-hold lag);
///   - REDUCTION TO CLASSICAL: with Xd = X'd and a matched held field E'q is constant, so the fleet is the
///     constant-E' classical swing (matched to machine epsilon over a faulted transient);
///   - EQUILIBRIUM: a coupled fleet started at a fixed point (Pm = Pe, Efd = E'q + (Xd-X'd)Id) stays
///     stationary in every state;
///   - CLOSED-FORM NETWORK INJECTION: the recovered d-axis current Id = Qe/|E'| matches the independent
///     phasor computation from the reduced admittance;
///   - DETERMINISM: two identical faulted runs are bit-for-bit equal.
/// </summary>
public class MultiMachineFluxDecayTests
{
    private const double F0 = 50.0;

    /// <summary>Reduced admittance of a single lossless reactance X between two internal nodes (G = 0).</summary>
    private static Complex[,] ReactanceLink(double x) => new Complex[,]
    {
        { new Complex(0, -1.0 / x), new Complex(0, 1.0 / x) },
        { new Complex(0, 1.0 / x), new Complex(0, -1.0 / x) },
    };

    // ---- Exact flux decay: an isolated machine's field step follows the analytic exponential to epsilon ----
    [Fact]
    public void Isolated_machine_field_step_follows_the_exact_flux_decay_exponential()
    {
        // A single machine with no network transfer (Y = 0) draws no current: Id = 0 and Pe = 0, so the rotor
        // is pinned and E'q obeys T'do dE'q/dt = Efd - E'q exactly - a pure first-order lag on the field.
        double[] h = { 4.0 }, d = { 0.0 }, pm = { 0.0 }, eq0 = { 1.0 };
        double[] delta0 = { 0.0 }, xdMinusXdp = { 1.5 }, tdop = { 5.0 };
        const double efdStep = 1.5;
        double[] efd = { efdStep };
        var y = new Complex[1, 1];   // all-zero: the machine is disconnected

        MultiMachineFluxDecay.Result r = MultiMachineFluxDecay.Simulate(
            h, d, pm, eq0, delta0, xdMinusXdp, tdop, efd, y, F0, 0.01, 15.0);

        MultiMachineFluxDecay.MachineTrace m = r.Machines[0];
        for (int k = 0; k < m.EqpPu.Count; k++)
        {
            double t = m.TimeSeconds[k];
            double analytic = efdStep + (eq0[0] - efdStep) * Math.Exp(-t / tdop[0]);
            Assert.Equal(analytic, m.EqpPu[k], 10);            // machine-epsilon exact at every sample
        }
        int last = m.DeltaRad.Count - 1;
        Assert.Equal(0.0, m.DeltaRad[last], 12);               // rotor pinned (Pe = 0, Pm = 0)
        Assert.True(Math.Abs(m.SpeedPu[last]) < 1e-12);
    }

    // ---- Reduction to classical: Xd = X'd + matched field => constant E' classical swing ----
    [Fact]
    public void With_no_flux_decay_the_fleet_matches_the_classical_swing()
    {
        double[] h = { 3.5, 4.0 }, d = { 0.5, 0.6 }, pm = { 0.8, -0.8 }, eq0 = { 1.05, 1.02 };
        double[] delta0 = { 0.35, 0.0 };
        Complex[,] y = ReactanceLink(0.4);
        var fault = new ClassicalMultiMachine.Fault(0.1, 0.25, new Complex[2, 2]);   // bolted, self-clearing

        // Xd = X'd makes the flux driver vanish; a field held at the initial EMF keeps E'q there, so the
        // model is the constant-E' classical machine.
        double[] xdMinusXdp = { 0.0, 0.0 }, tdop = { 6.0, 5.0 }, efd = { eq0[0], eq0[1] };

        ClassicalMultiMachine.Result classical = ClassicalMultiMachine.Simulate(
            h, d, pm, eq0, delta0, y, F0, 0.002, 4.0, fault);
        MultiMachineFluxDecay.Result flux = MultiMachineFluxDecay.Simulate(
            h, d, pm, eq0, delta0, xdMinusXdp, tdop, efd, y, F0, 0.002, 4.0, fault);

        Assert.Equal(classical.StayedInSynchronism, flux.StayedInSynchronism);
        for (int mi = 0; mi < 2; mi++)
        {
            var c = classical.Machines[mi];
            var f = flux.Machines[mi];
            double maxDDelta = 0.0, maxDOmega = 0.0, maxDEqp = 0.0;
            for (int k = 0; k < c.DeltaRad.Count; k++)
            {
                maxDDelta = Math.Max(maxDDelta, Math.Abs(c.DeltaRad[k] - f.DeltaRad[k]));
                maxDOmega = Math.Max(maxDOmega, Math.Abs(c.SpeedPu[k] - f.SpeedPu[k]));
                maxDEqp = Math.Max(maxDEqp, Math.Abs(f.EqpPu[k] - eq0[mi]));
            }
            Assert.True(maxDDelta < 1e-6, $"delta drift {maxDDelta:E3}");
            Assert.True(maxDOmega < 1e-6, $"omega drift {maxDOmega:E3}");
            Assert.True(maxDEqp < 1e-9, $"E'q drift {maxDEqp:E3}");   // E'q genuinely held (reduction premise)
        }
    }

    // ---- Equilibrium: a coupled fleet started at a fixed point stays stationary in every state ----
    [Fact]
    public void Started_in_equilibrium_the_coupled_fleet_is_stationary()
    {
        double[] h = { 4.0, 5.0 }, d = { 0.0, 0.0 }, eq0 = { 1.05, 1.02 };
        double[] delta0 = { 0.2, -0.1 }, xdMinusXdp = { 1.5, 1.4 }, tdop = { 6.0, 5.0 };
        Complex[,] y = ReactanceLink(0.3);

        // Fixed point: Pm = Pe(delta0) and Efd = E'q + (Xd-X'd)Id(delta0).
        (double[] pe0, _) = MultiMachineFluxDecay.NetworkInjection(eq0, delta0, y);
        double[] efd = MultiMachineFluxDecay.EquilibriumField(eq0, delta0, y, xdMinusXdp);

        MultiMachineFluxDecay.Result r = MultiMachineFluxDecay.Simulate(
            h, d, pe0, eq0, delta0, xdMinusXdp, tdop, efd, y, F0, 0.005, 10.0);

        Assert.True(r.StayedInSynchronism);
        for (int mi = 0; mi < 2; mi++)
        {
            var m = r.Machines[mi];
            int last = m.SpeedPu.Count - 1;
            Assert.True(Math.Abs(m.SpeedPu[last]) < 1e-8);         // no speed drift
            Assert.Equal(delta0[mi], m.DeltaRad[last], 7);         // no angle drift
            Assert.Equal(eq0[mi], m.EqpPu[last], 8);               // field flux at rest
        }
    }

    // ---- Closed-form network injection: Id = Qe/|E'| matches the independent phasor computation ----
    [Fact]
    public void Network_injection_matches_the_phasor_current_solution()
    {
        double[] eq = { 1.05, 1.02 };
        double[] delta = { 0.3, -0.15 };
        Complex[,] y = ReactanceLink(0.35);

        (double[] pe, double[] id) = MultiMachineFluxDecay.NetworkInjection(eq, delta, y);

        // Independent path: E_i = eq_i e^{j delta_i}; I = Y E; S_i = E_i conj(I_i); Pe = Re S, Id = Im S / eq.
        var e = new Complex[2];
        for (int i = 0; i < 2; i++) e[i] = Complex.FromPolarCoordinates(eq[i], delta[i]);
        for (int i = 0; i < 2; i++)
        {
            Complex ii = Complex.Zero;
            for (int j = 0; j < 2; j++) ii += y[i, j] * e[j];
            Complex s = e[i] * Complex.Conjugate(ii);
            Assert.Equal(s.Real, pe[i], 12);
            Assert.Equal(s.Imaginary / eq[i], id[i], 12);
        }
    }

    // ---- Determinism: two identical faulted runs are bit-for-bit equal ----
    [Fact]
    public void Two_identical_runs_are_bit_for_bit_equal()
    {
        double[] h = { 3.5, 4.2 }, d = { 0.4, 0.5 }, eq0 = { 1.04, 1.01 };
        double[] delta0 = { 0.25, -0.05 }, xdMinusXdp = { 1.5, 1.3 }, tdop = { 6.0, 5.5 };
        Complex[,] y = ReactanceLink(0.45);
        (double[] pm, _) = MultiMachineFluxDecay.NetworkInjection(eq0, delta0, y);
        double[] efd = MultiMachineFluxDecay.EquilibriumField(eq0, delta0, y, xdMinusXdp);

        MultiMachineFluxDecay.Result RunOnce() => MultiMachineFluxDecay.Simulate(
            h, d, pm, eq0, delta0, xdMinusXdp, tdop, efd, y, F0, 0.002, 3.0,
            new ClassicalMultiMachine.Fault(0.1, 0.2, new Complex[2, 2]));

        MultiMachineFluxDecay.Result a = RunOnce(), b = RunOnce();
        for (int mi = 0; mi < 2; mi++)
        {
            var am = a.Machines[mi];
            var bm = b.Machines[mi];
            for (int k = 0; k < am.DeltaRad.Count; k++)
            {
                Assert.True(am.DeltaRad[k] == bm.DeltaRad[k]);
                Assert.True(am.SpeedPu[k] == bm.SpeedPu[k]);
                Assert.True(am.EqpPu[k] == bm.EqpPu[k]);
            }
        }
    }
}
