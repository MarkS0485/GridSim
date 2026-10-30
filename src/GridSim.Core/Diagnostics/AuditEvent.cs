using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// One named input to an audited run and the SHA-256 of its content, for example the case file or the
/// measurement set an estimate was computed from. Recording the hash (not the content) lets a reviewer later
/// prove which exact input produced a recorded result, without copying the input into the log.
/// </summary>
public sealed record AuditInput
{
    /// <summary>Create an input record. Fails loud on a name that would make the summary ambiguous, or on a
    /// hash that is not 64 upper-case hexadecimal characters (the form <see cref="Convert.ToHexString(byte[])"/>
    /// produces, and the form the log's own hashes use).</summary>
    public AuditInput(string name, string sha256Hex)
    {
        AuditEvent.RequireToken(name, nameof(name), "input name");
        ArgumentNullException.ThrowIfNull(sha256Hex);
        if (sha256Hex.Length != 64 || !IsUpperHex(sha256Hex))
            throw new ArgumentException(
                $"Input '{name}': the SHA-256 must be 64 upper-case hexadecimal characters, got '{sha256Hex}'.",
                nameof(sha256Hex));
        Name = name;
        Sha256Hex = sha256Hex;
    }

    /// <summary>The input's logical name (for example <c>case</c> or <c>measurements</c>), not its path.</summary>
    public string Name { get; }

    /// <summary>SHA-256 of the input's bytes, 64 upper-case hex characters.</summary>
    public string Sha256Hex { get; }

    /// <summary>Hash a file's bytes. A missing file fails loud (<see cref="FileNotFoundException"/>).</summary>
    public static AuditInput OfFile(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return new AuditInput(name, Convert.ToHexString(SHA256.HashData(stream)));
    }

    /// <summary>Hash a byte buffer (for example an input that arrived over the network, never written to disk).</summary>
    public static AuditInput OfBytes(string name, ReadOnlySpan<byte> bytes) =>
        new(name, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>Hash a string as UTF-8 bytes.</summary>
    public static AuditInput OfText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OfBytes(name, Encoding.UTF8.GetBytes(text));
    }

