using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Restoration;

/// <summary>A branch identity checked against the case: zero-based model branch index plus its expected From and To bus
/// IDs, so parallel circuits stay distinct (the preventive study's identity rule).</summary>
public sealed record RestorationBranch(int Index, int From, int To);

/// <summary>One candidate switching action: close <paramref name="Branch"/>. <paramref name="Minutes"/> is the time the
/// action takes to perform; the state before it persists for that long, so it weights the unserved load it leaves.</summary>
public sealed record RestorationAction(string Id, RestorationBranch Branch, double Minutes);

/// <summary>An explicit ordering constraint: action <paramref name="Before"/> must be performed before <paramref name="After"/>.</summary>
public sealed record PrecedenceConstraint(string Before, string After);

/// <summary>
/// A restoration study (RC3 P8). Every candidate branch starts open, whatever its stored state, and an admissible
/// sequence closes every candidate exactly once, in an order that respects every <paramref name="Precedence"/>
/// constraint, such that the initial state and the state after every action pass the preventive study's absolute AC
/// verifier (voltage band [<paramref name="MinPu"/>, <paramref name="MaxPu"/>] on every live bus, branch MVA at both
/// ends against RATE_A, each unit's realised P and Q against its own range). <paramref name="MaxNodes"/> bounds the
/// number of search nodes (prefix extensions) the enumeration may expand.
/// </summary>
public sealed record RestorationRequest(IReadOnlyList<RestorationAction> Actions, double MinPu, double MaxPu,
    IReadOnlyList<PrecedenceConstraint>? Precedence = null, long MaxNodes = RestorationStudy.DefaultMaxNodes);

/// <summary>One step of the reported sequence. <paramref name="UnservedBeforeMw"/> persists for the action's minutes, so
/// <paramref name="StepEnergyMwh"/> = UnservedBeforeMw * minutes / 60. <paramref name="State"/> is the verified state after
/// the action, with branch and unit indices in the ORIGINAL model numbering.</summary>
public sealed record RestorationStep(int Step, string Action, RestorationBranch Branch, double StartMinute, double EndMinute,
    double UnservedBeforeMw, double UnservedAfterMw, double StepEnergyMwh, IReadOnlyList<int> EnergisedBuses, SecurityState State);

/// <summary>One evaluated switching state (a set of closed candidates; the state does not depend on the order that
/// reached it, so each set is solved once).</summary>
public sealed record RestorationStateSummary(IReadOnlyList<string> Closed, double UnservedMw, int LiveBuses,
    string Disposition, string? Reason, int Violations, bool Admissible, IReadOnlyList<LimitEvidence> ViolatedLimits);

/// <summary>Search accounting. A state is unverified when its evaluation produced no complete verdict (NO SOLUTION,
/// INCOMPLETE): it is not admissible, and the optimum proof is conditional on it unless the bound excludes it.</summary>
public sealed record RestorationSearch(long NodesExpanded, long MaxNodes, bool BudgetExhausted, int StatesEvaluated,
    int SecureStates, int ViolationStates, int UnverifiedStates, long PrunedInfeasible, long PrunedByBound,
    long CompleteSequencesFound, double? UnverifiedBoundMwh);

/// <summary>
/// The result. <paramref name="Status"/>: OPTIMAL (a proven optimum among all admissible sequences), FEASIBLE (an
/// admissible, fully verified sequence; optimality not proven), INFEASIBLE (proven: no admissible sequence exists) or
/// UNRESOLVED (no admissible sequence found and none proven impossible, or the case is outside the envelope). The
/// objective is the unserved energy during restoration in MWh. <paramref name="LowerBoundMwh"/> is a valid lower bound
/// on the objective of every admissible sequence; <paramref name="UpperBoundMwh"/> is the best verified sequence's.
/// </summary>
public sealed record RestorationStudyResult(string Status, string Outcome, string? Reason,
    double TotalLoadMw, double InitialUnservedMw, double FinalUnservedMw,
    double? ObjectiveMwh, double? LowerBoundMwh, double? UpperBoundMwh, double? RelaxedLowerBoundMwh,
    SecurityState? InitialState, IReadOnlyList<RestorationStep> Sequence,
    IReadOnlyList<RestorationStateSummary> States, RestorationSearch Search,
    string Objective = RestorationStudy.ObjectiveText, string Controls = RestorationStudy.ControlsText);

