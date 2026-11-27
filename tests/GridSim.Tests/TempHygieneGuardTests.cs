using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Test runs must not leave temporary folders behind (thousands had built up and filled the disk). Every test makes its
/// temporary folders and files through <see cref="TestTemp"/>, which records each one and deletes it at the end of the run.
/// The guard below refuses a test source file that makes a temporary path any other way; the remaining tests pin the
/// helper's own behaviour.
/// </summary>
public sealed class TempHygieneGuardTests
{
    // The three calls that make a temporary path. Spelt in pieces so this file does not match its own scan.
    private static readonly string[] Calls = { "GetTemp" + "Path(", "GetTemp" + "FileName(", "CreateTemp" + "Subdirectory(" };

    /// <summary>
    /// The lines that may still name the temporary folder directly, by file, with how many lines each covers. None of
    /// them leaves a folder behind per run: each is a path that is never created, an assertion about where a path lies,
    /// the product's own crash-log folder (which holds the user's real crash logs and must never be swept), or one fixed
    /// name that is overwritten on every run.
    /// </summary>
    private static readonly (string File, string Snippet, int Lines, string Why)[] Allowed =
    {
        ("tests/GridSim.App.Tests/StartupLoadTests.cs", "\"gone.m\"", 1, "a missing file; never created"),
        ("tests/GridSim.App.Tests/UiSmokeTests.cs", "a.StartsWith(", 1, "an assertion about where a path lies"),
        ("tests/GridSim.Tests/BreakingChangeValidateTests.cs", "\"escape.json\"", 1, "asserts nothing was written there"),
        ("tests/GridSim.Tests/BugHunt1101LicensingTests.cs", "\"not-shipped\"", 1, "a missing file; never created"),
        ("tests/GridSim.Tests/CrashLogTests.cs", "\"GridSim\", \"crash\"", 2, "the product's crash-log folder; the user's real crash logs live there"),
        ("tests/GridSim.Tests/Gr696GdaModeFlagTests.cs", "\"--root\",", 3, "a refused command line; nothing is written"),
        ("tests/GridSim.Tests/TestTempContainmentTests.cs", "Path.GetFullPath(Path.GetTempPath())", 2, "the containment tests name the temporary folder itself to prove it is never safe to delete"),
        ("tests/GridSim.Tests/Gr883LicenceIsolationTests.cs", "Assert.StartsWith(", 1, "an assertion about where a path lies"),
        ("tests/GridSim.Tests/JournalTestSupport.cs", "\"gridsim-journal-isolation\"", 1, "one fixed folder shared by the CLI child processes"),
        ("tests/GridSim.Tests/Rc5Wp11RpcV2ProcessTests.cs", "Assert.DoesNotContain(", 1, "an assertion that output names no temporary path"),
        ("tests/GridSim.Tests/Rc5Wp12UserModelRouteTests.cs", "\"../x\",", 1, "a list of refused paths; nothing is written"),
        ("tests/GridSim.Tests/WeccCliFamilyRunTests.cs", "$\"gridsim-wecc-cli-{family}.", 2, "fixed-name diagnostic files, overwritten on every run"),
        ("tests/GridSim.Tests/WeccPerformanceNoteTests.cs", "\"gridsim-wecc-perf.txt\"", 1, "a fixed-name diagnostic file, overwritten on every run"),
        ("tests/GridSim.Tests/WeccRegcBReecDTests.cs", "\"gridsim-epri-regcb-protocol.txt\"", 1, "a fixed-name diagnostic file, overwritten on every run"),
        ("tests/GridSim.Tests/WeccRegfmTests.cs", "\"gridsim-gfl-gfm-study.txt\"", 1, "a fixed-name diagnostic file, overwritten on every run"),
    };

