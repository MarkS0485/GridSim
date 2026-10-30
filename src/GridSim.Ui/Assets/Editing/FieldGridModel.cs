using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Dashboard;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1 (ASSETS plan section 3.3: "FieldGridModel built from RecordShape/FieldSpec (label, raw literal,
// absent/default/locked state, enum choices, unit, help text)"; plan Phase 4: "The property grid is generated from
// FieldCatalogue/FieldSpec"). The PROPERTIES sub-tab in Author mode: one row per field of the record's schema shape, in
// schema order, then any member the schema does not list. Nothing about a field is hard-coded here: the label, unit, help
// text, kind, requiredness, enum values and what absence means all come from the engine's FieldSpec (the schema, plus the
// loader DTO's default). An edit is typed text, handed to the engine's CaseOps.SetField, which parses it strictly and
// refuses an invalid literal with the reason; a reset is CaseOps.ResetField. Each is one transaction and one undo step.
// The only rule added here is the locked-field list (LockedFields): a field the v2 loader refuses is shown LOCKED with
// the loader's reason and an edit of it is refused before the engine is asked (a reset to absent is still allowed).
// Phase 5b (2026-10-20): an ASSET OVERLAY record edits in this same grid. Its rows come from its overlay kind spec through
// the engine's kind spec -> FieldSpec adapter (OverlayOps.Fields): each parameter with its type, unit, range, enum values
// and requiredness; each reference role as a picker over the case's elements of its target type; the parent as a picker
// over the records of the kind's parentKinds; the kind itself locked. Its edits are OverlayOps operations (one undo step
// each, the kind spec deciding what is valid); its id, note and provenance are edited as any record field.

/// <summary>The state of one field of the grid.</summary>
public enum FieldState
{
    /// <summary>The member is written in the record (its literal is shown exactly as written).</summary>
    Set,

    /// <summary>The member is absent: the record takes the default (or the derived value) the row says.</summary>
    Absent,

    /// <summary>The v2 loader refuses this field (see <see cref="LockedFields"/>); the row says why. An edit is refused.</summary>
    Locked,

    /// <summary>A member the schema does not list (a loaded case has none: the loader refuses unknown members).</summary>
    Unknown,
}

/// <summary>
/// One row of the field grid: what the field is (from the schema), what is written, and the text being typed. Editing the
/// text changes nothing until <see cref="Commit"/> hands it to the engine.
/// </summary>
public sealed class FieldRow : ObservableModel
{
    private readonly FieldGridModel _grid;
    private string _editText;

    internal FieldRow(FieldGridModel grid, string field, string label, string unit, string help, string kind, bool required,
        FieldState state, string literal, string absentText, string? defaultLiteral, string? lockReason, IReadOnlyList<string> enumChoices,
        string? writtenNote = null)
    {
        WrittenNote = writtenNote;
        _grid = grid;
        Field = field;
        Label = label;
        Unit = unit;
        Help = help;
        Kind = kind;
        Required = required;
        State = state;
        Literal = literal;
        AbsentText = absentText;
        DefaultLiteral = defaultLiteral;
        LockReason = lockReason;
        EnumChoices = enumChoices;
        _editText = literal;
    }

    /// <summary>The member name (canonical spelling).</summary>
    public string Field { get; }

    /// <summary>The label (the schema title without its unit).</summary>
    public string Label { get; }

    /// <summary>The unit (from the schema title's trailing bracket), or "".</summary>
    public string Unit { get; }

    /// <summary>The help line (the schema description).</summary>
    public string Help { get; }

    /// <summary>How the field is edited (Number, Integer, Boolean, String, Enum, Object, Array, Map, Raw, Unknown).</summary>
    public string Kind { get; }

    /// <summary>True when the schema requires the member (it cannot be reset to absent).</summary>
    public bool Required { get; }

    /// <summary>Set, absent, locked or unknown.</summary>
    public FieldState State { get; }

    /// <summary>True when the member is written in the record (whatever else the row's state says).</summary>
    public bool Present => Literal.Length > 0;

    /// <summary>The member's value exactly as written (raw JSON text), or "" when absent.</summary>
    public string Literal { get; }

    /// <summary>What an absent field means (the loader DTO's default literal, or "derived: ..." from the schema).</summary>
    public string AbsentText { get; }

    /// <summary>The DTO default as a JSON literal when it is a plain value, else null.</summary>
    public string? DefaultLiteral { get; }

    /// <summary>Why the field is locked (the loader's reason), or null when it is not.</summary>
    public string? LockReason { get; }

    /// <summary>True when the field is locked.</summary>
    public bool IsLocked => LockReason is not null;

    /// <summary>The allowed values of an enum field, as text (empty for every other kind).</summary>
    public IReadOnlyList<string> EnumChoices { get; }

    /// <summary>True for an enum field (the row lists its choices).</summary>
    public bool HasChoices => EnumChoices.Count > 0;

    /// <summary>The choices as one line ("one of: pq, pv, slack"), or "".</summary>
    public string ChoicesText => EnumChoices.Count == 0 ? "" : "one of: " + string.Join(", ", EnumChoices);

    /// <summary>True when the row takes typed text (a schema field that is not locked).</summary>
    public bool IsEditable => State is FieldState.Set or FieldState.Absent;

    /// <summary>True when the row can be reset to absent (written, not required, a schema field; locked fields too).</summary>
    public bool CanReset => Present && !Required && State != FieldState.Unknown;

    /// <summary>Phase 5b: a note on how the value is written (an overlay parameter in the object form keeps its unit and
    /// provenance; a reference written in another shape is rewritten when set), or null (every model record field).</summary>
    public string? WrittenNote { get; }

    /// <summary>The state column: "as written", "absent: default 1", "LOCKED: ...", "not a field of this schema".</summary>
    public string StateText => State switch
    {
        FieldState.Set => (Required ? "as written (required)" : "as written") + (WrittenNote is null ? "" : "; " + WrittenNote),
        FieldState.Absent => "absent: " + AbsentText,
        FieldState.Locked => (Present ? "as written, " : "absent, ") + LockReason,
        _ => "not a field of this record's schema",
    };

    /// <summary>The text being typed (starts as the literal; nothing changes until <see cref="Commit"/>).</summary>
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value ?? "");
    }

    /// <summary>Hand <see cref="EditText"/> to the engine as this field's new value (one undo step; refused with the
    /// reason when the text is not a valid value, the field is locked, or the edit would break the record).</summary>
    public AuthoringResult Commit() => _grid.Edit(Field, _editText);

    /// <summary>Reset the field to absent (refused for a required field, and for a key that is referred to).</summary>
    public AuthoringResult Reset() => _grid.Reset(Field);
}

