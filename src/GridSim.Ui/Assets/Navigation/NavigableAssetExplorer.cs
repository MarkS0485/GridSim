using System.Runtime.CompilerServices;
using GridSim.Ui.Assets.Pages;

namespace GridSim.Ui.Assets.Navigation;

/// <summary>
/// Makes the pages an asset explorer shows (UI programme ASSETS A3). The A2 explorer makes its pages through virtual
/// Create... methods so a front end can put its own page types on screen; this interface is the same set of factories,
/// handed to <see cref="NavigableAssetExplorer"/> so the navigator can learn each page's identity as it is made while a
/// front end still chooses the page types. Each method must return a NEW page of the kind named, made for
/// <paramref name="explorer"/>, exactly as the matching AssetExplorerModel default does (only the closed type may differ:
/// a front end may return a subclass of the neutral page with the neutral row types).
/// </summary>
public interface IAssetPageFactory
{
    /// <summary>The landing page.</summary>
    AssetPageModel Home(AssetExplorerModel explorer);

    /// <summary>A GENERATORS fleet page.</summary>
    AssetPageModel GeneratorFleet(AssetExplorerModel explorer);

    /// <summary>A GENERATOR PHASORS page.</summary>
    AssetPageModel PhasorFleet(AssetExplorerModel explorer);

    /// <summary>An INTERCONNECTORS fleet page.</summary>
    AssetPageModel InterconnectorFleet(AssetExplorerModel explorer);

    /// <summary>A TRANSFORMERS fleet page.</summary>
    AssetPageModel TransformerFleet(AssetExplorerModel explorer);

    /// <summary>A CABLES &amp; LINES fleet page.</summary>
    AssetPageModel CircuitFleet(AssetExplorerModel explorer);

    /// <summary>A SUBSTATIONS fleet page.</summary>
    AssetPageModel SubstationFleet(AssetExplorerModel explorer);

    /// <summary>A TAP CHANGERS fleet page.</summary>
    AssetPageModel TapChangerFleet(AssetExplorerModel explorer);

    /// <summary>A substation page for a bus id.</summary>
    AssetPageModel SubstationDetail(AssetExplorerModel explorer, int busId);

    /// <summary>A generator page (model index, confirmed by bus and model name).</summary>
    AssetPageModel GeneratorDetail(AssetExplorerModel explorer, int index, int bus, string? name);

    /// <summary>An interconnector page (model index, confirmed by bus and model name).</summary>
    AssetPageModel InterconnectorDetail(AssetExplorerModel explorer, int index, int bus, string? name);

    /// <summary>A circuit page (branch index, confirmed by its ends and model name).</summary>
    AssetPageModel CircuitDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name);

    /// <summary>A transformer page (branch index, confirmed by its ends and model name).</summary>
    AssetPageModel TransformerDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name);
}

/// <summary>
/// The neutral pages: exactly what the A2 <see cref="AssetExplorerModel"/> makes by default (the neutral page types with
/// the neutral row types), so an explorer built over this factory shows the same pages, type for type, as a plain
/// <see cref="AssetExplorerModel"/>.
/// </summary>
public sealed class NeutralAssetPageFactory : IAssetPageFactory
{
    /// <summary>The one instance (the factory holds no state).</summary>
    public static NeutralAssetPageFactory Instance { get; } = new();

    private NeutralAssetPageFactory() { }

    /// <inheritdoc />
    public AssetPageModel Home(AssetExplorerModel explorer) => new AssetHomeModel<AssetClassCardModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel GeneratorFleet(AssetExplorerModel explorer) => new GeneratorFleetModel<GenFleetRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel PhasorFleet(AssetExplorerModel explorer) => new GeneratorPhasorFleetModel<PhasorTileModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel InterconnectorFleet(AssetExplorerModel explorer) => new InterconnectorFleetModel<LinkFleetRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel TransformerFleet(AssetExplorerModel explorer) => new TransformerFleetModel<TransformerFleetRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel CircuitFleet(AssetExplorerModel explorer) => new CircuitFleetModel<BandChipModel, CircuitFleetRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel SubstationFleet(AssetExplorerModel explorer) => new SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel TapChangerFleet(AssetExplorerModel explorer) => new TapChangerFleetModel<TapChangerRowModel>(explorer);

    /// <inheritdoc />
    public AssetPageModel SubstationDetail(AssetExplorerModel explorer, int busId) =>
        new SubstationDetailModel<PlantRowModel, ConnectionRowModel>(busId, explorer);

    /// <inheritdoc />
    public AssetPageModel GeneratorDetail(AssetExplorerModel explorer, int index, int bus, string? name) =>
        new GeneratorDetailModel(index, bus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel InterconnectorDetail(AssetExplorerModel explorer, int index, int bus, string? name) =>
        new InterconnectorDetailModel(index, bus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel CircuitDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name) =>
        new CircuitDetailModel<CircuitStatusRowModel>(index, fromBus, toBus, name, explorer);

    /// <inheritdoc />
    public AssetPageModel TransformerDetail(AssetExplorerModel explorer, int index, int fromBus, int toBus, string? name) =>
        new TransformerDetailModel<TransformerReadingModel, TransformerEventModel>(index, fromBus, toBus, name, explorer);
}

/// <summary>
/// The A2 asset explorer with every page it makes labelled by its identity (UI programme ASSETS A3). It overrides each
/// Create... factory to ask an <see cref="IAssetPageFactory"/> for the page and to remember, beside the page, the exact
/// arguments it was made with (<see cref="AssetPageKey"/>). Nothing else changes: the explorer's stack, Open* routes,
/// Active gate and frame feed are the A2 code, so every value a page shows is A2's. The navigator
/// (<see cref="AssetNavigatorModel"/>) reads <see cref="KeyOf"/> to keep its tree in step with a page opened from inside
/// another page (a row click, a cross-link), which the A2 pages do by calling the explorer directly.
/// </summary>
public sealed class NavigableAssetExplorer : AssetExplorerModel
{
    private readonly IAssetPageFactory _pages;

