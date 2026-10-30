using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GridSim.Core.Numerics;

/// <summary>
/// A sparse LU factorisation built by the Gilbert-Peierls left-looking algorithm with
/// partial pivoting, over a fill-reducing column ordering (reverse Cuthill-McKee).
///
/// Left-looking G-P is the standard fast sparse LU: column <c>k</c> is produced by a
/// single sparse triangular solve <c>L z = A(:,q[k])</c>, whose nonzero pattern is found
/// first by a depth-first reach over the graph of the already-built <c>L</c>. That keeps
/// the arithmetic proportional to the true (post-fill) nonzeros instead of paying the
/// dictionary/hash overhead and per-solve re-symbolic cost of the old dynamic scheme.
///
/// The factorisation is <c>L U = P A Q</c>, where <c>Q</c> is the RCM column permutation
/// (<see cref="_q"/>), <c>P</c> is the partial-pivoting row permutation (encoded by
/// <see cref="_pinv"/>), <c>L</c> is unit lower-triangular and <c>U</c> upper-triangular.
/// All factors are stored compressed-sparse-column (CSC); <c>L</c> keeps its unit diagonal
/// as the first entry of each column, <c>U</c> keeps its pivot as the last.
/// </summary>
internal sealed class SparseLuFactorization
{
    private readonly int _n;

    // L in CSC: unit diagonal stored first in each column.
    private readonly int[] _lp;
    private readonly int[] _li;
    private readonly double[] _lx;

    // U in CSC: pivot (diagonal) stored last in each column.
    private readonly int[] _up;
    private readonly int[] _ui;
    private readonly double[] _ux;

    private readonly int[] _pinv; // original row -> pivot step (row permutation P)
    private readonly int[] _q;    // pivot step -> original column (column permutation Q)

    private SparseLuFactorization(int n, int[] lp, int[] li, double[] lx,
        int[] up, int[] ui, double[] ux, int[] pinv, int[] q)
    {
        _n = n; _lp = lp; _li = li; _lx = lx;
        _up = up; _ui = ui; _ux = ux; _pinv = pinv; _q = q;
    }

    /// <summary>The symbolic plan of this factorisation, when it was asked for (<see cref="Factor"/> with
    /// <c>keepPlan</c>); null otherwise.</summary>
    internal SparseLuPlan? Plan { get; private init; }

    /// <summary>The L and U values, in the order of the stored patterns (for the multi-case kernel's checks).</summary>
    internal ReadOnlySpan<double> LValues => _lx.AsSpan(0, _lp[_n]);
    internal ReadOnlySpan<double> UValues => _ux.AsSpan(0, _up[_n]);

