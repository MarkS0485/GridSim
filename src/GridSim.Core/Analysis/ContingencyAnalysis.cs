using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>Result of taking one or more elements out of service and re-solving.</summary>
public sealed record ContingencyResult(
    string Outage,
    bool Converged,
    double MaxBranchLoadingPercent,
    double WorstBoundaryLoadingPercent,
    double MinVm,
    double MaxVm,
    IReadOnlyList<string> Violations)
{
    public bool Secure => Converged && Violations.Count == 0;

    /// <summary>
    /// Every violation of the solved state, one entry per violating ELEMENT (each overloaded branch, each overloaded
    /// boundary, and each bus outside the operational voltage band), before any netting. Empty for a non-converged
    /// outage. <see cref="Violations"/> is the human-readable summary of the same facts (one line per branch and
    /// boundary, but a single undervoltage and a single overvoltage line however many buses breach). This list is
    /// what <see cref="ContingencyAnalysis.NetAgainstBase(ContingencyResult, ContingencyResult)"/> compares, element
    /// by element, with the base case (October 2026, D03).
    /// </summary>
    public IReadOnlyList<ContingencyViolation> Details { get; init; } = Array.Empty<ContingencyViolation>();

    /// <summary>
    /// True once <see cref="ContingencyAnalysis.NetAgainstBase(ContingencyResult, ContingencyResult)"/> has removed
    /// the base case's own violations from <see cref="Violations"/> (D03). False for a raw (absolute) screen result.
    /// </summary>
    public bool NettedAgainstBase { get; init; }

    /// <summary>
    /// After netting (D03): the violations of this state that the base case already had, in the same text form as
    /// <see cref="Violations"/>. They are reported once, as base-case violations, and are not counted against the
    /// outage. Empty for a raw screen result and for a non-converged outage.
    /// </summary>
    public IReadOnlyList<string> PreExistingViolations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// GR-125 (October 2026): the part of <see cref="PreExistingViolations"/> that this outage made materially WORSE than
    /// the base case (a branch or boundary loaded more than <see cref="ContingencyAnalysis.WorsenedLoadingMarginPercent"/>
    /// percentage points above its base loading, or a bus more than <see cref="ContingencyAnalysis.WorsenedVoltageMarginPu"/>
    /// further outside the band than in the base case), in the same text form. Netting used to hide these: a deepened
    /// base-case overload read "base only" like an untouched one. They are reported here and, when the netting is asked to
    /// count them (<c>countWorsened</c>), also counted against the outage in <see cref="Violations"/>.
    /// </summary>
    public IReadOnlyList<string> WorsenedPreExistingViolations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// GR-621 (October 2026): for an N-1-1 result screened with the default corrective action
    /// (<see cref="ContingencyAnalysis.PlanCorrectiveRedispatch(GridModel)"/>), what the corrective step after the FIRST
    /// outage did: applied, not needed, infeasible, or no DC model (the last two leave the dispatch unchanged). Null for
    /// every other result and for an N-1-1 screen given its own corrective action, whose outcome is not known. A result
    /// used to carry no trace of it, so an infeasible correction (the dispatch left as it was) read like an applied one.
    /// </summary>
    public RedispatchOutcome? CorrectiveOutcome { get; init; }

    /// <summary>
    /// N107 (October 2026): for a state with no converged solution, whether the outage SPLIT the network: the state has
    /// more connected parts (buses joined by in-service branches) than the un-outaged model. True reads "islanded"; false,
    /// with <see cref="Converged"/> false, is a connected network that did not solve (for example the reactive collapse of
    /// case14 branch 1-2 with generator limits on, N082) and reads "no solution". Every non-converged outage used to say
    /// "islanded / no solution" whatever the cause. Always false for a converged state (it is only worked out when the
    /// solve fails, so a converged outage costs nothing extra). Labels only: no number, verdict or exit code depends on it.
    /// </summary>
    public bool Islanded { get; init; }

    /// <summary>F20: what kind of element this outage took out (<see cref="OutageKind.Branch"/> for every branch, N-2,
    /// N-k and N-1-1 result, and for the base case).</summary>
    public OutageKind Kind { get; init; } = OutageKind.Branch;

    /// <summary>F20: demand that the outage itself disconnected (a bus outage takes its bus's load with it), MW. 0 for a
    /// branch or generator outage. Reported, not judged: the SQSS loss-of-supply limits (NETS SQSS v2.10 Table 5.1) are
    /// not modelled here.</summary>
    public double LostLoadMw { get; init; }

    /// <summary>F20: in-service generation the outage took out (the outaged unit's, or the units on an outaged bus), MW
    /// at their scheduled output; the reference (slack) bus picks it up in the re-solve.</summary>
    public double LostGenerationMw { get; init; }

    /// <summary>F20: what the screen had to change to re-solve, in words (for example the reference moving to another
    /// bus when the outage took the slack's only unit); null when nothing.</summary>
    public string? Note { get; init; }
}

/// <summary>F20 (October 2026): the kind of element a single outage removes.</summary>
public enum OutageKind
{
    /// <summary>A branch (line or transformer): the historical N-1 element.</summary>
    Branch,
    /// <summary>One in-service generating unit (NETS SQSS v2.10 paragraph 5.1.2: a single generating unit).</summary>
    Generator,
    /// <summary>A bus with everything on it: its branches, units and load (the bus-branch model's stand-in for a section
    /// of busbar, NETS SQSS v2.10 paragraph 5.1.5).</summary>
    Bus,
}

/// <summary>
/// N124 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): who picks up the real power a
/// generator or bus outage takes away, in the post-contingency steady state.
/// <para>The reference tools offer the same three choices and default to the first: PSS/E AC contingency analysis
/// (ACCC) has a generation dispatch mode (disabled, the swing bus takes the change; or subsystem machines by reserve,
/// Pmax, INERTIA or GOVERNOR DROOP), and PowerFactory's contingency analysis has its active power control ("as
/// dispatched", the reference machine; "according to secondary control"; "according to primary control", i.e. droop;
/// "according to inertias"). GridSim's default stays the reference bus, bit for bit the F20 screen; the other two are
/// opt-in.</para>
/// </summary>
public enum OutageDispatch
{
    /// <summary>The reference (slack) bus picks up the whole change in the re-solve (the F20 default, PSS/E "disabled",
    /// PowerFactory "as dispatched").</summary>
    ReferenceBus,
    /// <summary>The change is shared among the remaining synchronous units in proportion to their primary-response
    /// gain MBase / R (PSS/E dispatch by governor droop; PowerFactory "according to primary control"). The model
    /// carries no per-unit droop, so R is uniform and the share is in proportion to MBase. Each unit stays within
    /// [Pmin, Pmax]; what does not fit is left to the reference.</summary>
    GovernorDroop,
    /// <summary>The change is shared in proportion to each remaining synchronous unit's stored energy H x MBase (PSS/E
    /// dispatch by inertia; PowerFactory "according to inertias"): the distribution of the first seconds, before the
    /// governors act. Limits as for <see cref="GovernorDroop"/>.</summary>
    Inertia,
}

/// <summary>F20: one single-element outage. <paramref name="Index"/> is into <see cref="GridModel.Branches"/>,
/// <see cref="GridModel.Generators"/> (the in-service list) or <see cref="GridModel.Buses"/>, by <paramref name="Kind"/>.</summary>
public sealed record ElementOutage(OutageKind Kind, int Index);

/// <summary>The kind of element a <see cref="ContingencyViolation"/> is about.</summary>
public enum ContingencyViolationKind
{
    /// <summary>A branch loaded above <see cref="ContingencyAnalysis.OverloadThresholdPercent"/> of its RateA.</summary>
    BranchOverload,
    /// <summary>A boundary whose transfer exceeds its limit (<see cref="BoundaryFlow.Overloaded"/>).</summary>
    BoundaryOverload,
    /// <summary>A bus below <see cref="VoltageLimits.OperationalLowPu"/>.</summary>
    Undervoltage,
    /// <summary>A bus above <see cref="VoltageLimits.OperationalHighPu"/>.</summary>
    Overvoltage,
}

/// <summary>
/// One violating element of a solved contingency state (D03).
/// </summary>
/// <param name="Kind">What kind of limit is breached.</param>
/// <param name="Element">Which element, keyed so the same element has the same key in every outage of one model: a
/// branch is its index in the ORIGINAL (un-outaged) model's branch list, a boundary its index in
/// <see cref="GridModel.Boundaries"/>, and a bus its bus id. (A branch name would not do: names may be empty or
/// shared by parallel circuits.)</param>
/// <param name="Value">Branch or boundary loading in percent of its limit, or the bus voltage in pu.</param>
/// <param name="Name">The branch or boundary name as the text summary prints it; empty for a bus.</param>
public sealed record ContingencyViolation(ContingencyViolationKind Kind, int Element, double Value, string Name);

/// <summary>
/// The pruned N-k or N-1-1 screen with the single outages it left out of the pairing (October 2026, D02).
/// </summary>
/// <param name="Results">The screened combinations, worst first (exactly what <see cref="ContingencyAnalysis.ScreenNMinusK"/>
/// and <see cref="ContingencyAnalysis.ScreenNMinus1Minus1"/> return).</param>
/// <param name="ExcludedSingleOutages">The single (N-1) outages that did not solve, in branch order: islanding
/// outages and outages with no power-flow solution. They already fail at N-1, so pairing them adds no information;
/// they are reported here, separately, instead of filling the candidate list. Empty for an exhaustive screen (which
/// runs no pre-screen and pairs every branch).</param>
public sealed record NkScreenReport(IReadOnlyList<ContingencyResult> Results, IReadOnlyList<ContingencyResult> ExcludedSingleOutages)
{
    /// <summary>Why <see cref="Results"/> is empty when the screen could form no outage combination at all (fewer
    /// branches, or fewer single outages that solve, than the depth); null whenever combinations were screened. It
    /// used to come back empty with nothing saying why, which reads exactly like "every combination is secure".</summary>
    public string? Note { get; init; }

    internal static string TooFewBranches(int n, int k) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"the model has {n} branch(es), fewer than the depth k = {k}, so no {k}-outage combination exists; nothing was screened.");

    internal static string? TooFewCandidates(int candidates, int excluded, int k) => candidates >= k ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"only {candidates} single outage(s) solve ({excluded} do not), fewer than the depth k = {k}, so no {k}-outage combination could be formed; nothing was screened.");
}

/// <summary>What <see cref="ContingencyAnalysis.PlanCorrectiveRedispatch"/> did.</summary>
public enum RedispatchOutcome
{
    /// <summary>Nothing to relieve: every rated line and every limited boundary is inside its limit in the DC model
    /// with the present dispatch, so the operator takes no action and the dispatch is unchanged.</summary>
    NotNeeded,
    /// <summary>A redispatch was found and applied.</summary>
    Applied,
    /// <summary>The DC model of the network could not be formed (an in-service zero-reactance branch is the only
    /// link between two islands, or B' is singular); the dispatch is unchanged.</summary>
    NoDcModel,
    /// <summary>No redispatch within the generator limits removes the overloads (the LP is infeasible or did not
    /// reach an optimum); the dispatch is unchanged, never a fabricated operating point.</summary>
    Infeasible,
}

/// <summary>
/// The corrective redispatch decided after a first outage (D01, N069).
/// </summary>
/// <param name="Outcome">What was done.</param>
/// <param name="Model">The model with the corrective dispatch applied (the input model itself unless
/// <paramref name="Outcome"/> is <see cref="RedispatchOutcome.Applied"/>).</param>
/// <param name="MovedMw">Total MW moved, the sum over generators of |new Pg - old Pg| (0 unless applied).</param>
/// <param name="OverloadedBefore">The rated lines and limited boundaries over their limit in the DC model before any
/// action, by name (lines as "branch NAME", boundaries as "boundary NAME").</param>
/// <param name="LpStatus">The last LP's status; null when no LP ran.</param>
public sealed record CorrectiveRedispatch(
    RedispatchOutcome Outcome, GridModel Model, double MovedMw, IReadOnlyList<string> OverloadedBefore, LpStatus? LpStatus);

