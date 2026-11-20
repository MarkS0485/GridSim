using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics.Planning;

/// <summary>The converter technologies of ENA EREC G5 Issue 5 Stage 1C / 1D / 2A / 2B (clauses 7.4, 8.2).</summary>
public enum G55Technology { SixPulse, ActiveFrontEnd, TwelvePulse, SinglePhaseRectifier }

/// <summary>One item of plant or equipment: an identifier, its technology and its rating in kVA.</summary>
public sealed record G55Equipment(string Id, G55Technology Technology, double RatingKva);

/// <summary>The outcome words of a G5/5 substage, in the vocabulary of the RC3 / RC4 study verbs.</summary>
public static class G55Outcome
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string NotApplicable = "NOT APPLICABLE";
    public const string NotEvaluated = "NOT EVALUATED";
    public const string Incomplete = "INCOMPLETE";
}

/// <summary>One substage verdict: the stage name ("1C-1"), the clause, the outcome, the compared values and the reason.</summary>
public sealed record G55Substage(string Stage, string Clause, string Outcome, double? Required, double? Permitted, string Unit, string Note);

/// <summary>One harmonic order of a Stage 2C or Stage 3 verdict (all values % of the fundamental).</summary>
public sealed record G55OrderRow(int Order, double PlanningLevelPct, double BackgroundPct, double IncrementalPct, double PredictedPct, double AlphaUsed, bool Within);

/// <summary>The Stage 2C result for one connection.</summary>
public sealed record G55Stage2CResult(
    string Outcome,
    double ThdPlanningLevelPct,
    double ThdPredictedPct,
    double ThdIncrementalPct,
    double ThdCurrentPct,
    bool BackgroundAssumed,
    IReadOnlyList<G55OrderRow> Orders,
    IReadOnlyList<string> Missing,
    string Note);

/// <summary>The Stage 2C background: measured per order (% of the fundamental), or the permitted 75 % of the planning level assumption.</summary>
public sealed record G55Background(IReadOnlyDictionary<int, double>? MeasuredPct, bool Assume75PercentOfPlanningLevel)
{
    /// <summary>Clause 8.4.1: with the NO's agreement the background may be taken as 75 % of the planning level.</summary>
    public const double AssumedFraction = 0.75;

    public static G55Background Assumed { get; } = new(null, true);

    public static G55Background Measured(IReadOnlyDictionary<int, double> pct) => new(pct, false);
}

/// <summary>
/// RC4 R4-21: the staged connection assessment of ENA EREC G5 Issue 5 (2020), clauses 6 to 9, built from the published text
/// (docs/reference S139). Stage 1 (LV: 1A product-standard compliance, 1C and 1D aggregate rated power or minimum short-circuit
/// power), Stage 2 (2A, 2B as 1C and 1D at 60 MVA for 6.6 to 22 kV, and 2C the prediction against the planning levels with the
/// worst-case source impedance), and the resonant-plant test of clause 9. Stage 3 (clause 10) is <see cref="G55Stage3"/>.
///
/// <para>Not evaluated, and said so in the result: Stage 1B (the IEC 61000-3-12 short-circuit-ratio rule of Table 18 and Annex A,
/// 7.3: skipping it can only send a connection on to Stage 1C, never pass it wrongly). The product-standard compliance of Stage
/// 1A is a declaration by the new user that this code cannot check.</para>
/// </summary>
public static class G55StagedAssessment
{
    // Table 19 (p.50), Table 21 (p.61): maximum permitted aggregate equipment rated power at the reference short-circuit power, kVA.
    // Each is cross-checked in the tests against the minimum short-circuit constants of equations (12) and (23), which are an
    // independent statement of the same limit (reference MVA / constant).
    public const double Table19SixPulseKva = 22.0;
    public const double Table19ActiveFrontEndKva = 192.0;
    public const double Table19TwelvePulseKva = 77.0;
    public const double Table19SinglePhaseKva = 7.9;
    public const double Table19ThreePhaseReferenceMva = 10.0;
    public const double Table19SinglePhaseReferenceMva = 2.0;
    public const double Table21SixPulseKva = 76.0;
    public const double Table21ActiveFrontEndKva = 673.0;
    public const double Table21TwelvePulseKva = 287.0;
    public const double Table21ReferenceMva = 60.0;

