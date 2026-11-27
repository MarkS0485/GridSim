using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// The revision 35 field order of the records that revision 36 and RAWX change or name, written as field names. A field
/// spec is its names separated by <c>|</c> (the first is the RAW 35 name, the rest are the names a revision 36 heading or a
/// RAWX file uses for it), with a trailing <c>*</c> when the field is text. <see cref="PsseRevision36"/> and
/// <see cref="PsseRawx"/> rebuild a record in this order, by name, so the one revision 35 reader reads all three.
/// </summary>
internal static class PsseFieldRemap
{
    private static readonly string[] Rates12 =
        Enumerable.Range(1, 12).Select(k => k <= 3 ? $"RATE{k}|RATE{(char)('A' + k - 1)}" : $"RATE{k}").ToArray();

    private static string[] Concat(params IEnumerable<string>[] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Bus: I, NAME, BASKV, IDE, AREA, ZONE, OWNER, VM, VA, NVHI, NVLO, EVHI, EVLO.</summary>
    public static readonly string[] Bus =
        { "I|IBUS", "NAME*", "BASKV", "IDE", "AREA", "ZONE", "OWNER", "VM", "VA", "NVHI", "NVLO", "EVHI", "EVLO" };

    /// <summary>Load: I, ID, STATUS, AREA, ZONE, PL, QL, IP, IQ, YP, YQ, OWNER, SCALE, INTRPT, DGENP, DGENQ, DGENF, LOADTYPE.</summary>
    public static readonly string[] Load =
    {
        "I|IBUS", "ID|LOADID*", "STATUS|STAT|ST", "AREA", "ZONE", "PL", "QL", "IP", "IQ", "YP", "YQ", "OWNER", "SCALE",
        "INTRPT", "DGENP", "DGENQ", "DGENF|DGENM", "LOADTYPE*"
    };

    /// <summary>Fixed shunt: I, ID, STATUS, GL, BL.</summary>
    public static readonly string[] FixedShunt = { "I|IBUS", "ID|SHNTID*", "STATUS|STAT|ST", "GL", "BL" };

    /// <summary>Generator: I, ID, PG ... PB, BASLOD, O1 F1 ... O4 F4, WMOD, WPF.</summary>
    public static readonly string[] Generator = Concat(
        new[] { "I|IBUS", "ID|MACHID*", "PG", "QG", "QT", "QB", "VS", "IREG", "NREG", "MBASE", "ZR|ZSORCE_R", "ZX|ZSORCE_X",
            "RT", "XT", "GTAP", "STAT|STATUS|ST", "RMPCT", "PT", "PB", "BASLOD" },
        Enumerable.Range(1, 4).SelectMany(k => new[] { $"O{k}", $"F{k}" }),
        new[] { "WMOD", "WPF" });

    /// <summary>Branch: I, J, CKT, R, X, B, NAME, RATE1 ... RATE12, GI, BI, GJ, BJ, ST, MET, LEN, O1 F1 ... O4 F4.</summary>
    public static readonly string[] Branch = Concat(
        new[] { "I|IBUS", "J|JBUS", "CKT*", "R|RPU", "X|XPU", "B|BPU", "NAME*" },
        Rates12,
        new[] { "GI", "BI", "GJ", "BJ", "ST|STAT|STATUS", "MET", "LEN" },
        Enumerable.Range(1, 4).SelectMany(k => new[] { $"O{k}", $"F{k}" }));

    /// <summary>System switching device: I, J, CKT, X, RATE1 ... RATE12, STAT, NSTAT, MET, STYPE, NAME.</summary>
    public static readonly string[] SwitchingDevice = Concat(
        new[] { "I|IBUS", "J|JBUS", "CKT*", "X|XPU" },
        Rates12,
        new[] { "STAT|ST|STATUS", "NSTAT", "MET", "STYPE", "NAME*" });

    /// <summary>Switched shunt: I, ID, MODSW, ADJM, STAT, VSWHI, VSWLO, SWREG, NREG, RMPCT, RMIDNT, BINIT, S1 N1 B1 ... S8 N8 B8.</summary>
    public static readonly string[] SwitchedShunt = Concat(
        new[] { "I|IBUS", "ID|SHNTID*", "MODSW", "ADJM", "STAT|STATUS|ST", "VSWHI", "VSWLO", "SWREG|SWREM", "NREG", "RMPCT",
            "RMIDNT*", "BINIT" },
        Enumerable.Range(1, 8).SelectMany(k => new[] { $"S{k}", $"N{k}", $"B{k}" }));

    /// <summary>Induction machine: I, ID, STAT, SCODE, DCODE, AREA, ZONE, OWNER, TCODE, BCODE, MBASE, RATEKV, PCODE, PSET, H,
    /// A, B, D, E, RA, XA, XM, R1, X1, R2, X2, X3, E1, SE1, E2, SE2, IA1, IA2, XAMULT.</summary>
    /// <remarks>RAWX names the machine id <c>imid</c> and the torque terms <c>hconst</c>, <c>aconst</c>, <c>bconst</c>,
    /// <c>dconst</c> and <c>econst</c> (PowSyBl's PSS/E model, PsseInductionMachine FIELD_NAMES_35X, and its RAWX test case
    /// IEEE_14_bus_completed_rev35.rawx); those names are accepted as aliases (RC5 WP34). The PSS/E 35 RAW comment heading
    /// and PowSyBl's FIELD_NAMES_33_35 name the code fields SC, DC, TC, BC and PC; those short names are accepted too, so a
    /// revision 36 heading in that form places SCODE, DCODE, TCODE, BCODE and PCODE (RC5 WP34 review).</remarks>
    public static readonly string[] InductionMachine =
    {
        "I|IBUS", "ID|INDID|MACHID|IMID*", "STAT|STATUS|ST", "SCODE|SC", "DCODE|DC", "AREA", "ZONE", "OWNER", "TCODE|TC", "BCODE|BC", "MBASE",
        "RATEKV", "PCODE|PC", "PSET", "H|HCONST", "A|ACONST", "B|BCONST", "D|DCONST", "E|ECONST", "RA", "XA", "XM", "R1", "X1", "R2",
        "X2", "X3", "E1", "SE1", "E2", "SE2", "IA1", "IA2", "XAMULT"
    };

    // ---- RC5 WP34: the device sections RAWX carries beyond the common ones -------------------------------------------
    // Revision 35 field order, as the RAW reader reads each line (its comments name the fields), with the RAWX names as
    // aliases. RAWX names: PowSyBl's PSS/E model (powsybl-core, MPL-2.0: PsseTwoTerminalDcTransmissionLine,
    // PsseTwoTerminalDcConverter, PsseVoltageSourceConverterDcTransmissionLine, PsseVoltageSourceConverter, PsseFacts,
    // internal/PsseMultiTerminalDc*, ZCorr35X, PsseLineGrouping, PsseInterareaTransfer and PowerFlowRecordGroup for the
    // section keys) and its RAWX test case IEEE_14_bus_completed_rev35.rawx; checked against a second, independent reader,
    // surge-io's psse/rawx.rs (twotermdc ipr/nbr/anmxr..., vscdc ibus1/mode1/acset1..., facts name/ibus/jbus/mode...).

    /// <summary>Two-terminal DC link line: NAME, MDC, RDC, SETVL, VSCHD, VCMOD, RCOMP, DELTI, METER, DCVMIN, CCCITMX, CCCACC.</summary>
    public static readonly string[] TwoTerminalLine =
        { "NAME*", "MDC", "RDC", "SETVL", "VSCHD", "VCMOD", "RCOMP", "DELTI", "METER|MET*", "DCVMIN", "CCCITMX", "CCCACC" };

    /// <summary>Two-terminal DC converter line (revision 35): IP, NB, ANMX, ANMN, RC, XC, EBAS, TR, TAP, TMX, TMN, STP, IC, ND,
    /// IF, IT, ID, XCAP. RAWX writes the rectifier's with the suffix R and the inverter's with I (ipr, ipi, ...).</summary>
    public static readonly string[] TwoTerminalConverter =
        { "IP", "NB", "ANMX", "ANMN", "RC", "XC", "EBAS", "TR", "TAP", "TMX", "TMN", "STP", "IC", "ND", "IF", "IT", "ID*", "XCAP" };

    /// <summary>VSC DC link line: NAME, MDC, RDC, O1, F1 ... O4, F4.</summary>
    public static readonly string[] VscLine = Concat(
        new[] { "NAME*", "MDC", "RDC" },
        Enumerable.Range(1, 4).SelectMany(k => new[] { $"O{k}", $"F{k}" }));

    /// <summary>VSC converter line (revision 35): IBUS, TYPE, MODE, DCSET, ACSET, ALOSS, BLOSS, MINLOSS, SMAX, IMAX, PWF, MAXQ,
    /// MINQ, VSREG, NREG, RMPCT. RAWX writes the two converters with the suffixes 1 and 2 (ibus1, ibus2, ...).</summary>
    public static readonly string[] VscConverter =
        { "IBUS", "TYPE", "MODE", "DCSET", "ACSET", "ALOSS", "BLOSS", "MINLOSS", "SMAX", "IMAX", "PWF", "MAXQ", "MINQ", "VSREG|REMOT", "NREG", "RMPCT" };

    /// <summary>Multi-terminal DC link line: NAME, NCONV, NDCBS, NDCLN, MDC, VCONV, VCMOD, VCONVN.</summary>
    public static readonly string[] MultiTerminalLine = { "NAME*", "NCONV", "NDCBS", "NDCLN", "MDC", "VCONV", "VCMOD", "VCONVN" };

    /// <summary>Multi-terminal DC converter line: IB, N, ANGMX, ANGMN, RC, XC, EBAS, TR, TAP, TPMX, TPMN, TSTP, SETVL, DCPF, MARG, CNVCOD.</summary>
    public static readonly string[] MultiTerminalConverter =
        { "IB", "N", "ANGMX", "ANGMN", "RC", "XC", "EBAS", "TR", "TAP", "TPMX", "TPMN", "TSTP", "SETVL", "DCPF", "MARG", "CNVCOD" };

    /// <summary>Multi-terminal DC bus line: IDC, IB, AREA, ZONE, DCNAME, IDC2, RGRND, OWNER.</summary>
    public static readonly string[] MultiTerminalBus = { "IDC", "IB", "AREA", "ZONE", "DCNAME*", "IDC2", "RGRND", "OWNER" };

    /// <summary>Multi-terminal DC link line: IDC, JDC, DCCKT, MET, RDC, LDC.</summary>
    public static readonly string[] MultiTerminalLink = { "IDC", "JDC", "DCCKT*", "MET", "RDC", "LDC" };

    /// <summary>FACTS device (revision 35): NAME, I, J, MODE, PDES, QDES, VSET, SHMX, TRMX, VTMN, VTMX, VSMX, IMX, LINX, RMPCT,
    /// OWNER, SET1, SET2, VSREF, FCREG, NREG, MNAME.</summary>
    public static readonly string[] Facts =
    {
        "NAME*", "I|IBUS", "J|JBUS", "MODE", "PDES", "QDES", "VSET", "SHMX", "TRMX", "VTMN", "VTMX", "VSMX", "IMX", "LINX", "RMPCT",
        "OWNER", "SET1", "SET2", "VSREF", "FCREG|REMOT", "NREG", "MNAME*"
    };

    /// <summary>Multi-section line grouping: I, J, ID, MET, DUM1 ... DUM9.</summary>
    public static readonly string[] MultiSectionLine = Concat(
        new[] { "I|IBUS", "J|JBUS", "ID|MSLID*", "MET" },
        Enumerable.Range(1, 9).Select(k => $"DUM{k}"));

    /// <summary>Inter-area transfer: ARFROM, ARTO, TRID, PTRAN.</summary>
    public static readonly string[] InterAreaTransfer = { "ARFROM", "ARTO", "TRID*", "PTRAN" };

    /// <summary><paramref name="spec"/> with <paramref name="suffix"/> added to every name (the text mark kept last).</summary>
    public static string[] Suffixed(string[] spec, string suffix) => spec.Select(f =>
    {
        bool text = f.EndsWith('*');
        string names = text ? f[..^1] : f;
        return string.Join("|", names.Split('|').Select(n => n + suffix)) + (text ? "*" : "");
    }).ToArray();

    /// <summary>Area: I, ISW, PDES, PTOL, ARNAME.</summary>
    public static readonly string[] Area = { "I|IAREA", "ISW", "PDES", "PTOL", "ARNAME|ARNAM*" };

    /// <summary>Zone: I, ZONAME.</summary>
    public static readonly string[] Zone = { "I|IZONE", "ZONAME|ZONAM*" };

    /// <summary>Owner: I, OWNAME.</summary>
    public static readonly string[] Owner = { "I|IOWNER", "OWNAME|OWNAM*" };

    /// <summary>Transformer record line 1 (the identification line).</summary>
    public static readonly string[] TransformerLine1 = Concat(
        new[] { "I|IBUS", "J|JBUS", "K|KBUS", "CKT*", "CW", "CZ", "CM", "MAG1", "MAG2", "NMET", "NAME*", "STAT|STATUS|ST" },
        Enumerable.Range(1, 4).SelectMany(k => new[] { $"O{k}", $"F{k}" }),
        new[] { "VECGRP*", "ZCOD" });

    /// <summary>Transformer record line 2 (the impedance line).</summary>
    public static readonly string[] TransformerLine2 =
    {
        "R1-2|R1_2", "X1-2|X1_2", "SBASE1-2|SBASE1_2", "R2-3|R2_3", "X2-3|X2_3", "SBASE2-3|SBASE2_3", "R3-1|R3_1", "X3-1|X3_1",
        "SBASE3-1|SBASE3_1", "VMSTAR", "ANSTAR"
    };

    /// <summary>Transformer winding line <paramref name="w"/> (1 to 3): WINDV, NOMV, ANG, RATE w-1 ... RATE w-12, COD, CONT,
    /// NODE, RMA, RMI, VMA, VMI, NTP, TAB, CR, CX, CNXA. A two-winding unit's winding 2 line carries only WINDV and NOMV.</summary>
    public static string[] TransformerWinding(int w) => Concat(
        new[] { $"WINDV{w}", $"NOMV{w}", $"ANG{w}" },
        Enumerable.Range(1, 12).Select(k => $"RATE{w}-{k}|WDG{w}RATE{k}"),
        new[] { $"COD{w}", $"CONT{w}", $"NODE{w}", $"RMA{w}", $"RMI{w}", $"VMA{w}", $"VMI{w}", $"NTP{w}", $"TAB{w}", $"CR{w}", $"CX{w}", $"CNXA{w}" });

    /// <summary>The upper-case, unquoted, space-free name of a heading or RAWX field.</summary>
    public static string Key(string name) =>
        name.Trim().Trim('\'', '"').Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);

