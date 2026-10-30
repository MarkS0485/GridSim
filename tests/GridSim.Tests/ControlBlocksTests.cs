using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the common block-diagram integrator (<see cref="ControlBlocks"/> and the standard models
/// wired from it) against EXACT analytic references and structural reduction identities - the checks that
/// need no external data and cannot be fabricated:
///   - the first-order-lag step response reproduces K(1-e^{-t/T}) to machine epsilon (exact zero-order hold);
///   - the pure integrator ramps exactly for a held input;
///   - a lead-lag with equal time constants is a bit-exact pass-through, and a zero-time-constant lag is a
///     bit-exact gain (the reduction identities);
///   - the washout blocks DC (decays to exactly zero) and passes a change unattenuated;
///   - non-windup limits clamp and release immediately;
///   - the TGOV1 governor and SEXS exciter are stationary at their equilibrium and show the analytic
///     steady droop / gain;
///   - every block is bit-for-bit deterministic run to run.
/// These relocate machine-epsilon to the places a closed-form oracle exists, the same discipline the
/// classical-machine and contingency layers follow.
/// </summary>
public class ControlBlocksTests
{
    private const double Dt = 0.01;

    // ---- First-order lag: exact exponential step response ----
    [Fact]
    public void FirstOrderLag_step_response_matches_the_analytic_exponential_to_epsilon()
    {
        const double k = 2.5, t = 0.35, u = 1.3;
        var lag = new FirstOrderLag(k, t);
        for (int n = 1; n <= 200; n++)
        {
            double y = lag.Step(u, Dt);
            double analytic = k * u * (1.0 - Math.Exp(-(n * Dt) / t));
            Assert.Equal(analytic, y, 10);
        }
    }

    [Fact]
    public void FirstOrderLag_with_zero_time_constant_is_a_bit_exact_gain()
    {
        const double k = 3.7;
        var lag = new FirstOrderLag(k, timeConstantSeconds: 0.0);
        foreach (double u in new[] { 0.0, 1.0, -2.4, 17.9 })
            Assert.True(lag.Step(u, Dt) == k * u);
    }

    [Fact]
    public void FirstOrderLag_non_windup_limit_holds_and_releases()
    {
        var lag = new FirstOrderLag(gain: 1.0, timeConstantSeconds: 0.1, min: -1.0, max: 1.0);
        // Drive hard above the ceiling: the output pins at the limit, never past it.
        for (int i = 0; i < 500; i++) Assert.True(lag.Step(10.0, Dt) <= 1.0 + 1e-15);
        Assert.Equal(1.0, lag.Output, 12);
        // Reverse the input: a non-windup limit releases at once (no wound-up state to unload).
        double next = lag.Step(-10.0, Dt);
        Assert.True(next < 1.0);
    }

    // ---- Lead-lag: reduction identity, DC gain, phase lead ----
    [Fact]
    public void LeadLag_with_equal_time_constants_is_a_bit_exact_passthrough()
    {
        var ll = new LeadLag(leadSeconds: 0.4, lagSeconds: 0.4);
        double[] inputs = { 0.0, 1.0, 1.0, -3.2, 5.5, -5.5, 0.7 };
        foreach (double u in inputs)
            Assert.True(ll.Step(u, Dt) == u);   // exact pass-through, not merely close
    }

    [Fact]
    public void LeadLag_has_unity_dc_gain()
    {
        const double u = 2.2;
        var ll = new LeadLag(leadSeconds: 0.05, lagSeconds: 0.5);
        double y = 0.0;
        for (int i = 0; i < 2000; i++) y = ll.Step(u, Dt);
        Assert.Equal(u, y, 9);
    }

    [Fact]
    public void LeadLag_with_lead_over_lag_leads_the_input()
    {
        // Ta > Tb: the feedthrough coefficient Ta/Tb > 1, so a step produces an immediate overshoot
        // above the eventual (unity-gain) settling value - the defining phase-lead behaviour.
        var ll = new LeadLag(leadSeconds: 1.0, lagSeconds: 0.2);
        ll.Initialize(0.0);
        double first = ll.Step(1.0, Dt);
        Assert.True(first > 1.0);
    }

    // ---- Washout: DC blocking and change-passing ----
    [Fact]
    public void Washout_blocks_dc_and_decays_to_zero()
    {
        var wo = new Washout(timeConstantSeconds: 0.3);
        double y = 0.0;
        for (int i = 0; i < 5000; i++) y = wo.Step(4.0, Dt);
        Assert.Equal(0.0, y, 9);
    }

    [Fact]
    public void Washout_passes_a_change_unattenuated_then_decays()
    {
        var wo = new Washout(timeConstantSeconds: 0.3);
        wo.Initialize(2.0);                 // in equilibrium at input 2.0 => output 0
        Assert.Equal(0.0, wo.OutputFor(2.0), 12);
        double jump = wo.Step(3.0, Dt);     // input steps by +1: the change appears (nearly) undiminished
        Assert.True(jump > 0.9 && jump <= 1.0);
    }

    // ---- Integrator: exact ramp and non-windup limit ----
    [Fact]
    public void Integrator_ramps_exactly_for_a_held_input()
    {
        const double k = 1.5, u = 0.8;
        var integ = new Integrator(k);
        for (int n = 1; n <= 300; n++)
        {
            double y = integ.Step(u, Dt);
            Assert.Equal(k * u * (n * Dt), y, 10);
        }
    }

    [Fact]
    public void Integrator_non_windup_limit_clamps_and_releases()
    {
        var integ = new Integrator(gain: 1.0, min: -2.0, max: 2.0);
        for (int i = 0; i < 1000; i++) integ.Step(1.0, Dt);
        Assert.Equal(2.0, integ.Output, 12);   // pinned at the ceiling, not wound up beyond it
        double next = integ.Step(-1.0, Dt);
        Assert.True(next < 2.0);                // releases immediately
    }

    // ---- PI controller ----
    [Fact]
    public void PiController_initializes_to_equilibrium_and_holds()
    {
        var pi = new PiController(proportionalGain: 2.0, integralGain: 5.0);
        pi.Initialize(input: 0.0, output: 1.4);   // integral pre-loaded to hold 1.4 at zero error
        for (int i = 0; i < 100; i++)
            Assert.Equal(1.4, pi.Step(0.0, Dt), 12);
    }

    // ---- Determinism ----
    [Fact]
    public void Blocks_are_bit_for_bit_deterministic()
    {
        double[] Run()
        {
            var lag = new FirstOrderLag(1.3, 0.2);
            var ll = new LeadLag(0.1, 0.4);
            var wo = new Washout(0.25);
            var integ = new Integrator(0.7, -5, 5);
            var outs = new double[400];
            for (int i = 0; i < outs.Length; i++)
            {
                double u = Math.Sin(0.05 * i);
                outs[i] = lag.Step(u, Dt) + ll.Step(u, Dt) + wo.Step(u, Dt) + integ.Step(u, Dt);
            }
            return outs;
        }
        double[] a = Run(), b = Run();
        for (int i = 0; i < a.Length; i++) Assert.True(a[i] == b[i]);
    }

    // ---- TGOV1 governor ----
    [Fact]
    public void Tgov1_is_stationary_at_its_equilibrium()
    {
        var gov = new Tgov1(droopPu: 0.05, valveTimeSeconds: 0.5, reheatLeadSeconds: 1.0,
            reheatLagSeconds: 5.0, turbineDampingPu: 0.02);
        gov.Initialize(mechanicalPowerPu: 0.8);
        // dw = 0 is a fixed point to machine epsilon: the reheat lead-lag recombination c*u + (1-c)*u is
        // not guaranteed equal to u to the last ULP, but it neither drifts nor accumulates over the run.
        for (int i = 0; i < 500; i++)
            Assert.Equal(0.8, gov.Step(0.0, Dt), 13);
    }

    [Fact]
    public void Tgov1_steady_droop_matches_the_analytic_gain()
    {
        const double r = 0.05, dtDamp = 0.02, pm0 = 0.6, dw = -0.001; // slight under-speed => more power
        var gov = new Tgov1(r, valveTimeSeconds: 0.4, reheatLeadSeconds: 0.0, reheatLagSeconds: 4.0,
            turbineDampingPu: dtDamp);
        gov.Initialize(pm0);
        double pmech = 0.0;
        for (int i = 0; i < 20000; i++) pmech = gov.Step(dw, Dt);
        double analytic = pm0 - dw / r - dtDamp * dw;
        Assert.Equal(analytic, pmech, 6);
    }

    [Fact]
    public void Tgov1_valve_limits_cap_the_response()
    {
        var gov = new Tgov1(droopPu: 0.05, valveTimeSeconds: 0.3, reheatLeadSeconds: 0.0,
            reheatLagSeconds: 3.0, valveMinPu: 0.0, valveMaxPu: 1.0);
        gov.Initialize(0.8);
        for (int i = 0; i < 5000; i++) gov.Step(-1.0, Dt);   // huge under-speed demands far more than Vmax
        Assert.Equal(1.0, gov.ValvePosition, 9);
    }

    // ---- SEXS exciter ----
    [Fact]
    public void Sexs_steady_field_voltage_is_gain_times_error()
    {
        const double k = 100.0, verr = 0.01;
        var avr = new Sexs(leadSeconds: 0.0, lagSeconds: 0.1, gain: k, exciterTimeSeconds: 0.05,
            fieldMinPu: -5.0, fieldMaxPu: 5.0);
        double efd = 0.0;
        for (int i = 0; i < 5000; i++) efd = avr.Step(verr, Dt);
        Assert.Equal(k * verr, efd, 6);
    }

    [Fact]
    public void Sexs_field_limits_clamp_the_ceiling()
    {
        var avr = new Sexs(leadSeconds: 0.0, lagSeconds: 0.1, gain: 100.0, exciterTimeSeconds: 0.05,
            fieldMinPu: -3.0, fieldMaxPu: 3.0);
        double efd = 0.0;
        for (int i = 0; i < 5000; i++) efd = avr.Step(1.0, Dt);   // 100*1 way above the ceiling
        Assert.Equal(3.0, efd, 9);
    }

    [Fact]
    public void Sexs_initializes_to_equilibrium_and_holds()
    {
        const double k = 50.0, efd0 = 1.8;
        var avr = new Sexs(leadSeconds: 0.2, lagSeconds: 0.8, gain: k, exciterTimeSeconds: 0.02);
        avr.Initialize(efd0);
        for (int i = 0; i < 500; i++)
            Assert.Equal(efd0, avr.Step(efd0 / k, Dt), 10);
    }
}
