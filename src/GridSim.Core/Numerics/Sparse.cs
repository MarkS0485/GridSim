namespace GridSim.Core.Numerics;

/// <summary>
/// A real sparse matrix in compressed-sparse-row (CSR) form, with columns sorted
/// within each row and a cached pointer to each diagonal. Built from triplets
/// (duplicates summed left to right in the order they were added, D-DOC-026). This is the storage the large-network power flow assembles
/// its Jacobian into  -  each row touches only a handful of neighbours, so the dense
/// O(n^2) cost collapses to O(nnz).
/// </summary>
public sealed class CsrMatrix
{
    public int N { get; }
    public int[] RowPtr { get; }
    public int[] ColIdx { get; }
    public double[] Val { get; }
    public int[] DiagPtr { get; }   // index into Val/ColIdx of each row's diagonal, or -1

    private CsrMatrix(int n, int[] rowPtr, int[] colIdx, double[] val, int[] diagPtr)
    { N = n; RowPtr = rowPtr; ColIdx = colIdx; Val = val; DiagPtr = diagPtr; }

    /// <summary>y = Ax.</summary>
    public void Multiply(double[] x, double[] y)
    {
        for (int i = 0; i < N; i++)
        {
            double s = 0;
            for (int k = RowPtr[i]; k < RowPtr[i + 1]; k++) s += Val[k] * x[ColIdx[k]];
            y[i] = s;
        }
    }

    /// <summary>Incremental builder: accumulate (row, col, value) triplets, then <see cref="Build"/>.</summary>
    public sealed class Builder
    {
        private readonly int _n;
        private readonly List<int> _rows = new();
        private readonly List<int> _cols = new();
        private readonly List<double> _vals = new();

        public Builder(int n) => _n = n;

        public void Add(int row, int col, double value)
        {
            // GR-451: an entry outside the n x n matrix is a caller bug; it used to be stored and surface later as an
            // IndexOutOfRangeException (or a wrong matrix) far from its cause.
            if (value == 0.0) return;
            if ((uint)row >= (uint)_n || (uint)col >= (uint)_n)
                throw new ArgumentOutOfRangeException(nameof(row), $"Entry ({row}, {col}) is outside the {_n} x {_n} matrix.");
            _rows.Add(row); _cols.Add(col); _vals.Add(value);
        }

