using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Dashboard;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3 (plan Phase 4 order item 3, "Per-element editors: bus, line (ohl/cable, r0/x0, circuits,
// ratings), 2W transformer (windings, neutral X), generator + converter block, demand, OLTC, boundary, protection";
// ASSETS plan section 4.1 rows 4.3: "per-element editors as PROPERTIES specialisations").
//
// What a per-element editor IS: the SAME field grid (FieldGridModel, built from RecordShape/FieldSpec) arranged for an
// engineer. Each editor takes the grid's rows (the very same FieldRow objects) and groups them into labelled sections in
// an engineering order (a line: identity and ends, then electrical, then ratings, ...). Nothing is written any other way:
// an edit in an editor is the row's own Commit (CaseOps.SetField through CaseAuthoring.Apply, one transaction and one undo
// step), a reset is the row's own Reset (CaseOps.ResetField). A nested block (a v2 generator's converter block, a v2
// transformer's windings and neutral reactance, a bus's ZIP model, a boundary's branch entries) is shown as the block's own
// row and, when the block is written, the rows of a field grid OF THE BLOCK (the same FieldGridModel class over the nested
// object's pointer, so its edits are CaseOps.SetField on that object, exactly as the engine already supports).
//
// What an editor ADDS on top of the grid, and only for display:
//   - labels and units for v2 fields whose schema property has no title (most v2 network properties carry only a type):
//     the title of the v1 schema field the v2 one was carried over from (FieldSpec of the v1 catalogue), else a short
//     label of this editor's own; the source is always named on the field (LabelSource), so nobody mistakes one for the
//     other;
//   - pickers for references (a bus id, a branch name, an asset class name) over the case's own records, from the
//     engine's reference index (the same index that refuses a dangling reference), and for enum and boolean fields;
//   - derived read-outs beside the inputs (an X/R ratio, ohms from per unit, a transformer's impedance on its own
//     rating, a generator's MVA at its P/Q corner, ...). Each is computed in double precision from the values as written
//     (or the loader's default where the field is absent and the default is a plain value), its formula is in a comment
//     where it is computed and in the read-out's Formula text, it is labelled "derived", and it is NEVER written back.
//   - the reason of a refused edit, shown inline on the field (Error), not only in the status line.
// Fields the loader refuses stay LOCKED with the loader's reason: the FieldRow already says so (LockedFields), and an
// edit of one is refused by the grid before the engine is asked, as in the generic grid.
//
// Every field of the record appears exactly once: a field no section names (a member the schema does not list, or a field
// a later schema adds) goes into a last "Other fields" section, so an editor can never hide a field the grid shows.

/// <summary>One choice of a picker: the value as it is typed (a bus id "12", a branch name) and what the list shows.</summary>
/// <param name="Value">The text the field takes when the choice is picked (what one would type).</param>
/// <param name="Display">What the picker shows ("12  'Drax' (400 kV)").</param>
public sealed record EditorChoice(string Value, string Display)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>
/// A derived read-out shown beside the inputs (display only, never written back): a label, the value in double
/// precision (null when it cannot be computed, with the reason in <see cref="Text"/>), its unit and its formula.
/// </summary>
/// <param name="Label">What the read-out is ("X/R ratio").</param>
/// <param name="Value">The value, or null (not computable; <see cref="Text"/> says why), or null for a text-only read-out.</param>
/// <param name="Unit">The unit ("ohm", "pu on own rating", "" for a ratio).</param>
/// <param name="Text">The value as shown (6 significant figures, invariant culture), or the reason it is not shown.</param>
/// <param name="Formula">The formula in words and symbols, with the inputs it uses.</param>
public sealed record DerivedReadout(string Label, double? Value, string Unit, string Text, string Formula)
{
    /// <summary>The marker every read-out carries in the view.</summary>
    public const string Marker = "derived";

    /// <summary>The value written round-trip ("R"), for the tooltip; "" when there is no value.</summary>
    public string ExactText => Value is double v ? v.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>A read-out with a value (shown to 6 significant figures).</summary>
    public static DerivedReadout Of(string label, double value, string unit, string formula)
        => new(label, value, unit, double.IsFinite(value) ? value.ToString("G6", CultureInfo.InvariantCulture) : "not finite (" + value.ToString(CultureInfo.InvariantCulture) + ")", formula);

    /// <summary>A read-out that cannot be computed: the reason is shown in place of the value.</summary>
    public static DerivedReadout NotShown(string label, string unit, string why, string formula) => new(label, null, unit, "n/a: " + why, formula);

    /// <summary>A text-only read-out (a vector group, a resolved reference).</summary>
    public static DerivedReadout Words(string label, string text, string formula) => new(label, null, "", text, formula);
}

/// <summary>
/// One field of a per-element editor: the grid's own row (<see cref="Row"/>) with an engineer's label, unit and help,
/// a picker where the field is a reference, an enum or a boolean, and the reason of the last refused edit.
/// </summary>
public sealed class EditorField : ObservableModel
{
    private string? _error;

    internal EditorField(FieldRow row, string path, int depth, string label, string unit, string labelSource, string help, string helpSource,
        IReadOnlyList<EditorChoice> choices, int choiceCount, string pickerKind)
    {
        Row = row;
        Path = path;
        Depth = depth;
        Label = label;
        Unit = unit;
        LabelSource = labelSource;
        Help = help;
        HelpSource = helpSource;
        Choices = choices;
        ChoiceCount = choiceCount;
        PickerKind = pickerKind;
    }

    /// <summary>The field grid row this field edits (the same object the generic grid shows).</summary>
    public FieldRow Row { get; }

    /// <summary>The member name in its record (for a nested block's field, the name inside the block).</summary>
    public string Field => Row.Field;

    /// <summary>Where the field is, from the element record: "rPu", or "converter.interfaced", or "branches[1].branch".</summary>
    public string Path { get; }

    /// <summary>0 for a field of the record itself, 1 for a field of a nested block (shown indented).</summary>
    public int Depth { get; }

    /// <summary>True for a field of a nested block.</summary>
    public bool IsNested => Depth > 0;

    /// <summary>The label (the schema title without its unit, or the v1 counterpart's, or the editor's own: see <see cref="LabelSource"/>).</summary>
    public string Label { get; }

    /// <summary>The unit, or "".</summary>
    public string Unit { get; }

    /// <summary>Where the label and unit come from: "schema" (this field's FieldSpec), "v1 schema &lt;file&gt; &lt;field&gt;"
    /// (the FieldSpec of the v1 field this v2 one was carried over from), or "editor" (this editor's own words).</summary>
    public string LabelSource { get; }

    /// <summary>The help line (the schema description, or the v1 counterpart's, or the editor's own: see <see cref="HelpSource"/>).</summary>
    public string Help { get; }

    /// <summary>Where the help comes from (as <see cref="LabelSource"/>; "" when there is no help).</summary>
    public string HelpSource { get; }

    /// <summary>The picker's choices (empty when the field has no picker). A reference lists at most
    /// <see cref="FieldGridModel.ChoiceLimit"/> of the case's records; any other value can still be typed.</summary>
    public IReadOnlyList<EditorChoice> Choices { get; }

    /// <summary>How many choices there are in all (more than <see cref="Choices"/> holds when the list was cut).</summary>
    public int ChoiceCount { get; }

    /// <summary>What the picker picks: "bus", "branch", "asset class", "enum", "boolean", or "" (no picker).</summary>
    public string PickerKind { get; }

    /// <summary>True when the field has a picker.</summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>True when the field is a reference to another record (a typed picker over the case's records).</summary>
    public bool IsReference => PickerKind is "bus" or "branch" or "asset class";

    /// <summary>A note under a cut list ("1204 buses; the first 200 are listed; type any other id"), or "".</summary>
    public string ChoicesNote => ChoiceCount > Choices.Count
        ? ChoiceCount.ToString(CultureInfo.InvariantCulture) + " choices; the first " + Choices.Count.ToString(CultureInfo.InvariantCulture) + " are listed; type any other."
        : "";

    /// <summary>True when the choices note has something to say.</summary>
    public bool HasChoicesNote => ChoicesNote.Length > 0;

    /// <summary>The kind of the field (the grid row's: Number, Integer, Boolean, String, Enum, Object, Array, ...).</summary>
    public string Kind => Row.Kind;

    /// <summary>The value as written ("" when absent).</summary>
    public string Literal => Row.Literal;

    /// <summary>The state column (the grid row's: "as written", "absent: default 1", the lock reason).</summary>
    public string StateText => Row.StateText;

    /// <summary>True when the loader refuses the field (it is shown locked; an edit is refused with the reason).</summary>
    public bool IsLocked => Row.IsLocked;

    /// <summary>The loader's reason for the lock, or null.</summary>
    public string? LockReason => Row.LockReason;

    /// <summary>True when the field takes typed text.</summary>
    public bool IsEditable => Row.IsEditable;

    /// <summary>True when the field can be reset to absent.</summary>
    public bool CanReset => Row.CanReset;

    /// <summary>The text being typed (the grid row's own; nothing changes until <see cref="Commit"/>).</summary>
    public string EditText
    {
        get => Row.EditText;
        set
        {
            if (string.Equals(Row.EditText, value ?? "", StringComparison.Ordinal)) return;
            Row.EditText = value ?? "";
            Raise(nameof(EditText));
        }
    }

    /// <summary>The reason the last edit or reset of this field was refused (shown inline), or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (string.Equals(_error, value, StringComparison.Ordinal)) return;
            _error = value;
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    /// <summary>True when the last edit or reset of this field was refused.</summary>
    public bool HasError => _error is not null;

    /// <summary>Commit the typed text through the grid row (one undo step, or refused: the reason is kept inline).</summary>
    public AuthoringResult Commit()
    {
        AuthoringResult r = Row.Commit();
        Error = r.Ok ? null : r.Message;
        return r;
    }

    /// <summary>Pick <paramref name="choice"/> (its value becomes the typed text) and commit it.</summary>
    public AuthoringResult Pick(EditorChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        EditText = choice.Value;
        return Commit();
    }

    /// <summary>Reset the field to absent through the grid row (refused: the reason is kept inline).</summary>
    public AuthoringResult Reset()
    {
        AuthoringResult r = Row.Reset();
        Error = r.Ok ? null : r.Message;
        return r;
    }

    /// <inheritdoc />
    public override string ToString() => Path + " = " + (Literal.Length > 0 ? Literal : "(absent)");
}

/// <summary>One labelled section of an editor: its fields in order, then its derived read-outs.</summary>
/// <param name="Title">The section heading ("Electrical (per unit on the case base, one circuit)").</param>
/// <param name="Fields">The fields, in the editor's order (a nested block's fields follow the block's own row).</param>
/// <param name="Readouts">The derived read-outs (display only).</param>
public sealed record EditorSection(string Title, IReadOnlyList<EditorField> Fields, IReadOnlyList<DerivedReadout> Readouts)
{
    /// <summary>True when the section has derived read-outs.</summary>
    public bool HasReadouts => Readouts.Count > 0;

    /// <summary>The field at <paramref name="path"/> (throws when the section has none).</summary>
    public EditorField Field(string path) => Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal))
        ?? throw new ArgumentException($"section '{Title}' has no field '{path}'.", nameof(path));
}

