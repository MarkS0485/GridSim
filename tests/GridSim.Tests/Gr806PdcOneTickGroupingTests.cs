using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-806: the PDC aligned PMU samples exactly to the 100 ns tick, so PMUs whose TIME_BASE rounds the same reporting
/// instant to adjacent ticks formed two superframes, each listing the other PMUs as dropped. A sample now joins a bucket
/// when the bucket's span stays within one tick. Pinned: two PMUs one tick apart form one superframe at the earlier tick
/// whatever the arrival order; ticks two apart stay separate; a bucket never stretches past one tick; a straggler one tick
/// after an emitted instant is late; samples on exactly the same tick group as before; the ready test uses the latest
/// tick of the bucket.
/// </summary>
public sealed class Gr806PdcOneTickGroupingTests
{
    private static readonly DateTime T0 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PmuSample Sample(int id, DateTime t) => new(id, t, new[] { new Phasor(id, 0) }, 50.0, 0.0);

    private static PhasorDataConcentrator Pdc(params int[] ids) => new(ids, TimeSpan.FromMilliseconds(100));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr806_two_pmus_one_tick_apart_form_one_superframe_at_the_earlier_tick(bool laterFirst)
    {
        PhasorDataConcentrator pdc = Pdc(1, 2);
        PmuSample a = Sample(1, T0), b = Sample(2, T0.AddTicks(1));
        if (laterFirst) { Assert.True(pdc.Ingest(b)); Assert.True(pdc.Ingest(a)); }
        else { Assert.True(pdc.Ingest(a)); Assert.True(pdc.Ingest(b)); }

        PdcSuperframe f = Assert.Single(pdc.FlushAll());
        Assert.Equal(T0, f.TimeUtc);   // the earliest tick, whatever arrived first
        Assert.Equal(new[] { 1, 2 }, f.Samples.Keys.Order());
        Assert.Empty(f.Dropped);
        Assert.Equal(T0.AddTicks(1), f.Samples[2].TimeUtc);   // each sample keeps its own decoded time
    }

    [Fact]
    public void Gr806_ticks_two_apart_stay_two_superframes()
    {
        PhasorDataConcentrator pdc = Pdc(1, 2);
        pdc.Ingest(Sample(1, T0));
        pdc.Ingest(Sample(2, T0.AddTicks(2)));
        IReadOnlyList<PdcSuperframe> frames = pdc.FlushAll();
        Assert.Equal(2, frames.Count);
        Assert.Equal(new[] { 2 }, frames[0].Dropped);
        Assert.Equal(new[] { 1 }, frames[1].Dropped);
    }

    [Fact]
    public void Gr806_a_bucket_never_stretches_past_one_tick()
    {
        // T0 and T0+1 group; T0+2 would make the span two ticks, so it starts its own bucket.
        PhasorDataConcentrator pdc = Pdc(1, 2, 3);
        pdc.Ingest(Sample(1, T0));
        pdc.Ingest(Sample(2, T0.AddTicks(1)));
        pdc.Ingest(Sample(3, T0.AddTicks(2)));
        IReadOnlyList<PdcSuperframe> frames = pdc.FlushAll();
        Assert.Equal(2, frames.Count);
        Assert.Equal(new[] { 1, 2 }, frames[0].Samples.Keys.Order());
        Assert.Equal(new[] { 3 }, frames[1].Samples.Keys.Order());
        Assert.Equal(T0.AddTicks(2), frames[1].TimeUtc);
    }

    [Fact]
    public void Gr806_a_straggler_one_tick_after_an_emitted_instant_is_late()
    {
        PhasorDataConcentrator pdc = Pdc(1, 2);
        pdc.Ingest(Sample(1, T0));
        Assert.Single(pdc.Flush(T0.AddMilliseconds(150)));
        Assert.False(pdc.Ingest(Sample(2, T0.AddTicks(1))));
        Assert.Equal(1, pdc.LateSamples);
        Assert.Empty(pdc.FlushAll());
    }

    [Fact]
    public void Gr806_the_ready_test_uses_the_latest_tick_of_the_bucket()
    {
        PhasorDataConcentrator pdc = Pdc(1, 2);
        pdc.Ingest(Sample(1, T0));
        pdc.Ingest(Sample(2, T0.AddTicks(1)));
        // Watermark exactly 100 ms after T0: T0 is at the cutoff but T0 + 1 tick is not, so the instant waits.
        Assert.Empty(pdc.Flush(T0.AddMilliseconds(100)));
        Assert.Single(pdc.Flush(T0.AddMilliseconds(100).AddTicks(1)));
    }

    [Fact]
    public void Gr806_samples_on_the_same_tick_group_exactly_as_before()
    {
        PhasorDataConcentrator pdc = Pdc(1, 2);
        pdc.Ingest(Sample(1, T0));
        pdc.Ingest(Sample(2, T0));
        pdc.Ingest(Sample(1, T0.AddMilliseconds(20)));
        IReadOnlyList<PdcSuperframe> frames = pdc.FlushAll();
        Assert.Equal(2, frames.Count);
        Assert.Equal(T0, frames[0].TimeUtc);
        Assert.Empty(frames[0].Dropped);
        Assert.Equal(new[] { 2 }, frames[1].Dropped);
    }
}
