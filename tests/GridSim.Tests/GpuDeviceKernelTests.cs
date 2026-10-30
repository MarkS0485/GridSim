using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// The GPU kernels on a real device. <see cref="GpuSolverSeamTests"/> and the GPU row of the batched solver tests are
/// hardware-free: with no device they check the CPU fallback and pass, so on their own a green run does not say the
/// kernels ran. These tests run only where the accelerator is present (<see cref="GpuDeviceFactAttribute"/>; elsewhere
/// they are reported skipped with the reason) and assert inside the test that the device is still live, so where they
/// pass, the kernels ran on the device. CUDA and OpenCL each get the batched dense LU, the dense LU, the near-singular
/// refusal and the sparse solver.
/// </summary>
public class GpuDeviceKernelTests
{
    private readonly ITestOutputHelper _out;

    public GpuDeviceKernelTests(ITestOutputHelper output) => _out = output;

    // The device the test was gated on must still be there when the solver is called, or the solver would take its CPU
    // fallback and the test would check the wrong thing.
    private void RequireDevice(AcceleratorType accel)
    {
        Assert.True(GpuContext.IsAvailableFor(accel), $"{accel} accelerator vanished after discovery");
        _out.WriteLine($"device: {GpuContext.DescriptionFor(accel)}");
    }

