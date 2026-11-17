using System.Globalization;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>A transfer participant: an in-service unit bound by its index in <see cref="GridModel.Generators"/> and
/// its expected bus ID (the same index-plus-endpoint identity contract as <see cref="BranchOutage"/>), with a positive
/// participation weight.</summary>
public sealed record TransferParticipant(int Index, int Bus, double Weight);

/// <summary>A monitored interface branch. Its signed contribution is <c>Sign * PFromMw</c> (the active power leaving
/// the branch's From terminal), so Sign = +1 counts From-to-To flow as positive and -1 counts To-to-From.</summary>
public sealed record InterfaceBranch(int Index, int From, int To, int Sign = 1);

/// <summary>A monitored interface: the sum of its branches' signed From-terminal MW. An optional
/// <see cref="LimitMw"/> caps that signed sum in every evaluated state (intact and each outage).</summary>
public sealed record TransferInterface(string Name, IReadOnlyList<InterfaceBranch> Branches, double? LimitMw = null);

/// <summary>
/// RC3 P4 directional transfer request. Positive transfer raises the source group's generation and lowers the sink
/// group's, each by transfer x normalised weight (weights summed in declared order). The incumbent single slack
/// balances losses and any loss change. Voltage band, intact RATE_A and post-fault rating follow the preventive study.
/// </summary>
public sealed record TransferStudyRequest(
    IReadOnlyList<TransferParticipant> Sources, IReadOnlyList<TransferParticipant> Sinks,
    IReadOnlyList<BranchOutage> Outages, double MinPu, double MaxPu, PostFaultRating PostFaultRating,
    TransferSearchOptions Search, IReadOnlyList<TransferInterface>? Interfaces = null);

/// <summary>Normalised participation of one unit.</summary>
public sealed record TransferShare(string Group, int Index, int Bus, double Weight, double Share);

/// <summary>A participant's scheduled setpoint at one transfer level, with its P range recorded before solving.</summary>
public sealed record TransferSchedule(int Index, int Bus, string Group, double ScheduledMw, double Pmin, double Pmax);

/// <summary>Runner evidence behind one trial. <see cref="SlackBalancingMw"/> is the slack unit's realised P minus its
/// scheduled P in the intact state (loss balancing, reported separately from the requested transfer).
/// <see cref="InterfaceMw"/> is each interface's signed MW in the intact state.</summary>
public sealed record TransferTrialDetail(IReadOnlyList<TransferSchedule> Schedule, SecurityState Intact,
    IReadOnlyList<SecurityState> Outages, double? SlackBalancingMw, IReadOnlyList<double?> InterfaceMw, int Solves);

/// <summary>A compact trial row (full states are kept for the reference and the two final endpoints only).</summary>
public sealed record TransferTrialRow(int Sequence, string Phase, double TransferMw, TransferClass Class,
    TransferLimit? FirstLimit, int ViolationCount, IReadOnlyList<string> Dispositions, string ControlSignature,
    double? SlackBalancingMw, IReadOnlyList<double?> InterfaceMw, IReadOnlyList<double?> InterfaceChangeMw, string? Reason);

/// <summary>A fully reported trial: the reference or a re-solved bracket endpoint.</summary>
public sealed record TransferEndpoint(double TransferMw, TransferClass Class, string? Reason,
    IReadOnlyList<TransferSchedule> Schedule, SecurityState Intact, IReadOnlyList<SecurityState> Outages,
    IReadOnlyList<TransferLimit> Violations, string ControlSignature, double? SlackBalancingMw,
    IReadOnlyList<double?> InterfaceMw, IReadOnlyList<double?> InterfaceChangeMw);

public sealed record TransferStudyResult(
    string Status, string Outcome, string? Reason, IReadOnlyList<TransferQualification> Qualifications,
    double? LowerMw, double? UpperMw, TransferClass? UpperClass, double? BracketWidthMw,
    TransferLimit? FirstLimit, TransferEndpoint? Reference, TransferEndpoint? Lower, TransferEndpoint? Upper,
    IReadOnlyList<TransferTrialRow> Trials, TransferAccounting? Accounting, IReadOnlyList<TransferShare> Participation,
    string BalancingRule = "Generation shift: sources raised and sinks lowered by transfer x normalised weight; the incumbent single slack balances losses.",
    string Controls = "Fixed taps; enforced Q limits; the same solver options for every trial and state.",
    string SearchPolicy = "Coarse walk from zero, non-monotone probes, bisection to the declared MW tolerance, clean re-solve of both endpoints.");