    private static bool IsUpperHex(string s)
    {
        foreach (char c in s)
            if (!(char.IsAsciiDigit(c) || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}

/// <summary>
/// The small, explicit API a product path (a CLI verb, a service loop) uses to record an event in the
/// persistent audit log: WHO ran it, WHAT was done, WHEN (the run timestamp, plus the grid state time when
/// there is one), WHICH inputs (by SHA-256) and the OUTCOME (R075).
///
/// <para>The event becomes the entry's summary, in one fixed, unambiguous layout:
/// <c>actor=&lt;who&gt; action=&lt;what&gt; inputs=&lt;name&gt;:&lt;sha256&gt;,... outcome=&lt;text&gt;</c>
/// (<c>inputs=-</c> when there are none). Because the summary is inside the hash, every one of those fields is
/// covered by the chain. The actor, action and input names are restricted to letters, digits and
/// <c>. _ - @ /</c> so the layout can always be split back into its fields; the outcome is free text on a
/// single line and comes last.</para>
///
/// <para><b>No secrets.</b> The summary is plain text. Never put a password, key, token or connection string in
/// the actor, action or outcome; record a hash of a sensitive input instead.</para>
/// </summary>
public static class AuditEvent
{
    /// <summary>
    /// Record one event, stamped with the current UTC time, and return the entry as written (already on disk).
    /// </summary>
    /// <param name="log">The open audit log.</param>
    /// <param name="actor">Who ran it (see <see cref="DefaultActor"/>).</param>
    /// <param name="action">What was done: the verb, for example <c>estimate</c>, <c>crossval</c>, <c>interpose</c>.</param>
    /// <param name="inputs">The inputs and their hashes (may be empty, never null).</param>
    /// <param name="outcome">The result in one line, for example <c>status=Converged chi2=PASS</c>.</param>
    /// <param name="provenance">The trust class of what was produced.</param>
    /// <param name="stateTimeUtc">The present/past grid instant the result describes, if any.</param>
    public static AuditLogEntry Record(
        AuditLogWriter log,
        string actor,
        string action,
        IReadOnlyList<AuditInput> inputs,
        string outcome,
        ProvenanceTag provenance,
        DateTime? stateTimeUtc = null) =>
        Record(log, DateTime.UtcNow, actor, action, inputs, outcome, provenance, stateTimeUtc);

    /// <summary>
    /// Record one event with an explicit run timestamp (for tests, and for replaying a run whose time is already
    /// known). Validates every field before anything is written, so a bad call leaves the log untouched.
    /// </summary>
    public static AuditLogEntry Record(
        AuditLogWriter log,
        DateTime runTimestampUtc,
        string actor,
        string action,
        IReadOnlyList<AuditInput> inputs,
        string outcome,
        ProvenanceTag provenance,
        DateTime? stateTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        string summary = FormatSummary(actor, action, inputs, outcome);
        return log.Append(runTimestampUtc, stateTimeUtc, provenance, summary);
    }

    /// <summary>
    /// The summary text for an event, in the fixed layout described on <see cref="AuditEvent"/>. Fails loud
    /// (<see cref="ArgumentException"/>) on any field that would make the layout ambiguous, and on two inputs
    /// with the same name.
    /// </summary>
    public static string FormatSummary(string actor, string action, IReadOnlyList<AuditInput> inputs, string outcome)
    {
        RequireToken(actor, nameof(actor), "actor");
        RequireToken(action, nameof(action), "action");
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        // GR-853: the Unicode line and paragraph separators (U+2028, U+2029) break a line as surely as a newline, but
        // are not control characters, so they used to pass; they are refused too.
        foreach (char c in outcome)
            if (char.IsControl(c)
                || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator
                                              or System.Globalization.UnicodeCategory.ParagraphSeparator)
                throw new ArgumentException(
                    "The outcome must be a single line of text with no control characters or line separators (no newlines or tabs).",
                    nameof(outcome));

        var names = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append("actor=").Append(actor)
          .Append(" action=").Append(action)
          .Append(" inputs=");
        if (inputs.Count == 0)
            sb.Append('-');
        for (int i = 0; i < inputs.Count; i++)
        {
            AuditInput input = inputs[i] ?? throw new ArgumentException($"inputs[{i}] is null.", nameof(inputs));
            if (!names.Add(input.Name))
                throw new ArgumentException($"Two inputs are both named '{input.Name}'.", nameof(inputs));
            if (i > 0)
                sb.Append(',');
            sb.Append(input.Name).Append(':').Append(input.Sha256Hex);
        }
        sb.Append(" outcome=").Append(outcome);
        return sb.ToString();
    }

    /// <summary>
    /// The default actor for a local run: <c>user@machine</c>, with any character outside the allowed set
    /// (letters, digits, <c>. _ - @ /</c>) replaced by '_' so a Windows user name with a space still records.
    /// </summary>
    public static string DefaultActor()
    {
        string user = Sanitise(Environment.UserName);
        string machine = Sanitise(Environment.MachineName);
        return $"{user}@{machine}";
    }

    /// <summary>Fail loud unless <paramref name="value"/> is a non-empty run of allowed token characters.</summary>
    internal static void RequireToken(string value, string paramName, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        foreach (char c in value)
            if (!IsTokenChar(c))
                throw new ArgumentException(
                    $"The {what} '{value}' contains '{c}'. Only letters, digits and . _ - @ / are allowed, so the " +
                    $"audit summary can always be split back into its fields.",
                    paramName);
    }

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or '/';

    private static string Sanitise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unknown";
        var chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!IsTokenChar(chars[i]))
                chars[i] = '_';
        return new string(chars);
    }
}
