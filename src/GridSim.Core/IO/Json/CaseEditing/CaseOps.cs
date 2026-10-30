using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// The outcome of an editing operation. <see cref="Ok"/> false means REFUSED: nothing changed and
/// <see cref="Message"/> says why (fail loud). <see cref="Changed"/> false with Ok true means there was nothing to do
/// (for example typing the value a field already holds). <see cref="Select"/> is the record the selection should
/// follow afterwards, when it moved or was created.
/// </summary>
internal sealed record OpResult(bool Ok, bool Changed, string Message, RecordRef? Select = null)
{
    public static OpResult Done(string message, RecordRef? select = null) => new(true, true, message, select);
    public static OpResult NoChange(string message) => new(true, false, message);
    public static OpResult Refused(string message) => new(false, false, message);
}

/// <summary>
/// The editing operations of the case editor at FILE level (the built GridModel is not the file: outputs are derived,
/// lines divided by circuits, demand aggregated per bus; SCHEMA-EDITOR-PLAN.md section 2, fact 12). Every operation:
/// parses typed input strictly, refuses anything that would introduce a schema error into the record or a dangling
/// reference, then applies byte patches in ONE transaction (one undo step, possibly spanning files). Existing problems
/// in a case are reported by Check and never block an unrelated edit: a record is refused only for errors the edit
/// would ADD.
/// </summary>
internal static class CaseOps
{
    /// <summary>How many referrers a refusal lists before "and N more" (W lists them all).</summary>
    public const int ReferrersShown = 20;

    // =================================================================================================================
    // Reading and typed input
    // =================================================================================================================

