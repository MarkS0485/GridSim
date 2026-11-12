using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Batch 7 of the web, UI and build small-limitation fixes. Tests only add to the suite; none changes an existing one.
///
///   GR-892  the .NET runtime's own third-party notices ship beside every self-contained binary;
///   GR-907  no workflow passes a certificate password on a command line;
///   GR-908  the exact list of InternalsVisibleTo grants is pinned;
///   #293    every git-tracked shipped data file is either in the bundled-networks manifest or in a reviewed list.
/// </summary>
public sealed class GA7E3Batch7Tests
{
    private static readonly System.Buffers.SearchValues<char> ManifestSeparators = System.Buffers.SearchValues.Create(" \t");

    private static string Root() => CliProcess.Root();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

    // ------------------------------------------------------------------------------------------------ GR-892 ---

    private static readonly string[] SelfContainedProjects =
    {
        "src/GridSim.Cli/GridSim.Cli.csproj",
        "src/GridSim.App.Desktop/GridSim.App.Desktop.csproj",
        "src/GridSim.Wpf/GridSim.Wpf.csproj",
    };

    [Fact]
    public void Gr892_the_runtime_notices_target_copies_the_runtime_pack_file_and_fails_loud()
    {
        string targets = Read("src/DotnetRuntimeNotices.targets");
        // It runs after publish, only for a self-contained publish, and finds the notices in the runtime pack the SDK resolved.
        Assert.Contains("AfterTargets=\"Publish\"", targets, StringComparison.Ordinal);
        Assert.Contains("'$(SelfContained)' == 'true'", targets, StringComparison.Ordinal);
        Assert.Contains("@(ResolvedRuntimePack)", targets, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App", targets, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.TXT", targets, StringComparison.Ordinal);
        // The copy is named so it cannot be mistaken for (or overwrite) GridSim's own THIRD-PARTY-NOTICES.md.
        Assert.Contains("DestinationFiles=\"$(PublishDir)dotnet-THIRD-PARTY-NOTICES.txt\"", targets, StringComparison.Ordinal);
        // Fail loud twice: no single runtime pack, and no notices file in it.
        Assert.Equal(2, Regex.Count(targets, "<Error "));
        Assert.Contains("!Exists('$(_GridSimNetCoreNotices)')", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr892_every_self_contained_app_project_imports_the_runtime_notices_target()
    {
        foreach (string project in SelfContainedProjects)
            Assert.Contains("<Import Project=\"..\\DotnetRuntimeNotices.targets\" />", Read(project), StringComparison.Ordinal);
    }

    [PublishedOutputFact]
    public void Gr892_the_dotnet_runtime_notices_ship_beside_the_published_binary()
    {
        string dir = Path.GetFullPath(Environment.GetEnvironmentVariable(PublishedOutputFactAttribute.Variable)!, Root());
        Assert.True(Directory.Exists(dir), $"{PublishedOutputFactAttribute.Variable} names a folder that does not exist: {dir}");
        string notices = Path.Combine(dir, "dotnet-THIRD-PARTY-NOTICES.txt");
        Assert.True(File.Exists(notices), "dotnet-THIRD-PARTY-NOTICES.txt (the .NET runtime's notices) is missing from the publish");
        // The real file is tens of kilobytes; a stub or an empty file must not pass.
        Assert.True(new FileInfo(notices).Length > 1000, "dotnet-THIRD-PARTY-NOTICES.txt is suspiciously small");
    }

    // ------------------------------------------------------------------------------------------------ GR-907 ---

    private static IEnumerable<string> WorkflowFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Gr907_no_workflow_passes_a_certificate_password_on_a_command_line()
    {
        string[] files = WorkflowFiles().ToArray();
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            string text = File.ReadAllText(f);
            Assert.False(text.Contains("--certificate-password", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(f)} passes --certificate-password on a command line");
        }
    }

    [Fact]
    public void Gr907_the_package_signing_step_reads_the_password_from_the_environment_and_cleans_up()
    {
        string pub = Read(".github/workflows/publish.yml");
        Assert.Contains("-passin env:SIGNING_CERT_PASSWORD", pub, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf \"$work\"' EXIT", pub, StringComparison.Ordinal);
        Assert.Contains("chmod 700 \"$work\"", pub, StringComparison.Ordinal);
        // The signing command names the passwordless file and no password option of any kind.
        Assert.Contains("dotnet nuget sign \"$p\" --certificate-path \"$pfx\"", pub, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ GR-908 ---

    [Fact]
    public void Gr908_the_internals_visible_to_grants_are_exactly_the_reviewed_list()
    {
        string src = Path.Combine(Root(), "src");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        static bool Generated(string p) =>
            p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        // MSBuild item form, in any project file.
        foreach (string f in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories).Where(p => !Generated(p)))
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, "<InternalsVisibleTo\\s+Include=\"([^\"]+)\""))
                found.Add(Path.GetFileName(f) + " -> " + m.Groups[1].Value);
            Assert.DoesNotContain("InternalsVisibleToAttribute", text, StringComparison.Ordinal);
        }
        // Attribute form, in any source file (a line that starts with the attribute, so comments do not count).
        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(p => !Generated(p)))
            foreach (string line in File.ReadLines(f))
            {
                Match m = Regex.Match(line, "^\\s*\\[assembly:\\s*(?:System\\.Runtime\\.CompilerServices\\.)?InternalsVisibleTo(?:Attribute)?\\(\\s*\"([^\"]+)\"");
                if (m.Success) found.Add(Path.GetFileName(f) + " -> " + m.Groups[1].Value);
            }
        // Shared build files must not grant it to every project at once.
        foreach (string shared in new[] { "Directory.Build.props", "Directory.Build.targets" })
            Assert.DoesNotContain("InternalsVisibleTo", File.ReadAllText(Path.Combine(Root(), shared)), StringComparison.Ordinal);

