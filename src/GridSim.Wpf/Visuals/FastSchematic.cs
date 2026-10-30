using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Geography;
using GridSim.Ui.Schematic;
using EdgeRec = GridSim.Ui.Schematic.SchematicEdge;
using NodeRec = GridSim.Ui.Schematic.SchematicNode;

namespace GridSim.Wpf.Visuals;

/// <summary>
/// A high-performance, self-contained schematic renderer for transmission networks.
///
/// Unlike a one-<c>Shape</c>-per-element renderer (fine for a few dozen branches, hopeless at thousands; the old
/// SchematicRenderer, removed in 1.95.055 as dead code, R101), this is a single <see cref="FrameworkElement"/>
/// that draws everything in one <see cref="OnRender"/> pass with batched
/// <see cref="StreamGeometry"/> draws. Geometry is built once per zoom/pan/state change
/// (not per frame), corridors are batched by colour bucket, and only the marching-dash
/// "flow" overlay is rebuilt each animation frame  -  for the visible, in-LOD subset only.
///
/// It keeps the visual language of the reference renderer (Underground-style roundels and
/// tube-coloured corridors with marching-dash power flow) but adds built-in zoom/pan,
/// level-of-detail, viewport culling and hit-testing so it stays smooth at ~6,000 buses /
/// ~8,000 branches. Background is transparent  -  the host supplies it.
/// </summary>
public sealed class FastSchematic : FrameworkElement
{
    // ---- the platform-neutral map model ----
    // UI programme Phase 0.2: the topology (in WORLD coordinates), the live voltage and loading with their colour bins,
    // the GDA overlays, the view transform (zoom and pan), the projection with the stacked-asset fan-out, the level of
    // detail and the hit tests moved to GridSim.Ui.Schematic.SchematicView (src/GridSim.Ui/Schematic), operation for
    // operation, so the planned Avalonia map shares them. This control keeps the drawing. The fields the drawing code
    // reads are forwarding properties onto the model, under their old names, so that code reads exactly as before and
    // every pixel is unchanged. World space: x = normalised X (0..1), y = (1 - normalised Y) so north is up and
    // y grows downward like screen space; screen = world * _zoom + _offset.
    private readonly SchematicView _view = new();

    /// <summary>The model, with its viewport set to this control's current size (every size-dependent call goes
    /// through here, so the model sees exactly the ActualWidth / ActualHeight the old code read).</summary>
    private SchematicView View
    {
        get
        {
            _view.ViewportWidth = ActualWidth;
            _view.ViewportHeight = ActualHeight;
            return _view;
        }
    }

    private List<NodeRec> _nodes => _view.Nodes;
    private Dictionary<int, int> _idToIndex => _view.IdToIndex;   // bus Id -> _nodes index
    private List<EdgeRec> _edges => _view.Edges;
    private Rect _worldBounds => new(_view.WorldLeft, _view.WorldTop, _view.WorldWidth, _view.WorldHeight);
    private bool _showAllNodes => _view.ShowAllNodes;

    // ---- live state ----
    private double[]? _vmByNode => _view.VmByNode;   // per node, index-aligned with _nodes
    private bool _hasState => _view.HasState;

    // ---- GDA-bridge overlays (condition-by-bus, stress-by-branch) ----
    private double[]? _conditionByNode => _view.ConditionByNode;   // per node (index-aligned with _nodes), 0 healthy .. 1 critical
    private bool _hasOverlay => _view.HasOverlay;

    // ---- view transform (world -> screen): screen = world * _zoom + _offset ----
    private double _zoom { get => _view.Zoom; set => _view.Zoom = value; }
    private Vector _offset   // screen-space translation, in DIPs
    {
        get => new(_view.OffsetX, _view.OffsetY);
        set { _view.OffsetX = value.X; _view.OffsetY = value.Y; }
    }
    private bool _viewInitialised;

    // ---- panning ----
    private bool _dragging;
    private Point _dragStartScreen;
    private Vector _dragStartOffset;
    private bool _movedWhileDown;

    // ---- retained geometry, rebuilt only when the view or state changes ----
    private bool _staticDirty = true;     // corridors + roundels need rebuild
    private StreamGeometry? _txGeometry;             // transformer ties (thin grey)
    private readonly StreamGeometry?[] _corridorByBucket = new StreamGeometry?[LoadingBuckets];
    private StreamGeometry? _ringFillGeometry;       // roundel fills (rings are stroked per-voltage in DrawRings)
    private readonly List<RingRec> _visibleRings = new();   // for per-voltage ring colour + labels
    private readonly List<(int nodeIndex, Point screen)> _labelNodes = new();

    // ---- cached label layer (R133, October 2026) ----
    // DrawLabels used to build a new FormattedText (a full text-layout pass) for up to LabelBudget labels on EVERY
    // OnRender, and OnRender runs at the ~15 fps animation cadence while playing - about 3,300 layouts a second for
    // labels whose text and positions only change when RebuildStatic re-lays the view (pan, zoom, state, mode) or the
    // model is reloaded. The same draw calls are now recorded once into a frozen DrawingGroup and replayed each frame.
    // Null means "rebuild on the next render"; RebuildStatic and Load clear it, and a DPI change rebuilds it.
    private DrawingGroup? _labelDrawing;
    private double _labelDrawingPps;

    // ---- named renderer constants (R213: were literals; values unchanged). Several mirror map-interop.js, which
    // carries the same values for the web map. ----

    // UI programme 0.2: UnratedBranchNominalMw (the assumed MW of an unrated branch), NodePickRadiusPx and
    // BranchPickTolerancePx (the pick tolerances) moved with the state and hit-test logic to SchematicView.

    /// <summary>Flow (MW) that maps to one unit of dash speed before clamping.</summary>
    private const double DashMwPerUnit = 120.0;
    /// <summary>Slowest dash speed (units) so a lightly loaded corridor still visibly moves.</summary>
    private const double DashSpeedMin = 0.4;
    /// <summary>Fastest dash speed (units) so a heavy corridor never aliases into a frozen strobe.</summary>
    private const double DashSpeedMax = 8.0;
    /// <summary>Screen pixels per second for one unit of dash speed.</summary>
    private const double DashPxScale = 9.0;
    /// <summary>Animation step (s) assumed when the caller's dt is missing or implausible (one 60 Hz frame).</summary>
    private const double AnimDefaultDtS = 0.016;
    /// <summary>Longest animation step (s) accepted; a longer gap (window hidden, debugger) is treated as one frame.</summary>
    private const double AnimMaxDtS = 0.25;
    /// <summary>Most node labels drawn at once, so a dense deeply-zoomed view never causes a text-layout storm.
    /// map-interop.js uses the same budget.</summary>
    public const int LabelBudget = 220;
    /// <summary>Base kV at or above which a node counts as EHV (transmission) for the minimap and ring styling.</summary>
    public const double EhvDisplayKv = 200.0;

    // ---- co-located "stacked asset" fan-out (tube-map style): laid out by SchematicView (one entry per stacked site) ----
    private List<(double Wx, double Wy, int Count, double FullR, double MaxKv)> _fanClusters => _view.FanClusters;

    // ---- animation ----
    private double[] _dashPhase = Array.Empty<double>();    // per edge, world-distance phase
    // Pre-bucketed edge indices so DrawFlow touches only each bucket's edges (not all x 12); kept by SchematicView.
    private List<int>?[] _edgesByBucket => _view.EdgesByBucket;
    // Flow overlay geometry is built fresh + frozen per loading bucket in DrawFlow.

    // ---- frozen pens/brushes, built once ----
    private static readonly Brush DarkFill = Palette.Frozen(Palette.FromHex("#0E141B"));
    private static readonly Pen TxPen = MakePen(Palette.FromHex("#3A4756"), 1.4, 0.6);
    private static readonly Brush LabelBrush = Palette.Frozen(Palette.FromHex("#C7D2DD"));
    private static readonly Typeface LabelFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Loading colour is bucketed so corridors batch into a handful of geometries instead
    // of one pen per branch. 12 buckets across 0..1.2 loading is visually smooth enough.
    private const int LoadingBuckets = SchematicView.LoadingBuckets;
    private static readonly Pen[] CorridorPens = BuildCorridorPens();
    private static readonly Pen[] FlowPens = BuildFlowPens();

