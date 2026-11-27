namespace GridSim.Core.Harmonics.Planning;

/// <summary>A resonant-plant item to test with clause 9 (a capacitor bank, long cable): Ssc_X (MVA), Q (MVAr) and the damping power (MW).</summary>
public sealed record ResonantPlantInput(double SscXMva, double QrpMvar, double PdMw);

/// <summary>The inputs of a G5/5 staged connection assessment that needs no network model (Stages 1 and 2 and the resonant-plant test).</summary>
/// <param name="PccLineKv">The nominal phase-phase voltage at the PCC.</param>
/// <param name="SscMva">The three-phase short-circuit power (MVA); the minimum expected under planned system conditions (clause 6.3.2).</param>
/// <param name="SscSinglePhaseMva">The single-phase short-circuit power (MVA), needed for single-phase rectifiers only.</param>
/// <param name="XOverR">The system X/R ratio at 50 Hz (equation 31).</param>
/// <param name="Equipment">The plant and equipment.</param>
/// <param name="DeclaredCompliantIec61000_3_2">Stage 1A: every item up to 16 A per phase is declared compliant with IEC 61000-3-2.</param>
/// <param name="BackgroundMeasuredPct">The measured background by order (% of the fundamental); null means none was measured.</param>
/// <param name="Assume75PercentBackground">The clause 8.4.1 assumption, with the network operator's agreement, for Stage 2C.</param>
/// <param name="EmissionPct">The harmonic current emission by order (% of the fundamental current); null takes the Annex B Table B.3 profile of the technology.</param>
/// <param name="DiversityDeclared">Table 16 exponents are used only when the sources' diversity is declared; otherwise linear addition.</param>
/// <param name="MaxOrder">The highest order of the Stage 2C sum (default 100, equation 43).</param>
/// <param name="MissingBackgroundIsZero">Declare unmeasured orders of the background to be zero.</param>
/// <param name="Resonant">Optional resonant plant (clause 9).</param>
public sealed record ConnectionAssessmentRequest(
    double PccLineKv,
    double SscMva,
    double? SscSinglePhaseMva,
    double XOverR,
    IReadOnlyList<G55Equipment> Equipment,
    bool DeclaredCompliantIec61000_3_2 = false,
    IReadOnlyDictionary<int, double>? BackgroundMeasuredPct = null,
    bool Assume75PercentBackground = false,
    IReadOnlyDictionary<int, double>? EmissionPct = null,
    bool DiversityDeclared = false,
    int MaxOrder = 100,
    bool MissingBackgroundIsZero = false,
    ResonantPlantInput? Resonant = null);

/// <summary>The staged assessment result: the route taken, the substages in order, Stage 2C, the resonant-plant test and what is awaited or assumed.</summary>
public sealed record ConnectionAssessmentResult(
    string Format,
    string Verdict,
    string Route,
    double PccLineKv,
    string PlanningTable,
    double ThdPlanningLevelPct,
    IReadOnlyList<G55Substage> Substages,
    G55Stage2CResult? Stage2C,
    G55StagedAssessment.ResonantPlantResult? ResonantPlant,
    IReadOnlyList<string> Notes,
    G55Stage3Result? Stage3 = null)
{
    /// <summary>The notes as one-member rows, so the report generator's table block can bind them.</summary>
    public IReadOnlyList<NoteRow> NoteRows => Notes.Select(n => new NoteRow(n)).ToArray();
}

/// <summary>One note as a row.</summary>
public sealed record NoteRow(string Note);

/// <summary>
/// RC4 R4-21: runs the staged G5/5 connection assessment for a PCC and builds the report: the orchestration of clauses 6 to 9 over
/// <see cref="G55StagedAssessment"/> and a report template for R4-10's generator (<c>gridsim report</c>) bound to the result document.
/// Route: LV (0.4 kV and below) Stage 1A, 1B (not evaluated), 1C, 1D, then 2C, where a 2C failure is "no connection possible without
/// mitigation"; above 0.4 kV and below 33 kV Stage 2A, 2B, 2C and a 2C failure goes to Stage 3; 33 kV and above is Stage 3 outright
/// (clause 6.1). A stage that cannot run for want of evidence is INCOMPLETE and the route continues to the next, which is never less strict.
/// </summary>
public static class ConnectionAssessmentRunner
{
    public const string Format = "gridsim-harmonic-assess/1";

    public const string Accepted = "ACCEPTED";
    public const string Stage3Required = "STAGE 3 REQUIRED";
    public const string NoConnection = "NO CONNECTION WITHOUT MITIGATION";
    public const string Incomplete = "INCOMPLETE";

