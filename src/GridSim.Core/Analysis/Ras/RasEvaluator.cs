using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis.Ras;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

// =====================================================================================================================
// RC5 WP26: user-authored remedial action schemes (RAS, also called special protection schemes) inside a contingency
// screen. A new entry point beside the existing screens; none of them is changed.
//
// For each contingency:
//   1. the base case is solved once, and each scheme is armed (or not) from that state and the contingency's identity;
//   2. the post-contingency state is screened exactly as today (the row without schemes, bit for bit);
//   3. level by level (1 .. MaxLevels): every armed scheme whose triggers hold on the present state and that has
//      operations left operates; their actions are applied to the model in (priority, name, file order) and the state
//      is solved again. No scheme operating ends the loop. Schemes still operating after MaxLevels is RAS LOOP;
//   4. the final state is classified and netted against the base case like any contingency row.
// Steady state only: there are no time delays between actions and no dynamic simulation of the operation.
// =====================================================================================================================

/// <summary>The base case of a screen with remedial action schemes did not converge, so no scheme can be armed and no
/// outage netted against it.</summary>
public sealed class RasBaseCaseException : InvalidOperationException
{
    /// <summary>A new instance with no message.</summary>
    public RasBaseCaseException() { }

    /// <summary>A new instance with <paramref name="message"/>.</summary>
    public RasBaseCaseException(string message) : base(message) { }

    /// <summary>A new instance with <paramref name="message"/> and the exception that caused it.</summary>
    public RasBaseCaseException(string message, Exception inner) : base(message, inner) { }
}
public static partial class ContingencyAnalysis
{
    /// <summary>
    /// RC5 WP26: screen single-element outages (<see cref="ScreenElementOutage"/>, reference-bus dispatch) with remedial
    /// action schemes. Each result carries the row the screen gives without schemes (<see cref="RasContingencyResult.PreRas"/>,
    /// netted against the base case, bit for bit the <see cref="ScreenElementOutage"/> row) and the final state after the
    /// schemes. <paramref name="ras"/> must be resolved against <paramref name="model"/> (<see cref="RasSet.Resolve"/>).
    /// Throws <see cref="RasBaseCaseException"/> (an <see cref="InvalidOperationException"/>) when the base case does not
    /// converge, and
    /// <see cref="ArgumentException"/> for a branch outage of a branch already out of service.
    /// </summary>
    public static RasScreenReport ScreenWithRas(GridModel model, IReadOnlyList<ElementOutage> outages, RasSet ras,
        IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(outages);
        RequireResolved(ras);
        RequireInServiceOutages(model, outages.Where(o => o.Kind == OutageKind.Branch).Select(o => o.Index), nameof(outages));
        solver ??= SolverSelector.CreateNewton(model);
        (ContingencyResult baseCase, PowerFlowResult baseFlow) = RasBaseCase(model, solver, options);
        var results = new List<RasContingencyResult>(outages.Count);
        foreach (ElementOutage o in outages)
        {
            ContingencyResult pre = ScreenElementOutage(model, o, solver, options);
            results.Add(RunRas(model, ras, baseFlow, baseCase, pre, RasState.After(model, o), solver, options));
        }
        return new RasScreenReport(baseCase, results);
    }

    /// <summary>
    /// RC5 WP26: screen named sets of branches (each set out at once, as <see cref="ScreenOutage"/>) with remedial action
    /// schemes. As <see cref="ScreenWithRas"/> otherwise.
    /// </summary>
    public static RasScreenReport ScreenBranchSetsWithRas(GridModel model, IReadOnlyList<IReadOnlyList<int>> branchSets, RasSet ras,
        IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(branchSets);
        RequireResolved(ras);
        foreach (IReadOnlyList<int> set in branchSets)
        {
            foreach (int i in set)
                if (i < 0 || i >= model.Branches.Count)
                    throw new ArgumentOutOfRangeException(nameof(branchSets), i, $"branch {i} is outside 0..{model.Branches.Count - 1}.");
            RequireInServiceOutages(model, set, nameof(branchSets));
        }
        solver ??= SolverSelector.CreateNewton(model);
        (ContingencyResult baseCase, PowerFlowResult baseFlow) = RasBaseCase(model, solver, options);
        var results = new List<RasContingencyResult>(branchSets.Count);
        foreach (IReadOnlyList<int> set in branchSets)
        {
            ContingencyResult pre = ScreenOutageCore(model, set, solver, options);
            results.Add(RunRas(model, ras, baseFlow, baseCase, pre, RasState.AfterBranches(model, set), solver, options));
        }
        return new RasScreenReport(baseCase, results);
    }

