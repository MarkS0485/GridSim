using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R071 and R096 (October 2026 review): the CPU batched LU copied each system with an int byte offset that overflows
/// above about 2 GB, and the cycle runner accepted a pipelining option (and the CLI a --pipeline flag) that nothing
/// implemented.
/// </summary>
public class BatchedAndCycleOptionTests
{
    private static string Src(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Batched_cpu_solve_equals_the_dense_solver_bit_for_bit()
    {
        // Three 3x3 systems in one batch: each solution must be the DenseLuSolver's, to the bit.
        const int n = 3, count = 3;
        double[] a =
        {
            4, 1, 0, 1, 5, 2, 0, 2, 6,
            2, -1, 0, -1, 2, -1, 0, -1, 2,
            1e-3, 2, 3, 4, 5e2, 6, 7, 8, 9,
        };
        double[] b = { 1, 2, 3, 0.5, -1, 2, 7, -8, 9 };
        var expected = new double[count * n];
        for (int s = 0; s < count; s++)
        {
            var m = new double[n, n];
            var r = new double[n];
            for (int i = 0; i < n; i++) { r[i] = b[s * n + i]; for (int j = 0; j < n; j++) m[i, j] = a[s * n * n + i * n + j]; }
            new DenseLuSolver().SolveInPlace(m, r);
            Array.Copy(r, 0, expected, s * n, n);
        }
        bool[] ok = new CpuBatchedLuSolver().SolveBatch(count, n, (double[])a.Clone(), b);
        Assert.All(ok, Assert.True);
        Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), b.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void Batched_copy_no_longer_uses_an_int_byte_offset()
    {
        string src = Src("src", "GridSim.Core", "PowerFlow", "CpuBatchedLuSolver.cs");
        Assert.DoesNotContain("aOff * sizeof(double)", src);
        Assert.Contains("aFlat.AsSpan(aOff, n * n)", src);
    }

    [Fact]
    public void Cycle_runner_refuses_the_unimplemented_pipeline_option()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            new CycleRunner(IeeeCases.Case9(), new CycleOptions { PipelineBacklog = true }));
        Assert.Contains("not implemented", ex.Message);
    }

    [Fact]
    public void Cli_cycle_pipeline_flag_fails_loud()
    {
        string src = Src("src", "GridSim.Cli", "Program.cs");
        Assert.Contains("error: cycle --pipeline is not implemented", src);
    }
}
