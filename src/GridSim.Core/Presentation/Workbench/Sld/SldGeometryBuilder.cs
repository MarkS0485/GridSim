using GridSim.Core.Model;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>
/// Builds the <see cref="SldGeometry"/> of a model placed on the page (R4-18): the busbars with their connection slots, a symbol on
/// every edge and every hanging element, and an orthogonal connection for each. The only free choice in the whole diagram is the
/// busbar centres (<see cref="SldPlacement"/>): the same placement always gives the same geometry, to the last bit.
/// <para><b>Router.</b> A connection leaves its first end downwards through the edge symbol, then takes the first of these routes
/// that passes through no symbol box and lies on no track another connection already uses: straight down; a Z (down to a track in the
/// channel, across, down); for two ends in one row a U (down to a track, across, up to the bottom of the second end); and, for an edge
/// that spans more than one row, a route through a vertical lane outside every busbar. A route that is none of these is still drawn
/// (the straight Z) and reported in <see cref="SldGeometry.Crossings"/>; the automatic layout leaves room for the lane route, so on a
/// placement made by <see cref="SldAutoLayout"/> that list is empty.</para>
/// </summary>
public static class SldGeometryBuilder
{
    private const double Eps = 1e-9;

    public static SldGeometry Build(SldModel model, SldPlacement placement, SldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placement);
        SldOptions o = options ?? new SldOptions();
        int n = model.Nodes.Count;
        if (placement.X.Length != n || placement.Y.Length != n)
            throw new ArgumentException("The placement does not have one position per node of the model.", nameof(placement));

        (List<SldAttachment>[] top, List<SldAttachment>[] bottom) = SldAttachments.Analyse(model, placement.X, placement.Y, o.SameRowTolerance);

        var symbols = new List<SldSymbol>();
        var connections = new List<SldConnection>();
        var nodeBox = new SldBox[n];
        var slotsX = new double[n][];

        // 1. Busbars.
        for (int i = 0; i < n; i++)
        {
            int slots = Math.Max(top[i].Count, bottom[i].Count);
            double w = o.BusbarWidth(slots);
            double x = placement.X[i], y = placement.Y[i];
            var box = new SldBox(x - w / 2, y - o.BusbarThickness / 2, x + w / 2, y + o.BusbarThickness / 2);
            nodeBox[i] = box;
            int count = Math.Max(1, slots);
            slotsX[i] = new double[count];
            for (int k = 0; k < count; k++) slotsX[i][k] = box.Left + o.BusbarMargin + (k + 0.5) * o.SlotPitch;
            var ports = new List<SldPort>();
            string id = model.Nodes[i].Id;
            for (int k = 0; k < top[i].Count; k++) ports.Add(new SldPort(id + "#t" + WorkbenchText.Canon(k), slotsX[i][k], box.Top));
            for (int k = 0; k < bottom[i].Count; k++) ports.Add(new SldPort(id + "#b" + WorkbenchText.Canon(k), slotsX[i][k], box.Bottom));
            symbols.Add(new SldSymbol(id, SldKind.Busbar, model.Nodes[i].Label, box, ports, true, model.Nodes[i].BaseKv));
        }

