using System.Runtime.InteropServices;
using GridSim.Core.Abstractions;
using GridSim.Core.PowerFlow;

namespace GridSim.Gpu;

/// <summary>
/// AMD ROCm/HIP dense linear solver for <c>A x = b</c>: an LU factorisation with partial
/// pivoting run on the GPU via hipSOLVER (getrf + getrs), the HIP counterpart of
/// <see cref="GpuDenseLinearSolver"/> (which uses ILGPU/CUDA).
///
/// If no ROCm device is present (as on this Windows build box) the solve transparently
/// defers to the CPU <see cref="DenseLuSolver"/>, so this type is always safe to construct
/// and call. The GPU path below runs on real ROCm hardware using the native hipSOLVER API
/// (hipsolverDgetrf/Dgetrs with explicit workspace); its P/Invoke signatures are matched
/// against the ROCm 6.x headers in <see cref="RocmInterop"/> and get their first end-to-end
/// exercise via the <c>gpucheck</c> preflight on an AMD Instinct (MI-series) card.
/// </summary>
public sealed class RocmDenseLinearSolver : ILinearSolver
{
    public string Name => "dense-lu (gpu/rocm)";

    public void SolveInPlace(double[,] a, double[] b)
    {
        int n = b.Length;
        if (a.GetLength(0) != n || a.GetLength(1) != n)
            throw new ArgumentException("A must be square and match b's length.");

        if (!RocmContext.IsAvailable)
        {
            // No ROCm on this machine - use the CPU direct solver.
            new DenseLuSolver().SolveInPlace(a, b);
            return;
        }

        // ---- Column-major flatten for LAPACK/hipSOLVER --------------------------------
        // C# double[,] is ROW-major: a[i, j]. hipSOLVER (like LAPACK) expects COLUMN-major
        // with leading dimension lda = n, i.e. element (i, j) lives at index j*n + i.
        // We therefore transpose-pack A into a flat column-major buffer. For the RHS we
        // pass b as a single column (nrhs = 1, ldb = n); getrs overwrites it with x, and a
        // column vector is identical in row- and column-major layout.
        var aCol = new double[n * n];
        double aNorm = 0.0;   // matrix scale for the relative near-singular floor (mirrors DenseLuSolver)
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double v = a[i, j];
                aCol[j * n + i] = v;
                if (double.IsNaN(v))
                    throw new ArgumentException("ROCm dense solve: the matrix contains a NaN entry.", nameof(a));
                double av = Math.Abs(v);
                if (av > aNorm) aNorm = av;
            }

        const int nrhs = 1;
        nuint dblBytes = (nuint)n * (nuint)n * sizeof(double);
        nuint vecBytes = (nuint)n * sizeof(double);
        nuint pivBytes = (nuint)n * sizeof(int);
        nuint infoBytes = sizeof(int);

