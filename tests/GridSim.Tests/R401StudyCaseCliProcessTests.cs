using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-01 through the real entry points: <c>study-case</c>, <c>case-diff</c> and <c>result-diff</c>. W1 equivalence
/// against the closed-form two-bus voltages, W2 grouping, W3 diffs, W4 conflict, byte-identical repeat runs, serial
/// versus parallel, verified reuse on retry, and every refusal path (exit 1 with nothing written, exit 2 for usage).
/// </summary>
public sealed class R401StudyCaseCliProcessTests : IDisposable
{
    private readonly string _root = R401Cases.NewRoot();
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static CliProcess.Result Run(params string[] args) => CliProcess.RunBuilt(args);
    private string P(string name) => Path.Combine(_root, name);

    private string WriteText(string name, string text)
    {
        File.WriteAllText(P(name), text, new System.Text.UTF8Encoding(false));
        return P(name);
    }

    private static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;

    private static Dictionary<string, byte[]> Outputs(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f) != "evidence.json")
        .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes);

    private static void SameOutputs(string a, string b)
    {
        Dictionary<string, byte[]> x = Outputs(a), y = Outputs(b);
        Assert.Equal(x.Keys.Order(), y.Keys.Order());
        foreach (string key in x.Keys) Assert.True(x[key].AsSpan().SequenceEqual(y[key]), "differs: " + key);
    }

    private string TwoBusStudy(string? extraVariations = null, string? scenarios = null, string studyScenario = "peak", string? instants = null)
    {
        string dir = R401Cases.TwoBus(_root);
        return WriteText("w1.json", R401Cases.TwoBusStudyCase(R401Cases.Bundle(dir), extraVariations ?? "", scenarios ?? "", studyScenario, "", instants));
    }

    private static double BaseVoltage(string resultPath)
    {
        JsonElement limits = Json(resultPath).GetProperty("evidence").GetProperty("result").GetProperty("baseCase").GetProperty("limits");
        foreach (JsonElement l in limits.EnumerateArray())
            if (l.GetProperty("kind").GetString() == "Voltage" && l.GetProperty("index").GetInt32() == 2)
                return l.GetProperty("observed").GetDouble();
        throw new InvalidOperationException("no bus 2 voltage row");
    }

    // ---- W1 equivalence --------------------------------------------------------------------------------------------

    [Fact]
    public void W1_two_bus_variation_and_scenario_match_the_closed_form_and_repeat_byte_for_byte()
    {
        string study = TwoBusStudy();
        string run1 = P("run1"), run2 = P("run2"), run3 = P("run3");
        CliProcess.Result a = Run("study-case", study, "--out", run1);
        Assert.True(a.ExitCode is 0 or 3, a.ToString());
        CliProcess.Result b = Run("study-case", study, "--out", run2);
        CliProcess.Result c = Run("study-case", study, "--out", run3, "--parallel", "2");
        Assert.Equal(a.ExitCode, b.ExitCode);
        Assert.Equal(a.ExitCode, c.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
        Assert.Equal(a.StdOut, c.StdOut);
        SameOutputs(run1, run2);
        SameOutputs(run1, run3);
        Assert.Equal(File.ReadAllText(Path.Combine(run1, "summary.txt")), a.StdOut);

        JsonElement m = Json(Path.Combine(run1, "run-manifest.json"));
        Assert.Equal("gridsim-studycase-result/1", m.GetProperty("schemaVersion").GetString());
        Assert.Equal("COMPLETE", m.GetProperty("state").GetString());
        JsonElement[] jobs = m.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(["peak.before/n1", "peak.after/n1"], jobs.Select(j => j.GetProperty("id").GetString()));
        Assert.Equal([1, 2], jobs.Select(j => j.GetProperty("ordinal").GetInt32()));
        Assert.All(jobs, j => Assert.Equal("COMPLETED", j.GetProperty("state").GetString()));

        // 2026-06-01: both circuits (X = 0.1 pu equivalent); 2027-02-01: circuit one out (X = 0.2 pu). Closed form from the
        // RC3 W1 oracle: 0.965488891873777 and 0.9194904559886774. Tolerance 10 x the Newton mismatch (1e-8 pu).
        double intact = BaseVoltage(Path.Combine(run1, jobs[0].GetProperty("result").GetString()!));
        double outage = BaseVoltage(Path.Combine(run1, jobs[1].GetProperty("result").GetString()!));
        Assert.InRange(Math.Abs(intact - R401Cases.ReceivingVoltage(0.1, 80, 30)), 0, 1e-7);
        Assert.InRange(Math.Abs(intact - 0.965488891873777), 0, 1e-7);
        Assert.InRange(Math.Abs(outage - R401Cases.ReceivingVoltage(0.2, 80, 30)), 0, 1e-7);
        Assert.InRange(Math.Abs(outage - 0.9194904559886774), 0, 1e-7);

        // The pre-effective operation is recorded NotYetEffective in the job and in the derivation record.
        JsonElement audit = Json(Path.Combine(run1, jobs[0].GetProperty("result").GetString()!)).GetProperty("audit");
        JsonElement open = audit.EnumerateArray().Single(x => x.GetProperty("operation").GetString() == "open-one");
        Assert.Equal(("NotYetEffective", "true", "true"), (open.GetProperty("disposition").GetString(), open.GetProperty("original").GetString(), open.GetProperty("effective").GetString()));
        JsonElement derivation = Json(Path.Combine(run1, "derivation.json"));
        JsonElement[] cases = derivation.GetProperty("cases").EnumerateArray().ToArray();
        JsonElement beforeOp = cases[0].GetProperty("operations").EnumerateArray().Single(x => x.GetProperty("operation").GetString() == "open-one");
        JsonElement afterOp = cases[1].GetProperty("operations").EnumerateArray().Single(x => x.GetProperty("operation").GetString() == "open-one");
        Assert.Equal(("NotYetEffective", "variation", "2027 outage"), (beforeOp.GetProperty("disposition").GetString(), beforeOp.GetProperty("origin").GetString(), beforeOp.GetProperty("expansionStage").GetString()));
        Assert.Equal("Applied", afterOp.GetProperty("disposition").GetString());
        JsonElement[] changes = cases[1].GetProperty("scenarioChanges").EnumerateArray().ToArray();
        // No group named: every bus, in bus order, pdMw then qdMvar (bus 1 carries no demand, bus 2 80 MW and 30 MVAr).
        Assert.Equal(["pdMw", "qdMvar", "pdMw", "qdMvar"], changes.Select(x => x.GetProperty("field").GetString()));
        Assert.Equal(("0", "0", "80", "80", "30", "30"), (changes[0].GetProperty("before").GetString(), changes[0].GetProperty("after").GetString(),
            changes[2].GetProperty("before").GetString(), changes[2].GetProperty("after").GetString(),
            changes[3].GetProperty("before").GetString(), changes[3].GetProperty("after").GetString()));

        // The two cases differ in content, and neither is the master's by accident.
        JsonElement[] resolved = m.GetProperty("cases").EnumerateArray().ToArray();
        Assert.NotEqual(resolved[0].GetProperty("caseSha256").GetString(), resolved[1].GetProperty("caseSha256").GetString());
    }

    [Fact]
    public void Interrupted_run_retries_to_the_same_outputs_reusing_only_verified_results()
    {
        string study = TwoBusStudy();
        string reference = P("ref"), retry = P("retry");
        Run("study-case", study, "--out", reference);
        CliProcess.Result partial = Run("study-case", study, "--out", retry, "--max-jobs", "1");
        Assert.Equal(3, partial.ExitCode);
        Assert.Equal("CANCELLED", Json(Path.Combine(retry, "run-manifest.json")).GetProperty("state").GetString());
        Run("study-case", study, "--out", retry);
        SameOutputs(reference, retry);
        JsonElement evidence = Json(Path.Combine(retry, "evidence.json"));
        Assert.Equal([true, false], evidence.GetProperty("jobs").EnumerateArray().Select(t => t.GetProperty("reused").GetBoolean()));
        Run("study-case", study, "--out", retry);
        Assert.All(Json(Path.Combine(retry, "evidence.json")).GetProperty("jobs").EnumerateArray(), t => Assert.True(t.GetProperty("reused").GetBoolean()));
        SameOutputs(reference, retry);
    }

    // ---- W2 grouping end to end ------------------------------------------------------------------------------------

    private string ThreeBusStudy(double scaleA, double scaleB, bool solve = true, string? groupsText = null, string? tweak = null)
    {
        string dir = R401Cases.ThreeBus(_root);
        string bundle = R401Cases.Bundle(dir);
        string groups = WriteText("groups.json", groupsText ?? "{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"" + bundle + "\" }, \"groups\": [ " +
            "{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }, { \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [3] } ] }");
        string text = "{ \"manifestVersion\": \"gridsim-studycase/1\", " +
            "\"master\": { \"case\": { \"path\": \"three-bus\", \"sha256\": \"" + bundle + "\" }, \"interpretation\": { \"loader\": \"native-json\", \"dispatch\": false } }, " +
            "\"groups\": { \"path\": \"groups.json\", \"sha256\": \"" + R401Cases.Sha(groups) + "\" }, " +
            "\"instants\": [ { \"id\": \"t\", \"instant\": \"2027-01-01T00:00:00Z\" } ], " +
            "\"scenarios\": [ { \"id\": \"grow\", \"operations\": [ " +
            "{ \"id\": \"a\", \"group\": \"areaA\", \"field\": \"load\", \"action\": \"scale\", \"value\": " + scaleA.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " }, " +
            "{ \"id\": \"b\", \"group\": \"areaB\", \"field\": \"load\", \"action\": \"scale\", \"value\": " + scaleB.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " } ] } ], " +
            "\"studies\": [ { \"id\": \"sum\", \"scenario\": \"grow\", \"mode\": \"group-summary\", \"parameters\": { \"solve\": " + (solve ? "true" : "false") + " } } ] }";
        return WriteText("w2.json", tweak is null ? text : text.Replace(tweak.Split('|')[0], tweak.Split('|')[1]));
    }

    [Theory]
    [InlineData(1.25, 1.0, 20.0)]
    [InlineData(1.0, 1.25, 35.0)]
    public void W2_area_figures_through_the_verb_equal_the_hand_sums(double scaleA, double scaleB, double exportMw)
    {
        string study = ThreeBusStudy(scaleA, scaleB);
        string outDir = P("w2");
        CliProcess.Result r = Run("study-case", study, "--out", outDir);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("run COMPLETE: 1 of 1 jobs completed, 1 passed, 0 not passed, 0 failed", r.StdOut);
        JsonElement m = Json(Path.Combine(outDir, "run-manifest.json"));
        Assert.Equal(2, m.GetProperty("groups").GetProperty("groups").GetArrayLength());
        string result = m.GetProperty("jobs")[0].GetProperty("result").GetString()!;
        JsonElement[] groups = Json(Path.Combine(outDir, result)).GetProperty("evidence").GetProperty("groups").EnumerateArray().ToArray();
        JsonElement a = groups.Single(g => g.GetProperty("group").GetString() == "areaA");
        Assert.Equal(100 * scaleA, a.GetProperty("loadMw").GetDouble());
        Assert.InRange(Math.Abs(a.GetProperty("tieFlowMw").GetDouble() - exportMw), 0, 1e-4);
        Assert.InRange(Math.Abs(a.GetProperty("solvedNetInjectionMw").GetDouble() - exportMw), 0, 1e-4);
    }

    // ---- Refusals: exit 1 with nothing written ---------------------------------------------------------------------

    private void Refused(string study, string outName, string expected)
    {
        CliProcess.Result r = Run("study-case", study, "--out", P(outName));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(expected, r.StdErr);
        Assert.StartsWith("study-case: ", r.StdErr);
        Assert.False(Directory.Exists(P(outName)), "a refused run must write nothing");
    }

    [Fact]
    public void Duplicate_membership_unresolved_identity_and_digest_mismatches_refuse_before_any_solve()
    {
        string dir = R401Cases.ThreeBus(_root);
        string bundle = R401Cases.Bundle(dir);
        string G(string groups) => "{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"" + bundle + "\" }, \"groups\": [ " + groups + " ] }";

        Refused(ThreeBusStudy(1.25, 1, groupsText: G("{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }, { \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [2, 3] }")),
            "dup", "Bus 2 is in both area groups 'areaA' and 'areaB'");
        Refused(ThreeBusStudy(1.25, 1, groupsText: G("{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }, { \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [9] }")),
            "unres", "bus 9 is not in the case");
        Refused(ThreeBusStudy(1.25, 1, groupsText: G("{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }, { \"id\": \"areaB\", \"kind\": \"area\", \"branches\": [ { \"index\": 0, \"from\": 1, \"to\": 3 } ] }")),
            "ident", "branch 0 joins 1-2 in the case, not 1-3");

        // Groups bound to another case.
        string other = "{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"" + new string('0', 64) + "\" }, \"groups\": [ { \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1] }, { \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [3] } ] }";
        Refused(ThreeBusStudy(1.25, 1, groupsText: other), "bound", "is bound to case " + new string('0', 64));

        // A groups file that no longer matches its recorded digest.
        string study = ThreeBusStudy(1.25, 1);
        File.AppendAllText(P("groups.json"), " ");
        Refused(study, "tamper", "Groups document SHA256 mismatch");

        // The master digest.
        string two = TwoBusStudy();
        string wrong = WriteText("wrong.json", File.ReadAllText(two).Replace(R401Cases.Bundle(P("two-bus")), new string('1', 64)));
        Refused(wrong, "mastermismatch", "Master case SHA256 mismatch");
    }

    [Fact]
    public void W4_conflict_through_the_verb_names_both_operations()
    {
        string second = ", { \"id\": \"outage-two\", \"operations\": [ { \"id\": \"open-also\", \"target\": { \"element\": \"branch\", \"index\": 0, \"from\": 1, \"to\": 2 }, " +
            "\"field\": \"inService\", \"expectedPrior\": true, \"value\": false, \"effectiveFrom\": \"2027-01-01T00:00:00Z\" } ] }";
        string study = TwoBusStudy(extraVariations: second);
        CliProcess.Result r = Run("study-case", study, "--out", P("conflict"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("operations 'outage-one/open-one' and 'outage-two/open-also' both change inService of branch 0 (1-2) while active", r.StdErr);
        Assert.False(Directory.Exists(P("conflict")));
    }

    [Theory]
    [InlineData("\"manifestVersion\": \"gridsim-studycase/1\"", "\"manifestVersion\": \"gridsim-studycase/2\"", "manifestVersion must be")]
    [InlineData("\"scenarios\": [", "\"extra\": 1, \"scenarios\": [", "unknown property \"extra\"")]
    [InlineData("\"action\": \"scale\"", "\"action\": \"multiply\"", "action must be scale or set")]
    [InlineData("\"field\": \"load\"", "\"field\": \"pf\"", "is not supported")]
    [InlineData("\"scenario\": \"peak\"", "\"scenario\": \"ghost\"", "names scenario 'ghost'")]
    [InlineData("\"effectiveFrom\": \"2027-01-01T00:00:00Z\"", "\"effectiveFrom\": \"2027-01-01T00:00:00\"", "explicit UTC offset")]
    [InlineData("\"value\": 1.0", "\"value\": -1.0", "scale factor cannot be negative")]
    [InlineData("\"mode\": \"transmission-security\"", "\"mode\": \"nonesuch\"", "is not a planning study mode")]
    [InlineData("\"scenarios\": [ { \"id\": \"peak\"", "\"scenarios\": [ { \"id\": \"base\"", "reserved")]
    public void Invalid_study_case_documents_are_refused_with_the_path_named(string find, string replace, string expected)
    {
        string study = TwoBusStudy();
        string text = File.ReadAllText(study);
        Assert.Contains(find, text);
        string bad = WriteText("bad.json", text.Replace(find, replace));
        Refused(bad, "bad-out", expected);
    }

    [Fact]
    public void Duplicate_property_and_malformed_json_are_refused()
    {
        string study = TwoBusStudy();
        Refused(WriteText("dup.json", File.ReadAllText(study).Replace("\"instants\": [", "\"instants\": [], \"instants\": [")), "dup-out", "duplicate property \"instants\"");
        Refused(WriteText("junk.json", "{ nope"), "junk-out", "The study case is not valid JSON");
    }

    [Fact]
    public void Usage_errors_exit_2_and_output_folder_misuse_exits_1()
    {
        string study = TwoBusStudy();
        Assert.Equal(2, Run("study-case").ExitCode);
        Assert.Equal(2, Run("study-case", study).ExitCode);
        Assert.Equal(2, Run("study-case", study, "--out").ExitCode);
        Assert.Equal(2, Run("study-case", study, "--out", P("x"), "--parallel", "0").ExitCode);
        Assert.Equal(2, Run("study-case", study, "--out", P("x"), "--bogus", "1").ExitCode);
        Directory.CreateDirectory(P("occupied"));
        File.WriteAllText(P("occupied/other.txt"), "x");
        CliProcess.Result r = Run("study-case", study, "--out", P("occupied"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("not a study-case run folder", r.StdErr);
        Assert.Equal(1, Run("study-case", P("missing.json"), "--out", P("y")).ExitCode);
    }

    [Fact]
    public void An_invalid_resolved_case_is_never_handed_to_an_engine()
    {
        // Scenario opens the only two circuits: the case islands, so the job is INVALID CASE (RC3 behaviour, exit 3).
        string scenarios = "{ \"id\": \"cut\", \"operations\": [ { \"id\": \"open-all\", \"field\": \"inService\", \"action\": \"set\", \"value\": false } ] }";
        string study = TwoBusStudy(scenarios: scenarios, studyScenario: "cut", instants: "{ \"id\": \"t\", \"instant\": \"2026-06-01T00:00:00Z\" }");
        CliProcess.Result r = Run("study-case", study, "--out", P("invalid"));
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("cut.t/n1  INVALID CASE (Invalid)", r.StdOut);
        JsonElement job = Json(P("invalid/run-manifest.json")).GetProperty("jobs")[0];
        Assert.Equal("INVALID CASE", job.GetProperty("status").GetString());
    }

    // ---- case-diff and result-diff ---------------------------------------------------------------------------------

    [Fact]
    public void Case_diff_cli_reports_one_changed_field_and_exit_codes()
    {
        string a = R401Cases.TwoBus(_root, "a");
        string b = R401Cases.TwoBus(_root, "b");
        CliProcess.Result same = Run("case-diff", a, b);
        Assert.Equal(0, same.ExitCode);
        Assert.Contains("result: no differences; the files are byte-identical", same.StdOut);

        string lines = Path.Combine(b, "transmission.json");
        File.WriteAllText(lines, File.ReadAllText(lines).Replace("\"xPu\": 0.2, \"bPu\": 0, \"ratingMva\": 100 }\n]", "\"xPu\": 0.25, \"bPu\": 0, \"ratingMva\": 100 }\n]"));
        string json = P("diff.json");
        CliProcess.Result changed = Run("case-diff", a, b, "--out", json);
        Assert.Equal(3, changed.ExitCode);
        Assert.Contains("CHANGED    transmission.json[name=two].xPu  0.2 -> 0.25 pu", changed.StdOut);
        Assert.Contains("result: 1 difference\n", changed.StdOut);
        JsonElement doc = Json(json);
        Assert.Equal("gridsim-case-diff/1", doc.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, doc.GetProperty("entries").GetArrayLength());
        Assert.Equal("0.25", doc.GetProperty("entries")[0].GetProperty("new").GetString());

        // --out is a new file only.
        CliProcess.Result again = Run("case-diff", a, b, "--out", json);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("existing path", again.StdErr);

        Assert.Equal(2, Run("case-diff", a).ExitCode);
        Assert.Equal(1, Run("case-diff", a, P("nope")).ExitCode);
    }

    [Fact]
    public void Case_diff_cli_reports_a_permuted_record_order_as_reordered()
    {
        string a = R401Cases.TwoBus(_root, "a");
        string b = R401Cases.TwoBus(_root, "b");
        File.WriteAllText(Path.Combine(b, "transmission.json"),
            "[\n  { \"name\": \"two\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0.2, \"bPu\": 0, \"ratingMva\": 100 },\n  { \"name\": \"one\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0.2, \"bPu\": 0, \"ratingMva\": 100 }\n]\n");
        CliProcess.Result r = Run("case-diff", a, b);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("REORDERED  transmission.json  name=one,name=two -> name=two,name=one", r.StdOut);
        Assert.DoesNotContain("CHANGED", r.StdOut);
    }

    [Fact]
    public void Result_diff_cli_is_bit_equal_for_repeat_runs_and_reports_a_perturbation_with_its_ulp_distance()
    {
        string study = TwoBusStudy();
        Run("study-case", study, "--out", P("r1"));
        Run("study-case", study, "--out", P("r2"));
        string job = "jobs/002-peak.after--n1/result.json";
        CliProcess.Result same = Run("result-diff", P("r1/" + job), P("r2/" + job));
        Assert.Equal(0, same.ExitCode);
        Assert.Contains("result: equal; the documents are byte-identical", same.StdOut);

        double v = BaseVoltage(P("r1/" + job));
        double bumped = Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(v)));
        string text = File.ReadAllText(P("r1/" + job));
        string vText = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string perturbed = P("perturbed.json");
        File.WriteAllText(perturbed, ReplaceFirstVoltage(text, vText, bumped.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        string outJson = P("rd.json");
        CliProcess.Result diff = Run("result-diff", P("r1/" + job), perturbed, "--out", outJson);
        Assert.Equal(3, diff.ExitCode);
        Assert.Contains("FIRST      CHANGED ", diff.StdOut);
        Assert.Contains("(3 ulp, abs ", diff.StdOut);
        Assert.Equal(3UL, Json(outJson).GetProperty("first").GetProperty("ulpDistance").GetUInt64());
        Assert.Equal(1, Json(outJson).GetProperty("differences").GetInt32());

        Assert.Equal(1, Run("result-diff", P("r1/" + job), perturbed, "--out", outJson).ExitCode);
        Assert.Equal(2, Run("result-diff", perturbed).ExitCode);
        Assert.Equal(2, Run("result-diff", perturbed, perturbed, "--max-differences", "x").ExitCode);
        File.WriteAllText(P("junk.json"), "{");
        Assert.Equal(1, Run("result-diff", perturbed, P("junk.json")).ExitCode);
    }

    private static string ReplaceFirstVoltage(string text, string from, string to)
    {
        int i = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0, "voltage text not found");
        return text[..i] + to + text[(i + from.Length)..];
    }

    [Fact]
    public void Verb_help_and_the_verb_list_name_the_new_verbs()
    {
        foreach (string verb in new[] { "study-case", "case-diff", "result-diff" })
        {
            CliProcess.Result h = Run(verb, "--help");
            Assert.Equal(0, h.ExitCode);
            Assert.Contains("gridsim " + verb, h.StdOut);
        }
        Assert.Contains("study-case, case-diff, result-diff", Run("--help").StdOut);
        Assert.Equal(2, Run("case-diff", "a", "b", "--nonsense", "1").ExitCode);
    }
}
