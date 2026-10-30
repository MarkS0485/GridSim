using System;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Locks the <c>gridsim-harmonic-export/1</c> wire contract - every field the crossval sidecar reads. A
/// rename or a dropped block fails CI here with no Python, the same guard as EstimateExportContractTests.
/// </summary>
public class HarmonicExportContractTests
{
    [Fact]
    public void Export_locks_the_harmonic_export_contract()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11 } };
        var sources = new HarmonicSourceSet
        {
            Case = "case9",
            Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", model.Buses[4].Id, 0.8) },
        };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, sources.Sources, opt);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, idx.WorstBusId, opt);
        var compliance = HarmonicCompliance.AssessIeee519(model, pen);
        var meta = new SolveExportMeta("case9", "harmonics", "abc123", DateTime.UtcNow.ToString("o"), 0.0, "host");

        string json = HarmonicExport.ToJson(model, sources, pen, idx, meta, scan, compliance);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("gridsim-harmonic-export/1", root.GetProperty("schema").GetString());
        Assert.Equal("CuratedPlaceholder", root.GetProperty("provenance").GetString());
        Assert.True(root.GetProperty("orders").GetArrayLength() == 3);

        JsonElement v0 = root.GetProperty("voltage").EnumerateArray().First();
        foreach (string f in new[] { "order", "bus", "vmPu", "vaDeg" }) Assert.True(v0.TryGetProperty(f, out _), f);

        JsonElement indices = root.GetProperty("indices");
        Assert.True(indices.TryGetProperty("worstThdPct", out _));
        JsonElement t0 = indices.GetProperty("thd").EnumerateArray().First();
        Assert.True(t0.TryGetProperty("bus", out _) && t0.TryGetProperty("thdPct", out _));

        JsonElement res = root.GetProperty("resonance");
        Assert.True(res.TryGetProperty("order", out _) && res.TryGetProperty("zMag", out _) && res.TryGetProperty("sharpness", out _));

        JsonElement c0 = root.GetProperty("compliance").EnumerateArray().First();
        Assert.True(c0.TryGetProperty("compliant", out _) && c0.TryGetProperty("thdLimitPct", out _));

        JsonElement s0 = root.GetProperty("sources").EnumerateArray().First();
        Assert.Equal("CuratedPlaceholder", s0.GetProperty("provenance").GetString());
    }
}
