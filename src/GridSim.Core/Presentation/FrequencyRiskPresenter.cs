using GridSim.Core.Dynamics;
using GridSim.Core.Model;

namespace GridSim.Core.Presentation;

/// <summary>
/// The ONE presenter for the NESO RP1 / OPERATOR credible loss and the purchased-time grade ladder, called by both
/// the web projections (<c>TabProjections.Rp1</c> / <c>Operator</c>, <c>FuelBreakdown</c>) and the WPF view-models
/// (<c>NesoRp1Vm</c>, <c>OperatorVm</c>). Before October 2026 (Q1 / R304) the two shells disagreed: the web used the
/// largest single unit, the WPF the larger of that unit and the largest correlated interconnector group.
/// <para><b>The rule, grounded in the GB documents (Q1 ruling: "ground it in the real rules").</b> Source: NESO,
/// <i>Frequency Risk and Control Report 2025</i>, "The Report - For Consultation", March 2025 (the FRCR the NETS SQSS
/// requires NESO to produce; file <c>W:\Code\TSGB-2026V2\Official Policy Files\0006 - Frequency Risk and Control
/// Report 2025.pdf</c>):</para>
/// <list type="bullet">
/// <item>Executive summary (p.4), recommendation 2: "Secure all Balancing Mechanism Unit (BMU) only risks and do not
/// apply additional actions to mitigate all BMU plus Vector Shift (BMU+VS) and simultaneous events".</item>
/// <item>Section 6, Table 1 (p.15): current secured loss group = "BMU-only"; Section 6.1.1 (p.16): "the FRCR policy
/// mandates securing all BMU-only events".</item>
/// <item>Section 6.2 (p.18-19): securing all simultaneous events would need up to 3 GW of DC holding and is not
/// recommended; Section 7.1 (p.27): "Secure all BMU-only events to keep resulting frequency deviations within
/// 49.2 Hz and 50.5 Hz" and "Do not apply additional controls to secure all BMU+VS and simultaneous events".</item>
/// <item>Section 8.1 (p.29): a "unified loss risk policy" covering "the loss risk from individual BMUs or from a NETS
/// group" is only a FUTURE direction needing industry consultation and SQSS Panel approval - not today's rule.</item>
/// </list>
/// <para>So the credible loss NESO secures today is the loss of ONE unit (one BMU). Two interconnectors tripping
/// together at a shared landfall (Sellindge IFA + ElecLink, the North Sea pair) is a simultaneous / multi-BMU event,
/// which FRCR 2025 explicitly does NOT secure. The correlated group is therefore shown as unsecured live context
/// (<see cref="CredibleLossView.CorrelatedGroupExceedsSecured"/> flags when it is the bigger risk), but it never
/// redefines the credible loss. This is the web rule, pinned by
/// <c>WebProjectionAntiDriftTests.Rp1_CredibleLoss_IsTheSqssSecuredLargestLoss</c>; the WPF moved to it.</para>
/// <para><b>What the documents do not set.</b> No document in the repository defines a grade ladder on purchased
/// time; the thresholds below are presentation judgement (Mark's Q10 / Q1 "best judgement"), anchored to the 49.2 Hz
/// secured-event limit above (FRCR 2025 s.7.1) via <see cref="StabilityMetrics.LfddWorkingSetpointHz"/>. The unified
/// ladder is the WPF four-step NORMAL / WATCH / ADVISORY / WARNING ladder with its falling-trend escalation; its
/// colour levels keep the web's existing cuts exactly (green at or above 5 s, amber at or above 2 s, red below).</para>
/// </summary>
public static class FrequencyRiskPresenter
{
    // ---------------- grade ladder (seconds of purchased time to 49.2 Hz) ----------------

    /// <summary>At or above this (s) the grade is NORMAL (green). Same value as the web GREEN cut it replaces.</summary>
    public const double GradeNormalS = 5.0;

    /// <summary>At or above this (s), and below <see cref="GradeNormalS"/>, the grade is WATCH (amber).</summary>
    public const double GradeWatchS = 3.0;

    /// <summary>At or above this (s), and below <see cref="GradeWatchS"/>, the grade is ADVISORY (amber); below it
    /// the grade is WARNING (red). Same value as the web AMBER cut it replaces.</summary>
    public const double GradeAdvisoryS = 2.0;

    /// <summary>Purchased time (s) shown when it is infinite or longer: the gauge, the trace and the "&gt;10 s"
    /// text cap. A trace point for an infinite purchased time is stored as this value.</summary>
    public const double PurchasedTimeDisplayCapS = 10.0;

    /// <summary>Fall in purchased time (s) between the newest trace point and the point
    /// <see cref="TrendSpanPoints"/> - 1 frames earlier that counts as a tightening (falling) trend.</summary>
    public const double TighteningDeltaS = 0.05;

