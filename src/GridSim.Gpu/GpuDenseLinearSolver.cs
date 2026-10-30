using GridSim.Core.Abstractions;
using GridSim.Core.PowerFlow;
using ILGPU;
using ILGPU.Runtime;

namespace GridSim.Gpu;

/// <summary>
/// GPU dense linear solver for <c>A x = b</c>: Gaussian elimination with partial pivoting,
/// run as ILGPU kernels over an augmented row-major matrix <c>[A | b]</c>.
///
/// The whole factorisation is queued onto the accelerator's default stream and left to run
/// asynchronously; kernels on one stream execute in submission order, so no host
/// synchronisation is needed <em>between</em> steps. This is the key difference from the old
/// implementation, which round-tripped to the host three times per column (gather the pivot
/// column, copy it back, pick the pivot, swap, eliminate - each separated by a blocking
/// <c>Synchronize</c>). Here the entire forward elimination is submitted without a single host
/// stall and we synchronise exactly twice: once after forward elimination (to read the
/// singularity flag) and once after back-substitution (to read the solution).
///
/// Partial pivoting is done <em>on the device</em>: a one-block shared-memory argmax reduction
/// finds the pivot row and writes its index straight into device memory, where the swap kernel
/// consumes it - the pivot index never touches the host. A device-side flag records the first
/// singular column so the CPU-visible contract (throw on a singular matrix) is preserved.
///
/// The same kernels run on either ILGPU accelerator; the target is chosen by the ctor argument
/// (<see cref="AcceleratorType.Cuda"/> by default, or <see cref="AcceleratorType.OpenCL"/> for the
/// portable AMD/Intel path). If no device of that type is present the solve transparently defers to
/// the CPU <see cref="DenseLuSolver"/>, so this type is always safe to construct and call.
/// </summary>
public sealed class GpuDenseLinearSolver : ILinearSolver
{
    private readonly AcceleratorType _accel;

    /// <param name="accel">Which ILGPU accelerator to target (CUDA by default, or OpenCL).</param>
    public GpuDenseLinearSolver(AcceleratorType accel = AcceleratorType.Cuda) => _accel = accel;

    public string Name => $"dense-lu (gpu/{(_accel == AcceleratorType.OpenCL ? "opencl" : "ilgpu")})";

    /// <summary>Threads per block for the on-device pivot reduction. Power of two, <= 1024.</summary>
    private const int ReduceGroupSize = 256;

    public void SolveInPlace(double[,] a, double[] b)
    {
        int n = b.Length;
        if (a.GetLength(0) != n || a.GetLength(1) != n)
            throw new ArgumentException("A must be square and match b's length.");

        if (!GpuContext.IsAvailableFor(_accel))
        {
            new DenseLuSolver().SolveInPlace(a, b);
            return;
        }

        var acc = GpuContext.AcceleratorFor(_accel)!;
        int ncol = n + 1;             // augmented: last column holds b
        int rhs = n;                  // column index of the RHS

        // Flatten A and b into a row-major augmented matrix [A | b].
        var aug = new double[n * ncol];
        double aNorm = 0.0;   // matrix scale for the relative near-singular floor (mirrors DenseLuSolver)
        for (int i = 0; i < n; i++)
        {
            int baseRow = i * ncol;
            for (int j = 0; j < n; j++)
            {
                double v = a[i, j];
                // Bug #40: a NaN entry is refused, as the CPU dense LU refuses it (it never wins the max or fails the
                // pivot test, so it would be factored into a NaN solution).
                if (double.IsNaN(v))
                    throw new InvalidOperationException("GPU LU solve: the matrix contains a NaN entry.");
                aug[baseRow + j] = v;
                double av = Math.Abs(v);
                if (av > aNorm) aNorm = av;
            }
            aug[baseRow + rhs] = b[i];
        }
        double pivotFloor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(aNorm);   // the shared relative floor (WS5)
        // Bug #40: a NaN or infinite right-hand side is refused, as the CPU dense LU refuses it (it used to come back as a
        // NaN / Inf solution). Read-only pre-checks: on finite input the device arithmetic is unchanged. Without a device
        // the CPU dense LU above makes the same checks.
        int badRow = GridSim.Core.Numerics.NumericTolerances.FirstNonFinite(b);
        if (badRow >= 0)
            throw new InvalidOperationException(GridSim.Core.Numerics.NumericTolerances.NonFiniteRightHandSide("GPU LU solve", b[badRow], badRow));

        lock (GpuContext.Lock)
        {
            using var dAug = acc.Allocate1D<double>(aug.Length);
            using var dPiv = acc.Allocate1D<int>(1);      // pivot row for the current column
            using var dInfo = acc.Allocate1D<int>(1);     // 0 = ok; else (firstSingularCol + 1)
            using var dX = acc.Allocate1D<double>(n);     // solution
            dAug.CopyFromCPU(aug);
            dInfo.CopyFromCPU(new int[1]);                // clear the singularity flag

            var pivot = acc.LoadStreamKernel<ArrayView<double>, ArrayView<int>, ArrayView<int>, int, int, int, double>(PivotKernel);
            var swap = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, int, int>(SwapRowsKernel);
            var eliminate = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, int, int, int>(EliminateKernel);
            var divide = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, int, int, int>(BackDivideKernel);
            var backUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, int, int, int>(BackUpdateKernel);

            var pivotConfig = new KernelConfig(1, ReduceGroupSize);

            // ---- Forward elimination with partial pivoting (all queued, no host stalls) ----
            for (int k = 0; k < n; k++)
            {
                // Pick the pivot row on-device (argmax of |A[k..,k]|) -> dPiv[0].
                pivot(pivotConfig, dAug.View, dPiv.View, dInfo.View, k, n, ncol, pivotFloor);
                // Swap the pivot row into place across all columns.
                swap(ncol, dAug.View, dPiv.View, k, ncol);
                // Eliminate every row below the pivot in one launch (the trailing update).
                eliminate(n, dAug.View, k, n, ncol);
            }

            acc.Synchronize();
            var infoHost = new int[1];
            dInfo.CopyToCPU(infoHost);
            if (infoHost[0] != 0)
                throw new InvalidOperationException(
                    $"Near-singular matrix in GPU LU solve: pivot at or below the relative floor {pivotFloor:E3} "
                    + $"(matrix scale {aNorm:E3}) at column {infoHost[0] - 1}.");

            // ---- Back substitution (column sweep, queued, no per-row host stalls) ----
            for (int i = n - 1; i >= 0; i--)
            {
                divide(1, dAug.View, dX.View, i, rhs, ncol);
                if (i > 0)
                    backUpd(i, dAug.View, dX.View, i, rhs, ncol);
            }

            acc.Synchronize();
            dX.CopyToCPU(b);
        }
    }

