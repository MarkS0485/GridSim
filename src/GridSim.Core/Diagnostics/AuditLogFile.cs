using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// An externally kept note of how long a log was and what its last hash was at some moment (for example the
/// values printed when a run finished, or stored somewhere the log's writer cannot reach). The hash chain on
/// its own cannot tell a log that lost its last few entries from a log that never had them, and it cannot
/// tell a log rewritten end to end (every hash recomputed) from the original. Checking the file against an
/// anchor closes both gaps for every entry the anchor covers.
/// </summary>
/// <param name="Count">How many entries the log held when the anchor was taken (0 for an empty log).</param>
/// <param name="HeadHash">The hash of the last of those entries, or <see cref="AuditLog.Genesis"/> when
/// <paramref name="Count"/> is 0.</param>
public sealed record AuditAnchor(int Count, string HeadHash);

/// <summary>
/// The outcome of verifying an audit log file. A valid result means every line parsed, every entry sits at
/// its own sequence number, every entry links to the hash of the one before it, every stored hash equals the
/// hash recomputed from the entry's fields, and (when an anchor was given) the anchored entries are all
/// present and unchanged. An invalid result names the FIRST broken entry and why; nothing after it can be
/// trusted, because the chain is broken from that point on.
/// </summary>
/// <param name="IsValid">True only when the whole file verified.</param>
/// <param name="VerifiedCount">How many entries, from the start, verified cleanly.</param>
/// <param name="FirstBrokenIndex">The 0-based index of the first entry (line) that failed, or null when valid.</param>
/// <param name="Reason">Plain-English reason for the first failure, or null when valid.</param>
/// <param name="HeadHash">The hash of the last verified entry (<see cref="AuditLog.Genesis"/> when none).</param>
/// <param name="Entries">The verified entries, in order (only the clean prefix when invalid).</param>
public sealed record AuditLogVerification(
    bool IsValid,
    int VerifiedCount,
    int? FirstBrokenIndex,
    string? Reason,
    string HeadHash,
    IReadOnlyList<AuditLogEntry> Entries)
{
    /// <summary>An anchor for the verified part of the log: record this somewhere else to detect later
    /// tail truncation or a whole-log rewrite.</summary>
    public AuditAnchor Anchor => new(VerifiedCount, HeadHash);

    /// <summary>GR-374 (November 2026): the hash of the log's first verified entry, its identity (no two logs share one
    /// unless one is a copy of the other), or <see cref="AuditLog.Genesis"/> when no entry verified. A v2 signed anchor
    /// binds it.</summary>
    public string FirstHash { get; init; } = AuditLog.Genesis;

    /// <summary>Fail loud: throw <see cref="AuditLogTamperException"/> naming the first broken entry when
    /// this result is not valid. Does nothing when it is valid.</summary>
    public void ThrowIfInvalid(string filePath)
    {
        if (IsValid)
            return;
        // Both fields are always set on an invalid result (see AuditLogFile.Broken); the fallbacks only keep
        // the compiler happy and would still throw, never pass.
        throw new AuditLogTamperException(filePath, FirstBrokenIndex ?? 0, Reason ?? "verification failed");
    }
}

/// <summary>
/// Thrown when an audit log file fails verification: an entry was edited, deleted, inserted, reordered, the
/// file was truncated, or it does not match an external anchor. The message names the file, the first broken
/// entry index and the reason. A log in this state must not be appended to; investigate it instead.
/// </summary>
public sealed class AuditLogTamperException : IOException   // InvalidDataException is sealed
{
    public AuditLogTamperException(string filePath, int entryIndex, string reason)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"Audit log '{filePath}' failed verification at entry {entryIndex}: {reason}. The hash chain cannot " +
            $"be trusted from this entry onward; treat the log as tampered with or damaged, keep a copy for " +
            $"investigation and do not append to it."))
    {
        FilePath = filePath;
        EntryIndex = entryIndex;
        Reason = reason;
    }

    /// <summary>The log file that failed.</summary>
    public string FilePath { get; }

    /// <summary>The 0-based index (line number minus one) of the first broken entry.</summary>
    public int EntryIndex { get; }

    /// <summary>Why that entry failed, in plain English.</summary>
    public string Reason { get; }
}

