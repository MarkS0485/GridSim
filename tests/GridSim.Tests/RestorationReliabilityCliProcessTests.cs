using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC3 P8: the restoration-study and reliability-study verbs through the real entry point, on the
/// <see cref="RestorationStudyTests"/> and <see cref="ReliabilityStudyTests"/> fixtures.</summary>
public sealed class RestorationReliabilityCliProcessTests
{
    private const string Case = """
        function mpc = restoration
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 3 0 0 0 0 1 1 0 132 1 1.1 0.9;
        2 1 40 0 0 0 1 1 0 132 1 1.1 0.9;
        3 1 30 0 0 0 1 1 0 132 1 1.1 0.9;
        4 1 20 0 0 0 1 1 0 132 1 1.1 0.9;
        5 1 10 0 0 0 1 1 0 132 1 1.1 0.9;
        ];
        mpc.gen = [
        1 0 0 300 -300 1 100 1 500 0;
        ];
        mpc.branch = [
        1 2 .005 .05 0 60 60 60 0 0 1 -360 360;
        2 3 .005 .05 0 100 100 100 0 0 1 -360 360;
        1 3 .005 .05 0 100 100 100 0 0 1 -360 360;
        3 4 .005 .05 0 100 100 100 0 0 1 -360 360;
        1 5 .005 .05 0 100 100 100 0 0 1 -360 360;
        ];
        """;