        public CsrMatrix Build(bool ensureDiagonal = true)
        {
            int nnzRaw = _vals.Count;
            var count = new int[_n + 1];
            for (int t = 0; t < nnzRaw; t++) count[_rows[t] + 1]++;

            // Over-allocate room for explicit diagonals we may inject.
            var rowStart = new int[_n + 1];
            for (int i = 0; i < _n; i++) rowStart[i + 1] = rowStart[i] + count[i + 1];

            var colTmp = new int[nnzRaw];
            var valTmp = new double[nnzRaw];
            var fill = (int[])rowStart.Clone();
            for (int t = 0; t < nnzRaw; t++)
            {
                int r = _rows[t];
                int p = fill[r]++;
                colTmp[p] = _cols[t];
                valTmp[p] = _vals[t];
            }

            // Per row: sort by column, sum duplicates, optionally inject a zero diagonal.
            var outCol = new List<int>(nnzRaw + _n);
            var outVal = new List<double>(nnzRaw + _n);
            var outRowPtr = new int[_n + 1];
            var diagPtr = new int[_n];

            var idx = new int[0];
            var keys = new long[0]; // parallel (column, insertion index) keys - sort by these, no per-row delegate
            for (int i = 0; i < _n; i++)
            {
                int s = rowStart[i], e = rowStart[i + 1];
                int len = e - s;
                if (idx.Length < len) { idx = new int[len]; keys = new long[len]; }
                // D-DOC-026 (docs-session finding): a STABLE sort by column. The key is (column, insertion index),
                // so the entries of one cell keep the order they were added in, and that order is the summation
                // order of a duplicated cell (an EMT node's diagonal collects one stamp per branch and shunt; a
                // DistributionFactors diagonal one per branch; PowerFlowTracing one per parallel flow). The key used
                // to be the column alone, and Array.Sort is an introsort, which does not keep equal keys in order:
                // a 3-entry row is sorted by a three-comparison network that can carry an equal key past another,
                // and a row of more than 16 entries by quicksort / heapsort. With two duplicates the order cannot
                // change the sum (a + b == b + a exactly), but with three or more in a row over 16 entries the
                // grouping, and so the last bit, was set by the sort's pivots. Keys are now unique, so the result no
                // longer depends on the sort algorithm at all: for every row the old sort kept in order (every row
                // of 2 or 4..16 entries, and any row without three duplicates of one cell) nothing changes, bit for
                // bit. ComplexCsr.Builder below had the same fix in N004.
                for (int k = 0; k < len; k++) { idx[k] = s + k; keys[k] = ((long)colTmp[s + k] << 32) | (uint)k; }
                Array.Sort(keys, idx, 0, len);

                outRowPtr[i] = outCol.Count;
                diagPtr[i] = -1;
                int prevCol = int.MinValue;
                bool diagWritten = false;
                for (int k = 0; k < len; k++)
                {
                    int c = colTmp[idx[k]];
                    double v = valTmp[idx[k]];
                    if (c == prevCol) { outVal[^1] += v; }
                    else
                    {
                        if (ensureDiagonal && !diagWritten && prevCol < i && c > i)
                        { diagPtr[i] = outCol.Count; outCol.Add(i); outVal.Add(0.0); diagWritten = true; }
                        outCol.Add(c); outVal.Add(v); prevCol = c;
                        if (c == i) { diagPtr[i] = outCol.Count - 1; diagWritten = true; }
                    }
                }
                if (ensureDiagonal && !diagWritten)
                { diagPtr[i] = outCol.Count; outCol.Add(i); outVal.Add(0.0); }
            }
            outRowPtr[_n] = outCol.Count;

            return new CsrMatrix(_n, outRowPtr, outCol.ToArray(), outVal.ToArray(), diagPtr);
        }
    }
}

/// <summary>
/// A complex sparse matrix (real part G, imaginary part B sharing one pattern)  -  the
/// network admittance matrix Y in sparse form. Built from branch stamps; lets the
/// large-network power flow compute injections and assemble the Jacobian by walking
/// each bus's handful of neighbours instead of a dense nxn sweep.
/// </summary>
public sealed class ComplexCsr
{
    public int N { get; }
    public int[] RowPtr { get; }
    public int[] ColIdx { get; }
    public double[] G { get; }
    public double[] B { get; }
    public int[] DiagPtr { get; }

    private ComplexCsr(int n, int[] rowPtr, int[] colIdx, double[] g, double[] b, int[] diag)
    { N = n; RowPtr = rowPtr; ColIdx = colIdx; G = g; B = b; DiagPtr = diag; }

    public sealed class Builder
    {
        private readonly int _n;
        private readonly List<int> _r = new(), _c = new();
        private readonly List<double> _g = new(), _b = new();
        public Builder(int n) => _n = n;

        public void Add(int row, int col, double g, double b)
        {
            // GR-451: as CsrMatrix.Builder.Add.
            if (g == 0.0 && b == 0.0) return;
            if ((uint)row >= (uint)_n || (uint)col >= (uint)_n)
                throw new ArgumentOutOfRangeException(nameof(row), $"Entry ({row}, {col}) is outside the {_n} x {_n} matrix.");
            _r.Add(row); _c.Add(col); _g.Add(g); _b.Add(b);
        }