/// <summary>
/// The on-disk form of the hash-chained audit log (R075): reading, verifying and saving files. Appending to a
/// live log is done by <see cref="AuditLogWriter"/>.
///
/// <para><b>File format</b> (<see cref="FormatTag"/>): UTF-8 with no byte-order mark, one JSON object per line,
/// each line ended by a single LF ('\n'). Each object has exactly these fields, in this order:
/// <c>format</c> (always <see cref="FormatTag"/>), <c>seq</c> (0-based integer), <c>runUtc</c> (round-trip "o"
/// timestamp ending in Z), <c>stateUtc</c> (same, or null), <c>provenance</c> (a <see cref="ProvenanceTag"/>
/// name), <c>summary</c>, <c>prevHash</c> and <c>hash</c>. The hash is exactly the in-memory
/// <see cref="AuditLog"/> hash, so a file and an in-memory log holding the same records are interchangeable.</para>
///
/// <para><b>Canonical lines.</b> The writer always produces the same bytes for the same entry, and the verifier
/// re-encodes every parsed entry and requires the line to match byte for byte. So even a change that the hash
/// does not cover (extra spaces, reordered fields, a timestamp written in a different but equal form) is
/// reported, rather than silently accepted.</para>
///
/// <para><b>What verification can and cannot see.</b> Any edit, deletion, insertion or reordering of an entry,
/// and any cut through the middle of a line, breaks the chain at a specific entry and is reported with that
/// index. Removing whole entries from the END of the file leaves a shorter but internally consistent chain, and
/// rewriting the entire file with freshly computed hashes leaves a different but consistent chain; neither can
/// be seen from the file alone. Pass an <see cref="AuditAnchor"/> recorded elsewhere to catch both.</para>
/// </summary>
public static class AuditLogFile
{
    /// <summary>The format identifier written into every line.</summary>
    public const string FormatTag = "gridsim-audit/1";

    // Field names, in the one order the writer emits them. The verifier rejects any other set of names.
    private const string FFormat = "format";
    private const string FSeq = "seq";
    private const string FRunUtc = "runUtc";
    private const string FStateUtc = "stateUtc";
    private const string FProvenance = "provenance";
    private const string FSummary = "summary";
    private const string FPrevHash = "prevHash";
    private const string FHash = "hash";

    /// <summary>
    /// Verify the audit log at <paramref name="path"/>. Returns a result naming the first broken entry and the
    /// reason when the file does not verify; use <see cref="AuditLogVerification.ThrowIfInvalid"/> or
    /// <see cref="Load"/> to fail loud instead. An empty (zero-byte) file is a valid, empty log. A missing file
    /// is an error (<see cref="FileNotFoundException"/>), never an empty log: a deleted audit log must not pass.
    /// </summary>
    /// <param name="path">The log file.</param>
    /// <param name="anchor">Optional external anchor; when given, the file must still hold the anchored entries
    /// unchanged (it may hold more entries after them).</param>
    public static AuditLogVerification Verify(string path, AuditAnchor? anchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // FileShare.ReadWrite so a log can be checked while a writer still has it open. A check taken during a
        // write may see a half-written last line and report it as torn; verify a closed log for a final answer.
        // GR-859: verified in one streaming pass (one line held at a time); the file is no longer read whole first.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return VerifyStream(stream, anchor, keepEntries: true);
    }

    /// <summary>
    /// Load and verify the audit log at <paramref name="path"/>, returning its entries in order. Throws
    /// <see cref="AuditLogTamperException"/> (naming the first broken entry) if it does not verify.
    /// </summary>
    public static IReadOnlyList<AuditLogEntry> Load(string path, AuditAnchor? anchor = null)
    {
        AuditLogVerification result = Verify(path, anchor);
        result.ThrowIfInvalid(Path.GetFullPath(path));
        return result.Entries;
    }

