using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC3 P3: the <c>cgmes-study</c> entry point end to end (the built CLI in a child process).</summary>
public sealed class CgmesStudyCliProcessTests
{
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Copy the fixture bundle into a fresh folder and write a study file binding it.</summary>
    private static (string Directory, string Study, string BundleSha) Prepare(string? bundleSha = null, string extra = "")
    {
        string dir = TestTemp.Under("gridsim-cgmes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string[] files = ["p3_EQ.xml", "p3_TP.xml", "p3_SSH.xml", "p3_SV.xml"];
        string[] types = ["EQ", "TP", "SSH", "SV"];
        var manifest = new StringBuilder();
        var profiles = new StringBuilder();
        for (int i = 0; i < files.Length; i++)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(CgmesStudyJobTests.FixtureDir, files[i]));
            File.WriteAllBytes(Path.Combine(dir, files[i]), bytes);
            string sha = Sha(bytes);
            manifest.Append(types[i]).Append("\t2.4.15\t").Append(files[i]).Append('\t').Append(sha).Append('\n');
            if (i > 0) profiles.Append(',');
            profiles.Append($$"""{"profile":"{{types[i]}}","version":"2.4.15","path":"{{files[i]}}","sha256":"{{sha}}"}""");
        }
        string bundle = bundleSha ?? Sha(Encoding.UTF8.GetBytes(manifest.ToString()));
        string study = Path.Combine(dir, "study.json");
        File.WriteAllText(study, $$"""{"schemaVersion":1,"bundle":{"sha256":"{{bundle}}","profiles":[{{profiles}}]},"voltage":{"minPu":0.95,"maxPu":1.05}{{extra}}}""");
        return (dir, study, Sha(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    [Fact]
    public void The_entry_point_runs_the_full_job_repeatably_and_never_overwrites()
    {
        (string dir, string study, string bundleSha) = Prepare();
        try
        {
            string output = Path.Combine(dir, "result.json");
            string export = Path.Combine(dir, "exchange.xml");
            var first = CliProcess.RunBuilt("cgmes-study", study, "--out", output, "--export", export);
            Assert.True(first.ExitCode == 0, first.ToString());
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(output)))
            {
                JsonElement root = doc.RootElement;
                Assert.Equal(bundleSha, root.GetProperty("provenance").GetProperty("bundleSha256").GetString());
                Assert.Equal("SECURE", root.GetProperty("result").GetProperty("status").GetString());
                Assert.Equal(Sha(File.ReadAllBytes(export)), root.GetProperty("provenance").GetProperty("exportSha256").GetString());
                Assert.Equal(4, root.GetProperty("result").GetProperty("bundle").GetProperty("profiles").GetArrayLength());
            }
            var second = CliProcess.RunBuilt("cgmes-study", study);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);

            string saved = File.ReadAllText(output);
            var refusal = CliProcess.RunBuilt("cgmes-study", study, "--out", output);
            Assert.Equal(1, refusal.ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Identity_and_schema_failures_are_input_errors_and_a_bad_invocation_is_a_usage_error()
    {
        (string dir, string study, string resolved) = Prepare(bundleSha: new string('a', 64));
        try
        {
            var mismatch = CliProcess.RunBuilt("cgmes-study", study);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Bundle SHA256 mismatch. Resolved identity: " + resolved, mismatch.StdErr);

            File.WriteAllText(study, File.ReadAllText(study).Replace("\"voltage\"", "\"unknownField\":1,\"voltage\""));
            Assert.Equal(1, CliProcess.RunBuilt("cgmes-study", study).ExitCode);

            Assert.Equal(2, CliProcess.RunBuilt("cgmes-study").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("cgmes-study", study, "--json", "x.json").ExitCode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_switch_action_and_the_security_stage_run_from_the_study_file()
    {
        (string dir, string study, _) = Prepare(extra:
            ""","import":{"topology":"switches"},"switchActions":[{"mrid":"_BRK_A","open":true}],"security":{"outages":[{"mrid":"_L3"}],"postFaultRating":"A"}""");
        try
        {
            var run = CliProcess.RunBuilt("cgmes-study", study);
            Assert.True(run.ExitCode == 3, run.ToString());   // the radial L3 outage islands C: NOT SECURE, Incomplete
            using JsonDocument doc = JsonDocument.Parse(run.StdOut);
            JsonElement result = doc.RootElement.GetProperty("result");
            Assert.Equal("Incomplete", result.GetProperty("outcome").GetString());
            Assert.Equal(4, result.GetProperty("switching").GetProperty("busesAfter").GetInt32());
            Assert.Equal("ISLANDED", result.GetProperty("security").GetProperty("outages")[0].GetProperty("disposition").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verb_help_prints_the_section()
    {
        var help = CliProcess.RunBuilt("cgmes-study", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("gridsim cgmes-study <study.json>", help.StdOut);
        Assert.Contains("--export", help.StdOut);
    }
}
