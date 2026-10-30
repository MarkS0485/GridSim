using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): a UCTE-DEF .uct file is a case wherever a .m or .raw file is, loaded in one place
/// (Gated.Ucte). Runs the built CLI.
/// </summary>
public class UcteCliTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "ucte", name);

    private static string RepoFile(string rel) => File.ReadAllText(Path.Combine(CliProcess.Root(), rel)).Replace("\r\n", "\n");

    [Fact]
    public void A_uct_file_runs_as_a_case_and_its_notes_go_to_stderr()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Fixture("small.uct"), "--once");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("note: small.uct: 1 X-node(s) (boundary nodes) are buses", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_takes_a_uct_input_and_proves_the_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-ucte-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("convert", Fixture("small.uct"), Path.Combine(dir, "small.m"));
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("round trip: the written case solves bit-identically", r.StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Allow_unmodelled_reaches_a_uct_file_and_a_declared_frequency_applies()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Fixture("small.uct"), "--once", "--allow-unmodelled", "--frequency-hz", "50");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("had no effect", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("assumed 50 Hz", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_uct_load_goes_through_one_place_and_is_documented()
    {
        Assert.DoesNotContain("UcteParser", RepoFile("src/GridSim.Cli/Program.cs"), StringComparison.Ordinal);
        Assert.Contains("UcteParser.ParseFile(path,", RepoFile("src/GridSim.Cli/Gated.cs"), StringComparison.Ordinal);
        CliProcess.Result help = CliProcess.RunBuilt("--help");
        Assert.Contains("or a UCTE-DEF .uct file", help.StdOut, StringComparison.Ordinal);
        Assert.Contains("gridsim convert <input.m|input.raw|input.uct|case-dir|case>", RepoFile("docs/manual/0019 CLI reference.md"), StringComparison.Ordinal);
        Assert.Contains("## UCTE-DEF files", RepoFile("docs/manual/0002 Loading networks.md"), StringComparison.Ordinal);
    }
}
