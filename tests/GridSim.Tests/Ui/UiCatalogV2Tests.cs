using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme shell slice (v2 + pickers, 2026-10-20): JSON v2 case manifests in the model catalogue, for the
/// cross-platform app only.
/// <list type="bullet">
/// <item><description>With <see cref="CatalogOptions.IncludeV2Cases"/> on, a valid <c>*.case.json</c> (schema
/// gridsim-case/2) under data/, gda-out/ or results/ is listed as "v2  &lt;file name&gt;", read through the v2 path
/// (<see cref="ModelSource.V2Case"/>), after the pre-solved runs and before the live bridge; an invalid one (wrong tag,
/// not JSON) is skipped and recorded with the reason, never thrown.</description></item>
/// <item><description>WPF's catalogue is unchanged, exactly: the two-argument Discover the WPF window calls, and Discover
/// with the option off, give entry for entry the list a copy of the catalogue code as it was before this slice gives
/// (pinned here as <see cref="WpfCatalogueBeforeV2"/>), on the real app folder and on a folder that holds v2 manifests;
/// and the WPF window still calls the two-argument form.</description></item>
/// </list>
/// Every file is written to a temporary folder; nothing is written near the real data folder.
/// </summary>
public sealed class UiCatalogV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-catalog-v2-" + Guid.NewGuid().ToString("N"));

    public UiCatalogV2Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the OS temp clean-up takes it */ }
    }

    // ---------------------------------------------------------------- fixtures

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // A manifest the catalogue's cheap check passes (it reads the tag only; the loader validates the rest on load).
    private const string GoodManifest = "{\n  // a comment, as the v2 reader allows\n  \"schema\": \"gridsim-case/2\",\n  \"name\": \"t\",\n}\n";

    // An app folder in temp: a fake bundled v1 case (data/uk/system.json), v2 manifests in data/, gda-out/ and results/,
    // two invalid ones, and a gda-out v1 case and a results run so every kind of entry is there.
    private string AppFolderWithV2()
    {
        string app = Path.Combine(_root, "app");
        Write(Path.Combine("app", "data", "uk", "system.json"), "{}");
        Write(Path.Combine("app", "data", "top.case.json"), GoodManifest);
        Write(Path.Combine("app", "data", "mine", "alpha.case.json"), GoodManifest);
        Write(Path.Combine("app", "data", "mine", "wrong-tag.case.json"), "{ \"schema\": \"gridsim-case/1\" }");
        Write(Path.Combine("app", "data", "mine", "broken.case.json"), "{ this is not json");
        Write(Path.Combine("app", "data", "mine", "notes.json"), "{ \"schema\": \"gridsim-case/2\" }");   // not *.case.json: never read
        Write(Path.Combine("app", "gda-out", "zone-a", "system.json"), "{}");
        Write(Path.Combine("app", "gda-out", "zone-a", "zone-a.case.json"), GoodManifest);
        Write(Path.Combine("app", "results", "run1", "exports", "state-000001.json"), "{}");
        Write(Path.Combine("app", "results", "v2runs", "beta.case.json"), GoodManifest);
        return app;
    }

    // ---------------------------------------------------------------- the WPF catalogue, as it was before this slice

    /// <summary>
    /// A copy of ModelCatalog.Discover and ModelCatalog.Disambiguate as they were at 2.0.78, before v2 discovery (with
    /// the app folder as a parameter instead of AppContext.BaseDirectory). The WPF list must equal this, entry for entry.
    /// </summary>
    private static List<ModelSource> WpfCatalogueBeforeV2(string baseDir, AppSettings settings, ICollection<string> skipped)
    {
        var list = new List<ModelSource>();
        string reduced = Path.Combine(baseDir, "data", "uk");
        if (File.Exists(Path.Combine(reduced, "system.json"))) list.Add(new ModelSource("GB reduced  10-zone", reduced));
        string synthetic = Path.Combine(baseDir, "data", "gb-synthetic");
        if (File.Exists(Path.Combine(synthetic, "system.json"))) list.Add(new ModelSource("GB synthetic  distribution depth", synthetic));
        string full = Path.Combine(baseDir, "data", "gb-full");
        if (File.Exists(Path.Combine(full, "system.json"))) list.Add(new ModelSource("GB full  3539-bus transmission", full));
        foreach (string root in NamedDirsUp(baseDir, "gda-out"))
            foreach (string sub in DirectoryScan.Subdirectories(root, skipped))
                if (File.Exists(Path.Combine(sub, "system.json"))) list.Add(new ModelSource($"GDA  {Path.GetFileName(sub)}", sub));
        string matpower = Path.Combine(baseDir, "data", "matpower");
        if (Directory.Exists(matpower))
            foreach (string f in Directory.EnumerateFiles(matpower, "*.m", SearchOption.AllDirectories)
                                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal))
                list.Add(new ModelSource($"MATPOWER  {Path.GetFileNameWithoutExtension(f)}", Path.GetDirectoryName(f)!, f));
        var seenRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in NamedDirsUp(baseDir, "results"))
            foreach (string sub in DirectoryScan.Subdirectories(root, skipped))
                if (seenRuns.Add(sub) && DirectoryScan.Probe(sub, PresolvedReplay.IsRunDirectory, skipped))
                    list.Add(new ModelSource($"Replay  {Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}", sub, ReplayDir: sub));
        if (!string.IsNullOrWhiteSpace(settings.BridgeEndpoint)) list.Add(new ModelSource("Live bridge", Dir: "", Bridge: settings.BridgeEndpoint));

        // Disambiguate, as it was.
        var counts = list.GroupBy(s => s.Label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModelSource>(list.Count);
        foreach (ModelSource s in list)
        {
            string label = s.Label;
            if (counts[label] > 1)
            {
                string path = s.MFile ?? s.ReplayDir ?? s.Dir;
                string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? parentDir = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(trimmed);
                string? parent = string.IsNullOrEmpty(parentDir) ? null : Path.GetFileName(parentDir);
                if (!string.IsNullOrEmpty(parent)) label = $"{label}  ({parent})";
            }
            string unique = label;
            for (int n = 2; !used.Add(unique); n++) unique = $"{label}  #{n}";
            result.Add(unique == s.Label ? s : s with { Label = unique });
        }
        return result;
    }

    private static IEnumerable<string> NamedDirsUp(string baseDir, string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? dir = new(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string g = Path.Combine(dir.FullName, name);
            if (Directory.Exists(g) && seen.Add(g)) yield return g;
        }
    }

    // =================================================================================================================

    [Fact]
    public void WPFs_catalogue_is_exactly_as_before_on_the_real_app_folder_with_the_v2_option_off()
    {
        var settings = new AppSettings();
        var skippedBefore = new List<string>();
        List<ModelSource> before = WpfCatalogueBeforeV2(AppContext.BaseDirectory, settings, skippedBefore);

        // The two-argument call the WPF window makes, and the option off said explicitly: both exactly the old list.
        var skippedWpf = new List<string>();
        List<ModelSource> wpf = ModelCatalog.Discover(settings, skippedWpf);
        var skippedOff = new List<string>();
        List<ModelSource> off = ModelCatalog.Discover(settings, skippedOff, CatalogOptions.Wpf);
        Assert.NotEmpty(before);   // the test app folder carries the bundled data, so the pin is not vacuous
        Assert.Equal(before, wpf);
        Assert.Equal(before, off);
        Assert.Equal(before.Select(s => s.Label), wpf.Select(s => s.Label));
        Assert.Equal(before.Select(s => s.Key), wpf.Select(s => s.Key));
        Assert.Equal(skippedBefore, skippedWpf);
        Assert.Equal(skippedBefore, skippedOff);
        Assert.DoesNotContain(wpf, s => s.V2Case is not null);
        Assert.False(CatalogOptions.Wpf.IncludeV2Cases);
        Assert.False(new CatalogOptions().IncludeV2Cases);   // off unless asked for
        Assert.Null(CatalogOptions.Wpf.BaseDirectory);
    }

    [Fact]
    public void WPFs_catalogue_is_exactly_as_before_on_a_folder_that_holds_v2_manifests_and_with_a_bridge()
    {
        string app = AppFolderWithV2();
        var settings = new AppSettings { BridgeEndpoint = "wss://bridge.example.invalid/ws/bridge" };
        var skippedBefore = new List<string>();
        List<ModelSource> before = WpfCatalogueBeforeV2(app, settings, skippedBefore);
        var skippedOff = new List<string>();
        List<ModelSource> off = ModelCatalog.Discover(settings, skippedOff, new CatalogOptions { BaseDirectory = app });

        Assert.Equal(before, off);
        Assert.Equal(skippedBefore, skippedOff);
        Assert.Empty(skippedOff);   // the invalid manifests are not even read with the option off
        Assert.Equal(new[] { "GB reduced  10-zone", "GDA  zone-a", "Replay  run1", "Live bridge" }, off.Select(s => s.Label));
        Assert.DoesNotContain(off, s => s.V2Case is not null);
    }

    [Fact]
    public void The_WPF_window_still_calls_the_two_argument_catalogue_so_it_never_lists_v2_cases()
    {
        string wpfWindow = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Wpf", "MainWindow.xaml.cs"));
        Assert.Contains("ModelCatalog.Discover(_settings, skippedFolders)", wpfWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogOptions", wpfWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeV2Cases", wpfWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("V2CasesIn", wpfWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void With_v2_on_valid_manifests_are_listed_with_v2_labels_after_the_runs_and_before_the_bridge()
    {
        string app = AppFolderWithV2();
        var settings = new AppSettings { BridgeEndpoint = "wss://bridge.example.invalid/ws/bridge" };
        var skipped = new List<string>();
        List<ModelSource> on = ModelCatalog.Discover(settings, skipped, CatalogOptions.CrossPlatform with { BaseDirectory = app });

        Assert.Equal(new[]
        {
            "GB reduced  10-zone", "GDA  zone-a", "Replay  run1",
            "v2  top.case.json", "v2  alpha.case.json", "v2  zone-a.case.json", "v2  beta.case.json",
            "Live bridge",
        }, on.Select(s => s.Label));

        // Each v2 entry reads its manifest through the v2 path, with the manifest's folder as Dir, keyed by full path.
        ModelSource alpha = on.Single(s => s.Label == "v2  alpha.case.json");
        string alphaPath = Path.Combine(app, "data", "mine", "alpha.case.json");
        Assert.Equal(Path.GetFullPath(alphaPath), alpha.V2Case);
        Assert.Equal(Path.GetFullPath(Path.Combine(app, "data", "mine")), alpha.Dir);
        Assert.Null(alpha.MFile);
        Assert.Null(alpha.ReplayDir);
        Assert.Null(alpha.Bridge);
        Assert.Equal(Path.GetFullPath(alphaPath), alpha.Key);
        Assert.Equal(ModelCatalog.V2CaseSource(alphaPath), alpha);

        // Without the v2 entries, the list is exactly the WPF one.
        var skippedBefore = new List<string>();
        Assert.Equal(WpfCatalogueBeforeV2(app, settings, skippedBefore), on.Where(s => s.V2Case is null).ToList());

        // The two invalid manifests are skipped and recorded, each with its reason; notes.json is never read.
        string wrongTag = Path.Combine(app, "data", "mine", "wrong-tag.case.json");
        string broken = Path.Combine(app, "data", "mine", "broken.case.json");
        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, s => s.StartsWith(broken + ": not valid JSON: ", StringComparison.Ordinal));
        Assert.Contains($"{wrongTag}: not a v2 case manifest: schema \"gridsim-case/1\" (expected \"gridsim-case/2\")", skipped);
        Assert.DoesNotContain(on, s => s.Label.Contains("wrong-tag", StringComparison.Ordinal) || s.Label.Contains("broken", StringComparison.Ordinal));
        Assert.DoesNotContain(on, s => s.Label.Contains("notes", StringComparison.Ordinal));
        Assert.True(CatalogOptions.CrossPlatform.IncludeV2Cases);
    }

    [Fact]
    public void V2CasesIn_skips_missing_roots_lists_each_manifest_once_and_names_same_named_manifests_by_their_folder()
    {
        string a = Write(Path.Combine("lib", "a", "case.case.json"), GoodManifest);
        string b = Write(Path.Combine("lib", "b", "case.case.json"), GoodManifest);
        string lib = Path.Combine(_root, "lib");
        var skipped = new List<string>();

        // The same root twice and a root inside it, plus a root that does not exist: each manifest once, nothing skipped.
        List<ModelSource> found = ModelCatalog.V2CasesIn(new[] { lib, lib, Path.Combine(lib, "a"), Path.Combine(_root, "missing") }, skipped);
        Assert.Empty(skipped);
        Assert.Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b) }, found.Select(s => s.V2Case));
        Assert.Equal(new[] { "v2  case.case.json", "v2  case.case.json" }, found.Select(s => s.Label));

        // Two manifests with the same name are told apart by the folder that holds each (as a MATPOWER file is).
        List<ModelSource> named = ModelCatalog.Disambiguate(found);
        Assert.Equal(new[] { "v2  case.case.json  (a)", "v2  case.case.json  (b)" }, named.Select(s => s.Label));
    }

    [Fact]
    public void ProbeV2Manifest_passes_the_real_fixture_and_names_the_reason_for_everything_else()
    {
        string fixtures = Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "v2", "cases");
        Assert.Null(ModelCatalog.ProbeV2Manifest(Path.Combine(fixtures, "good.case.json")));   // comments and all
        Assert.Equal("not a v2 case manifest: schema \"gridsim-case/1\" (expected \"gridsim-case/2\")",
            ModelCatalog.ProbeV2Manifest(Path.Combine(fixtures, "wrong-schema-version.case.json")));

        Assert.Equal("the file does not exist", ModelCatalog.ProbeV2Manifest(Path.Combine(_root, "nope.case.json")));
        Assert.Equal("not a v2 case manifest: the top level is not a JSON object",
            ModelCatalog.ProbeV2Manifest(Write("array.case.json", "[1, 2]")));
        Assert.Equal("not a v2 case manifest: no \"schema\" text (expected \"gridsim-case/2\")",
            ModelCatalog.ProbeV2Manifest(Write("untagged.case.json", "{ \"name\": \"x\" }")));
        Assert.Equal("not a v2 case manifest: no \"schema\" text (expected \"gridsim-case/2\")",
            ModelCatalog.ProbeV2Manifest(Write("numeric.case.json", "{ \"schema\": 2 }")));
        Assert.StartsWith("not valid JSON: ", ModelCatalog.ProbeV2Manifest(Write("bad.case.json", "{")), StringComparison.Ordinal);

        // A file over the limit is not read whole just to list it.
        string big = Path.Combine(_root, "big.case.json");
        using (FileStream fs = File.Create(big)) fs.SetLength(ModelCatalog.V2ManifestMaxBytes + 1);
        Assert.Equal($"not read: {ModelCatalog.V2ManifestMaxBytes + 1} bytes is more than a case manifest's {ModelCatalog.V2ManifestMaxBytes}-byte limit",
            ModelCatalog.ProbeV2Manifest(big));
    }

    [Fact]
    public void CaseFolderSource_is_a_v1_folder_labelled_Case_and_keyed_by_its_full_path()
    {
        string dir = Path.Combine(_root, "my-case") + Path.DirectorySeparatorChar;
        ModelSource s = ModelCatalog.CaseFolderSource(dir);
        Assert.Equal("Case  my-case", s.Label);
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "my-case"), s.Dir);
        Assert.Null(s.V2Case);
        Assert.Null(s.MFile);
        Assert.Null(s.ReplayDir);
        Assert.Null(s.Bridge);
        Assert.Throws<ArgumentException>(() => ModelCatalog.CaseFolderSource(" "));
    }
}
