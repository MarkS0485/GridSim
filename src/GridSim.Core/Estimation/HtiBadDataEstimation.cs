using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>One measurement removed by the Hypothesis-Testing Identification loop, with the pass that removed it.</summary>
/// <param name="Identified">The HTI verdict: the measurement, its jointly estimated gross error and its test statistic.</param>
/// <param name="Pass">The HTI pass whose joint test identified it, counted from 0 (pass 0 is the first estimate), as the
/// largest-normalised-residual loop (<see cref="BadDataRemoval.Pass"/>) and the estimate trace count. N256 (October
/// 2026): it was 1-based, so an HTI removal and an LNR removal from the same first estimate printed different pass
/// numbers side by side.</param>
public sealed record HtiRemoval(HtiIdentified Identified, int Pass);

/// <summary>What HTI does with a measurement it identifies as bad (N061, October 2026; design robust-gm-leverage.md
/// section 6.2).</summary>
public enum HtiAction
{
    /// <summary>Remove it (the behaviour before N061, and still the default): the row is excluded from the next pass.</summary>
    Remove,

    /// <summary>Correct it in place and re-verify: z_i is replaced by z_i - e^_i (its jointly estimated gross error) and
    /// the row is KEPT, which preserves observability at low redundancy; the estimate is re-run and the chi-square test
    /// re-checked. A measurement that is identified again after its correction (the correction did not clear it) is
    /// then removed.</summary>
    CorrectThenReverify,
}

/// <summary>One measurement HTI corrected in place (<see cref="HtiAction.CorrectThenReverify"/>).</summary>
/// <param name="Identified">The HTI verdict on the ORIGINAL measurement: its jointly estimated gross error (per-unit of
/// the estimator's internal units) and test statistic.</param>
/// <param name="CorrectedValue">The value the row carried from then on, in the measurement's own units: the original
/// value minus the estimated error converted to those units.</param>
/// <param name="Pass">The HTI pass whose joint test identified it, counted from 0 like <see cref="HtiRemoval.Pass"/> (N256).</param>
public sealed record HtiCorrection(HtiIdentified Identified, double CorrectedValue, int Pass);

/// <summary>Why the HTI loop stopped. Every stop is reported; none is silent.</summary>
public enum HtiStopReason
{
    /// <summary>The final estimate's chi-square test passed: the remaining measurements are consistent.</summary>
    ChiSquarePassed,

    /// <summary>The chi-square test did not run on the final estimate (no redundancy, or the estimate did not
    /// converge or was not observable). There is no consistency verdict.</summary>
    ChiSquareNotRun,

    /// <summary>The chi-square test failed but HTI identified no measurement as bad (every suspect was
    /// exonerated, or every large residual sits on a critical measurement). The failure stands and is reported.</summary>
    NothingIdentified,

    /// <summary>The removal cap (<see cref="EstimationOptions.MaxBadDataRemovals"/>) was reached with the
    /// chi-square test still failing.</summary>
    RemovalCapReached,
}

/// <summary>The outcome of an estimate whose bad-data stage is Hypothesis-Testing Identification.</summary>
/// <param name="Estimate">The FINAL estimate, computed from the measurements left after every HTI removal. Its own
/// <see cref="EstimationResult.Removed"/> list is empty by construction: the WLS loop itself removed nothing (its
/// largest-normalised-residual removal was switched off); the HTI removals are in <paramref name="Removed"/>.</param>
/// <param name="Removed">Every measurement HTI removed, in removal order (by pass, then by descending test statistic).</param>
/// <param name="Passes">Estimates computed (one per pass, the final one included).</param>
/// <param name="TotalIterations">Gauss-Newton iterations summed over every pass.</param>
/// <param name="StopReason">Why the loop stopped.</param>
public sealed record HtiEstimationResult(
    EstimationResult Estimate,
    IReadOnlyList<HtiRemoval> Removed,
    int Passes,
    int TotalIterations,
    HtiStopReason StopReason)
{
    /// <summary>Every measurement HTI corrected in place, in correction order (N061; empty under
    /// <see cref="HtiAction.Remove"/>). A corrected row that was later identified again is also in
    /// <see cref="Removed"/> (its correction did not clear it).</summary>
    public IReadOnlyList<HtiCorrection> Corrected { get; init; } = Array.Empty<HtiCorrection>();
}

