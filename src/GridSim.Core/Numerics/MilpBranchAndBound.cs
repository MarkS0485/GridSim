using System.Globalization;

namespace GridSim.Core.Numerics;

/// <summary>A mixed-integer linear program for <see cref="MilpBranchAndBound"/>: the linear program <see cref="Lp"/>
/// (minimise c'x, A x = b, lo &lt;= x &lt;= hi) with the columns flagged in <see cref="Integer"/> required to take whole
/// values. An integer column's bounds are rounded inwards (ceiling of the lower, floor of the upper) before the search.</summary>
public sealed record MilpProblem(SparseLpProblem Lp, bool[] Integer);

/// <summary>How <see cref="MilpBranchAndBound.Solve"/> ended.</summary>
public enum MilpStatus
{
    /// <summary>The incumbent is optimal within the gap: no open node can beat it by more than the gap.</summary>
    Optimal,
    /// <summary>No point satisfies the constraints with the integer columns whole: the root relaxation is infeasible, or
    /// every node was infeasible or branched to infeasibility (each proven by the simplex's phase one). Also
    /// reported when an integer column's rounded bounds cross.</summary>
    Infeasible,
    /// <summary>The root relaxation is unbounded (an improving direction meets no bound), so there is no finite optimum to
    /// search for. No point is returned.</summary>
    RootUnbounded,
    /// <summary>The node cap was reached first. The incumbent (if any) and the best bound are returned, so the caller
    /// can see how far from proven optimal it is.</summary>
    NodeCap,
    /// <summary>A node relaxation ended without an answer (its iteration cap or a numerical failure), so the node could
    /// neither be pruned nor branched safely. The search stops there, loudly, with the incumbent and bound so far.</summary>
    LpFailure,
}

/// <summary>The order <see cref="MilpBranchAndBound"/> takes open nodes in.</summary>
public enum MilpNodeOrder
{
    /// <summary>The open node with the lowest relaxation bound first (ties: the node created first). Proves optimality
    /// in the fewest nodes; the default.</summary>
    BestBound,
    /// <summary>The newest node first (a stack): reaches whole-valued points quickly, so an incumbent appears early.</summary>
    DepthFirst,
}

/// <summary>The settings of <see cref="MilpBranchAndBound.Solve"/>.</summary>
public sealed record MilpOptions
{
    /// <summary>Stop (and prune) when the incumbent is within max(<see cref="AbsoluteGap"/>, RelativeGap x |incumbent|) of
    /// the bound. Default 1e-6: the node relaxations are solved to a tolerance of 1e-9, so a gap much tighter than that
    /// would branch on the relaxation's rounding rather than on the problem.</summary>
    public double RelativeGap { get; init; } = 1e-6;

    /// <summary>The absolute part of the gap (default 1e-9).</summary>
    public double AbsoluteGap { get; init; } = 1e-9;

    /// <summary>A relaxation value within this of a whole number counts as whole (default 1e-6).</summary>
    public double IntegralityTolerance { get; init; } = 1e-6;

    /// <summary>The most nodes (relaxations) solved before stopping with <see cref="MilpStatus.NodeCap"/> (default 100,000).</summary>
    public int NodeCap { get; init; } = 100_000;

    /// <summary>The node order (default <see cref="MilpNodeOrder.BestBound"/>).</summary>
    public MilpNodeOrder Order { get; init; } = MilpNodeOrder.BestBound;

    /// <summary>The tolerance of each node relaxation (default <see cref="SparseBoundedSimplex.DefaultTolerance"/>).</summary>
    public double LpTolerance { get; init; } = SparseBoundedSimplex.DefaultTolerance;

    /// <summary>The iteration cap of each node relaxation (default <see cref="SparseBoundedSimplex.DefaultMaxIterations"/>).</summary>
    public int LpMaxIterations { get; init; } = SparseBoundedSimplex.DefaultMaxIterations;
}

