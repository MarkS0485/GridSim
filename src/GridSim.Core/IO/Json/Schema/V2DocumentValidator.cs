using System.Text.Json;
using GridSim.Core.IO.Json.Overlay;

namespace GridSim.Core.IO.Json.Schema;

/// <summary>The validation outcome of one v2 document: its path, its schema tag (null when it has none) and every
/// error found (empty = valid).</summary>
public sealed record V2DocumentReport(string Path, string? SchemaTag, IReadOnlyList<SchemaError> Errors)
{
    /// <summary>True when the document has no error.</summary>
    public bool Valid => Errors.Count == 0;
}

/// <summary>
/// Validates GridSim JSON v2 documents by their <c>"schema"</c> tag (F24, October 2026: the engine of the
/// <c>validate</c> verb). It adds no rule of its own: each tag goes to the checker that JSON v2 Phase 1 already has.
/// <list type="bullet">
/// <item><c>gridsim-case/2</c>: <c>case.schema.json</c>; then, when the manifest is schema-valid, every network file it
/// lists (paths relative to the manifest) is read and checked against <c>network.schema.json</c>, a missing one being an
/// error on the manifest. Library imports name files "relative to the data root", which a lone manifest does not know, so
/// they are not resolved here (pass the library files themselves to check them).</item>
/// <item><c>gridsim-network/2</c>: <c>network.schema.json</c>.</item>
/// <item><c>gridsim-library/2</c>: every library file given is checked TOGETHER by <see cref="V2LibraryChecker"/> (the
/// schema, the kind specs, unique <c>id@rev</c> across the set, and controller references resolving within it).</item>
/// <item><c>gridsim-kind/2</c>: <c>kind.schema.json</c>.</item>
/// <item><c>gridsim-assets/2</c> (Phase 5a, 2026-10-20): the asset overlay files a manifest lists in <c>assets</c> are
/// checked as one set by <see cref="V2AssetChecker"/> (schema, overlay kind specs from <c>kinds/overlay</c>, unique ids,
/// parents, refs), with every reference to a bus, line, transformer, generator or demand resolved against the
/// manifest's network files; an overlay path outside the case folder, missing, or listed twice is an error on the
/// manifest. Overlay files given that no manifest given lists are checked together without a case, so their
/// references to network elements are not resolved. The overlay kind specs are loaded only when an overlay file is
/// checked.</item>
/// </list>
/// A file that is not JSON, or has no recognised tag, is reported as an error on that file; nothing throws for a bad
/// document. Every error is collected, never only the first.
/// </summary>
public static class V2DocumentValidator
{
    /// <summary>The document tags this validator knows.</summary>
    public static readonly IReadOnlyList<string> KnownTags = new[] { "gridsim-case/2", "gridsim-network/2", V2LibraryChecker.SchemaTag, KindSpecRegistry.SchemaTag, DynamicsTag, Dist3phTag, V2AssetChecker.SchemaTag };

    /// <summary>N339 (October 2026): the three-phase feeder document tag (<c>dist3ph.schema.json</c>).</summary>
    public const string Dist3phTag = GridSim.Core.Distribution.MultiPhaseJsonLoader.SchemaV2;

    /// <summary>F23 (October 2026, stream B3): the machine dynamic data document tag (<c>dynamics.schema.json</c>).</summary>
    public const string DynamicsTag = "gridsim-dynamics/2";

    /// <summary>F23: the schema file a <see cref="DynamicsTag"/> document is validated against.</summary>
    public const string DynamicsSchemaFile = "dynamics.schema.json";

