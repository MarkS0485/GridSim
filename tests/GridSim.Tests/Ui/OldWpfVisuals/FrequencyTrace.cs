// TEST ORACLE (UI programme 1.3): src/GridSim.Wpf/Visuals/FrequencyTrace.cs exactly as it was at commit f3559445, before its maths
// TEST ORACLE moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the WPF types are the recording
// TEST ORACLE stand-ins in UiVisualsWpfShim.cs); UiVisualsParityTests checks a SHA-256 of the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;

namespace GridSim.Tests.Ui.OldWpfVisuals;

/// <summary>
/// A control-room strip chart of a <see cref="FrequencyResponse"/>  -  frequency (Hz) against
/// time after an event  -  with the GB limit lines drawn in (operational 49.8, statutory 49.5,
/// LFDD 48.8). It overlays two traces so the TSGB compare reads at a glance: the grey
/// baseline dropping toward the limits, and the green "+ TSGB" trace held above them.
/// This is the RoCoF-app display, rebuilt on GridSim's own swing-equation model.
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

    // Upper bound is the statutory over-frequency limit (50.5 Hz), not 50.2: at 50.2 any
    // over-frequency excursion silently flat-topped against the ceiling. Lower bound stays at
    // 48.6 so the 48.8 LFDD line remains on-scale for the under-frequency case. N297: both about the system's nominal.
    private const double FMinGbHz = 48.6, FMaxGbHz = 50.5;
    private double FMin => FrequencyBands.GbEdgeAbout(FMinGbHz, _bands.NominalHz);
    private double FMax => FrequencyBands.GbEdgeAbout(FMaxGbHz, _bands.NominalHz);
    private const double PadL = 38, PadR = 12, PadT = 10, PadB = 20;

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
        if (w < 60 || h < 50) return;
        double pps = (double)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double plotW = w - PadL - PadR, plotH = h - PadT - PadB;
        double dur = Duration();
        Point Map(double t, double f) =>
            new(PadL + (dur <= 0 ? 0 : t / dur) * plotW, PadT + (FMax - f) / (FMax - FMin) * plotH);

        // x gridlines + time labels (every 5 s)
        for (double t = 0; t <= dur + 0.01; t += 5)
        {
            double x = Map(t, FMax).X;
            dc.DrawLine(GridPen, new Point(x, PadT), new Point(x, h - PadB));
            var ft = new FormattedText($"{t:0}s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.0, Axis, pps);
            dc.DrawText(ft, new Point(x - ft.Width / 2, h - PadB + 3));
        }

        // horizontal limit lines + Hz labels: the one Core list (N297, TripWhatIf.ReferenceLines), the same lines the web
        // chart draws, about the system's nominal (50.0 / 49.8 / 49.5 / 48.8 for a GB case).
        foreach (GridSim.Core.Presentation.TripWhatIf.ReferenceLine rl in GridSim.Core.Presentation.TripWhatIf.ReferenceLines(_bands))
            HLine(dc, Map, pps, rl.Hz, rl.Kind switch { "nom" => NominalPen, "oper" => OperPen, "stat" => StatPen, _ => LfddPen }, rl.Label);

        double? baseOff = DrawCurve(dc, _base, BasePen, Map);
        double? tsgbOff = DrawCurve(dc, _tsgb, TsgbPen, Map);

        // A curve that leaves the chart's scale is drawn along the edge, so say so with the real extreme, never let the
        // edge read as the nadir: a 48.1 Hz nadir used to draw as a flat line on the 48.6 Hz floor with no mark.
        double noteY = h - PadB - 14;
        if (baseOff is double bo) { DrawOffScale(dc, pps, "baseline", bo, BaseBrush, noteY); noteY -= 13; }
        if (tsgbOff is double to) DrawOffScale(dc, pps, "+ TSGB", to, TsgbBrush, noteY);

        if (_base is not null || _tsgb is not null)
        {
            double lx = w - PadR - 92, ly = PadT + 2;
            dc.DrawLine(BasePen, new Point(lx, ly + 5), new Point(lx + 16, ly + 5));
            dc.DrawText(new FormattedText("baseline", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, BaseBrush, pps), new Point(lx + 20, ly));
            dc.DrawLine(TsgbPen, new Point(lx, ly + 18), new Point(lx + 16, ly + 18));
            dc.DrawText(new FormattedText("+ TSGB", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, TsgbBrush, pps), new Point(lx + 20, ly + 13));
        }
    }

    private void HLine(DrawingContext dc, Func<double, double, Point> map, double pps, double f, Pen pen, string label)
    {
        double y = map(0, f).Y;
        dc.DrawLine(pen, new Point(PadL, y), new Point(ActualWidth - PadR, y));
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Axis, pps);
        dc.DrawText(ft, new Point(2, y - ft.Height / 2));
    }

    /// <summary>
    /// Draw one response, clamped to the chart's scale. Returns the most extreme frequency that fell OUTSIDE the scale
    /// (the lowest below the floor, else the highest above the ceiling), or null when the whole curve fits, so the
    /// caller can mark the clipping.
    /// </summary>
    private double? DrawCurve(DrawingContext dc, FrequencyResponse? r, Pen pen, Func<double, double, Point> map)
    {
        if (r is null || r.Points.Count < 2) return null;
        DateTime t0 = r.Points[0].Time;
        var g = new StreamGeometry();
        StreamGeometryContext c = g.Open();
        bool first = true;
        double fMin = FMin, fMax = FMax;   // N297: the scale about the system's nominal (instance, so no longer static)
        double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
        foreach (FrequencyPoint p in r.Points)
        {
            double t = (p.Time - t0).TotalSeconds;
            if (p.Hz < lowest) lowest = p.Hz;
            if (p.Hz > highest) highest = p.Hz;
            double f = Math.Clamp(p.Hz, fMin, fMax);
            Point pt = map(t, f);
            if (first) { c.BeginFigure(pt, false, false); first = false; }
            else c.LineTo(pt, true, false);
        }
        c.Close();
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        return lowest < fMin ? lowest : highest > fMax ? highest : null;
    }

    /// <summary>The note for a clipped curve: its real extreme and that it is drawn along the chart's edge.</summary>
    private void DrawOffScale(DrawingContext dc, double pps, string which, double hz, Brush brush, double y)
    {
        string edge = hz < FMin ? $"below the {FMin.ToString("0.0", CultureInfo.InvariantCulture)} Hz floor"
                                : $"above the {FMax.ToString("0.0", CultureInfo.InvariantCulture)} Hz ceiling";
        string text = $"{which} reaches {hz.ToString("0.00", CultureInfo.InvariantCulture)} Hz, {edge}: drawn along the edge";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, brush, pps);
        dc.DrawText(ft, new Point(PadL + 4, y));
    }

    private double Duration()
    {
        double d = Math.Max(Span(_base), Span(_tsgb));
        return d <= 0 ? 30 : d;

        static double Span(FrequencyResponse? r) =>
            r is null || r.Points.Count < 2 ? 0 : (r.Points[^1].Time - r.Points[0].Time).TotalSeconds;
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
