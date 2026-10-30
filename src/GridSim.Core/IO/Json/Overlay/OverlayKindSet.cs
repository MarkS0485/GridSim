using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.Overlay;

// =====================================================================================================================
// Phase 5a (2026-10-20): the asset overlay. Everything in this namespace is CARRIED BESIDE the model and is never an
// input to the solver. A guard test (AssetOverlayGuardTests) fails if any Core file outside this namespace and the case
// editor (CaseEditing) names one of these types, apart from the few named carry points (the v2 loader's result and the
// v2 document validator). A pin test proves a case loads to the same model and results with or without its overlay.
// =====================================================================================================================

/// <summary>What an overlay reference points at. Buses are addressed by id; lines, transformers, generators and demands
/// by name, exactly as the case's network files spell them; another overlay record by its id.</summary>
public enum AssetRefTarget
{
    Bus,
    Line,
    Transformer,
    Generator,
    Demand,
    Record,
}

/// <summary>One reference role an overlay kind declares (the <c>overlay.refs</c> block of its kind spec).</summary>
/// <param name="Role">The role name, for example <c>bus</c> or <c>from</c>.</param>
/// <param name="Target">What the role points at.</param>
/// <param name="Required">True when every record of the kind must give this role.</param>
/// <param name="RecordKinds">For a <see cref="AssetRefTarget.Record"/> target, the overlay kinds the record pointed at
/// may have; empty for every other target.</param>
/// <param name="Description">The plain-English meaning of the role.</param>
public sealed record OverlayRefSpec(string Role, AssetRefTarget Target, bool Required, IReadOnlyList<string> RecordKinds, string Description);

/// <summary>The overlay block of one overlay kind: its reference roles and where its records may sit.</summary>
/// <param name="Kind">The kind name.</param>
/// <param name="Refs">The roles, by name, in the order the kind file declares them.</param>
/// <param name="ParentKinds">The kinds a record of this kind may sit under; empty = top level only (no parent).</param>
/// <param name="ParentRequired">True when a record of this kind must have a parent.</param>
public sealed record OverlayKindSpec(string Kind, IReadOnlyDictionary<string, OverlayRefSpec> Refs, IReadOnlyList<string> ParentKinds, bool ParentRequired);

/// <summary>
/// The asset overlay kinds: the kind specs under <c>schemas/v2/kinds/overlay</c> (format <c>gridsim-kind/2</c>, each with
/// an <c>overlay</c> block). They are loaded APART from the template kinds in <c>schemas/v2/kinds</c>, so a library
/// template can never be of an overlay kind and an overlay record can never be of a template kind: the two sets cannot
/// leak into each other.
///
/// <para>Loading is fail loud and collects every problem. The parameter contracts load through
/// <see cref="KindSpecRegistry.FromSources"/> exactly as the template kinds do (kind.schema.json, then the registry's own
/// rules), so <see cref="KindSpecRegistry.CheckParams"/> is reused unchanged for overlay record parameters. Then the
/// overlay blocks are read and these rules, which the schema subset cannot express, are checked:</para>
/// <list type="bullet">
/// <item>every kind in the folder has an overlay block (it is what makes the kind an overlay kind);</item>
/// <item>a role name is lowerCamelCase ASCII and is declared once (the shared v2 reader already refuses a repeated
/// member name; the check here also covers kind text parsed by anything else);</item>
/// <item>a record target lists its recordKinds, and no other target does;</item>
/// <item>every kind named in recordKinds or parentKinds is an overlay kind of the same folder, and is named once;</item>
/// <item>parentRequired is true only when parentKinds is not empty.</item>
/// </list>
/// </summary>
public sealed partial class OverlayKindSet
{
    /// <summary>The folder, under the v2 <c>kinds</c> folder, that holds the overlay kind specs.</summary>
    public const string FolderName = "overlay";

