using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): the editing operations on a v2 case's templates, bindings and overrides. Every one
// follows the case editor's rules unchanged (GridSim.Core CaseEditing): typed text is parsed strictly (CaseOps.TryValue,
// with the field spec the kind spec gives the parameter, OverlayOps.ParamField: the same adapter the overlay editor
// uses), an edit that would ADD a problem is refused with the reason (a schema error in the manifest, CaseOps.NewErrors;
// a parameter the KIND SPEC refuses, KindSpecRegistry.CheckParams, the loader's own check, with its own message), and the
// byte patches are applied as ONE CaseSession transaction, so each operation is one undo step. A parameter edit replaces
// exactly the value token it changes (a value in the object form {v, unit, prov} has only its v replaced), so a saved
// manifest differs from the file on disk in that token alone (lossless).
// Where the engine already has the operation it is used as it is: a binding rule is added, rebound and removed with
// CaseOps.AddRecord / SetField / DeleteRecord (the manifest's "bindings" records), and a generator is bound to a template
// with CaseOps.SetField of its own "template" member (ResetField unbinds it). The rest (a template's parameters, adding,
// duplicating and deleting a local template, an override's values) are patches built with the lossless text layer
// (JsonTextOps) as OverlayOps builds its own. The checks that need the whole case (a template that is used cannot be
// deleted, a binding must name an active inverter template the case can see, an override must say why) are made here,
// before any patch, and each refusal names what is in the way.

