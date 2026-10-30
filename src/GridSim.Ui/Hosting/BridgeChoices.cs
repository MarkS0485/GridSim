using GridSim.Core.Model;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Hosting;

/// <summary>One entry of the bridge ZONE picker: the friendly DNO name shown and the GDA case the stream carries.</summary>
/// <param name="Label">The text the picker shows.</param>
/// <param name="Case">The GDA case name sent in the stream's <c>start</c> action.</param>
public sealed record BridgeZoneChoice(string Label, string Case);

/// <summary>One entry of the bridge FROM picker: the label shown and how far back the stream's historical start is.</summary>
/// <param name="Label">The text the picker shows.</param>
/// <param name="Back">The look-back from now to the stream anchor (also the instant what-if queries are evaluated at).</param>
public sealed record BridgeStartChoice(string Label, TimeSpan Back);

/// <summary>One entry of the bridge SRC picker: the label shown and the reconstruction the stream replays.</summary>
/// <param name="Label">The text the picker shows.</param>
/// <param name="Source">The source sent in the stream's <c>start</c> action (presolved, estimated or live).</param>
public sealed record BridgeSourceChoice(string Label, string Source);

/// <summary>
/// The GDA bridge's fixed choices and constants (UI programme slice 1.8), copied value for value and in the same order
/// from the WPF window (src/GridSim.Wpf/MainWindow.xaml.cs: BridgeZones, BridgeStarts, BridgeSources, BridgeStreamFps,
/// FeasibilityProbeMw, DefaultBridgeBack, the three status-pill brushes and BridgeTripRefusal), so the Avalonia app's
/// bridge pickers, stream and what-if menu offer exactly what the WPF ones offer. The WPF window still holds its own
/// copies (its source is pinned by the WPF source-contract tests); GridSim.Tests BridgeChoicesWpfParityTests reads the
/// WPF source and fails the run if the two ever differ.
/// <para>
/// Desktop only, by registration: these are data, but nothing on a mobile head offers a bridge (UI programme plan,
/// Phase 3: "bridge (GridBridgeClient) is disabled on mobile"). See <see cref="BridgeSession"/>.
/// </para>
/// </summary>
public static class BridgeChoices
{
    /// <summary>
    /// GDA zones the bridge can stream (the server discovers zone-* dirs plus gb-full and gb-spine), with friendly DNO
    /// names for the picker. The first is the default.
    /// </summary>
    public static IReadOnlyList<BridgeZoneChoice> Zones { get; } = new BridgeZoneChoice[]
    {
        new("NGED  -  Midlands/SW/S.Wales", "zone-nged"),
        new("UKPN  -  London/South East/East", "zone-ukpn"),
        new("SSEN  -  N.Scotland/Southern", "zone-ssen"),
        new("SPEN  -  Scotland/Merseyside", "zone-spen"),
        new("NPg  -  North East/Yorkshire", "zone-npg"),
        new("ENWL  -  North West", "zone-enwl"),
        new("GB full", "gb-full"),
        new("GB spine  -  GSP-level", "gb-spine"),
    };

    /// <summary>
    /// Historical start presets for the stream anchor. Every option is comfortably behind the 169 h (7 d + 1 h)
    /// no-forward-inference horizon AND the ~8-day settlement publication lag, so the constrained control can never ask
    /// the server for a start it would reject. The first is the default.
    /// </summary>
    public static IReadOnlyList<BridgeStartChoice> Starts { get; } = new BridgeStartChoice[]
    {
        new("9 days ago", TimeSpan.FromDays(9)),
        new("12 days ago", TimeSpan.FromDays(12)),
        new("2 weeks ago", TimeSpan.FromDays(14)),
        new("3 weeks ago", TimeSpan.FromDays(21)),
        new("30 days ago", TimeSpan.FromDays(30)),
        new("60 days ago", TimeSpan.FromDays(60)),
    };

    /// <summary>
    /// Which reconstruction the stream replays. presolved = demand-scaled re-solve lake; estimated =
    /// measurement-conditioned WLS lake (frames carry a per-tick estimation-quality block, the chi-square pill);
    /// live = static-network solve with only system scalars moving. The first is the default.
    /// </summary>
    public static IReadOnlyList<BridgeSourceChoice> Sources { get; } = new BridgeSourceChoice[]
    {
        new("PRESOLVED", "presolved"),
        new("ESTIMATED", "estimated"),
        new("LIVE", "live"),
    };

    /// <summary>Frame rate (frames per second) the GDA bridge stream is started at (WPF BridgeStreamFps).</summary>
    public const double StreamFps = 2;

    /// <summary>Extra load (MW) the bridge context menu's feasibility query adds at the chosen bus (WPF FeasibilityProbeMw).</summary>
    public const double FeasibilityProbeMw = 500.0;

    /// <summary>
    /// Look-back used when no bridge start preset is selected: the first (shortest) preset, 9 days, which clears both
    /// the 169 h no-forward-inference horizon and the ~8-day settlement lag (WPF DefaultBridgeBack).
    /// </summary>
    public static readonly TimeSpan DefaultBack = TimeSpan.FromDays(9);

    // Bridge status-pill colours (green live / amber transitional / red fault), reusing the map's status ramps, exactly
    // the tokens behind the WPF BridgeOkBrush, BridgeWarnBrush and BridgeBadBrush.

    /// <summary>The pill's colour while live (WPF BridgeOkBrush: Palette.Voltage(1.0)).</summary>
    public static readonly UiColour OkColour = ColourRamps.Voltage(1.0);

    /// <summary>The pill's colour while connecting, restarting, paused, querying or showing a what-if (WPF BridgeWarnBrush: Palette.Loading(0.7)).</summary>
    public static readonly UiColour WarnColour = ColourRamps.Loading(0.7);

    /// <summary>The pill's colour on a fault or a failed connect, restart or query (WPF BridgeBadBrush: Palette.Loading(1.1)).</summary>
    public static readonly UiColour BadColour = ColourRamps.Loading(1.1);

    /// <summary>
    /// Why a bridge scenario trip of the element named <paramref name="name"/> must not be sent, or null when it is
    /// safe (the WPF window's BridgeTripRefusal, word for word). The bridge server resolves the trip selector BY NAME, as
    /// a case-insensitive substring matched against generators, then lines, then transformers (technical chapter 9, the
    /// <c>scenario</c> op). So an element with no name cannot be addressed at all, and a name that is a substring of more
    /// than one element's name in this model could make the server trip a different element (a namesake, or a unit whose
    /// name merely contains it). Both are refused with the reason, never guessed.
    /// </summary>
    public static string? TripRefusal(GridModel m, string? name)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (string.IsNullOrWhiteSpace(name)) return "it has no name, and the bridge selects the element to trip by name";
        int matches = 0;
        foreach (Generator g in m.Generators)
            if (g.Name is { } n && n.Contains(name, StringComparison.OrdinalIgnoreCase)) matches++;
        foreach (Branch b in m.Branches)
            if (b.Name is { } n && n.Contains(name, StringComparison.OrdinalIgnoreCase)) matches++;
        return matches == 1 ? null
            : $"its name matches {matches} elements in this model, and the bridge selects by name, so it could trip a different one";
    }
}
