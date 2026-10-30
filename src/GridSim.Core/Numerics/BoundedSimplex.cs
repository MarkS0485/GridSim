namespace GridSim.Core.Numerics;

/// <summary>The terminal state of a linear program solved by <see cref="BoundedSimplex"/>.</summary>
public enum LpStatus
{
    /// <summary>An optimal vertex was found.</summary>
    Optimal,
    /// <summary>No point satisfies the equalities within the bounds.</summary>
    Infeasible,
    /// <summary>The objective decreases without bound along a feasible ray.</summary>
    Unbounded,
    /// <summary>The iteration cap was hit before optimality (numerical stall).</summary>
    IterationLimit,
}

/// <summary>
/// A dense two-phase primal simplex for the bounded-variable linear program
///   minimise c.x   subject to   A x = b,   lo &lt;= x &lt;= hi   (any hi may be +inf).
///
/// Variable bounds are handled natively (nonbasic variables rest at a finite bound; the ratio test
/// includes each entering variable's own opposite bound, so a step can be a bound flip with no basis
/// change) rather than expanded into extra rows - the standard bounded-variable method. Phase I drives a
/// set of artificial variables to zero to find a feasible basis (rows are pre-negated so every initial
/// residual is non-negative and the artificials form an identity basis); Phase II then optimises the real
/// objective. The basis inverse is carried by eta updates and refactorised periodically to bound
/// round-off. Dense and self-contained (no third-party solver), sized for the moderate LPs the analysis
/// layer builds - e.g. the security-constrained DC-OPF (one balance row plus a thermal row per rated
/// line).
/// </summary>
public static class BoundedSimplex
{
    // ---- Named constants (October 2026 review, WS13 R180). NAMING ONLY: every value is exactly the literal it
    // ---- replaced. These numbers steer the pivot path (which column enters, which row leaves, when the basis is
    // ---- refactorised), so changing any of them changes which vertex ties are broken how and can move the
    // ---- reported optimum in its last digits (or pick a different optimal vertex of a degenerate LP). The
    // ---- review's suggestions (derive them from tol; scale the Phase I test by max(1, ||b||_inf); a relative
    // ---- pivot floor) are therefore left for Mark.

    /// <summary>Default reduced-cost / feasibility tolerance of <see cref="Solve"/>.</summary>
    public const double DefaultTolerance = 1e-9;

    /// <summary>Default simplex iteration cap of <see cref="Solve"/>.</summary>
    public const int DefaultMaxIterations = 20000;

    /// <summary>Phase I ends feasible when the sum of artificial variables is at or below this. ABSOLUTE (not
    /// scaled by the right-hand side), see the class note.</summary>
    public const double PhaseOneFeasibilityTolerance = 1e-7;

    /// <summary>Number of eta (product-form) updates between full refactorisations of the basis inverse; bounds
    /// the round-off the eta file accumulates.</summary>
    public const int RefactorInterval = 64;

    /// <summary>Ratio-test tie margin: a leaving-row candidate replaces the current one only when its step is
    /// smaller by more than this, so among (near-)ties the FIRST row in index order is kept (Bland-like, and
    /// deterministic).</summary>
    public const double RatioTieMargin = 1e-12;

    /// <summary>The smallest pivot magnitude (absolute) the simplex will accept. N112 (October 2026): a chosen pivot below
    /// this is REFUSED (<see cref="RequirePivotAboveFloor"/> throws) before the basis changes. It used to be accepted, the
    /// basis changed, and the near-singular basis was then refactorised from scratch, so the LP carried on from a basis
    /// whose inverse was numerically meaningless. The ratio test only takes a row whose |alpha| exceeds the tolerance, so
    /// with any tolerance at or above this floor (the default 1e-9, and every GridSim caller) the refusal cannot happen and
    /// every pivot path is exactly what it was.</summary>
    public const double EtaPivotFloor = 1e-12;

    /// <summary>During refactorisation, a pivot smaller than this in magnitude (absolute) is clamped to
    /// +/-this value (keeping its sign) so the Gauss-Jordan inverse stays finite.</summary>
    public const double RefactorPivotClamp = 1e-14;