/// <summary>Phase 4.4: the template, binding and override operations (each one transaction, or refused with the reason).</summary>
internal static partial class TemplateOps
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The id prefix of a case's own template (the loader's: a local gridsim.local.NAME@1 is asset class NAME).</summary>
    public const string LocalIdPrefix = V2CaseLoader.LocalIdPrefix;

    /// <summary>The provenance a new template is written with: the honesty rule's own (a curated placeholder, [VERIFY]),
    /// because a value typed in AUTHOR has no stated source until the user gives one.</summary>
    public const string NewTemplateProvenanceNote = "[VERIFY] added in AUTHOR: give each parameter its source";

    // common.schema.json templateId (the schema's own pattern, restated for the refusal before the schema check runs).
    [GeneratedRegex("^gridsim\\.[A-Za-z0-9][A-Za-z0-9_-]*(\\.[A-Za-z0-9][A-Za-z0-9_-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdPattern();

    // =================================================================================================================
    // Shared
    // =================================================================================================================

    // The manifest of a v2 case session, or the refusal (a v1 case or a lone network file has no templates of this kind).
    private static string? Manifest(CaseSession s, out CaseDocument d, out JsonSourceText text, out JsonSourceNode root)
    {
        d = s.Documents[0];
        text = null!;
        root = null!;
        if (s.Format != CaseFormat.V2Case || d.SchemaFile != CaseSession.V2CaseSchema)
            return "templates, bindings and overrides belong to a v2 case manifest (gridsim-case/2); this case is not one.";
        if (!d.Exists || d.Text.Root.Kind != JsonSourceKind.Object) return d.FileName + " is not a JSON object; fix the manifest first.";
        text = d.Text;
        root = text.Root;
        return null;
    }

    private static string Show(string pointer, int index) => pointer + "/" + index.ToString(Inv);

    // Where a new member goes in an object whose members follow "order": just after the nearest present predecessor in
    // that order, else first (the overlay editor's rule, so a parameter lands in the kind file's order).
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

    // A JSON string literal as a fresh scalar.
    private static JsonFresh Str(string v) => JsonFresh.Scalar(JsonLiteral.FormatString(v));

    // Check a manifest edit (the schema errors it would ADD, then the kind-spec errors it would add) and apply it as one
    // transaction. The message reads as CaseOps.SetField's.
    private static OpResult Commit(CaseSession s, CaseDocument d, JsonSourceText text, IReadOnlyList<JsonTextEdit> edits, string description,
        string done, Func<JsonSourceText, IReadOnlyList<SchemaError>>? kindCheck, RecordRef? select = null)
    {
        (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) applied = text.Apply(edits);
        JsonSourceText after = applied.Text;
        List<SchemaError> added = CaseOps.NewErrors(s, d, text, after, "", "");
        if (added.Count == 0 && kindCheck is not null)
        {
            // The kind spec's verdict before and after: only a problem the edit ADDS refuses it.
            var was = new HashSet<string>(kindCheck(text).Select(e => e.Pointer + "\n" + e.Message), StringComparer.Ordinal);
            added = kindCheck(after).Where(e => !was.Contains(e.Pointer + "\n" + e.Message)).ToList();
        }
        if (added.Count > 0) return OpResult.Refused(description + ": refused, the case would not be valid: " + CaseOps.ShowErrors(added));
        using (CaseTransaction tx = s.Begin(d.FileName + " " + description))
        {
            tx.Patch(d, text, applied);
            tx.Commit();
        }
        return OpResult.Done(done + " (U to undo).", select);
    }

    // The kind check of one template's params object at a pointer (KindSpecRegistry.CheckParams, the loader's own).
    private static Func<JsonSourceText, IReadOnlyList<SchemaError>> ParamsCheck(string kind, string templatePointer, bool placeholder, StringComparer cmp)
        => t => t.Resolve(templatePointer) is { Kind: JsonSourceKind.Object } tpl && RecordRead.Member(tpl, "params", cmp) is { Kind: JsonSourceKind.Object } ps
            ? TemplateKinds.Specs.CheckParams(kind, t.ElementOf(ps), placeholder, templatePointer + "/params")
            : Array.Empty<SchemaError>();

    // A local template's node and its kind spec, or the refusal.
    private static string? Local(CaseTemplates t, JsonSourceText text, int index, out TemplateInfo info, out JsonSourceNode node, out KindSpec spec)
    {
        info = null!;
        node = null!;
        spec = null!;
        TemplateInfo? found = t.Local.FirstOrDefault(x => x.LocalIndex == index);
        if (found is null) return "there is no local template " + index.ToString(Inv) + " in the manifest.";
        info = found;
        if (text.Resolve(Show("/localTemplates", index)) is not { Kind: JsonSourceKind.Object } n) return "there is no local template at /localTemplates/" + index.ToString(Inv) + ".";
        node = n;
        if (TemplateKinds.Specs.Find(found.Kind) is not KindSpec k)
            return found.Ref + ": '" + found.Kind + "' is not a template kind GridSim knows (no " + found.Kind + KindSpecRegistry.KindFileSuffix + "), so its parameters cannot be edited.";
        spec = k;
        return null;
    }

    // =================================================================================================================
    // A local template's parameters (the kind-spec grid)
    // =================================================================================================================

    /// <summary>
    /// Set parameter <paramref name="param"/> of local template <paramref name="index"/> from typed text, parsed as the
    /// kind spec types it. A bare value has its token replaced; a value in the object form has only its v replaced (its
    /// unit and provenance kept); an absent one is inserted in the kind file's order. Refused when the kind has no such
    /// parameter, the template is a placeholder, the text does not parse, or the kind spec refuses the value (type, enum,
    /// range: KindSpecRegistry.CheckParams, its own message).
    /// </summary>
    public static OpResult SetParam(CaseSession s, CaseTemplates t, int index, string param, string typed)
    {
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(typed);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out _) is string m) return OpResult.Refused(m);
        if (Local(t, text, index, out TemplateInfo info, out JsonSourceNode tpl, out KindSpec spec) is string why) return OpResult.Refused(why);
        if (!spec.Params.TryGetValue(param, out KindParamSpec? p))
            return OpResult.Refused(info.Ref + ": kind '" + info.Kind + "' has no parameter '" + param + "' (declared: " + string.Join(", ", spec.Params.Keys) + ").");
        if (info.IsPlaceholder)
            return OpResult.Refused(info.Ref + " is a placeholder template: by the placeholder rule every value is null, so none can be set. A placeholder stands for something not yet modelled.");
        FieldSpec f = OverlayOps.ParamField(p);
        if (!CaseOps.TryValue(f, typed, out JsonFresh value, out string bad)) return OpResult.Refused(param + ": " + bad);
        if (RecordRead.Member(tpl, "params", d.KeyComparer) is not { Kind: JsonSourceKind.Object } ps)
            return OpResult.Refused(info.Ref + ": its params member is missing or not an object; fix the template first.");

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
                    return OpResult.Refused(param + ": the object form has no 'v' member; fix the template first.");
                target = inner.Value;
            }
            if (CaseOps.SameValue(text, target, value)) return OpResult.NoChange(info.Ref + " " + param + " unchanged (" + text.RawText(target) + ").");
            old = text.RawText(target);
            edits = JsonTextOps.ReplaceValue(text, target, value.Literal!);
        }
        else
        {
            old = "(absent)";
            edits = JsonTextOps.InsertMember(text, ps, InsertAt(ps, spec.Params.Keys.ToList(), param, d.KeyComparer), param, value);
        }
        string pointer = Show("/localTemplates", index);
        return Commit(s, d, text, edits, info.Ref + " params." + param + ": " + old + " -> " + value.Literal,
            info.Ref + " " + param + " " + old + " -> " + value.Literal, ParamsCheck(info.Kind, pointer, info.IsPlaceholder, d.KeyComparer),
            new RecordRef(d.Id, "/localTemplates", index));
    }

    /// <summary>Remove parameter <paramref name="param"/> of local template <paramref name="index"/> (the consumers then
    /// use their code default). Refused for a parameter the kind requires.</summary>
    public static OpResult ResetParam(CaseSession s, CaseTemplates t, int index, string param)
    {
        ArgumentNullException.ThrowIfNull(param);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out _) is string m) return OpResult.Refused(m);
        if (Local(t, text, index, out TemplateInfo info, out JsonSourceNode tpl, out KindSpec spec) is string why) return OpResult.Refused(why);
        if (spec.Params.TryGetValue(param, out KindParamSpec? p) && p.Required)
            return OpResult.Refused(info.Ref + " " + param + " is required by kind '" + info.Kind + "' and cannot be reset to absent.");
        if (RecordRead.Member(tpl, "params", d.KeyComparer) is not { Kind: JsonSourceKind.Object } ps)
            return OpResult.Refused(info.Ref + ": its params member is missing or not an object; fix the template first.");
        int at = ps.IndexOfMember(param, d.KeyComparer);
        if (at < 0) return OpResult.NoChange(info.Ref + " " + param + " is already absent.");
        string old = text.RawText(ps.Members[at].Value);
        string pointer = Show("/localTemplates", index);
        return Commit(s, d, text, JsonTextOps.RemoveMember(text, ps, at), info.Ref + " params." + param + ": " + old + " -> absent",
            info.Ref + " " + param + " " + old + " -> absent (the consumers use their code default)", ParamsCheck(info.Kind, pointer, info.IsPlaceholder, d.KeyComparer),
            new RecordRef(d.Id, "/localTemplates", index));
    }

    /// <summary>Set a local template's title or description (CaseOps.SetField on the template record: schema-checked).
    /// The id, revision, kind and status are not edited here (bindings and overrides name the id; Duplicate makes a new one).</summary>
    public static OpResult SetText(CaseSession s, CaseTemplates t, int index, string field, string typed)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(typed);
        if (field is not ("title" or "description"))
            return OpResult.Refused("only a template's title and description are edited here: its id, revision and kind are what bindings and overrides name (Duplicate makes a template with a new id), and its status follows the placeholder rule.");
        if (Manifest(s, out _, out JsonSourceText text, out _) is string m) return OpResult.Refused(m);
        if (Local(t, text, index, out _, out _, out _) is string why) return OpResult.Refused(why);
        return CaseOps.SetField(s, 0, Show("/localTemplates", index), field, typed);
    }

    // =================================================================================================================
    // Add, duplicate, delete
    // =================================================================================================================

    /// <summary>
    /// Add a local template of inverter kind <paramref name="kind"/> named <paramref name="name"/> (its id is
    /// gridsim.local.NAME, revision 1, active, no parameters set: every consumer uses its code default until a value is
    /// typed), with the honesty rule's provenance ([VERIFY]: no source stated yet). Refused for a kind that is not an
    /// inverter template kind, a name the id pattern refuses, and an id any template of the case already has.
    /// </summary>
    public static OpResult AddTemplate(CaseSession s, CaseTemplates t, string kind, string name, string? title)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out JsonSourceNode root) is string m) return OpResult.Refused(m);
        kind = kind.Trim();
        if (!TemplateKinds.IsInverterKind(kind))
            return OpResult.Refused("Add template refused: '" + kind + "' is not an inverter template kind (one of: " + string.Join(", ", TemplateKinds.InverterKinds) + ").");
        string id = LocalIdPrefix + name.Trim();
        if (!TemplateIdPattern().IsMatch(id))
            return OpResult.Refused("Add template refused: '" + id + "' is not a template id (gridsim. then dot-separated parts of letters, digits, '_' and '-', each starting with a letter or digit).");
        if (t.All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal)) is TemplateInfo clash)
            return OpResult.Refused("Add template refused: the id " + id + " is already used by " + clash.Ref + " (" + clash.Source + ")"
                + (clash.IsLocal ? "; a template is defined once." : "; a local template may not shadow an import."));
        string shownTitle = string.IsNullOrWhiteSpace(title) ? TemplateKinds.TitleOf(kind) + " (" + name.Trim() + ")" : title.Trim();
        JsonFresh record = JsonFresh.Object(new[]
        {
            new KeyValuePair<string, JsonFresh>("id", Str(id)),
            new KeyValuePair<string, JsonFresh>("rev", JsonFresh.Scalar("1")),
            new KeyValuePair<string, JsonFresh>("kind", Str(kind)),
            new KeyValuePair<string, JsonFresh>("status", Str("active")),
            new KeyValuePair<string, JsonFresh>("title", Str(shownTitle)),
            new KeyValuePair<string, JsonFresh>("params", JsonFresh.Object(Array.Empty<KeyValuePair<string, JsonFresh>>())),
            new KeyValuePair<string, JsonFresh>("provenance", JsonFresh.Object(new[]
            {
                new KeyValuePair<string, JsonFresh>("_default", JsonFresh.Object(new[]
                {
                    new KeyValuePair<string, JsonFresh>("tag", Str("CuratedPlaceholder")),
                    new KeyValuePair<string, JsonFresh>("note", Str(NewTemplateProvenanceNote)),
                })),
            })),
        });
        (IReadOnlyList<JsonTextEdit> edits, int at) = AppendTo(s, d, text, root, "localTemplates", record);
        return Commit(s, d, text, edits, "add template " + id + "@1 (" + kind + ")", "Added template " + id + "@1 (" + kind + ")", null,
            new RecordRef(d.Id, "/localTemplates", at));
    }

    // Append a record to a top-level array of the manifest, creating the member (in schema order) when it is absent.
    private static (IReadOnlyList<JsonTextEdit> Edits, int Index) AppendTo(CaseSession s, CaseDocument d, JsonSourceText text, JsonSourceNode root, string member, JsonFresh record)
    {
        if (RecordRead.Member(root, member, d.KeyComparer) is { Kind: JsonSourceKind.Array } arr)
        {
            JsonSourceNode? neighbour = arr.Elements.Count > 0 ? arr.Elements[^1] : null;
            JsonContainerStyle? style = neighbour is { Kind: JsonSourceKind.Object } ? JsonLayout.Detect(text, neighbour) : null;
            return (JsonTextOps.InsertElement(text, arr, arr.Elements.Count, record, style), arr.Elements.Count);
        }
        RecordShape shape = s.RecordShapeOf(d);
        return (JsonTextOps.InsertMember(text, root, CaseOps.InsertIndex(shape, root, member, d.KeyComparer), member, JsonFresh.Array(new[] { record })), 0);
    }

    /// <summary>
    /// Duplicate a template as a new local template: a local one is copied byte for byte directly after itself with its id
    /// changed to a fresh one (id-copy, then id-copy-2 ...); a library template is copied into the case as a local
    /// template gridsim.local.&lt;its id without the gridsim. prefix&gt; (then -copy ...), so its values can be edited
    /// here. Nothing is rebound: bindings and overrides still name the source.
    /// </summary>
    public static OpResult Duplicate(CaseSession s, CaseTemplates t, TemplateInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out JsonSourceNode root) is string m) return OpResult.Refused(m);
        var used = new HashSet<string>(t.All.Select(x => x.Id), StringComparer.Ordinal);
        // A local copy is id-copy (then id-copy-2, -copy-3 ...). A library copy is first tried without a suffix, as
        // gridsim.local.<the library id after "gridsim.">, then with -copy, -copy-2 ... like a local one.
        string baseId = source.IsLocal ? source.Id
            : LocalIdPrefix + (source.Id.StartsWith("gridsim.", StringComparison.Ordinal) ? source.Id["gridsim.".Length..] : source.Id);
        string newId = source.IsLocal ? baseId + "-copy" : baseId;
        if (used.Contains(newId)) newId = baseId + "-copy";
        for (int n = 2; used.Contains(newId); n++) newId = baseId + "-copy-" + n.ToString(Inv);
        string newRef = newId + "@" + source.Rev.ToString(Inv);

        if (source.IsLocal)
        {
            if (Local(t, text, source.LocalIndex, out _, out _, out _) is string why) return OpResult.Refused(why);
            JsonSourceNode arr = text.Resolve("/localTemplates")!;
            IReadOnlyList<JsonTextEdit> copy = JsonTextOps.CopyElement(text, arr, source.LocalIndex);
            JsonSourceText afterCopy = text.Apply(copy).Text;
            int at = source.LocalIndex + 1;
            if (afterCopy.Resolve(Show("/localTemplates", at)) is not { Kind: JsonSourceKind.Object } copied
                || copied.FindMember("id", d.KeyComparer) is not JsonSourceMember idMember)
                return OpResult.Refused(source.Ref + " has no id to change; fix the template first.");
            // Two steps (copy, then the copy's id) applied together: the id edit is made against the copied text, so the
            // patch is the net change from the text before to the text after both.
            JsonSourceText afterId = afterCopy.Apply(JsonTextOps.ReplaceValue(afterCopy, idMember.Value, JsonLiteral.FormatString(newId))).Text;
            IReadOnlyList<JsonTextEdit> net = new[] { NetEdit(text.ToArray(), afterId.ToArray()) };
            return Commit(s, d, text, net, "duplicate " + source.Ref + " as " + newRef,
                "Duplicated " + source.Ref + " as " + newRef + " (bindings and overrides still name " + source.Ref + "; rebind what should use the copy)",
                null, new RecordRef(d.Id, "/localTemplates", at));
        }

        if (source.LibraryJson is not JsonElement lib) return OpResult.Refused(source.Ref + ": the library template's text is not available.");
        var members = new List<KeyValuePair<string, JsonFresh>>();
        foreach (JsonProperty p in lib.EnumerateObject())
            members.Add(new KeyValuePair<string, JsonFresh>(p.Name, p.Name == "id" ? Str(newId) : JsonFresh.FromElement(p.Value)));
        (IReadOnlyList<JsonTextEdit> edits, int index) = AppendTo(s, d, text, root, "localTemplates", JsonFresh.Object(members));
        return Commit(s, d, text, edits, "copy library template " + source.Ref + " as " + newRef,
            "Copied library template " + source.Ref + " into the case as " + newRef + " (its values can now be edited here; bindings and overrides still name " + source.Ref + ")",
            null, new RecordRef(d.Id, "/localTemplates", index));
    }

    // The one edit that turns bytes b into bytes a: the differing middle between their common prefix and suffix.
    private static JsonTextEdit NetEdit(byte[] b, byte[] a)
    {
        int prefix = 0;
        int max = Math.Min(a.Length, b.Length);
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        return new JsonTextEdit(prefix, b.Length - prefix - suffix, a.AsSpan(prefix, a.Length - prefix - suffix).ToArray());
    }

    /// <summary>
    /// Delete local template <paramref name="index"/>. Refused while anything names it (a binding rule, an override,
    /// another template's controller slot, a network element's own template member), listing every one: the case would
    /// otherwise no longer load (a reference that resolves to nothing is refused by the loader).
    /// </summary>
    public static OpResult Delete(CaseSession s, CaseTemplates t, int index)
    {
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out _) is string m) return OpResult.Refused(m);
        if (Local(t, text, index, out TemplateInfo info, out _, out _) is string why) return OpResult.Refused(why);
        IReadOnlyList<string> refs = CaseTemplates.ReferrersOf(s, t, info.Ref);
        if (refs.Count > 0)
            return OpResult.Refused("Delete refused: " + refs.Count.ToString(Inv) + " thing(s) in the case use template " + info.Ref
                + ", so the case would no longer load without it. Rebind or remove them first: " + string.Join("; ", refs) + ".");
        JsonSourceNode arr = text.Resolve("/localTemplates")!;
        int remaining = arr.Elements.Count - 1;
        RecordRef? select = remaining == 0 ? null : new RecordRef(d.Id, "/localTemplates", Math.Min(index, remaining - 1));
        return Commit(s, d, text, JsonTextOps.RemoveElement(text, arr, index), "delete template " + info.Ref, "Deleted template " + info.Ref, null, select);
    }

    // =================================================================================================================
    // Bindings
    // =================================================================================================================

    /// <summary>Why <paramref name="reference"/> cannot be a generator's template (the loader's own rules: it must resolve
    /// to a template the case can see, be active, and be of a kind an asset class models; here, an inverter kind, the
    /// kinds a generator's controller is made from), or null.</summary>
    public static string? BindRefusal(CaseTemplates t, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        TemplateInfo? tpl = t.Find(reference);
        if (tpl is null) return "template " + reference + " is neither imported nor a local template of the case.";
        if (tpl.IsPlaceholder) return "template " + reference + " is a placeholder; a placeholder holds no values and can never be bound to an element.";
        if (!tpl.IsInverter)
            return "template " + reference + " is kind '" + tpl.Kind + "'; a generator is bound to an inverter template (" + string.Join(", ", TemplateKinds.InverterKinds) + ").";
        return null;
    }

    /// <summary>Add a binding rule: generators of type <paramref name="matchType"/> (a trailing * makes it a prefix) are
    /// bound to <paramref name="reference"/>, unless they name their own template. CaseOps.AddRecord on the manifest's
    /// bindings (the member is created when absent). Refused for a template that cannot be bound and for a match type a
    /// rule already has (the first rule would win, so a second one would never apply: rebind that rule instead).</summary>
    public static OpResult AddBinding(CaseSession s, CaseTemplates t, string matchType, string reference)
    {
        ArgumentNullException.ThrowIfNull(matchType);
        ArgumentNullException.ThrowIfNull(reference);
        if (Manifest(s, out _, out _, out _) is string m) return OpResult.Refused(m);
        if (BindRefusal(t, reference) is string why) return OpResult.Refused("Bind refused: " + why);
        string match = matchType.Trim();
        // The resolver compares types ignoring case, so two rules that differ only in case are the same rule.
        if (t.Bindings.FirstOrDefault(b => string.Equals(b.MatchType, match, StringComparison.OrdinalIgnoreCase)) is TemplateBindingRule same)
            return OpResult.Refused("Bind refused: binding rule #" + same.Index.ToString(Inv) + " already binds type '" + same.MatchType + "' (to " + same.Template
                + "); the first rule for a type wins, so a second would never apply. Rebind rule #" + same.Index.ToString(Inv) + " instead.");
        return CaseOps.AddRecord(s, 0, "/bindings", int.MaxValue, new[]
        {
            new KeyValuePair<string, string>("matchType", match),
            new KeyValuePair<string, string>("template", reference),
        });
    }

    /// <summary>Point binding rule <paramref name="ruleIndex"/> at <paramref name="reference"/> (CaseOps.SetField).</summary>
    public static OpResult Rebind(CaseSession s, CaseTemplates t, int ruleIndex, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (Manifest(s, out _, out _, out _) is string m) return OpResult.Refused(m);
        if (t.Bindings.All(b => b.Index != ruleIndex)) return OpResult.Refused("there is no binding rule #" + ruleIndex.ToString(Inv) + ".");
        if (BindRefusal(t, reference) is string why) return OpResult.Refused("Rebind refused: " + why);
        return CaseOps.SetField(s, 0, Show("/bindings", ruleIndex), "template", reference);
    }

    /// <summary>Remove binding rule <paramref name="ruleIndex"/> (CaseOps.DeleteRecord): the generators it bound are
    /// unbound (or bound by the next rule that matches them).</summary>
    public static OpResult RemoveBinding(CaseSession s, CaseTemplates t, int ruleIndex)
    {
        if (Manifest(s, out _, out _, out _) is string m) return OpResult.Refused(m);
        if (t.Bindings.All(b => b.Index != ruleIndex)) return OpResult.Refused("there is no binding rule #" + ruleIndex.ToString(Inv) + ".");
        return CaseOps.DeleteRecord(s, new RecordRef(0, "/bindings", ruleIndex));
    }

    /// <summary>Bind one generator to <paramref name="reference"/> by its own template member (it then wins over every
    /// binding rule): CaseOps.SetField of the generator's "template".</summary>
    public static OpResult BindGenerator(CaseSession s, CaseTemplates t, GeneratorBindingInfo generator, string reference)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(reference);
        if (Manifest(s, out _, out _, out _) is string m) return OpResult.Refused(m);
        if (BindRefusal(t, reference) is string why) return OpResult.Refused("Bind refused for " + generator.Label + ": " + why);
        return CaseOps.SetField(s, generator.Record.Document, generator.Record.Pointer, "template", reference);
    }

    /// <summary>Remove a generator's own template member (CaseOps.ResetField): it is then bound by the rules, if any match.</summary>
    public static OpResult UnbindGenerator(CaseSession s, GeneratorBindingInfo generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (Manifest(s, out _, out _, out _) is string m) return OpResult.Refused(m);
        if (generator.InlineTemplate is null)
            return OpResult.NoChange(generator.Label + " names no template of its own (" + generator.Route + "); remove or rebind the binding rule instead.");
        return CaseOps.ResetField(s, generator.Record.Document, generator.Record.Pointer, "template");
    }

    // =================================================================================================================
    // Overrides
    // =================================================================================================================

    /// <summary>
    /// Override parameter <paramref name="param"/> of template <paramref name="reference"/> (local or imported) with the
    /// typed value, for this case: the value already overridden has its token replaced; otherwise the parameter is added
    /// to the template's existing override entry, or a new entry is written with <paramref name="reason"/> and
    /// "shadow": "explicit" (both required by the schema: a silent shadow is not possible). Refused for a placeholder,
    /// a parameter the kind does not declare, a value the kind spec refuses (after merging, as the loader merges), and a
    /// new override with no reason.
    /// </summary>
    public static OpResult SetOverride(CaseSession s, CaseTemplates t, string reference, string param, string typed, string? reason)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(typed);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out JsonSourceNode root) is string m) return OpResult.Refused(m);
        TemplateInfo? tpl = t.Find(reference);
        if (tpl is null) return OpResult.Refused("Override refused: " + reference + " is neither imported nor a local template of the case.");
        if (tpl.IsPlaceholder) return OpResult.Refused("Override refused: " + reference + " is a placeholder template; a placeholder holds no values, so none can be overridden.");
        if (TemplateKinds.Specs.Find(tpl.Kind) is not KindSpec spec) return OpResult.Refused("Override refused: '" + tpl.Kind + "' is not a template kind GridSim knows.");
        if (!spec.Params.TryGetValue(param, out KindParamSpec? p))
            return OpResult.Refused("Override refused: kind '" + tpl.Kind + "' has no parameter '" + param + "' (declared: " + string.Join(", ", spec.Params.Keys) + ").");
        if (!CaseOps.TryValue(OverlayOps.ParamField(p), typed, out JsonFresh value, out string bad)) return OpResult.Refused("Override " + param + ": " + bad);

        IReadOnlyList<JsonTextEdit> edits;
        string old;
        if (t.OverrideOf(reference, param) is (TemplateOverrideEntry entry, _))
        {
            JsonSourceNode ps = text.Resolve(Show("/overrides", entry.Index) + "/params")!;
            JsonSourceNode target = ps.FindMember(param, d.KeyComparer)!.Value;
            if (target.Kind == JsonSourceKind.Object && target.FindMember("v", d.KeyComparer) is JsonSourceMember inner) target = inner.Value;
            if (CaseOps.SameValue(text, target, value)) return OpResult.NoChange(reference + " " + param + " override unchanged (" + text.RawText(target) + ").");
            old = "override " + text.RawText(target);
            edits = JsonTextOps.ReplaceValue(text, target, value.Literal!);
        }
        else if (t.OverridesOf(reference).FirstOrDefault() is TemplateOverrideEntry existing)
        {
            JsonSourceNode ps = text.Resolve(Show("/overrides", existing.Index) + "/params")!;
            old = TemplateValueText(tpl, param);
            edits = JsonTextOps.InsertMember(text, ps, InsertAt(ps, spec.Params.Keys.ToList(), param, d.KeyComparer), param, value);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(reason))
                return OpResult.Refused("Override refused: a new override must say why (its reason), because it shadows the template's own value; give a reason.");
            old = TemplateValueText(tpl, param);
            JsonFresh record = JsonFresh.Object(new[]
            {
                new KeyValuePair<string, JsonFresh>("template", Str(reference)),
                new KeyValuePair<string, JsonFresh>("params", JsonFresh.Object(new[] { new KeyValuePair<string, JsonFresh>(param, value) })),
                new KeyValuePair<string, JsonFresh>("reason", Str(reason.Trim())),
                new KeyValuePair<string, JsonFresh>("shadow", Str("explicit")),
            });
            (edits, _) = AppendTo(s, d, text, root, "overrides", record);
        }
        return Commit(s, d, text, edits, reference + " override " + param + ": " + old + " -> " + value.Literal,
            reference + " " + param + ": " + old + " -> override " + value.Literal, MergedCheck(tpl, d.KeyComparer));
    }

    /// <summary>Remove the override of <paramref name="param"/> of <paramref name="reference"/> (reset to the template's
    /// own value): the member is removed from its entry, and an entry left with no parameter is removed whole (the loader
    /// requires at least one parameter per override).</summary>
    public static OpResult ResetOverride(CaseSession s, CaseTemplates t, string reference, string param)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(param);
        if (Manifest(s, out CaseDocument d, out JsonSourceText text, out _) is string m) return OpResult.Refused(m);
        if (t.OverrideOf(reference, param) is not (TemplateOverrideEntry entry, TemplateParamLiteral was))
            return OpResult.NoChange(reference + " " + param + " is not overridden in this case.");
        TemplateInfo? tpl = t.Find(reference);
        string back = tpl is null ? "the template's value" : "template " + TemplateValueText(tpl, param);
        IReadOnlyList<JsonTextEdit> edits;
        if (entry.Params.Count == 1)
            edits = JsonTextOps.RemoveElement(text, text.Resolve("/overrides")!, entry.Index);
        else
        {
            JsonSourceNode ps = text.Resolve(Show("/overrides", entry.Index) + "/params")!;
            edits = JsonTextOps.RemoveMember(text, ps, ps.IndexOfMember(param, d.KeyComparer));
        }
        return Commit(s, d, text, edits, reference + " override " + param + ": " + was.Literal + " -> " + back,
            reference + " " + param + ": override " + was.Literal + " -> " + back + (entry.Params.Count == 1 ? " (the override entry, now empty, was removed)" : ""), null);
    }

    // The template's own value of a parameter in words ("0.05", "absent: code default 0.015 (2 consumers)").
    internal static string TemplateValueText(TemplateInfo tpl, string param)
    {
        if (tpl.Param(param) is TemplateParamLiteral v) return v.Literal;
        KindParamSpec? p = TemplateKinds.Specs.Find(tpl.Kind)?.Params.GetValueOrDefault(param);
        return "absent (" + (p is null ? "not declared" : TemplateKinds.Info(p).DefaultText) + ")";
    }

    // The kind check of a template's parameters with every override of it applied, as the loader merges them (template
    // order, overridden values in place, new keys appended): the merged set is what the loader checks again.
    private static Func<JsonSourceText, IReadOnlyList<SchemaError>> MergedCheck(TemplateInfo tpl, StringComparer cmp) => text =>
    {
        var merged = new List<KeyValuePair<string, string>>();
        foreach (TemplateParamLiteral p in tpl.Params) merged.Add(new KeyValuePair<string, string>(p.Name, p.Literal));
        if (text.Root.Kind == JsonSourceKind.Object && RecordRead.Member(text.Root, "overrides", cmp) is { Kind: JsonSourceKind.Array } os)
            foreach (JsonSourceNode o in os.Elements)
            {
                if (o.Kind != JsonSourceKind.Object || !string.Equals(RecordRead.Str(o, "template", cmp), tpl.Ref, StringComparison.Ordinal)) continue;
                if (RecordRead.Member(o, "params", cmp) is not { Kind: JsonSourceKind.Object } ps) continue;
                foreach (JsonSourceMember m in ps.Members)
                {
                    JsonSourceNode v = m.Value.Kind == JsonSourceKind.Object && RecordRead.Member(m.Value, "v", cmp) is JsonSourceNode inner ? inner : m.Value;
                    int at = merged.FindIndex(x => string.Equals(x.Key, m.Name, StringComparison.Ordinal));
                    var kv = new KeyValuePair<string, string>(m.Name, text.RawText(v));
                    if (at >= 0) merged[at] = kv;
                    else merged.Add(kv);
                }
            }
        string json = "{" + string.Join(",", merged.Select(kv => JsonLiteral.FormatString(kv.Key) + ":" + kv.Value)) + "}";
        using JsonDocument doc = JsonDocument.Parse(json);
        return TemplateKinds.Specs.CheckParams(tpl.Kind, doc.RootElement.Clone(), placeholder: false, "/overrides (" + tpl.Ref + " after its overrides)");
    };
}