/// <summary>The result of <see cref="MilpBranchAndBound.Solve"/>. <see cref="X"/> is the incumbent (integer columns
/// exactly whole, continuous columns from a final relaxation with the integers fixed), or null when there is none;
/// <see cref="Objective"/> is c'x at it (NaN with no incumbent). <see cref="Bound"/> is the best proven lower bound
/// (the incumbent's objective when the search finished, +inf when infeasible).</summary>
public sealed record MilpResult(
    MilpStatus Status, double[]? X, double Objective, double Bound, int Nodes, int LpIterations, string Message)
{
    /// <summary>True when the incumbent was proven optimal within the gap.</summary>
    public bool Optimal => Status == MilpStatus.Optimal;
}

/// <summary>
/// A mixed-integer linear programming solver: LP-based branch-and-bound over the sparse bounded simplex
/// (<see cref="SparseBoundedSimplex"/>), built in Core with no dependencies (roadmap decision Q2), for the unit commitment
/// of the roadmap (P0-3).
///
/// <para><b>Method.</b> Each node is the LP relaxation with some integer columns' bounds tightened. A node whose
/// relaxation is infeasible (proven by the simplex's phase one) is dropped; one whose bound cannot beat the incumbent by
/// more than the gap is pruned; one whose integer columns are all whole (within <see cref="MilpOptions.IntegralityTolerance"/>)
/// gives a candidate incumbent: the integer columns are rounded to the whole numbers, fixed, and the relaxation is solved
/// once more for the continuous columns, so the reported point has exactly whole integer columns and an objective computed
/// at them. Otherwise the node branches on its most fractional integer column (distance to the nearest whole number; ties:
/// the lowest column) into x &lt;= floor and x &gt;= ceiling, the child on the nearer side created first.</para>
///
/// <para><b>Why a simplex and not the interior point method.</b> The first version solved the nodes with
/// <see cref="SparseInteriorPointLp"/> (2.0.140), because it scales with the nonzeros. On unit commitment relaxations it
/// failed three ways that branch-and-bound cannot live with: it has no certificate of infeasibility (it diverges or stalls,
/// and on one infeasible child its Newton matrix even held an infinite entry, bug #33); it does not converge when the
/// relaxation has no strictly interior point (the optimal duals are then unbounded), which branching creates by fixing
/// columns and which a period whose demand equals the committed capacity creates by itself; and an exact-penalty elastic
/// reformulation, which has an interior point by construction, diverged too. A simplex has none of these problems: phase
/// one proves infeasibility, fixed columns and empty interiors are ordinary, and the answer is a vertex, so whole values come
/// out whole. The nodes are re-solved from a cold start (no dual simplex warm start yet), which is the price.</para>
///
/// <para>A node whose relaxation ends without an answer (its iteration cap or a singular basis) is never pruned or
/// branched on a guess: the search stops with <see cref="MilpStatus.LpFailure"/>, naming the node.</para>
///
/// <para><b>Determinism.</b> One thread; nodes are numbered as created, best-bound ties go to the lower number, branching
/// ties to the lower column, and each relaxation is itself bit-identical from run to run, so the whole search (node count,
/// incumbent, bound) is bit-identical from run to run and on every CPU the LP is.</para>
/// </summary>
public static class MilpBranchAndBound
{
    /// <summary>Solve <paramref name="problem"/>. Throws <see cref="ArgumentException"/> for a malformed problem (the LP
    /// checks of <see cref="SparseBoundedSimplex.Solve"/>, or an integer flag array of the wrong length) and
    /// <see cref="ArgumentOutOfRangeException"/> for settings that are not usable.</summary>
    public static MilpResult Solve(MilpProblem problem, MilpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(problem.Lp);
        options ??= new MilpOptions();
        SparseLpProblem lp = problem.Lp;
        int n = lp.Columns;
        if (problem.Integer is null || problem.Integer.Length != n)
            throw new ArgumentException($"MILP: the integer flags must have {n} entries, one per column.", nameof(problem));
        if (!(options.RelativeGap >= 0) || !double.IsFinite(options.RelativeGap))
            throw new ArgumentOutOfRangeException(nameof(options), options.RelativeGap, "The MILP relative gap must be a finite number at or above 0.");
        if (!(options.AbsoluteGap >= 0) || !double.IsFinite(options.AbsoluteGap))
            throw new ArgumentOutOfRangeException(nameof(options), options.AbsoluteGap, "The MILP absolute gap must be a finite number at or above 0.");
        if (!(options.IntegralityTolerance > 0) || !(options.IntegralityTolerance < 0.5))
            throw new ArgumentOutOfRangeException(nameof(options), options.IntegralityTolerance, "The integrality tolerance must be above 0 and below 0.5.");
        if (options.NodeCap < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.NodeCap, "The MILP needs a node cap of at least 1.");

        // Integer columns' bounds rounded inwards; a crossing means no whole value fits.
        var rootLo = (double[])lp.Lower.Clone();
        var rootHi = (double[])lp.Upper.Clone();
        for (int j = 0; j < n; j++)
        {
            if (!problem.Integer[j]) continue;
            if (double.IsNaN(rootLo[j]) || double.IsNaN(rootHi[j]))
                throw new ArgumentException($"MILP: column {j} has a bound that is not a number.", nameof(problem));
            if (double.IsFinite(rootLo[j])) rootLo[j] = Math.Ceiling(rootLo[j] - options.IntegralityTolerance);
            if (double.IsFinite(rootHi[j])) rootHi[j] = Math.Floor(rootHi[j] + options.IntegralityTolerance);
            if (rootLo[j] > rootHi[j])
                return new MilpResult(MilpStatus.Infeasible, null, double.NaN, double.PositiveInfinity, 0, 0,
                    $"integer column {j} has no whole value between its bounds {Fmt(lp.Lower[j])} and {Fmt(lp.Upper[j])}");
        }

        var nodes = new List<Node> { new Node(-1, -1, false, 0, double.NegativeInfinity) };
        var open = new OpenSet(options.Order);
        open.Add(0, double.NegativeInfinity);

        double[]? incumbent = null;
        double incumbentObj = double.PositiveInfinity;
        int solved = 0, lpIterations = 0;
        bool rootSeen = false;
        var lo = new double[n];
        var hi = new double[n];

        while (open.Count > 0)
        {
            // The best bound over every open node, for the gap test and the reports.
            double globalBound = Math.Min(open.MinBound(), incumbentObj);
            if (incumbent is not null && incumbentObj - globalBound <= Gap(incumbentObj, options))
                break;
            if (solved == options.NodeCap)
            {
                return new MilpResult(MilpStatus.NodeCap, incumbent, incumbent is null ? double.NaN : incumbentObj, globalBound,
                    solved, lpIterations,
                    $"node cap {options.NodeCap} reached: " + (incumbent is null
                        ? $"no whole-valued point found yet; best bound {Fmt(globalBound)}"
                        : $"incumbent {Fmt(incumbentObj)}, best bound {Fmt(globalBound)}, gap {Fmt(incumbentObj - globalBound)}"));
            }

            int id = open.Pop();
            Node node = nodes[id];
            if (incumbent is not null && node.ParentBound >= incumbentObj - Gap(incumbentObj, options))
                continue;   // pruned by bound before solving: its parent's relaxation already cannot beat the incumbent

            BoundsOf(id, nodes, rootLo, rootHi, lo, hi);
            SparseSimplexResult r = SparseBoundedSimplex.Solve(lp, lo, hi, options.LpTolerance, options.LpMaxIterations);
            solved++;
            lpIterations += r.Iterations;

            if (r.Status == SparseSimplexStatus.Infeasible)
            {
                if (!rootSeen)
                    return new MilpResult(MilpStatus.Infeasible, null, double.NaN, double.PositiveInfinity, solved, lpIterations,
                        $"the root relaxation is {r.Message}");
                continue;   // proven infeasible: drop it
            }
            if (r.Status == SparseSimplexStatus.Unbounded && !rootSeen)
                return new MilpResult(MilpStatus.RootUnbounded, null, double.NaN, double.NegativeInfinity, solved, lpIterations,
                    "the root relaxation is " + r.Message);
            if (!r.Optimal)
                return new MilpResult(MilpStatus.LpFailure, incumbent, incumbent is null ? double.NaN : incumbentObj,
                    Math.Min(open.MinBound(), Math.Min(node.ParentBound, incumbentObj)), solved, lpIterations,
                    $"the relaxation of node {id} ({Describe(id, nodes)}) ended without an answer: {r.Message}");
            rootSeen = true;

            double bound = r.Objective;
            if (incumbent is not null && bound >= incumbentObj - Gap(incumbentObj, options))
                continue;

            // Most fractional integer column; ties to the lowest column.
            int branch = -1;
            double bestFrac = options.IntegralityTolerance;
            for (int j = 0; j < n; j++)
            {
                if (!problem.Integer[j]) continue;
                double frac = Math.Abs(r.X[j] - Math.Round(r.X[j], MidpointRounding.ToEven));
                if (frac > bestFrac) { bestFrac = frac; branch = j; }
            }

            if (branch < 0)
            {
                (double[]? cand, double candObj, string? why, bool roundedInfeasible) = Polish(lp, problem.Integer, lo, hi, r.X, options);
                if (cand is null && roundedInfeasible)
                {
                    // The relaxation is whole within IntegralityTolerance, but rounding its integers by up to that much
                    // and fixing them leaves the continuous part infeasible: the node is NOT integer-feasible at the
                    // rounded values. It used to abort the whole search (LpFailure) although the problem may well be
                    // feasible elsewhere. Branch on the integer column farthest from whole (however small its
                    // fraction), so both sides of the rounding are explored; when every integer column is exactly whole
                    // the rounded point is the relaxation's own and the node is dropped as infeasible.
                    double worst = 0.0;
                    for (int j = 0; j < n; j++)
                    {
                        if (!problem.Integer[j]) continue;
                        double frac = Math.Abs(r.X[j] - Math.Round(r.X[j], MidpointRounding.ToEven));
                        if (frac > worst) { worst = frac; branch = j; }
                    }
                    if (branch < 0) continue;
                }
                else
                {
                    if (cand is null)
                        return new MilpResult(MilpStatus.LpFailure, incumbent, incumbent is null ? double.NaN : incumbentObj,
                            Math.Min(open.MinBound(), Math.Min(bound, incumbentObj)), solved, lpIterations,
                            $"node {id} ({Describe(id, nodes)}) is whole-valued but its integers-fixed relaxation failed: {why}");
                    if (candObj < incumbentObj)
                    {
                        incumbent = cand;
                        incumbentObj = candObj;
                    }
                    continue;
                }
            }

            // Children: the nearer side first (created first, so it wins best-bound ties and is popped first depth-first).
            double v = r.X[branch];
            double down = Math.Floor(v), up = Math.Ceiling(v);
            bool upFirst = v - down > 0.5;
            int first = nodes.Count;
            nodes.Add(new Node(id, branch, upFirst, upFirst ? up : down, bound));
            nodes.Add(new Node(id, branch, !upFirst, upFirst ? down : up, bound));
            if (options.Order == MilpNodeOrder.DepthFirst)
            {
                open.Add(first + 1, bound);   // the stack pops the last pushed: push the farther side first
                open.Add(first, bound);
            }
            else
            {
                open.Add(first, bound);
                open.Add(first + 1, bound);
            }
        }

        if (incumbent is null)
            return new MilpResult(MilpStatus.Infeasible, null, double.NaN, double.PositiveInfinity, solved, lpIterations,
                $"no whole-valued point exists: every one of the {solved} node(s) was infeasible or branched to infeasibility");
        double finalBound = Math.Min(open.Count > 0 ? open.MinBound() : incumbentObj, incumbentObj);
        return new MilpResult(MilpStatus.Optimal, incumbent, incumbentObj, finalBound, solved, lpIterations,
            $"optimal {Fmt(incumbentObj)} after {solved} node(s), bound {Fmt(finalBound)}");
    }

