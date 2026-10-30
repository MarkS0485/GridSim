using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F07 (release register section 1): THIRD-PARTY-NOTICES.md must name every NuGet package GridSim ships.
///
/// The shipped set is computed, not listed by hand: the test assembly's own .deps.json holds the full resolved
/// dependency graph, and walking it from the GridSim projects the tests reference (Core, Gda, Gpu, Web; the CLI and the
/// desktop app add no packages of their own) reaches exactly the packages those projects bring, transitively. Test-only
/// packages (xUnit, the test SDK, Mvc.Testing) hang off the test project, not off a GridSim project, so they are not
/// reached. A package upgrade or a new dependency that is not added to the notices fails here.
/// The Avalonia desktop app (GridSim.App, GridSim.App.Desktop; UI programme 0.4) is not referenced by this test
/// project, so its packages are read from those projects' committed packages.lock.json files instead.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>"Name/Version" of every package reachable from a GridSim project in this test run's dependency graph.</summary>
    private static SortedSet<string> ShippedPackages()
    {
        string depsPath = Path.Combine(AppContext.BaseDirectory, "GridSim.Tests.deps.json");
        Assert.True(File.Exists(depsPath), $"dependency graph not found: {depsPath}");
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        JsonElement libraries = deps.RootElement.GetProperty("libraries");
        // The single target (".NETCoreApp,Version=v10.0") maps "Name/Version" -> { dependencies: { Name: Version } }.
        JsonElement target = deps.RootElement.GetProperty("targets").EnumerateObject().Single().Value;

        var shipped = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (JsonProperty lib in libraries.EnumerateObject())
            if (lib.Name.StartsWith("GridSim.", StringComparison.Ordinal)
                && !lib.Name.StartsWith("GridSim.Tests/", StringComparison.Ordinal)   // the test project brings test-only packages
                && lib.Value.GetProperty("type").GetString() == "project")
                queue.Enqueue(lib.Name);
        Assert.True(queue.Count >= 4, $"expected the GridSim projects in the graph, found {queue.Count}");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            if (!seen.Add(key)) continue;
            if (libraries.TryGetProperty(key, out JsonElement info) && info.GetProperty("type").GetString() == "package")
                shipped.Add(key);
            if (target.TryGetProperty(key, out JsonElement node) && node.TryGetProperty("dependencies", out JsonElement d))
                foreach (JsonProperty dep in d.EnumerateObject())
                    queue.Enqueue($"{dep.Name}/{dep.Value.GetString()}");
        }
        return shipped;
    }

    /// <summary>
    /// "Name/Version" of every package (Direct or Transitive, not Project) in one project's packages.lock.json, across
    /// all of its targets. The file must exist: Directory.Build.props turns lock files on for every project.
    /// </summary>
    private static SortedSet<string> LockedPackages(string lockPath)
    {
        Assert.True(File.Exists(lockPath), $"lock file not found (RestorePackagesWithLockFile is on): {lockPath}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(lockPath));
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty target in doc.RootElement.GetProperty("dependencies").EnumerateObject())
            foreach (JsonProperty dep in target.Value.EnumerateObject())
            {
                string type = dep.Value.GetProperty("type").GetString()!;
                if (type == "Project") continue;
                Assert.True(type is "Direct" or "Transitive" or "CentralTransitive",
                    $"{lockPath}: unexpected lock entry type '{type}' for {dep.Name}");
                packages.Add($"{dep.Name}/{dep.Value.GetProperty("resolved").GetString()}");
            }
        return packages;
    }

    [Fact]
    public void Every_shipped_package_is_named_with_its_version_in_the_notices()
    {
        string notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        SortedSet<string> shipped = ShippedPackages();
        // Parquet.Net (Gda) and ILGPU (Gpu) are the two direct dependencies; if the walk misses them it is broken.
        Assert.Contains(shipped, p => p.StartsWith("Parquet.Net/", StringComparison.Ordinal));
        Assert.Contains(shipped, p => p.StartsWith("ILGPU/", StringComparison.Ordinal));

        // Test extended 2026-10-20 (UI programme 0.4): the Avalonia projects are shipped projects. The test project does
        // not reference them (it would pull Avalonia into every test run), so their packages are read from each
        // project's committed packages.lock.json instead: every Direct and Transitive package, in every target (the
        // plain net10.0 graph and any per-RID graph a later publish adds), must have its row too. GridSim.Ui is read the
        // same way, so a package added to it later is caught here as well.
        foreach (string project in new[] { "src/GridSim.Ui", "src/GridSim.App", "src/GridSim.App.Desktop" })
            foreach (string p in LockedPackages(Path.Combine(Root(), project, "packages.lock.json")))
                shipped.Add(p);
        // The admitted packages themselves (approved 2026-10-20); if the lock-file read misses them it is broken.
        foreach (string pkg in new[] { "Avalonia/", "Avalonia.Desktop/", "SkiaSharp/", "HarfBuzzSharp/", "CommunityToolkit.Mvvm/" })
            Assert.Contains(shipped, p => p.StartsWith(pkg, StringComparison.Ordinal));

        var missing = shipped.Where(p =>
        {
            string[] parts = p.Split('/');
            return !notices.Contains($"| {parts[0]} | {parts[1]} |", StringComparison.Ordinal);
        }).ToList();
        Assert.True(missing.Count == 0,
            "THIRD-PARTY-NOTICES.md has no table row '| Name | Version |' for: " + string.Join(", ", missing));
    }

    /// <summary>
    /// N125 (Ruling R1, 2026-10-04: kept bundled with attribution): the Edinburgh-archive GB files keep a clear
    /// attribution notice in THIRD-PARTY-NOTICES (which ships beside every binary) and in data/matpower/gb/README.md,
    /// with the citation the archive asks for; and every file the notice names really is bundled.
    /// </summary>
    [Fact]
    public void Bundled_edinburgh_archive_networks_carry_their_attribution()
    {
        string root = Root();
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md")).Replace("\r\n", "\n");
        // Whitespace collapsed (the README wraps its prose), so a citation split over two lines still matches.
        string readme = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(root, "data", "matpower", "gb", "README.md")), @"\s+", " ");
        const string citation = "W. A. Bukhsh, K. McKinnon, \"Network data of real transmission networks\", April 2013";
        Assert.Contains("## Bundled network data (attribution)", notices, StringComparison.Ordinal);
        foreach (string text in new[] { notices, readme })
        {
            Assert.Contains(citation, text, StringComparison.Ordinal);
            Assert.Contains("Belivanis", text, StringComparison.Ordinal);
            Assert.Contains("research and educational purposes", text, StringComparison.Ordinal);
        }
        foreach (string f in new[] { "GBnetwork.m", "GBreducednetwork.m", "LICENSE-PyPSA-GB.txt" })
        {
            Assert.Contains($"data/matpower/gb/{f}", notices, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, "data", "matpower", "gb", f)), $"{f} is named in the notices but not bundled");
        }
        Assert.Contains("Decided (N125, Ruling R1, 2026-10-04): kept bundled, with attribution.", readme, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_and_licence_ship_with_every_app()
    {
        // The CLI, web and desktop projects copy both files to their output (so every publish and the container carry
        // them); the Gda NuGet package packs the notices beside its licence.
        string root = Root();
        // Test extended 2026-10-20 (UI programme 0.4): the Avalonia projects are shipped projects. The desktop head is the
        // executable that ships, so it carries both files; GridSim.App and GridSim.Ui are libraries inside it (their
        // packages are covered by Every_shipped_package_is_named_with_its_version_in_the_notices).
        foreach (string proj in new[] { "src/GridSim.Cli/GridSim.Cli.csproj", "src/GridSim.Web/GridSim.Web.csproj", "src/GridSim.Wpf/GridSim.Wpf.csproj", "src/GridSim.App.Desktop/GridSim.App.Desktop.csproj" })
        {
            string text = File.ReadAllText(Path.Combine(root, proj));
            Assert.Contains("<Content Include=\"..\\..\\THIRD-PARTY-NOTICES.md\"", text, StringComparison.Ordinal);
            Assert.Contains("<Content Include=\"..\\..\\LICENSE.md\"", text, StringComparison.Ordinal);
        }
        string gda = File.ReadAllText(Path.Combine(root, "src/GridSim.Gda/GridSim.Gda.csproj"));
        Assert.Contains("<None Include=\"..\\..\\THIRD-PARTY-NOTICES.md\" Pack=\"true\"", gda, StringComparison.Ordinal);
    }
}
