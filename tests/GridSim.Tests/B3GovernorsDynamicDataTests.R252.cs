using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R252: a governor or exciter Initialize documents an exact equilibrium, but the limited blocks clamped their preset
/// state while the blocks downstream were preset with the unclamped value, so an operating point outside the limits
/// started off equilibrium with no error. Each model now refuses it (ArgumentOutOfRangeException), and an in-limits
/// initialisation (including exactly AT a limit) is unchanged.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    [Fact]
    public void R252_tgov1_refuses_an_equilibrium_below_its_valve_floor()
    {
        // The default valve floor is 0: a motoring unit (Pm = -0.1 pu) cannot be held there.
        var gov = new Tgov1(droopPu: 0.05, valveTimeSeconds: 0.5, reheatLeadSeconds: 1.0, reheatLagSeconds: 5.0);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => gov.Initialize(-0.1));
        Assert.Contains("TGOV1", ex.Message);
        Assert.Contains("[0, ", ex.Message);
        Assert.Equal("mechanicalPowerPu", ex.ParamName);

        // A floor below the operating point makes the same unit a valid equilibrium, and it holds exactly.
        var motoring = new Tgov1(0.05, 0.5, 1.0, 5.0, valveMinPu: -1.0);
        motoring.Initialize(-0.1);
        for (int k = 0; k < 200; k++) Assert.Equal(-0.1, motoring.Step(0.0, 0.01), 12);
    }

    [Fact]
    public void R252_tgov1_refuses_an_equilibrium_above_its_valve_ceiling_and_accepts_one_at_it()
    {
        var gov = new Tgov1(0.05, 0.5, 0.0, 5.0, valveMinPu: 0.0, valveMaxPu: 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => gov.Initialize(1.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => gov.Initialize(double.NaN));
        gov.Initialize(1.0);   // exactly at the ceiling is still an equilibrium (inclusive limits)
        Assert.Equal(1.0, gov.ValvePosition);
    }

    [Fact]
    public void R252_ieeeg1_and_hydro_refuse_out_of_limits_equilibria()
    {
        var g1 = new Ieeeg1(0.05, 0.0, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, valveMinPu: 0.0, valveMaxPu: 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => g1.Initialize(-0.05));
        Assert.Throws<ArgumentOutOfRangeException>(() => g1.Initialize(1.01));
        g1.Initialize(0.8);

        var hy = new HydroGovernor(0.04, 0.4, 5.0, 0.2, 2.0, gateMinPu: 0.0, gateMaxPu: 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => hy.Initialize(-0.2));   // pumping against a 0 gate floor
        Assert.Throws<ArgumentOutOfRangeException>(() => hy.Initialize(1.5));
        hy.Initialize(0.7);
    }

    [Fact]
    public void R252_exciters_refuse_a_field_outside_their_limits()
    {
        var sexs = new Sexs(0.1, 1.0, 100.0, 0.05, fieldMinPu: 0.0, fieldMaxPu: 3.0);
        var e1 = Assert.Throws<ArgumentOutOfRangeException>(() => sexs.Initialize(3.5));
        Assert.Contains("SEXS", e1.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => sexs.Initialize(-0.1));
        sexs.Initialize(3.0);   // at the ceiling: allowed
        Assert.Equal(3.0, sexs.FieldVoltage);

        // IEEET1 limits the regulator output Vr = (Ke + Se)*Efd: Ke = 1, no saturation, Vrmax = 4 -> Efd = 4.5 is out.
        var t1 = new Ieeet1(50.0, 0.02, 1.0, 0.2, 0.05, 1.0, regulatorMinPu: -4.0, regulatorMaxPu: 4.0);
        var e2 = Assert.Throws<ArgumentOutOfRangeException>(() => t1.Initialize(4.5));
        Assert.Contains("IEEET1", e2.Message);
        t1.Initialize(2.0);
        Assert.Equal(2.0, t1.FieldVoltage);
    }

    [Fact]
    public void R252_an_in_limits_initialisation_is_bit_for_bit_the_old_stationary_run()
    {
        // In limits nothing is clamped, so the states are exactly what the old Initialize preset: the run from it is the
        // same sequence of numbers as an uncapped governor of the same data (no limit is ever touched at equilibrium).
        var limited = new Tgov1(0.05, 0.5, 1.0, 5.0, valveMinPu: 0.0, valveMaxPu: 1.0);
        var open = new Tgov1(0.05, 0.5, 1.0, 5.0, valveMinPu: double.NegativeInfinity, valveMaxPu: double.PositiveInfinity);
        limited.Initialize(0.8);
        open.Initialize(0.8);
        for (int k = 0; k < 500; k++)
        {
            double dw = k < 100 ? 0.0 : 0.001;   // a small disturbance that stays inside the valve limits
            AssertBits(open.Step(dw, 0.01), limited.Step(dw, 0.01), $"step {k}");
        }
    }
}
