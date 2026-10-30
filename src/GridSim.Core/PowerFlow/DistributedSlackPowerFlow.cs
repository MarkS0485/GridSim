using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>F22: how the participation factors of a distributed slack are chosen.</summary>
public enum ParticipationRule
{
    /// <summary>Proportional to each unit's Pmax (a unit with an infinite or non-positive Pmax does not take part).</summary>
    Pmax,
    /// <summary>Proportional to each unit's scheduled Pg (units scheduled at or below 0 MW do not take part).</summary>
    Pg,
    /// <summary>Every unit equally.</summary>
    Equal,
}

/// <summary>
/// F22 (October 2026): the power flow with a DISTRIBUTED slack. The single-slack Newton engines hand the whole real-
/// power balance (losses and any scheduling error) to the reference bus; here it is shared among the participating
/// units by participation factors alpha_g (sum 1): every unit produces <c>Pg0_g + alpha_g K</c>, with K the system
/// imbalance, and the reference bus keeps only the angle reference (its own units take their share like any other).
///
/// <para><b>Method: an outer loop around an unchanged engine</b>, like the tap-changer loop
/// (<see cref="OltcLoopPowerFlow"/>). Each pass allocates K to the units, solves with the engine, and reads the
/// reference bus's solved generation: the excess e over its own units' allocation is the part of the imbalance the
/// allocation did not yet cover, so <c>K += e</c>, until <c>|e| &lt;= <see cref="ToleranceMw"/></c>. At the fixed point
/// every unit produces exactly its allocation, which is the distributed-slack solution (the reference's P equation
/// holds with its share, every other bus's by construction). Since K_new = demand + losses(K) - sum Pg0 and the losses
/// change little with K, the loop contracts quickly (a few passes). Because each pass is an ordinary engine solve, the
/// dense and sparse engines give the same bits here too: their per-solve results are bit-identical, and the loop's own
/// arithmetic is the same plain sequence of operations in model order.</para>
///
/// <para><b>Limits.</b> A unit is held within [Pmin, Pmax]: a unit that would leave its range is fixed at the bound
/// and the rest of its share goes to the others in proportion to their factors (water filling). When every
/// participating unit is at a bound and imbalance remains, the result is NOT converged (status MaxIterations) and
/// <see cref="LastNote"/> says why.</para>
/// <para><b>Several reference buses (N120, October 2026).</b> A model whose reference buses each sit in a separate
/// network island (buses joined by in-service branches) is solved with one imbalance per island: K_i is shared among
/// the participating units of island i only (the factors normalised within the island) and grown from island i's
/// reference bus excess, and the loop settles when every island's excess is within <see cref="ToleranceMw"/>. That is
/// the distributed slack of each island, exactly as if each were solved alone (the islands share no branch, so one
/// island's dispatch cannot move another's balance). Units in an island with no reference bus take no share. Two
/// reference buses in the SAME island stay refused: each fixes its own angle, so the power between them is set by the
/// network, not by the dispatch, and no single imbalance can bring both to their allocations. A model with exactly one
/// reference bus takes the original single-imbalance path, unchanged to the bit.</para>
/// <para>Opt-in: nothing uses this wrapper unless asked (the CLI <c>pf --distributed-slack</c>); every existing solve
/// is the single-slack engine, unchanged.</para>
/// </summary>
public sealed class DistributedSlackPowerFlow : IPowerFlowSolver
{
    /// <summary>The loop stops when the reference bus's generation differs from its allocation by at most this (MW).
    /// About 100 times the engines' 1e-8 pu mismatch tolerance on a 100 MVA base, so it measures the allocation, not
    /// the engine's own residual.</summary>
    public const double ToleranceMw = 1e-4;

    /// <summary>Default cap on outer passes.</summary>
    public const int DefaultMaxPasses = 50;

    private readonly IPowerFlowSolver _engine;
    private readonly ParticipationRule? _rule;
    private readonly double[]? _factors;
    private readonly int _maxPasses;

