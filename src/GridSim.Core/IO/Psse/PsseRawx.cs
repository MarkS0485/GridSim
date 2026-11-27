using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// Reads a PSS/E RAWX (JSON) power-flow file by writing it out as the revision 35 RAW text the RAW reader reads, so a case
/// has one set of rules whichever way it arrives.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> A RAWX file is one JSON object with a <c>network</c> member. Each section of the network is an
/// object with <c>fields</c> (the field names) and <c>data</c>: <c>caseid</c> holds one flat array of values, every other
/// section an array of rows. Fields are found by name, case-insensitively, so a file may list them in any order, leave
/// out the ones whose default applies, and add fields (such as the revision 36 NAME and DROOPNAME) that are then ignored.
/// Text is a JSON string; <c>null</c> leaves a field to its default.</para>
/// <para><b>Sections read:</b> caseid, bus, load, fixshunt, generator, acline, sysswd, transformer, area, zone, owner,
/// swshunt and indmach; and (RC5 WP34) twotermdc, vscdc, impcor, ntermdc with its ntermdcconv, ntermdcbus and ntermdclink
/// tables, msline, iatransfer and facts. Sections for the revision 36 tables GridSim has no model for (droop control,
/// rating tables, load types, interfaces) are read past and noted. Any other section that holds rows (GNE and substation
/// data) is refused, naming it, rather than dropped: write those cases as RAW. The case identification's REV must be 35
/// or 36 (or absent, read as 35).</para>
/// <para><b>The device sections</b> (RC5 WP34) follow the RAWX layout of PowSyBl's PSS/E model and its RAWX test case,
/// checked against surge-io's RAWX reader (see <see cref="PsseFieldRemap.TwoTerminalLine"/>): a two-terminal DC row holds
/// the link fields then the rectifier's and the inverter's converter fields with the suffixes R and I; a VSC DC row the
/// link fields then the two converters' with the suffixes 1 and 2; an impcor row is one point (itable, tap, refact,
/// imfact), the points of a table in rows of their own; a multi-terminal DC link is an ntermdc row, and its converters,
/// DC buses and DC links are rows of ntermdcconv, ntermdcbus and ntermdclink naming the link in their first field. Each
/// is rebuilt as the revision 35 RAW record and read by the RAW reader, so the meaning is the RAW reader's.</para>
/// </remarks>
internal static class PsseRawx
{
    private static readonly string[] SectionOrder =
    {
        "BUS", "LOAD", "FIXED SHUNT", "GENERATOR", "BRANCH", "SYSTEM SWITCHING DEVICE", "TRANSFORMER", "AREA", "TWO-TERMINAL DC",
        "VSC DC", "IMPEDANCE CORRECTION", "MULTI-TERMINAL DC", "MULTI-SECTION LINE", "ZONE", "INTER-AREA TRANSFER", "OWNER",
        "FACTS DEVICE", "SWITCHED SHUNT", "GNE DEVICE", "INDUCTION MACHINE", "SUBSTATION",
    };

    private static readonly string[] BenignKeywords = { "DROOP", "RATING", "LOADTYPE", "INTERFACE", "SWDRAT" };