/// <summary>
/// N-k security screening: take one element (N-1), a pair (N-2) or an arbitrary set out of
/// service, re-solve, and report whether the grid survives  -  branch/boundary overloads,
/// voltages outside 0.94-1.06 p.u., or outright islanding (the power flow fails to converge
/// because a radial element, e.g. a single GSP transformer, was the only feed to a load).
/// N-1 is the bread-and-butter check a control room runs continuously; N-2 is where a full
/// enumeration explodes, so it screens N-1 first and pairs only the most-stressed elements.
/// </summary>
public static partial class ContingencyAnalysis
{
    // ---- Named constants (October 2026 review, WS13 R153). Values unchanged. The voltage band itself is the
    // ---- shared VoltageLimits.OperationalLowPu/HighPu (R136).

    /// <summary>Weight of a voltage excursion beyond the operational band in <see cref="StressScore"/>: score points
    /// per pu outside the band. 1000 per pu makes a 0.01 pu excursion count like 10 percentage points of branch
    /// loading, so a voltage-stressed outage ranks alongside a heavily loaded one.</summary>
    public const double StressVoltageWeightPerPu = 1000.0;

    /// <summary>A branch loaded above this percentage of its rating is listed as a violation.</summary>
    public const double OverloadThresholdPercent = 100.0;

    /// <summary>Default size of the ranked critical set the N-k screens enumerate subsets of.</summary>
    public const int DefaultCandidateCount = 20;

    // ---- The one solver option set of the contingency family (October 2026, D04) --------------------------------
    //
    // Before D04 the verbs disagreed: nminus1 / nminus2 / n-1-1 used the PowerFlowOptions defaults (30 iterations,
    // Q-limits on), nminus1-sweep used 100 iterations with Q-limits OFF, stress and chain 100 with Q-limits on, and
    // none of them ran the OLTC loop that the default case solve runs. So the same outage could be secure in one verb
    // and violating or "islanded" in another. Now every contingency verb solves with StudyOptions through an engine
    // wrapped by WithOltcLoop:
    //   * 100 iterations: the cap the default case solve, stress and chain already used (a hard outage that needs 31
    //     to 100 Newton steps is a solution, not an islanding);
    //   * reactive limits enforced: the post-fault state must sit "without exceeding the available reactive capability
    //     of generation" (NETS SQSS v2.10, section 6.2);
    //   * the OLTC loop: the same section judges the post-fault voltages "after the operation of Grid Supply
    //     Transformer tap-changers".
    // The API methods keep their own defaults (null options mean the PowerFlowOptions defaults, and a null solver is
    // the raw Newton engine), so every existing API caller's numbers are unchanged; the CLI passes these explicitly.

    /// <summary>Newton iteration cap of the contingency family (D04): the cap of the default case solve.</summary>
    public const int StudyMaxIterations = 100;

    /// <summary>The power-flow options every contingency verb solves with (D04): <see cref="StudyMaxIterations"/>
    /// iterations, reactive limits enforced, the default tolerance (1e-8 pu) and Q-limit pass cap (12).</summary>
    public static PowerFlowOptions StudyOptions { get; } = new() { MaxIterations = StudyMaxIterations, EnforceQLimits = true };

    /// <summary>Wrap a Newton engine in the OLTC outer loop (D04), so it solves to the tap-regulated steady state.
    /// With no OLTC records in the model the answer is the engine's own, bit for bit. One instance per thread.</summary>
    public static IPowerFlowSolver WithOltcLoop(IPowerFlowSolver engine) => new OltcLoopPowerFlow(engine);

    /// <summary>The contingency family's solver for <paramref name="model"/> on the CPU (D04): the shared dense /
    /// sparse Newton rule (<see cref="SolverSelector.CreateNewton"/>) wrapped in the OLTC loop. One instance per thread.</summary>
    public static IPowerFlowSolver CreateStudySolver(GridModel model) => WithOltcLoop(SolverSelector.CreateNewton(model));

    /// <summary>A factory of <see cref="CreateStudySolver"/> instances, for the parallel screens (one per worker).</summary>
    public static Func<IPowerFlowSolver> StudySolverFactory(GridModel model) => () => CreateStudySolver(model);

    /// <summary>The <see cref="ContingencyResult.Outage"/> label of <see cref="ScreenBaseCase"/>.</summary>
    public const string BaseCaseLabel = "base case";

    public static IReadOnlyList<ContingencyResult> ScreenBranches(
        GridModel model, IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        solver ??= SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        var results = new List<ContingencyResult>(model.Branches.Count);
        for (int i = 0; i < model.Branches.Count; i++)
            results.Add(ScreenOutageCore(model, new[] { i }, solver, options));
        return results;
    }

