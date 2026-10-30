using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-671 (October 2026): an explicitly named GPU vendor with no usable device is refused with exit 2, run against the
/// real gridsim binary (CliProcess.RunBuilt). It used to print one stderr line, run on the CPU and exit 0, so a script
/// that asked for a GPU run got a CPU run reported as success. Auto-detect (--gpu) still falls back to the CPU, and
/// gpucheck still reports an absent vendor rather than refusing. On a machine that does have the device the refusal
/// cannot be provoked, so the test then checks only that the run succeeded on that device.
/// </summary>
public sealed class CliGpuVendorRefusalTests
{
    [Theory]
    [InlineData("rocm")]
    [InlineData("opencl")]
    [InlineData("cuda")]
    public void A_named_vendor_with_no_device_exits_2_instead_of_running_on_the_cpu(string vendor)
    {
        CliProcess.Result r = CliProcess.RunBuilt("case9", "--once", "--gpu=" + vendor);
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
        if (r.StdErr.Contains($"no usable {vendor} device found", StringComparison.Ordinal))
        {
            Assert.True(r.ExitCode == 2, $"expected exit 2 for an absent {vendor} device\n{r}");
            Assert.Contains($"error: --gpu={vendor} was asked for but no usable device of that kind was found", r.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Converged", r.StdOut, StringComparison.Ordinal);   // nothing was solved on the CPU
        }
        else
        {
            Assert.True(r.ExitCode == 0, $"the {vendor} device answered, so the run should succeed on it\n{r}");
        }
    }

    [Fact]
    public void Auto_detect_still_falls_back_to_the_cpu_with_exit_0()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case9", "--once", "--gpu");
        Assert.True(r.ExitCode == 0, $"--gpu (auto-detect) falls back to the CPU when no device answers\n{r}");
        Assert.DoesNotContain("was asked for but no usable device", r.StdErr, StringComparison.Ordinal);
    }
}
