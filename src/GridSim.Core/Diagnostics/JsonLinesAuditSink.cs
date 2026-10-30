using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.Diagnostics;

/// <summary>A secondary delivery channel (a forwarding or incident file) that keeps its first failure instead of throwing,
/// so the product path can report it when the run ends.</summary>
public interface IDeliveryChannel
{
    /// <summary>The channel's file (or other destination), for the report.</summary>
    string FilePath { get; }

    /// <summary>The first delivery failure, or null when everything was delivered.</summary>
    string? LastError { get; }
}

/// <summary>
/// An <see cref="IAuditSink"/> that appends every audit entry, as one JSON line, to a second file: the forwarding
/// point for a SIEM / syslog collector that tails a file, or a copy on a write-once (WORM) or remote mount, so a rewrite
/// of the local log and its anchor together can still be caught against an independent copy (NCSC CAF C1).
///
/// <para>The line holds every field of the entry, hash and previous hash included, so the copy can be verified as a
/// chain on its own. Each line is written and flushed to disk before <see cref="OnEntry"/> returns.</para>
///
/// <para><b>Never fails the primary log.</b> As the <see cref="IAuditSink"/> contract requires, a write failure (a
/// full disk, a lost mount) is caught and kept in <see cref="LastError"/>; the authoritative on-disk chain is already
/// durable by the time a sink is called. The product paths report <see cref="LastError"/> when the run ends.</para>
/// </summary>
public sealed class JsonLinesAuditSink : IAuditSink, IDeliveryChannel
{
    private readonly Lock _gate = new();
    private string? _lastError;

    /// <summary>Forward to <paramref name="path"/>, creating it if needed (its directory must exist).</summary>
    public JsonLinesAuditSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(FilePath);
        if (dir is null || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Cannot forward the audit log to '{FilePath}': its directory does not exist.");
    }

    /// <summary>The full path of the forwarding file.</summary>
    public string FilePath { get; }

    /// <summary>The first write failure, or null when every entry was forwarded (<see cref="IDeliveryChannel"/>).</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>The JSON line for one entry (no trailing newline). Public so a test and a reader agree on the shape.</summary>
    public static string Format(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("sequence", entry.Sequence);
            w.WriteString("runTimestampUtc", entry.RunTimestampUtc.ToString("o", CultureInfo.InvariantCulture));
            if (entry.StateTimeUtc is DateTime st)
                w.WriteString("stateTimeUtc", st.ToString("o", CultureInfo.InvariantCulture));
            else
                w.WriteNull("stateTimeUtc");
            w.WriteString("provenance", entry.Provenance.ToString());
            w.WriteString("summary", entry.Summary);
            w.WriteString("prevHash", entry.PrevHash);
            w.WriteString("hash", entry.Hash);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc />
    public void OnEntry(AuditLogEntry entry)
    {
        if (entry is null) return;   // the contract: never throw into the writer
        lock (_gate)
        {
            try
            {
                byte[] line = Encoding.UTF8.GetBytes(Format(entry) + "\n");
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(line);
                fs.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastError ??= $"audit forward to '{FilePath}' failed at entry {entry.Sequence}: {ex.Message}";
            }
        }
    }
}
