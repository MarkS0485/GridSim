using System.IO;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Settings;

namespace GridSim.Ui.Models;

/// <summary>
/// A selectable grid model. Either a multi-file JSON case directory (<see cref="Dir"/>), a single MATPOWER
/// <c>.m</c> file (<see cref="MFile"/>, when non-null it takes precedence), a pre-solved run directory to
/// replay (<see cref="ReplayDir"/>, a folder of exported grid states - takes precedence over the others), or
/// a live GDA bridge endpoint (<see cref="Bridge"/>, a <c>ws://...</c> URL - takes precedence over all: the
/// server streams the topology + solved state, so no local <see cref="Dir"/> is read).
/// <para>UI programme ASSETS slice 5-view: <see cref="V2Case"/>, a JSON v2 case manifest (<c>gridsim-case/2</c>), read by
/// the v2 loader (<see cref="GridSim.Core.IO.Json.V2CaseLoader"/>) instead of the v1 folder reader; <see cref="Dir"/> is
/// then the manifest's folder. A MATPOWER file, a replay folder and a bridge endpoint each take precedence over it, as
/// over the v1 folder.</para>
/// </summary>
public sealed record ModelSource(
    string Label, string Dir, string? MFile = null, string? ReplayDir = null, string? Bridge = null, string? V2Case = null)
{
    /// <summary>
    /// The source's identity: the bridge URL, else the full path of the file or folder it reads. Labels are for people
    /// and can repeat (two runs both called <c>run1</c> in different folders); this is what the app persists and
    /// de-duplicates on. Paths compare case-insensitively, as Windows does.
    /// </summary>
    public string Key => Bridge ?? FullPathOrSelf(MFile ?? ReplayDir ?? V2Case ?? Dir);

    private static string FullPathOrSelf(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;   // an unparseable path is still a usable (if less tidy) identity
        }
    }

    /// <summary>True when two sources read the same model (same <see cref="Key"/>).</summary>
    public bool SameSourceAs(ModelSource other) => string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Discovers the grid models the dashboard can show: the bundled reduced GB case,
