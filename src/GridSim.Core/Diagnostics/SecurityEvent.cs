using System.Globalization;

namespace GridSim.Core.Diagnostics;

/// <summary>How serious a security event is. <see cref="Significant"/> is the threshold a deploying Operator of
/// Essential Services maps to a NIS Regulation 11 significant-incident assessment (notify the competent
/// authority within 72 hours). Shared by the audit trail and the incident router so there is one scale.</summary>
public enum SecuritySeverity
{
    /// <summary>A routine, expected event worth recording but not acting on (for example a single too-fresh
    /// datum refused at ingestion, which is the guard doing its job).</summary>
    Info = 0,

    /// <summary>A low-severity event: notable, below the significance threshold on its own.</summary>
    Low = 1,

    /// <summary>A significant event: the OES should assess it against the Regulation 11 notification duty.</summary>
    Significant = 2,

    /// <summary>A critical event demanding immediate response.</summary>
    Critical = 3,
}

/// <summary>The kind of security event recorded in the audit trail and routed as an incident. Each value names
/// a detection GridSim can make from inside the engine; mapping a kind to an OES incident category and
/// severity is the incident router's job (CAF C1 security monitoring, C2 threat hunting, D1 response).</summary>
public enum SecurityEventKind
{
    /// <summary>An input was refused because its timestamp was newer than the 169 h horizon
    /// (<see cref="ForwardInferenceException"/>). The forward-inference guard working as designed.</summary>
    HorizonViolation = 0,

    /// <summary>A dynamics-into-estimation fusion was refused because it had no reviewed register entry
    /// (<c>RestrictionReview.AssertPermitted</c>).</summary>
    RestrictionRefused = 1,

    /// <summary>The FDIA screen flagged a chi-square-consistent estimate as a suspected residual-cloaked False
    /// Data Injection attack (<c>FdiaScreenReport.SuspectedFdia</c>).</summary>
    SuspectedFdia = 2,

    /// <summary>An estimate converged to a physically infeasible state and was rejected
    /// (<c>EstimationStatus.Infeasible</c>).</summary>
    InfeasibleState = 3,

    /// <summary>An access request was denied by the hosting layer's role guard (<c>AccessDeniedException</c>).</summary>
    AccessDenied = 4,

    /// <summary>An audit chain or anchor failed to verify (tamper evidence fired).</summary>
    AuditChainBroken = 5,

    /// <summary>Present-time live telemetry was captured to the quarantine store instead of the evidence lake
    /// (<c>PmuLiveQuarantine</c> / <c>QuarantineLiveStore</c>).</summary>
    LiveDataQuarantined = 6,

    /// <summary>A measurement source was marked distrusted, so its rows were forced to pseudo-measurement
    /// treatment.</summary>
    DistrustedSource = 7,

    /// <summary>A known-vulnerable dependency was flagged (supply-chain alert, CAF A4).</summary>
    DependencyAlert = 8,

    /// <summary>A service-level objective was breached (availability degraded, CAF B5).</summary>
    AvailabilityDegraded = 9,
}

/// <summary>
/// Records ONE security event in the persistent audit log, so the trail carries one entry per security event
/// with its reason, not only one entry per run (NCSC CAF C1; closes the 6.1 (b)-(f) gap in
/// <c>docs/assurance/caf-mapping.md</c>). The entry's outcome is a fixed, parseable layout so a
/// <c>SecurityEventMonitor</c> can read the kind and severity back:
/// <c>event=&lt;kind&gt; severity=&lt;severity&gt; reason=&lt;free text&gt;</c>, written through
/// <see cref="AuditEvent.FormatSummary"/> with <c>action=security</c>.
///
/// <para><b>Restriction aware.</b> A security entry carries no future-dated grid claim: a horizon-violation
/// event records the horizon and the rejected stamp inside its free-text reason, never as the entry's
/// <c>StateTimeUtc</c>. The optional <paramref name="stateTimeUtc"/> is for an event that genuinely concerns a
/// permitted-age (present/past) grid instant; leave it null for a refusal.</para>
///
/// <para><b>No secrets.</b> The reason is plain text (hashed, not encrypted): never put a credential in it.</para>
/// </summary>
public static class SecurityAudit
{
    /// <summary>The <c>action</c> value every security-event entry carries, so they can be told apart from run
    /// entries at a glance and by the monitor.</summary>
    public const string SecurityAction = "security";

