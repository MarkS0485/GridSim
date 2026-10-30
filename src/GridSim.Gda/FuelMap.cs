using GridSim.Core.Model;

namespace GridSim.Gda;

/// <summary>
/// Maps the free-text fuel / plant-type strings used across the GDA datasets
/// (LTDS "Fuel Type", TEC/Embedded "Plant Type") onto a canonical GridSim
/// generator <c>type</c> and a representative inertia constant H (MWs/MVA).
/// Converter-connected plant (wind, solar, batteries, interconnectors) contribute
/// no synchronous inertia  -  exactly the effect that makes RoCoF worse as the fleet
/// decarbonises.
/// </summary>
public static class FuelMap
{
    public readonly record struct Kind(string Type, double H);

    /// <summary>
    /// Classify a raw dataset fuel / plant-type string. R305 rest and N020 (October 2026): the keyword list is Core's
    /// one fuel taxonomy, <see cref="FuelTypes"/> (GDA reader), which matches whole words by their start instead of any
    /// substring; this class keeps only the GB inertia constant H per canonical type (below) and the capacity factors.
    /// On every fuel / plant-type string in the GDA lake (NESO registers, NGED LTDS) the result is exactly what the old
    /// keyword list gave. What changed, for strings no lake source carries: "Charcoal" is no longer coal (H 4 -> 0) and
    /// "Boiler" no longer an oil engine (H 1 -> 0), the substring false positives N020 names; a bare "offshore" or
    /// "wind-offshore" is offshore wind (was other / onshore; H 0 either way, capacity factor 0.30 -> 0.45); a bare
    /// "onshore" is onshore wind (was other; H 0, capacity factor 0.30 either way); "import" is an interconnector
    /// (was other; H 0, capacity factor 0.30 -> 0.50); "grid" / "infeed" give the canonical type "grid" (was "other";
    /// H 0 and capacity factor 0.30 either way); "photovolt..." without "photovoltaic" is solar (H 0, capacity factor
    /// 0.30 -> 0.10).
    /// </summary>
    public static Kind Classify(string raw)
    {
        FuelKind kind = FuelTypes.Classify(raw, FuelReader.Gda);
        string type = FuelTypes.CanonicalType(kind);
        return new Kind(type, InertiaH(type));
    }

    // N345 (CF3, October 2026): the arms of InertiaH below, named so the v2 kind specs cite each
    // value by symbol instead of quoting the source line. Each constant is the literal it replaces, so no number moves.
    internal const double InertiaHNuclear = 5.0;   // "nuclear"
    internal const double InertiaHCcgt = 5.0;   // "ccgt"
    internal const double InertiaHOcgt = 4.0;   // "ocgt"
    internal const double InertiaHGas = 4.0;   // "gas"
    internal const double InertiaHCoal = 4.0;   // "coal"
    internal const double InertiaHBiomass = 4.0;   // "biomass"
    internal const double InertiaHPumpedStorage = 3.0;   // "pumped-storage"
    internal const double InertiaHHydro = 3.0;   // "hydro"
    internal const double InertiaHChp = 2.5;   // "chp"
    internal const double InertiaHRecip = 1.0;   // "recip"
    internal const double InertiaHOther = 0.0;   // _

    /// <summary>
    /// Representative inertia constant H (MWs/MVA) per canonical type, exactly the values this importer has always
    /// used: nuclear 5, CCGT 5, OCGT 4, gas 4, coal 4, biomass 4, pumped storage 3, hydro 3, CHP 2.5, reciprocating
    /// engines 1; converter-connected plant (wind, solar, storage including hydrogen, tidal, interconnectors), a grid
    /// infeed and anything unrecognised 0. (Frequency / inertia values: DEFERRED with Mark; not changed here.)
    /// </summary>
    public static double InertiaH(string canonicalType) => canonicalType switch
    {
        "nuclear" => InertiaHNuclear,
        "ccgt" => InertiaHCcgt,
        "ocgt" => InertiaHOcgt,
        "gas" => InertiaHGas,
        "coal" => InertiaHCoal,
        "biomass" => InertiaHBiomass,
        "pumped-storage" => InertiaHPumpedStorage,
        "hydro" => InertiaHHydro,
        "chp" => InertiaHChp,
        "recip" => InertiaHRecip,
        _ => InertiaHOther,
    };

    // N345 (CF3, October 2026): the arms of CapacityFactor below, named so the v2 kind specs cite each
    // value by symbol instead of quoting the source line. Each constant is the literal it replaces, so no number moves.
    internal const double CapacityFactorNuclear = 0.90;   // "nuclear"
    internal const double CapacityFactorCcgt = 0.45;   // "ccgt"
    internal const double CapacityFactorOcgt = 0.05;   // "ocgt"
    internal const double CapacityFactorGas = 0.35;   // "gas"
    internal const double CapacityFactorCoal = 0.0;   // "coal"
    internal const double CapacityFactorBiomass = 0.65;   // "biomass"
    internal const double CapacityFactorChp = 0.55;   // "chp"
    internal const double CapacityFactorHydro = 0.35;   // "hydro"
    internal const double CapacityFactorPumpedStorage = 0.10;   // "pumped-storage"
    internal const double CapacityFactorStorage = 0.05;   // "storage"
    internal const double CapacityFactorWindOnshore = 0.30;   // "wind-onshore"
    internal const double CapacityFactorWindOffshore = 0.45;   // "wind-offshore"
    internal const double CapacityFactorSolar = 0.10;   // "solar"
    internal const double CapacityFactorTidal = 0.20;   // "tidal"
    internal const double CapacityFactorInterconnector = 0.50;   // "interconnector"
    internal const double CapacityFactorRecip = 0.10;   // "recip"
    internal const double CapacityFactorOther = 0.30;   // _

    /// <summary>Representative GB capacity factor for a typical snapshot  -  turns nameplate into a realistic dispatch.</summary>
    public static double CapacityFactor(string type) => type switch
    {
        "nuclear" => CapacityFactorNuclear,
        "ccgt" => CapacityFactorCcgt,
        "ocgt" => CapacityFactorOcgt,
        "gas" => CapacityFactorGas,
        "coal" => CapacityFactorCoal,
        "biomass" => CapacityFactorBiomass,
        "chp" => CapacityFactorChp,
        "hydro" => CapacityFactorHydro,
        "pumped-storage" => CapacityFactorPumpedStorage,
        "storage" => CapacityFactorStorage,
        "wind-onshore" => CapacityFactorWindOnshore,
        "wind-offshore" => CapacityFactorWindOffshore,
        "solar" => CapacityFactorSolar,
        "tidal" => CapacityFactorTidal,
        "interconnector" => CapacityFactorInterconnector,
        "recip" => CapacityFactorRecip,
        _ => CapacityFactorOther,
    };
}
