using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Anti-drift pins (October 2026 docs-programme finding: no test asserted these). Each constant shapes the PATH a
/// solver takes and so, for any case that touches it, the bits of the answer. Changing one is a numerics change that
/// needs re-validation and Mark's agreement, not a tidy-up; these tests make such a change impossible to miss.
/// The values are pinned exactly (IEEE-754 bit patterns), as the documentation quotes them.
/// </summary>
public class PowerFlowStepConstantsPinTests
{
    private static void AssertExactly(double expected, double actual, string name) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{name} drifted: expected {expected:R}, found {actual:R}");

    [Fact]
    public void Newton_step_damping_cap_is_0_4()
    {
        // Largest Newton correction applied undamped (rad for angles, p.u. for magnitudes); a bigger step is scaled
        // so its largest component is exactly this.
        AssertExactly(0.4, NewtonShell.MaxUndampedStep, nameof(NewtonShell.MaxUndampedStep));
    }

    [Fact]
    public void Newton_pq_voltage_floor_is_0_2_pu()
    {
        AssertExactly(0.2, NewtonShell.VmFloorPu, nameof(NewtonShell.VmFloorPu));
    }

    [Fact]
    public void Q_limit_revert_deadband_is_1e_minus_4_pu()
    {
        AssertExactly(1e-4, NewtonShell.RevertVoltTolPu, nameof(NewtonShell.RevertVoltTolPu));
    }

    [Fact]
    public void Sweep_safety_floor_is_1e_minus_6()
    {
        // Floor on |V| when dividing by a bus voltage, and on the path reactance of the PV dV/dQ sensitivity.
        AssertExactly(1e-6, BackwardForwardSweep.SafetyFloor, nameof(BackwardForwardSweep.SafetyFloor));
    }

    [Fact]
    public void The_damping_rule_uses_the_pinned_cap_and_floor()
    {
        // Behavioural pin: ApplyStep scales a step whose largest component exceeds the cap so that component is
        // exactly the cap, and holds a PQ magnitude that would go below the floor at the floor.
        int[] pPos = { -1, 0 };   // bus 0 slack (no angle unknown), bus 1 angle at position 0
        int[] qPos = { -1, 1 };   // bus 1 magnitude at position 1
        double[] dx = { 0.8, -2.0 };   // largest |component| 2.0 > 0.4, so damp = 0.4 / 2.0 = 0.2
        double[] vm = { 1.0, 0.5 };
        double[] va = { 0.0, 0.0 };
        NewtonShell.ApplyStep(2, pPos, qPos, dx, maxAbs: 2.0, vm, va);
        AssertExactly(0.2 * 0.8, va[1], "damped angle step");
        // 0.5 + 0.2 * -2.0 = 0.1 < 0.2, so the magnitude is held at the floor.
        AssertExactly(NewtonShell.VmFloorPu, vm[1], "floored magnitude");
        AssertExactly(1.0, vm[0], "slack magnitude untouched");
    }
}
