using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The GridSim side of the GDA<->GridSim bridge. A <c>topology</c> message caches the network; each slim <c>state</c>
/// message reconstructs a <see cref="PowerFlowResult"/> and a <see cref="SimFrame"/> against that cached topology,
/// mapping bus voltages/injections and line flows back by id. And the load-bearing invariant: a <c>state</c> whose
/// timestamp is inside the forward-inference horizon is refused BEFORE any frame is built.
/// </summary>
public class BridgeProtocolTests
{
    // A three-bus toy network: slack + one PV generator + one PQ load, two branches (one a named, rated transformer).
    private const string TopologyJson = """
    {
      "type": "topology",
      "model": {
        "name": "toy3",
        "baseMva": 100,
        "buses": [
          { "id": 1, "type": "Slack", "baseKv": 400, "name": "Slack400" },
          { "id": 2, "type": "Pv",    "baseKv": 400, "name": "Gen400" },
          { "id": 3, "type": "Pq",    "baseKv": 132, "pd": 80, "qd": 20, "name": "Load132" }
        ],
        "generators": [
          { "bus": 1, "pg": 0,   "name": "SlackGen" },
          { "bus": 2, "pg": 120, "vg": 1.02, "mBase": 200, "h": 4.0, "name": "UnitA", "type": "CCGT" }
        ],
        "branches": [
          { "from": 1, "to": 2, "r": 0.004, "x": 0.04, "b": 0.02, "rateAMva": 500, "name": "L1-2" },
          { "from": 2, "to": 3, "r": 0.006, "x": 0.08, "tap": 1.0, "rateAMva": 250, "name": "T2-3", "isTransformer": true }
        ]
      }
    }
    """;

    // A slim solved state: per-bus voltages/injections + line flows. Timestamps are filled per-test.
    private static string StateJson(string isoTimestamp) => $$"""
    {
      "type": "state",
      "t": "{{isoTimestamp}}",
      "result": {
        "totalLossMw": 1.5,
        "buses": [
          { "id": 1, "vm": 1.010, "vaDeg": 0.0,   "pInjMw": 40.0,  "qInjMvar": 12.0 },
          { "id": 2, "vm": 1.020, "vaDeg": -1.5,  "pInjMw": 120.0, "qInjMvar": 8.0 },
          { "id": 3, "vm": 0.978, "vaDeg": -6.2,  "pInjMw": -80.0, "qInjMvar": -20.0 }
        ],
        "lineFlows": [
          { "from": 1, "to": 2, "pFromMw": 40.0, "pToMw": -39.8, "loadingPercent": 8.0 },
          { "from": 2, "to": 3, "pFromMw": 80.2, "pToMw": -80.0, "loadingPercent": 32.0, "name": "T2-3", "ratingMva": 250 }
        ]
      },
      "scalars": {
        "freqHz": 49.98,
        "rocofHzPerS": -0.03,
        "genMw": 160.0,
        "demandMw": 80.0,
        "inertiaMwS": 800.0,
        "conditionIndex": 72.0,
        "rocofHeadroomHzS": 0.09
      },
      "overlays": {
        "stressByBranch": { "T2-3": 0.64 },
        "conditionByBus": { "3": 0.42 }
      }
    }
    """;

    private static string RecentIso() => DateTime.UtcNow.ToString("o");
    private static string HistoricalIso() =>
        DateTime.UtcNow.AddHours(-(ForwardInferenceGuard.HorizonHours + 24)).ToString("o");

    [Fact]
    public void Topology_message_rebuilds_the_network()
    {
        // (GR-147: the toy network is structurally valid, so the new GridModel.Validate gate passes it unchanged.)
        GridModel m = BridgeProtocol.ReadTopology(TopologyJson).ToModel();

        Assert.Equal("toy3", m.Name);
        Assert.Equal(3, m.BusCount);
        Assert.Equal(2, m.Generators.Count);
        Assert.Equal(2, m.Branches.Count);
        Assert.Equal(1, m.Buses.Count(b => b.Type == BusType.Slack));
        Assert.Equal(1, m.Buses.Count(b => b.Type == BusType.Pv));
        Assert.Equal("Load132", m.Buses.Single(b => b.Id == 3).Name);
        Assert.True(m.Branches.Single(b => b.Name == "T2-3").IsTransformer);
    }