    private static double Gap(double incumbentObj, MilpOptions o) => Math.Max(o.AbsoluteGap, o.RelativeGap * Math.Abs(incumbentObj));

    /// <summary>The integers rounded and fixed, the continuous columns re-solved: an exactly whole incumbent.</summary>
    private static (double[]? X, double Objective, string? Why, bool RoundedInfeasible) Polish(
        SparseLpProblem lp, bool[] integer, double[] lo, double[] hi, double[] x, MilpOptions o)
    {
        int n = lp.Columns;
        var flo = (double[])lo.Clone();
        var fhi = (double[])hi.Clone();
        bool anyContinuous = false;
        for (int j = 0; j < n; j++)
        {
            if (integer[j]) { double w = Math.Round(x[j], MidpointRounding.ToEven); flo[j] = w; fhi[j] = w; }
            else anyContinuous = true;
        }
        double[] result;
        if (anyContinuous)
        {
            SparseSimplexResult p = SparseBoundedSimplex.Solve(lp, flo, fhi, o.LpTolerance, o.LpMaxIterations);
            if (!p.Optimal) return (null, double.NaN, p.Message, p.Status == SparseSimplexStatus.Infeasible);
            result = p.X;
        }
        else
        {
            result = new double[n];
        }
        for (int j = 0; j < n; j++)
        {
            if (integer[j]) result[j] = flo[j];
            else
            {
                // A basic value within the relaxation's tolerance of a finite bound (round-off in the basis solve) is put on
                // it, so an output held at 0 or at its limit reads exactly so.
                double v = Math.Min(Math.Max(result[j], flo[j]), fhi[j]);
                if (double.IsFinite(flo[j]) && v - flo[j] <= o.LpTolerance * (1 + Math.Abs(flo[j]))) v = flo[j];
                if (double.IsFinite(fhi[j]) && fhi[j] - v <= o.LpTolerance * (1 + Math.Abs(fhi[j]))) v = fhi[j];
                result[j] = v;
            }
        }
        double obj = 0;
        for (int j = 0; j < n; j++) obj += lp.C[j] * result[j];
        return (result, obj, null, false);
    }

