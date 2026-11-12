using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// GR-885: the ROCm solvers on a real AMD device. <see cref="RocmSolverSeamTests"/> is hardware-free and, with no device,
/// checks the CPU fallback against the CPU reference (the fallback contract, kept as it is). These tests run only where a
/// ROCm device is present (<see cref="RocmDeviceFactAttribute"/>; elsewhere they are reported skipped with the reason)
/// and assert inside the test that the device is still live, so where they pass the device path ran. The comparisons use
/// the same agreement as the CUDA / OpenCL device tests (<see cref="GpuDeviceKernelTests"/>). Device run only.
/// </summary>
public class RocmDeviceKernelTests
{
    private readonly ITestOutputHelper _out;

    public RocmDeviceKernelTests(ITestOutputHelper output) => _out = output;

    private void RequireDevice()
    {
        Assert.True(RocmContext.IsAvailable, "the ROCm device vanished after discovery");
        _out.WriteLine($"device: {RocmContext.DeviceDescription}");
    }

    private static (double[,] a, double[] b) WellConditioned()
    {
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

    [RocmDeviceFact]
    public void Rocm_dense_lu_matches_cpu_dense_lu_on_the_device()
    {
        RequireDevice();
        (double[,] a1, double[] b1) = WellConditioned();
        (double[,] a2, double[] b2) = WellConditioned();
        new DenseLuSolver().SolveInPlace(a1, b1);
        new RocmDenseLinearSolver().SolveInPlace(a2, b2);
        for (int i = 0; i < b1.Length; i++)
            Assert.Equal(b1[i], b2[i], 10);
    }

    [RocmDeviceFact]
    public void Rocm_dense_lu_refuses_an_exactly_singular_matrix_on_the_device()
    {
        RequireDevice();
        double[,] a =
        {
            { 1.0, 2.0, 3.0 },
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
        };
        double[] b = { 1, 1, 1 };
        Assert.ThrowsAny<InvalidOperationException>(() => new RocmDenseLinearSolver().SolveInPlace(a, b));
    }

    [RocmDeviceFact]
    public void Rocm_sparse_solver_matches_cpu_sparse_lu_on_the_device()
    {
        RequireDevice();
        var builder = new CsrMatrix.Builder(4);
        builder.Add(0, 0, 10); builder.Add(0, 1, 1); builder.Add(0, 3, 2);
        builder.Add(1, 0, 1); builder.Add(1, 1, 12); builder.Add(1, 2, 3);
        builder.Add(2, 1, 3); builder.Add(2, 2, 9); builder.Add(2, 3, 1);
        builder.Add(3, 0, 2); builder.Add(3, 2, 1); builder.Add(3, 3, 11);
        CsrMatrix a = builder.Build();
        double[] b = { 13, 16, 13, 14 };
        long fallbacksBefore = RocmSparseLinearSolver.OutOfMemoryFallbacks;

        double[]? cpu = new CpuSparseLuSolver().Solve(a, (double[])b.Clone());
        double[]? rocm = new RocmSparseLinearSolver().Solve(a, (double[])b.Clone());

        Assert.NotNull(cpu);
        Assert.NotNull(rocm);
        // A 4 x 4 system cannot exhaust device memory: the answer came from the device, not the GR-475 CPU fallback.
        Assert.Equal(fallbacksBefore, RocmSparseLinearSolver.OutOfMemoryFallbacks);
        for (int i = 0; i < 4; i++)
            Assert.Equal(cpu![i], rocm![i], 8);
    }
}
