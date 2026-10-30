using GridSim.Core.Abstractions;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests.Scheduling;

/// <summary>
/// Validates the batched dense linear solver (Phase 4): the CPU reference matches a per-system dense LU, a
/// singular system is isolated (NaN + false, not a batch failure), and the GPU path agrees with the CPU
/// reference (running the ILGPU kernels when a device is present, else exercising the graceful CPU fallback).
/// </summary>
public class BatchedSolverTests
{
    // Deterministic, well-conditioned batch: each system is strongly diagonally dominant so LU is stable.
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
                a[s * n * n + i * n + i] = rowAbs + 1.0 + rng.NextDouble();   // diagonally dominant
                b[s * n + i] = rng.NextDouble() * 10 - 5;
            }
        }
        return (a, b);
    }

    // Ground truth: solve each system independently with the proven single-system dense LU.
    private static double[] PerSystem(int count, int n, double[] a, double[] b)
    {
        var x = new double[count * n];
        for (int s = 0; s < count; s++)
        {
            var m = new double[n, n];
            var rhs = new double[n];
            Buffer.BlockCopy(a, s * n * n * sizeof(double), m, 0, n * n * sizeof(double));
            Array.Copy(b, s * n, rhs, 0, n);
            new DenseLuSolver().SolveInPlace(m, rhs);
            Array.Copy(rhs, 0, x, s * n, n);
        }
        return x;
    }

    [Fact]
    public void Cpu_batched_matches_per_system_dense_lu()
    {
        const int count = 40, n = 12;
        (double[] a, double[] b) = MakeBatch(count, n, seed: 20260725);
        double[] expected = PerSystem(count, n, a, b);

        bool[] ok = new CpuBatchedLuSolver().SolveBatch(count, n, a, b);

        Assert.All(ok, Assert.True);
        for (int i = 0; i < count * n; i++)
            Assert.Equal(expected[i], b[i], 10);   // 10 significant digits
    }

    [Fact]
    public void Singular_system_is_isolated_returns_nan_and_false()
    {
        const int n = 5;
        (double[] a, double[] b) = MakeBatch(3, n, seed: 7);
        // Make system 1 singular: zero its whole second row (rank-deficient).
        for (int j = 0; j < n; j++) a[1 * n * n + 1 * n + j] = 0.0;

        bool[] ok = new CpuBatchedLuSolver().SolveBatch(3, n, a, b);

        Assert.True(ok[0]);
        Assert.False(ok[1]);
        Assert.True(ok[2]);
        for (int i = 0; i < n; i++) Assert.True(double.IsNaN(b[1 * n + i]));   // failed system -> NaN
        for (int i = 0; i < n; i++) Assert.False(double.IsNaN(b[0 * n + i]));  // neighbours unaffected
        for (int i = 0; i < n; i++) Assert.False(double.IsNaN(b[2 * n + i]));
    }

    // Bug #42 (October 2026): on a machine with no OpenCL device this test pins the fallback contract (the GPU batched
    // solver routes to the CPU reference and agrees with it), not the kernels. The kernels on the device are pinned by
    // the device-gated GpuDeviceKernelTests, which are reported Skipped with the reason where there is no device.
    [Fact]
    public void Gpu_batched_matches_cpu_batched()
    {
        const int count = 24, n = 10;
        (double[] a, double[] b1) = MakeBatch(count, n, seed: 99);
        double[] b2 = (double[])b1.Clone();
        double[] a2 = (double[])a.Clone();

        // OpenCL is the portable path; with no device it falls back to the CPU reference (still a valid run).
        bool[] okGpu = new GpuBatchedDenseSolver(AcceleratorType.OpenCL).SolveBatch(count, n, a, b1);
        bool[] okCpu = new CpuBatchedLuSolver().SolveBatch(count, n, a2, b2);

        Assert.Equal(okCpu, okGpu);
        for (int i = 0; i < count * n; i++)
            Assert.Equal(b2[i], b1[i], 8);   // kernels (or fallback) agree with the reference to 8 digits
    }
}
