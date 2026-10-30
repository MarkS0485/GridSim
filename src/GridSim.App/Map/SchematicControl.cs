using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Geography;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.App.Map;

/// <summary>
/// The network map of the Avalonia app (UI programme Phase 1.5): the WPF <c>Visuals/FastSchematic.cs</c>, drawn with
/// Avalonia's <see cref="DrawingContext"/> and driven by the same platform-neutral <see cref="SchematicView"/>.
/// <para>
/// What it draws, in the WPF order: the GB coastline backdrop, the DNO and GSP-region outlines, the corridors (one
/// batched <see cref="StreamGeometry"/> per colour bin, the coolest first so the hot lines sit on top), the
/// transformer ties, the marching-dash flow overlay, the geocoded asset markers, the ETYS boundary cuts, the roundels
/// (dark fills, the GSP supply-area tint, then the per-voltage rings), the stacked-asset clusters, the generation fleet,
/// the labels (by level of detail, budgeted), the geography key and the minimap. FastSchematic draws no interconnector
/// routes and no selection highlight, so neither does this (the selection shows in the fly-out, as in WPF).
/// </para>
/// <para>
/// Every number comes from <see cref="SchematicView"/>: the projection with the stacked-asset fan-out, the roundel
/// radius, the level-of-detail and label thresholds, the colour bins and the hit tests. This control adds no maths of
/// its own beyond what FastSchematic's drawing code does (the culling rectangle, the de-duplication of co-located
/// segments by rounded screen position, the dash phases), which is copied here operation for operation.
/// </para>
/// <para>
/// Caching. As in WPF, the corridors, ties, roundel fills, ring list and label list are built once per view, model,
/// state and colour mode, and only the flow dashes are rebuilt every animation frame. One addition for Avalonia: while
/// the map is being dragged the cached layer is not rebuilt on every mouse move; it is drawn through a
/// <see cref="DrawingContext.PushTransform"/> translation instead, built with a wider culling margin
/// (<see cref="DragSlackPx"/>) so nothing goes missing at the edges, and rebuilt once the drag has moved further than
/// that margin. When the drag ends the layer is rebuilt exactly at the final offset, so a map at rest is exactly the
/// WPF one. A zoom always rebuilds: the roundel radius, the fan-out and the level of detail all depend on the zoom, so a
/// scaled copy of the old layer would be wrong, not just blurred.
/// </para>
/// </summary>
public sealed class SchematicControl : Control, ICustomHitTest
{
    // ---- the platform-neutral map model ----
    private readonly SchematicView _view = new();

    /// <summary>
    /// The model, with its viewport set to this control's current size (every size-dependent call goes through here, so
    /// the model sees exactly this control's width and height, as FastSchematic's View property passes ActualWidth and
    /// ActualHeight).
    /// </summary>
    public SchematicView View
    {
        get
        {
            _view.ViewportWidth = Bounds.Width;
            _view.ViewportHeight = Bounds.Height;
            return _view;
        }
    }

    private List<SchematicNode> Nodes => _view.Nodes;
    private List<SchematicEdge> Edges => _view.Edges;
    private bool _viewInitialised;

    // ---- panning ----
    private bool _dragging;
    private Point _dragStartScreen;
    private double _dragStartOffsetX, _dragStartOffsetY;
    private bool _movedWhileDown;

    // ---- retained geometry, rebuilt only when the view or state changes ----
    private bool _staticDirty = true;
    private StreamGeometry? _txGeometry;
    private readonly StreamGeometry?[] _corridorByBucket = new StreamGeometry?[SchematicView.LoadingBuckets];
    private StreamGeometry? _ringFillGeometry;
    private readonly List<RingRec> _visibleRings = new();
    private readonly List<(int NodeIndex, Point Screen)> _labelNodes = new();

    // The offset the cached layer was built at and the extra culling margin it was built with (0 at rest).
    private double _builtOffsetX, _builtOffsetY, _builtSlack;

    // The cached label layer (WPF R133): the laid-out texts and their origins, rebuilt only with the label list.
    private List<(FormattedText Text, Point Origin)>? _labelDrawing;

    /// <summary>The extra culling margin (px, each side) the cached layer is built with while the map is dragged.</summary>
    public const double DragSlackPx = 512;

    // ---- named renderer constants (values unchanged from FastSchematic) ----
    private const double DashMwPerUnit = 120.0;
    private const double DashSpeedMin = 0.4;
    private const double DashSpeedMax = 8.0;
    private const double DashPxScale = 9.0;
    private const double AnimDefaultDtS = 0.016;
    private const double AnimMaxDtS = 0.25;

    /// <summary>Most node labels drawn at once (the WPF and web budget).</summary>
    public const int LabelBudget = 220;

    /// <summary>Display kV at or above which a node counts as EHV for the minimap and ring styling.</summary>
    public const double EhvDisplayKv = 200.0;

    /// <summary>The wheel's zoom step (WPF: 1.15 in, 1 / 1.15 out).</summary>
    public const double WheelZoomFactor = 1.15;

    /// <summary>The double-click zoom on empty space (WPF: 1.6).</summary>
    public const double DoubleClickZoomFactor = 1.6;

    /// <summary>How far (px) the pointer may move with the button down and still count as a click, not a pan (WPF: 3).</summary>
    public const double ClickSlopPx = 3;

    // ---- animation ----
    private double[] _dashPhase = Array.Empty<double>();

    // ---- layers captured in Load ----
    private readonly List<FleetMarker> _fleet = new();
    private double _maxFleetMw = 1;
    private readonly List<BoundaryRec> _boundaries = new();
    private Color[] _groupColorByNode = Array.Empty<Color>();
    private bool _hasGroups;

    // ---- geography ----
    private GeographyData? _geo;
    private ImmutableSolidColorBrush[] _assetFills = Array.Empty<ImmutableSolidColorBrush>();
    private StreamGeometry? _dnoWorldGeo, _gspWorldGeo;
    private StreamGeometry? _coastWorldGeo;

    // ---- minimap ----
    private Rect _mmRect;
    private double _mmScale = 1;
    private StreamGeometry? _minimapDots;
    private double _minimapDotsScale, _minimapDotsLeft, _minimapDotsTop;

    // ---- caches of pens and brushes made on demand (quantised colours) ----
    private readonly Dictionary<(byte R, byte G, byte B, bool Ehv), ImmutablePen> _ringPenCache = new();
    private readonly Dictionary<(byte, byte, byte), ImmutableSolidColorBrush> _gspBrushCache = new();

    /// <summary>A map with no model yet; it is focusable so its keys and clicks reach it.</summary>
    public SchematicControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The surface fill under the map (transparent by default: the host supplies the background).</summary>
    public IBrush? Background { get; set; } = Brushes.Transparent;

    // ============================ public API (FastSchematic's, name for name) ============================

    /// <summary>Raised with a bus Id when a roundel is left-clicked (without dragging).</summary>
    public event Action<int>? NodeClicked;

    /// <summary>Raised with a branch index (into the model's Branches) when a corridor is clicked.</summary>
    public event Action<int>? BranchClicked;

    /// <summary>Raised with the pointer position when the right button is released over the map (the context menu).</summary>
    public event Action<Point>? RightClicked;

    // ============================ AUTHOR mode editing (UI programme Phase 4.2) ============================
    // The map control only reports gestures and draws marks; what a gesture means (select, place, connect, move) is the
    // map editor's (MapAuthorController), and every change to the case is an engine operation there. With EditMode
    // false (Run mode) none of this is reached: the Run map's input and pixels are exactly as before.

    private bool _editMode;
    private int? _editSelectedBus;
    private int? _editSelectedBranch;
    private int? _editConnectFrom;
    private Point? _pointerAt;
    private int _busDragIndex = -1;   // the node being dragged (-1: none)
    private int _busDragId;
    private double _busDragOriginWx, _busDragOriginWy;
    private bool _busDragDrillIn;     // the press is a double-click's second: released without a move, it drills in

    /// <summary>The colour of the AUTHOR mode marks (the minimap viewport's amber, so it reads as the map's own mark, not
    /// as a voltage or loading colour).</summary>
    public static readonly Color EditMarkColour = Color.FromRgb(0xE0, 0xB3, 0x41);

    private static readonly ImmutablePen EditRingPen = new(new ImmutableSolidColorBrush(EditMarkColour), 2.5);
    private static readonly ImmutablePen EditBranchPen = new(new ImmutableSolidColorBrush(Color.FromArgb(200, 0xE0, 0xB3, 0x41)), 5.0, null, PenLineCap.Round);
    private static readonly ImmutablePen EditBandPen = new(new ImmutableSolidColorBrush(EditMarkColour), 1.5, new ImmutableDashStyle(new double[] { 4, 3 }, 0));

