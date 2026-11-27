using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.Dgs;

// =====================================================================================================================
// RC4 R4-11 part C: reader for the table structure of an ASCII DGS file (DIgSILENT PowerFactory "DGS" data exchange).
//
// WHAT IS KNOWN, AND FROM WHERE. No real PowerFactory export was available when this was written, and the DIgSILENT
// "DGS Interface" manual itself was NOT read (its public mirror refused access). What is taken from public sources:
//   * the ASCII variant is a CSV-like text with several tables in one file; a General table carries a mandatory Version
//     entry; the current format is DGS 5.0; table (class) names are case sensitive; a table header names the class and
//     typed columns, written like  $$ElmTerm;ID(a:40);loc_name(a:40);...  ; ID is the primary key and is unique across
//     the file and other tables refer to it. These facts come from search-engine summaries of the DIgSILENT manual
//     (PowerFactory 15, 2014), so they are second-hand.
//   * PowSyBl's PowerFactory importer documentation (https://powsybl.readthedocs.io/projects/powsybl-core/en/latest/
//     grid_exchange_formats/powerfactory/import.html) states that only DGS V5 in ASCII (.dgs) is supported.
//
// EVERYTHING ELSE IN THE DIALECT BELOW IS UNCONFIRMED and is a stated choice, not a discovery. Where a real file departs
// from it the reader REFUSES with the line number; it never guesses:
//   UNCONFIRMED: the delimiter is ';'; a header line starts with "$$"; column type letters are a (text), i (integer),
//   r (real), p (pointer, read as text); a type length is written (a:40); values may be wrapped in double quotes with ""
//   for an embedded quote; the real-number format is invariant (decimal point); blank lines are ignored and no other
//   comment syntax exists; the General table has the columns ID, Descr, Val and a row with Descr = Version; every table
//   has an ID column (except General); a table name may appear once. Any other type letter (d, t, ...) is refused.
// =====================================================================================================================

/// <summary>A DGS file that cannot be read: every problem named with its line number.</summary>
public sealed class DgsFormatException : Exception
{
    /// <summary>IO-15: the message lists at most this many problems, then "... and N more" (the full list is
    /// <see cref="Problems"/>).</summary>
    public const int MaxListedProblems = 50;

    public DgsFormatException(IReadOnlyList<string> problems) : base("DGS file refused: " + Listed(problems)) => Problems = problems;

    private static string Listed(IReadOnlyList<string> problems)
        => problems.Count <= MaxListedProblems
            ? string.Join(" ", problems)
            : string.Join(" ", problems.Take(MaxListedProblems)) + " ... and " + (problems.Count - MaxListedProblems).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more.";
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>One column of a table header: its name, its type letter and the declared length (null when none).</summary>
public sealed record DgsColumn(string Name, char Type, int? Length);

/// <summary>One data row with its 1-based source line.</summary>
public sealed class DgsRow
{
    private readonly DgsTable _table;
    internal DgsRow(DgsTable table, int line, string[] values) { _table = table; Line = line; Values = values; }
    public int Line { get; }
    public IReadOnlyList<string> Values { get; }
    public string Id => _table.IdIndex >= 0 ? Values[_table.IdIndex] : "";

    /// <summary>The trimmed text of a column, or null when the column is absent or the value is empty.</summary>
    public string? Text(string column)
    {
        int i = _table.ColumnIndex(column);
        if (i < 0) return null;
        string v = Values[i];
        return v.Length == 0 ? null : v;
    }

    /// <summary>A real value, or null when absent or empty. IO-09: the importer reads fixed attributes as numbers whatever
    /// type the header declared them, so a value that is not a finite number is refused here, naming the line, the table
    /// and the column (it used to surface as a bare FormatException, an "internal error").</summary>
    public double? Real(string column)
    {
        string? t = Text(column);
        if (t is null) return null;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw Bad(column, t, "a number");
        return v;
    }

    /// <summary>An integer value, or null when absent or empty. A whole number written as a real (<c>1.0</c>, <c>1e0</c>, as
    /// a column declared <c>r</c> holds it) is accepted; anything else is refused naming the line, the table and the column
    /// (IO-09).</summary>
    public long? Int(string column)
    {
        string? t = Text(column);
        if (t is null) return null;
        if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return v;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
            && d == Math.Floor(d) && Math.Abs(d) <= 9.007199254740992e15)
            return (long)d;
        throw Bad(column, t, "a whole number");
    }