    /// <summary>Equation (12), p.52: minimum short-circuit power (kVA) per kVA of six-pulse and of active-front-end plant (Stage 1C-2).</summary>
    public const double Eq12SixPulse = 459.977, Eq12ActiveFrontEnd = 52.170;
    /// <summary>Equation (23), p.62: the same at Stage 2A-2.</summary>
    public const double Eq23SixPulse = 785.962, Eq23ActiveFrontEnd = 89.143;
    /// <summary>Equation (28), p.66: the Stage 2B-2 numerators (divided by the 5th harmonic headroom, %).</summary>
    public const double Eq28SixPulse = 589.472, Eq28ActiveFrontEnd = 66.857;

    private static double Table19(G55Technology t) => t switch
    {
        G55Technology.SixPulse => Table19SixPulseKva,
        G55Technology.ActiveFrontEnd => Table19ActiveFrontEndKva,
        G55Technology.TwelvePulse => Table19TwelvePulseKva,
        _ => Table19SinglePhaseKva,
    };

    private static double Table21(G55Technology t) => t switch
    {
        G55Technology.SixPulse => Table21SixPulseKva,
        G55Technology.ActiveFrontEnd => Table21ActiveFrontEndKva,
        _ => Table21TwelvePulseKva,
    };

    private static double SumKva(IEnumerable<G55Equipment> e, G55Technology t) => e.Where(x => x.Technology == t).Sum(x => x.RatingKva);

    private static bool Single(IReadOnlyList<G55Equipment> e, out G55Technology tech)
    {
        G55Technology first = e.Count > 0 ? e[0].Technology : G55Technology.SixPulse;
        tech = first;
        return e.Count > 0 && e.All(x => x.Technology == first);
    }

    private static bool SixAndAfeOnly(IReadOnlyList<G55Equipment> e)
        => e.Any(x => x.Technology == G55Technology.SixPulse) && e.Any(x => x.Technology == G55Technology.ActiveFrontEnd)
           && e.All(x => x.Technology is G55Technology.SixPulse or G55Technology.ActiveFrontEnd);

    private static void RequireEquipment(IReadOnlyList<G55Equipment> e)
    {
        if (e is null || e.Count == 0) throw new ArgumentException("A G5/5 assessment needs at least one item of equipment.", nameof(e));
        foreach (G55Equipment x in e)
            if (!(x.RatingKva > 0.0) || !double.IsFinite(x.RatingKva))
                throw new ArgumentException($"Equipment '{x.Id}': the rating must be finite and above 0 kVA; got {x.RatingKva:R}.", nameof(e));
    }

    private static void RequireSsc(double sscMva)
    {
        if (!(sscMva > 0.0) || !double.IsFinite(sscMva)) throw new ArgumentOutOfRangeException(nameof(sscMva), sscMva, "The short-circuit power must be finite and above 0 MVA.");
    }

    /// <summary>Stage 1A (clause 7.2.1): equipment rated up to 16 A per phase that is declared compliant with IEC 61000-3-2 connects without further assessment. The declaration is the user's; it is not checked.</summary>
    public static G55Substage Stage1A(bool declaredCompliantWithIec61000_3_2) => declaredCompliantWithIec61000_3_2
        ? new G55Substage("1A", "7.2.1", G55Outcome.Pass, null, null, "", "Declared compliant with IEC 61000-3-2 (up to 16 A per phase); connects without further assessment. The declaration is not checked by GridSim.")
        : new G55Substage("1A", "7.2.1", G55Outcome.Fail, null, null, "", "Compliance with IEC 61000-3-2 not declared; the assessment advances to Stage 1B.");

    /// <summary>The Stage 1B placeholder: not evaluated (see the class remarks).</summary>
    public static G55Substage Stage1B() => new("1B", "7.3", G55Outcome.NotEvaluated, null, null, "",
        "The IEC 61000-3-12 short-circuit-ratio rule (Table 18, equations 8 and 9, Annex A) is not implemented; the assessment goes on to Stage 1C, which can only be more conservative.");