    // Same construction as BatchedSolverTests.MakeBatch: diagonally dominant systems, so partial pivoting never swaps
    // rows and the solve is well conditioned.
    private static (double[] a, double[] b) MakeBatch(int count, int n, int seed)
    {
        var rng = new Random(seed);
        var a = new double[count * n * n];
        var b = new double[count * n];
        for (int s = 0; s < count; s++)
        {
            for (int i = 0; i < n; i++)
            {
                double rowAbs = 0;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double v = rng.NextDouble() * 2 - 1;
                    a[s * n * n + i * n + j] = v;
                    rowAbs += Math.Abs(v);
                }
                a[s * n * n + i * n + i] = rowAbs + 1.0 + rng.NextDouble();
                b[s * n + i] = rng.NextDouble() * 10 - 5;
            }
        }
        return (a, b);
    }

    private void BatchedMatchesCpu(AcceleratorType accel)
    {
        RequireDevice(accel);
        const int count = 24, n = 10;
        (double[] a, double[] bGpu) = MakeBatch(count, n, seed: 99);
        double[] bCpu = (double[])bGpu.Clone();
        double[] aCpu = (double[])a.Clone();

        bool[] okGpu = new GpuBatchedDenseSolver(accel).SolveBatch(count, n, a, bGpu);
        bool[] okCpu = new CpuBatchedLuSolver().SolveBatch(count, n, aCpu, bCpu);

        Assert.Equal(okCpu, okGpu);
        Assert.All(okGpu, Assert.True);
        double maxRel = 0;
        for (int i = 0; i < count * n; i++)
            maxRel = Math.Max(maxRel, Math.Abs(bGpu[i] - bCpu[i]) / Math.Max(1.0, Math.Abs(bCpu[i])));
        _out.WriteLine($"max relative difference GPU vs CPU: {maxRel:R}");
        for (int i = 0; i < count * n; i++)
            Assert.Equal(bCpu[i], bGpu[i], 8);   // the batched row's agreement, now with the kernels known to have run
        // Tighter than 8 places: the device eliminates in the CPU's row order on these diagonally dominant systems (no
        // swaps), so only per-operation rounding differs; measured 3.3e-16 on a GTX 1650 (CUDA).
        Assert.True(maxRel <= 1e-13, $"GPU batched solution differs from the CPU reference by {maxRel:R} (relative)");
    }

    private void BatchedIsolatesSingular(AcceleratorType accel)
    {
        RequireDevice(accel);
        const int n = 5;
        (double[] a, double[] b) = MakeBatch(3, n, seed: 7);
        for (int j = 0; j < n; j++) a[1 * n * n + 1 * n + j] = 0.0;   // system 1: zero row, singular

        bool[] ok = new GpuBatchedDenseSolver(accel).SolveBatch(3, n, a, b);

        Assert.True(ok[0]);
        Assert.False(ok[1]);
        Assert.True(ok[2]);
        for (int i = 0; i < n; i++) Assert.True(double.IsNaN(b[1 * n + i]));
        for (int i = 0; i < n; i++) Assert.True(double.IsFinite(b[0 * n + i]));
        for (int i = 0; i < n; i++) Assert.True(double.IsFinite(b[2 * n + i]));
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

    private void DenseMatchesCpu(AcceleratorType accel)
    {
        RequireDevice(accel);
        (double[,] a1, double[] b1) = WellConditioned();
        (double[,] a2, double[] b2) = WellConditioned();

        new DenseLuSolver().SolveInPlace(a1, b1);
        new GpuDenseLinearSolver(accel).SolveInPlace(a2, b2);

        for (int i = 0; i < b1.Length; i++)
            Assert.Equal(b1[i], b2[i], 10);
    }

    private void DenseRefusesNearSingular(AcceleratorType accel)
    {
        RequireDevice(accel);
        double[,] a =
        {
            { 1.0, 2.0, 3.0 },
            { 1.0, 2.0, 3.0 + 1e-15 },
            { 4.0, 5.0, 6.0 },
        };
        double[] b = { 1, 1, 1 };
        Assert.ThrowsAny<InvalidOperationException>(() => new GpuDenseLinearSolver(accel).SolveInPlace(a, b));
    }

    private void SparseMatchesCpu(AcceleratorType accel)
    {
        RequireDevice(accel);
        var builder = new CsrMatrix.Builder(4);
        builder.Add(0, 0, 10); builder.Add(0, 1, 1); builder.Add(0, 3, 2);
        builder.Add(1, 0, 1); builder.Add(1, 1, 12); builder.Add(1, 2, 3);
        builder.Add(2, 1, 3); builder.Add(2, 2, 9); builder.Add(2, 3, 1);
        builder.Add(3, 0, 2); builder.Add(3, 2, 1); builder.Add(3, 3, 11);
        CsrMatrix a = builder.Build();
        double[] b = { 13, 16, 13, 14 };

        double[]? cpu = new CpuSparseLuSolver().Solve(a, (double[])b.Clone());
        // The GPU sparse solver returns null when the device solve fails (the caller then retries on the CPU); on a
        // device the solve must succeed, so null is a failure here.
        double[]? gpu = new GpuSparseLinearSolver(accel: accel).Solve(a, (double[])b.Clone());

        Assert.NotNull(cpu);
        Assert.NotNull(gpu);
        for (int i = 0; i < 4; i++)
            Assert.Equal(cpu![i], gpu![i], 8);
    }

    // ---- CUDA ----

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_batched_matches_cpu_batched() => BatchedMatchesCpu(AcceleratorType.Cuda);

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_batched_isolates_a_singular_system() => BatchedIsolatesSingular(AcceleratorType.Cuda);

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_dense_lu_matches_cpu_dense_lu() => DenseMatchesCpu(AcceleratorType.Cuda);

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_dense_lu_refuses_a_near_singular_pivot() => DenseRefusesNearSingular(AcceleratorType.Cuda);

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_sparse_solver_matches_cpu_sparse_lu() => SparseMatchesCpu(AcceleratorType.Cuda);

    // ---- OpenCL ----

    [GpuDeviceFact(AcceleratorType.OpenCL)]
    public void Opencl_batched_matches_cpu_batched_on_the_device() => BatchedMatchesCpu(AcceleratorType.OpenCL);

    [GpuDeviceFact(AcceleratorType.OpenCL)]
    public void Opencl_batched_isolates_a_singular_system_on_the_device() => BatchedIsolatesSingular(AcceleratorType.OpenCL);

    [GpuDeviceFact(AcceleratorType.OpenCL)]
    public void Opencl_dense_lu_matches_cpu_dense_lu_on_the_device() => DenseMatchesCpu(AcceleratorType.OpenCL);

    [GpuDeviceFact(AcceleratorType.OpenCL)]
    public void Opencl_dense_lu_refuses_a_near_singular_pivot_on_the_device() => DenseRefusesNearSingular(AcceleratorType.OpenCL);

    [GpuDeviceFact(AcceleratorType.OpenCL)]
    public void Opencl_sparse_solver_matches_cpu_sparse_lu_on_the_device() => SparseMatchesCpu(AcceleratorType.OpenCL);
}
