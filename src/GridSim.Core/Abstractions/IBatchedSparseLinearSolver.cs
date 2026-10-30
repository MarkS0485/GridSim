using GridSim.Core.Numerics;

namespace GridSim.Core.Abstractions;

/// <summary>
/// R071 (October 2026): solves many independent SPARSE systems <c>A_s x_s = b_s</c> in one call. This is the batched
/// counterpart of <see cref="ISparseLinearSolver"/>, and it is the seam the contingency sweep's Newton steps go through
/// when the sweep is run batched (<see cref="Analysis.ContingencyAnalysis.ScreenBranchesBatched"/>).
/// <para>
/// Why a sparse seam next to the dense <see cref="IBatchedLinearSolver"/>: every CPU Newton power flow takes its step
/// with the canonical sparse LU (<see cref="PowerFlow.CpuSparseLuSolver"/>; N057 routes every size sparse, and the dense
/// engine shares the same sparse step). A dense batched LU pivots in a different order, so its solution differs from
/// the canonical step in the last bits, and a sweep built on it could never be bit-identical to the one-at-a-time
/// sweep. The systems in one batch also need not share a size or a sparsity pattern (each outage removes a different
/// branch, and a Q-limit switch changes the Jacobian's size), which the dense seam cannot carry. The dense seam still
/// reaches the sweep through <see cref="PowerFlow.DenseBatchSparseAdapter"/>, for a GPU backend, outside the
/// bit-identity contract exactly as the single-system GPU solvers are.
/// </para>
/// </summary>
public interface IBatchedSparseLinearSolver
{
    string Name { get; }

    /// <summary>
    /// Solve <c>matrices.Count</c> independent systems. <paramref name="matrices"/>[s] and <paramref name="rhs"/>[s]
    /// form system s; neither may be modified. Returns one entry per system: the solution (a new array of length
    /// <c>matrices[s].N</c>), or <c>null</c> when the system is singular, exactly as
    /// <see cref="ISparseLinearSolver.Solve"/> reports it (the caller then takes its own fall-back path).
    /// <para>
    /// A system whose solve THROWS (for example a NaN entry, which the sparse LU refuses by name) must not abort the
    /// other systems: its exception is stored in <paramref name="errors"/>[s] (an array of length
    /// <c>matrices.Count</c> supplied by the caller, all null on entry) and its result entry is null. The caller then
    /// rethrows it on the system's own thread, so a batched solve fails exactly as a one-at-a-time solve would have.
    /// A malformed call (mismatched counts, a null system) throws <see cref="ArgumentException"/>.
    /// </para>
    /// </summary>
    double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors);
}
