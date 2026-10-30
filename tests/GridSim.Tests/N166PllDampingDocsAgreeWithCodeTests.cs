using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N166: the ch. 19 impedance-stability `--pll-damping` row said the flag exits 2 with a bound asset class, but since
/// 1.95.038 (R244, CHANGELOG "Authored PLL damping kept ... the CLI passes --pll-damping to a bound grid-following
/// class (relaxing the 1.95.036 refusal for that case; grid-forming still exits 2)") the code passes it as an
/// override. The code is the intended behaviour (a deliberate, recorded relaxation); the row was left from 1.95.036.
/// These tests pin docs and code to the same rule: override for grid-following (flag-built or bound class), exit 2
/// with `--converter gfm`.
/// </summary>
public class N166PllDampingDocsAgreeWithCodeTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    [Fact]
    public void The_cli_reference_row_describes_the_override_and_the_gfm_refusal_only()
    {
        string[] lines = File.ReadAllLines(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md"));
        string row = Assert.Single(lines, l => l.StartsWith("| `--pll-damping` |", StringComparison.Ordinal));
        Assert.Contains("override of a bound grid-following asset class", row, StringComparison.Ordinal);
        Assert.Contains("With `--converter gfm`", row, StringComparison.Ordinal);
        Assert.Contains("exits 2", row, StringComparison.Ordinal);
        // The stale 1.95.036 wording must not come back.
        Assert.DoesNotContain("or a bound asset class it exits 2", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_passes_the_damping_to_a_bound_class_and_refuses_it_only_for_gfm()
    {
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("GridFollowingImpedance.FromAssetClass(cls, bus, op, detail, pllBw, ciBw, pllDampingOverride: pllZeta)", src, StringComparison.Ordinal);
        Assert.Contains("if (pllZeta is not null && conv == \"gfm\")", src, StringComparison.Ordinal);
        // No refusal keyed on a bound class for the damping flag.
        Assert.DoesNotContain("pllZeta is not null && cls is not null", src, StringComparison.Ordinal);
    }
}
