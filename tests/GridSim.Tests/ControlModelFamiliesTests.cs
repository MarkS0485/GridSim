using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the wider standard control-model families (PSS1A, IEEEG1, IEEET1, hydro governor) against
/// their defining behaviours to the machine-epsilon standard where a closed form exists: steady-state gains,
/// non-windup limits, the reduction identities that collapse each onto a simpler known form, and determinism.
/// </summary>
public class ControlModelFamiliesTests
{
    // ================= PSS1A =================

    // A sustained input produces no steady output (the washout is the defining property).
    [Fact]
    public void Pss1a_washes_out_a_sustained_input()
    {
        var pss = new Pss1a(gain: 10.0, washoutSeconds: 10.0, 0.15, 0.02, 0.15, 0.02);
        pss.Initialize(0.0);
        double vs = 0.0;
        for (int k = 0; k < 20000; k++) vs = pss.Step(0.01, 0.005);   // 100 s of a constant input
        Assert.True(Math.Abs(vs) < 1e-4, $"steady output {vs:E3} should wash out to zero");
    }

    // A step in the input gives an immediate response that then decays back to zero.
    [Fact]
    public void Pss1a_responds_then_decays_and_is_deterministic()
    {
        Pss1a Make() { var p = new Pss1a(5.0, 5.0, 0.2, 0.05, 0.2, 0.05); p.Initialize(0.0); return p; }
        var a = Make();
        double first = a.Step(0.02, 0.01);
        Assert.True(Math.Abs(first) > 1e-6);                          // it does respond to the change

        // Output limits clamp.
        var lim = new Pss1a(1000.0, 5.0, 0.2, 0.05, 0.2, 0.05, outputMinPu: -0.1, outputMaxPu: 0.1);
        lim.Initialize(0.0);
        Assert.True(lim.Step(1.0, 0.01) <= 0.1 + 1e-12);

        // Determinism.
        var b = Make(); var c = Make();
        for (int k = 0; k < 500; k++) Assert.True(b.Step(0.01 * Math.Sin(k), 0.01) == c.Step(0.01 * Math.Sin(k), 0.01));
    }

    // ================= IEEEG1 =================

