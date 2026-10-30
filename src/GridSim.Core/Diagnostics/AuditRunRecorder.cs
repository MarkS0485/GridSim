using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// Records ONE product run (a CLI verb such as <c>estimate</c>) in the persistent audit log (R075 wiring).
///
/// <para><b>Lifecycle.</b> <see cref="Start"/> opens the log BEFORE the run does any work, so a tampered, locked
/// or misplaced log stops the run before it produces anything (fail loud). The run then adds its inputs
/// (<see cref="AddInput"/>), its one-line result (<see cref="SetOutcome"/>) and, when it has one, the grid instant
/// it describes (<see cref="StateTimeUtc"/>). <see cref="Finish"/> writes the single entry and closes the log.
/// <see cref="Finish"/> runs exactly once however it is reached: a normal return, an early return, a process
/// exit from deep inside a flag parser, or an unhandled exception all record the run, and a second call does
/// nothing. A failed run is recorded too: a refused or failed run is an audit event in its own right.</para>
///
/// <para><b>Entry layout</b> (via <see cref="AuditEvent.FormatSummary"/>): the outcome is
/// <c>exit=&lt;code&gt;</c>, followed by the run's own outcome text when it set one, so the exit code is always in
/// the hashed record.</para>
///
/// <para><b>Thread safety.</b> All members are serialised by one lock: <see cref="Finish"/> may be reached from a
/// process-exit handler on another thread while the run is still adding its outcome.</para>
/// </summary>
public sealed class AuditRunRecorder
{
    private readonly Lock _gate = new();
    private readonly AuditLogWriter _log;
    private readonly AuditRecorderOptions _options;
    private readonly List<AuditInput> _inputs = new();
    private string? _outcome;
    private DateTime? _stateTimeUtc;
    private bool _finished;

    private AuditRunRecorder(AuditLogWriter log, string actor, string action, ProvenanceTag provenance,
        string anchorPath, bool anchorWasMissing, AuditRecorderOptions options, string signaturePath)
    {
        _log = log;
        _options = options;
        SignaturePath = signaturePath;
        Actor = actor;
        Action = action;
        Provenance = provenance;
        AnchorPath = anchorPath;
        AnchorWasMissing = anchorWasMissing;
    }

    /// <summary>D-DOC-062: the anchor file this run checked the log against at <see cref="Start"/> and rewrites after
    /// every entry it appends.</summary>
    public string AnchorPath { get; }

    /// <summary>
    /// D-DOC-062: true when the log already held entries but no anchor file was found, so this run could not check the
    /// log for tail truncation (a log written before anchoring existed, or an anchor that was deleted). The run still
    /// proceeds, the gap is recorded in the hashed entry itself (<c>audit-anchor=missing</c> at the end of the outcome)
    /// and the anchor is written from this run on. A caller should also warn the operator.
    /// </summary>
    public bool AnchorWasMissing { get; }

    /// <summary>D-DOC-062: why the anchor file could not be rewritten after the last entry, or null when it was. The
    /// entry itself is durable either way; a stale anchor only means the next run checks a shorter prefix.</summary>
    public string? AnchorWriteError { get; private set; }

    /// <summary>The detached anchor-signature file this run checks (with a verification key) and rewrites (with a
    /// signing key). Unused when neither key is configured.</summary>
    public string SignaturePath { get; }

    /// <summary>True when the anchor is signed after every append (a signing key was configured).</summary>
    public bool SignsAnchor => _options.AnchorSigningKey is not null;

    /// <summary>The first failure of a forwarding sink (the on-disk log is unaffected), or null.</summary>
    public string? SinkError => _log.LastSinkError;

    /// <summary>Every delivery failure so far: a sink that threw into the writer (<see cref="SinkError"/>) and each
    /// configured <see cref="IDeliveryChannel"/> that recorded one. Empty when everything was delivered. A product path
    /// reports these when the run ends; they never fail an append.</summary>
    public IReadOnlyList<string> DeliveryErrors
    {
        get
        {
            var errors = new List<string>();
            if (SinkError is string s) errors.Add(s);
            foreach (IDeliveryChannel c in _options.DeliveryChannels)
                if (c.LastError is string e) errors.Add(e);
            return errors;
        }
    }

    /// <summary>How many security-event entries this run has recorded so far.</summary>
    public int SecurityEventCount
    {
        get { lock (_gate) return _securityEvents; }
    }

