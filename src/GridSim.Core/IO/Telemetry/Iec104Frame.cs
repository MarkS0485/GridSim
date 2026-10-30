namespace GridSim.Core.IO.Telemetry;

/// <summary>The APCI frame format of an IEC 60870-5-104 APDU.</summary>
public enum Iec104Format
{
    /// <summary>Information transfer format (carries an ASDU with sequence numbers).</summary>
    I,
    /// <summary>Supervisory format (acknowledges received frames; receive sequence only).</summary>
    S,
    /// <summary>Unnumbered control format (TESTFR / STARTDT / STOPDT).</summary>
    U,
}

/// <summary>A decoded IEC 60870-5-104 APDU: its APCI (format + sequence numbers) and the raw ASDU payload.</summary>
/// <param name="Format">The frame format.</param>
/// <param name="SendSeq">Send sequence number N(S) (I-format only).</param>
/// <param name="RecvSeq">Receive sequence number N(R) (I- and S-format).</param>
/// <param name="Asdu">The application service data unit bytes (empty for S/U).</param>
/// <param name="UControl">For U-format, the full control octet 1 carrying the function bits
/// (STARTDT/STOPDT/TESTFR act/con); 0 for I/S. Preserved across Parse/ToBytes so a U-frame round-trips.</param>
public sealed record Iec104Apdu(Iec104Format Format, int SendSeq, int RecvSeq, byte[] Asdu, byte UControl = 0);

/// <summary>The IEC 60870-5-104 U-format control-octet-1 function values (function bits + the 0x03 U marker).</summary>
public static class Iec104UFunction
{
    public const byte StartDtAct = 0x07;
    public const byte StartDtCon = 0x0B;
    public const byte StopDtAct = 0x13;
    public const byte StopDtCon = 0x23;
    public const byte TestFrAct = 0x43;
    public const byte TestFrCon = 0x83;
}

/// <summary>
/// A minimal codec for the IEC 60870-5-104 APCI (the TCP framing telemetry SCADA arrives on). It decodes and
/// encodes the 6-byte control header - start byte 0x68, APDU length, and the four control octets that carry
/// the I/S/U format and the 15-bit send/receive sequence numbers - plus the raw ASDU payload. The ASDU object
/// model (type identifiers, information objects, CP56Time2a timestamps) is a follow-up; this is the transport
/// framing. Dated ASDUs pass the horizon guard when mapped to a <see cref="Estimation.MeasurementSet"/>.
/// </summary>
public static class Iec104Frame
{
    private const byte Start = 0x68;

    /// <summary>Decode one APDU from the start of <paramref name="frame"/>.</summary>
    public static Iec104Apdu Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6) throw new ArgumentException("IEC 104 APDU shorter than the 6-byte APCI.");
        if (frame[0] != Start) throw new ArgumentException($"IEC 104 start byte is 0x{frame[0]:X2}, expected 0x68.");
        int length = frame[1];                 // octets following the length byte
        // IEC 60870-5-104: the APDU length is 4..253 (an APDU is at most 255 octets).
        if (length < 4 || length > 253 || length + 2 > frame.Length) throw new ArgumentException("IEC 104 APDU length out of range.");

        byte c1 = frame[2], c2 = frame[3], c3 = frame[4], c4 = frame[5];
        Iec104Format format;
        int ns = 0, nr = 0;
        byte uControl = 0;
        if ((c1 & 0x01) == 0)                  // I-format: bit0 = 0
        {
            format = Iec104Format.I;
            ns = (c1 >> 1) | (c2 << 7);
            nr = (c3 >> 1) | (c4 << 7);
        }
        else if ((c1 & 0x03) == 0x01)          // S-format: bits1..0 = 01
        {
            format = Iec104Format.S;
            nr = (c3 >> 1) | (c4 << 7);
        }
        else                                   // U-format: bits1..0 = 11
        {
            format = Iec104Format.U;
            uControl = c1;                     // preserve STARTDT/STOPDT/TESTFR function bits
        }

        // An S- or U-format APDU is the 4-octet control field and nothing else: one that carries a payload is malformed,
        // and refusing it keeps a corrupted stream from being read as a supervisory frame plus stray data.
        if (format != Iec104Format.I && length != 4)
            throw new ArgumentException($"IEC 104 {format}-format APDU has length {length}; an S or U frame is exactly 4 octets of control.");
        byte[] asdu = frame.Slice(6, length + 2 - 6).ToArray();
        return new Iec104Apdu(format, ns, nr, asdu, uControl);
    }

    /// <summary>Encode an APDU to bytes.</summary>
    public static byte[] ToBytes(Iec104Apdu apdu)
    {
        int asduLen = apdu.Format == Iec104Format.I ? apdu.Asdu.Length : 0;
        // The APDU length field is a single octet capped at 253 (ASDU <= 249): a larger payload would
        // wrap the length byte (e.g. 252 -> 0) and emit a corrupt frame that Parse then rejects/mis-slices.
        if (4 + asduLen > 253)
            throw new ArgumentOutOfRangeException(nameof(apdu), $"IEC 104 ASDU is {asduLen} octets; the maximum is 249 (APDU length field caps at 253).");
        var b = new byte[6 + asduLen];
        b[0] = Start;
        b[1] = (byte)(4 + asduLen);
        switch (apdu.Format)
        {
            case Iec104Format.I:
                // GR-654: N(S) and N(R) are 15-bit (0..32767); a larger value used to be masked into the frame silently.
                if ((uint)apdu.SendSeq > 32767 || (uint)apdu.RecvSeq > 32767)
                    throw new ArgumentOutOfRangeException(nameof(apdu), $"IEC 104 sequence numbers must be 0..32767 (N(S) {apdu.SendSeq}, N(R) {apdu.RecvSeq}).");
                b[2] = (byte)((apdu.SendSeq << 1) & 0xFE);
                b[3] = (byte)((apdu.SendSeq >> 7) & 0xFF);
                b[4] = (byte)((apdu.RecvSeq << 1) & 0xFE);
                b[5] = (byte)((apdu.RecvSeq >> 7) & 0xFF);
                apdu.Asdu.CopyTo(b, 6);
                break;
            case Iec104Format.S:
                b[2] = 0x01;
                b[4] = (byte)((apdu.RecvSeq << 1) & 0xFE);
                b[5] = (byte)((apdu.RecvSeq >> 7) & 0xFF);
                break;
            default: // U - emit the preserved function bits (STARTDT/STOPDT/TESTFR), keeping the 0x03 U marker
                b[2] = (byte)(apdu.UControl | 0x03);
                break;
        }
        return b;
    }
}
