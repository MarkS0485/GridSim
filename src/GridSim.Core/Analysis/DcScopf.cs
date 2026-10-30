using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>The settings of <see cref="DcScopf.Solve"/>.</summary>
public sealed record ScopfOptions
{
    /// <summary>The branch outages to secure (indices into <c>model.Branches</c>). Null: every in-service branch whose
    /// outage leaves the network connected; the outages that would island it are listed in
    /// <see cref="ScopfResult.IslandingOutages"/> and not secured. A listed outage that islands the network is refused.</summary>
    public IReadOnlyList<int>? Contingencies { get; init; }

    /// <summary>The rating a monitored branch must respect after an outage: RateA (default), RateB or RateC, each falling
    /// back to RateA on a branch where it is 0 (the rule of the AC contingency analysis). The pre-outage state always uses
    /// RateA.</summary>
    public PostFaultRating PostContingencyRating { get; init; } = PostFaultRating.RateA;

    /// <summary>Marginal cost per generator (per MWh, aligned to <c>model.Generators</c>). Null: the fuel-type costs of
    /// <see cref="DcOptimalPowerFlow.FuelCostPerMwh"/>, as the <c>dcopf</c> verb uses.</summary>
    public double[]? CostPerGen { get; init; }

    /// <summary>True (default): post-outage limits enter the LP only once the dispatch breaks them (constraint generation;
    /// the LP is re-solved with the broken limits added until none is broken). False: every post-outage limit is in the LP
    /// from the start. Both reach the same optimal cost; the first builds a far smaller LP.</summary>
    public bool ConstraintGeneration { get; init; } = true;

    /// <summary>The most LP solves constraint generation may take (default 50); reaching it is reported as
    /// <see cref="ScopfStatus.RoundLimit"/>, never as an optimum.</summary>
    public int MaxRounds { get; init; } = 50;

    /// <summary>Simplex iteration cap per LP solve.</summary>
    public int LpMaxIterations { get; init; } = SparseBoundedSimplex.DefaultMaxIterations;
}

/// <summary>How <see cref="DcScopf.Solve"/> ended.</summary>
public enum ScopfStatus
{
    /// <summary>The least-cost dispatch meeting every pre- and post-outage limit.</summary>
    Optimal,
    /// <summary>No dispatch meets the demand with every limit respected (the simplex's phase one proved it).</summary>
    Infeasible,
    /// <summary>The LP is unbounded (cannot happen with finite generator ranges; reported, never hidden).</summary>
    Unbounded,
    /// <summary>The simplex reached its iteration cap.</summary>
    IterationCap,
    /// <summary>A simplex basis could not be factorised.</summary>
    NumericalFailure,
    /// <summary>Constraint generation reached <see cref="ScopfOptions.MaxRounds"/> with limits still broken.</summary>
    RoundLimit,
}

/// <summary>A generator's secured dispatch.</summary>
public sealed record ScopfDispatch(int Index, int Bus, string Name, double Pmw, double CostPerMwh);

/// <summary>A branch at its limit: pre-outage (<see cref="Contingency"/> -1, against RateA) or after the outage of branch
/// <see cref="Contingency"/> (against the post-outage rating).</summary>
public sealed record ScopfBinding(int Branch, string Name, int Contingency, double FlowMw, double RateMw);

/// <summary>A bus's price: the change in optimal cost per extra MW of demand at the bus (the dual of its balance row).</summary>
public sealed record ScopfBusPrice(int BusId, double Lmp);

/// <summary>The result of <see cref="DcScopf.Solve"/>. The dispatch, flows and prices are filled only when
/// <see cref="Status"/> is <see cref="ScopfStatus.Optimal"/>; otherwise they are empty and <see cref="Message"/> says why.</summary>
public sealed record ScopfResult(
    ScopfStatus Status, double TotalCost, double DemandMw, IReadOnlyList<ScopfDispatch> Dispatch, double[] BaseFlowsMw,
    IReadOnlyList<ScopfBusPrice> Prices, IReadOnlyList<ScopfBinding> Bindings, IReadOnlyList<int> Contingencies,
    IReadOnlyList<int> IslandingOutages, int Rounds, int PostContingencyRows, int Rows, int Columns, int LpIterations,
    string Message)
{
    /// <summary>True when the secured optimum was found.</summary>
    public bool Optimal => Status == ScopfStatus.Optimal;
}

