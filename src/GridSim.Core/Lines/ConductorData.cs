namespace GridSim.Core.Lines;

/// <summary>What a conductor is for in the reduction: a phase conductor, or a grounded wire eliminated by Kron reduction.</summary>
public enum ConductorRole
{
    /// <summary>A phase conductor (kept).</summary>
    Phase,
    /// <summary>A continuously grounded wire (eliminated).</summary>
    GroundWire,
}

/// <summary>
/// One overhead conductor. Positions are metres (x horizontal, y height above ground). <see cref="ResistanceOhmPerKm"/> is
/// the AC resistance at the stated temperature (skin effect and temperature are the caller's stated inputs; no skin
/// model is applied here). <see cref="GmrM"/> is the geometric mean radius used for the self inductance (0.7788 times the
/// radius for a solid round wire), <see cref="OuterRadiusM"/> the physical radius used for the electric field.
/// </summary>
public sealed record ConductorData(
    string Name,
    ConductorRole Role,
    double XM,
    double HeightM,
    double ResistanceOhmPerKm,
    double GmrM,
    double OuterRadiusM);
