using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N008 (WS5 follow-up): three PowerFlow sites carried their own copy of a value the shared
/// <c>NumericTolerances</c> table holds (the dense LU and Kron-reduction pivot floor, aNorm x 1e-12, and the Newton
/// fallback BiCGSTAB cap, 4m + 200). They now call the table; the values are identical, so results are bit-identical
/// (covered by the dense / sparse parity and Kron-reduction suites). This pins that the literals do not come back.
/// </summary>
public class SharedToleranceRoutingTests
{
    private static string PowerFlow(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "PowerFlow", file));
    }

    [Theory]
    [InlineData("DenseLuSolver.cs")]
    [InlineData("KronReduction.cs")]
    public void Pivot_floor_comes_from_the_shared_table(string file)
    {
        string src = PowerFlow(file);
        Assert.Contains("NumericTolerances.PivotFloor(aNorm)", src);
        Assert.DoesNotContain("aNorm * 1e-12", src);
    }

    [Fact]
    public void Newton_fallback_krylov_cap_comes_from_the_shared_table()
    {
        string src = PowerFlow("NewtonShell.cs");
        Assert.Contains("NumericTolerances.DefaultKrylovMaxIterations(m)", src);
        Assert.DoesNotContain("4 * m + 200", src);
    }
}
