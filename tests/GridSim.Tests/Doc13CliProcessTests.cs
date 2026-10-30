using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026): the command-line side of stream 13 and its small follow-ups, run against the real gridsim
/// binary (<see cref="CliProcess.RunBuilt"/>): the exit code, the message and the files a script sees. Every test is new.
/// </summary>
public sealed class Doc13CliProcessTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc13-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    private static string Data(params string[] parts) => Path.Combine(new[] { CliProcess.Root(), "data" }.Concat(parts).ToArray());

    // ---- D-DOC-151: an existing folder that is not a case is loaded, not called an unknown case -------------------------

    [Theory]
    [InlineData("pf", "")]
    [InlineData("dcpf", "")]
    [InlineData("dcopf", "")]
    [InlineData("fault", "--bus 1")]
    [InlineData("physics-check", "")]
    public void D151_an_existing_folder_with_no_case_files_is_an_input_error_naming_what_is_missing(string verb, string extra)
    {
        string dir = TempDir();   // exists, holds nothing: like `gridsim pf docs`
        try
        {
            string[] args = new[] { verb, dir }.Concat(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            CliProcess.Result r = CliProcess.RunBuilt(args);
            AssertExit(1, r);
            Assert.DoesNotContain("unknown case", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("buses.json", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D151_an_unknown_name_is_still_a_usage_error()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "nosuchcase-doc13");
        AssertExit(2, r);
        Assert.Contains("unknown case 'nosuchcase-doc13'", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void D151_a_real_case_folder_still_loads()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", Data("uk"));
        AssertExit(0, r);
    }

    // ---- D-DOC-154: --enableExport names the file after the case, not the flattened path ------------------------------

    [Fact]
    public void D154_a_matpower_path_gives_the_file_stem_as_the_export_name()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Data("matpower", "lib", "case14.m"), "--enableExport");
        AssertExit(0, r);
        string expected = Path.Combine("results", "exports", "case14-solve.json");
        Assert.Contains(expected, r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("matpower_lib", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D154_a_case_folder_gives_its_folder_name()
    {
        CliProcess.Result r = CliProcess.RunBuilt("--dir", Data("uk") + Path.DirectorySeparatorChar, "--enableExport");
        AssertExit(0, r);
        Assert.Contains(Path.Combine("results", "exports", "uk-solve.json"), r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D154_a_built_in_name_is_used_as_before()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case9", "--enableExport");
        AssertExit(0, r);
        Assert.Contains(Path.Combine("results", "exports", "case9-solve.json"), r.StdOut, StringComparison.Ordinal);
    }
}
