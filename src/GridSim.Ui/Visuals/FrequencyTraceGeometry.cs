using System.Globalization;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>A vertical time gridline of the frequency chart and its label ("5s").</summary>
/// <param name="X">The gridline's x.</param>
/// <param name="Label">The label, centred under the gridline.</param>
public readonly record struct FrequencyTimeTick(double X, string Label);

/// <summary>A horizontal limit line of the frequency chart (nominal, operational, statutory, LFDD).</summary>
/// <param name="Y">The line's y.</param>
/// <param name="Label">The Hz label at the left edge ("49.8").</param>
/// <param name="Kind">The Core kind: "nom", "oper", "stat" or "lfdd".</param>
/// <param name="Pen">The pen for that kind.</param>
public readonly record struct FrequencyLimitLine(double Y, string Label, string Kind, VisualPen Pen);

/// <summary>One response drawn on the chart: the screen points (clamped to the scale) and the clipped extreme.</summary>
/// <param name="Points">The trace, in time order, each frequency clamped to the chart's scale.</param>
/// <param name="OffScaleHz">The most extreme frequency outside the scale (the lowest below the floor, else the highest
/// above the ceiling), or null when the whole curve fits.</param>
public sealed record FrequencyCurve(IReadOnlyList<ScreenPoint> Points, double? OffScaleHz);

/// <summary>The note printed for a clipped curve, and where.</summary>
/// <param name="Text">The note ("baseline reaches 48.10 Hz, below the 48.6 Hz floor: drawn along the edge").</param>
/// <param name="Y">The top of the note; it starts at <see cref="FrequencyTraceGeometry.NoteLeft"/>.</param>
/// <param name="IsBaseline">True for the baseline's note (grey), false for the "+ TSGB" note (green).</param>
public readonly record struct FrequencyOffScaleNote(string Text, double Y, bool IsBaseline);

/// <summary>
/// Everything the frequency strip chart draws, worked out from its size and its two responses (see
/// <see cref="FrequencyTraceGeometry.Compute"/>).
/// </summary>
public sealed class FrequencyTraceScene
{
    /// <summary>The control width.</summary>
    public double Width { get; init; }

    /// <summary>The control height.</summary>
    public double Height { get; init; }

    /// <summary>The bottom of the vertical scale (Hz): 48.6 Hz about the system's nominal.</summary>
    public double FMin { get; init; }

    /// <summary>The top of the vertical scale (Hz): 50.5 Hz about the system's nominal.</summary>
    public double FMax { get; init; }

    /// <summary>The plot area's width (the control less the left and right pads).</summary>
    public double PlotWidth { get; init; }

    /// <summary>The plot area's height (the control less the top and bottom pads).</summary>
    public double PlotHeight { get; init; }

    /// <summary>The time span shown (seconds): the longer response's span, or 30 when there is none.</summary>
    public double Duration { get; init; }

    /// <summary>The vertical gridlines, every 5 seconds from 0.</summary>
    public IReadOnlyList<FrequencyTimeTick> TimeTicks { get; init; } = Array.Empty<FrequencyTimeTick>();

    /// <summary>The horizontal limit lines (the one Core list, <c>TripWhatIf.ReferenceLines</c>), in that order.</summary>
    public IReadOnlyList<FrequencyLimitLine> LimitLines { get; init; } = Array.Empty<FrequencyLimitLine>();

    /// <summary>The baseline trace, or null when there is none to draw.</summary>
    public FrequencyCurve? Baseline { get; init; }

    /// <summary>The "+ TSGB" trace, or null when there is none to draw.</summary>
    public FrequencyCurve? Tsgb { get; init; }

    /// <summary>The off-scale notes, baseline first, stacked upward from the bottom of the plot.</summary>
    public IReadOnlyList<FrequencyOffScaleNote> Notes { get; init; } = Array.Empty<FrequencyOffScaleNote>();

