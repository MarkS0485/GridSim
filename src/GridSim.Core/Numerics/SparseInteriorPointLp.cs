using System.Globalization;

namespace GridSim.Core.Numerics;

/// <summary>A linear program for <see cref="SparseInteriorPointLp"/>: minimise c'x subject to A x = b and
/// lo &lt;= x &lt;= hi. A is given as (row, column, value) entries (a repeated cell is summed in the order given); a bound
/// may be infinite (lo = -inf, hi = +inf), and a variable with both bounds infinite is free.</summary>
public sealed record SparseLpProblem(
    int Rows, int Columns, IReadOnlyList<(int Row, int Col, double Val)> A, double[] B, double[] C, double[] Lower, double[] Upper);

/// <summary>How <see cref="SparseInteriorPointLp.Solve"/> ended.</summary>
public enum SparseLpStatus
{
    /// <summary>Primal feasibility, dual feasibility and the duality gap all met the tolerance.</summary>
    Optimal,
    /// <summary>The iterates grew without bound (above 1e12): the LP is infeasible or unbounded. No optimum is returned.</summary>
    InfeasibleOrUnbounded,
    /// <summary>The iteration cap was reached first.</summary>
    IterationCap,
    /// <summary>The Newton system was singular or produced a value that is not a number.</summary>
    NumericalFailure,
}

/// <summary>The result of <see cref="SparseInteriorPointLp.Solve"/>: the primal point, the equality multipliers (y, the
/// dual of A x = b: the change of the optimal objective per unit of b), the bound multipliers (both non-negative, zero
/// on an infinite bound), the objective, the iterations and the final residuals. When <see cref="Status"/> is not
/// <see cref="SparseLpStatus.Optimal"/> the numbers are where the method stopped, not an optimum.</summary>
public sealed record SparseLpResult(
    SparseLpStatus Status, int Iterations, double Objective, double[] X, double[] Y, double[] LowerDual, double[] UpperDual,
    double PrimalResidual, double DualResidual, double RelativeGap, string Message)
{
    /// <summary>True when the LP was solved to optimality.</summary>
    public bool Optimal => Status == SparseLpStatus.Optimal;
}

/// <summary>
/// A sparse linear programming solver: Mehrotra's predictor-corrector primal-dual interior point method for the
/// bounded-variable LP (minimise c'x, A x = b, lo &lt;= x &lt;= hi), with each Newton step taken on the augmented system
/// [-(D + rho I) A'; A delta I] solved by the sparse LU (<see cref="SparseLu"/>) after a power-of-two equilibration, and
/// the factorisation reused for the corrector. It is the sparse counterpart of <see cref="BoundedSimplex"/> (dense, a
/// vertex), left untouched: the interior point method scales with the nonzeros, not with rows x columns, which is what the
/// unit commitment and security-constrained OPF of the roadmap need.
///
/// <para><b>Method.</b> Bound slacks s = x - lo (finite lo) and t = hi - x (finite hi) with multipliers z and w; the
/// residuals rb = A x - b, rc = c - A'y - z + w, rl = x - lo - s, ru = x + t - hi; the complementarity products s z and
/// t w driven to sigma mu. Eliminating the slacks and bound multipliers leaves the augmented system in (dx, dy) with
/// D = z / s + w / t. The predictor solves with sigma = 0; Mehrotra's sigma = (mu_aff / mu)^3 and the second-order term
/// give the corrector. Steps keep 0.995 of the distance to the boundary, separately for the primal and the dual.
/// rho = delta = 1e-9 regularise free variables and a rank-deficient A; the residuals are recomputed exactly every
/// iteration, so they do not bias the converged point.</para>
///
/// <para><b>Stopping.</b> Optimal when the relative primal residual max(|rb|, |rl|, |ru|) / (1 + |b| + |bounds|), the
/// relative dual residual |rc| / (1 + |c|) and the relative gap |c'x - (b'y + lo'z - hi'w)| / (1 + |c'x|) are all at or
/// below the tolerance (1e-9 by default; infinity norms). Every loop runs in a fixed order, so the result is
/// bit-identical from run to run.</para>
/// </summary>
public static class SparseInteriorPointLp
{
    /// <summary>Default optimality tolerance (relative residuals and gap).</summary>
    public const double DefaultTolerance = 1e-9;

