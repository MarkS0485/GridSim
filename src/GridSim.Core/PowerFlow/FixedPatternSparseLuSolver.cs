using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// OPT-IN sparse LU solver that treats the matrix pattern as a function of the CSR STRUCTURE alone: every stored entry
/// is part of the pattern, explicit zeros included. Because the pattern then never depends on the values, the symbolic
/// work of one factorisation (each column's depth-first reach and pivot row, recorded as a <see cref="SparseLuPlan"/>)
/// can be replayed for the next matrix of the same structure (<see cref="SparseLuFactorization.Refactor"/>): about half
/// the cost of a full factorisation on the gb-full Jacobian.
///
/// <para><b>The pivot rule.</b> A plan is reusable only while the pivot order holds. Plain partial pivoting moves pivots
/// between Newton steps (8 to 12 columns of the case300 Jacobian at every step, near-ties broken by rounding), so this
/// solver pivots by default with a diagonal preference (<see cref="DefaultDiagonalPreference"/>): a column's diagonal is
/// its pivot whenever its magnitude is at least that fraction of the largest candidate's (threshold partial pivoting, as
/// KLU and UMFPACK offer). On gb-full the plan is then replayed for 8 of the 11 Newton factorisations.</para>
///
/// <para><b>Not the default, and not bit-identical to it.</b> The default solver (<see cref="CpuSparseLuSolver"/>)
/// drops exact-zero entries by value before factorising and uses plain partial pivoting. Keeping the zeros changes the
/// order of the depth-first reach, and the diagonal preference changes pivots, so the last bits of every solve differ
/// from the default path's (the two agree to rounding: gb-full voltages within 2.4e-13 pu). Nothing in GridSim uses this class unless a caller constructs it and passes it in (for example
/// <c>new SparseNewtonRaphsonPowerFlow(new FixedPatternSparseLuSolver())</c>); every default result is untouched.</para>
///
/// <para><b>What it does guarantee.</b> Each solve is bit-identical to a FRESH structural factorisation of the same
/// matrix (same ordering, zeros kept, same pivot rule, full Gilbert-Peierls): a replay produces exactly the fresh factorisation's L and
/// U, or is refused when the pivot search would choose a different row, in which case the full factorisation runs and
/// its plan replaces the old one. So the result never depends on which earlier matrices the instance has seen.</para>
///
/// <para><b>Not thread-safe</b> (it caches the ordering and plan per instance), like <see cref="CpuSparseLuSolver"/>.</para>
/// </summary>
public sealed class FixedPatternSparseLuSolver : ISparseLinearSolver
{
    private int[]? _q;
    private int[]? _rowPtr;
    private int[]? _colIdx;
    private SparseLuPlan? _plan;

    /// <summary>An opt-in fixed-pattern solver with the given pivot rule (see the type summary).</summary>
    /// <param name="diagonalPreference">The diagonal-preference threshold of the pivot search, in (0, 1]: a column's
    /// diagonal row is the pivot whenever its magnitude is at least this fraction of the largest candidate's. 0 is plain
    /// partial pivoting, which on power-flow Jacobians moves pivots between Newton steps (near-ties broken by rounding), so
    /// the plan is then rarely replayed.</param>
    public FixedPatternSparseLuSolver(double diagonalPreference = DefaultDiagonalPreference)
    {
        if (!(diagonalPreference >= 0.0 && diagonalPreference <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(diagonalPreference), diagonalPreference, "the diagonal preference must lie in [0, 1]");
        DiagonalPreference = diagonalPreference;
    }

    /// <summary>The default diagonal-preference threshold of this opt-in solver.</summary>
    public const double DefaultDiagonalPreference = 0.1;

    /// <summary>The pivot rule this instance factors under.</summary>
    public double DiagonalPreference { get; }

    public string Name => "sparse-lu fixed-pattern (cpu, opt-in)";

    /// <summary>Solves served by replaying the cached plan.</summary>
    public int PlanReplays { get; private set; }

    /// <summary>Solves that ran the full factorisation (a new structure, or a pivot that moved).</summary>
    public int FullFactorisations { get; private set; }

    public double[]? Solve(CsrMatrix a, double[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.N == 0) return Array.Empty<double>();
        return Factor(a)?.Solve(b);
    }

    /// <summary>The factorisation of <paramref name="a"/> this solver would use (null when singular); the test seam for
    /// comparing its L and U with a fresh structural factorisation.</summary>
    internal SparseLuFactorization? Factor(CsrMatrix a)
    {
        int n = a.N;
        bool sameStructure = _q is not null
            && a.RowPtr.AsSpan().SequenceEqual(_rowPtr)
            && a.ColIdx.AsSpan().SequenceEqual(_colIdx);
        if (!sameStructure)
        {
            _q = SparseLu.Ordering(a);
            _rowPtr = (int[])a.RowPtr.Clone();
            _colIdx = (int[])a.ColIdx.Clone();
            _plan = null;
        }
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCscKeepingZeros(a);
        if (_plan is not null)
        {
            SparseLuFactorization? replay = SparseLuFactorization.Refactor(_plan, ax);
            if (replay is not null) { PlanReplays++; return replay; }
            // Refused: a moved pivot (or a singular matrix). The full factorisation decides which, exactly as fresh.
        }
        FullFactorisations++;
        SparseLuFactorization? full = SparseLuFactorization.Factor(n, ap, ai, ax, _q!, keepPlan: true, diagonalPreference: DiagonalPreference);
        if (full is not null) _plan = full.Plan;
        return full;
    }

    /// <summary>A fresh structural factorisation (ordering from the structure, zeros kept, full Gilbert-Peierls, no
    /// plan): the reference every <see cref="Solve"/> of this class equals bit for bit.</summary>
    internal static SparseLuFactorization? FreshStructural(CsrMatrix a, double diagonalPreference = DefaultDiagonalPreference)
    {
        (int[] ap, int[] ai, double[] ax) = SparseLu.ToCscKeepingZeros(a);
        return SparseLuFactorization.Factor(a.N, ap, ai, ax, SparseLu.Ordering(a), diagonalPreference: diagonalPreference);
    }
}