        public ComplexCsr Build()
        {
            int nnz = _g.Count;
            var rowStart = new int[_n + 1];
            for (int t = 0; t < nnz; t++) rowStart[_r[t] + 1]++;
            for (int i = 0; i < _n; i++) rowStart[i + 1] += rowStart[i];

            var col = new int[nnz]; var gv = new double[nnz]; var bv = new double[nnz];
            var fill = (int[])rowStart.Clone();
            for (int t = 0; t < nnz; t++) { int p = fill[_r[t]]++; col[p] = _c[t]; gv[p] = _g[t]; bv[p] = _b[t]; }

            var outCol = new List<int>(nnz + _n);
            var outG = new List<double>(nnz + _n);
            var outB = new List<double>(nnz + _n);
            var outRowPtr = new int[_n + 1];
            var diag = new int[_n];
            var order = new int[0];
            var keys = new long[0]; // parallel (column, insertion index) keys - sort by these, no delegate

            for (int i = 0; i < _n; i++)
            {
                int s = rowStart[i], e = rowStart[i + 1], len = e - s;
                if (order.Length < len) { order = new int[len]; keys = new long[len]; }
                // STABLE sort by column: the key is (column, insertion index), so entries for the same cell
                // keep their insertion order. That order is the summation order of a duplicated cell (a bus
                // diagonal collects one stamp per connected branch plus its shunt), and it must be EXACTLY
                // the dense YBus.Build order (branches, then shunts, as added) for the sparse and dense
                // Newton-Raphson engines to be bit-identical. Array.Sort is an introsort and is not stable: a
                // 2-entry row (one compare-and-swap) and a 4..16-entry row (insertion sort) happen to keep equal
                // keys in order, but a 3-entry row goes through a three-comparison network that can carry an
                // equal key past another, and a row of more than 16 entries through quicksort / heapsort - which
                // summed the diagonal of any bus with 9+ branches in a different order (October 2026, N004).
                // (D-DOC-026: this comment used to say every row below 17 entries was kept in order; the 3-entry
                // case was the exception.) Keys are unique, so the result no longer depends on the sort
                // algorithm at all.
                for (int k = 0; k < len; k++) { order[k] = s + k; keys[k] = ((long)col[s + k] << 32) | (uint)k; }
                Array.Sort(keys, order, 0, len);

                outRowPtr[i] = outCol.Count;
                diag[i] = -1;
                int prev = int.MinValue;
                bool diagWritten = false;
                for (int k = 0; k < len; k++)
                {
                    int cc = col[order[k]]; double g = gv[order[k]], b = bv[order[k]];
                    if (cc == prev) { outG[^1] += g; outB[^1] += b; }
                    else
                    {
                        if (!diagWritten && prev < i && cc > i)
                        { diag[i] = outCol.Count; outCol.Add(i); outG.Add(0); outB.Add(0); diagWritten = true; }
                        // Start each cell from +0.0 exactly as the dense build does (y = 0; y += stamp): a
                        // first stamp of -0.0 becomes +0.0, so even signed zeros match the dense Y-bus.
                        outCol.Add(cc); outG.Add(0.0 + g); outB.Add(0.0 + b); prev = cc;
                        if (cc == i) { diag[i] = outCol.Count - 1; diagWritten = true; }
                    }
                }
                if (!diagWritten) { diag[i] = outCol.Count; outCol.Add(i); outG.Add(0); outB.Add(0); }
            }
            outRowPtr[_n] = outCol.Count;
            return new ComplexCsr(_n, outRowPtr, outCol.ToArray(), outG.ToArray(), outB.ToArray(), diag);
        }
    }
}

/// <summary>
/// An ILU(0) factorisation  -  an incomplete LU that keeps the sparsity pattern of the
/// original matrix (no fill). It's a cheap, effective preconditioner: applying it
/// brings BiCGSTAB's effective system close to the identity, so the iterative solve
/// converges in a handful of steps even on a stiff power-flow Jacobian.
/// </summary>
public sealed class Ilu0
{
    private readonly CsrMatrix _a;
    private readonly double[] _lu;     // combined L\U in A's pattern
    private readonly int[] _diag;

    private Ilu0(CsrMatrix a, double[] lu, int[] diag) { _a = a; _lu = lu; _diag = diag; }

