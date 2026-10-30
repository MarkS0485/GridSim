using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// The DC-OPF with quadratic generator costs (F19, October 2026): a small, self-contained, deterministic convex
/// quadratic program solver, used only when at least one generator's cost curve has a non-zero quadratic term.
///
/// <para><b>The problem.</b> Variables x (one per generator, or per piecewise-linear segment of a generator). Minimise
/// <c>sum_j (h_j / 2) x_j^2 + c_j x_j</c> (h_j = 2 a_j &gt;= 0, a diagonal Hessian) subject to the balance
/// <c>sum_j x_j = D</c>, the bounds <c>lo_j &lt;= x_j &lt;= hi_j</c> and, for each rated line r, the thermal band
/// <c>Lower_r &lt;= p_r . x &lt;= Upper_r</c>, p_r being the line's PTDF entry at each variable's bus. This is the same
/// constraint set as the linear security LP (<see cref="DcOptimalPowerFlow"/>), with the quadratic terms added.</para>
///
/// <para><b>The method</b> is the textbook primal active-set method for convex QP (Nocedal and Wright, Numerical
/// Optimization, 2nd ed., Algorithm 16.3), arranged so that the Hessian may be only positive SEMI-definite (units with
/// linear costs have h_j = 0): it starts at a VERTEX (the optimal vertex of the linear part of the problem, found by
/// the existing simplex), where the working set fixes every variable, and it keeps the invariant that the working-set
/// KKT matrix <c>[H -A^T; A 0]</c> is non-singular (H positive definite on the working set's null space):</para>
/// <list type="number">
/// <item>Away from the working set's minimiser: solve the KKT system for the step p to that minimiser and take it,
/// shortened at the first constraint it would cross (which then joins the working set).</item>
/// <item>At the minimiser: the same solve gives the multipliers. If every inequality multiplier is &gt;= -tolerance
/// the point satisfies the KKT conditions of the whole convex problem and is optimal. Otherwise the most negative
/// multiplier's constraint j leaves: the direction d with <c>A d = e_j</c>, <c>H d = A^T nu</c> (the same KKT matrix,
/// another right-hand side) is the best direction off j, its curvature is <c>d' H d = nu_j</c> and the slope along it
/// is the multiplier, so the exact line minimum is <c>t = -lambda_j / nu_j</c>; with zero curvature (only linear-cost
/// units move) the step runs to the first blocking constraint, which always exists because every variable is boxed.
/// Moving along d keeps H positive definite on the new working set's null space (the curvature along d is
/// decoupled from the old null space because <c>z' H d = z' A^T nu = 0</c>), so the invariant holds.</item>
/// </list>
/// <para>Every choice is made in index order with strict comparisons, and only plain IEEE double arithmetic is used (no
/// fused multiply-add, no parallel loop), so the result is the same bits on every run and every CPU tier. The dense KKT
/// solve costs O((n + |W|)^3) per iteration, which is fine for the MATPOWER-sized cases quadratic costs come with
/// (case300: 69 units) and is the documented limit for very large fleets.</para>
/// </summary>
internal static class DcOpfQuadratic
{
    /// <summary>An inequality multiplier at or above minus this (cost per MWh) counts as non-negative: the optimality
    /// test. The same value as the simplex's reduced-cost tolerance (<see cref="BoundedSimplex.DefaultTolerance"/>).</summary>
    internal const double MultiplierTolerance = BoundedSimplex.DefaultTolerance;

    /// <summary>A constraint blocks a step only when the step moves towards it faster than this (per unit step).</summary>
    internal const double DirectionTolerance = 1e-12;

    /// <summary>Curvature along a drop direction at or below this counts as zero (the step then runs to a blocker).</summary>
    internal const double CurvatureTolerance = 1e-12;

    /// <summary>A start-up candidate constraint joins the working set only when the part of its normal not already
    /// spanned by the chosen ones is longer than this fraction of the normal (Gram-Schmidt independence test).</summary>
    internal const double IndependenceTolerance = 1e-10;

    /// <summary>A KKT pivot smaller than this fraction of the matrix's largest entry means the working-set system is
    /// singular, which the method's invariant rules out; the solve then stops and says so instead of guessing.</summary>
    internal const double SingularPivotTolerance = 1e-13;

    /// <summary>Iteration cap (each iteration adds or drops one constraint).</summary>
    internal const int MaxIterations = 20000;

    /// <summary>The final point must satisfy every bound, line limit and the balance within this (MW), or the solve
    /// is reported as failed rather than optimal.</summary>
    internal const double FeasibilityToleranceMw = 1e-6;