/// <summary>
/// A per-element editor (UI programme Phase 4.3): a record's field grid arranged in labelled sections with pickers and
/// derived read-outs. The specialisations are <see cref="BusEditorModel"/>, <see cref="LineEditorModel"/>,
/// <see cref="TransformerEditorModel"/>, <see cref="GeneratorEditorModel"/>, <see cref="DemandEditorModel"/>,
/// <see cref="OltcEditorModel"/>, <see cref="BoundaryEditorModel"/> and <see cref="ProtectionEditorModel"/>;
/// <see cref="ElementEditors.For"/> picks one for a record (null: the generic grid is shown).
/// </summary>
public abstract class ElementEditorModel
{
    /// <summary>The title of the last section, which holds every field no other section names.</summary>
    public const string OtherSection = "Other fields";

    internal ElementEditorModel(EditorContext context, string kindTitle, IReadOnlyList<SectionSpec> layout,
        Func<EditorContext, string, IReadOnlyList<DerivedReadout>> readouts)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        KindTitle = kindTitle;
        Grid = context.Grid;
        IsV2 = context.IsV2;
        Sections = Build(context, layout, readouts);
    }

    internal EditorContext Context { get; }

    /// <summary>The element kind ("Line", "Two-winding transformer").</summary>
    public string KindTitle { get; }

    /// <summary>The field grid the editor arranges (its rows are the editor's fields).</summary>
    public FieldGridModel Grid { get; }

    /// <summary>The record as messages name it (the grid's heading).</summary>
    public string Heading => Grid.Heading;

    /// <summary>The schema title of the record.</summary>
    public string ShapeTitle => Grid.ShapeTitle;

    /// <summary>The record the editor edits.</summary>
    public CaseRef Record => Grid.Record;

    /// <summary>True for a JSON v2 record (network.schema.json), false for v1.</summary>
    public bool IsV2 { get; }

    /// <summary>"JSON v2" or "JSON v1", for the heading line.</summary>
    public string FormatText => IsV2 ? "JSON v2 (" + Grid.SchemaFile + ")" : "JSON v1 (" + Grid.SchemaFile + ")";

    /// <summary>The sections, in order; the last is <see cref="OtherSection"/> only when it has fields.</summary>
    public IReadOnlyList<EditorSection> Sections { get; }

    /// <summary>
    /// UI programme Phase 4.4 (2026-10-21): on a generator of a v2 case, the template it is bound to (with a link to it),
    /// that template's overrides and, once asked for, the effective parameters as the v2 loader resolves them; null for
    /// every other editor (set by the Author model after the editor is made).
    /// </summary>
    public Templates.GeneratorTemplateModel? TemplateLink { get; internal set; }

    /// <summary>Phase 4.4: true when the editor shows a template link.</summary>
    public bool HasTemplateLink => TemplateLink is not null;

    /// <summary>Every field of every section, in order.</summary>
    public IEnumerable<EditorField> AllFields => Sections.SelectMany(s => s.Fields);

    /// <summary>The section titled <paramref name="title"/> (throws when there is none).</summary>
    public EditorSection Section(string title) => Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.Ordinal))
        ?? throw new ArgumentException($"the {KindTitle} editor has no section '{title}' (it has: {string.Join(", ", Sections.Select(s => s.Title))}).", nameof(title));

    /// <summary>The field at <paramref name="path"/> in any section (throws when there is none).</summary>
    public EditorField Field(string path) => AllFields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal))
        ?? throw new ArgumentException($"the {KindTitle} editor has no field '{path}'.", nameof(path));

    /// <summary>The read-out labelled <paramref name="label"/> in any section (throws when there is none).</summary>
    public DerivedReadout Readout(string label) => Sections.SelectMany(s => s.Readouts).FirstOrDefault(r => string.Equals(r.Label, label, StringComparison.Ordinal))
        ?? throw new ArgumentException($"the {KindTitle} editor has no read-out '{label}'.", nameof(label));

    // Arrange the grid's rows by the layout; any row the layout does not name goes to "Other fields".
    private static IReadOnlyList<EditorSection> Build(EditorContext c, IReadOnlyList<SectionSpec> layout,
        Func<EditorContext, string, IReadOnlyList<DerivedReadout>> readouts)
    {
        var used = new HashSet<FieldRow>();
        var sections = new List<EditorSection>(layout.Count + 1);
        foreach (SectionSpec spec in layout)
        {
            var fields = new List<EditorField>();
            foreach (string name in spec.Fields)
            {
                // A field the record's schema does not have (a v1-only or v2-only member) is simply not shown in it.
                FieldRow? row = c.Grid.Rows.FirstOrDefault(r => string.Equals(r.Field, name, StringComparison.Ordinal) && r.State != FieldState.Unknown);
                if (row is null || !used.Add(row)) continue;
                fields.Add(c.MakeField(row, c.Shape, name, 0));
                // A nested block (an object or an array of records), when written: the rows of a grid of the block.
                fields.AddRange(c.NestedFields(row));
            }
            IReadOnlyList<DerivedReadout> ro = readouts(c, spec.Title);
            if (fields.Count > 0 || ro.Count > 0) sections.Add(new EditorSection(spec.Title, fields, ro));
        }
        var rest = c.Grid.Rows.Where(r => !used.Contains(r)).Select(r => c.MakeField(r, c.Shape, r.Field, 0)).ToList();
        if (rest.Count > 0) sections.Add(new EditorSection(OtherSection, rest, Array.Empty<DerivedReadout>()));
        return sections;
    }

    /// <summary>The section layout of a kind: a title and the member names in order (v1 and v2 names together; a name
    /// the record's schema does not have is skipped).</summary>
    internal sealed record SectionSpec(string Title, params string[] Fields);
}

