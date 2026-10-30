using System.Globalization;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>Which pen a gauge arc is drawn with.</summary>
public enum GaugeBandKind
{
    /// <summary>The full-scale dark track under everything.</summary>
    Track,
    /// <summary>An amber warning band.</summary>
    Amber,
    /// <summary>A red alarm band.</summary>
    Red,
}

/// <summary>
/// One arc of the dial: the track or a warning band, as fractions of the scale, and the arc to draw (null when the band
/// is shorter than the minimum the dial draws, as the old WPF <c>DrawArc</c> skipped it).
/// </summary>
/// <param name="Kind">Track, amber or red.</param>
/// <param name="From">Start of the band, as a fraction of the scale (0 = minimum, 1 = maximum).</param>
/// <param name="To">End of the band, as a fraction of the scale.</param>
/// <param name="Arc">The arc on screen, or null when the band is too short to draw.</param>
public readonly record struct GaugeBand(GaugeBandKind Kind, double From, double To, VisualArc? Arc);

/// <summary>
/// Everything the gauge dial draws, worked out from its size and its values (see <see cref="GaugeGeometry.Compute"/>).
/// The front end only turns these numbers into pens, arcs, lines and text.
/// </summary>
public sealed class GaugeScene
{
    /// <summary>The centre of the dial (the needle pivot and hub).</summary>
    public ScreenPoint Centre { get; init; }

    /// <summary>The radius of the arc track.</summary>
    public double Radius { get; init; }

    /// <summary>The arcs in drawing order: the track first, then the high-side amber and red bands, then the low-side
    /// amber and red bands (only the bands that are switched on).</summary>
    public IReadOnlyList<GaugeBand> Bands { get; init; } = Array.Empty<GaugeBand>();

    /// <summary>The nine tick marks across the sweep, each from the outer to the inner end.</summary>
    public IReadOnlyList<VisualLine> Ticks { get; init; } = Array.Empty<VisualLine>();

    /// <summary>The value as a fraction of the scale, clamped to 0..1 (NaN when the value is NaN).</summary>
    public double NeedleFraction { get; init; }

    /// <summary>The needle runs from <see cref="Centre"/> to this tip.</summary>
    public ScreenPoint NeedleTip { get; init; }

    /// <summary>Where the caption's text is centred (its top edge), above the dial.</summary>
    public ScreenPoint CaptionAt { get; init; }

    /// <summary>The hub text: the caller's override when it has one, else the value printed "0.##" (invariant).</summary>
    public string ValueLabel { get; init; } = "";

    /// <summary>Where the hub text is centred (its top edge).</summary>
    public ScreenPoint ValueAt { get; init; }

    /// <summary>The hub text's font size (it grows with the dial, never below 13).</summary>
    public double ValueFontSize { get; init; }

    /// <summary>Where the unit line is centred (its top edge), under the hub text.</summary>
    public ScreenPoint UnitAt { get; init; }
}

/// <summary>
/// The pure geometry of the control-room gauge dial (UI programme Phase 1.3): a 240 degree arc with tick marks, optional
/// amber and red zones on the high and the low side, a needle, and the value printed in the hub.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/Gauge.cs</c> <c>OnRender</c>, moved here operation for operation
/// so the WPF control and the Avalonia control draw from the same numbers: every point, radius, fraction and label is
/// computed by the same expressions in the same order, so the WPF pixels are unchanged. <c>UiVisualsParityTests</c>
/// pins this against a verbatim copy of the old WPF code, bit for bit.</para>
///
/// <para>The angles use <see cref="Math.Cos"/> and <see cref="Math.Sin"/>, as the WPF gauge always did. This is
/// cosmetic screen layout, not a solved number, and this project is not on the deterministic-maths guard's list (the
/// same note as <see cref="SchematicView"/>).</para>
/// </summary>
public static class GaugeGeometry
{
    /// <summary>Where the sweep starts, in screen degrees (0 = east, clockwise positive): the gap is at the bottom.</summary>
    public const double StartDeg = -210;

