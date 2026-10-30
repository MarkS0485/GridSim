using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>
/// N335 / N334 (CF5): the series (leakage) impedance of a transformer element whose To side is a delta (full or open),
/// from its PER-WINDING impedances, as the 3x3 line-to-neutral equivalent the three-phase sweep's forward pass drops
/// across (V_to = TransformerOutput(V_from) - Z I_to). Derived from Kersting's generalised-matrix construction
/// (W. H. Kersting, Distribution System Modeling and Analysis, the delta / delta, ungrounded wye / delta and open
/// wye / open delta sections), written out here in full because no published reference result is on disk:
///
/// The To-side delta windings ab, bc, ca carry the winding currents I_D = (I_ab, I_bc, I_ca); the line currents are
/// I_a = I_ab - I_ca, I_b = I_bc - I_ab, I_c = I_ca - I_bc. Each winding's terminal voltage is its ideal EMF less its own
/// drop, V_xy = E_xy - Zw_xy I_xy, so the line-to-line drop is Zw_D I_D (Zw_D = diag of the winding impedances). The
/// line currents fix I_D only up to a common circulating current k (the three-wire To side has I_a + I_b + I_c = 0):
/// I_D = M I_abc + k (1, 1, 1) with the particular solution M I_abc = (0, I_b, -I_a). The connection fixes k:
///   - delta / delta: the HV delta's EMFs sum to zero, so KVL round the LV delta gives sum Zw_xy I_xy = 0:
///     k = -(z . M I) / sum z, the circulating current that unequal windings carry (zero when they are equal);
///   - ungrounded wye / delta: each primary winding current is its secondary winding current over the ratio, and the
///     floating neutral makes them sum to zero, so sum I_D = 0: k = -(sum M I) / 3; the neutral-point shift then adds
///     (z . I_D) / 3 to every winding EMF, so the line-to-line drop is (Zw_D - (1/3) 1 z^T) I_D;
///   - open (two windings ab, bc; no ca winding): I_ca = 0, so I_D = (I_a, -I_c, 0) exactly, and the open side's
///     line-to-line voltage closes the triangle: drop_ca = -(drop_ab + drop_bc).
/// The line-to-line drop d is turned into the line-to-neutral equivalent of zero zero-sequence (the convention every
/// voltage on a three-wire GridSim bus uses) by Kersting's W = (1/3) [[2, 1, 0], [0, 2, 1], [1, 0, 2]], so Z = W d / I.
///
/// Units: each winding impedance is given in per-unit on the bank's per-phase power base and the To side's voltage
/// base, exactly the number the equal-winding element puts on its diagonal (PhaseImpedance.Diagonal(z) in the loader).
/// A delta winding sits across a line-to-line voltage, so on the line-to-neutral impedance base it is three times that
/// number (Zw = 3 z); with all three equal the equivalent is z on every phase, which is the existing element.
///
/// The equivalent holds for three-wire To-side currents (I_a + I_b + I_c = 0), which the sweep guarantees below a delta
/// secondary (it refuses wye loads, capacitors, charging and regulators there). [VERIFY] against Kersting's worked
/// examples once the book section is on disk; self-consistency is proved in Cf5OpenAndUnequalTransformerTests (the
/// equal-winding limit is the existing element bit for bit, the winding losses equal the equivalent's, power balances).
/// </summary>
public static class TransformerWindings
{
    /// <summary>The delta winding impedance on the line-to-neutral base is this multiple of its per-unit value on the
    /// bank's per-phase base: a winding across sqrt 3 times the line-to-neutral voltage, impedance base (sqrt 3)^2 = 3.</summary>
    public const double DeltaWindingBaseFactor = 3.0;

