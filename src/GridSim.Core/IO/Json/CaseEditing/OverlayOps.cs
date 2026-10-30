using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.CaseEditing;

// =====================================================================================================================
// Phase 5b (2026-10-20): editing the asset overlay (gridsim-assets/2) of a v2 case. The overlay is CARRIED BESIDE THE
// MODEL and never used by the solver (Phase 5a: V2CaseLoader reads it only after the model is built), so nothing here
// can change a model or a result: every operation patches an overlay file's bytes only. The case editor's rules hold
// unchanged: each operation parses typed text strictly, refuses anything that would ADD a problem (a schema error, a
// parameter the kind spec refuses, a dangling or wrongly typed reference, a parent of the wrong kind or a loop), and
// applies its byte patches as ONE transaction (one undo step). An edit replaces exactly the value token it changes, so
// a saved overlay differs from the file on disk only in that token (lossless, like every other case file).
//
// Overlay records are read against the overlay kind specs (schemas/v2/kinds/overlay, OverlayKindSet), not against a
// loader DTO: the kind spec -> FieldSpec adapter below (ParamField, RefField, ParentField) turns each declared parameter
// (type, unit, range, enum values, required) and each declared reference role (target type, required) into the same
// FieldSpec the property grid shows for a model record, so one grid edits both.
// =====================================================================================================================

/// <summary>Which part of an overlay record a field of the editor's grid is.</summary>
internal enum OverlayFieldPart
{
    /// <summary>The record id (the key: renamed with its references, or set when nothing refers to it).</summary>
    Id,

    /// <summary>The overlay kind (fixed: its params and refs follow it).</summary>
    Kind,

    /// <summary>The parent record's id.</summary>
    Parent,

    /// <summary>One reference role (refs.role).</summary>
    Ref,

    /// <summary>One parameter (params.name).</summary>
    Param,

    /// <summary>The record's note (plain text).</summary>
    Note,

    /// <summary>The record's provenance object (JSON).</summary>
    Prov,
}

/// <summary>
/// One field of an overlay record as the editor shows it: the field key ("id", "parent", "refs.bus", "params.device"),
/// its spec (from the kind spec through the adapter, or from the record schema), what is written, and the choices a
/// picker offers.
/// </summary>
/// <param name="Field">The field key the grid and the edit operations use.</param>
/// <param name="Spec">The field spec (label and unit in its title, help, kind, required, enum values, range).</param>
/// <param name="Part">Which part of the record it is.</param>
/// <param name="Literal">The value exactly as written ("" when absent): for a reference, its target's value token; for
/// a parameter in the object form {v, unit, prov}, the v token.</param>
/// <param name="Present">True when the member is written.</param>
/// <param name="LockReason">Why the field cannot be edited here, or null.</param>
/// <param name="Note">A note on how it is written (for example the object form's unit and provenance being kept), or null.</param>
/// <param name="Choices">What a picker offers (the case's elements of a reference's target type, the allowed parents, an
/// enum's values); empty when the field is typed freely.</param>
/// <param name="Declared">False for a member the kind spec does not declare (shown, not editable).</param>
internal sealed record OverlayField(string Field, FieldSpec Spec, OverlayFieldPart Part, string Literal, bool Present,
    string? LockReason, string? Note, IReadOnlyList<string> Choices, bool Declared);

/// <summary>
/// Phase 5b: the editing operations for asset overlay records, at file level, each one transaction (see the file
/// comment). The record id, note and provenance are edited with the ordinary <see cref="CaseOps"/> operations (the
/// overlay is a collection of role <see cref="CaseRoles.Overlay"/> keyed by "id"), as are Duplicate, Delete and Rename
/// with their overlay rules; this class adds what the kind spec decides: parameters, references and the parent, and
/// adding a record of a kind.
/// </summary>
internal static class OverlayOps
{
    /// <summary>The field-key prefix of a parameter ("params.ratedCurrentA").</summary>
    public const string ParamPrefix = "params.";

    /// <summary>The field-key prefix of a reference role ("refs.bus").</summary>
    public const string RefPrefix = "refs.";

    /// <summary>The overlay kind specs the editor reads (the embedded schemas/v2/kinds/overlay).</summary>
    public static OverlayKindSet Kinds => BundledSchemas.OverlayKinds();

    // =================================================================================================================
    // The kind spec -> FieldSpec adapter
    // =================================================================================================================

    /// <summary>A parameter of a kind as a field spec: its type, unit (in the title, as schema titles carry it), range,
    /// enum values and requiredness exactly as the kind spec declares them.</summary>
    public static FieldSpec ParamField(KindParamSpec p)
    {
        ArgumentNullException.ThrowIfNull(p);
        FieldKind kind = p.Type switch
        {
            KindParamType.Number => FieldKind.Number,
            KindParamType.Integer => FieldKind.Integer,
            KindParamType.Enum => FieldKind.Enum,
            KindParamType.Boolean => FieldKind.Boolean,
            _ => FieldKind.String,
        };
        IReadOnlyList<JsonElement> values = p.Type == KindParamType.Enum ? p.Values.Select(StringElement).ToList() : Array.Empty<JsonElement>();
        string title = p.Unit is null ? p.Name : p.Name + " (" + p.Unit + ")";
        return new FieldSpec(ParamPrefix + p.Name, title, p.Description, kind, p.Required, values, p.Min, p.Max, null, null, null,
            IsBusReference: false, DefaultLiteral: null, AbsentText: p.Required ? "required" : "none (the kind spec gives no default)");
    }

