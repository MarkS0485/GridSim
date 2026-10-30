using GridSim.Core.Abstractions;
using GridSim.Core.PowerFlow;
using ILGPU;
using ILGPU.Runtime;

namespace GridSim.Gpu;

/// <summary>
/// GPU batched dense linear solver: solves <c>count</c> independent <c>n×n</c> systems <c>A_s x_s = b_s</c> in
/// one dispatch, by running the same Gaussian-elimination-with-partial-pivoting kernels as
/// <see cref="GpuDenseLinearSolver"/> but with a batch index threaded through every kernel. All systems share
/// a single flat augmented buffer <c>[A_s | b_s]</c> (system <c>s</c> at stride <c>n*(n+1)</c>), so the whole
/// batch is factored under one <see cref="GpuContext.Lock"/> acquisition: three host-to-device copies up front (the
/// augmented systems, the zeroed singularity flags, the per-system pivot floors), every elimination and
/// back-substitution kernel queued with no host stall in between, then ONE device synchronisation and two
/// device-to-host copies (the flags and the solutions). D-DOC-209 (October 2026): this used to say "one pair of host
/// synchronisations", which matched no count in the code; the sequence above is read off the code below. This
/// amortises the per-solve launch and lock overhead across the batch: a contingency sweep of hundreds of same-sized
/// outages becomes a handful of batched dispatches instead of hundreds of locked single solves.
/// <para>
/// A singular system does not fail the batch, but it is masked LATE (D-DOC-209, stated rather than changed): the
/// pivot kernel records the first column whose pivot falls to or below the system's floor in its on-device flag, yet
/// the swap, elimination and back-substitution kernels do not read that flag, so the system keeps being eliminated
/// and back-substituted (its own segment may fill with Inf / NaN; each system's segment is disjoint, so no other
/// system is touched) and only the host, after the final copy, replaces its solution with NaN and clears its success
/// flag. The returned flags and every non-singular solution are as the per-system design intends; only the wasted
/// device work differs from the CPU reference (<see cref="CpuBatchedLuSolver"/> stops a singular system at its first
/// small pivot). UNVERIFIED ON HARDWARE: no GPU is available to the October 2026 review, so neither this description
/// nor an early-exit change could be run on a device; the kernels are left exactly as they were.
/// When no accelerator of the target type is present the whole call defers to the CPU
/// <see cref="CpuBatchedLuSolver"/>, so this is always safe to construct and call.
/// </para>
/// </summary>
public sealed class GpuBatchedDenseSolver : IBatchedLinearSolver
{
    private readonly AcceleratorType _accel;

    /// <param name="accel">Which ILGPU accelerator to target (CUDA by default, or OpenCL).</param>
    public GpuBatchedDenseSolver(AcceleratorType accel = AcceleratorType.Cuda) => _accel = accel;

    public string Name => $"batched-dense-lu (gpu/{(_accel == AcceleratorType.OpenCL ? "opencl" : "ilgpu")})";

    /// <summary>Threads per block for the on-device per-system pivot reduction. Power of two, &lt;= 1024.</summary>
    private const int ReduceGroupSize = 128;

