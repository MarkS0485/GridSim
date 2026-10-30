using System.Globalization;
using GridSim.Ui.Presentation;

// Test support for UiVisualsParityTests (UI programme Phase 1.3). NOT production code.
//
// A recording stand-in for the small part of WPF that the five instrument controls (Gauge, Sparkline, FrequencyTrace,
// Phasor, PqDiagram) use: dependency properties, pens, brushes, typefaces, formatted text, stream geometry and the
// drawing context. Every type has the WPF name, constructor and members those files call, so the controls' source can be
// compiled here VERBATIM (only the using and namespace lines differ), twice:
//   - OldWpfVisuals/*.cs: the five files exactly as they were before the maths moved to src/GridSim.Ui/Visuals (the
//     oracle; UiVisualsParityTests checks a SHA-256 of each copy's text against the hash of the original file);
//   - NewWpfVisuals/*.cs: the five files as they are now in src/GridSim.Wpf/Visuals (UiVisualsParityTests checks each
//     copy's text equals the real file, so the copy cannot drift from the code the WPF app runs).
// Rendering either version into a RecordingContext writes one line per drawing call, with every coordinate, size, colour
// and flag printed exactly (each double as its round-trip text and its 64 bits), so two renders draw the same pixels
// exactly when their recordings are equal, line for line.
//
// The one thing that is not WPF's own is text measurement: FormattedText.Width and Height are a fixed function of the
// text and the size (WPF measures the font). Both versions see the same function, so a position computed from a measured
// width is compared on equal terms.

// CA1822 (make static) is off for this file only: these members are instance members because the WPF members they stand
// in for are, and the verbatim copies call them on instances (pen.Freeze(), brush.CanFreeze, InvalidateVisual()).
#pragma warning disable CA1822

namespace GridSim.Tests.Ui.WpfShim;

/// <summary>Exact text of a double: its round-trip form and its 64 bits.</summary>
internal static class Exact
{
    public static string F(double d) =>
        d.ToString("R", CultureInfo.InvariantCulture) + "#" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
}