    /// <summary>
    /// On-device partial-pivot search for column <paramref name="k"/>: a single-block
    /// shared-memory reduction computing argmax of |aug[i,k]| over rows i in [k, n).
    /// Writes the pivot row into <c>piv[0]</c>; if the largest magnitude is zero the column is
    /// singular and <c>info[0]</c> is set to (k+1) (only the first failing column matters).
    /// </summary>
    private static void PivotKernel(ArrayView<double> aug, ArrayView<int> piv, ArrayView<int> info, int k, int n, int ncol, double pivotFloor)
    {
        var sVal = SharedMemory.Allocate1D<double>(ReduceGroupSize);
        var sIdx = SharedMemory.Allocate1D<int>(ReduceGroupSize);

        int tid = Group.IdxX;
        double best = -1.0;
        int bestIdx = k;
        for (int i = k + tid; i < n; i += Group.DimX)
        {
            double v = aug[i * ncol + k];
            if (v < 0.0) v = -v;
            if (v > best) { best = v; bestIdx = i; }
        }
        sVal[tid] = best;
        sIdx[tid] = bestIdx;
        Group.Barrier();

        // Tree reduction; ReduceGroupSize is a power of two so this halves cleanly.
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
            piv[0] = sIdx[0];
            // Relative near-singular floor, mirroring the CPU DenseLuSolver:
            // an exact-zero test alone accepted ill-conditioned pivots and
            // returned garbage as success (exact zero = the aNorm==0 subcase).
            if (sVal[0] <= pivotFloor) info[0] = k + 1;
        }
    }

    /// <summary>Swap rows k and piv[0] across all ncol columns (no-op when they are equal).</summary>
    private static void SwapRowsKernel(Index1D index, ArrayView<double> aug, ArrayView<int> piv, int k, int ncol)
    {
        int p = piv[0];
        if (p == k) return;
        int j = index.X;
        int a = k * ncol + j;
        int b = p * ncol + j;
        double tmp = aug[a];
        aug[a] = aug[b];
        aug[b] = tmp;
    }

    /// <summary>
    /// For every row i below pivot k: row_i -= (a[i,k]/a[k,k]) * row_k, over columns k..ncol-1.
    /// One thread per candidate row; rows at or above k are no-ops. This is the O(n^2) trailing
    /// update for the column, issued as a single launch.
    /// </summary>
    private static void EliminateKernel(Index1D index, ArrayView<double> aug, int k, int n, int ncol)
    {
        int i = index.X;
        if (i <= k || i >= n) return;

        double pivot = aug[k * ncol + k];
        double factor = aug[i * ncol + k] / pivot;
        if (factor == 0.0) return;

        int rowI = i * ncol;
        int rowK = k * ncol;
        for (int j = k; j < ncol; j++)
            aug[rowI + j] -= factor * aug[rowK + j];
    }

    /// <summary>x[i] = aug[i, rhs] / aug[i, i]. Launched with a single thread.</summary>
    private static void BackDivideKernel(Index1D index, ArrayView<double> aug, ArrayView<double> x, int i, int rhs, int ncol)
    {
        x[i] = aug[i * ncol + rhs] / aug[i * ncol + i];
    }

    /// <summary>
    /// After x[i] is known, eliminate column i from the RHS of every earlier row r &lt; i:
    /// aug[r, rhs] -= aug[r, i] * x[i]. One thread per row r in 0..i-1.
    /// </summary>
    private static void BackUpdateKernel(Index1D index, ArrayView<double> aug, ArrayView<double> x, int i, int rhs, int ncol)
    {
        int r = index.X;
        if (r >= i) return;
        aug[r * ncol + rhs] -= aug[r * ncol + i] * x[i];
    }
}
