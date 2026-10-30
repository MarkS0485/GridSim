using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F24 (October 2026 release gap): the MATPOWER writer, the JSON case writer (the two halves of the <c>convert</c>
/// verb), gencost / dcline parsing, and the JSON v2 <c>validate</c> engine. The binding rule for conversion is that a
/// round trip solves bit-identically: every bundled MATPOWER case is written out and read back both ways, and both
/// engines (dense and sparse) must give the same bits for every voltage, angle, injection, loss and line flow.
/// </summary>
public class ConvertAndValidateTests
{
    private static string TempDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-f24-{tag}-" + Guid.NewGuid().ToString("N"));
        return dir;
    }

    private static GridModel ReparseMatpower(string text, GridModel original) =>
        MatpowerUnitNormalizer.Normalize(MatpowerParser.Parse(text, "roundtrip", original.FrequencyHz));

    private static void AssertSameNetwork(string label, GridModel a, GridModel b)
    {
        static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);
        Assert.True(Bits(a.BaseMva) == Bits(b.BaseMva), $"{label}: base MVA");
        Assert.Equal(a.Buses.Count, b.Buses.Count);
        for (int i = 0; i < a.Buses.Count; i++)
        {
            Bus x = a.Buses[i], y = b.Buses[i];
            Assert.True(x.Id == y.Id && x.Type == y.Type, $"{label}: bus {i} id/type");
            Assert.True(new[] { x.Gs, x.Bs, x.BaseKv, x.Vm, x.VaDeg }.Select(Bits)
                .SequenceEqual(new[] { y.Gs, y.Bs, y.BaseKv, y.Vm, y.VaDeg }.Select(Bits)), $"{label}: bus {x.Id} numbers");
            // Demand: the same bits, except that a zero may change sign. The JSON loader sums demand rows onto 0.0, so a
            // demand of -0.0 (the pegase files give some buses Qd = -0) reads back as +0.0. The solve is still
            // bit-identical (asserted by the caller): (Qg - (-0.0)) and (Qg - 0.0) are the same number for every Qg.
            static bool SameDemand(double p, double q) => BitConverter.DoubleToInt64Bits(p) == BitConverter.DoubleToInt64Bits(q) || (p == 0 && q == 0);
            Assert.True(SameDemand(x.Pd, y.Pd) && SameDemand(x.Qd, y.Qd), $"{label}: bus {x.Id} demand {x.Pd:R}/{x.Qd:R} vs {y.Pd:R}/{y.Qd:R}");
            Assert.Equal(x.VminPu, y.VminPu);
            Assert.Equal(x.VmaxPu, y.VmaxPu);
        }
        Assert.Equal(a.AllGenerators.Count, b.AllGenerators.Count);
        for (int i = 0; i < a.AllGenerators.Count; i++)
        {
            Generator x = a.AllGenerators[i], y = b.AllGenerators[i];
            Assert.True(x.Bus == y.Bus && x.InService == y.InService, $"{label}: generator {i} bus/status");
            Assert.True(new[] { x.Pg, x.Qg, x.Vg, x.Qmin, x.Qmax, x.Pmin, x.Pmax, x.MBase }.Select(Bits)
                .SequenceEqual(new[] { y.Pg, y.Qg, y.Vg, y.Qmin, y.Qmax, y.Pmin, y.Pmax, y.MBase }.Select(Bits)), $"{label}: generator {i} numbers");
        }
        Assert.Equal(a.Branches.Count, b.Branches.Count);
        for (int i = 0; i < a.Branches.Count; i++)
        {
            Branch x = a.Branches[i], y = b.Branches[i];
            Assert.True(x.From == y.From && x.To == y.To && x.IsTransformer == y.IsTransformer && x.InService == y.InService, $"{label}: branch {i} ends/kind");
            Assert.True(new[] { x.R, x.X, x.B, x.TapRatio, x.PhaseShiftDeg, x.RateAMva }.Select(Bits)
                .SequenceEqual(new[] { y.R, y.X, y.B, y.TapRatio, y.PhaseShiftDeg, y.RateAMva }.Select(Bits)), $"{label}: branch {i} numbers");
        }
    }

    // ---- MATPOWER writer -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(S10TestSupport.BundledMatpowerDataBelowGate), MemberType = typeof(S10TestSupport))]
    [LargeCaseInlineData(CaseSizes.Pegase9241, Engine.Sparse, "pegase/case9241pegase.m")]
    public void Every_bundled_case_round_trips_through_the_matpower_writer_bit_identically(string rel)
    {
        MatpowerParseResult p = S10TestSupport.LoadMatpower(rel);
        MatpowerWriteResult w = MatpowerWriter.Write(p.Model, p.DcLines);
        GridModel back = ReparseMatpower(w.Text, p.Model);
        AssertSameNetwork(rel, p.Model, back);
        S10TestSupport.AssertSolvesIdentical(rel + " .m round trip", p.Model, back, new PowerFlowOptions { EnforceQLimits = true });

        // gencost is carried: a file that has one (all but cigre_mv and case533mt_hi) gets it back unchanged.
        for (int i = 0; i < p.Model.AllGenerators.Count; i++)
        {
            GeneratorCost? c0 = p.Model.AllGenerators[i].Cost, c1 = back.AllGenerators[i].Cost;
            Assert.True((c0 is null) == (c1 is null), $"{rel}: generator {i} cost present {c0 is not null} vs {c1 is not null}");
            if (c0 is null) continue;
            Assert.Equal(c0.Model, c1!.Model);
            Assert.Equal(c0.StartupCost, c1.StartupCost);
            Assert.Equal(c0.ShutdownCost, c1.ShutdownCost);
            Assert.Equal(c0.Coefficients, c1.Coefficients);
        }
    }

    [Fact]
    public void The_matpower_writer_writes_inf_limits_and_out_of_service_units_exactly()
    {
        var m = new GridModel
        {
            Name = "w", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, VminPu: 0.95, VmaxPu: 1.05), new Bus(2, BusType.Pq, Pd: 10, Qd: 2) },
            Generators = new[] { new Generator(1, 12.5, Qmin: double.NegativeInfinity, Qmax: double.PositiveInfinity), new Generator(2, 3, InService: false) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, 0.02, TapRatio: 1.025, PhaseShiftDeg: -3, IsTransformer: true, RateAMva: 80) },
        };
        MatpowerWriteResult w = MatpowerWriter.Write(m);
        Assert.Contains("Inf", w.Text, StringComparison.Ordinal);
        GridModel back = MatpowerParser.Parse(w.Text, "w", 50);
        Assert.Equal(double.PositiveInfinity, back.AllGenerators[0].Qmax);
        Assert.Equal(double.NegativeInfinity, back.AllGenerators[0].Qmin);
        Assert.False(back.AllGenerators[1].InService);
        Assert.Equal(1.025, back.Branches[0].TapRatio);
        Assert.Equal(-3.0, back.Branches[0].PhaseShiftDeg);
        Assert.True(back.Branches[0].IsTransformer);
        // Bus 2 had no per-bus limits: the operational band is written, the band the checks fall back to.
        Assert.Equal(0.94, back.Buses[1].VminPu);
        Assert.Equal(1.06, back.Buses[1].VmaxPu);
        Assert.Contains(w.Notes, n => n.Contains("carry no per-bus voltage limits"));
    }

    [Fact]
    public void The_matpower_writer_refuses_nan()
    {
        var m = new GridModel
        {
            Name = "nan", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: double.NaN) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerWriter.Write(m));
        Assert.Contains("bus 2 Pd", ex.Message);
    }

    [Fact]
    public void A_zip_load_and_oltcs_are_reported_as_not_carried_by_matpower()
    {
        var m = new GridModel
        {
            Name = "zip", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10, Zip: new ZipLoad(0.5, 0, 0.5, 0, 0, 1)) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0, 0.1, Name: "T", IsTransformer: true) },
            Oltcs = new[] { new Oltc("T", 2) },
        };
        MatpowerWriteResult w = MatpowerWriter.Write(m);
        Assert.Contains(w.Notes, n => n.Contains("ZIP") && n.Contains("DIFFERENTLY"));
        Assert.Contains(w.Notes, n => n.Contains("on-load tap changer"));
        Assert.Contains("% Not carried by this MATPOWER file", w.Text.Replace("%%", "%"), StringComparison.Ordinal);
    }

    // ---- JSON case writer ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(S10TestSupport.BundledMatpowerDataBelowGate), MemberType = typeof(S10TestSupport))]
    [LargeCaseInlineData(CaseSizes.Pegase9241, Engine.Sparse, "pegase/case9241pegase.m")]
    public void Every_bundled_case_round_trips_through_the_json_writer_bit_identically(string rel)
    {
        GridModel m = S10TestSupport.LoadMatpower(rel).Model;
        string dir = TempDir("json");
        try
        {
            IReadOnlyList<string> notes = JsonCaseWriter.WriteDirectory(m, dir);
            // The only thing a MATPOWER case loses in JSON v1 is its gencost (F19 will carry it).
            if (m.AllGenerators.Any(g => g.Cost is not null)) Assert.Contains(notes, n => n.Contains("gencost"));
            Assert.DoesNotContain(notes, n => !n.Contains("gencost"));
            GridModel back = JsonGridLoader.LoadDirectory(dir);    // dispatch on, as the CLI loads it: "dispatch": false wins
            AssertSameNetwork(rel, m, back);
            S10TestSupport.AssertSolvesIdentical(rel + " JSON round trip", m, back, new PowerFlowOptions { EnforceQLimits = true });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_json_case_round_trips_json_to_matpower_to_json()
    {
        // The bundled UK case (JSON, dispatched on load) to MATPOWER and back, solved with the plain Newton engines.
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, "uk"));
        MatpowerWriteResult w = MatpowerWriter.Write(uk);
        GridModel viaM = ReparseMatpower(w.Text, uk);
        S10TestSupport.AssertSolvesIdentical("uk -> .m", uk, viaM, new PowerFlowOptions { EnforceQLimits = true });

        string dir = TempDir("uk");
        try
        {
            JsonCaseWriter.WriteDirectory(viaM, dir);
            GridModel viaJ = JsonGridLoader.LoadDirectory(dir);
            S10TestSupport.AssertSolvesIdentical("uk -> .m -> JSON", uk, viaJ, new PowerFlowOptions { EnforceQLimits = true });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_json_writer_never_overwrites_a_case()
    {
        string dir = TempDir("over");
        try
        {
            GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
            JsonCaseWriter.WriteDirectory(m, dir);
            Assert.Throws<IOException>(() => JsonCaseWriter.WriteDirectory(m, dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Json_infinity_limits_tap_fields_and_dispatch_false_load_exactly()
    {
        string dir = TempDir("keys");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "k", "baseMva": 100, "dispatch": false }""");
            File.WriteAllText(Path.Combine(dir, "buses.json"), """[ { "id": 1, "baseKv": 132, "type": "slack" }, { "id": 2, "baseKv": 33 } ]""");
            File.WriteAllText(Path.Combine(dir, "generation.json"), """
                [ { "name": "G", "bus": 1, "role": "slack", "capacityMva": 100, "outputMw": 7.25, "qOutputMvar": 1.5,
                    "qMinMvar": "-Infinity", "qMaxMvar": "Infinity", "pMinMw": 2, "pMaxMw": "Infinity" } ]
                """);
            File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "bus": 2, "pMw": 5, "qMvar": 1 } ]""");
            File.WriteAllText(Path.Combine(dir, "transmission.json"), """
                [ { "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1, "tapRatio": 1.05, "phaseShiftDeg": 2, "isTransformer": true } ]
                """);
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Generator g = m.Generators.Single();
            Assert.Equal(7.25, g.Pg);                 // dispatch false: not rebalanced
            Assert.Equal(1.5, g.Qg);
            Assert.Equal(double.NegativeInfinity, g.Qmin);
            Assert.Equal(double.PositiveInfinity, g.Qmax);
            Assert.Equal(2.0, g.Pmin);
            Assert.Equal(double.PositiveInfinity, g.Pmax);
            Branch br = m.Branches.Single();
            Assert.Equal(1.05, br.TapRatio);
            Assert.Equal(2.0, br.PhaseShiftDeg);
            Assert.True(br.IsTransformer);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("\"qMaxMvar\": \"NaN\"", "generation.json")]
    [InlineData("\"qMinMvar\": \"Infinity\"", "qMinMvar is +Infinity")]
    [InlineData("\"pMaxMw\": \"-Infinity\"", "pMaxMw is -Infinity")]
    public void Json_impossible_generator_limits_are_refused(string member, string expected)
    {
        string dir = TempDir("bad");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "b" }""");
            File.WriteAllText(Path.Combine(dir, "buses.json"), """[ { "id": 1, "baseKv": 132, "type": "slack" } ]""");
            File.WriteAllText(Path.Combine(dir, "generation.json"), $$"""[ { "name": "G", "bus": 1, "role": "slack", "capacityMva": 100, {{member}} } ]""");
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains(expected, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Without_the_new_keys_a_json_case_loads_exactly_as_before()
    {
        // Every bundled JSON case: no generator has a non-zero Qg or Pmin, every Pmax is the capacity, every line has
        // tap 1, no shift and is not a transformer - the historical mapping, unchanged.
        foreach (string d in new[] { "uk", "gb-synthetic", "gb-full" })
        {
            GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, d));
            Assert.All(m.Generators, g => { Assert.Equal(0.0, g.Qg); Assert.Equal(0.0, g.Pmin); Assert.Equal(g.MBase, g.Pmax); });
        }
    }

    // ---- gencost / dcline parse ------------------------------------------------------------------------------------

    private const string WithDcLine = @"function mpc = dc
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 230 1 1.1 0.9; 2 1 50 10 0 0 1 1 0 230 1 1.1 0.9; 3 2 0 0 0 0 1 1 0 230 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 200 0; 3 20 0 50 -50 1.01 100 1 100 0; ];
mpc.branch = [ 1 2 0.01 0.1 0 0 0 0 0 0 1 -360 360; 2 3 0.01 0.1 0 0 0 0 0 0 1 -360 360; ];
mpc.gencost = [ 2 0 0 3 0.01 20 100; 1 10 5 2 0 0 100 2500; ];
mpc.dcline = [ 1 3 1 10 8.9 0 0 1.01 1 1 100 -10 10 -10 10 0.1 0.01; ];
";

    [Fact]
    public void Gencost_rows_are_carried_on_each_generator()
    {
        MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(WithDcLine, frequencyHz: 50);
        GeneratorCost poly = r.Model.AllGenerators[0].Cost!;
        Assert.Equal(GeneratorCostModel.Polynomial, poly.Model);
        Assert.Equal(new[] { 0.01, 20, 100 }, poly.Coefficients);
        Assert.Equal(3, poly.PointCount);
        GeneratorCost pwl = r.Model.AllGenerators[1].Cost!;
        Assert.Equal(GeneratorCostModel.PiecewiseLinear, pwl.Model);
        Assert.Equal(10.0, pwl.StartupCost);
        Assert.Equal(5.0, pwl.ShutdownCost);
        Assert.Equal(new[] { 0.0, 0, 100, 2500 }, pwl.Coefficients);
        Assert.Equal(2, pwl.PointCount);
        Assert.Null(r.Model.AllGenerators[0].ReactiveCost);
        Assert.Contains(r.Notes, n => n.Contains("read mpc.gencost (2 row(s))"));
    }

    [Fact]
    public void Dc_lines_are_carried_in_the_parse_result_with_a_note_and_written_back()
    {
        MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(WithDcLine, frequencyHz: 50);
        MatpowerDcLine d = Assert.Single(r.DcLines);
        Assert.Equal(1, d.FromBus);
        Assert.Equal(3, d.ToBus);
        Assert.True(d.InService);
        Assert.Equal(10.0, d.PfMw);
        Assert.Equal(0.01, d.Loss1);
        Assert.Contains(r.Notes, n => n.Contains("NOT modelled"));

        MatpowerWriteResult w = MatpowerWriter.Write(r.Model, r.DcLines);
        MatpowerParseResult back = MatpowerParser.ParseWithDiagnostics(w.Text, frequencyHz: 50);
        Assert.Equal(r.DcLines, back.DcLines);
        Assert.Equal(r.Model.AllGenerators[1].Cost!.Coefficients, back.Model.AllGenerators[1].Cost!.Coefficients);
    }

    [Theory]
    [InlineData("mpc.gencost = [ 2 0 0 3 0.01 20 100; ];", "has 1 row(s)")]
    [InlineData("mpc.gencost = [ 3 0 0 1 5; 2 0 0 1 5; ];", "MODEL is 3")]
    [InlineData("mpc.gencost = [ 2 0 0 4 1 2 3; 2 0 0 1 5; ];", "NCOST 4 needs 4 cost number(s)")]
    [InlineData("mpc.gencost = [ 2 0 0 0 1; 2 0 0 1 5; ];", "NCOST is 0")]
    public void A_malformed_gencost_is_refused_naming_the_row(string gencost, string expected)
    {
        string text = WithDcLine.Replace("mpc.gencost = [ 2 0 0 3 0.01 20 100; 1 10 5 2 0 0 100 2500; ];", gencost, StringComparison.Ordinal)
                                .Replace("mpc.dcline = [ 1 3 1 10 8.9 0 0 1.01 1 1 100 -10 10 -10 10 0.1 0.01; ];", "", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.ParseWithDiagnostics(text, frequencyHz: 50));
        Assert.Contains("mpc.gencost", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_short_dcline_row_is_refused()
    {
        string text = WithDcLine.Replace("1 3 1 10 8.9 0 0 1.01 1 1 100 -10 10 -10 10 0.1 0.01;", "1 3 1 10 8.9;", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.ParseWithDiagnostics(text, frequencyHz: 50));
        Assert.Contains("mpc.dcline row 1 has 5 number(s)", ex.Message);
    }

    // ---- validate (JSON v2) ----------------------------------------------------------------------------------------

    private static string Fixture(params string[] parts) => Path.Combine(new[] { V2.V2TestSupport.FixturesDir }.Concat(parts).ToArray());

    [Fact]
    public void Validate_passes_valid_v2_documents_and_reports_each_invalid_one()
    {
        var files = new[]
        {
            Fixture("networks", "good.network.json"),
            Fixture("valid-sites.library.json"),
            Fixture("placeholder-with-value.library.json"),
            Fixture("networks", "wrong-schema.network.json"),
        };
        IReadOnlyList<V2DocumentReport> r = V2DocumentValidator.ValidateFiles(files, V2.V2TestSupport.SchemasDir);
        Assert.Equal(4, r.Count);
        Assert.True(r[0].Valid, V2.V2TestSupport.Dump(r[0].Errors));
        Assert.Equal("gridsim-network/2", r[0].SchemaTag);
        Assert.True(r[1].Valid, V2.V2TestSupport.Dump(r[1].Errors));
        Assert.False(r[2].Valid);                                  // a placeholder template with a value
        Assert.Equal("gridsim-case/2", r[3].SchemaTag);            // tagged as a case, so checked as one, and it is not one
        Assert.False(r[3].Valid);
    }

    [Fact]
    public void Validate_reports_a_v1_file_bad_json_and_missing_network_files()
    {
        string dir = TempDir("val");
        Directory.CreateDirectory(dir);
        try
        {
            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ \"schema\": ");
            string manifest = Path.Combine(dir, "case.json");
            File.WriteAllText(manifest, """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["net.json", "missing.json"] }""");
            File.Copy(Fixture("networks", "good.network.json"), Path.Combine(dir, "net.json"));
            var files = new[] { Path.Combine(S10TestSupport.DataDir, "uk", "system.json"), bad, manifest };
            IReadOnlyList<V2DocumentReport> r = V2DocumentValidator.ValidateFiles(files, V2.V2TestSupport.SchemasDir);

            Assert.Contains(r[0].Errors, e => e.Message.Contains("not a GridSim v2 document"));
            Assert.Contains(r[1].Errors, e => e.Message.Contains("not valid JSON"));
            SchemaError missing = Assert.Single(r[2].Errors);
            Assert.Equal("/network/1", missing.Pointer);
            Assert.Contains("'missing.json' does not exist", missing.Message);
            // The manifest's existing network file is pulled in and checked too.
            Assert.Equal(4, r.Count);
            Assert.True(r[3].Valid, V2.V2TestSupport.Dump(r[3].Errors));
            Assert.EndsWith("net.json", r[3].Path, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
