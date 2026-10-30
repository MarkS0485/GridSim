using GridSim.Ui.Commands;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Shell;

/// <summary>
/// The map's commands (UI programme Phase 1.5): the WPF window's map keys, its map toolbar buttons and its right-click
/// on a bus, as registry commands. Every map key is on the View menu (with the T key's trip, since it is one of the
/// WPF map keys); the right-click trip is a context-menu command for a bus.
/// <para>
/// Keys. The same keys with the same meanings as the WPF window's <c>OnKeyDown</c>: F fit, M minimap, C coast,
/// B boundaries, G GSP areas, D DNO areas, R GSP regions, A assets, T trip the largest infeed, Escape clear the
/// selection. None clashes with the shell's Ctrl chords or the Run menu's F5, F6, '.' and ','; the registry refuses any
/// clash when these are registered. WPF ignores these keys while the keyboard focus is in a text box, a combo box or
/// its drop-down; the front end applies the same typing guard to every single-key shortcut.
/// </para>
/// <para>
/// The front end registers these with the Run commands, once the first case has loaded (before that the map has
/// nothing on it, and the registry shows no command without its page). They are Run mode, desktop commands.
/// </para>
/// </summary>
public static class MapCommands
{
    /// <summary>View / Fit to view (F).</summary>
    public const string FitToView = "view.fitToView";

    /// <summary>View / Minimap (M).</summary>
    public const string Minimap = "view.minimap";

    /// <summary>View / Coast (C).</summary>
    public const string Coast = "view.coast";

    /// <summary>View / Boundaries (B).</summary>
    public const string Boundaries = "view.boundaries";

    /// <summary>View / GSP areas (G).</summary>
    public const string GspAreas = "view.gspAreas";

    /// <summary>View / DNO areas (D, only with local geography).</summary>
    public const string DnoAreas = "view.dnoAreas";

    /// <summary>View / GSP regions (R, only with local geography).</summary>
    public const string GspRegions = "view.gspRegions";

    /// <summary>View / Assets (A, only with local geography).</summary>
    public const string Assets = "view.assets";

    /// <summary>View / Line routes (no key; UI programme Phase 5d: the asset overlay's line routes on the map).</summary>
    public const string LineRoutes = "view.lineRoutes";

    /// <summary>View / Fleet (no key: the WPF FLEET button).</summary>
    public const string Fleet = "view.fleet";

    /// <summary>View / Clear selection (Escape).</summary>
    public const string ClearSelection = "view.clearSelection";

    /// <summary>View / Trip largest infeed (T).</summary>
    public const string TripLargestInfeed = "map.tripLargestInfeed";

    /// <summary>The bus context menu's trip (the WPF right-click on a bus).</summary>
    public const string TripAtBus = "map.tripAtBus";

    /// <summary>The id of the View / Colour by item for <paramref name="mode"/>, for example <c>view.colourBy.voltage</c>.</summary>
    public static string ColourById(MapColourMode mode) => "view.colourBy." + mode.ToString().ToLowerInvariant();

    /// <summary>
    /// The text of a View / Colour by item: the WPF COLOR button's label for that mode, without its leading space
    /// (" LOADING", " VOLTAGE", " CONDITION", " STRESS").
    /// </summary>
    public static string ColourByTitle(MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => "VOLTAGE",
        MapColourMode.Condition => "CONDITION",
        MapColourMode.Stress => "STRESS",
        MapColourMode.Loading => "LOADING",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a map colour mode."),
    };

    // ---- UI programme slice 1.7: the WPF transport bar's map buttons (COLOR, FLEET, DNO, GSP), as display rules ----

    /// <summary>
    /// The WPF COLOR button's text for <paramref name="mode"/> (MainWindow.ColorLabel): the mode's name with a leading
    /// space, " LOADING", " VOLTAGE", " CONDITION" or " STRESS".
    /// </summary>
    public static string ColourButtonLabel(MapColourMode mode) => " " + ColourByTitle(mode);

