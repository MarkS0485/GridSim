using GridSim.Core.IO.Matpower;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// B37: the power-flow divergence stop (<c>PowerFlowOptions.DivergeLimitPu</c>, <c>pf --diverge-limit</c>, <c>pf --max-iter</c>).
/// <para>
/// The divergent fixture is a two-bus case: bus 1 the slack, bus 2 a 1600 MW + 300 MVAr load on a line of r = 0.02 and
/// x = 0.1 pu (two identical parallel lines of r = 0.04 and x = 0.2, which also keeps the case off the radial
/// backward/forward sweep; 100 MVA base), started from the file's own voltage at bus 2 (0.6 pu, -30 degrees). The most a line of
/// x = 0.1 pu can carry at 1 pu is about 1 / (2 x) = 5 pu, so 16 pu has no solution and the mismatch can never fall
/// below about 11 pu. Newton from that start, checked against an independent two-variable Newton iteration written
/// separately from GridSim (with the same 0.4 step damping), has largest mismatches 12.8 and then 14.1 pu: the
/// starting mismatch is above the 10 pu limit, so the limit in force is the starting mismatch, and the second
/// evaluation is above it, so the default stops there with status Diverged after one iteration. With the stop off the
/// same run goes on to the iteration cap.
/// </para>
/// <para>
/// A converging case is unchanged: the stop only reads the mismatch already computed and ends the loop, so case9's
/// output is the same bytes with the stop at its default, at a different limit and off.
/// </para>
/// </summary>
public sealed class Rc5B37DivergeStopTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-b37-" + Guid.NewGuid().ToString("N"));
    private readonly string _case;

    public Rc5B37DivergeStopTests()
    {
        Directory.CreateDirectory(_dir);
        _case = Path.Combine(_dir, "diverge2.m");
        File.WriteAllText(_case, """
            function mpc = diverge2
            mpc.version = '2';
            mpc.baseMVA = 100;
            mpc.bus = [
            	1	3	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	2	1	1600	300	0	0	1	0.6	-30	345	1	1.1	0.9;
            ];
            mpc.gen = [
            	1	0	0	9999	-9999	1.0	100	1	10000	0	0	0	0	0	0	0	0	0	0	0	0;
            ];
            mpc.branch = [
            	1	2	0.04	0.2	0	10000	10000	10000	0	0	1	-360	360;
            	1	2	0.04	0.2	0	10000	10000	10000	0	0	1	-360	360;
            ];
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CliProcess.Result Pf(string target, params string[] flags) => CliProcess.RunBuilt(new[] { "pf", target }.Concat(flags).ToArray());

    [Fact]
    public void A_converging_case_gives_the_same_bytes_with_the_stop_on_at_another_limit_and_off()
    {
        CliProcess.Result plain = Pf("case9");
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.DoesNotContain("DIVERGED", plain.StdOut, StringComparison.Ordinal);
        Assert.Equal(plain.StdOut, Pf("case9", "--diverge-limit", "off").StdOut);
        Assert.Equal(plain.StdOut, Pf("case9", "--diverge-limit", "10").StdOut);
        Assert.Equal(plain.StdOut, Pf("case9", "--diverge-limit", "1000").StdOut);
        // --max-iter at the engine's own default is the same run.
        Assert.Equal(plain.StdOut, Pf("case9", "--max-iter", "100").StdOut);
    }

    [Fact]
    public void A_divergent_fixture_stops_early_and_reports_DIVERGED_with_exit_3()
    {
        CliProcess.Result r = Pf(_case);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("DIVERGED", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT CONVERGED", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("in 1 iteration(s)", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Diverge_limit_off_runs_to_the_iteration_cap_as_before()
    {
        CliProcess.Result r = Pf(_case, "--diverge-limit", "off", "--max-iter", "20");
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.DoesNotContain("DIVERGED", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("NOT CONVERGED (MaxIterations) in 20 iteration(s)", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_above_the_peak_mismatch_never_stops_the_run()
    {
        CliProcess.Result r = Pf(_case, "--diverge-limit", "1e12", "--max-iter", "5");
        Assert.DoesNotContain("DIVERGED", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("inf")]
    public void A_bad_limit_exits_2_and_names_the_flag(string value)
    {
        CliProcess.Result r = Pf("case9", "--diverge-limit", value);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--diverge-limit", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    public void A_bad_max_iter_exits_2_and_names_the_flag(string value)
    {
        CliProcess.Result r = Pf("case9", "--max-iter", value);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--max-iter", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_library_defaults_to_10_pu_and_refuses_a_limit_that_is_not_above_zero()
    {
        Assert.Equal(10.0, new PowerFlowOptions().DivergeLimitPu);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerFlowOptions { DivergeLimitPu = 0.0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerFlowOptions { DivergeLimitPu = -1.0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerFlowOptions { DivergeLimitPu = double.NaN });
        Assert.Equal(double.PositiveInfinity, new PowerFlowOptions { DivergeLimitPu = double.PositiveInfinity }.DivergeLimitPu);
    }

    [Fact]
    public void The_library_reports_Diverged_and_off_reports_MaxIterations()
    {
        var model = MatpowerParser.ParseFile(_case);
        PowerFlowResult stopped = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { MaxIterations = 20 });
        Assert.False(stopped.Converged);
        Assert.Equal(PowerFlowStatus.Diverged, stopped.Status);
        Assert.True(stopped.Iterations < 20);
        PowerFlowResult off = new NewtonRaphsonPowerFlow().Solve(model,
            new PowerFlowOptions { MaxIterations = 20, DivergeLimitPu = double.PositiveInfinity });
        Assert.Equal(PowerFlowStatus.MaxIterations, off.Status);
        Assert.Equal(20, off.Iterations);
    }
}
