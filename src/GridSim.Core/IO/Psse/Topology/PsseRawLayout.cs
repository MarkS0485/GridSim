namespace GridSim.Core.IO.Psse.Topology;

/// <summary>One data line of a RAW file as the incumbent lexer splits it, with its 1-based line number and section.</summary>
internal sealed record PsseRawLine(int Line, string Section, IReadOnlyList<string?> Fields);

/// <summary>
/// Where each section's lines sit in a RAW text. Built with the incumbent lexer (<see cref="PsseText"/>,
/// <see cref="PsseLineCursor"/>) and the incumbent section order (<see cref="PsseRawParser.SectionsFor"/>), following
/// the same header, title and terminator rules as <see cref="PsseRawParser"/>. It reads no values: the parser remains
/// the only authority on what a record means. It exists so additive topology paths can rewrite or replace whole lines
/// without touching the parser.
/// </summary>
internal sealed record PsseRawLayout(
    string[] Lines,
    IReadOnlyList<PsseRawLine> DataLines,
    IReadOnlyDictionary<string, int> TerminatorLines,
    int? SubstationFirstLine,
    int? SubstationTerminatorLine)
{
    public const string Bus = "BUS", Area = "AREA", TwoTerminalDc = "TWO-TERMINAL DC", VscDc = "VSC DC",
        MultiTerminalDc = "MULTI-TERMINAL DC", MultiSection = "MULTI-SECTION LINE", Facts = "FACTS DEVICE",
        Gne = "GNE DEVICE", Induction = "INDUCTION MACHINE", Substation = "SUBSTATION", SystemWide = "SYSTEM-WIDE";

    public static PsseRawLayout Read(string text, int revision)
    {
        string[] lines = PsseText.Lines(text);
        int h = 0;
        while (h < lines.Length && (lines[h].Trim().Length == 0 || PsseText.IsHeading(lines[h]))) h++;
        if (h >= lines.Length) throw new InvalidDataException("PSS/E RAW text has no case identification line.");
        int at = h + 1;
        for (int k = 0; k < 2; k++)
        {
            while (at < lines.Length && PsseText.IsHeading(lines[at])) at++;
            if (at >= lines.Length) throw new InvalidDataException("PSS/E RAW text ends before the two title lines.");
            at++;
        }

        var data = new List<PsseRawLine>();
        var terminators = new Dictionary<string, int>(StringComparer.Ordinal);
        int? substationFirst = null, substationEnd = null;
        var cur = new PsseLineCursor(lines, at);
        IReadOnlyList<string> sections = PsseRawParser.SectionsFor(revision);
        bool ended = false;
        for (int si = 0; si < sections.Count && !ended; si++)
        {
            string key = sections[si];
            while (true)
            {
                PsseRecord? r = cur.Next(key);
                if (r is null || r.IsEndOfData) { ended = true; break; }
                if (r.IsTerminator)
                {
                    terminators[key] = r.Line;
                    if (key == Substation) substationEnd = r.Line;
                    break;
                }
                if (key == SystemWide && !char.IsLetter(r.Raw(0)?[0] ?? ' '))
                {
                    cur = new PsseLineCursor(lines, r.Line - 1);
                    break;
                }
                data.Add(new PsseRawLine(r.Line, key, r.Values));
                if (key == Substation)
                {
                    substationFirst ??= r.Line;
                    // A substation record is followed by its node, switching device and terminal data, each ended by 0.
                    for (int part = 0; part < 3; part++)
                        while (true)
                        {
                            PsseRecord inner = cur.Next(key) ?? throw r.Error("the file ends inside this substation's data");
                            if (inner.IsEndOfData) throw inner.Error("the end-of-data record comes inside a substation's data");
                            if (inner.IsTerminator) break;
                            data.Add(new PsseRawLine(inner.Line, key, inner.Values));
                        }
                }
            }
        }
        if (substationFirst is not null && substationEnd is null)
            throw new InvalidDataException("PSS/E RAW substation data has no section terminator.");
        return new PsseRawLayout(lines, Array.AsReadOnly(data.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(terminators), substationFirst, substationEnd);
    }

    /// <summary>One record written back as a RAW line. Numeric tokens are written as read; any other non-empty field is
    /// quoted so the incumbent lexer returns exactly the same field text.</summary>
    public static string FormatRecord(IEnumerable<string?> fields) => string.Join(", ", fields.Select(FormatField));

    public static string FormatField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (IsPlainNumber(field)) return field;
        if (!field.Contains('\'')) return "'" + field + "'";
        if (!field.Contains('"')) return "\"" + field + "\"";
        throw new InvalidDataException($"A PSS/E field cannot hold both quote characters: {field}");
    }

    private static bool IsPlainNumber(string s)
    {
        int i = 0, n = s.Length;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        int digits = 0;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < n && s[i] == '.') { i++; while (i < n && char.IsAsciiDigit(s[i])) { i++; digits++; } }
        if (digits == 0) return false;
        if (i < n && (s[i] is 'E' or 'e' or 'D' or 'd'))
        {
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            int exp = 0;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; exp++; }
            if (exp == 0) return false;
        }
        return i == n;
    }
}
