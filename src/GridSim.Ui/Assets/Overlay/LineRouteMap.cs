using System.Globalization;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Geography;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Assets.Overlay;

// =====================================================================================================================
// UI programme Phase 5d (2026-10-21; ASSETS plan 4.1 5d "map polyline", question Q6: no second map inside ASSETS, Show on
// map goes to NETWORK).
//
// The NETWORK map's optional "Line routes" layer, UI-neutral (no Avalonia here; the map control draws what this says).
// Each line route of the asset overlay whose far towers have geographic positions is one polyline through those towers,
// in route order (LineRoutes.Order, the same order its OVERVIEW page shows), each tower projected to map world
// coordinates by THE MAP'S OWN GEOGRAPHY PROJECTION (GeographyData.WorldOf: the equirectangular GB box the DNO and GSP
// layers, the geocoded asset markers and the coastline use), and to the screen by the map's own view transform
// (SchematicView.WorldToScreen). A route with fewer than two geographic towers (a route placed by chainage only) is not
// drawn and is named in the layer's hint with the reason.
//
// The layer is READ-ONLY (no tower dragging in this slice) and DISPLAY ONLY: it reads the overlay as carried and the
// map's own view; nothing here reaches the solver (the Phase 5a pin and guard tests). In Run mode a polyline is coloured
// by the loading bin of the line it carries when that line is drawn on the map with a solved state (the corridor's own
// LoadBucket); otherwise it takes the layer's neutral colour.
// =====================================================================================================================

/// <summary>One line route drawn on the map: its id and node, the line it carries, and its towers in world coordinates.</summary>
/// <param name="RouteId">The lineRoute record id.</param>
/// <param name="RouteKey">The record's ASSETS key (a click selects it).</param>
/// <param name="LineName">The name of the line it carries as the overlay writes it, or null.</param>
/// <param name="BranchIndex">The carried line's index in the model's branch list (resolved by the overlay's rule: the
/// name exactly one line of the model carries), or null when it names none.</param>
/// <param name="TowerIds">The towers the polyline runs through ("T1", "T2", ...), in route order.</param>
/// <param name="Points">The towers' world points, in route order (GeographyData.WorldOf of each far tower).</param>
public sealed record LineRoutePolyline(string RouteId, AssetKey RouteKey, string? LineName, int? BranchIndex,
    IReadOnlyList<string> TowerIds, IReadOnlyList<WorldPoint> Points);

/// <summary>A line route the layer does not draw, and why.</summary>
/// <param name="RouteId">The lineRoute record id.</param>
/// <param name="Reason">Why it is not drawn ("chainage only: no tower has a geographic position").</param>
public sealed record LineRouteNotDrawn(string RouteId, string Reason);

