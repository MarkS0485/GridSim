// TEST ORACLE (UI programme 1.3): src/GridSim.Wpf/Visuals/Sparkline.cs exactly as it was at commit f3559445, before its maths
// TEST ORACLE moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the WPF types are the recording
// TEST ORACLE stand-ins in UiVisualsWpfShim.cs); UiVisualsParityTests checks a SHA-256 of the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldWpfVisuals;

/// <summary>
/// A tiny live trace  -  the asset pages' "recent history" strip. Renders a value series
/// as a single polyline with a soft fill, auto-scaled to its own min/max (padded so a
/// flat series still draws mid-band). No axes, no chrome: context, not measurement.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x21, 0xC7, 0xE6)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>When true, draw faint dashed reference lines at the series' current min and max,
    /// each with a small right-aligned value label. They track new extremes as the series rolls.</summary>
    public static readonly DependencyProperty ShowMinMaxProperty = DependencyProperty.Register(
        nameof(ShowMinMax), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool ShowMinMax
    {
        get => (bool)GetValue(ShowMinMaxProperty);
        set => SetValue(ShowMinMaxProperty, value);
    }

    /// <summary>A small dim corner watermark. Defaults to "SIMULATED" because every series this
    /// control renders is synthetic (sim output, and the asset traces seed flat history on first
    /// sight)  -  the label keeps it from being read as measured/solved data. Set "" to suppress.</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Sparkline),
        new FrameworkPropertyMetadata("SIMULATED", FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private static readonly Pen RefPen = MakeRefPen();
    private static readonly Brush RefLabel = Palette.Frozen(Palette.FromHex("#8492A0"));
    private static readonly Brush BadgeBrush = MakeBadgeBrush();

    // Cached stroke pen + soft fill, rebuilt only when the Stroke brush changes (thickness/opacity
    // are constant). Was a fresh unfrozen Pen + a cloned brush every OnRender.
    private Pen? _linePen;
    private Brush? _lineFill;
    private Brush? _penFor;

    private static Brush MakeBadgeBrush()
    {
        var b = new SolidColorBrush(Palette.FromHex("#8492A0")) { Opacity = 0.6 };
        b.Freeze();
        return b;
    }
    private static readonly Typeface RefFace =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static Pen MakeRefPen()
    {
        Brush b = new SolidColorBrush(Palette.FromHex("#3A4756")) { Opacity = 0.7 };
        b.Freeze();
        var p = new Pen(b, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<double>? v = Values;
        double w = ActualWidth, h = ActualHeight;
        if (v is null || v.Count < 2 || w < 4 || h < 4) return;

        double dataMin = v.Min(), dataMax = v.Max();
        double min = dataMin, max = dataMax;
        double pad = Math.Max((max - min) * 0.15, 1e-9);     // flat series -> centred line
        min -= pad; max += pad;

        var pts = new Point[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            double x = i / (double)(v.Count - 1) * w;
            double y = h - (v[i] - min) / (max - min) * h;
            pts[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (StreamGeometryContext lc = line.Open())
        {
            lc.BeginFigure(pts[0], false, false);
            lc.PolyLineTo(pts, true, true);
        }
        using (StreamGeometryContext fc = fill.Open())
        {
            fc.BeginFigure(new Point(0, h), true, true);
            fc.LineTo(pts[0], false, false);
            fc.PolyLineTo(pts, false, false);
            fc.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        fill.Freeze();

        // Reuse a frozen pen + soft fill; rebuild only when the Stroke brush (colour) changes.
        if (_linePen is null || !ReferenceEquals(_penFor, Stroke))
        {
            var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
            if (pen.CanFreeze) pen.Freeze();
            Brush soft = Stroke.Clone();
            soft.Opacity = 0.12;
            if (soft.CanFreeze) soft.Freeze();
            _linePen = pen;
            _lineFill = soft;
            _penFor = Stroke;
        }
        dc.DrawGeometry(_lineFill, null, fill);
        dc.DrawGeometry(null, _linePen, line);

        // Faint min/max reference lines that ride with the series' rolling extremes.
        if (ShowMinMax)
        {
            double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double yMax = h - (dataMax - min) / (max - min) * h;   // top line, series high
            double yMin = h - (dataMin - min) / (max - min) * h;   // bottom line, series low
            dc.DrawLine(RefPen, new Point(0, yMax), new Point(w, yMax));
            dc.DrawLine(RefPen, new Point(0, yMin), new Point(w, yMin));
            DrawRefLabel(dc, dataMax, w, yMax + 1, pps);           // just below the top line
            DrawRefLabel(dc, dataMin, w, yMin - 11, pps);          // just above the bottom line
        }

        // "SIMULATED" (or caller-set) watermark  -  marks these synthetic/seeded traces as not measured.
        if (!string.IsNullOrEmpty(Caption))
        {
            double badgePps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = new FormattedText(Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                RefFace, 8.5, BadgeBrush, badgePps);
            dc.DrawText(ft, new Point(2, 1));
        }
    }

    private static void DrawRefLabel(DrawingContext dc, double value, double w, double y, double pps)
    {
        var ft = new FormattedText(value.ToString("0.##", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, RefFace, 9, RefLabel, pps);
        dc.DrawText(ft, new Point(w - ft.Width - 2, y));
    }
}