    private int _securityEvents;

    /// <summary>Who ran it (letters, digits and <c>. _ - @ /</c>; see <see cref="AuditEvent.DefaultActor"/>).</summary>
    public string Actor { get; }

    /// <summary>What was run, for example <c>estimate</c>.</summary>
    public string Action { get; }

    /// <summary>The trust class of what the run produces.</summary>
    public ProvenanceTag Provenance { get; set; }

    /// <summary>The full path of the open log.</summary>
    public string FilePath => _log.FilePath;

    /// <summary>The anchor (entry count and head hash) after <see cref="Finish"/>, or null before it.</summary>
    public AuditAnchor? FinalAnchor { get; private set; }

    /// <summary>The grid instant the result describes, if any (for example a measurement set's timestamp).</summary>
    public DateTime? StateTimeUtc
    {
        get { lock (_gate) return _stateTimeUtc; }
        set { lock (_gate) _stateTimeUtc = value; }
    }

    /// <summary>
    /// Open the audit log at <paramref name="path"/> (verified first; see <see cref="AuditLogWriter.Open"/>) and
    /// validate the actor and action, before any work is done. Every failure is loud: a bad actor or action
    /// (<see cref="ArgumentException"/>), a log that does not verify (<see cref="AuditLogTamperException"/>), a log
    /// another writer holds (<see cref="InvalidOperationException"/>) or a missing directory
    /// (<see cref="DirectoryNotFoundException"/>).
    /// </summary>
    /// <remarks>
    /// D-DOC-062 (October 2026): the log is opened WITH its anchor (<see cref="AuditAnchorFile"/>):
    /// <paramref name="anchorPath"/>, or <c>&lt;log&gt;.anchor</c> by default. When the anchor file exists, the log must
    /// still hold every entry it records with the same head hash, otherwise <see cref="AuditLogTamperException"/> (tail
    /// truncation, a restored older copy, a rewrite). Before, the log was opened with no anchor, so appending to a
    /// tail-truncated log went unnoticed. A malformed anchor file is refused (<see cref="InvalidOperationException"/>).
    /// A log with entries but no anchor file is accepted and flagged (<see cref="AnchorWasMissing"/>).
    /// </remarks>
    public static AuditRunRecorder Start(string path, string actor, string action, ProvenanceTag provenance,
        string? anchorPath = null)
        => Start(path, actor, action, provenance, anchorPath, AuditRecorderOptions.None);

