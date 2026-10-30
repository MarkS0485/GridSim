using System.Buffers.Binary;
using GridSim.Core.Interpose;

namespace GridSim.Core.IO.Telemetry;

/// <summary>One decoded IEC 60870-5 information object: its address (IOA), the measured value in the
/// type's native scale, the quality descriptor octet (QDS/SIQ/BCR high nibble), and an optional CP56Time2a
/// timestamp. Normalised values arrive as the raw fraction in [-1, +1); scaled/counter values as their
/// integer magnitude; short-float values as the IEEE-754 quantity; single-point values as 0 or 1.</summary>
/// <param name="Ioa">Information Object Address (3-octet, little-endian on the wire).</param>
/// <param name="Value">The value in the type's native units (see the type table in <see cref="Iec104Asdu"/>).</param>
/// <param name="Quality">The quality octet (IV=b7, NT=b6, SB=b5, BL=b4, OV=b0); folded by <see cref="QualityFold.FromIec104"/>.</param>
/// <param name="Type">Whether this object is analogue, a counter, or a binary status.</param>
/// <param name="TimeUtc">The object's CP56Time2a time tag in UTC, or null for the untimed types.</param>
/// <param name="Transient">N337: the VTI transient bit of a step position (types 5 / 32): true while the equipment
/// (a tap changer) is moving. Always false for every other type.</param>
public sealed record Iec104InfoObject(int Ioa, double Value, byte Quality, RtuPointType Type, DateTime? TimeUtc, bool Transient = false);

