namespace GridSim.Web.Projection;

// Map / topology extras for the Phase-1 FastSchematic port. The existing NodeDto/EdgeDto (Dtos.cs) are the
// wire topology; these are the SCHEMATIC enrichment - the kV band a node draws in, the from/to geometry an
// edge needs, per-frame flow direction, and the condition/stress overlay hooks (mirroring the bridge
// OverlaysBlock shape). IMPORTANT: the map path feeds JS-interop drawing, so every double here is finite-
// guarded via MapMath.Finite - a null coordinate (what NonFiniteDoubleConverter would emit for NaN/Inf)
// would break the schematic. Keep it that way.
//
// STATUS (R099, October 2026): MapDto, MapNodeDto, MapEdgeDto, MapEdgeStateDto, MapMath and MapProjection are NOT on
// the live map path. NetworkMap.razor pushes the TopologyDto and the frame's NodeStateDto / EdgeStateDto arrays to
// map-interop.js directly, and the script derives a node's drawn voltage from baseKv (it only falls back to a
// kvBand string, which TopologyDto does not carry). They are kept, not deleted, because the constitutional test
// WebProjectionRegressionTests (BuildMap_* / BuildEdgeState_*) calls MapProjection and pins these record shapes.
// Wiring them in would change the JS-interop payload the map script reads (and its tooltip text), which is a
// visible change for Mark to decide, so it is left as a reviewed open question rather than done silently.
// MapOverlaysDto IS read by NetworkMap (FrameEnvelope.MapOverlays); since N063 SimSession fills it on a bridge
// session from the bridge OverlaysBlock (MapProjection.BuildOverlays), and MapHudDto feeds the map's bridge HUD.

/// <summary>Coarse voltage-level band a node draws in, derived from its nominal kV. String so the client
/// maps it straight to a colour class without re-deriving the thresholds.</summary>
public sealed record MapNodeDto(int Id, double X, double Y, double BaseKv, string KvBand, string Name, string BusType);

/// <summary>Static schematic edge: endpoints + type + rating. Per-frame loading/direction is
/// <see cref="MapEdgeStateDto"/>, joined by <c>Index</c>.</summary>
public sealed record MapEdgeDto(int Index, int From, int To, bool IsTransformer, string Name, double RateAMva);

/// <summary>Per-frame schematic edge state: loading (0..1), signed flow, and the drawn direction.
/// <see cref="FlowFromTo"/> is true when real power leaves the <c>From</c> end (arrow points From-&gt;To).</summary>
public sealed record MapEdgeStateDto(int Index, double LoadingFrac, double FlowMw, bool FlowFromTo);

/// <summary>
/// Per-asset overlay scalars for the map's condition/stress heat overlays - the same shape as the bridge
/// <c>BridgeProtocol.OverlaysBlock</c> (stress keyed by branch NAME, condition keyed by bus id), re-exposed
/// as a projection DTO so the schematic can bind it without referencing the wire protocol type. Both maps are
/// 0..1 (or a documented scalar); absent keys mean "no overlay datum for that asset this frame".
/// </summary>
public sealed record MapOverlaysDto(
    IReadOnlyDictionary<string, double>? StressByBranch = null,
    IReadOnlyDictionary<int, double>? ConditionByBus = null);

/// <summary>
/// N063: the network map's bridge HUD scalars, from the bridge <c>BridgeProtocol.ScalarsBlock</c>: the lake's
/// condition index (shown as FRAGILITY, as the WPF HUD does) and the RoCoF headroom (Hz/s). Null = the tick carried
/// no (finite) value.
/// </summary>
public sealed record MapHudDto(double? ConditionIndex, double? RocofHeadroomHzPerS);

/// <summary>The one-time schematic payload: kV-banded nodes + typed edges, built once from a
/// <see cref="TopologyDto"/>. The FastSchematic caches this and layers per-frame edge state / overlays on top.</summary>
public sealed record MapDto(
    string CaseId,
    IReadOnlyList<MapNodeDto> Nodes,
    IReadOnlyList<MapEdgeDto> Edges);