    /// <summary>
    /// Validate <paramref name="paths"/> (files) against the v2 schemas in <paramref name="schemasDir"/> (the folder
    /// holding <c>case.schema.json</c> and friends, with the kind specs in its <c>kinds</c> subfolder). Returns one report
    /// per file given, in the order given, plus one per network file a manifest pulled in that was not itself given.
    /// Throws only when the schema set itself cannot be loaded (a missing or invalid schemas folder).
    /// </summary>
    public static IReadOnlyList<V2DocumentReport> ValidateFiles(IReadOnlyList<string> paths, string schemasDir)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(schemasDir);
        JsonSchemaSubset schemas = JsonSchemaSubset.LoadDirectory(schemasDir);
        var validator = new SchemaValidator(schemas);
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(schemasDir, "kinds"), schemas);

        var reports = new List<V2DocumentReport>();
        var seen = new HashSet<string>(FilePathComparison.Comparer);
        var libraries = new List<(int ReportIndex, string Name, JsonElement Root)>();
        var pulledNetworks = new List<string>();
        // Phase 5a: overlay files given directly (full path -> report index and root), and per manifest the overlay
        // files it lists (as written, and in full) with the targets of its network files (null when one of them
        // could not be read, so nothing is resolved against half a case).
        var givenAssets = new Dictionary<string, (int ReportIndex, string Name, JsonElement Root)>(FilePathComparison.Comparer);
        var assetGroups = new List<(List<(string Rel, string Full)> Files, AssetOverlayTargets? Targets)>();
        OverlayKindSet? overlayKinds = null;
        OverlayKindSet OverlayKinds() => overlayKinds ??= OverlayKindSet.LoadDirectory(
            System.IO.Path.Combine(schemasDir, "kinds", OverlayKindSet.FolderName), schemas);

        foreach (string path in paths)
        {
            string full = System.IO.Path.GetFullPath(path);
            seen.Add(full);
            (JsonElement? root, SchemaError? parseError) = Read(path);
            if (root is not JsonElement r)
            {
                reports.Add(new V2DocumentReport(path, null, new[] { parseError! }));
                continue;
            }
            string? tag = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("schema", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            switch (tag)
            {
                case "gridsim-case/2":
                {
                    var errors = validator.Validate(r, "case.schema.json").ToList();
                    bool manifestValid = errors.Count == 0;
                    if (errors.Count == 0 && r.TryGetProperty("network", out JsonElement nets))
                    {
                        string baseDir = System.IO.Path.GetDirectoryName(full)!;
                        for (int i = 0; i < nets.GetArrayLength(); i++)
                        {
                            string rel = nets[i].GetString()!;
                            string netPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                            // A network file outside the case folder is never opened (its values would be quoted in the
                            // schema errors): reported here, before the file is read.
                            string caseRoot = baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar) ? baseDir : baseDir + System.IO.Path.DirectorySeparatorChar;
                            if (!netPath.StartsWith(caseRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                                errors.Add(new SchemaError($"/network/{i}", $"network file '{rel}' lies outside the case folder ({baseDir}); it is not read."));
                            else if (!File.Exists(netPath))
                                errors.Add(new SchemaError($"/network/{i}", $"network file '{rel}' does not exist (looked for {netPath})."));
                            else if (!seen.Contains(netPath) && !pulledNetworks.Contains(netPath, FilePathComparison.Comparer))
                                pulledNetworks.Add(netPath);
                        }
                    }
                    if (manifestValid && r.TryGetProperty("assets", out JsonElement assets))
                        assetGroups.Add(ListAssets(full, r, assets, errors));
                    reports.Add(new V2DocumentReport(path, tag, errors));
                    break;
                }
                case V2AssetChecker.SchemaTag:
                    // Checked after the loop, with the manifest that lists it when one was given (its errors land here).
                    givenAssets[full] = (reports.Count, path, r);
                    reports.Add(new V2DocumentReport(path, tag, Array.Empty<SchemaError>()));
                    break;
                case "gridsim-network/2":
                    reports.Add(new V2DocumentReport(path, tag, validator.Validate(r, "network.schema.json")));
                    break;
                case "gridsim-kind/2":
                    reports.Add(new V2DocumentReport(path, tag, validator.Validate(r, KindSpecRegistry.KindSchemaFile)));
                    break;
                case DynamicsTag:
                    reports.Add(new V2DocumentReport(path, tag, CheckDynamics(r, validator, kinds)));
                    break;
                case Dist3phTag:
                    reports.Add(new V2DocumentReport(path, tag, CheckDist3ph(path, r, validator)));
                    break;
                case var lib when lib == V2LibraryChecker.SchemaTag:
                    libraries.Add((reports.Count, path, r));
                    reports.Add(new V2DocumentReport(path, tag, Array.Empty<SchemaError>()));   // filled in below
                    break;
                default:
                    reports.Add(new V2DocumentReport(path, tag, new[]
                    {
                        new SchemaError("/schema", tag is null
                            ? $"not a GridSim v2 document: no \"schema\" tag (expected one of {string.Join(", ", KnownTags)})."
                            : $"unknown schema tag '{tag}' (expected one of {string.Join(", ", KnownTags)})."),
                    }));
                    break;
            }
        }

        // Library files are checked as one set, and each error goes back to its own file's report.
        if (libraries.Count > 0)
        {
            IReadOnlyList<SchemaError> all = V2LibraryChecker.Check(libraries.Select(l => (l.Name, l.Root)).ToList(), validator, kinds);
            foreach ((int idx, string name, _) in libraries)
                reports[idx] = reports[idx] with { Errors = all.Where(e => e.File == name).Select(e => e with { File = null }).ToList() };
        }

        // Network files a manifest pulled in that the caller did not list themselves.
        foreach (string netPath in pulledNetworks)
        {
            (JsonElement? root, SchemaError? parseError) = Read(netPath);
            if (root is not JsonElement r) { reports.Add(new V2DocumentReport(netPath, null, new[] { parseError! })); continue; }
            reports.Add(new V2DocumentReport(netPath, "gridsim-network/2", validator.Validate(r, "network.schema.json")));
        }

        // Phase 5a: each manifest's overlay files as one set, resolved against its network files. An overlay file that
        // was also given directly gets the set's errors on its own report; one only the manifest names gets a report
        // of its own, after the pulled network files.
        var checkedGiven = new HashSet<string>(FilePathComparison.Comparer);
        foreach ((List<(string Rel, string Full)> files, AssetOverlayTargets? targets) in assetGroups)
        {
            var docs = new List<(string Name, JsonElement Root)>();
            var unreadable = new Dictionary<string, SchemaError>(FilePathComparison.Comparer);
            foreach ((string rel, string full) in files)
            {
                if (givenAssets.TryGetValue(full, out var given)) { docs.Add((rel, given.Root)); continue; }
                (JsonElement? root, SchemaError? parseError) = Read(full);
                if (root is JsonElement ar) docs.Add((rel, ar));
                else unreadable[full] = parseError!;
            }
            IReadOnlyList<SchemaError> found = docs.Count > 0
                ? V2AssetChecker.Check(docs, validator, OverlayKinds(), targets) : Array.Empty<SchemaError>();
            foreach ((string rel, string full) in files)
            {
                if (unreadable.TryGetValue(full, out SchemaError? bad))
                {
                    reports.Add(new V2DocumentReport(full, null, new[] { bad }));
                    continue;
                }
                List<SchemaError> mine = found.Where(e => e.File == rel).Select(e => e with { File = null }).ToList();
                if (givenAssets.TryGetValue(full, out var given))
                {
                    reports[given.ReportIndex] = reports[given.ReportIndex] with { Errors = reports[given.ReportIndex].Errors.Concat(mine).ToList() };
                    checkedGiven.Add(full);
                }
                else
                {
                    // The tag the file really carries (a wrong one is an error in mine, from the schema's const).
                    JsonElement own = docs.First(d => d.Name == rel).Root;
                    string? ownTag = own.ValueKind == JsonValueKind.Object && own.TryGetProperty("schema", out JsonElement st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() : null;
                    reports.Add(new V2DocumentReport(full, ownTag, mine));
                }
            }
        }

        // Overlay files given that no manifest given lists: one set, with no case to resolve against.
        var lone = givenAssets.Where(g => !checkedGiven.Contains(g.Key)).OrderBy(g => g.Value.ReportIndex).Select(g => g.Value).ToList();
        if (lone.Count > 0)
        {
            IReadOnlyList<SchemaError> found = V2AssetChecker.Check(lone.Select(l => (l.Name, l.Root)).ToList(), validator, OverlayKinds(), targets: null);
            foreach ((int idx, string name, _) in lone)
                reports[idx] = reports[idx] with { Errors = found.Where(e => e.File == name).Select(e => e with { File = null }).ToList() };
        }
        return reports;
    }

    /// <summary>
    /// Phase 5a: the overlay files a schema-valid manifest lists (<c>assets</c>), each checked to lie inside the case
    /// folder, exist and be listed once (an error on the manifest otherwise, and the file is not read), and the targets
    /// of the manifest's network files that overlay references resolve against (null when any network file is outside
    /// the folder, missing or not JSON: those are reported on their own, and nothing is resolved against part of a case).
    /// </summary>
    private static (List<(string Rel, string Full)> Files, AssetOverlayTargets? Targets) ListAssets(
        string manifestFull, JsonElement manifest, JsonElement assets, List<SchemaError> errors)
    {
        string baseDir = System.IO.Path.GetDirectoryName(manifestFull)!;
        var files = new List<(string Rel, string Full)>();
        for (int i = 0; i < assets.GetArrayLength(); i++)
        {
            string rel = assets[i].GetString()!;
            string assetPath = InsideOrNull(baseDir, rel) ?? "";
            if (assetPath.Length == 0)
                errors.Add(new SchemaError($"/assets/{i}", $"asset overlay file '{rel}' lies outside the case folder ({baseDir}); it is not read."));
            else if (!File.Exists(assetPath))
                errors.Add(new SchemaError($"/assets/{i}", $"asset overlay file '{rel}' does not exist (looked for {assetPath})."));
            else if (files.Any(f => string.Equals(f.Full, assetPath, FilePathComparison.Comparison)))
                errors.Add(new SchemaError($"/assets/{i}", $"asset overlay file '{rel}' is listed more than once."));
            else files.Add((rel, assetPath));
        }

        var roots = new List<JsonElement>();
        foreach (JsonElement n in manifest.GetProperty("network").EnumerateArray())
        {
            string? netPath = InsideOrNull(baseDir, n.GetString()!);
            if (netPath is null || !File.Exists(netPath)) return (files, null);
            (JsonElement? root, _) = Read(netPath);
            if (root is not JsonElement nr) return (files, null);
            roots.Add(nr);
        }
        return (files, AssetOverlayTargets.FromNetworks(roots));
    }

    // The full path of a manifest-relative file when it lies inside the case folder, else null (never opened then).
    private static string? InsideOrNull(string baseDir, string rel)
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        string caseRoot = baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar) ? baseDir : baseDir + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(caseRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// F23 (October 2026, stream B3): a <c>gridsim-dynamics/2</c> document. First <see cref="DynamicsSchemaFile"/>; when
    /// that passes, the document's own <c>templates</c> are checked as ONE template library by
    /// <see cref="V2LibraryChecker.Check"/> (kind specs, units, ranges, required keys, unique id@rev, per-parameter
    /// provenance keys, controller references resolving within the file to non-placeholder templates of the slot's
    /// kind): the same rules a <c>gridsim-library/2</c> file meets, so a dynamics file's templates could be moved into a
    /// library unchanged. The library check reports pointers as <c>/templates/i/...</c>, which are the same pointers in
    /// the dynamics document. The binding rules (one generator per entry, model-specific required parameters) need the
    /// case and are checked by the loader (<see cref="MachineDynamicsLoader"/>).
    /// </summary>
    private static IReadOnlyList<SchemaError> CheckDynamics(JsonElement root, SchemaValidator validator, KindSpecRegistry kinds)
    {
        IReadOnlyList<SchemaError> errors = validator.Validate(root, DynamicsSchemaFile);
        if (errors.Count > 0) return errors;
        string wrapped = "{\"schema\":\"" + V2LibraryChecker.SchemaTag + "\",\"templates\":" + root.GetProperty("templates").GetRawText() + "}";
        using JsonDocument lib = JsonDocument.Parse(wrapped, JsonSchemaSubset.ReaderOptions);
        return V2LibraryChecker.Check(new[] { ("dynamics", lib.RootElement.Clone()) }, validator, kinds)
            .Select(e => e with { File = null }).ToList();
    }

    /// <summary>
    /// N339 (October 2026): a <c>gridsim-dist3ph/2</c> feeder. First <see cref="GridSim.Core.Distribution.MultiPhaseJsonLoader.SchemaV2File"/>
    /// from this schema set (every structural error, with pointers); when that passes, the feeder reader itself
    /// (<see cref="GridSim.Core.Distribution.MultiPhaseJsonLoader.ParseV2"/>), whose first refusal (ids, references, the
    /// branch kind rules, one radial tree, phases) is reported as one error on the document root, its message naming
    /// the JSON path.
    /// </summary>
    private static IReadOnlyList<SchemaError> CheckDist3ph(string path, JsonElement root, SchemaValidator validator)
    {
        IReadOnlyList<SchemaError> errors = validator.Validate(root, GridSim.Core.Distribution.MultiPhaseJsonLoader.SchemaV2File);
        if (errors.Count > 0) return errors;
        try
        {
            GridSim.Core.Distribution.MultiPhaseJsonLoader.ParseV2(File.ReadAllText(path), path);
            return Array.Empty<SchemaError>();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return new[] { new SchemaError("", ex.Message) };
        }
    }

    // Read one document; a missing file or bad JSON becomes an error on that file (never an exception).
    private static (JsonElement? Root, SchemaError? Error) Read(string path)
    {
        if (!File.Exists(path)) return (null, new SchemaError("", $"file not found: {path}"));
        try
        {
            using JsonDocument doc = JsonSchemaSubset.ParseFile(path);
            return (doc.RootElement.Clone(), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, new SchemaError("", $"not valid JSON: {ex.Message}"));
        }
    }
}
