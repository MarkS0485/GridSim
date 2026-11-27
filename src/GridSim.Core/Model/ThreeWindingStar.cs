namespace GridSim.Core.Model;

/// <summary>
/// WP24: the star (T) equivalent of a three-winding transformer: from the pair impedances Z12, Z23 and Z31 (one component
/// at a time, resistance or reactance) the winding impedances Z1 = (Z12 + Z31 - Z23) / 2, Z2 = (Z12 + Z23 - Z31) / 2 and
/// Z3 = (Z31 + Z23 - Z12) / 2. The expressions and their evaluation order are those of the PSS/E reader
/// (<c>PsseRawParser</c>, three-winding transformers), so a unit given the same pair impedances gets the same bits on
/// either route. The reader keeps its own copy; it is not edited.
/// </summary>
public static class ThreeWindingStar
{
    /// <summary>(Z1, Z2, Z3) from (Z12, Z23, Z31).</summary>
    public static (double W1, double W2, double W3) Split(double z12, double z23, double z31) =>
        ((z12 + z31 - z23) / 2, (z12 + z23 - z31) / 2, (z31 + z23 - z12) / 2);
}