    /// <summary>True when the legend is drawn (either response is present, even one too short to plot).</summary>
    public bool ShowLegend { get; init; }

    /// <summary>The legend's left edge (its sample lines start here).</summary>
    public double LegendX { get; init; }

    /// <summary>The legend's top edge.</summary>
    public double LegendY { get; init; }

    /// <summary>The screen point of time <paramref name="t"/> (seconds after the event) and frequency
    /// <paramref name="f"/> (Hz). Not clamped: the caller clamps the frequency first.</summary>
    public ScreenPoint Map(double t, double f) => FrequencyTraceGeometry.Map(t, f, Duration, PlotWidth, PlotHeight, FMin, FMax);
}

/// <summary>
/// The pure geometry of the frequency strip chart (UI programme Phase 1.3): frequency (Hz) against time after an event,
/// with the GB limit lines drawn in, overlaying the grey baseline and the green "+ TSGB" trace.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/FrequencyTrace.cs</c>, moved here operation for operation (same
/// expressions, same order; the label texts are the same format strings), so the WPF control and the Avalonia control
/// draw the same numbers. <c>UiVisualsParityTests</c> pins it against a verbatim copy of the old WPF code, bit for bit
/// and string for string.</para>
/// </summary>
public static class FrequencyTraceGeometry
{
    /// <summary>The GB bottom of the scale (Hz), so the 48.8 Hz LFDD line stays on-scale; moved about the nominal.</summary>
    public const double FMinGbHz = 48.6;

    /// <summary>The GB top of the scale (Hz): the statutory over-frequency limit, so an over-frequency excursion is not
    /// flat-topped at 50.2; moved about the nominal.</summary>
    public const double FMaxGbHz = 50.5;

    /// <summary>Left pad (room for the Hz labels).</summary>
    public const double PadL = 38;

    /// <summary>Right pad.</summary>
    public const double PadR = 12;

    /// <summary>Top pad.</summary>
    public const double PadT = 10;

    /// <summary>Bottom pad (room for the time labels).</summary>
    public const double PadB = 20;

    /// <summary>Below this width (pixels) only the background is drawn.</summary>
    public const double MinWidth = 60;

    /// <summary>Below this height (pixels) only the background is drawn.</summary>
    public const double MinHeight = 50;

    /// <summary>The time-gridline spacing (seconds).</summary>
    public const double TimeStepS = 5;

    /// <summary>Font size of the time labels.</summary>
    public const double TimeFontSize = 9.0;

    /// <summary>Font size of the Hz labels, the notes and the legend.</summary>
    public const double LabelFontSize = 9.5;

    /// <summary>The x where the Hz labels start.</summary>
    public const double HzLabelLeft = 2;

    /// <summary>The x where the off-scale notes start.</summary>
    public const double NoteLeft = PadL + 4;

    /// <summary>The time span shown when there is no response to size it (seconds).</summary>
    public const double DefaultDurationS = 30;

    // ---- the colour tokens (the same hex values the WPF chart's pens and brushes were built from) ----

    /// <summary>The chart background.</summary>
    public static readonly VisualFill Background = new(ColourRamps.FromHex("#0B1117"));

    /// <summary>The axis labels.</summary>
    public static readonly VisualFill AxisText = new(ColourRamps.FromHex("#8794A2"));

    /// <summary>The baseline's legend and note text.</summary>
    public static readonly VisualFill BaselineText = new(ColourRamps.FromHex("#9AA7B4"));

    /// <summary>The "+ TSGB" legend and note text.</summary>
    public static readonly VisualFill TsgbText = new(ColourRamps.FromHex("#46C46A"));

    /// <summary>The time gridlines.</summary>
    public static readonly VisualPen GridPen = new(ColourRamps.FromHex("#1A232D"), 1.0);

    /// <summary>The nominal line.</summary>
    public static readonly VisualPen NominalPen = new(ColourRamps.FromHex("#3A4756"), 1.0);

