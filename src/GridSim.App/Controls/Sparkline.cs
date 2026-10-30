using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// A tiny live trace, the asset pages' "recent history" strip (UI programme Phase 1.3, the port of the WPF
/// <c>Visuals/Sparkline.cs</c>): a value series as one polyline with a soft fill, auto-scaled to its own minimum and
/// maximum (padded so a flat series still draws mid-band). No axes, no chrome: context, not measurement.
///
/// <para>It has the WPF control's properties under the same names and defaults (Values, Stroke, ShowMinMax, Caption), so
/// the dashboard port binds it the same way. Every number it draws comes from <see cref="SparklineGeometry"/> in
/// GridSim.Ui, the arithmetic the WPF sparkline draws from too.</para>
/// </summary>
public sealed class Sparkline : Control
{
    /// <summary>The series to draw, oldest first. Fewer than two values draws nothing. Replace the list to redraw (an
    /// in-place change to the same list is not seen, as in WPF).</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>The trace colour; the soft fill is the same colour at <see cref="SparklineGeometry.FillOpacity"/>.
    /// Defaults to the accent.</summary>
    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush>(nameof(Stroke),
            new ImmutableSolidColorBrush(VisualDrawing.ToColor(SparklineGeometry.DefaultStroke)));

    /// <summary>When true, draw faint dashed reference lines at the series' current min and max, each with a small
    /// right-aligned value label. They track new extremes as the series rolls.</summary>
    public static readonly StyledProperty<bool> ShowMinMaxProperty = AvaloniaProperty.Register<Sparkline, bool>(nameof(ShowMinMax));

    /// <summary>A small dim corner watermark. Defaults to "SIMULATED" because every series this control renders is
    /// synthetic (sim output, and the asset traces seed flat history on first sight): the label keeps it from being read
    /// as measured or solved data. Set "" (or null) to suppress.</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<Sparkline, string?>(nameof(Caption), SparklineGeometry.DefaultCaption);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, ShowMinMaxProperty, CaptionProperty);
    }

    /// <inheritdoc cref="ValuesProperty"/>
    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <inheritdoc cref="StrokeProperty"/>
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <inheritdoc cref="ShowMinMaxProperty"/>
    public bool ShowMinMax { get => GetValue(ShowMinMaxProperty); set => SetValue(ShowMinMaxProperty, value); }

    /// <inheritdoc cref="CaptionProperty"/>
    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    // The stroke pen and soft fill, rebuilt only when the Stroke brush changes (the WPF control cached them the same way).
    private IPen? _linePen;
    private IBrush? _lineFill;
    private IBrush? _penFor;

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        SparklineScene? s = SparklineGeometry.Compute(Values, Bounds.Width, Bounds.Height);
        if (s is null) return;

        IBrush stroke = Stroke;
        if (_linePen is null || !ReferenceEquals(_penFor, stroke))
        {
            _linePen = new ImmutablePen(stroke.ToImmutable(), SparklineGeometry.LineThickness, lineJoin: PenLineJoin.Round);
            // WPF cloned the brush and set its opacity to 0.12 (replacing, not multiplying, the brush's own opacity);
            // a solid colour brush does exactly that here. Any other brush keeps its own opacity times 0.12.
            _lineFill = stroke is ISolidColorBrush solid
                ? new ImmutableSolidColorBrush(solid.Color, SparklineGeometry.FillOpacity)
                : null;
            _penFor = stroke;
        }

        StreamGeometry? fill = VisualDrawing.Poly(s.FillPolygon, closed: true);
        StreamGeometry? line = VisualDrawing.Poly(s.Points, closed: false);
        if (fill is not null)
        {
            if (_lineFill is not null) context.DrawGeometry(_lineFill, null, fill);
            else
                using (context.PushOpacity(SparklineGeometry.FillOpacity))
                    context.DrawGeometry(stroke, null, fill);
        }
        if (line is not null) context.DrawGeometry(null, _linePen, line);

        // Faint min/max reference lines that ride with the series' rolling extremes.
        if (ShowMinMax)
        {
            IPen refPen = VisualDrawing.Pen(SparklineGeometry.RefPen);
            VisualDrawing.Line(context, refPen, new ScreenPoint(0, s.MaxLineY), new ScreenPoint(s.Width, s.MaxLineY));
            VisualDrawing.Line(context, refPen, new ScreenPoint(0, s.MinLineY), new ScreenPoint(s.Width, s.MinLineY));
            Typeface face = VisualDrawing.Face(this, FontWeight.SemiBold);
            IBrush label = VisualDrawing.Brush(SparklineGeometry.RefLabel);
            DrawRefLabel(context, VisualDrawing.Text(s.MaxLabel, face, SparklineGeometry.RefLabelFontSize, label), s.Width, s.MaxLabelY);
            DrawRefLabel(context, VisualDrawing.Text(s.MinLabel, face, SparklineGeometry.RefLabelFontSize, label), s.Width, s.MinLabelY);
        }

        // "SIMULATED" (or caller-set) watermark: marks these synthetic or seeded traces as not measured.
        string? caption = Caption;
        if (!string.IsNullOrEmpty(caption))
            VisualDrawing.DrawAt(context, VisualDrawing.Text(caption, VisualDrawing.Face(this, FontWeight.SemiBold),
                SparklineGeometry.CaptionFontSize, VisualDrawing.Brush(SparklineGeometry.Badge)), SparklineGeometry.CaptionAt);
    }

    private static void DrawRefLabel(DrawingContext dc, FormattedText ft, double w, double y)
        => VisualDrawing.DrawAt(dc, ft, new ScreenPoint(SparklineGeometry.RefLabelLeft(w, ft.Width), y));
}
