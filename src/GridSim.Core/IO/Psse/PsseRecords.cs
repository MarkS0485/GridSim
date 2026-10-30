using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// One data record of a PSS/E file as written: its fields (strings, already unquoted), the line it came from and the
/// section it belongs to, so every error can name the line, the section and the field (Tranche A, October 2026).
/// </summary>
/// <remarks>
/// <para><b>Fields.</b> PSS/E separates fields by a comma or by one or more blanks; a comma with blanks round it is one
/// separator, and two commas with nothing between them leave that field empty (its default applies). A field may be a
/// quoted string (single quotes, or double quotes as revision 35 writes its RATING lines), which can hold blanks,
/// commas and slashes. A slash outside quotes starts the end-of-line comment. Trailing fields may be left off: their
/// defaults apply. A field the reader needs and has no default for is refused, naming it.</para>
/// <para><b>Numbers</b> are read with the invariant culture. A Fortran double exponent (<c>1.0D+00</c>) is accepted. Not
/// a number, infinity, a fraction where a whole number is required, or text where a number is required is refused.</para>
/// </remarks>
public sealed class PsseRecord
{
    private readonly IReadOnlyList<string?> _fields;
    private readonly string _file;

    internal PsseRecord(IReadOnlyList<string?> fields, int line, string section, string? comment, string file = "RAW")
    {
        _fields = fields;
        Line = line;
        Section = section;
        Comment = comment;
        _file = file;
    }

    /// <summary>The 1-based line number in the file.</summary>
    public int Line { get; }

    /// <summary>The section this record belongs to (for example "BUS" or "TRANSFORMER").</summary>
    public string Section { get; }

    /// <summary>The end-of-line comment (the text after an unquoted slash), trimmed, or null when there is none.</summary>
    public string? Comment { get; }

    /// <summary>How many fields the line holds (an empty field between two commas counts; trailing ones left off do not).</summary>
    public int Count => _fields.Count;

    /// <summary>The raw field text, or null when the field is left off or empty.</summary>
    public string? Raw(int index) => index < _fields.Count && !string.IsNullOrEmpty(_fields[index]) ? _fields[index] : null;

    /// <summary>True when the field is present and not empty.</summary>
    public bool Has(int index) => Raw(index) is not null;

    /// <summary>True when this is a section terminator: a line whose only field is the integer 0 (a comment may follow).
    /// A data line that merely starts with 0 (a transformer impedance line with R written as 0) has more fields and is data.</summary>
    public bool IsTerminator => _fields.Count == 1 && _fields[0] == "0";

    /// <summary>True when this is the end-of-data record (Q).</summary>
    public bool IsEndOfData => _fields.Count >= 1 && string.Equals(_fields[0], "Q", StringComparison.OrdinalIgnoreCase);

    /// <summary>A real field. Left off or empty: <paramref name="dflt"/>, or refused when no default is given.</summary>
    public double Real(int index, string name, double? dflt = null)
    {
        string? s = Raw(index);
        if (s is null)
            return dflt ?? throw Error($"field {index + 1} ({name}) is missing and has no default");
        return ParseReal(s, index, name);
    }

    /// <summary>A whole-number field (ids, codes, statuses). Left off or empty: <paramref name="dflt"/>, or refused when no
    /// default is given. A fraction, or a number outside the int range, is refused.</summary>
    public int Int(int index, string name, int? dflt = null)
    {
        string? s = Raw(index);
        if (s is null)
            return dflt ?? throw Error($"field {index + 1} ({name}) is missing and has no default");
        double v = ParseReal(s, index, name);
        if (v != Math.Floor(v) || v < int.MinValue || v > int.MaxValue)
            throw Error($"field {index + 1} ({name}) is '{s}'; it must be a whole number");
        return (int)v;
    }

    /// <summary>A string field, trimmed of the blanks PSS/E pads names with. Left off or empty: <paramref name="dflt"/>.</summary>
    public string Str(int index, string dflt = "") => Raw(index)?.Trim() ?? dflt;

    /// <summary>A status-like code that must be one of <paramref name="allowed"/>.</summary>
    public int Code(int index, string name, int dflt, params int[] allowed)
    {
        int v = Int(index, name, dflt);
        if (Array.IndexOf(allowed, v) < 0)
            throw Error($"field {index + 1} ({name}) is {v}; it must be one of {string.Join(", ", allowed)}");
        return v;
    }

