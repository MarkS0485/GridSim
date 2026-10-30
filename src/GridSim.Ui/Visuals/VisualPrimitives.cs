using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

// UI programme Phase 1.3 (the small visuals): the building blocks the five instrument geometries below hand to a front
// end. They describe WHAT to draw (a pen, a fill, an arc) in plain numbers and colour tokens; the front end (the WPF
// controls in src/GridSim.Wpf/Visuals, the Avalonia controls in src/GridSim.App/Controls) turns them into its own pens,
// brushes and geometry. Nothing here knows about a user-interface framework.

/// <summary>
/// A pen, without a user-interface framework: a colour token, a stroke thickness, a brush opacity, the line-cap and
/// line-join choice, and an optional even dash. It carries exactly the values the old WPF controls built their frozen
/// <c>Pen</c> objects from, so a front end that builds its pen from this token draws the same stroke.
/// </summary>
/// <param name="Colour">The stroke colour (the brush colour; its alpha byte is kept as given).</param>
/// <param name="Thickness">Stroke width in device-independent pixels.</param>
/// <param name="Opacity">The brush opacity, 0 to 1, applied on top of the colour (WPF and Avalonia both have it).</param>
/// <param name="RoundCaps">True for round start and end caps (the dials and the phasor); false for flat caps.</param>
/// <param name="RoundJoin">True for round line joins (the sparkline trace); false for the default mitre join.</param>
/// <param name="Dash">The dash and gap length as a multiple of the thickness (WPF and Avalonia both measure dashes in
/// stroke widths), the same for the dash and the gap; 0 for a solid line.</param>
/// <param name="Pattern">A named dash pattern (UI programme ASSETS A1: the WPF transformer schematic draws its windings
/// and fan rings with WPF's stock <c>DashStyles.Dash</c> and <c>DashStyles.DashDot</c>, which an even dash cannot
/// express). <see cref="VisualDashPattern.None"/> (the default) leaves <paramref name="Dash"/> in charge; a pen may set
/// one or the other, never both (a front end refuses a pen that sets both, loudly).</param>
public readonly record struct VisualPen(
    UiColour Colour, double Thickness, double Opacity = 1.0, bool RoundCaps = false, bool RoundJoin = false, double Dash = 0,
    VisualDashPattern Pattern = VisualDashPattern.None);

/// <summary>
/// A named dash pattern, as WPF's stock dash styles define it (both WPF and Avalonia measure the lengths in stroke
/// widths). The arrays are stated in <see cref="VisualDashPatterns"/> so every front end builds exactly the same dashes.
/// </summary>
public enum VisualDashPattern
{
    /// <summary>No named pattern: the pen's even <see cref="VisualPen.Dash"/> applies (0 = solid).</summary>
    None,
    /// <summary>WPF <c>DashStyles.Dash</c>: dash 2, gap 2, offset 1.</summary>
    Dash,
    /// <summary>WPF <c>DashStyles.DashDot</c>: dash 2, gap 2, dot 0, gap 2, offset 1.</summary>
    DashDot,
}

/// <summary>The dash arrays and offsets of the named patterns (WPF's stock values, which Avalonia's match).</summary>
public static class VisualDashPatterns
{
    /// <summary>The dash and gap lengths (in stroke widths) of a named pattern, or null for <see cref="VisualDashPattern.None"/>.</summary>
    public static IReadOnlyList<double>? Dashes(VisualDashPattern p) => p switch
    {
        VisualDashPattern.None => null,
        VisualDashPattern.Dash => new[] { 2.0, 2.0 },
        VisualDashPattern.DashDot => new[] { 2.0, 2.0, 0.0, 2.0 },
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "not a dash pattern"),
    };

    /// <summary>The offset (in stroke widths) of a named pattern (1 for both WPF stock patterns; 0 for none).</summary>
    public static double Offset(VisualDashPattern p) => p switch
    {
        VisualDashPattern.None => 0.0,
        VisualDashPattern.Dash or VisualDashPattern.DashDot => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "not a dash pattern"),
    };
}

/// <summary>A solid fill, without a user-interface framework: a colour token and a brush opacity (0 to 1).</summary>
/// <param name="Colour">The fill colour (its alpha byte is kept as given).</param>
/// <param name="Opacity">The brush opacity applied on top of the colour.</param>
public readonly record struct VisualFill(UiColour Colour, double Opacity = 1.0);

/// <summary>
/// A circular arc in screen space: the start and end points, the radius, and the two flags an arc segment needs (the
/// large-arc flag and the sweep direction). WPF's <c>ArcTo</c> and Avalonia's <c>ArcTo</c> take exactly these.
/// </summary>
/// <param name="Start">Where the arc starts (the figure's first point).</param>
/// <param name="End">Where the arc ends.</param>
/// <param name="Radius">The circle's radius (both radii of the ellipse are this).</param>
/// <param name="IsLargeArc">True when the arc spans more than 180 degrees.</param>
/// <param name="Clockwise">True to sweep clockwise on screen (y down), false for counter-clockwise.</param>
public readonly record struct VisualArc(ScreenPoint Start, ScreenPoint End, double Radius, bool IsLargeArc, bool Clockwise);

/// <summary>A straight line segment in screen space (a tick mark, a needle, a gridline).</summary>
/// <param name="From">The first end.</param>
/// <param name="To">The second end.</param>
public readonly record struct VisualLine(ScreenPoint From, ScreenPoint To);

/// <summary>Small helpers the front ends share when they turn these tokens into drawing calls.</summary>
public static class VisualMaths
{
    /// <summary>
    /// True when both coordinates are finite numbers. A front end uses this to skip a shape whose position came out
    /// as NaN or infinity (a NaN value bound to a gauge, say): WPF silently drew nothing for such a shape, and this keeps
    /// the Avalonia drawing the same instead of handing a non-finite point to the renderer.
    /// </summary>
    public static bool IsFinite(ScreenPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
}
