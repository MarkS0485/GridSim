using GridSim.Core.Abstractions;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using ILGPU;
using ILGPU.Runtime;

namespace GridSim.Gpu;

/// <summary>
/// GPU sparse solver for the CSR system <c>A x = b</c>: preconditioned BiCGSTAB with a
/// Jacobi (diagonal) preconditioner, built from ILGPU kernels for CSR SpMV, the vector
/// updates, and the dot-products.
///
/// Each dot-product is a single-block shared-memory reduction that returns just the scalar
/// result to the host (one double), replacing the old "elementwise-product kernel + copy the
/// whole vector back + sum on the CPU" round-trip. The per-iteration vector updates are queued
/// on the default stream and left to run in submission order - no host synchronisation between
/// them - so the host only stalls where BiCGSTAB genuinely needs a scalar (the dot-products
/// that drive alpha/beta/omega and the convergence tests).
///
/// Returns the solution, or <c>null</c> when the iteration breaks down, stagnates, or goes
/// non-finite - matching <see cref="ISparseLinearSolver"/> so the caller can fall back to a
/// direct CPU factorisation. The kernels run on either ILGPU accelerator; the target is chosen by
/// the ctor (<see cref="AcceleratorType.Cuda"/> by default, or <see cref="AcceleratorType.OpenCL"/>).
/// If no device of that type is present it defers to <see cref="CpuSparseLuSolver"/>.
/// </summary>
public sealed class GpuSparseLinearSolver : ISparseLinearSolver
{
    private readonly AcceleratorType _accel;

    public string Name => $"sparse-bicgstab (gpu/{(_accel == AcceleratorType.OpenCL ? "opencl" : "ilgpu")})";

    /// <summary>Threads per block for the on-device dot-product reduction. Power of two, <= 1024.</summary>
    private const int ReduceGroupSize = 256;

    private readonly double _tol;

    /// <param name="tol">Relative residual tolerance for BiCGSTAB convergence.</param>
    /// <param name="accel">Which ILGPU accelerator to target (CUDA by default, or OpenCL).</param>
    public GpuSparseLinearSolver(double tol = NumericTolerances.DefaultKrylovTolerance, AcceleratorType accel = AcceleratorType.Cuda)
    {
        // GR-472: the same guard as the CPU BiCGSTAB solvers: a tolerance that is not positive and finite is refused.
        if (!(tol > 0.0) || !double.IsFinite(tol))
            throw new ArgumentOutOfRangeException(nameof(tol), tol, "The BiCGSTAB tolerance must be a positive finite number.");
        _tol = tol;
        _accel = accel;
    }