    [Fact]
    public void State_maps_bus_voltages_and_injections_by_id()
    {
        GridModel topo = BridgeProtocol.ReadTopology(TopologyJson).ToModel();
        BridgeProtocol.StateMessage state = BridgeProtocol.ReadState(StateJson(HistoricalIso()));

        PowerFlowResult r = state.ToPowerFlowResult(topo);

        Assert.True(r.Converged);
        Assert.Equal(3, r.BusIds.Count);
        Assert.Equal(1.5, r.TotalLossMw, 6);

        // Map each result index back to its bus id (the _idToIndex-style match the renderer uses).
        int IndexOf(int busId) => Array.FindIndex(r.BusIds.ToArray(), id => id == busId);

        Assert.Equal(1.010, r.Vm[IndexOf(1)], 6);
        Assert.Equal(1.020, r.Vm[IndexOf(2)], 6);
        Assert.Equal(0.978, r.Vm[IndexOf(3)], 6);
        Assert.Equal(-6.2, r.VaDeg[IndexOf(3)], 6);
        Assert.Equal(-80.0, r.PInjMw[IndexOf(3)], 6);
        Assert.Equal(120.0, r.PInjMw[IndexOf(2)], 6);
    }

    [Fact]
    public void State_maps_line_flows_and_backfills_name_and_rating_from_topology()
    {
        GridModel topo = BridgeProtocol.ReadTopology(TopologyJson).ToModel();
        PowerFlowResult r = BridgeProtocol.ReadState(StateJson(HistoricalIso())).ToPowerFlowResult(topo);

        Assert.Equal(2, r.LineFlows.Count);

        // The 2->3 flow carried its own name+rating on the wire.
        LineFlow t23 = r.LineFlows.Single(f => f.From == 2 && f.To == 3);
        Assert.Equal("T2-3", t23.Name);
        Assert.Equal(250, t23.RatingMva, 6);
        Assert.Equal(32.0, t23.LoadingPercent, 6);

        // The 1->2 flow omitted them: they are backfilled from the cached topology's matching branch.
        LineFlow l12 = r.LineFlows.Single(f => f.From == 1 && f.To == 2);
        Assert.Equal("L1-2", l12.Name);
        Assert.Equal(500, l12.RatingMva, 6);
    }

    [Fact]
    public void State_assembles_a_SimFrame_with_topology_and_scalars()
    {
        GridModel topo = BridgeProtocol.ReadTopology(TopologyJson).ToModel();
        SimFrame frame = BridgeProtocol.ReadState(StateJson(HistoricalIso())).ToSimFrame(topo, tick: 7);

        Assert.Same(topo, frame.Model);           // the cached topology drives the renderer
        Assert.Equal(7, frame.Tick);
        Assert.Equal(49.98, frame.FrequencyHz, 6);
        Assert.Equal(-0.03, frame.RocofHzPerS, 6);
        Assert.Equal(160.0, frame.GenerationMw, 6);
        Assert.Equal(80.0, frame.DemandMw, 6);
        Assert.Equal(0.978, frame.Result.Vm[Array.FindIndex(frame.Result.BusIds.ToArray(), id => id == 3)], 6);
    }

    [Fact]
    public void Overlays_parse_stress_by_branch_and_condition_by_bus()
    {
        BridgeProtocol.StateMessage state = BridgeProtocol.ReadState(StateJson(HistoricalIso()));
        Assert.NotNull(state.Overlays);
        Assert.Equal(0.64, state.Overlays!.StressByBranch!["T2-3"], 6);
        Assert.Equal(0.42, state.Overlays!.ConditionByBus![3], 6);   // keyed by integer bus id
    }

    [Fact]
    public void Client_caches_topology_then_emits_a_frame_for_a_historical_state()
    {
        var client = new GridBridgeClient();
        SimFrame? received = null;
        client.FrameReceived += f => received = f;

        client.HandleTopology(TopologyJson);
        Assert.NotNull(client.Topology);
        Assert.Equal(3, client.Topology!.BusCount);

        SimFrame frame = client.HandleState(StateJson(HistoricalIso()));
        Assert.NotNull(received);
        Assert.Same(frame, received);
        Assert.Equal(1, frame.Tick);              // ticks increment per accepted state
        Assert.Equal(49.98, frame.FrequencyHz, 6);
    }