/// <summary>
/// The field grid of one record in Author mode (UI programme Phase 4.1; PROPERTIES): the record's schema fields with
/// their literals, states, units, help and choices. Edits and resets go through the engine (CaseOps) as transactions.
/// A grid shows the record as it was when the grid was made; the Author model makes a new one after every change.
/// </summary>
public sealed class FieldGridModel
{
    private readonly CaseAuthoring _authoring;

    internal FieldGridModel(CaseAuthoring authoring, CaseRef record)
    {
        _authoring = authoring;
        Record = record;
        CaseSession s = authoring.Session;
        if (record.Document < 0 || record.Document >= s.Documents.Count)
            throw new ArgumentException($"{record.Display}: no such document in the case.", nameof(record));
        CaseDocument doc = s.Documents[record.Document];
        // The record's object, or null for a v1 system.json that does not exist yet (every field then reads absent).
        JsonSourceNode? node = doc.Exists ? doc.Text.Resolve(record.Pointer) : null;
        if (node is not null && node.Kind != JsonSourceKind.Object)
            throw new ArgumentException($"{record.Display}: the value at {record.Pointer} is not a record.", nameof(record));
        if (node is null && !(record.Index < 0 && doc.Spec is { IsArray: false }))
            throw new ArgumentException($"{record.Display}: there is no record at {record.Pointer}.", nameof(record));
        RecordShape shape = s.ShapeAt(doc, record.Pointer)
            ?? throw new ArgumentException($"{record.Display}: no schema describes the record at {record.Pointer}.", nameof(record));
        ShapeTitle = shape.Title;
        ShapeDescription = shape.Description;
        SchemaFile = shape.Location.File;
        ShapePointer = shape.Location.Pointer;
        Heading = authoring.Describe(record);

        // Phase 5b: an asset overlay record is read against its overlay KIND spec, not only its schema shape: the rows are
        // the kind spec -> FieldSpec adapter's (OverlayOps.Fields): id, kind (locked), parent (a picker over the records
        // of the kind's parentKinds), each declared reference role (a picker over the case's elements of its target type),
        // each declared parameter (type, unit, range, enum values, required, from the kind spec), then note and prov.
        if (OverlayOps.IsOverlayRecord(s, record.ToRecordRef()))
        {
            IsOverlay = true;
            string kind = OverlayOps.KindAt(s, record.ToRecordRef()) ?? "";
            ShapeTitle = "Asset overlay record: " + kind + (OverlayOps.Kinds.Parameters.Find(kind) is { } ks ? " (" + ks.Title + ")" : "");
            ShapeDescription = OverlayOps.Kinds.Parameters.Find(kind)?.Description ?? shape.Description;
            IReadOnlyList<OverlayField> fields = OverlayOps.Fields(s, record.ToRecordRef());
            Rows = fields.Select(f =>
            {
                (string label, string unit) = PropertyRow.SplitTitle(f.Spec.Title);
                FieldState state = f.LockReason is not null ? FieldState.Locked : f.Present ? FieldState.Set : FieldState.Absent;
                // A picker lists the choices; an enum parameter's are its values, a reference's or the parent's the case's
                // elements (only the first ChoiceLimit are listed in full: a big case has thousands of buses).
                IReadOnlyList<string> choices = f.Choices.Count <= ChoiceLimit ? f.Choices : f.Choices.Take(ChoiceLimit).ToList();
                string help = f.Spec.Description + (f.Choices.Count > ChoiceLimit
                    ? " (" + f.Choices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " choices; the first " + ChoiceLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " are listed)" : "");
                return new FieldRow(this, f.Field, label, unit, help, OverlayKindText(f), f.Spec.Required, state, f.Literal,
                    f.Spec.AbsentText, null, f.LockReason, choices, f.Note);
            }).ToList();
            // (A member written twice, which the overlay checker reports, gives two rows with one key: the first counts.)
            AllChoices = fields.GroupBy(f => f.Field, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Choices, StringComparer.Ordinal);
            return;
        }

        var rows = new List<FieldRow>(shape.Fields.Count);
        var matched = new HashSet<JsonSourceMember>();
        foreach (FieldSpec f in shape.Fields)
        {
            JsonSourceMember? m = node?.FindMember(f.Name, doc.KeyComparer);
            if (m is not null) matched.Add(m);
            (string label, string unit) = PropertyRow.SplitTitle(f.Title);
            LockedField? locked = LockedFields.Find(shape.Location.File, shape.Location.Pointer, f.Name);
            string literal = m is null ? "" : doc.Text.RawText(m.Value);
            FieldState state = locked is not null ? FieldState.Locked : m is null ? FieldState.Absent : FieldState.Set;
            rows.Add(new FieldRow(this, f.Name, label, unit, f.Description, f.Kind.ToString(), f.Required, state, literal,
                f.AbsentText, f.DefaultLiteral, locked?.Reason, f.EnumTexts));
        }
        // A member the schema does not list is still shown (read-only: there is no field spec to type it against).
        if (node is not null)
            foreach (JsonSourceMember m in node.Members)
                if (!matched.Contains(m))
                    rows.Add(new FieldRow(this, m.Name, m.Name, "", "Not a field of this record's schema.", "Unknown", false,
                        FieldState.Unknown, doc.Text.RawText(m.Value), "none", null, null, Array.Empty<string>()));
        Rows = rows;
    }