    public double[]? Solve(CsrMatrix a, double[] b)
    {
        int n = a.N;
        if (b.Length != n) throw new ArgumentException("b length must equal A.N.");

        if (!GpuContext.IsAvailableFor(_accel))
            return new CpuSparseLuSolver().Solve(a, b);

        // Bug #40: a NaN or infinite right-hand side is refused, as the CPU sparse LU refuses it, instead of returning null
        // (which sent it to a fallback that could only give a non-finite answer too). A read-only pre-check: on finite
        // input the device arithmetic is unchanged. Without a device the CPU sparse LU above makes the same check.
        int badRow = NumericTolerances.FirstNonFinite(b);
        if (badRow >= 0)
            throw new ArgumentException(NumericTolerances.NonFiniteRightHandSide("GPU sparse solve", b[badRow], badRow), nameof(b));

        var acc = GpuContext.AcceleratorFor(_accel)!;
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

        lock (GpuContext.Lock)
        {
            using var dRowPtr = acc.Allocate1D<int>(a.RowPtr.Length);
            using var dColIdx = acc.Allocate1D<int>(a.ColIdx.Length);
            using var dVal = acc.Allocate1D<double>(a.Val.Length);
            using var dInvDiag = acc.Allocate1D<double>(n);
            using var dB = acc.Allocate1D<double>(n);
            using var dX = acc.Allocate1D<double>(n);
            using var dR = acc.Allocate1D<double>(n);
            using var dRhat = acc.Allocate1D<double>(n);
            using var dP = acc.Allocate1D<double>(n);
            using var dV = acc.Allocate1D<double>(n);
            using var dS = acc.Allocate1D<double>(n);
            using var dT = acc.Allocate1D<double>(n);
            using var dPh = acc.Allocate1D<double>(n);
            using var dSh = acc.Allocate1D<double>(n);
            using var dDot = acc.Allocate1D<double>(1);   // single-scalar dot-product result

            dRowPtr.CopyFromCPU(a.RowPtr);
            dColIdx.CopyFromCPU(a.ColIdx);
            dVal.CopyFromCPU(a.Val);
            dInvDiag.CopyFromCPU(invDiag);
            dB.CopyFromCPU(b);

            var spmv = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<double>>(SpMvKernel);
            var jacobi = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>>(JacobiKernel);
            var dot = acc.LoadStreamKernel<ArrayView<double>, ArrayView<double>, ArrayView<double>, int>(DotReduceKernel);
            var copy = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>>(CopyKernel);
            var clear = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(ClearKernel);
            var pUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, double, double>(PUpdateKernel);
            var sUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, double>(SUpdateKernel);
            var xUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, double, double>(XUpdateKernel);
            var rUpd = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, double>(RUpdateKernel);
            var axpy = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, double>(AxpyKernel);

            var dotHost = new double[1];
            var dotConfig = new KernelConfig(1, ReduceGroupSize);

            // Dot product a*b via a single-block on-device reduction; only the scalar comes back.
            // The reduction kernel is queued on the default stream after any pending vector
            // updates, so ordering guarantees it sees their results without an extra sync.
            double Dot(ArrayView<double> va, ArrayView<double> vb)
            {
                dot(dotConfig, va, vb, dDot.View, n);
                acc.Synchronize();
                dDot.CopyToCPU(dotHost);
                return dotHost[0];
            }

            double Norm(ArrayView<double> v) => Math.Sqrt(Dot(v, v));

            // x = 0 ; r = b - A x = b ; rhat = r
            clear(n, dX.View);
            copy(n, dB.View, dR.View);
            copy(n, dR.View, dRhat.View);
            clear(n, dP.View);
            clear(n, dV.View);

            double bnorm = Norm(dB.View);
            // A zero right side is decided by an exact scan of b, not by the norm (which underflows to 0 for a non-zero
            // b whose entries are all below about 1e-162). And the caller's b is never written: the old branch copied
            // the device x (zeros) INTO b, so the CPU retry that follows a GPU fallback then solved the wrong system.
            if (Array.TrueForAll(b, v => v == 0.0)) return new double[n];
            if (bnorm == 0.0) return null;   // non-zero but underflowing: let the caller's exact CPU path take it

            double rho = 1, alpha = 1, omega = 1;

            for (int it = 0; it < maxIter; it++)
            {
                double rhoNew = Dot(dRhat.View, dR.View);
                if (Math.Abs(rhoNew) < NumericTolerances.UnderflowGuard || double.IsNaN(rhoNew)) return null; // breakdown

                double beta = (rhoNew / rho) * (alpha / omega);
                // p = r + beta*(p - omega*v)
                pUpd(n, dP.View, dR.View, dV.View, beta, omega);

                // ph = M^-1 p  (Jacobi) ; v = A ph
                jacobi(n, dP.View, dPh.View, dInvDiag.View);
                spmv(n, dRowPtr.View, dColIdx.View, dVal.View, dPh.View, dV.View);

                double rhatV = Dot(dRhat.View, dV.View);
                if (rhatV == 0.0 || double.IsNaN(rhatV)) return null;
                alpha = rhoNew / rhatV;

                // s = r - alpha*v
                sUpd(n, dS.View, dR.View, dV.View, alpha);

                if (Norm(dS.View) / bnorm < _tol)
                {
                    // x += alpha*ph
                    axpy(n, dX.View, dPh.View, alpha);
                    return TrueResidual.Verified(a, b, Finish(dX, b, n), _tol);
                }

                // sh = M^-1 s ; t = A sh
                jacobi(n, dS.View, dSh.View, dInvDiag.View);
                spmv(n, dRowPtr.View, dColIdx.View, dVal.View, dSh.View, dT.View);

                double tt = Dot(dT.View, dT.View);
                omega = tt > 0 ? Dot(dT.View, dS.View) / tt : 0.0;

                // x += alpha*ph + omega*sh ; r = s - omega*t
                xUpd(n, dX.View, dPh.View, dSh.View, alpha, omega);
                rUpd(n, dR.View, dS.View, dT.View, omega);
                rho = rhoNew;

                double rnorm = Norm(dR.View);
                if (double.IsNaN(rnorm)) return null;
                if (rnorm / bnorm < _tol) return TrueResidual.Verified(a, b, Finish(dX, b, n), _tol);
                if (omega == 0.0) return null;
            }

            return null; // did not converge within the iteration cap
        }
    }

