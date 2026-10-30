using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N061 (October 2026): multi-PMU C37.118 DATA frames (one block per PMU, each with its own STAT, in CONFIG order) and
/// the pmu-ingest verb that reads a recorded capture. Frames are built here byte by byte with the layout the decoder
/// documents (common header, then per PMU: STAT, phasors, FREQ, DFREQ, analogues, digitals; CRC-CCITT at the end).
/// </summary>
public class N061PmuIngestTests
{
    private sealed record PmuSpec(string Station, int IdCode, C37118DataFormat Format, int Phasors, int Analogs, int Digitals, uint[] PhUnits);

    private static byte[] Finish(byte[] f)
    {
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    private static void Header(byte[] f, C37118FrameType type, int idCode, uint soc, uint fracsec)
    {
        f[0] = 0xAA;
        f[1] = (byte)(((int)type << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)f.Length);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)idCode);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), soc);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), fracsec);
    }

    private static byte[] Config(int timeBase, params PmuSpec[] pmus)
    {
        int size = 14 + 4 + 2 + 2 + 2;   // header, TIME_BASE, NUM_PMU, DATA_RATE, CHK
        foreach (PmuSpec p in pmus)
            size += 16 + 2 + 2 + 2 + 2 + 2 + 16 * (p.Phasors + p.Analogs + 16 * p.Digitals) + 4 * p.Phasors + 4 * p.Analogs + 4 * p.Digitals + 2 + 2;
        var f = new byte[size];
        Header(f, C37118FrameType.Config2, 7, 1_700_000_000, 0);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), (uint)timeBase); off += 4;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)pmus.Length); off += 2;
        foreach (PmuSpec p in pmus)
        {
            Encoding.ASCII.GetBytes(p.Station.PadRight(16)).CopyTo(f.AsSpan(off)); off += 16;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.IdCode); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.Format); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.Phasors); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.Analogs); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.Digitals); off += 2;
            off += 16 * (p.Phasors + p.Analogs + 16 * p.Digitals);
            foreach (uint u in p.PhUnits) { BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), u); off += 4; }
            off += 4 * p.Analogs + 4 * p.Digitals;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;   // FNOM: 50 Hz
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;   // CFGCNT
        }
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50);
        return Finish(f);
    }

    // One PMU block's values: STAT, phasors (rectangular pairs, or polar), FREQ, DFREQ (raw field values).
    private sealed record Block(ushort Stat, double[] PhasorPairs, double Freq, double Dfreq);

    private static byte[] Data(uint soc, uint fracsec, PmuSpec[] pmus, Block[] blocks)
    {
        int size = 14 + 2;
        foreach (PmuSpec p in pmus)
        {
            bool pf = (p.Format & C37118DataFormat.PhasorFloat) != 0, ff = (p.Format & C37118DataFormat.FreqFloat) != 0, af = (p.Format & C37118DataFormat.AnalogFloat) != 0;
            size += 2 + p.Phasors * (pf ? 8 : 4) + (ff ? 8 : 4) + p.Analogs * (af ? 4 : 2) + 2 * p.Digitals;
        }
        var f = new byte[size];
        Header(f, C37118FrameType.Data, 7, soc, fracsec);
        int off = 14;
        for (int k = 0; k < pmus.Length; k++)
        {
            PmuSpec p = pmus[k];
            Block b = blocks[k];
            bool pf = (p.Format & C37118DataFormat.PhasorFloat) != 0, ff = (p.Format & C37118DataFormat.FreqFloat) != 0, af = (p.Format & C37118DataFormat.AnalogFloat) != 0;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), b.Stat); off += 2;
            for (int i = 0; i < p.Phasors; i++)
            {
                if (pf)
                {
                    BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), (float)b.PhasorPairs[2 * i]);
                    BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off + 4), (float)b.PhasorPairs[2 * i + 1]);
                    off += 8;
                }
                else
                {
                    BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), (short)b.PhasorPairs[2 * i]);
                    BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off + 2), (short)b.PhasorPairs[2 * i + 1]);
                    off += 4;
                }
            }
            if (ff)
            {
                BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), (float)b.Freq);
                BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off + 4), (float)b.Dfreq);
                off += 8;
            }
            else
            {
                BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), (short)b.Freq);
                BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off + 2), (short)b.Dfreq);
                off += 4;
            }
            off += p.Analogs * (af ? 4 : 2) + 2 * p.Digitals;   // analogues and digitals left at zero
        }
        return Finish(f);
    }

    private static readonly C37118DataFormat FloatAll = C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat;

    private static readonly PmuSpec A = new("STN_A", 101, FloatAll, 2, 0, 0, new uint[] { 0, 0x0100_0000 });
    private static readonly PmuSpec B = new("STN_B", 102, C37118DataFormat.None, 1, 1, 1, new uint[] { 1_000_000 });   // integer, 10 V per count

    private static readonly Block BlockA = new(0, new[] { 230_000.0, -1_000.0, 120.5, 30.25 }, 50.01, -0.02);
    private static readonly Block BlockB = new(0x8000, new[] { 2300.0, -100.0 }, 15, -4);   // FREQ +15 mHz, DFREQ -0.04 Hz/s

    [Fact]
    public void A_two_pmu_frame_decodes_both_blocks_in_config_order()
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config(1_000_000, A, B));
        byte[] data = Data(1_700_000_001, 500_000, new[] { A, B }, new[] { BlockA, BlockB });
        C37118StreamDataFrame d = C37118Decoder.DecodeData(data, cfg);
        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
        Assert.Equal(2, d.Blocks.Count);
        Assert.Equal(101, d.Blocks[0].IdCode);
        Assert.Equal((float)230_000.0, d.Blocks[0].Phasors[0].Real);
        Assert.Equal((float)30.25, d.Blocks[0].Phasors[1].Imag);
        Assert.Equal((float)50.01, d.Blocks[0].FreqHz);
        Assert.Equal(102, d.Blocks[1].IdCode);
        Assert.Equal(0x8000, d.Blocks[1].Stat);
        Assert.Equal(2300.0 * 10.0, d.Blocks[1].Phasors[0].Real);   // 1,000,000 x 1e-5 = 10 V per count
        Assert.Equal(50.0 + 15 / 1000.0, d.Blocks[1].FreqHz);
        Assert.Equal(-4 / 100.0, d.Blocks[1].RocofHzPerS);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_700_000_001).AddSeconds(0.5), d.TimeUtc);
    }

    [Fact]
    public void A_one_pmu_stream_decodes_bit_for_bit_as_the_single_pmu_overload()
    {
        foreach (PmuSpec p in new[] { A, B })
        {
            C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config(1_000_000, p));
            byte[] data = Data(1_700_000_001, 123_456, new[] { p }, new[] { p == A ? BlockA : BlockB });
            C37118DataFrame single = C37118Decoder.DecodeData(data, cfg.TimeBase, cfg.Pmus[0]);
            C37118StreamDataFrame stream = C37118Decoder.DecodeData(data, cfg);
            Assert.Equal(FrameDecodeStatus.Ok, single.Status);
            Assert.Equal(single.TimeUtc, stream.TimeUtc);
            Assert.Equal(single.Stat, stream.Blocks[0].Stat);
            Assert.Equal(BitConverter.DoubleToInt64Bits(single.FreqHz), BitConverter.DoubleToInt64Bits(stream.Blocks[0].FreqHz));
            Assert.Equal(BitConverter.DoubleToInt64Bits(single.RocofHzPerS), BitConverter.DoubleToInt64Bits(stream.Blocks[0].RocofHzPerS));
            for (int i = 0; i < single.Phasors.Length; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(single.Phasors[i].Real), BitConverter.DoubleToInt64Bits(stream.Blocks[0].Phasors[i].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(single.Phasors[i].Imag), BitConverter.DoubleToInt64Bits(stream.Blocks[0].Phasors[i].Imag));
            }
        }
    }

    [Fact]
    public void A_frame_whose_length_does_not_match_the_configuration_is_malformed_and_a_bad_crc_is_reported()
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config(1_000_000, A, B));
        byte[] onlyA = Data(1_700_000_001, 0, new[] { A }, new[] { BlockA });   // one block where the config has two
        Assert.Equal(FrameDecodeStatus.Malformed, C37118Decoder.DecodeData(onlyA, cfg).Status);
        byte[] good = Data(1_700_000_001, 0, new[] { A, B }, new[] { BlockA, BlockB });
        good[20] ^= 0xFF;   // corrupt a byte, CRC no longer matches
        Assert.Equal(FrameDecodeStatus.CrcMismatch, C37118Decoder.DecodeData(good, cfg).Status);
    }

    [Fact]
    public void Integer_phasors_without_a_phunit_table_are_refused()
    {
        var cfg = new C37118ConfigFrame(1_000_000, 50, new[] { new PmuConfig("X", 1, C37118DataFormat.None, 1, 0, 0, true, 1) });
        byte[] data = Data(1, 0, new[] { B with { Analogs = 0, Digitals = 0 } }, new[] { BlockB });
        Assert.Throws<ArgumentException>(() => C37118Decoder.DecodeData(data, cfg));
    }

    private static string Capture(params byte[][] frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-pmu-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, frames.SelectMany(f => f).ToArray());
        return path;
    }

    [Fact]
    public void Pmu_ingest_decodes_a_clean_capture_and_exits_0()
    {
        // Moved 2026-10 (D-DOC-108, docs-session finding, Ruling R1: Mark delegated 2026-10-04): this "clean" capture gave
        // PMU B the STAT word 0x8000 in every frame, which is data error 10 (PMU in test mode / do not use) under IEEE
        // C37.118.2. Passing such samples on as clean data with exit 0 is the defect D-DOC-108 reports; pmu-ingest now
        // drops them and exits 3 (proved in Doc3CliHorizonProcessTests). So the clean capture's PMU B carries STAT 0;
        // every assertion below is unchanged. Before: BlockB (Stat 0x8000) in all three DATA frames. After: cleanB
        // (BlockB with Stat 0). BlockB itself is untouched: the decode tests above still read its 0x8000.
        Block cleanB = BlockB with { Stat = 0 };
        string path = Capture(Config(1_000_000, A, B),
            Data(1_700_000_001, 0, new[] { A, B }, new[] { BlockA, cleanB }),
            Data(1_700_000_001, 20_000, new[] { A, B }, new[] { BlockA, cleanB }),
            Data(1_700_000_001, 40_000, new[] { A, B }, new[] { BlockA, cleanB }));
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", path);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("4 frame(s): 1 CONFIG (0 bad CRC), 3 DATA (3 decoded, 0 bad CRC, 0 malformed, 0 before any CONFIG), 0 other", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("| STN_A | 101 | 2 | 3 |", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("| STN_B | 102 | 1 | 3 |", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("aligned instants: 3 (0 with a PMU missing)", r.StdOut, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pmu_ingest_counts_bad_frames_and_exits_3()
    {
        byte[] bad = Data(1_700_000_001, 20_000, new[] { A, B }, new[] { BlockA, BlockB });
        bad[20] ^= 0xFF;
        string path = Capture(Data(1_700_000_000, 0, new[] { A, B }, new[] { BlockA, BlockB }),   // before any CONFIG
            Config(1_000_000, A, B), Data(1_700_000_001, 0, new[] { A, B }, new[] { BlockA, BlockB }), bad);
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", path);
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("(1 decoded, 1 bad CRC, 0 malformed, 1 before any CONFIG)", r.StdOut, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pmu_ingest_refuses_a_file_it_cannot_frame_or_with_no_config()
    {
        string junk = Capture(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        string noConfig = Capture(Data(1_700_000_000, 0, new[] { A }, new[] { BlockA }));
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", junk);
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains("does not start a C37.118 frame", r.StdErr, StringComparison.Ordinal);
            CliProcess.Result n = CliProcess.RunBuilt("pmu-ingest", noConfig);
            Assert.True(n.ExitCode == 1, n.ToString());
            Assert.Contains("no usable CONFIG", n.StdErr, StringComparison.Ordinal);
            CliProcess.Result u = CliProcess.RunBuilt("pmu-ingest");
            Assert.True(u.ExitCode == 2, u.ToString());
        }
        finally { File.Delete(junk); File.Delete(noConfig); }
    }
}