    /// <summary>
    /// RC5 WP26: the N-k branch screen (<paramref name="k"/> &gt;= 2) with remedial action schemes. The combinations are
    /// chosen exactly as <see cref="ScreenNMinusKReport"/> chooses them (the same pruned candidate set, or every
    /// combination with <paramref name="exhaustive"/>), and the results come back in its order (worst first by the row
    /// without schemes), with the same excluded single outages and note. The combinations are screened one after the
    /// other with one solver from <paramref name="solverFactory"/>.
    /// </summary>
    public static RasScreenReport ScreenNMinusKWithRas(GridModel model, int k, RasSet ras, Func<IPowerFlowSolver>? solverFactory = null,
        PowerFlowOptions? options = null, int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(model);
        RequireResolved(ras);
        if (k < 2) throw new ArgumentOutOfRangeException(nameof(k), k, "the N-k screen with schemes needs a depth k of 2 or more (use ScreenWithRas for single outages).");
        solverFactory ??= () => SolverSelector.CreateNewton(model);
        IPowerFlowSolver solver = solverFactory();
        (ContingencyResult baseCase, PowerFlowResult baseFlow) = RasBaseCase(model, solver, options);
        int[] live = InServiceBranchIndices(model);
        var none = Array.Empty<ContingencyResult>();
        if (k > live.Length)
            return new RasScreenReport(baseCase, Array.Empty<RasContingencyResult>()) { Note = NkScreenReport.TooFewBranches(live.Length, k) };

        // The candidate set and the combinations: the code of ScreenNMinusKReport, step for step.
        int[] candidates;
        IReadOnlyList<ContingencyResult> excluded = none;
        if (exhaustive) candidates = live;
        else
        {
            IReadOnlyList<ContingencyResult> single = ScreenBranchesParallel(model, solverFactory, options, maxDegreeOfParallelism, null);
            candidates = RankCandidates(single, candidateCount, k, out excluded).Select(i => live[i]).ToArray();
        }
        var combos = new List<int[]>();
        Combinations(candidates, k, combos);

        var results = new RasContingencyResult[combos.Count];
        for (int c = 0; c < combos.Count; c++)
        {
            ContingencyResult pre = ScreenOutageCore(model, combos[c], solver, options);
            results[c] = RunRas(model, ras, baseFlow, baseCase, pre, RasState.AfterBranches(model, combos[c]), solver, options);
        }
        // The order of ScreenNMinusKReport: a stable sort, worst first, on the row without schemes (the raw row; netting
        // changes no number StressScore reads).
        return new RasScreenReport(baseCase, results.OrderByDescending(r => StressScore(r.PreRas)).ToList())
        {
            ExcludedSingleOutages = excluded,
            Note = NkScreenReport.TooFewCandidates(candidates.Length, excluded.Count, k),
        };
    }

    private static void RequireResolved(RasSet ras)
    {
        ArgumentNullException.ThrowIfNull(ras);
        if (!ras.Resolved)
            throw new ArgumentException("the remedial action schemes must be resolved against the model first (RasSet.Resolve).", nameof(ras));
    }

