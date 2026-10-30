using System.Numerics;
using GridSim.Core.Abstractions;

namespace GridSim.Core.Numerics;

/// <summary>
/// Dense complex LU with partial pivoting (Gaussian elimination on a <c>Complex[,]</c>). Exact in one pass,
/// O(n^3) - the small-case path and the cross-check <b>oracle</b> the scalable solvers are validated against.
/// It was lifted out of the fault study; the fault study now uses the factor-based path (FaultNetworkSystem), and
/// <see cref="SolveDense"/> has no production caller: it is kept as the test oracle (GR-452).
/// </summary>
public sealed class DenseComplexLuSolver : IComplexLinearSolver
{
    public string Name => "complex-dense-lu";

    public Complex[]? Solve(ComplexCsr a, Complex[] b)
    {
        int n = a.N;
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                m[i, a.ColIdx[k]] = new Complex(a.G[k], a.B[k]);
        // The cross-validation seam contract is: singular => null (the two sparse solvers honour it).
        // A near-singular system must not "validate" against a clamped, fabricated answer, so the
        // interface path reports failure on a scale-invariant floor instead of clamping.
        return SolveDenseCore(m, b, n, clampSingular: false);
    }

    /// <summary>Dense complex solve <c>A x = b</c> by Gaussian elimination with partial pivoting. A/b are
    /// copied. Retains the historical near-singular <b>clamping</b> behaviour (never returns null) so the
    /// fault study - which lifted this solver - stays bit-compatible; the cross-validation seam (<see
    /// cref="Solve"/>) uses the honest null-on-singular path instead.
    /// <para>The clamp threshold is the shared relative floor
    /// (<see cref="NumericTolerances.RelativePivotFloor"/> times max|A_ij|), not the old absolute 1e-14
    /// (R144/R181), so it trips on a pivot that is singular to working precision whatever the matrix scale,
    /// and never on a legitimate small pivot of a small-scale matrix. On a non-singular system no pivot is
    /// near the floor and the result is bit-identical to before.</para></summary>
    public static Complex[] SolveDense(Complex[,] aIn, Complex[] bIn, int n)
        => SolveDenseCore(aIn, bIn, n, clampSingular: true)!;

    /// <summary>Dense complex solve on the HONEST path: the same elimination as <see cref="SolveDense"/> (so a
    /// non-singular system gives the same bits), but a pivot at or below the shared relative floor returns null
    /// instead of being clamped (N012). For callers that must fail loud on a singular system.</summary>
    public static Complex[]? SolveDenseOrNull(Complex[,] aIn, Complex[] bIn, int n)
        => SolveDenseCore(aIn, bIn, n, clampSingular: false);

