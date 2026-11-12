using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-896: a publish of the CLI from a working tree refuses to ship untracked or git-ignored data files. These pin the
/// guard target in the project file (the clean-checkout rule in code) without running a publish.
/// </summary>
public class GA7E3Batch6Tests
{
    private static string CliProject()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "src", "GridSim.Cli", "GridSim.Cli.csproj");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new InvalidOperationException("src/GridSim.Cli/GridSim.Cli.csproj was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Cli_publish_is_guarded_against_untracked_and_ignored_data_files()
    {
        string csproj = CliProject();
        // The guard runs only before a publish, so builds and test runs are untouched.
        Assert.Matches(new Regex(@"<Target Name=""GuardStrayDataFiles""\s+BeforeTargets=""PrepareForPublish"""), csproj);
        // It asks git for both untracked and git-ignored data json/csv, and leaves the never-shipped inbox out.
        Assert.Contains("git ls-files --others --exclude-standard", csproj, StringComparison.Ordinal);
        Assert.Contains("git ls-files --others --ignored --exclude-standard", csproj, StringComparison.Ordinal);
        Assert.Contains(":(exclude)data/reference-inbox", csproj, StringComparison.Ordinal);
        // It fails loudly, names the files, and offers an explicit override.
        Assert.Contains("<Error Condition=", csproj, StringComparison.Ordinal);
        Assert.Contains("Publish refused", csproj, StringComparison.Ordinal);
        Assert.Contains("GridSimAllowStrayData", csproj, StringComparison.Ordinal);
    }
}
