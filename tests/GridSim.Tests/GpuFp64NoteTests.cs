using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>The CUDA FP64 note names the device class correctly (RC4 CI: the RTX A4000 was called a consumer GPU).</summary>
public sealed class GpuFp64NoteTests
{
    [Theory]
    [InlineData("NVIDIA RTX A4000", "FP64 is likely rate-limited (workstation GPU)")]
    [InlineData("NVIDIA RTX A6000", "FP64 is likely rate-limited (workstation GPU)")]
    [InlineData("NVIDIA RTX 6000 Ada Generation", "FP64 is likely rate-limited (workstation GPU)")]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "FP64 is likely rate-limited (workstation GPU)")]
    [InlineData("Quadro RTX 5000", "FP64 is likely rate-limited (workstation GPU)")]
    [InlineData("Quadro GV100", "FP64 is fast (workstation GPU)")]
    [InlineData("NVIDIA GeForce RTX 4090", "FP64 is likely rate-limited (consumer GPU)")]
    [InlineData("NVIDIA GeForce RTX 3060", "FP64 is likely rate-limited (consumer GPU)")]
    [InlineData("NVIDIA A100-SXM4-80GB", "FP64 is fast (data-centre GPU)")]
    [InlineData("NVIDIA H100 PCIe", "FP64 is fast (data-centre GPU)")]
    [InlineData("", "FP64 is likely rate-limited (consumer GPU)")]
    public void The_cuda_fp64_note_names_the_device_class(string name, string expected) =>
        Assert.Equal(expected, GpuContext.Fp64Note(name));
}