    /// <summary>
    /// <see cref="Start(string, string, string, ProvenanceTag, string?)"/> with forwarding sinks and anchor signing
    /// (<see cref="AuditRecorderOptions"/>). With a verification key, the anchor's detached signature must exist and
    /// verify before the run does any work, or <see cref="AuditLogTamperException"/>: a log and anchor rewritten
    /// together without the private key are refused here. A log that has entries but no anchor at all is also refused
    /// when a verification key is configured (there is nothing signed to check it against).
    /// </summary>
    public static AuditRunRecorder Start(string path, string actor, string action, ProvenanceTag provenance,
        string? anchorPath, AuditRecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Validate the fixed fields first (an empty input list and a placeholder outcome), so a bad actor or
        // action is refused before the log file is even touched.
        _ = AuditEvent.FormatSummary(actor, action, Array.Empty<AuditInput>(), "exit=0");
        if (!Enum.IsDefined(provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Not a defined ProvenanceTag value.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string anchorFile = anchorPath is null ? AuditAnchorFile.DefaultPathFor(path) : Path.GetFullPath(anchorPath);
        string? anchorDir = Path.GetDirectoryName(anchorFile);
        if (anchorDir is null || !Directory.Exists(anchorDir))
            throw new DirectoryNotFoundException($"Cannot use audit anchor '{anchorFile}': its directory does not exist.");
        // Read the anchor BEFORE opening the log, and open the log against it: AuditLogWriter.Open verifies the whole
        // file and, with an anchor, also that the anchored entries are all still there and unchanged.
        AuditAnchor? expected = AuditAnchorFile.ReadIfExists(anchorFile);
        string signaturePath = options.SignaturePath is null
            ? SignedAuditAnchor.DefaultPathFor(path)
            : Path.GetFullPath(options.SignaturePath);
        if (options.AnchorVerificationKey is System.Security.Cryptography.ECDsa verifier && expected is not null)
        {
            // The signature is checked BEFORE the log is opened: an anchor that is not the one the key holder signed
            // means the log and anchor may have been rewritten together, which the hash chain alone cannot see.
            string? sig = SignedAuditAnchor.ReadIfExists(signaturePath);
            if (sig is null)
                throw new AuditLogTamperException(Path.GetFullPath(path), expected.Count,
                    $"the anchor has no signature file '{signaturePath}', and a verification key is configured");
            if (!SignedAuditAnchor.Verify(expected, sig, verifier, out string why))
                throw new AuditLogTamperException(Path.GetFullPath(path), expected.Count, why);
        }
        AuditLogWriter log = AuditLogWriter.Open(path, expected, options.Sinks);
        bool missing = expected is null && log.Count > 0;
        if (missing && options.AnchorVerificationKey is not null)
        {
            int entries = log.Count;
            log.Dispose();
            throw new AuditLogTamperException(Path.GetFullPath(path), entries,
                $"the log has entries but no anchor file '{anchorFile}', so its signature cannot be checked");
        }
        return new AuditRunRecorder(log, actor, action, provenance, anchorFile, missing, options, signaturePath);
    }

    // Rewrite the anchor after an append (caller holds _gate). A failure is kept, not thrown: the entry is already
    // durable, and Finish may be running from a process-exit handler where an exception would crash the process.
    private void WriteAnchor()
    {
        try
        {
            AuditAnchor anchor = _log.Anchor;
            AuditAnchorFile.Write(AnchorPath, anchor);
            // Sign the anchor just written, so the signature always names the current anchor.
            if (_options.AnchorSigningKey is System.Security.Cryptography.ECDsa signer)
                SignedAuditAnchor.Write(SignaturePath, anchor, signer);
            AnchorWriteError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AnchorWriteError = ex.Message;
        }
    }

    // The outcome text with the D-DOC-062 marker appended when this run could not check the log against an anchor.
    private string WithAnchorMarker(string outcome) => AnchorWasMissing ? outcome + " audit-anchor=missing" : outcome;

    /// <summary>Add one named input (its SHA-256). Two inputs with the same name fail loud at once.</summary>
    public void AddInput(AuditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (_finished)
                throw new InvalidOperationException("The run has already been recorded; an input added now would be lost.");
            foreach (AuditInput existing in _inputs)
                if (existing.Name == input.Name)
                    throw new ArgumentException($"Two inputs are both named '{input.Name}'.", nameof(input));
            _inputs.Add(input);
        }
    }

    /// <summary>
    /// Set the run's own one-line result (for example <c>method=Wls status=Converged J=12.34</c>). Replaces any
    /// earlier value. A line break or other control character fails loud here, not at exit.
    /// </summary>
    public void SetOutcome(string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        foreach (char c in outcome)
            if (char.IsControl(c))
                throw new ArgumentException("The outcome must be one line with no control characters.", nameof(outcome));
        lock (_gate) _outcome = outcome;
    }

    /// <summary>
    /// Write an extra entry now, without finishing the run (for example <c>interpose</c> records
    /// <c>outcome=started</c> when it starts listening, then the final entry when it stops). Its inputs are the
    /// inputs added so far.
    /// </summary>
    public AuditLogEntry RecordNow(string outcome)
    {
        lock (_gate)
        {
            if (_finished)
                throw new InvalidOperationException("The run has already been recorded and the log closed.");
            AuditLogEntry entry = AuditEvent.Record(_log, Actor, Action, _inputs.ToArray(), WithAnchorMarker(outcome), Provenance, _stateTimeUtc);
            WriteAnchor();   // D-DOC-062
            return entry;
        }
    }

    /// <summary>
    /// Record one security event in this run's log now, as its own entry (<see cref="SecurityAudit"/>:
    /// <c>action=security</c>, <c>event=&lt;kind&gt; severity=&lt;severity&gt; reason=&lt;text&gt;</c>), and rewrite (and,
    /// with a signing key, re-sign) the anchor. This is how a detection site in a product run puts one entry per
    /// security event in the trail (NCSC CAF C1) instead of only a count in the run's final entry. The severity
    /// defaults to the kind's baseline (<see cref="Incident.IncidentPolicy.Classify"/>). Attached sinks (a forwarder,
    /// the incident router) see the entry as soon as it is durable.
    /// </summary>
    /// <param name="kind">What was detected.</param>
    /// <param name="reason">One line, secret-free (for example the rejected stamp and the horizon).</param>
    /// <param name="severity">An explicit severity, or null for the kind's baseline.</param>
    public AuditLogEntry RecordSecurityEvent(SecurityEventKind kind, string reason, SecuritySeverity? severity = null)
    {
        lock (_gate)
        {
            if (_finished)
                throw new InvalidOperationException("The run has already been recorded and the log closed.");
            SecuritySeverity sev = severity ?? Incident.IncidentPolicy.Classify(kind).Severity;
            AuditLogEntry entry = SecurityAudit.Record(_log, Actor, kind, sev, OneLine(reason), Provenance);
            _securityEvents++;
            WriteAnchor();
            return entry;
        }
    }

    // A reason is free text from a detection site (it may quote an exception message): fold any line break or other
    // control character to a space so the entry stays one line, rather than failing the detection itself.
    private static string OneLine(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var sb = new StringBuilder(reason.Length);
        foreach (char c in reason) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Write the run's single entry with outcome <c>exit=&lt;exitCode&gt;</c> plus the run's own outcome text
    /// (when set) and close the log. Returns the log's anchor after the entry (keep it outside the log so tail
    /// truncation can be detected later), or null when the run was already recorded: only the first call writes.
    /// </summary>
    public AuditAnchor? Finish(int exitCode)
    {
        lock (_gate)
        {
            if (_finished)
                return null;
            _finished = true;
            try
            {
                string outcome = string.Create(CultureInfo.InvariantCulture, $"exit={exitCode}")
                    + (_outcome is null ? "" : " " + _outcome);
                AuditEvent.Record(_log, Actor, Action, _inputs.ToArray(), WithAnchorMarker(outcome), Provenance, _stateTimeUtc);
                FinalAnchor = _log.Anchor;
                WriteAnchor();   // D-DOC-062: the next run opens the log against this anchor
                return FinalAnchor;
            }
            finally
            {
                _log.Dispose();
            }
        }
    }

    /// <summary>
    /// A named input for a case given on the command line, whatever its form: a file is hashed by its bytes
    /// (<see cref="AuditInput.OfFile"/>), a directory by <see cref="HashDirectory"/>, and anything else (a bundled
    /// case name such as <c>ieee14</c>) by the UTF-8 bytes of the name itself, recorded under
    /// <c>&lt;name&gt;-id</c> so the log never claims a content hash it did not take.
    /// </summary>
    public static AuditInput InputForArgument(string name, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        if (File.Exists(argument))
            return AuditInput.OfFile(name, argument);
        if (Directory.Exists(argument))
            return new AuditInput(name, HashDirectory(argument));
        return AuditInput.OfText(name + "-id", argument);
    }

    /// <summary>
    /// SHA-256 of a directory tree, independent of the file system's enumeration order: every file below
    /// <paramref name="directory"/> contributes one line <c>&lt;relative path with / separators&gt;:&lt;SHA-256 of its
    /// bytes&gt;\n</c>, the lines are sorted ordinally, and the hash is taken over their UTF-8 bytes. Any change to a
    /// file's content, name or presence changes the hash. An empty directory hashes the empty string.
    /// </summary>
    public static string HashDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Cannot hash directory '{directory}': it does not exist.");
        var lines = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string native = Path.GetRelativePath(root, file);
            // GR-847 (October 2026): a line is "<path>:<64 hex digits>\n", so it is unambiguous only while the path holds
            // no line break (a name with a newline and a colon could forge a second line) and, on Linux and macOS, no
            // backslash (which is a legal name character there but would become '/', so "a\b" would hash as "a/b").
            // Such a name is refused rather than escaped, so every hash recorded so far keeps its meaning.
            foreach (char c in native)
                if (char.IsControl(c) || (c == '\\' && !OperatingSystem.IsWindows()))
                    throw new InvalidDataException(
                        $"Cannot hash directory '{directory}': the file name '{native.Replace("\n", "\\n", StringComparison.Ordinal)}' " +
                        "holds a control character or a backslash, which would make the directory hash ambiguous. Rename it.");
            string rel = native.Replace('\\', '/');
            using FileStream stream = File.OpenRead(file);
            lines.Add(rel + ":" + Convert.ToHexString(SHA256.HashData(stream)) + "\n");
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines))));
    }
}
