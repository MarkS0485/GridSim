using System;
using System.Buffers.Binary;
using System.Linq;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-804: IEC 104 short floats that are NaN or infinite are counted and never become points, and a time-tagged float
/// whose CP56Time2a is invalid (IV set) is kept, marked invalid and counted.
/// </summary>
public sealed class Iec104ValueAndTimeTests
{
    private static byte[] Apdu(byte[] asdu)
        => new byte[] { 0x68, (byte)(4 + asdu.Length), 0x00, 0x00, 0x00, 0x00 }.Concat(asdu).ToArray();

    // Type 13 (M_ME_NC_1), one object, COT 3, CA 1, IOA 100, the float, QDS 0.
    private static byte[] FloatAsdu(float value)
    {
        var f = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f, value);
        return new byte[] { 13, 0x01, 3, 0x00, 0x01, 0x00, 100, 0x00, 0x00 }.Concat(f).Concat(new byte[] { 0x00 }).ToArray();
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_non_finite_float_is_counted_and_not_a_point(float value)
    {
        var proto = new Iec104Protocol();
        Assert.Empty(proto.Decode(Apdu(FloatAsdu(value)), new QualityFold()));
        Assert.Equal(1, proto.NonFiniteValues);
    }

    [Fact]
    public void A_finite_float_is_a_good_point()
    {
        var proto = new Iec104Protocol();
        RtuPoint p = Assert.Single(proto.Decode(Apdu(FloatAsdu(12.5f)), new QualityFold()));
        Assert.Equal(12.5, p.Value);
        Assert.Equal(RtuQuality.Good, p.Quality);
        Assert.Equal(0, proto.NonFiniteValues);
    }

    [Fact]
    public void A_timed_float_with_an_invalid_time_tag_is_kept_invalid_and_counted()
    {
        // Type 36 (M_ME_TF_1): the float, QDS, then CP56Time2a with the IV bit (0x80 in the minutes octet) set.
        byte[] time = Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        time[2] |= 0x80;
        var f = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f, 50.0f);
        byte[] asdu = new byte[] { 36, 0x01, 3, 0x00, 0x01, 0x00, 101, 0x00, 0x00 }.Concat(f).Concat(new byte[] { 0x00 }).Concat(time).ToArray();

        var proto = new Iec104Protocol();
        RtuPoint p = Assert.Single(proto.Decode(Apdu(asdu), new QualityFold()));
        Assert.True(p.Quality.HasFlag(RtuQuality.Invalid));
        Assert.Null(p.StampUtc);
        Assert.Equal(1, proto.InvalidTimeTags);
    }
}
