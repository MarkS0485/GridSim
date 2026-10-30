using GridSim.Core.Abstractions;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The reference (and fallback) <see cref="IBatchedLinearSolver"/>: it just solves each system in the batch
/// with the proven <see cref="DenseLuSolver"/>, in parallel across the batch. This is the ground truth the GPU
/// batched kernel is validated against, and the path a GPU implementation defers to when no device is present.
/// A singular system is caught per-system (its rhs segment is filled with NaN and its success flag cleared)
/// rather than failing the whole batch - a contingency that islands must not abort the sweep.
/// </summary>
public sealed class CpuBatchedLuSolver : IBatchedLinearSolver
{
    public string Name => "batched-dense-lu (cpu)";

    public bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat)
    {
        if (count < 0 || n < 0) throw new ArgumentOutOfRangeException(nameof(count), "count and n must be non-negative.");
        if (aFlat.Length < (long)count * n * n) throw new ArgumentException("aFlat is too short for count*n*n.", nameof(aFlat));
        if (bFlat.Length < (long)count * n) throw new ArgumentException("bFlat is too short for count*n.", nameof(bFlat));

        var ok = new bool[count];
        if (count == 0 || n == 0) { Array.Fill(ok, true); return ok; }

        Parallel.For(0, count, s =>
        {
            int aOff = s * n * n;
            int bOff = s * n;

            // R071: each system is factored and solved IN PLACE on its own segment of the batch arrays (the
            // interface lets aFlat be overwritten): the dense LU's core on a span slice, so there is no per-system
            // double[n,n], no solver object and no flat working copy (three n*n allocations per system before).
            // The slices are by element offset, which is always below the array length, so nothing overflows (the
            // old copy used a BYTE offset computed in int, which overflowed past 268,435,455 elements). Same
            // operations in the same order as DenseLuSolver.SolveInPlace, so the solution bits are unchanged.
            try
            {
                DenseLuSolver.FactorSolveInPlace(aFlat.AsSpan(aOff, n * n), bFlat.AsSpan(bOff, n), n);
                // Solved only when every entry is finite: a NaN in the matrix away from the pivot path eliminated into a
                // NaN solution with no singular pivot, which used to be reported ok.
                bool finite = true;
                for (int i = 0; i < n && finite; i++) finite = double.IsFinite(bFlat[bOff + i]);
                if (!finite)
                    for (int i = 0; i < n; i++) bFlat[bOff + i] = double.NaN;
                ok[s] = finite;
            }
            catch (InvalidOperationException)
            {
                for (int i = 0; i < n; i++) bFlat[bOff + i] = double.NaN;   // singular: no solution
                ok[s] = false;
            }
        });

        return ok;
    }
}
