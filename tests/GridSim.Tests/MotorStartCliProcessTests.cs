using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static GridSim.Tests.MotorStartTestSupport;

namespace GridSim.Tests;

/// <summary>RC3 P8: <c>gridsim motor-start</c> through the real entry point: identity binding, repeatability, the exit
/// mapping (RUNNING 0, a stall 3, input errors 1, usage 2) and the no-overwrite result file.</summary>
public sealed class MotorStartCliProcessTests
{
    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The consumed-file identity, computed independently with the documented rule: one "path TAB sha256" line
    /// per file (the manifest, each network file, every v2 schema file in ordinal path order).</summary>
    private static string Identity(string dir, string manifest, params string[] networks)
    {
        var sb = new StringBuilder();
        sb.Append(manifest).Append('\t').Append(Sha(Path.Combine(dir, manifest))).Append('\n');
        foreach (string n in networks) sb.Append("network/").Append(n).Append('\t').Append(Sha(Path.Combine(dir, n))).Append('\n');
        foreach (string f in Directory.GetFiles(Schemas, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append("schemas/").Append(Path.GetRelativePath(Schemas, f).Replace('\\', '/')).Append('\t').Append(Sha(f)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Study(string dir, string manifest, string network, string motor, string sha, double horizon = 10) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            @case = new { path = manifest, sha256 = sha },
            motor,
            initial = new { speedPu = 0.0, connected = false },
            start = new { atSeconds = 0.1, method = "directOnLine" },
            integration = new { stepSeconds = 0.001 },
            termination = new { runningSpeedPu = 0.98, horizonSeconds = horizon, stallDwellSeconds = 0.5, stallAcceleratingTorquePu = 0.0 },
            preStart = new { pgToleranceMw = 1e-3, vmTolerancePu = 1e-6, maxSlackMovementMw = 2.0 },
            report = new { sampleEverySteps = 100 },
        });

    private static string Setup(string fixtureCase, string fixtureNetwork)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-motor-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(Fixture(fixtureCase), Path.Combine(dir, fixtureCase));
        File.Copy(Fixture(fixtureNetwork), Path.Combine(dir, fixtureNetwork));
        return dir;
    }

    [Fact]
    public void A_running_start_is_repeatable_and_writes_a_new_result_file()
    {
        string dir = Setup("prestart.case.json", "prestart.network.json");
        try
        {
            string sha = Identity(dir, "prestart.case.json", "prestart.network.json");
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, Study(dir, "prestart.case.json", "prestart.network.json", "M1", sha));
            string output = Path.Combine(dir, "result.json");
            var first = CliProcess.RunBuilt("motor-start", study, "--out", output);
            Assert.True(first.ExitCode == 0, first.ToString());
            var second = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(output));
            JsonElement result = doc.RootElement.GetProperty("result");
            Assert.Equal("RUNNING", result.GetProperty("status").GetString());
            Assert.Equal("running", result.GetProperty("outcome").GetString());
            Assert.Equal(sha, doc.RootElement.GetProperty("provenance").GetProperty("caseSha256").GetString());
            Assert.True(result.GetProperty("preStart").GetProperty("passed").GetBoolean());
            Assert.InRange(result.GetProperty("accelerationTimeSeconds").GetDouble(), 1.0, 5.0);
            string saved = File.ReadAllText(output);
            var refusal = CliProcess.RunBuilt("motor-start", study, "--out", output);
            Assert.Equal(1, refusal.ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_stall_exits_with_the_verdict_code_and_bad_input_with_the_input_code()
    {
        string dir = Setup("weak.case.json", "weak.network.json");
        try
        {
            string sha = Identity(dir, "weak.case.json", "weak.network.json");
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, Study(dir, "weak.case.json", "weak.network.json", "M2", sha));
            var stall = CliProcess.RunBuilt("motor-start", study);
            Assert.True(stall.ExitCode == 3, stall.ToString());
            Assert.Contains("\"status\": \"STALLED\"", stall.StdOut, StringComparison.Ordinal);

            File.WriteAllText(study, Study(dir, "weak.case.json", "weak.network.json", "M2", new string('0', 64)));
            var mismatch = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch. Resolved identity: " + sha, mismatch.StdErr, StringComparison.Ordinal);

            File.WriteAllText(study, Study(dir, "weak.case.json", "weak.network.json", "M2", sha).Replace("\"connected\":false", "\"connected\":true", StringComparison.Ordinal));
            var connected = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, connected.ExitCode);
            Assert.Contains("is not a start", connected.StdErr, StringComparison.Ordinal);

            File.WriteAllText(study, Study(dir, "weak.case.json", "weak.network.json", "M2", sha).Replace("\"motor\":", "\"extra\":1,\"motor\":", StringComparison.Ordinal));
            var unknown = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, unknown.ExitCode);
            Assert.Contains("unknown field 'extra'", unknown.StdErr, StringComparison.Ordinal);

            var usage = CliProcess.RunBuilt("motor-start");
            Assert.Equal(2, usage.ExitCode);
            var foreign = CliProcess.RunBuilt("motor-start", study, "--csv", "x.csv");
            Assert.Equal(2, foreign.ExitCode);
            var help = CliProcess.RunBuilt("motor-start", "--help");
            Assert.Equal(0, help.ExitCode);
            Assert.Contains("runningSpeedPu", help.StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
