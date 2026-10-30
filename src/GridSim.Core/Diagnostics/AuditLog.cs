using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>One tamper-evident audit record: an emitted estimate (or any provenance-bearing output) with the
/// run metadata and a hash that chains it to the previous entry.</summary>
/// <param name="Sequence">Position in the log (0-based).</param>
/// <param name="RunTimestampUtc">When the record was written (run/wall-clock metadata - NOT a grid claim).</param>
/// <param name="StateTimeUtc">The estimate's present/past state time (the grid instant it describes), if any.</param>
/// <param name="Provenance">The trust class of the output.</param>
/// <param name="Summary">Human-readable description (method, chi-square verdict, case, etc.).</param>
/// <param name="PrevHash">Hash of the previous entry (<see cref="AuditLog.Genesis"/> for the first).</param>
/// <param name="Hash">Hash over this entry's fields and <see cref="PrevHash"/>.</param>
public sealed record AuditLogEntry(
    int Sequence,
    DateTime RunTimestampUtc,
    DateTime? StateTimeUtc,
    ProvenanceTag Provenance,
    string Summary,
    string PrevHash,
    string Hash);

/// <summary>
/// An append-only, hash-chained audit log for the estimation pipeline - the evidence trail a critical-national-
/// infrastructure service needs (NCSC CAF objective C, "detecting cyber security events"). Each entry records
/// what was emitted, when (run metadata), for which grid instant (present/past state time), its provenance
/// class, and a hash chaining it to its predecessor. Altering any entry after the fact breaks the chain from
/// that point on, so tampering is detectable by <see cref="Verify(IReadOnlyList{AuditLogEntry})"/>. It pairs
/// the estate's existing <see cref="ProvenanceTag"/> (trust class) and run metadata (git commit, host, time)
/// into a single verifiable record.
/// <para>This class is the in-memory log. For a persistent, append-only log on disk (one JSON line per entry,
/// same hash chain) use <see cref="AuditLogWriter"/> to append, <see cref="AuditLogFile"/> to verify or load,
/// and <see cref="AuditEvent"/> to record a product event (who / what / when / input hashes) (R075).</para>
/// </summary>
public sealed class AuditLog
{
    /// <summary>The <see cref="AuditLogEntry.PrevHash"/> of the first entry.</summary>
    public const string Genesis = "GENESIS";

    private readonly List<AuditLogEntry> _entries = new();

    /// <summary>The log so far, in order.</summary>
    public IReadOnlyList<AuditLogEntry> Entries => _entries;

    /// <summary>Append a record; its hash chains to the current tail.</summary>
    public AuditLogEntry Append(DateTime runTimestampUtc, DateTime? stateTimeUtc, ProvenanceTag provenance, string summary)
    {
        // GR-849: the on-disk writer's checks and normalisation (AuditLogWriter.Append), so an in-memory log can always
        // be saved: a null summary or an undefined provenance is refused here rather than at Save, and the times are
        // stored as the UTC values the hash already used (a Local-kind time used to be stored as given).
        ArgumentNullException.ThrowIfNull(summary);
        if (!Enum.IsDefined(provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance,
                "The provenance is not a defined ProvenanceTag value; it could not be read back from the log.");
        DateTime runUtc = UtcTime.Normalise(runTimestampUtc);
        DateTime? stateUtc = stateTimeUtc is DateTime st ? UtcTime.Normalise(st) : null;

        int seq = _entries.Count;
        string prev = seq == 0 ? Genesis : _entries[seq - 1].Hash;
        string hash = ComputeHash(seq, runUtc, stateUtc, provenance, summary, prev);
        var entry = new AuditLogEntry(seq, runUtc, stateUtc, provenance, summary, prev, hash);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Verify this log's integrity (ordering + hash chain).</summary>
    public bool Verify() => Verify(_entries);

    /// <summary>Verify any entry list: sequence numbering, the genesis link, per-entry hash and chain links.
    /// Returns false if any entry has been altered or reordered.</summary>
    public static bool Verify(IReadOnlyList<AuditLogEntry> entries)
    {
        string prev = Genesis;
        for (int i = 0; i < entries.Count; i++)
        {
            AuditLogEntry e = entries[i];
            if (e.Sequence != i || !string.Equals(e.PrevHash, prev, StringComparison.Ordinal))
                return false;
            string recomputed = ComputeHash(e.Sequence, e.RunTimestampUtc, e.StateTimeUtc, e.Provenance, e.Summary, e.PrevHash);
            if (!string.Equals(recomputed, e.Hash, StringComparison.Ordinal))
                return false;
            prev = e.Hash;
        }
        return true;
    }

    // Internal (not private) so the on-disk log (AuditLogWriter / AuditLogFile, R075) hashes with exactly this
    // one function. There must never be a second copy of the chain hash: a file written by the writer has to
    // verify with Verify(entries) here, and an in-memory log saved to disk has to verify from the file.
    internal static string ComputeHash(
        int seq, DateTime runUtc, DateTime? stateUtc, ProvenanceTag tag, string summary, string prev)
    {
        // UtcTime.Normalise, not ToUniversalTime: an Unspecified stamp is UTC by contract. ToUniversalTime shifted
        // it by the machine's local offset, so the same entry hashed differently on a BST machine and a UTC
        // server, and a log written on one failed Verify() on the other (R277).
        string payload = string.Join(
            "|",
            seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UtcTime.Normalise(runUtc).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            stateUtc is DateTime s
                ? UtcTime.Normalise(s).ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                : "-",
            tag.ToString(),
            summary,
            prev);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}