    // The same lowerCamelCase rule kind.schema.json applies to kind names; role names follow it too.
    [GeneratedRegex(@"\A[a-z][A-Za-z0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private readonly Dictionary<string, OverlayKindSpec> _kinds;

    private OverlayKindSet(KindSpecRegistry parameters, Dictionary<string, OverlayKindSpec> kinds)
    {
        Parameters = parameters;
        _kinds = kinds;
    }

    /// <summary>The parameter contracts of the overlay kinds (used through <see cref="KindSpecRegistry.CheckParams"/>).</summary>
    public KindSpecRegistry Parameters { get; }

    /// <summary>Every overlay kind, sorted by name.</summary>
    public IReadOnlyList<OverlayKindSpec> Kinds => _kinds.Values.OrderBy(k => k.Kind, StringComparer.Ordinal).ToList();

    /// <summary>The overlay block of a kind, or null when the kind is not an overlay kind.</summary>
    public OverlayKindSpec? Find(string kind) => _kinds.GetValueOrDefault(kind);

    /// <summary>
    /// Load every <c>*.kind.json</c> in <paramref name="overlayKindsDir"/> (normally <c>&lt;schemas&gt;/kinds/overlay</c>),
    /// validating each against kind.schema.json in <paramref name="schemas"/>. Throws on a missing folder or any problem.
    /// </summary>
    public static OverlayKindSet LoadDirectory(string overlayKindsDir, JsonSchemaSubset schemas)
    {
        ArgumentNullException.ThrowIfNull(overlayKindsDir);
        if (!Directory.Exists(overlayKindsDir))
            throw new DirectoryNotFoundException($"Asset overlay kind spec directory not found: {overlayKindsDir} (a schema folder from a build older than Phase 5a has none).");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(overlayKindsDir, "*" + KindSpecRegistry.KindFileSuffix, SearchOption.TopDirectoryOnly)
                                          .OrderBy(p => p, StringComparer.Ordinal))
            sources[Path.GetFileName(path)] = File.ReadAllText(path);
        return FromSources(sources, schemas);
    }

    /// <summary>Load overlay kind specs from in-memory text keyed by file name (what <see cref="LoadDirectory"/> calls;
    /// also used by tests and by the embedded copies).</summary>
    public static OverlayKindSet FromSources(IReadOnlyDictionary<string, string> sources, JsonSchemaSubset schemas)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(schemas);

        // Layer 1: the parameter contracts, through the template kinds' own loader (schema, then its rules). It throws,
        // listing every problem, so the overlay blocks below are only read from schema-valid files.
        KindSpecRegistry parameters = KindSpecRegistry.FromSources(sources, schemas);

        // Layer 2: the overlay blocks.
        var errors = new List<string>();
        var raw = new List<(string File, string Kind, JsonElement Overlay)>();
        foreach ((string file, string text) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
            JsonElement root = doc.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            if (!root.TryGetProperty("overlay", out JsonElement overlay))
            {
                errors.Add($"{file}: an asset overlay kind must have an \"overlay\" block (refs and parentKinds); kind '{kind}' has none.");
                continue;
            }
            raw.Add((file, kind, overlay.Clone()));
        }
        var known = new HashSet<string>(raw.Select(r => r.Kind), StringComparer.Ordinal);

        var kinds = new Dictionary<string, OverlayKindSpec>(StringComparer.Ordinal);
        foreach ((string file, string kind, JsonElement overlay) in raw)
        {
            string at = file + ": /overlay";
            var refs = new Dictionary<string, OverlayRefSpec>(StringComparer.Ordinal);
            foreach (JsonProperty role in overlay.GetProperty("refs").EnumerateObject())
            {
                string rat = at + "/refs/" + JsonSchemaSubset.EscapeToken(role.Name);
                if (!NamePattern().IsMatch(role.Name))
                    errors.Add($"{rat}: role name '{role.Name}' must be lowerCamelCase ASCII (a lower-case letter, then letters and digits).");
                AssetRefTarget target = ParseTarget(role.Value.GetProperty("target").GetString()!);
                List<string> recordKinds = role.Value.TryGetProperty("recordKinds", out JsonElement rk)
                    ? rk.EnumerateArray().Select(v => v.GetString()!).ToList() : new List<string>();
                if (target == AssetRefTarget.Record && !role.Value.TryGetProperty("recordKinds", out _))
                    errors.Add($"{rat}: a record target must list its recordKinds (the overlay kinds it may point at).");
                if (target != AssetRefTarget.Record && role.Value.TryGetProperty("recordKinds", out _))
                    errors.Add($"{rat}: only a record target may list recordKinds (this role points at a {TargetWord(target)}).");
                CheckKindList(recordKinds, rat + "/recordKinds", known, errors);
                var spec = new OverlayRefSpec(role.Name, target, role.Value.GetProperty("required").GetBoolean(), recordKinds,
                    role.Value.GetProperty("description").GetString()!);
                if (!refs.TryAdd(role.Name, spec))
                    errors.Add($"{rat}: role '{role.Name}' is declared more than once.");
            }

            List<string> parentKinds = overlay.GetProperty("parentKinds").EnumerateArray().Select(v => v.GetString()!).ToList();
            CheckKindList(parentKinds, at + "/parentKinds", known, errors);
            bool parentRequired = overlay.TryGetProperty("parentRequired", out JsonElement pr) && pr.GetBoolean();
            if (parentRequired && parentKinds.Count == 0)
                errors.Add($"{at}/parentRequired: a kind with no parentKinds sits at the top level, so it cannot require a parent.");

            kinds[kind] = new OverlayKindSpec(kind, refs, parentKinds, parentRequired);
        }

        if (errors.Count > 0)
            throw new InvalidDataException("Invalid v2 asset overlay kind spec(s) (" + errors.Count + " problem(s)):" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", errors));
        return new OverlayKindSet(parameters, kinds);
    }

    /// <summary>The kind-file spelling of a target type to the enum (the schema's enum admits nothing else).</summary>
    internal static AssetRefTarget ParseTarget(string text) => text switch
    {
        "bus" => AssetRefTarget.Bus,
        "line" => AssetRefTarget.Line,
        "transformer" => AssetRefTarget.Transformer,
        "generator" => AssetRefTarget.Generator,
        "demand" => AssetRefTarget.Demand,
        "record" => AssetRefTarget.Record,
        _ => throw new InvalidDataException($"Unknown asset overlay target type '{text}' (expected bus, line, transformer, generator, demand or record)."),
    };

    /// <summary>The document spelling of a target type (the member name a reference object uses).</summary>
    public static string TargetWord(AssetRefTarget target) => target switch
    {
        AssetRefTarget.Bus => "bus",
        AssetRefTarget.Line => "line",
        AssetRefTarget.Transformer => "transformer",
        AssetRefTarget.Generator => "generator",
        AssetRefTarget.Demand => "demand",
        _ => "record",
    };

    // Each named kind must be an overlay kind of this folder, and named once in its list.
    private static void CheckKindList(List<string> list, string at, HashSet<string> known, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in list)
        {
            if (!seen.Add(k)) errors.Add($"{at}: kind '{k}' is listed more than once.");
            if (!known.Contains(k))
                errors.Add($"{at}: '{k}' is not an asset overlay kind (known: {string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal))}).");
        }
    }
}