    private static IReadOnlyList<HarmonicComponent> Profile(G55Technology t) => t switch
    {
        G55Technology.SixPulse => HarmonicSourceLibrary.SixPulseG5,
        G55Technology.ActiveFrontEnd => HarmonicSourceLibrary.ActiveFrontEndG5,
        G55Technology.TwelvePulse => HarmonicSourceLibrary.TwelvePulseG5,
        _ => throw new ArgumentException("A single-phase rectifier has no Table B.3 profile; give its emission by order."),
    };

    /// <summary>The emission by order of the equipment: the rating-weighted (linear) sum of the Annex B Table B.3 profiles, each in % of the group's fundamental current.</summary>
    public static IReadOnlyDictionary<int, double> ProfileEmission(IReadOnlyList<G55Equipment> equipment)
    {
        double total = equipment.Sum(e => e.RatingKva);
        var sum = new SortedDictionary<int, double>();
        foreach (G55Equipment e in equipment)
            foreach (HarmonicComponent c in Profile(e.Technology))
                sum[c.Order] = (sum.TryGetValue(c.Order, out double v) ? v : 0.0) + c.MagnitudePercent * e.RatingKva / total;
        return sum;
    }

    public static ConnectionAssessmentResult Run(ConnectionAssessmentRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Equipment is null || r.Equipment.Count == 0) throw new ArgumentException("The assessment needs at least one item of equipment.", nameof(r));
        G55VoltageBand band = G55Limits.Band(r.PccLineKv);
        string table = "Table " + G55Limits.IndividualTableNumber(band);
        double thdPl = G55Limits.ThdPlanningLevelPct(r.PccLineKv);
        var subs = new List<G55Substage>();
        var notes = new List<string>();
        bool anySinglePhase = r.Equipment.Any(e => e.Technology == G55Technology.SinglePhaseRectifier);
        if (anySinglePhase && r.Equipment.Any(e => e.Technology != G55Technology.SinglePhaseRectifier) && r.PccLineKv <= 0.4)
            notes.Add("Single-phase rectifiers are mixed with three-phase plant: Stage 1C and 1D cover one technology only, so the assessment goes to Stage 2C.");
        double ssc = anySinglePhase && r.Equipment.All(e => e.Technology == G55Technology.SinglePhaseRectifier)
            ? r.SscSinglePhaseMva ?? double.NaN
            : r.SscMva;

        G55StagedAssessment.ResonantPlantResult? resonant = r.Resonant is null ? null
            : G55StagedAssessment.ResonantPlant(r.PccLineKv, r.Resonant.SscXMva, r.Resonant.QrpMvar, r.Resonant.PdMw);

        string? decided = null;
        string route = "";
        G55Stage2CResult? s2c = null;

