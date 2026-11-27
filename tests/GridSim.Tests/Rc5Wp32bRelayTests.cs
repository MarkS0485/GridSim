using GridSim.Core.Diagnostics;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Assets;
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
/// RC5 wp32b: the WP32 follow-up. (1) The plug-in contract's refusal text of v2.1.105-RC-5 is kept byte for byte for every case
/// that existed then (the declarative format's unknown-role and unknown-block refusals likewise). (2) The declarative
/// <c>relay</c> role with the <c>ontimer</c> and <c>latch</c> blocks (whole-step timers), relays bound to branches, and relays on
/// the dynamics-study CLI through the model assignments file. Spec tests 2 and 3 are run with the declarative relay.
/// <para>The oracles: the built-in unit 27 stage, whose whole-step timer rule (pickup at the first step beyond, operation at the
/// first step k with (k - k_p) h + h 1e-6 &gt;= delay) the ontimer block reproduces by construction, for bit identity; the
/// contract 2 sample relay SAMPLE_LATCH, for the latch; a scheduled branch trip at the step a branch relay opens its branch,
/// for the branch trip path (the two runs build the same network triplets at the same steps, so they must be the same bits);
/// the API run, for the CLI route; the incumbent run, for every default output.</para>
/// </summary>
public sealed class Rc5Wp32bRelayTests : IDisposable
{
    private const double Step = 0.005;