    /// <summary>Distribute by a rule (<see cref="ParticipationRule"/>).</summary>
    public DistributedSlackPowerFlow(IPowerFlowSolver engine, ParticipationRule rule, int maxPasses = DefaultMaxPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass is needed");
        _engine = engine; _rule = rule; _maxPasses = maxPasses;
    }

    /// <summary>Distribute by explicit factors, aligned to <c>model.Generators</c> (each finite and &gt;= 0, at least
    /// one &gt; 0; normalised to sum 1).</summary>
    public DistributedSlackPowerFlow(IPowerFlowSolver engine, IReadOnlyList<double> factors, int maxPasses = DefaultMaxPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(factors);
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass is needed");
        _engine = engine; _factors = factors.ToArray(); _maxPasses = maxPasses;
    }

    /// <summary>Outer passes taken by the last <see cref="Solve"/>.</summary>
    public int LastPasses { get; private set; }

    /// <summary>The system imbalance K (MW) shared out by the last <see cref="Solve"/> (with several island references,
    /// the sum of the island imbalances, in island order; see <see cref="LastIslandImbalancesMw"/>).</summary>
    public double LastImbalanceMw { get; private set; }

    /// <summary>N120: the imbalance K_i (MW) of each island, keyed by its reference bus id, for the last
    /// <see cref="Solve"/>; one entry for a single-reference model.</summary>
    public IReadOnlyDictionary<int, double> LastIslandImbalancesMw { get; private set; } = new Dictionary<int, double>();

    /// <summary>Each in-service unit's real output (MW) in the last <see cref="Solve"/>, aligned to <c>model.Generators</c>.</summary>
    public IReadOnlyList<double> LastDispatchMw { get; private set; } = Array.Empty<double>();

    /// <summary>The normalised participation factors of the last <see cref="Solve"/>, aligned to <c>model.Generators</c>.</summary>
    public IReadOnlyList<double> LastFactors { get; private set; } = Array.Empty<double>();

    /// <summary>Why the last <see cref="Solve"/> did not settle (null when it did).</summary>
    public string? LastNote { get; private set; }

