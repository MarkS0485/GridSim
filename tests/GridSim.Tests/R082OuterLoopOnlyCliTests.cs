using System;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R082 (October 2026): ImpedanceModelDetail.OuterLoopOnly was reachable from code only; impedance-stability now selects
/// it with --outer-loop-only (grid-following), and refuses the inner-loop flags it would ignore and the grid-forming
/// model, whose admittance does not use the detail level.
/// </summary>
public class R082OuterLoopOnlyCliTests
{
    [Fact]
    public void Outer_loop_only_selects_the_detail_level_and_the_case_may_follow_it()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "--outer-loop-only", "case9", "--ppd", "5");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("case IEEE 9-bus (WSCC)", r.StdOut, StringComparison.Ordinal);   // case9 was read, not swallowed
        Assert.Contains("detail OuterLoopOnly", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_flag_the_detail_is_unchanged()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--ppd", "5");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("detail PllOuter", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--pll-bw", "5")]
    [InlineData("--pll-damping", "0.7")]
    [InlineData("--current-bw", "400")]
    public void Outer_loop_only_refuses_an_inner_loop_parameter(string flag, string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--outer-loop-only", flag, value);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains(flag, r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Outer_loop_only_refuses_the_grid_forming_model()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--outer-loop-only", "--converter", "gfm");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("grid-following model only", r.StdErr, StringComparison.Ordinal);
    }
}