    /// <summary>The operational limit line (dashed).</summary>
    public static readonly VisualPen OperPen = new(ColourRamps.FromHex("#E0B341"), 1.0, Dash: 4);

    /// <summary>The statutory limit line (dashed).</summary>
    public static readonly VisualPen StatPen = new(ColourRamps.FromHex("#E8833A"), 1.2, Dash: 4);

    /// <summary>The LFDD line (dashed).</summary>
    public static readonly VisualPen LfddPen = new(ColourRamps.FromHex("#E2554B"), 1.2, Dash: 4);

    /// <summary>The baseline trace.</summary>
    public static readonly VisualPen BasePen = new(ColourRamps.FromHex("#9AA7B4"), 2.0);

    /// <summary>The "+ TSGB" trace.</summary>
    public static readonly VisualPen TsgbPen = new(ColourRamps.FromHex("#46C46A"), 2.2);

    /// <summary>The pen for a Core reference-line kind ("nom", "oper", "stat"; anything else is the LFDD pen, as the
    /// old WPF switch had it).</summary>
    public static VisualPen PenForKind(string kind) => kind switch
    {
        "nom" => NominalPen,
        "oper" => OperPen,
        "stat" => StatPen,
        _ => LfddPen,
    };

    /// <summary>The bottom of the scale about the system's nominal.</summary>
    public static double FMin(FrequencyBands bands) => FrequencyBands.GbEdgeAbout(FMinGbHz, bands.NominalHz);

    /// <summary>The top of the scale about the system's nominal.</summary>
    public static double FMax(FrequencyBands bands) => FrequencyBands.GbEdgeAbout(FMaxGbHz, bands.NominalHz);

    /// <summary>The old WPF <c>Map</c>: time and frequency to a screen point.</summary>
    public static ScreenPoint Map(double t, double f, double dur, double plotW, double plotH, double fMin, double fMax) =>
        new(PadL + (dur <= 0 ? 0 : t / dur) * plotW, PadT + (fMax - f) / (fMax - fMin) * plotH);

    /// <summary>The time span shown: the longer response's span, or <see cref="DefaultDurationS"/> when neither has one.</summary>
    public static double Duration(FrequencyResponse? baseline, FrequencyResponse? tsgb)
    {
        double d = Math.Max(Span(baseline), Span(tsgb));
        return d <= 0 ? DefaultDurationS : d;

        static double Span(FrequencyResponse? r) =>
            r is null || r.Points.Count < 2 ? 0 : (r.Points[^1].Time - r.Points[0].Time).TotalSeconds;
    }

    /// <summary>
    /// The text for a clipped curve: its real extreme and that it is drawn along the chart's edge (the old WPF
    /// <c>DrawOffScale</c> text, the same formats).
    /// </summary>
    public static string OffScaleText(string which, double hz, double fMin, double fMax)
    {
        string edge = hz < fMin ? $"below the {fMin.ToString("0.0", CultureInfo.InvariantCulture)} Hz floor"
                                : $"above the {fMax.ToString("0.0", CultureInfo.InvariantCulture)} Hz ceiling";
        return $"{which} reaches {hz.ToString("0.00", CultureInfo.InvariantCulture)} Hz, {edge}: drawn along the edge";
    }

