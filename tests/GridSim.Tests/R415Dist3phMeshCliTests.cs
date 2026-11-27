using System.Numerics;
using System.Text.Json;
using GridSim.Core.Distribution.Meshed;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-15: <c>gridsim dist3ph-mesh</c> as the built CLI. The oracle is the hand-solved single-phase loop equation
/// (E = I (Z + R_load)) derived here, and the Core solver called directly on the same case: the CLI must add nothing and lose nothing.
/// </summary>
public sealed class R415Dist3phMeshCliTests : IDisposable
{
    // Single wire a with a 2 + j3 ohm line, a 10 ohm constant-impedance load (nominal 1000 V, S = V^2 / conj(Z)) returning through wire n,
    // neutral solidly earthed at the load. 1000 V source. Loop: I = 1000 / (2 + j3 + 10).
    private const string Case = """
        {"schema":"gridsim-dist3ph-mesh/1","name":"loop",
         "nodes":[{"name":"S","wires":["a","n"]},{"name":"L","wires":["a","n"]}],
         "sources":[{"node":"S","wires":["a"],"magnitude_v":[1000],"angle_deg":[0]}],
         "branches":[{"name":"line","from":"S","to":"L","wires":["a","n"],"r_ohm":[[2,0],[0,0.5]],"x_ohm":[[3,0],[0,0.5]]}],
         "loads":[{"name":"ld","node":"L","connection":"line-line","from":"a","to":"n","nominal_v":1000,"p_w":[100000],"q_var":[0],"zip":[0,0,1]}],
         "earthing":[{"node":"S","wire":"n","r_ohm":0}]}
        """;

    private readonly string _dir = TestTemp.Under("gridsim-r415-" + Guid.NewGuid().ToString("N"));

