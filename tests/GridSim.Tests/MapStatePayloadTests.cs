using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Projection;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// N022 (R132 remainder): the network map's per-frame state is sent as topology-aligned columns
/// (<see cref="MapStatePayload"/>) instead of one JSON object per bus and per branch, and the ASSETS graph is opt-in on
/// /ws/sim. These tests prove the map receives bit-identical numbers and measure the bytes on gb-full.
/// </summary>
public sealed class MapStatePayloadTests
{
    private readonly ITestOutputHelper _out;
    public MapStatePayloadTests(ITestOutputHelper output) => _out = output;

    // Blazor's JS interop serialises with the web defaults (camelCase); the same options give the same number text.
    private static readonly JsonSerializerOptions Interop = new(JsonSerializerDefaults.Web);

    // /ws/sim's serializer: web defaults plus NonFiniteDoubleConverter (cached, CA1869).
    private static readonly JsonSerializerOptions WsJson = new(JsonSerializerDefaults.Web) { Converters = { new NonFiniteDoubleConverter() } };

    /// <summary>Decode a payload the way map-interop.js setStateColumns does, back into the old row shape.</summary>
    private static (List<(int Id, double Vm, double Va, double Pi)> Nodes, List<(int Index, double P, double Q, double L, double F)> Edges)
        Decode(TopologyDto topo, string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        JsonElement r = d.RootElement;
        var nodes = new List<(int, double, double, double)>();
        for (int i = 0; i < topo.Nodes.Count; i++)
        {
            if (r.GetProperty("vm")[i].ValueKind == JsonValueKind.Null) continue;
            nodes.Add((topo.Nodes[i].Id, r.GetProperty("vm")[i].GetDouble(), r.GetProperty("va")[i].GetDouble(), r.GetProperty("pi")[i].GetDouble()));
        }
        var edges = new List<(int, double, double, double, double)>();
        for (int i = 0; i < topo.Edges.Count; i++)
        {
            if (r.GetProperty("ep")[i].ValueKind == JsonValueKind.Null) continue;
            edges.Add((topo.Edges[i].Index, r.GetProperty("ep")[i].GetDouble(), r.GetProperty("eq")[i].GetDouble(),
                r.GetProperty("el")[i].GetDouble(), r.GetProperty("ef")[i].GetDouble()));
        }
        return (nodes, edges);
    }