/// <summary>
/// Runs the state estimator with <see cref="HypothesisTestingBadData"/> as the bad-data method in place of the
/// sequential largest-normalised-residual (LNR) removal loop (R062, October 2026 review: HTI was designed to
/// replace LNR but nothing called it).
///
/// <para>The loop, in plain terms:</para>
/// <list type="number">
/// <item>Estimate with the WLS loop's own removal switched off (<see cref="EstimationOptions.MaxBadDataRemovals"/> = 0),
/// so the chi-square test still runs and its verdict is honest, but nothing is removed by LNR.</item>
/// <item>If the estimate is not converged, or the chi-square test did not run, or it passed: stop. This is the
/// same detection gate LNR uses (identification only follows a failed detection test).</item>
/// <item>Otherwise run HTI on the biased estimate: every measurement whose normalised residual exceeds the suspect
/// threshold is a suspect, the gross errors of all suspects are estimated JOINTLY, and each suspect whose test
/// statistic exceeds the test threshold is identified as bad. Interacting errors are therefore separated in one
/// step instead of being peeled off one at a time (where LNR can point at a good measurement).</item>
/// <item>Remove every identified measurement (up to the removal cap, largest test statistic first) and go round
/// again. If HTI identifies nothing while chi-square still fails, stop and report the failure.</item>
/// </list>
///
/// <para>HTI is dense (it forms the gain inverse and an m x s Jacobian, and costs O(m s^2) for the suspect screen),
/// so it is refused above <see cref="DenseEstimationLimits.StateCovarianceMaxState"/> states (the same dense
/// "form and invert G" limit the opt-in state covariance uses). The robust methods (Huber, LAV, Schweppe-GM)
/// down-weight outliers instead of removing them and run no removal loop, so HTI does not apply to them and is
/// refused; prior backfill (<see cref="EstimationOptions.BackfillRemovedFromPrior"/>) belongs to the LNR loop and is
/// refused too. Every refusal throws <see cref="NotSupportedException"/> or <see cref="ArgumentException"/>; nothing
/// silently falls back to LNR.</para>
/// </summary>
public static class HtiBadDataEstimation
{
    /// <summary>
    /// An error in the estimator's internal units converted to a measurement's own units (N061): voltage and current
    /// magnitudes are per-unit in both; angles are radians inside and degrees outside; powers and flows are per-unit of
    /// BaseMva inside and MW / Mvar outside. This is the inverse of the conversion <c>MeasurementModel</c> applies to
    /// every row (its constructor), so a corrected row means exactly what the estimate says it should have read.
    /// </summary>
    /// <remarks>Public since October 2026 (N177) so the CLI's HTI report can print the error in the measurement's own
    /// units from this one rule instead of a copy of it.</remarks>
    public static double ToMeasurementUnits(MeasurementKind kind, double internalUnits, double baseMva) => kind switch
    {
        MeasurementKind.VmPu or MeasurementKind.IMagPu => internalUnits,
        MeasurementKind.VaDeg or MeasurementKind.IAngDeg => internalUnits * 180.0 / Math.PI,
        _ => internalUnits * baseMva,
    };

    /// <summary>
    /// The name of the unit <see cref="HtiIdentified.EstimatedError"/> is in for a row of this kind, i.e. the
    /// estimator's internal unit (N177). Before October 2026 the CLI labelled every row "pu", which was wrong for the
    /// angle rows (radians). The names are the exact strings the CLI writes in its <c>estimated_error_unit</c> CSV
    /// column and after the number on the console:
    /// <list type="bullet">
    /// <item><c>pu</c>: voltage and current magnitudes (<see cref="MeasurementKind.VmPu"/>, <see cref="MeasurementKind.IMagPu"/>);</item>
    /// <item><c>rad</c>: angles (<see cref="MeasurementKind.VaDeg"/>, <see cref="MeasurementKind.IAngDeg"/>);</item>
    /// <item><c>pu_base_mva</c>: powers and flows, per-unit on the model's system base (multiply by BaseMva for MW or
    /// Mvar).</item>
    /// </list>
    /// Every kind is listed by name; a new kind added to the enum fails loud here rather than borrowing a label.
    /// </summary>
    public static string InternalUnitName(MeasurementKind kind) => kind switch
    {
        MeasurementKind.VmPu or MeasurementKind.IMagPu => "pu",
        MeasurementKind.VaDeg or MeasurementKind.IAngDeg => "rad",
        MeasurementKind.PInjMw or MeasurementKind.QInjMvar or MeasurementKind.PFlowMw or MeasurementKind.QFlowMvar => "pu_base_mva",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No internal unit name is defined for this measurement kind."),
    };