    [Fact]
    public void StateReceived_exposes_the_raw_scalars_and_overlays_alongside_the_frame()
    {
        var client = new GridBridgeClient();
        SimFrame? frame = null;
        BridgeProtocol.ScalarsBlock? scalars = null;
        BridgeProtocol.OverlaysBlock? overlays = null;
        client.StateReceived += (f, s, o) => { frame = f; scalars = s; overlays = o; };

        client.HandleTopology(TopologyJson);
        client.HandleState(StateJson(HistoricalIso()));

        // The HUD scalars the fixed SimFrame record can't carry come through on the event.
        Assert.NotNull(frame);
        Assert.NotNull(scalars);
        Assert.Equal(800.0, scalars!.InertiaMwS!.Value, 6);
        Assert.Equal(0.72, scalars.ConditionIndex!.Value, 6);
        Assert.Equal(0.09, scalars.RocofHeadroomHzS!.Value, 6);

        // The per-asset colour overlays come through too, keyed as the renderer consumes them.
        Assert.NotNull(overlays);
        Assert.Equal(0.64, overlays!.StressByBranch!["T2-3"], 6);
        Assert.Equal(0.42, overlays.ConditionByBus![3], 6);
    }

    [Fact]
    public void A_state_carries_the_operator_and_market_blocks_streamed_from_the_lake()
    {
        // The exact camelCase shape the GDA bridge emits (bridge_server.py _operator_at / _market_at).
        string json = $$"""
        {
          "type": "state", "t": "{{HistoricalIso()}}",
          "result": { "buses": [ { "id": 1, "vm": 1.01, "vaDeg": 0.0, "pInjMw": 40.0, "qInjMvar": 12.0 } ], "lineFlows": [] },
          "operator": {
            "largestLossMw": 1800.0, "reserveMw": 30547.6, "deratedMarginMw": 15476.3, "netInterconnectorMw": 3853.0,
            "synchronousInertiaGvaS": 131.4, "outturnInertiaGvaS": 129.0, "marketInertiaGvaS": 86.0,
            "warning": "NESO excess-price notification",
            "balancingActions": [ { "bmUnit": "T_DRAXX-4", "kind": "OFFER", "levelMw": 645.0, "fuelType": "BIOMASS", "synchronous": true }, { "bmUnit": "T_SGRWO-6", "kind": "BID", "levelMw": 125.0, "fuelType": "WIND", "synchronous": false } ],
            "constraints": [ { "name": "SCOTEX", "costGbp": 183842.0, "binding": true } ],
            "warnings": [ "NESO excess-price notification" ]
          },
          "market": {
            "imbalancePriceGbpMwh": 48.16, "marketIndexGbpMwh": 48.16, "bsuosGbpMwh": 13.74,
            "constraintCostGbp": 371155.0, "carbonIntensityGco2PerKwh": 169.0, "demandForecastMw": 22863.0,
            "interconnectors": [ { "name": "IFA", "flowMw": 1503.0 }, { "name": "Moyle", "flowMw": -451.0 } ]
          }
        }
        """;
        BridgeProtocol.StateMessage state = BridgeProtocol.ReadState(json);

        Assert.NotNull(state.Operator);
        Assert.Equal(1800.0, state.Operator!.LargestLossMw!.Value, 6);
        Assert.Equal(30547.6, state.Operator.ReserveMw!.Value, 6);
        Assert.Equal(3853.0, state.Operator.NetInterconnectorMw!.Value, 6);
        Assert.Equal(15476.3, state.Operator.DeratedMarginMw!.Value, 6);
        Assert.Equal(131.4, state.Operator.SynchronousInertiaGvaS!.Value, 6);
        Assert.Equal(129.0, state.Operator.OutturnInertiaGvaS!.Value, 6);
        Assert.Equal(86.0, state.Operator.MarketInertiaGvaS!.Value, 6);
        Assert.Equal("NESO excess-price notification", state.Operator.Warning);
        Assert.Equal(2, state.Operator.BalancingActions!.Count);
        Assert.Equal("T_DRAXX-4", state.Operator.BalancingActions[0].BmUnit);
        Assert.Equal("OFFER", state.Operator.BalancingActions[0].Kind);
        Assert.Equal(645.0, state.Operator.BalancingActions[0].LevelMw!.Value, 6);
        Assert.Equal("BIOMASS", state.Operator.BalancingActions[0].FuelType);
        Assert.True(state.Operator.BalancingActions[0].Synchronous);
        Assert.False(state.Operator.BalancingActions[1].Synchronous);
        Assert.Equal("SCOTEX", state.Operator.Constraints![0].Name);
        Assert.Equal(183842.0, state.Operator.Constraints[0].CostGbp!.Value, 6);
        Assert.True(state.Operator.Constraints[0].Binding);
        Assert.Single(state.Operator.Warnings!);

        Assert.NotNull(state.Market);
        Assert.Equal(48.16, state.Market!.MarketIndexGbpMwh!.Value, 6);
        Assert.Equal(13.74, state.Market.BsuosGbpMwh!.Value, 6);
        Assert.Equal(371155.0, state.Market.ConstraintCostGbp!.Value, 3);
        Assert.Equal(169.0, state.Market.CarbonIntensityGco2PerKwh!.Value, 6);
        Assert.Equal(22863.0, state.Market.DemandForecastMw!.Value, 6);
        Assert.Equal(2, state.Market.Interconnectors!.Count);
        Assert.Equal("IFA", state.Market.Interconnectors[0].Name);
        Assert.Equal(1503.0, state.Market.Interconnectors[0].FlowMw!.Value, 6);
        Assert.Equal(-451.0, state.Market.Interconnectors[1].FlowMw!.Value, 6);
    }