/// <summary>
/// The IEC 60870-5 <b>ASDU object model</b> - the application layer that sits inside the raw ASDU bytes the
/// <see cref="Iec104Frame"/> (104/TCP) and IEC-101 (serial) framers hand up. It decodes the Data Unit
/// Identifier (type id, variable structure qualifier, cause of transmission, common address) and the
/// information objects for the common <b>monitor-direction</b> types. This is the piece the codec docstrings
/// deferred as "a follow-up": with it, dated ASDUs become <see cref="Interpose.RtuPoint"/>s and, via the
/// crosswalk, a horizon-guarded <see cref="Estimation.MeasurementSet"/>.
///
/// <para>Supported type ids: 1 M_SP_NA_1, 3 M_DP_NA_1, 9 M_ME_NA_1 (normalised), 11 M_ME_NB_1 (scaled),
/// 13 M_ME_NC_1 (short float), 15 M_IT_NA_1 (counter), and their time-tagged CP56Time2a siblings
/// 30 M_SP_TB_1, 31 M_DP_TB_1 (double point + CP56Time2a, the common timed breaker status; R089), 34 M_ME_TD_1,
/// 35 M_ME_TE_1, 36 M_ME_TF_1, 37 M_IT_TB_1, and (N337) 5 M_ST_NA_1 / 32 M_ST_TB_1 step position and 7 M_BO_NA_1 /
/// 33 M_BO_TB_1 bitstring of 32 bits. Unsupported / control types (commands, ...) decode to an empty object list;
/// <see cref="IsSupportedType"/> lets a pipeline count them rather than drop them unseen (the interposer's
/// <c>Iec104Protocol.UnsupportedTypeIds</c>).</para>
///
/// <para>N337 (October 2026, ruled by main; this reverses R089's "5 / 32 and 7 / 33 stay unsupported"): the four
/// status types are now decoded here, with exactly the element layouts of the explicit fail-loud codec
/// <see cref="Iec104StatusTypes"/> (VTI + QDS, BSI + QDS; the layout [VERIFY] items are listed there, the IEC
/// 60870-5-101 text is not on disk). A step position becomes a <see cref="RtuPointType.StepPosition"/> object whose
/// value is the signed step (-64..+63) and whose <see cref="Iec104InfoObject.Transient"/> carries the VTI T bit; a
/// bitstring becomes a <see cref="RtuPointType.Bitstring"/> object whose value is the 32-bit word as an unsigned
/// integer (exact in a double). Both carry the full QDS octet as their quality, as the other QDS types do. The R089
/// pins that counted them unsupported move at the end pass.</para>
/// </summary>
/// <param name="TypeId">Type identification octet.</param>
/// <param name="Cause">Cause of transmission (low 6 bits of the COT octet).</param>
/// <param name="Test">The COT test bit (T): a test ASDU, not live telemetry.</param>
/// <param name="Negative">The COT positive/negative bit (P/N).</param>
/// <param name="OriginatorAddress">Originator address octet (0 when unused).</param>
/// <param name="CommonAddress">Common Address of ASDU (2-octet, little-endian).</param>
/// <param name="Objects">The decoded information objects.</param>
public sealed record Iec104Asdu(
    byte TypeId, int Cause, bool Test, bool Negative, int OriginatorAddress, int CommonAddress,
    IReadOnlyList<Iec104InfoObject> Objects)
{
    /// <summary>Decode an ASDU from the bytes that follow the transport framing. The DUI is
    /// 6 octets in the 104/101 profile this build targets: type(1) + VSQ(1) + COT(2) + CASDU(2).</summary>
    public static Iec104Asdu Parse(ReadOnlySpan<byte> asdu)
    {
        if (asdu.Length < 6) throw new ArgumentException("IEC 60870-5 ASDU shorter than its 6-octet DUI.");

        byte typeId = asdu[0];
        byte vsq = asdu[1];
        bool sequence = (vsq & 0x80) != 0;      // SQ: single IOA then N consecutive elements
        int count = vsq & 0x7F;                 // number of objects (SQ=0) or elements (SQ=1)
        byte cot = asdu[2];
        int cause = cot & 0x3F;
        bool pn = (cot & 0x40) != 0;
        bool test = (cot & 0x80) != 0;
        int originator = asdu[3];
        int commonAddr = asdu[4] | (asdu[5] << 8);

        var objects = new List<Iec104InfoObject>(count);
        int p = 6;

        (int elemBytes, bool timed, RtuPointType kind) = Layout(typeId);
        bool stepPosition = typeId is Iec104StatusTypes.StepPosition or Iec104StatusTypes.StepPositionTimed;
        if (elemBytes == 0) // unsupported / control type: valid ASDU, no measurements
            return new Iec104Asdu(typeId, cause, test, pn, originator, commonAddr, objects);

        int timeBytes = timed ? 7 : 0;

        if (sequence)
        {
            if (p + 3 > asdu.Length) throw new ArgumentException("IEC 60870-5 SQ ASDU missing its base IOA.");
            int baseIoa = asdu[p] | (asdu[p + 1] << 8) | (asdu[p + 2] << 16);
            p += 3;
            // A sequence of N objects addresses baseIoa .. baseIoa + N - 1, which must stay inside the 24-bit IOA range.
            if (count > 0 && baseIoa + count - 1 > 0xFFFFFF)
                throw new ArgumentException($"IEC 60870-5 SQ ASDU: base IOA {baseIoa} with {count} objects runs past the 24-bit address range.");
            for (int i = 0; i < count; i++)
            {
                if (p + elemBytes + timeBytes > asdu.Length)
                    throw new ArgumentException("IEC 60870-5 SQ ASDU truncated mid-element.");
                (double value, byte q) = DecodeElement(typeId, asdu.Slice(p, elemBytes));
                DateTime? time = timed ? TimeDecode.Cp56Time2a(asdu.Slice(p + elemBytes, 7)) : null;
                objects.Add(new Iec104InfoObject(baseIoa + i, value, q, kind, time,
                    Transient: stepPosition && VtiTransient(asdu[p])));
                p += elemBytes + timeBytes;
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (p + 3 + elemBytes + timeBytes > asdu.Length)
                    throw new ArgumentException("IEC 60870-5 ASDU truncated mid-object.");
                int ioa = asdu[p] | (asdu[p + 1] << 8) | (asdu[p + 2] << 16);
                p += 3;
                (double value, byte q) = DecodeElement(typeId, asdu.Slice(p, elemBytes));
                DateTime? time = timed ? TimeDecode.Cp56Time2a(asdu.Slice(p + elemBytes, 7)) : null;
                objects.Add(new Iec104InfoObject(ioa, value, q, kind, time,
                    Transient: stepPosition && VtiTransient(asdu[p])));
                p += elemBytes + timeBytes;
            }
        }

        // The VSQ count must account for the whole ASDU. Bytes left over mean the count disagrees with the length (a
        // corrupt VSQ that said 1 for three objects delivered one and the other two vanished); refuse it, as the strict
        // codec does, so the pipeline counts it as a decode error.
        if (p != asdu.Length)
            throw new ArgumentException($"IEC 60870-5 ASDU: VSQ declares {count} object(s) ending at octet {p}, but the ASDU has {asdu.Length} octets.");

        return new Iec104Asdu(typeId, cause, test, pn, originator, commonAddr, objects);
    }

    /// <summary>True when <paramref name="typeId"/> is a monitor-direction type this decoder turns into information
    /// objects; false for unsupported and control types, which <see cref="Parse"/> returns with no objects.</summary>
    public static bool IsSupportedType(byte typeId) => Layout(typeId).elemBytes != 0;

    /// <summary>True when <paramref name="typeId"/> is a supported type whose objects carry a CP56Time2a tag.</summary>
    public static bool IsTimedType(byte typeId)
    {
        (int elemBytes, bool timed, _) = Layout(typeId);
        return elemBytes != 0 && timed;
    }

    /// <summary>Per-type element size (excluding the optional 7-octet CP56Time2a), whether it is timed,
    /// and what it observes. Element size 0 marks an unsupported / control type.</summary>
    private static (int elemBytes, bool timed, RtuPointType kind) Layout(byte typeId) => typeId switch
    {
        1 => (1, false, RtuPointType.BinaryStatus),   // M_SP_NA_1  single point (SIQ)
        3 => (1, false, RtuPointType.BinaryStatus),   // M_DP_NA_1  double point (DIQ)
        5 => (2, false, RtuPointType.StepPosition),   // M_ST_NA_1  step position (VTI) + QDS (N337)
        7 => (5, false, RtuPointType.Bitstring),      // M_BO_NA_1  bitstring of 32 bits (BSI) + QDS (N337)
        9 => (3, false, RtuPointType.Analog),         // M_ME_NA_1  normalised value + QDS
        11 => (3, false, RtuPointType.Analog),        // M_ME_NB_1  scaled value + QDS
        13 => (5, false, RtuPointType.Analog),        // M_ME_NC_1  short float + QDS
        15 => (5, false, RtuPointType.Counter),       // M_IT_NA_1  binary counter (BCR)
        30 => (1, true, RtuPointType.BinaryStatus),   // M_SP_TB_1  single point + CP56Time2a
        31 => (1, true, RtuPointType.BinaryStatus),   // M_DP_TB_1  double point (DIQ) + CP56Time2a (R089)
        32 => (2, true, RtuPointType.StepPosition),   // M_ST_TB_1  step position + QDS + CP56Time2a (N337)
        33 => (5, true, RtuPointType.Bitstring),      // M_BO_TB_1  bitstring + QDS + CP56Time2a (N337)
        34 => (3, true, RtuPointType.Analog),         // M_ME_TD_1  normalised + CP56Time2a
        35 => (3, true, RtuPointType.Analog),         // M_ME_TE_1  scaled + CP56Time2a
        36 => (5, true, RtuPointType.Analog),         // M_ME_TF_1  short float + CP56Time2a
        37 => (5, true, RtuPointType.Counter),        // M_IT_TB_1  counter + CP56Time2a
        _ => (0, false, RtuPointType.Analog),
    };

    /// <summary>Decode one information element (value + trailing quality octet) for the given type.</summary>
    private static (double value, byte quality) DecodeElement(byte typeId, ReadOnlySpan<byte> e) => typeId switch
    {
        1 or 30 => (e[0] & 0x01, (byte)(e[0] & 0xF0)),                         // SIQ: SPI state, quality high nibble
        3 or 31 => (DoublePoint(e[0]), (byte)(e[0] & 0xF0)),                   // DIQ: DPI in low 2 bits
        5 or 32 => (VtiValue(e[0]), e[1]),                                      // VTI step (I7) + QDS (N337)
        7 or 33 => (BinaryPrimitives.ReadUInt32LittleEndian(e[..4]), e[4]),     // BSI 32 bits, LS octet first + QDS (N337)
        9 or 34 => (BinaryPrimitives.ReadInt16LittleEndian(e) / 32768.0, e[2]),// normalised fraction + QDS
        11 or 35 => (BinaryPrimitives.ReadInt16LittleEndian(e), e[2]),          // scaled integer + QDS
        13 or 36 => (BinaryPrimitives.ReadSingleLittleEndian(e[..4]), e[4]),    // IEEE-754 short float + QDS
        // BCR counter. GR-813: besides IV (0x80), the carry bit CY (0x20: the counter overflowed in the period, so the
        // value has wrapped) maps to the QDS overflow bit OV (0x01), and the adjusted bit CA (0x40) to substituted
        // (SB, 0x20); both used to be dropped, so a wrapped counter read as a good value.
        15 or 37 => (BinaryPrimitives.ReadInt32LittleEndian(e[..4]),
            (byte)((e[4] & 0x80) | ((e[4] & 0x20) != 0 ? 0x01 : 0) | ((e[4] & 0x40) != 0 ? 0x20 : 0))),
        _ => (0.0, 0x80),                                                       // treat unknown as invalid
    };

    /// <summary>N337: the VTI value field, a 7-bit two's complement integer in bits 0 to 6 (-64..+63), exactly as
    /// <see cref="Iec104StatusTypes.DecodeStepPositions"/> reads it: 0x40 is the field's sign bit.</summary>
    private static double VtiValue(byte vti)
    {
        int raw = vti & 0x7F;
        return raw >= 0x40 ? raw - 0x80 : raw;
    }

    /// <summary>N337: the VTI transient bit (bit 7): the equipment is moving between steps.</summary>
    private static bool VtiTransient(byte vti) => (vti & 0x80) != 0;

    /// <summary>Map a double-point DPI (0 indeterminate-off, 1 off, 2 on, 3 indeterminate) to 0/1,
    /// with the indeterminate states surfaced as an out-of-band NaN the caller drops.</summary>
    private static double DoublePoint(byte diq) => (diq & 0x03) switch
    {
        1 => 0.0,   // OFF
        2 => 1.0,   // ON
        _ => double.NaN, // indeterminate
    };
}
