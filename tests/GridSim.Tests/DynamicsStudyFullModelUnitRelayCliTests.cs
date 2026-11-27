using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 stage 6: <c>dynamics-study --unit-protection</c> also re-runs each events row on the full-model engine with the
/// units' relays acting; the result's <c>unitProtection</c> gains <c>fullModel</c> and every other member is as before.
/// </summary>
public sealed class DynamicsStudyFullModelUnitRelayCliTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-unitprot-fm-" + Guid.NewGuid().ToString("N"));

    public DynamicsStudyFullModelUnitRelayCliTests()
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

    private string Write(string name)
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
            ["cases"] = new object[]
            {
                new { name = "c", kind = "events", events = new object[] { new { kind = "loadStep", at = 0.3, bus = 5, pMw = 30.0, qMvar = 5.0 } } },
                new { name = "k", kind = "cct", cct = new { faultBus = 4, onsetSeconds = 0.1, maxClearingSeconds = 0.2, clearingTrips = Array.Empty<object>() } },
            },
            ["adaptive"] = new { relativeTolerance = 1e-8, absoluteTolerance = 1e-10, maxStepSeconds = 0.02, eventToleranceSeconds = 1e-7 },
        };
        File.WriteAllText(P(name), JsonSerializer.Serialize(d));
        return P(name);
    }

    [Fact]
    public void Unit_protection_adds_the_full_model_member_and_changes_no_other_member()
    {
        string study = Write("study.json");
        CliProcess.Result a = CliProcess.RunBuilt("dynamics-study", study);
        CliProcess.Result b = CliProcess.RunBuilt("dynamics-study", study, "--unit-protection", P("units.json"));
        Assert.True(a.ExitCode == 0, a.StdErr + a.StdOut);
        Assert.True(b.ExitCode == 0, b.StdErr + b.StdOut);
        using JsonDocument ja = JsonDocument.Parse(a.StdOut), jb = JsonDocument.Parse(b.StdOut);
        foreach (string m in new[] { "coverage", "engine", "initialisation", "cases", "counts", "status", "likeForLike", "reasons" })
            Assert.Equal(ja.RootElement.GetProperty(m).GetRawText(), jb.RootElement.GetProperty(m).GetRawText());

        JsonElement fm = jb.RootElement.GetProperty("unitProtection").GetProperty("fullModel");
        Assert.Contains("full model", fm.GetProperty("engine").GetString(), StringComparison.Ordinal);
        Assert.Contains("40 on the apparent impedance", fm.GetProperty("rule").GetString(), StringComparison.Ordinal);
        JsonElement rows = fm.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("EVALUATED", rows[0].GetProperty("disposition").GetString());
        Assert.Equal("NOT RUN", rows[1].GetProperty("disposition").GetString());
        // The fixture's unit 2:1 underfrequency stage (59.99 Hz, 0.2 s) operates on the full model too.
        JsonElement trip = Assert.Single(fm.GetProperty("trips").EnumerateArray());
        Assert.Equal("2:1", trip.GetProperty("unit").GetString());
        Assert.Equal("81U-1", trip.GetProperty("device").GetString());
        Assert.Equal(trip.GetProperty("operateSeconds").GetDouble() + 0.005, trip.GetProperty("disconnectedSeconds").GetDouble(), 12);
        // The adaptive list stays where it was.
        Assert.Equal("81U-1", Assert.Single(jb.RootElement.GetProperty("unitProtection").GetProperty("trips").EnumerateArray()).GetProperty("device").GetString());
    }
}