        lock (RocmContext.Lock)
        {
            IntPtr handle = IntPtr.Zero;
            IntPtr dA = IntPtr.Zero, dB = IntPtr.Zero, dWork = IntPtr.Zero;
            IntPtr dIpiv = IntPtr.Zero, dInfo = IntPtr.Zero;

            try
            {
                Check(RocmInterop.hipsolverCreate(out handle), "hipsolverCreate");

                Check(RocmInterop.hipMalloc(out dA, dblBytes), "hipMalloc(A)");
                Check(RocmInterop.hipMalloc(out dB, vecBytes), "hipMalloc(b)");
                Check(RocmInterop.hipMalloc(out dIpiv, pivBytes), "hipMalloc(ipiv)");
                Check(RocmInterop.hipMalloc(out dInfo, infoBytes), "hipMalloc(info)");

                // Upload A (column-major) and b.
                CheckHip(RocmInterop.hipMemcpy(dA, aCol, dblBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D A");
                CheckHip(RocmInterop.hipMemcpy(dB, b, vecBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D b");

                // Query the workspace for BOTH getrf and getrs and allocate the larger - the
                // hipSOLVER-Dn API carries a separate explicit lwork for each and their sizes can
                // differ, so we must not assume the getrf buffer suffices for getrs.
                Check(RocmInterop.hipsolverDgetrf_bufferSize(handle, n, n, dA, n, out int lworkF),
                      "hipsolverDgetrf_bufferSize");
                Check(RocmInterop.hipsolverDgetrs_bufferSize(handle, RocmInterop.HipsolverOperation.None,
                          n, nrhs, dA, n, dIpiv, dB, n, out int lworkS),
                      "hipsolverDgetrs_bufferSize");
                int lwork = Math.Max(lworkF, lworkS);

                // Allocate lwork*sizeof(double) bytes. hipSOLVER reports lwork as an element count of
                // the compute type, so this is the exact requirement; the value passed back to
                // getrf/getrs is verbatim what the bufferSize query returned.
                if (lwork > 0)
                    Check(RocmInterop.hipMalloc(out dWork, (nuint)lwork * sizeof(double)), "hipMalloc(work)");

                // LU factorise A in place: A = P L U. devIpiv gets the 1-based pivots.
                Check(RocmInterop.hipsolverDgetrf(handle, n, n, dA, n, dWork, lwork, dIpiv, dInfo),
                      "hipsolverDgetrf");
                ThrowIfSingular(dInfo, "getrf");

                // Near-singular test (R049/R301): hipSOLVER's devInfo only flags an EXACTLY zero U(i,i), so a
                // rounding-level pivot used to pass and getrs returned a huge garbage step as success while the
                // CPU and ILGPU solvers threw. Copy the factored matrix back and apply the same relative floor
                // they apply during elimination (with partial pivoting their k-th pivot IS |U(k,k)|).
                CheckHip(RocmInterop.hipDeviceSynchronize(), "hipDeviceSynchronize (getrf)");
                var luCol = new double[n * n];
                CheckHip(RocmInterop.hipMemcpy(luCol, dA, dblBytes, RocmInterop.HipMemcpyKind.DeviceToHost), "D2H LU");
                ThrowIfNearSingular(luCol, n, aNorm);

                // Solve A x = b (no transpose): overwrites dB with the solution x.
                Check(RocmInterop.hipsolverDgetrs(handle, RocmInterop.HipsolverOperation.None,
                          n, nrhs, dA, n, dIpiv, dB, n, dWork, lwork, dInfo),
                      "hipsolverDgetrs");
                ThrowIfSingular(dInfo, "getrs");

                CheckHip(RocmInterop.hipDeviceSynchronize(), "hipDeviceSynchronize");

                // Copy the solution back into b.
                CheckHip(RocmInterop.hipMemcpy(b, dB, vecBytes, RocmInterop.HipMemcpyKind.DeviceToHost), "D2H x");
            }
            finally
            {
                // teardown: status intentionally ignored (freeing resources on cleanup)
                if (dWork != IntPtr.Zero) _ = RocmInterop.hipFree(dWork);
                if (dInfo != IntPtr.Zero) _ = RocmInterop.hipFree(dInfo);
                if (dIpiv != IntPtr.Zero) _ = RocmInterop.hipFree(dIpiv);
                if (dB != IntPtr.Zero) _ = RocmInterop.hipFree(dB);
                if (dA != IntPtr.Zero) _ = RocmInterop.hipFree(dA);
                if (handle != IntPtr.Zero) _ = RocmInterop.hipsolverDestroy(handle);
            }
        }
    }

    /// <summary>Throw if a hipSOLVER call returned a non-success status.</summary>
    private static void Check(int status, string what)
    {
        if (status != RocmInterop.HipSolverStatusSuccess && status != RocmInterop.HipSuccess)
            throw new InvalidOperationException($"ROCm dense solve: {what} failed (status={status}).");
    }

    /// <summary>Throw if a HIP runtime call returned a non-success status.</summary>
    private static void CheckHip(int status, string what)
    {
        if (status != RocmInterop.HipSuccess)
            throw new InvalidOperationException($"ROCm dense solve: {what} failed (hipError={status}).");
    }

    /// <summary>
    /// hipSOLVER writes a factorisation/solve diagnostic into devInfo: 0 = success,
    /// &gt; 0 = U(i,i) exactly zero (singular), &lt; 0 = bad argument. Mirror the CPU
    /// solver by treating a singular matrix as an exception.
    /// </summary>
    private static void ThrowIfSingular(IntPtr dInfo, string stage)
    {
        var info = new int[1];
        int status = RocmInterop.hipMemcpy(info, dInfo, sizeof(int), RocmInterop.HipMemcpyKind.DeviceToHost);
        // Fail loud: if devInfo cannot be read, the factorisation's success is unknown, and an unknown result
        // must never be returned as a solution (this used to return silently as "best-effort").
        if (status != RocmInterop.HipSuccess)
            throw new InvalidOperationException(
                $"ROCm dense solve: could not read the {stage} devInfo diagnostic (hipError={status}), so the factorisation cannot be trusted.");
        if (info[0] != 0)
            throw new InvalidOperationException(
                $"Singular matrix in ROCm dense LU solve ({stage} devInfo={info[0]}).");
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> when any pivot U(k,k) of the column-major LU factors
    /// <paramref name="luColumnMajor"/> is at or below the shared relative floor
    /// (<see cref="GridSim.Core.Numerics.NumericTolerances.RelativePivotFloor"/> times <paramref name="matrixScale"/>,
    /// the largest |A_ij| of the ORIGINAL matrix), exactly the matrices the CPU <see cref="DenseLuSolver"/> and
    /// the ILGPU <see cref="GpuDenseLinearSolver"/> reject. Public so the host-side test runs without AMD hardware.
    /// <para>What is and is not proved (D-DOC-210, October 2026). Proved on the CPU: given the factors of the textbook
    /// partial-pivoting LU (first largest pivot, separate multiply and subtract), this check rejects exactly the
    /// matrices <see cref="DenseLuSolver"/> rejects, on a randomised set of near-singular and well-conditioned
    /// matrices of order 1 to 12 and at the exact floor boundary (Doc16NumericsDeterminismTests), not only the one
    /// 3 x 3 it rested on before. NOT proved, because no AMD device is available: that hipSOLVER's getrf (a blocked,
    /// device-ordered factorisation) produces pivots close enough to the textbook ones that a matrix near the floor
    /// gets the same verdict on the device. A pivot within rounding of the floor can be judged differently there.</para>
    /// </summary>
    public static void ThrowIfNearSingular(double[] luColumnMajor, int n, double matrixScale)
    {
        int k = GridSim.Core.Numerics.NumericTolerances.FirstSingularPivot(luColumnMajor, n, matrixScale);
        if (k < 0) return;
        double floor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(matrixScale);
        double pivot = Math.Abs(luColumnMajor[k * n + k]);
        throw new InvalidOperationException(
            $"Near-singular matrix in ROCm dense LU solve: pivot {pivot:E3} at column {k} is at or below the tolerance {floor:E3} (matrix scale {matrixScale:E3}).");
    }
}
