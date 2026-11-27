using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 review R6 finding 2: B33 (2.1.144, the DC model per MATPOWER makeBdc) has no bound, so <c>breaking-change validate</c>
/// raises it only for a job it can really have moved: a job that runs the DC model on a case with a term makeBdc adds (an
/// in-service tap other than 0 or 1, a phase shift or a bus Gs), decided with <see cref="DcPowerFlow.UsesMakeBdcTerms"/> on
/// the case the re-run loads. A tap-free case is computed by the earlier instructions bit for bit, so a change there is not
/// B33's; the AC modes of its verbs (<c>nminus1</c>, <c>sensitivity --kind tap|dvdq|effectiveness</c>) never run the DC
/// model. Before, B33 was raised for every job of its 14 verbs, so a real regression there read "review B33".
/// </summary>
public sealed class Rc5B33ValidateScopeTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("b33v");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static readonly BreakingChangeMap Map = BreakingChangeMap.Default;

    private static BreakingChange B33 => Map.Entries.Single(b => b.Id == "B33");

    // ---- the condition, in Core ----------------------------------------------------------------------------------

    [Fact]
    public void The_bundled_case9_has_no_makebdc_term_and_case14_has_one()
    {
        // The premises of the tests below, checked on the models themselves: case9's transformers have tap 0 (read as 1)
        // and no shift and no bus has Gs; case14's three transformers have taps 0.978, 0.969 and 0.932.
        Assert.False(DcPowerFlow.UsesMakeBdcTerms(IeeeCases.Case9()));
        Assert.True(DcPowerFlow.UsesMakeBdcTerms(IeeeCases.Case14()));
    }

    [Fact]
    public void B33_is_raised_only_for_a_dc_job_whose_case_has_a_makebdc_term()
    {
        string[] dcpf = ["dcpf", "case14", "--json", "d.json"];
        Assert.True(UnboundedChanges.Applies(B33, "dcpf", dcpf, [], caseUsesMakeBdcTerms: true).Applies);
        Assert.False(UnboundedChanges.Applies(B33, "dcpf", ["dcpf", "case9", "--json", "d.json"], [], caseUsesMakeBdcTerms: false).Applies);
        // A case that was not read raises nothing (validate makes such a job CANNOT-REPLAY before it is re-run).
        Assert.False(UnboundedChanges.Applies(B33, "dcpf", dcpf, [], caseUsesMakeBdcTerms: null).Applies);
        Assert.False(UnboundedChanges.Applies(B33, "dcpf", dcpf, []).Applies);
    }

    [Fact]
    public void The_ac_modes_of_b33s_verbs_never_raise_it_even_on_a_tapped_case()
    {
        foreach (string kind in new[] { "tap", "dvdq", "effectiveness", "DVDQ" })
        {
            string[] argv = ["sensitivity", "case14", "--kind", kind, "--bus", "9", "--json", "s.json"];
            Assert.False(UnboundedChanges.RunsDcModel("sensitivity", argv), kind);
            Assert.False(UnboundedChanges.Applies(B33, "sensitivity", argv, [], caseUsesMakeBdcTerms: true).Applies, kind);
        }
        foreach (string kind in new[] { "otdf", "psdf", "PSDF" })
        {
            string[] argv = ["sensitivity", "case14", "--kind", kind, "--branch", "1", "--json", "s.json"];
            Assert.True(UnboundedChanges.RunsDcModel("sensitivity", argv), kind);
            Assert.True(UnboundedChanges.Applies(B33, "sensitivity", argv, [], caseUsesMakeBdcTerms: true).Applies, kind);
        }
        Assert.True(UnboundedChanges.RunsDcModel("sensitivity", ["sensitivity", "case14", "--kind=otdf", "--branch", "1"]));
        Assert.False(UnboundedChanges.RunsDcModel("sensitivity", ["sensitivity", "case14", "--bus", "9"]));
        string[] n1 = ["nminus1", "case14", "--json", "n1.json"];
        Assert.False(UnboundedChanges.RunsDcModel("nminus1", n1));
        Assert.False(UnboundedChanges.Applies(B33, "nminus1", n1, [], caseUsesMakeBdcTerms: true).Applies);
        Assert.True(UnboundedChanges.RunsDcModel("dcopf", ["dcopf", "case14"]));
    }

    [Fact]
    public void Judge_marks_a_tapped_dc_job_unbounded_b33_and_leaves_a_tap_free_one_unexplained()
    {
        const string recorded = "2.1.143", build = "2.1.145";
        static ReplayOutcome Job(string[] argv, bool? terms)
        {
            FileComparison c = ResultCompare.Compare("d.json", Encoding.UTF8.GetBytes("""{"x_test":[1.0,2.0]}"""),
                Encoding.UTF8.GetBytes("""{"x_test":[-1.0,2.0]}"""), argv[0], s => s, s => s, VolatileFields.Default);
            var o = new ReplayOutcome { Seq = 1, RecordedUtc = "2026-11-24T00:00:00.0000000Z", Verb = argv[0], RecordedVersion = recorded, Status = ReplayStatus.Changed };
            o.Fields.AddRange(c.Fields);
            UnboundedChanges.Judge(Map, o, Map.Explain(recorded, build, argv[0], argv, []), argv[0], argv, [], terms);
            return o;
        }
        string[] dcpf = ["dcpf", "case14", "--json", "d.json"];
        Assert.Contains("B33", Map.Explain(recorded, build, "dcpf", dcpf, []));

        ReplayOutcome tapped = Job(dcpf, DcPowerFlow.UsesMakeBdcTerms(IeeeCases.Case14()));
        Assert.Equal(ClaimVerdict.Unbounded, tapped.Verdict);
        Assert.Equal(new[] { "B33" }, tapped.ExplainedBy);

        ReplayOutcome tapFree = Job(["dcpf", "case9", "--json", "d.json"], DcPowerFlow.UsesMakeBdcTerms(IeeeCases.Case9()));
        Assert.Equal(ClaimVerdict.Unexplained, tapFree.Verdict);
        Assert.Empty(tapFree.ExplainedBy);

        ReplayOutcome ac = Job(["sensitivity", "case14", "--kind", "dvdq", "--bus", "9", "--json", "d.json"], true);
        Assert.Equal(ClaimVerdict.Unexplained, ac.Verdict);
        Assert.Empty(ac.ExplainedBy);
    }

    // ---- the CLI end to end ----------------------------------------------------------------------------------------

    // Run one job with a journal, flip the sign of the first non-integral number in its result's tables (0 agreeing bits:
    // beyond every bound), rewrite the entry as recorded on 2.1.143 (B33 in its window) with the result's new hash, and
    // validate. Returns the validate run and the report's one entry.
    private (CliProcess.Result Run, string ClaimCheck, string ExplainedBy, string Status) Moved(string tag, string result, params string[] job)
    {
        string work = Path.Combine(_dir, tag);
        Directory.CreateDirectory(work);
        string journal = Path.Combine(_dir, tag + "-j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(work, new[] { "--journal", journal }.Concat(job).ToArray());
        Assert.True(run.ExitCode == 0, run.ToString());
        string path = Path.Combine(work, result);
        JsonNode doc = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(FlipFirst(doc["tables"]!), "no non-integral number in " + result);
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        e.Version = "2.1.143";
        byte[] bytes = File.ReadAllBytes(path);
        foreach (JournalOutput o in e.Outputs.Where(o => o.Abs == Path.GetFullPath(path)))
        {
            o.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            o.Bytes = bytes.LongLength;
        }
        File.WriteAllText(journal, JournalJson.Line(e) + "\n", new UTF8Encoding(false));

        string report = Path.Combine(_dir, tag + "-v.json");
        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement entry = d.RootElement.GetProperty("tables").GetProperty("entries")[0];
        return (v, entry.GetProperty("claim_check").GetString()!, entry.GetProperty("explained_by").GetString()!, entry.GetProperty("status").GetString()!);
    }

    private static bool FlipFirst(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> kv in o)
                    if (FlipFirst(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (JsonNode? item in a)
                    if (FlipFirst(item)) return true;
                return false;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double x) && x != Math.Floor(x):
                v.ReplaceWith(JsonValue.Create(-x));
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void The_cli_does_not_raise_b33_for_a_tap_free_dcpf_job()
    {
        var (run, claimCheck, explainedBy, status) = Moved("free", "d.json", "dcpf", "case9", "--json", "d.json");
        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal(ReplayStatus.Changed, status);
        Assert.Equal(ClaimVerdict.Unexplained, claimCheck);
        Assert.Equal("", explainedBy);
        Assert.DoesNotContain("UNBOUNDED", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_raises_b33_for_a_tapped_dcpf_job()
    {
        var (run, claimCheck, explainedBy, status) = Moved("tapped", "d.json", "dcpf", "case14", "--json", "d.json");
        Assert.True(run.ExitCode == 4, run.ToString());
        Assert.Equal(ReplayStatus.Changed, status);
        Assert.Equal(ClaimVerdict.Unbounded, claimCheck);
        Assert.Equal("B33", explainedBy);
        Assert.Contains("UNBOUNDED (B33), review needed", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_does_not_raise_b33_for_an_ac_sensitivity_job_on_a_tapped_case()
    {
        var (run, claimCheck, explainedBy, status) = Moved("ac", "s.json", "sensitivity", "case14", "--kind", "dvdq", "--bus", "9", "--json", "s.json");
        Assert.True(run.ExitCode == 3, run.ToString());
        Assert.Equal(ReplayStatus.Changed, status);
        Assert.Equal(ClaimVerdict.Unexplained, claimCheck);
        Assert.Equal("", explainedBy);
        Assert.DoesNotContain("UNBOUNDED", run.StdOut, StringComparison.Ordinal);
    }
}
