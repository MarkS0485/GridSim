using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.App.Dashboard;

/// <summary>
/// Turns a neutral colour token (<see cref="UiColour"/>) from the dashboard model into the Avalonia brush that paints
/// it (UI programme Phase 1.4). This is the Avalonia twin of the WPF <c>Palette.Frozen</c> the WPF dashboard adapter
/// uses: the brush always carries the token's own four bytes, unchanged, so a lamp, bar or badge shows exactly the
/// colour the WPF dashboard shows for the same frame.
///
/// <para>Where a token is one of the ported theme colours (Theme/Palette.axaml: Bg, Panel, ..., Red, Track), the theme's
/// own brush resource is returned, so the dashboard and the rest of the app share one brush per theme colour (the
/// steady fragility trend, for example, is the theme's Muted). Any other token (the colour ramps are continuous) gets a
/// cached immutable brush of its own, so a dashboard re-binding every frame does not build a new brush each time.</para>
///
/// <para>Fail loud: a value that is not a colour token is a wiring mistake, so it throws rather than paint nothing. A
/// null value (no data context yet, while a template is being built) gives no brush.</para>
/// </summary>
public sealed class UiColourBrushConverter : IValueConverter
{
    /// <summary>The one instance the view uses ({x:Static local:UiColourBrushConverter.Instance}).</summary>
    public static readonly UiColourBrushConverter Instance = new();

    /// <summary>The theme brush keys a token may resolve to, in Theme/Palette.axaml order.</summary>
    public static readonly IReadOnlyList<string> ThemeBrushKeys =
        new[] { "Bg", "Panel", "PanelHi", "Border", "Accent", "Text", "Muted", "Green", "Amber", "Red", "Track" };

    private static readonly Dictionary<UiColour, IBrush> Cache = new();
    private static readonly object Gate = new();
    private static Dictionary<Color, IBrush>? _themeByColour;   // the running application's theme brushes, by colour
    private static Application? _themeApp;                      // the application they were read from

    /// <summary>The brush for <paramref name="token"/>: the theme's brush when the token is a theme colour, else a cached
    /// immutable brush with the token's four bytes.</summary>
    public static IBrush BrushFor(UiColour token)
    {
        lock (Gate)
        {
            // A different application (the headless tests start one per test) has its own theme brushes: forget the
            // brushes chosen for the previous one, so a theme colour always paints with the running theme's brush.
            if (!ReferenceEquals(_themeApp, Application.Current))
            {
                _themeByColour = null;
                _themeApp = null;
                Cache.Clear();
            }
            if (Cache.TryGetValue(token, out IBrush? cached)) return cached;
            Color colour = Color.FromArgb(token.A, token.R, token.G, token.B);
            IBrush brush = ThemeBrushes().TryGetValue(colour, out IBrush? theme) ? theme : new ImmutableSolidColorBrush(colour);
            // Cached only once the theme has been read, so a call made before the application started cannot pin a
            // theme colour to a brush of its own.
            if (_themeByColour is not null) Cache[token] = brush;
            return brush;
        }
    }

    /// <summary>The theme's brushes by colour, read once per running application from its resources (dark only, as the
    /// app is); with no running application (a plain unit test) there are none, and every token gets its own brush.</summary>
    private static Dictionary<Color, IBrush> ThemeBrushes()
    {
        if (_themeByColour is not null) return _themeByColour;
        var map = new Dictionary<Color, IBrush>();
        Application? app = Application.Current;
        if (app is null) return map;   // not cached: the theme is read once an application is running
        foreach (string key in ThemeBrushKeys)
            if (app.TryGetResource(key, ThemeVariant.Dark, out object? r) && r is ISolidColorBrush b && b.Opacity == 1.0)
                map.TryAdd(b.Color, (IBrush)b);   // first key wins if two theme colours were ever equal
        _themeByColour = map;
        _themeApp = app;
        return map;
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => null,
        UiColour c => BrushFor(c),
        _ => throw new ArgumentException($"The dashboard colour converter takes a UiColour token; got {value.GetType().Name}.", nameof(value)),
    };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A dashboard colour is shown, never edited.");
}

/// <summary>
/// Turns the dashboard legend's mode (<see cref="DashboardModel.Legend"/>) into the swatch's left-to-right gradient,
/// built from <see cref="DashboardModel.LegendRampStops"/>, the same stops the WPF adapter's <c>BuildRamp</c> uses, so
/// the swatch and the map tint agree. One brush per mode, built once.
/// </summary>
public sealed class LegendBrushConverter : IValueConverter
{
    /// <summary>The one instance the view uses.</summary>
    public static readonly LegendBrushConverter Instance = new();

    private static readonly Dictionary<MapColourMode, IBrush> Cache = new();
    private static readonly object Gate = new();

    /// <summary>The swatch brush for <paramref name="mode"/>.</summary>
    public static IBrush BrushFor(MapColourMode mode)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(mode, out IBrush? cached)) return cached;
            var stops = new List<ImmutableGradientStop>();
            foreach ((UiColour colour, double offset) in DashboardModel.LegendRampStops(mode))
                stops.Add(new ImmutableGradientStop(offset, Color.FromArgb(colour.A, colour.R, colour.G, colour.B)));
            // Left to right across the swatch, as WPF's LinearGradientBrush from (0,0) to (1,0) in relative units.
            IBrush brush = new ImmutableLinearGradientBrush(
                stops, startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
            Cache[mode] = brush;
            return brush;
        }
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => null,
        MapColourMode m => BrushFor(m),
        _ => throw new ArgumentException($"The legend converter takes a MapColourMode; got {value.GetType().Name}.", nameof(value)),
    };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The legend is shown, never edited.");
}

/// <summary>
/// Turns the frequency dial's needle angle (<see cref="DashboardModel.FreqAngle"/>, degrees, clockwise from straight
/// up) into the needle's rotation about the dial's pivot. The pivot (75, 62) is the WPF dial's: its
/// <c>RotateTransform CenterX="75" CenterY="62"</c>, in a 150 x 92 dial. The needle is drawn with its render-transform
/// origin at its top-left corner, so this rotation is about exactly that point.
/// </summary>
public sealed class NeedleRotationConverter : IValueConverter
{
    /// <summary>The one instance the view uses.</summary>
    public static readonly NeedleRotationConverter Instance = new();

    /// <summary>The dial's pivot, x (WPF CenterX).</summary>
    public const double PivotX = 75;

    /// <summary>The dial's pivot, y (WPF CenterY).</summary>
    public const double PivotY = 62;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => null,
        double angle => new RotateTransform(angle, PivotX, PivotY),
        _ => throw new ArgumentException($"The needle converter takes an angle in degrees; got {value.GetType().Name}.", nameof(value)),
    };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The needle is shown, never edited.");
}
