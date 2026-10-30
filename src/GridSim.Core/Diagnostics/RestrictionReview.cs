namespace GridSim.Core.Diagnostics;

/// <summary>Thrown when a Dynamics-&gt;Estimation fusion runs without passing the review gate.</summary>
public sealed class RestrictionViolationException : Exception
{
    public RestrictionViolationException(string message) : base(message) { }
}

/// <summary>
/// One reviewed estimation method that fuses a <c>Dynamics/</c> process model into the estimator.
/// </summary>
/// <param name="Method">The <c>EstimationMethod</c> name (as written in the enum).</param>
/// <param name="UsesDynamicsModel">True when the method integrates a dynamic process model in its predict step.</param>
/// <param name="OutputPresentPastOnly">
/// Certifies the emitted estimate is stamped present/past only - never a future-dated claim about the real grid.
/// The internal predict step may integrate the MODEL a fraction ahead; the OUTPUT is the filtered present.</param>
/// <param name="ReviewNote">The sign-off rationale itself, written in the entry (this table is the review log).</param>
public sealed record RestrictionReviewEntry(
    string Method,
    bool UsesDynamicsModel,
    bool OutputPresentPastOnly,
    string ReviewNote);

/// <summary>
/// The Dynamics-&gt;Estimation review gate mandated by <c>UnderstandingRestrictions.md</c> (invariant #5).
/// Fusing a dynamic process model into the estimator (recursive / forecasting-aided Dynamic State
/// Estimation) is the one seam where "solving" could quietly become forbidden forward inference. Any
/// estimation method that consumes a dynamics process model must appear in <see cref="Reviewed"/> with an
/// explicit sign-off <b>before it may run</b>; an unregistered dynamics-fused method fails loud via
/// <see cref="AssertPermitted"/>. Static (measurement-only) estimators need no entry and are unconditionally
/// permitted. This code table IS the review log: each entry carries its own sign-off rationale in
/// <see cref="RestrictionReviewEntry.ReviewNote"/>, and <c>RestrictionInvariantTests</c> asserts the gate so it cannot
/// be bypassed. D-DOC-136 (DOC-17): an earlier markdown mirror of this table (the "restriction review log" page)
/// was removed from the repository in July 2026, so neither these comments nor the refusal messages below point at
/// it any more.
/// </summary>
public static class RestrictionReview
{
    /// <summary>
    /// The canonical, code-resident review log. It holds four entries: the recursive network-state trackers
    /// <c>Ekf</c>, <c>Ukf</c> and <c>EnKf</c>, and the frequency / RoCoF tracker <c>FrequencyRocofKf</c>. A further
    /// dynamics-fused method (a particle filter, joint state-parameter calibration, ...) is added here, with its
    /// review note against <c>UnderstandingRestrictions.md</c>, before it may run. D-DOC-195 (DOC-17): this comment
    /// used to say the table was empty until Phase 2, long after the four entries were registered.
    /// </summary>
    public static readonly IReadOnlyList<RestrictionReviewEntry> Reviewed = new[]
    {
        new RestrictionReviewEntry(
            Method: "Ekf",
            UsesDynamicsModel: true,
            OutputPresentPastOnly: true,
            ReviewNote:
                "Recursive EKF network-state tracker (Phase 2). Its predict step integrates a process model " +
                "forward one step (random-walk persistence by default; a Dynamics/ DAE process model may be " +
                "plugged in). The EMITTED estimate is the FILTERED PRESENT state, stamped at the window time - " +
                "never a future-dated claim about the real grid. Inputs stay guard-limited (>=169h old / replay). " +
                "Reviewed against UnderstandingRestrictions.md sec.3 (a filter, not a predictor)."),
        new RestrictionReviewEntry(
            Method: "Ukf",
            UsesDynamicsModel: true,
            OutputPresentPastOnly: true,
            ReviewNote:
                "Recursive UKF network-state tracker (Phase 2) - the derivative-free sibling of the EKF, " +
                "propagating sigma points through the same measurement model and the same predict-step process " +
                "model. Identical restriction posture: the emitted estimate is the filtered present, stamped at " +
                "the window time; inputs stay guard-limited. Reviewed against UnderstandingRestrictions.md sec.3."),
        new RestrictionReviewEntry(
            Method: "EnKf",
            UsesDynamicsModel: true,
            OutputPresentPastOnly: true,
            ReviewNote:
                "Recursive Ensemble Kalman Filter network-state tracker (Phase 2) - a Monte-Carlo sibling of the " +
                "EKF/UKF propagating an ensemble through the same predict-step process model and measurement " +
                "model. Same restriction posture: the emitted estimate (the ensemble mean) is the filtered " +
                "present, stamped at the window time; inputs stay guard-limited. Reviewed against UnderstandingRestrictions.md sec.3."),
        new RestrictionReviewEntry(
            Method: "FrequencyRocofKf",
            UsesDynamicsModel: true,
            OutputPresentPastOnly: true,
            ReviewNote:
                "Kalman filter tracking system frequency and RoCoF as dynamic states from frequency measurements " +
                "(the kinematic process model freq' = freq + RoCoF*dt). Estimating RoCoF as a filter state is more " +
                "reliable than PLL-derived values (the 2016 Blue Cut mis-trip). The emitted (freq, RoCoF) is the " +
                "filtered PRESENT, never a future-dated claim; inputs stay guard-limited. Reviewed against " +
                "UnderstandingRestrictions.md sec.3 (a filter, not a predictor)."),
    };

