using System.Numerics;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP32: plug-in contract 2 (the machine and relay roles), and injection, machine and load models on the dynamics-study
/// route. The plug-ins are tests/GridSim.ModelPlugin.Contract2Sample (SAMPLE_GENCLS, SAMPLE_UV27, SAMPLE_LATCH) and
/// tests/GridSim.ModelPlugin.Contract2Clock (CLOCK_RELAY), with the contract 1 sample tests/GridSim.ModelPlugin.Sample
/// (SAMPLE_PQ), built by the solution.
/// <para>The oracles: the built-in unit 27 stage, for bit identity of a plug-in relay written as that stage is evaluated
/// (FullModelUnitRelays: pickup at the first step beyond, operate at the first step k with (k - k_p) h + h 1e-6 &gt;= delay);
/// the closed-form two-source fault voltage of a constant-EMF machine (derived in the comment of test 1); the API run of the
/// same study, for bit identity of the CLI route; the incumbent study, for bit identity without any of it.</para>
/// <para>What is NOT claimed: a plug-in machine is not bit-identical to the built-in GENCLS. The built-in machines are
/// integrated by the kernel itself (rotor swing by RK4 with the network re-solved at each sub-step), which a plug-in cannot
/// join; a plug-in machine takes the injection route (a Norton source on its own bus, iterated with the network, stepped once
/// per step). The spec's bit-identity test 1 is therefore replaced by the closed-form fault voltage below.</para>
/// </summary>
public sealed class Rc5Wp32UserMachineRelayTests : IDisposable
{
    private const double Step = 0.005;

