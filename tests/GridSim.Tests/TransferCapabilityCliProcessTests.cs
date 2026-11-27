using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC3 P4 transfer-capability through the real CLI entry point: strict v1 study, case identity binding,
/// repeatable stdout, no-overwrite result file, exit mapping and refusal of invalid input.</summary>
public sealed class TransferCapabilityCliProcessTests
{
    // The thermal fixture of TransferCapabilityStudyTests as a MATPOWER file: slack unit at bus 1 (source), a fixed-P
    // unit at PQ bus 2 (sink) balancing the 100 MW load, two 0.2 pu circuits with RATE_A 100 and RATE_B 90.
    private const string Case = """
        function mpc = corridor
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
        2 1 100 0 0 0 1 1 0 230 1 1.1 0.9;
        ];
        mpc.gen = [
        1 0 0 200 -200 1 100 1 300 -50;
        2 100 0 50 -50 1 100 1 200 0;
        ];
        mpc.branch = [
        1 2 0 .2 0 100 90 90 0 0 1 -360 360;
        1 2 0 .2 0 100 90 90 0 0 1 -360 360;
        ];
        """;

    private static string Study(string hash, object? sinks = null, string? extra = null)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            @case = new { path = "corridor.m", sha256 = hash },
            sources = new[] { new { index = 0, bus = 1, weight = 1.0 } },
            sinks = sinks ?? new[] { new { index = 1, bus = 2, weight = 1.0 } },
            outages = new[] { new { index = 0, from = 1, to = 2 }, new { index = 1, from = 1, to = 2 } },
            voltage = new { minPu = .9, maxPu = 1.1 },
            postFaultRating = "B",
            search = new { maxMw = 100.0, stepMw = 10.0, toleranceMw = .01, maxTrials = 60 },
            interfaces = new[] { new { name = "corridor", branches = new[] { new { index = 0, from = 1, to = 2, sign = 1 }, new { index = 1, from = 1, to = 2, sign = 1 } } } }
        });
        return extra is null ? json : json[..^1] + "," + extra + "}";
    }

    [Fact]
    public void Real_entry_point_reports_the_thermal_limit_repeatably()
    {
        string directory = TestTemp.Under("gridsim-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "corridor.m");
            File.WriteAllText(model, Case);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            string input = Path.Combine(directory, "study.json");
            File.WriteAllText(input, Study(hash));
            string output = Path.Combine(directory, "result.json");

            var first = CliProcess.RunBuilt("transfer-capability", input, "--out", output);
            Assert.True(first.ExitCode == 0, first.StdErr + first.StdOut);
            var second = CliProcess.RunBuilt("transfer-capability", input);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);
            string saved = File.ReadAllText(output);
            Assert.Equal(first.StdOut.Replace("\r\n", "\n").TrimEnd(), saved.Replace("\r\n", "\n").TrimEnd());

            using JsonDocument doc = JsonDocument.Parse(saved);
            JsonElement root = doc.RootElement;
            Assert.Equal("transfer-capability", root.GetProperty("study").GetString());
            Assert.Equal(hash, root.GetProperty("provenance").GetProperty("caseSha256").GetString());
            // The loader kept the authored schedule (no incidental balancing).
            Assert.Equal(100, root.GetProperty("inputGenerators")[1].GetProperty("pg").GetDouble());
            JsonElement result = root.GetProperty("result");
            Assert.Equal("VERIFIED", result.GetProperty("status").GetString());
            Assert.Equal("Limited", result.GetProperty("outcome").GetString());
            double lo = result.GetProperty("lowerMw").GetDouble(), hi = result.GetProperty("upperMw").GetDouble();
            double exact = 100 * .9 * Math.Sqrt(1 - .04 * .81);
            Assert.InRange(exact, lo - 1e-4, hi + 1e-4);
            JsonElement limit = result.GetProperty("firstLimit");
            Assert.Equal("BranchFrom", limit.GetProperty("kind").GetString());
            Assert.Equal(0, limit.GetProperty("outageIndex").GetInt32());
            Assert.Equal(1, limit.GetProperty("index").GetInt32());
            Assert.Equal(90, limit.GetProperty("limit").GetDouble());
            Assert.Equal("UNVERIFIED", root.GetProperty("comparator").GetProperty("status").GetString());

            var refusal = CliProcess.RunBuilt("transfer-capability", input, "--out", output);
            Assert.Equal(1, refusal.ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Invalid_studies_exit_as_input_errors_before_any_solve()
    {
        string directory = TestTemp.Under("gridsim-transfer-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "corridor.m");
            File.WriteAllText(model, Case);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            string input = Path.Combine(directory, "study.json");
            string output = Path.Combine(directory, "result.json");

            File.WriteAllText(input, Study("0000"));
            var mismatch = CliProcess.RunBuilt("transfer-capability", input, "--out", output);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch", mismatch.StdErr);
            Assert.False(File.Exists(output));

            File.WriteAllText(input, Study(hash, sinks: new[] { new { index = 0, bus = 1, weight = 1.0 } }));
            var overlap = CliProcess.RunBuilt("transfer-capability", input);
            Assert.Equal(1, overlap.ExitCode);
            Assert.Contains("both source and sink", overlap.StdErr);

            File.WriteAllText(input, Study(hash, extra: "\"monotone\":true"));
            Assert.Equal(1, CliProcess.RunBuilt("transfer-capability", input).ExitCode);

            File.WriteAllText(input, Study(hash, sinks: new[] { new { index = 1, weight = 1.0 } }));
            var shape = CliProcess.RunBuilt("transfer-capability", input);
            Assert.Equal(1, shape.ExitCode);
            Assert.Contains("integer index, bus", shape.StdErr);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Help_and_foreign_flags_have_explicit_outcomes()
    {
        var help = CliProcess.RunBuilt("transfer-capability", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("toleranceMw", help.StdOut);
        Assert.Equal(2, CliProcess.RunBuilt("transfer-capability", "missing.json", "--csv", "x.csv").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("transfer-capability").ExitCode);
    }
}
