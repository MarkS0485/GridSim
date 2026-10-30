using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Gda.Csv;

/// <summary>
/// A streaming, RFC-4180-ish CSV reader tuned for the quirks of the GDA / LTDS
/// exports: a UTF-8 BOM on the first field, header names polluted with stray tabs
/// or trailing spaces (e.g. <c>"Licence\t"</c>), quoted fields containing commas,
/// escaped quotes (<c>""</c>) and embedded newlines, and ragged rows (missing
/// trailing columns). Access is by column <em>name</em> (case/space/tab-insensitive),
/// so a reader keeps working if upstream reorders or pads columns.
/// </summary>
public sealed class CsvTable : IDisposable
{
    private readonly TextReader _reader;
    private readonly bool _ownsReader;
    private readonly Dictionary<string, int> _index;

    /// <summary>The normalised header names, in file order.</summary>
    public IReadOnlyList<string> Headers { get; }

    private CsvTable(TextReader reader, bool ownsReader, IReadOnlyList<string> headers)
    {
        _reader = reader;
        _ownsReader = ownsReader;
        Headers = headers;
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            // GR-793: two headers that normalise alike ("Winter" and "Winter ", or "MW" and "mw") used to resolve
            // silently to the first, so the second column was unreachable and a blank first copy read as 0. The
            // name is now remembered as ambiguous and any lookup of it throws (see IndexOf). A table whose
            // duplicated column is never asked for (blank trailing headers from a padded export, say) still reads.
            string key = Normalize(headers[i]);
            if (!_index.TryAdd(key, i)) _ambiguous.Add(key);
        }
    }

    // Normalised header names that occur more than once (GR-793). Looking one up is an error.
    private readonly HashSet<string> _ambiguous;

    // The 1-based number of the last data record returned by Rows(), for error messages (GR-811).
    private long _record;

    /// <summary>Open a CSV file for streaming. Throws if the file has no header line.</summary>
    public static CsvTable Open(string path)
    {
        var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Create(sr, ownsReader: true);
    }

    /// <summary>Wrap an existing reader (used by tests with in-memory strings).</summary>
    public static CsvTable Create(TextReader reader, bool ownsReader = false)
    {
        List<string>? header = ReadRecord(reader);
        if (header is null) throw new InvalidDataException("CSV has no header row.");
        for (int i = 0; i < header.Count; i++) header[i] = Normalize(header[i]);
        return new CsvTable(reader, ownsReader, header);
    }

    public bool HasColumn(string name) => IndexOf(name) >= 0;

    /// <summary>
    /// Stream the data rows. Each row is reusable only within its iteration. A row with FEWER cells than headers is
    /// accepted (its missing trailing cells read as empty); a row with MORE cells than headers whose extra cells are
    /// not all blank throws (GR-811), because that is almost always an unquoted comma inside a value, which shifts
    /// every later cell of the row one column to the right (a node name "Smith, Jones St" made Status read the BSP
    /// group and the voltage read the status text, which parsed as 0). Blank extra cells (a trailing comma) are
    /// harmless and still accepted.
    /// </summary>
    public IEnumerable<CsvRow> Rows()
    {
        while (true)
        {
            List<string>? rec = ReadRecord(_reader);
            if (rec is null) yield break;
            // Skip fully-blank lines.
            if (rec.Count == 1 && rec[0].Length == 0) continue;
            _record++;
            for (int i = Headers.Count; i < rec.Count; i++)
            {
                if (rec[i].Trim().Length > 0)
                    throw new InvalidDataException(
                        $"CSV data record {_record.ToString(CultureInfo.InvariantCulture)} has {rec.Count.ToString(CultureInfo.InvariantCulture)} " +
                        $"cells but the header has {Headers.Count.ToString(CultureInfo.InvariantCulture)}, and extra cell " +
                        $"{(i + 1).ToString(CultureInfo.InvariantCulture)} holds '{rec[i].Trim()}': a value probably carries an " +
                        "unquoted comma, which would shift the rest of the row onto the wrong columns.");
            }
            yield return new CsvRow(this, rec);
        }
    }

    internal int IndexOf(string column)
    {
        string key = Normalize(column);
        // GR-793: a duplicated header cannot be resolved to one column; refuse rather than pick the first.
        if (_ambiguous.Contains(key))
            throw new InvalidDataException(
                $"CSV: the header names column '{key}' more than once (after trimming spaces, tabs and case), so which " +
                "column to read is ambiguous.");
        return _index.TryGetValue(key, out int i) ? i : -1;
    }

    internal static string Normalize(string s) => s.Trim().Trim('\uFEFF').Replace("	", " ").Trim();

    /// <summary>
    /// Parse one logical record (which may span multiple physical lines inside a
    /// quoted field). Returns null at end of stream.
    /// </summary>
    private static List<string>? ReadRecord(TextReader reader)
    {
        int first = reader.Peek();
        if (first < 0) return null;

        var fields = new List<string>(24);
        var sb = new StringBuilder(64);
        bool inQuotes = false;
        bool any = false;

        int ci;
        while ((ci = reader.Read()) >= 0)
        {
            any = true;
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); sb.Append('"'); } // escaped quote
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                switch (c)
                {
                    // A quote opens a quoted field only at the start of a field (RFC 4180; spaces before it are allowed
                    // and dropped, as before). A quote inside an unquoted field (an LTDS cell '12" XLPE cable') is a
                    // literal character: it used to open quote mode, so every following comma and newline was swallowed
                    // into one cell and dozens of rows vanished.
                    case '"' when OnlySpaces(sb): sb.Clear(); inQuotes = true; break;
                    case ',': fields.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break; // swallow; \n ends the record
                    case '\n': fields.Add(sb.ToString()); return fields;
                    default: sb.Append(c); break;
                }
            }
        }

        if (!any) return null;
        // The stream ended inside a quoted field: the closing quote is missing, and the rest of the file was read as one
        // cell. Refuse it rather than return the merged record.
        if (inQuotes)
            throw new InvalidDataException("CSV: the file ends inside a quoted field (a closing '\"' is missing).");
        fields.Add(sb.ToString());
        return fields;
    }

    // True when the field read so far is empty or only spaces / tabs.
    private static bool OnlySpaces(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] is not (' ' or '\t')) return false;
        return true;
    }

    public void Dispose() { if (_ownsReader) _reader.Dispose(); }
}

