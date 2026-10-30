// TEST COPY (UI programme ASSETS A1): src/GridSim.Wpf/Visuals/VisualDisplayListPainter.cs as it is now, drawing from src/GridSim.Ui/Visuals.
// TEST COPY Only the using and namespace lines differ (the WPF types are the recording stand-ins in UiVisualsWpfShim.cs);
// TEST COPY UiAssetVisualsParityTests checks the rest equals the real file, line for line.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Tests.Ui.NewAssetVisuals;

/// <summary>
/// UI programme ASSETS A1: replays a GridSim.Ui <see cref="VisualDisplayList"/> on a WPF <see cref="DrawingContext"/>,
/// call for call. The asset visuals (<see cref="SubstationMap"/>, <see cref="TransformerSchematic"/>) used to build their
/// drawing inline; their layout and colours now live in SubstationMapGeometry and TransformerSchematicGeometry
/// (src/GridSim.Ui/Visuals), which return the old drawing calls as a list. This class turns each listed call back into
/// exactly the WPF call the old code made: the same pen and brush values, the same rounded rectangle, the same stream
/// geometry figure calls, the same FormattedText settings (Bahnschrift at the listed weight, invariant culture, a 400
/// pixel box centred by starting 200 to the left, the device or literal pixels-per-dip). So the pixels are unchanged;
/// UiAssetVisualsParityTests pins that call by call against a verbatim copy of the old controls.
///
/// <para>Pens and brushes are frozen and cached by token, as the old controls held theirs in frozen static fields. A
/// pen is built with only the members its token changes from the WPF defaults (flat caps, mitre join, solid), so a
/// plain token gives exactly the pen <c>new Pen(brush, width)</c> gave.</para>
/// </summary>
internal static class VisualDisplayListPainter
{
    private static readonly Dictionary<VisualFill, Brush> Brushes = new();
    private static readonly Dictionary<VisualPen, Pen> Pens = new();
    private static readonly object Gate = new();

    /// <summary>The old controls' "Mono" typeface (Bahnschrift bold).</summary>
    private static readonly Typeface Mono =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>The old controls' "MonoLight" typeface (Bahnschrift semi-bold).</summary>
    private static readonly Typeface MonoLight =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>Draw every call of <paramref name="list"/>, in order. <paramref name="pps"/> is the element's device
    /// pixels-per-dip (VisualTreeHelper.GetDpi), used by the text calls that asked for it.</summary>
    public static void Paint(DrawingContext dc, VisualDisplayList list, double pps)
    {
        foreach (VisualCommand command in list.Commands)
        {
            switch (command)
            {
                case VisualLineCommand l:
                    dc.DrawLine(PenOf(l.Pen), P(l.From), P(l.To));
                    break;
                case VisualEllipseCommand e:
                    dc.DrawEllipse(BrushOrNull(e.Fill), PenOrNull(e.Pen), P(e.Centre), e.RadiusX, e.RadiusY);
                    break;
                case VisualRectCommand r:
                    dc.DrawRoundedRectangle(BrushOrNull(r.Fill), PenOrNull(r.Pen), new Rect(r.X, r.Y, r.Width, r.Height), r.RadiusX, r.RadiusY);
                    break;
                case VisualPathCommand path:
                    DrawPath(dc, path);
                    break;
                case VisualArcCommand a:
                    DrawArc(dc, PenOf(a.Pen), a.Arc);
                    break;
                case VisualTextCommand t:
                    DrawText(dc, t, pps);
                    break;
                case VisualPushTranslateCommand tr:
                    dc.PushTransform(new TranslateTransform(tr.X, tr.Y));
                    break;
                case VisualPushScaleCommand sc:
                    dc.PushTransform(new ScaleTransform(sc.ScaleX, sc.ScaleY));
                    break;
                case VisualPushOpacityCommand op:
                    dc.PushOpacity(op.Opacity);
                    break;
                case VisualPopCommand:
                    dc.Pop();
                    break;
                default:
                    // Fail loud: a new kind of call must be taught to this painter, never skipped silently.
                    throw new InvalidOperationException($"VisualDisplayListPainter cannot draw a {command.GetType().Name}");
            }
        }
    }

