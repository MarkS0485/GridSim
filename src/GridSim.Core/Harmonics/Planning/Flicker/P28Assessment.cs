using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics.Planning.Flicker;

/// <summary>One planning-level row (P28 Table 2): the short-term and long-term flicker planning levels.</summary>
public sealed record P28PlanningLevels(double Pst, double Plt, string Row);

/// <summary>The rapid-voltage-change categories of P28 Table 4 (clause 5.3.2).</summary>
public enum P28RvcCategory { Frequent = 1, Infrequent = 2, VeryInfrequent = 3 }

/// <summary>One sample of a voltage-change characteristic: time since the start of the change (s) and the deviation from V0 in % of the nominal voltage (negative is a dip).</summary>
public sealed record P28Sample(double TimeS, double DeviationPct);

/// <summary>An assessment verdict with the clause it rests on.</summary>
public sealed record P28Verdict(string Outcome, string Clause, double? Value, double? Limit, string Note);

/// <summary>
/// RC4 R4-21: the flicker and rapid-voltage-change planning method of ENA EREC P28 Issue 2 (2019), built from the published text
/// (docs/reference S141, the ENA publishes it free of charge; page numbers are the document's own). Table 2 planning levels, Table 3
/// transfer coefficients, equation (1) summation, equation (2) Plt from Pst, equations (3) and (4) voltage change, Annex C equation
/// C.1 inrush voltage change, the Stage 1 conditions of 6.3.2, the Stage 2 limit of 0.5 and the cap of the simplified step rule
/// (Figure B.1.2 note 2), and the RVC envelopes of Table 4 and Figures 5 to 7.
///
/// <para>Not tabulated in the text and therefore not encoded: the curves of Figures B.1.1 and B.1.2 (Pst = 1 and Pst = 0.5 for
/// rectangular changes against repetition rate) and the shape-factor curves of Annex B.2 (they are figures; the document says they
/// are replicated from BS EN 61000-3-3 and PD IEC/TR 61000-3-7). A caller supplies curve points read from those figures; the code
/// decides without a curve only where the text itself states a point (3 % once per 475 s, B.1.2 note 2; 1 % not more than every 20 s,
/// footnote 46).</para>
/// </summary>
public static class P28Assessment
{
    /// <summary>Equation (1)'s default exponent: "An exponent of alpha = 3 should be used for summation of flicker unless there is information/justification to support the application of another exponent" (Table 8, p.50).</summary>
    public const double DefaultExponent = 3.0;

    /// <summary>Table 2 (p.24): LV 1.0 / 0.8; 3.3, 6.6, 11, 20, 33 kV 0.9 / 0.7; 66 to 400 kV 0.8 / 0.6. LV is read as 1 kV and below, and the middle row as above 1 kV up to 33 kV.</summary>
    public static P28PlanningLevels PlanningLevels(double nominalKv)
    {
        if (!(nominalKv > 0.0) || !double.IsFinite(nominalKv)) throw new ArgumentOutOfRangeException(nameof(nominalKv), nominalKv, "The nominal voltage must be finite and above 0 kV.");
        if (nominalKv <= 1.0) return new P28PlanningLevels(1.0, 0.8, "LV");
        if (nominalKv <= 33.0) return new P28PlanningLevels(0.9, 0.7, "3.3 kV to 33 kV");
        return new P28PlanningLevels(0.8, 0.6, "66 kV to 400 kV");
    }

    /// <summary>Table 1 (p.23): the compatibility levels for LV supply systems, Pst 1.0 and Plt 0.8.</summary>
    public static (double Pst, double Plt) LvCompatibilityLevels => (1.0, 0.8);

    private static int Level(double kv) => kv <= 1.0 ? 0 : kv <= 20.0 ? 1 : kv <= 33.0 ? 2 : kv <= 66.0 ? 3 : kv <= 132.0 ? 4 : 5;

