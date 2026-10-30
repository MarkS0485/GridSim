using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The default CPU sparse linear solver: the direct sparse LU (reverse Cuthill-McKee ordering +
/// partial pivoting). The reference the GPU sparse backend is validated against.
///
/// It caches the fill-reducing ORDERING across calls whose matrix has the same sparsity pattern.
/// A Newton-Raphson power flow solves the same-structured Jacobian once per iteration (only the
/// values change as the voltages update); recomputing the RCM ordering - a sort/BFS over the graph
/// - every iteration is pure repeated structural work. Because RCM is a deterministic function of
/// the pattern, reusing it is bit-identical to recomputing it, so this is a speed-only change with
/// no effect on the solution. The pattern is compared by content each call (O(n+nnz), cheap next to
/// the ordering it saves); a changed structure (a Q-limit bus-type switch rebuilds the Jacobian
/// pattern) transparently triggers a fresh ordering.
/// <para><b>Not thread-safe</b> (GR-490): the cached ordering is per instance and unguarded, so one instance must not be
/// called from two threads at once. Parallel sweeps give each worker its own solver (a factory), as the contingency
/// screens do.</para>
/// </summary>
public sealed class CpuSparseLuSolver : ISparseLinearSolver
{
    private int[]? _q;
    private int[]? _rowPtr;
    private int[]? _colIdx;

    public string Name => "sparse-lu (cpu)";

    public double[]? Solve(CsrMatrix a, double[] b)
    {
        if (a.N == 0) return System.Array.Empty<double>();

        if (_q is null || !SamePattern(a))
        {
            _q = SparseLu.Ordering(a);
            _rowPtr = a.RowPtr;
            _colIdx = a.ColIdx;
        }
        SparseLuFactorization? f = SparseLu.FactorWithOrdering(a, _q);
        if (f is null) return null;   // singular: the caller's fallback, as before
        // Bug #40: a NaN or infinite right-hand side is refused (it used to come back as a NaN / Inf solution). Checked
        // after the factorisation so a bad matrix keeps its own refusal; a read-only check that changes no finite solve.
        int badRow = NumericTolerances.FirstNonFinite(b);
        if (badRow >= 0)
            throw new ArgumentException(NumericTolerances.NonFiniteRightHandSide("Sparse LU", b[badRow], badRow), nameof(b));
        return f.Solve(b);
    }

    /// <summary>Whether <paramref name="a"/>'s CSR structure matches the last-ordered matrix's, by content.</summary>
    private bool SamePattern(CsrMatrix a)
        => _rowPtr is not null && _colIdx is not null
        && a.RowPtr.AsSpan().SequenceEqual(_rowPtr)
        && a.ColIdx.AsSpan().SequenceEqual(_colIdx);
}