    /// <summary>
    /// True in AUTHOR mode: a left press on a bus drags it (<see cref="BusDragged"/>) instead of panning, a click is
    /// reported as <see cref="EditClicked"/> instead of <see cref="NodeClicked"/> / <see cref="BranchClicked"/>, and the
    /// AUTHOR marks are drawn. False (the default) is the Run map, unchanged.
    /// </summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value) return;
            _editMode = value;
            if (!value)
            {
                EndBusDrag(restore: true);
                _editSelectedBus = _editSelectedBranch = _editConnectFrom = null;
                _pointerAt = null;
            }
            SyncLodExempt();
            InvalidateVisual();
        }
    }

    // 4.2 follow-up (2026-10-24): in AUTHOR the selected bus and Connect's first bus are drawn and hit at every level of
    // detail (a new low-kV bus on a large case would otherwise vanish at the fitted view); none in Run mode.
    private void SyncLodExempt()
    {
        var ids = new List<int>(2);
        if (_editMode)
        {
            if (_editSelectedBus is int sel) ids.Add(sel);
            if (_editConnectFrom is int from) ids.Add(from);
        }
        if (_view.SetLodExempt(ids)) _staticDirty = true;
    }

    /// <summary>AUTHOR mode: the bus drawn with the selection ring (null: none).</summary>
    public int? EditSelectedBus
    {
        get => _editSelectedBus;
        set { if (_editSelectedBus != value) { _editSelectedBus = value; SyncLodExempt(); InvalidateVisual(); } }
    }

    /// <summary>AUTHOR mode: the branch (an index into the model's Branches) drawn highlighted (null: none).</summary>
    public int? EditSelectedBranch
    {
        get => _editSelectedBranch;
        set { if (_editSelectedBranch != value) { _editSelectedBranch = value; InvalidateVisual(); } }
    }

    /// <summary>AUTHOR mode: Connect's first bus (ringed, with a dashed band to the pointer), or null.</summary>
    public int? EditConnectFrom
    {
        get => _editConnectFrom;
        set
        {
            if (_editConnectFrom == value) return;
            _editConnectFrom = value;
            if (value is null) _pointerAt = null;
            SyncLodExempt();
            InvalidateVisual();
        }
    }

    /// <summary>AUTHOR mode: a left click (no drag) at a map point, with the bus under it (or null) and, when there is no
    /// bus, the branch under it (or null).</summary>
    public event Action<Point, int?, int?>? EditClicked;

    /// <summary>AUTHOR mode: a bus (its id) was dragged and dropped at a map point. The map shows it back where the case
    /// has it until the editor's move is accepted and the map is reloaded.</summary>
    public event Action<int, Point>? BusDragged;

    /// <summary>True while a bus is being dragged (AUTHOR mode).</summary>
    public bool IsDraggingBus => _busDragIndex >= 0;

    // Draw the dragged node at the pointer's world point (the screen point under the inverse projection).
    private void MoveDraggedNode(Point at)
    {
        ScreenPoint w = _view.ScreenToWorld(new ScreenPoint(at.X, at.Y));
        SchematicNode nd = Nodes[_busDragIndex];
        nd.Wx = w.X;
        nd.Wy = w.Y;
        Nodes[_busDragIndex] = nd;
        _staticDirty = true;
        InvalidateVisual();
    }

    // End a bus drag; with restore the node goes back to where it was (the case decides where it is drawn).
    private void EndBusDrag(bool restore)
    {
        if (_busDragIndex < 0) return;
        if (restore && _busDragIndex < Nodes.Count)
        {
            SchematicNode nd = Nodes[_busDragIndex];
            nd.Wx = _busDragOriginWx;
            nd.Wy = _busDragOriginWy;
            Nodes[_busDragIndex] = nd;
            _staticDirty = true;
            InvalidateVisual();
        }
        _busDragIndex = -1;
        _busDragDrillIn = false;
        _movedWhileDown = false;
    }

    // The AUTHOR mode marks: the selected branch (a wide amber line), the selected bus and Connect's first bus (amber
    // rings), and Connect's dashed band from the first bus to the pointer.
    private void DrawEditMarks(DrawingContext context)
    {
        SchematicView sv = View;
        if (_editSelectedBranch is int bi && (uint)bi < (uint)Edges.Count && Edges[bi] is { Valid: true } e)
            context.DrawLine(EditBranchPen, NodeScreen(sv, e.A), NodeScreen(sv, e.B));
        foreach (int? id in new[] { _editSelectedBus, _editConnectFrom })
        {
            if (id is not int busId || !_view.IdToIndex.TryGetValue(busId, out int ni)) continue;
            double r = sv.RoundelRadius(Nodes[ni].DisplayKv) + 5;
            context.DrawEllipse(null, EditRingPen, NodeScreen(sv, ni), r, r);
        }
        if (_editConnectFrom is int from && _pointerAt is Point to && _view.IdToIndex.TryGetValue(from, out int fi))
            context.DrawLine(EditBandPen, NodeScreen(sv, fi), to);
    }

    private MapColourMode _colorMode = MapColourMode.Loading;

    /// <summary>
    /// How the map is coloured. As in WPF, changing it re-tints the cached corridors at once; the flow dashes keep the
    /// previous mode's bins until the next state arrives (FastSchematic re-buckets the edges only in SetState and
    /// SetOverlay), so the two maps show the same thing in that frame too.
    /// </summary>
    public MapColourMode ColorMode
    {
        get => _colorMode;
        set { if (_colorMode != value) { _colorMode = value; _staticDirty = true; InvalidateVisual(); } }
    }

    private bool _showFleet;

    /// <summary>When true, draw the generation fleet as fuel-coloured, capacity-sized diamonds.</summary>
    public bool ShowFleet { get => _showFleet; set { if (_showFleet != value) { _showFleet = value; InvalidateVisual(); } } }

    private bool _showMinimap = true;

    /// <summary>A corner overview map with a viewport rectangle; click it to recentre.</summary>
    public bool ShowMinimap { get => _showMinimap; set { if (_showMinimap != value) { _showMinimap = value; InvalidateVisual(); } } }

    private bool _showCoast;

    /// <summary>A faint GB coastline behind the network (only aligns with geolocated models).</summary>
    public bool ShowCoast { get => _showCoast; set { if (_showCoast != value) { _showCoast = value; InvalidateVisual(); } } }

    private bool _showBoundaries;

    /// <summary>ETYS boundary cross-sections: highlight each cut's branches with its flow / limit label.</summary>
    public bool ShowBoundaries { get => _showBoundaries; set { if (_showBoundaries != value) { _showBoundaries = value; InvalidateVisual(); } } }

    private bool _showGspAreas;

    /// <summary>Tint substations by GSP supply area (nearest EHV grid-supply point on the network).</summary>
    public bool ShowGspAreas { get => _showGspAreas; set { if (_showGspAreas != value) { _showGspAreas = value; InvalidateVisual(); } } }

    private bool _showDnoAreas;

    /// <summary>Draw the real GB DNO licence-area boundary outlines.</summary>
    public bool ShowDnoAreas { get => _showDnoAreas; set { if (_showDnoAreas != value) { _showDnoAreas = value; InvalidateVisual(); } } }

    private bool _showGspRegions;

    /// <summary>Draw the real GB GSP-region boundary outlines.</summary>
    public bool ShowGspRegions { get => _showGspRegions; set { if (_showGspRegions != value) { _showGspRegions = value; InvalidateVisual(); } } }

    private bool _showAssets;

    /// <summary>Plot the geocoded REPD/DUKES assets as fuel-coloured markers.</summary>
    public bool ShowAssets { get => _showAssets; set { if (_showAssets != value) { _showAssets = value; InvalidateVisual(); } } }

    /// <summary>True when geography GeoJSON was found locally (drives whether the geography toggles do anything).</summary>
    public bool HasGeography => _geo is not null;

    // ---- UI programme Phase 5d (2026-10-21): the asset overlay's line routes (View / Line routes) ----

    private bool _showLineRoutes;
    private LineRouteMap _lineRoutes = LineRouteMap.Empty;

    /// <summary>
    /// Draw the asset overlay's line routes as thin dashed polylines through their towers (Phase 5d). Off by default:
    /// with it off, nothing of the layer is drawn or hit-tested, so the map is exactly as before.
    /// </summary>
    public bool ShowLineRoutes { get => _showLineRoutes; set { if (_showLineRoutes != value) { _showLineRoutes = value; InvalidateVisual(); } } }

    /// <summary>The line route layer the map holds (what it draws while <see cref="ShowLineRoutes"/>).</summary>
    public LineRouteMap LineRoutes => _lineRoutes;

    /// <summary>Feed the line route layer of the loaded case (null: none). Read-only: the map never moves a tower.</summary>
    public void SetLineRoutes(LineRouteMap? routes)
    {
        _lineRoutes = routes ?? LineRouteMap.Empty;
        InvalidateVisual();
    }

    /// <summary>A click on a line route polyline (Run mode, layer shown): the lineRoute record id.</summary>
    public event Action<string>? LineRouteClicked;

    /// <summary>
    /// Frame the drawn line route <paramref name="routeId"/> (LineRouteMap.Frame: its towers' box fitted in the map with
    /// the fit-to-view margin). False, changing nothing, when the route is not drawn or the map has no size yet.
    /// </summary>
    public bool FrameLineRoute(string routeId)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        if (_lineRoutes.Find(routeId) is not LineRoutePolyline route || !LineRouteMap.Frame(View, route)) return false;
        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
        return true;
    }

    // Draw each drawn route through its towers' screen points (the map's own transform), dashed, coloured by the loading
    // bin of the line it carries when the map has a solved state and draws that line, else neutral; then the layer's hint
    // (what is drawn and what is not, with why) at the bottom left.
    private void DrawLineRoutes(DrawingContext dc)
    {
        SchematicView sv = View;
        foreach (LineRoutePolyline route in _lineRoutes.Drawn)
        {
            IReadOnlyList<ScreenPoint> pts = LineRouteMap.ToScreen(route, sv);
            if (pts.Count < 2) continue;
            var g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(new Point(pts[0].X, pts[0].Y), false);
                for (int i = 1; i < pts.Count; i++) c.LineTo(new Point(pts[i].X, pts[i].Y));
                c.EndFigure(false);
            }
            dc.DrawGeometry(null, MapPaint.LineRoutePen(LineRouteMap.LoadingBucketOf(route, sv)), g);
        }
        if (_lineRoutes.Hint.Length == 0) return;
        var text = new FormattedText(_lineRoutes.Hint, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MapPaint.LabelFace, 11, MapPaint.LabelBrush);
        const double pad = 6;
        var box = new Rect(12, Bounds.Height - text.Height - 2 * pad - 12, text.Width + 2 * pad, text.Height + 2 * pad);
        if (box.Top < 4) return;
        dc.DrawRectangle(MapPaint.GeoLegendBg, MapPaint.GeoLegendBorder, box, 4, 4);
        dc.DrawText(text, new Point(box.Left + pad, box.Top + pad));
    }

    /// <summary>True when a model with at least one bus is loaded.</summary>
    public bool HasModel => Nodes.Count > 0;

    /// <summary>How many times the cached layer has been built (for tests and the timing report).</summary>
    public int StaticBuilds { get; private set; }

    /// <summary>
    /// How long the last <see cref="Render"/> took on the UI thread (building any geometry and recording the drawing;
    /// the rasterising happens afterwards, in the compositor). A diagnostic for the timing report.
    /// </summary>
    public TimeSpan LastRenderTime { get; private set; }

    /// <summary>Capture topology and normalised coordinates, and the fleet, boundary and supply-area layers. Resets state.</summary>
    public void Load(GridModel model) => Load(model, keepView: false);

    /// <summary>
    /// UI programme Phase 4.2: <see cref="Load(GridModel)"/>, with <paramref name="keepView"/> true keeping the current
    /// zoom and pan instead of fitting the new model on the next render (the AUTHOR map reloads after every edit, and a
    /// placed or moved bus must not make the view jump). False is exactly the old Load.
    /// </summary>
    public void Load(GridModel model, bool keepView)
    {
        ArgumentNullException.ThrowIfNull(model);
        bool wasInitialised = _viewInitialised;
        EndBusDrag(restore: false);   // a reload ends any bus drag (the new model is drawn where the case says)
        _visibleRings.Clear();
        _labelNodes.Clear();
        _labelDrawing = null;
        // The nodes, edges, world bounds, display tiers, fan-out and the reset of state and overlays: SchematicView's.
        _view.Load(model);

        // ---- generation fleet markers (positioned at their bus) ----
        _fleet.Clear();
        _maxFleetMw = 1;
        foreach (Generator g in model.Generators)
        {
            if (!_view.IdToIndex.TryGetValue(g.Bus, out int gni)) continue;
            SchematicNode gnd = Nodes[gni];
            double mw = g.MBase > 0 ? g.MBase : Math.Abs(g.Pg);
            if (mw <= 0) continue;
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            _fleet.Add(new FleetMarker(gnd.Wx, gnd.Wy, mw, MapPaint.Frozen(fi.Colour)));
            if (mw > _maxFleetMw) _maxFleetMw = mw;
        }

        // ---- ETYS boundary cuts: each boundary's named branches as edge indices ----
        // The same name rule as the boundary flows this highlights (ordinal, case-sensitive, the last branch of a name
        // wins), as FastSchematic.Load.
        _boundaries.Clear();
        if (model.Boundaries.Count > 0)
        {
            var nameToEdge = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < model.Branches.Count; i++)
                if (!string.IsNullOrEmpty(model.Branches[i].Name)) nameToEdge[model.Branches[i].Name!] = i;

            foreach (Boundary bd in model.Boundaries)
            {
                var idxs = new List<int>();
                foreach (BoundaryBranch bb in bd.Branches)
                    if (nameToEdge.TryGetValue(bb.Branch, out int ei)) idxs.Add(ei);
                if (idxs.Count > 0)
                    _boundaries.Add(new BoundaryRec { Name = bd.Name, EdgeIdx = idxs.ToArray(), LimitMw = bd.LimitMw });
            }
        }

        BuildGspGroups();

        _dashPhase = new double[Edges.Count];
        _minimapDots = null;
        _ringPenCache.Clear();
        _viewInitialised = keepView && wasInitialised;   // re-fit on the next render, unless the view is kept
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Assign every bus to a GSP supply area: the nearest grid-supply point (a node of 275 kV or more) by hop count,
    /// by a multi-source breadth-first search (FastSchematic.BuildGspGroups, step for step).
    /// </summary>
    private void BuildGspGroups()
    {
        int n = Nodes.Count;
        _groupColorByNode = new Color[n];
        _hasGroups = false;
        if (n == 0) return;

        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (SchematicEdge e in Edges)
            if (e.Valid) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

        var group = new int[n];
        for (int i = 0; i < n; i++) group[i] = -1;

        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (Nodes[i].DisplayKv >= 275) { group[i] = i; q.Enqueue(i); }   // each supply point seeds its area

        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            foreach (int nb in adj[cur])
                if (group[nb] == -1) { group[nb] = group[cur]; q.Enqueue(nb); }
        }

        Color fallback = Color.FromRgb(0x4A, 0x55, 0x60);
        for (int i = 0; i < n; i++)
        {
            if (group[i] >= 0) { _groupColorByNode[i] = GroupColor(group[i]); _hasGroups = true; }
            else _groupColorByNode[i] = fallback;
        }
    }

    /// <summary>Push a freshly solved state (applied and binned by <see cref="SchematicView.ApplyState"/>).</summary>
    public void SetState(PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_view.ApplyState(result)) return;   // no model loaded
        _staticDirty = true;
        _view.RebuildEdgeBuckets(_colorMode);
        InvalidateVisual();
    }

    /// <summary>Push the GDA-bridge overlays (applied and binned by <see cref="SchematicView.ApplyOverlay"/>).</summary>
    public void SetOverlay(IReadOnlyDictionary<int, double>? byBusId, IReadOnlyDictionary<string, double>? byBranchName)
    {
        if (!_view.ApplyOverlay(byBusId, byBranchName)) return;   // no model loaded
        _staticDirty = true;
        _view.RebuildEdgeBuckets(_colorMode);
        InvalidateVisual();
    }

    /// <summary>Push live boundary transfers (matched by name, case-insensitively, as WPF) for the cut labels.</summary>
    public void SetBoundaries(IReadOnlyList<BoundaryFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        if (_boundaries.Count == 0) return;
        for (int i = 0; i < _boundaries.Count; i++)
        {
            BoundaryRec b = _boundaries[i];
            foreach (BoundaryFlow f in flows)
                if (string.Equals(f.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                {
                    b.LoadingPct = f.LoadingPercent;
                    b.FlowMw = f.FlowMw;
                    b.LimitMw = f.LimitMw;
                    break;
                }
            _boundaries[i] = b;
        }
        if (_showBoundaries) InvalidateVisual();
    }

    /// <summary>
    /// Advance the marching-dash flow phases by <paramref name="dtSeconds"/>: speed scales with |PFromMw|, direction
    /// with its sign (FastSchematic.Animate, unchanged). Only the flow overlay is redrawn.
    /// </summary>
    public void Animate(double dtSeconds)
    {
        if (!_view.HasState || Edges.Count == 0) return;
        if (dtSeconds <= 0 || dtSeconds > AnimMaxDtS) dtSeconds = AnimDefaultDtS;

        bool any = false;
        for (int i = 0; i < Edges.Count; i++)
        {
            SchematicEdge e = Edges[i];
            if (e.IsTransformer || !e.Valid) continue;
            double pxPerSec = Math.Clamp(Math.Abs(e.FlowMw) / DashMwPerUnit, DashSpeedMin, DashSpeedMax) * DashPxScale;
            double dir = e.FlowMw >= 0 ? 1.0 : -1.0;   // +flow => A->B
            _dashPhase[i] += dir * pxPerSec * dtSeconds;
            any = true;
        }
        if (any) InvalidateVisual();
    }

    /// <summary>Reset zoom and pan so the whole network fits the map with a margin.</summary>
    public void FitToView()
    {
        if (FitCore()) InvalidateVisual();
    }

    // The fit itself, without asking for a redraw: Render calls this for its first frame, and Avalonia refuses an
    // invalidation from inside the render pass (that frame is being drawn already). False when there is no size yet.
    private bool FitCore()
    {
        if (!View.FitToView())
        {
            _viewInitialised = false;   // try again once there is a size
            return false;
        }
        _viewInitialised = true;
        _staticDirty = true;
        return true;
    }

    /// <summary>Centre the view on a bus (optionally setting a zoom). Used by search-to-jump.</summary>
    public void CenterOn(int busId, double? targetZoom = null)
    {
        if (!View.CenterOn(busId, targetZoom)) return;
        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Centre on a bus and zoom to substation level (search / drill-down helper).</summary>
    public void FocusNode(int busId) => CenterOn(busId, View.FocusZoom);

    /// <summary>First bus whose name contains <paramref name="query"/> (case-insensitive), or null.</summary>
    public int? FindNode(string query) => _view.FindNode(query);

    /// <summary>The bus under <paramref name="screenPt"/> (SchematicView.HitTestNode), or null.</summary>
    public int? HitTestNode(Point screenPt) => View.HitTestNode(new ScreenPoint(screenPt.X, screenPt.Y));

    /// <summary>The branch under <paramref name="screenPt"/> (SchematicView.HitTestBranch), or null.</summary>
    public int? HitTestBranch(Point screenPt, bool cycle = false) => View.HitTestBranch(new ScreenPoint(screenPt.X, screenPt.Y), cycle);

    /// <summary>Zoom by <paramref name="factor"/> keeping the world point under <paramref name="screen"/> fixed.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        if (!View.ZoomAt(new ScreenPoint(screen.X, screen.Y), factor)) return;   // the clamped zoom would not change
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ============================ rendering ============================

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RenderCore(context);
        }
        finally
        {
            LastRenderTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
    }

    private void RenderCore(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (Background is not null) context.FillRectangle(Background, new Rect(0, 0, w, h));

        if (Nodes.Count == 0) return;
        if (!_viewInitialised && !FitCore()) return;   // still no size

        // The cached layer is reused through a translation while the pan stays inside the margin it was built with;
        // otherwise (or whenever anything else changed) it is rebuilt here for the current view.
        double dx = _view.OffsetX - _builtOffsetX, dy = _view.OffsetY - _builtOffsetY;
        if (!_staticDirty && (Math.Abs(dx) > _builtSlack || Math.Abs(dy) > _builtSlack)) _staticDirty = true;
        if (_staticDirty)
        {
            RebuildStatic(_dragging ? DragSlackPx : 0);
            dx = 0;
            dy = 0;
        }
        Matrix shift = Matrix.CreateTranslation(dx, dy);

        // GB coastline backdrop (under everything), if toggled on.
        if (_showCoast) DrawCoast(context);

        // Real DNO/GSP admin boundaries (context, under the network), if toggled on.
        if (_showDnoAreas && _dnoWorldGeo is not null) DrawRegions(context, _dnoWorldGeo, MapPaint.DnoPenBrush, 1.3);
        if (_showGspRegions && _gspWorldGeo is not null) DrawRegions(context, _gspWorldGeo, MapPaint.GspPenBrush, 0.9);

        using (context.PushTransform(shift))
        {
            // Corridors (batched by colour bin; the lowest bin first so hot lines sit on top), then transformer ties.
            ImmutablePen[] corridorPens = MapPaint.CorridorPensFor(_colorMode);
            for (int bkt = 0; bkt < SchematicView.LoadingBuckets; bkt++)
            {
                StreamGeometry? g = _corridorByBucket[bkt];
                if (g is not null) context.DrawGeometry(null, corridorPens[bkt], g);
            }
            if (_txGeometry is not null) context.DrawGeometry(null, MapPaint.TxPen, _txGeometry);
        }

        // Animated flow overlay (rebuilt every frame for the visible, in-LOD subset, at the live offset).
        DrawFlow(context);

        // Phase 5d: the asset overlay's line routes (over corridors, under roundels), if toggled on. Never on the AUTHOR
        // map: that map is the case being edited, and the routes are the loaded case's.
        if (_showLineRoutes && !_editMode) DrawLineRoutes(context);

        // Geocoded asset markers and ETYS boundary cuts (over corridors, under roundels), if toggled on.
        if (_showAssets) DrawAssets(context);
        if (_showBoundaries) DrawBoundaries(context);

        using (context.PushTransform(shift))
        {
            // Roundels: dark fills, the supply-area tint, then the per-voltage rings.
            if (_ringFillGeometry is not null) context.DrawGeometry(MapPaint.DarkFill, null, _ringFillGeometry);
            if (_showGspAreas && _hasGroups) DrawGspAreas(context);
            DrawRings(context);
        }

        // Stacked-asset clusters, then the generation fleet (live positions).
        DrawFanClusters(context);
        if (_showFleet) DrawFleet(context);

        // Labels (cached with the layer, so drawn through the same translation).
        using (context.PushTransform(shift)) DrawLabels(context);

        // UI programme Phase 4.2: the AUTHOR mode marks (selection, Connect's first bus and rubber band). Nothing is
        // drawn here in Run mode, so the Run map's pixels are exactly as before.
        if (_editMode) DrawEditMarks(context);

        // Geography key (top right) and the minimap (drawn last, on top).
        DrawGeoLegend(context);
        if (_showMinimap) DrawMinimap(context);
    }

    /// <summary>
    /// Rebuild the cached corridor and roundel layer for the current view and state (FastSchematic.RebuildStatic, step
    /// for step), culling against the WPF rectangle widened by <paramref name="slack"/> pixels each side (0 at rest).
    /// </summary>
    private void RebuildStatic(double slack)
    {
        StaticBuilds++;
        int buckets = SchematicView.LoadingBuckets;
        var sc = new StreamGeometry?[buckets];
        var ctx = new StreamGeometryContext?[buckets];
        var tx = new StreamGeometry();

        _visibleRings.Clear();
        _labelNodes.Clear();
        _labelDrawing = null;   // the label layer follows _labelNodes

        double w = Bounds.Width, h = Bounds.Height;
        var view = new Rect(-32 - slack, -32 - slack, w + 64 + 2 * slack, h + 64 + 2 * slack);
        SchematicView sv = View;
        double minKv = sv.LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        bool showLabels = sv.ShowLabels();
        double labelMinKv = sv.LabelMinKv();

        // ---- corridors + transformer ties ----
        // Co-located busbars project onto the same screen geometry, so segments are de-duplicated by rounded screen
        // position; a shared segment takes the WORST member's colour bin and a shared roundel the busbar furthest from
        // 1.0 pu (fail loud on the map), in insertion order (FastSchematic, unchanged).
        var seenTie = new HashSet<(int, int, int, int)>();
        var lineBySeg = new Dictionary<(int, int, int, int), (Point A, Point B, int Bucket)>();
        var lineOrder = new List<(int, int, int, int)>();
        var ringAt = new Dictionary<(int, int, int), int>();
        double[] vmNodes = _view.VmByNode ?? Array.Empty<double>();
        double VoltSeverity(int ni) => ni < vmNodes.Length ? Math.Abs(vmNodes[ni] - 1.0) : 0.0;
        double BucketSeverity(int bkt) => _colorMode == MapColourMode.Voltage ? Math.Abs(SchematicView.VoltageOfBucket(bkt) - 1.0) : bkt;
        static (int, int) Q(Point p) => ((int)Math.Round(p.X), (int)Math.Round(p.Y));

        using (StreamGeometryContext txCtx = tx.Open())
        {
            foreach (SchematicEdge e in Edges)
            {
                if (!e.Valid) continue;
                SchematicNode a = Nodes[e.A], b = Nodes[e.B];
                if (ehvOnly && (sv.IsHiddenAtLod(e.A, minKv) || sv.IsHiddenAtLod(e.B, minKv))) continue;

                Point pa = NodeScreen(sv, e.A), pb = NodeScreen(sv, e.B);
                if (!SegmentIntersectsRect(pa, pb, view)) continue;   // viewport culling

                (int qax, int qay) = Q(pa);
                (int qbx, int qby) = Q(pb);
                if (qax == qbx && qay == qby) continue;               // zero-length (co-located endpoints)
                (int, int, int, int) segKey = qax < qbx || (qax == qbx && qay <= qby)
                    ? (qax, qay, qbx, qby) : (qbx, qby, qax, qay);     // undirected
                if (e.IsTransformer)
                {
                    if (!seenTie.Add(segKey)) continue;
                    txCtx.BeginFigure(pa, false);
                    txCtx.LineTo(pb);
                    txCtx.EndFigure(false);
                }
                else
                {
                    int bkt = sv.BucketFor(e, _colorMode);
                    if (lineBySeg.TryGetValue(segKey, out var drawn))
                    {
                        if (BucketSeverity(bkt) > BucketSeverity(drawn.Bucket)) lineBySeg[segKey] = drawn with { Bucket = bkt };
                    }
                    else
                    {
                        lineBySeg[segKey] = (pa, pb, bkt);
                        lineOrder.Add(segKey);   // WPF enumerates its Dictionary in insertion order; this list makes it explicit
                    }
                }
            }
        }
        foreach ((int, int, int, int) key in lineOrder)
        {
            (Point pa, Point pb, int bkt) = lineBySeg[key];
            ctx[bkt] ??= (sc[bkt] = new StreamGeometry()).Open();
            ctx[bkt]!.BeginFigure(pa, false);
            ctx[bkt]!.LineTo(pb);
            ctx[bkt]!.EndFigure(false);
        }
        for (int i = 0; i < buckets; i++)
        {
            ctx[i]?.Dispose();
            _corridorByBucket[i] = sc[i];
        }
        _txGeometry = tx;

        // ---- roundels: dark fills (batched), rings recorded for per-voltage colouring ----
        var fills = new StreamGeometry();
        using (StreamGeometryContext fillCtx = fills.Open())
        {
            for (int ni = 0; ni < Nodes.Count; ni++)
            {
                SchematicNode nd = Nodes[ni];
                if (sv.IsHiddenAtLod(ni, minKv)) continue;
                Point s = NodeScreen(sv, ni);
                if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;

                double r = sv.RoundelRadius(nd.DisplayKv);
                (int qx, int qy) = Q(s);
                if (ringAt.TryGetValue((qx, qy, (int)Math.Round(r)), out int ringIdx))
                {
                    RingRec shown = _visibleRings[ringIdx];
                    if (VoltSeverity(ni) > VoltSeverity(shown.NodeIndex)) { shown.NodeIndex = ni; _visibleRings[ringIdx] = shown; }
                    continue;
                }
                ringAt[(qx, qy, (int)Math.Round(r))] = _visibleRings.Count;
                EllipseFigure(fillCtx, s, r);
                _visibleRings.Add(new RingRec { Screen = s, Radius = r, NodeIndex = ni, Ehv = nd.DisplayKv >= EhvDisplayKv });

                if (showLabels && !string.IsNullOrEmpty(nd.Name) && nd.DisplayKv >= labelMinKv)
                    _labelNodes.Add((ni, s));
            }
        }
        _ringFillGeometry = fills;

        _builtOffsetX = _view.OffsetX;
        _builtOffsetY = _view.OffsetY;
        _builtSlack = slack;
        _staticDirty = false;
    }

    private ImmutablePen CachedRingPen(Color c, bool ehv)
    {
        var key = (c.R, c.G, c.B, ehv);
        if (!_ringPenCache.TryGetValue(key, out ImmutablePen? pen))
        {
            pen = MapPaint.PlainPen(MapPaint.Frozen(c), ehv ? MapPaint.RingThicknessEhv : MapPaint.RingThicknessLv);
            _ringPenCache[key] = pen;
        }
        return pen;
    }

    private void DrawRings(DrawingContext dc)
    {
        double[] vm = _view.VmByNode ?? Array.Empty<double>();
        double[]? cond = _view.ConditionByNode;
        bool byCondition = _colorMode == MapColourMode.Condition && _view.HasOverlay && cond is not null;
        foreach (RingRec rr in _visibleRings)
        {
            Color c;
            if (byCondition && rr.NodeIndex < cond!.Length)
                c = MapPaint.ToColor(ColourRamps.Condition(cond[rr.NodeIndex]));
            else
            {
                double v = _view.HasState && rr.NodeIndex < vm.Length ? vm[rr.NodeIndex] : 1.0;
                c = MapPaint.ToColor(ColourRamps.Voltage(v));
            }
            dc.DrawEllipse(null, CachedRingPen(c, rr.Ehv), rr.Screen, rr.Radius, rr.Radius);
        }
    }

    private void DrawGspAreas(DrawingContext dc)
    {
        Color[] gc = _groupColorByNode;
        foreach (RingRec rr in _visibleRings)
        {
            if (rr.NodeIndex >= gc.Length) continue;
            Color c = gc[rr.NodeIndex];
            var key = (c.R, c.G, c.B);
            if (!_gspBrushCache.TryGetValue(key, out ImmutableSolidColorBrush? fill))
            {
                fill = MapPaint.Frozen(Color.FromArgb(170, c.R, c.G, c.B));
                _gspBrushCache[key] = fill;
            }
            dc.DrawEllipse(fill, null, rr.Screen, rr.Radius, rr.Radius);
        }
    }

    private void DrawFlow(DrawingContext dc)
    {
        if (!_view.HasState) return;

        SchematicView sv = View;
        var view = new Rect(-32, -32, Bounds.Width + 64, Bounds.Height + 64);
        double minKv = sv.LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        ImmutablePen[] flowPens = MapPaint.FlowPensFor(_colorMode);

        const double dash = 5.0;     // screen px
        const double gap = 7.0;
        const double period = dash + gap;

        for (int bkt = 0; bkt < SchematicView.LoadingBuckets; bkt++)
        {
            List<int>? indices = _view.EdgesByBucket[bkt];
            if (indices is null || indices.Count == 0) continue;

            var flow = new StreamGeometry();
            bool any = false;
            using (StreamGeometryContext g = flow.Open())
            {
                foreach (int ei in indices)
                {
                    SchematicEdge e = Edges[ei];
                    if (ehvOnly && (sv.IsHiddenAtLod(e.A, minKv) || sv.IsHiddenAtLod(e.B, minKv))) continue;

                    Point pa = NodeScreen(sv, e.A), pb = NodeScreen(sv, e.B);
                    if (!SegmentIntersectsRect(pa, pb, view)) continue;

                    double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < period * 0.5) continue;
                    double ux = dx / len, uy = dy / len;

                    double phasePx = _dashPhase[e.BranchIndex];
                    double start = ((phasePx % period) + period) % period - period;

                    for (double t = start; t < len; t += period)
                    {
                        double s0 = Math.Max(0, t);
                        double s1 = Math.Min(len, t + dash);
                        if (s1 <= s0) continue;
                        g.BeginFigure(new Point(pa.X + ux * s0, pa.Y + uy * s0), false);
                        g.LineTo(new Point(pa.X + ux * s1, pa.Y + uy * s1));
                        g.EndFigure(false);
                        any = true;
                    }
                }
            }
            if (any) dc.DrawGeometry(null, flowPens[bkt], flow);
        }
    }

    private void DrawLabels(DrawingContext dc)
    {
        if (_labelNodes.Count == 0) return;
        _labelDrawing ??= BuildLabelDrawing();
        foreach ((FormattedText text, Point origin) in _labelDrawing) dc.DrawText(text, origin);
    }

    /// <summary>
    /// Lay out the label layer once per label list (WPF R133): the same texts, the same font and size, the same origins
    /// and the same budget as FastSchematic.BuildLabelDrawing.
    /// </summary>
    private List<(FormattedText, Point)> BuildLabelDrawing()
    {
        const double fontSize = 11;
        var list = new List<(FormattedText, Point)>();
        SchematicView sv = View;
        int drawn = 0;
        foreach ((int nodeIndex, Point screen) in _labelNodes)
        {
            if (drawn++ >= LabelBudget) break;
            SchematicNode nd = Nodes[nodeIndex];
            string text = (nd.Name ?? "").Replace(" 400kV", "", StringComparison.Ordinal)
                                         .Replace(" 275kV", "", StringComparison.Ordinal).ToUpperInvariant();
            if (text.Length == 0) continue;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MapPaint.LabelFace, fontSize, MapPaint.LabelBrush);
            double r = sv.RoundelRadius(nd.DisplayKv);
            list.Add((ft, new Point(screen.X + r + 4, screen.Y - ft.Height / 2)));
        }
        return list;
    }

    // ---- the coastline, the geography layers ----

    /// <summary>Draw the simplified GB coastline as a faint filled outline behind the network.</summary>
    private void DrawCoast(DrawingContext dc)
    {
        IReadOnlyList<WorldPoint> w = CoastlineGb.World;
        if (w.Count < 2) return;
        // Baked once in world coordinates and drawn through the world-to-screen transform with a pen whose world width
        // renders at the WPF 1.0 px on screen (FastSchematic builds the same outline in screen coordinates each frame).
        _coastWorldGeo ??= BakeOutline(w);
        double zoom = _view.Zoom;
        using (dc.PushTransform(new Matrix(zoom, 0, 0, zoom, _view.OffsetX, _view.OffsetY)))
        {
            var pen = new ImmutablePen(MapPaint.CoastPenBrush, MapPaint.CoastPen.Thickness / zoom, null, PenLineCap.Round, PenLineJoin.Round);
            dc.DrawGeometry(MapPaint.CoastFill, pen, _coastWorldGeo);
        }
    }

    private static StreamGeometry BakeOutline(IReadOnlyList<WorldPoint> w)
    {
        var g = new StreamGeometry();
        using StreamGeometryContext c = g.Open();
        c.BeginFigure(new Point(w[0].X, w[0].Y), true);
        for (int i = 1; i < w.Count; i++) c.LineTo(new Point(w[i].X, w[i].Y));
        c.EndFigure(true);
        return g;
    }

    /// <summary>
    /// Feed the real DNO/GSP geography and geocoded assets. The boundary rings are baked once into world-coordinate
    /// geometry, drawn through the world-to-screen transform each frame. A null clears the overlay.
    /// </summary>
    public void SetGeography(GeographyData? geo)
    {
        _geo = geo;
        _assetFills = geo is null ? Array.Empty<ImmutableSolidColorBrush>() : geo.Assets.Select(a => MapPaint.Frozen(a.Fill)).ToArray();
        _dnoWorldGeo = geo is null ? null : BakeRings(geo.Dno);
        _gspWorldGeo = geo is null ? null : BakeRings(geo.Gsp);
        InvalidateVisual();
    }

    private static StreamGeometry? BakeRings(IReadOnlyList<GeographyData.Region> regions)
    {
        if (regions.Count == 0) return null;
        var g = new StreamGeometry();
        using StreamGeometryContext c = g.Open();
        foreach (GeographyData.Region r in regions)
            foreach (WorldPoint[] ring in r.Rings)
            {
                if (ring.Length < 2) continue;
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), false);   // not filled, closed
                for (int i = 1; i < ring.Length; i++) c.LineTo(new Point(ring[i].X, ring[i].Y));
                c.EndFigure(true);
            }
        return g;
    }

    /// <summary>Stroke a baked world-space outline at a constant screen width (FastSchematic.DrawRegions).</summary>
    private void DrawRegions(DrawingContext dc, StreamGeometry geo, IImmutableBrush brush, double screenPx)
    {
        double zoom = _view.Zoom;
        using (dc.PushTransform(new Matrix(zoom, 0, 0, zoom, _view.OffsetX, _view.OffsetY)))   // == WorldToScreen
        {
            var pen = new ImmutablePen(brush, screenPx / zoom, null, PenLineCap.Flat, PenLineJoin.Round);
            dc.DrawGeometry(null, pen, geo);
        }
    }

    /// <summary>Plot the geocoded assets as fuel-coloured diamonds, batched by brush and viewport-culled.</summary>
    private void DrawAssets(DrawingContext dc)
    {
        if (_geo is null || _geo.Assets.Count == 0) return;
        var view = new Rect(-12, -12, Bounds.Width + 24, Bounds.Height + 24);
        double maxMw = _geo.MaxAssetMw;

        var byBrush = new Dictionary<ImmutableSolidColorBrush, (StreamGeometry G, StreamGeometryContext C)>(ReferenceEqualityComparer.Instance);
        for (int ai = 0; ai < _geo.Assets.Count; ai++)
        {
            GeographyData.AssetMarker a = _geo.Assets[ai];
            Point s = WorldToScreen(a.World.X, a.World.Y);
            if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;

            double rel = maxMw > 0 ? Math.Sqrt(Math.Max(0, a.Mw) / maxMw) : 0;   // 0..1
            double h = 2.0 + 5.0 * rel;                                          // half-extent, px
            DiamondFigure(Batch(byBrush, _assetFills[ai]), s, h);
        }
        foreach ((ImmutableSolidColorBrush fill, (StreamGeometry g, StreamGeometryContext c)) in byBrush)
        {
            c.Dispose();
            dc.DrawGeometry(fill, MapPaint.AssetOutlinePen, g);
        }
    }

    /// <summary>A compact top-right key for whichever geography layers are on (FastSchematic.DrawGeoLegend).</summary>
    private void DrawGeoLegend(DrawingContext dc)
    {
        if (_geo is null) return;
        bool anyAssets = _showAssets && _geo.Assets.Count > 0;
        if (!_showDnoAreas && !_showGspRegions && !anyAssets) return;

        var rows = new List<(Color Col, bool Line, string Text)>();
        if (_showDnoAreas) rows.Add((MapPaint.DnoColor, true, "DNO licence area"));
        if (_showGspRegions) rows.Add((MapPaint.GspColor, true, "GSP region"));
        if (anyAssets)
            foreach ((string tech, Color col) in AssetLegendKeys())
                rows.Add((col, false, tech));
        if (rows.Count == 0) return;

        const double sw = 14, pad = 9, rowH = 16;
        var texts = rows.Select(r => new FormattedText(r.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MapPaint.LabelFace, 11, MapPaint.LabelBrush)).ToList();
        double textW = texts.Max(t => t.Width);
        double panelW = pad + sw + 7 + textW + pad;
        double panelH = pad + rows.Count * rowH + (pad - 3);
        double left = Bounds.Width - panelW - 12, top = 12;
        if (left < 4 || panelH > Bounds.Height - 24) return;

        dc.DrawRectangle(MapPaint.GeoLegendBg, MapPaint.GeoLegendBorder, new Rect(left, top, panelW, panelH), 5, 5);

        double y = top + pad;
        for (int i = 0; i < rows.Count; i++)
        {
            (Color col, bool line, _) = rows[i];
            double cy = y + rowH / 2 - 1;
            double sx = left + pad;
            if (line)
            {
                var pen = new ImmutablePen(MapPaint.Frozen(col), 2.4, null, PenLineCap.Round);
                dc.DrawLine(pen, new Point(sx, cy), new Point(sx + sw, cy));
            }
            else
            {
                var dia = new StreamGeometry();
                using (StreamGeometryContext c = dia.Open()) DiamondFigure(c, new Point(sx + sw / 2, cy), 5);
                dc.DrawGeometry(MapPaint.Frozen(col), MapPaint.AssetOutlinePen, dia);
            }
            dc.DrawText(texts[i], new Point(sx + sw + 7, y + (rowH - texts[i].Height) / 2));
            y += rowH;
        }
    }

    /// <summary>The most common fuel classes in the asset set (at most nine), for the legend key.</summary>
    private IEnumerable<(string Tech, Color Col)> AssetLegendKeys()
    {
        if (_geo is null) return Array.Empty<(string, Color)>();
        var count = new Dictionary<string, int>();
        var col = new Dictionary<string, Color>();
        foreach (GeographyData.AssetMarker a in _geo.Assets)
        {
            UiFuelInfo fi = UiFuels.Classify(a.Tech);
            count[fi.Name] = count.GetValueOrDefault(fi.Name) + 1;
            col[fi.Name] = MapPaint.ToColor(fi.Colour);
        }
        return count.OrderByDescending(kv => kv.Value).Take(9).Select(kv => (kv.Key, col[kv.Key]));
    }

    /// <summary>
    /// Highlight each ETYS boundary's cut branches (coloured by transfer loading), add a perpendicular tick at each
    /// branch midpoint, and label the cut with flow / limit / % (FastSchematic.DrawBoundaries).
    /// </summary>
    private void DrawBoundaries(DrawingContext dc)
    {
        if (_boundaries.Count == 0 || Nodes.Count == 0) return;
        SchematicView sv = View;

        foreach (BoundaryRec b in _boundaries)
        {
            Color col = MapPaint.ToColor(ColourRamps.Loading(b.LoadingPct / 100.0));
            var hiPen = new ImmutablePen(MapPaint.Frozen(Color.FromArgb(150, col.R, col.G, col.B)), 7.0, null, PenLineCap.Round);
            var tickPen = new ImmutablePen(MapPaint.Frozen(col), 2.2);

            double cx = 0, cy = 0;
            int k = 0;
            foreach (int ei in b.EdgeIdx)
            {
                if (ei < 0 || ei >= Edges.Count) continue;
                SchematicEdge e = Edges[ei];
                if (!e.Valid) continue;
                Point pa = NodeScreen(sv, e.A), pb = NodeScreen(sv, e.B);
                dc.DrawLine(hiPen, pa, pb);

                var mid = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
                double ddx = pb.X - pa.X, ddy = pb.Y - pa.Y;
                double len = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (len > 1e-3)
                {
                    double px = -ddy / len, py = ddx / len;
                    dc.DrawLine(tickPen, new Point(mid.X - px * 11, mid.Y - py * 11), new Point(mid.X + px * 11, mid.Y + py * 11));
                }
                cx += mid.X;
                cy += mid.Y;
                k++;
            }
            if (k == 0) continue;
            var c = new Point(cx / k, cy / k);

            string sub = b.LimitMw >= 1000
                ? string.Create(CultureInfo.InvariantCulture, $"{b.FlowMw / 1000:0.0}/{b.LimitMw / 1000:0.0} GW  {b.LoadingPct:0}%")
                : string.Create(CultureInfo.InvariantCulture, $"{b.FlowMw:0}/{b.LimitMw:0} MW  {b.LoadingPct:0}%");
            var ftHead = new FormattedText(b.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MapPaint.BoundaryFace, 11.5, MapPaint.LabelBrush);
            var ftSub = new FormattedText(sub, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MapPaint.BoundaryFace, 11.5, MapPaint.Frozen(col));
            double bw = Math.Max(ftHead.Width, ftSub.Width) + 12;
            double bh = ftHead.Height + ftSub.Height + 8;
            var bg = new Rect(c.X - bw / 2, c.Y - bh / 2, bw, bh);
            dc.DrawRectangle(MapPaint.BoundaryLabelBg, null, bg, 4, 4);
            dc.DrawText(ftHead, new Point(bg.Left + 6, bg.Top + 4));
            dc.DrawText(ftSub, new Point(bg.Left + 6, bg.Top + 4 + ftHead.Height));
        }
    }

    /// <summary>The generation fleet as fuel-coloured diamonds sized by capacity, batched by brush, viewport-culled.</summary>
    private void DrawFleet(DrawingContext dc)
    {
        if (_fleet.Count == 0) return;
        var view = new Rect(-16, -16, Bounds.Width + 32, Bounds.Height + 32);
        var byBrush = new Dictionary<ImmutableSolidColorBrush, (StreamGeometry G, StreamGeometryContext C)>(ReferenceEqualityComparer.Instance);
        foreach (FleetMarker m in _fleet)
        {
            Point s = WorldToScreen(m.Wx, m.Wy);
            if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;
            double rel = Math.Sqrt(m.Mw / _maxFleetMw);     // 0..1
            double h = 3.0 + 8.0 * rel;                      // diamond half-extent, px
            DiamondFigure(Batch(byBrush, m.Fill), s, h);
        }
        foreach ((ImmutableSolidColorBrush fill, (StreamGeometry g, StreamGeometryContext c)) in byBrush)
        {
            c.Dispose();
            dc.DrawGeometry(fill, MapPaint.FleetOutlinePen, g);
        }
    }

    // The open geometry for one brush, made on first use (one geometry per fuel colour).
    private static StreamGeometryContext Batch(
        Dictionary<ImmutableSolidColorBrush, (StreamGeometry G, StreamGeometryContext C)> byBrush, ImmutableSolidColorBrush fill)
    {
        if (!byBrush.TryGetValue(fill, out (StreamGeometry G, StreamGeometryContext C) gc))
        {
            var g = new StreamGeometry();
            gc = (g, g.Open());
            byBrush[fill] = gc;
        }
        return gc.C;
    }

    /// <summary>
    /// The stacked-asset affordance: a bounded halo and an "N assets" badge round a fanned-open cluster, or a small
    /// count badge on a significant collapsed pile (FastSchematic.DrawFanClusters).
    /// </summary>
    private void DrawFanClusters(DrawingContext dc)
    {
        if (_view.FanClusters.Count == 0) return;
        SchematicView sv = View;
        double minKv = sv.LodMinBaseKv();
        double t = sv.FanT();
        var view = new Rect(-40, -40, Bounds.Width + 80, Bounds.Height + 80);

        foreach ((double wx, double wy, int count, double fullR, double maxKv) in _view.FanClusters)
        {
            if (maxKv < minKv) continue;                       // whole cluster below the LOD cut
            Point c = WorldToScreen(wx, wy);
            if (c.X < view.Left || c.X > view.Right || c.Y < view.Top || c.Y > view.Bottom) continue;

            if (t > 0.05)
            {
                double haloR = t * fullR + sv.RoundelRadius(maxKv) + 5;
                dc.DrawEllipse(MapPaint.FanHaloFill, MapPaint.FanHaloPen, c, haloR, haloR);
                DrawFanBadge(dc, new Point(c.X, c.Y - haloR - 2), string.Create(CultureInfo.InvariantCulture, $"{count} assets"), centreAbove: true);
            }
            else if (count >= 8)
            {
                double rr = sv.RoundelRadius(maxKv);
                DrawFanBadge(dc, new Point(c.X + rr * 0.7, c.Y - rr - 1), string.Create(CultureInfo.InvariantCulture, $"x{count}"), centreAbove: false);
            }
        }
    }

    private static void DrawFanBadge(DrawingContext dc, Point anchor, string text, bool centreAbove)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MapPaint.LabelFace, 10.5, MapPaint.LabelBrush);
        double w = ft.Width + 8, h = ft.Height + 3;
        double left = centreAbove ? anchor.X - w / 2 : anchor.X;
        double top = centreAbove ? anchor.Y - h : anchor.Y - h / 2;
        dc.DrawRectangle(MapPaint.FanBadgeBg, null, new Rect(left, top, w, h), 3, 3);
        dc.DrawText(ft, new Point(left + 4, top + 1.5));
    }

    /// <summary>
    /// The overview map in the bottom-left corner: EHV nodes as dots over a translucent panel, with an amber rectangle
    /// for the part of the world on screen. A click inside it recentres the main view (FastSchematic.DrawMinimap).
    /// </summary>
    private void DrawMinimap(DrawingContext dc)
    {
        double ww = _view.WorldWidth, wh = _view.WorldHeight, wl = _view.WorldLeft, wt = _view.WorldTop;
        if (Nodes.Count == 0 || ww <= 0 || wh <= 0) return;

        const double maxW = 156, maxH = 200;
        double scale = Math.Min(maxW / ww, maxH / wh);
        double mmW = ww * scale, mmH = wh * scale;
        if (mmW < 24 || mmH < 24 || Bounds.Height < mmH + 24) return;

        double left = 12, top = Bounds.Height - mmH - 12;
        _mmRect = new Rect(left, top, mmW, mmH);
        _mmScale = scale;

        dc.DrawRectangle(MapPaint.MinimapBg, MapPaint.MinimapBorder, _mmRect);
        using (dc.PushClip(_mmRect))
        {
            // Rebuild the dot geometry only when the minimap position or scale changes.
            if (_minimapDots is null || scale != _minimapDotsScale || left != _minimapDotsLeft || top != _minimapDotsTop)
            {
                _minimapDotsScale = scale;
                _minimapDotsLeft = left;
                _minimapDotsTop = top;
                var dots = new StreamGeometry();
                using (StreamGeometryContext g = dots.Open())
                {
                    foreach (SchematicNode nd in Nodes)
                    {
                        if (!_view.ShowAllNodes && nd.DisplayKv < EhvDisplayKv) continue;
                        double mx = left + (nd.Wx - wl) * scale;
                        double my = top + (nd.Wy - wt) * scale;
                        g.BeginFigure(new Point(mx - 0.7, my - 0.7), true);
                        g.LineTo(new Point(mx + 0.7, my - 0.7));
                        g.LineTo(new Point(mx + 0.7, my + 0.7));
                        g.LineTo(new Point(mx - 0.7, my + 0.7));
                        g.EndFigure(true);
                    }
                }
                _minimapDots = dots;
            }
            dc.DrawGeometry(MapPaint.MinimapDot, null, _minimapDots);

            // The GB coastline in the minimap too, when enabled.
            if (_showCoast)
            {
                IReadOnlyList<WorldPoint> w = CoastlineGb.World;
                var cg = new StreamGeometry();
                using (StreamGeometryContext cc = cg.Open())
                {
                    cc.BeginFigure(new Point(left + (w[0].X - wl) * scale, top + (w[0].Y - wt) * scale), false);
                    for (int i = 1; i < w.Count; i++)
                        cc.LineTo(new Point(left + (w[i].X - wl) * scale, top + (w[i].Y - wt) * scale));
                    cc.EndFigure(true);
                }
                dc.DrawGeometry(null, MapPaint.CoastPen, cg);
            }

            // The viewport rectangle (the world region currently on screen).
            ScreenPoint w0 = _view.ScreenToWorld(new ScreenPoint(0, 0));
            ScreenPoint w1 = _view.ScreenToWorld(new ScreenPoint(Bounds.Width, Bounds.Height));
            var vr = new Rect(
                new Point(left + (w0.X - wl) * scale, top + (w0.Y - wt) * scale),
                new Point(left + (w1.X - wl) * scale, top + (w1.Y - wt) * scale));
            dc.DrawRectangle(null, MapPaint.MinimapViewPen, vr);
        }
    }

    /// <summary>The minimap panel on screen as last drawn (empty before the first draw or when it is hidden).</summary>
    public Rect MinimapRect => _showMinimap ? _mmRect : default;

    private void RecenterFromMinimap(Point p)
    {
        double wx = _view.WorldLeft + (p.X - _mmRect.Left) / _mmScale;
        double wy = _view.WorldTop + (p.Y - _mmRect.Top) / _mmScale;
        _view.OffsetX = Bounds.Width / 2 - wx * _view.Zoom;
        _view.OffsetY = Bounds.Height / 2 - wy * _view.Zoom;
        _staticDirty = true;
        InvalidateVisual();
    }

    // ============================ interaction (FastSchematic's mouse handling) ============================

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (Nodes.Count == 0 || e.Delta.Y == 0) return;
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? WheelZoomFactor : 1 / WheelZoomFactor);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        PointerPoint pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;   // the right button acts on release (the context menu)
        Focus();

        // A click inside the minimap recentres the main view (and is not a node click or a pan).
        Point down = pt.Position;
        if (_showMinimap && _mmRect.Contains(down)) { RecenterFromMinimap(down); e.Handled = true; return; }

        // UI programme Phase 4.2: in AUTHOR mode a press on a bus starts a drag of that bus, not a pan. It comes before
        // the double-click drill-in, so a second quick press while Connect waits for its second bus (then a drag of it)
        // still picks the bus up. Run mode never comes here.
        if (_editMode && HitTestNode(down) is int pressedBus && _view.IdToIndex.TryGetValue(pressedBus, out int pressedIndex))
        {
            // 4.2 follow-up (2026-10-24): with no Connect waiting, the second press of a double-click still picks the bus
            // up (a click then a quick drag stays a drag), but released without a move it drills in onto the bus exactly
            // as a Run-mode double-click does (the first click has already selected it).
            _busDragDrillIn = e.ClickCount == 2 && _editConnectFrom is null;

            _busDragId = pressedBus;
            _busDragIndex = pressedIndex;
            _busDragOriginWx = Nodes[pressedIndex].Wx;
            _busDragOriginWy = Nodes[pressedIndex].Wy;
            _movedWhileDown = false;
            _dragStartScreen = down;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // A double-click drills in: onto a substation (centre and zoom to substation level), or toward the cursor.
        if (e.ClickCount == 2)
        {
            int? hit = HitTestNode(down);
            if (hit is int hb) FocusNode(hb);
            else ZoomAt(down, DoubleClickZoomFactor);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _movedWhileDown = false;
        _dragStartScreen = down;
        _dragStartOffsetX = _view.OffsetX;
        _dragStartOffsetY = _view.OffsetY;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        // UI programme Phase 4.2 (AUTHOR mode only): the Connect rubber band follows the pointer, and a dragged bus
        // follows it once the pointer has moved further than the click slop.
        if (_editMode && _editConnectFrom is not null)
        {
            _pointerAt = e.GetPosition(this);
            InvalidateVisual();
        }
        if (_busDragIndex >= 0)
        {
            Point at = e.GetPosition(this);
            double bx = at.X - _dragStartScreen.X, by = at.Y - _dragStartScreen.Y;
            if (Math.Sqrt(bx * bx + by * by) > ClickSlopPx) _movedWhileDown = true;
            if (_movedWhileDown) MoveDraggedNode(at);
            return;
        }
        if (!_dragging) return;
        Point p = e.GetPosition(this);
        double dx = p.X - _dragStartScreen.X, dy = p.Y - _dragStartScreen.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > ClickSlopPx) _movedWhileDown = true;
        _view.OffsetX = _dragStartOffsetX + dx;
        _view.OffsetY = _dragStartOffsetY + dy;
        // Not marked dirty: the render draws the cached layer through a translation while the drag lasts.
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        Point p = e.GetPosition(this);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            RightClicked?.Invoke(p);
            e.Handled = true;
            return;
        }
        // UI programme Phase 4.2 (AUTHOR mode only): the end of a press on a bus. A drag puts the node back where the
        // case has it (the edit, if accepted, reloads the map with the new position) and reports where it was dropped;
        // a press without a move is a click on that bus.
        if (e.InitialPressMouseButton == MouseButton.Left && _busDragIndex >= 0)
        {
            int busId = _busDragId;
            bool moved = _movedWhileDown;
            bool drillIn = _busDragDrillIn;
            _busDragDrillIn = false;
            EndBusDrag(restore: true);
            e.Pointer.Capture(null);
            if (moved) BusDragged?.Invoke(busId, p);
            else if (drillIn) FocusNode(busId);
            else EditClicked?.Invoke(p, busId, null);
            return;
        }
        if (e.InitialPressMouseButton != MouseButton.Left || !_dragging) return;
        EndDrag();
        e.Pointer.Capture(null);

        if (_movedWhileDown) return;   // it was a pan, not a click

        // UI programme Phase 4.2: in AUTHOR mode a click goes to the map editor as one event (where, which bus, which
        // branch: the editor's tool decides what it means); Run mode is unchanged below.
        if (_editMode)
        {
            int? onBus = HitTestNode(p);
            int? onBranch = onBus is null ? HitTestBranch(p, cycle: true) : null;
            EditClicked?.Invoke(p, onBus, onBranch);
            return;
        }

        int? node = HitTestNode(p);
        if (node is int id) { NodeClicked?.Invoke(id); return; }
        // Phase 5d: with the line route layer shown, a click on a route polyline selects its record ("Show in Assets").
        if (_showLineRoutes && LineRouteClicked is not null && _lineRoutes.HitTest(View, new ScreenPoint(p.X, p.Y)) is LineRoutePolyline hitRoute)
        {
            LineRouteClicked.Invoke(hitRoute.RouteId);
            return;
        }
        if (BranchClicked is not null)
        {
            int? br = HitTestBranch(p, cycle: true);   // repeated clicks step through coincident parallel circuits
            if (br is int bi) BranchClicked.Invoke(bi);
        }
    }

    /// <summary>
    /// Capture can be lost without a button-up reaching this control (Alt+Tab, a dialog). Ending the pan here stops
    /// later button-less moves from panning the map (as FastSchematic.OnLostMouseCapture).
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
        EndBusDrag(restore: true);   // Phase 4.2: a bus drag cut short changes nothing
    }

    // The drag is over: the next render rebuilds the cached layer exactly at the final offset (the WPF map at rest).
    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_viewInitialised) FitToView();
        else _staticDirty = true;   // the viewport size changed: re-cull
        InvalidateVisual();
    }

    // ============================ helpers ============================

    private Point WorldToScreen(double wx, double wy)
    {
        ScreenPoint s = _view.WorldToScreen(wx, wy);
        return new Point(s.X, s.Y);
    }

    private static Point NodeScreen(SchematicView sv, int ni)
    {
        ScreenPoint s = sv.NodeScreen(ni);
        return new Point(s.X, s.Y);
    }

    // A circle as two arc segments (cheap, smooth enough at these sizes), as FastSchematic.EllipseFigure.
    private static void EllipseFigure(StreamGeometryContext ctx, Point c, double r)
    {
        ctx.BeginFigure(new Point(c.X - r, c.Y), true);
        ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
    }

    // A filled, closed diamond of half-extent h about s (the fleet, asset and legend markers).
    private static void DiamondFigure(StreamGeometryContext c, Point s, double h)
    {
        c.BeginFigure(new Point(s.X, s.Y - h), true);
        c.LineTo(new Point(s.X + h, s.Y));
        c.LineTo(new Point(s.X, s.Y + h));
        c.LineTo(new Point(s.X - h, s.Y));
        c.EndFigure(true);
    }

    private static bool SegmentIntersectsRect(Point a, Point b, Rect r)
    {
        // Quick accept: either endpoint inside.
        if (Contains(r, a) || Contains(r, b)) return true;
        // Reject if both endpoints share an outside half-plane.
        if ((a.X < r.Left && b.X < r.Left) || (a.X > r.Right && b.X > r.Right) ||
            (a.Y < r.Top && b.Y < r.Top) || (a.Y > r.Bottom && b.Y > r.Bottom)) return false;
        // Otherwise it may cross: treat as visible (cheap, conservative).
        return true;
    }

    // The WPF Rect.Contains rule (edges inclusive).
    private static bool Contains(Rect r, Point p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

    /// <summary>A stable, well-spread colour for a supply area, keyed by its seed node index.</summary>
    private static Color GroupColor(int seed)
    {
        double hue = (seed * 0.6180339887498949) % 1.0;   // golden-ratio hue spread
        return HsvToRgb(hue, 0.55, 0.92);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double r, g, b;
        int i = (int)(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ============================ records ============================

    private struct RingRec
    {
        public Point Screen { get; set; }
        public double Radius { get; set; }
        public int NodeIndex { get; set; }
        public bool Ehv { get; set; }
    }

    private struct BoundaryRec
    {
        public string Name { get; set; }
        public int[] EdgeIdx { get; set; }     // edges (== branch indices) forming the cut
        public double LoadingPct { get; set; } // live transfer loading
        public double FlowMw { get; set; }
        public double LimitMw { get; set; }
    }

    private readonly record struct FleetMarker(double Wx, double Wy, double Mw, ImmutableSolidColorBrush Fill);
}
