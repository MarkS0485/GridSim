using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Schematic;

// Test oracle for UiSchematicParityTests (UI programme Phase 0.2, second slice). NOT production code.
//
// Below is the pure logic of the WPF map control exactly as it was before it moved to src/GridSim.Ui/Schematic
// (src/GridSim.Wpf/Visuals/FastSchematic.cs at the commit before the move): the topology capture, the display tiers, the
// stacked-asset fan-out, the state and overlay colour bins, the edge buckets, the view transform (fit, centre, zoom),
// the projection, the level of detail and the hit tests. The members were copied mechanically from git by their
// declarations; the only edits are:
//   - the WPF Point, Vector and Rect are the small shims below, which carry exactly the doubles and arithmetic the old
//     code used (Point - Point is a Vector; Vector.Length is Math.Sqrt(x * x + y * y); a Rect refuses a negative size);
//   - ActualWidth / ActualHeight are settable fields (the WPF control's size) and InvalidateVisual does nothing;
//   - the drawing-only parts of Load (the fleet markers, boundary cuts, supply-area groups and drawing caches) are left
//     out, each marked "(ORACLE: ...)": they draw from the topology but write nothing the logic below reads;
//   - "private" members are "internal", so the test can read the state they hold.

namespace GridSim.Tests.Ui.OldSchematic;

