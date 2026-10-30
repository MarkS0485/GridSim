using GridSim.Ui.Schematic;

namespace GridSim.Ui.Diagrams;

/// <summary>
/// One placed symbol: what it is, where it sits, how it is turned, its label and its live state. Immutable: a state
/// change replaces the record in the <see cref="DiagramModel"/> (which bumps the model's version, so a cached drawing
/// is rebuilt).
///
/// <para><see cref="Position"/> is the symbol's origin in diagram units: the centre for every symbol except the two
/// busbar kinds, whose origin is the left end of the bar (taps are offsets along it). <see cref="Length"/> and
/// <see cref="Taps"/> are for the busbar kinds only; the model refuses them on any other kind.</para>
/// </summary>
/// <param name="Id">A unique id within the diagram (shown in hit results and click events).</param>
/// <param name="Kind">The symbol.</param>
/// <param name="Position">The origin (see above).</param>
/// <param name="Rotation">Quarter turns clockwise.</param>
/// <param name="Label">The label drawn beside the symbol (inside the box for a relay); empty for none.</param>
/// <param name="Length">The bar length (busbar kinds only).</param>
/// <param name="Taps">The tap offsets along the bar (busbar kinds only), each from 0 to <paramref name="Length"/>.</param>
public sealed record DiagramSymbol(
    string Id, DiagramSymbolKind Kind, ScreenPoint Position, DiagramRotation Rotation = DiagramRotation.None,
    string Label = "", double Length = 0, IReadOnlyList<double>? Taps = null)
{
    /// <summary>The live state (energised, switch position, loading).</summary>
    public DiagramState State { get; init; } = DiagramState.Normal;

    /// <summary>True for the busbar kinds (a bar with taps, origin at its left end).</summary>
    public bool IsBar => Kind is DiagramSymbolKind.Busbar or DiagramSymbolKind.BusSection;

    /// <summary>True for the switching devices (their blade shows <see cref="DiagramState.Switch"/>).</summary>
    public bool IsSwitch => Kind is DiagramSymbolKind.CircuitBreaker or DiagramSymbolKind.Disconnector or DiagramSymbolKind.EarthSwitch;

    /// <summary>A point given in the symbol's own (library) coordinates, turned and moved onto the diagram.</summary>
    public ScreenPoint ToDiagram(ScreenPoint local)
    {
        ScreenPoint r = DiagramGeometry.Rotate(local, Rotation);
        return new ScreenPoint(Position.X + r.X, Position.Y + r.Y);
    }
}

/// <summary>The exact quarter-turn arithmetic every part of the toolkit shares (no trigonometry, so no rounding).</summary>
public static class DiagramGeometry
{
    /// <summary>
    /// A point turned about the origin by quarter turns clockwise on screen (y down): a quarter turn takes (x, y) to
    /// (-y, x), so "up" (0, -1) becomes "right" (1, 0). Only sign changes and swaps: exact for every double.
    /// </summary>
    public static ScreenPoint Rotate(ScreenPoint p, DiagramRotation r) => r switch
    {
        DiagramRotation.None => p,
        DiagramRotation.Quarter => new ScreenPoint(-p.Y, p.X),
        DiagramRotation.Half => new ScreenPoint(-p.X, -p.Y),
        DiagramRotation.ThreeQuarter => new ScreenPoint(p.Y, -p.X),
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "not a rotation"),
    };

    /// <summary>A port direction turned by quarter turns clockwise (up, right, down, left, up ...).</summary>
    public static DiagramPortDirection Rotate(DiagramPortDirection d, DiagramRotation r)
    {
        int turns = r switch
        {
            DiagramRotation.None => 0,
            DiagramRotation.Quarter => 1,
            DiagramRotation.Half => 2,
            DiagramRotation.ThreeQuarter => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(r), r, "not a rotation"),
        };
        return (DiagramPortDirection)(((int)d + turns) % 4);
    }

    /// <summary>True when the direction is up or down.</summary>
    public static bool IsVertical(DiagramPortDirection d) => d is DiagramPortDirection.Up or DiagramPortDirection.Down;

    /// <summary>The squared distance from a point to a segment (the shared schematic helper, exact as it is there).</summary>
    public static double DistSqToSegment(ScreenPoint p, ScreenPoint a, ScreenPoint b) => SchematicView.DistSqPointToSegment(p, a, b);
}