    private static string RestorationStudy(string hash, double minPu = 0.9, string? extra = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        @case = new { path = "restoration.m", sha256 = hash },
        voltage = new { minPu, maxPu = 1.1 },
        actions = new object[]
        {
            new { id = "A", branch = new { index = 0, from = 1, to = 2 }, minutes = 10 },
            new { id = "B", branch = new { index = 1, from = 2, to = 3 }, minutes = 10 },
            new { id = "C", branch = new { index = 2, from = 1, to = 3 }, minutes = 20 },
            new { id = "D", branch = new { index = 3, from = 3, to = 4 }, minutes = 5 },
        },
        search = new { maxNodes = 1000 }
    }).Replace("\"actions\"", (extra ?? "") + "\"actions\"");

    private static string ReliabilityStudy(bool withS3Rate = true, string? extra = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        assumptions = new { failureDependency = "IndependentFirstOrder", alternateSupply = "None", hoursPerYear = 8760 },
        switchingHours = 0.5,
        components = new object[]
        {
            new { id = "S1", parent = (string?)null, fused = false, switchable = true, failuresPerYear = 0.2, repairHours = 4 },
            new { id = "S2", parent = "S1", fused = false, switchable = true, failuresPerYear = 0.1, repairHours = 3 },
            withS3Rate
                ? new { id = "S3", parent = "S2", fused = false, switchable = true, failuresPerYear = (double?)0.3, repairHours = 2 }
                : new { id = "S3", parent = "S2", fused = false, switchable = true, failuresPerYear = (double?)null, repairHours = 2 },
            new { id = "L1", parent = "S1", fused = true, switchable = false, failuresPerYear = 0.5, repairHours = 1 },
        },
        loadPoints = new object[]
        {
            new { id = "A", component = "S1", customers = 100, averageLoadMw = 0.5 },
            new { id = "B", component = "S2", customers = 50, averageLoadMw = 0.3 },
            new { id = "C", component = "S3", customers = 80, averageLoadMw = 0.4 },
            new { id = "D", component = "L1", customers = 20, averageLoadMw = 0.1 },
        }
    }).Replace("\"components\"", (extra ?? "") + "\"components\"");

    private static string NewDirectory()
    {
        string directory = TestTemp.Under("gridsim-p8a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void Restoration_study_real_entry_point_is_repeatable_writes_once_and_maps_outcomes_to_exit_codes()
    {
        string directory = NewDirectory();
        try
        {
            string model = Path.Combine(directory, "restoration.m");
            File.WriteAllText(model, Case);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            string study = Path.Combine(directory, "study.json");
            File.WriteAllText(study, RestorationStudy(hash));
            string output = Path.Combine(directory, "result.json");

            var first = CliProcess.RunBuilt("restoration-study", study, "--out", output);
            Assert.True(first.ExitCode == 0, first.StdErr + first.StdOut);
            var second = CliProcess.RunBuilt("restoration-study", study);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);
            string saved = File.ReadAllText(output);
            using (JsonDocument doc = JsonDocument.Parse(saved))
            {
                JsonElement root = doc.RootElement;
                Assert.Equal("restoration-study", root.GetProperty("study").GetString());
                Assert.Equal(hash, root.GetProperty("provenance").GetProperty("caseSha256").GetString());
                JsonElement result = root.GetProperty("result");
                Assert.Equal("OPTIMAL", result.GetProperty("status").GetString());
                Assert.Equal(["A", "C", "D", "B"],
                    result.GetProperty("sequence").EnumerateArray().Select(s => s.GetProperty("action").GetString()!).ToArray());
                Assert.Equal(2000.0 / 60.0, result.GetProperty("objectiveMwh").GetDouble(), 9);
                Assert.Equal(1500.0 / 60.0, result.GetProperty("relaxedLowerBoundMwh").GetDouble(), 9);
            }
            Assert.Equal(1, CliProcess.RunBuilt("restoration-study", study, "--out", output).ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));

            string infeasible = Path.Combine(directory, "infeasible.json");
            File.WriteAllText(infeasible, RestorationStudy(hash, minPu: 1.01));
            var refused = CliProcess.RunBuilt("restoration-study", infeasible);
            Assert.Equal(3, refused.ExitCode);
            using (JsonDocument doc = JsonDocument.Parse(refused.StdOut))
                Assert.Equal("INFEASIBLE", doc.RootElement.GetProperty("result").GetProperty("status").GetString());

            string bad = Path.Combine(directory, "bad.json");
            File.WriteAllText(bad, RestorationStudy(hash, extra: "\"surprise\":1,"));
            Assert.Equal(1, CliProcess.RunBuilt("restoration-study", bad).ExitCode);
            File.WriteAllText(bad, RestorationStudy(hash, extra: "\"schemaVersion\":1,"));
            var duplicate = CliProcess.RunBuilt("restoration-study", bad);
            Assert.Equal(1, duplicate.ExitCode);
            Assert.Contains("Duplicate property", duplicate.StdErr);
            File.WriteAllText(bad, RestorationStudy(new string('0', 64)));
            var mismatch = CliProcess.RunBuilt("restoration-study", bad);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch", mismatch.StdErr);
            File.WriteAllText(bad, RestorationStudy(hash, extra: "\"precedence\":[{\"before\":\"A\",\"after\":\"A\"}],"));
            Assert.Equal(1, CliProcess.RunBuilt("restoration-study", bad).ExitCode);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Reliability_study_real_entry_point_reports_indices_with_units_and_missing_data()
    {
        string directory = NewDirectory();
        try
        {
            string study = Path.Combine(directory, "study.json");
            File.WriteAllText(study, ReliabilityStudy());
            string output = Path.Combine(directory, "result.json");
            var first = CliProcess.RunBuilt("reliability-study", study, "--out", output);
            Assert.True(first.ExitCode == 0, first.StdErr + first.StdOut);
            var second = CliProcess.RunBuilt("reliability-study", study);
            Assert.Equal(first.StdOut, second.StdOut);
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(output)))
            {
                JsonElement result = doc.RootElement.GetProperty("result");
                Assert.Equal("COMPLETE", result.GetProperty("status").GetString());
                JsonElement saidi = result.GetProperty("systemIndices").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "SAIDI");
                Assert.Equal(1.314, saidi.GetProperty("value").GetDouble(), 12);
                Assert.Equal("h/(customer.yr)", saidi.GetProperty("units").GetString());
            }
            Assert.Equal(1, CliProcess.RunBuilt("reliability-study", study, "--out", output).ExitCode);

            string missing = Path.Combine(directory, "missing.json");
            File.WriteAllText(missing, ReliabilityStudy(withS3Rate: false));
            var incomplete = CliProcess.RunBuilt("reliability-study", missing);
            Assert.Equal(3, incomplete.ExitCode);
            using (JsonDocument doc = JsonDocument.Parse(incomplete.StdOut))
            {
                JsonElement result = doc.RootElement.GetProperty("result");
                Assert.Equal("INCOMPLETE", result.GetProperty("status").GetString());
                Assert.Equal("S3", result.GetProperty("missing")[0].GetProperty("component").GetString());
            }

            string bad = Path.Combine(directory, "bad.json");
            File.WriteAllText(bad, ReliabilityStudy(extra: "\"surprise\":1,"));
            Assert.Equal(1, CliProcess.RunBuilt("reliability-study", bad).ExitCode);
            File.WriteAllText(bad, ReliabilityStudy().Replace("IndependentFirstOrder", "CommonMode"));
            var assumption = CliProcess.RunBuilt("reliability-study", bad);
            Assert.Equal(1, assumption.ExitCode);
            Assert.Contains("failureDependency", assumption.StdErr);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Help_and_invalid_invocations_have_explicit_outcomes()
    {
        foreach (string verb in new[] { "restoration-study", "reliability-study" })
        {
            var help = CliProcess.RunBuilt(verb, "--help");
            Assert.Equal(0, help.ExitCode);
            Assert.Contains(verb, help.StdOut);
            Assert.Equal(2, CliProcess.RunBuilt(verb).ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt(verb, "missing.json", "--csv", "x.csv").ExitCode);
        }
        Assert.Contains("maxNodes", CliProcess.RunBuilt("restoration-study", "--help").StdOut);
        Assert.Contains("switchingHours", CliProcess.RunBuilt("reliability-study", "--help").StdOut);
    }
}