    private DgsFormatException Bad(string column, string value, string expected)
    {
        string shown = value.Length <= 40 ? value : value[..40] + "...";
        return new DgsFormatException([FormattableString.Invariant($"line {Line}: table {_table.Name}, column {column}: '{shown}' is not {expected}.")]);
    }
}

/// <summary>One table: the class name, its typed columns and its rows in file order.</summary>
public sealed class DgsTable
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    internal DgsTable(string name, int line, IReadOnlyList<DgsColumn> columns)
    {
        Name = name; HeaderLine = line; Columns = columns;
        for (int i = 0; i < columns.Count; i++) _index.TryAdd(columns[i].Name, i);
    }
    public string Name { get; }
    public int HeaderLine { get; }
    public IReadOnlyList<DgsColumn> Columns { get; }
    public List<DgsRow> Rows { get; } = new();
    public int ColumnIndex(string column) => _index.TryGetValue(column, out int i) ? i : -1;
    public int IdIndex => ColumnIndex("ID");
    public bool Has(string column) => _index.ContainsKey(column);
}

/// <summary>A whole file: the General table's version and entries, and the object tables in file order.</summary>
public sealed class DgsFile
{
    public required string Version { get; init; }
    /// <summary>The General table rows other than Version (description, value, line), carried and not interpreted.</summary>
    public required IReadOnlyList<(string Descr, string Val, int Line)> General { get; init; }
    public required IReadOnlyList<DgsTable> Tables { get; init; }
    public DgsTable? Table(string name) => Tables.FirstOrDefault(t => t.Name == name);
}

/// <summary>Reads the DGS ASCII dialect described at the top of this file.</summary>
public static class DgsTableReader
{
    /// <summary>The only DGS version this reader accepts (the version public sources call current).</summary>
    public const string SupportedVersion = "5.0";
    public const char Delimiter = ';';

