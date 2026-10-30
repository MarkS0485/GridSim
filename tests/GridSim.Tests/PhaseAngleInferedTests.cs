using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Gda.Neso;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The GDA PHASEANGLE_INFERED series (the system COI angle used as reference frame + true frequency): re-zero
/// to 0° at the window start, at-or-before lookup, and the common-mode de-rotation it feeds into the feed.
/// </summary>
public class PhaseAngleInferedTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);

    private static PhaseAngleInferedSeries Series() => new(new[]
    {
        // +0.1 Hz for 1 s advances 36°/s; start at an arbitrary absolute phase 100°.
        new PhaseAngleSample(T0, 100.0, 50.1, 0.0),
        new PhaseAngleSample(T0.AddSeconds(1), 136.0, 50.1, 0.0),
        new PhaseAngleSample(T0.AddSeconds(2), 172.0, 50.1, 0.0),
    });

    [Fact]
    public void ReZeroed_puts_zero_at_the_window_start_and_tracks_the_drift()
    {
        PhaseAngleInferedSeries z = Series().ReZeroed();
        Assert.Equal(0.0, z.Samples[0].PhaseDeg, 6);   // 0° at t0
        Assert.Equal(36.0, z.Samples[1].PhaseDeg, 6);  // +36° after 1 s at +0.1 Hz
        Assert.Equal(72.0, z.Samples[2].PhaseDeg, 6);
    }

    [Fact]
    public void At_returns_the_nearest_sample_at_or_before()
    {
        // Moved 2026-10 (Q5, authorised by Mark): this test used to expect an instant 10 s BEFORE the first sample
        // to return the first sample (data from after the instant) and an instant 97 s AFTER the last sample to
        // return the last one (a stale angle), i.e. it encoded the R299 defect. The system angle advances
        // 360 degrees per second per hertz of deviation (here 36 degrees per second), so a 97 s old sample is
        // 3492 degrees out. Now: at or before, and at most MaxAge old (default 2 s for the 1 Hz product), else throw.
        PhaseAngleInferedSeries z = Series().ReZeroed();
        Assert.Equal(36.0, z.At(T0.AddSeconds(1.4)).PhaseDeg, 6);   // between samples 1 and 2 -> sample 1
        Assert.Throws<InvalidOperationException>(() => z.At(T0.AddSeconds(-10)));   // before the start -> refused
        Assert.Throws<InvalidOperationException>(() => z.At(T0.AddSeconds(99)));    // 97 s after the end -> stale
        Assert.Equal(72.0, z.At(T0.AddSeconds(4)).PhaseDeg, 6);     // 2 s after the last sample: exactly MaxAge, kept
        Assert.Equal(72.0, z.At(T0.AddSeconds(99), TimeSpan.FromSeconds(97)).PhaseDeg, 6);   // the age is configurable
    }

    [Fact]
    public void The_maximum_age_defaults_to_two_seconds_is_configurable_and_survives_re_zeroing()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), PhaseAngleInferedSeries.DefaultMaxAge);
        Assert.Equal(PhaseAngleInferedSeries.DefaultMaxAge, Series().MaxAge);

        var tight = new PhaseAngleInferedSeries(Series().Samples) { MaxAge = TimeSpan.FromMilliseconds(500) };
        PhaseAngleInferedSeries z = tight.ReZeroed();
        Assert.Equal(TimeSpan.FromMilliseconds(500), z.MaxAge);
        Assert.Equal(36.0, z.At(T0.AddSeconds(1.5)).PhaseDeg, 6);                   // 0.5 s old: kept
        Assert.Throws<InvalidOperationException>(() => z.At(T0.AddSeconds(1.6)));   // 0.6 s old: stale

        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseAngleInferedSeries(Series().Samples) { MaxAge = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => Series().At(T0, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void A_gap_inside_the_series_longer_than_the_maximum_age_is_refused()
    {
        var gappy = new PhaseAngleInferedSeries(new[]
        {
            new PhaseAngleSample(T0, 0.0, 50.0, 0.0),
            new PhaseAngleSample(T0.AddSeconds(10), 5.0, 50.0, 0.0),
        });
        Assert.Equal(0.0, gappy.At(T0.AddSeconds(2)).PhaseDeg);
        var ex = Assert.Throws<InvalidOperationException>(() => gappy.At(T0.AddSeconds(5)));
        Assert.Contains("stale", ex.Message);
        Assert.Equal(5.0, gappy.At(T0.AddSeconds(10)).PhaseDeg);
    }

    [Fact]
    public void Common_mode_de_rotates_an_absolute_feed_in_referenced_mode()
    {
        // A per-bus feed reported in the inertial frame (each bus = truth + theta_sys); subtracting the GDA
        // system angle (CommonModeDeg) in 'referenced' mode recovers the slack-frame truth angles.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        double thetaSys = Series().ReZeroed().At(T0.AddSeconds(1)).PhaseDeg;   // = 36°

        var obs = new List<PhaseAngleObs>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            if (m.Buses.First(b => b.Id == truth.BusIds[i]).Type != BusType.Slack)
                obs.Add(new PhaseAngleObs(truth.BusIds[i], truth.VaDeg[i] + thetaSys, T0, 50.0));

        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(m, obs,
            new PhaseAngleOptions { Mode = PhaseAngleReference.AlreadyReferenced, TargetUtc = T0, CommonModeDeg = thetaSys });

        var truthById = truth.BusIds.Select((id, i) => (id, va: truth.VaDeg[i])).ToDictionary(x => x.id, x => x.va);
        foreach (Measurement row in rows)
            Assert.Equal(truthById[row.Bus], row.Value, 6);   // theta_sys removed -> back to the slack-frame truth
    }
}