    /// <summary>The old setState message, decoded the same way (what the map used to receive).</summary>
    private static (List<(int Id, double Vm, double Va, double Pi)> Nodes, List<(int Index, double P, double Q, double L, double F)> Edges)
        DecodeOld(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        var nodes = d.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => (n.GetProperty("id").GetInt32(), n.GetProperty("vm").GetDouble(), n.GetProperty("vaDeg").GetDouble(), n.GetProperty("pInjMw").GetDouble()))
            .ToList();
        var edges = d.RootElement.GetProperty("edges").EnumerateArray()
            .Select(e => (e.GetProperty("index").GetInt32(), e.GetProperty("pFromMw").GetDouble(), e.GetProperty("qFromMvar").GetDouble(),
                e.GetProperty("pLossMw").GetDouble(), e.GetProperty("loadingFrac").GetDouble()))
            .ToList();
        return (nodes, edges);
    }

    private static string OldJson(FrameEnvelope f) => JsonSerializer.Serialize(new { nodes = f.Nodes, edges = f.Edges }, Interop);
    private static string NewJson(TopologyDto t, FrameEnvelope f) => JsonSerializer.Serialize(MapStatePayload.Build(t, f), Interop);

    /// <summary>Bit-for-bit equality of the decoded rows, keyed by id / index (row order may differ).</summary>
    private static void AssertSameBits(
        (List<(int Id, double Vm, double Va, double Pi)> Nodes, List<(int Index, double P, double Q, double L, double F)> Edges) expected,
        (List<(int Id, double Vm, double Va, double Pi)> Nodes, List<(int Index, double P, double Q, double L, double F)> Edges) actual)
    {
        static long B(double v) => BitConverter.DoubleToInt64Bits(v);
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        var an = actual.Nodes.ToDictionary(n => n.Id);
        foreach (var n in expected.Nodes)
        {
            var a = an[n.Id];
            Assert.Equal(B(n.Vm), B(a.Vm)); Assert.Equal(B(n.Va), B(a.Va)); Assert.Equal(B(n.Pi), B(a.Pi));
        }
        Assert.Equal(expected.Edges.Count, actual.Edges.Count);
        var ae = actual.Edges.ToDictionary(e => e.Index);
        foreach (var e in expected.Edges)
        {
            var a = ae[e.Index];
            Assert.Equal(B(e.P), B(a.P)); Assert.Equal(B(e.Q), B(a.Q)); Assert.Equal(B(e.L), B(a.L)); Assert.Equal(B(e.F), B(a.F));
        }
    }

    [Fact]
    public void The_map_receives_the_same_numbers_bit_for_bit()
    {
        GridModel model = WebProjectionFixtures.MixedFleet();
        SimFrame f = WebProjectionFixtures.Solve(model);
        TopologyDto topo = FrameProjector.BuildTopology("mixed", "Mixed", model, replay: false, frameCount: 0);
        FrameEnvelope env = FrameProjector.Project(f);
        AssertSameBits(DecodeOld(OldJson(env)), Decode(topo, NewJson(topo, env)));
    }

    [Fact]
    public void Columns_follow_topology_order_and_mark_missing_or_non_finite_as_null()
    {
        var topo = new TopologyDto("topology", "t", "t", 100, 3, false, 0,
            new[] { new NodeDto(7, 0, 0, 400, "a", "Slack"), new NodeDto(3, 0, 0, 400, "b", "Pq"), new NodeDto(9, 0, 0, 132, "c", "Pq") },
            new[] { new EdgeDto(0, 7, 3, false, "e0", 100), new EdgeDto(1, 3, 9, true, "e1", 100) },
            Array.Empty<FleetDto>(), Array.Empty<BoundaryCutDto>());
        var env = new FrameEnvelope("state", 1, 0, "00:00", null, new HeadlineDto(50, 0, 0, 0, 0, 0, 1, true, 1, 1),
            new[] { new NodeStateDto(3, 1.01, -2.5, 10, 1), new NodeStateDto(7, double.NaN, 0, 0, 0), new NodeStateDto(42, 1, 0, 0, 0) },
            new[] { new EdgeStateDto(1, 5, 1, 0.1, double.PositiveInfinity) },
            Array.Empty<BoundaryStateDto>());

        MapStatePayload p = MapStatePayload.Build(topo, env);
        Assert.Equal(new double?[] { null, 1.01, null }, p.Vm);          // 7: NaN -> null; 3 placed second; 9: no state
        Assert.Equal(new double?[] { 0, -2.5, null }, p.VaDeg);
        Assert.Equal(new double?[] { 0, 10, null }, p.PInjMw);
        Assert.Equal(new double?[] { null, 5 }, p.EdgePFromMw);          // edge 0 has no state
        Assert.Equal(new double?[] { null, 1 }, p.EdgeQFromMvar);
        Assert.Equal(new double?[] { null, 0.1 }, p.EdgePLossMw);
        Assert.Equal(new double?[] { null, null }, p.EdgeLoadingFrac);   // +inf -> null
        Assert.Equal("{\"vm\":[null,1.01,null],\"va\":[0,-2.5,null],\"pi\":[0,10,null],\"ep\":[null,5],\"eq\":[null,1],\"el\":[null,0.1],\"ef\":[null,null]}",
            JsonSerializer.Serialize(p, Interop));
    }

    [Fact]
    public void Identical_state_is_recognised_bit_for_bit()
    {
        MapStatePayload P(double v) => new(new double?[] { v, null }, new double?[] { 0, null }, new double?[] { 0, null },
            Array.Empty<double?>(), Array.Empty<double?>(), Array.Empty<double?>(), Array.Empty<double?>());
        Assert.True(P(1.0).SameBitsAs(P(1.0)));
        Assert.False(P(1.0).SameBitsAs(P(1.0000000000000002)));
        Assert.False(P(0.0).SameBitsAs(P(-0.0)));   // serialise differently ("0" vs "-0"), so not the same message
        Assert.False(P(1.0).SameBitsAs(null));
        var withNull = new MapStatePayload(new double?[] { null, null }, new double?[] { 0, null }, new double?[] { 0, null },
            Array.Empty<double?>(), Array.Empty<double?>(), Array.Empty<double?>(), Array.Empty<double?>());
        Assert.False(P(1.0).SameBitsAs(withNull));
    }

    [Fact]
    public void Ws_sim_strips_the_asset_graph_unless_asked()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        FrameEnvelope env = FrameProjector.Project(f) with { AssetGraph = AssetGraphProjection.Build(f.Model, f.Result) };
        var shape = new SimWebSocket.FrameShape();
        Assert.Null(shape.Apply(env).AssetGraph);
        shape.IncludeAssetGraph = true;
        // Moved 2026-10 (N154, Ruling R1, Mark delegated 2026-10-04): the EXPERIMENTAL wave field is now opt-in on
        // /ws/sim as the asset graph is, and this frame carries one, so "nothing removed" needs both opt-ins. Before:
        // only IncludeAssetGraph = true was set here; after: IncludeWaveField = true as well. The assertion's intent (a
        // connection that asks for everything gets the very same frame) is unchanged.
        shape.IncludeWaveField = true;
        Assert.Same(env, shape.Apply(env));
        FrameEnvelope bare = env with { AssetGraph = null };
        shape.IncludeAssetGraph = false;
        Assert.Same(bare, shape.Apply(bare));
    }

    /// <summary>
    /// The measurement behind N022's numbers, on gb-full (3,539 buses): the old per-frame map message against the
    /// column payload, both serialised as Blazor does, and the ASSETS graph a /ws/sim state frame no longer carries by
    /// default. Exact character counts go to the test output (run with a logger that shows it); the assertions pin the
    /// direction and the bit-identity on the real network.
    /// </summary>
    [Fact]
    public void Gb_full_bytes_per_frame_before_and_after()
    {
        GridModel model = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-full"));
        var live = new LiveSim(model, holdSteady: false, SolverKind.Auto);
        SimFrame f = live.Step(0, 20);
        TopologyDto topo = FrameProjector.BuildTopology("gb-full", "GB full", model, replay: false, frameCount: 0);
        FrameEnvelope env = FrameProjector.Project(f);

        string before = OldJson(env);
        string after = NewJson(topo, env);
        AssertSameBits(DecodeOld(before), Decode(topo, after));
        _out.WriteLine($"gb-full map state per frame: buses {topo.Nodes.Count}, branches {topo.Edges.Count}, solver {live.ActiveSolver}, converged {f.Result.Converged}");
        _out.WriteLine($"  before (setState rows):    {before.Length} chars, {System.Text.Encoding.UTF8.GetByteCount(before)} UTF-8 bytes");
        _out.WriteLine($"  after  (setStateColumns):  {after.Length} chars, {System.Text.Encoding.UTF8.GetByteCount(after)} UTF-8 bytes");
        Assert.True(after.Length < before.Length, $"column payload {after.Length} not smaller than {before.Length}");

        // /ws/sim serialises with web defaults plus NonFiniteDoubleConverter.
        string graph = JsonSerializer.Serialize(AssetGraphProjection.Build(f.Model, f.Result), WsJson);
        _out.WriteLine($"  /ws/sim assetGraph block (now opt-in): {graph.Length} chars, {System.Text.Encoding.UTF8.GetByteCount(graph)} UTF-8 bytes");
        Assert.True(graph.Length > 0);
    }
}
