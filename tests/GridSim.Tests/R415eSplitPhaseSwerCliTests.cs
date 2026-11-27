using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Lines;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 wiring: the 120/240 V split-phase source and centre-tap transformer (Core <see cref="SplitPhase"/>) and the single-wire earth-return
/// feeder (Core <see cref="Swer"/>) as optional members of the dist3ph-mesh case file. The oracles are the Core tests' own hand solutions
/// (the split-phase service with unbalanced halves, the centre-tap relations, the SWER loop equation and the pi-section closed form), and
/// the Core builders called directly on the same numbers, bit for bit.
/// </summary>
public sealed class R415eSplitPhaseSwerCliTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string R(double v) => v.ToString("R", Inv);
    private static Complex C(double r, double i) => new(r, i);
    private static double Abs(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);

    private readonly string _dir = TestTemp.Under("gridsim-r415e-" + Guid.NewGuid().ToString("N"));

    public R415eSplitPhaseSwerCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private JsonElement Run(string caseText, out CliProcess.Result result, params string[] extra)
    {
        string c = Write("case-" + Guid.NewGuid().ToString("N") + ".json", caseText);
        string o = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N") + ".json");
        result = CliProcess.RunBuilt(new[] { "dist3ph-mesh", c, "--tol", "1e-9", "--json", o }.Concat(extra).ToArray());
        Assert.True(result.ExitCode == 0, result.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(o));
        return d.RootElement.Clone();
    }

    private static Complex Volt(JsonElement doc, string node, string wire)
    {
        foreach (JsonElement row in doc.GetProperty("tables").GetProperty("voltages").EnumerateArray())
            if (row.GetProperty("node").GetString() == node && row.GetProperty("wire").GetString() == wire)
                return new Complex(row.GetProperty("v_re").GetDouble(), row.GetProperty("v_im").GetDouble());
        throw new KeyNotFoundException($"{node}.{wire}");
    }

    private static Complex IFrom(JsonElement doc, string branch, string wire)
    {
        foreach (JsonElement row in doc.GetProperty("tables").GetProperty("branch_currents").EnumerateArray())
            if (row.GetProperty("branch").GetString() == branch && row.GetProperty("wire").GetString() == wire)
                return new Complex(row.GetProperty("i_from_re").GetDouble(), row.GetProperty("i_from_im").GetDouble());
        throw new KeyNotFoundException($"{branch}.{wire}");
    }

    private static void Near(Complex expected, Complex actual, double tol, string what)
        => Assert.True(Abs(expected - actual) <= tol, $"{what}: expected {expected} but got {actual} (|diff| {Abs(expected - actual):E3} > {tol:E1})");

    private static void SameBits(Complex a, Complex b, string what)
        => Assert.True(BitConverter.DoubleToInt64Bits(a.Real) == BitConverter.DoubleToInt64Bits(b.Real)
                       && BitConverter.DoubleToInt64Bits(a.Imaginary) == BitConverter.DoubleToInt64Bits(b.Imaginary), $"{what}: {a} vs {b}");

    private static Complex SFor(Complex z, double v0) => v0 * v0 / Complex.Conjugate(z);

    private static string LineLine(string name, string node, string from, string to, Complex z, double v0)
    {
        Complex s = SFor(z, v0);
        return $$"""{"name":"{{name}}","node":"{{node}}","connection":"line-line","from":"{{from}}","to":"{{to}}","p_w":[{{R(s.Real)}}],"q_var":[{{R(s.Imaginary)}}],"nominal_v":{{R(v0)}},"zip":[0,0,1]}""";
    }

    // ---- split phase ----------------------------------------------------------------------------------------------------

    private const string ServiceBranch =
        """{"name":"service","from":"S","to":"L","wires":["l1","l2","n"],"r_ohm":[[0.05,0,0],[0,0.05,0],[0,0,0.08]],"x_ohm":[[0.02,0,0],[0,0.02,0],[0,0,0.03]]}""";

    private static string SplitCase(string loads) =>
        $$"""{"schema":"gridsim-dist3ph-mesh/1","name":"split","nodes":[{"name":"S","wires":["l1","l2","n"]},{"name":"L","wires":["l1","l2","n"]}],"split_phase_sources":[{"node":"S","volts":240}],"branches":[{{ServiceBranch}}],"loads":[{{loads}}]}""";

    [Fact]
    public void A_split_phase_service_with_unbalanced_halves_matches_the_hand_solution_and_the_core_builder_bit_for_bit()
    {
        // The Core test's case: lines Zl, neutral Zn, loads l1-n, l2-n and l1-l2, all constant impedance, so the answer is linear.
        Complex zl = C(0.05, 0.02), zn = C(0.08, 0.03), r1 = C(6.0, 1.0), r2 = C(12.0, 2.0), r3 = C(20.0, 5.0);
        string loads = string.Join(",", LineLine("h1", "L", "l1", "n", r1, 120.0), LineLine("h2", "L", "l2", "n", r2, 120.0), LineLine("h12", "L", "l1", "l2", r3, 240.0));
        JsonElement doc = Run(SplitCase(loads), out _);

        // By hand (the Core test's derivation): unknowns V1, V2 at the load node with V_N = zn (I1 + I2), I_k = (E_k - V_k)/zl.
        Complex e1 = 120, e2 = -120, k = zn / zl;
        Complex a00 = 1 / r1 + k / r1 + 1 / r3 + 1 / zl, a01 = k / r1 - 1 / r3, b0 = k / r1 * (e1 + e2) + e1 / zl;
        Complex a10 = k / r2 - 1 / r3, a11 = 1 / r2 + k / r2 + 1 / r3 + 1 / zl, b1 = k / r2 * (e1 + e2) + e2 / zl;
        Complex det = a00 * a11 - a01 * a10;
        Complex v1 = (b0 * a11 - a01 * b1) / det, v2 = (a00 * b1 - b0 * a10) / det;
        Complex i1 = (e1 - v1) / zl, i2 = (e2 - v2) / zl;
        Near(i1, IFrom(doc, "service", "l1"), 1e-8, "line l1 current");
        Near(i2, IFrom(doc, "service", "l2"), 1e-8, "line l2 current");
        Near(-(i1 + i2), IFrom(doc, "service", "n"), 1e-8, "neutral wire current = -(I_l1 + I_l2)");
        Assert.True(Abs(i1 + i2) > 1.0 && Abs(i1 + i2) < Abs(i1));

        // Core directly: the same builder and elements, the same bits.
        var nodes = new[] { new FrameNode("S", ["l1", "l2", "n"]), new FrameNode("L", ["l1", "l2", "n"]) };
        var model = new PhaseFrameModel("split", nodes, [SplitPhase.IdealSource("S", 240.0)],
            [new FrameBranch("service", "S", "L", ["l1", "l2", "n"], new Complex[,] { { zl, 0, 0 }, { 0, zl, 0 }, { 0, 0, zn } })],
            [FrameLoad.LineLine("h1", "L", "l1", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h2", "L", "l2", "n", SFor(r2, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h12", "L", "l1", "l2", SFor(r3, 240.0), 240.0, Zip.ConstantImpedance)]);
        MeshedResult core = Newton3Ph.Solve(model, new MeshedOptions { ToleranceAmps = 1e-9 });
        Assert.Equal(MeshedStatus.Converged, core.Status);
        foreach (string w in new[] { "l1", "l2", "n" })
        {
            SameBits(core.V("L", w), Volt(doc, "L", w), "V(L." + w + ")");
            SameBits(core.V("S", w), Volt(doc, "S", w), "V(S." + w + ")");
        }
    }

    [Fact]
    public void Balanced_split_phase_halves_carry_no_neutral_current()
    {
        string loads = string.Join(",", LineLine("h1", "L", "l1", "n", C(6.0, 1.0), 120.0), LineLine("h2", "L", "l2", "n", C(6.0, 1.0), 120.0));
        JsonElement doc = Run(SplitCase(loads), out _);
        Near(Complex.Zero, IFrom(doc, "service", "n"), 1e-8, "neutral current of balanced halves");
        Near(Volt(doc, "L", "l1"), -Volt(doc, "L", "l2"), 1e-8, "the lines are 180 degrees apart about the neutral");
    }

    [Fact]
    public void A_centre_tap_transformer_obeys_the_core_test_relations_and_matches_the_core_builder_bit_for_bit()
    {
        double vp0 = 11000.0, vs = 240.0, t2 = vs / 2.0;
        Complex zp = C(20.0, 60.0), zh = C(0.01, 0.03), zl = C(0.05, 0.02), zn = C(0.08, 0.03), r1 = C(6.0, 1.0), r2 = C(12.0, 2.0), r3 = C(20.0, 5.0);
        string text = $$"""
        {"schema":"gridsim-dist3ph-mesh/1","name":"ct",
         "nodes":[{"name":"P","wires":["a","n"]},{"name":"T","wires":["l1","l2","n"]},{"name":"L","wires":["l1","l2","n"]}],
         "sources":[{"node":"P","wires":["a"],"magnitude_v":[11000],"angle_deg":[0]}],
         "centre_tap_transformers":[{"name":"tx","primary_node":"P","primary_wire":"a","primary_return":"n","primary_volts":11000,"secondary_node":"T","secondary_volts":240,
                                      "primary_r_ohm":20,"primary_x_ohm":60,"half_r_ohm":0.01,"half_x_ohm":0.03}],
         "branches":[{"name":"svc","from":"T","to":"L","wires":["l1","l2","n"],"r_ohm":[[0.05,0,0],[0,0.05,0],[0,0,0.08]],"x_ohm":[[0.02,0,0],[0,0.02,0],[0,0,0.03]]}],
         "loads":[{{LineLine("h1", "L", "l1", "n", r1, 120.0)}},{{LineLine("h2", "L", "l2", "n", r2, 120.0)}},{{LineLine("h12", "L", "l1", "l2", r3, 240.0)}}],
         "earthing":[{"node":"P","wire":"n","r_ohm":0},{"node":"T","wire":"n","r_ohm":0}]}
        """;
        JsonElement doc = Run(text, out _);

        // The Core test's relations: neutral wire = -(I_l1 + I_l2) toward the load, primary current = (t2/vp0)(J1 - J2), l1 and l2 in antiphase.
        Complex j1 = IFrom(doc, "svc", "l1"), j2 = IFrom(doc, "svc", "l2");
        Near(-(j1 + j2), IFrom(doc, "svc", "n"), 1e-8, "neutral wire current");
        Assert.True(Abs(j1 + j2) > 1.0 && Abs(j1 + j2) < Abs(j1));
        JsonElement src = doc.GetProperty("tables").GetProperty("sources")[0];
        Near((t2 / vp0) * (j1 - j2), new Complex(src.GetProperty("i_re").GetDouble(), src.GetProperty("i_im").GetDouble()), 1e-8, "primary current");
        Assert.True((Volt(doc, "L", "l1") * Volt(doc, "L", "l2")).Real < 0, "l1 and l2 are in antiphase about the neutral");

        // Core directly.
        var nodes = new[] { new FrameNode("P", ["a", "n"]), new FrameNode("T", ["l1", "l2", "n"]), new FrameNode("L", ["l1", "l2", "n"]) };
        var model = new PhaseFrameModel("ct", nodes, [new FrameSource("P", ["a"], [C(11000.0, 0)])],
            [new FrameBranch("svc", "T", "L", ["l1", "l2", "n"], new Complex[,] { { zl, 0, 0 }, { 0, zl, 0 }, { 0, 0, zn } })],
            [FrameLoad.LineLine("h1", "L", "l1", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h2", "L", "l2", "n", SFor(r2, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h12", "L", "l1", "l2", SFor(r3, 240.0), 240.0, Zip.ConstantImpedance)],
            [new EarthingElement("P", "n", Complex.Zero), new EarthingElement("T", "n", Complex.Zero)],
            null, [SplitPhase.CentreTapUnit("tx", "P", "a", "n", vp0, "T", vs, zp, zh)]);
        MeshedResult core = Newton3Ph.Solve(model, new MeshedOptions { ToleranceAmps = 1e-9 });
        Assert.Equal(MeshedStatus.Converged, core.Status);
        foreach (WireVoltage v in core.Voltages) SameBits(v.V, Volt(doc, v.Node, v.Wire), $"V({v.Node}.{v.Wire})");
    }

    // ---- SWER -----------------------------------------------------------------------------------------------------------

    private static string SwerCase(string line, Complex zLoad)
    {
        Complex s = SFor(zLoad, 12700.0);
        return "{\"schema\":\"gridsim-dist3ph-mesh/1\",\"name\":\"swer\",\"swer\":{\"emf_v\":12700,\"source_earth_r_ohm\":5,\"line\":" + line
            + ",\"load_earth_r_ohm\":10,\"load\":{\"p_w\":" + R(s.Real) + ",\"q_var\":" + R(s.Imaginary) + ",\"nominal_v\":12700}}}";
    }

    private static string LineData(bool charging) =>
        "{\"length_km\":40,\"frequency_hz\":50,\"earth_resistivity_ohm_m\":100,\"conductor\":" + Conductor + (charging ? ",\"charging\":true" : "") + "}";

    private const string Conductor = """{"resistance_ohm_per_km":1.0,"gmr_m":0.0015,"radius_m":0.002,"height_m":10}""";

    private static (Complex z, double c) CoreLine(double km)
    {
        (Complex zKm, double cKm) = Swer.LineConstants(new ConductorData("W", ConductorRole.Phase, 0.0, 10.0, 1.0, 0.0015, 0.002), 50.0, 100.0);
        return (zKm * km, cKm * km);
    }

    [Fact]
    public void A_swer_line_from_conductor_data_obeys_the_series_loop_equation_with_the_earth_return_impedance()
    {
        (Complex zLine, _) = CoreLine(40.0);
        Assert.True(zLine.Real > 40.0, "the earth-return resistance adds to the conductor resistance");
        Complex zLoad = C(600, 200), rs = 5.0, rl = 10.0;
        JsonElement doc = Run(SwerCase(LineData(false), zLoad), out _);

        // Loop equation: E = I (Z_line + R_source_earth + R_load_earth + Z_load).
        Complex i = 12700.0 / (zLine + rs + rl + zLoad);
        Near(i, IFrom(doc, "line", "a"), 1e-8, "line current");
        Near((zLoad + rl) * i, Volt(doc, "L", "a"), 1e-6, "load-end conductor voltage");
        Near(rl * i, Volt(doc, "L", "e"), 1e-7, "load electrode potential rise");
        Near(12700.0 - rs * i, Volt(doc, "S", "a"), 1e-6, "source-end voltage");
        JsonElement earth = doc.GetProperty("tables").GetProperty("earthing")[0];
        Near(i, new Complex(earth.GetProperty("i_re").GetDouble(), earth.GetProperty("i_im").GetDouble()), 1e-8, "current into the load electrode");

        // The Core builder on the same numbers, bit for bit.
        MeshedResult core = Newton3Ph.Solve(Swer.Feeder(12700.0, rs, zLine, Complex.Zero, rl, SFor(zLoad, 12700.0), 12700.0), new MeshedOptions { ToleranceAmps = 1e-9 });
        foreach (WireVoltage v in core.Voltages) SameBits(v.V, Volt(doc, v.Node, v.Wire), $"V({v.Node}.{v.Wire})");
    }

    [Fact]
    public void Swer_line_charging_matches_the_closed_form_pi_section()
    {
        (Complex zLine, double cLine) = CoreLine(40.0);
        Complex ysh = C(0, 2.0 * Math.PI * 50.0 * cLine), zLoad = C(600, 200), rs = 5.0, rl = 10.0;
        JsonElement doc = Run(SwerCase(LineData(true), zLoad), out _);
        Complex yl = 1 / (zLoad + rl) + ysh / 2;
        Complex zin = zLine + 1 / yl;
        Complex yin = 1 / zin + ysh / 2;
        Complex vs = 12700.0 / (1 + rs * yin);
        Complex vl = vs / (zin * yl);
        Near(vs, Volt(doc, "S", "a"), 1e-5, "source-end voltage");
        Near(vl, Volt(doc, "L", "a"), 1e-5, "load-end voltage");
        Near((vs - vl) / zLine + ysh / 2 * vs, IFrom(doc, "line", "a"), 1e-7, "From-end line current");
    }

    [Fact]
    public void A_swer_line_given_as_a_total_impedance_gives_the_same_loop_current_as_the_equation()
    {
        Complex z = C(35.5, 91.25), zLoad = C(600, 200);
        JsonElement doc = Run(SwerCase("{\"r_ohm\":" + R(z.Real) + ",\"x_ohm\":" + R(z.Imaginary) + "}", zLoad), out _);
        Near(12700.0 / (z + 5.0 + 10.0 + zLoad), IFrom(doc, "line", "a"), 1e-8, "line current");
    }

    // ---- determinism and refusals -----------------------------------------------------------------------------------------

    [Fact]
    public void Two_runs_give_identical_bytes()
    {
        string c = Write("det.json", SplitCase(string.Join(",", LineLine("h1", "L", "l1", "n", C(6.0, 1.0), 120.0), LineLine("h2", "L", "l2", "n", C(12.0, 2.0), 120.0))));
        string o = Path.Combine(_dir, "det-out.json");
        CliProcess.Result a = CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o);
        byte[] first = File.ReadAllBytes(o);
        File.Delete(o);   // the same result path both times: the file records where it was written
        CliProcess.Result b = CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
        Assert.Equal(first, File.ReadAllBytes(o));
    }

    [Theory]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","nodes":[{"name":"S","wires":["l1","l2","n"]}],"split_phase_sources":[{"node":"S","volts":0}]}""", "volts")]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","nodes":[{"name":"S","wires":["l1","l2","n"]}],"split_phase_sources":[{"node":"S","volts":240,"colour":"red"}]}""", "colour")]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","nodes":[{"name":"S","wires":["a"]}],"sources":[{"node":"S","wires":["a"],"magnitude_v":[1],"angle_deg":[0]}],"centre_tap_transformers":[{"name":"t","primary_node":"S","primary_wire":"a","primary_volts":0,"secondary_node":"S","secondary_volts":240,"primary_r_ohm":1,"half_r_ohm":1}]}""", "above 0")]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","nodes":[{"name":"S","wires":["a"]}],"swer":{"emf_v":1,"line":{"r_ohm":1},"load":{"p_w":1,"q_var":0,"nominal_v":1}}}""", "complete single-wire")]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","swer":{"emf_v":12700,"line":{"length_km":10,"frequency_hz":50,"earth_resistivity_ohm_m":-1,"conductor":{"resistance_ohm_per_km":1,"gmr_m":0.0015,"radius_m":0.002,"height_m":10}},"load":{"p_w":1,"q_var":0,"nominal_v":1}}}""", "above 0")]
    [InlineData("""{"schema":"gridsim-dist3ph-mesh/1","name":"x","swer":{"emf_v":12700,"line":{"r_ohm":1},"load":{"p_w":1,"q_var":0,"nominal_v":1},"bogus":1}}""", "bogus")]
    public void A_bad_split_phase_or_swer_member_is_refused_by_name_with_exit_1(string text, string mention)
    {
        string c = Write("bad-" + Guid.NewGuid().ToString("N") + ".json", text);
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("error: dist3ph-mesh", r.StdErr, StringComparison.Ordinal);
        Assert.Contains(mention, r.StdErr, StringComparison.Ordinal);
    }
}
