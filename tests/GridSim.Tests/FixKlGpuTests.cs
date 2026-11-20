using ILGPU.Runtime;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>APP-08: after a timed-out CUDA driver probe, the ILGPU context is built without the CUDA backend.</summary>
public class FixKlGpuTests
{
    [Fact]
    public void App08_a_timed_out_probe_is_recognised_and_a_normal_probe_is_not()
    {
        using var never = new ManualResetEventSlim(false);
        string timedOut = GpuContext.ProbeWithTimeout(() => { never.Wait(); return "x"; }, TimeSpan.FromMilliseconds(100));
        never.Set();
        Assert.True(GpuContext.DriverProbeTimedOut(timedOut));
        Assert.False(GpuContext.DriverProbeTimedOut("cuInit=0 driverVer=12"));
        Assert.False(GpuContext.DriverProbeTimedOut("cuInit rc=100"));
    }

    [Fact]
    public void App08_the_context_built_after_a_timed_out_probe_enumerates_no_CUDA_device()
    {
        using var context = GpuContext.ContextFactory(false);
        Assert.DoesNotContain(context.Devices, d => d.AcceleratorType == AcceleratorType.Cuda);
    }
}