    /// <summary>
    /// The name of the measurement's own unit, the unit <see cref="ToMeasurementUnits"/> converts into (N177):
    /// <c>pu</c> for voltage and current magnitudes, <c>deg</c> for angles, <c>MW</c> for real power and flow,
    /// <c>Mvar</c> for reactive power and flow. Every kind is listed by name, as in <see cref="InternalUnitName"/>.
    /// </summary>
    public static string MeasurementUnitName(MeasurementKind kind) => kind switch
    {
        MeasurementKind.VmPu or MeasurementKind.IMagPu => "pu",
        MeasurementKind.VaDeg or MeasurementKind.IAngDeg => "deg",
        MeasurementKind.PInjMw or MeasurementKind.PFlowMw => "MW",
        MeasurementKind.QInjMvar or MeasurementKind.QFlowMvar => "Mvar",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No measurement unit name is defined for this measurement kind."),
    };

    /// <summary>The largest state size HTI accepts (dense gain inverse plus an O(m s^2) suspect screen).</summary>
    public const int MaxState = DenseEstimationLimits.StateCovarianceMaxState;

    /// <summary>Estimate <paramref name="set"/> on <paramref name="model"/> with HTI as the bad-data method.</summary>
    /// <param name="estimator">The estimator to run each pass with (its linear solver is reused).</param>
    /// <param name="options">The estimation options. <see cref="EstimationOptions.DetectBadData"/> must be on;
    /// <see cref="EstimationOptions.NormalizedResidualThreshold"/> is the HTI suspect threshold and
    /// <see cref="EstimationOptions.MaxBadDataRemovals"/> caps the total HTI removals.</param>
    /// <param name="testThreshold">The HTI test-statistic threshold (|e^| / sqrt(variance)); default 3.0, the same as
    /// <see cref="HypothesisTestingBadData.Identify"/>.</param>
    /// <param name="action">Remove the identified measurements (the default, exactly the behaviour before N061) or
    /// correct them in place and re-verify (<see cref="HtiAction.CorrectThenReverify"/>). With either action
    /// <see cref="EstimationOptions.MaxBadDataRemovals"/> caps the total number of rows acted on (corrections plus
    /// removals).</param>
    public static HtiEstimationResult Estimate(
        WlsStateEstimator estimator, GridModel model, MeasurementSet set, EstimationOptions options,
        double testThreshold = 3.0, HtiAction action = HtiAction.Remove)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);

        // ---- Preconditions: every one fails loud rather than quietly running something else. ----
        if (!options.DetectBadData)
            throw new ArgumentException("HTI is a bad-data method: it needs bad-data detection on (DetectBadData = true).", nameof(options));
        // N331 (CF5): LavLp too. N356 (CF6): LavIrls (the IRLS LAV under its new name) too.
        if (options.Method is EstimationMethod.Huber or EstimationMethod.Lav or EstimationMethod.SchweppeGm or EstimationMethod.LavLp
            or EstimationMethod.LavIrls)
            throw new NotSupportedException(
                $"HTI does not apply to the robust method {options.Method}: robust methods down-weight outliers instead of " +
                "removing them and run no removal loop. Use --method wls, qr, constrained or feasible with HTI.");
        if (options.BackfillRemovedFromPrior)
            throw new NotSupportedException(
                "prior backfill (BackfillRemovedFromPrior) belongs to the sequential LNR removal loop and is not defined for HTI.");
        if (!(testThreshold > 0) || !double.IsFinite(testThreshold))
            throw new ArgumentOutOfRangeException(nameof(testThreshold), testThreshold, "the HTI test threshold must be a finite positive number.");
        if (options.MaxBadDataRemovals < 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxBadDataRemovals, "MaxBadDataRemovals must not be negative.");

        // The state size is fixed by the network (non-slack angles plus every magnitude), so the dense limit can be
        // checked before any work is done.
        int stateSize = model.Buses.Count(b => b.Type != BusType.Slack) + model.BusCount;
        if (stateSize > MaxState)
            throw new NotSupportedException(
                $"HTI is dense (gain inverse and an O(m s^2) suspect screen) and is limited to {MaxState} states; " +
                $"this case has {stateSize} ({model.BusCount} buses). Use the default sequential LNR bad-data removal at this size.");

        // Each pass runs the WLS loop with its own removal disabled: chi-square still runs (and normalised residuals
        // are still formed when it fails), but LNR removes nothing. Every other option is passed through unchanged.
        EstimationOptions passOptions = options with { MaxBadDataRemovals = 0 };

        var active = set.Measurements.ToList();
        var removed = new List<HtiRemoval>();
        // N061: the corrected rows (by reference: the instance now in 'active'), so a row identified again after its
        // correction is recognised and removed instead of being corrected a second time.
        var corrected = new List<HtiCorrection>();
        var correctedRows = new HashSet<Measurement>(ReferenceEqualityComparer.Instance);
        int pass = 0, totalIterations = 0;
        HtiEstimationResult Done(EstimationResult est, HtiStopReason why) =>
            new(est, removed, pass, totalIterations, why) { Corrected = corrected };

        while (true)
        {
            pass++;
            // N256: 'pass' counts the estimates computed (HtiEstimationResult.Passes, 1 after the first estimate); the
            // records below carry the pass INDEX, from 0, the numbering every other estimation loop uses.
            int passIndex = pass - 1;
            MeasurementSet current = set with { Measurements = active.ToList() };
            EstimationResult est = estimator.Estimate(model, current, passOptions);
            totalIterations += est.Iterations;

            // Detection gate: only a converged estimate whose chi-square test ran and FAILED goes on to identification.
            if (!est.Converged || est.ChiSquareTest == ChiSquareOutcome.NotRun)
                return Done(est, HtiStopReason.ChiSquareNotRun);
            if (est.ChiSquareTest == ChiSquareOutcome.Passed)
                return Done(est, HtiStopReason.ChiSquarePassed);
            if (removed.Count + corrected.Count >= options.MaxBadDataRemovals)
                return Done(est, HtiStopReason.RemovalCapReached);

            // Identification: the joint hypothesis test over every suspect. Under the constrained and feasible methods
            // the virtual-zero-injection rows are hard equalities (network facts), so HTI must treat them as the
            // constrained LNR path does: never a suspect, never removed.
            bool hardConstraints = options.Method is EstimationMethod.Constrained or EstimationMethod.Feasible;
            IReadOnlyList<HtiIdentified> identified = HypothesisTestingBadData.Identify(
                model, current, est, options.NormalizedResidualThreshold, testThreshold, hardConstraints);
            if (identified.Count == 0)
                return Done(est, HtiStopReason.NothingIdentified);

            // Act on the identified measurements, largest test statistic first, up to the remaining cap. The sort is
            // stable on ties (OrderByDescending is a stable sort), so the order is deterministic.
            int room = options.MaxBadDataRemovals - removed.Count - corrected.Count;
            foreach (HtiIdentified bad in identified.OrderByDescending(h => h.TestStatistic).Take(room))
            {
                // Identify returns the very Measurement instances of the set it was given (MeasurementModel keeps the
                // list as-is), so removal is by reference: an identical duplicate row elsewhere is NOT removed with it.
                int index = active.FindIndex(m => ReferenceEquals(m, bad.Measurement));
                if (index < 0)
                    throw new InvalidOperationException(
                        $"HTI identified a measurement ({bad.Measurement.Kind} @ {bad.Measurement.Describe()}) that is not in the active set; this is a bug.");
                if (action == HtiAction.CorrectThenReverify && !correctedRows.Contains(bad.Measurement))
                {
                    // N061: correct in place, z_i <- z_i - e^_i, keeping the row (design section 6.2). e^ is in the
                    // estimator's internal units, so it is converted back to the measurement's own units first.
                    Measurement fixedRow = bad.Measurement with { Value = bad.Measurement.Value - ToMeasurementUnits(bad.Measurement.Kind, bad.EstimatedError, model.BaseMva) };
                    active[index] = fixedRow;
                    correctedRows.Add(fixedRow);
                    corrected.Add(new HtiCorrection(bad, fixedRow.Value, passIndex));
                    continue;
                }
                // Remove: the default action, or a corrected row identified again (its correction did not clear it).
                active.RemoveAt(index);
                removed.Add(new HtiRemoval(bad, passIndex));
            }
            // Next pass re-estimates from the original start with the identified rows excluded.
        }
    }
}