        // 2. Symbols on the bottom slots: an edge symbol at the first end, a hanging symbol at its own node. Remember where each edge's
        //    second end arrives.
        var edgeOut = new Dictionary<int, SldPoint>();
        var edgeInPort = new Dictionary<int, string>();
        var secondEnd = new Dictionary<int, (int Node, SldSide Side, int Slot)>();
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < bottom[i].Count; k++)
            {
                SldAttachment at = bottom[i][k];
                SldElement el = model.Elements[at.Element];
                double sx = slotsX[i][k];
                if (at.IsEdge && !at.IsFirstEnd) { secondEnd[at.Element] = (i, SldSide.Bottom, k); continue; }
                string busPort = model.Nodes[i].Id + "#b" + WorkbenchText.Canon(k);
                double w = SldOptions.SymbolWidth(el.Kind), h = SldOptions.SymbolHeight(el.Kind);
                if (at.IsEdge)
                {
                    double symTop = nodeBox[i].Bottom + o.StubLead;
                    var box = new SldBox(sx - w / 2, symTop, sx + w / 2, symTop + h);
                    symbols.Add(new SldSymbol(el.Id, el.Kind, el.Label, box,
                        new[] { new SldPort(el.Id + "#in", sx, box.Top), new SldPort(el.Id + "#out", sx, box.Bottom) }, el.InService, el.BaseKv));
                    connections.Add(new SldConnection(el.Id + "/a", el.Id, busPort, el.Id + "#in", new[] { new SldPoint(sx, nodeBox[i].Bottom), new SldPoint(sx, box.Top) }));
                    edgeOut[at.Element] = new SldPoint(sx, box.Bottom);
                }
                else
                {
                    double symTop = nodeBox[i].Bottom + o.InjectionStem;
                    var box = new SldBox(sx - w / 2, symTop, sx + w / 2, symTop + h);
                    symbols.Add(new SldSymbol(el.Id, el.Kind, el.Label, box, new[] { new SldPort(el.Id + "#t", sx, box.Top) }, el.InService, el.BaseKv));
                    connections.Add(new SldConnection(el.Id + "/c", el.Id, busPort, el.Id + "#t", new[] { new SldPoint(sx, nodeBox[i].Bottom), new SldPoint(sx, box.Top) }));
                }
            }
            for (int k = 0; k < top[i].Count; k++) secondEnd[top[i][k].Element] = (i, SldSide.Top, k);
        }

        // 3. The spatial index of every box, then route the second half of every edge, in element order.
        var index = new BoxIndex(symbols);
        var registry = new TrackRegistry();
        double globalLeft = nodeBox.Length == 0 ? 0 : nodeBox.Min(b => b.Left), globalRight = nodeBox.Length == 0 ? 0 : nodeBox.Max(b => b.Right);
        int laneRight = 0, laneLeft = 0;
        int maxTracks = model.Elements.Count + 8;   // a free track always exists within one per connection
        var fallback = new List<string>();

        // Route the widest connections first (the classic interval-colouring order: a long run blocks a track over its whole length, so it
        // chooses before the short ones that can share the remaining tracks), ties by element order; emit them in element order.
        var edgeIds = new List<int>();
        for (int e = 0; e < model.Elements.Count; e++) if (model.Elements[e].IsEdge) edgeIds.Add(e);
        double Span(int e) => Math.Abs(edgeOut[e].X - slotsX[secondEnd[e].Node][secondEnd[e].Slot]) + Math.Abs(nodeBox[secondEnd[e].Node].Top - edgeOut[e].Y);
        var order = edgeIds.Select((e, i) => (E: e, I: i, W: Span(e))).OrderByDescending(t => t.W).ThenBy(t => t.I).Select(t => t.E).ToList();
        var routed = new Dictionary<int, SldConnection>();
        foreach (int e in order)
        {
            SldElement el = model.Elements[e];
            SldPoint start = edgeOut[e];
            (int bNode, SldSide side, int slot) = secondEnd[e];
            (int aNode, _, _) = SldAttachments.Ends(el, placement.Y, o.SameRowTolerance);
            double bx = slotsX[bNode][slot];
            SldBox bBox = nodeBox[bNode], aBox = nodeBox[aNode];
            double endY = side == SldSide.Top ? bBox.Top : bBox.Bottom;
            var end = new SldPoint(bx, endY);
            string endPort = model.Nodes[bNode].Id + (side == SldSide.Top ? "#t" : "#b") + WorkbenchText.Canon(slot);
            List<SldPoint>? route = side == SldSide.Top
                ? RouteDown(start, end, aBox, bBox, o, index, registry, globalLeft, globalRight, ref laneLeft, ref laneRight, maxTracks)
                : RouteU(start, end, aBox, bBox, o, index, registry, maxTracks);
            if (route is null)
            {
                // Nothing is clear: draw the plainest route and let the crossing list say so.
                double mid = side == SldSide.Top ? Math.Max(start.Y, (start.Y + end.Y) / 2) : Math.Max(start.Y, end.Y) + o.Drop;
                route = Simplify(new List<SldPoint> { start, new(start.X, mid), new(end.X, mid), end });
                fallback.Add(el.Id);
            }
            registry.Add(route);
            routed[e] = new SldConnection(el.Id + "/b", el.Id, el.Id + "#out", endPort, route);
        }
        foreach (int e in edgeIds) connections.Add(routed[e]);

        // 4. Bounds and the router's own crossing report.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        foreach (SldSymbol s in symbols) { Grow(s.Box.Left, s.Box.Top); Grow(s.Box.Right, s.Box.Bottom); }
        foreach (SldConnection c in connections) foreach (SldPoint p in c.Points) Grow(p.X, p.Y);
        if (symbols.Count == 0) { minX = minY = maxX = maxY = 0; }
        var bounds = new SldBox(minX - o.BoundsMargin, minY - o.BoundsMargin, maxX + o.BoundsMargin, maxY + o.BoundsMargin);

        var crossings = new List<SldCrossing>();
        if (fallback.Count > 0)
        {
            var finalIndex = new BoxIndex(symbols);
            foreach (SldConnection c in connections)
                for (int i = 0; i + 1 < c.Points.Count; i++)
                    foreach (int s in finalIndex.Crossing(c.Points[i], c.Points[i + 1]))
                        if (!crossings.Any(x => x.Connection == c.Id && x.Symbol == symbols[s].Id)) crossings.Add(new SldCrossing(c.Id, symbols[s].Id));
        }
        return new SldGeometry(symbols, connections, bounds, model.Omissions.ToList(), crossings, placement.Layout);
    }

    /// <summary>A diagram placed from the case's own layout coordinates (the buses' x and y times the layout scale). Null when the case has no layout: every bus at the origin.</summary>
    public static SldPlacement? PlaceFromCase(SldModel model, SldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Grid is not { } grid) return null;
        SldOptions o = options ?? new SldOptions();
        int n = model.Nodes.Count;
        var x = new double[n];
        var y = new double[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            Bus b = grid.Buses.First(bb => bb.Id == model.Nodes[i].Key);
            x[i] = b.X * o.LayoutScale;
            y[i] = b.Y * o.LayoutScale;
            if (b.X != 0 || b.Y != 0) any = true;
        }
        return any ? new SldPlacement(x, y, "case") : null;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Routes
    // ------------------------------------------------------------------------------------------------------------------

    private static List<SldPoint>? RouteDown(SldPoint s, SldPoint e, SldBox aBox, SldBox bBox, SldOptions o, BoxIndex idx, TrackRegistry reg,
        double globalLeft, double globalRight, ref int laneLeft, ref int laneRight, int maxTracks)
    {
        // Straight.
        if (Math.Abs(s.X - e.X) < Eps && e.Y > s.Y)
        {
            var p = new List<SldPoint> { s, e };
            if (Clear(p, idx, reg)) return p;
        }
        // Z through a track of the channel below the first end.
        for (int k = 0; k < maxTracks; k++)
        {
            double y = aBox.Bottom + o.Drop + k * o.TrackPitch;
            if (y > e.Y - o.TrackPitch + Eps) break;
            if (y <= s.Y + Eps) continue;
            var p = Simplify(new List<SldPoint> { s, new(s.X, y), new(e.X, y), e });
            if (Clear(p, idx, reg)) return p;
        }
        // Through a lane outside every busbar: down to a track, across to the lane, along it, across to above the second end, down.
        bool right = (s.X + e.X) / 2 >= (globalLeft + globalRight) / 2;
        double lane = right ? globalRight + o.LaneStart + laneRight * o.LanePitch : globalLeft - o.LaneStart - laneLeft * o.LanePitch;
        for (int k = 0; k < maxTracks; k++)
        {
            double ya = aBox.Bottom + o.Drop + k * o.TrackPitch;
            if (ya <= s.Y + Eps) continue;
            if (ya > e.Y - o.TrackPitch + Eps) break;
            var first = new List<SldPoint> { s, new(s.X, ya), new(lane, ya) };
            if (!Clear(first, idx, reg)) continue;
            for (int j = 0; j < maxTracks; j++)
            {
                double yb = bBox.Top - o.TrackPitch * (1 + j);
                if (yb <= ya + Eps) break;
                var p = Simplify(new List<SldPoint> { s, new(s.X, ya), new(lane, ya), new(lane, yb), new(e.X, yb), e });
                if (Clear(p, idx, reg))
                {
                    if (right) laneRight++; else laneLeft++;
                    return p;
                }
            }
        }
        return null;
    }

    private static List<SldPoint>? RouteU(SldPoint s, SldPoint e, SldBox aBox, SldBox bBox, SldOptions o, BoxIndex idx, TrackRegistry reg, int maxTracks)
    {
        for (int k = 0; k < maxTracks; k++)
        {
            double y = aBox.Bottom + o.Drop + k * o.TrackPitch;
            if (y <= s.Y + Eps || y <= e.Y + o.TrackPitch - Eps) continue;
            var p = Simplify(new List<SldPoint> { s, new(s.X, y), new(e.X, y), e });
            if (Clear(p, idx, reg)) return p;
        }
        return null;
    }

    private static bool Clear(List<SldPoint> p, BoxIndex idx, TrackRegistry reg)
    {
        for (int i = 0; i + 1 < p.Count; i++)
        {
            if (reg.Overlaps(p[i], p[i + 1])) return false;
            if (idx.Crosses(p[i], p[i + 1])) return false;
        }
        return true;
    }

    /// <summary>Drop repeated points and merge collinear runs, so a route is only its corners.</summary>
    private static List<SldPoint> Simplify(List<SldPoint> p)
    {
        var r = new List<SldPoint>();
        foreach (SldPoint q in p)
        {
            if (r.Count > 0 && Math.Abs(r[^1].X - q.X) < Eps && Math.Abs(r[^1].Y - q.Y) < Eps) continue;
            if (r.Count >= 2)
            {
                SldPoint a = r[^2], b = r[^1];
                bool sameX = Math.Abs(a.X - b.X) < Eps && Math.Abs(b.X - q.X) < Eps;
                bool sameY = Math.Abs(a.Y - b.Y) < Eps && Math.Abs(b.Y - q.Y) < Eps;
                if (sameX || sameY) { r[^1] = q; continue; }
            }
            r.Add(q);
        }
        return r;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Spatial index and track registry
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>A uniform grid over every symbol box, so a segment is tested only against the boxes in the cells it passes. Built from lists, queried by cell number: no iteration over a dictionary.</summary>
    private sealed class BoxIndex
    {
        private const double Cell = 96;
        private readonly Dictionary<long, List<int>> _cells = new();
        private readonly IReadOnlyList<SldSymbol> _symbols;

        public BoxIndex(IReadOnlyList<SldSymbol> symbols)
        {
            _symbols = symbols;
            for (int i = 0; i < symbols.Count; i++)
            {
                SldBox b = symbols[i].Box;
                for (int cx = Cx(b.Left); cx <= Cx(b.Right); cx++)
                    for (int cy = Cx(b.Top); cy <= Cx(b.Bottom); cy++)
                    {
                        long key = Key(cx, cy);
                        if (!_cells.TryGetValue(key, out List<int>? list)) _cells[key] = list = new List<int>();
                        list.Add(i);
                    }
            }
        }

        private static int Cx(double v) => (int)Math.Floor(v / Cell);
        private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public bool Crosses(SldPoint p, SldPoint q)
        {
            foreach (int s in Candidates(p, q))
                if (SldGeometryMath.SegmentCrossesInterior(p, q, _symbols[s].Box)) return true;
            return false;
        }

        public IEnumerable<int> Crossing(SldPoint p, SldPoint q)
        {
            foreach (int s in Candidates(p, q).Distinct().OrderBy(v => v))
                if (SldGeometryMath.SegmentCrossesInterior(p, q, _symbols[s].Box)) yield return s;
        }

        private IEnumerable<int> Candidates(SldPoint p, SldPoint q)
        {
            int x1 = Cx(Math.Min(p.X, q.X)), x2 = Cx(Math.Max(p.X, q.X)), y1 = Cx(Math.Min(p.Y, q.Y)), y2 = Cx(Math.Max(p.Y, q.Y));
            for (int cx = x1; cx <= x2; cx++)
                for (int cy = y1; cy <= y2; cy++)
                    if (_cells.TryGetValue(Key(cx, cy), out List<int>? list))
                        foreach (int s in list) yield return s;
        }
    }

    /// <summary>The horizontal and vertical runs already used by routed connections, by the coordinate they lie on, so two connections never share a stretch of one line.</summary>
    private sealed class TrackRegistry
    {
        private readonly Dictionary<double, List<(double Lo, double Hi)>> _h = new();
        private readonly Dictionary<double, List<(double Lo, double Hi)>> _v = new();

        public bool Overlaps(SldPoint p, SldPoint q)
        {
            if (Math.Abs(p.Y - q.Y) < Eps) return Hit(_h, p.Y, Math.Min(p.X, q.X), Math.Max(p.X, q.X));
            if (Math.Abs(p.X - q.X) < Eps) return Hit(_v, p.X, Math.Min(p.Y, q.Y), Math.Max(p.Y, q.Y));
            return false;
        }

        private static bool Hit(Dictionary<double, List<(double Lo, double Hi)>> d, double key, double lo, double hi)
        {
            if (!d.TryGetValue(key, out List<(double Lo, double Hi)>? list)) return false;
            foreach ((double a, double b) in list) if (lo < b - Eps && a < hi - Eps) return true;
            return false;
        }

        public void Add(List<SldPoint> p)
        {
            for (int i = 0; i + 1 < p.Count; i++)
            {
                SldPoint a = p[i], b = p[i + 1];
                var d = Math.Abs(a.Y - b.Y) < Eps ? _h : _v;
                double key = d == _h ? a.Y : a.X;
                double lo = d == _h ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y), hi = d == _h ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                if (!d.TryGetValue(key, out List<(double Lo, double Hi)>? list)) d[key] = list = new List<(double, double)>();
                list.Add((lo, hi));
            }
        }
    }
}
