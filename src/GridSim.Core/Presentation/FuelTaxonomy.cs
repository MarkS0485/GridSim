namespace GridSim.Core.Presentation;

/// <summary>How a generator's fuel sits in the inertia / decarbonisation picture.</summary>
public enum FuelCategory { Fossil, Nuclear, Renewable, Storage, Interconnector, Other }

/// <summary>A classified fuel: display name, hex colour, category, and whether it is a spinning (synchronous,
/// inertial) machine.</summary>
public readonly record struct FuelDescriptor(string Name, string Color, FuelCategory Category, bool Synchronous);

/// <summary>
/// The ONE fuel taxonomy for the presentation layers (R305, October 2026): maps a generator's free-text
/// <see cref="Model.Generator.Type"/> onto display name, colour, category and synchronous/converter split. It used to
/// be copied into the web <c>Fuels</c>, the WPF <c>Fuels</c> and the map's <c>fuelColor</c>, and the copies could
/// drift; the web one now delegates here.
/// <para>Two classifications fixed on the way:</para>
/// <list type="bullet">
/// <item><b>Reciprocating engines</b> (<c>recip</c>, the canonical type GDA's <c>FuelMap</c> gives diesel / oil /
/// gas-engine plant, plus <c>reciprocating</c>, <c>diesel</c>, <c>engine</c>) are synchronous fossil machines - Core's
/// <c>PoleMap</c> treats <c>recip</c> as a 4-pole synchronous machine. They used to fall through to "Other"
/// (non-synchronous, non-fossil), so their inertia was counted in the total but not the intrinsic figure and
/// their CO2 was not counted as fossil. "gas engine" still reads as Gas (the gas rule is checked first), exactly
/// as before.</item>
/// <item><b>Hydrogen</b> (<c>hydrogen</c>, <c>fuel cell</c>, <c>electrolyser</c>) is checked BEFORE hydro: the
/// substring test used to read "hydrogen" as synchronous hydro. A hydrogen fuel cell or electrolyser is
/// converter-connected power-to-gas-to-power plant, shown as storage with no inertia. Hydrogen burnt in a gas
/// turbine, CCGT or CHP is caught by those earlier rules and stays synchronous fossil-class plant.</item>
/// </list>
/// <para>R305 rest and N020 (October 2026): the keyword list itself now lives in Core's one fuel taxonomy,
/// <see cref="Model.FuelTypes"/>, shared with the GDA importer, the DC-OPF merit order and carbon tracing, and matches
/// whole words by their start instead of any substring. This class keeps only the display table by kind. What that
/// changed for the shells, exactly: "onshore" (the type data/uk and data/gb-synthetic give onshore wind) shows as
/// Wind (onshore), Renewable, instead of Other (still non-synchronous); "gas turbine" shows as OCGT instead of Gas, and
/// "CHP (Combined Heat and Power)" as CHP instead of CCGT, and a bare "combined" as Gas instead of CCGT (all three
/// still synchronous fossil); "Charcoal" is no longer Coal and "Microwave" no longer Tidal (substring false positives,
/// now Other). No canonical type string changes its synchronous flag; the keywords whose reading would change one are
/// deferred to the frequency / inertia pass (see <see cref="Model.FuelTypes"/>).</para>
/// </summary>
public static class FuelTaxonomy
{
    /// <summary>Classify a generator's type string for display: Core's one taxonomy picks the kind (presentation
    /// reader), and the table below gives its name, colour, category and synchronous flag.</summary>
    public static FuelDescriptor Classify(string? type) => Describe(Model.FuelTypes.Classify(type, Model.FuelReader.Presentation));

