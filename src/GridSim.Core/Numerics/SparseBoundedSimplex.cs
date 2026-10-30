using System.Globalization;

namespace GridSim.Core.Numerics;

/// <summary>How <see cref="SparseBoundedSimplex.Solve"/> ended.</summary>
public enum SparseSimplexStatus
{
    /// <summary>An optimal vertex.</summary>
    Optimal,
    /// <summary>Phase one ended with a positive least sum of infeasibilities: no point meets A x = b within the bounds.</summary>
    Infeasible,
    /// <summary>An improving direction with no bound in its way: the objective has no lower bound.</summary>
    Unbounded,
    /// <summary>The iteration cap was reached first.</summary>
    IterationCap,
    /// <summary>A basis could not be factorised (numerically singular).</summary>
    NumericalFailure,
}

/// <summary>The result of <see cref="SparseBoundedSimplex.Solve"/>: the point (a vertex when optimal), the row duals
/// y = B^-T c_B (the change of the optimal objective per unit of b), the objective c'x and the iterations of both phases.
/// When the status is not Optimal the point is where the method stopped.</summary>
public sealed record SparseSimplexResult(SparseSimplexStatus Status, double[] X, double[] Y, double Objective, int Iterations, string Message)
{
    /// <summary>True when an optimal vertex was found.</summary>
    public bool Optimal => Status == SparseSimplexStatus.Optimal;
}

/// <summary>
/// A sparse two-phase bounded-variable revised primal simplex for minimise c'x subject to A x = b, lo &lt;= x &lt;= hi (any
/// bound may be infinite; a column with both infinite is free), on the sparse LU (<see cref="SparseLu"/>). It is the
/// vertex solver the branch-and-bound needs (<see cref="MilpBranchAndBound"/>): unlike the interior point method it
/// proves infeasibility (phase one), lands on a vertex (so whole values appear as whole), and has no difficulty with
/// fixed columns or a feasible set with no interior point. <see cref="BoundedSimplex"/> (dense) is left untouched.
///
/// <para><b>Method.</b> Nonbasic columns rest at a finite bound (a free one at 0). Phase one adds one artificial per row,
/// signed so it starts at |b - A x_N| &gt;= 0, and minimises their sum; a positive optimum (above 1000 times the feasibility
/// tolerance times 1 + max |b|) proves infeasibility. Because a smaller positive optimum is passed on to phase two with
/// the artificials pinned at zero while still basic, every Optimal point is checked before it is returned: each row's
/// |A x - b| and each column's bounds within <see cref="BoundedSimplex"/>'s post-solve tolerance (1e-6 relative); a
/// point that breaks either comes back <see cref="SparseSimplexStatus.NumericalFailure"/>, never Optimal. Artificials still basic at zero are pivoted out where a real column
/// can take their place (a row where none can is redundant), and are then held at zero. Phase two minimises c'x. Each
/// iteration prices every nonbasic column with y = B^-T c_B (Dantzig: the largest reduced cost of the right sign; ties to
/// the lowest column), takes the entering column's B^-1 a_j, and the ratio test takes the first bound reached (the entering
/// column's own opposite bound gives a bound flip with no basis change; ties to the lowest basis position; a pivot below
/// 1e-9 in magnitude is never taken). After 50 consecutive degenerate steps the entering and leaving choices switch to
/// Bland's rule (lowest index) until a step makes progress, which rules out cycling.</para>
///
/// <para><b>Basis.</b> B is factorised by the sparse LU (and B^T for the dual solves), and each basis change appends a
/// product-form eta; the factors are rebuilt from scratch every 50 changes, which bounds the round-off the etas carry.
/// The primal values of the basic columns are recomputed from the factors at each rebuild.</para>
///
/// <para>Every loop runs in a fixed order, so the result is bit-identical from run to run.</para>
/// </summary>
public static class SparseBoundedSimplex
{
    /// <summary>Default reduced-cost and primal feasibility tolerance.</summary>
    public const double DefaultTolerance = 1e-9;

    /// <summary>Default iteration cap (both phases together).</summary>
    public const int DefaultMaxIterations = 100_000;

    /// <summary>The smallest pivot magnitude the ratio test accepts.</summary>
    public const double PivotTolerance = 1e-9;

    /// <summary>Basis changes between full refactorisations.</summary>
    public const int RefactorInterval = 50;

    /// <summary>Consecutive degenerate steps before switching to Bland's rule.</summary>
    public const int DegenerateRunLimit = 50;