    /// <summary>
    /// Stage 1C (clause 7.4) at a PCC of 0.4 kV or below: 1C-1 for one technology (equation 10, Table 19, scaling equation 11, compare
    /// equation 10 with 11), 1C-2 for a mix of six-pulse and active-front-end (equation 12, compare the short-circuit power). Any other
    /// mix is NOT APPLICABLE and goes to Stage 2C. <paramref name="sscMva"/> is three-phase, or single-phase for single-phase rectifiers.
    /// </summary>
    public static G55Substage Stage1C(IReadOnlyList<G55Equipment> equipment, double sscMva)
    {
        RequireEquipment(equipment);
        RequireSsc(sscMva);
        if (Single(equipment, out G55Technology tech))
        {
            double s = equipment.Sum(x => x.RatingKva);
            double reference = tech == G55Technology.SinglePhaseRectifier ? Table19SinglePhaseReferenceMva : Table19ThreePhaseReferenceMva;
            double permitted = sscMva / reference * Table19(tech);
            return new G55Substage("1C-1", "7.4.1", s <= permitted ? G55Outcome.Pass : G55Outcome.Fail, s, permitted, "kVA",
                $"Sequ {s:R} kVA against Table 19 {Table19(tech):R} kVA at {reference:R} MVA scaled by equation (11) to {sscMva:R} MVA.");
        }
        if (SixAndAfeOnly(equipment))
        {
            double min = Eq12SixPulse * SumKva(equipment, G55Technology.SixPulse) + Eq12ActiveFrontEnd * SumKva(equipment, G55Technology.ActiveFrontEnd);
            double have = sscMva * 1000.0;
            return new G55Substage("1C-2", "7.4.2", have >= min ? G55Outcome.Pass : G55Outcome.Fail, min, have, "kVA",
                $"Minimum short-circuit power by equation (12) {min:R} kVA against the PCC's {have:R} kVA.");
        }
        return new G55Substage("1C", "7.4", G55Outcome.NotApplicable, null, null, "",
            "Stage 1C covers one technology, or a mix of six-pulse and active-front-end only; this mix goes to Stage 2C.");
    }

    /// <summary>
    /// Stage 1D (clause 7.5): Stage 1C with the measured background at the limiting order. 1D-1 (equations 13 to 18): order 5 for six-pulse
    /// and active-front-end, 37 for twelve-pulse, 21 for single-phase rectifiers; 1D-2 (equations 19 and 20): order 5. The planning level is
    /// the LV one (Table 2). <paramref name="backgroundPct"/> maps order to the measured level; a missing limiting order is INCOMPLETE.
    /// </summary>
    public static G55Substage Stage1D(IReadOnlyList<G55Equipment> equipment, double sscMva, IReadOnlyDictionary<int, double>? backgroundPct)
    {
        RequireEquipment(equipment);
        RequireSsc(sscMva);
        if (Single(equipment, out G55Technology tech))
        {
            int h = tech switch { G55Technology.TwelvePulse => 37, G55Technology.SinglePhaseRectifier => 21, _ => 5 };
            if (backgroundPct is null || !backgroundPct.TryGetValue(h, out double bg))
                return new G55Substage("1D-1", "7.5.1", G55Outcome.Incomplete, null, null, "kVA", $"The measured background at the limiting order {h} is needed (clause 6.3.5).");
            double pl = G55Limits.PlanningLevelPct(0.4, h);
            double headroom = Math.Max(pl - bg, 0.0);
            double reference = tech == G55Technology.SinglePhaseRectifier ? Table19SinglePhaseReferenceMva : Table19ThreePhaseReferenceMva;
            double permitted = sscMva / reference * (headroom / (0.25 * pl)) * Table19(tech);
            double s = equipment.Sum(x => x.RatingKva);
            return new G55Substage("1D-1", "7.5.1", s <= permitted ? G55Outcome.Pass : G55Outcome.Fail, s, permitted, "kVA",
                $"Sequ {s:R} kVA against {permitted:R} kVA (equations 13, 15 or 17: V{h} headroom {headroom:R} % of {pl:R} % planning level).");
        }
        if (SixAndAfeOnly(equipment))
        {
            if (backgroundPct is null || !backgroundPct.TryGetValue(5, out double bg5))
                return new G55Substage("1D-2", "7.5.2", G55Outcome.Incomplete, null, null, "kVA", "The measured background at the 5th harmonic is needed (clause 6.3.5).");
            double headroom = Math.Max(G55Limits.PlanningLevelPct(0.4, 5) - bg5, 0.0);
            double num = Eq12SixPulse * SumKva(equipment, G55Technology.SixPulse) + Eq12ActiveFrontEnd * SumKva(equipment, G55Technology.ActiveFrontEnd);
            double have = sscMva * 1000.0;
            if (headroom == 0.0)
                return new G55Substage("1D-2", "7.5.2", G55Outcome.Fail, double.PositiveInfinity, have, "kVA", "No 5th harmonic headroom: the background is at or above the planning level.");
            double min = num / headroom;
            return new G55Substage("1D-2", "7.5.2", have >= min ? G55Outcome.Pass : G55Outcome.Fail, min, have, "kVA",
                $"Minimum short-circuit power by equation (19) {min:R} kVA (5th headroom {headroom:R} %) against the PCC's {have:R} kVA.");
        }
        return new G55Substage("1D", "7.5", G55Outcome.NotApplicable, null, null, "", "Stage 1D follows 1C: this mix of technologies is outside it.");
    }