    /// <summary>The problem. <see cref="LineRows"/> is row-major, Lines x N.</summary>
    internal sealed record Problem(int N, double[] H, double[] C, double[] Lo, double[] Hi, double Demand,
        int Lines, double[] LineRows, double[] Upper, double[] Lower);

    /// <summary>The outcome. <see cref="LineDuals"/>[r] is the derivative of the optimal cost with respect to the
    /// right-hand side of line r written as in the linear security LP (lambda_lower - lambda_upper), so the nodal prices
    /// are formed exactly as from the simplex duals. <see cref="Failure"/> says why a non-Optimal status was returned.</summary>
    internal sealed record Result(LpStatus Status, double[] X, double BalanceMultiplier, double[] LineDuals, int Iterations, string? Failure);

    // Constraint ids (all of the form a . x >= b): lower bound j -> j; upper bound j -> N + j; line r upper -> 2N + r;
    // line r lower -> 2N + L + r. The balance equality is held separately and is always in the working set.

    /// <summary>
    /// Solve from the feasible vertex <paramref name="x0"/> (the linear part's simplex optimum). The line limits active
    /// there are given by <paramref name="upperActive"/> / <paramref name="lowerActive"/> (the simplex slack at exactly
    /// 0 or at exactly twice the rating); bounds are read from x0 (a variable exactly at a bound).
    /// </summary>
    internal static Result Solve(Problem p, double[] x0, bool[] upperActive, bool[] lowerActive)
    {
        int n = p.N, lines = p.Lines;
        int total = 2 * n + 2 * lines;
        var x = (double[])x0.Clone();
        var inW = new bool[total];
        var w = new List<int>();

        // ---- Start-up working set: the balance, then the active constraints in id order, each kept only if its normal
        // ---- is independent of those already chosen (modified Gram-Schmidt, two passes for stability).
        var basis = new List<double[]>();
        var a = new double[n];
        for (int j = 0; j < n; j++) a[j] = 1.0;
        TryAddIndependent(basis, a);
        var candidates = new List<int>();
        for (int j = 0; j < n; j++)
        {
            if (x[j] == p.Lo[j]) candidates.Add(j);
            else if (x[j] == p.Hi[j]) candidates.Add(n + j);
        }
        for (int r = 0; r < lines; r++)
        {
            if (upperActive[r]) candidates.Add(2 * n + r);
            else if (lowerActive[r]) candidates.Add(2 * n + lines + r);
        }
        foreach (int id in candidates)
        {
            if (basis.Count >= n) break;
            Normal(p, id, a);
            if (TryAddIndependent(basis, a)) { w.Add(id); inW[id] = true; }
        }
        // At a vertex the working set fixes every variable: the start point is that set's minimiser.
        bool atMinimiser = basis.Count == n;

        var g = new double[n];
        for (int iter = 1; iter <= MaxIterations; iter++)
        {
            int size = n + 1 + w.Count;
            var k = BuildKkt(p, w, size);
            if (!Factor(k, size, out int[] perm))
                return Fail(LpStatus.IterationLimit, x, lines, iter, "the active-set working system became singular");

            for (int j = 0; j < n; j++) g[j] = p.H[j] * x[j] + p.C[j];
            var rhs = new double[size];
            for (int j = 0; j < n; j++) rhs[j] = -g[j];
            double[] sol = SolveFactored(k, perm, size, rhs);

            double[] dir;
            double tFull;
            int dropped = -1;
            if (!atMinimiser)
            {
                // Step to the working set's minimiser.
                dir = new double[n];
                Array.Copy(sol, dir, n);
                tFull = 1.0;
            }
            else
            {
                // Multipliers: sol[n] the balance, sol[n + 1 + i] working constraint i. Most negative leaves.
                int leave = -1;
                double most = -MultiplierTolerance;
                for (int i = 0; i < w.Count; i++)
                {
                    double lam = sol[n + 1 + i];
                    if (lam < most) { most = lam; leave = i; }
                }
                if (leave < 0) return Finish(p, x, sol, w, iter);

                var rhs2 = new double[size];
                rhs2[n + 1 + leave] = 1.0;
                double[] sol2 = SolveFactored(k, perm, size, rhs2);
                dir = new double[n];
                Array.Copy(sol2, dir, n);
                double nu = sol2[n + 1 + leave];
                tFull = nu > CurvatureTolerance ? -most / nu : double.PositiveInfinity;
                dropped = leave;
            }

            // Ratio test over the inequality constraints outside the working set (the leaving one moves away: a.d = 1).
            double t = tFull;
            int block = -1;
            for (int id = 0; id < total; id++)
            {
                if (inW[id]) continue;
                double ad = Dot(p, id, dir);
                if (!(ad < -DirectionTolerance)) continue;
                double slack = Dot(p, id, x) - Rhs(p, id);
                if (slack < 0.0) slack = 0.0;   // rounding can leave a constraint a hair violated: no negative steps
                double tk = slack / -ad;
                if (tk < t) { t = tk; block = id; }
            }
            if (double.IsPositiveInfinity(t))
                return Fail(LpStatus.Unbounded, x, lines, iter, "a step direction met no bound (an unbounded variable)");

            for (int j = 0; j < n; j++) x[j] += t * dir[j];
            if (dropped >= 0) { inW[w[dropped]] = false; w.RemoveAt(dropped); }
            if (block >= 0)
            {
                w.Add(block); inW[block] = true;
                // A bound that blocks is met exactly (no drift off it from the step's rounding).
                if (block < n) x[block] = p.Lo[block];
                else if (block < 2 * n) x[block - n] = p.Hi[block - n];
                atMinimiser = false;
            }
            else atMinimiser = true;
        }
        return Fail(LpStatus.IterationLimit, x, lines, MaxIterations, $"no optimum within {MaxIterations} active-set iterations");
    }

