using System.Buffers.Binary;

namespace GridSim.Core.IO.Telemetry;

/// <summary>The IEC 61850 GOOSE Ethernet-frame header (after the MAC addresses) with its raw APDU payload.</summary>
/// <param name="AppId">Application identifier (APPID).</param>
/// <param name="Pdu">The raw goosePdu (ASN.1/BER) bytes.</param>
// GR-650: Simulation is the Reserved1 most significant bit (IEC 61850-8-1 Ed. 2 "simulation"): a test frame. It used to be
// dropped, so a simulated frame was indistinguishable from a live one.
public sealed record GooseHeader(int AppId, byte[] Pdu, bool Simulation = false);

/// <summary>
/// A minimal codec for the IEC 61850 GOOSE frame header - the layer-2 real-time messaging used for
/// substation protection/status. It encodes/decodes the fixed header that follows the Ethernet MAC addresses:
/// the 0x88B8 EtherType, the APPID, the length field and the two reserved words, plus the raw goosePdu.
/// <para><b>SCAFFOLDING (R089, October 2026): framing only, not a telemetry path.</b> The ASN.1/BER goosePdu (gocbRef,
/// StNum / SqNum, timeAllowedToLive, allData) is returned as raw bytes and not decoded, there is no GOOSE
/// <c>IRtuProtocol</c>, and nothing in the product calls this: <c>interpose --protocol</c> offers only iec104 and
/// modbus. The GOOSE quality fold and time decoder (<c>QualityFold.FromGoose</c>, <c>TimeDecode.IsoUtcTime</c>) are the
/// same scaffolding. IEC 61850-8-1 is not in docs/reference, so the layout here is [VERIFY].</para>
/// </summary>
public static class Iec61850GooseFrame
{
    private const ushort EtherType = 0x88B8;
    private const int HeaderBytes = 10; // EtherType(2) + APPID(2) + Length(2) + Reserved1(2) + Reserved2(2)

    /// <summary>Encode the GOOSE header + PDU (starting at the EtherType, i.e. after the MAC addresses).</summary>
    public static byte[] ToBytes(GooseHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(header.Pdu);
        // Bug hunt 2026-10-29, finding 55: both header fields are 16-bit. An APPID outside 0..0xFFFF, or a PDU so long
        // that the Length field (8 + PDU octets) passes 0xFFFF, used to be cast to ushort and wrap, emitting a frame whose
        // APPID was another device's or whose Length disagreed with its size (the IEC 104 encoder guards the same
        // overflow, GR-654). Each is refused by name instead.
        if (header.AppId < 0 || header.AppId > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(header), header.AppId, $"GOOSE APPID {header.AppId} does not fit the 16-bit field (0 to 65535).");
        if (header.Pdu.Length > ushort.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(header), header.Pdu.Length,
                $"A GOOSE PDU of {header.Pdu.Length} octets makes the Length field {8L + header.Pdu.Length}, which does not fit 16 bits (at most {ushort.MaxValue - 8} PDU octets).");
        int length = 8 + header.Pdu.Length; // octets from APPID onward (APPID+Length+Res1+Res2 = 8) + PDU
        var b = new byte[HeaderBytes + header.Pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0), EtherType);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)header.AppId);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)length);
        // Reserved1 (6..7): only the simulation bit (bit 15) is set, when the header says so; Reserved2 (8..9) stays zero.
        if (header.Simulation) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 0x8000);
        header.Pdu.CopyTo(b, HeaderBytes);
        return b;
    }

    /// <summary>Decode a GOOSE header + PDU from the EtherType onward.</summary>
    public static GooseHeader Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderBytes) throw new ArgumentException("GOOSE frame shorter than its 10-byte header.");
        ushort ethertype = BinaryPrimitives.ReadUInt16BigEndian(frame);
        if (ethertype != EtherType) throw new ArgumentException($"EtherType 0x{ethertype:X4}, expected 0x88B8 (GOOSE).");

        int appId = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);
        int pduLen = length - 8;
        if (pduLen < 0 || HeaderBytes + pduLen > frame.Length) throw new ArgumentException("GOOSE length field out of range.");
        bool simulation = (BinaryPrimitives.ReadUInt16BigEndian(frame[6..]) & 0x8000) != 0;   // GR-650
        return new GooseHeader(appId, frame.Slice(HeaderBytes, pduLen).ToArray(), simulation);
    }
}
