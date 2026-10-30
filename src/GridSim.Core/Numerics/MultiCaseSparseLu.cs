using System.Runtime.Intrinsics;

namespace GridSim.Core.Numerics;

/// <summary>
/// The multi-case sparse LU: up to <see cref="Lanes"/> matrices with one sparsity pattern (Monte Carlo cases, scenario
/// sets, a run of measurement frames) factored and solved together, one case per SIMD lane, on a
/// <see cref="SparseLuPlan"/> recorded from a full factorisation.
///
/// <para><b>Bit-identity.</b> Each lane does exactly the arithmetic <see cref="SparseLuFactorization.Refactor"/> does for
/// that case alone, operation for operation and in the same order (lane-wise IEEE subtraction, multiplication and
/// division, never a fused multiply-add), and <see cref="SparseLuFactorization.Refactor"/> does exactly what a fresh
/// <see cref="SparseLuFactorization.Factor"/> does. So every lane's L, U and solution hold the bits the single-case path
/// gives. The lane-wise operations are IEEE operations on each element whether the hardware runs them as AVX2 / AVX-512
/// instructions or the runtime runs them element by element (hardware intrinsics off), so the bits do not depend on the
/// CPU either.</para>
///
/// <para><b>Fallback.</b> A lane whose CSC pattern differs from the plan's (a value-dependent dropped zero), or whose
/// pivot search picks a different row from the plan's at any column, cannot share the plan: that lane is factored alone
/// by <see cref="SparseLuFactorization.Factor"/>, exactly as a single case would be (and reported in
/// <see cref="Batch.SharedLanes"/>). A lane whose matrix is singular gives null, as the single path does.</para>
/// </summary>
internal static class MultiCaseSparseLu
{
    /// <summary>The cases one batch carries: the width of <see cref="Vector256{T}"/> of double.</summary>
    internal const int Lanes = 4;

    /// <summary>A factored batch: the shared-plan lanes' interleaved L and U values, and each lane's own factorisation
    /// when it fell back (or null when its matrix is singular).</summary>
    internal sealed class Batch
    {
        internal Batch(SparseLuPlan? plan, int n, int count, double[] lx, double[] ux, bool[] shared, SparseLuFactorization?[] own, bool[] singular)
        {
            Plan = plan; N = n; Count = count; _lx = lx; _ux = ux; _shared = shared; _own = own; _singular = singular;
        }

        private readonly double[] _lx, _ux;   // interleaved: entry p of lane l at p * Lanes + l
        private readonly bool[] _shared, _singular;
        private readonly SparseLuFactorization?[] _own;

        /// <summary>The plan the shared lanes used (null when every lane was factored alone).</summary>
        internal SparseLuPlan? Plan { get; }

        /// <summary>The order of every lane's matrix.</summary>
        internal int N { get; }

        /// <summary>How many cases the batch holds (1 to <see cref="Lanes"/>).</summary>
        internal int Count { get; }

        /// <summary>Which lanes shared the plan (the rest were factored alone or are singular).</summary>
        internal IReadOnlyList<bool> SharedLanes => _shared;

        /// <summary>Whether lane <paramref name="lane"/>'s matrix is singular (no factorisation).</summary>
        internal bool IsSingular(int lane) => _singular[lane];

        /// <summary>Lane <paramref name="lane"/>'s factorisation as a single-case object (the shared lanes' values copied
        /// out of the interleaved arrays), for checks and for callers that want one case.</summary>
        internal SparseLuFactorization? Lane(int lane)
        {
            if (_singular[lane]) return null;
            if (!_shared[lane]) return _own[lane];
            SparseLuPlan plan = Plan!;   // a shared lane implies a plan
            var lx = new double[plan.Lp[plan.N]];
            var ux = new double[plan.Up[plan.N]];
            for (int p = 0; p < lx.Length; p++) lx[p] = _lx[p * Lanes + lane];
            for (int p = 0; p < ux.Length; p++) ux[p] = _ux[p * Lanes + lane];
            return SparseLuFactorization.FromPlanValues(plan, lx, ux);
        }

