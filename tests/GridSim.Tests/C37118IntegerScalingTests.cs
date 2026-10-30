using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R293 (Q3, 2026-10): IEEE C37.118.2-2011 integer DATA fields are scaled quantities. Table 5: FREQ is the
/// deviation from FNOM in millihertz, DFREQ is ROCOF x 100, an integer phasor is a count of PHUNIT x 1e-5 V or A
/// (polar angle in 1e-4 rad). Table 9: FNOM and PHUNIT come from the CONFIG frame. These tests write the CONFIG and
/// DATA bytes by hand (never through the decoder's own types), so the decoder is checked against the standard.
/// </summary>
public class C37118IntegerScalingTests
{
    // A one-PMU CONFIG-2 frame with the given FORMAT, PHUNIT words, analogue count and FNOM word.
    private static byte[] Config2(ushort format, uint[] phunit, int annmr, ushort fnom)
    {
        int phnmr = phunit.Length;
        int pmuBlock = 16 + 2 + 2 + 2 + 2 + 2 + 16 * (phnmr + annmr) + 4 * phnmr + 4 * annmr + 2 + 2;
        int size = 14 + 4 + 2 + pmuBlock + 2 + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = 0x31;                                                    // CONFIG-2 (type 3), version 1
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 9);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 1_000_000); off += 4;   // TIME_BASE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // NUM_PMU
        Encoding.ASCII.GetBytes("PMU_INT         ").CopyTo(f.AsSpan(off)); off += 16;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 9); off += 2;           // IDCODE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), format); off += 2;      // FORMAT, raw
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)phnmr); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)annmr); off += 2;
        off += 2;                                                                    // DGNMR = 0
        off += 16 * (phnmr + annmr);                                                 // CHNAM
        foreach (uint u in phunit) { BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), u); off += 4; }   // PHUNIT
        off += 4 * annmr;                                                            // ANUNIT
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), fnom); off += 2;        // FNOM
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // CFGCNT
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50);                     // DATA_RATE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));
        return f;
    }

    // A one-PMU DATA frame: STAT 0, then the given body bytes (phasors, FREQ, DFREQ, ...), then the CRC.
    private static byte[] Data(byte[] body)
    {
        int size = 14 + 2 + body.Length + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = 0x01;                                                    // DATA (type 0), version 1
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 9);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), 1_000);
        body.CopyTo(f, 16);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));
        return f;
    }

    private static byte[] Int16s(params int[] values)
    {
        var b = new byte[2 * values.Length];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2 * i), unchecked((ushort)values[i]));
        return b;
    }

    [Fact]
    public void The_config_parser_keeps_phunit_and_fnom()
    {
        // Voltage channel at 10 V per count, current channel (top byte 1) at 0.5 A per count; FNOM bit 0 clear = 60 Hz.
        PmuConfig pmu = C37118ConfigParser.Parse(Config2(0x0000, new[] { 1_000_000u, 0x0100_0000u | 50_000u }, 0, 0)).Pmus.Single();
        Assert.Equal(new[] { 1_000_000u, 0x0100_C350u }, pmu.PhasorUnits!.ToArray());
        Assert.Equal(10.0, pmu.PhasorScale(0));
        Assert.Equal(0.5, pmu.PhasorScale(1));
        Assert.False(pmu.PhasorIsCurrent(0));
        Assert.True(pmu.PhasorIsCurrent(1));
        Assert.Equal(60.0, pmu.NominalHz);
        Assert.Equal(50.0, C37118ConfigParser.Parse(Config2(0, new[] { 1u }, 0, 1)).Pmus.Single().NominalHz);
    }

    [Theory]
    [InlineData(0x0000)]   // integer phasors and integer FREQ
    [InlineData(0x0002)]   // float phasors, integer FREQ
    [InlineData(0x0008)]   // integer phasors, float FREQ
    [InlineData(0x0009)]   // integer polar phasors, float FREQ
    public void The_format_only_overload_refuses_any_integer_field(int format)
    {
        byte[] frame = Data(new byte[16]);
        var ex = Assert.Throws<ArgumentException>(
            () => C37118Decoder.DecodeData(frame, 1_000_000, 1, (C37118DataFormat)format));
        Assert.Contains("PmuConfig", ex.Message);
    }

    [Fact]
    public void Integer_rectangular_phasors_and_frequency_are_scaled_from_the_parsed_config()
    {
        // 50 Hz nominal; voltage PHUNIT 1,000,000 (10 V per count); current PHUNIT 0x01 | 2,000 (0.02 A per count).
        PmuConfig pmu = C37118ConfigParser.Parse(Config2(0x0000, new[] { 1_000_000u, 0x0100_0000u | 2_000u }, 0, 1)).Pmus.Single();
        byte[] frame = Data(Int16s(23_094, -1_200, 30_000, -5_000, -37, 12));   // V re/im, I re/im, FREQ, DFREQ

        C37118DataFrame d = C37118Decoder.DecodeData(frame, 1_000_000, pmu);

        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
        Assert.Equal(230_940.0, d.Phasors[0].Real);              // 23094 x 10 V, exact
        Assert.Equal(-12_000.0, d.Phasors[0].Imag);              // -1200 x 10 V, exact
        Assert.Equal(30_000 * (2_000 / 100_000.0), d.Phasors[1].Real);   // count x PHUNIT / 1e5, the decoder's arithmetic
        Assert.Equal(600.0, d.Phasors[1].Real, 9);               // 30000 x 0.02 A
        Assert.Equal(-100.0, d.Phasors[1].Imag, 9);
        Assert.Equal(50.0 + -37 / 1000.0, d.FreqHz);             // FNOM + mHz deviation, the decoder's arithmetic
        Assert.Equal(49.963, d.FreqHz, 12);
        Assert.Equal(0.12, d.RocofHzPerS, 12);                   // 12 / 100 Hz/s
    }

    [Fact]
    public void Integer_polar_phasors_scale_the_unsigned_magnitude_and_keep_the_angle_in_1e4_rad_at_60_hz()
    {
        // 60 Hz nominal (FNOM 0); PHUNIT 100,000 = 1 V per count. Magnitude 50,000 counts (above int16 range, read
        // unsigned), angle 7854 x 1e-4 rad. FREQ +25 mHz, DFREQ -150 = -1.5 Hz/s.
        PmuConfig pmu = C37118ConfigParser.Parse(Config2(0x0001, new[] { 100_000u }, 0, 0)).Pmus.Single();
        byte[] frame = Data(Int16s(50_000, 7_854, 25, -150));

        C37118DataFrame d = C37118Decoder.DecodeData(frame, 1_000_000, pmu);

        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
        Assert.Equal(50_000.0, d.Phasors[0].Magnitude, 6);
        Assert.Equal(0.7854, d.Phasors[0].AngleRad, 12);
        Assert.Equal(60.025, d.FreqHz, 12);
        Assert.Equal(-1.5, d.RocofHzPerS, 12);
    }

    [Fact]
    public void Float_data_decodes_the_same_through_either_overload()
    {
        // Float rectangular phasors + float FREQ: no scaling, so the config overload must agree bit for bit with
        // the format-only overload (and take FREQ as the actual frequency, Table 5).
        PmuConfig pmu = C37118ConfigParser.Parse(Config2(0x000A, new[] { 0u }, 0, 1)).Pmus.Single();
        var body = new byte[16];
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(0), 230_000f);
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(4), -4_000f);
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(8), 49.98f);
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(12), -0.05f);
        byte[] frame = Data(body);

        C37118DataFrame a = C37118Decoder.DecodeData(frame, 1_000_000, pmu);
        C37118DataFrame b = C37118Decoder.DecodeData(frame, 1_000_000, 1, pmu.Format);
        Assert.Equal(FrameDecodeStatus.Ok, a.Status);
        Assert.Equal(b.Phasors[0].Real, a.Phasors[0].Real);
        Assert.Equal(b.Phasors[0].Imag, a.Phasors[0].Imag);
        Assert.Equal((double)49.98f, a.FreqHz);
        Assert.Equal(b.RocofHzPerS, a.RocofHzPerS);
    }

    [Fact]
    public void An_integer_phasor_config_without_phunit_is_refused()
    {
        var bare = new PmuConfig("HAND", 9, C37118DataFormat.None, 1, 0, 0, Is50Hz: true, ConfigCount: 1);
        byte[] frame = Data(Int16s(1, 2, 3, 4));
        var ex = Assert.Throws<ArgumentException>(() => C37118Decoder.DecodeData(frame, 1_000_000, bare));
        Assert.Contains("PHUNIT", ex.Message);
        Assert.Throws<InvalidOperationException>(() => bare.PhasorScale(0));
    }

    [Fact]
    public void The_frame_length_check_counts_the_analogue_channels()
    {
        // One integer analogue declared in the CONFIG: a DATA frame that stops after DFREQ is too short.
        PmuConfig pmu = C37118ConfigParser.Parse(Config2(0x0000, new[] { 100_000u }, 1, 1)).Pmus.Single();
        Assert.Equal(1, pmu.AnalogCount);
        Assert.Equal(FrameDecodeStatus.Malformed, C37118Decoder.DecodeData(Data(Int16s(1, 2, 3, 4)), 1_000_000, pmu).Status);
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(Data(Int16s(1, 2, 3, 4, 99)), 1_000_000, pmu).Status);
    }
}
