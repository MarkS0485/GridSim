using System.Runtime.InteropServices;

namespace GridSim.Gpu;

/// <summary>
/// P/Invoke declarations for the AMD ROCm math stack used by the ROCm/HIP GPU backend:
/// the HIP runtime (device query + allocation + memcpy), hipSOLVER (dense LU: getrf/getrs),
/// rocSPARSE (CSR sparse mat-vec) and rocBLAS (level-1 vector algebra for the iterative solve).
///
/// These bind to the ROCm shared libraries by their canonical UNIX-style base names:
///   - HIP runtime  -> "amdhip64"   (libamdhip64.so on Linux; amdhip64.dll on Windows ROCm)
///   - hipSOLVER    -> "hipsolver"  (libhipsolver.so)
///   - rocSPARSE    -> "rocsparse"  (librocsparse.so)
///   - rocBLAS      -> "rocblas"    (librocblas.so)
///
/// IMPORTANT: nothing here loads a native library at type-load or JIT time. A
/// <c>[DllImport]</c> method only resolves its library on the FIRST CALL. That is why this
/// assembly compiles and loads cleanly on a machine with no ROCm installed (this Windows
/// laptop): as long as <see cref="RocmContext.IsAvailable"/> short-circuits to the CPU
/// fallback before any of these methods is invoked, the missing .so/.dll is never probed.
///
/// Opaque library handles and device pointers are modelled as <see cref="IntPtr"/> (== nint).
/// Every "device pointer" argument below is a raw GPU address returned by <see cref="hipMalloc"/>.
///
/// ABI provenance: these signatures are matched against the published ROCm 6.x headers
/// (hipSOLVER <c>hipsolver.h</c>, rocSPARSE <c>rocsparse-functions.h</c>/<c>rocsparse-types.h</c>,
/// rocBLAS <c>rocblas-functions.h</c>/<c>rocblas-types.h</c>, HIP <c>hip_runtime_api.h</c>);
/// each entry notes the header it follows. hipSOLVER's dense LU uses the NATIVE hipSOLVER API
/// (<c>hipsolverD*</c>) - whose getrf/getrs carry an explicit workspace <c>work</c>/<c>lwork</c>
/// with separate <c>*_bufferSize</c> queries - rather than the thin cuSOLVER-compatibility
/// aliases (<c>hipsolverDnD*</c>), which mirror cuSOLVER's implicit-workspace signatures and are
/// only present when the compat layer is built. The native API is always exported and is the
/// documented, stable choice, so the explicit-lwork shape is unambiguous.
/// MI250 runs on ROCm 5.x and MI300X needs ROCm 6.0+; the entry points below are stable across
/// both. On real hardware the first end-to-end check is the <c>gpucheck</c> preflight.
/// </summary>
internal static class RocmInterop
{
    // Canonical ROCm library base names. The .NET loader appends the platform-specific
    // prefix/suffix (lib*.so on Linux, *.dll on Windows) and probes the usual search paths.
    public const string HipRuntimeLib = "amdhip64";
    public const string HipSolverLib = "hipsolver";
    public const string RocSparseLib = "rocsparse";
    public const string RocBlasLib = "rocblas";