        /// <summary>
        /// Solve every lane's system A_l x_l = b_l: <paramref name="b"/>[l] and <paramref name="x"/>[l] of length n. The
        /// shared lanes' forward and back sweeps run together in the SIMD lanes, each lane exactly the arithmetic of
        /// <see cref="SparseLuFactorization.SolveInto"/>; a fallen-back lane uses its own factorisation; a singular
        /// lane's x is filled with NaN.
        /// </summary>
        internal void SolveInto(double[][] b, double[][] x)
        {
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(x);
            int n = N;
            if (b.Length != Count || x.Length != Count)
                throw new ArgumentException($"multi-case solve: the batch holds {Count} cases but b has {b.Length} and x has {x.Length}.");
            for (int l = 0; l < Count; l++)
                if (b[l] is null || x[l] is null || b[l].Length != n || x[l].Length != n)
                    throw new ArgumentException($"multi-case solve: case {l} needs b and x of length {n}.");
            bool anyShared = false;
            for (int l = 0; l < Count; l++)
            {
                if (_singular[l]) { Array.Fill(x[l], double.NaN); continue; }
                if (!_shared[l]) { _own[l]!.SolveInto(b[l], x[l], new double[n]); continue; }
                anyShared = true;
            }
            if (!anyShared) return;
            SparseLuPlan plan = Plan!;
            int[] lp = plan.Lp, li = plan.LiStep, up = plan.Up, ui = plan.Ui, pinv = plan.PivotStep, q = plan.Q;
            var w = new double[n * Lanes];
            for (int l = 0; l < Count; l++)
            {
                if (!_shared[l] || _singular[l]) continue;
                for (int k = 0; k < n; k++) w[pinv[k] * Lanes + l] = b[l][k];
            }
            // Forward: unit lower L (diagonal first in each column).
            for (int j = 0; j < n; j++)
            {
                Vector256<double> xj = Vector256.LoadUnsafe(ref w[j * Lanes]);
                for (int p = lp[j] + 1; p < lp[j + 1]; p++)
                {
                    int r = li[p] * Lanes;
                    Vector256<double> cur = Vector256.LoadUnsafe(ref w[r]);
                    Vector256<double> l = Vector256.LoadUnsafe(ref _lx[p * Lanes]);
                    Store(w, r, cur - l * xj);
                }
            }
            // Back: U with the pivot last in each column.
            for (int j = n - 1; j >= 0; j--)
            {
                int d = (up[j + 1] - 1) * Lanes;
                Vector256<double> piv = Vector256.LoadUnsafe(ref _ux[d]);
                Vector256<double> xj = Vector256.LoadUnsafe(ref w[j * Lanes]) / piv;
                Store(w, j * Lanes, xj);
                for (int p = up[j]; p < up[j + 1] - 1; p++)
                {
                    int r = ui[p] * Lanes;
                    Vector256<double> cur = Vector256.LoadUnsafe(ref w[r]);
                    Vector256<double> u = Vector256.LoadUnsafe(ref _ux[p * Lanes]);
                    Store(w, r, cur - u * xj);
                }
            }
            for (int l = 0; l < Count; l++)
            {
                if (!_shared[l] || _singular[l]) continue;
                for (int k = 0; k < n; k++) x[l][q[k]] = w[k * Lanes + l];
            }
        }
    }

    private static void Store(double[] a, int at, Vector256<double> v) => v.StoreUnsafe(ref a[at]);

    /// <summary>
    /// Factor up to <see cref="Lanes"/> cases on <paramref name="plan"/>: <paramref name="ap"/>[l],
    /// <paramref name="ai"/>[l], <paramref name="ax"/>[l] are case l's CSC arrays (explicit zeros dropped, as
    /// <see cref="SparseLu.ToCsc"/> gives them). See the type summary for the bit-identity and fallback rules.
    /// </summary>
    internal static Batch Factor(SparseLuPlan plan, int[][] ap, int[][] ai, double[][] ax)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int count = ax.Length;
        if (count < 1 || count > Lanes) throw new ArgumentOutOfRangeException(nameof(ax), count, $"a batch holds 1 to {Lanes} cases");
        if (ap.Length != count || ai.Length != count) throw new ArgumentException("ap, ai and ax must hold one entry per case.");
        int n = plan.N;
        int[] pap = plan.Ap, pai = plan.Ai;
        var shared = new bool[count];
        var singular = new bool[count];
        var own = new SparseLuFactorization?[count];
        var floor = new double[Lanes];
        var live = new bool[Lanes];
        for (int l = 0; l < count; l++)
        {
            if (!plan.SamePattern(ap[l], ai[l])) continue;   // a different pattern: alone, below
            double aNorm = NumericTolerances.MaxAbs(ax[l].AsSpan(0, pap[n]));
            if (double.IsNaN(aNorm))
                throw new ArgumentException($"Sparse LU: case {l} contains a NaN entry, so it cannot be factorised.", nameof(ax));
            if (double.IsInfinity(aNorm))
                throw new ArgumentException($"Sparse LU: case {l} contains an infinite entry, so it cannot be factorised.", nameof(ax));
            floor[l] = NumericTolerances.PivotFloor(aNorm);
            live[l] = true;
        }

