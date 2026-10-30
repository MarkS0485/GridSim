using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>How close the operating point is to a capability boundary, which sets the dot's colour.</summary>
public enum PqStress
{
    /// <summary>Stress at most 0.85: the accent dot.</summary>
    Normal,
    /// <summary>Stress above 0.85 and at most 0.95: amber.</summary>
    Near,
    /// <summary>Stress above 0.95: red.</summary>
    AtLimit,
}

/// <summary>
/// Everything the P-Q capability diagram draws, worked out from its size and the machine's values (see
/// <see cref="PqDiagramGeometry.Compute"/>).
/// </summary>
public sealed class PqDiagramScene
{
    /// <summary>The plot box's left edge (Q = the scale minimum).</summary>
    public double PlotLeft { get; init; }

    /// <summary>The plot box's right edge (Q = the scale maximum).</summary>
    public double PlotRight { get; init; }

    /// <summary>The plot box's bottom edge (P = 0).</summary>
    public double PlotBottom { get; init; }

    /// <summary>The plot box's top edge (P = Pmax).</summary>
    public double PlotTop { get; init; }

    /// <summary>The Q scale minimum: Qmin, but never above -1.</summary>
    public double QMin { get; init; }

    /// <summary>The Q scale maximum: Qmax, but never below 1.</summary>
    public double QMax { get; init; }

    /// <summary>The P scale maximum: Pmax, but never below 1.</summary>
    public double PMax { get; init; }

    /// <summary>The Q span (at least 1).</summary>
    public double Span { get; init; }

    /// <summary>The x of the Q = 0 axis.</summary>
    public double ZeroQX { get; init; }

    /// <summary>
    /// The capability boundary as an open polyline: up the over-excited limit, along the thermal top, down the
    /// under-excited limit to 45 percent of Pmax, then the loss-of-excitation chamfer to the baseline at half Qmin.
    /// </summary>
    public IReadOnlyList<ScreenPoint> Boundary { get; init; } = Array.Empty<ScreenPoint>();

    /// <summary>The larger of the P and Q utilisations (can be NaN when a value is NaN).</summary>
    public double StressValue { get; init; }

    /// <summary>The stress band that colours the dot.</summary>
    public PqStress Stress { get; init; }

    /// <summary>The operating point (the dot's centre).</summary>
    public ScreenPoint Dot { get; init; }

    /// <summary>Top-left of the "MW" axis label.</summary>
    public ScreenPoint MwLabelAt { get; init; }

    /// <summary>Top-left of the "under-exc" label.</summary>
    public ScreenPoint UnderExcitedLabelAt { get; init; }

    /// <summary>Top-left of the "over-exc" label.</summary>
    public ScreenPoint OverExcitedLabelAt { get; init; }

    /// <summary>The x of reactive power <paramref name="q"/> (not clamped).</summary>
    public double QtoX(double q) => PqDiagramGeometry.QtoX(q, PlotLeft, PlotRight, QMin, Span);

    /// <summary>The y of real power <paramref name="p"/>, clamped to the plot box (0 to Pmax).</summary>
    public double PtoY(double p) => PqDiagramGeometry.PtoY(p, PlotBottom, PlotTop, PMax);
}

/// <summary>
/// The pure geometry of the simplified P-Q capability diagram for one synchronous machine (UI programme Phase 1.3): the
/// operating point as a dot against the thermal MW limit, the reactive export and absorption limits and the
/// loss-of-excitation corner, coloured by how close it is to any boundary.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/PqDiagram.cs</c> <c>OnRender</c>, moved here operation for
/// operation (same expressions, same order), so the WPF control and the Avalonia control draw the same numbers.
/// <c>UiVisualsParityTests</c> pins it against a verbatim copy of the old WPF code, bit for bit.</para>
/// </summary>
public static class PqDiagramGeometry
{
    /// <summary>Below this width or height (pixels) the diagram draws nothing.</summary>
    public const double MinSize = 20;

    /// <summary>Left margin (room for the "MW" label).</summary>
    public const double MarginLeft = 30;

    /// <summary>Right margin.</summary>
    public const double MarginRight = 14;

    /// <summary>Top margin.</summary>
    public const double MarginTop = 14;

    /// <summary>Bottom margin (room for the excitation labels).</summary>
    public const double MarginBottom = 22;

    /// <summary>Radius of the filled operating dot.</summary>
    public const double DotRadius = 5;

    /// <summary>Radius of the ring round the dot.</summary>
    public const double RingRadius = 9;

    /// <summary>Stroke width of the ring round the dot.</summary>
    public const double RingThickness = 1.5;

    /// <summary>Font size of the "MW" label.</summary>
    public const double MwFontSize = 9;

    /// <summary>Font size of the excitation labels.</summary>
    public const double ExcitationFontSize = 8;

    // ---- the colour tokens (the same hex values the WPF diagram's pens and brushes were built from) ----

    /// <summary>The capability boundary.</summary>
    public static readonly VisualPen Boundary = new(ColourRamps.FromHex("#46535F"), 1.6);

    /// <summary>The Q = 0 and P = 0 axes.</summary>
    public static readonly VisualPen Axis = new(ColourRamps.FromHex("#2A3742"), 1.0);

    /// <summary>The faint plot-box fill (white at alpha 0x12).</summary>
    public static readonly VisualFill Fill = new(ColourRamps.FromHex("#12FFFFFF"));

