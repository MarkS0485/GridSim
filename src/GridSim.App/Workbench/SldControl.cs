using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.App.Controls;
using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.App.Workbench;

/// <summary>
/// R4-18: draws the Core single-line diagram geometry (<see cref="SldGeometry"/>) on an Avalonia drawing context and turns pointer
/// input into the view model's selection and moves. Like <see cref="PlotControl"/> it only strokes and fills what Core computed: every
/// symbol box, port and connection point is Core's, so this view and the web SVG show the same diagram. The sheet is white whatever
/// the theme (as the plot sheet is), so a screenshot matches the exported SVG.
/// </summary>
public sealed class SldControl : Control
{
    public static readonly StyledProperty<SldViewModel?> ViewModelProperty = AvaloniaProperty.Register<SldControl, SldViewModel?>(nameof(ViewModel));

    static SldControl()
    {
        AffectsRender<SldControl>(ViewModelProperty);
    }

    private double _zoom = 1;
    private Point _pan;
    private bool _fit = true;
    private bool _dragging;
    private Point _grab;
    private Point _dragAt;

    public SldViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set
        {
            if (ViewModel is { } old) old.PropertyChanged -= OnChanged;
            SetValue(ViewModelProperty, value);
            if (value is not null) value.PropertyChanged += OnChanged;
            InvalidateVisual();
        }
    }

    /// <summary>The number of symbols and connections the last render painted (for tests); -1 before the first render.</summary>
    public int PaintedObjects { get; private set; } = -1;

    public SldControl()
    {
        Focusable = true;
        ClipToBounds = true;
        DoubleTapped += (_, _) =>
        {
            if (ViewModel is { SelectedId: { } id } vm && id.StartsWith("switch/", StringComparison.Ordinal)) vm.ToggleSelectedSwitch();
        };
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>Show the whole diagram in the control.</summary>
    public void FitToView()
    {
        _fit = true;
        InvalidateVisual();
    }

    /// <summary>The scale and the diagram-to-screen offset in force for a given size (fit, or the user's zoom and pan).</summary>
    public (double Scale, Point Origin) Transform(Size size)
    {
        if (ViewModel?.Diagram?.Geometry.Bounds is not { } b || b.Width <= 0 || b.Height <= 0) return (1, default);
        if (_fit)
        {
            double s = Math.Min(size.Width / b.Width, size.Height / b.Height);
            if (!double.IsFinite(s) || s <= 0) s = 1;
            s = Math.Min(s, 2);
            _zoom = s;
            _pan = new Point((size.Width - b.Width * s) / 2, (size.Height - b.Height * s) / 2);
        }
        return (_zoom, new Point(_pan.X - b.Left * _zoom, _pan.Y - b.Top * _zoom));
    }

    /// <summary>A point of the control in diagram units.</summary>
    public Point ToDiagram(Point screen)
    {
        (double s, Point o) = Transform(Bounds.Size);
        return new Point((screen.X - o.X) / s, (screen.Y - o.Y) / s);
    }

    /// <summary>A point of the diagram in control units.</summary>
    public Point ToScreen(double x, double y)
    {
        (double s, Point o) = Transform(Bounds.Size);
        return new Point(x * s + o.X, y * s + o.Y);
    }

    // ------------------------------------------------------------------ pointer

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is not { } vm || vm.Diagram is null) return;
        Point p = ToDiagram(e.GetPosition(this));
        _ = Transform(Bounds.Size);
        SldHit hit = vm.SelectAt(p.X, p.Y);
        if (hit is { Kind: SldHitKind.Symbol } && hit.Id.StartsWith("bus/", StringComparison.Ordinal) && vm.CanMove && vm.Diagram.Geometry.Symbol(hit.Id) is { } s)
        {
            _dragging = true;
            _grab = new Point(p.X - s.Box.CentreX, p.Y - s.Box.CentreY);
            _dragAt = p;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        _dragAt = ToDiagram(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || ViewModel is not { } vm) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Point p = ToDiagram(e.GetPosition(this));
        vm.MoveSelectedTo(p.X - _grab.X, p.Y - _grab.Y);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewModel?.Diagram is null) return;
        _ = Transform(Bounds.Size);
        _fit = false;
        Point at = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double next = Math.Clamp(_zoom * factor, 0.05, 20);
        // Keep the diagram point under the pointer where it is.
        _pan = new Point(at.X - (at.X - _pan.X) * next / _zoom, at.Y - (at.Y - _pan.Y) * next / _zoom);
        _zoom = next;
        InvalidateVisual();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ painting

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        PaintedObjects = 0;
        context.DrawRectangle(Brushes.White, null, new Rect(Bounds.Size));
        if (ViewModel?.Diagram is not { } d) return;
        SldColourSet? colours = ViewModel.Colours;
        (double s, Point o) = Transform(Bounds.Size);
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(o.X, o.Y)))
            Paint(context, d.Geometry, colours, ViewModel.SelectedId);
        if (_dragging && ViewModel.SelectedId is { } id && d.Geometry.Symbol(id) is { } sym)
        {
            using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(o.X, o.Y)))
                context.DrawRectangle(null, new ImmutablePen(Selection, 1.5 / s, new ImmutableDashStyle([3, 2], 0)),
                    new Rect(_dragAt.X - _grab.X - sym.Box.Width / 2, _dragAt.Y - _grab.Y - sym.Box.Height / 2, sym.Box.Width, sym.Box.Height));
        }
    }

    private static readonly ImmutableSolidColorBrush Selection = new(Color.Parse("#ff4500"));

    private static ImmutableSolidColorBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    private void Paint(DrawingContext dc, SldGeometry g, SldColourSet? colours, string? selected)
    {
        Typeface face = VisualDrawing.Face(this, FontWeight.Normal);
        for (int i = 0; i < g.Connections.Count; i++)
        {
            SldConnection c = g.Connections[i];
            string col = colours is not null && i < colours.Connections.Count ? colours.Connections[i] : SldColouring.Ink;
            var geo = new StreamGeometry();
            using (StreamGeometryContext sc = geo.Open())
            {
                sc.BeginFigure(new Point(c.Points[0].X, c.Points[0].Y), false);
                for (int k = 1; k < c.Points.Count; k++) sc.LineTo(new Point(c.Points[k].X, c.Points[k].Y));
                sc.EndFigure(false);
            }
            dc.DrawGeometry(null, new ImmutablePen(Brush(col), c.ElementId == selected ? 3 : 1.5), geo);
            PaintedObjects++;
        }
        for (int i = 0; i < g.Symbols.Count; i++)
        {
            SldSymbol sy = g.Symbols[i];
            string col = colours is not null && i < colours.Symbols.Count ? colours.Symbols[i] : SldColouring.Ink;
            Symbol(dc, sy, col, face);
            if (sy.Id == selected)
                dc.DrawRectangle(null, new ImmutablePen(Selection, 1.5), new Rect(sy.Box.Left - 2, sy.Box.Top - 2, sy.Box.Width + 4, sy.Box.Height + 4));
            PaintedObjects++;
        }
    }

    private static void Symbol(DrawingContext dc, SldSymbol s, string colour, Typeface face)
    {
        SldBox x = s.Box;
        ImmutableSolidColorBrush brush = Brush(colour);
        var pen = new ImmutablePen(brush, 1.5, s.InService ? null : new ImmutableDashStyle([3, 2], 0));
        var rect = new Rect(x.Left, x.Top, x.Width, x.Height);
        double cx = x.CentreX, cy = x.CentreY;
        switch (s.Kind)
        {
            case SldKind.Busbar:
                dc.DrawRectangle(brush, null, rect);
                Text(dc, s.Label, face, 9, x.CentreX, x.Top - 12, true);
                return;
            case SldKind.Line:
                dc.DrawRectangle(Brushes.White, pen, rect);
                break;
            case SldKind.Transformer:
            {
                double r = Math.Min(x.Width / 2, x.Height * 0.3);
                dc.DrawEllipse(Brushes.White, pen, new Point(cx, x.Top + x.Height * 0.35), r, r);
                dc.DrawEllipse(Brushes.White, pen, new Point(cx, x.Top + x.Height * 0.65), r, r);
                break;
            }
            case SldKind.Breaker:
            case SldKind.LoadBreak:
                dc.DrawRectangle(s.InService ? brush : Brushes.White, pen, rect);
                if (s.Kind == SldKind.LoadBreak) dc.DrawLine(new ImmutablePen(Brush("#ffffff"), 1), new Point(x.Left, x.Bottom), new Point(x.Right, x.Top));
                break;
            case SldKind.Disconnector:
                dc.DrawLine(new ImmutablePen(brush, 2), new Point(cx, x.Top), new Point(s.InService ? cx : x.Right, x.Bottom));
                break;
            case SldKind.Generator:
                dc.DrawEllipse(Brushes.White, pen, new Point(cx, cy), x.Width / 2, x.Width / 2);
                Text(dc, "G", face, 9, cx, cy - 6, true);
                break;
            case SldKind.Load:
            {
                var geo = new StreamGeometry();
                using (StreamGeometryContext sc = geo.Open())
                {
                    sc.BeginFigure(new Point(x.Left, x.Top), true);
                    sc.LineTo(new Point(x.Right, x.Top));
                    sc.LineTo(new Point(cx, x.Bottom));
                    sc.EndFigure(true);
                }
                dc.DrawGeometry(brush, null, geo);
                break;
            }
            case SldKind.Shunt:
                dc.DrawLine(new ImmutablePen(brush, 2), new Point(x.Left, cy - 2), new Point(x.Right, cy - 2));
                dc.DrawLine(new ImmutablePen(brush, 2), new Point(x.Left, cy + 2), new Point(x.Right, cy + 2));
                break;
            case SldKind.EarthSwitch:
                for (int k = 0; k < 3; k++)
                {
                    double w2 = x.Width / 2 * (1 - 0.3 * k), yy = x.Top + 3 + k * 3;
                    dc.DrawLine(pen, new Point(cx - w2, yy), new Point(cx + w2, yy));
                }
                return;
        }
        Text(dc, s.Label, face, 9, x.Right + 3, cy - 6, false);
    }

    private static void Text(DrawingContext dc, string text, Typeface face, double size, double x, double top, bool centred)
    {
        FormattedText ft = VisualDrawing.Text(text, face, size, Brush(SldColouring.Ink));
        dc.DrawText(ft, new Point(centred ? x - ft.Width / 2 : x, top));
    }
}