/// <summary>
/// Preventive security-constrained DC optimal power flow: the least-cost generator dispatch that meets demand with every
/// rated branch inside its rating before any outage AND after each listed single-branch outage, with no action after the
/// outage (preventive). Solved as a sparse linear program by <see cref="SparseBoundedSimplex"/>.
///
/// <para><b>Formulation.</b> DC model of <see cref="DistributionFactors"/> (reactance only, 1 pu voltages; taps and phase
/// shifts ignored). Columns: the dispatch P_g in [Pmin, Pmax] at cost c_g; an angle psi_i per bus in MW-scaled form
/// (psi = base MVA x theta; the reference bus fixed at 0); a flow f_l per branch, within +/- RateA when rated (free when
/// not). Rows: each bus's balance, sum of its units' P - sum of flows leaving + sum of flows arriving = its demand; each
/// branch's flow, f_l - b_l (psi_from - psi_to) = 0 with b_l = 1 / X_l. After the outage of branch k, branch l carries
/// f_l + LODF(l, k) f_k (<see cref="DistributionFactors.LodfColumn"/>); each secured pair gets a column q in +/- the
/// post-outage rating and the row q - f_l - LODF(l, k) f_k = 0. Every row has a handful of non-zeros, so the LP grows with
/// the network and the secured pairs, not with buses x branches.</para>
///
/// <para><b>Constraint generation</b> (the default). The LP starts with no post-outage row. After each solve every pair
/// (outage k, monitored branch l) is checked; the pairs whose post-outage flow exceeds the rating by more than
/// <see cref="ViolationToleranceMw"/> are added (in outage then branch order) and the LP is solved again from scratch.
/// It stops when nothing is broken: the answer then meets every post-outage limit to within that tolerance and is optimal
/// for the LP holding all of them, because the rows left out do not bind. Rows are only added, so it ends within the
/// pair count; <see cref="ScopfOptions.MaxRounds"/> caps it and is reported when reached.</para>
///
/// <para><b>Prices.</b> A bus's price is its balance row's dual: the energy price plus the congestion of every binding
/// pre- and post-outage limit. One connected network only (several DC islands are refused, naming the count).
/// Deterministic: every loop runs in a fixed order, and the result is bit-identical from run to run.</para>
/// </summary>
public static class DcScopf
{
    /// <summary>A post-outage flow above its rating by more than this (MW) is a broken limit for constraint generation.</summary>
    public const double ViolationToleranceMw = 1e-6;

    /// <summary>A branch whose flow is within this of its rating (MW) is reported binding (as the DC-OPF reports).</summary>
    public const double BindingToleranceMw = DcOptimalPowerFlow.BindingToleranceMw;

    /// <summary>Solve the preventive DC SCOPF. Throws <see cref="ArgumentException"/> (or
    /// <see cref="ArgumentOutOfRangeException"/>) naming the problem for input it cannot build exactly.</summary>
    public static ScopfResult Solve(GridModel model, ScopfOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRounds < 1) throw new ArgumentOutOfRangeException(nameof(options), options.MaxRounds, "SCOPF: at least one round is needed.");
        if (options.LpMaxIterations < 1) throw new ArgumentOutOfRangeException(nameof(options), options.LpMaxIterations, "SCOPF: the LP iteration cap must be at least 1.");
        if (!Enum.IsDefined(options.PostContingencyRating))
            throw new ArgumentOutOfRangeException(nameof(options), options.PostContingencyRating, "SCOPF: unknown post-outage rating.");

        int nBus = model.Buses.Count, nBr = model.Branches.Count, G = model.Generators.Count;
        if (G == 0) throw new ArgumentException("SCOPF: the case has no in-service generator.", nameof(model));
        var busIndex = new Dictionary<int, int>(nBus);
        for (int i = 0; i < nBus; i++)
            if (!busIndex.TryAdd(model.Buses[i].Id, i))
                throw new ArgumentException($"SCOPF: bus id {model.Buses[i].Id} appears twice.", nameof(model));

