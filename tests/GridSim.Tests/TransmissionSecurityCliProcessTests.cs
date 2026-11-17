using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

public sealed class TransmissionSecurityCliProcessTests
{
    [Theory]
    [InlineData(.90, 0)]
    [InlineData(.95, 3)]
    public void Real_entry_point_is_repeatable_and_preserves_existing_output(double minimum, int exit)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gridsim-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "parallel.m");
            File.WriteAllText(model, """
                function mpc = parallel
                mpc.version = '2';
                mpc.baseMVA = 100;
                mpc.bus = [
                1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
                2 1 80 30 0 0 1 1 0 230 1 1.1 0.9;
                ];
                mpc.gen = [1 80 0 100 0 1 100 1 200 0;];
                mpc.branch = [
                1 2 0 .2 0 100 100 100 0 0 1 -360 360;
                1 2 0 .2 0 100 100 100 0 0 1 -360 360;
                ];
                """);
            string input = Path.Combine(directory, "study.json");
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            File.WriteAllText(input, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "parallel.m", sha256 = hash },
                outages = new[] { new { index = 0, from = 1, to = 2 }, new { index = 1, from = 1, to = 2 } },
                voltage = new { minPu = minimum, maxPu = 1.1 },
                postFaultRating = "A"
            }));
            string output = Path.Combine(directory, "result.json");
            var first = CliProcess.RunBuilt("transmission-security", input, "--out", output);
            Assert.True(first.ExitCode == exit, first.StdErr + first.StdOut);
            var second = CliProcess.RunBuilt("transmission-security", input);
            Assert.Equal(exit, second.ExitCode);
            Assert.Equal(first.StdOut, second.StdOut);
            string saved = File.ReadAllText(output);
            var refusal = CliProcess.RunBuilt("transmission-security", input, "--out", output);
            Assert.Equal(1, refusal.ExitCode);
            Assert.Equal(saved, File.ReadAllText(output));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Input_hash_mismatch_and_missing_outage_identity_exit_as_input_errors()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gridsim-security-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string study = Path.Combine(directory, "study.json");
            File.WriteAllText(study, """{"schemaVersion":1,"case":{"builtIn":"case9","sha256":"bad"},"outages":[{"index":0,"from":1}],"voltage":{"minPu":0.9,"maxPu":1.1},"postFaultRating":"A"}""");
            var malformed = CliProcess.RunBuilt("transmission-security", study);
            Assert.Equal(1, malformed.ExitCode);
            Assert.Contains("Every outage needs integer", malformed.StdErr);

            File.WriteAllText(study, """{"schemaVersion":1,"case":{"builtIn":"case9","sha256":"bad"},"outages":[{"index":0,"from":1,"to":4}],"voltage":{"minPu":0.9,"maxPu":1.1},"postFaultRating":"A"}""");
            var mismatch = CliProcess.RunBuilt("transmission-security", study);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch", mismatch.StdErr);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Help_and_foreign_flags_have_explicit_outcomes()
    {
        var help = CliProcess.RunBuilt("transmission-security", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("postFaultRating", help.StdOut);
        var invalid = CliProcess.RunBuilt("transmission-security", "missing.json", "--csv", "x.csv");
        Assert.Equal(2, invalid.ExitCode);
    }
}
