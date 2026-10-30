using System.Buffers.Binary;
using System.Text;

namespace GridSim.Core.Synchrophasor;

/// <summary>One PMU's configuration from a CONFIG-2 frame - the context the DATA decoder needs.</summary>
/// <param name="StationName">Station name (trimmed).</param>
/// <param name="IdCode">This PMU's data-source id.</param>
/// <param name="Format">The FORMAT word (phasor/analog/freq float, polar).</param>
/// <param name="PhasorCount">Number of phasors per data frame.</param>
/// <param name="AnalogCount">Number of analog values.</param>
/// <param name="DigitalCount">Number of digital status words.</param>
/// <param name="Is50Hz">Nominal line frequency 50 Hz (else 60 Hz).</param>
/// <param name="ConfigCount">Configuration change count.</param>
/// <param name="PhasorUnits">The raw 32-bit PHUNIT word of each phasor channel, in channel order (IEEE
/// C37.118.2-2011 Table 9, CONFIG-1/2 field PHUNIT): the most significant byte is the channel type (0 = voltage,
/// 1 = current) and the low 24 bits are an unsigned conversion factor in units of 10^-5 V or A per bit, used to
/// scale 16-bit INTEGER phasor data (ignored when the PMU sends float phasors). Null only for a configuration built
/// by hand without the unit table; the integer-format decode refuses such a configuration rather than guess.</param>
public sealed record PmuConfig(
    string StationName, int IdCode, C37118DataFormat Format,
    int PhasorCount, int AnalogCount, int DigitalCount, bool Is50Hz, int ConfigCount,
    IReadOnlyList<uint>? PhasorUnits = null)
{
    /// <summary>The nominal line frequency FNOM in hertz: 50 when FNOM bit 0 is set, else 60 (IEEE C37.118.2-2011
    /// Table 9, field FNOM). The integer FREQ field is a deviation from this value.</summary>
    public double NominalHz => Is50Hz ? 50.0 : 60.0;

    /// <summary>
    /// The engineering-unit size of one count of integer phasor channel <paramref name="index"/>, in volts or
    /// amperes per bit: the low 24 bits of its PHUNIT word times 10^-5 (IEEE C37.118.2-2011 Table 9, PHUNIT:
    /// "an unsigned 24-bit word in 10^-5 V or amperes per bit to scale 16-bit integer data"). Computed as
    /// factor / 100000 (one correctly rounded division; 100000 is exact in binary floating point).
    /// Throws when the configuration carries no PHUNIT table or the index is out of range: an unscaled integer
    /// phasor is a raw count, not a voltage, and must never reach the estimator as if it were one.
    /// </summary>
    public double PhasorScale(int index)
    {
        if (PhasorUnits is null)
            throw new InvalidOperationException(
                $"PMU '{StationName}' (IDCODE {IdCode}): the configuration has no PHUNIT table, so integer phasor " +
                "counts cannot be converted to volts or amperes. Parse the CONFIG-2 frame (C37118ConfigParser) " +
                "rather than building the configuration by hand.");
        if (index < 0 || index >= PhasorUnits.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"PMU '{StationName}' (IDCODE {IdCode}) has {PhasorUnits.Count} PHUNIT entries; phasor {index} has none.");
        uint factor = PhasorUnits[index] & 0x00FF_FFFFu;   // the low 24 bits: 10^-5 V or A per bit
        return factor / 100_000.0;
    }

    /// <summary>True when phasor channel <paramref name="index"/> is a CURRENT (PHUNIT most significant byte = 1),
    /// false for a voltage (0). IEEE C37.118.2-2011 Table 9, PHUNIT. Throws when there is no PHUNIT table.</summary>
    public bool PhasorIsCurrent(int index)
    {
        _ = PhasorScale(index);   // the same presence and range checks, with the same loud messages
        // GR-807: the channel type is 0 (voltage) or 1 (current); any other value is not a phasor type and used to be
        // read as a voltage.
        uint type = PhasorUnits![index] >> 24;
        return type switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException(
                $"PMU '{StationName}' phasor channel {index} has PHUNIT type {type}; IEEE C37.118.2 defines 0 (voltage) and 1 (current) only."),
        };
    }
}

