using GridSim.Ui.Assets.Navigation;

namespace GridSim.App.Assets;

/// <summary>
/// The ASSETS registry commands' actions over the window (UI programme ASSETS A3): the bus and branch the map's context
/// menu was opened on (read from the map actions when asked, so a later right-click is seen) and the move into the
/// ASSETS tab. Each part is handed in by the window, which owns the map and the tab strip.
/// </summary>
public sealed class AssetCommandActions : IAssetCommandActions
{
    private readonly Func<int?> _bus;
    private readonly Func<int?> _branch;
    private readonly Func<Task> _open;

    /// <summary>Actions that read the context bus with <paramref name="bus"/>, the context branch with
    /// <paramref name="branch"/>, and open the ASSETS tab on them with <paramref name="open"/>.</summary>
    public AssetCommandActions(Func<int?> bus, Func<int?> branch, Func<Task> open)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _branch = branch ?? throw new ArgumentNullException(nameof(branch));
        _open = open ?? throw new ArgumentNullException(nameof(open));
    }

    /// <inheritdoc />
    public int? ContextBusId => _bus();

    /// <inheritdoc />
    public int? ContextBranchIndex => _branch();

    /// <inheritdoc />
    public Task OpenInAssetsAsync() => _open();
}
