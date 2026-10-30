using GridSim.Web.Api;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Round-2 limitation fixes (October 2026) for the command line and the web host: each test names the GR finding it
/// pins. The CLI tests run the real binary (CliProcess.RunBuilt), so they check the exit code and the message a user
/// sees; the web tests call the pure helpers the /ws/sim handler uses.
/// </summary>
public sealed class R2CliAndWebLimitTests
{
    // ---- GR-670: a value flag given twice is refused ------------------------------------------------------------------

    [Fact]
    public void GR670_a_value_flag_given_twice_exits_2_naming_it()
    {
        CliProcess.Result r = CliProcess.RunBuilt("dcopf", "case9", "--top", "5", "--top", "10");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--top given more than once", r.StdErr, StringComparison.Ordinal);
    }

    // ---- GR-683: a single-dash word is refused; a negative number is still a value -----------------------------------

    [Fact]
    public void GR683_a_single_dash_flag_exits_2_and_names_the_double_dash_spelling()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fault", "case9", "-bus", "5");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("unknown option '-bus'", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("'--bus'", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void GR683_a_negative_number_is_still_a_flag_value()
    {
        // "-1" passes the single-dash gate (it is a number) and is refused by --top's own value check (exit 2).
        CliProcess.Result r = CliProcess.RunBuilt("dcopf", "case9", "--top", "-1");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.DoesNotContain("unknown option", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("--top", r.StdErr, StringComparison.Ordinal);
    }

    // ---- GR-202 / GR-688: the bare run takes one case and the global switches only -----------------------------------

    [Theory]
    [InlineData(new object[] { new[] { "case14", "--top", "5" } })]          // a flag of a verb
    [InlineData(new object[] { new[] { "case14", "case30" } })]              // a second case
    [InlineData(new object[] { new[] { "case14", "--dir", "data/uk" } })]    // --dir after a case
    public void GR202_the_bare_run_refuses_anything_beyond_its_case(string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("the case run (no verb) takes one case and the global switches only", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void GR688_enableExport_with_a_spaced_path_is_refused_not_written_to_the_default()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--enableExport", "gr688-out.json");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--enableExport takes its path in the '=' form only: --enableExport=gr688-out.json", r.StdErr, StringComparison.Ordinal);
    }

    // ---- GR-676: an analysis verb refuses a second case ---------------------------------------------------------------

    [Theory]
    [InlineData("pf")]
    [InlineData("dcopf")]
    [InlineData("physics-check")]
    public void GR676_an_analysis_verb_refuses_a_second_case(string verb)
    {
        CliProcess.Result r = CliProcess.RunBuilt(verb, "case9", "case14");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains($"{verb} takes one case argument but got: case9 case14", r.StdErr, StringComparison.Ordinal);
    }

    // ---- GR-701: --trip-at / --trip-window without a trip ------------------------------------------------------------

    [Theory]
    [InlineData("--trip-at")]
    [InlineData("--trip-window")]
    public void GR701_trip_timing_without_a_trip_is_refused(string flag)
    {
        CliProcess.Result r = CliProcess.RunBuilt("transient", "case9", "--minutes", "0.01", flag, "0.5");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains($"{flag} times a trip, but no trip is scheduled", r.StdErr, StringComparison.Ordinal);
    }

    // ---- GR-687: usage banners go to stderr ---------------------------------------------------------------------------

    [Fact]
    public void GR687_a_missing_required_argument_prints_usage_on_stderr_only()
    {
        CliProcess.Result r = CliProcess.RunBuilt("crossval");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("usage: gridsim crossval", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("usage:", r.StdOut, StringComparison.Ordinal);
    }

    // ---- GR-721: error frames carry no server path --------------------------------------------------------------------

    [Theory]
    [InlineData(@"Could not find a part of the path 'C:\srv\gridsim\results\run-1\frames.csv'.", "Could not find a part of the path '<path>'.")]
    [InlineData("Could not find file '/home/gridsim/results/run-1/frames.csv'.", "Could not find file '<path>'.")]
    [InlineData(@"share \\fileserver\gridsim\cases is offline", "share <path> is offline")]
    [InlineData("unknown case 'x'; use one of: case9, case14.", "unknown case 'x'; use one of: case9, case14.")]
    [InlineData("send a 'start' frame first on /ws/sim or use /api/solve.", "send a 'start' frame first on /ws/sim or use /api/solve.")]
    [InlineData("see https://example.org/a/b/c for details", "see https://example.org/a/b/c for details")]
    public void GR721_error_text_replaces_absolute_paths_and_keeps_everything_else(string message, string expected)
    {
        Assert.Equal(expected, ClientErrorText.Scrub(message));
        Assert.Equal(expected, ClientErrorText.For(new InvalidOperationException(message)));
    }

    // ---- GR-734: /ws/sim refuses an out-of-range frame rate -----------------------------------------------------------

    [Theory]
    [InlineData(0.1)]
    [InlineData(5.0)]
    [InlineData(60.0)]
    public void GR734_an_in_range_fps_is_returned_unchanged(double fps)
    {
        Assert.Equal(fps, SimWebSocket.CheckFps(fps, "speed"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.05)]
    [InlineData(60.5)]
    [InlineData(120.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void GR734_an_out_of_range_fps_is_refused_naming_the_range(double fps)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => SimWebSocket.CheckFps(fps, "speed"));
        Assert.Contains("from 0.1 to 60", ex.Message, StringComparison.Ordinal);
    }
}
