using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R031: a solve export re-imports as the SAME network. The branch service state, ZIP load model, generator
/// pmin, zero-sequence/winding data, OLTCs and boundaries used to be dropped, so a post-outage state came back
/// with its opened branch in service (inconsistent with its own zero-flow result) and a ZIP case came back as
/// constant power. R266: the bridge backfills parallel circuits in order, not all from the first twin.
/// </summary>
public class SolveExportRoundTripTests
{
    // case9 with: branch 7-8 opened, a ZIP load at bus 5, a pmin on gen 2, zero-sequence + winding data on the
    // 1-4 step-up transformer, an OLTC and a boundary. Every field the export now carries is non-default.
    private static GridModel Decorated()
    {
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.ToList();
        buses[4] = buses[4] with { Zip = new ZipLoad(Ap: 0.3, Bp: 0.2, Cp: 0.5, Aq: 0.4, Bq: 0.1, Cq: 0.5) };
        var gens = m.Generators.ToList();
        gens[1] = gens[1] with { Pmin = 40.0 };
        var branches = m.Branches.ToList();
        branches[0] = branches[0] with
        {
            Name = "T1-4", IsTransformer = true, R0 = 0.001, X0 = 0.05,
            FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.WyeGrounded,
            ToNeutralXPu = 0.02,
        };
        branches[5] = branches[5] with { Name = "L7-8", InService = false };
        return new GridModel
        {
            Name = m.Name,
            BaseMva = m.BaseMva,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            Oltcs = new[] { new Oltc("T1-4", 4, TargetVpu: 1.01, MinTap: 0.92, MaxTap: 1.08, StepPu: 0.01, DeadbandPu: 0.005) },
            Boundaries = new[] { new Boundary("cut", 300, new[] { new BoundaryBranch("T1-4", -1) }) },
        };
    }

    private static (GridModel Model, PowerFlowResult Result, string Json) ExportDecorated()
    {
        GridModel m = Decorated();
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
        return (m, r, SolveExport.ToJson(m, r, trace, meta));
    }

    [Fact]
    public void An_opened_branch_stays_out_of_service_through_export_and_import()
    {
        (_, _, string json) = ExportDecorated();
        GridModel back = SolveImport.Read(json).Model;
        Assert.False(back.Branches[5].InService);                       // was re-imported as in service
        Assert.All(back.Branches.Where((_, i) => i != 5), b => Assert.True(b.InService));
    }

    [Fact]
    public void Every_carried_network_field_round_trips_exactly()
    {
        (GridModel m, _, string json) = ExportDecorated();
        GridModel back = SolveImport.Read(json).Model;

        Assert.Equal(m.Buses[4].Zip, back.Buses[4].Zip);                // ZIP survives (record equality)
        Assert.Null(back.Buses[0].Zip);                                  // constant power stays constant power
        Assert.Equal(40.0, back.Generators[1].Pmin);
        Branch t = back.Branches[0];
        Assert.Equal(0.001, t.R0);
        Assert.Equal(0.05, t.X0);
        Assert.Equal(WindingConnection.Delta, t.FromWinding);
        Assert.Equal(WindingConnection.WyeGrounded, t.ToWinding);
        Assert.Equal(0.02, t.ToNeutralXPu);
        Assert.Null(back.Branches[1].FromWinding);                       // a line stays a line
        Assert.Equal(m.Oltcs, back.Oltcs);
        Boundary bd = Assert.Single(back.Boundaries);
        Assert.Equal("cut", bd.Name);
        Assert.Equal(-1, Assert.Single(bd.Branches).Direction);
    }

    [Fact]
    public void The_reimported_model_resolves_to_the_bit_identical_state()
    {
        // The physical proof: re-solving the re-imported network reproduces the exported result exactly. Before
        // R031 the opened branch came back in service and the ZIP load as constant power, so this differed.
        (_, PowerFlowResult r, string json) = ExportDecorated();
        GridModel back = SolveImport.Read(json).Model;
        PowerFlowResult again = new NewtonRaphsonPowerFlow().Solve(back);
        Assert.True(again.Converged);
        Assert.Equal(r.Vm, again.Vm);
        Assert.Equal(r.VaDeg, again.VaDeg);
    }

    [Fact]
    public void The_export_writes_the_service_state_explicitly()
    {
        (_, _, string json) = ExportDecorated();
        JsonElement br = JsonDocument.Parse(json).RootElement.GetProperty("model").GetProperty("branches");
        Assert.False(br[5].GetProperty("inService").GetBoolean());
        Assert.True(br[0].GetProperty("inService").GetBoolean());
    }

