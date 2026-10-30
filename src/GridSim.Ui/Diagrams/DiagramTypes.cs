using GridSim.Ui.Schematic;

namespace GridSim.Ui.Diagrams;

// UI programme ASSETS A1b: the electrical diagram toolkit, the small value types. A diagram is a set of standard symbols
// (IEC 60617 conventions) placed in "diagram units" (one unit is one device-independent pixel at zoom 1, y pointing
// down, as on screen), joined by orthogonal connections between their ports, with free labels. Nothing here knows
// about a user-interface framework: a front end draws the display list the DiagramRenderer makes.

/// <summary>The standard symbols the toolkit can draw (single-line diagram, IEC 60617 conventions).</summary>
public enum DiagramSymbolKind
{
    /// <summary>A busbar: a thick bar with tap ports along it (IEC 60617, a conductor drawn thick).</summary>
    Busbar,
    /// <summary>One section of a sectioned busbar: a busbar with end ticks marking where the section stops.</summary>
    BusSection,
    /// <summary>A circuit breaker (IEC 60617): a switch blade with a cross on the fixed contact.</summary>
    CircuitBreaker,
    /// <summary>A disconnector (isolator, IEC 60617): a switch blade with a bar on the fixed contact.</summary>
    Disconnector,
    /// <summary>An earth switch: a disconnector whose far side goes to an earth symbol (IEC 60617).</summary>
    EarthSwitch,
    /// <summary>A two-winding transformer (IEC 60617): two overlapping circles.</summary>
    Transformer2W,
    /// <summary>A three-winding transformer (IEC 60617): three overlapping circles.</summary>
    Transformer3W,
    /// <summary>A generator (IEC 60617): a circle with the letter G.</summary>
    Generator,
    /// <summary>A load: a filled arrow head on a stub (the usual single-line convention for a demand).</summary>
    Load,
    /// <summary>A current transformer (IEC 60617): a circle threaded by the primary conductor, a secondary lead.</summary>
    CurrentTransformer,
    /// <summary>A voltage transformer (IEC 60617, drawn small): two small overlapping circles off the conductor.</summary>
    VoltageTransformer,
    /// <summary>A protection relay (IEC 60617, a function box): a rectangle carrying its function number.</summary>
    ProtectionRelay,
    /// <summary>A cable end (cable sealing end): a cone where the conductor becomes a cable.</summary>
    CableEnd,
    /// <summary>A line end (overhead line terminal): the conductor leaving the diagram under an open arrow head.</summary>
    LineEnd,
}

/// <summary>A symbol's rotation in quarter turns, clockwise on screen.</summary>
public enum DiagramRotation
{
    /// <summary>As drawn in the library (two-port symbols run top to bottom).</summary>
    None,
    /// <summary>A quarter turn clockwise (top to bottom becomes right to left).</summary>
    Quarter,
    /// <summary>A half turn (upside down).</summary>
    Half,
    /// <summary>Three quarter turns clockwise (a quarter turn anticlockwise).</summary>
    ThreeQuarter,
}

/// <summary>Which way a connection leaves a port.</summary>
public enum DiagramPortDirection
{
    /// <summary>Upwards (negative y).</summary>
    Up,
    /// <summary>To the right (positive x).</summary>
    Right,
    /// <summary>Downwards (positive y).</summary>
    Down,
    /// <summary>To the left (negative x).</summary>
    Left,
}

/// <summary>The position of a switching device.</summary>
public enum DiagramSwitchState
{
    /// <summary>Closed: the blade bridges the contacts.</summary>
    Closed,
    /// <summary>Open: the blade stands off the fixed contact.</summary>
    Open,
    /// <summary>Not known (no indication, or a disagreeing pair of auxiliary contacts).</summary>
    Unknown,
}

/// <summary>
/// The live state of a symbol or a connection: energised or not, the switch position (switching devices only), and an
/// optional loading fraction (0 = none, 1 = at the rating) that colours a conductor or a transformer by the shared
/// loading ramp. See <see cref="DiagramStyle"/> for the colours.
/// </summary>
/// <param name="Energised">True when the item is live.</param>
/// <param name="Switch">The switch position (ignored by symbols that are not switching devices).</param>
/// <param name="Loading">The loading fraction, or null for no reading (a non-finite value shows as unknown).</param>
public readonly record struct DiagramState(bool Energised = true, DiagramSwitchState Switch = DiagramSwitchState.Closed, double? Loading = null)
{
    /// <summary>Energised, closed, no loading reading.</summary>
    public static DiagramState Normal => new(true, DiagramSwitchState.Closed, null);
}

/// <summary>An axis-aligned rectangle in diagram units. A point on the edge is inside.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width (not negative).</param>
/// <param name="Height">The height (not negative).</param>
public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The centre.</summary>
    public ScreenPoint Centre => new(X + Width / 2, Y + Height / 2);

    /// <summary>True when the point lies inside or on the edge.</summary>
    public bool Contains(ScreenPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    /// <summary>The rectangle spanning two corners given in any order.</summary>
    public static DiagramRect FromCorners(ScreenPoint a, ScreenPoint b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new DiagramRect(x0, y0, Math.Max(a.X, b.X) - x0, Math.Max(a.Y, b.Y) - y0);
    }

    /// <summary>The smallest rectangle holding both.</summary>
    public DiagramRect Union(DiagramRect o)
    {
        double x0 = Math.Min(X, o.X), y0 = Math.Min(Y, o.Y);
        return new DiagramRect(x0, y0, Math.Max(Right, o.Right) - x0, Math.Max(Bottom, o.Bottom) - y0);
    }
}

/// <summary>A connection point on a placed symbol, in diagram units.</summary>
/// <param name="SymbolId">The symbol it belongs to.</param>
/// <param name="Name">The port's name (unique on the symbol: "A", "B", "HV", "T0" ...).</param>
/// <param name="Position">Where it is.</param>
/// <param name="Direction">Which way a connection leaves it.</param>
public sealed record DiagramPort(string SymbolId, string Name, ScreenPoint Position, DiagramPortDirection Direction);

/// <summary>Where a free label's anchor sits relative to its text.</summary>
public enum DiagramLabelAlign
{
    /// <summary>The anchor is the top-left corner of the text.</summary>
    Left,
    /// <summary>The anchor is the top-centre of the text.</summary>
    Centre,
}

/// <summary>A free text label on the diagram (a bay name, a reading).</summary>
/// <param name="Text">The text.</param>
/// <param name="Anchor">The anchor point (see <paramref name="Align"/>).</param>
/// <param name="Size">The font size in diagram units.</param>
/// <param name="Align">Left aligned or centred on the anchor.</param>
/// <param name="Muted">True for the muted caption colour, false for the label colour.</param>
public sealed record DiagramLabel(string Text, ScreenPoint Anchor, double Size = 10, DiagramLabelAlign Align = DiagramLabelAlign.Left, bool Muted = false);

/// <summary>What a hit test found.</summary>
public enum DiagramHitKind
{
    /// <summary>A symbol (its bounds hold the point).</summary>
    Symbol,
    /// <summary>A connection (the point is within the tolerance of one of its segments).</summary>
    Connection,
}

/// <summary>The result of a hit test: a symbol or a connection, by id.</summary>
/// <param name="Kind">Symbol or connection.</param>
/// <param name="Id">The symbol's or the connection's id.</param>
/// <param name="SymbolKind">The symbol kind (for a symbol hit), else null.</param>
public sealed record DiagramHit(DiagramHitKind Kind, string Id, DiagramSymbolKind? SymbolKind);
