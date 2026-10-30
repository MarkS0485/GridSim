using System;
using System.Reflection;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A hardware-free guard on the native ROCm P/Invoke surface (<c>GridSim.Gpu.RocmInterop</c>).
/// The signatures are matched by hand against the ROCm 6.x headers and can only be exercised at
/// runtime on an AMD Instinct box, so these reflection tests pin the ABI-critical constants and
/// method shapes in CI: an accidental edit that flips an enum value, a status sentinel, or the
/// explicit-workspace getrf/getrs signature - or that regresses to the ambiguous cuSOLVER-compat
/// (hipsolverDn*) API - fails here without a device. RocmInterop is internal, so we reach it by
/// reflection through the GridSim.Gpu assembly.
/// </summary>
public class RocmInteropShapeTests
{
    private static readonly Type Interop =
        typeof(RocmDenseLinearSolver).Assembly.GetType("GridSim.Gpu.RocmInterop")
        ?? throw new InvalidOperationException("GridSim.Gpu.RocmInterop not found.");

    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static int EnumValue(string enumName, string member)
    {
        Type e = Interop.GetNestedType(enumName, BindingFlags.Public | BindingFlags.NonPublic)
                 ?? throw new InvalidOperationException($"enum {enumName} not found.");
        return Convert.ToInt32(Enum.Parse(e, member));
    }

    private static int ConstValue(string name)
    {
        FieldInfo f = Interop.GetField(name, Any) ?? throw new InvalidOperationException($"const {name} not found.");
        return Convert.ToInt32(f.GetValue(null));
    }

    private static MethodInfo? Method(string name) => Interop.GetMethod(name, Any);

    [Fact]
    public void Operation_enums_use_the_netlib_char_codes_111_112_113()
    {
        Assert.Equal(111, EnumValue("HipsolverOperation", "None"));
        Assert.Equal(112, EnumValue("HipsolverOperation", "Transpose"));
        Assert.Equal(113, EnumValue("HipsolverOperation", "Conjugate"));

        Assert.Equal(111, EnumValue("RocsparseOperation", "None"));
        Assert.Equal(112, EnumValue("RocsparseOperation", "Transpose"));
        Assert.Equal(113, EnumValue("RocsparseOperation", "Conjugate"));
    }

    [Fact]
    public void Rocblas_side_enum_uses_141_142_143()
    {
        Assert.Equal(141, EnumValue("RocblasSide", "Left"));
        Assert.Equal(142, EnumValue("RocblasSide", "Right"));
        Assert.Equal(143, EnumValue("RocblasSide", "Both"));
    }

    [Fact]
    public void Status_success_sentinels_are_all_zero()
    {
        Assert.Equal(0, ConstValue("HipSuccess"));
        Assert.Equal(0, ConstValue("HipSolverStatusSuccess"));
        Assert.Equal(0, ConstValue("RocSparseStatusSuccess"));
        Assert.Equal(0, ConstValue("RocBlasStatusSuccess"));
    }

    [Fact]
    public void Hipsolver_getrf_getrs_carry_the_explicit_workspace_signature()
    {
        // Native hipSOLVER API: getrf(handle,m,n,A,lda,work,lwork,ipiv,info) = 9 params;
        // getrs(handle,trans,n,nrhs,A,lda,ipiv,B,ldb,work,lwork,info) = 12 params.
        MethodInfo getrf = Method("hipsolverDgetrf") ?? throw new InvalidOperationException("hipsolverDgetrf missing");
        MethodInfo getrs = Method("hipsolverDgetrs") ?? throw new InvalidOperationException("hipsolverDgetrs missing");
        Assert.Equal(9, getrf.GetParameters().Length);
        Assert.Equal(12, getrs.GetParameters().Length);

        // Both must have their own buffer-size query (the getrf/getrs separate-workspace divergence).
        Assert.NotNull(Method("hipsolverDgetrf_bufferSize"));
        Assert.NotNull(Method("hipsolverDgetrs_bufferSize"));
    }

    [Fact]
    public void The_ambiguous_cusolver_compat_names_are_not_used()
    {
        // hipsolverDn* mirrors cuSOLVER's implicit-workspace shape and is only present when the
        // compat layer is built; the native hipsolver* API is the unambiguous choice we bind.
        Assert.Null(Method("hipsolverDnDgetrf"));
        Assert.Null(Method("hipsolverDnDgetrs"));
        Assert.Null(Method("hipsolverDnCreate"));
    }

    [Fact]
    public void The_rocblas_level1_surface_for_the_resident_sparse_solve_is_present()
    {
        Assert.NotNull(Method("rocblas_create_handle"));
        Assert.NotNull(Method("rocblas_ddot"));
        Assert.NotNull(Method("rocblas_daxpy"));
        Assert.NotNull(Method("rocblas_dscal"));
        Assert.NotNull(Method("rocblas_dcopy"));

        // dgmm is the on-device Hadamard product for the Jacobi apply: (handle,side,m,n,A,lda,x,incx,C,ldc).
        MethodInfo dgmm = Method("rocblas_ddgmm") ?? throw new InvalidOperationException("rocblas_ddgmm missing");
        Assert.Equal(10, dgmm.GetParameters().Length);
    }

    [Fact]
    public void The_unused_generic_rocsparse_spmv_scaffold_was_removed()
    {
        // We stay on the classic rocsparse_dcsrmv; the half-declared generic path is gone.
        Assert.NotNull(Method("rocsparse_dcsrmv"));
        Assert.Null(Method("rocsparse_spmv"));
        Assert.Null(Method("rocsparse_create_csr_descr"));
        Assert.Null(Method("rocsparse_create_dnvec_descr"));
    }
}
