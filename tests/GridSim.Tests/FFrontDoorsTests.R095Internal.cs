using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R095 remainder (stream F): the Core APIs with no sensible user are internal, not public, with the reason in code.
/// HybridMultiRateScheduler interleaves SCADA and PMU estimator updates, but no hybrid estimator exists to drive it; it
/// stays (tested through InternalsVisibleTo) for when one does.
/// </summary>
public sealed partial class FFrontDoorsTests
{
    [Fact]
    public void R095_the_undriven_hybrid_scheduler_is_internal_with_its_reason()
    {
        Assert.False(typeof(ScheduledUpdate).IsPublic);
        Assert.False(typeof(HybridMultiRateScheduler).IsPublic);
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Simulation", "HybridMultiRateScheduler.cs"));
        Assert.Contains("INTERNAL. A schedule with no estimator to drive has no user", src, StringComparison.Ordinal);
        // Still the same schedule (the behaviour is untouched): 5 s SCADA, 0.5 s PMU over 5 s.
        var t0 = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<ScheduledUpdate> s = HybridMultiRateScheduler.Schedule(t0, t0.AddSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5));
        Assert.Equal(11, s.Count);
    }
}
