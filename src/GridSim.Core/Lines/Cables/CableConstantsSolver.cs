using System.Numerics;

namespace GridSim.Core.Lines.Cables;

/// <summary>
/// The result of <see cref="CableConstantsSolver.Solve"/>. <see cref="Line"/> holds every stage in the same form as the
/// overhead solver (primitive Z and P over all conductors, the bonded phase Z and C, the transposition average and the
/// sequence constants; ohm/km, km/F, F/km). The cable-only parts: the conductor admittance matrix including the
/// dielectric conductance (S/km), the phase conductance G (S/km), the largest Wedepohl argument |m_e| max(d, h_i + h_j)
/// met, and any warnings.
/// </summary>
public sealed record CableConstantsResult(
    LineConstantsResult Line,
    CableBonding Bonding,
    CableEarthMethod Earth,
    Complex[,] ConductorY,
    double[,] PhaseG,
    double MaxWedepohlArgument,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Series impedance and shunt admittance of buried single-core cables (core, sheath, armour), after Ametani, "A general
/// formulation of impedance and admittance of cables", IEEE Trans. PAS-99(3), 1980, and the cable constants chapter of the
/// EMTP Theory Book (Dommel, BPA, 1986). Defined order:
/// <list type="number">
/// <item>Validate the system.</item>
/// <item>For each cable, the loop impedances: loop k runs out along conductor k and back along conductor k + 1 (the
/// last loop returns through the earth). Z_loop[k,k] = outer surface impedance of conductor k + its insulation + inner
/// surface impedance of conductor k + 1; Z_loop[k,k+1] = -(mutual impedance of conductor k + 1)
/// (<see cref="TubularImpedance"/>). The conductor matrix is Z_c = B^T Z_loop B, B the lower-triangular matrix of ones
/// (loop current k = the sum of conductor currents 0..k).</item>
/// <item>Earth return (<see cref="BuriedEarthReturn"/>) between every pair of cables, the self term at the cable's outer
/// radius, added to every conductor pair of the two cables.</item>
/// <item>Admittance per insulation layer: C = 2 pi eps0 eps_r / ln(r_o / r_i) and G = w C tan(delta); in conductor form
/// the tridiagonal Y = B^-1 diag(y) B^-T. Buried cables are screened from each other by the earth, so Y is block
/// diagonal. The potential coefficients P = B^T diag(1/C) B are kept as the primitive P.</item>
/// <item>Bonding: both ends, Kron elimination of the sheaths and armour (earthed, as the overhead solver eliminates
/// ground wires); single point, the metallic layers carry no current and are dropped; cross-bonded, the ideal
/// cross-bonding average of Z over the three cyclic positions of the metallic layers, then Kron elimination. In every
/// case the metallic layers sit at earth potential for the shunt admittance, so the phase C and G are the core-layer
/// values.</item>
/// <item>Transposition average and symmetrical components as the overhead solver (<see cref="SequenceReduction"/>).</item>
/// </list>
/// Scope: single-core cables only (no pipe-type or three-core cables), homogeneous earth, no proximity effect between
/// cables beyond the earth-return mutuals.
/// </summary>
public static class CableConstantsSolver
{
    /// <summary>The result schema label.</summary>
    public const string Schema = "gridsim-cableconstants/1";

    /// <summary>Solve for the cable system (refuses an invalid one).</summary>
    public static CableConstantsResult Solve(CableGeometry g)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.Validate();
        int nc = g.Cables.Count;
        double omega = 2.0 * Math.PI * g.FrequencyHz;
        var offsets = new int[nc];
        int total = 0;
        for (int i = 0; i < nc; i++) { offsets[i] = total; total += g.Cables[i].Conductors.Count; }

        var z = new Complex[total, total];   // ohm/m while assembling
        var cap = new double[total, total];  // F/m
        var con = new double[total, total];  // S/m
        var pot = new double[total, total];  // m/F
        var names = new string[total];
        var phases = new List<int>(); var metal = new List<int>();
        for (int i = 0; i < nc; i++)
        {
            var c = g.Cables[i];
            int n = c.Conductors.Count, o = offsets[i];
            for (int k = 0; k < n; k++) { names[o + k] = c.ConductorName(k); (k == 0 ? phases : metal).Add(o + k); }
            Complex[,] zc = InternalImpedance(c, omega);
            for (int p = 0; p < n; p++) for (int q = 0; q < n; q++) z[o + p, o + q] = zc[p, q];
            for (int k = 0; k < n; k++)
            {
                double ri = c.Conductors[k].OuterRadiusM, ro = c.InsulationOuterRadius(k);
                double ck = 2.0 * Math.PI * EarthReturn.Eps0 * c.Insulation[k].RelativePermittivity / DetMath.Log(ro / ri);
                double gk = omega * ck * c.Insulation[k].LossTangent;
                double pk = 1.0 / ck;
                cap[o + k, o + k] += ck; con[o + k, o + k] += gk;
                if (k + 1 < n)
                {
                    cap[o + k + 1, o + k + 1] += ck; cap[o + k, o + k + 1] -= ck; cap[o + k + 1, o + k] -= ck;
                    con[o + k + 1, o + k + 1] += gk; con[o + k, o + k + 1] -= gk; con[o + k + 1, o + k] -= gk;
                }
                // P_c[p,q] = sum of the layer coefficients outside both conductors (k >= max(p, q)).
                for (int p = 0; p <= k; p++) for (int q = 0; q <= k; q++) pot[o + p, o + q] += pk;
            }
        }

        double maxArg = 0.0;
        for (int i = 0; i < nc; i++)
            for (int j = i; j < nc; j++)
            {
                var a = g.Cables[i]; var b = g.Cables[j];
                double dx = a.XM - b.XM, dy = a.DepthM - b.DepthM;
                double direct = i == j ? a.OuterRadiusM : Math.Sqrt(dx * dx + dy * dy);
                Complex ze = BuriedEarthReturn.ImpedancePerMetre(g.Earth, omega, g.EarthResistivityOhmM,
                    a.DepthM, b.DepthM, i == j ? 0.0 : dx, direct);
                maxArg = Math.Max(maxArg, BuriedEarthReturn.WedepohlArgument(omega, g.EarthResistivityOhmM, a.DepthM + b.DepthM, direct));
                int na = a.Conductors.Count, nb = b.Conductors.Count;
                for (int p = 0; p < na; p++)
                    for (int q = 0; q < nb; q++)
                    {
                        z[offsets[i] + p, offsets[j] + q] += ze;
                        if (i != j) z[offsets[j] + q, offsets[i] + p] += ze;
                    }
            }

        var zKm = new Complex[total, total];
        var pKm = new double[total, total];
        var yKm = new Complex[total, total];
        for (int p = 0; p < total; p++)
            for (int q = 0; q < total; q++)
            {
                zKm[p, q] = z[p, q] * 1000.0;
                pKm[p, q] = pot[p, q] / 1000.0;
                yKm[p, q] = new Complex(con[p, q] * 1000.0, omega * cap[p, q] * 1000.0);
            }

        Complex[,] zPhase = g.Bonding switch
        {
            CableBonding.BothEnds => SequenceReduction.Kron(zKm, phases, metal),
            CableBonding.SinglePoint => SequenceReduction.Sub(zKm, phases, phases),
            _ => SequenceReduction.Kron(CrossBondingAverage(zKm, g.Cables[0].Conductors.Count), phases, metal),
        };
        int np = phases.Count;
        var cPhase = new double[np, np];
        var gPhase = new double[np, np];
        for (int i = 0; i < np; i++)
            for (int k = 0; k < np; k++)
            {
                cPhase[i, k] = cap[phases[i], phases[k]] * 1000.0;
                gPhase[i, k] = con[phases[i], phases[k]] * 1000.0;
            }

        var zT = (Complex[,])zPhase.Clone(); var cT = (double[,])cPhase.Clone();
        SequenceConstants? seq = null; string? note = null;
        if (np != 3) note = "OUT OF SCOPE: symmetrical components need exactly three cables.";
        else if (g.Transposed)
        {
            zT = SequenceReduction.TransposeAverage3(zPhase); cT = SequenceReduction.TransposeAverage3(cPhase);
            var (z1, z0) = SequenceReduction.Sequence(zT); var (c1, c0) = SequenceReduction.Sequence(cT);
            seq = new SequenceConstants(z1, z0, c1, c0);
        }
        else
        {
            var zs = SequenceReduction.TransposeAverage3(zPhase); var cs = SequenceReduction.TransposeAverage3(cPhase);
            note = "Untransposed cables: sequence values use the mean self and mean mutual terms of the phase matrices.";
            var (z1, z0) = SequenceReduction.Sequence(zs); var (c1, c0) = SequenceReduction.Sequence(cs);
            seq = new SequenceConstants(z1, z0, c1, c0);
        }

        var warnings = new List<string>();
        if (g.Earth == CableEarthMethod.Wedepohl && maxArg > BuriedEarthReturn.WedepohlLimit)
            warnings.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The Wedepohl earth-return form is outside its range: |m_e| max(d, h_i + h_j) = {maxArg:G6} exceeds {BuriedEarthReturn.WedepohlLimit:G6} (error above about 1e-3); use the Pollaczek integral."));

        var line = new LineConstantsResult(
            g.Earth == CableEarthMethod.Pollaczek ? "pollaczek" : "wedepohl",
            Schema, g.FrequencyHz, g.EarthResistivityOhmM,
            names, phases.Select(i => names[i]).ToArray(),
            zKm, pKm, zPhase, cPhase, zT, cT, seq, note,
            g.Earth == CableEarthMethod.Pollaczek ? EarthReturn.TailBound : 0.0);
        return new CableConstantsResult(line, g.Bonding, g.Earth, yKm, gPhase, maxArg, warnings);
    }

    /// <summary>
    /// The internal series impedance matrix of one cable (ohm/m), conductors from the inside out, without the earth
    /// return: Z_c = B^T Z_loop B, filled on and above the diagonal and mirrored so it is exactly symmetric.
    /// </summary>
    public static Complex[,] InternalImpedance(SingleCoreCable c, double omega)
    {
        ArgumentNullException.ThrowIfNull(c);
        int n = c.Conductors.Count;
        var tubes = new TubularImpedance.Tube[n];
        for (int k = 0; k < n; k++)
        {
            var m = c.Conductors[k];
            if (m.InnerRadiusM > 0)
                tubes[k] = TubularImpedance.Tubular(omega, m.ResistivityOhmM, m.RelativePermeability, m.InnerRadiusM, m.OuterRadiusM);
        }
        var loop = new Complex[n, n];
        for (int k = 0; k < n; k++)
        {
            var m = c.Conductors[k];
            Complex outer = m.InnerRadiusM > 0
                ? tubes[k].Outer
                : TubularImpedance.SolidCore(omega, m.ResistivityOhmM, m.RelativePermeability, m.OuterRadiusM);
            loop[k, k] = outer + TubularImpedance.Insulation(omega, c.Insulation[k].RelativePermeability, m.OuterRadiusM, c.InsulationOuterRadius(k));
            if (k + 1 < n)
            {
                loop[k, k] += tubes[k + 1].Inner;
                loop[k, k + 1] = -tubes[k + 1].Mutual;
                loop[k + 1, k] = -tubes[k + 1].Mutual;
            }
        }
        var zc = new Complex[n, n];
        for (int p = 0; p < n; p++)
            for (int q = p; q < n; q++)
            {
                Complex s = Complex.Zero;
                for (int a = p; a < n; a++) for (int b = q; b < n; b++) s += loop[a, b];
                zc[p, q] = s;
                zc[q, p] = s;
            }
        return zc;
    }

    /// <summary>
    /// Ideal cross-bonding: the average of Z over the three minor sections, in section s the metallic layer that starts
    /// in cable i lying in cable (i + s) mod 3; cores stay in place. Every cable has <paramref name="perCable"/> conductors.
    /// </summary>
    public static Complex[,] CrossBondingAverage(Complex[,] z, int perCable)
    {
        ArgumentNullException.ThrowIfNull(z);
        int total = z.GetLength(0);
        int Map(int k, int s)
        {
            int cable = k / perCable, layer = k % perCable;
            return layer == 0 ? k : ((cable + s) % 3) * perCable + layer;
        }
        var r = new Complex[total, total];
        for (int p = 0; p < total; p++)
            for (int q = 0; q < total; q++)
                r[p, q] = (z[Map(p, 0), Map(q, 0)] + z[Map(p, 1), Map(q, 1)] + z[Map(p, 2), Map(q, 2)]) / 3.0;
        return r;
    }
}
