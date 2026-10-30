namespace GridSim.Core.Diagnostics;

/// <summary>
/// A secondary observer of audit entries as they are durably appended to an <see cref="AuditLogWriter"/>
/// (NCSC CAF C1: forwarding the evidence trail to a monitoring function). A deploying party supplies an
/// implementation to forward entries to a SIEM / SOC, a syslog collector, or a write-once (WORM) or remote
/// append-only store, so a rewrite of the local log and its anchor together can still be caught against an
/// independent copy (see <see cref="AuditLogFile"/> section 6.2 of <c>docs/assurance/caf-mapping.md</c>).
///
/// <para><b>The on-disk chain is authoritative; a sink is secondary.</b> A sink is notified only AFTER the
/// entry is durable on disk and the hash chain has advanced, so a sink can never affect what is recorded.
/// A sink that throws does NOT fault the log or fail the append: the authoritative record must not depend on
/// an external forwarder being reachable (a syslog outage must not stop estimation). The writer keeps the
/// first sink error for inspection (<see cref="AuditLogWriter.LastSinkError"/>); it does not hide it, but it
/// does not let it corrupt the durable trail. This is the one deliberate departure from the fail-loud rule in
/// the audit path, and it is deliberate: durability of the primary record outranks delivery to a secondary.</para>
///
/// <para><b>No secrets.</b> An entry's summary is plain text (hashed, not encrypted). A sink that forwards it
/// off the host must treat it as it would any audit record; it must never be used to carry a credential, which
/// the writers already forbid.</para>
/// </summary>
public interface IAuditSink
{
    /// <summary>Observe one entry that has just been durably appended. Must not throw into the caller; any
    /// failure is the sink's own concern. Called under the writer's append lock, so an implementation must be
    /// quick and must not call back into the same writer.</summary>
    /// <param name="entry">The entry exactly as written to the chain.</param>
    void OnEntry(AuditLogEntry entry);
}

/// <summary>A sink that does nothing. The default when no monitoring forwarder is configured.</summary>
public sealed class NullAuditSink : IAuditSink
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullAuditSink Instance = new();

    private NullAuditSink() { }

    /// <inheritdoc />
    public void OnEntry(AuditLogEntry entry) { }
}

/// <summary>A sink that keeps every observed entry in memory, in order. For tests and for an in-process
/// consumer such as <c>SecurityEventMonitor</c> that aggregates the trail without a disk round trip.</summary>
public sealed class BufferedAuditSink : IAuditSink
{
    private readonly Lock _gate = new();
    private readonly List<AuditLogEntry> _entries = new();

    /// <summary>A snapshot of the entries observed so far, in append order.</summary>
    public IReadOnlyList<AuditLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <inheritdoc />
    public void OnEntry(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) _entries.Add(entry);
    }
}
