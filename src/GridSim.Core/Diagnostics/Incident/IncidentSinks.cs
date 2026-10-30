using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.Diagnostics.Incident;

/// <summary>
/// An <see cref="IIncidentSink"/> that appends every incident, as one JSON line, to a file: the reporting channel a
/// deploying OES points its SOC, ticketing or notification process at (NCSC CAF C1/D1; NIS Regulation 11). Each line
/// is flushed to disk before <see cref="Publish"/> returns.
///
/// <para>As the <see cref="IIncidentSink"/> contract requires, a write failure never stops the engine: it is kept in
/// <see cref="LastError"/>, which the product paths report when the run ends.</para>
/// </summary>
public sealed class JsonLinesIncidentSink : IIncidentSink, IDeliveryChannel
{
    private readonly Lock _gate = new();
    private string? _lastError;
    private int _published;

    /// <summary>Publish to <paramref name="path"/>, creating it if needed (its directory must exist).</summary>
    public JsonLinesIncidentSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(FilePath);
        if (dir is null || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Cannot write incidents to '{FilePath}': its directory does not exist.");
    }

    /// <summary>The full path of the incident file.</summary>
    public string FilePath { get; }

    /// <summary>The first write failure, or null when every incident was written.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>How many incidents were written.</summary>
    public int Published
    {
        get { lock (_gate) return _published; }
    }

    /// <summary>The JSON line for one incident (no trailing newline). <see cref="IncidentReport.DetailJson"/> is
    /// embedded as a JSON value when it parses, and as a string otherwise, so the line is always valid JSON.</summary>
    public static string Format(IncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", report.Id);
            w.WriteString("raisedUtc", report.RaisedUtc.ToString("o", CultureInfo.InvariantCulture));
            if (report.StateTimeUtc is DateTime st)
                w.WriteString("stateTimeUtc", st.ToString("o", CultureInfo.InvariantCulture));
            else
                w.WriteNull("stateTimeUtc");
            w.WriteString("category", report.Category.ToString());
            w.WriteString("severity", report.Severity.ToString());
            w.WriteString("summary", report.Summary);
            w.WritePropertyName("detail");
            try
            {
                using JsonDocument detail = JsonDocument.Parse(report.DetailJson);
                detail.RootElement.WriteTo(w);
            }
            catch (JsonException)
            {
                w.WriteStringValue(report.DetailJson);
            }
            w.WriteString("auditHash", report.AuditHash);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc />
    public void Publish(IncidentReport report)
    {
        if (report is null) return;   // the contract: never throw into the engine
        lock (_gate)
        {
            try
            {
                byte[] line = Encoding.UTF8.GetBytes(Format(report) + "\n");
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(line);
                fs.Flush(flushToDisk: true);
                _published++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastError ??= $"incident write to '{FilePath}' failed: {ex.Message}";
            }
        }
    }
}

/// <summary>
/// The audit-to-incident bridge for SINGLE events: an <see cref="IAuditSink"/> that raises one incident, through an
/// <see cref="IncidentRouter"/>, for every security-event entry whose kind is significant on its own
/// (<see cref="IncidentPolicy.Classify"/> baseline <see cref="SecuritySeverity.Significant"/> or above: a suspected
/// FDIA, a refused restriction, a distrusted source, a breached service-level objective, a dependency alert). Routine
/// kinds (a single refused too-fresh input, an infeasible estimate, an access denial, a quarantined live feed) raise
/// nothing here; a BURST of them is escalated by <see cref="SecurityEventMonitor"/>, which a product path attaches
/// beside this sink. Each incident carries the hash of the audit entry that evidences it, so the incident and the
/// chain are linked both ways.
///
/// <para>Deterministic: the raised instant is the entry's own run timestamp, never a fresh clock read.</para>
/// </summary>
public sealed class SecurityIncidentSink : IAuditSink
{
    private readonly IncidentRouter _router;

    /// <summary>Raise single significant events through <paramref name="router"/>.</summary>
    public SecurityIncidentSink(IncidentRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    /// <inheritdoc />
    public void OnEntry(AuditLogEntry entry)
    {
        if (entry is null || !SecurityAudit.TryRead(entry, out SecurityEventKind kind, out SecuritySeverity severity))
            return;
        (_, SecuritySeverity baseline) = IncidentPolicy.Classify(kind);
        // The recorded severity can raise an event above its baseline (a caller may judge one instance critical).
        SecuritySeverity effective = severity > baseline ? severity : baseline;
        if (effective < SecuritySeverity.Significant)
            return;
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"{{\"kind\":\"{kind}\",\"auditSequence\":{entry.Sequence}}}");
        _router.Raise(entry.RunTimestampUtc, kind,
            summary: string.Create(CultureInfo.InvariantCulture, $"security event {kind} (audit entry {entry.Sequence})"),
            detailJson: detail, auditHash: entry.Hash, severityOverride: effective);
    }
}

/// <summary>
/// Builds the standard incident wiring a product path attaches to its audit log: an <see cref="IncidentRouter"/> to
/// <paramref name="sink"/>, a <see cref="SecurityIncidentSink"/> for single significant events, and a
/// <see cref="SecurityEventMonitor"/> for bursts. The CLI's <c>--incident-log</c> and the web host's
/// <c>Audit:IncidentLogPath</c> both use it, so the two raise identical incidents for identical trails.
/// </summary>
public static class IncidentWiring
{
    /// <summary>The two audit sinks (single events, then bursts) that feed <paramref name="sink"/>.</summary>
    public static IReadOnlyList<IAuditSink> AuditSinksFor(IIncidentSink sink, IncidentPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var router = new IncidentRouter(sink, policy);
        return new IAuditSink[] { new SecurityIncidentSink(router), new SecurityEventMonitor(router) };
    }
}