    /// <summary>
    /// Stage 2A (clause 8.2) at a PCC of 6.6, 11, 20 or 22 kV: 2A-1 for one three-phase technology (equations 21, 22, Table 21 at 60 MVA),
    /// 2A-2 for a mix of six-pulse and active-front-end (equation 23). Single-phase rectifiers and any other mix are NOT APPLICABLE.
    /// </summary>
    public static G55Substage Stage2A(IReadOnlyList<G55Equipment> equipment, double sscMva)
    {
        RequireEquipment(equipment);
        RequireSsc(sscMva);
        if (equipment.Any(x => x.Technology == G55Technology.SinglePhaseRectifier))
            return new G55Substage("2A", "8.2", G55Outcome.NotApplicable, null, null, "", "Stage 2A covers three-phase converters only.");
        if (Single(equipment, out G55Technology tech))
        {
            double s = equipment.Sum(x => x.RatingKva);
            double permitted = sscMva / Table21ReferenceMva * Table21(tech);
            return new G55Substage("2A-1", "8.2.1", s <= permitted ? G55Outcome.Pass : G55Outcome.Fail, s, permitted, "kVA",
                $"Sequ {s:R} kVA against Table 21 {Table21(tech):R} kVA at 60 MVA scaled by equation (22) to {sscMva:R} MVA.");
        }
        if (SixAndAfeOnly(equipment))
        {
            double min = Eq23SixPulse * SumKva(equipment, G55Technology.SixPulse) + Eq23ActiveFrontEnd * SumKva(equipment, G55Technology.ActiveFrontEnd);
            double have = sscMva * 1000.0;
            return new G55Substage("2A-2", "8.2.2", have >= min ? G55Outcome.Pass : G55Outcome.Fail, min, have, "kVA",
                $"Minimum short-circuit power by equation (23) {min:R} kVA against the PCC's {have:R} kVA.");
        }
        return new G55Substage("2A", "8.2", G55Outcome.NotApplicable, null, null, "", "This mix of technologies is outside Stage 2A; it goes to Stage 2C.");
    }