    private static double[] Finish(MemoryBuffer1D<double, Stride1D.Dense> dX, double[] b, int n)
    {
        var x = new double[n];
        dX.CopyToCPU(x);
        for (int i = 0; i < n; i++)
            if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) return null!; // caller treats null as fallback
        return x;
    }

    // ---- Kernels ----

    /// <summary>y = A x for CSR A (one thread per row).</summary>
    private static void SpMvKernel(Index1D index, ArrayView<int> rowPtr, ArrayView<int> colIdx, ArrayView<double> val, ArrayView<double> x, ArrayView<double> y)
    {
        int i = index.X;
        double s = 0;
        int end = rowPtr[i + 1];
        for (int k = rowPtr[i]; k < end; k++)
            s += val[k] * x[colIdx[k]];
        y[i] = s;
    }

    /// <summary>z = M^-1 r with Jacobi M^-1 = diag(invDiag).</summary>
    private static void JacobiKernel(Index1D index, ArrayView<double> r, ArrayView<double> z, ArrayView<double> invDiag)
    {
        int i = index.X;
        z[i] = r[i] * invDiag[i];
    }

    /// <summary>
    /// Single-block dot product: result[0] = sum a[i]*b[i]. One group of <see cref="ReduceGroupSize"/>
    /// threads strides over the vectors accumulating a partial sum, then a shared-memory tree
    /// reduction folds the partials to a scalar. Only that scalar is copied back to the host.
    /// </summary>
    private static void DotReduceKernel(ArrayView<double> a, ArrayView<double> b, ArrayView<double> result, int n)
    {
        var s = SharedMemory.Allocate1D<double>(ReduceGroupSize);

        int tid = Group.IdxX;
        double local = 0.0;
        for (int i = tid; i < n; i += Group.DimX)
            local += a[i] * b[i];
        s[tid] = local;
        Group.Barrier();

        // Tree reduction; ReduceGroupSize is a power of two so this halves cleanly.
        for (int off = Group.DimX / 2; off > 0; off >>= 1)
        {
            if (tid < off) s[tid] += s[tid + off];
            Group.Barrier();
        }

        if (tid == 0) result[0] = s[0];
    }

    private static void CopyKernel(Index1D index, ArrayView<double> src, ArrayView<double> dst)
    {
        int i = index.X;
        dst[i] = src[i];
    }

    private static void ClearKernel(Index1D index, ArrayView<double> v)
    {
        v[index.X] = 0.0;
    }

    /// <summary>p = r + beta*(p - omega*v).</summary>
    private static void PUpdateKernel(Index1D index, ArrayView<double> p, ArrayView<double> r, ArrayView<double> v, double beta, double omega)
    {
        int i = index.X;
        p[i] = r[i] + beta * (p[i] - omega * v[i]);
    }

    /// <summary>s = r - alpha*v.</summary>
    private static void SUpdateKernel(Index1D index, ArrayView<double> s, ArrayView<double> r, ArrayView<double> v, double alpha)
    {
        int i = index.X;
        s[i] = r[i] - alpha * v[i];
    }

    /// <summary>x += alpha*ph + omega*sh.</summary>
    private static void XUpdateKernel(Index1D index, ArrayView<double> x, ArrayView<double> ph, ArrayView<double> sh, double alpha, double omega)
    {
        int i = index.X;
        x[i] += alpha * ph[i] + omega * sh[i];
    }

    /// <summary>r = s - omega*t.</summary>
    private static void RUpdateKernel(Index1D index, ArrayView<double> r, ArrayView<double> s, ArrayView<double> t, double omega)
    {
        int i = index.X;
        r[i] = s[i] - omega * t[i];
    }

    /// <summary>y += alpha*x.</summary>
    private static void AxpyKernel(Index1D index, ArrayView<double> y, ArrayView<double> x, double alpha)
    {
        int i = index.X;
        y[i] += alpha * x[i];
    }
}

// TODO: cuBLAS Dgemm for trailing update - an ILGPU.Algorithms cuBLAS-backed dense GEMM
// could replace the row-parallel elimination in GpuDenseLinearSolver's trailing update.
// Left out deliberately to keep the build free of the cuBLAS native dependency.
