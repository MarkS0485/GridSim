using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-848: HybridMultiRateScheduler has no production caller and is kept for its tests (now marked TEST-ONLY in its doc
/// comment). Its two documented fail-silent inputs are refused: an end before the start (an empty schedule) and mixed
/// DateTimeKind values (every instant stamped with the start's kind).
/// </summary>
public class Gr848HybridSchedulerFailLoudTests
{
    private static readonly DateTime T0 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Gr848_an_end_before_the_start_is_refused()
        => Assert.Throws<ArgumentException>(() =>
            HybridMultiRateScheduler.Schedule(T0, T0.AddSeconds(-1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5)));

    [Fact]
    public void Gr848_mixed_datetime_kinds_are_refused()
    {
        DateTime localEnd = DateTime.SpecifyKind(T0.AddSeconds(5), DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() =>
            HybridMultiRateScheduler.Schedule(T0, localEnd, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void Gr848_a_single_instant_window_is_one_scada_update()
    {
        IReadOnlyList<ScheduledUpdate> s = HybridMultiRateScheduler.Schedule(T0, T0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5));
        ScheduledUpdate only = Assert.Single(s);
        Assert.True(only.IsScada);
        Assert.Equal(T0, only.Time);
    }
}