    /// <summary>Default iteration cap.</summary>
    public const int DefaultMaxIterations = 200;

    private const double StepFraction = 0.995;
    private const double Regularisation = 1e-9;
    private const double DivergenceLimit = 1e12;

    /// <summary>Solve <paramref name="lp"/>. Throws <see cref="ArgumentException"/> for a malformed problem (sizes that
    /// do not agree, an entry outside A, a value that is not finite, a lower bound above its upper bound or an infinite
    /// bound on the wrong side).</summary>
    public static SparseLpResult Solve(SparseLpProblem lp, double tolerance = DefaultTolerance, int maxIterations = DefaultMaxIterations)
    {
        ArgumentNullException.ThrowIfNull(lp);
        Validate(lp);
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The LP tolerance must be a finite number above 0.");
        if (maxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "The LP needs at least one iteration.");

        int m = lp.Rows, n = lp.Columns;
        double[] lo = lp.Lower, hi = lp.Upper, b = lp.B, c = lp.C;
        var hasLo = new bool[n];
        var hasHi = new bool[n];
        for (int j = 0; j < n; j++) { hasLo[j] = !double.IsNegativeInfinity(lo[j]); hasHi[j] = !double.IsPositiveInfinity(hi[j]); }

        // A by columns, each column's entries in the order given (a repeated cell is summed in that order wherever used).
        var cols = new List<(int Row, double Val)>[n];
        for (int j = 0; j < n; j++) cols[j] = new List<(int, double)>();
        foreach ((int r, int col, double v) in lp.A) cols[col].Add((r, v));

        // Starting point: inside the bounds (the middle of a finite box, one from a single finite bound, 0 for a free
        // variable); slacks at least 1; multipliers 1 on every finite bound; y = 0.
        var x = new double[n];
        var s = new double[n];
        var t = new double[n];
        var z = new double[n];
        var w = new double[n];
        var y = new double[m];
        for (int j = 0; j < n; j++)
        {
            x[j] = hasLo[j] && hasHi[j] ? (lo[j] + hi[j]) / 2 : hasLo[j] ? lo[j] + 1 : hasHi[j] ? hi[j] - 1 : 0.0;
            if (hasLo[j]) { s[j] = Math.Max(x[j] - lo[j], 1.0); z[j] = 1.0; }
            if (hasHi[j]) { t[j] = Math.Max(hi[j] - x[j], 1.0); w[j] = 1.0; }
        }
        int nComp = 0;
        for (int j = 0; j < n; j++) { if (hasLo[j]) nComp++; if (hasHi[j]) nComp++; }

        double bNorm = NormInf(b);
        double boundNorm = 0;
        for (int j = 0; j < n; j++)
        {
            if (hasLo[j]) boundNorm = Math.Max(boundNorm, Math.Abs(lo[j]));
            if (hasHi[j]) boundNorm = Math.Max(boundNorm, Math.Abs(hi[j]));
        }
        double cNorm = NormInf(c);

        var rb = new double[m];
        var rc = new double[n];
        var rl = new double[n];
        var ru = new double[n];
        var d = new double[n];
        var rhs = new double[n + m];
        var dxA = new double[n]; var dyA = new double[m]; var dsA = new double[n]; var dtA = new double[n]; var dzA = new double[n]; var dwA = new double[n];
        var dx = new double[n]; var dy = new double[m]; var ds = new double[n]; var dt = new double[n]; var dz = new double[n]; var dw = new double[n];

        SparseLpStatus status = SparseLpStatus.IterationCap;
        string message = $"no optimum within {maxIterations} iteration(s)";
        double pRes = 0, dRes = 0, gap = 0;
        int it = 0;
        for (; ; it++)
        {
            // Residuals.
            for (int i = 0; i < m; i++) rb[i] = -b[i];
            for (int j = 0; j < n; j++)
            {
                double ay = 0;
                foreach ((int r, double v) in cols[j]) { rb[r] += v * x[j]; ay += v * y[r]; }
                rc[j] = c[j] - ay - (hasLo[j] ? z[j] : 0) + (hasHi[j] ? w[j] : 0);
                rl[j] = hasLo[j] ? x[j] - lo[j] - s[j] : 0;
                ru[j] = hasHi[j] ? x[j] + t[j] - hi[j] : 0;
            }
            double primalObj = Dot(c, x);
            double dualObj = Dot(b, y);
            for (int j = 0; j < n; j++)
            {
                if (hasLo[j]) dualObj += lo[j] * z[j];
                if (hasHi[j]) dualObj -= hi[j] * w[j];
            }
            pRes = Math.Max(NormInf(rb), Math.Max(NormInf(rl), NormInf(ru))) / (1 + bNorm + boundNorm);
            dRes = NormInf(rc) / (1 + cNorm);
            gap = Math.Abs(primalObj - dualObj) / (1 + Math.Abs(primalObj));
            if (pRes <= tolerance && dRes <= tolerance && gap <= tolerance)
            {
                status = SparseLpStatus.Optimal;
                message = $"optimal after {it} iteration(s)";
                break;
            }
            if (NormInf(x) > DivergenceLimit || NormInf(y) > DivergenceLimit || NormInf(z) > DivergenceLimit || NormInf(w) > DivergenceLimit)
            {
                status = SparseLpStatus.InfeasibleOrUnbounded;
                message = $"the iterates passed {DivergenceLimit.ToString("E0", CultureInfo.InvariantCulture)} after {it} iteration(s): the LP is infeasible or unbounded";
                break;
            }
            if (it == maxIterations) break;

            double mu = 0;
            for (int j = 0; j < n; j++) { if (hasLo[j]) mu += s[j] * z[j]; if (hasHi[j]) mu += t[j] * w[j]; }
            mu = nComp > 0 ? mu / nComp : 0;

            // The augmented matrix, factorised once for the predictor and the corrector.
            for (int j = 0; j < n; j++) d[j] = (hasLo[j] ? z[j] / s[j] : 0) + (hasHi[j] ? w[j] / t[j] : 0) + Regularisation;
            // Bug #33 (2026-10-25): on an infeasible LP a bound slack can reach 0 (or a multiplier overflow) before the
            // iterates pass the divergence limit, so D held an infinite entry and the sparse LU threw instead of this
            // method reporting. It now stops with a status, naming the iteration; a finite D runs exactly as before.
            if (!Finite(d))
            {
                status = SparseLpStatus.NumericalFailure;
                message = $"the Newton system of iteration {it + 1} is not finite (a bound slack reached 0): the LP is probably infeasible";
                break;
            }
            var kb = new CsrMatrix.Builder(n + m);
            for (int j = 0; j < n; j++)
            {
                kb.Add(j, j, -d[j]);
                foreach ((int r, double v) in cols[j]) { kb.Add(n + r, j, v); kb.Add(j, n + r, v); }
            }
            for (int i = 0; i < m; i++) kb.Add(n + i, n + i, Regularisation);
            CsrMatrix k = kb.Build();
            double[] scale = Equilibrate(k);
            SparseLuFactorization? lu = SparseLu.Factor(k);
            if (lu is null)
            {
                status = SparseLpStatus.NumericalFailure;
                message = $"the Newton system of iteration {it + 1} is singular";
                break;
            }

            // Predictor (sigma = 0).
            Direction(lu, scale, 0.0, null);
            if (!Finite(dx) || !Finite(dy)) { status = SparseLpStatus.NumericalFailure; message = $"the predictor of iteration {it + 1} is not a number"; break; }
            Array.Copy(dx, dxA, n); Array.Copy(dy, dyA, m); Array.Copy(ds, dsA, n); Array.Copy(dt, dtA, n); Array.Copy(dz, dzA, n); Array.Copy(dw, dwA, n);
            (double apA, double adA) = StepLengths(1.0);
            double muAff = 0;
            for (int j = 0; j < n; j++)
            {
                if (hasLo[j]) muAff += (s[j] + apA * dsA[j]) * (z[j] + adA * dzA[j]);
                if (hasHi[j]) muAff += (t[j] + apA * dtA[j]) * (w[j] + adA * dwA[j]);
            }
            muAff = nComp > 0 ? muAff / nComp : 0;
            double ratio = mu > 0 ? muAff / mu : 0;
            double sigma = ratio * ratio * ratio;

            // Corrector: sigma mu and the second-order term ds_aff dz_aff.
            Direction(lu, scale, sigma * mu, (dsA, dzA, dtA, dwA));
            if (!Finite(dx) || !Finite(dy)) { status = SparseLpStatus.NumericalFailure; message = $"the corrector of iteration {it + 1} is not a number"; break; }
            (double ap, double ad) = StepLengths(StepFraction);
            for (int j = 0; j < n; j++)
            {
                x[j] += ap * dx[j];
                if (hasLo[j]) { s[j] += ap * ds[j]; z[j] += ad * dz[j]; }
                if (hasHi[j]) { t[j] += ap * dt[j]; w[j] += ad * dw[j]; }
            }
            for (int i = 0; i < m; i++) y[i] += ad * dy[i];

            // The Newton direction for the complementarity target "target" (and the corrector's second-order terms).
            void Direction(SparseLuFactorization f, double[] sc, double target, (double[] Ds, double[] Dz, double[] Dt, double[] Dw)? second)
            {
                for (int j = 0; j < n; j++)
                {
                    double cl = 0, cu = 0;
                    if (hasLo[j])
                    {
                        double comp = target - s[j] * z[j] - (second is { } q ? q.Ds[j] * q.Dz[j] : 0);
                        cl = (comp - z[j] * rl[j]) / s[j];
                    }
                    if (hasHi[j])
                    {
                        double comp = target - t[j] * w[j] - (second is { } q ? q.Dt[j] * q.Dw[j] : 0);
                        cu = (comp + w[j] * ru[j]) / t[j];
                    }
                    rhs[j] = rc[j] - cl + cu;
                }
                for (int i = 0; i < m; i++) rhs[n + i] = -rb[i];
                var scaled = new double[n + m];
                for (int q = 0; q < n + m; q++) scaled[q] = rhs[q] * sc[q];
                double[] sol = f.Solve(scaled);
                for (int j = 0; j < n; j++) dx[j] = sol[j] * sc[j];
                for (int i = 0; i < m; i++) dy[i] = sol[n + i] * sc[n + i];
                for (int j = 0; j < n; j++)
                {
                    if (hasLo[j])
                    {
                        ds[j] = dx[j] + rl[j];
                        double comp = target - s[j] * z[j] - (second is { } q ? q.Ds[j] * q.Dz[j] : 0);
                        dz[j] = (comp - z[j] * ds[j]) / s[j];
                    }
                    if (hasHi[j])
                    {
                        dt[j] = -ru[j] - dx[j];
                        double comp = target - t[j] * w[j] - (second is { } q ? q.Dt[j] * q.Dw[j] : 0);
                        dw[j] = (comp - w[j] * dt[j]) / t[j];
                    }
                }
            }

            // The largest steps (times the fraction) that keep s, t, z, w positive, at most 1.
            (double, double) StepLengths(double fraction)
            {
                double aP = 1.0, aD = 1.0;
                for (int j = 0; j < n; j++)
                {
                    if (hasLo[j])
                    {
                        if (ds[j] < 0) aP = Math.Min(aP, fraction * (s[j] / -ds[j]));
                        if (dz[j] < 0) aD = Math.Min(aD, fraction * (z[j] / -dz[j]));
                    }
                    if (hasHi[j])
                    {
                        if (dt[j] < 0) aP = Math.Min(aP, fraction * (t[j] / -dt[j]));
                        if (dw[j] < 0) aD = Math.Min(aD, fraction * (w[j] / -dw[j]));
                    }
                }
                return (aP, aD);
            }
        }

        var lowerDual = new double[n];
        var upperDual = new double[n];
        for (int j = 0; j < n; j++) { if (hasLo[j]) lowerDual[j] = z[j]; if (hasHi[j]) upperDual[j] = w[j]; }
        return new SparseLpResult(status, it, Dot(c, x), x, y, lowerDual, upperDual, pRes, dRes, gap, message);
    }