    // The base case row (for netting) and its solved state (for arming), both with the screen's solver and options.
    private static (ContingencyResult BaseCase, PowerFlowResult Flow) RasBaseCase(GridModel model, IPowerFlowSolver solver, PowerFlowOptions? options)
    {
        ContingencyResult baseCase = ScreenBaseCase(model, solver, options);
        if (!baseCase.Converged)
            throw new RasBaseCaseException(
                "Remedial action schemes: the base case did not converge, so no scheme can be armed and no outage netted against it.");
        PowerFlowResult flow = solver.Solve(model, options);
        if (!flow.Converged)
            throw new RasBaseCaseException("Remedial action schemes: the base case solve used for arming did not converge.");
        return (baseCase, flow);
    }

    private static RasContingencyResult RunRas(GridModel model, RasSet ras, PowerFlowResult baseFlow, ContingencyResult baseCase,
        ContingencyResult preRaw, RasState? state, IPowerFlowSolver solver, PowerFlowOptions? options)
    {
        ContingencyResult pre = NetAgainstBase(preRaw, baseCase);
        IReadOnlyList<RasScheme> schemes = ras.Schemes;
        if (state is null)
            return new RasContingencyResult(pre, pre, Array.Empty<string>(), Array.Empty<RasLevelRecord>(), Array.Empty<RasActionRecord>(),
                RasStatus.NotConvergedAfterRas, "no in-service unit is left to hold the reference, so no scheme was evaluated");

        // 1. Arming: every arming condition of the scheme holds on the base case and the contingency.
        var armed = new bool[schemes.Count];
        var armedNames = new List<string>();
        for (int s = 0; s < schemes.Count; s++)
        {
            armed[s] = schemes[s].Arming.All(c => Armed(c, model, baseFlow, state));
            if (armed[s]) armedNames.Add(schemes[s].Name);
        }

        // 2. The operating order: priority, then name (ordinal), then file order.
        int[] order = Enumerable.Range(0, schemes.Count)
            .OrderBy(s => schemes[s].Priority).ThenBy(s => schemes[s].Name, StringComparer.Ordinal).ThenBy(s => schemes[s].Ordinal).ToArray();

        // 3. The levels.
        var ops = new int[schemes.Count];
        var levels = new List<RasLevelRecord>();
        var actions = new List<RasActionRecord>();
        PowerFlowResult? flow = state.Solve(solver, options);
        bool changed = false, loop = false;
        for (int level = 1; ; level++)
        {
            var fire = new List<int>();
            foreach (int s in order)
                if (armed[s] && ops[s] < schemes[s].MaxOperations && Triggered(schemes[s], state, flow)) fire.Add(s);
            if (fire.Count == 0) break;
            if (level > ras.MaxLevels) { loop = true; break; }
            bool any = false;
            foreach (int s in fire)
            {
                ops[s]++;
                for (int a = 0; a < schemes[s].Actions.Count; a++)
                {
                    RasAction act = schemes[s].Actions[a];
                    (bool applied, string detail) = state.Apply(act);
                    actions.Add(new RasActionRecord(level, schemes[s].Name, a + 1, act.Kind, act.Key, applied, detail));
                    any |= applied;
                }
            }
            levels.Add(new RasLevelRecord(level, fire.Select(s => schemes[s].Name).ToList()));
            // No action applied: the state is unchanged, so the next level would see the same triggers and skip the same
            // actions. Stop here rather than count unchanged levels up to a false RAS LOOP.
            if (!any) break;
            changed = true;
            flow = state.Solve(solver, options);
        }

        // 4. The final state: the screen's own classification of the state the schemes left.
        ContingencyResult post = pre;
        if (changed)
        {
            (GridModel reduced, int[] original) = state.Build();
            ContingencyResult raw = ScreenStateCore(preRaw.Outage, reduced, original, solver, options, postFault: true, before: model)
                with { Kind = preRaw.Kind, LostLoadMw = preRaw.LostLoadMw, LostGenerationMw = preRaw.LostGenerationMw };
            post = NetAgainstBase(raw, baseCase);
        }
        RasStatus status = loop ? RasStatus.RasLoop
            : !post.Converged ? RasStatus.NotConvergedAfterRas
            : post.Secure ? RasStatus.SecureAfterRas
            : RasStatus.ViolationsAfterRas;
        return new RasContingencyResult(pre, post, armedNames, levels, actions, status);
    }

