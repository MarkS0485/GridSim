using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// R071 (October 2026): the CPU reference <see cref="IBatchedSparseLinearSolver"/>. Each system in the batch is solved
/// by a <see cref="CpuSparseLuSolver"/>, the canonical Newton step solver, so a batched contingency sweep is
/// bit-identical to the one-at-a-time sweep BY CONSTRUCTION: the very same function runs on the very same inputs.
/// <para>
/// Why a fresh <see cref="CpuSparseLuSolver"/> per system is still bit-identical: that solver only caches the
/// reverse Cuthill-McKee ordering, and the ordering is a deterministic function of the sparsity pattern alone (no
/// random tie-break, no state). Recomputing it gives the same permutation the cache would have held, so the
/// factorisation and the solution are the same bits. The cache is a speed device, never a numerical one.
/// </para>
/// <para>
/// The systems are independent, so they run in parallel across the batch; each writes only its own result slot, so
/// the order in which they finish cannot change any number.
/// </para>
/// </summary>
public sealed class CpuBatchedSparseLuSolver : IBatchedSparseLinearSolver
{
    public string Name => "batched-sparse-lu (cpu)";

    public double[]?[] SolveBatch(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
    {
        BatchedSparseArguments.Require(matrices, rhs, errors);

        int count = matrices.Count;
        var results = new double[]?[count];
        if (count == 0) return results;

        Parallel.For(0, count, s =>
        {
            // A per-system exception is handed back, not thrown: the other systems in the batch must still finish,
            // and the caller rethrows it on the system's own thread (see the interface contract).
            try
            {
                results[s] = new CpuSparseLuSolver().Solve(matrices[s], rhs[s]);
            }
            catch (Exception ex)
            {
                results[s] = null;
                errors[s] = ex;
            }
        });
        return results;
    }
}

/// <summary>R071: the argument checks every <see cref="IBatchedSparseLinearSolver"/> shares, so a malformed batch is
/// refused the same way (by name) whichever backend receives it.</summary>
internal static class BatchedSparseArguments
{
    public static void Require(IReadOnlyList<CsrMatrix> matrices, IReadOnlyList<double[]> rhs, Exception?[] errors)
    {
        ArgumentNullException.ThrowIfNull(matrices);
        ArgumentNullException.ThrowIfNull(rhs);
        ArgumentNullException.ThrowIfNull(errors);
        if (rhs.Count != matrices.Count)
            throw new ArgumentException(
                $"batched sparse solve: {matrices.Count} matrices but {rhs.Count} right-hand sides; they must pair one to one.",
                nameof(rhs));
        if (errors.Length != matrices.Count)
            throw new ArgumentException(
                $"batched sparse solve: the error array holds {errors.Length} slots for {matrices.Count} systems.",
                nameof(errors));
        for (int s = 0; s < matrices.Count; s++)
        {
            if (matrices[s] is null)
                throw new ArgumentException($"batched sparse solve: system {s} has no matrix.", nameof(matrices));
            if (rhs[s] is null)
                throw new ArgumentException($"batched sparse solve: system {s} has no right-hand side.", nameof(rhs));
            if (rhs[s].Length != matrices[s].N)
                throw new ArgumentException(
                    $"batched sparse solve: system {s} is {matrices[s].N} x {matrices[s].N} but its right-hand side has "
                    + $"{rhs[s].Length} entries.", nameof(rhs));
        }
    }
}