/// <summary>
/// RC3 P8 restoration package: deterministic finite enumeration of admissible switching sequences with each step
/// verified by the preventive study's AC verifier (<see cref="TransmissionSecurityRunner.VerifyState"/>). It is a
/// separate study: <see cref="RestorationSequencer"/> (the <c>restore</c> verb) is untouched and is not called.
/// <para>
/// Search. Depth-first over the candidate actions in input order (so the first optimum found in lexicographic input
/// order is the one reported). A prefix is extended only by an action whose precedence predecessors are all closed. The
/// new switching state is verified (memoised by the set of closed actions); an inadmissible state prunes every
/// sequence through it. A branch-and-bound cut drops a prefix whose cost plus the RELAXED cost-to-go (the exact optimum
/// of the same problem without the AC state constraints, by dynamic programming over subsets: a valid lower bound)
/// is not below the incumbent. The relaxation needs only connectivity, never a solve.
/// </para>
/// <para>
/// Proof. OPTIMAL needs the enumeration to finish within <see cref="RestorationRequest.MaxNodes"/> and every unverified
/// state met to be excluded by the bound (its prefix cost plus relaxed cost-to-go is at least the optimum). Otherwise a
/// found sequence is FEASIBLE with lower bound = the relaxed optimum (budget exhausted) or the least bound over the
/// unverified states (enumeration complete).
/// </para>
/// </summary>
public static class RestorationStudy
{
    /// <summary>The candidate cap: 2^12 states and 12! orders bound the worst-case enumeration.</summary>
    public const int MaxActions = 12;
    public const long DefaultMaxNodes = 100_000;
    public const string ObjectiveText =
        "Unserved energy during restoration, MWh: sum over steps of (net demand of de-energised buses before the action, MW) x (action minutes) / 60.";
    public const string ControlsText =
        "Fixed taps; enforced Q limits; incumbent single slack balances losses; units at an energised bus run at their stated setpoint; branch MVA against RATE_A.";

    public static readonly IReadOnlyList<string> StepOrder =
    [
        "ValidateRequest", "EnvelopeCheck", "RelaxedBound", "VerifyInitialState", "EnumerateSequences", "Classify"
    ];