    // ---- N240 (October 2026, ruled by main: a fixed, documented tie-break so a degenerate optimum does not depend on
    // ---- summation order). The margin is OFF (0) by default, so every existing caller runs the exact code it ran
    // ---- before, statement for statement; the DC-OPF turns it on (DcOptimalPowerFlow builds its LPs with it).
    //
    // Why: an LP whose optimum is not unique (for example a DC-OPF where every generator costs the same) has many optimal
    // vertices, and which one the simplex stops at is decided by the pivot path. Where two entering columns are
    // MATHEMATICALLY tied, the strict "most negative reduced cost, first index on an exact tie" rule lets the last bit of
    // a sum decide between them, so a change in summation order that moves PTDF entries in their last places could move
    // tens or hundreds of MW between equal-cost units (pglib case588: 44.06 MW between gen 64 and gen 70 at identical
    // cost bits; measured: PTDF entries scaled by random factors within 1e-13 move case588's strict dispatch by up to
    // 251.7 MW in 17 of 20 trials and case1354pegase's by 1,333.3 MW in 20 of 20). With the margin on, reduced costs
    // that differ only by round-off count as tied and the LOWEST index wins, so the choice no longer depends on the last
    // bits (the same trials move neither by more than 6.5e-10 MW). The ratio test needs no change: it already keeps the
    // first row among steps within RatioTieMargin, and a relative ratio margin was measured to add no robustness.

    /// <summary>N240: the DC-OPF's pricing tie margin (relative). With a margin above 0, after the usual Dantzig scan has
    /// found the best score S (the largest improving |reduced cost|), the entering column is the LOWEST-index eligible
    /// column whose score is at least S - margin * S: reduced costs within this relative distance of the best count as
    /// tied and the first column in index order wins. 1e-10 is far above the round-off of a reduced cost (a few units
    /// in the last place of its terms) and far below any real difference in cost between two units.</summary>
    public const double DcOpfPricingTieMargin = 1e-10;

    /// <summary>N262 (October 2026, ruled: the N240 rule for every BoundedSimplex user): the pricing tie margin the other
    /// two GridSim callers pass, the contingency corrective redispatch (ContingencyAnalysis.PlanCorrectiveRedispatch: its
    /// minimum-change LP costs every MW moved the same, so it is degenerate by construction) and the storage LP scheduler
    /// (StorageLpScheduler: a flat or repeating price makes many schedules optimal). The same relative 1e-10 as the
    /// DC-OPF, so one rule, one number, holds for every LP GridSim solves. Where no pivot meets a near-tie the solve is
    /// statement for statement the strict one (proved per caller in Doc16NumericsDeterminismTests).</summary>
    public const double NearTiePricingMargin = DcOpfPricingTieMargin;

    /// <summary>N262: a record of one LP solved inside a <see cref="CaptureScope"/> (tests only): copies of the inputs as
    /// given, the options, and what the solve returned, including how many pivots the tie margin decided.</summary>
    internal sealed record CapturedLp(double[,] A, double[] B, double[] C, double[] Lo, double[] Hi, double Tol, int MaxIter,
        double PricingTieMargin, LpStatus Status, double[] X, int TieBreaks);

    /// <summary>N262: the capture list of the current async flow (null: nothing is recorded; production never sets it).</summary>
    private static readonly AsyncLocal<List<CapturedLp>?> CaptureFlow = new();

    /// <summary>N262 (tests only): record every LP <see cref="Solve"/> runs on the current async flow into
    /// <paramref name="sink"/> until the returned scope is disposed, so a test can re-solve exactly the LPs a caller built
    /// (with the margin on and off, or with perturbed data). Reading the inputs changes nothing in the solve.</summary>
    internal static IDisposable CaptureScope(List<CapturedLp> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        List<CapturedLp>? previous = CaptureFlow.Value;
        CaptureFlow.Value = sink;
        return new RestoreCapture(previous);
    }

    private sealed class RestoreCapture(List<CapturedLp>? previous) : IDisposable
    {
        public void Dispose() => CaptureFlow.Value = previous;
    }