    /// <summary>
    /// Stage 2B (clause 8.3): Stage 2A with the measured background at the limiting order (5 for six-pulse and active-front-end, 11 for
    /// twelve-pulse), equations (24) to (29). The planning level is that of the PCC's own band (Table 3 for 6.6 to 22 kV: with
    /// the 5th at 3.0 %, equation (28)'s constants are equation (23)'s times 0.75, as the test pins).
    /// </summary>
    public static G55Substage Stage2B(IReadOnlyList<G55Equipment> equipment, double sscMva, double pccKv, IReadOnlyDictionary<int, double>? backgroundPct)
    {
        RequireEquipment(equipment);
        RequireSsc(sscMva);
        if (equipment.Any(x => x.Technology == G55Technology.SinglePhaseRectifier))
            return new G55Substage("2B", "8.3", G55Outcome.NotApplicable, null, null, "", "Stage 2B covers three-phase converters only.");
        if (Single(equipment, out G55Technology tech))
        {
            int h = tech == G55Technology.TwelvePulse ? 11 : 5;
            if (backgroundPct is null || !backgroundPct.TryGetValue(h, out double bg))
                return new G55Substage("2B-1", "8.3.1", G55Outcome.Incomplete, null, null, "kVA", $"The measured background at the limiting order {h} is needed (clause 6.3.5).");
            double pl = G55Limits.PlanningLevelPct(pccKv, h);
            double headroom = Math.Max(pl - bg, 0.0);
            double permitted = sscMva / Table21ReferenceMva * (headroom / (0.25 * pl)) * Table21(tech);
            double s = equipment.Sum(x => x.RatingKva);
            return new G55Substage("2B-1", "8.3.1", s <= permitted ? G55Outcome.Pass : G55Outcome.Fail, s, permitted, "kVA",
                $"Sequ {s:R} kVA against {permitted:R} kVA (equations 24 or 26: V{h} headroom {headroom:R} % of {pl:R} %).");
        }
        if (SixAndAfeOnly(equipment))
        {
            if (backgroundPct is null || !backgroundPct.TryGetValue(5, out double bg5))
                return new G55Substage("2B-2", "8.3.2", G55Outcome.Incomplete, null, null, "kVA", "The measured background at the 5th harmonic is needed (clause 6.3.5).");
            double headroom = Math.Max(G55Limits.PlanningLevelPct(pccKv, 5) - bg5, 0.0);
            double have = sscMva * 1000.0;
            if (headroom == 0.0)
                return new G55Substage("2B-2", "8.3.2", G55Outcome.Fail, double.PositiveInfinity, have, "kVA", "No 5th harmonic headroom: the background is at or above the planning level.");
            double min = (Eq28SixPulse * SumKva(equipment, G55Technology.SixPulse) + Eq28ActiveFrontEnd * SumKva(equipment, G55Technology.ActiveFrontEnd)) / headroom;
            return new G55Substage("2B-2", "8.3.2", have >= min ? G55Outcome.Pass : G55Outcome.Fail, min, have, "kVA",
                $"Minimum short-circuit power by equation (28) {min:R} kVA (5th headroom {headroom:R} %) against the PCC's {have:R} kVA.");
        }
        return new G55Substage("2B", "8.3", G55Outcome.NotApplicable, null, null, "", "This mix of technologies is outside Stage 2B; it goes to Stage 2C.");
    }

