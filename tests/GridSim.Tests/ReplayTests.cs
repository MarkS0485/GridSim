using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

public class ReplayTests
{
    private static List<FrequencyPoint> Frames(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new FrequencyPoint(new DateTime(2024, 1, 1).AddMilliseconds(i * 20), 50.0 + i * 0.001, 0.05))
            .ToList();

    [Fact]
    public void Advances_and_clamps_to_range()
    {
        var rc = new ReplayController(Frames(5));
        Assert.Equal(0, rc.Index);
        rc.Advance();
        Assert.Equal(1, rc.Index);
        rc.Advance(100);
        Assert.Equal(4, rc.Index);
        Assert.True(rc.AtEnd);
        rc.Rewind(100);
        Assert.Equal(0, rc.Index);
    }

    [Fact]
    public void Slices_per_second_set_the_slice_period()
    {
        var rc = new ReplayController(Frames(3), 50);
        Assert.Equal(0.02, rc.SlicePeriod.TotalSeconds, 6);  // 50 sps -> 20 ms real time
        rc.Faster();
        Assert.Equal(100, rc.SlicesPerSecond, 6);
        rc.Slower();
        rc.Slower();
        Assert.Equal(25, rc.SlicesPerSecond, 6);
    }

    [Fact]
    public void Speed_is_clamped()
    {
        var rc = new ReplayController(Frames(3));
        rc.SetSpeed(1e9);
        Assert.Equal(ReplayController.MaxSlicesPerSecond, rc.SlicesPerSecond, 6);
        rc.SetSpeed(0);
        Assert.Equal(ReplayController.MinSlicesPerSecond, rc.SlicesPerSecond, 6);
    }

    [Fact]
    public void Seek_and_progress_work()
    {
        var rc = new ReplayController(Frames(101));
        rc.SeekFraction(0.5);
        Assert.Equal(50, rc.Index);
        Assert.Equal(0.5, rc.ProgressFraction, 6);
        rc.SeekFraction(2.0);   // clamps
        Assert.Equal(100, rc.Index);
        Assert.True(rc.AtEnd);
    }
}
