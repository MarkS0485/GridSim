using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics.Planning;

/// <summary>
/// The voltage band of ENA EREC G5 Issue 5 (2020) Table 1: V &lt;= 0.4 kV, 0.4 &lt; V &lt;= 25 kV, 25 &lt; V &lt;= 66 kV,
/// 66 &lt; V &lt;= 230 kV, V &gt; 230 kV (nominal voltage at the PCC).
/// </summary>
public enum G55VoltageBand { Lv, Above0p4To25, Above25To66, Above66To230, Above230 }

/// <summary>
/// RC4 R4-21: the planning levels, aggregation exponents and worst-case source impedance of ENA Engineering Recommendation
/// G5 Issue 5 (2020; the "G5/5" of the UK connection process), transcribed from the published document (docs/reference S139;
/// the ENA publishes it free of charge). Every constant carries its table or equation number; the page numbers are the
/// document's own printed page numbers. The existing <see cref="HarmonicCompliance"/> (IEEE 519 and only the Table 1 headline
/// levels) is NOT edited: this class extends it in a new file and the tests pin the two to agree on Table 1.
///
/// <para>Interpretation notes (where the document's text is ambiguous): the rows labelled "&gt;= 25", "&gt;= 21" and "&gt;= 12" in
/// Tables 2 to 6 are read as "every order from that row on" (the table has no further rows); Table 22 (p.69) is read as
/// 0.4 kV: k = 1 for h &lt;= 7 and 0.5 above; 6.6 to 22 kV: k = 2 for h &lt;= 8 and 1 above.</para>
/// </summary>
public static class G55Limits
{
    /// <summary>G5 Issue 5 Table 1 (p.21) band of a nominal PCC voltage in kV. A non-finite or non-positive voltage is refused.</summary>
    public static G55VoltageBand Band(double kv)
    {
        if (!double.IsFinite(kv) || !(kv > 0.0))
            throw new ArgumentOutOfRangeException(nameof(kv), kv, "G5/5 limits need a finite nominal voltage above 0 kV.");
        return kv <= 0.4 ? G55VoltageBand.Lv
            : kv <= 25.0 ? G55VoltageBand.Above0p4To25
            : kv <= 66.0 ? G55VoltageBand.Above25To66
            : kv <= 230.0 ? G55VoltageBand.Above66To230
            : G55VoltageBand.Above230;
    }

    /// <summary>Table 1 (p.21): THDV planning level, % of the fundamental, by nominal voltage.</summary>
    public static double ThdPlanningLevelPct(double kv) => Band(kv) switch
    {
        G55VoltageBand.Lv => 5.0,
        G55VoltageBand.Above0p4To25 => 4.5,
        G55VoltageBand.Above25To66 => 3.7,
        _ => 3.0,   // 66 < V <= 230 kV and V > 230 kV
    };

    private static readonly int[] OddNonTriplenOrders = [5, 7, 11, 13, 17, 19, 23];
    private static readonly int[] OddTriplenOrders = [3, 9, 15];
    private static readonly int[] EvenOrders = [2, 4, 6, 8, 10];

    // Odd non-triplen 5, 7, 11, 13, 17, 19, 23 | odd triplen 3, 9, 15 (then 0.2 from 21) | even 2, 4, 6, 8, 10 (then 0.2 from 12).
    private sealed record Table(double[] OddNonTriplen, double[] OddTriplen, double TriplenFrom21, double[] Even, double EvenFrom12, bool Above25IsSimple);

