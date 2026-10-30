using GridSim.Gpu;
using ILGPU.Runtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A fact that runs only when GridSim's GPU context has a live accelerator of the named type; otherwise it is SKIPPED
/// with the reason, never passed. The GPU solvers fall back to their CPU counterparts when no device is present, so a
/// plain <c>[Fact]</c> that compares a GPU solver with the CPU reference passes on a machine with no device while checking
/// only the fallback. A test that is meant to check the kernels themselves uses this attribute: on a machine without the
/// device it is reported as skipped (the kernels were not checked there), and where it runs the kernels did run.
/// The probe is <see cref="GpuContext.IsAvailableFor"/>, the same check the solvers use to choose the device path, so the
/// skip decision and the solver's choice cannot disagree. The probe never throws (a missing driver reads as absent).
/// </summary>
public sealed class GpuDeviceFactAttribute : FactAttribute
{
    public GpuDeviceFactAttribute(AcceleratorType accelerator)
    {
        Accelerator = accelerator;
        Skip = GpuContext.IsAvailableFor(accelerator)
            ? null
            : $"no {accelerator} accelerator: the GPU kernels cannot run on this machine ({GpuContext.DescriptionFor(accelerator)})";
    }

    /// <summary>The ILGPU accelerator type the test needs.</summary>
    public AcceleratorType Accelerator { get; }
}