    private static void Validate(SparseLpProblem lp)
    {
        int m = lp.Rows, n = lp.Columns;
        if (m < 0 || n < 1) throw new ArgumentException($"LP: {m} row(s) and {n} column(s); at least one column is needed.", nameof(lp));
        ArgumentNullException.ThrowIfNull(lp.A);
        if (lp.B is null || lp.B.Length != m) throw new ArgumentException($"LP: b must have {m} entries.", nameof(lp));
        if (lp.C is null || lp.C.Length != n) throw new ArgumentException($"LP: c must have {n} entries.", nameof(lp));
        if (lp.Lower is null || lp.Lower.Length != n || lp.Upper is null || lp.Upper.Length != n)
            throw new ArgumentException($"LP: the bounds must have {n} entries each.", nameof(lp));
        foreach ((int r, int col, double v) in lp.A)
        {
            if ((uint)r >= (uint)m || (uint)col >= (uint)n)
                throw new ArgumentException($"LP: entry ({r}, {col}) is outside the {m} x {n} matrix.", nameof(lp));
            if (!double.IsFinite(v)) throw new ArgumentException($"LP: entry ({r}, {col}) is not a finite number.", nameof(lp));
        }
        for (int i = 0; i < m; i++)
            if (!double.IsFinite(lp.B[i])) throw new ArgumentException($"LP: b[{i}] is not a finite number.", nameof(lp));
        for (int j = 0; j < n; j++)
        {
            if (!double.IsFinite(lp.C[j])) throw new ArgumentException($"LP: c[{j}] is not a finite number.", nameof(lp));
            double l = lp.Lower[j], h = lp.Upper[j];
            if (double.IsNaN(l) || double.IsNaN(h) || double.IsPositiveInfinity(l) || double.IsNegativeInfinity(h))
                throw new ArgumentException($"LP: variable {j} has bounds {l} .. {h}; a bound must be a number, a lower one finite or -inf, an upper one finite or +inf.", nameof(lp));
            if (l > h) throw new ArgumentException($"LP: variable {j} has its lower bound {l} above its upper bound {h}.", nameof(lp));
        }
    }

