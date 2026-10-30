using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Deleting entries from the in-memory hash-chained audit log. Removing an entry from the middle (or the start) breaks
/// the chain and is detected. Removing entries from the end leaves a shorter chain that is still internally consistent,
/// so the chain alone cannot see it: that is the documented limit, closed only by an anchor (entry count and head hash)
/// kept out of the writer's reach. Both halves are pinned here so the limit stays stated as a test, not a footnote.
/// </summary>
public class AuditLogDeletionTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLog FourEntryLog()
    {
        var log = new AuditLog();
        for (int k = 0; k < 4; k++)
            log.Append(T0.AddMinutes(k), T0.AddHours(-200 + k), ProvenanceTag.DerivedFromRecorded, $"estimate SP{k + 1} chi2 PASS");
        Assert.True(log.Verify());
        return log;
    }

    [Fact]
    public void Deleting_a_middle_entry_is_detected()
    {
        AuditLog log = FourEntryLog();
        var deleted = new List<AuditLogEntry> { log.Entries[0], log.Entries[2], log.Entries[3] };
        Assert.False(AuditLog.Verify(deleted));
    }

    [Fact]
    public void Deleting_the_first_entry_is_detected()
    {
        AuditLog log = FourEntryLog();
        var deleted = new List<AuditLogEntry> { log.Entries[1], log.Entries[2], log.Entries[3] };
        Assert.False(AuditLog.Verify(deleted));
    }

    [Fact]
    public void Deleting_entries_from_the_end_is_not_visible_to_the_chain_alone()
    {
        AuditLog log = FourEntryLog();
        var truncated = new List<AuditLogEntry> { log.Entries[0], log.Entries[1] };
        Assert.True(AuditLog.Verify(truncated));
    }
}
