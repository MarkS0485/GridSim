using System.Globalization;

namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>Classification of one transfer trial: every required state evaluated and within its limits, at least one
/// absolute limit breached with every required state evaluated, or evidence missing (no solution, islanding, incomplete
/// measurement). Unknown is never a physical limit.</summary>
public enum TransferClass { Feasible, Violation, Unknown }

/// <summary>Search horizon (RC3 P4). All MW values finite and positive. <see cref="MaxTrials"/> bounds the search
/// trials, the zero-transfer reference included; the two final endpoint re-solves are made in addition and declared.
/// <see cref="Probes"/> coarse points above the first violation are evaluated (within <see cref="MaxMw"/> and the
/// budget) only to detect a non-monotone feasible region; they never move the reported bound.</summary>
public sealed record TransferSearchOptions(double MaxMw, double StepMw, double ToleranceMw, int MaxTrials, int Probes = 1)
{
    /// <summary>Report-size cap: at most this many search trials may be requested.</summary>
    public const int TrialCap = 10_000;

    internal void Validate()
    {
        if (!double.IsFinite(MaxMw) || !double.IsFinite(StepMw) || !double.IsFinite(ToleranceMw) ||
            MaxMw <= 0 || StepMw <= 0 || ToleranceMw <= 0)
            throw new ArgumentException("Transfer maxMw, stepMw and toleranceMw must be finite and positive.");
        if (ToleranceMw > StepMw)
            throw new ArgumentException("Transfer toleranceMw cannot exceed stepMw.");
        if (MaxMw / StepMw > 100_000)
            throw new ArgumentException("Transfer maxMw / stepMw exceeds 100000 coarse points.");
        if (MaxTrials < 2 || MaxTrials > TrialCap)
            throw new ArgumentException($"Transfer maxTrials must be between 2 and {TrialCap}.");
        if (Probes < 0 || Probes > 10)
            throw new ArgumentException("Transfer probes must be between 0 and 10.");
    }

    /// <summary>Number of coarse points above zero: k * StepMw for k = 1..K, the last clamped to MaxMw.</summary>
    internal int CoarsePoints => (int)Math.Ceiling(MaxMw / StepMw);
}

/// <summary>One violated absolute limit, located by state (null outage = intact network) and element.
/// Margin is unrounded and signed: upper = limit - observed, lower = observed - limit.</summary>
public sealed record TransferLimit(int? OutageIndex, string Kind, int Index, string Units, double Observed,
    double Limit, double Margin, string Side, string? Source);

/// <summary>Evaluator output for one transfer level. <see cref="Fingerprint"/> is an exact (bit-level) digest of the
/// evidence used to compare a clean re-solve with the original trial. <see cref="Detail"/> is the runner's own
/// evidence (null for an injected evaluator).</summary>
public sealed record TransferEvaluation(TransferClass Class, IReadOnlyList<TransferLimit> Violations,
    string ControlSignature, string? Reason, string Fingerprint, TransferTrialDetail? Detail = null)
{
    internal string? BindingIdentity => Violations.Count == 0 ? null
        : string.Create(CultureInfo.InvariantCulture,
            $"{(Violations[0].OutageIndex is int o ? "outage " + o : "intact")}/{Violations[0].Kind}/{Violations[0].Index}");
}

/// <summary>A trial in deterministic execution order.</summary>
public sealed record TransferSearchTrial(int Sequence, string Phase, double TransferMw, TransferEvaluation Evaluation);

/// <summary>A condition under which the search assumptions failed or the bound is weaker than requested.</summary>
public sealed record TransferQualification(string Code, string Detail);

/// <summary>Exact accounting of what the search evaluated and what the budget left unevaluated.</summary>
public sealed record TransferAccounting(int SearchTrials, int FinalResolves, int CoarsePoints, int CoarseEvaluated,
    int CoarseUnevaluated, int ProbesEvaluated, int ProbesUnevaluated, int RefinementTrials,
    int RefinementStepsUnevaluated, int Solves);

public sealed record TransferSearchOutcome(string Status, string Outcome, IReadOnlyList<TransferQualification> Qualifications,
    IReadOnlyList<TransferSearchTrial> Trials, TransferSearchTrial Reference, TransferSearchTrial? Lower,
    TransferSearchTrial? Upper, TransferAccounting Accounting);