    /// <summary>
    /// Record one security event, stamped with the current UTC time, and return the entry as written.
    /// </summary>
    /// <param name="log">The open audit log.</param>
    /// <param name="actor">Who or what raised it (see <see cref="AuditEvent.DefaultActor"/>).</param>
    /// <param name="kind">The kind of event.</param>
    /// <param name="severity">How serious it is.</param>
    /// <param name="reason">A one-line, secret-free explanation (for example the horizon and the rejected age).</param>
    /// <param name="provenance">The trust class of the data the event concerns; defaults to
    /// <see cref="ProvenanceTag.DerivedFromRecorded"/> (a security event is metadata derived from the data
    /// flowing through the system, not a new datum).</param>
    /// <param name="stateTimeUtc">A permitted-age grid instant the event concerns, or null for a refusal.</param>
    public static AuditLogEntry Record(
        AuditLogWriter log,
        string actor,
        SecurityEventKind kind,
        SecuritySeverity severity,
        string reason,
        ProvenanceTag provenance = ProvenanceTag.DerivedFromRecorded,
        DateTime? stateTimeUtc = null) =>
        Record(log, DateTime.UtcNow, actor, kind, severity, reason, provenance, stateTimeUtc);

    /// <summary>Record one security event with an explicit run timestamp (for tests and deterministic replay).</summary>
    public static AuditLogEntry Record(
        AuditLogWriter log,
        DateTime runTimestampUtc,
        string actor,
        SecurityEventKind kind,
        SecuritySeverity severity,
        string reason,
        ProvenanceTag provenance = ProvenanceTag.DerivedFromRecorded,
        DateTime? stateTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined SecurityEventKind value.");
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Not a defined SecuritySeverity value.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        string outcome = FormatOutcome(kind, severity, reason);
        return AuditEvent.Record(log, runTimestampUtc, actor, SecurityAction, Array.Empty<AuditInput>(),
            outcome, provenance, stateTimeUtc);
    }

    /// <summary>The fixed outcome layout for a security event. Public so the monitor and tests format and parse
    /// the exact same string.</summary>
    public static string FormatOutcome(SecurityEventKind kind, SecuritySeverity severity, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return string.Create(CultureInfo.InvariantCulture, $"event={kind} severity={severity} reason={reason}");
    }

    /// <summary>
    /// Read the kind and severity back from an audit entry, if it is a security event (its summary has
    /// <c>action=security</c> and the fixed outcome layout). Returns false for a run entry or an unparseable one;
    /// it never throws, so a monitor can fold a whole log without guarding every entry.
    /// </summary>
    public static bool TryRead(AuditLogEntry entry, out SecurityEventKind kind, out SecuritySeverity severity)
    {
        kind = default;
        severity = default;
        if (entry is null)
            return false;

        // The summary is: actor=<a> action=security inputs=- outcome=event=<kind> severity=<sev> reason=<text>
        // Parsed POSITIONALLY: the actor token (which can hold no space) and then exactly that prefix. A substring search
        // let a run entry whose free-text outcome merely CONTAINED " action=security ... outcome=event=..." be read as a
        // security event (a spoofed Critical incident from a normal run).
        string summary = entry.Summary;
        if (!summary.StartsWith("actor=", StringComparison.Ordinal))
            return false;
        int actorEnd = summary.IndexOf(' ');
        if (actorEnd < 0)
            return false;
        const string Prefix = "action=" + SecurityAction + " inputs=- outcome=event=";
        if (string.CompareOrdinal(summary, actorEnd + 1, Prefix, 0, Prefix.Length) != 0)
            return false;
        string rest = summary[(actorEnd + 1 + Prefix.Length)..];

        int sevAt = rest.IndexOf(" severity=", StringComparison.Ordinal);
        if (sevAt < 0)
            return false;
        string kindText = rest[..sevAt];
        string afterSev = rest[(sevAt + " severity=".Length)..];
        int reasonAt = afterSev.IndexOf(" reason=", StringComparison.Ordinal);
        if (reasonAt < 0)
            return false;
        string sevText = afterSev[..reasonAt];

        // Names only, exact case, and defined values: Enum.TryParse alone also accepts "7", "-1" and "A, B".
        return TryParseName(kindText, out kind) && TryParseName(sevText, out severity);
    }

    private static bool TryParseName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (text.Length == 0 || !char.IsAsciiLetterUpper(text[0])) return false;
        foreach (char c in text)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value);
    }
}