    /// <summary>Factor <c>A</c> once (same pivoting and near-singular clamping as <see cref="SolveDense"/>) and
    /// return a handle that solves any number of right-hand sides at O(n^2) each. Each
    /// <see cref="DenseComplexLu.Solve"/> is bit-identical to <see cref="SolveDense"/> on the same A and b: the
    /// forward pass replays the recorded row swaps and multipliers in the original elimination order.
    /// <para>N186 (October 2026): the elimination is now done column by column ("left-looking") on a column-major
    /// copy, a block of <see cref="FactorBlockColumns"/> columns at a time, with the inner update vectorised
    /// (<see cref="SimdKernels.SubtractScaledComplexColumn(Complex[],int,int,int,Complex)"/>).
    /// The factor is bit-for-bit the one the previous row-by-row ("right-looking") loop produced, because every
    /// entry receives exactly the same operations in exactly the same order:
    /// <list type="bullet">
    /// <item>The old loop, at step k, swapped rows k and p_k in columns k..n-1, then for every row r &gt; k with a
    /// non-zero multiplier l(r,k) did <c>a(r,j) -= l(r,k) * a(k,j)</c> for every column j &gt; k. So column j saw, for
    /// k = 0, 1, ..., j-1 in that order: the swap of step k, then the update of step k; and at step j itself the pivot
    /// search over rows j..n-1, its swap and the division that makes the multipliers.</item>
    /// <item>The new loop gives each column exactly that sequence: for k = 0..j-1 in increasing order, the swap of step
    /// k, then the update of step k (the same multipliers, the same zero skip, the same <c>a -= l * u</c> per entry),
    /// then the pivot search, swap and division of step j. Columns are independent of one another within a step, so
    /// doing a block of columns together (for cache reuse of each multiplier column) changes nothing.</item>
    /// <item>Multipliers stay in the slot of the row they were applied to (later swaps never move them), as before,
    /// so <see cref="DenseComplexLu.Solve"/> replays them unchanged. The finished column-major array is transposed in
    /// place into the row-major layout the solve reads.</item>
    /// <item>The clamp floor comes from the largest |a_ij| of the same entries (a maximum does not depend on the order
    /// it is taken in; a NaN still fails loud), and a clamped pivot is clamped exactly as before.</item>
    /// </list>
    /// Pinned by N186DenseComplexFactorBitIdentityTests against the previous loop kept verbatim, at every vector tier
    /// and several block widths. It is also faster on large matrices: each multiplier column is reused from cache
    /// across a block of columns, so the whole matrix is no longer streamed through memory once per step.</para></summary>
    public static DenseComplexLu FactorDense(Complex[,] aIn, int n) => FactorDenseAt(aIn, n, SimdCapabilities.Tier);

    /// <summary>Columns updated together in <see cref="FactorDense"/>: a tuning knob for cache reuse only
    /// (the factor does not depend on it; the tests run several values).</summary>
    internal const int FactorBlockColumns = 16;

    /// <summary><see cref="FactorDense"/> at an explicit vector tier and block width (the N186 test seam;
    /// production passes <see cref="SimdCapabilities.Tier"/> and <see cref="FactorBlockColumns"/>).</summary>
    internal static DenseComplexLu FactorDenseAt(Complex[,] aIn, int n, SimdTier tier, int blockColumns = FactorBlockColumns)
    {
        if (blockColumns < 1) throw new ArgumentOutOfRangeException(nameof(blockColumns), blockColumns, "must be at least 1.");

        // Column-major working copy: entry (i, j) at c[j * n + i], so a column (one step's multipliers, or one column
        // being brought up to date) is contiguous.
        var c = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) c[j * n + i] = aIn[i, j];
        var pivots = new int[n];
        // Same clamp threshold as SolveDense (computed from the same original matrix), so the replayed
        // solve stays bit-identical to the one-shot solve.
        double clampFloor = NumericTolerances.PivotFloor(MaxMagnitude(c));
        int clamped = 0;   // N019: how many pivots were clamped (0 on every matrix that is not singular to working precision)

        for (int j0 = 0; j0 < n; j0 += blockColumns)
        {
            int j1 = Math.Min(n, j0 + blockColumns);

            // (1) Bring the block's columns up to date with every finished step k < j0. For each step, its multiplier
            // column (rows k+1..n-1 of column k) is applied to every column of the block while it is in cache.
            // Four columns at a time share each loaded multiplier vector (the same arithmetic per entry as one at a
            // time; see SimdKernels.SubtractScaledComplexColumns4); the last one to three columns go singly.
            for (int k = 0; k < j0; k++)
            {
                int piv = pivots[k];
                int j = j0;
                for (; j + 4 <= j1; j += 4) ApplyStep4(c, n, k, piv, j, tier);
                for (; j < j1; j++) ApplyStep(c, n, k, piv, j, tier);
            }

            // (2) Finish the block's columns in order: each takes the steps of the block's earlier columns, then
            // becomes a step itself (pivot search, swap, multipliers).
            for (int j = j0; j < j1; j++)
            {
                for (int k = j0; k < j; k++) ApplyStep(c, n, k, pivots[k], j, tier);

                int cj = j * n;
                int piv = j; double best = c[cj + j].Magnitude;
                for (int r = j + 1; r < n; r++) { double mm = c[cj + r].Magnitude; if (mm > best) { best = mm; piv = r; } }
                pivots[j] = piv;
                // The old loop swapped rows j and piv in columns j..n-1. Here column j swaps now; each later column
                // swaps when it applies step j (ApplyStep); earlier columns hold multipliers and are not swapped.
                if (piv != j) (c[cj + j], c[cj + piv]) = (c[cj + piv], c[cj + j]);
                Complex d = c[cj + j];
                if (d.Magnitude <= clampFloor) { d = ClampedPivot(clampFloor); clamped++; }
                for (int r = j + 1; r < n; r++) c[cj + r] = c[cj + r] / d;   // record the multipliers in the dead slots
            }
        }

