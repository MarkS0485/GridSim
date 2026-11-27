using System.Text.Json;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Nodal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-16: <c>gridsim emt3</c> as the built CLI. The CLI is a strict reader and printer over the Core runner, so the
/// oracles are the Core runner called directly on the same study (bit-identical traces), the positive-sequence fault
/// current closed form, and the named failure paths.
/// </summary>
public sealed class R416Emt3CliTests : IDisposable
{
    private const string Fault = """
        {"schema":"gridsim-emt3/1","time_step_s":5e-6,"duration_s":0.05,"sample_every":4,"frequency_hz":50,
         "buses":["S","F"],
         "sources":[{"name":"G","bus":"S","v_ll_rms":11000,"rs_ohm":0.5,"ls_h":0.01}],
         "branches":[{"name":"L","from":"S","to":"F","r1_ohm":1.0,"l1_h":0.02,"r0_ohm":3.0,"l0_h":0.06}],
         "faults":[{"name":"FLT","bus":"F","phases":"abc","r_ohm":0.01,"at_s":0.0205}],
         "outputs":[{"name":"ia","kind":"current","target":"L","phase":"a"},{"name":"vf","kind":"voltage","target":"F","phase":"a"}]}
        """;

    private const string Arrester = """
        {"schema":"gridsim-emt3/1","time_step_s":1e-4,"duration_s":0.002,
         "buses":["S"],
         "sources":[{"name":"G","bus":"S","v_ll_rms":34641.0,"rs_ohm":50.0}],
         "arresters":[{"name":"ZnO","bus":"S","phases":"a","model":"power-law","vref_v":10000,"iref_a":1.0,"alpha":30}],
         "newton":{"max_iterations":NEWTON},
         "outputs":[{"name":"va","kind":"voltage","target":"S","phase":"a"}]}
        """;

    private readonly string _dir = TestTemp.Under("gridsim-r416-" + Guid.NewGuid().ToString("N"));

    public R416Emt3CliTests() => Directory.CreateDirectory(_dir);

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
    public void The_cli_traces_are_bit_identical_to_the_core_runner_and_repeat()
    {
        string f = Write("fault.json", Fault);
        string o1 = Path.Combine(_dir, "o1.json"), o2 = Path.Combine(_dir, "o2.json");
        CliProcess.Result r1 = CliProcess.RunBuilt("emt3", f, "--json", o1);
        Assert.True(r1.ExitCode == 0, r1.ToString());
        Assert.Equal(0, CliProcess.RunBuilt("emt3", f, "--json", o2).ExitCode);
        JsonElement doc = Read(o1);
        Assert.Equal("emt3", doc.GetProperty("verb").GetString());
        JsonElement s = doc.GetProperty("summary");
        Assert.Equal("Completed", s.GetProperty("status").GetString());
        Assert.Equal("gridsim-emt3/1", s.GetProperty("schema").GetString());
        Assert.Equal(10000, s.GetProperty("steps").GetInt64());
        Assert.Equal(2, s.GetProperty("refactorisations").GetInt32());   // initial assembly and the fault event
        JsonElement traces = doc.GetProperty("tables").GetProperty("traces");

        var study = new Emt3Study(5e-6, 0.05, 4, 50, IntegrationRule.Trapezoidal, NodalEventRule.Trapezoidal, new NewtonPolicy(),
            ["S", "F"], [new Emt3Source("G", "S", 11000, 50, 0, 0.5, 0.01)], [new Emt3Branch("L", "S", "F", 1.0, 0.02, 3.0, 0.06)], [], [], [], [], [], [],
            [new Emt3Fault("FLT", "F", "abc", 0.01, true, 0.0205, null)],
            [new Emt3Output("ia", "current", "L", "a", "from"), new Emt3Output("vf", "voltage", "F", "a", "from")]);
        NodalResult core = Emt3Runner.Run(study).Run;
        Assert.Equal(core.Times.Length, traces.GetArrayLength());
        for (int i = 0; i < core.Times.Length; i++)
        {
            JsonElement row = traces[i];
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Times[i]), BitConverter.DoubleToInt64Bits(row.GetProperty("time_s").GetDouble()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[0][i]), BitConverter.DoubleToInt64Bits(row.GetProperty("ia").GetDouble()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(core.Series[1][i]), BitConverter.DoubleToInt64Bits(row.GetProperty("vf").GetDouble()));
        }
        // the whole trace table repeats exactly between two process runs
        Assert.Equal(traces.GetRawText(), Read(o2).GetProperty("tables").GetProperty("traces").GetRawText());
        // steady fault current peak from the positive-sequence closed form (derived here): Vpk / |R + jwL|
        double w = 2 * Math.PI * 50, rt = 0.5 + 1.0 + 0.01, lt = 0.03;
        double peak = 11000 * Math.Sqrt(2.0 / 3.0) / Math.Sqrt(rt * rt + w * w * lt * lt);
        Assert.InRange(s.GetProperty("max_abs_ia").GetDouble() / peak, 1.0, 1.9);   // between the steady peak and full offset
    }

    [Fact]
    public void A_power_law_arrester_study_runs_and_a_starved_newton_policy_exits_3_with_a_named_status()
    {
        string ok = Write("ok.json", Arrester.Replace("NEWTON", "100"));
        string oOk = Path.Combine(_dir, "ok-out.json");
        Assert.Equal(0, CliProcess.RunBuilt("emt3", ok, "--json", oOk).ExitCode);
        JsonElement s = Read(oOk).GetProperty("summary");
        Assert.Equal("Completed", s.GetProperty("status").GetString());
        Assert.True(s.GetProperty("newton_iterations").GetInt64() > 0);
        double vmax = s.GetProperty("max_abs_va").GetDouble();
        Assert.InRange(vmax, 9000, 20000);   // clamped near the reference voltage, well below the 28 kV crest

        string bad = Write("bad.json", Arrester.Replace("NEWTON", "2"));
        string oBad = Path.Combine(_dir, "bad-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("emt3", bad, "--json", oBad);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("did not converge", r.StdErr);
        JsonElement sb = Read(oBad).GetProperty("summary");
        Assert.Equal("NewtonNotConverged", sb.GetProperty("status").GetString());
        Assert.True(sb.GetProperty("failed_at_s").GetDouble() > 0);
    }

    [Fact]
    public void Bad_input_exits_1_and_bad_usage_exits_2()
    {
        string o = Path.Combine(_dir, "x.json");
        Assert.Equal(1, CliProcess.RunBuilt("emt3", Path.Combine(_dir, "missing.json"), "--json", o).ExitCode);
        string unknown = Write("unknown.json", Fault.Replace("\"sample_every\":4", "\"sample_evry\":4"));
        CliProcess.Result r = CliProcess.RunBuilt("emt3", unknown, "--json", o);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("sample_evry", r.StdErr);
        string badBus = Write("badbus.json", Fault.Replace("\"bus\":\"F\",\"phases\"", "\"bus\":\"Q\",\"phases\""));
        CliProcess.Result rb = CliProcess.RunBuilt("emt3", badBus, "--json", o);
        Assert.Equal(1, rb.ExitCode);
        Assert.Contains("no bus 'Q'", rb.StdErr);
        Assert.Equal(2, CliProcess.RunBuilt("emt3", Write("f.json", Fault), "--bogus", "1").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("emt3").ExitCode);
    }
}