/// <summary>
/// Picks the per-element editor for a record (UI programme Phase 4.3), or null when its kind has none (an asset class,
/// an asset binding, an overlay record, a case file record): the generic field grid is shown for those.
/// </summary>
public static class ElementEditors
{
    /// <summary>The roles that have a per-element editor, in the order the plan lists them.</summary>
    public static IReadOnlyList<string> Roles { get; } = new[]
    {
        CaseRoles.Buses, CaseRoles.Lines, CaseRoles.Transformers, CaseRoles.Generators, CaseRoles.Demands,
        CaseRoles.Oltcs, CaseRoles.Boundaries, CaseRoles.Protections,
    };

    /// <summary>
    /// The editor of <paramref name="grid"/>'s record, whose role is <paramref name="role"/>, or null when the role has
    /// no per-element editor (or the record is an overlay record or a root record such as system.json).
    /// </summary>
    public static ElementEditorModel? For(CaseAuthoring authoring, string? role, FieldGridModel grid)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(grid);
        if (role is null || grid.IsOverlay || grid.Record.Index < 0 || !Roles.Contains(role, StringComparer.Ordinal)) return null;
        var c = new EditorContext(authoring, grid);
        return role switch
        {
            CaseRoles.Buses => new BusEditorModel(c),
            CaseRoles.Lines => new LineEditorModel(c),
            CaseRoles.Transformers => new TransformerEditorModel(c),
            CaseRoles.Generators => new GeneratorEditorModel(c),
            CaseRoles.Demands => new DemandEditorModel(c),
            CaseRoles.Oltcs => new OltcEditorModel(c),
            CaseRoles.Boundaries => new BoundaryEditorModel(c),
            CaseRoles.Protections => new ProtectionEditorModel(c),
            _ => null,
        };
    }
}