    /// <summary>Solve <paramref name="model"/> with the slack distributed.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        LastNote = null;
        int g = model.Generators.Count;
        int[] slackBuses = model.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Id).Distinct().ToArray();
        if (slackBuses.Length == 0)
            throw new InvalidOperationException(
                "Distributed slack: the model has 0 reference (slack) buses; it needs one reference bus in each network island.");
        if (slackBuses.Length > 1) return SolveIslands(model, options, slackBuses);   // N120
        int slackId = slackBuses[0];

        double[] alpha = Factors(model);
        LastFactors = alpha;
        var pg0 = new double[g];
        var lo = new double[g];
        var hi = new double[g];
        for (int i = 0; i < g; i++)
        {
            Generator gen = model.Generators[i];
            pg0[i] = gen.Pg;
            lo[i] = gen.Pmin;
            hi[i] = gen.Pmax;
        }
        Bus slackBus = model.Buses.First(b => b.Id == slackId);

        double k = 0.0;
        PowerFlowResult? r = null;
        double[] pg = pg0;
        for (int pass = 1; pass <= _maxPasses; pass++)
        {
            LastPasses = pass;
            (pg, double unplaced) = Allocate(pg0, alpha, lo, hi, k);
            var gens = new Generator[g];
            for (int i = 0; i < g; i++) gens[i] = model.Generators[i] with { Pg = pg[i] };
            r = _engine.Solve(model.CopyWith(generators: gens), options);
            LastImbalanceMw = k;
            LastIslandImbalancesMw = new Dictionary<int, double> { [slackId] = k };
            LastDispatchMw = pg;
            if (!r.Converged) { LastNote = $"the engine did not converge on pass {pass}"; return r; }

            // The reference bus's solved generation: its net injection plus the load it actually draws.
            int si = -1;
            for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == slackId) { si = i; break; }
            if (si < 0) throw new InvalidOperationException($"Distributed slack: the result has no reference bus {slackId}.");
            double slackGen = r.PInjMw[si] + LoadAtVoltage.Mw(slackBus, r.Vm[si]).PMw;
            double allocated = 0.0;
            for (int i = 0; i < g; i++) if (model.Generators[i].Bus == slackId) allocated += pg[i];
            double e = slackGen - allocated;
            if (Math.Abs(e) <= ToleranceMw)
            {
                if (Math.Abs(unplaced) > ToleranceMw)
                {
                    LastNote = $"every participating unit is at a limit; {unplaced:R} MW of the imbalance could not be shared";
                    return NotConverged(r);
                }
                return r;
            }
            if (AtLimitAndShortInSameDirection(unplaced, e))
            {
                // GR-052: every participating unit is bound (Allocate leaves a remainder only then) and the reference
                // still wants more in the same direction, so a larger K places nothing more: the next pass would solve
                // the same dispatch and find the same excess. The loop used to run all its passes, grow K by the
                // shortfall each time and end with the generic note; it now stops here and says why.
                LastNote = $"every participating unit is at a limit; {unplaced + e:R} MW of the imbalance could not be shared";
                return NotConverged(r);
            }
            k += e;
        }
        LastNote = $"the distributed slack did not settle within {_maxPasses} passes";
        return NotConverged(r!);
    }

    /// <summary>GR-052: true when the allocation left part of K unplaced (every participating unit at a bound) and the
    /// reference bus's excess asks for more in that same direction, so no further pass can place it.</summary>
    private static bool AtLimitAndShortInSameDirection(double unplaced, double excess) =>
        Math.Abs(unplaced) > ToleranceMw && Math.Abs(excess) > ToleranceMw && Math.Sign(unplaced) == Math.Sign(excess);

    /// <summary>
    /// N120: the distributed slack of a model with several reference buses, one imbalance per island (see the class
    /// remarks). Refuses two reference buses in one island, naming them.
    /// </summary>
    private PowerFlowResult SolveIslands(GridModel model, PowerFlowOptions? options, int[] slackBuses)
    {
        int g = model.Generators.Count;

        // Islands: buses joined by in-service branches (union-find over bus ids; out-of-service branches carry nothing).
        var parent = new Dictionary<int, int>(model.Buses.Count);
        foreach (Bus b in model.Buses) parent.TryAdd(b.Id, b.Id);
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || !parent.ContainsKey(br.From) || !parent.ContainsKey(br.To)) continue;
            int a = Find(br.From), c = Find(br.To);
            if (a != c) parent[a] = c;
        }

        // Exactly one reference bus per island that has one; two in one island is not a distributed-slack problem.
        var slackOfRoot = new Dictionary<int, int>();
        var clashes = new List<string>();
        foreach (int s in slackBuses)
        {
            int root = Find(s);
            if (slackOfRoot.TryGetValue(root, out int other)) clashes.Add($"buses {other} and {s}");
            else slackOfRoot[root] = s;
        }
        if (clashes.Count > 0)
            throw new InvalidOperationException(
                $"Distributed slack: the model has {slackBuses.Length} reference (slack) buses, and some share one connected " +
                $"network ({string.Join("; ", clashes)}). Each reference bus fixes its own angle, so the power between two in " +
                "one network is set by the network, not by the dispatch, and no single imbalance can share it. Several " +
                "reference buses are supported only one per network island (one imbalance per island); make one of them PV.");

        // The raw factors, then normalised within each island (units in an island with no reference take no share).
        double[] raw = RawFactors(model);
        var islandOfGen = new int[g];   // the reference bus of the unit's island, or int.MinValue for none
        for (int i = 0; i < g; i++)
        {
            int bus = model.Generators[i].Bus;
            islandOfGen[i] = parent.ContainsKey(bus) && slackOfRoot.TryGetValue(Find(bus), out int s) ? s : int.MinValue;
        }
        int[] islands = slackBuses;   // island order = reference bus order in the model
        var members = new Dictionary<int, int[]>(islands.Length);
        foreach (int s in islands)
        {
            int[] idx = Enumerable.Range(0, g).Where(i => islandOfGen[i] == s).ToArray();
            double sum = 0.0;
            foreach (int i in idx) sum += raw[i];
            if (!(sum > 0.0))
                throw new InvalidOperationException(
                    $"Distributed slack: no generator takes part in the island of reference bus {s} (every participation factor there is zero).");
            members[s] = idx;
        }
        var alpha = new double[g];
        foreach (int s in islands)
        {
            double sum = 0.0;
            foreach (int i in members[s]) sum += raw[i];
            foreach (int i in members[s]) alpha[i] = raw[i] / sum;
        }
        LastFactors = alpha;

        var pg0 = new double[g];
        for (int i = 0; i < g; i++) pg0[i] = model.Generators[i].Pg;
        var busOf = model.Buses.GroupBy(b => b.Id).ToDictionary(x => x.Key, x => x.First());
        var k = new Dictionary<int, double>(islands.Length);
        foreach (int s in islands) k[s] = 0.0;

        PowerFlowResult? r = null;
        for (int pass = 1; pass <= _maxPasses; pass++)
        {
            LastPasses = pass;
            var pg = (double[])pg0.Clone();
            var unplaced = new Dictionary<int, double>(islands.Length);
            foreach (int s in islands)
            {
                int[] idx = members[s];
                double[] p0 = idx.Select(i => pg0[i]).ToArray();
                double[] a = idx.Select(i => alpha[i]).ToArray();
                double[] lo = idx.Select(i => model.Generators[i].Pmin).ToArray();
                double[] hi = idx.Select(i => model.Generators[i].Pmax).ToArray();
                (double[] pIsland, double left) = Allocate(p0, a, lo, hi, k[s]);
                for (int j = 0; j < idx.Length; j++) pg[idx[j]] = pIsland[j];
                unplaced[s] = left;
            }
            var gens = new Generator[g];
            for (int i = 0; i < g; i++) gens[i] = model.Generators[i] with { Pg = pg[i] };
            r = _engine.Solve(model.CopyWith(generators: gens), options);
            LastImbalanceMw = 0.0;
            foreach (int s in islands) LastImbalanceMw += k[s];
            LastIslandImbalancesMw = new Dictionary<int, double>(k);
            LastDispatchMw = pg;
            if (!r.Converged) { LastNote = $"the engine did not converge on pass {pass}"; return r; }

            bool settled = true;
            var excess = new Dictionary<int, double>(islands.Length);
            foreach (int s in islands)
            {
                int si = -1;
                for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == s) { si = i; break; }
                if (si < 0) throw new InvalidOperationException($"Distributed slack: the result has no reference bus {s}.");
                double slackGen = r.PInjMw[si] + LoadAtVoltage.Mw(busOf[s], r.Vm[si]).PMw;
                double allocated = 0.0;
                for (int i = 0; i < g; i++) if (model.Generators[i].Bus == s) allocated += pg[i];
                excess[s] = slackGen - allocated;
                if (Math.Abs(excess[s]) > ToleranceMw) settled = false;
            }
            if (settled)
            {
                foreach (int s in islands)
                    if (Math.Abs(unplaced[s]) > ToleranceMw)
                    {
                        LastNote = $"every participating unit in the island of reference bus {s} is at a limit; {unplaced[s]:R} MW of its imbalance could not be shared";
                        return NotConverged(r);
                    }
                return r;
            }
            foreach (int s in islands)
                if (AtLimitAndShortInSameDirection(unplaced[s], excess[s]))
                {
                    // GR-052: this island can never settle (see the single-reference path), so neither can the loop.
                    LastNote = $"every participating unit in the island of reference bus {s} is at a limit; {unplaced[s] + excess[s]:R} MW of its imbalance could not be shared";
                    return NotConverged(r);
                }
            foreach (int s in islands) k[s] += excess[s];
        }
        LastNote = $"the distributed slack did not settle within {_maxPasses} passes";
        return NotConverged(r!);
    }

    /// <summary>The normalised factors for <paramref name="model"/>.</summary>
    private double[] Factors(GridModel model)
    {
        double[] a = RawFactors(model);
        int g = a.Length;
        double sum = 0.0;
        for (int i = 0; i < g; i++) sum += a[i];
        if (!(sum > 0.0))
            throw new InvalidOperationException("Distributed slack: no generator takes part (every participation factor is zero).");
        for (int i = 0; i < g; i++) a[i] /= sum;
        return a;
    }

    /// <summary>The participation factors before normalisation (explicit factors checked, or the rule's values), with a
    /// power-flow-only unit at 0.</summary>
    private double[] RawFactors(GridModel model)
    {
        int g = model.Generators.Count;
        var a = new double[g];
        if (_factors is not null)
        {
            if (_factors.Length != g)
                throw new ArgumentException($"Distributed slack: {_factors.Length} participation factors for {g} in-service generators.");
            for (int i = 0; i < g; i++)
            {
                double f = _factors[i];
                if (!double.IsFinite(f) || f < 0.0)
                    throw new ArgumentException($"Distributed slack: participation factor {i} is {f}; each must be finite and 0 or more.");
                a[i] = model.Generators[i].PowerFlowOnly ? 0.0 : f;
            }
        }
        else
        {
            for (int i = 0; i < g; i++)
            {
                Generator gen = model.Generators[i];
                if (gen.PowerFlowOnly) continue;
                a[i] = _rule switch
                {
                    ParticipationRule.Pmax => double.IsFinite(gen.Pmax) && gen.Pmax > 0 ? gen.Pmax : 0.0,
                    ParticipationRule.Pg => gen.Pg > 0 ? gen.Pg : 0.0,
                    _ => 1.0,
                };
            }
        }
        return a;
    }

    /// <summary>
    /// Share <paramref name="k"/> MW among the units by their factors within [lo, hi] (water filling): each free unit
    /// takes alpha_i / (sum of free alpha) of what is left; a unit that would cross a bound is fixed there and the
    /// share is recomputed over the rest. Returns the outputs and any MW that could not be placed (all units bound).
    /// </summary>
    internal static (double[] Pg, double Unplaced) Allocate(double[] pg0, double[] alpha, double[] lo, double[] hi, double k)
    {
        int g = pg0.Length;
        var pg = (double[])pg0.Clone();
        var fixedAt = new bool[g];
        for (int i = 0; i < g; i++) if (alpha[i] == 0.0) fixedAt[i] = true;
        double remaining = k;
        for (int round = 0; round <= g; round++)
        {
            double sum = 0.0;
            for (int i = 0; i < g; i++) if (!fixedAt[i]) sum += alpha[i];
            if (!(sum > 0.0) || remaining == 0.0) break;
            bool clamped = false;
            for (int i = 0; i < g; i++)
            {
                if (fixedAt[i]) continue;
                double target = pg0[i] + alpha[i] / sum * remaining;
                if (target > hi[i] || target < lo[i]) { clamped = true; break; }
            }
            if (!clamped)
            {
                for (int i = 0; i < g; i++) if (!fixedAt[i]) pg[i] = pg0[i] + alpha[i] / sum * remaining;
                remaining = 0.0;
                break;
            }
            // Fix every unit that would cross a bound at that bound, and take what it absorbs off the remainder.
            for (int i = 0; i < g; i++)
            {
                if (fixedAt[i]) continue;
                double target = pg0[i] + alpha[i] / sum * remaining;
                if (target > hi[i]) { pg[i] = hi[i]; fixedAt[i] = true; }
                else if (target < lo[i]) { pg[i] = lo[i]; fixedAt[i] = true; }
            }
            double placed = 0.0;
            for (int i = 0; i < g; i++) if (fixedAt[i] && alpha[i] != 0.0) placed += pg[i] - pg0[i];
            remaining = k - placed;
        }
        return (pg, remaining);
    }

    private static PowerFlowResult NotConverged(PowerFlowResult r) => new()
    {
        Converged = false,
        Status = PowerFlowStatus.MaxIterations,
        Iterations = r.Iterations,
        MaxMismatch = r.MaxMismatch,
        BusIds = r.BusIds,
        Vm = r.Vm,
        VaDeg = r.VaDeg,
        PInjMw = r.PInjMw,
        QInjMvar = r.QInjMvar,
        TotalLossMw = r.TotalLossMw,
        TotalLossMvar = r.TotalLossMvar,
        LineFlows = r.LineFlows,
        SolvedBusTypes = r.SolvedBusTypes,
    };
}
