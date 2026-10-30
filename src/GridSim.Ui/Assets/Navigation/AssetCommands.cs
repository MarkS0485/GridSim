using GridSim.Ui.Commands;

namespace GridSim.Ui.Assets.Navigation;

/// <summary>
/// What the ASSETS tab's registry commands act on (UI programme ASSETS A3): the bus or branch a map context menu was
/// opened on, and the move into the ASSETS tab. The front end implements it over its map and its navigator.
/// </summary>
public interface IAssetCommandActions
{
    /// <summary>The bus the map's context menu was opened on, or null.</summary>
    int? ContextBusId { get; }

    /// <summary>The branch (its index in the model's branch list) the map's context menu was opened on, or null.</summary>
    int? ContextBranchIndex { get; }

    /// <summary>Switch to the ASSETS tab and select the context bus (its substation) or branch (its circuit or
    /// transformer), as the map fly-out's OPEN IN EXPLORER does.</summary>
    Task OpenInAssetsAsync();
}

/// <summary>
/// The ASSETS tab's registry commands (UI programme ASSETS A3; plan section 3.5): "Open in Assets" on the map's context
/// menu for a bus or a branch, Run mode, desktop. It has no menu path and no key: it acts on what was right-clicked.
/// </summary>
public static class AssetCommands
{
    /// <summary>The map context menu's "Open in Assets" (a bus or a branch).</summary>
    public const string OpenInAssets = "assets.openInAssets";

    /// <summary>Its menu text.</summary>
    public const string OpenInAssetsTitle = "Open in Assets";

    /// <summary>Register the commands into <paramref name="registry"/>, each running a member of <paramref name="actions"/>,
    /// and return them in registration order. Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IAssetCommandActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        return new[]
        {
            registry.Register(new AppCommand
            {
                Id = OpenInAssets, Title = OpenInAssetsTitle,
                Modes = CommandModes.Run, Targets = CommandTargets.Bus | CommandTargets.Branch,
                CanExecute = () => actions.ContextBusId is not null || actions.ContextBranchIndex is not null,
                Execute = actions.OpenInAssetsAsync,
            }),
        };
    }
}
