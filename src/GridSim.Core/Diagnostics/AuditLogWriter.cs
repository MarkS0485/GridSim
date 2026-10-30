using System.Globalization;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// The append-only, on-disk audit log (R075). Each <see cref="Append"/> chains a new entry to the current tail
/// with the same hash as the in-memory <see cref="AuditLog"/>, writes it as one JSON line (format in
/// <see cref="AuditLogFile"/>), and flushes it to disk before returning, so an entry the caller has been handed
/// is already durable.
///
/// <para><b>Single writer, enforced.</b> Exactly one writer may have a given log open at a time, across threads
/// and across processes. Opening takes an exclusive lock on a companion file (<c>&lt;log&gt;.lock</c>, which is
/// left in place and holds no data; the lock is the open handle, not the file's existence) and opens the log
/// itself with write access for this writer only. A second <see cref="Open"/> on the same log, from this or any
/// other process, fails loud with <see cref="InvalidOperationException"/>. Readers (<see cref="AuditLogFile.Verify"/>)
/// may still read the log while it is open.</para>
///
/// <para><b>Thread safe.</b> Several threads may call <see cref="Append"/> on one writer; appends are serialised
/// by a lock, so the sequence numbers and the chain stay intact whatever the interleaving.</para>
///
/// <para><b>Refuses a damaged log.</b> Opening an existing file verifies it first. A log that does not verify
/// (edited, reordered, truncated, a torn last line from a crash) is never appended to:
/// <see cref="Open"/> throws <see cref="AuditLogTamperException"/> naming the first broken entry, so the damage
/// is investigated rather than buried under new entries. If a write fails part-way, the writer marks itself
/// faulted and refuses every later append for the same reason.</para>
///
/// <para><b>No secrets.</b> The summary is stored as plain text (it is hashed, not encrypted). Never put a
/// password, key, token or connection string into it.</para>
/// </summary>
public sealed class AuditLogWriter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly FileStream _lockFile;
    private readonly FileStream _stream;
    private readonly IReadOnlyList<IAuditSink> _sinks;
    private int _count;
    private string _headHash;
    private bool _faulted;
    private bool _disposed;
    private string? _lastSinkError;

    private AuditLogWriter(string filePath, FileStream lockFile, FileStream stream, int count, string headHash,
        IReadOnlyList<IAuditSink> sinks)
    {
        FilePath = filePath;
        _lockFile = lockFile;
        _stream = stream;
        _count = count;
        _headHash = headHash;
        _sinks = sinks;
    }

    /// <summary>The full path of the log file.</summary>
    public string FilePath { get; }

    /// <summary>How many entries the log holds (those already on disk when opened, plus those appended).</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>The hash of the last entry, or <see cref="AuditLog.Genesis"/> for an empty log.</summary>
    public string HeadHash
    {
        get { lock (_gate) return _headHash; }
    }

    /// <summary>The message of the first <see cref="IAuditSink"/> failure since the writer was opened, or null
    /// if every sink has accepted every entry. A sink failure never faults the log or fails an append (the
    /// on-disk chain is authoritative; a sink is a secondary forwarder), but it is kept here so a caller can
    /// surface a syslog / SIEM delivery problem without trusting that delivery for the record itself.</summary>
    public string? LastSinkError
    {
        get { lock (_gate) return _lastSinkError; }
    }

    /// <summary>The current length and head hash as an <see cref="AuditAnchor"/>, taken atomically. Record it
    /// somewhere the writer cannot change (for example print it at the end of a run) so that later tail
    /// truncation or a whole-log rewrite can be detected with <see cref="AuditLogFile.Verify"/>.</summary>
    public AuditAnchor Anchor
    {
        get { lock (_gate) return new AuditAnchor(_count, _headHash); }
    }

    /// <summary>
    /// Open (or create) the audit log at <paramref name="path"/> for appending. An existing file is verified
    /// first, and against <paramref name="expectedAnchor"/> when one is given; a log that does not verify is
    /// refused with <see cref="AuditLogTamperException"/>. A missing file is created empty. The directory must
    /// already exist (a mistyped directory is an error, not something to create silently).
    /// </summary>
    /// <exception cref="InvalidOperationException">Another writer already has this log open.</exception>
    /// <exception cref="AuditLogTamperException">The existing log does not verify.</exception>
    /// <exception cref="DirectoryNotFoundException">The log's directory does not exist.</exception>
    /// <param name="path">The log file to open or create.</param>
    /// <param name="expectedAnchor">An anchor the existing file must still match, or null to verify the chain
    /// alone.</param>
    /// <param name="sinks">Secondary observers (SIEM / syslog / WORM forwarders) notified after each durable
    /// append; see <see cref="IAuditSink"/>. Null or empty means the on-disk chain is the only record.</param>
    public static AuditLogWriter Open(string path, AuditAnchor? expectedAnchor = null,
        IReadOnlyList<IAuditSink>? sinks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"Cannot open audit log '{fullPath}': its directory does not exist.");

        FileStream? lockFile = null;
        FileStream? stream = null;
        try
        {
            // Step 1: the single-writer lock. FileShare.None makes this an exclusive lock on every platform
            // (on Linux and macOS .NET maps it to an exclusive advisory lock), so a second writer stops here.
            string lockPath = fullPath + ".lock";
            try
            {
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Audit log '{fullPath}' is already open for writing (its lock file '{lockPath}' is held by " +
                    $"another writer in this or another process). Only one writer may append to an audit log at " +
                    $"a time. ({ex.Message})",
                    ex);
            }

            // Step 2: the log itself. FileShare.Read lets verifiers read it; nobody else may write to it.
            try
            {
                stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Audit log '{fullPath}' could not be opened for exclusive writing; another program has it " +
                    $"open for writing. ({ex.Message})",
                    ex);
            }

            // Step 3: verify what is already there before adding to it, and pick up the tail of the chain.
            byte[] existing = AuditLogFile.ReadAll(stream);
            AuditLogVerification verification = AuditLogFile.VerifyBytes(existing, expectedAnchor);
            verification.ThrowIfInvalid(fullPath);

            // Step 4: position at the end; every write from here on is an append.
            stream.Seek(0, SeekOrigin.End);

            IReadOnlyList<IAuditSink> sinkList = sinks is null
                ? Array.Empty<IAuditSink>()
                : sinks.ToArray();   // copy so the caller cannot mutate the list behind the writer
            foreach (IAuditSink sink in sinkList)
                ArgumentNullException.ThrowIfNull(sink, nameof(sinks));
            var writer = new AuditLogWriter(fullPath, lockFile, stream, verification.VerifiedCount, verification.HeadHash, sinkList);
            // Ownership has passed to the writer; stop the finally block from closing the handles.
            lockFile = null;
            stream = null;
            return writer;
        }
        finally
        {
            stream?.Dispose();
            lockFile?.Dispose();
        }
    }

    /// <summary>
    /// Append one record, chained to the current tail, and flush it to disk before returning. Timestamps are
    /// stored as UTC using the shared <see cref="UtcTime.Normalise"/> rule (an Unspecified time is taken as UTC,
    /// as everywhere in GridSim), which is also the rule the hash uses.
    /// </summary>
    /// <param name="runTimestampUtc">When the event happened (run / wall-clock metadata, not a grid claim).</param>
    /// <param name="stateTimeUtc">The present/past grid instant the record describes, if any.</param>
    /// <param name="provenance">The trust class of the output being recorded.</param>
    /// <param name="summary">What happened. Plain text, hashed but NOT secret: never include credentials.</param>
    /// <returns>The entry exactly as written.</returns>
    public AuditLogEntry Append(DateTime runTimestampUtc, DateTime? stateTimeUtc, ProvenanceTag provenance, string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!Enum.IsDefined(provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance,
                "The provenance is not a defined ProvenanceTag value; it could not be read back from the log.");

        DateTime runUtc = UtcTime.Normalise(runTimestampUtc);
        DateTime? stateUtc = stateTimeUtc is DateTime s ? UtcTime.Normalise(s) : null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted)
                throw new InvalidOperationException(
                    $"Audit log '{FilePath}' is faulted: an earlier write failed part-way, so the end of the file " +
                    $"is in an unknown state. Close it and run a verify before appending again.");
            if (_count == int.MaxValue)
                throw new InvalidOperationException(
                    $"Audit log '{FilePath}' has reached the maximum number of entries; start a new log.");

            int sequence = _count;
            string hash = AuditLog.ComputeHash(sequence, runUtc, stateUtc, provenance, summary, _headHash);
            var entry = new AuditLogEntry(sequence, runUtc, stateUtc, provenance, summary, _headHash, hash);
            byte[] line = AuditLogFile.EncodeLine(entry);

            try
            {
                _stream.Write(line);
                // flushToDisk: true asks the operating system to put the bytes on the disk, not just in its cache,
                // so a crash straight after Append returns does not lose the entry.
                _stream.Flush(flushToDisk: true);
            }
            catch
            {
                // We do not know how much of the line reached the file. Refuse all further appends.
                _faulted = true;
                throw;
            }

            // Only advance the chain once the line is safely written.
            _count = sequence + 1;
            _headHash = hash;

            // Notify secondary forwarders AFTER the entry is durable and the chain has advanced. A sink failure
            // is kept, not thrown: the on-disk chain is the authoritative record and must not depend on an
            // external forwarder (a syslog/SIEM outage must not stop the service or lose the durable entry).
            if (_sinks.Count > 0)
            {
                foreach (IAuditSink sink in _sinks)
                {
                    try
                    {
                        sink.OnEntry(entry);
                    }
                    catch (Exception ex)
                    {
                        _lastSinkError ??= $"{sink.GetType().Name}: {ex.Message}";
                    }
                }
            }
            return entry;
        }
    }

    /// <summary>Close the log and release the single-writer lock.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream.Dispose();
            _lockFile.Dispose();
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"AuditLogWriter({FilePath}, {Count} entries)");
}
