using System.Runtime;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

public class GcScopeTests
{
    [Fact]
    public void LowLatency_sets_sustained_low_latency_and_restores()
    {
        GCLatencyMode before = GCSettings.LatencyMode;
        using (GcScope.LowLatency())
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        Assert.Equal(before, GCSettings.LatencyMode);
    }

    [Fact]
    public void Overlapping_scopes_ended_out_of_order_restore_the_original_mode()
    {
        // GR-862: the first scope to end must not restore the mode while the other is still running.
        GCLatencyMode before = GCSettings.LatencyMode;
        IDisposable a = GcScope.LowLatency();
        IDisposable b = GcScope.LowLatency();
        a.Dispose();
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        a.Dispose();   // a second Dispose changes nothing
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        b.Dispose();
        Assert.Equal(before, GCSettings.LatencyMode);
    }

    [Fact]
    public void NoGc_is_safe_and_does_not_leave_a_lingering_region()
    {
        using (GcScope.NoGc(16L * 1024 * 1024)) { /* bounded work would go here */ }
        Assert.NotEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
    }
}