/// <summary>Map maths shared by the schematic projection: finite guards (the map must never carry NaN/Inf)
/// and the nominal-kV -> band classification.</summary>
public static class MapMath
{
    /// <summary>Coerce a possibly non-finite double to a finite value (map DTOs must not carry NaN/Inf, which
    /// the JSON layer would emit as null and break the drawing path).</summary>
    public static double Finite(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;

    /// <summary>Classify a nominal voltage into the GB transmission bands the schematic colours by.</summary>
    public static string KvBand(double baseKv) => baseKv switch
    {
        >= 380 => "400kV",
        >= 260 => "275kV",
        >= 120 => "132kV",
        >= 40 => "66kV",
        > 0 => "LV",
        _ => "unknown",
    };
}

/// <summary>Builds the schematic DTOs from the already-projected topology/frame. Pure and finite-guarded.</summary>
public static class MapProjection
{
    /// <summary>Enrich a <see cref="TopologyDto"/> into the schematic payload (kV bands + finite coords).</summary>
    public static MapDto BuildMap(TopologyDto topo)
    {
        var nodes = new List<MapNodeDto>(topo.Nodes.Count);
        foreach (NodeDto n in topo.Nodes)
            nodes.Add(new MapNodeDto(
                n.Id, MapMath.Finite(n.X), MapMath.Finite(n.Y), MapMath.Finite(n.BaseKv),
                MapMath.KvBand(n.BaseKv), n.Name, n.BusType));

        var edges = new List<MapEdgeDto>(topo.Edges.Count);
        foreach (EdgeDto e in topo.Edges)
            edges.Add(new MapEdgeDto(e.Index, e.From, e.To, e.IsTransformer, e.Name, MapMath.Finite(e.RateAMva)));

        return new MapDto(topo.CaseId, nodes, edges);
    }

    /// <summary>
    /// N063: the bridge overlays as the map DTO. Finite-guarded as this file requires, by DROPPING a non-finite entry
    /// (an absent key already means "no overlay datum for that asset"), never by inventing a 0 that would colour the
    /// asset as healthy. Null when the tick carried no overlays block or nothing finite in it.
    /// </summary>
    public static MapOverlaysDto? BuildOverlays(GridSim.Core.IO.BridgeProtocol.OverlaysBlock? block)
    {
        if (block is null) return null;
        Dictionary<string, double>? stress = null;
        if (block.StressByBranch is { } sb)
            foreach (KeyValuePair<string, double> kv in sb)
                if (double.IsFinite(kv.Value)) (stress ??= new Dictionary<string, double>(StringComparer.Ordinal))[kv.Key] = kv.Value;
        Dictionary<int, double>? cond = null;
        if (block.ConditionByBus is { } cb)
            foreach (KeyValuePair<int, double> kv in cb)
                if (double.IsFinite(kv.Value)) (cond ??= new Dictionary<int, double>())[kv.Key] = kv.Value;
        return stress is null && cond is null ? null : new MapOverlaysDto(stress, cond);
    }

    /// <summary>N063: the bridge HUD scalars (null for no scalars block; a non-finite value reads as absent, "-").</summary>
    public static MapHudDto? BuildHud(GridSim.Core.IO.BridgeProtocol.ScalarsBlock? scalars)
    {
        if (scalars is null) return null;
        static double? F(double? v) => v is double d && double.IsFinite(d) ? d : null;
        return new MapHudDto(F(scalars.ConditionIndex), F(scalars.RocofHeadroomHzS));
    }

    /// <summary>Per-frame schematic edge state (loading + drawn direction) from the frame's edge array.</summary>
    public static IReadOnlyList<MapEdgeStateDto> BuildEdgeState(FrameEnvelope frame)
    {
        var states = new List<MapEdgeStateDto>(frame.Edges.Count);
        foreach (EdgeStateDto e in frame.Edges)
        {
            double flow = MapMath.Finite(e.PFromMw);
            states.Add(new MapEdgeStateDto(e.Index, MapMath.Finite(e.LoadingFrac), flow, flow >= 0));
        }
        // Note: edges carry no from/to coordinates; a consumer joins them to the MapDto node table by From/To.
        return states;
    }
}
