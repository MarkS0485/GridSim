using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;

namespace GridSim.Core.Journal;

/// <summary>
/// How one field of a result moved between the recorded run and the re-run. A field is a JSON path with array indices
/// collapsed (<c>$.tables.buses[*].vm_pu</c>) or a CSV column. The figures are over every value of the field:
/// <list type="bullet">
/// <item><c>MaxAbsDiff</c> = max |b - a|;</item>
/// <item><c>MaxRelDiff</c> = max |b - a| / max(|a|, |b|) (0 when both are 0);</item>
/// <item><c>MaxUlps</c> = max <see cref="ResultDiff.Ulps"/>(a, b), the number of doubles between them;</item>
/// <item><c>MinAgreeingBits</c> = min(53 - bitlen(ulps)), clamped to [0, 53]: 53 when identical, 52 for 1 ulp, 0 for a
/// sign flip, NaN against a number, or 2^53 ulps and more.</item>
/// </list>
/// A field whose values are not numbers (strings, added or removed keys, changed row counts) has no numeric figures and
/// <c>MinAgreeingBits</c> 0.
/// </summary>
public sealed class FieldDifference
{
    public string File { get; init; } = "";
    public string Field { get; init; } = "";
    /// <summary>number, string, added, removed, rows, type or bytes.</summary>
    public string Kind { get; set; } = "number";
    public double? MaxAbsDiff { get; set; }
    public double? MaxRelDiff { get; set; }
    public ulong? MaxUlps { get; set; }
    public int MinAgreeingBits { get; set; } = 53;
    public long Count { get; set; }

    /// <summary>For a number field: the smallest and largest new / old ratio over its changed values (the re-run's value
    /// over the recorded one), for checking a predicted transform. Null when no changed value had a non-zero old value.</summary>
    public double? MinRatio { get; set; }
    public double? MaxRatio { get; set; }

    /// <summary>True when a changed value went from exactly 0 to something else (no ratio exists).</summary>
    public bool FromZero { get; set; }

    /// <summary>For a string field (or the exit code): the transition "old->new" when every changed value made the same
    /// one; null when they differ, or for other kinds.</summary>
    public string? Transition { get; set; }

    /// <summary>The claim check (see <see cref="BreakingChangeMap.Judge"/>): EXPLAINED, EXCEEDS-CLAIM, AMBIGUOUS or
    /// UNEXPLAINED, the IDs it rests on, the claim and what was observed.</summary>
    public string Verdict { get; set; } = "";
    public string ExplainedBy { get; set; } = "";
    public string Claimed { get; set; } = "";
    public string Observed { get; set; } = "";

    /// <summary>Record one changed string value's transition.</summary>
    public void Transit(string from, string to)
    {
        string t = from + "->" + to;
        if (Count == 1) Transition = t;
        else if (Transition != t) Transition = null;
    }
}

/// <summary>The comparison of one output file.</summary>
public sealed class FileComparison
{
    public string File { get; init; } = "";
    public bool Identical => Fields.Count == 0;
    public List<FieldDifference> Fields { get; } = new();
    /// <summary>Set when a <c>*Sha256</c> field inside a <c>provenance</c> object differs: an input the study file
    /// names changed, so the re-run is not a re-run of the same job.</summary>
    public string? IndirectInputChanged { get; set; }
    /// <summary>Set when the recorded document's schema tag is not the one this build writes for it.</summary>
    public string? SchemaNotReadable { get; set; }
}