    public static RestorationStudyResult Run(GridModel model, RestorationRequest request)
    {
        Validate(model, request);
        int n = request.Actions.Count;
        int full = (1 << n) - 1;
        double[] minutes = request.Actions.Select(a => a.Minutes).ToArray();
        int[] predecessors = Predecessors(request);
        var candidateIndex = new Dictionary<int, int>();
        for (int a = 0; a < n; a++) candidateIndex[request.Actions[a].Branch.Index] = a;

        Bus[] buses = model.Buses.ToArray();
        double totalLoad = 0;
        foreach (Bus b in buses) totalLoad += Demand(b);
        int slack = buses.First(b => b.Type == BusType.Slack).Id;

        // Connectivity-only unserved demand of every switching state (no solve).
        var unserved = new double[full + 1];
        var liveOf = new HashSet<int>[full + 1];
        for (int mask = 0; mask <= full; mask++)
        {
            liveOf[mask] = Live(model, candidateIndex, mask, slack);
            double u = 0;
            foreach (Bus b in buses) if (!liveOf[mask].Contains(b.Id)) u += Demand(b);
            unserved[mask] = u;
        }

        // Envelope: the target network (every candidate closed) must be inside the preventive verifier's envelope.
        Branch[] target = model.Branches.ToArray();
        foreach (RestorationAction a in request.Actions)
            target[a.Branch.Index] = target[a.Branch.Index] with { InService = true };
        string? refusal = TransmissionSecurityRunner.EnvelopeRefusal(model.CopyWith(branches: target));

        // Relaxed cost-to-go (precedence kept, AC state constraints dropped): exact DP over subsets, descending.
        var relaxed = new double[full + 1];
        for (int mask = full; mask >= 0; mask--)
        {
            if (mask == full) { relaxed[mask] = 0; continue; }
            double best = double.PositiveInfinity;
            for (int a = 0; a < n; a++)
            {
                int bit = 1 << a;
                if ((mask & bit) != 0 || (predecessors[a] & ~mask) != 0) continue;
                double c = unserved[mask] * minutes[a] / 60.0 + relaxed[mask | bit];
                if (c < best) best = c;
            }
            relaxed[mask] = best;
        }

        var empty = new RestorationSearch(0, request.MaxNodes, false, 0, 0, 0, 0, 0, 0, 0, null);
        if (refusal is not null)
            return new("UNRESOLVED", "Unsupported", refusal, totalLoad, unserved[0], unserved[full],
                null, Finite(relaxed[0]), null, Finite(relaxed[0]), null, [], [], empty);

        var memo = new Dictionary<int, (SecurityState State, bool Admissible, bool Unverified)>();
        (SecurityState State, bool Admissible, bool Unverified) Evaluate(int mask)
        {
            if (memo.TryGetValue(mask, out var hit)) return hit;
            SecurityState s = VerifyMask(model, request, candidateIndex, mask, liveOf[mask]);
            bool admissible = s.Disposition == "SECURE";
            bool unverified = !admissible && !s.Disposition.StartsWith("VIOLATION", StringComparison.Ordinal);
            return memo[mask] = (s, admissible, unverified);
        }

        var initial = Evaluate(0);
        long nodes = 0, prunedInfeasible = 0, prunedBound = 0, complete = 0;
        bool exhausted = false;
        double bestCost = double.PositiveInfinity;
        int[]? bestOrder = null;
        double unverifiedBound = double.PositiveInfinity;
        if (initial.Unverified) unverifiedBound = relaxed[0];
        var prefix = new int[n];

        void Search(int depth, int mask, double cost)
        {
            if (mask == full)
            {
                complete++;
                if (cost < bestCost) { bestCost = cost; bestOrder = prefix[..n]; }
                return;
            }
            for (int a = 0; a < n; a++)
            {
                int bit = 1 << a;
                if ((mask & bit) != 0 || (predecessors[a] & ~mask) != 0) continue;
                if (++nodes > request.MaxNodes) { nodes = request.MaxNodes; exhausted = true; return; }
                double next = cost + unserved[mask] * minutes[a] / 60.0;
                int m2 = mask | bit;
                if (next + relaxed[m2] >= bestCost) { prunedBound++; continue; }
                var state = Evaluate(m2);
                if (!state.Admissible)
                {
                    prunedInfeasible++;
                    if (state.Unverified) unverifiedBound = Math.Min(unverifiedBound, next + relaxed[m2]);
                    continue;
                }
                prefix[depth] = a;
                Search(depth + 1, m2, next);
                if (exhausted) return;
            }
        }
        if (initial.Admissible) Search(0, 0, 0.0);

        int secure = memo.Values.Count(v => v.Admissible);
        int unverifiedCount = memo.Values.Count(v => v.Unverified);
        var search = new RestorationSearch(nodes, request.MaxNodes, exhausted, memo.Count, secure,
            memo.Count - secure - unverifiedCount, unverifiedCount, prunedInfeasible, prunedBound, complete,
            Finite(unverifiedBound));
        RestorationStateSummary[] states = memo.Keys.Order().Select(mask =>
        {
            var v = memo[mask];
            return new RestorationStateSummary(
                Enumerable.Range(0, n).Where(a => (mask & (1 << a)) != 0).Select(a => request.Actions[a].Id).ToArray(),
                unserved[mask], liveOf[mask].Count, v.State.Disposition, v.State.Reason,
                v.State.Limits.Count(l => l.Violated), v.Admissible, v.State.Limits.Where(l => l.Violated).ToArray());
        }).ToArray();

        bool proofComplete = !exhausted && unverifiedBound >= bestCost;
        RestorationStep[] sequence = [];
        if (bestOrder is not null)
        {
            var steps = new List<RestorationStep>(n);
            int mask = 0;
            double clock = 0;
            for (int k = 0; k < n; k++)
            {
                int a = bestOrder[k];
                int m2 = mask | (1 << a);
                RestorationAction action = request.Actions[a];
                double energy = unserved[mask] * minutes[a] / 60.0;
                steps.Add(new RestorationStep(k + 1, action.Id, action.Branch, clock, clock + minutes[a],
                    unserved[mask], unserved[m2], energy, liveOf[m2].Order().ToArray(), memo[m2].State));
                clock += minutes[a];
                mask = m2;
            }
            sequence = steps.ToArray();
        }

        string status, outcome;
        string? reason;
        double? lower, upper = bestOrder is null ? null : bestCost;
        if (!initial.Admissible && !initial.Unverified)
        {
            status = "INFEASIBLE"; outcome = "ProvenInfeasible"; lower = null;
            reason = "The initial state (every candidate open) fails the AC verifier: " + initial.State.Disposition + ".";
        }
        else if (bestOrder is not null && proofComplete)
        {
            status = "OPTIMAL"; outcome = "ProvenOptimal"; lower = bestCost; reason = null;
        }
        else if (bestOrder is not null)
        {
            status = "FEASIBLE"; outcome = "FeasibleNotProven";
            lower = exhausted ? relaxed[0] : Math.Min(bestCost, unverifiedBound);
            reason = exhausted
                ? $"Search budget of {request.MaxNodes} nodes exhausted; optimality not proven."
                : $"{unverifiedCount} state(s) could not be verified and the bound does not exclude them; optimality not proven.";
        }
        else if (!exhausted && double.IsPositiveInfinity(unverifiedBound))
        {
            status = "INFEASIBLE"; outcome = "ProvenInfeasible"; lower = null;
            reason = "Every sequence reaches a state that fails the AC verifier or violates a precedence constraint.";
        }
        else
        {
            status = "UNRESOLVED"; outcome = "Unresolved";
            lower = exhausted ? relaxed[0] : unverifiedBound;
            reason = exhausted
                ? $"Search budget of {request.MaxNodes} nodes exhausted before any admissible sequence was found."
                : $"No admissible sequence was found; {unverifiedCount} state(s) could not be verified, so infeasibility is not proven.";
        }
        return new(status, outcome, reason, totalLoad, unserved[0], unserved[full],
            bestOrder is null ? null : bestCost, Finite(lower), Finite(upper), Finite(relaxed[0]),
            initial.State, sequence, states, search);
    }

