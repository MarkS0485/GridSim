using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Gpu;

/// <summary>
/// AMD ROCm/HIP sparse solver for the CSR system <c>A x = b</c>: preconditioned BiCGSTAB
/// with a Jacobi (diagonal) preconditioner, the HIP counterpart of
/// <see cref="GpuSparseLinearSolver"/>.
///
/// Fully device-resident: the CSR matrix, the inverse-diagonal preconditioner and every Krylov
/// vector (r, rhat, p, v, s, t, ph, sh, x) live on the GPU for the whole iteration. The O(nnz)
/// sparse mat-vec runs via <c>rocsparse_dcsrmv</c>; the vector algebra runs via rocBLAS level-1
/// (<c>ddot</c> for dot-products/norms, <c>daxpy</c>/<c>dscal</c>/<c>dcopy</c> for the updates)
/// and the Jacobi apply is an on-device Hadamard product via <c>rocblas_ddgmm</c> (diag(invDiag) *
/// v). Only the convergence scalars (host-pointer-mode <c>ddot</c> results) and the final solution
/// cross the PCIe bus - no per-iteration vector round-trips.
///
/// Returns the solution, or <c>null</c> when the iteration breaks down, stagnates, or goes
/// non-finite - matching <see cref="ISparseLinearSolver"/> so the caller can fall back to a
/// direct CPU factorisation. If no ROCm device is present (as on this Windows build box) it
/// defers to <see cref="CpuSparseLuSolver"/>. The native P/Invoke signatures are matched against
/// the ROCm 6.x headers in <see cref="RocmInterop"/> and get their first end-to-end exercise via
/// the <c>gpucheck</c> preflight on an AMD Instinct (MI-series) card.
/// </summary>
public sealed class RocmSparseLinearSolver : ISparseLinearSolver
{
    public string Name => "sparse (gpu/rocm)";

    private readonly double _tol;

    public RocmSparseLinearSolver(double tol = NumericTolerances.DefaultKrylovTolerance)
    {
        // GR-472: the same guard as the CPU BiCGSTAB solvers.
        if (!(tol > 0.0) || !double.IsFinite(tol))
            throw new ArgumentOutOfRangeException(nameof(tol), tol, "The BiCGSTAB tolerance must be a positive finite number.");
        _tol = tol;
    }

