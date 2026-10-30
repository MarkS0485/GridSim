using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics.Incident;

/// <summary>The class of an incident an Operator of Essential Services may need to assess for NIS Regulation 11
/// notification. One per detection GridSim can raise from inside the engine.</summary>
public enum IncidentCategory
{
    /// <summary>The integrity of recorded data or the audit trail is in doubt.</summary>
    DataIntegrity = 0,

    /// <summary>A suspected residual-cloaked False Data Injection attack (from the FDIA screen).</summary>
    SuspectedFdia = 1,

    /// <summary>An estimate converged to a physically infeasible state.</summary>
    InfeasibleState = 2,

    /// <summary>The audit hash chain or anchor failed to verify.</summary>
    AuditChainBroken = 3,

    /// <summary>An input newer than the 169 h horizon was refused.</summary>
    HorizonViolation = 4,

    /// <summary>A service-level objective was breached (availability degraded).</summary>
    AvailabilityDegraded = 5,

    /// <summary>A known-vulnerable dependency was flagged.</summary>
    DependencyAlert = 6,

    /// <summary>An access request was denied by the role guard.</summary>
    AccessDenied = 7,

    /// <summary>A measurement source was marked distrusted.</summary>
    DistrustedSource = 8,
}

/// <summary>
/// A structured incident GridSim raises for a deploying OES's reporting process (NCSC CAF C1/D1; NIS
/// Regulation 11 - notify the competent authority of a significant-impact incident within 72 hours). GridSim
/// detects and raises; the OES's <see cref="IIncidentSink"/> implementation reports. The report carries enough
/// to assess significance without leaking a grid claim newer than the horizon.
///
/// <para><b>Restriction aware by construction.</b> <see cref="RaisedUtc"/> is run/wall-clock metadata.
/// <see cref="StateTimeUtc"/>, when present, is a permitted-age (present/past) grid instant and is checked
/// against the forward-inference horizon at construction, so no incident can carry a future-dated grid claim.
/// A horizon-violation incident records the rejected stamp and the horizon inside <see cref="DetailJson"/>,
/// never as <see cref="StateTimeUtc"/>.</para>
/// </summary>
/// <param name="Id">A deterministic id derived from the incident's content (identical events get identical ids).</param>
/// <param name="RaisedUtc">When the incident was raised (wall-clock metadata, NOT a grid claim).</param>
/// <param name="StateTimeUtc">A permitted-age grid instant the incident concerns, or null.</param>
/// <param name="Category">The incident class.</param>
/// <param name="Severity">How serious it is (<see cref="SecuritySeverity.Significant"/> is the Reg 11 threshold).</param>
/// <param name="Summary">A one-line, secret-free description.</param>
/// <param name="DetailJson">Structured, secret-free detail (JSON), or an empty object.</param>
/// <param name="AuditHash">The hash of the audit entry that evidences this incident, or empty when none.</param>
public sealed record IncidentReport(
    string Id,
    DateTime RaisedUtc,
    DateTime? StateTimeUtc,
    IncidentCategory Category,
    SecuritySeverity Severity,
    string Summary,
    string DetailJson,
    string AuditHash)
{
    /// <summary>
    /// Build an incident, checking every invariant up front (fail loud) and deriving a deterministic
    /// <see cref="Id"/> from the content so the same event always produces the same id.
    /// </summary>
    /// <param name="raisedUtc">The wall-clock instant the incident was raised (injected for determinism).</param>
    /// <param name="utcNowForHorizon">The "now" used to horizon-check <paramref name="stateTimeUtc"/>; null uses
    /// the real clock. Supplied explicitly in tests so the boundary is fixed.</param>
    public static IncidentReport Create(
        DateTime raisedUtc,
        IncidentCategory category,
        SecuritySeverity severity,
        string summary,
        DateTime? stateTimeUtc = null,
        string detailJson = "{}",
        string auditHash = "",
        DateTime? utcNowForHorizon = null)
    {
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "Not a defined IncidentCategory value.");
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Not a defined SecuritySeverity value.");
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(detailJson);
        ArgumentNullException.ThrowIfNull(auditHash);
        // GR-850: the detail is JSON by contract, so a sink that parses it must never meet "{oops".
        try
        {
            using System.Text.Json.JsonDocument _ = System.Text.Json.JsonDocument.Parse(detailJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException($"The incident detail is not valid JSON: {ex.Message}", nameof(detailJson), ex);
        }
        foreach (char c in summary)
            if (char.IsControl(c))
                throw new ArgumentException("The summary must be one line with no control characters.", nameof(summary));

        DateTime raised = UtcTime.Normalise(raisedUtc);
        DateTime? state = null;
        if (stateTimeUtc is DateTime s)
        {
            state = UtcTime.Normalise(s);
            // No incident may carry a future-dated grid claim. A grid instant an incident concerns must be at
            // least one horizon old, exactly like any datum the engine will touch.
            ForwardInferenceGuard.RejectFuture(state.Value, utcNowForHorizon,
                "an incident's StateTimeUtc (the grid instant it concerns)");
        }

        string id = DeriveId(raised, state, category, severity, summary, detailJson, auditHash);
        return new IncidentReport(id, raised, state, category, severity, summary, detailJson, auditHash);
    }

    private static string DeriveId(DateTime raised, DateTime? state, IncidentCategory category,
        SecuritySeverity severity, string summary, string detailJson, string auditHash)
    {
        string payload = string.Join('|',
            raised.ToString("o", CultureInfo.InvariantCulture),
            state is DateTime s ? s.ToString("o", CultureInfo.InvariantCulture) : "-",
            category.ToString(),
            severity.ToString(),
            summary,
            detailJson,
            auditHash);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        // A short, stable, printable id (first 16 bytes as hex): enough to distinguish incidents, deterministic.
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// The channel GridSim raises an incident on. A deploying OES supplies the implementation (a SIEM/SOC
/// forwarder, syslog, a file, a ticket system). GridSim ships <see cref="NullIncidentSink"/> (the default) and
/// <see cref="BufferedIncidentSink"/> (for tests and in-process consumers).
/// </summary>
public interface IIncidentSink
{
    /// <summary>Publish one incident. Must not throw into the caller: a reporting-channel failure must not stop
    /// estimation. An implementation that cannot deliver should buffer or drop and record its own error.</summary>
    void Publish(IncidentReport report);
}

/// <summary>An incident sink that does nothing. The default when no reporting channel is configured.</summary>
public sealed class NullIncidentSink : IIncidentSink
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullIncidentSink Instance = new();

    private NullIncidentSink() { }

    /// <inheritdoc />
    public void Publish(IncidentReport report) { }
}

/// <summary>An incident sink that keeps every published incident in memory, in order. For tests and in-process
/// consumers.</summary>
public sealed class BufferedIncidentSink : IIncidentSink
{
    private readonly Lock _gate = new();
    private readonly List<IncidentReport> _reports = new();

    /// <summary>A snapshot of the incidents published so far, in order.</summary>
    public IReadOnlyList<IncidentReport> Reports
    {
        get { lock (_gate) return _reports.ToArray(); }
    }

    /// <inheritdoc />
    public void Publish(IncidentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate) _reports.Add(report);
    }
}
