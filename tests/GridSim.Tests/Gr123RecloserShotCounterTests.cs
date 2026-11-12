using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-123: the auto-recloser (a stage with a finite AutoRestoreDelayS) had no shot counter or lockout, so on a persistent
/// fault it tripped and reclosed for as long as the fault lasted. ProtectionStage.MaxRecloseShots now limits the
/// automatic reclosures; after the last shot the next operation latches. The default (int.MaxValue) keeps the old
/// unlimited behaviour, so every existing stage is unchanged.
/// </summary>
public class Gr123RecloserShotCounterTests
{
    private const double Dt = 0.1;

    private static ProtectionStage Recloser(int shots = int.MaxValue) => new(
        MonitoredQuantity.Frequency, ThresholdSense.Below, Threshold: 49.0, DwellSeconds: Dt, ProtectionAction.TripInfeed,
        AutoRestoreDelayS: 1.0, MaxRecloseShots: shots);

    /// <summary>Drive a persistent under-frequency condition for <paramref name="steps"/> substeps; count the edges.</summary>
    private static (int Fired, int Restored) Persistent(ProtectionEvaluator e, int steps)
    {
        int fired = 0, restored = 0;
        for (int n = 0; n < steps; n++)
        {
            ProtectionEdge edge = e.Step(new AssetStepContext(48.0, 0.0, 0.0, Dt, n * Dt), 48.0);
            if (edge == ProtectionEdge.Fired) fired++;
            if (edge == ProtectionEdge.Restored) restored++;
        }
        return (fired, restored);
    }

    [Fact]
    public void Gr123_two_shots_then_lockout_on_a_persistent_fault()
    {
        var e = new ProtectionEvaluator(Recloser(shots: 2));
        (int fired, int restored) = Persistent(e, 1000);   // 100 s of fault

        Assert.Equal(3, fired);       // trip, reclose, trip, reclose, trip: locked out
        Assert.Equal(2, restored);
        Assert.Equal(2, e.Recloses);
        Assert.True(e.Operated);
        Assert.True(e.LockedOut);
    }

    [Fact]
    public void Gr123_zero_shots_trips_and_locks_out_at_once()
    {
        var e = new ProtectionEvaluator(Recloser(shots: 0));
        (int fired, int restored) = Persistent(e, 200);
        Assert.Equal(1, fired);
        Assert.Equal(0, restored);
        Assert.True(e.LockedOut);
    }

    [Fact]
    public void Gr123_default_is_unlimited_as_before()
    {
        var e = new ProtectionEvaluator(Recloser());
        (int fired, int restored) = Persistent(e, 1000);
        Assert.True(fired >= 40, $"fired {fired}");   // about one trip per 1.1 s for 100 s
        Assert.True(restored >= fired - 1);
        Assert.False(e.LockedOut);
        Assert.Equal(int.MaxValue, new ProtectionStage(MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, 0.1,
            ProtectionAction.TripInfeed).MaxRecloseShots);
    }

    [Fact]
    public void Gr123_a_latching_stage_is_never_reported_as_locked_out()
    {
        // AutoRestoreDelayS = +inf latches by design; that is not a reclose lockout.
        var e = new ProtectionEvaluator(new ProtectionStage(MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, Dt,
            ProtectionAction.TripInfeed));
        Persistent(e, 50);
        Assert.True(e.Operated);
        Assert.False(e.LockedOut);
    }

    [Fact]
    public void Gr123_reset_clears_the_shot_count_and_the_lockout()
    {
        var e = new ProtectionEvaluator(Recloser(shots: 1));
        Persistent(e, 500);
        Assert.True(e.LockedOut);
        e.Reset();
        Assert.Equal(0, e.Recloses);
        Assert.False(e.LockedOut);
        Assert.False(e.Operated);
    }

    [Fact]
    public void Gr123_negative_shot_count_fails_loud()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectionEvaluator(Recloser(shots: -1)));
}
