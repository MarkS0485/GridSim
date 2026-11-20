using System.Text;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>
/// Monospace text forms of the workbench tables (the Avalonia view lists them in a monospaced list; the console and tests use
/// them too). Numbers are right-aligned, text left-aligned, a gap is a dash; columns are as wide as their widest cell.
/// </summary>
public static class WorkbenchTextTables
{
    /// <summary>The heading of a column: its title, with the unit in brackets when it has one.</summary>
    public static string Heading(ModelColumn c) => c.Unit.Length > 0 ? $"{c.Title} [{c.Unit}]" : c.Title;

    private static bool IsRight(ModelColumn c) => c.Type is ModelColumnType.Integer or ModelColumnType.Number;

    /// <summary>The lines of a model table: a header line and a rule, then each section (a "== title ==" line when grouped), its rows
    /// and, in a grouped table, a total line for the summable columns.</summary>
    public static IReadOnlyList<string> Lines(ModelTableView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var items = new List<(string? Section, IReadOnlyList<string>? Cells)>();
        foreach (ModelSection s in view.Sections)
        {
            if (s.Title.Length > 0) items.Add(("== " + s.Title + " ==", null));
            foreach (ModelRow r in s.Rows) items.Add((null, r.Cells.Select(c => c.Text).ToList()));
            if (s.Title.Length > 0 && s.Totals.Any(t => t is not null))
                items.Add((null, Enumerable.Range(0, view.Columns.Count)
                    .Select(i => s.Totals[i] is double d ? WorkbenchText.Show(d, view.Columns[i].Decimals) : i == 0 ? "total" : "").ToList()));
        }
        return Format(view.Columns.Select(Heading).ToList(), view.Columns.Select(IsRight).ToList(), items);
    }

    /// <summary>The lines of a result table: a header line, a rule, then one line per row.</summary>
    public static IReadOnlyList<string> Lines(WorkbenchTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Format(table.Columns.Select(Heading).ToList(), table.Columns.Select(IsRight).ToList(),
            table.Rows.Select(r => ((string?)null, (IReadOnlyList<string>?)r.Select(c => c.Text).ToList())).ToList());
    }

    private static List<string> Format(IReadOnlyList<string> headings, IReadOnlyList<bool> right, IReadOnlyList<(string? Section, IReadOnlyList<string>? Cells)> items)
    {
        int n = headings.Count;
        var width = new int[n];
        for (int c = 0; c < n; c++) width[c] = headings[c].Length;
        foreach ((_, IReadOnlyList<string>? cells) in items)
            if (cells is not null)
                for (int c = 0; c < n; c++) width[c] = Math.Max(width[c], cells[c].Length);
        string Line(IReadOnlyList<string> cells)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < n; c++)
            {
                if (c > 0) sb.Append("  ");
                sb.Append(right[c] ? cells[c].PadLeft(width[c]) : cells[c].PadRight(width[c]));
            }
            return sb.ToString().TrimEnd();
        }
        string header = Line(headings);
        var lines = new List<string> { header, new('-', header.Length) };
        foreach ((string? section, IReadOnlyList<string>? cells) in items) lines.Add(section ?? Line(cells!));
        return lines;
    }
}