    /// <summary>
    /// The mode one press of the WPF COLOR button moves to from <paramref name="mode"/> (MainWindow.Color_Click):
    /// LOADING, then VOLTAGE, then CONDITION, then STRESS, then back to LOADING. The last two are the GDA-bridge
    /// overlays (fragility by bus, stress by branch); without a bridge they colour from whatever overlay data there is,
    /// as in WPF.
    /// </summary>
    public static MapColourMode NextColourMode(MapColourMode mode) => mode switch
    {
        MapColourMode.Loading => MapColourMode.Voltage,
        MapColourMode.Voltage => MapColourMode.Condition,
        MapColourMode.Condition => MapColourMode.Stress,
        MapColourMode.Stress => MapColourMode.Loading,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a map colour mode."),
    };

    /// <summary>The WPF opacity of a map toggle button whose layer is shown.</summary>
    public const double ButtonOnOpacity = 1.0;

    /// <summary>The WPF opacity of a map toggle button whose layer is hidden, or that is disabled (no geography).</summary>
    public const double ButtonOffOpacity = 0.55;

    /// <summary>
    /// The WPF opacity of a map toggle button (FleetBtn, DnoBtn, GspRegionBtn): <see cref="ButtonOnOpacity"/> while the
    /// layer is <paramref name="shown"/> and the button is <paramref name="available"/> (the DNO and GSP buttons need
    /// local geography; FLEET always is), else <see cref="ButtonOffOpacity"/>.
    /// </summary>
    public static double ToggleButtonOpacity(bool shown, bool available = true) =>
        available && shown ? ButtonOnOpacity : ButtonOffOpacity;

    /// <summary>
    /// Register the map commands into <paramref name="registry"/>, each running one member of
    /// <paramref name="actions"/>, and return them in registration order (the front end asks them to refresh their
    /// enabled state when geography appears). Throws (from <see cref="CommandRegistry.Register"/>) if any clashes with
    /// a command already there.
    /// <para>UI programme Phase 5d: with <paramref name="lineRoutes"/> the View menu also has "Line routes" (a toggle with a
    /// tick, after Fleet), the asset overlay's line route layer; without it (null) the commands are exactly as before.</para>
    /// </summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IMapActions actions, ILineRouteMapActions? lineRoutes = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        var added = new List<AppCommand>();
        void Add(AppCommand c) => added.Add(registry.Register(c));

        // UI programme integration (2026-10-20): the toggles (the eight layers and the fleet) and the four colour modes
        // carry a tick (AppCommand.IsChecked) read from the map's own state. Each runs its action and then asks every
        // toggle's menu item to read its tick again: choosing a colour mode unticks the other three, and a key press
        // flips a layer without the menu being open, so all of them are refreshed (there are only thirteen).
        var toggles = new List<AppCommand>();
        void AddToggle(AppCommand c)
        {
            Add(c);
            toggles.Add(c);
        }
        Func<Task> ThenRefreshTicks(Func<Task> action) => async () =>
        {
            await action().ConfigureAwait(true);   // stay on the calling (UI) thread: the menu items are refreshed next
            foreach (AppCommand t in toggles) t.RaiseCheckedChanged();
        };