    /// <summary>The sweep of the full scale, in degrees.</summary>
    public const double SweepDeg = 240;

    /// <summary>Below this width or height (pixels) the dial draws nothing.</summary>
    public const double MinSize = 30;

    /// <summary>A band shorter than this (fraction of the scale) is not drawn.</summary>
    public const double MinBandFraction = 1e-4;

    /// <summary>Radius of the filled hub at the needle pivot.</summary>
    public const double HubRadius = 4;

    /// <summary>Font size of the caption above the dial.</summary>
    public const double CaptionFontSize = 9.5;

    /// <summary>Font size of the unit line under the hub text.</summary>
    public const double UnitFontSize = 10;

    // ---- the colour tokens (the same hex values the WPF gauge's pens and brushes were built from) ----

    /// <summary>The dark full-scale track.</summary>
    public static readonly VisualPen Track = new(ColourRamps.FromHex("#2A3742"), 7, RoundCaps: true);

    /// <summary>The amber warning band.</summary>
    public static readonly VisualPen AmberArc = new(ColourRamps.FromHex("#F3C13A"), 7, 0.85, RoundCaps: true);

    /// <summary>The red alarm band.</summary>
    public static readonly VisualPen RedArc = new(ColourRamps.FromHex("#FF5B5B"), 7, 0.9, RoundCaps: true);

    /// <summary>The tick marks.</summary>
    public static readonly VisualPen TickPen = new(ColourRamps.FromHex("#46535F"), 1.6, RoundCaps: true);

    /// <summary>The needle (the accent colour).</summary>
    public static readonly VisualPen NeedlePen = new(ColourRamps.FromHex("#21C7E6"), 3, RoundCaps: true);

    /// <summary>The hub and the hub text.</summary>
    public static readonly VisualFill Hub = new(ColourRamps.FromHex("#E6EDF3"));

    /// <summary>The caption and the unit line.</summary>
    public static readonly VisualFill Muted = new(ColourRamps.FromHex("#8492A0"));

    /// <summary>The pen for an arc of the given kind.</summary>
    public static VisualPen PenFor(GaugeBandKind kind) => kind switch
    {
        GaugeBandKind.Track => Track,
        GaugeBandKind.Amber => AmberArc,
        GaugeBandKind.Red => RedArc,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a gauge band kind"),
    };

    /// <summary>A fraction of the scale (0..1) as a screen angle in degrees.</summary>
    public static double Deg(double frac) => StartDeg + frac * SweepDeg;

    /// <summary>
    /// The arc of the dial from fraction <paramref name="f0"/> to <paramref name="f1"/> at radius <paramref name="r"/>
    /// about <paramref name="c"/>, or null when it is shorter than <see cref="MinBandFraction"/> (the old WPF
    /// <c>DrawArc</c>, which skipped such a band). Always clockwise; large when it spans more than 180 degrees.
    /// </summary>
    public static VisualArc? Arc(ScreenPoint c, double r, double f0, double f1)
    {
        if (f1 - f0 < MinBandFraction) return null;
        double a0 = Deg(f0) * Math.PI / 180, a1 = Deg(f1) * Math.PI / 180;
        return new VisualArc(
            new ScreenPoint(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r),
            new ScreenPoint(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r),
            r, (f1 - f0) * SweepDeg > 180, Clockwise: true);
    }

