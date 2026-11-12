using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-684: the report label "WSCC-9" is what makes the classical stability engines use the built-in Anderson-Fouad machine
/// data (by bus number). The label fell back to the case's own name, so a USER case whose system name is "WSCC-9" silently
/// got the built-in data on its own buses. The built-in data is now chosen by the case argument alone (case9 / ieee9), and a
/// user case with that name is labelled "WSCC-9 (user case)". Pinned against the real binary: the built-in case keeps the
/// plain label; a folder case named "WSCC-9" gets the suffixed label and not the Anderson-Fouad reactances line.
/// </summary>
public sealed class Gr684UserCaseNamedWscc9Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr684-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Gr684_the_built_in_case_keeps_the_plain_wscc9_label_and_the_anderson_fouad_data()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--screen");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("*  WSCC-9", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("(user case)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("X'd = Anderson-Fouad", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr684_a_user_folder_case_named_wscc9_is_labelled_a_user_case_and_does_not_take_the_built_in_data()
    {
        string src = Path.Combine(CliProcess.Root(), "data", "uk");
        Directory.CreateDirectory(_dir);
        foreach (string f in Directory.GetFiles(src, "*.json"))
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        string sys = Path.Combine(_dir, "system.json");
        string text = File.ReadAllText(sys);
        string renamed = Regex.Replace(text, @"""name""\s*:\s*""[^""]*""", "\"name\": \"WSCC-9\"");
        Assert.NotEqual(text, renamed);
        File.WriteAllText(sys, renamed);

        CliProcess.Result r = CliProcess.RunBuilt("stability", _dir, "--screen");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("*  WSCC-9 (user case)", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("X'd = Anderson-Fouad", r.StdOut, StringComparison.Ordinal);
    }
}
