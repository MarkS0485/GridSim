using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC3 P5: the corrective-sequence verb through the real entry point, on the
/// <see cref="CorrectiveSequenceRunnerTests"/> fixture written as a MATPOWER case.</summary>
public sealed class CorrectiveSequenceCliProcessTests
{
    private const string Case = """
        function mpc = corrective
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 2 0 0 0 0 1 1 0 230 1 1.1 0.9;
        2 2 180 0 0 0 1 1 0 230 1 1.1 0.9;
        3 3 20 0 0 0 1 1 0 230 1 1.1 0.9;
        ];
        mpc.gen = [
        1 150 0 200 -200 1 100 1 250 0;
        2 30 0 200 -200 1 100 1 150 0;
        3 20 0 200 -200 1 100 1 100 0;
        ];
        mpc.branch = [
        1 2 0 .3 0 60 80 120 0 0 1 -360 360;
        1 2 0 .3 0 60 80 120 0 0 1 -360 360;
        1 2 0 .3 0 60 65 70 0 0 1 -360 360;
        2 3 0 .05 0 100 100 100 0 0 1 -360 360;
        ];
        """;

    private static string Study(string hash, double minutes, string? extra = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        @case = new { path = "corrective.m", sha256 = hash },
        voltage = new { minPu = .95, maxPu = 1.05 },
        ratings = new { firstStage = "C", intermediate = "B", final = "B" },
        policy = new
        {
            availableMinutes = minutes,
            eligible = new object[]
            {
                new { generator = 0, bus = 1, direction = "Down", minFinalMw = 0, maxFinalMw = 200, maxDeltaMw = 100, rampMwPerMin = 10, windowMinutes = 15 },
                new { generator = 1, bus = 2, direction = "Up", minFinalMw = 0, maxFinalMw = 150, maxDeltaMw = 100, rampMwPerMin = 10, windowMinutes = 15 },
            }
        },
        sequences = new object[]
        {
            new
            {
                first = new { index = 2, from = 1, to = 2 }, second = new { index = 0, from = 1, to = 2 },
                proposal = "Prescribed",
                actions = new[] { new { generator = 0, bus = 1, deltaMw = -90.0 }, new { generator = 1, bus = 2, deltaMw = 90.0 } }
            }
        }
    }).Replace("\"sequences\"", (extra ?? "") + "\"sequences\"");

    [Fact]
    public void Real_entry_point_is_repeatable_writes_once_and_maps_verdicts_to_exit_codes()
    {
        string directory = TestTemp.Under("gridsim-corrective-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "corrective.m");
            File.WriteAllText(model, Case);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            string secure = Path.Combine(directory, "secure.json");
            File.WriteAllText(secure, Study(hash, 10));
            string output = Path.Combine(directory, "result.json");

            var first = CliProcess.RunBuilt("corrective-sequence", secure, "--out", output);
            Assert.True(first.ExitCode == 0, first.StdErr + first.StdOut);
            var second = CliProcess.RunBuilt("corrective-sequence", secure);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);
            string saved = File.ReadAllText(output);
            using (JsonDocument doc = JsonDocument.Parse(saved))
            {
                JsonElement root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("corrective-sequence", root.GetProperty("study").GetString());
                Assert.Equal(hash, root.GetProperty("provenance").GetProperty("caseSha256").GetString());
                JsonElement result = root.GetProperty("result");
                Assert.Equal("SECURE", result.GetProperty("status").GetString());
                JsonElement steps = result.GetProperty("sequences")[0].GetProperty("steps");
                Assert.Equal(9, steps.GetArrayLength());
                Assert.Equal("FinalAcVerification", steps[8].GetProperty("name").GetString());
            }
            var refusal = CliProcess.RunBuilt("corrective-sequence", secure, "--out", output);
            Assert.Equal(1, refusal.ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));

            // Halved window: the ramp budget is exhausted, the action rejected and the study NOT SECURE.
            string halved = Path.Combine(directory, "halved.json");
            File.WriteAllText(halved, Study(hash, 5));
            var rejected = CliProcess.RunBuilt("corrective-sequence", halved);
            Assert.Equal(3, rejected.ExitCode);
            using (JsonDocument doc = JsonDocument.Parse(rejected.StdOut))
            {
                JsonElement sequence = doc.RootElement.GetProperty("result").GetProperty("sequences")[0];
                Assert.Equal("ActionRejected", sequence.GetProperty("outcome").GetString());
                Assert.Equal(40, sequence.GetProperty("steps")[4].GetProperty("checks")[0].GetProperty("shortfallMw").GetDouble());
            }
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model))));

            // Strict input: unknown field, duplicated field, case identity mismatch.
            string bad = Path.Combine(directory, "bad.json");
            File.WriteAllText(bad, Study(hash, 10, "\"surprise\":1,"));
            Assert.Equal(1, CliProcess.RunBuilt("corrective-sequence", bad).ExitCode);
            File.WriteAllText(bad, Study(hash, 10, "\"schemaVersion\":1,"));
            var duplicate = CliProcess.RunBuilt("corrective-sequence", bad);
            Assert.Equal(1, duplicate.ExitCode);
            Assert.Contains("Duplicate property", duplicate.StdErr);
            File.WriteAllText(bad, Study(new string('0', 64), 10));
            var mismatch = CliProcess.RunBuilt("corrective-sequence", bad);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch", mismatch.StdErr);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Help_and_foreign_flags_have_explicit_outcomes()
    {
        var help = CliProcess.RunBuilt("corrective-sequence", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("availableMinutes", help.StdOut);
        Assert.Equal(2, CliProcess.RunBuilt("corrective-sequence", "missing.json", "--csv", "x.csv").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("corrective-sequence").ExitCode);
    }
}
