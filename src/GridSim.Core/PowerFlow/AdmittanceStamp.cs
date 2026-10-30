using System.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>The 2x2 pi-model admittance stamp of a branch: the four cells it contributes to Y.</summary>
/// <param name="Yff">From-from (diagonal at the from bus).</param>
/// <param name="Ytt">To-to (diagonal at the to bus).</param>
/// <param name="Yft">From-to (off-diagonal).</param>
/// <param name="Ytf">To-from (off-diagonal).</param>
public readonly record struct BranchStamp(Complex Yff, Complex Ytt, Complex Yft, Complex Ytf);

/// <summary>
/// The single source of the pi-model branch admittance stamp - series admittance + line charging with the
/// transformer tap/phase-shift, the exact 2x2 every builder of Y contributes. It was copy-pasted across
/// <see cref="YBus"/>, the fault-study Y, the physics audit, and the reciprocity check; centralising it here
/// removes the drift risk and, crucially, makes the stamp <b>frequency-parameterisable</b> so the harmonic
/// engine builds Y(h) from the same arithmetic.
///
/// <para>At <paramref name="h"/> = 1 and <paramref name="rFactor"/> = 1 this reproduces the fundamental stamp
/// bit-for-bit (multiplying by 1.0 is exact for doubles), so the existing power-flow / fault / audit results
/// are unchanged. For a harmonic order h, series reactance and line charging scale with h
/// (<c>X -&gt; hX</c>, <c>B -&gt; hB</c>); series resistance scales by a skin-effect factor
/// <paramref name="rFactor"/> the caller supplies.</para>
/// </summary>
public static class AdmittanceStamp
{
    /// <summary>The 2x2 stamp for a branch's pi-model at harmonic order <paramref name="h"/> (h=1 = fundamental).</summary>
    /// <param name="r">Series resistance (pu, fundamental).</param>
    /// <param name="x">Series reactance (pu, fundamental).</param>
    /// <param name="b">Total line charging susceptance (pu, fundamental) - half applied each end.</param>
    /// <param name="tapRatio">Transformer off-nominal tap (&lt;=0 treated as 1).</param>
    /// <param name="phaseShiftDeg">Transformer phase shift (degrees).</param>
    /// <param name="h">Harmonic order (frequency multiple of the fundamental). Default 1.</param>
    /// <param name="rFactor">Series-resistance multiplier (skin effect at order h). Default 1.</param>
    public static BranchStamp Branch(
        double r, double x, double b, double tapRatio, double phaseShiftDeg, double h = 1.0, double rFactor = 1.0)
    {
        Complex ys = Complex.One / new Complex(r * rFactor, x * h); // series admittance (reactance scales with h)
        Complex bc = new Complex(0, (b * h) / 2.0);                 // half line charging each end (scales with h)
        return FromAdmittance(ys, bc, tapRatio, phaseShiftDeg);
    }

    /// <summary>
    /// The 2x2 stamp from an already-computed series admittance <paramref name="ys"/> and per-end shunt
    /// <paramref name="bcHalf"/> (half the total line charging), with the transformer tap. This is the core
    /// the frequency-dependent builder also uses for the long-line-corrected series/shunt admittances.
    /// </summary>
    public static BranchStamp FromAdmittance(Complex ys, Complex bcHalf, double tapRatio, double phaseShiftDeg)
    {
        // A tap ratio of 0 is MATPOWER's "a line" and stamps as 1.0, as always. GR-480: a negative or NaN ratio is not a
        // tap; it used to be stamped as 1.0 silently.
        if (tapRatio < 0 || double.IsNaN(tapRatio))
            throw new ArgumentOutOfRangeException(nameof(tapRatio), tapRatio, "A transformer tap ratio must be 0 (a line) or above.");
        double a = tapRatio == 0 ? 1.0 : tapRatio;
        double shift = phaseShiftDeg * Math.PI / 180.0;
        Complex tap = DetMath.FromPolar(a, shift);

        Complex yff = (ys + bcHalf) / (a * a);
        Complex ytt = ys + bcHalf;
        Complex yft = -ys / Complex.Conjugate(tap);
        Complex ytf = -ys / tap;
        return new BranchStamp(yff, ytt, yft, ytf);
    }
}
