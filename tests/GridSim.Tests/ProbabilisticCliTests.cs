using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-09: the <c>probabilistic</c> verb run as the built CLI (strict study, seed, methods, exit codes, result file).</summary>
public sealed class ProbabilisticCliTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-r409-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Units3 = """
        "units": [ {"id":"g1","capacityMw":100,"availability":0.9}, {"id":"g2","capacityMw":100,"availability":0.9}, {"id":"g3","capacityMw":100,"availability":0.9} ],
        "load": { "constantMw": 150, "hours": 1 }
        """;

    private static string Adequacy(string methods, string extra = "\"seed\": 7,") =>
        "{ \"schemaVersion\": 1, \"study\": \"adequacy\", " + extra + " " + Units3 + ", \"methods\": " + methods + " }";

    [Fact]
    public void Adequacy_by_convolution_gives_the_closed_form_lolp_and_writes_a_new_file_only()
    {
        string d = Dir();
        string study = Write(d, "s.json", Adequacy("[ {\"method\":\"convolution\",\"samples\":1} ]"));
        string outFile = Path.Combine(d, "r.json");
        CliProcess.Result r = CliProcess.RunBuilt("probabilistic", study, "--out", outFile);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outFile));
        JsonElement root = doc.RootElement;
        Assert.Equal("COMPLETE", root.GetProperty("status").GetString());
        JsonElement res = root.GetProperty("methods")[0].GetProperty("result");
        Assert.Equal(0.028, res.GetProperty("lole").GetDouble(), 12);     // 0.1^3 + 3 x 0.9 x 0.1^2
        Assert.Equal("gridsim-rng/1", res.GetProperty("provenance").GetProperty("generatorVersion").GetString());
        // An existing result path is refused.
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", study, "--out", outFile).ExitCode);
    }

    [Fact]
    public void Several_methods_run_in_one_study_and_threads_do_not_change_a_byte()
    {
        string d = Dir();
        string study = Write(d, "s.json", Adequacy("[ {\"method\":\"convolution\",\"samples\":1}, {\"method\":\"monte-carlo\",\"samples\":5000}, {\"method\":\"qmc\",\"samples\":4096}, {\"method\":\"qmc-shifted\",\"samples\":1024,\"replicates\":4} ]"));
        CliProcess.Result one = CliProcess.RunBuilt("probabilistic", study, "--threads", "1");
        CliProcess.Result many = CliProcess.RunBuilt("probabilistic", study, "--threads", "8");
        Assert.True(one.ExitCode == 0, one.ToString());
        Assert.Equal(one.StdOut, many.StdOut);
        using JsonDocument doc = JsonDocument.Parse(one.StdOut);
        Assert.Equal(4, doc.RootElement.GetProperty("methods").GetArrayLength());
        foreach (JsonElement m in doc.RootElement.GetProperty("methods").EnumerateArray())
            Assert.Equal("COMPLETE", m.GetProperty("result").GetProperty("status").GetString());
        // A different seed changes the sampled digests (the study text differs only in the seed).
        string other = Write(d, "t.json", Adequacy("[ {\"method\":\"monte-carlo\",\"samples\":5000} ]", "\"seed\": 8,"));
        string same = Write(d, "u.json", Adequacy("[ {\"method\":\"monte-carlo\",\"samples\":5000} ]", "\"seed\": 7,"));
        string Digest(string path)
        {
            using JsonDocument x = JsonDocument.Parse(CliProcess.RunBuilt("probabilistic", path).StdOut);
            return x.RootElement.GetProperty("methods")[0].GetProperty("result").GetProperty("provenance").GetProperty("sampleDigest").GetString()!;
        }
        Assert.NotEqual(Digest(same), Digest(other));
        Assert.Equal(Digest(same), Digest(same));
    }

    [Fact]
    public void Missing_seed_unknown_fields_and_bad_input_are_refused_with_exit_1_and_usage_with_exit_2()
    {
        string d = Dir();
        string m = "[ {\"method\":\"monte-carlo\",\"samples\":100} ]";
        CliProcess.Result noSeed = CliProcess.RunBuilt("probabilistic", Write(d, "a.json", Adequacy(m, "")));
        Assert.Equal(1, noSeed.ExitCode);
        Assert.Contains("seed", noSeed.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Write(d, "b.json", Adequacy(m, "\"seed\": 1, \"bogus\": 2,"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Write(d, "c.json", Adequacy("[ {\"method\":\"magic\",\"samples\":100} ]"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Write(d, "e.json", Adequacy("[ {\"method\":\"monte-carlo\",\"samples\":0} ]"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Write(d, "f.json", Adequacy(m, "\"seed\": 1, \"seed\": 2,"))).ExitCode);   // duplicate field
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Write(d, "g.json", Adequacy(m, "\"seed\": 1, \"case\": {\"builtIn\":\"case9\",\"sha256\":\"x\"},"))).ExitCode);   // load-flow field in an adequacy study
        Assert.Equal(1, CliProcess.RunBuilt("probabilistic", Path.Combine(d, "missing.json")).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("probabilistic").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("probabilistic", Write(d, "h.json", Adequacy(m)), "--threads", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("probabilistic", Write(d, "i.json", Adequacy(m)), "--bogus").ExitCode);
    }

    [Fact]
    public void A_method_outside_its_domain_is_out_of_scope_and_exits_3()
    {
        string d = Dir();
        string study = Write(d, "s.json", "{ \"schemaVersion\": 1, \"study\": \"adequacy\", \"seed\": 1, "
            + "\"units\": [ {\"id\":\"a\",\"capacityMw\":100.5,\"availability\":0.9}, {\"id\":\"b\",\"capacityMw\":50,\"availability\":0.9} ], "
            + "\"load\": { \"hourlyMw\": [120, 130] }, \"methods\": [ {\"method\":\"convolution\",\"samples\":1}, {\"method\":\"monte-carlo\",\"samples\":1000} ] }");
        CliProcess.Result r = CliProcess.RunBuilt("probabilistic", study);
        Assert.Equal(3, r.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(r.StdOut);
        JsonElement methods = doc.RootElement.GetProperty("methods");
        Assert.Equal("OUT OF SCOPE", methods[0].GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("COMPLETE", methods[1].GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("INCOMPLETE", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Load_flow_on_a_built_in_case_is_bound_by_sha256_and_runs_dc_and_refuses_ac_convolution()
    {
        string d = Dir();
        string Study(string sha, string evaluator, string methods) =>
            "{ \"schemaVersion\": 1, \"study\": \"load-flow\", \"seed\": 5, \"case\": {\"builtIn\":\"case9\",\"sha256\":\"" + sha + "\"}, \"evaluator\":\"" + evaluator + "\","
            + "\"inputs\": [ {\"id\":\"load5\",\"target\":\"bus-load\",\"bus\":5,\"distribution\":{\"kind\":\"normal\",\"mean\":90,\"sd\":10}} ],"
            + "\"quantiles\": [0.5], \"methods\": " + methods + " }";
        CliProcess.Result bad = CliProcess.RunBuilt("probabilistic", Write(d, "a.json", Study("00", "dc", "[ {\"method\":\"monte-carlo\",\"samples\":64} ]")));
        Assert.Equal(1, bad.ExitCode);
        Match id = Regex.Match(bad.StdErr, "Resolved identity: ([0-9a-f]{64})");
        Assert.True(id.Success, bad.ToString());
        string sha = id.Groups[1].Value;
        CliProcess.Result ok = CliProcess.RunBuilt("probabilistic", Write(d, "b.json", Study(sha, "dc", "[ {\"method\":\"convolution\",\"samples\":1}, {\"method\":\"monte-carlo\",\"samples\":2048} ]")));
        Assert.True(ok.ExitCode == 0, ok.ToString());
        using JsonDocument doc = JsonDocument.Parse(ok.StdOut);
        JsonElement conv = doc.RootElement.GetProperty("methods")[0].GetProperty("result");
        JsonElement mc = doc.RootElement.GetProperty("methods")[1].GetProperty("result");
        Assert.Equal(9, conv.GetProperty("branches").GetArrayLength());
        double cm = conv.GetProperty("branches")[3].GetProperty("mean").GetDouble();
        double mm = mc.GetProperty("branches")[3].GetProperty("mean").GetDouble();
        double se = mc.GetProperty("branches")[3].GetProperty("meanStdError").GetDouble();
        Assert.True(Math.Abs(cm - mm) <= 5 * se + 1e-9, $"convolution mean {cm} vs Monte Carlo {mm}, SE {se}");
        // AC with convolution: out of scope (exit 3) while the sampled AC method completes.
        CliProcess.Result ac = CliProcess.RunBuilt("probabilistic", Write(d, "c.json", Study(sha, "ac", "[ {\"method\":\"convolution\",\"samples\":1}, {\"method\":\"qmc\",\"samples\":64} ]")));
        Assert.Equal(3, ac.ExitCode);
        using JsonDocument acDoc = JsonDocument.Parse(ac.StdOut);
        Assert.Equal("OUT OF SCOPE", acDoc.RootElement.GetProperty("methods")[0].GetProperty("result").GetProperty("status").GetString());
        JsonElement q = acDoc.RootElement.GetProperty("methods")[1].GetProperty("result");
        Assert.Equal("COMPLETE", q.GetProperty("status").GetString());
        Assert.Equal(9, q.GetProperty("buses").GetArrayLength());
    }
}
