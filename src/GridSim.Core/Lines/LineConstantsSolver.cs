using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Lines;

/// <summary>
/// Overhead line constants from geometry. Defined order: validate geometry; primitive Z (earth-return, ohm/km) and Maxwell
/// potential coefficients P (perfect ground, km/F) over every conductor; Kron-eliminate grounded wires (Z) and take the
/// phase capacitance as the inverse of the Kron-reduced P block (a grounded wire sits at zero potential); transposition average (three phases only); symmetrical components (three phases only). Every stage is kept
/// in the result. Buried single-core cables with sheath and armour are solved by Cables.CableConstantsSolver (the
/// <c>line-constants --cable</c> path); this overhead solver is unchanged by it.
/// </summary>
public static class LineConstantsSolver
{
    /// <summary>The result schema label.</summary>
    public const string Schema = "gridsim-lineconstants/1";

    /// <summary>Solve for the geometry (refuses an invalid one).</summary>
    public static LineConstantsResult Solve(LineGeometry g)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.Validate();
        int n = g.Conductors.Count;
        double omega = 2.0 * Math.PI * g.FrequencyHz;
        var z = new Complex[n, n];
        var p = new double[n, n];
        double pk = 1.0 / (2.0 * Math.PI * EarthReturn.Eps0) / 1000.0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                var a = g.Conductors[i]; var b = g.Conductors[k];
                double dx = a.XM - b.XM, dy = a.HeightM - b.HeightM;
                double direct = i == k ? a.GmrM : Math.Sqrt(dx * dx + dy * dy);
                Complex zm = EarthReturn.ImpedancePerMetre(g.Method, omega, g.EarthResistivityOhmM,
                    a.HeightM, b.HeightM, i == k ? 0.0 : dx, direct) * 1000.0;
                if (i == k) zm += a.ResistanceOhmPerKm;
                z[i, k] = zm;
                double dirP = i == k ? a.OuterRadiusM : Math.Sqrt(dx * dx + dy * dy);
                double sy = a.HeightM + b.HeightM;
                double imgP = Math.Sqrt((i == k ? 0.0 : dx * dx) + sy * sy);
                p[i, k] = pk * DetMath.Log(imgP / dirP);
            }
        var phases = new List<int>(); var grounds = new List<int>();
        for (int i = 0; i < n; i++) (g.Conductors[i].Role == ConductorRole.Phase ? phases : grounds).Add(i);
        var zPhase = SequenceReduction.Kron(z, phases, grounds);
        var pc = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) pc[i, k] = p[i, k];
        var pRed = SequenceReduction.Kron(pc, phases, grounds);
        var pPhase = new double[phases.Count, phases.Count];
        for (int i = 0; i < phases.Count; i++) for (int k = 0; k < phases.Count; k++) pPhase[i, k] = pRed[i, k].Real;
        var cPhase = SequenceReduction.InvertReal(pPhase);
        var zT = (Complex[,])zPhase.Clone(); var cT = (double[,])cPhase.Clone();
        SequenceConstants? seq = null; string? note = null;
        if (phases.Count != 3) note = "OUT OF SCOPE: symmetrical components need exactly three phase conductors.";
        else
        {
            if (g.Transposed) { zT = SequenceReduction.TransposeAverage3(zPhase); cT = SequenceReduction.TransposeAverage3(cPhase); }
            else
            {
                // Untransposed: the sequence values of the mean self and mean mutual terms (the usual approximation); stated.
                var zs = SequenceReduction.TransposeAverage3(zPhase); var cs = SequenceReduction.TransposeAverage3(cPhase);
                note = "Untransposed line: sequence values use the mean self and mean mutual terms of the phase matrices.";
                var (a1, a0) = SequenceReduction.Sequence(zs); var (c1, c0) = SequenceReduction.Sequence(cs);
                seq = new SequenceConstants(a1, a0, c1, c0);
            }
            if (seq is null)
            {
                var (z1, z0) = SequenceReduction.Sequence(zT); var (c1, c0) = SequenceReduction.Sequence(cT);
                seq = new SequenceConstants(z1, z0, c1, c0);
            }
        }
        return new LineConstantsResult(
            g.Method == EarthReturnMethod.PerfectGround ? "perfect-ground" : "earth-integral",
            Schema, g.FrequencyHz, g.EarthResistivityOhmM,
            g.Conductors.Select(c => c.Name).ToArray(), phases.Select(i => g.Conductors[i].Name).ToArray(),
            z, p, zPhase, cPhase, zT, cT, seq, note,
            g.Method == EarthReturnMethod.EarthIntegral ? EarthReturn.TailBound : 0.0);
    }
}