/// <summary>
/// What an editor reads while it is built: the grid, the record's schema shape and JSON node, the case facts the derived
/// read-outs need (the system base MVA, the buses' base voltages, the branches) and the reference index the pickers list.
/// Read only: nothing here writes.
/// </summary>
internal sealed class EditorContext
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private ReferenceIndex? _index;

    public EditorContext(CaseAuthoring authoring, FieldGridModel grid)
    {
        Authoring = authoring;
        Grid = grid;
        Session = authoring.Session;
        Doc = Session.Documents[grid.Record.Document];
        Shape = Session.ShapeAt(Doc, grid.Record.Pointer)
            ?? throw new ArgumentException($"{grid.Heading}: no schema describes the record.", nameof(grid));
        Node = Doc.Text.Resolve(grid.Record.Pointer)
            ?? throw new ArgumentException($"{grid.Heading}: there is no record at {grid.Record.Pointer}.", nameof(grid));
        IsV2 = string.Equals(Shape.Location.File, CaseSession.V2NetworkSchema, StringComparison.Ordinal);
    }

    public CaseAuthoring Authoring { get; }

    public FieldGridModel Grid { get; }

    public CaseSession Session { get; }

    public CaseDocument Doc { get; }

    public RecordShape Shape { get; }

    /// <summary>The record's JSON node as it is now.</summary>
    public JsonSourceNode Node { get; }

    public bool IsV2 { get; }

    /// <summary>The engine's reference index (built once per editor, on first use).</summary>
    public ReferenceIndex Index => _index ??= ReferenceIndex.Build(Session);

    // ------------------------------------------------------------------ values

    /// <summary>
    /// The value of number field <paramref name="name"/> of the record: as written, else the loader DTO's default when
    /// that is a plain number (the FieldSpec's DefaultLiteral), else null with the reason.
    /// </summary>
    public (double? Value, string Why) Num(string name) => NumOf(Node, Shape, name);

    /// <summary>As <see cref="Num"/> for the object <paramref name="node"/> of shape <paramref name="shape"/>.</summary>
    public (double? Value, string Why) NumOf(JsonSourceNode? node, RecordShape shape, string name)
    {
        JsonSourceNode? v = node is null ? null : RecordRead.Member(node, name, Doc.KeyComparer);
        if (v is { Kind: JsonSourceKind.Number, NumberValue: double d }) return (d, "");
        if (v is not null) return (null, name + " is written as " + Doc.Text.RawText(v) + ", not a number");
        FieldSpec? f = shape.Field(name);
        if (f?.DefaultLiteral is string lit && JsonLiteral.TryParseNumber(lit, out double dd, out _)) return (dd, "");
        return (null, name + " is absent (" + (f?.AbsentText ?? "no default") + ")");
    }

    /// <summary>True when member <paramref name="name"/> is written in the record.</summary>
    public bool Written(string name) => RecordRead.Member(Node, name, Doc.KeyComparer) is not null;

    /// <summary>A string member of the record as written, or null.</summary>
    public string? Str(string name) => RecordRead.Str(Node, name, Doc.KeyComparer);

    /// <summary>An integer member of the record as written, or null.</summary>
    public int? Int(string name) => RecordRead.Int(Node, name, Doc.KeyComparer);

    /// <summary>A nested object member of the record as written, or null.</summary>
    public JsonSourceNode? Obj(string name) => RecordRead.Member(Node, name, Doc.KeyComparer) is { Kind: JsonSourceKind.Object } o ? o : null;

    /// <summary>
    /// The system base MVA every per-unit value of the case is on, and where it was read: v1 system.json baseMva (absent
    /// file or member: the loader's default, 100); v2 the case manifest's system.baseMva (required). Null with the reason
    /// when the session holds no manifest (a v2 network file opened on its own).
    /// </summary>
    public (double? Value, string Source) BaseMva()
    {
        if (!IsV2)
        {
            CaseDocument? sys = Session.Documents.FirstOrDefault(d => d.Spec?.FileName == "system.json");
            if (sys is { Exists: true } && sys.Text.Resolve("") is { Kind: JsonSourceKind.Object } root
                && RecordRead.Num(root, "baseMva", sys.KeyComparer) is double written)
                return (written, "system.json baseMva");
            return (Core.IO.Json.JsonDefaults.BaseMva, "system.json baseMva absent: the loader's default");
        }
        CaseDocument? manifest = Session.Documents.FirstOrDefault(d => d.SchemaFile == CaseSession.V2CaseSchema && d.Exists);
        if (manifest?.Text.Resolve("/system") is { Kind: JsonSourceKind.Object } system
            && RecordRead.Num(system, "baseMva", manifest.KeyComparer) is double mva)
            return (mva, manifest.FileName + " system.baseMva");
        return (null, "no case manifest with system.baseMva is open (a network file opened on its own)");
    }

    /// <summary>The bus record of bus id <paramref name="id"/>, or null.</summary>
    public JsonSourceNode? Bus(int id, out CaseDocument? doc)
    {
        doc = null;
        if (!Index.Buses.TryGetValue(id, out RecordRef r)) return null;
        doc = Session.Documents[r.Document];
        return doc.Exists ? doc.Text.Resolve(r.Pointer) : null;
    }

    /// <summary>The base voltage (kV) of the bus member <paramref name="busField"/> of the record names, or null with the reason.</summary>
    public (double? Kv, string Why) BusKv(string busField)
    {
        if (Int(busField) is not int id) return (null, busField + " is not written as a bus id");
        return BusKvOf(id);
    }

    /// <summary>The base voltage (kV) of bus <paramref name="id"/>, or null with the reason.</summary>
    public (double? Kv, string Why) BusKvOf(int id)
    {
        if (Bus(id, out CaseDocument? d) is not JsonSourceNode bus) return (null, "bus " + id.ToString(Inv) + " does not exist");
        return RecordRead.Num(bus, "baseKv", d!.KeyComparer) is double kv ? (kv, "") : (null, "bus " + id.ToString(Inv) + " has no baseKv");
    }

    /// <summary>The first branch record (lines, then transformers) named <paramref name="name"/>, with its role, or null.</summary>
    public (JsonSourceNode? Node, string? Role, CaseDocument? Doc) Branch(string name)
    {
        if (!Index.Branches.TryGetValue(name, out List<RecordRef>? refs) || refs.Count == 0) return (null, null, null);
        RecordRef r = refs[0];
        CaseDocument d = Session.Documents[r.Document];
        return (d.Exists ? d.Text.Resolve(r.Pointer) : null, Session.CollectionAt(r.Document, r.ArrayPointer)?.Role, d);
    }

    // ------------------------------------------------------------------ fields

    // The v1 schema field each v2 field was carried over from (the v2 schema descriptions name it: "v1 rPu0", "v1
    // dynamicsClass", "v1 converterInterfaced"): (v2 shape pointer, v2 field) -> (v1 schema file, v1 shape pointer, v1 field).
    // A v2 field of the same name on the same element is its own counterpart (no entry needed).
    private static readonly Dictionary<string, (string File, string Pointer)> V1ShapeOf = new(StringComparer.Ordinal)
    {
        ["/$defs/bus"] = ("buses.schema.json", "/$defs/bus"),
        ["/$defs/zip"] = ("buses.schema.json", "/$defs/zip"),
        ["/$defs/generator"] = ("generation.schema.json", "/$defs/generator"),
        ["/$defs/converter"] = ("generation.schema.json", "/$defs/generator"),
        ["/$defs/demand"] = ("demand.schema.json", "/$defs/demand"),
        ["/$defs/line"] = ("transmission.schema.json", "/$defs/line"),
        ["/$defs/transformer"] = ("transformer.schema.json", "/$defs/transformer"),
        ["/$defs/oltc"] = ("onloadtapchanger.schema.json", "/$defs/oltc"),
        ["/$defs/boundary"] = ("boundaries.schema.json", "/$defs/boundary"),
        ["/$defs/boundary/properties/branches/items"] = ("boundaries.schema.json", "/$defs/boundaryBranch"),
        ["/$defs/protection"] = ("protections.schema.json", "/$defs/protection"),
    };

    private static readonly Dictionary<(string Pointer, string Field), string> V1Renamed = new()
    {
        [("/$defs/line", "r0Pu")] = "rPu0",
        [("/$defs/line", "x0Pu")] = "xPu0",
        [("/$defs/line", "template")] = "dynamicsClass",
        [("/$defs/transformer", "template")] = "dynamicsClass",
        [("/$defs/generator", "template")] = "dynamicsClass",
        [("/$defs/protection", "template")] = "class",
        [("/$defs/converter", "interfaced")] = "converterInterfaced",
        [("/$defs/converter", "faultCurrentPu")] = "converterFaultCurrentPu",
        [("/$defs/converter", "gridForming")] = "gridForming",
    };

    // The editor's own label, unit and help for a v2 field whose schema has no title and no v1 counterpart (keyed by the
    // shape pointer and the field). Only used when the FieldSpec gives nothing; the field says "editor" as its source.
    private static readonly Dictionary<(string Pointer, string Field), (string Label, string Unit, string Help)> Own = new()
    {
        [("/$defs/line", "type")] = ("Line type", "", "The line (conductor or cable) type template."),
        [("/$defs/transformer", "type")] = ("Transformer type", "", "The transformer type template."),
        [("/$defs/transformer", "r0Pu")] = ("Zero-sequence resistance", "pu, case base", ""),
        [("/$defs/transformer", "x0Pu")] = ("Zero-sequence reactance", "pu, case base", ""),
        [("/$defs/transformer", "windings")] = ("Winding connections", "", ""),
        [("/$defs/transformer", "neutralXPu")] = ("Neutral earthing reactance", "pu, case base", ""),
        [("/$defs/transformer", "inService")] = ("In service", "", ""),
        [("/$defs/line", "inService")] = ("In service", "", ""),
        [("/$defs/transformer/properties/windings", "from")] = ("From-side winding", "", "The from-side winding connection."),
        [("/$defs/transformer/properties/windings", "to")] = ("To-side winding", "", "The to-side winding connection."),
        [("/$defs/transformer/properties/neutralXPu", "from")] = ("From-side neutral reactance", "pu, case base", "Only on a WyeGrounded from side."),
        [("/$defs/transformer/properties/neutralXPu", "to")] = ("To-side neutral reactance", "pu, case base", "Only on a WyeGrounded to side."),
        [("/$defs/generator", "converter")] = ("Converter block", "", ""),
        [("/$defs/generator", "subtransientXdPu")] = ("Subtransient reactance X''d", "pu, unit rating", ""),
        [("/$defs/generator", "negativeSeqXPu")] = ("Negative-sequence reactance X2", "pu, unit rating", ""),
    };

    /// <summary>The v1 FieldSpec a v2 field was carried over from, with its name for the source text, or null.</summary>
    public static (FieldSpec Spec, string Source)? V1Counterpart(RecordShape shape, string field)
    {
        if (!string.Equals(shape.Location.File, CaseSession.V2NetworkSchema, StringComparison.Ordinal)) return null;
        if (!V1ShapeOf.TryGetValue(shape.Location.Pointer, out (string File, string Pointer) v1)) return null;
        string name = V1Renamed.TryGetValue((shape.Location.Pointer, field), out string? renamed) ? renamed : field;
        RecordShape v1Shape = FieldCatalogue.V1.Shape(new SchemaLocation(v1.File, v1.Pointer));
        return v1Shape.Field(name) is FieldSpec f ? (f, "v1 schema " + v1.File + " " + name) : null;
    }

    /// <summary>
    /// The label, unit and help of a field and where each comes from: the field's own FieldSpec when its schema gives a
    /// title (and a description), else the v1 counterpart's FieldSpec, else the editor's own words.
    /// </summary>
    public static (string Label, string Unit, string LabelSource, string Help, string HelpSource) Describe(RecordShape shape, FieldRow row)
    {
        FieldSpec? own = shape.Field(row.Field);
        (FieldSpec Spec, string Source)? v1 = own is null ? null : V1Counterpart(shape, row.Field);
        (string Label, string Unit, string Help)? mine = Own.TryGetValue((shape.Location.Pointer, row.Field), out var o) ? o : null;

        string label, unit, labelSource;
        if (own is not null && !string.Equals(own.Title, own.Name, StringComparison.Ordinal))
        {
            (label, unit) = PropertyRow.SplitTitle(own.Title);
            labelSource = "schema";
        }
        else if (v1 is { } c)
        {
            (label, unit) = PropertyRow.SplitTitle(c.Spec.Title);
            labelSource = c.Source;
        }
        else if (mine is { } m)
        {
            (label, unit, labelSource) = (m.Label, m.Unit, "editor");
        }
        else
        {
            (label, unit, labelSource) = (row.Label, row.Unit, "schema");
        }

        string help, helpSource;
        if (own is not null && own.Description.Length > 0) (help, helpSource) = (own.Description, "schema");
        else if (v1 is { } c2 && c2.Spec.Description.Length > 0) (help, helpSource) = (c2.Spec.Description, c2.Source);
        else if (mine is { Help.Length: > 0 } m2) (help, helpSource) = (m2.Help, "editor");
        else (help, helpSource) = (row.Help, row.Help.Length > 0 ? "schema" : "");
        return (label, unit, labelSource, help, helpSource);
    }

    /// <summary>An editor field over <paramref name="row"/> of a record (or block) of shape <paramref name="shape"/>.</summary>
    public EditorField MakeField(FieldRow row, RecordShape shape, string path, int depth)
    {
        (string label, string unit, string labelSource, string help, string helpSource) = Describe(shape, row);
        (IReadOnlyList<EditorChoice> choices, int count, string kind) = Picker(shape, row);
        return new EditorField(row, path, depth, label, unit, labelSource, help, helpSource, choices, count, kind);
    }

    // The picker of a field: a bus reference lists the case's buses, a branch reference its lines and transformers, an
    // asset-class reference its classes (the engine's own reference fields, ReferenceIndex), an enum its values and a
    // boolean true and false. A locked or unknown field has no picker.
    private (IReadOnlyList<EditorChoice> Choices, int Count, string Kind) Picker(RecordShape shape, FieldRow row)
    {
        if (!row.IsEditable) return (Array.Empty<EditorChoice>(), 0, "");
        FieldSpec? f = shape.Field(row.Field);
        if (f is null) return (Array.Empty<EditorChoice>(), 0, "");
        if (ReferenceIndex.RefersToBus(shape, f))
        {
            var all = Index.Buses.Keys.Order().ToList();
            return (Cut(all, id =>
            {
                JsonSourceNode? b = Bus(id, out CaseDocument? d);
                string name = b is null ? "" : RecordRead.Str(b, "name", d!.KeyComparer) is string n ? "  '" + n + "'" : "";
                string kv = b is not null && RecordRead.Num(b, "baseKv", d!.KeyComparer) is double k ? " (" + k.ToString("R", Inv) + " kV)" : "";
                return new EditorChoice(id.ToString(Inv), id.ToString(Inv) + name + kv);
            }), all.Count, "bus");
        }
        if (ReferenceIndex.BranchRefFields.Any(b => b.Shape == shape.Location && b.Field == f.Name))
        {
            var all = Index.Branches.Keys.ToList();
            return (Cut(all, name =>
            {
                string? role = Index.Branches[name] is { Count: > 0 } l ? Session.CollectionAt(l[0].Document, l[0].ArrayPointer)?.Role : null;
                return new EditorChoice(name, name + (role == CaseRoles.Transformers ? "  (transformer)" : role == CaseRoles.Lines ? "  (line)" : ""));
            }), all.Count, "branch");
        }
        if (ReferenceIndex.ClassRefFields.Any(c => c.Shape == shape.Location && c.Field == f.Name))
        {
            var all = Index.Classes.Keys.Order(StringComparer.Ordinal).ToList();
            return (Cut(all, name => new EditorChoice(name, name)), all.Count, "asset class");
        }
        if (f.Kind == FieldKind.Enum)
        {
            var all = f.EnumTexts;
            return (all.Select(t => new EditorChoice(t, t)).ToList(), all.Count, "enum");
        }
        if (f.Kind == FieldKind.Boolean)
            return (new[] { new EditorChoice("true", "true"), new EditorChoice("false", "false") }, 2, "boolean");
        return (Array.Empty<EditorChoice>(), 0, "");
    }

    private static List<EditorChoice> Cut<T>(List<T> all, Func<T, EditorChoice> make)
        => all.Take(FieldGridModel.ChoiceLimit).Select(make).ToList();

    /// <summary>
    /// The fields of a nested block when it is written: an object block (a v2 converter, windings, neutralXPu, a ZIP
    /// model) gives the rows of a field grid over the block's object; an array of records (a boundary's branches) gives
    /// the rows of a grid over each entry. Each such grid edits its own object through CaseOps.SetField (the engine's
    /// nested-object edit). Absent block: nothing (the block's own row takes the whole block as JSON).
    /// </summary>
    public IEnumerable<EditorField> NestedFields(FieldRow parent)
    {
        FieldSpec? f = Shape.Field(parent.Field);
        if (f?.Nested is null || !parent.Present || parent.State == FieldState.Locked) yield break;
        string pointer = Grid.Record.Pointer + "/" + JsonPointerToken(parent.Field);
        JsonSourceNode? node = Doc.Text.Resolve(pointer);
        if (f.Kind == FieldKind.Object && node is { Kind: JsonSourceKind.Object })
        {
            FieldGridModel block = Authoring.FieldGrid(new CaseRef(Grid.Record.Document, pointer, -1, Grid.Record.FileName));
            RecordShape shape = Session.ShapeAt(Doc, pointer)!;
            foreach (FieldRow r in block.Rows)
                yield return MakeField(r, shape, parent.Field + "." + r.Field, 1);
        }
        else if (f.Kind == FieldKind.Array && node is { Kind: JsonSourceKind.Array })
        {
            for (int i = 0; i < node.Elements.Count; i++)
            {
                if (node.Elements[i].Kind != JsonSourceKind.Object) continue;   // the schema refuses it; Check says so
                string item = pointer + "/" + i.ToString(Inv);
                if (Session.ShapeAt(Doc, item) is not RecordShape shape) continue;
                FieldGridModel entry = Authoring.FieldGrid(new CaseRef(Grid.Record.Document, item, -1, Grid.Record.FileName));
                foreach (FieldRow r in entry.Rows)
                    yield return MakeField(r, shape, parent.Field + "[" + i.ToString(Inv) + "]." + r.Field, 1);
            }
        }
    }

    private static string JsonPointerToken(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    // ------------------------------------------------------------------ read-out helpers

    /// <summary>Format a number for a formula text (invariant, round-trip).</summary>
    public static string Show(double d) => d.ToString("R", Inv);
}
