using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>The element kinds the model browser lists.</summary>
public enum ModelElementKind { Bus, Generator, Line, Transformer }

/// <summary>The type of a model-table column: how its cells sort and compare.</summary>
public enum ModelColumnType { Integer, Number, Text, Flag }

/// <summary>One column: its key (stable, used by sort and filter), title, unit text and type. Summable columns
/// carry a total in every section.</summary>
public sealed record ModelColumn(string Key, string Title, string Unit, ModelColumnType Type, bool Summable = false, int Decimals = 3);

/// <summary>One cell. <see cref="Missing"/> is an absent value (an unset rating, an unnamed element): it shows as a dash,
/// holds no number, sorts last and matches no filter. It is never zero.</summary>
public sealed record ModelCell(string Text, double? Number, bool Missing)
{
    public static ModelCell Gap { get; } = new(WorkbenchText.Gap, null, true);
    public static ModelCell Int(int v) => new(WorkbenchText.Canon(v), v, false);
    public static ModelCell Num(double v, int decimals) =>
        double.IsFinite(v) ? new(WorkbenchText.Show(v, decimals), v, false) : Gap;
    public static ModelCell Txt(string? v) => string.IsNullOrEmpty(v) ? Gap : new(v, null, false);
    public static ModelCell Flag(bool v) => new(v ? "yes" : "no", v ? 1 : 0, false);
}

/// <summary>One table row: the element's position in its model list (<see cref="Index"/>), a label, its cells in column
/// order and the ids of the groups it belongs to (empty without a groups document).</summary>
public sealed record ModelRow(int Index, string Label, IReadOnlyList<ModelCell> Cells, IReadOnlyList<string> Groups);

/// <summary>A section of a grouped table (one group, or the "ungrouped" remainder), or the whole table when it is not
/// grouped (then <see cref="GroupId"/> is null and the title empty). <see cref="Totals"/> aligns with the columns: the sum
/// of the section's rows in row order for a summable column, null elsewhere.</summary>
public sealed record ModelSection(string? GroupId, string Title, IReadOnlyList<ModelRow> Rows, IReadOnlyList<double?> Totals);

public enum ModelFilterOp { Equals, NotEquals, Less, LessOrEqual, Greater, GreaterOrEqual, Contains }

/// <summary>One filter: the column key, the operator and the operand text. Several filters combine with AND.</summary>
public sealed record ModelFilter(string Column, ModelFilterOp Op, string Value);

/// <summary>What to show: the kind, the filters, an optional sort (stable; ties keep model order), and an optional group kind
/// to section the rows by (the R4-01 groups of that kind, in the groups document's order).</summary>
public sealed record ModelTableQuery(ModelElementKind Kind, IReadOnlyList<ModelFilter>? Filters = null,
    string? SortKey = null, bool Descending = false, GroupKind? GroupBy = null);

