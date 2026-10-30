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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = ReadAll(stream);
        return VerifyBytes(bytes, anchor);
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

    /// <summary>Read a whole file from the start. Shared by <see cref="Verify"/> and the writer's open-time check.</summary>
    internal static byte[] ReadAll(FileStream stream)
    {
        long length = stream.Length;
        if (length > Array.MaxLength)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"Audit log '{stream.Name}' is {length} bytes, too large to verify in one pass."));
        var bytes = new byte[length];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Verify raw file bytes: the whole format and chain check. Stops at the first broken entry, because once
    /// the chain is broken nothing after that point can be trusted.
    /// </summary>
    internal static AuditLogVerification VerifyBytes(ReadOnlySpan<byte> bytes, AuditAnchor? anchor)
    {
        if (anchor is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(anchor.Count, nameof(anchor));
            ArgumentNullException.ThrowIfNull(anchor.HeadHash, nameof(anchor));
        }

        var entries = new List<AuditLogEntry>();
        string previousHash = AuditLog.Genesis;

        // A byte-order mark means the file was re-saved by something other than the writer.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Broken(entries, 0,
                "the file starts with a UTF-8 byte-order mark, which the writer never produces (the file was " +
                "re-saved by another program)");

        int start = 0;
        while (start < bytes.Length)
        {
            int index = entries.Count;
            ReadOnlySpan<byte> rest = bytes[start..];
            int newline = rest.IndexOf((byte)'\n');
            if (newline < 0)
                return Broken(entries, index,
                    "the last line has no terminating newline: the file was cut off part-way through this entry, " +
                    "or a write was interrupted");

            ReadOnlySpan<byte> line = rest[..newline];
            start += newline + 1;

            if (line.IsEmpty)
                return Broken(entries, index, "a blank line stands where an entry was expected");
            if (line[^1] == (byte)'\r')
                return Broken(entries, index,
                    "the line ends with CR LF; the writer only ever writes LF (the file was re-saved by another program)");

            if (!TryParseLine(line, out AuditLogEntry? entry, out string? parseError))
                return Broken(entries, index, parseError);

            // Order of checks: position first (catches deletion, insertion and reordering at the exact place),
            // then the link to the previous entry, then the entry's own hash (catches an edited field).
            if (entry.Sequence != index)
                return Broken(entries, index, string.Create(CultureInfo.InvariantCulture,
                    $"the entry's sequence number is {entry.Sequence} but it is at position {index}: an entry was " +
                    $"deleted, inserted or moved here"));

            if (!string.Equals(entry.PrevHash, previousHash, StringComparison.Ordinal))
                return Broken(entries, index, index == 0
                    ? "the first entry's prevHash is not GENESIS: the start of the log was removed or replaced"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"prevHash does not equal the hash of entry {index - 1}: an earlier entry was edited " +
                        $"(and its hash recomputed), removed or reordered"));

            string recomputed = AuditLog.ComputeHash(
                entry.Sequence, entry.RunTimestampUtc, entry.StateTimeUtc, entry.Provenance, entry.Summary, entry.PrevHash);
            if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
                return Broken(entries, index,
                    "the stored hash does not match the hash recomputed from the entry's fields: this entry's " +
                    "content was edited");

            entries.Add(entry);
            previousHash = entry.Hash;
        }

        if (anchor is not null)
        {
            if (entries.Count < anchor.Count)
                return Broken(entries, entries.Count, string.Create(CultureInfo.InvariantCulture,
                    $"the file holds {entries.Count} entries but the anchor records {anchor.Count}: the entries " +
                    $"from index {entries.Count} onward were removed (tail truncation)"));

            string anchoredHead = anchor.Count == 0 ? AuditLog.Genesis : entries[anchor.Count - 1].Hash;
            if (!string.Equals(anchoredHead, anchor.HeadHash, StringComparison.Ordinal))
            {
                int at = Math.Max(anchor.Count - 1, 0);
                return Broken(entries, at, string.Create(CultureInfo.InvariantCulture,
                    $"the hash of entry {at} does not match the externally anchored head hash: the log was " +
                    $"rewritten with recomputed hashes (or the anchor belongs to a different log)"));
            }
        }

        return new AuditLogVerification(true, entries.Count, null, null, previousHash, entries);
    }

    /// <summary>Build an invalid result: the clean prefix before <paramref name="index"/>, the index and why.</summary>
    private static AuditLogVerification Broken(List<AuditLogEntry> entries, int index, string reason)
    {
        int clean = Math.Min(index, entries.Count);
        List<AuditLogEntry> prefix = entries.GetRange(0, clean);
        string head = clean == 0 ? AuditLog.Genesis : prefix[clean - 1].Hash;
        return new AuditLogVerification(false, clean, index, reason, head, prefix);
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
