using GridSim.Core.Presentation;

namespace GridSim.Web.Visuals;

/// <summary>How a generator's fuel sits in the inertia / decarbonisation picture.</summary>
public enum FuelClass { Fossil, Nuclear, Renewable, Storage, Interconnector, Other }

/// <summary>A classified fuel: display name, hex colour, class, and whether it's a spinning (inertial) machine.</summary>
public readonly record struct FuelInfo(string Name, string Color, FuelClass Class, bool Synchronous)
{
    public bool IsFossil => Class == FuelClass.Fossil;
    public bool IsRenewable => Class == FuelClass.Renewable;
}

/// <summary>
/// The web view of the shared fuel taxonomy. The keyword table itself lives in ONE place,
/// <see cref="FuelTaxonomy"/> in Core (R305, October 2026: it used to be copied here, in the WPF <c>Fuels</c> and in
/// the map's <c>fuelColor</c>, and the copies drifted - this one classed reciprocating engines as non-synchronous
/// "Other" while Core treats them as synchronous machines). This class only translates Core's
/// <see cref="FuelCategory"/> into the web's <see cref="FuelClass"/> so existing web callers are unchanged.
/// Spinning (synchronous) plant contributes rotational inertia; wind, solar, storage and interconnectors are
/// converter-connected and contribute none.
/// </summary>
public static class Fuels
{
    public static FuelInfo Classify(string? type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        return new FuelInfo(d.Name, d.Color, ToFuelClass(d.Category), d.Synchronous);
    }

    /// <summary>One-to-one translation of Core's category; an unknown value is a programming error, so it throws.</summary>
    public static FuelClass ToFuelClass(FuelCategory c) => c switch
    {
        FuelCategory.Fossil => FuelClass.Fossil,
        FuelCategory.Nuclear => FuelClass.Nuclear,
        FuelCategory.Renewable => FuelClass.Renewable,
        FuelCategory.Storage => FuelClass.Storage,
        FuelCategory.Interconnector => FuelClass.Interconnector,
        FuelCategory.Other => FuelClass.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown fuel category from the Core taxonomy."),
    };
}
