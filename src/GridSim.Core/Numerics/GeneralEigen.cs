using System.Numerics;

namespace GridSim.Core.Numerics;

/// <summary>
/// Eigenvalues and biorthonormal left/right eigenvectors of a small dense REAL, generally NON-SYMMETRIC
/// matrix - the state matrix A of a small-signal (modal) stability problem, whose asymmetric structure
/// (the ws*I angle-speed block vs the -M^-1 K speed-angle block, and the transfer-conductance asymmetry
/// of the synchronising matrix K itself) puts it outside <see cref="SymmetricEigen"/>'s reach and forces
/// complex eigenpairs. Self-contained (no BLAS/LAPACK); intended for the tens-of-states systems modal
/// analysis forms, exactly as <see cref="SymmetricEigen"/> is scoped.
///
/// <para><b>Algorithm.</b> Eigenvalues: Parlett-Reinsch <b>balancing</b> (a diagonal similarity that
/// trims the norm), an <b>upper-Hessenberg reduction by our own Householder reflectors</b> (a similarity,
/// so eigenvalues are preserved), then the <b>Francis double-shift implicit-QR</b> iteration (the
/// EISPACK/Numerical-Recipes <c>hqr</c> real-Schur eigenvalue kernel: 1x1 blocks = real eigenvalues, 2x2
/// blocks = complex-conjugate pairs). Eigenvectors: <b>inverse iteration</b> on the ORIGINAL A with the
/// computed eigenvalue as the shift gives the right vectors (no back-transform through balancing/Hessenberg);
/// the left vectors are the rows of W = V^-1, so psi^H phi = 1 by construction. (R090: this doc used to say the
/// left vectors came from inverse iteration on A^T; that path was never called and is removed.)</para>
///
/// <para><b>Honesty / conditioning.</b> Participation factors need biorthonormal left/right vectors
/// (psi^H phi = 1). We form that scaling explicitly and then <b>assert</b> |psi^H phi| is not tiny: a
/// defective or near-defective A (e.g. the undamped swing system's rigid-body COI Jordan block) has a
/// singular right-eigenvector matrix, so the biorthonormal scaling does not exist. Rather than fabricate a
/// participation vector, <see cref="Decompose"/> <b>throws</b> <see cref="DefectiveEigenproblemException"/>
/// - the caller (modal analysis) is expected to deflate the known null space first and re-call on the
/// non-defective electromechanical subspace.</para>
/// </summary>
public static class GeneralEigen
{
    // ---- Named constants (October 2026 review, WS13 R182). Values unchanged: each replaced the literal of the
    // ---- same value, so every eigenvalue and eigenvector is bit-identical. (The exceptional-shift constants of
    // ---- the Francis QR, its 10/20/30/40 iteration triggers and the 0.75 and -0.4375 multipliers, are the
    // ---- standard EISPACK hqr values and are commented in place.)

    /// <summary>Default number of inverse-iteration sweeps per eigenvector in <see cref="Decompose"/>.</summary>
    public const int DefaultInverseIterations = 40;

    /// <summary>Default biorthonormality tolerance of <see cref="Decompose"/>. A pair fails loud as defective when
    /// |psi^H phi - 1| exceeds the larger of this and <see cref="BiorthonormalitySlack"/> (R090: the old guard also
    /// required |psi^H phi| to be tiny, so a badly converged pair with psi^H phi = 0.5 passed silently and this
    /// parameter had almost no effect).</summary>
    public const double DefaultBiorthonormalityTolerance = 1e-9;

    /// <summary>|psi^H phi - 1| at or below this is accepted as biorthonormal outright.</summary>
    public const double BiorthonormalitySlack = 1e-6;

    /// <summary>Iterations on one eigenvalue up to which the original schedule applies (EISPACK hqr uses 30 per
    /// eigenvalue; 60 leaves room for its exceptional shifts at 10, 20, 30 and 40). Every decomposition that converged
    /// under the old cap of 60 runs exactly the same arithmetic as before.</summary>
    public const int QrMaxIterationsPerEigenvalue = 60;

    /// <summary>
    /// D1 (October 2026): the hard cap on Francis QR iterations spent on one eigenvalue before the decomposition fails
    /// loud, LAPACK dlahqr's ITMAX = 30 * max(10, n). Beyond <see cref="QrMaxIterationsPerEigenvalue"/> the iteration
    /// continues with an exceptional shift every 10 iterations (dlahqr's KEXSH = 10). Why: a defective state matrix
    /// (repeated eigenvalues with a Jordan block, such as data/gb-synthetic with --enable-inverters, 116 states)
    /// converges slowly, and with the deterministic fdlibm maths its last-bit-different matrix needed more than 60
    /// iterations where the OS libm's happened to need fewer: the old cap made the verdict depend on rounding luck.
    /// </summary>
    public static int QrHardCapIterations(int n) => 30 * Math.Max(10, n);

