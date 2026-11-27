using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5: dynamics-study with unit protection (opt-in): <c>--unit-protection &lt;file&gt;</c> or <c>adaptive.unitProtection</c>
/// in the study file puts the units' own relays, named by their PSS/E machine keys, in the variable-step companion run.
/// The result gains the members <c>unitProtection</c> (the file's identity, its units and every unit trip) and
/// <c>effectiveOptions.adaptive.unitProtection</c>; every other member is as without it. Unknown units, a file given twice,
/// and a hash mismatch are refused. Without an adaptive member, <c>--unit-protection</c> runs the units' relays in the
/// full-model companion run alone (<c>unitProtection.fullModel</c>, <c>effectiveOptions.unitProtection</c>).
/// </summary>
public sealed class DynamicsStudyUnitProtectionCliTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-unitprot-cli-" + Guid.NewGuid().ToString("N"));

    public DynamicsStudyUnitProtectionCliTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(Psse("case14.raw"), Path.Combine(_dir, "case14.raw"));
        File.Copy(Study("case14-study.dyr"), Path.Combine(_dir, "case14.dyr"));
        File.Copy(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "unit-protection", "case14-units.json"), Path.Combine(_dir, "units.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private string Write(string name, object? adaptive)
    {
        var d = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["raw"] = new { path = "case14.raw", sha256 = Sha(P("case14.raw")) },
            ["dyr"] = new { path = "case14.dyr", sha256 = Sha(P("case14.dyr")) },
            ["initialisation"] = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            ["integration"] = new { stepSeconds = 0.005, durationSeconds = 2.0 },
            ["criteria"] = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            ["angleReference"] = new { bus = 1, id = "1" },
            ["channels"] = new object[] { new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } } },
            ["cases"] = new object[] { new { name = "c", kind = "events", events = new object[] { new { kind = "loadStep", at = 0.3, bus = 5, pMw = 30.0, qMvar = 5.0 } } } },
        };
        if (adaptive is not null) d["adaptive"] = adaptive;
        File.WriteAllText(P(name), JsonSerializer.Serialize(d));
        return P(name);
    }

    private static readonly object Tolerances = new { relativeTolerance = 1e-8, absoluteTolerance = 1e-10, maxStepSeconds = 0.02, eventToleranceSeconds = 1e-7 };

    [Fact]
    public void The_flag_and_the_study_member_attach_unit_protection_and_report_each_unit_trip_additively()
    {
        string plain = Write("plain.json", Tolerances);
        string member = Write("member.json", new
        {
            relativeTolerance = 1e-8, absoluteTolerance = 1e-10, maxStepSeconds = 0.02, eventToleranceSeconds = 1e-7,
            unitProtection = new { path = "units.json", sha256 = Sha(P("units.json")) },
        });
        CliProcess.Result a = CliProcess.RunBuilt("dynamics-study", plain);
        CliProcess.Result b = CliProcess.RunBuilt("dynamics-study", plain, "--unit-protection", P("units.json"));
        CliProcess.Result c = CliProcess.RunBuilt("dynamics-study", member);
        Assert.True(a.ExitCode == 0, a.StdErr + a.StdOut);
        Assert.True(b.ExitCode == 0, b.StdErr + b.StdOut);
        Assert.True(c.ExitCode == 0, c.StdErr + c.StdOut);
        using JsonDocument ja = JsonDocument.Parse(a.StdOut), jb = JsonDocument.Parse(b.StdOut), jc = JsonDocument.Parse(c.StdOut);
        Assert.False(ja.RootElement.TryGetProperty("unitProtection", out _));

        JsonElement up = jb.RootElement.GetProperty("unitProtection");
        Assert.Equal("--unit-protection", up.GetProperty("source").GetString());
        Assert.Equal("units.json", up.GetProperty("file").GetString());
        Assert.Equal(Sha(P("units.json")), up.GetProperty("sha256").GetString());
        Assert.Equal("gridsim-unit-protection/1", up.GetProperty("schema").GetString());
        Assert.Equal(new[] { "1:1", "2:1" }, up.GetProperty("units").EnumerateArray().Select(x => x.GetString()));
        JsonElement trip = Assert.Single(up.GetProperty("trips").EnumerateArray());
        Assert.Equal("2:1", trip.GetProperty("unit").GetString());
        Assert.Equal("81U-1", trip.GetProperty("device").GetString());
        Assert.Equal("c", trip.GetProperty("case").GetString());
        Assert.Equal(2, jb.RootElement.GetProperty("effectiveOptions").GetProperty("adaptive").GetProperty("unitProtection").GetProperty("units").GetInt32());
        Assert.Contains(jb.RootElement.GetProperty("adaptive").GetProperty("rows")[0].GetProperty("unitRelays").EnumerateArray(),
            r => r.GetProperty("unit").GetString() == "2:1" && r.GetProperty("tripped").GetBoolean());

        // Additive: the fixed-step members are the plain run's, byte for byte.
        foreach (string m in new[] { "coverage", "engine", "initialisation", "cases", "counts", "status", "likeForLike", "reasons" })
            Assert.Equal(ja.RootElement.GetProperty(m).GetRawText(), jb.RootElement.GetProperty(m).GetRawText());

        // The study member gives the same run (its source and the study hash differ).
        Assert.Equal("adaptive.unitProtection", jc.RootElement.GetProperty("unitProtection").GetProperty("source").GetString());
        Assert.Equal(jb.RootElement.GetProperty("adaptive").GetRawText(), jc.RootElement.GetProperty("adaptive").GetRawText());
        Assert.Equal(up.GetProperty("trips").GetRawText(), jc.RootElement.GetProperty("unitProtection").GetProperty("trips").GetRawText());
    }

    [Fact]
    public void Unknown_units_a_second_file_and_a_hash_mismatch_are_refused_and_without_an_adaptive_run_the_full_model_runs_them()
    {
        string plain = Write("plain.json", Tolerances);
        File.WriteAllText(P("unknown.json"), File.ReadAllText(P("units.json")).Replace("\"2:1\"", "\"99:1\"", StringComparison.Ordinal));
        CliProcess.Result r = CliProcess.RunBuilt("dynamics-study", plain, "--unit-protection", P("unknown.json"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("unit '99:1' is not a machine of the RAW case", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("<bus>:<id>", r.StdErr, StringComparison.Ordinal);
        Assert.Empty(r.StdOut);

        string member = Write("member.json", new { unitProtection = new { path = "units.json", sha256 = Sha(P("units.json")) } });
        r = CliProcess.RunBuilt("dynamics-study", member, "--unit-protection", P("units.json"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("give it once", r.StdErr, StringComparison.Ordinal);

        string wrong = Write("wrong.json", new { unitProtection = new { path = "units.json", sha256 = new string('0', 64) } });
        r = CliProcess.RunBuilt("dynamics-study", wrong);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("adaptive.unitProtection SHA256 mismatch", r.StdErr, StringComparison.Ordinal);

        // Without the adaptive member the units' relays act in the full-model companion run alone (approved 2026-11-21:
        // this input was refused before).
        string noAdaptive = Write("none.json", null);
        r = CliProcess.RunBuilt("dynamics-study", noAdaptive, "--unit-protection", P("units.json"));
        Assert.True(r.ExitCode == 0, r.StdErr + r.StdOut);
        using (JsonDocument jn = JsonDocument.Parse(r.StdOut))
        {
            Assert.False(jn.RootElement.TryGetProperty("adaptive", out _));
            JsonElement fm = jn.RootElement.GetProperty("unitProtection").GetProperty("fullModel");
            Assert.Equal("EVALUATED", Assert.Single(fm.GetProperty("rows").EnumerateArray()).GetProperty("disposition").GetString());
            Assert.Equal("81U-1", Assert.Single(fm.GetProperty("trips").EnumerateArray()).GetProperty("device").GetString());
        }

        r = CliProcess.RunBuilt("dynamics-study", plain, "--unit-protection", P("units.json"), "--unit-protection", P("units.json"));
        Assert.Equal(2, r.ExitCode);
        r = CliProcess.RunBuilt("dynamics-study", plain, "--unit-protection");
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void The_flag_with_out_writes_the_same_result_file()
    {
        string plain = Write("plain.json", Tolerances);
        CliProcess.Result r = CliProcess.RunBuilt("dynamics-study", plain, "--out", P("result.json"), "--unit-protection", P("units.json"));
        Assert.True(r.ExitCode == 0, r.StdErr);
        Assert.Equal(r.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal), File.ReadAllText(P("result.json")).Replace("\r\n", "\n", StringComparison.Ordinal));
        using JsonDocument j = JsonDocument.Parse(File.ReadAllText(P("result.json")));
        Assert.True(j.RootElement.TryGetProperty("unitProtection", out _));
    }
    [Fact]
    public void Unit_protection_without_an_adaptive_run_is_deterministic_and_matches_the_full_model_member_of_the_adaptive_study()
    {
        string none = Write("none.json", null);
        string withAdaptive = Write("plain.json", Tolerances);
        CliProcess.Result a = CliProcess.RunBuilt("dynamics-study", none);
        CliProcess.Result b1 = CliProcess.RunBuilt("dynamics-study", none, "--unit-protection", P("units.json"));
        CliProcess.Result b2 = CliProcess.RunBuilt("dynamics-study", none, "--unit-protection", P("units.json"));
        CliProcess.Result c = CliProcess.RunBuilt("dynamics-study", withAdaptive, "--unit-protection", P("units.json"));
        Assert.True(a.ExitCode == 0, a.StdErr + a.StdOut);
        Assert.True(b1.ExitCode == 0, b1.StdErr + b1.StdOut);
        Assert.True(c.ExitCode == 0, c.StdErr + c.StdOut);

        // Same input, same bits.
        Assert.Equal(b1.ExitCode, b2.ExitCode);
        Assert.Equal(b1.StdOut, b2.StdOut);
        Assert.Equal(b1.StdErr, b2.StdErr);

        using JsonDocument ja = JsonDocument.Parse(a.StdOut), jb = JsonDocument.Parse(b1.StdOut), jc = JsonDocument.Parse(c.StdOut);
        Assert.False(ja.RootElement.TryGetProperty("unitProtection", out _));
        Assert.False(jb.RootElement.TryGetProperty("adaptive", out _));
        // Additive: the fixed-step members are the plain run's, byte for byte.
        foreach (string m in new[] { "provenance", "coverage", "engine", "initialisation", "cases", "counts", "status", "likeForLike", "reasons" })
            Assert.Equal(ja.RootElement.GetProperty(m).GetRawText(), jb.RootElement.GetProperty(m).GetRawText());
        JsonElement up = jb.RootElement.GetProperty("unitProtection");
        Assert.Equal("--unit-protection", up.GetProperty("source").GetString());
        Assert.Equal(Sha(P("units.json")), up.GetProperty("sha256").GetString());
        Assert.Equal(new[] { "1:1", "2:1" }, up.GetProperty("units").EnumerateArray().Select(x => x.GetString()));
        Assert.False(up.TryGetProperty("trips", out _));   // the adaptive trip list: no adaptive run was made
        Assert.Equal(2, jb.RootElement.GetProperty("effectiveOptions").GetProperty("unitProtection").GetProperty("units").GetInt32());
        // The full-model run does not depend on the adaptive run: the same member, byte for byte.
        Assert.Equal(jc.RootElement.GetProperty("unitProtection").GetProperty("fullModel").GetRawText(), up.GetProperty("fullModel").GetRawText());
    }
}