    /// <summary>The node at a pointer of a document (null when the document or the node does not exist).</summary>
    public static JsonSourceNode? NodeAt(CaseSession s, int doc, string pointer)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument d = s.Documents[doc];
        return d.Exists ? d.Text.Resolve(pointer) : null;
    }

    /// <summary>The key field of a role's records ("id" for buses, "name" for branches and asset classes), or null.</summary>
    public static string? KeyFieldOf(string? role) => role switch
    {
        CaseRoles.Buses => "id",
        CaseRoles.Lines or CaseRoles.Transformers or CaseRoles.AssetClasses => "name",
        CaseRoles.Overlay => "id",   // Phase 5b: an asset overlay record is keyed by its id (parents and record refs name it)
        _ => null,
    };

    /// <summary>The key field of the records of the collection at (document, array), or null (no key, or not a top-level collection).</summary>
    public static string? KeyField(CaseSession s, int doc, string arrayPointer)
    {
        ArgumentNullException.ThrowIfNull(s);
        return KeyFieldOf(s.CollectionAt(doc, arrayPointer)?.Role);
    }

    /// <summary>The record a pointer names: (document, array, index) for an element, (document, pointer, -1) otherwise.</summary>
    public static RecordRef RefOf(CaseDocument d, string pointer)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(pointer);
        int slash = pointer.LastIndexOf('/');
        if (slash >= 0 && int.TryParse(pointer.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            return new RecordRef(d.Id, pointer[..slash], index);
        return new RecordRef(d.Id, pointer, -1);
    }

    /// <summary>
    /// Turn typed text into the JSON value of a field. Integer: digits in the int32 range. Number: the strict JSON
    /// grammar, finite, written shortest round-trip (Q2). Boolean: y / n / true / false. String: taken verbatim (no
    /// unpaired surrogates). Enum: one of the listed values (case-insensitive when unambiguous; written canonically).
    /// Object, array, map and raw fields: a JSON text, re-written with the editor's literal writers.
    /// </summary>
    public static bool TryValue(FieldSpec f, string typed, out JsonFresh value, out string why)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(typed);
        value = JsonFresh.Scalar("null");
        switch (f.Kind)
        {
            case FieldKind.Integer:
                if (!JsonLiteral.TryParseInt32(typed, out int i, out why)) return false;
                value = JsonFresh.Scalar(JsonLiteral.FormatInteger(i));
                return true;
            case FieldKind.Number:
                if (!JsonLiteral.TryParseNumber(typed, out double d, out why)) return false;
                value = JsonFresh.Scalar(JsonLiteral.FormatNumber(d));
                return true;
            case FieldKind.Boolean:
                if (!JsonLiteral.TryParseBool(typed, out bool b, out why)) return false;
                value = JsonFresh.Scalar(JsonLiteral.FormatBool(b));
                return true;
            case FieldKind.String:
                if (JsonLiteral.HasUnpairedSurrogate(typed)) { why = "the text holds an unpaired surrogate, which cannot be written as UTF-8."; return false; }
                value = JsonFresh.Scalar(JsonLiteral.FormatString(typed));
                why = "";
                return true;
            case FieldKind.Enum:
            {
                string t = typed.Trim();
                IReadOnlyList<string> texts = f.EnumTexts;
                int exact = IndexOf(texts, t, StringComparison.Ordinal);
                if (exact < 0)
                {
                    var loose = texts.Select((x, k) => (x, k)).Where(p => string.Equals(p.x, t, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (loose.Count == 1) exact = loose[0].k;
                }
                if (exact < 0) { why = $"'{typed}' is not one of: {string.Join(", ", texts)}."; return false; }
                value = JsonFresh.Scalar(f.EnumValues[exact].GetRawText());
                why = "";
                return true;
            }
            default:
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(typed);
                    value = JsonFresh.FromElement(doc.RootElement);
                    why = "";
                    return true;
                }
                catch (JsonException ex)
                {
                    why = $"not valid JSON: {ex.Message}";
                    return false;
                }
                catch (ArgumentException ex)
                {
                    why = ex.Message;
                    return false;
                }
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string value, StringComparison cmp)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], value, cmp)) return i;
        return -1;
    }

    /// <summary>
    /// True when an existing value already equals a new one: numbers by their double bits (so typing 0, 0.0 or
    /// 0.00 over a 0 is no change), strings by decoded text, everything else by JSON deep equality.
    /// </summary>
    public static bool SameValue(JsonSourceText text, JsonSourceNode existing, JsonFresh value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(value);
        string literal = JsonLayout.Format(value, null, "", "\n");
        using JsonDocument doc = JsonDocument.Parse(literal);
        JsonElement v = doc.RootElement;
        if (existing.Kind == JsonSourceKind.Number && v.ValueKind == JsonValueKind.Number)
            return existing.NumberValue is double a && BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(v.GetDouble());
        if (existing.Kind == JsonSourceKind.String && v.ValueKind == JsonValueKind.String)
            return string.Equals(existing.StringValue, v.GetString(), StringComparison.Ordinal);
        return JsonElement.DeepEquals(text.ElementOf(existing), v);
    }

    // =================================================================================================================
    // Validation helpers
    // =================================================================================================================

    /// <summary>
    /// The pointer of the top-level record containing <paramref name="pointer"/>: an element of one of the case's
    /// collections ("/17" in a v1 array file, "/buses/3" in a v2 network file), or "" (the document root: a v1
    /// system.json, a v2 manifest's own members).
    /// </summary>
    public static string TopRecordPointer(CaseSession s, CaseDocument doc, string pointer)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(pointer);
        foreach (RecordCollection c in s.Collections.Where(c => c.Document == doc.Id))
        {
            string prefix = c.ArrayPointer + "/";
            if (!pointer.StartsWith(prefix, StringComparison.Ordinal) || pointer.Length == prefix.Length) continue;
            int next = pointer.IndexOf('/', prefix.Length);
            return next < 0 ? pointer : pointer[..next];
        }
        return "";
    }

    /// <summary>
    /// The schema errors of the record at <paramref name="recordPointer"/> in a text, against the shape the document's
    /// schema gives that position (error pointers relative to the record).
    /// </summary>
    public static IReadOnlyList<SchemaError> RecordErrors(CaseSession s, CaseDocument doc, JsonSourceText text, string recordPointer)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(text);
        JsonSourceNode? rec = text.Resolve(recordPointer);
        if (rec is null) return Array.Empty<SchemaError>();
        RecordShape shape = s.ShapeAt(doc, recordPointer) ?? s.RecordShapeOf(doc);
        SchemaValidator validator = ReferenceEquals(s.Catalogue.Schemas, BundledSchemas.V1()) ? BundledSchemas.V1Validate
            : ReferenceEquals(s.Catalogue.Schemas, BundledSchemas.V2()) ? BundledSchemas.V2Validate
            : new SchemaValidator(s.Catalogue.Schemas);
        return validator.Validate(text.ElementOf(rec), shape.Location.File, shape.Location.Pointer);
    }

    /// <summary>
    /// The errors a candidate text ADDS to a record compared with the current text (by pointer and message), so an
    /// existing problem in the record never blocks an unrelated edit.
    /// </summary>
    internal static List<SchemaError> NewErrors(CaseSession s, CaseDocument doc, JsonSourceText? before, JsonSourceText after, string beforePointer, string afterPointer)
    {
        IReadOnlyList<SchemaError> was = before is null ? Array.Empty<SchemaError>() : RecordErrors(s, doc, before, beforePointer);
        var old = new HashSet<string>(was.Select(e => e.Pointer + "\n" + e.Message), StringComparer.Ordinal);
        return RecordErrors(s, doc, after, afterPointer).Where(e => !old.Contains(e.Pointer + "\n" + e.Message)).ToList();
    }

    internal static string ShowErrors(IReadOnlyList<SchemaError> errors)
        => string.Join("; ", errors.Take(3).Select(e => e.ToString())) + (errors.Count > 3 ? $" (and {errors.Count - 3} more)" : "");

    /// <summary>The schema-order position for a new member: just after the nearest present predecessor, else first.</summary>
    public static int InsertIndex(RecordShape shape, JsonSourceNode obj, string field, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(obj);
        int pos = shape.IndexOf(field);
        for (int k = pos - 1; k >= 0; k--)
        {
            int at = obj.IndexOfMember(shape.Fields[k].Name, comparer);
            if (at >= 0) return at + 1;
        }
        return 0;
    }

    /// <summary>The style a fresh value inside <paramref name="obj"/> should take (the object's own style).</summary>
    private static JsonContainerStyle StyleOf(JsonSourceText text, JsonSourceNode obj) => JsonLayout.Detect(text, obj);

    /// <summary>
    /// Check one field value's references: a bus reference must name an existing bus, a branch reference an existing
    /// line or transformer, a class reference an existing asset class. Returns the refusal text, or null when fine.
    /// </summary>
    /// <summary>True when <paramref name="f"/> of <paramref name="shape"/> refers to a bus, a branch or an asset class:
    /// the fields <see cref="ReferenceProblem"/> checks against the index (N099).</summary>
    private static bool IsReferenceField(RecordShape shape, FieldSpec f)
        => ReferenceIndex.RefersToBus(shape, f)
           || ReferenceIndex.BranchRefFields.Any(b => b.Shape == shape.Location && b.Field == f.Name)
           || ReferenceIndex.ClassRefFields.Any(c => c.Shape == shape.Location && c.Field == f.Name);

    private static string? ReferenceProblem(CaseSession s, ReferenceIndex ix, RecordShape shape, FieldSpec f, JsonFresh value)
    {
        if (value.Literal is null) return null;
        using JsonDocument doc = JsonDocument.Parse(value.Literal);
        JsonElement v = doc.RootElement;
        if (ReferenceIndex.RefersToBus(shape, f) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int bus) && !ix.Buses.ContainsKey(bus))
            return $"bus {bus.ToString(CultureInfo.InvariantCulture)} does not exist (G goes to a bus; add the bus first).";
        if (v.ValueKind == JsonValueKind.String)
        {
            string name = v.GetString()!;
            if (ReferenceIndex.BranchRefFields.Any(b => b.Shape == shape.Location && b.Field == f.Name) && !ix.Branches.ContainsKey(name))
                return $"no line or transformer is named '{name}' (names must match exactly).";
            if (ReferenceIndex.ClassRefFields.Any(c => c.Shape == shape.Location && c.Field == f.Name) && !ix.Classes.ContainsKey(name))
                return $"no asset class is named '{name}' in asset-classes.json.";
        }
        return null;
    }

    internal static string ListReferrers(IReadOnlyList<ReferenceIndex.Referrer> refs)
        => string.Join(", ", refs.Take(ReferrersShown).Select(r => r.What + " (" + r.Field + ")"))
           + (refs.Count > ReferrersShown ? $", and {refs.Count - ReferrersShown} more (W lists all)" : "");

    /// <summary>
    /// The referrers of the key value of a record of a role (bus id, branch name, class name); empty for other roles.
    /// Phase 5b (2026-10-20): the asset overlay's references are added, listed before the model's own: a bus by id, a line or a
    /// transformer by name (each of its own type), a generator or a demand by name (they have no key, but an overlay
    /// record may name them, so they cannot be deleted from under it), and an overlay record by id (its children and the
    /// record references to it). A case without an overlay has none of these, so its lists are exactly as before.
    /// </summary>
    private static IReadOnlyList<ReferenceIndex.Referrer> KeyReferrers(string? role, StringComparer cmp, JsonSourceNode rec, ReferenceIndex ix)
    {
        switch (role)
        {
            case CaseRoles.Buses:
                return RecordRead.Int(rec, "id", cmp) is int id ? Join(ix.BusReferrers(id), ix.OverlayBusReferrers(id)) : Array.Empty<ReferenceIndex.Referrer>();
            case CaseRoles.Lines:
            case CaseRoles.Transformers:
                return RecordRead.Str(rec, "name", cmp) is string br
                    ? Join(ix.BranchReferrers(br), ix.OverlayNameReferrers(role == CaseRoles.Lines ? Overlay.AssetRefTarget.Line : Overlay.AssetRefTarget.Transformer, br))
                    : Array.Empty<ReferenceIndex.Referrer>();
            case CaseRoles.AssetClasses:
                return RecordRead.Str(rec, "name", cmp) is string cl ? ix.ClassReferrers(cl) : Array.Empty<ReferenceIndex.Referrer>();
            case CaseRoles.Generators:
            case CaseRoles.Demands:
                return RecordRead.Str(rec, "name", cmp) is string nm
                    ? ix.OverlayNameReferrers(role == CaseRoles.Generators ? Overlay.AssetRefTarget.Generator : Overlay.AssetRefTarget.Demand, nm)
                    : Array.Empty<ReferenceIndex.Referrer>();
            case CaseRoles.Overlay:
                return RecordRead.Str(rec, "id", cmp) is string oid ? ix.OverlayRecordReferrers(oid) : Array.Empty<ReferenceIndex.Referrer>();
            default:
                return Array.Empty<ReferenceIndex.Referrer>();
        }
    }

    // The overlay's referrers FIRST, then the model's: a refusal lists the first ReferrersShown, and a bus of a big case can
    // have more model referrers than that, so the overlay's (the reason a case with an overlay refuses what it did not
    // before) always show. The model's own list itself when the overlay adds none (a case without an overlay).
    private static IReadOnlyList<ReferenceIndex.Referrer> Join(IReadOnlyList<ReferenceIndex.Referrer> model, IReadOnlyList<ReferenceIndex.Referrer> overlay)
        => overlay.Count == 0 ? model : overlay.Concat(model).ToList();

    /// <summary>
    /// Everything that refers to a top-level record through its key (a bus id, a branch name, a class name): the
    /// "where used" list. Empty for records whose role has no key, a nested record, or a record that does not exist.
    /// </summary>
    public static IReadOnlyList<ReferenceIndex.Referrer> ReferrersOf(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument d = s.Documents[r.Document];
        RecordCollection? c = s.CollectionAt(r.Document, r.ArrayPointer);
        if (c is null || !d.Exists || d.Text.Resolve(r.Pointer) is not { Kind: JsonSourceKind.Object } rec)
            return Array.Empty<ReferenceIndex.Referrer>();
        return KeyReferrers(c.Role, d.KeyComparer, rec, ReferenceIndex.Build(s));
    }

    /// <summary>A collision of a NEW key value of a role with another record's key (null when none).</summary>
    private static string? KeyCollision(CaseSession s, string? role, ReferenceIndex ix, JsonFresh value, RecordRef? self)
    {
        if (value.Literal is null) return null;
        using JsonDocument d = JsonDocument.Parse(value.Literal);
        JsonElement v = d.RootElement;
        switch (role)
        {
            case CaseRoles.Buses:
                if (v.TryGetInt32(out int id) && ix.Buses.TryGetValue(id, out RecordRef other) && other != self)
                    return $"bus id {id.ToString(CultureInfo.InvariantCulture)} is already used by {s.Documents[other.Document].FileName} {other.Pointer}.";
                return null;
            case CaseRoles.Lines:
            case CaseRoles.Transformers:
                if (v.ValueKind == JsonValueKind.String && ix.Branches.TryGetValue(v.GetString()!, out List<RecordRef>? named) && named.Any(r => r != self))
                    return $"another line or transformer is already named '{v.GetString()}' (a shared name makes boundaries and tap changers ambiguous).";
                return null;
            case CaseRoles.AssetClasses:
                if (v.ValueKind == JsonValueKind.String && ix.Classes.TryGetValue(v.GetString()!, out RecordRef cls) && cls != self)
                    return $"an asset class is already named '{v.GetString()}' (class names are compared ignoring case).";
                return null;
            case CaseRoles.Overlay:
                // Phase 5b: an overlay record id is unique across every overlay file of the case (matched exactly).
                if (v.ValueKind == JsonValueKind.String && ix.OverlayRecords.TryGetValue(v.GetString()!, out RecordRef rec) && rec != self)
                    return $"overlay record id '{v.GetString()}' is already used by {s.Documents[rec.Document].FileName} {rec.Pointer}; an id must be unique across every overlay file of the case.";
                return null;
            default:
                return null;
        }
    }

    // =================================================================================================================
    // SetField / ResetField
    // =================================================================================================================

    /// <summary>
    /// Set a field of the object at <paramref name="objectPointer"/> (a record, or a nested object such as a zip
    /// block) from typed text. Same bits as before: no change and no undo step. A key field (bus id, branch name,
    /// class name) with referrers is refused here: Rename (R) changes it everywhere in one step.
    /// </summary>
    public static OpResult SetField(CaseSession s, int doc, string objectPointer, string field, string typed)
        => SetFieldIn(s, doc, objectPointer, field, typed, outer: null);

    /// <summary>One field edit of a <see cref="SetFields"/> group: the object (document and pointer), the field and the
    /// typed text, exactly as <see cref="SetField"/> takes them.</summary>
    internal sealed record FieldEdit(int Document, string ObjectPointer, string Field, string Typed);

    /// <summary>
    /// Phase 5f (2026-10-20): set several fields as ONE transaction, so the group is one undo step (the three-winding
    /// transformer's "derive star legs" helper writes six leg values, r and x of three legs, and one undo must take all
    /// six back). Each edit runs through exactly the checks of <see cref="SetField"/> (parse, key and reference rules, the
    /// overlay name guard, the record still valid), in order, each seeing the edits before it. The first refusal refuses
    /// the whole group and the open transaction is rolled back, so every document is exactly as before; an edit that
    /// changes nothing (same bits) adds nothing; when no edit changes anything there is no undo step at all.
    /// Why Core: one undo step needs one CaseTransaction around several patches, and transactions (and SetField's checks)
    /// live in the engine; a caller outside it could only make one undo step per SetField.
    /// </summary>
    public static OpResult SetFields(CaseSession s, IReadOnlyList<FieldEdit> edits, string description)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(description);
        if (edits.Count == 0) return OpResult.NoChange("no field to set.");
        var messages = new List<string>(edits.Count);
        using (CaseTransaction tx = s.Begin(description))
        {
            foreach (FieldEdit e in edits)
            {
                ArgumentNullException.ThrowIfNull(e);
                OpResult r = SetFieldIn(s, e.Document, e.ObjectPointer, e.Field, e.Typed, tx);
                // A refusal leaves the transaction uncommitted: disposing it below reverses every edit made so far.
                if (!r.Ok) return OpResult.Refused($"{description}: refused, nothing changed. {s.Documents[e.Document].FileName} {JsonSchemaSubset.Show(e.ObjectPointer)} {e.Field}: {r.Message}");
                if (r.Changed) messages.Add(r.Message);
            }
            if (!tx.HasChanges)
            {
                tx.Commit();   // records nothing (no step changed anything)
                return OpResult.NoChange($"{description}: every value is already as derived; nothing changed.");
            }
            tx.Commit();
        }
        return OpResult.Done($"{description}: {messages.Count} value(s) set as one change (U to undo).");
    }

    // SetField's body. With outer null it opens (and commits) its own transaction, exactly as SetField always did; with
    // outer given it adds its patch to that open transaction instead (SetFields), and the caller commits or rolls back.
    private static OpResult SetFieldIn(CaseSession s, int doc, string objectPointer, string field, string typed, CaseTransaction? outer)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(objectPointer);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(typed);
        CaseDocument d = s.Documents[doc];

        // system.json (an object root) may not exist yet: setting a field creates it as "{}" first.
        byte[]? create = null;
        JsonSourceText text;
        if (!d.Exists)
        {
            if (d.Spec is not { IsArray: false } || objectPointer.Length != 0) return OpResult.Refused($"{d.FileName} does not exist.");
            create = NewFileBytes(s, "{}");
            text = JsonSourceText.Parse(create, d.FileName, d.KeyComparer);
        }
        else text = d.Text;

        JsonSourceNode? obj = text.Resolve(objectPointer);
        if (obj is not { Kind: JsonSourceKind.Object }) return OpResult.Refused($"{d.FileName}: there is no record at {JsonSchemaSubset.Show(objectPointer)}.");
        RecordShape? shape = s.ShapeAt(d, objectPointer);
        if (shape is null) return OpResult.Refused($"{d.FileName}: no schema describes the object at {JsonSchemaSubset.Show(objectPointer)}.");
        FieldSpec? f = shape.Field(field);
        if (f is null) return OpResult.Refused($"{shape.Title} has no field '{field}'.");
        if (!TryValue(f, typed, out JsonFresh value, out string why)) return OpResult.Refused($"{f.Title}: {why}");

        JsonSourceMember? existing = obj.FindMember(field, d.KeyComparer);
        if (existing is not null && SameValue(text, existing.Value, value))
            return OpResult.NoChange($"{field} unchanged ({text.RawText(existing.Value)}).");

        // Integrity: a key with referrers changes only by Rename; a new key must not collide; references must resolve.
        string recordPointer = TopRecordPointer(s, d, objectPointer);
        RecordRef self = RefOf(d, recordPointer);
        RecordCollection? coll = recordPointer.Length > 0 ? s.CollectionAt(d.Id, self.ArrayPointer) : null;
        bool isKey = coll is not null && KeyFieldOf(coll.Role) == field && objectPointer == recordPointer;
        // N099 (1.95.240): the index is built only when the field is a key or a reference. It walks every record of
        // every collection (most of the time an edit took on gb-full), and a plain value (a rating, a set point) needs
        // none of it. The checks below are the same for the fields that do need it.
        if (isKey || IsReferenceField(shape, f))
        {
            ReferenceIndex ix = ReferenceIndex.Build(s);
            if (isKey)
            {
                if (KeyCollision(s, coll!.Role, ix, value, self) is string clash) return OpResult.Refused($"{f.Title}: {clash}");
                IReadOnlyList<ReferenceIndex.Referrer> refs = KeyReferrers(coll.Role, d.KeyComparer, obj, ix);
                if (refs.Count > 0)
                    return OpResult.Refused($"{f.Title}: {refs.Count} record(s) refer to this {field}; use Rename (R) to change it everywhere in one step. Referrers: {ListReferrers(refs)}");
            }
            if (ReferenceProblem(s, ix, shape, f, value) is string refProblem) return OpResult.Refused($"{f.Title}: {refProblem}");
        }
        // Phase 5b: a generator's or a demand's name is not a key (there is no Rename), but an asset overlay record may
        // name it; changing it would leave that reference dangling, so it is refused, listing the overlay referrers.
        if (OverlayNameGuard(s, coll, d, obj, objectPointer, recordPointer, field) is string guarded) return OpResult.Refused($"{f.Title}: {guarded}");

        // The patch: replace the value token, or insert the member at its schema-order position.
        IReadOnlyList<JsonTextEdit> edits;
        string newText;
        if (existing is not null)
        {
            string indent = JsonLayout.LineIndentOf(text, existing.Start);
            newText = JsonLayout.Format(value, StyleOf(text, obj), indent, text.Newline);
            edits = JsonTextOps.ReplaceValue(text, existing.Value, newText);
        }
        else
        {
            newText = JsonLayout.Format(value, StyleOf(text, obj), "", text.Newline);
            edits = JsonTextOps.InsertMember(text, obj, InsertIndex(shape, obj, field, d.KeyComparer), field, value);
        }

        (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) applied = text.Apply(edits);
        JsonSourceText after = applied.Text;
        List<SchemaError> added = NewErrors(s, d, create is null ? text : null, after, recordPointer, recordPointer);
        if (added.Count > 0) return OpResult.Refused($"{f.Title}: refused, the record would not be valid: {ShowErrors(added)}");

        string old = existing is null ? "(absent)" : text.RawText(existing.Value);
        if (outer is not null)
        {
            // Phase 5f: part of a SetFields group; the group's transaction commits (or rolls back) every edit together.
            if (create is not null)
            {
                outer.Create(d, create);
                outer.Patch(d, edits);
            }
            else outer.Patch(d, text, applied);
        }
        else
        {
            using CaseTransaction tx = s.Begin($"{d.FileName} {JsonSchemaSubset.Show(objectPointer)} {field}: {old} -> {newText}");
            if (create is not null)
            {
                tx.Create(d, create);
                tx.Patch(d, edits);
            }
            else tx.Patch(d, text, applied);   // N099: the text parsed above is the new text; no second parse
            tx.Commit();
        }

        string note = "";
        if (d.Spec?.FileName == "generation.json" && (field == "name" || field == "type"))
            note = " The unit now belongs to the station '" + (RecordRead.Str(after.Resolve(recordPointer)!, "name", d.KeyComparer) ?? StationIndex.Unnamed) + "'.";
        return OpResult.Done($"{field} {old} -> {newText} (U to undo).{note}", coll is not null ? self : null);
    }

    /// <summary>
    /// Reset a field to absent (its member removed), so it takes its default again. Refused for a required field and
    /// for a key field with referrers.
    /// </summary>
    public static OpResult ResetField(CaseSession s, int doc, string objectPointer, string field)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(objectPointer);
        ArgumentNullException.ThrowIfNull(field);
        CaseDocument d = s.Documents[doc];
        if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist.");
        JsonSourceText text = d.Text;
        JsonSourceNode? obj = text.Resolve(objectPointer);
        if (obj is not { Kind: JsonSourceKind.Object }) return OpResult.Refused($"{d.FileName}: there is no record at {JsonSchemaSubset.Show(objectPointer)}.");
        RecordShape? shape = s.ShapeAt(d, objectPointer);
        FieldSpec? f = shape?.Field(field);
        if (shape is null || f is null) return OpResult.Refused($"{d.FileName}: no field '{field}' here.");
        if (f.Required) return OpResult.Refused($"{f.Title} is required and cannot be reset to absent.");
        int at = obj.IndexOfMember(field, d.KeyComparer);
        if (at < 0) return OpResult.NoChange($"{field} is already absent ({f.AbsentText}).");

        string recordPointer = TopRecordPointer(s, d, objectPointer);
        RecordRef self = RefOf(d, recordPointer);
        RecordCollection? coll = recordPointer.Length > 0 ? s.CollectionAt(d.Id, self.ArrayPointer) : null;
        if (coll is not null && KeyFieldOf(coll.Role) == field && objectPointer == recordPointer)
        {
            IReadOnlyList<ReferenceIndex.Referrer> refs = KeyReferrers(coll.Role, d.KeyComparer, obj, ReferenceIndex.Build(s));
            if (refs.Count > 0) return OpResult.Refused($"{f.Title}: {refs.Count} record(s) refer to it; it cannot be removed. Referrers: {ListReferrers(refs)}");
        }
        if (OverlayNameGuard(s, coll, d, obj, objectPointer, recordPointer, field) is string guarded) return OpResult.Refused($"{f.Title}: {guarded}");

        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.RemoveMember(text, obj, at);
        JsonSourceText after = text.Apply(edits).Text;
        List<SchemaError> added = NewErrors(s, d, text, after, recordPointer, recordPointer);
        if (added.Count > 0) return OpResult.Refused($"{f.Title}: refused, the record would not be valid: {ShowErrors(added)}");

        string old = text.RawText(obj.Members[at].Value);
        using (CaseTransaction tx = s.Begin($"{d.FileName} {JsonSchemaSubset.Show(objectPointer)} {field}: {old} -> absent"))
        {
            tx.Patch(d, edits);
            tx.Commit();
        }
        return OpResult.Done($"{field} {old} -> absent ({f.AbsentText}) (U to undo).", coll is not null ? self : null);
    }

    /// <summary>
    /// Phase 5b: the refusal for changing or removing the "name" of a generator or a demand record that asset overlay
    /// records refer to by that name, or null. Only a case with an overlay is looked at (no index is built otherwise, so
    /// a case without one edits exactly as before).
    /// </summary>
    private static string? OverlayNameGuard(CaseSession s, RecordCollection? coll, CaseDocument d, JsonSourceNode obj, string objectPointer, string recordPointer, string field)
    {
        if (!s.HasOverlay || coll is null || coll.Role is not (CaseRoles.Generators or CaseRoles.Demands)
            || field != "name" || objectPointer != recordPointer) return null;
        IReadOnlyList<ReferenceIndex.Referrer> refs = KeyReferrers(coll.Role, d.KeyComparer, obj, ReferenceIndex.Build(s));
        if (refs.Count == 0) return null;
        string what = coll.Role == CaseRoles.Generators ? "generator" : "demand";
        return $"{refs.Count} asset overlay record(s) refer to this {what} by its name, so it cannot change (a {what} name is not a key and has no Rename); change or remove those references first. Referrers: {ListReferrers(refs)}";
    }

    /// <summary>Initial bytes of a file the editor creates: the given JSON in the case's newline and trailing-newline style.</summary>
    private static byte[] NewFileBytes(CaseSession s, string json)
    {
        // Take the style from the first existing file (buses.json for v1, the manifest for v2).
        JsonSourceText? model = s.Documents.FirstOrDefault(x => x.Exists)?.Current;
        string tail = model is { TrailingNewline: true } ? model.Newline : "";
        return Encoding.UTF8.GetBytes(json + tail);
    }

    // =================================================================================================================
    // Add / Duplicate / Delete
    // =================================================================================================================

    /// <summary>The record shape of the items of the array at <paramref name="arrayPointer"/> ("" = the file's root array).</summary>
    public static RecordShape? ItemShape(CaseSession s, CaseDocument d, string arrayPointer)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(arrayPointer);
        if (arrayPointer.Length == 0) return d.Spec is { IsArray: true } ? s.RecordShapeOf(d) : null;
        int slash = arrayPointer.LastIndexOf('/');
        RecordShape? parent = s.ShapeAt(d, arrayPointer[..slash]);
        string name = arrayPointer[(slash + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        FieldSpec? f = parent?.FieldIgnoringCase(name);
        return f is { Kind: FieldKind.Array, Nested: SchemaLocation loc } && s.Catalogue.IsRecord(loc) ? s.Catalogue.Shape(loc) : null;
    }

    /// <summary>
    /// Add a record to the array at <paramref name="arrayPointer"/> so that it becomes element
    /// <paramref name="insertAt"/>, from typed field values (canonical field names). The record is written fresh in
    /// the style of its neighbour (one record per line when the array is empty). Refused when a required member is
    /// missing, a value does not parse or validate, a key collides, or a reference does not resolve. A missing file is
    /// created (as an array holding just this record).
    /// </summary>
    public static OpResult AddRecord(CaseSession s, int doc, string arrayPointer, int insertAt, IReadOnlyList<KeyValuePair<string, string>> typedValues)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(arrayPointer);
        ArgumentNullException.ThrowIfNull(typedValues);
        CaseDocument d = s.Documents[doc];
        RecordShape? shape = ItemShape(s, d, arrayPointer);
        if (shape is null) return OpResult.Refused($"{d.FileName}: {JsonSchemaSubset.Show(arrayPointer)} is not a list of records.");

        // Parse every value; build the record in schema order.
        var values = new Dictionary<string, JsonFresh>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in typedValues)
        {
            FieldSpec? f = shape.Field(kv.Key);
            if (f is null) return OpResult.Refused($"{shape.Title} has no field '{kv.Key}'.");
            if (values.ContainsKey(f.Name)) return OpResult.Refused($"{f.Title} is given twice.");
            if (!TryValue(f, kv.Value, out JsonFresh v, out string why)) return OpResult.Refused($"{f.Title}: {why}");
            values[f.Name] = v;
        }
        var missing = shape.Fields.Where(f => f.Required && !values.ContainsKey(f.Name)).Select(f => f.Title).ToList();
        if (missing.Count > 0) return OpResult.Refused($"A new {shape.Title.ToLowerInvariant()} needs: {string.Join(", ", missing)}.");
        JsonFresh record = JsonFresh.Object(shape.Fields.Where(f => values.ContainsKey(f.Name)).Select(f => new KeyValuePair<string, JsonFresh>(f.Name, values[f.Name])));

        ReferenceIndex ix = ReferenceIndex.Build(s);
        RecordCollection? coll = s.CollectionAt(doc, arrayPointer);
        if (KeyFieldOf(coll?.Role) is string key && values.TryGetValue(key, out JsonFresh? keyValue)
            && KeyCollision(s, coll!.Role, ix, keyValue, null) is string clash)
            return OpResult.Refused($"{shape.Field(key)!.Title}: {clash}");
        foreach (FieldSpec f in shape.Fields.Where(f => values.ContainsKey(f.Name)))
            if (ReferenceProblem(s, ix, shape, f, values[f.Name]) is string problem) return OpResult.Refused($"{f.Title}: {problem}");

        // The text to patch: the file (created when missing: a v1 array file as an empty array, a listed v2 network
        // file as an empty network), and the array inside it.
        byte[]? create = null;
        JsonSourceText text;
        if (!d.Exists)
        {
            if (coll is null) return OpResult.Refused($"{d.FileName} does not exist.");
            create = NewFileBytes(s, d.Spec is not null ? "[]" : "{ \"schema\": \"gridsim-network/2\" }");
            text = JsonSourceText.Parse(create, d.FileName, d.KeyComparer);
        }
        else text = d.Text;

        IReadOnlyList<JsonTextEdit> edits;
        string newPointer;
        JsonSourceNode? array = text.Resolve(arrayPointer);
        if (array is null && arrayPointer.Length > 0)
        {
            // A nested array that is absent (a boundary with no branches): insert the member holding [record].
            int slash = arrayPointer.LastIndexOf('/');
            string parentPointer = arrayPointer[..slash];
            string member = arrayPointer[(slash + 1)..];
            JsonSourceNode? parent = text.Resolve(parentPointer);
            RecordShape? parentShape = s.ShapeAt(d, parentPointer);
            if (parent is not { Kind: JsonSourceKind.Object } || parentShape is null) return OpResult.Refused($"{d.FileName}: no record at {JsonSchemaSubset.Show(parentPointer)}.");
            edits = JsonTextOps.InsertMember(text, parent, InsertIndex(parentShape, parent, member, d.KeyComparer), member, JsonFresh.Array(new[] { record }));
            newPointer = arrayPointer + "/0";
            insertAt = 0;
        }
        else
        {
            if (array is not { Kind: JsonSourceKind.Array }) return OpResult.Refused($"{d.FileName}: {JsonSchemaSubset.Show(arrayPointer)} is not an array.");
            if (insertAt < 0 || insertAt > array.Elements.Count) insertAt = array.Elements.Count;
            JsonSourceNode? neighbour = insertAt > 0 ? array.Elements[insertAt - 1] : array.Elements.Count > 0 ? array.Elements[0] : null;
            JsonContainerStyle? style = neighbour is { Kind: JsonSourceKind.Object } ? JsonLayout.Detect(text, neighbour) : null;
            edits = JsonTextOps.InsertElement(text, array, insertAt, record, style);
            newPointer = arrayPointer + "/" + insertAt.ToString(CultureInfo.InvariantCulture);
        }

        JsonSourceText after = text.Apply(edits).Text;
        string top = TopRecordPointer(s, d, newPointer);
        // A brand-new top-level record has no "before"; a new nested record is checked against its parent record.
        List<SchemaError> added = coll is not null
            ? RecordErrors(s, d, after, top).ToList()
            : NewErrors(s, d, text, after, top, top);
        if (added.Count > 0) return OpResult.Refused($"A new {shape.Title.ToLowerInvariant()} would not be valid: {ShowErrors(added)}");

        using (CaseTransaction tx = s.Begin($"{d.FileName}: add {shape.Title.ToLowerInvariant()} at {JsonSchemaSubset.Show(newPointer)}"))
        {
            if (create is not null) tx.Create(d, create);
            tx.Patch(d, edits);
            tx.Commit();
        }
        var select = new RecordRef(d.Id, arrayPointer, insertAt);
        return OpResult.Done($"Added {shape.Title.ToLowerInvariant()} {RecordRead.Describe(d, after.Resolve(newPointer)!, insertAt, coll is not null && d.Spec is null ? arrayPointer : "")} (U to undo).", select);
    }

    /// <summary>
    /// Duplicate a record: its bytes copied directly after it (alignment survives). A bus copy gets id = max + 1,
    /// so the case stays valid; a branch or class copy keeps the name and the message says to rename it.
    /// </summary>
    public static OpResult DuplicateRecord(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument d = s.Documents[r.Document];
        if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist.");
        JsonSourceText text = d.Text;
        JsonSourceNode? array = text.Resolve(r.ArrayPointer);
        if (array is not { Kind: JsonSourceKind.Array } || r.Index < 0 || r.Index >= array.Elements.Count)
            return OpResult.Refused($"{d.FileName}: there is no record {r.Index.ToString(CultureInfo.InvariantCulture)} to duplicate.");

        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.CopyElement(text, array, r.Index);
        JsonSourceText after = text.Apply(edits).Text;
        RecordRef copy = r.WithIndex(r.Index + 1);
        string note = "";
        List<IReadOnlyList<JsonTextEdit>> followUps = new();

        RecordCollection? coll = s.CollectionAt(r.Document, r.ArrayPointer);
        if (coll?.Role == CaseRoles.Buses)
        {
            ReferenceIndex ix = ReferenceIndex.Build(s);
            if (ix.MaxBusId == int.MaxValue) return OpResult.Refused("the largest bus id is already 2147483647; no new id is available.");
            int newId = ix.MaxBusId + 1;
            JsonSourceNode copied = after.Resolve(copy.Pointer)!;
            JsonSourceMember? idMember = copied.FindMember("id", d.KeyComparer);
            if (idMember is null) return OpResult.Refused("the bus to duplicate has no id.");
            followUps.Add(JsonTextOps.ReplaceValue(after, idMember.Value, JsonLiteral.FormatInteger(newId)));
            note = $" The copy is bus {newId.ToString(CultureInfo.InvariantCulture)} (largest id + 1).";
        }
        else if (coll?.Role == CaseRoles.Overlay)
        {
            // Phase 5b: an overlay record id must be unique across the case, so the copy takes a fresh one, "<id>-copy"
            // (then -copy-2, -copy-3 ...: every one still matches the id pattern). Its children are not copied.
            ReferenceIndex ix = ReferenceIndex.Build(s);
            JsonSourceNode copied = after.Resolve(copy.Pointer)!;
            JsonSourceMember? idMember = copied.FindMember("id", d.KeyComparer);
            if (idMember?.Value.StringValue is not string id) return OpResult.Refused("the overlay record to duplicate has no id.");
            string newId = id + "-copy";
            for (int n = 2; ix.OverlayRecords.ContainsKey(newId); n++) newId = id + "-copy-" + n.ToString(CultureInfo.InvariantCulture);
            followUps.Add(JsonTextOps.ReplaceValue(after, idMember.Value, JsonLiteral.FormatString(newId)));
            note = $" The copy is record '{newId}' (its children, if any, are not copied).";
        }
        else if (KeyFieldOf(coll?.Role) is string key && RecordRead.Str(after.Resolve(copy.Pointer)!, key, d.KeyComparer) is string shared)
            note = $" The copy shares the {key} '{shared}'; rename it (R) before relying on it.";

        using (CaseTransaction tx = s.Begin($"{d.FileName}: duplicate {JsonSchemaSubset.Show(r.Pointer)}"))
        {
            tx.Patch(d, edits);
            foreach (IReadOnlyList<JsonTextEdit> e in followUps) tx.Patch(d, e);
            tx.Commit();
        }
        return OpResult.Done($"Duplicated {RecordRead.Describe(d, d.Text.Resolve(copy.Pointer)!, copy.Index, d.Spec is null ? r.ArrayPointer : "")}.{note} (U to undo)", copy);
    }

    /// <summary>
    /// Delete a record. Refused while anything refers to it (a bus id, a branch name, a class name), listing the first
    /// <see cref="ReferrersShown"/> referrers and the count.
    /// </summary>
    public static OpResult DeleteRecord(CaseSession s, RecordRef r)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument d = s.Documents[r.Document];
        if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist.");
        JsonSourceText text = d.Text;
        JsonSourceNode? array = text.Resolve(r.ArrayPointer);
        if (array is not { Kind: JsonSourceKind.Array } || r.Index < 0 || r.Index >= array.Elements.Count)
            return OpResult.Refused($"{d.FileName}: there is no record {r.Index.ToString(CultureInfo.InvariantCulture)} to delete.");
        JsonSourceNode rec = array.Elements[r.Index];
        RecordCollection? coll = s.CollectionAt(r.Document, r.ArrayPointer);
        string what = RecordRead.Describe(d, rec, r.Index, d.Spec is null ? r.ArrayPointer : "");

        if (coll is not null)
        {
            ReferenceIndex ix = ReferenceIndex.Build(s);
            IReadOnlyList<ReferenceIndex.Referrer> refs = KeyReferrers(coll.Role, d.KeyComparer, rec, ix);
            if (refs.Count > 0)
                return OpResult.Refused($"Delete refused: {refs.Count} record(s) refer to {what}: {ListReferrers(refs)}");
        }

        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.RemoveElement(text, array, r.Index);
        if (coll is null)
        {
            JsonSourceText after = text.Apply(edits).Text;
            string top = TopRecordPointer(s, d, r.Pointer);
            List<SchemaError> added = NewErrors(s, d, text, after, top, top);
            if (added.Count > 0) return OpResult.Refused($"Delete refused, the record would not be valid: {ShowErrors(added)}");
        }

        using (CaseTransaction tx = s.Begin($"{d.FileName}: delete {what}"))
        {
            tx.Patch(d, edits);
            tx.Commit();
        }
        int remaining = array.Elements.Count - 1;
        RecordRef? select = remaining == 0 ? null : r.WithIndex(Math.Min(r.Index, remaining - 1));
        return OpResult.Done($"Deleted {what} (U to undo).", select);
    }

    // =================================================================================================================
    // Rename with cascade
    // =================================================================================================================

    /// <summary>
    /// Rename a key (bus id, line or transformer name, asset-class name) and every reference to it, as ONE
    /// transaction across files. Refused when the new key collides; for a branch name, also when the old name is
    /// shared by several branches and a boundary or protection refers to it (they match by name only, so which branch
    /// they mean is ambiguous). Of the OLTCs on a shared name only those the solver assigns to THIS branch follow
    /// (the <see cref="ReferenceIndex.OltcBranch"/> mirror). With <paramref name="dryRun"/> nothing changes and the
    /// message says what would happen (the UI shows it for confirmation).
    /// </summary>
    public static OpResult RenameKey(CaseSession s, RecordRef r, string typed, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(typed);
        CaseDocument d = s.Documents[r.Document];
        RecordCollection? coll = s.CollectionAt(r.Document, r.ArrayPointer);
        string? key = KeyFieldOf(coll?.Role);
        if (key is null || coll is null) return OpResult.Refused($"{d.FileName} {JsonSchemaSubset.Show(r.ArrayPointer)}: these records have no key to rename.");
        if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist.");
        JsonSourceText text = d.Text;
        JsonSourceNode? rec = text.Resolve(r.Pointer);
        if (rec is not { Kind: JsonSourceKind.Object }) return OpResult.Refused($"{d.FileName}: there is no record {r.Index.ToString(CultureInfo.InvariantCulture)}.");
        RecordShape shape = coll.Shape;
        FieldSpec f = shape.Field(key)!;
        if (!TryValue(f, typed, out JsonFresh value, out string why)) return OpResult.Refused($"{f.Title}: {why}");
        JsonSourceMember? keyMember = rec.FindMember(key, d.KeyComparer);
        if (keyMember is null) return OpResult.Refused($"the record has no {key} to rename; set it instead.");
        if (SameValue(text, keyMember.Value, value)) return OpResult.NoChange($"{key} unchanged.");

        ReferenceIndex ix = ReferenceIndex.Build(s);
        if (KeyCollision(s, coll.Role, ix, value, r) is string clash) return OpResult.Refused($"Rename refused: {clash}");
        string newLiteral = value.Literal!;
        string oldText = text.RawText(keyMember.Value);
        if (coll.Role == CaseRoles.Overlay)
        {
            // Phase 5b: a new overlay id must still be a valid id (the schema's id pattern), checked on the record as renamed.
            IReadOnlyList<JsonTextEdit> idEdit = JsonTextOps.ReplaceValue(text, keyMember.Value, newLiteral);
            List<SchemaError> added = NewErrors(s, d, text, text.Apply(idEdit).Text, r.Pointer, r.Pointer);
            if (added.Count > 0) return OpResult.Refused($"Rename refused, the record would not be valid: {ShowErrors(added)}");
        }

        // Which references follow the key.
        var follow = new List<ReferenceIndex.Referrer>();
        switch (coll.Role)
        {
            case CaseRoles.Buses:
            {
                int busId = RecordRead.Int(rec, "id", d.KeyComparer)!.Value;
                follow.AddRange(ix.BusReferrers(busId));
                follow.AddRange(ix.OverlayBusReferrers(busId));   // Phase 5b: the overlay's bus references follow too
                break;
            }
            case CaseRoles.Lines:
            case CaseRoles.Transformers:
            {
                string oldName = keyMember.Value.StringValue!;
                IReadOnlyList<ReferenceIndex.Referrer> refs = ix.BranchReferrers(oldName);
                bool shared = ix.Branches.TryGetValue(oldName, out List<RecordRef>? named) && named.Count > 1;
                var byName = refs.Where(x => s.CollectionAt(x.Record.Document, x.Record.ArrayPointer)?.Role != CaseRoles.Oltcs).ToList();
                if (shared && byName.Count > 0)
                    return OpResult.Refused($"Rename refused: the name '{oldName}' is shared by {named!.Count} branches and {byName.Count} boundary or protection record(s) refer to it by name only, so which branch they mean is ambiguous: {ListReferrers(byName)}");
                // Phase 5b: the overlay's references to this line (or transformer) by name follow it; on a shared name
                // which branch they mean is ambiguous (the overlay checker already reports such a reference), so refused.
                IReadOnlyList<ReferenceIndex.Referrer> overlay = ix.OverlayNameReferrers(coll.Role == CaseRoles.Lines ? Overlay.AssetRefTarget.Line : Overlay.AssetRefTarget.Transformer, oldName);
                if (shared && overlay.Count > 0)
                    return OpResult.Refused($"Rename refused: the name '{oldName}' is shared by {named!.Count} branches and {overlay.Count} asset overlay record(s) refer to it by name only, so which branch they mean is ambiguous: {ListReferrers(overlay)}");
                follow.AddRange(byName);
                follow.AddRange(overlay);
                // OLTCs: only those the solver assigns to this very branch.
                for (int k = 0; k < ix.Oltcs.Count; k++)
                    if (ix.OltcBranch[k] == r && refs.Any(x => x.Record == ix.Oltcs[k]))
                        follow.Add(refs.First(x => x.Record == ix.Oltcs[k]));
                break;
            }
            case CaseRoles.AssetClasses:
                follow.AddRange(ix.ClassReferrers(keyMember.Value.StringValue!));
                break;
            case CaseRoles.Overlay:
                // Phase 5b: the record's children (their "parent") and every record reference to it follow.
                follow.AddRange(ix.OverlayRecordReferrers(keyMember.Value.StringValue!));
                break;
        }

        string summary = $"Rename {key} {oldText} -> {newLiteral}: {follow.Count} reference(s) follow";
        if (dryRun) return OpResult.NoChange(summary + (follow.Count > 0 ? ": " + ListReferrers(follow) : "."));

        // Edits per document, sorted by offset (they are disjoint value tokens).
        var perDoc = new Dictionary<int, List<JsonTextEdit>>();
        void AddEdit(int docId, JsonTextEdit e)
        {
            if (!perDoc.TryGetValue(docId, out List<JsonTextEdit>? l)) perDoc[docId] = l = new List<JsonTextEdit>();
            l.Add(e);
        }
        AddEdit(d.Id, JsonTextEdit.ReplaceText(keyMember.Value.Start, keyMember.Value.Length, newLiteral));
        foreach (ReferenceIndex.Referrer x in follow)
        {
            CaseDocument rd = s.Documents[x.Record.Document];
            // Phase 5b: an overlay referrer names its value token exactly (it sits inside the record's refs); every model
            // referrer's value is the member Field of its record, as before.
            JsonSourceNode v = x.ValuePointer is string vp
                ? rd.Text.Resolve(vp)!
                : rd.Text.Resolve(x.Record.Pointer)!.FindMember(x.Field, rd.KeyComparer)!.Value;
            AddEdit(rd.Id, JsonTextEdit.ReplaceText(v.Start, v.Length, newLiteral));
        }

        using (CaseTransaction tx = s.Begin(summary))
        {
            foreach ((int docId, List<JsonTextEdit> edits) in perDoc.OrderBy(kv => kv.Key))
                tx.Patch(s.Documents[docId], edits.OrderBy(e => e.Offset).ToList());
            tx.Commit();
        }
        return OpResult.Done(summary + " (U to undo).", r);
    }

    // =================================================================================================================
    // Bus layout coordinates (UI programme Phase 4.2 map editing; moved here from GridSim.Ui on 2026-10-24)
    // =================================================================================================================

    /// <summary>The "x" and "y" members of a bus record: its layout coordinates (the only members
    /// <see cref="SetBusCoordinates"/> touches).</summary>
    internal const string BusXField = "x";

    /// <inheritdoc cref="BusXField"/>
    internal const string BusYField = "y";

    /// <summary>One bus of a <see cref="SetBusCoordinates"/> change: the bus record and its new x and y as typed text
    /// (the caller's own number form; the map writes them rounded, in shortest round-trip form).</summary>
    internal sealed record CoordinateEdit(RecordRef Bus, string X, string Y);

    /// <summary>
    /// Set "x" and "y" of every bus record in <paramref name="edits"/> as ONE transaction (one undo step) described as
    /// <paramref name="description"/>: each value parsed by <see cref="TryValue"/>, each member replaced or inserted at
    /// its schema-order position (lossless: no other byte of any file changes), every touched record checked for new
    /// schema errors (refused, changing nothing, if any). A member that already holds the value is left as it is;
    /// nothing to do at all is NoChange. <paramref name="select"/> is the record the selection follows.
    /// <para>Why Core: it is the engine's multi-record pattern (<see cref="SetOnUnits"/>, a station rename) for the x and
    /// y of bus records, built on the engine's own parsing, lossless patches, schema check and transaction. It was first
    /// written in GridSim.Ui's map editing on those internals; it lives with them now, and the map only converts a
    /// clicked point into the two numbers. It adds no rule of its own.</para>
    /// </summary>
    internal static OpResult SetBusCoordinates(CaseSession s, IReadOnlyList<CoordinateEdit> edits, string description, RecordRef? select)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(description);
        if (edits.Count == 0) return OpResult.NoChange("No bus to move.");
        foreach (CoordinateEdit e in edits) ArgumentNullException.ThrowIfNull(e);
        if (edits.Select(e => e.Bus).Distinct().Count() != edits.Count)
            throw new ArgumentException("a bus record is listed twice in one coordinate change", nameof(edits));

        var perDoc = new List<(CaseDocument Doc, List<IReadOnlyList<JsonTextEdit>> Passes)>();
        int changed = 0;
        foreach (IGrouping<int, CoordinateEdit> group in edits.GroupBy(e => e.Bus.Document))
        {
            CaseDocument d = s.Documents[group.Key];
            if (!d.Exists) return OpResult.Refused($"{d.FileName} does not exist.");
            JsonSourceText before = d.Text;
            JsonSourceText text = before;
            var passes = new List<IReadOnlyList<JsonTextEdit>>();

            // One pass per member, so the x and the y of one record are never two inserts at the same place: the y pass
            // works on the text the x pass made. Within a pass the records are taken in file order (ascending, disjoint
            // edits, as SetOnUnits makes them).
            foreach (string field in new[] { BusXField, BusYField })
            {
                var patch = new List<JsonTextEdit>();
                foreach (CoordinateEdit e in group.OrderBy(e => text.Resolve(e.Bus.Pointer)?.Start ?? -1))
                {
                    RecordRef bus = e.Bus;
                    if (s.CollectionAt(bus.Document, bus.ArrayPointer)?.Role != CaseRoles.Buses)
                        return OpResult.Refused($"{ShowRecord(d, bus)} is not a bus record; only bus records have layout coordinates.");
                    if (text.Resolve(bus.Pointer) is not { Kind: JsonSourceKind.Object } rec)
                        return OpResult.Refused($"{d.FileName}: there is no record at {JsonSchemaSubset.Show(bus.Pointer)}.");
                    RecordShape shape = s.ShapeAt(d, bus.Pointer)
                        ?? throw new InvalidOperationException($"{d.FileName}: no schema describes the bus at {bus.Pointer}.");
                    FieldSpec f = shape.Field(field)
                        ?? throw new InvalidOperationException($"{shape.Title} has no field '{field}': the bus schema no longer carries layout coordinates.");
                    if (f.Kind != FieldKind.Number)
                        throw new InvalidOperationException($"{shape.Title} field '{field}' is a {f.Kind}, not a number: the layout coordinate convention changed.");
                    string typed = field == BusXField ? e.X : e.Y;
                    if (!TryValue(f, typed, out JsonFresh value, out string why)) return OpResult.Refused($"{f.Title}: {why}");
                    JsonSourceMember? m = rec.FindMember(field, d.KeyComparer);
                    if (m is not null && SameValue(text, m.Value, value)) continue;
                    patch.AddRange(m is not null
                        ? JsonTextOps.ReplaceValue(text, m.Value, value.Literal!)
                        : JsonTextOps.InsertMember(text, rec, InsertIndex(shape, rec, field, d.KeyComparer), field, value));
                    changed++;
                }
                if (patch.Count == 0) continue;
                passes.Add(patch);
                text = text.Apply(patch).Text;
            }
            if (passes.Count == 0) continue;

            // Only members were added or replaced, so every record keeps its pointer: check each touched one.
            foreach (CoordinateEdit e in group)
            {
                List<SchemaError> added = NewErrors(s, d, before, text, e.Bus.Pointer, e.Bus.Pointer);
                if (added.Count > 0) return OpResult.Refused($"Move refused, {ShowRecord(d, e.Bus)} would not be valid: {ShowErrors(added)}");
            }
            perDoc.Add((d, passes));
        }
        if (changed == 0) return OpResult.NoChange("x and y unchanged (the bus is already there).");

        using (CaseTransaction tx = s.Begin(description))
        {
            foreach ((CaseDocument d, List<IReadOnlyList<JsonTextEdit>> passes) in perDoc)
                foreach (IReadOnlyList<JsonTextEdit> patch in passes) tx.Patch(d, patch);
            tx.Commit();
        }
        return OpResult.Done($"{char.ToUpperInvariant(description[0])}{description[1..]} ({changed.ToString(CultureInfo.InvariantCulture)} member(s) written).", select);
    }

    // "generation.json #17" (v1) or "good.network.json buses #0" (v2): a record by file, array and position, without its
    // name (the form the map's refusals have always used; a root-object record, index -1, is its file name alone).
    private static string ShowRecord(CaseDocument d, RecordRef r)
        => r.Index < 0 ? d.FileName : d.FileName + (r.ArrayPointer.Length > 0 ? " " + r.ArrayPointer.TrimStart('/') : "")
            + " #" + r.Index.ToString(CultureInfo.InvariantCulture);

    // =================================================================================================================
    // Stations
    // =================================================================================================================

    /// <summary>Rename a station: every unit of the group gets the new name (generators are not referenced by name).</summary>
    public static OpResult RenameStation(CaseSession s, Station station, string newName)
        => SetOnUnits(s, station, "name", newName, $"station '{station?.DisplayName}' renamed to '{newName}'");

    /// <summary>Retype a station: every unit of the group gets the new type (it moves to that fuel group).</summary>
    public static OpResult RetypeStation(CaseSession s, Station station, string newType)
        => SetOnUnits(s, station, "type", newType, $"station '{station?.DisplayName}' moved to type '{newType}'");

    private static OpResult SetOnUnits(CaseSession s, Station station, string field, string typed, string description)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(typed);
        CaseDocument d = s.Doc("generation.json");
        RecordShape shape = s.RecordShapeOf(d);
        FieldSpec f = shape.Field(field)!;
        if (!TryValue(f, typed, out JsonFresh value, out string why)) return OpResult.Refused($"{f.Title}: {why}");

        // One patch list over the file: each unit's member replaced or inserted. Units are in file order, so the
        // edits are ascending and disjoint.
        JsonSourceText text = d.Text;
        var edits = new List<JsonTextEdit>();
        int changed = 0;
        foreach (int u in station.Units)
        {
            JsonSourceNode rec = text.Root.Elements[u];
            JsonSourceMember? m = rec.FindMember(field, d.KeyComparer);
            if (m is not null && SameValue(text, m.Value, value)) continue;
            edits.AddRange(m is not null
                ? JsonTextOps.ReplaceValue(text, m.Value, value.Literal!)
                : JsonTextOps.InsertMember(text, rec, InsertIndex(shape, rec, field, d.KeyComparer), field, value));
            changed++;
        }
        if (changed == 0) return OpResult.NoChange($"{field} unchanged on every unit.");

        JsonSourceText after = text.Apply(edits).Text;
        foreach (int u in station.Units)
        {
            string p = "/" + u.ToString(CultureInfo.InvariantCulture);
            List<SchemaError> added = NewErrors(s, d, text, after, p, p);
            if (added.Count > 0) return OpResult.Refused($"{f.Title}: refused, unit #{u.ToString(CultureInfo.InvariantCulture)} would not be valid: {ShowErrors(added)}");
        }

        using (CaseTransaction tx = s.Begin(description))
        {
            tx.Patch(d, edits);
            tx.Commit();
        }
        return OpResult.Done($"{char.ToUpperInvariant(description[0])}{description[1..]}: {changed} unit(s) changed (U to undo).", new RecordRef(d.Id, "", station.Units[0]));
    }

    // =================================================================================================================
    // Normalise key spelling (Q1)
    // =================================================================================================================

    /// <summary>
    /// Rewrite every member name whose spelling differs from the schema's canonical one only by case (the loader
    /// accepts it, the strict schema does not; Q1). One transaction across all files; values are untouched.
    /// </summary>
    public static OpResult NormaliseKeys(CaseSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var perDoc = new List<(CaseDocument Doc, List<JsonTextEdit> Edits)>();
        int total = 0;
        foreach (CaseDocument d in s.Documents.Where(x => x.Exists && x.Spec is not null))
        {
            var edits = new List<JsonTextEdit>();
            RecordShape shape = s.RecordShapeOf(d);
            JsonSourceNode root = d.Text.Root;
            if (d.Spec!.IsArray && root.Kind == JsonSourceKind.Array)
            {
                foreach (JsonSourceNode rec in root.Elements) if (rec.Kind == JsonSourceKind.Object) Normalise(s, d.Text, rec, shape, edits);
            }
            else if (root.Kind == JsonSourceKind.Object) Normalise(s, d.Text, root, shape, edits);
            if (edits.Count > 0) { perDoc.Add((d, edits)); total += edits.Count; }
        }
        if (total == 0) return OpResult.NoChange("Every member name already has its canonical spelling.");
        using (CaseTransaction tx = s.Begin($"normalise {total} member name spelling(s)"))
        {
            foreach ((CaseDocument d, List<JsonTextEdit> edits) in perDoc) tx.Patch(d, edits);
            tx.Commit();
        }
        return OpResult.Done($"Normalised {total} member name spelling(s) in {string.Join(", ", perDoc.Select(p => p.Doc.FileName))} (U to undo).");
    }

    private static void Normalise(CaseSession s, JsonSourceText text, JsonSourceNode obj, RecordShape shape, List<JsonTextEdit> edits)
    {
        foreach (JsonSourceMember m in obj.Members)
        {
            FieldSpec? f = shape.FieldIgnoringCase(m.Name);
            if (f is null) continue;
            if (!string.Equals(f.Name, m.Name, StringComparison.Ordinal))
                edits.Add(JsonTextEdit.ReplaceText(m.NameStart, m.NameEnd - m.NameStart, JsonLiteral.FormatString(f.Name)));
            if (f.Nested is not SchemaLocation loc || !s.Catalogue.IsRecord(loc)) continue;
            RecordShape inner = s.Catalogue.Shape(loc);
            switch (f.Kind)
            {
                case FieldKind.Object when m.Value.Kind == JsonSourceKind.Object:
                    Normalise(s, text, m.Value, inner, edits);
                    break;
                case FieldKind.Array when m.Value.Kind == JsonSourceKind.Array:
                    foreach (JsonSourceNode e in m.Value.Elements) if (e.Kind == JsonSourceKind.Object) Normalise(s, text, e, inner, edits);
                    break;
                case FieldKind.Map when m.Value.Kind == JsonSourceKind.Object:
                    foreach (JsonSourceMember e in m.Value.Members) if (e.Value.Kind == JsonSourceKind.Object) Normalise(s, text, e.Value, inner, edits);
                    break;
            }
        }
    }
}
