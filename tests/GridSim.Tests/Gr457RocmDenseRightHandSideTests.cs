using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-457: the ROCm dense solver checked only A for NaN, so a NaN or infinite right-hand side went to the device and came
/// back as a non-finite solution. It now refuses b the way the CPU dense LU (its no-ROCm fallback) and the ILGPU dense
/// solver do: InvalidOperationException with the shared wording. The behaviour test runs on any machine (with no ROCm
/// device it exercises the fallback, which must refuse the same way); the source check pins the device path, which only a
/// ROCm machine can execute.
/// </summary>
public class Gr457RocmDenseRightHandSideTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Gr457_a_non_finite_right_hand_side_is_refused_with_the_shared_wording(double bad)
    {
        var a = new double[,] { { 4.0, 1.0 }, { 1.0, 3.0 } };
        var b = new[] { 1.0, bad };
        var ex = Assert.Throws<InvalidOperationException>(() => new RocmDenseLinearSolver().SolveInPlace(a, b));
        Assert.Contains("the right-hand side has a non-finite entry", ex.Message);
        Assert.Contains("at row 1", ex.Message);
    }

    [Fact]
    public void Gr457_the_device_path_checks_b_before_anything_is_sent_to_the_device()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Gpu", "RocmDenseLinearSolver.cs"));

        int check = src.IndexOf("NonFiniteRightHandSide(\"ROCm dense solve\"", StringComparison.Ordinal);
        int upload = src.IndexOf("hipMemcpy(dB, b,", StringComparison.Ordinal);
        Assert.True(check > 0, "the ROCm dense solver must refuse a non-finite b");
        Assert.True(upload > check, "b must be checked before it is copied to the device");
    }
}
