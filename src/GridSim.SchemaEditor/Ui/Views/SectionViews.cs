using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.SchemaEditor.Ui.Views;

/// <summary>
/// The top of the v1 tree: one row per section (System, Buses, Generation, Demand, Lines, Transformers, Tap changers,
/// Boundaries, Asset classes, Asset bindings, Protections) plus "Not part of the case" (the files the loader does not
/// read, listed and never written). A '*' marks a section whose file has unsaved changes.
/// </summary>
internal sealed class RootView : View
{
    private readonly List<(string Label, CaseDocument? Doc)> _rows = new();

    public RootView(CaseSession session) : base(session) { }

    public override string Crumb => "Case";

    public override string Header => "  " + Cell("section", 22) + Cell("records", 10) + "file";

    protected override void Rebuild()
    {
        _rows.Clear();
        foreach (CaseDocument d in Session.Documents.Where(x => x.Spec is not null))
            _rows.Add((d.Spec!.Section, d));
        _rows.Add(("Not part of the case", null));
    }

    protected override int RowCount() => _rows.Count;

    protected override string RowText(int i)
    {
        (string label, CaseDocument? d) = _rows[i];
        if (d is null)
            return "  " + Cell(label, 22) + Cell(Session.OtherFiles.Count.ToString(CultureInfo.InvariantCulture), 10) + "files the loader does not read (never written)";
        string count = !d.Exists ? "-" : d.Text.Root.Kind == JsonSourceKind.Array ? d.Text.Root.Elements.Count.ToString(CultureInfo.InvariantCulture) : "1";
        return (d.IsDirty ? "* " : "  ") + Cell(label, 22) + Cell(count, 10) + d.FileName + (d.Exists ? "" : " (absent)");
    }

    public override View? Open(int i)
    {
        (string label, CaseDocument? d) = _rows[i];
        if (d is null) return TextListView.Plain(Session, label, "file", Session.OtherFiles.Concat(Session.LeftoverFiles.Select(f => f + "  (left by an interrupted save)")).ToList());
        return d.Spec!.FileName switch
        {
            "system.json" => new RecordFormView(Session, d.Id, "", label),
            "buses.json" => new BusVoltageView(Session),
            "generation.json" => new FuelTypeView(Session),
            _ => new RecordListView(Session, d.Id, "", label),
        };
    }

    public override AddContext? AddHere(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Doc is not { Spec.IsArray: true } d) return null;
        int at = d.Exists && d.Text.Root.Kind == JsonSourceKind.Array ? d.Text.Root.Elements.Count : 0;
        return new AddContext(d.Id, "", at, Array.Empty<KeyValuePair<string, string>>());
    }
}

/// <summary>Buses grouped by voltage level, highest first (absent baseKv last); Enter lists the buses of a level.</summary>
internal sealed class BusVoltageView : View
{
    private readonly List<(double? Kv, List<int> Buses)> _groups = new();

    public BusVoltageView(CaseSession session) : base(session) { }

    private CaseDocument Buses => Session.Doc("buses.json");

    public override string Crumb => "Buses";

    public override string Header => Cell("voltage level", 22) + "buses";

    protected override void Rebuild()
    {
        _groups.Clear();
        _groups.AddRange(Group(Session));
    }

    internal static List<(double? Kv, List<int> Buses)> Group(CaseSession s)
    {
        CaseDocument d = s.Doc("buses.json");
        var map = new Dictionary<long, (double? Kv, List<int> Buses)>();
        var none = new List<int>();
        if (d.Exists && d.Text.Root.Kind == JsonSourceKind.Array)
            for (int i = 0; i < d.Text.Root.Elements.Count; i++)
            {
                double? kv = RecordRead.Num(d.Text.Root.Elements[i], "baseKv", d.KeyComparer);
                if (kv is not double k) { none.Add(i); continue; }
                long key = BitConverter.DoubleToInt64Bits(k);
                if (!map.TryGetValue(key, out var g)) map[key] = g = (k, new List<int>());
                g.Buses.Add(i);
            }
        var groups = map.Values.OrderByDescending(g => g.Kv!.Value).ToList();
        if (none.Count > 0) groups.Add((null, none));
        return groups;
    }