    /// <summary>The WPF point for a neutral screen point: the same two doubles.</summary>
    private static Point P(ScreenPoint p) => new(p.X, p.Y);

    private static Brush? BrushOrNull(VisualFill? f) => f is VisualFill fill ? BrushOf(fill) : null;

    private static Pen? PenOrNull(VisualPen? p) => p is VisualPen pen ? PenOf(pen) : null;

    /// <summary>The frozen brush for a fill token: its four colour bytes, its opacity only when it is not 1.</summary>
    private static Brush BrushOf(VisualFill f)
    {
        lock (Gate)
        {
            if (!Brushes.TryGetValue(f, out Brush? b))
            {
                var solid = new SolidColorBrush(Color.FromArgb(f.Colour.A, f.Colour.R, f.Colour.G, f.Colour.B));
                if (f.Opacity != 1.0) solid.Opacity = f.Opacity;
                solid.Freeze();
                b = solid;
                Brushes[f] = b;
            }
            return b;
        }
    }

    /// <summary>
    /// The frozen pen for a pen token. Only what differs from the WPF defaults is set: round caps, a round join, and a
    /// dash (a named stock pattern, or an even dash measured in stroke widths). A token that names both a pattern and an
    /// even dash is refused (it would be ambiguous which one the old code meant).
    /// </summary>
    private static Pen PenOf(VisualPen p)
    {
        lock (Gate)
        {
            if (!Pens.TryGetValue(p, out Pen? pen))
            {
                if (p.Pattern != VisualDashPattern.None && p.Dash != 0)
                    throw new ArgumentException("a pen token may set a named dash pattern or an even dash, not both", nameof(p));
                Brush brush = new SolidColorBrush(Color.FromArgb(p.Colour.A, p.Colour.R, p.Colour.G, p.Colour.B));
                if (p.Opacity != 1.0) brush.Opacity = p.Opacity;
                brush.Freeze();
                pen = new Pen(brush, p.Thickness);
                if (p.RoundCaps) { pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round; }
                if (p.RoundJoin) pen.LineJoin = PenLineJoin.Round;
                if (p.Pattern == VisualDashPattern.Dash) pen.DashStyle = DashStyles.Dash;
                else if (p.Pattern == VisualDashPattern.DashDot) pen.DashStyle = DashStyles.DashDot;
                else if (p.Dash > 0) pen.DashStyle = new DashStyle(new[] { p.Dash, p.Dash }, 0);
                pen.Freeze();
                Pens[p] = pen;
            }
            return pen;
        }
    }

    /// <summary>A figure through the points: the first begins it (filled when there is a fill, closed when asked), each
    /// later point is a stroked, unsmoothed LineTo; exactly the calls the old arrow heads made.</summary>
    private static void DrawPath(DrawingContext dc, VisualPathCommand path)
    {
        if (path.Points.Count < 2)
            throw new ArgumentException("a path needs at least two points", nameof(path));
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(P(path.Points[0]), path.Fill is not null, path.Closed);
            for (int i = 1; i < path.Points.Count; i++)
                g.LineTo(P(path.Points[i]), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(BrushOrNull(path.Fill), PenOrNull(path.Pen), geo);
    }

    private static void DrawArc(DrawingContext dc, Pen pen, VisualArc a)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(P(a.Start), false, false);
            g.ArcTo(P(a.End), new Size(a.Radius, a.Radius), 0, a.IsLargeArc,
                a.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>The old <c>Txt</c> helper: invariant layout culture, a 400 pixel box, centred by starting 200 left.</summary>
    private static void DrawText(DrawingContext dc, VisualTextCommand t, double pps)
    {
        TextAlignment align = t.Align == VisualTextAlign.Centre ? TextAlignment.Center : TextAlignment.Left;
        double scale = t.DevicePixelsPerDip ? pps : 1;
        var ft = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            t.Weight == VisualTextWeight.Bold ? Mono : MonoLight, t.Size, BrushOf(t.Fill), Math.Max(scale, 1))
        { TextAlignment = align, MaxTextWidth = 400 };
        double dx = align == TextAlignment.Center ? t.Anchor.X - 200 : t.Anchor.X;
        dc.DrawText(ft, new Point(dx, t.Anchor.Y));
    }
}
