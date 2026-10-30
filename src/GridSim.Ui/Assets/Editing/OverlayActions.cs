using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 5b (2026-10-20; ASSETS plan section 3.3 "Assets/Overlay" and section 3.5's "Add overlay child (by the
// kind's parentKinds), Attach overlay to this asset"): adding asset overlay records in Author mode. Each add is ONE engine
// operation (OverlayOps.AddRecord: one transaction, one undo step), and every rule is the engine's and the kind spec's:
// the id unique across every overlay file, the parent of one of the kind's parentKinds, each reference declared by the
// kind and resolving in the case, every required role and parameter given, each value as the kind spec types it. Duplicate,
// Delete (refused while children or record references point at the record, listing them) and Rename (the id, with every
// parent and record reference following) are the ordinary RecordActions, which the engine applies with its overlay rules.
//
// Where a record is added depends on what is selected:
//   - an overlay record: a CHILD of it, of a kind whose parentKinds name its kind, in the same overlay file;
//   - a model asset (a bus, line, transformer, generator or demand record): a record ATTACHED to it, of a kind with a role
//     pointing at that type of element, the role filled in with the asset's id or name;
//   - the "Asset overlay" class node: a record of any kind (a parent is asked for when the kind needs one).
// Overlay files are not created here: a case gets an overlay by listing one in its manifest's "assets" (Phase 5a).

/// <summary>One kind an overlay record may be added as: the kind, its title, and the role that attaches it to the
/// selected model asset (null when it is not attached by a role).</summary>
public sealed record OverlayKindChoice(string Kind, string Title, string? AttachRole);

/// <summary>The Author mode's overlay adds (UI programme Phase 5b), each one engine operation and one undo step.</summary>
public static class OverlayActions
{
    /// <summary>The field key of the new record's id in the Add prompt.</summary>
    public const string IdField = "id";

    /// <summary>The field key of the new record's parent in the Add prompt.</summary>
    public const string ParentField = "parent";

    /// <summary>The field key of the overlay file to add to (asked only when the case has several and no parent decides).</summary>
    public const string FileField = "file";

    /// <summary>The model roles an overlay record can be attached to, with the target type their records are.</summary>
    private static AssetRefTarget? TargetOfRole(string? role) => role switch
    {
        CaseRoles.Buses => AssetRefTarget.Bus,
        CaseRoles.Lines => AssetRefTarget.Line,
        CaseRoles.Transformers => AssetRefTarget.Transformer,
        CaseRoles.Generators => AssetRefTarget.Generator,
        CaseRoles.Demands => AssetRefTarget.Demand,
        _ => null,
    };

    /// <summary>True when the case has an overlay file to add to (one the manifest lists and that exists).</summary>
    public static bool HasOverlayFile(CaseAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        return authoring.Session.AssetDocuments.Any(d => d.Exists);
    }

    /// <summary>True when an overlay record can be attached to records of <paramref name="role"/> (bus, line, transformer,
    /// generator or demand).</summary>
    public static bool IsAttachableRole(string? role) => TargetOfRole(role) is not null;

    /// <summary>
    /// The kinds an overlay record may be added as at <paramref name="at"/>: under an overlay record, the kinds whose
    /// parentKinds name its kind; at a model asset, the kinds with a role pointing at its type (with that role); at null
    /// (the overlay class node), every kind. Sorted by kind.
    /// </summary>
    public static IReadOnlyList<OverlayKindChoice> KindsFor(CaseAuthoring authoring, CaseRef? at)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        if (at is null) return OverlayOps.Kinds.Kinds.Select(k => new OverlayKindChoice(k.Kind, TitleOf(k.Kind), null)).ToList();
        CaseSession s = authoring.Session;
        RecordRef r = at.ToRecordRef();
        if (OverlayOps.IsOverlayRecord(s, r))
            return OverlayOps.KindsUnder(OverlayOps.KindAt(s, r) ?? "").Select(k => new OverlayKindChoice(k.Kind, TitleOf(k.Kind), null)).ToList();
        if (TargetOfRole(authoring.RoleOf(at)) is AssetRefTarget t)
            return OverlayOps.KindsAttachableTo(t).Select(x => new OverlayKindChoice(x.Kind.Kind, TitleOf(x.Kind.Kind), x.Role.Role)).ToList();
        return Array.Empty<OverlayKindChoice>();
    }

    // A kind's title from its kind spec ("Substation (asset overlay)").
    private static string TitleOf(string kind) => OverlayOps.Kinds.Parameters.Find(kind)?.Title ?? kind;

    /// <summary>
    /// What the Add prompt asks for a new record of <paramref name="kind"/> at <paramref name="at"/>: its id; its parent
    /// when the kind takes one and the context does not give it (the choices are the records it may sit under); the
    /// overlay file when the case has several and no parent decides; every required reference role (with the case's
    /// elements of its type as choices) except the one that attaches it to the selected asset; every required
    /// parameter (an enum's values as choices).
    /// </summary>
    public static IReadOnlyList<NewRecordField> FieldsFor(CaseAuthoring authoring, CaseRef? at, string kind)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(kind);
        OverlayKindSpec spec = OverlayOps.Kinds.Find(kind) ?? throw new ArgumentException($"'{kind}' is not an asset overlay kind.", nameof(kind));
        CaseSession s = authoring.Session;
        ReferenceIndex ix = ReferenceIndex.Build(s);
        bool underRecord = at is not null && OverlayOps.IsOverlayRecord(s, at.ToRecordRef());
        string? attachRole = at is not null && !underRecord ? KindsFor(authoring, at).FirstOrDefault(k => k.Kind == kind)?.AttachRole : null;

        var fields = new List<NewRecordField> { new(IdField, "id (unique across every overlay file; letters, digits, . _ : -)", "String", Array.Empty<string>()) };
        if (!underRecord && spec.ParentKinds.Count > 0)
            fields.Add(new(ParentField, "parent (" + (spec.ParentRequired ? "required" : "optional") + ": a record of kind " + string.Join(" or ", spec.ParentKinds) + ")",
                "Parent", OverlayOps.ParentChoices(s, ix, spec, selfId: null)));
        IReadOnlyList<string> files = authoring.OverlayFileNames;
        if (!underRecord && files.Count > 1)
            fields.Add(new(FileField, "overlay file (empty: " + files[0] + ")", "Enum", files));
        foreach (OverlayRefSpec role in spec.Refs.Values.Where(r => r.Required && r.Role != attachRole))
            fields.Add(new(OverlayOps.RefPrefix + role.Role, OverlayOps.RefField(role).Title, "Reference", Limit(OverlayOps.TargetChoices(s, ix, role, null))));
        foreach (Core.IO.Json.Schema.KindParamSpec p in OverlayOps.Kinds.Parameters.Find(kind)?.Params.Values.Where(p => p.Required) ?? Enumerable.Empty<Core.IO.Json.Schema.KindParamSpec>())
        {
            FieldSpec f = OverlayOps.ParamField(p);
            fields.Add(new(f.Name, f.Title, f.Kind.ToString(), f.EnumTexts));
        }
        return fields;
    }

    // A prompt lists a manageable number of choices (a reference to a bus of a big case has thousands).
    private static IReadOnlyList<string> Limit(IReadOnlyList<string> choices)
        => choices.Count <= FieldGridModel.ChoiceLimit ? choices : choices.Take(FieldGridModel.ChoiceLimit).ToList();

    /// <summary>
    /// Add an overlay record of <paramref name="kind"/> at <paramref name="at"/> from the Add prompt's values (keyed as
    /// <see cref="FieldsFor"/> keys them; "refs.role" and "params.name" for optional ones too, "note" for a note): under
    /// an overlay record (its parent, in its file), attached to a model asset (the attaching role set to the asset's id or
    /// name), or (at null) wherever the values say. One undo step; refused, with the reason and nothing changed, by every
    /// rule of the engine and the kind spec.
    /// </summary>
    public static AuthoringResult Add(CaseAuthoring authoring, CaseRef? at, string kind, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(values);
        CaseSession s = authoring.Session;
        if (!HasOverlayFile(authoring))
            return AuthoringResult.Refused("Add refused: this case has no asset overlay file to add to (a case gets one by listing it in its manifest's \"assets\").");

        string id = "";
        string? parent = null, file = null, note = null;
        var refs = new List<KeyValuePair<string, string>>();
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in values)
        {
            if (kv.Key == IdField) id = kv.Value.Trim();
            else if (kv.Key == ParentField) parent = kv.Value.Trim();
            else if (kv.Key == FileField) file = kv.Value.Trim();
            else if (kv.Key == "note") note = kv.Value;
            else if (kv.Key.StartsWith(OverlayOps.RefPrefix, StringComparison.Ordinal)) refs.Add(new(kv.Key[OverlayOps.RefPrefix.Length..], kv.Value));
            else if (kv.Key.StartsWith(OverlayOps.ParamPrefix, StringComparison.Ordinal)) parameters.Add(new(kv.Key[OverlayOps.ParamPrefix.Length..], kv.Value));
            else return AuthoringResult.Refused($"Add refused: '{kv.Key}' is not a field of a new overlay record.");
        }

        // The overlay file: the parent's own under a record, else the one named, else the first.
        CaseDocument[] docs = s.AssetDocuments.Where(d => d.Exists).ToArray();
        int doc = docs[0].Id;
        if (at is not null && OverlayOps.IsOverlayRecord(s, at.ToRecordRef()))
        {
            CaseDocument pd = s.Documents[at.Document];
            if (pd.Text.Resolve(at.Pointer) is not { Kind: JsonSourceKind.Object } prec || RecordRead.Str(prec, "id", pd.KeyComparer) is not string pid)
                return AuthoringResult.Refused("Add refused: the selected overlay record has no id, so nothing can be added under it.");
            if (!string.IsNullOrEmpty(parent) && parent != pid)
                return AuthoringResult.Refused($"Add refused: the record is added under the selected record '{pid}', not '{parent}'.");
            parent = pid;
            doc = pd.Id;
        }
        else if (!string.IsNullOrEmpty(file))
        {
            CaseDocument? named = docs.FirstOrDefault(d => string.Equals(d.FileName, file, StringComparison.Ordinal));
            if (named is null) return AuthoringResult.Refused($"Add refused: '{file}' is not an overlay file of the case (they are: {string.Join(", ", docs.Select(d => d.FileName))}).");
            doc = named.Id;
        }

        // Attached to a model asset: the attaching role is that asset's id or name.
        if (at is not null && !OverlayOps.IsOverlayRecord(s, at.ToRecordRef()))
        {
            OverlayKindChoice? choice = KindsFor(authoring, at).FirstOrDefault(k => k.Kind == kind);
            if (choice?.AttachRole is not string role)
                return AuthoringResult.Refused($"Add refused: a '{kind}' record has no role that points at {authoring.Describe(at)}.");
            if (refs.Any(x => x.Key == role)) return AuthoringResult.Refused($"Add refused: role '{role}' is set by the selected asset; it is not typed.");
            if (AnchorKey(authoring, at) is not string key)
                return AuthoringResult.Refused($"Add refused: {authoring.Describe(at)} has no {(authoring.RoleOf(at) == CaseRoles.Buses ? "id" : "name")}, so nothing can point at it.");
            refs.Insert(0, new(role, key));
        }
        return authoring.Apply(x => OverlayOps.AddRecord(x, doc, kind, id, parent, refs, parameters, note));
    }

    /// <summary>The key an overlay reference names a model asset by: a bus's id, any other element's name; null when the
    /// record has none.</summary>
    public static string? AnchorKey(CaseAuthoring authoring, CaseRef at)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(at);
        CaseSession s = authoring.Session;
        CaseDocument d = s.Documents[at.Document];
        if (!d.Exists || d.Text.Resolve(at.Pointer) is not { Kind: JsonSourceKind.Object } rec) return null;
        return authoring.RoleOf(at) == CaseRoles.Buses
            ? RecordRead.Int(rec, "id", d.KeyComparer)?.ToString(CultureInfo.InvariantCulture)
            : RecordRead.Str(rec, "name", d.KeyComparer);
    }
}