    /// <summary>
    /// Work out the chart for a control of <paramref name="w"/> by <paramref name="h"/> pixels, or null when it is
    /// smaller than <see cref="MinWidth"/> by <see cref="MinHeight"/> (then only the background is drawn).
    /// </summary>
    /// <param name="w">Control width.</param>
    /// <param name="h">Control height.</param>
    /// <param name="baseline">The baseline response, or null.</param>
    /// <param name="tsgb">The "+ TSGB" response, or null.</param>
    /// <param name="bands">The system's frequency bands; null means GB (<see cref="FrequencyBands.Gb"/>).</param>
    public static FrequencyTraceScene? Compute(double w, double h, FrequencyResponse? baseline, FrequencyResponse? tsgb,
        FrequencyBands? bands)
    {
        FrequencyBands b = bands ?? FrequencyBands.Gb;
        if (!(w >= MinWidth) || !(h >= MinHeight)) return null;   // NaN sizes refused too (2026-10-24)

        double fMin = FMin(b), fMax = FMax(b);
        double plotW = w - PadL - PadR, plotH = h - PadT - PadB;
        double dur = Duration(baseline, tsgb);

        // x gridlines + time labels (every 5 s); the label format is the old one, verbatim
        var ticks = new List<FrequencyTimeTick>();
        for (double t = 0; t <= dur + 0.01; t += TimeStepS)
        {
            double x = Map(t, fMax, dur, plotW, plotH, fMin, fMax).X;
            ticks.Add(new FrequencyTimeTick(x, $"{t:0}s"));
        }

        // horizontal limit lines + Hz labels: the one Core list (N297, TripWhatIf.ReferenceLines), the same lines the web
        // chart draws, about the system's nominal (50.0 / 49.8 / 49.5 / 48.8 for a GB case).
        var lines = new List<FrequencyLimitLine>();
        foreach (TripWhatIf.ReferenceLine rl in TripWhatIf.ReferenceLines(b))
            lines.Add(new FrequencyLimitLine(Map(0, rl.Hz, dur, plotW, plotH, fMin, fMax).Y, rl.Label, rl.Kind, PenForKind(rl.Kind)));

        FrequencyCurve? baseCurve = Curve(baseline, dur, plotW, plotH, fMin, fMax);
        FrequencyCurve? tsgbCurve = Curve(tsgb, dur, plotW, plotH, fMin, fMax);

        // A curve that leaves the chart's scale is drawn along the edge, so say so with the real extreme, never let the
        // edge read as the nadir: a 48.1 Hz nadir used to draw as a flat line on the 48.6 Hz floor with no mark.
        var notes = new List<FrequencyOffScaleNote>(2);
        double noteY = h - PadB - 14;
        if (baseCurve?.OffScaleHz is double bo) { notes.Add(new(OffScaleText("baseline", bo, fMin, fMax), noteY, true)); noteY -= 13; }
        if (tsgbCurve?.OffScaleHz is double to) notes.Add(new(OffScaleText("+ TSGB", to, fMin, fMax), noteY, false));

        return new FrequencyTraceScene
        {
            Width = w,
            Height = h,
            FMin = fMin,
            FMax = fMax,
            PlotWidth = plotW,
            PlotHeight = plotH,
            Duration = dur,
            TimeTicks = ticks,
            LimitLines = lines,
            Baseline = baseCurve,
            Tsgb = tsgbCurve,
            Notes = notes,
            ShowLegend = baseline is not null || tsgb is not null,
            LegendX = w - PadR - 92,
            LegendY = PadT + 2,
        };
    }

    /// <summary>
    /// One response as screen points, each frequency clamped to the scale, with the most extreme frequency that fell
    /// outside the scale (the old WPF <c>DrawCurve</c>); null when there is no response or it has fewer than two points.
    /// </summary>
    public static FrequencyCurve? Curve(FrequencyResponse? r, double dur, double plotW, double plotH, double fMin, double fMax)
    {
        if (r is null || r.Points.Count < 2) return null;
        DateTime t0 = r.Points[0].Time;
        var pts = new List<ScreenPoint>(r.Points.Count);
        double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
        foreach (FrequencyPoint p in r.Points)
        {
            double t = (p.Time - t0).TotalSeconds;
            if (p.Hz < lowest) lowest = p.Hz;
            if (p.Hz > highest) highest = p.Hz;
            double f = Math.Clamp(p.Hz, fMin, fMax);
            pts.Add(Map(t, f, dur, plotW, plotH, fMin, fMax));
        }
        return new FrequencyCurve(pts, lowest < fMin ? lowest : highest > fMax ? highest : null);
    }
}
