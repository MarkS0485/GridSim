using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>The IEC 60870-5 ASDU object model: the monitor-direction types decode to information objects
/// with the right IOA, native value, quality octet, and CP56Time2a time tag - the application layer the
/// framing codec deferred.</summary>
public class Iec104AsduTests
{
    // ---- ASDU builders (DUI = type, vsq, cot, originator, casdu-lo, casdu-hi) ----

    private static byte[] DuiHeader(byte typeId, byte vsq, int cause, int commonAddr) =>
        new byte[] { typeId, vsq, (byte)cause, 0x00, (byte)(commonAddr & 0xFF), (byte)((commonAddr >> 8) & 0xFF) };

    private static void WriteIoa(List<byte> b, int ioa)
    {
        b.Add((byte)(ioa & 0xFF));
        b.Add((byte)((ioa >> 8) & 0xFF));
        b.Add((byte)((ioa >> 16) & 0xFF));
    }

    private static void WriteFloat(List<byte> b, float v)
    {
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(t, v);
        b.AddRange(t.ToArray());
    }

    [Fact]
    public void Short_float_objects_decode_with_ioa_value_and_quality()
    {
        var b = new List<byte>(DuiHeader(13, vsq: 0x02, cause: 3, commonAddr: 1)); // M_ME_NC_1, SQ=0, 2 objects
        WriteIoa(b, 1001); WriteFloat(b, 1.05f); b.Add(0x00);   // good quality
        WriteIoa(b, 1002); WriteFloat(b, -42.5f); b.Add(0x40);  // NT (not topical)

        Iec104Asdu asdu = Iec104Asdu.Parse(b.ToArray());

        Assert.Equal(13, asdu.TypeId);
        Assert.Equal(2, asdu.Objects.Count);
        Assert.Equal(1001, asdu.Objects[0].Ioa);
        Assert.Equal(1.05, asdu.Objects[0].Value, 5);
        Assert.Equal(RtuQuality.Good, QualityFold.FromIec104(asdu.Objects[0].Quality));
        Assert.Equal(1002, asdu.Objects[1].Ioa);
        Assert.Equal(-42.5, asdu.Objects[1].Value, 5);
        Assert.True(QualityFold.FromIec104(asdu.Objects[1].Quality).HasFlag(RtuQuality.NonTopical));
    }

    [Fact]
    public void Sequence_of_elements_shares_one_base_ioa()
    {
        // SQ=1: single base IOA (500), then 3 consecutive scaled values -> IOAs 500,501,502.
        var b = new List<byte>(DuiHeader(11, vsq: 0x83, cause: 3, commonAddr: 1)); // M_ME_NB_1, SQ=1, count 3
        WriteIoa(b, 500);
        foreach (short v in new short[] { 10, 20, 30 })
        {
            b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); b.Add(0x00); // scaled + QDS
        }

        Iec104Asdu asdu = Iec104Asdu.Parse(b.ToArray());

        Assert.Equal(3, asdu.Objects.Count);
        Assert.Equal(new[] { 500, 501, 502 }, new[] { asdu.Objects[0].Ioa, asdu.Objects[1].Ioa, asdu.Objects[2].Ioa });
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, new[] { asdu.Objects[0].Value, asdu.Objects[1].Value, asdu.Objects[2].Value });
    }

    [Fact]
    public void Timed_short_float_decodes_cp56time2a_to_utc()
    {
        var b = new List<byte>(DuiHeader(36, vsq: 0x01, cause: 3, commonAddr: 1)); // M_ME_TF_1, SQ=0, 1 object
        WriteIoa(b, 7); WriteFloat(b, 50.02f); b.Add(0x00);
        // CP56Time2a for 2026-07-20 13:45:12.500 UTC
        int ms = 12 * 1000 + 500;
        b.Add((byte)(ms & 0xFF)); b.Add((byte)((ms >> 8) & 0xFF)); // ms LE
        b.Add(45);   // minute (IV clear)
        b.Add(13);   // hour
        b.Add(20);   // day of month (dow bits 0)
        b.Add(7);    // month
        b.Add(26);   // year -> 2026

        Iec104InfoObject o = Iec104Asdu.Parse(b.ToArray()).Objects[0];

        Assert.Equal(50.02, o.Value, 4);
        Assert.Equal(new DateTime(2026, 7, 20, 13, 45, 12, 500, DateTimeKind.Utc), o.TimeUtc);
    }

    [Fact]
    public void Control_or_unsupported_type_yields_no_objects()
    {
        // Type 100 (C_IC_NA_1 interrogation command) is not a monitor measurement type here.
        Iec104Asdu asdu = Iec104Asdu.Parse(DuiHeader(100, vsq: 0x01, cause: 6, commonAddr: 1));
        Assert.Empty(asdu.Objects);
    }
}