    /// <summary>Solve min c'x, A x = b, lo &lt;= x &lt;= hi with the bounds given (they replace <paramref name="lp"/>'s
    /// when not null). Throws <see cref="ArgumentException"/> for a malformed problem.</summary>
    public static SparseSimplexResult Solve(SparseLpProblem lp, double[]? lower = null, double[]? upper = null,
        double tolerance = DefaultTolerance, int maxIterations = DefaultMaxIterations)
    {
        ArgumentNullException.ThrowIfNull(lp);
        int m = lp.Rows, n = lp.Columns;
        double[] lo = lower ?? lp.Lower, hi = upper ?? lp.Upper;
        if (lp.B is null || lp.B.Length != m || lp.C is null || lp.C.Length != n || lo is null || lo.Length != n || hi is null || hi.Length != n)
            throw new ArgumentException($"Simplex: b needs {m} entries, c and the bounds {n} each.", nameof(lp));
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The simplex tolerance must be a finite number above 0.");
        for (int j = 0; j < n; j++)
        {
            if (double.IsNaN(lo[j]) || double.IsNaN(hi[j]) || double.IsPositiveInfinity(lo[j]) || double.IsNegativeInfinity(hi[j]) || lo[j] > hi[j])
                throw new ArgumentException($"Simplex: column {j} has bounds {lo[j]} .. {hi[j]}.", nameof(lp));
            if (!double.IsFinite(lp.C[j])) throw new ArgumentException($"Simplex: c[{j}] is not a finite number.", nameof(lp));
        }
        for (int i = 0; i < m; i++)
            if (!double.IsFinite(lp.B[i])) throw new ArgumentException($"Simplex: b[{i}] is not a finite number.", nameof(lp));

        // Columns 0..n-1 the real ones, n..n+m-1 the artificials (one per row, signed below).
        int nt = n + m;
        var colRows = new List<int>[nt];
        var colVals = new List<double>[nt];
        for (int j = 0; j < nt; j++) { colRows[j] = new List<int>(); colVals[j] = new List<double>(); }
        foreach ((int r, int c, double v) in lp.A)
        {
            if ((uint)r >= (uint)m || (uint)c >= (uint)n) throw new ArgumentException($"Simplex: entry ({r}, {c}) is outside the {m} x {n} matrix.", nameof(lp));
            if (!double.IsFinite(v)) throw new ArgumentException($"Simplex: entry ({r}, {c}) is not a finite number.", nameof(lp));
            if (v == 0) continue;
            // A repeated cell is summed in the order given.
            int at = colRows[c].IndexOf(r);
            if (at >= 0) colVals[c][at] += v;
            else { colRows[c].Add(r); colVals[c].Add(v); }
        }

        var xlo = new double[nt];
        var xhi = new double[nt];
        var x = new double[nt];
        Array.Copy(lo, xlo, n);
        Array.Copy(hi, xhi, n);
        for (int j = 0; j < n; j++) x[j] = double.IsFinite(lo[j]) ? lo[j] : double.IsFinite(hi[j]) ? hi[j] : 0.0;

        // Residual b - A x_N fixes each artificial's sign.
        var res = (double[])lp.B.Clone();
        for (int j = 0; j < n; j++)
            if (x[j] != 0)
                for (int q = 0; q < colRows[j].Count; q++) res[colRows[j][q]] -= colVals[j][q] * x[j];
        for (int i = 0; i < m; i++)
        {
            int a = n + i;
            colRows[a].Add(i);
            colVals[a].Add(res[i] >= 0 ? 1.0 : -1.0);
            xlo[a] = 0; xhi[a] = double.PositiveInfinity;
            x[a] = Math.Abs(res[i]);
        }

        var basis = new int[m];          // column basic in each position
        var posOf = new int[nt];         // position of a basic column, -1 if nonbasic
        Array.Fill(posOf, -1);
        for (int i = 0; i < m; i++) { basis[i] = n + i; posOf[n + i] = i; }

        double bNorm = 0;
        foreach (double v in lp.B) bNorm = Math.Max(bNorm, Math.Abs(v));
        var state = new State(m, nt, colRows, colVals, basis, posOf, x, xlo, xhi);
        if (!state.Refactor(lp.B))
            return Result(SparseSimplexStatus.NumericalFailure, state, lp, 0, "the starting basis could not be factorised");

        int iterations = 0;
        // Phase one: minimise the artificials.
        var c1 = new double[nt];
        for (int i = 0; i < m; i++) c1[n + i] = 1.0;
        SparseSimplexStatus st = state.Run(c1, n, tolerance, maxIterations, ref iterations, lp.B, allowArtificialEntry: true);
        if (st == SparseSimplexStatus.IterationCap || st == SparseSimplexStatus.NumericalFailure)
            return Result(st, state, lp, iterations, st == SparseSimplexStatus.IterationCap
                ? $"no feasible point found within {maxIterations} iteration(s)" : "a basis could not be factorised in phase one");
        double infeas = 0;
        for (int i = 0; i < m; i++) infeas += state.X[n + i];
        if (infeas > tolerance * 1e3 * (1 + bNorm))
            return Result(SparseSimplexStatus.Infeasible, state, lp, iterations,
                $"infeasible: the least sum of infeasibilities is {infeas.ToString("R", CultureInfo.InvariantCulture)}");

        // Artificials held at zero; pivot the basic ones out where a real column can replace them.
        for (int i = 0; i < m; i++) { state.Lo[n + i] = 0; state.Hi[n + i] = 0; }
        if (!state.DriveOutArtificials(n, lp.B))
            return Result(SparseSimplexStatus.NumericalFailure, state, lp, iterations, "a basis could not be factorised while removing the artificials");

        // Phase two.
        var c2 = new double[nt];
        Array.Copy(lp.C, c2, n);
        st = state.Run(c2, n, tolerance, maxIterations, ref iterations, lp.B, allowArtificialEntry: false);
        return st switch
        {
            SparseSimplexStatus.Optimal => VerifyOptimal(state, lp, colRows, colVals, lo, hi) is string broken
                ? Result(SparseSimplexStatus.NumericalFailure, state, lp, iterations, broken)
                : Result(st, state, lp, iterations, $"optimal after {iterations} iteration(s)", c2),
            SparseSimplexStatus.Unbounded => Result(st, state, lp, iterations, "unbounded: an improving direction meets no bound"),
            SparseSimplexStatus.IterationCap => Result(st, state, lp, iterations, $"no optimum within {maxIterations} iteration(s)"),
            _ => Result(st, state, lp, iterations, "a basis could not be factorised in phase two"),
        };
    }

