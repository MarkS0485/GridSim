using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N268 (a): the bridge model-block reader refuses a present baseMva that is not finite and positive, by name.
    // =================================================================================================================

    private static BridgeProtocol.ModelBlock N268Block(double baseMva) => new()
    {
        Name = "t",
        BaseMva = baseMva,
        Buses = new()
        {
            new BridgeProtocol.BusDto { Id = 1, Type = "Slack", BaseKv = 132 },
            new BridgeProtocol.BusDto { Id = 2, Type = "Pq", BaseKv = 132, Pd = 10 },
        },
        Generators = new() { new BridgeProtocol.GenDto { Bus = 1, Pg = 10 } },
        Branches = new() { new BridgeProtocol.BranchDto { From = 1, To = 2, R = 0.01, X = 0.1 } },
    };

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void N268_a_bridge_baseMva_that_is_not_finite_and_positive_is_refused_by_name(double baseMva)
    {
        // It used to be replaced by 100 MVA without a word.
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(N268Block(baseMva)));
        Assert.Contains("baseMva", ex.Message, StringComparison.Ordinal);
        Assert.Contains("finite and positive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N268_a_negative_zero_bridge_baseMva_is_refused_too()
    {
        // -0.0 is its own case (xUnit folds it into 0.0 as InlineData): it is not above 0.
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(N268Block(-0.0)));
        Assert.Contains("baseMva is -0", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(1.0)]
    [InlineData(1e-6)]
    [InlineData(1000.0)]
    public void N268_a_positive_bridge_baseMva_is_read_exactly(double baseMva)
    {
        GridModel m = BridgeProtocol.BuildModel(N268Block(baseMva));
        Assert.Equal(BitConverter.DoubleToInt64Bits(baseMva), BitConverter.DoubleToInt64Bits(m.BaseMva));
    }

    [Fact]
    public void N268_an_absent_bridge_baseMva_still_reads_as_100()
    {
        // The topology message and solve export without a baseMva field (an older writer): the ModelBlock default.
        const string json = """
            { "name": "t", "buses": [ { "id": 1, "type": "Slack", "baseKv": 132 } ], "generators": [], "branches": [] }
            """;
        var block = System.Text.Json.JsonSerializer.Deserialize<BridgeProtocol.ModelBlock>(json, BridgeProtocol.JsonOptions)!;
        GridModel m = BridgeProtocol.BuildModel(block);
        Assert.Equal(100.0, m.BaseMva);
        Assert.Equal(GridModel.DefaultBaseMva, m.BaseMva);
    }

    [Fact]
    public void N268_a_NaN_baseMva_on_the_wire_is_refused()
    {
        // The bridge options admit named floating-point literals, so "NaN" can arrive in a message.
        const string json = """
            { "name": "t", "baseMva": "NaN", "buses": [ { "id": 1, "type": "Slack", "baseKv": 132 } ], "generators": [], "branches": [] }
            """;
        var block = System.Text.Json.JsonSerializer.Deserialize<BridgeProtocol.ModelBlock>(json, BridgeProtocol.JsonOptions)!;
        Assert.True(double.IsNaN(block.BaseMva));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(block));
        Assert.Contains("baseMva is NaN", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // N268 (b): a v2 synchronousMachine template is valid content to the schema AND to the binder; binding an element to
    // one is refused at the binding, by name (the v2 twin of D-DOC-149).
    // =================================================================================================================

    private const string N268Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "North", "baseKv": 400, "type": "slack" },
            { "id": 2, "name": "South", "baseKv": 400 }
          ],
          "generators": [
            { "name": "Slack", "bus": 1, "type": "ccgt", "capacityMva": 1000, "outputMw": 0, "role": "slack" },
            { "name": "Wind", "bus": 2, "type": "wind-onshore", "capacityMva": 200, "outputMw": 100, "role": "pq" }
          ],
          "demands": [ { "name": "Load", "bus": 2, "pMw": 300, "qMvar": 50 } ],
          "lines": [ { "name": "L1-2", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.02, "ratingMva": 1000 } ]
        }
        """;

    private const string N268MachineTemplate = """
        "localTemplates": [ { "id": "gridsim.local.sm", "rev": 1, "kind": "synchronousMachine", "status": "active", "title": "A machine",
          "params": { "xdPu": 1.8 }, "provenance": { "_default": { "tag": "Synthetic", "note": "test fixture" } } } ],
        """;

    private static string N268Manifest(string extra) => """
        {
          "schema": "gridsim-case/2",
          "name": "Machine template test",
          "system": { "baseMva": 100, "frequencyHz": 50 },
        """ + extra + """
          "network": ["network.json"]
        }
        """;

    /// <summary>Write the case into a fresh temp folder, run <paramref name="act"/> on the manifest path, clean up.</summary>
    private static T N268WithCase<T>(string manifest, Func<string, T> act)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-lf1-n268-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "network.json"), N268Network);
            string path = Path.Combine(dir, "case.json");
            File.WriteAllText(path, manifest);
            return act(path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string N268SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    [Fact]
    public void N268_an_unbound_synchronousMachine_template_loads_and_changes_nothing()
    {
        // Before N268 every active synchronousMachine template became an asset class the library refused, so this case
        // was refused although the schema admits it. Now it loads, and the model is the one without the template.
        GridModel without = N268WithCase(N268Manifest(""), p => V2CaseLoader.Load(p, N268SchemasDir));
        GridModel with = N268WithCase(N268Manifest(N268MachineTemplate), p => V2CaseLoader.Load(p, N268SchemasDir));
        PowerFlowResult a = SolverSelector.CreateNewton(without).Solve(without);
        PowerFlowResult b = SolverSelector.CreateNewton(with).Solve(with);
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void N268_binding_an_element_to_a_synchronousMachine_template_is_refused_at_the_binding_by_name()
    {
        string manifest = N268Manifest(N268MachineTemplate + """
            "bindings": [ { "matchType": "ccgt", "template": "gridsim.local.sm@1" } ],
            """);
        string msg = N268WithCase(manifest, p => Assert.Throws<InvalidDataException>(() => V2CaseLoader.Load(p, N268SchemasDir)).Message);
        Assert.Contains("gridsim.local.sm@1", msg, StringComparison.Ordinal);
        Assert.Contains("is kind 'synchronousMachine'", msg, StringComparison.Ordinal);
        // The kinds an element can be bound to are listed, and synchronousMachine is not among them.
        Assert.Contains("gridFollowingInverter, gridFormingInverter, oltc, lineType, protection", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void N268_the_v2_machine_kind_spec_is_still_shipped_so_the_schema_admits_the_template()
    {
        // The other half of "both admit it": the kind spec the schema validates a synchronousMachine template against.
        Assert.True(File.Exists(Path.Combine(N268SchemasDir, "kinds", "synchronousMachine.kind.json")));
    }
}
