using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R089 (October 2026 review): type 31 (M_DP_TB_1, double point with CP56Time2a, the usual timed breaker status) fell
/// through as unsupported and was silently discarded, and no pipeline counted unsupported ASDUs.
/// </summary>
public class Iec104DoublePointTimedTests
{
    private static byte[] Asdu31(byte diq, int ioa)
    {
        var b = new List<byte> { 31, 0x01, 3, 0x00, 0x01, 0x00 };   // M_DP_TB_1, SQ=0, 1 object, COT 3, CASDU 1
        b.Add((byte)(ioa & 0xFF)); b.Add((byte)((ioa >> 8) & 0xFF)); b.Add((byte)((ioa >> 16) & 0xFF));
        b.Add(diq);
        int ms = 12 * 1000 + 500;                                   // 2026-07-20 13:45:12.500 UTC
        b.Add((byte)(ms & 0xFF)); b.Add((byte)((ms >> 8) & 0xFF));
        b.Add(45); b.Add(13); b.Add(20); b.Add(7); b.Add(26);
        return b.ToArray();
    }

    [Fact]
    public void Timed_double_point_on_decodes_with_its_time_tag()
    {
        Iec104Asdu asdu = Iec104Asdu.Parse(Asdu31(diq: 0x02, ioa: 4001));   // DPI 2 = ON
        Iec104InfoObject o = Assert.Single(asdu.Objects);
        Assert.Equal(4001, o.Ioa);
        Assert.Equal(1.0, o.Value);
        Assert.Equal(RtuPointType.BinaryStatus, o.Type);
        Assert.Equal(new DateTime(2026, 7, 20, 13, 45, 12, 500, DateTimeKind.Utc), o.TimeUtc);
    }

    [Fact]
    public void Timed_double_point_off_and_indeterminate_follow_the_untimed_rules()
    {
        Assert.Equal(0.0, Iec104Asdu.Parse(Asdu31(0x01, 1)).Objects[0].Value);                 // DPI 1 = OFF
        Assert.True(double.IsNaN(Iec104Asdu.Parse(Asdu31(0x03, 1)).Objects[0].Value));         // indeterminate
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(30, true)]
    [InlineData(3, true)]
    // Moved 2026-10 (END-PASS CF2 row 2, end pass; IEC 60870-5-101 / 104 type ids 5 M_ST_NA_1, 32 M_ST_TB_1, 7 M_BO_NA_1
    // decoded by N337, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): N337 decodes step position and
    // bitstring in the pipeline decoder, so these three types are now reported supported. Before -> after: rows 5, 32,
    // 7: supported false -> true (observed: Expected False, Actual True, 1.95.273-275 runs). Row 100 still pins an
    // unsupported type.
    [InlineData(5, true)]      // step position
    [InlineData(32, true)]
    [InlineData(7, true)]      // bitstring
    [InlineData(100, false)]   // interrogation command
    public void Supported_type_ids_are_reported(byte typeId, bool supported)
    {
        Assert.Equal(supported, Iec104Asdu.IsSupportedType(typeId));
    }

    [Fact]
    public void The_protocol_counts_unsupported_asdus_by_type_id()
    {
        // Moved 2026-10 (END-PASS CF2 row 3, end pass; IEC 60870-5-101 / 104 type 5 M_ST_NA_1 decoded by N337, Ruling R1
        // and Mark's go on the end-pass moves 2026-10-11): type 5 is now decoded, so it can no longer carry this test's
        // purpose (counting unsupported ASDUs). The counting behaviour still exists for every type the decoder does not
        // support, so the test is pointed at one: type 21 (M_ME_ND_1, normalised value without quality, a monitor type
        // this decoder does not support), whose one element is exactly the two octets the old frame already carried.
        // Before -> after: frame type octet 5 -> 21, UnsupportedTypeIds[5] -> UnsupportedTypeIds[21] (counts 1 and 2
        // unchanged). The type 5 frame is kept below and now pins the decoded point (observed in the 1.95.273-275 runs:
        // "RtuPoint { Address = IOA:10, Type = StepPosition, Value = 5, Quality = Good, StampUtc = , FrameSeq = 0 }");
        // that it is no longer counted unsupported is derived from Iec104Protocol.Decode (the run stopped at the first
        // assertion).
        // An I-format APDU carrying a type 21 (normalised value without quality) ASDU: no points, but counted.
        byte[] asdu = { 21, 0x01, 3, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x05, 0x00 };
        var apdu = new List<byte> { 0x68, (byte)(4 + asdu.Length), 0x00, 0x00, 0x00, 0x00 };
        apdu.AddRange(asdu);
        var proto = new Iec104Protocol();
        Assert.Empty(proto.Decode(apdu.ToArray(), new QualityFold()));
        Assert.Equal(1, proto.UnsupportedTypeIds[21]);
        proto.Decode(apdu.ToArray(), new QualityFold());
        Assert.Equal(2, proto.UnsupportedTypeIds[21]);

        // The old type 5 (step position) frame: now one point, and not counted unsupported.
        byte[] asdu5 = { 5, 0x01, 3, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x05, 0x00 };
        var apdu5 = new List<byte> { 0x68, (byte)(4 + asdu5.Length), 0x00, 0x00, 0x00, 0x00 };
        apdu5.AddRange(asdu5);
        var proto5 = new Iec104Protocol();
        RtuPoint p5 = Assert.Single(proto5.Decode(apdu5.ToArray(), new QualityFold()));
        Assert.Equal("IOA:10", p5.Address);
        Assert.Equal(RtuPointType.StepPosition, p5.Type);
        Assert.Equal(5.0, p5.Value);
        Assert.Equal(RtuQuality.Good, p5.Quality);
        Assert.Null(p5.StampUtc);
        Assert.False(proto5.UnsupportedTypeIds.ContainsKey(5));
    }
}
