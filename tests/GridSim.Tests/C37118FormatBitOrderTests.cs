using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R046: the CONFIG FORMAT word follows IEEE C37.118.2-2011 Table 8 (bit 0 polar, bit 1 phasor float, bit 2
/// analog float, bit 3 freq float). These tests write the FORMAT word as a RAW NUMBER, never through the enum,
/// so the parser is checked against the standard rather than against itself. R294: the PDC never re-emits an
/// instant it has already flushed.
/// </summary>
public class C37118FormatBitOrderTests
{
    // A one-PMU CONFIG-2 frame with the given raw FORMAT word and phasor count (no analogs/digitals).
    private static byte[] Config2(ushort rawFormat, int phnmr)
    {
        int pmuBlock = 16 + 2 + 2 + 2 + 2 + 2 + 16 * phnmr + 4 * phnmr + 2 + 2;
        int size = 14 + 4 + 2 + pmuBlock + 2 + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = 0x31;                                                    // CONFIG-2 (type 3), version 1
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 7);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 1_000_000); off += 4;   // TIME_BASE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // NUM_PMU
        Encoding.ASCII.GetBytes("PMU_A           ").CopyTo(f.AsSpan(off)); off += 16;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 42); off += 2;          // IDCODE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), rawFormat); off += 2;   // FORMAT, raw
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)phnmr); off += 2;
        off += 2 + 2;                                                                // ANNMR = DGNMR = 0
        off += 16 * phnmr + 4 * phnmr;                                               // CHNAM + PHUNIT
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // FNOM 50 Hz
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;           // CFGCNT
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50);                     // DATA_RATE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));
        return f;
    }

    [Theory]
    [InlineData(0x0001, false, true, false, false)]   // polar, all integer
    [InlineData(0x0002, true, false, false, false)]   // float rectangular phasors
    [InlineData(0x0004, false, false, true, false)]   // float analogs only
    [InlineData(0x0008, false, false, false, true)]   // float FREQ/DFREQ only
    [InlineData(0x000E, true, false, true, true)]     // the finding's case: float rectangular + float freq/analog
    public void The_raw_format_word_decodes_by_the_standard_bit_positions(
        int raw, bool phasorFloat, bool polar, bool analogFloat, bool freqFloat)
    {
        PmuConfig pmu = C37118ConfigParser.Parse(Config2((ushort)raw, 1)).Pmus.Single();
        Assert.Equal(phasorFloat, pmu.Format.HasFlag(C37118DataFormat.PhasorFloat));
        Assert.Equal(polar, pmu.Format.HasFlag(C37118DataFormat.PhasorPolar));
        Assert.Equal(analogFloat, pmu.Format.HasFlag(C37118DataFormat.AnalogFloat));
        Assert.Equal(freqFloat, pmu.Format.HasFlag(C37118DataFormat.FreqFloat));
    }

    [Fact]
    public void A_float_rectangular_frame_decodes_correctly_with_the_parsed_format()
    {
        // FORMAT 0x000E: float rectangular phasors + float FREQ/DFREQ. Before R046 the parser called this
        // integer POLAR phasors and the decoder read 4-byte slots out of 8-byte floats (garbage, Status Ok).
        C37118DataFormat format = C37118ConfigParser.Parse(Config2(0x000E, 1)).Pmus.Single().Format;

        int size = 14 + 2 + 8 + 8 + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = 0x01;                                                     // DATA (type 0), version 1
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), 1_000);
        int off = 16;                                                    // after STAT (0)
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 230_000f); off += 4;   // real
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), -4_000f); off += 4;    // imag
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 49.98f); off += 4;     // FREQ
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), -0.05f);               // DFREQ
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));

        C37118DataFrame d = C37118Decoder.DecodeData(f, 1_000_000, 1, format);
        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
        Assert.Equal(230_000.0, d.Phasors[0].Real);
        Assert.Equal(-4_000.0, d.Phasors[0].Imag);
        Assert.Equal((double)49.98f, d.FreqHz);
        Assert.Equal((double)-0.05f, d.RocofHzPerS);
    }

    // ---- R294 ---------------------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static PmuSample S(int id, DateTime t) => new(id, t, new[] { new Phasor(id, 0) }, 50.0, 0.0);

    [Fact]
    public void A_straggler_for_a_flushed_instant_is_discarded_and_counted_not_re_emitted()
    {
        var pdc = new PhasorDataConcentrator(new[] { 1, 2, 3 }, TimeSpan.FromMilliseconds(40));
        foreach (int id in new[] { 1, 2, 3 }) pdc.Ingest(S(id, T0));
        Assert.Single(pdc.Flush(T0.AddMilliseconds(50)));

        // PMU 2's retransmission for T0 arrives at T0+60 ms. Before R294 the next flush emitted a second
        // superframe for T0 holding only PMU 2 and listing 1 and 3 as dropped.
        Assert.False(pdc.Ingest(S(2, T0)));
        Assert.Equal(1, pdc.LateSamples);
        Assert.Empty(pdc.Flush(T0.AddMilliseconds(200)));
    }

    [Fact]
    public void Instants_after_the_high_water_mark_still_flow_normally()
    {
        var pdc = new PhasorDataConcentrator(new[] { 1 }, TimeSpan.FromMilliseconds(40));
        Assert.True(pdc.Ingest(S(1, T0)));
        pdc.Flush(T0.AddMilliseconds(50));
        Assert.True(pdc.Ingest(S(1, T0.AddMilliseconds(20))));
        PdcSuperframe next = Assert.Single(pdc.FlushAll());
        Assert.Equal(T0.AddMilliseconds(20), next.TimeUtc);
        Assert.Equal(0, pdc.LateSamples);
    }
}