    /// <summary>A reference role of a kind as a field spec: a bus is typed as its id (an integer), every other target by
    /// its name or record id (text). The title carries the target type where a schema title carries a unit.</summary>
    public static FieldSpec RefField(OverlayRefSpec r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string target = OverlayKindSet.TargetWord(r.Target) + (r.RecordKinds.Count > 0 ? ": " + string.Join(" or ", r.RecordKinds) : "");
        return new FieldSpec(RefPrefix + r.Role, r.Role + " (" + target + ")", r.Description,
            r.Target == AssetRefTarget.Bus ? FieldKind.Integer : FieldKind.String, r.Required, Array.Empty<JsonElement>(),
            null, null, null, null, null, IsBusReference: false, DefaultLiteral: null, AbsentText: r.Required ? "required" : "none (not given)");
    }

    /// <summary>The parent of a kind as a field spec (a record id of one of the kind's parentKinds).</summary>
    public static FieldSpec ParentField(OverlayKindSpec k)
    {
        ArgumentNullException.ThrowIfNull(k);
        string kinds = k.ParentKinds.Count == 0 ? "none: top level" : "record: " + string.Join(" or ", k.ParentKinds);
        return new FieldSpec("parent", "parent (" + kinds + ")", "The id of the record this one sits under.", FieldKind.String,
            k.ParentRequired, Array.Empty<JsonElement>(), null, null, null, null, null, IsBusReference: false, DefaultLiteral: null,
            AbsentText: k.ParentRequired ? "required" : "none (top level)");
    }

