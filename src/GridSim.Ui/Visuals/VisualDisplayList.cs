using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

// UI programme ASSETS slices A1 and A1b: a display list, the ordered drawing calls of one picture, in plain numbers and
// colour tokens. The two asset visuals (SubstationMapGeometry, TransformerSchematicGeometry) and the electrical diagram
// toolkit (GridSim.Ui/Diagrams) all produce one; a front end only replays it, call by call, with its own pens, brushes
// and text. Because the order is part of the list, two front ends that replay the same list paint the same layers in
// the same order. Nothing here knows about a user-interface framework.

/// <summary>The weight of a line of text (the WPF asset visuals used Bahnschrift at these two weights).</summary>
public enum VisualTextWeight
{
    /// <summary>Semi-bold (the old WPF "MonoLight" typeface).</summary>
    SemiBold,
    /// <summary>Bold (the old WPF "Mono" typeface).</summary>
    Bold,
}

/// <summary>Where a line of text sits relative to its anchor point.</summary>
public enum VisualTextAlign
{
    /// <summary>Centred on the anchor's x (the WPF controls centred inside a 400 pixel box starting 200 to the left).</summary>
    Centre,
    /// <summary>Starting at the anchor's x.</summary>
    Left,
}

/// <summary>
/// One drawing call. A front end switches on the concrete type; an unknown type is a programming error and a front end
/// must refuse it loudly, never skip it.
/// </summary>
public abstract record VisualCommand;

/// <summary>A straight stroked line.</summary>
/// <param name="Pen">The stroke.</param>
/// <param name="From">The first end.</param>
/// <param name="To">The second end.</param>
public sealed record VisualLineCommand(VisualPen Pen, ScreenPoint From, ScreenPoint To) : VisualCommand;

/// <summary>An ellipse (a circle when the radii are equal), filled, stroked, or both.</summary>
/// <param name="Fill">The fill, or null for none.</param>
/// <param name="Pen">The outline, or null for none.</param>
/// <param name="Centre">The centre.</param>
/// <param name="RadiusX">The horizontal radius.</param>
/// <param name="RadiusY">The vertical radius.</param>
public sealed record VisualEllipseCommand(VisualFill? Fill, VisualPen? Pen, ScreenPoint Centre, double RadiusX, double RadiusY) : VisualCommand;

/// <summary>
/// A rectangle with rounded corners (radius 0 gives square corners), filled, stroked, or both. WPF refuses a negative
/// width or height with an exception; this record keeps the numbers as given so a WPF front end behaves exactly as the
/// old code, and other front ends must refuse such a rectangle too (fail loud) rather than draw something else.
/// </summary>
/// <param name="Fill">The fill, or null for none.</param>
/// <param name="Pen">The outline, or null for none.</param>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="RadiusX">The corner radius across.</param>
/// <param name="RadiusY">The corner radius down.</param>
public sealed record VisualRectCommand(VisualFill? Fill, VisualPen? Pen, double X, double Y, double Width, double Height,
    double RadiusX, double RadiusY) : VisualCommand;

/// <summary>
/// A figure through a list of points: an open polyline (a connection, a needle) or, with <paramref name="Closed"/>, a
/// closed polygon (an arrow head). Filled when <paramref name="Fill"/> is set.
/// </summary>
/// <param name="Fill">The fill, or null for none (a fill on an open figure fills it as if closed, as WPF does).</param>
/// <param name="Pen">The stroke, or null for none.</param>
/// <param name="Points">The points, at least two.</param>
/// <param name="Closed">True to close the figure back to its first point.</param>
public sealed record VisualPathCommand(VisualFill? Fill, VisualPen? Pen, IReadOnlyList<ScreenPoint> Points, bool Closed) : VisualCommand;

/// <summary>A stroked circular arc.</summary>
/// <param name="Pen">The stroke.</param>
/// <param name="Arc">The arc.</param>
public sealed record VisualArcCommand(VisualPen Pen, VisualArc Arc) : VisualCommand;

/// <summary>
/// A line of text with its top edge at the anchor's y. <paramref name="DevicePixelsPerDip"/> records which scale the old
/// WPF code handed to <c>FormattedText</c>: the element's device scale (true) or a literal 1 (false; a few labels of the
/// transformer schematic). WPF uses it to snap glyphs to device pixels, so a WPF front end must keep it to keep the
/// pixels; other front ends may ignore it.
/// </summary>
/// <param name="Text">The text (as formatted by the geometry, in the culture the old code used).</param>
/// <param name="Weight">Bold or semi-bold.</param>
/// <param name="Size">The font size in device-independent pixels.</param>
/// <param name="Fill">The text colour.</param>
/// <param name="Anchor">The anchor: the top-centre (centred) or the top-left (left aligned) of the text.</param>
/// <param name="Align">Centred or left aligned.</param>
/// <param name="DevicePixelsPerDip">True for the device scale, false for a literal 1.</param>
public sealed record VisualTextCommand(string Text, VisualTextWeight Weight, double Size, VisualFill Fill, ScreenPoint Anchor,
    VisualTextAlign Align, bool DevicePixelsPerDip = true) : VisualCommand;

