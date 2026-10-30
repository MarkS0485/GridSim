namespace GridSim.Core.Abstractions;

/// <summary>
/// Solves many independent, equally-sized dense linear systems in one call. Where <see cref="ILinearSolver"/>
/// solves a single <c>A x = b</c>, this solves <c>count</c> systems <c>A_s x_s = b_s</c> (all <c>n×n</c>) as a
/// batch - the shape a GPU wants, so a device can factor many small systems in one dispatch instead of paying
/// the per-solve launch + global-lock overhead once per system. The motivating workload is a contingency
/// sweep, where every outage yields a same-sized system that is otherwise solved one-at-a-time.
/// <para>
/// A CPU reference implementation ships now (it just loops the dense LU); a GPU implementation drops in behind
/// this seam. When no device is present the GPU implementation defers to the CPU loop, so this is always safe
/// to construct and call.
/// </para>
/// <para>
/// R071 (October 2026): the contingency sweep now batches its Newton steps
/// (<see cref="Analysis.ContingencyAnalysis.ScreenBranchesBatched"/>) through the SPARSE batched seam,
/// <see cref="IBatchedSparseLinearSolver"/>, because the canonical CPU Newton step is the sparse LU and a dense LU
/// differs from it in the last bits. This dense seam reaches the sweep through
/// <see cref="PowerFlow.DenseBatchSparseAdapter"/> (the GPU route, outside the CPU bit-identity contract).
/// </para>
/// </summary>
public interface IBatchedLinearSolver
{
    string Name { get; }

    /// <summary>
    /// Solve <paramref name="count"/> independent <paramref name="n"/>×<paramref name="n"/> systems.
    /// <paramref name="aFlat"/> is row-major, length <c>count*n*n</c> (system <c>s</c> starts at
    /// <c>s*n*n</c>); <paramref name="bFlat"/> is length <c>count*n</c> (rhs for system <c>s</c> at
    /// <c>s*n</c>). The solution overwrites each system's segment of <paramref name="bFlat"/> in place, and
    /// <paramref name="aFlat"/> may be overwritten too (allocation-free hot path).
    /// </summary>
    /// <returns>
    /// A per-system success mask of length <paramref name="count"/>: <c>false</c> for a system whose matrix
    /// was singular/failed, whose <paramref name="bFlat"/> segment is left filled with <see cref="double.NaN"/>
    /// (so a failed contingency reads as "no solution", never as stale numbers). Never throws for a singular
    /// system - only for malformed arguments.
    /// </returns>
    bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat);
}