    /// <summary>The field names of an <c>@!</c> heading line, keyed.</summary>
    public static List<string> HeadingNames(string line)
    {
        string t = line.TrimStart();
        t = t.Length >= 2 && t[0] == '@' && t[1] == '!' ? t[2..] : t;
        int slash = t.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0) t = t[..slash];
        var names = new List<string>();
        foreach (string k in t.Split(',').Select(Key).Where(k => k.Length > 0))
        {
            // A heading may abbreviate a run of numbered fields as RATE1..RATE12.
            Match m = Regex.Match(k, @"^([A-Z_]+)(\d+)\.\.([A-Z_]+)(\d+)$");
            if (m.Success && m.Groups[1].Value == m.Groups[3].Value
                && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) <= int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)
                && int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) < 100)
            {
                for (int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); n <= int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture); n++)
                    names.Add(m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture));
            }
            else names.Add(k);
        }
        return names;
    }

    /// <summary>
    /// The values of one record rebuilt in the order of <paramref name="spec"/>: each spec field takes the value under the
    /// first of its names that <paramref name="names"/> carries; a field the source has no name for is left empty (its
    /// default applies) and trailing empty fields are cut. Names the spec does not know are dropped.
    /// </summary>
    public static List<string?> Reorder(string[] spec, IReadOnlyList<string> names, IReadOnlyList<string?> values)
    {
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < names.Count; k++) at.TryAdd(names[k], k);
        var outp = new List<string?>(spec.Length);
        foreach (string field in spec)
        {
            string f = field.EndsWith('*') ? field[..^1] : field;
            string? v = null;
            foreach (string alias in f.Split('|'))
                if (at.TryGetValue(alias, out int idx)) { v = idx < values.Count ? values[idx] : null; break; }
            outp.Add(string.IsNullOrEmpty(v) ? null : v);
        }
        while (outp.Count > 0 && outp[^1] is null) outp.RemoveAt(outp.Count - 1);
        return outp;
    }

    /// <summary>The value of the field called <paramref name="name"/> (any of its <c>|</c> names), or null.</summary>
    public static string? ValueOf(IReadOnlyList<string> names, IReadOnlyList<string?> values, string name)
    {
        foreach (string alias in name.Split('|'))
        {
            int idx = -1;
            for (int k = 0; k < names.Count; k++) if (names[k] == alias) { idx = k; break; }
            if (idx >= 0 && idx < values.Count && !string.IsNullOrEmpty(values[idx])) return values[idx];
        }
        return null;
    }

    /// <summary>One record as RAW text: text fields quoted, numbers as given, empty fields left empty.
    /// IO-10: the RAW tokeniser reads this text back, so a value that would change the record's fields is refused, naming
    /// the field: a value of a field that is not text holding a field separator, a blank, a slash (the end of a record) or
    /// a quote, and text holding both quote kinds (no RAW quoting can hold it; PsseRawWriter refuses it too). Such a value
    /// used to shift every later field, or end the record early so the later fields took their defaults.</summary>
    public static string Line(string[] spec, IReadOnlyList<string?> values)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < values.Count; k++)
        {
            if (k > 0) sb.Append(',');
            string? v = values[k];
            if (string.IsNullOrEmpty(v)) continue;
            bool text = k < spec.Length && spec[k].EndsWith('*');
            if (text) sb.Append(Quote(v, k < spec.Length ? spec[k] : null));
            else
            {
                v = v.Trim();   // surrounding blanks are not part of a number (a RAWX writer may pad a quoted number)
                if (v.Length == 0) continue;
                if (v.AsSpan().IndexOfAny(RecordBreakers) >= 0)
                    throw new InvalidDataException($"PSS/E: the field {FieldName(spec, k)} holds '{Clip(v)}', which is not a number or a code: a separator, blank, slash or quote in it would change the fields of its record.");
                sb.Append(v);
            }
        }
        return sb.ToString();
    }

    /// <summary>IO-10: the characters that end or split a RAW field: a separator, a blank, a tab, a slash and the quotes.</summary>
    private static readonly System.Buffers.SearchValues<char> RecordBreakers = System.Buffers.SearchValues.Create(",/ \t'\"\r\n");

    private static string FieldName(string[] spec, int k) => k < spec.Length ? spec[k].TrimEnd('*').Split('|')[0] : (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Clip(string v) => v.Length <= 40 ? v : v[..40] + "...";

    /// <summary>Text as a quoted RAW field (single quotes, or double when the text holds a single quote). IO-10: text that
    /// holds both kinds of quote is refused.</summary>
    public static string Quote(string v) => Quote(v, null);

    private static string Quote(string v, string? field)
    {
        // R2-06: the RAW tokeniser reads line by line, so a line break (or any other control character) in text splits its
        // record; refused naming the field.
        if (v.Any(ch => ch < ' '))
            throw new InvalidDataException($"PSS/E: the text {(field is null ? "" : "of the field " + field.TrimEnd('*').Split('|')[0] + " ")}holds a line break or another control character, which a RAW record cannot hold.");
        bool single = v.Contains('\'', StringComparison.Ordinal), dbl = v.Contains('"', StringComparison.Ordinal);
        if (single && dbl)
            throw new InvalidDataException($"PSS/E: the text {(field is null ? "" : "of the field " + field.TrimEnd('*').Split('|')[0] + " ")}'{Clip(v)}' holds both a single and a double quote, which a RAW record cannot hold.");
        return single ? "\"" + v + "\"" : "'" + v + "'";
    }

    /// <summary>The section a terminator comment names after <c>BEGIN</c>, or null.</summary>
    public static string? BeginLabel(string? comment)
    {
        if (comment is null) return null;
        Match m = Regex.Match(comment.ToUpperInvariant(), @"BEGIN (.+?) DATA");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim() : null;
    }

    /// <summary>The section a terminator comment names after <c>END OF</c>, or null.</summary>
    public static string? EndLabel(string? comment)
    {
        if (comment is null) return null;
        Match m = Regex.Match(comment.ToUpperInvariant(), @"END OF (.+?) DATA");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim() : null;
    }

    /// <summary>A number as invariant round-trip text.</summary>
    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
