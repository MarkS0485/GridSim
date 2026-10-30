using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The hydro governor's gate and transient-droop washout form an algebraic loop. With an instant gate (Tg = 0, the
/// documented "gate instantaneous" setting) the loop is now solved exactly; before, the explicit evaluation multiplied
/// a gate error by -Rt/Rp each step and the gate diverged. A finite but too-short Tg, for which the explicit step would
/// ring or diverge, is refused with the smallest usable Tg named.
/// </summary>
public class HydroGovernorGateLoopTests
{
    // Typical hydro data: Rt/Rp = 0.38 / 0.05 = 7.6, Tr = 5 s, Tw = 1 s.
    private const double Rp = 0.05, Rt = 0.38, Tr = 5.0, Tw = 1.0;

    [Fact]
    public void Instant_gate_is_stationary_at_zero_speed_deviation()
    {
        var gov = new HydroGovernor(Rp, Rt, Tr, gateTimeSeconds: 0.0, waterTimeSeconds: Tw);
        gov.Initialize(0.8);
        double pm = 0.0;
        for (int k = 0; k < 2000; k++) pm = gov.Step(0.0, 0.005);
        Assert.Equal(0.8, gov.GatePosition, 12);
        Assert.Equal(0.8, pm, 12);
    }

    [Fact]
    public void Instant_gate_stays_bounded_and_settles_on_the_permanent_droop()
    {
        var gov = new HydroGovernor(Rp, Rt, Tr, gateTimeSeconds: 0.0, waterTimeSeconds: Tw);
        gov.Initialize(0.8);

        // A small speed drop: the steady gate is 0.8 + 1e-4 / Rp = 0.802 (permanent droop dPm/dw = -1/Rp).
        double maxGate = double.NegativeInfinity, minGate = double.PositiveInfinity;
        // Test changed 2026-10-19 (first run of this test): the slow mode of the transient-droop loop has the time
        // constant Tr (1 + Rt/Rp) = 5 x 8.6 = 43 s, so at 100 s (2.3 time constants) the gate was still 1.7e-4 short of
        // 0.802 (0.8018271). Settling to 9 decimal places needs 43 ln(0.002 / 5e-10) = 654 s; 1,500 s (35 time constants)
        // is ample. The assertions are unchanged.
        for (int k = 0; k < 300000; k++)         // 1,500 s
        {
            gov.Step(-1e-4, 0.005);
            maxGate = Math.Max(maxGate, gov.GatePosition);
            minGate = Math.Min(minGate, gov.GatePosition);
        }
        // Monotone approach from below: never under the start, never above the steady value (no ringing).
        Assert.True(minGate >= 0.8, $"gate dipped to {minGate:R}");
        Assert.True(maxGate <= 0.802 + 1e-12, $"gate overshot to {maxGate:R}");
        Assert.Equal(0.802, gov.GatePosition, 9);
    }

    [Fact]
    public void A_gate_time_too_short_for_the_step_is_refused()
    {
        // Tg = 1 ms at a 5 ms step with Rt/Rp = 7.6: the explicit step factor is about -6.6.
        var gov = new HydroGovernor(Rp, Rt, Tr, gateTimeSeconds: 0.001, waterTimeSeconds: Tw);
        gov.Initialize(0.8);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => gov.Step(0.0, 0.005));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void Typical_gate_time_is_unaffected()
    {
        // Tg = 0.2 s at 5 ms: factor about 0.79, the explicit scheme exactly as before (stationary at dw = 0).
        var gov = new HydroGovernor(Rp, Rt, Tr, gateTimeSeconds: 0.2, waterTimeSeconds: Tw);
        gov.Initialize(0.8);
        double pm = 0.0;
        for (int k = 0; k < 2000; k++) pm = gov.Step(0.0, 0.005);
        Assert.Equal(0.8, pm, 12);
    }
}