    private readonly string _dir = TestTemp.Under("gridsim-wp32b-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp32bRelayTests() => Directory.CreateDirectory(_dir);

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

    private string ConfigureFile(string file, params string[] ids)
    {
        string config = F(Path.GetFileNameWithoutExtension(file) + ".plugins.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { schema = "gridsim-plugins/1", assemblies = new[] { new { path = file, sha256 = Sha(F(file)), models = ids } } }));
        return config;
    }

    private string Configure(string project, string file, params string[] ids)
    {
        File.Copy(BuiltDll(project), F(file), overwrite: true);
        return ConfigureFile(file, ids);
    }

    private static void Same(double a, double b, string what, int k)
        => Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"{what}: sample {k}: {a:R} ({BitConverter.DoubleToInt64Bits(a):X16}) vs {b:R} ({BitConverter.DoubleToInt64Bits(b):X16})");

    private static void SameRun(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(FullModelKernelDigestTests.Digest(a, Array.Empty<RmsEvent>()), FullModelKernelDigestTests.Digest(b, Array.Empty<RmsEvent>()));
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
        {
            Assert.Equal(a.Machines[i].SpeedPu.Count, b.Machines[i].SpeedPu.Count);
            for (int k = 0; k < a.Machines[i].SpeedPu.Count; k++)
            {
                Same(a.Machines[i].SpeedPu[k], b.Machines[i].SpeedPu[k], $"machine {i} speed", k);
                Same(a.Machines[i].DeltaRad[k], b.Machines[i].DeltaRad[k], $"machine {i} angle", k);
            }
        }
    }

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

    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int j = 0; j < m.Branches.Count; j++)
            if ((m.Branches[j].From == a && m.Branches[j].To == b) || (m.Branches[j].From == b && m.Branches[j].To == a)) return j;
        throw new InvalidOperationException($"no branch {a}-{b}");
    }

    private const string RelayPorts = """
        "ports": { "inputs": [ { "name": "vm", "unit": "pu" }, { "name": "f", "unit": "1" }, { "name": "p", "unit": "pu" }, { "name": "q", "unit": "pu" },
                               { "name": "dfdt", "unit": "1/s" }, { "name": "speed", "unit": "pu" }, { "name": "i", "unit": "pu" } ],
                   "outputs": [ { "name": "trip", "unit": "1", "from": "out" } ] },
        """;

    /// <summary>An under-voltage element with a definite-time delay: below = 1 while vm &lt; PICKUP (the switch gives its second
    /// input when vm &gt;= PICKUP, so the comparison is the built-in 27's strict one), timed by an ontimer, tripping with CODE.</summary>
    private static readonly string Uv27 = """
        { "format": "gridsim-model/1", "id": "UV27", "role": "relay", "source": "definite-time under-voltage element (test fixture)",
          "parameters": [ { "name": "PICKUP", "unit": "pu", "value": 0.8 }, { "name": "DELAY", "unit": "s", "value": 0.05, "min": 0 },
                          { "name": "CODE", "unit": "1", "value": 27 } ],
        """ + RelayPorts + """
          "blocks": [
            { "id": "below", "type": "switch", "inputs": ["vm", 0, 1], "params": { "threshold": "@PICKUP" } },
            { "id": "timer", "type": "ontimer", "inputs": ["below"], "params": { "time": "@DELAY" } },
            { "id": "out", "type": "gain", "inputs": ["timer"], "params": { "k": "@CODE" } } ],
          "initialisation": { "inputs": [], "steps": [ { "action": "initialise", "block": "timer", "values": [0] } ] } }
        """;

    /// <summary>UV27 with a sealed-in pickup (a latch before the timer): SAMPLE_LATCH written declaratively.</summary>
    private static readonly string Latch27 = """
        { "format": "gridsim-model/1", "id": "LATCH27", "role": "relay", "source": "sealed-in under-voltage element (test fixture)",
          "parameters": [ { "name": "PICKUP", "unit": "pu", "value": 0.8 }, { "name": "DELAY", "unit": "s", "value": 0.3, "min": 0 },
                          { "name": "CODE", "unit": "1", "value": 86 } ],
        """ + RelayPorts + """
          "blocks": [
            { "id": "below", "type": "switch", "inputs": ["vm", 0, 1], "params": { "threshold": "@PICKUP" } },
            { "id": "seal", "type": "latch", "inputs": ["below"] },
            { "id": "timer", "type": "ontimer", "inputs": ["seal"], "params": { "time": "@DELAY" } },
            { "id": "out", "type": "gain", "inputs": ["timer"], "params": { "k": "@CODE" } } ],
          "initialisation": { "inputs": [], "steps": [ { "action": "initialise", "block": "seal", "values": [0] },
                                                     { "action": "initialise", "block": "timer", "values": [0] } ] } }
        """;

    /// <summary>A definite-time overcurrent element on |I| (over = 1 while i &gt;= PICKUP), for a branch.</summary>
    private static readonly string Oc51 = """
        { "format": "gridsim-model/1", "id": "OC51", "role": "relay", "source": "definite-time overcurrent element (test fixture)",
          "parameters": [ { "name": "PICKUP", "unit": "pu", "value": 1.5 }, { "name": "DELAY", "unit": "s", "value": 0.03, "min": 0 },
                          { "name": "CODE", "unit": "1", "value": 51 } ],
        """ + RelayPorts + """
          "blocks": [
            { "id": "over", "type": "switch", "inputs": ["i", 1, 0], "params": { "threshold": "@PICKUP" } },
            { "id": "timer", "type": "ontimer", "inputs": ["over"], "params": { "time": "@DELAY" } },
            { "id": "out", "type": "gain", "inputs": ["timer"], "params": { "k": "@CODE" } } ],
          "initialisation": { "inputs": [], "steps": [ { "action": "initialise", "block": "timer", "values": [0] } ] } }
        """;

    /// <summary>An under-frequency element: it reads f, which a branch relay does not have.</summary>
    private static readonly string Uf81 = """
        { "format": "gridsim-model/1", "id": "UF81", "role": "relay", "source": "under-frequency element (test fixture)",
          "parameters": [ { "name": "PICKUP", "unit": "1", "value": 59.5 } ],
        """ + RelayPorts + """
          "blocks": [
            { "id": "below", "type": "switch", "inputs": ["f", 0, 1], "params": { "threshold": "@PICKUP" } },
            { "id": "out", "type": "gain", "inputs": ["below"], "params": { "k": 81 } } ],
          "initialisation": { "inputs": [], "steps": [] } }
        """;

    private static UserModel Declared(string text, Dictionary<string, double>? parameters = null)
        => UserModel.FromDocument(ModelDocument.Parse(text), parameters);

    /// <summary>A role-generic model wrapping one block of <paramref name="type"/> on input u (output y).</summary>
    private static ModelInstance OneBlock(string type, string paramsJson, double init) => UserModel.FromDocument(ModelDocument.Parse($$"""
        { "format": "gridsim-model/1", "id": "ONE", "role": "generic", "source": "one block (test fixture)",
          "ports": { "inputs": [ { "name": "u", "unit": "1" } ], "outputs": [ { "name": "y", "unit": "1", "from": "b" } ] },
          "blocks": [ { "id": "b", "type": "{{type}}", "inputs": ["u"]{{paramsJson}} } ],
          "initialisation": { "inputs": [], "steps": [ { "action": "initialise", "block": "b", "values": [{{init.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}] } ] } }
        """)).NewInstance();

    // ------------------------------------------------------------------------------------------------ 1. released refusal text

    /// <summary>
    /// An assembly that declares plug-in contract 3 is refused with "...; this build implements contracts 1 and 2." (v2.1.105-RC-5 said "contract 1"; a refusal must be true.)
    /// (PluginLoader at that tag). The text is pinned whole, as the tag's template gives it. The assembly is the contract 2
    /// sample with the [GridSimModelPlugin] argument rewritten in its metadata blob (the bytes 01 00 02 00 00 00 00 00: the
    /// prolog, the int32 2, no named arguments); the verifier reads 3 from the patched bytes before the loader is asked.
    /// </summary>
    [Fact]
    public void An_assembly_of_another_contract_is_refused_with_the_v2_1_105_text_byte_for_byte()
    {
        byte[] bytes = File.ReadAllBytes(BuiltDll("GridSim.ModelPlugin.Contract2Sample"));
        int at;
        using (var pe = new PEReader(ImmutableArray.Create(bytes)))
        {
            MetadataReader md = pe.GetMetadataReader();
            at = -1;
            foreach (CustomAttributeHandle h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute ca = md.GetCustomAttribute(h);
                if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                MemberReference ctor = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
                if (md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != nameof(GridSimModelPluginAttribute)) continue;
                // The blob's file offset: metadata start + blob heap start + the handle's heap offset + its one-byte length.
                at = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(ca.Value) + 1;
            }
        }
        Assert.True(at > 0, "the contract attribute was not found");
        Assert.Equal(new byte[] { 1, 0, 2, 0, 0, 0, 0, 0 }, bytes.AsSpan(at, 8).ToArray());
        bytes[at + 2] = 3;
        Assert.Equal(3, PluginVerifier.Verify(bytes).ContractVersion);
        File.WriteAllBytes(F("c3.dll"), bytes);
        string config = ConfigureFile("c3.dll", "SAMPLE_GENCLS");

        var ex = Assert.Throws<PluginRefusedException>(() => PluginLoader.Load(config));
        Assert.Equal("model plug-ins: assemblies[0] (c3.dll): the assembly declares plug-in contract 3; this build implements contracts 1 and 2.", ex.Message);
    }

    private sealed class RoleFiveFactory : IUserModelFactory
    {
        public string Id => "ROLE_FIVE";

        public UserModelRole Role => (UserModelRole)5;

        public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

        public object Create(IReadOnlyList<double> parameters) => new object();
    }

    /// <summary>
    /// A contract 1 model whose factory gives the role number 5 (no contract 1 role; contract 2 later gave it to machine) is held
    /// as no role and refused when bound, by the replay check's injection probe. v2.1.105-RC-5 called its role "load", which it
    /// is not; the refusal now names the number it gave (a diagnostic, so it follows the coordinator's accuracy ruling). It is
    /// still not taken as a contract 2 machine (that would say "has role machine ... IUserMachine").
    /// </summary>
    [Fact]
    public void A_contract_1_model_with_a_contract_2_role_number_is_refused_naming_the_number_it_gave()
    {
        var def = new PluginModelDefinition(new RoleFiveFactory(), "Fake", "fake.dll", new string('0', 64), PluginContract.Version);
        Assert.Equal("number 5 (not a role of contract 1)", def.RoleText);
        Assert.NotEqual(UserModelRole.Machine, def.Role);
        var ex = Assert.Throws<PluginRefusedException>(() => new PluginModel(def));
        Assert.Equal("plug-in model ROLE_FIVE has role number 5 (not a role of contract 1) but Create returned System.Object, which is not an IUserInjection.", ex.Message);

        // The same number in a contract 2 assembly is the machine role.
        var two = new PluginModelDefinition(new RoleFiveFactory(), "Fake", "fake.dll", new string('0', 64), PluginContract.Version2);
        Assert.Equal(UserModelRole.Machine, two.Role);
        Assert.Equal("machine", two.RoleText);
    }

    /// <summary>The declarative format's refusals of an unknown role and an unknown block type name every role and block this
    /// build reads, the relay role and the ontimer and latch blocks included (coordinator ruling: a refusal must be true, so
    /// diagnostic text follows the build rather than the v2.1.105-RC-5 wording).</summary>
    [Fact]
    public void Unknown_roles_and_block_types_are_refused_with_lists_that_name_every_role_and_block()
    {
        var role = Assert.Throws<UserModelException>(() => ModelDocument.Parse(Uv27.Replace("\"role\": \"relay\"", "\"role\": \"breaker\"", StringComparison.Ordinal)));
        Assert.Contains("'breaker' is not a role (use one of exciter, governor, stabiliser, generic, injection, load, relay).", role.Message, StringComparison.Ordinal);

        ModelDocument doc = ModelDocument.Parse(Uv27.Replace("\"type\": \"ontimer\"", "\"type\": \"timer\"", StringComparison.Ordinal));
        ModelIssue issue = Assert.Single(ModelValidator.Validate(doc), i => i.Code == "unknown-block");
        Assert.Equal("the block type 'timer' is not in the closed catalogue (gain, div, sum, product, lag, leadlag, washout, integrator, pi, "
            + "limit, ratelimit, deadband, table1d, expsat, min, max, switch, const, hold, delay, satpole, expr, ontimer, latch); it is refused rather than approximated.",
            issue.Message);
        // The new blocks are in the catalogue all the same.
        Assert.Contains(ModelCatalogue.Rows, r => r.Type == "ontimer");
        Assert.Contains(ModelCatalogue.Rows, r => r.Type == "latch");
    }

    // ------------------------------------------------------------------------------------------------ 2. ontimer and latch

    /// <summary>
    /// ontimer with time 0.045 s at h = 0.01 s: picked up at step 0, it operates at the first k with k h + h 1e-6 &gt;= 0.045,
    /// which is k = 5 (4 h + 1e-8 = 0.04000001 &lt; 0.045 &lt;= 0.05000001 = 5 h + 1e-8): a whole number of steps, never early.
    /// A zero input at step 7 resets it (0 at that step), and the count starts again from the pickup at step 8, so it operates
    /// again at step 13. Every step is asserted.
    /// </summary>
    [Fact]
    public void An_ontimer_counts_whole_steps_operates_never_early_and_resets_on_a_zero_step()
    {
        ModelInstance m = OneBlock("ontimer", ", \"params\": { \"time\": 0.045 }", 0.0);
        m.Initialize(ReadOnlySpan<double>.Empty);
        double[] u = { 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1 };
        double[] want = { 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1 };
        for (int k = 0; k < u.Length; k++) Assert.True(m.Step(u[k], 0.01) == want[k], $"step {k}: {m.Output(0):R}, expected {want[k]:R}");

        // time 0 operates on the pickup step itself; any non-zero input is true (here a negative one).
        ModelInstance z = OneBlock("ontimer", ", \"params\": { \"time\": 0 }", 0.0);
        z.Initialize(ReadOnlySpan<double>.Empty);
        Assert.Equal(0.0, z.Step(0.0, 0.01));
        Assert.Equal(1.0, z.Step(-2.0, 0.01));

        // The step must keep one size, and the timer starts not picked up.
        ModelInstance c = OneBlock("ontimer", ", \"params\": { \"time\": 0.045 }", 0.0);
        c.Initialize(ReadOnlySpan<double>.Empty);
        c.Step(1.0, 0.01);
        Assert.Contains("step-changed", Assert.Throws<UserModelException>(() => c.Step(1.0, 0.02)).Message, StringComparison.Ordinal);
        ModelInstance bad = OneBlock("ontimer", ", \"params\": { \"time\": 0.045 }", 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => bad.Initialize(ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void A_latch_seals_in_at_the_first_non_zero_step_and_stays_for_the_run()
    {
        ModelInstance m = OneBlock("latch", "", 0.0);
        m.Initialize(ReadOnlySpan<double>.Empty);
        double[] u = { 0, 0, 1, 0, 0, 0 };
        double[] want = { 0, 0, 1, 1, 1, 1 };
        for (int k = 0; k < u.Length; k++) Assert.True(m.Step(u[k], 0.01) == want[k], $"step {k}: {m.Output(0):R}");
        ModelInstance sealedIn = OneBlock("latch", "", 1.0);
        sealedIn.Initialize(ReadOnlySpan<double>.Empty);
        Assert.Equal(1.0, sealedIn.Step(0.0, 0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => OneBlock("latch", "", 0.5).Initialize(ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void A_relay_document_must_have_the_relay_ports_in_order()
    {
        Assert.Empty(ModelValidator.Validate(ModelDocument.Parse(Uv27)));
        ModelDocument swapped = ModelDocument.Parse(Uv27.Replace("{ \"name\": \"vm\", \"unit\": \"pu\" }, { \"name\": \"f\", \"unit\": \"1\" }",
            "{ \"name\": \"f\", \"unit\": \"1\" }, { \"name\": \"vm\", \"unit\": \"pu\" }", StringComparison.Ordinal));
        Assert.Contains(ModelValidator.Validate(swapped), i => i.Code == "role-ports" && i.Object == "ports.inputs");
        ModelDocument rate = ModelDocument.Parse(Uv27.Replace("{ \"name\": \"dfdt\", \"unit\": \"1/s\" }", "{ \"name\": \"dfdt\", \"unit\": \"1\" }", StringComparison.Ordinal));
        Assert.Contains(ModelValidator.Validate(rate), i => i.Code == "unit-mismatch" && i.Object == "port dfdt");
    }

    // ------------------------------------------------------------------------------------------------ 3. spec test 2, declarative

    /// <summary>
    /// Spec test 2 with the declarative relay: UV27 (switch below 0.8, ontimer 0.05 s) on case9 G3 against the built-in unit 27
    /// stage (uv1Pu 0.8, uv1DelayS 0.05) on the bus 6 fault from 1.0 s to 1.1 s. Both read |V| at the unit bus from the same
    /// solve and time it by the same whole-step rule, so the trip is at the same step and the run is the same bits.
    /// </summary>
    [Fact]
    public void A_declarative_undervoltage_relay_trips_at_the_same_step_as_the_built_in_27_and_the_run_is_the_same_bits()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy builtIn = RmsTransientStability.StudyEventsWithUnitRelays(
            m.WithUnitProtection(Set(("G3", new[] { ("uv1Pu", 0.8), ("uv1DelayS", 0.05) }))), pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0);
        UserModel uv = Declared(Uv27);
        RmsTransientStability.UnitRelayStudy declared = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForUnit("G3", uv) }, stepSeconds: Step, durationSeconds: 2.0);

        FullModelUnitRelayRecord a = Assert.Single(builtIn.UnitRelays);
        FullModelUnitRelayRecord b = Assert.Single(declared.UnitRelays);
        Assert.Equal("27-1", a.Device);
        Assert.Equal("USER:UV27", b.Device);
        Assert.True(a.Tripped);
        Assert.True(b.Tripped);
        Same(a.OperateSeconds!.Value, b.OperateSeconds!.Value, "operate time", 0);
        Same(a.DisconnectedSeconds!.Value, b.DisconnectedSeconds!.Value, "disconnect time", 0);
        Assert.Equal(27.0, b.OperateQuantity);
        // Inside the verified block catalogue: no OUTSIDE THE VERIFIED CONTRACT label, the document named by its hash.
        Assert.DoesNotContain(PluginContract.OutsideLabel, b.Settings, StringComparison.Ordinal);
        Assert.StartsWith("user model UV27 (relay) sha256:" + uv.Hash, b.Settings, StringComparison.Ordinal);
        Assert.Null(declared.EndReason);
        SameRun(builtIn.Result, declared.Result);
    }

    [Fact]
    public void A_declarative_relay_that_never_operates_changes_no_bit_of_the_run()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = FullModelKernelDigestTests.Events(m);
        MultiMachineSubtransient.Result off = RmsTransientStability.StudyEvents(m, pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0);
        RmsTransientStability.UnitRelayStudy on = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForUnit("G2", Declared(Uv27, P(("PICKUP", 0.01), ("DELAY", 1.0)))) }, stepSeconds: Step, durationSeconds: 2.0);
        Assert.False(Assert.Single(on.UnitRelays).Operated);
        SameRun(off, on.Result);
    }

    // ------------------------------------------------------------------------------------------------ 4. spec test 3, declarative

    /// <summary>
    /// Spec test 3 with the declarative relay: LATCH27 (latch, then ontimer 0.3 s) on the 0.1 s bus 6 fault. Without the latch
    /// (UV27 at 0.3 s) the timer resets when the voltage recovers and nothing operates; with it the relay picks up inside the
    /// fault, operates 0.3 s later, once, and the unit stays out to the end of the run. SAMPLE_LATCH, the contract 2 sample
    /// written the same way, gives the same trip step and the same bits.
    /// </summary>
    [Fact]
    public void A_declarative_latched_relay_trips_once_after_its_delay_the_unit_stays_out_and_it_matches_the_plugin_latch()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy plain = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForUnit("G3", Declared(Uv27, P(("DELAY", 0.3)))) }, stepSeconds: Step, durationSeconds: 2.0);
        Assert.False(Assert.Single(plain.UnitRelays).Operated);

        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForUnit("G3", Declared(Latch27)) }, stepSeconds: Step, durationSeconds: 2.0);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.True(r.Tripped);
        Assert.True(r.OperateSeconds >= 1.3 - 1e-9 && r.OperateSeconds < 1.4, $"operate {r.OperateSeconds:R}");
        Assert.Equal(r.OperateSeconds!.Value + Step, r.DisconnectedSeconds!.Value, 12);
        Assert.Equal(86.0, r.OperateQuantity);
        Assert.Null(s.EndReason);
        Assert.Equal((int)Math.Round(2.0 / Step) + 1, s.Result.Machines[0].TimeSeconds.Count);
        Assert.NotEqual(FullModelKernelDigestTests.Digest(plain.Result, Array.Empty<RmsEvent>()), FullModelKernelDigestTests.Digest(s.Result, Array.Empty<RmsEvent>()));

        PluginSet set = PluginLoader.Load(Configure("GridSim.ModelPlugin.Contract2Sample", "c2.dll", "SAMPLE_LATCH"));
        RmsTransientStability.UnitRelayStudy plugin = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { new UserRelayBinding("G3", new PluginModel(set.Find("SAMPLE_LATCH")!, P(("PICKUP", 0.8), ("DELAY", 0.3), ("CODE", 86.0)))) },
            stepSeconds: Step, durationSeconds: 2.0);
        FullModelUnitRelayRecord q = Assert.Single(plugin.UnitRelays);
        Same(q.OperateSeconds!.Value, r.OperateSeconds!.Value, "operate time", 0);
        SameRun(plugin.Result, s.Result);
    }

    // ------------------------------------------------------------------------------------------------ 5. branch relays

    /// <summary>
    /// OC51 (|I| &gt;= 1.5 pu for 0.03 s) on case9 branch 5-6 read at bus 5, on the bus 6 bolted fault from 1.0 s to 1.1 s:
    /// before the fault the branch carries well under 1.5 pu, during it the current from bus 5 into the fault is above it, so the
    /// relay operates inside the fault and opens the branch from the next step. The oracle is the same events with a scheduled
    /// trip of that branch at the relay's disconnection time, run with a relay that never operates (so both runs take the same
    /// kernel path): from that step on both solve the network built from the same stage state plus the branch out, so the runs
    /// are the same bits on every sample.
    /// </summary>
    [Fact]
    public void A_branch_relay_trip_is_the_same_bits_as_a_scheduled_trip_of_that_branch_at_the_same_step()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        int b56 = BranchBetween(m, 5, 6), b45 = BranchBetween(m, 4, 5);
        RmsEvent[] ev = { new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy tripped = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForBranch(b56, 5, Declared(Oc51), "branch 5-6") }, stepSeconds: Step, durationSeconds: 2.0);
        Assert.Empty(tripped.UnitRelays);
        FullModelBranchRelayRecord r = Assert.Single(tripped.BranchRelays);
        Assert.Equal("branch 5-6", r.Branch);
        Assert.Equal(b56, r.BranchIndex);
        Assert.Equal(5, r.MeasuredAtBus);
        Assert.Equal("USER:OC51", r.Device);
        Assert.True(r.Tripped, "the relay must open the branch inside the fault");
        Assert.True(r.OperateSeconds >= 1.0 && r.OperateSeconds < 1.1, $"operate {r.OperateSeconds:R}");
        Assert.Equal(r.OperateSeconds!.Value + Step, r.DisconnectedSeconds!.Value, 12);
        Assert.Equal(51.0, r.ReasonCode);
        Assert.Null(tripped.EndReason);

        var quiet = UserRelayBinding.ForBranch(b45, 4, Declared(Oc51, P(("PICKUP", 1e6))));
        RmsTransientStability.UnitRelayStudy scheduled = RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m),
            ev.Append(new RmsBranchTrip(r.DisconnectedSeconds!.Value, b56)).ToArray(), new[] { quiet }, stepSeconds: Step, durationSeconds: 2.0);
        Assert.False(Assert.Single(scheduled.BranchRelays).Operated);
        SameRun(scheduled.Result, tripped.Result);
    }

    [Fact]
    public void A_branch_relay_that_reads_the_frequency_is_refused_and_a_split_branch_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => UserRelayBinding.ForBranch(0, 1, Declared(Uf81)));
        Assert.Contains("reads the input 'f', which a branch relay does not have", ex.Message, StringComparison.Ordinal);
        // The same document binds to a unit.
        Assert.Equal("USER:UF81", UserRelayBinding.ForUnit("G1", Declared(Uf81)).Device);

        (GridModel m, PowerFlowResult pf) = Case9();
        int b56 = BranchBetween(m, 5, 6);
        RmsEvent[] ev = { new RmsBranchSequenceFault(1.0, b56, 0.5, FaultType.ThreePhase), new RmsBranchFaultClear(1.1, b56) };
        var split = Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsWithUserRelays(m, pf, GenrouData(m), ev,
            new[] { UserRelayBinding.ForBranch(b56, 5, Declared(Oc51)) }, stepSeconds: Step, durationSeconds: 2.0));
        Assert.Contains("a branch relay is bound to that branch", split.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ 6. relays on the CLI

    /// <summary>The case14 study of DynamicsStudyTestSupport.Request (fault-4, channels of machines 1 and 2 and bus 4, 0.005 s,
    /// 2 s) as a study file.</summary>
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
            channels = new object[]
            {
                new { kind = "rotorAngle", machine = new { bus = 1, id = "1" } }, new { kind = "speed", machine = new { bus = 1, id = "1" } },
                new { kind = "frequency", machine = new { bus = 1, id = "1" } }, new { kind = "rotorAngle", machine = new { bus = 2, id = "1" } },
                new { kind = "speed", machine = new { bus = 2, id = "1" } }, new { kind = "frequency", machine = new { bus = 2, id = "1" } },
                new { kind = "busVoltage", bus = 4 },
            },
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

    private static void SameNullable(double? a, JsonElement b, string what)
    {
        if (a is null) { Assert.Equal(JsonValueKind.Null, b.ValueKind); return; }
        Same(a.Value, b.GetDouble(), what, 0);
    }

    /// <summary>
    /// A relay plug-in (SAMPLE_UV27 on unit 2:1) and a declarative relay (OC51 on branch 2-4 circuit 1, read at bus 2) through
    /// the CLI's model assignments file: the full-model companion rows are the API's (DynamicsStudyRunner with the same
    /// RelayModels) to the bit, the study's own rows are the incumbent CLI's text for text, the provenance names both relays
    /// (the plug-in with OUTSIDE THE VERIFIED CONTRACT, the declarative one without), and a run without relays has no relay
    /// members at all.
    /// </summary>
    [Fact]
    public void Relays_through_the_cli_give_the_api_records_and_leave_the_study_rows_as_they_were()
    {
        string study = Case14Study();
        string config = Configure("GridSim.ModelPlugin.Contract2Sample", "c2.dll", "SAMPLE_UV27");
        File.WriteAllText(F("oc.model.json"), Oc51);
        string assign = F("assign-relays.json");
        File.WriteAllText(assign, JsonSerializer.Serialize(new
        {
            schema = "gridsim-model-assignments/1",
            deviceModels = new object[]
            {
                new { role = "relay", bind = new { machine = new { bus = 2, id = "1" } }, plugin = "SAMPLE_UV27",
                    parameters = new Dictionary<string, double> { ["PICKUP"] = 0.9, ["DELAY"] = 0.02, ["CODE"] = 27.0 } },
                new { role = "relay", bind = new { branch = new { from = 2, to = 4, circuit = "1" } }, file = "oc.model.json", sha256 = Sha(F("oc.model.json")),
                    parameters = new Dictionary<string, double> { ["PICKUP"] = 2.0, ["DELAY"] = 0.02 } },
            },
        }));

        PluginSet set = PluginLoader.Load(config);
        var uv = RelayModelAssignment.AtMachine(M(2), new PluginModel(set.Find("SAMPLE_UV27")!, P(("PICKUP", 0.9), ("DELAY", 0.02), ("CODE", 27.0))));
        var oc = RelayModelAssignment.AtBranch(new DynamicsBranchId(2, 4, "1"), Declared(Oc51, P(("PICKUP", 2.0), ("DELAY", 0.02))));
        DynamicsStudyResult api = DynamicsStudyRunner.Run(Request(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")), new[] { Fault4("fault-4") }, duration: 2.0)
            with { RelayModels = new[] { uv, oc } });
        DynamicsFullModelUnitRelayRow apiRow = api.FullModelUnitRelays!.Rows[0];
        Assert.Equal(DynamicsDisposition.Evaluated, apiRow.Disposition);
        FullModelUnitRelayRecord apiUnit = Assert.Single(apiRow.UnitRelays!);
        FullModelBranchRelayRecord apiBranch = Assert.Single(apiRow.BranchRelays!);
        Assert.Equal("2:1", apiUnit.Unit);
        Assert.Contains(PluginContract.OutsideLabel, apiUnit.Settings, StringComparison.Ordinal);
        Assert.DoesNotContain(PluginContract.OutsideLabel, apiBranch.Settings, StringComparison.Ordinal);

        CliProcess.Result cli = CliProcess.RunBuilt("dynamics-study", study, "--model-plugins", config, "--model-assignments", assign);
        Assert.True(cli.ExitCode is 0 or 3, cli.StdErr);
        CliProcess.Result plain = CliProcess.RunBuilt("dynamics-study", study);
        Assert.True(plain.ExitCode is 0 or 3, plain.StdErr);
        using JsonDocument doc = JsonDocument.Parse(cli.StdOut);
        using JsonDocument p = JsonDocument.Parse(plain.StdOut);
        JsonElement root = doc.RootElement;

        // The study's own rows and status: the incumbent's.
        Assert.Equal(p.RootElement.GetProperty("cases").GetRawText(), root.GetProperty("cases").GetRawText());
        Assert.Equal(p.RootElement.GetProperty("status").GetString(), root.GetProperty("status").GetString());

        // The companion rows: the API's records to the bit.
        JsonElement row = root.GetProperty("userRelays").GetProperty("fullModel").GetProperty("rows")[0];
        JsonElement unit = Assert.Single(row.GetProperty("unitRelays").EnumerateArray());
        Assert.Equal(apiUnit.Unit, unit.GetProperty("unit").GetString());
        Assert.Equal(apiUnit.Device, unit.GetProperty("device").GetString());
        Assert.Equal(apiUnit.Operated, unit.GetProperty("operated").GetBoolean());
        SameNullable(apiUnit.OperateSeconds, unit.GetProperty("operateSeconds"), "unit relay operate time");
        SameNullable(apiUnit.DisconnectedSeconds, unit.GetProperty("disconnectedSeconds"), "unit relay disconnect time");
        JsonElement branch = Assert.Single(row.GetProperty("branchRelays").EnumerateArray());
        Assert.Equal(apiBranch.Branch, branch.GetProperty("branch").GetString());
        Assert.Equal(apiBranch.Device, branch.GetProperty("device").GetString());
        Assert.Equal(2, branch.GetProperty("measuredAtBus").GetInt32());
        Assert.Equal(apiBranch.Operated, branch.GetProperty("operated").GetBoolean());
        SameNullable(apiBranch.OperateSeconds, branch.GetProperty("operateSeconds"), "branch relay operate time");
        SameNullable(apiBranch.DisconnectedSeconds, branch.GetProperty("disconnectedSeconds"), "branch relay disconnect time");

        // The provenance: both relays, the label on the plug-in only.
        JsonElement relays = root.GetProperty("provenance").GetProperty("relayModels");
        Assert.Equal(2, relays.GetArrayLength());
        Assert.Equal("plugin", relays[0].GetProperty("source").GetString());
        Assert.Equal(PluginContract.OutsideLabel, relays[0].GetProperty("label").GetString());
        Assert.Equal(2, relays[0].GetProperty("contractVersion").GetInt32());
        Assert.Equal("declarative", relays[1].GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, relays[1].GetProperty("label").ValueKind);
        Assert.Equal(oc.Hash, relays[1].GetProperty("sha256").GetString());

        // Without relays: no relay member anywhere.
        Assert.False(p.RootElement.TryGetProperty("userRelays", out _));
        Assert.False(p.RootElement.GetProperty("provenance").TryGetProperty("relayModels", out _));
    }

    [Fact]
    public void The_journal_lists_a_declarative_relay_file_and_the_parser_reads_its_binding()
    {
        File.WriteAllText(F("uv.model.json"), Uv27);
        string assign = F("assign-uv.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "gridsim-model-assignments/1",
            deviceModels = new[] { new { role = "relay", bind = new { machine = new { bus = 2, id = "1" } }, file = "uv.model.json", sha256 = Sha(F("uv.model.json")) } },
        });
        File.WriteAllBytes(assign, bytes);
        DynamicsInputs.NamedFile f = Assert.Single(DynamicsInputs.Expand(assign, bytes));
        Assert.Equal("assignments:deviceModels[0].file", f.Label);
        Assert.Equal(Path.GetFullPath(F("uv.model.json")), f.Full);
        Assert.True(f.Snapshot);

        (IReadOnlyList<DeviceModelAssignment> devices, IReadOnlyList<RelayModelAssignment> relays) = ModelAssignmentsFile.ParseModels(bytes, assign, null, Step);
        Assert.Empty(devices);
        RelayModelAssignment r = Assert.Single(relays);
        Assert.Equal(M(2), r.Machine);
        Assert.Null(r.Branch);
        Assert.Equal("USER:UV27", r.Device);
        Assert.False(r.IsPlugin);
        // A changed file is refused by its pin.
        File.WriteAllText(F("uv.model.json"), Uv27 + " ");
        Assert.Contains("not the pinned", Assert.Throws<InvalidDataException>(() => ModelAssignmentsFile.ParseModels(bytes, assign, null, Step)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relays_are_refused_with_a_step_schedule_whose_steps_differ()
    {
        var uv = RelayModelAssignment.AtMachine(M(2), Declared(Uv27));
        DynamicsStudyRequest baseRequest = Request(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")), new[] { Fault4("fault-4") }, duration: 2.0)
            with { RelayModels = new[] { uv } };
        var schedule = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(baseRequest with
        {
            StepSchedule = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.005), new RmsStepWindow(1.0, 0.01) }),
        }));
        Assert.Contains("relay models", schedule.Message, StringComparison.Ordinal);
    }
}
