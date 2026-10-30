using System;
using System.IO;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R075 wiring: <see cref="AuditRunRecorder"/> records one product run (a CLI verb) in the persistent audit log.
/// It must open and verify the log before any work (fail loud), write exactly one entry however the run ends, keep
/// the exit code inside the hashed record, and hash its inputs honestly (a bundled case NAME is never passed off as
/// a content hash). The last tests are source-level anti-drift checks over the CLI wiring (the test project does
/// not reference the CLI).
/// </summary>
public sealed class AuditRunRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-auditrun-{Guid.NewGuid():N}");

    public AuditRunRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    [Fact]
    public void A_finished_run_writes_one_verifiable_entry_with_exit_code_inputs_and_outcome()
    {
        string input = Path.Combine(_dir, "m.json");
        File.WriteAllText(input, "{}");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "mark@i3", "estimate", ProvenanceTag.DerivedFromRecorded);
        run.AddInput(AuditInput.OfFile("measurements", input));
        run.SetOutcome("method=Wls status=Converged");
        DateTime state = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        run.StateTimeUtc = state;

        AuditAnchor? anchor = run.Finish(3);

        Assert.NotNull(anchor);
        Assert.Equal(1, anchor!.Count);
        AuditLogVerification v = AuditLogFile.Verify(LogPath, anchor);
        Assert.True(v.IsValid, v.Reason);
        AuditLogEntry e = Assert.Single(v.Entries);
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, e.Provenance);
        Assert.Equal(state, e.StateTimeUtc);
        Assert.Equal(
            "actor=mark@i3 action=estimate inputs=measurements:" + AuditInput.OfFile("measurements", input).Sha256Hex +
            " outcome=exit=3 method=Wls status=Converged", e.Summary);
        Assert.Equal(anchor, run.FinalAnchor);
    }

    [Fact]
    public void Finish_writes_exactly_once_however_often_it_is_reached()
    {
        // The CLI reaches Finish from both ProcessExit and UnhandledException; only the first call may write.
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "crossval", ProvenanceTag.Synthetic);
        Assert.NotNull(run.Finish(0));
        Assert.Null(run.Finish(1));
        Assert.Null(run.Finish(0));
        AuditLogEntry e = Assert.Single(AuditLogFile.Load(LogPath));
        Assert.Equal("actor=a action=crossval inputs=- outcome=exit=0", e.Summary);
    }

    [Fact]
    public void A_run_without_an_outcome_still_records_its_exit_code()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        run.Finish(2);
        Assert.EndsWith(" outcome=exit=2", Assert.Single(AuditLogFile.Load(LogPath)).Summary);
    }

    [Fact]
    public void The_log_is_released_after_finish_so_the_next_run_can_append()
    {
        AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic).Finish(0);
        AuditRunRecorder second = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        AuditAnchor? anchor = second.Finish(0);
        Assert.Equal(2, anchor!.Count);
        Assert.True(AuditLogFile.Verify(LogPath, anchor).IsValid);
    }

    [Fact]
    public void Start_holds_the_single_writer_lock_until_finish()
    {
        AuditRunRecorder first = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        Assert.Throws<InvalidOperationException>(() => AuditRunRecorder.Start(LogPath, "b", "estimate", ProvenanceTag.Synthetic));
        first.Finish(0);
    }

    [Fact]
    public void Start_refuses_a_tampered_log_before_any_work()
    {
        AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic).Finish(0);
        AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic).Finish(0);
        string[] lines = File.ReadAllLines(LogPath);
        lines[0] = lines[0].Replace("exit=0", "exit=1", StringComparison.Ordinal);
        File.WriteAllText(LogPath, string.Join("\n", lines) + "\n");
        Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic));
    }

    [Fact]
    public void Start_refuses_a_missing_directory_and_a_bad_actor_or_action_without_creating_a_file()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            AuditRunRecorder.Start(Path.Combine(_dir, "no-such-dir", "a.jsonl"), "a", "estimate", ProvenanceTag.Synthetic));
        Assert.Throws<ArgumentException>(() => AuditRunRecorder.Start(LogPath, "has space", "estimate", ProvenanceTag.Synthetic));
        Assert.Throws<ArgumentException>(() => AuditRunRecorder.Start(LogPath, "a", "", ProvenanceTag.Synthetic));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditRunRecorder.Start(LogPath, "a", "estimate", (ProvenanceTag)999));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Duplicate_inputs_a_multi_line_outcome_and_late_inputs_fail_loud()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Synthetic);
        run.AddInput(AuditInput.OfText("case-id", "ieee14"));
        Assert.Throws<ArgumentException>(() => run.AddInput(AuditInput.OfText("case-id", "ieee30")));
        Assert.Throws<ArgumentException>(() => run.SetOutcome("two\nlines"));
        Assert.Throws<ArgumentException>(() => run.SetOutcome(" "));
        run.Finish(0);
        Assert.Throws<InvalidOperationException>(() => run.AddInput(AuditInput.OfText("late", "x")));
        Assert.Throws<InvalidOperationException>(() => run.RecordNow("late"));
    }

    [Fact]
    public void RecordNow_writes_a_start_entry_before_the_final_one()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "interpose", ProvenanceTag.Recorded);
        run.AddInput(AuditInput.OfText("crosswalk", "map"));
        run.RecordNow("started listen=127.0.0.1:2404");
        run.SetOutcome("stopped accepted=5");
        AuditAnchor? anchor = run.Finish(0);
        Assert.Equal(2, anchor!.Count);
        var entries = AuditLogFile.Load(LogPath, anchor);
        Assert.EndsWith(" outcome=started listen=127.0.0.1:2404", entries[0].Summary);
        Assert.EndsWith(" outcome=exit=0 stopped accepted=5", entries[1].Summary);
    }

    [Fact]
    public void A_case_name_is_recorded_as_an_id_never_as_a_content_hash()
    {
        // "ieee14" is not a path here, so only the name can be hashed; the input is named case-id to say so.
        AuditInput named = AuditRunRecorder.InputForArgument("case", "ieee14-not-a-file-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith("case-id", named.Name, StringComparison.Ordinal);
        Assert.Equal("case-id", named.Name);

        string file = Path.Combine(_dir, "c.m");
        File.WriteAllText(file, "mpc");
        AuditInput fromFile = AuditRunRecorder.InputForArgument("case", file);
        Assert.Equal("case", fromFile.Name);
        Assert.Equal(AuditInput.OfFile("case", file).Sha256Hex, fromFile.Sha256Hex);
    }

    [Fact]
    public void A_directory_hash_covers_content_names_and_presence_and_ignores_enumeration_order()
    {
        string d = Path.Combine(_dir, "case");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(d, "system.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(d, "sub", "buses.json"), "[]");
        string h0 = AuditRunRecorder.HashDirectory(d);
        Assert.Matches(new Regex("^[0-9A-F]{64}$"), h0);
        Assert.Equal(h0, AuditRunRecorder.HashDirectory(d));                      // deterministic
        Assert.Equal(h0, AuditRunRecorder.InputForArgument("case", d).Sha256Hex); // a directory argument uses it

        // The same tree built in the opposite order hashes the same.
        string d2 = Path.Combine(_dir, "case2");
        Directory.CreateDirectory(Path.Combine(d2, "sub"));
        File.WriteAllText(Path.Combine(d2, "sub", "buses.json"), "[]");
        File.WriteAllText(Path.Combine(d2, "system.json"), "{\"a\":1}");
        Assert.Equal(h0, AuditRunRecorder.HashDirectory(d2));

        File.WriteAllText(Path.Combine(d, "system.json"), "{\"a\":2}");
        string h1 = AuditRunRecorder.HashDirectory(d);
        Assert.NotEqual(h0, h1);                                                   // content
        File.Move(Path.Combine(d, "sub", "buses.json"), Path.Combine(d, "sub", "bus.json"));
        string h2 = AuditRunRecorder.HashDirectory(d);
        Assert.NotEqual(h1, h2);                                                   // name
        File.WriteAllText(Path.Combine(d, "extra.json"), "");
        Assert.NotEqual(h2, AuditRunRecorder.HashDirectory(d));                    // presence

        Assert.Throws<DirectoryNotFoundException>(() => AuditRunRecorder.HashDirectory(Path.Combine(_dir, "absent")));
    }

    [UnixFact("file names with a line break or a backslash can be created only on Linux and macOS")]
    public void A_directory_hash_refuses_a_file_name_that_would_make_it_ambiguous()
    {
        // GR-847: "<path>:<hash>\n" lines are unambiguous only without a line break in the path, and on Unix a
        // backslash in a name would become '/', so "a\b" would hash as the file "b" in folder "a".
        string d = Path.Combine(_dir, "ambiguous-newline");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "x:0\ny"), "");
        Assert.Throws<InvalidDataException>(() => AuditRunRecorder.HashDirectory(d));

        string d2 = Path.Combine(_dir, "ambiguous-backslash");
        Directory.CreateDirectory(d2);
        File.WriteAllText(Path.Combine(d2, "a\\b"), "");
        Assert.Throws<InvalidDataException>(() => AuditRunRecorder.HashDirectory(d2));
    }

    // ---- CLI wiring (source-level anti-drift; the test project does not reference the CLI) ----

    private static string CliSource() => File.ReadAllText(CliPath()).Replace("\r\n", "\n");

    // src/GridSim.Cli/Program.cs under the repository root (the folder holding GridSim.slnx).
    private static string CliPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs");
    }

    [Fact]
    public void Cli_audit_log_is_a_known_flag_refused_on_unaudited_verbs_and_opened_before_the_verb_runs()
    {
        string src = CliSource();
        Assert.Contains("    \"--audit-log\",\n", src);
        // Moved 2026-10 (D-DOC-060, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the pinned list of audited
        // verbs left out serve, which runs the real-time estimator over recorded windows (the same evidence as estimate)
        // and wrote no audit entry: the defect D-DOC-060 reports. Before:
        //   Assert.Contains("if (auditVerb is not (\"estimate\" or \"crossval\" or \"interpose\"))", src);
        // After: the same check with "serve" added (serve's entry is proved end to end in Doc3AuditAnchorTests).
        Assert.Contains("if (auditVerb is not (\"estimate\" or \"crossval\" or \"interpose\" or \"serve\"))", src);
        Assert.Contains("--audit-log given more than once", src);
        // Opened before dispatch: the recorder is started before the first verb branch.
        int start = src.IndexOf("AuditRunRecorder.Start(", StringComparison.Ordinal);
        int dispatch = src.IndexOf("rest[0].Equals(\"estimate\"", StringComparison.Ordinal);
        Assert.True(start > 0 && dispatch > start, "the audit log must be opened before any verb is dispatched");
        // Every exit path records: ProcessExit (normal end and Environment.Exit) and UnhandledException.
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit += (_, _) => FinishAudit(Environment.ExitCode);", src);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", src);
    }

    [Fact]
    public void Cli_audited_verbs_record_their_inputs_and_outcome()
    {
        string src = CliSource();
        Assert.Contains("auditRun?.AddInput(GridSim.Core.Diagnostics.AuditRunRecorder.InputForArgument(\"case\", caseName));", src);
        Assert.Contains("auditRun?.AddInput(GridSim.Core.Diagnostics.AuditInput.OfFile(\"measurements\", measurementsPath!));", src);
        Assert.Contains("auditRun?.AddInput(GridSim.Core.Diagnostics.AuditRunRecorder.InputForArgument(\"case\", arg));", src);
        Assert.Contains("auditRun?.AddInput(GridSim.Core.Diagnostics.AuditInput.OfFile(\"crosswalk\", crosswalkPath));", src);
        // Moved 2026-10 (docs-programme finding "EstimationAuditTrail.Record is never called", authorised by Mark's
        // best-judgement ruling): the expectation pinned the CLI building its OWN estimate outcome line, which was the
        // duplication the finding reports. The outcome text now has one source, EstimationAuditTrail.Outcome in Core
        // (same fields, same text), and the CLI must call it. Before: Assert.Contains("$\"method={method}
        // status={est.Status}", src). After: the two assertions below.
        Assert.Contains("auditRun.SetOutcome(EstimationAuditTrail.Outcome(method.ToString(), est, set.Measurements.Count, extra));", src);
        string srcDir = Path.GetDirectoryName(Path.GetDirectoryName(CliPath()))!;   // .../src
        string trail = File.ReadAllText(Path.Combine(srcDir, "GridSim.Core", "Estimation", "EstimationAuditTrail.cs")).Replace("\r\n", "\n");
        Assert.Contains("$\"method={method} status={estimate.Status}", trail);
        Assert.Contains("$\"verdict=", src);
        Assert.Contains("$\"started protocol=", src);
        Assert.Contains("$\"stopped accepted=", src);
    }
}
