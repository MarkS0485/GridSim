using GridSim.Ui.Commands;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.2 (ASSETS plan section 3.5, modes and the command registry): the map-editing commands of AUTHOR
// mode, each declared once. Author mode only and desktop only, like every Author command (the registry refuses an Author
// command offered on mobile). They make a "Map" menu that appears in Author mode only, between Case and Help, so the
// Run-mode menu bar and its shortcuts are exactly as before:
//   Map > Select tool  (Escape)   back to selecting and dragging; also cancels a Connect half-way;
//   Map > Place bus    (P)        the next click on the map adds a bus there;
//   Map > Connect      (L)        click bus A, then bus B, to add a line or a transformer between them;
//   Map > Delete       (Delete)   delete the selected bus, line or transformer (refused, listing the referrers, while
//                                 anything refers to it);
// and the context menu of a bus or a branch right-clicked on the map in Author mode (CommandTargets.MapElement): Delete
// and Show in Assets. No gesture clashes: the map's Run-mode keys (F, M, C, B, G, D, R, A, T, Escape) are Run mode only,
// and P, L and Delete are taken by nothing else in either mode; the registry refuses any clash per mode, so a later
// command that took one would fail loudly at start-up. P, L, Escape and Delete are single keys, so the window's typing
// guard keeps them from firing while the focus is in a text box (a field grid value, the search box).

/// <summary>What the map-editing commands act on: the window implements it over its Author map controller.</summary>
public interface IMapAuthorActions
{
    /// <summary>The Author mode's model while the window is in AUTHOR, else null.</summary>
    AssetAuthorModel? Author { get; }

    /// <summary>The map's current tool.</summary>
    MapEditTool Tool { get; }

    /// <summary>True when the selected record is a bus, a line or a transformer drawn on the map (Delete acts on it).</summary>
    bool HasMapSelection { get; }

    /// <summary>Choose <paramref name="tool"/> (Select also cancels a Connect half-way).</summary>
    Task ChooseToolAsync(MapEditTool tool);

    /// <summary>Delete the selected bus, line or transformer (the engine refuses, listing the referrers, while anything
    /// refers to it; nothing changes then).</summary>
    Task DeleteSelectedAsync();

    /// <summary>Show the ASSETS tab with the selected record (it is already selected there: the selection is shared).</summary>
    Task ShowInAssetsAsync();
}

/// <summary>The map-editing registry commands of AUTHOR mode (UI programme Phase 4.2).</summary>
public static class MapAuthorCommands
{
    /// <summary>Map > Select tool (Escape).</summary>
    public const string SelectTool = "map.selectTool";

    /// <summary>Map > Place bus (P).</summary>
    public const string PlaceBus = "map.placeBus";

    /// <summary>Map > Connect (L).</summary>
    public const string Connect = "map.connect";

    /// <summary>Map > Delete (the Delete key), also on the map element's context menu.</summary>
    public const string Delete = "map.delete";

    /// <summary>The map element's context menu: Show in Assets.</summary>
    public const string ShowInAssets = "map.showInAssets";

    /// <summary>Register the map-editing commands into <paramref name="registry"/>, each running a member of
    /// <paramref name="actions"/>, and return them in registration order. Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IMapAuthorActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        bool Authoring() => actions.Author is not null;
        AppCommand Add(AppCommand c) => registry.Register(c);
        return new[]
        {
            Add(new AppCommand
            {
                Id = SelectTool, Title = "Select tool", MenuPath = "Map/Select tool", Gesture = "Escape", Modes = CommandModes.Author,
                CanExecute = Authoring, IsChecked = () => actions.Tool == MapEditTool.Select,
                Execute = () => actions.ChooseToolAsync(MapEditTool.Select),
            }),
            Add(new AppCommand
            {
                Id = PlaceBus, Title = "Place bus", MenuPath = "Map/Place bus", Gesture = "P", Modes = CommandModes.Author,
                CanExecute = Authoring, IsChecked = () => actions.Tool == MapEditTool.PlaceBus,
                Execute = () => actions.ChooseToolAsync(MapEditTool.PlaceBus),
            }),
            Add(new AppCommand
            {
                Id = Connect, Title = "Connect", MenuPath = "Map/Connect", Gesture = "L", Modes = CommandModes.Author,
                CanExecute = Authoring, IsChecked = () => actions.Tool == MapEditTool.Connect,
                Execute = () => actions.ChooseToolAsync(MapEditTool.Connect),
            }),
            Add(new AppCommand
            {
                Id = Delete, Title = "Delete", MenuPath = "Map/Delete", Gesture = "Delete", Modes = CommandModes.Author,
                Targets = CommandTargets.MapElement,
                CanExecute = () => actions.HasMapSelection, Execute = actions.DeleteSelectedAsync,
            }),
            Add(new AppCommand
            {
                Id = ShowInAssets, Title = "Show in Assets", Modes = CommandModes.Author, Targets = CommandTargets.MapElement,
                CanExecute = () => actions.HasMapSelection, Execute = actions.ShowInAssetsAsync,
            }),
        };
    }
}
