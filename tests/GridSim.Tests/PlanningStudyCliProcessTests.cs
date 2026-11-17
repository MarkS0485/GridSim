using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P7.3 through the real entry point: <c>gridsim planning-study</c> on the seasonal fixture. Ordered result
/// manifest, separate job folders, byte-identical repeat runs, reproducible retry after an interrupted batch, no stale
/// result standing in for a failed job, conflict refusal and the input-error paths.
/// </summary>
public sealed class PlanningStudyCliProcessTests
{
    private static readonly string[] Deterministic = ["run-manifest.json", "summary.txt"];

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gridsim-planning-" + Guid.NewGuid().ToString("N"));
        public Workspace()
        {
            string fixture = PlanningStudyExpansionTests.FixtureDir();
            Directory.CreateDirectory(Path.Combine(Root, "master"));
            foreach (string f in Directory.GetFiles(Path.Combine(fixture, "master")))
                File.Copy(f, Path.Combine(Root, "master", Path.GetFileName(f)));
            File.Copy(Path.Combine(fixture, "seasonal.json"), Manifest);
        }
        public string Manifest => Path.Combine(Root, "seasonal.json");
        public string Out(string name) => Path.Combine(Root, name);
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
        public JsonObject ManifestJson() => JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
        public string Write(string name, JsonNode manifest)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, manifest.ToJsonString());
            return path;
        }
    }

    private static CliProcess.Result Run(params string[] args) => CliProcess.RunBuilt(["planning-study", .. args]);
    private static JsonElement RunManifest(string dir) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "run-manifest.json"))).RootElement;
    private static Dictionary<string, byte[]> Outputs(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f) != "evidence.json")
        .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes);
    private static void SameOutputs(string a, string b)
    {
        Dictionary<string, byte[]> x = Outputs(a), y = Outputs(b);
        Assert.Equal(x.Keys.Order(), y.Keys.Order());
        foreach (string key in x.Keys) Assert.True(x[key].AsSpan().SequenceEqual(y[key]), "differs: " + key);
    }

    [Fact]
    public void Seasonal_fixture_runs_in_order_and_repeats_byte_for_byte()
    {
        using var w = new Workspace();
        string first = w.Out("run1"), second = w.Out("run2"), parallel = w.Out("run3");
        CliProcess.Result a = Run(w.Manifest, "--out", first);
        Assert.True(a.ExitCode == 3, a.ToString());   // two NOT SECURE jobs (the planned outage)
        CliProcess.Result b = Run(w.Manifest, "--out", second);
        CliProcess.Result c = Run(w.Manifest, "--out", parallel, "--parallel", "3");
        Assert.Equal(3, b.ExitCode);
        Assert.Equal(3, c.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
        Assert.Equal(a.StdOut, c.StdOut);
        SameOutputs(first, second);
        SameOutputs(first, parallel);
        Assert.Equal(File.ReadAllText(Path.Combine(first, "summary.txt")), a.StdOut);

        JsonElement m = RunManifest(first);
        Assert.Equal("gridsim-planning-result/1", m.GetProperty("schemaVersion").GetString());
        Assert.Equal("COMPLETE", m.GetProperty("state").GetString());
        Assert.Equal("8a6e86d2fd32b6d0899e28176e9e0778c2f9a555e69e761d6fefee00725bfa93", m.GetProperty("master").GetProperty("bundleSha256").GetString());
        Assert.Equal(["system.json", "buses.json", "generation.json", "demand.json", "transmission.json"],
            m.GetProperty("master").GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("path").GetString()));
        JsonElement[] jobs = m.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(["winter-peak/n-1", "outage-before/n-1", "outage-at/n-1", "outage-after/n-1", "summer/n-1"],
            jobs.Select(j => j.GetProperty("id").GetString()));
        Assert.Equal([1, 2, 3, 4, 5], jobs.Select(j => j.GetProperty("ordinal").GetInt32()));
        Assert.All(jobs, j => Assert.Equal("COMPLETED", j.GetProperty("state").GetString()));
        Assert.Equal(PlanningStudyExpectations.Statuses,
            jobs.Select(j => (j.GetProperty("status").GetString()!, j.GetProperty("outcome").GetString()!)));
        Assert.Equal(5, jobs.Select(j => j.GetProperty("result").GetString()).Distinct().Count());

        // Case identities through the real loader: expiry restores the master, the boundary instants agree.
        var snaps = m.GetProperty("snapshots").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("caseSha256").GetString());
        string masterCase = m.GetProperty("master").GetProperty("caseSha256").GetString()!;
        Assert.Equal(masterCase, snaps["outage-before"]);
        Assert.Equal(masterCase, snaps["summer"]);
        Assert.Equal(snaps["outage-at"], snaps["outage-after"]);
        Assert.NotEqual(masterCase, snaps["outage-at"]);
        Assert.NotEqual(masterCase, snaps["winter-peak"]);
        Assert.Equal("2027-06-01T00:00:00.0000000Z", m.GetProperty("snapshots")[2].GetProperty("instantUtc").GetString());

        // A job result carries the audit with original and effective values and the NOT APPLICABLE row.
        JsonElement at = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, jobs[2].GetProperty("result").GetString()!))).RootElement;
        JsonElement open = at.GetProperty("audit")[4];
        Assert.Equal(("Applied", "true", "false"), (open.GetProperty("disposition").GetString(), open.GetProperty("original").GetString(), open.GetProperty("effective").GetString()));
        Assert.Equal("NOT APPLICABLE", at.GetProperty("evidence").GetProperty("requestedOutages")[4].GetProperty("applicability").GetString());
        Assert.Equal(snaps["outage-at"], at.GetProperty("provenance").GetProperty("caseSha256").GetString());

        // Exit 0 only when every job is SECURE: the seasonal snapshots alone.
        JsonObject seasons = w.ManifestJson();
        seasons["snapshots"] = new JsonArray(seasons["snapshots"]![0]!.DeepClone(), seasons["snapshots"]![4]!.DeepClone());
        CliProcess.Result ok = Run(w.Write("seasons.json", seasons), "--out", w.Out("seasons"));
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Contains("run COMPLETE: 2 of 2 jobs completed, 2 passed, 0 not passed, 0 failed", ok.StdOut);

        // The master inputs are unchanged by the runs.
        Assert.Equal("8a6e86d2fd32b6d0899e28176e9e0778c2f9a555e69e761d6fefee00725bfa93", PlanningStudyExpansionTests.Master().BundleSha256);
    }

    [Fact]
    public void Interrupted_batch_retries_to_the_same_outputs_reusing_only_verified_results()
    {
        using var w = new Workspace();
        string reference = w.Out("reference"), retry = w.Out("retry");
        Assert.Equal(3, Run(w.Manifest, "--out", reference).ExitCode);

        CliProcess.Result partial = Run(w.Manifest, "--out", retry, "--max-jobs", "2");
        Assert.True(partial.ExitCode == 3, partial.ToString());
        JsonElement m = RunManifest(retry);
        Assert.Equal("CANCELLED", m.GetProperty("state").GetString());
        Assert.Equal(["COMPLETED", "COMPLETED", "NOT ATTEMPTED", "NOT ATTEMPTED", "NOT ATTEMPTED"],
            m.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, m.GetProperty("jobs")[2].GetProperty("result").ValueKind);
        Assert.False(Directory.Exists(Path.Combine(retry, "jobs", "003-outage-at--n-1")));

        // Tamper with job 2's result: its write check fails, so it is re-run rather than reused.
        string tampered = Path.Combine(retry, "jobs", "002-outage-before--n-1", "result.json");
        File.AppendAllText(tampered, " ");

        CliProcess.Result resumed = Run(w.Manifest, "--out", retry);
        Assert.True(resumed.ExitCode == 3, resumed.ToString());
        SameOutputs(reference, retry);
        JsonElement evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(retry, "evidence.json"))).RootElement;
        JsonElement[] timing = evidence.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal([true, false, false, false, false], timing.Select(t => t.GetProperty("reused").GetBoolean()));
        Assert.Equal("result file fails its write check", timing[1].GetProperty("reuseRefusal").GetString());

        // A third run reuses everything and still reproduces the reference outputs.
        Assert.Equal(3, Run(w.Manifest, "--out", retry).ExitCode);
        SameOutputs(reference, retry);
        JsonElement again = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(retry, "evidence.json"))).RootElement;
        Assert.All(again.GetProperty("jobs").EnumerateArray(), t => Assert.True(t.GetProperty("reused").GetBoolean()));
    }

    [Fact]
    public void A_failed_job_is_never_represented_by_an_earlier_result_of_the_same_name()
    {
        using var w = new Workspace();
        string dir = w.Out("run");
        Assert.Equal(3, Run(w.Manifest, "--out", dir).ExitCode);
        string result = Path.Combine(dir, "jobs", "005-summer--n-1", "result.json");
        Assert.Contains("\"status\": \"SECURE\"", File.ReadAllText(result));

        // Same job names, but a new project islands bus 4 in summer: that case is invalid and its job fails.
        JsonObject manifest = w.ManifestJson();
        manifest["projects"]!.AsArray().Add(JsonNode.Parse("""
            {"id":"isolate-west","operations":[
              {"id":"open-34","target":{"element":"branch","index":2,"from":3,"to":4},"field":"inService","expectedPrior":true,"value":false,"effectiveFrom":"2027-07-01T00:00:00Z"},
              {"id":"open-41","target":{"element":"branch","index":3,"from":4,"to":1},"field":"inService","expectedPrior":true,"value":false,"effectiveFrom":"2027-07-01T00:00:00Z"}]}
            """));
        CliProcess.Result failed = Run(w.Write("islanding.json", manifest), "--out", dir);
        Assert.True(failed.ExitCode == 3, failed.ToString());
        JsonElement job = RunManifest(dir).GetProperty("jobs")[4];
        Assert.Equal(("COMPLETED", "INVALID CASE"), (job.GetProperty("state").GetString(), job.GetProperty("status").GetString()));
        string text = File.ReadAllText(result);
        Assert.Contains("\"status\": \"INVALID CASE\"", text);
        Assert.DoesNotContain("\"status\": \"SECURE\"", text);
        Assert.False(File.Exists(Path.Combine(dir, "jobs", "005-summer--n-1", "complete.json")));
        Assert.Contains("005  summer/n-1  INVALID CASE (Invalid)", failed.StdOut);
        // The four unaffected jobs were reused.
        JsonElement evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "evidence.json"))).RootElement;
        Assert.Equal([true, true, true, true, false], evidence.GetProperty("jobs").EnumerateArray().Select(t => t.GetProperty("reused").GetBoolean()));
    }

    [Fact]
    public void Shadowed_change_is_refused_before_any_job_with_the_precise_diagnostic()
    {
        using var w = new Workspace();
        JsonObject manifest = w.ManifestJson();
        manifest["projects"]!.AsArray().Add(JsonNode.Parse("""
            {"id":"reconductor","operations":[{"id":"r1","target":{"element":"branch","index":0,"from":1,"to":2},"field":"rateAMva","expectedPrior":150,"value":200,"effectiveFrom":"2027-01-01T00:00:00Z"}]}
            """));
        manifest["projects"]!.AsArray().Add(JsonNode.Parse("""
            {"id":"feeder-uprate","operations":[{"id":"f1","target":{"element":"branch","index":0,"from":1,"to":2},"field":"rateAMva","expectedPrior":150,"value":180,"effectiveFrom":"2027-01-01T00:00:00Z"}]}
            """));
        manifest["projectOrder"] = new JsonArray("winter-2026-27", "diagonal-maintenance", "reconductor", "feeder-uprate");
        string dir = w.Out("conflict");
        CliProcess.Result refused = Run(w.Write("conflict.json", manifest), "--out", dir);
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("planning-study: Snapshot 'winter-peak' at 2027-01-15T18:00:00.0000000Z: operation 'feeder-uprate/f1' " +
            "expects rateAMva of branch 0 (1-2) to be 150 but operation 'reconductor/r1', applied earlier by projectOrder, " +
            "set it to 200. The change is shadowed; correct its expectedPrior or the project order.", refused.StdErr.Trim());
        Assert.False(Directory.Exists(dir));

        manifest["projectOrder"] = new JsonArray("winter-2026-27", "diagonal-maintenance", "reconductor");
        CliProcess.Result unordered = Run(w.Write("unordered.json", manifest), "--out", dir);
        Assert.Equal(1, unordered.ExitCode);
        Assert.Contains("operations 'reconductor/r1' and 'feeder-uprate/f1' both change rateAMva of branch 0 (1-2) while active", unordered.StdErr);
    }

    [Fact]
    public void Input_errors_and_usage_have_explicit_outcomes()
    {
        using var w = new Workspace();
        Assert.Equal(2, Run(w.Manifest).ExitCode);
        Assert.Equal(2, Run(w.Manifest, "--out", w.Out("x"), "--parallel", "0").ExitCode);
        Assert.Equal(2, Run(w.Manifest, "--out", w.Out("x"), "--bogus", "1").ExitCode);

        void Refuse(Action<JsonObject> edit, string expected)
        {
            JsonObject m = w.ManifestJson();
            edit(m);
            CliProcess.Result r = Run(w.Write("bad.json", m), "--out", w.Out("bad"));
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains(expected, r.StdErr);
            Assert.False(Directory.Exists(w.Out("bad")));
        }
        Refuse(m => m["snapshots"]![0]!["instant"] = "2027-01-15T18:00:00", "explicit UTC offset");
        Refuse(m => m["studies"]![0]!["mode"] = "n-2", "mode \"n-2\" is not a planning study mode (available: transmission-security, transfer, corrective-n-1-1)");
        Refuse(m => m["studies"]![0]!["mode"] = "transfer", "studies[0].parameters: required property \"sources\" is missing.");
        Refuse(m => m["master"]!["interpretation"]!["dispatch"] = true, "dispatch must be false");
        Refuse(m => m["master"]!["case"]!["sha256"] = new string('0', 64), "Master case SHA256 mismatch. Resolved identity: 8a6e86d2");
        Refuse(m => m["surprise"] = 1, "unknown property \"surprise\"");
        Refuse(m => m["manifestVersion"] = "gridsim-planning/2", "manifestVersion must be");
        Refuse(m => m["projects"]![0]!["operations"]![0]!["expectedPrior"] = 149, "expects pdMw of load (bus 2) to be 149 but the master holds 150.");

        // A duplicated property is refused even though JSON parsers would keep the last.
        string dup = w.Write("dup.json", w.ManifestJson()).Replace("dup.json", "dup2.json");
        File.WriteAllText(dup, File.ReadAllText(w.Manifest).Replace("\"projectOrder\"", "\"snapshots\": [], \"projectOrder\""));
        CliProcess.Result d = Run(dup, "--out", w.Out("dup"));
        Assert.Equal(1, d.ExitCode);
        Assert.Contains("duplicate property \"snapshots\"", d.StdErr);

        // An unrelated non-empty folder is never used as a run folder.
        string foreign = w.Out("foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");
        CliProcess.Result f = Run(w.Manifest, "--out", foreign);
        Assert.Equal(1, f.ExitCode);
        Assert.Contains("not a planning run folder", f.StdErr);
        Assert.Equal(["keep.txt"], Directory.GetFiles(foreign).Select(Path.GetFileName));
    }

    /// <summary>
    /// P4 and P5 through the selector: transfer and corrective N-1-1 run on the snapshot just before and at the planned
    /// outage start. The outage (and the sequence) that needs the opened diagonal keeps an explicit NOT APPLICABLE row,
    /// the remaining rows run through the unaltered engines, and a repeat run is byte-identical.
    /// </summary>
    [Fact]
    public void Transfer_and_corrective_modes_run_through_the_selector_with_planned_outage_rows()
    {
        using var w = new Workspace();
        JsonObject m = w.ManifestJson();
        m["snapshots"] = new JsonArray(m["snapshots"]![1]!.DeepClone(), m["snapshots"]![2]!.DeepClone());
        JsonArray studies = m["studies"]!.AsArray();
        studies.Add(JsonNode.Parse("""
            {"id":"south-north","mode":"transfer","parameters":{
              "sources":[{"index":1,"bus":3,"weight":1}],"sinks":[{"index":0,"bus":1,"weight":1}],
              "outages":[{"index":0,"from":1,"to":2},{"index":2,"from":3,"to":4},{"index":4,"from":1,"to":3}],
              "voltage":{"minPu":0.9,"maxPu":1.1},"postFaultRating":"B",
              "search":{"maxMw":150,"stepMw":25,"toleranceMw":5,"maxTrials":60}}}
            """));
        studies.Add(JsonNode.Parse("""
            {"id":"n-1-1","mode":"corrective-n-1-1","parameters":{
              "voltage":{"minPu":0.9,"maxPu":1.1},"ratings":{"firstStage":"B","intermediate":"B","final":"B"},
              "policy":{"eligible":[],"availableMinutes":10},
              "sequences":[{"first":{"index":4,"from":1,"to":3},"second":{"index":0,"from":1,"to":2},"proposal":"None"},
                           {"first":{"index":0,"from":1,"to":2},"second":{"index":2,"from":3,"to":4},"proposal":"None"}]}}
            """));
        string manifest = w.Write("modes.json", m);
        CliProcess.Result a = Run(manifest, "--out", w.Out("a"));
        CliProcess.Result b = Run(manifest, "--out", w.Out("b"), "--parallel", "2");
        Assert.True(a.ExitCode is 0 or 3, a.ToString());
        Assert.Equal(a.ExitCode, b.ExitCode);
        SameOutputs(w.Out("a"), w.Out("b"));

        JsonElement[] jobs = RunManifest(w.Out("a")).GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(["outage-before/n-1", "outage-before/south-north", "outage-before/n-1-1",
                "outage-at/n-1", "outage-at/south-north", "outage-at/n-1-1"], jobs.Select(j => j.GetProperty("id").GetString()));
        Assert.Equal(["transmission-security", "transfer", "corrective-n-1-1"], jobs.Take(3).Select(j => j.GetProperty("mode").GetString()));
        Assert.All(jobs, j => Assert.Equal("COMPLETED", j.GetProperty("state").GetString()));
        Assert.Equal(PlanningStudyExpectations.Modes, jobs.Select(j => (j.GetProperty("status").GetString()!, j.GetProperty("outcome").GetString()!)));

        JsonElement Result(int i) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(w.Out("a"), jobs[i].GetProperty("result").GetString()!))).RootElement;
        JsonElement transferAt = Result(4).GetProperty("evidence");
        Assert.Equal(["REQUESTED", "REQUESTED", "NOT APPLICABLE"],
            transferAt.GetProperty("requestedOutages").EnumerateArray().Select(r => r.GetProperty("applicability").GetString()));
        Assert.Equal([0, 2], transferAt.GetProperty("effectiveRequest").GetProperty("outages").EnumerateArray().Select(o => o.GetProperty("index").GetInt32()));
        Assert.Equal(3, Result(1).GetProperty("evidence").GetProperty("effectiveRequest").GetProperty("outages").GetArrayLength());
        JsonElement limited = transferAt.GetProperty("result");
        Assert.Equal((78.125, 81.25), (limited.GetProperty("lowerMw").GetDouble(), limited.GetProperty("upperMw").GetDouble()));
        JsonElement first = limited.GetProperty("firstLimit");
        Assert.Equal((2, "BranchTo", 1, "B"), (first.GetProperty("outageIndex").GetInt32(), first.GetProperty("kind").GetString(),
            first.GetProperty("index").GetInt32(), first.GetProperty("source").GetString()));
        JsonElement correctiveAt = Result(5).GetProperty("evidence");
        Assert.Equal(["NOT APPLICABLE", "REQUESTED"],
            correctiveAt.GetProperty("requestedSequences").EnumerateArray().Select(r => r.GetProperty("applicability").GetString()));
        Assert.Equal("Branch 4 is out of service in this snapshot.", correctiveAt.GetProperty("requestedSequences")[0].GetProperty("reason").GetString());
        Assert.Equal(1, correctiveAt.GetProperty("effectiveRequest").GetProperty("sequences").GetArrayLength());
        Assert.Equal(2, Result(2).GetProperty("evidence").GetProperty("effectiveRequest").GetProperty("sequences").GetArrayLength());
    }

    [Fact]
    public void Master_digest_is_the_transmission_security_case_identity()
    {
        // The same case.sha256 binds the native master in both verbs.
        using var w = new Workspace();
        string study = Path.Combine(w.Root, "study.json");
        File.WriteAllText(study, """
            {"schemaVersion":1,"case":{"path":"master","sha256":"8a6e86d2fd32b6d0899e28176e9e0778c2f9a555e69e761d6fefee00725bfa93"},
             "outages":[{"index":0,"from":1,"to":2}],"voltage":{"minPu":0.9,"maxPu":1.1},"postFaultRating":"B"}
            """);
        CliProcess.Result r = CliProcess.RunBuilt("transmission-security", study);
        Assert.DoesNotContain("SHA256 mismatch", r.StdErr);
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
    }

    [Fact]
    public void Help_is_registered()
    {
        CliProcess.Result help = CliProcess.RunBuilt("planning-study", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("gridsim planning-study <manifest.json> --out <dir>", help.StdOut);
        Assert.Contains("planning-study", CliProcess.RunBuilt("--help").StdOut);
    }
}