/// plus any GDA model materialised into a local <c>gda-out/</c> folder (each a
/// standard multi-file JSON case the loader understands). The materialised networks
/// are git-ignored and produced by <c>gridsim gda --out</c> on an authorised
/// machine  -  the dashboard just reads whatever is present locally.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Find every model the picker can offer. The gda-out/ and results/ folders found above the app are scanned
    /// through <see cref="DirectoryScan"/>: a folder that cannot be read (access denied, a dropped network share)
    /// is skipped and recorded in <paramref name="skipped"/> ("folder: reason") instead of throwing out of
    /// MainWindow.OnLoaded and leaving the window with no models (R307). The caller logs the skipped folders.
    /// </summary>
    public static List<ModelSource> Discover(AppSettings? settings = null, ICollection<string>? skipped = null)
        => Discover(settings, skipped, CatalogOptions.Wpf);

    /// <summary>
    /// UI programme shell slice (v2 + pickers, 2026-10-20): <see cref="Discover(AppSettings?, ICollection{string}?)"/>
    /// with <paramref name="options"/>. With <see cref="CatalogOptions.IncludeV2Cases"/> off (the default, and the only
    /// way the WPF window ever calls it) the list is exactly the one the two-argument overload gives, entry for entry;
    /// with it on (the cross-platform app), the JSON v2 case manifests found by <see cref="V2CasesIn"/> under the same
    /// folders join the list, after the pre-solved runs and before the live bridge.
    /// </summary>
    public static List<ModelSource> Discover(AppSettings? settings, ICollection<string>? skipped, CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        settings ??= AppSettings.Load();
        skipped ??= new List<string>();
        var list = new List<ModelSource>();
        // The folder the bundled data/ sits in and the gda-out/ and results/ folders are looked for above. Only a test
        // changes it (to a temporary folder, so it never writes near the real data folder).
        string baseDir = options.BaseDirectory ?? AppContext.BaseDirectory;

        string reduced = Path.Combine(baseDir, "data", "uk");
        if (File.Exists(Path.Combine(reduced, "system.json")))
            list.Add(new ModelSource("GB reduced  10-zone", reduced));

        string synthetic = Path.Combine(baseDir, "data", "gb-synthetic");
        if (File.Exists(Path.Combine(synthetic, "system.json")))
            list.Add(new ModelSource("GB synthetic  distribution depth", synthetic));

        string full = Path.Combine(baseDir, "data", "gb-full");
        if (File.Exists(Path.Combine(full, "system.json")))
            list.Add(new ModelSource("GB full  3539-bus transmission", full));

        foreach (string root in GdaOutDirs(baseDir))
            foreach (string sub in DirectoryScan.Subdirectories(root, skipped))
                if (File.Exists(Path.Combine(sub, "system.json")))   // File.Exists never throws
                    list.Add(new ModelSource($"GDA  {Path.GetFileName(sub)}", sub));

        // Committed MATPOWER .m libraries (PEGASE / RTS / NESTA / classic IEEE / benchmarks).
        string matpower = Path.Combine(baseDir, "data", "matpower");
        if (Directory.Exists(matpower))
            foreach (string f in Directory.EnumerateFiles(matpower, "*.m", SearchOption.AllDirectories)
                                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal))
                list.Add(new ModelSource(
                    $"MATPOWER  {Path.GetFileNameWithoutExtension(f)}", Path.GetDirectoryName(f)!, f));

        // Pre-solved runs (transient / gda-replay exports) under any nearby results/ folder.
        var seenRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in NamedDirsUp(baseDir, "results"))
            foreach (string sub in DirectoryScan.Subdirectories(root, skipped))
                // IsRunDirectory lists sub/exports, which can itself be unreadable: probe it under the guard too.
                if (seenRuns.Add(sub) && DirectoryScan.Probe(sub, PresolvedReplay.IsRunDirectory, skipped))
                    list.Add(ReplaySource(sub));

        // JSON v2 case manifests (the cross-platform app only; WPF cannot load them, so its list never has them). They
        // are looked for under the folders scanned above: data/ beside the app, and every gda-out/ and results/ found.
        if (options.IncludeV2Cases)
        {
            var roots = new List<string> { Path.Combine(baseDir, "data") };
            roots.AddRange(GdaOutDirs(baseDir));
            roots.AddRange(NamedDirsUp(baseDir, "results"));
            list.AddRange(V2CasesIn(roots, skipped));
        }

        // Live GDA bridge: a streamed source (no local files) when an endpoint is configured.
        if (!string.IsNullOrWhiteSpace(settings.BridgeEndpoint))
            list.Add(new ModelSource("Live bridge", Dir: "", Bridge: settings.BridgeEndpoint));

        return Disambiguate(list);
    }

    /// <summary>
    /// Give every source a label no other source shares, so the picker, the last-used match and a launch model text
    /// can tell them apart. A repeated label (two GDA or replay folders with the same base name under different
    /// parents) gets its parent folder appended in brackets; a label still repeated after that gets a running number.
    /// Unique labels are left exactly as they were.
    /// </summary>
    public static List<ModelSource> Disambiguate(List<ModelSource> sources)
    {
        var counts = sources.GroupBy(s => s.Label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModelSource>(sources.Count);
        foreach (ModelSource s in sources)
        {
            string label = s.Label;
            if (counts[label] > 1)
            {
                // A v2 manifest, like a MATPOWER file, is named by the folder that holds the file itself (its Dir is
                // that folder, whose own parent would name two manifests in sibling folders the same). No WPF source
                // has a V2Case, so WPF's labels are unchanged by this.
                string? parent = ParentName(s.MFile ?? s.ReplayDir ?? s.V2Case ?? s.Dir);
                if (!string.IsNullOrEmpty(parent)) label = $"{label}  ({parent})";
            }
            string unique = label;
            for (int n = 2; !used.Add(unique); n++) unique = $"{label}  #{n}";
            result.Add(unique == s.Label ? s : s with { Label = unique });
        }
        return result;
    }

    // The name of the folder that holds a path's last segment, or null when there is none.
    private static string? ParentName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? null : Path.GetFileName(parent);
    }

    /// <summary>
    /// UI programme ASSETS slice 5-view: a source for a JSON v2 case manifest (<c>gridsim-case/2</c>), read by the v2
    /// loader; its asset overlay, if the manifest lists one, is carried to the ASSETS tab (carried, not used by the
    /// solver). Shell slice (v2 + pickers, 2026-10-20): the catalogue finds v2 manifests itself when asked to
    /// (<see cref="CatalogOptions.IncludeV2Cases"/>, the cross-platform app only) through <see cref="V2CasesIn"/>, which
    /// makes each source with this; File > Open v2 case... and the tests make it with this too.
    /// </summary>
    public static ModelSource V2CaseSource(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        string full = Path.GetFullPath(manifestPath);
        return new ModelSource($"v2  {Path.GetFileName(full)}", Path.GetDirectoryName(full)!, V2Case: full);
    }

    /// <summary>
    /// UI programme shell slice (v2 + pickers, 2026-10-20): a source for a v1 JSON case folder the user opened with
    /// File > Open case folder... (a folder holding <c>system.json</c>), labelled "Case  &lt;folder name&gt;" in the style
    /// of the other labels (kind, two spaces, name). The folder is not checked here; the caller checks it first.
    /// </summary>
    public static ModelSource CaseFolderSource(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return new ModelSource($"Case  {Path.GetFileName(full)}", full);
    }

    /// <summary>The file name a JSON v2 case manifest is looked for under (<c>*.case.json</c>, the v2 naming rule).</summary>
    public const string V2ManifestPattern = "*.case.json";

    /// <summary>The largest manifest the catalogue reads to check its schema tag (4 MiB); a bigger file is skipped and
    /// recorded, never read whole just to list it.</summary>
    public const long V2ManifestMaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// UI programme shell slice (v2 + pickers, 2026-10-20): the JSON v2 case manifests in each of <paramref name="roots"/>
    /// and in each of its immediate subfolders (the depth a v1 case folder is found at under gda-out/), as v2 sources
    /// (<see cref="V2CaseSource"/>, labelled "v2  &lt;file name&gt;"). Each candidate (<see cref="V2ManifestPattern"/>) is
    /// checked cheaply by its schema tag only (<see cref="ProbeV2Manifest"/>): the full validation is the loader's, when
    /// the case is chosen. Fail-soft, as the rest of the catalogue: a root that does not exist is passed over, a folder
    /// that cannot be listed and a manifest that is unreadable, too big, not JSON or not tagged <c>gridsim-case/2</c> are
    /// skipped and recorded in <paramref name="skipped"/> ("path: reason"). A manifest reached twice (two roots that
    /// overlap) is listed once. Sorted within each folder as <see cref="DirectoryScan"/> sorts folders.
    /// </summary>
    public static List<ModelSource> V2CasesIn(IEnumerable<string> roots, ICollection<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(skipped);
        var found = new List<ModelSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;   // Directory.Exists never throws
            var folders = new List<string> { root };
            folders.AddRange(DirectoryScan.Subdirectories(root, skipped));
            foreach (string folder in folders)
            {
                // A folder reached from two roots (results/ inside data/, say) is read once, so a bad manifest in it
                // is recorded once.
                if (!seenFolders.Add(Path.GetFullPath(folder))) continue;
                foreach (string file in ManifestCandidates(folder, skipped))
                {
                    string? reason = ProbeV2Manifest(file);
                    if (reason is not null)
                    {
                        skipped.Add($"{file}: {reason}");
                        continue;
                    }
                    ModelSource s = V2CaseSource(file);
                    if (seen.Add(s.Key)) found.Add(s);
                }
            }
        }
        return found;
    }

    // The *.case.json files directly in `folder`, sorted, listed inside the scan guard (an unreadable folder is recorded
    // and gives none).
    private static List<string> ManifestCandidates(string folder, ICollection<string> skipped)
    {
        List<string> files = new();
        DirectoryScan.Probe(folder, f =>
        {
            files = Directory.EnumerateFiles(f, V2ManifestPattern, SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal)
                .ToList();
            return true;
        }, skipped);
        return files;
    }

    /// <summary>
    /// The cheap check the catalogue makes before listing a JSON v2 case manifest: the file is at most
    /// <see cref="V2ManifestMaxBytes"/>, is JSON (comments and trailing commas allowed, as the v2 reader allows them), and
    /// its top level is an object whose <c>schema</c> is exactly <c>gridsim-case/2</c>
    /// (<see cref="GridSim.Core.IO.Json.V2CaseLoader.CaseSchemaTag"/>). Returns null when it passes, else the reason in
    /// plain words. Never throws for a file problem (unreadable, vanished, locked): that is a reason too.
    /// </summary>
    public static string? ProbeV2Manifest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "the file does not exist";
            if (info.Length > V2ManifestMaxBytes)
                return $"not read: {info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes is more than a case manifest's "
                    + $"{V2ManifestMaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}-byte limit";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            System.Text.Json.JsonElement top = doc.RootElement;
            if (top.ValueKind != System.Text.Json.JsonValueKind.Object) return "not a v2 case manifest: the top level is not a JSON object";
            if (!top.TryGetProperty("schema", out System.Text.Json.JsonElement tag) || tag.ValueKind != System.Text.Json.JsonValueKind.String)
                return $"not a v2 case manifest: no \"schema\" text (expected \"{GridSim.Core.IO.Json.V2CaseLoader.CaseSchemaTag}\")";
            string? value = tag.GetString();
            return value == GridSim.Core.IO.Json.V2CaseLoader.CaseSchemaTag
                ? null
                : $"not a v2 case manifest: schema \"{value}\" (expected \"{GridSim.Core.IO.Json.V2CaseLoader.CaseSchemaTag}\")";
        }
        catch (System.Text.Json.JsonException ex)
        {
            return "not valid JSON: " + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "could not be read: " + ex.Message;
        }
    }

    /// <summary>A one-off replay source for an arbitrary run directory (the "Load run..." browse button).</summary>
    public static ModelSource ReplaySource(string dir) =>
        new($"Replay  {Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}",
            dir, ReplayDir: dir);

    /// <summary>Walk up from the app folder looking for a <c>gda-out</c> directory (handles bin/ vs repo root).</summary>
    private static IEnumerable<string> GdaOutDirs(string baseDir) => NamedDirsUp(baseDir, "gda-out");

    /// <summary>Walk up from <paramref name="baseDir"/> yielding each existing <c>&lt;ancestor&gt;/<paramref name="name"/></c>.</summary>
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
}

