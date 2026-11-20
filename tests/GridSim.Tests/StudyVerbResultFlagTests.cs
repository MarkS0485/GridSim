using GridSim.Cli;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 (last candidate): the ten study verbs that used to accept only their own --out take --json / --csv through the
/// shared result writer, like every other writer verb, so <c>gridsim.run</c> and <c>gridsim rpc</c> can call them. Their own
/// --out is unchanged. New coverage; no existing expectation moved.
/// </summary>
public sealed class StudyVerbResultFlagTests
{
    private static readonly string[] Verbs =
    {
        "probabilistic", "reliability-network", "flicker", "filter-design", "harmonic-assess", "connection-assess",
        "study-case", "case-diff", "result-diff", "report",
    };

    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-rc4-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static JsonDocument Doc(string path) => JsonDocument.Parse(File.ReadAllText(path));

    [Fact]
    public void All_ten_verbs_are_writer_verbs_and_their_own_out_stays_theirs()
    {
        foreach (string v in Verbs)
        {
            Assert.Contains(v, CliOutput.WriterVerbs);
            Assert.Contains(v, CliOutput.VerbsWithoutOut);
        }
    }

    [Theory]
    [InlineData("probabilistic")]
    [InlineData("reliability-network")]
    [InlineData("flicker")]
    [InlineData("filter-design")]
    [InlineData("harmonic-assess")]
    [InlineData("connection-assess")]
    [InlineData("study-case")]
    [InlineData("case-diff")]
    [InlineData("result-diff")]
    [InlineData("report")]
    public void The_json_and_csv_flags_are_taken_by_the_shared_writer_not_refused_as_unknown(string verb)
    {
        string d = Dir();
        string missing = Path.Combine(d, "missing.json");
        foreach ((string flag, string file) in new[] { ("--json", "r.json"), ("--csv", "r.csv") })
        {
            CliProcess.Result r = CliProcess.RunBuilt(verb, missing, flag, Path.Combine(d, file));
            Assert.DoesNotContain("applies to", r.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(d, file)), "no result is written when the run stops before its result: " + r);
        }
        // A wrong extension is the shared writer's own usage error (exit 2).
        Assert.Equal(2, CliProcess.RunBuilt(verb, missing, "--json", Path.Combine(d, "r.txt")).ExitCode);
    }

    [Fact]
    public void Probabilistic_writes_the_shared_json_and_csv_beside_its_own_out()
    {
        string d = Dir();
        string study = Write(d, "s.json", """
            { "schemaVersion": 1, "study": "adequacy", "seed": 7,
              "units": [ {"id":"g1","capacityMw":100,"availability":0.9}, {"id":"g2","capacityMw":100,"availability":0.9}, {"id":"g3","capacityMw":100,"availability":0.9} ],
              "load": { "constantMw": 150, "hours": 1 },
              "methods": [ {"method":"convolution","samples":1} ] }
            """);
        string own = Path.Combine(d, "own.json"), shared = Path.Combine(d, "shared.json"), csv = Path.Combine(d, "shared.csv");
        CliProcess.Result r = CliProcess.RunBuilt("probabilistic", study, "--out", own, "--json", shared, "--csv", csv);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument ownDoc = Doc(own);
        Assert.Equal("COMPLETE", ownDoc.RootElement.GetProperty("status").GetString());   // --out is still the verb's own document
        using JsonDocument sharedDoc = Doc(shared);
        Assert.Equal("gridsim-cli-result/1", sharedDoc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("probabilistic", sharedDoc.RootElement.GetProperty("verb").GetString());
        Assert.Equal(0, sharedDoc.RootElement.GetProperty("exitCode").GetInt32());
        JsonElement summary = sharedDoc.RootElement.GetProperty("summary");
        Assert.Equal("COMPLETE", summary.GetProperty("status").GetString());
        Assert.Equal(7, summary.GetProperty("provenance_seed").GetInt64());
        Assert.True(File.ReadAllText(csv).Length > 0);
        Assert.True(File.Exists(Path.Combine(d, "shared-summary.csv")));
        // stdout is the verb's own document, the same with or without the flags.
        CliProcess.Result plain = CliProcess.RunBuilt("probabilistic", study);
        Assert.Equal(plain.StdOut, r.StdOut);
    }

    [Fact]
    public void Harmonic_assess_and_flicker_write_the_shared_json()
    {
        string d = Dir();
        string study = Write(d, "h.json", """
            { "schemaVersion": 1, "study": "harmonic-assess",
              "pcc": { "lineKv": 11, "sscMva": 60, "xOverR": 10 },
              "equipment": [ { "id": "drive", "technology": "active-front-end", "ratingKva": 600 } ] }
            """);
        string json = Path.Combine(d, "h-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("harmonic-assess", study, "--json", json);
        Assert.True(r.ExitCode == 0, r.ToString());
        using (JsonDocument doc = Doc(json))
        {
            Assert.Equal("harmonic-assess", doc.RootElement.GetProperty("verb").GetString());
            Assert.Equal("ACCEPTED", doc.RootElement.GetProperty("summary").GetProperty("result_verdict").GetString());
        }
        string flicker = Write(d, "f.json", """
            { "schemaVersion": 1, "study": "flicker", "nominalKv": 11,
              "sources": [ { "id": "A", "pst": 0.5, "plt": 0.4 } ], "flickermeter": { } }
            """);
        string fjson = Path.Combine(d, "f-out.json");
        CliProcess.Result f = CliProcess.RunBuilt("flicker", flicker, "--json", fjson);
        Assert.True(f.ExitCode == 0, f.ToString());
        using JsonDocument fdoc = Doc(fjson);
        Assert.Equal("flicker", fdoc.RootElement.GetProperty("verb").GetString());
    }

    [Fact]
    public void Case_diff_result_diff_and_report_write_the_shared_json()
    {
        string d = Dir();
        string root = CliProcess.Root();
        string a = Write(d, "a.json", "{ \"x\": 1.0, \"y\": [1, 2, 3] }");
        string b = Write(d, "b.json", "{ \"x\": 1.0000000000000002, \"y\": [1, 2, 3] }");
        string rd = Path.Combine(d, "rd.json");
        CliProcess.Result diff = CliProcess.RunBuilt("result-diff", a, b, "--json", rd);
        Assert.Equal(3, diff.ExitCode);   // a difference: the verdict exit, the file is still written
        using (JsonDocument doc = Doc(rd))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("differences").GetInt64());
        }

        string cd = Path.Combine(d, "cd.json");
        string uk = Path.Combine(root, "data", "uk");
        CliProcess.Result same = CliProcess.RunBuilt("case-diff", uk, uk, "--json", cd);
        Assert.Equal(0, same.ExitCode);
        using (JsonDocument doc = Doc(cd))
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("equal").GetBoolean());

        string tmpl = Path.Combine(d, "template.json"), rep = Path.Combine(d, "rep.json");
        CliProcess.Result t = CliProcess.RunBuilt("report", "--template-for", a, "--out", tmpl, "--json", rep);
        Assert.True(File.Exists(tmpl), t.ToString());   // --out is still the report / template file
        using JsonDocument rdoc = Doc(rep);
        Assert.Equal("report", rdoc.RootElement.GetProperty("verb").GetString());
        Assert.Equal("template", rdoc.RootElement.GetProperty("summary").GetProperty("kind").GetString());
    }

    [Fact]
    public void Dgs_import_and_user_model_stay_outside_the_shared_writer()
    {
        Assert.DoesNotContain("dgs-import", CliOutput.WriterVerbs);
        Assert.DoesNotContain("user-model", CliOutput.WriterVerbs);
    }

    [Fact]
    public void The_snake_case_names_the_bridge_makes_pass_the_shared_writers_name_rule()
    {
        foreach ((string source, string expected) in new[] { ("schemaVersion", "schema_version"), ("Sha256", "sha256"), ("caseSha256", "case_sha256"),
                     ("LOLE", "lole"), ("studySHA", "study_sha"), ("a-b c", "a_b_c"), ("1st", "x_1st") })
        {
            string got = CliResultFromJson.Snake(source);
            Assert.Equal(expected, got);
            CliResultNames.Check(got, "name");
        }
    }
}
