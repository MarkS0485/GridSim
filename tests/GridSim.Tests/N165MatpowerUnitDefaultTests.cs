using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N165 (October 2026): <see cref="MatpowerParser"/> used to return the raw ohm / kW numbers of the published
/// distribution feeders (case22, case33bw, case69, case85) and relied on every caller to wrap the result in
/// <see cref="MatpowerUnitNormalizer.Normalize"/>. The safe path is now the default: the parser applies the same
/// normaliser itself, and <see cref="MatpowerUnitHandling.AsWritten"/> keeps the old numbers for a caller that asks.
/// These tests prove (1) the default equals the product paths' Normalize(raw) bit for bit, (2) every other bundled file
/// parses exactly as before, (3) the product composition Normalize(ParseFile(path)) is unchanged (Normalize is a no-op
/// on the parser's output), and (4) the refusals.
/// </summary>
public class N165MatpowerUnitDefaultTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>The four bundled feeders that ship in raw units (classified by LooksRawUnits; asserted below).</summary>
    public static IEnumerable<object[]> RawFeeders =>
        new[] { "dist/case22.m", "dist/case33bw.m", "dist/case69.m", "dist/case85.m" }.Select(f => new object[] { f });

    private static string PathOf(string rel)
    {
        string path = Path.Combine(DataDir, "matpower", rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled case file missing: {path}");   // never silently skip
        return path;
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>Every bus, branch and generator equal, with the numeric fields compared by their bits.</summary>
    private static void AssertSameModel(string label, GridModel a, GridModel b)
    {
        Assert.True(Bits(a.BaseMva) == Bits(b.BaseMva), $"{label}: base MVA");
        Assert.True(Bits(a.FrequencyHz) == Bits(b.FrequencyHz), $"{label}: frequency");
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Buses.Count, b.Buses.Count);
        for (int i = 0; i < a.Buses.Count; i++)
        {
            Bus x = a.Buses[i], y = b.Buses[i];
            Assert.True(x == y, $"{label}: bus {i} record");
            Assert.True(new[] { x.Pd, x.Qd, x.Gs, x.Bs, x.BaseKv, x.Vm, x.VaDeg }.Select(Bits)
                .SequenceEqual(new[] { y.Pd, y.Qd, y.Gs, y.Bs, y.BaseKv, y.Vm, y.VaDeg }.Select(Bits)), $"{label}: bus {x.Id} bits");
        }
        Assert.Equal(a.Branches.Count, b.Branches.Count);
        for (int i = 0; i < a.Branches.Count; i++)
        {
            Branch x = a.Branches[i], y = b.Branches[i];
            Assert.True(x == y, $"{label}: branch {i} record");
            Assert.True(new[] { x.R, x.X, x.B, x.TapRatio, x.PhaseShiftDeg, x.RateAMva }.Select(Bits)
                .SequenceEqual(new[] { y.R, y.X, y.B, y.TapRatio, y.PhaseShiftDeg, y.RateAMva }.Select(Bits)), $"{label}: branch {i} bits");
        }
        Assert.Equal(a.AllGenerators.Count, b.AllGenerators.Count);
        // Generators by their fields (record equality would compare each parse's own cost-curve objects by reference).
        for (int i = 0; i < a.AllGenerators.Count; i++)
        {
            Generator x = a.AllGenerators[i], y = b.AllGenerators[i];
            Assert.True(x.Bus == y.Bus && x.InService == y.InService, $"{label}: generator {i} bus/status");
            Assert.True(new[] { x.Pg, x.Qg, x.Vg, x.Qmin, x.Qmax, x.Pmin, x.Pmax, x.MBase }.Select(Bits)
                .SequenceEqual(new[] { y.Pg, y.Qg, y.Vg, y.Qmin, y.Qmax, y.Pmin, y.Pmax, y.MBase }.Select(Bits)), $"{label}: generator {i} numbers");
        }
    }

    [Theory]
    [MemberData(nameof(RawFeeders))]
    public void A_raw_feeder_parses_to_exactly_the_normalised_model_the_product_paths_built(string rel)
    {
        string path = PathOf(rel);
        GridModel asWritten = MatpowerParser.ParseFile(path, MatpowerUnitHandling.AsWritten);
        Assert.True(MatpowerUnitNormalizer.LooksRawUnits(asWritten), $"{rel} should be classified raw");

        // The pre-N165 product composition, computed from the as-written numbers, equals the new default bit for bit.
        GridModel productBefore = MatpowerUnitNormalizer.Normalize(asWritten);
        GridModel defaultNow = MatpowerParser.ParseFile(path);
        AssertSameModel(rel, productBefore, defaultNow);

        // The product call sites still wrap Normalize; on the parser's output it is a no-op returning the same object,
        // so Normalize(ParseFile(path)) is exactly the model it was before N165.
        Assert.Same(defaultNow, MatpowerUnitNormalizer.Normalize(defaultNow));
        Assert.False(MatpowerUnitNormalizer.LooksRawUnits(defaultNow));

        MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        Assert.True(r.RawUnitsConverted);
        Assert.Contains(r.Notes, n => n.StartsWith("raw engineering units detected", StringComparison.Ordinal));

        MatpowerParseResult raw = MatpowerParser.ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path),
            null, MatpowerUnitHandling.AsWritten);
        Assert.False(raw.RawUnitsConverted);
        Assert.Contains(raw.Notes, n => n.StartsWith("raw engineering units suspected", StringComparison.Ordinal));
        AssertSameModel(rel + " as written", asWritten, raw.Model);
    }

    [Theory]
    [MemberData(nameof(S10TestSupport.BundledMatpowerData), MemberType = typeof(S10TestSupport))]
    public void Every_other_bundled_file_parses_exactly_as_before(string rel)
    {
        string text = S10TestSupport.MatpowerText(rel);
        string stem = Path.GetFileNameWithoutExtension(rel);
        MatpowerParseResult asWritten = MatpowerParser.ParseWithDiagnostics(text, stem, null, MatpowerUnitHandling.AsWritten);
        if (MatpowerUnitNormalizer.LooksRawUnits(asWritten.Model))
        {
            // Only the four feeders are raw; anything else appearing here is a new raw file that needs its own row above.
            Assert.Contains(rel, new[] { "dist/case22.m", "dist/case33bw.m", "dist/case69.m", "dist/case85.m" });
            return;
        }
        MatpowerParseResult now = MatpowerParser.ParseWithDiagnostics(text, stem);
        Assert.False(now.RawUnitsConverted);
        Assert.Equal(asWritten.Notes, now.Notes);           // no unit note on a per-unit file
        AssertSameModel(rel, asWritten.Model, now.Model);
        Assert.Same(now.Model, MatpowerUnitNormalizer.Normalize(now.Model));
    }

    [Fact]
    public void Case33bw_from_the_plain_parser_solves_to_baran_and_wu()
    {
        // The Core caller that "forgets" the normaliser now gets the right network: Baran and Wu (1989) minimum voltage
        // 0.9131 pu and real loss 202.7 kW, the same figures S4SolverConvergenceTests pins through Normalize.
        GridModel m = MatpowerParser.ParseFile(PathOf("dist/case33bw.m"));
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(0.9131, r.Vm.Min(), 4);
        Assert.Equal(0.2027, r.TotalLossMw, 4);
    }

    [Fact]
    public void An_undefined_unit_handling_value_is_refused()
    {
        string text = S10TestSupport.MatpowerText("lib/case9.m");
        Assert.Throws<ArgumentOutOfRangeException>(() => MatpowerParser.ParseWithDiagnostics(text, "case9", null, (MatpowerUnitHandling)7));
    }

    [Fact]
    public void A_file_that_still_looks_raw_after_one_conversion_is_refused()
    {
        // Two 11 kV buses on a 1 MVA base: 40,000 "kW" of load (40,000 x base raw, still 40 x base after / 1000) and a
        // 10 "ohm" branch (still 0.083 after / Zbase = 121 ohm). Converted once it would still pass every raw tell, so a
        // caller that also wraps Normalize would scale it twice: refused as ambiguous.
        const string s = @"function mpc = twice
mpc.baseMVA = 1;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 11 1 1.1 0.9; 2 1 40000 0 0 0 1 1 0 11 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 6 8 0 0 0 0 0 0 1 -360 360; ];";
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(s));
        Assert.Contains("units are ambiguous", ex.Message, StringComparison.Ordinal);
        // As written it parses (the caller has taken responsibility for the units).
        GridModel raw = MatpowerParser.Parse(s, units: MatpowerUnitHandling.AsWritten);
        Assert.True(MatpowerUnitNormalizer.LooksRawUnits(raw));
    }
}
