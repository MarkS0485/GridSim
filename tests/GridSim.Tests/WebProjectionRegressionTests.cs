using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression guards for the web projection layer: the map / asset-graph JS-interop path must never emit a
/// non-finite double (a null coordinate breaks the schematic drawing), the shared JSON converter must turn any
/// inf/NaN into null, and the on-demand harmonics study must return a sane (finite, non-negative) THD.
/// </summary>
public sealed class WebProjectionRegressionTests
{
    private static void AssertFinite(double v, string what) =>
        Assert.True(double.IsFinite(v), $"{what} must be finite but was {v}");

    // Cached once (CA1869): the same converter config the web HTTP/JS-interop paths use.
    private static readonly JsonSerializerOptions s_nonFiniteJson = BuildNonFiniteJson();
    private static JsonSerializerOptions BuildNonFiniteJson()
    {
        var o = new JsonSerializerOptions();
        o.Converters.Add(new NonFiniteDoubleConverter());
        return o;
    }

    // ---- map projection: every schematic double is finite ------------------------------------------------

    [Fact]
    public void BuildMap_ProducesAllFiniteNodeAndEdgeCoordinates()
    {
        GridModel model = WebProjectionFixtures.MixedFleet();
        TopologyDto topo = FrameProjector.BuildTopology("mixed", "Mixed fleet", model, replay: false, frameCount: 0);
        MapDto map = MapProjection.BuildMap(topo);

        Assert.NotEmpty(map.Nodes);
        foreach (MapNodeDto n in map.Nodes)
        {
            AssertFinite(n.X, "node.X"); AssertFinite(n.Y, "node.Y"); AssertFinite(n.BaseKv, "node.BaseKv");
        }
        foreach (MapEdgeDto e in map.Edges)
            AssertFinite(e.RateAMva, "edge.RateAMva");
    }

    [Fact]
    public void BuildEdgeState_ProducesAllFiniteLoadingAndFlow()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        FrameEnvelope env = FrameProjector.Project(f);
        IReadOnlyList<MapEdgeStateDto> states = MapProjection.BuildEdgeState(env);

        Assert.NotEmpty(states);
        foreach (MapEdgeStateDto s in states)
        {
            AssertFinite(s.LoadingFrac, "edgeState.LoadingFrac");
            AssertFinite(s.FlowMw, "edgeState.FlowMw");
        }
    }

    // ---- asset graph: every carried double is finite -----------------------------------------------------

    [Fact]
    public void AssetGraph_Build_ProducesAllFiniteDoubles()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        AssetGraphDto g = AssetGraphProjection.Build(f.Model, f.Result);

        AssertFinite(g.SystemInertiaGvaS, "SystemInertiaGvaS");
        Assert.NotEmpty(g.Generators);
        foreach (GenAssetDto a in g.Generators)
        {
            AssertFinite(a.Pg, "gen.Pg"); AssertFinite(a.Qg, "gen.Qg"); AssertFinite(a.PmaxMw, "gen.PmaxMw");
            AssertFinite(a.MBase, "gen.MBase"); AssertFinite(a.HSeconds, "gen.HSeconds"); AssertFinite(a.Vg, "gen.Vg");
            AssertFinite(a.QminMw, "gen.QminMw"); AssertFinite(a.QmaxMw, "gen.QmaxMw");
        }
        foreach (BranchAssetDto b in g.Branches)
        {
            AssertFinite(b.RateAMva, "branch.RateAMva"); AssertFinite(b.RPu, "branch.RPu");
            AssertFinite(b.XPu, "branch.XPu"); AssertFinite(b.BPu, "branch.BPu");
            AssertFinite(b.TapRatio, "branch.TapRatio"); AssertFinite(b.PhaseShiftDeg, "branch.PhaseShiftDeg");
            AssertFinite(b.LengthKm, "branch.LengthKm"); AssertFinite(b.PFromMw, "branch.PFromMw");
            AssertFinite(b.PToMw, "branch.PToMw"); AssertFinite(b.QFromMvar, "branch.QFromMvar");
            AssertFinite(b.QToMvar, "branch.QToMvar"); AssertFinite(b.PLossMw, "branch.PLossMw");
            AssertFinite(b.QLossMvar, "branch.QLossMvar"); AssertFinite(b.LoadingPercent, "branch.LoadingPercent");
            AssertFinite(b.RatingMva, "branch.RatingMva");
        }
        foreach (BusAssetDto b in g.Buses)
        {
            AssertFinite(b.BaseKv, "bus.BaseKv"); AssertFinite(b.DemandMw, "bus.DemandMw");
            AssertFinite(b.DemandMvar, "bus.DemandMvar"); AssertFinite(b.Vm, "bus.Vm");
            AssertFinite(b.VaDeg, "bus.VaDeg"); AssertFinite(b.PInjMw, "bus.PInjMw"); AssertFinite(b.QInjMvar, "bus.QInjMvar");
        }
    }

    /// <summary>The finite guard must hold even when the model carries +/-inf Q-limits (GDA-style): the
    /// projection carries a bool + 0 sentinel instead of leaking the infinity to the browser.</summary>
    [Fact]
    public void AssetGraph_WithInfiniteQLimits_StaysFinite()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400),
                new Bus(2, BusType.Pq, Pd: 500, BaseKv: 400),
            },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            // Default Qmin/Qmax are -inf/+inf and Pmax is +inf - the exact non-finite fields the guard exists for.
            Generators = new[] { new Generator(1, 500, MBase: 600, H: 4, Type: "CCGT") },
        };
        SimFrame f = WebProjectionFixtures.Solve(model);
        AssetGraphDto g = AssetGraphProjection.Build(f.Model, f.Result);
        foreach (GenAssetDto a in g.Generators)
        {
            AssertFinite(a.PmaxMw, "gen.PmaxMw"); AssertFinite(a.QminMw, "gen.QminMw"); AssertFinite(a.QmaxMw, "gen.QmaxMw");
        }
    }

    // ---- NonFiniteDoubleConverter: inf/NaN -> JSON null --------------------------------------------------

    [Fact]
    public void NonFiniteDoubleConverter_SerialisesInfAndNanAsNull()
    {
        // Default System.Text.Json throws on NaN/Infinity; the converter must instead emit null (and not throw).
        double[] values = { double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1.5 };
        string json = JsonSerializer.Serialize(values, s_nonFiniteJson);
        Assert.Equal("[null,null,null,1.5]", json);
    }

    // ---- HARMONICS on-demand study smoke test -----------------------------------------------------------

    [Fact]
    public async Task RunHarmonicsStudy_OnCase9_ReturnsSaneThd()
    {
        // Small case: the study is a per-order complex solve (fast for 9 buses). Exercises the real public
        // entry point (SimSession.RunHarmonicsStudy -> HarmonicsProjection.FromCore).
        await using var session = SimSession.CreateLive("t-harm", "case9", "IEEE 9-bus", IeeeCases.Case9(), holdSteady: true);
        HarmonicsDto dto = session.RunHarmonicsStudy();

        Assert.False(dto.HeavyModelsSkipped);           // the study actually ran (not the guarded stub)
        Assert.NotEmpty(dto.BusSpectra);
        foreach (HarmonicBusSpectrumDto s in dto.BusSpectra)
        {
            Assert.True(double.IsFinite(s.ThdPct), "bus THD must be finite");
            Assert.True(s.ThdPct >= 0, "bus THD must be non-negative");
        }
        Assert.True(double.IsFinite(dto.Indices.WorstThdPct) && dto.Indices.WorstThdPct >= 0);
    }
}