    /// <summary>
    /// The same per-branch N-1 screen as <see cref="ScreenBranches"/>, run in parallel across the
    /// branch list. Each worker gets its own solver from <paramref name="solverFactory"/> (the
    /// solvers hold no shared state and <see cref="GridModel"/> is immutable, so this is safe).
    /// Results are returned in branch order regardless of completion order.
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenBranchesParallel(
        GridModel model, Func<IPowerFlowSolver>? solverFactory = null,
        PowerFlowOptions? options = null, int maxDegreeOfParallelism = -1,
        IReadOnlyList<int>? branchIndices = null, CancellationToken ct = default)
    {
        solverFactory ??= () => SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        // Which branches to outage: all of them, or a caller-supplied subset (for sampling the
        // largest systems where a full N-1 sweep would blow the time budget).
        int[] idx = branchIndices?.ToArray() ?? Enumerable.Range(0, model.Branches.Count).ToArray();
        var results = new ContingencyResult[idx.Length];
        using var local = new ThreadLocal<IPowerFlowSolver>(solverFactory);
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism,
            // Default None never trips; when a deadline token is supplied the sweep stops between outages
            // (Parallel.For throws OperationCanceledException), so a deferred N-1 shard aborts at the window.
            CancellationToken = ct,
        };
        Parallel.For(0, idx.Length, po, k => results[k] = ScreenOutageCore(model, new[] { idx[k] }, local.Value!, options));
        return results;
    }

    /// <summary>
    /// N-2 branch screening. A full pairwise sweep is C(n,2) solves - infeasible at scale - so unless
    /// <paramref name="exhaustive"/> is set this first runs the N-1 screen, ranks the branches by how
    /// much stress their single outage puts on the system, keeps the top <paramref name="candidateCount"/>
    /// of the outages that SOLVE, and enumerates only the unordered pairs among that critical set. That is the
    /// standard "screen, then pair the credible ones" workflow: a second outage on an already-stressed network is
    /// where cascades begin. Single outages that do not solve (islanding, no solution) are left out of the pairing
    /// (D02; see <see cref="ScreenNMinusKReport"/>, which also returns them). Results are returned worst-first. When
    /// <paramref name="exhaustive"/> is true it enumerates every pair (use only on small cases).
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenNMinus2(
        GridModel model, Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1,
        CancellationToken ct = default)
        => ScreenNMinusK(model, 2, solverFactory, options, candidateCount, exhaustive, maxDegreeOfParallelism, ct);

    /// <summary>
    /// General N-k branch screening (<paramref name="k"/> &gt;= 1). Below the top of the tree it prunes
    /// exactly as <see cref="ScreenNMinus2"/> does: screen N-1, rank the single outages that solve by their stress,
    /// keep the top <paramref name="candidateCount"/>, and enumerate the k-subsets of that critical set. With
    /// <paramref name="exhaustive"/> the candidate set is every branch (C(n,k) solves - small cases only).
    /// k=1 is the plain N-1 screen. Results are returned worst-first.
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenNMinusK(
        GridModel model, int k, Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1,
        CancellationToken ct = default)
        => ScreenNMinusKReport(model, k, solverFactory, options, candidateCount, exhaustive, maxDegreeOfParallelism, ct).Results;

    /// <summary>
    /// <see cref="ScreenNMinusK"/> with the single outages the pruned screen left out of the pairing (D02).
    /// </summary>
    public static NkScreenReport ScreenNMinusKReport(
        GridModel model, int k, Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1,
        CancellationToken ct = default)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "contingency depth k must be >= 1");
        solverFactory ??= () => SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        int n = model.Branches.Count;
        var none = Array.Empty<ContingencyResult>();
        if (k > n) return new NkScreenReport(none, none) { Note = NkScreenReport.TooFewBranches(n, k) };
        if (k == 1) return new NkScreenReport(ScreenBranchesParallel(model, solverFactory, options, maxDegreeOfParallelism, null, ct), none);

        // Candidate set: every branch when exhaustive, else the most-stressed single outages that solve.
        int[] candidates;
        IReadOnlyList<ContingencyResult> excluded = none;
        if (exhaustive)
        {
            candidates = Enumerable.Range(0, n).ToArray();
        }
        else
        {
            IReadOnlyList<ContingencyResult> single =
                ScreenBranchesParallel(model, solverFactory, options, maxDegreeOfParallelism, null, ct);
            candidates = RankCandidates(single, candidateCount, k, out excluded);
        }

        // Enumerate the k-subsets of the candidate set.
        var combos = new List<int[]>();
        Combinations(candidates, k, combos);

        var results = new ContingencyResult[combos.Count];
        using var local = new ThreadLocal<IPowerFlowSolver>(solverFactory);
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism,
            CancellationToken = ct,
        };
        Parallel.For(0, combos.Count, po, c => results[c] = ScreenOutageCore(model, combos[c], local.Value!, options));
        return new NkScreenReport(results.OrderByDescending(StressScore).ToList(), excluded)
            { Note = NkScreenReport.TooFewCandidates(candidates.Length, excluded.Count, k) };
    }

    /// <summary>
    /// The pruned candidate set of the N-k and N-1-1 screens (D02). Only single outages that SOLVE are ranked: a
    /// non-converged single outage (islanding, or no power-flow solution) scored +Infinity and so took the first
    /// places of the ranking, in branch order, which meant that on a network with radial feeders a small
    /// <c>candidateCount</c> paired nothing but outages that already island (GB reduced, 5 candidates: all 10 pairs
    /// islanded). Those outages are returned in <paramref name="excluded"/> (branch order) and reported separately.
    /// The solving outages are ranked exactly as before (stable <c>OrderByDescending</c> on
    /// <see cref="StressScore"/>, ties in branch order), and the top max(<paramref name="candidateCount"/>,
    /// <paramref name="minimum"/>) are kept, or all of them when fewer solve.
    /// </summary>
    private static int[] RankCandidates(IReadOnlyList<ContingencyResult> single, int candidateCount, int minimum,
        out IReadOnlyList<ContingencyResult> excluded)
    {
        var solvable = new List<int>(single.Count);
        var failed = new List<ContingencyResult>();
        for (int i = 0; i < single.Count; i++)
        {
            if (single[i].Converged) solvable.Add(i);
            else failed.Add(single[i]);
        }
        excluded = failed;
        int take = Math.Min(Math.Max(candidateCount, minimum), solvable.Count);
        return solvable.OrderByDescending(i => StressScore(single[i])).Take(take).ToArray();
    }

    /// <summary>
    /// N-1-1 screening: lose one element, let the operator take corrective action, then lose a SECOND
    /// element from that corrected state. The corrective step is what distinguishes N-1-1 from N-2 - a
    /// simultaneous double outage (N-2) has an order-independent post-outage power flow, so without an
    /// intervening operator action "A then B" would collapse to the N-2 pair {A,B}. Here the first
    /// outage is applied, <paramref name="correctiveAction"/> redispatches the post-contingency network
    /// (default: <see cref="RedispatchToRelieve"/>, a minimum-MW corrective redispatch that acts only when the first
    /// outage leaves a line or boundary over its limit), and only then is the
    /// second element lost. Pairs are therefore ORDERED (A =&gt; B differs from B =&gt; A, because the
    /// corrective dispatch is decided after the first loss). The corrective dispatch is computed once per
    /// first outage, not per pair. First and second outages are drawn from the ranked candidate set (as
    /// in <see cref="ScreenNMinus2"/>); results are returned worst-first.
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenNMinus1Minus1(
        GridModel model, Func<GridModel, GridModel>? correctiveAction = null,
        Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1,
        CancellationToken ct = default)
        => ScreenNMinus1Minus1Report(model, correctiveAction, solverFactory, options, candidateCount, exhaustive,
            maxDegreeOfParallelism, ct).Results;

    /// <summary>
    /// <see cref="ScreenNMinus1Minus1"/> with the single outages the pruned screen left out of the pairing (D02).
    /// </summary>
    public static NkScreenReport ScreenNMinus1Minus1Report(
        GridModel model, Func<GridModel, GridModel>? correctiveAction = null,
        Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int candidateCount = DefaultCandidateCount, bool exhaustive = false, int maxDegreeOfParallelism = -1,
        CancellationToken ct = default)
    {
        bool defaultAction = correctiveAction is null;   // GR-621: the default action reports its outcome
        correctiveAction ??= RedispatchToRelieve;
        solverFactory ??= () => SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        int n = model.Branches.Count;
        var none = Array.Empty<ContingencyResult>();
        if (n < 2) return new NkScreenReport(none, none) { Note = NkScreenReport.TooFewBranches(n, 2) };

        // Candidate set: every branch when exhaustive, else the most-stressed single outages that solve (D02).
        int[] candidates;
        IReadOnlyList<ContingencyResult> excluded = none;
        if (exhaustive)
        {
            candidates = Enumerable.Range(0, n).ToArray();
        }
        else
        {
            IReadOnlyList<ContingencyResult> single =
                ScreenBranchesParallel(model, solverFactory, options, maxDegreeOfParallelism, null, ct);
            candidates = RankCandidates(single, candidateCount, 2, out excluded);
        }

        // Corrective operating point per first outage: remove A, redispatch the post-contingency network,
        // then carry that dispatch back onto the full topology so screening {A,B} removes both from it.
        // Computed once per first outage (sequential + deterministic; the LP is pure) and reused for every
        // second outage.
        var correctedForFirst = new GridModel[candidates.Length];
        var outcomeForFirst = new RedispatchOutcome?[candidates.Length];
        for (int ia = 0; ia < candidates.Length; ia++)
        {
            GridModel postFirst = WithoutMany(model, new[] { candidates[ia] });
            GridModel redispatched;
            if (defaultAction)
            {
                // GR-621: the same call RedispatchToRelieve makes (so the dispatch is bit for bit as before), keeping
                // its outcome for the results.
                CorrectiveRedispatch plan = PlanCorrectiveRedispatch(postFirst);
                redispatched = plan.Model;
                outcomeForFirst[ia] = plan.Outcome;
            }
            else redispatched = correctiveAction(postFirst);
            correctedForFirst[ia] = Dispatch.Dispatcher.Clone(model, generators: redispatched.Generators);
        }

        // Ordered pairs (first, second) over the candidate positions.
        var pairs = new List<(int First, int Second)>(candidates.Length * Math.Max(0, candidates.Length - 1));
        for (int ia = 0; ia < candidates.Length; ia++)
            for (int ib = 0; ib < candidates.Length; ib++)
                if (ia != ib) pairs.Add((ia, ib));

        var results = new ContingencyResult[pairs.Count];
        using var local = new ThreadLocal<IPowerFlowSolver>(solverFactory);
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism,
            CancellationToken = ct,
        };
        Parallel.For(0, pairs.Count, po, k =>
        {
            (int ia, int ib) = pairs[k];
            int a = candidates[ia], b = candidates[ib];
            ContingencyResult r = ScreenOutageCore(correctedForFirst[ia], new[] { a, b }, local.Value!, options);
            // Relabel to the ordered "first => second" form (the core produces the unordered "A + B").
            results[k] = r with { Outage = $"{Label(model.Branches[a])} => {Label(model.Branches[b])}", CorrectiveOutcome = outcomeForFirst[ia] };
        });
        return new NkScreenReport(results.OrderByDescending(StressScore).ToList(), excluded)
            { Note = NkScreenReport.TooFewCandidates(candidates.Length, excluded.Count, 2) };
    }

    /// <summary>
    /// The default N-1-1 corrective action (D01, N069): <see cref="PlanCorrectiveRedispatch"/>'s model. The
    /// dispatch is unchanged unless the first outage leaves a rated line or a limited boundary over its limit, and
    /// then it moves as few MW as possible to relieve it. See <see cref="PlanCorrectiveRedispatch"/>.
    /// </summary>
    public static GridModel RedispatchToRelieve(GridModel model) => PlanCorrectiveRedispatch(model).Model;

    /// <summary>
    /// The corrective redispatch an operator makes after a first outage (October 2026, D01 and N069).
    /// <para>
    /// <b>What it replaced, and why.</b> The old corrective action ran a full security-constrained DC-OPF: it
    /// re-optimised EVERY generator against the indicative fuel-cost table and constrained only rated lines. On the
    /// reduced GB model that moved 31,810 to 32,380 MW of generation (the sum of |change|) after every meshed first
    /// outage, even when the post-outage network was secure, and pushed the B4 boundary from 48.4 % to 115.6 % of its
    /// 3,200 MW limit, so every converged N-1-1 pair reported a violation caused by the "correction" itself. It also
    /// left an islanded network's dispatch untouched (N069).
    /// </para>
    /// <para>
    /// <b>What it does now</b> (DC model, reactance only, as the old action):
    /// <list type="number">
    /// <item>Form the DC model of <paramref name="model"/> (one reference per DC island, <see cref="DistributionFactors"/>)
    /// and the DC flows of the PRESENT dispatch (each island's reference absorbs its imbalance, as the AC slack does).</item>
    /// <item>Monitor every in-service rated line (|flow| against RateA, read as MW, as the DC-OPF does) and every boundary
    /// with a limit (the signed sum of its branches' flows against LimitMw; branches matched by name, a later branch
    /// of the same name winning, exactly as <see cref="BoundaryAnalysis.Evaluate"/> does). If nothing is over its limit
    /// by more than <see cref="RedispatchLimitToleranceMw"/>, return the model unchanged: there is nothing to relieve,
    /// and an operator does not redispatch a secure system.</item>
    /// <item>Otherwise solve the minimum-change LP: for each generator an increase up_i in [0, Pmax_i - Pg_i] and a
    /// decrease dn_i in [0, Pg_i - Pmin_i]; minimise the total MW moved, sum(up_i + dn_i); keep every DC island's
    /// generation total unchanged (one balance row per island that has generators: sum over the island of
    /// up_i - dn_i = 0, which is the island-by-island dispatch of N069 and leaves each island's balancing machine where
    /// it was); and hold each monitored element inside its limit: -L_e &lt;= F_e + sum_i a_e,i (up_i - dn_i) &lt;= L_e,
    /// with a_e,i the element's PTDF sensitivity to generator i's bus. Constraint rows are generated lazily: the LP
    /// starts with the elements that are over their limit, and any other element that the redispatch pushes over its
    /// limit is added and the LP solved again, until no monitored element is over (each round adds at least one
    /// element, so this ends). An element no generator can influence (every sensitivity exactly zero, e.g. a line in an
    /// island with no generation) cannot be relieved by redispatch and is left out of the LP.</item>
    /// <item>An infeasible LP (or one that does not reach an optimum) leaves the dispatch unchanged: no fabricated
    /// operating point. The subsequent AC screen then reports the overload honestly.</item>
    /// </list>
    /// Deterministic: the LP (<see cref="BoundedSimplex"/>) and every loop are sequential and pure.
    /// </para>
    /// <para>
    /// <b>The reference unit's operating point (N083, October 2026).</b> Each DC island's reference bus absorbs the
    /// island's imbalance (island demand minus the island's stated generation), exactly as the AC slack does. So the
    /// unit that really sits at that point is not at its stated Pg but at its stated Pg plus that imbalance: after a
    /// first outage that sheds or strands load, the difference can be thousands of MW. Before N083 the LP bounded the
    /// reference unit's movement from its STATED Pg (up to Pmax - Pg, down to Pg - Pmin), so it could be offered room
    /// it did not have, or denied room it did. Now its bounds are taken from its DC-balanced (solved) output: the first
    /// generator, in model order, at the island's reference bus carries the whole imbalance (the rule MATPOWER's
    /// <c>pfsoln</c> uses for the slack unit's reported output). The DC flows, the balance rows and the written-back
    /// dispatch are unchanged in form: the adopted Pg is still the stated Pg plus the LP's change, and the reference
    /// unit's own movement changes no flow (its bus is its island's reference), only its island's balance. An island
    /// whose reference bus holds no generator has no unit to correct and keeps the stated values.
    /// </para>
    /// </summary>
    public static CorrectiveRedispatch PlanCorrectiveRedispatch(GridModel model)
        => PlanCorrectiveRedispatch(model, BoundedSimplex.NearTiePricingMargin);

    /// <summary>
    /// N262 (October 2026, ruled: the N240 near-tie rule for every BoundedSimplex user): <see cref="PlanCorrectiveRedispatch(GridModel)"/>
    /// with the minimum-change LP solved at an explicit pricing tie margin. Production always passes
    /// <see cref="BoundedSimplex.NearTiePricingMargin"/> (relative 1e-10): the LP costs every MW moved the same, so between
    /// units with equal sensitivities the strict "most negative reduced cost" rule let the last bit of a PTDF sum choose
    /// which unit moves; with the margin, reduced costs within round-off of the best count as tied and the lowest index
    /// enters. Tests pass 0 (the strict rule, the code every plan ran before N262) to prove that every plan whose pivot
    /// path meets no near-tie is bit-identical, and to measure what moves where one does.
    /// </summary>
    internal static CorrectiveRedispatch PlanCorrectiveRedispatch(GridModel model, double pricingTieMargin)
    {
        ArgumentNullException.ThrowIfNull(model);
        var noneNames = Array.Empty<string>();
        int g = model.Generators.Count;

        // 1. The DC model. Its absence is not an error of the caller: the AC screen that follows reports whatever
        //    state the network is in (an islanded or singular network fails to solve there, loudly).
        DistributionFactors df;
        try { df = DistributionFactors.Build(model); }
        catch (InvalidOperationException) { return new CorrectiveRedispatch(RedispatchOutcome.NoDcModel, model, 0.0, noneNames, null); }

        // Generator limits, validated as the DC-OPF validates them (R227: Pmin used as given; an inconsistent range is
        // an input error, not something to repair quietly).
        var pg = new double[g];
        var pmin = new double[g];
        var pmax = new double[g];
        for (int i = 0; i < g; i++)
        {
            Generator gen = model.Generators[i];
            if (!double.IsFinite(gen.Pg))
                throw new InvalidDataException(
                    $"Corrective redispatch: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus}) has Pg {gen.Pg} MW; an output must be finite.");
            if (!double.IsFinite(gen.Pmin))
                throw new InvalidDataException(
                    $"Corrective redispatch: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus}) has Pmin {gen.Pmin} MW; a dispatch floor must be finite.");
            if (double.IsNaN(gen.Pmax) || gen.Pmin > gen.Pmax)
                throw new InvalidDataException(
                    $"Corrective redispatch: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus}) has Pmin {gen.Pmin} MW above Pmax {gen.Pmax} MW.");
            pg[i] = gen.Pg;
            pmin[i] = gen.Pmin;
            pmax[i] = double.IsInfinity(gen.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : gen.Pmax;
        }

        // 2. The monitored elements and their present DC flows.
        List<MonitoredElement> elements = MonitoredElements(model);
        double[] flows = DcFlowsOf(model, df, pg);
        var overloaded = new List<int>();
        for (int e = 0; e < elements.Count; e++)
            if (Math.Abs(elements[e].Flow(flows)) > elements[e].LimitMw + RedispatchLimitToleranceMw) overloaded.Add(e);
        if (overloaded.Count == 0)
            return new CorrectiveRedispatch(RedispatchOutcome.NotNeeded, model, 0.0, noneNames, null);
        string[] overloadedNames = overloaded.Select(e => elements[e].Text).ToArray();
        if (g == 0) return new CorrectiveRedispatch(RedispatchOutcome.Infeasible, model, 0.0, overloadedNames, null);

        // Island of each generator (index into df.Islands), from the island bus lists.
        var islandOfBus = new Dictionary<int, int>();
        for (int k = 0; k < df.Islands.Count; k++)
            foreach (int id in df.Islands[k].BusIds) islandOfBus.TryAdd(id, k);
        var islandOfGen = new int[g];
        for (int i = 0; i < g; i++)
            islandOfGen[i] = islandOfBus.TryGetValue(model.Generators[i].Bus, out int k) ? k : -1;

        // Sensitivity of element e to generator i: sum over the element's branches of weight * PTDF(branch, bus_i).
        // PTDF columns are computed once per distinct generator bus, on demand.
        var ptdfOfBus = new Dictionary<int, double[]>();
        double[] Column(int bus)
        {
            if (!ptdfOfBus.TryGetValue(bus, out double[]? col)) { col = df.PtdfColumn(bus); ptdfOfBus[bus] = col; }
            return col;
        }
        var sensitivity = new Dictionary<int, double[]>();
        double[] SensitivityOf(int e)
        {
            if (sensitivity.TryGetValue(e, out double[]? s)) return s;
            s = new double[g];
            for (int i = 0; i < g; i++)
            {
                double[] col = Column(model.Generators[i].Bus);
                double a = 0.0;
                foreach ((int branch, double weight) in elements[e].Terms) a += weight * col[branch];
                s[i] = a;
            }
            sensitivity[e] = s;
            return s;
        }

        // N083: the operating point each unit's up / down room is measured from. Every unit except an island's
        // reference unit is at its stated Pg. The reference unit (the first generator, in model order, at the island's
        // reference bus) also carries the island's imbalance, island demand minus the island's stated generation,
        // because that is what the reference absorbs in the DC flows above (DcFlowsOf) and what the AC slack absorbs
        // in the solve that follows. Used ONLY for the LP's variable bounds: the flows and the adopted dispatch keep
        // the stated values plus the LP's change, so a unit the LP does not move keeps its stated Pg to the bit.
        double[] pgOperating = ReferenceBalancedOperatingPoint(model, df, pg, islandOfGen);

        // 3. Lazy constraint generation around the minimum-change LP.
        var active = new List<int>(overloaded);
        var inActive = new HashSet<int>(overloaded);
        LpStatus? lastStatus = null;
        double[] newPg = pg;
        while (true)
        {
            (LpStatus status, double[]? candidate) = SolveMinimumChange(elements, active, SensitivityOf,
                flows, pg, pgOperating, pmin, pmax, islandOfGen, pricingTieMargin);
            lastStatus = status;
            if (status != LpStatus.Optimal || candidate is null)
                return new CorrectiveRedispatch(RedispatchOutcome.Infeasible, model, 0.0, overloadedNames, status);

            double[] newFlows = DcFlowsOf(model, df, candidate);
            bool added = false;
            for (int e = 0; e < elements.Count; e++)
            {
                if (inActive.Contains(e)) continue;
                if (Math.Abs(elements[e].Flow(newFlows)) > elements[e].LimitMw + RedispatchLimitToleranceMw)
                {
                    active.Add(e);
                    inActive.Add(e);
                    added = true;
                }
            }
            newPg = candidate;
            if (!added) break;
        }

        // 4. Adopt the redispatch.
        var gens = model.Generators.ToList();
        double moved = 0.0;
        for (int i = 0; i < g; i++)
        {
            moved += Math.Abs(newPg[i] - pg[i]);
            gens[i] = gens[i] with { Pg = newPg[i] };
        }
        return new CorrectiveRedispatch(RedispatchOutcome.Applied, Dispatch.Dispatcher.Clone(model, generators: gens),
            moved, overloadedNames, lastStatus);
    }

    /// <summary>A monitored element is over its limit, for <see cref="PlanCorrectiveRedispatch"/>, when its |DC flow|
    /// exceeds the limit by more than this (MW). The DC-OPF's binding tolerance
    /// (<see cref="DcOptimalPowerFlow.BindingToleranceMw"/>, 1e-3 MW): an LP answer sitting exactly on a limit, give or
    /// take rounding, is not re-reported as an overload.</summary>
    public const double RedispatchLimitToleranceMw = DcOptimalPowerFlow.BindingToleranceMw;

    /// <summary>A line or boundary the corrective redispatch keeps inside its limit: its flow is the weighted sum of
    /// branch flows (one term of weight 1 for a line; the boundary's branches with their directions).</summary>
    private sealed record MonitoredElement(string Text, double LimitMw, IReadOnlyList<(int Branch, double Weight)> Terms)
    {
        public double Flow(double[] branchFlows)
        {
            double f = 0.0;
            foreach ((int branch, double weight) in Terms) f += weight * branchFlows[branch];
            return f;
        }
    }

    /// <summary>The monitored elements of <paramref name="model"/>: every in-service rated line, in branch order, then
    /// every boundary with a limit, in boundary order.</summary>
    private static List<MonitoredElement> MonitoredElements(GridModel model)
    {
        var list = new List<MonitoredElement>();
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            if (br.InService && br.RateAMva > 0)
                list.Add(new MonitoredElement($"branch {(string.IsNullOrEmpty(br.Name) ? $"{br.From}-{br.To}" : br.Name)}",
                    br.RateAMva, new[] { (l, 1.0) }));
        }

        // Branch name -> index, a later branch of the same name winning (BoundaryAnalysis.Evaluate's rule).
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int l = 0; l < model.Branches.Count; l++)
            if (!string.IsNullOrEmpty(model.Branches[l].Name)) byName[model.Branches[l].Name!] = l;
        foreach (Boundary b in model.Boundaries)
        {
            if (!double.IsFinite(b.LimitMw) || b.LimitMw < 0)
                throw new InvalidDataException(
                    $"Boundary '{b.Name}' has transfer limit {b.LimitMw} MW; a limit must be a finite non-negative MW value (0 = no limit set).");
            if (b.LimitMw == 0) continue;   // 0 = no limit authored: never over its limit (R224)
            var terms = new List<(int, double)>();
            foreach (BoundaryBranch bb in b.Branches)
                if (byName.TryGetValue(bb.Branch, out int l)) terms.Add((l, bb.Direction));
            list.Add(new MonitoredElement($"boundary {b.Name}", b.LimitMw, terms));
        }
        return list;
    }

    /// <summary>
    /// The operating point of each generator for the corrective redispatch's bounds (N083): a copy of the stated
    /// <paramref name="pg"/> in which each DC island's reference unit carries its island's imbalance.
    /// <para>
    /// For island k: imbalance_k = (island demand, <see cref="DcIsland.LoadMw"/>) - (sum of the stated Pg of the
    /// generators in island k, in model order). The reference unit is the first generator, in model order, whose bus is
    /// the island's <see cref="DcIsland.ReferenceBusId"/>; its operating point is its stated Pg + imbalance_k (MATPOWER
    /// <c>pfsoln</c> gives the slack bus's whole real-power balance to the first unit at that bus the same way). The DC
    /// model is lossless, so this is the reference unit's output in the DC solution of the present dispatch: exactly the
    /// injection <see cref="DcFlowsOf"/> leaves the reference to absorb. An island with no generator at its reference
    /// bus (a dead island, or a slack-typed bus with no unit) has no unit to correct.
    /// </para>
    /// <para>
    /// A balanced island (the stated dispatch already meets the island's demand) gets an imbalance of exactly zero
    /// only when the two sums agree to the bit; any rounding residue is a genuine part of the DC balance and is kept.
    /// </para>
    /// </summary>
    private static double[] ReferenceBalancedOperatingPoint(GridModel model, DistributionFactors df, double[] pg, int[] islandOfGen)
    {
        var op = (double[])pg.Clone();
        int g = pg.Length;
        for (int k = 0; k < df.Islands.Count; k++)
        {
            DcIsland island = df.Islands[k];
            int referenceUnit = -1;
            double islandGeneration = 0.0;
            for (int i = 0; i < g; i++)
            {
                if (islandOfGen[i] != k) continue;
                islandGeneration += pg[i];
                if (referenceUnit < 0 && model.Generators[i].Bus == island.ReferenceBusId) referenceUnit = i;
            }
            if (referenceUnit < 0) continue;   // no unit at the reference bus: nothing to correct
            op[referenceUnit] = pg[referenceUnit] + (island.LoadMw - islandGeneration);
        }
        return op;
    }

    /// <summary>DC branch flows (MW) for the dispatch <paramref name="pg"/> against the model's loads; each island's
    /// reference absorbs its imbalance.</summary>
    private static double[] DcFlowsOf(GridModel model, DistributionFactors df, double[] pg)
    {
        var inj = new Dictionary<int, double>();
        foreach (Bus b in model.Buses) if (b.Pd != 0) inj[b.Id] = inj.GetValueOrDefault(b.Id) - b.Pd;
        for (int i = 0; i < model.Generators.Count; i++)
        {
            int bus = model.Generators[i].Bus;
            inj[bus] = inj.GetValueOrDefault(bus) + pg[i];
        }
        return df.DcFlows(inj, model.BaseMva);
    }

    /// <summary>
    /// One round of the minimum-change LP of <see cref="PlanCorrectiveRedispatch"/>. Variables: up_0..up_{g-1},
    /// dn_0..dn_{g-1}, then one slack per controllable active element. Rows: one balance row per DC island that holds a
    /// generator (sum of up - dn over the island = 0), then per controllable active element
    /// sum_i a_e,i (up_i - dn_i) + s_e = L_e - F_e with s_e in [0, 2 L_e], so the new flow F_e + sum a (up - dn) =
    /// L_e - s_e lies in [-L_e, +L_e]. Objective: minimise sum(up_i + dn_i). Returns the new dispatch on Optimal: the
    /// stated <paramref name="pg"/> plus the change. The up / down bounds are measured from <paramref name="pgOperating"/>
    /// (N083: the stated Pg, except each island's reference unit at its DC-balanced output).
    /// </summary>
    private static (LpStatus Status, double[]? Pg) SolveMinimumChange(
        List<MonitoredElement> elements, List<int> active,
        Func<int, double[]> sensitivityOf, double[] flows, double[] pg, double[] pgOperating,
        double[] pmin, double[] pmax, int[] islandOfGen, double pricingTieMargin)
    {
        int g = pg.Length;

        // Balance rows: the islands that hold at least one generator, in island order.
        var islandRow = new Dictionary<int, int>();
        for (int i = 0; i < g; i++)
            if (islandOfGen[i] >= 0 && !islandRow.ContainsKey(islandOfGen[i])) islandRow[islandOfGen[i]] = -1;
        int row = 0;
        foreach (int k in islandRow.Keys.Order().ToList()) islandRow[k] = row++;
        int balanceRows = row;

        // Controllable active elements: at least one non-zero sensitivity. An element no generator can move cannot be
        // relieved by redispatch, so it is left out rather than making the whole LP infeasible.
        var rowsOf = new List<(int Element, double[] Sensitivity)>();
        foreach (int e in active)
        {
            double[] s = sensitivityOf(e);
            bool any = false;
            for (int i = 0; i < g; i++) if (s[i] != 0.0) { any = true; break; }
            if (any) rowsOf.Add((e, s));
        }

        int m = balanceRows + rowsOf.Count;
        int n = 2 * g + rowsOf.Count;
        var a = new double[m, n];
        var b = new double[m];
        var c = new double[n];
        var lo = new double[n];
        var hi = new double[n];
        for (int i = 0; i < g; i++)
        {
            // up_i then dn_i, each costing 1 per MW moved, bounded by the generator's headroom and footroom. A unit
            // already outside its own range gets no room in that direction (never pushed further out).
            // N083: measured from the operating point (the reference unit's DC-balanced output, not its stated Pg).
            c[i] = 1.0; lo[i] = 0.0; hi[i] = Math.Max(0.0, pmax[i] - pgOperating[i]);
            c[g + i] = 1.0; lo[g + i] = 0.0; hi[g + i] = Math.Max(0.0, pgOperating[i] - pmin[i]);
            if (islandOfGen[i] >= 0)
            {
                int r = islandRow[islandOfGen[i]];
                a[r, i] = 1.0;
                a[r, g + i] = -1.0;
            }
        }
        for (int j = 0; j < rowsOf.Count; j++)
        {
            (int e, double[] s) = rowsOf[j];
            int r = balanceRows + j;
            for (int i = 0; i < g; i++)
            {
                a[r, i] = s[i];
                a[r, g + i] = -s[i];
            }
            int sVar = 2 * g + j;
            double limit = elements[e].LimitMw;
            a[r, sVar] = 1.0;
            c[sVar] = 0.0; lo[sVar] = 0.0; hi[sVar] = 2.0 * limit;
            b[r] = limit - elements[e].Flow(flows);
        }

        // N262: the N240 near-tie pricing rule (production: BoundedSimplex.NearTiePricingMargin; see the overload above).
        (LpStatus status, double[] x, double _) = BoundedSimplex.Solve(a, b, c, lo, hi, pricingTieMargin: pricingTieMargin);
        if (status != LpStatus.Optimal) return (status, null);
        var newPg = new double[g];
        for (int i = 0; i < g; i++) newPg[i] = pg[i] + x[i] - x[g + i];
        return (status, newPg);
    }

    /// <summary>
    /// Take a set of branches out of service at once and re-solve - the general k-element outage that
    /// backs N-1 (a single index), N-2 (a pair) and N-k. Public so a caller can screen a specific,
    /// named combination (a known common-mode group - a double-circuit tower, a substation busbar)
    /// without going through the ranked enumeration.
    /// </summary>
    public static ContingencyResult ScreenOutage(
        GridModel model, IReadOnlyList<int> branchIndices,
        IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        solver ??= SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        return ScreenOutageCore(model, branchIndices, solver, options);
    }

    /// <summary>
    /// Solve and classify the intact network (no outage), with the same solver and options as the outages, so its
    /// violations can be netted out of every outage (D03, <see cref="NetAgainstBase(ContingencyResult, ContingencyResult)"/>).
    /// The result is labelled <see cref="BaseCaseLabel"/>. Not converged means the base case has no solution with
    /// these options; a screen against it cannot be netted and must not be reported as a success.
    /// </summary>
    public static ContingencyResult ScreenBaseCase(GridModel model, IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        solver ??= SolverSelector.CreateNewton(model);
        return ScreenOutageCore(model, Array.Empty<int>(), solver, options) with { Outage = BaseCaseLabel };
    }

    /// <summary>
    /// Net one outage's violations against the base case (October 2026, D03). Before D03 the violations were absolute,
    /// so a network already outside a limit in the base case (case14 holds bus 8 at 1.09 pu, above the 1.06 band)
    /// reported that same violation on every outage and no outage could ever be secure. Now a violation is counted
    /// against the outage only when its ELEMENT (the branch, the boundary, or the bus) is not already violating in the
    /// base case; the base case's own violations are reported once, as base-case violations, by the caller, and here
    /// in <see cref="ContingencyResult.PreExistingViolations"/>. An outage that pushes a NEW bus outside the band, or
    /// overloads a NEW branch or boundary, still violates.
    /// <para>The netted <see cref="ContingencyResult.Violations"/> keep the text form of the raw screen: one line per
    /// new branch and boundary overload (in branch, then boundary order), then one undervoltage line (the lowest
    /// voltage among the NEWLY low buses) and one overvoltage line (the highest among the newly high buses). With a base
    /// case that has no violations the netted list equals the raw list exactly. The numeric fields (loadings, MinVm,
    /// MaxVm) are not changed. A non-converged outage is returned unchanged apart from the flag (an islanding is never
    /// netted away).</para>
    /// <para>Throws <see cref="InvalidOperationException"/> when the base case did not converge: there is nothing to
    /// net against, and a caller must report that as a failure rather than screen against an unsolved base.</para>
    /// </summary>
    public static ContingencyResult NetAgainstBase(ContingencyResult outage, ContingencyResult baseCase)
        => NetAgainstBase(outage, baseCase, countWorsened: false);

    /// <summary>GR-125: a pre-existing branch or boundary overload counts as worsened by an outage when its loading rises
    /// by more than this many percentage points above the base case's.</summary>
    public const double WorsenedLoadingMarginPercent = 1.0;

    /// <summary>GR-125: a pre-existing under- or overvoltage counts as worsened by an outage when the bus moves more than
    /// this far (pu) further outside the band than in the base case.</summary>
    public const double WorsenedVoltageMarginPu = 0.005;

    /// <summary>
    /// <see cref="NetAgainstBase(ContingencyResult, ContingencyResult)"/> with the GR-125 choice made explicit. Every
    /// pre-existing violation that the outage made materially worse (see
    /// <see cref="ContingencyResult.WorsenedPreExistingViolations"/>) is always reported in that list; with
    /// <paramref name="countWorsened"/> true it is ALSO counted against the outage (moved from the pre-existing list to
    /// <see cref="ContingencyResult.Violations"/>), so a deepened base-case overload makes the outage insecure. The
    /// default (false) keeps the D03 verdicts, so an existing screen's secure / violation counts do not move.
    /// </summary>
    public static ContingencyResult NetAgainstBase(ContingencyResult outage, ContingencyResult baseCase, bool countWorsened)
    {
        ArgumentNullException.ThrowIfNull(outage);
        ArgumentNullException.ThrowIfNull(baseCase);
        if (!baseCase.Converged)
            throw new InvalidOperationException(
                "Contingency netting: the base case did not converge, so there is no base state to net the outage violations against.");
        if (!outage.Converged) return outage with { NettedAgainstBase = true };

        var baseValue = new Dictionary<(ContingencyViolationKind, int), double>();
        foreach (ContingencyViolation v in baseCase.Details) baseValue[(v.Kind, v.Element)] = v.Value;
        var fresh = new List<ContingencyViolation>();
        var pre = new List<ContingencyViolation>();
        var worsened = new List<ContingencyViolation>();
        foreach (ContingencyViolation v in outage.Details)
        {
            if (!baseValue.TryGetValue((v.Kind, v.Element), out double before)) { fresh.Add(v); continue; }
            bool worse = v.Kind switch
            {
                ContingencyViolationKind.Undervoltage => v.Value < before - WorsenedVoltageMarginPu,
                ContingencyViolationKind.Overvoltage => v.Value > before + WorsenedVoltageMarginPu,
                _ => v.Value > before + WorsenedLoadingMarginPercent,   // branch and boundary loadings, percent
            };
            if (worse) worsened.Add(v);
            (worse && countWorsened ? fresh : pre).Add(v);
        }
        // One pass in the raw screen's element order, so every list keeps it (branch, then boundary, then buses).
        return outage with
        {
            Violations = ViolationTexts(fresh),
            PreExistingViolations = ViolationTexts(pre),
            WorsenedPreExistingViolations = ViolationTexts(worsened),
            NettedAgainstBase = true,
        };
    }

    /// <summary>Net every result of a screen against the base case (D03), keeping the order. Throws as
    /// <see cref="NetAgainstBase(ContingencyResult, ContingencyResult)"/> does when the base case did not converge.</summary>
    public static IReadOnlyList<ContingencyResult> NetAgainstBase(IReadOnlyList<ContingencyResult> results, ContingencyResult baseCase)
    {
        ArgumentNullException.ThrowIfNull(results);
        var netted = new ContingencyResult[results.Count];
        for (int i = 0; i < results.Count; i++) netted[i] = NetAgainstBase(results[i], baseCase);
        return netted;
    }

    /// <summary>
    /// The text summary of a set of violation details, in the raw screen's exact format and order: branch overloads
    /// and boundary overloads one line each in the order given (the screen produces branch order, then boundary
    /// order), then a single undervoltage line at the lowest voltage and a single overvoltage line at the highest.
    /// </summary>
    private static List<string> ViolationTexts(IReadOnlyList<ContingencyViolation> details)
    {
        var texts = new List<string>();
        foreach (ContingencyViolation v in details)
        {
            if (v.Kind == ContingencyViolationKind.BranchOverload) texts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"branch {v.Name} {v.Value:F0}%"));
            else if (v.Kind == ContingencyViolationKind.BoundaryOverload) texts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"boundary {v.Name} {v.Value:F0}%"));
        }
        double minUnder = double.PositiveInfinity, maxOver = double.NegativeInfinity;
        bool anyUnder = false, anyOver = false;
        foreach (ContingencyViolation v in details)
        {
            if (v.Kind == ContingencyViolationKind.Undervoltage) { anyUnder = true; if (v.Value < minUnder) minUnder = v.Value; }
            else if (v.Kind == ContingencyViolationKind.Overvoltage) { anyOver = true; if (v.Value > maxOver) maxOver = v.Value; }
        }
        if (anyUnder) texts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"undervoltage {minUnder:F3} p.u."));
        if (anyOver) texts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"overvoltage {maxOver:F3} p.u."));
        return texts;
    }

    /// <summary>
    /// How hard a contingency stresses the system, for ranking (islanded/non-converged is worst).
    /// A converged case scores on branch and boundary loading plus voltage excursion beyond the
    /// 0.94-1.06 band, so a heavily-but-legally-loaded outage still ranks above a relaxed one - which
    /// is exactly the outage worth pairing for an N-2 screen. Higher is worse.
    /// </summary>
    public static double StressScore(ContingencyResult c)
    {
        if (!c.Converged) return double.PositiveInfinity;
        double s = c.MaxBranchLoadingPercent + c.WorstBoundaryLoadingPercent;
        s += Math.Max(0.0, VoltageLimits.OperationalLowPu - c.MinVm) * StressVoltageWeightPerPu;   // the shared operational band (R136)
        s += Math.Max(0.0, c.MaxVm - VoltageLimits.OperationalHighPu) * StressVoltageWeightPerPu;
        return s;
    }

    /// <summary>Take the branches at <paramref name="indices"/> out, re-solve, and classify the outcome.</summary>
    private static ContingencyResult ScreenOutageCore(
        GridModel model, IReadOnlyList<int> indices, IPowerFlowSolver solver, PowerFlowOptions? options)
    {
        string label = LabelMany(model, indices);
        GridModel reduced = WithoutMany(model, indices);
        // Reduced branch j -> its index in the ORIGINAL model (WithoutMany keeps model order), so a violation is keyed
        // by the same branch index in every outage (D03). (Computed here, before the solve, since F20 shares the rest of
        // this routine with the generator and bus outages; it is a pure function of the inputs, so nothing changes.)
        int[] original = KeptIndices(model.Branches.Count, indices);
        return ScreenStateCore(label, reduced, original, solver, options, postFault: indices.Count > 0, before: model);
    }

    /// <summary>
    /// Solve <paramref name="reduced"/> (the network after the outage) and classify it. <paramref name="original"/> maps
    /// each reduced branch to its index in the un-outaged model. <paramref name="postFault"/> is true for an outage state
    /// and false for the base case: with <see cref="PowerFlowOptions.PostFaultRating"/> set to RateB or RateC (F20), an
    /// outage state's branch loadings are judged against that rating; the base case always against RateA.
    /// </summary>
    private static ContingencyResult ScreenStateCore(
        string label, GridModel reduced, int[] original, IPowerFlowSolver solver, PowerFlowOptions? options, bool postFault,
        GridModel before)
    {
        // N138: the boundary names must resolve on the model the outage was taken from (the reduced model may
        // legitimately lack the outaged boundary branch, so it is read with EvaluateAfterOutage below).
        Boundary.RequireResolvedBranchNames(before);

        // N232 (October 2026, LF2): an outage that leaves a part of the network with no reference bus and a real-power
        // balance that part can never meet cannot converge, so it is reported at once with exactly the result the solve
        // would have ended in (see ProvablyUnsolvablePart for the proof). It used to be solved anyway: the direct LU
        // reported the singular Jacobian, and the ILU0 / BiCGSTAB fallback then ran to its 4m + 200 iteration cap
        // (about 6 s per such outage on SyntheticGb) before the Newton solve gave up. Every other state takes the
        // unchanged path below, so its result is bit-for-bit what it was (the check only reads the model).
        if (IsBuiltInNewton(solver))
        {
            PowerFlowOptions effective = options ?? new PowerFlowOptions();
            if (ProvablyUnsolvablePart(reduced, effective.Tolerance) is not null)
            {
                // A cancelled sweep stays cancelled: the solve would have thrown at its first iteration.
                effective.Cancellation.ThrowIfCancellationRequested();
                return NonConverged(label, Splits(before, reduced));
            }
        }

        PowerFlowResult r;
        try { r = solver.Solve(reduced, options); }
        catch (OperationCanceledException) { throw; }   // a cancelled sweep is cancelled, not an islanded outage
        catch (Exception ex)
        {
            // R225: every exception used to be labelled "islanded / no solution". Record what actually failed.
            // N107: and whether the outage split the network (the status column reads ISLANDED or NO SOLUTION).
            return new ContingencyResult(label, false, 0, 0, 0, 0, new[] { $"solver error: {ex.GetType().Name}: {ex.Message}" })
                { Islanded = Splits(before, reduced) };
        }

        if (!r.Converged) return NonConverged(label, Splits(before, reduced));

        // LineFlows is index-aligned with the reduced model's branches; original[j] is branch j's key (D03).
        var violations = new List<string>();
        var details = new List<ContingencyViolation>();
        double maxLoad = 0;
        // F20: the post-fault rating, only for an outage state and only when asked; otherwise the solver's RateA loading
        // is used exactly as before.
        PostFaultRating postRating = postFault && options is not null ? options.PostFaultRating : PostFaultRating.RateA;
        for (int j = 0; j < r.LineFlows.Count; j++)
        {
            LineFlow f = r.LineFlows[j];
            double loading = postRating == PostFaultRating.RateA ? f.LoadingPercent : PostFaultLoadingPercent(reduced.Branches[j], f, postRating);
            // A NaN rating used to pass silently: every loading formula guards "rating > 0", which NaN fails, so the
            // branch read as UNRATED (loading 0) and could never be overloaded, and a NaN loading (NaN > x is false)
            // raised neither the maximum nor a violation. The outage then scored as secure. A rating is 0 (unrated) or
            // a number above 0; NaN is refused by name here, as the AC-OPF refuses it.
            Branch rated = reduced.Branches[j];
            if (double.IsNaN(loading) || double.IsNaN(rated.RateAMva) || double.IsNaN(rated.RateBMva) || double.IsNaN(rated.RateCMva))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{label}: branch {(string.IsNullOrWhiteSpace(f.Name) ? $"{f.From}-{f.To}" : f.Name)} has rating A/B/C "
                    + $"{rated.RateAMva}/{rated.RateBMva}/{rated.RateCMva} MVA and loading {loading} %; a rating must be 0 (unrated) or a number above 0."));
            if (loading > maxLoad) maxLoad = loading;
            if (loading > OverloadThresholdPercent)
            {
                // D-DOC-157 (October 2026): a branch with no name (every MATPOWER branch, the built-in IEEE cases, cigre) is
                // named by its end buses "from-to", the label the outage column already uses. It used to print an empty
                // name ("branch  104%"). Only the label changes; the violation, its key and its value are as before.
                string branchLabel = string.IsNullOrWhiteSpace(f.Name) ? $"{f.From}-{f.To}" : f.Name;
                violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"branch {branchLabel} {loading:F0}%"));
                details.Add(new ContingencyViolation(ContingencyViolationKind.BranchOverload,
                    j < original.Length ? original[j] : -1, loading, branchLabel));
            }
        }

        double worstBoundary = 0;
        IReadOnlyList<BoundaryFlow> boundaries = BoundaryAnalysis.EvaluateAfterOutage(reduced, r);
        for (int k = 0; k < boundaries.Count; k++)
        {
            BoundaryFlow bf = boundaries[k];
            if (bf.LoadingPercent > worstBoundary) worstBoundary = bf.LoadingPercent;
            if (bf.Overloaded)
            {
                violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"boundary {bf.Name} {bf.LoadingPercent:F0}%"));
                details.Add(new ContingencyViolation(ContingencyViolationKind.BoundaryOverload, k, bf.LoadingPercent, bf.Name));
            }
        }

        double minVm = r.Vm.Min(), maxVm = r.Vm.Max();
        if (options?.PerBusVoltageLimits == true && VoltageLimits.HasPerBusLimits(reduced))
        {
            // F17 (October 2026), opt-in: each bus is judged against its OWN limits (Bus.VminPu / VmaxPu, from the
            // MATPOWER VMIN / VMAX columns or the JSON vminPu / vmaxPu keys), falling back side by side to the
            // operational band where the bus gives none. The summary lines keep their form: the lowest voltage among
            // the buses below their own limit, and the highest among those above theirs.
            double worstUnder = double.PositiveInfinity, worstOver = double.NegativeInfinity;
            var busById = new Dictionary<int, Bus>(reduced.Buses.Count);
            foreach (Bus b in reduced.Buses) busById.TryAdd(b.Id, b);   // first occurrence, as the solvers key buses
            for (int i = 0; i < r.Vm.Length && i < r.BusIds.Count; i++)
            {
                if (!busById.TryGetValue(r.BusIds[i], out Bus? bus))
                    throw new InvalidOperationException($"result bus {r.BusIds[i]} is not in the model it was solved from.");
                if (r.Vm[i] < VoltageLimits.OperationalLowFor(bus))
                {
                    details.Add(new ContingencyViolation(ContingencyViolationKind.Undervoltage, r.BusIds[i], r.Vm[i], ""));
                    if (r.Vm[i] < worstUnder) worstUnder = r.Vm[i];
                }
                else if (r.Vm[i] > VoltageLimits.OperationalHighFor(bus))
                {
                    details.Add(new ContingencyViolation(ContingencyViolationKind.Overvoltage, r.BusIds[i], r.Vm[i], ""));
                    if (r.Vm[i] > worstOver) worstOver = r.Vm[i];
                }
            }
            if (!double.IsPositiveInfinity(worstUnder)) violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"undervoltage {worstUnder:F3} p.u."));
            if (!double.IsNegativeInfinity(worstOver)) violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"overvoltage {worstOver:F3} p.u."));
            return new ContingencyResult(label, true, maxLoad, worstBoundary, minVm, maxVm, violations) { Details = details };
        }

        // The default: one operational band for every bus (unchanged code, so every existing verdict is unchanged).
        if (minVm < VoltageLimits.OperationalLowPu) violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"undervoltage {minVm:F3} p.u."));   // shared band (R136)
        if (maxVm > VoltageLimits.OperationalHighPu) violations.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"overvoltage {maxVm:F3} p.u."));
        // Per-bus detail of the same band check (one entry per bus outside it), for netting against the base case.
        for (int i = 0; i < r.Vm.Length && i < r.BusIds.Count; i++)
        {
            if (r.Vm[i] < VoltageLimits.OperationalLowPu)
                details.Add(new ContingencyViolation(ContingencyViolationKind.Undervoltage, r.BusIds[i], r.Vm[i], ""));
            else if (r.Vm[i] > VoltageLimits.OperationalHighPu)
                details.Add(new ContingencyViolation(ContingencyViolationKind.Overvoltage, r.BusIds[i], r.Vm[i], ""));
        }

        return new ContingencyResult(label, true, maxLoad, worstBoundary, minVm, maxVm, violations) { Details = details };
    }

    /// <summary>
    /// F20: a branch's loading (percent) against its short-term (RateB) or emergency (RateC) rating: the larger end MVA,
    /// computed from the solved end flows exactly as the solver computes it for RateA (|S_from| and |S_to| as complex
    /// magnitudes, the larger divided by the rating, times 100). A branch with no such rating (0) falls back to its RateA
    /// loading (the solver's own figure, unchanged); an unrated branch (RateA 0 too) is never overloaded.
    /// </summary>
    internal static double PostFaultLoadingPercent(Branch br, LineFlow f, PostFaultRating which)
    {
        double rating = which switch
        {
            PostFaultRating.RateB => br.RateBMva,
            PostFaultRating.RateC => br.RateCMva,
            _ => 0.0,
        };
        if (!(rating > 0)) return f.LoadingPercent;
        double mva = Math.Max(new System.Numerics.Complex(f.PFromMw, f.QFromMvar).Magnitude, new System.Numerics.Complex(f.PToMw, f.QToMvar).Magnitude);
        return mva / rating * 100.0;
    }

    // =================================================================================================================
    // F20 (October 2026): generator and bus outages beside the branch outages.
    // =================================================================================================================

    /// <summary>
    /// The single outages of the given kinds, in a fixed order: every branch (branch order), then every in-service
    /// generator (model order), then every bus (model order). With branches alone this is exactly the historical N-1 list.
    /// </summary>
    public static IReadOnlyList<ElementOutage> SingleOutages(GridModel model, bool branches, bool generators, bool buses)
    {
        ArgumentNullException.ThrowIfNull(model);
        var list = new List<ElementOutage>();
        if (branches) for (int i = 0; i < model.Branches.Count; i++) list.Add(new ElementOutage(OutageKind.Branch, i));
        if (generators) for (int i = 0; i < model.Generators.Count; i++) list.Add(new ElementOutage(OutageKind.Generator, i));
        if (buses) for (int i = 0; i < model.Buses.Count; i++) list.Add(new ElementOutage(OutageKind.Bus, i));
        return list;
    }

    /// <summary>
    /// Screen a list of single-element outages (F20), in order, with one solver. A branch outage is screened by exactly
    /// the code <see cref="ScreenBranches"/> uses (same result, bit for bit); a generator or bus outage by
    /// <see cref="ScreenElementOutage"/>.
    /// </summary>
    public static IReadOnlyList<ContingencyResult> ScreenElementOutages(
        GridModel model, IReadOnlyList<ElementOutage> outages, IPowerFlowSolver? solver = null, PowerFlowOptions? options = null,
        OutageDispatch dispatch = OutageDispatch.ReferenceBus)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(outages);
        solver ??= SolverSelector.CreateNewton(model);
        var results = new List<ContingencyResult>(outages.Count);
        foreach (ElementOutage o in outages) results.Add(ScreenElementOutage(model, o, solver, options, dispatch));
        return results;
    }

    /// <summary>
    /// Screen one single-element outage (F20).
    /// <list type="bullet">
    /// <item><b>Branch</b>: the historical N-1 outage (<see cref="ScreenOutage"/>).</item>
    /// <item><b>Generator</b>: the unit is removed; its scheduled output is lost and the reference bus picks it up in the
    /// re-solve (the same balancing a branch outage's loss change gets). A PV bus left with no unit is solved as PQ by the
    /// engines (R283).</item>
    /// <item><b>Bus</b>: the bus is removed with every branch touching it, every unit on it and its load (lost load and
    /// generation are reported on the result, not judged).</item>
    /// </list>
    /// When the outage takes away the last in-service unit of a reference (slack) bus, or the reference bus itself, the
    /// reference moves to the bus of the largest remaining unit by |Pg| (first in model order among ties) in the same
    /// connected part of the network: the rule the DC islands and the AC island pre-pass use. The move is said in
    /// <see cref="ContingencyResult.Note"/>. When no unit remains there, the outage is reported as not solved. An outage
    /// that splits the network is reported exactly as a splitting branch outage is (islanded / no solution).
    /// <para>N124: <paramref name="dispatch"/> chooses who picks up the lost real power (<see cref="OutageDispatch"/>).
    /// The default, the reference bus, is the F20 screen unchanged. With <see cref="OutageDispatch.GovernorDroop"/> or
    /// <see cref="OutageDispatch.Inertia"/> the outage's net lost power (lost generation minus lost load) is first shared
    /// among the remaining units by <see cref="ShareLostPower"/> (their scheduled Pg raised or lowered within their
    /// limits), and only then is the state re-solved, so the reference takes just the change in losses and anything the
    /// units had no headroom for. The sharing is said in <see cref="ContingencyResult.Note"/>. A branch outage takes
    /// no generation and is unaffected.</para>
    /// </summary>
    public static ContingencyResult ScreenElementOutage(GridModel model, ElementOutage outage, IPowerFlowSolver? solver = null,
        PowerFlowOptions? options = null, OutageDispatch dispatch = OutageDispatch.ReferenceBus)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(outage);
        solver ??= SolverSelector.CreateNewton(model);
        switch (outage.Kind)
        {
            case OutageKind.Branch:
                if (outage.Index < 0 || outage.Index >= model.Branches.Count)
                    throw new ArgumentOutOfRangeException(nameof(outage), $"branch outage {outage.Index} is outside 0..{model.Branches.Count - 1}.");
                return ScreenOutageCore(model, new[] { outage.Index }, solver, options);

            case OutageKind.Generator:
            {
                if (outage.Index < 0 || outage.Index >= model.Generators.Count)
                    throw new ArgumentOutOfRangeException(nameof(outage), $"generator outage {outage.Index} is outside 0..{model.Generators.Count - 1}.");
                Generator gen = model.Generators[outage.Index];
                string label = "gen " + (string.IsNullOrEmpty(gen.Name) ? $"{outage.Index} at bus {gen.Bus}" : $"{gen.Name} (bus {gen.Bus})");
                var gens = new List<Generator>(model.Generators.Count - 1);
                for (int i = 0; i < model.Generators.Count; i++) if (i != outage.Index) gens.Add(model.Generators[i]);
                (IReadOnlyList<Bus>? buses, string? note, bool noUnitLeft) = ReelectReference(model, model.Branches, gens, removedBusId: null, gen.Bus);
                // N107: removing a unit never splits the network, so a unit-less reference is "no solution", not "islanded".
                if (noUnitLeft)
                    return NonConverged(label, islanded: false) with { Kind = OutageKind.Generator, LostGenerationMw = gen.Pg, Note = note };
                GridModel reduced = model.CopyWith(buses: buses, generators: gens);
                if (dispatch != OutageDispatch.ReferenceBus)   // N124: share the lost output before the re-solve
                {
                    (reduced, string shareNote) = ShareLostPower(reduced, gen.Pg, dispatch);
                    note = note is null ? shareNote : note + "; " + shareNote;
                }
                int[] original = KeptIndices(model.Branches.Count, Array.Empty<int>());
                return ScreenStateCore(label, reduced, original, solver, options, postFault: true, before: model)
                    with { Kind = OutageKind.Generator, LostGenerationMw = gen.Pg, Note = note };
            }

            case OutageKind.Bus:
            {
                if (outage.Index < 0 || outage.Index >= model.Buses.Count)
                    throw new ArgumentOutOfRangeException(nameof(outage), $"bus outage {outage.Index} is outside 0..{model.Buses.Count - 1}.");
                Bus bus = model.Buses[outage.Index];
                string label = "bus " + bus.Id + (string.IsNullOrEmpty(bus.Name) ? "" : $" {bus.Name}");
                var removedBranches = new List<int>();
                for (int j = 0; j < model.Branches.Count; j++)
                    if (model.Branches[j].From == bus.Id || model.Branches[j].To == bus.Id) removedBranches.Add(j);
                var keptBranches = new List<Branch>(model.Branches.Count - removedBranches.Count);
                var removedSet = new HashSet<int>(removedBranches);
                for (int j = 0; j < model.Branches.Count; j++) if (!removedSet.Contains(j)) keptBranches.Add(model.Branches[j]);
                var gens = new List<Generator>(model.Generators.Count);
                double lostGen = 0.0;
                foreach (Generator g in model.Generators)
                {
                    if (g.Bus == bus.Id) lostGen += g.Pg;
                    else gens.Add(g);
                }
                (IReadOnlyList<Bus>? buses, string? note, bool noUnitLeft) = ReelectReference(model, keptBranches, gens, removedBusId: bus.Id, bus.Id);
                var result = new List<Bus>(model.Buses.Count - 1);
                foreach (Bus b in buses ?? model.Buses) if (b.Id != bus.Id) result.Add(b);
                GridModel reduced = model.CopyWith(buses: result, generators: gens, branches: keptBranches);
                if (noUnitLeft)
                    return NonConverged(label, Splits(model, reduced)) with { Kind = OutageKind.Bus, LostLoadMw = bus.Pd, LostGenerationMw = lostGen, Note = note };
                if (dispatch != OutageDispatch.ReferenceBus)   // N124: share the net lost power (generation less load)
                {
                    (reduced, string shareNote) = ShareLostPower(reduced, lostGen - bus.Pd, dispatch);
                    note = note is null ? shareNote : note + "; " + shareNote;
                }
                int[] original = KeptIndices(model.Branches.Count, removedBranches);
                return ScreenStateCore(label, reduced, original, solver, options, postFault: true, before: model)
                    with { Kind = OutageKind.Bus, LostLoadMw = bus.Pd, LostGenerationMw = lostGen, Note = note };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(outage), $"unknown outage kind {outage.Kind}.");
        }
    }

    /// <summary>
    /// F20: keep the network solvable after a generator or bus outage. If the bus <paramref name="affectedBusId"/> was a
    /// reference (slack) bus and is gone (<paramref name="removedBusId"/>) or has no in-service unit left in
    /// <paramref name="gens"/>, it is demoted to PQ (when it stays) and the reference moves to the bus of the largest
    /// remaining unit by |Pg| (first in model order among ties) among the buses connected to it over
    /// <paramref name="branches"/> (in service). Returns the new bus list (null when nothing changes), the note, and
    /// whether no unit was left to take the reference.
    /// </summary>
    private static (IReadOnlyList<Bus>? Buses, string? Note, bool NoUnitLeft) ReelectReference(
        GridModel model, IReadOnlyList<Branch> branches, IReadOnlyList<Generator> gens, int? removedBusId, int affectedBusId)
    {
        Bus? affected = null;
        foreach (Bus b in model.Buses) if (b.Id == affectedBusId) { affected = b; break; }
        if (affected is null || affected.Type != BusType.Slack) return (null, null, false);
        bool removed = removedBusId == affectedBusId;
        if (!removed && gens.Any(g => g.Bus == affectedBusId)) return (null, null, false);   // another unit still holds it

        // The connected part of the network the reference served: from the affected bus itself, or (when the bus is
        // gone) from each of its former neighbours. Breadth-first over in-service branches.
        var adjacency = new Dictionary<int, List<int>>();
        void Link(int a, int c)
        {
            if (!adjacency.TryGetValue(a, out List<int>? l)) adjacency[a] = l = new List<int>();
            l.Add(c);
        }
        foreach (Branch br in branches) if (br.InService) { Link(br.From, br.To); Link(br.To, br.From); }
        var seeds = new List<int>();
        if (!removed) seeds.Add(affectedBusId);
        else
            foreach (Branch br in model.Branches)
                if (br.InService && (br.From == affectedBusId || br.To == affectedBusId)) seeds.Add(br.From == affectedBusId ? br.To : br.From);
        var reach = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int s in seeds) if (reach.Add(s)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (!adjacency.TryGetValue(u, out List<int>? next)) continue;
            foreach (int v in next) if (v != removedBusId && reach.Add(v)) queue.Enqueue(v);
        }

        Generator? best = null;
        foreach (Generator g in gens)
            if (reach.Contains(g.Bus) && (best is null || Math.Abs(g.Pg) > Math.Abs(best.Pg))) best = g;
        if (best is null)
            return (null, $"no in-service unit is left to take over the reference of bus {affectedBusId}", true);

        var buses = new List<Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses)
        {
            if (b.Id == affectedBusId && !removed) buses.Add(b with { Type = BusType.Pq });
            else if (b.Id == best.Bus && b.Type != BusType.Slack) buses.Add(b with { Type = BusType.Slack });
            else buses.Add(b);
        }
        return (buses, $"reference moved from bus {affectedBusId} to bus {best.Bus}", false);
    }

    /// <summary>
    /// N124 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): share an outage's net lost
    /// real power <paramref name="lostMw"/> (MW; positive = generation lost, so the others must produce more) among the
    /// remaining units of <paramref name="reduced"/>, the post-outage model, before its re-solve.
    /// <para><b>Who takes part.</b> Every unit of <paramref name="reduced"/> (the in-service list) that is a synchronous
    /// machine with a governor to respond (not <see cref="Generator.PowerFlowOnly"/>, not converter plant), with a
    /// positive weight, on a bus connected over in-service branches to a reference bus (a unit in a dead part could not
    /// deliver). Weights: <see cref="OutageDispatch.GovernorDroop"/>, the effective MVA base
    /// (<see cref="Dynamics.Models.MachineBase.EffectiveMva"/>; MBase / R with a uniform droop R, since the model
    /// carries no per-unit droop); <see cref="OutageDispatch.Inertia"/>, H x that base (units with H &lt;= 0 take no
    /// share).</para>
    /// <para><b>Limits (water filling).</b> Each unit moves within [Pmin, Pmax] from its scheduled Pg. The shares are
    /// computed for all active units; every unit whose share exceeds its headroom is fixed at its limit and removed, and
    /// the remainder is shared again among the rest, until every share fits or no unit is left. Whatever cannot be
    /// placed is left to the reference bus and said in the note. The loop visits units in model order with plain
    /// arithmetic, so the result is the same on every machine.</para>
    /// <para>The reference bus's own units are participants like any other (their scheduled Pg moves with their share;
    /// in the solve the reference then supplies its share plus the change in losses). Returns the model with the moved
    /// dispatch and a note in words; with nothing to share (0 MW) the model is returned unchanged.</para>
    /// </summary>
    internal static (GridModel Model, string Note) ShareLostPower(GridModel reduced, double lostMw, OutageDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(reduced);
        if (dispatch == OutageDispatch.ReferenceBus)
            throw new ArgumentOutOfRangeException(nameof(dispatch), dispatch, "ShareLostPower shares among units; the reference-bus mode does not share.");
        if (!double.IsFinite(lostMw))
            throw new ArgumentOutOfRangeException(nameof(lostMw), lostMw, "The lost power must be a finite number of MW.");
        string how = dispatch == OutageDispatch.GovernorDroop ? "governor droop" : "inertia";
        if (lostMw == 0.0) return (reduced, $"no net power lost, nothing to share by {how}");

        // Buses reachable from a reference bus over in-service branches.
        var adjacency = new Dictionary<int, List<int>>();
        foreach (Branch br in reduced.Branches)
        {
            if (!br.InService) continue;
            if (!adjacency.TryGetValue(br.From, out List<int>? a)) adjacency[br.From] = a = new List<int>();
            a.Add(br.To);
            if (!adjacency.TryGetValue(br.To, out List<int>? c)) adjacency[br.To] = c = new List<int>();
            c.Add(br.From);
        }
        var reach = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (Bus b in reduced.Buses) if (b.Type == BusType.Slack && reach.Add(b.Id)) queue.Enqueue(b.Id);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (!adjacency.TryGetValue(u, out List<int>? next)) continue;
            foreach (int v in next) if (reach.Add(v)) queue.Enqueue(v);
        }

        IReadOnlyList<Generator> gens = reduced.Generators;
        int n = gens.Count;
        var weight = new double[n];
        var room = new double[n];      // headroom in the direction of the change (MW, >= 0, may be infinite)
        var delta = new double[n];
        var active = new List<int>();
        bool raise = lostMw > 0.0;
        for (int i = 0; i < n; i++)
        {
            Generator g = gens[i];
            if (g.PowerFlowOnly || g.IsConverterInterfaced || !reach.Contains(g.Bus)) continue;
            double mva = Dynamics.Models.MachineBase.EffectiveMva(g, reduced.BaseMva);
            double w = dispatch == OutageDispatch.GovernorDroop ? mva : g.H * mva;
            if (!(w > 0.0) || !double.IsFinite(w)) continue;
            double r = raise ? g.Pmax - g.Pg : g.Pg - g.Pmin;
            if (!(r > 0.0)) continue;   // already at (or beyond) its limit in this direction: no headroom
            weight[i] = w;
            room[i] = r;
            active.Add(i);
        }

        double remaining = Math.Abs(lostMw);
        int fixedAtLimit = 0;
        int participants = active.Count;
        while (active.Count > 0 && remaining > 0.0)
        {
            double wsum = 0.0;
            foreach (int i in active) wsum += weight[i];
            // Units whose proportional share would exceed their headroom go to their limit first.
            var capped = new List<int>();
            foreach (int i in active) if (remaining * weight[i] / wsum > room[i]) capped.Add(i);
            if (capped.Count == 0)
            {
                foreach (int i in active) delta[i] += remaining * weight[i] / wsum;
                remaining = 0.0;
                break;
            }
            foreach (int i in capped)
            {
                delta[i] += room[i];
                remaining -= room[i];
                active.Remove(i);
                fixedAtLimit++;
            }
            if (remaining < 0.0) remaining = 0.0;   // rounding of the subtraction; nothing is over-placed
        }

        double sign = raise ? 1.0 : -1.0;
        var moved = new Generator[n];
        for (int i = 0; i < n; i++) moved[i] = delta[i] == 0.0 ? gens[i] : gens[i] with { Pg = gens[i].Pg + sign * delta[i] };
        string what = raise ? "lost" : "surplus";
        // Bug hunt 2026-10-29, finding 72 (decided, documented): "left to the reference" means the reduced model's slack
        // bus, which after a reference-unit outage is the re-elected one (the largest remaining unit), and that is the
        // intended absorber. The note does not name it because its exact text is pinned (B3GovernorsDynamicDataTests,
        // N124); the reference is the Slack bus of the post-outage model the result is reported on.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string note = string.Create(inv, $"{what} {Math.Abs(lostMw):F1} MW shared by {how} over {participants} unit(s)")
            + (fixedAtLimit > 0 ? string.Create(inv, $", {fixedAtLimit} at a limit") : "")
            + (remaining > 0.0 ? string.Create(inv, $"; {remaining:F1} MW beyond their headroom left to the reference") : "");
        return (reduced.CopyWith(generators: moved), note);
    }

    /// <summary>The original indices of the branches that remain after removing <paramref name="removed"/>, in order.</summary>
    private static int[] KeptIndices(int count, IReadOnlyList<int> removed)
    {
        var skip = removed as HashSet<int> ?? new HashSet<int>(removed);
        var kept = new List<int>(count);
        for (int j = 0; j < count; j++) if (!skip.Contains(j)) kept.Add(j);
        return kept.ToArray();
    }

    // N107 (October 2026): a state with no converged solution says WHY: "islanded" when the outage split the network
    // (more connected parts than before it), "no solution" when the network is still in one piece (or as many pieces as
    // before) and the power flow found no solution, a voltage collapse for example. Both used to read "islanded / no
    // solution".
    private static ContingencyResult NonConverged(string label, bool islanded) =>
        new(label, false, 0, 0, 0, 0, new[] { islanded ? "islanded" : "no solution" }) { Islanded = islanded };

    /// <summary>N107: true when <paramref name="after"/> has more connected parts than <paramref name="before"/>.</summary>
    internal static bool Splits(GridModel before, GridModel after) => ConnectedParts(after) > ConnectedParts(before);

    /// <summary>
    /// N107: the number of connected parts of a model: its buses joined by in-service branches whose two end buses are
    /// both in the model (union-find, no solve). A bus with no branch is a part of its own. Used only to label a
    /// non-converged outage; it reads the topology and changes nothing.
    /// </summary>
    internal static int ConnectedParts(GridModel m)
    {
        var index = new Dictionary<int, int>(m.Buses.Count);
        foreach (Bus b in m.Buses) index.TryAdd(b.Id, index.Count);
        int[] parent = new int[index.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        int parts = parent.Length;
        foreach (Branch br in m.Branches)
        {
            if (!br.InService || !index.TryGetValue(br.From, out int a) || !index.TryGetValue(br.To, out int c)) continue;
            int ra = Find(a), rc = Find(c);
            if (ra != rc) { parent[ra] = rc; parts--; }
        }
        return parts;
    }

    /// <summary>N232: a connected part of a model that no built-in Newton solve can bring to convergence (see
    /// <see cref="ProvablyUnsolvablePart"/>). <paramref name="BusIds"/> in model order; <paramref name="NetInjectionPu"/>
    /// is the part's specified net real injection, sum of (Pg - Pd) / BaseMva; <paramref name="Reason"/> names which of
    /// the two proofs applies.</summary>
    internal sealed record UnsolvablePart(IReadOnlyList<int> BusIds, double NetInjectionPu, string Reason);

    /// <summary>Margin (pu on the system base) added to the convergence slack of the N232 test. In exact arithmetic the
    /// identity "sum of the computed injections of a part = its real losses" holds exactly; in floating point the sum
    /// carries rounding of order (number of Y terms) x 2^-52 x |V|^2 |Y|, far below 1e-6 pu for any state with
    /// physical voltages. 1e-6 pu is 0.1 kW on a 100 MVA base, so a part is only judged unsolvable when its imbalance
    /// is real power, never a rounding residue.</summary>
    internal const double UnsolvableMarginPu = 1e-6;

    /// <summary>
    /// N232 (October 2026, LF2): true when <paramref name="solver"/> is one of GridSim's own Newton engines (dense or
    /// sparse, on any linear backend, bare or inside the OLTC loop), whose convergence test is the one the N232 proof
    /// relies on: every P equation of every non-reference bus within <see cref="PowerFlowOptions.Tolerance"/>. Any other
    /// <see cref="IPowerFlowSolver"/> (a caller's own, which might handle islands its own way, or a test double) is
    /// always called, exactly as before.
    /// </summary>
    internal static bool IsBuiltInNewton(IPowerFlowSolver solver) => solver switch
    {
        NewtonRaphsonPowerFlow or SparseNewtonRaphsonPowerFlow => true,
        OltcLoopPowerFlow loop => IsBuiltInNewton(loop.Engine),
        _ => false,
    };

    /// <summary>
    /// N232 (October 2026, LF2): the first connected part of <paramref name="m"/> (buses joined by in-service branches,
    /// parts in order of their first bus in the model) that a Newton power flow provably cannot solve, or null.
    ///
    /// <para><b>The claim.</b> Let I be a part with NO reference (slack) bus. Every bus of I is then PV or PQ, so every
    /// bus of I has a P equation, and the Newton engines report convergence only when every equation's mismatch is below
    /// the tolerance tol: |Pspec_i - P_i(V)| &lt; tol for each i in I. Adding those n_I inequalities:
    /// |sum Pspec_I - sum P_I(V)| &lt; n_I tol. The parts share no branch, so sum P_I(V) is exactly the real power
    /// absorbed inside I: the series losses |I_b|^2 R_b of its branches (an ideal tap or phase shifter absorbs nothing,
    /// line charging is reactive) plus its shunt conductances Gs_i Vm_i^2. Hence:</para>
    /// <list type="number">
    /// <item><b>Load part</b>: every branch of I has R &gt;= 0 and every bus Gs &gt;= 0, so sum P_I(V) &gt;= 0 for EVERY
    /// voltage state. If sum Pspec_I &lt;= -(n_I tol + margin), no state satisfies the inequality: the part's demand
    /// exceeds its generation and nothing can import the difference.</item>
    /// <item><b>Lossless part</b>: every branch of I has R = 0 and every bus Gs = 0 (a single bus with no branch and no
    /// shunt is the common case: a generator or load cut off on a radial spur), so sum P_I(V) = 0 for every state. If
    /// |sum Pspec_I| &gt;= n_I tol + margin, no state satisfies it.</item>
    /// </list>
    /// <para>Either way the engine cannot report convergence, whatever its iterates, linear backend, Q-limit or OLTC
    /// passes (those change Q equations and taps, never a P specification or a resistance), so its result is
    /// non-converged and the screen's verdict is the one <see cref="NonConverged"/> gives, which is what the screen now
    /// returns without solving. The margin is <see cref="UnsolvableMarginPu"/>.</para>
    ///
    /// <para><b>Left to the solve, as before</b> (so their results are unchanged): a part with a reference bus; a part
    /// with a voltage-dependent (ZIP) load (its P specification moves with the voltage); a lossy part whose generation
    /// exceeds its demand (the losses might absorb the surplus); a part whose net injection is within the margin (for
    /// example case14's synchronous condenser at bus 8, cut off by branch 7-8: Pg = Pd = 0, so its P equation holds and
    /// the screen still reports the main network solved); and any model with a branch or unit on a bus it does not hold
    /// (the engine refuses those itself, and its error is reported as before).</para>
    ///
    /// <para>Benchmark: PSS/E ACCC and PowerFactory contingency analysis detect islands topologically before the solve
    /// and never iterate an island without a swing bus; GridSim keeps its own verdict for such an outage (ISLANDED,
    /// N107) and only stops spending the iteration budget on a solve that cannot succeed.</para>
    /// </summary>
    internal static UnsolvablePart? ProvablyUnsolvablePart(GridModel m, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(m);
        // A tolerance that is not a positive finite number gives no proof (the engine would judge it its own way).
        if (!(tolerance > 0.0) || !double.IsFinite(tolerance)) return null;
        if (!(m.BaseMva > 0.0) || !double.IsFinite(m.BaseMva)) return null;

        int n = m.Buses.Count;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            if (!index.TryAdd(m.Buses[i].Id, i)) return null;   // duplicate bus id: the engine's to refuse, not ours

        // Union-find over bus indices, joined by in-service branches. A branch or a unit naming a bus the model does not
        // hold makes the model one the engine refuses (an error the screen records): leave it to the solve.
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (Branch br in m.Branches)
        {
            if (!br.InService) continue;
            if (!index.TryGetValue(br.From, out int a) || !index.TryGetValue(br.To, out int c)) return null;
            int ra = Find(a), rc = Find(c);
            if (ra != rc) parent[ra] = rc;
        }
        var pgByBus = new double[n];
        foreach (Generator g in m.Generators)
        {
            if (!index.TryGetValue(g.Bus, out int gi)) return null;
            pgByBus[gi] += g.Pg;
        }

        // Per part (keyed by its root): reference present, any ZIP load, any branch with negative or non-zero R, any
        // negative or non-zero Gs, bus count, and the specified net injection. Parts are visited in the order of their
        // first bus in the model, so the part returned (and its bus list) is deterministic.
        // A part's root is one of its bus indices, so per-part facts live in arrays indexed by that root.
        var order = new List<int>();
        var hasSlack = new bool[n];
        var hasZip = new bool[n];
        var anyNegativeLoss = new bool[n];
        var anyLoss = new bool[n];
        var count = new int[n];
        var net = new double[n];
        for (int i = 0; i < n; i++)
        {
            Bus b = m.Buses[i];
            int r = Find(i);
            if (count[r] == 0) order.Add(r);   // the first bus of this part in model order
            count[r]++;
            if (b.Type == BusType.Slack) hasSlack[r] = true;
            if (b.Zip is { VariesWithVoltage: true }) hasZip[r] = true;
            if (!double.IsFinite(b.Gs) || !double.IsFinite(b.Pd) || !double.IsFinite(pgByBus[i])) return null;
            if (b.Gs < 0.0) anyNegativeLoss[r] = true;
            if (b.Gs != 0.0) anyLoss[r] = true;
            // The engines' own specification, per bus: (sum of the bus's unit outputs - Pd) / BaseMva.
            net[r] += (pgByBus[i] - b.Pd) / m.BaseMva;
        }
        foreach (Branch br in m.Branches)
        {
            if (!br.InService) continue;
            int r = Find(index[br.From]);   // both ends are in the same part by construction
            if (!double.IsFinite(br.R)) return null;
            if (br.R < 0.0) anyNegativeLoss[r] = true;
            if (br.R != 0.0) anyLoss[r] = true;
        }

        foreach (int r in order)
        {
            if (hasSlack[r] || hasZip[r] || anyNegativeLoss[r]) continue;
            double slack = count[r] * tolerance + UnsolvableMarginPu;
            string? reason = null;
            if (net[r] <= -slack)
                reason = "no reference bus, and its demand exceeds its generation (its losses cannot be negative)";
            else if (!anyLoss[r] && Math.Abs(net[r]) >= slack)
                reason = "no reference bus, lossless, and its generation and demand do not balance";
            if (reason is null) continue;
            var buses = new List<int>(count[r]);
            for (int i = 0; i < n; i++) if (Find(i) == r) buses.Add(m.Buses[i].Id);
            return new UnsolvablePart(buses, net[r], reason);
        }
        return null;
    }

    private static string Label(Branch b) =>
        (string.IsNullOrEmpty(b.Name) ? $"{b.From}-{b.To}" : b.Name) + (b.IsTransformer ? " (tx)" : "");

    /// <summary>A combined label for a multi-element outage, e.g. "line A + tx B".</summary>
    private static string LabelMany(GridModel m, IReadOnlyList<int> indices) =>
        string.Join(" + ", indices.Select(i => Label(m.Branches[i])));

    private static GridModel WithoutMany(GridModel m, IReadOnlyList<int> indices)
    {
        var skip = indices as HashSet<int> ?? new HashSet<int>(indices);
        var branches = new List<Branch>(m.Branches.Count - skip.Count);
        for (int j = 0; j < m.Branches.Count; j++) if (!skip.Contains(j)) branches.Add(m.Branches[j]);
        return m.CopyWith(branches: branches);   // keeps asset dynamics and protections too (R270)
    }

    /// <summary>All unordered k-subsets of <paramref name="items"/>, appended to <paramref name="into"/>.</summary>
    private static void Combinations(int[] items, int k, List<int[]> into)
    {
        int n = items.Length;
        if (k > n) return;
        var pick = new int[k];
        void Recurse(int start, int depth)
        {
            if (depth == k) { into.Add((int[])pick.Clone()); return; }
            // Leave room for the remaining (k-depth) picks.
            for (int i = start; i <= n - (k - depth); i++)
            {
                pick[depth] = items[i];
                Recurse(i + 1, depth + 1);
            }
        }
        Recurse(0, 0);
    }
}
