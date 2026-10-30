using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-413: the three promises <see cref="IAuditSink"/> makes about the writer's forwarding path. A sink is told of an
/// entry only after the entry is on disk and the chain has advanced; a sink that throws never fails the append, faults
/// the log or stops later sinks; and the FIRST sink failure is kept in <see cref="AuditLogWriter.LastSinkError"/>.
/// </summary>
public sealed class AuditSinkTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Every test gets its own scratch directory, removed afterwards.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-audit-sink-{Guid.NewGuid():N}");

    public AuditSinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string LogPath() => Path.Combine(_dir, "audit.jsonl");

    /// <summary>Reads the log file from inside OnEntry (the writer shares it for reading) and records whether the
    /// entry's own hash is already in the file, and what the writer's count was, at the moment it was told.</summary>
    private sealed class DurabilityProbe(string path) : IAuditSink
    {
        public List<(int Sequence, bool HashOnDisk)> Seen { get; } = new();

        public void OnEntry(AuditLogEntry entry)
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                text = reader.ReadToEnd();
            Seen.Add((entry.Sequence, text.Contains(entry.Hash, StringComparison.Ordinal)));
        }
    }

    /// <summary>Throws on every entry, with a message that names the call so the kept error can be checked.</summary>
    private sealed class ThrowingSink : IAuditSink
    {
        public int Calls { get; private set; }

        public void OnEntry(AuditLogEntry entry)
        {
            Calls++;
            throw new IOException($"forwarder down (call {Calls})");
        }
    }

    [Fact]
    public void A_sink_is_told_of_an_entry_only_after_it_is_on_disk()
    {
        var probe = new DurabilityProbe(LogPath());
        using (AuditLogWriter writer = AuditLogWriter.Open(LogPath(), sinks: new IAuditSink[] { probe }))
        {
            for (int i = 0; i < 3; i++)
                writer.Append(T0.AddMinutes(i), null, ProvenanceTag.Synthetic, $"entry {i}");
            Assert.Null(writer.LastSinkError);
        }
        Assert.Equal(new[] { (0, true), (1, true), (2, true) }, probe.Seen);
    }

    [Fact]
    public void A_throwing_sink_never_fails_the_append_and_later_sinks_still_see_every_entry()
    {
        var thrower = new ThrowingSink();
        var buffer = new BufferedAuditSink();
        AuditAnchor anchor;
        using (AuditLogWriter writer = AuditLogWriter.Open(LogPath(), sinks: new IAuditSink[] { thrower, buffer }))
        {
            for (int i = 0; i < 3; i++)
            {
                AuditLogEntry e = writer.Append(T0.AddMinutes(i), null, ProvenanceTag.Synthetic, $"entry {i}");
                Assert.Equal(i, e.Sequence);
            }
            Assert.Equal(3, writer.Count);
            anchor = writer.Anchor;
        }
        Assert.Equal(3, thrower.Calls);
        Assert.Equal(new[] { 0, 1, 2 }, buffer.Entries.Select(e => e.Sequence));

        // The log the throwing sink sat beside is whole: it reopens against its anchor and takes another entry.
        using AuditLogWriter again = AuditLogWriter.Open(LogPath(), anchor);
        Assert.Equal(3, again.Count);
        Assert.Equal(3, again.Append(T0.AddMinutes(3), null, ProvenanceTag.Synthetic, "entry 3").Sequence);
    }

    [Fact]
    public void The_first_sink_failure_is_kept_not_the_latest()
    {
        var thrower = new ThrowingSink();
        using AuditLogWriter writer = AuditLogWriter.Open(LogPath(), sinks: new IAuditSink[] { thrower });
        Assert.Null(writer.LastSinkError);
        writer.Append(T0, null, ProvenanceTag.Synthetic, "first");
        writer.Append(T0.AddMinutes(1), null, ProvenanceTag.Synthetic, "second");
        Assert.Equal("ThrowingSink: forwarder down (call 1)", writer.LastSinkError);
    }
}
