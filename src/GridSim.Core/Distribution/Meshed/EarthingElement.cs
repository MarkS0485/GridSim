using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

// RC4 R4-15: earthing in the phase frame. Earth is the 0 V reference; a wire reaches it solidly, through a grounding
// impedance, or through a shared grid whose own impedance to remote earth gives the ground potential rise.

/// <summary>A common earth grid (or electrode system) that earthing elements can share: it connects to remote earth through
/// <paramref name="Z"/> (ohm). Its voltage is the ground potential rise. <paramref name="Z"/> must not be zero (a solid
/// connection to remote earth is an <see cref="EarthingElement"/> with no grid).</summary>
public sealed record EarthGrid(string Name, Complex Z);

/// <summary>
/// A grounding impedance from one wire at a node to earth, or to a shared <see cref="EarthGrid"/> when
/// <paramref name="Grid"/> is named. A zero <paramref name="Z"/> is a solid connection to remote earth (the wire is fixed at
/// 0 V); a zero impedance to a grid is refused.
/// </summary>
public sealed record EarthingElement(string Node, string Wire, Complex Z, string? Grid = null);


/// <summary>Closed-form earthing helpers.</summary>
public static class EarthingFormulas
{
    /// <summary>Resistance (ohm) of a hemispherical electrode of radius <paramref name="radiusM"/> in soil of resistivity
    /// <paramref name="resistivityOhmM"/>: rho / (2 pi r).</summary>
    public static double HemisphereResistance(double resistivityOhmM, double radiusM)
    {
        if (!(resistivityOhmM > 0 && radiusM > 0 && double.IsFinite(resistivityOhmM) && double.IsFinite(radiusM)))
            throw new ArgumentOutOfRangeException(nameof(radiusM), "resistivity and radius must be finite and above 0.");
        return resistivityOhmM / (2.0 * Math.PI * radiusM);
    }
}
