using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The append-only, hash-chained audit log (CAF objective C evidence trail). A clean log verifies; the chain
/// links entry-to-entry from a genesis anchor; and altering any entry after the fact is detected.
/// </summary>
public class AuditLogTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLog ThreeEntryLog()
    {
        var log = new AuditLog();
        log.Append(T0, T0.AddHours(-200), ProvenanceTag.DerivedFromRecorded, "estimate SP1 chi2 PASS");
        log.Append(T0.AddMinutes(1), T0.AddHours(-199), ProvenanceTag.DerivedFromRecorded, "estimate SP2 chi2 PASS");
        log.Append(T0.AddMinutes(2), T0.AddHours(-198), ProvenanceTag.DerivedFromRecorded, "estimate SP3 chi2 FAIL");
        return log;
    }

    [Fact]
    public void A_clean_log_verifies_and_chains_from_genesis()
    {
        AuditLog log = ThreeEntryLog();
        Assert.True(log.Verify());

        Assert.Equal(AuditLog.Genesis, log.Entries[0].PrevHash);
        for (int i = 1; i < log.Entries.Count; i++)
            Assert.Equal(log.Entries[i - 1].Hash, log.Entries[i].PrevHash);
    }

    [Fact]
    public void Tampering_with_an_entry_breaks_verification()
    {
        AuditLog log = ThreeEntryLog();
        var tampered = new List<AuditLogEntry>(log.Entries);
        // Rewrite the middle entry's summary but keep its (now-stale) stored hash - the classic silent edit.
        tampered[1] = tampered[1] with { Summary = "estimate SP2 chi2 FAIL (doctored)" };

        Assert.False(AuditLog.Verify(tampered));
    }

    [Fact]
    public void Reordering_entries_is_detected()
    {
        AuditLog log = ThreeEntryLog();
        var reordered = new List<AuditLogEntry> { log.Entries[1], log.Entries[0], log.Entries[2] };
        Assert.False(AuditLog.Verify(reordered));
    }

    [Fact]
    public void Append_refuses_what_the_on_disk_writer_refuses_and_stores_utc()
    {
        // GR-849: the in-memory log applies the writer's checks, so it can always be saved.
        var log = new AuditLog();
        var t = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentNullException>(() => log.Append(t, null, ProvenanceTag.Recorded, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Append(t, null, (ProvenanceTag)999, "bad"));
        Assert.Empty(log.Entries);

        AuditLogEntry e = log.Append(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified), t, ProvenanceTag.Recorded, "ok");
        Assert.Equal(DateTimeKind.Utc, e.RunTimestampUtc.Kind);
        Assert.Equal(t, e.RunTimestampUtc);
        Assert.Equal(DateTimeKind.Utc, e.StateTimeUtc!.Value.Kind);
        Assert.True(log.Verify());
    }
}
