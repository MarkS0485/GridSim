using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The GDA emit-contract extension consumed on the GridSim side: the frequency-dependent line fields
/// (length, conductor type, zero-sequence impedance) load additively through JsonGridLoader, and the harmonic
/// model uses them - a per-equipment skin factor and the distributed-parameter long-line correction.
/// </summary>
public class HarmonicNetworkDataTests
{
    private static Complex Cell(ComplexCsr y, int i, int j)
    {
        for (int k = y.RowPtr[i]; k < y.RowPtr[i + 1]; k++)
            if (y.ColIdx[k] == j) return new Complex(y.G[k], y.B[k]);
        return Complex.Zero;
    }

    private static GridModel TwoBus(double r, double x, double b, string? equipment = null)
        => new()
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Buses = new[] { new Bus(0, BusType.Slack), new Bus(1, BusType.Pq) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(0, 1, r, x, b, Equipment: equipment) },
        };

    [Fact]
    public void JsonGridLoader_reads_the_new_line_fields_additively()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-harmonic-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), "{ \"name\": \"t\", \"baseMva\": 100, \"frequencyHz\": 50 }");
        // Moved 2026-10 (Q13, authorised): R179 makes bus baseKv required, and this fixture relied on the silent
        // 400 kV default. Before: buses without baseKv. After: "baseKv": 400 written explicitly, the exact value the
        // default gave, so the model this test loads is unchanged. The test is about line fields, not buses.
        File.WriteAllText(Path.Combine(dir, "buses.json"),
            "[ { \"id\": 0, \"type\": \"slack\", \"baseKv\": 400 }, { \"id\": 1, \"type\": \"pq\", \"baseKv\": 400 } ]");
        File.WriteAllText(Path.Combine(dir, "transmission.json"),
            "[ { \"name\": \"L1\", \"from\": 0, \"to\": 1, \"rPu\": 0.01, \"xPu\": 0.1, \"bPu\": 0.2, " +
            "\"ratingMva\": 100, \"circuits\": 1, \"lengthKm\": 12.5, \"equipment\": \"cable\", " +
            "\"rPu0\": 0.03, \"xPu0\": 0.3 } ]");

        GridModel model = JsonGridLoader.LoadDirectory(dir, dispatch: false);
        Branch br = model.Branches.Single();
        Assert.Equal(12.5, br.LengthKm, 9);
        Assert.Equal("cable", br.Equipment);
        Assert.Equal(0.03, br.R0, 9);
        Assert.Equal(0.3, br.X0, 9);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Cable_and_ohl_get_different_skin_factors_and_different_Yh()
    {
        var opt = new HarmonicModelOptions { SkinCoefficient = 0.1, CableSkinCoefficient = 0.6 };
        Assert.True(opt.SkinFactor(25, "cable") > opt.SkinFactor(25, "ohl"), "cable skin is stronger than OHL");
        Assert.Equal(opt.SkinFactor(25, null), opt.SkinFactor(25, "ohl"), 12); // unknown falls back to OHL coeff

        ComplexCsr yCable = FrequencyDependentYBus.Build(TwoBus(0.02, 0.1, 0.0, "cable"), 25, opt);
        ComplexCsr yOhl = FrequencyDependentYBus.Build(TwoBus(0.02, 0.1, 0.0, "ohl"), 25, opt);
        Assert.True((Cell(yCable, 0, 1) - Cell(yOhl, 0, 1)).Magnitude > 1e-6, "cable and OHL Y(h) differ");
    }

    [Fact]
    public void Long_line_correction_is_identity_for_a_short_line_and_bites_for_a_long_one()
    {
        var plain = new HarmonicModelOptions();
        var corrected = new HarmonicModelOptions { LongLineCorrection = true };

        // Short line (tiny charging): the correction is negligible (relative to the value).
        GridModel shortLine = TwoBus(0.001, 0.02, 1e-4);
        Complex zSp = Cell(FrequencyDependentYBus.Build(shortLine, 25, plain), 0, 0);
        Complex zSc = Cell(FrequencyDependentYBus.Build(shortLine, 25, corrected), 0, 0);
        double shortRel = (zSp - zSc).Magnitude / zSp.Magnitude;
        Assert.True(shortRel < 0.02, $"short-line correction ~ identity ({shortRel:P2})");

        // Long line (large charging x reactance): the hyperbolic correction bites much harder.
        GridModel longLine = TwoBus(0.01, 0.2, 1.0);
        Complex zLp = Cell(FrequencyDependentYBus.Build(longLine, 25, plain), 0, 0);
        Complex zLc = Cell(FrequencyDependentYBus.Build(longLine, 25, corrected), 0, 0);
        double longRel = (zLp - zLc).Magnitude / zLp.Magnitude;
        Assert.True(longRel > 5.0 * shortRel, $"long-line correction ({longRel:P2}) dwarfs the short-line one ({shortRel:P2})");
    }
}