    [Fact]
    public void A_state_with_a_future_timestamp_is_rejected_before_a_frame_is_built()
    {
        var client = new GridBridgeClient();
        bool raised = false;
        client.FrameReceived += _ => raised = true;

        client.HandleTopology(TopologyJson);

        // "now" is inside the 169 h historical horizon -> the guard must refuse it.
        Assert.Throws<ForwardInferenceException>(() => client.HandleState(StateJson(RecentIso())));
        Assert.False(raised);   // no frame ever reached a subscriber
    }

    [Fact]
    public void A_state_before_any_topology_throws()
    {
        var client = new GridBridgeClient();
        Assert.Throws<InvalidOperationException>(() => client.HandleState(StateJson(HistoricalIso())));
    }

    [Fact]
    public void Dispatch_routes_by_type_and_ignores_unknown_messages()
    {
        var client = new GridBridgeClient();
        SimFrame? received = null;
        client.FrameReceived += f => received = f;

        client.Dispatch(TopologyJson);
        client.Dispatch("""{ "type": "heartbeat" }""");   // unknown type: forward-compatible no-op
        client.Dispatch(StateJson(HistoricalIso()));

        Assert.NotNull(received);
    }

    [Fact]
    public void Query_result_runs_the_payload_solveExport_through_SolveImport()
    {
        // A full gridsim-solve-export/1 document (as SolveExport writes) becomes the query payload's solveExport.
        var trace = new SolveTrace();
        PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(),
            new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("case9", "cpu", "deadbeef", "2026-07-01T00:00:00Z", 1.0, "test");
        string solveExport = SolveExport.ToJson(IeeeCases.Case9(), solved, trace, meta);

        string resultJson = $$"""
        {
          "type": "result",
          "op": "scenario",
          "at": "2026-07-01T00:00:00Z",
          "payload": {
            "verdict": "infeasible",
            "bindingConstraint": "B6 Scotland-England",
            "violations": [
              { "kind": "branch_overload", "element": "L14-15", "value": 112.0, "limit": 100.0, "message": "over rating" }
            ],
            "solveExport": {{solveExport}}
          }
        }
        """;

        var client = new GridBridgeClient();
        QueryResult qr = client.HandleResult(resultJson);

        Assert.Equal("scenario", qr.Op);
        Assert.Equal("infeasible", qr.Verdict);
        Assert.Equal("B6 Scotland-England", qr.BindingConstraint);
        Assert.Single(qr.Violations);
        Assert.Equal("L14-15", qr.Violations[0].Element);
        Assert.Equal(112.0, qr.Violations[0].Value);

        // The embedded solve-export reconstructed exactly as a loaded run would.
        Assert.NotNull(qr.Solve);
        Assert.Equal("case9", qr.Solve!.Case);
        Assert.Equal(IeeeCases.Case9().BusCount, qr.Solve.Model.BusCount);
        for (int i = 0; i < solved.Vm.Length; i++)
            Assert.Equal(solved.Vm[i], qr.Solve.Result.Vm[i], 6);
    }