    /// <summary>The trend compares the newest point with the one three frames before it, so it needs four points.</summary>
    public const int TrendSpanPoints = 4;

    /// <summary>Rolling purchased-time trace length: the last 60 solved state FRAMES, one point per frame. Its span in
    /// time is 60 times the frame step (60 minutes at a 1 minute step, 20 hours at 20 minutes, 30 hours on a
    /// half-hourly replay), so it is labelled in frames (D-DOC-148: the old summary and the desktop label said
    /// "30 minutes", which no step gives).</summary>
    public const int TraceCapacity = 60;

    /// <summary>The header both shells may show over the trace: "STABILITY TREND  *  last 60 frames" (D-DOC-148).</summary>
    public static readonly string StabilityTrendTitle =
        "STABILITY TREND  *  last " + TraceCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture) + " frames";

    /// <summary>The colour level a grade is drawn in. Both shells map it to their own brush / CSS class.</summary>
    public enum GradeLevel { Green, Amber, Red }

    /// <summary>
    /// One graded purchased-time reading. <see cref="Name"/> is the ladder step; <see cref="Tightening"/> is true
    /// when a falling trend escalated it (below NORMAL only), in which case <see cref="Level"/> is forced to Red and
    /// <see cref="Text"/> carries a " down" suffix, exactly as the WPF ladder always did.
    /// </summary>
    public readonly record struct Grade(string Name, GradeLevel Level, bool Tightening)
    {
        /// <summary>The label both shells display: the step name, with " down" when the trend escalated it.</summary>
        public string Text => Tightening ? Name + " down" : Name;
    }

    /// <summary>
    /// Grade a purchased time (seconds to 49.2 Hz) on the unified ladder. <paramref name="tightening"/> is the
    /// falling-trend flag (see <see cref="PurchasedTimeTrace.IsTightening"/>); it escalates any grade below NORMAL
    /// to red. +infinity (no loss to ride, or zero RoCoF) is NORMAL. Fails loud on NaN or a negative time: purchased
    /// time is a duration and <see cref="StabilityMetrics.PurchasedTimeSeconds"/> never returns either.
    /// </summary>
    public static Grade GradePurchasedTime(double purchasedSeconds, bool tightening)
    {
        if (double.IsNaN(purchasedSeconds) || purchasedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(purchasedSeconds), purchasedSeconds,
                "Purchased time must be a non-negative number of seconds (or +infinity).");

        string name; GradeLevel level;
        if (purchasedSeconds >= GradeNormalS) { name = "NORMAL"; level = GradeLevel.Green; }
        else if (purchasedSeconds >= GradeWatchS) { name = "WATCH"; level = GradeLevel.Amber; }
        else if (purchasedSeconds >= GradeAdvisoryS) { name = "ADVISORY"; level = GradeLevel.Amber; }
        else { name = "WARNING"; level = GradeLevel.Red; }

        // A falling trend only escalates a grade that is already below NORMAL (the WPF rule, kept).
        bool escalate = tightening && purchasedSeconds < GradeNormalS;
        return new Grade(name, escalate ? GradeLevel.Red : level, escalate);
    }

    // ---------------- credible loss ----------------

    /// <summary>
    /// The credible loss both shells grade against, plus the correlated interconnector group as unsecured context.
    /// <see cref="SecuredLossMw"/> is the largest single online unit (FRCR 2025 "BMU-only"); the correlated group is
    /// carried for display only.
    /// </summary>
    public readonly record struct CredibleLossView(
        double SecuredLossMw,
        string SecuredLossName,
        double CorrelatedGroupMw,
        string CorrelatedGroupName)
    {
        /// <summary>True when the unsecured correlated group would be a bigger loss than the secured single unit:
        /// a simultaneous-event risk FRCR 2025 does not secure, worth flagging to the operator.</summary>
        public bool CorrelatedGroupExceedsSecured => CorrelatedGroupMw > SecuredLossMw;
    }

    /// <summary>
    /// The largest single online infeed that counts as a secured loss, read from the model's generators: the largest
    /// positive output among ALL generating units, synchronous or converter-connected (wind and solar farms, batteries
    /// discharging, importing interconnectors), named by the generator's name or, when it has none, its fuel name
    /// (shared fuel taxonomy, <see cref="FuelTaxonomy"/>). Ties keep the first unit met, as both shells always did.
    /// A unit at zero or negative output (idle, or absorbing: a pump, a charging battery, an exporting link) is not an
    /// infeed and cannot be a loss of infeed. A grid infeed (<see cref="Model.FuelKind.GridInfeed"/>: the upstream
    /// network equivalent at a model boundary) is not a unit and is skipped. Returns (0, "-") when nothing generates.
    /// <para><b>N039 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04).</b> Before, only SYNCHRONOUS
    /// units were searched, so a 2,000 MW offshore wind farm or a 2,000 MW interconnector import was never the credible
    /// loss even when it was the biggest single infeed. The documents define the secured set by the unit, not by its
    /// technology:</para>
    /// <list type="bullet">
    /// <item>NETS SQSS v2.10 (08 April 2025, docs/reference S036), para 5.1.2: the secured events include the fault outage
    /// of "a single generating unit (or several generating units sharing a common circuit breaker), a single power park
    /// module, or a single DC converter". A power park module is converter-connected plant (a wind or solar farm); a DC
    /// converter is an HVDC interconnector or offshore link.</item>
    /// <item>NESO FRCR Methodology v3, March 2025 (docs/reference S053), s.4.1.1 Table 2: a "BMU-only" event is "an event
    /// that disconnects one BMU" "caused by a Loss of Power Infeed or Loss of Power Outfeed", with no technology
    /// restriction; FRCR 2025 (S052) s.6, Table 1: the current secured loss group is "BMU-only", and s.6.1.1: "the FRCR
    /// policy mandates securing all BMU-only events".</item>
    /// <item>FRCR Data Handbook (S054) s.6.1 "BMU failure probability" (p.23): the failure data "includes interconnector failure
    /// rates" (REMIT outage messages for interconnectors), i.e. an interconnector is a BMU loss FRCR secures.</item>
    /// </list>
    /// <para>What moves: the credible loss both shells grade against (web RP1 / OPERATOR, WPF NesoRp1Vm / OperatorVm),
    /// and so the purchased time, the RoCoF and the grade, on any model whose largest positive infeed is converter plant
    /// or an interconnector. A model whose largest infeed is synchronous is unchanged bit for bit (the same unit wins with
    /// the same Pg). Pinned by <c>B4GbLossThresholdsTests.N039_*</c>.</para>
    /// </summary>
    public static (double Mw, string Name) LargestSecuredUnit(IEnumerable<Generator> generators)
    {
        // N302 (LF3, October 2026): the selection loop moved verbatim into LargestSecuredGenerator so the WPF TripCompare
        // can trip the same machine (it needs the Generator itself, for its H and MBase). Same comparisons in the same
        // order, so the (Mw, Name) pair is bit-identical: Pg of the winner (0.0 when none), its name, or the fuel name
        // when it has none, or "-" when no unit is generating.
        Generator? winner = LargestSecuredGenerator(generators);
        if (winner is null) return (0.0, "-");
        return (winner.Pg, winner.Name ?? FuelTaxonomy.Classify(winner.Type).Name);
    }

    /// <summary>
    /// N302 (LF3, October 2026): the generator <see cref="LargestSecuredUnit"/> names: the largest strictly positive Pg
    /// over every generator except a grid infeed (<see cref="IsSingleInfeedUnit"/>), the first of equal units winning, or
    /// null when no unit is generating. Fails loud on a non-finite output (it would silently win or lose the
    /// comparison). Used by the WPF TripCompare, which used to take the largest Pg of ANY generator, grid infeed included
    /// (B4 NF-2): on a model with a grid infeed larger than every unit it tripped the upstream network equivalent.
    /// </summary>
    public static Generator? LargestSecuredGenerator(IEnumerable<Generator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        double largest = 0.0;
        Generator? winner = null;
        foreach (Generator g in generators)
        {
            // A grid infeed is the rest of the network behind a model boundary, not a single unit that can trip.
            if (!IsSingleInfeedUnit(g.Type)) continue;
            // Fail loud: a non-finite output would silently win or lose the comparison.
            if (!double.IsFinite(g.Pg))
                throw new ArgumentException($"Generator '{g.Name}' has a non-finite output {g.Pg} MW.", nameof(generators));
            // Strictly greater: an idle or absorbing unit (Pg <= 0) never wins, and the first of equal units stays.
            if (g.Pg > largest) { largest = g.Pg; winner = g; }
        }
        return winner;
    }

    /// <summary>
    /// N039: true when a generator of this type string is a single unit whose trip is a secured loss of infeed (every
    /// technology: SQSS v2.10 para 5.1.2, FRCR Methodology v3 Table 2). False only for a grid infeed
    /// (<see cref="Model.FuelKind.GridInfeed"/>), the upstream network equivalent at a model boundary.
    /// </summary>
    public static bool IsSingleInfeedUnit(string? type) =>
        Model.FuelTypes.Classify(type, Model.FuelReader.Presentation) != Model.FuelKind.GridInfeed;

    /// <summary>
    /// Build the credible-loss view from the largest secured unit and the live correlated interconnector group
    /// (<see cref="StabilityMetrics.LargestCorrelatedGroup"/>). The credible loss IS the largest secured unit; the
    /// group never replaces it (FRCR 2025 Executive summary, s.6.2, s.7.1). Fails loud on a negative or
    /// non-finite figure.
    /// </summary>
    public static CredibleLossView CredibleLoss(double largestUnitMw, string? largestUnitName, StabilityMetrics.GroupLoss correlatedGroup)
    {
        if (!double.IsFinite(largestUnitMw) || largestUnitMw < 0)
            throw new ArgumentOutOfRangeException(nameof(largestUnitMw), largestUnitMw, "The largest unit must be a finite, non-negative MW figure.");
        if (!double.IsFinite(correlatedGroup.Mw) || correlatedGroup.Mw < 0)
            throw new ArgumentOutOfRangeException(nameof(correlatedGroup), correlatedGroup.Mw, "The correlated group must be a finite, non-negative MW figure.");
        return new CredibleLossView(largestUnitMw, largestUnitName ?? "-", correlatedGroup.Mw, correlatedGroup.Name ?? "");
    }

    // ---------------- where the RP1 page applies (N301) ----------------

    /// <summary>
    /// N301 (October 2026, stream LF3; ruled by main under Ruling R1, Mark delegated the EE calls 2026-10-04): true when
    /// the NESO RP1 page applies to a case of this nominal frequency, i.e. exactly the GB 50 Hz
    /// (<see cref="FrequencyLimits.NominalHz"/>). RP1 is a NESO GB control-room product: its purchased time is graded to
    /// the absolute 49.2 Hz secured-event limit (FRCR 2025 s.7.1) and its RoCoF / MIF use the GB 50 Hz nominal. On a case
    /// at any other nominal (case9 at 60 Hz) those figures have no meaning, so both shells HIDE the page with the one-line
    /// reason <see cref="Rp1NotApplicableReason"/> instead of re-basing it. The GB computation itself is unchanged: the web
    /// projection and the WPF view-model still compute every figure, only the page is not shown.
    /// <para>Exact comparison on purpose: a GB case carries exactly 50.0 (the same test <see cref="FrequencyBands.ForNominal"/>
    /// uses to return the GB set itself). Fails loud on a nominal that is not a finite positive frequency, because a NaN
    /// would otherwise silently hide (or show) the page.</para>
    /// </summary>
    public static bool Rp1AppliesAt(double nominalHz)
    {
        if (!double.IsFinite(nominalHz) || !(nominalHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz,
                "The case nominal frequency must be a finite number of Hz above 0 to decide whether the NESO RP1 page applies.");
        return nominalHz == FrequencyLimits.NominalHz;
    }

    /// <summary>
    /// N301: the one-line reason both shells show in place of the NESO RP1 page on a case whose nominal is not 50 Hz.
    /// Plain ASCII, invariant culture (the nominal is printed with <see cref="FrequencyBands.Label"/>, e.g. "60.0").
    /// </summary>
    public static string Rp1NotApplicableReason(double nominalHz) =>
        $"NESO RP1 is a GB 50 Hz product page (purchased time to 49.2 Hz, GB RoCoF and MIF); this case runs at "
        + $"{FrequencyBands.Label(nominalHz)} Hz, so the page is not shown.";

    // ---------------- falling-trend trace ----------------

    /// <summary>
    /// The rolling purchased-time trace that drives the falling-trend escalation. Each shell owns one instance per
    /// session (WPF <c>NesoRp1Vm</c>, web <c>SimSession</c>) and adds one point per solved state frame.
    /// </summary>
    public sealed class PurchasedTimeTrace
    {
        private readonly List<double> _points = new();

        /// <summary>The trace, oldest first (at most <see cref="TraceCapacity"/> points).</summary>
        public IReadOnlyList<double> Points => _points;

        /// <summary>Add one purchased-time reading. +infinity is stored as <see cref="PurchasedTimeDisplayCapS"/>
        /// (it plots at the cap); NaN or a negative time fails loud.</summary>
        public void Add(double purchasedSeconds)
        {
            if (double.IsNaN(purchasedSeconds) || purchasedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(purchasedSeconds), purchasedSeconds,
                    "Purchased time must be a non-negative number of seconds (or +infinity).");
            _points.Add(double.IsPositiveInfinity(purchasedSeconds) ? PurchasedTimeDisplayCapS : purchasedSeconds);
            while (_points.Count > TraceCapacity) _points.RemoveAt(0);
        }

        /// <summary>Empty the trace (model swap / session reset).</summary>
        public void Clear() => _points.Clear();

        /// <summary>True when the newest point is more than <see cref="TighteningDeltaS"/> below the point
        /// <see cref="TrendSpanPoints"/> - 1 frames earlier. False until the trace holds enough points.</summary>
        public bool IsTightening =>
            _points.Count >= TrendSpanPoints && _points[^1] < _points[^TrendSpanPoints] - TighteningDeltaS;
    }
}