    /// <summary>Symmetric power-of-two equilibration of <paramref name="k"/> in place (a few Ruiz passes); returns the
    /// scale D, so the scaled system is D K D. Exact: powers of two do not round.</summary>
    private static double[] Equilibrate(CsrMatrix k)
    {
        int n = k.N;
        int[] rowPtr = k.RowPtr, col = k.ColIdx;
        double[] val = k.Val;
        var dd = new double[n];
        Array.Fill(dd, 1.0);
        var e = new int[n];
        for (int pass = 0; pass < 10; pass++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                double mx = 0;
                for (int q = rowPtr[i]; q < rowPtr[i + 1]; q++) mx = Math.Max(mx, Math.Abs(val[q]));
                e[i] = mx > 0 && double.IsFinite(mx) ? -(Math.ILogB(mx) / 2) : 0;
                if (e[i] != 0) changed = true;
            }
            if (!changed) break;
            for (int i = 0; i < n; i++)
            {
                dd[i] = Math.ScaleB(dd[i], e[i]);
                for (int q = rowPtr[i]; q < rowPtr[i + 1]; q++) val[q] = Math.ScaleB(val[q], e[i] + e[col[q]]);
            }
        }
        return dd;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double NormInf(double[] v)
    {
        double mx = 0;
        foreach (double e in v) mx = Math.Max(mx, Math.Abs(e));
        return mx;
    }

    private static bool Finite(double[] v) => Array.TrueForAll(v, double.IsFinite);
}