    private readonly string _dir = TestTemp.Under("gridsim-wp32-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp32UserMachineRelayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------------------------------------ fixtures

    private static string BuiltDll(string project)
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string dll = Path.Combine(CliProcess.Root(), "tests", project, "bin", tfmDir.Parent!.Name, tfmDir.Name, project + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"the plug-in {project} was not found; build the solution (or that project) first", dll);
        return dll;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private string F(string name) => Path.Combine(_dir, name);

    /// <summary>Copy the project's DLL into the test folder as <paramref name="file"/> and write a configuration listing it with
    /// <paramref name="ids"/> (and, when given, the entry's "contract").</summary>
    private string Configure(string project, string file, int? contract, params string[] ids)
    {
        File.Copy(BuiltDll(project), F(file), overwrite: true);
        string config = F(Path.GetFileNameWithoutExtension(file) + ".plugins.json");
        object entry = contract is int c
            ? new { path = file, sha256 = Sha(F(file)), models = ids, contract = c }
            : new { path = file, sha256 = Sha(F(file)), models = ids };
        File.WriteAllText(config, JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { entry } }));
        return config;
    }

    private PluginSet LoadContract2(params string[] ids) => PluginLoader.Load(Configure("GridSim.ModelPlugin.Contract2Sample", "c2.dll", 2, ids));

    private static void AssertNothingLoaded(string configPath)
    {
        string name = PluginLoader.ContextName(Path.GetFullPath(configPath));
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == name && c.Assemblies.Any());
    }

    private static void Same(double a, double b, string what, int k)
        => Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"{what}: sample {k}: {a:R} ({BitConverter.DoubleToInt64Bits(a):X16}) vs {b:R} ({BitConverter.DoubleToInt64Bits(b):X16})");

    private static Dictionary<string, double> P(params (string, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    private static readonly AssetClassProvenance TestSetting = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => TestSetting, StringComparer.Ordinal)))));

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));
        return data;
    }

    /// <summary>case9 with its generators named G&lt;bus&gt; (unit relays bind by name), solved.</summary>
    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" }).ToList(), generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private static string Digest(MultiMachineSubtransient.Result r) => FullModelKernelDigestTests.Digest(r, Array.Empty<RmsEvent>());

    // ------------------------------------------------------------------------------------------------ 1. the machine route

    private const double V2 = 0.9987460731103327;   // the WP12 two-bus operating point (Rc5Wp12UserModelRouteTests, test 2)

    /// <summary>The WP12 two-bus system: bus 1 slack with a stiff GENCLS (X'd = 1e-4 pu), a lossless line X = 0.1 pu, bus 2 a
    /// 50 MW unit held PV at V2 (Sb = MBASE = 100 MVA).</summary>
    private static (GridModel Model, PowerFlowResult Pf) TwoBus()
    {
        var m = new GridModel
        {
            Name = "wp32-machine",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, 0, Vg: 1.0, MBase: 100, H: 1e4, Name: "source"),
                new Generator(2, 50, Vg: V2, Qmin: -999, Qmax: 999, MBase: 100, Name: "machine"),
            },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private static Complex BusV(PowerFlowResult pf, int bus)
    {
        int i = pf.BusIds.ToList().IndexOf(bus);
        return Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
    }

    private static Complex BusS(PowerFlowResult pf, int bus)
    {
        int i = pf.BusIds.ToList().IndexOf(bus);
        return new Complex(pf.PInjMw[i], pf.QInjMvar[i]) / 100.0;
    }

    /// <summary>
    /// SAMPLE_GENCLS (X'd = 0.1 pu) drives the bus 2 unit; both machines have H = 1e15 s, so neither rotor angle can move
    /// measurably in 0.4 s (the swing gives delta - delta0 &lt;= w_s (|dP| / 2H) t^2 / 2 = 314 x 0.5 / 2e15 x 0.08 &lt; 1e-14 rad
    /// for |dP| &lt;= 0.5 pu), and each machine is a constant EMF behind its reactance. From the power flow: I_k = conj(S_k / V_k)
    /// and E_k = V_k + j X_k I_k (X_1 = 1e-4, X_2 = 0.1). A three-phase fault at bus 2 through j 0.05 from 0.1 s to 0.2 s gives,
    /// by nodal analysis at bus 2, V2f = (E_1 Y_l + E_2 Y_2) / (Y_l + Y_2 + Y_f) with Y_l = 1 / (j 0.1001) (line plus X'd of the
    /// source), Y_2 = 1 / (j 0.1) and Y_f = 1 / (j 0.05). Before the fault and after the clearing the bus holds the power-flow V2.
    /// The kernel solves the same linear network from the same E (the plug-in's Norton source is E_2 Y_2 behind the stamp Y_2,
    /// and its start is checked to 1e-10 (1 + |S0|) pu); what is left is floating-point rounding of a few ulps of 1 and the rotor
    /// drift above, so every sample must agree to 1e-12, the bound WP12 uses on the same network.
    /// </summary>
    [Fact]
    public void A_plugin_classical_machine_starts_at_the_power_flow_point_and_meets_the_closed_form_fault_voltage_at_every_sample()
    {
        (GridModel m, PowerFlowResult pf) = TwoBus();
        PluginSet set = LoadContract2("SAMPLE_GENCLS");
        PluginModelDefinition def = set.Find("SAMPLE_GENCLS")!;
        Assert.Equal(UserModelRole.Machine, def.Role);
        Assert.Equal("machine", def.RoleText);
        Assert.Equal(2, def.ContractVersion);
        var device = UserDeviceModel.FromPlugin(new PluginModel(def, P(("H", 1e15), ("D", 0.0), ("XDP", 0.1), ("F0", 50.0))));
        Assert.Equal("machine", device.Role);
        var data = new List<SubtransientInit.MachineData> { Gencls.Data(1e15, 0, 1e-4), Gencls.Data(1, 0, 0.1) with { UserInjection = device } };
        RmsEvent[] events = { new RmsSequenceFault(0.1, 2, FaultType.ThreePhase, 0.0, 0.05), new RmsFaultClear(0.2, 2) };
        RmsTransientStability.ObservedStudy run = RmsTransientStability.StudyEventsObserved(m, pf, data, events, new[] { 2 },
            stepSeconds: Step, durationSeconds: 0.4);

        Complex v1 = BusV(pf, 1), v2 = BusV(pf, 2);
        Complex e1 = v1 + Complex.ImaginaryOne * 1e-4 * Complex.Conjugate(BusS(pf, 1) / v1);
        Complex e2 = v2 + Complex.ImaginaryOne * 0.1 * Complex.Conjugate(BusS(pf, 2) / v2);
        Complex yl = 1.0 / (Complex.ImaginaryOne * 0.1001), y2 = 1.0 / (Complex.ImaginaryOne * 0.1), yf = 1.0 / (Complex.ImaginaryOne * 0.05);
        double faulted = ((e1 * yl + e2 * y2) / (yl + y2 + yf)).Magnitude;
        double held = pf.Vm[pf.BusIds.ToList().IndexOf(2)];   // the PV set point V2
        Assert.True(Math.Abs(held - V2) <= 1e-9);

        RmsConverterTrace t = Assert.Single(run.Result.Converters!);
        Assert.True(Math.Abs(t.ActivePowerPu[0] - 0.5) <= 1e-9, $"P at t = 0: {t.ActivePowerPu[0]:R}");
        IReadOnlyList<double> ts = run.Buses.TimeSeconds;
        int before = 0, during = 0, after = 0;
        for (int k = 0; k < ts.Count; k++)
        {
            double v = run.Buses.MagnitudePu[0][k];
            if (ts[k] < 0.0975) { before++; Assert.True(Math.Abs(v - held) <= 1e-12, $"pre-fault V2 {v:R} at t = {ts[k]:R}"); }
            else if (ts[k] > 0.1025 && ts[k] < 0.1975) { during++; Assert.True(Math.Abs(v - faulted) <= 1e-12, $"faulted V2 {v:R} vs {faulted:R} at t = {ts[k]:R}"); }
            else if (ts[k] > 0.2025) { after++; Assert.True(Math.Abs(v - held) <= 1e-12, $"post-fault V2 {v:R} at t = {ts[k]:R}"); }
        }
        Assert.True(before >= 19 && during >= 18 && after >= 38, $"{before} / {during} / {after} samples");
        Assert.True(faulted < 0.5, "the fault must depress the bus");
    }

    [Fact]
    public void A_plugin_machine_run_repeats_bit_for_bit_and_a_fault_moves_it()
    {
        (GridModel m, PowerFlowResult pf) = TwoBus();
        PluginSet set = LoadContract2("SAMPLE_GENCLS");
        PluginModel model = new(set.Find("SAMPLE_GENCLS")!, P(("H", 3.0), ("D", 0.0), ("XDP", 0.1), ("F0", 50.0)));
        RmsEvent[] events = { new RmsSequenceFault(0.1, 2, FaultType.ThreePhase, 0.0, 0.05), new RmsFaultClear(0.2, 2) };
        RmsTransientStability.ObservedStudy Run() => RmsTransientStability.StudyEventsObserved(m, pf,
            new List<SubtransientInit.MachineData> { Gencls.Data(1e4, 0, 1e-4), Gencls.Data(1, 0, 0.1) with { UserInjection = UserDeviceModel.FromPlugin(model) } },
            events, new[] { 2 }, stepSeconds: Step, durationSeconds: 1.0);
        RmsTransientStability.ObservedStudy a = Run(), b = Run();
        RmsConverterTrace ta = Assert.Single(a.Result.Converters!), tb = Assert.Single(b.Result.Converters!);
        Assert.Equal(ta.ActivePowerPu.Count, tb.ActivePowerPu.Count);
        for (int k = 0; k < ta.ActivePowerPu.Count; k++)
        {
            Same(ta.ActivePowerPu[k], tb.ActivePowerPu[k], "P", k);
            Same(ta.TerminalVoltagePu[k], tb.TerminalVoltagePu[k], "V", k);
            Same(a.Buses.MagnitudePu[0][k], b.Buses.MagnitudePu[0][k], "bus V", k);
        }
        // With H = 3 s the rotor swings after the fault: the active power after clearing is not the pre-fault 0.5.
        Assert.True(Math.Abs(ta.ActivePowerPu[^1] - 0.5) > 1e-6, $"P at the end {ta.ActivePowerPu[^1]:R}");
    }

    // ------------------------------------------------------------------------------------------------ 2. relay = built-in 27

    /// <summary>
    /// The built-in unit 27 stage (uv1Pu 0.8, uv1DelayS 0.05) on case9 G3 against SAMPLE_UV27 with the same settings, on the
    /// bus 6 fault from 1.0 s to 1.1 s (a fault the built-in stage trips G3 on, FullModelUnitRelayTests). The plug-in reads the
    /// same |V| (the unit bus, from the same solve) and keeps the same whole-step timer, so it must operate at the same step and
    /// the run must be the same bits on every sample.
    /// </summary>
    [Fact]
    public void A_plugin_undervoltage_relay_trips_at_the_same_step_as_the_built_in_27_and_the_run_is_the_same_bits()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy builtIn = RmsTransientStability.StudyEventsWithUnitRelays(
            m.WithUnitProtection(Set(("G3", new[] { ("uv1Pu", 0.8), ("uv1DelayS", 0.05) }))), pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0);
        PluginSet set = LoadContract2("SAMPLE_UV27");
        var relay = new UserRelayBinding("G3", new PluginModel(set.Find("SAMPLE_UV27")!, P(("PICKUP", 0.8), ("DELAY", 0.05), ("CODE", 27.0))));
        RmsTransientStability.UnitRelayStudy plugged = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev, new[] { relay },
            stepSeconds: Step, durationSeconds: 2.0);

        FullModelUnitRelayRecord a = Assert.Single(builtIn.UnitRelays);
        FullModelUnitRelayRecord b = Assert.Single(plugged.UnitRelays);
        Assert.Equal("27-1", a.Device);
        Assert.Equal("PLUGIN:SAMPLE_UV27", b.Device);
        Assert.True(a.Tripped);
        Assert.True(b.Tripped);
        Same(a.OperateSeconds!.Value, b.OperateSeconds!.Value, "operate time", 0);
        Same(a.DisconnectedSeconds!.Value, b.DisconnectedSeconds!.Value, "disconnect time", 0);
        Assert.True(b.OperateSeconds >= 1.05 - 1e-9 && b.OperateSeconds < 1.1, $"operate {b.OperateSeconds:R}");
        Assert.Equal(27.0, b.OperateQuantity);   // the plug-in's reason code
        Assert.Contains(PluginContract.OutsideLabel, b.Settings, StringComparison.Ordinal);
        Assert.Contains("contract 2", b.Settings, StringComparison.Ordinal);
        Assert.Null(plugged.EndReason);
        Assert.Equal(Digest(builtIn.Result), Digest(plugged.Result));
        for (int i = 0; i < builtIn.Result.Machines.Count; i++)
            for (int k = 0; k < builtIn.Result.Machines[i].SpeedPu.Count; k++)
            {
                Same(builtIn.Result.Machines[i].SpeedPu[k], plugged.Result.Machines[i].SpeedPu[k], $"machine {i} speed", k);
                Same(builtIn.Result.Machines[i].DeltaRad[k], plugged.Result.Machines[i].DeltaRad[k], $"machine {i} angle", k);
            }
    }

    [Fact]
    public void A_plugin_relay_that_never_operates_changes_no_bit_of_the_run()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = FullModelKernelDigestTests.Events(m);
        PluginSet set = LoadContract2("SAMPLE_UV27");
        var quiet = new UserRelayBinding("G2", new PluginModel(set.Find("SAMPLE_UV27")!, P(("PICKUP", 0.01), ("DELAY", 1.0), ("CODE", 1.0))));
        MultiMachineSubtransient.Result off = RmsTransientStability.StudyEvents(m, pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0);
        RmsTransientStability.UnitRelayStudy on = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev, new[] { quiet },
            stepSeconds: Step, durationSeconds: 2.0);
        Assert.False(Assert.Single(on.UnitRelays).Operated);
        Assert.Equal(Digest(off), Digest(on.Result));
    }

    // ------------------------------------------------------------------------------------------------ 3. latch

    /// <summary>The bus 6 fault lasts 0.1 s. SAMPLE_UV27 with a 0.3 s delay resets when the voltage recovers, so it never
    /// operates; SAMPLE_LATCH (the same element with a sealed-in pickup) picks up at a step inside the fault and operates 0.3 s
    /// later, once: the unit is out from the next step and the run continues to its end.</summary>
    [Fact]
    public void A_latched_plugin_relay_trips_once_after_its_delay_and_the_unit_stays_out()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        PluginSet set = LoadContract2("SAMPLE_UV27", "SAMPLE_LATCH");
        var plain = new UserRelayBinding("G3", new PluginModel(set.Find("SAMPLE_UV27")!, P(("PICKUP", 0.8), ("DELAY", 0.3), ("CODE", 27.0))));
        var latch = new UserRelayBinding("G3", new PluginModel(set.Find("SAMPLE_LATCH")!, P(("PICKUP", 0.8), ("DELAY", 0.3), ("CODE", 86.0))));

        RmsTransientStability.UnitRelayStudy p = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev, new[] { plain },
            stepSeconds: Step, durationSeconds: 2.0);
        Assert.False(Assert.Single(p.UnitRelays).Operated);

        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev, new[] { latch },
            stepSeconds: Step, durationSeconds: 2.0);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.True(r.Tripped);
        Assert.Equal(1, s.UnitRelays.Count(x => x.Tripped));
        // Picked up at a step inside the fault window [1.0, 1.1), so it operates in [1.3, 1.4).
        Assert.True(r.OperateSeconds >= 1.3 - 1e-9 && r.OperateSeconds < 1.4, $"operate {r.OperateSeconds:R}");
        Assert.Equal(r.OperateSeconds!.Value + Step, r.DisconnectedSeconds!.Value, 12);
        Assert.Equal(86.0, r.OperateQuantity);
        Assert.Null(s.EndReason);
        Assert.Equal((int)Math.Round(2.0 / Step) + 1, s.Result.Machines[0].TimeSeconds.Count);
        // Not the same run as the plain relay's: the unit left.
        Assert.NotEqual(Digest(p.Result), Digest(s.Result));
    }

    // ------------------------------------------------------------------------------------------------ 4. contract 1 unchanged

    [Fact]
    public void Contract_1_plugins_and_configurations_load_exactly_as_before()
    {
        // The five contract 1 roles keep their numbers; machine and relay are appended.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, new[] { UserModelRole.Exciter, UserModelRole.Governor, UserModelRole.Stabiliser,
            UserModelRole.Injection, UserModelRole.Load, UserModelRole.Machine, UserModelRole.Relay }.Select(r => (int)r).ToArray());
        Assert.Equal(1, PluginContract.Version);
        Assert.Equal(2, PluginContract.Version2);

        // A contract 1 configuration with no "contract" member, and the same with "contract": 1, give the same models.
        PluginSet plain = PluginLoader.Load(Configure("GridSim.ModelPlugin.Sample", "s1.dll", null, "SAMPLE_TGOV1", "SAMPLE_PQ"));
        PluginSet said = PluginLoader.Load(Configure("GridSim.ModelPlugin.Sample", "s1b.dll", 1, "SAMPLE_TGOV1", "SAMPLE_PQ"));
        Assert.Equal(plain.Models.Select(x => (x.Id, x.RoleText, x.ContractVersion, x.AssemblySha256)), said.Models.Select(x => (x.Id, x.RoleText, x.ContractVersion, x.AssemblySha256)));
        Assert.All(plain.Models, x => Assert.Equal(1, x.ContractVersion));
        Assert.Null(plain.Config.Assemblies[0].Contract);
        Assert.Equal(1, said.Config.Assemblies[0].Contract);
        Assert.StartsWith("plug-in model SAMPLE_TGOV1 (governor) from GridSim.ModelPlugin.Sample sha256:", new PluginModel(plain.Find("SAMPLE_TGOV1")!).ProvenanceNote, StringComparison.Ordinal);
        Assert.Contains(" contract 1;", new PluginModel(plain.Find("SAMPLE_TGOV1")!).ProvenanceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_whose_contract_differs_from_the_assembly_is_refused_and_nothing_loads()
    {
        string wrong = Configure("GridSim.ModelPlugin.Contract2Sample", "c2w.dll", 1, "SAMPLE_GENCLS");
        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(wrong));
        Assert.Contains("the entry says contract 1 but the assembly declares contract 2", ex.Message, StringComparison.Ordinal);
        AssertNothingLoaded(wrong);
        string bad = F("bad.plugins.json");
        File.Copy(BuiltDll("GridSim.ModelPlugin.Contract2Sample"), F("c2b.dll"), overwrite: true);
        File.WriteAllText(bad, JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { new { path = "c2b.dll", sha256 = Sha(F("c2b.dll")), models = new[] { "SAMPLE_GENCLS" }, contract = 3 } } }));
        Assert.Contains(".contract must be 1 or 2", Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(bad)).Message, StringComparison.Ordinal);
        AssertNothingLoaded(bad);
        // The pin still holds for a contract 2 assembly: a wrong hash is refused before anything loads.
        string pinned = Configure("GridSim.ModelPlugin.Contract2Sample", "c2p.dll", 2, "SAMPLE_GENCLS");
        File.WriteAllText(pinned, File.ReadAllText(pinned).Replace(Sha(F("c2p.dll")), new string('0', 64), StringComparison.Ordinal));
        Assert.Contains("not the pinned", Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(pinned)).Message, StringComparison.Ordinal);
        AssertNothingLoaded(pinned);
    }

    // ------------------------------------------------------------------------------------------------ 5. lint

    [Fact]
    public void A_relay_plugin_that_reads_the_clock_is_refused_before_it_loads()
    {
        PluginVerification v = PluginVerifier.Verify(File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.Contract2Clock")));
        Assert.Equal(2, v.ContractVersion);
        Assert.Contains(v.Refusals, r => r.StartsWith("System.DateTime.get_Now (referenced by GridSim.ModelPlugin.Contract2Clock.ClockRelay.Evaluate)", StringComparison.Ordinal));
        string config = Configure("GridSim.ModelPlugin.Contract2Clock", "clock.dll", 2, "CLOCK_RELAY");
        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
        Assert.Contains("the determinism check refuses", ex.Message, StringComparison.Ordinal);
        Assert.Contains("System.DateTime.get_Now", ex.Message, StringComparison.Ordinal);
        AssertNothingLoaded(config);
        // The contract 2 sample passes the same lint with no refusal at all.
        Assert.Empty(PluginVerifier.Verify(File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.Contract2Sample"))).Refusals);
    }

    // ------------------------------------------------------------------------------------------------ 6. CLI = API

    private static object[] Case14Channels() => new object[]
    {
        new { kind = "rotorAngle", machine = new { bus = 1, id = "1" } }, new { kind = "speed", machine = new { bus = 1, id = "1" } },
        new { kind = "frequency", machine = new { bus = 1, id = "1" } }, new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } },
        new { kind = "speed", machine = new { bus = 2, id = "1" } }, new { kind = "frequency", machine = new { bus = 2, id = "1" } },
        new { kind = "busVoltage", bus = 4 },
    };

    /// <summary>The case14 study of DynamicsStudyTestSupport.Request (Fault4, Channels(M(1), M(2)), 0.005 s, 2 s) as a study file.</summary>
    private string Case14Study()
    {
        File.Copy(Psse("case14.raw"), F("case14.raw"), overwrite: true);
        File.Copy(Study("case14-study.dyr"), F("case14.dyr"), overwrite: true);
        string path = F("study.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(F("case14.raw")) },
            dyr = new { path = "case14.dyr", sha256 = Sha(F("case14.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 2.0 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = Case14Channels(),
            cases = new object[]
            {
                new { name = "fault-4", kind = "events", events = new object[]
                {
                    new { kind = "busFault", at = 0.1, bus = 4 }, new { kind = "faultClear", at = 0.15, bus = 4 },
                    new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                } },
            },
        }));
        return path;
    }

    private static DynamicsStudyRequest Case14Api(IReadOnlyList<DeviceModelAssignment>? devices) =>
        Request(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")), new[] { Fault4("fault-4") }, duration: 2.0) with { DeviceModels = devices };

    /// <summary>
    /// The WP12 injection model (SAMPLE_PQ, a constant-power current source) drives the bus 3 unit of the case14 study, once
    /// through the API (DynamicsStudyRequest.DeviceModels) and once through the CLI (the assignments file's deviceModels): every
    /// channel sample is the same bits, the result names the plug-in with OUTSIDE THE VERIFIED CONTRACT, and the run is not the
    /// incumbent one (bus 3 is no longer a synchronous condenser).
    /// </summary>
    [Fact]
    public void An_injection_model_through_the_cli_gives_the_api_bits_and_carries_the_label()
    {
        string study = Case14Study();
        string config = Configure("GridSim.ModelPlugin.Sample", "sample.dll", null, "SAMPLE_PQ");
        string assign = F("assign.json");
        File.WriteAllText(assign, JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            deviceModels = new[] { new { role = "injection", bind = new { machine = new { bus = 3, id = "1" } }, plugin = "SAMPLE_PQ" } },
        }));

        PluginSet set = PluginLoader.Load(config);
        var device = UserDeviceModel.FromPlugin(new PluginModel(set.Find("SAMPLE_PQ")!));
        DynamicsStudyResult api = DynamicsStudyRunner.Run(Case14Api(new[] { DeviceModelAssignment.AtMachine(M(3), device) }));
        DynamicsStudyResult incumbent = DynamicsStudyRunner.Run(Case14Api(null));
        Assert.Contains(api.Coverage.ReaderNotes, n => n.StartsWith("plug-in model SAMPLE_PQ (injection) from GridSim.ModelPlugin.Sample sha256:", StringComparison.Ordinal)
            && n.Contains(PluginContract.OutsideLabel, StringComparison.Ordinal) && n.Contains("bound to", StringComparison.Ordinal));

        CliProcess.Result cli = CliProcess.RunBuilt("dynamics-study", study, "--model-plugins", config, "--model-assignments", assign);
        Assert.True(cli.ExitCode is 0 or 3, cli.StdErr);
        using JsonDocument doc = JsonDocument.Parse(cli.StdOut);
        JsonElement root = doc.RootElement;
        Assert.Equal(api.Status, root.GetProperty("status").GetString());
        DynamicsRunEvidence run = api.Cases[0].Run!;
        JsonElement channels = root.GetProperty("cases")[0].GetProperty("run").GetProperty("channels");
        Assert.Equal(run.Channels.Count, channels.GetArrayLength());
        bool differs = false;
        DynamicsRunEvidence old = incumbent.Cases[0].Run!;
        for (int c = 0; c < run.Channels.Count; c++)
        {
            JsonElement values = channels[c].GetProperty("values");
            Assert.Equal(run.Channels[c].Values.Count, values.GetArrayLength());
            for (int k = 0; k < run.Channels[c].Values.Count; k++)
            {
                Same(run.Channels[c].Values[k], values[k].GetDouble(), run.Channels[c].Label, k);
                differs |= BitConverter.DoubleToInt64Bits(run.Channels[c].Values[k]) != BitConverter.DoubleToInt64Bits(old.Channels[c].Values[k]);
            }
        }
        Assert.True(differs, "the injection model must change the run");
        JsonElement dm = root.GetProperty("provenance").GetProperty("deviceModels")[0];
        Assert.Equal("injection", dm.GetProperty("role").GetString());
        Assert.Equal("SAMPLE_PQ", dm.GetProperty("model").GetString());
        Assert.Equal(device.Hash, dm.GetProperty("sha256").GetString());
        Assert.Equal(PluginContract.OutsideLabel, dm.GetProperty("label").GetString());
        // The incumbent CLI result has no deviceModels member at all.
        CliProcess.Result plain = CliProcess.RunBuilt("dynamics-study", study);
        Assert.True(plain.ExitCode is 0 or 3, plain.StdErr);
        using JsonDocument p = JsonDocument.Parse(plain.StdOut);
        Assert.False(p.RootElement.GetProperty("provenance").TryGetProperty("deviceModels", out _));
    }

    [Fact]
    public void A_machine_model_through_the_cli_records_contract_2_and_the_label()
    {
        string study = Case14Study();
        string config = Configure("GridSim.ModelPlugin.Contract2Sample", "c2.dll", 2, "SAMPLE_GENCLS", "SAMPLE_UV27");
        string assign = F("assign-machine.json");
        File.WriteAllText(assign, JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            deviceModels = new[] { new { role = "machine", bind = new { machine = new { bus = 3, id = "1" } }, plugin = "SAMPLE_GENCLS",
                parameters = new Dictionary<string, double> { ["H"] = 4.0, ["D"] = 0.0, ["XDP"] = 0.18, ["F0"] = 60.0 } } },
        }));
        CliProcess.Result cli = CliProcess.RunBuilt("dynamics-study", study, "--model-plugins", config, "--model-assignments", assign);
        Assert.True(cli.ExitCode is 0 or 3, cli.StdErr);
        using (JsonDocument doc = JsonDocument.Parse(cli.StdOut))
        {
            JsonElement dm = doc.RootElement.GetProperty("provenance").GetProperty("deviceModels")[0];
            Assert.Equal("machine", dm.GetProperty("role").GetString());
            Assert.Equal(2, dm.GetProperty("contractVersion").GetInt32());
            Assert.Equal(PluginContract.OutsideLabel, dm.GetProperty("label").GetString());
            Assert.Contains("plug-in model SAMPLE_GENCLS (machine)", cli.StdOut, StringComparison.Ordinal);
        }
        // RC5 wp32b: a relay entry is no longer refused; it is bound and run (Rc5Wp32bRelayTests).
    }

    // ------------------------------------------------------------------------------------------------ 7. label

    /// <summary>The load role, constant impedance: p = q = (vm / v0)^2 (the WP12 fixture).</summary>
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

    [Fact]
    public void A_declarative_load_model_is_named_without_the_label_and_a_plugin_machine_with_it()
    {
        var load = DeviceModelAssignment.AtBus(4, 10.0, 0.0, UserDeviceModel.FromDeclarative(UserModel.FromDocument(ModelDocument.Parse(ConstZLoad))));
        DynamicsStudyResult declared = DynamicsStudyRunner.Run(Case14Api(new[] { load }));
        Assert.Contains(declared.Coverage.ReaderNotes, n => n.StartsWith("user model ZLOAD (load) sha256:", StringComparison.Ordinal) && n.Contains("bus 4", StringComparison.Ordinal));
        Assert.DoesNotContain(declared.Coverage.ReaderNotes, n => n.Contains(PluginContract.OutsideLabel, StringComparison.Ordinal));
        Assert.NotNull(declared.Cases[0].Run);

        PluginSet set = LoadContract2("SAMPLE_GENCLS");
        var machine = DeviceModelAssignment.AtMachine(M(3), UserDeviceModel.FromPlugin(new PluginModel(set.Find("SAMPLE_GENCLS")!,
            P(("H", 4.0), ("D", 0.0), ("XDP", 0.18), ("F0", 60.0)))));
        DynamicsStudyResult plugged = DynamicsStudyRunner.Run(Case14Api(new[] { machine }));
        Assert.Contains(plugged.Coverage.ReaderNotes, n => n.StartsWith("plug-in model SAMPLE_GENCLS (machine) from GridSim.ModelPlugin.Contract2Sample sha256:", StringComparison.Ordinal)
            && n.Contains(" contract 2;", StringComparison.Ordinal) && n.Contains(PluginContract.OutsideLabel, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ 8. bit identity and replay

    [Fact]
    public void Without_device_models_the_study_is_the_incumbent_one()
    {
        DynamicsStudyResult a = DynamicsStudyRunner.Run(Case14Api(null));
        DynamicsStudyResult b = DynamicsStudyRunner.Run(Case14Api(Array.Empty<DeviceModelAssignment>()));
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(JsonSerializer.Serialize(a.Coverage), JsonSerializer.Serialize(b.Coverage));
        Assert.Equal(JsonSerializer.Serialize(a.Cases), JsonSerializer.Serialize(b.Cases));
    }

    [Fact]
    public void The_journal_lists_a_declarative_device_model_file_so_a_replay_re_verifies_it()
    {
        File.WriteAllText(F("z.model.json"), ConstZLoad);
        string assign = F("assign-z.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "gridsim-model-assignments/1",
            deviceModels = new[] { new { role = "load", bind = new { bus = 4, pMw = 10.0, qMvar = 0.0 }, file = "z.model.json", sha256 = Sha(F("z.model.json")) } },
        });
        File.WriteAllBytes(assign, bytes);
        DynamicsInputs.NamedFile f = Assert.Single(DynamicsInputs.Expand(assign, bytes));
        Assert.Equal("assignments:deviceModels[0].file", f.Label);
        Assert.Equal(Path.GetFullPath(F("z.model.json")), f.Full);
        Assert.True(f.Snapshot);
        // The parser reads the same entry, pinned by its hash.
        DeviceModelAssignment a = Assert.Single(ModelAssignmentsFile.ParseDevices(bytes, assign, null, Step));
        Assert.Equal("load", a.Device.Role);
        Assert.Equal(4, a.Bus);
        File.WriteAllText(F("z.model.json"), ConstZLoad + " ");
        Assert.Contains("not the pinned", Assert.Throws<InvalidDataException>(() => ModelAssignmentsFile.ParseDevices(bytes, assign, null, Step)).Message, StringComparison.Ordinal);
    }
}