    private static readonly Pen RingPenEhv = MakePen(Palette.Voltage(1.0), 2.6, 1.0);
    private static readonly Pen RingPenLv = MakePen(Palette.Voltage(1.0), 1.8, 1.0);

    // ============================ public API ============================

    /// <summary>Raised with a bus Id when a roundel is left-clicked (without dragging).</summary>
    public event Action<int>? NodeClicked;

    /// <summary>Raised with a branch index (into the model's Branches) when a corridor is clicked.</summary>
    public event Action<int>? BranchClicked;

    /// <summary>
    /// How the map is coloured: by circuit loading, as a voltage heatmap, by per-bus asset
    /// <see cref="MapColourMode.Condition"/> (fragility), or by per-branch <see cref="MapColourMode.Stress"/>.
    /// The last two are driven by the GDA-bridge overlays fed through <see cref="SetOverlay"/>.
    /// UI programme Phase 0.2: the mode is the platform-neutral <see cref="MapColourMode"/> (it was this class's nested
    /// enum <c>ColorModeKind</c>, with the same four members in the same order), so the dashboard legend and the planned
    /// Avalonia map name the same modes.
    /// </summary>
    private MapColourMode _colorMode = MapColourMode.Loading;
    public MapColourMode ColorMode
    {
        get => _colorMode;
        set { if (_colorMode != value) { _colorMode = value; _staticDirty = true; InvalidateVisual(); } }
    }

    /// <summary>When true, draw the generation fleet as fuel-coloured, capacity-sized diamonds.</summary>
    private bool _showFleet;
    public bool ShowFleet
    {
        get => _showFleet;
        set { if (_showFleet != value) { _showFleet = value; InvalidateVisual(); } }
    }

    // Generation fleet markers, in world coords (captured in Load).
    private readonly List<FleetMarker> _fleet = new();
    private double _maxFleetMw = 1;
    private static readonly Pen FleetOutlinePen = MakePen(Palette.FromHex("#0B0F14"), 0.8, 0.9);

    /// <summary>A corner overview map with a viewport rectangle; click it to recenter.</summary>
    private bool _showMinimap = true;
    public bool ShowMinimap
    {
        get => _showMinimap;
        set { if (_showMinimap != value) { _showMinimap = value; InvalidateVisual(); } }
    }
    private Rect _mmRect;        // minimap panel in screen space (for hit-testing)
    private double _mmScale = 1; // world units -> minimap pixels

    /// <summary>A faint GB coastline behind the network (only aligns with geolocated models).</summary>
    private bool _showCoast;
    public bool ShowCoast
    {
        get => _showCoast;
        set { if (_showCoast != value) { _showCoast = value; InvalidateVisual(); } }
    }
    private static readonly Brush CoastFill = Palette.Frozen(Color.FromArgb(36, 70, 92, 110));
    private static readonly Pen CoastPen = MakePen(Palette.FromHex("#33485C"), 1.0, 0.8);

    /// <summary>ETYS boundary cross-sections: highlight each cut's branches + flow/limit label.</summary>
    private bool _showBoundaries;
    public bool ShowBoundaries
    {
        get => _showBoundaries;
        set { if (_showBoundaries != value) { _showBoundaries = value; InvalidateVisual(); } }
    }
    private readonly List<BoundaryRec> _boundaries = new();
    private static readonly Brush BoundaryLabelBg = Palette.Frozen(Color.FromArgb(214, 12, 18, 26));
    private static readonly Typeface BoundaryFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>Tint substations by GSP supply-area (nearest EHV grid-supply point on the network).</summary>
    private bool _showGspAreas;
    public bool ShowGspAreas
    {
        get => _showGspAreas;
        set { if (_showGspAreas != value) { _showGspAreas = value; InvalidateVisual(); } }
    }
    private Color[] _groupColorByNode = Array.Empty<Color>();
    private bool _hasGroups;

    // ---- real DNO/GSP geography overlay (task #92) ----
    // Admin boundaries + geocoded assets in the SAME world box as the coastline, so they line up
    // with geolocated buses. Each layer is individually toggleable; off by default.
    private GeographyData? _geo;
    // UI programme Phase 0.2: the geography now lives in the platform-neutral GridSim.Ui and carries each asset's fill
    // as a neutral colour token. The matching cached frozen brushes are looked up once per SetGeography (index-aligned
    // with _geo.Assets), so DrawAssets batches by the same brush instances it used before, with no per-frame lookups.
    private Brush[] _assetFills = Array.Empty<Brush>();
    private StreamGeometry? _dnoWorldGeo;   // all DNO rings, baked once in world coords
    private StreamGeometry? _gspWorldGeo;   // all GSP rings, baked once in world coords
    // Outline colours carry their own alpha (frozen brushes can't have Opacity set at draw time).
    private static readonly Color DnoColor = Palette.FromHex("#E0B341");    // amber
    private static readonly Color GspColor = Palette.FromHex("#4FC3F7");    // cyan
    private static readonly Brush DnoPenBrush = Palette.Frozen(Color.FromArgb(217, DnoColor.R, DnoColor.G, DnoColor.B));
    private static readonly Brush GspPenBrush = Palette.Frozen(Color.FromArgb(150, GspColor.R, GspColor.G, GspColor.B));
    private static readonly Pen AssetOutlinePen = MakePen(Palette.FromHex("#0B0F14"), 0.7, 0.85);
    private static readonly Brush GeoLegendBg = Palette.Frozen(Color.FromArgb(210, 12, 18, 26));
    private static readonly Pen GeoLegendBorder = MakePen(Palette.FromHex("#33414F"), 1.0, 1.0);
    private static readonly Typeface GeoLegendFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>Draw the real GB DNO licence-area boundary outlines.</summary>
    private bool _showDnoAreas;
    public bool ShowDnoAreas
    {
        get => _showDnoAreas;
        set { if (_showDnoAreas != value) { _showDnoAreas = value; InvalidateVisual(); } }
    }

    /// <summary>Draw the real GB GSP-region boundary outlines.</summary>
    private bool _showGspRegions;
    public bool ShowGspRegions
    {
        get => _showGspRegions;
        set { if (_showGspRegions != value) { _showGspRegions = value; InvalidateVisual(); } }
    }

    /// <summary>Plot the geocoded REPD/DUKES assets as fuel-coloured markers.</summary>
    private bool _showAssets;
    public bool ShowAssets
    {
        get => _showAssets;
        set { if (_showAssets != value) { _showAssets = value; InvalidateVisual(); } }
    }

    /// <summary>True when geography GeoJSON was found locally (drives whether the toggles do anything).</summary>
    public bool HasGeography => _geo is not null;

    private static readonly Brush MinimapBg = Palette.Frozen(Color.FromArgb(196, 10, 16, 22));
    private static readonly Pen MinimapBorder = MakePen(Palette.FromHex("#33414F"), 1.0, 1.0);
    private static readonly Brush MinimapDot = Palette.Frozen(Palette.FromHex("#5A6B7B"));
    private static readonly Pen MinimapViewPen = MakePen(Palette.FromHex("#E0B341"), 1.4, 1.0);

    public FastSchematic()
    {
        ClipToBounds = true;
        Focusable = true;
        // Make the whole element hit-testable even though the background is transparent.
        Background = Brushes.Transparent;
    }

    /// <summary>A transparent backing brush so mouse events land on the whole surface.</summary>
    public Brush? Background { get; set; }