/// <summary>
/// RC3 P4 bounded transfer search, independent of the physics so its classification logic can be tested with an
/// injected evaluator. Order of operations:
/// <list type="number">
/// <item>Evaluate zero transfer. A violation there is NoAdmissibleStart; missing evidence is ReferenceIncomplete.
/// Neither is reported as zero MW available.</item>
/// <item>Walk upward at k * StepMw (k = 1, 2, ...; the last point clamped to MaxMw) until the first violation, the
/// first unknown, the cap or the trial budget.</item>
/// <item>Above a first violation, evaluate up to <see cref="TransferSearchOptions.Probes"/> further coarse points; a
/// feasible probe qualifies the result NonMonotone (the reported region stays the one connected to zero).</item>
/// <item>Bisect [last feasible, first violation] at lo + (hi - lo) / 2 until hi - lo &lt;= ToleranceMw or the budget
/// ends. An unknown midpoint stops refinement and qualifies the bracket.</item>
/// <item>Qualify a change in solved control state (PV/PQ) or in the first binding limit across the sampled trials.</item>
/// <item>Re-solve both bracket endpoints from a clean state and require bit-identical evidence.</item>
/// </list>
/// Status: VERIFIED (limit or cap, no qualification), QUALIFIED (bound found with a qualification), NOT SECURE (no
/// admissible start), INCOMPLETE (reference evidence missing, unknown upper endpoint or budget exhausted below the
/// first violation).
/// </summary>
public static class TransferSearch
{
    public static TransferSearchOutcome Run(Func<double, TransferEvaluation> evaluate, TransferSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var trials = new List<TransferSearchTrial>();
        var qualifications = new List<TransferQualification>();
        TransferSearchTrial Eval(string phase, double mw)
        {
            TransferEvaluation e = evaluate(mw) ?? throw new InvalidOperationException("Evaluator returned null.");
            var trial = new TransferSearchTrial(trials.Count, phase, mw, e);
            trials.Add(trial);
            return trial;
        }
        int K = options.CoarsePoints;

        TransferSearchTrial reference = Eval("reference", 0);
        if (reference.Evaluation.Class != TransferClass.Feasible)
        {
            TransferSearchTrial again = Eval("final-reference", 0);
            if (!Same(reference.Evaluation, again.Evaluation))
                qualifications.Add(new("RepeatSolveMismatch", "The zero-transfer re-solve did not reproduce the reference evidence."));
            bool violated = reference.Evaluation.Class == TransferClass.Violation;
            return new(violated ? "NOT SECURE" : "INCOMPLETE", violated ? "NoAdmissibleStart" : "ReferenceIncomplete",
                qualifications, trials, reference, null, null,
                Accounting(trials, options, K, 0, K, 0, 0, 1));
        }

        TransferSearchTrial lo = reference;
        TransferSearchTrial? hi = null;
        int coarseEvaluated = 0;
        for (int k = 1; k <= K; k++)
        {
            if (trials.Count >= options.MaxTrials) break;
            TransferSearchTrial t = Eval("coarse", Math.Min(k * options.StepMw, options.MaxMw));
            coarseEvaluated++;
            if (t.Evaluation.Class == TransferClass.Feasible) lo = t;
            else { hi = t; break; }
        }
        int coarseUnevaluated = hi is null ? K - coarseEvaluated : 0;

        int probesEvaluated = 0, probesUnevaluated = 0, refinement = 0, refinementUnevaluated = 0;
        string outcome;
        if (hi is null)
            outcome = lo.TransferMw == options.MaxMw ? "AtLeastMax" : "BudgetExhausted";
        else if (hi.Evaluation.Class == TransferClass.Unknown)
            outcome = "UnknownAbove";
        else
        {
            outcome = "Limited";
            // Probes: coarse points above the first violation, only to expose a non-monotone feasible region.
            int firstK = coarseEvaluated;
            for (int p = 1; p <= options.Probes; p++)
            {
                int k = firstK + p;
                if (k > K) break;
                if (trials.Count >= options.MaxTrials) { probesUnevaluated = Math.Min(options.Probes, K - firstK) - (p - 1); break; }
                TransferSearchTrial probe = Eval("probe", Math.Min(k * options.StepMw, options.MaxMw));
                probesEvaluated++;
                if (probe.Evaluation.Class == TransferClass.Feasible)
                    qualifications.Add(new("NonMonotone", string.Create(CultureInfo.InvariantCulture,
                        $"Feasible trial at {probe.TransferMw:R} MW above the violation at {hi.TransferMw:R} MW; the reported bound is the region connected to zero, not a global maximum.")));
            }
            // Bisection over [lo, hi].
            while (hi.TransferMw - lo.TransferMw > options.ToleranceMw)
            {
                if (trials.Count >= options.MaxTrials)
                {
                    double width = hi.TransferMw - lo.TransferMw;
                    while (width > options.ToleranceMw) { width /= 2; refinementUnevaluated++; }
                    qualifications.Add(new("ToleranceNotReached", string.Create(CultureInfo.InvariantCulture,
                        $"Trial budget exhausted with a bracket of {hi.TransferMw - lo.TransferMw:R} MW; {refinementUnevaluated} bisection step(s) unevaluated.")));
                    break;
                }
                TransferSearchTrial mid = Eval("refine", lo.TransferMw + (hi.TransferMw - lo.TransferMw) / 2);
                refinement++;
                if (mid.Evaluation.Class == TransferClass.Feasible) lo = mid;
                else if (mid.Evaluation.Class == TransferClass.Violation) hi = mid;
                else
                {
                    qualifications.Add(new("UnknownInBracket", string.Create(CultureInfo.InvariantCulture,
                        $"Refinement trial at {mid.TransferMw:R} MW has no complete evidence ({mid.Evaluation.Reason}); the bracket stops at the last known violation.")));
                    break;
                }
            }
        }

        // Control state and binding identity across the sampled region (zero up to the upper endpoint).
        double top = hi?.TransferMw ?? lo.TransferMw;
        var sampled = trials.Where(t => t.Phase != "probe" && t.TransferMw <= top && t.Evaluation.Class != TransferClass.Unknown)
            .OrderBy(t => t.TransferMw).ThenBy(t => t.Sequence).ToList();
        var signatures = sampled.Select(t => t.Evaluation.ControlSignature).Distinct(StringComparer.Ordinal).ToList();
        if (signatures.Count > 1)
            qualifications.Add(new("ControlStateChange", string.Join("; ", sampled
                .Where((t, i) => i == 0 || t.Evaluation.ControlSignature != sampled[i - 1].Evaluation.ControlSignature)
                .Select(t => string.Create(CultureInfo.InvariantCulture,
                    $"{t.TransferMw:R} MW: {(t.Evaluation.ControlSignature.Length == 0 ? "none" : t.Evaluation.ControlSignature)}")))));
        // Binding identity over every violating coarse and refinement trial (the coarse first violation may lie above the
        // final upper endpoint), in execution order.
        var bindings = trials.Where(t => t.Phase is "coarse" or "refine" && t.Evaluation.Class == TransferClass.Violation)
            .Select(t => t.Evaluation.BindingIdentity!).Distinct(StringComparer.Ordinal).ToList();
        if (bindings.Count > 1)
            qualifications.Add(new("BindingLimitChanged", "First binding limit differs across violating trials: " + string.Join(", ", bindings)));

        // Clean re-solve of both endpoints.
        int finals = 0;
        TransferSearchTrial lower = Eval("final-lower", lo.TransferMw);
        finals++;
        if (!Same(lo.Evaluation, lower.Evaluation))
            qualifications.Add(new("RepeatSolveMismatch", string.Create(CultureInfo.InvariantCulture,
                $"Re-solve at {lo.TransferMw:R} MW did not reproduce the lower endpoint.")));
        TransferSearchTrial? upper = null;
        if (hi is not null)
        {
            upper = Eval("final-upper", hi.TransferMw);
            finals++;
            if (!Same(hi.Evaluation, upper.Evaluation))
                qualifications.Add(new("RepeatSolveMismatch", string.Create(CultureInfo.InvariantCulture,
                    $"Re-solve at {hi.TransferMw:R} MW did not reproduce the upper endpoint.")));
        }

        string status = outcome is "UnknownAbove" or "BudgetExhausted" ? "INCOMPLETE"
            : qualifications.Count == 0 ? "VERIFIED" : "QUALIFIED";
        return new(status, outcome, qualifications, trials, reference, lower, upper,
            Accounting(trials, options, K, coarseEvaluated, coarseUnevaluated, probesEvaluated, probesUnevaluated, finals,
                refinement, refinementUnevaluated));
    }

    private static bool Same(TransferEvaluation a, TransferEvaluation b) =>
        a.Class == b.Class && string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(a.ControlSignature, b.ControlSignature, StringComparison.Ordinal);

    private static TransferAccounting Accounting(List<TransferSearchTrial> trials, TransferSearchOptions options, int coarse,
        int coarseEvaluated, int coarseUnevaluated, int probesEvaluated, int probesUnevaluated, int finals,
        int refinement = 0, int refinementUnevaluated = 0) =>
        new(trials.Count - finals, finals, coarse, coarseEvaluated, coarseUnevaluated, probesEvaluated, probesUnevaluated,
            refinement, refinementUnevaluated, trials.Sum(t => t.Evaluation.Detail?.Solves ?? 0));
}