    /// <summary>
    /// Stage 2C (clause 8.4): the prediction against the planning levels. For each order h from 2 to <paramref name="maxOrder"/>
    /// (default 100): the incremental voltage by equation (36) (or (40), the same form for a single-phase connection),
    /// <c>Vc = Sequ Ih sqrt(h + k^2 h^2 (X/R)^2) / (Ssc sqrt(1+THDI^2) sqrt(1+(X/R)^2))</c> with Ih in % of the fundamental current, the
    /// predicted voltage by the summation law of clause 6.3.3 (<see cref="G55Limits.Aggregate"/>), and THDVp by equation (43), all
    /// compared with the planning levels of the PCC's band.
    ///
    /// <para>The background is measured per order or the 75 % assumption of clause 8.4.1. A measured background missing an order from
    /// 2 to <paramref name="maxOrder"/> is INCOMPLETE and names the orders (missing evidence is never a pass), unless
    /// <paramref name="missingBackgroundIsZero"/> is declared. INTERPRETATION: with the 75 % assumption applied to every one of the
    /// 99 orders at once the background alone already exceeds the THDV planning level (the rss of 0.75 of Table 3 over orders 2 to 100
    /// is 5.59 %, hand-added in the tests, against 4.5 %), so the assumed background is applied to THDV as one level:
    /// <c>THDVp^2 = (0.75 THDV_PL)^2 + sum over h of (Vp_h^2 - Vm_h^2)</c>. This is stated in the result's note.</para>
    /// </summary>
    public static G55Stage2CResult Stage2C(
        double pccLineKv, double sscMva, double xOverR, double sequKva,
        IReadOnlyDictionary<int, double> emissionPct, bool threePhase, G55Background background,
        bool diversityDeclared = false, int maxOrder = 100, bool missingBackgroundIsZero = false)
    {
        RequireSsc(sscMva);
        if (!(sequKva > 0.0) || !double.IsFinite(sequKva)) throw new ArgumentOutOfRangeException(nameof(sequKva), sequKva, "The aggregate rating must be finite and above 0 kVA.");
        if (!(xOverR >= 0.0) || !double.IsFinite(xOverR)) throw new ArgumentOutOfRangeException(nameof(xOverR), xOverR, "X/R must be finite and not negative.");
        if (emissionPct is null || emissionPct.Count == 0) throw new ArgumentException("Stage 2C needs the harmonic current emission (Ih, % of the fundamental) at each emitted order.", nameof(emissionPct));
        if (maxOrder < 2 || maxOrder > 1000) throw new ArgumentOutOfRangeException(nameof(maxOrder), maxOrder, "The highest order must be 2 to 1000.");
        foreach (KeyValuePair<int, double> kv in emissionPct)
        {
            if (kv.Key < 2) throw new ArgumentException($"Emission order {kv.Key} is not a harmonic order of 2 or more.", nameof(emissionPct));
            if (!(kv.Value >= 0.0) || !double.IsFinite(kv.Value)) throw new ArgumentException($"Emission at order {kv.Key} must be finite and not negative.", nameof(emissionPct));
        }
        ArgumentNullException.ThrowIfNull(background);
        double thdPl = G55Limits.ThdPlanningLevelPct(pccLineKv);

        double thdI = 0.0;
        foreach (double v in emissionPct.Values) thdI += (v / 100.0) * (v / 100.0);
        thdI = Math.Sqrt(thdI);

        var missing = new List<string>();
        if (!background.Assume75PercentOfPlanningLevel)
        {
            if (background.MeasuredPct is null) throw new ArgumentException("A measured background needs its levels.", nameof(background));
            if (!missingBackgroundIsZero)
                for (int h = 2; h <= maxOrder; h++)
                    if (!background.MeasuredPct.ContainsKey(h)) missing.Add("background order " + h);
        }
        if (missing.Count > 0)
            return new G55Stage2CResult(G55Outcome.Incomplete, thdPl, double.NaN, double.NaN, thdI * 100.0, false, [], missing,
                "The measured background must cover every order from 2 to " + maxOrder + " (clause 6.3.5), or be declared zero, or the 75 % assumption of clause 8.4.1 agreed with the network operator.");

        var rows = new List<G55OrderRow>(maxOrder - 1);
        double sumIncSq = 0.0, sumPredSq = 0.0, sumBgSq = 0.0;
        bool allWithin = true;
        double rx = xOverR;
        for (int h = 2; h <= maxOrder; h++)
        {
            double pl = G55Limits.PlanningLevelPct(pccLineKv, h);
            double vm = background.Assume75PercentOfPlanningLevel
                ? G55Background.AssumedFraction * pl
                : background.MeasuredPct!.TryGetValue(h, out double m) ? m : 0.0;
            double ih = emissionPct.TryGetValue(h, out double e) ? e : 0.0;
            double k = G55Limits.WorstCaseReactanceFactor(pccLineKv, h);
            double vc = sequKva / (sscMva * 1000.0) * ih * Math.Sqrt(h + k * k * h * (double)h * rx * rx)
                        / (Math.Sqrt(1.0 + thdI * thdI) * Math.Sqrt(1.0 + rx * rx));
            double alpha = diversityDeclared ? G55Limits.SummationExponent(h) : 1.0;
            double vp = G55Limits.AggregateWithExponent([vm, vc], alpha);
            bool within = vp <= pl;
            allWithin &= within;
            sumIncSq += vc * vc;
            sumPredSq += vp * vp;
            sumBgSq += vm * vm;
            rows.Add(new G55OrderRow(h, pl, vm, vc, vp, alpha, within));
        }
        double thdP;
        string note;
        if (background.Assume75PercentOfPlanningLevel)
        {
            double bgThd = G55Background.AssumedFraction * thdPl;
            thdP = Math.Sqrt(bgThd * bgThd + (sumPredSq - sumBgSq));
            note = "75 % background assumption (clause 8.4.1): applied to each order, and to THDV as one level (THDVp^2 = (0.75 THDV_PL)^2 + sum of (Vp^2 - Vm^2)), because 0.75 of every order's planning level at once exceeds the THDV planning level.";
        }
        else
        {
            thdP = Math.Sqrt(sumPredSq);
            note = "Measured background; THDVp by equation (43).";
        }
        bool pass = allWithin && thdP <= thdPl;
        return new G55Stage2CResult(pass ? G55Outcome.Pass : G55Outcome.Fail, thdPl, thdP, Math.Sqrt(sumIncSq), thdI * 100.0,
            background.Assume75PercentOfPlanningLevel, rows, [], note);
    }