/// <summary>A parsed CONFIG-2 frame: the TIME_BASE and per-PMU configuration a data stream reports against.</summary>
/// <param name="TimeBase">FRACSEC resolution (counts per second).</param>
/// <param name="DataRate">Frames/second when positive; seconds/frame when negative.</param>
/// <param name="Pmus">Per-PMU configuration blocks.</param>
/// <param name="StreamIdCode">GR-304: the IDCODE of the CONFIG frame's own header (the data stream or PDC id), set by
/// <see cref="C37118ConfigParser"/>. The stream DATA decoder refuses a DATA frame whose header IDCODE differs from it
/// (<see cref="FrameDecodeStatus.WrongStream"/>), so a capture carrying two streams can never decode one stream's frames
/// with the other's configuration. Null for a configuration built by hand, which is then not checked.</param>
/// <param name="IsConfig1">GR-826: true when the frame was a CONFIG-1 (the PMU's capability) rather than a CONFIG-2 (what
/// the stream actually sends). pmu-ingest never lets a CONFIG-1 replace a CONFIG-2 already in force.</param>
public sealed record C37118ConfigFrame(
    long TimeBase, int DataRate, IReadOnlyList<PmuConfig> Pmus, int? StreamIdCode = null, bool IsConfig1 = false);

/// <summary>
/// A C37.118 frame that cannot be trusted or read: its declared size does not fit the bytes supplied, its CRC-CCITT
/// check word does not match, or a field the frame declares (a PMU block, a channel-name or unit table, DATA_RATE)
/// runs past the end of the frame. Derives from <see cref="ArgumentException"/> so every caller that already catches
/// bad-input exceptions (pmu-ingest among them) keeps working; the message always names what is wrong and where.
/// Added for N163: before it, a truncated CONFIG frame surfaced as a raw <see cref="ArgumentOutOfRangeException"/>
/// from span slicing, and a CONFIG frame with a wrong CRC was parsed as if it were good.
/// </summary>
public sealed class C37118FrameException : ArgumentException
{
    /// <summary>Create the exception with a message naming the defect.</summary>
    public C37118FrameException(string message) : base(message) { }
}

/// <summary>
/// Parser for the IEEE C37.118.2 CONFIG-2 frame - the stateful context (TIME_BASE, per-PMU FORMAT, phasor/
/// analog/digital counts) the DATA decoder needs to size and interpret data frames. It reads the fields the
/// decoder consumes and skips the channel-name / conversion-factor tables by their declared sizes.
/// </summary>
/// <remarks>
/// Fail loud (N163). Before any field is trusted the parser checks, in this order: the 14-byte common header, that the
/// declared FRAMESIZE is at least the smallest possible CONFIG frame and fits in the bytes supplied, and that the
/// trailing CHK equals the CRC-CCITT over the rest of the frame (the same check <see cref="C37118Decoder.VerifyCrc"/>
/// makes for DATA frames). Every later read is bounds-checked against the declared frame (CHK excluded), so a frame
/// whose PMU blocks or tables run past its end is refused with a <see cref="C37118FrameException"/> naming the field
/// and the PMU, never a raw <see cref="ArgumentOutOfRangeException"/> from span slicing. Bytes left over between
/// DATA_RATE and CHK are not refused (they were accepted before N163 and the CRC vouches for them as sent; see the
/// N163 notes). A good frame parses exactly as before: same fields, same values.
/// </remarks>
public static class C37118ConfigParser
{
    // The smallest CONFIG-1/2 frame with no PMU: 14-byte header + TIME_BASE (4) + NUM_PMU (2) + DATA_RATE (2) + CHK (2).
    private const int MinimumConfigFrameBytes = 14 + 4 + 2 + 2 + 2;