    private static Result Fail(LpStatus status, double[] x, int lines, int iter, string why)
        => new(status, x, double.NaN, new double[lines], iter, why);

    /// <summary>Optimal: verify feasibility, then read the balance multiplier and the line duals off the solve.</summary>
    private static Result Finish(Problem p, double[] x, double[] sol, List<int> w, int iter)
    {
        int n = p.N, lines = p.Lines;
        double sum = 0.0;
        for (int j = 0; j < n; j++)
        {
            sum += x[j];
            if (x[j] < p.Lo[j] - FeasibilityToleranceMw || x[j] > p.Hi[j] + FeasibilityToleranceMw)
                return Fail(LpStatus.IterationLimit, x, lines, iter, $"variable {j} ended outside its bounds");
        }
        if (Math.Abs(sum - p.Demand) > FeasibilityToleranceMw * Math.Max(1.0, Math.Abs(p.Demand)))
            return Fail(LpStatus.IterationLimit, x, lines, iter, "the balance is not met at the end point");
        for (int r = 0; r < lines; r++)
        {
            double f = 0.0;
            for (int j = 0; j < n; j++) f += p.LineRows[r * n + j] * x[j];
            if (f > p.Upper[r] + FeasibilityToleranceMw || f < p.Lower[r] - FeasibilityToleranceMw)
                return Fail(LpStatus.IterationLimit, x, lines, iter, $"line row {r} ended outside its limits");
        }

        var y = new double[lines];
        for (int i = 0; i < w.Count; i++)
        {
            int id = w[i];
            double lam = sol[n + 1 + i];
            if (id >= 2 * n + lines) y[id - 2 * n - lines] += lam;   // lower line limit: +lambda
            else if (id >= 2 * n) y[id - 2 * n] -= lam;             // upper line limit: -lambda
        }
        return new Result(LpStatus.Optimal, x, sol[n], y, iter, null);
    }

    /// <summary>Gram-Schmidt independence test: add <paramref name="a"/> (normalised residual) when independent.</summary>
    private static bool TryAddIndependent(List<double[]> basis, double[] a)
    {
        int n = a.Length;
        double norm0 = 0.0;
        for (int j = 0; j < n; j++) norm0 += a[j] * a[j];
        norm0 = Math.Sqrt(norm0);
        if (norm0 == 0.0) return false;
        var v = (double[])a.Clone();
        for (int pass = 0; pass < 2; pass++)
            foreach (double[] q in basis)
            {
                double d = 0.0;
                for (int j = 0; j < n; j++) d += q[j] * v[j];
                for (int j = 0; j < n; j++) v[j] -= d * q[j];
            }
        double norm = 0.0;
        for (int j = 0; j < n; j++) norm += v[j] * v[j];
        norm = Math.Sqrt(norm);
        if (!(norm > IndependenceTolerance * norm0)) return false;
        for (int j = 0; j < n; j++) v[j] /= norm;
        basis.Add(v);
        return true;
    }