        // Column-major -> row-major in place (a square matrix transposes by swapping across the diagonal): the layout
        // DenseComplexLu.Solve reads (multipliers below the diagonal at a[r * n + k], U on and above it).
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++) (c[i * n + j], c[j * n + i]) = (c[j * n + i], c[i * n + j]);
        return new DenseComplexLu(n, c, pivots, clampFloor, clamped);
    }

    /// <summary>
    /// Apply elimination step <paramref name="k"/> (pivot row <paramref name="piv"/>) to column <paramref name="j"/>
    /// (j &gt; k) of the column-major matrix: swap rows k and piv in that column, then for every row r &gt; k whose
    /// multiplier l(r,k) is non-zero, <c>a(r,j) -= l(r,k) * a(k,j)</c>; exactly what the old loop did to column j at
    /// step k.
    /// </summary>
    private static void ApplyStep(Complex[] c, int n, int k, int piv, int j, SimdTier tier)
    {
        int cj = j * n;
        if (piv != k) (c[cj + k], c[cj + piv]) = (c[cj + piv], c[cj + k]);
        Complex u = c[cj + k];
        SimdKernels.SubtractScaledComplexColumn(c, cj + k + 1, k * n + k + 1, n - k - 1, u, tier);
    }

    /// <summary><see cref="ApplyStep"/> for the four columns j..j+3 at once (each swapped, then updated with its own
    /// a(k, column) against the one shared multiplier column of step k).</summary>
    private static void ApplyStep4(Complex[] c, int n, int k, int piv, int j, SimdTier tier)
    {
        int c0 = j * n, c1 = c0 + n, c2 = c1 + n, c3 = c2 + n;
        if (piv != k)
        {
            (c[c0 + k], c[c0 + piv]) = (c[c0 + piv], c[c0 + k]);
            (c[c1 + k], c[c1 + piv]) = (c[c1 + piv], c[c1 + k]);
            (c[c2 + k], c[c2 + piv]) = (c[c2 + piv], c[c2 + k]);
            (c[c3 + k], c[c3 + piv]) = (c[c3 + piv], c[c3 + k]);
        }
        SimdKernels.SubtractScaledComplexColumns4(c, c0 + k + 1, c1 + k + 1, c2 + k + 1, c3 + k + 1, k * n + k + 1, n - k - 1,
            c[c0 + k], c[c1 + k], c[c2 + k], c[c3 + k], tier);
    }

    /// <summary>Largest |a_ij| of a flat complex matrix (the reference scale for the relative pivot floor).
    /// A NaN or infinite entry fails loud: a corrupted admittance matrix must never be "solved".
    /// GR-478: an infinite entry used to pass (only NaN was refused); it made the scale and so the pivot floor
    /// infinite, every pivot was then clamped on the clamping path and the "solution" came back as the zero vector.
    /// |c| of a complex with an infinite part is infinite (even beside a NaN part), so the two tests below cover
    /// every non-finite entry.</summary>
    internal static double MaxMagnitude(Complex[] a)
    {
        double aNorm = 0.0;
        foreach (Complex c in a)
        {
            double av = c.Magnitude;
            if (double.IsNaN(av))
                throw new ArgumentException("Dense complex LU: the matrix contains a NaN entry, so it cannot be factorised.");
            if (double.IsInfinity(av))
                throw new ArgumentException("Dense complex LU: the matrix contains an infinite entry, so it cannot be factorised.");
            if (av > aNorm) aNorm = av;
        }
        return aNorm;
    }

    /// <summary>The stand-in for a clamped near-singular pivot on the historical (fault-study) path: the
    /// relative floor itself. On the all-zero matrix the floor is zero and every numerator is zero too, so any
    /// non-zero stand-in (1.0) keeps the elimination finite without changing a single value.</summary>
    private static Complex ClampedPivot(double clampFloor) => new(clampFloor > 0.0 ? clampFloor : 1.0, 0.0);

    private static Complex[]? SolveDenseCore(Complex[,] aIn, Complex[] bIn, int n, bool clampSingular)
    {
        // Flat row-major working copy (element (i,j) at a[i*n+j]): contiguous storage drops the 2-D
        // indexer bounds checks and gives the JIT row locality. The elimination order is unchanged, so
        // this is bit-identical to the 2-D form (same treatment DenseLuSolver already uses).
        var a = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i * n + j] = aIn[i, j];
        var b = (Complex[])bIn.Clone();

        // Scale-invariant near-singular floor (the shared NumericTolerances.RelativePivotFloor, as the real
        // DenseLuSolver uses): a pivot at or below aNorm * floor is numerically singular. The honest seam path
        // returns null there; the historical clamping path substitutes a stand-in pivot instead.
        double pivotFloor = NumericTolerances.PivotFloor(MaxMagnitude(a));

        for (int col = 0; col < n; col++)
        {
            int piv = col; double best = a[col * n + col].Magnitude;
            for (int r = col + 1; r < n; r++) { double mm = a[r * n + col].Magnitude; if (mm > best) { best = mm; piv = r; } }
            if (!clampSingular && best <= pivotFloor) return null; // singular: report, don't fabricate
            if (piv != col)
            {
                for (int j = 0; j < n; j++) (a[col * n + j], a[piv * n + j]) = (a[piv * n + j], a[col * n + j]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            Complex d = a[col * n + col];
            if (clampSingular && d.Magnitude <= pivotFloor) d = ClampedPivot(pivotFloor);
            for (int r = col + 1; r < n; r++)
            {
                Complex factor = a[r * n + col] / d;
                if (factor == Complex.Zero) continue;
                for (int j = col; j < n; j++) a[r * n + j] -= factor * a[col * n + j];
                b[r] -= factor * b[col];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            Complex s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i * n + j] * x[j];
            Complex dd = a[i * n + i];
            if (clampSingular)
                x[i] = dd.Magnitude <= pivotFloor ? Complex.Zero : s / dd;
            else
            {
                if (dd.Magnitude <= pivotFloor) return null;
                x[i] = s / dd;
            }
        }
        return x;
    }
}

