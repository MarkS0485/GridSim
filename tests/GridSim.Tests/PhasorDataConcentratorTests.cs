using System;
using System.Linq;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The PDC time-aligner: it groups interleaved PMU samples onto common instants, holds an instant until it
/// falls behind the wait-window, and flags PMUs that did not report as dropouts - deterministically.
/// </summary>
public class PhasorDataConcentratorTests
{
    private static readonly DateTime T0 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PmuSample Sample(int id, DateTime t) =>
        new(id, t, new[] { new Phasor(id, 0) }, 50.0, 0.0);

    [Fact]
    public void Aligns_pmus_onto_common_instants_in_time_order()
    {
        var pdc = new PhasorDataConcentrator(new[] { 1, 2, 3 }, TimeSpan.FromMilliseconds(100));
        DateTime t1 = T0, t2 = T0.AddMilliseconds(20);

        // Interleaved arrival, all three report at both instants.
        pdc.Ingest(Sample(2, t1));
        pdc.Ingest(Sample(1, t1));
        pdc.Ingest(Sample(3, t2));
        pdc.Ingest(Sample(1, t2));
        pdc.Ingest(Sample(3, t1));
        pdc.Ingest(Sample(2, t2));

        var frames = pdc.FlushAll();
        Assert.Equal(2, frames.Count);
        Assert.Equal(t1, frames[0].TimeUtc); // time order
        Assert.Equal(t2, frames[1].TimeUtc);
        Assert.Equal(new[] { 1, 2, 3 }, frames[0].Samples.Keys.OrderBy(k => k));
        Assert.Empty(frames[0].Dropped);
        Assert.Empty(frames[1].Dropped);
    }

    [Fact]
    public void Flags_a_missing_pmu_as_dropped()
    {
        var pdc = new PhasorDataConcentrator(new[] { 1, 2, 3 }, TimeSpan.FromMilliseconds(100));
        pdc.Ingest(Sample(1, T0));
        pdc.Ingest(Sample(3, T0)); // PMU 2 never reports at T0

        var frames = pdc.FlushAll();
        Assert.Single(frames);
        Assert.Equal(new[] { 2 }, frames[0].Dropped);
        Assert.False(frames[0].Samples.ContainsKey(2));
    }

    [Fact]
    public void Holds_an_instant_until_it_falls_behind_the_wait_window()
    {
        var pdc = new PhasorDataConcentrator(new[] { 1 }, TimeSpan.FromMilliseconds(100));
        pdc.Ingest(Sample(1, T0));

        // Watermark only 50 ms past T0 (< 100 ms wait-window): not ready yet.
        Assert.Empty(pdc.Flush(T0.AddMilliseconds(50)));
        // Watermark 150 ms past T0 (> wait-window): now ready.
        var frames = pdc.Flush(T0.AddMilliseconds(150));
        Assert.Single(frames);
        Assert.Equal(T0, frames[0].TimeUtc);
    }
}
