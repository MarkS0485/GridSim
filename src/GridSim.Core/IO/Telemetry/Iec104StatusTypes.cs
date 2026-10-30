using System.Buffers.Binary;
using GridSim.Core.Interpose;

namespace GridSim.Core.IO.Telemetry;

/// <summary>One step position information object (IEC 60870-5-101 / -104 type 5 M_ST_NA_1 or 32 M_ST_TB_1).</summary>
/// <param name="Ioa">Information Object Address (3 octets, little-endian on the wire, 0 to 16,777,215).</param>
/// <param name="Position">The step position, a signed 7-bit integer from -64 to +63 (the VTI "value" field, two's
/// complement in bits 0 to 6). For a transformer tap changer this is the tap step.</param>
/// <param name="Transient">The VTI "T" bit (bit 7): true while the equipment is in a transient state (the tap
/// changer is moving), false when it has come to rest.</param>
/// <param name="Quality">The QDS quality octet exactly as received: IV bit 7, NT bit 6, SB bit 5, BL bit 4, OV bit 0
/// (bits 1 to 3 are reserved and must be zero when encoding).</param>
/// <param name="TimeUtc">The CP56Time2a time tag in UTC for type 32; null for type 5. On decode a time tag whose
/// own invalid (IV) bit is set, or whose fields are out of range, also comes back null (the same rule as every
/// other timed type, <see cref="TimeDecode.Cp56Time2a"/>).</param>
public sealed record Iec104StepPosition(int Ioa, int Position, bool Transient, byte Quality, DateTime? TimeUtc);

/// <summary>One bitstring-of-32-bits information object (IEC 60870-5-101 / -104 type 7 M_BO_NA_1 or 33 M_BO_TB_1).</summary>
/// <param name="Ioa">Information Object Address (3 octets, little-endian on the wire).</param>
/// <param name="Bits">The 32-bit string (BSI). On the wire it is four octets, least significant octet first, the same
/// octet order as every other multi-octet IEC 60870-5 field; bit 0 of this value is bit 1 of the first octet.</param>
/// <param name="Quality">The QDS quality octet exactly as received (see <see cref="Iec104StepPosition.Quality"/>).</param>
/// <param name="TimeUtc">The CP56Time2a time tag in UTC for type 33; null for type 7.</param>
public sealed record Iec104Bitstring(int Ioa, uint Bits, byte Quality, DateTime? TimeUtc);

/// <summary>
/// N337 (October 2026): the IEC 60870-5-101 / -104 monitor-direction types that <see cref="Iec104Asdu"/> deliberately
/// leaves undecoded, as a SEPARATE, explicit codec:
///
/// <list type="bullet">
/// <item>type 5 M_ST_NA_1, step position information: element = VTI (1 octet) + QDS (1 octet);</item>
/// <item>type 32 M_ST_TB_1, step position with CP56Time2a: VTI + QDS + 7-octet time tag;</item>
/// <item>type 7 M_BO_NA_1, bitstring of 32 bits: element = BSI (4 octets) + QDS (1 octet);</item>
/// <item>type 33 M_BO_TB_1, bitstring of 32 bits with CP56Time2a: BSI + QDS + 7-octet time tag.</item>
/// </list>
///
/// <para><b>Relation to <see cref="Iec104Asdu.Parse"/>.</b> This codec came first (CF1), behind R089's "counted
/// unsupported" rule. Main then ruled (N337, CF2) that the four types are supported: <see cref="Iec104Asdu.Parse"/> now
/// decodes them too, with the same element layouts, into <see cref="RtuPointType.StepPosition"/> and
/// <see cref="RtuPointType.Bitstring"/> objects that the interposer turns into points. The difference that remains is
/// strictness: the pipeline decoder is lenient like every other type (it ignores trailing octets and never throws on a
/// field value), this codec refuses. A caller that wants the fail-loud reading, or needs to encode, uses this class.</para>
///
/// <para><b>Source of the layouts and what is [VERIFY].</b> The IEC 60870-5-101 text is not on disk (docs/reference).
/// The layouts are the published ones (IEC 60870-5-101 clause 7.3.1 type definitions, information elements VTI 7.2.6.5,
/// BSI 7.2.6.13 and QDS 7.2.6.3; clause numbers from the 2003 edition as widely quoted, [VERIFY] against the text):
/// VTI = value I7 in bits 0 to 6 (two's complement, -64..+63) and the transient bit T in bit 7; BSI = 32 bits sent
/// as four octets, least significant octet first [VERIFY the bit numbering convention: this codec treats the first
/// octet's bit 0 as bit 0 of <see cref="Iec104Bitstring.Bits"/>, the IEC 60870-5-4 convention for multi-octet fields];
/// QDS = OV bit 0, BL bit 4, SB bit 5, NT bit 6, IV bit 7, bits 1 to 3 reserved. The DUI is the 6-octet profile the
/// rest of this build targets (type, VSQ, two-octet COT, two-octet common address), the IOA three octets.</para>
///
/// <para><b>Fail loud.</b> Unlike the lenient pipeline decoder, this codec refuses (ArgumentException) a wrong type id,
/// an ASDU with no objects, a truncated element, trailing octets after the last element, and on encode any value out
/// of its field's range, reserved quality bits, a time that is not UTC or not whole milliseconds, or an SQ = 1 run whose
/// addresses are not consecutive. It never guesses.</para>
/// </summary>
public static class Iec104StatusTypes
{
    /// <summary>Type 5, M_ST_NA_1: step position information.</summary>
    public const byte StepPosition = 5;
    /// <summary>Type 32, M_ST_TB_1: step position information with CP56Time2a time tag.</summary>
    public const byte StepPositionTimed = 32;
    /// <summary>Type 7, M_BO_NA_1: bitstring of 32 bits.</summary>
    public const byte Bitstring32 = 7;
    /// <summary>Type 33, M_BO_TB_1: bitstring of 32 bits with CP56Time2a time tag.</summary>
    public const byte Bitstring32Timed = 33;

