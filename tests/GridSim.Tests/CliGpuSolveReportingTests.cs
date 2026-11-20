using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4: a GPU solve says where its linear solves ran (the Newton steps can fall back to the CPU LU, or run entirely on
/// the CPU), against the real gridsim binary. The CPU backend's output is unchanged: no GPU line.
/// </summary>
public sealed class CliGpuSolveReportingTests
{
    [Fact]
    public void A_cpu_solve_prints_no_gpu_line()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case9", "--once");
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("GPU:", r.StdOut, StringComparison.Ordinal);
    }

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void A_cuda_solve_reports_where_its_linear_solves_ran()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case9", "--once", "--gpu=cuda");
        Assert.True(r.ExitCode == 0, r.ToString());
        // case9 converges in 4 Newton steps, all on the device (RC4 GPU-vs-CPU sweep).
        Assert.Contains("GPU: 4 linear solves: 4 on the device, 0 on the CPU (0 gave up to the CPU LU, 0 on the host)", r.StdOut, StringComparison.Ordinal);
    }

    [GpuDeviceFact(AcceleratorType.Cuda)]
    public void Gpucheck_reports_where_the_cuda_probe_solves_ran_and_leaves_the_cpu_probe_line_unchanged()
    {
        CliProcess.Result r = CliProcess.RunBuilt("gpucheck");
        Assert.True(r.ExitCode == 0, r.ToString());
        string[] lines = r.StdOut.Split('\n');
        Assert.Contains(lines, l => l.Contains("CUDA dense:", StringComparison.Ordinal) && l.Contains("on the device", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("CUDA sparse:", StringComparison.Ordinal) && l.Contains("on the device", StringComparison.Ordinal));
        Assert.All(lines.Where(l => l.Contains("CPU dense:", StringComparison.Ordinal) || l.Contains("CPU sparse:", StringComparison.Ordinal)),
            l => Assert.DoesNotContain("[", l, StringComparison.Ordinal));
    }
}
