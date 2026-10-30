namespace GridSim.Core.Dynamics.Distributed;

/// <summary>The dynamic character of a speed domain.</summary>
public enum DomainKind
{
    /// <summary>Rotating synchronous machines - the electromechanical swing masses.</summary>
    Synchronous,
    /// <summary>Inverter-coupled plant that emulates inertia via df/dt response (grid-following storage/wind).</summary>
    SyntheticInertia,
    /// <summary>Pure phase-locked-loop inverters / HVDC - no inertia contribution.</summary>
    Pll,
}

/// <summary>
/// GridSim-owned mapping from a generator's fuel/technology TYPE (the string
/// <see cref="GridSim.Gda.FuelMap"/> produces) to its synchronous machine speed - pole count and
/// rpm - and domain kind. GDA supplies only the type label; grouping machines into "speed domains"
/// by mechanical speed / pole ratio is GridSim physics (LOS sec. 2.7/sec. 5.5).
///
/// PROVENANCE: this is a conservative, documented MODELLING ASSUMPTION, not measured per-unit data
/// (GDA holds no pole/speed field). It is surfaced with an unvalidated tag where shown. rpm = 6000 / poles
/// at 50 Hz: the <see cref="MachineSpeed.Rpm"/> values here are the GB (50 Hz) speeds of the GDA fuel types. The speed
/// domains themselves (<see cref="DomainAssignment.Assign"/>) compute their rpm from the model's own nominal frequency,
/// 120 * f / poles (R196 remainder, D2, October 2026).
/// </summary>
public static class PoleMap
{
    /// <summary>A machine's synchronous speed: pole count, rpm, and whether it is a swing mass.</summary>
    /// <param name="IsKnownType">R239 (October 2026 review, stream B1): true when the type string names a technology this
    /// map classifies explicitly (a turbo-alternator, an engine, hydro, storage or a named converter technology); false for
    /// a blank, unknown or catch-all type, which falls to the PLL default. Only an unknown type may be overridden to a
    /// synchronous machine by its inertia constant (<see cref="DomainAssignment.Assign"/>).</param>
    public readonly record struct MachineSpeed(int Poles, double Rpm, DomainKind Kind, bool IsKnownType = true);

    /// <summary>Map a canonical fuel/type string (see FuelMap.Classify) to its machine speed.</summary>
    public static MachineSpeed ForType(string? type)
    {
        string t = (type ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            // Turbo-alternators (steam / gas turbine): 2-pole, 3000 rpm at 50 Hz.
            "nuclear" or "coal" or "biomass" or "ccgt" or "ocgt" or "chp" or "gas"
                => new MachineSpeed(2, 3000.0, DomainKind.Synchronous),
            // Reciprocating engines: typically 4-pole, 1500 rpm.
            "recip" => new MachineSpeed(4, 1500.0, DomainKind.Synchronous),
            // Hydro / pumped storage: low-speed, many poles (representative 8-pole, 750 rpm).
            "hydro" or "pumped-storage" or "pumped" => new MachineSpeed(8, 750.0, DomainKind.Synchronous),
            // Inverter-coupled storage may emulate inertia (grid-following) -> virtual synthetic domain.
            "storage" or "battery" or "bess" => new MachineSpeed(0, 0.0, DomainKind.SyntheticInertia),
            // R239: the converter-connected technologies, named explicitly (the canonical FuelTypes names and the short
            // forms the bundled data uses: onshore / offshore / pv). Same result as the default branch below (PLL, no
            // inertia), but KNOWN: an H value in their data is a converter's synthetic figure, not rotating mass, so it
            // must never turn them into a synchronous swing mass ("virtual inertia is never real mass").
            "wind" or "wind-onshore" or "wind-offshore" or "onshore" or "offshore" or "solar" or "pv" or "tidal"
                or "interconnector" or "hvdc" or "converter"
                => new MachineSpeed(0, 0.0, DomainKind.Pll),
            // Everything else (a blank, unknown or catch-all type such as "other" or "grid") -> PLL, flagged UNKNOWN.
            _ => new MachineSpeed(0, 0.0, DomainKind.Pll, IsKnownType: false),
        };
    }
}
