using System;
using System.Buffers.Binary;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>The C37.118 DATA decoder's fixed-integer format path: 16-bit rectangular phasors and integer
/// freq/dfreq decode to the authored values scaled by the PMU's CONFIG (FNOM, PHUNIT).</summary>
public class C37118FixedFormatTests
{
    [Fact]
    public void Decodes_a_fixed_integer_rectangular_data_frame()
    {
        // Fixed format: phasors 2x int16, freq/dfreq 2x int16. One PMU, one phasor.
        int size = 14 + 2 + 4 + 4 + 2; // header + STAT + phasor(4) + freq/dfreq(4) + CHK
        var f = new byte[size];
        f[0] = 0xAA;
        f[1] = (byte)(((int)C37118FrameType.Data << 4) | 1);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), 1_000);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), 250_000); // 0.25 s at TIME_BASE 1e6
        int off = 14;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(off), 0); off += 2;      // STAT
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 2300); off += 2;    // phasor real
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), -150); off += 2;    // phasor imag
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), 5001); off += 2;    // freq
        BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(off), -12); off += 2;     // dfreq
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(size - 2), C37118Crc.Compute(f.AsSpan(0, size - 2)));

        // Moved 2026-10 (Q3, authorised by Mark): this test used to decode with only the FORMAT word and expect the
        // RAW COUNTS back (real 2300, imag -150, FreqHz 5001, RoCoF -12), i.e. it encoded the R293 defect. IEEE
        // C37.118.2-2011 Table 5 defines the integer fields as scaled quantities: FREQ = FNOM + count/1000 Hz,
        // DFREQ = count/100 Hz/s, phasor component = count x PHUNIT x 1e-5 (PHUNIT and FNOM from the CONFIG frame,
        // Table 9). The frame bytes are unchanged; the decode now takes the PMU's configuration (50 Hz nominal,
        // PHUNIT 1,000,000 = 10 V per count, voltage channel) and the expectations are the scaled values. The float-
        // only overload now refuses this all-integer format (covered in C37118IntegerScalingTests).
        var pmu = new PmuConfig("PMU_3", IdCode: 3, C37118DataFormat.None, PhasorCount: 1, AnalogCount: 0,
            DigitalCount: 0, Is50Hz: true, ConfigCount: 1, PhasorUnits: new uint[] { 1_000_000u });
        C37118DataFrame d = C37118Decoder.DecodeData(f, 1_000_000, pmu); // all-integer format, scaled by the CONFIG

        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
        Assert.Equal(23_000.0, d.Phasors[0].Real, 6);     // 2300 counts x 10 V
        Assert.Equal(-1_500.0, d.Phasors[0].Imag, 6);     // -150 counts x 10 V
        Assert.Equal(55.001, d.FreqHz, 6);                // 50 Hz + 5001 mHz
        Assert.Equal(-0.12, d.RocofHzPerS, 6);            // -12 / 100 Hz/s
        DateTime expected = DateTime.UnixEpoch.AddSeconds(1_000).AddSeconds(0.25);
        Assert.True(Math.Abs((d.TimeUtc - expected).TotalSeconds) < 1e-6);
    }
}
