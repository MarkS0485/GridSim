using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The persistent, append-only audit log (R075): one JSON line per entry, the hash chain preserved on disk, a
/// verifier that names the first broken entry and why, a single-writer lock, and the event-recording API that
/// product paths call. Every tamper kind is checked to be caught AT THE RIGHT ENTRY, not merely "somewhere".
/// </summary>
public sealed class AuditLogFileTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Every test gets its own scratch directory, removed afterwards.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-audit-{Guid.NewGuid():N}");

    public AuditLogFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string LogPath(string name = "audit.jsonl") => Path.Combine(_dir, name);

    /// <summary>The five records used by most tests; summaries are distinct so edits are easy to target.</summary>
    private static void AppendFive(Func<DateTime, DateTime?, ProvenanceTag, string, AuditLogEntry> append)
    {
        for (int i = 0; i < 5; i++)
            append(T0.AddMinutes(i), T0.AddHours(-200 + i), ProvenanceTag.DerivedFromRecorded, $"estimate SP{i + 1} chi2 PASS");
    }

    /// <summary>Write the five records to a fresh file and return the writer's anchor at close.</summary>
    private static AuditAnchor WriteFive(string path)
    {
        using AuditLogWriter writer = AuditLogWriter.Open(path);
        AppendFive(writer.Append);
        return writer.Anchor;
    }

    // Files are rewritten in tests with exactly the writer's conventions (UTF-8, no BOM, LF endings) so that
    // the ONLY difference from a genuine log is the tampering under test.
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static List<string> ReadLines(string path)
    {
        string text = File.ReadAllText(path, NoBom);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        return new List<string>(text[..^1].Split('\n'));
    }

    private static void WriteLines(string path, List<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines) + "\n", NoBom);

    // ---------------------------------------------------------------- round trip

    [Fact]
    public void Round_trip_file_entries_equal_the_in_memory_log_hash_for_hash()
    {
        // The same five records appended in memory and on disk must give identical entries, because the file
        // uses the one shared hash function. This is what makes the two forms interchangeable.
        var memory = new AuditLog();
        AppendFive(memory.Append);
        string path = LogPath();
        WriteFive(path);

        IReadOnlyList<AuditLogEntry> loaded = AuditLogFile.Load(path);

        Assert.Equal(memory.Entries.Count, loaded.Count);
        for (int i = 0; i < loaded.Count; i++)
        {
            Assert.Equal(memory.Entries[i], loaded[i]);
            Assert.Equal(memory.Entries[i].Hash, loaded[i].Hash);
            Assert.Equal(DateTimeKind.Utc, loaded[i].RunTimestampUtc.Kind);
        }
        Assert.True(AuditLog.Verify(loaded));
    }

    [Fact]
    public void Save_of_an_in_memory_log_loads_back_identically_and_can_be_appended_to()
    {
        var memory = new AuditLog();
        AppendFive(memory.Append);
        string path = LogPath();
        AuditLogFile.Save(memory, path);

        Assert.Equal(memory.Entries, AuditLogFile.Load(path));

        // The saved file is a normal log: a writer continues the same chain from its tail.
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            Assert.Equal(5, writer.Count);
            Assert.Equal(memory.Entries[^1].Hash, writer.HeadHash);
            AuditLogEntry sixth = writer.Append(T0.AddMinutes(5), null, ProvenanceTag.Synthetic, "sixth");
            Assert.Equal(5, sixth.Sequence);
            Assert.Equal(memory.Entries[^1].Hash, sixth.PrevHash);
        }

        Assert.Equal(6, AuditLogFile.Load(path).Count);
    }

    [Fact]
    public void Save_refuses_to_overwrite_an_existing_file()
    {
        var memory = new AuditLog();
        AppendFive(memory.Append);
        string path = LogPath();
        File.WriteAllText(path, "precious", NoBom);

        Assert.ThrowsAny<IOException>(() => AuditLogFile.Save(memory, path));
        Assert.Equal("precious", File.ReadAllText(path, NoBom));
    }

    [Fact]
    public void Reopening_continues_the_chain_across_sessions()
    {
        string path = LogPath();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            w.Append(T0, null, ProvenanceTag.Recorded, "first session a");
            w.Append(T0.AddSeconds(1), null, ProvenanceTag.Recorded, "first session b");
        }
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            Assert.Equal(2, w.Count);
            AuditLogEntry e = w.Append(T0.AddSeconds(2), null, ProvenanceTag.Recorded, "second session");
            Assert.Equal(2, e.Sequence);
        }

        AuditLogVerification v = AuditLogFile.Verify(path);
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(3, v.VerifiedCount);
        Assert.Equal("second session", v.Entries[2].Summary);
    }

    [Fact]
    public void An_unspecified_kind_timestamp_is_stored_as_utc_with_no_shift_and_hashes_as_in_memory()
    {
        // GridSim's rule: an Unspecified time is UTC that lost its label (UtcTime.Normalise). The stored value
        // must keep the same ticks, gain the Utc kind, and hash exactly as the in-memory log does.
        var naive = new DateTime(2026, 6, 1, 12, 30, 0, DateTimeKind.Unspecified);
        var memory = new AuditLog();
        AuditLogEntry expected = memory.Append(naive, naive, ProvenanceTag.Recorded, "naive stamps");

        string path = LogPath();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            AuditLogEntry written = w.Append(naive, naive, ProvenanceTag.Recorded, "naive stamps");
            Assert.Equal(expected.Hash, written.Hash);
        }

        AuditLogEntry loaded = Assert.Single(AuditLogFile.Load(path));
        Assert.Equal(naive.Ticks, loaded.RunTimestampUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, loaded.RunTimestampUtc.Kind);
        Assert.Equal(expected.Hash, loaded.Hash);
        Assert.Contains("\"runUtc\":\"2026-06-01T12:30:00.0000000Z\"", File.ReadAllText(path, NoBom), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_format_is_one_compact_canonical_json_object_per_line()
    {
        string path = LogPath();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
            w.Append(T0, null, ProvenanceTag.Recorded, "x");

        List<string> lines = ReadLines(path);
        string line = Assert.Single(lines);
        Assert.StartsWith("{\"format\":\"gridsim-audit/1\",\"seq\":0,\"runUtc\":\"2026-06-01T00:00:00.0000000Z\",\"stateUtc\":null,\"provenance\":\"Recorded\",\"summary\":\"x\",\"prevHash\":\"GENESIS\",\"hash\":\"", line, StringComparison.Ordinal);
        // The file is plain ASCII.
        foreach (byte b in File.ReadAllBytes(path))
            Assert.True(b < 0x80, "audit log contains a non-ASCII byte");
    }

    // ---------------------------------------------------------------- empty and missing

    [Fact]
    public void An_empty_log_verifies_with_zero_entries_and_the_genesis_head()
    {
        string path = LogPath();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            Assert.Equal(0, w.Count);
            Assert.Equal(AuditLog.Genesis, w.HeadHash);
        }

        Assert.Equal(0L, new FileInfo(path).Length);
        AuditLogVerification v = AuditLogFile.Verify(path);
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(0, v.VerifiedCount);
        Assert.Null(v.FirstBrokenIndex);
        Assert.Equal(AuditLog.Genesis, v.HeadHash);
        Assert.Empty(v.Entries);
        Assert.Empty(AuditLogFile.Load(path, new AuditAnchor(0, AuditLog.Genesis)));
    }

    [Fact]
    public void A_missing_log_is_an_error_never_an_empty_log()
    {
        Assert.Throws<FileNotFoundException>(() => AuditLogFile.Verify(LogPath("does-not-exist.jsonl")));
    }

    // ---------------------------------------------------------------- tamper kinds, each at the right index

    /// <summary>Verify must fail at exactly <paramref name="index"/> with a reason containing
    /// <paramref name="reasonFragment"/>, and Load must throw the tamper exception naming the same index.</summary>
    private static void AssertBrokenAt(string path, int index, string reasonFragment, AuditAnchor? anchor = null)
    {
        AuditLogVerification v = AuditLogFile.Verify(path, anchor);
        Assert.False(v.IsValid);
        Assert.Equal(index, v.FirstBrokenIndex);
        Assert.NotNull(v.Reason);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
        Assert.Equal(index, v.VerifiedCount);
        Assert.Equal(index, v.Entries.Count);

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditLogFile.Load(path, anchor));
        Assert.Equal(index, ex.EntryIndex);
        Assert.Contains("entry " + index.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_an_entry_is_detected_at_that_entry()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        Assert.Contains("estimate SP3 chi2 PASS", lines[2], StringComparison.Ordinal);
        lines[2] = lines[2].Replace("estimate SP3 chi2 PASS", "estimate SP3 chi2 FAIL", StringComparison.Ordinal);
        WriteLines(path, lines);

        AssertBrokenAt(path, 2, "stored hash does not match");
    }

    [Fact]
    public void Editing_an_entry_and_recomputing_its_hash_is_detected_at_the_next_entry()
    {
        // A more careful tamperer recomputes the edited entry's own hash. The edited entry then checks out on its
        // own, but the NEXT entry's prevHash still points at the original, so the chain breaks there.
        string path = LogPath();
        WriteFive(path);
        IReadOnlyList<AuditLogEntry> original = AuditLogFile.Load(path);
        AuditLogEntry e = original[2] with { Summary = "estimate SP3 chi2 FAIL" };
        e = e with { Hash = AuditLog.ComputeHash(e.Sequence, e.RunTimestampUtc, e.StateTimeUtc, e.Provenance, e.Summary, e.PrevHash) };
        List<string> lines = ReadLines(path);
        lines[2] = AuditLogFile.ToJsonLine(e);
        WriteLines(path, lines);

        AssertBrokenAt(path, 3, "prevHash does not equal the hash of entry 2");
    }

    [Fact]
    public void Deleting_a_middle_entry_is_detected_where_it_was()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines.RemoveAt(2);
        WriteLines(path, lines);

        AssertBrokenAt(path, 2, "sequence number is 3 but it is at position 2");
    }

    [Fact]
    public void Deleting_the_first_entry_is_detected_at_index_zero()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines.RemoveAt(0);
        WriteLines(path, lines);

        AssertBrokenAt(path, 0, "sequence number is 1 but it is at position 0");
    }

    [Fact]
    public void Reordering_entries_is_detected_at_the_first_moved_entry()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        (lines[1], lines[3]) = (lines[3], lines[1]);
        WriteLines(path, lines);

        AssertBrokenAt(path, 1, "sequence number is 3 but it is at position 1");
    }

    [Fact]
    public void Inserting_a_blank_line_is_detected()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines.Insert(2, "");
        WriteLines(path, lines);

        AssertBrokenAt(path, 2, "blank line");
    }

    [Fact]
    public void Truncating_through_the_last_entry_is_detected_at_that_entry()
    {
        string path = LogPath();
        WriteFive(path);
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^10]);

        AssertBrokenAt(path, 4, "no terminating newline");
    }

    [Fact]
    public void Truncating_through_a_middle_entry_is_detected_at_that_entry()
    {
        // Cut the file part-way through entry 2: entries 0 and 1 survive intact, entry 2 is a fragment.
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        string cut = lines[0] + "\n" + lines[1] + "\n" + lines[2][..40];
        File.WriteAllText(path, cut, NoBom);

        AssertBrokenAt(path, 2, "no terminating newline");
    }

    [Fact]
    public void Removing_whole_entries_from_the_end_is_caught_by_the_anchor_and_only_by_the_anchor()
    {
        string path = LogPath();
        AuditAnchor anchor = WriteFive(path);
        Assert.Equal(5, anchor.Count);
        List<string> lines = ReadLines(path);
        lines.RemoveRange(3, 2);
        WriteLines(path, lines);

        // Honest limit, pinned: without an anchor the shorter chain is internally consistent and verifies.
        AuditLogVerification alone = AuditLogFile.Verify(path);
        Assert.True(alone.IsValid, alone.Reason);
        Assert.Equal(3, alone.VerifiedCount);

        // With the anchor recorded at close, the missing tail is reported at the first missing index.
        AssertBrokenAt(path, 3, "tail truncation", anchor);
    }

    [Fact]
    public void Rewriting_the_whole_log_with_fresh_hashes_is_caught_by_the_anchor()
    {
        string path = LogPath();
        AuditAnchor anchor = WriteFive(path);

        // The attacker rebuilds a fully consistent log with one changed summary and replaces the file.
        var forged = new AuditLog();
        for (int i = 0; i < 5; i++)
            forged.Append(T0.AddMinutes(i), T0.AddHours(-200 + i), ProvenanceTag.DerivedFromRecorded,
                i == 1 ? "estimate SP2 chi2 FAIL" : $"estimate SP{i + 1} chi2 PASS");
        File.Delete(path);
        AuditLogFile.Save(forged, path);

        Assert.True(AuditLogFile.Verify(path).IsValid); // the forgery is self-consistent
        AssertBrokenAt(path, 4, "externally anchored head hash", anchor);
    }

    [Fact]
    public void An_anchor_taken_earlier_still_verifies_a_log_that_has_grown_since()
    {
        string path = LogPath();
        AuditAnchor anchor;
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            w.Append(T0, null, ProvenanceTag.Recorded, "a");
            w.Append(T0, null, ProvenanceTag.Recorded, "b");
            anchor = w.Anchor;
            w.Append(T0, null, ProvenanceTag.Recorded, "c");
        }

        AuditLogVerification v = AuditLogFile.Verify(path, anchor);
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(3, v.VerifiedCount);
    }

    [Fact]
    public void A_hand_reformatted_line_is_rejected_even_though_its_values_are_unchanged()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines[1] = lines[1].Replace("\"seq\":1,", "\"seq\": 1,", StringComparison.Ordinal);
        WriteLines(path, lines);

        AssertBrokenAt(path, 1, "canonical form");
    }

    [Fact]
    public void A_timestamp_rewritten_as_non_utc_is_rejected()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines[3] = lines[3].Replace("2026-06-01T00:03:00.0000000Z", "2026-06-01T01:03:00.0000000+01:00", StringComparison.Ordinal);
        WriteLines(path, lines);

        AssertBrokenAt(path, 3, "runUtc");
    }

    [Fact]
    public void A_line_that_is_not_json_is_rejected()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines[4] = "this is not json";
        WriteLines(path, lines);

        AssertBrokenAt(path, 4, "not valid JSON");
    }

    [Fact]
    public void An_extra_field_is_rejected()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        lines[0] = lines[0][..^1] + ",\"note\":\"added\"}";
        WriteLines(path, lines);

        AssertBrokenAt(path, 0, "unknown field 'note'");
    }

    [Fact]
    public void Crlf_line_endings_from_an_editor_are_rejected()
    {
        string path = LogPath();
        WriteFive(path);
        List<string> lines = ReadLines(path);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", NoBom);

        AssertBrokenAt(path, 0, "CR LF");
    }

    [Fact]
    public void A_byte_order_mark_from_an_editor_is_rejected()
    {
        string path = LogPath();
        WriteFive(path);
        byte[] body = File.ReadAllBytes(path);
        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        body.CopyTo(withBom, 3);
        File.WriteAllBytes(path, withBom);

        AssertBrokenAt(path, 0, "byte-order mark");
    }

    // ---------------------------------------------------------------- writer refuses damaged logs

    [Fact]
    public void Opening_a_tampered_log_for_writing_fails_loud_and_leaves_it_untouched()
    {
        string path = LogPath();
        WriteFive(path);
        byte[] genuine = File.ReadAllBytes(path);
        List<string> lines = ReadLines(path);
        lines[2] = lines[2].Replace("SP3", "SP9", StringComparison.Ordinal);
        WriteLines(path, lines);
        byte[] before = File.ReadAllBytes(path);

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditLogWriter.Open(path));
        Assert.Equal(2, ex.EntryIndex);
        Assert.Equal(before, File.ReadAllBytes(path));

        // The failed open released its lock and file handles: once the genuine bytes are restored, the same
        // log opens again for writing.
        File.WriteAllBytes(path, genuine);
        using AuditLogWriter again = AuditLogWriter.Open(path);
        Assert.Equal(5, again.Count);
    }

    [Fact]
    public void Opening_a_log_with_a_torn_last_line_fails_loud()
    {
        string path = LogPath();
        WriteFive(path);
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^1]); // drop only the final newline: a torn write

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditLogWriter.Open(path));
        Assert.Equal(4, ex.EntryIndex);
    }

    [Fact]
    public void Opening_with_an_anchor_the_log_no_longer_matches_fails_loud()
    {
        string path = LogPath();
        AuditAnchor anchor = WriteFive(path);
        List<string> lines = ReadLines(path);
        lines.RemoveAt(4);
        WriteLines(path, lines);

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditLogWriter.Open(path, anchor));
        Assert.Equal(4, ex.EntryIndex);
    }

    [Fact]
    public void A_missing_directory_is_an_error_not_created_silently()
    {
        string path = Path.Combine(_dir, "no-such-dir", "audit.jsonl");
        Assert.Throws<DirectoryNotFoundException>(() => AuditLogWriter.Open(path));
        Assert.False(Directory.Exists(Path.Combine(_dir, "no-such-dir")));
    }

    // ---------------------------------------------------------------- single writer and concurrency

    [Fact]
    public void A_second_writer_on_the_same_log_fails_loud_while_the_first_is_open()
    {
        string path = LogPath();
        using (AuditLogWriter first = AuditLogWriter.Open(path))
        {
            first.Append(T0, null, ProvenanceTag.Recorded, "held");
            var ex = Assert.Throws<InvalidOperationException>(() => AuditLogWriter.Open(path));
            Assert.Contains("already open for writing", ex.Message, StringComparison.Ordinal);

            // Readers are still allowed while the writer is open.
            Assert.True(AuditLogFile.Verify(path).IsValid);
        }

        // Once the first writer is closed, the lock is free again.
        using AuditLogWriter second = AuditLogWriter.Open(path);
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void Concurrent_appends_from_two_threads_keep_a_valid_gap_free_chain()
    {
        // Kept modest: every append flushes to disk, which is deliberately slow.
        const int perThread = 100;
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            using var start = new ManualResetEventSlim(false);
            var errors = new List<Exception>();
            Thread MakeThread(string name) => new(() =>
            {
                try
                {
                    start.Wait();
                    for (int i = 0; i < perThread; i++)
                        writer.Append(T0.AddSeconds(i), null, ProvenanceTag.Recorded, $"{name} {i}");
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });

            Thread a = MakeThread("A");
            Thread b = MakeThread("B");
            a.Start();
            b.Start();
            start.Set();
            a.Join();
            b.Join();

            Assert.Empty(errors);
            Assert.Equal(2 * perThread, writer.Count);
        }

        AuditLogVerification v = AuditLogFile.Verify(path);
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(2 * perThread, v.VerifiedCount);
        for (int i = 0; i < v.Entries.Count; i++)
            Assert.Equal(i, v.Entries[i].Sequence);

        // Every append from each thread is present exactly once, and each thread's own order is preserved.
        int nextA = 0, nextB = 0;
        foreach (AuditLogEntry e in v.Entries)
        {
            if (e.Summary.StartsWith("A ", StringComparison.Ordinal))
                Assert.Equal($"A {nextA++}", e.Summary);
            else
                Assert.Equal($"B {nextB++}", e.Summary);
        }
        Assert.Equal(perThread, nextA);
        Assert.Equal(perThread, nextB);
    }

    [Fact]
    public void A_disposed_writer_refuses_to_append()
    {
        AuditLogWriter w = AuditLogWriter.Open(LogPath());
        w.Dispose();
        w.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => w.Append(T0, null, ProvenanceTag.Recorded, "late"));
    }

    [Fact]
    public void An_undefined_provenance_is_refused_before_anything_is_written()
    {
        string path = LogPath();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Append(T0, null, (ProvenanceTag)999, "bad"));
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    // ---------------------------------------------------------------- the event-recording API

    [Fact]
    public void Input_hashes_are_sha256_upper_hex()
    {
        // SHA-256("abc"), the FIPS 180-2 test vector.
        const string abc = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        Assert.Equal(abc, AuditInput.OfText("t", "abc").Sha256Hex);
        Assert.Equal(abc, AuditInput.OfBytes("b", Encoding.ASCII.GetBytes("abc")).Sha256Hex);

        string file = LogPath("input.txt");
        File.WriteAllText(file, "abc", NoBom);
        Assert.Equal(abc, AuditInput.OfFile("case", file).Sha256Hex);
    }

    [Fact]
    public void Recorded_event_has_the_fixed_summary_layout_and_verifies()
    {
        string path = LogPath();
        var inputs = new[]
        {
            AuditInput.OfText("case", "case body"),
            AuditInput.OfText("measurements", "measurement body"),
        };
        AuditLogEntry e;
        using (AuditLogWriter w = AuditLogWriter.Open(path))
            e = AuditEvent.Record(w, T0, "mark@host", "estimate", inputs, "status=Converged chi2=PASS",
                ProvenanceTag.DerivedFromRecorded, T0.AddHours(-200));

        string expected =
            "actor=mark@host action=estimate inputs=case:" + inputs[0].Sha256Hex +
            ",measurements:" + inputs[1].Sha256Hex + " outcome=status=Converged chi2=PASS";
        Assert.Equal(expected, e.Summary);
        Assert.Equal(T0, e.RunTimestampUtc);
        Assert.Equal(T0.AddHours(-200), e.StateTimeUtc);
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, e.Provenance);

        Assert.Equal(e, Assert.Single(AuditLogFile.Load(path)));
    }

    [Fact]
    public void Event_with_no_inputs_records_a_dash()
    {
        Assert.Equal(
            "actor=a action=interpose inputs=- outcome=started",
            AuditEvent.FormatSummary("a", "interpose", Array.Empty<AuditInput>(), "started"));
    }

    [Fact]
    public void Ambiguous_or_multi_line_event_fields_fail_loud_and_write_nothing()
    {
        string path = LogPath();
        var none = Array.Empty<AuditInput>();
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            Assert.Throws<ArgumentException>(() => AuditEvent.Record(w, T0, "mark s", "estimate", none, "ok", ProvenanceTag.Recorded));
            Assert.Throws<ArgumentException>(() => AuditEvent.Record(w, T0, "mark", "estimate run", none, "ok", ProvenanceTag.Recorded));
            Assert.Throws<ArgumentException>(() => AuditEvent.Record(w, T0, "mark", "estimate", none, "line1\nline2", ProvenanceTag.Recorded));
            Assert.Throws<ArgumentException>(() => AuditEvent.Record(w, T0, "mark", "estimate", none, " ", ProvenanceTag.Recorded));
            var twice = new[] { AuditInput.OfText("case", "x"), AuditInput.OfText("case", "y") };
            Assert.Throws<ArgumentException>(() => AuditEvent.Record(w, T0, "mark", "estimate", twice, "ok", ProvenanceTag.Recorded));
            Assert.Equal(0, w.Count);
        }
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public void Input_names_and_hashes_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new AuditInput("case:1", new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new AuditInput("case", new string('a', 64)));   // lower case
        Assert.Throws<ArgumentException>(() => new AuditInput("case", new string('A', 63)));   // too short
        Assert.Equal("case", new AuditInput("case", new string('F', 64)).Name);
    }

    [Fact]
    public void Default_actor_is_a_valid_token()
    {
        string actor = AuditEvent.DefaultActor();
        Assert.Contains("@", actor, StringComparison.Ordinal);
        // Must be accepted by the summary formatter (i.e. sanitised to the allowed characters).
        Assert.StartsWith("actor=" + actor + " ", AuditEvent.FormatSummary(actor, "x", Array.Empty<AuditInput>(), "y"), StringComparison.Ordinal);
    }
}
