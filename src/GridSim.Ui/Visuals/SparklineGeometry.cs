using System.Globalization;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>
/// Everything the sparkline draws, worked out from its size and its series (see <see cref="SparklineGeometry.Compute"/>).
/// </summary>
public sealed class SparklineScene
{
    /// <summary>The control width the scene was worked out for.</summary>
    public double Width { get; init; }

    /// <summary>The control height the scene was worked out for.</summary>
    public double Height { get; init; }

    /// <summary>The trace, one point per value, left edge to right edge.</summary>
    public IReadOnlyList<ScreenPoint> Points { get; init; } = Array.Empty<ScreenPoint>();

    /// <summary>
    /// The soft fill under the trace as one closed polygon: the bottom-left corner, the first trace point, every trace
    /// point, then the bottom-right corner (the old WPF fill figure, point for point; the first trace point appears twice,
    /// as it did there).
    /// </summary>
    public IReadOnlyList<ScreenPoint> FillPolygon { get; init; } = Array.Empty<ScreenPoint>();

    /// <summary>The series' lowest value (NaN when any value is NaN, as <see cref="Enumerable.Min(IEnumerable{double})"/> gives).</summary>
    public double DataMin { get; init; }

    /// <summary>The series' highest value.</summary>
    public double DataMax { get; init; }

    /// <summary>The bottom of the vertical scale: the lowest value less a 15 percent pad (a flat series still draws mid-band).</summary>
    public double ScaleMin { get; init; }

    /// <summary>The top of the vertical scale: the highest value plus the same pad.</summary>
    public double ScaleMax { get; init; }

    /// <summary>The y of the faint reference line at the series' high.</summary>
    public double MaxLineY { get; init; }

    /// <summary>The y of the faint reference line at the series' low.</summary>
    public double MinLineY { get; init; }

    /// <summary>The label of the high line ("0.##", invariant).</summary>
    public string MaxLabel { get; init; } = "";

    /// <summary>The label of the low line ("0.##", invariant).</summary>
    public string MinLabel { get; init; } = "";

    /// <summary>The top of the high line's label (just below the line).</summary>
    public double MaxLabelY { get; init; }

    /// <summary>The top of the low line's label (just above the line).</summary>
    public double MinLabelY { get; init; }
}

/// <summary>
/// The pure geometry of the sparkline, the asset pages' "recent history" strip (UI programme Phase 1.3): a value series
/// as one polyline with a soft fill, auto-scaled to its own minimum and maximum (padded so a flat series still draws
/// mid-band), with optional faint min/max reference lines and a "SIMULATED" corner watermark.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/Sparkline.cs</c> <c>OnRender</c>, moved here operation for
/// operation (same expressions, same order), so the WPF control and the Avalonia control draw the same numbers.
/// <c>UiVisualsParityTests</c> pins it against a verbatim copy of the old WPF code, bit for bit.</para>
/// </summary>
public static class SparklineGeometry
{
    /// <summary>Below this width or height (pixels), or with fewer than two values, the sparkline draws nothing.</summary>
    public const double MinSize = 4;

    /// <summary>The trace's stroke width.</summary>
    public const double LineThickness = 1.6;

    /// <summary>The opacity of the soft fill under the trace (the stroke colour at this opacity).</summary>
    public const double FillOpacity = 0.12;

    /// <summary>The gap (pixels) between a reference label's right edge and the control's right edge.</summary>
    public const double RefLabelRightGap = 2;

    /// <summary>Font size of the reference labels.</summary>
    public const double RefLabelFontSize = 9;

    /// <summary>Font size of the corner watermark.</summary>
    public const double CaptionFontSize = 8.5;

    /// <summary>The watermark's top-left corner.</summary>
    public static readonly ScreenPoint CaptionAt = new(2, 1);

    /// <summary>The default watermark: every series this control renders is synthetic (sim output, seeded history).</summary>
    public const string DefaultCaption = "SIMULATED";

    // ---- the colour tokens (the same hex values the WPF sparkline's pens and brushes were built from) ----

    /// <summary>The default trace colour (the accent).</summary>
    public static readonly UiColour DefaultStroke = new(0xFF, 0x21, 0xC7, 0xE6);

    /// <summary>The faint dashed min/max reference lines.</summary>
    public static readonly VisualPen RefPen = new(ColourRamps.FromHex("#3A4756"), 1, 0.7, Dash: 3);

    /// <summary>The reference labels.</summary>
    public static readonly VisualFill RefLabel = new(ColourRamps.FromHex("#8492A0"));

    /// <summary>The corner watermark.</summary>
    public static readonly VisualFill Badge = new(ColourRamps.FromHex("#8492A0"), 0.6);

    /// <summary>
    /// Work out the sparkline for a control of <paramref name="w"/> by <paramref name="h"/> pixels, or null when there is
    /// nothing to draw (no series, fewer than two values, or a control smaller than <see cref="MinSize"/>).
    /// </summary>
    public static SparklineScene? Compute(IReadOnlyList<double>? v, double w, double h)
    {
        if (v is null || v.Count < 2 || !(w >= MinSize) || !(h >= MinSize)) return null;   // NaN sizes refused too (2026-10-24)

        double dataMin = v.Min(), dataMax = v.Max();
        double min = dataMin, max = dataMax;
        double pad = Math.Max((max - min) * 0.15, 1e-9);     // flat series -> centred line
        min -= pad; max += pad;

        var pts = new ScreenPoint[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            double x = i / (double)(v.Count - 1) * w;
            double y = h - (v[i] - min) / (max - min) * h;
            pts[i] = new ScreenPoint(x, y);
        }

        var fill = new ScreenPoint[pts.Length + 3];
        fill[0] = new ScreenPoint(0, h);
        fill[1] = pts[0];
        Array.Copy(pts, 0, fill, 2, pts.Length);
        fill[^1] = new ScreenPoint(w, h);

        // the faint min/max reference lines that ride with the series' rolling extremes
        double yMax = h - (dataMax - min) / (max - min) * h;   // top line, series high
        double yMin = h - (dataMin - min) / (max - min) * h;   // bottom line, series low
        return new SparklineScene
        {
            Width = w,
            Height = h,
            Points = pts,
            FillPolygon = fill,
            DataMin = dataMin,
            DataMax = dataMax,
            ScaleMin = min,
            ScaleMax = max,
            MaxLineY = yMax,
            MinLineY = yMin,
            MaxLabel = Label(dataMax),
            MinLabel = Label(dataMin),
            MaxLabelY = yMax + 1,                              // just below the top line
            MinLabelY = yMin - 11,                             // just above the bottom line
        };
    }

    /// <summary>A reference line's label: "0.##", invariant culture (the old WPF <c>DrawRefLabel</c> text).</summary>
    public static string Label(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The left edge of a right-aligned reference label of the given measured width: the old WPF expression
    /// <c>w - ft.Width - 2</c>, kept in this order so the result is the same double.
    /// </summary>
    public static double RefLabelLeft(double controlWidth, double labelWidth) => controlWidth - labelWidth - RefLabelRightGap;
}
