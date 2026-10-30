using System.Buffers.Binary;

namespace GridSim.Core.Synchrophasor;

/// <summary>Why a frame could not be trusted (never thrown in the resident loop - reported instead).</summary>
public enum FrameDecodeStatus
{
    /// <summary>Decoded and CRC-verified.</summary>
    Ok,
    /// <summary>The trailing CHK did not match the computed CRC-CCITT; treat the PMU as dropped this tick.</summary>
    CrcMismatch,
    /// <summary>Not a DATA frame (a CONFIG/HEADER/COMMAND frame reached the data decoder).</summary>
    NotDataFrame,
    /// <summary>The frame was too short for its declared contents.</summary>
    Malformed,
    /// <summary>GR-304: the DATA frame's header IDCODE is not the IDCODE of the parsed CONFIG frame it was decoded
    /// against, so it belongs to another stream and is not decoded (stream overload only).</summary>
    WrongStream,
}

/// <summary>One decoded PMU DATA frame: the timestamp, the STAT word, the phasors, and freq/RoCoF, all in
/// engineering units (R293: integer-format fields are scaled per IEEE C37.118.2-2011 before they get here).</summary>
/// <param name="Status">Decode outcome.</param>
/// <param name="TimeUtc">SOC + FRACSEC/TIME_BASE as UTC.</param>
/// <param name="Stat">The 16-bit STAT word (data-valid/sync flags).</param>
/// <param name="Phasors">Phasors in rectangular engineering units (volts or amperes). Integer-format phasors are
/// scaled by their PHUNIT conversion factor (count x factor x 10^-5); float phasors are already engineering units.
/// The angle is the PMU's own, referenced to the UTC-synchronised nominal-frequency cosine, NOT to any bus.</param>
/// <param name="FreqHz">Absolute frequency in hertz. Integer format: FNOM + count / 1000 (the field is the
/// deviation from nominal in millihertz); float format: the actual frequency as transmitted.</param>
/// <param name="RocofHzPerS">Rate of change of frequency in Hz/s. Integer format: count / 100 (the field is ROCOF
/// x 100); float format: the actual value as transmitted.</param>
public sealed record C37118DataFrame(
    FrameDecodeStatus Status,
    DateTime TimeUtc,
    ushort Stat,
    Phasor[] Phasors,
    double FreqHz,
    double RocofHzPerS);

/// <summary>One PMU's block of a stream DATA frame (N061, October 2026), in engineering units exactly as
/// <see cref="C37118DataFrame"/> carries a single-PMU frame's values.</summary>
/// <param name="IdCode">The PMU's IDCODE from the CONFIG frame (the block itself carries no id: blocks are in the
/// CONFIG frame's order).</param>
/// <param name="StationName">The PMU's station name from the CONFIG frame.</param>
/// <param name="Stat">This PMU's 16-bit STAT word.</param>
/// <param name="Phasors">Its phasors (see <see cref="C37118DataFrame.Phasors"/>).</param>
/// <param name="FreqHz">Its absolute frequency in hertz.</param>
/// <param name="RocofHzPerS">Its rate of change of frequency in Hz/s.</param>
public sealed record C37118PmuBlock(int IdCode, string StationName, ushort Stat, Phasor[] Phasors, double FreqHz, double RocofHzPerS);

/// <summary>A decoded DATA frame of a whole stream (N061): one timestamp and one block per PMU of the CONFIG frame.</summary>
/// <param name="Status">Decode outcome; the blocks are empty unless it is <see cref="FrameDecodeStatus.Ok"/>.</param>
/// <param name="TimeUtc">SOC + FRACSEC / TIME_BASE as UTC.</param>
/// <param name="StreamIdCode">The frame header's IDCODE (the stream or PDC id; 0 when the CRC failed).</param>
/// <param name="Blocks">The PMU blocks in CONFIG order.</param>
public sealed record C37118StreamDataFrame(FrameDecodeStatus Status, DateTime TimeUtc, int StreamIdCode, IReadOnlyList<C37118PmuBlock> Blocks);

