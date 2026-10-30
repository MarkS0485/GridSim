using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.PyPsa;

/// <summary>
/// A small, strict reader for the comma-separated files PyPSA (and PyPSA-GB) write: buses.csv, lines.csv, links.csv
/// (S2 / P02, October 2026).
/// </summary>
/// <remarks>
/// Strict on purpose (fail loud, PLAN.md rule 3): the header must be EXACTLY the columns the importer reads, in that
/// order, so a file with a renamed, missing or extra column is refused naming the file rather than read into the wrong
/// fields; every row must have the header's number of fields; numbers are invariant culture and must be finite. Quoted
/// fields ("a, b" and doubled quotes) are accepted, as PyPSA's pandas writer produces them when a name holds a comma.
/// A UTF-8 byte-order mark is ignored; blank lines are skipped.
/// </remarks>
internal static class PypsaCsv
{
    /// <summary>One data row: the fields in header order and the 1-based line number in the file (for messages).</summary>
    internal sealed record Row(string[] Fields, int Line);

    /// <summary>
    /// Read <paramref name="path"/>, requiring its header to be exactly <paramref name="header"/>. Returns the data rows.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header differs, a row has the wrong number of fields, or a quote is
    /// left open.</exception>
    public static IReadOnlyList<Row> Read(string path, IReadOnlyList<string> header)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"PyPSA file not found: '{path}' (full path {Path.GetFullPath(path)}).", path);
        string file = Path.GetFileName(path);
        string[] lines = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0) throw new InvalidDataException($"{file}: the file is empty (a header row is required: {string.Join(",", header)}).");
        string[] got = Split(lines[first], file, first + 1);
        if (!got.SequenceEqual(header, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"{file}: header is '{string.Join(",", got)}' but this importer reads exactly '{string.Join(",", header)}' (same names, same order).");

        var rows = new List<Row>();
        for (int i = first + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            string[] f = Split(lines[i], file, i + 1);
            if (f.Length != header.Count)
                throw new InvalidDataException($"{file} line {i + 1}: {f.Length} field(s), but the header has {header.Count}.");
            rows.Add(new Row(f, i + 1));
        }
        return rows;
    }

    /// <summary>Parse a finite number from field <paramref name="column"/> of a row, naming file, line and column on
    /// failure.</summary>
    public static double Number(Row row, int column, string columnName, string file)
    {
        string s = row.Fields[column];
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"{file} line {row.Line}: column '{columnName}' is '{s}', which is not a finite number.");
        return v;
    }

    /// <summary>Split one CSV line into fields (RFC 4180 quoting: a quoted field may hold commas and "" for a quote).</summary>
    private static string[] Split(string line, string file, int lineNo)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        if (quoted) throw new InvalidDataException($"{file} line {lineNo}: a quoted field is not closed.");
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
