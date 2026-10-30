using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// The Avalonia side of the neutral drawing tokens (UI programme Phase 1.3): it turns a <see cref="UiColour"/>,
/// <see cref="VisualFill"/>, <see cref="VisualPen"/> or <see cref="VisualArc"/> from GridSim.Ui/Visuals into the
/// Avalonia brush, pen or geometry that draws it, and holds the few text helpers the five instrument controls share.
///
/// <para>Brushes and pens are immutable and cached by token, so a control re-rendering every frame does not build a new
/// pen each time (the WPF controls froze theirs for the same reason). Every colour is the token's four bytes, unchanged,
/// so the Avalonia controls paint exactly the colours the WPF controls paint.</para>
/// </summary>
internal static class VisualDrawing
{
    private static readonly Dictionary<VisualFill, ImmutableSolidColorBrush> Brushes = new();
    private static readonly Dictionary<VisualPen, ImmutablePen> Pens = new();
    private static readonly object Gate = new();

    /// <summary>The font list the WPF controls used, when the theme's UiFont resource is not reachable.</summary>
    private static readonly FontFamily FallbackFont = new("Bahnschrift, Segoe UI, Arial");

    /// <summary>The Avalonia colour for a neutral colour token: the same alpha, red, green and blue bytes.</summary>
    public static Color ToColor(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>The Avalonia point for a neutral screen point: the same two doubles.</summary>
    public static Point P(ScreenPoint p) => new(p.X, p.Y);

    /// <summary>The cached immutable brush for a fill token (its colour at its opacity).</summary>
    public static ImmutableSolidColorBrush Brush(VisualFill f)
    {
        lock (Gate)
        {
            if (!Brushes.TryGetValue(f, out ImmutableSolidColorBrush? b))
            {
                b = new ImmutableSolidColorBrush(ToColor(f.Colour), f.Opacity);
                Brushes[f] = b;
            }
            return b;
        }
    }

    /// <summary>
    /// The cached immutable pen for a pen token: its colour at its opacity, its thickness, round or flat caps, round or
    /// mitre joins, and a named dash pattern or an even dash measured in stroke widths (WPF and Avalonia both scale
    /// dashes by the thickness).
    /// </summary>
    public static ImmutablePen Pen(VisualPen p)
    {
        lock (Gate)
        {
            if (!Pens.TryGetValue(p, out ImmutablePen? pen))
            {
                // A named pattern (UI programme ASSETS A1: WPF's stock Dash and DashDot) takes the shared arrays and
                // offset; otherwise an even dash, or solid. A token naming both is refused, loudly.
                if (p.Pattern != VisualDashPattern.None && p.Dash != 0)
                    throw new ArgumentException("a pen token may set a named dash pattern or an even dash, not both", nameof(p));
                ImmutableDashStyle? dash = p.Pattern != VisualDashPattern.None
                    ? new ImmutableDashStyle(VisualDashPatterns.Dashes(p.Pattern)!, VisualDashPatterns.Offset(p.Pattern))
                    : p.Dash > 0 ? new ImmutableDashStyle(new[] { p.Dash, p.Dash }, 0) : null;
                pen = new ImmutablePen(new ImmutableSolidColorBrush(ToColor(p.Colour), p.Opacity), p.Thickness, dash,
                    p.RoundCaps ? PenLineCap.Round : PenLineCap.Flat,
                    p.RoundJoin ? PenLineJoin.Round : PenLineJoin.Miter);
                Pens[p] = pen;
            }
            return pen;
        }
    }

    /// <summary>The control-room font (the theme's UiFont resource, as the WPF controls used Bahnschrift).</summary>
    public static FontFamily UiFont(Control owner)
        => owner.TryFindResource("UiFont", out object? f) && f is FontFamily family ? family : FallbackFont;

    /// <summary>A typeface of the control-room font at the given weight (the WPF controls used Bold and SemiBold).</summary>
    public static Typeface Face(Control owner, FontWeight weight) => new(UiFont(owner), FontStyle.Normal, weight);

    /// <summary>Lay out a line of text (invariant culture, left to right), ready to measure or draw.</summary>
    public static FormattedText Text(string text, Typeface face, double size, IBrush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);

    /// <summary>Draw text with its top edge at <paramref name="centreTop"/>.Y, centred on <paramref name="centreTop"/>.X
    /// (the WPF controls centred within a 400 pixel box; measuring gives the same centre).</summary>
    public static void DrawCentred(DrawingContext dc, FormattedText ft, ScreenPoint centreTop)
    {
        if (!VisualMaths.IsFinite(centreTop)) return;
        dc.DrawText(ft, new Point(centreTop.X - ft.Width / 2, centreTop.Y));
    }

    /// <summary>Draw text with its top-left corner at <paramref name="topLeft"/>.</summary>
    public static void DrawAt(DrawingContext dc, FormattedText ft, ScreenPoint topLeft)
    {
        if (!VisualMaths.IsFinite(topLeft)) return;
        dc.DrawText(ft, P(topLeft));
    }

    /// <summary>Draw a straight line, skipping one whose ends are not finite numbers.</summary>
    public static void Line(DrawingContext dc, IPen pen, ScreenPoint a, ScreenPoint b)
    {
        if (!VisualMaths.IsFinite(a) || !VisualMaths.IsFinite(b)) return;
        dc.DrawLine(pen, P(a), P(b));
    }

    /// <summary>Stroke an arc, skipping one whose ends or radius are not finite numbers.</summary>
    public static void Arc(DrawingContext dc, IPen pen, VisualArc arc)
    {
        if (!VisualMaths.IsFinite(arc.Start) || !VisualMaths.IsFinite(arc.End) || !double.IsFinite(arc.Radius)) return;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(P(arc.Start), false);
            g.ArcTo(P(arc.End), new Size(arc.Radius, arc.Radius), 0, arc.IsLargeArc,
                arc.Clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            g.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>
    /// A polyline (or, with <paramref name="closed"/>, a filled polygon) through the given points, or null when there
    /// are fewer than two or any point is not a finite number (WPF drew nothing visible for such a figure).
    /// </summary>
    public static StreamGeometry? Poly(IReadOnlyList<ScreenPoint> pts, bool closed)
    {
        if (pts.Count < 2) return null;
        foreach (ScreenPoint p in pts)
            if (!VisualMaths.IsFinite(p)) return null;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(P(pts[0]), closed);
            for (int i = 1; i < pts.Count; i++) g.LineTo(P(pts[i]));
            g.EndFigure(closed);
        }
        return geo;
    }
}
