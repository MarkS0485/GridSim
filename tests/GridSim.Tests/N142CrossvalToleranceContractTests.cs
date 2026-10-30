using System;
using System.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N142 (October 2026, the N005 follow-up): crossval_impedance.py and crossval_smallsignal.py default to 4096 machine
/// epsilons (about 9.09e-13) instead of 1e-6 / 1e-9, measured against the worst agreement on the bundled cases (25.55 eps
/// for the impedance rebuild; 0.2165 eps of max_rel / cond(V) for the eigenvalues). The test run has no SciPy, so these
/// are source-level contracts on the defaults, which must never go back up.
/// </summary>
public class N142CrossvalToleranceContractTests
{
    private static string Tool(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "tools", "crossval", name);
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    [Fact]
    public void Impedance_crossval_defaults_are_4096_machine_epsilons_relative_and_absolute()
    {
        string py = Tool("crossval_impedance.py");
        Assert.Contains("EPS = 2.0 ** -52", py, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_TOL_EPS = 4096", py, StringComparison.Ordinal);
        Assert.Contains("ap.add_argument(\"--tol\", type=float, default=DEFAULT_RTOL,", py, StringComparison.Ordinal);
        Assert.Contains("ap.add_argument(\"--atol\", type=float, default=DEFAULT_ATOL,", py, StringComparison.Ordinal);
        Assert.DoesNotContain("default=1e-6", py, StringComparison.Ordinal);
        Assert.DoesNotContain("default=1e-9", py, StringComparison.Ordinal);
    }

    [Fact]
    public void Smallsignal_crossval_default_rtol_is_4096_machine_epsilons()
    {
        string py = Tool("crossval_smallsignal.py");
        Assert.Contains("EPS = 2.0 ** -52", py, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_RTOL_EPS = 4096", py, StringComparison.Ordinal);
        Assert.Contains("ap.add_argument(\"--rtol\", type=float, default=DEFAULT_RTOL,", py, StringComparison.Ordinal);
        Assert.DoesNotContain("default=1e-6", py, StringComparison.Ordinal);
        // The conditioning guard itself is unchanged.
        Assert.Contains("guard = args.rtol * max(1.0, condV)", py, StringComparison.Ordinal);
    }

    [Fact]
    public void The_defaults_are_far_tighter_than_the_old_ones()
    {
        double eps = Math.Pow(2, -52);
        double tol = 4096 * eps;
        Assert.Equal(9.094947017729282e-13, tol);
        Assert.True(tol < 1e-9 && tol < 1e-6);
    }
}