    /// <summary>Solve the bounded-variable LP. <paramref name="a"/> is the m x n equality matrix,
    /// <paramref name="b"/> the m right-hand sides, <paramref name="c"/> the n objective coefficients,
    /// and <paramref name="lo"/>/<paramref name="hi"/> the per-variable bounds (an entry of
    /// <see cref="double.PositiveInfinity"/> in <paramref name="hi"/> means unbounded above). Returns the
    /// status, the solution x (best point found), and the objective c.x at it.</summary>
    /// <param name="pricingTieMargin">N240: the relative pricing tie margin (0, the default, is the strict rule every
    /// caller used before; the DC-OPF passes <see cref="DcOpfPricingTieMargin"/>, and since N262 the corrective redispatch
    /// and the storage scheduler pass <see cref="NearTiePricingMargin"/>, the same value).</param>
    public static (LpStatus Status, double[] X, double Objective) Solve(
        double[,] a, double[] b, double[] c, double[] lo, double[] hi,
        double tol = DefaultTolerance, int maxIter = DefaultMaxIterations,
        double pricingTieMargin = 0.0)
    {
        (LpStatus status, double[] x, double objective, int tieBreaks) = SolveCounted(a, b, c, lo, hi, tol, maxIter, pricingTieMargin);
        List<CapturedLp>? sink = CaptureFlow.Value;
        if (sink is not null)
        {
            // N262 test capture: copies, so a later change to the caller's arrays cannot alter the record.
            var rec = new CapturedLp((double[,])a.Clone(), (double[])b.Clone(), (double[])c.Clone(), (double[])lo.Clone(),
                (double[])hi.Clone(), tol, maxIter, pricingTieMargin, status, (double[])x.Clone(), tieBreaks);
            lock (sink) sink.Add(rec);
        }
        return (status, x, objective);
    }

    /// <summary>N262: <see cref="Solve"/>, also returning how many pivots the pricing tie margin decided (the entering
    /// column it chose differs from the strict Dantzig choice). 0 means the pivot path, and therefore every returned bit,
    /// is exactly the strict rule's: the margin only ever replaces the strict choice, it never changes any arithmetic.</summary>
    internal static (LpStatus Status, double[] X, double Objective, int TieBreaks) SolveCounted(
        double[,] a, double[] b, double[] c, double[] lo, double[] hi,
        double tol, int maxIter, double pricingTieMargin)
    {
        int tieBreaks = 0;
        // N112 (October 2026): a tolerance that is not a positive finite number would make every comparison below false
        // (NaN) or meaningless; refuse it by name rather than return a status computed from it.
        if (!(tol > 0) || !double.IsFinite(tol))
            throw new ArgumentOutOfRangeException(nameof(tol), tol, "BoundedSimplex: the tolerance must be a positive finite number.");
        CheckTieMargin(pricingTieMargin);
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        int total = n + m; // structural/slack variables then one artificial per row
        ValidateInputs(a, b, c, lo, hi, m, n);

        // Working copies so rows can be negated for a non-negative initial residual.
        var A = new double[m, total];
        var rhs = new double[m];
        for (int i = 0; i < m; i++) { rhs[i] = b[i]; for (int j = 0; j < n; j++) A[i, j] = a[i, j]; }

        var loX = new double[total];
        var hiX = new double[total];
        var cost = new double[total];       // phase-II objective (artificials cost 0, pinned to 0)
        var phase1 = new double[total];     // phase-I objective (artificials cost 1)
        var val = new double[total];        // current value of every variable
        var atUpper = new bool[total];      // a nonbasic variable rests at its upper (else lower) bound
        for (int j = 0; j < n; j++)
        {
            loX[j] = lo[j]; hiX[j] = hi[j]; cost[j] = c[j];
            // Rest each nonbasic structural variable at a finite bound (prefer the lower).
            if (!double.IsInfinity(lo[j])) { val[j] = lo[j]; atUpper[j] = false; }
            else if (!double.IsInfinity(hi[j])) { val[j] = hi[j]; atUpper[j] = true; }
            else { val[j] = 0.0; atUpper[j] = false; }
        }

        // Initial residual with structural variables at their resting bound; negate rows to make it >= 0.
        for (int i = 0; i < m; i++)
        {
            double r = rhs[i];
            for (int j = 0; j < n; j++) r -= A[i, j] * val[j];
            if (r < 0) { for (int j = 0; j < n; j++) A[i, j] = -A[i, j]; rhs[i] = -rhs[i]; r = -r; }
            int art = n + i;
            A[i, art] = 1.0;
            loX[art] = 0.0; hiX[art] = double.PositiveInfinity;
            cost[art] = 0.0; phase1[art] = 1.0;
            val[art] = r; atUpper[art] = false;
        }

        var basis = new int[m];
        var inBasis = new bool[total];
        for (int i = 0; i < m; i++) { basis[i] = n + i; inBasis[n + i] = true; }
        var bInv = Identity(m);

        // ---- Phase I: minimise the sum of artificials. ----
        LpStatus s1 = RunSimplex(A, rhs, phase1, loX, hiX, val, atUpper, basis, inBasis, bInv, m, total, n, tol, maxIter, phaseOne: true,
            pricingTieMargin, ref tieBreaks);
        if (s1 == LpStatus.IterationLimit) return (LpStatus.IterationLimit, Extract(val, n), Dot(c, val, n), tieBreaks);

        double artSum = 0;
        for (int i = 0; i < m; i++) if (basis[i] >= n) artSum += Math.Abs(val[basis[i]]);
        for (int j = n; j < total; j++) if (!inBasis[j]) artSum += Math.Abs(val[j]);
        // Written so a NaN sum reads as infeasible (NaN > tol is false, so the old form let it through to Phase II).
        if (!(artSum <= PhaseOneFeasibilityTolerance)) return (LpStatus.Infeasible, Extract(val, n), Dot(c, val, n), tieBreaks);

        // Pin every artificial to zero so Phase II can never reintroduce infeasibility, then optimise c.
        for (int j = n; j < total; j++) { loX[j] = 0.0; hiX[j] = 0.0; if (!inBasis[j]) { val[j] = 0.0; atUpper[j] = false; } }
        LpStatus s2 = RunSimplex(A, rhs, cost, loX, hiX, val, atUpper, basis, inBasis, bInv, m, total, n, tol, maxIter, phaseOne: false,
            pricingTieMargin, ref tieBreaks);

        double[] xOut = Extract(val, n);
        // An Optimal answer is checked against the problem it claims to solve: A x = b and lo <= x <= hi to a scaled
        // tolerance. The basic values come from up to 63 eta updates since the last refactorisation, and nothing used
        // to look at them before Optimal went out; a drifted basis now fails loud instead of shipping a point that
        // breaks its own constraints. The point itself is not touched, so a passing solve is bit-for-bit as before.
        if (s2 == LpStatus.Optimal) VerifyOptimalPoint(a, b, lo, hi, xOut, m, n);
        return (s2, xOut, Dot(c, val, n), tieBreaks);
    }

