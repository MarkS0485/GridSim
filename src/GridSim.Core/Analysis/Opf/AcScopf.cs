using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Opf;

/// <summary>What the units may do after an outage in <see cref="AcScopf"/>.</summary>
public enum AcScopfMode
{
    /// <summary>Preventive: every unit not at a slack bus keeps its base-case output after the outage.</summary>
    Preventive,
    /// <summary>Corrective: every unit not at a slack bus may move by at most its ramp after the outage.</summary>
    Corrective,
}

/// <summary>The settings of <see cref="AcScopf.Solve"/>.</summary>
public sealed record AcScopfOptions
{
    /// <summary>Preventive (default) or corrective.</summary>
    public AcScopfMode Mode { get; init; } = AcScopfMode.Preventive;

    /// <summary>The branch outages to consider (indices into <c>model.Branches</c>), or null for every in-service branch.
    /// An outage that islands the network is listed in <see cref="AcScopfResult.Islanding"/> and not secured.</summary>
    public IReadOnlyList<int>? Contingencies { get; init; }

    /// <summary>
    /// General outages in place of <see cref="Contingencies"/> (giving both is refused): each removes a set of branches
    /// and units (N-2 pairs, generator outages, or a mix). The ids in the result (<see cref="AcScopfResult.Secured"/> and
    /// the rest) are then positions in this list. The lost output of a removed unit is shared by governor droop
    /// (<see cref="AcScopf"/>); when a slack bus loses all its units the reference moves to the bus of the largest
    /// remaining unit. An outage that islands the network is listed in <see cref="AcScopfResult.Islanding"/> and not secured.
    /// </summary>
    public IReadOnlyList<ScopfOutage>? Outages { get; init; }

    /// <summary>Corrective mode: one ramp (MW) for every unit, used when <see cref="RampPerGenMw"/> is null.</summary>
    public double RampMw { get; init; } = double.NaN;

    /// <summary>Corrective mode: a ramp (MW) per in-service unit, aligned to <c>model.Generators</c>.</summary>
    public IReadOnlyList<double>? RampPerGenMw { get; init; }

    /// <summary>The rating after an outage: RateA (default), RateB or RateC, falling back to RateA where not given.</summary>
    public PostFaultRating PostContingencyRating { get; init; } = PostFaultRating.RateA;

    /// <summary>The most OPF solves constraint generation may take (default 20).</summary>
    public int MaxRounds { get; init; } = 20;

    /// <summary>The objective of the base case (cost or losses; reactive power optimisation is not offered here).</summary>
    public AcOpfObjective Objective { get; init; } = AcOpfObjective.Cost;

    /// <summary>The interior point settings, or null for <see cref="AcOpfExtended.DefaultIpm"/> of the objective.</summary>
    public AcOpfIpmOptions? Ipm { get; init; }
}

/// <summary>How <see cref="AcScopf.Solve"/> ended.</summary>
public enum AcScopfStatus
{
    /// <summary>The secured optimum: every listed outage that does not island the network, secured or not, passes the AC
    /// screen at the final dispatch.</summary>
    Optimal,
    /// <summary>An OPF solve did not converge (the cause is in the message); no optimum.</summary>
    NotConverged,
    /// <summary>The round cap was reached with outages still failing the screen; no optimum.</summary>
    RoundLimit,
    /// <summary>Nothing more was added, but at least one secured outage fails its own AC screen at the final dispatch
    /// (listed in <see cref="AcScopfResult.UnresolvedSecuredOutages"/>). The outage's block lets the unit voltages and
    /// reactive outputs move after the outage; the screen holds the unit voltages at their base-case values, so a block
    /// can meet its limits while the screen cannot. Not an optimum.</summary>
    SecuredOutageFailsScreen,
}

/// <summary>The AC screen of one outage at a dispatch: whether the post-outage Newton power flow converged and, if so,
/// whether every branch is within its post-outage rating and every bus within its voltage limits (by 1e-6).
/// <see cref="Reason"/> names the first violation (null when it passes).</summary>
public sealed record AcScopfScreen(int Outage, bool Converged, bool Passes, string? Reason);

/// <summary>One round of constraint generation: the outages in the problem, the solve's iterations and objective, and
/// the outages the screen added.</summary>
public sealed record AcScopfRound(int Round, IReadOnlyList<int> Secured, int Iterations, double Objective, IReadOnlyList<int> Added);