    /// <summary>The dot at normal stress (the accent).</summary>
    public static readonly VisualFill DotNormal = new(ColourRamps.FromHex("#21C7E6"));

    /// <summary>The dot near a limit (amber).</summary>
    public static readonly VisualFill DotNear = new(ColourRamps.FromHex("#F3C13A"));

    /// <summary>The dot at a limit (red).</summary>
    public static readonly VisualFill DotAtLimit = new(ColourRamps.FromHex("#FF5B5B"));

    /// <summary>The axis labels.</summary>
    public static readonly VisualFill Muted = new(ColourRamps.FromHex("#8492A0"));

    /// <summary>The dot colour for a stress band.</summary>
    public static VisualFill DotFill(PqStress s) => s switch
    {
        PqStress.AtLimit => DotAtLimit,
        PqStress.Near => DotNear,
        PqStress.Normal => DotNormal,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "not a P-Q stress band"),
    };

    /// <summary>The stress band of a stress value: above 0.95 at the limit, above 0.85 near it, else normal (a NaN
    /// stress is normal, as the old comparisons had it).</summary>
    public static PqStress StressOf(double stress) => stress > 0.95 ? PqStress.AtLimit : stress > 0.85 ? PqStress.Near : PqStress.Normal;

    /// <summary>The old WPF <c>QtoX</c>: reactive power to x across the plot box (not clamped).</summary>
    public static double QtoX(double q, double px0, double px1, double qmin, double span) => px0 + (q - qmin) / span * (px1 - px0);

    /// <summary>The old WPF <c>PtoY</c>: real power to y, clamped to the plot box (0 at the bottom, Pmax at the top).</summary>
    public static double PtoY(double p, double py0, double py1, double pmax) => py0 - Math.Clamp(p / pmax, 0, 1) * (py0 - py1);

    /// <summary>
    /// Work out the diagram for a control of <paramref name="w"/> by <paramref name="h"/> pixels, or null when it is
    /// smaller than <see cref="MinSize"/> either way (the old diagram drew nothing then).
    /// </summary>
    /// <param name="w">Control width.</param>
    /// <param name="h">Control height.</param>
    /// <param name="pg">The machine's real power output.</param>
    /// <param name="qg">The machine's reactive power output (signed).</param>
    /// <param name="qminLimit">The reactive absorption limit (negative).</param>
    /// <param name="qmaxLimit">The reactive export limit (positive).</param>
    /// <param name="pmaxLimit">The thermal real-power limit.</param>
    public static PqDiagramScene? Compute(double w, double h, double pg, double qg, double qminLimit, double qmaxLimit, double pmaxLimit)
    {
        // NaN fails every comparison, so "w < MinSize" let a NaN size through; written the other way round it is refused
        // (2026-10-24). Every finite size gives the same answer as before.
        if (!(w >= MinSize) || !(h >= MinSize)) return null;
        double ml = MarginLeft, mr = MarginRight, mt = MarginTop, mb = MarginBottom;   // margins (room for axis labels)
        double px0 = ml, px1 = w - mr, py0 = h - mb, py1 = mt;   // plot box; py0 = P=0 (bottom)

        double qmin = Math.Min(qminLimit, -1), qmax = Math.Max(qmaxLimit, 1);
        double pmax = Math.Max(pmaxLimit, 1);
        double span = Math.Max(qmax - qmin, 1);

        double QtoX(double q) => PqDiagramGeometry.QtoX(q, px0, px1, qmin, span);
        double PtoY(double p) => PqDiagramGeometry.PtoY(p, py0, py1, pmax);

        // capability boundary: thermal top (Pmax) + reactive limits (Qmin/Qmax) + a loss-of-excitation
        // chamfer in the under-excited (Qmin, low-P) corner.
        double xMin = QtoX(qmin), xMax = QtoX(qmax), yTop = PtoY(pmax);
        ScreenPoint[] boundary =
        {
            new(xMax, py0),
            new(xMax, yTop),                      // right (over-excited) limit
            new(xMin, yTop),                      // thermal top
            new(xMin, PtoY(pmax * 0.45)),         // down the under-excited limit
            new(QtoX(qmin * 0.5), py0),           // loss-of-excitation chamfer
        };

        // operating dot, coloured by proximity to any boundary
        double qUtil = qg >= 0 ? (qmax > 0 ? qg / qmax : 0) : (qmin < 0 ? qg / qmin : 0);
        double stress = Math.Max(pmax > 0 ? pg / pmax : 0, qUtil);

        return new PqDiagramScene
        {
            PlotLeft = px0,
            PlotRight = px1,
            PlotBottom = py0,
            PlotTop = py1,
            QMin = qmin,
            QMax = qmax,
            PMax = pmax,
            Span = span,
            ZeroQX = QtoX(0),
            Boundary = boundary,
            StressValue = stress,
            Stress = StressOf(stress),
            Dot = new ScreenPoint(QtoX(qg), PtoY(pg)),
            MwLabelAt = new ScreenPoint(px0 - 26, py1 - 2),
            UnderExcitedLabelAt = new ScreenPoint(px0, py0 + 4),
            OverExcitedLabelAt = new ScreenPoint(px1 - 40, py0 + 4),
        };
    }
}
