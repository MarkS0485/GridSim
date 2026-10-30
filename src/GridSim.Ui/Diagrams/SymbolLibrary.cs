using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.Ui.Diagrams;

/// <summary>A port as the library defines it, in the symbol's own coordinates, before any rotation.</summary>
/// <param name="Name">The port name.</param>
/// <param name="Local">Where it is, relative to the symbol's origin.</param>
/// <param name="Direction">Which way a connection leaves it, unrotated.</param>
public readonly record struct LocalPort(string Name, ScreenPoint Local, DiagramPortDirection Direction);

/// <summary>
/// The symbol library (UI programme ASSETS A1b): for every <see cref="DiagramSymbolKind"/>, its ports, its bounds and
/// its drawing, all in the symbol's own coordinates (origin at the centre, y down, two-port symbols running top to
/// bottom between ports 40 units apart), then turned and moved onto the diagram by <see cref="DiagramSymbol.ToDiagram"/>.
///
/// <para>Every symbol is drawn from the shared primitives only (lines, circles, polygons, rectangles and text in a
/// <see cref="VisualDisplayList"/>), so any front end that can replay a display list can draw every symbol. The shapes
/// follow IEC 60617 single-line conventions: switches as a blade between two contacts (a cross on the fixed contact for
/// a circuit breaker, a bar for a disconnector, an earth symbol beyond an earth switch), transformers as overlapping
/// winding circles, a generator as a circled G, instrument transformers small and threaded by the conductor, a relay
/// as a function box. Circles stay circles under every rotation and text stays upright, so turning a symbol never
/// distorts it.</para>
///
/// <para>Sizes are fixed numbers (the constants below and the literals in each drawing), so a test can pin every
/// primitive and port exactly.</para>
/// </summary>
public static class SymbolLibrary
{
    /// <summary>Half the distance between the two ports of a two-port symbol.</summary>
    public const double HalfPitch = 20;

    /// <summary>The radius of a transformer winding circle.</summary>
    public const double WindingRadius = 10;

    /// <summary>The radius of a generator circle.</summary>
    public const double GeneratorRadius = 12;

    /// <summary>The radius of an instrument transformer circle.</summary>
    public const double InstrumentRadius = 6;

    /// <summary>The y of a switch's fixed contact (top) and moving contact (bottom), as plus or minus this.</summary>
    public const double ContactY = 10;

    /// <summary>Where a closed blade ends (on the fixed contact).</summary>
    public static readonly ScreenPoint ClosedBladeTip = new(0, -10);

    /// <summary>Where an open blade ends (standing off to the left).</summary>
    public static readonly ScreenPoint OpenBladeTip = new(-10, -7);

    /// <summary>Where a blade of unknown position ends (half way: visibly neither open nor closed).</summary>
    public static readonly ScreenPoint UnknownBladeTip = new(-5, -9);

    /// <summary>The gap between a symbol's bounds and its label.</summary>
    public const double LabelGap = 4;