    /// <summary>An error naming this record's line and section.</summary>
    public InvalidDataException Error(string what) =>
        new($"PSS/E {_file} line {Line} ({Section} data): {what}.");

    private double ParseReal(string s, int index, string name)
    {
        // A Fortran double-precision exponent (1.0D+00) is a plain exponent here.
        string t = s.Replace('D', 'E').Replace('d', 'e');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw Error($"field {index + 1} ({name}) is '{s}'; it must be a finite number");
        return v;
    }
}

/// <summary>
/// Splits PSS/E text into lines and fields (Tranche A). Shared by the RAW, DYR and SEQ readers so all three read fields,
/// quotes and comments the same way.
/// </summary>
public static class PsseText
{
    /// <summary>The lines of <paramref name="text"/>, line endings removed (CR LF, LF or CR).</summary>
    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>True for a line PSS/E writes as a column heading (<c>@!</c> first) and readers skip.</summary>
    public static bool IsHeading(string line) => line.TrimStart().StartsWith("@!", StringComparison.Ordinal);

    /// <summary>
    /// Split one line into fields and its comment. An empty field between two commas is returned as an empty string; a
    /// quoted field keeps its text exactly (quotes removed). A quote that is never closed is refused.
    /// </summary>
    public static (List<string?> Fields, string? Comment) Split(string line, int lineNumber, string section, string file = "RAW")
    {
        var fields = new List<string?>();
        var cur = new StringBuilder();
        bool inToken = false;         // characters of an unquoted token or a closed quoted token are pending
        bool closedByBlank = false;   // the last token ended at a blank, so a comma after it is that token's separator
        string? comment = null;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '\'' || c == '"')
            {
                int close = line.IndexOf(c, i + 1);
                if (close < 0)
                    throw new InvalidDataException($"PSS/E {file} line {lineNumber} ({section} data): a {(c == '"' ? "double" : "single")} quote is never closed.");
                cur.Append(line, i + 1, close - i - 1);
                inToken = true;
                closedByBlank = false;
                i = close + 1;
                continue;
            }
            if (c == '/')
            {
                comment = line[(i + 1)..].Trim();
                break;
            }
            if (c == ',')
            {
                if (inToken) fields.Add(cur.ToString());
                else if (!closedByBlank) fields.Add("");   // two separators with nothing between: an empty (default) field
                cur.Clear();
                inToken = false;
                closedByBlank = false;
                i++;
                continue;
            }
            if (c == ' ' || c == '\t')
            {
                if (inToken)
                {
                    fields.Add(cur.ToString());
                    cur.Clear();
                    inToken = false;
                    closedByBlank = true;
                }
                i++;
                continue;
            }
            cur.Append(c);
            inToken = true;
            closedByBlank = false;
            i++;
        }
        if (inToken) fields.Add(cur.ToString());
        return (fields, comment);
    }
}

/// <summary>
/// Walks the lines of a PSS/E file record by record (Tranche A): skips <c>@!</c> heading lines and blank lines, and
/// tracks the line number so each record can name it.
/// </summary>
internal sealed class PsseLineCursor
{
    private readonly string[] _lines;
    private readonly string _file;   // "RAW" or "SEQ", for the messages
    private int _next;   // index of the next line to look at

    public PsseLineCursor(string[] lines, int start, string file = "RAW")
    {
        _lines = lines;
        _next = start;
        _file = file;
    }

    /// <summary>The next data record in <paramref name="section"/>, or null at the end of the text.</summary>
    public PsseRecord? Next(string section)
    {
        while (_next < _lines.Length)
        {
            string line = _lines[_next++];
            if (line.Trim().Length == 0 || PsseText.IsHeading(line)) continue;
            (List<string?> fields, string? comment) = PsseText.Split(line, _next, section, _file);
            if (fields.Count == 0) continue;   // a comment-only line
            return new PsseRecord(fields, _next, section, comment, _file);
        }
        return null;
    }

    /// <summary>The next record of a multi-line record, refused when the file ends or a section terminator appears first.</summary>
    public PsseRecord Continuation(string section, PsseRecord first, string what)
    {
        PsseRecord? r = Next(section);
        if (r is null)
            throw first.Error($"the file ends inside this record ({what} is missing)");
        if (r.IsTerminator || r.IsEndOfData)
            throw first.Error($"the section ends at line {r.Line} inside this record ({what} is missing)");
        return r;
    }
}