/// <summary>A dense complex LU factorization from <see cref="DenseComplexLuSolver.FactorDense"/>: multipliers
/// below the diagonal, U on and above it, and the per-step pivot rows. Immutable; thread-safe to solve.</summary>
public sealed class DenseComplexLu
{
    private readonly int _n;
    private readonly Complex[] _a;
    private readonly int[] _pivots;
    private readonly double _clampFloor;   // the relative pivot floor of the factored matrix (see FactorDense)

    internal DenseComplexLu(int n, Complex[] a, int[] pivots, double clampFloor, int clampedPivots)
    { _n = n; _a = a; _pivots = pivots; _clampFloor = clampFloor; ClampedPivots = clampedPivots; }

    /// <summary>The system order.</summary>
    public int N => _n;

    /// <summary>
    /// How many pivots <see cref="DenseComplexLuSolver.FactorDense"/> found at or below the shared relative floor and
    /// clamped (N019, October 2026). Zero means the matrix is not singular to working precision and every solve is an
    /// honest one; above zero, the factor is of a (numerically) singular matrix and a solve returns a finite vector
    /// that does not satisfy <c>A x = b</c>. The clamp itself is kept on purpose: inverse iteration
    /// (<see cref="GeneralEigen"/>) factors <c>A - shift I</c> with the shift next to an eigenvalue, where a vanishing
    /// pivot is the expected case and the clamped factor still gives the eigenvector direction (the classic
    /// perturbed-pivot treatment). Every caller that needs a true solution must check this: the fault study rejects
    /// a clamped answer by its residual (R285), and the RMS converter network refuses to build on one.
    /// Reading it changes nothing: the factor and every solve are bit-for-bit what they were.
    /// </summary>
    public int ClampedPivots { get; }