    private static void BoundsOf(int id, List<Node> nodes, double[] rootLo, double[] rootHi, double[] lo, double[] hi)
    {
        Array.Copy(rootLo, lo, lo.Length);
        Array.Copy(rootHi, hi, hi.Length);
        // Walk to the root; a column branched twice keeps the tighter bound whatever the order.
        for (int k = id; k >= 0 && nodes[k].Column >= 0; k = nodes[k].Parent)
        {
            Node nd = nodes[k];
            if (nd.Up) lo[nd.Column] = Math.Max(lo[nd.Column], nd.Value);
            else hi[nd.Column] = Math.Min(hi[nd.Column], nd.Value);
        }
    }

    private static string Describe(int id, List<Node> nodes)
    {
        var parts = new List<string>();
        for (int k = id; k >= 0 && nodes[k].Column >= 0; k = nodes[k].Parent)
            parts.Add($"x{nodes[k].Column} {(nodes[k].Up ? ">=" : "<=")} {Fmt(nodes[k].Value)}");
        if (parts.Count == 0) return "the root";
        parts.Reverse();
        return string.Join(", ", parts);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>One node: its parent, the column it branched (-1 at the root), the side (Up: x &gt;= Value; else
    /// x &lt;= Value) and the parent's relaxation bound.</summary>
    private readonly record struct Node(int Parent, int Column, bool Up, double Value, double ParentBound);

    /// <summary>The open nodes: a stack for depth-first, a (bound, id) ordered set for best-bound.</summary>
    private sealed class OpenSet(MilpNodeOrder order)
    {
        private readonly List<int> _stack = new();
        private readonly SortedSet<(double Bound, int Id)> _byBound = new();

        public int Count => order == MilpNodeOrder.DepthFirst ? _stack.Count : _byBound.Count;

        public void Add(int id, double bound)
        {
            if (order == MilpNodeOrder.DepthFirst) { _stack.Add(id); _boundOf[id] = bound; }
            else _byBound.Add((bound, id));
        }

        private readonly Dictionary<int, double> _boundOf = new();

        public int Pop()
        {
            if (order == MilpNodeOrder.DepthFirst)
            {
                int id = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                _boundOf.Remove(id);
                return id;
            }
            (double _, int top) = _byBound.Min;
            _byBound.Remove(_byBound.Min);
            return top;
        }

        /// <summary>The lowest parent bound over the open nodes (+inf when none).</summary>
        public double MinBound()
        {
            if (order == MilpNodeOrder.DepthFirst)
            {
                double m = double.PositiveInfinity;
                foreach (int id in _stack) m = Math.Min(m, _boundOf[id]);
                return m;
            }
            return _byBound.Count == 0 ? double.PositiveInfinity : _byBound.Min.Bound;
        }
    }
}