    /// <summary>
    /// The delta / delta element's series impedance from its three winding impedances (ab, bc, ca), per-unit as described
    /// on the class. All three bitwise equal: exactly <see cref="PhaseImpedance.Diagonal"/> of that value (the existing
    /// equal-winding element, bit for bit). Otherwise the W M construction with the circulating current. Refuses a
    /// winding that is not finite or has a negative resistance, a zero winding, and windings whose sum is zero.
    /// </summary>
    public static PhaseImpedance DeltaDeltaSeriesZ(IReadOnlyList<Complex> windings)
    {
        Complex[] z = CheckWindings(windings, 3);
        if (z[0] == z[1] && z[1] == z[2]) return PhaseImpedance.Diagonal(z[0]);
        // I_D = L I_abc, L = (I - 1 z^T / sum z) M; drop_LL = Zw_D I_D; Z = W Zw_D L.
        Complex[,] l = DeltaDeltaWindingMatrix(z);
        var d = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) d[i, j] = DeltaWindingBaseFactor * z[i] * l[i, j];
        return new PhaseImpedance(ApplyW(d));
    }

    /// <summary>
    /// The ungrounded wye / delta element's series impedance from its three delta winding impedances (ab, bc, ca), per-unit
    /// as described on the class. All three bitwise equal: exactly <see cref="PhaseImpedance.Diagonal"/> of that value.
    /// Otherwise the W (Zw_D - (1/3) 1 z^T) L construction (the floating primary neutral forces sum I_D = 0).
    /// </summary>
    public static PhaseImpedance WyeDeltaSeriesZ(IReadOnlyList<Complex> windings)
    {
        Complex[] z = CheckWindings(windings, 3);
        if (z[0] == z[1] && z[1] == z[2]) return PhaseImpedance.Diagonal(z[0]);
        Complex[,] l = WyeDeltaWindingMatrix();
        // drop_LL = 3 (Z_D - (1/3) 1 z^T) I_D, I_D = L I.
        var d = new Complex[3, 3];
        for (int j = 0; j < 3; j++)
        {
            Complex zDotL = z[0] * l[0, j] + z[1] * l[1, j] + z[2] * l[2, j];
            for (int i = 0; i < 3; i++)
                d[i, j] = DeltaWindingBaseFactor * (z[i] * l[i, j] - zDotL / 3.0);
        }
        return new PhaseImpedance(ApplyW(d));
    }

    /// <summary>
    /// The series impedance of an open-delta secondary (the open wye / open delta and open delta / open delta elements)
    /// from its two winding impedances: winding ab (the unit on the From side's phase A or A-B) and winding bc (phase B or
    /// B-C); the ca side is open. With Zw = 3 z: drop_ab = Zw_ab I_a, drop_bc = -Zw_bc I_c, drop_ca = -(drop_ab + drop_bc),
    /// and W gives Z = [[2 z_ab, 0, -z_bc], [-z_ab, 0, -z_bc], [-z_ab, 0, 2 z_bc]] (every entry exact: 3 z times W's
    /// thirds is a whole multiple of z).
    /// </summary>
    public static PhaseImpedance OpenDeltaSeriesZ(Complex zab, Complex zbc)
    {
        Complex[] z = CheckWindings(new[] { zab, zbc }, 2);
        Complex a = z[0], b = z[1];
        return new PhaseImpedance(new Complex[,]
        {
            { 2.0 * a, Complex.Zero, -b },
            { -a, Complex.Zero, -b },
            { -a, Complex.Zero, 2.0 * b },
        });
    }

    /// <summary>The delta / delta winding currents (ab, bc, ca) of the To-side line currents, including the circulating
    /// current that unequal windings carry. <paramref name="lineCurrents"/> must sum to zero (three-wire).</summary>
    public static Complex[] DeltaDeltaWindingCurrents(IReadOnlyList<Complex> windings, Complex[] lineCurrents)
        => Multiply(DeltaDeltaWindingMatrix(CheckWindings(windings, 3)), CheckLine(lineCurrents));

    /// <summary>The ungrounded wye / delta winding currents (ab, bc, ca): no circulating current (the floating primary
    /// neutral forces their sum to zero). <paramref name="lineCurrents"/> must sum to zero (three-wire).</summary>
    public static Complex[] WyeDeltaWindingCurrents(Complex[] lineCurrents)
        => Multiply(WyeDeltaWindingMatrix(), CheckLine(lineCurrents));

    /// <summary>The open-delta winding currents (ab, bc, ca = 0): I_ab = I_a, I_bc = -I_c.</summary>
    public static Complex[] OpenDeltaWindingCurrents(Complex[] lineCurrents)
    {
        Complex[] i = CheckLine(lineCurrents);
        return new[] { i[0], -i[2], Complex.Zero };
    }

    /// <summary>L = (I - 1 z^T / sum z) M for delta / delta, M I = (0, I_b, -I_a).</summary>
    private static Complex[,] DeltaDeltaWindingMatrix(Complex[] z)
    {
        Complex sum = z[0] + z[1] + z[2];
        if (sum == Complex.Zero) throw new ArgumentException("the three winding impedances sum to zero; the circulating current is undefined.", nameof(z));
        Complex[,] m = ParticularM();
        var l = new Complex[3, 3];
        for (int j = 0; j < 3; j++)
        {
            Complex zDotM = z[0] * m[0, j] + z[1] * m[1, j] + z[2] * m[2, j];
            for (int i = 0; i < 3; i++) l[i, j] = m[i, j] - zDotM / sum;
        }
        return l;
    }

    /// <summary>L = (I - J / 3) M for ungrounded wye / delta.</summary>
    private static Complex[,] WyeDeltaWindingMatrix()
    {
        Complex[,] m = ParticularM();
        var l = new Complex[3, 3];
        for (int j = 0; j < 3; j++)
        {
            Complex colSum = m[0, j] + m[1, j] + m[2, j];
            for (int i = 0; i < 3; i++) l[i, j] = m[i, j] - colSum / 3.0;
        }
        return l;
    }

    // The particular winding-current solution I_D0 = M I_abc with I_ab0 = 0: I_bc0 = I_b, I_ca0 = -I_a.
    private static Complex[,] ParticularM() => new Complex[,]
    {
        { Complex.Zero, Complex.Zero, Complex.Zero },
        { Complex.Zero, Complex.One, Complex.Zero },
        { -Complex.One, Complex.Zero, Complex.Zero },
    };

    // Kersting's W (line-to-line to the line-to-neutral of zero zero-sequence) applied on the left: (1/3) [[2,1,0],[0,2,1],[1,0,2]] d.
    private static Complex[,] ApplyW(Complex[,] d)
    {
        var r = new Complex[3, 3];
        for (int j = 0; j < 3; j++)
        {
            r[0, j] = (2.0 * d[0, j] + d[1, j]) / 3.0;
            r[1, j] = (2.0 * d[1, j] + d[2, j]) / 3.0;
            r[2, j] = (d[0, j] + 2.0 * d[2, j]) / 3.0;
        }
        return r;
    }

    private static Complex[] Multiply(Complex[,] a, Complex[] x)
    {
        var y = new Complex[3];
        for (int i = 0; i < 3; i++) y[i] = a[i, 0] * x[0] + a[i, 1] * x[1] + a[i, 2] * x[2];
        return y;
    }

    private static Complex[] CheckWindings(IReadOnlyList<Complex> windings, int count)
    {
        ArgumentNullException.ThrowIfNull(windings);
        if (windings.Count != count)
            throw new ArgumentException($"{count} winding impedances are needed (got {windings.Count}).", nameof(windings));
        var z = new Complex[count];
        for (int k = 0; k < count; k++)
        {
            Complex w = windings[k];
            if (!double.IsFinite(w.Real) || !double.IsFinite(w.Imaginary))
                throw new ArgumentException($"winding {k} impedance {w} is not finite.", nameof(windings));
            if (w.Real < 0) throw new ArgumentException($"winding {k} has a negative resistance ({w.Real}).", nameof(windings));
            if (w == Complex.Zero) throw new ArgumentException($"winding {k} has zero impedance; a transformer winding has leakage impedance.", nameof(windings));
            z[k] = w;
        }
        return z;
    }

    private static Complex[] CheckLine(Complex[] lineCurrents)
    {
        ArgumentNullException.ThrowIfNull(lineCurrents);
        if (lineCurrents.Length != 3) throw new ArgumentException($"three line currents are needed (got {lineCurrents.Length}).", nameof(lineCurrents));
        return lineCurrents;
    }
}
