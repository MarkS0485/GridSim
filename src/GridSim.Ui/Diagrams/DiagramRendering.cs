using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Ui.Diagrams;

/// <summary>
/// The scale and zoom transform between diagram units and the screen: screen = diagram * <see cref="Scale"/> + offset.
/// Immutable; zooming or fitting makes a new one.
/// </summary>
/// <param name="Scale">Screen pixels per diagram unit (above 0).</param>
/// <param name="OffsetX">Where diagram x = 0 lands on screen.</param>
/// <param name="OffsetY">Where diagram y = 0 lands on screen.</param>
public readonly record struct DiagramViewport(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>The smallest zoom the helpers below allow.</summary>
    public const double MinScale = 0.1;

    /// <summary>The largest zoom the helpers below allow.</summary>
    public const double MaxScale = 16;

    /// <summary>One screen pixel per diagram unit, no offset.</summary>
    public static DiagramViewport Identity => new(1, 0, 0);

    /// <summary>A diagram point on screen.</summary>
    public ScreenPoint ToScreen(ScreenPoint p) => new(p.X * Scale + OffsetX, p.Y * Scale + OffsetY);

    /// <summary>A screen point on the diagram.</summary>
    public ScreenPoint ToDiagram(ScreenPoint s) => new((s.X - OffsetX) / Scale, (s.Y - OffsetY) / Scale);

    /// <summary>
    /// The viewport that shows <paramref name="extent"/> whole and centred in a <paramref name="width"/> by
    /// <paramref name="height"/> area with <paramref name="margin"/> pixels to spare on every side, at no more than
    /// <paramref name="maxScale"/>. An empty diagram, or an area too small for the margins, gets the identity.
    /// </summary>
    public static DiagramViewport Fit(DiagramRect? extent, double width, double height, double margin = 24, double maxScale = 4)
    {
        if (extent is not DiagramRect e || !(width > 2 * margin) || !(height > 2 * margin)) return Identity;
        double sx = e.Width > 0 ? (width - 2 * margin) / e.Width : double.PositiveInfinity;
        double sy = e.Height > 0 ? (height - 2 * margin) / e.Height : double.PositiveInfinity;
        double scale = Math.Clamp(Math.Min(Math.Min(sx, sy), maxScale), MinScale, MaxScale);
        ScreenPoint c = e.Centre;
        return new DiagramViewport(scale, width / 2 - c.X * scale, height / 2 - c.Y * scale);
    }

    /// <summary>Zoom by <paramref name="factor"/> keeping the diagram point under <paramref name="screenPoint"/> fixed
    /// (the scale is kept within <see cref="MinScale"/> and <see cref="MaxScale"/>).</summary>
    public DiagramViewport ZoomAbout(ScreenPoint screenPoint, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor), factor, "a zoom factor must be a finite number above 0");
        ScreenPoint d = ToDiagram(screenPoint);
        double scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        return new DiagramViewport(scale, screenPoint.X - d.X * scale, screenPoint.Y - d.Y * scale);
    }
}

/// <summary>
/// Hit testing on a diagram: which symbol or connection is at a point. Symbols are tested first, topmost first (the
/// last placed, which is drawn last); a point inside or on the edge of a symbol's bounds hits it. Then connections,
/// topmost first: a point within <c>tolerance</c> of any segment of the route hits it (squared distances compared,
/// so the test is exact for points on the axis-aligned routes).
/// </summary>
public static class DiagramHitTester
{
    /// <summary>The default pick tolerance for connections, in screen pixels.</summary>
    public const double DefaultTolerancePx = 4;

    /// <summary>The hit at <paramref name="p"/> in diagram units, or null.</summary>
    public static DiagramHit? HitTest(DiagramModel model, ScreenPoint p, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return null;
        for (int i = model.Symbols.Count - 1; i >= 0; i--)
        {
            DiagramSymbol s = model.Symbols[i];
            if (SymbolLibrary.Bounds(s).Contains(p)) return new DiagramHit(DiagramHitKind.Symbol, s.Id, s.Kind);
        }
        double tol2 = tolerance * tolerance;
        for (int i = model.Connections.Count - 1; i >= 0; i--)
        {
            DiagramConnection c = model.Connections[i];
            for (int k = 1; k < c.Points.Count; k++)
                if (DiagramGeometry.DistSqToSegment(p, c.Points[k - 1], c.Points[k]) <= tol2)
                    return new DiagramHit(DiagramHitKind.Connection, c.Id, null);
        }
        return null;
    }

    /// <summary>The hit at a screen point through a viewport (the tolerance is in screen pixels).</summary>
    public static DiagramHit? HitTestScreen(DiagramModel model, DiagramViewport viewport, ScreenPoint screenPoint, double tolerancePx = DefaultTolerancePx) =>
        HitTest(model, viewport.ToDiagram(screenPoint), tolerancePx / viewport.Scale);
}

/// <summary>
/// Turns a diagram model into a display list (the drawing any front end replays): the viewport's translate and scale
/// layers, then the connections (under everything), then the symbols in placement order with their labels, then the
/// free labels on top.
/// </summary>
public static class DiagramRenderer
{
    /// <summary>The drawing of <paramref name="model"/> through <paramref name="viewport"/>.</summary>
    public static VisualDisplayList Render(DiagramModel model, DiagramViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dc = new VisualDisplayList();
        dc.PushTranslate(viewport.OffsetX, viewport.OffsetY);
        dc.PushScale(viewport.Scale, viewport.Scale);
        foreach (DiagramConnection c in model.Connections)
            dc.Polyline(DiagramStyle.ConductorPen(c.State), c.Points);
        foreach (DiagramSymbol s in model.Symbols)
            SymbolLibrary.Draw(dc, s);
        foreach (DiagramLabel l in model.Labels)
            dc.Text(l.Text, VisualTextWeight.SemiBold, l.Size, l.Muted ? DiagramStyle.MutedFill : DiagramStyle.LabelFill, l.Anchor,
                l.Align == DiagramLabelAlign.Centre ? VisualTextAlign.Centre : VisualTextAlign.Left);
        dc.Pop();
        dc.Pop();
        return dc;
    }
}
