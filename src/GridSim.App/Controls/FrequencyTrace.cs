using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Core.Dynamics;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// A control-room strip chart of a <see cref="FrequencyResponse"/> (UI programme Phase 1.3, the port of the WPF
/// <c>Visuals/FrequencyTrace.cs</c>): frequency (Hz) against time after an event, with the GB limit lines drawn in
/// (operational 49.8, statutory 49.5, LFDD 48.8, about the system's nominal). It overlays two traces so the TSGB compare
/// reads at a glance: the grey baseline dropping toward the limits, and the green "+ TSGB" trace held above them.
///
/// <para>The WPF control is fed by one method, <see cref="SetResponses"/>, and this one keeps it with the same
/// signature. It also exposes the three inputs as styled properties (<see cref="Baseline"/>, <see cref="Tsgb"/>,
/// <see cref="Bands"/>) so the dashboard port can bind them; <see cref="SetResponses"/> simply sets all three. Every
/// number it draws comes from <see cref="FrequencyTraceGeometry"/> in GridSim.Ui, the arithmetic the WPF chart draws
/// from too.</para>
/// </summary>
public sealed class FrequencyTrace : Control
{
    /// <summary>The baseline response (grey), or null.</summary>
    public static readonly StyledProperty<FrequencyResponse?> BaselineProperty =
        AvaloniaProperty.Register<FrequencyTrace, FrequencyResponse?>(nameof(Baseline));

    /// <summary>The "+ TSGB" response (green), or null.</summary>
    public static readonly StyledProperty<FrequencyResponse?> TsgbProperty =
        AvaloniaProperty.Register<FrequencyTrace, FrequencyResponse?>(nameof(Tsgb));

    /// <summary>The bands of the system the traces belong to; null means GB (<see cref="FrequencyBands.Gb"/>), which
    /// draws the 50.0 / 49.8 / 49.5 / 48.8 lines on the 48.6 - 50.5 Hz scale.</summary>
    public static readonly StyledProperty<FrequencyBands?> BandsProperty =
        AvaloniaProperty.Register<FrequencyTrace, FrequencyBands?>(nameof(Bands));

    static FrequencyTrace()
    {
        AffectsRender<FrequencyTrace>(BaselineProperty, TsgbProperty, BandsProperty);
    }

    /// <inheritdoc cref="BaselineProperty"/>
    public FrequencyResponse? Baseline { get => GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }

    /// <inheritdoc cref="TsgbProperty"/>
    public FrequencyResponse? Tsgb { get => GetValue(TsgbProperty); set => SetValue(TsgbProperty, value); }

    /// <inheritdoc cref="BandsProperty"/>
    public FrequencyBands? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    /// <summary>Show a baseline and a "+ TSGB" response (either may be null) on the given bands (null = GB): the WPF
    /// control's one entry point, kept with the same signature.</summary>
    public void SetResponses(FrequencyResponse? baseline, FrequencyResponse? tsgb, FrequencyBands? bands = null)
    {
        Baseline = baseline;
        Tsgb = tsgb;
        Bands = bands;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        context.DrawRectangle(VisualDrawing.Brush(FrequencyTraceGeometry.Background), null, new Rect(0, 0, w, h));
        FrequencyTraceScene? s = FrequencyTraceGeometry.Compute(w, h, Baseline, Tsgb, Bands);
        if (s is null) return;

        Typeface face = VisualDrawing.Face(this, FontWeight.SemiBold);
        IBrush axis = VisualDrawing.Brush(FrequencyTraceGeometry.AxisText);

        // x gridlines + time labels (every 5 s), the label centred under its line
        IPen grid = VisualDrawing.Pen(FrequencyTraceGeometry.GridPen);
        foreach (FrequencyTimeTick t in s.TimeTicks)
        {
            VisualDrawing.Line(context, grid, new ScreenPoint(t.X, FrequencyTraceGeometry.PadT), new ScreenPoint(t.X, h - FrequencyTraceGeometry.PadB));
            FormattedText ft = VisualDrawing.Text(t.Label, face, FrequencyTraceGeometry.TimeFontSize, axis);
            VisualDrawing.DrawAt(context, ft, new ScreenPoint(t.X - ft.Width / 2, h - FrequencyTraceGeometry.PadB + 3));
        }

        // horizontal limit lines + Hz labels, each label vertically centred on its line at the left edge
        foreach (FrequencyLimitLine rl in s.LimitLines)
        {
            VisualDrawing.Line(context, VisualDrawing.Pen(rl.Pen),
                new ScreenPoint(FrequencyTraceGeometry.PadL, rl.Y), new ScreenPoint(w - FrequencyTraceGeometry.PadR, rl.Y));
            FormattedText ft = VisualDrawing.Text(rl.Label, face, FrequencyTraceGeometry.LabelFontSize, axis);
            VisualDrawing.DrawAt(context, ft, new ScreenPoint(FrequencyTraceGeometry.HzLabelLeft, rl.Y - ft.Height / 2));
        }

        // the two traces, baseline under "+ TSGB"
        DrawCurve(context, s.Baseline, FrequencyTraceGeometry.BasePen);
        DrawCurve(context, s.Tsgb, FrequencyTraceGeometry.TsgbPen);

        // the off-scale notes: a clipped curve's real extreme, never letting the edge read as the nadir
        foreach (FrequencyOffScaleNote n in s.Notes)
        {
            IBrush b = VisualDrawing.Brush(n.IsBaseline ? FrequencyTraceGeometry.BaselineText : FrequencyTraceGeometry.TsgbText);
            VisualDrawing.DrawAt(context, VisualDrawing.Text(n.Text, face, FrequencyTraceGeometry.LabelFontSize, b),
                new ScreenPoint(FrequencyTraceGeometry.NoteLeft, n.Y));
        }

        // the legend, top right
        if (s.ShowLegend)
        {
            double lx = s.LegendX, ly = s.LegendY;
            VisualDrawing.Line(context, VisualDrawing.Pen(FrequencyTraceGeometry.BasePen), new ScreenPoint(lx, ly + 5), new ScreenPoint(lx + 16, ly + 5));
            VisualDrawing.DrawAt(context, VisualDrawing.Text("baseline", face, FrequencyTraceGeometry.LabelFontSize,
                VisualDrawing.Brush(FrequencyTraceGeometry.BaselineText)), new ScreenPoint(lx + 20, ly));
            VisualDrawing.Line(context, VisualDrawing.Pen(FrequencyTraceGeometry.TsgbPen), new ScreenPoint(lx, ly + 18), new ScreenPoint(lx + 16, ly + 18));
            VisualDrawing.DrawAt(context, VisualDrawing.Text("+ TSGB", face, FrequencyTraceGeometry.LabelFontSize,
                VisualDrawing.Brush(FrequencyTraceGeometry.TsgbText)), new ScreenPoint(lx + 20, ly + 13));
        }
    }

    private static void DrawCurve(DrawingContext dc, FrequencyCurve? curve, VisualPen pen)
    {
        if (curve is null) return;
        StreamGeometry? g = VisualDrawing.Poly(curve.Points, closed: false);
        if (g is not null) dc.DrawGeometry(null, VisualDrawing.Pen(pen), g);
    }
}