    public bool[] SolveBatch(int count, int n, double[] aFlat, double[] bFlat)
    {
        if (count < 0 || n < 0) throw new ArgumentOutOfRangeException(nameof(count), "count and n must be non-negative.");
        if (aFlat.Length < (long)count * n * n) throw new ArgumentException("aFlat is too short for count*n*n.", nameof(aFlat));
        if (bFlat.Length < (long)count * n) throw new ArgumentException("bFlat is too short for count*n.", nameof(bFlat));

        // No device (or a trivial batch): the CPU reference IS the fallback, per the seam's contract.
        if (count == 0 || n == 0 || !GpuContext.IsAvailableFor(_accel))
            return new CpuBatchedLuSolver().SolveBatch(count, n, aFlat, bFlat);

        var acc = GpuContext.AcceleratorFor(_accel)!;
        int ncol = n + 1;              // augmented: last column holds b
        int rhs = n;

        // One flat augmented buffer for the whole batch; a caller with more systems than fit one dispatch
        // must chunk (each chunk is an independent SolveBatch call).
        long augLen = (long)count * n * ncol;
        if (augLen > int.MaxValue)
            throw new ArgumentException("Batch too large for a single dispatch (count*n*(n+1) exceeds int range); chunk it.", nameof(count));

        // Flatten each system into a row-major augmented matrix [A_s | b_s], and record each system's scale
        // for its own relative near-singular floor (mirrors DenseLuSolver, but per system).
        var aug = new double[augLen];
        var floors = new double[count];
        for (int s = 0; s < count; s++)
        {
            int aBase = s * n * n;
            int augBase = s * n * ncol;
            double aNorm = 0.0;
            for (int i = 0; i < n; i++)
            {
                int augRow = augBase + i * ncol;
                int aRow = aBase + i * n;
                for (int j = 0; j < n; j++)
                {
                    double v = aFlat[aRow + j];
                    aug[augRow + j] = v;
                    double av = Math.Abs(v);
                    if (av > aNorm) aNorm = av;
                }
                aug[augRow + rhs] = bFlat[s * n + i];
            }
            floors[s] = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);   // the shared relative floor (WS5)
        }

        var info = new int[count];   // 0 = ok; else (firstSingularCol + 1)
        var xHost = new double[count * n];

        lock (GpuContext.Lock)
        {
            using var dAug = acc.Allocate1D<double>(aug.Length);
            using var dPiv = acc.Allocate1D<int>(count);       // pivot row for the current column, per system
            using var dInfo = acc.Allocate1D<int>(count);      // per-system singularity flag
            using var dFloor = acc.Allocate1D<double>(count);
            using var dX = acc.Allocate1D<double>(count * n);
            dAug.CopyFromCPU(aug);
            dInfo.CopyFromCPU(new int[count]);
            dFloor.CopyFromCPU(floors);

            var pivot = acc.LoadStreamKernel<ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>, int, int, int>(PivotBatchKernel);
            var swap = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, int, int, int>(SwapBatchKernel);
            var eliminate = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, int, int, int>(EliminateBatchKernel);
            var divide = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, int, int, int, int>(BackDivideBatchKernel);
            var backUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, int, int, int, int>(BackUpdateBatchKernel);

            // One block per system for the pivot reduction; the other kernels are 1-D over (system × row/col).
            var pivotConfig = new KernelConfig(count, ReduceGroupSize);

            // ---- Forward elimination with partial pivoting, batched (all queued, no host stalls) ----
            for (int k = 0; k < n; k++)
            {
                pivot(pivotConfig, dAug.View, dPiv.View, dInfo.View, dFloor.View, k, n, ncol);
                swap(count * ncol, dAug.View, dPiv.View, k, n, ncol);
                eliminate(count * n, dAug.View, k, n, ncol);
            }

            // ---- Back substitution, batched (column sweep) ----
            for (int i = n - 1; i >= 0; i--)
            {
                divide(count, dAug.View, dX.View, i, rhs, n, ncol);
                if (i > 0)
                    backUpd(count * n, dAug.View, dX.View, i, rhs, n, ncol);
            }

            acc.Synchronize();
            dInfo.CopyToCPU(info);
            dX.CopyToCPU(xHost);
        }

