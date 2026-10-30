using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A CP56Time2a tag with the summer-time (SU) bit set is local summer time, one hour off UTC in GB. GridSim reads every
/// tag as UTC, so such a tag is refused as an invalid time (null), like an IV-flagged one, instead of being read an hour
/// late without a word.
/// </summary>
public class Cp56SummerTimeTests
{
    [Fact]
    public void A_tag_with_the_summer_time_bit_is_an_invalid_time()
    {
        var t = new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc);
        byte[] utcTag = Iec104StatusTypes.EncodeCp56Time2a(t);
        Assert.Equal(t, TimeDecode.Cp56Time2a(utcTag));

        byte[] summer = (byte[])utcTag.Clone();
        summer[3] |= 0x80;   // SU: the station stamps local summer time
        Assert.Null(TimeDecode.Cp56Time2a(summer));
    }
}