    // An arming condition. A condition on an element that is out of service in the base case is false (nothing flows
    // and nothing is measured there).
    private static bool Armed(RasCondition c, GridModel model, PowerFlowResult baseFlow, RasState state)
    {
        switch (c.Kind)
        {
            case RasConditionKind.Always: return true;
            case RasConditionKind.Outage: return state.Contains(c.Element, c.Index);
            case RasConditionKind.PreFlowAbove:
            case RasConditionKind.PreFlowBelow:
            {
                if (!model.Branches[c.Index].InService) return false;
                LineFlow f = baseFlow.LineFlows[c.Index];
                double v = c.InMw ? f.PFromMw : EndMva(f);
                return c.Kind == RasConditionKind.PreFlowAbove ? v > c.Value : v < c.Value;
            }
            case RasConditionKind.PreVoltageBelow:
            case RasConditionKind.PreVoltageAbove:
            {
                double? vm = VoltageOf(baseFlow, model.Buses[c.Index].Id);
                if (vm is not double v) return false;
                return c.Kind == RasConditionKind.PreVoltageBelow ? v < c.Value : v > c.Value;
            }
            default:
                throw new InvalidDataException($"a {c.Kind} condition is a trigger, not an arming condition.");
        }
    }

    // The triggers of a scheme on the present state. With no converged state only notConverged can hold; a trigger on an
    // element that is out of service is false.
    private static bool Triggered(RasScheme scheme, RasState state, PowerFlowResult? flow)
    {
        bool converged = flow is not null && flow.Converged;
        bool One(RasCondition c)
        {
            if (c.Kind == RasConditionKind.NotConverged) return !converged;
            if (!converged) return false;
            switch (c.Kind)
            {
                case RasConditionKind.LoadingAbove:
                {
                    if (state.FlowOf(c.Index, flow!) is not LineFlow f) return false;
                    double loading = c.Rating == 'A' ? f.LoadingPercent
                        : PostFaultLoadingPercent(state.BranchRecord(c.Index), f, c.Rating == 'B' ? PostFaultRating.RateB : PostFaultRating.RateC);
                    return loading > c.Value;
                }
                case RasConditionKind.FlowAbove:
                {
                    if (state.FlowOf(c.Index, flow!) is not LineFlow f) return false;
                    return (c.AtToEnd ? f.PToMw : f.PFromMw) > c.Value;
                }
                case RasConditionKind.VoltageBelow:
                case RasConditionKind.VoltageAbove:
                {
                    if (state.BusIdIfIn(c.Index) is not int id || VoltageOf(flow!, id) is not double v) return false;
                    return c.Kind == RasConditionKind.VoltageBelow ? v < c.Value : v > c.Value;
                }
                default:
                    throw new InvalidDataException($"a {c.Kind} condition is an arming condition, not a trigger.");
            }
        }
        return scheme.AnyTrigger ? scheme.Triggers.Any(One) : scheme.Triggers.All(One);
    }

    private static double EndMva(LineFlow f) =>
        Math.Max(new System.Numerics.Complex(f.PFromMw, f.QFromMvar).Magnitude, new System.Numerics.Complex(f.PToMw, f.QToMvar).Magnitude);

