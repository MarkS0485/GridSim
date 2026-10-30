using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.SchemaEditor.Ui.Views;

/// <summary>
/// A schema-driven form for one object (a record, or a nested object such as a zip block or one boundary branch):
/// one row per field in schema order, showing the label (the schema title, which carries the unit), the raw literal
/// as stored, and a state. An absent field shows "(absent: 1)" or "(absent: derived ...)". Objects show {...}, arrays
/// [n]; Enter descends into them. Members the schema does not list (only possible in a file that already fails its
/// schema) are shown at the end, read-only, so nothing in the file is hidden.
/// </summary>
internal sealed class RecordFormView : View
{
    private const int LabelWidth = 38;
    private const int ValueWidth = 30;

    private readonly int _doc;
    private readonly string _pointer;
    private readonly string _crumb;
    private RecordShape? _shape;
    private readonly List<(FieldSpec? Spec, string Name)> _rows = new();

    public RecordFormView(CaseSession session, int doc, string objectPointer, string crumb) : base(session)
    {
        _doc = doc;
        _pointer = objectPointer;
        _crumb = crumb;
    }

    /// <summary>The document this form edits.</summary>
    public int Document => _doc;

    /// <summary>The JSON pointer of the object this form edits.</summary>
    public string ObjectPointer => _pointer;

    public override string Crumb => _crumb;

    public override string Header => Cell("field", LabelWidth) + Cell("value", ValueWidth) + "state";

    private JsonSourceNode? Target()
    {
        CaseDocument d = Session.Documents[_doc];
        if (!d.Exists) return null;
        return d.Text.Resolve(_pointer);
    }

    public override bool IsGone
    {
        get
        {
            CaseDocument d = Session.Documents[_doc];
            // system.json may not exist yet: its form is still meaningful (editing creates the file).
            if (!d.Exists) return d.Spec is not { IsArray: false };
            return Target() is not { Kind: JsonSourceKind.Object };
        }
    }

    protected override void Rebuild()
    {
        _rows.Clear();
        CaseDocument d = Session.Documents[_doc];
        _shape = d.Exists || d.Spec is { IsArray: false } ? Session.ShapeAt(d, _pointer) : null;
        if (_shape is null) return;
        foreach (FieldSpec f in _shape.Fields) _rows.Add((f, f.Name));
        if (Target() is { Kind: JsonSourceKind.Object } obj)
            foreach (JsonSourceMember m in obj.Members)
                if (_shape.FieldIgnoringCase(m.Name) is null) _rows.Add((null, m.Name));
    }

    protected override int RowCount() => _rows.Count;

    protected override string RowText(int i)
    {
        (FieldSpec? f, string name) = _rows[i];
        CaseDocument d = Session.Documents[_doc];
        JsonSourceNode? obj = Target();
        JsonSourceMember? m = obj is { Kind: JsonSourceKind.Object } ? obj.FindMember(name, d.KeyComparer) : null;
        string label = f?.Title ?? name;
        string value;
        // A derived absence ("derived: capacityMva x capacityFactor ...") is too long for the cell; the help line gives it in full.
        if (m is null) value = "(absent: " + (f is null ? "?" : f.AbsentText.StartsWith("derived: ", StringComparison.Ordinal) ? "derived, see help" : f.AbsentText) + ")";
        else value = m.Value.Kind switch
        {
            JsonSourceKind.Object => "{" + m.Value.Members.Count.ToString(CultureInfo.InvariantCulture) + "}",
            JsonSourceKind.Array => "[" + m.Value.Elements.Count.ToString(CultureInfo.InvariantCulture) + "]",
            _ => d.Text.RawText(m.Value),
        };
        string state = f is null ? "not in the schema (read-only)"
            : m is not null && !string.Equals(m.Name, f.Name, StringComparison.Ordinal) ? $"spelt '{m.Name}' (L normalises)"
            : f.Required ? "required" : "";
        return Cell(label, LabelWidth) + Cell(value, ValueWidth) + state;
    }

    public override FormField? FieldAt(int i)
    {
        Refresh();
        return i >= 0 && i < _rows.Count && _rows[i].Spec is FieldSpec f ? new FormField(_doc, _pointer, f) : null;
    }

    public override string? HelpLine(int i)
    {
        Refresh();
        if (i < 0 || i >= _rows.Count) return null;
        (FieldSpec? f, string name) = _rows[i];
        if (f is null) return $"'{name}' is not a member the schema knows; the file fails its schema until it is removed or renamed.";
        return f.Name + ": " + f.Description;
    }

    public override View? Open(int i)
    {
        Refresh();
        if (i < 0 || i >= _rows.Count || _rows[i].Spec is not FieldSpec f) return null;
        CaseDocument d = Session.Documents[_doc];
        JsonSourceNode? obj = Target();
        JsonSourceMember? m = obj is { Kind: JsonSourceKind.Object } ? obj.FindMember(f.Name, d.KeyComparer) : null;
        if (m is null) return null;
        string child = _pointer + "/" + m.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        return f.Kind switch
        {
            FieldKind.Object when m.Value.Kind == JsonSourceKind.Object => new RecordFormView(Session, _doc, child, f.Title),
            FieldKind.Array when m.Value.Kind == JsonSourceKind.Array => new RecordListView(Session, _doc, child, f.Title),
            FieldKind.Map when m.Value.Kind == JsonSourceKind.Object => new MapView(Session, _doc, child, f.Title),
            _ => null,
        };
    }

    /// <summary>True when Enter on this row descends (a present object, array or map) rather than editing.</summary>
    public bool Descends(int i) => Open(i) is not null;
}

/// <summary>
/// The entries of a map field (an asset class's params or provenance), one per row, read-only; a map is edited as a
/// whole from its form row with F2 (raw JSON), which keeps the editor honest about entries it has no schema for.
/// </summary>
internal sealed class MapView : View
{
    private readonly int _doc;
    private readonly string _pointer;
    private readonly string _crumb;

    public MapView(CaseSession session, int doc, string pointer, string crumb) : base(session)
    {
        _doc = doc;
        _pointer = pointer;
        _crumb = crumb;
    }

    public override string Crumb => _crumb;

    public override string Header => Cell("key", 32) + "value";

    private JsonSourceNode? Map() => Session.Documents[_doc].Exists ? Session.Documents[_doc].Text.Resolve(_pointer) : null;

    public override bool IsGone => Map() is not { Kind: JsonSourceKind.Object };

    protected override int RowCount() => Map() is { Kind: JsonSourceKind.Object } m ? m.Members.Count : 0;

    protected override string RowText(int i)
    {
        JsonSourceMember e = Map()!.Members[i];
        return Cell(e.Name, 32) + Session.Documents[_doc].Text.RawText(e.Value);
    }
}
