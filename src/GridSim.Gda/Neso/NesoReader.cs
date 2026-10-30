using GridSim.Gda.Csv;

namespace GridSim.Gda.Neso;

/// <summary>
/// Schema-faithful readers for the NESO transmission-overlay datasets: the TEC
/// register (generation fleet), interconnectors, embedded plant, and per-boundary
/// transfer limits. Read from the authorised <c>/gda</c> path; nothing is embedded.
/// </summary>
public static class NesoReader
{
    public static IReadOnlyList<TecEntry> Tec(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        var list = new List<TecEntry>(2300);
        foreach (CsvRow r in t.Rows())
        {
            string name = r["Project Name"];
            if (name.Length == 0) continue;
            list.Add(new TecEntry(
                name, r["Customer Name"], r["Connection Site"], r["Stage"],
                r.Double("MW Connected"), r.Double("Cumulative Total Capacity (MW)"),
                r["Project Status"], r["HOST TO"], r["Plant Type"], r["Project ID"]));
        }
        return list;
    }

    public static IReadOnlyList<InterconnectorEntry> Interconnectors(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        var list = new List<InterconnectorEntry>(40);
        foreach (CsvRow r in t.Rows())
        {
            string name = r["Project Name"];
            if (name.Length == 0) continue;
            list.Add(new InterconnectorEntry(
                name, r["Connection Site"],
                r.Double("MW Import - Total"), r.Double("MW Export - Total"),
                r["Project Status"], r["HOST TO"],
                r.Double("MW Import - Current"), r.Double("MW Export - Current")));
        }
        return list;
    }

    public static IReadOnlyList<EmbeddedEntry> Embedded(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        var list = new List<EmbeddedEntry>(600);
        foreach (CsvRow r in t.Rows())
        {
            string name = r["Project Name"];
            if (name.Length == 0) continue;
            list.Add(new EmbeddedEntry(
                name, r["Connection Site"],
                r.Double("MW Connected"), r.Double("Cumulative Total Capacity (MW)"),
                r["Project Status"], r["HOST TO"], r["Plant Type"]));
        }
        return list;
    }

    /// <summary>
    /// Summarise the wide "24 Months Ahead Constraint Limits" table (columns YEAR,
    /// Week No, then one column per boundary code) into a per-boundary limit envelope.
    /// </summary>
    public static IReadOnlyList<BoundaryLimit> BoundaryLimits(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        var nonBoundary = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "YEAR", "Week No", "Week", "Year" };
        var codes = t.Headers.Where(h => !nonBoundary.Contains(h)).ToList();
        var values = codes.ToDictionary(c => c, _ => new List<double>(), StringComparer.OrdinalIgnoreCase);

        foreach (CsvRow r in t.Rows())
            foreach (string c in codes)
            {
                double v = r.Double(c, double.NaN);
                if (!double.IsNaN(v) && v > 0) values[c].Add(v);
            }

        var result = new List<BoundaryLimit>(codes.Count);
        foreach (string c in codes)
        {
            List<double> xs = values[c];
            if (xs.Count == 0) continue;
            xs.Sort();
            double median = xs[xs.Count / 2];
            result.Add(new BoundaryLimit(c.Trim(), xs[0], median, xs[^1], xs.Count));
        }
        return result;
    }
}