/// <summary>
/// Decoder for IEEE C37.118.2 DATA frames (single PMU block, and since N061 a whole multi-PMU stream frame). It verifies the CRC-CCITT before trusting any field
/// and converts SOC+FRACSEC to UTC using the configuration TIME_BASE. The TIME_BASE, phasor count, FORMAT, FNOM and
/// PHUNIT it needs come from the stream's CONFIG-1/CONFIG-2 frame, which <see cref="C37118ConfigParser"/> parses.
///
/// <para><b>Integer formats need the CONFIG frame (R293).</b> IEEE C37.118.2-2011 section 6.3 (Table 5, the data
/// frame) and section 6.4 (Table 9, the CONFIG-1/2 fields) define the 16-bit integer encodings as SCALED
/// quantities: FREQ is the deviation from the nominal FNOM in millihertz (Hz = FNOM + count / 1000), DFREQ is
/// ROCOF x 100 (Hz/s = count / 100), and an integer phasor component is a count of PHUNIT x 10^-5 V or A
/// (value = count x PHUNIT x 10^-5; the polar angle is in 10^-4 rad). FNOM and PHUNIT live only in the CONFIG
/// frame, so integer data can only be decoded with the parsed <see cref="PmuConfig"/>: use
/// <see cref="DecodeData(ReadOnlySpan{byte}, long, PmuConfig)"/>. The older overload that takes only a FORMAT word
/// decodes FLOAT data (already in engineering units) and refuses, loudly, any format with an integer field to
/// scale. It used to return the raw counts (FreqHz 5001 for a 5001 mHz deviation, a phasor of 2300 counts as
/// 2300 V), which looked like plausible numbers and were wrong.</para>
///
/// <para>Analogue and digital channels follow FREQ/DFREQ in the frame and are not decoded; the configuration
/// overload includes their sizes in the frame-length check.</para>
///
/// <para>Multi-PMU DATA frames (one block per PMU, each with its own STAT, in CONFIG order) are decoded by
/// <see cref="DecodeData(ReadOnlySpan{byte}, C37118ConfigFrame)"/> (N061, October 2026); the two single-PMU
/// overloads read only the first block of such a frame, so a stream with more than one PMU must use that overload.
/// The FORMAT bit order follows IEEE C37.118.2 since R046 (1.95.030; see <see cref="C37118DataFormat"/>). The CLI
/// verb <c>pmu-ingest</c> (N061) reads a capture file through this decoder. The IEEE C37.118.2-2011 text is not in
/// docs/reference: the table numbers cited here are as earlier work recorded them [VERIFY].</para>
/// </summary>
public static class C37118Decoder
{
    /// <summary>Parse the common header from the start of a frame.</summary>
    public static C37118CommonHeader ReadHeader(ReadOnlySpan<byte> frame) => C37118CommonHeader.Parse(frame);

    // GR-781: true when the common header can be parsed (the 14 header bytes are there and byte 0 is the 0xAA SYNC).
    private static bool HeaderReadable(ReadOnlySpan<byte> frame) => frame.Length >= 14 && frame[0] == 0xAA;

