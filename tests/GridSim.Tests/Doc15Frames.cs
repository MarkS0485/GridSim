using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using GridSim.Core.Synchrophasor;

namespace GridSim.Tests;

/// <summary>
/// DOC-15 (docs-session stream 15, October 2026): byte-for-byte builders of IEEE C37.118.2 CONFIG-2 and DATA frames for
/// the synchrophasor tests. Every PMU here sends FLOAT rectangular phasors and FLOAT FREQ / DFREQ (FORMAT 0x000A), with no
/// analogue or digital channels, so a DATA block is STAT (2 bytes), 8 bytes per phasor, FREQ and DFREQ (4 bytes each).
/// The layout is the one the decoder documents (IEEE C37.118.2-2011 Tables 5 and 9 as recorded there [VERIFY]): the
/// 14-byte common header (SYNC, FRAMESIZE, IDCODE, SOC, FRACSEC), the body, and the CRC-CCITT check word.
/// </summary>
internal static class Doc15Frames
{
    /// <summary>FLOAT phasors and FLOAT FREQ / DFREQ, rectangular.</summary>
    internal static readonly C37118DataFormat FloatAll = C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat;

    /// <summary>One PMU of a CONFIG frame: its id, station name and PHUNIT words (one per phasor: the top byte is the
    /// channel type, 0 voltage, 1 current).</summary>
    internal sealed record Pmu(int IdCode, string Station, uint[] PhUnits)
    {
        internal int Phasors => PhUnits.Length;
    }

    /// <summary>A PHUNIT word for a voltage channel.</summary>
    internal const uint Voltage = 0x0000_0000;

    /// <summary>A PHUNIT word for a current channel.</summary>
    internal const uint Current = 0x0100_0000;

    // Write the 14-byte common header; FRAMESIZE is the array's length.
    private static void Header(byte[] f, C37118FrameType type, uint soc, uint fracsec)
    {
        f[0] = 0xAA;
        f[1] = (byte)(((int)type << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)f.Length);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), soc);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), fracsec);
    }

    // Write the CRC-CCITT of everything before the last two bytes into them.
    private static byte[] Finish(byte[] f)
    {
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    /// <summary>A CONFIG-2 frame with the given TIME_BASE (written as the whole 32-bit field) and PMUs, DATA_RATE 50.</summary>
    internal static byte[] Config(uint timeBase, uint soc, params Pmu[] pmus)
    {
        int size = 14 + 4 + 2 + 2 + 2;   // header, TIME_BASE, NUM_PMU, DATA_RATE, CHK
        foreach (Pmu p in pmus) size += 16 + 2 + 2 + 2 + 2 + 2 + 16 * p.Phasors + 4 * p.Phasors + 2 + 2;
        var f = new byte[size];
        Header(f, C37118FrameType.Config2, soc, 0);
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), timeBase); off += 4;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)pmus.Length); off += 2;
        foreach (Pmu p in pmus)
        {
            Encoding.ASCII.GetBytes(p.Station.PadRight(16)).CopyTo(f.AsSpan(off)); off += 16;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.IdCode); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)FloatAll); off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), (ushort)p.Phasors); off += 2;
            off += 4;                                     // ANNMR, DGNMR = 0
            off += 16 * p.Phasors;                        // channel names (blank)
            foreach (uint u in p.PhUnits) { BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), u); off += 4; }
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;   // FNOM: 50 Hz
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;   // CFGCNT
        }
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50);                 // DATA_RATE
        return Finish(f);
    }

    /// <summary>A DATA frame with one block per PMU, in the order given: STAT 0, every phasor (re, im) as given (the
    /// k-th PMU's phasors are <paramref name="phasors"/>[k]), FREQ 50 Hz, DFREQ 0.</summary>
    internal static byte[] Data(uint soc, uint fracsec, params (float Re, float Im)[][] phasors)
    {
        int size = 14 + 2 + phasors.Sum(p => 2 + 8 * p.Length + 8);
        var f = new byte[size];
        Header(f, C37118FrameType.Data, soc, fracsec);
        int off = 14;
        foreach ((float Re, float Im)[] block in phasors)
        {
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 0); off += 2;   // STAT: good data, synchronised
            foreach ((float re, float im) in block)
            {
                BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), re); off += 4;
                BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), im); off += 4;
            }
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 50.0f); off += 4;
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(off), 0.0f); off += 4;
        }
        return Finish(f);
    }
}
