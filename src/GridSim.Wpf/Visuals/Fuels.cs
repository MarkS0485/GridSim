using System.Windows.Media;
using GridSim.Core.Presentation;

namespace GridSim.Wpf.Visuals;

/// <summary>How a generator's fuel sits in the inertia / decarbonisation picture.</summary>
public enum FuelClass { Fossil, Nuclear, Renewable, Storage, Interconnector, Other }

/// <summary>A classified fuel: display name, colour, and whether it's a spinning (inertial) machine.</summary>
public readonly record struct FuelInfo(string Name, Color Color, FuelClass Class, bool Synchronous)
{
    public bool IsFossil => Class == FuelClass.Fossil;
    public bool IsRenewable => Class == FuelClass.Renewable;
}

/// <summary>
/// The WPF view of the shared fuel taxonomy. The keyword table lives in ONE place, Core's
/// <see cref="FuelTaxonomy"/> (N016 / R305 remainder, October 2026): this class used to keep its own copy, which had
/// drifted from the web and Core one - it read "hydrogen" as synchronous Hydro (the substring "hydro") and left
/// reciprocating engines ("recip", "diesel", "engine") as non-synchronous "Other". It now only translates Core's
/// <see cref="FuelCategory"/> and hex colour into the WPF <see cref="FuelClass"/> and <see cref="Color"/>, exactly as
/// the web <c>Fuels</c> does, so existing WPF callers are unchanged.
/// <para>Behaviour change, stated: a generator typed "hydrogen" / "fuel cell" / "electrolyser" now reads as
/// Hydrogen (storage, converter-connected, no inertia) instead of Hydro (renewable, synchronous); "recip" / "diesel"
/// / "engine" now reads as Reciprocating engine (fossil, synchronous) instead of Other. Every other classification
/// and colour is identical to the old WPF table.</para>
/// Spinning (synchronous) plant contributes rotational inertia; wind, solar, storage and interconnectors are
/// converter-connected and contribute none.
/// </summary>
public static class Fuels
{
    public static FuelInfo Classify(string? type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        return new FuelInfo(d.Name, Palette.FromHex(d.Color), ToFuelClass(d.Category), d.Synchronous);
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
