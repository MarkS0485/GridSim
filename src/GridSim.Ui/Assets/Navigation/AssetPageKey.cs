namespace GridSim.Ui.Assets.Navigation;

// UI programme ASSETS A3: the identity of a page the asset explorer shows. The A2 page models keep the identity they
// were opened with (a model index confirmed by bus or ends and model name, or a bus id) private, and their public
// surface is pinned against the old WPF pages (UiAssetPagesParityTests fails on any property added to either side), so
// the navigator does not ask a page who it is: it records the key at the moment the explorer makes the page, through
// the explorer's own page factories (NavigableAssetExplorer). The key is exactly the argument list of that factory.

/// <summary>Which page of the asset explorer a key names.</summary>
public enum AssetPageKind
{
    /// <summary>The landing page (ASSETS): the seven class cards.</summary>
    Home,

    /// <summary>The GENERATORS fleet page.</summary>
    GeneratorFleet,

    /// <summary>The GENERATOR PHASORS page.</summary>
    PhasorFleet,

    /// <summary>The INTERCONNECTORS fleet page.</summary>
    InterconnectorFleet,

    /// <summary>The TRANSFORMERS fleet page.</summary>
    TransformerFleet,

    /// <summary>The CABLES &amp; LINES fleet page.</summary>
    CircuitFleet,

    /// <summary>The SUBSTATIONS fleet page.</summary>
    SubstationFleet,

    /// <summary>The TAP CHANGERS fleet page.</summary>
    TapChangerFleet,

    /// <summary>One substation (bus) page, by bus id.</summary>
    Substation,

    /// <summary>One generator page, by model index confirmed by bus and model name.</summary>
    Generator,

    /// <summary>One interconnector page, by model index confirmed by bus and model name.</summary>
    Interconnector,

    /// <summary>One circuit (line or cable) page, by branch index confirmed by its ends and model name.</summary>
    Circuit,

    /// <summary>One transformer page, by branch index confirmed by its ends and model name.</summary>
    Transformer,

    // ---- UI programme ASSETS B2 (plan S8): the class nodes the A2 explorer never had, each page an AssetOverviewPage ----

    /// <summary>The DEMANDS class page (the demand records of the case file).</summary>
    DemandFleet,

    /// <summary>One demand record, by its ordinal among the case's demand records, its bus and name.</summary>
    Demand,

    /// <summary>The BOUNDARIES class page.</summary>
    BoundaryFleet,

    /// <summary>One boundary, by its model index and name.</summary>
    Boundary,

    /// <summary>The PROTECTIONS class page.</summary>
    ProtectionFleet,

    /// <summary>One placed protection, by its model index, monitored bus and name.</summary>
    Protection,

    /// <summary>The ASSET CLASSES class page (classes and bindings).</summary>
    AssetClassFleet,

    /// <summary>One asset class, by its library index and name.</summary>
    AssetClass,

    /// <summary>One binding rule, by its index and match type.</summary>
    AssetBinding,

    /// <summary>The OUT OF SERVICE class page (units marked out of service).</summary>
    OutOfServiceFleet,

    /// <summary>One out-of-service unit, by its position in the whole fleet, its bus and name.</summary>
    OutOfServiceUnit,

    // ---- UI programme ASSETS slice 5-view (plan Q12): the asset overlay, carried and not used by the solver ----

    /// <summary>The OVERLAY class page (every overlay record, by kind).</summary>
    OverlayFleet,

    /// <summary>One kind's page in the overlay's by-kind view, by the kind name (<see cref="AssetPageKey.Name"/>).</summary>
    OverlayKind,

    /// <summary>One overlay record, by its position in the overlay and its id (<see cref="AssetPageKey.Name"/>).</summary>
    OverlayRecord,
}

/// <summary>
/// The identity of one explorer page: its kind plus the exact arguments its factory was called with. Two keys are equal
/// when the explorer would make the same page for both (record equality: kind, index, bus fields and name, the name
/// compared ordinally). The fields a kind does not use stay at their defaults (index -1, buses 0, name null).
/// </summary>
/// <param name="Kind">Which page.</param>
/// <param name="Index">The model index (generator or branch), or -1 when the kind has none or the page was opened
/// without one (a transformer opened by name only).</param>
/// <param name="BusA">The bus id (substation), the unit's bus (generator, interconnector) or the From end (branch).</param>
/// <param name="BusB">The To end (branch); 0 for every other kind.</param>
/// <param name="Name">The model name the page confirms the unit by (null when the model has none).</param>
public sealed record AssetPageKey(AssetPageKind Kind, int Index = -1, int BusA = 0, int BusB = 0, string? Name = null)
{
    /// <summary>The landing page.</summary>
    public static AssetPageKey Home { get; } = new(AssetPageKind.Home);

    /// <summary>A fleet (class) page. Throws for a kind that is not a fleet page.</summary>
    public static AssetPageKey Fleet(AssetPageKind kind) => IsFleet(kind)
        ? new AssetPageKey(kind)
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a fleet page kind.");

    /// <summary>A substation page for <paramref name="busId"/>.</summary>
    public static AssetPageKey Substation(int busId) => new(AssetPageKind.Substation, -1, busId);

    /// <summary>A generator page (model index, bus, model name).</summary>
    public static AssetPageKey Generator(int index, int bus, string? name) => new(AssetPageKind.Generator, index, bus, 0, name);

    /// <summary>An interconnector page (model index, bus, model name).</summary>
    public static AssetPageKey Interconnector(int index, int bus, string? name) => new(AssetPageKind.Interconnector, index, bus, 0, name);

    /// <summary>A circuit page (branch index, From, To, model name).</summary>
    public static AssetPageKey Circuit(int index, int from, int to, string? name) => new(AssetPageKind.Circuit, index, from, to, name);

    /// <summary>A transformer page (branch index, From, To, model name).</summary>
    public static AssetPageKey Transformer(int index, int from, int to, string? name) => new(AssetPageKind.Transformer, index, from, to, name);

    /// <summary>True for the seven fleet (class) page kinds.</summary>
    public static bool IsFleet(AssetPageKind kind) => kind is AssetPageKind.GeneratorFleet or AssetPageKind.PhasorFleet
        or AssetPageKind.InterconnectorFleet or AssetPageKind.TransformerFleet or AssetPageKind.CircuitFleet
        or AssetPageKind.SubstationFleet or AssetPageKind.TapChangerFleet;

    /// <summary>True for the B2 class page kinds (DEMANDS, BOUNDARIES, PROTECTIONS, ASSET CLASSES, OUT OF SERVICE).</summary>
    public static bool IsOverviewClass(AssetPageKind kind) => kind is AssetPageKind.DemandFleet or AssetPageKind.BoundaryFleet
        or AssetPageKind.ProtectionFleet or AssetPageKind.AssetClassFleet or AssetPageKind.OutOfServiceFleet;

    /// <summary>A B2 class page. Throws for any other kind.</summary>
    public static AssetPageKey OverviewClass(AssetPageKind kind) => IsOverviewClass(kind)
        ? new AssetPageKey(kind)
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a B2 class page kind.");
}
