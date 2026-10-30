using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;

namespace GridSim.App.Assets;

// UI programme ASSETS A3: the page types the Avalonia ASSETS tab shows. Each is the A2 neutral page itself, closed over
// the neutral row types and given a plain (non-generic) name, so an AXAML DataTemplate can name it in DataType (the
// compiled bindings need a closed, named type). Nothing is added: no property, no brush, no command. The WPF front end
// needs adapters for its brushes and commands; this one binds the neutral colour tokens through TokenBrush and handles
// each action as a click, so every value on screen is the A2 page's own property. The two unit pages that are not
// generic (GeneratorDetailModel, InterconnectorDetailModel) are shown as they are.

/// <summary>The landing page (ASSETS): the A2 home with the neutral class cards.</summary>
public sealed class AssetHomePage(AssetExplorerModel explorer) : AssetHomeModel<AssetClassCardModel>(explorer);

/// <summary>The INTERCONNECTORS fleet page.</summary>
public sealed class InterconnectorFleetPage(AssetExplorerModel explorer) : InterconnectorFleetModel<LinkFleetRowModel>(explorer);

/// <summary>The GENERATORS fleet page.</summary>
public sealed class GeneratorFleetPage(AssetExplorerModel explorer) : GeneratorFleetModel<GenFleetRowModel>(explorer);

/// <summary>The GENERATOR PHASORS page.</summary>
public sealed class PhasorFleetPage(AssetExplorerModel explorer) : GeneratorPhasorFleetModel<PhasorTileModel>(explorer);

/// <summary>The TRANSFORMERS fleet page.</summary>
public sealed class TransformerFleetPage(AssetExplorerModel explorer) : TransformerFleetModel<TransformerFleetRowModel>(explorer);

/// <summary>A transformer's unit page.</summary>
public sealed class TransformerDetailPage(int index, int fromBus, int toBus, string? name, AssetExplorerModel explorer)
    : TransformerDetailModel<TransformerReadingModel, TransformerEventModel>(index, fromBus, toBus, name, explorer);

/// <summary>The TAP CHANGERS fleet page.</summary>
public sealed class TapChangerFleetPage(AssetExplorerModel explorer) : TapChangerFleetModel<TapChangerRowModel>(explorer);

/// <summary>The CABLES &amp; LINES fleet page.</summary>
public sealed class CircuitFleetPage(AssetExplorerModel explorer) : CircuitFleetModel<BandChipModel, CircuitFleetRowModel>(explorer);

/// <summary>A circuit's unit page.</summary>
public sealed class CircuitDetailPage(int index, int fromBus, int toBus, string? name, AssetExplorerModel explorer)
    : CircuitDetailModel<CircuitStatusRowModel>(index, fromBus, toBus, name, explorer);

/// <summary>The SUBSTATIONS fleet page.</summary>
public sealed class SubstationFleetPage(AssetExplorerModel explorer) : SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel>(explorer);

/// <summary>A substation's unit page.</summary>
public sealed class SubstationDetailPage(int busId, AssetExplorerModel explorer) : SubstationDetailModel<PlantRowModel, ConnectionRowModel>(busId, explorer);

/// <summary>
/// Makes the Avalonia ASSETS pages (the types above), each with the same arguments the A2 explorer's default would use,
/// so the explorer shows the A2 page under its Avalonia name.
/// </summary>
public sealed class AppAssetPageFactory : IAssetPageFactory
{
    /// <summary>The one instance (the factory holds no state).</summary>
    public static AppAssetPageFactory Instance { get; } = new();

    private AppAssetPageFactory() { }

    /// <inheritdoc />
    public AssetPageModel Home(AssetExplorerModel explorer) => new AssetHomePage(explorer);

    /// <inheritdoc />
    public AssetPageModel GeneratorFleet(AssetExplorerModel explorer) => new GeneratorFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel PhasorFleet(AssetExplorerModel explorer) => new PhasorFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel InterconnectorFleet(AssetExplorerModel explorer) => new InterconnectorFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel TransformerFleet(AssetExplorerModel explorer) => new TransformerFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel CircuitFleet(AssetExplorerModel explorer) => new CircuitFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel SubstationFleet(AssetExplorerModel explorer) => new SubstationFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel TapChangerFleet(AssetExplorerModel explorer) => new TapChangerFleetPage(explorer);

    /// <inheritdoc />
    public AssetPageModel SubstationDetail(AssetExplorerModel explorer, int busId) => new SubstationDetailPage(busId, explorer);

    /// <inheritdoc />
    public AssetPageModel GeneratorDetail(AssetExplorerModel explorer, int index, int bus, string? name) =>
        new GeneratorDetailModel(index, bus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel InterconnectorDetail(AssetExplorerModel explorer, int index, int bus, string? name) =>
        new InterconnectorDetailModel(index, bus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel CircuitDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name) =>
        new CircuitDetailPage(index, fromBus, toBus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel TransformerDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name) =>
        new TransformerDetailPage(index, fromBus, toBus, name, explorer);
}