    /// <summary>The result of the resonant-plant test of clause 9 (equations 44 to 49).</summary>
    public sealed record ResonantPlantResult(string Outcome, double MagnificationFactor, double ResonantOrder, double LimitFactor, string Clause, string Note);

    /// <summary>
    /// Clause 9, resonant plant (power factor capacitors, long cables) below 33 kV. At 0.4 kV and below (9.2): equations (44) and (45),
    /// <c>G = sqrt(Ssc_X Q) / Pd</c>, <c>hr = sqrt(Ssc_X / Q)</c>, permitted if G is at most 1.33 for hr up to 15 or at most 2 above 15.
    /// Above 0.4 kV and below 33 kV (9.3): equations (48) and (49) with the Table 22 factor k, <c>G = sqrt(Ssc_X Q)/(Pd sqrt(k))</c>,
    /// <c>hr = sqrt(Ssc_X/(Q k))</c>; the 9.3 test then needs the aggregated distortion within 75 % of the planning level, which the
    /// caller makes with Stage 2C, so here a factor within the Figure 14 limits is reported PASS subject to that aggregate check
    /// and anything else is "STAGE 3". At 33 kV and above clause 9.1 sends every resonant plant to Stage 3.
    /// <paramref name="sscXMva"/> is the short-circuit level on the reactive part of the source impedance (equation 47),
    /// <paramref name="qrpMvar"/> the plant's reactive power, <paramref name="pdMw"/> the damping power (equation 46).
    /// </summary>
    public static ResonantPlantResult ResonantPlant(double pccLineKv, double sscXMva, double qrpMvar, double pdMw)
    {
        if (!(sscXMva > 0.0) || !double.IsFinite(sscXMva)) throw new ArgumentOutOfRangeException(nameof(sscXMva), sscXMva, "Ssc_X must be finite and above 0.");
        if (!(qrpMvar > 0.0) || !double.IsFinite(qrpMvar)) throw new ArgumentOutOfRangeException(nameof(qrpMvar), qrpMvar, "The resonant plant's reactive power must be finite and above 0.");
        if (!(pdMw > 0.0) || !double.IsFinite(pdMw)) throw new ArgumentOutOfRangeException(nameof(pdMw), pdMw, "The damping power must be finite and above 0.");
        if (pccLineKv >= 33.0)
            return new ResonantPlantResult("STAGE 3", double.NaN, double.NaN, double.NaN, "9.1", "Resonant plant at 33 kV or above is always a Stage 3 assessment.");
        if (pccLineKv <= 0.4)
        {
            double g = Math.Sqrt(sscXMva * qrpMvar) / pdMw;
            double hr = Math.Sqrt(sscXMva / qrpMvar);
            double limit = hr <= 15.0 ? 1.33 : 2.0;
            return new ResonantPlantResult(g <= limit ? G55Outcome.Pass : "STAGE 3", g, hr, limit, "9.2",
                g <= limit ? "Within the clause 9.2 limit." : "Above the clause 9.2 limit: the assessment proceeds to clause 9.3.");
        }
        // Table 22's k depends on the order and the resonant order depends on k: take the factor of the low-order row when the
        // resonance it gives is at or below 8, otherwise the high-order row's.
        double kLow = G55Limits.WorstCaseReactanceFactor(pccLineKv, 2), kHigh = G55Limits.WorstCaseReactanceFactor(pccLineKv, 10);
        double hrLow = Math.Sqrt(sscXMva / (qrpMvar * kLow));
        double kUse = hrLow <= 8.0 ? kLow : kHigh;
        double hrUse = hrLow <= 8.0 ? hrLow : Math.Sqrt(sscXMva / (qrpMvar * kHigh));
        double gUse = Math.Sqrt(sscXMva * qrpMvar) / (pdMw * Math.Sqrt(kUse));
        double limit93 = hrUse <= 15.0 ? 1.33 : 2.0;   // Figure 14 (p.77) applies the same factor limits on the 9.3 branch
        return new ResonantPlantResult(gUse <= limit93 ? G55Outcome.Pass : "STAGE 3", gUse, hrUse, limit93, "9.3",
            "Equations (48) and (49) with Table 22's k; the aggregated distortion must also stay within 75 % of the planning level (Stage 2C with the amplified background) before connection.");
    }
}