    public double[]? Solve(CsrMatrix a, double[] b)
    {
        int n = a.N;
        if (b.Length != n) throw new ArgumentException("b length must equal A.N.");

        if (!RocmContext.IsAvailable)
            return new CpuSparseLuSolver().Solve(a, b);

        int nnz = a.Val.Length;
        int maxIter = NumericTolerances.DefaultKrylovMaxIterations(n);

        // Jacobi preconditioner: invDiag[i] = 1 / A[i,i]. Bail to CPU if a diagonal is missing/zero.
        var invDiag = new double[n];
        for (int i = 0; i < n; i++)
        {
            int dp = a.DiagPtr[i];
            double d = dp >= 0 ? a.Val[dp] : 0.0;
            if (d == 0.0 || double.IsNaN(d)) return new CpuSparseLuSolver().Solve(a, b);
            invDiag[i] = 1.0 / d;
        }

        nuint intRowBytes = (nuint)a.RowPtr.Length * sizeof(int);
        nuint intColBytes = (nuint)nnz * sizeof(int);
        nuint valBytes = (nuint)nnz * sizeof(double);
        nuint vecBytes = (nuint)n * sizeof(double);

        lock (RocmContext.Lock)
        {
            IntPtr hSp = IntPtr.Zero, descr = IntPtr.Zero, info = IntPtr.Zero, hBlas = IntPtr.Zero;
            IntPtr dRowPtr = IntPtr.Zero, dColIdx = IntPtr.Zero, dVal = IntPtr.Zero, dInvDiag = IntPtr.Zero;
            IntPtr dB = IntPtr.Zero, dX = IntPtr.Zero, dR = IntPtr.Zero, dRhat = IntPtr.Zero;
            IntPtr dP = IntPtr.Zero, dV = IntPtr.Zero, dS = IntPtr.Zero, dT = IntPtr.Zero;
            IntPtr dPh = IntPtr.Zero, dSh = IntPtr.Zero;

            try
            {
                Check(RocmInterop.rocsparse_create_handle(out hSp), "rocsparse_create_handle");
                Check(RocmInterop.rocsparse_create_mat_descr(out descr), "rocsparse_create_mat_descr");
                Check(RocmInterop.rocsparse_create_mat_info(out info), "rocsparse_create_mat_info");
                CheckBlas(RocmInterop.rocblas_create_handle(out hBlas), "rocblas_create_handle");

                CheckHip(RocmInterop.hipMalloc(out dRowPtr, intRowBytes), "hipMalloc(rowPtr)");
                CheckHip(RocmInterop.hipMalloc(out dColIdx, intColBytes), "hipMalloc(colIdx)");
                CheckHip(RocmInterop.hipMalloc(out dVal, valBytes), "hipMalloc(val)");
                CheckHip(RocmInterop.hipMalloc(out dInvDiag, vecBytes), "hipMalloc(invDiag)");
                CheckHip(RocmInterop.hipMalloc(out dB, vecBytes), "hipMalloc(b)");
                CheckHip(RocmInterop.hipMalloc(out dX, vecBytes), "hipMalloc(x)");
                CheckHip(RocmInterop.hipMalloc(out dR, vecBytes), "hipMalloc(r)");
                CheckHip(RocmInterop.hipMalloc(out dRhat, vecBytes), "hipMalloc(rhat)");
                CheckHip(RocmInterop.hipMalloc(out dP, vecBytes), "hipMalloc(p)");
                CheckHip(RocmInterop.hipMalloc(out dV, vecBytes), "hipMalloc(v)");
                CheckHip(RocmInterop.hipMalloc(out dS, vecBytes), "hipMalloc(s)");
                CheckHip(RocmInterop.hipMalloc(out dT, vecBytes), "hipMalloc(t)");
                CheckHip(RocmInterop.hipMalloc(out dPh, vecBytes), "hipMalloc(ph)");
                CheckHip(RocmInterop.hipMalloc(out dSh, vecBytes), "hipMalloc(sh)");

                // Upload the (immutable) CSR structure, the preconditioner and the RHS once.
                CheckHip(RocmInterop.hipMemcpy(dRowPtr, a.RowPtr, intRowBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D rowPtr");
                CheckHip(RocmInterop.hipMemcpy(dColIdx, a.ColIdx, intColBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D colIdx");
                CheckHip(RocmInterop.hipMemcpy(dVal, a.Val, valBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D val");
                CheckHip(RocmInterop.hipMemcpy(dInvDiag, invDiag, vecBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D invDiag");
                CheckHip(RocmInterop.hipMemcpy(dB, b, vecBytes, RocmInterop.HipMemcpyKind.HostToDevice), "H2D b");

                // One-time analysis so the info-driven adaptive csrmv can be used across every
                // iteration below. Required whenever a non-null info is passed to rocsparse_dcsrmv.
                Check(RocmInterop.rocsparse_dcsrmv_analysis(
                          hSp, RocmInterop.RocsparseOperation.None, n, n, nnz,
                          descr, dVal, dRowPtr, dColIdx, info),
                      "rocsparse_dcsrmv_analysis");

                // ---- device-resident level-1 helpers (rocBLAS, host pointer mode) ----
                double Dot(IntPtr x, IntPtr y)
                {
                    CheckBlas(RocmInterop.rocblas_ddot(hBlas, n, x, 1, y, 1, out double res), "rocblas_ddot");
                    return res;
                }
                double Norm(IntPtr x) => Math.Sqrt(Dot(x, x));
                void Axpy(double alpha, IntPtr x, IntPtr y) // y := alpha*x + y
                    => CheckBlas(RocmInterop.rocblas_daxpy(hBlas, n, ref alpha, x, 1, y, 1), "rocblas_daxpy");
                void Scal(double alpha, IntPtr x)           // x := alpha*x
                    => CheckBlas(RocmInterop.rocblas_dscal(hBlas, n, ref alpha, x, 1), "rocblas_dscal");
                void Copy(IntPtr x, IntPtr y)               // y := x
                    => CheckBlas(RocmInterop.rocblas_dcopy(hBlas, n, x, 1, y, 1), "rocblas_dcopy");
                // z := diag(invDiag) * v  (on-device Hadamard product, the Jacobi apply)
                void Jacobi(IntPtr v, IntPtr z)
                    => CheckBlas(RocmInterop.rocblas_ddgmm(hBlas, RocmInterop.RocblasSide.Left, n, 1, v, n, dInvDiag, 1, z, n), "rocblas_ddgmm");

                // y = A x on the device (rocsparse_dcsrmv computes y = alpha*A*x + beta*y).
                void SpMv(IntPtr x, IntPtr y)
                {
                    double alpha = 1.0, beta = 0.0;
                    Check(RocmInterop.rocsparse_dcsrmv(
                              hSp, RocmInterop.RocsparseOperation.None, n, n, nnz,
                              ref alpha, descr, dVal, dRowPtr, dColIdx, info, x, ref beta, y),
                          "rocsparse_dcsrmv");
                    CheckHip(RocmInterop.hipDeviceSynchronize(), "hipDeviceSynchronize");
                }

                // ---- BiCGSTAB, entirely on device ----
                // x0 = 0 ; r = b - A x0 = b ; rhat = r ; p = v = 0
                CheckHip(RocmInterop.hipMemset(dX, 0, vecBytes), "hipMemset(x)");
                Copy(dB, dR);
                Copy(dR, dRhat);
                CheckHip(RocmInterop.hipMemset(dP, 0, vecBytes), "hipMemset(p)");
                CheckHip(RocmInterop.hipMemset(dV, 0, vecBytes), "hipMemset(v)");

                double bnorm = Norm(dB);
                // GR-448: x = 0 only for an exactly zero b; a non-zero b whose norm underflows is left to the caller's CPU path.
                if (bnorm == 0.0) return Array.TrueForAll(b, bi => bi == 0.0) ? new double[n] : null;

                double rho = 1, alpha1 = 1, omega = 1;

                for (int it = 0; it < maxIter; it++)
                {
                    double rhoNew = Dot(dRhat, dR);
                    if (Math.Abs(rhoNew) < NumericTolerances.UnderflowGuard || double.IsNaN(rhoNew)) return null; // breakdown

                    double beta = (rhoNew / rho) * (alpha1 / omega);
                    // p = r + beta*(p - omega*v)
                    Axpy(-omega, dV, dP);   // p := p - omega*v
                    Scal(beta, dP);         // p := beta*p
                    Axpy(1.0, dR, dP);      // p := p + r

                    // ph = M^-1 p (Jacobi) ; v = A ph
                    Jacobi(dP, dPh);
                    SpMv(dPh, dV);

                    double rhatV = Dot(dRhat, dV);
                    if (rhatV == 0.0 || double.IsNaN(rhatV)) return null;
                    alpha1 = rhoNew / rhatV;

                    // s = r - alpha*v
                    Copy(dR, dS);
                    Axpy(-alpha1, dV, dS);

                    if (Norm(dS) / bnorm < _tol)
                    {
                        Axpy(alpha1, dPh, dX);   // x += alpha*ph
                        return TrueResidual.Verified(a, b, Finish(dX, n), _tol);
                    }

                    // sh = M^-1 s ; t = A sh
                    Jacobi(dS, dSh);
                    SpMv(dSh, dT);

                    double tt = Dot(dT, dT);
                    omega = tt > 0 ? Dot(dT, dS) / tt : 0.0;

                    // x += alpha*ph + omega*sh ; r = s - omega*t
                    Axpy(alpha1, dPh, dX);
                    Axpy(omega, dSh, dX);
                    Copy(dS, dR);
                    Axpy(-omega, dT, dR);
                    rho = rhoNew;

                    double rnorm = Norm(dR);
                    if (double.IsNaN(rnorm)) return null;
                    if (rnorm / bnorm < _tol) return TrueResidual.Verified(a, b, Finish(dX, n), _tol);
                    if (omega == 0.0) return null;
                }

                return null; // did not converge within the iteration cap
            }
            finally
            {
                // teardown: status intentionally ignored (freeing resources on cleanup)
                if (dSh != IntPtr.Zero) _ = RocmInterop.hipFree(dSh);
                if (dPh != IntPtr.Zero) _ = RocmInterop.hipFree(dPh);
                if (dT != IntPtr.Zero) _ = RocmInterop.hipFree(dT);
                if (dS != IntPtr.Zero) _ = RocmInterop.hipFree(dS);
                if (dV != IntPtr.Zero) _ = RocmInterop.hipFree(dV);
                if (dP != IntPtr.Zero) _ = RocmInterop.hipFree(dP);
                if (dRhat != IntPtr.Zero) _ = RocmInterop.hipFree(dRhat);
                if (dR != IntPtr.Zero) _ = RocmInterop.hipFree(dR);
                if (dX != IntPtr.Zero) _ = RocmInterop.hipFree(dX);
                if (dB != IntPtr.Zero) _ = RocmInterop.hipFree(dB);
                if (dInvDiag != IntPtr.Zero) _ = RocmInterop.hipFree(dInvDiag);
                if (dVal != IntPtr.Zero) _ = RocmInterop.hipFree(dVal);
                if (dColIdx != IntPtr.Zero) _ = RocmInterop.hipFree(dColIdx);
                if (dRowPtr != IntPtr.Zero) _ = RocmInterop.hipFree(dRowPtr);
                if (hBlas != IntPtr.Zero) _ = RocmInterop.rocblas_destroy_handle(hBlas);
                // Release csrmv analysis metadata before destroying the info handle.
                if (info != IntPtr.Zero) _ = RocmInterop.rocsparse_csrmv_clear(hSp, info);
                if (info != IntPtr.Zero) _ = RocmInterop.rocsparse_destroy_mat_info(info);
                if (descr != IntPtr.Zero) _ = RocmInterop.rocsparse_destroy_mat_descr(descr);
                if (hSp != IntPtr.Zero) _ = RocmInterop.rocsparse_destroy_handle(hSp);
            }
        }
    }

    /// <summary>Copy the device solution back to the host, returning null if it went non-finite.</summary>
    private static double[]? Finish(IntPtr dX, int n)
    {
        var x = new double[n];
        int st = RocmInterop.hipMemcpy(x, dX, (nuint)n * sizeof(double), RocmInterop.HipMemcpyKind.DeviceToHost);
        if (st != RocmInterop.HipSuccess) return null;
        for (int i = 0; i < n; i++)
            if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) return null; // caller falls back
        return x;
    }

    /// <summary>Throw if a rocSPARSE call returned a non-success status.</summary>
    private static void Check(int status, string what)
    {
        if (status != RocmInterop.RocSparseStatusSuccess)
            throw new InvalidOperationException($"ROCm sparse solve: {what} failed (status={status}).");
    }

    /// <summary>Throw if a rocBLAS call returned a non-success status.</summary>
    private static void CheckBlas(int status, string what)
    {
        if (status != RocmInterop.RocBlasStatusSuccess)
            throw new InvalidOperationException($"ROCm sparse solve: {what} failed (rocblas status={status}).");
    }

    /// <summary>Throw if a HIP runtime call returned a non-success status.</summary>
    private static void CheckHip(int status, string what)
    {
        if (status != RocmInterop.HipSuccess)
            throw new InvalidOperationException($"ROCm sparse solve: {what} failed (hipError={status}).");
    }
}
