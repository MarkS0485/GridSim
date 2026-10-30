using System;
using System.Collections.Generic;
using System.IO;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// One audit entry per security event (NCSC CAF C1): <see cref="SecurityAudit.Record"/> writes a security event
/// into the persistent, hash-chained log with its kind, severity and secret-free reason, in a fixed outcome
/// layout a monitor can read back with <see cref="SecurityAudit.TryRead"/>. A security entry round-trips through
/// the on-disk log and still verifies; an ordinary run entry is not read as a security event.
/// </summary>
public sealed class SecurityAuditTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Each test gets its own scratch directory, removed afterwards (as AuditLogFileTests does).
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-secaudit-{Guid.NewGuid():N}");

    public SecurityAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string LogPath(string name = "audit.jsonl") => Path.Combine(_dir, name);

    [Fact]
    public void A_recorded_security_event_reopens_verifies_and_reads_back_its_kind_and_severity()
    {
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            AuditLogEntry written = SecurityAudit.Record(writer, T0, "mark@host",
                SecurityEventKind.SuspectedFdia, SecuritySeverity.Significant,
                "chi-square PASSED but bus 4 Vm 1.35 pu is 25 sigma out of band");

            // The outcome layout is the public FormatOutcome string, inside the standard event summary.
            string expectedOutcome = SecurityAudit.FormatOutcome(SecurityEventKind.SuspectedFdia,
                SecuritySeverity.Significant, "chi-square PASSED but bus 4 Vm 1.35 pu is 25 sigma out of band");
            Assert.Contains(" action=" + SecurityAudit.SecurityAction + " ", written.Summary, StringComparison.Ordinal);
            Assert.Contains("outcome=" + expectedOutcome, written.Summary, StringComparison.Ordinal);
        }

        // The log still verifies after the security event was appended.
        AuditLogVerification v = AuditLogFile.Verify(path);
        Assert.True(v.IsValid, v.Reason);
        AuditLogEntry loaded = Assert.Single(AuditLogFile.Load(path));

        Assert.True(SecurityAudit.TryRead(loaded, out SecurityEventKind kind, out SecuritySeverity severity));
        Assert.Equal(SecurityEventKind.SuspectedFdia, kind);
        Assert.Equal(SecuritySeverity.Significant, severity);
    }

    [Fact]
    public void Try_read_returns_the_right_kind_and_severity_for_every_pairing()
    {
        string path = LogPath();
        var expected = new List<(SecurityEventKind Kind, SecuritySeverity Severity)>();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            int minute = 0;
            foreach (SecurityEventKind kind in Enum.GetValues<SecurityEventKind>())
                foreach (SecuritySeverity severity in Enum.GetValues<SecuritySeverity>())
                {
                    SecurityAudit.Record(writer, T0.AddMinutes(minute++), "actor", kind, severity, $"reason {kind}/{severity}");
                    expected.Add((kind, severity));
                }
        }

        IReadOnlyList<AuditLogEntry> loaded = AuditLogFile.Load(path);
        Assert.Equal(expected.Count, loaded.Count);
        for (int i = 0; i < loaded.Count; i++)
        {
            Assert.True(SecurityAudit.TryRead(loaded[i], out SecurityEventKind kind, out SecuritySeverity severity));
            Assert.Equal(expected[i].Kind, kind);
            Assert.Equal(expected[i].Severity, severity);
        }
    }

    [Fact]
    public void A_normal_run_entry_is_not_read_as_a_security_event()
    {
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
            AuditEvent.Record(writer, T0, "mark@host", "estimate", Array.Empty<AuditInput>(),
                "status=Converged", ProvenanceTag.DerivedFromRecorded);

        AuditLogEntry loaded = Assert.Single(AuditLogFile.Load(path));
        Assert.False(SecurityAudit.TryRead(loaded, out SecurityEventKind kind, out SecuritySeverity severity));
        Assert.Equal(default, kind);
        Assert.Equal(default, severity);
    }

    [Fact]
    public void A_refusal_event_records_no_state_time_but_keeps_the_rejected_stamp_in_its_reason()
    {
        // A horizon-violation refusal carries no future-dated grid claim: the rejected stamp lives inside the
        // free-text reason, never as the entry's StateTimeUtc.
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
            SecurityAudit.Record(writer, T0, "ingest", SecurityEventKind.HorizonViolation, SecuritySeverity.Info,
                "rejected stamp 2026-06-01T00:00:00Z newer than horizon 2026-05-25T00:00:00Z");

        AuditLogEntry loaded = Assert.Single(AuditLogFile.Load(path));
        Assert.Null(loaded.StateTimeUtc);
        Assert.Contains("rejected stamp", loaded.Summary, StringComparison.Ordinal);
        Assert.True(SecurityAudit.TryRead(loaded, out SecurityEventKind kind, out _));
        Assert.Equal(SecurityEventKind.HorizonViolation, kind);
    }

    [Fact]
    public void An_undefined_kind_or_severity_fails_loud_and_writes_nothing()
    {
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SecurityAudit.Record(writer, T0, "a", (SecurityEventKind)999, SecuritySeverity.Info, "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SecurityAudit.Record(writer, T0, "a", SecurityEventKind.AccessDenied, (SecuritySeverity)999, "x"));
            Assert.Throws<ArgumentException>(() =>
                SecurityAudit.Record(writer, T0, "a", SecurityEventKind.AccessDenied, SecuritySeverity.Info, " "));
            Assert.Equal(0, writer.Count);
        }
        Assert.Equal(0L, new FileInfo(path).Length);
    }
}
