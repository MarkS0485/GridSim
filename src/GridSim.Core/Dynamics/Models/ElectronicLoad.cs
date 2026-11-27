namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP20 (RC5): the CMLD electronic load (WECC MVWG, "WECC Composite Load Model Specification", January 2015, re-issued April
/// 2021, section "Electronic Load Model"; public on wecc.org). A constant power at power factor <see cref="PowerFactor"/>
/// (positive lagging), scaled by the connected fraction Fv1. Vmin, initially the starting voltage, tracks the lowest voltage
/// but not below Vd2. The specification's logic:
/// <code>
///   V &lt; Vd2:                    Fv1 = 0
///   Vd2 &lt;= V &lt; Vd1, V &lt;= Vmin:   Fv1 = (V - Vd2) / (Vd1 - Vd2)
///   Vd2 &lt;= V &lt; Vd1, V &gt; Vmin:    Fv1 = ((Vmin - Vd2) + Frcel (V - Vmin)) / (Vd1 - Vd2)
///   V &gt;= Vd1, Vmin &gt;= Vd1:       Fv1 = 1
///   V &gt;= Vd1, Vmin &lt; Vd1:        Fv1 = ((Vmin - Vd2) + Frcel (Vd1 - Vmin)) / (Vd1 - Vd2)
/// </code>
/// The specification's last line misplaces a parenthesis; it is read as the continuation of the line before it at V = Vd1.
/// </summary>
/// <param name="PowerFactor">PFel, in [-1, 0) or (0, 1], positive lagging.</param>
/// <param name="Vd1">The voltage below which the load decreases (pu).</param>
/// <param name="Vd2">The voltage below which it is 0 (pu), below <paramref name="Vd1"/>.</param>
/// <param name="RecoveryFraction">Frcel, the share of the tripped part that recovers.</param>
public sealed record ElectronicLoadParameters(double PowerFactor, double Vd1, double Vd2, double RecoveryFraction)
{
    internal void Check(string who)
    {
        if (!(PowerFactor >= -1.0 && PowerFactor <= 1.0 && PowerFactor != 0.0)) throw LoadChecks.OutOfRange(nameof(PowerFactor), PowerFactor, $"{who}: PFel must lie in [-1, 0) or (0, 1].");
        LibraryChecks.NonNegative(who, "Vd2", Vd2);
        if (!(Vd1 > Vd2) || !double.IsFinite(Vd1)) throw LoadChecks.OutOfRange(nameof(Vd1), Vd1, $"{who}: Vd1 must be above Vd2.");
        if (!(RecoveryFraction >= 0.0 && RecoveryFraction <= 1.0)) throw LoadChecks.OutOfRange(nameof(RecoveryFraction), RecoveryFraction, $"{who}: frcel must lie in [0, 1].");
    }

    /// <summary>tan(acos PFel), signed as the power factor.</summary>
    internal double TanPhi => Math.Sqrt(1.0 - PowerFactor * PowerFactor) / Math.Abs(PowerFactor) * Math.Sign(PowerFactor);

    /// <summary>Vmin after a step at <paramref name="v"/>: the lowest voltage, not below Vd2.</summary>
    internal double NextVmin(double vmin, double v) => Math.Max(Math.Min(vmin, v), Vd2);

    /// <summary>Fv1 at <paramref name="v"/>, with Vmin first updated by <paramref name="v"/> (the specification updates it
    /// before the fraction).</summary>
    internal double Fraction(double v, double vminHeld)
    {
        double vmin = NextVmin(vminHeld, v);
        double span = Vd1 - Vd2;
        if (v < Vd2) return 0.0;
        if (v < Vd1) return v <= vmin ? (v - Vd2) / span : ((vmin - Vd2) + RecoveryFraction * (v - vmin)) / span;
        return vmin >= Vd1 ? 1.0 : ((vmin - Vd2) + RecoveryFraction * (Vd1 - vmin)) / span;
    }
}
