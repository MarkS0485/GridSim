using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// DC optimal power flow / security-constrained economic dispatch: the cheapest generation dispatch
/// that meets demand and keeps every rated line within its thermal limit. It layers on the DC
/// sensitivities of <see cref="DistributionFactors"/> (line flow = PTDF . injection).
///
/// When line limits are enforced it solves the exact security-constrained LP
///   minimise  sum_i cost_i . Pg_i
///   subject to  sum_i Pg_i = demand,   Pmin_i &lt;= Pg_i &lt;= Pmax_i,
///               -RateA_l &lt;= f0_l + sum_i PTDF[l, bus_i] . Pg_i &lt;= RateA_l  for every rated line l,
/// where f0_l is the line's flow from the fixed loads. This is a bounded-variable linear program (one
/// balance row plus a thermal row per rated line; a per-line slack in [0, 2.RateA] carries the two-sided
/// limit), solved to a provable optimum by the self-contained <see cref="BoundedSimplex"/> - so the
/// dispatch is globally cost-optimal across ALL simultaneously-binding constraints, not a one-line-at-a-
/// time redispatch. With limits off it reduces to a plain merit-order (cheapest-first) economic dispatch.
/// Costs are a linear per-generator marginal cost, defaulted from fuel type when a case carries no
/// explicit cost. DC assumptions (reactance-only, 1 pu voltages) are inherited from the PTDF.
///
/// Scale (R005, October 2026): the LP is solved by <see cref="DcOpfSecuritySimplex"/>, the BoundedSimplex
/// algorithm performed operation for operation without its dense matrices (PTDF held once per distinct
/// generator bus; basis inverse stored column by column, dense only where filled in), so the answer is the
/// dense solver's bit for bit while gb-full fits in memory. The original dense path remains callable as
/// <see cref="SolveDenseReference"/> for the parity tests.
///
/// <para><b>Islands (N034, October 2026).</b> A network of several DC islands (see
/// <see cref="DistributionFactors.FindIslands"/>) is dispatched island by island: no power crosses between islands,
/// so each energised island is an independent problem with its own balance (its own generation meets its own
/// demand), its own reference bus (the AC island rule: its slack, else its largest generator) and its own rated
/// lines. Each island is solved by exactly the connected-case code below and the answers are merged back in model
/// order (<see cref="DcOpfResult.Islands"/> reports each island). An island with no generator cannot be supplied: it
/// is reported (with its load) and, when it carries load, the result is not feasible; it is never silently dropped.
/// Before N034 a network with islands threw "DC susceptance is singular" (gb-full could not be dispatched at all).
/// A connected network takes the unchanged single-model path, bit for bit.</para>
/// </summary>
public static class DcOptimalPowerFlow
{
    // ---- Named constants (October 2026 review, WS13 R154). Values unchanged.

    /// <summary>Stand-in Pmax (MW) for a generator with an infinite Pmax, so the LP bounds stay finite.</summary>
    public const double UnboundedPmaxMw = 1e6;

    /// <summary>Tolerance (MW) of the merit-order balance feasibility test: demand may exceed the dispatchable
    /// range, or fall below the Pmin floor, by this much and still count as met.</summary>
    public const double BalanceToleranceMw = 1e-6;

    /// <summary>The merit-order fill stops once the unserved remainder is at or below this (MW). <b>Differs from
    /// <see cref="BalanceToleranceMw"/>, deliberately</b> (R154, decided under Q13, October 2026): they are two
    /// different jobs, not one tolerance written twice. This one is a loop stop that should leave as little
    /// unserved as the arithmetic allows (each take is min(room, remaining), so the remainder normally lands on
    /// exactly 0 and this only guards against a sub-nanowatt rounding crumb sending the fill on through every
    /// remaining unit). <see cref="BalanceToleranceMw"/> is a feasibility verdict: how far demand may sit outside the
    /// dispatchable range and still count as met. Using the looser 1e-6 here would stop the fill up to 1e-6 MW
    /// short and move dispatch figures in their last digits for no gain; using the tighter 1e-9 for the verdict
    /// would call a case infeasible over rounding in a sum of thousands of MW. Both stay.</summary>
    public const double MeritOrderFillStopMw = 1e-9;

    /// <summary>A rated line whose |flow| is within this (MW) of its rating is reported as binding.</summary>
    public const double BindingToleranceMw = 1e-3;

    /// <summary>Mid-merit marginal cost (GBP/MWh) given to a generator whose fuel type is empty or unknown.</summary>
    public const double DefaultCostPerMwh = 50.0;

    /// <summary>A generator's dispatched output and its marginal cost.</summary>
    public sealed record GenDispatch(int GenBus, double Pg, double CostPerMwh);

    /// <summary>A line held at (or over) its thermal limit by the dispatch.</summary>
    public sealed record LineBinding(int Branch, string Name, double FlowMw, double RateMw);

    /// <summary>
    /// N109 (Ruling R1, Mark delegated 2026-10-04): how far a rated line's DC flow must miss its rating, over EVERY
    /// dispatch in the units' output ranges, before the line is called unrelievable and left out of the security LP
    /// (MW). It is ten times <see cref="BoundedSimplex.PhaseOneFeasibilityTolerance"/> (1e-7, the most total infeasibility
    /// an LP may keep and still be called feasible), so a line left out could never have been met by an LP the solver
    /// calls Optimal: every security LP that was feasible before N109 leaves nothing out and is solved exactly as before.
    /// </summary>
    public const double UnrelievableMarginMw = 1e-6;

    /// <summary>
    /// N109: a rated line that no dispatch can bring within its rating, left out of the security LP and reported here
    /// (as the corrective redispatch leaves out an element no generator can move, and as PowerWorld and PSS/E report
    /// uncontrollable violations apart from the optimised ones). The reachable range is over every unit anywhere in
    /// [Pmin, Pmax] with the balance row dropped, a superset of every dispatch the LP could choose, so a line whose range
    /// misses its rating cannot be relieved by any dispatch, and keeping it would only make the whole LP Infeasible.
    /// </summary>
    /// <param name="Branch">The line's index in the model's branch list (full-model index, also for an island).</param>
    /// <param name="Name">The line's name (empty when it has none).</param>
    /// <param name="RateMw">The line's rating (RateA, read as MW, as the LP reads it).</param>
    /// <param name="ReachableMinMw">The lowest DC flow any dispatch in the units' ranges gives the line (MW).</param>
    /// <param name="ReachableMaxMw">The highest DC flow any dispatch in the units' ranges gives the line (MW).</param>
    public sealed record UnrelievableLine(int Branch, string Name, double RateMw, double ReachableMinMw, double ReachableMaxMw);

