using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Simulation;

namespace GridSim.Web.Model;

/// <summary>What kind of source a <see cref="CaseEntry"/> is, which decides how it loads.</summary>
public enum CaseKind
{
    /// <summary>A built-in IEEE/MATPOWER benchmark baked into Core (<see cref="IeeeCases"/>).</summary>
    BuiltIn,
    /// <summary>A multi-file JSON case directory (has <c>system.json</c>).</summary>
    JsonDir,
    /// <summary>A single MATPOWER <c>.m</c> file.</summary>
    Matpower,
    /// <summary>A pre-solved run directory to replay (a folder of exported grid states).</summary>
    Replay,
}

/// <summary>
/// One loadable model the web host can show. <see cref="Id"/> is a URL-safe handle used by the API and
/// the UI; <see cref="Path"/> is the file/dir backing non-built-in kinds. Mirrors the WPF
/// <c>ModelCatalog</c>/<c>ModelSource</c> but is loader-agnostic (returns Core types directly).
/// </summary>
public sealed record CaseEntry(string Id, string Label, CaseKind Kind, string Path = "")
{
    /// <summary>True for the pre-solved replay runs (loaded via <see cref="CaseCatalog.LoadReplay"/>).</summary>
    public bool IsReplay => Kind == CaseKind.Replay;
}

/// <summary>
/// Discovers and loads the grid models the dashboard can show: the built-in IEEE/MATPOWER benchmarks,
/// the bundled GB JSON cases, any committed MATPOWER <c>.m</c> libraries, and pre-solved run directories
/// under nearby <c>results/</c> folders. The web analogue of <c>GridSim.Wpf.ModelCatalog</c>.
/// </summary>
public sealed class CaseCatalog
{
    // The built-in benchmarks, in the order the CLI documents them. case9 is the physics ground-truth.
    private static readonly (string Id, string Label, Func<GridModel> Factory)[] BuiltIns =
    {
        ("case9",   "IEEE 9-bus (WSCC) - ground truth", IeeeCases.Case9),
        ("case14",  "IEEE 14-bus",                       IeeeCases.Case14),
        ("case30",  "IEEE 30-bus",                       IeeeCases.Case30),
        ("case57",  "IEEE 57-bus",                       IeeeCases.Case57),
        ("case118", "IEEE 118-bus",                      IeeeCases.Case118),
        ("case300", "IEEE 300-bus",                      IeeeCases.Case300),
        ("case145", "IEEE 145-bus (dynamic)",            IeeeCases.Case145),
        ("case24",  "IEEE RTS-79 24-bus",                IeeeCases.Case24Rts),
        ("cigre",   "CIGRE MV benchmark",                IeeeCases.CigreMv),
    };

    private readonly List<CaseEntry> _entries = new();
    private readonly Dictionary<string, Func<GridModel>> _builtInFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _baseDir;
    private readonly LicenceGate _license;

    public CaseCatalog(string baseDir) : this(baseDir, null) { }

    public CaseCatalog(string baseDir, LicenceGate? license)
    {
        _baseDir = baseDir;
        _license = license ?? new LicenceGate(Entitlement.FreeDefault);
        Discover();
    }

    /// <summary>Everything loadable, in a stable order (built-ins, GB JSON, MATPOWER, replays).</summary>
    public IReadOnlyList<CaseEntry> Entries => _entries;

    private readonly List<string> _skippedFolders = new();

    /// <summary>gda-out/ and results/ folders the scan could not read, each as "folder: reason" (empty when all
    /// were readable). Skipping keeps one bad folder from hiding every model; recording it keeps the skip visible.</summary>
    public IReadOnlyList<string> SkippedFolders => _skippedFolders;

