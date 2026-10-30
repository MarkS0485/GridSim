using System;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Locks the <c>gridsim-impedance-stability/1</c> wire contract - every field the crossval sidecar reads. A
/// rename or a dropped block fails CI here with no Python, the same guard as
/// <see cref="HarmonicExportContractTests"/>.
/// </summary>
public class ImpedanceExportContractTests
{
    [Fact]
    public void Export_locks_the_impedance_stability_contract()
    {
        var op = new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, 0.2);
        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, 20.0, 0.707, 0.0);
        var opt = new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 200, PointsPerDecade = 20, F0Hz = 50, Criterion = StabilityCriterion.Siso,
            UseCanonicalThevenin = true, Scr = 1.0, XOverR = 10, PRatedPu = 1.0,
            ScrMin = 0.2, ScrMax = 6.0, ScrSteps = 20,
        };
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, conv).WithModalCrossCheck(0);

        GridModel model = GridImpedanceAssembler.CanonicalThevenin(1.0, 10, 1.0, 100);
        var meta = new SolveExportMeta("canonical", "impedance-stability", "abc123", DateTime.UtcNow.ToString("o"), 0.0, "host");
        string json = ImpedanceStabilityExport.ToJson(model, r, meta);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("gridsim-impedance-stability/1", root.GetProperty("schema").GetString());
        Assert.Equal("CuratedPlaceholder", root.GetProperty("provenance").GetString());
        Assert.Equal("GridFollowingInverter", root.GetProperty("mode").GetString());
        Assert.Equal("Siso", root.GetProperty("criterion").GetString());
        Assert.True(root.TryGetProperty("inverterBus", out _));

        JsonElement opj = root.GetProperty("operatingPoint");
        foreach (string f in new[] { "vd0Pu", "vq0Pu", "id0Pu", "iq0Pu", "pPu", "qPu", "vd0Clamped" })
            Assert.True(opj.TryGetProperty(f, out _), f);

        JsonElement ny = root.GetProperty("nyquist");
        foreach (string f in new[] { "encirclements", "unstableModeCount", "stable", "gainMarginDb", "phaseMarginDeg", "criticalFreqHz", "minDistToMinusOne" })
            Assert.True(ny.TryGetProperty(f, out _), f);

        JsonElement ml0 = root.GetProperty("minorLoop").EnumerateArray().First();
        foreach (string f in new[] { "fHz", "lRe", "lIm", "eig1Re", "eig1Im", "eig2Re", "eig2Im" })
            Assert.True(ml0.TryGetProperty(f, out _), f);

        JsonElement gi0 = root.GetProperty("gridImpedance").EnumerateArray().First();
        Assert.True(gi0.TryGetProperty("fHz", out _) && gi0.TryGetProperty("re", out _) && gi0.TryGetProperty("im", out _));

        JsonElement scr0 = root.GetProperty("scrSweep").EnumerateArray().First();
        foreach (string f in new[] { "scr", "encirclements", "stable", "minDistToMinusOne" })
            Assert.True(scr0.TryGetProperty(f, out _), f);

        JsonElement b = root.GetProperty("boundary");
        Assert.True(b.TryGetProperty("criticalScr", out _) && b.TryGetProperty("found", out _));

        JsonElement cv = root.GetProperty("crossval");
        Assert.True(cv.TryGetProperty("rhpEigenvalueCountModal", out _) && cv.TryGetProperty("agreement", out _));
    }
}