/// <summary>The answer to a <see cref="ModelTableQuery"/>.</summary>
public sealed record ModelTableView(ModelElementKind Kind, IReadOnlyList<ModelColumn> Columns, IReadOnlyList<ModelSection> Sections,
    int TotalRows, int ShownRows)
{
    /// <summary>The rows of every section, in order.</summary>
    public IEnumerable<ModelRow> AllRows => Sections.SelectMany(s => s.Rows);

    /// <summary>A canonical text form (one line per row, tab separated), so two clients can show equal tables by comparing text.</summary>
    public string Canonical()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("gridsim-model-table/1\nkind\t").Append(Kind).Append('\n');
        sb.Append("columns\t").AppendJoin('\t', Columns.Select(c => c.Key)).Append('\n');
        foreach (ModelSection s in Sections)
        {
            sb.Append("section\t").Append(s.GroupId ?? "").Append('\t').Append(s.Title).Append('\n');
            foreach (ModelRow r in s.Rows)
                sb.Append("row\t").Append(r.Index).Append('\t').AppendJoin('\t', r.Cells.Select(c => c.Missing ? "" : c.Text)).Append('\n');
            sb.Append("totals\t").AppendJoin('\t', s.Totals.Select(t => t is null ? "" : WorkbenchText.Canon(t.Value))).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// The model browser's table builder (R4-10). It lists the buses, generators, lines and transformers of a
/// <see cref="GridModel"/> with their data in named, unit-labelled columns; sorts and filters them; and sections them by the
/// R4-01 groups. The same table feeds the Avalonia view model and the web DTO, so both clients show the same cells.
/// <para>Order of operations: build every row of the kind in model order, then filter (AND), then sort (stable), then
/// section by group, then total each section in row order. A group section lists a row once per group it belongs to (the
/// exclusive kinds - area, zone, owner - put it in at most one); rows in no group of the kind go to a final "(ungrouped)"
/// section. A line or transformer belongs to a group when the group lists it or both its ends are in the group's buses.</para>
/// </summary>
public static class ModelTable
{
    public const string UngroupedTitle = "(ungrouped)";

    /// <summary>The columns of <paramref name="kind"/>, in display order.</summary>
    public static IReadOnlyList<ModelColumn> Columns(ModelElementKind kind) => kind switch
    {
        ModelElementKind.Bus => BusColumns,
        ModelElementKind.Generator => GeneratorColumns,
        ModelElementKind.Line or ModelElementKind.Transformer => BranchColumns,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static readonly ModelColumn[] BusColumns =
    [
        new("id", "Bus", "", ModelColumnType.Integer),
        new("name", "Name", "", ModelColumnType.Text),
        new("type", "Type", "", ModelColumnType.Text),
        new("baseKv", "Base voltage", "kV", ModelColumnType.Number, false, 3),
        new("pd", "Demand P", "MW", ModelColumnType.Number, true, 3),
        new("qd", "Demand Q", "MVAr", ModelColumnType.Number, true, 3),
        new("gs", "Shunt G", "MW at 1 pu", ModelColumnType.Number, false, 3),
        new("bs", "Shunt B", "MVAr at 1 pu", ModelColumnType.Number, false, 3),
        new("vm", "Voltage", "pu", ModelColumnType.Number, false, 4),
        new("vaDeg", "Angle", "deg", ModelColumnType.Number, false, 3),
        new("vminPu", "Own V min", "pu", ModelColumnType.Number, false, 3),
        new("vmaxPu", "Own V max", "pu", ModelColumnType.Number, false, 3),
    ];

    private static readonly ModelColumn[] GeneratorColumns =
    [
        new("index", "Unit", "", ModelColumnType.Integer),
        new("bus", "Bus", "", ModelColumnType.Integer),
        new("name", "Name", "", ModelColumnType.Text),
        new("type", "Type", "", ModelColumnType.Text),
        new("pg", "Output P", "MW", ModelColumnType.Number, true, 3),
        new("qg", "Output Q", "MVAr", ModelColumnType.Number, true, 3),
        new("pmin", "P min", "MW", ModelColumnType.Number, false, 3),
        new("pmax", "P max", "MW", ModelColumnType.Number, true, 3),
        new("qmin", "Q min", "MVAr", ModelColumnType.Number, false, 3),
        new("qmax", "Q max", "MVAr", ModelColumnType.Number, false, 3),
        new("vg", "Set voltage", "pu", ModelColumnType.Number, false, 4),
        new("mbase", "Machine base", "MVA", ModelColumnType.Number, false, 3),
        new("inService", "In service", "", ModelColumnType.Flag),
    ];

    private static readonly ModelColumn[] BranchColumns =
    [
        new("index", "Branch", "", ModelColumnType.Integer),
        new("from", "From", "", ModelColumnType.Integer),
        new("to", "To", "", ModelColumnType.Integer),
        new("name", "Name", "", ModelColumnType.Text),
        new("r", "Resistance", "pu", ModelColumnType.Number, false, 5),
        new("x", "Reactance", "pu", ModelColumnType.Number, false, 5),
        new("b", "Charging", "pu", ModelColumnType.Number, false, 5),
        new("tap", "Tap ratio", "", ModelColumnType.Number, false, 4),
        new("shiftDeg", "Phase shift", "deg", ModelColumnType.Number, false, 3),
        new("rateA", "Rating A", "MVA", ModelColumnType.Number, false, 3),
        new("inService", "In service", "", ModelColumnType.Flag),
    ];

    /// <summary>The element counts the project tree shows.</summary>
    public static int Count(GridModel model, ModelElementKind kind)
    {
        ArgumentNullException.ThrowIfNull(model);
        return kind switch
        {
            ModelElementKind.Bus => model.Buses.Count,
            ModelElementKind.Generator => model.AllGenerators.Count,
            ModelElementKind.Line => model.Branches.Count(b => !b.IsTransformer),
            ModelElementKind.Transformer => model.Branches.Count(b => b.IsTransformer),
            _ => 0,
        };
    }

    /// <summary>Run a query against a model (and, to section by group, its bound groups).</summary>
    public static ModelTableView Build(GridModel model, ModelTableQuery query, BoundGroups? groups = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(query);
        IReadOnlyList<ModelColumn> cols = Columns(query.Kind);
        Dictionary<string, int> colIndex = Index(cols);
        foreach (ModelFilter f in query.Filters ?? [])
            if (!colIndex.ContainsKey(f.Column)) throw new ArgumentException($"Unknown column '{f.Column}' for {query.Kind}.", nameof(query));
        if (query.SortKey is not null && !colIndex.ContainsKey(query.SortKey))
            throw new ArgumentException($"Unknown sort column '{query.SortKey}' for {query.Kind}.", nameof(query));

        // 1. every row of the kind, in model order
        List<ModelRow> rows = Rows(model, query.Kind, groups);
        int total = rows.Count;

        // 2. filter
        foreach (ModelFilter f in query.Filters ?? [])
        {
            int ci = colIndex[f.Column];
            ModelColumn col = cols[ci];
            rows = rows.Where(r => Matches(r.Cells[ci], col, f)).ToList();
        }

        // 3. stable sort (a missing cell last, whichever the direction)
        if (query.SortKey is not null)
        {
            int ci = colIndex[query.SortKey];
            ModelColumn col = cols[ci];
            int dir = query.Descending ? -1 : 1;
            var indexed = rows.Select((r, i) => (r, i)).ToList();
            indexed.Sort((a, b) =>
            {
                int c = Compare(a.r.Cells[ci], b.r.Cells[ci], col, dir);
                return c != 0 ? c : a.i.CompareTo(b.i);
            });
            rows = indexed.Select(t => t.r).ToList();
        }

        // 4. sections and totals
        var sections = new List<ModelSection>();
        if (query.GroupBy is GroupKind kind && groups is not null)
        {
            var seen = new HashSet<ModelRow>(ReferenceEqualityComparer.Instance);
            foreach (BoundGroup g in groups.Groups.Where(g => g.Group.Kind == kind))
            {
                List<ModelRow> members = rows.Where(r => r.Groups.Contains(g.Id)).ToList();
                foreach (ModelRow r in members) seen.Add(r);
                sections.Add(Section(g.Id, g.Id, members, cols));
            }
            List<ModelRow> rest = rows.Where(r => !seen.Contains(r)).ToList();
            if (rest.Count > 0) sections.Add(Section(null, UngroupedTitle, rest, cols));
        }
        else sections.Add(Section(null, "", rows, cols));

        return new ModelTableView(query.Kind, cols, sections, total, rows.Count);
    }

    private static Dictionary<string, int> Index(IReadOnlyList<ModelColumn> cols)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < cols.Count; i++) d[cols[i].Key] = i;
        return d;
    }

    private static ModelSection Section(string? id, string title, List<ModelRow> rows, IReadOnlyList<ModelColumn> cols)
    {
        var totals = new double?[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            if (!cols[c].Summable) continue;
            double sum = 0;
            foreach (ModelRow r in rows) if (r.Cells[c].Number is double v) sum += v;
            totals[c] = sum;
        }
        return new ModelSection(id, title, rows, totals);
    }

    private static bool Matches(ModelCell cell, ModelColumn col, ModelFilter f)
    {
        if (cell.Missing) return false;
        if (f.Op == ModelFilterOp.Contains)
            return cell.Text.Contains(f.Value, StringComparison.OrdinalIgnoreCase);
        if (col.Type == ModelColumnType.Text)
        {
            return f.Op switch
            {
                ModelFilterOp.Equals => string.Equals(cell.Text, f.Value, StringComparison.OrdinalIgnoreCase),
                ModelFilterOp.NotEquals => !string.Equals(cell.Text, f.Value, StringComparison.OrdinalIgnoreCase),
                _ => throw new ArgumentException($"Operator {f.Op} needs a numeric column; '{col.Key}' is text."),
            };
        }
        double operand;
        if (col.Type == ModelColumnType.Flag)
        {
            string t = f.Value.Trim().ToLowerInvariant();
            operand = t is "yes" or "true" or "1" ? 1 : t is "no" or "false" or "0" ? 0
                : throw new ArgumentException($"'{f.Value}' is not yes or no.");
        }
        else if (!double.TryParse(f.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out operand))
            throw new ArgumentException($"'{f.Value}' is not a number.");
        double v = cell.Number!.Value;
        return f.Op switch
        {
            ModelFilterOp.Equals => v == operand,
            ModelFilterOp.NotEquals => v != operand,
            ModelFilterOp.Less => v < operand,
            ModelFilterOp.LessOrEqual => v <= operand,
            ModelFilterOp.Greater => v > operand,
            ModelFilterOp.GreaterOrEqual => v >= operand,
            _ => false,
        };
    }

    private static int Compare(ModelCell a, ModelCell b, ModelColumn col, int dir)
    {
        if (a.Missing || b.Missing) return a.Missing == b.Missing ? 0 : a.Missing ? 1 : -1;
        int c = col.Type == ModelColumnType.Text
            ? string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase)
            : a.Number!.Value.CompareTo(b.Number!.Value);
        return c * dir;
    }

    // ------------------------------------------------------------------ row builders

    private static List<ModelRow> Rows(GridModel model, ModelElementKind kind, BoundGroups? groups)
    {
        var rows = new List<ModelRow>();
        switch (kind)
        {
            case ModelElementKind.Bus:
                for (int i = 0; i < model.Buses.Count; i++)
                {
                    Bus b = model.Buses[i];
                    IReadOnlyList<string> g = groups is null ? [] : groups.Groups.Where(x => x.BusIds.Contains(b.Id)).Select(x => x.Id).ToList();
                    rows.Add(new ModelRow(i, WorkbenchText.Canon(b.Id), new[]
                    {
                        ModelCell.Int(b.Id), ModelCell.Txt(b.Name), ModelCell.Txt(b.Type.ToString()),
                        Positive(b.BaseKv, 3), ModelCell.Num(b.Pd, 3), ModelCell.Num(b.Qd, 3), ModelCell.Num(b.Gs, 3), ModelCell.Num(b.Bs, 3),
                        ModelCell.Num(b.Vm, 4), ModelCell.Num(b.VaDeg, 3), Opt(b.VminPu, 3), Opt(b.VmaxPu, 3),
                    }, g));
                }
                break;
            case ModelElementKind.Generator:
                for (int i = 0; i < model.AllGenerators.Count; i++)
                {
                    Generator u = model.AllGenerators[i];
                    IReadOnlyList<string> g = groups is null ? [] : groups.Groups.Where(x => BoundGroups.GeneratorsOf(x, model).Contains(i)).Select(x => x.Id).ToList();
                    rows.Add(new ModelRow(i, u.Name ?? "unit " + i, new[]
                    {
                        ModelCell.Int(i), ModelCell.Int(u.Bus), ModelCell.Txt(u.Name), ModelCell.Txt(u.Type),
                        ModelCell.Num(u.Pg, 3), ModelCell.Num(u.Qg, 3), ModelCell.Num(u.Pmin, 3), ModelCell.Num(u.Pmax, 3),
                        ModelCell.Num(u.Qmin, 3), ModelCell.Num(u.Qmax, 3), ModelCell.Num(u.Vg, 4), Positive(u.MBase, 3), ModelCell.Flag(u.InService),
                    }, g));
                }
                break;
            default:
                bool wantTransformers = kind == ModelElementKind.Transformer;
                for (int i = 0; i < model.Branches.Count; i++)
                {
                    Branch br = model.Branches[i];
                    if (br.IsTransformer != wantTransformers) continue;
                    IReadOnlyList<string> g = groups is null ? [] : groups.Groups.Where(x => x.BranchIndices.Contains(i)
                        || (x.BusIds.Contains(br.From) && x.BusIds.Contains(br.To))).Select(x => x.Id).ToList();
                    rows.Add(new ModelRow(i, br.Name ?? $"{br.From}-{br.To}", new[]
                    {
                        ModelCell.Int(i), ModelCell.Int(br.From), ModelCell.Int(br.To), ModelCell.Txt(br.Name),
                        ModelCell.Num(br.R, 5), ModelCell.Num(br.X, 5), ModelCell.Num(br.B, 5), ModelCell.Num(br.TapRatio, 4),
                        ModelCell.Num(br.PhaseShiftDeg, 3), Positive(br.RateAMva, 3), ModelCell.Flag(br.InService),
                    }, g));
                }
                break;
        }
        return rows;
    }

    /// <summary>A value where zero (or less) means "not given" in the model data: shown as a gap, never as zero.</summary>
    private static ModelCell Positive(double v, int decimals) => v > 0 && double.IsFinite(v) ? ModelCell.Num(v, decimals) : ModelCell.Gap;

    private static ModelCell Opt(double? v, int decimals) => v is double d ? ModelCell.Num(d, decimals) : ModelCell.Gap;
}
