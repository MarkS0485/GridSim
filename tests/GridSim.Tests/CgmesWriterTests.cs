using GridSim.Core.Analysis;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Export pack (2026-10): <see cref="CgmesWriter"/> writes a model as CGMES that GridSim's CIM reader (with
/// <see cref="CgmesWriter.ReadBackOptions"/>) and bridge rebuild to the same power flow. A model whose per-unit values are
/// images of physical values (one read from CIM) round-trips to the bit; a MATPOWER case's values are not all reachable
/// through ohms and kV, and the writer says how many are not; devices the reader does not rebuild are refused.
/// </summary>
public class CgmesWriterTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    private static PowerFlowResult Solve(GridModel m) =>
        ContingencyAnalysis.CreateStudySolver(m).Solve(m, ContingencyAnalysis.StudyOptions);

    [Theory]
    [InlineData("lib/case30.m")]
    [InlineData("lib/case118.m")]
    public void A_model_with_values_cgmes_holds_round_trips_to_the_same_records_and_bits(string rel)
    {
        // The first pass quantises the case to values CGMES holds; the second pass must then be exact.
        GridModel source = Case(rel);
        GridModel once = CgmesWriter.ReadBack(CgmesWriter.Write(source).Text, source.BaseMva, "once");
        CgmesWriteResult w = CgmesWriter.Write(once);
        Assert.DoesNotContain(w.Notes, n => n.Contains("no CGMES form", StringComparison.Ordinal));
        GridModel back = CgmesWriter.ReadBack(w.Text, once.BaseMva, "twice");
        GridModel expected = CgmesWriter.InFileOrder(once, w);

        Assert.Equal(expected.Buses, back.Buses);
        Assert.Equal(expected.Branches, back.Branches);
        Assert.Equal(expected.Generators, back.Generators);
        PowerFlowResult a = Solve(expected), b = Solve(back);
        Assert.True(a.Converged, rel);
        S10TestSupport.AssertBitIdentical(rel, a, b);
    }

    [Fact]
    public void The_first_pass_is_within_one_ulp_per_value_and_says_how_many_values_moved()
    {
        GridModel source = Case("lib/case30.m");
        CgmesWriteResult w = CgmesWriter.Write(source);
        string note = Assert.Single(w.Notes, n => n.Contains("no CGMES form", StringComparison.Ordinal));
        Assert.StartsWith("19 value(s)", note, StringComparison.Ordinal);
        Assert.Contains("at most 1 ulp off", note, StringComparison.Ordinal);

        GridModel back = CgmesWriter.ReadBack(w.Text, source.BaseMva, "case30");
        GridModel expected = CgmesWriter.InFileOrder(source, w);
        Assert.Equal(expected.Buses.Count, back.Buses.Count);
        static long Ulps(double x, double y) => Math.Abs(BitConverter.DoubleToInt64Bits(x) - BitConverter.DoubleToInt64Bits(y));
        for (int k = 0; k < expected.Branches.Count; k++)
        {
            Branch e = expected.Branches[k], g = back.Branches[k];
            Assert.True(Ulps(e.R, g.R) <= 1 && Ulps(e.X, g.X) <= 1 && Ulps(e.B, g.B) <= 1, $"branch {k}");
            Assert.Equal(e.RateAMva, g.RateAMva);
            Assert.Equal(e.TapRatio <= 0 ? 1.0 : e.TapRatio, g.TapRatio);
            Assert.Equal(e.PhaseShiftDeg, g.PhaseShiftDeg);
        }
        for (int i = 0; i < expected.Buses.Count; i++)
        {
            Assert.Equal(expected.Buses[i].Type, back.Buses[i].Type);
            Assert.Equal(expected.Buses[i].Pd, back.Buses[i].Pd);
            Assert.Equal(expected.Buses[i].Qd, back.Buses[i].Qd);
        }
    }

    [Fact]
    public void A_bus_with_no_base_voltage_and_devices_the_reader_does_not_rebuild_are_refused_together()
    {
        GridModel m = Case("lib/case14.m");
        Branch t = m.Branches.First(b => b.TapRatio != 0 && b.TapRatio != 1);
        GridModel withOltc = m.CopyWith(
            branches: m.Branches.Select(b => b == t ? b with { Name = "T1" } : b).ToList(),
            oltcs: new[] { new Oltc("T1", t.To) });
        var ex = Assert.Throws<InvalidDataException>(() => CgmesWriter.Write(withOltc));
        Assert.Contains("14 bus(es) with no base voltage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 on-load tap changer(s) (first: T1)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_buses_with_mixed_set_points_are_one_counted_refusal_line()
    {
        // Bug #56: a second machine at every voltage-controlled bus of case30 (slack and PV, six) with another set-point;
        // one line counts them, naming five.
        GridModel m = Case("lib/case30.m");
        var pv = m.Buses.Where(b => b.Type is BusType.Pv or BusType.Slack).Select(b => b.Id).ToHashSet();
        var extra = m.Generators.Where(g => pv.Contains(g.Bus)).Select(g => g with { Vg = g.Vg + 0.01, Name = null }).ToList();
        Assert.True(extra.Count > 5);
        GridModel mixed = m.CopyWith(generators: m.AllGenerators.Concat(extra).ToList());
        var ex = Assert.Throws<InvalidDataException>(() => CgmesWriter.Write(mixed));
        string[] lines = ex.Message.Split(Environment.NewLine);
        string line = Assert.Single(lines, l => l.Contains("different voltage set-points", StringComparison.Ordinal));
        Assert.Contains($"- {pv.Count} voltage-controlled bus(es) whose machines hold different voltage set-points (first: ", line, StringComparison.Ordinal);
        Assert.Equal(5, line.Split(" pu)").Length - 1);
    }

    [Fact]
    public void The_starting_voltages_and_ratings_are_read_only_when_asked()
    {
        GridModel source = Case("lib/case118.m");
        CgmesWriteResult w = CgmesWriter.Write(source);
        CimNetwork plain = CimImporter.Parse(w.Text);
        Assert.All(plain.Buses, b => Assert.Null(b.SvVKv));
        Assert.All(plain.Branches, b => Assert.Null(b.RateAMva));
        GridModel bridged = CimGridModelBridge.ToGridModel(plain, source.BaseMva);
        Assert.All(bridged.Buses, b => Assert.Equal(1.0, b.Vm));
        Assert.All(bridged.Branches, b => Assert.Equal(0.0, b.RateAMva));

        CimNetwork full = CimImporter.Parse(w.Text, CgmesWriter.ReadBackOptions);
        Assert.Equal(source.Buses.Count(b => b.Vm != 1.0 || b.VaDeg != 0.0), full.Buses.Count(b => b.SvVKv is not null));
        Assert.Equal(source.Branches.Count(b => b.RateAMva > 0), full.Branches.Count(b => b.RateAMva is not null) + full.Transformers.Count(t => t.RateAMva is not null));
    }

    [Fact]
    public void A_ratio_the_linear_tap_changer_cannot_give_back_is_written_as_a_table()
    {
        // Find a ratio for which the importer's linear form 1 + 100 (r - 1) / 100 is not r. Near 1 there is none: for
        // r in [0.75, 1.25] the two roundings of r - 1 stay below half an ulp of r, so the linear form is always exact there
        // (a search from 0.9 never ends). From 0.6 the 12th double up misses; the search is capped so it fails loud.
        double ratio = 0.6;
        int steps = 0;
        while (BitConverter.DoubleToInt64Bits(1.0 + 100.0 * (ratio - 1.0) / 100.0) == BitConverter.DoubleToInt64Bits(ratio))
        {
            ratio = Math.BitIncrement(ratio);
            Assert.True(++steps < 1000, "no ratio the linear form misses within 1000 doubles of 0.6");
        }
        var net = new CimNetwork(
            new[] { new CimBus("a", "a", 132), new CimBus("b", "b", 33) },
            Array.Empty<CimBranch>(), Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g", "g", "a", 0.0) },
            new[] { new CimTransformer("t", "t", new[] { new CimTransformerEnd(1, "a", 1.0, 10.0, 132), new CimTransformerEnd(2, "b", 0.0, 0.0, 33) }, ratio, 0.0) });
        string text = CimExporter.Export(net);
        Assert.Contains("RatioTapChangerTablePoint", text, StringComparison.Ordinal);
        Assert.Equal(ratio, Assert.Single(CimImporter.Parse(text).Transformers).TapRatio);
        // A ratio the linear form does give back is written linearly, as before.
        string linear = CimExporter.Export(net with { Transformers = new[] { net.Transformers[0] with { TapRatio = 0.975 } } });
        Assert.DoesNotContain("RatioTapChangerTable", linear, StringComparison.Ordinal);
        Assert.Equal(0.975, Assert.Single(CimImporter.Parse(linear).Transformers).TapRatio);
    }

    [Fact]
    public void Convert_writes_cgmes_and_shows_the_values_it_could_not_hold()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-convert-cgmes-" + Guid.NewGuid().ToString("N"));
        try
        {
            string output = Path.Combine(dir, "case30.xml");
            CliProcess.Result r = CliProcess.RunBuilt("convert", "case30", output);
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("value(s) have no CGMES form", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("does NOT solve bit-identically", r.StdErr, StringComparison.Ordinal);
            Assert.True(File.Exists(output));

            string refused = Path.Combine(dir, "case14.xml");
            CliProcess.Result n = CliProcess.RunBuilt("convert", "case14", refused);
            Assert.True(n.ExitCode == 1, n.ToString());
            Assert.Contains("no base voltage", n.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(refused));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