    public static Ilu0 Factor(CsrMatrix a)
    {
        int n = a.N;
        int[] rp = a.RowPtr, ci = a.ColIdx, dp = a.DiagPtr;
        // GR-461: ILU(0) pivots on the stored diagonal, so every row needs one (a CSR built with ensureDiagonal: false
        // may have none, DiagPtr -1). It used to index -1 and throw IndexOutOfRangeException.
        for (int i = 0; i < n; i++)
            if (dp[i] < 0)
                throw new ArgumentException($"ILU(0): row {i} has no stored diagonal entry; build the matrix with its diagonal.", nameof(a));
        var lu = (double[])a.Val.Clone();
        var iw = new int[n];
        Array.Fill(iw, -1);

        for (int i = 0; i < n; i++)
        {
            for (int k = rp[i]; k < rp[i + 1]; k++) iw[ci[k]] = k;

            for (int k = rp[i]; k < dp[i]; k++)   // strictly-lower entries: col j < i
            {
                int j = ci[k];
                double piv = lu[dp[j]];
                double lij = (piv != 0.0) ? lu[k] / piv : 0.0;
                lu[k] = lij;
                if (lij == 0.0) continue;
                for (int kk = dp[j] + 1; kk < rp[j + 1]; kk++) // upper of row j
                {
                    int col = ci[kk];
                    int pos = iw[col];
                    if (pos != -1) lu[pos] -= lij * lu[kk];
                }
            }

            // Guard a tiny preconditioner pivot, RELATIVE to this row's scale in the original matrix (R183;
            // was an absolute 1e-12). Regularising here is legitimate: ILU(0) only steers BiCGSTAB, whose own
            // residual test against the true matrix decides success.
            double rowScale = 0.0;
            for (int k = rp[i]; k < rp[i + 1]; k++) { double av = Math.Abs(a.Val[k]); if (av > rowScale) rowScale = av; }
            double guard = NumericTolerances.Ilu0PivotGuard(rowScale);
            double d = lu[dp[i]];
            if (Math.Abs(d) < guard) lu[dp[i]] = d < 0 ? -guard : guard;

            for (int k = rp[i]; k < rp[i + 1]; k++) iw[ci[k]] = -1;
        }
        return new Ilu0(a, lu, dp);
    }

    /// <summary>Solve (LU)z = r in place into z (unit-lower L, upper U).</summary>
    public void Apply(double[] r, double[] z)
    {
        int n = _a.N;
        int[] rp = _a.RowPtr, ci = _a.ColIdx;
        for (int i = 0; i < n; i++)
        {
            double s = r[i];
            for (int k = rp[i]; k < _diag[i]; k++) s -= _lu[k] * z[ci[k]];
            z[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = z[i];
            for (int k = _diag[i] + 1; k < rp[i + 1]; k++) s -= _lu[k] * z[ci[k]];
            z[i] = s / _lu[_diag[i]];
        }
    }
}

/// <summary>
/// Preconditioned BiCGSTAB  -  a Krylov iterative solver for the nonsymmetric linear
/// systems a Newton step produces. With the <see cref="Ilu0"/> preconditioner it
/// converges fast and uses only sparse mat-vecs, so it scales to tens of thousands
/// of buses where a dense factorisation would not fit.
/// </summary>
public static class BiCgStab
{
    /// <param name="tol">Relative residual tolerance ||r|| / ||b|| (default
    /// <see cref="NumericTolerances.DefaultKrylovTolerance"/>).</param>
    /// <param name="maxIter">Iteration cap; 0 (the default) selects
    /// <see cref="NumericTolerances.DefaultKrylovMaxIterations"/> for this system, the same cap every other
    /// BiCGSTAB in GridSim uses (it was a separate fixed 2000 here).</param>
    public static bool Solve(CsrMatrix a, double[] b, double[] x, Ilu0 precond,
        double tol = NumericTolerances.DefaultKrylovTolerance, int maxIter = 0)
    {
        if (!(tol > 0.0) || double.IsInfinity(tol))
            throw new ArgumentOutOfRangeException(nameof(tol), tol, "The BiCGSTAB tolerance must be a positive finite number.");
        if (maxIter < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIter), maxIter, "The BiCGSTAB iteration cap must be zero (default) or positive.");
        int n = a.N;
        if (maxIter == 0) maxIter = NumericTolerances.DefaultKrylovMaxIterations(n);
        Array.Clear(x, 0, n);
        var r = (double[])b.Clone();           // r = b - A x, x=0 => r=b
        var rhat = (double[])r.Clone();
        var p = new double[n];
        var v = new double[n];
        var s = new double[n];
        var t = new double[n];
        var ph = new double[n];
        var sh = new double[n];