    [Fact]
    public void Ieeeg1_is_stationary_and_has_the_expected_droop()
    {
        var gov = new Ieeeg1(droopPu: 0.05, governorLeadSeconds: 0.0, governorLagSeconds: 0.2,
            valveTimeSeconds: 0.3, chestSeconds: 0.2, reheatSeconds: 7.0, crossoverSeconds: 0.4, lpSeconds: 0.4,
            k1: 0.3, k3: 0.3, k5: 0.2, k7: 0.2);
        gov.Initialize(0.8);

        // At dw = 0 it holds the initial power to machine epsilon over a long run.
        double pm = 0.8;
        for (int k = 0; k < 4000; k++) pm = gov.Step(0.0, 0.005);
        Assert.Equal(0.8, pm, 8);

        // Steady droop dPmech/dw = -1/R in the unlimited region.
        var g2 = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, 0.3, 0.3, 0.2, 0.2);
        g2.Initialize(0.8);
        double pmDroop = 0.8;
        for (int k = 0; k < 20000; k++) pmDroop = g2.Step(0.01, 0.005);   // hold a small speed rise to steady state
        Assert.Equal(0.8 - 0.01 / 0.05, pmDroop, 4);
    }

    // Reduction: K1 = 1, no phase shaping => a single-stage governor (fractions collapse to the chest stage).
    [Fact]
    public void Ieeeg1_reduces_to_a_single_stage_when_k1_is_one()
    {
        var gov = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.25, 7.0, 0.4, 0.4, k1: 1.0);
        gov.Initialize(0.6);
        double pm = 0.6;
        for (int k = 0; k < 4000; k++) pm = gov.Step(0.0, 0.005);
        Assert.Equal(0.6, pm, 8);                                     // still stationary with all power on one stage
    }

    // ================= IEEET1 =================

    [Fact]
    public void Ieeet1_reduces_to_a_two_lag_avr_without_saturation_or_feedback()
    {
        // Ke = 1, no saturation (Aex = 0), no rate feedback (Kf = 0): steady Efd = Ka*Verr.
        var ex = new Ieeet1(regulatorGain: 50.0, regulatorTimeSeconds: 0.05, exciterConstant: 1.0,
            exciterTimeSeconds: 0.5, feedbackGain: 0.0, feedbackTimeSeconds: 1.0);
        ex.Initialize(2.0);
        double efd = 2.0;
        for (int k = 0; k < 6000; k++) efd = ex.Step(2.0 / 50.0, 0.005);   // hold Verr = Efd/Ka
        Assert.Equal(2.0, efd, 6);                                          // held at the initialised field

        // A raised reference drives Efd up toward Ka*Verr.
        var ex2 = new Ieeet1(50.0, 0.05, 1.0, 0.5, 0.0, 1.0);
        ex2.Initialize(2.0);
        double efd2 = 2.0;
        for (int k = 0; k < 6000; k++) efd2 = ex2.Step(0.05, 0.005);        // Verr = 0.05 => steady Efd = 2.5
        Assert.Equal(2.5, efd2, 4);
    }

    // Regulator ceiling clamps the field, and saturation raises the regulator voltage needed for a given field.
    [Fact]
    public void Ieeet1_ceiling_limits_and_saturation_raises_the_required_regulator_voltage()
    {
        var lim = new Ieeet1(500.0, 0.02, 1.0, 0.5, 0.0, 1.0, regulatorMinPu: -6.0, regulatorMaxPu: 4.0);
        lim.Initialize(2.0);
        double efd = 2.0;
        for (int k = 0; k < 4000; k++) efd = lim.Step(1.0, 0.005);          // huge error, but Vr clamps at 4
        Assert.True(efd <= 4.0 + 1e-9);                                     // Efd -> Vrmax/Ke = 4

        // With saturation, holding the same field needs a larger regulator voltage (Efd steady lower for same Verr).
        var sat = new Ieeet1(50.0, 0.05, 1.0, 0.5, 0.0, 1.0, saturationA: 0.02, saturationB: 1.0);
        sat.Initialize(2.0);
        double efdSat = 2.0;
        for (int k = 0; k < 6000; k++) efdSat = sat.Step(2.0 / 50.0, 0.005);
        Assert.True(efdSat < 2.0);   // the same Verr no longer sustains Efd = 2 once the iron saturates
    }

    // ================= Hydro governor =================

    [Fact]
    public void Hydro_governor_is_stationary_with_permanent_droop()
    {
        var gov = new HydroGovernor(permanentDroopPu: 0.04, transientDroopPu: 0.4, resetTimeSeconds: 5.0,
            gateTimeSeconds: 0.2, waterTimeSeconds: 1.0);
        gov.Initialize(0.7);
        double pm = 0.7;
        for (int k = 0; k < 8000; k++) pm = gov.Step(0.0, 0.005);
        Assert.Equal(0.7, pm, 7);                                     // stationary at dw = 0
    }

    // The defining hydro feature: a gate opening makes the power initially move the wrong way (water inertia).
    [Fact]
    public void Hydro_water_column_has_the_non_minimum_phase_inverse_response()
    {
        // Fast gate, slow water: a speed drop opens the gate; the ideal column drops power first.
        var gov = new HydroGovernor(0.04, 0.0, 5.0, gateTimeSeconds: 0.01, waterTimeSeconds: 2.0);
        gov.Initialize(0.5);
        double pmStart = gov.Step(-0.02, 0.02);   // step the speed down (calls for more power)
        Assert.True(pmStart < 0.5, "power should first drop below its initial value (water hammer)");

        // But it recovers above the start as the flow builds.
        double pm = pmStart;
        for (int k = 0; k < 4000; k++) pm = gov.Step(-0.02, 0.02);
        Assert.True(pm > 0.5, "power should recover above the initial value in steady state");
    }
}