    private static JsonElement StringElement(string v)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonLiteral.FormatString(v));
        return doc.RootElement.Clone();
    }

    // =================================================================================================================
    // Reading a record
    // =================================================================================================================

    /// <summary>True when <paramref name="r"/> is a record of an asset overlay file of the session.</summary>
    public static bool IsOverlayRecord(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        return r.Index >= 0 && s.CollectionAt(r.Document, r.ArrayPointer)?.Role == CaseRoles.Overlay;
    }

    // The record node, its document and its kind spec, or the refusal text.
    private static string? Open(CaseSession s, RecordRef r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec)
    {
        d = s.Documents[r.Document];
        rec = null!;
        id = kind = "";
        spec = null;
        if (!IsOverlayRecord(s, r)) return $"{d.FileName} {r.Pointer} is not an asset overlay record.";
        if (!d.Exists || d.Text.Resolve(r.Pointer) is not { Kind: JsonSourceKind.Object } node) return $"{d.FileName}: there is no overlay record at {r.Pointer}.";
        rec = node;
        id = RecordRead.Str(rec, "id", d.KeyComparer) ?? "#" + r.Index.ToString(CultureInfo.InvariantCulture);
        kind = RecordRead.Str(rec, "kind", d.KeyComparer) ?? "";
        spec = Kinds.Find(kind);
        return null;
    }

    /// <summary>
    /// The fields of an overlay record, in the order the grid shows them: id, kind, parent, each reference role the kind
    /// declares (in the kind file's order), each parameter it declares (likewise), any role or parameter written that the
    /// kind does not declare (shown, not editable), then note and prov. Pickers offer the case's elements of each
    /// reference's target type and the records a parent may be.
    /// </summary>
    public static IReadOnlyList<OverlayField> Fields(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec) is string why)
            throw new ArgumentException(why, nameof(r));
        RecordShape shape = s.ShapeAt(d, r.Pointer) ?? throw new InvalidDataException($"{d.FileName}: no schema describes the overlay record at {r.Pointer}.");
        ReferenceIndex ix = ReferenceIndex.Build(s);
        KindSpec? parameters = Kinds.Parameters.Find(kind);
        StringComparer cmp = d.KeyComparer;
        string Raw(JsonSourceNode n) => d.Text.RawText(n);
        var list = new List<OverlayField>();

        JsonSourceNode? idNode = RecordRead.Member(rec, "id", cmp);
        list.Add(new OverlayField("id", shape.Field("id")!, OverlayFieldPart.Id, idNode is null ? "" : Raw(idNode), idNode is not null, null,
            "the key: Rename changes it and every parent and record reference to it in one step", Array.Empty<string>(), true));

        JsonSourceNode? kindNode = RecordRead.Member(rec, "kind", cmp);
        list.Add(new OverlayField("kind", shape.Field("kind")!, OverlayFieldPart.Kind, kindNode is null ? "" : Raw(kindNode), kindNode is not null,
            spec is null
                ? $"'{kind}' is not an asset overlay kind, so its params and refs cannot be read; delete the record or fix the file."
                : "the kind of an overlay record is fixed (its params and refs follow it); to change it, add a record of the other kind and delete this one.",
            null, Array.Empty<string>(), true));

        JsonSourceNode? parentNode = RecordRead.Member(rec, "parent", cmp);
        if (spec is not null)
        {
            string? parentLock = spec.ParentKinds.Count == 0 ? $"a '{kind}' record sits at the top level and takes no parent." : null;
            list.Add(new OverlayField("parent", ParentField(spec), OverlayFieldPart.Parent, parentNode is null ? "" : Raw(parentNode), parentNode is not null,
                parentLock, null, parentLock is null ? ParentChoices(s, ix, spec, id) : Array.Empty<string>(), true));
        }
        else if (parentNode is not null)
            list.Add(new OverlayField("parent", shape.Field("parent")!, OverlayFieldPart.Parent, Raw(parentNode), true, "the kind is unknown.", null, Array.Empty<string>(), false));

        // References: declared roles first, in the kind's order, then any role written that the kind does not declare.
        JsonSourceNode? refs = RecordRead.Member(rec, "refs", cmp);
        var shownRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (OverlayRefSpec role in spec?.Refs.Values ?? Enumerable.Empty<OverlayRefSpec>())
        {
            shownRoles.Add(role.Role);
            JsonSourceNode? v = refs is { Kind: JsonSourceKind.Object } ? RecordRead.Member(refs, role.Role, cmp) : null;
            (string literal, string? note) = RefLiteral(d, v, role.Target);
            list.Add(new OverlayField(RefPrefix + role.Role, RefField(role), OverlayFieldPart.Ref, literal, v is not null, null, note,
                TargetChoices(s, ix, role, id), true));
        }
        if (refs is { Kind: JsonSourceKind.Object })
            foreach (JsonSourceMember m in refs.Members.Where(m => !shownRoles.Contains(m.Name)))
                list.Add(Undeclared(RefPrefix + m.Name, m.Name, Raw(m.Value), OverlayFieldPart.Ref,
                    spec is null ? "the kind is unknown." : $"kind '{kind}' declares no role '{m.Name}'; reset it to remove it."));

        // Parameters: declared first, in the kind's order, then any written that the kind does not declare.
        JsonSourceNode? ps = RecordRead.Member(rec, "params", cmp);
        var shownParams = new HashSet<string>(StringComparer.Ordinal);
        foreach (KindParamSpec p in parameters?.Params.Values ?? Enumerable.Empty<KindParamSpec>())
        {
            shownParams.Add(p.Name);
            JsonSourceNode? v = ps is { Kind: JsonSourceKind.Object } ? RecordRead.Member(ps, p.Name, cmp) : null;
            (string literal, string? note) = ParamLiteral(d, v);
            FieldSpec f = ParamField(p);
            list.Add(new OverlayField(ParamPrefix + p.Name, f, OverlayFieldPart.Param, literal, v is not null, null, note, f.EnumTexts, true));
        }
        if (ps is { Kind: JsonSourceKind.Object })
            foreach (JsonSourceMember m in ps.Members.Where(m => !shownParams.Contains(m.Name)))
                list.Add(Undeclared(ParamPrefix + m.Name, m.Name, Raw(m.Value), OverlayFieldPart.Param,
                    parameters is null ? "the kind is unknown." : $"kind '{kind}' declares no parameter '{m.Name}'; reset it to remove it."));

        JsonSourceNode? noteNode = RecordRead.Member(rec, "note", cmp);
        list.Add(new OverlayField("note", shape.Field("note")!, OverlayFieldPart.Note, noteNode is null ? "" : Raw(noteNode), noteNode is not null, null, null, Array.Empty<string>(), true));
        JsonSourceNode? provNode = RecordRead.Member(rec, "prov", cmp);
        list.Add(new OverlayField("prov", shape.Field("prov")!, OverlayFieldPart.Prov, provNode is null ? "" : Raw(provNode), provNode is not null, null,
            "the record's provenance, typed as a JSON object", Array.Empty<string>(), true));
        return list;
    }

    private static OverlayField Undeclared(string field, string name, string literal, OverlayFieldPart part, string reason)
        => new(field, new FieldSpec(field, name, "Not declared by the record's kind.", FieldKind.Raw, false, Array.Empty<JsonElement>(), null, null, null, null, null,
            false, null, "none"), part, literal, true, reason, null, Array.Empty<string>(), false);

    // A reference's literal: the target's value token, with a note when it is not written in the role's target type.
    private static (string Literal, string? Note) RefLiteral(CaseDocument d, JsonSourceNode? v, AssetRefTarget target)
    {
        if (v is null) return ("", null);
        if (v.Kind == JsonSourceKind.Object && v.Members.Count == 1)
        {
            JsonSourceMember t = v.Members[0];
            string word = OverlayKindSet.TargetWord(target);
            return (d.Text.RawText(t.Value), t.Name == word ? null : $"written as a {t.Name} reference, but the role points at a {word}; setting it rewrites it");
        }
        return (d.Text.RawText(v), "not a one-member reference object; setting it rewrites it");
    }

    // A parameter's literal: a bare value as written, or the v of the object form (its unit and provenance are kept).
    private static (string Literal, string? Note) ParamLiteral(CaseDocument d, JsonSourceNode? v)
    {
        if (v is null) return ("", null);
        if (v.Kind != JsonSourceKind.Object) return (d.Text.RawText(v), null);
        JsonSourceNode? inner = RecordRead.Member(v, "v", d.KeyComparer);
        var kept = new List<string>();
        if (RecordRead.Str(v, "unit", d.KeyComparer) is string unit) kept.Add("unit '" + unit + "'");
        if (RecordRead.Member(v, "prov", d.KeyComparer) is not null) kept.Add("provenance");
        string note = "object form" + (kept.Count > 0 ? ": " + string.Join(" and ", kept) + " kept when the value is set" : "");
        return (inner is null ? d.Text.RawText(v) : d.Text.RawText(inner), note);
    }

    // =================================================================================================================
    // Pickers: what a reference or a parent may point at
    // =================================================================================================================

    /// <summary>The names the case's records of <paramref name="role"/> carry, each with how many records carry it, in
    /// file order (the overlay resolves line, transformer, generator and demand references by these names).</summary>
    public static IReadOnlyList<(string Name, int Count)> NamesOf(CaseSession s, string role)
    {
        ArgumentNullException.ThrowIfNull(s);
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RecordCollection c in s.CollectionsOf(role))
        {
            if (s.ArrayOf(c) is not JsonSourceNode array) continue;
            StringComparer cmp = s.Documents[c.Document].KeyComparer;
            foreach (JsonSourceNode rec in array.Elements)
                if (RecordRead.Str(rec, "name", cmp) is string n)
                {
                    if (!counts.ContainsKey(n)) order.Add(n);
                    counts[n] = counts.GetValueOrDefault(n) + 1;
                }
        }
        return order.Select(n => (n, counts[n])).ToList();
    }

    private static string RoleOf(AssetRefTarget t) => t switch
    {
        AssetRefTarget.Line => CaseRoles.Lines,
        AssetRefTarget.Transformer => CaseRoles.Transformers,
        AssetRefTarget.Generator => CaseRoles.Generators,
        AssetRefTarget.Demand => CaseRoles.Demands,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "only named targets have a model role."),
    };

    /// <summary>The kind of the overlay record at <paramref name="r"/>, or null.</summary>
    public static string? KindAt(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument d = s.Documents[r.Document];
        return d.Exists && d.Text.Resolve(r.Pointer) is { Kind: JsonSourceKind.Object } rec ? RecordRead.Str(rec, "kind", d.KeyComparer) : null;
    }

    /// <summary>
    /// What a reference role may point at, as the text to type: every bus id of the case, every name a line,
    /// transformer, generator or demand of the case carries ONCE (a shared name is ambiguous, so it is not offered), or
    /// every overlay record of one of the role's recordKinds except <paramref name="selfId"/>.
    /// </summary>
    public static IReadOnlyList<string> TargetChoices(CaseSession s, ReferenceIndex ix, OverlayRefSpec role, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ix);
        ArgumentNullException.ThrowIfNull(role);
        return role.Target switch
        {
            AssetRefTarget.Bus => ix.Buses.Keys.OrderBy(b => b).Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList(),
            AssetRefTarget.Record => ix.OverlayRecords
                .Where(kv => !string.Equals(kv.Key, selfId, StringComparison.Ordinal) && role.RecordKinds.Contains(KindAt(s, kv.Value) ?? "", StringComparer.Ordinal))
                .Select(kv => kv.Key).ToList(),
            _ => NamesOf(s, RoleOf(role.Target)).Where(n => n.Count == 1).Select(n => n.Name).ToList(),
        };
    }

    /// <summary>The records a record of <paramref name="kind"/> may sit under: those of its parentKinds, except the record
    /// itself and its own descendants (which would make a loop).</summary>
    public static IReadOnlyList<string> ParentChoices(CaseSession s, ReferenceIndex ix, OverlayKindSpec kind, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ix);
        ArgumentNullException.ThrowIfNull(kind);
        return ix.OverlayRecords
            .Where(kv => kind.ParentKinds.Contains(KindAt(s, kv.Value) ?? "", StringComparer.Ordinal)
                         && (selfId is null || !ChainReaches(s, ix, kv.Key, selfId)))
            .Select(kv => kv.Key).ToList();
    }

    // True when walking up the parent chain from "from" (itself included) reaches "target". A loop already in the file
    // stops the walk (each id is visited once).
    private static bool ChainReaches(CaseSession s, ReferenceIndex ix, string from, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cur = from;
        while (cur is not null && seen.Add(cur))
        {
            if (string.Equals(cur, target, StringComparison.Ordinal)) return true;
            if (!ix.OverlayRecords.TryGetValue(cur, out RecordRef at)) return false;
            CaseDocument d = s.Documents[at.Document];
            cur = d.Text.Resolve(at.Pointer) is { Kind: JsonSourceKind.Object } rec ? RecordRead.Str(rec, "parent", d.KeyComparer) : null;
        }
        return false;
    }

    // =================================================================================================================
    // Typed input for references
    // =================================================================================================================

    /// <summary>
    /// Turn typed text into a reference's target value and check that it resolves in the case as it is now: a bus id of
    /// the case; a name exactly one line (transformer, generator, demand) of the case carries; or the id of another
    /// overlay record of one of the role's recordKinds. Returns false with the reason otherwise.
    /// </summary>
    public static bool TryTarget(CaseSession s, ReferenceIndex ix, OverlayRefSpec role, string typed, string? selfId, out JsonFresh value, out string why)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ix);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(typed);
        value = JsonFresh.Scalar("null");
        string word = OverlayKindSet.TargetWord(role.Target);
        switch (role.Target)
        {
            case AssetRefTarget.Bus:
                if (!JsonLiteral.TryParseInt32(typed, out int bus, out why)) return false;
                if (!ix.Buses.ContainsKey(bus)) { why = $"bus {bus.ToString(CultureInfo.InvariantCulture)} is not a bus of the case."; return false; }
                value = JsonFresh.Scalar(JsonLiteral.FormatInteger(bus));
                return true;
            case AssetRefTarget.Record:
                if (string.Equals(typed, selfId, StringComparison.Ordinal)) { why = "a record cannot point at itself."; return false; }
                if (!ix.OverlayRecords.TryGetValue(typed, out RecordRef hit)) { why = $"'{typed}' is not a record of the overlay."; return false; }
                string kind = KindAt(s, hit) ?? "";
                if (!role.RecordKinds.Contains(kind, StringComparer.Ordinal))
                {
                    why = $"role '{role.Role}' must point at a record of kind {string.Join(" or ", role.RecordKinds)}, but '{typed}' is a '{kind}'.";
                    return false;
                }
                value = JsonFresh.Scalar(JsonLiteral.FormatString(typed));
                why = "";
                return true;
            default:
                if (JsonLiteral.HasUnpairedSurrogate(typed)) { why = "the text holds an unpaired surrogate, which cannot be written as UTF-8."; return false; }
                int n = NamesOf(s, RoleOf(role.Target)).FirstOrDefault(x => string.Equals(x.Name, typed, StringComparison.Ordinal)).Count;
                if (n == 0) { why = $"no {word} of the case is named '{typed}' (names must match exactly)."; return false; }
                if (n > 1) { why = $"'{typed}' is ambiguous: {n.ToString(CultureInfo.InvariantCulture)} {word}s of the case carry that name."; return false; }
                value = JsonFresh.Scalar(JsonLiteral.FormatString(typed));
                why = "";
                return true;
        }
    }

    // =================================================================================================================
    // Parameters
    // =================================================================================================================

    /// <summary>
    /// Set a parameter of an overlay record from typed text, parsed as the kind spec types it. A bare value has its
    /// token replaced; a value in the object form {v, unit, prov} has only its v replaced (unit and provenance kept); an
    /// absent one is inserted in the kind spec's order. Refused when the text does not parse, the kind spec refuses the
    /// value (type, enum, range) or the record would not be valid.
    /// </summary>
    public static OpResult SetParam(CaseSession s, RecordRef r, string param, string typed)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(typed);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out _) is string why) return OpResult.Refused(why);
        KindSpec? spec = Kinds.Parameters.Find(kind);
        if (spec is null) return OpResult.Refused($"record '{id}': '{kind}' is not an asset overlay kind, so its parameters cannot be set.");
        if (!spec.Params.TryGetValue(param, out KindParamSpec? p))
            return OpResult.Refused($"record '{id}': kind '{kind}' has no parameter '{param}' (declared: {string.Join(", ", spec.Params.Keys)}).");
        FieldSpec f = ParamField(p);
        if (!CaseOps.TryValue(f, typed, out JsonFresh value, out string bad)) return OpResult.Refused($"{ParamPrefix}{param}: {bad}");
        JsonSourceText text = d.Text;
        if (RecordRead.Member(rec, "params", d.KeyComparer) is not { Kind: JsonSourceKind.Object } ps)
            return OpResult.Refused($"record '{id}': its params member is missing or not an object; fix the record first.");

        JsonSourceMember? existing = ps.FindMember(param, d.KeyComparer);
        IReadOnlyList<JsonTextEdit> edits;
        string old;
        if (existing is not null)
        {
            JsonSourceNode target = existing.Value;
            if (target.Kind == JsonSourceKind.Object)
            {
                // The object form: only its v changes (its unit and provenance are kept exactly as written).
                if (target.FindMember("v", d.KeyComparer) is not JsonSourceMember inner)
                    return OpResult.Refused($"{ParamPrefix}{param}: the object form has no 'v' member; fix the record first.");
                target = inner.Value;
            }
            if (CaseOps.SameValue(text, target, value)) return OpResult.NoChange($"{ParamPrefix}{param} unchanged ({text.RawText(target)}).");
            old = text.RawText(target);
            edits = JsonTextOps.ReplaceValue(text, target, value.Literal!);
        }
        else
        {
            old = "(absent)";
            edits = JsonTextOps.InsertMember(text, ps, InsertAt(ps, spec.Params.Keys.ToList(), param, d.KeyComparer), param, value);
        }
        return Commit(s, d, r, id, edits, text, $"{ParamPrefix}{param}", old, value.Literal!, checkParams: kind);
    }

    /// <summary>Reset a parameter to absent (its member removed). Refused for a parameter the kind requires.</summary>
    public static OpResult ResetParam(CaseSession s, RecordRef r, string param)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(param);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out _) is string why) return OpResult.Refused(why);
        KindSpec? spec = Kinds.Parameters.Find(kind);
        if (spec is not null && spec.Params.TryGetValue(param, out KindParamSpec? p) && p.Required)
            return OpResult.Refused($"{ParamPrefix}{param} is required by kind '{kind}' and cannot be reset to absent.");
        if (RecordRead.Member(rec, "params", d.KeyComparer) is not { Kind: JsonSourceKind.Object } ps)
            return OpResult.Refused($"record '{id}': its params member is missing or not an object; fix the record first.");
        int at = ps.IndexOfMember(param, d.KeyComparer);
        if (at < 0) return OpResult.NoChange($"{ParamPrefix}{param} is already absent.");
        JsonSourceText text = d.Text;
        return Commit(s, d, r, id, JsonTextOps.RemoveMember(text, ps, at), text, $"{ParamPrefix}{param}", text.RawText(ps.Members[at].Value), "absent",
            checkParams: spec is null ? null : kind);
    }

    // =================================================================================================================
    // References
    // =================================================================================================================

    /// <summary>
    /// Set a reference role of an overlay record to the target typed (a bus id, a name, a record id; see
    /// <see cref="TryTarget"/>). A role written as {"target": value} has only its value token replaced; an absent role is
    /// inserted in the kind's order as a one-member object; a role written in another shape is rewritten. Refused when
    /// the kind does not declare the role or the target does not resolve.
    /// </summary>
    public static OpResult SetRef(CaseSession s, RecordRef r, string role, string typed)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(typed);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec) is string why) return OpResult.Refused(why);
        if (spec is null) return OpResult.Refused($"record '{id}': '{kind}' is not an asset overlay kind, so its references cannot be set.");
        if (!spec.Refs.TryGetValue(role, out OverlayRefSpec? rs))
            return OpResult.Refused(spec.Refs.Count == 0
                ? $"record '{id}': kind '{kind}' declares no roles, so role '{role}' cannot be given."
                : $"record '{id}': kind '{kind}' has no role '{role}' (declared: {string.Join(", ", spec.Refs.Keys)}).");
        ReferenceIndex ix = ReferenceIndex.Build(s);
        if (!TryTarget(s, ix, rs, typed, id, out JsonFresh value, out string bad)) return OpResult.Refused($"{RefPrefix}{role}: {bad}");
        JsonSourceText text = d.Text;
        if (RecordRead.Member(rec, "refs", d.KeyComparer) is not { Kind: JsonSourceKind.Object } refs)
            return OpResult.Refused($"record '{id}': its refs member is missing or not an object; fix the record first.");

        string word = OverlayKindSet.TargetWord(rs.Target);
        JsonFresh whole = JsonFresh.Object(new[] { new KeyValuePair<string, JsonFresh>(word, value) });
        JsonSourceMember? existing = refs.FindMember(role, d.KeyComparer);
        IReadOnlyList<JsonTextEdit> edits;
        string old;
        if (existing is null)
        {
            old = "(absent)";
            edits = JsonTextOps.InsertMember(text, refs, InsertAt(refs, spec.Refs.Keys.ToList(), role, d.KeyComparer), role, whole);
        }
        else if (existing.Value is { Kind: JsonSourceKind.Object, Members.Count: 1 } obj && obj.Members[0].Name == word)
        {
            JsonSourceNode target = obj.Members[0].Value;
            if (CaseOps.SameValue(text, target, value)) return OpResult.NoChange($"{RefPrefix}{role} unchanged ({text.RawText(target)}).");
            old = text.RawText(target);
            edits = JsonTextOps.ReplaceValue(text, target, value.Literal!);
        }
        else
        {
            old = text.RawText(existing.Value);
            edits = JsonTextOps.ReplaceValue(text, existing.Value, JsonLayout.Format(whole, JsonLayout.Detect(text, refs), JsonLayout.LineIndentOf(text, existing.Start), text.Newline));
        }
        return Commit(s, d, r, id, edits, text, $"{RefPrefix}{role}", old, value.Literal!, checkParams: null);
    }

    /// <summary>Reset a reference role to absent. Refused for a role the kind requires.</summary>
    public static OpResult ResetRef(CaseSession s, RecordRef r, string role)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(role);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec) is string why) return OpResult.Refused(why);
        if (spec is not null && spec.Refs.TryGetValue(role, out OverlayRefSpec? rs) && rs.Required)
            return OpResult.Refused($"{RefPrefix}{role} is required by kind '{kind}' (a {OverlayKindSet.TargetWord(rs.Target)}) and cannot be reset to absent.");
        if (RecordRead.Member(rec, "refs", d.KeyComparer) is not { Kind: JsonSourceKind.Object } refs)
            return OpResult.Refused($"record '{id}': its refs member is missing or not an object; fix the record first.");
        int at = refs.IndexOfMember(role, d.KeyComparer);
        if (at < 0) return OpResult.NoChange($"{RefPrefix}{role} is already absent.");
        JsonSourceText text = d.Text;
        return Commit(s, d, r, id, JsonTextOps.RemoveMember(text, refs, at), text, $"{RefPrefix}{role}", text.RawText(refs.Members[at].Value), "absent", checkParams: null);
    }

    // =================================================================================================================
    // Parent
    // =================================================================================================================

    /// <summary>
    /// Set the parent of an overlay record to the record id typed. Refused when the kind takes no parent, the id is not
    /// a record of the overlay, it is not of one of the kind's parentKinds, or the parent chain would loop.
    /// </summary>
    public static OpResult SetParent(CaseSession s, RecordRef r, string typed)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(typed);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec) is string why) return OpResult.Refused(why);
        if (spec is null) return OpResult.Refused($"record '{id}': '{kind}' is not an asset overlay kind, so its parent cannot be set.");
        ReferenceIndex ix = ReferenceIndex.Build(s);
        if (ParentProblem(s, ix, spec, kind, typed, id) is string bad) return OpResult.Refused("parent: " + bad);
        JsonFresh value = JsonFresh.Scalar(JsonLiteral.FormatString(typed));
        JsonSourceText text = d.Text;
        JsonSourceMember? existing = rec.FindMember("parent", d.KeyComparer);
        IReadOnlyList<JsonTextEdit> edits;
        string old;
        if (existing is not null)
        {
            if (CaseOps.SameValue(text, existing.Value, value)) return OpResult.NoChange($"parent unchanged ({text.RawText(existing.Value)}).");
            old = text.RawText(existing.Value);
            edits = JsonTextOps.ReplaceValue(text, existing.Value, value.Literal!);
        }
        else
        {
            old = "(absent)";
            RecordShape shape = s.ShapeAt(d, r.Pointer)!;
            edits = JsonTextOps.InsertMember(text, rec, CaseOps.InsertIndex(shape, rec, "parent", d.KeyComparer), "parent", value);
        }
        return Commit(s, d, r, id, edits, text, "parent", old, value.Literal!, checkParams: null);
    }

    /// <summary>Remove the parent (the record moves to the top level). Refused for a kind that requires a parent.</summary>
    public static OpResult ResetParent(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (Open(s, r, out CaseDocument d, out JsonSourceNode rec, out string id, out string kind, out OverlayKindSpec? spec) is string why) return OpResult.Refused(why);
        if (spec is { ParentRequired: true })
            return OpResult.Refused($"parent is required: a '{kind}' record must sit under a record of kind {string.Join(" or ", spec.ParentKinds)}.");
        int at = rec.IndexOfMember("parent", d.KeyComparer);
        if (at < 0) return OpResult.NoChange("parent is already absent (top level).");
        JsonSourceText text = d.Text;
        return Commit(s, d, r, id, JsonTextOps.RemoveMember(text, rec, at), text, "parent", text.RawText(rec.Members[at].Value), "absent", checkParams: null);
    }

    // Why a record of a kind may not sit under "parent" (null when it may). selfId is null for a record being added.
    private static string? ParentProblem(CaseSession s, ReferenceIndex ix, OverlayKindSpec spec, string kind, string parent, string? selfId)
    {
        if (spec.ParentKinds.Count == 0) return $"a '{kind}' record sits at the top level and takes no parent.";
        if (string.Equals(parent, selfId, StringComparison.Ordinal)) return "a record cannot sit under itself.";
        if (!ix.OverlayRecords.TryGetValue(parent, out RecordRef at)) return $"'{parent}' is not a record of the overlay.";
        string parentKind = KindAt(s, at) ?? "";
        if (!spec.ParentKinds.Contains(parentKind, StringComparer.Ordinal))
            return $"the parent of a '{kind}' record must be of kind {string.Join(" or ", spec.ParentKinds)}, but '{parent}' is a '{parentKind}'.";
        if (selfId is not null && ChainReaches(s, ix, parent, selfId))
            return $"'{parent}' sits under '{selfId}' already, so the parent chain would loop.";
        return null;
    }

    // =================================================================================================================
    // Add
    // =================================================================================================================

    /// <summary>The overlay kinds a record may be added under a record of <paramref name="parentKind"/> (its parentKinds
    /// name it), sorted by name.</summary>
    public static IReadOnlyList<OverlayKindSpec> KindsUnder(string parentKind)
    {
        ArgumentNullException.ThrowIfNull(parentKind);
        return Kinds.Kinds.Where(k => k.ParentKinds.Contains(parentKind, StringComparer.Ordinal)).ToList();
    }

    /// <summary>The overlay kinds that may sit at the top level (no parent required), sorted by name.</summary>
    public static IReadOnlyList<OverlayKindSpec> TopLevelKinds() => Kinds.Kinds.Where(k => !k.ParentRequired).ToList();

    /// <summary>The overlay kinds with a role pointing at <paramref name="target"/> (a record of the kind can be attached
    /// to such an element), with that role (the first, in the kind's order), sorted by kind.</summary>
    public static IReadOnlyList<(OverlayKindSpec Kind, OverlayRefSpec Role)> KindsAttachableTo(AssetRefTarget target)
        => Kinds.Kinds.Select(k => (k, k.Refs.Values.FirstOrDefault(r => r.Target == target)))
            .Where(x => x.Item2 is not null).Select(x => (x.k, x.Item2!)).ToList();

    /// <summary>
    /// Add an overlay record of <paramref name="kind"/> with id <paramref name="id"/> at the end of the records of the
    /// overlay file <paramref name="doc"/>: written fresh in its neighbours' style, members in schema order (id, kind,
    /// parent, refs, params, note), references in the kind's role order, parameters in the kind's order. Refused (with
    /// nothing changed) when the file is not an existing overlay file of the case, the kind is not an overlay kind, the
    /// id is not a valid id or is already used, the parent breaks the kind's rules, a role is not declared or does not
    /// resolve, a required role or parameter is missing, a value does not parse, or the kind spec refuses a value.
    /// </summary>
    public static OpResult AddRecord(CaseSession s, int doc, string kind, string id, string? parent,
        IReadOnlyList<KeyValuePair<string, string>> refs, IReadOnlyList<KeyValuePair<string, string>> parameters, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(parameters);
        if (doc < 0 || doc >= s.Documents.Count || s.Documents[doc].SchemaFile != CaseSession.V2AssetsSchema)
            return OpResult.Refused("an overlay record is added to an asset overlay file of the case (one the manifest lists in \"assets\").");
        CaseDocument d = s.Documents[doc];
        if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist (the manifest lists it, but it is not on disk); an overlay record cannot be added to it.");
        OverlayKindSpec? spec = Kinds.Find(kind);
        KindSpec? pspec = Kinds.Parameters.Find(kind);
        if (spec is null || pspec is null)
            return OpResult.Refused($"'{kind}' is not an asset overlay kind (known: {string.Join(", ", Kinds.Kinds.Select(k => k.Kind))}).");
        RecordCollection coll = s.CollectionAt(doc, CaseSession.OverlayRecordsPointer)
            ?? throw new InvalidOperationException($"{d.FileName}: an overlay file of the session has no records collection.");
        ReferenceIndex ix = ReferenceIndex.Build(s);

        // The id: a valid id (the schema's pattern is checked with the whole record below), not used yet.
        if (id.Length == 0) return OpResult.Refused("A new overlay record needs an id.");
        if (ix.OverlayRecords.TryGetValue(id, out RecordRef used))
            return OpResult.Refused($"id: overlay record id '{id}' is already used by {s.Documents[used.Document].FileName} {used.Pointer}; an id must be unique across every overlay file of the case.");

        // The parent, by the kind's rules.
        if (parent is not null && parent.Length == 0) parent = null;
        if (parent is null && spec.ParentRequired)
            return OpResult.Refused($"parent: a '{kind}' record must have a parent (of kind {string.Join(" or ", spec.ParentKinds)}).");
        if (parent is not null && ParentProblem(s, ix, spec, kind, parent, selfId: null) is string pbad) return OpResult.Refused("parent: " + pbad);

        // The references: each declared, given once, resolving; every required role given.
        var refValues = new Dictionary<string, JsonFresh>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in refs)
        {
            if (!spec.Refs.TryGetValue(kv.Key, out OverlayRefSpec? rs))
                return OpResult.Refused(spec.Refs.Count == 0
                    ? $"kind '{kind}' declares no roles, so role '{kv.Key}' cannot be given."
                    : $"kind '{kind}' has no role '{kv.Key}' (declared: {string.Join(", ", spec.Refs.Keys)}).");
            if (refValues.ContainsKey(kv.Key)) return OpResult.Refused($"{RefPrefix}{kv.Key} is given twice.");
            if (kv.Value.Length == 0 && !rs.Required) continue;   // an optional role left empty is not given
            if (!TryTarget(s, ix, rs, kv.Value, id, out JsonFresh v, out string why)) return OpResult.Refused($"{RefPrefix}{kv.Key}: {why}");
            refValues[kv.Key] = JsonFresh.Object(new[] { new KeyValuePair<string, JsonFresh>(OverlayKindSet.TargetWord(rs.Target), v) });
        }
        var missingRefs = spec.Refs.Values.Where(r => r.Required && !refValues.ContainsKey(r.Role)).Select(r => r.Role + " (a " + OverlayKindSet.TargetWord(r.Target) + ")").ToList();

        // The parameters: each declared, given once, parsed by its type; every required one given.
        var paramValues = new Dictionary<string, JsonFresh>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in parameters)
        {
            if (!pspec.Params.TryGetValue(kv.Key, out KindParamSpec? p))
                return OpResult.Refused($"kind '{kind}' has no parameter '{kv.Key}' (declared: {string.Join(", ", pspec.Params.Keys)}).");
            if (paramValues.ContainsKey(kv.Key)) return OpResult.Refused($"{ParamPrefix}{kv.Key} is given twice.");
            if (kv.Value.Length == 0 && !p.Required) continue;   // an optional parameter left empty is not given
            if (!CaseOps.TryValue(ParamField(p), kv.Value, out JsonFresh v, out string why)) return OpResult.Refused($"{ParamPrefix}{kv.Key}: {why}");
            paramValues[kv.Key] = v;
        }
        var missingParams = pspec.Params.Values.Where(p => p.Required && !paramValues.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        if (missingRefs.Count > 0 || missingParams.Count > 0)
            return OpResult.Refused($"A new '{kind}' record needs: " + string.Join(", ",
                missingRefs.Select(x => "role " + x).Concat(missingParams.Select(x => "parameter " + x))) + ".");

        // The record, in schema order; refs and params in the kind's order.
        var members = new List<KeyValuePair<string, JsonFresh>>
        {
            new("id", JsonFresh.Scalar(JsonLiteral.FormatString(id))),
            new("kind", JsonFresh.Scalar(JsonLiteral.FormatString(kind))),
        };
        if (parent is not null) members.Add(new("parent", JsonFresh.Scalar(JsonLiteral.FormatString(parent))));
        members.Add(new("refs", JsonFresh.Object(spec.Refs.Keys.Where(refValues.ContainsKey).Select(k => new KeyValuePair<string, JsonFresh>(k, refValues[k])))));
        members.Add(new("params", JsonFresh.Object(pspec.Params.Keys.Where(paramValues.ContainsKey).Select(k => new KeyValuePair<string, JsonFresh>(k, paramValues[k])))));
        if (!string.IsNullOrEmpty(note))
        {
            if (JsonLiteral.HasUnpairedSurrogate(note)) return OpResult.Refused("note: the text holds an unpaired surrogate, which cannot be written as UTF-8.");
            members.Add(new("note", JsonFresh.Scalar(JsonLiteral.FormatString(note))));
        }
        JsonFresh record = JsonFresh.Object(members);

        // The patch: appended to the records array, in the style of the last record (one record per line when empty).
        JsonSourceText text = d.Text;
        if (text.Resolve(CaseSession.OverlayRecordsPointer) is not { Kind: JsonSourceKind.Array } array)
            return OpResult.Refused($"{d.FileName}: its records member is missing or not an array; fix the file first.");
        int insertAt = array.Elements.Count;
        JsonSourceNode? neighbour = insertAt > 0 ? array.Elements[insertAt - 1] : null;
        JsonContainerStyle? style = neighbour is { Kind: JsonSourceKind.Object } ? JsonLayout.Detect(text, neighbour) : null;
        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.InsertElement(text, array, insertAt, record, style);
        (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) applied = text.Apply(edits);
        JsonSourceText after = applied.Text;
        string pointer = CaseSession.OverlayRecordsPointer + "/" + insertAt.ToString(CultureInfo.InvariantCulture);

        // The new record against the schema (the id pattern among it) and its params against the kind spec.
        List<SchemaError> problems = CaseOps.RecordErrors(s, d, after, pointer).ToList();
        JsonSourceNode added = after.Resolve(pointer)!;
        problems.AddRange(Kinds.Parameters.CheckParams(kind, after.ElementOf(RecordRead.Member(added, "params", d.KeyComparer)!), placeholder: false, pointer + "/params"));
        if (problems.Count > 0) return OpResult.Refused($"A new '{kind}' record would not be valid: {CaseOps.ShowErrors(problems)}");

        using (CaseTransaction tx = s.Begin($"{d.FileName}: add overlay record '{id}' ({kind})"))
        {
            tx.Patch(d, text, applied);   // the text parsed above is the new text (no second parse)
            tx.Commit();
        }
        return OpResult.Done($"Added {RecordRead.Describe(d, added, insertAt, coll.ArrayPointer)} (U to undo).", new RecordRef(doc, coll.ArrayPointer, insertAt));
    }

    // =================================================================================================================
    // Shared
    // =================================================================================================================

    // Where a new member goes in an object whose members follow "order": just after the nearest present predecessor in
    // that order, else first (members the order does not know are passed over).
    private static int InsertAt(JsonSourceNode obj, IReadOnlyList<string> order, string name, StringComparer cmp)
    {
        int pos = -1;
        for (int i = 0; i < order.Count; i++) if (string.Equals(order[i], name, StringComparison.Ordinal)) { pos = i; break; }
        for (int k = pos - 1; k >= 0; k--)
        {
            int at = obj.IndexOfMember(order[k], cmp);
            if (at >= 0) return at + 1;
        }
        return 0;
    }

    // Check an overlay record's edit (the schema errors it would ADD, and, for a parameter edit, the kind-spec errors it
    // would add), then apply it as one transaction. The message reads as CaseOps.SetField's.
    private static OpResult Commit(CaseSession s, CaseDocument d, RecordRef r, string id, IReadOnlyList<JsonTextEdit> edits, JsonSourceText text,
        string field, string old, string now, string? checkParams)
    {
        (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) applied = text.Apply(edits);
        JsonSourceText after = applied.Text;
        List<SchemaError> added = CaseOps.NewErrors(s, d, text, after, r.Pointer, r.Pointer);
        if (added.Count == 0 && checkParams is not null)
        {
            // The kind spec's verdict on the params, before and after: only a problem the edit ADDS refuses it.
            string at = r.Pointer + "/params";
            IReadOnlyList<SchemaError> Check(JsonSourceText t)
                => RecordRead.Member(t.Resolve(r.Pointer)!, "params", d.KeyComparer) is { Kind: JsonSourceKind.Object } ps
                    ? Kinds.Parameters.CheckParams(checkParams, t.ElementOf(ps), placeholder: false, at) : Array.Empty<SchemaError>();
            var was = new HashSet<string>(Check(text).Select(e => e.Pointer + "\n" + e.Message), StringComparer.Ordinal);
            added = Check(after).Where(e => !was.Contains(e.Pointer + "\n" + e.Message)).ToList();
        }
        if (added.Count > 0) return OpResult.Refused($"{field}: refused, the record would not be valid: {CaseOps.ShowErrors(added)}");

        using (CaseTransaction tx = s.Begin($"{d.FileName} record '{id}' {field}: {old} -> {now}"))
        {
            tx.Patch(d, text, applied);
            tx.Commit();
        }
        return OpResult.Done($"{field} {old} -> {now} (U to undo).", r);
    }
}