        double bnorm = Norm(b);
        // GR-448: x = 0 is the answer only for an exactly zero b. A non-zero b whose norm underflows to 0 (every entry
        // below about 1e-162) is not solved by x = 0; it is reported as a failure, as the GPU solver already does.
        if (bnorm == 0) return Array.TrueForAll(b, bi => bi == 0.0);
        double rho = 1, alpha = 1, omega = 1;

        for (int it = 0; it < maxIter; it++)
        {
            double rhoNew = Dot(rhat, r);
            if (Math.Abs(rhoNew) < NumericTolerances.UnderflowGuard || double.IsNaN(rhoNew)) return false; // breakdown
            double beta = (rhoNew / rho) * (alpha / omega);
            for (int i = 0; i < n; i++) p[i] = r[i] + beta * (p[i] - omega * v[i]);

            precond.Apply(p, ph);
            a.Multiply(ph, v);
            double rhatV = Dot(rhat, v);
            // A zero (rhat, v) is a breakdown: dividing by it would fill x with Inf/NaN and burn the rest of the
            // iteration budget before reporting failure. Report it now (same false, just without the waste).
            if (rhatV == 0.0 || double.IsNaN(rhatV)) return false;
            alpha = rhoNew / rhatV;

            for (int i = 0; i < n; i++) s[i] = r[i] - alpha * v[i];
            if (Norm(s) / bnorm < tol)
            {
                for (int i = 0; i < n; i++) x[i] += alpha * ph[i];
                return TrueResidualMeets(a, b, x, bnorm, tol, t);
            }

            precond.Apply(s, sh);
            a.Multiply(sh, t);
            double tt = Dot(t, t);
            omega = tt > 0 ? Dot(t, s) / tt : 0.0;

            for (int i = 0; i < n; i++) { x[i] += alpha * ph[i] + omega * sh[i]; r[i] = s[i] - omega * t[i]; }
            rho = rhoNew;

            if (Norm(r) / bnorm < tol) return TrueResidualMeets(a, b, x, bnorm, tol, t);
            if (omega == 0) return false;
        }
        return false;
    }

    /// <summary>
    /// The success decision on the TRUE residual. BiCGSTAB's r and s are updated by recurrence, and in finite precision
    /// the recursive residual drifts away from b - A x, worst on the ill-conditioned systems that reach this solver (the
    /// Newton fallback runs only after the sparse LU rejected the matrix). So when the recursive residual meets the
    /// tolerance, b - A x is computed with one more product and must meet it too; otherwise the solve reports failure
    /// rather than hand back a step that was not solved to the stated tolerance. When the recursion was honest (every
    /// ordinary solve) the answer and x are exactly what they were. <paramref name="scratch"/> is overwritten.
    /// </summary>
    private static bool TrueResidualMeets(CsrMatrix a, double[] b, double[] x, double bnorm, double tol, double[] scratch)
    {
        a.Multiply(x, scratch);
        double sum = 0.0;
        for (int i = 0; i < b.Length; i++)
        {
            double d = b[i] - scratch[i];
            sum += d * d;
        }
        return Math.Sqrt(sum) / bnorm < tol;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s;
    }
    private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
}
