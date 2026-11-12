using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-859 (November 2026): an audit log is verified in one streaming pass, one line at a time, instead of being read
/// whole into memory first; opening a writer keeps only the entry count and head hash. These tests pin that the
/// streaming pass gives exactly the verdict of the whole-buffer pass (validity, count, first broken index, reason and
/// head hash) for every kind of damage, however the stream splits its reads, with and without the entries kept, and
/// that a line longer than one read chunk is assembled correctly.
/// </summary>
public sealed class Gr859StreamingAuditVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-gr859-{Guid.NewGuid():N}");

    public Gr859StreamingAuditVerifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    // A stream that hands out at most `step` bytes per read, so every line boundary falls at every place in a read.
    private sealed class TrickleStream(byte[] data, int step) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, step), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    private static byte[] LogBytes(int entries, int summaryLength = 20)
    {
        var log = new AuditLog();
        var t = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < entries; i++)
            log.Append(t.AddSeconds(i), i % 2 == 0 ? t : null, ProvenanceTag.Recorded,
                $"entry {i} " + new string((char)('a' + (i % 26)), summaryLength));
        var sb = new StringBuilder();
        foreach (AuditLogEntry e in log.Entries) sb.Append(AuditLogFile.ToJsonLine(e)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static List<string> Lines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\n').SkipLast(1).ToList();

    private static byte[] Join(IEnumerable<string> lines) =>
        Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    // Every kind of damage the verifier names, each with the anchor it is checked against (null = chain only).
    public static IEnumerable<object?[]> Damages()
    {
        byte[] good = LogBytes(6);
        List<string> lines = Lines(good);
        AuditAnchor full = AuditLogFile.VerifyBytes(good, null).Anchor;
        AuditAnchor at3 = new(3, AuditLogFile.VerifyBytes(Join(lines.Take(3)), null).HeadHash);

        yield return new object?[] { "valid", good, null };
        yield return new object?[] { "valid, full anchor", good, full };
        yield return new object?[] { "valid, anchor at 3", good, at3 };
        yield return new object?[] { "valid, empty anchor", good, new AuditAnchor(0, AuditLog.Genesis) };
        yield return new object?[] { "empty file", Array.Empty<byte>(), null };
        yield return new object?[] { "empty file, anchor 2", Array.Empty<byte>(), new AuditAnchor(2, "x") };
        yield return new object?[] { "bom", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(good).ToArray(), null };
        yield return new object?[] { "bom only", new byte[] { 0xEF, 0xBB, 0xBF }, null };
        yield return new object?[] { "two bytes", new byte[] { 0xEF, 0xBB }, null };
        yield return new object?[] { "torn last line", good.Take(good.Length - 5).ToArray(), null };
        yield return new object?[] { "blank line", Join(lines.Take(2).Append("").Concat(lines.Skip(2))), null };
        yield return new object?[] { "crlf", Join(lines.Select((l, i) => i == 3 ? l + "\r" : l)), null };
        yield return new object?[] { "not json", Join(lines.Select((l, i) => i == 2 ? "{nope" : l)), null };
        yield return new object?[] { "edited summary", Join(lines.Select((l, i) => i == 4 ? l.Replace("entry 4", "entry X", StringComparison.Ordinal) : l)), null };
        yield return new object?[] { "deleted entry", Join(lines.Where((_, i) => i != 1)), null };
        yield return new object?[] { "swapped entries", Join(lines.Select((l, i) => i == 2 ? lines[3] : i == 3 ? lines[2] : l)), null };
        yield return new object?[] { "start removed", Join(lines.Skip(1)), null };
        yield return new object?[] { "tail truncated vs anchor", Join(lines.Take(4)), full };
        yield return new object?[] { "wrong anchor head at 1", good, new AuditAnchor(1, "0000") };
        yield return new object?[] { "wrong anchor head at 2", good, new AuditAnchor(2, "0000") };
        yield return new object?[] { "wrong anchor head at 6", good, full with { HeadHash = "0000" } };
        yield return new object?[] { "wrong empty anchor head", good, new AuditAnchor(0, "0000") };
    }

    private static void AssertSameVerdict(AuditLogVerification expected, AuditLogVerification actual, string label)
    {
        Assert.Equal((label, expected.IsValid), (label, actual.IsValid));
        Assert.Equal((label, expected.VerifiedCount), (label, actual.VerifiedCount));
        Assert.Equal((label, expected.FirstBrokenIndex), (label, actual.FirstBrokenIndex));
        Assert.Equal((label, expected.Reason), (label, actual.Reason));
        Assert.Equal((label, expected.HeadHash), (label, actual.HeadHash));
    }

    [Theory]
    [MemberData(nameof(Damages))]
    public void Streaming_without_entries_gives_the_full_verdict_for_every_damage_and_every_read_split(string label, byte[] bytes, AuditAnchor? anchor)
    {
        AuditLogVerification reference = AuditLogFile.VerifyBytes(bytes, anchor);
        Assert.Equal(reference.VerifiedCount, reference.Entries.Count);
        foreach (int step in new[] { 1, 2, 3, 7, 64, 1 << 20 })
        {
            using var kept = new TrickleStream(bytes, step);
            AuditLogVerification withEntries = AuditLogFile.VerifyStream(kept, anchor, keepEntries: true);
            AssertSameVerdict(reference, withEntries, $"{label}, step {step}, entries kept");
            Assert.Equal(reference.Entries.Select(e => e.Hash), withEntries.Entries.Select(e => e.Hash));

            using var lean = new TrickleStream(bytes, step);
            AuditLogVerification countOnly = AuditLogFile.VerifyStream(lean, anchor, keepEntries: false);
            AssertSameVerdict(reference, countOnly, $"{label}, step {step}, count only");
            Assert.Empty(countOnly.Entries);
        }
    }

    [Fact]
    public void The_damaged_cases_report_the_messages_of_the_whole_file_pass()
    {
        // Anchors for the reasons, so the streaming pass is not only equal to itself: these are the texts the
        // whole-buffer verifier gave before GR-859.
        byte[] good = LogBytes(3);
        Assert.StartsWith("the file starts with a UTF-8 byte-order mark",
            AuditLogFile.VerifyBytes(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(good).ToArray(), null).Reason, StringComparison.Ordinal);
        Assert.StartsWith("the last line has no terminating newline",
            AuditLogFile.VerifyBytes(good.Take(good.Length - 1).ToArray(), null).Reason, StringComparison.Ordinal);
        AuditLogVerification truncated = AuditLogFile.VerifyBytes(good, new AuditAnchor(5, "x"));
        Assert.Equal(3, truncated.FirstBrokenIndex);
        Assert.Contains("the file holds 3 entries but the anchor records 5", truncated.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_longer_than_one_read_chunk_is_assembled_and_verified()
    {
        byte[] bytes = LogBytes(3, summaryLength: 200_000);   // each line is over three 64 KiB chunks
        AuditLogVerification v = AuditLogFile.VerifyBytes(bytes, null);
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(3, v.VerifiedCount);
        Assert.Equal(200_000 + "entry 0 ".Length, v.Entries[0].Summary.Length);
    }

    [Fact]
    public void A_writer_opens_an_existing_log_from_the_streaming_count_and_head_and_appends_after_it()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        byte[] bytes = LogBytes(500, summaryLength: 300);
        File.WriteAllBytes(path, bytes);
        AuditAnchor before = AuditLogFile.VerifyBytes(bytes, null).Anchor;

        using (AuditLogWriter w = AuditLogWriter.Open(path, before))
        {
            Assert.Equal(500, w.Count);
            Assert.Equal(before.HeadHash, w.HeadHash);
            w.Append(new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc), null, ProvenanceTag.Recorded, "after");
        }

        AuditLogVerification after = AuditLogFile.Verify(path, before);
        Assert.True(after.IsValid, after.Reason);
        Assert.Equal(501, after.VerifiedCount);
        Assert.Equal("after", after.Entries[^1].Summary);
    }

    [Fact]
    public void A_writer_still_refuses_a_damaged_log_with_the_first_broken_entry()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        List<string> lines = Lines(LogBytes(5));
        File.WriteAllBytes(path, Join(lines.Where((_, i) => i != 2)));

        AuditLogTamperException ex = Assert.Throws<AuditLogTamperException>(() => AuditLogWriter.Open(path));
        Assert.Equal(2, ex.EntryIndex);
    }
}
