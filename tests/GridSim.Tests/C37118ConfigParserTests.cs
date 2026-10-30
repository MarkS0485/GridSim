using System;
using System.Buffers.Binary;
using System.Text;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>The CONFIG-2 parser: build a one-PMU config frame and recover TIME_BASE, DATA_RATE and the per-PMU
/// FORMAT/counts the DATA decoder needs.</summary>
public class C37118ConfigParserTests
{
    [Fact]
    public void Parses_time_base_data_rate_and_per_pmu_format()
    {
        const int phnmr = 2, annmr = 0, dgnmr = 0;
        int chnam = 16 * (phnmr + annmr + 16 * dgnmr);
        int pmuBlock = 16 + 2 + 2 + 2 + 2 + 2 + chnam + 4 * phnmr + 4 * annmr + 4 * dgnmr + 2 + 2;
        int size = 14 + 4 + 2 + pmuBlock + 2 + 2; // header + timebase + numpmu + block + datarate + chk

        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = (byte)(((int)C37118FrameType.Config2 << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 7);       // IDCODE (stream)
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), 1_000);   // SOC
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), 0);      // FRACSEC
        int off = 14;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(off), 1_000_000); off += 4; // TIME_BASE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;         // NUM_PMU

        Encoding.ASCII.GetBytes("STATION_A       ").CopyTo(f.AsSpan(off)); off += 16; // STN (16)
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 42); off += 2;   // IDCODE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off),
            (ushort)(C37118DataFormat.PhasorFloat | C37118DataFormat.FreqFloat)); off += 2; // FORMAT
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), phnmr); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), annmr); off += 2;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), dgnmr); off += 2;
        off += chnam + 4 * phnmr + 4 * annmr + 4 * dgnmr; // CHNAM + unit tables (zeros)
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 1); off += 2;    // FNOM: 50 Hz
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 3); off += 2;    // CFGCNT
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 50); off += 2;    // DATA_RATE
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));

        Assert.True(C37118Decoder.VerifyCrc(f));
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(f);

        Assert.Equal(1_000_000, cfg.TimeBase);
        Assert.Equal(50, cfg.DataRate);
        Assert.Single(cfg.Pmus);
        PmuConfig pmu = cfg.Pmus[0];
        Assert.Equal("STATION_A", pmu.StationName);
        Assert.Equal(42, pmu.IdCode);
        Assert.Equal(2, pmu.PhasorCount);
        Assert.True(pmu.Is50Hz);
        Assert.Equal(3, pmu.ConfigCount);
        Assert.True((pmu.Format & C37118DataFormat.PhasorFloat) != 0);
    }
}
