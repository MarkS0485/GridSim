using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: what the audit log and its anchor do after a run is killed part-way. The writer holds the log and
/// its lock open until <c>Finish</c>, so a kill leaves exactly what was on disk at that instant. Each test takes a copy
/// of the log and anchor files while the recorder is still open (the state a killed process leaves; every append is
/// flushed to disk before it returns), abandons the original, and starts a new recorder on the copy.
/// </summary>
public sealed class AuditKillRecoveryTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-auditkill-" + Guid.NewGuid().ToString("N"));

    public AuditKillRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string Sub(string name)
    {
        string d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // The files a killed process leaves: the log and the anchor as they are now (the .lock handle dies with the process).
    private static string Snapshot(AuditRunRecorder open, string toDir)
    {
        string log = Path.Combine(toDir, "audit.log");
        using (var src = new FileStream(open.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = File.Create(log))
            src.CopyTo(dst);
        if (File.Exists(open.AnchorPath)) File.Copy(open.AnchorPath, log + ".anchor", overwrite: true);
        return log;
    }

    private static AuditRunRecorder StartRun(string log) =>
        AuditRunRecorder.Start(log, "tester", "estimate", ProvenanceTag.Synthetic);

    [Fact]
    public void A_run_killed_after_its_started_entry_leaves_a_log_the_next_run_extends()
    {
        string log = Path.Combine(Sub("a"), "audit.log");
        AuditRunRecorder killed = StartRun(log);
        killed.RecordNow("outcome=started");
        string survivor = Snapshot(killed, Sub("a-after-kill"));
        killed.Finish(0);   // the original is only closed so the test can clean up

        AuditLogVerification before = AuditLogFile.Verify(survivor);
        Assert.True(before.IsValid, before.Reason);
        Assert.Equal(1, before.VerifiedCount);

        AuditRunRecorder next = StartRun(survivor);
        Assert.False(next.AnchorWasMissing);
        AuditAnchor? final = next.Finish(0);

        AuditLogVerification after = AuditLogFile.Verify(survivor, final);
        Assert.True(after.IsValid, after.Reason);
        Assert.Equal(2, after.VerifiedCount);
        Assert.Equal(final!.HeadHash, AuditAnchorFile.ReadIfExists(survivor + ".anchor")!.HeadHash);
    }

    [Fact]
    public void A_kill_between_the_append_and_the_anchor_rewrite_is_recovered_not_refused()
    {
        string log = Path.Combine(Sub("b"), "audit.log");
        AuditRunRecorder run = StartRun(log);
        run.RecordNow("outcome=first");
        AuditAnchor oldAnchor = AuditAnchorFile.ReadIfExists(run.AnchorPath)!;
        run.RecordNow("outcome=second");
        string survivor = Snapshot(run, Sub("b-after-kill"));
        run.Finish(0);
        // The kill came after the second entry was durable but before the anchor was rewritten: the anchor is one entry behind.
        AuditAnchorFile.Write(survivor + ".anchor", oldAnchor);
        Assert.Equal(1, AuditAnchorFile.ReadIfExists(survivor + ".anchor")!.Count);
        Assert.Equal(2, AuditLogFile.Verify(survivor).VerifiedCount);

        AuditRunRecorder next = StartRun(survivor);   // an anchor that is a prefix of the log is not tampering
        AuditAnchor? final = next.Finish(0);

        Assert.Equal(3, final!.Count);
        Assert.True(AuditLogFile.Verify(survivor, final).IsValid);
        Assert.Equal(3, AuditAnchorFile.ReadIfExists(survivor + ".anchor")!.Count);
    }

    [Fact]
    public void A_torn_last_line_is_refused_loudly_and_a_log_cut_back_to_the_last_whole_entry_recovers()
    {
        string log = Path.Combine(Sub("c"), "audit.log");
        AuditRunRecorder run = StartRun(log);
        run.RecordNow("outcome=first");
        run.RecordNow("outcome=second");
        string survivor = Snapshot(run, Sub("c-after-kill"));
        run.Finish(0);
        long whole = new FileInfo(survivor).Length;
        // A kill part-way through a third write leaves half a line with no newline.
        File.AppendAllText(survivor, "{\"seq\":2,\"runTime");
        Assert.False(AuditLogFile.Verify(survivor).IsValid);

        Assert.Throws<AuditLogTamperException>(() => StartRun(survivor));   // never appended to, never silently repaired

        // The operator investigates and removes the torn bytes; the chain up to the last whole entry is intact.
        using (var fs = new FileStream(survivor, FileMode.Open, FileAccess.Write)) fs.SetLength(whole);
        AuditRunRecorder next = StartRun(survivor);
        AuditAnchor? final = next.Finish(0);
        Assert.Equal(3, final!.Count);
        Assert.True(AuditLogFile.Verify(survivor, final).IsValid);
    }

    [Fact]
    public void A_log_cut_back_past_its_anchor_after_a_kill_is_still_detected()
    {
        string log = Path.Combine(Sub("d"), "audit.log");
        AuditRunRecorder run = StartRun(log);
        run.RecordNow("outcome=first");
        run.RecordNow("outcome=second");
        string survivor = Snapshot(run, Sub("d-after-kill"));
        run.Finish(0);
        // Recovery must not weaken tail-truncation detection: drop the last whole line and keep the newer anchor.
        string[] lines = File.ReadAllLines(survivor);
        Assert.Equal(2, lines.Length);
        File.WriteAllText(survivor, lines[0] + "\n");
        Assert.Throws<AuditLogTamperException>(() => StartRun(survivor));
    }
}