    // UI programme Phase 4.4 (2026-10-21): a grid whose rows are NOT one record's schema fields but the parameters of an
    // inverter template, built by the template editor from the template's KIND SPEC (TemplateParamGrid). The row
    // mechanics are this class's own (FieldRow: label, literal, state, unit, help, choices, Commit, Reset); only where an
    // edit goes differs, so the two delegates below take the place of CaseOps.SetField / ResetField. They are null for
    // every grid made by the constructor above, so that grid edits exactly as before.
    private readonly Func<string, string, AuthoringResult>? _customEdit;
    private readonly Func<string, AuthoringResult>? _customReset;

    /// <summary>
    /// Phase 4.4: a grid over rows built by the caller (the template editor's kind-spec parameter rows), whose edits and
    /// resets go to <paramref name="edit"/> and <paramref name="reset"/> (each one engine transaction, or refused with the
    /// reason). <paramref name="rows"/> is called once with this grid, so each row commits through it.
    /// </summary>
    internal FieldGridModel(CaseAuthoring authoring, CaseRef record, string heading, string shapeTitle, string shapeDescription,
        string schemaFile, string shapePointer, Func<FieldGridModel, IReadOnlyList<FieldRow>> rows,
        Func<string, string, AuthoringResult> edit, Func<string, AuthoringResult> reset)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Heading = heading ?? throw new ArgumentNullException(nameof(heading));
        ShapeTitle = shapeTitle ?? throw new ArgumentNullException(nameof(shapeTitle));
        ShapeDescription = shapeDescription ?? throw new ArgumentNullException(nameof(shapeDescription));
        SchemaFile = schemaFile ?? throw new ArgumentNullException(nameof(schemaFile));
        ShapePointer = shapePointer ?? throw new ArgumentNullException(nameof(shapePointer));
        _customEdit = edit ?? throw new ArgumentNullException(nameof(edit));
        _customReset = reset ?? throw new ArgumentNullException(nameof(reset));
        Rows = rows(this);
    }

    /// <summary>The record the grid shows.</summary>
    public CaseRef Record { get; }

    /// <summary>Phase 5b: how many choices a row lists in full (a reference to a bus of a big case has thousands).</summary>
    public const int ChoiceLimit = 200;

    /// <summary>Phase 5b: true when the record is an asset overlay record (its rows come from its kind spec).</summary>
    public bool IsOverlay { get; }

    /// <summary>Phase 5b: every choice of each overlay row by field key (a row lists at most <see cref="ChoiceLimit"/>);
    /// empty for a model record.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllChoices { get; } = new Dictionary<string, IReadOnlyList<string>>();

    // The kind column of an overlay row: the parameter's type, or what a reference or the parent points at.
    private static string OverlayKindText(OverlayField f) => f.Part switch
    {
        OverlayFieldPart.Ref => "Reference",
        OverlayFieldPart.Parent => "Parent",
        _ => f.Spec.Kind.ToString(),
    };

    /// <summary>The record as messages name it ("network.json lines #3 'L1-2'").</summary>
    public string Heading { get; }

    /// <summary>The schema title of the record ("Line").</summary>
    public string ShapeTitle { get; }

    /// <summary>The schema description of the record.</summary>
    public string ShapeDescription { get; }

    /// <summary>The schema file of the record's shape.</summary>
    public string SchemaFile { get; }

    /// <summary>The JSON pointer of the record's shape inside <see cref="SchemaFile"/>.</summary>
    public string ShapePointer { get; }

    /// <summary>One row per schema field (schema order), then any member the schema does not list.</summary>
    public IReadOnlyList<FieldRow> Rows { get; }

    /// <summary>The row of <paramref name="field"/> (throws for a field the record has no row for).</summary>
    public FieldRow Row(string field) => Rows.FirstOrDefault(r => string.Equals(r.Field, field, StringComparison.Ordinal))
        ?? throw new ArgumentException($"{Heading} has no field '{field}'.", nameof(field));

    /// <summary>
    /// Set <paramref name="field"/> from typed text: refused (with the loader's reason) for a locked field and for a
    /// member the schema does not list; otherwise the engine's SetField (strict parse, integrity, schema of the record),
    /// one undo step.
    /// </summary>
    public AuthoringResult Edit(string field, string typed)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(typed);
        FieldRow row = Row(field);
        if (row.State == FieldState.Locked) return AuthoringResult.Refused(row.Label + ": refused. " + row.LockReason);
        if (row.State == FieldState.Unknown) return AuthoringResult.Refused(row.Label + ": refused, not a field of this record's schema (it can only be removed by editing the file).");
        if (_customEdit is not null) return _customEdit(field, typed);   // Phase 4.4: a template parameter grid
        if (IsOverlay)
        {
            // Phase 5b: parameters, references and the parent by the kind spec (OverlayOps); id, note and prov as any
            // record field (CaseOps.SetField: the id is the key, so with referrers it changes only by Rename).
            RecordRef r = Record.ToRecordRef();
            if (field.StartsWith(OverlayOps.ParamPrefix, StringComparison.Ordinal))
                return _authoring.Apply(s => OverlayOps.SetParam(s, r, field[OverlayOps.ParamPrefix.Length..], typed));
            if (field.StartsWith(OverlayOps.RefPrefix, StringComparison.Ordinal))
                return _authoring.Apply(s => OverlayOps.SetRef(s, r, field[OverlayOps.RefPrefix.Length..], typed));
            if (field == "parent") return _authoring.Apply(s => OverlayOps.SetParent(s, r, typed));
        }
        return _authoring.Apply(s => CaseOps.SetField(s, Record.Document, Record.Pointer, field, typed));
    }

    /// <summary>Reset <paramref name="field"/> to absent (the engine's ResetField; allowed for a locked field, which is
    /// how a case that sets one is put right).</summary>
    public AuthoringResult Reset(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        FieldRow row = Row(field);
        if (row.State == FieldState.Unknown) return AuthoringResult.Refused(row.Label + ": refused, not a field of this record's schema.");
        if (_customReset is not null) return _customReset(field);   // Phase 4.4: a template parameter grid
        if (IsOverlay)
        {
            RecordRef r = Record.ToRecordRef();
            if (field == "kind") return AuthoringResult.Refused(row.Label + ": refused. " + row.LockReason);
            if (field.StartsWith(OverlayOps.ParamPrefix, StringComparison.Ordinal))
                return _authoring.Apply(s => OverlayOps.ResetParam(s, r, field[OverlayOps.ParamPrefix.Length..]));
            if (field.StartsWith(OverlayOps.RefPrefix, StringComparison.Ordinal))
                return _authoring.Apply(s => OverlayOps.ResetRef(s, r, field[OverlayOps.RefPrefix.Length..]));
            if (field == "parent") return _authoring.Apply(s => OverlayOps.ResetParent(s, r));
        }
        return _authoring.Apply(s => CaseOps.ResetField(s, Record.Document, Record.Pointer, field));
    }
}