    /// <summary>Solve <c>A x = b</c> (b is not modified).</summary>
    public Complex[] Solve(Complex[] bIn)
    {
        int n = _n;
        Complex[] a = _a;
        var b = (Complex[])bIn.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = _pivots[col];
            if (piv != col) (b[col], b[piv]) = (b[piv], b[col]);
            for (int r = col + 1; r < n; r++)
            {
                Complex factor = a[r * n + col];
                if (factor == Complex.Zero) continue;
                b[r] -= factor * b[col];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            Complex s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i * n + j] * x[j];
            Complex dd = a[i * n + i];
            x[i] = dd.Magnitude <= _clampFloor ? Complex.Zero : s / dd;
        }
        return x;
    }
}

/// <summary>
/// Solves the complex system by the real 2n-expansion: writing A = G + jB, x = xr + j xi, b = br + j bi,
/// the complex equation <c>(G+jB)(xr+jxi) = br+jbi</c> is the real system
/// <c>[G -B; B G][xr; xi] = [br; bi]</c>. That 2n x 2n real system is assembled into a <see cref="CsrMatrix"/>
/// and solved by the existing tuned real sparse-direct <see cref="SparseLu"/> - so this reuses the whole real
/// linear-algebra stack (fill-reducing ordering, Gilbert-Peierls LU) and <b>scales to gb-full</b> (~3,500
/// buses), unlike the dense O(n^3) path. It is the scalable default for harmonic penetration.
/// </summary>
public sealed class RealExpandedComplexSolver : IComplexLinearSolver
{
    // Cached fill-reducing ordering of the 2n system, reused across calls whose 2n pattern matches by
    // content. A harmonic scan builds Y(h) for many orders with the SAME topology, so the real-expanded
    // pattern is identical across orders and only the values change; the RCM ordering is then computed once
    // and reused for every order's factorization (bit-identical - RCM is a pure function of the pattern -
    // exactly as CpuSparseLuSolver does for the NR loop). Per-instance state: NOT thread-safe, so parallel
    // harmonic orders must use one solver per worker (see ContingencyAnalysis's ThreadLocal pattern).
    private int[]? _q;
    private int[]? _rowPtr;
    private int[]? _colIdx;

    public string Name => "complex-real-expanded (sparse-lu)";

    // A single solve is a factor-once against one RHS - so Solve is exactly Factor().Solve(), keeping the
    // one-shot and reusable paths bit-identical and sharing the 2n assembly.
    // GR-447: an empty system has the empty solution, as the other complex solvers return; Factor's null for n == 0
    // ("singular" under the seam contract) is not reached for it.
    public Complex[]? Solve(ComplexCsr a, Complex[] b) => a.N == 0 ? Array.Empty<Complex>() : Factor(a)?.Solve(b);