    // ---- op="propagation": GDA's propagation_delays corridors projected to a ProvidedTauSeconds map ----
    [Fact]
    public void Propagation_result_projects_to_a_provided_tau_map()
    {
        // Matches GDA bridge_server._op_propagation: {corridors:[{from,to,tau,km,vKmS,class}], provenance}.
        const string resultJson = """
        {
          "type": "result",
          "op": "propagation",
          "at": "2026-07-01T00:00:00Z",
          "payload": {
            "corridors": [
              { "from": 101, "to": 102, "tau": 0.0421, "km": 42.3, "vKmS": 1004.0, "class": "branch" },
              { "from": 102, "to": 110, "tau": 0.1240, "km": 130.5, "vKmS": 1052.0, "class": "gen_pair" },
              { "from": 110, "to": 999, "tau": null,   "km": null,  "vKmS": null,   "class": "branch" }
            ],
            "provenance": "distances + inertia REAL; wave_speed + tau MODELLED - unvalidated at scale"
          }
        }
        """;

        QueryResult qr = new GridBridgeClient().HandleResult(resultJson);

        Assert.Equal("propagation", qr.Op);
        Assert.NotNull(qr.Propagation);
        IReadOnlyDictionary<(int From, int To), double> tau = qr.Propagation!.TauByBusPair;
        Assert.Equal(2, tau.Count);                              // the null-tau corridor is omitted
        Assert.Equal(0.0421, tau[(101, 102)], 6);
        Assert.Equal(0.1240, tau[(102, 110)], 6);
        Assert.False(tau.ContainsKey((110, 999)));
        Assert.Contains("MODELLED", qr.Propagation.Provenance);

        // The map drops straight into the distributed config the model reads.
        var cfg = new GridSim.Core.Dynamics.Distributed.DistributedConfig { ProvidedTauSeconds = tau };
        Assert.Equal(0.0421, cfg.ProvidedTauSeconds![(101, 102)], 6);
    }

    // ---- Real wire contract: GDA's own gridsim_export.py output, split into a topology frame + a state frame ----
    //
    // The canonical sample is a full gridsim-solve-export/1 document emitted by
    //   python NetworkModel/gridsim_export.py NetworkModel/zones/zone-nged
    // (the 1292-bus NGED boundary-equivalent zone). The bridge streams it SLIM: its `model` block becomes the
    // once-per-stream `topology` message, and its `result` block becomes a per-tick `state` message. These tests
    // prove the T-D DTOs bind that real output byte-for-byte - same bus count, same solved voltages - and that a
    // now()-stamped tick is refused by the forward-inference guard before any frame is built.