    /// <summary>True if the trailing CHK matches the CRC-CCITT over the rest of the frame.</summary>
    public static bool VerifyCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        int size = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (size < 4 || size > frame.Length) return false;
        ushort expected = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(size - 2, 2));
        ushort actual = C37118Crc.Compute(frame[..(size - 2)]);
        return expected == actual;
    }

    /// <summary>
    /// Decode a single-PMU DATA frame whose FORMAT says every field is FLOAT (phasors and FREQ/DFREQ), so it needs
    /// no scale factor from the CONFIG frame. <paramref name="timeBase"/>, <paramref name="phasorCount"/> and
    /// <paramref name="format"/> come from the stream's CONFIG-2. Assumes zero analog and digital channels.
    /// <para>R293: a format with INTEGER phasors (when there are phasors) or INTEGER FREQ/DFREQ is refused with an
    /// <see cref="ArgumentException"/>: those fields are scaled by FNOM and PHUNIT (IEEE C37.118.2-2011 Table 5 and
    /// Table 9), which only the CONFIG frame carries. Use the <see cref="PmuConfig"/> overload for them.</para>
    /// </summary>
    public static C37118DataFrame DecodeData(
        ReadOnlySpan<byte> frame, long timeBase, int phasorCount, C37118DataFormat format)
    {
        bool integerPhasors = (format & C37118DataFormat.PhasorFloat) == 0 && phasorCount > 0;
        bool integerFreq = (format & C37118DataFormat.FreqFloat) == 0;
        if (integerPhasors || integerFreq)
        {
            string what = integerPhasors && integerFreq ? "16-bit integer phasors and 16-bit integer FREQ/DFREQ"
                : integerPhasors ? "16-bit integer phasors" : "16-bit integer FREQ/DFREQ";
            throw new ArgumentException(
                $"C37.118 FORMAT {format} has {what}. Integer fields are scaled quantities (IEEE C37.118.2-2011 " +
                "Table 5: FREQ = FNOM + count/1000 Hz, DFREQ = count/100 Hz/s, phasor = count x PHUNIT x 1e-5) and " +
                "FNOM / PHUNIT come only from the CONFIG frame. Refusing to return raw counts: decode with " +
                "DecodeData(frame, timeBase, PmuConfig) using the configuration parsed by C37118ConfigParser.",
                nameof(format));
        }
        RequireValidTimeBase(timeBase);   // D-DOC-111: TIME_BASE 0 cannot turn FRACSEC into a time
        return Decode(frame, timeBase, phasorCount, format, analogCount: 0, digitalCount: 0, pmu: null);
    }

    /// <summary>
    /// Decode a single-PMU DATA frame against that PMU's parsed CONFIG-2 block (<paramref name="pmu"/>, from
    /// <see cref="C37118ConfigParser"/>) and the stream's <paramref name="timeBase"/>. Every format is supported:
    /// integer fields are scaled per IEEE C37.118.2-2011 Table 5 / Table 9 (FREQ = FNOM + count/1000 Hz,
    /// DFREQ = count/100 Hz/s, phasor = count x PHUNIT x 10^-5 V or A, polar angle = count x 10^-4 rad); float
    /// fields are taken as transmitted (already engineering units). The frame-length check includes the PMU's
    /// analogue and digital channels. Throws if an integer-phasor configuration carries no complete PHUNIT table.
    /// </summary>
    public static C37118DataFrame DecodeData(ReadOnlySpan<byte> frame, long timeBase, PmuConfig pmu)
    {
        ArgumentNullException.ThrowIfNull(pmu);
        bool integerPhasors = (pmu.Format & C37118DataFormat.PhasorFloat) == 0 && pmu.PhasorCount > 0;
        // Fail loud before reading a byte: an integer-phasor stream without its unit table cannot be scaled.
        if (integerPhasors && (pmu.PhasorUnits is null || pmu.PhasorUnits.Count != pmu.PhasorCount))
        {
            string have = pmu.PhasorUnits is null ? "no PHUNIT table"
                : $"{pmu.PhasorUnits.Count} PHUNIT entries for {pmu.PhasorCount} phasors";
            throw new ArgumentException(
                $"PMU '{pmu.StationName}' (IDCODE {pmu.IdCode}) sends 16-bit integer phasors but its configuration " +
                $"has {have}; the counts cannot be converted to volts or amperes (IEEE C37.118.2-2011 Table 9, " +
                "PHUNIT).", nameof(pmu));
        }
        RequireValidTimeBase(timeBase);   // D-DOC-111: TIME_BASE 0 cannot turn FRACSEC into a time
        return Decode(frame, timeBase, pmu.PhasorCount, pmu.Format, pmu.AnalogCount, pmu.DigitalCount, pmu);
    }

    // The shared single-block decode. 'pmu' is null only on the float-only path, where no scale factor is needed (that
    // public overload has already refused every format that would need one).
    private static C37118DataFrame Decode(
        ReadOnlySpan<byte> frame, long timeBase, int phasorCount, C37118DataFormat format,
        int analogCount, int digitalCount, PmuConfig? pmu)
    {
        if (!VerifyCrc(frame))
            return new C37118DataFrame(FrameDecodeStatus.CrcMismatch, default, 0, Array.Empty<Phasor>(), 0, 0);
        // GR-781: the CRC does not cover the SYNC byte's meaning, so a CRC-valid frame with a wrong SYNC byte (or shorter
        // than the header) is reported Malformed here; the header parse would throw on it.
        if (!HeaderReadable(frame))
            return new C37118DataFrame(FrameDecodeStatus.Malformed, default, 0, Array.Empty<Phasor>(), 0, 0);

        C37118CommonHeader h = C37118CommonHeader.Parse(frame);
        if (h.FrameType != C37118FrameType.Data)
            return new C37118DataFrame(FrameDecodeStatus.NotDataFrame, default, 0, Array.Empty<Phasor>(), 0, 0);

        int off = 14;
        // header + STAT + phasors + FREQ/DFREQ + analogues + digital words + CHK
        int need = off + BlockSize(phasorCount, format, analogCount, digitalCount) + 2;
        // GR-831: the frame must be exactly the size this configuration describes, as on the stream overload. A longer
        // one (a multi-PMU frame, or extra channels) decoded its first block as Ok and dropped the rest silently.
        if (frame.Length < need || h.FrameSize != need)
            return new C37118DataFrame(FrameDecodeStatus.Malformed, default, 0, Array.Empty<Phasor>(), 0, 0);
        // D-DOC-111 companion: a FRACSEC of TIME_BASE or more is not a fraction of a second; the frame is malformed.
        if (!FracSecInRange(h, timeBase))
            return new C37118DataFrame(FrameDecodeStatus.Malformed, default, 0, Array.Empty<Phasor>(), 0, 0);

        (ushort stat, Phasor[] phasors, double freq, double dfreq) = ReadBlock(frame, ref off, phasorCount, format, analogCount, digitalCount, pmu);
        return new C37118DataFrame(FrameDecodeStatus.Ok, FrameTime(h, timeBase), stat, phasors, freq, dfreq);
    }

    /// <summary>The largest TIME_BASE the CONFIG frame can carry: the field is 32 bits, of which the low 24 are the
    /// count of FRACSEC units per second (IEEE C37.118.2-2011 Table 9, TIME_BASE [VERIFY]; the top byte is reserved).</summary>
    public const long MaxTimeBase = 0x00FF_FFFF;

    /// <summary>
    /// Refuse a TIME_BASE that cannot describe a fraction of a second (D-DOC-111, October 2026). TIME_BASE is the number
    /// of FRACSEC counts in one second, so it must be at least 1; the field holds 24 bits, so it is at most
    /// <see cref="MaxTimeBase"/>. Before, TIME_BASE 0 was not refused: a FRACSEC of 0 decoded silently (0 / 0 is NaN,
    /// which DateTime.AddSeconds turned into no time at all) and any other FRACSEC gave a raw "Value to add was out of
    /// range" from DateTime.AddSeconds (x / 0 is infinity).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The TIME_BASE is 0, negative, or wider than 24 bits; the message
    /// names TIME_BASE and its value.</exception>
    public static void RequireValidTimeBase(long timeBase, string paramName = "timeBase")
    {
        if (timeBase < 1 || timeBase > MaxTimeBase)
            throw new ArgumentOutOfRangeException(paramName, timeBase,
                $"C37.118 TIME_BASE is {timeBase}; it is the number of FRACSEC counts per second, so it must be between 1 and " +
                $"{MaxTimeBase} (a 24-bit field). A TIME_BASE of 0 cannot turn FRACSEC into a time.");
    }

    // SOC + FRACSEC / TIME_BASE as UTC (the same rule for every overload), computed EXACTLY in integer 100 ns ticks
    // (D-DOC-110, October 2026).
    //
    // ticks = SOC x 10^7 + floor(FRACSEC x 10^7 / TIME_BASE)
    //
    // Both products are exact in a 64-bit integer: SOC is below 2^32, so SOC x 10^7 is below 4.3e16; FRACSEC is below 2^24
    // (and, checked by the caller, below TIME_BASE), so FRACSEC x 10^7 is below 1.7e14. Adding the result to the Unix epoch
    // (6.2e17 ticks) stays far below DateTime.MaxValue (3.2e18 ticks). The only rounding is the one floor, and it bites
    // only when TIME_BASE does not divide FRACSEC x 10^7 (a time finer than one 100 ns tick, which DateTime cannot hold):
    // the instant is then the last whole tick at or before the true time, the rule DateTime.AddSeconds itself applies.
    //
    // Before, the fraction went through a double: AddSeconds(FRACSEC / (double)TIME_BASE). The quotient is rounded to the
    // nearest double, and DateTime.AddSeconds then TRUNCATES the fraction to whole ticks, so whenever the rounded quotient
    // fell just below the exact value the instant landed one tick (100 ns) short. At TIME_BASE 1,000,000: FRACSEC 820,000
    // (the 42nd frame of a second at 50 frames/s) gave .8199999 s instead of .82 s; 4 of the 100 FRACSEC values at
    // 100 frames/s, 59 of 1,000 at 1,000 frames/s, and 54,601 of the 1,000,000 possible FRACSEC values were one tick short.
    // The phasor data concentrator keys its buckets on Ticks, so two PMUs reporting the same instant through different
    // arithmetic (or a capture compared with its own result file) could land a tick apart. Every other instant is
    // unchanged: the exact integer rule and the old one agree whenever the double did not fall short.
    private static DateTime FrameTime(C37118CommonHeader h, long timeBase)
    {
        long fractionTicks = (long)h.FracSecRaw * TimeSpan.TicksPerSecond / timeBase;   // integer division: the floor
        long ticks = (long)h.Soc * TimeSpan.TicksPerSecond + fractionTicks;
        return DateTime.UnixEpoch.AddTicks(ticks);
    }

    // True when FRACSEC names a fraction of a second, i.e. its count is below TIME_BASE (D-DOC-111 companion, October
    // 2026). FRACSEC is "the fraction of the second" in units of 1 / TIME_BASE, so a count of TIME_BASE or more is a
    // second or more: the frame contradicts its own configuration. Before, it was added as is (FRACSEC 1,500,000 at
    // TIME_BASE 1,000,000 put the frame 1.5 s after its SOC, the same instant as the next second's frame at .5 s). Such a
    // frame is now Malformed, counted and not used, like a frame whose length does not match the configuration.
    private static bool FracSecInRange(C37118CommonHeader h, long timeBase) => h.FracSecRaw < timeBase;

    // The bytes of one PMU block of a DATA frame: STAT, phasors, FREQ + DFREQ, analogue values, digital words.
    private static int BlockSize(int phasorCount, C37118DataFormat format, int analogCount, int digitalCount)
    {
        int phasorSize = (format & C37118DataFormat.PhasorFloat) != 0 ? 8 : 4;
        int freqSize = (format & C37118DataFormat.FreqFloat) != 0 ? 8 : 4;       // FREQ and DFREQ together
        int analogSize = (format & C37118DataFormat.AnalogFloat) != 0 ? 4 : 2;   // one analogue value
        return 2 + phasorCount * phasorSize + freqSize + analogCount * analogSize + 2 * digitalCount;
    }

    // Read one PMU block starting at 'off' (the caller has checked the frame is long enough), advancing 'off' past it,
    // analogue and digital channels included (they are sized, not decoded).
    private static (ushort Stat, Phasor[] Phasors, double Freq, double Dfreq) ReadBlock(
        ReadOnlySpan<byte> frame, ref int off, int phasorCount, C37118DataFormat format,
        int analogCount, int digitalCount, PmuConfig? pmu)
    {
        bool phasorFloat = (format & C37118DataFormat.PhasorFloat) != 0;
        bool phasorPolar = (format & C37118DataFormat.PhasorPolar) != 0;
        bool freqFloat = (format & C37118DataFormat.FreqFloat) != 0;
        bool analogFloat = (format & C37118DataFormat.AnalogFloat) != 0;
        int phasorSize = phasorFloat ? 8 : 4;
        int analogSize = analogFloat ? 4 : 2;       // one analogue value

        ushort stat = BinaryPrimitives.ReadUInt16BigEndian(frame[off..]); off += 2;

        var phasors = new Phasor[phasorCount];
        for (int i = 0; i < phasorCount; i++)
        {
            double a, b;
            // C37.118.2 fills a channel with no data with 0x8000 in a 16-bit integer field (NaN in a float one). It used
            // to be read as -32768 x scale volts. A missing integer channel becomes a NaN phasor, as a float one already
            // is, and the mapper leaves NaN channels out. For integer polar data the marker is in the angle (0x8000 is
            // -32768 x 1e-4 rad, outside +/- pi, so it cannot be a real angle); for rectangular data, in either part.
            bool missing = false;
            if (phasorFloat)
            {
                // Float (Table 5): already engineering units (real / imag, or magnitude / angle in radians).
                a = BinaryPrimitives.ReadSingleBigEndian(frame[off..]);
                b = BinaryPrimitives.ReadSingleBigEndian(frame[(off + 4)..]);
            }
            else
            {
                // Integer (Table 5): counts, scaled by this channel's PHUNIT (Table 9): 10^-5 V or A per bit.
                // 'pmu' is never null here: the float-only overload refuses integer phasors before calling Decode.
                double scale = pmu!.PhasorScale(i);
                if (phasorPolar)
                {
                    // IEEE C37.118 integer polar: magnitude is UNSIGNED 16-bit counts; angle is a SIGNED 16-bit
                    // value in units of 1e-4 radian. Reading the magnitude as signed made counts > 32767 go
                    // negative, and passing the angle raw made a 45 deg (7854 unit) angle arrive as 7854 rad.
                    a = BinaryPrimitives.ReadUInt16BigEndian(frame[off..]) * scale;
                    b = BinaryPrimitives.ReadInt16BigEndian(frame[(off + 2)..]) * 1e-4;
                    missing = BinaryPrimitives.ReadInt16BigEndian(frame[(off + 2)..]) == short.MinValue;
                }
                else
                {
                    // Integer rectangular: real and imaginary are SIGNED 16-bit counts.
                    a = BinaryPrimitives.ReadInt16BigEndian(frame[off..]) * scale;
                    b = BinaryPrimitives.ReadInt16BigEndian(frame[(off + 2)..]) * scale;
                    missing = BinaryPrimitives.ReadInt16BigEndian(frame[off..]) == short.MinValue
                              || BinaryPrimitives.ReadInt16BigEndian(frame[(off + 2)..]) == short.MinValue;
                }
            }
            off += phasorSize;
            if (missing || !double.IsFinite(a) || !double.IsFinite(b)) phasors[i] = new Phasor(double.NaN, double.NaN);
            else phasors[i] = phasorPolar ? Phasor.FromPolar(a, b) : new Phasor(a, b);
        }

        double freq, dfreq;
        if (freqFloat)
        {
            // Float (Table 5): FREQ is the actual frequency in Hz, DFREQ the actual ROCOF in Hz/s.
            freq = BinaryPrimitives.ReadSingleBigEndian(frame[off..]);
            dfreq = BinaryPrimitives.ReadSingleBigEndian(frame[(off + 4)..]);
            off += 8;
        }
        else
        {
            // Integer (Table 5): FREQ = deviation from FNOM in millihertz, DFREQ = ROCOF x 100 (R293). 'pmu' is
            // never null here: the float-only overload refuses an integer FREQ format before calling Decode.
            freq = pmu!.NominalHz + BinaryPrimitives.ReadInt16BigEndian(frame[off..]) / 1000.0;
            dfreq = BinaryPrimitives.ReadInt16BigEndian(frame[(off + 2)..]) / 100.0;
            off += 4;
        }

        // Analogue values and digital words are not decoded; step over them so the next block starts in the right place.
        off += analogCount * analogSize + 2 * digitalCount;
        return (stat, phasors, freq, dfreq);
    }

    /// <summary>
    /// Decode a DATA frame of a whole stream (N061, October 2026): one PMU block per PMU of the stream's CONFIG frame,
    /// in the CONFIG frame's order, each with its own STAT, phasors and FREQ / DFREQ, as a phasor data concentrator
    /// sends them. Each block is decoded exactly as <see cref="DecodeData(ReadOnlySpan{byte}, long, PmuConfig)"/>
    /// decodes a single-PMU frame against that PMU's configuration (so a one-PMU stream gives the same numbers). The
    /// frame must be exactly as long as the configuration says (header, every block, CHK); otherwise it is
    /// <see cref="FrameDecodeStatus.Malformed"/>, never half-read. Throws, before reading a byte, when a PMU sends
    /// integer phasors and its configuration has no complete PHUNIT table (as the single-PMU overload does).
    /// Before N061 only the first block of such a frame was read and the others were silently ignored.
    /// </summary>
    public static C37118StreamDataFrame DecodeData(ReadOnlySpan<byte> frame, C37118ConfigFrame config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Pmus.Count == 0)
            throw new ArgumentException("the CONFIG frame lists no PMU, so a DATA frame cannot be decoded against it.", nameof(config));
        RequireValidTimeBase(config.TimeBase, nameof(config));   // D-DOC-111 (a hand-built configuration can still hold 0)
        foreach (PmuConfig pmu in config.Pmus)
        {
            bool integerPhasors = (pmu.Format & C37118DataFormat.PhasorFloat) == 0 && pmu.PhasorCount > 0;
            if (integerPhasors && (pmu.PhasorUnits is null || pmu.PhasorUnits.Count != pmu.PhasorCount))
                throw new ArgumentException(
                    $"PMU '{pmu.StationName}' (IDCODE {pmu.IdCode}) sends 16-bit integer phasors but its configuration " +
                    "has no complete PHUNIT table; the counts cannot be converted to volts or amperes.", nameof(config));
        }

        if (!VerifyCrc(frame))
            return new C37118StreamDataFrame(FrameDecodeStatus.CrcMismatch, default, 0, Array.Empty<C37118PmuBlock>());
        if (!HeaderReadable(frame))   // GR-781, as on the single-PMU path
            return new C37118StreamDataFrame(FrameDecodeStatus.Malformed, default, 0, Array.Empty<C37118PmuBlock>());
        C37118CommonHeader h = C37118CommonHeader.Parse(frame);
        if (h.FrameType != C37118FrameType.Data)
            return new C37118StreamDataFrame(FrameDecodeStatus.NotDataFrame, default, h.IdCode, Array.Empty<C37118PmuBlock>());
        // GR-304: a parsed configuration knows its stream's IDCODE. A DATA frame from any other stream is refused before
        // its size is even compared: two streams whose frames happen to be the same length used to decode one with the
        // other's configuration (other phasors, other scale factors) and report the result as Ok.
        if (config.StreamIdCode is int streamId && h.IdCode != streamId)
            return new C37118StreamDataFrame(FrameDecodeStatus.WrongStream, default, h.IdCode, Array.Empty<C37118PmuBlock>());

        int need = 14 + 2;
        foreach (PmuConfig pmu in config.Pmus) need += BlockSize(pmu.PhasorCount, pmu.Format, pmu.AnalogCount, pmu.DigitalCount);
        // A multi-PMU frame of the wrong length means the configuration does not describe it: refuse it whole.
        if (frame.Length < need || h.FrameSize != need)
            return new C37118StreamDataFrame(FrameDecodeStatus.Malformed, default, h.IdCode, Array.Empty<C37118PmuBlock>());
        // D-DOC-111 companion: a FRACSEC of TIME_BASE or more is not a fraction of a second; the frame is malformed.
        if (!FracSecInRange(h, config.TimeBase))
            return new C37118StreamDataFrame(FrameDecodeStatus.Malformed, default, h.IdCode, Array.Empty<C37118PmuBlock>());

        int off = 14;
        var blocks = new C37118PmuBlock[config.Pmus.Count];
        for (int k = 0; k < config.Pmus.Count; k++)
        {
            PmuConfig pmu = config.Pmus[k];
            (ushort stat, Phasor[] phasors, double freq, double dfreq) =
                ReadBlock(frame, ref off, pmu.PhasorCount, pmu.Format, pmu.AnalogCount, pmu.DigitalCount, pmu);
            blocks[k] = new C37118PmuBlock(pmu.IdCode, pmu.StationName, stat, phasors, freq, dfreq);
        }
        return new C37118StreamDataFrame(FrameDecodeStatus.Ok, FrameTime(h, config.TimeBase), h.IdCode, blocks);
    }
}
