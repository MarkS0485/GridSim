using System;
using System.Globalization;

namespace GridSim.Web.Components.Visuals;

/// <summary>
/// Colour language for the web control suite - the Underground line palette plus the live status
/// ramps. A faithful port of <c>GridSim.Wpf.Visuals.Palette</c>, but returning CSS hex strings
/// ("#RRGGBB") so the values can be dropped straight into SVG attributes / inline styles for
/// data-driven colours (voltage, loading, condition, stress). Static breakpoints/chrome stay in
/// CSS variables; only the value-driven colours come from here.
/// </summary>
public static class WebPalette
{
    private readonly record struct Rgb(int R, int G, int B);

    private static Rgb Parse(string hex)
    {
        hex = hex.TrimStart('#');
        int i = hex.Length == 8 ? 2 : 0;                 // skip alpha if present
        int r = Convert.ToInt32(hex.Substring(i, 2), 16);
        int g = Convert.ToInt32(hex.Substring(i + 2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(i + 4, 2), 16);
        return new Rgb(r, g, b);
    }

    private static string Hex(Rgb c) =>
        "#" + c.R.ToString("X2", CultureInfo.InvariantCulture)
            + c.G.ToString("X2", CultureInfo.InvariantCulture)
            + c.B.ToString("X2", CultureInfo.InvariantCulture);

    private static Rgb LerpRgb(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Normalise any #RGB/#RRGGBB/#AARRGGBB hex to "#RRGGBB" (alpha discarded).</summary>
    public static string FromHex(string hex) => Hex(Parse(hex));

    /// <summary>Blend two hex colours by t (0..1, clamped); returns "#RRGGBB".</summary>
    public static string Lerp(string a, string b, double t) => Hex(LerpRgb(Parse(a), Parse(b), t));

    private static readonly Rgb Blue = Parse("#3FA9F5");
    private static readonly Rgb Green = Parse("#2FD27F");
    private static readonly Rgb Amber = Parse("#F3C13A");
    private static readonly Rgb Red = Parse("#FF5B5B");

    /// <summary>Voltage band -> colour: blue (low), green (nominal 1.0), amber/red (high). Out-of-band saturates.</summary>
    public static string Voltage(double vm)
    {
        if (vm <= 0.96) return Hex(Blue);
        if (vm < 1.00) return Hex(LerpRgb(Blue, Green, (vm - 0.96) / 0.04));
        if (vm <= 1.03) return Hex(LerpRgb(Green, Amber, (vm - 1.00) / 0.03));
        if (vm <= 1.06) return Hex(LerpRgb(Amber, Red, (vm - 1.03) / 0.03));
        return Hex(Red);
    }

    /// <summary>Loading fraction (0..1+) -> green/amber/red.</summary>
    public static string Loading(double frac)
    {
        if (frac <= 0.6) return Hex(LerpRgb(Green, Amber, frac / 0.6));
        if (frac <= 1.0) return Hex(LerpRgb(Amber, Red, (frac - 0.6) / 0.4));
        return Hex(Red);
    }

    /// <summary>Asset condition/fragility index (0 healthy .. 1 critical) -> green -> amber -> red.</summary>
    public static string Condition(double idx01)
    {
        double t = Math.Clamp(idx01, 0, 1);
        return t <= 0.5 ? Hex(LerpRgb(Green, Amber, t / 0.5)) : Hex(LerpRgb(Amber, Red, (t - 0.5) / 0.5));
    }

    /// <summary>Branch stress fraction (0 cool .. 1 hot) -> blue -> amber -> red.</summary>
    public static string Stress(double frac)
    {
        double t = Math.Clamp(frac, 0, 1);
        return t <= 0.5 ? Hex(LerpRgb(Blue, Amber, t / 0.5)) : Hex(LerpRgb(Amber, Red, (t - 0.5) / 0.5));
    }

    /// <summary>Classic London Underground line colours, reused to tint the transmission corridors.</summary>
    public static readonly string[] TubeLines =
    {
        "#E32017", // Central red
        "#0098D4", // Victoria blue
        "#00782A", // District green
        "#9B0056", // Metropolitan magenta
        "#F3A9BB", // Hammersmith pink
        "#003688", // Piccadilly dark blue
        "#EE7C0E", // Overground orange
        "#6950A1", // Elizabeth purple
        "#B36305", // Bakerloo brown
        "#00A4A7", // Waterloo teal
        "#FFD300", // Circle yellow
        "#A0A5A9", // Jubilee grey
    };
}