        // Core's internals are the case-editing engine, used by the case editor and the platform-neutral UI layer
        // (both reviewed: they call CaseSession, CaseOps and SaveGate directly) and by the tests. Gpu, Web, the case
        // editor and the UI layer expose internals to the tests only. No other friend is allowed.
        string[] expected =
        {
            "GridSim.Core.csproj -> GridSim.SchemaEditor",
            "GridSim.Core.csproj -> GridSim.Tests",
            "GridSim.Core.csproj -> GridSim.Ui",
            "GridSim.Gpu.csproj -> GridSim.Tests",
            "GridSim.SchemaEditor.csproj -> GridSim.Tests",
            "GridSim.Ui.csproj -> GridSim.Tests",
            "GridSim.Web.csproj -> GridSim.Tests",
        };
        Assert.Equal(expected, found.ToArray());
    }

    // -------------------------------------------------------------------------------------------- bug 293 ---

    /// <summary>
    /// Folders of shipped data that are not bundled networks, so are not in data/bundled.sha256, each with the reason it
    /// is accepted. Adding a folder here is a review decision: the data ships in the release.
    /// </summary>
    private static readonly (string Folder, string Reason)[] NonNetworkShippedFolders =
    {
        ("data/devices/", "device fleets (the TSGB fleet definition), not a network"),
        ("data/events/", "recorded event dossiers (frequency events), not a network"),
        ("data/library/", "the v2 template library (placeholders for unmodelled sites), not a network"),
        ("data/neso/", "NESO reference series (frequency recordings) used for comparison, not a network"),
        ("data/qsts/", "quasi-static time-series profiles and the feeder reference series, not a network"),
    };

    /// <summary>Extensions the apps ship from data/ (the Content globs of the Cli, App.Desktop and Wpf projects: json and
    /// csv anywhere, m under matpower, and the licence, attribution, README and model-card text files).</summary>
    private static readonly HashSet<string> ShippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".csv", ".m", ".txt", ".md", ".sha256" };

    private static string[] TrackedDataFiles()
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in new[] { "ls-files", "-z", "--", "data/" }) psi.ArgumentList.Add(a);
        try
        {
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"`git ls-files data/` failed in {Root()} (exit {p.ExitCode}): {err.GetAwaiter().GetResult()}");
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"git is not available, so the shipped-data inventory cannot be checked: {ex.Message}");
        }
    }

    [Fact]
    public void Every_tracked_shipped_data_file_is_accounted_for()
    {
        string[] tracked = TrackedDataFiles();
        Assert.True(tracked.Length > 0, "git lists no tracked file under data/: is this a repository checkout?");

        // The bundled-networks manifest: "<sha256>  <path relative to data/>" per line.
        var manifest = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Read("data/bundled.sha256").Split('\n'))
        {
            string l = line.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            int gap = l.IndexOfAny(ManifestSeparators);
            Assert.True(gap > 0, $"bundled.sha256 line has no path: {l}");
            manifest.Add("data/" + l[(gap + 1)..].Trim());
        }
        Assert.NotEmpty(manifest);

        var unaccounted = new List<string>();
        foreach (string file in tracked)
        {
            if (!ShippedExtensions.Contains(Path.GetExtension(file))) continue;
            if (file == "data/bundled.sha256") continue;                       // the manifest itself
            if (manifest.Contains(file)) continue;
            if (NonNetworkShippedFolders.Any(f => file.StartsWith(f.Folder, StringComparison.Ordinal))) continue;
            unaccounted.Add(file);
        }
        Assert.True(unaccounted.Count == 0,
            "tracked shipped data file(s) in neither data/bundled.sha256 nor the reviewed non-network folder list: "
            + string.Join(", ", unaccounted));

        // The reviewed list must not go stale: every listed folder holds at least one tracked shipped file, and every
        // entry has a reason.
        foreach ((string folder, string reason) in NonNetworkShippedFolders)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{folder} has no reason");
            Assert.True(tracked.Any(t => t.StartsWith(folder, StringComparison.Ordinal) && ShippedExtensions.Contains(Path.GetExtension(t))),
                $"{folder} is on the reviewed list but holds no tracked shipped file: remove the stale entry");
        }
    }
}
