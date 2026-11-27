using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 programme (package linux-q): the opt-in <c>pf --max-q-passes &lt;N&gt;</c> and <c>pf --q-revert</c>.
/// <para>
/// The reactive-limit loop stops after <c>PowerFlowOptions.MaxQLimitPasses</c> (100 by default) PV/PQ passes, and a run whose last
/// permitted pass still switched a bus is reported NOT CONVERGED, although the mismatch is tiny. The flags raise that cap
/// and turn on the revert rule.
/// </para>
/// <para>
/// The test case is MATPOWER case9 with two limits changed, so that the loop needs exactly three passes. The pass counts
/// were worked out by an independent Newton-Raphson power flow with the same switching rule (PV bus pinned when its
/// reactive output passes a limit by more than 1e-3 MVAr, one pass per switch, re-solve after each): the generator at bus 2
/// (Qmax set to 5 MVAr) produces 6.654 MVAr in the first solve and is pinned; with bus 2 at 5.000 MVAr the generator at bus 3
/// (Qmax set to -10.2 MVAr) moves from -10.860 to -9.651 MVAr and is pinned in the second pass; the third solve switches
/// nothing. The margins to the limits are 1.65 and 0.55 MVAr, far above the 1e-3 MVAr tolerance. So a cap of 1 or 2
/// passes ends with a switch not yet solved (NOT CONVERGED), and 3 or more converges.
/// </para>
/// </summary>
public sealed class Rc5LinuxQPfOptionsTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-linuxq-" + Guid.NewGuid().ToString("N"));
    private readonly string _case;

    public Rc5LinuxQPfOptionsTests()
    {
        Directory.CreateDirectory(_dir);
        _case = Path.Combine(_dir, "q3.m");
        File.WriteAllText(_case, """
            function mpc = q3
            mpc.version = '2';
            mpc.baseMVA = 100;
            mpc.bus = [
            	1	3	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	2	2	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	3	2	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	4	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	5	1	90	30	0	0	1	1	0	345	1	1.1	0.9;
            	6	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	7	1	100	35	0	0	1	1	0	345	1	1.1	0.9;
            	8	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
            	9	1	125	50	0	0	1	1	0	345	1	1.1	0.9;
            ];
            mpc.gen = [
            	1	72.3	27.03	300	-300	1.04	100	1	250	10	0	0	0	0	0	0	0	0	0	0	0;
            	2	163	6.54	5	-300	1.025	100	1	300	10	0	0	0	0	0	0	0	0	0	0	0;
            	3	85	-10.95	-10.2	-300	1.025	100	1	270	10	0	0	0	0	0	0	0	0	0	0	0;
            ];
            mpc.branch = [
            	1	4	0	0.0576	0	250	250	250	0	0	1	-360	360;
            	4	5	0.017	0.092	0.158	250	250	250	0	0	1	-360	360;
            	5	6	0.039	0.17	0.358	150	150	150	0	0	1	-360	360;
            	3	6	0	0.0586	0	300	300	300	0	0	1	-360	360;
            	6	7	0.0119	0.1008	0.209	150	150	150	0	0	1	-360	360;
            	7	8	0.0085	0.072	0.149	250	250	250	0	0	1	-360	360;
            	8	2	0	0.0625	0	250	250	250	0	0	1	-360	360;
            	8	9	0.032	0.161	0.306	250	250	250	0	0	1	-360	360;
            	9	4	0.01	0.085	0.176	250	250	250	0	0	1	-360	360;
            ];
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private CliProcess.Result Pf(params string[] flags) => CliProcess.RunBuilt(new[] { "pf", _case }.Concat(flags).ToArray());

    [Fact]
    public void The_default_is_100_passes_and_converges_this_case_in_three()
    {
        CliProcess.Result plain = Pf();
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.DoesNotContain("NOT CONVERGED", plain.StdOut, StringComparison.Ordinal);
        // The explicit default is the implicit default: the same bytes.
        CliProcess.Result explicitDefault = Pf("--max-q-passes", "100");
        Assert.Equal(plain.ExitCode, explicitDefault.ExitCode);
        Assert.Equal(plain.StdOut, explicitDefault.StdOut);
        // A cap of 12 changes nothing for a case that settles in three passes.
        CliProcess.Result high = Pf("--max-q-passes", "12");
        Assert.Equal(plain.StdOut, high.StdOut);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void A_cap_below_the_passes_the_case_needs_reports_not_converged(string cap)
    {
        CliProcess.Result r = Pf("--max-q-passes", cap);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("NOT CONVERGED", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cap_of_three_is_exactly_enough()
    {
        CliProcess.Result three = Pf("--max-q-passes", "3");
        Assert.True(three.ExitCode == 0, three.ToString());
        Assert.Equal(Pf().StdOut, three.StdOut);
    }

    [Fact]
    public void Q_revert_converges_the_case_and_the_switch_may_stand_anywhere()
    {
        CliProcess.Result revert = Pf("--q-revert");
        Assert.True(revert.ExitCode == 0, revert.ToString());
        Assert.DoesNotContain("NOT CONVERGED", revert.StdOut, StringComparison.Ordinal);
        // The switch may stand before or after the case name, and together with the cap.
        Assert.Equal(revert.StdOut, CliProcess.RunBuilt("pf", "--q-revert", _case).StdOut);
        Assert.Equal(revert.StdOut, Pf("--q-revert", "--max-q-passes", "50").StdOut);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void A_bad_cap_exits_2_and_names_the_flag(string value)
    {
        CliProcess.Result r = Pf("--max-q-passes", value);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--max-q-passes", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cap_with_no_value_exits_2_and_names_the_flag()
    {
        CliProcess.Result r = Pf("--max-q-passes");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--max-q-passes", r.StdErr, StringComparison.Ordinal);
    }
}
