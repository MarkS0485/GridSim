using GridSim.Ui.Presentation;
using GridSim.Ui.Visuals;

namespace GridSim.Ui.Diagrams;

/// <summary>
/// The live-state colours and strokes of the diagram toolkit, as colour tokens (UI programme ASSETS A1b).
///
/// <list type="bullet">
/// <item>A conductor (a lead, a connection, a busbar, a transformer's windings) is cyan when energised, slate when
/// de-energised; with a loading reading it takes the shared loading ramp (<see cref="ColourRamps.Loading"/>: green to
/// amber at 60 percent to red at 100 percent and above), and a non-finite reading shows as amber "unknown" rather than
/// a made-up band. A de-energised item is slate whatever its loading reading (nothing flows).</item>
/// <item>A switching device's blade and contact marks show its position whatever the energisation, in the GB
/// control-room convention: red closed, green open, amber unknown.</item>
/// </list>
/// The hex values are the palette the asset visuals already use (the substation map's cyan bus bar and slate captions).
/// </summary>
public static class DiagramStyle
{
    /// <summary>The stroke width of leads, connections and symbol outlines.</summary>
    public const double ConductorThickness = 2;

    /// <summary>The stroke width of a busbar.</summary>
    public const double BusbarThickness = 5;

    /// <summary>The font size of a symbol's label.</summary>
    public const double LabelSize = 10;

    /// <summary>An energised conductor with no loading reading.</summary>
    public static readonly UiColour Energised = ColourRamps.FromHex("#22D3EE");

    /// <summary>A de-energised conductor.</summary>
    public static readonly UiColour DeEnergised = ColourRamps.FromHex("#64748B");

    /// <summary>A closed switch (GB control-room convention: red).</summary>
    public static readonly UiColour Closed = ColourRamps.FromHex("#EF4444");

    /// <summary>An open switch (GB control-room convention: green).</summary>
    public static readonly UiColour Open = ColourRamps.FromHex("#22C55E");

    /// <summary>An unknown switch position, or a non-finite loading reading.</summary>
    public static readonly UiColour Unknown = ColourRamps.FromHex("#F59E0B");

    /// <summary>Labels.</summary>
    public static readonly VisualFill LabelFill = new(ColourRamps.FromHex("#E6EDF3"));

    /// <summary>Muted captions.</summary>
    public static readonly VisualFill MutedFill = new(ColourRamps.FromHex("#94A3B8"));

    /// <summary>The dark fill inside a relay box and behind a generator's letter.</summary>
    public static readonly VisualFill BoxFill = new(ColourRamps.FromHex("#0F172A"));

    /// <summary>The colour of a conductor in the given state (see the class summary).</summary>
    public static UiColour ConductorColour(DiagramState s)
    {
        if (!s.Energised) return DeEnergised;
        if (s.Loading is not double frac) return Energised;
        return double.IsFinite(frac) ? ColourRamps.Loading(frac) : Unknown;
    }

    /// <summary>The colour of a switch blade in the given position.</summary>
    public static UiColour SwitchColour(DiagramSwitchState sw) => sw switch
    {
        DiagramSwitchState.Closed => Closed,
        DiagramSwitchState.Open => Open,
        DiagramSwitchState.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(sw), sw, "not a switch state"),
    };

    /// <summary>The pen of a conductor (leads, connections, outlines) in the given state.</summary>
    public static VisualPen ConductorPen(DiagramState s) => new(ConductorColour(s), ConductorThickness);

    /// <summary>The pen of a busbar in the given state.</summary>
    public static VisualPen BusbarPen(DiagramState s) => new(ConductorColour(s), BusbarThickness);

    /// <summary>The pen of a switch blade and its contact mark in the given state.</summary>
    public static VisualPen SwitchPen(DiagramState s) => new(SwitchColour(s.Switch), ConductorThickness);
}
