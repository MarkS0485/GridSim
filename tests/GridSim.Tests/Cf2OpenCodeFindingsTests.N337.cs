using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N337 (CF2, October 2026, ruled by main): IEC 60870-5-104 types 5 / 32 (step position) and 7 / 33 (bitstring of 32
/// bits) are SUPPORTED by the pipeline decoder <see cref="Iec104Asdu"/> and the interposer protocol, not refused. These
/// tests pin that the pipeline decoder reads exactly what the explicit fail-loud codec <see cref="Iec104StatusTypes"/>
/// reads (same layouts, every VTI octet), that the protocol turns them into points with the right type and quality and
/// no longer counts them unsupported, and that the RTU crosswalk refuses to bind them as electrical measurements.
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    // 2026-07-20 13:45:12.500 UTC (a Monday), the same instant the other IEC 104 tests use.
    private static readonly DateTime N337T = new(2026, 7, 20, 13, 45, 12, 500, DateTimeKind.Utc);

    /// <summary>Wrap an ASDU in an I-format APDU (start 0x68, length, four zero control octets).</summary>
    private static byte[] N337Apdu(byte[] asdu)
    {
        var b = new List<byte> { 0x68, (byte)(4 + asdu.Length), 0x00, 0x00, 0x00, 0x00 };
        b.AddRange(asdu);
        return b.ToArray();
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)32)]
    [InlineData((byte)7)]
    [InlineData((byte)33)]
    public void N337_the_four_status_types_are_supported_by_the_pipeline_decoder(byte typeId)
    {
        Assert.True(Iec104Asdu.IsSupportedType(typeId));
    }

    [Fact]
    public void N337_type_5_parses_to_the_same_values_as_the_explicit_codec()
    {
        // Hand-written: type 5, SQ = 0, four objects, COT 3, common address 0x0102.
        byte[] asdu =
        {
            5, 0x04, 3, 0x00, 0x02, 0x01,
            0x0A, 0x00, 0x00, 0x05, 0x00,        // IOA 10: +5, at rest, good
            0x0B, 0x00, 0x00, 0x7F, 0x80,        // IOA 11: -1, at rest, IV
            0x0C, 0x00, 0x00, 0xC0, 0x00,        // IOA 12: -64, transient
            0x00, 0x00, 0x01, 0x3F, 0x11,        // IOA 65536: +63, BL + OV
        };
        Iec104Asdu parsed = Iec104Asdu.Parse(asdu);
        Assert.Equal(new[]
        {
            new Iec104InfoObject(10, 5.0, 0x00, RtuPointType.StepPosition, null, Transient: false),
            new Iec104InfoObject(11, -1.0, 0x80, RtuPointType.StepPosition, null, Transient: false),
            new Iec104InfoObject(12, -64.0, 0x00, RtuPointType.StepPosition, null, Transient: true),
            new Iec104InfoObject(65536, 63.0, 0x11, RtuPointType.StepPosition, null, Transient: false),
        }, parsed.Objects);

        var (_, codec) = Iec104StatusTypes.DecodeStepPositions(asdu);
        Assert.Equal(codec.Select(o => (o.Ioa, (double)o.Position, o.Quality, o.Transient, o.TimeUtc)),
            parsed.Objects.Select(o => (o.Ioa, o.Value, o.Quality, o.Transient, o.TimeUtc)));
    }

    [Fact]
    public void N337_every_vti_octet_parses_as_the_codec_reads_it()
    {
        // Exhaustive over the 256 possible VTI octets: value I7 two's complement and the transient bit agree.
        for (int vti = 0; vti < 256; vti++)
        {
            byte[] asdu = { 5, 0x01, 3, 0, 1, 0, 0x01, 0x00, 0x00, (byte)vti, 0x00 };
            Iec104InfoObject o = Assert.Single(Iec104Asdu.Parse(asdu).Objects);
            Iec104StepPosition c = Assert.Single(Iec104StatusTypes.DecodeStepPositions(asdu).Objects);
            Assert.Equal((double)c.Position, o.Value);
            Assert.Equal(c.Transient, o.Transient);
            Assert.InRange(o.Value, -64.0, 63.0);
        }
    }

    [Fact]
    public void N337_type_32_timed_sequence_parses_with_its_time_tags()
    {
        var header = new Iec104StatusTypes.Header(32, 20, false, false, 0, 7, Sequence: true);
        var objs = new[]
        {
            new Iec104StepPosition(500, 12, false, 0x00, N337T),
            new Iec104StepPosition(501, -3, true, 0x40, N337T.AddMilliseconds(1)),
        };
        byte[] asdu = Iec104StatusTypes.EncodeStepPositions(header, objs);
        Iec104Asdu parsed = Iec104Asdu.Parse(asdu);
        Assert.Equal(new[]
        {
            new Iec104InfoObject(500, 12.0, 0x00, RtuPointType.StepPosition, N337T, Transient: false),
            new Iec104InfoObject(501, -3.0, 0x40, RtuPointType.StepPosition, N337T.AddMilliseconds(1), Transient: true),
        }, parsed.Objects);
        Assert.Equal(20, parsed.Cause);
        Assert.Equal(7, parsed.CommonAddress);
    }

    [Fact]
    public void N337_types_7_and_33_parse_the_32_bit_word_exactly()
    {
        // Untimed, SQ = 0: the full-scale word 0xFFFFFFFF must come back as 4294967295 exactly (a uint is exact in a
        // double), and the octet order is least significant first.
        byte[] asdu7 =
        {
            7, 0x02, 3, 0x00, 0x01, 0x00,
            0x0A, 0x00, 0x00, 0x04, 0x03, 0x02, 0x01, 0x00,   // IOA 10: 0x01020304, good
            0x0B, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x20,   // IOA 11: 0xFFFFFFFF, SB
        };
        Iec104Asdu p7 = Iec104Asdu.Parse(asdu7);
        Assert.Equal(new[]
        {
            new Iec104InfoObject(10, 16909060.0, 0x00, RtuPointType.Bitstring, null),
            new Iec104InfoObject(11, 4294967295.0, 0x20, RtuPointType.Bitstring, null),
        }, p7.Objects);
        var (_, c7) = Iec104StatusTypes.DecodeBitstrings(asdu7);
        Assert.Equal(c7.Select(o => (double)o.Bits), p7.Objects.Select(o => o.Value));

        // Timed type 33 built by the codec, read back by the pipeline decoder.
        var header = new Iec104StatusTypes.Header(33, 3, false, false, 0, 1, Sequence: false);
        byte[] asdu33 = Iec104StatusTypes.EncodeBitstrings(header, new[] { new Iec104Bitstring(42, 0x80000001u, 0x00, N337T) });
        Iec104InfoObject o33 = Assert.Single(Iec104Asdu.Parse(asdu33).Objects);
        Assert.Equal(new Iec104InfoObject(42, 2147483649.0, 0x00, RtuPointType.Bitstring, N337T), o33);
    }

    [Fact]
    public void N337_the_protocol_turns_a_type_5_asdu_into_a_point_and_counts_nothing_unsupported()
    {
        // The very bytes R089's counting test used: type 5, one object, IOA 10, step +5, good.
        byte[] asdu = { 5, 0x01, 3, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x05, 0x00 };
        var proto = new Iec104Protocol();
        RtuPoint p = Assert.Single(proto.Decode(N337Apdu(asdu), new QualityFold()));
        Assert.Equal("IOA:10", p.Address);
        Assert.Equal(RtuPointType.StepPosition, p.Type);
        Assert.Equal(5.0, p.Value);
        Assert.Equal(RtuQuality.Good, p.Quality);
        Assert.Null(p.StampUtc);
        Assert.Empty(proto.UnsupportedTypeIds);
    }

    [Fact]
    public void N337_the_protocol_marks_a_moving_tap_transient_and_a_test_asdu_invalid()
    {
        // VTI 0x85: step +5 with T set (the tap changer is moving).
        byte[] moving = { 5, 0x01, 3, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x85, 0x00 };
        RtuPoint m = Assert.Single(new Iec104Protocol().Decode(N337Apdu(moving), new QualityFold()));
        Assert.Equal(5.0, m.Value);
        Assert.Equal(RtuQuality.Transient, m.Quality);
        Assert.False(QualityFold.ShouldDrop(m.Quality));
        Assert.Equal(1.0, QualityFold.Default.SigmaMultiplier(m.Quality));

        // COT test bit set on a bitstring: never live telemetry.
        byte[] test = { 7, 0x01, 0x83, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 1, 0, 0, 0, 0x00 };
        RtuPoint t = Assert.Single(new Iec104Protocol().Decode(N337Apdu(test), new QualityFold()));
        Assert.Equal(RtuPointType.Bitstring, t.Type);
        Assert.Equal(1.0, t.Value);
        Assert.Equal(RtuQuality.Invalid, t.Quality);
    }

    [Fact]
    public void N337_the_protocol_still_counts_a_genuinely_unsupported_type()
    {
        // Type 100 (interrogation command) in monitor direction: no points, counted.
        byte[] asdu = { 100, 0x01, 6, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x14 };
        var proto = new Iec104Protocol();
        Assert.Empty(proto.Decode(N337Apdu(asdu), new QualityFold()));
        Assert.Equal(1, proto.UnsupportedTypeIds[100]);
        Assert.False(proto.UnsupportedTypeIds.ContainsKey(5));
    }

    private static readonly DateTime N337Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime N337Historical = N337Now.AddHours(-200);

    private static RtuPointMap N337Crosswalk(params RtuPointBinding[] extra) => new()
    {
        Case = "case14",
        BaseMva = 100.0,
        Bindings = new List<RtuPointBinding>
        {
            new() { Address = "IOA:1001", Kind = MeasurementKind.VmPu, Bus = 4, Sigma = 0.01 },
        }.Concat(extra).ToList(),
    };

    [Fact]
    public void N337_unbound_step_positions_and_bitstrings_stay_out_of_the_measurement_set()
    {
        var points = new List<RtuPoint>
        {
            new("IOA:1001", RtuPointType.Analog, 1.05, RtuQuality.Good, null),
            new("IOA:10", RtuPointType.StepPosition, 5.0, RtuQuality.Good, null),
            new("IOA:11", RtuPointType.Bitstring, 7.0, RtuQuality.Transient, null),
        };
        MeasurementSet set = RtuMeasurementSetMapper.Map(points, N337Crosswalk(), N337Historical, utcNow: N337Now);
        Measurement vm = Assert.Single(set.Measurements);
        Assert.Equal(MeasurementKind.VmPu, vm.Kind);
        Assert.Equal(1.05, vm.Value);
    }

    [Theory]
    [InlineData(RtuPointType.StepPosition, RtuQuality.Good, "step position")]
    [InlineData(RtuPointType.StepPosition, RtuQuality.Invalid, "step position")]   // refused whatever the quality
    [InlineData(RtuPointType.Bitstring, RtuQuality.Good, "32-bit bitstring")]
    public void N337_a_crosswalk_binding_a_status_point_as_a_measurement_is_refused(RtuPointType type, RtuQuality q, string what)
    {
        var points = new List<RtuPoint> { new("IOA:10", type, 5.0, q, null) };
        RtuPointMap map = N337Crosswalk(new RtuPointBinding { Address = "IOA:10", Kind = MeasurementKind.VmPu, Bus = 2, Sigma = 0.01 });
        var ex = Assert.Throws<InvalidDataException>(() => RtuMeasurementSetMapper.Map(points, map, N337Historical, utcNow: N337Now));
        Assert.Contains("IOA:10", ex.Message, StringComparison.Ordinal);
        Assert.Contains(what, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Remove the binding", ex.Message, StringComparison.Ordinal);
    }
}