/// <summary>Start a translated layer: everything up to the matching <see cref="VisualPopCommand"/> moves by (X, Y).</summary>
/// <param name="X">The shift across.</param>
/// <param name="Y">The shift down.</param>
public sealed record VisualPushTranslateCommand(double X, double Y) : VisualCommand;

/// <summary>Start a scaled layer: everything up to the matching <see cref="VisualPopCommand"/> is scaled about the origin.</summary>
/// <param name="ScaleX">The scale across.</param>
/// <param name="ScaleY">The scale down.</param>
public sealed record VisualPushScaleCommand(double ScaleX, double ScaleY) : VisualCommand;

/// <summary>Start a translucent layer: everything up to the matching <see cref="VisualPopCommand"/> is drawn at this opacity.</summary>
/// <param name="Opacity">The layer opacity, 0 to 1.</param>
public sealed record VisualPushOpacityCommand(double Opacity) : VisualCommand;

/// <summary>End the most recent translate, scale or opacity layer.</summary>
public sealed record VisualPopCommand : VisualCommand
{
    /// <summary>The one instance (the command carries no data).</summary>
    public static readonly VisualPopCommand Instance = new();
}

/// <summary>
/// An ordered list of drawing calls, built with the methods below and then handed to a front end. Every push must be
/// popped: <see cref="Commands"/> refuses (throws) while a layer is still open, and a pop with nothing open throws at
/// once, so an unbalanced picture fails loud where it is built, never half-drawn on screen.
/// </summary>
public sealed class VisualDisplayList
{
    private readonly List<VisualCommand> _commands = new();
    private int _depth;

    /// <summary>The calls in drawing order. Throws when a pushed layer has not been popped.</summary>
    public IReadOnlyList<VisualCommand> Commands
    {
        get
        {
            if (_depth != 0)
                throw new InvalidOperationException($"the display list has {_depth} open layer(s): every push needs a pop");
            return _commands;
        }
    }

    /// <summary>The number of calls so far (layers open or not).</summary>
    public int Count => _commands.Count;

    /// <summary>Add a call as it is (used by builders that already hold a command, such as the diagram toolkit).</summary>
    public void Add(VisualCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case VisualPushTranslateCommand or VisualPushScaleCommand or VisualPushOpacityCommand: _depth++; break;
            case VisualPopCommand:
                if (_depth == 0) throw new InvalidOperationException("pop with no open layer");
                _depth--;
                break;
        }
        _commands.Add(command);
    }

    /// <summary>A stroked line.</summary>
    public void Line(VisualPen pen, ScreenPoint from, ScreenPoint to) => Add(new VisualLineCommand(pen, from, to));

    /// <summary>An ellipse, filled and/or stroked.</summary>
    public void Ellipse(VisualFill? fill, VisualPen? pen, ScreenPoint centre, double rx, double ry) =>
        Add(new VisualEllipseCommand(fill, pen, centre, rx, ry));

    /// <summary>A rounded rectangle, filled and/or stroked.</summary>
    public void Rect(VisualFill? fill, VisualPen? pen, double x, double y, double w, double h, double rx, double ry) =>
        Add(new VisualRectCommand(fill, pen, x, y, w, h, rx, ry));

    /// <summary>A closed, filled polygon with no outline (the arrow heads of the asset visuals).</summary>
    public void Polygon(VisualFill fill, params ScreenPoint[] points) => Add(new VisualPathCommand(fill, null, points, Closed: true));

    /// <summary>An open stroked polyline.</summary>
    public void Polyline(VisualPen pen, IReadOnlyList<ScreenPoint> points) => Add(new VisualPathCommand(null, pen, points, Closed: false));

    /// <summary>A stroked arc.</summary>
    public void Arc(VisualPen pen, VisualArc arc) => Add(new VisualArcCommand(pen, arc));

    /// <summary>A line of text.</summary>
    public void Text(string text, VisualTextWeight weight, double size, VisualFill fill, ScreenPoint anchor,
        VisualTextAlign align = VisualTextAlign.Centre, bool devicePixelsPerDip = true) =>
        Add(new VisualTextCommand(text, weight, size, fill, anchor, align, devicePixelsPerDip));

    /// <summary>Start a translated layer.</summary>
    public void PushTranslate(double x, double y) => Add(new VisualPushTranslateCommand(x, y));

    /// <summary>Start a scaled layer.</summary>
    public void PushScale(double sx, double sy) => Add(new VisualPushScaleCommand(sx, sy));

    /// <summary>Start a translucent layer.</summary>
    public void PushOpacity(double opacity) => Add(new VisualPushOpacityCommand(opacity));

    /// <summary>End the most recent layer.</summary>
    public void Pop() => Add(VisualPopCommand.Instance);
}
