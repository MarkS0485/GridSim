using GridSim.Core.IO;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuSolveCountersSerial
{
    // The counters are process-wide: the tests that read exact differences run alone, so no other GPU test's solves
    // land between their two snapshots.
    public const string Name = "GpuSolveCounters (serial)";
}

/// <summary>
/// RC4: the GPU solvers count where each linear solve ran (<see cref="GpuSolveCounters"/>), so a run selected with
/// <c>--gpu</c> can say how much of it fell back to the CPU. Device tests run only where CUDA is present.
/// </summary>
[Collection(GpuSolveCountersSerial.Name)]
public sealed class GpuSolveCountersTests
{
    [Fact]
    public void A_count_adds_up_and_says_when_the_whole_solve_ran_on_the_cpu()
    {
        var c = new GpuSolveCount(OnDevice: 3, GaveUpToCpu: 2, OnHost: 1);
        Assert.Equal(6, c.Total);
        Assert.Equal(3, c.OnCpu);
        Assert.False(c.RanEntirelyOnCpu);
        Assert.Equal("6 linear solves: 3 on the device, 3 on the CPU (2 gave up to the CPU LU, 1 on the host)", c.Describe());

        var allCpu = new GpuSolveCount(0, 4, 0);
        Assert.True(allCpu.RanEntirelyOnCpu);
        Assert.EndsWith("; the whole solve ran on the CPU", allCpu.Describe(), StringComparison.Ordinal);

        Assert.False(default(GpuSolveCount).RanEntirelyOnCpu);   // no GPU solve at all is not "ran on the CPU"
        Assert.Equal("no GPU linear solves", default(GpuSolveCount).Describe());
        Assert.Equal(new GpuSolveCount(1, 1, 0), new GpuSolveCount(5, 3, 2) - new GpuSolveCount(4, 2, 2));
    }

    [Fact]
    public void A_solver_for_an_absent_accelerator_counts_its_solve_on_the_host()
    {
        // OpenCL is the accelerator most test machines lack; where it is present the solve is counted on the device.
        bool present = GpuContext.IsAvailableFor(AcceleratorType.OpenCL);
        GpuSolveCount before = GpuSolveCounters.Snapshot();
        double[]? x = new GpuSparseLinearSolver(accel: AcceleratorType.OpenCL).Solve(Diagonal2(), new[] { 2.0, 4.0 });
        GpuSolveCount d = GpuSolveCounters.Snapshot() - before;
        Assert.NotNull(x);
        Assert.Equal(present ? new GpuSolveCount(1, 0, 0) : new GpuSolveCount(0, 0, 1), d);
    }

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Cuda_dense_and_sparse_solves_are_counted_on_the_device()
    {
        GpuSolveCount before = GpuSolveCounters.Snapshot();
        var a = new double[,] { { 4, 1 }, { 1, 3 } };
        new GpuDenseLinearSolver(AcceleratorType.Cuda).SolveInPlace(a, new[] { 1.0, 2.0 });
        Assert.NotNull(new GpuSparseLinearSolver(accel: AcceleratorType.Cuda).Solve(Diagonal2(), new[] { 2.0, 4.0 }));
        Assert.Equal(new GpuSolveCount(2, 0, 0), GpuSolveCounters.Snapshot() - before);
    }

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void A_cuda_sparse_solve_with_no_usable_diagonal_is_counted_on_the_host()
    {
        // [[0 1] [1 0]]: the Jacobi preconditioner needs every diagonal, so the solver hands this to the CPU LU itself.
        var b = new CsrMatrix.Builder(2);
        b.Add(0, 1, 1.0);
        b.Add(1, 0, 1.0);
        GpuSolveCount before = GpuSolveCounters.Snapshot();
        double[]? x = new GpuSparseLinearSolver(accel: AcceleratorType.Cuda).Solve(b.Build(ensureDiagonal: false), new[] { 5.0, 7.0 });
        Assert.Equal(new[] { 7.0, 5.0 }, x);
        Assert.Equal(new GpuSolveCount(0, 0, 1), GpuSolveCounters.Snapshot() - before);
    }

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void A_cuda_newton_solve_counts_one_linear_solve_per_step()
    {
        GpuSolveCount before = GpuSolveCounters.Snapshot();
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow(new GpuSparseLinearSolver(accel: AcceleratorType.Cuda))
            .Solve(IeeeCases.Case9(), new PowerFlowOptions { MaxIterations = 50, EnforceQLimits = false });
        GpuSolveCount d = GpuSolveCounters.Snapshot() - before;
        Assert.True(r.Converged);
        Assert.Equal(r.Iterations, d.Total);       // one linear solve per Newton step
        Assert.Equal(0, d.OnHost);
    }

    private static CsrMatrix Diagonal2()
    {
        var b = new CsrMatrix.Builder(2);
        b.Add(0, 0, 1.0);
        b.Add(1, 1, 2.0);
        return b.Build();
    }
}