    // Tables 2 to 6 (pp.22 to 24).
    private static readonly Table T2 = new([4.0, 4.0, 3.0, 2.5, 1.6, 1.5, 1.2], [4.0, 1.2, 0.5], 0.2, [1.6, 1.0, 0.5, 0.4, 0.4], 0.2, true);
    private static readonly Table T3 = new([3.0, 3.0, 2.0, 2.0, 1.6, 1.5, 1.2], [3.0, 1.2, 0.4], 0.2, [1.5, 1.0, 0.5, 0.4, 0.4], 0.2, true);
    private static readonly Table T4 = new([2.8, 2.8, 1.9, 1.8, 1.4, 1.3, 1.0], [2.6, 1.1, 0.3], 0.2, [1.3, 0.9, 0.5, 0.4, 0.4], 0.2, false);
    private static readonly Table T5 = new([2.5, 2.0, 1.8, 1.5, 1.2, 1.0, 0.8], [2.0, 1.0, 0.3], 0.2, [1.0, 0.8, 0.5, 0.4, 0.4], 0.2, false);
    private static readonly Table T6 = new([2.0, 2.0, 1.5, 1.5, 1.2, 1.0, 0.8], [1.5, 0.5, 0.3], 0.2, [1.0, 0.8, 0.5, 0.4, 0.4], 0.2, false);

    /// <summary>The table number (2 to 6) that gives the individual-harmonic planning levels for a band.</summary>
    public static int IndividualTableNumber(G55VoltageBand band) => band switch
    {
        G55VoltageBand.Lv => 2,
        G55VoltageBand.Above0p4To25 => 3,
        G55VoltageBand.Above25To66 => 4,
        G55VoltageBand.Above66To230 => 5,
        _ => 6,
    };

    /// <summary>
    /// Tables 2 to 6 (pp.22 to 24): the planning level for the harmonic voltage of order <paramref name="h"/> (2 or more, whole),
    /// % of the fundamental, at a nominal voltage. Orders at or above 25 (odd, not a multiple of 3) use 25/h in Tables 2 and 3 and
    /// 0.6 (25/h) + 0.2 in Tables 4 to 6.
    /// </summary>
    public static double PlanningLevelPct(double kv, int h)
    {
        if (h < 2) throw new ArgumentOutOfRangeException(nameof(h), h, "G5/5 gives planning levels for harmonic orders 2 and above.");
        Table t = Band(kv) switch
        {
            G55VoltageBand.Lv => T2,
            G55VoltageBand.Above0p4To25 => T3,
            G55VoltageBand.Above25To66 => T4,
            G55VoltageBand.Above66To230 => T5,
            _ => T6,
        };
        if (h % 2 == 0)
        {
            int e = Array.IndexOf(EvenOrders, h);
            return e >= 0 ? t.Even[e] : t.EvenFrom12;   // 12 and above
        }
        if (h % 3 == 0)
        {
            int e = Array.IndexOf(OddTriplenOrders, h);
            return e >= 0 ? t.OddTriplen[e] : t.TriplenFrom21;   // 21 and above
        }
        int o = Array.IndexOf(OddNonTriplenOrders, h);
        if (o >= 0) return t.OddNonTriplen[o];
        return t.Above25IsSimple ? 25.0 / h : 0.6 * (25.0 / h) + 0.2;   // 25 and above
    }

    /// <summary>Table 16 (p.36): the summation exponent for order <paramref name="h"/>: 1.0 for h &lt; 5, 1.4 for 5 &lt;= h &lt;= 10, 2.0 for h &gt; 10.</summary>
    public static double SummationExponent(int h) => h < 5 ? 1.0 : h <= 10 ? 1.4 : 2.0;

    /// <summary>
    /// Clause 6.3.3 equation (7), p.36: <c>V_h = (sum V_i^alpha)^(1/alpha)</c>. With <paramref name="diversityDeclared"/> false the
    /// document requires linear addition ("without which linear addition shall be employed"), so alpha = 1; true uses the
    /// Table 16 exponent of the order.
    /// </summary>
    public static double Aggregate(IEnumerable<double> values, int h, bool diversityDeclared)
        => AggregateWithExponent(values, diversityDeclared ? SummationExponent(h) : 1.0);

