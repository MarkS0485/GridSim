using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F09 / N055 (October 2026): per-verb help and the unknown-flag message, on the real gridsim binary. The binary prints
/// the copy of manual ch. 19 compiled into it; these tests render the chapter on disk with the same code and require
/// the two to agree, so a build that embedded a stale chapter (or none) fails here.
/// </summary>
public sealed class CliVerbHelpProcessTests
{
    private static string Chapter() => File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "manual", "0019 CLI reference.md")).Replace("\r\n", "\n");

    private static string Out(CliProcess.Result r) => r.StdOut.Replace("\r\n", "\n");

    [Theory]
    [InlineData("nminus1 --help", "nminus1", null)]
    [InlineData("help nminus1", "nminus1", null)]
    [InlineData("n-1 -h", "nminus1", null)]
    [InlineData("dcopf case9 --help", "dcopf", null)]
    [InlineData("licence --help", "licence", null)]
    [InlineData("help help", "help", null)]
    [InlineData("bench --estimate --help", "bench", "--estimate")]
    [InlineData("help bench --perf", "bench", "--perf")]
    public void Verb_help_prints_the_embedded_chapter_section_and_exits_0(string line, string verb, string? mode)
    {
        CliProcess.Result r = CliProcess.RunBuilt(line.Split(' '));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(VerbHelp.Render(Chapter(), verb, mode), Out(r));
        Assert.Equal("", r.StdErr);
    }

    [Fact]
    public void Help_with_no_verb_is_the_verb_list()
    {
        CliProcess.Result help = CliProcess.RunBuilt("help");
        CliProcess.Result dashed = CliProcess.RunBuilt("--help");
        Assert.True(help.ExitCode == 0, help.ToString());
        Assert.Equal(dashed.StdOut, help.StdOut);
        Assert.Contains("verb help:  gridsim <verb> --help", help.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_for_a_word_that_is_not_a_verb_is_a_usage_error()
    {
        CliProcess.Result r = CliProcess.RunBuilt("help", "no-such-verb");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("no help for 'no-such-verb'", r.StdErr, StringComparison.Ordinal);
        // `<not a verb> --help` keeps printing the verb list, as before F09.
        CliProcess.Result list = CliProcess.RunBuilt("case9", "--help");
        Assert.True(list.ExitCode == 0, list.ToString());
        Assert.Contains("contingency:", list.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_flag_names_the_verb_help_and_the_manual_not_the_old_stub()
    {
        CliProcess.Result r = CliProcess.RunBuilt("nminus1", "case9", "--no-such-flag");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("error: unknown flag '--no-such-flag' (misspelled?). Run 'gridsim nminus1 --help' for the flags nminus1 takes, or see docs/manual/0019 CLI reference.md.", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/cli-reference ", r.StdErr, StringComparison.Ordinal);

        CliProcess.Result noVerb = CliProcess.RunBuilt("case9", "--no-such-flag");
        Assert.True(noVerb.ExitCode == 2, noVerb.ToString());
        Assert.Contains("Run 'gridsim --help' for the verb list, or see docs/manual/0019 CLI reference.md.", noVerb.StdErr, StringComparison.Ordinal);
    }
}