public readonly struct Point
{
    public Point(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
    public override string ToString() => $"({Exact.F(X)}, {Exact.F(Y)})";
}

public readonly struct Size
{
    public Size(double width, double height) { Width = width; Height = height; }
    public double Width { get; }
    public double Height { get; }
    public override string ToString() => $"[{Exact.F(Width)} x {Exact.F(Height)}]";
}

/// <summary>The WPF rectangle: a negative size is refused with an ArgumentException, as WPF refused it.</summary>
public readonly struct Rect
{
    public Rect(double x, double y, double width, double height)
    {
        if (width < 0 || height < 0) throw new ArgumentException("Width and Height must be non-negative.");
        X = x; Y = y; Width = width; Height = height;
    }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public override string ToString() => $"rect({Exact.F(X)}, {Exact.F(Y)}, {Exact.F(Width)}, {Exact.F(Height)})";
}

public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>The WPF Visuals/Palette as these controls use it: hex parsing (the shared ColourRamps parser the WPF Palette
/// delegates to) and a frozen solid brush.</summary>
public static class Palette
{
    public static Color FromHex(string hex)
    {
        UiColour c = ColourRamps.FromHex(hex);
        return new Color(c.A, c.R, c.G, c.B);
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>The WPF freezable base (added 2026-10-20 for UI programme ASSETS A1: the asset visuals freeze through a
/// generic helper constrained to it). Freezing is a no-op here; the recording prints values, not identities.</summary>
public abstract class Freezable
{
    public bool CanFreeze => true;
    public void Freeze() { }
}

public abstract class Brush : Freezable
{
    public double Opacity { get; set; } = 1.0;
    public abstract Brush Clone();
}

public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color color) { Color = color; }
    public Color Color { get; }
    public override Brush Clone() => new SolidColorBrush(Color) { Opacity = Opacity };
    public override string ToString() => $"brush({Color} op {Exact.F(Opacity)})";
}

public enum PenLineCap { Flat, Square, Round, Triangle }

public enum PenLineJoin { Miter, Bevel, Round }

public sealed class DashStyle
{
    public DashStyle(double[] dashes, double offset) { Dashes = dashes; Offset = offset; }
    public double[] Dashes { get; }
    public double Offset { get; }
    public override string ToString() => $"dash[{string.Join(",", Dashes.Select(Exact.F))} @ {Exact.F(Offset)}]";
}

/// <summary>WPF's stock dash styles (System.Windows.Media.DashStyles), with WPF's arrays and offsets. Added 2026-10-20
/// for UI programme ASSETS A1 (the transformer schematic's windings and fan rings).</summary>
public static class DashStyles
{
    public static DashStyle Dash { get; } = new(new[] { 2.0, 2.0 }, 1);
    public static DashStyle DashDot { get; } = new(new[] { 2.0, 2.0, 0.0, 2.0 }, 1);
}

public sealed class Pen : Freezable
{
    public Pen(Brush brush, double thickness) { Brush = brush; Thickness = thickness; }
    public Brush Brush { get; }
    public double Thickness { get; }
    public PenLineCap StartLineCap { get; set; }
    public PenLineCap EndLineCap { get; set; }
    public PenLineJoin LineJoin { get; set; }
    public DashStyle? DashStyle { get; set; }
    public override string ToString() =>
        $"pen({Brush} w {Exact.F(Thickness)} caps {StartLineCap}/{EndLineCap} join {LineJoin} {DashStyle?.ToString() ?? "solid"})";
}

public sealed class FontFamily
{
    public FontFamily(string names) { Names = names; }
    public string Names { get; }
}

public enum FontStyle { Normal }
public enum FontWeight { Normal, SemiBold, Bold }
public enum FontStretch { Normal }
public static class FontStyles { public static FontStyle Normal => FontStyle.Normal; }
public static class FontWeights { public static FontWeight SemiBold => FontWeight.SemiBold; public static FontWeight Bold => FontWeight.Bold; }
public static class FontStretches { public static FontStretch Normal => FontStretch.Normal; }

public sealed class Typeface
{
    public Typeface(FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        Family = family; Style = style; Weight = weight; Stretch = stretch;
    }
    public FontFamily Family { get; }
    public FontStyle Style { get; }
    public FontWeight Weight { get; }
    public FontStretch Stretch { get; }
    public override string ToString() => $"{Family.Names}/{Weight}";
}

public enum FlowDirection { LeftToRight, RightToLeft }

public enum TextAlignment { Left, Right, Center, Justify }

public sealed class FormattedText
{
    public FormattedText(string text, CultureInfo culture, FlowDirection flow, Typeface face, double size, Brush brush, double pixelsPerDip)
    {
        Text = text; Culture = culture; Face = face; Size = size; Brush = brush; PixelsPerDip = pixelsPerDip;
    }
    public string Text { get; }
    public CultureInfo Culture { get; }
    public Typeface Face { get; }
    public double Size { get; }
    public Brush Brush { get; }
    public double PixelsPerDip { get; }
    public TextAlignment TextAlignment { get; set; }
    public double MaxTextWidth { get; set; }

    /// <summary>The stand-in measurement (see the file header): a fixed function of the text and the size.</summary>
    public double Width => Text.Length * Size * 0.55;

    /// <summary>The stand-in line height.</summary>
    public double Height => Size * 1.3;

    public override string ToString() =>
        $"'{Text}' [{Culture.Name}] {Face} size {Exact.F(Size)} {Brush} pps {Exact.F(PixelsPerDip)} align {TextAlignment} max {Exact.F(MaxTextWidth)}";
}

public enum SweepDirection { Counterclockwise, Clockwise }

/// <summary>Records each figure call (the WPF StreamGeometryContext calls these controls make).</summary>
public sealed class StreamGeometryContext : IDisposable
{
    private readonly List<string> _log;
    internal StreamGeometryContext(List<string> log) { _log = log; }
    public void BeginFigure(Point start, bool isFilled, bool isClosed) => _log.Add($"begin {start} filled {isFilled} closed {isClosed}");
    public void LineTo(Point point, bool isStroked, bool isSmoothJoin) => _log.Add($"line {point} stroked {isStroked} smooth {isSmoothJoin}");
    public void PolyLineTo(IList<Point> points, bool isStroked, bool isSmoothJoin) =>
        _log.Add($"poly [{string.Join(" ", points)}] stroked {isStroked} smooth {isSmoothJoin}");
    public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked, bool isSmoothJoin) =>
        _log.Add($"arc {point} {size} rot {Exact.F(rotationAngle)} large {isLargeArc} {sweepDirection} stroked {isStroked} smooth {isSmoothJoin}");
    public void Close() => _log.Add("close");
    public void Dispose() => _log.Add("end");
}

public sealed class StreamGeometry
{
    private readonly List<string> _log = new();
    public StreamGeometryContext Open() => new(_log);
    public void Freeze() { }
    public override string ToString() => "{" + string.Join("; ", _log) + "}";
}

/// <summary>Records every drawing call, one line each, exactly.</summary>
public sealed class DrawingContext
{
    public List<string> Ops { get; } = new();
    public void DrawLine(Pen pen, Point a, Point b) => Ops.Add($"line {pen} {a} -> {b}");
    public void DrawEllipse(Brush? brush, Pen? pen, Point centre, double rx, double ry) =>
        Ops.Add($"ellipse {brush?.ToString() ?? "-"} {pen?.ToString() ?? "-"} {centre} r {Exact.F(rx)},{Exact.F(ry)}");
    public void DrawGeometry(Brush? brush, Pen? pen, StreamGeometry geometry) =>
        Ops.Add($"geometry {brush?.ToString() ?? "-"} {pen?.ToString() ?? "-"} {geometry}");
    public void DrawText(FormattedText text, Point origin) => Ops.Add($"text {text} at {origin}");
    public void DrawRectangle(Brush? brush, Pen? pen, Rect rect) => Ops.Add($"rect {brush?.ToString() ?? "-"} {pen?.ToString() ?? "-"} {rect}");

    // Added 2026-10-20 for UI programme ASSETS A1 (the substation map and the transformer schematic).
    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rect, double radiusX, double radiusY) =>
        Ops.Add($"roundrect {brush?.ToString() ?? "-"} {pen?.ToString() ?? "-"} {rect} r {Exact.F(radiusX)},{Exact.F(radiusY)}");
    public void PushTransform(Transform transform) => Ops.Add($"push {transform}");
    public void PushOpacity(double opacity) => Ops.Add($"push opacity {Exact.F(opacity)}");
    public void Pop() => Ops.Add("pop");
}

/// <summary>The WPF transforms the asset visuals push (added 2026-10-20 for UI programme ASSETS A1).</summary>
public abstract class Transform { }

public sealed class TranslateTransform : Transform
{
    public TranslateTransform(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
    public override string ToString() => $"translate({Exact.F(X)}, {Exact.F(Y)})";
}

public sealed class ScaleTransform : Transform
{
    public ScaleTransform(double scaleX, double scaleY) { ScaleX = scaleX; ScaleY = scaleY; }
    public double ScaleX { get; }
    public double ScaleY { get; }
    public override string ToString() => $"scale({Exact.F(ScaleX)}, {Exact.F(ScaleY)})";
}

[Flags]
public enum FrameworkPropertyMetadataOptions { None = 0, AffectsRender = 1 }

public sealed class FrameworkPropertyMetadata
{
    public FrameworkPropertyMetadata(object? defaultValue, FrameworkPropertyMetadataOptions options) { DefaultValue = defaultValue; Options = options; }
    public object? DefaultValue { get; }
    public FrameworkPropertyMetadataOptions Options { get; }
}

public sealed class DependencyProperty
{
    private DependencyProperty(string name, Type type, Type owner, FrameworkPropertyMetadata meta)
    {
        Name = name; PropertyType = type; OwnerType = owner; Metadata = meta;
    }
    public string Name { get; }
    public Type PropertyType { get; }
    public Type OwnerType { get; }
    public FrameworkPropertyMetadata Metadata { get; }
    public static DependencyProperty Register(string name, Type type, Type owner, FrameworkPropertyMetadata meta) => new(name, type, owner, meta);
}

/// <summary>The WPF element as these controls use it: a property store with defaults, a settable size, and a render.</summary>
public abstract class FrameworkElement
{
    private readonly Dictionary<DependencyProperty, object?> _values = new();
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public object GetValue(DependencyProperty dp) => (_values.TryGetValue(dp, out object? v) ? v : dp.Metadata.DefaultValue)!;
    public void SetValue(DependencyProperty dp, object? value) => _values[dp] = value;
    public void InvalidateVisual() { }
    protected virtual void OnRender(DrawingContext dc) { }

    /// <summary>Render at the given size into a fresh recording; an exception from the render is recorded as the last line.</summary>
    public List<string> Record(double width, double height)
    {
        ActualWidth = width;
        ActualHeight = height;
        var dc = new DrawingContext();
        try { OnRender(dc); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            dc.Ops.Add("THREW " + ex.GetType().Name);
        }
        return dc.Ops;
    }
}

public readonly struct DpiScale
{
    public DpiScale(double pixelsPerDip) { PixelsPerDip = pixelsPerDip; }
    public double PixelsPerDip { get; }
}

public static class VisualTreeHelper
{
    /// <summary>
    /// The device scale a test renders at: 1.0 unless a test sets another (added 2026-10-20 for UI programme ASSETS A1,
    /// so a text call that used the device scale and one that used a literal 1 can be told apart). It is per async
    /// flow, so test classes running in parallel never see each other's setting.
    /// </summary>
    public static readonly AsyncLocal<double?> PixelsPerDipForTest = new();

    public static DpiScale GetDpi(FrameworkElement element) => new(PixelsPerDipForTest.Value ?? 1.0);
}
