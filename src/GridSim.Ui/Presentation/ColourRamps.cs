using System.Globalization;

namespace GridSim.Ui.Presentation;

/// <summary>
/// The colour language of the control suite, without a user-interface framework: the London Underground line palette
/// that tints the transmission corridors, and the live status ramps (voltage, loading, condition, stress) that colour
/// the map, the lamps and the legends.
///
/// <para>Moved here from the WPF <c>Visuals/Palette.cs</c> (UI programme Phase 0.2) so the planned Avalonia app reads
/// the same colours from the same code. The arithmetic is the old WPF arithmetic, operation for operation: every byte
/// of every colour is unchanged (the WPF <c>Palette</c> now converts these results to its own <c>Color</c> type, and
/// <c>UiColourRampsTests</c> pins the ramps against a verbatim copy of the old code).</para>
/// </summary>
public static class ColourRamps
{
    /// <summary>Classic London Underground line colours, reused to tint the transmission corridors.</summary>
    public static IReadOnlyList<UiColour> TubeLines { get; } = new[]
    {
        FromHex("#E32017"), // Central red
        FromHex("#0098D4"), // Victoria blue
        FromHex("#00782A"), // District green
        FromHex("#9B0056"), // Metropolitan magenta
        FromHex("#F3A9BB"), // Hammersmith pink
        FromHex("#003688"), // Piccadilly dark blue
        FromHex("#EE7C0E"), // Overground orange
        FromHex("#6950A1"), // Elizabeth purple
        FromHex("#B36305"), // Bakerloo brown
        FromHex("#00A4A7"), // Waterloo teal
        FromHex("#FFD300"), // Circle yellow
        FromHex("#A0A5A9"), // Jubilee grey
    };

    /// <summary>
    /// Parse <c>#RRGGBB</c> or <c>#AARRGGBB</c> (the leading <c>#</c> is optional). Six digits give an opaque colour.
    /// A string that is not hexadecimal throws (<see cref="FormatException"/>), as the old WPF parser did.
    /// </summary>
    public static UiColour FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255, i = 0;
        if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0, 2), 16); i = 2; }
        byte r = Convert.ToByte(hex.Substring(i, 2), 16);
        byte g = Convert.ToByte(hex.Substring(i + 2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(i + 4, 2), 16);
        return new UiColour(a, r, g, b);
    }

    /// <summary>The colour as <c>#AARRGGBB</c> (upper case), for logs, tests and front ends that take a hex string.</summary>
    public static string ToHex(UiColour c) =>
        string.Create(CultureInfo.InvariantCulture, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

    /// <summary>
    /// Straight-line blend from <paramref name="a"/> (t = 0) to <paramref name="b"/> (t = 1), t clamped to 0..1. Each
    /// channel is <c>(byte)(a + (b - a) * t)</c>: computed in double and truncated toward zero by the cast, exactly as
    /// the old WPF code did on <c>System.Windows.Media.Color</c>'s byte channels.
    /// </summary>
    public static UiColour Lerp(UiColour a, UiColour b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new UiColour(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // The four anchor colours of every ramp below.
    private static readonly UiColour Blue = FromHex("#3FA9F5");
    private static readonly UiColour Green = FromHex("#2FD27F");
    private static readonly UiColour Amber = FromHex("#F3C13A");
    private static readonly UiColour Red = FromHex("#FF5B5B");

    /// <summary>Voltage band -> colour: blue (low)  green (nominal 1.0)  amber/red (high). Out-of-band saturates.</summary>
    public static UiColour Voltage(double vm)
    {
        if (vm <= 0.96) return Blue;
        if (vm < 1.00) return Lerp(Blue, Green, (vm - 0.96) / 0.04);
        if (vm <= 1.03) return Lerp(Green, Amber, (vm - 1.00) / 0.03);
        if (vm <= 1.06) return Lerp(Amber, Red, (vm - 1.03) / 0.03);
        return Red;
    }

    /// <summary>Loading fraction (0..1+) -> green/amber/red.</summary>
    public static UiColour Loading(double frac)
    {
        if (frac <= 0.6) return Lerp(Green, Amber, frac / 0.6);
        if (frac <= 1.0) return Lerp(Amber, Red, (frac - 0.6) / 0.4);
        return Red;
    }

    /// <summary>Asset condition/fragility index (0 healthy .. 1 critical) -> green -> amber -> red.</summary>
    public static UiColour Condition(double idx01)
    {
        double t = Math.Clamp(idx01, 0, 1);
        return t <= 0.5 ? Lerp(Green, Amber, t / 0.5) : Lerp(Amber, Red, (t - 0.5) / 0.5);
    }

    /// <summary>Branch stress fraction (0 cool .. 1 hot) -> blue -> amber -> red.</summary>
    public static UiColour Stress(double frac)
    {
        double t = Math.Clamp(frac, 0, 1);
        return t <= 0.5 ? Lerp(Blue, Amber, t / 0.5) : Lerp(Amber, Red, (t - 0.5) / 0.5);
    }
}
