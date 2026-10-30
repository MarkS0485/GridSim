using GridSim.Core.Presentation;

namespace GridSim.Ui.Presentation;

/// <summary>A classified fuel for the platform-neutral views: display name, colour token, Core category, and whether it
/// is a spinning (inertial) machine.</summary>
/// <param name="Name">The display name ("CCGT", "Wind", ...), from Core's <see cref="FuelTaxonomy"/>.</param>
/// <param name="Colour">The fuel's colour, parsed from the taxonomy's hex exactly as the WPF <c>Fuels</c> parses it.</param>
/// <param name="Category">The Core category.</param>
/// <param name="Synchronous">True for spinning plant (contributes rotational inertia).</param>
public readonly record struct UiFuelInfo(string Name, UiColour Colour, FuelCategory Category, bool Synchronous)
{
    /// <summary>Fossil plant (the WPF <c>FuelInfo.IsFossil</c>).</summary>
    public bool IsFossil => Category == FuelCategory.Fossil;

    /// <summary>Renewable plant (the WPF <c>FuelInfo.IsRenewable</c>).</summary>
    public bool IsRenewable => Category == FuelCategory.Renewable;
}

/// <summary>
/// The neutral view of the shared fuel taxonomy (UI programme Phase 0.2): the same classification the WPF
/// <c>Visuals/Fuels.cs</c> gives, without its WPF colour type. Both read Core's <see cref="FuelTaxonomy"/>, so the
/// name, category, synchronous flag and colour bytes are identical; the only translation is hex to
/// <see cref="UiColour"/>, by the same parser the WPF palette uses (<see cref="ColourRamps.FromHex"/>).
/// </summary>
public static class UiFuels
{
    /// <summary>Classify a generator type. An unknown Core category is a programming error, so it throws, as the WPF
    /// translation does.</summary>
    public static UiFuelInfo Classify(string? type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        if (!Enum.IsDefined(d.Category))
            throw new ArgumentOutOfRangeException(nameof(type), d.Category, "Unknown fuel category from the Core taxonomy.");
        return new UiFuelInfo(d.Name, ColourRamps.FromHex(d.Color), d.Category, d.Synchronous);
    }
}