    // A runtime-only ROCm install (e.g. a cloud container) often ships only versioned shared objects
    // (librocsparse.so.1) without the unversioned dev symlink that DllImport("rocsparse") expects.
    // Register a resolver (once, before the first P/Invoke) that tries the plain name, then common
    // versioned names, then the same under $ROCM_PATH/lib - so the backend loads on a bare box. Scoped
    // to this assembly, so it only affects the ROCm bindings below.
    static RocmInterop()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(RocmInterop).Assembly, ResolveRocmLibrary); }
        catch { /* resolver already set for this assembly - fine */ }
    }

    private static IntPtr ResolveRocmLibrary(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            // GR-466: the Windows HIP SDK 6.x runtime ships a versioned DLL (amdhip64_6.dll) beside or instead of the plain name.
            HipRuntimeLib => new[] { "libamdhip64.so", "libamdhip64.so.6", "libamdhip64.so.5", "amdhip64.dll", "amdhip64_6.dll" },
            HipSolverLib => new[] { "libhipsolver.so", "libhipsolver.so.0", "libhipsolver.so.1", "hipsolver.dll" },
            RocSparseLib => new[] { "librocsparse.so", "librocsparse.so.0", "librocsparse.so.1", "rocsparse.dll" },
            RocBlasLib => new[] { "librocblas.so", "librocblas.so.0", "librocblas.so.4", "rocblas.dll" },
            // The CUDA driver-API diagnostic probe (GpuContext) imports "cuda"; the real driver library is
            // nvcuda.dll on Windows and the versioned-only libcuda.so.1 on Linux, neither of which the
            // default "cuda.dll"/"libcuda.so" resolution finds. Handle it here (this is the single
            // per-assembly resolver, so GpuContext can't register its own).
            "cuda" => OperatingSystem.IsWindows()
                ? new[] { "nvcuda.dll" }
                : new[] { "libcuda.so.1", "libcuda.so" },
            _ => Array.Empty<string>(),
        };
        string rocmDir = Environment.GetEnvironmentVariable("ROCM_PATH") ?? "/opt/rocm";
        foreach (string c in candidates)
        {
            if (NativeLibrary.TryLoad(c, out IntPtr h)) return h;
            if (NativeLibrary.TryLoad(System.IO.Path.Combine(rocmDir, "lib", c), out h)) return h;
        }
        return IntPtr.Zero;   // not ours / not found -> fall back to the default resolver
    }

    /// <summary>
    /// GR-043: load <paramref name="name"/> by the route the solver P/Invokes take: this assembly's resolver first (the
    /// versioned and <c>$ROCM_PATH</c> names above), then the runtime's default DllImport search. The availability probe
    /// uses it, so "available" means exactly "the solver calls will find their libraries"; before, the probe called
    /// <c>NativeLibrary.TryLoad</c> with the plain name, which does not consult the resolver, so a runtime-only install
    /// with versioned names alone read unavailable although the solvers would have loaded it.
    /// </summary>
    internal static bool TryLoadLikePInvoke(string name)
    {
        IntPtr h = ResolveRocmLibrary(name, typeof(RocmInterop).Assembly, null);
        if (h != IntPtr.Zero) return true;
        return NativeLibrary.TryLoad(name, typeof(RocmInterop).Assembly, null, out _);
    }

    // ---------------------------------------------------------------------------------
    // Enums / status sentinels
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// hipMemcpyKind - direction argument to <see cref="hipMemcpy"/>. Values match the HIP
    /// runtime header (mirrors CUDA's cudaMemcpyKind numbering). (hip_runtime_api.h)
    /// </summary>
    public enum HipMemcpyKind
    {
        HostToHost = 0,
        HostToDevice = 1,
        DeviceToHost = 2,
        DeviceToDevice = 3,
        Default = 4,
    }

    /// <summary>hipError_t success sentinel (hipSuccess == 0). (hip_runtime_api.h)</summary>
    public const int HipSuccess = 0;

    /// <summary>hipsolverStatus_t success sentinel (HIPSOLVER_STATUS_SUCCESS == 0). (hipsolver.h)</summary>
    public const int HipSolverStatusSuccess = 0;

    /// <summary>rocsparse_status success sentinel (rocsparse_status_success == 0). (rocsparse-types.h)</summary>
    public const int RocSparseStatusSuccess = 0;

    /// <summary>rocblas_status success sentinel (rocblas_status_success == 0). (rocblas-types.h)</summary>
    public const int RocBlasStatusSuccess = 0;

    /// <summary>
    /// hipsolverOperation_t - matrix operation (no-transpose / transpose / conjugate). hipSOLVER
    /// follows the netlib/rocBLAS char codes: 'N'=111, 'T'=112, 'C'=113 (unlike cuSOLVER's 0/1/2).
    /// verified vs ROCm 6.x hipsolver.h (HIPSOLVER_OP_N/T/C).
    /// </summary>
    public enum HipsolverOperation
    {
        None = 111,        // HIPSOLVER_OP_N
        Transpose = 112,   // HIPSOLVER_OP_T
        Conjugate = 113,   // HIPSOLVER_OP_C
    }

    /// <summary>
    /// rocsparse_operation - same netlib/rocBLAS char numbering. verified vs ROCm 6.x
    /// rocsparse-types.h (rocsparse_operation_none/transpose/conjugate_transpose = 111/112/113).
    /// </summary>
    public enum RocsparseOperation
    {
        None = 111,        // rocsparse_operation_none
        Transpose = 112,   // rocsparse_operation_transpose
        Conjugate = 113,   // rocsparse_operation_conjugate_transpose
    }

    /// <summary>
    /// rocblas_side - which side a diagonal/triangular operand multiplies from. verified vs ROCm 6.x
    /// rocblas-types.h (rocblas_side_left = 141, rocblas_side_right = 142, rocblas_side_both = 143).
    /// </summary>
    public enum RocblasSide
    {
        Left = 141,   // rocblas_side_left
        Right = 142,  // rocblas_side_right
        Both = 143,   // rocblas_side_both
    }

    // ---------------------------------------------------------------------------------
    // HIP runtime  (amdhip64)  - all entries verified vs ROCm 6.x hip_runtime_api.h
    // ---------------------------------------------------------------------------------

    /// <summary>hipError_t hipGetDeviceCount(int* count). Cheap probe used for availability.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipGetDeviceCount")]
    public static extern int hipGetDeviceCount(out int count);

    /// <summary>hipError_t hipGetDevice(int* deviceId). Returns the currently-selected device.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipGetDevice")]
    public static extern int hipGetDevice(out int deviceId);

    /// <summary>hipError_t hipSetDevice(int deviceId). Binds subsequent runtime calls to a device.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipSetDevice")]
    public static extern int hipSetDevice(int deviceId);

    /// <summary>
    /// hipError_t hipDeviceGetName(char* name, int len, int device). Writes the device's name
    /// (NUL-terminated) into <paramref name="name"/>. Deliberately used instead of
    /// hipGetDeviceProperties to avoid marshalling the large, version-sensitive hipDeviceProp_t
    /// struct - this call is stable and struct-free.
    /// </summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipDeviceGetName")]
    public static extern int hipDeviceGetName([Out] byte[] name, int len, int device);

    /// <summary>
    /// hipError_t hipMalloc(void** ptr, size_t size). Returns the device address in
    /// <paramref name="ptr"/>. <c>size</c> is bytes; <see cref="nuint"/> maps size_t on both bitnesses.
    /// </summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMalloc")]
    public static extern int hipMalloc(out IntPtr ptr, nuint size);

    /// <summary>hipError_t hipFree(void* ptr).</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipFree")]
    public static extern int hipFree(IntPtr ptr);

    /// <summary>hipError_t hipMemset(void* dst, int value, size_t sizeBytes). Byte-fill device memory.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemset")]
    public static extern int hipMemset(IntPtr dst, int value, nuint sizeBytes);

    /// <summary>hipError_t hipDeviceSynchronize(void). Block until the device is idle.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipDeviceSynchronize")]
    public static extern int hipDeviceSynchronize();

    // hipMemcpy overloads. The native prototype is
    //   hipError_t hipMemcpy(void* dst, const void* src, size_t sizeBytes, hipMemcpyKind kind)
    // We provide typed overloads so the CLR marshals managed arrays straight to/from the
    // device without manual pinning. Pick the overload matching the transfer direction.

    /// <summary>Device &lt;- device (both raw pointers), or any pointer-to-pointer copy.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemcpy")]
    public static extern int hipMemcpy(IntPtr dst, IntPtr src, nuint sizeBytes, HipMemcpyKind kind);

    /// <summary>Host double[] -> device (HostToDevice).</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemcpy")]
    public static extern int hipMemcpy(IntPtr dst, double[] src, nuint sizeBytes, HipMemcpyKind kind);

    /// <summary>Device -> host double[] (DeviceToHost).</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemcpy")]
    public static extern int hipMemcpy(double[] dst, IntPtr src, nuint sizeBytes, HipMemcpyKind kind);

    /// <summary>Host int[] -> device (HostToDevice) - used for CSR row/col arrays.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemcpy")]
    public static extern int hipMemcpy(IntPtr dst, int[] src, nuint sizeBytes, HipMemcpyKind kind);

    /// <summary>Device -> host int[] (DeviceToHost) - e.g. reading devInfo / pivots.</summary>
    [DllImport(HipRuntimeLib, EntryPoint = "hipMemcpy")]
    public static extern int hipMemcpy(int[] dst, IntPtr src, nuint sizeBytes, HipMemcpyKind kind);

    // ---------------------------------------------------------------------------------
    // hipSOLVER dense LU  (hipsolver) - NATIVE hipSOLVER API (explicit workspace)
    // ---------------------------------------------------------------------------------
    //
    // The dense solve is column-major (LAPACK convention). A is factorised in place by
    // getrf into L\U with a unit-lower L; devIpiv receives the 1-based row-swap pivots;
    // getrs then applies the factors to the right-hand side B (ldb x nrhs, column-major).
    // The native hipSOLVER API carries an explicit workspace: getrf and getrs each have their
    // own *_bufferSize query and their own work/lwork parameters (rocSOLVER, which hipSOLVER
    // wraps, does the LU without workspace, but the hipSOLVER API surface still exposes the
    // buffers for LAPACK-style call symmetry). All entries verified vs ROCm 6.x hipsolver.h.

    /// <summary>hipsolverStatus_t hipsolverCreate(hipsolverHandle_t* handle).</summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverCreate")]
    public static extern int hipsolverCreate(out IntPtr handle);

    /// <summary>hipsolverStatus_t hipsolverDestroy(hipsolverHandle_t handle).</summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverDestroy")]
    public static extern int hipsolverDestroy(IntPtr handle);

    /// <summary>
    /// hipsolverStatus_t hipsolverSetStream(hipsolverHandle_t handle, hipStream_t streamId).
    /// Optional: binds the handle to a HIP stream. The dense solver leaves the handle on the
    /// default (null) stream and calls <see cref="hipDeviceSynchronize"/> before reading back,
    /// so this is declared for completeness only.
    /// </summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverSetStream")]
    public static extern int hipsolverSetStream(IntPtr handle, IntPtr streamId);

    /// <summary>
    /// hipsolverStatus_t hipsolverDgetrf_bufferSize(handle, int m, int n, double* A, int lda, int* lwork).
    /// Returns, in <paramref name="lwork"/>, the workspace size the factorisation needs (as an
    /// element count of the compute type). Allocate lwork*sizeof(double) bytes on device.
    /// </summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverDgetrf_bufferSize")]
    public static extern int hipsolverDgetrf_bufferSize(
        IntPtr handle, int m, int n, IntPtr A, int lda, out int lwork);

    /// <summary>
    /// hipsolverStatus_t hipsolverDgetrf(handle, m, n, double* A, lda, double* work, int lwork,
    ///                                   int* devIpiv, int* devInfo).
    /// LU-factorises the column-major mxn matrix A in place.
    /// </summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverDgetrf")]
    public static extern int hipsolverDgetrf(
        IntPtr handle, int m, int n, IntPtr A, int lda,
        IntPtr work, int lwork, IntPtr devIpiv, IntPtr devInfo);

    /// <summary>
    /// hipsolverStatus_t hipsolverDgetrs_bufferSize(handle, trans, int n, int nrhs, double* A,
    ///                                              lda, int* devIpiv, double* B, ldb, int* lwork).
    /// Returns the getrs scratch requirement. getrf and getrs each carry their own workspace, so
    /// the two sizes are queried separately and the caller allocates the maximum of the two.
    /// </summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverDgetrs_bufferSize")]
    public static extern int hipsolverDgetrs_bufferSize(
        IntPtr handle, HipsolverOperation trans, int n, int nrhs, IntPtr A, int lda,
        IntPtr devIpiv, IntPtr B, int ldb, out int lwork);

    /// <summary>
    /// hipsolverStatus_t hipsolverDgetrs(handle, trans, int n, int nrhs, double* A, lda,
    ///                                   int* devIpiv, double* B, ldb, double* work, int lwork,
    ///                                   int* devInfo).
    /// Solves A X = B in place in B using the factors from getrf.
    /// </summary>
    [DllImport(HipSolverLib, EntryPoint = "hipsolverDgetrs")]
    public static extern int hipsolverDgetrs(
        IntPtr handle, HipsolverOperation trans, int n, int nrhs, IntPtr A, int lda,
        IntPtr devIpiv, IntPtr B, int ldb, IntPtr work, int lwork, IntPtr devInfo);

    // ---------------------------------------------------------------------------------
    // rocSPARSE  (rocsparse) - CSR sparse mat-vec for the iterative sparse solve
    // ---------------------------------------------------------------------------------
    //
    // We use the classic level-2 CSR SpMV (rocsparse_dcsrmv), which needs no external work
    // buffer. It is deprecated-but-present in ROCm 6.x (superseded by the staged generic
    // rocsparse_spmv); we stay on the classic entry because our solver uploads the CSR once and
    // issues many SpMVs, and dcsrmv's info-driven analysis path is the canonical fit for that.
    // All entries verified vs ROCm 6.x rocsparse-functions.h.

    /// <summary>rocsparse_status rocsparse_create_handle(rocsparse_handle* handle).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_create_handle")]
    public static extern int rocsparse_create_handle(out IntPtr handle);

    /// <summary>rocsparse_status rocsparse_destroy_handle(rocsparse_handle handle).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_destroy_handle")]
    public static extern int rocsparse_destroy_handle(IntPtr handle);

    /// <summary>rocsparse_status rocsparse_create_mat_descr(rocsparse_mat_descr* descr).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_create_mat_descr")]
    public static extern int rocsparse_create_mat_descr(out IntPtr descr);

    /// <summary>rocsparse_status rocsparse_destroy_mat_descr(rocsparse_mat_descr descr).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_destroy_mat_descr")]
    public static extern int rocsparse_destroy_mat_descr(IntPtr descr);

    /// <summary>rocsparse_status rocsparse_create_mat_info(rocsparse_mat_info* info).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_create_mat_info")]
    public static extern int rocsparse_create_mat_info(out IntPtr info);

    /// <summary>rocsparse_status rocsparse_destroy_mat_info(rocsparse_mat_info info).</summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_destroy_mat_info")]
    public static extern int rocsparse_destroy_mat_info(IntPtr info);

    /// <summary>
    /// rocsparse_status rocsparse_dcsrmv_analysis(
    ///     rocsparse_handle handle, rocsparse_operation trans,
    ///     rocsparse_int m, rocsparse_int n, rocsparse_int nnz,
    ///     const rocsparse_mat_descr descr,
    ///     const double* csr_val, const rocsparse_int* csr_row_ptr, const rocsparse_int* csr_col_ind,
    ///     rocsparse_mat_info info).
    ///
    /// One-time analysis that populates <c>info</c> for the adaptive CSR SpMV algorithm. When a
    /// non-null <c>info</c> is later handed to <see cref="rocsparse_dcsrmv"/>, rocSPARSE expects
    /// this analysis to have run first. Because our solver uploads the CSR once and issues many
    /// SpMVs, running analysis once here is the faster, canonical choice.
    /// rocsparse_int is 32-bit in the standard build. verified vs ROCm 6.x rocsparse-functions.h.
    /// </summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_dcsrmv_analysis")]
    public static extern int rocsparse_dcsrmv_analysis(
        IntPtr handle, RocsparseOperation trans, int m, int n, int nnz,
        IntPtr descr, IntPtr csrVal, IntPtr csrRowPtr, IntPtr csrColInd, IntPtr info);

    /// <summary>
    /// rocsparse_status rocsparse_csrmv_clear(rocsparse_handle handle, rocsparse_mat_info info).
    /// Releases the analysis metadata attached to <c>info</c> by
    /// <see cref="rocsparse_dcsrmv_analysis"/>. Type-agnostic (no d/s prefix).
    /// </summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_csrmv_clear")]
    public static extern int rocsparse_csrmv_clear(IntPtr handle, IntPtr info);

    /// <summary>
    /// rocsparse_status rocsparse_dcsrmv(
    ///     rocsparse_handle handle, rocsparse_operation trans,
    ///     rocsparse_int m, rocsparse_int n, rocsparse_int nnz,
    ///     const double* alpha, const rocsparse_mat_descr descr,
    ///     const double* csr_val, const rocsparse_int* csr_row_ptr, const rocsparse_int* csr_col_ind,
    ///     rocsparse_mat_info info, const double* x, const double* beta, double* y).
    ///
    /// Computes y = alpha * op(A) * x + beta * y for a CSR matrix. <c>alpha</c>/<c>beta</c> are
    /// scalar pointers read according to the handle's pointer mode; a freshly-created rocSPARSE
    /// handle defaults to rocsparse_pointer_mode_host, so passing them as host scalars
    /// (<c>ref double</c>) is correct. The freshly-created mat_descr defaults to
    /// rocsparse_index_base_zero + rocsparse_matrix_type_general, which matches GridSim's 0-based
    /// CSR, so no extra descriptor setup is needed. verified vs ROCm 6.x rocsparse-functions.h.
    /// </summary>
    [DllImport(RocSparseLib, EntryPoint = "rocsparse_dcsrmv")]
    public static extern int rocsparse_dcsrmv(
        IntPtr handle, RocsparseOperation trans, int m, int n, int nnz,
        ref double alpha, IntPtr descr,
        IntPtr csrVal, IntPtr csrRowPtr, IntPtr csrColInd,
        IntPtr info, IntPtr x, ref double beta, IntPtr y);

    // ---------------------------------------------------------------------------------
    // rocBLAS  (rocblas) - level-1 vector algebra for the device-resident BiCGSTAB
    // ---------------------------------------------------------------------------------
    //
    // rocblas_int is 32-bit. A freshly-created rocBLAS handle defaults to
    // rocblas_pointer_mode_host, so scalar inputs (alpha) and reduction outputs (result) are HOST
    // pointers - marshalled here as `ref double` / `out double`. In host pointer mode the reduction
    // routines (ddot) block until the scalar is available on the host. All entries verified vs
    // ROCm 6.x rocblas-functions.h.

    /// <summary>rocblas_status rocblas_create_handle(rocblas_handle* handle).</summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_create_handle")]
    public static extern int rocblas_create_handle(out IntPtr handle);

    /// <summary>rocblas_status rocblas_destroy_handle(rocblas_handle handle).</summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_destroy_handle")]
    public static extern int rocblas_destroy_handle(IntPtr handle);

    /// <summary>
    /// rocblas_status rocblas_ddot(handle, int n, const double* x, int incx, const double* y,
    ///                             int incy, double* result). result = x . y (host scalar).
    /// </summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_ddot")]
    public static extern int rocblas_ddot(
        IntPtr handle, int n, IntPtr x, int incx, IntPtr y, int incy, out double result);

    /// <summary>
    /// rocblas_status rocblas_daxpy(handle, int n, const double* alpha, const double* x, int incx,
    ///                              double* y, int incy). y := alpha*x + y.
    /// </summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_daxpy")]
    public static extern int rocblas_daxpy(
        IntPtr handle, int n, ref double alpha, IntPtr x, int incx, IntPtr y, int incy);

    /// <summary>rocblas_status rocblas_dscal(handle, int n, const double* alpha, double* x, int incx). x := alpha*x.</summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_dscal")]
    public static extern int rocblas_dscal(IntPtr handle, int n, ref double alpha, IntPtr x, int incx);

    /// <summary>rocblas_status rocblas_dcopy(handle, int n, const double* x, int incx, double* y, int incy). y := x.</summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_dcopy")]
    public static extern int rocblas_dcopy(IntPtr handle, int n, IntPtr x, int incx, IntPtr y, int incy);

    /// <summary>
    /// rocblas_status rocblas_ddgmm(handle, rocblas_side side, int m, int n, const double* A, int lda,
    ///                              const double* x, int incx, double* C, int ldc).
    /// Diagonal matrix multiply: side==Left gives C = diag(x) * A. Used as an on-device Hadamard
    /// (elementwise) product for the Jacobi preconditioner apply: with A and C as N x 1 column
    /// vectors (lda=ldc=N) and x the inverse-diagonal, C[i] = x[i] * A[i].
    /// </summary>
    [DllImport(RocBlasLib, EntryPoint = "rocblas_ddgmm")]
    public static extern int rocblas_ddgmm(
        IntPtr handle, RocblasSide side, int m, int n, IntPtr A, int lda, IntPtr x, int incx, IntPtr C, int ldc);
}
