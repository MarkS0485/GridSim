using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R090 (October 2026 review): GeneralEigen's biorthonormality guard only threw when |psi^H phi| was ALSO tiny, so a
/// badly converged pair (psi^H phi = 0.5) passed silently and the tolerance parameter had almost no effect; and the
/// documented left-vector inverse iteration on A^T was dead code. The guard now fails on |psi^H phi - 1| above the
/// tolerance, and every pair Decompose returns is biorthonormal to that bound.
/// </summary>
public class BiorthonormalityGuardTests
{
    [Fact]
    public void Every_returned_pair_is_biorthonormal_within_the_bound()
    {
        var a = new double[,] { { -1.0, 2.0, 0.5 }, { -2.0, -1.0, 0.1 }, { 0.3, 0.0, -4.0 } };
        IReadOnlyList<GeneralEigen.Eigenpair> pairs = GeneralEigen.Decompose(a);
        foreach (GeneralEigen.Eigenpair p in pairs)
        {
            System.Numerics.Complex c = System.Numerics.Complex.Zero;
            for (int i = 0; i < p.Right.Length; i++) c += System.Numerics.Complex.Conjugate(p.Left[i]) * p.Right[i];
            Assert.True((c - System.Numerics.Complex.One).Magnitude <= GeneralEigen.BiorthonormalitySlack, $"psi^H phi = {c}");
        }
    }

    [Fact]
    public void Guard_tests_the_distance_from_one_and_the_transpose_path_is_gone()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Numerics", "GeneralEigen.cs"));
        // Test changed 2026-10-19: 1.95.302 (GR-028..GR-062 series, "NaN in the biorthonormality checks fails loud")
        // rewrote the guard as a negated <= so a NaN distance throws; the guard still tests the distance from one.
        Assert.Contains("if (!((c - Complex.One).Magnitude <= biorthoBound))", src);
        Assert.DoesNotContain("c.Magnitude <= biorthoTol", src);
        Assert.DoesNotContain("bool transpose", src);
    }
}
