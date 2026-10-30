using System.Globalization;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// Reading helpers for records at file level: a member's value by canonical name (looked up with the document's
/// comparer, so a non-canonical v1 spelling still reads, as it does for the loader).
/// </summary>
internal static class RecordRead
{
    /// <summary>The value node of a member, or null when absent (or the node is not an object).</summary>
    public static JsonSourceNode? Member(JsonSourceNode record, string name, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Kind == JsonSourceKind.Object ? record.FindMember(name, comparer)?.Value : null;
    }

    /// <summary>A member's value as an int, when it is a whole number in range; null otherwise. Bug hunt 2026-10-29,
    /// finding 35: a whole number written with a fraction part (<c>12.0</c>) counts, as it does for the v2 loader
    /// (JsonWhole) and the schema's integer. It used to read only an integral literal, so a bus written "id": 12.0 was
    /// not indexed as bus 12 (references to it were reported dangling, and it could be deleted while referenced) and a
    /// reference written "bus": 12.0 was invisible to the dangling-reference check and to Rename cascades. The overlay
    /// path already read it this way (<see cref="ReferenceIndex.OverlayBusId"/>), and now shares the rule.</summary>
    public static int? Int(JsonSourceNode record, string name, StringComparer comparer)
        => Member(record, name, comparer) is { } value ? ReferenceIndex.OverlayBusId(value) : null;

    /// <summary>A member's value as a string, when it is a JSON string; null otherwise.</summary>
    public static string? Str(JsonSourceNode record, string name, StringComparer comparer)
        => Member(record, name, comparer) is { Kind: JsonSourceKind.String } n ? n.StringValue : null;

    /// <summary>A member's value as a double, when it is a number; null otherwise.</summary>
    public static double? Num(JsonSourceNode record, string name, StringComparer comparer)
        => Member(record, name, comparer) is { Kind: JsonSourceKind.Number } n ? n.NumberValue : null;

    /// <summary>
    /// A short description of a record for messages: "generation.json #17 'Drax Biomass'" (v1), or
    /// "good.network.json generators #0 'Big Nuclear'" (v2, where one file holds several arrays).
    /// </summary>
    public static string Describe(CaseDocument doc, JsonSourceNode record, int index, string arrayPointer = "")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(arrayPointer);
        // Phase 5b: an asset overlay record is named by its id and kind ("site.assets.json record 'sub.north' (substation)").
        if (doc.SchemaFile == CaseSession.V2AssetsSchema && arrayPointer == CaseSession.OverlayRecordsPointer)
            return doc.FileName + " record '" + (Str(record, "id", doc.KeyComparer) ?? "#" + index.ToString(CultureInfo.InvariantCulture)) + "'"
                   + (Str(record, "kind", doc.KeyComparer) is string kind ? " (" + kind + ")" : "");
        string name = Str(record, "name", doc.KeyComparer) is { } n ? " '" + n + "'" : "";
        bool busRecord = doc.Spec?.FileName == "buses.json" || arrayPointer == "/" + CaseRoles.Buses;
        string id = busRecord && Int(record, "id", doc.KeyComparer) is int busId ? " bus " + busId.ToString(CultureInfo.InvariantCulture) : "";
        string where = arrayPointer.Length > 0 ? " " + arrayPointer.TrimStart('/') : "";
        return doc.FileName + where + " #" + index.ToString(CultureInfo.InvariantCulture) + id + name;
    }
}

/// <summary>
/// Who refers to what in a case, built from the current documents (SCHEMA-EDITOR-PLAN.md section 3), by role so v1
/// and v2 share it: bus ids (every schema field that is a bus reference), branch names (an OLTC's transformer, a
/// boundary's branches, a protection's target branch) and asset-class names (v1: dynamicsClass on generators, lines
/// and transformers, binding rules, protections; v2 binds templates instead, which the editor does not cascade).
/// In v2 the references span every network file of the case. Rebuilt for each operation that needs it; on gb-full
/// that is about twenty thousand records.
/// </summary>
internal sealed class ReferenceIndex
{
    /// <summary>One reference: the record holding it, the field, and a description for messages.</summary>
    /// <param name="Record">The record holding the reference.</param>
    /// <param name="Field">The referring field ("bus", "transformer"; for an overlay record "parent" or "refs.role").</param>
    /// <param name="What">The referring record as messages name it.</param>
    /// <param name="ValuePointer">Phase 5b: the JSON pointer of the referring value itself when it is not the member
    /// <paramref name="Field"/> of <paramref name="Record"/> (an overlay reference sits inside the record's refs, for
    /// example "/records/3/refs/bus/bus"); null for every model referrer, whose value is that member.</param>
    internal sealed record Referrer(RecordRef Record, string Field, string What, string? ValuePointer = null);