    private const int NgedBusCount = 1292;

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "zone-nged-solve-export.json");

    /// <summary>Load the real solve-export and split it into (topology envelope, state result block) as the bridge does.</summary>
    private static (string TopologyJson, JsonNode ResultBlock, JsonNode ExpectedResultBuses) LoadZoneSample()
    {
        JsonNode doc = JsonNode.Parse(File.ReadAllText(FixturePath()))!;
        JsonNode model = doc["model"]!.DeepClone();
        JsonNode result = doc["result"]!.DeepClone();

        var topology = new JsonObject { ["type"] = "topology", ["model"] = model };
        return (topology.ToJsonString(), result, doc["result"]!["buses"]!.DeepClone());
    }

    private static string StateEnvelope(JsonNode resultBlock, string isoTimestamp) =>
        new JsonObject
        {
            ["type"] = "state",
            ["t"] = isoTimestamp,
            ["result"] = resultBlock.DeepClone(),
        }.ToJsonString();

    [Fact]
    public void Real_zone_export_binds_as_topology_plus_state_with_1292_buses_and_matching_voltages()
    {
        Assert.True(File.Exists(FixturePath()), $"missing wire-contract fixture: {FixturePath()}");
        (string topologyJson, JsonNode resultBlock, JsonNode expectedBuses) = LoadZoneSample();

        var client = new GridBridgeClient();
        SimFrame? received = null;
        client.FrameReceived += f => received = f;

        // 1) topology frame -> cache the network.
        GridModel topo = client.HandleTopology(topologyJson);
        Assert.Equal(NgedBusCount, topo.BusCount);
        Assert.Same(topo, client.Topology);

        // 2) slim state frame (historical t) -> reconstruct the PowerFlowResult / SimFrame against the cached topology.
        SimFrame frame = client.HandleState(StateEnvelope(resultBlock, HistoricalIso()));
        Assert.Same(received, frame);

        PowerFlowResult r = frame.Result;
        Assert.True(r.Converged);
        Assert.Equal(NgedBusCount, r.BusIds.Count);
        Assert.Equal(NgedBusCount, r.Vm.Length);
        Assert.Same(topo, frame.Model);   // the cached topology drives the render, not a per-tick copy

        // 3) every solved bus voltage/angle/injection matches the sample the serializer produced, mapped by id.
        JsonArray expected = expectedBuses.AsArray();
        Assert.Equal(NgedBusCount, expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            JsonNode b = expected[i]!;
            Assert.Equal(b["id"]!.GetValue<int>(), r.BusIds[i]);
            Assert.Equal(b["vm"]!.GetValue<double>(), r.Vm[i], 9);
            Assert.Equal(b["vaDeg"]!.GetValue<double>(), r.VaDeg[i], 9);
            Assert.Equal(b["pInjMw"]!.GetValue<double>(), r.PInjMw[i], 6);
        }

        // Voltages land in the plausible transmission band the solve reported (0.995-1.020 pu for this case).
        Assert.All(r.Vm, v => Assert.InRange(v, 0.90, 1.10));
    }

    [Fact]
    public void Real_zone_state_stamped_now_is_rejected_by_the_forward_inference_guard()
    {
        Assert.True(File.Exists(FixturePath()), $"missing wire-contract fixture: {FixturePath()}");
        (string topologyJson, JsonNode resultBlock, _) = LoadZoneSample();

        var client = new GridBridgeClient();
        bool raised = false;
        client.FrameReceived += _ => raised = true;
        client.HandleTopology(topologyJson);

        // A now()-stamped tick of the very same real network is inside the 169 h horizon -> refused before any frame.
        Assert.Throws<ForwardInferenceException>(() => client.HandleState(StateEnvelope(resultBlock, RecentIso())));
        Assert.False(raised);
    }

    [Fact]
    public void Topology_boundaries_ride_the_model_block()
    {
        const string json = """
        {
          "type": "topology",
          "model": {
            "name": "b-test", "baseMva": 100, "busCount": 2,
            "buses": [ {"id":1,"type":"slack","baseKv":400}, {"id":2,"type":"pq","baseKv":400,"pd":10} ],
            "generators": [ {"bus":1,"pg":10} ],
            "branches": [ {"from":1,"to":2,"r":0.01,"x":0.1,"name":"CUT-1"} ],
            "boundaries": [ { "name":"B6 test", "limitMw": 4459,
                              "branches": [ {"branch":"CUT-1","direction":-1} ] } ]
          }
        }
        """;
        var msg = System.Text.Json.JsonSerializer.Deserialize<BridgeProtocol.TopologyMessage>(
            json, BridgeProtocol.JsonOptions)!;
        GridModel model = msg.ToModel();

        Boundary b = Assert.Single(model.Boundaries);
        Assert.Equal("B6 test", b.Name);
        Assert.Equal(4459, b.LimitMw);
        BoundaryBranch bb = Assert.Single(b.Branches);
        Assert.Equal("CUT-1", bb.Branch);
        Assert.Equal(-1, bb.Direction);

        // Older servers omit the field entirely: still fine, empty boundaries.
        const string legacyJson = """
        {
          "type": "topology",
          "model": {
            "name": "legacy", "baseMva": 100, "busCount": 1,
            "buses": [ {"id":1,"type":"slack","baseKv":400} ],
            "generators": [ {"bus":1,"pg":0} ],
            "branches": []
          }
        }
        """;
        var legacy = System.Text.Json.JsonSerializer.Deserialize<BridgeProtocol.TopologyMessage>(
            legacyJson, BridgeProtocol.JsonOptions)!;
        Assert.Empty(legacy.ToModel().Boundaries);
    }

    [Fact]
    public void Estimate_block_rides_the_state_message()
    {
        var client = new GridBridgeClient();
        client.HandleTopology(TopologyJson);

        string? seenSource = null;
        BridgeProtocol.EstimateBlock? seenEstimate = null;
        client.EstimateReceived += (_, src, est) => { seenSource = src; seenEstimate = est; };

        // An estimated frame: the wire block the bridge server attaches per estimated tick.
        string json = StateJson(HistoricalIso()).TrimEnd();
        json = json[..^1] + $$"""
          ,
          "source": "estimated",
          "estimate": {
            "objectiveJ": 194.7, "chiThreshold": 358.2, "chiPassed": true,
            "dof": 316, "measurementsN": 948, "removedN": 1, "iterations": 18
          }
        }
        """;
        client.HandleState(json);

        Assert.Equal("estimated", seenSource);
        Assert.NotNull(seenEstimate);
        Assert.Equal(194.7, seenEstimate!.ObjectiveJ!.Value, 9);
        Assert.True(seenEstimate.ChiPassed);
        Assert.Equal(948, seenEstimate.MeasurementsN);
        Assert.Equal(1, seenEstimate.RemovedN);
        Assert.Equal(18, seenEstimate.Iterations);

        // Legacy frame (no source/estimate): the event still fires, with nulls - additive contract.
        seenSource = "sentinel";
        client.HandleState(StateJson(HistoricalIso()));
        Assert.Null(seenSource);
        Assert.Null(seenEstimate);
    }

    /// <summary>An estimated state frame carrying the given estimate-block JSON members.</summary>
    private static string EstimatedStateJson(string estimateMembers)
    {
        string json = StateJson(HistoricalIso()).TrimEnd();
        return json[..^1] + ",\n  \"source\": \"estimated\",\n  \"estimate\": { " + estimateMembers + " }\n}";
    }

    [Fact]
    public void N214_a_chi_square_test_that_did_not_run_rides_the_state_message_and_reads_not_run()
    {
        // LF1 N214 (October 2026): the bridge sends chiTest "not-run" with chiPassed false (as GDA writes the lake row);
        // the shared quality text says "chi-square not run" (it used to say FAIL), and the pill stays red.
        var client = new GridBridgeClient();
        client.HandleTopology(TopologyJson);
        BridgeProtocol.EstimateBlock? seen = null;
        client.EstimateReceived += (_, _, est) => seen = est;

        client.HandleState(EstimatedStateJson("\"objectiveJ\": 0.0, \"chiPassed\": false, \"chiTest\": \"not-run\", \"measurementsN\": 12"));
        Assert.NotNull(seen);
        Assert.Equal("not-run", seen!.ChiTest);
        Assert.StartsWith("chi-square not run  *  J=0.0  n=12", seen.QualitySummary(), StringComparison.Ordinal);
        Assert.False(seen.QualityOk);
    }

    [Theory]
    [InlineData("\"chiPassed\": false, \"chiTest\": \"NotRun\"", "it must be")]
    [InlineData("\"chiPassed\": false, \"chiTest\": \"\"", "it must be")]
    [InlineData("\"chiPassed\": true, \"chiTest\": \"not-run\"", "contradicts chiPassed true")]
    [InlineData("\"chiPassed\": false, \"chiTest\": \"passed\"", "contradicts chiPassed false")]
    [InlineData("\"chiPassed\": true, \"chiTest\": \"failed\"", "contradicts chiPassed true")]
    public void N214_an_unknown_or_contradictory_chiTest_is_refused_before_the_frame_is_published(string members, string expected)
    {
        var client = new GridBridgeClient();
        client.HandleTopology(TopologyJson);
        bool published = false;
        client.FrameReceived += _ => published = true;
        client.EstimateReceived += (_, _, _) => published = true;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => client.HandleState(EstimatedStateJson(members)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.False(published);
    }

    [Theory]
    [InlineData("\"from\": 2, \"to\": 3", "\"from\": 2, \"to\": 99")]     // a branch to a bus that does not exist
    [InlineData("\"from\": 2, \"to\": 3", "\"from\": 3, \"to\": 3")]      // a self-loop
    [InlineData("{ \"id\": 3, \"type\": \"Pq\"", "{ \"id\": 2, \"type\": \"Pq\"")]   // a duplicate bus id
    public void A_structurally_invalid_topology_is_refused_by_name(string from, string to)
    {
        // GR-147: BuildModel runs GridModel.Validate, as every other entry loader does.
        string bad = TopologyJson.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(TopologyJson, bad);
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => BridgeProtocol.ReadTopology(bad).ToModel());
        Assert.Contains("not a valid grid model", ex.Message, StringComparison.Ordinal);
    }
}