/// <summary>One CSV data row, accessed by column name. Missing/blank cells read as empty.</summary>
public readonly struct CsvRow : IRow
{
    private readonly CsvTable _table;
    private readonly List<string> _values;

    internal CsvRow(CsvTable table, List<string> values) { _table = table; _values = values; }

    /// <summary>Raw cell text by column name (trimmed). Empty string if absent.</summary>
    public string this[string column]
    {
        get
        {
            int i = _table.IndexOf(column);
            return i >= 0 && i < _values.Count ? _values[i].Trim() : string.Empty;
        }
    }

    public bool Has(string column) => _table.IndexOf(column) is int i && i >= 0 && i < _values.Count && _values[i].Trim().Length > 0;

    /// <summary>Parse a cell as double; returns <paramref name="fallback"/> if blank/unparseable. Tolerates thousands
    /// commas and a percent sign; refuses a number in another form (see <see cref="GdaNumber.TryParse"/>).</summary>
    public double Double(string column, double fallback = 0.0)
    {
        string s = this[column];
        if (s.Length == 0) return fallback;
        if (!GdaNumber.TryParse(s, column, out double v)) return fallback;
        // 'NaN' and 'Infinity' parse, and passed every "x <= 0" gate downstream into the model; they are not numbers.
        if (!double.IsFinite(v))
            throw new InvalidDataException($"CSV column '{column}': cell '{this[column]}' is not a finite number.");
        return v;
    }

    // GR-791: CsvRow.Int (parse a cell as int, falling back silently on anything unparseable) had no production
    // caller and was removed; a future integer column should parse strictly rather than revive it.
}

/// <summary>
/// GR-379 (October 2026): the one number rule of the GDA CSV, Parquet-text and UKPN readers. The readers used to strip
/// every comma and then parse with <see cref="NumberStyles.Any"/>, so <c>0,5</c> (a decimal comma) read as 5,
/// <c>(5)</c> as -5, <c>5-</c> as -5 and a currency sign was accepted. Now:
/// <list type="bullet">
/// <item>a percent sign is dropped and commas are accepted only as thousands separators in their proper places
/// (<c>1,234.5</c>, <c>12,345,678</c>); any other comma is refused, since it is most likely a decimal comma;</item>
/// <item>the rest must be a plain invariant number (<see cref="NumberStyles.Float"/>: sign, digits, point, exponent);</item>
/// <item>text that only the permissive form reads (parentheses, a trailing sign, a currency symbol) is refused, because
/// guessing its sign or unit would change a value silently;</item>
/// <item>text that is not a number in any form (<c>N/A</c>, <c>-</c>) is still "no value" and the caller's fallback
/// applies, as before.</item>
/// </list>
/// A refusal throws <see cref="InvalidDataException"/> naming the column and the cell, so a build stops on it.
/// </summary>
internal static class GdaNumber
{
    // Digits in groups of three after a leading group of one to three, with an optional sign and fraction.
    private static readonly Regex ThousandsGrouped = new(
        @"^[+-]?\d{1,3}(,\d{3})+(\.\d*)?([eE][+-]?\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>Parse <paramref name="raw"/> by the GDA rule; false (no value) for text that is not a number at all.</summary>
    /// <exception cref="InvalidDataException">The text is a number in a form GridSim does not guess at.</exception>
    public static bool TryParse(string raw, string column, out double value)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string s = raw.Replace("%", "", StringComparison.Ordinal).Trim();
        if (s.Contains(',', StringComparison.Ordinal))
        {
            if (!ThousandsGrouped.IsMatch(s))
                throw new InvalidDataException(
                    $"column '{column}': cell '{raw}' has a comma that is not a thousands separator (a decimal comma?); " +
                    "export the source data with a decimal point.");
            s = s.Replace(",", "", StringComparison.Ordinal);
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException(
                $"column '{column}': cell '{raw}' is not a plain number (parentheses, a trailing sign or a currency " +
                "symbol); GridSim does not guess its sign or unit.");
        value = 0;
        return false;
    }
}