        double[] cost = options.CostPerGen ?? model.Generators.Select(g => DcOptimalPowerFlow.FuelCostPerMwh(g.Type)).ToArray();
        if (cost.Length != G)
            throw new ArgumentException($"SCOPF: {cost.Length} costs given for {G} generators; the cost vector must align to model.Generators.", nameof(options));
        for (int g = 0; g < G; g++)
            if (!double.IsFinite(cost[g])) throw new ArgumentException($"SCOPF: generator {g} cost {cost[g]} is not a finite number.", nameof(options));
        DcOptimalPowerFlow.CheckGeneratorLimits(model);
        var genBus = new int[G];
        for (int g = 0; g < G; g++)
            if (!busIndex.TryGetValue(model.Generators[g].Bus, out genBus[g]))
                throw new ArgumentException($"SCOPF: generator {g} sits at bus {model.Generators[g].Bus}, which is not in the case.", nameof(model));

        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(model);
        if (islands.Count != 1)
            throw new ArgumentException($"SCOPF: the network has {islands.Count} DC islands; one connected network is needed.", nameof(model));
        DistributionFactors df = DistributionFactors.Build(model);
        int refIndex = busIndex[df.SlackBusId];

        // The branches the DC model holds (in service, both buses present; zero reactance is refused by the factors).
        var inDc = new bool[nBr];
        for (int l = 0; l < nBr; l++)
        {
            Branch br = model.Branches[l];
            inDc[l] = br.InService && busIndex.ContainsKey(br.From) && busIndex.ContainsKey(br.To);
        }

        // Outages to secure, each with its LODF column.
        var islanding = new List<int>();
        var outages = new List<int>();
        if (options.Contingencies is null)
        {
            for (int k = 0; k < nBr; k++)
            {
                if (!inDc[k]) continue;
                if (df.OutageIslands(k)) islanding.Add(k); else outages.Add(k);
            }
        }
        else
        {
            var seen = new HashSet<int>();
            foreach (int k in options.Contingencies)
            {
                if ((uint)k >= (uint)nBr)
                    throw new ArgumentException($"SCOPF: contingency branch {k} is not in the case ({nBr} branches).", nameof(options));
                if (!inDc[k])
                    throw new ArgumentException($"SCOPF: contingency branch {k} ({BranchName(model, k)}) is out of service or not in the DC model.", nameof(options));
                if (!seen.Add(k))
                    throw new ArgumentException($"SCOPF: contingency branch {k} is listed twice.", nameof(options));
                if (df.OutageIslands(k))
                    throw new ArgumentException($"SCOPF: the outage of branch {k} ({BranchName(model, k)}) islands the network; preventive redispatch cannot secure it.", nameof(options));
                outages.Add(k);
            }
        }
        var lodf = new double[outages.Count][];
        for (int c = 0; c < outages.Count; c++) lodf[c] = df.LodfColumn(outages[c]);

        // Monitored after an outage: every rated branch of the DC model, at the post-outage rating.
        var postRate = new double[nBr];
        var monitored = new List<int>();
        for (int l = 0; l < nBr; l++)
        {
            if (!inDc[l] || !(model.Branches[l].RateAMva > 0)) continue;
            postRate[l] = PostRating(model.Branches[l], options.PostContingencyRating);
            monitored.Add(l);
        }

        double demand = 0;
        foreach (Bus b in model.Buses) demand += b.Pd;

        // The pairs (outage position c, branch l) in the LP: all of them, or none to start with.
        var active = new SortedSet<(int C, int L)>();
        if (!options.ConstraintGeneration)
            for (int c = 0; c < outages.Count; c++)
                foreach (int l in monitored)
                    if (l != outages[c] && lodf[c][l] != 0.0) active.Add((c, l));