    /// <summary>
    /// True when a generator of this type is an interconnector LINK: a real cross-border (or Crown dependency) circuit of
    /// the interconnector kind (<c>interconnector</c>, <c>hvdc</c>, <c>import</c>). This is the ONE rule for every place
    /// that lists, counts or totals links: the INTERCONNECTORS page and card, the explorer tree, the GENERATION tab's
    /// import-links panel and net import, and the web projections.
    /// <para>Bug #28 (2026-10-24): those places used to test the CATEGORY, and the category also holds the grid infeed
    /// (<see cref="Model.FuelKind.GridInfeed"/>: the slack balancer and the voltage-control units at a model boundary,
    /// power-flow devices, not links). On gb-full that put 1,072 grid infeeds (the national slack at Melksham and 1,071
    /// GSP voltage-control units) on the INTERCONNECTORS page beside the 12 real links, and its capacity read 849 GW.
    /// A grid infeed is now listed with the generators. The CATEGORY is unchanged: a grid infeed still counts as imported
    /// energy in the fuel mix, the carbon intensity and THE MACHINE's IMPORTS group, where "energy from outside the
    /// model" is the right reading. A grid infeed is not plant either, so the generator pages leave it out as they always
    /// have (see <see cref="IsGridInfeed"/>).</para>
    /// </summary>
    public static bool IsInterconnectorLink(string? type) =>
        Model.FuelTypes.Classify(type, Model.FuelReader.Presentation) == Model.FuelKind.Interconnector;

    /// <summary>True when a generator of this type is a grid infeed (<see cref="Model.FuelKind.GridInfeed"/>: <c>grid</c>,
    /// <c>infeed</c>): the slack balancer or a voltage-control unit at a model boundary, a power-flow device that is
    /// neither plant nor an interconnector link.</summary>
    public static bool IsGridInfeed(string? type) =>
        Model.FuelTypes.Classify(type, Model.FuelReader.Presentation) == Model.FuelKind.GridInfeed;

    /// <summary>The display descriptor of a kind. The names, colours, categories and synchronous flags are exactly the
    /// ones this taxonomy has always shown for these classes.</summary>
    public static FuelDescriptor Describe(Model.FuelKind kind) => kind switch
    {
        Model.FuelKind.Nuclear => new("Nuclear", "#7E57C2", FuelCategory.Nuclear, true),
        Model.FuelKind.Coal => new("Coal", "#5D4037", FuelCategory.Fossil, true),
        Model.FuelKind.Ccgt => new("CCGT", "#FF8A3D", FuelCategory.Fossil, true),
        Model.FuelKind.Ocgt => new("OCGT", "#FFB55A", FuelCategory.Fossil, true),
        Model.FuelKind.Chp => new("CHP", "#D98A45", FuelCategory.Fossil, true),
        Model.FuelKind.Biomass => new("Biomass", "#8BC34A", FuelCategory.Renewable, true),
        Model.FuelKind.PumpedStorage => new("Pumped storage", "#26A69A", FuelCategory.Renewable, true),
        // A hydrogen fuel cell or electrolyser is converter-connected power-to-gas-to-power plant: storage, no inertia.
        Model.FuelKind.Hydrogen => new("Hydrogen", "#80CBC4", FuelCategory.Storage, false),
        Model.FuelKind.Hydro => new("Hydro", "#29B6F6", FuelCategory.Renewable, true),
        Model.FuelKind.WindOffshore => new("Wind (offshore)", "#00B8D4", FuelCategory.Renewable, false),
        Model.FuelKind.WindOnshore => new("Wind (onshore)", "#4DD0E1", FuelCategory.Renewable, false),
        Model.FuelKind.Solar => new("Solar", "#FFD54F", FuelCategory.Renewable, false),
        Model.FuelKind.Storage => new("Storage", "#EC407A", FuelCategory.Storage, false),
        Model.FuelKind.Interconnector => new("Interconnector", "#90A4AE", FuelCategory.Interconnector, false),
        Model.FuelKind.GridInfeed => new("Grid infeed", "#607D8B", FuelCategory.Interconnector, false),
        Model.FuelKind.Gas => new("Gas", "#FF8A3D", FuelCategory.Fossil, true),
        // Reciprocating engines: synchronous fossil machines (R305), as Core's PoleMap treats "recip".
        Model.FuelKind.Recip => new("Reciprocating engine", "#A1887F", FuelCategory.Fossil, true),
        Model.FuelKind.Tidal => new("Tidal", "#26C6DA", FuelCategory.Renewable, false),
        Model.FuelKind.Other => new("Other", "#9E9E9E", FuelCategory.Other, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fuel kind."),
    };
}