        int[] lp = plan.Lp, liO = plan.LiOriginal, up = plan.Up, step = plan.PivotStep, reach = plan.Reach, rp = plan.ReachPtr, q = plan.Q;
        var lx = new double[lp[n] * Lanes];
        var ux = new double[up[n] * Lanes];
        var x = new double[n * Lanes];
        int lnz = 0, unz = 0;
        bool any = live.Any(v => v);
        for (int k = 0; k < n && any; k++)
        {
            int r0 = rp[k], r1 = rp[k + 1];
            int col = q[k];
            for (int p = r0; p < r1; p++) { int b = reach[p] * Lanes; x[b] = 0.0; x[b + 1] = 0.0; x[b + 2] = 0.0; x[b + 3] = 0.0; }
            for (int l = 0; l < count; l++)
            {
                if (!live[l]) continue;
                double[] axl = ax[l];
                for (int p = pap[col]; p < pap[col + 1]; p++) x[pai[p] * Lanes + l] = axl[p];
            }
            for (int px = r0; px < r1; px++)
            {
                int j = reach[px];
                int jcol = step[j];
                if (jcol >= k) continue;
                int jb = j * Lanes;
                Vector256<double> xj = Vector256.LoadUnsafe(ref x[jb]);
                for (int p = lp[jcol] + 1; p < lp[jcol + 1]; p++)
                {
                    int r = liO[p] * Lanes;
                    Vector256<double> cur = Vector256.LoadUnsafe(ref x[r]);
                    Vector256<double> lv = Vector256.LoadUnsafe(ref lx[p * Lanes]);
                    Store(x, r, cur - lv * xj);
                }
            }
            // Each lane's pivot search, in the reach order; a lane that picks another row, or hits the floor, leaves.
            for (int l = 0; l < count; l++)
            {
                if (!live[l]) continue;
                int ipiv = -1;
                double best = -1.0;
                for (int px = r0; px < r1; px++)
                {
                    int i = reach[px];
                    if (step[i] >= k)
                    {
                        double t = Math.Abs(x[i * Lanes + l]);
                        if (t > best) { best = t; ipiv = i; }
                    }
                }
                if (ipiv >= 0 && best > floor[l] && plan.DiagonalPreference > 0.0)
                    ipiv = SparseLuFactorization.PreferDiagonalInReach(col, ipiv, best, floor[l], plan.DiagonalPreference,
                        reach, r0, r1, x, step, k, Lanes, l);
                if (ipiv != plan.PivotRow[k]) { live[l] = false; continue; }
                if (best <= floor[l]) { live[l] = false; singular[l] = true; }
            }
            // U entries above the pivot, the pivot, then L below it (all lanes: a lane that left carries garbage that is
            // never read).
            for (int px = r0; px < r1; px++)
            {
                int i = reach[px];
                if (step[i] < k) { Copy4(x, i * Lanes, ux, unz * Lanes); unz++; }
            }
            int pr = plan.PivotRow[k] * Lanes;
            Vector256<double> piv = Vector256.LoadUnsafe(ref x[pr]);
            Store(ux, unz * Lanes, piv); unz++;
            lx[lnz * Lanes] = 1.0; lx[lnz * Lanes + 1] = 1.0; lx[lnz * Lanes + 2] = 1.0; lx[lnz * Lanes + 3] = 1.0; lnz++;
            for (int px = r0; px < r1; px++)
            {
                int i = reach[px];
                if (step[i] > k)
                {
                    int ib = i * Lanes;
                    Vector256<double> v = Vector256.LoadUnsafe(ref x[ib]);
                    Store(lx, lnz * Lanes, v / piv);
                    lnz++;
                }
            }
            for (int p = r0; p < r1; p++) { int b = reach[p] * Lanes; x[b] = 0.0; x[b + 1] = 0.0; x[b + 2] = 0.0; x[b + 3] = 0.0; }
            any = false;
            for (int l = 0; l < count; l++) any |= live[l];
        }

