using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The security-event monitor (NCSC CAF C2 proactive discovery / threat hunting): it folds the audit trail,
/// tallies each security-event kind, and escalates a burst into exactly one latched incident. Three suspected
/// FDIA events (burst threshold 3) raise one incident; two raise none; the per-kind tally is reported; and the
/// same sequence of entries always produces the same number of incidents (determinism).
/// </summary>
public sealed class SecurityEventMonitorTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Build a security-event audit entry exactly as the on-disk writer would, in the form the monitor reads
    // (it only looks at Summary, RunTimestampUtc and Hash).
    private static AuditLogEntry SecurityEntry(int seq, SecurityEventKind kind, SecuritySeverity severity, string reason)
    {
        string summary = AuditEvent.FormatSummary("actor", SecurityAudit.SecurityAction, Array.Empty<AuditInput>(),
            SecurityAudit.FormatOutcome(kind, severity, reason));
        return new AuditLogEntry(seq, T0.AddMinutes(seq), null, ProvenanceTag.DerivedFromRecorded, summary, "GENESIS", "HASH");
    }

    private static List<AuditLogEntry> Burst(SecurityEventKind kind, SecuritySeverity severity, int count)
    {
        var entries = new List<AuditLogEntry>(count);
        for (int i = 0; i < count; i++)
            entries.Add(SecurityEntry(i, kind, severity, $"event {i}"));
        return entries;
    }

    private static (SecurityEventMonitor Monitor, BufferedIncidentSink Sink) NewMonitor()
    {
        var sink = new BufferedIncidentSink();
        var router = new IncidentRouter(sink);
        return (new SecurityEventMonitor(router), sink);
    }

    [Fact]
    public void Three_suspected_fdia_events_raise_exactly_one_latched_incident()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor();

        foreach (AuditLogEntry entry in Burst(SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant, 3))
            monitor.OnEntry(entry);

        // Exactly one incident for the burst (latched: a fourth event would not raise a second).
        Assert.Single(sink.Reports);
        Assert.Equal(3, monitor.CountOf(SecurityEventKind.SuspectedFdia));

        monitor.OnEntry(SecurityEntry(3, SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant, "event 3"));
        Assert.Single(sink.Reports);
        Assert.Equal(4, monitor.CountOf(SecurityEventKind.SuspectedFdia));
    }

    [Fact]
    public void Two_suspected_fdia_events_raise_no_incident()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor();

        foreach (AuditLogEntry entry in Burst(SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant, 2))
            monitor.OnEntry(entry);

        Assert.Empty(sink.Reports);
        Assert.Equal(2, monitor.CountOf(SecurityEventKind.SuspectedFdia));
    }

    [Fact]
    public void A_run_entry_is_not_tallied_as_a_security_event()
    {
        (SecurityEventMonitor monitor, BufferedIncidentSink sink) = NewMonitor();

        string runSummary = AuditEvent.FormatSummary("actor", "estimate", Array.Empty<AuditInput>(), "status=Converged");
        var runEntry = new AuditLogEntry(0, T0, null, ProvenanceTag.DerivedFromRecorded, runSummary, "GENESIS", "HASH");
        monitor.OnEntry(runEntry);

        Assert.Empty(sink.Reports);
        Assert.Equal(0, monitor.CountOf(SecurityEventKind.SuspectedFdia));
    }

    [Fact]
    public void The_same_entry_sequence_produces_the_same_number_of_incidents()
    {
        List<AuditLogEntry> entries = Burst(SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant, 5);

        (SecurityEventMonitor a, BufferedIncidentSink sinkA) = NewMonitor();
        a.Observe(entries);
        (SecurityEventMonitor b, BufferedIncidentSink sinkB) = NewMonitor();
        b.Observe(entries);

        Assert.Equal(sinkA.Reports.Count, sinkB.Reports.Count);
        Assert.Single(sinkA.Reports);   // one latched incident for the whole run
    }
}
