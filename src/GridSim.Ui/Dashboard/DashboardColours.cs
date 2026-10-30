using GridSim.Ui.Presentation;

namespace GridSim.Ui.Dashboard;

/// <summary>
/// The fixed colour tokens of the dashboard: the starting colours its lamps and badges show before the first frame,
/// and the muted grey of a steady trend. Each one is the exact four bytes of the WPF brush the dashboard used before UI
/// programme Phase 0.2 (the WPF named brushes <c>Brushes.Gray</c>, <c>Brushes.LightGreen</c> and
/// <c>Brushes.Transparent</c>, and the dashboard's own <c>#8492A0</c>), so a front end that turns a token back into a
/// brush shows the colour it showed before. Every live colour comes from <see cref="ColourRamps"/>.
/// </summary>
public static class DashboardColours
{
    /// <summary>The WPF <c>Brushes.Gray</c>: #FF808080. The "no reading yet" colour of every lamp and bar.</summary>
    public static readonly UiColour Gray = new(0xFF, 0x80, 0x80, 0x80);

    /// <summary>The WPF <c>Brushes.LightGreen</c>: #FF90EE90. The frequency read-out before the first frame.</summary>
    public static readonly UiColour LightGreen = new(0xFF, 0x90, 0xEE, 0x90);

    /// <summary>The WPF <c>Brushes.Transparent</c>: #00FFFFFF (white at zero alpha). The empty trip-verdict badge.</summary>
    public static readonly UiColour Transparent = new(0x00, 0xFF, 0xFF, 0xFF);

    /// <summary>The muted slate of a steady (or unknown) fragility trend: #8492A0, opaque.</summary>
    public static readonly UiColour Muted = ColourRamps.FromHex("#8492A0");
}
