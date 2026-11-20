using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.Core.Presentation.Workbench;

namespace GridSim.App.Controls;

/// <summary>
/// R4-10: draws a Core <see cref="PlotSpec"/> (the workbench plot geometry) on an Avalonia drawing context. It is a thin
/// painter beside <see cref="DisplayListPainter"/>: every coordinate is Core's, this control only strokes and fills them, so
/// the Avalonia view and the web SVG show the same geometry. The plot is drawn on a white sheet whatever the theme, as the
/// report's SVG is, so a screenshot of it matches the report.
/// </summary>
public sealed class PlotControl : Control
{
    /// <summary>The plot to draw; null draws nothing.</summary>
    public static readonly StyledProperty<PlotSpec?> SpecProperty = AvaloniaProperty.Register<PlotControl, PlotSpec?>(nameof(Spec));

    static PlotControl()
    {
        AffectsRender<PlotControl>(SpecProperty);
        AffectsMeasure<PlotControl>(SpecProperty);
    }

    public PlotSpec? Spec { get => GetValue(SpecProperty); set => SetValue(SpecProperty, value); }

    /// <summary>The number of geometry items the last render painted (for tests); -1 before the first render.</summary>
    public int PaintedItems { get; private set; } = -1;

    protected override Size MeasureOverride(Size availableSize)
        => Spec is { } s ? new Size(s.Width, s.Height) : new Size(0, 0);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        PaintedItems = 0;
        if (Spec is not { } spec) return;
        Paint(context, spec);
    }

    private static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.Parse("#333333"));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#d9d9d9")), 1);
    private static readonly IPen FramePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#888888")), 1);
    private static readonly IPen RulePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#666666")), 1, new ImmutableDashStyle([4, 3], 0));
    private static readonly IPen GapPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#b00020")), 1, new ImmutableDashStyle([2, 3], 0));

    private static Color Colour(int style) => Color.Parse(PlotSvg.Palette[((style % PlotSvg.Palette.Count) + PlotSvg.Palette.Count) % PlotSvg.Palette.Count]);

    private void Paint(DrawingContext dc, PlotSpec spec)
    {
        Typeface face = VisualDrawing.Face(this, FontWeight.Normal), bold = VisualDrawing.Face(this, FontWeight.Bold);
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, spec.Width, spec.Height));
        Text(dc, spec.Title, bold, 13, spec.Width / 2, 4, "middle");
        foreach (PlotPanel p in spec.Panels)
        {
            double right = p.Left + p.Width, bottom = p.Top + p.Height;
            foreach (PlotTick t in p.X.Ticks)
            {
                dc.DrawLine(GridPen, new Point(t.Position, p.Top), new Point(t.Position, bottom));
                Text(dc, t.Label, face, 11, t.Position, bottom + 3, "middle");
            }
            foreach (PlotTick t in p.Y.Ticks)
            {
                dc.DrawLine(GridPen, new Point(p.Left, t.Position), new Point(right, t.Position));
                Text(dc, t.Label, face, 11, p.Left - 6, t.Position - 7, "end");
            }
            dc.DrawRectangle(null, FramePen, new Rect(p.Left, p.Top, p.Width, p.Height));
            if (p.X.Label.Length > 0) Text(dc, p.X.Label, face, 11, p.Left + p.Width / 2, bottom + 20, "middle");
            if (p.Y.Label.Length > 0)
            {
                double cy = p.Top + p.Height / 2;
                using (dc.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(14, cy)))
                    Text(dc, p.Y.Label, face, 11, 0, -7, "middle");
            }
            foreach (PlotItem item in p.Items) { Item(dc, item, p, face); PaintedItems++; }
        }
        if (spec.Legend.Count > 1)
        {
            double x = 64;
            for (int i = 0; i < spec.Legend.Count; i++)
            {
                dc.DrawRectangle(new ImmutableSolidColorBrush(Colour(i)), null, new Rect(x, spec.Height - 14, 10, 10));
                Text(dc, spec.Legend[i], face, 11, x + 14, spec.Height - 15, "start");
                x += 24 + spec.Legend[i].Length * 6.5;
            }
        }
    }

    private static void Item(DrawingContext dc, PlotItem item, PlotPanel p, Typeface face)
    {
        switch (item)
        {
            case PlotRule r when Finite(r.X1, r.Y1, r.X2, r.Y2):
                dc.DrawLine(RulePen, new Point(r.X1, r.Y1), new Point(r.X2, r.Y2));
                break;
            case PlotGap g when double.IsFinite(g.Px):
                dc.DrawLine(GapPen, new Point(g.Px, p.Top), new Point(g.Px, p.Top + p.Height));
                break;
            case PlotPolyline pl:
                Polyline(dc, pl);
                break;
            case PlotBar b:
                dc.DrawRectangle(new ImmutableSolidColorBrush(Colour(b.Style)), null, new Rect(b.Px - b.Width / 2, b.Py, b.Width, b.Height));
                break;
            case PlotMarker m when Finite(m.Px, m.Py):
                Marker(dc, m, face);
                break;
            case PlotText t:
                Text(dc, t.Text, face, 11, t.Px, t.Py - 8, t.Anchor);
                break;
        }
    }

    private static void Polyline(DrawingContext dc, PlotPolyline pl)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Colour(pl.Style)), 1.5);
        var run = new List<Point>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                var geo = new StreamGeometry();
                using (StreamGeometryContext c = geo.Open())
                {
                    c.BeginFigure(run[0], false);
                    for (int i = 1; i < run.Count; i++) c.LineTo(run[i]);
                    c.EndFigure(pl.Closed);
                }
                dc.DrawGeometry(null, pen, geo);
            }
            else if (run.Count == 1) dc.DrawEllipse(pen.Brush, null, run[0], 2, 2);
            run.Clear();
        }
        foreach (PlotPoint pt in pl.Points)
        {
            if (pt.Missing || !Finite(pt.Px, pt.Py)) Flush();
            else run.Add(new Point(pt.Px, pt.Py));
        }
        Flush();
    }

    private static void Marker(DrawingContext dc, PlotMarker m, Typeface face)
    {
        var brush = new ImmutableSolidColorBrush(Colour(m.Style));
        switch (m.Shape)
        {
            case PlotMarkerShape.Cross:
                var pen = new ImmutablePen(brush, 2);
                dc.DrawLine(pen, new Point(m.Px - 5, m.Py - 5), new Point(m.Px + 5, m.Py + 5));
                dc.DrawLine(pen, new Point(m.Px - 5, m.Py + 5), new Point(m.Px + 5, m.Py - 5));
                break;
            case PlotMarkerShape.Square:
                dc.DrawRectangle(brush, null, new Rect(m.Px - 3, m.Py - 3, 6, 6));
                break;
            default:
                dc.DrawEllipse(brush, null, new Point(m.Px, m.Py), 3.5, 3.5);
                break;
        }
        if (!string.IsNullOrEmpty(m.Label)) Text(dc, m.Label, face, 11, m.Px + 7, m.Py - 16, "start");
    }

    private static void Text(DrawingContext dc, string text, Typeface face, double size, double x, double top, string anchor)
    {
        FormattedText ft = VisualDrawing.Text(text, face, size, Ink);
        double left = anchor switch { "middle" => x - ft.Width / 2, "end" => x - ft.Width, _ => x };
        dc.DrawText(ft, new Point(left, top));
    }

    private static bool Finite(params double[] v) { foreach (double d in v) if (!double.IsFinite(d)) return false; return true; }
}