/// <summary>
/// Compares a recorded result file with its re-run, field by field, bit for bit, after the volatile fields
/// (<see cref="VolatileFields"/>) are left out and path strings are mapped to their token placeholders.
/// </summary>
public static class ResultCompare
{
    /// <summary>
    /// Compare two versions of one output. JSON and CSV are compared by field; any other format byte for byte.
    /// </summary>
    /// <param name="file">The output's name, for the report.</param>
    /// <param name="recorded">The recorded output's bytes.</param>
    /// <param name="replay">The re-run's bytes.</param>
    /// <param name="verb">The canonical verb (selects the verb's volatile fields).</param>
    /// <param name="mapRecorded">Maps a string of the recorded output to its placeholder (or returns it unchanged).</param>
    /// <param name="mapReplay">The same for the re-run.</param>
    /// <param name="mask">The volatile-field list.</param>
    public static FileComparison Compare(string file, byte[] recorded, byte[] replay, string verb,
        Func<string, string> mapRecorded, Func<string, string> mapReplay, VolatileFields mask)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(replay);
        var cmp = new FileComparison { File = file };
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".json")
        {
            JsonDocument? a = TryParse(recorded), b = TryParse(replay);
            if (a is not null && b is not null)
            {
                using (a) using (b)
                {
                    string? schemaA = SchemaTag(a.RootElement), schemaB = SchemaTag(b.RootElement);
                    if (schemaA is not null && schemaA != schemaB)
                    {
                        cmp.SchemaNotReadable = schemaA;
                        return cmp;
                    }
                    IReadOnlyList<string> patterns = mask.For(schemaA, verb, file);
                    var acc = new Accumulator(file);
                    Walk(a.RootElement, b.RootElement, "$", patterns, mapRecorded, mapReplay, acc, false, cmp);
                    cmp.Fields.AddRange(acc.Fields());
                    return cmp;
                }
            }
            a?.Dispose();
            b?.Dispose();
        }
        else if (ext == ".csv")
        {
            CompareCsv(file, recorded, replay, mask.CsvNames(verb), mask.CsvCommentPrefixes(verb, file), mapRecorded, mapReplay, cmp);
            return cmp;
        }
        if (!recorded.AsSpan().SequenceEqual(replay))
            cmp.Fields.Add(new FieldDifference { File = file, Field = "(bytes)", Kind = "bytes", MinAgreeingBits = 0, Count = 1 });
        return cmp;
    }

    /// <summary>53 - bitlen(ulps), clamped to [0, 53].</summary>
    public static int AgreeingBits(ulong ulps)
    {
        int bitlen = ulps == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(ulps);
        return Math.Clamp(53 - bitlen, 0, 53);
    }

    private static JsonDocument? TryParse(byte[] bytes)
    {
        try { return JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes)); }
        catch (JsonException) { return null; }
    }

    private static string? SchemaTag(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String) return s.GetString();
        if (root.TryGetProperty("schemaVersion", out JsonElement v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    // Per-field accumulation, in first-seen order (document order), so the report order is fixed.
    private sealed class Accumulator(string file)
    {
        private readonly List<FieldDifference> _order = new();
        private readonly Dictionary<(string, string), FieldDifference> _byKey = new();

        public FieldDifference Get(string field, string kind)
        {
            if (!_byKey.TryGetValue((field, kind), out FieldDifference? d))
            {
                d = new FieldDifference { File = file, Field = field, Kind = kind, MinAgreeingBits = kind == "number" ? 53 : 0 };
                if (kind != "number") { d.MaxAbsDiff = null; d.MaxRelDiff = null; d.MaxUlps = null; }
                _byKey[(field, kind)] = d;
                _order.Add(d);
            }
            return d;
        }

        public void Number(string field, double a, double b)
        {
            FieldDifference d = Get(field, "number");
            d.Count++;
            double abs = Math.Abs(b - a);
            double den = Math.Max(Math.Abs(a), Math.Abs(b));
            double rel = den == 0 ? 0 : abs / den;
            ulong ulps = ResultDiff.Ulps(a, b);
            int bits = double.IsNaN(a) || double.IsNaN(b) ? 0 : AgreeingBits(ulps);
            d.MaxAbsDiff = d.MaxAbsDiff is double m ? (double.IsNaN(abs) || abs > m ? abs : m) : abs;
            d.MaxRelDiff = d.MaxRelDiff is double r ? (double.IsNaN(rel) || rel > r ? rel : r) : rel;
            d.MaxUlps = d.MaxUlps is ulong u ? Math.Max(u, ulps) : ulps;
            d.MinAgreeingBits = Math.Min(d.MinAgreeingBits, bits);
            if (a == 0 || !double.IsFinite(a)) d.FromZero |= b != a;
            else
            {
                double ratio = b / a;
                d.MinRatio = d.MinRatio is double lo ? Math.Min(lo, ratio) : ratio;
                d.MaxRatio = d.MaxRatio is double hi ? Math.Max(hi, ratio) : ratio;
            }
        }

        public IEnumerable<FieldDifference> Fields() => _order;
    }

    private static string Collapse(string path, bool array) => array ? path + "[*]" : path;

    private static void Walk(JsonElement a, JsonElement b, string path, IReadOnlyList<string> patterns,
        Func<string, string> mapA, Func<string, string> mapB, Accumulator acc, bool inProvenance, FileComparison cmp)
    {
        if (VolatileFields.Matches(path, patterns)) return;
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var mb = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty p in b.EnumerateObject()) mb[p.Name] = p.Value;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in a.EnumerateObject())
            {
                seen.Add(p.Name);
                string at = path + "." + p.Name;
                // A provenance object, or (in a study document flattened into the shared result) its provenance_* summary
                // keys and tables.
                bool prov = inProvenance || p.Name.StartsWith("provenance", StringComparison.OrdinalIgnoreCase);
                if (mb.TryGetValue(p.Name, out JsonElement vb))
                {
                    if (prov && p.Name.EndsWith("sha256", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String && vb.ValueKind == JsonValueKind.String
                        && p.Value.GetString() != vb.GetString())
                        cmp.IndirectInputChanged ??= at;
                    Walk(p.Value, vb, at, patterns, mapA, mapB, acc, prov, cmp);
                }
                else if (!VolatileFields.Matches(at, patterns)) acc.Get(at, "removed").Count++;
            }
            foreach (JsonProperty p in b.EnumerateObject())
            {
                string at = path + "." + p.Name;
                if (!seen.Contains(p.Name) && !VolatileFields.Matches(at, patterns)) acc.Get(at, "added").Count++;
            }
            return;
        }
        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            string at = Collapse(path, true);
            int na = a.GetArrayLength(), nb = b.GetArrayLength();
            if (na != nb) acc.Get(at, "rows").Count += Math.Abs(na - nb);
            using var ea = a.EnumerateArray().GetEnumerator();
            using var eb = b.EnumerateArray().GetEnumerator();
            while (ea.MoveNext() && eb.MoveNext()) Walk(ea.Current, eb.Current, at, patterns, mapA, mapB, acc, inProvenance, cmp);
            return;
        }
        double? x = AsNumber(a), y = AsNumber(b);
        if (x is double xa && y is double yb && (a.ValueKind == JsonValueKind.Number || b.ValueKind == JsonValueKind.Number))
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number && a.GetRawText() == b.GetRawText()) return;
            if (BitConverter.DoubleToInt64Bits(xa) == BitConverter.DoubleToInt64Bits(yb))
            {
                // SEC-33: two integers above 2^53 that differ in their text can round to one double (large ids,
                // counters); they are different values, counted as a number-text difference.
                if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number
                    && IsIntegerText(a.GetRawText()) && IsIntegerText(b.GetRawText()))
                    acc.Get(path, "number-text").Count++;
                return;
            }
            acc.Number(path, xa, yb);
            return;
        }
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
        {
            string sa = mapA(a.GetString()!), sb = mapB(b.GetString()!);
            if (!string.Equals(sa, sb, StringComparison.Ordinal))
            {
                FieldDifference d = acc.Get(path, "string");
                d.Count++;
                d.Transit(sa, sb);
            }
            return;
        }
        if (a.ValueKind != b.ValueKind) { acc.Get(path, "type").Count++; return; }
        if (a.ValueKind == JsonValueKind.Number && a.GetRawText() != b.GetRawText()) acc.Get(path, "number-text").Count++;
    }

    // A JSON number, or one of the strings the result writer uses for the non-finite doubles.
    /// <summary>SEC-33: a JSON number written as an integer (an optional minus and digits only).</summary>
    private static bool IsIntegerText(string raw)
    {
        int i = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        if (i >= raw.Length) return false;
        for (; i < raw.Length; i++) if (!char.IsAsciiDigit(raw[i])) return false;
        return true;
    }

    private static double? AsNumber(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetDouble(out double d) ? d : null,
        JsonValueKind.String => e.GetString() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ => null,
        },
        _ => null,
    };

    // ---- CSV -----------------------------------------------------------------------------------------------------------

    /// <summary>Split CSV text into rows of fields (RFC 4180 quoting; CR LF or LF line ends).</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static double? CsvNumber(string s)
    {
        if (s is "NaN") return double.NaN;
        if (s is "Infinity") return double.PositiveInfinity;
        if (s is "-Infinity") return double.NegativeInfinity;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>
    /// 2.1.127: split the leading comment lines (each starting with <c>#</c>, such as the run header a dynamics export
    /// writes above its column header) off CSV text. Returns them, without the <c>#</c> and its spaces, and the rest of the
    /// text. Text with no leading comment line comes back whole.
    /// </summary>
    public static (List<string> Comments, string Body) SplitCommentLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var comments = new List<string>();
        int at = 0;
        while (at < text.Length && text[at] == '#')
        {
            int nl = text.IndexOf('\n', at);
            string line = (nl < 0 ? text[at..] : text[at..nl]).TrimEnd('\r');
            comments.Add(line[1..].TrimStart(' '));
            at = nl < 0 ? text.Length : nl + 1;
        }
        return (comments, at == 0 ? text : text[at..]);
    }

    private static void CompareCsv(string file, byte[] recorded, byte[] replay, IReadOnlyList<string> masked, IReadOnlyList<string> maskedComments,
        Func<string, string> mapA, Func<string, string> mapB, FileComparison cmp)
    {
        var acc = new Accumulator(file);
        (List<string> commentsA, string ta) = SplitCommentLines(Encoding.UTF8.GetString(recorded));
        (List<string> commentsB, string tb) = SplitCommentLines(Encoding.UTF8.GetString(replay));
        if (commentsA.Count > 0 || commentsB.Count > 0)
        {
            // The comment lines in order, those the volatile list names (the run header's time, build and host lines) left out;
            // every other comment line (the run description, the provenance line) is compared as text.
            bool kept(string c) => !maskedComments.Any(m => c.StartsWith(m, StringComparison.Ordinal));
            List<string> ka = commentsA.Where(kept).ToList(), kb = commentsB.Where(kept).ToList();
            if (ka.Count != kb.Count) acc.Get("(comment lines)", "rows").Count += Math.Abs(ka.Count - kb.Count);
            for (int i = 0; i < Math.Min(ka.Count, kb.Count); i++)
            {
                string sa = mapA(ka[i]), sb = mapB(kb[i]);
                if (string.Equals(sa, sb, StringComparison.Ordinal)) continue;
                FieldDifference d = acc.Get("(comment line)", "string");
                d.Count++;
                d.Transit(sa, sb);
            }
        }
        List<List<string>> ra = ParseCsv(ta), rb = ParseCsv(tb);
        bool isMasked(string name) => masked.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ra.Count == 0 || rb.Count == 0)
        {
            if (!string.Equals(ta, tb, StringComparison.Ordinal)) acc.Get("(file)", "rows").Count += Math.Abs(ra.Count - rb.Count) + 1;
            cmp.Fields.AddRange(acc.Fields());
            return;
        }
        List<string> ha = ra[0], hb = rb[0];
        bool keyValue = ha.Count == 2 && ha[0] == "key" && ha[1] == "value" && hb.Count == 2 && hb[0] == "key" && hb[1] == "value";
        if (keyValue)
        {
            var vb = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < rb.Count; i++) if (rb[i].Count >= 2) vb.TryAdd(rb[i][0], rb[i][1]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < ra.Count; i++)
            {
                if (ra[i].Count < 2) continue;
                string key = ra[i][0];
                seen.Add(key);
                if (isMasked(key)) continue;
                if (!vb.TryGetValue(key, out string? other)) { acc.Get(key, "removed").Count++; continue; }
                Cell(key, ra[i][1], other, mapA, mapB, acc);
            }
            foreach (string key in vb.Keys.Order(StringComparer.Ordinal))
                if (!seen.Contains(key) && !isMasked(key)) acc.Get(key, "added").Count++;
            cmp.Fields.AddRange(acc.Fields());
            return;
        }
        for (int c = 0; c < ha.Count; c++)
            if (!hb.Contains(ha[c]) && !isMasked(ha[c])) acc.Get(ha[c], "removed").Count++;
        for (int c = 0; c < hb.Count; c++)
            if (!ha.Contains(hb[c]) && !isMasked(hb[c])) acc.Get(hb[c], "added").Count++;
        if (ra.Count != rb.Count) acc.Get("(rows)", "rows").Count += Math.Abs(ra.Count - rb.Count);
        int rows = Math.Min(ra.Count, rb.Count);
        for (int c = 0; c < ha.Count; c++)
        {
            string name = ha[c];
            // SEC-33: a repeated header name is matched by occurrence (the second "x" of one file with the second of the
            // other); IndexOf took the first, so a second column of that name was compared with the other file's first.
            int occurrence = 0;
            for (int q = 0; q < c; q++) if (ha[q] == name) occurrence++;
            int cb = -1;
            for (int q = 0, seenSoFar = 0; q < hb.Count; q++)
                if (hb[q] == name && seenSoFar++ == occurrence) { cb = q; break; }
            if (cb < 0 || isMasked(name)) continue;
            for (int r = 1; r < rows; r++)
            {
                string va = c < ra[r].Count ? ra[r][c] : "", vbv = cb < rb[r].Count ? rb[r][cb] : "";
                Cell(name, va, vbv, mapA, mapB, acc);
            }
        }
        cmp.Fields.AddRange(acc.Fields());
    }

    private static void Cell(string field, string a, string b, Func<string, string> mapA, Func<string, string> mapB, Accumulator acc)
    {
        if (a == b) return;
        if (CsvNumber(a) is double x && CsvNumber(b) is double y)
        {
            if (BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)) return;
            acc.Number(field, x, y);
            return;
        }
        string sa = mapA(a), sb = mapB(b);
        if (!string.Equals(sa, sb, StringComparison.Ordinal))
        {
            FieldDifference d = acc.Get(field, "string");
            d.Count++;
            d.Transit(sa, sb);
        }
    }
}