    // The fixed sizes, named so the arithmetic below reads in words.
    private const int DuiOctets = 6;          // type (1) + VSQ (1) + COT (2) + common address (2)
    private const int IoaOctets = 3;          // information object address
    private const int TimeOctets = 7;         // CP56Time2a
    private const int StepElementOctets = 2;  // VTI + QDS
    private const int BitsElementOctets = 5;  // BSI (4) + QDS
    private const int MaxObjects = 127;       // the VSQ count is 7 bits
    private const byte QdsReservedBits = 0x0E; // QDS bits 1 to 3 are reserved

    /// <summary>The header fields of an ASDU, as decoded or to encode.</summary>
    /// <param name="TypeId">Type identification.</param>
    /// <param name="Cause">Cause of transmission, 1 to 63 (3 spontaneous, 20 interrogated by station interrogation, ...).</param>
    /// <param name="Test">The COT test bit.</param>
    /// <param name="Negative">The COT P/N bit (negative confirmation).</param>
    /// <param name="OriginatorAddress">Originator address octet, 0 to 255 (0 when unused).</param>
    /// <param name="CommonAddress">Common address of ASDU, 0 to 65,535.</param>
    /// <param name="Sequence">The VSQ SQ bit: true when the objects share one base IOA and run consecutively.</param>
    public sealed record Header(byte TypeId, int Cause, bool Test, bool Negative, int OriginatorAddress, int CommonAddress, bool Sequence);

    /// <summary>True for the four types this codec handles (5, 32, 7, 33).</summary>
    public static bool Handles(byte typeId) => typeId is StepPosition or StepPositionTimed or Bitstring32 or Bitstring32Timed;

    // ------------------------------------------------------------------------------------------------------------
    // Decode
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Decode a type 5 or 32 ASDU (the bytes after the APCI) into its header and step position objects.</summary>
    /// <exception cref="ArgumentException">Not type 5 / 32, no objects, truncated, or trailing octets.</exception>
    public static (Header Header, IReadOnlyList<Iec104StepPosition> Objects) DecodeStepPositions(ReadOnlySpan<byte> asdu)
    {
        Header h = ReadHeader(asdu, out int count, StepPosition, StepPositionTimed);
        bool timed = h.TypeId == StepPositionTimed;
        var list = new List<Iec104StepPosition>(count);
        WalkObjects(asdu, h.Sequence, count, StepElementOctets, timed, (ioa, element, time) =>
        {
            byte vti = element[0];
            // Value I7: bits 0 to 6, two's complement. 0x40 is the sign bit of the 7-bit field.
            int raw = vti & 0x7F;
            int position = raw >= 0x40 ? raw - 0x80 : raw;
            bool transient = (vti & 0x80) != 0;
            list.Add(new Iec104StepPosition(ioa, position, transient, element[1], time));
        });
        return (h, list);
    }