/// <summary>The result of <see cref="AcScopf.Solve"/>. <see cref="Last"/> is the last OPF solve (an optimum only when
/// <see cref="Status"/> is Optimal; otherwise the state it stopped in, never to be printed as an optimum).</summary>
public sealed record AcScopfResult(
    AcScopfStatus Status, AcScopfMode Mode, AcOpfExtendedResult? Last, IReadOnlyList<int> Secured, IReadOnlyList<int> ScreenedOk,
    IReadOnlyList<int> Islanding, IReadOnlyList<AcScopfRound> Rounds, string Message)
{
    /// <summary>True when the secured optimum was found.</summary>
    public bool Optimal => Status == AcScopfStatus.Optimal;

    /// <summary>The secured outages that fail their own AC screen at the final dispatch, in index order. Not empty only
    /// when <see cref="Status"/> is <see cref="AcScopfStatus.SecuredOutageFailsScreen"/>.</summary>
    public IReadOnlyList<int> UnresolvedSecuredOutages { get; init; } = Array.Empty<int>();

    /// <summary>The outage list when the run was given <see cref="AcScopfOptions.Outages"/> (the ids in this result are
    /// positions in it); null when it was given branch <see cref="AcScopfOptions.Contingencies"/> (the ids are branch
    /// indices).</summary>
    public IReadOnlyList<ScopfOutage>? OutageList { get; init; }
}

/// <summary>
/// AC security-constrained optimal power flow, preventive or corrective (Capitanescu et al., "State-of-the-art,
/// challenges, and future trends in security constrained optimal power flow", EPSR 81(8), 2011, eqs. 1-5; Monticelli,
/// Pereira and Granville, IEEE Trans. PWRS 2(1), 1987).
/// <para>Block 0 is the base case; each secured branch outage c is its own block (Va, Vm, Pg, Qg on the network with c
/// out). Preventive: Pg_c = Pg_0 for each unit not at a slack bus. Corrective: |Pg_c - Pg_0| &lt;= R_g for each such
/// unit. Slack-bus units are free in every block (within their limits) to take the change in losses; Qg and Vm are free
/// within their limits in every block (post-outage voltage control). The objective is block 0's. Post-outage branch
/// limits use the <see cref="DcScopf"/> rating rule. All blocks are one stacked problem, solved by the MIPS copy of
/// <see cref="AcOpfExtended"/>.</para>
/// <para>General outages (<see cref="AcScopfOptions.Outages"/>): a block removes every branch and unit of its outage. A
/// removed unit's Pg and Qg are held at 0 in that block and it has no coupling row there. The power it was producing is
/// shared by governor droop, as PSS/E ACCC's dispatch by governor droop and PowerFactory's "according to primary control"
/// do (<see cref="ContingencyAnalysis.ShareLostPower"/>, weights from
/// <see cref="ContingencyAnalysis.ParticipationWeights"/>): each remaining synchronous unit g with a dispatch range takes
/// the fixed fraction alpha_g = w_g / sum w of the lost output dP (the sum of the removed units' base-case Pg). After the
/// outage, preventive: Pg_c = Pg_0 + alpha_g dP; corrective: |Pg_c - (Pg_0 + alpha_g dP)| &lt;= R_g. The units at the
/// post-outage reference bus take their own share and the change in losses (they are free in the block). Every unit's
/// Pmin .. Pmax holds in every block, so the problem keeps the reserve each unit needs for its share. When the outage
/// removes every unit of a slack bus, the reference moves to the bus of the largest remaining unit by case |Pg|
/// (<see cref="ContingencyAnalysis.ReelectReference"/>, the rule of the contingency screen), in the block and in the
/// screen. The screen of such an outage shares the lost output the same way and fails when a unit's share takes it outside
/// Pmin .. Pmax, or when the reference bus's P or Q leaves the range of its units, by more than 1e-6 MW or MVAr. The screen
/// of a branch-only outage is the screen below, unchanged.</para>
/// <para>Constraint generation: start with block 0; after each solve, screen every listed outage not yet in the problem
/// with an AC Newton (base-case dispatch, PV set-points from block 0's voltages, reactive limits on, the slack takes the
/// losses); add every outage that does not converge, breaks a post-outage rating by more than 1e-6 MVA or a voltage limit
/// by more than 1e-6 pu, in index order; re-solve from scratch; stop when nothing is added. Then screen every secured
/// outage the same way at the final dispatch (in corrective mode with the unit outputs of its block, the re-dispatch the
/// problem allows after it). The block of an outage lets unit voltages move after it, the screen does
/// not, so a secured outage can still fail its screen. If one does, the status is
/// <see cref="AcScopfStatus.SecuredOutageFailsScreen"/>, not Optimal, and the outages are listed in
/// <see cref="AcScopfResult.UnresolvedSecuredOutages"/>.</para>
/// </summary>
public static class AcScopf
{
    /// <summary>A post-outage branch above its rating by more than this (MVA), or a bus outside its limits by more than
    /// this (pu), fails the screen.</summary>
    public const double ViolationTolerance = 1e-6;