    /// <summary>The general summation law with an explicit exponent (alpha at least 1). Each value must be finite and not negative.</summary>
    public static double AggregateWithExponent(IEnumerable<double> values, double alpha)
    {
        if (!(alpha >= 1.0) || !double.IsFinite(alpha)) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "The summation exponent must be finite and at least 1.");
        double sum = 0.0;
        foreach (double v in values)
        {
            if (!(v >= 0.0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(values), v, "A summed distortion must be finite and not negative.");
            sum += alpha == 1.0 ? v : DetMath.Pow(v, alpha);
        }
        return alpha == 1.0 ? sum : DetMath.Pow(sum, 1.0 / alpha);
    }

    /// <summary>
    /// Headroom for order h, equations (51) and (52), p.82: <c>(V_PL^alpha - V_bg^alpha)^(1/alpha)</c>. A background at or above
    /// the planning level has no headroom (0), never a negative number.
    /// </summary>
    public static double Headroom(double planningLevelPct, double backgroundPct, double alpha)
    {
        if (!(alpha >= 1.0) || !double.IsFinite(alpha)) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "The summation exponent must be finite and at least 1.");
        if (!(planningLevelPct >= 0.0) || !(backgroundPct >= 0.0) || !double.IsFinite(planningLevelPct) || !double.IsFinite(backgroundPct))
            throw new ArgumentOutOfRangeException(nameof(backgroundPct), "Levels must be finite and not negative.");
        if (backgroundPct >= planningLevelPct) return 0.0;
        if (alpha == 1.0) return planningLevelPct - backgroundPct;
        return DetMath.Pow(DetMath.Pow(planningLevelPct, alpha) - DetMath.Pow(backgroundPct, alpha), 1.0 / alpha);
    }

    /// <summary>
    /// Table 22 (p.69): the worst-case reactance factor k of the assumed source-impedance curve, for a PCC of 0.4 kV
    /// (1 for h &lt;= 7, 0.5 above) or 6.6, 11, 20 or 22 kV (2 for h &lt;= 8, 1 above). Any other voltage has no k and is refused.
    /// </summary>
    public static double WorstCaseReactanceFactor(double kv, int h)
    {
        if (kv == 0.4) return h <= 7 ? 1.0 : 0.5;
        if (kv == 6.6 || kv == 11.0 || kv == 20.0 || kv == 22.0) return h <= 8 ? 2.0 : 1.0;
        throw new ArgumentOutOfRangeException(nameof(kv), kv, "Table 22 gives the worst-case reactance factor only for 0.4, 6.6, 11, 20 and 22 kV.");
    }

    /// <summary>
    /// Equations (30) and (31), pp.69 to 70: the Stage 2C worst-case harmonic impedance in ohms,
    /// <c>Z_h = sqrt((R1 sqrt(h))^2 + k^2 h^2 X1^2)</c>, from the three-phase short-circuit power (MVA), the phase-phase voltage
    /// (kV) and the system X/R ratio at 50 Hz: <c>Z1 = V^2/S</c>, <c>R1 = Z1/sqrt(1+(X/R)^2)</c>, <c>X1 = R1 (X/R)</c>.
    /// </summary>
    public static double WorstCaseImpedanceOhm(double sscMva, double vLineKv, double xOverR, double k, int h)
    {
        if (!(sscMva > 0.0) || !double.IsFinite(sscMva)) throw new ArgumentOutOfRangeException(nameof(sscMva), sscMva, "The short-circuit power must be finite and above 0 MVA.");
        if (!(vLineKv > 0.0) || !double.IsFinite(vLineKv)) throw new ArgumentOutOfRangeException(nameof(vLineKv), vLineKv, "The voltage must be finite and above 0 kV.");
        if (!(xOverR >= 0.0) || !double.IsFinite(xOverR)) throw new ArgumentOutOfRangeException(nameof(xOverR), xOverR, "X/R must be finite and not negative.");
        double z1 = vLineKv * vLineKv / sscMva;
        double r1 = z1 / Math.Sqrt(1.0 + xOverR * xOverR);
        double x1 = r1 * xOverR;
        double rh = r1 * Math.Sqrt(h);
        double xh = k * h * x1;
        return Math.Sqrt(rh * rh + xh * xh);
    }
}