    /// <summary>The post-solve check of an Optimal point, with the dense solver's rule
    /// (<see cref="BoundedSimplex.OptimalPointRelativeTolerance"/>): each row within 1e-6 x max(1, |b_i| + sum |a_ij x_j|)
    /// of its right-hand side, each column within 1e-6 x max(1, |x_j|) of its bounds. Null when the point passes (it is
    /// then returned exactly as found, bit for bit); otherwise what it breaks. Phase one accepts a least sum of
    /// infeasibilities up to 1000 x tolerance x (1 + max |b|) and nothing re-checked A x = b afterwards, and the ratio
    /// test can move a basic column off its bound by a sub-pivot alpha: either used to ship a point that broke its own
    /// constraints as Optimal.</summary>
    private static string? VerifyOptimal(State s, SparseLpProblem lp, List<int>[] colRows, List<double>[] colVals, double[] lo, double[] hi)
    {
        int m = lp.Rows, n = lp.Columns;
        var ax = new double[m];
        var scale = new double[m];
        for (int i = 0; i < m; i++) scale[i] = Math.Abs(lp.B[i]);
        for (int j = 0; j < n; j++)
        {
            double xj = s.X[j];
            if (xj == 0) continue;
            for (int q = 0; q < colRows[j].Count; q++)
            {
                double t = colVals[j][q] * xj;
                ax[colRows[j][q]] += t;
                scale[colRows[j][q]] += Math.Abs(t);
            }
        }
        for (int i = 0; i < m; i++)
        {
            double bound = BoundedSimplex.OptimalPointRelativeTolerance * Math.Max(1.0, scale[i]);
            if (!(Math.Abs(ax[i] - lp.B[i]) <= bound))
                return string.Create(CultureInfo.InvariantCulture,
                    $"the Optimal point breaks row {i} (|A x - b| = {Math.Abs(ax[i] - lp.B[i]):R}, allowed {bound:R}); the basis drifted or phase one left a residual infeasibility");
        }
        for (int j = 0; j < n; j++)
        {
            double xj = s.X[j];
            double slack = BoundedSimplex.OptimalPointRelativeTolerance * Math.Max(1.0, Math.Abs(xj));
            if (!(xj >= lo[j] - slack) || !(xj <= hi[j] + slack))
                return string.Create(CultureInfo.InvariantCulture,
                    $"the Optimal point puts column {j} at {xj:R}, outside [{lo[j]:R}, {hi[j]:R}]; the basis drifted");
        }
        return null;
    }

