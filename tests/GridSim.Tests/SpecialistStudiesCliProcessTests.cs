using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC3 P8: the emt-line, arc-flash and cable-rating verbs through the real CLI entry point.</summary>
public sealed class SpecialistStudiesCliProcessTests
{
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = TestTemp.Under("gridsim-p8-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public string Write(string name, object study)
        {
            string p = File(name);
            System.IO.File.WriteAllText(p, JsonSerializer.Serialize(study));
            return p;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static object EmtStudy(object? termination = null, double dt = 1e-6) => new
    {
        schemaVersion = 1,
        label = "30 km distortionless line",
        line = new { lengthM = 30000.0, rOhmPerM = 1e-3, lHenryPerM = 1e-6, cFaradPerM = 1.0 / 9e10, gSiemensPerM = 1e-3 / 9e4 },
        segments = 2,
        source = new { kind = "Step", amplitudeV = 1000.0, matched = true },
        termination = termination ?? new { kind = "Open" },
        integration = new { dtSeconds = dt, durationSeconds = 400e-6 },
        refinement = new { timestepDivisors = new[] { 1, 2 }, segmentCounts = new[] { 1, 2 } },
        output = new { sampleEvery = 10 },
    };

    private static object ArcStudy(double voc = 4.16) => new
    {
        schemaVersion = 1,
        label = "4.16 kV switchgear (IEEE 1584-2018 Annex D.1 inputs)",
        equipment = new
        {
            systemVoltageKv = voc, configuration = "VCB", gapMm = 104.0, workingDistanceMm = 914.4, frequencyHz = 60.0, phases = 3,
            enclosure = new { heightMm = 1143.0, widthMm = 762.0, depthMm = 508.0 },
        },
        boltedFault = new { ka = 15.0, source = "Annex D.1 input" },
        clearingTime = new { fullArcingCurrentMs = 197.0, reducedArcingCurrentMs = 223.0, source = "Annex D.1 input" },
    };

    private static object CableStudy(string selection = "None") => new
    {
        schemaVersion = 1,
        label = "1x400 Cu XLPE 18/30 kV, direct buried",
        cable = new
        {
            material = "Copper", dcResistance20OhmPerKm = 0.0470, skinEffectKs = 1.0, conductorDiameterMm = 23.3, insulationDiameterMm = 42.5,
            screenDiameterMm = 44.5, overallDiameterMm = 50.5, insulationThermalResistivity = 3.5, oversheathThermalResistivity = 3.5,
            relativePermittivity = 2.5, lossTangent = 0.004, maxConductorTemperatureC = 90.0,
        },
        system = new { u0Kv = 18.0, frequencyHz = 50.0 },
        installation = new { depthToAxisM = 0.8, soilThermalResistivity = 1.0, ambientGroundTemperatureC = 20.0 },
        screenLoss = new { lambda1 = 0.0, source = "single-point bonded, declared" },
        nameplate = new { amperes = 950.0, source = "datasheet, declared" },
        selection,
    };

    [Fact]
    public void Emt_line_is_repeatable_writes_once_and_reports_the_analytic_comparison()
    {
        using var f = new Folder();
        string study = f.Write("emt.json", EmtStudy());
        string output = f.File("emt-result.json");
        var first = CliProcess.RunBuilt("emt-line", study, "--out", output);
        Assert.True(first.ExitCode == 0, first.ToString());
        var second = CliProcess.RunBuilt("emt-line", study);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);
        Assert.Equal(first.StdOut.TrimEnd(), File.ReadAllText(output).TrimEnd());
        using JsonDocument doc = JsonDocument.Parse(first.StdOut);
        JsonElement res = doc.RootElement.GetProperty("result");
        Assert.Equal("EXACT", res.GetProperty("oracle").GetString());
        Assert.Equal("EVALUATED", res.GetProperty("status").GetString());
        Assert.Equal(300.0, res.GetProperty("derived").GetProperty("surgeImpedanceOhm").GetDouble(), 9);
        Assert.Equal(1e-4, res.GetProperty("base").GetProperty("measures").GetProperty("measuredDelaySeconds").GetDouble(), 15);
        Assert.Equal(4, res.GetProperty("refinement").GetArrayLength());
        Assert.StartsWith("unverified", doc.RootElement.GetProperty("comparator").GetString(), StringComparison.Ordinal);

        Assert.Equal(1, CliProcess.RunBuilt("emt-line", study, "--out", output).ExitCode);   // never overwrites
    }

    [Fact]
    public void Emt_line_refuses_bad_studies()
    {
        using var f = new Folder();
        var rounded = CliProcess.RunBuilt("emt-line", f.Write("a.json", EmtStudy(dt: 3e-6)));
        Assert.Equal(1, rounded.ExitCode);
        Assert.Contains("whole number", rounded.StdErr, StringComparison.Ordinal);
        string text = JsonSerializer.Serialize(EmtStudy()).Replace("\"segments\":2", "\"segments\":2,\"extra\":1", StringComparison.Ordinal);
        File.WriteAllText(f.File("b.json"), text);
        Assert.Equal(1, CliProcess.RunBuilt("emt-line", f.File("b.json")).ExitCode);
        string dup = JsonSerializer.Serialize(EmtStudy()).Replace("\"segments\":2", "\"segments\":2,\"segments\":3", StringComparison.Ordinal);
        File.WriteAllText(f.File("c.json"), dup);
        var d = CliProcess.RunBuilt("emt-line", f.File("c.json"));
        Assert.Equal(1, d.ExitCode);
        Assert.Contains("Duplicate property", d.StdErr, StringComparison.Ordinal);
        Assert.Equal(2, CliProcess.RunBuilt("emt-line").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("emt-line", f.File("a.json"), "--bogus", "x").ExitCode);
    }

    [Fact]
    public void Arc_flash_reports_the_annex_example_and_refuses_out_of_envelope_inputs()
    {
        using var f = new Folder();
        var r = CliProcess.RunBuilt("arc-flash", f.Write("af.json", ArcStudy()));
        Assert.True(r.ExitCode == 0, r.ToString());
        using (JsonDocument doc = JsonDocument.Parse(r.StdOut))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            Assert.Equal("EVALUATED", res.GetProperty("status").GetString());
            Assert.Equal("reduced", res.GetProperty("governing").GetString());
            Assert.InRange(res.GetProperty("incidentEnergyJPerCm2").GetDouble(), 13.3425, 13.3435);   // Annex D.1, D.62
            Assert.InRange(res.GetProperty("arcFlashBoundaryMm").GetDouble(), 1703.5, 1704.5);       // D.72
            Assert.Equal("kA", doc.RootElement.GetProperty("units").GetProperty("current").GetString());
            Assert.Equal("Annex D.1 input", res.GetProperty("inputs").GetProperty("clearingTime").GetProperty("source").GetString());
        }
        Assert.Equal(r.StdOut, CliProcess.RunBuilt("arc-flash", f.File("af.json")).StdOut);

        var oos = CliProcess.RunBuilt("arc-flash", f.Write("hv.json", ArcStudy(voc: 33.0)));
        Assert.Equal(3, oos.ExitCode);
        using (JsonDocument doc = JsonDocument.Parse(oos.StdOut))
            Assert.Equal("OUT OF SCOPE", doc.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public void Arc_flash_reads_iec60909_duty_bound_to_the_case_identity()
    {
        using var f = new Folder();
        object Study(string sha) => new
        {
            schemaVersion = 1,
            label = "case9 bus 5 (a 345 kV bus: refused, voltage mismatch)",
            equipment = new { systemVoltageKv = 4.16, configuration = "VOA", gapMm = 104.0, workingDistanceMm = 914.4, frequencyHz = 60.0, phases = 3 },
            boltedFault = new { iec60909 = new { @case = new { builtIn = "case9", sha256 = sha }, bus = 5, objective = "Maximum" } },
            clearingTime = new { fullArcingCurrentMs = 100.0, reducedArcingCurrentMs = 100.0, source = "declared" },
        };
        var mismatch = CliProcess.RunBuilt("arc-flash", f.Write("a.json", Study(new string('0', 64))));
        Assert.Equal(1, mismatch.ExitCode);
        Assert.Contains("Case SHA256 mismatch. Resolved identity: ", mismatch.StdErr, StringComparison.Ordinal);
        string identity = mismatch.StdErr.Split("Resolved identity: ")[1].Trim();
        var voltage = CliProcess.RunBuilt("arc-flash", f.Write("b.json", Study(identity)));
        Assert.Equal(1, voltage.ExitCode);
        Assert.Contains("does not match the equipment voltage", voltage.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cable_rating_keeps_nameplate_separate_and_selects_only_when_asked()
    {
        using var f = new Folder();
        var none = CliProcess.RunBuilt("cable-rating", f.Write("c.json", CableStudy()));
        Assert.True(none.ExitCode == 0, none.ToString());
        using (JsonDocument doc = JsonDocument.Parse(none.StdOut))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            Assert.Equal(JsonValueKind.Null, res.GetProperty("selectedRatingA").ValueKind);
            Assert.Equal(950.0, res.GetProperty("nameplate").GetProperty("amperes").GetDouble());
            Assert.True(res.GetProperty("calculatedRatingA").GetDouble() > 900);
        }
        var lower = CliProcess.RunBuilt("cable-rating", f.Write("d.json", CableStudy("Lower")));
        Assert.Equal(0, lower.ExitCode);
        using (JsonDocument doc = JsonDocument.Parse(lower.StdOut))
            Assert.Equal(950.0, doc.RootElement.GetProperty("result").GetProperty("selectedRatingA").GetDouble());
        Assert.Equal(1, CliProcess.RunBuilt("cable-rating", f.Write("e.json", CableStudy("Highest"))).ExitCode);
    }

    [Theory]
    [InlineData("emt-line")]
    [InlineData("arc-flash")]
    [InlineData("cable-rating")]
    public void Each_verb_has_its_own_help(string verb)
    {
        var h = CliProcess.RunBuilt(verb, "--help");
        Assert.Equal(0, h.ExitCode);
        Assert.Contains($"gridsim {verb} <study.json> [--out <result.json>]", h.StdOut, StringComparison.Ordinal);
    }
}
