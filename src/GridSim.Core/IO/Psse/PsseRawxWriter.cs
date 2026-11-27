using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// Writes a <see cref="GridModel"/> as a PSS/E RAWX (JSON) file, revision 35 (RC5 WP34).
/// </summary>
/// <remarks>
/// <para><b>What is written.</b> Exactly the records <see cref="PsseRawWriter"/> writes for revision 35, with the same
/// refusals and notes: the writer writes the revision 35 RAW text and re-expresses each record as a RAWX row, field by
/// field in the revision 35 order. So RAWX carries what RAW carries, no more and no less.</para>
/// <para><b>Layout.</b> One JSON object <c>{"network": {...}}</c>; each section an object with <c>fields</c> (the names)
/// and <c>data</c> (<c>caseid</c> one flat array, every other section an array of rows). Section keys and field names
/// are those of PowSyBl's PSS/E model (powsybl-core, MPL-2.0: PowerFlowRecordGroup and the FIELD_NAMES_35X lists) and its
/// RAWX test case IEEE_14_bus_completed_rev35.rawx, checked against surge-io's RAWX reader (psse/rawx.rs); the device
/// sections are listed at <see cref="PsseFieldRemap.TwoTerminalLine"/>. A transformer is one row (identification,
/// impedance and winding fields, the windings' with the suffixes 1 and 2 and the ratings as wdg1rate1 ...); a two-terminal
/// DC row carries the rectifier's and inverter's fields with the suffixes r and i, a VSC row its converters' with 1 and 2;
/// a multi-terminal link's converters, DC buses and DC links are rows of ntermdcconv, ntermdcbus and ntermdclink whose
/// first field names the link. Sections with no rows are not written.</para>
/// <para><b>Exactness.</b> A number is written as the very token the RAW writer wrote (round-trip "R" form, invariant
/// culture), which is a valid JSON number, and the RAWX reader hands that token back unchanged, so a write-read gives the
/// RAW revision 35 write-read's model bit for bit. Text is a JSON string, escaped only where JSON requires it.</para>
/// </remarks>
public static class PsseRawxWriter
{
    // The RAWX field names of each RAW line, in the revision 35 order the RAW writer writes; a trailing * marks text.
    private static readonly string[] Rates = Enumerable.Range(1, 12).Select(k => $"rate{k}").ToArray();
    private static readonly string[] Owners = Enumerable.Range(1, 4).SelectMany(k => new[] { $"o{k}", $"f{k}" }).ToArray();

