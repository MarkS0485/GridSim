using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R002 (October 2026 review): the CLI's bundled-network list lacked <c>data/gb-full</c>, so the free tier refused
/// the shipped 3,539-bus GB case on every CLI verb (exit 2) while the Web and WPF apps loaded it. All three front-ends
/// now ask <see cref="BundledNetworks"/>; these tests pin the list and guard against a front-end growing its own again.
/// </summary>
public class BundledNetworksTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    [Fact]
    public void The_shipped_gb_full_case_is_bundled()
    {
        string gbFull = Path.Combine(RepoRoot(), "data", "gb-full");
        Assert.True(Directory.Exists(gbFull), $"shipped case missing: {gbFull}");
        Assert.True(BundledNetworks.IsBundledPath(gbFull));
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(gbFull, "buses.json")));
        Assert.True(BundledNetworks.IsBundledJsonCase("gb-full"));
    }

    [Fact]
    public void Every_shipped_network_folder_on_disk_resolves_as_bundled()
    {
        // Every folder named in the list that exists in this checkout must resolve as bundled, by directory and by a
        // file inside it. (gb-reduced is listed for installs that ship it; it is not required to exist here.)
        string data = Path.Combine(RepoRoot(), "data");
        int checkedFolders = 0;
        foreach (string folder in BundledNetworks.DataFolders)
        {
            string dir = Path.Combine(data, folder);
            if (!Directory.Exists(dir)) continue;
            checkedFolders++;
            Assert.True(BundledNetworks.IsBundledPath(dir), dir);
            string? file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (file is not null) Assert.True(BundledNetworks.IsBundledPath(file), file);
        }
        Assert.True(checkedFolders >= 4, $"expected matpower, uk, gb-synthetic and gb-full on disk; found {checkedFolders}");
    }

    [Theory]
    [InlineData("C:/work/gda-out/zone-1")]               // a materialised GDA model is a user network
    [InlineData("C:/work/my-case.m")]
    [InlineData("C:/work/data/gb-fullish")]              // whole-segment match only
    [InlineData("C:/work/data/ukx/buses.json")]          // the old substring test let this through
    [InlineData("C:/work/gb-full")]                      // must sit under a data/ folder
    [InlineData("")]
    [InlineData(null)]
    public void User_paths_are_not_bundled(string? path)
    {
        Assert.False(BundledNetworks.IsBundledPath(path));
    }

    [Fact]
    public void Matching_ignores_case_and_separator()
    {
        // Moved 2026-10 (N216, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the two paths this test pinned
        // as bundled sit under an arbitrary C:\Repo\DATA\... tree, not under the shipped data root. Accepting ANY
        // data/<bundled folder> segment wherever it sits is the licence bypass N216 reports (a user network copied under
        // such a folder escaped the tier limit), so the expectation encoded the defect. Before:
        //   Assert.True(BundledNetworks.IsBundledPath(@"C:\Repo\DATA\GB-FULL\buses.json"));
        //   Assert.True(BundledNetworks.IsBundledPath("C:/Repo/data/matpower/case118.m"));
        // After: those two are NOT bundled (asserted in N216BundledPathAnchorTests), and the case / separator rule the
        // test is named for is proved on the shipped data root itself (case ignored on Windows and macOS, either separator).
        string root = BundledNetworks.DataRoots[0];
        string gbFull = Path.Combine(root, "gb-full", "buses.json");
        Assert.True(BundledNetworks.IsBundledPath(gbFull.Replace('\\', '/')));
        // Moved 2026-10 (B2, final batch; LF5 N229, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): since
        // N229 "bundled" also checks the file content against the manifest, and case118.m lives at data/matpower/lib/ (the
        // old path has no file). Before -> after: Path.Combine(root, "matpower", "case118.m") -> Path.Combine(root,
        // "matpower", "lib", "case118.m"). The case and separator matching this test is about is unchanged.
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(root, "matpower", "lib", "case118.m")));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.True(BundledNetworks.IsBundledPath(gbFull.ToUpperInvariant()));
        Assert.False(BundledNetworks.IsBundledPath(@"C:\Repo\DATA\GB-FULL\buses.json"));
        Assert.False(BundledNetworks.IsBundledPath("C:/Repo/data/matpower/case118.m"));
    }

    [Fact]
    public void Json_case_ids_are_exact()
    {
        Assert.False(BundledNetworks.IsBundledJsonCase("gda-zone-1"));
        Assert.False(BundledNetworks.IsBundledJsonCase("GB-FULL"));
        Assert.False(BundledNetworks.IsBundledJsonCase(null));
    }

    [Fact]
    public void No_front_end_keeps_its_own_bundled_folder_list()
    {
        // Anti-drift: the CLI, WPF and Web bundled checks must delegate to BundledNetworks, not list folders again.
        string root = RepoRoot();
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop's bundled check (IsBundledSource) moved from
        // src/GridSim.Wpf/MainWindow.xaml.cs to src/GridSim.Ui/Hosting/SimHost.cs; the loop reads it there, unchanged.
        // Strengthened: the window is still checked for a folder list of its own (below), so neither file may keep one.
        foreach (string rel in new[] { "src/GridSim.Cli/Gated.cs", "src/GridSim.Ui/Hosting/SimHost.cs", "src/GridSim.Web/Model/CaseCatalog.cs" })
        {
            string src = File.ReadAllText(Path.Combine(root, rel));
            Assert.Contains("BundledNetworks.", src);
            Assert.DoesNotContain("\"/data/gb-", src);
            Assert.DoesNotContain("\"/data/matpower/\"", src);
        }
        string window = File.ReadAllText(Path.Combine(root, "src/GridSim.Wpf/MainWindow.xaml.cs"));
        Assert.DoesNotContain("\"/data/gb-", window);
        Assert.DoesNotContain("\"/data/matpower/\"", window);
        Assert.DoesNotContain("IsBundledPath(", window);   // the window asks the host, which asks BundledNetworks
    }
}