/// <summary>
/// The "Line routes" layer of the NETWORK map (Phase 5d): what to draw, what is left out and why, and the layer's hit
/// test, colour bin and framing over a <see cref="SchematicView"/>. Immutable; built once per loaded case.
/// </summary>
public sealed class LineRouteMap
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private LineRouteMap(IReadOnlyList<LineRoutePolyline> drawn, IReadOnlyList<LineRouteNotDrawn> notDrawn)
    {
        Drawn = drawn;
        NotDrawn = notDrawn;
        Hint = drawn.Count + notDrawn.Count == 0 ? ""
            : "LINE ROUTES (CARRIED, NOT USED BY THE SOLVER): " + drawn.Count.ToString(Inv) + " drawn"
              + (notDrawn.Count == 0 ? "" : "; not drawn: " + string.Join("; ", notDrawn.Select(n => "'" + n.RouteId + "' (" + n.Reason + ")")));
    }

    /// <summary>No routes (no overlay, or an overlay without a line route).</summary>
    public static LineRouteMap Empty { get; } = new(Array.Empty<LineRoutePolyline>(), Array.Empty<LineRouteNotDrawn>());

    /// <summary>The routes drawn, in overlay order.</summary>
    public IReadOnlyList<LineRoutePolyline> Drawn { get; }

    /// <summary>The routes not drawn, in overlay order, each with its reason.</summary>
    public IReadOnlyList<LineRouteNotDrawn> NotDrawn { get; }

    /// <summary>The layer's hint line: how many routes are drawn and which are not, with why; "" when the overlay has no
    /// line route.</summary>
    public string Hint { get; }

    /// <summary>The drawn route with this id, or null.</summary>
    public LineRoutePolyline? Find(string routeId) => Drawn.FirstOrDefault(d => string.Equals(d.RouteId, routeId, StringComparison.Ordinal));

    /// <summary>The map world point of a tower at (<paramref name="latitudeDeg"/>, <paramref name="longitudeDeg"/>): the
    /// map's geography projection, exactly (<see cref="GeographyData.WorldOf"/>).</summary>
    public static WorldPoint Project(double latitudeDeg, double longitudeDeg) => GeographyData.WorldOf(longitudeDeg, latitudeDeg);

    /// <summary>
    /// The layer of <paramref name="overlay"/> (null: none) over <paramref name="model"/> (the model the map shows; it
    /// resolves each route's line to its branch index by the overlay's rule, a name exactly one line carries).
    /// </summary>
    public static LineRouteMap Build(CaseAssetOverlay? overlay, GridModel? model)
    {
        if (overlay is null) return Empty;
        var drawn = new List<LineRoutePolyline>();
        var notDrawn = new List<LineRouteNotDrawn>();
        foreach (AssetOverlayRecord route in overlay.Records.Where(r => string.Equals(r.Kind, LineRoutes.RouteKind, StringComparison.Ordinal)))
        {
            LineRouteSpans spans = LineRoutes.Order(overlay, route.Id);
            if (LineRoutes.NotDrawnReason(spans) is string why)
            {
                notDrawn.Add(new LineRouteNotDrawn(route.Id, why));
                continue;
            }
            string? lineName = route.Refs.FirstOrDefault(x => x.Role == "line" && x.Target == AssetRefTarget.Line)?.Key;
            var towers = new List<string>();
            var points = new List<WorldPoint>();
            foreach (LineRouteSpan s in spans.Spans.Where(s => s.HasGeo))
            {
                towers.Add(s.ToTower);
                points.Add(Project(s.LatitudeDeg!.Value, s.LongitudeDeg!.Value));
            }
            drawn.Add(new LineRoutePolyline(route.Id, OverlayIndex.KeyOf(route.Id), lineName, LineIndex(model, lineName), towers, points));
        }
        return new LineRouteMap(drawn, notDrawn);
    }

    // The index of the one line (not a transformer) of the model with this name, or null (none, or more than one: the
    // overlay's resolution rule).
    private static int? LineIndex(GridModel? model, string? name)
    {
        if (model is null || name is null) return null;
        int found = -1;
        for (int i = 0; i < model.Branches.Count; i++)
        {
            if (model.Branches[i].IsTransformer || !string.Equals(model.Branches[i].Name, name, StringComparison.Ordinal)) continue;
            if (found >= 0) return null;   // ambiguous
            found = i;
        }
        return found >= 0 ? found : null;
    }

    /// <summary>The polyline's points on the screen of <paramref name="view"/> (its own world-to-screen transform).</summary>
    public static IReadOnlyList<ScreenPoint> ToScreen(LineRoutePolyline route, SchematicView view)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(view);
        return route.Points.Select(p => view.WorldToScreen(p.X, p.Y)).ToList();
    }

    /// <summary>The pick tolerance of a route polyline, pixels (the map's branch pick tolerance).</summary>
    public const double PickTolerancePx = SchematicView.BranchPickTolerancePx;

    /// <summary>
    /// The drawn route nearest <paramref name="at"/> on <paramref name="view"/>'s screen, within
    /// <see cref="PickTolerancePx"/> of one of its segments, or null. Ties go to the first in overlay order.
    /// </summary>
    public LineRoutePolyline? HitTest(SchematicView view, ScreenPoint at)
    {
        ArgumentNullException.ThrowIfNull(view);
        LineRoutePolyline? best = null;
        double bestD2 = PickTolerancePx * PickTolerancePx;
        foreach (LineRoutePolyline r in Drawn)
        {
            IReadOnlyList<ScreenPoint> s = ToScreen(r, view);
            for (int i = 0; i + 1 < s.Count; i++)
            {
                double d2 = SchematicView.DistSqPointToSegment(at, s[i], s[i + 1]);
                if (d2 < bestD2) { bestD2 = d2; best = r; }
            }
        }
        return best;
    }

    /// <summary>
    /// The loading colour bin of the line <paramref name="route"/> carries, as the map draws that line's corridor (the
    /// edge's LoadBucket), or null when the map has no solved state yet or the line is not drawn on it (the polyline then
    /// takes the layer's neutral colour).
    /// </summary>
    public static int? LoadingBucketOf(LineRoutePolyline route, SchematicView view)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(view);
        if (!view.HasState || route.BranchIndex is not int bi) return null;
        foreach (SchematicEdge e in view.Edges)
            if (e.Valid && e.BranchIndex == bi) return e.LoadBucket;
        return null;
    }

    /// <summary>The margin left round a framed route, pixels (as the map's fit to view).</summary>
    public const double FramePadPx = 36;

    /// <summary>
    /// Frame <paramref name="route"/> on <paramref name="view"/>: the zoom that fits its towers' bounding box in the
    /// viewport with <see cref="FramePadPx"/> each side (clamped to the view's zoom range), centred on the box. A box of
    /// no width and height (towers at one point) is centred at the view's focus zoom. False, changing nothing, while the
    /// viewport has no size.
    /// </summary>
    public static bool Frame(SchematicView view, LineRoutePolyline route)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(route);
        double w = view.ViewportWidth, h = view.ViewportHeight;
        if (w < 1 || h < 1 || route.Points.Count == 0) return false;
        double minX = route.Points.Min(p => p.X), maxX = route.Points.Max(p => p.X);
        double minY = route.Points.Min(p => p.Y), maxY = route.Points.Max(p => p.Y);
        double bw = maxX - minX, bh = maxY - minY;
        double availW = Math.Max(1, w - 2 * FramePadPx), availH = Math.Max(1, h - 2 * FramePadPx);
        double zoom = bw <= 0 && bh <= 0 ? view.FocusZoom
            : Math.Min(bw > 0 ? availW / bw : double.MaxValue, bh > 0 ? availH / bh : double.MaxValue);
        view.Zoom = Math.Clamp(zoom, SchematicView.MinZoom, view.MaxZoom);
        double cx = minX + bw / 2, cy = minY + bh / 2;
        view.OffsetX = w / 2 - cx * view.Zoom;
        view.OffsetY = h / 2 - cy * view.Zoom;
        return true;
    }
}