/// <summary>
/// RC3 P4 AC-verified directional transfer capability. Each trial is built from the same reference model by changing
/// only the participants' scheduled P; the intact network and every required outage are then verified with the
/// preventive study's absolute AC verifier (<see cref="TransmissionSecurityRunner"/>). The search itself is
/// <see cref="TransferSearch"/>.
/// </summary>
public static class TransferStudyRunner
{
    public static TransferStudyResult Run(GridModel model, TransferStudyRequest request)
    {
        TransferShare[] shares = Validate(model, request);
        string? unsupported = TransmissionSecurityRunner.Unsupported(model);
        if (unsupported is not null)
            return new("INCOMPLETE", "Unsupported", unsupported, [], null, null, null, null, null, null, null, null, [], null, shares);

        var factors = DistributionFactors.Build(model);
        var islanding = request.Outages.Where(o => factors.OutageIslands(o.Index)).Select(o => o.Index).ToHashSet();
        var verify = new TransmissionSecurityRequest(request.Outages, request.MinPu, request.MaxPu, request.PostFaultRating);
        int slackBus = model.Buses.Single(b => b.Type == BusType.Slack).Id;
        int[] slackUnits = Enumerable.Range(0, model.Generators.Count).Where(g => model.Generators[g].Bus == slackBus).ToArray();
        IReadOnlyList<TransferInterface> interfaces = request.Interfaces ?? [];

        TransferEvaluation Evaluate(double mw) => EvaluateTrial(model, request, verify, shares, islanding,
            slackUnits.Length == 1 ? slackUnits[0] : null, interfaces, mw);

        TransferSearchOutcome search = TransferSearch.Run(Evaluate, request.Search);
        double?[] referenceInterface = search.Reference.Evaluation.Detail!.InterfaceMw.ToArray();
        IReadOnlyList<double?> Change(IReadOnlyList<double?> values) =>
            values.Select((v, i) => v is double x && referenceInterface[i] is double r ? x - r : (double?)null).ToArray();

        TransferEndpoint Endpoint(TransferSearchTrial t)
        {
            TransferTrialDetail d = t.Evaluation.Detail!;
            return new(t.TransferMw, t.Evaluation.Class, t.Evaluation.Reason, d.Schedule, d.Intact, d.Outages,
                t.Evaluation.Violations, t.Evaluation.ControlSignature, d.SlackBalancingMw, d.InterfaceMw, Change(d.InterfaceMw));
        }
        TransferTrialRow[] rows = search.Trials.Select(t =>
        {
            TransferTrialDetail d = t.Evaluation.Detail!;
            return new TransferTrialRow(t.Sequence, t.Phase, t.TransferMw, t.Evaluation.Class,
                t.Evaluation.Violations.FirstOrDefault(), t.Evaluation.Violations.Count,
                [d.Intact.Disposition, .. d.Outages.Select(o => o.Disposition)], t.Evaluation.ControlSignature,
                d.SlackBalancingMw, d.InterfaceMw, Change(d.InterfaceMw), t.Evaluation.Reason);
        }).ToArray();

        string? reason = search.Outcome switch
        {
            "NoAdmissibleStart" => "The zero-transfer reference already violates an absolute limit; no transfer headroom is reported.",
            "ReferenceIncomplete" => "The zero-transfer reference lacks complete AC evidence: " + search.Reference.Evaluation.Reason,
            "UnknownAbove" => "The first trial above the verified lower bound has no complete AC evidence; it is not a physical limit.",
            "BudgetExhausted" => "The trial budget ended before a violation or the cap; the lower bound is verified, no limit was found.",
            "AtLeastMax" => "Every sampled trial up to the cap is feasible: the capability is at least the cap; no upper bound was established.",
            _ => null
        };
        TransferSearchTrial? lower = search.Lower, upper = search.Upper;
        return new(search.Status, search.Outcome, reason, search.Qualifications,
            lower?.TransferMw, upper?.TransferMw, upper?.Evaluation.Class,
            lower is not null && upper is not null ? upper.TransferMw - lower.TransferMw : null,
            upper?.Evaluation.Class == TransferClass.Violation ? upper.Evaluation.Violations[0]
                : search.Outcome == "NoAdmissibleStart" ? search.Reference.Evaluation.Violations[0] : null,
            Endpoint(search.Reference), lower is null ? null : Endpoint(lower), upper is null ? null : Endpoint(upper),
            rows, search.Accounting, shares);
    }