    /// <summary>One DC island's share of a multi-island dispatch (N034).</summary>
    /// <param name="ReferenceBus">The island's reference bus (its slack, else its largest generator, else its first bus).</param>
    /// <param name="BusCount">How many buses the island holds.</param>
    /// <param name="LoadMw">The island's demand (MW).</param>
    /// <param name="GenMw">The island's dispatched generation (MW); 0 for an island with no generator.</param>
    /// <param name="HasGeneration">False for a dead island: no generator sits on it, so its load cannot be supplied.</param>
    /// <param name="Feasible">The island's own verdict: its dispatch meets its demand (and its line limits, when they
    /// are enforced). A dead island is feasible only when it carries no load.</param>
    /// <param name="SecurityLpStatus">The island's security LP outcome; null when no LP ran for it.</param>
    public sealed record IslandDispatch(int ReferenceBus, int BusCount, double LoadMw, double GenMw,
        bool HasGeneration, bool Feasible, LpStatus? SecurityLpStatus);

    /// <summary>The dispatch result.</summary>
    public sealed record DcOpfResult(
        IReadOnlyList<GenDispatch> Dispatch,
        double TotalCost,
        double TotalGenMw,
        double TotalLoadMw,
        IReadOnlyList<LineBinding> BindingLines,
        bool Feasible,
        bool NetworkConstrained)
    {
        /// <summary>The security-constrained LP's outcome, or null when no LP ran (unconstrained dispatch, no
        /// rated lines, or the merit order was already infeasible). Lets a caller tell a numerical stall
        /// (<see cref="LpStatus.IterationLimit"/>) or an unbounded LP from true infeasibility (R024).</summary>
        public LpStatus? SecurityLpStatus { get; init; }

        /// <summary>Per-island breakdown when the network has more than one DC island (N034); null for a connected
        /// network. With islands, <see cref="SecurityLpStatus"/> is null when no island ran an LP, Optimal when every
        /// island that ran one reached it, and otherwise the first non-optimal island status (in island order).</summary>
        public IReadOnlyList<IslandDispatch>? Islands { get; init; }

        /// <summary>F19: the nodal prices (LMPs) and line shadow prices, when <see cref="DcOpfOptions.ComputePrices"/>
        /// asked for them and the dispatch reached an optimum; null otherwise.</summary>
        public DcOpfPrices? Prices { get; init; }

        /// <summary>F19: true when each generator's own cost curve (<see cref="Generator.Cost"/>) set the objective;
        /// false for the fuel-type merit-order costs. With curves, <see cref="TotalCost"/> is the sum of the curves'
        /// values (constant terms included, as MATPOWER reports its objective) and each
        /// <see cref="GenDispatch.CostPerMwh"/> is the unit's marginal cost at its dispatch.</summary>
        public bool UsedGeneratorCosts { get; init; }

        /// <summary>F19: why the cost-curve solve did not reach an optimum (for example an iteration cap), when it did
        /// not; null otherwise.</summary>
        public string? SolverFailure { get; init; }

        /// <summary>
        /// N109: the rated lines no dispatch can bring within their rating, left out of the security LP (see
        /// <see cref="UnrelievableLine"/>), in branch order; empty when there are none or no security LP ran. When the
        /// LP over the remaining lines is Optimal the result is <see cref="Feasible"/> for those lines, and these lines
        /// are still over their rating at the dispatch (they also appear in <see cref="BindingLines"/> with their flow),
        /// so a caller must report them; they are never silently dropped.
        /// </summary>
        public IReadOnlyList<UnrelievableLine> UnrelievableLines { get; init; } = Array.Empty<UnrelievableLine>();
    }

    /// <summary>
    /// F19 (October 2026): options of <see cref="Solve(GridModel, DcOpfOptions)"/>. The defaults are exactly the
    /// historical <see cref="Solve(GridModel, double[], bool)"/> call: fuel-type costs, line limits on, no prices.
    /// </summary>
    public sealed record DcOpfOptions
    {
        /// <summary>Hold every rated line within RateA (the security LP). Off gives the unconstrained dispatch.</summary>
        public bool EnforceLineLimits { get; init; } = true;

        /// <summary>Use each in-service generator's own cost curve (<see cref="Generator.Cost"/>: MATPOWER gencost or
        /// the JSON cost file) instead of the fuel-type marginal costs. Every in-service unit must then carry one;
        /// polynomial curves up to quadratic and convex piecewise-linear curves are optimised exactly
        /// (<see cref="GeneratorCostCurve"/>).</summary>
        public bool UseGeneratorCosts { get; init; }

        /// <summary>Also report the nodal prices (LMPs) and the binding lines' shadow prices from the optimum's dual
        /// values (<see cref="DcOpfResult.Prices"/>). The dispatch itself is unchanged by asking.</summary>
        public bool ComputePrices { get; init; }
    }

    /// <summary>F19: the price at one bus. Lmp = EnergyComponent + CongestionComponent (the DC model is lossless: no
    /// loss component). Units: the cost curves' currency per MWh (GBP/MWh for the fuel-type costs).</summary>
    /// <param name="BusId">The bus.</param>
    /// <param name="Lmp">Locational marginal price: the optimal cost's increase per MW of extra demand at the bus.</param>
    /// <param name="EnergyComponent">The balance row's dual: the price at the island's reference bus.</param>
    /// <param name="CongestionComponent">The binding lines' contribution: the sum over lines of dual x PTDF at the bus.</param>
    public sealed record BusPrice(int BusId, double Lmp, double EnergyComponent, double CongestionComponent);

    /// <summary>F19: a line's shadow price: the optimal cost saved per MW of extra rating (0 when it does not bind).</summary>
    public sealed record LinePrice(int Branch, string Name, double FlowMw, double RateMw, double ShadowPrice);

    /// <summary>F19: the prices of a dispatch: one entry per bus (model order) and one per binding line (branch order).</summary>
    public sealed record DcOpfPrices(IReadOnlyList<BusPrice> Buses, IReadOnlyList<LinePrice> Lines);