    /// <summary>
    /// Work out the whole dial for a control of <paramref name="w"/> by <paramref name="h"/> pixels, or null when it is
    /// smaller than <see cref="MinSize"/> either way (the old gauge drew nothing then).
    /// </summary>
    /// <param name="w">Control width.</param>
    /// <param name="h">Control height.</param>
    /// <param name="value">The value the needle shows.</param>
    /// <param name="minimum">The value at the start of the scale.</param>
    /// <param name="maximum">The value at the end of the scale.</param>
    /// <param name="amberFrom">Fraction where the high-side amber band starts; 1 or more = none.</param>
    /// <param name="redFrom">Fraction where the high-side red band starts; 1 or more = none.</param>
    /// <param name="amberTo">Fraction up to which the low-side amber band runs; 0 or less = none.</param>
    /// <param name="redTo">Fraction up to which the low-side red band runs; 0 or less = none.</param>
    /// <param name="valueText">The hub text override; empty = the value, auto-formatted.</param>
    public static GaugeScene? Compute(double w, double h, double value, double minimum, double maximum,
        double amberFrom, double redFrom, double amberTo, double redTo, string valueText)
    {
        ArgumentNullException.ThrowIfNull(valueText);
        // NaN fails every comparison, so "w < MinSize" let a NaN size through; written the other way round it is refused
        // (2026-10-24). Every finite size gives the same answer as before.
        if (!(w >= MinSize) || !(h >= MinSize)) return null;

        double r = Math.Min(w, h * 1.16) / 2 - 10;
        ScreenPoint c = new(w / 2, h / 2 + r * 0.12);

        // arc track + warning bands (high side toward full scale, low side from zero), in the old drawing order
        var bands = new List<GaugeBand>(5) { Band(GaugeBandKind.Track, c, r, 0, 1) };
        if (amberFrom < 1) bands.Add(Band(GaugeBandKind.Amber, c, r, Math.Clamp(amberFrom, 0, 1), Math.Clamp(redFrom, amberFrom, 1)));
        if (redFrom < 1) bands.Add(Band(GaugeBandKind.Red, c, r, Math.Clamp(redFrom, 0, 1), 1));
        if (amberTo > 0) bands.Add(Band(GaugeBandKind.Amber, c, r, Math.Clamp(redTo, 0, amberTo), Math.Clamp(amberTo, 0, 1)));
        if (redTo > 0) bands.Add(Band(GaugeBandKind.Red, c, r, 0, Math.Clamp(redTo, 0, 1)));

        // ticks (9 across the sweep)
        var ticks = new VisualLine[9];
        for (int i = 0; i <= 8; i++)
        {
            double a = Deg(i / 8.0) * Math.PI / 180;
            ScreenPoint t0 = new(c.X + Math.Cos(a) * (r - 7), c.Y + Math.Sin(a) * (r - 7));
            ScreenPoint t1 = new(c.X + Math.Cos(a) * (r - 13), c.Y + Math.Sin(a) * (r - 13));
            ticks[i] = new VisualLine(t0, t1);
        }

        // needle: the value as a clamped fraction of the range (a zero range counts as a tiny one, never a division by 0)
        double range = Math.Max(1e-9, maximum - minimum);
        double frac = Math.Clamp((value - minimum) / range, 0, 1);
        double na = Deg(frac) * Math.PI / 180;
        ScreenPoint tip = new(c.X + Math.Cos(na) * (r - 16), c.Y + Math.Sin(na) * (r - 16));

        // text: caption above, value + unit in/under the hub
        double valueSize = Math.Max(13, r * 0.34);
        return new GaugeScene
        {
            Centre = c,
            Radius = r,
            Bands = bands,
            Ticks = ticks,
            NeedleFraction = frac,
            NeedleTip = tip,
            CaptionAt = new ScreenPoint(c.X, c.Y - r - 4),
            ValueLabel = valueText.Length > 0 ? valueText : value.ToString("0.##", CultureInfo.InvariantCulture),
            ValueAt = new ScreenPoint(c.X, c.Y + r * 0.30),
            ValueFontSize = valueSize,
            UnitAt = new ScreenPoint(c.X, c.Y + r * 0.30 + Math.Max(13, r * 0.34) + 2),
        };
    }

    private static GaugeBand Band(GaugeBandKind kind, ScreenPoint c, double r, double f0, double f1) =>
        new(kind, f0, f1, Arc(c, r, f0, f1));
}
