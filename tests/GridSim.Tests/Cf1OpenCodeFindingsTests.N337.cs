using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N337 (CF1, October 2026): IEC 60870-5-101 / -104 types 5 / 32 (step position, VTI + QDS, with and without
/// CP56Time2a) and 7 / 33 (bitstring of 32 bits, BSI + QDS, with and without CP56Time2a) get an explicit codec,
/// <see cref="Iec104StatusTypes"/>. The interposer pipeline is NOT changed: R089's pins (Iec104DoublePointTimedTests,
/// R089ScaffoldingLabelTests) keep these types counted-unsupported in <see cref="Iec104Asdu"/>, and one test below
/// re-checks that on the very bytes this codec decodes. Expected bytes are written out by hand from the published type
/// layouts (the standard's text is not on disk: [VERIFY] items are named in the codec's summary).
/// </summary>
public sealed partial class Cf1OpenCodeFindingsTests
{
    // 2026-07-20 13:45:12.500 UTC, a Monday: ms 12500 = 0x30D4; day-of-week 1 in bits 5-7 of the day octet.
    private static readonly DateTime N337Time = new(2026, 7, 20, 13, 45, 12, 500, DateTimeKind.Utc);
    private static readonly byte[] N337TimeBytes = { 0xD4, 0x30, 45, 13, (byte)(20 | (1 << 5)), 7, 26 };

    [Fact]
    public void N337_cp56time2a_encodes_by_hand_and_round_trips_through_the_existing_decoder()
    {
        Assert.Equal(N337TimeBytes, Iec104StatusTypes.EncodeCp56Time2a(N337Time));
        Assert.Equal(N337Time, TimeDecode.Cp56Time2a(N337TimeBytes));
        // A Sunday is day-of-week 7.
        byte[] sunday = Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal((byte)(11 | (7 << 5)), sunday[4]);
    }

    [Fact]
    public void N337_cp56time2a_encoding_refuses_local_out_of_century_and_sub_millisecond_times()
    {
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeCp56Time2a(new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeCp56Time2a(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1)));
    }

    [Fact]
    public void N337_type_5_decodes_the_seven_bit_two_s_complement_value_and_the_transient_bit()
    {
        // Type 5, SQ = 0, four objects, COT 3 (spontaneous), originator 0, common address 0x0102.
        byte[] asdu =
        {
            5, 0x04, 3, 0x00, 0x02, 0x01,
            0x0A, 0x00, 0x00, 0x05, 0x00,        // IOA 10: +5, at rest, good
            0x0B, 0x00, 0x00, 0x7F, 0x80,        // IOA 11: -1, at rest, IV
            0x0C, 0x00, 0x00, 0xC0, 0x00,        // IOA 12: -64, transient
            0x00, 0x00, 0x01, 0x3F, 0x11,        // IOA 65536: +63, BL + OV
        };
        var (h, objs) = Iec104StatusTypes.DecodeStepPositions(asdu);
        Assert.Equal(new Iec104StatusTypes.Header(5, 3, false, false, 0, 0x0102, false), h);
        Assert.Equal(new[]
        {
            new Iec104StepPosition(10, 5, false, 0x00, null),
            new Iec104StepPosition(11, -1, false, 0x80, null),
            new Iec104StepPosition(12, -64, true, 0x00, null),
            new Iec104StepPosition(65536, 63, false, 0x11, null),
        }, objs.ToArray());
        // Encoding the decoded objects gives the same bytes back.
        Assert.Equal(asdu, Iec104StatusTypes.EncodeStepPositions(h, objs));
    }

    [Fact]
    public void N337_type_32_carries_the_cp56time2a_tag_and_round_trips()
    {
        var h = new Iec104StatusTypes.Header(32, 3, false, false, 7, 1, false);
        var objs = new[] { new Iec104StepPosition(4001, -12, true, 0x40, N337Time) };
        byte[] bytes = Iec104StatusTypes.EncodeStepPositions(h, objs);
        var expected = new List<byte> { 32, 0x01, 3, 7, 0x01, 0x00, 0xA1, 0x0F, 0x00, (byte)(0x74 | 0x80), 0x40 };
        expected.AddRange(N337TimeBytes);                    // -12 = 0b1110100 in seven bits = 0x74, T bit set
        Assert.Equal(expected.ToArray(), bytes);
        var (h2, back) = Iec104StatusTypes.DecodeStepPositions(bytes);
        Assert.Equal(h, h2);
        Assert.Equal(objs, back.ToArray());
    }

    [Fact]
    public void N337_type_7_reads_the_bitstring_least_significant_octet_first()
    {
        byte[] asdu =
        {
            7, 0x02, 20, 0x00, 0x01, 0x00,                       // 2 objects, COT 20 (interrogated by station)
            0x01, 0x02, 0x00, 0x78, 0x56, 0x34, 0x12, 0x00,      // IOA 513: 0x12345678
            0x02, 0x02, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x20,      // IOA 514: all ones, SB
        };
        var (h, objs) = Iec104StatusTypes.DecodeBitstrings(asdu);
        Assert.Equal(new Iec104StatusTypes.Header(7, 20, false, false, 0, 1, false), h);
        Assert.Equal(new[]
        {
            new Iec104Bitstring(513, 0x12345678u, 0x00, null),
            new Iec104Bitstring(514, 0xFFFFFFFFu, 0x20, null),
        }, objs.ToArray());
        Assert.Equal(asdu, Iec104StatusTypes.EncodeBitstrings(h, objs));
    }

    [Fact]
    public void N337_type_33_sequence_form_round_trips_with_one_base_address()
    {
        // SQ = 1: one base IOA (300), three consecutive elements, each BSI + QDS + CP56Time2a. Test and negative bits set.
        var h = new Iec104StatusTypes.Header(33, 5, true, true, 0, 65535, true);
        var objs = new[]
        {
            new Iec104Bitstring(300, 0u, 0x00, N337Time),
            new Iec104Bitstring(301, 1u, 0x00, N337Time),
            new Iec104Bitstring(302, 0x80000000u, 0xF1, N337Time),
        };
        byte[] bytes = Iec104StatusTypes.EncodeBitstrings(h, objs);
        Assert.Equal(6 + 3 + 3 * (5 + 7), bytes.Length);
        Assert.Equal(new byte[] { 33, 0x83, 5 | 0x40 | 0x80, 0, 0xFF, 0xFF, 0x2C, 0x01, 0x00 }, bytes[..9]);
        var (h2, back) = Iec104StatusTypes.DecodeBitstrings(bytes);
        Assert.Equal(h, h2);
        Assert.Equal(objs, back.ToArray());
    }

    [Fact]
    public void N337_a_cp56time2a_with_its_invalid_bit_decodes_to_a_null_time_as_for_every_other_timed_type()
    {
        byte[] t = (byte[])N337TimeBytes.Clone();
        t[2] |= 0x80;                                                    // minute octet IV bit
        var b = new List<byte> { 32, 0x01, 3, 0, 1, 0, 1, 0, 0, 0x02, 0x00 };
        b.AddRange(t);
        var (_, objs) = Iec104StatusTypes.DecodeStepPositions(b.ToArray());
        Assert.Null(Assert.Single(objs).TimeUtc);
        Assert.Equal(2, objs[0].Position);
    }

    [Fact]
    public void N337_decoding_refuses_wrong_types_empty_truncated_and_trailing_asdus()
    {
        byte[] good = { 5, 0x01, 3, 0, 1, 0, 0x0A, 0x00, 0x00, 0x05, 0x00 };
        Assert.Single(Iec104StatusTypes.DecodeStepPositions(good).Objects);
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeBitstrings(good));                 // type 5 is not 7 / 33
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(new byte[] { 9, 1, 3, 0, 1, 0, 1, 0, 0, 0, 0, 0 }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(good.AsSpan(0, 5)));         // short DUI
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(good.AsSpan(0, 10)));        // truncated element
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(new byte[] { 5, 0x00, 3, 0, 1, 0 })); // zero objects
        byte[] trailing = new byte[good.Length + 1];
        good.CopyTo(trailing, 0);
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(trailing));          // one octet too many
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.DecodeStepPositions(new byte[] { 5, 0x81, 3, 0, 1, 0, 0x0A })); // SQ base IOA cut
    }

    [Fact]
    public void N337_encoding_refuses_out_of_range_and_inconsistent_fields()
    {
        var h5 = new Iec104StatusTypes.Header(5, 3, false, false, 0, 1, false);
        var h32 = h5 with { TypeId = 32 };
        var ok = new Iec104StepPosition(1, 0, false, 0, null);
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, new[] { ok with { Position = 64 } }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, new[] { ok with { Position = -65 } }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, new[] { ok with { Quality = 0x02 } }));    // reserved bit
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, new[] { ok with { Ioa = 0x1000000 } }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, new[] { ok with { TimeUtc = N337Time } })); // untimed type with a time
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h32, new[] { ok }));                          // timed type, no time
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5 with { TypeId = 7 }, new[] { ok }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5 with { Cause = 0 }, new[] { ok }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5 with { Cause = 64 }, new[] { ok }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5 with { CommonAddress = 65536 }, new[] { ok }));
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, Array.Empty<Iec104StepPosition>()));
        var many = new Iec104StepPosition[128];
        for (int i = 0; i < many.Length; i++) many[i] = ok with { Ioa = i };
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5, many));
        // SQ = 1 needs consecutive addresses.
        Assert.Throws<ArgumentException>(() => Iec104StatusTypes.EncodeStepPositions(h5 with { Sequence = true }, new[] { ok, ok with { Ioa = 3 } }));
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)32)]
    [InlineData((byte)7)]
    [InlineData((byte)33)]
    public void N337_the_pipeline_decoder_still_counts_these_types_unsupported(byte typeId)
    {
        // Was (CF1): "R089's rule is unchanged: Iec104Asdu turns these into no objects. Only the explicit codec decodes them."
        // Moved 2026-10 (END-PASS CF2 row 4, end pass; IEC 60870-5-101 / 104 type ids 5 / 32 / 7 / 33 decoded by N337,
        // Ruling R1 and Mark's go on the end-pass moves 2026-10-11): N337 (ruled by main) reverses the R089 rule quoted
        // above, so the pipeline decoder now decodes these types with the explicit codec's element layouts; the old
        // behaviour no longer exists. Before -> after: Assert.False(IsSupportedType) -> Assert.True (observed: Expected
        // False, Actual True, 1.95.273-275 runs); Assert.Empty(Parse(asdu).Objects) -> Assert.Single (derived, not
        // observed: the run stopped at the first assertion; Iec104Asdu.Layout gives 2 / 5 element octets for these
        // types and the frame carries one object). The name is kept so existing references still resolve.
        Assert.True(Iec104StatusTypes.Handles(typeId));
        Assert.True(Iec104Asdu.IsSupportedType(typeId));
        bool timed = typeId is 32 or 33;
        var b = new List<byte> { typeId, 0x01, 3, 0, 1, 0, 0x0A, 0x00, 0x00 };
        b.AddRange(typeId is 5 or 32 ? new byte[] { 0x05, 0x00 } : new byte[] { 1, 2, 3, 4, 0x00 });
        if (timed) b.AddRange(N337TimeBytes);
        byte[] asdu = b.ToArray();
        Assert.Single(Iec104Asdu.Parse(asdu).Objects);
        int decoded = typeId is 5 or 32
            ? Iec104StatusTypes.DecodeStepPositions(asdu).Objects.Count
            : Iec104StatusTypes.DecodeBitstrings(asdu).Objects.Count;
        Assert.Equal(1, decoded);
    }
}