    /// <summary>Capture topology and normalised coordinates; compute world bounds. Resets state.</summary>
    public void Load(GridModel model)
    {
        _visibleRings.Clear();
        _labelNodes.Clear();
        _labelDrawing = null;   // new model: new label text (R133)
        // UI programme 0.2: the nodes, edges, world bounds, synthesised display tiers, stacked-asset fan-out and the
        // reset of the live state and overlays are SchematicView.Load's (the same steps in the same order); the fleet,
        // boundary and supply-area layers below are this control's.
        _view.Load(model);

        // ---- generation fleet markers (positioned at their bus) ----
        _fleet.Clear();
        _maxFleetMw = 1;
        foreach (Generator g in model.Generators)
        {
            if (!_idToIndex.TryGetValue(g.Bus, out int gni)) continue;
            NodeRec gnd = _nodes[gni];
            double mw = g.MBase > 0 ? g.MBase : Math.Abs(g.Pg);
            if (mw <= 0) continue;
            FuelInfo fi = Fuels.Classify(g.Type);
            _fleet.Add(new FleetMarker
            {
                Wx = gnd.Wx,
                Wy = gnd.Wy,
                Mw = mw,
                Fill = Palette.Frozen(fi.Color),
                Name = g.Name ?? gnd.Name,
                Synchronous = fi.Synchronous,
            });
            if (mw > _maxFleetMw) _maxFleetMw = mw;
        }

        // ---- ETYS boundary cuts: map each boundary's named branches to edge indices ----
        _boundaries.Clear();
        if (model.Boundaries.Count > 0)
        {
            // The same name rule as the boundary flows this highlights (Core BoundaryAnalysis: ORDINAL, case-sensitive,
            // the last branch of a name wins), so the highlighted edge is the one whose flow the boundary counts. This
            // used a case-insensitive dictionary, so "Line A" and "LINE A" (distinct to the loader) collided.
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

        _dashPhase = new double[_edges.Count];

        _minimapDots = null;
        _ringPenCache.Clear();
        _viewInitialised = false;   // re-fit on next render
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Assign every bus to a GSP supply-area: the nearest grid-supply point (an EHV node,
    /// >=275 kV) by hop count over the network, via a multi-source breadth-first search. Each
    /// supply point seeds its own area and gets a stable colour; buses inherit their area's
    /// colour. This is the "who feeds whom" regional patchwork (a stand-in for per-GSP/DNO
    /// licence areas, which aren't carried on the bus records).
    /// </summary>
    private void BuildGspGroups()
    {
        int n = _nodes.Count;
        _groupColorByNode = new Color[n];
        _hasGroups = false;
        if (n == 0) return;

        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (EdgeRec e in _edges)
            if (e.Valid) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

        var group = new int[n];
        for (int i = 0; i < n; i++) group[i] = -1;

        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (_nodes[i].DisplayKv >= 275) { group[i] = i; q.Enqueue(i); }   // each supply point seeds its area

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

    /// <summary>Push a freshly solved state: per-branch loading/flow and per-node voltage (applied by
    /// <see cref="SchematicView.ApplyState"/>, which also sets each edge's loading and voltage colour bins).</summary>
    public void SetState(PowerFlowResult result)
    {
        if (!_view.ApplyState(result)) return;   // no model loaded
        _staticDirty = true;     // corridor colour buckets and ring voltages changed
        RebuildEdgeBuckets();
        InvalidateVisual();
    }

    private void RebuildEdgeBuckets() => _view.RebuildEdgeBuckets(_colorMode);

    /// <summary>
    /// Push the GDA-bridge per-asset overlays: condition/fragility keyed by bus <c>Id</c> and stress keyed by branch
    /// name, applied and bucketed by <see cref="SchematicView.ApplyOverlay"/> for the
    /// <see cref="MapColourMode.Condition"/> / <see cref="MapColourMode.Stress"/> corridor colouring. A null map
    /// leaves that channel at its previous values.
    /// </summary>
    public void SetOverlay(IReadOnlyDictionary<int, double>? byBusId, IReadOnlyDictionary<string, double>? byBranchName)
    {
        if (!_view.ApplyOverlay(byBusId, byBranchName)) return;   // no model loaded
        _staticDirty = true;     // corridor colour buckets changed
        RebuildEdgeBuckets();
        InvalidateVisual();
    }

    /// <summary>Push live boundary transfers (matched by name) so the cut overlay shows flow/limit.</summary>
    public void SetBoundaries(IReadOnlyList<BoundaryFlow> flows)
    {
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
    /// Advance marching-dash flow phases by <paramref name="dtSeconds"/>. Speed scales with
    /// |PFromMw|, direction with its sign. Allocation-free: only the per-edge phase array is
    /// touched. Call from <c>CompositionTarget.Rendering</c>.
    /// </summary>
    public void Animate(double dtSeconds)
    {
        if (!_hasState || _edges.Count == 0) return;
        if (dtSeconds <= 0 || dtSeconds > AnimMaxDtS) dtSeconds = AnimDefaultDtS;

        bool any = false;
        for (int i = 0; i < _edges.Count; i++)
        {
            EdgeRec e = _edges[i];
            if (e.IsTransformer || !e.Valid) continue;
            // Screen pixels per second, independent of zoom  -  dashes march at the same
            // readable pace fitted or fully dived-in. Clamped well under the dash period
            // x frame rate so heavy corridors never alias into a frozen strobe.
            double pxPerSec = Math.Clamp(Math.Abs(e.FlowMw) / DashMwPerUnit, DashSpeedMin, DashSpeedMax) * DashPxScale;
            double dir = e.FlowMw >= 0 ? 1.0 : -1.0;   // +flow => A->B
            _dashPhase[i] += dir * pxPerSec * dtSeconds;
            any = true;
        }
        // Only the (cheap) flow overlay is rebuilt; corridors/rings stay cached.
        if (any) InvalidateVisual();
    }

    /// <summary>Reset zoom/pan so the whole network fits the current viewport with a margin.</summary>
    public void FitToView()
    {
        if (!View.FitToView())
        {
            _viewInitialised = false;   // try again once we have a size
            return;
        }

        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Centre the view on a bus (optionally setting a zoom). Used by search-to-jump.</summary>
    public void CenterOn(int busId, double? targetZoom = null)
    {
        if (!View.CenterOn(busId, targetZoom)) return;   // no size yet, or not a bus on the map
        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Centre on a bus and zoom to substation level (search / drill-down helper).</summary>
    public void FocusNode(int busId) => CenterOn(busId, View.FocusZoom);

    /// <summary>First bus whose name contains <paramref name="query"/> (case-insensitive), or null.</summary>
    public int? FindNode(string query) => _view.FindNode(query);

    /// <summary>
    /// Return the bus Id of the roundel nearest <paramref name="screenPt"/> within a small
    /// pixel radius, or null. Honours the current LOD (hidden nodes aren't hit). (SchematicView.HitTestNode.)
    /// </summary>
    public int? HitTestNode(Point screenPt) => View.HitTestNode(new ScreenPoint(screenPt.X, screenPt.Y));

    /// <summary>
    /// Branch index nearest <paramref name="screenPt"/> within a small radius, or null; with <paramref name="cycle"/> (a
    /// map click) a repeated click on coincident parallel circuits steps through them. (SchematicView.HitTestBranch.)
    /// </summary>
    public int? HitTestBranch(Point screenPt, bool cycle = false) => View.HitTestBranch(new ScreenPoint(screenPt.X, screenPt.Y), cycle);

    // ============================ rendering ============================

    protected override void OnRender(DrawingContext dc)
    {
        // Fill the surface so the element is hit-testable across its whole area.
        if (Background is not null)
            dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_nodes.Count == 0) return;
        if (!_viewInitialised) FitToView();
        if (!_viewInitialised) return;   // still no size

        if (_staticDirty) RebuildStatic();

        // GB coastline backdrop (under everything), if toggled on.
        if (_showCoast) DrawCoast(dc);

        // Real DNO/GSP admin boundaries (context, under the network), if toggled on.
        if (_showDnoAreas && _dnoWorldGeo is not null) DrawRegions(dc, _dnoWorldGeo, DnoPenBrush, 1.3);
        if (_showGspRegions && _gspWorldGeo is not null) DrawRegions(dc, _gspWorldGeo, GspPenBrush, 0.9);

        // Corridors (batched by colour bucket; low bucket drawn first so hot lines sit on top).
        Pen[] corridorPens = CorridorPensFor(ColorMode);
        for (int bkt = 0; bkt < LoadingBuckets; bkt++)
        {
            StreamGeometry? g = _corridorByBucket[bkt];
            if (g is not null) dc.DrawGeometry(null, corridorPens[bkt], g);
        }

        // Transformer ties.
        if (_txGeometry is not null) dc.DrawGeometry(null, TxPen, _txGeometry);

        // Animated flow overlay (rebuilt every frame for the visible, in-LOD subset).
        DrawFlow(dc);

        // Geocoded asset markers (over corridors, under roundels), if toggled on.
        if (_showAssets) DrawAssets(dc);

        // ETYS boundary cross-sections (over corridors, under roundels), if toggled on.
        if (_showBoundaries) DrawBoundaries(dc);

        // Roundels: dark fills, then per-voltage rings.
        if (_ringFillGeometry is not null) dc.DrawGeometry(DarkFill, null, _ringFillGeometry);
        if (_showGspAreas && _hasGroups) DrawGspAreas(dc);   // tint discs by supply area
        DrawRings(dc);

        // Stacked-asset clusters: bounded halo + count when fanned open, hint dot when collapsed.
        DrawFanClusters(dc);

        // Generation fleet overlay (fuel-coloured diamonds), if toggled on.
        if (_showFleet) DrawFleet(dc);

        // Labels (only when zoomed in enough; budgeted to avoid text-layout storms).
        DrawLabels(dc);

        // Geography key (top-right) for whichever DNO/GSP/asset layers are on.
        DrawGeoLegend(dc);

        // Overview minimap with viewport rectangle (drawn last, on top).
        if (_showMinimap) DrawMinimap(dc);
    }

    /// <summary>
    /// A small overview map in the bottom-left corner: EHV nodes as dots over a translucent
    /// panel, with an amber rectangle marking the slice of the world currently on screen.
    /// Clicking inside it recenters the main view (see <see cref="OnMouseLeftButtonDown"/>).
    /// </summary>
    // Cached minimap dot geometry - rebuilt only on Load (topology change), not per frame.
    private StreamGeometry? _minimapDots;
    private double _minimapDotsScale;
    private double _minimapDotsLeft, _minimapDotsTop;

    private void DrawMinimap(DrawingContext dc)
    {
        if (_nodes.Count == 0 || _worldBounds.Width <= 0 || _worldBounds.Height <= 0) return;

        const double maxW = 156, maxH = 200;
        double scale = Math.Min(maxW / _worldBounds.Width, maxH / _worldBounds.Height);
        double mmW = _worldBounds.Width * scale, mmH = _worldBounds.Height * scale;
        if (mmW < 24 || mmH < 24 || ActualHeight < mmH + 24) return;

        double left = 12, top = ActualHeight - mmH - 12;
        _mmRect = new Rect(left, top, mmW, mmH);
        _mmScale = scale;

        dc.DrawRectangle(MinimapBg, MinimapBorder, _mmRect);
        dc.PushClip(new RectangleGeometry(_mmRect));

        // Rebuild dot geometry only when the minimap position/scale changes.
        if (_minimapDots is null || scale != _minimapDotsScale || left != _minimapDotsLeft || top != _minimapDotsTop)
        {
            _minimapDotsScale = scale;
            _minimapDotsLeft = left;
            _minimapDotsTop = top;
            StreamGeometry dots = new();
            StreamGeometryContext g = dots.Open();
            foreach (NodeRec nd in _nodes)
            {
                if (!_showAllNodes && nd.DisplayKv < EhvDisplayKv) continue;
                double mx = left + (nd.Wx - _worldBounds.Left) * scale;
                double my = top + (nd.Wy - _worldBounds.Top) * scale;
                g.BeginFigure(new Point(mx - 0.7, my - 0.7), true, true);
                g.LineTo(new Point(mx + 0.7, my - 0.7), true, false);
                g.LineTo(new Point(mx + 0.7, my + 0.7), true, false);
                g.LineTo(new Point(mx - 0.7, my + 0.7), true, false);
            }
            g.Close();
            dots.Freeze();
            _minimapDots = dots;
        }
        dc.DrawGeometry(MinimapDot, null, _minimapDots);

        // GB coastline in the minimap too, when enabled.
        if (_showCoast)
        {
            Point[] w = CoastlineGb.World;
            StreamGeometry cg = new();
            StreamGeometryContext cc = cg.Open();
            cc.BeginFigure(new Point(left + (w[0].X - _worldBounds.Left) * scale, top + (w[0].Y - _worldBounds.Top) * scale), false, true);
            for (int i = 1; i < w.Length; i++)
                cc.LineTo(new Point(left + (w[i].X - _worldBounds.Left) * scale, top + (w[i].Y - _worldBounds.Top) * scale), true, false);
            cc.Close();
            cg.Freeze();
            dc.DrawGeometry(null, CoastPen, cg);
        }

        // Viewport rectangle (the world region currently on screen).
        Point w0 = ScreenToWorld(new Point(0, 0));
        Point w1 = ScreenToWorld(new Point(ActualWidth, ActualHeight));
        var vr = new Rect(
            new Point(left + (w0.X - _worldBounds.Left) * scale, top + (w0.Y - _worldBounds.Top) * scale),
            new Point(left + (w1.X - _worldBounds.Left) * scale, top + (w1.Y - _worldBounds.Top) * scale));
        dc.DrawRectangle(null, MinimapViewPen, vr);

        dc.Pop();
    }

    /// <summary>Draw the simplified GB coastline as a faint filled outline behind the network.</summary>
    private void DrawCoast(DrawingContext dc)
    {
        Point[] w = CoastlineGb.World;
        if (w.Length < 2) return;
        StreamGeometry g = new();
        StreamGeometryContext c = g.Open();
        c.BeginFigure(WorldToScreen(w[0].X, w[0].Y), true, true);
        for (int i = 1; i < w.Length; i++)
            c.LineTo(WorldToScreen(w[i].X, w[i].Y), true, false);
        c.Close();
        g.Freeze();
        dc.DrawGeometry(CoastFill, CoastPen, g);
    }

    /// <summary>
    /// Feed the real DNO/GSP geography + geocoded assets (loaded from <c>gda-out/geo</c>). The
    /// boundary rings are baked once into world-coord <see cref="StreamGeometry"/> so each redraw
    /// is a single transformed <see cref="DrawingContext.DrawGeometry"/>; asset markers stay as a
    /// point list drawn (culled) per frame. A null clears the overlay.
    /// </summary>
    public void SetGeography(GeographyData? geo)
    {
        _geo = geo;
        _assetFills = geo is null ? Array.Empty<Brush>() : geo.Assets.Select(a => (Brush)Palette.Frozen(a.Fill)).ToArray();
        _dnoWorldGeo = geo is null ? null : BakeRings(geo.Dno);
        _gspWorldGeo = geo is null ? null : BakeRings(geo.Gsp);
        InvalidateVisual();
    }

    /// <summary>Bake every region's rings into one frozen world-space geometry (closed outlines).</summary>
    private static StreamGeometry? BakeRings(IReadOnlyList<GeographyData.Region> regions)
    {
        if (regions.Count == 0) return null;
        StreamGeometry g = new();
        StreamGeometryContext c = g.Open();
        foreach (GeographyData.Region r in regions)
            foreach (WorldPoint[] ring in r.Rings)
            {
                if (ring.Length < 2) continue;
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), false, true);   // not filled, closed
                for (int i = 1; i < ring.Length; i++) c.LineTo(new Point(ring[i].X, ring[i].Y), true, false);
            }
        c.Close();
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Draw a baked world-space boundary geometry by pushing the world->screen transform and
    /// stroking with a pen whose world-space thickness renders at a constant screen width.
    /// </summary>
    private void DrawRegions(DrawingContext dc, StreamGeometry geo, Brush brush, double screenPx)
    {
        var m = new Matrix(_zoom, 0, 0, _zoom, _offset.X, _offset.Y);   // == WorldToScreen
        dc.PushTransform(new MatrixTransform(m));
        var pen = new Pen(brush, screenPx / _zoom) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
        dc.Pop();
    }

    /// <summary>
    /// Plot the geocoded assets as fuel-coloured diamonds (size gently graded by capacity), batched
    /// by fill brush and viewport-culled - the same allocation-light pattern as the generation fleet.
    /// </summary>
    private void DrawAssets(DrawingContext dc)
    {
        if (_geo is null || _geo.Assets.Count == 0) return;
        Rect view = new(-12, -12, ActualWidth + 24, ActualHeight + 24);
        double maxMw = _geo.MaxAssetMw;

        var ctxByBrush = new Dictionary<Brush, (StreamGeometry g, StreamGeometryContext c)>();
        for (int ai = 0; ai < _geo.Assets.Count; ai++)
        {
            GeographyData.AssetMarker a = _geo.Assets[ai];
            Point s = WorldToScreen(a.World.X, a.World.Y);
            if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;

            double rel = maxMw > 0 ? Math.Sqrt(Math.Max(0, a.Mw) / maxMw) : 0;   // 0..1
            double h = 2.0 + 5.0 * rel;                                          // half-extent, px

            Brush fill = _assetFills[ai];   // the cached frozen brush of this asset's colour token (see SetGeography)
            if (!ctxByBrush.TryGetValue(fill, out (StreamGeometry g, StreamGeometryContext c) gc))
            {
                StreamGeometry g = new();
                gc = (g, g.Open());
                ctxByBrush[fill] = gc;
            }
            StreamGeometryContext c = gc.c;
            c.BeginFigure(new Point(s.X, s.Y - h), true, true);
            c.LineTo(new Point(s.X + h, s.Y), true, false);
            c.LineTo(new Point(s.X, s.Y + h), true, false);
            c.LineTo(new Point(s.X - h, s.Y), true, false);
        }

        foreach ((Brush fill, (StreamGeometry g, StreamGeometryContext c)) in ctxByBrush)
        {
            c.Close();
            g.Freeze();
            dc.DrawGeometry(fill, AssetOutlinePen, g);
        }
    }

    /// <summary>
    /// A compact top-right key for whichever geography layers are on: line swatches for the DNO/GSP
    /// outlines and a fuel-colour key for the asset markers.
    /// </summary>
    private void DrawGeoLegend(DrawingContext dc)
    {
        if (_geo is null) return;
        bool anyAssets = _showAssets && _geo.Assets.Count > 0;
        if (!_showDnoAreas && !_showGspRegions && !anyAssets) return;

        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rows = new List<(Color col, bool line, string text)>();
        if (_showDnoAreas) rows.Add((DnoColor, true, "DNO licence area"));
        if (_showGspRegions) rows.Add((GspColor, true, "GSP region"));
        if (anyAssets)
            foreach ((string tech, Color col) in AssetLegendKeys())
                rows.Add((col, false, tech));
        if (rows.Count == 0) return;

        const double sw = 14, pad = 9, rowH = 16;
        var texts = rows.Select(r => new FormattedText(r.text,
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            GeoLegendFace, 11, LabelBrush, pps)).ToList();
        double textW = texts.Max(t => t.Width);
        double panelW = pad + sw + 7 + textW + pad;
        double panelH = pad + rows.Count * rowH + (pad - 3);
        double left = ActualWidth - panelW - 12, top = 12;
        if (left < 4 || panelH > ActualHeight - 24) return;

        var panel = new Rect(left, top, panelW, panelH);
        dc.DrawRoundedRectangle(GeoLegendBg, GeoLegendBorder, panel, 5, 5);

        double y = top + pad;
        for (int i = 0; i < rows.Count; i++)
        {
            (Color col, bool line, _) = rows[i];
            double cy = y + rowH / 2 - 1;
            double sx = left + pad;
            if (line)
            {
                var pen = new Pen(Palette.Frozen(col), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                dc.DrawLine(pen, new Point(sx, cy), new Point(sx + sw, cy));
            }
            else
            {
                double hh = 5;
                double mx = sx + sw / 2;
                var dia = new StreamGeometry();
                StreamGeometryContext dc2 = dia.Open();
                dc2.BeginFigure(new Point(mx, cy - hh), true, true);
                dc2.LineTo(new Point(mx + hh, cy), true, false);
                dc2.LineTo(new Point(mx, cy + hh), true, false);
                dc2.LineTo(new Point(mx - hh, cy), true, false);
                dc2.Close();
                dia.Freeze();
                dc.DrawGeometry(Palette.Frozen(col), AssetOutlinePen, dia);
            }
            dc.DrawText(texts[i], new Point(sx + sw + 7, y + (rowH - texts[i].Height) / 2));
            y += rowH;
        }
    }

    /// <summary>The most common fuel classes in the asset set (capped), for the legend key.</summary>
    private IEnumerable<(string Tech, Color Col)> AssetLegendKeys()
    {
        if (_geo is null) return Array.Empty<(string, Color)>();
        var count = new Dictionary<string, int>();
        var col = new Dictionary<string, Color>();
        foreach (GeographyData.AssetMarker a in _geo.Assets)
        {
            FuelInfo fi = Fuels.Classify(a.Tech);
            count[fi.Name] = count.GetValueOrDefault(fi.Name) + 1;
            col[fi.Name] = fi.Color;
        }
        return count.OrderByDescending(kv => kv.Value).Take(9)
                    .Select(kv => (kv.Key, col[kv.Key]));
    }

    /// <summary>
    /// Highlight each ETYS boundary's cut branches (coloured by transfer loading), add a small
    /// perpendicular "cut" tick at each branch midpoint, and label the cut with flow / limit / %.
    /// </summary>
    private void DrawBoundaries(DrawingContext dc)
    {
        if (_boundaries.Count == 0 || _nodes.Count == 0) return;
        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (BoundaryRec b in _boundaries)
        {
            Color col = Palette.Loading(b.LoadingPct / 100.0);
            Pen hiPen = new(Palette.Frozen(Color.FromArgb(150, col.R, col.G, col.B)), 7.0)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            hiPen.Freeze();
            Pen tickPen = new(Palette.Frozen(col), 2.2);
            tickPen.Freeze();

            double cx = 0, cy = 0; int k = 0;
            foreach (int ei in b.EdgeIdx)
            {
                if (ei < 0 || ei >= _edges.Count) continue;
                EdgeRec e = _edges[ei];
                if (!e.Valid) continue;
                Point pa = NodeScreen(e.A);
                Point pb = NodeScreen(e.B);
                dc.DrawLine(hiPen, pa, pb);

                Point mid = new((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
                Vector d = pb - pa;
                double len = d.Length;
                if (len > 1e-3)
                {
                    Vector perp = new(-d.Y / len, d.X / len);
                    dc.DrawLine(tickPen, mid - perp * 11, mid + perp * 11);
                }
                cx += mid.X; cy += mid.Y; k++;
            }
            if (k == 0) continue;
            Point c = new(cx / k, cy / k);

            string sub = b.LimitMw >= 1000
                ? $"{b.FlowMw / 1000:0.0}/{b.LimitMw / 1000:0.0} GW  {b.LoadingPct:0}%"
                : $"{b.FlowMw:0}/{b.LimitMw:0} MW  {b.LoadingPct:0}%";
            var ftHead = new FormattedText(b.Name, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, BoundaryFace, 11.5, LabelBrush, pps);
            var ftSub = new FormattedText(sub, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, BoundaryFace, 11.5, Palette.Frozen(col), pps);
            double w = Math.Max(ftHead.Width, ftSub.Width) + 12;
            double h = ftHead.Height + ftSub.Height + 8;
            Rect bg = new(c.X - w / 2, c.Y - h / 2, w, h);
            dc.DrawRoundedRectangle(BoundaryLabelBg, null, bg, 4, 4);
            dc.DrawText(ftHead, new Point(bg.Left + 6, bg.Top + 4));
            dc.DrawText(ftSub, new Point(bg.Left + 6, bg.Top + 4 + ftHead.Height));
        }
    }

    private void RecenterFromMinimap(Point p)
    {
        double wx = _worldBounds.Left + (p.X - _mmRect.Left) / _mmScale;
        double wy = _worldBounds.Top + (p.Y - _mmRect.Top) / _mmScale;
        _offset = new Vector(ActualWidth / 2 - wx * _zoom, ActualHeight / 2 - wy * _zoom);
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Rebuild the cached corridor/roundel geometry for the current view + state.</summary>
    private void RebuildStatic()
    {
        var sc = new StreamGeometry?[LoadingBuckets];
        var ctx = new StreamGeometryContext?[LoadingBuckets];
        StreamGeometry tx = new();
        StreamGeometryContext txCtx = tx.Open();

        _visibleRings.Clear();
        _labelNodes.Clear();
        _labelDrawing = null;   // the label layer follows _labelNodes (R133)

        Rect view = new(-32, -32, ActualWidth + 64, ActualHeight + 64);
        double minKv = LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        bool showLabels = ShowLabels();

        // ---- corridors + transformer ties ----
        // Real substations carry several co-located busbar nodes (same site, same kV),
        // so many corridors/roundels project onto the exact same screen geometry. Dedupe
        // by quantised screen position so a substation draws as ONE roundel and parallel
        // co-located corridors draw once, instead of a piled-up smear when zoomed in.
        // Fail loud on the map: when several parallel circuits share a quantised screen segment, the ONE line drawn takes
        // the WORST member's colour (the highest loading / stress / condition bucket, or in voltage mode the bucket
        // furthest from 1.0 pu), and a shared roundel takes the busbar whose voltage is furthest from 1.0 pu. It used to
        // be the FIRST member, so a double circuit at 40 % and 120 % drew green and two co-located busbars at 1.00 and
        // 1.08 pu drew a green ring. Insertion order is kept, so the drawing order is unchanged.
        var seenTie = new HashSet<(int, int, int, int)>();
        var lineBySeg = new Dictionary<(int, int, int, int), (Point A, Point B, int Bucket)>();
        var ringAt = new Dictionary<(int, int, int), int>();   // quantised roundel -> its index in _visibleRings
        double[] vmNodes = _vmByNode ?? Array.Empty<double>();
        double VoltSeverity(int ni) => ni < vmNodes.Length ? Math.Abs(vmNodes[ni] - 1.0) : 0.0;
        double BucketSeverity(int bkt) => _colorMode == MapColourMode.Voltage ? Math.Abs(SchematicView.VoltageOfBucket(bkt) - 1.0) : bkt;
        static (int, int) Q(Point p) => ((int)Math.Round(p.X), (int)Math.Round(p.Y));
        foreach (EdgeRec e in _edges)
        {
            if (!e.Valid) continue;
            NodeRec a = _nodes[e.A], b = _nodes[e.B];
            if (ehvOnly && (a.DisplayKv < minKv || b.DisplayKv < minKv)) continue;

            Point pa = NodeScreen(e.A), pb = NodeScreen(e.B);
            if (!SegmentIntersectsRect(pa, pb, view)) continue;   // viewport culling

            (int qax, int qay) = Q(pa);
            (int qbx, int qby) = Q(pb);
            if (qax == qbx && qay == qby) continue;               // zero-length (co-located endpoints)
            (int, int, int, int) segKey = qax < qbx || (qax == qbx && qay <= qby)
                ? (qax, qay, qbx, qby) : (qbx, qby, qax, qay);     // undirected
            if (e.IsTransformer)
            {
                if (!seenTie.Add(segKey)) continue;
                txCtx.BeginFigure(pa, false, false);
                txCtx.LineTo(pb, true, false);
            }
            else
            {
                int bkt = BucketFor(e);
                // The worst edge on a segment sets its colour (see above); the first sets its endpoints.
                if (lineBySeg.TryGetValue(segKey, out var drawn))
                {
                    if (BucketSeverity(bkt) > BucketSeverity(drawn.Bucket)) lineBySeg[segKey] = drawn with { Bucket = bkt };
                }
                else lineBySeg[segKey] = (pa, pb, bkt);
            }
        }
        foreach ((Point pa, Point pb, int bkt) in lineBySeg.Values)
        {
            ctx[bkt] ??= (sc[bkt] = new StreamGeometry()).Open();
            ctx[bkt]!.BeginFigure(pa, false, false);
            ctx[bkt]!.LineTo(pb, true, false);
        }

        for (int i = 0; i < LoadingBuckets; i++)
        {
            if (ctx[i] is StreamGeometryContext c) { c.Close(); sc[i]!.Freeze(); }
            _corridorByBucket[i] = sc[i];
        }
        txCtx.Close();
        tx.Freeze();
        _txGeometry = tx;

        // ---- roundels: dark fills (batched), rings recorded for per-voltage colouring ----
        StreamGeometry fills = new();
        StreamGeometryContext fillCtx = fills.Open();

        for (int ni = 0; ni < _nodes.Count; ni++)
        {
            NodeRec nd = _nodes[ni];
            if (nd.DisplayKv < minKv) continue;
            Point s = NodeScreen(ni);
            if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;

            double r = RoundelRadius(nd.DisplayKv);
            (int qx, int qy) = Q(s);
            // A co-located same-tier busbar draws ONE roundel, coloured by the member furthest from 1.0 pu.
            if (ringAt.TryGetValue((qx, qy, (int)Math.Round(r)), out int ringIdx))
            {
                RingRec shown = _visibleRings[ringIdx];
                if (VoltSeverity(ni) > VoltSeverity(shown.NodeIndex)) { shown.NodeIndex = ni; _visibleRings[ringIdx] = shown; }
                continue;
            }
            ringAt[(qx, qy, (int)Math.Round(r))] = _visibleRings.Count;
            EllipseFigure(fillCtx, s, r);
            _visibleRings.Add(new RingRec { Screen = s, Radius = r, NodeIndex = ni, Ehv = nd.DisplayKv >= EhvDisplayKv });

            if (showLabels && !string.IsNullOrEmpty(nd.Name) && nd.DisplayKv >= LabelMinKv())
                _labelNodes.Add((ni, s));
        }

        fillCtx.Close();
        fills.Freeze();
        _ringFillGeometry = fills;

        _staticDirty = false;
    }

    // Cached ring pens: quantise colour to avoid allocating hundreds of Pens per frame.
    private readonly Dictionary<(int r, int g, int b, bool ehv), Pen> _ringPenCache = new();

    private Pen CachedRingPen(Color c, bool ehv)
    {
        var key = (c.R, c.G, c.B, ehv);
        if (!_ringPenCache.TryGetValue(key, out Pen? pen))
        {
            pen = new Pen(Palette.Frozen(c), ehv ? RingPenEhv.Thickness : RingPenLv.Thickness);
            pen.Freeze();
            _ringPenCache[key] = pen;
        }
        return pen;
    }

    private void DrawRings(DrawingContext dc)
    {
        double[] vm = _vmByNode ?? Array.Empty<double>();
        double[]? cond = _conditionByNode;
        bool byCondition = _colorMode == MapColourMode.Condition && _hasOverlay && cond is not null;
        foreach (RingRec rr in _visibleRings)
        {
            Color c;
            if (byCondition && rr.NodeIndex < cond!.Length)
                c = Palette.Condition(cond[rr.NodeIndex]);
            else
            {
                double v = _hasState && rr.NodeIndex < vm.Length ? vm[rr.NodeIndex] : 1.0;
                c = Palette.Voltage(v);
            }
            dc.DrawEllipse(null, CachedRingPen(c, rr.Ehv), rr.Screen, rr.Radius, rr.Radius);
        }
    }

    /// <summary>
    /// Draw the generation fleet as fuel-coloured diamonds, sized by capacity (-scaled so a
    /// 50 MW peaker is still visible next to a 3 GW nuclear station). Batched by fill colour
    /// so the whole GB fleet is a handful of geometries. Viewport-culled.
    /// </summary>
    private void DrawFleet(DrawingContext dc)
    {
        if (_fleet.Count == 0) return;
        Rect view = new(-16, -16, ActualWidth + 32, ActualHeight + 32);

        // Group diamonds by brush (fuel colour)  -  few distinct colours, so few geometries.
        var ctxByBrush = new Dictionary<Brush, (StreamGeometry g, StreamGeometryContext c)>();

        foreach (FleetMarker m in _fleet)
        {
            Point s = WorldToScreen(m.Wx, m.Wy);
            if (s.X < view.Left || s.X > view.Right || s.Y < view.Top || s.Y > view.Bottom) continue;

            double rel = Math.Sqrt(m.Mw / _maxFleetMw);     // 0..1
            double h = 3.0 + 8.0 * rel;                      // diamond half-extent, px

            if (!ctxByBrush.TryGetValue(m.Fill, out (StreamGeometry g, StreamGeometryContext c) gc))
            {
                StreamGeometry g = new();
                gc = (g, g.Open());
                ctxByBrush[m.Fill] = gc;
            }
            StreamGeometryContext c = gc.c;
            c.BeginFigure(new Point(s.X, s.Y - h), true, true);
            c.LineTo(new Point(s.X + h, s.Y), true, false);
            c.LineTo(new Point(s.X, s.Y + h), true, false);
            c.LineTo(new Point(s.X - h, s.Y), true, false);
        }

        foreach ((Brush fill, (StreamGeometry g, StreamGeometryContext c)) in ctxByBrush)
        {
            c.Close();
            g.Freeze();
            dc.DrawGeometry(fill, FleetOutlinePen, g);
        }
    }

    /// <summary>Tint each visible roundel with its GSP supply-area colour (over the dark fill).</summary>
    private readonly Dictionary<(byte, byte, byte), Brush> _gspBrushCache = new();

    private void DrawGspAreas(DrawingContext dc)
    {
        Color[] gc = _groupColorByNode;
        foreach (RingRec rr in _visibleRings)
        {
            if (rr.NodeIndex >= gc.Length) continue;
            Color c = gc[rr.NodeIndex];
            var key = (c.R, c.G, c.B);
            if (!_gspBrushCache.TryGetValue(key, out Brush? fill))
            {
                fill = Palette.Frozen(Color.FromArgb(170, c.R, c.G, c.B));
                _gspBrushCache[key] = fill;
            }
            dc.DrawEllipse(fill, null, rr.Screen, rr.Radius, rr.Radius);
        }
    }

    /// <summary>A stable, well-spread colour for a supply-area, keyed by its seed node index.</summary>
    private static Color GroupColor(int seed)
    {
        double hue = (seed * 0.6180339887498949) % 1.0;   // golden-ratio hue spread
        return HsvToRgb(hue, 0.55, 0.92);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double r = 0, g = 0, b = 0;
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

    private void DrawFlow(DrawingContext dc)
    {
        if (!_hasState) return;

        Rect view = new(-32, -32, ActualWidth + 64, ActualHeight + 64);
        double minKv = LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        Pen[] flowPens = FlowPensFor(ColorMode);

        const double dash = 5.0;     // screen px
        const double gap = 7.0;
        double period = dash + gap;

        for (int bkt = 0; bkt < LoadingBuckets; bkt++)
        {
            List<int>? indices = _edgesByBucket[bkt];
            if (indices is null || indices.Count == 0) continue;

            StreamGeometry flow = new();
            StreamGeometryContext g = flow.Open();
            bool any = false;

            foreach (int ei in indices)
            {
                EdgeRec e = _edges[ei];
                NodeRec a = _nodes[e.A], b = _nodes[e.B];
                if (ehvOnly && (a.DisplayKv < minKv || b.DisplayKv < minKv)) continue;

                Point pa = NodeScreen(e.A), pb = NodeScreen(e.B);
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
                    g.BeginFigure(new Point(pa.X + ux * s0, pa.Y + uy * s0), false, false);
                    g.LineTo(new Point(pa.X + ux * s1, pa.Y + uy * s1), true, false);
                    any = true;
                }
            }

            g.Close();
            if (any) { flow.Freeze(); dc.DrawGeometry(null, flowPens[bkt], flow); }
        }
    }

    private void DrawLabels(DrawingContext dc)
    {
        if (_labelNodes.Count == 0) return;
        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // R133: lay the labels out only when the label set (or the DPI) changed, then replay the recording.
        if (_labelDrawing is null || _labelDrawingPps != pps)
        {
            _labelDrawing = BuildLabelDrawing(pps);
            _labelDrawingPps = pps;
        }
        dc.DrawDrawing(_labelDrawing);
    }

    /// <summary>
    /// Record the label layer: exactly the FormattedText objects and DrawText origins the per-frame loop used to
    /// issue straight onto the render context, in the same order, captured into a frozen <see cref="DrawingGroup"/>
    /// so they are laid out once per view rather than once per animation frame (R133).
    /// </summary>
    private DrawingGroup BuildLabelDrawing(double pps)
    {
        // Budget the number of labels so a dense, deeply-zoomed view never causes a
        // text-layout storm. (Each FormattedText is a real layout pass.)
        int budget = LabelBudget;
        double fontSize = 11;

        var group = new DrawingGroup();
        using (DrawingContext ldc = group.Open())
        {
            int drawn = 0;
            foreach ((int nodeIndex, Point screen) in _labelNodes)
            {
                if (drawn++ >= budget) break;
                NodeRec nd = _nodes[nodeIndex];
                string text = (nd.Name ?? "").Replace(" 400kV", "").Replace(" 275kV", "").ToUpperInvariant();
                if (text.Length == 0) continue;
                var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelFace, fontSize, LabelBrush, pps);
                double r = RoundelRadius(nd.DisplayKv);
                ldc.DrawText(ft, new Point(screen.X + r + 4, screen.Y - ft.Height / 2));
            }
        }
        group.Freeze();
        return group;
    }

    // ============================ interaction ============================

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_nodes.Count == 0) return;
        Point cursor = e.GetPosition(this);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(cursor, factor);
        e.Handled = true;
    }

    /// <summary>Zoom by <paramref name="factor"/> keeping the world point under <paramref name="screen"/> fixed.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        if (!View.ZoomAt(new ScreenPoint(screen.X, screen.Y), factor)) return;   // the clamped zoom would not change
        _staticDirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        // A click inside the minimap recenters the main view (and is not a node click / pan).
        Point down = e.GetPosition(this);
        if (_showMinimap && _mmRect.Contains(down)) { RecenterFromMinimap(down); e.Handled = true; return; }

        // Double-click drills in: onto a substation (centre + zoom to substation level), or
        // zoom toward the cursor on empty space.
        if (e.ClickCount == 2)
        {
            int? hit = HitTestNode(down);
            if (hit is int hb) FocusNode(hb);
            else ZoomAt(down, 1.6);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _movedWhileDown = false;
        _dragStartScreen = e.GetPosition(this);
        _dragStartOffset = _offset;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Point p = e.GetPosition(this);
        Vector d = p - _dragStartScreen;
        if (d.Length > 3) _movedWhileDown = true;
        _offset = _dragStartOffset + d;
        _staticDirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (_movedWhileDown) return;   // it was a pan, not a click

        Point p = e.GetPosition(this);
        int? node = HitTestNode(p);
        if (node is int id) { NodeClicked?.Invoke(id); return; }
        if (BranchClicked is not null)
        {
            int? br = HitTestBranch(p, cycle: true);   // repeated clicks step through coincident parallel circuits
            if (br is int bi) BranchClicked.Invoke(bi);
        }
    }

    /// <summary>
    /// Capture can be lost without a left-button-up reaching this control (Alt+Tab, a message box, another window
    /// taking the mouse). Ending the pan here stops later button-less mouse moves from panning the map.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!_viewInitialised) FitToView();
        else _staticDirty = true;   // viewport size changed; re-cull
        InvalidateVisual();
    }

    // ============================ transforms / LOD / geometry helpers ============================

    // UI programme 0.2: the transforms, the fan-out, the roundel sizing and the level of detail are SchematicView's;
    // these wrappers give the drawing code the same names and WPF points (the same two doubles).
    private static Point P(ScreenPoint p) => new(p.X, p.Y);

    private Point WorldToScreen(double wx, double wy) => P(_view.WorldToScreen(wx, wy));

    private static readonly Brush FanHaloFill = Palette.Frozen(Color.FromArgb(28, 90, 120, 150));
    private static readonly Pen FanHaloPen = MakePen(Color.FromArgb(150, 120, 150, 175), 1.0, 1.0);
    private static readonly Brush FanBadgeBg = Palette.Frozen(Color.FromArgb(220, 14, 20, 28));
    private static readonly Typeface FanBadgeFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>
    /// Draw the "these are stacked assets at one site" affordance: a bounded halo enclosing a
    /// fanned-open cluster plus an "N assets" badge; when collapsed (zoomed out), just a small
    /// count badge so the stacking is discoverable before you dive in.
    /// </summary>
    private void DrawFanClusters(DrawingContext dc)
    {
        if (_fanClusters.Count == 0) return;
        double minKv = LodMinBaseKv();
        double t = FanT();
        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var view = new Rect(-40, -40, ActualWidth + 80, ActualHeight + 80);

        foreach ((double wx, double wy, int count, double fullR, double maxKv) in _fanClusters)
        {
            if (maxKv < minKv) continue;                       // whole cluster below the LOD cut
            Point c = WorldToScreen(wx, wy);
            if (c.X < view.Left || c.X > view.Right || c.Y < view.Top || c.Y > view.Bottom) continue;

            if (t > 0.05)
            {
                double haloR = t * fullR + RoundelRadius(maxKv) + 5;
                dc.DrawEllipse(FanHaloFill, FanHaloPen, c, haloR, haloR);
                DrawFanBadge(dc, new Point(c.X, c.Y - haloR - 2), $"{count} assets", pps, centreAbove: true);
            }
            else if (count >= 8)
            {
                // Collapsed: only tag the significant piles (a badge on every 2-3 stack would smother
                // the fitted view); smaller busbar stacks reveal themselves by fanning as you zoom in.
                double rr = RoundelRadius(maxKv);
                DrawFanBadge(dc, new Point(c.X + rr * 0.7, c.Y - rr - 1), $"x{count}", pps, centreAbove: false);
            }
        }
    }

    private static void DrawFanBadge(DrawingContext dc, Point anchor, string text, double pps, bool centreAbove)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, FanBadgeFace, 10.5, LabelBrush, pps);
        double w = ft.Width + 8, h = ft.Height + 3;
        double left = centreAbove ? anchor.X - w / 2 : anchor.X;
        double top = centreAbove ? anchor.Y - h : anchor.Y - h / 2;
        var bg = new Rect(left, top, w, h);
        dc.DrawRoundedRectangle(FanBadgeBg, null, bg, 3, 3);
        dc.DrawText(ft, new Point(left + 4, top + 1.5));
    }

    /// <summary>How far a stacked-asset cluster is fanned open, 0 (collapsed) .. 1 (fully separated).</summary>
    private double FanT() => View.FanT();

    /// <summary>Screen position of a node, including its stacked-asset fan offset at the current zoom.</summary>
    private Point NodeScreen(int ni) => P(View.NodeScreen(ni));

    private Point ScreenToWorld(Point s) => P(_view.ScreenToWorld(new ScreenPoint(s.X, s.Y)));

    /// <summary>The radius of a roundel for a given base voltage, in screen pixels.</summary>
    private double RoundelRadius(double baseKv) => View.RoundelRadius(baseKv);

    /// <summary>Level of detail: the minimum base-kV currently shown.</summary>
    private double LodMinBaseKv() => View.LodMinBaseKv();

    private double LabelMinKv() => View.LabelMinKv();

    private bool ShowLabels() => View.ShowLabels();

    // UI programme 0.2: the colour-bin rules (LoadingBucketOf, BucketFrac, VoltageBucketOf, VoltageOfBucket, Bucket01,
    // BucketFor) moved to SchematicView; the pens below are built from the same bins.

    /// <summary>The colour bucket an edge draws in for the current mode (0 when that mode's data isn't in yet).</summary>
    private int BucketFor(EdgeRec e) => _view.BucketFor(e, _colorMode);

    private static Pen[] CorridorPensFor(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => VoltageCorridorPens,
        MapColourMode.Condition => ConditionCorridorPens,
        MapColourMode.Stress => StressCorridorPens,
        _ => CorridorPens,
    };

    private static Pen[] FlowPensFor(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => VoltageFlowPens,
        MapColourMode.Condition => ConditionFlowPens,
        MapColourMode.Stress => StressFlowPens,
        _ => FlowPens,
    };

    // ---- StreamGeometry helpers ----

    private static void EllipseFigure(StreamGeometryContext ctx, Point c, double r)
    {
        // Approximate a circle with two arc segments (cheap, smooth enough at these sizes).
        ctx.BeginFigure(new Point(c.X - r, c.Y), true, true);
        ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        ctx.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
    }

    private static bool SegmentIntersectsRect(Point a, Point b, Rect r)
    {
        // Quick accept: either endpoint inside.
        if (r.Contains(a) || r.Contains(b)) return true;
        // Reject if both endpoints share an outside half-plane.
        if ((a.X < r.Left && b.X < r.Left) || (a.X > r.Right && b.X > r.Right) ||
            (a.Y < r.Top && b.Y < r.Top) || (a.Y > r.Bottom && b.Y > r.Bottom)) return false;
        // Otherwise it may cross  -  treat as visible (cheap, conservative).
        return true;
    }

    // ---- pen/brush builders ----

    private static Pen MakePen(Color c, double thickness, double opacity)
    {
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Pen[] BuildCorridorPens()
    {
        var pens = new Pen[LoadingBuckets];
        for (int i = 0; i < LoadingBuckets; i++)
        {
            Color c = Palette.Loading(SchematicView.BucketFrac(i));
            // Base corridor is subtle; the bright marching dashes carry the eye.
            pens[i] = MakePen(c, 2.4, 0.42 + 0.22 * (i / (double)(LoadingBuckets - 1)));
        }
        return pens;
    }

    private static Pen[] BuildFlowPens()
    {
        var pens = new Pen[LoadingBuckets];
        for (int i = 0; i < LoadingBuckets; i++)
            pens[i] = MakePen(Palette.Loading(SchematicView.BucketFrac(i)), 2.8, 0.95);
        return pens;
    }

    private static readonly Pen[] VoltageCorridorPens = BuildVoltagePens(2.4, 0.5);
    private static readonly Pen[] VoltageFlowPens = BuildVoltagePens(2.8, 0.95);

    private static Pen[] BuildVoltagePens(double thickness, double opacity)
    {
        var pens = new Pen[LoadingBuckets];
        for (int i = 0; i < LoadingBuckets; i++)
            pens[i] = MakePen(Palette.Voltage(SchematicView.VoltageOfBucket(i)), thickness, opacity);
        return pens;
    }

    // GDA-bridge overlay pens: condition (fragility) and stress corridors, bucketed over a plain 0..1 ramp.
    private static readonly Pen[] ConditionCorridorPens = BuildRampPens(Palette.Condition, 2.4, 0.5);
    private static readonly Pen[] ConditionFlowPens = BuildRampPens(Palette.Condition, 2.8, 0.95);
    private static readonly Pen[] StressCorridorPens = BuildRampPens(Palette.Stress, 2.4, 0.5);
    private static readonly Pen[] StressFlowPens = BuildRampPens(Palette.Stress, 2.8, 0.95);

    private static Pen[] BuildRampPens(Func<double, Color> ramp, double thickness, double opacity)
    {
        var pens = new Pen[LoadingBuckets];
        for (int i = 0; i < LoadingBuckets; i++)
            pens[i] = MakePen(ramp(i / (double)(LoadingBuckets - 1)), thickness, opacity);
        return pens;
    }

    // ============================ records ============================

    // UI programme 0.2: NodeRec and EdgeRec are now GridSim.Ui.Schematic.SchematicNode and SchematicEdge (the same
    // fields), aliased at the top of this file.

    private struct RingRec
    {
        public Point Screen;
        public double Radius;
        public int NodeIndex;
        public bool Ehv;
    }

    private struct BoundaryRec
    {
        public string Name;
        public int[] EdgeIdx;     // edges (== branch indices) forming the cut
        public double LoadingPct; // live transfer loading
        public double FlowMw;
        public double LimitMw;
    }

    private struct FleetMarker
    {
        public double Wx, Wy;     // world coords (its bus)
        public double Mw;         // capacity (MBase, or |Pg| fallback)
        public Brush Fill;        // fuel colour (frozen)
        public string? Name;
        public bool Synchronous;  // spinning plant (contributes inertia)
    }
}
