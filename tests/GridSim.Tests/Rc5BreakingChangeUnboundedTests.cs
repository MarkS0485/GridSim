using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal): breaking changes with no bound in <c>breaking-change validate</c>. B3 (a PSS/E DYR CSVGN1 record
/// read with <c>--dyr-extended --allow-unmodelled</c> now follows its control) claims only a change of model, so no number
/// can check what it moves. It is raised only for a job whose command line holds both options and whose DYR input holds
/// a CSVGN1 record; such a job is UNBOUNDED (B3), once, with each moved field and its size, and validate exits 4 (review
/// needed) when nothing else is wrong. <c>--accept B3</c> records the review beside the journal; later runs show the job
/// as REVIEWED (exit 0) while it moves no further, and flag it again on any larger movement or for any new job.
/// </summary>
public sealed class Rc5BreakingChangeUnboundedTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bcu");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static readonly BreakingChangeMap Map = BreakingChangeMap.Default;

    private const string Build = "2.1.107";

    private static readonly byte[] DyrWithCsvgn1 = Encoding.ASCII.GetBytes("3 'CSVGN1' 1 1 0.05 0.5 1 0.05 50 -50 0 /\n");
    private static readonly byte[] DyrWithout = Encoding.ASCII.GetBytes("1 'GENCLS' 1 6.0 0.0 /  a CSVGN1 named only in a comment\n");

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static readonly string[] B3Argv = ["stability", "net.raw", "--rms", "--dyn-data", "net.dyr", "--dyr-extended", "--allow-unmodelled", "--json", "s.json"];

    // A stability job recorded on 2.1.080 (B3, 2.1.083, is in its window) whose x_test field moved from 1.0 to `moved`.
    private static ReplayOutcome Job(long seq, IReadOnlyList<string> argv, IReadOnlyList<UnboundedChanges.DyrInput> dyr, double moved, string field = "x_test")
    {
        string verb = argv[0];
        FileComparison c = ResultCompare.Compare("s.json", Encoding.UTF8.GetBytes($$"""{"{{field}}":[1.0,2.0]}"""),
            Encoding.UTF8.GetBytes($$"""{"{{field}}":[{{N(moved)}},2.0]}"""), verb, s => s, s => s, VolatileFields.Default);
        var o = new ReplayOutcome { Seq = seq, RecordedUtc = "2026-11-01T00:00:00.0000000Z", Verb = verb, RecordedVersion = "2.1.080", Status = ReplayStatus.Changed };
        o.Fields.AddRange(c.Fields);
        UnboundedChanges.Judge(Map, o, Map.Explain("2.1.080", Build, verb, argv, ["/w/net.raw", "/w/net.dyr"]), verb, argv, dyr);
        return o;
    }

    // A dynamics-study job carrying both options (it cannot be recorded: the verb refuses --allow-unmodelled).
    private static readonly string[] StudyArgv = ["dynamics-study", "study.json", "--dyr-extended", "--allow-unmodelled", "--out", "s.json"];

    // A pf job carrying both options: pf reads no DYR file.
    private static readonly string[] PfArgv = ["pf", "case14", "--dyr-extended", "--allow-unmodelled", "--json", "s.json"];

    private static UnboundedChanges.DyrInput[] Dyr(byte[] bytes) => [new UnboundedChanges.DyrInput("net.dyr", bytes, null)];

    [Fact]
    public void A_b3_job_is_unbounded_once_with_its_fields_and_exits_4()
    {
        Assert.True(UnboundedChanges.IsUnbounded(Map.Entries.Single(b => b.Id == "B3")));
        // B27 (2.1.117) is the second: EXDC2, IEEEX1, ESAC8B, ESST3A and IEEEG3 records modelled on the same path.
        Assert.Equal(new[] { "B3", "B27", "B33" }, Map.Entries.Where(UnboundedChanges.IsUnbounded).Select(b => b.Id));

        ReplayOutcome o = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        Assert.Equal(ClaimVerdict.Unbounded, o.Verdict);
        Assert.Contains("B3", o.ExplainedBy);
        FieldDifference f = Assert.Single(o.Fields);
        Assert.Equal(ClaimVerdict.Unbounded, f.Verdict);
        Assert.Equal("B3", f.ExplainedBy);
        Assert.True(o.Flagged);
        Assert.False(o.Unexplained);
        Assert.Equal(ReplayReport.ReviewNeededExit, ReplayReport.ExitCode([o]));
        Assert.Equal(4, ReplayReport.ReviewNeededExit);

        string text = ReplayReport.ConsoleText([o], Build);
        Assert.Contains("unexplained=0 exceeds-claim=0 ambiguous=0", text, StringComparison.Ordinal);
        Assert.Contains("unbounded=1 reviewed=0", text, StringComparison.Ordinal);
        // One line for the job, naming every moved field with its size; never EXCEEDS-CLAIM.
        // 1.0 to 1.5 is 2^51 ulps, so 53 - 52 = 1 bit agrees.
        Assert.Contains("seq 7 stability (2.1.080): CHANGED, 1 field(s), worst s.json $.x_test[*] 1 of 53 bits agree, UNBOUNDED (B3), review needed: 1 field(s) moved: s.json $.x_test[*] abs 0.5 rel " + N(0.5 / 1.5), text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l.Contains("UNBOUNDED (B3)", StringComparison.Ordinal));
        Assert.DoesNotContain("EXCEEDS-CLAIM", text, StringComparison.Ordinal);
        Assert.Contains("--accept B3", text, StringComparison.Ordinal);

        // 2.1.127 (the B3 scope made strict): B3 is raised only when the DYR's recorded bytes hold a CSVGN1 record. A DYR
        // input whose bytes could not be read raises nothing and assumes nothing (it used to raise "B3 assumed"); validate
        // now makes such a job CANNOT-REPLAY ("input unreadable") before it is re-run, so no CLI job reaches Judge with one.
        ReplayOutcome unread = Job(9, B3Argv, [new UnboundedChanges.DyrInput("net.dyr", null, "access denied")], 1.5);
        // 2.1.130: the exact verdict and reason: the moved field is UNEXPLAINED (exit 3), explained by nothing, with no note.
        Assert.Equal(ClaimVerdict.Unexplained, unread.Verdict);
        Assert.Empty(unread.ExplainedBy);
        Assert.Null(unread.Reason);
        Assert.Equal(3, ReplayReport.ExitCode([unread]));
    }

    [Fact]
    public void A_job_outside_the_b3_condition_is_never_unbounded()
    {
        // Only --dyr-extended (no --allow-unmodelled): B3 cannot have acted, so the moved field is UNEXPLAINED (exit 3).
        string[] oneOption = B3Argv.Where(a => a != "--allow-unmodelled").ToArray();
        ReplayOutcome a = Job(1, oneOption, Dyr(DyrWithCsvgn1), 1.5);
        Assert.Equal(ClaimVerdict.Unexplained, a.Verdict);
        Assert.DoesNotContain("B3", a.ExplainedBy);
        Assert.Equal(3, ReplayReport.ExitCode([a]));
        // Both options, but the DYR holds no CSVGN1 record (the name in a comment after '/' does not count).
        ReplayOutcome b = Job(2, B3Argv, Dyr(DyrWithout), 1.5);
        Assert.Equal(ClaimVerdict.Unexplained, b.Verdict);
        Assert.False(UnboundedChanges.HoldsModel(DyrWithout, "CSVGN1"));
        Assert.True(UnboundedChanges.HoldsModel(DyrWithCsvgn1, "CSVGN1"));
        Assert.True(UnboundedChanges.HoldsModel(Encoding.ASCII.GetBytes("  3  csvgn1  1  0.05 /\n"), "CSVGN1"));
        Assert.False(UnboundedChanges.HoldsModel(Encoding.ASCII.GetBytes("3 'CSVGN10' 1 /\n"), "CSVGN1"));
        // Recorded after B3 landed: not in the window, whatever the options.
        FileComparison c = ResultCompare.Compare("s.json", Encoding.UTF8.GetBytes("""{"x_test":[1.0]}"""), Encoding.UTF8.GetBytes("""{"x_test":[1.5]}"""),
            "stability", s => s, s => s, VolatileFields.Default);
        var late = new ReplayOutcome { Seq = 3, Verb = "stability", RecordedVersion = "2.1.090", Status = ReplayStatus.Changed };
        late.Fields.AddRange(c.Fields);
        UnboundedChanges.Judge(Map, late, Map.Explain("2.1.090", Build, "stability", B3Argv, []), "stability", B3Argv, Dyr(DyrWithCsvgn1));
        Assert.NotEqual(ClaimVerdict.Unbounded, late.Verdict);
        // Both options but no DYR input: a pf job, and a stability job whose --dyn-data was a JSON file (validate records no
        // DYR input for it). Neither read a DYR, so B3 cannot have acted and nothing is assumed.
        ReplayOutcome pf = Job(4, PfArgv, [], 1.5);
        string[] jsonDynData = B3Argv.Select(x => x == "net.dyr" ? "net.json" : x).ToArray();
        ReplayOutcome json = Job(5, jsonDynData, [], 1.5);
        foreach (ReplayOutcome o in new[] { pf, json })
        {
            Assert.NotEqual(ClaimVerdict.Unbounded, o.Verdict);
            Assert.DoesNotContain("B3", o.ExplainedBy);
            Assert.True(o.Reason is null || !o.Reason.Contains("assumed", StringComparison.Ordinal), o.Reason);
        }
        Assert.False(UnboundedChanges.Applies(Map.Entries.Single(x => x.Id == "B3"), "pf", PfArgv, []).Applies);
        // 2.1.122: a dynamics-study job is not raised either. The verb refuses --allow-unmodelled (exit 2), so validate can
        // never record such a job, and it reads its DYR without the extended library, where B3 cannot act.
        Assert.False(UnboundedChanges.Applies(Map.Entries.Single(x => x.Id == "B3"), "dynamics-study", StudyArgv, []).Applies);
        Assert.Empty(UnboundedChanges.Review([a, b, late, pf, json], new AcceptFile(), ["B3"], Build));
    }

    [Fact]
    public void Accept_then_rerun_is_reviewed_and_any_larger_or_new_movement_is_flagged_again()
    {
        var file = new AcceptFile();
        ReplayOutcome first = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        IReadOnlyList<UnboundedChanges.ReviewRow> rows = UnboundedChanges.Review([first], file, ["B3"], Build);
        Assert.Equal(ClaimVerdict.Reviewed, first.Verdict);
        Assert.Equal(new UnboundedChanges.ReviewRow(7, "B3", "accepted now", "1 field(s) recorded in the accept file"), Assert.Single(rows));
        Assert.Equal(0, ReplayReport.ExitCode([first]));
        Assert.False(first.Flagged);

        // The file round-trips through its text.
        AcceptFile reread = AcceptFile.Parse(file.Json());
        Assert.Equal(file.Json(), reread.Json());
        AcceptRecord rec = Assert.Single(reread.Records);
        Assert.Equal((7L, "B3", "2.1.080", Build), (rec.Seq, rec.Id, rec.RecordedVersion, rec.AcceptedOn));
        AcceptedField af = Assert.Single(rec.Fields);
        Assert.Equal(("s.json", "$.x_test[*]", 0.5, 0.5 / 1.5), (af.File, af.Field, af.MaxAbs!.Value, af.MaxRel!.Value));

        // A later run, no --accept: the same movement, and a smaller one, are REVIEWED; exit 0.
        ReplayOutcome same = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        ReplayOutcome smaller = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.25);
        Assert.Equal("accepted earlier", UnboundedChanges.Review([same], reread, [], Build).Single().Outcome);
        UnboundedChanges.Review([smaller], reread, [], Build);
        Assert.Equal(ClaimVerdict.Reviewed, same.Verdict);
        Assert.Equal(ClaimVerdict.Reviewed, smaller.Verdict);
        Assert.Equal(0, ReplayReport.ExitCode([same]));
        string text = ReplayReport.ConsoleText([same], Build);
        Assert.Contains("REVIEWED (B3)", text, StringComparison.Ordinal);
        Assert.Contains("unbounded=0 reviewed=1", text, StringComparison.Ordinal);

        // A larger movement of the same job is flagged again, saying which field and by how much.
        ReplayOutcome larger = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.75);
        UnboundedChanges.ReviewRow r = UnboundedChanges.Review([larger], reread, [], Build).Single();
        Assert.Equal(ClaimVerdict.Unbounded, larger.Verdict);
        Assert.Equal("needs review", r.Outcome);
        Assert.StartsWith("s.json $.x_test[*] moved further than accepted (abs 0.75 rel " + N(0.75 / 1.75), r.Detail, StringComparison.Ordinal);
        Assert.EndsWith("; accepted abs 0.5 rel " + N(0.5 / 1.5) + ")", r.Detail, StringComparison.Ordinal);
        Assert.Equal(4, ReplayReport.ExitCode([larger]));
        // A field that did not move before is flagged again.
        ReplayOutcome otherField = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.5, field: "y_test");
        Assert.Equal("s.json $.y_test[*] moved, and was not accepted before", UnboundedChanges.Review([otherField], reread, [], Build).Single().Detail);
        Assert.Equal(ClaimVerdict.Unbounded, otherField.Verdict);
        // A new job (another sequence number, or the same number recorded at another time) is flagged again.
        ReplayOutcome newJob = Job(8, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        Assert.Equal("B3 not yet accepted for this job", UnboundedChanges.Review([newJob], reread, [], Build).Single().Detail);
        Assert.Equal(ClaimVerdict.Unbounded, newJob.Verdict);

        // Accepting the larger movement keeps the larger size per field.
        ReplayOutcome again = Job(7, B3Argv, Dyr(DyrWithCsvgn1), 1.75);
        UnboundedChanges.Review([again], reread, ["B3"], Build);
        Assert.Equal(0.75, Assert.Single(Assert.Single(reread.Records).Fields).MaxAbs);
    }

    [Fact]
    public void Exit_codes_put_input_errors_first_then_verdicts_then_review_needed()
    {
        ReplayOutcome unbounded = Job(1, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        ReplayOutcome unexplained = Job(2, B3Argv.Where(a => a != "--allow-unmodelled").ToArray(), Dyr(DyrWithCsvgn1), 1.5);
        var identical = new ReplayOutcome { Seq = 3, Status = ReplayStatus.Identical };
        var cannot = new ReplayOutcome { Seq = 4, Status = ReplayStatus.CannotReplay, Reason = "input missing: x" };
        Assert.Equal(4, ReplayReport.ExitCode([identical, unbounded]));
        Assert.Equal(3, ReplayReport.ExitCode([unbounded, unexplained]));
        Assert.Equal(1, ReplayReport.ExitCode([unbounded, cannot]));
        Assert.Equal(0, ReplayReport.ExitCode([identical]));
        ReplayOutcome reviewed = Job(5, B3Argv, Dyr(DyrWithCsvgn1), 1.5);
        UnboundedChanges.Review([reviewed], new AcceptFile(), ["B3"], Build);
        Assert.Equal(0, ReplayReport.ExitCode([identical, reviewed]));
        Assert.Equal(4, ReplayReport.ExitCode([reviewed, unbounded]));
    }

    // ---- the CLI end to end ----------------------------------------------------------------------------------------

    private CliProcess.Result Validate(string journal, params string[] more) =>
        JournalTestSupport.Run(_dir, new[] { "breaking-change", "validate", "--journal", journal }.Concat(more).ToArray());

    // Rewrite the one journal entry as recorded on 2.1.080 (B3 in its window), with the result's new hash.
    private static void AsRecordedOn2_1_080(string journal, string result)
    {
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        e.Version = "2.1.080";
        byte[] bytes = File.ReadAllBytes(result);
        foreach (JournalOutput o in e.Outputs.Where(o => o.Abs == Path.GetFullPath(result)))
        {
            o.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            o.Bytes = bytes.LongLength;
        }
        File.WriteAllText(journal, JournalJson.Line(e) + "\n", new UTF8Encoding(false));
    }

    private static void SetP(string result, double factor)
    {
        JsonNode doc = JsonNode.Parse(File.ReadAllText(result))!;
        JsonArray buses = doc["tables"]!["buses"]!.AsArray();
        buses[5]!["p_inj_mw"] = buses[5]!["p_inj_mw"]!.GetValue<double>() * factor;
        File.WriteAllText(result, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    [Fact]
    public void The_cli_raises_no_unbounded_change_for_a_pf_job_that_read_no_dyr()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        // A pf run carrying both options. They have no effect on pf, which reads no DYR file, so the job records no DYR
        // input. B3 and B27 are both in the job's window, but neither can have acted, so neither is raised or assumed: the
        // moved field is judged by the bounded changes alone (beyond every bound), and there is nothing to accept.
        CliProcess.Result run = JournalTestSupport.Run(work, "--journal", journal, "pf", "case14", "--json", "pf.json", "--dyr-extended", "--allow-unmodelled");
        Assert.True(run.ExitCode == 0, run.ToString());
        string pf = Path.Combine(work, "pf.json");
        SetP(pf, -1.0);   // the recorded run's bus-6 injection had the other sign: 0 agreeing bits, beyond every bound
        AsRecordedOn2_1_080(journal, pf);

        CliProcess.Result first = Validate(journal, "--json", Path.Combine(_dir, "v1.json"));
        Assert.True(first.ExitCode == 3, first.ToString());
        Assert.DoesNotContain("UNBOUNDED", first.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("assumed", first.StdOut, StringComparison.Ordinal);
        Assert.Contains("unbounded=0 reviewed=0", first.StdOut, StringComparison.Ordinal);
        using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "v1.json"))))
        {
            Assert.Equal(0, d.RootElement.GetProperty("summary").GetProperty("changed_unbounded").GetInt32());
            JsonElement entry = d.RootElement.GetProperty("tables").GetProperty("entries")[0];
            Assert.NotEqual(ClaimVerdict.Unbounded, entry.GetProperty("claim_check").GetString());
            if (d.RootElement.GetProperty("tables").TryGetProperty("reviews", out JsonElement reviews))
                Assert.Equal(0, reviews.GetArrayLength());
        }
        string acceptFile = Path.Combine(_dir, "j", "journal.accept.json");

        // A bounded change cannot be accepted; an ID that names nothing is a usage error.
        Assert.Equal(2, Validate(journal, "--accept", "B5").ExitCode);
        Assert.Equal(2, Validate(journal, "--accept", "B99").ExitCode);

        // Accepting B3 accepts nothing: no job moved under it, so no file is written and the job is not REVIEWED.
        CliProcess.Result accept = Validate(journal, "--accept", "B3");
        Assert.True(accept.ExitCode == 3, accept.ToString());
        Assert.Contains("--accept B3: no job moved under B3 in this run; nothing recorded.", accept.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("REVIEWED", accept.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(acceptFile));
    }
}