    /// <summary>
    /// Table 3 (pp.24 to 25), the typical transfer coefficient of flicker (Pst and Plt alike) from a higher voltage level to a lower:
    /// 400/275 kV to 132/110, 66, 33/22, 20/11/6.6 kV: 0.85, 0.85, 0.80, 0.70; 132/110 kV to 66, 33/22, 20/11/6.6: 0.95, 0.90, 0.75; 66 kV to
    /// 33/22 and 20/11/6.6: 0.95, 0.90; 33/22 kV to 20/11/6.6: 0.90; 11 kV to LV: 1.0. The same level is 1.0. Flicker is NOT transferred
    /// from a lower to a higher voltage (clause 5.2.2, p.25): 0.0. A pair the table does not list (for example 33 kV to LV) returns null;
    /// the caller supplies a measured or modelled value.
    /// </summary>
    public static double? TransferCoefficient(double fromKv, double toKv)
    {
        int f = Level(fromKv), t = Level(toKv);
        if (f == t) return 1.0;
        if (t > f) return 0.0;
        // Levels: 0 LV, 1 20/11/6.6 (up to 20 kV), 2 33/22, 3 66, 4 132/110, 5 400/275 and 220/200/150 (above 132 kV).
        return (f, t) switch
        {
            (5, 4) => 0.85,
            (5, 3) => 0.85,
            (5, 2) => 0.80,
            (5, 1) => 0.70,
            (4, 3) => 0.95,
            (4, 2) => 0.90,
            (4, 1) => 0.75,
            (3, 2) => 0.95,
            (3, 1) => 0.90,
            (2, 1) => 0.90,
            (1, 0) => 1.0,
            _ => null,
        };
    }

    /// <summary>Equation (1), p.48, the general summation law: <c>Pst = (sum Pst_i^alpha)^(1/alpha)</c>; also for Plt. The default exponent is 3.</summary>
    public static double Summation(IEnumerable<double> values, double alpha = DefaultExponent) => G55Limits.AggregateWithExponent(values, alpha);

    /// <summary>Equation (2), p.49: <c>Plt = ((1/n) sum Pst_i^3)^(1/3)</c> over n Pst values (n = 12 for the usual 2 h).</summary>
    public static double PltFromPst(IReadOnlyList<double> pst)
    {
        if (pst is null || pst.Count == 0) throw new ArgumentException("Plt needs at least one Pst value.", nameof(pst));
        double sum = 0.0;
        foreach (double p in pst)
        {
            if (!(p >= 0.0) || !double.IsFinite(p)) throw new ArgumentOutOfRangeException(nameof(pst), p, "A Pst must be finite and not negative.");
            sum += p * p * p;
        }
        return DetMath.Pow(sum / pst.Count, 1.0 / 3.0);
    }

    /// <summary>Equation (4), p.51: the voltage change in percent for an apparent power change <paramref name="deltaSMva"/> on a supply of short-circuit power <paramref name="sscMva"/> (the load's power factor taken as the supply's R/Z: the worst case, footnote 33).</summary>
    public static double VoltageChangePct(double deltaSMva, double sscMva)
    {
        if (!double.IsFinite(deltaSMva)) throw new ArgumentOutOfRangeException(nameof(deltaSMva), deltaSMva, "The power change must be finite.");
        if (!(sscMva > 0.0) || !double.IsFinite(sscMva)) throw new ArgumentOutOfRangeException(nameof(sscMva), sscMva, "The short-circuit power must be finite and above 0 MVA.");
        return deltaSMva / sscMva * 100.0;
    }

    /// <summary>Equation (3), p.50: <c>dV/V = (S / S_base)(cos(phi) R_pu + sin(phi) X_pu)</c> in per unit, with the supply impedance per unit on <paramref name="sBaseMva"/>. A lagging power factor has sin(phi) positive.</summary>
    public static double VoltageChangePu(double deltaSMva, double sBaseMva, double cosPhi, double sinPhi, double rPu, double xPu)
    {
        if (!(sBaseMva > 0.0) || !double.IsFinite(sBaseMva)) throw new ArgumentOutOfRangeException(nameof(sBaseMva), sBaseMva, "The base must be finite and above 0 MVA.");
        return deltaSMva / sBaseMva * (cosPhi * rPu + sinPhi * xPu);
    }

    /// <summary>Annex C equation C.1, p.80: <c>%dV = m k (S / S_sc) 100</c>, m the ratio of peak inrush to peak rated current, k the factor converting the peak inrush to r.m.s., S the rated power of the transformer or motor, S_sc the supply short-circuit power.</summary>
    public static double InrushVoltageChangePct(double m, double k, double ratedMva, double sscMva)
    {
        if (!(m > 0.0) || !double.IsFinite(m)) throw new ArgumentOutOfRangeException(nameof(m), m, "m must be finite and above 0.");
        if (!(k > 0.0) || !double.IsFinite(k)) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be finite and above 0.");
        if (!(ratedMva > 0.0) || !double.IsFinite(ratedMva)) throw new ArgumentOutOfRangeException(nameof(ratedMva), ratedMva, "The rating must be finite and above 0 MVA.");
        if (!(sscMva > 0.0) || !double.IsFinite(sscMva)) throw new ArgumentOutOfRangeException(nameof(sscMva), sscMva, "The short-circuit power must be finite and above 0 MVA.");
        return m * k * ratedMva / sscMva * 100.0;
    }