    /// <summary>Assemble the real 2n x 2n system <c>[G -B; B G]</c> once, order and factor it once, and
    /// return a handle that back-substitutes each complex RHS - so a harmonic caller solving many RHS
    /// against one Y(h) pays a single ordering + Gilbert-Peierls factorization instead of one per solve.</summary>
    public IComplexLinearFactorization? Factor(ComplexCsr a)
    {
        int n = a.N;
        var builder = new CsrMatrix.Builder(2 * n);
        for (int i = 0; i < n; i++)
            for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
            {
                int j = a.ColIdx[k];
                double g = a.G[k], bb = a.B[k];
                builder.Add(i, j, g);              // G  (top-left)
                builder.Add(i, n + j, -bb);        // -B (top-right)
                builder.Add(n + i, j, bb);         // B  (bottom-left)
                builder.Add(n + i, n + j, g);      // G  (bottom-right)
            }

        CsrMatrix m = builder.Build();
        // SparseLu.Solve(m, rhs) == FactorWithOrdering(m, RCM(m)).Solve(rhs), so splitting the ordering +
        // factorization out here and reusing it across RHS is bit-identical to calling Solve per RHS. The
        // ordering is additionally cached across same-pattern matrices (successive harmonic orders).
        if (_q is null || !SamePattern(m)) { _q = SparseLu.Ordering(m); _rowPtr = m.RowPtr; _colIdx = m.ColIdx; }
        SparseLuFactorization? lu = SparseLu.FactorWithOrdering(m, _q);
        return lu is null ? null : new RealExpandedFactorization(n, lu);
    }

    private bool SamePattern(CsrMatrix m)
        => _rowPtr is not null && _colIdx is not null
        && m.RowPtr.AsSpan().SequenceEqual(_rowPtr)
        && m.ColIdx.AsSpan().SequenceEqual(_colIdx);

    private sealed class RealExpandedFactorization : IComplexLinearFactorization
    {
        private readonly int _n;
        private readonly SparseLuFactorization _lu;
        public RealExpandedFactorization(int n, SparseLuFactorization lu) { _n = n; _lu = lu; }

        public Complex[]? Solve(Complex[] b)
        {
            var rhs = new double[2 * _n];
            for (int i = 0; i < _n; i++) { rhs[i] = b[i].Real; rhs[_n + i] = b[i].Imaginary; }
            double[]? sol = _lu.Solve(rhs);
            if (sol is null) return null;
            // GR-467: a non-finite solution (from a NaN or infinite right-hand side, or overflow in the sweep) is a
            // failed solve, reported as null exactly like a singular one, never handed back as a result.
            if (!AllFinite(sol)) return null;
            var x = new Complex[_n];
            for (int i = 0; i < _n; i++) x[i] = new Complex(sol[i], sol[_n + i]);
            return x;
        }

        /// <summary>True when every entry is finite (GR-467: the fail-loud test of a real-expanded solution).</summary>
        private static bool AllFinite(double[] v)
        {
            foreach (double d in v)
                if (!double.IsFinite(d)) return false;
            return true;
        }

        // N060 / R115: the three real 2n-vectors of a solve (right-hand side, solution, sweep scratch), reused. Taken
        // with an atomic exchange and put back afterwards, so two threads solving with the same factorization never
        // share them (the second simply allocates its own set); a single-threaded caller allocates them once.
        private sealed class Scratch
        {
            public readonly double[] Rhs, Sol, Work;
            public Scratch(int m) { Rhs = new double[m]; Sol = new double[m]; Work = new double[m]; }
        }
        private Scratch? _spare;

        /// <inheritdoc />
        /// <remarks>Exactly <see cref="Solve"/>'s arithmetic without its three allocations: the right-hand side is
        /// fully rewritten (every real and imaginary slot, as in <see cref="Solve"/>), the sweep is
        /// <see cref="SparseLuFactorization.SolveInto"/> (operation for operation <see cref="SparseLuFactorization.Solve"/>,
        /// which writes every slot of its working and output vectors through the row and column permutations, so the
        /// reused vectors' old contents are never read), and the complex result is assembled the same way.</remarks>
        public bool SolveInto(Complex[] b, Complex[] x)
        {
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(x);
            if (ReferenceEquals(b, x)) throw new ArgumentException("x must not be the right-hand side array.", nameof(x));
            if (b.Length < _n) throw new ArgumentException($"b holds {b.Length} entries; the system has {_n} unknowns.", nameof(b));
            if (x.Length != _n) throw new ArgumentException($"x holds {x.Length} entries; the system has {_n} unknowns.", nameof(x));

            Scratch s = Interlocked.Exchange(ref _spare, null) ?? new Scratch(2 * _n);
            try
            {
                double[] rhs = s.Rhs;
                for (int i = 0; i < _n; i++) { rhs[i] = b[i].Real; rhs[_n + i] = b[i].Imaginary; }
                _lu.SolveInto(rhs, s.Sol, s.Work);
                double[] sol = s.Sol;
                // GR-467: false (as Solve's null) on a non-finite solution; x is then unspecified, per the contract.
                if (!AllFinite(sol)) return false;
                for (int i = 0; i < _n; i++) x[i] = new Complex(sol[i], sol[_n + i]);
                return true;
            }
            finally
            {
                Volatile.Write(ref _spare, s);
            }
        }
    }
}

