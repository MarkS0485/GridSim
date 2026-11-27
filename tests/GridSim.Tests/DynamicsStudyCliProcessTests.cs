using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>RC3 P6: the dynamics-study verb through the real CLI entry point.</summary>
public sealed class DynamicsStudyCliProcessTests
{
    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = TestTemp.Under("gridsim-dynamics-study-" + Guid.NewGuid().ToString("N"));
        public Folder()
        {
            Directory.CreateDirectory(Path);
            System.IO.File.Copy(Psse("case14.raw"), System.IO.Path.Combine(Path, "case14.raw"));
            System.IO.File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), System.IO.Path.Combine(Path, "case14.dyr"));
        }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static object Study(Folder f, string unmodelled = "refuse", object[]? cases = null, string? rawSha = null) => new
    {
        schemaVersion = 1,
        raw = new { path = "case14.raw", sha256 = rawSha ?? Sha(f.File("case14.raw")) },
        dyr = new { path = "case14.dyr", sha256 = Sha(f.File("case14.dyr")) },
        initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled },
        integration = new { stepSeconds = 0.005, durationSeconds = 3.15 },
        criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
        angleReference = new { bus = 1, id = "1" },
        channels = new object[]
        {
            new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } },
            new { kind = "frequency", machine = new { bus = 2, id = "1" } },
            new { kind = "busVoltage", bus = 4 },
        },
        cases = cases ?? new object[]
        {
            new
            {
                name = "fault-4", kind = "events", events = new object[]
                {
                    new { kind = "busFault", at = 0.1, bus = 4 },
                    new { kind = "faultClear", at = 0.15, bus = 4 },
                    new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                },
            },
        },
        execution = new { maxParallelism = 2 },
    };

    private static string Write(Folder f, object study, string name = "study.json")
    {
        string path = f.File(name);
        File.WriteAllText(path, JsonSerializer.Serialize(study));
        return path;
    }

    [Fact]
    public void Real_entry_point_reports_coverage_first_is_repeatable_and_never_overwrites()
    {
        using var f = new Folder();
        string study = Write(f, Study(f));
        string output = f.File("result.json");
        var first = CliProcess.RunBuilt("dynamics-study", study, "--out", output);
        Assert.True(first.ExitCode == 0, first.StdErr + first.StdOut);
        var second = CliProcess.RunBuilt("dynamics-study", study);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);
        Assert.Equal(first.StdOut.TrimEnd(), File.ReadAllText(output).TrimEnd());

        using JsonDocument doc = JsonDocument.Parse(first.StdOut);
        JsonElement root = doc.RootElement;
        var order = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(order.IndexOf("coverage") < order.IndexOf("status"));
        Assert.True(order.IndexOf("coverage") < order.IndexOf("cases"));
        Assert.Equal("COMPLETE", root.GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal("STABLE", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("likeForLike").GetBoolean());
        Assert.Equal(Sha(f.File("case14.raw")), root.GetProperty("provenance").GetProperty("inputs")[0].GetProperty("sha256").GetString());
        JsonElement row = root.GetProperty("cases")[0];
        Assert.Equal("STABLE", row.GetProperty("disposition").GetString());
        Assert.Equal(631, row.GetProperty("run").GetProperty("samples").GetInt32());

        var refusal = CliProcess.RunBuilt("dynamics-study", study, "--out", output);
        Assert.Equal(1, refusal.ExitCode);
    }

    [Fact]
    public void The_legacy_rms_event_verb_gives_the_same_verdict_on_the_same_inputs_and_is_unchanged()
    {
        using var f = new Folder();
        var legacy = CliProcess.RunBuilt("stability", f.File("case14.raw"), "--rms", "--dyn-data", f.File("case14.dyr"),
            "--events", "fault:4@0.1,clear:4@0.15,trip:6@0.15");
        Assert.True(legacy.ExitCode == 0, legacy.StdErr + legacy.StdOut);
        Assert.Contains("STABLE (the connected machines stay in synchronism)", legacy.StdOut, StringComparison.Ordinal);
        // The reader fixture psse/case14.dyr cannot start at the solved point (TGOV1 VMAX); the legacy verb's exit is unchanged.
        var original = CliProcess.RunBuilt("stability", f.File("case14.raw"), "--rms", "--dyn-data", Psse("case14.dyr"),
            "--events", "fault:4@0.1,clear:4@0.15,trip:6@0.15");
        Assert.Equal(2, original.ExitCode);
        Assert.Contains("TGOV1 governor cannot start at the solved operating point", original.StdErr, StringComparison.Ordinal);
        var study = CliProcess.RunBuilt("dynamics-study", Write(f, Study(f)));
        using JsonDocument doc = JsonDocument.Parse(study.StdOut);
        Assert.Equal("STABLE", doc.RootElement.GetProperty("cases")[0].GetProperty("disposition").GetString());
    }

    [Fact]
    public void An_unsupported_record_gives_a_blocked_coverage_report_and_exit_3()
    {
        using var f = new Folder();
        File.AppendAllText(f.File("case14.dyr"), "     6 'ESST1A' 1  0.0 1 2 3 /\r\n");
        var r = CliProcess.RunBuilt("dynamics-study", Write(f, Study(f)));
        Assert.True(r.ExitCode == 3, r.StdErr + r.StdOut);
        using JsonDocument doc = JsonDocument.Parse(r.StdOut);
        Assert.Equal("BLOCKED", doc.RootElement.GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal("INCOMPLETE", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("NOT RUN", doc.RootElement.GetProperty("cases")[0].GetProperty("disposition").GetString());
        JsonElement rec = doc.RootElement.GetProperty("coverage").GetProperty("records").EnumerateArray().Single(x => x.GetProperty("model").GetString() == "ESST1A");
        Assert.Equal("SKIPPED", rec.GetProperty("status").GetString());

        // The existing --dyn-data refusal path is unchanged: exit 1, naming the model.
        var legacy = CliProcess.RunBuilt("stability", f.File("case14.raw"), "--rms", "--dyn-data", f.File("case14.dyr"));
        Assert.Equal(1, legacy.ExitCode);
        Assert.Contains("models this reader does not map: ESST1A x1", legacy.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_inputs_have_explicit_exit_codes()
    {
        using var f = new Folder();
        var mismatch = CliProcess.RunBuilt("dynamics-study", Write(f, Study(f, rawSha: new string('0', 64))));
        Assert.Equal(1, mismatch.ExitCode);
        Assert.Contains("raw SHA256 mismatch. Resolved identity: " + Sha(f.File("case14.raw")), mismatch.StdErr, StringComparison.Ordinal);

        var unsupported = CliProcess.RunBuilt("dynamics-study", Write(f, Study(f, cases: new object[]
        {
            new { name = "slg", kind = "events", events = new object[] { new { kind = "singlePhaseFault", at = 0.1, bus = 4 } } },
        })));
        Assert.Equal(1, unsupported.ExitCode);
        Assert.Contains("unsupported event kind 'singlePhaseFault'", unsupported.StdErr, StringComparison.Ordinal);

        string unknownField = f.File("unknown.json");
        File.WriteAllText(unknownField, JsonSerializer.Serialize(Study(f)).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":true", StringComparison.Ordinal));
        var unknown = CliProcess.RunBuilt("dynamics-study", unknownField);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("unknown field 'extra'", unknown.StdErr, StringComparison.Ordinal);

        var usage = CliProcess.RunBuilt("dynamics-study");
        Assert.Equal(2, usage.ExitCode);
        var inherited = CliProcess.RunBuilt("dynamics-study", Write(f, Study(f)), "--allow-unmodelled");
        Assert.Equal(2, inherited.ExitCode);
    }

    [Fact]
    public void Help_names_the_verb_and_its_flag()
    {
        var help = CliProcess.RunBuilt("dynamics-study", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("gridsim dynamics-study <study.json> [--out <result.json>]", help.StdOut, StringComparison.Ordinal);
        var all = CliProcess.RunBuilt("--help");
        Assert.Contains("dynamics-study", all.StdOut, StringComparison.Ordinal);
    }
}
