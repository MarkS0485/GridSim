using System.Windows.Media;
using GridSim.Ui.Presentation;

namespace GridSim.Wpf.Visuals;

/// <summary>
/// Colour language for the control suite  -  Underground line palette plus the live status ramps.
/// UI programme Phase 0.2: the colour arithmetic moved to the platform-neutral <see cref="ColourRamps"/>
/// (src/GridSim.Ui/Presentation), so the planned Avalonia app shares it. This class is now the WPF side of that
/// boundary: it turns each neutral <see cref="UiColour"/> into a WPF <see cref="Color"/> (the same four bytes) and keeps
/// the frozen-brush cache. Every colour it returns is byte for byte what it returned before the move.
/// </summary>
public static class Palette
{
    /// <summary>Classic London Underground line colours, reused to tint the transmission corridors.</summary>
    public static readonly Color[] TubeLines = ColourRamps.TubeLines.Select(ToColor).ToArray();

    /// <summary>The WPF colour for a neutral colour token: the same alpha, red, green and blue bytes.</summary>
    public static Color ToColor(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Color FromHex(string hex) => ToColor(ColourRamps.FromHex(hex));

    public static Color Lerp(Color a, Color b, double t) =>
        ToColor(ColourRamps.Lerp(new UiColour(a.A, a.R, a.G, a.B), new UiColour(b.A, b.R, b.G, b.B), t));

    /// <summary>Voltage band -> colour: blue (low)  green (nominal 1.0)  amber/red (high). Out-of-band saturates.</summary>
    public static Color Voltage(double vm) => ToColor(ColourRamps.Voltage(vm));

    /// <summary>Loading fraction (0..1+) -> green/amber/red.</summary>
    public static Color Loading(double frac) => ToColor(ColourRamps.Loading(frac));

    /// <summary>Asset condition/fragility index (0 healthy .. 1 critical) -> green -> amber -> red.</summary>
    public static Color Condition(double idx01) => ToColor(ColourRamps.Condition(idx01));

    /// <summary>Branch stress fraction (0 cool .. 1 hot) -> blue -> amber -> red.</summary>
    public static Color Stress(double frac) => ToColor(ColourRamps.Stress(frac));

    private static readonly Dictionary<Color, SolidColorBrush> FrozenCache = new();

    public static SolidColorBrush Frozen(Color c)
    {
        // Cache by colour so repeated calls return the SAME frozen brush instance. FastSchematic.DrawFleet
        // batches fleet markers into one geometry per brush (keyed by reference equality); without this
        // cache every marker got a brand-new brush and thus its own StreamGeometry every frame.
        lock (FrozenCache)
        {
            if (FrozenCache.TryGetValue(c, out SolidColorBrush? cached)) return cached;
            var b = new SolidColorBrush(c);
            b.Freeze();
            FrozenCache[c] = b;
            return b;
        }
    }

    /// <summary>The token-to-brush converter: the cached frozen WPF brush for a neutral colour token.</summary>
    public static SolidColorBrush Frozen(UiColour c) => Frozen(ToColor(c));
}