    /// <summary>
    /// F19: solve the DC-OPF with options. With the options at their defaults this IS
    /// <see cref="Solve(GridModel, double[], bool)"/> (the same call, so every number is unchanged). With
    /// <see cref="DcOpfOptions.UseGeneratorCosts"/> each unit's cost curve sets the objective
    /// (<see cref="DcOpfCosted"/>); with <see cref="DcOpfOptions.ComputePrices"/> the nodal and line prices are added,
    /// leaving the dispatch exactly as it would be without them.
    /// </summary>
    public static DcOpfResult Solve(GridModel model, DcOpfOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.UseGeneratorCosts && !options.ComputePrices) return Solve(model, null, options.EnforceLineLimits);
        return SolveCore(model, null, options.EnforceLineLimits, denseReference: false,
            useCosts: options.UseGeneratorCosts, computePrices: options.ComputePrices);
    }

    /// <summary>Solve the DC-OPF. <paramref name="costPerGen"/> (aligned to <c>model.Generators</c>,
    /// £/MWh) overrides the fuel-based default. <paramref name="enforceLineLimits"/> off gives the plain
    /// merit-order (unconstrained) economic dispatch.</summary>
    public static DcOpfResult Solve(GridModel model, double[]? costPerGen = null, bool enforceLineLimits = true)
        => SolveCore(model, costPerGen, enforceLineLimits, denseReference: false);

    /// <summary>The same solve through the ORIGINAL dense security LP (a dense constraint matrix handed to the dense
    /// <see cref="BoundedSimplex"/>). Kept, unchanged, as the bit-for-bit reference that the structured solver
    /// (<see cref="DcOpfSecuritySimplex"/>) is tested against (R005). Practical only for small and mid-size cases:
    /// it needs about 8 (m n + m (n + m) + m^2 + g B) bytes for m = 1 + rated lines, n = generators + rated lines,
    /// g generators and B branches, and an O(m^3) refactorisation every 64 simplex iterations.</summary>
    internal static DcOpfResult SolveDenseReference(GridModel model, double[]? costPerGen = null, bool enforceLineLimits = true)
        => SolveCore(model, costPerGen, enforceLineLimits, denseReference: true);

    private static DcOpfResult SolveCore(GridModel model, double[]? costPerGen, bool enforceLineLimits, bool denseReference,
        bool useCosts = false, bool computePrices = false)
    {
        // N034: a network of several DC islands is dispatched island by island. A connected network (one island)
        // falls straight through to the unchanged single-model path below.
        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(model);
        if (islands.Count > 1) return SolveIslands(model, islands, costPerGen, enforceLineLimits, denseReference, useCosts, computePrices);

        // F19: each unit's own cost curve (opt-in). A separate solver; the fuel-type path below is not touched by it.
        if (useCosts) return DcOpfCosted.SolveConnected(model, enforceLineLimits, computePrices);

        int g = model.Generators.Count;
        // GR-607: the same length rule as the island path (a longer vector was truncated, a shorter one ran off its end).
        if (costPerGen is not null && costPerGen.Length != g)
            throw new ArgumentException(
                $"DC-OPF: {costPerGen.Length} costs given for {g} generators; the cost vector must align to model.Generators.",
                nameof(costPerGen));
        var cost = costPerGen ?? model.Generators.Select(gen => FuelCostPerMwh(gen.Type)).ToArray();
        CheckGeneratorLimits(model);
        var pmin = model.Generators.Select(gen => gen.Pmin).ToArray();
        var pmax = model.Generators.Select(gen => double.IsInfinity(gen.Pmax) ? UnboundedPmaxMw : gen.Pmax).ToArray();
        double demand = model.Buses.Sum(b => b.Pd);

        // 1. Merit order: start at Pmin, then load cheapest generators up to Pmax to meet demand.
        var pg = (double[])pmin.Clone();
        double remaining = demand - pg.Sum();
        bool feasible = remaining >= -BalanceToleranceMw
            && Enumerable.Range(0, g).Sum(i => pmax[i] - pmin[i]) + BalanceToleranceMw >= remaining;
        foreach (int i in Enumerable.Range(0, g).OrderBy(i => cost[i]))
        {
            if (remaining <= MeritOrderFillStopMw) break;
            double room = pmax[i] - pg[i];
            double take = Math.Min(room, remaining);
            pg[i] += take; remaining -= take;
        }
        if (remaining > BalanceToleranceMw) feasible = false;

        bool constrained = false;
        var binding = new List<LineBinding>();
        LpStatus? lpStatus = null;
        double[]? duals = null;          // F19: the security LP's optimal-basis duals (prices asked for only)
        SecurityLp? securityLp = null;
        IReadOnlyList<UnrelievableLine> unrelievable = Array.Empty<UnrelievableLine>();   // N109
        if (enforceLineLimits && feasible && model.Branches.Any(b => b.RateAMva > 0))
        {
            LpStatus st;
            if (computePrices && !denseReference)
            {
                // F19: the same LP run, with its duals kept (SolveDetailed is Solve's arithmetic exactly).
                var withDuals = SecuredDispatchWithDuals(model, cost, pmin, pmax, pg);
                (pg, feasible, constrained, binding, st) = withDuals.Outcome;
                duals = withDuals.Duals;
                securityLp = withDuals.Lp;
                unrelievable = withDuals.Lp.Unrelievable;
            }
            else if (denseReference)
            {
                (pg, feasible, constrained, binding, st, unrelievable) = SecuredDispatchDense(model, cost, pmin, pmax, pg);
            }
            else
            {
                var structured = SecuredDispatchWithDuals(model, cost, pmin, pmax, pg);
                (pg, feasible, constrained, binding, st) = structured.Outcome;
                unrelievable = structured.Lp.Unrelievable;
            }
            lpStatus = st;
        }

        var dispatch = new List<GenDispatch>(g);
        double total = 0;
        for (int i = 0; i < g; i++) { dispatch.Add(new GenDispatch(model.Generators[i].Bus, pg[i], cost[i])); total += pg[i] * cost[i]; }
        DcOpfPrices? prices = computePrices && !denseReference && feasible
            ? FuelPathPrices(model, cost, pmin, pmax, demand, pg, binding, lpStatus, duals, securityLp)
            : null;
        return new DcOpfResult(dispatch, total, pg.Sum(), demand, binding, feasible, constrained)
        {
            SecurityLpStatus = lpStatus, Prices = prices, UnrelievableLines = unrelievable,
        };
    }

    /// <summary>
    /// F19: prices for the fuel-type path. When the security LP ran and reached its optimum, they come from its duals.
    /// When no LP ran (line limits off, or no rated line) the merit-order dispatch is the optimum of the balance-only LP
    /// (minimise cost . Pg subject to sum Pg = demand and the bounds), so that LP is solved for its dual: any dual
    /// optimum pairs with any primal optimum, so the price it gives is a valid price for the merit-order dispatch even
    /// when the LP lands on a different tied vertex. Every bus then has that one price (no congestion component).
    /// Null when the LP did not reach its optimum.
    /// </summary>
    private static DcOpfPrices? FuelPathPrices(GridModel model, double[] cost, double[] pmin, double[] pmax, double demand,
        double[] pg, List<LineBinding> binding, LpStatus? lpStatus, double[]? duals, SecurityLp? securityLp)
    {
        if (lpStatus is not null)
        {
            if (lpStatus != LpStatus.Optimal || duals is null || securityLp is null) return null;
            var lineDuals = new double[securityLp.Rated.Count];
            Array.Copy(duals, 1, lineDuals, 0, lineDuals.Length);
            double[] flow = FlowsFrom(model, securityLp.Factors, pg);
            return PricesFrom(model, securityLp.Factors, securityLp.Rated, duals[0], lineDuals, flow, binding);
        }

        int g = model.Generators.Count;
        var problem = WithTieBreak(new DcOpfSecuritySimplex.Problem(g, 0, 1, new int[g], Array.Empty<double>(),
            new[] { demand }, (double[])cost.Clone(), (double[])pmin.Clone(), (double[])pmax.Clone()));
        DcOpfSecuritySimplex.Detailed d = DcOpfSecuritySimplex.SolveDetailed(problem);
        if (d.Status != LpStatus.Optimal || d.Duals is null) return null;
        double y0 = d.Duals[0];
        var buses = model.Buses.Select(b => new BusPrice(b.Id, y0, y0, 0.0)).ToList();
        return new DcOpfPrices(buses, Array.Empty<LinePrice>());
    }

    /// <summary>
    /// Pmin is used AS GIVEN (R227, October 2026). It used to be clamped to zero, so a unit that can absorb power -
    /// storage or pumped storage charging, an exporting interconnector - could never be dispatched below zero, and the
    /// merit-order feasibility test and the LP bounds silently excluded that range. The balance row handles negative
    /// injections exactly like positive ones. An inconsistent range is an input error, not something to quietly repair.
    /// (Moved here unchanged from SolveCore in F19, so the cost-curve path applies the same checks.)
    /// </summary>
    internal static void CheckGeneratorLimits(GridModel model)
    {
        int g = model.Generators.Count;
        for (int i = 0; i < g; i++)
        {
            Generator gen = model.Generators[i];
            if (double.IsNaN(gen.Pmin) || double.IsInfinity(gen.Pmin))
                throw new InvalidDataException(
                    $"DC-OPF: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus}) has Pmin {gen.Pmin} MW; a dispatch floor must be finite.");
            if (double.IsNaN(gen.Pmax) || gen.Pmin > gen.Pmax)
                throw new InvalidDataException(
                    $"DC-OPF: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus}) has Pmin {gen.Pmin} MW above Pmax {gen.Pmax} MW.");
        }
    }

    /// <summary>
    /// N034: dispatch a network of several DC islands one island at a time. Each energised island becomes its own
    /// <see cref="GridModel"/> (its buses, the branches with both ends on it and the generators on it, all in model
    /// order; its reference bus marked Slack when it has none, the AC island rule) and is solved by
    /// <see cref="SolveCore"/>, which for a single island is the unchanged connected-case path. The answers are mapped
    /// back to the full model's generator and branch indices. A dead island (no generator) is reported and makes the
    /// result infeasible when it carries load. Fails loud on a generator whose bus is not in the model (it belongs to
    /// no island) and on a cost vector that does not match the generators.
    /// </summary>
    private static DcOpfResult SolveIslands(GridModel model, IReadOnlyList<DcIsland> islands, double[]? costPerGen,
        bool enforceLineLimits, bool denseReference, bool useCosts, bool computePrices)
    {
        int g = model.Generators.Count;
        if (costPerGen is not null && costPerGen.Length != g)
            throw new ArgumentException(
                $"DC-OPF: {costPerGen.Length} costs given for {g} generators; the cost vector must align to model.Generators.",
                nameof(costPerGen));
        var cost = costPerGen ?? model.Generators.Select(gen => FuelCostPerMwh(gen.Type)).ToArray();

        // Bus id -> island number.
        var islandOfBus = new Dictionary<int, int>(model.Buses.Count);
        for (int k = 0; k < islands.Count; k++)
            foreach (int id in islands[k].BusIds) islandOfBus[id] = k;

        // Generators per island, in model order. A generator on a bus the model does not have belongs to no island:
        // the connected path would give it a zero PTDF column; here there is no island to balance it in, so refuse.
        var gensOf = new List<int>[islands.Count];
        for (int k = 0; k < islands.Count; k++) gensOf[k] = new List<int>();
        for (int i = 0; i < g; i++)
        {
            Generator gen = model.Generators[i];
            if (!islandOfBus.TryGetValue(gen.Bus, out int k))
                throw new InvalidDataException(
                    $"DC-OPF: generator {i} ('{gen.Name ?? gen.Type}') sits on bus {gen.Bus}, which is not in the model.");
            gensOf[k].Add(i);
        }

        // Branches per island, in model order: those with both ends on the island. A branch joining two islands is
        // out of service (an in-service one would have joined them, and a zero-reactance one was refused by
        // FindIslands), and a branch with an end bus the model does not have carries no DC flow in the connected path
        // either; neither belongs to an island, and neither can carry flow, so neither can bind.
        var branchesOf = new List<int>[islands.Count];
        for (int k = 0; k < islands.Count; k++) branchesOf[k] = new List<int>();
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch br = model.Branches[l];
            if (islandOfBus.TryGetValue(br.From, out int kf) && islandOfBus.TryGetValue(br.To, out int kt) && kf == kt)
                branchesOf[kf].Add(l);
        }

        var pg = new double[g];
        var binding = new List<LineBinding>();
        var unrelievable = new List<UnrelievableLine>();   // N109: every island's left-out lines, full-model indices
        var summaries = new List<IslandDispatch>(islands.Count);
        bool feasible = true, constrained = false;
        LpStatus? lpStatus = null;
        bool anyLp = false;
        // F19 (cost curves / prices only): each unit's marginal cost and each island's own total, since a curve's cost
        // is not Pg x a constant; and the per-island prices, merged by bus id and full-model branch index.
        var marginal = new double[g];
        double curveTotal = 0.0;
        var busPrices = new Dictionary<int, BusPrice>();
        var linePrices = new List<LinePrice>();
        bool pricesComplete = computePrices;
        string? failure = null;
        for (int k = 0; k < islands.Count; k++)
        {
            DcIsland island = islands[k];
            if (!island.HasGeneration)
            {
                // Dead island: nothing can supply it. Reported, never dropped; infeasible when it has load to serve.
                bool deadOk = !(Math.Abs(island.LoadMw) > BalanceToleranceMw);
                if (!deadOk) feasible = false;
                summaries.Add(new IslandDispatch(island.ReferenceBusId, island.BusIds.Count, island.LoadMw, 0.0,
                    HasGeneration: false, Feasible: deadOk, SecurityLpStatus: null));
                continue;
            }

            // The island as its own model. Its reference is marked Slack when the island has no slack bus (the AC
            // island pre-pass does the same), so the island's PTDF uses the reference the AC rule elects.
            var busSet = new HashSet<int>(island.BusIds);
            var buses = new List<Bus>(island.BusIds.Count);
            foreach (Bus bus in model.Buses)
            {
                if (!busSet.Contains(bus.Id)) continue;
                buses.Add(bus.Id == island.ReferenceBusId && bus.Type != BusType.Slack ? bus with { Type = BusType.Slack } : bus);
            }
            List<int> gi = gensOf[k];
            List<int> bi = branchesOf[k];
            GridModel sub = model.CopyWith(
                buses: buses,
                generators: gi.Select(i => model.Generators[i]).ToList(),
                branches: bi.Select(l => model.Branches[l]).ToList());
            double[] subCost = gi.Select(i => cost[i]).ToArray();

            DcOpfResult r = SolveCore(sub, subCost, enforceLineLimits, denseReference, useCosts, computePrices);
            if (r.Islands is not null)
                throw new InvalidOperationException("DC-OPF: an island split again when solved on its own; the island partition is inconsistent.");

            for (int j = 0; j < gi.Count; j++) { pg[gi[j]] = r.Dispatch[j].Pg; marginal[gi[j]] = r.Dispatch[j].CostPerMwh; }
            curveTotal += r.TotalCost;
            failure ??= r.SolverFailure;
            if (computePrices)
            {
                if (r.Prices is null) pricesComplete = false;
                else
                {
                    foreach (BusPrice bp in r.Prices.Buses) busPrices[bp.BusId] = bp;
                    foreach (LinePrice lp in r.Prices.Lines) linePrices.Add(lp with { Branch = bi[lp.Branch] });
                }
            }
            foreach (LineBinding lb in r.BindingLines) binding.Add(lb with { Branch = bi[lb.Branch] });
            foreach (UnrelievableLine ul in r.UnrelievableLines) unrelievable.Add(ul with { Branch = bi[ul.Branch] });
            feasible &= r.Feasible;
            constrained |= r.NetworkConstrained;
            if (r.SecurityLpStatus is LpStatus st)
            {
                anyLp = true;
                if (st != LpStatus.Optimal && (lpStatus is null || lpStatus == LpStatus.Optimal)) lpStatus = st;
                else lpStatus ??= st;
            }
            summaries.Add(new IslandDispatch(island.ReferenceBusId, island.BusIds.Count, island.LoadMw, r.TotalGenMw,
                HasGeneration: true, Feasible: r.Feasible, SecurityLpStatus: r.SecurityLpStatus));
        }
        if (!anyLp) lpStatus = null;

        // Binding lines in branch order (the order the connected path reports them in).
        binding.Sort((a, b) => a.Branch.CompareTo(b.Branch));
        unrelievable.Sort((a, b) => a.Branch.CompareTo(b.Branch));

        // Merge in model order, summing exactly as the connected path does.
        var dispatch = new List<GenDispatch>(g);
        double total = 0;
        if (!useCosts)
        {
            for (int i = 0; i < g; i++) { dispatch.Add(new GenDispatch(model.Generators[i].Bus, pg[i], cost[i])); total += pg[i] * cost[i]; }
        }
        else
        {
            // F19: with cost curves each unit reports its marginal cost and the total is the islands' curve totals.
            for (int i = 0; i < g; i++) dispatch.Add(new GenDispatch(model.Generators[i].Bus, pg[i], marginal[i]));
            total = curveTotal;
        }
        double demand = model.Buses.Sum(b => b.Pd);

        // F19: prices only when every energised island reached a priced optimum. A dead island's buses get no price
        // (nothing can supply them, so there is no marginal cost to report); they are left out of the bus list.
        DcOpfPrices? prices = null;
        if (computePrices && pricesComplete)
        {
            var buses = new List<BusPrice>();
            foreach (Bus b in model.Buses) if (busPrices.TryGetValue(b.Id, out BusPrice? bp)) buses.Add(bp);
            linePrices.Sort((x, y) => x.Branch.CompareTo(y.Branch));
            prices = new DcOpfPrices(buses, linePrices);
        }
        return new DcOpfResult(dispatch, total, pg.Sum(), demand, binding, feasible, constrained)
        {
            SecurityLpStatus = lpStatus,
            Islands = summaries,
            Prices = prices,
            UsedGeneratorCosts = useCosts,
            SolverFailure = failure,
            UnrelievableLines = unrelievable.Count == 0 ? Array.Empty<UnrelievableLine>() : unrelievable,
        };
    }

    /// <summary>The security LP in structured form, with what is needed to interpret its answer.
    /// <see cref="Rated"/> are the lines that have a thermal row (in branch order); N109: <see cref="AllRated"/> are every
    /// rated line (RateA &gt; 0, the reporting set, as before N109) and <see cref="Unrelievable"/> the rated lines left
    /// out because no dispatch can relieve them. With nothing left out, Rated and AllRated hold the same lines.</summary>
    internal sealed record SecurityLp(DistributionFactors Factors, List<int> Rated, DcOpfSecuritySimplex.Problem Problem)
    {
        /// <summary>N109: every rated line, the set the overload report runs over.</summary>
        public List<int> AllRated { get; init; } = Rated;

        /// <summary>N109: the rated lines left out of the LP (no dispatch can bring them within their rating).</summary>
        public IReadOnlyList<UnrelievableLine> Unrelievable { get; init; } = Array.Empty<UnrelievableLine>();
    }

    /// <summary>
    /// N109 (Ruling R1, Mark delegated 2026-10-04): split the rated lines into those the security LP keeps and those no
    /// dispatch can relieve. For rated line r (model branch rated[r]) the DC flow at a dispatch is
    /// f0 + sum_i a(r, i) Pg_i, with a(r, i) the PTDF sensitivity of the line to generator i's bus. Over every unit
    /// anywhere in [Pmin_i, Pmax_i] (the balance row dropped, so a superset of the LP's feasible set) its range is
    /// [f0 + sum_i min(a Pmin_i, a Pmax_i), f0 + sum_i max(a Pmin_i, a Pmax_i)]. A line whose whole range lies above
    /// +RateA + <see cref="UnrelievableMarginMw"/> or below -RateA - <see cref="UnrelievableMarginMw"/> cannot be
    /// brought within its rating by any dispatch: it is left out of the LP and reported. A line no unit moves at all
    /// (every a exactly zero) has a single-point range and falls under the same rule (the redispatch's own rule for
    /// such lines). Every sum runs over the generators in model order, so the split is deterministic.
    /// </summary>
    /// <param name="model">The (connected or single-island) model.</param>
    /// <param name="rated">The rated lines (model branch indices, branch order).</param>
    /// <param name="sensitivity">a(r, i): the PTDF of rated line r (its position in <paramref name="rated"/>) to
    /// generator i's bus.</param>
    /// <param name="f0">Each branch's DC flow from the fixed loads alone (indexed by model branch).</param>
    /// <param name="pmin">Each generator's Pmin as the LP bounds it.</param>
    /// <param name="pmax">Each generator's Pmax as the LP bounds it.</param>
    /// <param name="unrelievable">Receives the lines left out, in branch order.</param>
    /// <returns>The positions (into <paramref name="rated"/>) of the lines the LP keeps, ascending.</returns>
    internal static List<int> ScreenUnrelievable(GridModel model, List<int> rated, Func<int, int, double> sensitivity,
        double[] f0, double[] pmin, double[] pmax, out List<UnrelievableLine> unrelievable)
    {
        int g = model.Generators.Count;
        var keep = new List<int>(rated.Count);
        unrelievable = new List<UnrelievableLine>();
        for (int r = 0; r < rated.Count; r++)
        {
            int l = rated[r];
            double low = 0.0, high = 0.0;
            for (int i = 0; i < g; i++)
            {
                double a = sensitivity(r, i);
                if (a > 0.0) { low += a * pmin[i]; high += a * pmax[i]; }
                else if (a < 0.0) { low += a * pmax[i]; high += a * pmin[i]; }
            }
            double flowMin = f0[l] + low, flowMax = f0[l] + high;
            double rate = model.Branches[l].RateAMva;
            if (flowMin > rate + UnrelievableMarginMw || flowMax < -rate - UnrelievableMarginMw)
                unrelievable.Add(new UnrelievableLine(l, model.Branches[l].Name ?? "", rate, flowMin, flowMax));
            else
                keep.Add(r);
        }
        return keep;
    }

    /// <summary>
    /// Build the security-constrained economic dispatch LP (R005) in the structured form solved by
    /// <see cref="DcOpfSecuritySimplex"/>. It is, entry for entry, the LP the dense reference
    /// (<see cref="SecuredDispatchDense"/>) builds:
    ///   variables  g dispatches, then one slack per rated line (rated = RateA &gt; 0, in branch order);
    ///   balance    sum_i Pg_i = demand;
    ///   thermal    sum_i PTDF[l, bus_i] Pg_i + s_l = RateA_l - f0_l,   s_l in [0, 2 RateA_l],
    /// with f0 each line's flow from the fixed loads (so the flow RateA_l - s_l ranges over [-RateA_l, +RateA_l]).
    /// The PTDF is computed once per DISTINCT generator bus (the dense reference solved the same column once per
    /// generator; the solve is deterministic, so the values are identical) and kept only for the rated lines,
    /// row-major: rated lines x distinct generator buses (about 73 MB on gb-full instead of about 250 MB).
    /// </summary>
    internal static SecurityLp BuildSecurityLp(GridModel model, double[] cost, double[] pmin, double[] pmax)
    {
        DistributionFactors df = DistributionFactors.Build(model);
        int g = model.Generators.Count;
        var rated = new List<int>();
        for (int l = 0; l < model.Branches.Count; l++) if (model.Branches[l].RateAMva > 0) rated.Add(l);
        int lines = rated.Count;

        // Distinct generator buses, numbered in order of first appearance.
        var colOfBus = new Dictionary<int, int>();
        var genCol = new int[g];
        var distinctBuses = new List<int>();
        for (int i = 0; i < g; i++)
        {
            int bus = model.Generators[i].Bus;
            if (!colOfBus.TryGetValue(bus, out int u)) { u = distinctBuses.Count; colOfBus[bus] = u; distinctBuses.Add(bus); }
            genCol[i] = u;
        }
        int nu = distinctBuses.Count;
        long cells = (long)lines * nu;
        if (cells > Array.MaxLength)
            throw new InvalidOperationException($"DC-OPF: the security LP's PTDF block ({lines} rated lines x {nu} generator buses) is too large to hold.");
        var ptdf = new double[cells];
        for (int u = 0; u < nu; u++)
        {
            double[] column = df.PtdfColumn(distinctBuses[u]);
            for (int r = 0; r < lines; r++) ptdf[(long)r * nu + u] = column[rated[r]];
        }

        double[] f0 = FlowsFrom(model, df, new double[g]);

        // N109: leave out the rated lines no dispatch can relieve, and report them. When none is left out (every LP
        // that was feasible before N109, see UnrelievableMarginMw) the arrays below are the very arrays built above,
        // untouched, so the LP is exactly the one solved before.
        List<int> allRated = rated;
        List<int> keep = ScreenUnrelievable(model, rated, (r, i) => ptdf[(long)r * nu + genCol[i]], f0, pmin, pmax,
            out List<UnrelievableLine> unrelievable);
        if (unrelievable.Count > 0)
        {
            var keptRated = new List<int>(keep.Count);
            var keptPtdf = new double[(long)keep.Count * nu];
            for (int k = 0; k < keep.Count; k++)
            {
                keptRated.Add(rated[keep[k]]);
                Array.Copy(ptdf, (long)keep[k] * nu, keptPtdf, (long)k * nu, nu);
            }
            rated = keptRated;
            ptdf = keptPtdf;
            lines = rated.Count;
        }

        int n = g + lines;
        var b = new double[1 + lines];
        var c = new double[n];
        var lo = new double[n];
        var hi = new double[n];
        for (int i = 0; i < g; i++) { c[i] = cost[i]; lo[i] = pmin[i]; hi[i] = pmax[i]; }
        b[0] = model.Buses.Sum(bus => bus.Pd);
        for (int r = 0; r < lines; r++)
        {
            int l = rated[r];
            double rate = model.Branches[l].RateAMva;
            int sVar = g + r;
            c[sVar] = 0.0; lo[sVar] = 0.0; hi[sVar] = 2.0 * rate;
            b[1 + r] = rate - f0[l];
        }
        return new SecurityLp(df, rated, WithTieBreak(new DcOpfSecuritySimplex.Problem(g, lines, nu, genCol, ptdf, b, c, lo, hi)))
        {
            AllRated = allRated,
            Unrelievable = unrelievable,
        };
    }

    /// <summary>Solve the security-constrained economic dispatch LP: cheapest generation meeting demand with every
    /// rated line inside its thermal limit. On an infeasible security set (no dispatch keeps all lines within limit)
    /// it reports the unconstrained merit-order dispatch as not-feasible with the overloaded lines listed, rather
    /// than a fabricated point. R005: the LP is solved by <see cref="DcOpfSecuritySimplex"/>, which performs the
    /// dense <see cref="BoundedSimplex"/>'s arithmetic without its dense matrices, so the answer is the dense
    /// reference's (<see cref="SecuredDispatchDense"/>) bit for bit and gb-full fits in memory.
    /// F19: it also hands back the optimal basis's duals and the factors (for the prices). The LP run is the same one
    /// whether or not prices are wanted (<see cref="DcOpfSecuritySimplex.SolveDetailed"/> performs exactly the
    /// arithmetic of <see cref="DcOpfSecuritySimplex.Solve"/>), so the dispatch is the same bits. (N109: the plain
    /// wrapper that dropped the duals was folded into this method, so the left-out lines can be read from the LP.)</summary>
    private static ((double[] Pg, bool Feasible, bool Constrained, List<LineBinding> Binding, LpStatus Status) Outcome,
        double[]? Duals, SecurityLp Lp) SecuredDispatchWithDuals(GridModel model, double[] cost, double[] pmin, double[] pmax, double[] meritPg)
    {
        SecurityLp lp = BuildSecurityLp(model, cost, pmin, pmax);
        DcOpfSecuritySimplex.Detailed d = DcOpfSecuritySimplex.SolveDetailed(lp.Problem);
        // N109: the overload report of a non-optimal LP runs over EVERY rated line (lp.AllRated), as before N109.
        return (InterpretSecurityLp(model, lp.Factors, lp.AllRated, d.Status, d.X, meritPg), d.Duals, lp);
    }

    /// <summary>The ORIGINAL dense security-constrained dispatch, kept unchanged as the test reference (R005): a
    /// dense (1 + L) x (g + L) constraint matrix and one full PTDF column per generator, solved by the dense
    /// <see cref="BoundedSimplex"/>.</summary>
    private static (double[] Pg, bool Feasible, bool Constrained, List<LineBinding> Binding, LpStatus Status,
        IReadOnlyList<UnrelievableLine> Unrelievable) SecuredDispatchDense(
        GridModel model, double[] cost, double[] pmin, double[] pmax, double[] meritPg)
    {
        DistributionFactors df = DistributionFactors.Build(model);
        int g = model.Generators.Count;
        // Rated lines (index into model.Branches) and the LP variable layout: g dispatches then one slack
        // per rated line. f0 = each line's flow from the fixed loads (Pg = 0); a[l,i] = PTDF sensitivity
        // of line l to generator i's bus injection.
        var genBus = model.Generators.Select(x => x.Bus).ToArray();
        var ptdf = genBus.Select(b => df.PtdfColumn(b)).ToArray();
        double[] f0 = FlowsFrom(model, df, new double[g]);
        var allRated = new List<int>();
        for (int l = 0; l < model.Branches.Count; l++) if (model.Branches[l].RateAMva > 0) allRated.Add(l);
        // N109: the same screen as the structured path (same sensitivities, same summation order), so the dense
        // reference leaves out exactly the same lines; with none left out the list is the same lines as before.
        List<int> keep = ScreenUnrelievable(model, allRated, (r, i) => ptdf[i][allRated[r]], f0, pmin, pmax,
            out List<UnrelievableLine> unrelievable);
        List<int> rated = unrelievable.Count == 0 ? allRated : keep.Select(k => allRated[k]).ToList();
        int L = rated.Count;

        int n = g + L;
        int m = 1 + L;
        var A = new double[m, n];
        var b = new double[m];
        var c = new double[n];
        var lo = new double[n];
        var hi = new double[n];

        // Balance row and generator bounds/costs.
        for (int i = 0; i < g; i++) { A[0, i] = 1.0; c[i] = cost[i]; lo[i] = pmin[i]; hi[i] = pmax[i]; }
        b[0] = model.Buses.Sum(bus => bus.Pd);

        // Thermal rows:  sum_i a[l,i] Pg_i + s_l = RateA_l - f0_l,   s_l in [0, 2 RateA_l]
        // so the line flow RateA_l - s_l ranges over [-RateA_l, +RateA_l].
        for (int r = 0; r < L; r++)
        {
            int l = rated[r];
            double rate = model.Branches[l].RateAMva;
            for (int i = 0; i < g; i++) A[1 + r, i] = ptdf[i][l];
            int sVar = g + r;
            A[1 + r, sVar] = 1.0;
            c[sVar] = 0.0; lo[sVar] = 0.0; hi[sVar] = 2.0 * rate;
            b[1 + r] = rate - f0[l];
        }

        // N240: the dense reference runs the same tie-break as the structured solver, so the two stay bit-identical.
        (LpStatus status, double[] x, double _) = BoundedSimplex.Solve(A, b, c, lo, hi, pricingTieMargin: BoundedSimplex.DcOpfPricingTieMargin);
        var outcome = InterpretSecurityLp(model, df, allRated, status, x, meritPg);
        return (outcome.Pg, outcome.Feasible, outcome.Constrained, outcome.Binding, outcome.Status, unrelievable);
    }

    /// <summary>
    /// N240 (October 2026, ruled by main: a fixed, documented tie-break so a degenerate optimum does not depend on
    /// summation order): every LP the DC-OPF solves (the security LP of the fuel-type path, the balance-only LP behind its
    /// prices, and the cost-curve LP of DcOpfCosted) runs with the simplex's lowest-index tie-break on: reduced costs
    /// within <see cref="BoundedSimplex.DcOpfPricingTieMargin"/> (relative) of the best count as tied, and the lowest
    /// column index enters. A DC-OPF whose costs are all equal (every MATPOWER case on the fuel-type merit order) has
    /// a whole face of optimal dispatches; which vertex the simplex stops at used to be decided, between mathematically
    /// tied candidates, by the last bit of a PTDF sum, so pglib case588 moved 44.06 MW between two units when the CSR
    /// summation order changed (D-DOC-026). An LP whose pivot path meets no such near-tie runs exactly as before
    /// (<see cref="DcOpfSecuritySimplex.Diagnostics.TieBreaks"/> = 0 proves it), so its result is the same bits.
    /// </summary>
    internal static DcOpfSecuritySimplex.Problem WithTieBreak(DcOpfSecuritySimplex.Problem p) => p with
    {
        PricingTieMargin = BoundedSimplex.DcOpfPricingTieMargin,
    };

    /// <summary>Turn the security LP's outcome into the dispatch result. Shared by the structured solver and the
    /// dense reference, so the reporting is identical by construction.</summary>
    private static (double[] Pg, bool Feasible, bool Constrained, List<LineBinding> Binding, LpStatus Status) InterpretSecurityLp(
        GridModel model, DistributionFactors df, List<int> rated, LpStatus status, double[] x, double[] meritPg)
    {
        int g = model.Generators.Count;
        if (status != LpStatus.Optimal)
        {
            // Security cannot be met (or a numerical stall): report the merit-order dispatch, mark the
            // overloaded lines, and flag not-feasible so callers see the honest state, not a fiction.
            double[] flowM = FlowsFrom(model, df, meritPg);
            var over = new List<LineBinding>();
            foreach (int l in rated)
                if (Math.Abs(flowM[l]) >= model.Branches[l].RateAMva - BindingToleranceMw)
                    over.Add(new LineBinding(l, model.Branches[l].Name ?? "", flowM[l], model.Branches[l].RateAMva));
            // R024: only an Optimal LP is a secure dispatch. An iteration-limit stall or an unbounded LP used to be
            // reported Feasible (the merit-order dispatch with overloaded lines, labelled secure).
            return (meritPg, false, true, over, status);
        }

        var pg = new double[g];
        Array.Copy(x, pg, g);
        double[] flow = FlowsFrom(model, df, pg);
        var binding = new List<LineBinding>();
        for (int l = 0; l < flow.Length; l++)
            if (model.Branches[l].RateAMva > 0 && Math.Abs(flow[l]) >= model.Branches[l].RateAMva - BindingToleranceMw)
                binding.Add(new LineBinding(l, model.Branches[l].Name ?? "", flow[l], model.Branches[l].RateAMva));
        return (pg, true, binding.Count > 0, binding, LpStatus.Optimal);
    }

    /// <summary>F19: the rated lines at (or over) their limit for the given flows, in branch order: the rule
    /// <see cref="InterpretSecurityLp"/> applies to an optimal dispatch.</summary>
    internal static List<LineBinding> BindingFrom(GridModel model, double[] flow)
    {
        var binding = new List<LineBinding>();
        for (int l = 0; l < flow.Length; l++)
            if (model.Branches[l].RateAMva > 0 && Math.Abs(flow[l]) >= model.Branches[l].RateAMva - BindingToleranceMw)
                binding.Add(new LineBinding(l, model.Branches[l].Name ?? "", flow[l], model.Branches[l].RateAMva));
        return binding;
    }

    /// <summary>
    /// F19: the prices from an optimum's duals. <paramref name="balanceDual"/> is y0 (the balance row) and
    /// <paramref name="lineDuals"/>[r] the dual of the thermal row of <paramref name="rated"/>[r], both as the change in
    /// optimal cost per unit increase of the row's right-hand side (see <see cref="DcOpfCosted"/>). LMP at bus k =
    /// y0 + sum_r y_r PTDF[r, k] (one PTDF-transpose solve); a line's shadow price is -y_r when its flow is at +RateA
    /// and +y_r at -RateA. Lines are listed when they bind or carry a non-zero dual (a non-zero dual implies the line
    /// is at its limit, by complementary slackness, so in practice the two sets agree).
    /// </summary>
    internal static DcOpfPrices PricesFrom(GridModel model, DistributionFactors df, List<int> rated, double balanceDual,
        double[] lineDuals, double[] flow, List<LineBinding> binding)
    {
        var weights = new Dictionary<int, double>();
        for (int r = 0; r < rated.Count; r++) if (lineDuals[r] != 0.0) weights[rated[r]] = lineDuals[r];
        double[] congestion = df.PtdfTransposeProduct(weights);
        var buses = new List<BusPrice>(model.Buses.Count);
        for (int k = 0; k < model.Buses.Count; k++)
        {
            // + 0.0 turns a negative zero (from a zero right-hand side through the solve) into +0, so an uncongested
            // bus prints 0, not -0; any non-zero value is unchanged by it.
            double cong = congestion[k] + 0.0;
            buses.Add(new BusPrice(model.Buses[k].Id, balanceDual + cong, balanceDual, cong));
        }

        var dualOf = new Dictionary<int, double>();
        for (int r = 0; r < rated.Count; r++) dualOf[rated[r]] = lineDuals[r];
        var bindingSet = new HashSet<int>(binding.Select(b => b.Branch));
        var lines = new List<LinePrice>();
        foreach (int l in rated)
        {
            double y = dualOf[l];
            if (y == 0.0 && !bindingSet.Contains(l)) continue;
            double shadow = flow[l] >= 0.0 ? -y : y;
            lines.Add(new LinePrice(l, model.Branches[l].Name ?? "", flow[l], model.Branches[l].RateAMva, shadow));
        }
        return new DcOpfPrices(buses, lines);
    }

    internal static double[] FlowsFrom(GridModel model, DistributionFactors df, double[] pg)
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

    /// <summary>Indicative marginal cost (£/MWh) by fuel type - a plausible GB merit order. An unknown/
    /// empty fuel (fuel-less IEEE cases) takes a mid-merit default, so a dispatch is still well-defined.
    /// <para>R305 rest and N020 (October 2026): the fuel is classified by Core's one taxonomy,
    /// <see cref="Model.FuelTypes"/> (economics reader), not by this method's own substring list, and the cost is then
    /// looked up by kind with the same values as before. What moved, exactly: the types "onshore" and "offshore" (used
    /// for wind in data/uk and data/gb-synthetic) cost 1 instead of the 50 default (the old list knew wind only as
    /// "wind"); "Pumped storage hydro" 30 instead of 5 and "hydrogen" 30 instead of 5 (both used to hit "hydro");
    /// "biogas" and landfill gas 55 instead of 65 (they hit "gas"); "waste", "anaerobic" 55 and "cogen", "combined cycle",
    /// "open cycle", "peaker", "photovoltaic", "marine" their own kinds' costs instead of the default; "gas engine" /
    /// "Gas Reciprocating" 65 instead of 120 (gas plant, as the importer and the shells read them); "gas turbine" 95
    /// instead of 65 (OCGT); "Charcoal" and "Boiler" the default instead of coal / oil (substring false positives).</para>
    /// </summary>
    public static double FuelCostPerMwh(string? type) => Model.FuelTypes.Classify(type, Model.FuelReader.Economics) switch
    {
        Model.FuelKind.WindOnshore or Model.FuelKind.WindOffshore or Model.FuelKind.Solar or Model.FuelKind.Tidal => 1.0,
        Model.FuelKind.Hydro => 5.0,
        Model.FuelKind.Nuclear => 10.0,
        // Storage of every kind: batteries, pumped storage and hydrogen (power-to-gas-to-power).
        Model.FuelKind.Storage or Model.FuelKind.PumpedStorage or Model.FuelKind.Hydrogen => 30.0,
        Model.FuelKind.Coal => 45.0,
        Model.FuelKind.Biomass => 55.0,
        Model.FuelKind.Interconnector or Model.FuelKind.GridInfeed => 60.0,
        Model.FuelKind.Ocgt => 95.0,
        Model.FuelKind.Recip => 120.0,
        Model.FuelKind.Ccgt or Model.FuelKind.Chp or Model.FuelKind.Gas => 65.0,
        Model.FuelKind.Other => DefaultCostPerMwh,
        var k => throw new ArgumentOutOfRangeException(nameof(type), k, "Unknown fuel kind."),
    };
}