    // Page -> the key it was made with. Weak, so a page dropped from the explorer and the navigator's history is freed.
    private readonly ConditionalWeakTable<AssetPageModel, AssetPageKey> _keys = new();

    /// <summary>An explorer over the neutral pages (the same page types as a plain <see cref="AssetExplorerModel"/>).</summary>
    public NavigableAssetExplorer() : this(NeutralAssetPageFactory.Instance) { }

    /// <summary>An explorer whose pages <paramref name="pages"/> makes.</summary>
    public NavigableAssetExplorer(IAssetPageFactory pages) : base(HomeMaker(pages))
    {
        _pages = pages;
        _keys.AddOrUpdate(Home, AssetPageKey.Home);
    }

    // The base constructor makes the landing page through this function; a null factory fails loud before anything else.
    private static Func<AssetExplorerModel, AssetPageModel> HomeMaker(IAssetPageFactory pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return e => pages.Home(e) ?? throw new InvalidOperationException("The page factory made no landing page.");
    }

    /// <summary>The factory this explorer makes its pages with.</summary>
    public IAssetPageFactory Pages => _pages;

    /// <summary>The key <paramref name="page"/> was made with, or null when this explorer did not make it.</summary>
    public AssetPageKey? KeyOf(AssetPageModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _keys.TryGetValue(page, out AssetPageKey? key) ? key : null;
    }

    // Remember the key beside a newly made page; a factory that returns nothing is a wiring mistake and fails loud.
    private AssetPageModel Track(AssetPageModel? page, AssetPageKey key)
    {
        if (page is null) throw new InvalidOperationException($"The page factory made no page for {key.Kind}.");
        _keys.AddOrUpdate(page, key);
        return page;
    }

    /// <summary>
    /// UI programme ASSETS B2: open a page the A2 factories do not make (a B2 overview page), recording the key it was
    /// made for first, so the navigator finds its tree node and history entry exactly as for an A2 page. The page is
    /// opened by the A2 Open (pushed on the stack and refreshed with the last frame).
    /// </summary>
    public void OpenTracked(AssetPageModel page, AssetPageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Open(Track(page, key));
    }

    /// <inheritdoc />
    public override AssetPageModel CreateGeneratorFleet() =>
        Track(_pages.GeneratorFleet(this), AssetPageKey.Fleet(AssetPageKind.GeneratorFleet));

    /// <inheritdoc />
    public override AssetPageModel CreatePhasorFleet() =>
        Track(_pages.PhasorFleet(this), AssetPageKey.Fleet(AssetPageKind.PhasorFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateInterconnectorFleet() =>
        Track(_pages.InterconnectorFleet(this), AssetPageKey.Fleet(AssetPageKind.InterconnectorFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateTransformerFleet() =>
        Track(_pages.TransformerFleet(this), AssetPageKey.Fleet(AssetPageKind.TransformerFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateCircuitFleet() =>
        Track(_pages.CircuitFleet(this), AssetPageKey.Fleet(AssetPageKind.CircuitFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateSubstationFleet() =>
        Track(_pages.SubstationFleet(this), AssetPageKey.Fleet(AssetPageKind.SubstationFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateTapChangerFleet() =>
        Track(_pages.TapChangerFleet(this), AssetPageKey.Fleet(AssetPageKind.TapChangerFleet));

    /// <inheritdoc />
    public override AssetPageModel CreateSubstationDetail(int busId) =>
        Track(_pages.SubstationDetail(this, busId), AssetPageKey.Substation(busId));

    /// <inheritdoc />
    public override AssetPageModel CreateGeneratorDetail(int index, int bus, string? name) =>
        Track(_pages.GeneratorDetail(this, index, bus, name), AssetPageKey.Generator(index, bus, name));

    /// <inheritdoc />
    public override AssetPageModel CreateInterconnectorDetail(int index, int bus, string? name) =>
        Track(_pages.InterconnectorDetail(this, index, bus, name), AssetPageKey.Interconnector(index, bus, name));

    /// <inheritdoc />
    public override AssetPageModel CreateCircuitDetail(int index, int fromBus, int toBus, string? name) =>
        Track(_pages.CircuitDetail(this, index, fromBus, toBus, name), AssetPageKey.Circuit(index, fromBus, toBus, name));

    /// <inheritdoc />
    public override AssetPageModel CreateTransformerDetail(int index, int fromBus, int toBus, string? name) =>
        Track(_pages.TransformerDetail(this, index, fromBus, toBus, name), AssetPageKey.Transformer(index, fromBus, toBus, name));
}