    /// <summary>True for text that is a RAWX document (its first non-blank character is an opening brace).</summary>
    internal static bool LooksLikeRawx(string text)
    {
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == (char)0xFEFF) continue;
            return c == '{';
        }
        return false;
    }

    private sealed record Table(List<string> Names, List<List<string?>> Rows);

    /// <summary>The revision 35 RAW lines of a RAWX document, the revision it states, and notes.</summary>
    internal static (string[] Lines, int Revision) ToRaw(string json, List<string> notes)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { throw new InvalidDataException("PSS/E RAWX: the file is not valid JSON: " + e.Message); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("network", out JsonElement net)
                || net.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("PSS/E RAWX: the file has no 'network' object.");

            var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
            Table? caseId = null;
            foreach (JsonProperty p in net.EnumerateObject())
            {
                string key = p.Name.Trim().ToLowerInvariant();
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                if (!p.Value.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Array
                    || !p.Value.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"PSS/E RAWX: section '{p.Name}' needs 'fields' and 'data' arrays.");
                var names = new List<string>();
                foreach (JsonElement f in fields.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.String) throw new InvalidDataException($"PSS/E RAWX: section '{p.Name}' has a field name that is not text.");
                    names.Add(PsseFieldRemap.Key(f.GetString() ?? ""));
                }
                var rows = new List<List<string?>>();
                if (key == "caseid")
                {
                    rows.Add(data.EnumerateArray().Select(Cell).ToList());
                    caseId = new Table(names, rows);
                    continue;
                }
                int r = 0;
                foreach (JsonElement row in data.EnumerateArray())
                {
                    r++;
                    if (row.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException($"PSS/E RAWX: section '{p.Name}' row {r} is not an array.");
                    List<string?> cells = row.EnumerateArray().Select(Cell).ToList();
                    if (cells.Count > names.Count)
                        throw new InvalidDataException($"PSS/E RAWX: section '{p.Name}' row {r} has {cells.Count} values for {names.Count} fields.");
                    rows.Add(cells);
                }
                tables[key] = new Table(names, rows);
            }

            if (caseId is null || caseId.Rows.Count == 0) throw new InvalidDataException("PSS/E RAWX: the file has no 'caseid' record.");
            List<string> idn = caseId.Names;
            List<string?> idv = caseId.Rows[0];
            string? revText = PsseFieldRemap.ValueOf(idn, idv, "REV");
            int rev = 35;
            if (revText is not null)
            {
                if (!double.TryParse(revText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rd) || rd != Math.Floor(rd))
                    throw new InvalidDataException($"PSS/E RAWX: caseid REV is '{revText}'; it must be a whole number.");
                rev = (int)rd;
            }
            if (rev is not (35 or 36))
                throw new InvalidDataException($"PSS/E RAWX: caseid REV is {rev}; RAWX files are read as revision 35 or 36.");

            var outp = new List<string>();
            outp.Add(string.Join(",",
                PsseFieldRemap.ValueOf(idn, idv, "IC") ?? "0",
                PsseFieldRemap.ValueOf(idn, idv, "SBASE") ?? "100",
                "35",
                PsseFieldRemap.ValueOf(idn, idv, "XFRRAT") ?? "",
                PsseFieldRemap.ValueOf(idn, idv, "NXFRAT") ?? "",
                PsseFieldRemap.ValueOf(idn, idv, "BASFRQ") ?? "") + " / PSS/E-35 from RAWX");
            outp.Add(Title(OneLine(PsseFieldRemap.ValueOf(idn, idv, "TITLE1")), "TITLE1"));
            outp.Add(Title(OneLine(PsseFieldRemap.ValueOf(idn, idv, "TITLE2")), "TITLE2"));

            // Refuse what GridSim would otherwise drop without a word.
            var supported = new HashSet<string>(StringComparer.Ordinal)
            {
                "caseid", "bus", "load", "fixshunt", "generator", "acline", "sysswd", "transformer", "area", "zone", "owner",
                "swshunt", "indmach",
                // RC5 WP34
                "twotermdc", "vscdc", "impcor", "ntermdc", "ntermdcconv", "ntermdcbus", "ntermdclink", "msline", "iatransfer", "facts",
            };
            foreach (var kv in tables.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (supported.Contains(kv.Key) || kv.Value.Rows.Count == 0) continue;
                string upper = kv.Key.ToUpperInvariant();
                if (BenignKeywords.Any(k => upper.Contains(k, StringComparison.Ordinal)))
                {
                    notes.Add(FormattableString.Invariant($"RAWX: {kv.Value.Rows.Count} '{kv.Key}' row(s) read past (GridSim has no model for them)."));
                    continue;
                }
                throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: the '{kv.Key}' section holds {kv.Value.Rows.Count} row(s), which this reader does not read from RAWX; write the case as RAW to read them."));
            }

            for (int s = 0; s < SectionOrder.Length; s++)
            {
                string section = SectionOrder[s];
                foreach (string line in SectionLines(section, tables)) outp.Add(line);
                string next = s + 1 < SectionOrder.Length ? $", BEGIN {SectionOrder[s + 1]} DATA" : "";
                outp.Add($"0 / END OF {section} DATA{next}");
            }
            outp.Add("Q");
            notes.Add("read from a RAWX file: the sections were written as revision 35 RAW text field by field, by name.");
            return (outp.ToArray(), rev);
        }
    }

    private static string? Cell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => throw new InvalidDataException("PSS/E RAWX: a value is an array or an object; every value must be a number, text or null."),
    };

    private static string OneLine(string? title) => (title ?? "").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>IO-20: a title that begins with "@!" would be read as a heading line of the RAW text it is rewritten to,
    /// shifting the header by one line (the first record lost without an error); it is refused by name.</summary>
    private static string Title(string title, string field)
        => PsseText.IsHeading(title)
            ? throw new InvalidDataException($"RAWX caseid {field} begins with '@!', which the RAW reader takes as a heading line, not a title.")
            : title;

    private static IEnumerable<string> SectionLines(string section, Dictionary<string, Table> tables)
    {
        string key = section switch
        {
            "BUS" => "bus", "LOAD" => "load", "FIXED SHUNT" => "fixshunt", "GENERATOR" => "generator", "BRANCH" => "acline",
            "SYSTEM SWITCHING DEVICE" => "sysswd", "TRANSFORMER" => "transformer", "AREA" => "area", "ZONE" => "zone",
            "OWNER" => "owner", "SWITCHED SHUNT" => "swshunt", "INDUCTION MACHINE" => "indmach",
            // RC5 WP34
            "TWO-TERMINAL DC" => "twotermdc", "VSC DC" => "vscdc", "IMPEDANCE CORRECTION" => "impcor", "MULTI-TERMINAL DC" => "ntermdc",
            "MULTI-SECTION LINE" => "msline", "INTER-AREA TRANSFER" => "iatransfer", "FACTS DEVICE" => "facts", _ => "",
        };
        if (key == "ntermdc") return MultiTerminalLines(tables);
        if (key == "impcor") return tables.TryGetValue(key, out Table? zt) ? ImpedanceTableLines(zt) : Array.Empty<string>();
        if (key.Length == 0 || !tables.TryGetValue(key, out Table? t)) return Array.Empty<string>();
        return RowLines(key, t);
    }

    private static IEnumerable<string> RowLines(string key, Table t)
    {
        string[]? spec = key switch
        {
            "bus" => PsseFieldRemap.Bus, "load" => PsseFieldRemap.Load, "fixshunt" => PsseFieldRemap.FixedShunt,
            "generator" => PsseFieldRemap.Generator, "acline" => PsseFieldRemap.Branch, "sysswd" => PsseFieldRemap.SwitchingDevice,
            "area" => PsseFieldRemap.Area, "zone" => PsseFieldRemap.Zone, "owner" => PsseFieldRemap.Owner,
            "swshunt" => PsseFieldRemap.SwitchedShunt, "indmach" => PsseFieldRemap.InductionMachine,
            "msline" => PsseFieldRemap.MultiSectionLine, "iatransfer" => PsseFieldRemap.InterAreaTransfer, "facts" => PsseFieldRemap.Facts,
            _ => null,
        };
        int r = 0;
        foreach (List<string?> row in t.Rows)
        {
            r++;
            if (key == "transformer")
            {
                foreach (string l in TransformerLines(t.Names, row)) yield return l;
                continue;
            }
            if (key == "twotermdc")
            {
                foreach (string l in LinkLines(key, r, t.Names, row, PsseFieldRemap.TwoTerminalLine, PsseFieldRemap.TwoTerminalConverter, "R", "I", "IP")) yield return l;
                continue;
            }
            if (key == "vscdc")
            {
                foreach (string l in LinkLines(key, r, t.Names, row, PsseFieldRemap.VscLine, PsseFieldRemap.VscConverter, "1", "2", "IBUS")) yield return l;
                continue;
            }
            yield return PsseFieldRemap.Line(spec!, PsseFieldRemap.Reorder(spec!, t.Names, row));
        }
    }

    /// <summary>A two-terminal or VSC DC row as its three RAW lines: the link line, then the two converters, whose fields
    /// carry <paramref name="first"/> and <paramref name="second"/> as a suffix. A row without both converter buses is
    /// refused, naming the section, rather than read as a link whose converters default.</summary>
    private static IEnumerable<string> LinkLines(string key, int r, List<string> names, List<string?> row, string[] line, string[] converter,
        string first, string second, string busField)
    {
        if (PsseFieldRemap.ValueOf(names, row, busField + first) is null || PsseFieldRemap.ValueOf(names, row, busField + second) is null)
            throw new InvalidDataException(FormattableString.Invariant(
                $"PSS/E RAWX: '{key}' row {r} ('{PsseFieldRemap.ValueOf(names, row, "NAME") ?? ""}') has no {busField.ToLowerInvariant()}{first.ToLowerInvariant()} or {busField.ToLowerInvariant()}{second.ToLowerInvariant()} (the converter buses), so its converters cannot be placed."));
        yield return PsseFieldRemap.Line(line, PsseFieldRemap.Reorder(line, names, row));
        foreach (string s in new[] { first, second })
        {
            string[] spec = PsseFieldRemap.Suffixed(converter, s);
            yield return PsseFieldRemap.Line(spec, PsseFieldRemap.Reorder(spec, names, row));
        }
    }

    /// <summary>The impcor rows (one point each: itable, tap, refact, imfact) as revision 35 tables: the table number, then
    /// each point's T, Re F, Im F, ended by 0, 0, 0, on one line; tables in the order of their first row.</summary>
    private static IEnumerable<string> ImpedanceTableLines(Table t)
    {
        var order = new List<string>();
        var points = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int r = 0;
        foreach (List<string?> row in t.Rows)
        {
            r++;
            string? table = PsseFieldRemap.ValueOf(t.Names, row, "ITABLE|I");
            if (table is null) throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: 'impcor' row {r} has no itable (the table number)."));
            string? tap = PsseFieldRemap.ValueOf(t.Names, row, "TAP|T");
            string? re = PsseFieldRemap.ValueOf(t.Names, row, "REFACT");
            if (tap is null || re is null) throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: 'impcor' row {r} (table {table}) needs tap and refact."));
            string im = PsseFieldRemap.ValueOf(t.Names, row, "IMFACT") ?? "0";
            if (!points.TryGetValue(table, out List<string>? list)) { list = new List<string>(); points[table] = list; order.Add(table); }
            list.Add(tap + "," + re + "," + im);
        }
        foreach (string table in order) yield return table + "," + string.Join(",", points[table]) + ",0,0,0";
    }

    /// <summary>Each ntermdc row as its revision 35 record: the link line, then its converters, DC buses and DC links, the
    /// rows of ntermdcconv, ntermdcbus and ntermdclink whose first field (name) names the link, in file order. The counts
    /// NCONV, NDCBS and NDCLN must match those rows, and a row naming no link is refused.</summary>
    private static List<string> MultiTerminalLines(Dictionary<string, Table> tables)
    {
        var outp = new List<string>();
        tables.TryGetValue("ntermdc", out Table? main);
        var sub = new (string Key, string Count, string[] Spec)[]
        {
            ("ntermdcconv", "NCONV", PsseFieldRemap.MultiTerminalConverter),
            ("ntermdcbus", "NDCBS", PsseFieldRemap.MultiTerminalBus),
            ("ntermdclink", "NDCLN", PsseFieldRemap.MultiTerminalLink),
        };
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (main is not null)
        {
            int r = 0;
            foreach (List<string?> row in main.Rows)
            {
                r++;
                string name = (PsseFieldRemap.ValueOf(main.Names, row, "NAME") ?? "").Trim();
                if (name.Length == 0 || !names.Add(name))
                    throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: 'ntermdc' row {r} needs a name of its own (it is '{name}')."));
                outp.Add(PsseFieldRemap.Line(PsseFieldRemap.MultiTerminalLine, PsseFieldRemap.Reorder(PsseFieldRemap.MultiTerminalLine, main.Names, row)));
                foreach ((string key, string count, string[] spec) in sub)
                {
                    var mine = new List<List<string?>>();
                    if (tables.TryGetValue(key, out Table? t))
                        foreach (List<string?> s in t.Rows)
                            if ((PsseFieldRemap.ValueOf(t.Names, s, "NAME") ?? "").Trim() == name) mine.Add(s);
                    string? declared = PsseFieldRemap.ValueOf(main.Names, row, count);
                    if (declared is null || !int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n != mine.Count)
                        throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: 'ntermdc' link '{name}' gives {count.ToLowerInvariant()} {declared ?? "(none)"} but '{key}' holds {mine.Count} row(s) naming it."));
                    foreach (List<string?> s in mine)
                        outp.Add(PsseFieldRemap.Line(spec, PsseFieldRemap.Reorder(spec, t!.Names, s)));
                    used[key] = used.GetValueOrDefault(key) + mine.Count;
                }
            }
        }
        foreach (var (key, _, _) in sub)
            if (tables.TryGetValue(key, out Table? t) && t.Rows.Count != used.GetValueOrDefault(key))
                throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAWX: '{key}' holds {t.Rows.Count - used.GetValueOrDefault(key)} row(s) naming no 'ntermdc' link."));
        return outp;
    }

    private static IEnumerable<string> TransformerLines(List<string> names, List<string?> row)
    {
        string? k = PsseFieldRemap.ValueOf(names, row, "K|KBUS");
        bool three = k is not null && double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out double kv) && kv != 0;
        string[] l1 = PsseFieldRemap.TransformerLine1;
        yield return PsseFieldRemap.Line(l1, PsseFieldRemap.Reorder(l1, names, row));
        string[] l2 = PsseFieldRemap.TransformerLine2;
        yield return PsseFieldRemap.Line(l2, PsseFieldRemap.Reorder(l2, names, row));
        for (int w = 1; w <= (three ? 3 : 2); w++)
        {
            string[] spec = PsseFieldRemap.TransformerWinding(w);
            List<string?> vals = PsseFieldRemap.Reorder(spec, names, row);
            if (!three && w == 2 && vals.Count > 2) vals = vals.GetRange(0, 2);   // a two-winding unit's second line is WINDV2, NOMV2
            yield return PsseFieldRemap.Line(spec, vals);
        }
    }
}
