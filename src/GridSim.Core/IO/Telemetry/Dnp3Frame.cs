namespace GridSim.Core.IO.Telemetry;

/// <summary>A decoded DNP3 data-link-layer header.</summary>
/// <param name="Control">The control octet (direction, primary, function code).</param>
/// <param name="Destination">Destination data-link address.</param>
/// <param name="Source">Source data-link address.</param>
public sealed record Dnp3DataLinkHeader(byte Control, int Destination, int Source);

/// <summary>
/// A minimal codec for the DNP3 (IEEE 1815) data-link-layer frame header - the framing DNP3 SCADA telemetry
/// arrives on. It encodes/decodes the 10-byte header (start 0x05 0x64, length, control, little-endian
/// destination and source addresses, and the CRC-16/DNP block check) and validates the CRC.
/// <para><b>SCAFFOLDING (R089, October 2026): framing only, not a telemetry path.</b> It reads just the 10-byte
/// link header; the user-data blocks and their per-block CRCs, the transport layer and the application layer (object
/// groups, variations, flags, timestamps) are not implemented, there is no DNP3 <c>IRtuProtocol</c>, and nothing in
/// the product calls this: <c>interpose --protocol</c> offers only iec104 and modbus (and <c>--list-protocols</c> says
/// so). The DNP3 quality fold and time decoder (<c>QualityFold.FromDnp3</c>, <c>TimeDecode.Dnp3AbsoluteTime</c>) are
/// the same scaffolding. IEEE 1815 is not in docs/reference, so the layout here is [VERIFY].</para>
/// </summary>
public static class Dnp3Frame
{
    /// <summary>CRC-16/DNP (polynomial 0x3D65 reflected, init 0, reflected in/out, final XOR 0xFFFF).</summary>
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        int crc = 0;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA6BC : crc >> 1;
        }
        return (ushort)(~crc & 0xFFFF);
    }

    /// <summary>Encode a header-only (no user data) data-link frame.</summary>
    public static byte[] ToBytes(Dnp3DataLinkHeader header)
    {
        var b = new byte[10];
        b[0] = 0x05;
        b[1] = 0x64;
        b[2] = 5; // LENGTH = control + dest + src (no user data)
        b[3] = header.Control;
        b[4] = (byte)(header.Destination & 0xFF);
        b[5] = (byte)((header.Destination >> 8) & 0xFF);
        b[6] = (byte)(header.Source & 0xFF);
        b[7] = (byte)((header.Source >> 8) & 0xFF);
        ushort crc = Crc(b.AsSpan(0, 8));
        b[8] = (byte)(crc & 0xFF);
        b[9] = (byte)((crc >> 8) & 0xFF);
        return b;
    }

    /// <summary>Decode and CRC-validate a data-link header.</summary>
    public static Dnp3DataLinkHeader Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 10) throw new ArgumentException("DNP3 frame shorter than the 10-byte data-link header.");
        if (frame[0] != 0x05 || frame[1] != 0x64)
            throw new ArgumentException($"DNP3 start octets are 0x{frame[0]:X2}{frame[1]:X2}, expected 0x0564.");

        ushort expected = (ushort)(frame[8] | (frame[9] << 8));
        if (Crc(frame[..8]) != expected) throw new InvalidDataException("DNP3 data-link header CRC mismatch.");
        // GR-643: LENGTH counts CONTROL, DESTINATION and SOURCE (5 octets) plus the user data, so it is at least 5; a
        // smaller value is a malformed header, not a frame to decode.
        if (frame[2] < 5)
            throw new InvalidDataException($"DNP3 data-link LENGTH octet is {frame[2]}; it must be at least 5.");

        byte control = frame[3];
        int dest = frame[4] | (frame[5] << 8);
        int src = frame[6] | (frame[7] << 8);
        return new Dnp3DataLinkHeader(control, dest, src);
    }
}
