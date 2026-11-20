using System.Numerics;
using GridSim.Core.Lines;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>
/// RC4 R4-15: single-wire earth return (SWER). One conductor carries the load current and the earth is the return path. The
/// line's series impedance is the self impedance of a single conductor with earth return (the Carson/Pollaczek earth-return
/// integral of R4-04 <see cref="LineConstantsSolver"/>, ohm/km, which already contains the earth-return resistance and
/// reactance); the earth electrodes at the two ends are grounding resistances. The loop equation is
/// E = I (Z_line + R_source_earth + R_load_earth + Z_load).
/// </summary>
public static class Swer
{
    /// <summary>The self impedance (ohm/km) and shunt capacitance (F/km) of a single conductor with earth return, from the
    /// R4-04 earth-return integral at <paramref name="frequencyHz"/> and soil resistivity <paramref name="earthResistivityOhmM"/>.</summary>
    public static (Complex ZOhmPerKm, double CFaradPerKm) LineConstants(ConductorData conductor, double frequencyHz, double earthResistivityOhmM)
    {
        ConductorData c = conductor with { Role = ConductorRole.Phase };
        LineConstantsResult r = LineConstantsSolver.Solve(new LineGeometry([c], frequencyHz, earthResistivityOhmM, EarthReturnMethod.EarthIntegral, true));
        return (r.PhaseZ[0, 0], r.PhaseC[0, 0]);
    }

    /// <summary>
    /// Build a SWER feeder model: node "S" (wire a) fed by the source EMF <paramref name="emf"/> behind the source earth
    /// electrode <paramref name="rSourceEarth"/>; the line S-L of impedance <paramref name="zLine"/> (ohm, total) and optional shunt
    /// <paramref name="shuntSiemens"/> (total, to earth, half each end); node "L" (wires a and e) with the load between a and e
    /// and the load-end electrode <paramref name="rLoadEarth"/> from e to earth. Load: <paramref name="loadS"/> at
    /// <paramref name="nominalV"/> with <paramref name="zip"/> (default constant impedance).
    /// </summary>
    public static PhaseFrameModel Feeder(Complex emf, Complex rSourceEarth, Complex zLine, Complex shuntSiemens, Complex rLoadEarth,
        Complex loadS, double nominalV, Zip? zip = null)
    {
        var nodes = new[] { new FrameNode("S", ["a"]), new FrameNode("L", ["a", "e"]) };
        var sources = new[] { new FrameSource("S", ["a"], [emf], rSourceEarth == Complex.Zero ? null : [rSourceEarth]) };
        var line = new FrameBranch("line", "S", "L", ["a"], new Complex[,] { { zLine } },
            shuntSiemens == Complex.Zero ? null : new Complex[,] { { shuntSiemens } });
        var load = FrameLoad.LineLine("load", "L", "a", "e", loadS, nominalV, zip ?? Zip.ConstantImpedance);
        var earthing = new[] { new EarthingElement("L", "e", rLoadEarth) };
        return new PhaseFrameModel("swer", nodes, sources, [line], [load], earthing);
    }
}
