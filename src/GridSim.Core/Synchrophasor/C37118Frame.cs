using System.Buffers.Binary;

namespace GridSim.Core.Synchrophasor;

/// <summary>IEEE C37.118.2 frame type, from SYNC byte-1 bits 6..4.</summary>
public enum C37118FrameType
{
    /// <summary>Measurement data frame.</summary>
    Data = 0,
    /// <summary>Human-readable header frame.</summary>
    Header = 1,
    /// <summary>Configuration frame 1.</summary>
    Config1 = 2,
    /// <summary>Configuration frame 2 (the machine-readable config the decoder needs).</summary>
    Config2 = 3,
    /// <summary>Command frame (to the PMU/PDC).</summary>
    Command = 4,
    /// <summary>Configuration frame 3.</summary>
    Config3 = 5,
}

/// <summary>
/// The CONFIG FORMAT word (low 4 bits) describing how a PMU encodes its DATA frame values. The flag values ARE
/// the IEEE C37.118.2-2011 bit positions (Table 8), because <see cref="C37118ConfigParser"/> casts the raw
/// FORMAT word straight into this enum: bit 0 = phasors polar, bit 1 = phasors float, bit 2 = analogs float,
/// bit 3 = FREQ/DFREQ float. (R046: the values used to run in the reverse order - PhasorFloat = 1 ...
/// PhasorPolar = 8 - so a real CONFIG word such as 0x000E, float rectangular phasors with float frequency,
/// decoded as integer polar phasors with garbage magnitudes and Status Ok. Only 0x00 and 0x0F were right.)
/// </summary>
[Flags]
public enum C37118DataFormat
{
    /// <summary>Fixed 16-bit integer phasors, rectangular, integer freq/analog (the all-zero default).</summary>
    None = 0,
    /// <summary>Bit 0: phasors are polar (magnitude, angle) (else rectangular real/imag).</summary>
    PhasorPolar = 1,
    /// <summary>Bit 1: phasors are 32-bit IEEE floats (else 16-bit integers).</summary>
    PhasorFloat = 2,
    /// <summary>Bit 2: analog values are 32-bit floats (else 16-bit integers).</summary>
    AnalogFloat = 4,
    /// <summary>Bit 3: FREQ/DFREQ are 32-bit floats (else 16-bit integers).</summary>
    FreqFloat = 8,
}

/// <summary>A synchrophasor value in rectangular engineering units (volts or amps, referenced to the datum
/// the PMU reports against).</summary>
public readonly struct Phasor
{
    /// <summary>Real (in-phase) component.</summary>
    public double Real { get; }
    /// <summary>Imaginary (quadrature) component.</summary>
    public double Imag { get; }

    public Phasor(double real, double imag) { Real = real; Imag = imag; }

    /// <summary>Construct from polar magnitude and angle (radians).</summary>
    public static Phasor FromPolar(double magnitude, double angleRad)
        => new(magnitude * DetMath.Cos(angleRad), magnitude * DetMath.Sin(angleRad));

    /// <summary>Magnitude sqrt(Real^2 + Imag^2).</summary>
    public double Magnitude => Math.Sqrt(Real * Real + Imag * Imag);
    /// <summary>Angle atan2(Imag, Real), radians.</summary>
    public double AngleRad => DetMath.Atan2(Imag, Real);
}

/// <summary>The 14-byte common header every C37.118.2 frame begins with (all fields big-endian).</summary>
public readonly struct C37118CommonHeader
{
    /// <summary>Frame type from SYNC bits 6..4.</summary>
    public C37118FrameType FrameType { get; }
    /// <summary>Protocol version (SYNC bits 3..0): 1 = 2005 mapping, 2 = 2011.</summary>
    public int Version { get; }
    /// <summary>Total frame length in bytes (header..CHK inclusive).</summary>
    public int FrameSize { get; }
    /// <summary>Data-stream / PMU / PDC id code.</summary>
    public int IdCode { get; }
    /// <summary>Second-of-century: UNIX seconds since 1970-01-01T00:00:00Z.</summary>
    public uint Soc { get; }
    /// <summary>Fraction-of-second raw count (lower 24 bits of FRACSEC), in units of 1/TIME_BASE.</summary>
    public uint FracSecRaw { get; }
    /// <summary>Message time-quality flags (top byte of FRACSEC).</summary>
    public byte TimeQuality { get; }

    private C37118CommonHeader(C37118FrameType type, int version, int frameSize, int idCode,
        uint soc, uint fracSecRaw, byte timeQuality)
    {
        FrameType = type; Version = version; FrameSize = frameSize; IdCode = idCode;
        Soc = soc; FracSecRaw = fracSecRaw; TimeQuality = timeQuality;
    }

    /// <summary>Parse the common header from the start of a frame. Does not convert time to UTC (that needs
    /// TIME_BASE from the configuration frame).</summary>
    public static C37118CommonHeader Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) throw new ArgumentException("C37.118 frame shorter than the 14-byte header.");
        if (frame[0] != 0xAA) throw new ArgumentException($"C37.118 SYNC byte 0 is 0x{frame[0]:X2}, expected 0xAA.");
        byte sync1 = frame[1];
        var type = (C37118FrameType)((sync1 >> 4) & 0x07);
        int version = sync1 & 0x0F;
        int frameSize = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        int idCode = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
        uint soc = BinaryPrimitives.ReadUInt32BigEndian(frame[6..]);
        uint fracSecWord = BinaryPrimitives.ReadUInt32BigEndian(frame[10..]);
        return new C37118CommonHeader(type, version, frameSize, idCode, soc,
            fracSecWord & 0x00FF_FFFF, (byte)(fracSecWord >> 24));
    }
}

/// <summary>CRC-CCITT (width 16, polynomial 0x1021, init 0xFFFF, no reflection, no final XOR) - the frame
/// integrity check C37.118 uses.</summary>
public static class C37118Crc
{
    /// <summary>Compute the CRC-CCITT over <paramref name="bytes"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
