using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

public class FrequencyTests
{
    private static string SampleCsv => Path.Combine(AppContext.BaseDirectory, "data", "neso", "frequency-sample.csv");

    [Fact]
    public void Reads_the_neso_sample()
    {
        var samples = NesoFrequencyCsv.Read(SampleCsv).ToList();
        Assert.Equal(300, samples.Count);
        Assert.Equal(new DateTime(2024, 8, 1, 0, 0, 0), samples[0].Time);
        Assert.Equal(50.105, samples[0].Hz, 3);
    }

    [Fact]
    public void Rocof_is_the_slope_between_frames()
    {
        var s = new[]
        {
            new FrequencySample(new DateTime(2024, 1, 1, 0, 0, 0), 50.00),
            new FrequencySample(new DateTime(2024, 1, 1, 0, 0, 1), 49.90), // -0.10 Hz in 1 s
        };
        var rocof = RocofUpsampler.Rocof(s).ToList();
        Assert.Single(rocof);
        Assert.Equal(-0.10, rocof[0].RocofHzPerS, 6);
    }

    [Fact]
    public void Upsamples_one_second_to_fifty_twenty_ms_points()
    {
        var s = new[]
        {
            new FrequencySample(new DateTime(2024, 1, 1, 0, 0, 0), 50.0),
            new FrequencySample(new DateTime(2024, 1, 1, 0, 0, 1), 50.1),
        };
        var pts = RocofUpsampler.Upsample(s, 50).ToList();

        Assert.Equal(51, pts.Count);                       // 50 across the interval + closing point
        Assert.Equal(50.0, pts[0].Hz, 9);                  // start
        Assert.Equal(0.1, pts[0].RocofHzPerS, 9);          // RoCoF carried
        Assert.Equal(20, (pts[1].Time - pts[0].Time).TotalMilliseconds, 6); // 20 ms step
        Assert.Equal(50.05, pts[25].Hz, 6);                // halfway up the ramp
        Assert.Equal(50.1, pts[50].Hz, 6);                 // closing point
    }

    [Fact]
    public void Summarize_reports_range_and_peak_rocof()
    {
        var stats = RocofUpsampler.Summarize(NesoFrequencyCsv.Read(SampleCsv));
        Assert.Equal(300, stats.Count);
        Assert.InRange(stats.MinHz, 49.0, 50.0);
        Assert.InRange(stats.MaxHz, 50.0, 51.0);
        Assert.InRange(stats.MeanHz, 49.9, 50.1);
        Assert.True(stats.MaxAbsRocofHzPerS > 0);
    }
}