        for (int l = 0; l < count; l++)
        {
            if (live[l]) { shared[l] = true; continue; }
            if (singular[l]) continue;
            // Alone: the full factorisation, exactly as a single case.
            own[l] = SparseLuFactorization.Factor(n, ap[l], ai[l], ax[l], q, diagonalPreference: plan.DiagonalPreference);
            if (own[l] is null) singular[l] = true;
        }
        return new Batch(plan, n, count, lx, ux, shared, own, singular);
    }

    /// <summary>A batch with no plan to share (the family's template was singular): every case factored alone by
    /// <see cref="SparseLuFactorization.Factor"/> with ordering <paramref name="q"/>, exactly as a single case.</summary>
    internal static Batch FactorAlone(int n, int[][] ap, int[][] ai, double[][] ax, int[] q, double diagonalPreference = 0.0)
    {
        int count = ax.Length;
        if (count < 1 || count > Lanes) throw new ArgumentOutOfRangeException(nameof(ax), count, $"a batch holds 1 to {Lanes} cases");
        if (ap.Length != count || ai.Length != count) throw new ArgumentException("ap, ai and ax must hold one entry per case.");
        var own = new SparseLuFactorization?[count];
        var singular = new bool[count];
        for (int l = 0; l < count; l++)
        {
            own[l] = SparseLuFactorization.Factor(n, ap[l], ai[l], ax[l], q, diagonalPreference: diagonalPreference);
            singular[l] = own[l] is null;
        }
        return new Batch(null, n, count, Array.Empty<double>(), Array.Empty<double>(), new bool[count], own, singular);
    }

    private static void Copy4(double[] from, int at, double[] to, int dst)
    {
        to[dst] = from[at]; to[dst + 1] = from[at + 1]; to[dst + 2] = from[at + 2]; to[dst + 3] = from[at + 3];
    }
}

/// <summary>
/// A family of sparse matrices that share one CSR structure (a DC susceptance matrix under sampled branch reactances, a
/// time-domain network matrix under varied parameters), factored <see cref="MultiCaseSparseLu.Lanes"/> at a time in the
/// SIMD lanes of <see cref="MultiCaseSparseLu"/>.
///
/// <para><b>What each lane equals.</b> The single-case path for a matrix <c>a</c> of the family is
/// <see cref="SparseLu.Factor(CsrMatrix)"/>: the reverse Cuthill-McKee ordering of <c>a</c>'s CSR structure, then
/// <see cref="SparseLu.FactorWithOrdering"/>. The ordering depends on the CSR structure alone, so every member of the
/// family has the same ordering, computed here once from the template. Each lane is then the full factorisation with that
/// ordering, bit for bit (replayed on the template's plan, or factored alone when it cannot share it), so a lane's L, U
/// and solution are exactly what <see cref="SparseLu.Factor(CsrMatrix)"/> gives for that matrix.</para>
///
/// <para><b>Fail loud.</b> A matrix whose CSR structure differs from the template's is not a member of the family and is
/// refused with <see cref="ArgumentException"/> (naming its position): grouping matrices by structure is the caller's
/// job, and silently factoring a stranger with the family's ordering would not be the single-case path.</para>
///
/// <para>Immutable after construction, so one instance may serve many threads at once.</para>
/// </summary>
internal sealed class SparseLuFamily
{
    private readonly int[] _rowPtr;
    private readonly int[] _colIdx;

    /// <summary>The order of every matrix in the family.</summary>
    internal int N { get; }

    /// <summary>The family's column ordering (reverse Cuthill-McKee of the template's structure).</summary>
    internal int[] Ordering { get; }

    /// <summary>The plan recorded from the template's factorisation, or null when the template is singular (then every
    /// lane is factored alone, exactly as a single case).</summary>
    internal SparseLuPlan? Plan { get; }

