using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.Overlay;

/// <summary>
/// Phase 5a (2026-10-20): checks the asset overlay files of one case (<c>gridsim-assets/2</c>, schema
/// <c>assets.schema.json</c>) as one set. Fail loud, and EVERY problem is listed, each naming the record id (in the
/// message) and the field (in the JSON pointer); nothing stops at the first.
///
/// <para>Two layers, as <see cref="V2LibraryChecker"/> has:</para>
/// <list type="number">
/// <item>Each file against <c>assets.schema.json</c> (shape, scalar params, the one-member reference objects).</item>
/// <item>When every file is schema-valid, the rules the schema subset cannot express, across all the files together:
///   <list type="bullet">
///   <item>the kind is an asset overlay kind (<see cref="OverlayKindSet"/>; a template kind such as oltc is not);</item>
///   <item>params against the kind spec (<see cref="KindSpecRegistry.CheckParams"/>, unchanged: unknown or missing
///   parameter, type, unit, range), and no parameter or role written twice;</item>
///   <item>ids unique across every file of the case;</item>
///   <item>parent: it exists, the kind may have one and the parent is of one of the kind's parentKinds, a kind that
///   requires a parent has one, and no parent chain loops;</item>
///   <item>refs: every role is declared by the kind, with the target type the kind gives it, every required role is
///   given, and every reference resolves: another record by id (of one of the role's recordKinds, never itself), and
///   when the case is known (<paramref name="targets"/> given) a bus by id and a line, transformer, generator or
///   demand by a name exactly one element of that type carries.</item>
///   </list>
/// </item>
/// </list>
/// The second layer is skipped when the first finds anything: its checks assume the shape the schema guarantees.
/// Without <paramref name="targets"/> (a lone overlay file given to the <c>validate</c> verb), references to the
/// case's elements are not resolved; the loader always resolves them.
/// </summary>
public static class V2AssetChecker
{
    /// <summary>The document tag every asset overlay file carries, matched exactly.</summary>
    public const string SchemaTag = "gridsim-assets/2";

    /// <summary>The schema file (in the loaded schema set) overlay files are validated against.</summary>
    public const string SchemaFile = "assets.schema.json";

    private sealed record Indexed(string File, string Pointer, string Id, string Kind, string? Parent, JsonElement Record);

    /// <summary>
    /// Check the overlay files of one case, given as (name, root) pairs; the name is what the errors carry in
    /// <see cref="SchemaError.File"/>. <paramref name="targets"/> is the case's elements, or null when no case is known.
    /// </summary>
    public static IReadOnlyList<SchemaError> Check(
        IReadOnlyList<(string Name, JsonElement Root)> documents, SchemaValidator validator, OverlayKindSet kinds, AssetOverlayTargets? targets)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(kinds);

        // ---- Layer 1: the schema, per document. A message under /records/i names the record's id when it has one. ----
        var errors = new List<SchemaError>();
        foreach ((string name, JsonElement root) in documents)
            foreach (SchemaError e in validator.Validate(root, SchemaFile))
                errors.Add(e with { Message = Prefix(root, e.Pointer) + e.Message, File = name });
        if (errors.Count > 0) return errors;

        // ---- Layer 2a: index every record by id across the whole set, refusing a duplicate. ----
        var all = new List<Indexed>();
        var byId = new Dictionary<string, Indexed>(StringComparer.Ordinal);
        foreach ((string name, JsonElement root) in documents)
        {
            int i = 0;
            foreach (JsonElement r in root.GetProperty("records").EnumerateArray())
            {
                string ptr = "/records/" + i.ToString(CultureInfo.InvariantCulture);
                i++;
                var rec = new Indexed(name, ptr, r.GetProperty("id").GetString()!, r.GetProperty("kind").GetString()!,
                    r.TryGetProperty("parent", out JsonElement p) ? p.GetString() : null, r);
                all.Add(rec);
                if (!byId.TryAdd(rec.Id, rec))
                    errors.Add(new SchemaError(ptr + "/id",
                        $"record '{rec.Id}': the id is declared twice (first in {byId[rec.Id].File} at {byId[rec.Id].Pointer}); an id must be unique across every overlay file of the case.", name));
            }
        }

        // ---- Layer 2b: per record, the kind, its params, its parent and its refs. ----
        foreach (Indexed rec in all)
        {
            OverlayKindSpec? spec = kinds.Find(rec.Kind);
            if (spec is null)
            {
                errors.Add(new SchemaError(rec.Pointer + "/kind",
                    $"record '{rec.Id}': '{rec.Kind}' is not an asset overlay kind (known: {string.Join(", ", kinds.Kinds.Select(k => k.Kind))}).", rec.File));
                continue;   // params, parent and refs all need the kind
            }

            JsonElement ps = rec.Record.GetProperty("params");
            foreach (string twice in Duplicates(ps))
                errors.Add(new SchemaError(rec.Pointer + "/params/" + JsonSchemaSubset.EscapeToken(twice),
                    $"record '{rec.Id}': parameter '{twice}' is written more than once (the reader would keep only one of them).", rec.File));
            foreach (SchemaError e in kinds.Parameters.CheckParams(rec.Kind, ps, placeholder: false, rec.Pointer + "/params"))
                errors.Add(e with { Message = $"record '{rec.Id}': " + e.Message, File = rec.File });

            CheckParent(rec, spec, byId, errors);
            CheckRefs(rec, spec, byId, targets, errors);
        }

