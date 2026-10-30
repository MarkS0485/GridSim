using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The GPU solvers sit behind the same ILinearSolver/ISparseLinearSolver seams as the CPU
/// implementations and MUST agree with them - previously nothing referenced GridSim.Gpu from
/// the test project at all, so GPU/CPU seam drift was structurally undetectable. These tests
/// are hardware-free: without a device they pin the CPU-fallback path; with one they pin the
/// kernels against the CPU reference. The OpenCL cases exercise the SAME ILGPU kernels via the
/// portable OpenCL accelerator (AMD/Intel/NVIDIA) - so a single kernel set covers both vendors.
/// <para>Bug #42 (October 2026): so a pass here on a machine without the device is a pass of the fallback contract
/// (the GPU solver routes to the CPU solver and agrees with it), not of the kernels. The kernels on the device are pinned
/// by the device-gated GpuDeviceKernelTests, which are reported Skipped with the reason where there is no device.</para>
/// </summary>
public class GpuSolverSeamTests
{
    private static (double[,] a, double[] b) WellConditioned()
    {
        // Diagonally dominant 4x4 - a stable, unambiguous solution.
        double[,] a =
        {
            { 10,  1,  0,  2 },
            {  1, 12,  3,  0 },
            {  0,  3,  9,  1 },
            {  2,  0,  1, 11 },
        };
        double[] b = { 13, 16, 13, 14 };
        return (a, b);
    }

    [Fact]
    public void Gpu_dense_lu_matches_cpu_dense_lu()
    {
        (double[,] a1, double[] b1) = WellConditioned();
        (double[,] a2, double[] b2) = WellConditioned();

        new DenseLuSolver().SolveInPlace(a1, b1);
        new GpuDenseLinearSolver().SolveInPlace(a2, b2);

        for (int i = 0; i < b1.Length; i++)
            Assert.Equal(b1[i], b2[i], 10);
    }

    [Fact]
    public void Gpu_dense_lu_throws_on_a_near_singular_pivot_not_just_an_exact_zero()
    {
        // Two nearly identical rows: no pivot is exactly zero, but the matrix is numerically
        // singular. The CPU solver's relative floor rejects it; the GPU path must agree
        // (its kernel used to accept anything != 0.0 and return garbage as success).
        double[,] a =
        {
            { 1.0, 2.0, 3.0 },
            { 1.0, 2.0, 3.0 + 1e-15 },
            { 4.0, 5.0, 6.0 },
        };
        double[] b = { 1, 1, 1 };
        Assert.ThrowsAny<InvalidOperationException>(() => new GpuDenseLinearSolver().SolveInPlace(a, b));
    }

    [Fact]
    public void Gpu_dense_lu_rejects_mismatched_dimensions()
    {
        var a = new double[3, 3];
        var b = new double[2];
        Assert.Throws<ArgumentException>(() => new GpuDenseLinearSolver().SolveInPlace(a, b));
    }

    [Fact]
    public void Gpu_sparse_solver_matches_cpu_sparse_lu()
    {
        var builder = new CsrMatrix.Builder(4);
        builder.Add(0, 0, 10); builder.Add(0, 1, 1); builder.Add(0, 3, 2);
        builder.Add(1, 0, 1); builder.Add(1, 1, 12); builder.Add(1, 2, 3);
        builder.Add(2, 1, 3); builder.Add(2, 2, 9); builder.Add(2, 3, 1);
        builder.Add(3, 0, 2); builder.Add(3, 2, 1); builder.Add(3, 3, 11);
        CsrMatrix a = builder.Build();
        double[] b = { 13, 16, 13, 14 };

        double[]? cpu = new CpuSparseLuSolver().Solve(a, (double[])b.Clone());
        double[]? gpu = new GpuSparseLinearSolver().Solve(a, (double[])b.Clone());

        Assert.NotNull(cpu);
        Assert.NotNull(gpu);
        for (int i = 0; i < 4; i++)
            Assert.Equal(cpu![i], gpu![i], 8);
    }

    [Fact]
    public void Gpu_sparse_solver_rejects_mismatched_rhs_length()
    {
        var builder = new CsrMatrix.Builder(2);
        builder.Add(0, 0, 1); builder.Add(1, 1, 1);
        CsrMatrix a = builder.Build();
        Assert.Throws<ArgumentException>(() => new GpuSparseLinearSolver().Solve(a, new double[3]));
    }

    // ---- OpenCL: the same ILGPU kernels via the portable accelerator ----

    [Fact]
    public void Opencl_dense_lu_matches_cpu_dense_lu()
    {
        (double[,] a1, double[] b1) = WellConditioned();
        (double[,] a2, double[] b2) = WellConditioned();

        new DenseLuSolver().SolveInPlace(a1, b1);
        new GpuDenseLinearSolver(AcceleratorType.OpenCL).SolveInPlace(a2, b2);

        for (int i = 0; i < b1.Length; i++)
            Assert.Equal(b1[i], b2[i], 10);
    }

    [Fact]
    public void Opencl_dense_lu_throws_on_a_near_singular_pivot_not_just_an_exact_zero()
    {
        double[,] a =
        {
            { 1.0, 2.0, 3.0 },
            { 1.0, 2.0, 3.0 + 1e-15 },
            { 4.0, 5.0, 6.0 },
        };
        double[] b = { 1, 1, 1 };
        Assert.ThrowsAny<InvalidOperationException>(() => new GpuDenseLinearSolver(AcceleratorType.OpenCL).SolveInPlace(a, b));
    }

    [Fact]
    public void Opencl_dense_lu_rejects_mismatched_dimensions()
    {
        var a = new double[3, 3];
        var b = new double[2];
        Assert.Throws<ArgumentException>(() => new GpuDenseLinearSolver(AcceleratorType.OpenCL).SolveInPlace(a, b));
    }

    [Fact]
    public void Opencl_sparse_solver_matches_cpu_sparse_lu()
    {
        var builder = new CsrMatrix.Builder(4);
        builder.Add(0, 0, 10); builder.Add(0, 1, 1); builder.Add(0, 3, 2);
        builder.Add(1, 0, 1); builder.Add(1, 1, 12); builder.Add(1, 2, 3);
        builder.Add(2, 1, 3); builder.Add(2, 2, 9); builder.Add(2, 3, 1);
        builder.Add(3, 0, 2); builder.Add(3, 2, 1); builder.Add(3, 3, 11);
        CsrMatrix a = builder.Build();
        double[] b = { 13, 16, 13, 14 };

        double[]? cpu = new CpuSparseLuSolver().Solve(a, (double[])b.Clone());
        double[]? gpu = new GpuSparseLinearSolver(accel: AcceleratorType.OpenCL).Solve(a, (double[])b.Clone());

        Assert.NotNull(cpu);
        Assert.NotNull(gpu);
        for (int i = 0; i < 4; i++)
            Assert.Equal(cpu![i], gpu![i], 8);
    }

    [Fact]
    public void Opencl_sparse_solver_rejects_mismatched_rhs_length()
    {
        var builder = new CsrMatrix.Builder(2);
        builder.Add(0, 0, 1); builder.Add(1, 1, 1);
        CsrMatrix a = builder.Build();
        Assert.Throws<ArgumentException>(() => new GpuSparseLinearSolver(accel: AcceleratorType.OpenCL).Solve(a, new double[3]));
    }
}
