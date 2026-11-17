using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>A unit's scheduled MW setpoint (model generator index and bus).</summary>
public sealed record UnitSetpoint(int Generator, int Bus, double Mw);

/// <summary>A scheduled setpoint changed by an applied corrective action.</summary>
public sealed record SetpointChange(int Generator, int Bus, double BeforeMw, double AfterMw);

/// <summary>
/// A branch end in the first post-outage state that is within its first-stage (temporary emergency) rating but above
/// the rating the intermediate state must meet: acceptable only temporarily, until the actions complete.
/// </summary>
public sealed record TemporaryLoading(int Branch, string End, double ObservedMva, double StageRatingMva,
    string StageSource, double ContinuousRatingMva, string ContinuousSource);

/// <summary>
/// One step of a sequence, always present in the result. <paramref name="Status"/> is Completed, Failed or Blocked
/// (not run because an earlier step failed; <paramref name="Reason"/> names that step). AC steps carry their
/// <see cref="SecurityState"/>; outage steps carry the cumulative outages applied; action steps carry the proposal,
/// its validation rows or the setpoint changes.
/// </summary>
public sealed record SequenceStep(int Order, string Name, string Status, string? Reason,
    SecurityState? State = null, IReadOnlyList<int>? OutagesApplied = null,
    IReadOnlyList<GeneratorAction>? Proposed = null, IReadOnlyList<ActionCheck>? Checks = null,
    IReadOnlyList<SetpointChange>? Applied = null, IReadOnlyList<TemporaryLoading>? TemporaryEmergencyLoading = null,
    string? ProposalDiagnostic = null);

/// <summary>
/// One ordered sequence's result. <paramref name="Outcome"/> is Verified, Violations (every gate up to the failing AC
/// state evaluated and an absolute limit failed), ActionRejected (the proposal failed or was refused by the policy),
/// Incomplete (missing evidence: ISLANDED, NO SOLUTION, INCOMPLETE) or Blocked (the intact base is not secure).
/// <paramref name="FirstFailedStep"/> is the order of the first failed step; null when verified.
/// </summary>
public sealed record CorrectiveSequenceResult(int Sequence, BranchOutage First, BranchOutage Second,
    CorrectiveProposal Proposal, string Status, string Outcome, int? FirstFailedStep, string TerminalDisposition,
    IReadOnlyList<SequenceStep> Steps);

/// <summary>Sequence coverage. Attempted: the first outage was applied. Completed: the final state was evaluated
/// (SECURE or VIOLATIONxN). Verified: SECURE. Incomplete: Outcome Incomplete or Blocked.</summary>
public sealed record CorrectiveSequenceCounts(int Requested, int Attempted, int Completed, int Verified, int Incomplete);

public sealed record CorrectiveStudyResult(string Status, string Outcome, string? Reason,
    IReadOnlyList<UnitSetpoint> InitialDispatch, SecurityState Intact,
    IReadOnlyList<CorrectiveSequenceResult> Sequences, CorrectiveSequenceCounts Counts,
    string Scheduling = CorrectiveActionPolicy.Scheduling,
    string Controls = "Fixed taps; enforced Q limits; incumbent single slack balances losses in every state; " +
        "reactive output is the result of each AC solve. No control acts after the second outage.",
    string Proposals = "Prescribed actions are replayed, not searched for. PlannedRedispatch uses the incumbent " +
        "DC minimum-change corrective redispatch as a candidate generator only; it does not find feasible corrective actions.",
    string DeferredActions = "Breaker and switching actions are not supported in this version: they need P2 identity " +
        "and grouping proof after each action. Only generator MW redispatch is applied.",
    string Reuse = "No state is reused between sequences: each sequence starts from a fresh copy of the intact case.");

/// <summary>
/// RC3 P5: ordered corrective N-1-1 as a sequence of auditable states. For each requested sequence, in input order and
/// sequentially: intact base, first outage, first AC solve, action proposal, eligibility and ramp validation, action
/// application, intermediate AC verification, second outage, final AC verification. A step runs only when every
/// earlier step completed; a failed step and every step after it stay in the result (the later ones Blocked). The AC
/// checks are the preventive study's verifier (<see cref="TransmissionSecurityRunner.VerifyState"/>). The legacy
/// screen (<see cref="ContingencyAnalysis.ScreenNMinus1Minus1Report"/>) is untouched.
/// </summary>
public static class CorrectiveSequenceRunner
{
    public const string IntactBase = "IntactBase", FirstOutage = "FirstOutage", FirstAcSolve = "FirstAcSolve",
        ActionProposal = "ActionProposal", ActionValidation = "ActionValidation", ActionApplication = "ActionApplication",
        IntermediateAcVerification = "IntermediateAcVerification", SecondOutage = "SecondOutage",
        FinalAcVerification = "FinalAcVerification";
    /// <summary>The prescribed step order.</summary>
    public static readonly IReadOnlyList<string> StepOrder = [IntactBase, FirstOutage, FirstAcSolve, ActionProposal,
        ActionValidation, ActionApplication, IntermediateAcVerification, SecondOutage, FinalAcVerification];
    private const string Completed = "Completed", Failed = "Failed", Blocked = "Blocked";