    /// <summary>
    /// Clause 6.3.3.1 (Stage 2, p.45): disturbing equipment whose Pst at the PCC is at most 0.5 under the worst-case normal operating
    /// condition can be connected without further detailed assessment; otherwise it goes to Stage 3.
    /// </summary>
    public static P28Verdict Stage2(double pstAtPcc)
    {
        if (!(pstAtPcc >= 0.0) || !double.IsFinite(pstAtPcc)) throw new ArgumentOutOfRangeException(nameof(pstAtPcc), pstAtPcc, "Pst must be finite and not negative.");
        return pstAtPcc <= 0.5
            ? new P28Verdict(G55Outcome.Pass, "6.3.3.1", pstAtPcc, 0.5, "Pst at or below 0.5: connect without further detailed assessment.")
            : new P28Verdict("STAGE 3", "6.3.3.1", pstAtPcc, 0.5, "Pst above 0.5: Stage 3 assessment (clause 6.3.4).");
    }

    /// <summary>
    /// The simplified step-change rule of clause 6.3.3.2 against the Pst = 0.5 curve of Figure B.1.2. With <paramref name="curvePoints"/> (interval in
    /// seconds, largest allowed step in %, read from the figure) the limit is interpolated linearly in log-log between points (held flat
    /// beyond the ends, and capped at 3 % by note 2). Without a curve the rule decides only at the text's own points: 3 % once per 475 s
    /// (note 2) and 1 % not more than every 20 s (footnote 46): a step of at most 1 % at least 20 s apart, or of at most 3 % at least
    /// 475 s apart, is within the curve; anything else is INCOMPLETE (a curve or a flickermeter result is needed).
    /// </summary>
    public static P28Verdict SimplifiedStep(double stepPct, double intervalSeconds, IReadOnlyList<(double IntervalS, double StepPct)>? curvePoints = null)
    {
        if (!(stepPct >= 0.0) || !double.IsFinite(stepPct)) throw new ArgumentOutOfRangeException(nameof(stepPct), stepPct, "The step must be finite and not negative.");
        if (!(intervalSeconds > 0.0) || !double.IsFinite(intervalSeconds)) throw new ArgumentOutOfRangeException(nameof(intervalSeconds), intervalSeconds, "The interval must be finite and above 0 s.");
        if (curvePoints is { Count: > 0 })
        {
            var pts = curvePoints.OrderBy(p => p.IntervalS).ToArray();
            for (int i = 0; i < pts.Length; i++)
                if (!(pts[i].IntervalS > 0.0) || !(pts[i].StepPct > 0.0) || (i > 0 && pts[i].IntervalS == pts[i - 1].IntervalS))
                    throw new ArgumentException("Curve points need distinct positive intervals and positive steps.", nameof(curvePoints));
            double allowed;
            if (intervalSeconds <= pts[0].IntervalS) allowed = pts[0].StepPct;
            else if (intervalSeconds >= pts[^1].IntervalS) allowed = pts[^1].StepPct;
            else
            {
                int j = 1;
                while (pts[j].IntervalS < intervalSeconds) j++;
                double t = (DetMath.Log(intervalSeconds) - DetMath.Log(pts[j - 1].IntervalS)) / (DetMath.Log(pts[j].IntervalS) - DetMath.Log(pts[j - 1].IntervalS));
                allowed = DetMath.Exp(DetMath.Log(pts[j - 1].StepPct) + t * (DetMath.Log(pts[j].StepPct) - DetMath.Log(pts[j - 1].StepPct)));
            }
            allowed = Math.Min(allowed, 3.0);
            return new P28Verdict(stepPct <= allowed ? G55Outcome.Pass : "STAGE 3", "6.3.3.2 / Figure B.1.2", stepPct, allowed,
                "Against the supplied Pst = 0.5 curve points (log-log interpolation), capped at 3 %.");
        }
        if ((stepPct <= 1.0 && intervalSeconds >= 20.0) || (stepPct <= 3.0 && intervalSeconds >= 475.0))
            return new P28Verdict(G55Outcome.Pass, "6.3.3.2 / Figure B.1.2 notes", stepPct, null,
                "Within the Pst = 0.5 curve at a point the text states (3 % once per 475 s; 1 % not more than every 20 s).");
        return new P28Verdict(G55Outcome.Incomplete, "6.3.3.2 / Figure B.1.2", stepPct, null,
            "The Pst = 0.5 curve (Figure B.1.2) is a figure, not tabulated in the text: supply curve points, or assess by flickermeter simulation (6.3.3.1).");
    }

