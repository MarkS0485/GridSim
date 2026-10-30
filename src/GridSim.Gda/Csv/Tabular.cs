using System.Globalization;
using Parquet.Schema;
using Parquet.Serialization;

namespace GridSim.Gda.Csv;

/// <summary>
/// A format-agnostic row: access cells by column name. Implemented over both CSV and
/// Parquet, so a DNO source reads whichever format the archive published.
/// </summary>
public interface IRow
{
    string this[string column] { get; }
    bool Has(string column);
    double Double(string column, double fallback = 0.0);
}

/// <summary>A tabular data source  -  CSV or Parquet  -  exposing a uniform row stream.</summary>
public abstract class DataSourceTable : IDisposable
{
    public abstract IReadOnlyList<string> Headers { get; }
    public abstract IEnumerable<IRow> Rows();
    public virtual void Dispose() => GC.SuppressFinalize(this);

    /// <summary>
    /// Open a dataset by path: a <c>.parquet</c> file, a directory of
    /// <c>part-*.parquet</c> (read and concatenated), or a <c>.csv</c> file.
    /// </summary>
    public static DataSourceTable Open(string pathOrDir)
    {
        if (Directory.Exists(pathOrDir))
        {
            var parts = Directory.EnumerateFiles(pathOrDir, "*.parquet", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (parts.Count > 0) return new ParquetDataTable(parts);
            string? csv = PickCsv(pathOrDir);
            if (csv is not null) return new CsvDataTable(csv);
            throw new FileNotFoundException($"No .parquet or .csv under {pathOrDir}");
        }
        if (pathOrDir.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            return new ParquetDataTable(new[] { pathOrDir });
        return new CsvDataTable(pathOrDir);
    }

    /// <summary>
    /// Choose THE csv of a directory dataset, deterministically (R297). It used to take
    /// <c>EnumerateFiles(...).FirstOrDefault()</c>, and enumeration order is filesystem-dependent (sorted on
    /// NTFS, hash order on ext4/NFS), so a folder holding two publications (a 2024 and a 2025 LTDS table)
    /// loaded one file on Windows and the other on the Linux server: identical commands built different
    /// replicas. Now: one csv is used as it is; with several, the one whose FILE NAME carries the strictly
    /// newest publication date (<see cref="GdaPaths.TryParseDateFromName"/>) is chosen; anything else - an
    /// undated candidate, or a tie for newest - is ambiguous and throws, naming the candidates, so the caller
    /// must point at one file. File modification times are never used: they differ between machines.
    /// Returns null when the directory holds no csv.
    /// </summary>
    internal static string? PickCsv(string dir)
    {
        List<string> csvs = Directory.EnumerateFiles(dir, "*.csv", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (csvs.Count <= 1) return csvs.FirstOrDefault();

        var dated = csvs.Select(p => (Path: p, Date: GdaPaths.TryParseDateFromName(p))).ToList();
        if (dated.All(d => d.Date is not null))
        {
            DateTime newest = dated.Max(d => d.Date!.Value);
            var top = dated.Where(d => d.Date == newest).ToList();
            if (top.Count == 1) return top[0].Path;
        }
        throw new InvalidDataException(
            $"{dir} holds {csvs.Count} csv files and no single one is unambiguously the newest by the date in its " +
            "name, so which to read would depend on the filesystem. Point at one file. Candidates: " +
            string.Join(", ", csvs.Select(p => Path.GetRelativePath(dir, p))));
    }
}

/// <summary>CSV-backed table (wraps <see cref="CsvTable"/>; <see cref="CsvRow"/> already satisfies <see cref="IRow"/>).</summary>
internal sealed class CsvDataTable : DataSourceTable
{
    private readonly CsvTable _table;
    public CsvDataTable(string path) => _table = CsvTable.Open(path);
    public override IReadOnlyList<string> Headers => _table.Headers;
    public override IEnumerable<IRow> Rows() { foreach (CsvRow r in _table.Rows()) yield return r; }
    public override void Dispose() { _table.Dispose(); base.Dispose(); }
}

/// <summary>
/// Parquet-backed table. Reads every row group into per-column object arrays (keyed
/// by field name) and serves them as rows. Loads a dataset fully into memory  -  fine
/// for the LTDS tables (thousands of rows), which is what this is used for.
/// </summary>
internal sealed class ParquetDataTable : DataSourceTable
{
    private readonly List<string> _headers = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);   // GR-793
    private readonly List<object?[]> _columns = new();   // one object?[rowCount] per column
    private readonly int _rowCount;

    public ParquetDataTable(IReadOnlyList<string> files)
    {
        // R297: the schema is the UNION of every part's columns (first-seen order), and every column gets exactly
        // one cell per row - null where a part lacks that column or a row lacks the value. The old reader took
        // the headers from the first part only and simply skipped a missing value, so a column absent from a
        // middle part shifted every later part's values onto the wrong rows.
        var colData = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        var fields = new List<DataField>();
        int rows = 0;

        foreach (string file in files)
        {
            using Stream stream = File.OpenRead(file);
            DeserializationResult<Dictionary<string, object>> result =
                ParquetSerializer.DeserializeUntypedAsync(stream).GetAwaiter().GetResult();
            foreach (DataField df in result.Schema.DataFields)
            {
                if (colData.ContainsKey(df.Name)) continue;
                // A column first seen in a later part is null for every earlier row.
                colData[df.Name] = new List<object?>(Enumerable.Repeat<object?>(null, rows));
                fields.Add(df);
            }
            foreach (Dictionary<string, object> row in result.Data)
            {
                // Nested/list leaves (e.g. geo shapes) don't surface as top-level cells: they read as null.
                foreach (DataField df in fields)
                    colData[df.Name].Add(row.TryGetValue(df.Name, out object? v) ? v : null);
                rows++;
            }
        }

        foreach (DataField df in fields)
        {
            string name = CsvTable.Normalize(df.Name);
            // GR-793: two field names that normalise alike are ambiguous; looking one up throws (see Cell).
            if (!_index.TryAdd(name, _headers.Count)) _ambiguous.Add(name);
            _headers.Add(name);
            _columns.Add(colData[df.Name].ToArray());
        }
        _rowCount = rows;
    }

    public override IReadOnlyList<string> Headers => _headers;

    // N185 (October 2026): the row count and the raw typed cell (Cell, below) are exposed inside the assembly so the
    // UKPN reader can read a Parquet copy with exactly the CSV reader's semantics (UkpnSource.CsvEquivalentRow).
    // Additive only: nothing here changes how existing callers (NpgSource, gda --schema) see a row.
    internal int RowCount => _rowCount;

    public override IEnumerable<IRow> Rows()
    {
        for (int i = 0; i < _rowCount; i++) yield return new ParquetRow(this, i);
    }

    internal object? Cell(int row, string column)
    {
        string key = CsvTable.Normalize(column);
        if (_ambiguous.Contains(key))
            throw new InvalidDataException(
                $"Parquet: the schema names column '{key}' more than once (after trimming spaces, tabs and case), so " +
                "which column to read is ambiguous.");
        return _index.TryGetValue(key, out int c) && row < _columns[c].Length ? _columns[c][row] : null;
    }

    private readonly struct ParquetRow : IRow
    {
        private readonly ParquetDataTable _t;
        private readonly int _i;
        public ParquetRow(ParquetDataTable t, int i) { _t = t; _i = i; }

        public string this[string column]
        {
            get { object? v = _t.Cell(_i, column); return v is null ? string.Empty : Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty; }
        }

        public bool Has(string column) => _t.Cell(_i, column) is { } v && v.ToString()!.Length > 0;

        public double Double(string column, double fallback = 0.0)
        {
            object? v = _t.Cell(_i, column);
            if (v is null) return fallback;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is decimal m) return (double)m;
            // GR-379: text cells follow the GDA number rule (GdaNumber), as CsvRow.Double does.
            string s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (s.Length == 0) return fallback;
            return GdaNumber.TryParse(s, column, out double r) ? r : fallback;
        }
    }
}