    private static TransferEvaluation EvaluateTrial(GridModel model, TransferStudyRequest request,
        TransmissionSecurityRequest verify, TransferShare[] shares, HashSet<int> islanding, int? slackUnit,
        IReadOnlyList<TransferInterface> interfaces, double mw)
    {
        // 1. Schedule: only the participants' P changes, from the same reference model, in declared order.
        Generator[] generators = model.Generators.ToArray();
        var schedule = new TransferSchedule[shares.Length];
        var violations = new List<TransferLimit>();
        for (int s = 0; s < shares.Length; s++)
        {
            TransferShare share = shares[s];
            Generator unit = model.Generators[share.Index];
            double delta = mw * share.Share;
            double pg = share.Group == "source" ? unit.Pg + delta : unit.Pg - delta;
            generators[share.Index] = unit with { Pg = pg };
            schedule[s] = new(share.Index, share.Bus, share.Group, pg, unit.Pmin, unit.Pmax);
            // A participant is never clipped: a schedule outside its range is a known violation of this transfer.
            if (pg < unit.Pmin) violations.Add(new(null, "ScheduledGeneratorP", share.Index, "MW", pg, unit.Pmin, pg - unit.Pmin, "lower", share.Group));
            if (pg > unit.Pmax) violations.Add(new(null, "ScheduledGeneratorP", share.Index, "MW", pg, unit.Pmax, unit.Pmax - pg, "upper", share.Group));
        }
        if (violations.Count > 0)
        {
            const string why = "A participant schedule is outside its P range; the trial was not solved.";
            SecurityState skipped = TransmissionSecurityRunner.Missing(null, why);
            SecurityState[] none = request.Outages.Select(o => TransmissionSecurityRunner.Missing(o.Index, why)).ToArray();
            var detail0 = new TransferTrialDetail(schedule, skipped, none, null, interfaces.Select(_ => (double?)null).ToArray(), 0);
            return new(TransferClass.Violation, violations, "", why, Fingerprint(TransferClass.Violation, violations, detail0), detail0);
        }

        // 2. The same absolute AC verifier as the preventive study, intact first then each outage in request order.
        GridModel candidate = model.CopyWith(generators: generators);
        int solves = 0;
        (SecurityState State, double?[] Interface) Solve(GridModel state, int? outage)
        {
            PowerFlowResult? raw = null;
            solves++;
            SecurityState verified = TransmissionSecurityRunner.Verify(state, verify, outage, r => raw = r);
            return AddInterfaces(verified, raw, state, interfaces);
        }
        var (intact, intactInterface) = Solve(candidate, null);
        var outages = new SecurityState[request.Outages.Count];
        for (int i = 0; i < outages.Length; i++)
        {
            BranchOutage o = request.Outages[i];
            if (islanding.Contains(o.Index))
            {
                outages[i] = TransmissionSecurityRunner.Missing(o.Index, "Required outage islands the network; not verified.", "ISLANDED");
                continue;
            }
            Branch[] branches = candidate.Branches.ToArray();
            branches[o.Index] = branches[o.Index] with { InService = false };
            outages[i] = Solve(candidate.CopyWith(branches: branches), o.Index).State;
        }

        // 3. Classify: missing evidence dominates; known violations are kept in stable order either way.
        SecurityState[] states = [intact, .. outages];
        foreach (SecurityState state in states)
            foreach (LimitEvidence l in state.Limits.Where(l => l.Violated))
            {
                bool upper = l.UpperMargin < -l.Tolerance;
                violations.Add(new(state.OutageIndex, l.Kind, l.Index, l.Units, l.Observed,
                    upper ? l.Upper!.Value : l.Lower!.Value, upper ? l.UpperMargin!.Value : l.LowerMargin!.Value,
                    upper ? "upper" : "lower", l.Source));
            }
        bool complete = states.All(s => s.Disposition == "SECURE" || s.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal));
        TransferClass cls = !complete ? TransferClass.Unknown : violations.Count > 0 ? TransferClass.Violation : TransferClass.Feasible;
        string? reason = complete ? null : string.Join(" ", states.Where(s => s.Disposition != "SECURE" &&
                !s.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal))
            .Select(s => $"{(s.OutageIndex is int x ? "outage " + x.ToString(CultureInfo.InvariantCulture) : "intact")}: {s.Disposition} ({s.Reason})"));

        double? slack = slackUnit is int u && intact.Limits.FirstOrDefault(l => l.Kind == "GeneratorPAdjustment" && l.Index == u) is { } adj
            ? adj.Observed : null;
        var detail = new TransferTrialDetail(schedule, intact, outages, slack, intactInterface, solves);
        string signature = string.Join(";", states.Select(s => Signature(model, s)).Where(s => s.Length > 0));
        return new(cls, violations, signature, reason, Fingerprint(cls, violations, detail), detail);
    }

    private static (SecurityState, double?[]) AddInterfaces(SecurityState state, PowerFlowResult? raw, GridModel model,
        IReadOnlyList<TransferInterface> interfaces)
    {
        var flows = new double?[interfaces.Count];
        bool usable = raw is not null && (state.Disposition == "SECURE" || state.Disposition.StartsWith("VIOLATIONx", StringComparison.Ordinal));
        if (!usable || interfaces.Count == 0) return (state, flows);
        var extra = new List<LimitEvidence>();
        for (int i = 0; i < interfaces.Count; i++)
        {
            double sum = 0;
            foreach (InterfaceBranch b in interfaces[i].Branches)
                sum += b.Sign * raw!.LineFlows[b.Index].PFromMw;   // the verifier has already matched row identity
            flows[i] = sum;
            if (interfaces[i].LimitMw is double limit)
                extra.Add(new("InterfaceMw", i, "MW", sum, null, limit, null, limit - sum, interfaces[i].Name));
        }
        if (extra.Count == 0) return (state, flows);
        LimitEvidence[] limits = [.. state.Limits, .. extra];
        int violated = limits.Count(l => l.Violated);
        return (state with { Limits = limits, Disposition = violated > 0 ? $"VIOLATIONx{violated}" : "SECURE" }, flows);
    }

    /// <summary>Buses whose solved type differs from the model's (a PV-to-PQ switch under enforced Q limits).</summary>
    private static string Signature(GridModel model, SecurityState state)
    {
        if (state.SolvedBusTypes is null || state.SolvedBusTypes.Count != model.Buses.Count) return "";
        var changed = new List<string>();
        for (int i = 0; i < model.Buses.Count; i++)
        {
            BusType declared = model.Buses[i].Type == BusType.Pv && !model.Generators.Any(g => g.Bus == model.Buses[i].Id)
                ? BusType.Pq : model.Buses[i].Type;
            if (state.SolvedBusTypes[i] != declared)
                changed.Add(string.Create(CultureInfo.InvariantCulture, $"{model.Buses[i].Id}={state.SolvedBusTypes[i]}"));
        }
        if (changed.Count == 0) return "";
        string where = state.OutageIndex is int o ? "outage " + o.ToString(CultureInfo.InvariantCulture) : "intact";
        return where + ":" + string.Join(",", changed);
    }

    /// <summary>Exact digest of the trial evidence: dispositions and the raw bits of every observed value.</summary>
    private static string Fingerprint(TransferClass cls, IReadOnlyList<TransferLimit> violations, TransferTrialDetail detail)
    {
        var sb = new StringBuilder();
        sb.Append(cls).Append('|').Append(violations.Count);
        static string Bits(double? v) => v is double x ? BitConverter.DoubleToInt64Bits(x).ToString("x16", CultureInfo.InvariantCulture) : "-";
        foreach (TransferSchedule s in detail.Schedule) sb.Append('|').Append(Bits(s.ScheduledMw));
        foreach (SecurityState s in detail.Outages.Prepend(detail.Intact))
        {
            sb.Append("|S").Append(s.Disposition).Append(':').Append(s.SolverStatus).Append(':').Append(Bits(s.ResidualPu)).Append(':').Append(Bits(s.LossMw));
            foreach (LimitEvidence l in s.Limits) sb.Append(',').Append(l.Kind).Append(l.Index.ToString(CultureInfo.InvariantCulture)).Append('=').Append(Bits(l.Observed));
        }
        foreach (double? f in detail.InterfaceMw) sb.Append("|I").Append(Bits(f));
        return sb.ToString();
    }

    private static TransferShare[] Validate(GridModel model, TransferStudyRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Search);
        request.Search.Validate();
        if (!double.IsFinite(request.MinPu) || !double.IsFinite(request.MaxPu) || request.MinPu <= 0 || request.MaxPu < request.MinPu)
            throw new ArgumentException("Voltage bounds must be finite, positive and ordered.");
        if (!Enum.IsDefined(request.PostFaultRating))
            throw new ArgumentException("Invalid post-fault rating.");
        if (model.Generators.Any(g => double.IsNaN(g.Pmin) || double.IsNaN(g.Pmax) ||
            double.IsNaN(g.Qmin) || double.IsNaN(g.Qmax) || g.Pmin > g.Pmax || g.Qmin > g.Qmax))
            throw new ArgumentException("Generator bounds must be ordered and cannot be NaN.");
        if (request.Outages is null || request.Outages.Count == 0)
            throw new ArgumentException("At least one explicit branch outage is required.");
        var seenOutage = new HashSet<int>();
        foreach (BranchOutage o in request.Outages)
        {
            if (o is null || o.Index < 0 || o.Index >= model.Branches.Count || !seenOutage.Add(o.Index))
                throw new ArgumentException("Outage indices must be unique and within the model.");
            Branch b = model.Branches[o.Index];
            if (!b.InService || b.From != o.From || b.To != o.To)
                throw new ArgumentException($"Outage {o.Index} is inactive or its endpoints do not match.");
        }
        if (request.Sources is null || request.Sinks is null || request.Sources.Count == 0 || request.Sinks.Count == 0)
            throw new ArgumentException("Transfer needs at least one source and one sink unit.");
        var seen = new HashSet<int>();
        var shares = new List<TransferShare>();
        foreach (var (group, members) in new[] { ("source", request.Sources), ("sink", request.Sinks) })
        {
            double total = 0;
            foreach (TransferParticipant p in members)
            {
                if (p is null || p.Index < 0 || p.Index >= model.Generators.Count)
                    throw new ArgumentException($"Transfer {group} unit index is outside the in-service unit list.");
                if (!seen.Add(p.Index))
                    throw new ArgumentException($"Unit {p.Index} is repeated or appears in both source and sink groups.");
                if (model.Generators[p.Index].Bus != p.Bus)
                    throw new ArgumentException($"Unit {p.Index} is at bus {model.Generators[p.Index].Bus}, not the declared bus {p.Bus}.");
                if (!double.IsFinite(p.Weight) || p.Weight <= 0)
                    throw new ArgumentException($"Unit {p.Index} weight must be finite and positive.");
                if (!double.IsFinite(model.Generators[p.Index].Pg))
                    throw new ArgumentException($"Unit {p.Index} has a non-finite scheduled P.");
                total += p.Weight;
            }
            if (!double.IsFinite(total) || total <= 0)
                throw new ArgumentException($"Transfer {group} weights must have a finite positive total.");
            foreach (TransferParticipant p in members)
                shares.Add(new(group, p.Index, p.Bus, p.Weight, p.Weight / total));
        }
        if (request.Interfaces is not null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (TransferInterface itf in request.Interfaces)
            {
                if (itf is null || string.IsNullOrWhiteSpace(itf.Name) || !names.Add(itf.Name))
                    throw new ArgumentException("Interfaces need unique, non-empty names.");
                if (itf.Branches is null || itf.Branches.Count == 0)
                    throw new ArgumentException($"Interface {itf.Name} needs at least one branch.");
                if (itf.LimitMw is double limit && !double.IsFinite(limit))
                    throw new ArgumentException($"Interface {itf.Name} limit must be finite.");
                var members = new HashSet<int>();
                foreach (InterfaceBranch b in itf.Branches)
                {
                    if (b is null || b.Index < 0 || b.Index >= model.Branches.Count || !members.Add(b.Index))
                        throw new ArgumentException($"Interface {itf.Name} branch indices must be unique and within the model.");
                    Branch branch = model.Branches[b.Index];
                    if (!branch.InService || branch.From != b.From || branch.To != b.To || b.Sign is not (1 or -1))
                        throw new ArgumentException($"Interface {itf.Name} branch {b.Index} is inactive, its endpoints do not match or its sign is not +1/-1.");
                }
            }
        }
        return shares.ToArray();
    }
}