/// <summary>
/// UI programme shell slice (v2 + pickers, 2026-10-20): what <see cref="ModelCatalog.Discover(AppSettings?, ICollection{string}?, CatalogOptions)"/>
/// looks for beyond the WPF list. The defaults (<see cref="Wpf"/>) give exactly the WPF window's catalogue: the WPF
/// window cannot load a JSON v2 case, so it never turns <see cref="IncludeV2Cases"/> on, and its picker (and the tests
/// that pin it) are unchanged.
/// </summary>
public sealed record CatalogOptions
{
    /// <summary>The WPF window's catalogue: no v2 cases, the app's own folder.</summary>
    public static CatalogOptions Wpf { get; } = new();

    /// <summary>The cross-platform app's catalogue: the WPF list plus the JSON v2 case manifests under the same folders.</summary>
    public static CatalogOptions CrossPlatform { get; } = new() { IncludeV2Cases = true };

    /// <summary>List JSON v2 case manifests (<c>gridsim-case/2</c>) too (default off: the WPF list).</summary>
    public bool IncludeV2Cases { get; init; }

    /// <summary>The folder data/ is read beside and gda-out/ and results/ are looked for above (default null: the app's
    /// own folder, <see cref="AppContext.BaseDirectory"/>). A test passes a temporary folder.</summary>
    public string? BaseDirectory { get; init; }
}
