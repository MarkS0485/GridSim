using System;
using System.Linq;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>The hybrid multi-rate scheduler: interleaves SCADA (5 s) and PMU (0.5 s) updates in time order,
/// with about nine linear PMU updates per SCADA cycle and SCADA winning coincident instants.</summary>
public class HybridMultiRateSchedulerTests
{
    private static readonly DateTime T0 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Interleaves_scada_and_pmu_with_nine_pmu_per_scada_cycle()
    {
        var schedule = HybridMultiRateScheduler.Schedule(
            T0, T0.AddSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5));

        // SCADA at 0 and 5 s; PMU at 0.5..4.5 s (0 and 5 coincide with SCADA -> SCADA wins).
        Assert.Equal(2, schedule.Count(u => u.IsScada));
        Assert.Equal(9, schedule.Count(u => !u.IsScada));
        Assert.True(schedule[0].IsScada && schedule[0].Time == T0);
        Assert.True(schedule[^1].IsScada && schedule[^1].Time == T0.AddSeconds(5));

        // Strictly increasing in time.
        for (int i = 1; i < schedule.Count; i++)
            Assert.True(schedule[i].Time > schedule[i - 1].Time);
    }
}
