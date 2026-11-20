using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 (last candidate): <c>dist3ph-mesh</c> with step-voltage regulators, transformer banks and capacitor banks in its case file. Core already
/// models all three (<see cref="RegulatedFrame"/>, <see cref="RegulatorTapSearch"/>, <see cref="BankSpec"/>); the CLI wires them in. The oracles are
/// the closed forms the Core tests use (a wye regulator at a fixed tap, the hand-derived line-drop-compensator tap), the divider equation for a
/// capacitor bank and its switching, and the Core solver called directly on the same elements (the CLI must add and lose nothing, bit for bit).
/// </summary>
public sealed class R415cDist3phMeshRegulatorsBanksCliTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string R(double v) => v.ToString("R", Inv);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-r415c-" + Guid.NewGuid().ToString("N"));

    public R415cDist3phMeshRegulatorsBanksCliTests() => Directory.CreateDirectory(_dir);

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

    private static JsonElement Read(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    private static Complex Volt(JsonElement doc, string node, string wire, string? step = null)
    {
        foreach (JsonElement row in doc.GetProperty("tables").GetProperty("voltages").EnumerateArray())
            if (row.GetProperty("node").GetString() == node && row.GetProperty("wire").GetString() == wire
                && (step is null || row.GetProperty("step").GetString() == step))
                return new Complex(row.GetProperty("v_re").GetDouble(), row.GetProperty("v_im").GetDouble());
        throw new KeyNotFoundException($"{node}.{wire}");
    }

    private const double Vln = 7200.0;
    private static readonly string[] Abc = ["a", "b", "c"];
    private const string ThreePhaseSource = """{"node":"S","wires":["a","b","c"],"magnitude_v":[7200,7200,7200],"angle_deg":[0,-120,120]}""";
    private const string Nodes = """{"name":"S","wires":["a","b","c"]},{"name":"L","wires":["a","b","c"]}""";

    private static Complex[] Source() => Abc.Select((_, i) => DetMath.FromPolar(Vln, new[] { 0.0, -120.0, 120.0 }[i] * Math.PI / 180.0)).ToArray();

    private static string ConstantZLoad(Complex zl)
    {
        Complex s = Vln * Vln / Complex.Conjugate(zl);
        return $$"""{"name":"ld","node":"L","connection":"wye-earthed","phases":["a","b","c"],"nominal_v":7200,"p_w":[{{R(s.Real)}},{{R(s.Real)}},{{R(s.Real)}}],"q_var":[{{R(s.Imaginary)}},{{R(s.Imaginary)}},{{R(s.Imaginary)}}],"zip":[0,0,1]}""";
    }

    private static string RegulatorCase(string regulators, string load)
        => $$"""{"schema":"gridsim-dist3ph-mesh/1","name":"reg","nodes":[{{Nodes}}],"sources":[{{ThreePhaseSource}}],"regulators":[{{regulators}}],"loads":[{{load}}]}""";

    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(16)]
    public void A_wye_regulator_at_a_fixed_tap_gives_the_hand_derived_voltage_and_the_core_result_bit_for_bit(int tap)
    {
        Complex zl = new(4.0, 1.5);
        string c = Write("reg.json", RegulatorCase($$"""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a","b","c"],"taps":[{{tap}},{{tap}},{{tap}}]}""", ConstantZLoad(zl)));
        string o = Path.Combine(_dir, "out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement doc = Read(o);

        // V_to = r V_s / (1 + zw (r^2 + 1) / Zl), zw the default 1e-5 ohm winding impedance (the closed form of R415bTransformerRegulatorTests).
        double ratio = 1.0 + 0.00625 * tap, zw = 1e-5;
        Complex[] vs = Source();
        for (int p = 0; p < 3; p++)
        {
            Complex expected = ratio * vs[p] / (1.0 + zw * (ratio * ratio + 1.0) / zl);
            Assert.True((expected - Volt(doc, "L", Abc[p])).Magnitude < 1e-4, $"phase {Abc[p]}: expected {expected} got {Volt(doc, "L", Abc[p])}");
        }
        JsonElement regs = doc.GetProperty("tables").GetProperty("regulators");
        Assert.Equal(3, regs.GetArrayLength());
        Assert.Equal(tap, regs[0].GetProperty("tap").GetInt32());
        Assert.Equal(ratio, regs[0].GetProperty("ratio").GetDouble(), 12);

        // The CLI adds and loses nothing: the same elements through Core, bit for bit (the tolerance of the run above, 40 iterations; the 1e-5 ohm
        // regulator winding limits the attainable current mismatch, as the 1e-5 A tolerance of the Core regulator tests reflects).
        string o2 = o;
        Complex s = Vln * Vln / Complex.Conjugate(zl);
        var frame = new RegulatedFrame("reg", [new FrameNode("S", Abc), new FrameNode("L", Abc)], [new FrameSource("S", Abc, vs)], [],
            [FrameLoad.WyeEarthed("ld", "L", [s, s, s], Vln, Zip.ConstantImpedance, Abc)],
            regulators: [new StepRegulator("RG", "S", "L", RegulatorConnection.Wye, Abc, [tap, tap, tap])]);
        MeshedResult core = Newton3Ph.Solve(frame.Build([[tap, tap, tap]]), new MeshedOptions { ToleranceAmps = 1e-5, MaxIterations = 40 });
        JsonElement doc2 = Read(o2);
        foreach (string w in Abc)
        {
            Complex v = Volt(doc2, "L", w), e = core.V("L", w);
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Real), BitConverter.DoubleToInt64Bits(v.Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(e.Imaginary), BitConverter.DoubleToInt64Bits(v.Imaginary));
        }
    }

    [Fact]
    public void An_open_delta_regulator_is_the_core_result_bit_for_bit()
    {
        Complex zl = new(4.0, 1.5);
        string c = Write("od.json", RegulatorCase("""{"name":"RO","from":"S","to":"L","connection":"open-delta","taps":[3,-2]}""", ConstantZLoad(zl)));
        string o = Path.Combine(_dir, "od-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        Complex s = Vln * Vln / Complex.Conjugate(zl);
        var frame = new RegulatedFrame("reg", [new FrameNode("S", Abc), new FrameNode("L", Abc)], [new FrameSource("S", Abc, Source())], [],
            [FrameLoad.WyeEarthed("ld", "L", [s, s, s], Vln, Zip.ConstantImpedance, Abc)],
            regulators: [new StepRegulator("RO", "S", "L", RegulatorConnection.OpenDelta, [], [3, -2])]);
        MeshedResult core = Newton3Ph.Solve(frame.Build([[3, -2]]), new MeshedOptions { ToleranceAmps = 1e-5, MaxIterations = 40 });
        JsonElement doc = Read(o);
        foreach (string w in Abc)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.V("L", w).Real), BitConverter.DoubleToInt64Bits(Volt(doc, "L", w).Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.V("L", w).Imaginary), BitConverter.DoubleToInt64Bits(Volt(doc, "L", w).Imaginary));
        }
        JsonElement regs = doc.GetProperty("tables").GetProperty("regulators");
        Assert.Equal(new[] { "ab", "cb" }, regs.EnumerateArray().Select(x => x.GetProperty("phase").GetString()!).ToArray());
        Assert.Equal(new[] { 3, -2 }, regs.EnumerateArray().Select(x => x.GetProperty("tap").GetInt32()).ToArray());
    }

    [Fact]
    public void The_compensator_search_finds_the_tap_computed_by_hand_is_deterministic_and_can_be_held()
    {
        // The hand derivation of R415bTransformerRegulatorTests: a regulator straight onto a constant-current load (400 A at 0.9 pf lagging),
        // PT 60, CT 700, R 3, X 7.5, level 123 V, band 2 V: the tap is the smallest t >= 0 with |120 r(t) - D| >= 122.
        double pt = 60.0, ct = 700.0, rSet = 3.0, xSet = 7.5, vSet = 123.0, bw = 2.0, i0 = 400.0, pf = 0.9;
        double phi = Math.Acos(pf);
        Complex d = new Complex(rSet, xSet) * Complex.FromPolarCoordinates(i0, -phi) / ct;
        double lo = vSet - bw / 2;
        double rStar = (d.Real + Math.Sqrt(lo * lo - d.Imaginary * d.Imaginary)) / (Vln / pt);
        int expected = (int)Math.Ceiling((rStar - 1.0) / 0.00625 - 1e-12);
        Assert.InRange(expected, 1, 15);

        string ldc = $$"""{"vset_volts":123,"bandwidth_volts":2,"pt_ratio":60,"ct_primary_amps":700,"r_volts":3,"x_volts":7.5}""";
        string load = $$"""{"name":"ld","node":"L","connection":"wye-earthed","phases":["a","b","c"],"nominal_v":7200,"p_w":[{{R(Vln * i0 * pf)}},{{R(Vln * i0 * pf)}},{{R(Vln * i0 * pf)}}],"q_var":[{{R(Vln * i0 * Math.Sin(phi))}},{{R(Vln * i0 * Math.Sin(phi))}},{{R(Vln * i0 * Math.Sin(phi))}}],"zip":[0,1,0]}""";
        string c = Write("ldc.json", RegulatorCase($$"""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a","b","c"],"taps":[0,0,0],"ganged":true,"ldc":[{{ldc}},{{ldc}},{{ldc}}]}""", load));
        string a = Path.Combine(_dir, "out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--json", a);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("tap search: SETTLED", r.StdOut, StringComparison.Ordinal);
        byte[] first = File.ReadAllBytes(a);
        File.Delete(a);
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--json", a).ExitCode);
        Assert.Equal(first, File.ReadAllBytes(a));   // two runs, identical bytes

        JsonElement doc = Read(a);
        Assert.Equal("SETTLED", doc.GetProperty("summary").GetProperty("tap_search").GetString());
        JsonElement regs = doc.GetProperty("tables").GetProperty("regulators");
        foreach (JsonElement row in regs.EnumerateArray())
        {
            Assert.Equal(expected, row.GetProperty("tap").GetInt32());
            Assert.True(row.GetProperty("in_band").GetBoolean());
            double relay = Complex.Abs(Vln * (1 + 0.00625 * expected) / pt - d);
            Assert.Equal(relay, row.GetProperty("relay_v").GetDouble(), 3);
        }
        Assert.Equal(expected + 1, doc.GetProperty("summary").GetProperty("tap_search_load_flows").GetInt32());

        // --hold-taps keeps tap 0 and reports the phase below its band: exit 3.
        string h = Path.Combine(_dir, "hold.json");
        CliProcess.Result hold = CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--hold-taps", "--json", h);
        Assert.Equal(3, hold.ExitCode);
        Assert.Contains("outside band", hold.StdOut, StringComparison.Ordinal);
        Assert.Equal(0, Read(h).GetProperty("tables").GetProperty("regulators")[0].GetProperty("tap").GetInt32());

        // A search with --steps is refused (exit 2); with --hold-taps the steps run at the written taps.
        string steps = Write("steps.json", """{"schema":"gridsim-dist3ph-mesh-steps/1","steps":[{"label":"t0"},{"label":"t1","scale":0.5}]}""");
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", steps).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-5", "--steps", steps, "--hold-taps").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", Write("fixed.json", RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a"],"taps":[1]}""", ConstantZLoad(new Complex(4, 1.5)))), "--hold-taps").ExitCode);
    }

    private const string OnePhase = """
        {"schema":"gridsim-dist3ph-mesh/1","name":"cap",
         "nodes":[{"name":"S","wires":["a"]},{"name":"L","wires":["a"]}],
         "sources":[{"node":"S","wires":["a"],"magnitude_v":[1000],"angle_deg":[0]}],
         "branches":[{"name":"line","from":"S","to":"L","wires":["a"],"r_ohm":[[1]],"x_ohm":[[2]]}],
         "loads":[{"name":"ld","node":"L","connection":"wye-earthed","phases":["a"],"nominal_v":1000,"p_w":[100000],"q_var":[0],"zip":[0,0,1]}],
         "capacitors":[{"name":"C1","node":"L","phases":["a"],"q_var":[20000],"nominal_v":1000}]}
        """;

    private static Complex Divider(double loadScale, bool capacitor)
    {
        // Load: constant impedance 10 ohm at scale 1 (admittance 0.1 S times the scale); capacitor: current j Q V / Vnom^2 = j 0.02 S V.
        Complex y = loadScale * 0.1 + (capacitor ? new Complex(0, 0.02) : Complex.Zero);
        Complex zp = 1.0 / y;
        return 1000.0 * zp / (new Complex(1, 2) + zp);
    }

    [Fact]
    public void A_capacitor_bank_raises_the_voltage_by_the_divider_equation_and_is_not_scaled_by_the_load_factors()
    {
        string c = Write("cap.json", OnePhase);
        string o = Path.Combine(_dir, "cap-out.json");
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-10", "--json", o).ExitCode);
        Assert.True((Divider(1.0, true) - Volt(Read(o), "L", "a")).Magnitude < 1e-6);

        // --scale halves the load and leaves the capacitor alone.
        string o2 = Path.Combine(_dir, "cap-half.json");
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-10", "--scale", "0.5", "--json", o2).ExitCode);
        Assert.True((Divider(0.5, true) - Volt(Read(o2), "L", "a")).Magnitude < 1e-6);

        // Switched: out (0), half load with the bank in, and in.
        string steps = Write("capsteps.json", """{"schema":"gridsim-dist3ph-mesh-steps/1","steps":[{"label":"off","capacitors":{"C1":0}},{"label":"half","scale":0.5},{"label":"on"},{"label":"off-half","scale":0.5,"capacitors":{"C1":0},"load_scale":{}}]}""");
        string o3 = Path.Combine(_dir, "cap-steps.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", steps, "--tol", "1e-10", "--json", o3);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement doc = Read(o3);
        Assert.True((Divider(1.0, false) - Volt(doc, "L", "a", "off")).Magnitude < 1e-6);
        Assert.True((Divider(0.5, true) - Volt(doc, "L", "a", "half")).Magnitude < 1e-6);
        Assert.True((Divider(1.0, true) - Volt(doc, "L", "a", "on")).Magnitude < 1e-6);
        Assert.True((Divider(0.5, false) - Volt(doc, "L", "a", "off-half")).Magnitude < 1e-6);
        // The bank raises the voltage, as a shunt capacitor does.
        Assert.True(Volt(doc, "L", "a", "on").Magnitude > Volt(doc, "L", "a", "off").Magnitude);

        // A step that is the same as a single run is bit-identical to it (the default of independent steps is kept).
        string single = Path.Combine(_dir, "cap-single.json");
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--tol", "1e-10", "--json", single).ExitCode);
        Assert.Equal(BitConverter.DoubleToInt64Bits(Volt(Read(single), "L", "a").Real), BitConverter.DoubleToInt64Bits(Volt(doc, "L", "a", "on").Real));
    }

    [Fact]
    public void A_zero_load_factor_with_a_capacitor_in_the_case_is_refused_and_bad_capacitor_input_is_an_input_error()
    {
        string c = Write("cap.json", OnePhase);
        CliProcess.Result zero = CliProcess.RunBuilt("dist3ph-mesh", c, "--scale", "0");
        Assert.Equal(1, zero.ExitCode);
        Assert.Contains("capacitor", zero.StdErr, StringComparison.Ordinal);
        string badState = Write("badstate.json", """{"schema":"gridsim-dist3ph-mesh-steps/1","steps":[{"label":"x","capacitors":{"C1":0.5}}]}""");
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", badState).ExitCode);
        string unknown = Write("unknown.json", """{"schema":"gridsim-dist3ph-mesh-steps/1","steps":[{"label":"x","capacitors":{"C9":0}}]}""");
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", unknown).ExitCode);
        string neg = Write("neg.json", OnePhase.Replace("\"q_var\":[20000]", "\"q_var\":[-20000]"));
        CliProcess.Result n = CliProcess.RunBuilt("dist3ph-mesh", neg);
        Assert.Equal(1, n.ExitCode);
        Assert.Contains("must not be negative", n.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grounded_wye_bank_is_the_core_result_bit_for_bit_with_the_turns_ratio_between_its_sides()
    {
        string load = """{"name":"ld","node":"M","connection":"wye-earthed","phases":["a","b","c"],"nominal_v":2401.7771,"p_w":[300000,300000,300000],"q_var":[100000,100000,100000],"zip":[0,0,1]}""";
        string bank = """{"name":"TB","from":"S","to":"M","connection":"grounded-wye-grounded-wye","kv_ll_from":12.47,"kv_ll_to":4.16,"unit_kva":500,"r_percent":1.0,"x_percent":5.0}""";
        string c = Write("bank.json", $$"""{"schema":"gridsim-dist3ph-mesh/1","name":"bank","nodes":[{"name":"S","wires":["a","b","c"]},{"name":"M","wires":["a","b","c"]}],"sources":[{{ThreePhaseSource}}],"banks":[{{bank}}],"loads":[{{load}}]}""");
        string o = Path.Combine(_dir, "bank-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());

        Complex[] vs = Source();
        var frame = new RegulatedFrame("bank", [new FrameNode("S", Abc), new FrameNode("M", Abc)], [new FrameSource("S", Abc, vs)], [],
            [FrameLoad.WyeEarthed("ld", "M", [new Complex(300000, 100000), new Complex(300000, 100000), new Complex(300000, 100000)], 2401.7771, Zip.ConstantImpedance, Abc)],
            banks: [BankSpec.Split("TB", "S", "M", BankConnection.GroundedWyeGroundedWye, 12.47, 4.16, 500, 1.0, 5.0)]);
        MeshedResult core = Newton3Ph.Solve(frame.Build([]), new MeshedOptions { ToleranceAmps = 1e-6, MaxIterations = 40 });
        JsonElement doc = Read(o);
        foreach (string w in Abc)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.V("M", w).Real), BitConverter.DoubleToInt64Bits(Volt(doc, "M", w).Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.V("M", w).Imaginary), BitConverter.DoubleToInt64Bits(Volt(doc, "M", w).Imaginary));
        }
        // Side to side the turns ratio is 4.16 / 12.47 (no phase shift): within the drop of a 5 % bank at this load.
        double ratio = Volt(doc, "M", "a").Magnitude / Vln;
        Assert.InRange(ratio, 0.97 * 4.16 / 12.47, 4.16 / 12.47 * 1.0001);
        Assert.InRange(Math.Abs(Math.Atan2(Volt(doc, "M", "a").Imaginary, Volt(doc, "M", "a").Real)), 0.0, 0.05);
    }

    [Fact]
    public void Malformed_regulator_bank_and_capacitor_members_are_input_errors_naming_the_member()
    {
        string load = ConstantZLoad(new Complex(4, 1.5));
        (string Case, string Needle)[] bad =
        [
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a","b","c"],"taps":[1,1,1],"extra":1}""", load), "unknown key 'extra'"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"delta","taps":[1]}""", load), "connection must be wye, open-delta or closed-delta"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"open-delta","phases":["a"],"taps":[1,1]}""", load), "\"phases\" is for a wye regulator only"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"closed-delta","taps":[1,2,1]}""", load), "taps must be equal"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a","b","c"],"taps":[1.5,1,1]}""", load), "must be an integer"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"wye","phases":["a","b","c"],"taps":[99,1,1]}""", load), "outside"),
            (RegulatorCase("""{"name":"RG","from":"S","to":"L","connection":"open-delta","taps":[1,1],"ldc":[{"vset_volts":120,"bandwidth_volts":2,"pt_ratio":60,"ct_primary_amps":700}]}""", load), "line-drop compensation is modelled for wye"),
            ($$"""{"schema":"gridsim-dist3ph-mesh/1","name":"b","nodes":[{{Nodes}}],"sources":[{{ThreePhaseSource}}],"banks":[{"name":"B","from":"S","to":"L","connection":"zigzag","kv_ll_from":12,"kv_ll_to":4,"unit_kva":100,"r_percent":1,"x_percent":5}]}""", "connection must be one of"),
            ($$"""{"schema":"gridsim-dist3ph-mesh/1","name":"b","nodes":[{{Nodes}}],"sources":[{{ThreePhaseSource}}],"banks":[{"name":"B","from":"S","to":"L","connection":"delta-delta","kv_ll_from":12,"kv_ll_to":4,"unit_kva":100}]}""", "give either"),
        ];
        int k = 0;
        foreach ((string text, string needle) in bad)
        {
            CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", Write($"bad{k++}.json", text));
            Assert.True(r.ExitCode == 1, $"{needle}: {r}");
            Assert.True(r.StdErr.Contains(needle, StringComparison.Ordinal), $"{needle} not in: {r.StdErr}");
        }
    }

    [Fact]
    public void A_case_without_the_new_members_is_read_and_solved_exactly_as_before()
    {
        // The R4-15 loop case (no regulators, banks or capacitors) still writes no `regulators` table and the same summary members.
        string c = Write("loop.json", """
            {"schema":"gridsim-dist3ph-mesh/1","name":"loop",
             "nodes":[{"name":"S","wires":["a","n"]},{"name":"L","wires":["a","n"]}],
             "sources":[{"node":"S","wires":["a"],"magnitude_v":[1000],"angle_deg":[0]}],
             "branches":[{"name":"line","from":"S","to":"L","wires":["a","n"],"r_ohm":[[2,0],[0,0.5]],"x_ohm":[[3,0],[0,0.5]]}],
             "loads":[{"name":"ld","node":"L","connection":"line-line","from":"a","to":"n","nominal_v":1000,"p_w":[100000],"q_var":[0],"zip":[0,0,1]}],
             "earthing":[{"node":"S","wire":"n","r_ohm":0}]}
            """);
        string o = Path.Combine(_dir, "loop-out.json");
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o).ExitCode);
        JsonElement t = Read(o).GetProperty("tables");
        Assert.False(t.TryGetProperty("regulators", out _));
        Assert.False(Read(o).GetProperty("summary").TryGetProperty("tap_search", out _));
    }
}