    /// <summary>Write constraint <paramref name="id"/>'s normal into <paramref name="a"/>.</summary>
    private static void Normal(Problem p, int id, double[] a)
    {
        int n = p.N, lines = p.Lines;
        Array.Clear(a);
        if (id < n) a[id] = 1.0;
        else if (id < 2 * n) a[id - n] = -1.0;
        else if (id < 2 * n + lines) { int r = id - 2 * n; for (int j = 0; j < n; j++) a[j] = -p.LineRows[r * n + j]; }
        else { int r = id - 2 * n - lines; for (int j = 0; j < n; j++) a[j] = p.LineRows[r * n + j]; }
    }

    /// <summary>a_id . v.</summary>
    private static double Dot(Problem p, int id, double[] v)
    {
        int n = p.N, lines = p.Lines;
        if (id < n) return v[id];
        if (id < 2 * n) return -v[id - n];
        double s = 0.0;
        if (id < 2 * n + lines) { int r = id - 2 * n; for (int j = 0; j < n; j++) s += p.LineRows[r * n + j] * v[j]; return -s; }
        int rl = id - 2 * n - lines;
        for (int j = 0; j < n; j++) s += p.LineRows[rl * n + j] * v[j];
        return s;
    }

    /// <summary>b_id (the constraint reads a_id . x &gt;= b_id).</summary>
    private static double Rhs(Problem p, int id)
    {
        int n = p.N, lines = p.Lines;
        if (id < n) return p.Lo[id];
        if (id < 2 * n) return -p.Hi[id - n];
        if (id < 2 * n + lines) return -p.Upper[id - 2 * n];
        return p.Lower[id - 2 * n - lines];
    }

    /// <summary>The working-set KKT matrix [H -A^T; A 0] (balance row first, then the working constraints in order).</summary>
    private static double[] BuildKkt(Problem p, List<int> w, int size)
    {
        int n = p.N;
        var k = new double[size * size];
        var a = new double[n];
        for (int j = 0; j < n; j++)
        {
            k[j * size + j] = p.H[j];
            k[j * size + n] = -1.0;   // balance column
            k[n * size + j] = 1.0;    // balance row
        }
        for (int i = 0; i < w.Count; i++)
        {
            Normal(p, w[i], a);
            int col = n + 1 + i;
            for (int j = 0; j < n; j++)
            {
                if (a[j] == 0.0) continue;
                k[j * size + col] = -a[j];
                k[col * size + j] = a[j];
            }
        }
        return k;
    }

    /// <summary>In-place LU with partial pivoting (first row with the strictly largest |entry| wins). False when a
    /// pivot falls below <see cref="SingularPivotTolerance"/> times the largest entry.</summary>
    private static bool Factor(double[] k, int size, out int[] perm)
    {
        perm = new int[size];
        for (int i = 0; i < size; i++) perm[i] = i;
        double scale = 0.0;
        for (int i = 0; i < k.Length; i++) scale = Math.Max(scale, Math.Abs(k[i]));
        if (scale == 0.0) return false;
        double floor = SingularPivotTolerance * scale;
        for (int c = 0; c < size; c++)
        {
            int piv = c;
            double best = Math.Abs(k[c * size + c]);
            for (int r = c + 1; r < size; r++)
            {
                double v = Math.Abs(k[r * size + c]);
                if (v > best) { best = v; piv = r; }
            }
            if (!(best > floor)) return false;
            if (piv != c)
            {
                for (int j = 0; j < size; j++) (k[c * size + j], k[piv * size + j]) = (k[piv * size + j], k[c * size + j]);
                (perm[c], perm[piv]) = (perm[piv], perm[c]);
            }
            double d = k[c * size + c];
            for (int r = c + 1; r < size; r++)
            {
                double f = k[r * size + c];
                if (f == 0.0) continue;
                f /= d;
                k[r * size + c] = f;
                for (int j = c + 1; j < size; j++) k[r * size + j] -= f * k[c * size + j];
            }
        }
        return true;
    }

    /// <summary>Solve with the factors from <see cref="Factor"/>.</summary>
    private static double[] SolveFactored(double[] k, int[] perm, int size, double[] rhs)
    {
        var y = new double[size];
        for (int i = 0; i < size; i++) y[i] = rhs[perm[i]];
        for (int i = 0; i < size; i++)
        {
            double s = y[i];
            for (int j = 0; j < i; j++) s -= k[i * size + j] * y[j];
            y[i] = s;
        }
        for (int i = size - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int j = i + 1; j < size; j++) s -= k[i * size + j] * y[j];
            y[i] = s / k[i * size + i];
        }
        return y;
    }
}
