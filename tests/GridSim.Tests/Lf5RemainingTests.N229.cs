using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N229 (LF5; ruled: adopt with the F-release work): Bundled Networks are bundled by CONTENT, not location alone. Pinned:
/// the committed manifest (data/bundled.sha256) is exactly what tools/gen_bundled_manifest.py gives for the shipped data;
/// the content hash and the manifest grammar; on a data root of the test's own, a shipped file and folder are bundled, and
/// an edited file, an added file (and the folder holding it) and a file the manifest does not list are not; a root with
/// no manifest bundles nothing; the install (test output) root carries the manifest; and, as a process, a 2,869-bus copy
/// placed INSIDE the program's own data folder is refused by the free tier while the shipped file still runs.
/// </summary>
public sealed partial class Lf5RemainingTests
{
    private static readonly string[] ManifestFolders = { "matpower", "uk", "gb-synthetic", "gb-full", "intl", "dist3ph" };

    /// <summary>The manifest the shipped data should have: every file under the bundled folders, hashed by the runtime's
    /// own rule, keyed by its path relative to data/ with '/' separators.</summary>
    private static SortedDictionary<string, string> ExpectedManifest(string dataRoot)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in BundledNetworks.DataFolders)
        {
            string top = Path.Combine(dataRoot, folder);
            if (!Directory.Exists(top)) continue;
            foreach (string f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(dataRoot, f).Replace('\\', '/')] = BundledNetworks.ContentHash(f);
        }
        return map;
    }

    [Fact]
    public void N229_the_committed_manifest_matches_the_shipped_files()
    {
        string data = RepoPath("data");
        string manifestPath = Path.Combine(data, BundledNetworks.ManifestFileName);
        Assert.True(File.Exists(manifestPath), manifestPath);
        IReadOnlyDictionary<string, string>? committed = BundledNetworks.ParseManifest(File.ReadAllLines(manifestPath));
        Assert.NotNull(committed);
        SortedDictionary<string, string> expected = ExpectedManifest(data);
        var problems = new List<string>();
        foreach ((string rel, string hash) in expected)
        {
            if (!committed!.TryGetValue(rel, out string? have)) problems.Add($"not listed: {rel}");
            else if (have != hash) problems.Add($"hash differs: {rel}");
        }
        foreach (string rel in committed!.Keys)
            if (!expected.ContainsKey(rel)) problems.Add($"listed but not shipped: {rel}");
        Assert.True(problems.Count == 0, "data/bundled.sha256 is stale; run 'python tools/gen_bundled_manifest.py' and commit it with the data change:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(40)));
        Assert.True(expected.Count >= 100, $"expected the shipped data (about 120 files), found {expected.Count}");

        // The generator walks the same folders the runtime checks.
        Assert.Equal(ManifestFolders, BundledNetworks.DataFolders);
        string tool = File.ReadAllText(RepoPath("tools", "gen_bundled_manifest.py"));
        Assert.Contains("FOLDERS = [\"matpower\", \"uk\", \"gb-synthetic\", \"gb-full\", \"intl\", \"dist3ph\"]", tool, StringComparison.Ordinal);
        Assert.Contains("data.replace(b\"\\r\\n\", b\"\\n\")", tool, StringComparison.Ordinal);
    }

    [Fact]
    public void N229_the_install_root_carries_the_same_manifest()
    {
        // Every front end's project file copies data/bundled.sha256 with the data; the test project does too, so the
        // install root of this test run (its output folder) is verified like a shipped program's.
        string installed = Path.Combine(AppContext.BaseDirectory, "data", BundledNetworks.ManifestFileName);
        Assert.True(File.Exists(installed), installed);
        IReadOnlyDictionary<string, string>? a = BundledNetworks.ParseManifest(File.ReadAllLines(installed));
        IReadOnlyDictionary<string, string>? b = BundledNetworks.ParseManifest(File.ReadAllLines(RepoPath("data", BundledNetworks.ManifestFileName)));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(b!.OrderBy(kv => kv.Key, StringComparer.Ordinal), a!.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        foreach (string csproj in new[]
        {
            RepoPath("src", "GridSim.Cli", "GridSim.Cli.csproj"), RepoPath("src", "GridSim.Web", "GridSim.Web.csproj"),
            RepoPath("src", "GridSim.Wpf", "GridSim.Wpf.csproj"), RepoPath("tests", "GridSim.Tests", "GridSim.Tests.csproj"),
        })
            Assert.Contains("<Content Include=\"..\\..\\data\\bundled.sha256\" Link=\"data\\bundled.sha256\"", File.ReadAllText(csproj), StringComparison.Ordinal);
    }

    [Fact]
    public void N229_content_hash_is_sha256_with_crlf_read_as_lf()
    {
        string dir = TempDir();
        try
        {
            string crlf = Path.Combine(dir, "crlf.txt"), lf = Path.Combine(dir, "lf.txt"), cr = Path.Combine(dir, "cr.txt"), abc = Path.Combine(dir, "abc.txt");
            File.WriteAllBytes(crlf, Encoding.ASCII.GetBytes("a\r\nb\r\n"));
            File.WriteAllBytes(lf, Encoding.ASCII.GetBytes("a\nb\n"));
            File.WriteAllBytes(cr, Encoding.ASCII.GetBytes("a\rb\r"));   // a lone CR is a byte like any other
            File.WriteAllBytes(abc, Encoding.ASCII.GetBytes("abc"));
            Assert.Equal(BundledNetworks.ContentHash(lf), BundledNetworks.ContentHash(crlf));
            Assert.NotEqual(BundledNetworks.ContentHash(lf), BundledNetworks.ContentHash(cr));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("a\nb\n"))).ToLowerInvariant(), BundledNetworks.ContentHash(crlf));
            // The FIPS 180-2 test vector for "abc".
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", BundledNetworks.ContentHash(abc));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N229_manifest_grammar_is_strict()
    {
        string h = new('a', 64);
        IReadOnlyDictionary<string, string>? ok = BundledNetworks.ParseManifest(new[] { $"{h}  uk/buses.json", "", $"{new string('0', 64)}  matpower/lib/case9.m\r" });
        Assert.NotNull(ok);
        Assert.Equal(2, ok!.Count);
        Assert.Equal(h, ok["uk/buses.json"]);
        foreach (string bad in new[]
        {
            $"{h} uk/buses.json",                    // one space
            $"{h.ToUpperInvariant()}  uk/buses.json", // upper-case hex
            $"{h[..63]}  uk/buses.json",             // 63 digits
            $"{h}  uk\\buses.json",                  // a backslash
            $"{h}  ../uk/buses.json",                // climbs out
            $"{h}  /uk/buses.json",                  // absolute
            $"{h}  ",                                // no path
        })
            Assert.Null(BundledNetworks.ParseManifest(new[] { bad }));
        Assert.Null(BundledNetworks.ParseManifest(new[] { $"{h}  uk/a.json", $"{h}  uk/a.json" }));   // listed twice
    }

    /// <summary>A data root of the test's own: uk/system.json and uk/buses.json, and a manifest listing both.</summary>
    private static string ContentRoot(string dir, bool withManifest = true)
    {
        string root = Path.Combine(dir, "install", "data");
        Directory.CreateDirectory(Path.Combine(root, "uk"));
        File.WriteAllText(Path.Combine(root, "uk", "system.json"), "{ \"name\": \"t\" }\n");
        File.WriteAllText(Path.Combine(root, "uk", "buses.json"), "[]\n");
        if (withManifest)
            File.WriteAllLines(Path.Combine(root, BundledNetworks.ManifestFileName), new[]
            {
                $"{BundledNetworks.ContentHash(Path.Combine(root, "uk", "buses.json"))}  uk/buses.json",
                $"{BundledNetworks.ContentHash(Path.Combine(root, "uk", "system.json"))}  uk/system.json",
            });
        return root;
    }

    [Fact]
    public void N229_shipped_content_is_bundled_and_anything_else_in_place_is_not()
    {
        string dir = TempDir();
        try
        {
            string root = ContentRoot(dir);
            var roots = new[] { root };
            string uk = Path.Combine(root, "uk"), system = Path.Combine(uk, "system.json");
            Assert.True(BundledNetworks.IsBundledByContent(uk, roots));
            Assert.True(BundledNetworks.IsBundledByContent(system, roots));
            Assert.True(BundledNetworks.IsBundledByContent(system.Replace('\\', '/'), roots));

            // A file the manifest does not list, placed in the shipped folder: not bundled, and the folder holding it is
            // not either (its content is no longer what shipped). The location rule alone (N216) still says bundled.
            string big = Path.Combine(uk, "big.m");
            File.WriteAllText(big, "function mpc = big\n");
            Assert.False(BundledNetworks.IsBundledByContent(big, roots));
            Assert.False(BundledNetworks.IsBundledByContent(uk, roots));
            Assert.True(BundledNetworks.IsBundledPath(big, roots));
            Assert.True(BundledNetworks.IsBundledByContent(system, roots));   // the untouched shipped file still is
            File.Delete(big);
            Assert.True(BundledNetworks.IsBundledByContent(uk, roots));

            // A shipped file edited in place (a user network written over it): not bundled, nor its folder.
            File.WriteAllText(system, "{ \"name\": \"edited\" }\n");
            Assert.False(BundledNetworks.IsBundledByContent(system, roots));
            Assert.False(BundledNetworks.IsBundledByContent(uk, roots));

            // A new case folder beside the shipped ones (under a bundled data folder): nothing of it is listed.
            string mine = Path.Combine(root, "intl", "mine");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "system.json"), "{}\n");
            Assert.False(BundledNetworks.IsBundledByContent(mine, roots));
            // An empty folder holds no shipped content.
            Directory.CreateDirectory(Path.Combine(root, "gb-full"));
            Assert.False(BundledNetworks.IsBundledByContent(Path.Combine(root, "gb-full"), roots));
            // Outside the bundled folders the location rule still decides: not bundled.
            Assert.False(BundledNetworks.IsBundledByContent(Path.Combine(dir, "elsewhere", "uk", "system.json"), roots));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N229_a_root_without_a_manifest_bundles_nothing()
    {
        string dir = TempDir();
        try
        {
            string root = ContentRoot(dir, withManifest: false);
            Assert.False(BundledNetworks.IsBundledByContent(Path.Combine(root, "uk", "system.json"), new[] { root }));
            Assert.False(BundledNetworks.IsBundledByContent(Path.Combine(root, "uk"), new[] { root }));
            Assert.True(BundledNetworks.IsBundledPath(Path.Combine(root, "uk"), new[] { root }));   // location alone: yes
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N229_the_real_shipped_files_are_bundled_by_content()
    {
        // The checkout's data root (a data root of this test run, N216) and the install root both verify.
        Assert.True(BundledNetworks.IsBundledPath(RepoPath("data", "gb-full")));
        Assert.True(BundledNetworks.IsBundledPath(RepoPath("data", "matpower", "pegase", "case2869pegase.m")));
        Assert.True(BundledNetworks.IsBundledPath(Path.Combine(AppContext.BaseDirectory, "data", "uk")));
        Assert.True(BundledNetworks.IsBundledByContent(RepoPath("data", "uk", "buses.json"), new[] { RepoPath("data") }));
        Assert.False(BundledNetworks.IsBundledByContent(RepoPath("data", "neso"), new[] { RepoPath("data") }));   // not a bundled folder
    }

    [Fact]
    public void N229_web_catalogue_checks_matpower_and_json_entries_by_content()
    {
        string src = File.ReadAllText(RepoPath("src", "GridSim.Web", "Model", "CaseCatalog.cs"));
        Assert.Contains("CaseKind.BuiltIn => true,", src, StringComparison.Ordinal);
        Assert.Contains("&& GridSim.Core.Licensing.BundledNetworks.IsBundledPath(e.Path),", src, StringComparison.Ordinal);
        Assert.Contains("_ => GridSim.Core.Licensing.BundledNetworks.IsBundledPath(e.Path),", src, StringComparison.Ordinal);
        Assert.DoesNotContain("        _ => true,", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The finding's process-level repro after N216: a 2,869-bus case copied INSIDE the program's own data/matpower
    /// folder (where N216's location rule called it bundled) is refused by the free tier, exit 2; the shipped file beside
    /// it still runs. The copy goes into the CLI's own output folder under a unique name and is removed afterwards.
    /// </summary>
    [Fact]
    public void N229_cli_a_copy_placed_inside_the_install_data_folder_is_not_bundled()
    {
        string cliData = Path.Combine(Path.GetDirectoryName(CliProcess.BuiltCliDll())!, "data", "matpower");
        string shipped = Path.Combine(cliData, "pegase", "case2869pegase.m");
        Assert.True(File.Exists(shipped), shipped);
        string copy = Path.Combine(cliData, "lf5-n229-" + Guid.NewGuid().ToString("N") + ".m");
        string home = TempDir();
        var freeTier = new Dictionary<string, string?> { ["GRIDSIM_LICENCE"] = "not-a-key", ["XDG_DATA_HOME"] = home };
        try
        {
            File.Copy(shipped, copy);
            Assert.True(BundledNetworks.IsBundledPath(copy, new[] { Path.GetDirectoryName(cliData)! }));   // in place (N216)
            CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(freeTier, "dcpf", copy);
            Assert.True(r.ExitCode == 2, r.ToString());
            Assert.Contains("2,869", r.StdErr, StringComparison.Ordinal);

            CliProcess.Result ok = CliProcess.RunBuiltWithEnvironment(freeTier, "dcpf", shipped);
            Assert.True(ok.ExitCode != 2, ok.ToString());
            Assert.DoesNotContain("licence allows", ok.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Clean(home);
        }
    }
}