        // Assemble results: a good system's solution back into its rhs segment; a singular one -> NaN.
        var ok = new bool[count];
        for (int s = 0; s < count; s++)
        {
            int bOff = s * n;
            // A system is solved only when the device reported no singular pivot AND every solution entry is finite: a
            // NaN in a non-pivot position eliminated into a NaN solution with info 0, which was reported ok.
            bool finite = true;
            for (int i = 0; i < n && finite; i++) finite = double.IsFinite(xHost[bOff + i]);
            if (info[s] == 0 && finite)
            {
                Array.Copy(xHost, bOff, bFlat, bOff, n);
                ok[s] = true;
            }
            else
            {
                for (int i = 0; i < n; i++) bFlat[bOff + i] = double.NaN;
                ok[s] = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// Per-system partial-pivot search for column <paramref name="k"/>: one block per system (grid index =
    /// system), a shared-memory argmax reduction over rows [k,n) of |aug[s][i,k]|. Writes the pivot row into
    /// <c>piv[s]</c>; sets <c>info[s]=k+1</c> the first time system s's pivot falls to/below its floor.
    /// </summary>
    private static void PivotBatchKernel(
        ArrayView<double> aug, ArrayView<int> piv, ArrayView<int> info, ArrayView<double> floors, int k, int n, int ncol)
    {
        var sVal = SharedMemory.Allocate1D<double>(ReduceGroupSize);
        var sIdx = SharedMemory.Allocate1D<int>(ReduceGroupSize);

        int s = Grid.IdxX;              // system index (one block per system)
        int tid = Group.IdxX;
        int sBase = s * n * ncol;       // fits int: the host guards aug.Length <= int.MaxValue

        double best = -1.0;
        int bestIdx = k;
        for (int i = k + tid; i < n; i += Group.DimX)
        {
            double v = aug[sBase + i * ncol + k];
            if (v < 0.0) v = -v;
            if (v > best) { best = v; bestIdx = i; }
        }
        sVal[tid] = best;
        sIdx[tid] = bestIdx;
        Group.Barrier();

        for (int off = Group.DimX / 2; off > 0; off >>= 1)
        {
            if (tid < off && sVal[tid + off] > sVal[tid])
            {
                sVal[tid] = sVal[tid + off];
                sIdx[tid] = sIdx[tid + off];
            }
            Group.Barrier();
        }

        if (tid == 0)
        {
            piv[s] = sIdx[0];
            if (info[s] == 0 && sVal[0] <= floors[s]) info[s] = k + 1;   // first singular column only
        }
    }

    /// <summary>Swap rows k and piv[s] across all ncol columns of each system (no-op when equal). Grid over (system × col).</summary>
    private static void SwapBatchKernel(Index1D index, ArrayView<double> aug, ArrayView<int> piv, int k, int n, int ncol)
    {
        int idx = index.X;
        int s = idx / ncol;
        int j = idx % ncol;
        int p = piv[s];
        if (p == k) return;
        int sBase = s * n * ncol;
        int a = sBase + k * ncol + j;
        int b = sBase + p * ncol + j;
        double tmp = aug[a];
        aug[a] = aug[b];
        aug[b] = tmp;
    }

    /// <summary>For every row i below pivot k in each system: row_i -= (a[i,k]/a[k,k]) * row_k. Grid over (system × row).</summary>
    private static void EliminateBatchKernel(Index1D index, ArrayView<double> aug, int k, int n, int ncol)
    {
        int idx = index.X;
        int s = idx / n;
        int i = idx % n;
        if (i <= k || i >= n) return;

        int sBase = s * n * ncol;
        double pivot = aug[sBase + k * ncol + k];
        double factor = aug[sBase + i * ncol + k] / pivot;
        if (factor == 0.0) return;

        int rowI = sBase + i * ncol;
        int rowK = sBase + k * ncol;
        for (int j = k; j < ncol; j++)
            aug[rowI + j] -= factor * aug[rowK + j];
    }

    /// <summary>x[s,i] = aug[s][i,rhs] / aug[s][i,i], one thread per system.</summary>
    private static void BackDivideBatchKernel(Index1D index, ArrayView<double> aug, ArrayView<double> x, int i, int rhs, int n, int ncol)
    {
        int s = index.X;
        int sBase = s * n * ncol;
        x[s * n + i] = aug[sBase + i * ncol + rhs] / aug[sBase + i * ncol + i];
    }

    /// <summary>After x[s,i] is known, aug[s][r,rhs] -= aug[s][r,i]*x[s,i] for every earlier row r &lt; i. Grid over (system × row).</summary>
    private static void BackUpdateBatchKernel(Index1D index, ArrayView<double> aug, ArrayView<double> x, int i, int rhs, int n, int ncol)
    {
        int idx = index.X;
        int s = idx / n;
        int r = idx % n;
        if (r >= i) return;
        int sBase = s * n * ncol;
        aug[sBase + r * ncol + rhs] -= aug[sBase + r * ncol + i] * x[s * n + i];
    }
}