    /// <summary>True if <paramref name="method"/> is a reviewed dynamics-fused estimation method.</summary>
    public static bool IsReviewed(string method) =>
        Reviewed.Any(e => string.Equals(e.Method, method, StringComparison.Ordinal) && e.UsesDynamicsModel);

    /// <summary>
    /// The gate the estimator calls before running a method. A method that does NOT use a dynamics model is
    /// permitted unconditionally. A dynamics-fused method must be reviewed (present in <see cref="Reviewed"/>)
    /// and must certify present/past-only output, or this throws <see cref="RestrictionViolationException"/>.
    /// </summary>
    public static void AssertPermitted(string method, bool usesDynamicsModel)
    {
        if (!usesDynamicsModel)
            return; // static, measurement-only estimation is always allowed - it is a filter, not a predictor.

        RestrictionReviewEntry? entry =
            Reviewed.FirstOrDefault(e => string.Equals(e.Method, method, StringComparison.Ordinal));

        if (entry is null || !entry.UsesDynamicsModel)
            throw new RestrictionViolationException(
                $"Estimation method '{method}' fuses a Dynamics/ process model into the estimator but is not " +
                $"registered in the Dynamics->Estimation review gate (UnderstandingRestrictions.md invariant #5). " +
                // D-DOC-136 (DOC-17): the message used to send the reader to a markdown review-log page, which is
                // not in the repository; the code table is where a review is recorded.
                $"Record the review as a RestrictionReviewEntry (with its review note) in RestrictionReview.Reviewed " +
                $"(src/GridSim.Core/Diagnostics/RestrictionReview.cs) before it can run.");

        if (!entry.OutputPresentPastOnly)
            throw new RestrictionViolationException(
                $"Estimation method '{method}' is reviewed but does not certify present/past-only output; a DSE " +
                $"estimate must never be a future-dated claim about the real grid (UnderstandingRestrictions.md).");
    }

    /// <summary>
    /// Mechanical OUTPUT guard for the review gate's <c>OutputPresentPastOnly</c> certification. Where
    /// <see cref="AssertPermitted"/> checks a reviewer's <b>attestation</b> at construction,
    /// this enforces it at the emit boundary: an emitted estimate stamped <paramref name="outputStampUtc"/>
    /// must be at or before the historical horizon (<see cref="ForwardInferenceGuard.Horizon"/>), never
    /// future-dated. A recursive DSE step integrates the process model forward internally, so this is the
    /// check that a predict step can never leak a future-dated state into an output frame - closing the gap
    /// between "reviewed as present/past" and "proven present/past at output". Throws
    /// <see cref="RestrictionViolationException"/> on violation.
    /// </summary>
    public static void AssertOutputHistorical(DateTime outputStampUtc, DateTime? utcNow = null, string? what = null)
    {
        DateTime now = utcNow ?? DateTime.UtcNow;
        DateTime stamp = UtcTime.Normalise(outputStampUtc);   // Unspecified is UTC by contract (same rule as before)
        DateTime horizon = ForwardInferenceGuard.Horizon(now);
        if (stamp > horizon)
            throw new RestrictionViolationException(
                $"{what ?? "DSE estimate"} is stamped {stamp:o}, newer than the historical horizon {horizon:o} - " +
                $"a dynamic-state estimate must be the filtered PRESENT/PAST, never a future-dated claim about the " +
                $"real grid (UnderstandingRestrictions.md invariant #5).");
    }
}
