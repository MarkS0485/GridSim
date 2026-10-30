using System;
using System.Buffers.Binary;
using System.Text;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N163: <see cref="C37118ConfigParser.Parse"/> checks the CRC-CCITT and bounds-checks every field against the declared
/// frame, refusing a bad frame with a <see cref="C37118FrameException"/> that names what is wrong. Before N163 a wrong
/// CRC was parsed as if good and a truncated frame threw a raw ArgumentOutOfRangeException from span slicing. A good
/// frame parses exactly as before.
/// </summary>
public class N163C37118ConfigParserRefusalTests
{
    // Build a CONFIG-2 frame with one PMU of 'phnmr' phasors (no analogue, no digital channels), declared PHNMR
    // optionally different from the real table size, NUM_PMU optionally different from the one block written, and a
    // correct CRC unless told otherwise. Layout: IEEE C37.118.2-2011 Table 9 (same as C37118ConfigParserTests).
    private static byte[] Config(int phnmr = 2, int? declaredPhnmr = null, int declaredNumPmu = 1,
        bool withDataRate = true, bool goodCrc = true)
    {
        int chnam = 16 * phnmr;
        int pmuBlock = 16 + 2 + 2 + 2 + 2 + 2 + chnam + 4 * phnmr + 2 + 2;
        int size = 14 + 4 + 2 + pmuBlock + (withDataRate ? 2 : 0) + 2;
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = (byte)(((int)C37118FrameType.Config2 << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), 1_000);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 1_000_000); off += 4;          // TIME_BASE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)declaredNumPmu); off += 2;
        Encoding.ASCII.GetBytes("STATION_A       ").CopyTo(f.AsSpan(off)); off += 16;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 42); off += 2;                 // IDCODE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off),
            (ushort)(C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat)); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)(declaredPhnmr ?? phnmr)); off += 2;
        off += 2 + 2;                                                                       // ANNMR, DGNMR = 0
        off += chnam;
        for (int i = 0; i < phnmr; i++) { BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 100_000u + (uint)i); off += 4; }
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;                  // FNOM 50 Hz
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 3); off += 2;                  // CFGCNT
        if (withDataRate) { BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50); off += 2; }
        ushort crc = C37118Crc.Compute(f.AsSpan(0, size - 2));
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), goodCrc ? crc : (ushort)(crc ^ 0x0001));
        return f;
    }

    [Fact]
    public void A_good_frame_parses_every_field_as_before()
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config());
        Assert.Equal(1_000_000, cfg.TimeBase);
        Assert.Equal(50, cfg.DataRate);
        PmuConfig pmu = Assert.Single(cfg.Pmus);
        Assert.Equal("STATION_A", pmu.StationName);
        Assert.Equal(42, pmu.IdCode);
        Assert.Equal(2, pmu.PhasorCount);
        Assert.Equal(0, pmu.AnalogCount);
        Assert.Equal(0, pmu.DigitalCount);
        Assert.True(pmu.Is50Hz);
        Assert.Equal(3, pmu.ConfigCount);
        Assert.NotNull(pmu.PhasorUnits);
        Assert.Equal(2, pmu.PhasorUnits!.Count);
        Assert.Equal(100_000u, pmu.PhasorUnits[0]);
        Assert.Equal(100_001u, pmu.PhasorUnits[1]);
    }

    [Fact]
    public void Bytes_after_the_declared_frame_are_ignored_as_before()
    {
        // A span holding the frame plus the start of the next one (a capture buffer): the declared FRAMESIZE bounds it.
        byte[] f = Config();
        var longer = new byte[f.Length + 10];
        f.CopyTo(longer, 0);
        longer[f.Length] = 0xAA;
        Assert.Equal(50, C37118ConfigParser.Parse(longer).DataRate);
    }

    [Fact]
    public void A_wrong_crc_is_refused_naming_both_check_words()
    {
        byte[] good = Config();
        byte[] bad = Config(goodCrc: false);
        ushort chkGood = BinaryPrimitives.ReadUInt16BigEndian(good.AsSpan(good.Length - 2));
        ushort chkBad = BinaryPrimitives.ReadUInt16BigEndian(bad.AsSpan(bad.Length - 2));
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(bad));
        Assert.Contains("failed its CRC check", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"CHK is 0x{chkBad:X4}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"CRC-CCITT of the frame is 0x{chkGood:X4}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupted_body_byte_is_refused_by_the_crc()
    {
        byte[] f = Config();
        f[30] ^= 0xFF;   // inside the station name: the parse would succeed with a wrong name before N163
        Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(f));
    }

    [Fact]
    public void A_frame_cut_short_of_its_declared_size_is_refused_as_truncated()
    {
        byte[] f = Config();
        byte[] cut = f.AsSpan(0, f.Length - 5).ToArray();
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(cut));
        Assert.Contains($"declares FRAMESIZE {f.Length} bytes but only {f.Length - 5} were supplied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_size_below_the_smallest_config_frame_is_refused()
    {
        byte[] f = Config();
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), 20);
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(f));
        Assert.Contains("declares FRAMESIZE 20 bytes; a CONFIG frame is at least 24", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phasor_count_that_runs_past_the_frame_is_refused_by_name_not_by_a_raw_range_exception()
    {
        // The CRC is good (the sender really sent PHNMR = 60000), so only the bounds check can catch it. Before N163
        // this threw ArgumentOutOfRangeException from span slicing; Assert.Throws demands the exact named type.
        byte[] f = Config(phnmr: 2, declaredPhnmr: 60_000);
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(f));
        Assert.Contains("PMU block 1 of 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("60000 phasor(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pmu_count_larger_than_the_blocks_present_is_refused_naming_the_missing_block()
    {
        byte[] f = Config(declaredNumPmu: 2);
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(f));
        Assert.Contains("PMU block 2 of 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fixed fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_with_no_room_for_data_rate_is_refused()
    {
        byte[] f = Config(withDataRate: false);
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(f));
        Assert.Contains("DATA_RATE (2 bytes) is due", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_named_error_is_still_an_argument_exception_for_existing_callers()
    {
        // pmu-ingest and other callers catch ArgumentException; the new type must stay inside that net.
        Assert.IsAssignableFrom<ArgumentException>(new C37118FrameException("x"));
    }
}