        int rounds = 0, lpIterations = 0;
        while (true)
        {
            rounds++;
            Lp lp = Build(model, busIndex, genBus, cost, refIndex, inDc, outages, lodf, postRate, active);
            SparseSimplexResult r = SparseBoundedSimplex.Solve(lp.Problem, maxIterations: options.LpMaxIterations);
            lpIterations += r.Iterations;
            if (!r.Optimal)
                return Failed(Map(r.Status), demand, outages, islanding, rounds, active.Count, lp, lpIterations,
                    $"SCOPF LP did not reach an optimum in round {rounds}: {r.Message}");

            double[] flow = new double[nBr];
            for (int l = 0; l < nBr; l++) if (inDc[l]) flow[l] = r.X[lp.FlowCol[l]];

            int added = 0;
            for (int c = 0; c < outages.Count; c++)
            {
                double fk = flow[outages[c]];
                foreach (int l in monitored)
                {
                    if (l == outages[c]) continue;
                    double f = lodf[c][l];
                    if (f == 0.0 || active.Contains((c, l))) continue;
                    double post = flow[l] + f * fk;
                    if (Math.Abs(post) > postRate[l] + ViolationToleranceMw) { active.Add((c, l)); added++; }
                }
            }
            if (added > 0)
            {
                if (rounds >= options.MaxRounds)
                    return Failed(ScopfStatus.RoundLimit, demand, outages, islanding, rounds, active.Count, lp, lpIterations,
                        $"SCOPF: {added} post-outage limit(s) still broken after {rounds} round(s) (the cap).");
                continue;
            }

            var dispatch = new List<ScopfDispatch>(G);
            double total = 0;
            for (int g = 0; g < G; g++)
            {
                double p = r.X[g];
                total += cost[g] * p;
                dispatch.Add(new ScopfDispatch(g, model.Generators[g].Bus, model.Generators[g].Name ?? "", p, cost[g]));
            }
            var prices = new List<ScopfBusPrice>(nBus);
            for (int i = 0; i < nBus; i++) prices.Add(new ScopfBusPrice(model.Buses[i].Id, r.Y[lp.BalanceRow0 + i] + 0.0));

            var bindings = new List<ScopfBinding>();
            for (int l = 0; l < nBr; l++)
            {
                double rate = model.Branches[l].RateAMva;
                if (inDc[l] && rate > 0 && Math.Abs(flow[l]) >= rate - BindingToleranceMw)
                    bindings.Add(new ScopfBinding(l, BranchName(model, l), -1, flow[l], rate));
            }
            for (int c = 0; c < outages.Count; c++)
            {
                double fk = flow[outages[c]];
                foreach (int l in monitored)
                {
                    if (l == outages[c]) continue;
                    double post = flow[l] + lodf[c][l] * fk;
                    if (Math.Abs(post) >= postRate[l] - BindingToleranceMw)
                        bindings.Add(new ScopfBinding(l, BranchName(model, l), outages[c], post, postRate[l]));
                }
            }
            return new ScopfResult(ScopfStatus.Optimal, total, demand, dispatch, flow, prices, bindings, outages, islanding,
                rounds, active.Count, lp.Problem.Rows, lp.Problem.Columns, lpIterations,
                $"optimal after {rounds} LP solve(s) with {active.Count} post-outage limit(s) in the LP");
        }
    }

    /// <summary>The post-outage rating of a branch: RateB or RateC when asked for and given (above 0), else RateA.</summary>
    public static double PostRating(Branch br, PostFaultRating which)
    {
        ArgumentNullException.ThrowIfNull(br);
        double r = which switch
        {
            PostFaultRating.RateB => br.RateBMva,
            PostFaultRating.RateC => br.RateCMva,
            _ => 0.0,
        };
        return r > 0 ? r : br.RateAMva;
    }

    private sealed record Lp(SparseLpProblem Problem, int[] FlowCol, int BalanceRow0);

    private static Lp Build(GridModel model, Dictionary<int, int> busIndex, int[] genBus, double[] cost, int refIndex,
        bool[] inDc, List<int> outages, double[][] lodf, double[] postRate, SortedSet<(int C, int L)> active)
    {
        int nBus = model.Buses.Count, nBr = model.Branches.Count, G = genBus.Length;
        var a = new List<(int, int, double)>();
        var c = new List<double>();
        var lo = new List<double>();
        var hi = new List<double>();
        var b = new List<double>();
        int Col(double cc, double l, double h) { c.Add(cc); lo.Add(l); hi.Add(h); return c.Count - 1; }

        for (int g = 0; g < G; g++)
        {
            Generator gen = model.Generators[g];
            Col(cost[g], gen.Pmin, double.IsPositiveInfinity(gen.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : gen.Pmax);
        }
        int psi0 = c.Count;
        for (int i = 0; i < nBus; i++)
            if (i == refIndex) Col(0, 0, 0); else Col(0, double.NegativeInfinity, double.PositiveInfinity);
        var flowCol = new int[nBr];
        for (int l = 0; l < nBr; l++)
        {
            flowCol[l] = -1;
            if (!inDc[l]) continue;
            double rate = model.Branches[l].RateAMva;
            flowCol[l] = rate > 0 ? Col(0, -rate, rate) : Col(0, double.NegativeInfinity, double.PositiveInfinity);
        }

        // Bus balance rows: units in, flows out, flows in = demand.
        int balance0 = b.Count;
        for (int i = 0; i < nBus; i++) b.Add(model.Buses[i].Pd);
        for (int g = 0; g < G; g++) a.Add((balance0 + genBus[g], g, 1.0));
        for (int l = 0; l < nBr; l++)
        {
            if (!inDc[l]) continue;
            Branch br = model.Branches[l];
            int f = busIndex[br.From], t = busIndex[br.To];
            a.Add((balance0 + f, flowCol[l], -1.0));
            a.Add((balance0 + t, flowCol[l], 1.0));
        }
        // Flow rows: f - b (psi_from - psi_to) = 0.
        for (int l = 0; l < nBr; l++)
        {
            if (!inDc[l]) continue;
            Branch br = model.Branches[l];
            double sus = 1.0 / br.X;
            int row = b.Count;
            b.Add(0.0);
            a.Add((row, flowCol[l], 1.0));
            a.Add((row, psi0 + busIndex[br.From], -sus));
            a.Add((row, psi0 + busIndex[br.To], sus));
        }
        // Secured pairs: q - f_l - LODF(l, k) f_k = 0, q within the post-outage rating.
        foreach ((int ci, int l) in active)
        {
            int q = Col(0, -postRate[l], postRate[l]);
            int row = b.Count;
            b.Add(0.0);
            a.Add((row, q, 1.0));
            a.Add((row, flowCol[l], -1.0));
            a.Add((row, flowCol[outages[ci]], -lodf[ci][l]));
        }
        var problem = new SparseLpProblem(b.Count, c.Count, a, b.ToArray(), c.ToArray(), lo.ToArray(), hi.ToArray());
        return new Lp(problem, flowCol, balance0);
    }

    private static ScopfStatus Map(SparseSimplexStatus s) => s switch
    {
        SparseSimplexStatus.Optimal => ScopfStatus.Optimal,
        SparseSimplexStatus.Infeasible => ScopfStatus.Infeasible,
        SparseSimplexStatus.Unbounded => ScopfStatus.Unbounded,
        SparseSimplexStatus.IterationCap => ScopfStatus.IterationCap,
        _ => ScopfStatus.NumericalFailure,
    };

    private static ScopfResult Failed(ScopfStatus status, double demand, List<int> outages, List<int> islanding, int rounds,
        int pairs, Lp lp, int lpIterations, string message) =>
        new(status, double.NaN, demand, Array.Empty<ScopfDispatch>(), Array.Empty<double>(), Array.Empty<ScopfBusPrice>(),
            Array.Empty<ScopfBinding>(), outages, islanding, rounds, pairs, lp.Problem.Rows, lp.Problem.Columns, lpIterations, message);

    private static string BranchName(GridModel model, int l)
    {
        Branch br = model.Branches[l];
        return string.IsNullOrEmpty(br.Name) ? $"{br.From}-{br.To}" : br.Name;
    }
}
