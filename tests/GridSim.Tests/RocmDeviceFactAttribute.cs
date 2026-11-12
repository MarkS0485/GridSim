using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-885: a fact that runs only when a ROCm (HIP) device is present; otherwise it is SKIPPED with the reason, never
/// passed. The ROCm solvers defer to the CPU reference when no device is present, so a plain <c>[Fact]</c> comparing them
/// with the CPU (<see cref="RocmSolverSeamTests"/>) compares the CPU with itself on such a machine. The probe is
/// <see cref="RocmContext.IsAvailable"/>, the same check the solvers use to choose the device path, so the skip decision
/// and the solver's choice cannot disagree. The ROCm counterpart of <see cref="GpuDeviceFactAttribute"/>.
/// </summary>
public sealed class RocmDeviceFactAttribute : FactAttribute
{
    public RocmDeviceFactAttribute()
    {
        Skip = RocmContext.IsAvailable
            ? null
            : $"no ROCm device: the hipSOLVER / rocSPARSE / rocBLAS paths cannot run on this machine ({RocmContext.DeviceDescription})";
    }
}