    [Fact]
    public void An_older_document_without_the_new_fields_reads_with_model_defaults()
    {
        const string json = """
        { "type": "topology", "model": { "name": "old", "baseMva": 100,
          "buses": [ { "id": 1, "type": "slack", "baseKv": 400 }, { "id": 2, "type": "pq", "baseKv": 400, "pd": 5 } ],
          "generators": [ { "bus": 1, "pg": 5 } ],
          "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1 } ] } }
        """;
        GridModel m = BridgeProtocol.ReadTopology(json).ToModel();
        Assert.True(m.Branches[0].InService);
        Assert.Null(m.Branches[0].FromWinding);
        Assert.Equal(0.0, m.Branches[0].R0);
        Assert.Null(m.Buses[1].Zip);
        Assert.Equal(0.0, m.Generators[0].Pmin);
        Assert.Empty(m.Oltcs);
    }

    [Fact]
    public void The_bridge_topology_reads_an_opened_branch()
    {
        // Test changed 2026-10-19: since 1.95.372 (GR-147) the bridge validates the model it builds, and opening the
        // only branch left bus 2 an island with no slack, which is correctly refused. A second, in-service parallel
        // branch keeps bus 2 connected, so the test still checks only that inService false is read.
        const string json = """
        { "type": "topology", "model": { "name": "t", "baseMva": 100,
          "buses": [ { "id": 1, "type": "slack" }, { "id": 2, "type": "pq" } ],
          "generators": [ { "bus": 1, "pg": 0 } ],
          "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1, "inService": false },
                        { "from": 1, "to": 2, "r": 0.01, "x": 0.1 } ] } }
        """;
        Assert.False(BridgeProtocol.ReadTopology(json).ToModel().Branches[0].InService);
    }

    [Fact]
    public void An_unknown_winding_name_fails_loud()
    {
        const string json = """
        { "type": "topology", "model": { "name": "t", "baseMva": 100,
          "buses": [ { "id": 1, "type": "slack" }, { "id": 2, "type": "pq" } ],
          "generators": [ { "bus": 1, "pg": 0 } ],
          "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1, "fromWinding": "Zigzag" } ] } }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.ReadTopology(json).ToModel());
        Assert.Contains("Zigzag", ex.Message);
    }

    // ---- R266: parallel circuits backfilled in order -------------------------------------------------

    private const string TwinTopology = """
    { "type": "topology", "model": { "name": "twins", "baseMva": 100,
      "buses": [ { "id": 10, "type": "slack" }, { "id": 11, "type": "pq", "pd": 50 } ],
      "generators": [ { "bus": 10, "pg": 50 } ],
      "branches": [
        { "from": 10, "to": 11, "r": 0.01, "x": 0.1, "rateAMva": 500, "name": "A1" },
        { "from": 11, "to": 10, "r": 0.01, "x": 0.1, "rateAMva": 400, "name": "A2" },
        { "from": 10, "to": 11, "r": 0.01, "x": 0.1, "rateAMva": 300, "name": "A3" } ] } }
    """;

    private static string TwinState(int flows) =>
        "{ \"type\": \"state\", \"t\": \"2020-01-01T00:00:00Z\", \"result\": { \"buses\": [], \"lineFlows\": [" +
        string.Join(",", Enumerable.Range(0, flows).Select(k =>
            k == 1 ? "{ \"from\": 11, \"to\": 10, \"pFromMw\": -1 }" : $"{{ \"from\": 10, \"to\": 11, \"pFromMw\": {k} }}")) +
        "] } }";

    [Fact]
    public void Parallel_circuits_each_get_their_own_name_and_rating()
    {
        GridModel topo = BridgeProtocol.ReadTopology(TwinTopology).ToModel();
        PowerFlowResult r = BridgeProtocol.ReadState(TwinState(3)).ToPowerFlowResult(topo);

        // Before R266 all three rows were labelled "A1" with rating 500.
        Assert.Equal(new[] { "A1", "A2", "A3" }, r.LineFlows.Select(f => f.Name).ToArray());
        Assert.Equal(new[] { 500.0, 400.0, 300.0 }, r.LineFlows.Select(f => f.RatingMva).ToArray());
    }

    [Fact]
    public void More_unnamed_flows_than_circuits_on_a_pair_fails_loud()
    {
        GridModel topo = BridgeProtocol.ReadTopology(TwinTopology).ToModel();
        var ex = Assert.Throws<InvalidDataException>(() =>
            BridgeProtocol.ReadState(TwinState(4)).ToPowerFlowResult(topo));
        Assert.Contains("does not match the topology", ex.Message);
    }
}