    private static readonly string[] Projects = { "GridSim.Tests", "GridSim.App.Tests", "GridSim.Scripting.Tests" };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_test_makes_its_temporary_paths_through_TestTemp()
    {
        string root = Root();
        string self = nameof(TempHygieneGuardTests) + ".cs";
        var counts = new Dictionary<(string, string), int>();
        var problems = new List<string>();
        foreach (string project in Projects)
        {
            string dir = Path.Combine(root, "tests", project);
            Assert.True(Directory.Exists(dir), "test project folder not found: tests/" + project);
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (Path.GetFileName(file) == self) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!Calls.Any(c => lines[i].Contains(c, StringComparison.Ordinal))) continue;
                    (string File, string Snippet, int Lines, string Why) allow = Allowed.FirstOrDefault(a => a.File == rel && lines[i].Contains(a.Snippet, StringComparison.Ordinal));
                    if (allow.File is null)
                        problems.Add($"{rel}:{i + 1}: makes a temporary path without TestTemp (use TestTemp.Under, TestTemp.NewDir, TestTemp.NewFile or TestTemp.CreateSubdirectory): {lines[i].Trim()}");
                    else
                        counts[(allow.File, allow.Snippet)] = counts.GetValueOrDefault((allow.File, allow.Snippet)) + 1;
                }
            }
        }
        foreach ((string file, string snippet, int expected, string _) in Allowed)
        {
            int seen = counts.GetValueOrDefault((file, snippet));
            if (seen != expected)
                problems.Add($"{file}: the allowance for {snippet} covers {expected} line(s) but {seen} line(s) match; review the new use and convert it to TestTemp.");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Under_returns_the_path_the_tests_always_used_and_records_its_top_level_entry()
    {
        string name = "gridsim-hygiene-" + Guid.NewGuid().ToString("N");
        string path = TestTemp.Under(name, "inner", "file.txt");
        Assert.Equal(Path.Combine(Path.GetTempPath(), name, "inner", "file.txt"), path);
        Assert.Contains(Path.GetFullPath(Path.Combine(Path.GetTempPath(), name)), TestTemp.RecordedPaths);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(Path.GetTempPath(), name, "inner")), TestTemp.RecordedPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../gridsim-outside")]
    [InlineData("./gridsim-x")]
    public void Under_refuses_a_name_that_is_not_a_child_of_the_temporary_folder(string first)
    {
        Assert.Throws<ArgumentException>(() => TestTemp.Under(first));
        Assert.DoesNotContain(Path.GetFullPath(Path.GetTempPath()), TestTemp.RecordedPaths);
    }

    [Fact]
    public void Under_refuses_a_rooted_path()
    {
        string rooted = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gridsim-rooted"));
        Assert.Throws<ArgumentException>(() => TestTemp.Under(rooted));
    }

    [Fact]
    public void A_tracked_folder_exists_until_it_is_disposed_and_is_gone_after()
    {
        string path;
        using (TrackedTempDir d = TestTemp.NewDir("gridsim-hygiene-"))
        {
            path = d.Path;
            Assert.True(Directory.Exists(path));
            Directory.CreateDirectory(Path.Combine(path, "sub"));
            File.WriteAllText(Path.Combine(path, "sub", "locked.txt"), "x");
            File.SetAttributes(Path.Combine(path, "sub", "locked.txt"), FileAttributes.ReadOnly);
            Assert.Contains(Path.GetFullPath(path), TestTemp.RecordedPaths);
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void The_sweep_deletes_recorded_folders_and_files_and_reports_nothing_left()
    {
        string dir = TestTemp.Under("gridsim-hygiene-" + Guid.NewGuid().ToString("N"));
        string file = TestTemp.Under("gridsim-hygiene-" + Guid.NewGuid().ToString("N") + ".txt");
        string missing = TestTemp.Under("gridsim-hygiene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a", "b"));
        File.WriteAllText(Path.Combine(dir, "a", "b", "c.txt"), "x");
        File.WriteAllText(file, "x");

        IReadOnlyList<string> left = TestTemp.Sweep(new[] { dir, file, missing });

        Assert.Empty(left);
        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void NewFile_and_CreateSubdirectory_are_recorded()
    {
        string file = TestTemp.NewFile();
        DirectoryInfo dir = TestTemp.CreateSubdirectory("gridsim-hygiene-");
        try
        {
            Assert.True(File.Exists(file));
            Assert.True(dir.Exists);
            Assert.Contains(file, TestTemp.RecordedPaths);
            Assert.Contains(dir.FullName, TestTemp.RecordedPaths);
        }
        finally
        {
            Assert.Empty(TestTemp.Sweep(new[] { file, dir.FullName }));
        }
    }
}