    /// <summary>Relative tolerance of the post-solve check of an Optimal point (residual and bounds).</summary>
    internal const double OptimalPointRelativeTolerance = 1e-6;

    // Fail loud on malformed input instead of solving it: a NaN or infinite coefficient, right-hand side or cost, a NaN
    // bound, an empty interval (lo > hi), or a bound that admits no finite value (lo = +inf, hi = -inf). A NaN in b used
    // to become a NaN artificial that never limited a step and never failed Phase I, so the LP came back Optimal.
    private static void ValidateInputs(double[,] a, double[] b, double[] c, double[] lo, double[] hi, int m, int n)
    {
        if (b.Length != m || c.Length != n || lo.Length != n || hi.Length != n)
            throw new ArgumentException($"BoundedSimplex: inconsistent sizes (A {m} x {n}, b {b.Length}, c {c.Length}, lo {lo.Length}, hi {hi.Length}).");
        for (int i = 0; i < m; i++)
        {
            if (!double.IsFinite(b[i])) throw new ArgumentException($"BoundedSimplex: b[{i}] = {b[i]} is not finite.", nameof(b));
            for (int j = 0; j < n; j++)
                if (!double.IsFinite(a[i, j])) throw new ArgumentException($"BoundedSimplex: A[{i},{j}] = {a[i, j]} is not finite.", nameof(a));
        }
        for (int j = 0; j < n; j++)
        {
            if (!double.IsFinite(c[j])) throw new ArgumentException($"BoundedSimplex: c[{j}] = {c[j]} is not finite.", nameof(c));
            if (double.IsNaN(lo[j]) || double.IsNaN(hi[j]))
                throw new ArgumentException($"BoundedSimplex: variable {j} has a NaN bound.", nameof(lo));
            if (lo[j] > hi[j])
                throw new ArgumentException($"BoundedSimplex: variable {j} has lo {lo[j]} above hi {hi[j]}.", nameof(lo));
            if (double.IsPositiveInfinity(lo[j]) || double.IsNegativeInfinity(hi[j]))
                throw new ArgumentException($"BoundedSimplex: variable {j} has a bound that admits no finite value.", nameof(lo));
        }
    }

