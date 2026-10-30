using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.SchemaEditor.Ui.Views;

/// <summary>
/// A list of records: every record of an array (a whole file, or a nested array such as a boundary's branches), or
/// a subset chosen by a filter function (the buses of one voltage level, the units of one station). Columns are a
/// short per-file selection of fields; Enter opens the record's form.
/// </summary>
internal sealed class RecordListView : View
{
    /// <summary>The columns shown per v1 file: field name, header, width.</summary>
    internal static readonly Dictionary<string, (string Field, string Header, int Width)[]> Columns = new(StringComparer.Ordinal)
    {
        ["buses.json"] = new[] { ("id", "id", 8), ("name", "name", 30), ("baseKv", "kV", 9), ("type", "type", 7), ("vm", "vm", 8) },
        ["generation.json"] = new[] { ("name", "name", 24), ("bus", "bus", 7), ("type", "type", 13), ("capacityMva", "MVA", 10), ("outputMw", "MW", 9), ("capacityFactor", "CF", 6), ("role", "role", 6), ("inertiaH", "H", 5) },
        ["demand.json"] = new[] { ("name", "name", 30), ("bus", "bus", 7), ("pMw", "MW", 11), ("qMvar", "Mvar", 11), ("powerFactor", "pf", 7) },
        ["transmission.json"] = new[] { ("name", "name", 26), ("from", "from", 7), ("to", "to", 7), ("rPu", "r pu", 9), ("xPu", "x pu", 9), ("ratingMva", "MVA", 9), ("circuits", "n", 3) },
        ["transformer.json"] = new[] { ("name", "name", 28), ("from", "from", 7), ("to", "to", 7), ("xPu", "x pu", 9), ("tapRatio", "tap", 8), ("ratingMva", "MVA", 9) },
        ["onloadtapchanger.json"] = new[] { ("transformer", "transformer", 32), ("controlsBus", "bus", 7), ("targetVpu", "target", 8), ("minTap", "min", 7), ("maxTap", "max", 7) },
        ["boundaries.json"] = new[] { ("name", "name", 40), ("limitMw", "limit MW", 10), ("branches", "branches", 9) },
        ["asset-classes.json"] = new[] { ("name", "name", 30), ("kind", "kind", 24), ("mode", "mode", 18) },
        ["asset-bindings.json"] = new[] { ("matchType", "match type", 24), ("class", "class", 32) },
        ["protections.json"] = new[] { ("name", "name", 26), ("class", "class", 22), ("monitorBus", "bus", 7), ("capacityMw", "MW", 9) },
    };

    private readonly int _doc;
    private readonly string _arrayPointer;
    private readonly string _crumb;
    private readonly Func<IReadOnlyList<int>>? _filter;
    private readonly Func<int, AddContext?>? _add;
    private readonly (string Field, string Header, int Width)[] _columns;
    private List<int> _indices = new();

    public RecordListView(CaseSession session, int doc, string arrayPointer, string crumb,
                          Func<IReadOnlyList<int>>? filter = null, Func<int, AddContext?>? add = null) : base(session)
    {
        _doc = doc;
        _arrayPointer = arrayPointer;
        _crumb = crumb;
        _filter = filter;
        _add = add;
        CaseDocument d = session.Documents[doc];
        RecordShape? shape = CaseOps.ItemShape(session, d, arrayPointer);
        _columns = arrayPointer.Length == 0 && d.Spec is not null && Columns.TryGetValue(d.Spec.FileName, out var cols)
            ? cols
            : (shape?.Fields.Take(4).Select(f => (f.Name, f.Name, 20)).ToArray() ?? Array.Empty<(string, string, int)>());
    }

    public override string Crumb => _crumb;

    public override string Header => "#".PadRight(7) + string.Concat(_columns.Select(c => Cell(c.Header, c.Width)));

    public override bool IsGone => _arrayPointer.Length > 0 && !Session.Documents[_doc].Exists;

    protected override void Rebuild()
    {
        CaseDocument d = Session.Documents[_doc];
        JsonSourceNode? array = d.Exists ? d.Text.Resolve(_arrayPointer) : null;
        int n = array is { Kind: JsonSourceKind.Array } ? array.Elements.Count : 0;
        _indices = _filter is null ? Enumerable.Range(0, n).ToList() : _filter().Where(i => i < n).ToList();
    }

    protected override int RowCount() => _indices.Count;

    protected override string RowText(int i)
    {
        int idx = _indices[i];
        CaseDocument d = Session.Documents[_doc];
        JsonSourceNode? rec = d.Text.Resolve(_arrayPointer + "/" + idx.ToString(CultureInfo.InvariantCulture));
        string row = ("#" + idx.ToString(CultureInfo.InvariantCulture)).PadRight(7);
        foreach ((string field, _, int width) in _columns) row += Cell(rec is null ? "" : Show(d, rec, field), width);
        return row;
    }

    /// <summary>A field's value for a list cell: the decoded string, a number as written, [n] for an array, blank when absent.</summary>
    internal static string Show(CaseDocument d, JsonSourceNode rec, string field)
    {
        JsonSourceNode? v = RecordRead.Member(rec, field, d.KeyComparer);
        return v switch
        {
            null => "",
            { Kind: JsonSourceKind.String } => v.StringValue!,
            { Kind: JsonSourceKind.Array } => "[" + v.Elements.Count.ToString(CultureInfo.InvariantCulture) + "]",
            { Kind: JsonSourceKind.Object } => "{...}",
            _ => d.Text.RawText(v),
        };
    }

    public override View? Open(int i)
    {
        int idx = _indices[i];
        string pointer = _arrayPointer + "/" + idx.ToString(CultureInfo.InvariantCulture);
        CaseDocument d = Session.Documents[_doc];
        JsonSourceNode? rec = d.Text.Resolve(pointer);
        string label = rec is null ? "#" + idx.ToString(CultureInfo.InvariantCulture) : RecordRead.Describe(d, rec, idx).Replace(d.FileName + " ", "", StringComparison.Ordinal);
        return new RecordFormView(Session, _doc, pointer, label);
    }

    public override RecordRef? RecordAt(int i) => i >= 0 && i < _indices.Count ? new RecordRef(_doc, _arrayPointer, _indices[i]) : null;

    public override int RowOf(RecordRef record)
    {
        Refresh();
        if (record.Document != _doc || !string.Equals(record.ArrayPointer, _arrayPointer, StringComparison.Ordinal)) return -1;
        return _indices.IndexOf(record.Index);
    }

    public override AddContext? AddHere(int row)
    {
        if (_add is not null) return _add(row);
        int at = row >= 0 && row < _indices.Count ? _indices[row] + 1 : (_indices.Count == 0 ? 0 : _indices[^1] + 1);
        return new AddContext(_doc, _arrayPointer, at, Array.Empty<KeyValuePair<string, string>>());
    }
}