    /// <summary>
    /// Stage 1 for LV connections (clause 6.3.2, pp.43 to 44). Equipment up to 16 A per phase conforming to BS EN 61000-3-3 connects
    /// without assessment if the supply impedance is within the typical maximum (single-phase loop 0.4 + j0.25 ohm, |Z| 0.472; three-phase
    /// 0.24 + j0.15, |Z| 0.283); above 16 A and up to 75 A it does so if it conforms to 61000-3-3 and the service capacity is confirmed
    /// adequate; equipment not conforming to 61000-3-3 but to BS EN 61000-3-11 connects if the supply impedance is at most its declared
    /// Z_max, or (service capacity at least 100 A) at most 0.35 ohm single-phase / 0.212 ohm three-phase. Anything else goes to Stage 2.
    /// </summary>
    public static P28Verdict Stage1Lv(double ratedCurrentA, bool threePhase, bool conformsEn61000_3_3, bool conformsEn61000_3_11,
        double supplyImpedanceOhm, double? equipmentZmaxOhm, bool serviceCapacityConfirmedAdequate, bool serviceCapacityAtLeast100A)
    {
        if (!(ratedCurrentA > 0.0) || !double.IsFinite(ratedCurrentA)) throw new ArgumentOutOfRangeException(nameof(ratedCurrentA), ratedCurrentA, "The rated current must be finite and above 0 A.");
        if (!(supplyImpedanceOhm > 0.0) || !double.IsFinite(supplyImpedanceOhm)) throw new ArgumentOutOfRangeException(nameof(supplyImpedanceOhm), supplyImpedanceOhm, "The supply impedance must be finite and above 0 ohm.");
        double typical = threePhase ? 0.283 : 0.472;
        double alt = threePhase ? 0.212 : 0.35;
        if (ratedCurrentA <= 16.0 && conformsEn61000_3_3 && supplyImpedanceOhm <= typical)
            return new P28Verdict(G55Outcome.Pass, "6.3.2.1", supplyImpedanceOhm, typical, "Up to 16 A per phase, conforms to BS EN 61000-3-3, supply impedance within the typical maximum.");
        if (ratedCurrentA <= 75.0 && ratedCurrentA > 16.0 && conformsEn61000_3_3 && serviceCapacityConfirmedAdequate)
            return new P28Verdict(G55Outcome.Pass, "6.3.2.2", null, null, "Above 16 A and up to 75 A, conforms to BS EN 61000-3-3, service capacity confirmed adequate.");
        if (ratedCurrentA <= 75.0 && conformsEn61000_3_11)
        {
            if (equipmentZmaxOhm is double zmax && supplyImpedanceOhm <= zmax)
                return new P28Verdict(G55Outcome.Pass, "6.3.2.2 a)", supplyImpedanceOhm, zmax, "Conditional connection: conforms to BS EN 61000-3-11 and the supply impedance is at most the declared Z_max.");
            if (serviceCapacityAtLeast100A && supplyImpedanceOhm <= alt)
                return new P28Verdict(G55Outcome.Pass, "6.3.2.2 b)", supplyImpedanceOhm, alt, "Conditional connection: service capacity at least 100 A per phase and the measured supply impedance is within 0.35 / 0.212 ohm.");
        }
        return new P28Verdict("STAGE 2", "6.3.2", null, null, "The Stage 1 conditions are not met: Stage 2 assessment (clause 6.3.3).");
    }

    /// <summary>
    /// The planning-level check of clauses 6.3.4 and 5.2.2: the summated Pst (equation 1) of the new equipment, known future connections
    /// and the background must not exceed the Pst planning level of the voltage (Table 2); the same for Plt. The exponent is 3 unless
    /// justified otherwise.
    /// </summary>
    public static (P28Verdict Pst, P28Verdict Plt, double PstSummated, double PltSummated) PlanningCheck(
        double nominalKv, IReadOnlyList<double> pstSources, IReadOnlyList<double> pltSources, double alpha = DefaultExponent)
    {
        P28PlanningLevels pl = PlanningLevels(nominalKv);
        double pst = Summation(pstSources, alpha);
        double plt = Summation(pltSources, alpha);
        var vPst = new P28Verdict(pst <= pl.Pst ? G55Outcome.Pass : G55Outcome.Fail, "6.3.4 / Table 2", pst, pl.Pst, $"Pst summated by equation (1) with exponent {alpha:R}.");
        var vPlt = new P28Verdict(plt <= pl.Plt ? G55Outcome.Pass : G55Outcome.Fail, "6.3.4 / Table 2", plt, pl.Plt, $"Plt summated by equation (1) with exponent {alpha:R}.");
        return (vPst, vPlt, pst, plt);
    }

