using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// UI programme ASSETS A1b: replays a GridSim.Ui <see cref="VisualDisplayList"/> on an Avalonia
/// <see cref="DrawingContext"/>, call by call, in order: lines, ellipses, rounded rectangles, paths, arcs and text with
/// the cached pens and brushes of <see cref="VisualDrawing"/>, and the translate, scale and opacity layers pushed and
/// popped as listed. The electrical diagram control draws through it, and so can any later control that draws a
/// display list (the asset visuals of slice A1 return one too).
///
/// <para>Fail loud: a call this painter does not know, a pop with nothing pushed, a rectangle with a negative size
/// (which WPF refuses too) or a path with fewer than two points throws; it is never skipped silently. A shape whose
/// position is not a finite number is skipped, as the instrument controls do (WPF drew nothing visible for it).</para>
/// </summary>
internal static class DisplayListPainter
{
    /// <summary>Draw every call of <paramref name="list"/> in order; <paramref name="owner"/> supplies the theme font.</summary>
    public static void Paint(DrawingContext dc, VisualDisplayList list, Control owner)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(owner);
        Typeface bold = VisualDrawing.Face(owner, FontWeight.Bold), semi = VisualDrawing.Face(owner, FontWeight.SemiBold);
        var layers = new Stack<DrawingContext.PushedState>();
        try
        {
            foreach (VisualCommand command in list.Commands)
            {
                switch (command)
                {
                    case VisualLineCommand l:
                        VisualDrawing.Line(dc, VisualDrawing.Pen(l.Pen), l.From, l.To);
                        break;
                    case VisualEllipseCommand e:
                        if (VisualMaths.IsFinite(e.Centre) && double.IsFinite(e.RadiusX) && double.IsFinite(e.RadiusY))
                            dc.DrawEllipse(BrushOrNull(e.Fill), PenOrNull(e.Pen), VisualDrawing.P(e.Centre), e.RadiusX, e.RadiusY);
                        break;
                    case VisualRectCommand r:
                        if (r.Width < 0 || r.Height < 0)
                            throw new ArgumentException($"a rectangle cannot have a negative size ({r.Width} x {r.Height})", nameof(list));
                        dc.DrawRectangle(BrushOrNull(r.Fill), PenOrNull(r.Pen), new Rect(r.X, r.Y, r.Width, r.Height), r.RadiusX, r.RadiusY);
                        break;
                    case VisualPathCommand path:
                        if (path.Points.Count < 2) throw new ArgumentException("a path needs at least two points", nameof(list));
                        StreamGeometry? geo = Path(path);
                        if (geo is not null) dc.DrawGeometry(BrushOrNull(path.Fill), PenOrNull(path.Pen), geo);
                        break;
                    case VisualArcCommand a:
                        VisualDrawing.Arc(dc, VisualDrawing.Pen(a.Pen), a.Arc);
                        break;
                    case VisualTextCommand t:
                        FormattedText ft = VisualDrawing.Text(t.Text, t.Weight == VisualTextWeight.Bold ? bold : semi, t.Size, VisualDrawing.Brush(t.Fill));
                        if (t.Align == VisualTextAlign.Centre) VisualDrawing.DrawCentred(dc, ft, t.Anchor);
                        else VisualDrawing.DrawAt(dc, ft, t.Anchor);
                        break;
                    case VisualPushTranslateCommand tr:
                        layers.Push(dc.PushTransform(Matrix.CreateTranslation(tr.X, tr.Y)));
                        break;
                    case VisualPushScaleCommand sc:
                        layers.Push(dc.PushTransform(Matrix.CreateScale(sc.ScaleX, sc.ScaleY)));
                        break;
                    case VisualPushOpacityCommand op:
                        layers.Push(dc.PushOpacity(op.Opacity));
                        break;
                    case VisualPopCommand:
                        if (layers.Count == 0) throw new InvalidOperationException("pop with no open layer");
                        layers.Pop().Dispose();
                        break;
                    default:
                        throw new InvalidOperationException($"DisplayListPainter cannot draw a {command.GetType().Name}");
                }
            }
        }
        finally
        {
            // A list that threw part way still leaves the context as it found it.
            while (layers.Count > 0) layers.Pop().Dispose();
        }
    }

    private static IBrush? BrushOrNull(VisualFill? f) => f is VisualFill fill ? VisualDrawing.Brush(fill) : null;

    private static IPen? PenOrNull(VisualPen? p) => p is VisualPen pen ? VisualDrawing.Pen(pen) : null;

    /// <summary>The figure through the path's points, filled when it has a fill, closed when asked; null when a point
    /// is not a finite number.</summary>
    private static StreamGeometry? Path(VisualPathCommand path)
    {
        foreach (var p in path.Points)
            if (!VisualMaths.IsFinite(p)) return null;
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(VisualDrawing.P(path.Points[0]), path.Fill is not null);
            for (int i = 1; i < path.Points.Count; i++) g.LineTo(VisualDrawing.P(path.Points[i]));
            g.EndFigure(path.Closed);
        }
        return geo;
    }
}