    public static CorrectiveStudyResult Run(GridModel model, CorrectiveSequenceRequest request)
    {
        Validate(model, request);
        UnitSetpoint[] initial = model.Generators.Select((g, i) => new UnitSetpoint(i, g.Bus, g.Pg)).ToArray();
        string? refusal = TransmissionSecurityRunner.EnvelopeRefusal(model);
        SecurityState intact = refusal is null
            ? TransmissionSecurityRunner.VerifyState(model, request.MinPu, request.MaxPu, PostFaultRating.RateA, null)
            : new SecurityState(null, "UNEVALUATED", refusal, null, null, null, [], null);
        var sequences = new CorrectiveSequenceResult[request.Sequences.Count];
        for (int k = 0; k < sequences.Length; k++)
            sequences[k] = RunSequence(model, request, k, intact);

        var counts = new CorrectiveSequenceCounts(sequences.Length,
            sequences.Count(s => s.Steps[1].Status != Blocked),
            sequences.Count(s => s.Steps[8].State is { } f && (f.Disposition == "SECURE" || f.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal))),
            sequences.Count(s => s.Outcome == "Verified"),
            sequences.Count(s => s.Outcome is "Incomplete" or "Blocked"));
        string outcome = intact.Disposition != "SECURE"
            ? (intact.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal) ? "Violations" : "Incomplete")
            : sequences.Any(s => s.Outcome == "Incomplete") ? "Incomplete"
            : sequences.Any(s => s.Outcome == "ActionRejected") ? "ActionRejected"
            : sequences.Any(s => s.Outcome == "Violations") ? "Violations" : "Verified";
        string? reason = intact.Disposition != "SECURE" ? "Intact base is not secure; no sequence can be verified." : null;
        return new(outcome == "Verified" ? "SECURE" : "NOT SECURE", outcome, reason, initial, intact, sequences, counts);
    }

    private static CorrectiveSequenceResult RunSequence(GridModel model, CorrectiveSequenceRequest request, int k,
        SecurityState intact)
    {
        CorrectiveSequence sequence = request.Sequences[k];
        var steps = new List<SequenceStep>(StepOrder.Count);
        int? failed = null;
        string terminal = "SECURE";
        string outcome = "Verified";

        // A step body runs only while nothing has failed; otherwise the step is recorded as Blocked.
        bool Open() => failed is null;
        void Block(string name) => steps.Add(new(steps.Count, name, Blocked,
            $"Not run: step {failed} ({StepOrder[failed!.Value]}) failed."));
        void Fail(string disposition, string result)
        {
            failed = steps.Count - 1;
            terminal = disposition;
            outcome = result;
        }
        static string Classify(SecurityState s) => s.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal) ? "Violations" : "Incomplete";
        SequenceStep Ac(string name, SecurityState state, string? reason = null) =>
            new(steps.Count, name, state.Disposition == "SECURE" ? Completed : Failed,
                state.Disposition == "SECURE" ? reason : $"{state.Disposition}: {state.Reason ?? "absolute limit(s) breached."}{(reason is null ? "" : " " + reason)}",
                State: state);

        // 0. Intact base (shared by every sequence; evaluated once from the case as given).
        steps.Add(new(0, IntactBase, intact.Disposition == "SECURE" ? Completed : Failed,
            intact.Disposition == "SECURE" ? null : $"{intact.Disposition}: {intact.Reason ?? "absolute limit(s) breached."}", State: intact));
        if (intact.Disposition != "SECURE") Fail(intact.Disposition, "Blocked");

        // 1. First outage, applied to a fresh copy of the intact case.
        GridModel? first = null;
        if (Open())
        {
            first = Without(model, sequence.First.Index);
            string? islands = Islands(first);
            steps.Add(new(1, FirstOutage, islands is null ? Completed : Failed, islands, OutagesApplied: [sequence.First.Index]));
            if (islands is not null) Fail("ISLANDED", "Incomplete");
        }
        else Block(FirstOutage);

        // 2. First AC solve against the first-stage (temporary emergency) rating.
        SecurityState? firstState = null;
        if (Open())
        {
            firstState = TransmissionSecurityRunner.VerifyState(first!, request.MinPu, request.MaxPu,
                request.FirstStageRating, sequence.First.Index);
            TemporaryLoading[] temporary = Temporary(first!, firstState, request);
            steps.Add(Ac(FirstAcSolve, firstState, firstState.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal)
                ? "Prohibited first-stage state: above the first-stage rating." : null) with { TemporaryEmergencyLoading = temporary });
            if (firstState.Disposition != "SECURE") Fail(firstState.Disposition, Classify(firstState));
        }
        else Block(FirstAcSolve);

        // 3. Action proposal.
        IReadOnlyList<GeneratorAction>? proposal = null;
        if (Open())
        {
            string? diagnostic = null;
            string? error = null;
            switch (sequence.Proposal)
            {
                case CorrectiveProposal.None: proposal = []; diagnostic = "No action proposed."; break;
                case CorrectiveProposal.Prescribed: proposal = sequence.Actions!.ToArray(); diagnostic = "Prescribed action replay."; break;
                default:
                    try
                    {
                        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(first!);
                        diagnostic = FormattableString.Invariant(
                            $"PlanCorrectiveRedispatch: {plan.Outcome}; moved {plan.MovedMw} MW; LP {plan.LpStatus?.ToString() ?? "not run"}; DC overloads before: {(plan.OverloadedBefore.Count == 0 ? "none" : string.Join(", ", plan.OverloadedBefore))}.");
                        if (plan.Outcome is RedispatchOutcome.NoDcModel or RedispatchOutcome.Infeasible)
                            error = "The candidate generator produced no proposal: " + plan.Outcome + ".";
                        else
                            proposal = Enumerable.Range(0, first!.Generators.Count)
                                .Where(i => plan.Model.Generators[i].Pg != first.Generators[i].Pg)
                                .Select(i => new GeneratorAction(i, first.Generators[i].Bus, plan.Model.Generators[i].Pg - first.Generators[i].Pg))
                                .ToArray();
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
                    {
                        error = "The candidate generator failed: " + ex.Message;
                    }
                    break;
            }
            steps.Add(new(3, ActionProposal, error is null ? Completed : Failed, error, Proposed: proposal,
                ProposalDiagnostic: diagnostic));
            if (error is not null) Fail("NO PROPOSAL", "ActionRejected");
        }
        else Block(ActionProposal);

        // 4. Eligibility, direction, setpoint, MW cap and ramp/time validation of the whole proposal.
        if (Open())
        {
            var (checks, refused) = request.Policy.Validate(first!, proposal!, firstState!);
            steps.Add(new(4, ActionValidation, refused is null ? Completed : Failed, refused, Checks: checks));
            if (refused is not null) Fail("ACTION REJECTED", "ActionRejected");
        }
        else Block(ActionValidation);

        // 5. Action application to a copy of the first post-outage state (first outage retained).
        GridModel? intermediate = null;
        if (Open())
        {
            Generator[] units = first!.Generators.ToArray();
            var changes = new List<SetpointChange>();
            foreach (GeneratorAction action in proposal!)
            {
                double before = units[action.Generator].Pg;
                units[action.Generator] = units[action.Generator] with { Pg = before + action.DeltaMw };
                changes.Add(new(action.Generator, action.Bus, before, units[action.Generator].Pg));
            }
            intermediate = first.CopyWith(generators: units);
            steps.Add(new(5, ActionApplication, Completed, null, OutagesApplied: [sequence.First.Index], Applied: changes));
        }
        else Block(ActionApplication);

        // 6. Intermediate AC verification: first outage applied, actions realised, intermediate rating.
        if (Open())
        {
            SecurityState state = TransmissionSecurityRunner.VerifyState(intermediate!, request.MinPu, request.MaxPu,
                request.IntermediateRating, sequence.First.Index);
            steps.Add(Ac(IntermediateAcVerification, state));
            if (state.Disposition != "SECURE") Fail(state.Disposition, Classify(state));
        }
        else Block(IntermediateAcVerification);

        // 7. Second outage, applied to a copy of the accepted intermediate state (first outage and setpoints retained).
        GridModel? final = null;
        if (Open())
        {
            final = Without(intermediate!, sequence.Second.Index);
            string? islands = Islands(final);
            steps.Add(new(7, SecondOutage, islands is null ? Completed : Failed, islands,
                OutagesApplied: [sequence.First.Index, sequence.Second.Index]));
            if (islands is not null) Fail("ISLANDED", "Incomplete");
        }
        else Block(SecondOutage);

        // 8. Final AC verification under the final rating; no post-second control.
        if (Open())
        {
            SecurityState state = TransmissionSecurityRunner.VerifyState(final!, request.MinPu, request.MaxPu,
                request.FinalRating, sequence.Second.Index);
            steps.Add(Ac(FinalAcVerification, state));
            if (state.Disposition != "SECURE") Fail(state.Disposition, Classify(state));
        }
        else Block(FinalAcVerification);

        return new(k, sequence.First, sequence.Second, sequence.Proposal, outcome == "Verified" ? "SECURE" : "NOT SECURE",
            outcome, failed, terminal, steps);
    }

    private static GridModel Without(GridModel model, int branch)
    {
        Branch[] branches = model.Branches.ToArray();
        branches[branch] = branches[branch] with { InService = false };
        return model.CopyWith(branches: branches);
    }

    private static string? Islands(GridModel model)
    {
        try
        {
            int count = DistributionFactors.FindIslands(model).Count;
            return count == 1 ? null : FormattableString.Invariant($"ISLANDED: the outage splits the network into {count} islands; the state is not solved.");
        }
        catch (InvalidOperationException ex)
        {
            return "ISLANDED: the network's islands could not be classified: " + ex.Message;
        }
    }

    private static TemporaryLoading[] Temporary(GridModel model, SecurityState state, CorrectiveSequenceRequest request)
    {
        var rows = new List<TemporaryLoading>();
        foreach (LimitEvidence limit in state.Limits)
        {
            if (limit.Kind is not ("BranchFrom" or "BranchTo") || limit.Upper is not double stage) continue;
            Branch branch = model.Branches[limit.Index];
            double continuous = DcScopf.PostRating(branch, request.IntermediateRating);
            if (limit.Observed > continuous && limit.Observed <= stage)
                rows.Add(new(limit.Index, limit.Kind == "BranchFrom" ? "From" : "To", limit.Observed, stage,
                    limit.Source ?? "", continuous, Source(branch, request.IntermediateRating)));
        }
        return rows.ToArray();
    }

    private static string Source(Branch branch, PostFaultRating rating) => TransmissionSecurityRunner.RatingSource(branch, rating);

    private static void Validate(GridModel model, CorrectiveSequenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.MinPu) || !double.IsFinite(request.MaxPu) || request.MinPu <= 0 || request.MaxPu < request.MinPu)
            throw new ArgumentException("Voltage bounds must be finite, positive and ordered.");
        if (!Enum.IsDefined(request.FirstStageRating) || !Enum.IsDefined(request.IntermediateRating) || !Enum.IsDefined(request.FinalRating))
            throw new ArgumentException("Invalid stage rating.");
        if (model.Generators.Any(g => double.IsNaN(g.Pmin) || double.IsNaN(g.Pmax) ||
            double.IsNaN(g.Qmin) || double.IsNaN(g.Qmax) || g.Pmin > g.Pmax || g.Qmin > g.Qmax))
            throw new ArgumentException("Generator bounds must be ordered and cannot be NaN.");
        if (request.Policy is null) throw new ArgumentException("An action policy is required.");
        request.Policy.Check(model);
        if (request.Sequences is null || request.Sequences.Count == 0)
            throw new ArgumentException("At least one explicit outage sequence is required.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < request.Sequences.Count; k++)
        {
            CorrectiveSequence s = request.Sequences[k] ?? throw new ArgumentException($"Sequence {k} is null.");
            if (s.First is null || s.Second is null) throw new ArgumentException($"Sequence {k} needs a first and a second outage.");
            foreach (BranchOutage o in new[] { s.First, s.Second })
            {
                if (o.Index < 0 || o.Index >= model.Branches.Count)
                    throw new ArgumentException($"Sequence {k}: outage index {o.Index} is outside the model.");
                Branch branch = model.Branches[o.Index];
                if (!branch.InService || branch.From != o.From || branch.To != o.To)
                    throw new ArgumentException($"Sequence {k}: outage {o.Index} is inactive or its endpoints do not match.");
            }
            if (s.First.Index == s.Second.Index)
                throw new ArgumentException($"Sequence {k}: the two outages must be distinct branches.");
            if (!Enum.IsDefined(s.Proposal)) throw new ArgumentException($"Sequence {k}: unknown proposal.");
            if ((s.Proposal == CorrectiveProposal.Prescribed) != (s.Actions is not null))
                throw new ArgumentException($"Sequence {k}: actions are required for a prescribed proposal and refused otherwise.");
            if (s.Actions is not null && (s.Actions.Count == 0 || s.Actions.Any(a => a is null || !double.IsFinite(a.DeltaMw))))
                throw new ArgumentException($"Sequence {k}: prescribed actions must be a non-empty list of finite MW changes.");
            string key = FormattableString.Invariant($"{s.First.Index}>{s.Second.Index}|{s.Proposal}|") +
                string.Join(";", (s.Actions ?? []).Select(a => FormattableString.Invariant($"{a.Generator}@{a.Bus}:{a.DeltaMw:R}")));
            if (!keys.Add(key)) throw new ArgumentException($"Sequence {k} repeats an earlier sequence.");
        }
    }
}