    public R415Dist3phMeshCliTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void The_loop_equation_and_the_core_solver_agree_with_the_cli_result_file()
    {
        string c = Write("loop.json", Case);
        string o = Path.Combine(_dir, "out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("CONVERGED", r.StdOut, StringComparison.Ordinal);
        JsonElement doc = Read(o);
        Assert.Equal("dist3ph-mesh", doc.GetProperty("verb").GetString());
        Assert.True(doc.GetProperty("summary").GetProperty("all_converged").GetBoolean());
        Assert.Equal(64, doc.GetProperty("summary").GetProperty("case_sha256").GetString()!.Length);

        // Loop equation: the load is 10 ohm (1000^2 / 100000), the neutral wire carries the same current back (0.5 + j0.5 ohm in its own
        // wire), so I = E / (2 + j3 + 0.5 + j0.5 + 10).
        Complex i = 1000.0 / (new Complex(2, 3) + new Complex(0.5, 0.5) + 10.0);
        JsonElement bc = doc.GetProperty("tables").GetProperty("branch_currents");
        JsonElement rowA = bc.EnumerateArray().First(x => x.GetProperty("wire").GetString() == "a");
        Assert.Equal(i.Real, rowA.GetProperty("i_from_re").GetDouble(), 1e-9);
        Assert.Equal(i.Imaginary, rowA.GetProperty("i_from_im").GetDouble(), 1e-9);
        JsonElement rowN = bc.EnumerateArray().First(x => x.GetProperty("wire").GetString() == "n");
        Assert.Equal(-i.Real, rowN.GetProperty("i_from_re").GetDouble(), 1e-9);

        // The CLI adds and loses nothing: the same case through Core, bit for bit.
        var model = new PhaseFrameModel("loop", [new FrameNode("S", ["a", "n"]), new FrameNode("L", ["a", "n"])],
            [new FrameSource("S", ["a"], [GridSim.Core.Numerics.DetMath.FromPolar(1000.0, 0.0)])],
            [new FrameBranch("line", "S", "L", ["a", "n"], new Complex[,] { { new Complex(2, 3), 0 }, { 0, new Complex(0.5, 0.5) } })],
            [FrameLoad.LineLine("ld", "L", "a", "n", 100000.0, 1000.0, Zip.ConstantImpedance)],
            [new EarthingElement("S", "n", Complex.Zero)]);
        MeshedResult core = Newton3Ph.Solve(model, new MeshedOptions { ToleranceAmps = 1e-6, MaxIterations = 40 });
        JsonElement v = doc.GetProperty("tables").GetProperty("voltages");
        foreach (JsonElement row in v.EnumerateArray())
        {
            Complex expected = core.V(row.GetProperty("node").GetString()!, row.GetProperty("wire").GetString()!);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Real), BitConverter.DoubleToInt64Bits(row.GetProperty("v_re").GetDouble()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Imaginary), BitConverter.DoubleToInt64Bits(row.GetProperty("v_im").GetDouble()));
        }
    }

    [Fact]
    public void Repeat_runs_write_identical_results_and_steps_are_independent_by_default()
    {
        string c = Write("loop.json", Case);
        string s = Write("steps.json", """{"schema":"gridsim-dist3ph-mesh-steps/1","steps":[{"label":"low","scale":0.5},{"label":"full"},{"label":"high","scale":1.5,"load_scale":{"ld":2}}]}""");
        Directory.CreateDirectory(Path.Combine(_dir, "r1")); Directory.CreateDirectory(Path.Combine(_dir, "r2"));
        // Same file name in two folders: the result records its own output name, so equal bytes need equal names.
        string a = Path.Combine(_dir, "r1", "out.json"), b = Path.Combine(_dir, "r2", "out.json"), single = Path.Combine(_dir, "single.json");
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", s, "--json", a).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--steps", s, "--json", b).ExitCode);
        Assert.Equal(Read(a).GetProperty("tables").GetRawText(), Read(b).GetProperty("tables").GetRawText());
        Assert.Equal(Read(a).GetProperty("summary").GetRawText(), Read(b).GetProperty("summary").GetRawText());
        // Step "high" alone: scale 1.5 and load factor 2 is not expressible by flags, but "full" alone (--scale 1) must equal the "full" step.
        Assert.Equal(0, CliProcess.RunBuilt("dist3ph-mesh", c, "--json", single).ExitCode);
        JsonElement steps = Read(a).GetProperty("tables").GetProperty("voltages");
        JsonElement alone = Read(single).GetProperty("tables").GetProperty("voltages");
        var fullRows = steps.EnumerateArray().Where(x => x.GetProperty("step").GetString() == "full").ToList();
        Assert.Equal(alone.GetArrayLength(), fullRows.Count);
        for (int k = 0; k < fullRows.Count; k++)
            Assert.Equal(alone[k].GetProperty("v_re").GetDouble(), fullRows[k].GetProperty("v_re").GetDouble());
        Assert.Equal(3, Read(a).GetProperty("tables").GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public void An_incomplete_case_names_its_reason_and_exits_3_with_no_voltages()
    {
        // The neutral of a four-wire line is earthed nowhere: the case is INCOMPLETE, not a number.
        string c = Write("float.json", Case.Replace("\"earthing\":[{\"node\":\"S\",\"wire\":\"n\",\"r_ohm\":0}]", "\"earthing\":[]")
            .Replace("\"connection\":\"line-line\",\"from\":\"a\",\"to\":\"n\"", "\"connection\":\"wye-earthed\",\"phases\":[\"a\"]"));
        string o = Path.Combine(_dir, "inc.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", c, "--json", o);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("INCOMPLETE", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("no reference", r.StdOut, StringComparison.Ordinal);
        JsonElement doc = Read(o);
        Assert.False(doc.GetProperty("summary").GetProperty("all_converged").GetBoolean());
        Assert.Equal(0, doc.GetProperty("tables").GetProperty("voltages").GetArrayLength());
    }

    [Fact]
    public void Unusable_input_is_exit_1_and_bad_usage_is_exit_2()
    {
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Path.Combine(_dir, "missing.json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("schema.json", Case.Replace("dist3ph-mesh/1", "dist3ph-mesh/2"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("key.json", Case.Replace("\"name\":\"loop\",", "\"name\":\"loop\",\"colour\":1,"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("dup.json", Case.Replace("\"name\":\"loop\",", "\"name\":\"loop\",\"name\":\"again\","))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("node.json", Case.Replace("\"to\":\"L\"", "\"to\":\"Z\""))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("conn.json", Case.Replace("line-line", "star"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("zip.json", Case.Replace("[0,0,1]", "[0.5,0.5,0.5]"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", Write("notjson.json", "{ nope")).ExitCode);
        string good = Write("good.json", Case);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", good, good).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", good, "--top", "3").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", good, "--tol", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", good, "--max-iter", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", good, "--warm-start").ExitCode);   // needs --steps
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", good, "--steps", Path.Combine(_dir, "nosteps.json")).ExitCode);
    }

    [Fact]
    public void Help_names_the_flags_and_the_verb_is_listed()
    {
        CliProcess.Result h = CliProcess.RunBuilt("dist3ph-mesh", "--help");
        Assert.Equal(0, h.ExitCode);
        foreach (string flag in new[] { "--steps", "--warm-start", "--tol", "--max-iter", "--scale", "--pin-floating" })
            Assert.Contains(flag, h.StdOut, StringComparison.Ordinal);
        Assert.Contains("dist3ph-mesh", CliProcess.RunBuilt("--help").StdOut, StringComparison.Ordinal);
    }
}