    /// <summary>Solve the AC-SCOPF. Throws <see cref="ArgumentException"/> or <see cref="InvalidDataException"/> for input
    /// it cannot build.</summary>
    public static AcScopfResult Solve(GridModel model, AcScopfOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRounds < 1) throw new ArgumentOutOfRangeException(nameof(options), options.MaxRounds, "AC-SCOPF: at least one round is needed.");
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "AC-SCOPF: unknown mode.");
        if (!Enum.IsDefined(options.PostContingencyRating)) throw new ArgumentOutOfRangeException(nameof(options), options.PostContingencyRating, "AC-SCOPF: unknown post-outage rating.");
        if (options.Objective == AcOpfObjective.ReactivePower)
            throw new ArgumentException("AC-SCOPF: the objective must be cost or losses.", nameof(options));

        IReadOnlyList<double>? ramps = null;
        if (options.Mode == AcScopfMode.Corrective)
        {
            if (options.RampPerGenMw is { } per)
            {
                if (per.Count != model.Generators.Count)
                    throw new ArgumentException($"AC-SCOPF: {per.Count} ramp(s) given for {model.Generators.Count} in-service unit(s).", nameof(options));
                ramps = per;
            }
            else
            {
                if (double.IsNaN(options.RampMw)) throw new ArgumentException("AC-SCOPF: corrective mode needs a ramp (one for every unit, or one per unit).", nameof(options));
                ramps = Enumerable.Repeat(options.RampMw, model.Generators.Count).ToArray();
            }
            foreach (double r in ramps)
                if (double.IsNaN(r) || r < 0) throw new ArgumentException($"AC-SCOPF: a ramp of {r.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} MW; a ramp must be 0 or above.", nameof(options));
        }

        if (options.Outages is not null && options.Contingencies is not null)
            throw new ArgumentException("AC-SCOPF: give Outages or Contingencies, not both.", nameof(options));

        // The outages: listed or every in-service branch, each checked; islanding ones are listed, not secured. Each
        // outage has an id: its branch index (Contingencies) or its position in the list (Outages).
        var candidates = new List<int>();
        var islanding = new List<int>();
        var outageOf = new Dictionary<int, ScopfOutage>();
        IReadOnlyList<ScopfOutage>? general = options.Outages;
        if (general is null)
        {
            IReadOnlyList<int> listed = options.Contingencies ?? ContingencyAnalysis.InServiceBranchIndices(model);
            var seen = new HashSet<int>();
            foreach (int k in listed)
            {
                if (k < 0 || k >= model.Branches.Count) throw new ArgumentException($"AC-SCOPF: outage {k} is not a branch of the case (0..{model.Branches.Count - 1}).", nameof(options));
                if (!model.Branches[k].InService) throw new ArgumentException($"AC-SCOPF: outage {k} is a branch that is already out of service.", nameof(options));
                if (!seen.Add(k)) throw new ArgumentException($"AC-SCOPF: outage {k} is listed twice.", nameof(options));
                if (Islands(model, k)) islanding.Add(k); else candidates.Add(k);
                outageOf[k] = ScopfOutage.OfBranch(k);
            }
        }
        else
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < general.Count; i++)
            {
                ScopfOutage o = general[i] ?? throw new ArgumentException($"AC-SCOPF: outage {i} is null.", nameof(options));
                CheckOutage(model, o, i);
                string key = string.Join(",", o.Branches.OrderBy(k => k)) + "|" + string.Join(",", o.Generators.OrderBy(k => k));
                if (!keys.Add(key)) throw new ArgumentException($"AC-SCOPF: outage {i} ({o.Label}) is listed twice.", nameof(options));
                if (Islands(model, o.Branches)) islanding.Add(i);
                else
                {
                    candidates.Add(i);
                    if (o.Generators.Count > 0) Response(model, o);   // refuses an outage that leaves no unit to take a reference
                }
                outageOf[i] = o;
            }
        }
        candidates.Sort();
        islanding.Sort();

        var opfOptions = new AcOpfExtendedOptions { Objective = options.Objective, Ipm = options.Ipm };
        AcOpfCoupling coupling = options.Mode == AcScopfMode.Preventive ? AcOpfCoupling.Preventive : AcOpfCoupling.Corrective;
        var secured = new List<int>();
        var rounds = new List<AcScopfRound>();
        AcOpfExtendedResult? last = null;
        for (int round = 1; round <= options.MaxRounds; round++)
        {
            last = AcOpfExtended.SolveBlocks(model, opfOptions,
                new AcOpfBlocks(secured.Select(k => outageOf[k]).ToArray(), secured.ToArray(), coupling, ramps, options.PostContingencyRating)
                {
                    Labels = general is null ? null : secured.Select(k => outageOf[k].Label).ToArray(),
                });
            if (!last.Converged)
            {
                rounds.Add(new AcScopfRound(round, secured.ToArray(), last.Base.Iterations, last.Base.ObjectivePerHour, Array.Empty<int>()));
                return new AcScopfResult(AcScopfStatus.NotConverged, options.Mode, last, secured.ToArray(), Array.Empty<int>(), islanding, rounds,
                    $"the OPF of round {round} ({secured.Count} outage(s) secured) did not converge: {last.Base.Message}")
                { OutageList = general };
            }
            var added = new List<int>();
            foreach (int k in candidates)
            {
                if (secured.Contains(k)) continue;
                AcScopfScreen first = general is null
                    ? Screen(model, last.Base, k, options.PostContingencyRating)
                    : ScreenCore(model, last.Base, last.Base.Generators, k, outageOf[k], options.PostContingencyRating);
                if (!first.Passes) added.Add(k);
            }
            rounds.Add(new AcScopfRound(round, secured.ToArray(), last.Base.Iterations, last.Base.ObjectivePerHour, added.ToArray()));
            if (added.Count == 0)
            {
                int[] ok = candidates.Where(k => !secured.Contains(k)).ToArray();
                // Re-screen the secured outages at the final dispatch: their blocks let unit voltages move, the screen
                // holds them, so a block that meets its limits does not prove the screen passes.
                var unresolved = new List<int>();
                string? firstReason = null;
                foreach (int k in secured)
                {
                    // Corrective: the unit outputs after the outage are the block's (the corrective re-dispatch the problem
                    // allows); preventive: the base case's, as for every other screen.
                    IReadOnlyList<AcOpfGenResult>? after = null;
                    if (options.Mode == AcScopfMode.Corrective)
                        foreach (AcOpfBlockResult b in last.Outages)
                            if (b.Outage == k) { after = b.Generators; break; }
                    // A corrective block's outputs are the post-outage dispatch already (the lost output is in them).
                    AcScopfScreen sc = general is null
                        ? ScreenAt(model, last.Base, after ?? last.Base.Generators, k, options.PostContingencyRating)
                        : ScreenCore(model, last.Base, after ?? last.Base.Generators, k, outageOf[k], options.PostContingencyRating,
                            postOutageDispatch: after is not null);
                    if (sc.Passes) continue;
                    unresolved.Add(k);
                    firstReason ??= $"outage {k}: {sc.Reason}";
                }
                if (unresolved.Count > 0)
                    return new AcScopfResult(AcScopfStatus.SecuredOutageFailsScreen, options.Mode, last, secured.ToArray(), ok, islanding, rounds,
                        $"after {round} round(s) {unresolved.Count} secured outage(s) [{string.Join(",", unresolved)}] still fail the AC screen at the final dispatch (first: {firstReason}); the outage blocks let unit voltages move after the outage, the screen holds them")
                    { UnresolvedSecuredOutages = unresolved.ToArray(), OutageList = general };
                return new AcScopfResult(AcScopfStatus.Optimal, options.Mode, last, secured.ToArray(), ok, islanding, rounds,
                    $"secured optimum after {round} round(s): {secured.Count} outage(s) in the problem, {ok.Length} pass the AC screen")
                { OutageList = general };
            }
            secured.AddRange(added);
            secured.Sort();
        }
        return new AcScopfResult(AcScopfStatus.RoundLimit, options.Mode, last, secured.ToArray(), Array.Empty<int>(), islanding, rounds,
            $"the round cap ({options.MaxRounds}) was reached with outages still failing the AC screen")
        { OutageList = general };
    }

    /// <summary>
    /// How the remaining units answer a general outage: the units at a reference bus after it (<see cref="Balancing"/>,
    /// free in the block), each remaining unit's governor-droop fraction of the lost output (<see cref="Alpha"/>, 0 for a
    /// removed unit, a unit with no dispatch range or one that takes no part), the bus types after the reference moved
    /// (<see cref="Types"/>, aligned to <c>model.Buses</c>; null when it did not) and the note saying so. Aligned to
    /// <c>model.Generators</c>.
    /// </summary>
    internal sealed record OutageResponse(bool[] Balancing, double[] Alpha, BusType[]? Types, string? Note);

    /// <summary>The <see cref="OutageResponse"/> of <paramref name="outage"/>. Depends only on the case data (the units'
    /// weights and case outputs, the topology), never on a dispatch, so the block and the screen use the same one. Throws
    /// <see cref="ArgumentException"/> when no unit is left to take over a reference.</summary>
    internal static OutageResponse Response(GridModel model, ScopfOutage outage)
    {
        int ng = model.Generators.Count;
        var removed = new bool[ng];
        foreach (int g in outage.Generators) removed[g] = true;
        var outB = new HashSet<int>(outage.Branches);
        var brs = new Branch[model.Branches.Count];
        for (int k = 0; k < brs.Length; k++) brs[k] = outB.Contains(k) ? model.Branches[k] with { InService = false } : model.Branches[k];
        var survivors = new List<Generator>(ng);
        var map = new List<int>(ng);
        for (int g = 0; g < ng; g++)
            if (!removed[g]) { survivors.Add(model.Generators[g]); map.Add(g); }

        // A slack bus that loses every unit hands the reference on (the rule of the contingency screen, on the case outputs).
        GridModel current = model;
        string? note = null;
        foreach (Bus b in model.Buses)
        {
            if (b.Type != BusType.Slack) continue;
            bool lostHere = false;
            foreach (int g in outage.Generators) if (model.Generators[g].Bus == b.Id) lostHere = true;
            if (!lostHere) continue;
            (IReadOnlyList<Bus>? buses, string? n, bool noUnitLeft) = ContingencyAnalysis.ReelectReference(current, brs, survivors, null, b.Id);
            if (noUnitLeft) throw new ArgumentException($"AC-SCOPF: outage {outage.Label} leaves no in-service unit to take over the reference of bus {b.Id}.", nameof(outage));
            if (buses is not null)
            {
                current = current.CopyWith(buses: buses);
                note = note is null ? n : note + "; " + n;
            }
        }
        BusType[]? types = null;
        if (!ReferenceEquals(current, model))
        {
            types = new BusType[model.Buses.Count];
            for (int i = 0; i < types.Length; i++) types[i] = current.Buses[i].Type;
        }

        GridModel reduced = current.CopyWith(generators: survivors, branches: brs);
        double[] w = ContingencyAnalysis.ParticipationWeights(reduced, OutageDispatch.GovernorDroop);
        double sum = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (!(survivors[i].Pmax > survivors[i].Pmin)) w[i] = 0;   // no dispatch range: it holds its output
            sum += w[i];
        }
        var slackIds = new HashSet<int>();
        foreach (Bus b in current.Buses) if (b.Type == BusType.Slack) slackIds.Add(b.Id);
        var alpha = new double[ng];
        var balancing = new bool[ng];
        for (int i = 0; i < survivors.Count; i++)
        {
            alpha[map[i]] = sum > 0 ? w[i] / sum : 0;
            balancing[map[i]] = slackIds.Contains(survivors[i].Bus);
        }
        return new OutageResponse(balancing, alpha, types, note);
    }

    // Check one general outage against the case.
    private static void CheckOutage(GridModel model, ScopfOutage outage, int i)
    {
        if (outage.Branches is null || outage.Generators is null) throw new ArgumentException($"AC-SCOPF: outage {i} has a null list.", nameof(outage));
        if (outage.Branches.Count == 0 && outage.Generators.Count == 0) throw new ArgumentException($"AC-SCOPF: outage {i} removes nothing.", nameof(outage));
        var bs = new HashSet<int>();
        foreach (int k in outage.Branches)
        {
            if (k < 0 || k >= model.Branches.Count) throw new ArgumentException($"AC-SCOPF: outage {i} ({outage.Label}): branch {k} is not a branch of the case (0..{model.Branches.Count - 1}).", nameof(outage));
            if (!model.Branches[k].InService) throw new ArgumentException($"AC-SCOPF: outage {i} ({outage.Label}): branch {k} is already out of service.", nameof(outage));
            if (!bs.Add(k)) throw new ArgumentException($"AC-SCOPF: outage {i} ({outage.Label}) lists branch {k} twice.", nameof(outage));
        }
        var gs = new HashSet<int>();
        foreach (int g in outage.Generators)
        {
            if (g < 0 || g >= model.Generators.Count) throw new ArgumentException($"AC-SCOPF: outage {i} ({outage.Label}): generator {g} is not an in-service unit of the case (0..{model.Generators.Count - 1}).", nameof(outage));
            if (!gs.Add(g)) throw new ArgumentException($"AC-SCOPF: outage {i} ({outage.Label}) lists generator {g} twice.", nameof(outage));
        }
    }

    /// <summary>
    /// The AC screen of the outage of branch <paramref name="outage"/> at the base-case state <paramref name="baseCase"/>:
    /// each unit at its base-case output with its bus voltage from the base case as set-point, the bus voltages as the
    /// warm start, solved by the contingency family's Newton (100 iterations, reactive limits on, OLTC loop) with the
    /// slack taking the losses.
    /// </summary>
    public static AcScopfScreen Screen(GridModel model, AcOpfIpmResult baseCase, int outage, PostFaultRating rating)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(baseCase);
        return ScreenAt(model, baseCase, baseCase.Generators, outage, rating);
    }

    // The screen with the unit outputs taken from unitOutputs (aligned to model.Generators) and everything else from the
    // base case: the general screen of the one-branch outage (no unit removed, so nothing is shared).
    private static AcScopfScreen ScreenAt(GridModel model, AcOpfIpmResult baseCase, IReadOnlyList<AcOpfGenResult> unitOutputs, int outage, PostFaultRating rating) =>
        ScreenCore(model, baseCase, unitOutputs, outage, ScopfOutage.OfBranch(outage), rating);

    /// <summary>
    /// The AC screen of a general outage (reported as <paramref name="id"/>): its branches out, its units removed, every
    /// other unit at <paramref name="unitOutputs"/> with its base-case bus voltage as set-point; then the checks of
    /// <see cref="Screen"/>. When a unit is removed: the reference moves as in the block (<see cref="Response"/>); unless
    /// <paramref name="postOutageDispatch"/> (the outputs already are the post-outage ones), the lost output is shared by
    /// governor droop first (<see cref="ContingencyAnalysis.ShareLostPower"/>), and a unit whose share would take it outside
    /// its Pmin .. Pmax fails the screen; after the solve the reference bus's P and Q are checked against its units' ranges.
    /// </summary>
    internal static AcScopfScreen ScreenCore(GridModel model, AcOpfIpmResult baseCase, IReadOnlyList<AcOpfGenResult> unitOutputs, int id,
        ScopfOutage outage, PostFaultRating rating, bool postOutageDispatch = false)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        OutageResponse? response = outage.Generators.Count > 0 ? Response(model, outage) : null;
        int nb = model.BusCount;
        var vm = new Dictionary<int, double>(nb);
        var buses = new Bus[nb];
        for (int i = 0; i < nb; i++)
        {
            AcOpfBusResult b = baseCase.Buses[i];
            vm[b.BusId] = b.VmPu;
            buses[i] = model.Buses[i] with { Vm = b.VmPu, VaDeg = b.VaDeg };
            if (response?.Types is { } types && types[i] != buses[i].Type) buses[i] = buses[i] with { Type = types[i] };
        }
        var genOut = new HashSet<int>(outage.Generators);
        var gens = new List<Generator>(model.Generators.Count);
        for (int g = 0; g < model.Generators.Count; g++)
            if (!genOut.Contains(g)) gens.Add(model.Generators[g] with { Pg = unitOutputs[g].PgMw, Vg = vm[model.Generators[g].Bus] });
        var branchOut = new HashSet<int>(outage.Branches);
        var brs = new Branch[model.Branches.Count];
        for (int k = 0; k < brs.Length; k++) brs[k] = branchOut.Contains(k) ? model.Branches[k] with { InService = false } : model.Branches[k];
        GridModel m = model.CopyWith(buses: buses, generators: gens, branches: brs);
        if (response is not null && !postOutageDispatch)
        {
            // Governor droop: each unit off the reference bus takes alpha_g of the lost output, within its range (the
            // reserve the block holds for it). The units at the reference bus take their own shares and the change in
            // losses, checked as a bus after the solve, as the block leaves them free.
            double lost = 0;
            for (int g = 0; g < model.Generators.Count; g++) if (genOut.Contains(g)) lost += unitOutputs[g].PgMw;
            var target = new double[model.Generators.Count];
            for (int g = 0; g < model.Generators.Count; g++)
            {
                if (genOut.Contains(g) || response.Balancing[g]) continue;
                Generator gen = model.Generators[g];
                double share = response.Alpha[g] * lost;
                target[g] = unitOutputs[g].PgMw + share;
                if (target[g] > gen.Pmax + ViolationTolerance || target[g] < gen.Pmin - ViolationTolerance)
                    return new AcScopfScreen(id, true, false,
                        $"generator {g} at bus {gen.Bus}: its governor share {share.ToString("R", inv)} MW of the lost {lost.ToString("R", inv)} MW takes it to {target[g].ToString("R", inv)} MW, outside {gen.Pmin.ToString("R", inv)} .. {gen.Pmax.ToString("R", inv)} MW");
            }
            m = ContingencyAnalysis.ShareLostPower(m, lost, OutageDispatch.GovernorDroop).Model;
            // The sharing moves a unit further than its share only when the reference bus's units reach their limits and
            // pass the rest on: the reference bus then lacks the reserve for its own share, which the block does not allow.
            int kept = 0;
            for (int g = 0; g < model.Generators.Count; g++)
            {
                if (genOut.Contains(g)) continue;
                Generator moved = m.Generators[kept++];
                if (response.Balancing[g] || Math.Abs(moved.Pg - target[g]) <= ViolationTolerance) continue;
                return new AcScopfScreen(id, true, false,
                    $"generator {g} at bus {moved.Bus}: the governor sharing of the lost {lost.ToString("R", inv)} MW puts it at {moved.Pg.ToString("R", inv)} MW, not at its share {target[g].ToString("R", inv)} MW, because the units at the reference bus reach their limits");
            }
        }

        PowerFlowResult pf = ContingencyAnalysis.CreateStudySolver(m).Solve(m, ContingencyAnalysis.StudyOptions);
        if (!pf.Converged) return new AcScopfScreen(id, false, false, "the post-outage power flow does not converge");

        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < pf.BusIds.Count; i++) index[pf.BusIds[i]] = i;
        var v = new Complex[pf.BusIds.Count];
        for (int i = 0; i < v.Length; i++) v[i] = DetMath.FromPolar(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
        for (int i = 0; i < nb; i++)
        {
            Bus b = model.Buses[i];
            double lo = b.VminPu ?? VoltageLimits.OperationalLowPu, hi = b.VmaxPu ?? VoltageLimits.OperationalHighPu;
            double x = pf.Vm[index[b.Id]];
            if (x < lo - ViolationTolerance || x > hi + ViolationTolerance)
                return new AcScopfScreen(id, true, false, $"bus {b.Id} at {x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} pu");
        }
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (!br.InService) continue;
            double rate = AcOpfExtended.PostRating(br, rating);
            if (!(rate > 0) || rate >= 1e10) continue;
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            Complex vf = v[index[br.From]], vt = v[index[br.To]];
            Complex sf = vf * Complex.Conjugate(s.Yff * vf + s.Yft * vt) * model.BaseMva;
            Complex st = vt * Complex.Conjugate(s.Ytf * vf + s.Ytt * vt) * model.BaseMva;
            double worst = Math.Max(sf.Magnitude, st.Magnitude);
            if (worst > rate + ViolationTolerance)
                return new AcScopfScreen(id, true, false,
                    $"branch {k} ({br.From}-{br.To}) at {worst.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} MVA against {rate.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        // ANA-27: after a branch-only outage too: the reference bus absorbs the change in losses, so a reference unit the OPF
        // dispatched at its limit can end beyond it, and the outage passed the screen unchecked.
        if (genOut.Count > 0 || branchOut.Count > 0)
        {
            // The reference bus takes its own share and the change in losses: its bus's generation (net injection plus
            // load) must stay within the P and Q ranges of the units there, as the block holds them.
            foreach (Bus b in m.Buses)
            {
                if (b.Type != BusType.Slack) continue;
                double pmin = 0, pmax = 0, qmin = 0, qmax = 0;
                foreach (Generator g in gens)
                    if (g.Bus == b.Id) { pmin += g.Pmin; pmax += g.Pmax; qmin += g.Qmin; qmax += g.Qmax; }
                double pg = pf.PInjMw[index[b.Id]] + b.Pd;
                if (pg > pmax + ViolationTolerance || pg < pmin - ViolationTolerance)
                    return new AcScopfScreen(id, true, false,
                        $"slack bus {b.Id} at {pg.ToString("R", inv)} MW against its units' range {pmin.ToString("R", inv)} .. {pmax.ToString("R", inv)} MW");
                double qg = pf.QInjMvar[index[b.Id]] + b.Qd;
                if (qg > qmax + ViolationTolerance || qg < qmin - ViolationTolerance)
                    return new AcScopfScreen(id, true, false,
                        $"slack bus {b.Id} at {qg.ToString("R", inv)} MVAr against its units' range {qmin.ToString("R", inv)} .. {qmax.ToString("R", inv)} MVAr");
            }
        }
        return new AcScopfScreen(id, true, true, null);
    }

    /// <summary>True when taking out every branch in <paramref name="out"/> splits the in-service network.</summary>
    internal static bool Islands(GridModel model, IReadOnlyList<int> @out)
    {
        int nb = model.BusCount;
        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) index[model.Buses[i].Id] = i;
        var removed = new HashSet<int>(@out);
        var adj = new List<int>[nb];
        for (int i = 0; i < nb; i++) adj[i] = new List<int>();
        for (int j = 0; j < model.Branches.Count; j++)
        {
            Branch br = model.Branches[j];
            if (!br.InService || removed.Contains(j)) continue;
            int f = index[br.From], t = index[br.To];
            adj[f].Add(t); adj[t].Add(f);
        }
        var seen = new bool[nb];
        var stack = new Stack<int>();
        seen[0] = true; stack.Push(0);
        int count = 1;
        while (stack.Count > 0)
            foreach (int q in adj[stack.Pop()])
                if (!seen[q]) { seen[q] = true; count++; stack.Push(q); }
        return count != nb;
    }

    /// <summary>True when the outage of branch <paramref name="k"/> splits the in-service network.</summary>
    internal static bool Islands(GridModel model, int k)
    {
        int nb = model.BusCount;
        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) index[model.Buses[i].Id] = i;
        var adj = new List<int>[nb];
        for (int i = 0; i < nb; i++) adj[i] = new List<int>();
        for (int j = 0; j < model.Branches.Count; j++)
        {
            Branch br = model.Branches[j];
            if (!br.InService || j == k) continue;
            int f = index[br.From], t = index[br.To];
            adj[f].Add(t); adj[t].Add(f);
        }
        var seen = new bool[nb];
        var stack = new Stack<int>();
        seen[0] = true; stack.Push(0);
        int count = 1;
        while (stack.Count > 0)
            foreach (int q in adj[stack.Pop()])
                if (!seen[q]) { seen[q] = true; count++; stack.Push(q); }
        return count != nb;
    }
}