    /// <summary>Fields holding a branch name, by record schema location and field (v1 and v2 shapes).</summary>
    internal static readonly IReadOnlyList<(SchemaLocation Shape, string Field)> BranchRefFields = new[]
    {
        (new SchemaLocation("onloadtapchanger.schema.json", "/$defs/oltc"), "transformer"),
        (new SchemaLocation("boundaries.schema.json", "/$defs/boundaryBranch"), "branch"),
        (new SchemaLocation("protections.schema.json", "/$defs/protection"), "targetBranch"),
        (new SchemaLocation(CaseSession.V2NetworkSchema, "/$defs/oltc"), "transformer"),
        (new SchemaLocation(CaseSession.V2NetworkSchema, "/$defs/boundary/properties/branches/items"), "branch"),
        (new SchemaLocation(CaseSession.V2NetworkSchema, "/$defs/protection"), "targetBranch"),
    };

    /// <summary>The bus record shapes (v1 and v2), whose "id" is the key itself, not a reference to a bus.</summary>
    internal static readonly IReadOnlyList<SchemaLocation> BusShapes = new[]
    {
        new SchemaLocation("buses.schema.json", "/$defs/bus"),
        new SchemaLocation(CaseSession.V2NetworkSchema, "/$defs/bus"),
    };

    /// <summary>
    /// True when a field of a record REFERS to a bus: its schema is the shared bus-id definition, and it is not the
    /// bus record's own id (which uses the same definition, but is the key every reference points at).
    /// </summary>
    internal static bool RefersToBus(RecordShape shape, FieldSpec f)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(f);
        return f.IsBusReference && !(f.Name == "id" && BusShapes.Contains(shape.Location));
    }

    /// <summary>Fields holding an asset-class name, by record schema location and field (v1 only).</summary>
    internal static readonly IReadOnlyList<(SchemaLocation Shape, string Field)> ClassRefFields = new[]
    {
        (new SchemaLocation("generation.schema.json", "/$defs/generator"), "dynamicsClass"),
        (new SchemaLocation("transmission.schema.json", "/$defs/line"), "dynamicsClass"),
        (new SchemaLocation("transformer.schema.json", "/$defs/transformer"), "dynamicsClass"),
        (new SchemaLocation("asset-bindings.schema.json", "/$defs/assetBinding"), "class"),
        (new SchemaLocation("protections.schema.json", "/$defs/protection"), "class"),
    };

    private readonly Dictionary<int, List<Referrer>> _bus = new();
    private readonly Dictionary<string, List<Referrer>> _branch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Referrer>> _class = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, RecordRef> _busRecords = new();
    private readonly Dictionary<int, int> _busIdCount = new();
    private readonly Dictionary<string, List<RecordRef>> _branchRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordRef> _classRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordRef> _oltcs = new();
    private readonly List<RecordRef?> _oltcBranch = new();

    // Phase 5b (2026-10-20): the asset overlay, kept APART from the model's references above, so everything that reads
    // those (the Run-mode ASSETS where-used list among them) is unchanged; the editing rules ask for these explicitly.
    private readonly Dictionary<int, List<Referrer>> _overlayBus = new();
    private readonly Dictionary<AssetRefTarget, Dictionary<string, List<Referrer>>> _overlayNamed = new()
    {
        [AssetRefTarget.Line] = new(StringComparer.Ordinal),
        [AssetRefTarget.Transformer] = new(StringComparer.Ordinal),
        [AssetRefTarget.Generator] = new(StringComparer.Ordinal),
        [AssetRefTarget.Demand] = new(StringComparer.Ordinal),
    };
    private readonly Dictionary<string, List<Referrer>> _overlayRecord = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordRef> _overlayRecords = new(StringComparer.Ordinal);

    private ReferenceIndex() { }

    /// <summary>Bus id -> the bus record (the first, if an id is duplicated).</summary>
    public IReadOnlyDictionary<int, RecordRef> Buses => _busRecords;

    /// <summary>Bus id -> how many bus records carry it (more than one is a duplicate).</summary>
    public IReadOnlyDictionary<int, int> BusIdCounts => _busIdCount;

    /// <summary>Branch name -> every branch carrying it, lines first then transformers (the model's branch order).</summary>
    public IReadOnlyDictionary<string, List<RecordRef>> Branches => _branchRecords;

    /// <summary>Asset-class name (ignoring case, as the class library does) -> the class record.</summary>
    public IReadOnlyDictionary<string, RecordRef> Classes => _classRecords;

    /// <summary>The OLTC records in file order.</summary>
    public IReadOnlyList<RecordRef> Oltcs => _oltcs;

    /// <summary>
    /// The branch each OLTC record drives, aligned to <see cref="Oltcs"/>, by the rule of
    /// SteadyStateSolver.MapOltcRecordsToBranches (N046) mirrored at file level: the first branch of the name (lines
    /// then transformers) with the controlled bus at an end, else the first branch of the name; null when no branch
    /// carries it. A test pins this equal to the Core method on every bundled case.
    /// </summary>
    public IReadOnlyList<RecordRef?> OltcBranch => _oltcBranch;

    /// <summary>Every reference to a bus id (empty when none).</summary>
    public IReadOnlyList<Referrer> BusReferrers(int id) => _bus.TryGetValue(id, out List<Referrer>? l) ? l : Array.Empty<Referrer>();

    /// <summary>Every reference to a branch name (empty when none).</summary>
    public IReadOnlyList<Referrer> BranchReferrers(string name) => _branch.TryGetValue(name, out List<Referrer>? l) ? l : Array.Empty<Referrer>();

    /// <summary>Every reference to an asset-class name (empty when none).</summary>
    public IReadOnlyList<Referrer> ClassReferrers(string name) => _class.TryGetValue(name, out List<Referrer>? l) ? l : Array.Empty<Referrer>();

    /// <summary>Phase 5b: every asset overlay reference to a bus id (empty when none, and for a case without an overlay).</summary>
    public IReadOnlyList<Referrer> OverlayBusReferrers(int id) => _overlayBus.TryGetValue(id, out List<Referrer>? l) ? l : Array.Empty<Referrer>();

    /// <summary>Phase 5b: every asset overlay reference to a line, transformer, generator or demand by name.</summary>
    public IReadOnlyList<Referrer> OverlayNameReferrers(AssetRefTarget target, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_overlayNamed.TryGetValue(target, out Dictionary<string, List<Referrer>>? byName))
            throw new ArgumentOutOfRangeException(nameof(target), target, "only line, transformer, generator and demand targets are named.");
        return byName.TryGetValue(name, out List<Referrer>? l) ? l : Array.Empty<Referrer>();
    }

    /// <summary>Phase 5b: everything in the overlay that points at the overlay record <paramref name="id"/>: its children
    /// (their "parent") and every record reference to it.</summary>
    public IReadOnlyList<Referrer> OverlayRecordReferrers(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _overlayRecord.TryGetValue(id, out List<Referrer>? l) ? l : Array.Empty<Referrer>();
    }

    /// <summary>Phase 5b: overlay record id -> the record (the first, if an id is duplicated), across every overlay file.</summary>
    public IReadOnlyDictionary<string, RecordRef> OverlayRecords => _overlayRecords;

    /// <summary>The largest bus id in the case (0 when there are no buses).</summary>
    public int MaxBusId => _busRecords.Count == 0 ? 0 : _busRecords.Keys.Max();

    /// <summary>Build the index from the session's current documents.</summary>
    public static ReferenceIndex Build(CaseSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var ix = new ReferenceIndex();

        // Keys: buses by id, branches by name (every lines collection, then every transformers collection), classes.
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Buses))
            Each(s, c, (r, n, cmp) =>
            {
                if (RecordRead.Int(n, "id", cmp) is not int id) return;
                ix._busRecords.TryAdd(id, r);
                ix._busIdCount[id] = ix._busIdCount.GetValueOrDefault(id) + 1;
            });
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Lines).Concat(s.CollectionsOf(CaseRoles.Transformers)))
            Each(s, c, (r, n, cmp) =>
            {
                if (RecordRead.Str(n, "name", cmp) is not string nm) return;
                if (!ix._branchRecords.TryGetValue(nm, out List<RecordRef>? list)) ix._branchRecords[nm] = list = new List<RecordRef>(1);
                list.Add(r);
            });
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.AssetClasses))
            Each(s, c, (r, n, cmp) => { if (RecordRead.Str(n, "name", cmp) is string nm) ix._classRecords.TryAdd(nm, r); });

        // References, driven by the schema: every bus-reference field of every record of every collection.
        foreach (RecordCollection c in s.Collections)
        {
            CaseDocument d = s.Documents[c.Document];
            JsonSourceNode? root = s.ArrayOf(c);
            if (root is null) continue;
            var busFields = c.Shape.Fields.Where(f => RefersToBus(c.Shape, f)).Select(f => f.Name).ToList();
            var branchFields = BranchRefFields.Where(b => b.Shape == c.Shape.Location).Select(b => b.Field).ToList();
            var classFields = ClassRefFields.Where(x => x.Shape == c.Shape.Location).Select(x => x.Field).ToList();
            StringComparer cmp = d.KeyComparer;
            for (int i = 0; i < root.Elements.Count; i++)
            {
                JsonSourceNode rec = root.Elements[i];
                if (rec.Kind != JsonSourceKind.Object) continue;
                var rr = new RecordRef(d.Id, c.ArrayPointer, i);
                string what = RecordRead.Describe(d, rec, i, c.ArrayPointer);
                foreach (string f in busFields)
                    if (RecordRead.Int(rec, f, cmp) is int bus) Add(ix._bus, bus, new Referrer(rr, f, what));
                foreach (string f in branchFields)
                    if (RecordRead.Str(rec, f, cmp) is string br) Add(ix._branch, br, new Referrer(rr, f, what));
                foreach (string f in classFields)
                    if (RecordRead.Str(rec, f, cmp) is string cl) Add(ix._class, cl, new Referrer(rr, f, what));

                // Boundary branches are a nested array of records naming branches.
                if (c.Role == CaseRoles.Boundaries && rec.FindMember("branches", cmp) is { Value.Kind: JsonSourceKind.Array } bm)
                {
                    string arrayPointer = rr.Pointer + "/" + bm.Name;
                    for (int j = 0; j < bm.Value.Elements.Count; j++)
                        if (RecordRead.Str(bm.Value.Elements[j], "branch", cmp) is string br)
                            Add(ix._branch, br, new Referrer(new RecordRef(d.Id, arrayPointer, j), "branch", what + " branch " + j.ToString(CultureInfo.InvariantCulture)));
                }

                if (c.Role == CaseRoles.Oltcs) ix._oltcs.Add(rr);
            }
        }

        // Phase 5b: the asset overlay's references (only cases that list "assets" have overlay collections).
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Overlay)) IndexOverlay(s, c, ix);

        // The OLTC -> branch mirror of SteadyStateSolver.MapOltcRecordsToBranches.
        foreach (RecordRef o in ix._oltcs)
        {
            CaseDocument od = s.Documents[o.Document];
            JsonSourceNode rec = od.Text.Resolve(o.Pointer)!;
            StringComparer cmp = od.KeyComparer;
            RecordRef? target = null;
            // Absent members read as the DTO defaults, exactly as the loader builds the model: transformer "" and
            // controlsBus / from / to 0.
            string nm = RecordRead.Str(rec, "transformer", cmp) ?? "";
            if (ix._branchRecords.TryGetValue(nm, out List<RecordRef>? named))
            {
                int controls = RecordRead.Int(rec, "controlsBus", cmp) ?? 0;
                foreach (RecordRef b in named)
                {
                    CaseDocument bd = s.Documents[b.Document];
                    JsonSourceNode br = bd.Text.Resolve(b.Pointer)!;
                    int from = RecordRead.Int(br, "from", bd.KeyComparer) ?? 0, to = RecordRead.Int(br, "to", bd.KeyComparer) ?? 0;
                    if (from == controls || to == controls) { target = b; break; }
                }
                target ??= named[0];
            }
            ix._oltcBranch.Add(target);
        }
        return ix;
    }

    // Phase 5b: one overlay file's records: ids, parents and refs, each referrer carrying the pointer of its value token
    // (so a rename cascade replaces exactly that token). Values of the wrong shape are skipped here: the save gate's
    // schema stage and the overlay checker report them, and this index never decides whether an overlay is valid.
    private static void IndexOverlay(CaseSession s, RecordCollection c, ReferenceIndex ix)
    {
        CaseDocument d = s.Documents[c.Document];
        JsonSourceNode? array = s.ArrayOf(c);
        if (array is null) return;
        StringComparer cmp = d.KeyComparer;
        for (int i = 0; i < array.Elements.Count; i++)
        {
            JsonSourceNode rec = array.Elements[i];
            if (rec.Kind != JsonSourceKind.Object) continue;
            var rr = new RecordRef(d.Id, c.ArrayPointer, i);
            string what = RecordRead.Describe(d, rec, i, c.ArrayPointer);
            string at = rr.Pointer;
            if (RecordRead.Str(rec, "id", cmp) is string id) ix._overlayRecords.TryAdd(id, rr);
            if (RecordRead.Str(rec, "parent", cmp) is string parent) Add(ix._overlayRecord, parent, new Referrer(rr, "parent", what, at + "/parent"));
            if (RecordRead.Member(rec, "refs", cmp) is not { Kind: JsonSourceKind.Object } refs) continue;
            foreach (JsonSourceMember role in refs.Members)
            {
                if (role.Value.Kind != JsonSourceKind.Object || role.Value.Members.Count != 1) continue;
                JsonSourceMember target = role.Value.Members[0];
                string pointer = at + "/refs/" + JsonSchemaSubset.EscapeToken(role.Name) + "/" + JsonSchemaSubset.EscapeToken(target.Name);
                var referrer = new Referrer(rr, "refs." + role.Name, what, pointer);
                switch (target.Name)
                {
                    case "bus":
                        if (OverlayBusId(target.Value) is int bus) Add(ix._overlayBus, bus, referrer);
                        break;
                    case "line" or "transformer" or "generator" or "demand":
                        if (target.Value is { Kind: JsonSourceKind.String, StringValue: string name })
                            Add(ix._overlayNamed[OverlayKindSet.ParseTarget(target.Name)], name, referrer);
                        break;
                    case "record":
                        if (target.Value is { Kind: JsonSourceKind.String, StringValue: string other }) Add(ix._overlayRecord, other, referrer);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Phase 5b: an overlay bus reference's value as a model bus id: a whole number in the int range (the schema's
    /// integer admits a written 12.0, which is bus 12, as the overlay checker reads it); null for anything else.
    /// </summary>
    internal static int? OverlayBusId(JsonSourceNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != JsonSourceKind.Number) return null;
        if (value.IntegerValue is long l) return l >= int.MinValue && l <= int.MaxValue ? (int)l : null;
        return value.NumberValue is double x && double.IsFinite(x) && Math.Floor(x) == x && x >= int.MinValue && x <= int.MaxValue ? (int)x : null;
    }

    private static void Each(CaseSession s, RecordCollection c, Action<RecordRef, JsonSourceNode, StringComparer> visit)
    {
        JsonSourceNode? root = s.ArrayOf(c);
        if (root is null) return;
        StringComparer cmp = s.Documents[c.Document].KeyComparer;
        for (int i = 0; i < root.Elements.Count; i++)
            if (root.Elements[i].Kind == JsonSourceKind.Object) visit(new RecordRef(c.Document, c.ArrayPointer, i), root.Elements[i], cmp);
    }

    private static void Add<TKey>(Dictionary<TKey, List<Referrer>> map, TKey key, Referrer r) where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<Referrer>? l)) map[key] = l = new List<Referrer>();
        l.Add(r);
    }
}