    public static DgsFile Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const int MaxProblems = 1000;
        var problems = new List<string>();
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        string[] lines = text.Split('\n');
        var tables = new List<DgsTable>();
        var all = new List<DgsTable>();
        DgsTable? current = null;
        var seenIds = new Dictionary<string, (string Table, int Line)>(StringComparer.Ordinal);
        for (int n = 0; n < lines.Length; n++)
        {
            int lineNo = n + 1;
            // IO-15: a large file that is not DGS gave one problem per line, all held and joined into one message.
            if (problems.Count >= MaxProblems)
            {
                problems.Add(FormattableString.Invariant($"reading stopped at line {lineNo} after {MaxProblems} problems; the rest of the file was not checked."));
                break;
            }
            string line = lines[n].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.StartsWith("$$", StringComparison.Ordinal))
            {
                current = ReadHeader(line, lineNo, problems);
                if (current is null) continue;
                DgsTable? dup = all.FirstOrDefault(t => t.Name == current.Name);
                if (dup is not null)
                {
                    problems.Add($"line {lineNo}: table '{current.Name}' appears a second time (first at line {dup.HeaderLine}); whether DGS allows that is UNCONFIRMED, so it is refused.");
                    current = null;
                    continue;
                }
                if (current.Name != "General" && current.IdIndex < 0)
                    problems.Add($"line {lineNo}: table '{current.Name}' has no ID column; every object table is expected to carry its primary key ID.");
                all.Add(current);
                tables.Add(current);
                continue;
            }
            if (current is null)
            {
                problems.Add($"line {lineNo}: a data line before any table header (\"$$Class;column(type)...\"): '{Shorten(line)}'. Comment syntax is UNCONFIRMED and none is accepted.");
                continue;
            }
            if (!TrySplit(line, 0, lineNo, problems, out string[]? values)) continue;
            if (values!.Length != current.Columns.Count)
            {
                problems.Add($"line {lineNo}: table '{current.Name}' declares {current.Columns.Count} column(s) but the row has {values.Length}.");
                continue;
            }
            bool ok = true;
            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i];
                if (v.Length == 0) continue;
                DgsColumn c = current.Columns[i];
                if (c.Type == 'r' && !(double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)))
                { problems.Add($"line {lineNo}: column '{c.Name}' is real (r) but '{v}' is not a finite number (decimal point only; a decimal comma is UNCONFIRMED and refused)."); ok = false; }
                else if (c.Type == 'i' && !long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                { problems.Add($"line {lineNo}: column '{c.Name}' is integer (i) but '{v}' is not an integer."); ok = false; }
            }
            if (!ok) continue;
            var row = new DgsRow(current, lineNo, values);
            if (current.Name != "General")
            {
                string id = row.Id;
                if (id.Length == 0) { problems.Add($"line {lineNo}: table '{current.Name}' row has an empty ID."); continue; }
                if (seenIds.TryGetValue(id, out var first))
                { problems.Add($"line {lineNo}: ID '{id}' repeats the ID at line {first.Line} (table '{first.Table}'); IDs are unique across the whole file."); continue; }
                seenIds[id] = (current.Name, lineNo);
            }
            current.Rows.Add(row);
        }

        DgsTable? general = tables.FirstOrDefault(t => t.Name == "General");
        string? version = null;
        var gen = new List<(string, string, int)>();
        if (general is null) problems.Add("no General table: the version entry is mandatory in DGS and without it the format version is unknown.");
        else if (general.ColumnIndex("Descr") < 0 || general.ColumnIndex("Val") < 0)
            problems.Add($"line {general.HeaderLine}: the General table needs the columns Descr and Val (layout UNCONFIRMED; this is the one accepted).");
        else
        {
            foreach (DgsRow r in general.Rows)
            {
                string d = r.Text("Descr") ?? "", v = r.Text("Val") ?? "";
                if (d == "Version") version = v; else gen.Add((d, v, r.Line));
            }
            if (version is null) problems.Add($"line {general.HeaderLine}: the General table has no row with Descr = Version.");
            else if (version != SupportedVersion) problems.Add($"DGS version '{version}' is not supported: only {SupportedVersion} is described by the public sources used and any other version's layout is UNCONFIRMED.");
        }
        if (problems.Count > 0) throw new DgsFormatException(problems);
        return new DgsFile { Version = version!, General = gen, Tables = tables.Where(t => t.Name != "General").ToList() };
    }

    private static DgsTable? ReadHeader(string line, int lineNo, List<string> problems)
    {
        if (!TrySplit(line, 2, lineNo, problems, out string[]? parts)) return null;
        string name = parts![0].Trim();
        if (name.Length == 0) { problems.Add($"line {lineNo}: a table header with no class name."); return null; }
        var cols = new List<DgsColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool ok = true;
        for (int i = 1; i < parts.Length; i++)
        {
            string spec = parts[i].Trim();
            int open = spec.IndexOf('(');
            if (open <= 0 || !spec.EndsWith(')'))
            { problems.Add($"line {lineNo}: column specification '{spec}' is not of the form name(type) or name(type:length)."); ok = false; continue; }
            string cn = spec.Substring(0, open);
            string[] ty = spec.Substring(open + 1, spec.Length - open - 2).Split(':');
            if (ty[0].Length != 1 || "airp".IndexOf(ty[0][0]) < 0)
            { problems.Add($"line {lineNo}: column '{cn}' has type '{ty[0]}', which is not one of a, i, r, p (other DGS types are UNCONFIRMED and refused)."); ok = false; continue; }
            int? len = null;
            if (ty.Length == 2) { if (int.TryParse(ty[1], NumberStyles.None, CultureInfo.InvariantCulture, out int l)) len = l; else { problems.Add($"line {lineNo}: column '{cn}' has a bad length '{ty[1]}'."); ok = false; continue; } }
            else if (ty.Length > 2) { problems.Add($"line {lineNo}: column '{cn}' has a malformed type '{string.Join(":", ty)}'."); ok = false; continue; }
            if (!seen.Add(cn)) { problems.Add($"line {lineNo}: column '{cn}' is declared twice."); ok = false; continue; }
            cols.Add(new DgsColumn(cn, ty[0][0], len));
        }
        return ok ? new DgsTable(name, lineNo, cols) : null;
    }

    private static bool TrySplit(string line, int start, int lineNo, List<string> problems, out string[]? values)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                else sb.Append(c);
            }
            else if (c == '"' && !wasQuoted && sb.ToString().Trim().Length == 0) { quoted = true; wasQuoted = true; sb.Clear(); }
            else if (c == Delimiter) { list.Add(wasQuoted ? sb.ToString() : sb.ToString().Trim()); sb.Clear(); wasQuoted = false; }
            else if (wasQuoted)
            {
                if (!char.IsWhiteSpace(c)) { problems.Add($"line {lineNo}: text after a closing quote."); values = null; return false; }
            }
            else sb.Append(c);
        }
        if (quoted) { problems.Add($"line {lineNo}: an unterminated double quote."); values = null; return false; }
        list.Add(wasQuoted ? sb.ToString() : sb.ToString().Trim());
        values = list.ToArray();
        return true;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : string.Concat(s.AsSpan(0, 57), "...");
}