    /// <summary>
    /// The envelope of Table 4 and Figures 5 to 7 (pp.26 to 29): the upper and lower limit, in % of the nominal voltage about V0, at a time
    /// since the start of the change. Category 1 (frequent): +/-6 % to 100 ms then +/-3 %. Category 2 (infrequent): -10 % to 100 ms, -6 % to
    /// 2 s, then -3 %; +6 % to 0.8 s then +3 %. Category 3 (very infrequent): -12 % to 100 ms, -10 % to 2 s, then -3 %; +6 % to 0.8 s then +3 %.
    /// </summary>
    public static (double LowerPct, double UpperPct) Envelope(P28RvcCategory category, double timeS)
    {
        if (!(timeS >= 0.0) || !double.IsFinite(timeS)) throw new ArgumentOutOfRangeException(nameof(timeS), timeS, "The time must be finite and not negative.");
        switch (category)
        {
            case P28RvcCategory.Frequent:
                return timeS <= 0.1 ? (-6.0, 6.0) : (-3.0, 3.0);
            case P28RvcCategory.Infrequent:
                return (timeS <= 0.1 ? -10.0 : timeS <= 2.0 ? -6.0 : -3.0, timeS <= 0.8 ? 6.0 : 3.0);
            case P28RvcCategory.VeryInfrequent:
                return (timeS <= 0.1 ? -12.0 : timeS <= 2.0 ? -10.0 : -3.0, timeS <= 0.8 ? 6.0 : 3.0);
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "The category is 1, 2 or 3.");
        }
    }

    /// <summary>The maximum number of occurrences of each category (Table 4, notes 1 and 2): category 2 is 4 events in 1 calendar month, category 3 is 1 event in 3 calendar months; category 1 is the flicker-governed frequent case.</summary>
    public static string OccurrenceLimit(P28RvcCategory category) => category switch
    {
        P28RvcCategory.Frequent => "Frequent: any single or repetitive change inside the Figure 5 envelope, and within the flicker planning levels",
        P28RvcCategory.Infrequent => "Infrequent: at most 4 events in 1 calendar month (no more than 1 event a day of up to 4 changes at least 10 minutes apart within 2 hours)",
        _ => "Very infrequent: 1 event in 3 calendar months",
    };

    /// <summary>
    /// The RVC check of clause 5.3.2: every sample of the voltage-change characteristic inside the envelope of the category, and the steady-state
    /// change within the general 3 % step limit (clause 5.4, Table 4). Samples before the start of the change (negative time) are refused.
    /// </summary>
    public static P28Verdict RapidVoltageChange(P28RvcCategory category, IReadOnlyList<P28Sample> samples, double steadyStateChangePct)
    {
        if (samples is null || samples.Count == 0) throw new ArgumentException("An RVC check needs at least one sample.", nameof(samples));
        if (!double.IsFinite(steadyStateChangePct)) throw new ArgumentOutOfRangeException(nameof(steadyStateChangePct), steadyStateChangePct, "The steady-state change must be finite.");
        double worst = 0.0;
        double worstMargin = double.PositiveInfinity;
        foreach (P28Sample s in samples)
        {
            if (!double.IsFinite(s.DeviationPct)) throw new ArgumentException("A sample deviation must be finite.", nameof(samples));
            (double lo, double hi) = Envelope(category, s.TimeS);
            double margin = s.DeviationPct >= 0 ? hi - s.DeviationPct : s.DeviationPct - lo;   // positive when inside
            if (margin < worstMargin) { worstMargin = margin; worst = s.DeviationPct; }
        }
        if (Math.Abs(steadyStateChangePct) > 3.0)
            return new P28Verdict(G55Outcome.Fail, "5.4 / Table 4", Math.Abs(steadyStateChangePct), 3.0, "The steady-state voltage change exceeds the 3 % step limit.");
        return worstMargin >= 0.0
            ? new P28Verdict(G55Outcome.Pass, "5.3.2 / Figures 5 to 7", worst, null, "Inside the envelope at every sample; the occurrence limit is: " + OccurrenceLimit(category))
            : new P28Verdict(G55Outcome.Fail, "5.3.2 / Figures 5 to 7", worst, null, "Outside the category's envelope.");
    }
}