    /// <summary>Decode a type 7 or 33 ASDU into its header and bitstring objects.</summary>
    /// <exception cref="ArgumentException">Not type 7 / 33, no objects, truncated, or trailing octets.</exception>
    public static (Header Header, IReadOnlyList<Iec104Bitstring> Objects) DecodeBitstrings(ReadOnlySpan<byte> asdu)
    {
        Header h = ReadHeader(asdu, out int count, Bitstring32, Bitstring32Timed);
        bool timed = h.TypeId == Bitstring32Timed;
        var list = new List<Iec104Bitstring>(count);
        WalkObjects(asdu, h.Sequence, count, BitsElementOctets, timed, (ioa, element, time) =>
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(element);   // least significant octet first
            list.Add(new Iec104Bitstring(ioa, bits, element[4], time));
        });
        return (h, list);
    }

    /// <summary>The callback the object walker hands each element to: its IOA, the element octets (without the time
    /// tag) and the decoded time tag (null when untimed or the tag is invalid).</summary>
    private delegate void ElementSink(int ioa, ReadOnlySpan<byte> element, DateTime? time);

    /// <summary>Read and check the 6-octet DUI. The type must be one of the two allowed; the object count must be at least 1.</summary>
    private static Header ReadHeader(ReadOnlySpan<byte> asdu, out int count, byte untimed, byte timed)
    {
        if (asdu.Length < DuiOctets)
            throw new ArgumentException($"IEC 60870-5 ASDU of {asdu.Length} octet(s) is shorter than its {DuiOctets}-octet data unit identifier.", nameof(asdu));
        byte typeId = asdu[0];
        if (typeId != untimed && typeId != timed)
            throw new ArgumentException($"IEC 60870-5 ASDU type {typeId} is not type {untimed} or {timed}; this decoder handles only those.", nameof(asdu));
        byte vsq = asdu[1];
        count = vsq & 0x7F;
        if (count == 0)
            throw new ArgumentException($"IEC 60870-5 type {typeId} ASDU declares zero information objects (VSQ number = 0).", nameof(asdu));
        byte cot = asdu[2];
        return new Header(
            TypeId: typeId,
            Cause: cot & 0x3F,
            Test: (cot & 0x80) != 0,
            Negative: (cot & 0x40) != 0,
            OriginatorAddress: asdu[3],
            CommonAddress: asdu[4] | (asdu[5] << 8),
            Sequence: (vsq & 0x80) != 0);
    }

    /// <summary>Walk the information objects (SQ = 0: an IOA before every element; SQ = 1: one base IOA, then the
    /// elements for consecutive addresses), check every length, and refuse trailing octets.</summary>
    private static void WalkObjects(ReadOnlySpan<byte> asdu, bool sequence, int count, int elementOctets, bool timed, ElementSink sink)
    {
        int timeOctets = timed ? TimeOctets : 0;
        int p = DuiOctets;
        int baseIoa = 0;
        if (sequence)
        {
            if (p + IoaOctets > asdu.Length)
                throw new ArgumentException("IEC 60870-5 SQ = 1 ASDU is missing its base information object address.", nameof(asdu));
            baseIoa = ReadIoa(asdu, p);
            p += IoaOctets;
        }
        for (int i = 0; i < count; i++)
        {
            int ioa;
            if (sequence)
            {
                ioa = baseIoa + i;
                if (ioa > 0xFFFFFF)
                    throw new ArgumentException($"IEC 60870-5 SQ = 1 ASDU runs past the largest 3-octet address (base {baseIoa}, {count} elements).", nameof(asdu));
            }
            else
            {
                if (p + IoaOctets > asdu.Length)
                    throw new ArgumentException($"IEC 60870-5 ASDU truncated at the address of object {i + 1} of {count}.", nameof(asdu));
                ioa = ReadIoa(asdu, p);
                p += IoaOctets;
            }
            if (p + elementOctets + timeOctets > asdu.Length)
                throw new ArgumentException($"IEC 60870-5 ASDU truncated inside object {i + 1} of {count} (needs {elementOctets + timeOctets} octet(s) at offset {p}, has {asdu.Length - p}).", nameof(asdu));
            DateTime? time = timed ? TimeDecode.Cp56Time2a(asdu.Slice(p + elementOctets, TimeOctets)) : null;
            sink(ioa, asdu.Slice(p, elementOctets), time);
            p += elementOctets + timeOctets;
        }
        if (p != asdu.Length)
            throw new ArgumentException($"IEC 60870-5 ASDU has {asdu.Length - p} octet(s) after its last declared object (VSQ number {count}); refusing rather than ignoring them.", nameof(asdu));
    }

    private static int ReadIoa(ReadOnlySpan<byte> b, int p) => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);

    // ------------------------------------------------------------------------------------------------------------
    // Encode
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Encode step position objects as one type 5 ASDU (all untimed) or type 32 ASDU (all timed). The header's
    /// type must be 5 or 32 and match the objects: type 5 objects carry no time, type 32 objects all carry one.</summary>
    /// <exception cref="ArgumentException">Any field out of range or inconsistent with the type (see the class summary).</exception>
    public static byte[] EncodeStepPositions(Header header, IReadOnlyList<Iec104StepPosition> objects)
    {
        CheckHeader(header, objects.Count, StepPosition, StepPositionTimed);
        bool timed = header.TypeId == StepPositionTimed;
        var b = new List<byte>(DuiOctets + IoaOctets + objects.Count * (IoaOctets + StepElementOctets + TimeOctets));
        WriteHeader(b, header, objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            Iec104StepPosition o = objects[i];
            if (o.Position is < -64 or > 63)
                throw new ArgumentException($"Step position {o.Position} at IOA {o.Ioa} is outside the 7-bit range -64..+63.", nameof(objects));
            WriteAddress(b, header, objects[0].Ioa, o.Ioa, i);
            // Value I7 in bits 0 to 6 (two's complement: masking a negative int keeps its low seven bits), T in bit 7.
            b.Add((byte)((o.Position & 0x7F) | (o.Transient ? 0x80 : 0x00)));
            b.Add(CheckQuality(o.Quality, o.Ioa));
            WriteTime(b, timed, o.TimeUtc, o.Ioa);
        }
        return b.ToArray();
    }

    /// <summary>Encode bitstring objects as one type 7 ASDU (untimed) or type 33 ASDU (timed).</summary>
    /// <exception cref="ArgumentException">Any field out of range or inconsistent with the type (see the class summary).</exception>
    public static byte[] EncodeBitstrings(Header header, IReadOnlyList<Iec104Bitstring> objects)
    {
        CheckHeader(header, objects.Count, Bitstring32, Bitstring32Timed);
        bool timed = header.TypeId == Bitstring32Timed;
        var b = new List<byte>(DuiOctets + IoaOctets + objects.Count * (IoaOctets + BitsElementOctets + TimeOctets));
        WriteHeader(b, header, objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            Iec104Bitstring o = objects[i];
            WriteAddress(b, header, objects[0].Ioa, o.Ioa, i);
            // BSI, least significant octet first.
            b.Add((byte)(o.Bits & 0xFF));
            b.Add((byte)((o.Bits >> 8) & 0xFF));
            b.Add((byte)((o.Bits >> 16) & 0xFF));
            b.Add((byte)((o.Bits >> 24) & 0xFF));
            b.Add(CheckQuality(o.Quality, o.Ioa));
            WriteTime(b, timed, o.TimeUtc, o.Ioa);
        }
        return b.ToArray();
    }

    /// <summary>Encode a UTC time as a 7-octet CP56Time2a time tag: milliseconds of the minute (u16 LE), minute (bits 0
    /// to 5, IV bit 7 clear), hour (bits 0 to 4, SU bit 7 clear), day of month (bits 0 to 4) with day of week in bits 5
    /// to 7 (1 Monday .. 7 Sunday), month (bits 0 to 3), year of the century (bits 0 to 6). The inverse of
    /// <see cref="TimeDecode.Cp56Time2a"/> for every time it accepts (the decoder ignores the day of week).</summary>
    /// <exception cref="ArgumentException">Not UTC, outside 2000..2099, or not a whole number of milliseconds.</exception>
    public static byte[] EncodeCp56Time2a(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            throw new ArgumentException($"CP56Time2a encoding needs a UTC time (Kind = Utc); got Kind = {utc.Kind}. Convert it first; this codec does not guess the offset.", nameof(utc));
        // The year field is 7 bits (0..127), but the decoder in this build reads it as 2000 + year and the standard's
        // century handling is a [VERIFY] item; refuse what would not round-trip as one century.
        if (utc.Year is < 2000 or > 2099)
            throw new ArgumentException($"CP56Time2a carries a year of the century; {utc.Year} is outside 2000..2099.", nameof(utc));
        if (utc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentException($"CP56Time2a has millisecond resolution; {utc:O} has a sub-millisecond part. Round it explicitly first.", nameof(utc));
        int ms = utc.Second * 1000 + utc.Millisecond;
        int dayOfWeek = utc.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)utc.DayOfWeek;   // ISO numbering, 1 Monday .. 7 Sunday
        return new[]
        {
            (byte)(ms & 0xFF), (byte)((ms >> 8) & 0xFF),
            (byte)utc.Minute,
            (byte)utc.Hour,
            (byte)(utc.Day | (dayOfWeek << 5)),
            (byte)utc.Month,
            (byte)(utc.Year - 2000),
        };
    }

    private static void CheckHeader(Header h, int count, byte untimed, byte timed)
    {
        if (h.TypeId != untimed && h.TypeId != timed)
            throw new ArgumentException($"Header type {h.TypeId} is not type {untimed} or {timed}.", nameof(h));
        if (count is < 1 or > MaxObjects)
            throw new ArgumentException($"An ASDU carries 1 to {MaxObjects} information objects (7-bit VSQ number); got {count}.", nameof(h));
        if (h.Cause is < 1 or > 63)
            throw new ArgumentException($"Cause of transmission {h.Cause} is outside 1..63 (0 is not used, the field is 6 bits).", nameof(h));
        if (h.OriginatorAddress is < 0 or > 255)
            throw new ArgumentException($"Originator address {h.OriginatorAddress} is outside 0..255.", nameof(h));
        if (h.CommonAddress is < 0 or > 0xFFFF)
            throw new ArgumentException($"Common address {h.CommonAddress} is outside 0..65535.", nameof(h));
    }

    private static void WriteHeader(List<byte> b, Header h, int count)
    {
        b.Add(h.TypeId);
        b.Add((byte)(count | (h.Sequence ? 0x80 : 0x00)));
        b.Add((byte)(h.Cause | (h.Negative ? 0x40 : 0x00) | (h.Test ? 0x80 : 0x00)));
        b.Add((byte)h.OriginatorAddress);
        b.Add((byte)(h.CommonAddress & 0xFF));
        b.Add((byte)((h.CommonAddress >> 8) & 0xFF));
    }

    /// <summary>SQ = 0: write every object's IOA. SQ = 1: write the first IOA once and require each later object to be
    /// the next address (the wire cannot say otherwise).</summary>
    private static void WriteAddress(List<byte> b, Header h, int baseIoa, int ioa, int index)
    {
        if (ioa is < 0 or > 0xFFFFFF)
            throw new ArgumentException($"Information object address {ioa} is outside the 3-octet range 0..16777215.", nameof(ioa));
        if (h.Sequence)
        {
            if (ioa != baseIoa + index)
                throw new ArgumentException($"SQ = 1 needs consecutive addresses: object {index + 1} has IOA {ioa}, expected {baseIoa + index}.", nameof(ioa));
            if (index > 0) return;
        }
        b.Add((byte)(ioa & 0xFF));
        b.Add((byte)((ioa >> 8) & 0xFF));
        b.Add((byte)((ioa >> 16) & 0xFF));
    }

    private static byte CheckQuality(byte qds, int ioa)
    {
        if ((qds & QdsReservedBits) != 0)
            throw new ArgumentException($"QDS 0x{qds:X2} at IOA {ioa} sets reserved bits 1 to 3; only OV (bit 0), BL, SB, NT and IV (bits 4 to 7) may be set.", nameof(qds));
        return qds;
    }

    private static void WriteTime(List<byte> b, bool timed, DateTime? time, int ioa)
    {
        if (timed)
        {
            if (time is null)
                throw new ArgumentException($"The time-tagged type needs a time on every object; IOA {ioa} has none.", nameof(time));
            b.AddRange(EncodeCp56Time2a(time.Value));
        }
        else if (time is not null)
        {
            throw new ArgumentException($"The untimed type carries no time tag, but IOA {ioa} has one; use the time-tagged type (32 or 33) to send it.", nameof(time));
        }
    }
}
