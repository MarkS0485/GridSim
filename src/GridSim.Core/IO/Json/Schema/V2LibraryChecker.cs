using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Schema;

/// <summary>
/// Validates v2 template library files (<c>gridsim-library/2</c>, schema <c>library.schema.json</c>), alone or as a
/// set. Nothing in the production numeric path reads a library yet (Phase 1); this is the gate the files must pass.
///
/// Two layers, both fail-loud and both collecting every error:
/// <list type="number">
/// <item>Each file is validated against <c>library.schema.json</c>. That schema already enforces, with if/then/else,
/// that a placeholder template has every parameter and controller value null and an active one has none null.</item>
/// <item>If every file is schema-valid, the rules the schema subset cannot express are checked here, across all the
/// files together:
///   <list type="bullet">
///   <item>each template's <c>params</c> against its kind spec (<see cref="KindSpecRegistry.CheckParams"/>: unknown
///   kind or key, units, ranges, required keys, null rules, value types);</item>
///   <item>each <c>id@rev</c> is declared once across the whole set;</item>
///   <item>each per-parameter provenance key (anything but <c>_default</c>) names a parameter the template sets;</item>
///   <item>each controller reference resolves to a template in the set, of the slot's kind (the <c>exciter</c> slot
///   to an <c>exciter</c> template, and so on), and that target is NOT a placeholder: a placeholder is a named empty
///   slot and can never drive anything.</item>
///   </list>
/// </item>
/// </list>
/// The second layer is skipped when the first finds anything, because its checks assume the shape the schema
/// guarantees; the author fixes the shape first and then sees the semantic errors.
/// </summary>
public static class V2LibraryChecker
{
    /// <summary>The document tag every library file carries, matched exactly.</summary>
    public const string SchemaTag = "gridsim-library/2";

    /// <summary>The schema file (in the loaded schema set) library files are validated against.</summary>
    public const string LibrarySchemaFile = "library.schema.json";

    private sealed record TemplateInfo(string File, string Pointer, string Kind, bool Placeholder);

    /// <summary>
    /// Validate several library documents together, given as (name, root) pairs. Returns every error, each with
    /// its document name in <see cref="SchemaError.File"/>.
    /// </summary>
    public static IReadOnlyList<SchemaError> Check(
        IReadOnlyList<(string Name, JsonElement Root)> libraries, SchemaValidator validator, KindSpecRegistry kinds)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(kinds);

        // Layer 1: the schema, per document.
        var errors = new List<SchemaError>();
        foreach ((string name, JsonElement root) in libraries)
            errors.AddRange(validator.Validate(root, LibrarySchemaFile).Select(e => e with { File = name }));
        if (errors.Count > 0) return errors;

        // Layer 2a: index every template by id@rev across the whole set, refusing duplicates.
        var index = new Dictionary<string, TemplateInfo>(StringComparer.Ordinal);
        foreach ((string name, JsonElement root) in libraries)
        {
            int i = 0;
            foreach (JsonElement t in root.GetProperty("templates").EnumerateArray())
            {
                string ptr = "/templates/" + i.ToString(CultureInfo.InvariantCulture);
                string key = RefOf(t);
                var info = new TemplateInfo(name, ptr, t.GetProperty("kind").GetString()!,
                    t.GetProperty("status").GetString() == "placeholder");
                if (!index.TryAdd(key, info))
                    errors.Add(new SchemaError(ptr + "/id", $"template {key} is declared twice (first in {index[key].File} at {index[key].Pointer}).", name));
                i++;
            }
        }

        // Layer 2b: per template, kind spec, provenance keys and controller targets.
        foreach ((string name, JsonElement root) in libraries)
        {
            int i = 0;
            foreach (JsonElement t in root.GetProperty("templates").EnumerateArray())
            {
                string ptr = "/templates/" + i.ToString(CultureInfo.InvariantCulture);
                i++;
                string kind = t.GetProperty("kind").GetString()!;
                bool placeholder = t.GetProperty("status").GetString() == "placeholder";
                JsonElement ps = t.GetProperty("params");

                errors.AddRange(kinds.CheckParams(kind, ps, placeholder, ptr + "/params").Select(e => e with { File = name }));

                foreach (JsonProperty prov in t.GetProperty("provenance").EnumerateObject())
                    if (prov.Name != "_default" && !ps.TryGetProperty(prov.Name, out _))
                        errors.Add(new SchemaError(ptr + "/provenance/" + JsonSchemaSubset.EscapeToken(prov.Name),
                            $"provenance is given for '{prov.Name}', which is not a parameter of this template.", name));

                if (!t.TryGetProperty("controllers", out JsonElement controllers)) continue;
                foreach (JsonProperty slot in controllers.EnumerateObject())
                {
                    if (slot.Value.ValueKind == JsonValueKind.Null) continue;   // only legal in a placeholder (schema)
                    string at = ptr + "/controllers/" + slot.Name;
                    string target = slot.Value.GetString()!;
                    if (!index.TryGetValue(target, out TemplateInfo? hit))
                        errors.Add(new SchemaError(at, $"controller reference {target} does not resolve to any template in the loaded libraries.", name));
                    else if (hit.Placeholder)
                        errors.Add(new SchemaError(at, $"controller reference {target} is a placeholder template ({hit.File} at {hit.Pointer}); a placeholder can never be a controller target.", name));
                    else if (hit.Kind != slot.Name)
                        errors.Add(new SchemaError(at, $"the '{slot.Name}' slot must reference a '{slot.Name}' template, but {target} is kind '{hit.Kind}'.", name));
                }
            }
        }
        return errors;
    }

    /// <summary>
    /// Read, validate and cross-check a set of library files; throws one <see cref="InvalidDataException"/>
    /// listing every error with its file (a file that does not parse is named too).
    /// </summary>
    public static void ValidateFilesOrThrow(IEnumerable<string> paths, SchemaValidator validator, KindSpecRegistry kinds)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // GR-656: a document is named by its file name, as before, unless two libraries share that name in different
        // folders; those are named by their full path, so neither gets the other's errors or an ambiguous "first in X".
        List<string> all = paths.ToList();
        var nameCount = all.GroupBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var docs = new List<(string, JsonElement)>();
        foreach (string path in all)
        {
            using JsonDocument doc = JsonSchemaSubset.ParseFile(path);
            string fileName = Path.GetFileName(path);
            docs.Add((nameCount[fileName] > 1 ? Path.GetFullPath(path) : fileName, doc.RootElement.Clone()));
        }
        IReadOnlyList<SchemaError> errors = Check(docs, validator, kinds);
        SchemaValidator.ThrowIfAny(errors, "v2 template library (" + docs.Count + " file(s))");
    }

    /// <summary>
    /// The <c>id@rev</c> reference of a schema-valid template. The revision is printed as a plain whole number
    /// (the schema's integer type also admits a written <c>1.0</c>, which must still be found by a ref <c>@1</c>).
    /// </summary>
    private static string RefOf(JsonElement template)
        => template.GetProperty("id").GetString() + "@" +
           ((long)template.GetProperty("rev").GetDouble()).ToString(CultureInfo.InvariantCulture);
}