    /// <summary>The ports of a symbol kind, unrotated. Busbar kinds take their length and taps from the symbol.</summary>
    public static IReadOnlyList<LocalPort> LocalPorts(DiagramSymbol s)
    {
        ArgumentNullException.ThrowIfNull(s);
        ScreenPoint top = new(0, -HalfPitch), bottom = new(0, HalfPitch);
        return s.Kind switch
        {
            DiagramSymbolKind.Busbar or DiagramSymbolKind.BusSection => BarPorts(s),
            DiagramSymbolKind.CircuitBreaker or DiagramSymbolKind.Disconnector =>
                [new("A", top, DiagramPortDirection.Up), new("B", bottom, DiagramPortDirection.Down)],
            DiagramSymbolKind.EarthSwitch or DiagramSymbolKind.Generator or DiagramSymbolKind.Load or DiagramSymbolKind.LineEnd =>
                [new("T", top, DiagramPortDirection.Up)],
            DiagramSymbolKind.Transformer2W =>
                [new("HV", top, DiagramPortDirection.Up), new("LV", bottom, DiagramPortDirection.Down)],
            DiagramSymbolKind.Transformer3W =>
                [new("HV", top, DiagramPortDirection.Up), new("MV", new(-6, HalfPitch), DiagramPortDirection.Down),
                 new("LV", new(6, HalfPitch), DiagramPortDirection.Down)],
            DiagramSymbolKind.CurrentTransformer =>
                [new("A", top, DiagramPortDirection.Up), new("B", bottom, DiagramPortDirection.Down),
                 new("S", new(14, 0), DiagramPortDirection.Right)],
            DiagramSymbolKind.VoltageTransformer =>
                [new("T", top, DiagramPortDirection.Up), new("S", new(14, 2), DiagramPortDirection.Right)],
            DiagramSymbolKind.ProtectionRelay => [new("IN", new(-20, 0), DiagramPortDirection.Left)],
            DiagramSymbolKind.CableEnd =>
                [new("T", top, DiagramPortDirection.Up), new("C", bottom, DiagramPortDirection.Down)],
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "not a symbol kind"),
        };
    }

    private static List<LocalPort> BarPorts(DiagramSymbol s)
    {
        var ports = new List<LocalPort>
        {
            new("L", new ScreenPoint(0, 0), DiagramPortDirection.Left),
            new("R", new ScreenPoint(s.Length, 0), DiagramPortDirection.Right),
        };
        IReadOnlyList<double> taps = s.Taps ?? [];
        for (int i = 0; i < taps.Count; i++)
            ports.Add(new LocalPort("T" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), new ScreenPoint(taps[i], 0), DiagramPortDirection.Down));
        return ports;
    }

    /// <summary>The bounds of a symbol kind, unrotated (what a hit test and a fit-to-view use; the label is outside).</summary>
    public static DiagramRect LocalBounds(DiagramSymbol s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Kind switch
        {
            DiagramSymbolKind.Busbar => new DiagramRect(0, -4, s.Length, 8),
            DiagramSymbolKind.BusSection => new DiagramRect(0, -7, s.Length, 14),
            DiagramSymbolKind.CircuitBreaker or DiagramSymbolKind.Disconnector or DiagramSymbolKind.Transformer2W =>
                new DiagramRect(-11, -20, 22, 40),
            DiagramSymbolKind.EarthSwitch => new DiagramRect(-11, -20, 22, 43),
            DiagramSymbolKind.Transformer3W => new DiagramRect(-17, -20, 34, 40),
            DiagramSymbolKind.Generator => new DiagramRect(-13, -20, 26, 33),
            DiagramSymbolKind.Load => new DiagramRect(-8, -20, 16, 37),
            DiagramSymbolKind.CurrentTransformer => new DiagramRect(-7, -20, 21, 40),
            DiagramSymbolKind.VoltageTransformer => new DiagramRect(-7, -20, 21, 29),
            DiagramSymbolKind.ProtectionRelay => new DiagramRect(-20, -9, 33, 18),
            DiagramSymbolKind.CableEnd => new DiagramRect(-8, -20, 16, 40),
            DiagramSymbolKind.LineEnd => new DiagramRect(-7, -20, 14, 31),
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "not a symbol kind"),
        };
    }

    /// <summary>The symbol's ports on the diagram (turned and moved), in library order.</summary>
    public static IReadOnlyList<DiagramPort> Ports(DiagramSymbol s) =>
        LocalPorts(s).Select(p => new DiagramPort(s.Id, p.Name, s.ToDiagram(p.Local), DiagramGeometry.Rotate(p.Direction, s.Rotation))).ToList();

    /// <summary>The symbol's bounds on the diagram (the unrotated bounds' corners turned and moved).</summary>
    public static DiagramRect Bounds(DiagramSymbol s)
    {
        DiagramRect b = LocalBounds(s);
        return DiagramRect.FromCorners(s.ToDiagram(new ScreenPoint(b.X, b.Y)), s.ToDiagram(new ScreenPoint(b.Right, b.Bottom)));
    }

    /// <summary>The primitives of one symbol on its own (for tests and previews): the same calls <see cref="Draw"/> adds.</summary>
    public static VisualDisplayList Primitives(DiagramSymbol s)
    {
        var dc = new VisualDisplayList();
        Draw(dc, s);
        return dc;
    }

    /// <summary>Add the drawing of <paramref name="s"/> (in its live state) to <paramref name="dc"/>, then its label.</summary>
    public static void Draw(VisualDisplayList dc, DiagramSymbol s)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(s);
        var c = new Canvas(dc, s);
        VisualPen wire = DiagramStyle.ConductorPen(s.State);
        switch (s.Kind)
        {
            case DiagramSymbolKind.Busbar:
                c.Line(DiagramStyle.BusbarPen(s.State), new(0, 0), new(s.Length, 0));
                break;
            case DiagramSymbolKind.BusSection:
                c.Line(DiagramStyle.BusbarPen(s.State), new(0, 0), new(s.Length, 0));
                c.Line(wire, new(0, -7), new(0, 7));
                c.Line(wire, new(s.Length, -7), new(s.Length, 7));
                break;
            case DiagramSymbolKind.CircuitBreaker:
            case DiagramSymbolKind.Disconnector:
                c.Line(wire, new(0, -HalfPitch), new(0, -ContactY));
                c.Line(wire, new(0, ContactY), new(0, HalfPitch));
                FixedContact(c, s);
                Blade(c, s);
                break;
            case DiagramSymbolKind.EarthSwitch:
                c.Line(wire, new(0, -HalfPitch), new(0, -ContactY));
                FixedContact(c, s);
                Blade(c, s);
                c.Line(wire, new(0, ContactY), new(0, 14));
                c.Line(wire, new(-8, 14), new(8, 14));
                c.Line(wire, new(-5, 18), new(5, 18));
                c.Line(wire, new(-2, 22), new(2, 22));
                break;
            case DiagramSymbolKind.Transformer2W:
                c.Line(wire, new(0, -HalfPitch), new(0, -16));
                c.Line(wire, new(0, 16), new(0, HalfPitch));
                c.Circle(null, wire, new(0, -6), WindingRadius);
                c.Circle(null, wire, new(0, 6), WindingRadius);
                break;
            case DiagramSymbolKind.Transformer3W:
                c.Line(wire, new(0, -HalfPitch), new(0, -16));
                c.Line(wire, new(-6, 14), new(-6, HalfPitch));
                c.Line(wire, new(6, 14), new(6, HalfPitch));
                c.Circle(null, wire, new(0, -6), WindingRadius);
                c.Circle(null, wire, new(-6, 4), WindingRadius);
                c.Circle(null, wire, new(6, 4), WindingRadius);
                break;
            case DiagramSymbolKind.Generator:
                c.Line(wire, new(0, -HalfPitch), new(0, -GeneratorRadius));
                c.Circle(DiagramStyle.BoxFill, wire, new(0, 0), GeneratorRadius);
                c.TextCentred("G", VisualTextWeight.Bold, 12, new VisualFill(wire.Colour), new(0, 0));
                break;
            case DiagramSymbolKind.Load:
                c.Line(wire, new(0, -HalfPitch), new(0, 4));
                c.Polygon(new VisualFill(wire.Colour), new(-7, 4), new(7, 4), new(0, 16));
                break;
            case DiagramSymbolKind.CurrentTransformer:
                c.Line(wire, new(0, -HalfPitch), new(0, HalfPitch));
                c.Circle(null, wire, new(0, 0), InstrumentRadius);
                c.Line(wire, new(InstrumentRadius, 0), new(14, 0));
                break;
            case DiagramSymbolKind.VoltageTransformer:
                c.Line(wire, new(0, -HalfPitch), new(0, -12));
                c.Circle(null, wire, new(0, -6), InstrumentRadius);
                c.Circle(null, wire, new(0, 2), InstrumentRadius);
                c.Line(wire, new(InstrumentRadius, 2), new(14, 2));
                break;
            case DiagramSymbolKind.ProtectionRelay:
                c.Line(wire, new(-20, 0), new(-12, 0));
                c.Box(DiagramStyle.BoxFill, wire, new(-12, -8), new(12, 8));
                c.TextCentred(s.Label, VisualTextWeight.Bold, 8, DiagramStyle.LabelFill, new(0, 0));
                break;
            case DiagramSymbolKind.CableEnd:
                c.Line(wire, new(0, -HalfPitch), new(0, 0));
                c.Outline(wire, new(0, 0), new(-7, 12), new(7, 12));
                c.Line(wire, new(0, 12), new(0, HalfPitch));
                break;
            case DiagramSymbolKind.LineEnd:
                c.Line(wire, new(0, -HalfPitch), new(0, 10));
                dc.Polyline(wire, [s.ToDiagram(new(-6, 4)), s.ToDiagram(new(0, 10)), s.ToDiagram(new(6, 4))]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "not a symbol kind");
        }
        DrawLabel(dc, s);
    }

    /// <summary>Where a symbol's label goes: above the left end of a bar, else right of the bounds, centred on them.</summary>
    public static ScreenPoint LabelAnchor(DiagramSymbol s)
    {
        DiagramRect b = Bounds(s);
        return s.IsBar
            ? new ScreenPoint(b.X, b.Y - DiagramStyle.LabelSize - LabelGap)
            : new ScreenPoint(b.Right + LabelGap, b.Centre.Y - DiagramStyle.LabelSize / 2 - 1);
    }

    private static void DrawLabel(VisualDisplayList dc, DiagramSymbol s)
    {
        // A relay carries its label inside the box (drawn above); every other symbol beside it, when it has one.
        if (s.Kind == DiagramSymbolKind.ProtectionRelay || s.Label.Length == 0) return;
        dc.Text(s.Label, VisualTextWeight.SemiBold, DiagramStyle.LabelSize, DiagramStyle.LabelFill, LabelAnchor(s), VisualTextAlign.Left);
    }

    /// <summary>The fixed-contact mark: a cross for a circuit breaker, a bar for a disconnector or an earth switch.</summary>
    private static void FixedContact(Canvas c, DiagramSymbol s)
    {
        VisualPen pen = DiagramStyle.SwitchPen(s.State);
        if (s.Kind == DiagramSymbolKind.CircuitBreaker)
        {
            c.Line(pen, new(-3, -13), new(3, -7));
            c.Line(pen, new(-3, -7), new(3, -13));
        }
        else
        {
            c.Line(pen, new(-5, -ContactY), new(5, -ContactY));
        }
    }

    /// <summary>The blade from the moving contact to the tip its position puts it at.</summary>
    private static void Blade(Canvas c, DiagramSymbol s)
    {
        ScreenPoint tip = s.State.Switch switch
        {
            DiagramSwitchState.Closed => ClosedBladeTip,
            DiagramSwitchState.Open => OpenBladeTip,
            DiagramSwitchState.Unknown => UnknownBladeTip,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.State.Switch, "not a switch state"),
        };
        c.Line(DiagramStyle.SwitchPen(s.State), new(0, ContactY), tip);
    }

    /// <summary>Adds primitives given in the symbol's own coordinates, turned and moved onto the diagram.</summary>
    private readonly struct Canvas
    {
        private readonly VisualDisplayList _dc;
        private readonly DiagramSymbol _s;

        public Canvas(VisualDisplayList dc, DiagramSymbol s) { _dc = dc; _s = s; }

        public void Line(VisualPen pen, ScreenPoint a, ScreenPoint b) => _dc.Line(pen, _s.ToDiagram(a), _s.ToDiagram(b));

        public void Circle(VisualFill? fill, VisualPen? pen, ScreenPoint centre, double r) => _dc.Ellipse(fill, pen, _s.ToDiagram(centre), r, r);

        public void Polygon(VisualFill fill, params ScreenPoint[] pts) => _dc.Polygon(fill, pts.Select(_s.ToDiagram).ToArray());

        /// <summary>A closed outline (stroked, not filled).</summary>
        public void Outline(VisualPen pen, params ScreenPoint[] pts) =>
            _dc.Add(new VisualPathCommand(null, pen, pts.Select(_s.ToDiagram).ToArray(), Closed: true));

        /// <summary>A square-cornered box between two corners (turned, so a quarter-turned box stays a box).</summary>
        public void Box(VisualFill? fill, VisualPen? pen, ScreenPoint a, ScreenPoint b)
        {
            DiagramRect r = DiagramRect.FromCorners(_s.ToDiagram(a), _s.ToDiagram(b));
            _dc.Rect(fill, pen, r.X, r.Y, r.Width, r.Height, 0, 0);
        }

        /// <summary>Upright text centred on a point of the symbol (its top edge half the size plus one above it).</summary>
        public void TextCentred(string text, VisualTextWeight weight, double size, VisualFill fill, ScreenPoint centre)
        {
            ScreenPoint p = _s.ToDiagram(centre);
            _dc.Text(text, weight, size, fill, new ScreenPoint(p.X, p.Y - (size / 2 + 1)), VisualTextAlign.Centre);
        }
    }
}
