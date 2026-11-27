using System.Text;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>A point in diagram units (y grows downwards, as on a screen).</summary>
public readonly record struct SldPoint(double X, double Y);

/// <summary>An axis-aligned rectangle in diagram units.</summary>
public readonly record struct SldBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CentreX => (Left + Right) / 2;
    public double CentreY => (Top + Bottom) / 2;

    /// <summary>True when the point lies inside or on the boundary.</summary>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>True when the interiors overlap (touching edges do not count).</summary>
    public bool Overlaps(SldBox o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
}

/// <summary>A connection point of a symbol. <see cref="Id"/> is the symbol id and a suffix: <c>#t{k}</c> and <c>#b{k}</c> are the
/// top and bottom slots of a busbar, <c>#in</c> and <c>#out</c> the two ends of an edge symbol, <c>#t</c> the top of a hanging symbol.</summary>
public sealed record SldPort(string Id, double X, double Y);

/// <summary>One drawn object: a busbar or a symbol. <c>ElementId</c> is the model element it stands for (the same as
/// <see cref="Id"/>), <see cref="InService"/> false for an open switch or an out-of-service element.</summary>
public sealed record SldSymbol(string Id, SldKind Kind, string Label, SldBox Box, IReadOnlyList<SldPort> Ports, bool InService, double BaseKv);

/// <summary>One orthogonal connection: a polyline of horizontal and vertical segments between two ports. <see cref="ElementId"/> is
/// the element it belongs to (a branch has two connections, a hanging element one).</summary>
public sealed record SldConnection(string Id, string ElementId, string FromPort, string ToPort, IReadOnlyList<SldPoint> Points);

/// <summary>A connection that passes through the interior of a symbol other than at its own end points (the router's failure, stated).</summary>
public sealed record SldCrossing(string Connection, string Symbol);

/// <summary>
/// The neutral geometry of a single-line diagram (R4-18): the symbols with their boxes and ports, the orthogonal connections, the
/// drawing bounds and the router's own account of anything it could not keep clear. Both clients draw exactly this (the web as SVG
/// from <see cref="SldExport"/>, the Avalonia view from the same records), so the diagram is the same on both. It is a pure function of
/// the model and the options: <see cref="Canonical"/> gives the same text, and so <see cref="Sha256"/> the same hash, for the same
/// input on every machine.
/// </summary>
public sealed class SldGeometry
{
    public SldGeometry(IReadOnlyList<SldSymbol> symbols, IReadOnlyList<SldConnection> connections, SldBox bounds,
        IReadOnlyList<string> omitted, IReadOnlyList<SldCrossing> crossings, string layout)
    {
        Symbols = symbols;
        Connections = connections;
        Bounds = bounds;
        Omitted = omitted;
        Crossings = crossings;
        Layout = layout;
    }

    public IReadOnlyList<SldSymbol> Symbols { get; }
    public IReadOnlyList<SldConnection> Connections { get; }
    public SldBox Bounds { get; }

    /// <summary>The elements the model has and the diagram could not place (an element naming a missing node), with the reason.</summary>
    public IReadOnlyList<string> Omitted { get; }

    /// <summary>Connections the router could not keep clear of a symbol (empty on every case in the shipped set).</summary>
    public IReadOnlyList<SldCrossing> Crossings { get; }

    /// <summary>"auto" (placed by <see cref="SldAutoLayout"/>) or "case" (placed from the case's own x and y).</summary>
    public string Layout { get; }

    public SldSymbol? Symbol(string id) => Symbols.FirstOrDefault(s => s.Id == id);

