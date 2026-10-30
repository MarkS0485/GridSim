using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>The definite-time dwell/edge state machine: fires only after a sustained crossing, resets on a sub-dwell dip, and auto-recloses.</summary>
public class ProtectionEvaluatorTests
{
    private static AssetStepContext Ctx(double clockS, double dt = 0.1)
        => new(LocalHz: 50, LocalRocofHzPerS: 0, LocalPhaseDeg: 0, DtSeconds: dt, ClockSeconds: clockS);

    [Fact]
    public void Fires_only_after_the_dwell_is_satisfied()
    {
        var e = new ProtectionEvaluator(new ProtectionStage(
            MonitoredQuantity.Rocof, ThresholdSense.Above, Threshold: 0.5, DwellSeconds: 0.3, ProtectionAction.TripInfeed));

        // Over threshold, but not yet for 0.3 s.
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.0), 0.6));   // dwell 0.1
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.1), 0.6));   // dwell 0.2
        Assert.Equal(ProtectionEdge.Fired, e.Step(Ctx(0.2), 0.6));  // dwell 0.3 -> fire
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.3), 0.6));   // latched
        Assert.True(e.Operated);
    }

    [Fact]
    public void A_sub_dwell_spike_does_not_fire()
    {
        var e = new ProtectionEvaluator(new ProtectionStage(
            MonitoredQuantity.Rocof, ThresholdSense.Above, 0.5, DwellSeconds: 0.3, ProtectionAction.TripInfeed));
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.0), 0.6));   // over
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.1), 0.2));   // back under -> dwell resets
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.2), 0.6));   // over again, dwell only 0.1
        Assert.False(e.Operated);
    }

    [Fact]
    public void Auto_reclose_restores_after_the_delay()
    {
        var e = new ProtectionEvaluator(new ProtectionStage(
            MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, DwellSeconds: 0.0,
            ProtectionAction.OpenBranch, AutoRestoreDelayS: 1.0));

        Assert.Equal(ProtectionEdge.Fired, e.Step(Ctx(0.0), 48.5));    // fires immediately
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.5), 48.5));     // still within reclose delay
        Assert.Equal(ProtectionEdge.Restored, e.Step(Ctx(1.0), 48.5)); // reclose at t0 + 1.0
        Assert.False(e.Operated);
    }

    [Fact]
    public void Arm_delay_blocks_operation_until_armed()
    {
        var e = new ProtectionEvaluator(new ProtectionStage(
            MonitoredQuantity.Rocof, ThresholdSense.Above, 0.5, DwellSeconds: 0.0, ProtectionAction.TripInfeed, ArmDelayS: 0.5));
        Assert.Equal(ProtectionEdge.None, e.Step(Ctx(0.0), 5.0));   // over threshold but not armed
        Assert.Equal(ProtectionEdge.Fired, e.Step(Ctx(0.5), 5.0));  // armed now
    }
}
