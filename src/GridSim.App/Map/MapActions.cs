using GridSim.App.Run;
using GridSim.Ui.Schematic;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;

namespace GridSim.App.Map;

/// <summary>
/// The map commands' actions (<see cref="IMapActions"/>, UI programme Phase 1.5) over the window's map control, its map
/// view model and the run pipeline. Each is what the WPF window does for the same key, button or right-click:
/// a toggle flips the control's layer and records the choice in the settings (WPF saves the same flags on exit);
/// a trip goes through the run view model's port of the WPF trip; the line WPF writes to its event log goes to
/// <c>log</c> (the window's status bar, until the event log is ported).
/// </summary>
public sealed class MapActions : IMapActions, ILineRouteMapActions
{
    private readonly SchematicControl _map;
    private readonly MapViewModel _mapVm;
    private readonly RunViewModel _run;
    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Action<string> _log;

    /// <summary>
    /// Actions on <paramref name="map"/>, <paramref name="mapVm"/> and <paramref name="run"/>. A changed layer or colour
    /// mode is written to <paramref name="settings"/> and reported through <paramref name="settingsChanged"/>; each
    /// WPF event-log line goes to <paramref name="log"/>.
    /// </summary>
    public MapActions(SchematicControl map, MapViewModel mapVm, RunViewModel run, AppSettings settings,
        Action settingsChanged, Action<string> log)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _mapVm = mapVm ?? throw new ArgumentNullException(nameof(mapVm));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsChanged = settingsChanged ?? throw new ArgumentNullException(nameof(settingsChanged));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public int? ContextBusId { get; set; }

    /// <summary>
    /// UI programme ASSETS A3: the branch (its index in the model's branch list) the map's context menu was opened on,
    /// or null (a menu opened on a bus, or on nothing). The ASSETS tab's "Open in Assets" reads it.
    /// </summary>
    public int? ContextBranchIndex { get; set; }

    /// <inheritdoc />
    public bool HasGeography => _map.HasGeography;

    // UI programme integration (2026-10-20): each View toggle's tick reads the map control's own flag, the one the
    // toggle flips, so the tick can never disagree with what is drawn.

    /// <inheritdoc />
    public bool MinimapShown => _map.ShowMinimap;

    /// <inheritdoc />
    public bool CoastShown => _map.ShowCoast;

    /// <inheritdoc />
    public bool BoundariesShown => _map.ShowBoundaries;

    /// <inheritdoc />
    public bool GspAreasShown => _map.ShowGspAreas;

    /// <inheritdoc />
    public bool DnoAreasShown => _map.ShowDnoAreas;

    /// <inheritdoc />
    public bool GspRegionsShown => _map.ShowGspRegions;

    /// <inheritdoc />
    public bool AssetsShown => _map.ShowAssets;

    /// <inheritdoc />
    public bool FleetShown => _map.ShowFleet;

    /// <inheritdoc />
    public MapColourMode ColourMode => _map.ColorMode;

    /// <inheritdoc />
    public bool LineRoutesShown => _map.ShowLineRoutes;

    /// <summary>
    /// Put the saved layer flags and colour mode on the map (the WPF window's <c>ApplySettingsToRenderer</c>): the
    /// colour mode is Voltage when the setting says so, else Loading, as WPF stores only that one flag.
    /// </summary>
    public void ApplySettings()
    {
        _map.ColorMode = _settings.ColorVoltage ? MapColourMode.Voltage : MapColourMode.Loading;
        _map.ShowFleet = _settings.Fleet;
        _map.ShowMinimap = _settings.Minimap;
        _map.ShowCoast = _settings.Coast;
        _map.ShowBoundaries = _settings.Boundaries;
        _map.ShowGspAreas = _settings.GspAreas;
        _map.ShowDnoAreas = _settings.DnoAreas;
        _map.ShowGspRegions = _settings.GspRegions;
        _map.ShowAssets = _settings.Assets;
        _map.ShowLineRoutes = _settings.LineRoutes;   // Phase 5d (off unless the user turned it on)
        _mapVm.SetColourLegend(_map.ColorMode);
    }

    /// <inheritdoc />
    public Task FitToViewAsync()
    {
        _map.FitToView();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ToggleMinimapAsync() => Toggle(() => _settings.Minimap = _map.ShowMinimap = !_map.ShowMinimap);

    /// <inheritdoc />
    public Task ToggleCoastAsync() => Toggle(() => _settings.Coast = _map.ShowCoast = !_map.ShowCoast);

    /// <inheritdoc />
    public Task ToggleBoundariesAsync() => Toggle(() => _settings.Boundaries = _map.ShowBoundaries = !_map.ShowBoundaries);

    /// <inheritdoc />
    public Task ToggleGspAreasAsync() => Toggle(() => _settings.GspAreas = _map.ShowGspAreas = !_map.ShowGspAreas);

    /// <inheritdoc />
    public Task ToggleDnoAreasAsync() => Toggle(() => _settings.DnoAreas = _map.ShowDnoAreas = !_map.ShowDnoAreas);

    /// <inheritdoc />
    public Task ToggleGspRegionsAsync() => Toggle(() => _settings.GspRegions = _map.ShowGspRegions = !_map.ShowGspRegions);

    /// <inheritdoc />
    public Task ToggleAssetsAsync() => Toggle(() => _settings.Assets = _map.ShowAssets = !_map.ShowAssets);

    /// <inheritdoc />
    public Task ToggleFleetAsync() => Toggle(() => _settings.Fleet = _map.ShowFleet = !_map.ShowFleet);

    /// <inheritdoc />
    public Task ToggleLineRoutesAsync() => Toggle(() => _settings.LineRoutes = _map.ShowLineRoutes = !_map.ShowLineRoutes);

    /// <summary>
    /// Phase 5d: turn the line route layer on (Show on map from a route page frames the route, so the route must be
    /// drawn), recording it in the settings as the toggle does. False when it was on already (nothing changed).
    /// </summary>
    public bool ShowLineRoutesLayer()
    {
        if (_map.ShowLineRoutes) return false;
        _settings.LineRoutes = _map.ShowLineRoutes = true;
        _settingsChanged();
        return true;
    }

    /// <inheritdoc />
    public Task ChooseColourModeAsync(MapColourMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a map colour mode.");
        _map.ColorMode = mode;
        _mapVm.SetColourLegend(mode);   // keep the legend swatch and labels in step with the tint (WPF)
        _settings.ColorVoltage = mode == MapColourMode.Voltage;   // WPF stores only "voltage or not"
        _settingsChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearSelectionAsync()
    {
        _mapVm.ClearSelection();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TripLargestInfeedAsync()
    {
        if (_run.TripLargestInfeed() is string line) _log(line);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TripAtContextBusAsync()
    {
        int busId = ContextBusId ?? throw new InvalidOperationException("The bus context menu was not opened on a bus.");
        if (_run.TripAtBus(busId) is string line) _log(line);
        return Task.CompletedTask;
    }

    private Task Toggle(Action flip)
    {
        flip();
        _settingsChanged();
        return Task.CompletedTask;
    }
}
