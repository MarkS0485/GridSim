using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>The result of <see cref="AcOptimalPowerFlow.Solve"/>: a DC-OPF dispatch with an AC loss loop (not a full
/// AC optimal power flow; see the class summary).</summary>
/// <param name="Converged">True when the AC power flow at the final dispatch solved and losses converged.</param>
/// <param name="Iterations">Outer loss-correction iterations.</param>
/// <param name="Dispatch">The economic generator dispatch (from the security-constrained LP).</param>
/// <param name="TotalLossMw">The AC network losses at the final dispatch.</param>
/// <param name="TotalCostPerHour">Total generation cost (sum of Pg * cost).</param>
public sealed record AcOpfResult(
    bool Converged, int Iterations, IReadOnlyList<DcOptimalPowerFlow.GenDispatch> Dispatch, double TotalLossMw, double TotalCostPerHour)
{
    /// <summary>N121 (October 2026): the nodal prices (LMPs) and line shadow prices of the FINAL DC-OPF pass, when asked
    /// for (<c>computePrices</c>); null otherwise. They are the lossless DC prices at the loss-inflated demand that pass
    /// dispatched (energy plus congestion, <see cref="DcOptimalPowerFlow.BusPrice"/>): the loss loop only scales demand,
    /// so there is no marginal-loss component, and nothing about voltage or reactive power enters them.</summary>
    public DcOptimalPowerFlow.DcOpfPrices? Prices { get; init; }

    /// <summary>N111 (October 2026): why the run is not <see cref="Converged"/>, so a caller can say which of the three
    /// causes stopped it instead of one message for all (it used to print "the loss iteration did not converge" even
    /// when the AC power flow of pass 0 had failed, or when the loop settled on an infeasible DC dispatch).
    /// <see cref="AcOpfFailure.None"/> when converged. Information only: no number of the run depends on it.</summary>
    public AcOpfFailure Failure { get; init; }

    /// <summary>N111: the failed AC solve when <see cref="Failure"/> is <see cref="AcOpfFailure.AcPowerFlowNotConverged"/>;
    /// null otherwise.</summary>
    public AcOpfAcSolveFailure? AcFailure { get; init; }

    /// <summary>N173 (October 2026): true when the loop stopped at pass 0, before any AC solve, because that pass's DC-OPF
    /// dispatch was infeasible in a way no later pass can repair (see <see cref="AcOptimalPowerFlow.Solve"/>); the
    /// <see cref="Failure"/> is then <see cref="AcOpfFailure.DcDispatchInfeasible"/>, <see cref="AcOpfResult.Iterations"/>
    /// is 0 (no pass completed), the losses and the cost are NaN (no AC solve measured them) and
    /// <see cref="AcOpfResult.Dispatch"/> is pass 0's infeasible dispatch. False otherwise.</summary>
    public bool StoppedAtInfeasibleFirstPass { get; init; }

    /// <summary>N109 (Ruling R1, Mark delegated 2026-10-04): the rated lines the LAST DC-OPF pass left out because no
    /// dispatch can relieve them (<see cref="DcOptimalPowerFlow.DcOpfResult.UnrelievableLines"/>); they stay over their
    /// rating at <see cref="Dispatch"/>, so a caller must report them. Empty when there are none.</summary>
    public IReadOnlyList<DcOptimalPowerFlow.UnrelievableLine> UnrelievableLines { get; init; } = Array.Empty<DcOptimalPowerFlow.UnrelievableLine>();
}

/// <summary>N111 (October 2026): the cause of a non-converged <see cref="AcOptimalPowerFlow.Solve"/>.</summary>
public enum AcOpfFailure
{
    /// <summary>Converged: no failure.</summary>
    None,
    /// <summary>The AC power flow at one pass's DC-OPF dispatch did not converge (<see cref="AcOpfResult.AcFailure"/>).</summary>
    AcPowerFlowNotConverged,
    /// <summary>The losses settled, but the final pass's DC-OPF dispatch is infeasible (no dispatch meets demand within
    /// the units' limits and, when rated, the line limits).</summary>
    DcDispatchInfeasible,
    /// <summary>The losses did not settle within the iteration cap.</summary>
    IterationCap,
}

/// <summary>N111 (October 2026): the AC power flow that failed inside the loss loop: the 0-based loss pass, the
/// solver's status, its iteration count, its largest mismatch (pu) and the lowest voltage it reached (pu; the state it
/// stopped in, not a solution).</summary>
public sealed record AcOpfAcSolveFailure(int Pass, PowerFlowStatus Status, int Iterations, double MaxMismatchPu, double MinVmPu);

/// <summary>
/// A DC-OPF dispatch with an AC loss loop - <b>not</b> a full AC optimal power flow (F15, October 2026: the name
/// promises more than the code does, so the CLI header, help and docs now say so). The DC-OPF
/// (<see cref="DcOptimalPowerFlow"/>) gives a security-constrained economic dispatch but ignores network
/// losses; this wraps it in an outer loop that inflates the demand by the AC losses, re-dispatches, solves the
/// AC power flow, and repeats until the losses converge - so the returned dispatch pays for its own losses and
/// the AC power flow at it converges. Bus voltages, reactive power, generator reactive limits and AC branch
/// loading are neither optimised nor constrained: the AC solve only measures the losses. It reuses the merit-order
/// LP + PTDF security of the DC-OPF and Newton-Raphson for the loss measurement. A real AC-OPF with voltage and
/// reactive limits is post-1.0 work.
/// N173 (October 2026): when pass 0's DC dispatch is infeasible in a way the loss loop provably cannot repair (the loop
/// only raises demand), the run stops there with <see cref="AcOpfFailure.DcDispatchInfeasible"/> and
/// <see cref="AcOpfResult.StoppedAtInfeasibleFirstPass"/>; see the private StaysInfeasible for the proof and its limits.
/// </summary>
public static class AcOptimalPowerFlow
{
    /// <summary>Run the DC dispatch with its AC loss loop. <paramref name="costPerGen"/> aligns to <c>model.Generators</c>
    /// (null uses the fuel-type merit order). <paramref name="useGeneratorCosts"/> (F19, October 2026; default off, so
    /// every existing call is unchanged) dispatches each pass on the units' own cost curves
    /// (<see cref="DcOptimalPowerFlow.DcOpfOptions.UseGeneratorCosts"/>) instead, and the reported cost is then the sum
    /// of the curves at the final dispatch; it cannot be combined with <paramref name="costPerGen"/>.
    /// <paramref name="computePrices"/> (N121, October 2026; default off) also returns the final DC pass's nodal and line
    /// prices (<see cref="AcOpfResult.Prices"/>); the dispatch is exactly what it is without them (the DC-OPF's own
    /// guarantee, F19). It needs the fuel-type costs or the cost curves, not a cost vector.</summary>
    public static AcOpfResult Solve(GridModel model, double[]? costPerGen = null, double lossToleranceMw = 1e-3, int maxIterations = 20,
        bool useGeneratorCosts = false, bool computePrices = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        // GR-481: at least one loss iteration (0 or fewer ran none and then read a null DC result).
        if (maxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "ACOPF needs at least one loss iteration.");
        if (!(lossToleranceMw > 0) || !double.IsFinite(lossToleranceMw))
            throw new ArgumentOutOfRangeException(nameof(lossToleranceMw), lossToleranceMw, "The ACOPF loss tolerance must be a finite number of MW above 0.");
        if (useGeneratorCosts && costPerGen is not null)
            throw new ArgumentException("ACOPF: give either a cost vector or useGeneratorCosts, not both.", nameof(costPerGen));
        if (computePrices && costPerGen is not null)
            throw new ArgumentException("ACOPF: prices come from the fuel-type costs or the cost curves; a cost vector has no price path.", nameof(costPerGen));
        // N121: the options of each DC pass. Without prices these are exactly the calls the loop always made.
        DcOptimalPowerFlow.DcOpfOptions? passOptions = useGeneratorCosts
            ? (computePrices ? CurveOptions with { ComputePrices = true } : CurveOptions)
            : (computePrices ? new DcOptimalPowerFlow.DcOpfOptions { ComputePrices = true } : null);

        // N070 (October 2026): a network of several DC islands is dispatched island by island by the DC-OPF (N034), so
        // each island's demand must be inflated by ITS OWN AC losses. One system-wide ratio moved loss cover between
        // islands that no power can cross: the slack of one island absorbed the shortfall of another's (measured on two
        // case14 islands, one at 1.3x load: 2.2961060308310266 MW over-dispatched in one, 2.2964030498212082 MW short in
        // the other, whose slack then ran 2.30 MW above its Pmax). A connected network keeps the exact code below.
        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(model);
        if (islands.Count > 1) return SolvePerIsland(model, islands, costPerGen, lossToleranceMw, maxIterations, passOptions);

        double totalPd = model.Buses.Sum(b => b.Pd);
        IPowerFlowSolver solver = SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)

        double losses = 0;
        DcOptimalPowerFlow.DcOpfResult dc = null!;
        PowerFlowResult ac = null!;
        int iter = 0;
        bool settled = false;
        for (; iter < maxIterations; iter++)
        {
            double scale = totalPd > 0 ? (totalPd + losses) / totalPd : 1.0;
            dc = passOptions is not null
                ? DcOptimalPowerFlow.Solve(ContinuationPowerFlow.ScaleLoads(model, scale), passOptions)
                : DcOptimalPowerFlow.Solve(ContinuationPowerFlow.ScaleLoads(model, scale), costPerGen);

            // N173: an infeasible pass 0 that no later pass can repair ends the run here (see StaysInfeasible).
            if (iter == 0 && StaysInfeasible(model, dc, islands))
                return StoppedAtPass0(dc);

            ac = solver.Solve(ApplyDispatch(model, dc.Dispatch));
            if (!ac.Converged)
                return new AcOpfResult(false, iter, dc.Dispatch, double.NaN, double.NaN)
                    { Failure = AcOpfFailure.AcPowerFlowNotConverged, AcFailure = Describe(iter, ac), UnrelievableLines = dc.UnrelievableLines };

            double newLosses = 0;
            foreach (double p in ac.PInjMw) newLosses += p; // sum of net injections = losses
            settled = Math.Abs(newLosses - losses) < lossToleranceMw;
            losses = newLosses;
            if (settled) { iter++; break; }
        }

        // F19: with cost curves the cost is the curves' value (a marginal cost times output would not be).
        double cost = useGeneratorCosts ? dc.TotalCost : dc.Dispatch.Sum(d => d.Pg * d.CostPerMwh);
        // R223: converged only when the loss iteration settled AND the final DC-OPF dispatch is feasible; it used to
        // return true for an unsettled loop and never read dc.Feasible.
        return new AcOpfResult(settled && dc.Feasible, iter, dc.Dispatch, losses, cost)
            { Prices = computePrices ? dc.Prices : null, Failure = Cause(settled, dc.Feasible), UnrelievableLines = dc.UnrelievableLines };
    }

    /// <summary>
    /// The loss loop of <see cref="Solve"/> for a network of several DC islands (N070). Each island k keeps its own loss
    /// estimate L_k and its demand is scaled by (D_k + L_k) / D_k (D_k = the island's demand, <see cref="DcIsland.LoadMw"/>;
    /// an island without demand is not scaled), exactly as the connected loop scales the whole network. L_k is the sum,
    /// in result order, of the AC net injections over the island's buses (generation minus demand inside the island =
    /// the island's losses, since nothing crosses between islands). The loop has settled when every island's estimate
    /// moved by less than <paramref name="lossToleranceMw"/>. The reported loss is the sum of every net injection, the
    /// connected loop's definition. Fail loud as the connected loop: a non-converged AC solve returns Converged false.
    /// </summary>
    private static AcOpfResult SolvePerIsland(GridModel model, IReadOnlyList<DcIsland> islands, double[]? costPerGen,
        double lossToleranceMw, int maxIterations, DcOptimalPowerFlow.DcOpfOptions? passOptions)
    {
        bool useGeneratorCosts = passOptions is { UseGeneratorCosts: true };
        bool computePrices = passOptions is { ComputePrices: true };
        IPowerFlowSolver solver = SolverSelector.CreateNewton(model);

        // Island of every bus id (first occurrence, the rule the solvers key buses by).
        var islandOf = new Dictionary<int, int>(model.Buses.Count);
        for (int k = 0; k < islands.Count; k++)
            foreach (int id in islands[k].BusIds) islandOf.TryAdd(id, k);

        var losses = new double[islands.Count];
        double totalLoss = 0;
        DcOptimalPowerFlow.DcOpfResult dc = null!;
        int iter = 0;
        bool settled = false;
        for (; iter < maxIterations; iter++)
        {
            var scale = new double[islands.Count];
            for (int k = 0; k < islands.Count; k++)
            {
                double d = islands[k].LoadMw;
                scale[k] = d > 0 ? (d + losses[k]) / d : 1.0;
            }
            var buses = new List<Bus>(model.Buses.Count);
            foreach (Bus b in model.Buses)
            {
                double s = scale[islandOf[b.Id]];
                buses.Add(b with { Pd = b.Pd * s, Qd = b.Qd * s });
            }
            dc = passOptions is not null
                ? DcOptimalPowerFlow.Solve(model.CopyWith(buses: buses), passOptions)
                : DcOptimalPowerFlow.Solve(model.CopyWith(buses: buses), costPerGen);

            // N173: an infeasible pass 0 that no later pass can repair ends the run here (see StaysInfeasible).
            if (iter == 0 && StaysInfeasible(model, dc, islands))
                return StoppedAtPass0(dc);

            PowerFlowResult ac = solver.Solve(ApplyDispatch(model, dc.Dispatch));
            if (!ac.Converged)
                return new AcOpfResult(false, iter, dc.Dispatch, double.NaN, double.NaN)
                    { Failure = AcOpfFailure.AcPowerFlowNotConverged, AcFailure = Describe(iter, ac), UnrelievableLines = dc.UnrelievableLines };

            var newLosses = new double[islands.Count];
            totalLoss = 0;
            for (int i = 0; i < ac.BusIds.Count; i++)
            {
                newLosses[islandOf[ac.BusIds[i]]] += ac.PInjMw[i];
                totalLoss += ac.PInjMw[i];
            }
            settled = true;
            for (int k = 0; k < islands.Count; k++)
                if (!(Math.Abs(newLosses[k] - losses[k]) < lossToleranceMw)) settled = false;
            losses = newLosses;
            if (settled) { iter++; break; }
        }

        // F19: with cost curves the cost is the curves' value (a marginal cost times output would not be).
        double cost = useGeneratorCosts ? dc.TotalCost : dc.Dispatch.Sum(d => d.Pg * d.CostPerMwh);
        return new AcOpfResult(settled && dc.Feasible, iter, dc.Dispatch, totalLoss, cost)
            { Prices = computePrices ? dc.Prices : null, Failure = Cause(settled, dc.Feasible), UnrelievableLines = dc.UnrelievableLines };
    }

    /// <summary>N111: the cause of a run that ended without an AC failure (None when it converged). The loss loop is
    /// judged first: an unsettled loop is the cap, whatever the last DC pass said.</summary>
    private static AcOpfFailure Cause(bool settled, bool dcFeasible)
        => !settled ? AcOpfFailure.IterationCap : !dcFeasible ? AcOpfFailure.DcDispatchInfeasible : AcOpfFailure.None;

    /// <summary>N173: the result of a run that stopped at pass 0 on a DC dispatch no later pass can make feasible. Shaped
    /// like the AC-failure return (no pass completed, losses and cost not measured), with the DC verdict.</summary>
    private static AcOpfResult StoppedAtPass0(DcOptimalPowerFlow.DcOpfResult dc)
        => new(false, 0, dc.Dispatch, double.NaN, double.NaN)
            { Failure = AcOpfFailure.DcDispatchInfeasible, StoppedAtInfeasibleFirstPass = true, UnrelievableLines = dc.UnrelievableLines };

    /// <summary>
    /// N173 (October 2026): is pass 0's DC dispatch infeasible in a way that EVERY later pass inherits, so that the run
    /// cannot end converged whatever the AC solves do? Before N173 the loop went on regardless: on gb-full that meant a
    /// second 2-minute DC-OPF and AC solves of an insecure dispatch, 3 min 37 s in all, for a run that could never
    /// converge (R223: converged needs the final DC pass feasible). This answers true only where it is PROVED; anything
    /// else returns false and the loop runs exactly as before.
    /// </summary>
    /// <remarks>
    /// <para><b>Why later passes scale demand up.</b> Pass k dispatches the island's demand D times
    /// s_k = (D + L) / D, with L the island's loss estimate from the previous AC solve (s_0 = 1: no losses yet). L is
    /// the sum of the AC net injections, which is the network's own real-power absorption: the series branches
    /// (Re of 1 / (R + jX), times |current| squared) plus the bus shunt conductances (Gs |V|^2); the tap ratio, phase
    /// shift and line charging are lossless. So when every branch has R &gt;= 0 and every bus Gs &gt;= 0, L &gt;= 0 and every
    /// later pass has s &gt;= 1 (checked here; otherwise false). An island with no positive demand is never scaled: every
    /// pass gives it the identical DC problem, so its verdict cannot change.</para>
    /// <para><b>Which infeasibilities stay infeasible for s &gt;= 1.</b>
    /// (a) A dead island (no unit) with demand: |s D| &gt;= |D| stays unserved.
    /// (b) Capability: D above the island's total Pmax (plus the DC-OPF's balance tolerance): s D &gt;= D stays above it.
    /// (c) The security LP proved Infeasible (line limits): the DC feasibility conditions (units within Pmin..Pmax,
    /// generation equals s D, every rated line's PTDF flow of (generation - s d) within its rating) are linear in the
    /// unit outputs AND s together, so the set of feasible s is an interval. When every unit has Pmin &lt;= 0 &lt;= Pmax, s = 0
    /// is feasible (no output, no flow), so the interval is [0, s_max]; s = 1 infeasible then means s_max &lt; 1, and
    /// every s &gt;= 1 is infeasible too.
    /// Not proved, so never stopped on: demand below the units' total Pmin (more demand can cure that), an LP that
    /// stalled or was unbounded (a numerical outcome, not a proof), and (c) with any unit whose Pmin is above 0.</para>
    /// <para>A multi-island network stops when any one island is proved to stay infeasible: the run's verdict needs every
    /// island feasible.</para>
    /// </remarks>
    private static bool StaysInfeasible(GridModel model, DcOptimalPowerFlow.DcOpfResult dc, IReadOnlyList<DcIsland> islands)
    {
        if (dc.Feasible) return false;

        // Later passes scale demand by s >= 1 only if no loss estimate can be negative.
        foreach (Branch br in model.Branches) if (!(br.R >= 0)) return false;
        foreach (Bus b in model.Buses) if (!(b.Gs >= 0)) return false;

        if (dc.Islands is null)
        {
            // Connected network: one island holding every bus and unit, under the connected path's verdict.
            double demand = model.Buses.Sum(b => b.Pd);
            return IslandStaysInfeasible(demand, model.Generators, dc.SecurityLpStatus);
        }

        // Several islands: the DC-OPF found its islands on the same topology, so they are AcOpf's islands in the same
        // order. Should that ever not hold, prove nothing (fall back to the full loop) rather than pair them wrongly.
        if (dc.Islands.Count != islands.Count) return false;
        var islandOf = new Dictionary<int, int>(model.Buses.Count);
        for (int k = 0; k < islands.Count; k++)
        {
            if (dc.Islands[k].ReferenceBus != islands[k].ReferenceBusId) return false;
            foreach (int id in islands[k].BusIds) islandOf.TryAdd(id, k);
        }
        var unitsOf = new List<Generator>[islands.Count];
        for (int k = 0; k < islands.Count; k++) unitsOf[k] = new List<Generator>();
        foreach (Generator g in model.Generators)
        {
            if (!islandOf.TryGetValue(g.Bus, out int k)) return false;
            unitsOf[k].Add(g);
        }
        for (int k = 0; k < islands.Count; k++)
        {
            DcOptimalPowerFlow.IslandDispatch d = dc.Islands[k];
            if (d.Feasible) continue;
            if (!d.HasGeneration)
            {
                if (Math.Abs(d.LoadMw) > DcOptimalPowerFlow.BalanceToleranceMw) return true;   // (a)
                continue;
            }
            if (IslandStaysInfeasible(d.LoadMw, unitsOf[k], d.SecurityLpStatus)) return true;
        }
        return false;
    }

    /// <summary>N173: one energised (or dead) island's proof, cases (a)-(c) of <see cref="StaysInfeasible"/>, for an island
    /// whose pass-0 DC verdict was infeasible.</summary>
    private static bool IslandStaysInfeasible(double demandMw, IReadOnlyList<Generator> units, LpStatus? lpStatus)
    {
        // No positive demand: never scaled, so every pass repeats the same infeasible DC problem.
        if (!(demandMw > 0)) return true;
        // (a) No unit at all.
        if (units.Count == 0) return true;
        // (b) Capability, with the DC-OPF's own Pmax reading (an unbounded Pmax counts as UnboundedPmaxMw) and tolerance.
        double capability = 0;
        foreach (Generator g in units) capability += double.IsInfinity(g.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : g.Pmax;
        if (demandMw > capability + DcOptimalPowerFlow.BalanceToleranceMw) return true;
        // (c) Line limits proved infeasible, with zero output inside every unit's range.
        if (lpStatus == LpStatus.Infeasible && units.All(g => g.Pmin <= 0 && g.Pmax >= 0)) return true;
        return false;
    }

    /// <summary>N111: what a failed AC solve inside the loop reported (its last state, not a solution).</summary>
    private static AcOpfAcSolveFailure Describe(int pass, PowerFlowResult ac)
        => new(pass, ac.Status, ac.Iterations, ac.MaxMismatch, ac.Vm.Length > 0 ? ac.Vm.Min() : double.NaN);

    /// <summary>F19: the DC-OPF options of a cost-curve pass (line limits on, as the fuel-type call has them).</summary>
    private static readonly DcOptimalPowerFlow.DcOpfOptions CurveOptions = new() { UseGeneratorCosts = true };

    private static GridModel ApplyDispatch(GridModel model, IReadOnlyList<DcOptimalPowerFlow.GenDispatch> dispatch)
    {
        var gens = new List<Generator>(model.Generators.Count);
        for (int i = 0; i < model.Generators.Count; i++)
        {
            Generator g = model.Generators[i];
            double pg = i < dispatch.Count ? dispatch[i].Pg : g.Pg;
            gens.Add(g with { Pg = pg });
        }
        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        return model.CopyWith(generators: gens);
    }
}
