using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Round-two fixes on the dynamics path (October 2026 review): GR-540 (an event time meant to be on the step grid fires
/// on its step, not one late), GR-567 (a grid-following small-signal block refuses a rating that would silently give no
/// droop) and GR-572 (a measured frequency trace stamped out of time order is refused, not stepped over).
/// </summary>
public class GrRound2DynamicsTests
{
    [Fact]
    public void Gr540_an_event_on_the_grid_is_reached_on_its_own_step_although_k_dt_rounds_below_it()
    {
        // 11 * 0.03 is 0.32999999999999996 in binary floating point, just below 0.33: a strict AtSeconds <= t fired the
        // event one step late. The tolerant rule (one millionth of a step) reaches it on step 11.
        double dt = 0.03;
        Assert.False(0.33 <= 11 * dt, "the premise: k * dt rounds below the event time");
        Assert.True(SwingKernel.EventReached(0.33, 11 * dt, dt));
        Assert.False(SwingKernel.EventReached(0.33, 10 * dt, dt), "a whole step early is not reached");
        Assert.False(SwingKernel.EventReached(double.NaN, 11 * dt, dt), "a NaN time is never reached");

        var ev = new[] { new FrequencyEvent(0.33, -1000.0) };
        Assert.Equal(-1000.0, SwingKernel.ActiveEventMw(ev, 11 * dt, dt));
        Assert.Equal(0.0, SwingKernel.ActiveEventMw(ev, 10 * dt, dt));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Gr567_a_grid_following_block_refuses_a_rating_that_gives_no_droop(double ratedMw)
    {
        AssetClass cls = AssetTestHelpers.GflClass("gfl-gr567");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => LinearInverterModel.ForGfl(cls, 7, ratedMw, 50.0));
        Assert.Contains("bus 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr567_a_positive_rating_still_scales_the_droop()
    {
        AssetClass cls = AssetTestHelpers.GflClass("gfl-gr567");
        InverterLinearBlock a = LinearInverterModel.ForGfl(cls, 2, 100.0, 50.0);
        InverterLinearBlock b = LinearInverterModel.ForGfl(cls, 2, 200.0, 50.0);
        // The droop sits in the lag row (-droopGain / tau); twice the rating is twice the gain, exactly.
        Assert.Equal(2.0 * a.Aself[2, 0], b.Aself[2, 0]);
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<FrequencySample> Trace(params (double Seconds, double Hz)[] pts)
        => pts.Select(p => new FrequencySample(T0.AddSeconds(p.Seconds), p.Hz)).ToList();

    [Fact]
    public void Gr572_a_trace_out_of_time_order_is_refused_by_every_scorer()
    {
        List<FrequencySample> bad = Trace((0, 50.0), (2, 49.9), (1, 49.95), (3, 49.9));
        Assert.Throws<InvalidDataException>(() => RocofUpsampler.Upsample(bad, 10).ToList());
        Assert.Throws<InvalidDataException>(() => RocofUpsampler.Rocof(bad).ToList());
        Assert.Throws<InvalidDataException>(() => RocofUpsampler.Summarize(bad));
        Assert.Throws<InvalidDataException>(() => FrequencyLimits.AssessTrace(bad));
    }

    [Fact]
    public void Gr572_a_repeated_time_stamp_is_still_skipped()
    {
        // A zero interval has no rate: it is stepped over as before, and the trace still scores.
        List<FrequencySample> dup = Trace((0, 50.0), (1, 49.9), (1, 49.9), (2, 49.8));
        Assert.Equal(2, RocofUpsampler.Rocof(dup).Count());
        Assert.Equal(4, RocofUpsampler.Summarize(dup).Count);
    }
}