    /// <summary>Set up the family of <paramref name="template"/>'s CSR structure: its ordering, and the plan of the
    /// template's own factorisation (the template's values choose the pivots the lanes try to share).</summary>
    /// <param name="template">The family's first member: its structure defines the family, its values the plan.</param>
    /// <param name="diagonalPreference">The pivot rule (see <see cref="SparseLuFactorization.Factor"/>): 0, the default,
    /// is plain partial pivoting, so each lane equals <see cref="SparseLu.Factor(CsrMatrix)"/>; a positive value is the
    /// OPT-IN diagonal-preference rule, and each lane then equals the full factorisation under that rule (not the
    /// default path's bits).</param>
    internal SparseLuFamily(CsrMatrix template, double diagonalPreference = 0.0)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.N == 0) throw new ArgumentException("A sparse LU family needs matrices of order at least 1.", nameof(template));
        N = template.N;
        _rowPtr = (int[])template.RowPtr.Clone();
        _colIdx = (int[])template.ColIdx.Clone();
        Ordering = SparseLu.Ordering(template);
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCsc(template);
        DiagonalPreference = diagonalPreference;
        Plan = SparseLuFactorization.Factor(N, ap, ai, ax, Ordering, keepPlan: true, diagonalPreference: diagonalPreference)?.Plan;
    }

    /// <summary>The family's pivot rule (0 = plain partial pivoting, the default).</summary>
    internal double DiagonalPreference { get; }

    /// <summary>Whether <paramref name="a"/> has exactly the family's CSR structure (order, row pointers, column indices).</summary>
    internal bool IsMember(CsrMatrix a)
        => a.N == N && a.RowPtr.AsSpan().SequenceEqual(_rowPtr) && a.ColIdx.AsSpan().SequenceEqual(_colIdx);

    /// <summary>
    /// Factor 1 to <see cref="MultiCaseSparseLu.Lanes"/> members of the family together. Lane l of the result is
    /// <paramref name="matrices"/>[l]. Throws <see cref="ArgumentException"/> for a matrix outside the family, and as the
    /// single path does for a NaN or infinite entry; a singular member is reported by the batch
    /// (<see cref="MultiCaseSparseLu.Batch.IsSingular"/>), as the single path reports it with null.
    /// </summary>
    internal MultiCaseSparseLu.Batch Factor(IReadOnlyList<CsrMatrix> matrices) => Factor(matrices, useLanes: true);

    /// <summary>The members factored one at a time by the full factorisation (no lanes): the same bits as
    /// <see cref="Factor(IReadOnlyList{CsrMatrix})"/>, for a family whose members are known not to share the plan (each
    /// would only fall back).</summary>
    internal MultiCaseSparseLu.Batch FactorAlone(IReadOnlyList<CsrMatrix> matrices) => Factor(matrices, useLanes: false);

    private MultiCaseSparseLu.Batch Factor(IReadOnlyList<CsrMatrix> matrices, bool useLanes)
    {
        ArgumentNullException.ThrowIfNull(matrices);
        int count = matrices.Count;
        if (count < 1 || count > MultiCaseSparseLu.Lanes)
            throw new ArgumentOutOfRangeException(nameof(matrices), count, $"a batch holds 1 to {MultiCaseSparseLu.Lanes} matrices");
        var ap = new int[count][];
        var ai = new int[count][];
        var ax = new double[count][];
        for (int l = 0; l < count; l++)
        {
            CsrMatrix a = matrices[l] ?? throw new ArgumentException($"sparse LU family: matrix {l} is null.", nameof(matrices));
            if (!IsMember(a))
                throw new ArgumentException(
                    $"sparse LU family: matrix {l} does not have the family's sparsity structure (order {a.N}, {a.ColIdx.Length} stored "
                    + $"entries; the family has order {N} and {_colIdx.Length}); factor it on its own or in its own family.", nameof(matrices));
            (ap[l], ai[l], ax[l]) = SparseLu.ToCsc(a);
        }
        return Plan is not null && useLanes
            ? MultiCaseSparseLu.Factor(Plan, ap, ai, ax)
            : MultiCaseSparseLu.FactorAlone(N, ap, ai, ax, Ordering, DiagonalPreference);
    }
}