    /// <summary>Inverse iteration shifts off the eigenvalue by <c>ShiftOffsetRelative * max(scale, |mu| + 1)</c>
    /// so (A - shift I) is well conditioned, not exactly singular.</summary>
    public const double ShiftOffsetRelative = 1e-10;

    /// <summary>The shift offset is skewed into the complex plane by this ratio (imaginary/real), so a real
    /// matrix's complex-conjugate pair is never hit on the real axis. Any irrational-looking value works.</summary>
    public const double ShiftImaginarySkew = 0.37;

    /// <summary>Inverse-iteration start vector: <c>v_i = (1 + StartVectorRealStep * i) + j StartVectorImagStep * (i + 1)</c>,
    /// a deterministic, non-symmetric pattern with a component along every eigenvector in practice.</summary>
    public const double StartVectorRealStep = 0.017;

    /// <summary>See <see cref="StartVectorRealStep"/>.</summary>
    public const double StartVectorImagStep = 0.003;

    /// <summary>Inverse iteration stops early when successive normalised iterates agree to this (|1 - |v^H w||).
    /// At machine precision this is rarely reached, so the sweep usually runs the full iteration count.</summary>
    public const double InverseIterationStopTolerance = 1e-14;

    /// <summary>One eigenpair: the eigenvalue s = sigma + j*omega, the right eigenvector phi
    /// (<c>A phi = s phi</c>) and the left eigenvector psi (<c>psi^H A = s psi^H</c>), scaled so the pair is
    /// biorthonormal (<c>psi^H phi = 1</c>) - the normalisation participation factors require.</summary>
    public readonly record struct Eigenpair(Complex Value, Complex[] Right, Complex[] Left);

    /// <summary>Thrown when the right-eigenvector matrix is (numerically) singular, so biorthonormal
    /// left/right vectors do not exist - the matrix is defective / near-defective. Fail loud, never
    /// fabricate participation.</summary>
    public sealed class DefectiveEigenproblemException(string message) : Exception(message);

    /// <summary>Eigenvalues of <paramref name="a"/> (n x n), by balancing + Householder Hessenberg + Francis
    /// double-shift QR. The input is not modified. Order is the Schur order the QR sweep produces.</summary>
    public static Complex[] Eigenvalues(double[,] a)
    {
        int n = a.GetLength(0);
        if (a.GetLength(1) != n) throw new ArgumentException("matrix must be square.", nameof(a));
        if (n == 0) return Array.Empty<Complex>();
        RequireFinite(a, n);

        var h = (double[,])a.Clone();
        Balance(h, n);
        HessenbergByHouseholder(h, n);
        (double[] wr, double[] wi) = Hqr(h, n);
        var e = new Complex[n];
        for (int i = 0; i < n; i++) e[i] = new Complex(wr[i], wi[i]);
        return e;
    }

    /// <summary>Full decomposition: every eigenpair with biorthonormal left/right vectors, sorted by
    /// (Re, Im). Right vectors come from inverse iteration; the left vectors / participation come from the
    /// INVERSE of the right-eigenvector matrix V (rows of V^-1 are biorthonormal to the columns of V by
    /// construction), computed with a condition-guarded LU that FAILS LOUD - throwing
    /// <see cref="DefectiveEigenproblemException"/> - when V is singular (a defective / near-defective A),
    /// rather than fabricating a participation vector.</summary>
    public static IReadOnlyList<Eigenpair> Decompose(double[,] a, int inverseIterations = DefaultInverseIterations,
        double biorthoTol = DefaultBiorthonormalityTolerance)
        => DecomposeWith(a, inverseIterations, biorthoTol, maxDegreeOfParallelism: 0);

    /// <summary>Order at or above which <see cref="Decompose"/> runs the plain inverse
    /// iterations in parallel (N186). Below it the per-eigenvalue work is too small to pay for the threads. The result
    /// does not depend on it (the tests run both sides).</summary>
    internal const int ParallelInverseIterationMinOrder = 32;