        if (r.PccLineKv >= 33.0)
        {
            route = "Stage 3 (PCC at 33 kV or above, clause 6.1)";
            decided = Stage3Required;
        }
        else
        {
            if (r.PccLineKv <= 0.4)
            {
                if (r.DeclaredCompliantIec61000_3_2) { subs.Add(G55StagedAssessment.Stage1A(true)); decided = Accepted; route = "1A"; }
                else
                {
                    subs.Add(G55StagedAssessment.Stage1A(false));
                    subs.Add(G55StagedAssessment.Stage1B());
                    if (double.IsNaN(ssc)) subs.Add(new G55Substage("1C", "7.4", G55Outcome.Incomplete, null, null, "", "The single-phase short-circuit power is needed for single-phase rectifiers."));
                    else
                    {
                        G55Substage c = G55StagedAssessment.Stage1C(r.Equipment, ssc);
                        subs.Add(c);
                        if (c.Outcome == G55Outcome.Pass) { decided = Accepted; route = c.Stage; }
                        else
                        {
                            G55Substage d = G55StagedAssessment.Stage1D(r.Equipment, ssc, r.BackgroundMeasuredPct);
                            subs.Add(d);
                            if (d.Outcome == G55Outcome.Pass) { decided = Accepted; route = d.Stage; }
                        }
                    }
                }
            }
            else
            {
                bool tableVoltage = r.PccLineKv is 6.6 or 11.0 or 20.0 or 22.0;
                if (tableVoltage)
                {
                    G55Substage a = G55StagedAssessment.Stage2A(r.Equipment, r.SscMva);
                    subs.Add(a);
                    if (a.Outcome == G55Outcome.Pass) { decided = Accepted; route = a.Stage; }
                    else
                    {
                        G55Substage b = G55StagedAssessment.Stage2B(r.Equipment, r.SscMva, r.PccLineKv, r.BackgroundMeasuredPct);
                        subs.Add(b);
                        if (b.Outcome == G55Outcome.Pass) { decided = Accepted; route = b.Stage; }
                    }
                }
                else
                    notes.Add(FormattableString.Invariant($"Table 21 and Table 22 (Stages 2A, 2B, 2C) are given for 6.6, 11, 20 and 22 kV; a PCC at {r.PccLineKv:R} kV has no worst-case reactance factor, so Stage 2C cannot run: the network operator's Stage 3 applies."));
            }

            if (decided is null)
            {
                bool kKnown = r.PccLineKv == 0.4 || r.PccLineKv is 6.6 or 11.0 or 20.0 or 22.0;
                if (!kKnown)
                {
                    subs.Add(new G55Substage("2C", "8.4", G55Outcome.Incomplete, null, null, "", "No Table 22 reactance factor for this voltage."));
                    decided = Stage3Required;
                    route = "Stage 3 (no Table 22 factor)";
                }
                else if (double.IsNaN(ssc) || (anySinglePhase && r.Equipment.Any(e => e.Technology != G55Technology.SinglePhaseRectifier) && r.SscSinglePhaseMva is null))
                {
                    subs.Add(new G55Substage("2C", "8.4", G55Outcome.Incomplete, null, null, "", "The short-circuit power for the connection's phases is missing."));
                    decided = Incomplete;
                }
                else
                {
                    IReadOnlyDictionary<int, double> emission;
                    try { emission = r.EmissionPct ?? ProfileEmission(r.Equipment); }
                    catch (ArgumentException ex)
                    {
                        subs.Add(new G55Substage("2C", "8.4.2", G55Outcome.Incomplete, null, null, "", ex.Message));
                        emission = new Dictionary<int, double>();
                    }
                    if (emission.Count == 0) decided = Incomplete;
                    else
                    {
                        G55Background bg = r.BackgroundMeasuredPct is not null ? G55Background.Measured(r.BackgroundMeasuredPct)
                            : r.Assume75PercentBackground ? G55Background.Assumed : null!;
                        if (bg is null)
                        {
                            subs.Add(new G55Substage("2C", "8.4.1", G55Outcome.Incomplete, null, null, "", "Stage 2C needs the measured background at the PCC, or the 75 % assumption of clause 8.4.1 agreed with the network operator."));
                            decided = Incomplete;
                        }
                        else
                        {
                            bool threePhase = !anySinglePhase;
                            s2c = G55StagedAssessment.Stage2C(r.PccLineKv, ssc, r.XOverR, r.Equipment.Sum(e => e.RatingKva), emission, threePhase, bg, r.DiversityDeclared, r.MaxOrder, r.MissingBackgroundIsZero);
                            subs.Add(new G55Substage("2C", "8.4", s2c.Outcome, s2c.ThdPredictedPct, s2c.ThdPlanningLevelPct, "% THDV", s2c.Note));
                            route = "2C";
                            if (s2c.Outcome == G55Outcome.Pass) decided = Accepted;
                            else if (s2c.Outcome == G55Outcome.Incomplete) decided = Incomplete;
                            else decided = r.PccLineKv <= 0.4 ? NoConnection : Stage3Required;
                            if (s2c.Outcome == G55Outcome.Fail) route = r.PccLineKv <= 0.4 ? "2C failed at an LV PCC (clause 8.4.7)" : "2C failed, advance to Stage 3 (clause 8.4.7)";
                        }
                    }
                }
            }
        }

        if (resonant is not null)
        {
            notes.Add(FormattableString.Invariant($"Resonant plant (clause {resonant.Clause}): {resonant.Outcome}; magnification {resonant.MagnificationFactor:R}, resonant order {resonant.ResonantOrder:R}."));
            if (resonant.Outcome == "STAGE 3" && decided == Accepted) decided = Stage3Required;
        }
        if (s2c is { BackgroundAssumed: true } && decided == Accepted)
            notes.Add("Accepted on the 75 % background assumption of clause 8.4.1, which is subject to the network operator's agreement.");
        notes.Add("The final decision rests with the network operator (clause 6.2).");
        return new ConnectionAssessmentResult(Format, decided ?? Incomplete, route, r.PccLineKv, table, thdPl, subs, s2c, resonant, notes);
    }
}