    private static void VerifyOptimalPoint(double[,] a, double[] b, double[] lo, double[] hi, double[] x, int m, int n)
    {
        for (int i = 0; i < m; i++)
        {
            double ax = 0.0, scale = Math.Abs(b[i]);
            for (int j = 0; j < n; j++) { double t = a[i, j] * x[j]; ax += t; scale += Math.Abs(t); }
            double bound = OptimalPointRelativeTolerance * Math.Max(1.0, scale);
            if (!(Math.Abs(ax - b[i]) <= bound))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"BoundedSimplex: the Optimal point breaks row {i} (|A x - b| = {Math.Abs(ax - b[i]):R}, allowed {bound:R}); the basis drifted."));
        }
        for (int j = 0; j < n; j++)
        {
            double slack = OptimalPointRelativeTolerance * Math.Max(1.0, Math.Abs(x[j]));
            if (!(x[j] >= lo[j] - slack) || !(x[j] <= hi[j] + slack))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"BoundedSimplex: the Optimal point puts variable {j} at {x[j]:R}, outside [{lo[j]:R}, {hi[j]:R}]; the basis drifted."));
        }
    }

    /// <summary>One phase of the bounded-variable primal simplex, mutating the basis/values in place.</summary>
    private static LpStatus RunSimplex(
        double[,] A, double[] rhs, double[] obj, double[] loX, double[] hiX, double[] val, bool[] atUpper,
        int[] basis, bool[] inBasis, double[,] bInv, int m, int total, int nStruct, double tol, int maxIter, bool phaseOne,
        double pricingTieMargin, ref int tieBreaks)
    {
        var alpha = new double[m];
        var y = new double[m];
        int sinceRefactor = 0;

        for (int iter = 0; iter < maxIter; iter++)
        {
            if (sinceRefactor >= RefactorInterval) { Refactorize(A, basis, bInv, m); RecomputeBasics(A, rhs, loX, hiX, val, atUpper, basis, bInv, m, total); sinceRefactor = 0; }

            // Dual multipliers y = cB . Binv.
            for (int j = 0; j < m; j++)
            {
                double sum = 0;
                for (int i = 0; i < m; i++) sum += obj[basis[i]] * bInv[i, j];
                y[j] = sum;
            }

            // Price the nonbasic variables; pick the most promising (Dantzig) improving direction.
            int q = -1; double best = tol; int qDir = 0;
            for (int j = 0; j < total; j++)
            {
                if (inBasis[j]) continue;
                if (loX[j] == hiX[j]) continue;                     // fixed variable: never enters
                double d = obj[j];
                for (int i = 0; i < m; i++) d -= y[i] * A[i, j];
                // A FREE nonbasic variable (both bounds infinite) can improve the objective by moving
                // in EITHER direction - optimality requires its reduced cost to be exactly 0. Pricing
                // only the increase move let a free variable with d > 0 (improves by decreasing) be
                // skipped, so the solver returned Optimal at a non-optimal / unbounded vertex.
                bool free = double.IsInfinity(loX[j]) && double.IsInfinity(hiX[j]);
                if ((!atUpper[j] || free) && d < -best) { best = -d; q = j; qDir = +1; }   // increase from lower (or free)
                else if ((atUpper[j] || free) && d > best) { best = d; q = j; qDir = -1; }  // decrease from upper (or free)
            }
            if (q < 0) return LpStatus.Optimal;

            // N240: with a pricing tie margin, the lowest-index column whose score is within the margin of the best wins.
            // Only the columns before q can (every column after q scored no more than best, and a column before q scored
            // strictly less, or it would have been kept). Skipped entirely at margin 0, so the strict rule is untouched.
            if (pricingTieMargin > 0)
            {
                double floor = best - pricingTieMargin * best;
                for (int j = 0; j < q; j++)
                {
                    if (inBasis[j]) continue;
                    if (loX[j] == hiX[j]) continue;
                    double d = obj[j];
                    for (int i = 0; i < m; i++) d -= y[i] * A[i, j];
                    (double score, int dir) = PricingScore(d, atUpper[j], double.IsInfinity(loX[j]) && double.IsInfinity(hiX[j]), tol);
                    if (dir != 0 && score >= floor) { q = j; qDir = dir; tieBreaks++; break; }   // N262: counted
                }
            }

            // Entering column in the basis metric: alpha = Binv . A[:,q].
            for (int i = 0; i < m; i++)
            {
                double sum = 0;
                for (int k = 0; k < m; k++) sum += bInv[i, k] * A[k, q];
                alpha[i] = sum;
            }

            // Ratio test. dir = qDir: the entering value moves by qDir*t (t>=0); each basic value moves by
            // -qDir*t*alpha_i. Limit t by (a) each basic variable's near bound and (b) the entering
            // variable's own opposite bound (a bound flip keeps the basis unchanged).
            double tMax = double.PositiveInfinity; int leave = -1; bool leaveToUpper = false;
            double gap = hiX[q] - loX[q];
            if (!double.IsInfinity(gap)) tMax = gap;

            for (int i = 0; i < m; i++)
            {
                double rate = qDir * alpha[i]; // basic i changes by -rate*t
                int bi = basis[i];
                if (rate > tol)
                {
                    double room = val[bi] - loX[bi]; // decreasing toward its lower bound
                    double t = room / rate;
                    if (t < tMax - RatioTieMargin) { tMax = t; leave = i; leaveToUpper = false; }
                }
                else if (rate < -tol)
                {
                    double hib = hiX[bi];
                    if (double.IsInfinity(hib)) continue; // unbounded above: no limit from this row
                    double room = hib - val[bi];         // increasing toward its upper bound
                    double t = room / -rate;
                    if (t < tMax - RatioTieMargin) { tMax = t; leave = i; leaveToUpper = true; }
                }
            }

            if (double.IsInfinity(tMax)) return LpStatus.Unbounded;
            if (tMax < 0) tMax = 0;

            // N112: refuse a pivot below the eta floor BEFORE the step and the basis change (see EtaPivotFloor).
            if (leave >= 0) RequirePivotAboveFloor(alpha[leave], tol, nameof(BoundedSimplex));

            // Apply the step to all basic variables and to the entering variable.
            for (int i = 0; i < m; i++) val[basis[i]] -= qDir * tMax * alpha[i];
            val[q] += qDir * tMax;

            if (leave < 0)
            {
                // Bound flip: the entering variable reached its opposite bound; basis is unchanged.
                atUpper[q] = !atUpper[q];
                val[q] = atUpper[q] ? hiX[q] : loX[q];
                sinceRefactor++;
                continue;
            }

            // Pivot: q enters, basis[leave] exits to the bound it reached.
            int outVar = basis[leave];
            inBasis[outVar] = false; atUpper[outVar] = leaveToUpper;
            val[outVar] = leaveToUpper ? hiX[outVar] : loX[outVar];
            basis[leave] = q; inBasis[q] = true;

            // Eta update of Binv for the new basis (pivot element alpha[leave], at or above the floor: checked above).
            double piv = alpha[leave];
            for (int j = 0; j < m; j++) bInv[leave, j] /= piv;
            for (int i = 0; i < m; i++)
            {
                if (i == leave) continue;
                double f = alpha[i];
                if (f == 0) continue;
                for (int j = 0; j < m; j++) bInv[i, j] -= f * bInv[leave, j];
            }
            sinceRefactor++;
        }
        return LpStatus.IterationLimit;
    }

    /// <summary>N240: the improving score and direction of a nonbasic column with reduced cost <paramref name="d"/>, by
    /// exactly the eligibility rule of the Dantzig scan: increasing from its lower bound (or free) when d &lt; -tol, score
    /// -d, direction +1; else decreasing from its upper bound (or free) when d &gt; tol, score d, direction -1; else not
    /// eligible (direction 0). Shared with the structured security simplex.</summary>
    internal static (double Score, int Dir) PricingScore(double d, bool atUpper, bool free, double tol)
    {
        if ((!atUpper || free) && d < -tol) return (-d, +1);
        if ((atUpper || free) && d > tol) return (d, -1);
        return (0.0, 0);
    }

    /// <summary>N240: the pricing tie margin must be in [0, 1e-3] (it is a round-off allowance, not a tolerance on cost);
    /// anything else (negative, NaN, infinite, too large) is refused by name.</summary>
    internal static void CheckTieMargin(double pricingTieMargin)
    {
        if (!(pricingTieMargin >= 0 && pricingTieMargin <= 1e-3))
            throw new ArgumentOutOfRangeException(nameof(pricingTieMargin), pricingTieMargin, "BoundedSimplex: the pricing tie margin must be in [0, 1e-3].");
    }

    /// <summary>
    /// N112 (October 2026): refuse a pivot whose magnitude is below <see cref="EtaPivotFloor"/>, throwing
    /// <see cref="InvalidOperationException"/> naming the solver, the pivot and the tolerance. Shared by this solver and the
    /// structured security simplex (DcOpfSecuritySimplex), which follow the same pivot rules. Never trips while the
    /// tolerance is at or above the floor: the ratio test admits only rows with |alpha| above the tolerance.
    /// </summary>
    internal static void RequirePivotAboveFloor(double pivot, double tol, string solver)
    {
        if (Math.Abs(pivot) >= EtaPivotFloor) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        throw new InvalidOperationException(
            $"{solver}: the ratio test chose a pivot of magnitude {Math.Abs(pivot).ToString("E3", inv)}, below the eta floor " +
            $"{EtaPivotFloor.ToString("E0", inv)} (tolerance {tol.ToString("E3", inv)}). The basis it would make is numerically singular, so the LP " +
            "is refused rather than refactorised around it; a tolerance at or above the floor cannot choose such a pivot.");
    }

    private static double[,] Identity(int m)
    {
        var e = new double[m, m];
        for (int i = 0; i < m; i++) e[i, i] = 1.0;
        return e;
    }

    /// <summary>Rebuild Binv from the current basis columns by Gauss-Jordan (bounds round-off drift).</summary>
    private static void Refactorize(double[,] A, int[] basis, double[,] bInv, int m)
    {
        var b = new double[m, m];
        for (int i = 0; i < m; i++) for (int k = 0; k < m; k++) b[i, k] = A[i, basis[k]];
        for (int i = 0; i < m; i++) for (int j = 0; j < m; j++) bInv[i, j] = i == j ? 1.0 : 0.0;

        for (int col = 0; col < m; col++)
        {
            int piv = col; double bestAbs = Math.Abs(b[col, col]);
            for (int r = col + 1; r < m; r++) { double v = Math.Abs(b[r, col]); if (v > bestAbs) { bestAbs = v; piv = r; } }
            if (piv != col) { SwapRows(b, col, piv, m); SwapRows(bInv, col, piv, m); }
            double d = b[col, col];
            // A basis singular to working precision is a failure, not a pivot to clamp: replacing the pivot by +/-1e-14
            // and carrying on built a meaningless inverse (values amplified 1e14) that could still end Optimal.
            if (!(Math.Abs(d) >= RefactorPivotClamp))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"BoundedSimplex: the basis is singular at refactorisation (pivot {d:R} in column {col}); no reliable solution."));
            for (int j = 0; j < m; j++) { b[col, j] /= d; bInv[col, j] /= d; }
            for (int r = 0; r < m; r++)
            {
                if (r == col) continue;
                double f = b[r, col];
                if (f == 0) continue;
                for (int j = 0; j < m; j++) { b[r, j] -= f * b[col, j]; bInv[r, j] -= f * bInv[col, j]; }
            }
        }
    }

    /// <summary>Recompute basic variable values from Binv.(rhs - A_N x_N) for the current nonbasic set.</summary>
    private static void RecomputeBasics(
        double[,] A, double[] rhs, double[] loX, double[] hiX, double[] val, bool[] atUpper,
        int[] basis, double[,] bInv, int m, int total)
    {
        var inBasis = new bool[total];
        for (int i = 0; i < m; i++) inBasis[basis[i]] = true;
        var r = new double[m];
        for (int i = 0; i < m; i++)
        {
            double sum = rhs[i];
            for (int j = 0; j < total; j++) if (!inBasis[j]) sum -= A[i, j] * val[j];
            r[i] = sum;
        }
        for (int i = 0; i < m; i++)
        {
            double sum = 0;
            for (int k = 0; k < m; k++) sum += bInv[i, k] * r[k];
            val[basis[i]] = sum;
        }
    }

    private static void SwapRows(double[,] mtx, int a, int b, int m)
    {
        for (int j = 0; j < m; j++) { (mtx[a, j], mtx[b, j]) = (mtx[b, j], mtx[a, j]); }
    }

    private static double[] Extract(double[] val, int n)
    {
        var x = new double[n];
        Array.Copy(val, x, n);
        return x;
    }

    private static double Dot(double[] c, double[] val, int n)
    {
        double s = 0;
        for (int j = 0; j < n; j++) s += c[j] * val[j];
        return s;
    }
}