    public SldPort? Port(string id)
    {
        int hash = id.IndexOf('#', StringComparison.Ordinal);
        SldSymbol? s = Symbol(hash < 0 ? id : id[..hash]);
        return s?.Ports.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>The canonical text of the geometry: one line per symbol, port and connection with numbers in the round-trip form.</summary>
    public string Canonical()
    {
        var sb = new StringBuilder();
        sb.Append("gridsim-sld/1\nlayout\t").Append(Layout).Append('\n');
        sb.Append("bounds\t").Append(WorkbenchText.Canon(Bounds.Left)).Append('\t').Append(WorkbenchText.Canon(Bounds.Top)).Append('\t')
          .Append(WorkbenchText.Canon(Bounds.Right)).Append('\t').Append(WorkbenchText.Canon(Bounds.Bottom)).Append('\n');
        foreach (SldSymbol s in Symbols)
        {
            sb.Append("symbol\t").Append(s.Id).Append('\t').Append(s.Kind).Append('\t').Append(s.InService ? "1" : "0").Append('\t')
              .Append(WorkbenchText.Canon(s.Box.Left)).Append('\t').Append(WorkbenchText.Canon(s.Box.Top)).Append('\t')
              .Append(WorkbenchText.Canon(s.Box.Right)).Append('\t').Append(WorkbenchText.Canon(s.Box.Bottom)).Append('\n');
            foreach (SldPort p in s.Ports)
                sb.Append("port\t").Append(p.Id).Append('\t').Append(WorkbenchText.Canon(p.X)).Append('\t').Append(WorkbenchText.Canon(p.Y)).Append('\n');
        }
        foreach (SldConnection c in Connections)
        {
            sb.Append("connection\t").Append(c.Id).Append('\t').Append(c.FromPort).Append('\t').Append(c.ToPort);
            foreach (SldPoint p in c.Points) sb.Append('\t').Append(WorkbenchText.Canon(p.X)).Append(',').Append(WorkbenchText.Canon(p.Y));
            sb.Append('\n');
        }
        foreach (string o in Omitted) sb.Append("omitted\t").Append(o).Append('\n');
        foreach (SldCrossing x in Crossings) sb.Append("crossing\t").Append(x.Connection).Append('\t').Append(x.Symbol).Append('\n');
        return sb.ToString();
    }

    public string Sha256() => WorkbenchText.Sha256(Canonical());

    /// <summary>
    /// The independent check of the router: every segment of every connection against the interior of every symbol box, by brute
    /// force. A segment that only touches a box (an end point on its boundary) does not cross it. This is the predicate the tests use
    /// as the oracle; it shares no code with the router's own spatial index.
    /// </summary>
    public IReadOnlyList<SldCrossing> FindCrossings()
    {
        var found = new List<SldCrossing>();
        foreach (SldConnection c in Connections)
            foreach (SldSymbol s in Symbols)
                if (SldGeometryMath.PolylineCrossesInterior(c.Points, s.Box)) found.Add(new SldCrossing(c.Id, s.Id));
        return found;
    }

    /// <summary>The overlaps between symbol boxes (interiors), by brute force. Empty when no two symbols overlap.</summary>
    public IReadOnlyList<(string A, string B)> FindOverlaps()
    {
        var found = new List<(string, string)>();
        for (int i = 0; i < Symbols.Count; i++)
            for (int j = i + 1; j < Symbols.Count; j++)
                if (Symbols[i].Box.Overlaps(Symbols[j].Box)) found.Add((Symbols[i].Id, Symbols[j].Id));
        return found;
    }
}

/// <summary>Plain geometric predicates shared by the router and the hit tester (and re-derived independently in the tests).</summary>
public static class SldGeometryMath
{
    private const double Eps = 1e-9;

    /// <summary>True when the axis-aligned segment p-q passes through the open interior of the box.</summary>
    public static bool SegmentCrossesInterior(SldPoint p, SldPoint q, SldBox b)
    {
        double x1 = Math.Min(p.X, q.X), x2 = Math.Max(p.X, q.X), y1 = Math.Min(p.Y, q.Y), y2 = Math.Max(p.Y, q.Y);
        // The segment is a degenerate rectangle [x1,x2] x [y1,y2]; it meets the open interior when the open ranges overlap
        // on both axes (a zero extent counts as a point inside the open range).
        bool xs = x1 == x2 ? (x1 > b.Left + Eps && x1 < b.Right - Eps) : (x1 < b.Right - Eps && x2 > b.Left + Eps);
        bool ys = y1 == y2 ? (y1 > b.Top + Eps && y1 < b.Bottom - Eps) : (y1 < b.Bottom - Eps && y2 > b.Top + Eps);
        return xs && ys;
    }

    public static bool PolylineCrossesInterior(IReadOnlyList<SldPoint> pts, SldBox b)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
            if (SegmentCrossesInterior(pts[i], pts[i + 1], b)) return true;
        return false;
    }

    /// <summary>The distance from the point to the segment p-q (any direction).</summary>
    public static double DistanceToSegment(double x, double y, SldPoint p, SldPoint q)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((x - p.X) * dx + (y - p.Y) * dy) / len2, 0, 1);
        double ex = p.X + t * dx - x, ey = p.Y + t * dy - y;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}

/// <summary>What a pointer is over.</summary>
public enum SldHitKind { None, Symbol, Connection }

/// <summary>The answer of <see cref="SldHitTester"/>: the kind, the id of the symbol or connection (and its element), and for a
/// connection the distance to it.</summary>
public sealed record SldHit(SldHitKind Kind, string Id, string ElementId, double Distance);

/// <summary>
/// Hit testing of a point against the geometry (R4-18): a symbol whose box contains the point wins (the smallest such box, so a
/// symbol sitting on a stub wins over a busbar it overlaps; ties by id order); otherwise the nearest connection segment within
/// <c>tolerance</c> (ties by connection order); otherwise nothing. Analytic: point-in-rectangle and distance-to-segment.
/// </summary>
public static class SldHitTester
{
    public static SldHit HitTest(SldGeometry g, double x, double y, double tolerance = 4)
    {
        ArgumentNullException.ThrowIfNull(g);
        SldSymbol? best = null;
        double bestArea = double.MaxValue;
        foreach (SldSymbol s in g.Symbols)
        {
            if (!s.Box.Contains(x, y)) continue;
            double area = s.Box.Width * s.Box.Height;
            if (area < bestArea) { best = s; bestArea = area; }
        }
        if (best is not null) return new SldHit(SldHitKind.Symbol, best.Id, best.Id, 0);

        SldConnection? near = null;
        double nearD = double.MaxValue;
        foreach (SldConnection c in g.Connections)
            for (int i = 0; i + 1 < c.Points.Count; i++)
            {
                double d = SldGeometryMath.DistanceToSegment(x, y, c.Points[i], c.Points[i + 1]);
                if (d <= tolerance && d < nearD) { near = c; nearD = d; }
            }
        return near is null ? new SldHit(SldHitKind.None, "", "", 0) : new SldHit(SldHitKind.Connection, near.Id, near.ElementId, nearD);
    }
}