    private static double? VoltageOf(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count && i < r.Vm.Length; i++) if (r.BusIds[i] == busId) return r.Vm[i];
        return null;
    }

    /// <summary>
    /// The network state a contingency's schemes act on: the original model's buses, branches and units, each with its
    /// present record and whether it is in the state. The model it builds is the post-contingency model of the existing
    /// screen (the same records in the same order), so its solve is the screen's solve; the schemes' actions then change
    /// records or take elements in and out.
    /// </summary>
    private sealed class RasState
    {
        private readonly GridModel _model;
        private readonly Bus[] _bus;
        private readonly bool[] _busIn;
        private readonly Branch[] _br;
        private readonly bool[] _brIn;
        private readonly Generator[] _fleet;
        private readonly Dictionary<int, int> _busIndex = new();
        private readonly HashSet<int> _outBranches = new(), _outUnits = new(), _outBuses = new();
        private int[] _position = Array.Empty<int>();   // original branch -> its place in the last built model, or -1

        private RasState(GridModel model)
        {
            _model = model;
            _bus = model.Buses.ToArray();
            _busIn = Enumerable.Repeat(true, _bus.Length).ToArray();
            _br = model.Branches.ToArray();
            _brIn = Enumerable.Repeat(true, _br.Length).ToArray();
            _fleet = model.AllGenerators.ToArray();
            for (int i = 0; i < _bus.Length; i++) _busIndex.TryAdd(_bus[i].Id, i);
        }

        /// <summary>The state after a set of branches is lost.</summary>
        public static RasState AfterBranches(GridModel model, IReadOnlyList<int> branches)
        {
            var s = new RasState(model);
            foreach (int k in branches) { s._brIn[k] = false; s._outBranches.Add(k); }
            return s;
        }

        /// <summary>The state after one element outage, built as <see cref="ScreenElementOutage"/> builds it; null when
        /// the outage leaves no unit to hold the reference (the screen reports that state as not solved).</summary>
        public static RasState? After(GridModel model, ElementOutage outage)
        {
            switch (outage.Kind)
            {
                case OutageKind.Branch:
                    return AfterBranches(model, new[] { outage.Index });
                case OutageKind.Generator:
                {
                    Generator gen = model.Generators[outage.Index];
                    var gens = new List<Generator>(model.Generators.Count - 1);
                    for (int i = 0; i < model.Generators.Count; i++) if (i != outage.Index) gens.Add(model.Generators[i]);
                    (IReadOnlyList<Bus>? buses, _, bool noUnitLeft) = ReelectReference(model, model.Branches, gens, removedBusId: null, gen.Bus);
                    if (noUnitLeft) return null;
                    var s = new RasState(model);
                    int all = FleetIndexOfInService(model, outage.Index);
                    s._fleet[all] = s._fleet[all] with { InService = false };
                    s._outUnits.Add(all);
                    if (buses is not null) for (int i = 0; i < buses.Count; i++) s._bus[i] = buses[i];
                    return s;
                }
                case OutageKind.Bus:
                {
                    Bus bus = model.Buses[outage.Index];
                    var kept = new List<Branch>(model.Branches.Count);
                    var removed = new List<int>();
                    for (int j = 0; j < model.Branches.Count; j++)
                    {
                        if (model.Branches[j].From == bus.Id || model.Branches[j].To == bus.Id) removed.Add(j);
                        else kept.Add(model.Branches[j]);
                    }
                    var gens = new List<Generator>(model.Generators.Count);
                    foreach (Generator g in model.Generators) if (g.Bus != bus.Id) gens.Add(g);
                    (IReadOnlyList<Bus>? buses, _, bool noUnitLeft) = ReelectReference(model, kept, gens, removedBusId: bus.Id, bus.Id);
                    if (noUnitLeft) return null;
                    var s = new RasState(model);
                    if (buses is not null) for (int i = 0; i < buses.Count; i++) s._bus[i] = buses[i];
                    // Every bus with this id is removed, as the screen removes them.
                    for (int i = 0; i < s._bus.Length; i++) if (s._bus[i].Id == bus.Id) { s._busIn[i] = false; s._outBuses.Add(i); }
                    foreach (int j in removed) { s._brIn[j] = false; s._outBranches.Add(j); }
                    for (int a = 0; a < s._fleet.Length; a++)
                        if (s._fleet[a].InService && s._fleet[a].Bus == bus.Id) { s._fleet[a] = s._fleet[a] with { InService = false }; s._outUnits.Add(a); }
                    return s;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(outage), $"unknown outage kind {outage.Kind}.");
            }
        }

        // The place of in-service unit i (an index into model.Generators) in model.AllGenerators.
        private static int FleetIndexOfInService(GridModel model, int i)
        {
            int seen = -1;
            for (int a = 0; a < model.AllGenerators.Count; a++)
                if (model.AllGenerators[a].InService && ++seen == i) return a;
            throw new InvalidOperationException($"in-service generator {i} is not in the fleet.");
        }

        /// <summary>True when the contingency took out the element (a bus outage takes its branches and units too).</summary>
        public bool Contains(RasElement e, int index) => e switch
        {
            RasElement.Branch => _outBranches.Contains(index),
            RasElement.Unit => _outUnits.Contains(index),
            RasElement.Bus => _outBuses.Contains(index),
            _ => false,
        };

        public Branch BranchRecord(int k) => _br[k];

        public int? BusIdIfIn(int busIndex) => _busIn[busIndex] ? _bus[busIndex].Id : null;

        /// <summary>The model of the present state, and each of its branches' index in the original model.</summary>
        public (GridModel Model, int[] Original) Build()
        {
            var buses = new List<Bus>(_bus.Length);
            for (int i = 0; i < _bus.Length; i++) if (_busIn[i]) buses.Add(_bus[i]);
            var branches = new List<Branch>(_br.Length);
            var original = new List<int>(_br.Length);
            var position = new int[_br.Length];
            for (int j = 0; j < _br.Length; j++)
            {
                position[j] = -1;
                if (!_brIn[j]) continue;
                position[j] = branches.Count;
                branches.Add(_br[j]);
                original.Add(j);
            }
            _position = position;
            GridModel m = _model.CopyWith(buses: buses, generators: _fleet.ToArray(), branches: branches, generatorsAreWholeFleet: true);
            return (m, original.ToArray());
        }

        /// <summary>Solve the present state as the screen does; null when it has no solution (or the solver threw).</summary>
        public PowerFlowResult? Solve(IPowerFlowSolver solver, PowerFlowOptions? options)
        {
            (GridModel m, _) = Build();
            if (IsBuiltInNewton(solver) && ProvablyUnsolvablePart(m, (options ?? new PowerFlowOptions()).Tolerance) is not null) return null;
            try { return solver.Solve(m, options); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        /// <summary>The solved flow of an original branch, or null when it is out of the state or out of service.</summary>
        public LineFlow? FlowOf(int k, PowerFlowResult flow)
        {
            if (!_brIn[k] || !_br[k].InService) return null;
            int p = k < _position.Length ? _position[k] : -1;
            return p >= 0 && p < flow.LineFlows.Count ? flow.LineFlows[p] : null;
        }

        private static string F(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Apply one action: whether it was applied, and what was done or why it was skipped.</summary>
        public (bool Applied, string Detail) Apply(RasAction a)
        {
            switch (a.Kind)
            {
                case RasActionKind.TripGenerator:
                {
                    Generator g = _fleet[a.Index];
                    if (!g.InService) return (false, "ACTION SKIPPED: the unit is already out of service");
                    _fleet[a.Index] = g with { InService = false };
                    if (_busIndex.TryGetValue(g.Bus, out int bi) && _bus[bi].Type == BusType.Slack && !_fleet.Any(u => u.InService && u.Bus == g.Bus))
                    {
                        (GridModel cur, _) = Build();
                        (IReadOnlyList<Bus>? buses, string? note, bool noUnitLeft) = ReelectReference(cur, cur.Branches, cur.Generators, removedBusId: null, g.Bus);
                        if (noUnitLeft)
                        {
                            _fleet[a.Index] = g;
                            return (false, $"ACTION SKIPPED: no other in-service unit could take over the reference of bus {g.Bus}");
                        }
                        if (buses is not null)
                            foreach (Bus b in buses) if (_busIndex.TryGetValue(b.Id, out int k)) _bus[k] = b;
                        return (true, $"tripped ({F(g.Pg)} MW scheduled); {note}");
                    }
                    return (true, $"tripped ({F(g.Pg)} MW scheduled; the reference picks it up)");
                }
                case RasActionKind.TripBranch:
                {
                    if (!_brIn[a.Index] || !_br[a.Index].InService) return (false, "ACTION SKIPPED: the branch is already out of service");
                    _brIn[a.Index] = false;
                    return (true, "opened");
                }
                case RasActionKind.CloseBranch:
                {
                    Branch b = _br[a.Index];
                    // The contingency's own outage is never put back: that would undo the contingency and screen the base
                    // case under its name.
                    if (_outBranches.Contains(a.Index)) return (false, "ACTION SKIPPED: the branch is the contingency's outage");
                    if (_brIn[a.Index] && b.InService) return (false, "ACTION SKIPPED: the branch is already in service");
                    if (!(_busIndex.TryGetValue(b.From, out int f) && _busIn[f] && _busIndex.TryGetValue(b.To, out int t) && _busIn[t]))
                        return (false, "ACTION SKIPPED: an end bus of the branch is out of service");
                    _brIn[a.Index] = true;
                    _br[a.Index] = b with { InService = true };
                    return (true, "closed");
                }
                case RasActionKind.ShedLoad:
                {
                    if (!_busIn[a.Index]) return (false, "ACTION SKIPPED: the bus is out of service");
                    Bus b = _bus[a.Index];
                    if (!(b.Pd > 0)) return (false, "ACTION SKIPPED: the bus has no load to shed");
                    bool limited = !a.Fraction && a.Value > b.Pd;
                    double shed = a.Fraction ? b.Pd * a.Value : Math.Min(a.Value, b.Pd);
                    double pd = b.Pd - shed;
                    double qd = pd == 0 ? 0 : b.Qd * (pd / b.Pd);   // constant power factor
                    _bus[a.Index] = b with { Pd = pd, Qd = qd };
                    return (true, $"shed {F(shed)} MW: load {F(b.Pd)} -> {F(pd)} MW, {F(b.Qd)} -> {F(qd)} Mvar{(limited ? " (limited to the bus load)" : "")}");
                }
                case RasActionKind.SetGeneratorP:
                {
                    Generator g = _fleet[a.Index];
                    if (!g.InService) return (false, "ACTION SKIPPED: the unit is out of service");
                    if (g.Pmin > g.Pmax) return (false, $"ACTION SKIPPED: the unit's Pmin {F(g.Pmin)} MW is above its Pmax {F(g.Pmax)} MW");
                    double target = a.Delta ? g.Pg + a.Value : a.Value;
                    double set = Math.Min(Math.Max(target, g.Pmin), g.Pmax);
                    _fleet[a.Index] = g with { Pg = set };
                    string held = set != target ? $" (held at its limit; asked for {F(target)} MW)" : "";
                    string slack = _busIndex.TryGetValue(g.Bus, out int bi) && _bus[bi].Type == BusType.Slack
                        ? "; the unit is on the reference bus, so the re-solve sets the reference's output" : "";
                    return (true, $"output {F(g.Pg)} -> {F(set)} MW{held}{slack}");
                }
                case RasActionKind.SwitchShunt:
                {
                    if (!_busIn[a.Index]) return (false, "ACTION SKIPPED: the bus is out of service");
                    Bus b = _bus[a.Index];
                    _bus[a.Index] = b with { Bs = b.Bs + a.Value };
                    return (true, $"shunt {F(b.Bs)} -> {F(b.Bs + a.Value)} Mvar at 1 pu");
                }
                case RasActionKind.SetTap:
                {
                    Branch b = _br[a.Index];
                    if (!_brIn[a.Index] || !b.InService) return (false, "ACTION SKIPPED: the branch is out of service");
                    _br[a.Index] = b with { TapRatio = a.Value };
                    return (true, $"tap ratio {F(b.TapRatio)} -> {F(a.Value)}");
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(a), a.Kind, "unknown RAS action kind");
            }
        }
    }
}
