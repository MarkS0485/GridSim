namespace GridSim.Core.Diagnostics.Incident;

/// <summary>
/// Watches the audit trail for PATTERNS the per-event entries do not raise on their own, and escalates a run of
/// events into one incident (NCSC CAF C2 proactive security event discovery / threat hunting). It is an
/// <see cref="IAuditSink"/>, so it can be attached directly to an <see cref="AuditLogWriter"/> and fold every
/// entry as it is appended, or be fed entries from a <see cref="BufferedAuditSink"/> or a loaded log. It
/// consumes the trail; it does not replace it - the authoritative record is still the hash chain.
///
/// <para><b>Deterministic.</b> Thresholds are crossed on cumulative counts of each security-event kind, latched
/// so one burst raises exactly one incident, and the incident's raised instant is the triggering entry's own
/// <see cref="AuditLogEntry.RunTimestampUtc"/> (already in the log), never a fresh wall-clock read. The same
/// sequence of entries therefore always produces the same incident sequence.</para>
/// </summary>
public sealed class SecurityEventMonitor : IAuditSink
{
    private readonly Lock _gate = new();
    private readonly IncidentRouter _router;
    private readonly IncidentPolicy _policy;
    private readonly Dictionary<SecurityEventKind, int> _counts = new();
    private readonly HashSet<SecurityEventKind> _latched = new();

    /// <summary>Create a monitor that escalates bursts through <paramref name="router"/>. The policy is the
    /// router's own policy unless <paramref name="policy"/> overrides it.</summary>
    public SecurityEventMonitor(IncidentRouter router, IncidentPolicy? policy = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _policy = policy ?? router.Policy;
    }

    /// <summary>The cumulative count of a security-event kind seen so far.</summary>
    public int CountOf(SecurityEventKind kind)
    {
        lock (_gate) return _counts.TryGetValue(kind, out int n) ? n : 0;
    }

    /// <inheritdoc />
    public void OnEntry(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!SecurityAudit.TryRead(entry, out SecurityEventKind kind, out _))
            return;   // a run entry or a non-security event: nothing to tally

        SecurityEventKind? escalate = null;
        DateTime raisedUtc = entry.RunTimestampUtc;
        int count;
        lock (_gate)
        {
            count = _counts.TryGetValue(kind, out int n) ? n + 1 : 1;
            _counts[kind] = count;

            int threshold = _policy.BurstThreshold(kind);
            if (threshold > 0 && count >= threshold && _latched.Add(kind))
                escalate = kind;
        }

        if (escalate is SecurityEventKind k)
        {
            // Escalate the burst to one incident, at the severity the kind warrants when it recurs.
            SecuritySeverity severity = k == SecurityEventKind.SuspectedFdia || k == SecurityEventKind.AuditChainBroken
                ? SecuritySeverity.Critical
                : SecuritySeverity.Significant;
            string detail = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"kind\":\"{k}\",\"count\":{count},\"threshold\":{_policy.BurstThreshold(k)}}}");
            _router.Raise(
                raisedUtc,
                k,
                summary: string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"security-event burst: {count} {k} events reached the alert threshold"),
                detailJson: detail,
                auditHash: entry.Hash,
                severityOverride: severity);
        }
    }

    /// <summary>Fold an existing sequence of entries (for example a loaded log) through the monitor, in order.</summary>
    public void Observe(IReadOnlyList<AuditLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (AuditLogEntry entry in entries)
            OnEntry(entry);
    }
}