    private static string[] Cat(params IEnumerable<string>[] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly string[] BusF = { "ibus", "name*", "baskv", "ide", "area", "zone", "owner", "vm", "va", "nvhi", "nvlo", "evhi", "evlo" };
    private static readonly string[] LoadF =
        { "ibus", "loadid*", "stat", "area", "zone", "pl", "ql", "ip", "iq", "yp", "yq", "owner", "scale", "intrpt", "dgenp", "dgenq", "dgenm", "loadtype*" };
    private static readonly string[] FixShuntF = { "ibus", "shntid*", "stat", "gl", "bl" };
    private static readonly string[] GeneratorF = Cat(
        new[] { "ibus", "machid*", "pg", "qg", "qt", "qb", "vs", "ireg", "nreg", "mbase", "zr", "zx", "rt", "xt", "gtap", "stat", "rmpct", "pt", "pb", "baslod" },
        Owners, new[] { "wmod", "wpf" });
    private static readonly string[] AclineF = Cat(
        new[] { "ibus", "jbus", "ckt*", "rpu", "xpu", "bpu", "name*" }, Rates, new[] { "gi", "bi", "gj", "bj", "stat", "met", "len" }, Owners);
    private static readonly string[] SysswdF = Cat(new[] { "ibus", "jbus", "ckt*", "xpu" }, Rates, new[] { "stat", "nstat", "met", "stype", "name*" });
    private static readonly string[] XfLine1F = Cat(
        new[] { "ibus", "jbus", "kbus", "ckt*", "cw", "cz", "cm", "mag1", "mag2", "nmet", "name*", "stat" }, Owners, new[] { "vecgrp*", "zcod" });
    private static readonly string[] XfLine2F = { "r1_2", "x1_2", "sbase1_2", "r2_3", "x2_3", "sbase2_3", "r3_1", "x3_1", "sbase3_1", "vmstar", "anstar" };
    private static string[] XfWindingF(int w) => Cat(
        new[] { $"windv{w}", $"nomv{w}", $"ang{w}" }, Rates.Select(r => $"wdg{w}{r}"),
        new[] { $"cod{w}", $"cont{w}", $"node{w}", $"rma{w}", $"rmi{w}", $"vma{w}", $"vmi{w}", $"ntp{w}", $"tab{w}", $"cr{w}", $"cx{w}", $"cnxa{w}" });
    private static readonly string[] AreaF = { "iarea", "isw", "pdes", "ptol", "arname*" };
    private static readonly string[] ZoneF = { "izone", "zoname*" };
    private static readonly string[] OwnerF = { "iowner", "owname*" };
    private static readonly string[] TwoTermF = { "name*", "mdc", "rdc", "setvl", "vschd", "vcmod", "rcomp", "delti", "met*", "dcvmin", "cccitmx", "cccacc" };
    private static string[] TwoTermConvF(string s) =>
        new[] { "ip", "nb", "anmx", "anmn", "rc", "xc", "ebas", "tr", "tap", "tmx", "tmn", "stp", "ic", "nd", "if", "it", "id*", "xcap" }
            .Select(n => n.EndsWith('*') ? n[..^1] + s + "*" : n + s).ToArray();
    private static readonly string[] VscF = Cat(new[] { "name*", "mdc", "rdc" }, Owners);
    private static string[] VscConvF(string s) =>
        new[] { "ibus", "type", "mode", "dcset", "acset", "aloss", "bloss", "minloss", "smax", "imax", "pwf", "maxq", "minq", "vsreg", "nreg", "rmpct" }
            .Select(n => n + s).ToArray();
    private static readonly string[] NtermF = { "name*", "nconv", "ndcbs", "ndcln", "mdc", "vconv", "vcmod", "vconvn" };
    private static readonly string[] NtermConvF = { "ib", "n", "angmx", "angmn", "rc", "xc", "ebas", "tr", "tap", "tpmx", "tpmn", "tstp", "setvl", "dcpf", "marg", "cnvcod" };
    private static readonly string[] NtermBusF = { "idc", "ib", "area", "zone", "dcname*", "idc2", "rgrnd", "owner" };
    private static readonly string[] NtermLinkF = { "idc", "jdc", "dcckt*", "met", "rdc", "ldc" };
    private static readonly string[] FactsF =
    {
        "name*", "ibus", "jbus", "mode", "pdes", "qdes", "vset", "shmx", "trmx", "vtmn", "vtmx", "vsmx", "imx", "linx", "rmpct", "owner",
        "set1", "set2", "vsref", "fcreg", "nreg", "mname*"
    };
    private static readonly string[] SwShuntF = Cat(
        new[] { "ibus", "shntid*", "modsw", "adjm", "stat", "vswhi", "vswlo", "swreg", "nreg", "rmpct", "rmidnt*", "binit" },
        Enumerable.Range(1, 8).SelectMany(k => new[] { $"s{k}", $"n{k}", $"b{k}" }));
    private static readonly string[] IndMachF =
    {
        "ibus", "imid*", "stat", "scode", "dcode", "area", "zone", "owner", "tcode", "bcode", "mbase", "ratekv", "pcode", "pset", "hconst",
        "aconst", "bconst", "dconst", "econst", "ra", "xa", "xm", "r1", "x1", "r2", "x2", "x3", "e1", "se1", "e2", "se2", "ia1", "ia2", "xamult"
    };

    /// <summary>A JSON number as the RAW writer writes one (invariant "R" form): sign, digits, optional fraction and
    /// exponent. Checked for every number so the output is valid JSON.</summary>
    private static readonly Regex JsonNumber = new(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>Indented, with the relaxed encoder: text is escaped only where JSON requires it (quote, backslash, control
    /// characters), so an id such as <c>&amp;1</c> is written as it is, not as <c>&amp;</c>. The output is a file, not HTML,
    /// so the HTML-sensitive characters the default encoder also escapes need no escaping.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One RAWX section being built: its field names (the longest row's) and rows of cells (text, number token or null).</summary>
    private sealed class Section
    {
        public Section(string key) => Key = key;
        public string Key { get; }
        public List<string> Fields { get; } = new();
        public List<List<(string? Value, bool Text)>> Rows { get; } = new();
    }

    /// <summary>Write <paramref name="model"/> as RAWX (revision 35). Refuses what <see cref="PsseRawWriter"/> refuses.</summary>
    public static PsseWriteResult Write(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        PsseWriteResult raw = PsseRawWriter.Write(model, 35);
        string[] lines = PsseText.Lines(raw.Text);
        if (lines.Length < 4) throw new InvalidOperationException("PSS/E RAWX writer: the revision 35 text is shorter than its header.");

        (List<string?> id, _) = PsseText.Split(lines[0], 1, "CASE IDENTIFICATION");
        if (id.Count < 6 || id[2] != "35") throw new InvalidOperationException("PSS/E RAWX writer: the revision 35 header was not in the form expected.");

        var sections = new List<Section>();
        Section Get(string key)
        {
            foreach (Section s in sections) if (s.Key == key) return s;
            var made = new Section(key);
            sections.Add(made);
            return made;
        }

        string? current = null;
        var records = new List<List<string?>>();
        void Flush()
        {
            if (current is null || records.Count == 0) { records.Clear(); return; }
            AddRecords(current, records, Get);
            records.Clear();
        }
        for (int k = 3; k < lines.Length; k++)
        {
            string line = lines[k];
            if (line.Trim().Length == 0) continue;
            (List<string?> f, string? comment) = PsseText.Split(line, k + 1, current ?? "SYSTEM-WIDE");
            if (f.Count == 1 && f[0] == "Q") { Flush(); break; }
            if (f.Count == 1 && f[0] == "0")
            {
                Flush();
                current = PsseFieldRemap.BeginLabel(comment);
                continue;
            }
            if (current is null) throw new InvalidOperationException(FormattableString.Invariant($"PSS/E RAWX writer: line {k + 1} of the revision 35 text is outside a section."));
            records.Add(f);
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WritePropertyName("network");
            w.WriteStartObject();
            w.WritePropertyName("caseid");
            w.WriteStartObject();
            w.WritePropertyName("fields");
            w.WriteStartArray();
            foreach (string n in new[] { "ic", "sbase", "rev", "xfrrat", "nxfrat", "basfrq", "title1", "title2" }) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WritePropertyName("data");
            w.WriteStartArray();
            for (int i = 0; i < 6; i++) Number(w, id[i], "case identification field " + (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteStringValue(lines[1]);
            w.WriteStringValue(lines[2]);
            w.WriteEndArray();
            w.WriteEndObject();
            foreach (Section s in sections)
            {
                w.WritePropertyName(s.Key);
                w.WriteStartObject();
                w.WritePropertyName("fields");
                w.WriteStartArray();
                foreach (string n in s.Fields) w.WriteStringValue(n);
                w.WriteEndArray();
                w.WritePropertyName("data");
                w.WriteStartArray();
                foreach (List<(string? Value, bool Text)> row in s.Rows)
                {
                    w.WriteStartArray();
                    for (int i = 0; i < s.Fields.Count; i++)
                    {
                        (string? Value, bool Text) cell = i < row.Count ? row[i] : default;
                        if (cell.Value is null) w.WriteNullValue();
                        else if (cell.Text) w.WriteStringValue(cell.Value);
                        else Number(w, cell.Value, $"{s.Key} field {s.Fields[i]}");
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        string text = Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
        var notes = new List<string>(raw.Notes)
        {
            "written as PSS/E RAWX (JSON), revision 35: the records of the revision 35 RAW file, field by field, numbers in the same round-trip form.",
        };
        return new PsseWriteResult(text, notes, raw.BranchOrder);
    }

    private static void Number(Utf8JsonWriter w, string? token, string what)
    {
        if (string.IsNullOrEmpty(token)) { w.WriteNullValue(); return; }
        if (!JsonNumber.IsMatch(token))
            throw new InvalidOperationException($"PSS/E RAWX writer: {what} is '{token}', which is not a JSON number.");
        w.WriteRawValue(token);
    }

    /// <summary>The records of one RAW section as RAWX rows.</summary>
    private static void AddRecords(string section, List<List<string?>> records, Func<string, Section> get)
    {
        switch (section)
        {
            case "BUS": Single(get("bus"), BusF, records); break;
            case "LOAD": Single(get("load"), LoadF, records); break;
            case "FIXED SHUNT": Single(get("fixshunt"), FixShuntF, records); break;
            case "GENERATOR": Single(get("generator"), GeneratorF, records); break;
            case "BRANCH": Single(get("acline"), AclineF, records); break;
            case "SYSTEM SWITCHING DEVICE": Single(get("sysswd"), SysswdF, records); break;
            case "AREA": Single(get("area"), AreaF, records); break;
            case "ZONE": Single(get("zone"), ZoneF, records); break;
            case "OWNER": Single(get("owner"), OwnerF, records); break;
            case "FACTS CONTROL DEVICE": Single(get("facts"), FactsF, records); break;
            case "SWITCHED SHUNT": Single(get("swshunt"), SwShuntF, records); break;
            case "INDUCTION MACHINE": Single(get("indmach"), IndMachF, records); break;
            case "TRANSFORMER":
            {
                Section s = get("transformer");
                for (int i = 0; i < records.Count;)
                {
                    bool three = records[i].Count > 2 && records[i][2] is { Length: > 0 } kk && kk != "0";
                    int n = three ? 5 : 4;
                    if (i + n > records.Count) throw Bad(section, "a transformer record is cut short");
                    var groups = new List<(string[] Names, List<string?> Values)>
                    {
                        (XfLine1F, records[i]), (XfLine2F, records[i + 1]), (XfWindingF(1), records[i + 2]), (XfWindingF(2), records[i + 3]),
                    };
                    if (three) groups.Add((XfWindingF(3), records[i + 4]));
                    Row(s, groups, section);
                    i += n;
                }
                break;
            }
            case "TWO-TERMINAL DC":
            {
                Section s = get("twotermdc");
                if (records.Count % 3 != 0) throw Bad(section, "a two-terminal DC record is cut short");
                for (int i = 0; i < records.Count; i += 3)
                    Row(s, new() { (TwoTermF, records[i]), (TwoTermConvF("r"), records[i + 1]), (TwoTermConvF("i"), records[i + 2]) }, section);
                break;
            }
            case "VOLTAGE SOURCE CONVERTER":
            {
                Section s = get("vscdc");
                if (records.Count % 3 != 0) throw Bad(section, "a VSC DC record is cut short");
                for (int i = 0; i < records.Count; i += 3)
                    Row(s, new() { (VscF, records[i]), (VscConvF("1"), records[i + 1]), (VscConvF("2"), records[i + 2]) }, section);
                break;
            }
            case "MULTI-TERMINAL DC":
            {
                Section main = get("ntermdc"), conv = get("ntermdcconv"), bus = get("ntermdcbus"), link = get("ntermdclink");
                for (int i = 0; i < records.Count;)
                {
                    List<string?> head = records[i];
                    string name = head.Count > 0 ? head[0] ?? "" : "";
                    int nc = Count(head, 1, section), nb = Count(head, 2, section), nl = Count(head, 3, section);
                    if (i + 1 + nc + nb + nl > records.Count) throw Bad(section, $"multi-terminal DC link '{name}' is cut short");
                    Row(main, new() { (NtermF, head) }, section);
                    int at = i + 1;
                    foreach ((Section target, string[] spec, int count) in new[] { (conv, NtermConvF, nc), (bus, NtermBusF, nb), (link, NtermLinkF, nl) })
                        for (int c = 0; c < count; c++, at++)
                            Row(target, new() { (new[] { "name*" }, new List<string?> { name }), (spec, records[at]) }, section);
                    i = at;
                }
                break;
            }
            default:
                throw Bad(section, $"{records.Count} record(s) in a section the RAWX writer has no layout for");
        }
    }

    private static int Count(List<string?> head, int at, string section) =>
        at < head.Count && int.TryParse(head[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0
            ? n : throw Bad(section, "a multi-terminal DC link's NCONV, NDCBS or NDCLN is not a whole number");

    private static InvalidOperationException Bad(string section, string what) =>
        new($"PSS/E RAWX writer: {section} data: {what}.");

    private static void Single(Section s, string[] names, List<List<string?>> records)
    {
        foreach (List<string?> r in records) Row(s, new() { (names, r) }, s.Key);
    }

    /// <summary>One RAWX row from one or more RAW lines, each line's values named by its list. The section's field list
    /// grows to the longest row; a row with fewer values is padded with nulls (their defaults apply), as RAW leaves
    /// trailing fields off.</summary>
    private static void Row(Section s, List<(string[] Names, List<string?> Values)> groups, string section)
    {
        var row = new List<(string? Value, bool Text)>();
        var names = new List<string>();
        foreach ((string[] spec, List<string?> values) in groups)
        {
            if (values.Count > spec.Length) throw Bad(section, $"a line has {values.Count} fields, more than the {spec.Length} the layout names");
            for (int k = 0; k < spec.Length; k++)
            {
                bool text = spec[k].EndsWith('*');
                names.Add(text ? spec[k][..^1] : spec[k]);
                string? v = k < values.Count ? values[k] : null;
                row.Add((text ? v ?? "" : string.IsNullOrEmpty(v) ? null : v, text));
                if (k >= values.Count) row[^1] = (null, text);
            }
        }
        // The section's field list is the longest list seen; every row's names agree with it position by position.
        for (int k = 0; k < names.Count; k++)
        {
            if (k < s.Fields.Count) { if (s.Fields[k] != names[k]) throw Bad(section, $"field {k + 1} is '{names[k]}' in one row and '{s.Fields[k]}' in another"); }
            else s.Fields.Add(names[k]);
        }
        s.Rows.Add(row);
    }
}