/// <summary>
/// Native complex sparse solve: Jacobi-preconditioned complex BiCGSTAB operating directly on the
/// <see cref="ComplexCsr"/> in complex arithmetic (no real expansion, no dense fill) - a third, independent
/// path that cross-validates the other two. Returns <c>null</c> on breakdown/stagnation/non-finite iterates
/// or a missing/zero diagonal, so a caller can fall back to the direct solvers.
/// <para>All inner products are the CONJUGATED (Hermitian) form <c>(a, b) = sum conj(a_i) b_i</c>, the
/// standard complex BiCGSTAB (van der Vorst). Before October 2026 (R278) rho, alpha's denominator and omega
/// used the bilinear sum <c>a_i b_i</c>: with rhat = r0 the very first rho = sum r0_i^2 is exactly zero for a
/// valid non-zero complex residual such as [1, i] (1 + i^2 = 0), so the solver "broke down" on a perfectly
/// good system, and omega was not the residual-minimising step.</para>
/// </summary>
public sealed class SparseComplexBiCgStabSolver : IComplexLinearSolver
{
    private readonly double _tol;
    private readonly int _maxIter;

    /// <param name="tol">Relative residual tolerance ||r|| / ||b||; defaults to
    /// <see cref="NumericTolerances.DefaultComplexKrylovTolerance"/>.</param>
    /// <param name="maxIter">Iteration cap; 0 selects <see cref="NumericTolerances.DefaultKrylovMaxIterations"/>.</param>
    public SparseComplexBiCgStabSolver(double tol = NumericTolerances.DefaultComplexKrylovTolerance, int maxIter = 0)
    {
        if (!(tol > 0.0) || double.IsInfinity(tol))
            throw new ArgumentOutOfRangeException(nameof(tol), tol, "The BiCGSTAB tolerance must be a positive finite number.");
        if (maxIter < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIter), maxIter, "The BiCGSTAB iteration cap must be zero (default) or positive.");
        _tol = tol;
        _maxIter = maxIter;
    }

    public string Name => "complex-bicgstab (jacobi)";

    public Complex[]? Solve(ComplexCsr a, Complex[] b)
    {
        int n = a.N;
        if (n == 0) return Array.Empty<Complex>();
        int maxIter = _maxIter > 0 ? _maxIter : NumericTolerances.DefaultKrylovMaxIterations(n);

        // Jacobi (diagonal) preconditioner.
        var invDiag = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            int dp = a.DiagPtr[i];
            Complex d = dp >= 0 ? new Complex(a.G[dp], a.B[dp]) : Complex.Zero;
            if (d == Complex.Zero || Complex.IsNaN(d)) return null;
            invDiag[i] = Complex.One / d;
        }

        void Mv(Complex[] xv, Complex[] yv)
        {
            for (int i = 0; i < n; i++)
            {
                Complex s = Complex.Zero;
                for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                    s += new Complex(a.G[k], a.B[k]) * xv[a.ColIdx[k]];
                yv[i] = s;
            }
        }

        var x = new Complex[n];
        var r = (Complex[])b.Clone();          // r = b - A x0 = b (x0 = 0)
        var rhat = (Complex[])r.Clone();
        var p = new Complex[n];
        var v = new Complex[n];
        var s = new Complex[n];
        var t = new Complex[n];
        var ph = new Complex[n];
        var sh = new Complex[n];

        double bnorm = Norm(b);
        // GR-448: x = 0 only for an exactly zero b; a non-zero b whose norm underflows to 0 is a failure (null).
        if (bnorm == 0.0) return Array.TrueForAll(b, bi => bi == Complex.Zero) ? x : null;

        Complex rho = Complex.One, alpha = Complex.One, omega = Complex.One;

        for (int it = 0; it < maxIter; it++)
        {
            Complex rhoNew = Dot(rhat, r);      // conjugated inner product (rhat, r)
            if (rhoNew.Magnitude < NumericTolerances.UnderflowGuard || Complex.IsNaN(rhoNew)) return null;

            Complex beta = (rhoNew / rho) * (alpha / omega);
            for (int i = 0; i < n; i++) p[i] = r[i] + beta * (p[i] - omega * v[i]);

            for (int i = 0; i < n; i++) ph[i] = p[i] * invDiag[i];  // Jacobi apply
            Mv(ph, v);

            Complex rhatV = Dot(rhat, v);
            if (rhatV == Complex.Zero || Complex.IsNaN(rhatV)) return null;
            alpha = rhoNew / rhatV;

            for (int i = 0; i < n; i++) s[i] = r[i] - alpha * v[i];
            if (Norm(s) / bnorm < _tol)
            {
                for (int i = 0; i < n; i++) x[i] += alpha * ph[i];
                return TrueResidualMeets(x, t) ? Finite(x) : null;
            }

            for (int i = 0; i < n; i++) sh[i] = s[i] * invDiag[i];
            Mv(sh, t);

            Complex tt = Dot(t, t);
            omega = tt == Complex.Zero ? Complex.Zero : Dot(t, s) / tt;

            for (int i = 0; i < n; i++) { x[i] += alpha * ph[i] + omega * sh[i]; r[i] = s[i] - omega * t[i]; }
            rho = rhoNew;

            double rnorm = Norm(r);
            if (double.IsNaN(rnorm)) return null;
            if (rnorm / bnorm < _tol) return TrueResidualMeets(x, t) ? Finite(x) : null;
            if (omega == Complex.Zero) return null;
        }
        return null;

        // Success is decided on the TRUE residual b - A x (one more product into the scratch t), not only on the
        // recursively updated one, which drifts from it in finite precision; a solve whose true residual misses the
        // tolerance reports failure (null) instead of a solution it did not reach. Ordinary solves are unchanged.
        bool TrueResidualMeets(Complex[] xs, Complex[] scratch)
        {
            Mv(xs, scratch);
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                Complex d = b[i] - scratch[i];
                sum += d.Real * d.Real + d.Imaginary * d.Imaginary;
            }
            return Math.Sqrt(sum) / bnorm < _tol;
        }
    }

    // Conjugated (Hermitian) inner product (a, b) = sum(conj(a_i) * b_i). With it (r0, r0) = ||r0||^2 > 0 for
    // any non-zero residual, and (t, s) / (t, t) is the step that minimises ||s - omega t||.
    private static Complex Dot(Complex[] a, Complex[] b)
    {
        Complex s = Complex.Zero;
        for (int i = 0; i < a.Length; i++) s += Complex.Conjugate(a[i]) * b[i];
        return s;
    }

    // Euclidean norm sqrt(sum |a_i|^2) - conjugated, real.
    private static double Norm(Complex[] a)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i].Real * a[i].Real + a[i].Imaginary * a[i].Imaginary;
        return Math.Sqrt(s);
    }

    private static Complex[]? Finite(Complex[] x)
    {
        foreach (Complex c in x)
            if (Complex.IsNaN(c) || double.IsInfinity(c.Real) || double.IsInfinity(c.Imaginary)) return null;
        return x;
    }
}
