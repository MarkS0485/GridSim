using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.SchemaEditor.Ui.Views;

/// <summary>
/// The top of a v2 case (SCHEMA-EDITOR-PLAN.md section 6, v2 tree): the "Case" form for the manifest (when a manifest
/// was opened), then the network sections merged across every listed network file, then the list of network files.
/// A '*' marks a section with unsaved changes in any of its files.
/// </summary>
internal sealed class V2RootView : View
{
    private readonly List<(string Label, string? Role, int Kind)> _rows = new();   // Kind: 0 manifest, 1 section, 2 files

    public V2RootView(CaseSession session) : base(session) { }

    public override string Crumb => "Case";

    public override string Header => "  " + Cell("section", 22) + Cell("records", 10) + "files";

    protected override void Rebuild()
    {
        _rows.Clear();
        if (Session.Format == CaseFormat.V2Case) _rows.Add(("Case (manifest)", null, 0));
        foreach (string role in CaseRoles.V2Network) _rows.Add((CaseRoles.Title(role), role, 1));
        _rows.Add(("Network files", null, 2));
    }

    protected override int RowCount() => _rows.Count;

    private IEnumerable<CaseDocument> Networks => Session.Documents.Where(d => d.SchemaFile == CaseSession.V2NetworkSchema);

    protected override string RowText(int i)
    {
        (string label, string? role, int kind) = _rows[i];
        switch (kind)
        {
            case 0:
            {
                CaseDocument m = Session.Documents[0];
                return (m.IsDirty ? "* " : "  ") + Cell(label, 22) + Cell("1", 10) + m.FileName;
            }
            case 1:
            {
                var colls = Session.CollectionsOf(role!).ToList();
                int count = colls.Sum(c => Session.ArrayOf(c)?.Elements.Count ?? 0);
                bool dirty = colls.Any(c => Session.Documents[c.Document].IsDirty);
                return (dirty ? "* " : "  ") + Cell(label, 22) + Cell(count.ToString(CultureInfo.InvariantCulture), 10)
                     + string.Join(", ", colls.Where(c => Session.ArrayOf(c) is not null).Select(c => Session.Documents[c.Document].FileName).Distinct());
            }
            default:
                return "  " + Cell(label, 22) + Cell(Networks.Count().ToString(CultureInfo.InvariantCulture), 10) + "the network files the manifest lists";
        }
    }

    public override View? Open(int i)
    {
        (string label, string? role, int kind) = _rows[i];
        return kind switch
        {
            0 => new RecordFormView(Session, 0, "", "Case"),
            1 => new MergedListView(Session, role!),
            _ => TextListView.Plain(Session, label, "file",
                Networks.Select(d => d.FileName + (d.Exists ? "" : "  (missing)") + (d.IsDirty ? "  (modified)" : "")).ToList()),
        };
    }

    public override AddContext? AddHere(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Role is not string role) return null;
        RecordCollection? c = Session.CollectionsOf(role).FirstOrDefault();
        return c is null ? null : new AddContext(c.Document, c.ArrayPointer, Session.ArrayOf(c)?.Elements.Count ?? 0, Array.Empty<KeyValuePair<string, string>>());
    }
}

/// <summary>
/// One network section of a v2 case merged across every listed network file (buses, generators, ...), with a File
/// column when the case has more than one network file. Enter opens a record's form; Add puts a new record in the
/// file of the highlighted row, just after it.
/// </summary>
internal sealed class MergedListView : View
{
    private readonly string _role;
    private readonly (string Field, string Header, int Width)[] _columns;
    private readonly List<(RecordCollection Coll, int Index)> _rows = new();

    public MergedListView(CaseSession session, string role) : base(session)
    {
        _role = role;
        _columns = RecordListView.Columns.TryGetValue(CaseRoles.V1File(role), out var cols) ? cols : Array.Empty<(string, string, int)>();
    }

    private bool ManyFiles => Session.Documents.Count(d => d.SchemaFile == CaseSession.V2NetworkSchema) > 1;

    public override string Crumb => CaseRoles.Title(_role);

    public override string Header => "#".PadRight(7) + (ManyFiles ? Cell("file", 22) : "") + string.Concat(_columns.Select(c => Cell(c.Header, c.Width)));

    protected override void Rebuild()
    {
        _rows.Clear();
        foreach (RecordCollection c in Session.CollectionsOf(_role))
            if (Session.ArrayOf(c) is JsonSourceNode a)
                for (int i = 0; i < a.Elements.Count; i++) _rows.Add((c, i));
    }

    protected override int RowCount() => _rows.Count;

    protected override string RowText(int i)
    {
        (RecordCollection c, int idx) = _rows[i];
        CaseDocument d = Session.Documents[c.Document];
        JsonSourceNode rec = Session.ArrayOf(c)!.Elements[idx];
        string row = ("#" + idx.ToString(CultureInfo.InvariantCulture)).PadRight(7) + (ManyFiles ? Cell(d.FileName, 22) : "");
        foreach ((string field, _, int width) in _columns) row += Cell(RecordListView.Show(d, rec, field), width);
        return row;
    }

    public override View? Open(int i)
    {
        (RecordCollection c, int idx) = _rows[i];
        CaseDocument d = Session.Documents[c.Document];
        string pointer = c.ArrayPointer + "/" + idx.ToString(CultureInfo.InvariantCulture);
        return new RecordFormView(Session, c.Document, pointer, RecordRead.Describe(d, d.Text.Resolve(pointer)!, idx, c.ArrayPointer));
    }

    public override RecordRef? RecordAt(int i)
    {
        Refresh();
        return i >= 0 && i < _rows.Count ? new RecordRef(_rows[i].Coll.Document, _rows[i].Coll.ArrayPointer, _rows[i].Index) : null;
    }

    public override int RowOf(RecordRef record)
    {
        Refresh();
        return _rows.FindIndex(r => r.Coll.Document == record.Document && r.Coll.ArrayPointer == record.ArrayPointer && r.Index == record.Index);
    }

    public override AddContext? AddHere(int row)
    {
        Refresh();
        if (row >= 0 && row < _rows.Count)
            return new AddContext(_rows[row].Coll.Document, _rows[row].Coll.ArrayPointer, _rows[row].Index + 1, Array.Empty<KeyValuePair<string, string>>());
        RecordCollection? c = Session.CollectionsOf(_role).FirstOrDefault();
        return c is null ? null : new AddContext(c.Document, c.ArrayPointer, Session.ArrayOf(c)?.Elements.Count ?? 0, Array.Empty<KeyValuePair<string, string>>());
    }
}