    protected override int RowCount() => _groups.Count;

    protected override string RowText(int i)
    {
        (double? kv, List<int> buses) = _groups[i];
        string label = kv is double k ? FieldCatalogue.Show(k) + " kV" : "no baseKv";
        return Cell(label, 22) + buses.Count.ToString(CultureInfo.InvariantCulture);
    }

    public override View? Open(int i)
    {
        double? kv = _groups[i].Kv;
        string crumb = kv is double k ? FieldCatalogue.Show(k) + " kV" : "no baseKv";
        return new RecordListView(Session, Buses.Id, "", crumb,
            filter: () => Group(Session).FirstOrDefault(g => Nullable.Equals(g.Kv, kv)).Buses ?? new List<int>(),
            add: row => new AddContext(Buses.Id, "", Buses.Exists ? Buses.Text.Root.Elements.Count : 0,
                kv is double v ? new[] { new KeyValuePair<string, string>("baseKv", FieldCatalogue.Show(v)) } : Array.Empty<KeyValuePair<string, string>>()));
    }
}

/// <summary>
/// Generation by fuel type (ordinal by effective type; an absent type is ccgt, marked '*'), with station count, unit
/// count and capacity sum (display only). Enter lists the stations of a type.
/// </summary>
internal sealed class FuelTypeView : View
{
    private IReadOnlyList<FuelGroup> _groups = Array.Empty<FuelGroup>();

    public FuelTypeView(CaseSession session) : base(session) { }

    public override string Crumb => "Generation";

    public override string Header => Cell("fuel type", 22) + Cell("stations", 10) + Cell("units", 8) + "capacity MVA";

    protected override void Rebuild() => _groups = StationIndex.Build(Session);

    protected override int RowCount() => _groups.Count;

    protected override string RowText(int i)
    {
        FuelGroup g = _groups[i];
        return Cell(g.Type + (g.AnyTypeAbsent ? " *" : ""), 22) + Cell(g.Stations.Count.ToString(CultureInfo.InvariantCulture), 10)
             + Cell(g.UnitCount.ToString(CultureInfo.InvariantCulture), 8) + g.CapacityMva.ToString("F1", CultureInfo.InvariantCulture);
    }

    public override string? HelpLine(int i) => _groups.Count > i && _groups[i].AnyTypeAbsent ? "* some units have no type member; the loader reads them as ccgt." : null;

    public override View? Open(int i) => new StationView(Session, _groups[i].Type);

    public override AddContext? AddHere(int row)
    {
        CaseDocument gen = Session.Doc("generation.json");
        string? type = row >= 0 && row < _groups.Count ? _groups[row].Type : null;
        return new AddContext(gen.Id, "", gen.Exists ? gen.Text.Root.Elements.Count : 0,
            type is null ? Array.Empty<KeyValuePair<string, string>>() : new[] { new KeyValuePair<string, string>("type", type) });
    }
}

/// <summary>The stations of one fuel type (ordinal by name, then first unit); Enter lists a station's units.</summary>
internal sealed class StationView : View
{
    private readonly string _type;
    private IReadOnlyList<Station> _stations = Array.Empty<Station>();

    public StationView(CaseSession session, string type) : base(session) => _type = type;

    public override string Crumb => _type;

    public override string Header => Cell("station", 44) + Cell("units", 8) + "capacity MVA";

    protected override void Rebuild()
        => _stations = StationIndex.Build(Session).FirstOrDefault(g => g.Type == _type)?.Stations ?? (IReadOnlyList<Station>)Array.Empty<Station>();

    protected override int RowCount() => _stations.Count;

