using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R246: a mechanicalReference that differs from pm0 is documented as a set-point step at t = 0, but the governor used to
/// be INITIALISED at the reference, which preset its valve and reheat states there, so the mechanical power jumped on the
/// first step with no valve or reheat lag. Governors now have SetReference (no state touched) and GovernedMultiMachine
/// initialises at pm0, then moves the reference.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    [Fact]
    public void R246_tgov1_reference_step_rises_through_the_valve_lag_exactly()
    {
        // Reheat lead == lag makes the turbine an exact pass-through (LeadLag reduction identity), so Pm = Pgv and the
        // response to a reference step 0.5 -> 0.6 at dw = 0 is the exact first-order rise 0.6 - 0.1 * exp(-t / T1).
        const double t1 = 0.4, dt = 0.01;
        var gov = new Tgov1(droopPu: 0.05, valveTimeSeconds: t1, reheatLeadSeconds: 3.0, reheatLagSeconds: 3.0);
        gov.Initialize(0.5);
        gov.SetReference(0.6);
        for (int k = 1; k <= 300; k++)
        {
            double pm = gov.Step(0.0, dt);
            double expected = 0.6 - 0.1 * Math.Exp(-k * dt / t1);
            Assert.True(Math.Abs(pm - expected) < 1e-12, $"step {k}: {pm:R} vs {expected:R}");
        }
        // The first step moved only (1 - exp(-dt/T1)) of the way: about 2.5 %, not the whole 0.1 pu jump of the old code.
        var fresh = new Tgov1(0.05, t1, 3.0, 3.0);
        fresh.Initialize(0.5);
        fresh.SetReference(0.6);
        Assert.True(fresh.Step(0.0, dt) < 0.5 + 0.003);
    }

    [Fact]
    public void R246_set_reference_to_the_initial_power_is_an_exact_no_op()
    {
        var a = new Tgov1(0.05, 0.3, 1.0, 5.0);
        var b = new Tgov1(0.05, 0.3, 1.0, 5.0);
        a.Initialize(0.7);
        b.Initialize(0.7);
        b.SetReference(0.7);
        var ha = new HydroGovernor(0.04, 0.4, 5.0, 0.2, 2.0);
        var hb = new HydroGovernor(0.04, 0.4, 5.0, 0.2, 2.0);
        ha.Initialize(0.6);
        hb.Initialize(0.6);
        hb.SetReference(0.6);
        var ia = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.3, 0.2, 0.2);
        var ib = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.3, 0.2, 0.2);
        ia.Initialize(0.8);
        ib.Initialize(0.8);
        ib.SetReference(0.8);
        for (int k = 0; k < 400; k++)
        {
            double dw = k < 50 ? 0.0 : -0.002;
            AssertBits(a.Step(dw, 0.01), b.Step(dw, 0.01), $"TGOV1 step {k}");
            AssertBits(ha.Step(dw, 0.01), hb.Step(dw, 0.01), $"hydro step {k}");
            AssertBits(ia.Step(dw, 0.01), ib.Step(dw, 0.01), $"IEEEG1 step {k}");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => a.SetReference(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ha.SetReference(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ia.SetReference(double.NegativeInfinity));
    }

    private static Complex[,] B3Link(double x) => new Complex[,]
    {
        { new Complex(0, -1.0 / x), new Complex(0, 1.0 / x) },
        { new Complex(0, 1.0 / x), new Complex(0, -1.0 / x) },
    };

    [Fact]
    public void R246_governed_swing_with_a_reference_equal_to_pm0_is_bit_for_bit_the_run_with_none()
    {
        double[] h = { 4.0, 4.0 }, d = { 0.0, 0.0 }, eq = { 1.0, 1.0 };
        double p = Math.Sin(0.3) / 0.5;
        double[] pm0 = { p, -p }, delta0 = { 0.3, 0.0 };
        Tgov1 Gov() => new(0.05, 0.3, 0.0, 2.0, valveMinPu: -10.0, valveMaxPu: 10.0);
        var fault = new ClassicalMultiMachine.Fault(0.1, 0.2, new Complex[2, 2]);
        ClassicalMultiMachine.Result none = GovernedMultiMachine.Simulate(h, d, pm0, eq, delta0, B3Link(0.5),
            new Tgov1?[] { Gov(), Gov() }, 50.0, 0.005, 3.0, fault);
        ClassicalMultiMachine.Result same = GovernedMultiMachine.Simulate(h, d, pm0, eq, delta0, B3Link(0.5),
            new Tgov1?[] { Gov(), Gov() }, 50.0, 0.005, 3.0, fault, mechanicalReference: (double[])pm0.Clone());
        for (int m = 0; m < 2; m++)
            for (int k = 0; k < none.Machines[m].SpeedPu.Count; k++)
            {
                AssertBits(none.Machines[m].SpeedPu[k], same.Machines[m].SpeedPu[k], $"machine {m} speed {k}");
                AssertBits(none.Machines[m].DeltaRad[k], same.Machines[m].DeltaRad[k], $"machine {m} angle {k}");
            }
    }

    [Fact]
    public void R246_a_set_point_step_accelerates_less_on_the_first_step_than_an_instant_power_step()
    {
        // Islanded pair at rest (pm0 = Pe = 0). Machine 0's reference steps to 1 pu. The old code was the same as an
        // instant mechanical step to 1 pu (the governor preset at the reference); the classical swing with pm = {1, 0}
        // is exactly that instant step. Through the valve lag (T1 = 0.3 s, dt = 0.01 s) the first-step acceleration must
        // be a small fraction of it: the power has only begun to rise.
        double[] h = { 4.0, 4.0 }, d = { 2.0, 2.0 }, pm0 = { 0.0, 0.0 }, eq = { 1.0, 1.0 }, delta0 = { 0.0, 0.0 };
        Tgov1 Gov() => new(0.05, 0.3, 0.0, 2.0, valveMinPu: -10.0, valveMaxPu: 10.0);
        ClassicalMultiMachine.Result governed = GovernedMultiMachine.Simulate(h, d, pm0, eq, delta0, B3Link(0.5),
            new Tgov1?[] { Gov(), Gov() }, 50.0, 0.01, 0.5, mechanicalReference: new[] { 1.0, 0.0 });
        ClassicalMultiMachine.Result instant = ClassicalMultiMachine.Simulate(h, d, new[] { 1.0, 0.0 }, eq, delta0,
            B3Link(0.5), 50.0, 0.01, 0.5);
        double wGov = governed.Machines[0].SpeedPu[1], wInstant = instant.Machines[0].SpeedPu[1];
        Assert.True(wInstant > 0.0);
        Assert.True(wGov > 0.0 && wGov < 0.1 * wInstant, $"first-step speed {wGov:R} vs the instant step {wInstant:R}");
    }
}