/// <summary>
/// The report of a connection assessment, through R4-10's report generator: a <c>gridsim-report-template/1</c> bound to the
/// <c>gridsim-harmonic-assess/1</c> result by JSON pointers, so <c>gridsim report</c> renders it without new report code.
/// </summary>
public static class ConnectionAssessmentReport
{
    /// <summary>The template JSON for a result printed by <c>gridsim harmonic-assess</c> (pointers under <c>/result</c>).</summary>
    public static string TemplateJson(string title = "UK harmonic connection assessment (ENA EREC G5 Issue 5)")
    {
        if (title is null || title.Contains('"') || title.Contains((char)92) || title.Any(char.IsControl))
            throw new ArgumentException("The title must not contain quotes, backslashes or control characters.", nameof(title));
        return TemplateText.Replace("@@TITLE@@", title, StringComparison.Ordinal);
    }

    private const string TemplateText = """
{
  "schema": "gridsim-report-template/1",
  "title": "@@TITLE@@",
  "sections": [
    {
      "heading": "Verdict",
      "blocks": [
        { "type": "text", "text": "Staged assessment of ENA Engineering Recommendation G5 Issue 5 (2020), clauses 6 to 9. The final decision rests with the network operator." },
        { "type": "value", "label": "Verdict", "path": "/result/verdict" },
        { "type": "value", "label": "Route", "path": "/result/route" },
        { "type": "value", "label": "PCC voltage (kV)", "path": "/result/pccLineKv", "decimals": 3 },
        { "type": "value", "label": "Planning table", "path": "/result/planningTable" },
        { "type": "value", "label": "THDV planning level (%)", "path": "/result/thdPlanningLevelPct", "decimals": 2 }
      ]
    },
    {
      "heading": "Substages",
      "blocks": [
        { "type": "table", "title": "Substages in the order run", "path": "/result/substages",
          "columns": [
            { "key": "stage", "title": "Stage" }, { "key": "clause", "title": "Clause" }, { "key": "outcome", "title": "Outcome" },
            { "key": "required", "title": "Required", "decimals": 3 }, { "key": "permitted", "title": "Permitted", "decimals": 3 },
            { "key": "unit", "title": "Unit" }, { "key": "note", "title": "Note" }
          ] }
      ]
    },
    {
      "heading": "Stage 2C prediction",
      "blocks": [
        { "type": "value", "label": "Outcome", "path": "/result/stage2C/outcome" },
        { "type": "value", "label": "Predicted THDV (%)", "path": "/result/stage2C/thdPredictedPct", "decimals": 3 },
        { "type": "value", "label": "THDV planning level (%)", "path": "/result/stage2C/thdPlanningLevelPct", "decimals": 2 },
        { "type": "table", "title": "Predicted harmonic voltage by order", "path": "/result/stage2C/orders",
          "columns": [
            { "key": "order", "title": "h" }, { "key": "planningLevelPct", "title": "Planning level", "unit": "%", "decimals": 3 },
            { "key": "backgroundPct", "title": "Background", "unit": "%", "decimals": 3 }, { "key": "incrementalPct", "title": "Incremental", "unit": "%", "decimals": 4 },
            { "key": "predictedPct", "title": "Predicted", "unit": "%", "decimals": 3 }, { "key": "within", "title": "Within" }
          ] }
      ]
    },
    {
      "heading": "Stage 3 limits",
      "blocks": [
        { "type": "value", "label": "Outcome", "path": "/result/stage3/outcome" },
        { "type": "value", "label": "Total THDV (%)", "path": "/result/stage3/thdTotalPct", "decimals": 3 },
        { "type": "table", "title": "Limit table by order", "path": "/result/stage3/rows",
          "columns": [
            { "key": "order", "title": "h" }, { "key": "backgroundPct", "title": "Background", "unit": "%", "decimals": 3 },
            { "key": "incrementalLimitPct", "title": "Incremental limit", "unit": "%", "decimals": 4 }, { "key": "incrementalPct", "title": "Incremental", "unit": "%", "decimals": 4 },
            { "key": "totalLimitPct", "title": "Total limit", "unit": "%", "decimals": 4 }, { "key": "totalPct", "title": "Total", "unit": "%", "decimals": 4 },
            { "key": "incrementalWithin", "title": "Inc ok" }, { "key": "totalWithin", "title": "Total ok" }
          ] }
      ]
    },
    {
      "heading": "Notes",
      "blocks": [ { "type": "table", "title": "Notes", "path": "/result/noteRows" } ]
    }
  ]
}
""";
}