    private static SparseSimplexResult Result(SparseSimplexStatus st, State s, SparseLpProblem lp, int it, string msg, double[]? cost = null)
    {
        int n = lp.Columns;
        var x = new double[n];
        Array.Copy(s.X, x, n);
        double obj = 0;
        for (int j = 0; j < n; j++) obj += lp.C[j] * x[j];
        double[] y = cost is null ? new double[lp.Rows] : s.Duals(cost);
        return new SparseSimplexResult(st, x, y, obj, it, msg);
    }

    /// <summary>The working state: columns, basis, values, bounds and the factorised basis with its eta file.</summary>
    private sealed class State(int m, int nt, List<int>[] colRows, List<double>[] colVals, int[] basis, int[] posOf,
        double[] x, double[] lo, double[] hi)
    {
        public double[] X => x;
        public double[] Lo => lo;
        public double[] Hi => hi;

        private SparseLuFactorization? _lu, _luT;
        private readonly List<(int Pos, double[] Alpha)> _etas = new();

        /// <summary>Factorise B and B^T afresh, drop the etas, and recompute the basic values from b - A_N x_N.</summary>
        public bool Refactor(double[] b)
        {
            _etas.Clear();
            if (m == 0) return true;
            var bb = new CsrMatrix.Builder(m);
            var bt = new CsrMatrix.Builder(m);
            for (int p = 0; p < m; p++)
            {
                int j = basis[p];
                for (int q = 0; q < colRows[j].Count; q++) { bb.Add(colRows[j][q], p, colVals[j][q]); bt.Add(p, colRows[j][q], colVals[j][q]); }
            }
            _lu = SparseLu.Factor(bb.Build());
            _luT = SparseLu.Factor(bt.Build());
            if (_lu is null || _luT is null) return false;
            var r = (double[])b.Clone();
            for (int j = 0; j < nt; j++)
            {
                if (posOf[j] >= 0 || x[j] == 0) continue;
                for (int q = 0; q < colRows[j].Count; q++) r[colRows[j][q]] -= colVals[j][q] * x[j];
            }
            double[] xb = _lu.Solve(r);
            for (int p = 0; p < m; p++) x[basis[p]] = xb[p];
            return true;
        }

        /// <summary>B^-1 a_j (FTRAN): the base factors, then the etas in order.</summary>
        private double[] Ftran(int j)
        {
            var v = new double[m];
            for (int q = 0; q < colRows[j].Count; q++) v[colRows[j][q]] = colVals[j][q];
            v = _lu!.Solve(v);
            foreach ((int pos, double[] alpha) in _etas)
            {
                double vp = v[pos] / alpha[pos];
                if (vp != 0)
                    for (int i = 0; i < m; i++) if (i != pos) v[i] -= alpha[i] * vp;
                v[pos] = vp;
            }
            return v;
        }

        /// <summary>y = B^-T c_B (BTRAN): the etas transposed in reverse order, then the base factors of B^T.</summary>
        public double[] Duals(double[] cost)
        {
            var v = new double[m];
            for (int p = 0; p < m; p++) v[p] = cost[basis[p]];
            for (int e = _etas.Count - 1; e >= 0; e--)
            {
                (int pos, double[] alpha) = _etas[e];
                double s = v[pos];
                for (int i = 0; i < m; i++) if (i != pos) s -= alpha[i] * v[i];
                v[pos] = s / alpha[pos];
            }
            return m == 0 ? v : _luT!.Solve(v);
        }

        /// <summary>Simplex iterations for <paramref name="cost"/>. Artificial columns (index &gt;= nReal) may enter only
        /// when <paramref name="allowArtificialEntry"/>.</summary>
        public SparseSimplexStatus Run(double[] cost, int nReal, double tol, int maxIterations, ref int iterations, double[] b, bool allowArtificialEntry)
        {
            int degenerateRun = 0;
            while (true)
            {
                if (iterations >= maxIterations) return SparseSimplexStatus.IterationCap;
                double[] y = Duals(cost);
                bool bland = degenerateRun >= DegenerateRunLimit;

                // Pricing.
                int enter = -1;
                double enterDir = 0, best = 0;
                int jEnd = allowArtificialEntry ? nt : nReal;
                for (int j = 0; j < jEnd; j++)
                {
                    if (posOf[j] >= 0 || lo[j] == hi[j]) continue;
                    double d = cost[j];
                    for (int q = 0; q < colRows[j].Count; q++) d -= y[colRows[j][q]] * colVals[j][q];
                    bool atLo = double.IsFinite(lo[j]) && x[j] <= lo[j];
                    bool atHi = double.IsFinite(hi[j]) && x[j] >= hi[j];
                    double dir = 0;
                    if (d < -tol && !atHi) dir = 1;          // increasing lowers the cost
                    else if (d > tol && !atLo) dir = -1;     // decreasing lowers the cost
                    if (dir == 0) continue;
                    if (bland) { enter = j; enterDir = dir; break; }
                    if (Math.Abs(d) > best) { best = Math.Abs(d); enter = j; enterDir = dir; }
                }
                if (enter < 0) return SparseSimplexStatus.Optimal;

                double[] alpha = Ftran(enter);

                // Ratio test: the entering column moves by t in direction enterDir; basic p moves by -t enterDir alpha_p.
                double tMax = hi[enter] - lo[enter];   // the bound flip (infinite if either bound is)
                int leavePos = -1;
                bool leaveToHi = false;
                for (int p = 0; p < m; p++)
                {
                    double rate = enterDir * alpha[p];   // basic value falls at this rate
                    if (Math.Abs(alpha[p]) < PivotTolerance) continue;
                    int jb = basis[p];
                    double t;
                    bool toHi;
                    if (rate > 0)
                    {
                        if (!double.IsFinite(lo[jb])) continue;
                        t = Math.Max(0, (x[jb] - lo[jb]) / rate); toHi = false;
                    }
                    else
                    {
                        if (!double.IsFinite(hi[jb])) continue;
                        t = Math.Max(0, (hi[jb] - x[jb]) / -rate); toHi = true;
                    }
                    if (t < tMax || (t == tMax && leavePos >= 0 && (bland ? jb < basis[leavePos] : p < leavePos)))
                    {
                        tMax = t; leavePos = p; leaveToHi = toHi;
                    }
                }
                if (double.IsPositiveInfinity(tMax)) return SparseSimplexStatus.Unbounded;

                iterations++;
                degenerateRun = tMax == 0 ? degenerateRun + 1 : 0;

                // Move.
                if (tMax != 0)
                {
                    x[enter] += enterDir * tMax;
                    for (int p = 0; p < m; p++) if (alpha[p] != 0) x[basis[p]] -= enterDir * tMax * alpha[p];
                }
                if (leavePos < 0)
                {
                    // Bound flip: the entering column lands exactly on its other bound; the basis is unchanged.
                    x[enter] = enterDir > 0 ? hi[enter] : lo[enter];
                    continue;
                }
                int leave = basis[leavePos];
                x[leave] = leaveToHi ? hi[leave] : lo[leave];
                basis[leavePos] = enter;
                posOf[enter] = leavePos;
                posOf[leave] = -1;
                _etas.Add((leavePos, alpha));
                if (_etas.Count >= RefactorInterval && !Refactor(b)) return SparseSimplexStatus.NumericalFailure;
            }
        }

        /// <summary>After phase one: replace each artificial still basic (at zero) by a real column with a nonzero in its
        /// row of B^-1 A, if one exists (a degenerate pivot, no value moves).</summary>
        public bool DriveOutArtificials(int nReal, double[] b)
        {
            for (int p = 0; p < m; p++)
            {
                if (basis[p] < nReal) continue;
                // Row p of B^-1: e_p' B^-1, i.e. B^-T e_p.
                var unit = new double[m];
                unit[p] = 1;
                double[] row = Btran(unit);
                int pick = -1;
                double pickVal = 0;
                for (int j = 0; j < nReal; j++)
                {
                    if (posOf[j] >= 0) continue;
                    double v = 0;
                    for (int q = 0; q < colRows[j].Count; q++) v += row[colRows[j][q]] * colVals[j][q];
                    if (Math.Abs(v) > Math.Max(PivotTolerance, Math.Abs(pickVal))) { pick = j; pickVal = v; }
                }
                if (pick < 0) continue;   // a redundant row: its artificial stays basic, held at zero
                double[] alpha = Ftran(pick);
                int art = basis[p];
                basis[p] = pick;
                posOf[pick] = p;
                posOf[art] = -1;
                x[art] = 0;
                _etas.Add((p, alpha));
                if (_etas.Count >= RefactorInterval && !Refactor(b)) return false;
            }
            return Refactor(b);
        }

        private double[] Btran(double[] v0)
        {
            var v = (double[])v0.Clone();
            for (int e = _etas.Count - 1; e >= 0; e--)
            {
                (int pos, double[] alpha) = _etas[e];
                double s = v[pos];
                for (int i = 0; i < m; i++) if (i != pos) s -= alpha[i] * v[i];
                v[pos] = s / alpha[pos];
            }
            return _luT!.Solve(v);
        }
    }
}
