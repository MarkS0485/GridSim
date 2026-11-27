using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP32/wp32b review fixes (review-wp32.md): an injection or machine model on a unit whose DYR record has a governor runs
/// (the governor is not used, as the exciter is not); a plug-in may not take a name GridSim grants its internals to; the
/// factory's role is read once; a relay plug-in is replayed with a branch's inputs; the default study is pinned to a digest
/// taken before WP32; and the margins behind two WP32/wp32b assertions are derived and demonstrated.
/// </summary>
public sealed class Rc5Wp32ReviewFixTests : IDisposable
{
    private const double Step = 0.005;

    private readonly string _dir = TestTemp.Under("gridsim-wp32-review-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp32ReviewFixTests() => Directory.CreateDirectory(_dir);

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

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string F(string name) => Path.Combine(_dir, name);

    /// <summary>Write <paramref name="bytes"/> as <paramref name="file"/> and a configuration listing it with <paramref name="ids"/>.</summary>
    private string ConfigureBytes(byte[] bytes, string file, params string[] ids)
    {
        File.WriteAllBytes(F(file), bytes);
        string config = F(Path.GetFileNameWithoutExtension(file) + ".plugins.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { new { path = file, sha256 = Sha(bytes), models = ids } } }));
        return config;
    }

    private PluginSet Load(string project, string file, params string[] ids) => PluginLoader.Load(ConfigureBytes(File.ReadAllBytes(BuiltDll(project)), file, ids));

    private static void AssertNothingLoaded(string configPath)
    {
        string name = PluginLoader.ContextName(Path.GetFullPath(configPath));
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == name && c.Assemblies.Any());
    }

    private static Dictionary<string, double> P(params (string, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    private static DynamicsStudyRequest Case14Api(IReadOnlyList<DeviceModelAssignment>? devices) =>
        Request(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")), new[] { Fault4("fault-4") }, duration: 2.0) with { DeviceModels = devices };

    // ------------------------------------------------------------------------------------------------ 1. a governed unit

    /// <summary>
    /// case14-study.dyr gives the bus 3 unit an IEEEG1 governor. An injection model (SAMPLE_PQ) or a machine model (SAMPLE_GENCLS)
    /// on that unit makes it converter plant, which the kernel refuses to govern ("data[k].Governor is set, but generator k ... is
    /// not integrated as a synchronous machine"). Before this fix the runner cleared the unit's exciter but not its governor, so
    /// the study stopped at initialisation and every row was NOT RUN (the cause of the CLI test's null run: the API row was null
    /// too). Now the row runs, the unit is not among the integrated machines, and nothing says a governor was refused.
    /// </summary>
    [Fact]
    public void An_injection_or_machine_model_on_a_unit_with_a_DYR_governor_runs_and_the_governor_is_not_used()
    {
        // The fixture's premise: the incumbent study integrates the bus 3 unit with its IEEEG1 governor.
        DynamicsStudyResult incumbent = DynamicsStudyRunner.Run(Case14Api(null));
        DynamicsInitialMachine unit3 = Assert.Single(incumbent.Initialisation!.Machines, x => x.Bus == 3);
        Assert.Equal("IEEEG1", unit3.Governor);

        PluginSet one = Load("GridSim.ModelPlugin.Sample", "s1.dll", "SAMPLE_PQ");
        PluginSet two = Load("GridSim.ModelPlugin.Contract2Sample", "c2.dll", "SAMPLE_GENCLS");
        var injection = UserDeviceModel.FromPlugin(new PluginModel(one.Find("SAMPLE_PQ")!));
        var machine = UserDeviceModel.FromPlugin(new PluginModel(two.Find("SAMPLE_GENCLS")!, P(("H", 4.0), ("D", 0.0), ("XDP", 0.18), ("F0", 60.0))));
        foreach (UserDeviceModel device in new[] { injection, machine })
        {
            DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14Api(new[] { DeviceModelAssignment.AtMachine(M(3), device) }));
            Assert.DoesNotContain(r.Reasons, x => x.Contains("Governor is set", StringComparison.Ordinal));
            DynamicsCaseResult row = Assert.Single(r.Cases);
            Assert.NotEqual(DynamicsDisposition.NotRun, row.Disposition);
            Assert.NotNull(row.Run);
            Assert.NotNull(r.Initialisation);
            Assert.DoesNotContain(r.Initialisation!.Machines, x => x.Bus == 3);
            Assert.Contains(r.Coverage.ReaderNotes, n => n.StartsWith($"plug-in model {device.Id} ({device.Role})", StringComparison.Ordinal)
                && n.Contains("DYR data for it, if any, are not used", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------------------------------------ 2. friend assembly names

    /// <summary>The contract 2 sample with its assembly name (the AssemblyDefinition row's #Strings entry) overwritten in place by
    /// <paramref name="name"/> and a terminating zero. The sample's own name is longer, so the new name fits.</summary>
    private static byte[] Renamed(string name)
    {
        byte[] bytes = File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.Contract2Sample"));
        int at;
        int length;
        using (var pe = new PEReader(ImmutableArray.Create(bytes)))
        {
            MetadataReader md = pe.GetMetadataReader();
            StringHandle h = md.GetAssemblyDefinition().Name;
            length = Encoding.UTF8.GetByteCount(md.GetString(h));
            at = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(h);
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        Assert.True(utf8.Length < length, $"'{name}' does not fit in place of a {length}-byte name");
        Array.Copy(utf8, 0, bytes, at, utf8.Length);
        bytes[at + utf8.Length] = 0;
        return bytes;
    }

    /// <summary>
    /// GridSim.Core gives its internals to the assemblies its InternalsVisibleTo attributes name (GridSim.Tests among them),
    /// unsigned, so an assembly of that simple name would be given them too. A plug-in named as any of them is refused before
    /// anything loads, whatever the case of the name; the names are read here from the attributes, independently of the loader.
    /// </summary>
    [Fact]
    public void A_plugin_named_as_an_assembly_gridsim_grants_its_internals_to_is_refused_and_nothing_loads()
    {
        string[] friends = typeof(PluginLoader).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName.Split(',')[0].Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Contains("GridSim.Tests", friends);
        foreach (string friend in friends) Assert.Contains(friend, PluginLoader.FriendAssemblyNames());

        int i = 0;
        foreach (string name in friends.Append("gridsim.TESTS"))
        {
            byte[] bytes = Renamed(name);
            Assert.Equal(name, PluginVerifier.Verify(bytes).AssemblyName);
            string config = ConfigureBytes(bytes, $"friend{i++}.dll", "SAMPLE_GENCLS");
            var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
            Assert.Contains($"the assembly is named {name}, a name GridSim grants access to its internals (InternalsVisibleTo); a plug-in may not take it.",
                ex.Message, StringComparison.Ordinal);
            AssertNothingLoaded(config);
        }

        // The same bytes under a name that is no friend's load (the rename alone is not what is refused).
        PluginSet ok = PluginLoader.Load(ConfigureBytes(Renamed("GridSim.Testz"), "notfriend.dll", "SAMPLE_GENCLS"));
        Assert.Equal("GridSim.Testz", Assert.Single(ok.Models).AssemblyName);
    }

    // ------------------------------------------------------------------------------------------------ 3. the role, read once

    private sealed class OneReadFactory(UserModelRole role) : IUserModelFactory
    {
        private int _reads;

        public int Reads => _reads;

        public string Id => "ONE_READ";

        public UserModelRole Role
        {
            get
            {
                _reads++;
                return _reads == 1 ? role : (UserModelRole)99;
            }
        }

        public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

        public object Create(IReadOnlyList<double> parameters) => new object();
    }

    /// <summary>The loader reads a factory's role once and hands it to the definition, which does not read it again: a factory
    /// that answers 99 (no role) on its second read is held with the role it gave first.</summary>
    [Fact]
    public void A_definition_takes_the_role_the_loader_read_and_does_not_read_the_factory_again()
    {
        var f = new OneReadFactory(UserModelRole.Relay);
        UserModelRole read = f.Role;
        Assert.Equal(1, f.Reads);
        var def = new PluginModelDefinition(f, read, "Fake", "fake.dll", new string('0', 64), PluginContract.Version2);
        Assert.Equal(1, f.Reads);
        Assert.Equal(UserModelRole.Relay, def.Role);
        Assert.Equal("relay", def.RoleText);
    }

    // ------------------------------------------------------------------------------------------------ 4. branch-relay replay

    /// <summary>A relay that shares a static counter between instances, but only when it is given no frequency (as on a branch):
    /// with a unit's inputs it never trips and is deterministic.</summary>
    private sealed class NanSharedRelayFactory : IUserModelFactory
    {
        public string Id => "NAN_SHARED";

        public UserModelRole Role => UserModelRole.Relay;

        public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

        public object Create(IReadOnlyList<double> parameters) => new NanSharedRelay();
    }

    private sealed class NanSharedRelay : IUserRelay
    {
        private static int s_calls;

        public void Initialise() { }

        public RelayDecision Evaluate(in RelayInputs inputs)
            => double.IsNaN(inputs.FrequencyHz) && Interlocked.Increment(ref s_calls) % 2 == 0 ? RelayDecision.TripWith(7) : RelayDecision.None;
    }

    private sealed class PlainRelayFactory : IUserModelFactory
    {
        public string Id => "PLAIN_RELAY";

        public UserModelRole Role => UserModelRole.Relay;

        public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

        public object Create(IReadOnlyList<double> parameters) => new PlainRelay();
    }

    /// <summary>Trips below 0.8 pu (the probe dips to 0.7 at step 100); reads nothing else, so NaN inputs change nothing.</summary>
    private sealed class PlainRelay : IUserRelay
    {
        public void Initialise() { }

        public RelayDecision Evaluate(in RelayInputs inputs) => inputs.VoltagePu < 0.8 ? RelayDecision.TripWith(27) : RelayDecision.None;
    }

    /// <summary>
    /// The replay check gives a relay a branch's inputs too (frequency, rate and speed NaN). The shared counter advances once per
    /// call of either instance, so in the paired run each instance sees every other count and its trip flag no longer alternates
    /// as in its solo run: it differs at step 1 or 2 of the branch replay, and the model is refused when bound. With only a
    /// unit's inputs (the check before this fix) it never touches the counter. A relay that reads only the voltage passes both.
    /// </summary>
    [Fact]
    public void A_relay_that_shares_state_only_on_branch_inputs_is_refused_by_the_replay()
    {
        var bad = new PluginModelDefinition(new NanSharedRelayFactory(), "Fake", "fake.dll", new string('0', 64), PluginContract.Version2);
        var ex = Assert.Throws<PluginRefusedException>(() => new PluginModel(bad));
        Assert.True(ex.Message is "plug-in model NAN_SHARED: not deterministic (replay differs at step 1)."
            or "plug-in model NAN_SHARED: not deterministic (replay differs at step 2).", ex.Message);

        var good = new PluginModelDefinition(new PlainRelayFactory(), "Fake", "fake.dll", new string('0', 64), PluginContract.Version2);
        Assert.Equal("PLAIN_RELAY", new PluginModel(good).Id);
    }

    // ------------------------------------------------------------------------------------------------ 5. default bits pinned

    /// <summary>
    /// The dynamics-study digest of FullModelKernelDigestTests (case14 RAW + DYR, the baseline rows, 2 s), taken on the kernel at
    /// d7655da6, before WP32 existed. The request with empty device-model and relay-model lists must give it, as the request
    /// without them must: unlike comparing the two requests with each other, this pins both to bits taken before the change.
    /// </summary>
    private const string PreWp32DynamicsStudyDigest = "3371237C7599B199E0E258ADB476258467CFC3A9722B1305D5AF6971C823D026";

    private static string StudyDigest(DynamicsStudyRequest request)
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(request);
        string json = JsonSerializer.Serialize(new { r.Status, r.Cases, r.Counts, r.Initialisation });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Without_device_or_relay_models_the_study_is_the_pre_wp32_digest()
    {
        DynamicsStudyRequest plain = Case14(UnitRelayInLoopTests.BaselineCases(), duration: 2.0);
        Assert.Equal(PreWp32DynamicsStudyDigest, StudyDigest(plain));
        Assert.Equal(PreWp32DynamicsStudyDigest, StudyDigest(plain with { DeviceModels = Array.Empty<DeviceModelAssignment>() }));
        Assert.Equal(PreWp32DynamicsStudyDigest, StudyDigest(plain with { RelayModels = Array.Empty<RelayModelAssignment>() }));
        Assert.Equal(PreWp32DynamicsStudyDigest, StudyDigest(plain with
        {
            DeviceModels = Array.Empty<DeviceModelAssignment>(),
            RelayModels = Array.Empty<RelayModelAssignment>(),
        }));
    }

    // ------------------------------------------------------------------------------------------------ 6. fault-voltage margin

    private const double V2 = 0.9987460731103327;   // the WP12 two-bus operating point (Rc5Wp32UserMachineRelayTests)

    /// <summary>
    /// The closed-form faulted voltage of Rc5Wp32UserMachineRelayTests test 1, re-derived outside the repository in 40-digit
    /// arithmetic (mpmath): with P2 = 0.5, X = 0.1 and |V2| = V2, d = asin(P2 X / V2), E1 = V1 + j 1e-4 conj(S1 / V1),
    /// E2 = V2 + j 0.1 conj(S2 / V2), and V2f = (E1 Yl + E2 Y2) / (Yl + Y2 + Yf) gives |V2f| = 0.49924828685830020802.
    /// Its distance below 0.5 (the bound that test asserts) is 7.5171e-4. The double evaluation from the power flow (solved to
    /// 1e-13) agrees with it to 1e-12 (the bound the sample comparison uses), so the margin is at least 7.5171e-4 - 1e-12, which
    /// is above 7.5e-4; every faulted sample of that test is within 1e-12 of the closed form, so it is below 0.5 by as much.
    /// </summary>
    [Fact]
    public void The_closed_form_fault_voltage_is_the_high_precision_value_and_lies_7_5e_4_below_one_half()
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
        Complex BusV(int bus)
        {
            int i = pf.BusIds.ToList().IndexOf(bus);
            return Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
        }
        Complex BusS(int bus)
        {
            int i = pf.BusIds.ToList().IndexOf(bus);
            return new Complex(pf.PInjMw[i], pf.QInjMvar[i]) / 100.0;
        }
        Complex v1 = BusV(1), v2 = BusV(2);
        Complex e1 = v1 + Complex.ImaginaryOne * 1e-4 * Complex.Conjugate(BusS(1) / v1);
        Complex e2 = v2 + Complex.ImaginaryOne * 0.1 * Complex.Conjugate(BusS(2) / v2);
        Complex yl = 1.0 / (Complex.ImaginaryOne * 0.1001), y2 = 1.0 / (Complex.ImaginaryOne * 0.1), yf = 1.0 / (Complex.ImaginaryOne * 0.05);
        double faulted = ((e1 * yl + e2 * y2) / (yl + y2 + yf)).Magnitude;

        Assert.True(Math.Abs(faulted - 0.49924828685830020802) <= 1e-12, $"|V2f| {faulted:R}");
        Assert.True(0.5 - faulted >= 7.5e-4, $"margin {0.5 - faulted:R}");
    }

    // ------------------------------------------------------------------------------------------------ 7. branch-current margins

    /// <summary>A definite-time overcurrent element on |I| (over = 1 while i &gt;= PICKUP), as Rc5Wp32bRelayTests' OC51.</summary>
    private const string Oc51 = """
        { "format": "gridsim-model/1", "id": "OC51", "role": "relay", "source": "definite-time overcurrent element (test fixture)",
          "parameters": [ { "name": "PICKUP", "unit": "pu", "value": 1.5 }, { "name": "DELAY", "unit": "s", "value": 0.03, "min": 0 },
                          { "name": "CODE", "unit": "1", "value": 51 } ],
          "ports": { "inputs": [ { "name": "vm", "unit": "pu" }, { "name": "f", "unit": "1" }, { "name": "p", "unit": "pu" }, { "name": "q", "unit": "pu" },
                                 { "name": "dfdt", "unit": "1/s" }, { "name": "speed", "unit": "pu" }, { "name": "i", "unit": "pu" } ],
                     "outputs": [ { "name": "trip", "unit": "1", "from": "out" } ] },
          "blocks": [
            { "id": "over", "type": "switch", "inputs": ["i", 1, 0], "params": { "threshold": "@PICKUP" } },
            { "id": "timer", "type": "ontimer", "inputs": ["over"], "params": { "time": "@DELAY" } },
            { "id": "out", "type": "gain", "inputs": ["timer"], "params": { "k": "@CODE" } } ],
          "initialisation": { "inputs": [], "steps": [ { "action": "initialise", "block": "timer", "values": [0] } ] } }
        """;

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

    /// <summary>
    /// The |I| at bus 5 of case9 branch 5-6 before the fault, re-derived outside the repository from the MATPOWER case9 power
    /// flow (Newton to 1e-14): I = (y + j b/2) V5 - y V6 with y = 1/(0.039 + j0.17) and b = 0.358 gives 0.6020452128689695 pu.
    /// The kernel holds the power-flow voltages before the fault. GridSim solves the power flow to 1e-8 pu mismatch; with
    /// ||J^-1|| = 1.23 (infinity norm, same derivation) each bus voltage is within about 2.5e-8 pu, and |y + jb/2| + |y| = 11.3,
    /// so the current is within 2.8e-7 pu of the value above: 1e-6 brackets it.
    /// </summary>
    private const double PreFaultCurrent56 = 0.6020452128689695;

    private static double OperateSeconds(GridModel m, PowerFlowResult pf, int branch, double pickup, double delay)
    {
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForBranch(branch, 5, UserModel.FromDocument(ModelDocument.Parse(Oc51), P(("PICKUP", pickup), ("DELAY", delay))), "branch 5-6") },
            stepSeconds: Step, durationSeconds: 1.2);
        FullModelBranchRelayRecord r = Assert.Single(s.BranchRelays);
        Assert.True(r.Tripped, $"PICKUP {pickup:R}, DELAY {delay:R}: the relay must operate");
        return r.OperateSeconds!.Value;
    }

    /// <summary>
    /// Rc5Wp32bRelayTests' branch-trip oracle needs case9 branch 5-6 below 1.5 pu before the bus 6 fault and at or above it from
    /// the fault step until the 0.03 s timer runs out. Before the fault: an instantaneous element at the derived pre-fault
    /// current + 1e-6 does not operate before 1.0 s, and one at the current - 1e-6 does, so the current is 0.60205 pu and the
    /// margin below 1.5 is 0.898 pu. During the fault: an element at 1.9 pu with the same 0.03 s timer operates at the same step,
    /// to the bit, as the 1.5 pu one, so the current is at least 1.9 pu on every step the 1.5 pu timer counted: a margin of at
    /// least 0.4 pu. (A frozen-subtransient estimate of the first fault step, 2.28 pu, and a transient-EMF one, 2.10 pu, both
    /// derived outside the repository, put it there; this test demonstrates it on the kernel itself.)
    /// </summary>
    [Fact]
    public void The_branch_5_6_current_is_well_below_1_5_pu_before_the_fault_and_well_above_it_until_the_relay_operates()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        int b56 = -1;
        for (int j = 0; j < m.Branches.Count; j++)
            if ((m.Branches[j].From == 5 && m.Branches[j].To == 6) || (m.Branches[j].From == 6 && m.Branches[j].To == 5)) b56 = j;
        Assert.True(b56 >= 0);
        Assert.Equal(5, m.Branches[b56].From);   // measured at bus 5, the from end, as the derivation takes it

        double above = OperateSeconds(m, pf, b56, PreFaultCurrent56 + 1e-6, 0.0);
        Assert.True(above >= 1.0 - 1e-9, $"an element above the pre-fault current operated at {above:R} s, before the fault");
        double below = OperateSeconds(m, pf, b56, PreFaultCurrent56 - 1e-6, 0.0);
        Assert.True(below < 1.0, $"an element below the pre-fault current operated only at {below:R} s");

        double at15 = OperateSeconds(m, pf, b56, 1.5, 0.03);
        double at19 = OperateSeconds(m, pf, b56, 1.9, 0.03);
        Assert.True(at15 >= 1.03 - 1e-9 && at15 < 1.1, $"1.5 pu element operated at {at15:R} s");
        Assert.True(BitConverter.DoubleToInt64Bits(at15) == BitConverter.DoubleToInt64Bits(at19), $"1.5 pu at {at15:R} s, 1.9 pu at {at19:R} s");
    }
}