    public CaseEntry? Find(string id) =>
        _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Load a non-replay case to a fresh <see cref="GridModel"/>. Throws for replay entries, and throws
    /// <see cref="LicenceException"/> when the licence tier's bus limit forbids a licensed (non-bundled) model.
    /// </summary>
    public GridModel LoadModel(string id)
    {
        CaseEntry e = Find(id) ?? throw new KeyNotFoundException($"Unknown case '{id}'.");
        GridModel model = e.Kind switch
        {
            CaseKind.BuiltIn  => _builtInFactories[e.Id](),
            CaseKind.JsonDir  => JsonGridLoader.LoadDirectory(e.Path),
            CaseKind.Matpower => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(e.Path)),
            CaseKind.Replay   => throw new InvalidOperationException($"Case '{id}' is a replay run - use LoadReplay."),
            _ => throw new InvalidOperationException($"Unhandled case kind {e.Kind}."),
        };
        _license.Enforce(model.BusCount, IsBundled(e));
        return model;
    }

    /// <summary>
    /// Whether a catalog entry is a Bundled Network (LICENSE.md section 1) exempt from bus limits. The built-in
    /// benchmarks and the committed MATPOWER library are bundled; of the JSON cases, only the shipped GB
    /// networks are bundled - a materialised <c>gda-*</c> model is a licensed user network.
    /// </summary>
    // N229 (LF5, October 2026): a file-backed entry is bundled by CONTENT, through the one shared rule
    // (BundledNetworks.IsBundledPath: under the install's data folder AND matching the shipped-content manifest). A
    // MATPOWER entry used to be bundled by its kind alone ("_ => true"), so any .m file the catalogue listed was exempt;
    // a shipped GB JSON id now also needs its folder to hold the shipped content. Built-in cases never touch a file and
    // stay bundled. Every shipped entry is unchanged (its files match the manifest).
    private static bool IsBundled(CaseEntry e) => e.Kind switch
    {
        CaseKind.BuiltIn => true,
        CaseKind.JsonDir => GridSim.Core.Licensing.BundledNetworks.IsBundledJsonCase(e.Id)   // R002: the one shared list
                            && GridSim.Core.Licensing.BundledNetworks.IsBundledPath(e.Path),
        _ => GridSim.Core.Licensing.BundledNetworks.IsBundledPath(e.Path),
    };

    /// <summary>Load a pre-solved run directory into a scrubbable replay cursor.</summary>
    public PresolvedReplay LoadReplay(string id)
    {
        CaseEntry e = Find(id) ?? throw new KeyNotFoundException($"Unknown case '{id}'.");
        if (e.Kind != CaseKind.Replay)
            throw new InvalidOperationException($"Case '{id}' is not a replay run.");
        PresolvedReplay replay = PresolvedReplay.Load(e.Path);
        // A replay run is a network model the host loads (LICENSE.md section 1), so the licence bus limit applies to it
        // as to a live case; only LoadModel enforced it before, so a free-tier host played a replay of any size. A run
        // of a bundled network (a built-in benchmark or a shipped GB case, by the model's name) stays exempt.
        string name = replay.LayoutModel.Name;
        bool bundled = BuiltIns.Any(b => string.Equals(b.Id, name, StringComparison.OrdinalIgnoreCase))
                       || GridSim.Core.Licensing.BundledNetworks.IsBundledJsonCase(name);
        _license.Enforce(replay.LayoutModel.BusCount, bundled);
        return replay;
    }

    private void Discover()
    {
        foreach ((string id, string label, Func<GridModel> factory) in BuiltIns)
        {
            _entries.Add(new CaseEntry(id, label, CaseKind.BuiltIn));
            _builtInFactories[id] = factory;
        }

        string reduced = Path.Combine(_baseDir, "data", "uk");
        if (File.Exists(Path.Combine(reduced, "system.json")))
            _entries.Add(new CaseEntry("gb-reduced", "GB reduced * 10-zone", CaseKind.JsonDir, reduced));

        string synthetic = Path.Combine(_baseDir, "data", "gb-synthetic");
        if (File.Exists(Path.Combine(synthetic, "system.json")))
            _entries.Add(new CaseEntry("gb-synthetic", "GB synthetic * distribution depth", CaseKind.JsonDir, synthetic));

        string full = Path.Combine(_baseDir, "data", "gb-full");
        if (File.Exists(Path.Combine(full, "system.json")))
            _entries.Add(new CaseEntry("gb-full", "GB full * 3539-bus transmission", CaseKind.JsonDir, full));

        // Any GDA models materialised into a nearby gda-out/ (git-ignored, produced on an authorised machine).
        // Known limitation "Discovery and static files" (chapter 36): two model folders whose names slug alike ("Foo Bar"
        // and "foo-bar", or one name in two gda-out/ folders at different levels) used to get the same gda-* id, and
        // Find() is FirstOrDefault by id, so the second model could never be opened (asking for it opened the first).
        // The id is now made unique the way the MATPOWER and replay ids already are: the first keeps the plain id and a
        // later one gets a numbered suffix. An id that did not collide is unchanged.
        foreach (string root in NamedDirsUp(_baseDir, "gda-out"))
            foreach (string sub in SafeDirs(root))
                if (File.Exists(Path.Combine(sub, "system.json")))
                    _entries.Add(new CaseEntry(UniqueId($"gda-{Slug(Path.GetFileName(sub))}"), $"GDA * {Path.GetFileName(sub)}", CaseKind.JsonDir, sub));

        string matpower = Path.Combine(_baseDir, "data", "matpower");
        if (Directory.Exists(matpower))
            foreach (string f in Directory.EnumerateFiles(matpower, "*.m", SearchOption.AllDirectories)
                                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                _entries.Add(new CaseEntry(UniqueId($"mp-{Slug(name)}"), $"MATPOWER * {name}", CaseKind.Matpower, f));
            }

        var seenRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in NamedDirsUp(_baseDir, "results"))
            foreach (string sub in SafeDirs(root))
                // IsRunDirectory lists sub/exports, which can itself be unreadable: probe it under the guard too.
                if (seenRuns.Add(sub) && DirectoryScan.Probe(sub, PresolvedReplay.IsRunDirectory, _skippedFolders))
                {
                    string name = Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    // Distinct run directories can slug to the same id (names differing only in case, spacing
                    // or punctuation). Find() is FirstOrDefault by id, so a collision would open the wrong
                    // run's data - disambiguate the id here.
                    _entries.Add(new CaseEntry(UniqueId($"run-{Slug(name)}"), $"Replay * {name}", CaseKind.Replay, sub));
                }
    }

    /// <summary>Return <paramref name="baseId"/>, or a numbered suffix if that id is already taken.</summary>
    private string UniqueId(string baseId)
    {
        bool Taken(string id) => _entries.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseId)) return baseId;
        int k = 2;
        string candidate;
        do { candidate = $"{baseId}-{k++}"; } while (Taken(candidate));
        return candidate;
    }

    // The shared Core guard (also used by the WPF ModelCatalog). The old local version returned a LAZY ordered
    // enumerable from inside its try, so an error raised while the listing was actually walked escaped the guard;
    // it also dropped the folder silently. DirectoryScan enumerates fully inside the guard and records the skip.
    private IReadOnlyList<string> SafeDirs(string root) => DirectoryScan.Subdirectories(root, _skippedFolders);

    /// <summary>Walk up from <paramref name="baseDir"/> yielding each existing <c>&lt;ancestor&gt;/name</c>.</summary>
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

    /// <summary>URL-safe slug for an id (lowercase, non-alphanumerics -> single dash).</summary>
    private static string Slug(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        string joined = new string(chars);
        while (joined.Contains("--")) joined = joined.Replace("--", "-");
        return joined.Trim('-');
    }
}