        // The WPF map keys, in the order its OnKeyDown tests them.
        Add(new AppCommand
        {
            Id = FitToView, Title = "Fit to view", MenuPath = "View/Fit to view", Gesture = "F",
            Modes = CommandModes.Run, Execute = actions.FitToViewAsync,
        });
        AddToggle(new AppCommand
        {
            Id = Minimap, Title = "Minimap", MenuPath = "View/Minimap", Gesture = "M",
            Modes = CommandModes.Run, IsChecked = () => actions.MinimapShown, Execute = ThenRefreshTicks(actions.ToggleMinimapAsync),
        });
        AddToggle(new AppCommand
        {
            Id = Coast, Title = "Coast", MenuPath = "View/Coast", Gesture = "C",
            Modes = CommandModes.Run, IsChecked = () => actions.CoastShown, Execute = ThenRefreshTicks(actions.ToggleCoastAsync),
        });
        AddToggle(new AppCommand
        {
            Id = Boundaries, Title = "Boundaries", MenuPath = "View/Boundaries", Gesture = "B",
            Modes = CommandModes.Run, IsChecked = () => actions.BoundariesShown, Execute = ThenRefreshTicks(actions.ToggleBoundariesAsync),
        });
        AddToggle(new AppCommand
        {
            Id = GspAreas, Title = "GSP areas", MenuPath = "View/GSP areas", Gesture = "G",
            Modes = CommandModes.Run, IsChecked = () => actions.GspAreasShown, Execute = ThenRefreshTicks(actions.ToggleGspAreasAsync),
        });
        // WPF: "else if (e.Key == Key.D && _renderer.HasGeography)" and the same for R and A, so without local
        // geography the key does nothing and the button is disabled.
        AddToggle(new AppCommand
        {
            Id = DnoAreas, Title = "DNO areas", MenuPath = "View/DNO areas", Gesture = "D",
            Modes = CommandModes.Run, CanExecute = () => actions.HasGeography,
            IsChecked = () => actions.DnoAreasShown, Execute = ThenRefreshTicks(actions.ToggleDnoAreasAsync),
        });
        AddToggle(new AppCommand
        {
            Id = GspRegions, Title = "GSP regions", MenuPath = "View/GSP regions", Gesture = "R",
            Modes = CommandModes.Run, CanExecute = () => actions.HasGeography,
            IsChecked = () => actions.GspRegionsShown, Execute = ThenRefreshTicks(actions.ToggleGspRegionsAsync),
        });
        AddToggle(new AppCommand
        {
            Id = Assets, Title = "Assets", MenuPath = "View/Assets", Gesture = "A",
            Modes = CommandModes.Run, CanExecute = () => actions.HasGeography,
            IsChecked = () => actions.AssetsShown, Execute = ThenRefreshTicks(actions.ToggleAssetsAsync),
        });

        // The WPF toolbar's FLEET button (it has no key) and its COLOR button's four modes, one item each.
        AddToggle(new AppCommand
        {
            Id = Fleet, Title = "Fleet", MenuPath = "View/Fleet",
            Modes = CommandModes.Run, IsChecked = () => actions.FleetShown, Execute = ThenRefreshTicks(actions.ToggleFleetAsync),
        });
        // Phase 5d: the line route layer, with the other layer toggles (no key: none is free that WPF did not use).
        if (lineRoutes is not null)
            AddToggle(new AppCommand
            {
                Id = LineRoutes, Title = "Line routes", MenuPath = "View/Line routes",
                Modes = CommandModes.Run, IsChecked = () => lineRoutes.LineRoutesShown, Execute = ThenRefreshTicks(lineRoutes.ToggleLineRoutesAsync),
            });
        foreach (MapColourMode mode in new[] { MapColourMode.Loading, MapColourMode.Voltage, MapColourMode.Condition, MapColourMode.Stress })
        {
            MapColourMode m = mode;   // each item chooses its own mode
            AddToggle(new AppCommand
            {
                Id = ColourById(m), Title = ColourByTitle(m), MenuPath = "View/Colour by/" + ColourByTitle(m),
                Modes = CommandModes.Run, IsChecked = () => actions.ColourMode == m, Execute = ThenRefreshTicks(() => actions.ChooseColourModeAsync(m)),
            });
        }

        // The WPF T key and Escape key, the last two map keys.
        Add(new AppCommand
        {
            Id = TripLargestInfeed, Title = "Trip largest infeed", MenuPath = "View/Trip largest infeed", Gesture = "T",
            Modes = CommandModes.Run, Execute = actions.TripLargestInfeedAsync,
        });
        Add(new AppCommand
        {
            Id = ClearSelection, Title = "Clear selection", MenuPath = "View/Clear selection", Gesture = "Escape",
            Modes = CommandModes.Run, Execute = actions.ClearSelectionAsync,
        });

        // The WPF right-click on a bus trips its largest infeed straight away; here it is the one item of the bus
        // context menu, so the action is named before it is taken. Only offered when the menu was opened on a bus.
        Add(new AppCommand
        {
            Id = TripAtBus, Title = "Trip the largest infeed at this bus",
            Modes = CommandModes.Run, Targets = CommandTargets.Bus,
            CanExecute = () => actions.ContextBusId is not null, Execute = actions.TripAtContextBusAsync,
        });
        return added;
    }
}
