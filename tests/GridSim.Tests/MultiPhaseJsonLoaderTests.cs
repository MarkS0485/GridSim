using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R059: the three-phase distribution stack gets a file format (gridsim-dist3ph/1, MultiPhaseJsonLoader) and a
/// bundled published test feeder. These tests check (1) the sweep reproduces the IEEE 4-node test feeder's published
/// node voltages, (2) the loader builds bit-for-bit the model a hand-built MultiPhaseModel would be, and (3) the
/// loader is strict: every malformed input fails loud, naming the file and the JSON path.
/// </summary>
public class MultiPhaseJsonLoaderTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string FeederPath(string name) => Path.Combine(Root(), "data", "dist3ph", name);

    // ---- (1) published IEEE 4-node results ---------------------------------------------------------------

    // Published node voltages (line-to-neutral volts, degrees) of the IEEE 4 Node Test Feeder (IEEE PES Distribution
    // System Analysis Subcommittee, W. H. Kersting), Y-Y (grounded wye - grounded wye) step-down cases. They are
    // printed to 4 significant figures (1 V) and 0.1 degree, so the comparison tolerance is HALF of the last printed
    // digit: 0.5 V on magnitude and 0.05 degree on angle. A model that agrees to rounding passes; anything more is a
    // real mismatch. (The same values are tabulated as reference results in the LBL Modelica Buildings library,
    // Buildings.Electrical.AC.ThreePhasesUnbalanced.Validation.IEEETests.Test4NodesFeeder.)
    private const double MagTolVolts = 0.5;
    private const double AngTolDeg = 0.05;

    private static readonly (int Bus, double[] V, double[] Deg)[] PublishedBalanced =
    {
        (2, new[] { 7107.0, 7140.0, 7121.0 }, new[] { -0.3, -120.3, 119.6 }),
        // Moved 2026-10 (END-PASS VERIFY row 2, end pass; IEEE 4 Node Test Feeder solution tables, revised 2006-09-19,
        // Gr Y-Gr Y step-down balanced node 3 prints 2247.6 / -3.7, N066, Ruling R1 and Mark's go on the end-pass moves
        // 2026-10-11): the 2247.0 was a copying error. Before -> after: node 3 phase a 2247.0 -> 2247.6 V. GridSim 2247.4140 V
        // at -3.69430 deg (measured on the endpass-v branch at 1.95.274, not a suite run log):
        // residual -0.1860 V, inside this test's 0.5 V tolerance (unchanged); strictly it is 0.136 V outside the 0.05 V half
        // digit of a value printed to 0.1 V, recorded here and not hidden.
        (3, new[] { 2247.6, 2269.0, 2256.0 }, new[] { -3.7, -123.5, 116.4 }),
        (4, new[] { 1918.0, 2061.0, 1981.0 }, new[] { -9.1, -128.3, 110.9 }),
    };

    private static readonly (int Bus, double[] V, double[] Deg)[] PublishedUnbalanced =
    {
        (2, new[] { 7164.0, 7110.0, 7082.0 }, new[] { -0.1, -120.2, 119.3 }),
        (3, new[] { 2305.0, 2255.0, 2203.0 }, new[] { -2.3, -123.6, 114.8 }),
        (4, new[] { 2175.0, 1930.0, 1833.0 }, new[] { -4.1, -126.8, 102.8 }),
    };

    [Theory]
    [InlineData("ieee4-gyGy-stepdown-balanced.json", true)]
    [InlineData("ieee4-gyGy-stepdown-unbalanced.json", false)]
    public void Ieee4_gyGy_stepdown_matches_the_published_node_voltages_to_rounding(string file, bool balanced)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath(file));
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"sweep status {r.Status} after {r.Iterations} iterations");

        var failures = new List<string>();
        foreach ((int bus, double[] vPub, double[] degPub) in balanced ? PublishedBalanced : PublishedUnbalanced)
        {
            int i = r.BusIds.ToList().IndexOf(bus);
            Assert.True(i >= 0, $"bus {bus} missing from the result");
            for (int p = 0; p < 3; p++)
            {
                double volts = r.V[i][p].Magnitude * f.VoltBaseLN(bus);
                double deg = r.V[i][p].Phase * 180.0 / Math.PI;
                double dv = volts - vPub[p];
                double dd = deg - degPub[p];
                if (Math.Abs(dv) > MagTolVolts || Math.Abs(dd) > AngTolDeg)
                    failures.Add($"bus {bus} phase {"abc"[p]}: {volts:F3} V at {deg:F4} deg vs published {vPub[p]} V at {degPub[p]} deg (dV {dv:F3} V, dAngle {dd:F4} deg)");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- (2) the loader is the hand-built model, bit for bit -------------------------------------------------

    [Fact]
    public void Loader_builds_exactly_the_hand_built_per_unit_model()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath("ieee4-gyGy-stepdown-unbalanced.json"));

        // The same feeder written directly against the Core API, with the per-unit conversion spelt out.
        double[,] r =
        {
            { 0.45755108799674815, 0.15595005994412853, 0.1534848254947009 },
            { 0.15595005994412856, 0.4666276021929273, 0.15800625374417016 },
            { 0.1534848254947009, 0.15800625374417016, 0.46147239337530727 },
        };
        double[,] x =
        {
            { 1.0780345407868444, 0.5016733333358068, 0.38493375201227714 },
            { 0.5016733333358068, 1.048163373204583, 0.42364802055288314 },
            { 0.3849337520122771, 0.42364802055288314, 1.0650579323735838 },
        };
        PhaseImpedance Line(double feet, double kv)
        {
            double miles = feet / 5280.0;
            double zBase = kv * kv / 6.0;
            var z = new Complex[3, 3];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                z[i, j] = new Complex(r[i, j], x[i, j]) * miles / zBase;
            return new PhaseImpedance(z);
        }
        double rebase = 6.0 * 1000.0 / 6000.0;
        var tx = PhaseImpedance.Diagonal(new Complex(1.0 / 100.0 * rebase, 6.0 / 100.0 * rebase));
        double perPhaseKva = 6.0 * 1000.0 / 3.0;
        var s = new[]
        {
            new Complex(1275 / perPhaseKva, 790.1740314639554 / perPhaseKva),
            new Complex(1800 / perPhaseKva, 871.7797887081346 / perPhaseKva),
            new Complex(2375 / perPhaseKva, 780.6247497998002 / perPhaseKva),
        };
        Complex[] src = MultiPhaseModel.BalancedNominal();
        var hand = new MultiPhaseModel(
            new[] { new PhaseBus(1, true, src), new PhaseBus(2, false), new PhaseBus(3, false), new PhaseBus(4, false) },
            new[] { new PhaseBranch(1, 2, Line(2000, 12.47)), new PhaseBranch(3, 4, Line(2500, 4.16)), new PhaseBranch(2, 3, tx) },
            new[] { new PhaseLoad(4, s) },
            6.0);

        // Lines come before transformers in the loaded order (the file's section order).
        Assert.Equal(new[] { "line1-2", "line3-4", "t2-3" }, f.BranchIds);
        Assert.Equal(new[] { "line", "line", "transformer" }, f.BranchKinds);
        for (int b = 0; b < 3; b++)
        {
            Assert.Equal(hand.Branches[b].From, f.Model.Branches[b].From);
            Assert.Equal(hand.Branches[b].To, f.Model.Branches[b].To);
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.Equal(hand.Branches[b].Z[i, j], f.Model.Branches[b].Z[i, j]);
        }
        for (int p = 0; p < 3; p++)
        {
            Assert.Equal(src[p], f.Model.Buses[0].SourceV![p]);
            Assert.Equal(s[p], f.Model.Loads[0].SPerPhase[p]);
        }

        // And so the sweep result is bit-identical.
        MultiPhaseResult a = new ThreePhaseSweep().Solve(hand);
        MultiPhaseResult c = new ThreePhaseSweep().Solve(f.Model);
        Assert.Equal(a.Iterations, c.Iterations);
        for (int i = 0; i < 4; i++)
        for (int p = 0; p < 3; p++)
            Assert.Equal(a.V[i][p], c.V[i][p]);
    }

    [Fact]
    public void Bases_give_volts_and_amps()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath("ieee4-gyGy-stepdown-balanced.json"));
        Assert.Equal(12470.0 / Math.Sqrt(3.0), f.VoltBaseLN(1), 9);
        Assert.Equal(6e6 / (Math.Sqrt(3.0) * 4160.0), f.AmpBase(4), 9);
        Assert.Contains("Kersting", f.Description, StringComparison.Ordinal);
    }

    // ---- (3) strictness ---------------------------------------------------------------------------------------

    private static string BalancedText() => File.ReadAllText(FeederPath("ieee4-gyGy-stepdown-balanced.json"));

    /// <summary>Parse a mutated copy of the balanced feeder and return the error message (the parse must fail).</summary>
    private static string Refused(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(json, "mutated.json"));
        Assert.StartsWith("mutated.json: ", ex.Message, StringComparison.Ordinal);   // every error names the file
        return ex.Message;
    }

    private static string Replace(string text, string from, string to)
    {
        Assert.Contains(from, text, StringComparison.Ordinal);   // the mutation must actually apply
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unmutated_text_parses()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(BalancedText(), "ok.json");
        Assert.Equal(4, f.Model.Buses.Count);
    }

    [Theory]
    [InlineData("\"gridsim-dist3ph/1\"", "\"gridsim-dist3ph/2\"", "$.schema")]
    [InlineData("\"gridsim-dist3ph/1\"", "\"GridSim-Dist3ph/1\"", "$.schema")]
    [InlineData("\"baseMva\": 6.0", "\"baseMVA\": 6.0", "$.baseMVA")]                                 // case-sensitive: unknown member
    [InlineData("\"baseMva\": 6.0", "\"baseMva\": 0", "$.baseMva")]
    [InlineData("\"baseMva\": 6.0", "\"baseMva\": 1e999", "$.baseMva")]                               // overflows to infinity
    [InlineData("\"baseMva\": 6.0", "\"baseMva\": \"6\"", "$.baseMva")]                               // wrong JSON type
    [InlineData("{ \"id\": 2, \"baseKvLL\": 12.47 }", "{ \"id\": 1, \"baseKvLL\": 12.47 }", "$.buses[1].id")]   // duplicate bus id
    [InlineData("{ \"id\": 2, \"baseKvLL\": 12.47 }", "{ \"id\": 2, \"baseKvLL\": 12.47, \"source\": {} }", "$.buses")] // two sources
    [InlineData("{ \"id\": 2, \"baseKvLL\": 12.47 }", "{ \"id\": 2 }", "$.buses[1].baseKvLL")]       // missing required
    [InlineData("\"from\": 1, \"to\": 2, \"code\"", "\"from\": 1, \"to\": 9, \"code\"", "$.lines[0].to")]       // unknown bus
    [InlineData("\"from\": 1, \"to\": 2, \"code\"", "\"from\": 1, \"to\": 3, \"code\"", "$.lines[0]")]          // 12.47 kV to 4.16 kV line
    [InlineData("\"code\": \"ieee4-overhead\", \"length\": 2000", "\"code\": \"ieee4\", \"length\": 2000", "$.lines[0].code")]
    [InlineData("\"length\": 2000, \"lengthUnit\": \"ft\"", "\"length\": 2000, \"lengthUnit\": \"feet\"", "$.lines[0].lengthUnit")]
    [InlineData("\"length\": 2000, \"lengthUnit\": \"ft\"", "\"length\": -2000, \"lengthUnit\": \"ft\"", "$.lines[0].length")]
    [InlineData("\"id\": \"line3-4\"", "\"id\": \"line1-2\"", "$.lines[1].id")]                       // duplicate branch id
    [InlineData("\"id\": \"t2-3\"", "\"id\": \"line1-2\"", "$.transformers[0].id")]                   // shared id space
    // Moved 2026-10 (N168, 1.95.251, the same R059 / N062 rule as the delta-load case below: Mark's 2026-10-02
    // best-judgement ruling): delta / grounded-wye step-down transformers are now modelled and validated against the IEEE 4
    // Node Test Feeder's published D-GrY results, so "d-gy" is accepted; the case keeps checking that a transformer
    // connection the loader does not model is refused at its path, with a value that is still unmodelled. Before: "d-gy"
    // refused at $.transformers[0].connection. After: "d-d" refused there.
    // Moved again 2026-10 (N168, 1.95.253, the same rule): "d-d" (delta / delta) and "y-d" are now modelled and validated
    // against the IEEE 4 Node Test Feeder's published D-D and Y-D results. Before: "d-d" refused at
    // $.transformers[0].connection. After: "open-delta" (an open connection, still unmodelled) refused there.
    [InlineData("\"connection\": \"gy-gy\"", "\"connection\": \"open-delta\"", "$.transformers[0].connection")]
    // Moved 2026-10 (N168, 1.95.254, the R059 / N062 rule, Mark's 2026-10-02 best-judgement ruling): off-nominal ratios
    // are now modelled (proved by the per-unit invariance tests in Dist3phOffNominalTransformerTests), so 4.2 kV on a 4.16
    // kV bus loads. The case keeps checking that an unusable rated voltage is refused at its path. Before: 4.2 refused as
    // off-nominal. After: 41.6 (kV entered ten times too large, more than 20 % from the bus base) refused there.
    [InlineData("\"kvLLTo\": 4.16", "\"kvLLTo\": 41.6", "$.transformers[0].kvLLTo")]                  // implausible rated kV
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0", "\"rPercent\": 0, \"xPercent\": 0", "$.transformers[0]")]
    [InlineData("\"bus\": 4, \"connection\": \"wye\"", "\"bus\": 5, \"connection\": \"wye\"", "$.loads[0].bus")]
    // Moved 2026-10 (R059 / N062, authorised by Mark's 2026-10-02 best-judgement ruling): delta loads are now modelled,
    // so "delta" is accepted; the case keeps checking that a connection the loader does not know is refused at its path,
    // with a value that is still unknown. Before: "delta" refused at $.loads[0].connection. After: "star" refused there.
    [InlineData("\"connection\": \"wye\"", "\"connection\": \"star\"", "$.loads[0].connection")]
    [InlineData("\"model\": \"constant-power\"", "\"model\": \"zip\"", "$.loads[0].model")]
    [InlineData("\"kw\": [1800, 1800, 1800]", "\"kw\": [1800, 1800]", "$.loads[0].kw")]
    [InlineData("\"kw\": [1800, 1800, 1800]", "\"kw\": [1800, null, 1800]", "$.loads[0].kw[1]")]
    [InlineData("\"kw\": [1800, 1800, 1800]", "\"kw\": [1800, 1800, 1800], \"kw\": [1, 1, 1]", "")]  // duplicate member
    [InlineData("[0.1534848254947009, 0.15800625374417016, 0.46147239337530727]\n", "", "$.lineCodes[0].r")] // 2-row matrix
    [InlineData("\"magnitudePu\": [1.0, 1.0, 1.0]", "\"magnitudePu\": [1.0, 0.0, 1.0]", "$.buses[0].source.magnitudePu[1]")]
    public void Malformed_input_is_refused_naming_the_path(string from, string to, string path)
    {
        string text = BalancedText().Replace("\r\n", "\n");
        if (from.EndsWith('\n'))
        {
            // Drop the third row of the r matrix (and the comma ending the second row) to make it 2x3.
            text = Replace(text, "0.15800625374417016],\n        [0.1534848254947009, 0.15800625374417016, 0.46147239337530727]\n",
                                 "0.15800625374417016]\n");
        }
        else text = Replace(text, from, to);
        string msg = Refused(text);
        if (path.Length > 0) Assert.Contains(path + ":", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_loop_or_an_island_is_refused()
    {
        // Close a loop: add a line 2-1 in place of nothing (n branches for n buses).
        string loop = Replace(BalancedText(),
            "{ \"id\": \"line3-4\"",
            "{ \"id\": \"loop\", \"from\": 2, \"to\": 1, \"code\": \"ieee4-overhead\", \"length\": 100, \"lengthUnit\": \"ft\" },\n    { \"id\": \"line3-4\"");
        Assert.Contains("radial tree", Refused(loop), StringComparison.Ordinal);

        // An island: bus 5 with no branch (n - 1 = 4 branches needed, 3 present).
        string island = Replace(BalancedText(), "{ \"id\": 4, \"baseKvLL\": 4.16 }", "{ \"id\": 4, \"baseKvLL\": 4.16 },\n    { \"id\": 5, \"baseKvLL\": 4.16 }");
        Assert.Contains("radial tree", Refused(island), StringComparison.Ordinal);
    }

    [Fact]
    public void Not_json_names_the_file()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse("{ \"schema\": ", "broken.json"));
        Assert.StartsWith("broken.json: not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FileNotFoundException>(() => MultiPhaseJsonLoader.Load(FeederPath("no-such-feeder.json")));
    }

    // ---- measurements and the estimator ------------------------------------------------------------------

    [Fact]
    public void Example_measurements_estimate_the_sweep_state()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath("ieee4-gyGy-stepdown-unbalanced.json"));
        MultiPhaseMeasurementSet m = MultiPhaseJsonLoader.LoadMeasurements(FeederPath("ieee4-gyGy-stepdown-unbalanced.measurements.json"), f);
        Assert.Equal(9, m.VoltageMagnitudes.Count);
        Assert.Equal(9, m.Currents.Count);

        ThreePhaseWlsResult est = ThreePhaseWlsEstimator.Estimate(f.Model, m.VoltageMagnitudes, m.Currents);
        Assert.True(est.Converged);
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(f.Model);
        // The example values are the sweep solution rounded to 6 decimals, so the estimate agrees to that rounding.
        for (int i = 0; i < 4; i++)
        for (int p = 0; p < 3; p++)
            Assert.True((est.V[i][p] - truth.V[i][p]).Magnitude < 5e-6, $"bus index {i} phase {p}: {(est.V[i][p] - truth.V[i][p]).Magnitude:E3}");
    }

    [Theory]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"voltageMagnitudes\": [ { \"bus\": 7, \"phase\": \"a\", \"valuePu\": 1.0, \"sigmaPu\": 0.01 } ] }", "$.voltageMagnitudes[0].bus")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"voltageMagnitudes\": [ { \"bus\": 2, \"phase\": \"d\", \"valuePu\": 1.0, \"sigmaPu\": 0.01 } ] }", "$.voltageMagnitudes[0].phase")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"voltageMagnitudes\": [ { \"bus\": 2, \"phase\": \"a\", \"valuePu\": 1.0, \"sigmaPu\": 0 } ] }", "$.voltageMagnitudes[0].sigmaPu")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"currents\": [ { \"from\": 1, \"to\": 3, \"phase\": \"a\", \"realPu\": 1, \"imagPu\": 0, \"sigmaPu\": 0.01 } ] }", "$.currents[0]")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\" }", "$")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph/1\" }", "$.schema")]
    [InlineData("{ \"schema\": \"gridsim-dist3ph-measurements/1\", \"pseudoVoltage\": { \"valuePu\": 1.0, \"sigma\": 0.05 } }", "$.pseudoVoltage.sigma")]
    public void Malformed_measurements_are_refused_naming_the_path(string json, string path)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath("ieee4-gyGy-stepdown-unbalanced.json"));
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.ParseMeasurements(json, "m.json", f));
        Assert.StartsWith("m.json: " + path + ":", ex.Message, StringComparison.Ordinal);
    }
}