    /// <summary>Net demand counted as load: positive Pd of a bus (MW). Negative Pd (net embedded generation) is not load.</summary>
    private static double Demand(Bus b) => b.Pd > 0 ? b.Pd : 0;

    private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;

    // Bus IDs reachable from the slack over in-service branches, with candidate k closed exactly when its bit is set.
    private static HashSet<int> Live(GridModel model, Dictionary<int, int> candidate, int mask, int slack)
    {
        var adjacency = new Dictionary<int, List<int>>();
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            bool closed = candidate.TryGetValue(i, out int a) ? (mask & (1 << a)) != 0 : b.InService;
            if (!closed) continue;
            (adjacency.TryGetValue(b.From, out var f) ? f : adjacency[b.From] = []).Add(b.To);
            (adjacency.TryGetValue(b.To, out var t) ? t : adjacency[b.To] = []).Add(b.From);
        }
        var live = new HashSet<int> { slack };
        var stack = new Stack<int>();
        stack.Push(slack);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (!adjacency.TryGetValue(u, out var next)) continue;
            foreach (int v in next) if (live.Add(v)) stack.Push(v);
        }
        return live;
    }

    // The live sub-model of one switching state, verified by the P1 verifier; indices mapped back to the model's.
    private static SecurityState VerifyMask(GridModel model, RestorationRequest request, Dictionary<int, int> candidate,
        int mask, HashSet<int> live)
    {
        var branchMap = new List<int>();
        var branches = new List<Branch>();
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            bool closed = candidate.TryGetValue(i, out int a) ? (mask & (1 << a)) != 0 : b.InService;
            if (!closed || !live.Contains(b.From) || !live.Contains(b.To)) continue;
            branches.Add(b.InService ? b : b with { InService = true });
            branchMap.Add(i);
        }
        var generatorMap = new List<int>();
        var generators = new List<Generator>();
        for (int g = 0; g < model.Generators.Count; g++)
            if (live.Contains(model.Generators[g].Bus)) { generators.Add(model.Generators[g]); generatorMap.Add(g); }
        var liveBranchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Branch b in branches) if (b.Name is { } nm) liveBranchNames.Add(nm);
        GridModel liveModel = model.CopyWith(
            buses: model.Buses.Where(b => live.Contains(b.Id)).ToList(),
            generators: generators,
            branches: branches,
            oltcs: model.Oltcs.Where(o => liveBranchNames.Contains(o.Transformer) && live.Contains(o.ControlsBus)).ToList());
        SecurityState s = TransmissionSecurityRunner.VerifyState(liveModel, request.MinPu, request.MaxPu, PostFaultRating.RateA, null);
        LimitEvidence[] limits = s.Limits.Select(l => l.Kind switch
        {
            "BranchFrom" or "BranchTo" => l with { Index = branchMap[l.Index] },
            "GeneratorP" or "GeneratorPAdjustment" or "GeneratorQ" => l with { Index = generatorMap[l.Index] },
            _ => l
        }).ToArray();
        return s with { Limits = limits };
    }

    private static int[] Predecessors(RestorationRequest request)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int a = 0; a < request.Actions.Count; a++) index[request.Actions[a].Id] = a;
        var predecessors = new int[request.Actions.Count];
        foreach (PrecedenceConstraint p in request.Precedence ?? [])
            predecessors[index[p.After]] |= 1 << index[p.Before];
        return predecessors;
    }

    private static void Validate(GridModel model, RestorationRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.MinPu) || !double.IsFinite(request.MaxPu) ||
            request.MinPu <= 0 || request.MaxPu < request.MinPu)
            throw new ArgumentException("Voltage bounds must be finite, positive and ordered.");
        if (request.MaxNodes < 1)
            throw new ArgumentException("search.maxNodes must be at least 1.");
        if (request.Actions is null || request.Actions.Count == 0)
            throw new ArgumentException("At least one candidate switching action is required.");
        if (request.Actions.Count > MaxActions)
            throw new ArgumentException($"At most {MaxActions} candidate actions are supported (exhaustive enumeration).");
        if (model.Buses.Count(b => b.Type == BusType.Slack) != 1)
            throw new ArgumentException("Exactly one slack bus (the restoration source) is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var branches = new HashSet<int>();
        foreach (RestorationAction a in request.Actions)
        {
            if (a is null || a.Branch is null || string.IsNullOrWhiteSpace(a.Id))
                throw new ArgumentException("Every action needs an id and a branch.");
            if (!ids.Add(a.Id)) throw new ArgumentException($"Action id '{a.Id}' is duplicated.");
            if (a.Branch.Index < 0 || a.Branch.Index >= model.Branches.Count || !branches.Add(a.Branch.Index))
                throw new ArgumentException($"Action '{a.Id}': branch indices must be unique and within the model.");
            Branch b = model.Branches[a.Branch.Index];
            if (b.From != a.Branch.From || b.To != a.Branch.To)
                throw new ArgumentException($"Action '{a.Id}': branch {a.Branch.Index} endpoints do not match.");
            if (!double.IsFinite(a.Minutes) || a.Minutes <= 0)
                throw new ArgumentException($"Action '{a.Id}': minutes must be finite and positive.");
        }
        var pairs = new HashSet<(string, string)>();
        foreach (PrecedenceConstraint p in request.Precedence ?? [])
        {
            if (p is null || p.Before is null || p.After is null || !ids.Contains(p.Before) || !ids.Contains(p.After))
                throw new ArgumentException("Precedence constraints must name declared actions.");
            if (p.Before == p.After) throw new ArgumentException($"Precedence '{p.Before}' before itself.");
            if (!pairs.Add((p.Before, p.After))) throw new ArgumentException($"Precedence '{p.Before}' before '{p.After}' is duplicated.");
        }
        // A cyclic precedence set admits no order: an input contradiction, refused rather than reported as infeasible.
        int[] pred = Predecessors(request);
        int done = 0, count = request.Actions.Count;
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int a = 0; a < count; a++)
                if ((done & (1 << a)) == 0 && (pred[a] & ~done) == 0) { done |= 1 << a; progress = true; }
        }
        if (done != (1 << count) - 1) throw new ArgumentException("Precedence constraints are cyclic.");
    }
}