/// <summary>Shim of the WPF vector: two doubles, with the WPF length.</summary>
public readonly struct Vector
{
    public Vector(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>Shim of the WPF point: two doubles; the difference of two points is a vector.</summary>
public readonly struct Point
{
    public Point(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
    public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>Shim of the WPF rectangle: left, top, width and height; a negative size is refused, as WPF refused it.</summary>
public readonly struct Rect
{
    public Rect(double x, double y, double width, double height)
    {
        if (width < 0 || height < 0) throw new ArgumentException("negative size");
        X = x; Y = y; Width = width; Height = height;
    }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double Left => X;
    public double Top => Y;
}

/// <summary>The old FastSchematic's logic (see the file header).</summary>
public sealed class FastSchematic
{
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    private static void InvalidateVisual() { }

    // ---- topology (captured in Load), in WORLD coordinates ----
    // World space: x = normalised X (0..1), y = (1 - normalised Y) so north is up and
    // y grows downward like screen space. World bounds are computed from the data.
    internal readonly List<NodeRec> _nodes = new();
    internal readonly Dictionary<int, int> _idToIndex = new();   // bus Id -> _nodes index
    internal readonly List<EdgeRec> _edges = new();
    internal Rect _worldBounds = new(0, 0, 1, 1);
    internal double _maxBaseKv = 400;
    // Small networks (the IEEE/MATPOWER test cases) are drawn in full at every zoom: the GB
    // voltage level-of-detail (EHV backbone first) would hide most of a small imported case, whose
    // voltages don't line up with the GB tiers anyway. Large networks keep the progressive LOD.
    internal bool _showAllNodes;
    // A single-voltage case has no hierarchy at all, so we synthesise a display voltage tier from
    // the graph topology (see AssignDisplayTiers) purely to size the roundels; true kV is kept.
    internal bool _syntheticTiers;

    /// <summary>Bus count up to which the whole topology is drawn at fit (no voltage LOD culling).</summary>
    internal const int ShowAllCeiling = 800;

    // ---- live state ----
    internal double[]? _vmByNode;          // per node, index-aligned with _nodes
    internal bool _hasState;

    // ---- GDA-bridge overlays (condition-by-bus, stress-by-branch) ----
    internal double[]? _conditionByNode;   // per node (index-aligned with _nodes), 0 healthy .. 1 critical
    internal double[]? _stressByEdge;       // per edge (index-aligned with _edges), 0 cool .. 1 hot
    internal bool _hasOverlay;

    // ---- view transform (world -> screen): screen = world * _zoom + _offset ----
    internal double _zoom = 1.0;
    internal Vector _offset;               // screen-space translation, in DIPs
    internal const double MinZoom = 0.05;
    internal bool _viewInitialised;

    /// <summary>
    /// Zoom ceiling, relative to the fitted view rather than absolute: a distribution-depth
    /// model packs whole 11 kV clusters into a few thousandths of the world, so the ceiling
    /// must scale with how much detail there is to dive into. (The old absolute 400 sat
    /// *below* the fit zoom of a full-screen window  -  you could never even reach 1:1.)
    /// </summary>
    internal double MaxZoom => Math.Max(400.0, FitZoomEstimate() * 600.0);

    internal bool _staticDirty = true;     // (ORACLE: the drawing cache flag; set as before, read by nothing here)

    /// <summary>A branch with no thermal rating is coloured against this assumed MW figure (|P| / 2,000 MW, capped
    /// at 1). The same value as the web <c>PresentationLimits.UnratedBranchNominalMw</c>.</summary>
    public const double UnratedBranchNominalMw = GridSim.Core.Presentation.PresentationConstants.UnratedBranchNominalMw;   // N023: the one shared copy (was the literal 2000.0)

    /// <summary>Screen-space pick radius (px) added to a roundel's radius when hit-testing a click on a node.</summary>
    internal const double NodePickRadiusPx = 14.0;

    /// <summary>Screen-space distance (px) within which a click picks a branch.</summary>
    internal const double BranchPickTolerancePx = 8.0;

    // ---- co-located "stacked asset" fan-out (tube-map style) ----
    // Many buses are distinct busbars at the SAME physical site, so they project onto one
    // point. Zoomed out they read as a single roundel; zoomed in they fan out into a bounded
    // cluster (phyllotaxis disc) so each is individually visible + clickable, with a hint box.
    internal int[] _fanCount = Array.Empty<int>();     // per node: members at this site (1 = not stacked)
    internal double[] _fanDirX = Array.Empty<double>(); // per node: unit-disc offset direction (x)
    internal double[] _fanDirY = Array.Empty<double>(); // per node: unit-disc offset direction (y)
    internal double[] _fanFullR = Array.Empty<double>();// per node: fully-fanned disc radius (screen px)
    internal readonly List<(double Wx, double Wy, int Count, double FullR, double MaxKv)> _fanClusters = new();
    internal const double GoldenAngle = 2.399963229728653;

    // Pre-bucketed edge indices so DrawFlow touches only each bucket's edges (not all x 12).
    internal readonly List<int>[] _edgesByBucket = new List<int>[LoadingBuckets];

    // Loading colour is bucketed so corridors batch into a handful of geometries instead
    // of one pen per branch. 12 buckets across 0..1.2 loading is visually smooth enough.
    internal const int LoadingBuckets = 12;

    internal MapColourMode _colorMode = MapColourMode.Loading;   // (ORACLE: the mode field; set by the test)

    /// <summary>Capture topology and normalised coordinates; compute world bounds. Resets state.</summary>
    public void Load(GridModel model)
    {
        _nodes.Clear();
        _idToIndex.Clear();
        _edges.Clear();
        // (ORACLE: the drawing caches _visibleRings, _labelNodes, _labelDrawing are left out.)
        _hasState = false;
        _vmByNode = null;
        _hasOverlay = false;
        _conditionByNode = null;
        _stressByEdge = null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        _maxBaseKv = 1;

        foreach (Bus b in model.Buses)
        {
            double wx = b.X;
            double wy = 1.0 - b.Y;   // flip so north (y up) is at the top of the screen
            _idToIndex[b.Id] = _nodes.Count;
            _nodes.Add(new NodeRec
            {
                Id = b.Id,
                Wx = wx,
                Wy = wy,
                BaseKv = b.BaseKv,
                DisplayKv = b.BaseKv,   // GB models draw by true kV; flat imports overridden below
                Name = b.Name,
            });
            if (wx < minX) minX = wx;
            if (wy < minY) minY = wy;
            if (wx > maxX) maxX = wx;
            if (wy > maxY) maxY = wy;
            if (b.BaseKv > _maxBaseKv) _maxBaseKv = b.BaseKv;
        }

        // Small networks (test cases): draw the whole topology at fit. Single-voltage cases also get
        // a synthesised topological tier so the map isn't uniformly sized. Large multi-tier networks
        // (GB synthetic, PEGASE) keep the EHV-first progressive level-of-detail.
        int distinctKv = _nodes.Select(nd => Math.Round(nd.BaseKv, 3)).Distinct().Count();
        _showAllNodes = _nodes.Count is > 0 and <= ShowAllCeiling;
        _syntheticTiers = _nodes.Count > 1 && distinctKv <= 1;

        int idx = 0;
        foreach (Branch br in model.Branches)
        {
            bool haveA = _idToIndex.TryGetValue(br.From, out int a);
            bool haveB = _idToIndex.TryGetValue(br.To, out int c);
            _edges.Add(new EdgeRec
            {
                BranchIndex = idx++,
                A = haveA ? a : -1,
                B = haveB ? c : -1,
                IsTransformer = br.IsTransformer,
                Valid = haveA && haveB,
                Name = br.Name,
            });
        }

        // ---- synthesise a voltage hierarchy for flat imported cases ----
        // A bare IEEE/MATPOWER case has one voltage level, so every roundel would draw the same
        // size and the map reads flat. Derive a display tier from the graph topology (see
        // TopologyTiers) and map it to a GB-like nominal kV so the existing kV-driven sizing gives
        // a backbone->distribution hierarchy. The true kV is kept for tooltips.
        if (_syntheticTiers)
            AssignDisplayTiers(model);

        // (ORACLE: the fleet markers, the boundary cuts and BuildGspGroups are drawing layers and are left out.)
        BuildFanClusters();

        if (_nodes.Count == 0)
        {
            _worldBounds = new Rect(0, 0, 1, 1);
        }
        else
        {
            // Guard against degenerate (all-equal) coordinates.
            if (maxX - minX < 1e-9) { minX -= 0.5; maxX += 0.5; }
            if (maxY - minY < 1e-9) { minY -= 0.5; maxY += 0.5; }
            _worldBounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        _vmByNode = new double[_nodes.Count];
        for (int i = 0; i < _vmByNode.Length; i++) _vmByNode[i] = 1.0;

        // (ORACLE: the minimap and ring-pen caches are left out.)
        _viewInitialised = false;   // re-fit on next render
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Give a flat imported case a synthesised voltage hierarchy: map each bus's topological tier
    /// (see <see cref="TopologyTiers"/>) to a GB-like nominal kV in <see cref="NodeRec.DisplayKv"/>,
    /// so the renderer's kV-driven roundel sizing / ring weight / level-of-detail draw the backbone
    /// large and the distribution small - the "re-imagine as the UK grid" look. True kV is kept.
    /// </summary>
    internal void AssignDisplayTiers(GridModel model)
    {
        int n = _nodes.Count;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (EdgeRec e in _edges)
            if (e.Valid && e.A != e.B) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

        int source = -1;
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Slack && _idToIndex.TryGetValue(b.Id, out int si)) { source = si; break; }

        int[] tier = TopologyTiers.Assign(n, adj, source);
        for (int i = 0; i < n; i++)
        {
            NodeRec nd = _nodes[i];
            nd.DisplayKv = TopologyTiers.KvForTier(tier[i]);
            _nodes[i] = nd;
        }
    }

    /// <summary>Push a freshly solved state: per-branch loading/flow and per-node voltage.</summary>
    public void SetState(PowerFlowResult result)
    {
        if (_nodes.Count == 0) return;
        _vmByNode ??= new double[_nodes.Count];

        // Voltage by node.
        for (int i = 0; i < result.BusIds.Count && i < result.Vm.Length; i++)
            if (_idToIndex.TryGetValue(result.BusIds[i], out int ni))
                _vmByNode[ni] = result.Vm[i];

        // Per-branch loading + flow (LineFlows is index-aligned with model.Branches,
        // and _edges were built in that same order).
        int n = Math.Min(_edges.Count, result.LineFlows.Count);
        for (int i = 0; i < n; i++)
        {
            EdgeRec e = _edges[i];
            LineFlow lf = result.LineFlows[i];
            e.FlowMw = lf.PFromMw;
            double frac = lf.RatingMva > 0
                ? lf.LoadingPercent / 100.0
                : Math.Min(1.0, Math.Abs(lf.PFromMw) / UnratedBranchNominalMw);
            e.LoadingFrac = frac;
            // An UNRATED branch's fraction is only a flow-scaled tint (capped at 1.0 above), not a loading against a
            // rating, so it never takes the overload (red) bucket; a rated branch at or above 1.0 always does.
            e.LoadBucket = lf.RatingMva > 0 ? LoadingBucketOf(frac) : Math.Min(LoadingBucketOf(frac), LoadingBuckets - 2);
            double va = e.A >= 0 && e.A < _vmByNode.Length ? _vmByNode[e.A] : 1.0;
            double vb = e.B >= 0 && e.B < _vmByNode.Length ? _vmByNode[e.B] : 1.0;
            e.VoltBucket = VoltageBucketOf((va + vb) * 0.5);
            _edges[i] = e;
        }

        _hasState = true;
        _staticDirty = true;     // corridor colour buckets and ring voltages changed
        RebuildEdgeBuckets();
        InvalidateVisual();
    }

    internal void RebuildEdgeBuckets()
    {
        for (int b = 0; b < LoadingBuckets; b++)
            (_edgesByBucket[b] ??= new List<int>()).Clear();
        for (int i = 0; i < _edges.Count; i++)
        {
            EdgeRec e = _edges[i];
            if (e.IsTransformer || !e.Valid) continue;
            int bkt = BucketFor(e);
            _edgesByBucket[bkt]!.Add(i);
        }
    }

    /// <summary>
    /// Push the GDA-bridge per-asset overlays: condition/fragility keyed by bus <c>Id</c> (mapped to a node via
    /// <see cref="_idToIndex"/>) and stress keyed by branch <see cref="EdgeRec.Name"/>. Populates the per-node and
    /// per-edge scalar arrays (mirroring the <see cref="_vmByNode"/> pattern) and pre-buckets each edge for the
    /// <see cref="MapColourMode.Condition"/> / <see cref="MapColourMode.Stress"/> corridor colouring. A null map
    /// leaves that channel at its previous values.
    /// </summary>
    public void SetOverlay(IReadOnlyDictionary<int, double>? byBusId, IReadOnlyDictionary<string, double>? byBranchName)
    {
        if (_nodes.Count == 0) return;
        _conditionByNode ??= new double[_nodes.Count];
        _stressByEdge ??= new double[_edges.Count];

        if (byBusId is not null)
            foreach ((int id, double v) in byBusId)
                if (_idToIndex.TryGetValue(id, out int ni)) _conditionByNode[ni] = v;

        // The bridge protocol keys stress by branch NAME (the wire format carries no branch index), so namesakes all
        // take the one value the server sent for that name. That limit is the protocol's; an index-keyed field needs a
        // protocol change on both ends.
        if (byBranchName is not null)
            for (int i = 0; i < _edges.Count; i++)
            {
                EdgeRec e = _edges[i];
                if (e.Name is not null && byBranchName.TryGetValue(e.Name, out double s)) _stressByEdge[i] = s;
            }

        // Bucket each edge for batched corridor colouring: stress from its own scalar, condition from the
        // average of its endpoint buses (the same "corridor tracks its endpoints" rule the voltage mode uses).
        for (int i = 0; i < _edges.Count; i++)
        {
            EdgeRec e = _edges[i];
            e.StressBucket = Bucket01(_stressByEdge[i]);
            double ca = e.A >= 0 && e.A < _conditionByNode.Length ? _conditionByNode[e.A] : 0;
            double cb = e.B >= 0 && e.B < _conditionByNode.Length ? _conditionByNode[e.B] : 0;
            e.CondBucket = Bucket01((ca + cb) * 0.5);
            _edges[i] = e;
        }

        _hasOverlay = true;
        _staticDirty = true;     // corridor colour buckets changed
        RebuildEdgeBuckets();
        InvalidateVisual();
    }

    /// <summary>Reset zoom/pan so the whole network fits the current viewport with a margin.</summary>
    public void FitToView()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1 || _worldBounds.Width <= 0 || _worldBounds.Height <= 0)
        {
            _viewInitialised = false;   // try again once we have a size
            return;
        }

        const double pad = 36;
        double availW = Math.Max(1, w - 2 * pad);
        double availH = Math.Max(1, h - 2 * pad);
        double zx = availW / _worldBounds.Width;
        double zy = availH / _worldBounds.Height;
        _zoom = Math.Clamp(Math.Min(zx, zy), MinZoom, MaxZoom);

        // Centre the world bounds in the viewport.
        double cx = _worldBounds.X + _worldBounds.Width / 2;
        double cy = _worldBounds.Y + _worldBounds.Height / 2;
        _offset = new Vector(w / 2 - cx * _zoom, h / 2 - cy * _zoom);

        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Centre the view on a bus (optionally setting a zoom). Used by search-to-jump.</summary>
    public void CenterOn(int busId, double? targetZoom = null)
    {
        if (ActualWidth < 1 || !_idToIndex.TryGetValue(busId, out int ni)) return;
        NodeRec nd = _nodes[ni];
        if (targetZoom is double z) _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        _offset = new Vector(ActualWidth / 2 - nd.Wx * _zoom, ActualHeight / 2 - nd.Wy * _zoom);
        _viewInitialised = true;
        _staticDirty = true;
        InvalidateVisual();
    }

    /// <summary>Centre on a bus and zoom to substation level (search / drill-down helper).</summary>
    public void FocusNode(int busId) => CenterOn(busId, Math.Min(MaxZoom, FitZoomEstimate() * 6));

    /// <summary>First bus whose name contains <paramref name="query"/> (case-insensitive), or null.</summary>
    public int? FindNode(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query.Trim();
        foreach (NodeRec nd in _nodes)
            if (nd.Name is not null && nd.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                return nd.Id;
        return null;
    }

    /// <summary>
    /// Return the bus Id of the roundel nearest <paramref name="screenPt"/> within a small
    /// pixel radius, or null. Honours the current LOD (hidden nodes aren't hit).
    /// </summary>
    public int? HitTestNode(Point screenPt)
    {
        double bestDist = double.MaxValue;
        int bestId = -1;
        double tol = NodePickRadiusPx;   // screen-space pick radius
        double minKv = LodMinBaseKv();

        for (int i = 0; i < _nodes.Count; i++)
        {
            NodeRec nd = _nodes[i];
            if (nd.DisplayKv < minKv) continue;
            Point s = NodeScreen(i);
            double dx = s.X - screenPt.X, dy = s.Y - screenPt.Y;
            double d2 = dx * dx + dy * dy;
            double r = RoundelRadius(nd.DisplayKv) + tol;
            if (d2 <= r * r && d2 < bestDist) { bestDist = d2; bestId = nd.Id; }
        }
        return bestId >= 0 ? bestId : null;
    }

    /// <summary>
    /// Branch index nearest <paramref name="screenPt"/> within a small radius, or null. Parallel circuits drawn on the
    /// same screen line tie exactly, and the first used to win every time, so the second circuit of a double-circuit
    /// route could never be opened from the map. With <paramref name="cycle"/> (a map click), a repeated click at the
    /// same spot on the same set of coincident branches steps to the next one in turn; without it (hover, menus) the
    /// first is returned as before.
    /// </summary>
    public int? HitTestBranch(Point screenPt, bool cycle = false)
    {
        double minKv = LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        double bestDist = BranchPickTolerancePx * BranchPickTolerancePx;   // squared pixel tolerance
        var hits = new List<(int Branch, double D2)>();

        foreach (EdgeRec e in _edges)
        {
            if (!e.Valid) continue;
            NodeRec a = _nodes[e.A], b = _nodes[e.B];
            if (ehvOnly && (a.DisplayKv < minKv || b.DisplayKv < minKv)) continue;
            Point pa = NodeScreen(e.A), pb = NodeScreen(e.B);
            double d2 = DistSqPointToSegment(screenPt, pa, pb);
            if (d2 < bestDist) hits.Add((e.BranchIndex, d2));
        }
        if (hits.Count == 0) return null;

        // The nearest distance, and every branch within a hair of it (coincident on screen), in model order.
        double nearest = hits.Min(h => h.D2);
        int[] coincident = hits.Where(h => h.D2 <= nearest + CoincidentPickTolerancePx2).Select(h => h.Branch).ToArray();
        if (!cycle || coincident.Length == 1) { _lastPick = null; return coincident[0]; }

        // Same spot (within a couple of pixels) and the same coincident set as the last click: take the next one.
        int next = 0;
        if (_lastPick is { } lp && (lp.At - screenPt).Length <= BranchPickTolerancePx && lp.Set.SequenceEqual(coincident))
            next = (lp.Index + 1) % coincident.Length;
        _lastPick = (screenPt, coincident, next);
        return coincident[next];
    }

    /// <summary>Squared screen distance (px^2) within which two branches count as drawn on the same line for picking.</summary>
    internal const double CoincidentPickTolerancePx2 = 0.25;

    internal (Point At, int[] Set, int Index)? _lastPick;

    /// <summary>Zoom by <paramref name="factor"/> keeping the world point under <paramref name="screen"/> fixed.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;

        // Keep the world point under the cursor anchored: solve offset so screen stays put.
        Point worldUnderCursor = ScreenToWorld(screen);
        _zoom = newZoom;
        _offset = new Vector(screen.X - worldUnderCursor.X * _zoom,
                             screen.Y - worldUnderCursor.Y * _zoom);
        _staticDirty = true;
        InvalidateVisual();
    }

    internal Point WorldToScreen(double wx, double wy) =>
        new(wx * _zoom + _offset.X, wy * _zoom + _offset.Y);

    /// <summary>
    /// Group co-located nodes (same site) and lay each group's members out as a phyllotaxis disc,
    /// so <see cref="NodeScreen"/> can fan them apart as the view zooms in. One-off per Load.
    /// </summary>
    internal void BuildFanClusters()
    {
        int n = _nodes.Count;
        _fanCount = new int[n];
        _fanDirX = new double[n];
        _fanDirY = new double[n];
        _fanFullR = new double[n];
        _fanClusters.Clear();
        for (int i = 0; i < n; i++) _fanCount[i] = 1;

        // Bucket node indices by quantised world position (exact co-location).
        var byPos = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var key = ((long)Math.Round(_nodes[i].Wx * 1e6), (long)Math.Round(_nodes[i].Wy * 1e6));
            if (!byPos.TryGetValue(key, out List<int>? list)) byPos[key] = list = new List<int>();
            list.Add(i);
        }

        foreach (List<int> members in byPos.Values)
        {
            int k = members.Count;
            if (k < 2) continue;
            // Stable order so the layout doesn't jump between loads: by display kV then id.
            members.Sort((a, b) => _nodes[b].DisplayKv != _nodes[a].DisplayKv
                ? _nodes[b].DisplayKv.CompareTo(_nodes[a].DisplayKv)
                : _nodes[a].Id.CompareTo(_nodes[b].Id));
            double maxKv = 0;
            foreach (int m in members) maxKv = Math.Max(maxKv, _nodes[m].DisplayKv);
            // Disc big enough that k roundels don't overlap, bounded so it stays a tidy cluster.
            double fullR = Math.Clamp(11.0 * Math.Sqrt(k) * 1.9, 26, 240);
            for (int s = 0; s < k; s++)
            {
                double ang = s * GoldenAngle;
                double rad = Math.Sqrt((s + 0.5) / k);      // 0..1, even area fill
                int ni = members[s];
                _fanCount[ni] = k;
                _fanDirX[ni] = Math.Cos(ang) * rad;
                _fanDirY[ni] = Math.Sin(ang) * rad;
                _fanFullR[ni] = fullR;
            }
            _fanClusters.Add((_nodes[members[0]].Wx, _nodes[members[0]].Wy, k, fullR, maxKv));
        }
    }

    /// <summary>How far a stacked-asset cluster is fanned open, 0 (collapsed) .. 1 (fully separated),
    /// driven by zoom: closed at the fitted view, opening as you dive in.</summary>
    internal double FanT()
    {
        double rel = _zoom / FitZoomEstimate();
        return Math.Clamp((rel - 2.0) / 7.0, 0.0, 1.0);
    }

    /// <summary>Screen position of a node, including its stacked-asset fan offset at the current zoom.</summary>
    internal Point NodeScreen(int ni)
    {
        NodeRec nd = _nodes[ni];
        Point p = WorldToScreen(nd.Wx, nd.Wy);
        if ((uint)ni >= (uint)_fanCount.Length || _fanCount[ni] <= 1) return p;
        double t = FanT();
        if (t <= 0.001) return p;
        double r = t * _fanFullR[ni];
        return new Point(p.X + _fanDirX[ni] * r, p.Y + _fanDirY[ni] * r);
    }

    internal Point ScreenToWorld(Point s) =>
        new((s.X - _offset.X) / _zoom, (s.Y - _offset.Y) / _zoom);

    /// <summary>The radius of a roundel for a given base voltage, in screen pixels.</summary>
    internal double RoundelRadius(double baseKv)
    {
        // Graded by voltage level so the hierarchy reads at a glance: 400 kV backbone largest,
        // stepping down through 275 / 132 / 33 to 11 kV distribution.
        double baseR = baseKv >= 300 ? 10.5
                     : baseKv >= 200 ? 8.5
                     : baseKv >= 100 ? 6.5
                     : baseKv >= 25  ? 5.0
                     : 3.6;
        // Grow gently with zoom but clamp so they stay readable and never balloon.
        double scaled = baseR * Math.Clamp(_zoom / FitZoomEstimate(), 0.55, 1.8);
        return scaled;
    }

    /// <summary>A reference zoom (the fit-to-view zoom) used to scale roundels relative to "fitted".</summary>
    internal double FitZoomEstimate()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1 || _worldBounds.Width <= 0 || _worldBounds.Height <= 0) return 1;
        double zx = (w - 72) / _worldBounds.Width;
        double zy = (h - 72) / _worldBounds.Height;
        return Math.Max(0.01, Math.Min(zx, zy));
    }

    /// <summary>
    /// Level of detail: at low zoom only EHV (>=200 kV) nodes/branches are shown; as we zoom
    /// in, lower voltages are revealed. Returns the minimum base-kV currently shown.
    /// </summary>
    internal double LodMinBaseKv()
    {
        if (_showAllNodes) return 0;              // small test case: draw the whole topology at any zoom
        double rel = _zoom / FitZoomEstimate();   // 1.0 ~= fitted whole-network view
        if (rel < 1.4) return 200;                // far out: EHV backbone only
        if (rel < 3.0) return 100;                // mid: + sub-transmission (132 kV)
        if (rel < 7.0) return 20;                 // closer: + 33 kV primaries
        if (rel < 16.0) return 10;                // closer still: + 11 kV secondaries
        return 0;                                 // dived in: everything (6.6 kV pockets)
    }

    internal double LabelMinKv()
    {
        if (_showAllNodes) return 0;   // small test case: label by zoom only, not by kV tier
        double rel = _zoom / FitZoomEstimate();
        if (rel < 3.0) return 200;   // only EHV labels at first
        if (rel < 8.0) return 100;
        if (rel < 18.0) return 20;
        return 0;                    // all labels when fully dived in
    }

    internal bool ShowLabels() => _zoom / FitZoomEstimate() >= 1.8;

    /// <summary>
    /// The colour bucket for a loading fraction. Loadings below 1.0 fill the first <c>LoadingBuckets - 1</c> buckets in
    /// equal steps (floor, so a bucket never reaches past its upper edge), and EVERY loading at or above 1.0 (an
    /// overload) goes to the last bucket, which is drawn fully red. The old mapping rounded 0..1.2 across all twelve
    /// buckets, so bucket 9 spanned 0.927 to 1.036 and was drawn at 0.982 (amber-red): a circuit at 102 % of rating drew
    /// in exactly the colour of one at 94 %. A non-finite loading also lands in the red bucket (fail loud, never green).
    /// </summary>
    internal static int LoadingBucketOf(double frac)
    {
        if (!(frac < 1.0)) return LoadingBuckets - 1;                 // >= 1.0, or NaN: overloaded / unknown -> red
        if (frac <= 0) return 0;
        int b = (int)Math.Floor(frac * (LoadingBuckets - 1));         // 0 .. LoadingBuckets - 2 for 0 <= frac < 1
        return Math.Clamp(b, 0, LoadingBuckets - 2);
    }

    /// <summary>The loading fraction a bucket is drawn at: the middle of its band below 1.0, and 1.1 (fully red on
    /// <see cref="Palette.Loading"/>, which is red from 1.0) for the overload bucket.</summary>
    internal static double BucketFrac(int bucket) =>
        bucket >= LoadingBuckets - 1 ? 1.1 : (bucket + 0.5) / (LoadingBuckets - 1);

    internal static int VoltageBucketOf(double vm)
    {
        double frac = Math.Clamp((vm - 0.94) / 0.12, 0, 1);   // 0.94..1.06 p.u. band
        return Math.Clamp((int)(frac * (LoadingBuckets - 1) + 0.5), 0, LoadingBuckets - 1);
    }

    internal static double VoltageOfBucket(int b) => 0.94 + (double)b / (LoadingBuckets - 1) * 0.12;

    /// <summary>Bucket a plain 0..1 fraction (condition/stress overlays) across the loading-bucket count.</summary>
    internal static int Bucket01(double frac) =>
        Math.Clamp((int)(Math.Clamp(frac, 0, 1) * (LoadingBuckets - 1) + 0.5), 0, LoadingBuckets - 1);

    /// <summary>The colour bucket an edge draws in for the current mode (0 when that mode's data isn't in yet).</summary>
    internal int BucketFor(EdgeRec e) => _colorMode switch
    {
        MapColourMode.Voltage => _hasState ? e.VoltBucket : 0,
        MapColourMode.Condition => _hasOverlay ? e.CondBucket : 0,
        MapColourMode.Stress => _hasOverlay ? e.StressBucket : 0,
        _ => _hasState ? e.LoadBucket : 0,
    };

    internal static double DistSqPointToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) { double ex = p.X - a.X, ey = p.Y - a.Y; return ex * ex + ey * ey; }
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ox = p.X - cx, oy = p.Y - cy;
        return ox * ox + oy * oy;
    }

    internal struct NodeRec
    {
        public int Id;
        public double Wx, Wy;     // world coords
        public double BaseKv;     // true nominal kV (tooltips, labels)
        public double DisplayKv;  // kV used for drawing size/level-of-detail; == BaseKv for GB models,
                                  // a synthesised topological tier for flat imported cases
        public string? Name;
    }

    internal struct EdgeRec
    {
        public int BranchIndex;
        public int A, B;          // node indices, or -1
        public bool Valid;
        public bool IsTransformer;
        public string? Name;      // branch name (keys the stress-by-branch overlay)
        public double FlowMw;
        public double LoadingFrac;
        public int LoadBucket;
        public int VoltBucket;
        public int CondBucket;    // avg endpoint condition bucket (Condition colour mode)
        public int StressBucket;  // per-branch stress bucket (Stress colour mode)
    }
}
