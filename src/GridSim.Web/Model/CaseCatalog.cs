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

    /// <summary>
    /// GR-715: the setting that names the folders case discovery searches for <c>results/</c> and <c>gda-out/</c>, as a
    /// list separated by the platform's path separator (<c>;</c> on Windows, <c>:</c> elsewhere). Environment variable
    /// <c>GridSim__CaseRoots</c>. Unset: the earlier search (the install folder and up to seven ancestors of it).
    /// </summary>
    public const string CaseRootsSetting = "GridSim:CaseRoots";

    /// <summary>
    /// GR-715: the lowest role that is shown the models found under <c>results/</c> and <c>gda-out/</c> in a listing.
    /// Those folders hold run outputs and licensed or private GDA models, so a caller below this band (an anonymous caller
    /// on a host that answers non-loopback names is a Viewer) sees only the built-in, bundled GB and MATPOWER cases.
    /// </summary>
    public const GridSim.Core.Security.OperationRole PrivateListingRole = GridSim.Core.Security.OperationRole.Operator;

    // GR-715: the configured discovery roots, or null for the earlier ancestor walk.
    private readonly IReadOnlyList<string>? _discoveryRoots;
    // GR-715: the ids found under results/ or gda-out/ (the entries a low role is not shown in a listing).
    private readonly HashSet<string> _privateIds = new(StringComparer.OrdinalIgnoreCase);

    public CaseCatalog(string baseDir, LicenceGate? license, IReadOnlyList<string>? discoveryRoots = null)
    {
        _baseDir = baseDir;
        _license = license ?? new LicenceGate(Entitlement.FreeDefault);
        if (discoveryRoots is not null && discoveryRoots.Count == 0)
            throw new ArgumentException(
                $"GridSim.Web: configuration setting {CaseRootsSetting} names no folder; name at least one, or leave it unset.", nameof(discoveryRoots));
        _discoveryRoots = discoveryRoots;
        Discover();
    }

    /// <summary>
    /// GR-715: read <see cref="CaseRootsSetting"/>. Null when unset or blank (the earlier search). Otherwise every item must
    /// be an existing folder, or start-up stops naming the setting and the item (fail loud: a typo must not silently list
    /// nothing).
    /// </summary>
    public static IReadOnlyList<string>? ParseCaseRoots(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var roots = new List<string>();
        foreach (string item in raw.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(item))
                throw new InvalidOperationException(
                    $"GridSim.Web: configuration setting {CaseRootsSetting} names '{item}', which is not a folder; give existing folders separated by '{Path.PathSeparator}'.");
            roots.Add(Path.GetFullPath(item));
        }
        if (roots.Count == 0)
            throw new InvalidOperationException($"GridSim.Web: configuration setting {CaseRootsSetting} names no folder; name at least one, or leave it unset.");
        return roots;
    }

    /// <summary>GR-715: true when <paramref name="id"/> names a model found under <c>results/</c> or <c>gda-out/</c>
    /// (a bundled, built-in or MATPOWER case is not).</summary>
    public bool IsPrivate(string id) => _privateIds.Contains(id);

    /// <summary>GR-715: the entries a caller holding <paramref name="role"/> is shown. Everything for
    /// <see cref="PrivateListingRole"/> and above; below it, the entries not found under <c>results/</c> or <c>gda-out/</c>.
    /// Listing only: opening a case by id is decided where it is opened.</summary>
    public IReadOnlyList<CaseEntry> EntriesFor(GridSim.Core.Security.OperationRole role) =>
        role >= PrivateListingRole ? _entries : _entries.Where(e => !_privateIds.Contains(e.Id)).ToList();

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
        GridModel model = LoadEntry(e, out bool bundled, refuseBeforeParse: true);
        _license.Enforce(model.BusCount, bundled);
        return model;
    }

    /// <summary>
    /// GR-712: the number of bus rows a MATPOWER case text declares in its <c>mpc.bus = [ ... ];</c> block, counted
    /// without parsing the case (comments after <c>%</c> are ignored; a row is a <c>;</c>-terminated piece holding a
    /// digit), or -1 when no such block is found (then no early decision is made and the full parse and the licence check
    /// after it decide, exactly as before). Only an early REFUSAL uses it: the check after the parse stays, so a count
    /// that was too low can never let an oversize model through.
    /// </summary>
    internal static int CountMatpowerBusRows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            text, @"^[ \t]*mpc\.bus[ \t]*=[ \t]*\[", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!m.Success) return -1;
        int end = text.IndexOf(']', m.Index + m.Length);
        if (end < 0) return -1;
        var body = new System.Text.StringBuilder();
        foreach (string line in text.Substring(m.Index + m.Length, end - (m.Index + m.Length)).Split('\n'))
        {
            int pct = line.IndexOf('%');
            body.Append(pct >= 0 ? line.AsSpan(0, pct) : line.AsSpan()).Append('\n');
        }
        int rows = 0;
        foreach (string piece in body.ToString().Split(';'))
            if (piece.Any(char.IsDigit)) rows++;
        return rows;
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
    // Bug #178: the model and its bundled verdict from checks made before AND after the load (BundledAcrossLoad).
    // GR-712 (November 2026): for a MATPOWER file the bundled verdict (a content hash) is taken FIRST, the file's bytes are
    // read ONCE, and when the file is not bundled the licence is checked against the bus rows its text declares BEFORE
    // the parser runs, so an oversize user model is refused without being parsed in full (a large file used to be parsed
    // and normalised first, then refused). The parse reads the same bytes (the same text MatpowerParser.ParseFile reads,
    // the same case name), so the model is exactly the one built before. The check after the parse (LoadModel) stays, so
    // the early check can only refuse sooner, never let more through. A JSON case directory is still parsed in full
    // first (its bus count lives in several files, with no cheap count): that part of the limitation remains.
    private GridModel LoadEntry(CaseEntry e, out bool bundled, bool refuseBeforeParse = false)
    {
        string? matpowerText = null;
        if (refuseBeforeParse && e.Kind == CaseKind.Matpower)
        {
            bool bundledFirst = IsBundled(e);
            byte[] bytes = File.ReadAllBytes(e.Path);
            using (var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true))
                matpowerText = reader.ReadToEnd();
            int rows = CountMatpowerBusRows(matpowerText);
            if (!bundledFirst && rows >= 0) _license.Enforce(rows, bundled: false);   // throws LicenceException
        }
        GridModel Load() => e.Kind switch
        {
            CaseKind.BuiltIn  => _builtInFactories[e.Id](),
            CaseKind.JsonDir  => JsonGridLoader.LoadDirectory(e.Path),
            CaseKind.Matpower => MatpowerUnitNormalizer.Normalize(matpowerText is null
                ? MatpowerParser.ParseFile(e.Path)
                : MatpowerParser.Parse(matpowerText, Path.GetFileNameWithoutExtension(e.Path))),
            CaseKind.Replay   => throw new InvalidOperationException($"Case '{e.Id}' is a replay run - use LoadReplay."),
            _ => throw new InvalidOperationException($"Unhandled case kind {e.Kind}."),
        };
        if (e.Kind == CaseKind.BuiltIn) { bundled = true; return Load(); }   // built in: no file to check
        bool before = IsBundled(e);
        GridModel model = Load();
        bundled = before && IsBundled(e);
        return model;
    }

    /// <summary>
    /// Bug #171 / #172: whether a model that arrived without shipped files (a replay run's snapshot, a bridge server's
    /// topology) is a Bundled Network. It is when <paramref name="name"/> names a built-in case or a shipped JSON case
    /// whose files verify (<see cref="IsBundled"/>, by content), AND the model has that shipped network's topology
    /// (<see cref="GridSim.Core.Licensing.BundledNetworks.SameTopology"/>). The name alone used to decide, and the name
    /// is written inside the run's own exported files, so any replay renamed gb-full played above the free tier limit.
    /// </summary>
    public bool IsBundledModel(GridModel model, string? name)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(name)) return false;
        GridModel? shipped = null;
        foreach ((string id, _, Func<GridModel> factory) in BuiltIns)
            if (string.Equals(id, name, StringComparison.OrdinalIgnoreCase)) { shipped = factory(); break; }
        if (shipped is null && GridSim.Core.Licensing.BundledNetworks.IsBundledJsonCase(name)
            && Find(name) is { Kind: CaseKind.JsonDir } entry)
        {
            GridModel loaded = LoadEntry(entry, out bool verified);
            if (verified) shipped = loaded;   // only shipped content may serve as the reference
        }
        return shipped is not null && GridSim.Core.Licensing.BundledNetworks.SameTopology(model, shipped);
    }

    /// <summary>Bug #172: the licence verdict for a model a bridge server sent, named by the remote case it asked for.</summary>
    public GateResult CheckLicence(GridModel model, string? name)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _license.Check(model.BusCount, IsBundledModel(model, name));
    }

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
        // as to a live case; only LoadModel enforced it before, so a free-tier host played a replay of any size.
        // Bug #171: a run of a bundled network stays exempt only when its model HAS the shipped network's topology
        // (IsBundledModel, by content); the name inside the run's files no longer decides on its own.
        bool bundled = IsBundledModel(replay.LayoutModel, replay.LayoutModel.Name);
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
                {
                    var gdaEntry = new CaseEntry(UniqueId($"gda-{Slug(Path.GetFileName(sub))}"), $"GDA * {Path.GetFileName(sub)}", CaseKind.JsonDir, sub);
                    _entries.Add(gdaEntry);
                    _privateIds.Add(gdaEntry.Id);   // GR-715: not listed to a caller below PrivateListingRole
                }

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
                    var runEntry = new CaseEntry(UniqueId($"run-{Slug(name)}"), $"Replay * {name}", CaseKind.Replay, sub);
                    _entries.Add(runEntry);
                    _privateIds.Add(runEntry.Id);   // GR-715: not listed to a caller below PrivateListingRole
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
    private IEnumerable<string> NamedDirsUp(string baseDir, string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // GR-715: with configured roots (GridSim:CaseRoots) only <root>/name is looked at, for each root; nothing is walked up.
        if (_discoveryRoots is not null)
        {
            foreach (string root in _discoveryRoots)
            {
                string g = Path.Combine(root, name);
                if (Directory.Exists(g) && seen.Add(g)) yield return g;
            }
            yield break;
        }
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