        // ---- Layer 2c: no parent chain may loop (each loop reported once, at its first record in document order). ----
        CheckCycles(all, byId, errors);
        return errors;
    }

    /// <summary>
    /// Phase 5c (2026-10-20): the checker's WARNINGS, a separate list beside the errors of <see cref="Check"/> (which is
    /// unchanged: a warning never becomes an error, and no error is moved here). Today they are the substation topology
    /// warnings (<see cref="SubstationTopology"/>): sections of one authored bus that the normal switch states split into
    /// separate nodes, and sections of different buses that closed devices join into one. Warnings are only worked out
    /// for a set that passes <see cref="Check"/> (their rules assume the shape it guarantees), so a set with any error
    /// has no warnings here; the arguments are those of <see cref="Check"/>.
    /// </summary>
    public static IReadOnlyList<SubstationTopologyWarning> Warnings(
        IReadOnlyList<(string Name, JsonElement Root)> documents, SchemaValidator validator, OverlayKindSet kinds, AssetOverlayTargets? targets)
    {
        if (Check(documents, validator, kinds, targets).Count > 0) return Array.Empty<SubstationTopologyWarning>();
        return SubstationTopology.WarningsOf(CaseAssetOverlay.FromCheckedDocuments(documents));
    }

    // A parent must exist, be allowed by the kind and be of one of its parentKinds; a kind that needs a parent has one.
    private static void CheckParent(Indexed rec, OverlayKindSpec spec, Dictionary<string, Indexed> byId, List<SchemaError> errors)
    {
        string at = rec.Pointer + "/parent";
        if (rec.Parent is null)
        {
            if (spec.ParentRequired)
                errors.Add(new SchemaError(rec.Pointer,
                    $"record '{rec.Id}': a '{rec.Kind}' record must have a parent (of kind {string.Join(" or ", spec.ParentKinds)}), and it has none.", rec.File));
            return;
        }
        if (spec.ParentKinds.Count == 0)
        {
            errors.Add(new SchemaError(at, $"record '{rec.Id}': a '{rec.Kind}' record sits at the top level and takes no parent, but parent '{rec.Parent}' is given.", rec.File));
            return;
        }
        if (!byId.TryGetValue(rec.Parent, out Indexed? parent))
        {
            errors.Add(new SchemaError(at, $"record '{rec.Id}': parent '{rec.Parent}' is not a record of the overlay.", rec.File));
            return;
        }
        if (!spec.ParentKinds.Contains(parent.Kind, StringComparer.Ordinal))
            errors.Add(new SchemaError(at,
                $"record '{rec.Id}': the parent of a '{rec.Kind}' record must be of kind {string.Join(" or ", spec.ParentKinds)}, but '{parent.Id}' is a '{parent.Kind}'.", rec.File));
    }

    // Every role declared, of the right target type, every required role given, every reference resolving.
    private static void CheckRefs(Indexed rec, OverlayKindSpec spec, Dictionary<string, Indexed> byId, AssetOverlayTargets? targets, List<SchemaError> errors)
    {
        JsonElement refs = rec.Record.GetProperty("refs");
        string at = rec.Pointer + "/refs";
        foreach (string twice in Duplicates(refs))
            errors.Add(new SchemaError(at + "/" + JsonSchemaSubset.EscapeToken(twice),
                $"record '{rec.Id}': role '{twice}' is written more than once (the reader would keep only one of them).", rec.File));

        foreach (OverlayRefSpec role in spec.Refs.Values.Where(r => r.Required))
            if (!refs.TryGetProperty(role.Role, out _))
                errors.Add(new SchemaError(at,
                    $"record '{rec.Id}': a '{rec.Kind}' record requires role '{role.Role}' (a {OverlayKindSet.TargetWord(role.Target)}), which is missing.", rec.File));

        foreach (JsonProperty m in refs.EnumerateObject())
        {
            string rat = at + "/" + JsonSchemaSubset.EscapeToken(m.Name);
            if (!spec.Refs.TryGetValue(m.Name, out OverlayRefSpec? role))
            {
                errors.Add(new SchemaError(rat, spec.Refs.Count == 0
                    ? $"record '{rec.Id}': kind '{rec.Kind}' declares no roles, so role '{m.Name}' cannot be given."
                    : $"record '{rec.Id}': kind '{rec.Kind}' has no role '{m.Name}' (declared: {string.Join(", ", spec.Refs.Keys)}).", rec.File));
                continue;
            }
            AssetOverlayRef r = CaseAssetOverlay.ReadRef(m);
            string written = OverlayKindSet.TargetWord(r.Target);
            if (r.Target != role.Target)
            {
                errors.Add(new SchemaError(rat,
                    $"record '{rec.Id}': role '{m.Name}' of a '{rec.Kind}' record points at a {OverlayKindSet.TargetWord(role.Target)}, but a {written} is written.", rec.File));
                continue;
            }
            rat += "/" + written;
            switch (r.Target)
            {
                case AssetRefTarget.Record:
                    if (string.Equals(r.Key, rec.Id, StringComparison.Ordinal))
                        errors.Add(new SchemaError(rat, $"record '{rec.Id}': role '{m.Name}' points at the record itself.", rec.File));
                    else if (!byId.TryGetValue(r.Key!, out Indexed? hit))
                        errors.Add(new SchemaError(rat, $"record '{rec.Id}': role '{m.Name}' points at record '{r.Key}', which is not a record of the overlay.", rec.File));
                    else if (!role.RecordKinds.Contains(hit.Kind, StringComparer.Ordinal))
                        errors.Add(new SchemaError(rat,
                            $"record '{rec.Id}': role '{m.Name}' must point at a record of kind {string.Join(" or ", role.RecordKinds)}, but '{hit.Id}' is a '{hit.Kind}'.", rec.File));
                    break;
                case AssetRefTarget.Bus:
                    if (r.Bus is null)
                        errors.Add(new SchemaError(rat, $"record '{rec.Id}': role '{m.Name}' gives bus id {m.Value.GetProperty("bus").GetRawText()}, which is not a whole number a bus id can be.", rec.File));
                    else if (targets is not null && !targets.HasBus(r.Bus.Value))
                        errors.Add(new SchemaError(rat, $"record '{rec.Id}': role '{m.Name}' points at bus {r.Bus.Value.ToString(CultureInfo.InvariantCulture)}, which is not a bus of the case.", rec.File));
                    break;
                default:
                    if (targets is null) break;   // a lone overlay file: no case to resolve against
                    int n = targets.CountOf(r.Target, r.Key!);
                    if (n == 0)
                        errors.Add(new SchemaError(rat, $"record '{rec.Id}': role '{m.Name}' points at {written} '{r.Key}', which is not a {written} of the case.", rec.File));
                    else if (n > 1)
                        errors.Add(new SchemaError(rat,
                            $"record '{rec.Id}': role '{m.Name}' points at {written} '{r.Key}', which is ambiguous: {n.ToString(CultureInfo.InvariantCulture)} {written}s of the case carry that name.", rec.File));
                    break;
            }
        }
    }

    // Parent pointers form a forest unless a chain loops. Walk each record's chain once; a chain that comes back to a
    // record on the current walk is a loop, reported once with every record on it.
    private static void CheckCycles(List<Indexed> all, Dictionary<string, Indexed> byId, List<SchemaError> errors)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (Indexed start in all)
        {
            if (done.Contains(start.Id)) continue;
            var path = new List<Indexed>();
            var onPath = new Dictionary<string, int>(StringComparer.Ordinal);
            Indexed? cur = start;
            while (cur is not null && !done.Contains(cur.Id))
            {
                if (onPath.TryGetValue(cur.Id, out int from))
                {
                    List<Indexed> loop = path.Skip(from).ToList();
                    Indexed first = loop[0];
                    errors.Add(new SchemaError(first.Pointer + "/parent",
                        $"record '{first.Id}': the parent chain loops ({string.Join(" -> ", loop.Select(x => x.Id).Append(first.Id))}); a record cannot sit, directly or through others, under itself.", first.File));
                    break;
                }
                onPath[cur.Id] = path.Count;
                path.Add(cur);
                cur = cur.Parent is not null && byId.TryGetValue(cur.Parent, out Indexed? next) ? next : null;
            }
            foreach (Indexed x in path) done.Add(x.Id);
        }
    }

    // JsonDocument keeps a repeated member name and a lookup takes one of them, so a repeat is found here explicitly.
    private static IEnumerable<string> Duplicates(JsonElement obj)
        => obj.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);

    // "record 'id': " for a schema error inside /records/i when that record has a string id; empty otherwise.
    private static string Prefix(JsonElement root, string pointer)
    {
        const string head = "/records/";
        if (!pointer.StartsWith(head, StringComparison.Ordinal)) return "";
        string rest = pointer[head.Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string index = slash < 0 ? rest : rest[..slash];
        if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return "";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("records", out JsonElement records)
            || records.ValueKind != JsonValueKind.Array || i >= records.GetArrayLength()) return "";
        JsonElement r = records[i];
        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? $"record '{id.GetString()}': " : "";
    }
}
