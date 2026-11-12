using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-696: gda's modes (--fleet, --schema, --all-gsps, one build) each ignored the flags that do not apply to them
/// without a word. Such a flag is now refused, exit 2, naming the flag and the mode, before the lake is read (so these
/// runs need no GDA root). Pinned per mode, plus --dense / --sparse without --solve and --as-of with a reader that takes
/// no register date. The source pin checks the refusal runs first in RunGda.
/// </summary>
public sealed class Gr696GdaModeFlagTests
{
    private static CliProcess.Result Gda(params string[] args) => CliProcess.RunBuilt(new[] { "gda" }.Concat(args).ToArray());

    [Theory]
    [InlineData("--fleet", "--dno", "ukpn", "error: gda --fleet does not use --dno")]
    [InlineData("--fleet", "--solve", null, "error: gda --fleet does not use --solve")]
    [InlineData("--schema", "--as-of", "2024-01-01", "error: gda --schema does not use --as-of")]
    [InlineData("--all-gsps", "--licence", "EMIDS", "error: gda --all-gsps does not use --licence")]
    [InlineData("--all-gsps", "--gsp", "X", "error: gda --all-gsps does not use --gsp")]
    [InlineData("--all-gsps", "--out", "x", "error: gda --all-gsps does not use --out")]
    [InlineData("--all-gsps", "--dense", null, "error: gda --all-gsps does not use --dense")]
    public void Gr696_a_flag_the_mode_ignores_is_refused(string mode, string flag, string? value, string expected)
    {
        var args = new List<string> { "--root", Path.GetTempPath(), mode };
        if (mode == "--schema") args.Add("x.csv");
        args.Add(flag);
        if (value is not null) args.Add(value);
        CliProcess.Result r = Gda(args.ToArray());
        Assert.Equal(2, r.ExitCode);
        Assert.Contains(expected, r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--dense")]
    [InlineData("--sparse")]
    public void Gr696_an_engine_flag_without_solve_is_refused(string engine)
    {
        CliProcess.Result r = Gda("--root", Path.GetTempPath(), engine);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"error: gda {engine} chooses the engine of --solve, and there is no --solve", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ukpn")]
    [InlineData("npg")]
    public void Gr696_as_of_with_a_reader_that_takes_no_register_date_is_refused(string dno)
    {
        CliProcess.Result r = Gda("--root", Path.GetTempPath(), "--dno", dno, "--as-of", "2024-01-01");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"the {dno} reader takes no register date", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr696_the_mode_check_runs_before_anything_else_in_RunGda()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        int body = src.IndexOf("void RunGda(List<string> a)\n{", StringComparison.Ordinal);
        Assert.True(body > 0);
        int check = src.IndexOf("GdaModeIgnoredFlag(a)", body, StringComparison.Ordinal);
        int firstRead = src.IndexOf("FlagString(a, \"--root\")", body, StringComparison.Ordinal);
        Assert.True(check > body && check < firstRead, "the GR-696 mode check must come before gda reads any flag");
    }
}
