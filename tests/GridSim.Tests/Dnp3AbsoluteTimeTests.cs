using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-792: a DNP3 absolute time (48-bit milliseconds since 1970) past the last instant DateTime can hold is out of
/// range, so the decoder returns null as its contract says, instead of throwing ArgumentOutOfRangeException.
/// </summary>
public class Dnp3AbsoluteTimeTests
{
    private static byte[] Le48(long ms)
    {
        var t = new byte[6];
        for (int i = 0; i < 6; i++) t[i] = (byte)(ms >> (8 * i));
        return t;
    }

    [Fact]
    public void An_in_range_time_decodes_as_utc()
    {
        DateTime? t = TimeDecode.Dnp3AbsoluteTime(Le48(1_700_000_000_123));
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1_700_000_000_123), t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
    }

    [Fact]
    public void The_last_representable_millisecond_decodes_and_the_next_is_null()
    {
        long max = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        Assert.NotNull(TimeDecode.Dnp3AbsoluteTime(Le48(max)));
        Assert.Null(TimeDecode.Dnp3AbsoluteTime(Le48(max + 1)));
        Assert.Null(TimeDecode.Dnp3AbsoluteTime(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));
    }
}
