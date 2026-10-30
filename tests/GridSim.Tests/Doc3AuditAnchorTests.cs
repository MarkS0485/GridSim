using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-3 (docs-session stream 3, October 2026), the audit items. D-DOC-062: <see cref="AuditRunRecorder"/> opens the log
/// with its anchor (<see cref="AuditAnchorFile"/>), so appending to a tail-truncated, restored or rewritten log is
/// detected and refused before anything is appended. D-DOC-060: <c>serve</c> and the web host's resident estimator
/// write audit entries like the CLI's evidence verbs.
/// </summary>
public sealed class Doc3AuditAnchorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-doc3-audit-" + Guid.NewGuid().ToString("N"));

    public Doc3AuditAnchorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    private void Runs(int n, string? anchorPath = null)
    {
        for (int i = 0; i < n; i++)
            AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic, anchorPath).Finish(0);
    }

    // ================================================================================================================
    // D-DOC-062: the anchor file
    // ================================================================================================================

    [Fact]
    public void D062_the_anchor_line_round_trips_and_a_malformed_one_is_refused()
    {
        var a = new AuditAnchor(3, new string('A', 64));
        string line = AuditAnchorFile.Format(a);
        Assert.Equal("gridsim-audit-anchor/1 count=3 head=" + new string('A', 64), line);
        Assert.Equal(a, AuditAnchorFile.Parse(line + "\n", "x"));
        Assert.Equal(new AuditAnchor(0, AuditLog.Genesis), AuditAnchorFile.Parse("gridsim-audit-anchor/1 count=0 head=GENESIS\r\n", "x"));

        foreach (string bad in new[]
        {
            "", "gridsim-audit-anchor/1", "gridsim-audit-anchor/2 count=1 head=" + new string('A', 64),
            "gridsim-audit-anchor/1 count=-1 head=" + new string('A', 64), "gridsim-audit-anchor/1 count=1 head=GENESIS",
            "gridsim-audit-anchor/1 count=0 head=" + new string('A', 64), "gridsim-audit-anchor/1 count=1 head=" + new string('a', 64),
            "gridsim-audit-anchor/1 count=1 head=" + new string('A', 63), "gridsim-audit-anchor/1 count=x head=GENESIS",
            "gridsim-audit-anchor/1  count=0 head=GENESIS", "gridsim-audit-anchor/1 count=0 head=GENESIS extra",
        })
            Assert.Throws<InvalidOperationException>(() => AuditAnchorFile.Parse(bad, "x"));
        Assert.Throws<InvalidOperationException>(() => AuditAnchorFile.Format(new AuditAnchor(2, AuditLog.Genesis)));
    }

    [Fact]
    public void D062_every_run_writes_the_anchor_beside_the_log()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        Assert.Equal(LogPath + ".anchor", run.AnchorPath);
        Assert.False(run.AnchorWasMissing);   // a new, empty log needs no anchor yet
        AuditAnchor? anchor = run.Finish(0);
        Assert.Null(run.AnchorWriteError);
        Assert.Equal(anchor, AuditAnchorFile.ReadIfExists(LogPath + ".anchor"));
        Runs(2);
        Assert.Equal(3, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")!.Count);
        Assert.Equal(AuditLogFile.Verify(LogPath).Anchor, AuditAnchorFile.ReadIfExists(LogPath + ".anchor"));
    }

    [Fact]
    public void D062_appending_to_a_tail_truncated_log_is_refused()
    {
        Runs(3);
        // Cut the last entry off. The shorter chain is self-consistent, so without the anchor it verifies...
        string[] lines = File.ReadAllLines(LogPath);
        File.WriteAllText(LogPath, string.Join("\n", lines.Take(2)) + "\n");
        Assert.True(AuditLogFile.Verify(LogPath).IsValid);
        // ...and before D-DOC-062 the next run appended to it as if nothing had happened. Now it is refused first.
        var ex = Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic));
        Assert.Contains("anchor records 3", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);   // nothing appended
    }

    [Fact]
    public void D062_a_deleted_log_and_a_rewritten_log_are_refused()
    {
        Runs(2);
        // Rewritten end to end with recomputed hashes (two entries, but different ones): head hash mismatch.
        string saved = File.ReadAllText(LogPath);
        File.Delete(LogPath);
        var forged = new AuditLog();
        forged.Append(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, ProvenanceTag.Synthetic, "actor=a action=estimate inputs=- outcome=exit=0");
        forged.Append(new DateTime(2026, 10, 1, 0, 1, 0, DateTimeKind.Utc), null, ProvenanceTag.Synthetic, "actor=a action=estimate inputs=- outcome=exit=0");
        AuditLogFile.Save(forged, LogPath);
        Assert.True(AuditLogFile.Verify(LogPath).IsValid);   // the forgery is self-consistent
        Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic));

        // Deleted (a new empty log appears in its place): the anchor still records 2 entries.
        File.Delete(LogPath);
        Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic));

        // Allowed: the untouched log appends.
        File.WriteAllText(LogPath, saved);
        Assert.Equal(3, AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic).Finish(0)!.Count);
    }

    [Fact]
    public void D062_a_log_with_entries_but_no_anchor_is_accepted_flagged_and_recorded_in_the_entry()
    {
        Runs(2);
        File.Delete(LogPath + ".anchor");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        Assert.True(run.AnchorWasMissing);
        run.SetOutcome("method=Wls");
        run.Finish(0);
        AuditLogEntry last = AuditLogFile.Load(LogPath).Last();
        Assert.Equal("actor=a action=estimate inputs=- outcome=exit=0 method=Wls audit-anchor=missing", last.Summary);
        // Anchoring restarts: the next run is checked again and carries no marker.
        AuditRunRecorder next = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        Assert.False(next.AnchorWasMissing);
        next.Finish(0);
        Assert.Equal("actor=a action=estimate inputs=- outcome=exit=0", AuditLogFile.Load(LogPath).Last().Summary);
    }

    [Fact]
    public void D062_an_anchor_kept_elsewhere_is_used_and_a_damaged_anchor_is_refused()
    {
        string elsewhere = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(elsewhere);
        string anchor = Path.Combine(elsewhere, "audit.anchor");
        Runs(2, anchor);
        Assert.False(File.Exists(LogPath + ".anchor"));
        Assert.Equal(2, AuditAnchorFile.ReadIfExists(anchor)!.Count);

        string[] lines = File.ReadAllLines(LogPath);
        File.WriteAllText(LogPath, lines[0] + "\n");
        Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic, anchor));

        File.WriteAllText(anchor, "not an anchor\n");
        Assert.Throws<InvalidOperationException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic, anchor));
        Assert.Throws<DirectoryNotFoundException>(() =>
            AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic, Path.Combine(_dir, "no-such", "x.anchor")));
    }

    [Fact]
    public void D062_record_now_updates_the_anchor_too()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "interpose", ProvenanceTag.Recorded);
        run.RecordNow("started");
        Assert.Equal(1, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")!.Count);
        run.Finish(0);
        Assert.Equal(2, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")!.Count);
    }

    // ---- process level ----------------------------------------------------------------------------------------------

    [Fact]
    public void D062_the_cli_refuses_a_truncated_log_with_exit_1_and_needs_audit_log_for_audit_anchor()
    {
        CliProcess.Result first = CliProcess.RunBuilt("--audit-log", LogPath, "estimate", "case9", "--synth", "--out", Path.Combine(_dir, "o1"));
        Assert.True(first.ExitCode == 0, first.ToString());
        Assert.True(File.Exists(LogPath + ".anchor"), "the CLI writes the anchor after its entry");
        CliProcess.Result second = CliProcess.RunBuilt("--audit-log", LogPath, "estimate", "case9", "--synth", "--out", Path.Combine(_dir, "o2"));
        Assert.True(second.ExitCode == 0, second.ToString());

        string[] lines = File.ReadAllLines(LogPath);
        File.WriteAllText(LogPath, lines[0] + "\n");
        CliProcess.Result third = CliProcess.RunBuilt("--audit-log", LogPath, "estimate", "case9", "--synth", "--out", Path.Combine(_dir, "o3"));
        Assert.True(third.ExitCode == 1, third.ToString());
        Assert.Contains("anchor records 2", third.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "o3")), "nothing runs when the log is refused");
        Assert.Single(File.ReadAllLines(LogPath));

        CliProcess.Result alone = CliProcess.RunBuilt("--audit-anchor", Path.Combine(_dir, "x.anchor"), "estimate", "case9", "--synth", "--out", Path.Combine(_dir, "o4"));
        Assert.True(alone.ExitCode == 2, alone.ToString());
        Assert.Contains("--audit-anchor needs --audit-log", alone.StdErr, StringComparison.Ordinal);
    }

    // ================================================================================================================
    // D-DOC-060: serve and the web host's resident estimator are audited
    // ================================================================================================================

    private string WindowsDir(params DateTime?[] stamps)
    {
        string dir = Path.Combine(_dir, "windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        for (int i = 0; i < stamps.Length; i++)
            (template with { TakenUtc = stamps[i] }).Save(Path.Combine(dir, $"w{i:D3}.json"));
        return dir;
    }

    [Fact]
    public void D060_serve_records_its_inputs_and_outcome_in_the_audit_log()
    {
        string windows = WindowsDir(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 3, 1, 12, 0, 1, DateTimeKind.Utc));
        CliProcess.Result r = CliProcess.RunBuilt("--audit-log", LogPath, "serve", "case9", "--measurements-dir", windows);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("audit: ", r.StdErr, StringComparison.Ordinal);
        AuditLogEntry e = Assert.Single(AuditLogFile.Load(LogPath, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")));
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, e.Provenance);
        Assert.StartsWith("actor=", e.Summary, StringComparison.Ordinal);
        Assert.Contains(" action=serve inputs=case-id:", e.Summary, StringComparison.Ordinal);
        Assert.Contains(",measurements-dir:" + AuditRunRecorder.HashDirectory(windows) + " ", e.Summary, StringComparison.Ordinal);
        Assert.Contains(" outcome=exit=0 method=wls processed=2 converged=2 rejected-fresh=0 failed=0 unloadable=0 now=", e.Summary, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 1, DateTimeKind.Utc), e.StateTimeUtc);

        // A refused run is recorded too: --utc-now later than the real clock.
        CliProcess.Result refused = CliProcess.RunBuilt("--audit-log", LogPath, "serve", "case9", "--measurements-dir", windows, "--utc-now", "2100-01-01T00:00:00Z");
        Assert.True(refused.ExitCode == 2, refused.ToString());
        Assert.EndsWith(" outcome=exit=2 rejected=ForwardInferenceException reason=NowLaterThanRealClock", AuditLogFile.Load(LogPath).Last().Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void D060_audit_log_is_still_refused_on_an_unaudited_verb()
    {
        CliProcess.Result r = CliProcess.RunBuilt("--audit-log", LogPath, "cpf", "case9");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--audit-log applies to estimate, crossval, interpose and serve only (got 'cpf')", r.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(LogPath));
    }

    private sealed class ListFeed : IRealtimeWindowFeed
    {
        private readonly IReadOnlyList<MeasurementSet> _windows;
        public ListFeed(GridModel model, IReadOnlyList<MeasurementSet> windows) { Model = model; _windows = windows; }
        public GridModel Model { get; }
        public IEnumerable<MeasurementSet> Windows() => _windows;
    }

    [Fact]
    public void D060_the_web_resident_estimator_records_one_entry_when_its_run_ends()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        DateTime t0 = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var feed = new ListFeed(model, new[] { template with { TakenUtc = t0 }, template with { TakenUtc = t0.AddSeconds(1) } });

        AuditRunRecorder audit = AuditRunRecorder.Start(LogPath, "web@host", "web-realtime", ProvenanceTag.DerivedFromRecorded);
        audit.AddInput(AuditRunRecorder.InputForArgument("case", "case9"));
        var svc = new RealtimeEstimatorHostedService(feed, options: null, audit: audit);
        RealtimeRunSummary s = svc.RunToCompletion();

        Assert.Equal(2, s.WindowsConverged);
        AuditLogEntry e = Assert.Single(AuditLogFile.Load(LogPath));
        Assert.Equal(
            "actor=web@host action=web-realtime inputs=case-id:" + AuditInput.OfText("case-id", "case9").Sha256Hex +
            " outcome=exit=0 method=wls processed=2 converged=2 rejected-fresh=0 failed=0 skipped-files=0 cancelled=False", e.Summary);
        Assert.Equal(t0.AddSeconds(1), e.StateTimeUtc);
        Assert.Equal(svc.AuditAnchor, AuditAnchorFile.ReadIfExists(LogPath + ".anchor"));

        // Without an audit recorder the service behaves exactly as before and writes nothing.
        var plain = new RealtimeEstimatorHostedService(feed);
        Assert.Equal(2, plain.RunToCompletion().WindowsConverged);
        Assert.Null(plain.AuditAnchor);
        Assert.Single(AuditLogFile.Load(LogPath));
    }

    [Fact]
    public void D060_a_failed_web_run_is_recorded_as_a_rejection()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var feed = new ListFeed(model, new[] { WlsEstimatorTests.ExactFullSet(model, truth) with { TakenUtc = null } });   // undated: refused
        AuditRunRecorder audit = AuditRunRecorder.Start(LogPath, "web@host", "web-realtime", ProvenanceTag.DerivedFromRecorded);
        var svc = new RealtimeEstimatorHostedService(feed, options: null, audit: audit);
        Assert.Throws<ForwardInferenceException>(() => svc.RunToCompletion());
        // Test changed 2026-10-19: since 1.95.297 (CAF C1, GR-002 / GR-011 to GR-026) a forward-inference refusal is
        // ALSO recorded as its own security event (HorizonViolation) at the detection site, before the run's rejection
        // entry, so the log holds two entries in that order. The rejection entry itself is unchanged.
        IReadOnlyList<AuditLogEntry> entries = AuditLogFile.Load(LogPath);
        Assert.Equal(2, entries.Count);
        Assert.StartsWith("actor=web@host action=security inputs=- outcome=event=HorizonViolation severity=Info reason=resident estimator stopped: ",
            entries[0].Summary, StringComparison.Ordinal);
        Assert.Equal("actor=web@host action=web-realtime inputs=- outcome=exit=2 rejected=ForwardInferenceException",
            entries[1].Summary);
    }

    [Fact]
    public void D060_the_web_host_refuses_an_audit_log_it_would_never_write_and_an_anchor_without_a_log()
    {
        string keys = Path.Combine(_dir, "keys");
        WebApplicationFactory<Program> F(params (string K, string V)[] s) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in s) b.UseSetting(k, v);
        });

        using (WebApplicationFactory<Program> noRealtime = F(("Audit:LogPath", LogPath)))
        {
            var ex = Assert.ThrowsAny<Exception>(() => noRealtime.CreateClient());
            Assert.Contains("Audit:LogPath is set, but this host runs no resident estimator", Flatten(ex), StringComparison.Ordinal);
        }
        using (WebApplicationFactory<Program> anchorOnly = F(("Audit:AnchorPath", LogPath + ".anchor")))
        {
            var ex = Assert.ThrowsAny<Exception>(() => anchorOnly.CreateClient());
            Assert.Contains("Audit:AnchorPath is set but Audit:LogPath is not", Flatten(ex), StringComparison.Ordinal);
        }
        Assert.False(File.Exists(LogPath));
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        if (ex is AggregateException agg) parts.AddRange(agg.Flatten().InnerExceptions.Select(i => i.Message));
        return string.Join(" | ", parts);
    }
}
