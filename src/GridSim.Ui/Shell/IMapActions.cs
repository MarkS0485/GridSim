using GridSim.Ui.Schematic;

namespace GridSim.Ui.Shell;

/// <summary>
/// What the map's commands do, as seen from the platform-neutral side (UI programme Phase 1.5, the map). The front end
/// (the Avalonia window's map actions) implements it over its map control and the run pipeline;
/// <see cref="MapCommands.Register"/> wires each command to one member, so the registry itself never touches a
/// control. Every member is one thing the WPF window does from a map key, a toolbar button or a right-click.
/// </summary>
public interface IMapActions
{
    /// <summary>The WPF F key: reset zoom and pan so the whole network fits the map.</summary>
    Task FitToViewAsync();

    /// <summary>The WPF M key: show or hide the corner overview map.</summary>
    Task ToggleMinimapAsync();

    /// <summary>The WPF C key: show or hide the GB coastline behind the network.</summary>
    Task ToggleCoastAsync();

    /// <summary>The WPF B key: show or hide the ETYS boundary cross-sections.</summary>
    Task ToggleBoundariesAsync();

    /// <summary>The WPF G key: tint the substations by GSP supply area.</summary>
    Task ToggleGspAreasAsync();

    /// <summary>The WPF D key and DNO button: the DNO licence-area outlines (only with local geography).</summary>
    Task ToggleDnoAreasAsync();

    /// <summary>The WPF R key and GSP REGIONS button: the GSP-region outlines (only with local geography).</summary>
    Task ToggleGspRegionsAsync();

    /// <summary>The WPF A key and ASSETS button: the geocoded asset markers (only with local geography).</summary>
    Task ToggleAssetsAsync();

    /// <summary>The WPF FLEET button: the generation fleet as fuel-coloured diamonds.</summary>
    Task ToggleFleetAsync();

    /// <summary>The WPF COLOR button's four modes: colour the map by <paramref name="mode"/>.</summary>
    Task ChooseColourModeAsync(MapColourMode mode);

    /// <summary>The WPF Escape key and the fly-out's close button: drop the map selection.</summary>
    Task ClearSelectionAsync();

    /// <summary>The WPF T key: trip the largest online unit in the whole model (the worst-case loss of infeed).</summary>
    Task TripLargestInfeedAsync();

    /// <summary>
    /// The WPF right-click on a bus: trip the largest unit generating at the bus the context menu was opened on
    /// (<see cref="ContextBusId"/>).
    /// </summary>
    Task TripAtContextBusAsync();

    /// <summary>The bus the open context menu was built for, or null when none is open on a bus.</summary>
    int? ContextBusId { get; }

    // ---- UI programme integration (2026-10-20): what each View toggle shows now, so its menu item carries a tick ----

    /// <summary>True while the corner overview map is shown (View / Minimap).</summary>
    bool MinimapShown { get; }

    /// <summary>True while the GB coastline is drawn (View / Coast).</summary>
    bool CoastShown { get; }

    /// <summary>True while the ETYS boundary cross-sections are drawn (View / Boundaries).</summary>
    bool BoundariesShown { get; }

    /// <summary>True while the substations are tinted by GSP supply area (View / GSP areas).</summary>
    bool GspAreasShown { get; }

    /// <summary>True while the DNO licence-area outlines are drawn (View / DNO areas).</summary>
    bool DnoAreasShown { get; }

    /// <summary>True while the GSP-region outlines are drawn (View / GSP regions).</summary>
    bool GspRegionsShown { get; }

    /// <summary>True while the geocoded asset markers are drawn (View / Assets).</summary>
    bool AssetsShown { get; }

    /// <summary>True while the generation fleet is drawn (View / Fleet).</summary>
    bool FleetShown { get; }

    /// <summary>The colour mode the map is drawn in now (View / Colour by: the one ticked item).</summary>
    MapColourMode ColourMode { get; }

    /// <summary>
    /// True when local DNO/GSP geography was found. Without it WPF disables the DNO, GSP REGIONS and ASSETS buttons
    /// and its D, R and A keys do nothing, so those commands are disabled here too.
    /// </summary>
    bool HasGeography { get; }
}