    /// <summary>
    /// <see cref="Decompose"/> with an explicit cap on the worker count for the plain inverse
    /// iterations (N186 test seam): 0 = automatic (see <see cref="InverseIterationWorkers"/>), 1 = one at a time on
    /// the calling thread (the pre-N186 loop), above 1 = at most that many at once. Every value gives the same bits.
    /// </summary>
    // GR-458: a NaN or infinite entry has no eigenvalues; it used to spin the Francis QR to its iteration cap (30 sweeps
    // per eigenvalue) before failing with InvalidOperationException. Refused up front with the same exception type (so
    // every caller's handling is unchanged), naming the entry. Read-only, so a finite input is unchanged.
    private static void RequireFinite(double[,] a, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!double.IsFinite(a[i, j]))
                    throw new InvalidOperationException($"Eigenproblem matrix entry [{i},{j}] is {a[i, j]}; every entry must be finite (the Francis QR cannot converge on it).");
    }

    internal static IReadOnlyList<Eigenpair> DecomposeWith(double[,] a, int inverseIterations, double biorthoTol,
        int maxDegreeOfParallelism)
    {
        int n = a.GetLength(0);
        if (a.GetLength(1) != n) throw new ArgumentException("matrix must be square.", nameof(a));
        if (n == 0) return Array.Empty<Eigenpair>();
        RequireFinite(a, n);

        Complex[] values = Eigenvalues(a);
        FrozenIntroSort.Sort(values, (x, y) =>   // frozen .NET 10 introsort: the tie order cannot move with the runtime
        {
            int c = x.Real.CompareTo(y.Real);
            return c != 0 ? c : x.Imaginary.CompareTo(y.Imaginary);
        });

        double scale = MatrixScale(a, n);
        // Right eigenvectors as the columns of V.
        //
        // N186 (October 2026): one plain inverse iteration per eigenvalue, each an O(n^3) factorisation of
        // (A - shift I), was the whole cost of participation on large systems (gb-full, order 1,902: about 1,902 x 3.6
        // to 7.2 s). The iterations are independent of one another: iteration k reads only A, n, values[k], the
        // iteration count and the scale (all fixed before the loop) and writes only cols[k]. Each one is the same
        // deterministic sequence of operations wherever and whenever it runs, so running several at once on worker
        // threads gives exactly the bits the one-at-a-time loop gave (pinned by N186ParticipationBitIdentityTests,
        // which run both). The repeated-eigenvalue pass below reads earlier columns, so it stays sequential.
        var cols = new Complex[n][];
        int workers = n >= ParallelInverseIterationMinOrder ? InverseIterationWorkers(n, maxDegreeOfParallelism) : 1;
        if (workers <= 1)
        {
            for (int k = 0; k < n; k++)
                cols[k] = InverseIteration(a, n, values[k], inverseIterations, scale, deflateAgainst: null);
        }
        else
        {
            // A failure in iteration k is kept and the one with the LOWEST k is rethrown after the loop, unwrapped, so
            // a failing decomposition throws the same exception the sequential loop would have thrown first (never an
            // AggregateException, never a different eigenvalue's failure depending on thread timing).
            var failures = new Exception?[n];
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
            {
                try { cols[k] = InverseIteration(a, n, values[k], inverseIterations, scale, deflateAgainst: null); }
                catch (Exception ex) { failures[k] = ex; }
            });
            foreach (Exception? ex in failures)
                if (ex is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        }

        // Repeated but NON-defective eigenvalues (R033), e.g. identical co-located machines: plain inverse
        // iteration acts as one scalar on the whole eigenspace, so every copy of the eigenvalue converges to the
        // SAME vector (the projection of the fixed start vector), V gets equal columns and the matrix would be
        // wrongly declared defective. For each eigenvalue whose plain vector lies (numerically) in the span of
        // the vectors already found for copies of the same eigenvalue, recompute it with deflation against those
        // vectors, so the cluster returns a full basis of the eigenspace. Eigenvalues with no such twin keep
        // their plain vector untouched, so every non-repeated result is bit-identical to before.
        var v = new Complex[n, n];
        for (int k = 0; k < n; k++)
        {
            List<Complex[]> mates = ClusterMates(values, cols, k, scale);
            if (mates.Count > 0)
            {
                List<Complex[]> basis = OrthonormalBasis(mates, n);
                if (OutsideSpanSquared(cols[k], basis, n) <= 2.0 * NumericTolerances.EigenParallelTolerance)
                {
                    cols[k] = InverseIteration(a, n, values[k], inverseIterations, scale, deflateAgainst: basis);
                    // A defective (Jordan) cluster has no second eigenvector: the deflated vector then fails the
                    // eigen-equation by about the Jordan coupling. Fail loud, never fabricate a basis.
                    double residual = EigenResidual(a, n, values[k], cols[k]);
                    if (!(residual <= NumericTolerances.EigenResidualRelativeTolerance * scale))   // NaN fails too
                        throw new DefectiveEigenproblemException(
                            $"eigenvalue {values[k]} is repeated but its eigenspace has too few independent directions: the " +
                            $"deflated eigenvector leaves a residual |A v - lambda v| = {residual:E3}, above the tolerance " +
                            $"{NumericTolerances.EigenResidualRelativeTolerance * scale:E3}. A is defective, so biorthonormal " +
                            "participation is undefined.");
                }
            }
            for (int i = 0; i < n; i++) v[i, k] = cols[k][i];
        }

        // Every eigenvalue and right eigenvector must be finite before V is inverted: an overflowing solve normalised
        // to NaN used to pass every later guard (each written "x > bound", false for NaN) and come back as success.
        for (int k = 0; k < n; k++)
        {
            if (!double.IsFinite(values[k].Real) || !double.IsFinite(values[k].Imaginary))
                throw new DefectiveEigenproblemException($"eigenvalue {k} is not finite ({values[k]}); no eigen-decomposition.");
            for (int i = 0; i < n; i++)
                if (!double.IsFinite(v[i, k].Real) || !double.IsFinite(v[i, k].Imaginary))
                    throw new DefectiveEigenproblemException($"the eigenvector of eigenvalue {values[k]} is not finite; no eigen-decomposition.");
        }

        // W = V^-1 (rows are the left eigenvectors, biorthonormal to V's columns). Singular V => defective.
        Complex[,]? w = InvertComplex(v, n);
        if (w is null)
            throw new DefectiveEigenproblemException(
                "the right-eigenvector matrix V is singular: A is defective / near-defective, so biorthonormal " +
                "participation is undefined. Deflate the null space (e.g. the COI rigid-body mode) and re-analyse " +
                "the electromechanical subspace.");

        var pairs = new Eigenpair[n];
        for (int k = 0; k < n; k++)
        {
            var right = new Complex[n];
            var left = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                right[i] = v[i, k];
                left[i] = Complex.Conjugate(w[k, i]);   // psi^H phi_j = sum W[k,i] V[i,j] = delta_kj
            }
            // Biorthonormality is exact by construction (W V = I) up to rounding; anything further from 1 than the
            // tolerance means V^-1 was formed from a near-singular V (a slipped pivot): fail loud (R090).
            Complex c = Complex.Zero;
            for (int i = 0; i < n; i++) c += Complex.Conjugate(left[i]) * right[i];
            double biorthoBound = Math.Max(biorthoTol, BiorthonormalitySlack);
            if (!((c - Complex.One).Magnitude <= biorthoBound))   // written so a NaN fails
                throw new DefectiveEigenproblemException(
                    $"eigenvalue {values[k]} failed biorthonormality: |psi^H phi - 1| = {(c - Complex.One).Magnitude:E3} " +
                    $"exceeds {biorthoBound:E3} (|psi^H phi| = {c.Magnitude:E3}).");
            pairs[k] = new Eigenpair(values[k], right, left);
        }
        return pairs;
    }

    /// <summary>
    /// N186: how many plain inverse iterations run at once. Each worker holds two dense complex n x n matrices while it
    /// factors (the shifted matrix and the LU's working copy, 32 n^2 bytes), so the count is the smallest of: the
    /// requested cap (0 = no cap), the processor count, n, and what fits in the memory budget (at least one). The
    /// budget is a quarter of the memory the runtime may use, or half of what the system had free at the last
    /// garbage collection when that is known and smaller (an 8 GB machine running other work should not page). Only
    /// the speed depends on it, never a result.
    /// </summary>
    internal static int InverseIterationWorkers(int n, int maxDegreeOfParallelism)
    {
        int workers = Math.Min(Environment.ProcessorCount, n);
        if (maxDegreeOfParallelism > 0) workers = Math.Min(workers, maxDegreeOfParallelism);
        double perWorkerBytes = 32.0 * n * (double)n;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        double budget = info.TotalAvailableMemoryBytes / 4.0;
        if (info.MemoryLoadBytes > 0 && info.TotalAvailableMemoryBytes > info.MemoryLoadBytes)
            budget = Math.Min(budget, (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 2.0);
        if (budget > 0)
            workers = (int)Math.Min(workers, Math.Max(1.0, Math.Floor(budget / perWorkerBytes)));
        return Math.Max(1, workers);
    }

    // The vectors already computed for EARLIER copies of eigenvalue k: indices j < k whose eigenvalue agrees
    // with values[k] to the shared relative cluster tolerance. All earlier indices are scanned (not only the
    // neighbour) because the (Re, Im) sort can interleave rounding-level copies of a complex pair.
    private static List<Complex[]> ClusterMates(Complex[] values, Complex[][] cols, int k, double scale)
    {
        var mates = new List<Complex[]>();
        double tol = NumericTolerances.EigenClusterRelativeTolerance * Math.Max(scale, values[k].Magnitude);
        for (int j = 0; j < k; j++)
            if ((values[j] - values[k]).Magnitude <= tol) mates.Add(cols[j]);
        return mates;
    }

    // Orthonormal basis of span(vectors) by modified Gram-Schmidt with the Hermitian inner product. A vector
    // that adds no new direction (already in the span) is dropped.
    private static List<Complex[]> OrthonormalBasis(List<Complex[]> vectors, int n)
    {
        var basis = new List<Complex[]>(vectors.Count);
        foreach (Complex[] src in vectors)
        {
            var q = (Complex[])src.Clone();
            Project(q, basis, n);
            double norm = Norm2(q, n);
            if (norm <= NumericTolerances.EigenParallelTolerance) continue;
            for (int i = 0; i < n; i++) q[i] /= norm;
            basis.Add(q);
        }
        return basis;
    }

    // q -= sum_b b (b^H q) over an orthonormal basis (modified Gram-Schmidt, one pass).
    private static void Project(Complex[] q, List<Complex[]> basis, int n)
    {
        foreach (Complex[] b in basis)
        {
            Complex dot = Complex.Zero;
            for (int i = 0; i < n; i++) dot += Complex.Conjugate(b[i]) * q[i];
            for (int i = 0; i < n; i++) q[i] -= dot * b[i];
        }
    }

    private static double Norm2(Complex[] q, int n)
    {
        double s = 0.0;
        for (int i = 0; i < n; i++) s += q[i].Real * q[i].Real + q[i].Imaginary * q[i].Imaginary;
        return Math.Sqrt(s);
    }

    // Squared norm of the part of the unit vector v outside span(basis): 1 - ||Q^H v||^2. For a single basis
    // vector u this is 1 - |u^H v|^2, so "<= 2 * tol" is (to first order) "|u^H v| >= 1 - tol".
    private static double OutsideSpanSquared(Complex[] v, List<Complex[]> basis, int n)
    {
        var r = (Complex[])v.Clone();
        Project(r, basis, n);
        double norm = Norm2(r, n);
        return norm * norm;
    }

    // ||A v - lambda v||_2 for a unit vector v (the eigen-equation residual).
    private static double EigenResidual(double[,] a, int n, Complex lambda, Complex[] v)
    {
        double s = 0.0;
        for (int i = 0; i < n; i++)
        {
            Complex r = -lambda * v[i];
            for (int j = 0; j < n; j++) r += a[i, j] * v[j];
            s += r.Real * r.Real + r.Imaginary * r.Imaginary;
        }
        return Math.Sqrt(s);
    }

    // Complex dense inverse by Gauss-Jordan with partial pivoting; returns null on a (near-)singular pivot
    // (the shared scale-invariant NumericTolerances.RelativePivotFloor, which replaced a local 1e-13) so the
    // caller can fail loud instead of fabricating.
    private static Complex[,]? InvertComplex(Complex[,] src, int n)
        => InvertComplexWith(src, n, n >= ParallelInverseMinOrder ? InverseIterationWorkers(n, 0) : 1);

    /// <summary>N197 (CF5): the order at or above which the eigenvector-matrix inverse eliminates its rows on several
    /// threads. Below it a column's row updates are too small to pay for the threads. The result never depends on it.</summary>
    internal const int ParallelInverseMinOrder = 256;

    /// <summary>
    /// N197 (CF5): <see cref="InvertComplex"/> with an explicit worker count (the test seam: 1 is the sequential loop).
    /// Gauss-Jordan with partial pivoting; for each column the pivot search, the row swap and the pivot-row division are
    /// serial, and then every OTHER row's update (row r -= f_r times the pivot row, in column order) is independent of
    /// every other row's: it reads only the pivot row (fixed during the phase) and its own f_r, and writes only row r.
    /// Each row therefore runs exactly the same operations in the same order on any thread, so every worker count gives
    /// the same bits (pinned by Cf5EigenInverseParallelTests). gb-full (order 1,902) spent minutes here on one thread.
    /// </summary>
    internal static Complex[,]? InvertComplexWith(Complex[,] src, int n, int workers)
    {
        var a = (Complex[,])src.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = Complex.One;

        double aNorm = 0.0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { double m = a[i, j].Magnitude; if (m > aNorm) aNorm = m; }
        double floor = NumericTolerances.PivotFloor(aNorm <= 0 ? 1.0 : aNorm);

        for (int col = 0; col < n; col++)
        {
            int piv = col; double best = a[col, col].Magnitude;
            for (int r = col + 1; r < n; r++) { double m = a[r, col].Magnitude; if (m > best) { best = m; piv = r; } }
            if (!(best > floor)) return null;   // a NaN pivot is no pivot
            if (piv != col)
                for (int j = 0; j < n; j++)
                {
                    (a[col, j], a[piv, j]) = (a[piv, j], a[col, j]);
                    (inv[col, j], inv[piv, j]) = (inv[piv, j], inv[col, j]);
                }
            Complex d = a[col, col];
            for (int j = 0; j < n; j++) { a[col, j] /= d; inv[col, j] /= d; }
            if (workers <= 1)
            {
                for (int r = 0; r < n; r++) EliminateRow(a, inv, n, col, r);
            }
            else
            {
                int c = col;   // the lambda captures the column of this phase only
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers }, r => EliminateRow(a, inv, n, c, r));
            }
        }
        return inv;
    }

    // One row's Gauss-Jordan update against pivot row col (N197: shared by the sequential and the parallel paths, so both
    // run the identical operations). The pivot row itself is skipped.
    private static void EliminateRow(Complex[,] a, Complex[,] inv, int n, int col, int r)
    {
        if (r == col) return;
        Complex f = a[r, col];
        if (f == Complex.Zero) return;
        for (int j = 0; j < n; j++) { a[r, j] -= f * a[col, j]; inv[r, j] -= f * inv[col, j]; }
    }

    // --- Parlett-Reinsch balancing (radix-2 diagonal similarity; eigenvalue-preserving). ---
    private static void Balance(double[,] a, int n)
    {
        const double radix = 2.0;
        double sqrdx = radix * radix;
        bool done = false;
        while (!done)
        {
            done = true;
            for (int i = 0; i < n; i++)
            {
                double r = 0.0, c = 0.0;
                for (int j = 0; j < n; j++)
                    if (j != i) { c += Math.Abs(a[j, i]); r += Math.Abs(a[i, j]); }
                if (c == 0.0 || r == 0.0) continue;
                // A non-finite column/row sum (e.g. an Inf off-diagonal from a 1/M term with M=0)
                // makes the `while (c > g)` scaling loop spin forever (Inf > finite is always true,
                // Inf/radix stays Inf). Skip balancing this row/column; Hqr then fails cleanly
                // instead of hanging the calling thread.
                if (!double.IsFinite(c) || !double.IsFinite(r)) continue;

                double g = r / radix;
                double f = 1.0;
                double s = c + r;
                while (c < g) { f *= radix; c *= sqrdx; }
                g = r * radix;
                while (c > g) { f /= radix; c /= sqrdx; }
                if ((c + r) / f < 0.95 * s)
                {
                    done = false;
                    g = 1.0 / f;
                    for (int j = 0; j < n; j++) a[i, j] *= g;
                    for (int j = 0; j < n; j++) a[j, i] *= f;
                }
            }
        }
    }

    // --- Upper-Hessenberg reduction by our OWN Householder reflectors (H = P^T A P, similarity). ---
    private static void HessenbergByHouseholder(double[,] a, int n)
    {
        var v = new double[n];
        for (int col = 0; col < n - 2; col++)
        {
            // Reflector over rows col+1..n-1 of column col, zeroing everything below the sub-diagonal.
            double norm = 0.0;
            for (int i = col + 1; i < n; i++) norm += a[i, col] * a[i, col];
            norm = Math.Sqrt(norm);
            if (norm == 0.0) continue;

            double alpha = a[col + 1, col] > 0 ? -norm : norm;
            for (int i = col + 1; i < n; i++) v[i] = a[i, col];
            v[col + 1] -= alpha;
            double vtv = 0.0;
            for (int i = col + 1; i < n; i++) vtv += v[i] * v[i];
            if (vtv <= 0.0) continue;

            // Left: A <- (I - 2 v v^T / v^T v) A   over rows col+1..n-1 (all columns).
            for (int j = 0; j < n; j++)
            {
                double sdot = 0.0;
                for (int i = col + 1; i < n; i++) sdot += v[i] * a[i, j];
                sdot = 2.0 * sdot / vtv;
                for (int i = col + 1; i < n; i++) a[i, j] -= sdot * v[i];
            }
            // Right: A <- A (I - 2 v v^T / v^T v)   over columns col+1..n-1 (all rows) - keeps the similarity.
            for (int i = 0; i < n; i++)
            {
                double sdot = 0.0;
                for (int j = col + 1; j < n; j++) sdot += a[i, j] * v[j];
                sdot = 2.0 * sdot / vtv;
                for (int j = col + 1; j < n; j++) a[i, j] -= sdot * v[j];
            }
        }
    }

    // --- Francis double-shift implicit-QR eigenvalues of a real upper-Hessenberg matrix (EISPACK hqr). ---
    private static (double[] Wr, double[] Wi) Hqr(double[,] a, int n)
    {
        var wr = new double[n];
        var wi = new double[n];

        double anorm = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = Math.Max(i - 1, 0); j < n; j++)
                anorm += Math.Abs(a[i, j]);

        int nn = n - 1;
        double t = 0.0;
        while (nn >= 0)
        {
            int its = 0;
            int l;
            do
            {
                for (l = nn; l >= 1; l--)
                {
                    double s0 = Math.Abs(a[l - 1, l - 1]) + Math.Abs(a[l, l]);
                    if (s0 == 0.0) s0 = anorm;
                    if (Math.Abs(a[l, l - 1]) + s0 == s0) { a[l, l - 1] = 0.0; break; }
                }
                double x = a[nn, nn];
                if (l == nn)
                {
                    wr[nn] = x + t; wi[nn] = 0.0; nn--;
                }
                else
                {
                    double y = a[nn - 1, nn - 1];
                    double w = a[nn, nn - 1] * a[nn - 1, nn];
                    if (l == nn - 1)
                    {
                        double p0 = 0.5 * (y - x);
                        double q0 = p0 * p0 + w;
                        double z0 = Math.Sqrt(Math.Abs(q0));
                        x += t;
                        if (q0 >= 0.0)
                        {
                            z0 = p0 + CopySign(z0, p0);
                            wr[nn - 1] = wr[nn] = x + z0;
                            if (z0 != 0.0) wr[nn] = x - w / z0;
                            wi[nn - 1] = wi[nn] = 0.0;
                        }
                        else
                        {
                            wr[nn - 1] = wr[nn] = x + p0;
                            wi[nn] = z0; wi[nn - 1] = -z0;
                        }
                        nn -= 2;
                    }
                    else
                    {
                        if (its >= QrHardCapIterations(n))
                            throw new InvalidOperationException("Francis QR failed to converge (too many iterations).");
                        // The original schedule (10, 20, 30, 40) and, past the original cap, every 10th iteration.
                        if (its == 10 || its == 20 || its == 30 || its == 40 || (its >= QrMaxIterationsPerEigenvalue && its % 10 == 0))
                        {
                            // EISPACK hqr exceptional shift (ad hoc, to break a stalled cycle): standard constants.
                            t += x;
                            for (int i = 0; i <= nn; i++) a[i, i] -= x;
                            double s1 = Math.Abs(a[nn, nn - 1]) + Math.Abs(a[nn - 1, nn - 2]);
                            y = x = 0.75 * s1;
                            w = -0.4375 * s1 * s1;
                        }
                        its++;

                        double p = 0, q = 0, r = 0;
                        int m;
                        for (m = nn - 2; m >= l; m--)
                        {
                            double z1 = a[m, m];
                            double rr = x - z1;
                            double ss = y - z1;
                            p = (rr * ss - w) / a[m + 1, m] + a[m, m + 1];
                            q = a[m + 1, m + 1] - z1 - rr - ss;
                            r = a[m + 2, m + 1];
                            double s2 = Math.Abs(p) + Math.Abs(q) + Math.Abs(r);
                            p /= s2; q /= s2; r /= s2;
                            if (m == l) break;
                            double u0 = Math.Abs(a[m, m - 1]) * (Math.Abs(q) + Math.Abs(r));
                            double v0 = Math.Abs(p) * (Math.Abs(a[m - 1, m - 1]) + Math.Abs(z1) + Math.Abs(a[m + 1, m + 1]));
                            if (u0 + v0 == v0) break;
                        }
                        for (int i = m + 2; i <= nn; i++)
                        {
                            a[i, i - 2] = 0.0;
                            if (i != m + 2) a[i, i - 3] = 0.0;
                        }
                        for (int k = m; k <= nn - 1; k++)
                        {
                            if (k != m)
                            {
                                p = a[k, k - 1];
                                q = a[k + 1, k - 1];
                                r = k != nn - 1 ? a[k + 2, k - 1] : 0.0;
                                x = Math.Abs(p) + Math.Abs(q) + Math.Abs(r);
                                if (x != 0.0) { p /= x; q /= x; r /= x; }
                            }
                            double s3 = CopySign(Math.Sqrt(p * p + q * q + r * r), p);
                            if (s3 == 0.0) continue;
                            if (k == m)
                            {
                                if (l != m) a[k, k - 1] = -a[k, k - 1];
                            }
                            else
                            {
                                a[k, k - 1] = -s3 * x;
                            }
                            p += s3;
                            double px = p / s3, qx = q / s3, rx = r / s3;
                            q /= p; r /= p;
                            for (int j = k; j <= nn; j++)
                            {
                                double pp = a[k, j] + q * a[k + 1, j];
                                if (k != nn - 1) { pp += r * a[k + 2, j]; a[k + 2, j] -= pp * rx; }
                                a[k + 1, j] -= pp * qx;
                                a[k, j] -= pp * px;
                            }
                            int mmin = nn < k + 3 ? nn : k + 3;
                            for (int i = l; i <= mmin; i++)
                            {
                                double pp = px * a[i, k] + qx * a[i, k + 1];
                                if (k != nn - 1) { pp += rx * a[i, k + 2]; a[i, k + 2] -= pp * r; }
                                a[i, k + 1] -= pp * q;
                                a[i, k] -= pp;
                            }
                        }
                    }
                }
            } while (l < nn - 1);
        }
        return (wr, wi);
    }

    // Inverse iteration on (A - mu I) for the right vector (R090: the unused A^T / left-vector branch is removed;
    // left vectors come from V^-1). Uses the shared dense complex LU (its near-singular pivot clamp is exactly the
    // regularisation inverse iteration relies on when mu sits on an eigenvalue); the honest guard is the
    // biorthonormality assertion in Decompose, not this solve.
    // deflateAgainst (R033): null for the plain iteration (unchanged arithmetic). Otherwise an orthonormal basis
    // of eigenvectors already found for copies of the same repeated eigenvalue; the start vector and every
    // iterate are projected off that basis, so the iteration converges to an eigenvector of the eigenspace
    // that is independent of them. (A - shift I)^-1 scales the whole eigenspace by one factor, so removing the
    // components along vectors that lie IN the eigenspace keeps the iterate in the eigenspace.
    private static Complex[] InverseIteration(double[,] a, int n, Complex mu, int iterations, double scale,
        List<Complex[]>? deflateAgainst)
    {
        // Regularise the shift a hair off the eigenvalue so (A - shift I) is well-conditioned (not exactly
        // singular): standard inverse iteration. The perturbation is tiny (~1e-10 of the matrix scale) so the
        // recovered eigenvector's residual against the TRUE eigenvalue stays at that level, well below the
        // biorthonormality / eigen-equation tolerances.
        Complex baseShift = mu;
        double eps = ShiftOffsetRelative * Math.Max(scale, baseShift.Magnitude + 1.0);
        Complex shift = baseShift + new Complex(eps, ShiftImaginarySkew * eps);

        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = new Complex(a[i, j], 0.0);
        for (int i = 0; i < n; i++) m[i, i] -= shift;

        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = new Complex(1.0 + StartVectorRealStep * i, StartVectorImagStep * (i + 1));
        if (deflateAgainst is not null) v = DeflatedStart(v, deflateAgainst, n);
        Normalize(v, n);

        // R124: (A - shift I) is the same on every iteration, so factor it ONCE and back-substitute each step
        // (O(n^2) per iteration instead of an O(n^3) refactorisation). DenseComplexLu.Solve replays the recorded
        // row swaps and multipliers in the original elimination order and is bit-identical to SolveDense on the
        // same matrix and right-hand side (pinned by its own tests), so every eigenvector is unchanged.
        // N019: the shift sits next to an eigenvalue, so (A - shift I) is near-singular by design; FactorDense's pivot
        // clamp (lu.ClampedPivots > 0 when it trips) is the classic perturbed-pivot treatment of inverse iteration and
        // still yields the eigenvector direction. Deliberately NOT refused here, unlike a caller that needs a solution.
        DenseComplexLu lu = DenseComplexLuSolver.FactorDense(m, n);
        Complex[] w = v;
        for (int it = 0; it < iterations; it++)
        {
            w = lu.Solve(v);
            if (deflateAgainst is not null) Project(w, deflateAgainst, n);
            Normalize(w, n);
            Complex dot = Complex.Zero;
            for (int i = 0; i < n; i++) dot += Complex.Conjugate(v[i]) * w[i];
            v = w;
            if (Math.Abs(1.0 - dot.Magnitude) < InverseIterationStopTolerance) break;
        }
        return w;
    }

    // The deterministic start vector projected off the deflation basis. If it lies (numerically) in that span,
    // fall back to the unit vectors e_0, e_1, ... in turn (the first with a solid component outside the span
    // wins), so the deflated iteration always starts with a usable direction. n basis vectors cannot span all
    // n unit vectors plus the start unless the basis already covers C^n, which a strict cluster subset never does.
    private static Complex[] DeflatedStart(Complex[] start, List<Complex[]> basis, int n)
    {
        var v = (Complex[])start.Clone();
        double startNorm = Norm2(v, n);
        Project(v, basis, n);
        if (Norm2(v, n) > 0.1 * startNorm) return v;
        for (int e = 0; e < n; e++)
        {
            var u = new Complex[n];
            u[e] = Complex.One;
            Project(u, basis, n);
            if (Norm2(u, n) > 0.1) return u;
        }
        throw new DefectiveEigenproblemException(
            "inverse-iteration deflation found no start direction outside the eigenvectors already computed; " +
            "the eigenvalue cluster is larger than the space it lives in.");
    }

    private static void Normalize(Complex[] v, int n)
    {
        // Unit 2-norm with a canonical phase: rotate so the largest-magnitude entry is real-positive.
        double norm = 0.0;
        int imax = 0; double amax = -1.0;
        for (int i = 0; i < n; i++)
        {
            double a = v[i].Real * v[i].Real + v[i].Imaginary * v[i].Imaginary;
            norm += a;
            if (a > amax) { amax = a; imax = i; }
        }
        norm = Math.Sqrt(norm);
        if (norm == 0.0) { v[0] = Complex.One; return; }
        Complex phase = v[imax].Magnitude > 0 ? Complex.Conjugate(v[imax]) / v[imax].Magnitude : Complex.One;
        double inv = 1.0 / norm;
        for (int i = 0; i < n; i++) v[i] = v[i] * phase * inv;
    }

    private static double MatrixScale(double[,] a, int n)
    {
        double s = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { double v = Math.Abs(a[i, j]); if (v > s) s = v; }
        return s <= 0 ? 1.0 : s;
    }

    private static double CopySign(double magnitude, double sign)
        => sign >= 0.0 ? Math.Abs(magnitude) : -Math.Abs(magnitude);
}
