using System.Globalization;
using System.Text.Json;
using GridSim.Core.Journal;
using Xunit;
using static GridSim.Tests.Rc5DynamicsReplaySupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.127): <c>breaking-change validate</c> re-runs the dynamics verbs. Before this release it re-ran no stability
/// job ("not yet classified") and no verb it re-ran took a DYR on its command line, so B3, B10, B11, B19, B20, B23 and B27
/// could not be checked for any real CLI job. These tests run real jobs end to end through the built CLI:
/// <list type="bullet">
/// <item>the upgrade simulation on real stability jobs (one with a real DYR): a recorded result is perturbed as an older
/// build might have written it, and validate gives EXPLAINED (within B10's bound), EXCEEDS-CLAIM (past it) and UNBOUNDED
/// (B27, which has no bound); <c>--accept B27</c> makes the job REVIEWED, it stays REVIEWED, and a larger movement flags it
/// again;</item>
/// <item>a DYR that is gone (on the command line, or named inside a study file) makes the job CANNOT-REPLAY naming it, and
/// the snapshot store replays it IDENTICAL;</item>
/// <item>a plug-in assembly that changed or is gone makes the job CANNOT-REPLAY naming it, before any re-run starts;</item>
/// <item>the report is the same bytes for <c>--parallel 1</c> and <c>--parallel 8</c>.</item>
/// </list>
/// Every perturbation is built from the recorded value itself, and every expected figure is computed from the two values
/// with the report's own definitions (technical ch. 42), so nothing here is pinned from the code under test.
/// </summary>
public sealed class Rc5DynamicsReplayTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("dynreplay");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ---- 1. the upgrade simulation ----------------------------------------------------------------------------------

    /// <summary>
    /// Two real stability jobs with a line-to-line sequence fault, so their export has the fault-point trace, whose z2
    /// columns B10 (2.1.098, the negative-sequence network now carries the dynamic loads) bounds at a relative change of
    /// 0.375. Recorded on this build and labelled 2.1.080, with one recorded z2_re value changed as an older build might
    /// have written it: by a factor 1.25 in the first job (relative change 0.25 / 1.25 = 0.2, inside the bound) and 2 in
    /// the second (0.5, past it). The re-run gives the original value back, so the first job is CHANGED and EXPLAINED by
    /// B10, and the second EXCEEDS-CLAIM of B10, with the exact figures. Validate exits 3.
    /// </summary>
    [Fact]
    public void A_moved_z2_within_b10_is_explained_and_beyond_it_exceeds_the_claim()
    {
        string work = P("seq-work");
        Directory.CreateDirectory(work);
        string journal = P("ja", "journal.jsonl");
        Record(work, journal, SequenceFaultJob("ev1"));
        Record(work, journal, SequenceFaultJob("ev2"));
        string f1 = Path.Combine(work, "ev1", "rms-fault-points.csv"), f2 = Path.Combine(work, "ev2", "rms-fault-points.csv");
        Assert.Equal(File.ReadAllText(f1).Split('\n').Where(l => !l.StartsWith('#')), File.ReadAllText(f2).Split('\n').Where(l => !l.StartsWith('#')));
        (double a1, double p1) = PerturbCsv(f1, "z2_re", x => x * 1.25);
        (double a2, double p2) = PerturbCsv(f2, "z2_re", x => x * 2.0);
        Relabel(journal, "2.1.080", f1, f2);

        string report = P("va.json");
        CliProcess.Result v = Validate(_dir, journal, "--json", report);
        Assert.True(v.ExitCode == 3, v.ToString());
        string line1 = Line(v, 1), line2 = Line(v, 2);
        Assert.StartsWith("  seq 1 stability (2.1.080): CHANGED, 1 field(s), worst rms-fault-points.csv z2_re", line1, StringComparison.Ordinal);
        Assert.Contains(", EXPLAINED by B10", line1, StringComparison.Ordinal);
        Assert.StartsWith("  seq 2 stability (2.1.080): CHANGED, 1 field(s), worst rms-fault-points.csv z2_re", line2, StringComparison.Ordinal);
        Assert.Contains(", EXCEEDS-CLAIM of B10", line2, StringComparison.Ordinal);

        // The figures, from the recorded (perturbed) value a and the re-run's (original) value b.
        List<JsonElement> fields = Table(report, "field_differences");
        Assert.Equal(2, fields.Count);
        foreach ((JsonElement f, long seq, double recorded, double original, string verdict) in new[]
        {
            (fields[0], 1L, p1, a1, ClaimVerdict.Explained), (fields[1], 2L, p2, a2, ClaimVerdict.ExceedsClaim),
        })
        {
            Assert.Equal(seq, f.GetProperty("seq").GetInt64());
            Assert.Equal("rms-fault-points.csv", f.GetProperty("file").GetString());
            Assert.Equal("z2_re", f.GetProperty("field").GetString());
            Assert.Equal(1, f.GetProperty("count").GetInt64());
            double abs = Math.Abs(original - recorded);
            Assert.Equal(abs, f.GetProperty("max_abs_diff").GetDouble());
            Assert.Equal(abs / Math.Max(Math.Abs(recorded), Math.Abs(original)), f.GetProperty("max_rel_diff").GetDouble());
            Assert.Equal(verdict, f.GetProperty("claim_check").GetString());
            Assert.Equal("B10", f.GetProperty("explained_by").GetString());
        }
        // The relative changes sit either side of B10's 0.375, as built: 0.25/1.25 = 0.2 and 1/2 = 0.5 (to rounding).
        Assert.InRange(fields[0].GetProperty("max_rel_diff").GetDouble(), 0.2 - 1e-15, 0.2 + 1e-15);
        Assert.InRange(fields[1].GetProperty("max_rel_diff").GetDouble(), 0.5 - 1e-15, 0.5 + 1e-15);
        // Every other output (the machine and summary files, run-metadata.json with its host and build fields left out,
        // and the CSV run header lines) is the same bits: one field moved in each job.
        Assert.Equal(new[] { ReplayStatus.Changed, ReplayStatus.Changed }, Table(report, "entries").Select(e => e.GetProperty("status").GetString()));
    }

    /// <summary>
    /// The job the 2.1.122 review asked for and that build could not run: a real stability job with a real DYR, holding
    /// ESST3A (one of B27's records) and no CSVGN1, run with both <c>--dyr-extended</c> and <c>--allow-unmodelled</c>.
    /// One recorded rotor angle is changed by a relative 2^-20. No bounded change claims delta_rad, B27 has no bound and its
    /// condition holds, and B3's does not (no CSVGN1 record), so the job is UNBOUNDED (B27) and not B3, and validate exits 4
    /// (review needed). <c>--accept B27</c> makes it REVIEWED (exit 0) and writes the accept file; the next validate still
    /// reads REVIEWED (accepted earlier). The recorded angle is then moved a further relative 2^-10: the job is UNBOUNDED
    /// again, exit 4, and the review row says it moved further than accepted.
    /// </summary>
    [Fact]
    public void A_stability_job_with_a_real_dyr_is_unbounded_b27_then_reviewed_then_flagged_again_when_it_moves_further()
    {
        string work = P("b27-work");
        WriteCase14(work);
        string journal = P("jb", "journal.jsonl");
        Record(work, journal, Case14StabilityJob("ev"));
        JournalEntry recorded = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        Assert.Contains(recorded.Tokens, t => t.Kind == "input" && t.Raw == "case14.dyr" && t.Sha256 == Sha(Path.Combine(work, "case14.dyr")));
        Assert.Equal("safe", recorded.ReplayClass);
        string machines = Path.Combine(work, "ev", "rms-events-machines.csv");
        PerturbCsv(machines, "delta_rad", x => x * (1.0 + Math.Pow(2, -20)));
        Relabel(journal, "2.1.080", machines);

        string r1 = P("v1.json");
        CliProcess.Result first = Validate(_dir, journal, "--json", r1);
        Assert.True(first.ExitCode == 4, first.ToString());
        string line = Line(first, 1);
        Assert.Contains("UNBOUNDED (B27), review needed: 1 field(s) moved: rms-events-machines.csv delta_rad", line, StringComparison.Ordinal);
        JsonElement entry = Table(r1, "entries")[0];
        Assert.Equal(ClaimVerdict.Unbounded, entry.GetProperty("claim_check").GetString());
        string[] ids = entry.GetProperty("explained_by").GetString()!.Split(';');
        Assert.Contains("B27", ids);
        Assert.DoesNotContain("B3", ids);   // both options, but no CSVGN1 record: B3 is not raised
        Assert.DoesNotContain("assumed", first.StdOut, StringComparison.Ordinal);
        JsonElement review = Assert.Single(Table(r1, "reviews"));
        Assert.Equal(("B27", "needs review"), (review.GetProperty("id").GetString(), review.GetProperty("outcome").GetString()));

        string acceptFile = P("jb", "journal.accept.json");
        CliProcess.Result accept = Validate(_dir, journal, "--accept", "B27");
        Assert.True(accept.ExitCode == 0, accept.ToString());
        Assert.Contains("REVIEWED (B27)", Line(accept, 1), StringComparison.Ordinal);
        Assert.True(File.Exists(acceptFile));
        Assert.Contains("\"id\": \"B27\"", File.ReadAllText(acceptFile), StringComparison.Ordinal);

        string r2 = P("v2.json");
        CliProcess.Result again = Validate(_dir, journal, "--json", r2);
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Contains("REVIEWED (B27)", Line(again, 1), StringComparison.Ordinal);
        Assert.Equal("accepted earlier", Assert.Single(Table(r2, "reviews")).GetProperty("outcome").GetString());

        // A larger movement of the same field: flagged again.
        PerturbCsv(machines, "delta_rad", x => x * (1.0 + Math.Pow(2, -10)));
        Relabel(journal, "2.1.080", machines);
        string r3 = P("v3.json");
        CliProcess.Result moved = Validate(_dir, journal, "--json", r3);
        Assert.True(moved.ExitCode == 4, moved.ToString());
        Assert.Contains("UNBOUNDED (B27), review needed", Line(moved, 1), StringComparison.Ordinal);
        JsonElement flagged = Assert.Single(Table(r3, "reviews"));
        Assert.Equal("needs review", flagged.GetProperty("outcome").GetString());
        Assert.Contains("moved further than accepted", flagged.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // ---- 2. a DYR that is gone --------------------------------------------------------------------------------------

    /// <summary>The DYR of a stability job (its <c>--dyn-data</c>) is deleted after the run. With no snapshot the job is
    /// CANNOT-REPLAY naming the file, exit 1, and nothing is re-run; recorded with <c>--journal-snapshot</c>, the DYR comes
    /// from the store and the job is IDENTICAL, exit 0.</summary>
    [Fact]
    public void A_missing_dyr_on_the_command_line_is_cannot_replay_naming_it_and_the_snapshot_replays_it()
    {
        string plain = P("plain");
        WriteCase14(plain);
        string j1 = P("j1", "journal.jsonl");
        Record(plain, j1, Case14StabilityJob("ev"));
        JournalTestSupport.FromAnotherBuild(j1);
        string dyr = Path.Combine(plain, "case14.dyr");
        File.Delete(dyr);
        string report = P("v1.json");
        CliProcess.Result v = Validate(_dir, j1, "--json", report);
        Assert.True(v.ExitCode == 1, v.ToString());
        Assert.Contains($": CANNOT-REPLAY: input missing: {dyr}", Line(v, 1), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Table(report, "entries")[0].GetProperty("replay_exit").ValueKind);

        string kept = P("kept");
        WriteCase14(kept);
        string j2 = P("j2", "journal.jsonl");
        RecordWithSnapshot(kept, j2, Case14StabilityJob("ev"));
        JournalTestSupport.FromAnotherBuild(j2);
        File.Delete(Path.Combine(kept, "case14.dyr"));
        CliProcess.Result s = Validate(_dir, j2);
        Assert.True(s.ExitCode == 0, s.ToString());
        Assert.EndsWith(": IDENTICAL", Line(s, 1), StringComparison.Ordinal);
    }

    /// <summary>A dynamics-study job: the DYR is named inside the study file, not on the command line. The journal now
    /// hashes it (and the RAW) with the study. Deleted with no snapshot: CANNOT-REPLAY naming the DYR, exit 1. With the
    /// snapshot store, the study, RAW and DYR are placed together in the re-run's scratch folder in the study's layout, the
    /// study's own SHA-256 pins still hold, and the job is IDENTICAL.</summary>
    [Fact]
    public void A_missing_dyr_named_inside_a_study_is_cannot_replay_naming_it_and_the_snapshot_replays_it()
    {
        string plain = P("study-plain");
        WriteStudy(plain);
        string j1 = P("s1", "journal.jsonl");
        Record(plain, j1, ["dynamics-study", "study.json", "--out", "result.json"]);
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(j1)[0], out _)!;
        string dyr = Path.Combine(plain, "case14.dyr");
        Assert.Contains(e.CaseFiles, c => c.Label == "study:dyr" && c.Path == "case14.dyr" && c.Abs == dyr && c.Sha256 == Sha(dyr));
        Assert.Contains(e.CaseFiles, c => c.Label == "study:raw" && c.Path == "case14.raw");
        JournalTestSupport.FromAnotherBuild(j1);
        File.Delete(dyr);
        CliProcess.Result v = Validate(_dir, j1);
        Assert.True(v.ExitCode == 1, v.ToString());
        Assert.Contains($": CANNOT-REPLAY: input missing: {dyr}", Line(v, 1), StringComparison.Ordinal);

        string kept = P("study-kept");
        WriteStudy(kept);
        string j2 = P("s2", "journal.jsonl");
        RecordWithSnapshot(kept, j2, ["dynamics-study", "study.json", "--out", "result.json"]);
        JournalTestSupport.FromAnotherBuild(j2);
        File.Delete(Path.Combine(kept, "case14.dyr"));
        CliProcess.Result s = Validate(_dir, j2);
        Assert.True(s.ExitCode == 0, s.ToString());
        Assert.EndsWith(": IDENTICAL", Line(s, 1), StringComparison.Ordinal);
    }

    // ---- 3. a plug-in that changed ----------------------------------------------------------------------------------

    /// <summary>
    /// A dynamics-study job with a model plug-in (<c>--model-plugins</c>). SEC-27 (2.1.156): it is never re-run, untouched,
    /// with its assembly's bytes replaced (by another valid plug-in) or with the assembly gone: CANNOT-REPLAY, and no re-run
    /// starts (no replay exit), so no assembly is ever loaded by validate. The assembly is never kept in the snapshot store.
    /// </summary>
    [Fact]
    public void A_changed_or_missing_plugin_assembly_is_cannot_replay_and_never_loaded()
    {
        string work = P("plugin");
        WriteStudy(work);
        WritePlugin(work);
        string[] job = ["dynamics-study", "study.json", "--model-plugins", "plugins.json", "--model-assignments", "assign.json", "--out", "result.json"];
        string journal = P("jp", "journal.jsonl");
        RecordWithSnapshot(work, journal, job);
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        string dll = Path.Combine(work, "sample.dll");
        JournalCaseFile assembly = Assert.Single(e.CaseFiles, c => c.Label.StartsWith(DynamicsInputs.PluginAssemblyLabel, StringComparison.Ordinal));
        Assert.Equal((dll, Sha(dll)), (assembly.Abs, assembly.Sha256));
        Assert.True(assembly.Snapshot != true, "a plug-in assembly is never kept in the snapshot store");
        Assert.False(File.Exists(JournalSnapshot.ObjectPath(JournalSnapshot.ObjectsDir(journal), Sha(dll))));
        JournalTestSupport.FromAnotherBuild(journal);

        // SEC-27 (2.1.156): a re-run never loads code, so a plug-in job is CANNOT-REPLAY by the replay policy whatever the
        // assembly's state: untouched, replaced or gone, and no re-run starts (no replay exit). The pins used to read IDENTICAL
        // for the untouched assembly and "plug-in assembly changed / missing" for the others, because plug-in jobs were
        // re-runnable; ruling 4 of the 2026-11-25 bug hunt makes them never re-runnable (a journal line or an imported result
        // file could carry the plug-in configuration and its assembly with it).
        const string Never = ": CANNOT-REPLAY: not replayable: --model-plugins loads plug-in code, and a re-run never loads code";
        string sameReport = P("same.json");
        CliProcess.Result same = Validate(_dir, journal, "--json", sameReport);
        Assert.True(same.ExitCode == 1, same.ToString());
        Assert.EndsWith(Never, Line(same, 1), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Table(sameReport, "entries")[0].GetProperty("replay_exit").ValueKind);

        string pinned = Sha(dll);
        File.Copy(BuiltDll("GridSim.ModelPlugin.LeakSample"), dll, overwrite: true);
        Assert.NotEqual(pinned, Sha(dll));
        string changedReport = P("changed.json");
        CliProcess.Result changed = Validate(_dir, journal, "--json", changedReport);
        Assert.True(changed.ExitCode == 1, changed.ToString());
        Assert.EndsWith(Never, Line(changed, 1), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Table(changedReport, "entries")[0].GetProperty("replay_exit").ValueKind);

        File.Delete(dll);
        CliProcess.Result missing = Validate(_dir, journal);
        Assert.True(missing.ExitCode == 1, missing.ToString());
        Assert.EndsWith(Never, Line(missing, 1), StringComparison.Ordinal);
    }

    // ---- 4. the report does not depend on --parallel ----------------------------------------------------------------

    /// <summary>
    /// A journal of dynamics jobs of every re-run kind (stability with an RMS event export, with a DYR and with the
    /// classical export; dynamics-study; long-term; trip; modal), one of them with a moved result and one with a missing
    /// input, validated with <c>--parallel 1</c> and <c>--parallel 8</c>. The console, the CSV report (every file of it) and
    /// the JSON report apart from its <c>args</c> are byte for byte the same. Each child is its own process in its own
    /// scratch folder, single-threaded, and the report is built in journal order after every job has finished
    /// (technical ch. 42), so the parallelism cannot reach it.
    /// </summary>
    [Fact]
    public void The_dynamics_report_is_the_same_bytes_for_parallel_1_and_8()
    {
        string work = P("par");
        WriteCase14(work);
        WriteStudy(Path.Combine(work, "study"));
        File.WriteAllText(Path.Combine(work, "lt.json"), LongTermCase9);
        File.WriteAllText(Path.Combine(work, "lt-gone.json"), LongTermCase9);
        string journal = P("jq", "journal.jsonl");
        Record(work, journal, Case14StabilityJob("ev-dyr"));
        Record(work, journal, SequenceFaultJob("ev-seq"));
        Record(work, journal, ["stability", "case9", "--export", "classical.json"]);
        Record(work, journal, ["dynamics-study", Path.Combine("study", "study.json"), "--out", "study-result.json"]);
        Record(work, journal, ["long-term", "case9", "lt.json", "--out", "lt.csv"]);
        Record(work, journal, ["trip", "case9", "--export", "trip.csv"], 0, 3);
        Record(work, journal, ["modal", "case9", "--export", "modal.json"], 0, 1);
        Record(work, journal, ["long-term", "case9", "lt-gone.json", "--out", "lt-gone.csv"]);
        string seqCsv = Path.Combine(work, "ev-seq", "rms-fault-points.csv");
        PerturbCsv(seqCsv, "z2_re", x => x * 1.25);
        JournalTestSupport.FromAnotherBuild(journal);
        Relabel(journal, "2.1.080", seqCsv);
        File.Delete(Path.Combine(work, "lt-gone.json"));   // the last job's input is gone: CANNOT-REPLAY, part of the report too

        CliProcess.Result one = Validate(_dir, journal, "--parallel", "1", "--csv", P("r1", "report.csv"), "--json", P("r1", "report.json"));
        CliProcess.Result eight = Validate(_dir, journal, "--parallel", "8", "--csv", P("r8", "report.csv"), "--json", P("r8", "report.json"));
        Assert.True(one.ExitCode == 1, one.ToString());
        Assert.Equal(one.ExitCode, eight.ExitCode);
        Assert.Equal(one.StdOut, eight.StdOut);
        Assert.Contains("stability (2.1.080): CHANGED", one.StdOut, StringComparison.Ordinal);
        Assert.Contains("long-term (2.1.080): CANNOT-REPLAY: input missing", one.StdOut, StringComparison.Ordinal);
        foreach (string verb in new[] { "dynamics-study", "long-term", "trip", "modal" })
            Assert.Contains($" {verb} (2.1.080): IDENTICAL", one.StdOut, StringComparison.Ordinal);
        string[] csv1 = Directory.GetFiles(P("r1"), "report*.csv").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        string[] csv8 = Directory.GetFiles(P("r8"), "report*.csv").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(csv1, csv8);
        Assert.True(csv1.Length > 1);
        foreach (string f in csv1) Assert.Equal(File.ReadAllBytes(P("r1", f)), File.ReadAllBytes(P("r8", f)));
        Assert.Equal(WithoutArgs(P("r1", "report.json")), WithoutArgs(P("r8", "report.json")));
    }

    private static string WithoutArgs(string report)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report))!.AsObject();
        Assert.True(node.Remove("args"));
        return node.ToJsonString();
    }
}
