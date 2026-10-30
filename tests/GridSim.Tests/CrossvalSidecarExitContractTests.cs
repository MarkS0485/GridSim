using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R103 / R308 (October 2026 review): the Python cross-validation sidecars must never pass vacuously and must name
/// what they cannot check. The test run has no pandapower or scipy, so these are source-level contracts; the
/// behaviour itself was exercised by hand on case14 exports (recorded in CHANGELOG 1.95.032).
/// </summary>
public class CrossvalSidecarExitContractTests
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
    public void Impedance_crossval_reports_not_checked_when_nothing_was_compared()
    {
        // R103: an export with no converterParams printed PASS and exited 0 although no comparison ran.
        string py = Tool("crossval_impedance.py");
        Assert.Contains("EXIT_NOT_CHECKED = 77", py);
        Assert.Contains("if checks == 0:", py);
        Assert.Contains("return EXIT_NOT_CHECKED", py);
    }

    [Fact]
    public void Solve_crossval_docstring_no_longer_promises_branch_flow_diffs()
    {
        // R103: the docstring promised branch-flow diffs; the script compares bus Vm / Va only.
        string py = Tool("crossval.py");
        Assert.Contains("Branch flows are NOT compared", py);
        Assert.DoesNotContain("bus voltages/angles\nand branch flows against GridSim's", py);
    }

    [Fact]
    public void Estimation_bad_data_mode_says_it_compares_counts_not_identity()
    {
        // R103: --bad-data claimed to check that both tools 'fingered the same measurement(s)'.
        string py = Tool("crossval_estimation.py");
        Assert.DoesNotContain("fingered the same measurement", py);
        Assert.Contains("does NOT check", py);
    }

    [Theory]
    [InlineData("se_residual_check.py", "\"VaDeg\"", "\"IMagPu\"", "\"IAngDeg\"")]
    [InlineData("crossval_estimation.py", "\"VaDeg\"", "SUPPORTED_KINDS", "unsupported_kinds(used)")]
    public void Estimation_sidecars_handle_angle_kinds_and_refuse_unknown_kinds(string tool, string a, string b, string c)
    {
        // R308: VaDeg / IMagPu / IAngDeg rows fell into the branch-flow branch and crashed with a KeyError.
        string py = Tool(tool);
        Assert.Contains(a, py);
        Assert.Contains(b, py);
        Assert.Contains(c, py);
        Assert.Contains("unsupported measurement kind", py);
        Assert.Contains("return 3", py.Replace("sys.exit(3)", "return 3"));
    }

    [Fact]
    public void Estimation_crossval_checks_kinds_before_the_dependency_check()
    {
        // A bad input must never be reported as "pandapower absent" (a skip).
        string py = Tool("crossval_estimation.py");
        int kinds = py.IndexOf("bad = unsupported_kinds(used)", StringComparison.Ordinal);
        int dep = py.IndexOf("import pandapower  # noqa: F401", StringComparison.Ordinal);
        Assert.True(kinds > 0 && dep > kinds, "the kinds check must run before the pandapower import check");
    }
}