    /// <summary>
    /// Factor the matrix given in compressed-sparse-column form with column permutation
    /// <paramref name="q"/>. Returns <c>null</c> if a zero/negligible pivot is hit
    /// (structurally or numerically singular). "Negligible" is the shared relative floor
    /// <see cref="NumericTolerances.RelativePivotFloor"/> times the largest |A_ij|, the same test the
    /// dense LU applies; before October 2026 (R034/R280) it was an absolute 1e-300, so a matrix singular
    /// to working precision "factorised" and returned huge finite garbage instead of null.
    /// <para><paramref name="diagonalPreference"/> (OPT-IN, default 0 = off): when positive, the pivot of column
    /// <c>q[k]</c> is its diagonal row whenever that row is still a candidate and its magnitude is at least
    /// <paramref name="diagonalPreference"/> times the largest candidate's (and above the singular floor); otherwise the
    /// largest candidate as usual (threshold partial pivoting with diagonal preference, as KLU and UMFPACK offer). It
    /// makes the pivot sequence of a family of matrices stable where plain partial pivoting breaks near-ties by rounding,
    /// so it changes the factors; at 0 the code below runs exactly the plain partial pivoting it always ran.</para>
    /// </summary>
    public static SparseLuFactorization? Factor(int n, int[] ap, int[] ai, double[] ax, int[] q, bool keepPlan = false,
        double diagonalPreference = 0.0)
    {
        if (!(diagonalPreference >= 0.0 && diagonalPreference <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(diagonalPreference), diagonalPreference, "the diagonal preference must lie in [0, 1]");
        // Reference scale for the near-singular pivot test: the largest magnitude anywhere in A (only the
        // first ap[n] slots are live entries). A NaN entry fails loud here rather than poisoning the factors.
        double aNorm = NumericTolerances.MaxAbs(ax.AsSpan(0, ap[n]));
        if (double.IsNaN(aNorm))
            throw new ArgumentException("Sparse LU: the matrix contains a NaN entry, so it cannot be factorised.", nameof(ax));
        // GR-479: an infinite entry fails loud too. It made the pivot floor infinite, so the first column returned null
        // and a corrupt matrix was reported as a singular one.
        if (double.IsInfinity(aNorm))
            throw new ArgumentException("Sparse LU: the matrix contains an infinite entry, so it cannot be factorised.", nameof(ax));
        double pivotFloor = NumericTolerances.PivotFloor(aNorm);

        // Storage for L and U grows on demand (doubling); seed with a generous guess.
        int nnz = ax.Length;
        int cap = Math.Max(2 * nnz + n, 16);
        var li = new int[cap]; var lx = new double[cap];
        var ui = new int[cap]; var ux = new double[cap];
        var lp = new int[n + 1]; var up = new int[n + 1];

        var pinv = new int[n];
        Array.Fill(pinv, -1);

        // Numeric workspace and DFS scratch (allocated once for the whole factorisation).
        var x = new double[n];
        var xi = new int[n];       // pattern / DFS output stack
        var pstack = new int[n];   // DFS pause positions
        var marked = new bool[n];  // DFS visited flags (kept all-false between reaches)

        // The plan (keepPlan): each column's reach in topological order and its pivot row. Recording reads the arrays the
        // factorisation already computes; it changes no arithmetic.
        int[]? reachPtr = keepPlan ? new int[n + 1] : null;
        int[]? reach = keepPlan ? new int[Math.Max(16, 2 * nnz)] : null;
        int[]? pivotRow = keepPlan ? new int[n] : null;
        int rnz = 0;

        int lnz = 0, unz = 0;
        for (int k = 0; k < n; k++)
        {
            // Room for up to n new entries in this column of each factor.
            if (lnz + n > li.Length) { Grow(ref li, lnz + n); Grow(ref lx, lnz + n); }
            if (unz + n > ui.Length) { Grow(ref ui, unz + n); Grow(ref ux, unz + n); }

            lp[k] = lnz;
            up[k] = unz;
            int col = q[k];

            // x = L \ A(:,col); xi[top..n-1] is the (topologically ordered) nonzero pattern.
            int top = SpSolve(col, ap, ai, ax, lp, li, lx, pinv, n, xi, pstack, marked, x);
            if (keepPlan)
            {
                if (rnz + (n - top) > reach!.Length) Grow(ref reach, rnz + (n - top));
                Array.Copy(xi, top, reach, rnz, n - top);
                rnz += n - top;
                reachPtr![k + 1] = rnz;
            }

            // Partial pivot: largest |x[i]| over rows not yet pivotal; emit the rest to U.
            int ipiv = -1;
            double best = -1.0;
            for (int px = top; px < n; px++)
            {
                int i = xi[px];
                if (pinv[i] < 0)
                {
                    double t = Math.Abs(x[i]);
                    if (t > best) { best = t; ipiv = i; }
                }
                else
                {
                    ui[unz] = pinv[i];   // U(pinv[i], k) is an above-diagonal entry
                    ux[unz] = x[i];
                    unz++;
                }
            }
            double pivot = ipiv >= 0 ? x[ipiv] : 0.0;
            // Singular: no candidate row at all (structural), or the best candidate has collapsed to the
            // relative floor (numerical). best == |pivot|; the comparison is <= so an exact zero is always
            // caught, matching DenseLuSolver's "max <= pivotFloor".
            if (ipiv < 0 || best <= pivotFloor)
                return null;
            if (diagonalPreference > 0.0)
            {
                // Opt-in threshold rule (see the summary); never reached on the default path.
                ipiv = PreferDiagonal(col, ipiv, best, pivotFloor, diagonalPreference, xi, top, n, x, pinv);
                pivot = x[ipiv];
            }
            if (keepPlan) pivotRow![k] = ipiv;

            // U(k,k) = pivot (stored last), L(k,k) = 1 (stored first).
            ui[unz] = k; ux[unz] = pivot; unz++;
            pinv[ipiv] = k;
            li[lnz] = ipiv; lx[lnz] = 1.0; lnz++;

            for (int px = top; px < n; px++)
            {
                int i = xi[px];
                if (pinv[i] < 0)          // remaining rows form L(:,k)
                {
                    li[lnz] = i;
                    lx[lnz] = x[i] / pivot;
                    lnz++;
                }
                x[i] = 0.0;               // clear workspace for the next column
            }
        }
        lp[n] = lnz;
        up[n] = unz;

        int[]? liOriginal = keepPlan ? li.AsSpan(0, lnz).ToArray() : null;

        // Remap L's row indices from original rows into pivot-step space.
        for (int p = 0; p < lnz; p++) li[p] = pinv[li[p]];

        if (!keepPlan) return new SparseLuFactorization(n, lp, li, lx, up, ui, ux, pinv, q);
        int annz = ap[n];
        var plan = new SparseLuPlan(n, (int[])ap.Clone(), ai.AsSpan(0, annz).ToArray(), (int[])q.Clone(), reachPtr!,
            reach!.AsSpan(0, rnz).ToArray(), pivotRow!, (int[])pinv.Clone(), lp, liOriginal!, li, up, ui, diagonalPreference);
        return new SparseLuFactorization(n, lp, li, lx, up, ui, ux, pinv, q) { Plan = plan };
    }

    /// <summary>The opt-in diagonal-preference rule: the diagonal row <paramref name="col"/> replaces the largest
    /// candidate <paramref name="ipiv"/> when it is in this column's reach, not yet pivotal (<paramref name="pinv"/> &lt; 0)
    /// and its magnitude is at least <paramref name="tau"/> times <paramref name="best"/> and above the singular floor.</summary>
    private static int PreferDiagonal(int col, int ipiv, double best, double pivotFloor, double tau,
        int[] xi, int top, int n, double[] x, int[] pinv)
    {
        if (ipiv == col || pinv[col] >= 0) return ipiv;
        for (int px = top; px < n; px++)
        {
            if (xi[px] != col) continue;
            double td = Math.Abs(x[col]);
            return td >= tau * best && td > pivotFloor ? col : ipiv;
        }
        return ipiv;
    }

    /// <summary>A factorisation on <paramref name="plan"/>'s pattern holding the given L and U values (the multi-case
    /// kernel's lanes, copied out).</summary>
    internal static SparseLuFactorization FromPlanValues(SparseLuPlan plan, double[] lx, double[] ux)
        => new(plan.N, plan.Lp, plan.LiStep, lx, plan.Up, plan.Ui, ux, plan.PivotStep, plan.Q) { Plan = plan };

    /// <summary>
    /// Factor values <paramref name="ax"/> on <paramref name="plan"/>'s pattern (the caller has checked
    /// <see cref="SparseLuPlan.SamePattern"/>) by replaying the full factorisation's arithmetic: for each column, the
    /// same scatter of A(:,col), the same updates in the same reach order (the order the depth-first search gave), the same
    /// pivot search and the same divisions, so every L and U value is the value <see cref="Factor"/> computes, to the bit.
    /// Returns null when the pivot search picks a different row from the plan's (the full factorisation would then build
    /// a different pattern; the caller must fall back to <see cref="Factor"/>) or when a pivot is at or below the relative
    /// floor (singular, as <see cref="Factor"/> would report). NaN or infinite entries are refused as there. The returned
    /// factorisation shares the plan's index arrays (read only) and carries the plan.
    /// </summary>
    /// <summary><see cref="PreferDiagonal"/> for a replay: the reach is <paramref name="reach"/>[r0..r1) and a row is
    /// still a candidate at step <paramref name="k"/> when its pivot step is at least k. <paramref name="stride"/> and
    /// <paramref name="lane"/> address an interleaved multi-case workspace (1 and 0 for a single case).</summary>
    internal static int PreferDiagonalInReach(int col, int ipiv, double best, double pivotFloor, double tau,
        int[] reach, int r0, int r1, double[] x, int[] step, int k, int stride = 1, int lane = 0)
    {
        if (ipiv == col || step[col] < k) return ipiv;
        for (int px = r0; px < r1; px++)
        {
            if (reach[px] != col) continue;
            double td = Math.Abs(x[col * stride + lane]);
            return td >= tau * best && td > pivotFloor ? col : ipiv;
        }
        return ipiv;
    }

    internal static SparseLuFactorization? Refactor(SparseLuPlan plan, double[] ax)
    {
        int n = plan.N;
        int[] ap = plan.Ap, ai = plan.Ai;
        double aNorm = NumericTolerances.MaxAbs(ax.AsSpan(0, ap[n]));
        if (double.IsNaN(aNorm))
            throw new ArgumentException("Sparse LU: the matrix contains a NaN entry, so it cannot be factorised.", nameof(ax));
        if (double.IsInfinity(aNorm))
            throw new ArgumentException("Sparse LU: the matrix contains an infinite entry, so it cannot be factorised.", nameof(ax));
        double pivotFloor = NumericTolerances.PivotFloor(aNorm);

        int[] lp = plan.Lp, liO = plan.LiOriginal, up = plan.Up, step = plan.PivotStep, reach = plan.Reach, rp = plan.ReachPtr;
        var lx = new double[lp[n]];
        var ux = new double[up[n]];
        var x = new double[n];
        int lnz = 0, unz = 0;
        for (int k = 0; k < n; k++)
        {
            int r0 = rp[k], r1 = rp[k + 1];
            int col = plan.Q[k];
            for (int p = r0; p < r1; p++) x[reach[p]] = 0.0;
            for (int p = ap[col]; p < ap[col + 1]; p++) x[ai[p]] = ax[p];
            for (int px = r0; px < r1; px++)
            {
                int j = reach[px];
                int jcol = step[j];
                if (jcol >= k) continue;   // not yet a pivot row at step k (Factor: pinv[j] < 0)
                double xj = x[j];
                for (int p = lp[jcol] + 1; p < lp[jcol + 1]; p++)
                    x[liO[p]] -= lx[p] * xj;
            }
            int ipiv = -1;
            double best = -1.0;
            for (int px = r0; px < r1; px++)
            {
                int i = reach[px];
                if (step[i] >= k)
                {
                    double t = Math.Abs(x[i]);
                    if (t > best) { best = t; ipiv = i; }
                }
                else ux[unz++] = x[i];
            }
            if (ipiv >= 0 && best > pivotFloor && plan.DiagonalPreference > 0.0)
                ipiv = PreferDiagonalInReach(col, ipiv, best, pivotFloor, plan.DiagonalPreference, reach, r0, r1, x, step, k);
            if (ipiv != plan.PivotRow[k]) return null;   // a different pivot: a different pattern; fall back
            if (best <= pivotFloor) return null;          // singular, as Factor reports
            double pivot = x[ipiv];
            ux[unz++] = pivot;
            lx[lnz++] = 1.0;
            for (int px = r0; px < r1; px++)
            {
                int i = reach[px];
                if (step[i] > k) lx[lnz++] = x[i] / pivot;
                x[i] = 0.0;
            }
        }
        return new SparseLuFactorization(n, lp, plan.LiStep, lx, up, plan.Ui, ux, step, plan.Q) { Plan = plan };
    }

    /// <summary>
    /// Solve A x = b into caller-owned buffers: <paramref name="x"/> receives the solution and
    /// <paramref name="work"/> is scratch, both of length n (R114: the per-step EMT solve allocated two fresh
    /// n-vectors per call). The arithmetic is exactly <see cref="Solve(double[])"/>'s, operation for operation, so
    /// the result is bit-identical.
    /// </summary>
    public void SolveInto(double[] b, double[] x, double[] work)
    {
        int n = _n;
        if (b.Length < n || x.Length < n || work.Length < n)
            throw new ArgumentException($"SolveInto needs b, x and work of length at least {n}.");
        // work plays the role of Solve's intermediate vector, x of its output vector.
        for (int k = 0; k < n; k++) work[_pinv[k]] = b[k];
        for (int j = 0; j < n; j++)
        {
            double xj = work[j];
            for (int p = _lp[j] + 1; p < _lp[j + 1]; p++)
                work[_li[p]] -= _lx[p] * xj;
        }
        for (int j = n - 1; j >= 0; j--)
        {
            double xj = work[j] / _ux[_up[j + 1] - 1];
            work[j] = xj;
            for (int p = _up[j]; p < _up[j + 1] - 1; p++)
                work[_ui[p]] -= _ux[p] * xj;
        }
        for (int k = 0; k < n; k++) x[_q[k]] = work[k];
    }

    /// <summary>Solve A x = b using the stored factors. Returns a fresh solution vector.</summary>
    public double[] Solve(double[] b)
    {
        ArgumentNullException.ThrowIfNull(b);
        int n = _n;
        // GR-468: the right-hand side must have one entry per row (a longer one was silently truncated).
        if (b.Length != n)
            throw new ArgumentException($"The right-hand side has {b.Length} entries; the factorised system has {n}.", nameof(b));
        var x = new double[n];

        // x = P b : place original-row value into its pivot-step slot.
        for (int k = 0; k < n; k++) x[_pinv[k]] = b[k];

        // Forward solve L z = P b (unit lower-triangular, diagonal is first entry).
        for (int j = 0; j < n; j++)
        {
            double xj = x[j];
            for (int p = _lp[j] + 1; p < _lp[j + 1]; p++)
                x[_li[p]] -= _lx[p] * xj;
        }

        // Back solve U y = z (upper-triangular, pivot is last entry of each column).
        for (int j = n - 1; j >= 0; j--)
        {
            double xj = x[j] / _ux[_up[j + 1] - 1];
            x[j] = xj;
            for (int p = _up[j]; p < _up[j + 1] - 1; p++)
                x[_ui[p]] -= _ux[p] * xj;
        }

        // x = Q y : map pivot-step solution back to original column positions.
        var outv = new double[n];
        for (int k = 0; k < n; k++) outv[_q[k]] = x[k];
        return outv;
    }

    /// <summary>
    /// Build the evaluator for many inverse quadratic forms h^T A^-1 h against these factors (the state
    /// estimator's residual-covariance stage: one per measurement row). Construction is O(n + nnz(L+U)) and
    /// is done once, single-threaded; the evaluator is then read-only and may be shared by parallel workers,
    /// each passing its own workspace. See <see cref="InverseQuadraticForm"/> for the bit-identity contract.
    /// </summary>
    internal InverseQuadraticForm CreateInverseQuadraticForm() => new(this);

    /// <summary>
    /// Evaluates q = sum_p hVal[p] * x[hCol[p]] where x = A^-1 h, for a SPARSE h, giving a result that is
    /// BIT-IDENTICAL to the reference
    /// <code>
    ///     hDense[hCol[p]] = hVal[p] (each p, in order); x = Solve(hDense); q = 0; q += hVal[p] * x[hCol[p]] (each p, in order)
    /// </code>
    /// while doing only the work that can change the answer (October 2026 review, R010/R117). The reference
    /// paid, per call, two fresh n-length allocations, two full O(n) permutation passes and full forward and
    /// back sweeps over every column of L and U, whatever the sparsity of h. This evaluator differs from it in
    /// exactly four ways, each of which provably leaves every bit of q unchanged:
    /// <list type="number">
    /// <item><b>No allocation.</b> The caller supplies an all-zero workspace of length n, which is used in
    /// pivot-step space directly (h is scattered straight to its permuted slots) and cleared again before
    /// returning, so it is all +0 on exit.</item>
    /// <item><b>Forward solve starts at the first nonzero.</b> In the reference, every pivot-step slot below
    /// the smallest slot h touches holds +0 and every column j there has multiplier x[j] = +0. An update
    /// x[i] -= l*(+0) leaves a nonzero x[i] unchanged and leaves a +0 at +0 (+0 - (+0) = +0 - (-0) = +0 in
    /// round-to-nearest), so skipping those columns - and any later column whose multiplier is exactly zero -
    /// is bit-identical, signed zeros included (in the forward sweep a zero slot is always +0: an exact
    /// cancellation a - a gives +0, never -0).</item>
    /// <item><b>Back solve stops at the last slot the caller reads.</b> Back substitution finalises x[j] at
    /// column j and later (smaller) columns only update rows above themselves, so once column kMin (the
    /// smallest pivot-step slot of any requested entry) is done, every requested entry holds its final
    /// value. A column whose finalised x[j] is exactly zero is not propagated: its updates could only flip
    /// the sign of a zero entry. A zero requested entry - whatever its sign - contributes a product of
    /// +0 or -0 to q, and adding a signed zero to the accumulator never changes it (q starts at +0; a nonzero
    /// q is unchanged by +-0; +0 + (-0) = +0; and a sum that cancels exactly is +0, never -0).</item>
    /// <item><b>Only the requested entries are read back</b>, through the inverse column permutation.</item>
    /// </list>
    /// Arguments 2 and 3 rely on every factor entry being finite (l*0 would be NaN for an infinite l). The
    /// constructor checks that once; if any factor entry is not finite the evaluator falls back to the full
    /// reference arithmetic for every call, so even that pathological case is reproduced exactly.
    /// </summary>
    internal sealed class InverseQuadraticForm
    {
        private readonly SparseLuFactorization _f;
        private readonly int[] _qinv;          // original column -> pivot step (inverse of _q)
        private readonly int[] _clearFrom;     // per back-solve stop step k: lowest slot the sweep n-1..k can write
        private readonly bool _finiteFactors;  // false => every call takes the full reference path

        internal InverseQuadraticForm(SparseLuFactorization f)
        {
            _f = f;
            int n = f._n;
            _qinv = new int[n];
            for (int k = 0; k < n; k++) _qinv[f._q[k]] = k;

            // The back sweep over columns n-1..k writes x[j] for j >= k (the division) and x[ui] for every
            // above-diagonal row index ui in those columns. _clearFrom[k] is the smallest of those, so
            // clearing [min(forward start, _clearFrom[stop]), n) restores the workspace to all +0.
            _clearFrom = new int[n + 1];
            _clearFrom[n] = n;
            for (int j = n - 1; j >= 0; j--)
            {
                int lowest = j;
                for (int p = f._up[j]; p < f._up[j + 1] - 1; p++)
                    if (f._ui[p] < lowest) lowest = f._ui[p];
                _clearFrom[j] = Math.Min(lowest, _clearFrom[j + 1]);
            }

            _finiteFactors = AllFinite(f._lx, f._lp[n]) && AllFinite(f._ux, f._up[n]);
        }

        /// <summary>False when some L or U entry is not finite, in which case every call reproduces the full
        /// reference arithmetic instead of the trimmed sweeps (see the type summary).</summary>
        internal bool HasFiniteFactors => _finiteFactors;

        /// <summary>The order n of the factored matrix (the workspace length every call needs).</summary>
        internal int Order => _f._n;

        /// <summary>
        /// q = h^T A^-1 h for the sparse h given as (column, value) entries (see the type summary for the
        /// exact reference this reproduces bit for bit). <paramref name="work"/> must have length n and be all
        /// zero on entry; it is all +0 again on return. Not thread-safe per workspace: give each worker its own.
        /// </summary>
        internal double Evaluate(ReadOnlySpan<(int Col, double Val)> h, double[] work)
        {
            SparseLuFactorization f = _f;
            int n = f._n;
            if (work.Length != n)
                throw new ArgumentException($"Workspace length {work.Length} does not match the factor order {n}.", nameof(work));
            if (h.Length == 0) return 0.0;   // the reference sums nothing: q = +0
            if (!_finiteFactors) return EvaluateReference(h, work);

            // Scatter h straight into pivot-step space (the reference scatters into a dense vector, last entry
            // for a column winning, then permutes by _pinv - the same values land in the same slots), and find
            // the forward start (smallest row slot) and the back-solve stop (smallest requested column slot).
            int fwdStart = n, backStop = n;
            for (int p = 0; p < h.Length; p++)
            {
                int col = h[p].Col;
                int kr = f._pinv[col];
                work[kr] = h[p].Val;
                if (kr < fwdStart) fwdStart = kr;
                int kc = _qinv[col];
                if (kc < backStop) backStop = kc;
            }

            int[] lp = f._lp, li = f._li, up = f._up, ui = f._ui;
            double[] lx = f._lx, ux = f._ux;

            // Forward solve L z = P h from the first nonzero slot (unit diagonal first in each column).
            for (int j = fwdStart; j < n; j++)
            {
                double xj = work[j];
                if (xj == 0.0) continue;
                for (int p = lp[j] + 1; p < lp[j + 1]; p++)
                    work[li[p]] -= lx[p] * xj;
            }

            // Back solve U y = z, columns n-1 down to the smallest requested slot (pivot last in each column).
            for (int j = n - 1; j >= backStop; j--)
            {
                double xj = work[j] / ux[up[j + 1] - 1];
                work[j] = xj;
                if (xj == 0.0) continue;
                for (int p = up[j]; p < up[j + 1] - 1; p++)
                    work[ui[p]] -= ux[p] * xj;
            }

            // q = sum_p hVal[p] * y[qinv[col]], in the caller's entry order (the reference's summation order).
            double q = 0;
            for (int p = 0; p < h.Length; p++)
                q += h[p].Val * work[_qinv[h[p].Col]];

            // Restore the workspace to all +0 over every slot either sweep could have written.
            int clearFrom = Math.Min(Math.Min(fwdStart, backStop), _clearFrom[backStop]);
            Array.Clear(work, clearFrom, n - clearFrom);
            return q;
        }

        /// <summary>The smallest pivot-step slot of any entry of <paramref name="h"/> through the column
        /// permutation - where the back solve for h may stop - or n for an empty row. The natural sort key for
        /// grouping rows into blocks (see <see cref="EvaluateBlock"/>).</summary>
        internal int BackSolveStop(List<(int Col, double Val)> h)
        {
            int stop = _f._n;
            for (int p = 0; p < h.Count; p++)
            {
                int kc = _qinv[h[p].Col];
                if (kc < stop) stop = kc;
            }
            return stop;
        }

        /// <summary>Right-hand sides solved together by <see cref="EvaluateBlock"/> (lanes of one block).</summary>
        internal const int BlockWidth = 8;

        /// <summary>
        /// <see cref="Evaluate"/> for up to <see cref="BlockWidth"/> rows at once, each lane BIT-IDENTICAL to
        /// <see cref="Evaluate"/> (and so to the full-solve reference) for its own row. The lanes share one
        /// pass over L and U: the workspace is lane-interleaved (slot k of lane l at k*8 + l), so each factor
        /// entry is loaded once and applied to all eight right-hand sides with one vector multiply and one
        /// vector subtract - separate operations, never a fused multiply-add, so every lane rounds exactly as
        /// the scalar x[i] -= l*x[j] does, at every SIMD width (the <see cref="SimdKernels"/> bit-exact
        /// contract). Per lane, every slot still receives its updates in the reference's column order (forward
        /// ascending, back descending); within one column the updated rows are distinct.
        ///
        /// <para>What is shared is only the RANGE: the forward sweep starts at the smallest lane start and the
        /// back sweep stops at the smallest lane stop, so a lane can see columns its own solve would have
        /// skipped. Those columns carry an exactly-zero multiplier for that lane (before its start) or update
        /// only rows below its smallest requested slot (after its stop), which is the same argument that lets
        /// <see cref="Evaluate"/> skip them; a column is skipped outright only when all eight lanes are zero.
        /// Callers group rows with similar slots (the estimator sorts by back-solve stop) to keep the shared
        /// range tight; the grouping changes only the speed, never a bit.</para>
        /// </summary>
        /// <param name="rows">All sparse rows; the block is <paramref name="order"/>[start .. start+count).</param>
        /// <param name="q">Receives each block row's form at its own row index.</param>
        /// <param name="work">All-zero lane-interleaved workspace of length n * <see cref="BlockWidth"/>; all
        /// +0 again on return.</param>
        internal void EvaluateBlock(List<(int Col, double Val)>[] rows, int[] order, int start, int count,
            double[] q, double[] work)
            => EvaluateBlock(rows, order, start, count, q, work, SimdCapabilities.Tier);

        /// <summary><see cref="EvaluateBlock(List{ValueTuple{int, double}}[], int[], int, int, double[], double[])"/>
        /// at an explicit vector tier - the test seam that runs every tier's arm on any machine (the R102
        /// convention). Production passes <see cref="SimdCapabilities.Tier"/>.</summary>
        internal void EvaluateBlock(List<(int Col, double Val)>[] rows, int[] order, int start, int count,
            double[] q, double[] work, SimdTier tier)
        {
            const int K = BlockWidth;
            SparseLuFactorization f = _f;
            int n = f._n;
            if (count < 1 || count > K)
                throw new ArgumentOutOfRangeException(nameof(count), count, $"A block holds 1 to {K} rows.");
            if (work.Length != n * K)
                throw new ArgumentException($"Block workspace length {work.Length} is not {K} x the factor order {n}.", nameof(work));

            if (!_finiteFactors)
            {
                // Pathological factors: every lane takes the exact reference path, on its own n-length scratch.
                // (Fixed 2026-10-19: this used to hand the whole n x BlockWidth block workspace to EvaluateReference,
                // whose Solve read only its first n slots; GR-468 (1.95.332) made Solve refuse a right-hand
                // side of the wrong length, so the pathological block path threw. A separate n-length buffer gives
                // the same first-n values (all zero), hence the same bits, and leaves the block workspace untouched.)
                var scratch = new double[n];
                for (int l = 0; l < count; l++)
                {
                    int r = order[start + l];
                    var span = CollectionsMarshal.AsSpan(rows[r]);
                    q[r] = span.Length == 0 ? 0.0 : EvaluateReference(span, scratch);
                }
                return;
            }

            // Scatter each lane's row into its interleaved pivot-step slots; find the shared range.
            int fwdStart = n, backStop = n;
            for (int l = 0; l < count; l++)
            {
                List<(int Col, double Val)> row = rows[order[start + l]];
                for (int p = 0; p < row.Count; p++)
                {
                    int col = row[p].Col;
                    int kr = f._pinv[col];
                    work[kr * K + l] = row[p].Val;
                    if (kr < fwdStart) fwdStart = kr;
                    int kc = _qinv[col];
                    if (kc < backStop) backStop = kc;
                }
            }

            if (backStop < n)   // at least one lane has entries (otherwise every form is +0, set below)
            {
                int[] lp = f._lp, li = f._li, up = f._up, ui = f._ui;
                double[] lx = f._lx, ux = f._ux;
                ref double w0 = ref MemoryMarshal.GetArrayDataReference(work);

                // Forward: L z = P h for all lanes.
                for (int j = fwdStart; j < n; j++)
                {
                    int src = j * K;
                    if (AllZero8(ref w0, src)) continue;
                    for (int p = lp[j] + 1; p < lp[j + 1]; p++)
                        SubtractScaled8(ref w0, li[p] * K, src, lx[p], tier);
                }

                // Back: U y = z for all lanes, down to the smallest requested slot of any lane.
                for (int j = n - 1; j >= backStop; j--)
                {
                    int src = j * K;
                    Divide8(ref w0, src, ux[up[j + 1] - 1], tier);
                    if (AllZero8(ref w0, src)) continue;
                    for (int p = up[j]; p < up[j + 1] - 1; p++)
                        SubtractScaled8(ref w0, ui[p] * K, src, ux[p], tier);
                }
            }

            // Each lane's form, summed in its own row's entry order (the reference order).
            for (int l = 0; l < count; l++)
            {
                int r = order[start + l];
                List<(int Col, double Val)> row = rows[r];
                double acc = 0;
                for (int p = 0; p < row.Count; p++)
                    acc += row[p].Val * work[_qinv[row[p].Col] * K + l];
                q[r] = acc;
            }

            if (backStop < n)
            {
                int clearFrom = Math.Min(Math.Min(fwdStart, backStop), _clearFrom[backStop]);
                Array.Clear(work, clearFrom * K, (n - clearFrom) * K);
            }
        }

        /// <summary>True when all eight lanes at <paramref name="off"/> are zero (either sign).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllZero8(ref double w0, int off)
        {
            for (int l = 0; l < BlockWidth; l++)
                if (Unsafe.Add(ref w0, off + l) != 0.0) return false;
            return true;
        }

        /// <summary>Lanes dst[0..8) -= factor * src[0..8): a separate multiply and subtract per lane (bit-exact
        /// at every width; no fused multiply-add).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SubtractScaled8(ref double w0, int dst, int src, double factor, SimdTier tier)
        {
            switch (tier)
            {
                case SimdTier.Avx512:
                {
                    var d = Vector512.LoadUnsafe(ref w0, (nuint)dst);
                    var s = Vector512.LoadUnsafe(ref w0, (nuint)src);
                    (d - Vector512.Create(factor) * s).StoreUnsafe(ref w0, (nuint)dst);
                    return;
                }
                case SimdTier.Avx256:
                {
                    var fv = Vector256.Create(factor);
                    for (int h = 0; h < BlockWidth; h += 4)
                    {
                        var d = Vector256.LoadUnsafe(ref w0, (nuint)(dst + h));
                        var s = Vector256.LoadUnsafe(ref w0, (nuint)(src + h));
                        (d - fv * s).StoreUnsafe(ref w0, (nuint)(dst + h));
                    }
                    return;
                }
                case SimdTier.Sse128:
                {
                    var fv = Vector128.Create(factor);
                    for (int h = 0; h < BlockWidth; h += 2)
                    {
                        var d = Vector128.LoadUnsafe(ref w0, (nuint)(dst + h));
                        var s = Vector128.LoadUnsafe(ref w0, (nuint)(src + h));
                        (d - fv * s).StoreUnsafe(ref w0, (nuint)(dst + h));
                    }
                    return;
                }
                default:
                    for (int l = 0; l < BlockWidth; l++)
                        Unsafe.Add(ref w0, dst + l) -=
                            factor * Unsafe.Add(ref w0, src + l);
                    return;
            }
        }

        /// <summary>Lanes x[0..8) /= divisor (IEEE division is correctly rounded at every width).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Divide8(ref double w0, int off, double divisor, SimdTier tier)
        {
            switch (tier)
            {
                case SimdTier.Avx512:
                    (Vector512.LoadUnsafe(ref w0, (nuint)off)
                        / Vector512.Create(divisor)).StoreUnsafe(ref w0, (nuint)off);
                    return;
                case SimdTier.Avx256:
                {
                    var dv = Vector256.Create(divisor);
                    for (int h = 0; h < BlockWidth; h += 4)
                        (Vector256.LoadUnsafe(ref w0, (nuint)(off + h)) / dv)
                            .StoreUnsafe(ref w0, (nuint)(off + h));
                    return;
                }
                case SimdTier.Sse128:
                {
                    var dv = Vector128.Create(divisor);
                    for (int h = 0; h < BlockWidth; h += 2)
                        (Vector128.LoadUnsafe(ref w0, (nuint)(off + h)) / dv)
                            .StoreUnsafe(ref w0, (nuint)(off + h));
                    return;
                }
                default:
                    for (int l = 0; l < BlockWidth; l++)
                        Unsafe.Add(ref w0, off + l) /= divisor;
                    return;
            }
        }

        /// <summary>The full reference arithmetic (dense scatter, full <see cref="Solve"/>, gather) - used only
        /// when a factor entry is not finite, so that case too reproduces the original bits exactly.</summary>
        private double EvaluateReference(ReadOnlySpan<(int Col, double Val)> h, double[] work)
        {
            for (int p = 0; p < h.Length; p++) work[h[p].Col] = h[p].Val;
            double[] x = _f.Solve(work);
            for (int p = 0; p < h.Length; p++) work[h[p].Col] = 0.0;
            double q = 0;
            for (int p = 0; p < h.Length; p++) q += h[p].Val * x[h[p].Col];
            return q;
        }

        private static bool AllFinite(double[] a, int count)
        {
            for (int i = 0; i < count; i++)
                if (!double.IsFinite(a[i])) return false;
            return true;
        }
    }

    /// <summary>Sparse lower-triangular solve x = L \ A(:,col), returning the pattern top.</summary>
    private static int SpSolve(int col, int[] ap, int[] ai, double[] ax,
        int[] lp, int[] li, double[] lx, int[] pinv, int n,
        int[] xi, int[] pstack, bool[] marked, double[] x)
    {
        int top = Reach(col, ap, ai, lp, li, pinv, n, xi, pstack, marked);

        for (int p = top; p < n; p++) x[xi[p]] = 0.0;               // clear the pattern
        for (int p = ap[col]; p < ap[col + 1]; p++) x[ai[p]] = ax[p]; // scatter A(:,col)

        for (int px = top; px < n; px++)
        {
            int j = xi[px];        // nodes come out in topological order
            int jcol = pinv[j];    // its column in L (or -1 if not yet a pivot row)
            if (jcol < 0) continue;
            double xj = x[j];      // unit diagonal => x[j] is already final
            for (int p = lp[jcol] + 1; p < lp[jcol + 1]; p++)
                x[li[p]] -= lx[p] * xj;
        }
        return top;
    }

    /// <summary>Nonzero reach of A(:,col) in the graph of L: fills xi[top..n-1] in topo order.</summary>
    private static int Reach(int col, int[] ap, int[] ai, int[] lp, int[] li, int[] pinv,
        int n, int[] xi, int[] pstack, bool[] marked)
    {
        int top = n;
        for (int p = ap[col]; p < ap[col + 1]; p++)
        {
            int j = ai[p];
            if (!marked[j]) top = Dfs(j, lp, li, pinv, top, xi, pstack, marked);
        }
        for (int p = top; p < n; p++) marked[xi[p]] = false; // leave marked[] all-false
        return top;
    }

    /// <summary>Iterative depth-first search from node j over the columns of L.</summary>
    private static int Dfs(int j, int[] lp, int[] li, int[] pinv, int top,
        int[] xi, int[] pstack, bool[] marked)
    {
        int head = 0;
        xi[0] = j;
        while (head >= 0)
        {
            j = xi[head];
            int jcol = pinv[j];   // column of L for node j (-1 => leaf, no outgoing edges)
            if (!marked[j])
            {
                marked[j] = true;
                pstack[head] = jcol < 0 ? 0 : lp[jcol] + 1; // skip the unit diagonal
            }
            bool done = true;
            int pend = jcol < 0 ? 0 : lp[jcol + 1];
            for (int p = pstack[head]; p < pend; p++)
            {
                int i = li[p];
                if (marked[i]) continue;
                pstack[head] = p + 1; // resume here after visiting i
                xi[++head] = i;       // descend into i
                done = false;
                break;
            }
            if (done)
            {
                head--;
                xi[--top] = j;        // finished j: prepend to output (reverse postorder)
            }
        }
        return top;
    }

    private static void Grow(ref int[] a, int needed)
    {
        int cap = a.Length * 2;
        while (cap < needed) cap *= 2;
        Array.Resize(ref a, cap);
    }

    private static void Grow(ref double[] a, int needed)
    {
        int cap = a.Length * 2;
        while (cap < needed) cap *= 2;
        Array.Resize(ref a, cap);
    }
}
