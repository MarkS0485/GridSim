namespace GridSim.Ui.Shell;

/// <summary>
/// UI programme Phase 5d (2026-10-21): what the NETWORK map's "Line routes" layer command does, from the platform-neutral
/// side. Kept apart from <see cref="IMapActions"/> (the WPF map keys and buttons, a fixed set) so every implementation of
/// that interface stays as it was; the front end implements both over its map control.
/// </summary>
public interface ILineRouteMapActions
{
    /// <summary>True while the asset overlay's line routes are drawn on the map (View / Line routes carries a tick).</summary>
    bool LineRoutesShown { get; }

    /// <summary>Show or hide the line route layer, and record the choice in the settings.</summary>
    Task ToggleLineRoutesAsync();
}