    /// <summary>
    /// Write an in-memory <see cref="AuditLog"/> to a NEW file at <paramref name="path"/> in the on-disk format.
    /// Refuses to overwrite an existing file (an audit log is never replaced) and refuses to save a log that
    /// does not itself verify. The file can be appended to afterwards with <see cref="AuditLogWriter.Open"/>.
    /// </summary>
    public static void Save(AuditLog log, string path)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!log.Verify())
            throw new InvalidOperationException(
                "Refusing to save an audit log that does not verify in memory: its hash chain is already broken.");

        // CreateNew throws IOException if the file exists: an existing audit log is never overwritten.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (AuditLogEntry entry in log.Entries)
            stream.Write(EncodeLine(entry));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The canonical JSON line for <paramref name="entry"/>, without the trailing newline.</summary>
    public static string ToJsonLine(AuditLogEntry entry)
    {
        byte[] line = EncodeLine(entry);
        return Encoding.UTF8.GetString(line, 0, line.Length - 1);
    }

    /// <summary>
    /// The canonical bytes of one line, INCLUDING the trailing '\n'. Timestamps are normalised to UTC with the
    /// one shared rule (<see cref="UtcTime.Normalise"/>), which is also what the hash uses, so normalising here
    /// never changes whether the entry verifies.
    /// </summary>
    internal static byte[] EncodeLine(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // A provenance value outside the enum would be written as a bare number that the loader rejects;
        // refuse it at write time instead of producing a log that can never be read back.
        if (!Enum.IsDefined(entry.Provenance))
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Audit entry {entry.Sequence} has an undefined provenance value ({(int)entry.Provenance})."),
                nameof(entry));
        ArgumentNullException.ThrowIfNull(entry.Summary, nameof(entry));
        ArgumentNullException.ThrowIfNull(entry.PrevHash, nameof(entry));
        ArgumentNullException.ThrowIfNull(entry.Hash, nameof(entry));

        var buffer = new ArrayBufferWriter<byte>(256);
        // Default writer options: compact (no indentation) and the default encoder, which escapes every
        // non-ASCII character, so each line is plain ASCII and byte-for-byte reproducible.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(FFormat, FormatTag);
            writer.WriteNumber(FSeq, entry.Sequence);
            writer.WriteString(FRunUtc, FormatUtc(entry.RunTimestampUtc));
            if (entry.StateTimeUtc is DateTime state)
                writer.WriteString(FStateUtc, FormatUtc(state));
            else
                writer.WriteNull(FStateUtc);
            writer.WriteString(FProvenance, entry.Provenance.ToString());
            writer.WriteString(FSummary, entry.Summary);
            writer.WriteString(FPrevHash, entry.PrevHash);
            writer.WriteString(FHash, entry.Hash);
            writer.WriteEndObject();
        }

        var line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>The size of each read when a log is verified from a stream (GR-859).</summary>
    private const int StreamChunkBytes = 64 * 1024;

    /// <summary>
    /// Verify raw file bytes: the whole format and chain check. Stops at the first broken entry, because once
    /// the chain is broken nothing after that point can be trusted. GR-859: the same streaming pass as
    /// <see cref="VerifyStream"/>, over the bytes, so a file and a byte array always get the same verdict.
    /// </summary>
    internal static AuditLogVerification VerifyBytes(ReadOnlySpan<byte> bytes, AuditAnchor? anchor)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        return VerifyStream(stream, anchor, keepEntries: true);
    }

    /// <summary>
    /// GR-859 (November 2026): verify a log read from <paramref name="stream"/>, from its current position to its end,
    /// one line at a time. Before, the whole file was read into one byte array first, so opening a writer held the
    /// entire log in memory twice over (bytes and entries) and a log of 2 GiB or more could not be opened at all. Now
    /// only one line is held at a time; with <paramref name="keepEntries"/> false (the writer's open-time check, which
    /// needs only the count and the head hash) memory no longer grows with the log, and the result's
    /// <see cref="AuditLogVerification.Entries"/> is empty while its count and head hash are those of the full check.
    /// Every check, its order and every message are exactly those of the whole-file pass this replaces. Time still
    /// grows with the log: every entry's hash is recomputed on every open.
    /// </summary>
    internal static AuditLogVerification VerifyStream(Stream stream, AuditAnchor? anchor, bool keepEntries)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (anchor is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(anchor.Count, nameof(anchor));
            ArgumentNullException.ThrowIfNull(anchor.HeadHash, nameof(anchor));
        }

        var chain = new ChainVerifier(anchor, keepEntries);
        byte[] chunk = new byte[StreamChunkBytes];
        // The bytes of the line being assembled across reads (a line may span any number of chunks).
        byte[] line = new byte[256];
        int lineLength = 0;

        // A byte-order mark means the file was re-saved by something other than the writer. At least the first three
        // bytes (or the whole stream, if shorter) are read before anything else, so the check sees what the old one did.
        int read = stream.ReadAtLeast(chunk, 3, throwOnEndOfStream: false);
        if (read >= 3 && chunk[0] == 0xEF && chunk[1] == 0xBB && chunk[2] == 0xBF)
            return chain.Fail(0,
                "the file starts with a UTF-8 byte-order mark, which the writer never produces (the file was " +
                "re-saved by another program)");

        while (read > 0)
        {
            int at = 0;
            while (at < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', at, read - at);
                int take = (newline < 0 ? read : newline) - at;
                if (lineLength + take > line.Length)
                    Array.Resize(ref line, Math.Max(line.Length * 2, lineLength + take));
                Buffer.BlockCopy(chunk, at, line, lineLength, take);
                lineLength += take;
                if (newline < 0)
                    break;   // the line goes on in the next chunk
                if (chain.Accept(line.AsSpan(0, lineLength)) is AuditLogVerification broken)
                    return broken;
                lineLength = 0;
                at = newline + 1;
            }
            read = stream.Read(chunk, 0, chunk.Length);
        }

        if (lineLength > 0)
            return chain.Fail(chain.Count,
                "the last line has no terminating newline: the file was cut off part-way through this entry, " +
                "or a write was interrupted");
        return chain.Finish();
    }

    /// <summary>
    /// GR-859: the chain state of one verification pass, fed one complete line (without its '\n') at a time. It keeps
    /// every entry only when asked to; otherwise just the count, the head hash and the two hashes an anchor check can
    /// need (the anchored entry's and the one before it), which are all a result ever reports.
    /// </summary>
    private sealed class ChainVerifier(AuditAnchor? anchor, bool keepEntries)
    {
        private readonly List<AuditLogEntry> _entries = new();
        private string _previousHash = AuditLog.Genesis;
        private string? _hashAtAnchor;        // the hash of entry anchor.Count - 1, once verified
        private string? _hashBeforeAnchor;    // the hash of entry anchor.Count - 2, once verified
        private string? _firstHash;           // GR-374: the hash of entry 0, once verified

        /// <summary>How many entries have verified so far.</summary>
        public int Count { get; private set; }

        /// <summary>Check one line. Null when it verified and was added; the invalid result when it did not.</summary>
        public AuditLogVerification? Accept(ReadOnlySpan<byte> line)
        {
            int index = Count;
            if (line.IsEmpty)
                return Fail(index, "a blank line stands where an entry was expected");
            if (line[^1] == (byte)'\r')
                return Fail(index,
                    "the line ends with CR LF; the writer only ever writes LF (the file was re-saved by another program)");

            if (!TryParseLine(line, out AuditLogEntry? entry, out string? parseError))
                return Fail(index, parseError);

            // Order of checks: position first (catches deletion, insertion and reordering at the exact place),
            // then the link to the previous entry, then the entry's own hash (catches an edited field).
            if (entry.Sequence != index)
                return Fail(index, string.Create(CultureInfo.InvariantCulture,
                    $"the entry's sequence number is {entry.Sequence} but it is at position {index}: an entry was " +
                    $"deleted, inserted or moved here"));

            if (!string.Equals(entry.PrevHash, _previousHash, StringComparison.Ordinal))
                return Fail(index, index == 0
                    ? "the first entry's prevHash is not GENESIS: the start of the log was removed or replaced"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"prevHash does not equal the hash of entry {index - 1}: an earlier entry was edited " +
                        $"(and its hash recomputed), removed or reordered"));

            string recomputed = AuditLog.ComputeHash(
                entry.Sequence, entry.RunTimestampUtc, entry.StateTimeUtc, entry.Provenance, entry.Summary, entry.PrevHash);
            if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
                return Fail(index,
                    "the stored hash does not match the hash recomputed from the entry's fields: this entry's " +
                    "content was edited");

            if (keepEntries)
                _entries.Add(entry);
            if (anchor is not null)
            {
                if (index == anchor.Count - 1) _hashAtAnchor = entry.Hash;
                if (index == anchor.Count - 2) _hashBeforeAnchor = entry.Hash;
            }
            if (index == 0) _firstHash = entry.Hash;
            _previousHash = entry.Hash;
            Count = index + 1;
            return null;
        }

        /// <summary>The end of the stream: the anchor checks, then the valid result.</summary>
        public AuditLogVerification Finish()
        {
            if (anchor is not null)
            {
                if (Count < anchor.Count)
                    return Fail(Count, string.Create(CultureInfo.InvariantCulture,
                        $"the file holds {Count} entries but the anchor records {anchor.Count}: the entries " +
                        $"from index {Count} onward were removed (tail truncation)"));

                string anchoredHead = anchor.Count == 0 ? AuditLog.Genesis : HashAt(anchor.Count - 1);
                if (!string.Equals(anchoredHead, anchor.HeadHash, StringComparison.Ordinal))
                {
                    int at = Math.Max(anchor.Count - 1, 0);
                    return Fail(at, string.Create(CultureInfo.InvariantCulture,
                        $"the hash of entry {at} does not match the externally anchored head hash: the log was " +
                        $"rewritten with recomputed hashes (or the anchor belongs to a different log)"));
                }
            }

            return new AuditLogVerification(true, Count, null, null, _previousHash,
                keepEntries ? _entries : Array.Empty<AuditLogEntry>()) { FirstHash = _firstHash ?? AuditLog.Genesis };
        }

        /// <summary>Build an invalid result: the clean prefix before <paramref name="index"/>, the index and why.</summary>
        public AuditLogVerification Fail(int index, string reason)
        {
            int clean = Math.Min(index, Count);
            IReadOnlyList<AuditLogEntry> prefix = keepEntries ? _entries.GetRange(0, clean) : Array.Empty<AuditLogEntry>();
            string head = clean == 0 ? AuditLog.Genesis : HashAt(clean - 1);
            return new AuditLogVerification(false, clean, index, reason, head, prefix)
            {
                FirstHash = clean > 0 && _firstHash is not null ? _firstHash : AuditLog.Genesis,
            };
        }

        // The hash of verified entry i. Without the entries kept, only the last entry's and the two anchor-adjacent
        // ones are known, and those are the only ones Finish and Fail ever ask for (Fail asks for clean - 1, where
        // clean is either Count or anchor.Count - 1).
        private string HashAt(int i)
        {
            if (keepEntries) return _entries[i].Hash;
            if (i == Count - 1) return _previousHash;
            if (anchor is not null && i == anchor.Count - 1 && _hashAtAnchor is not null) return _hashAtAnchor;
            if (anchor is not null && i == anchor.Count - 2 && _hashBeforeAnchor is not null) return _hashBeforeAnchor;
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Audit verification asked for the hash of entry {i}, which a streaming pass does not keep."));
        }
    }

    /// <summary>
    /// Parse one line strictly: a JSON object with exactly the eight known fields, each of the right type,
    /// UTC timestamps, a named provenance, and bytes identical to what the writer would produce for it.
    /// </summary>
    private static bool TryParseLine(
        ReadOnlySpan<byte> line,
        [NotNullWhen(true)] out AuditLogEntry? entry,
        [NotNullWhen(false)] out string? error)
    {
        entry = null;
        string? format = null, runText = null, provenanceText = null, summary = null, prevHash = null, hash = null;
        string? stateText = null;
        bool stateSeen = false;
        int? sequence = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line.ToArray());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the line is not a JSON object";
                return false;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    error = $"the field '{property.Name}' appears twice";
                    return false;
                }

                JsonElement value = property.Value;
                switch (property.Name)
                {
                    case FFormat: format = StringOrNull(value); break;
                    case FRunUtc: runText = StringOrNull(value); break;
                    case FProvenance: provenanceText = StringOrNull(value); break;
                    case FSummary: summary = StringOrNull(value); break;
                    case FPrevHash: prevHash = StringOrNull(value); break;
                    case FHash: hash = StringOrNull(value); break;
                    case FSeq:
                        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int s) && s >= 0)
                            sequence = s;
                        else
                        {
                            error = "the 'seq' field is not a non-negative whole number";
                            return false;
                        }
                        break;
                    case FStateUtc:
                        stateSeen = true;
                        if (value.ValueKind == JsonValueKind.Null)
                            stateText = null;
                        else if (value.ValueKind == JsonValueKind.String)
                            stateText = value.GetString();
                        else
                        {
                            error = "the 'stateUtc' field is neither a string nor null";
                            return false;
                        }
                        break;
                    default:
                        error = $"unknown field '{property.Name}'";
                        return false;
                }
            }
        }
        // JsonException for malformed JSON; InvalidOperationException / ArgumentException for text that is not
        // valid UTF-8 or cannot be transcoded. Any of them means the line is not one the writer produced.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            error = $"the line is not valid JSON ({ex.Message})";
            return false;
        }

        if (!string.Equals(format, FormatTag, StringComparison.Ordinal))
        {
            error = $"the 'format' field is missing or is not '{FormatTag}'";
            return false;
        }
        if (sequence is not int seq)
        {
            error = "the 'seq' field is missing";
            return false;
        }
        if (runText is null || !TryParseUtc(runText, out DateTime runUtc))
        {
            error = "the 'runUtc' field is missing or is not a round-trip UTC timestamp ending in Z";
            return false;
        }
        if (!stateSeen)
        {
            error = "the 'stateUtc' field is missing (it must be present, as null when there is no state time)";
            return false;
        }
        DateTime? stateUtc = null;
        if (stateText is not null)
        {
            if (!TryParseUtc(stateText, out DateTime parsedState))
            {
                error = "the 'stateUtc' field is not a round-trip UTC timestamp ending in Z";
                return false;
            }
            stateUtc = parsedState;
        }
        if (provenanceText is null
            || !Enum.TryParse(provenanceText, ignoreCase: false, out ProvenanceTag provenance)
            || !Enum.IsDefined(provenance)
            || !string.Equals(provenance.ToString(), provenanceText, StringComparison.Ordinal))
        {
            error = "the 'provenance' field is missing or is not a ProvenanceTag name";
            return false;
        }
        if (summary is null || prevHash is null || hash is null)
        {
            error = "one of the 'summary', 'prevHash' or 'hash' fields is missing or is not a string";
            return false;
        }

        var parsed = new AuditLogEntry(seq, runUtc, stateUtc, provenance, summary, prevHash, hash);

        // Canonical form: the writer is the only legitimate author of a line, so the line must be exactly the
        // bytes the writer would produce for these values (minus the newline, already stripped).
        byte[] canonical = EncodeLine(parsed);
        if (!line.SequenceEqual(canonical.AsSpan(0, canonical.Length - 1)))
        {
            error = "the line is not in the writer's canonical form (fields reordered, spacing changed or a value " +
                    "re-written in a different notation): it was edited by hand";
            return false;
        }

        entry = parsed;
        error = null;
        return true;
    }

    /// <summary>The string value of a JSON element, or null when it is not a string (reported as missing).</summary>
    private static string? StringOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Parse a round-trip ("o") timestamp and accept it only if it is UTC (ends in Z).</summary>
    private static bool TryParseUtc(string text, out DateTime utc) =>
        DateTime.TryParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc)
        && utc.Kind == DateTimeKind.Utc;

    /// <summary>
    /// N064 (October 2026): the ONE display form of a verified audit entry, for every surface that shows a log to a
    /// person (the CLI's <c>audit show</c> today; the web and desktop shells are meant to call this too, so the three
    /// never word an entry differently). One line, plain ASCII:
    /// <c>#&lt;seq&gt; run=&lt;UTC&gt; state=&lt;UTC or -&gt; provenance=&lt;tag&gt; hash=&lt;hash&gt; &lt;summary&gt;</c>.
    /// The timestamps use the file's own notation (UTC, round-trip "o"), so what is shown is exactly what was hashed.
    /// The summary is printed as recorded: the writer escapes nothing in memory, so any non-ASCII character a summary
    /// might hold is replaced by '?' here (the CLI promises plain ASCII output), and line breaks become spaces so one
    /// entry is always one line. Only call this with entries that came out of a verification
    /// (<see cref="Load"/> or <see cref="AuditLogVerification.Entries"/>): showing an unverified entry would present
    /// untrusted text as if it were evidence.
    /// </summary>
    public static string Describe(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string state = entry.StateTimeUtc is DateTime s ? FormatUtc(s) : "-";
        var summary = new StringBuilder(entry.Summary.Length);
        foreach (char c in entry.Summary)
            summary.Append(c is '\r' or '\n' ? ' ' : c < 32 || c > 126 ? '?' : c);
        return string.Create(CultureInfo.InvariantCulture,
            $"#{entry.Sequence} run={FormatUtc(entry.RunTimestampUtc)} state={state} provenance={entry.Provenance} hash={entry.Hash} {summary}");
    }

    /// <summary>The one timestamp notation in the file: UTC, round-trip "o" format, invariant culture.</summary>
    private static string FormatUtc(DateTime t) =>
        UtcTime.Normalise(t).ToString("o", CultureInfo.InvariantCulture);
}
