using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N266 (LF5): stability --screen refuses --fault-bus / --clear (it ignored them); --clear has an upper bound (1000 ms,
/// the CCT search window, covering GB's slowest back-up clearance of 800 ms); impedance-stability --pll-bw /
/// --pll-damping at 0 or below are refused (as --current-bw is); TwoAxisMachine validates Xd / Xq (the D-DOC-056 rule).
/// </summary>
public sealed partial class Lf5RemainingTests
{
    // ---- stability --screen with --fault-bus / --clear -------------------------------------------------------------------

    [Theory]
    [InlineData("--fault-bus", "--fault-bus", "7")]
    [InlineData("--clear", "--clear", "150")]
    [InlineData("--fault-bus and --clear", "--fault-bus", "7", "--clear", "150")]
    public void N266_screen_refuses_fault_bus_and_clear_on_both_engines(string named, params string[] flags)
    {
        foreach (string[] engine in new[] { Array.Empty<string>(), new[] { "--rms" } })
        {
            CliProcess.Result r = CliProcess.RunBuilt(new[] { "stability", "case9", "--screen" }.Concat(engine).Concat(flags).ToArray());
            AssertExit(2, r);
            Assert.Contains($"error: stability: --screen searches the critical clearing time at every generator bus, so {named} would be ignored", r.StdErr, StringComparison.Ordinal);
            Assert.Equal("", r.StdOut.Trim());
        }
    }

    [Fact]
    public void N266_screen_alone_and_a_single_bus_study_still_run()
    {
        AssertExit(0, CliProcess.RunBuilt("stability", "case9", "--screen"));
        CliProcess.Result one = CliProcess.RunBuilt("stability", "case9", "--fault-bus", "7", "--clear", "150");
        AssertExit(0, one);
        Assert.Contains("3-phase fault at bus 7, cleared after 150 ms", one.StdOut, StringComparison.Ordinal);
    }

    // ---- stability --clear upper bound ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("1000.001")]
    [InlineData("1001")]
    [InlineData("5000")]
    [InlineData("1e9")]
    public void N266_clear_above_1000_ms_exits_2_on_both_engines(string value)
    {
        foreach (string[] args in new[]
        {
            new[] { "stability", "case9", "--clear", value },
            new[] { "stability", "case9", "--rms", "--clear", value },
        })
        {
            CliProcess.Result r = CliProcess.RunBuilt(args);
            AssertExit(2, r);
            Assert.Contains($"error: --clear expected a clearing time of at most 1000 ms", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("Grid Code CC.6.2.2.2.2", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("STABLE", r.StdOut, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void N266_clear_of_exactly_1000_ms_runs_and_the_bound_is_the_cct_window()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--clear", "1000");
        AssertExit(0, r);
        Assert.Contains("cleared after 1000 ms", r.StdOut, StringComparison.Ordinal);
        // The bound is the CCT search window of both engines (1.0 s), and the RMS single study's clear stays inside its run.
        Assert.Equal(1.0, ClassicalInit.CctDefaultMaxSeconds);
        Assert.Equal(1.0, RmsTransientStability.CctDefaultMaxSeconds);
        string defaults = File.ReadAllText(RepoPath("src", "GridSim.Cli", "CliDefaults.cs"));
        Assert.Contains("public const double MaxClearMs = GridSim.Core.Dynamics.Distributed.ClassicalInit.CctDefaultMaxSeconds * 1000.0;", defaults, StringComparison.Ordinal);
        Assert.Contains("public const double RmsStepS = 0.005, RmsDurationS = 3.0;", defaults, StringComparison.Ordinal);
    }

    // ---- impedance-stability --pll-bw / --pll-damping -------------------------------------------------------------------

    [Theory]
    [InlineData("--pll-bw", "0", "error: --pll-bw expected a finite PLL (or grid-forming power-loop) bandwidth in Hz above 0")]
    [InlineData("--pll-bw", "-5", "error: --pll-bw expected a finite PLL (or grid-forming power-loop) bandwidth in Hz above 0")]
    [InlineData("--pll-damping", "0", "error: --pll-damping expected a finite PLL damping ratio above 0")]
    [InlineData("--pll-damping", "-0.1", "error: --pll-damping expected a finite PLL damping ratio above 0")]
    public void N266_non_positive_pll_parameters_exit_2(string flag, string value, string expected)
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", flag, value);
        AssertExit(2, r);
        Assert.Contains(expected, r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N266_non_positive_pll_bandwidth_is_refused_for_the_flag_built_grid_forming_model_too()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--converter", "gfm", "--pll-bw", "0");
        AssertExit(2, r);
        Assert.Contains("error: --pll-bw expected a finite PLL (or grid-forming power-loop) bandwidth in Hz above 0", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N266_positive_pll_parameters_still_run()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--pll-bw", "5", "--pll-damping", "0.7");
        AssertExit(0, r);
    }

    // ---- TwoAxisMachine Xd / Xq ----------------------------------------------------------------------------------------------

    private static TwoAxisMachine.Parameters Two(double xd = 1.8, double xdp = 0.3, double xq = 1.7, double xqp = 0.55) =>
        new(InertiaSeconds: 4.0, DampingPu: 0.0, Xd: xd, Xdp: xdp, Xq: xq, Xqp: xqp, Tdop: 6.0, Tqop: 0.4,
            ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    [Theory]
    [InlineData(0.0, 1.7, "Xd")]
    [InlineData(-1.0, 1.7, "Xd")]
    [InlineData(double.NaN, 1.7, "Xd")]
    [InlineData(double.PositiveInfinity, 1.7, "Xd")]
    [InlineData(0.29, 1.7, "Xd")]    // below X'd = 0.3
    [InlineData(1.8, 0.0, "Xq")]
    [InlineData(1.8, double.NaN, "Xq")]
    [InlineData(1.8, 0.54, "Xq")]    // below X'q = 0.55
    [InlineData(1.8, -2.0, "Xq")]
    public void N266_two_axis_machine_validates_xd_and_xq(double xd, double xq, string param)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Two(xd: xd, xq: xq));
        Assert.Equal(param, ex.ParamName);
        Assert.Contains($"{param} must be a finite number > 0 and at least X'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N266_two_axis_machine_accepts_equality_and_keeps_its_values()
    {
        TwoAxisMachine.Parameters eq = Two(xd: 0.3, xq: 0.55);   // Xd = X'd, Xq = X'q (the reduction identities)
        Assert.Equal(0.3, eq.Xd);
        Assert.Equal(0.55, eq.Xq);
        TwoAxisMachine.Parameters typical = Two();
        Assert.Equal(1.8, typical.Xd);
        Assert.Equal(1.7, typical.Xq);
    }
}
