using System.Text.RegularExpressions;
using GridSim.Core;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F02 (release register section 1): one version number for the whole repository.
///
/// Before F02 only Core and Gda carried a &lt;Version&gt; (bumped by hand, in two places), while Cli, Web, Wpf and Gpu
/// silently defaulted to 1.0.0 and the web OpenAPI document hard-coded "1.0". These tests pin the new contract:
/// the single &lt;Version&gt; lives in Directory.Build.props, no project overrides it, the CHANGELOG has a heading for
/// it, and every GridSim assembly the tests can load carries exactly the same informational version as Core.
/// </summary>
public class VersionContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The one version in Directory.Build.props, checked to be a plain MAJOR.MINOR.PATCH number.</summary>
    private static string PropsVersion()
    {
        string props = File.ReadAllText(Path.Combine(Root(), "Directory.Build.props"));
        MatchCollection m = Regex.Matches(props, @"<Version>([^<]*)</Version>");
        Assert.True(m.Count == 1, $"Directory.Build.props must hold exactly one <Version>; found {m.Count}");
        string v = m[0].Groups[1].Value;
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), v);
        return v;
    }

    [Fact]
    public void No_project_file_sets_its_own_version()
    {
        // Every csproj in the repository (src, tests, tools, probes): a <Version> element there would override the
        // central number for that one project and the binaries would disagree again. PackageReference Version="..."
        // attributes are package versions, not this, and are not matched by the element pattern.
        string root = Root();
        var offenders = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            // A project under a hidden folder (a name starting with '.') is local tool or editor state, such as a
            // temporary working copy, not part of the repository's build.
            .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(segment => segment.StartsWith('.'))
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"<(Version|VersionPrefix|AssemblyVersion|FileVersion|InformationalVersion)>"))
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();
        Assert.True(offenders.Count == 0, "these projects set their own version (remove it; Directory.Build.props owns it): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Changelog_has_a_heading_for_the_current_version()
    {
        // The bump procedure is one file plus its CHANGELOG entry: a version with no heading is a skipped entry.
        string v = PropsVersion();
        string changelog = File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md"));
        Assert.True(Regex.IsMatch(changelog, $@"^## \[{Regex.Escape(v)}\]", RegexOptions.Multiline),
            $"CHANGELOG.md has no '## [{v}]' heading for the version in Directory.Build.props");
    }

    [Fact]
    public void Core_build_info_reports_the_props_version()
    {
        Assert.Equal(PropsVersion(), BuildInfo.Version);
        Assert.StartsWith(BuildInfo.Version, BuildInfo.InformationalVersion, StringComparison.Ordinal);
        Assert.Equal($"GridSim {BuildInfo.Version} (commit {BuildInfo.Commit})", BuildInfo.DisplayLine);
    }

    [Fact]
    public void Every_loadable_gridsim_assembly_carries_the_same_stamp()
    {
        // Core, Gda, Gpu and Web are all referenced by the tests; each must carry the identical informational
        // version (same number, same commit), proving the central property reached every project.
        string core = BuildInfo.InformationalVersion;
        Assert.Equal(core, BuildInfo.ReadInformationalVersion(typeof(GridSim.Gda.GdaBuildOptions).Assembly));
        Assert.Equal(core, BuildInfo.ReadInformationalVersion(typeof(GridSim.Gpu.GpuDenseLinearSolver).Assembly));
        Assert.Equal(core, BuildInfo.ReadInformationalVersion(typeof(GridSim.Web.Api.RestApi).Assembly));
        Assert.Equal(core, BuildInfo.ReadInformationalVersion(typeof(VersionContractTests).Assembly));
    }

    [Theory]
    [InlineData("1.95.111+43e779ed96a88bcdeb4a9711f4950a4aa0d07f68", "1.95.111", "43e779ed")]
    [InlineData("1.95.111", "1.95.111", "unknown")]
    [InlineData("1.95.111+", "1.95.111", "unknown")]
    [InlineData("1.95.111+abc", "1.95.111", "abc")]
    public void Informational_version_splits_into_release_and_commit(string info, string version, string commit)
    {
        Assert.Equal(version, BuildInfo.SplitVersion(info));
        Assert.Equal(commit, BuildInfo.SplitCommit(info));
    }

    [Fact]
    public void Cli_answers_version_before_any_other_processing()
    {
        // Source-level guard (the end-to-end run is in CliProcessSmokeTests): --version is handled before the
        // licence is resolved and before the unknown-flag gate, so it can never be refused or slowed by either.
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs"));
        int version = src.IndexOf("if (rest.Contains(\"--version\"))", StringComparison.Ordinal);
        int licence = src.IndexOf("LicenceGate.Resolve()", StringComparison.Ordinal);
        int gate = src.IndexOf("var knownFlags = new HashSet<string>", StringComparison.Ordinal);
        Assert.True(version > 0, "--version handler not found in Program.cs");
        Assert.True(version < licence && version < gate, "--version must be answered before the licence and the flag gate");
    }
}