    protected override string RowText(int i)
    {
        Station st = _stations[i];
        return Cell(st.DisplayName, 44) + Cell(st.Units.Count.ToString(CultureInfo.InvariantCulture), 8) + st.CapacityMva.ToString("F1", CultureInfo.InvariantCulture);
    }

    public override Station? StationAt(int i) { Refresh(); return i >= 0 && i < _stations.Count ? _stations[i] : null; }

    public override View? Open(int i)
    {
        Station st = _stations[i];
        CaseDocument gen = Session.Doc("generation.json");
        string type = st.Type;
        string? name = st.Name;
        return new RecordListView(Session, gen.Id, "", $"{st.DisplayName} ({st.Units.Count.ToString(CultureInfo.InvariantCulture)} units)",
            filter: () => StationIndex.Build(Session).FirstOrDefault(g => g.Type == type)?.Stations.FirstOrDefault(x => x.Name == name)?.Units ?? Array.Empty<int>(),
            add: row => UnitAdd(gen, type, name, row));
    }

    /// <summary>Add in a station: name and type preset, and the bus of the selected unit (or the station's first unit).</summary>
    private AddContext UnitAdd(CaseDocument gen, string type, string? name, int row)
    {
        var presets = new List<KeyValuePair<string, string>> { new("type", type) };
        if (name is not null) presets.Add(new("name", name));
        Station? st = StationIndex.Build(Session).FirstOrDefault(g => g.Type == type)?.Stations.FirstOrDefault(x => x.Name == name);
        if (st is not null && st.Units.Count > 0)
        {
            int unit = row >= 0 && row < st.Units.Count ? st.Units[row] : st.Units[0];
            if (RecordRead.Int(gen.Text.Root.Elements[unit], "bus", gen.KeyComparer) is int bus)
                presets.Add(new("bus", bus.ToString(CultureInfo.InvariantCulture)));
            return new AddContext(gen.Id, "", st.Units[^1] + 1, presets);
        }
        return new AddContext(gen.Id, "", gen.Exists ? gen.Text.Root.Elements.Count : 0, presets);
    }

    public override AddContext? AddHere(int row)
    {
        CaseDocument gen = Session.Doc("generation.json");
        return new AddContext(gen.Id, "", gen.Exists ? gen.Text.Root.Elements.Count : 0, new[] { new KeyValuePair<string, string>("type", _type) });
    }
}

/// <summary>
/// A plain list of text lines (the files not part of the case, help, the error list, referrers, find results). A
/// line may carry a jump target (a document and the pointer of a record); Enter opens that record's form.
/// </summary>
internal sealed class TextListView : View
{
    private readonly string _crumb;
    private readonly string _header;
    private readonly IReadOnlyList<string> _lines;
    private readonly IReadOnlyList<(int Doc, string Pointer)?> _targets;

    public TextListView(CaseSession session, string crumb, string header, IReadOnlyList<string> lines, IReadOnlyList<(int Doc, string Pointer)?> targets) : base(session)
    {
        _crumb = crumb;
        _header = header;
        _lines = lines;
        _targets = targets;
    }

    /// <summary>A list with no jump targets.</summary>
    public static TextListView Plain(CaseSession session, string crumb, string header, IReadOnlyList<string> lines)
        => new(session, crumb, header, lines, lines.Select(_ => ((int, string)?)null).ToList());

    public override string Crumb => _crumb;

    public override string Header => _header;

    protected override int RowCount() => _lines.Count;

    protected override string RowText(int i) => _lines[i];

    public override View? Open(int i)
    {
        if (i < 0 || i >= _targets.Count || _targets[i] is not (int doc, string pointer)) return null;
        CaseDocument d = Session.Documents[doc];
        if (!d.Exists && d.Spec is { IsArray: true }) return null;
        return new RecordFormView(Session, doc, pointer, d.FileName + " " + (pointer.Length == 0 ? "(root)" : pointer));
    }
}
