using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Stochastic;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP12: the user-model route. A .NET plug-in interface for dynamic models (loaded only from a configured, confined,
/// hash-pinned assembly, behind a determinism lint and a replay check) and the declarative injection and load roles with
/// the <c>expr</c> block. The oracles: the coded models (TGOV1, SEXS), for bit identity of a plug-in and of an expr model
/// that re-express them; closed-form two-bus power flows and fault voltages derived in the comments (checked with an
/// independent Python evaluation of the same formulas: 0.0500837105807799 rad, 0.9987460731103327, 0.9795831523312719,
/// 1.0000204168476687, 0.33081894288367963); and the static constant-admittance load path of the RMS network. The sample
/// plug-ins are tests/GridSim.ModelPlugin.Sample (SAMPLE_TGOV1, SAMPLE_SEXS, SAMPLE_PQ, BAD_STATIC) and
/// tests/GridSim.ModelPlugin.BadSample (BAD_CLOCK), built by the solution.
/// </summary>
public sealed class Rc5Wp12UserModelRouteTests
{
    // ------------------------------------------------------------------------------------------------ fixtures

    private static string BuiltDll(string project)
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string dll = Path.Combine(CliProcess.Root(), "tests", project, "bin", tfmDir.Parent!.Name, tfmDir.Name, project + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"the sample plug-in {project} was not found; build the solution (or that project) first", dll);
        return dll;
    }

    private static string SampleDll => BuiltDll("GridSim.ModelPlugin.Sample");

    private static string BadSampleDll => BuiltDll("GridSim.ModelPlugin.BadSample");

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Sha(string path) => Sha(File.ReadAllBytes(path));

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = TestTemp.Under("gridsim-wp12-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Write a configuration listing one assembly entry per (path, sha, ids).</summary>
    private static string Config(Folder f, params (string Path, string Sha, string[] Ids)[] entries)
    {
        string path = f.File("plugins.json");
        var doc = new { schema = "gridsim-plugins/1", assemblies = entries.Select(e => new { path = e.Path, sha256 = e.Sha, models = e.Ids }).ToArray() };
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return path;
    }

    private static PluginSet LoadSample(Folder f, params string[] ids)
    {
        File.Copy(SampleDll, f.File("sample.dll"));
        return PluginLoader.Load(Config(f, ("sample.dll", Sha(f.File("sample.dll")), ids)));
    }

    private static void Same(double a, double b, string what, int k)
        => Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"{what}: step {k}: {a:R} ({BitConverter.DoubleToInt64Bits(a):X16}) vs {b:R} ({BitConverter.DoubleToInt64Bits(b):X16})");

    /// <summary>A pinned input sequence (quiet, step, large step driving the limits, ramp, random from the R4-02 counter stream).</summary>
    private static double[] Sequence(int channel, double a, int n = 50_000)
    {
        var s = new CounterStream(new StreamKey(0x5EED0412UL, "rc5-wp12/seq/" + channel));
        var x = new double[n];
        int seg = n / 5;
        for (int k = 0; k < n; k++)
        {
            int g = k / seg, j = k % seg;
            x[k] = g switch
            {
                0 => 0.0,
                1 => 0.3 * a,
                2 => -a,
                3 => a * (2.0 * j / seg - 1.0),
                _ => a * (2.0 * s.Uniform((ulong)k) - 1.0),
            };
        }
        return x;
    }

    private static Dictionary<string, double> P(params (string, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    // ------------------------------------------------------------------------------------------------ 1. bit identity

    private static readonly double[] StepSizes = { 0.001, 0.005, 0.02 };

    [Fact]
    public void A_plugin_tgov1_and_sexs_give_the_coded_models_output_bit_for_bit_at_three_step_sizes()
    {
        using var f = new Folder();
        PluginSet set = LoadSample(f, "SAMPLE_TGOV1", "SAMPLE_SEXS");
        var tg = new PluginModel(set.Find("SAMPLE_TGOV1")!, P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.02), ("Vmin", 0.0), ("Vmax", 1.2)));
        var sx = new PluginModel(set.Find("SAMPLE_SEXS")!, P(("K", 100.0), ("Ta", 0.1), ("Tb", 1.0), ("Te", 0.05), ("Emin", -5.0), ("Emax", 5.0)));
        double[] speed = Sequence(0, 0.05), verr = Sequence(1, 0.2);
        foreach (double dt in StepSizes)
        {
            var coded = new Tgov1(0.05, 0.5, 1.0, 5.0, 0.02, 0.0, 1.2);
            IRmsGovernor plug = new PluginGovernorData(tg).Create();
            coded.Initialize(0.8); plug.Initialize(0.8);
            bool hitMax = false;
            for (int k = 0; k < speed.Length; k++)
            {
                Same(coded.Step(speed[k], dt), plug.Step(speed[k], dt), $"TGOV1 dt={dt}", k);
                hitMax |= coded.ValvePosition >= 1.2 - 1e-9;
            }
            Assert.True(hitMax, "the valve limit must be exercised");

            RmsExciter codedEx = RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, -5.0, 5.0);
            RmsExciter plugEx = RmsExciter.FromUser(new PluginFieldController(sx), "PLUGIN:SAMPLE_SEXS");
            codedEx.Initialize(1.5); plugEx.Initialize(1.5);
            Same(codedEx.EquilibriumErrorPu(1.5), plugEx.EquilibriumErrorPu(1.5), "SEXS equilibrium error", 0);
            for (int k = 0; k < verr.Length; k++)
                Same(codedEx.Step(verr[k], 0.0, dt), plugEx.Step(verr[k], 0.0, dt), $"SEXS dt={dt}", k);
        }
    }

    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    private static List<SubtransientInit.MachineData> Data9(GridModel model, Func<int, GovernorData?> governor)
    {
        var data = new List<SubtransientInit.MachineData>();
        for (int k = 0; k < model.Generators.Count; k++)
            data.Add(new SubtransientInit.MachineData(H: model.Generators[k].H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: governor(k)));
        return data;
    }

    private static void AssertRunsIdentical(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace x = a.Machines[i], y = b.Machines[i];
            Assert.Equal(x.DeltaRad.Count, y.DeltaRad.Count);
            for (int k = 0; k < x.DeltaRad.Count; k++)
            {
                Same(x.DeltaRad[k], y.DeltaRad[k], $"machine {i} angle", k);
                Same(x.SpeedPu[k], y.SpeedPu[k], $"machine {i} speed", k);
                Same(x.EqpPu[k], y.EqpPu[k], $"machine {i} Eq'", k);
                Same(x.EdpPu[k], y.EdpPu[k], $"machine {i} Ed'", k);
            }
        }
    }

    [Fact]
    public void Ieee9_with_plugin_governors_and_exciters_gives_the_coded_study_trace_bit_for_bit()
    {
        using var f = new Folder();
        PluginSet set = LoadSample(f, "SAMPLE_TGOV1", "SAMPLE_SEXS");
        (GridModel model, PowerFlowResult pf) = Case9();
        var tgPars = P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.0), ("Vmin", 0.0), ("Vmax", 3.0));
        var plugGov = new PluginGovernorData(new PluginModel(set.Find("SAMPLE_TGOV1")!, tgPars));
        var codedGov = new Tgov1Data(0.05, 0.5, 1.0, 5.0, TurbineDampingPu: 0.0, ValveMinPu: 0.0, ValveMaxPu: 3.0);
        var sx = new PluginModel(set.Find("SAMPLE_SEXS")!, P(("K", 100.0), ("Ta", 0.1), ("Tb", 1.0), ("Te", 0.05), ("Emin", 0.0), ("Emax", 8.0)));
        List<RmsExciter?> Coded() => Enumerable.Range(0, 3).Select(_ => (RmsExciter?)RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0)).ToList();
        List<RmsExciter?> Plugged() => Enumerable.Range(0, 3).Select(_ => (RmsExciter?)RmsExciter.FromUser(new PluginFieldController(sx), "PLUGIN:SAMPLE_SEXS")).ToList();

        MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, Data9(model, _ => codedGov), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0, exciters: Coded());
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, Data9(model, _ => plugGov), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0, exciters: Plugged());
        AssertRunsIdentical(a, b);
        // Not vacuous: without the controllers the trace differs.
        MultiMachineSubtransient.Result plain = RmsTransientStability.Study(model, pf, Data9(model, _ => null), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0);
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(plain.Machines[0].SpeedPu[^1]), BitConverter.DoubleToInt64Bits(a.Machines[0].SpeedPu[^1]));
    }

    private static DynamicsStudyRequest Case14Request(IReadOnlyList<PluginModelAssignment>? plugins)
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        return Request(raw, Text(Study("case14-study.dyr")), new[] { Fault4("fault-4") }) with { PluginModels = plugins };
    }

    [Fact]
    public void Dynamics_study_with_plugin_tgov1_and_sexs_is_bit_identical_to_the_dyr_models_and_the_coverage_says_outside_the_contract()
    {
        using var f = new Folder();
        PluginSet set = LoadSample(f, "SAMPLE_TGOV1", "SAMPLE_SEXS");
        PsseParseResult raw = Raw(Psse("case14.raw"));
        PsseDyrResult dyr = PsseDyrReader.Read(Text(Study("case14-study.dyr")), raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        MachineDynamicsSet dyn = DynamicsModelCoverage.BindDocument(dyr.Json, "case14.dyr", Schemas, raw.Model);
        int g1 = Enumerable.Range(0, raw.Model.Generators.Count).First(k => raw.Model.Generators[k].Bus == 1);
        MachineDynamicsEntry e1 = dyn.For(g1)!;
        var tg = Assert.IsType<Tgov1Data>(e1.Data.Governor);
        IReadOnlyDictionary<string, double> ex = e1.Exciter!.Params;
        var gov = new PluginModel(set.Find("SAMPLE_TGOV1")!, P(("R", tg.DroopPu), ("T1", tg.ValveTimeSeconds), ("T2", tg.ReheatLeadSeconds), ("T3", tg.ReheatLagSeconds),
            ("Dt", tg.TurbineDampingPu), ("Vmin", tg.ValveMinPu), ("Vmax", tg.ValveMaxPu)));
        var exc = new PluginModel(set.Find("SAMPLE_SEXS")!, P(("K", ex["sexsGain"]), ("Ta", ex["sexsLeadS"]), ("Tb", ex["sexsLagS"]), ("Te", ex["sexsExciterTimeS"]),
            ("Emin", ex["sexsFieldMinPu"]), ("Emax", ex["sexsFieldMaxPu"])));

        DynamicsStudyResult baseline = DynamicsStudyRunner.Run(Case14Request(null));
        DynamicsStudyResult plugged = DynamicsStudyRunner.Run(Case14Request(new[] { new PluginModelAssignment(M(1), gov), new PluginModelAssignment(M(1), exc) }));
        Assert.Equal(baseline.Status, plugged.Status);
        DynamicsRunEvidence ra = baseline.Cases[0].Run!, rb = plugged.Cases[0].Run!;
        Assert.Equal(ra.Samples, rb.Samples);
        for (int c = 0; c < ra.Channels.Count; c++)
            for (int k = 0; k < ra.Channels[c].Values.Count; k++)
                Same(ra.Channels[c].Values[k], rb.Channels[c].Values[k], ra.Channels[c].Label, k);

        string status = $"SUPPORTED (PLUGIN, sha256:{gov.Hash}, OUTSIDE THE VERIFIED CONTRACT)";
        Assert.Equal(status, plugged.Coverage.Records.Single(r => r.Model == "TGOV1").Status);
        Assert.Equal(status, plugged.Coverage.Records.Single(r => r.Model == "SEXS").Status);
        Assert.Contains(plugged.Coverage.ReaderNotes, n => n.StartsWith("plug-in model SAMPLE_TGOV1 (governor) from GridSim.ModelPlugin.Sample sha256:" + gov.Hash + " contract 1;", StringComparison.Ordinal)
            && n.Contains("OUTSIDE THE VERIFIED CONTRACT", StringComparison.Ordinal));
        DynamicsUnitCoverage u1 = plugged.Coverage.Units.Single(u => u.Bus == 1);
        Assert.StartsWith("PLUGIN:SAMPLE_TGOV1 (sha256:", u1.Governor, StringComparison.Ordinal);
        Assert.StartsWith("PLUGIN:SAMPLE_SEXS (sha256:", u1.Exciter, StringComparison.Ordinal);
        Assert.Equal(Sha(f.File("sample.dll")), gov.Hash);
    }

    /// <summary>TGOV1 written with two expr blocks in place of the div/sum and gain/sum pairs of the shipped document. The
    /// expressions read left to right as the coded model's lines do: valveCmd = Pref - dw / R (in0 - in1 / @R, RPN: in0 in1
    /// @R / -) and Pmech = Pt - Dt dw (in0 - @Dt * in1, RPN: in0 @Dt in1 * -), so every operation is the same IEEE operation
    /// on the same operands in the same order, and bit identity (not a bound) is the claim.</summary>
    private const string ExprTgov1 = """
        { "format": "gridsim-model/1", "id": "EXPR_TGOV1", "role": "governor", "source": "TGOV1 as GridSim.Core.Dynamics.Models.Tgov1, written with expr blocks (test fixture)",
          "parameters": [
            { "name": "R", "unit": "pu", "value": 0.05, "min": 0, "minExclusive": true }, { "name": "T1", "unit": "s", "value": 0.5, "min": 0 },
            { "name": "T2", "unit": "s", "value": 1.0, "min": 0 }, { "name": "T3", "unit": "s", "value": 5.0, "min": 0, "minExclusive": true },
            { "name": "Dt", "unit": "pu", "value": 0.0 }, { "name": "Vmin", "unit": "pu", "value": 0.0, "allowInfinite": true },
            { "name": "Vmax", "unit": "pu", "value": 1.2, "allowInfinite": true } ],
          "ports": { "inputs": [ { "name": "speed", "unit": "pu" } ], "outputs": [ { "name": "pm", "unit": "pu", "from": "pmech" } ] },
          "blocks": [
            { "id": "pref", "type": "hold" },
            { "id": "cmd", "type": "expr", "inputs": ["pref", "speed"], "expr": "in0 - in1 / @R", "unit": "pu" },
            { "id": "valve", "type": "lag", "inputs": ["cmd"], "params": { "gain": 1.0, "time": "@T1", "min": "@Vmin", "max": "@Vmax" } },
            { "id": "reheat", "type": "leadlag", "inputs": ["valve"], "params": { "lead": "@T2", "lag": "@T3" } },
            { "id": "pmech", "type": "expr", "inputs": ["reheat", "speed"], "expr": "in0 - @Dt * in1", "unit": "pu" } ],
          "initialisation": { "inputs": [ { "name": "pm0", "unit": "pu" } ], "steps": [
            { "action": "require", "value": "pm0", "min": "@Vmin", "max": "@Vmax", "model": "TGOV1", "quantity": "valve position Pgv", "argument": "mechanicalPowerPu" },
            { "action": "initialise", "block": "pref", "values": ["pm0"] },
            { "action": "initialise", "block": "valve", "values": ["pm0"] },
            { "action": "initialise", "block": "reheat", "values": ["pm0"] } ] } }
        """;

    [Fact]
    public void An_expr_tgov1_is_bit_identical_to_the_coded_tgov1_unit_by_unit_and_on_ieee9()
    {
        UserModel um = UserModel.FromDocument(ModelDocument.Parse(ExprTgov1), P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.02), ("Vmin", 0.0), ("Vmax", 1.2)));
        double[] speed = Sequence(2, 0.05);
        foreach (double dt in StepSizes)
        {
            var coded = new Tgov1(0.05, 0.5, 1.0, 5.0, 0.02, 0.0, 1.2);
            ModelInstance decl = um.NewInstance();
            coded.Initialize(0.8); decl.Initialize(0.8);
            for (int k = 0; k < speed.Length; k++) Same(coded.Step(speed[k], dt), decl.Step(speed[k], dt), $"expr TGOV1 dt={dt}", k);
        }
        (GridModel model, PowerFlowResult pf) = Case9();
        var codedGov = new Tgov1Data(0.05, 0.5, 1.0, 5.0, TurbineDampingPu: 0.0, ValveMinPu: 0.0, ValveMaxPu: 3.0);
        var exprGov = new UserModelGovernorData(UserModel.FromDocument(ModelDocument.Parse(ExprTgov1),
            P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.0), ("Vmin", 0.0), ("Vmax", 3.0))));
        AssertRunsIdentical(
            RmsTransientStability.Study(model, pf, Data9(model, _ => codedGov), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0),
            RmsTransientStability.Study(model, pf, Data9(model, _ => exprGov), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0));
    }

    // ------------------------------------------------------------------------------------------------ 2. injection

    /// <summary>
    /// Two-bus system: bus 1 slack holding a stiff GENCLS (H = 1e4 s, X'd = 1e-4 pu); a lossless line X = 0.1 pu; bus 2 a
    /// 50 MW unit driven by SAMPLE_PQ (Sb = 100 MVA). Hand power flow with V1 = 1 at angle 0 and Q2 = 0: the reactive
    /// balance at bus 2, (V2^2 - V2 cos d) / X = 0, gives V2 = cos d, and the active balance P2 = V2 sin d / X = 0.5 then
    /// gives sin(2d) = 2 P2 X = 0.1, so d = asin(0.1) / 2 = 0.0500837105807799 rad and V2 = cos d = 0.9987460731103327.
    /// The bus is held PV at that V2, so the power flow must find the angle d and Q2 = 0 (both checked to 1e-9).
    /// </summary>
    [Fact]
    public void A_plugin_constant_power_injection_starts_at_the_analytical_operating_point_and_stays_there()
    {
        const double delta = 0.0500837105807799, v2 = 0.9987460731103327;
        using var f = new Folder();
        PluginSet set = LoadSample(f, "SAMPLE_PQ");
        var m = new GridModel
        {
            Name = "wp12-pq",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, 0, Vg: 1.0, MBase: 100, H: 1e4, Name: "source"),
                new Generator(2, 50, Vg: v2, Qmin: -999, Qmax: 999, MBase: 100, Name: "pq"),
            },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        Assert.True(pf.Converged);
        int b2 = pf.BusIds.ToList().IndexOf(2);
        Assert.True(Math.Abs(pf.VaDeg[b2] * Math.PI / 180.0 - delta) <= 1e-9, $"angle {pf.VaDeg[b2] * Math.PI / 180.0:R}");
        Assert.True(Math.Abs(pf.Vm[b2] - v2) <= 1e-9);
        Assert.True(Math.Abs(pf.QInjMvar[b2]) <= 1e-7, $"Q2 {pf.QInjMvar[b2]:R} Mvar");   // 1e-9 pu on 100 MVA

        var device = UserDeviceModel.FromPlugin(new PluginModel(set.Find("SAMPLE_PQ")!));
        var data = new List<SubtransientInit.MachineData> { Gencls.Data(1e4, 0, 1e-4), Gencls.Data(1, 0, 0.1) with { UserInjection = device } };
        RmsTransientStability.ObservedStudy run = RmsTransientStability.StudyEventsObserved(m, pf, data, Array.Empty<RmsEvent>(), new[] { 2 },
            stepSeconds: 0.005, durationSeconds: 1.0);
        RmsConverterTrace t = Assert.Single(run.Result.Converters!);
        Assert.True(t.TerminalVoltagePu.Count >= 200);
        for (int k = 0; k < t.TerminalVoltagePu.Count; k++)
        {
            Assert.True(Math.Abs(t.TerminalVoltagePu[k] - t.TerminalVoltagePu[0]) <= 1e-12, $"V at sample {k}");
            Assert.True(Math.Abs(t.ActivePowerPu[k] - t.ActivePowerPu[0]) <= 1e-12, $"P at sample {k}");
            Assert.True(Math.Abs(run.Buses.MagnitudePu[0][k] - run.Buses.MagnitudePu[0][0]) <= 1e-12, $"bus V at sample {k}");
        }
        Assert.True(Math.Abs(t.TerminalVoltagePu[0] - v2) <= 1e-9);
        Assert.True(Math.Abs(t.ActivePowerPu[0] - 0.5) <= 1e-9);
        Assert.All(run.Result.Machines[0].SpeedPu, w => Assert.True(Math.Abs(w) <= 1e-12));
    }

    // ------------------------------------------------------------------------------------------------ 3. declarative load

    /// <summary>The load role, constant impedance: p = q = (vm / v0)^2, with v0 held from initialisation.</summary>
    private const string ConstZLoad = """
        { "format": "gridsim-model/1", "id": "ZLOAD", "role": "load", "source": "constant-impedance load, p = q = (V/V0)^2 (test fixture)",
          "ports": { "inputs": [ { "name": "vm", "unit": "pu" } ],
                     "outputs": [ { "name": "p", "unit": "pu", "from": "pz" }, { "name": "q", "unit": "pu", "from": "qz" } ] },
          "blocks": [
            { "id": "v0h", "type": "hold" },
            { "id": "pz", "type": "expr", "inputs": ["vm", "v0h"], "expr": "(in0 / in1) * (in0 / in1)", "unit": "pu" },
            { "id": "qz", "type": "expr", "inputs": ["vm", "v0h"], "expr": "(in0 / in1) * (in0 / in1)", "unit": "pu" } ],
          "initialisation": { "inputs": [ { "name": "p0", "unit": "pu" }, { "name": "q0", "unit": "pu" }, { "name": "v0", "unit": "pu" } ],
            "steps": [ { "action": "initialise", "block": "v0h", "values": ["v0"] } ] } }
        """;

    /// <summary>
    /// As test 2 but bus 2 carries a 20 Mvar load and no unit. Power flow (P = 0, so the angle is 0): the reactive balance
    /// (V2^2 - V2) / X = -0.2 gives V2^2 - V2 + 0.02 = 0, V2 = (1 + sqrt(0.92)) / 2 = 0.9795831523312719. The source current
    /// is I1 = (1 - V2) / (j 0.1), so the GENCLS EMF is E = 1 + j X'd I1 = 1 + 1e-4 (1 - V2) / 0.1 = 1.0000204168476687. A
    /// three-phase fault at bus 2 through j Xf = j 0.05: with Yl = 1 / (j 0.1001) (line plus X'd), Yf = 1 / (j 0.05) and the
    /// load admittance YL = -j 0.2 / V2^2, the bus voltage is |E Yl / (Yl + Yf + YL)| = 0.33081894288367963. The load as a
    /// declarative model (role load, p = q = (vm / v0)^2) and as the static constant admittance must agree to 1e-12, and
    /// both must give the closed form to 1e-12.
    /// </summary>
    [Fact]
    public void A_declarative_constant_impedance_load_matches_the_static_load_and_the_closed_form_fault_voltage()
    {
        const double v2 = 0.9795831523312719, faulted = 0.33081894288367963;
        var m = new GridModel
        {
            Name = "wp12-z",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Qd: 20, BaseKv: 132) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, H: 1e4, Name: "source") },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        Assert.True(pf.Converged);
        Assert.True(Math.Abs(pf.Vm[pf.BusIds.ToList().IndexOf(2)] - v2) <= 1e-12);

        var machines = new List<SubtransientInit.MachineData> { Gencls.Data(1e4, 0, 1e-4) };
        var load = new UserDynamicLoadData(2, 0.0, 20.0, UserDeviceModel.FromDeclarative(UserModel.FromDocument(ModelDocument.Parse(ConstZLoad))));
        RmsEvent[] events = { new RmsSequenceFault(0.1, 2, FaultType.ThreePhase, 0.0, 0.05), new RmsFaultClear(0.2, 2) };
        RmsTransientStability.ObservedStudy stat = RmsTransientStability.StudyEventsObserved(m, pf, machines, events, new[] { 2 }, stepSeconds: 0.005, durationSeconds: 0.4);
        RmsTransientStability.ObservedStudy dyn = RmsTransientStability.StudyEventsObserved(m, pf, new RmsLoadedMachineData(machines, new DynamicLoadData[] { load }),
            events, new[] { 2 }, stepSeconds: 0.005, durationSeconds: 0.4);
        IReadOnlyList<double> ts = stat.Buses.TimeSeconds;
        Assert.Equal(ts.Count, dyn.Buses.TimeSeconds.Count);
        int during = 0;
        for (int k = 0; k < ts.Count; k++)
        {
            double a = stat.Buses.MagnitudePu[0][k], b = dyn.Buses.MagnitudePu[0][k];
            Assert.True(Math.Abs(a - b) <= 1e-12, $"sample {k} (t = {ts[k]:R}): static {a:R}, declarative {b:R}");
            if (ts[k] > 0.1025 && ts[k] < 0.1975)
            {
                during++;
                Assert.True(Math.Abs(b - faulted) <= 1e-12, $"faulted V2 {b:R} at t = {ts[k]:R}");
            }
            if (ts[k] < 0.0975) Assert.True(Math.Abs(b - v2) <= 1e-12, $"pre-fault V2 {b:R}");
        }
        Assert.True(during >= 15);
    }

    // ------------------------------------------------------------------------------------------------ 4. expr

    private static ModelInstance ExprModel(string expr, int inputs, string paramsJson = "")
    {
        string ins = string.Join(", ", Enumerable.Range(0, inputs).Select(i => $"{{ \"name\": \"u{i}\", \"unit\": \"pu\" }}"));
        string wires = string.Join(", ", Enumerable.Range(0, inputs).Select(i => $"\"u{i}\""));
        string text = $$"""
            { "format": "gridsim-model/1", "id": "X", "role": "generic", "source": "test",
              {{(paramsJson.Length > 0 ? "\"parameters\": [" + paramsJson + "]," : "")}}
              "ports": { "inputs": [ {{ins}} ], "outputs": [ { "name": "y", "unit": "pu", "from": "e" } ] },
              "blocks": [ { "id": "e", "type": "expr", "inputs": [ {{wires}} ], "expr": "{{expr}}", "unit": "pu" } ],
              "initialisation": { "inputs": [ { "name": "z0", "unit": "pu" } ], "steps": [] } }
            """;
        return UserModel.FromDocument(ModelDocument.Parse(text)).NewInstance();
    }

    private static double Eval(ModelInstance m, params double[] u)
    {
        m.Step(u, 0.01);
        return m.Output(0);
    }

    [Fact]
    public void The_expr_block_evaluates_its_text_left_to_right_exactly()
    {
        ModelInstance c = ExprModel("clamp(in0*@K, -1, 1)", 1, "{ \"name\": \"K\", \"unit\": \"pu\", \"value\": 2 }");
        Assert.Equal(0.6, Eval(c, 0.3));   // 0.3 * 2 = 0.6 exactly in binary (doubling is exact)
        Assert.Equal(1.0, Eval(c, 0.8));
        Assert.Equal(-4.0, Eval(ExprModel("1 - 2 - 3", 1), 0.0));   // left-associative: (1 - 2) - 3
        Assert.Equal(10.0, Eval(ExprModel("2*3+4", 1), 0.0));       // * above +
        Assert.Equal(14.0, Eval(ExprModel("2+3*4", 1), 0.0));
        Assert.Equal(-6.0, Eval(ExprModel("-(1+2)*2", 1), 0.0));
        Assert.Equal(GridSim.Core.Numerics.DetMath.Exp(1.0), Eval(ExprModel("exp(in0)", 1), 1.0));
        Assert.Equal(1.4142135623730951, Eval(ExprModel("sqrt(in0)", 1), 2.0));
        Assert.Equal(3.0, Eval(ExprModel("max(in0, in1, 3)", 2), 1.0, 2.0));
        Assert.Equal(-2.0, Eval(ExprModel("min(abs(in0), -2)", 1), -5.0));
        // A non-finite result refuses the step, naming the block.
        var ex = Assert.Throws<UserModelException>(() => Eval(ExprModel("1/in0", 1), 0.0));
        Assert.Equal("non-finite", ex.Issues[0].Code);
        Assert.Equal("block e", ex.Issues[0].Object);
        // The grammar is closed: an exponent, an unknown name, an input the block does not have, a missing unit.
        Assert.Contains(ModelValidator.Validate(Doc("1e3", 1)), i => i.Code == "bad-expr");
        Assert.Contains(ModelValidator.Validate(Doc("sin(in0)", 1)), i => i.Code == "bad-expr");
        Assert.Contains(ModelValidator.Validate(Doc("in1", 1)), i => i.Code == "bad-expr");
        Assert.Contains(ModelValidator.Validate(Doc("in0 + @Nope", 1)), i => i.Code == "undeclared-parameter");
        Assert.Contains(ModelValidator.Validate(Doc("in0", 1, unit: false)), i => i.Code == "missing-param");
        Assert.Contains(ModelValidator.Validate(Doc(string.Concat(Enumerable.Repeat("(", 70)) + "1" + string.Concat(Enumerable.Repeat(")", 70)), 1)), i => i.Code == "bad-expr");
    }

    private static ModelDocument Doc(string expr, int inputs, bool unit = true)
    {
        string ins = string.Join(", ", Enumerable.Range(0, inputs).Select(i => $"{{ \"name\": \"u{i}\", \"unit\": \"pu\" }}"));
        string wires = string.Join(", ", Enumerable.Range(0, inputs).Select(i => $"\"u{i}\""));
        return ModelDocument.Parse($$"""
            { "format": "gridsim-model/1", "id": "X", "role": "generic", "source": "test",
              "ports": { "inputs": [ {{ins}} ], "outputs": [ { "name": "y", "unit": "pu", "from": "e" } ] },
              "blocks": [ { "id": "e", "type": "expr", "inputs": [ {{wires}} ], "expr": "{{expr}}"{{(unit ? ", \"unit\": \"pu\"" : "")}} } ],
              "initialisation": { "inputs": [ { "name": "z0", "unit": "pu" } ], "steps": [] } }
            """);
    }

    [Fact]
    public void Held_evaluation_changes_no_state_and_repeats_bit_for_bit()
    {
        // A lag inside a load model: the held output is the lag's state, whatever the input, until a step advances it.
        ModelInstance m = UserModel.FromDocument(ModelDocument.Parse("""
            { "format": "gridsim-model/1", "id": "LAGLOAD", "role": "load", "source": "test",
              "ports": { "inputs": [ { "name": "vm", "unit": "pu" } ], "outputs": [ { "name": "p", "unit": "pu", "from": "lp" }, { "name": "q", "unit": "pu", "from": "lp" } ] },
              "blocks": [ { "id": "lp", "type": "lag", "inputs": ["vm"], "params": { "gain": 1.0, "time": 0.5 } } ],
              "initialisation": { "inputs": [ { "name": "p0", "unit": "pu" }, { "name": "q0", "unit": "pu" }, { "name": "v0", "unit": "pu" } ],
                "steps": [ { "action": "initialise", "block": "lp", "values": ["v0"] } ] } }
            """)).NewInstance();
        m.Initialize(new[] { 0.5, 0.1, 1.0 });
        m.EvaluateHeld(new[] { 0.3 });
        Assert.Equal(1.0, m.Output(0));
        m.EvaluateHeld(new[] { 0.9 });
        Assert.Equal(1.0, m.Output(0));
        m.Step(new[] { 0.9 }, 0.01);
        double after = m.Output(0);
        Assert.True(after < 1.0);
        m.EvaluateHeld(new[] { 0.1 });
        Same(after, m.Output(0), "held after step", 0);
    }

    // ------------------------------------------------------------------------------------------------ 5. contract

    [Fact]
    public void The_verifier_refuses_a_clock_read_naming_the_member_and_the_type_and_nothing_is_loaded()
    {
        using var f = new Folder();
        File.Copy(BadSampleDll, f.File("bad.dll"));
        PluginVerification v = PluginVerifier.Verify(File.ReadAllBytes(f.File("bad.dll")));
        Assert.Equal(1, v.ContractVersion);
        Assert.Contains(v.Refusals, r => r.StartsWith("System.DateTime.get_UtcNow (referenced by GridSim.ModelPlugin.BadSample.BadClockGovernor.Step)", StringComparison.Ordinal));
        string config = Config(f, ("bad.dll", Sha(f.File("bad.dll")), new[] { "BAD_CLOCK" }));
        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
        Assert.Contains("System.DateTime.get_UtcNow", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BadClockGovernor", ex.Message, StringComparison.Ordinal);
        AssertNothingLoaded(config);
        // The sample passes the lint with no refusal at all.
        Assert.Empty(PluginVerifier.Verify(File.ReadAllBytes(SampleDll)).Refusals);
    }

    [Fact]
    public void A_shared_static_counter_passes_the_lint_and_is_refused_by_the_replay_check()
    {
        using var f = new Folder();
        PluginSet set = LoadSample(f, "BAD_STATIC");
        var ex = Assert.Throws<PluginRefusedException>(() => new PluginModel(set.Find("BAD_STATIC")!));
        Assert.Contains("BAD_STATIC: not deterministic (replay differs at step 1)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_denylist_is_ordinal_sorted_and_names_what_the_contract_forbids()
    {
        // REVIEW-FIX-7: the lists are private immutable arrays; the internal Lists view gives them for this order check.
        foreach ((string _, System.Collections.Immutable.ImmutableArray<string> list) in PluginDenylist.Lists)
            Assert.Equal(list.OrderBy(x => x, StringComparer.Ordinal), list);
        Assert.NotNull(PluginDenylist.Refusal("System.Threading.Thread", null));
        Assert.NotNull(PluginDenylist.Refusal("System.Threading.Tasks.Task", "Run"));
        Assert.NotNull(PluginDenylist.Refusal("System.Random", ".ctor"));
        Assert.NotNull(PluginDenylist.Refusal("System.Math", "Sin"));
        Assert.NotNull(PluginDenylist.Refusal("System.Math", "FusedMultiplyAdd"));
        Assert.NotNull(PluginDenylist.Refusal("System.Collections.Generic.Dictionary`2", "GetEnumerator"));
        Assert.NotNull(PluginDenylist.Refusal("System.IO.File", "ReadAllBytes"));
        Assert.NotNull(PluginDenylist.Refusal("System.Runtime.Intrinsics.Vector256", "Add"));
        Assert.Null(PluginDenylist.Refusal("System.Math", "Sqrt"));
        Assert.Null(PluginDenylist.Refusal("System.Collections.Generic.Dictionary`2", "TryGetValue"));
        Assert.Null(PluginDenylist.Refusal("GridSim.Core.Dynamics.Models.FirstOrderLag", "Step"));
    }

    // ------------------------------------------------------------------------------------------------ 6. loading rules

    private static void AssertNothingLoaded(string configPath)
    {
        string name = PluginLoader.ContextName(Path.GetFullPath(configPath));
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == name && c.Assemblies.Any());
    }

    [Fact]
    public void Every_loading_rule_refuses_naming_the_entry_and_loads_nothing()
    {
        using var f = new Folder();
        string root = Directory.CreateDirectory(f.File("root")).FullName;
        File.Copy(SampleDll, Path.Combine(root, "sample.dll"));
        string good = Sha(Path.Combine(root, "sample.dll"));
        using var outside = new Folder();
        File.Copy(SampleDll, outside.File("sample.dll"));

        void Refused(string label, string configJson, string expect)
        {
            string path = Path.Combine(root, label + ".plugins.json");
            File.WriteAllText(path, configJson);
            var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(path));
            Assert.Contains(expect, ex.Message, StringComparison.Ordinal);
            AssertNothingLoaded(path);
        }
        string Cfg(string p, string sha, string id = "SAMPLE_TGOV1") => JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { new { path = p, sha256 = sha, models = new[] { id } } } });

        // No flag: an assignment of a plug-in model without a configuration.
        string assignments = JsonSerializer.Serialize(new { schema = "gridsim-model-assignments/1", pluginModels = new[] { new { machine = new { bus = 1, id = "1" }, model = "SAMPLE_TGOV1" } } });
        var noFlag = Assert.Throws<InvalidDataException>(() => ModelAssignmentsFile.Parse(Encoding.UTF8.GetBytes(assignments), f.File("a.json"), null, 0.005));
        Assert.Contains("plug-in models need --model-plugins", noFlag.Message, StringComparison.Ordinal);

        Refused("escape", Cfg("../outside.dll", good), "outside the base folder");
        Refused("absolute", Cfg(outside.File("sample.dll"), good), "outside the base folder");
        Refused("unc", Cfg(@"\\server\share\sample.dll", good), "network or device path");
        Refused("device", Cfg(@"\\?\C:\sample.dll", good), "network or device path");
        string flipped = string.Concat(good[0] == '0' ? "1" : "0", good.AsSpan(1));
        Refused("hash", Cfg("sample.dll", flipped), "not the pinned");
        Refused("duplicate", Cfg("sample.dll", good, "TGOV1"), "the id TGOV1 is a shipped model's id");
        // An assembly without [GridSimModelPlugin(1)]: GridSim.Core itself, pinned by its hash.
        File.Copy(typeof(PluginLoader).Assembly.Location, Path.Combine(root, "core.dll"));
        Refused("attribute", Cfg("core.dll", Sha(Path.Combine(root, "core.dll")), "ANY"), "carries no [GridSimModelPlugin(1)] or [GridSimModelPlugin(2)] attribute");

        // A symbolic link or junction inside the root (skipped where the OS will not create one, as for the scripting --confine tests).
        string linkDir = Path.Combine(root, "linked");
        if (TryLink(linkDir, outside.Path))
            Refused("link", Cfg("linked/sample.dll", good), "symbolic link or junction");
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
        if (!OperatingSystem.IsWindows()) return false;
        // A junction needs no privilege on Windows.
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit();
            return p.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void A_listed_model_the_assembly_does_not_hold_is_refused_and_its_context_unloaded()
    {
        using var f = new Folder();
        File.Copy(SampleDll, f.File("sample.dll"));
        string config = Config(f, ("sample.dll", Sha(f.File("sample.dll")), new[] { "NO_SUCH" }));
        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
        Assert.Contains("the assembly has no model NO_SUCH", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SAMPLE_TGOV1", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ 7. no change without opt-in

    [Fact]
    public void The_shipped_library_and_its_listing_are_unchanged()
    {
        // The SHA-256 of each shipped document with line ends normalised to LF (the repository's bytes; a Windows checkout
        // embeds CRLF, so the raw hash is per platform): as at 2.1.091.
        var pinned = new (string Id, string Role, string Lf)[]
        {
            ("GAST", "governor", "02f3d19a4be4460333269cd7144cb278ca307c61d8a02d1bcc70f1927f403fad"),
            ("IEEEG1", "governor", "73efbf4ec07ad0d1e9afd0bdd6d1e9784559d2c0bf7a1dd38787a10ba8221c6b"),
            ("IEEET1", "exciter", "513307f4d82da4cd203b3aad401e0823034d9fa49b20ff4a3a4cb1126ee3058d"),
            ("PSS1A", "stabiliser", "70d606974d673d41209b512167d2f6ee97217e0efb8a30ec664141f23cae5eeb"),
            ("SEXS", "exciter", "d5459588b0684c48878e0846a5ab9e52a46cc85443f45ec00b0961fb7a478625"),
            ("SEXS_PSS1A", "exciter", "2f2f6b1c44577d714b4bdec24ac801269c3e9e7fcab1a2c7dd0cea5dc390f352"),
            ("TGOV1", "governor", "b8dbe80131c5eb0300fd1c4d3a24c978783f9609b7c60fe05a80744436ba0ee4"),
        };
        Assert.Equal(pinned.Select(p => p.Id), UserModelLibrary.Ids);
        var lines = new List<string>();
        foreach ((string id, string role, string lf) in pinned)
        {
            byte[] bytes = UserModelLibrary.Bytes(id);
            string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(lf, Sha(Encoding.UTF8.GetBytes(text)));
            ModelDocument d = UserModelLibrary.Document(id);
            Assert.Equal(role, d.Role);
            Assert.Equal(Sha(bytes), d.Sha256);
            lines.Add($"{d.Id,-12} {d.Role,-10} sha256:{d.Sha256}  {d.Title}");
        }
        CliProcess.Result r = CliProcess.RunBuilt("user-model", "list");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(string.Join("\n", lines), r.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
    }

    [Fact]
    public void The_moved_path_rule_gives_the_scripting_forwarder_the_same_answers()
    {
        using var f = new Folder();
        Directory.CreateDirectory(f.File("sub"));
        File.WriteAllText(f.File(Path.Combine("sub", "a.txt")), "x");
        string[] written = { "sub/a.txt", "../x", Path.GetTempPath(), @"\\server\share\x", "//server/x", @"\\?\C:\x", @"\\.\pipe\x", "sub/../sub/a.txt", "missing/deeper/y" };
        foreach (string w in written)
        {
            string full = PathConfinement_Full(w, f.Path);
            Assert.Equal(GridSim.Core.Security.PathConfinement.Refusal(w, full, f.Path), GridSim.Scripting.PathConfinement.Refusal(w, full, f.Path));
            Assert.Equal(GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(w), GridSim.Scripting.PathConfinement.IsNetworkOrDevice(w));
        }
        Assert.Null(GridSim.Core.Security.PathConfinement.Refusal("sub/a.txt", Path.GetFullPath(f.File("sub/a.txt")), f.Path));
        Assert.Equal("it is outside the base folder", GridSim.Core.Security.PathConfinement.Refusal("../x", Path.GetFullPath(Path.Combine(f.Path, "../x")), f.Path));
    }

    private static string PathConfinement_Full(string w, string root)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(w) ? w : Path.Combine(root, w)); }
        catch (ArgumentException) { return w; }
    }

    /// <summary>
    /// The two runs here are both new code (with and without the generalised loads route), so the historical bits rest on the
    /// existing RMS goldens, which run the same kernel with no user injection or user load and pin digests recorded before
    /// WP12: MachineAdmittanceSimdBitIdentityTests.Dense_rms_runs_are_bit_identical_to_the_scalar_kernel (the same
    /// RmsTransientStability.Study entry with a bus fault cleared after 0.08 s at a 0.005 s step for 2 s, IEEE 118 and 300,
    /// through the dense Kron network, three digests pinned in 2026-10); FullModelKernelDigestTests (HeldFieldDigest, ExcitedGovernedDigest
    /// and DynamicsStudyDigest, recorded at d7655da6: StudyEvents with a held field and with SEXS and TGOV1, and the case14
    /// dynamics study); and Rc5Wp6FactsHvdcDynamicsTests.Without_device_data_an_hvdc_and_facts_case_gives_the_old_bits
    /// (StudyEvents through RmsLoadedMachineData with no loads, digest from the 2.1.082 build).
    /// </summary>
    [Fact]
    public void Without_a_user_injection_or_user_load_the_rms_study_is_the_historical_one()
    {
        (GridModel model, PowerFlowResult pf) = Case9();
        List<SubtransientInit.MachineData> plain = Data9(model, _ => null);
        List<SubtransientInit.MachineData> withNull = plain.Select(d => d with { UserInjection = null }).ToList();
        AssertRunsIdentical(RmsTransientStability.Study(model, pf, plain, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0),
            RmsTransientStability.Study(model, pf, new RmsLoadedMachineData(withNull, Array.Empty<DynamicLoadData>()), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0));
        // The declarative overlay through the generalised route writes the R4-08 texts (pinned in R408UserModelBitIdentityTests).
        Assert.Equal("SUPPORTED (USER MODEL, sha256:abc)", UserModelOverlay.StatusText("abc"));
        Assert.Equal("SUPPORTED (PLUGIN, sha256:abc, OUTSIDE THE VERIFIED CONTRACT)", UserModelOverlay.PluginStatusText("abc"));
    }

    // ------------------------------------------------------------------------------------------------ 8. provenance (CLI)

    [Fact]
    public void Dynamics_study_with_a_plugin_records_its_hash_and_label_is_repeatable_and_keeps_the_traces()
    {
        using var f = new Folder();
        File.Copy(Psse("case14.raw"), f.File("case14.raw"));
        File.Copy(Study("case14-study.dyr"), f.File("case14.dyr"));
        File.Copy(SampleDll, f.File("sample.dll"));
        string dll = Sha(f.File("sample.dll"));
        File.WriteAllText(f.File("study.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(f.File("case14.raw")) },
            dyr = new { path = "case14.dyr", sha256 = Sha(f.File("case14.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 2.0 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = new object[] { new { kind = "speed", machine = new { bus = 1, id = "1" } }, new { kind = "busVoltage", bus = 4 } },
            cases = new object[]
            {
                new { name = "fault-4", kind = "events", events = new object[]
                {
                    new { kind = "busFault", at = 0.1, bus = 4 }, new { kind = "faultClear", at = 0.15, bus = 4 },
                    new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                } },
            },
        }));
        File.WriteAllText(f.File("plugins.json"), JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { new { path = "sample.dll", sha256 = dll, models = new[] { "SAMPLE_TGOV1" } } } }));
        // The DYR TGOV1 of bus 1: R 0.05, T1 0.49, VMAX 2.5, VMIN 0, T2 2.1, T3 7.0, Dt 0.
        File.WriteAllText(f.File("assign.json"), JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            pluginModels = new[] { new { machine = new { bus = 1, id = "1" }, model = "SAMPLE_TGOV1",
                parameters = new Dictionary<string, double> { ["R"] = 0.05, ["T1"] = 0.49, ["T2"] = 2.1, ["T3"] = 7.0, ["Dt"] = 0.0, ["Vmin"] = 0.0, ["Vmax"] = 2.5 } } },
        }));

        CliProcess.Result baseline = CliProcess.RunBuilt("dynamics-study", f.File("study.json"));
        Assert.True(baseline.ExitCode == 0, baseline.StdErr);
        CliProcess.Result first = CliProcess.RunBuilt("dynamics-study", f.File("study.json"), "--model-plugins", f.File("plugins.json"), "--model-assignments", f.File("assign.json"));
        Assert.True(first.ExitCode == 0, first.StdErr);
        CliProcess.Result second = CliProcess.RunBuilt("dynamics-study", f.File("study.json"), "--model-plugins", f.File("plugins.json"), "--model-assignments", f.File("assign.json"));
        Assert.Equal(first.StdOut, second.StdOut);

        using JsonDocument a = JsonDocument.Parse(baseline.StdOut), b = JsonDocument.Parse(first.StdOut);
        JsonElement plugins = b.RootElement.GetProperty("provenance").GetProperty("plugins");
        Assert.Equal("OUTSIDE THE VERIFIED CONTRACT", plugins.GetProperty("label").GetString());
        Assert.Equal(1, plugins.GetProperty("contractVersion").GetInt32());
        Assert.Equal(dll, plugins.GetProperty("models")[0].GetProperty("sha256").GetString());
        Assert.Contains($"SUPPORTED (PLUGIN, sha256:{dll}, OUTSIDE THE VERIFIED CONTRACT)", first.StdOut, StringComparison.Ordinal);
        // The plug-in is the DYR's TGOV1 operation for operation: every trace sample is the baseline's.
        Assert.Equal(a.RootElement.GetProperty("cases").GetRawText(), b.RootElement.GetProperty("cases").GetRawText());
        // Without the plug-in flags the result has no plug-in member at all.
        Assert.False(a.RootElement.GetProperty("provenance").TryGetProperty("plugins", out _));
        Assert.False(a.RootElement.GetProperty("provenance").TryGetProperty("modelAssignments", out _));

        // check-plugin reports the same model and hash; a plug-in assignment without --model-plugins is refused.
        CliProcess.Result check = CliProcess.RunBuilt("user-model", "check-plugin", f.File("plugins.json"));
        Assert.True(check.ExitCode == 0, check.StdErr);
        Assert.Contains("SAMPLE_TGOV1", check.StdOut, StringComparison.Ordinal);
        Assert.Contains("sha256:" + dll, check.StdOut, StringComparison.Ordinal);
        CliProcess.Result noFlag = CliProcess.RunBuilt("dynamics-study", f.File("study.json"), "--model-assignments", f.File("assign.json"));
        Assert.NotEqual(0, noFlag.ExitCode);
        Assert.Contains("plug-in models need --model-plugins", noFlag.StdErr, StringComparison.Ordinal);
    }
}