    /// <summary>Parse a CONFIG-1 or CONFIG-2 frame (same layout for these fields).</summary>
    /// <exception cref="ArgumentException">The header is short or its SYNC byte is wrong, or the frame is not a
    /// CONFIG-1 / CONFIG-2 frame.</exception>
    /// <exception cref="C37118FrameException">The declared frame size does not fit the bytes supplied, the CRC does
    /// not match, or a declared field runs past the end of the frame.</exception>
    public static C37118ConfigFrame Parse(ReadOnlySpan<byte> frame)
    {
        C37118CommonHeader h = C37118CommonHeader.Parse(frame);
        if (h.FrameType is not (C37118FrameType.Config1 or C37118FrameType.Config2))
            throw new ArgumentException($"not a CONFIG frame (type {h.FrameType}).");
        // GR-826: the low nibble of SYNC byte 1 is the protocol version (1 = C37.118-2005, 2 = C37.118.2-2011). Version 0
        // is defined by no edition, so such a frame is not a C37.118 CONFIG frame at all and is refused rather than read as
        // if it were one. Later version numbers are accepted, because the fields read here have the same layout in them.
        if (h.Version == 0)
            throw new C37118FrameException(
                "C37.118 CONFIG frame declares protocol version 0 (SYNC byte 1, low nibble); version 1 (2005) and 2 (2011) are " +
                "the defined editions, and no edition defines version 0.");

        // Size: the header's FRAMESIZE is the whole frame, header to CHK inclusive. It must hold at least the fixed
        // fields and must not claim more bytes than were supplied (a truncated capture or a short read).
        int size = h.FrameSize;
        if (size < MinimumConfigFrameBytes)
            throw new C37118FrameException(
                $"C37.118 CONFIG frame declares FRAMESIZE {size} bytes; a CONFIG frame is at least {MinimumConfigFrameBytes} " +
                "(header, TIME_BASE, NUM_PMU, DATA_RATE, CHK).");
        if (size > frame.Length)
            throw new C37118FrameException(
                $"C37.118 CONFIG frame is truncated: it declares FRAMESIZE {size} bytes but only {frame.Length} were supplied.");

        // CRC: the trailing CHK must match the CRC-CCITT over every byte before it. A configuration sizes and scales
        // every later DATA frame, so a corrupt one must never be parsed as if it were good.
        ushort expected = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(size - 2, 2));
        ushort actual = C37118Crc.Compute(frame[..(size - 2)]);
        if (expected != actual)
            throw new C37118FrameException(
                $"C37.118 CONFIG frame failed its CRC check: CHK is 0x{expected:X4} but the CRC-CCITT of the frame is 0x{actual:X4}.");

        // From here on read only inside the declared frame, CHK excluded: 'body' ends where CHK starts.
        ReadOnlySpan<byte> body = frame[..(size - 2)];

        int off = 14;
        long timeBase = BinaryPrimitives.ReadUInt32BigEndian(body[off..]) & 0x00FF_FFFF; off += 4;
        // D-DOC-111 (October 2026): TIME_BASE is the number of FRACSEC counts per second, so 0 cannot turn any FRACSEC into
        // a time. Before, it was accepted: every later DATA frame then decoded with FRACSEC 0 silently, or failed with a raw
        // "Value to add was out of range" for any other FRACSEC. Refused here, naming the field, so a stream with such a
        // configuration is refused at its CONFIG frame rather than frame by frame. (The 24-bit mask above already keeps
        // the value at or below C37118Decoder.MaxTimeBase.)
        if (timeBase == 0)
            throw new C37118FrameException(
                "C37.118 CONFIG frame declares TIME_BASE 0 (bytes 14-17, low 24 bits); TIME_BASE is the number of FRACSEC " +
                "counts per second and must be at least 1, so no DATA frame of this configuration could be given a time.");
        int numPmu = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;

