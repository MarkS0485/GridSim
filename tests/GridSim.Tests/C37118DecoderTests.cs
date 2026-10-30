using System;
using System.Buffers.Binary;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The IEEE C37.118.2 synchrophasor decoder: CRC-CCITT against the standard known-answer vector, and a
/// DATA-frame round-trip (build a float-format frame, decode it, recover timestamp/phasors/freq), plus
/// tamper detection (a flipped byte fails the CRC).
/// </summary>
public class C37118DecoderTests
{
    private const long TimeBase = 1_000_000;

    private static byte[] BuildDataFrame(uint soc, uint fracSec, ushort idCode, ushort stat,
        (float Re, float Im)[] phasors, float freq, float dfreq)
    {
        int size = 14 + 2 + phasors.Length * 8 + 8 + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = (byte)(((int)C37118FrameType.Data << 4) | 1); // Data frame, version 1
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), idCode);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), soc);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), fracSec);
        int off = 14;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), stat); off += 2;
        foreach ((float re, float im) in phasors)
        {
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), re);
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off + 4), im);
            off += 8;
        }
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), freq); off += 4;
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), dfreq); off += 4;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));
        return f;
    }

    [Fact]
    public void Crc_ccitt_matches_the_standard_check_vector()
    {
        // CRC-16/CCITT-FALSE of "123456789" is 0x29B1.
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal((ushort)0x29B1, C37118Crc.Compute(data));
    }

    [Fact]
    public void A_data_frame_round_trips_through_the_decoder()
    {
        const uint soc = 1_719_835_200;   // a UNIX time
        const uint fracSec = 500_000;     // 0.5 s at TIME_BASE = 1e6
        var phasors = new[] { (230.0f, 10.0f), (-5.0f, 100.0f) };
        byte[] frame = BuildDataFrame(soc, fracSec, idCode: 7, stat: 0, phasors, freq: 49.98f, dfreq: -0.12f);

        Assert.True(C37118Decoder.VerifyCrc(frame));
        C37118CommonHeader h = C37118Decoder.ReadHeader(frame);
        Assert.Equal(C37118FrameType.Data, h.FrameType);
        Assert.Equal(7, h.IdCode);
        Assert.Equal(frame.Length, h.FrameSize);

        C37118DataFrame decoded = C37118Decoder.DecodeData(frame, TimeBase, phasors.Length,
            C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat);

        Assert.Equal(FrameDecodeStatus.Ok, decoded.Status);
        Assert.Equal(2, decoded.Phasors.Length);
        Assert.Equal(230.0, decoded.Phasors[0].Real, 3);
        Assert.Equal(10.0, decoded.Phasors[0].Imag, 3);
        Assert.Equal(-5.0, decoded.Phasors[1].Real, 3);
        Assert.Equal(100.0, decoded.Phasors[1].Imag, 3);
        Assert.Equal(49.98, decoded.FreqHz, 3);
        Assert.Equal(-0.12, decoded.RocofHzPerS, 3);

        DateTime expected = DateTime.UnixEpoch.AddSeconds(soc).AddSeconds(0.5);
        Assert.True(Math.Abs((decoded.TimeUtc - expected).TotalSeconds) < 1e-6);
    }

    [Fact]
    public void A_flipped_byte_fails_the_crc()
    {
        byte[] frame = BuildDataFrame(1_719_835_200, 0, idCode: 1, stat: 0,
            new[] { (1.0f, 2.0f) }, freq: 50.0f, dfreq: 0.0f);
        frame[16] ^= 0xFF; // corrupt a phasor byte

        Assert.False(C37118Decoder.VerifyCrc(frame));
        C37118DataFrame decoded = C37118Decoder.DecodeData(frame, TimeBase, 1,
            C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat);
        Assert.Equal(FrameDecodeStatus.CrcMismatch, decoded.Status);
    }

    // Recompute the trailing CHK after a deliberate edit, so only the edit (not the CRC) can refuse the frame.
    private static void Reseal(byte[] f)
        => BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));

    [Fact]
    public void A_crc_valid_frame_with_a_wrong_sync_byte_is_malformed_not_an_exception()
    {
        // GR-781: the header parse threw ArgumentException on it, which the "reported, never thrown" contract forbids.
        byte[] frame = BuildDataFrame(1_719_835_200, 0, idCode: 1, stat: 0,
            new[] { (1.0f, 2.0f) }, freq: 50.0f, dfreq: 0.0f);
        frame[0] = 0xAB;
        Reseal(frame);
        Assert.True(C37118Decoder.VerifyCrc(frame));
        C37118DataFrame decoded = C37118Decoder.DecodeData(frame, TimeBase, 1,
            C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat);
        Assert.Equal(FrameDecodeStatus.Malformed, decoded.Status);
    }

    [Fact]
    public void A_frame_longer_than_the_configuration_is_malformed_on_the_single_pmu_overload()
    {
        // GR-831: a two-phasor frame decoded as one phasor used to read the first phasor as Ok and drop the second.
        byte[] frame = BuildDataFrame(1_719_835_200, 0, idCode: 1, stat: 0,
            new[] { (1.0f, 2.0f), (3.0f, 4.0f) }, freq: 50.0f, dfreq: 0.0f);
        Assert.True(C37118Decoder.VerifyCrc(frame));
        C37118DataFrame decoded = C37118Decoder.DecodeData(frame, TimeBase, 1,
            C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat);
        Assert.Equal(FrameDecodeStatus.Malformed, decoded.Status);
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(frame, TimeBase, 2,
            C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat).Status);
    }
}
