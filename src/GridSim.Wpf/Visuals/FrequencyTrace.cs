using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GridSim.Core.Dynamics;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Wpf.Visuals;

/// <summary>
/// A control-room strip chart of a <see cref="FrequencyResponse"/>  -  frequency (Hz) against
/// time after an event  -  with the GB limit lines drawn in (operational 49.8, statutory 49.5,
/// LFDD 48.8). It overlays two traces so the TSGB compare reads at a glance: the grey
/// baseline dropping toward the limits, and the green "+ TSGB" trace held above them.
/// This is the RoCoF-app display, rebuilt on GridSim's own swing-equation model.
/// UI programme Phase 1.3: the chart's arithmetic (the scale about the nominal, the time span, the time-to-pixel map, the
/// gridlines and limit lines, the clamped curves, the off-scale notes and the legend position) moved to the
/// platform-neutral <see cref="FrequencyTraceGeometry"/> (src/GridSim.Ui/Visuals), operation for operation, so the
/// Avalonia chart draws from the same numbers; this control now only draws them and its pixels are unchanged
/// (UiVisualsParityTests pins the moved maths against a verbatim copy of the old code, bit for bit).
/// </summary>
public sealed class FrequencyTrace : FrameworkElement
{
    private FrequencyResponse? _base, _tsgb;
    // N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the bands of the system the traces belong to
    // (the GB bands about its nominal). The limit lines and the vertical scale follow them; FrequencyBands.Gb (the default)
    // draws exactly the old 50.0 / 49.8 / 49.5 / 48.8 lines on the old 48.6 - 50.5 Hz scale.
    private FrequencyBands _bands = FrequencyBands.Gb;

    public void SetResponses(FrequencyResponse? baseline, FrequencyResponse? tsgb, FrequencyBands? bands = null)
    {
        _base = baseline;
        _tsgb = tsgb;
        _bands = bands ?? FrequencyBands.Gb;
        InvalidateVisual();
    }

    // The scale (48.6 - 50.5 Hz about the system's nominal: the upper bound is the statutory over-frequency limit so an
    // over-frequency excursion is not flat-topped, the lower keeps the 48.8 LFDD line on-scale) and the pads now live in
    // FrequencyTraceGeometry (FMinGbHz, FMaxGbHz, PadL, PadR, PadT, PadB), unchanged.
    private const double PadL = FrequencyTraceGeometry.PadL, PadR = FrequencyTraceGeometry.PadR,
        PadT = FrequencyTraceGeometry.PadT, PadB = FrequencyTraceGeometry.PadB;

    private static readonly Brush Bg = Palette.Frozen(Palette.FromHex("#0B1117"));
    private static readonly Brush Axis = Palette.Frozen(Palette.FromHex("#8794A2"));
    private static readonly Brush BaseBrush = Palette.Frozen(Palette.FromHex("#9AA7B4"));
    private static readonly Brush TsgbBrush = Palette.Frozen(Palette.FromHex("#46C46A"));
    private static readonly Pen GridPen = MakePen("#1A232D", 1.0);
    private static readonly Pen NominalPen = MakePen("#3A4756", 1.0);
    private static readonly Pen OperPen = MakeDash("#E0B341", 1.0);
    private static readonly Pen StatPen = MakeDash("#E8833A", 1.2);
    private static readonly Pen LfddPen = MakeDash("#E2554B", 1.2);
    private static readonly Pen BasePen = MakePen("#9AA7B4", 2.0);
    private static readonly Pen TsgbPen = MakePen("#46C46A", 2.2);
    private static readonly Typeface Face =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        // The whole chart worked out by the shared geometry (null = too small to plot, as before: under 60 x 50).
        FrequencyTraceScene? s = FrequencyTraceGeometry.Compute(w, h, _base, _tsgb, _bands);
        if (s is null) return;
        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // x gridlines + time labels (every 5 s)
        foreach (FrequencyTimeTick tick in s.TimeTicks)
        {
            double x = tick.X;
            dc.DrawLine(GridPen, new Point(x, PadT), new Point(x, h - PadB));
            var ft = new FormattedText(tick.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.0, Axis, pps);
            dc.DrawText(ft, new Point(x - ft.Width / 2, h - PadB + 3));
        }

        // horizontal limit lines + Hz labels: the one Core list (N297, TripWhatIf.ReferenceLines), the same lines the web
        // chart draws, about the system's nominal (50.0 / 49.8 / 49.5 / 48.8 for a GB case).
        foreach (FrequencyLimitLine rl in s.LimitLines)
            HLine(dc, pps, rl.Y, rl.Kind switch { "nom" => NominalPen, "oper" => OperPen, "stat" => StatPen, _ => LfddPen }, rl.Label);

        DrawCurve(dc, s.Baseline, BasePen);
        DrawCurve(dc, s.Tsgb, TsgbPen);

        // A curve that leaves the chart's scale is drawn along the edge, so say so with the real extreme, never let the
        // edge read as the nadir: a 48.1 Hz nadir used to draw as a flat line on the 48.6 Hz floor with no mark.
        foreach (FrequencyOffScaleNote note in s.Notes)
            DrawOffScale(dc, pps, note.Text, note.IsBaseline ? BaseBrush : TsgbBrush, note.Y);

        if (s.ShowLegend)
        {
            double lx = s.LegendX, ly = s.LegendY;
            dc.DrawLine(BasePen, new Point(lx, ly + 5), new Point(lx + 16, ly + 5));
            dc.DrawText(new FormattedText("baseline", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, BaseBrush, pps), new Point(lx + 20, ly));
            dc.DrawLine(TsgbPen, new Point(lx, ly + 18), new Point(lx + 16, ly + 18));
            dc.DrawText(new FormattedText("+ TSGB", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, TsgbBrush, pps), new Point(lx + 20, ly + 13));
        }
    }

    private void HLine(DrawingContext dc, double pps, double y, Pen pen, string label)
    {
        dc.DrawLine(pen, new Point(PadL, y), new Point(ActualWidth - PadR, y));
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Axis, pps);
        dc.DrawText(ft, new Point(2, y - ft.Height / 2));
    }

    /// <summary>
    /// Draw one response, already clamped to the chart's scale by the geometry (which also found the most extreme
    /// frequency outside the scale, for the off-scale note). Nothing to draw for a missing or one-point response.
    /// </summary>
    private static void DrawCurve(DrawingContext dc, FrequencyCurve? curve, Pen pen)
    {
        if (curve is null) return;
        var g = new StreamGeometry();
        StreamGeometryContext c = g.Open();
        bool first = true;
        foreach (ScreenPoint p in curve.Points)
        {
            Point pt = new(p.X, p.Y);
            if (first) { c.BeginFigure(pt, false, false); first = false; }
            else c.LineTo(pt, true, false);
        }
        c.Close();
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>The note for a clipped curve (its text from the geometry): its real extreme and that it is drawn along
    /// the chart's edge.</summary>
    private static void DrawOffScale(DrawingContext dc, double pps, string text, Brush brush, double y)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, brush, pps);
        dc.DrawText(ft, new Point(FrequencyTraceGeometry.NoteLeft, y));
    }

    private static Pen MakePen(string hex, double t)
    {
        var p = new Pen(Palette.Frozen(Palette.FromHex(hex)), t);
        p.Freeze();
        return p;
    }

    private static Pen MakeDash(string hex, double t)
    {
        var p = new Pen(Palette.Frozen(Palette.FromHex(hex)), t) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        p.Freeze();
        return p;
    }
}