        var pmus = new List<PmuConfig>(Math.Min(numPmu, 1024));   // capacity capped: numPmu is untrusted until read
        for (int k = 0; k < numPmu; k++)
        {
            // The fixed part of the PMU block: STN (16), IDCODE, FORMAT, PHNMR, ANNMR, DGNMR (2 each).
            Need(body, off, 16 + 2 + 2 + 2 + 2 + 2, k, numPmu, "fixed fields (STN, IDCODE, FORMAT, PHNMR, ANNMR, DGNMR)");
            string stn = Encoding.ASCII.GetString(body.Slice(off, 16)).TrimEnd(); off += 16;
            int idCode = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;
            var format = (C37118DataFormat)(BinaryPrimitives.ReadUInt16BigEndian(body[off..]) & 0x0F); off += 2;
            int phnmr = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;
            int annmr = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;
            int dgnmr = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;

            // The variable part: CHNAM (16 bytes per phasor and analogue name, 16 names of 16 bytes per digital word),
            // PHUNIT / ANUNIT / DIGUNIT (4 bytes each), then FNOM and CFGCNT (2 each). The largest possible value
            // (16 * (65535 + 65535 + 16 * 65535) + 4 * 3 * 65535 + 4, about 19.7 million) fits an int, so the sum
            // cannot overflow. Checked whole before reading, so a short frame never allocates the PHUNIT table.
            int chnam = 16 * (phnmr + annmr + 16 * dgnmr);
            Need(body, off, chnam + 4 * (phnmr + annmr + dgnmr) + 2 + 2, k, numPmu,
                $"channel names, unit tables, FNOM and CFGCNT for {phnmr} phasor(s), {annmr} analogue(s), {dgnmr} digital word(s)");

            off += chnam; // CHNAM

            // PHUNIT (IEEE C37.118.2-2011 Table 9): one 4-byte word per phasor, kept verbatim (channel type in
            // the top byte, 10^-5 V or A per bit in the low 24 bits). The DATA decoder needs it to scale 16-bit
            // integer phasors into volts and amperes (R293); it used to be skipped, so integer phasors arrived
            // as raw counts.
            var phunit = new uint[phnmr];
            for (int i = 0; i < phnmr; i++) { phunit[i] = BinaryPrimitives.ReadUInt32BigEndian(body[off..]); off += 4; }
            off += 4 * annmr;                          // ANUNIT (analogue channels are not decoded; see C37118Decoder)
            off += 4 * dgnmr;                          // DIGUNIT

            // FNOM (Table 9): bit 0 set = 50 Hz nominal, clear = 60 Hz. The integer FREQ field is a deviation
            // from this value, so the decoder needs it too (R293).
            int fnom = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;
            int cfgCnt = BinaryPrimitives.ReadUInt16BigEndian(body[off..]); off += 2;
            pmus.Add(new PmuConfig(stn, idCode, format, phnmr, annmr, dgnmr, (fnom & 1) != 0, cfgCnt, phunit));
        }

        if (body.Length - off < 2)
            throw new C37118FrameException(
                $"C37.118 CONFIG frame is truncated: DATA_RATE (2 bytes) is due at byte {off} but the frame ends (CHK starts) " +
                $"at byte {body.Length}, after {numPmu} PMU block(s).");
        int dataRate = BinaryPrimitives.ReadInt16BigEndian(body[off..]);
        return new C37118ConfigFrame(timeBase, dataRate, pmus, h.IdCode, h.FrameType == C37118FrameType.Config1);
    }

    // Refuse, naming the field and the PMU, when 'count' bytes from 'off' would run past the end of the frame body
    // (the frame without its CHK). Written as a subtraction so it cannot overflow.
    private static void Need(ReadOnlySpan<byte> body, int off, int count, int pmuIndex, int numPmu, string what)
    {
        if (count > body.Length - off)
            throw new C37118FrameException(
                $"C37.118 CONFIG frame is truncated: PMU block {pmuIndex + 1} of {numPmu} needs {count} more byte(s) for its " +
                $"{what} at byte {off}, but the frame ends (CHK starts) at byte {body.Length}.");
    }
}
