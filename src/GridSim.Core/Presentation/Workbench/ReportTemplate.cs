using System.Text;
using System.Text.Json;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>A report template or result that cannot be used: the message says what and where. Nothing is written for it.</summary>
public sealed class ReportException(string message) : Exception(message);

/// <summary>One column of a table block: the member key of the row object, the heading, the unit text and the decimals.</summary>
public sealed record ReportColumn(string Key, string Title, string Unit, int? Decimals);

/// <summary>A block of a report section, bound to the result document by JSON pointers (RFC 6901).</summary>
public abstract record ReportBlock;

/// <summary>Literal text.</summary>
public sealed record ReportTextBlock(string Text) : ReportBlock;

/// <summary>A labelled single value at <see cref="Path"/>.</summary>
public sealed record ReportValueBlock(string Label, string Path, int? Decimals) : ReportBlock;

/// <summary>A table over the array of objects at <see cref="Path"/>; with no columns given, the keys of the first row.</summary>
public sealed record ReportTableBlock(string Title, string Path, IReadOnlyList<ReportColumn>? Columns) : ReportBlock;

/// <summary>A plot of the number arrays at <see cref="XPath"/> and <see cref="YPath"/>. Kind is line, spectrum (x = orders, y =
/// amplitudes) or eigen (x = real parts, y = imaginary parts).</summary>
public sealed record ReportPlotBlock(string Title, string Kind, string XPath, string YPath, string XLabel, string YLabel, bool LogX, bool LogY) : ReportBlock;

public sealed record ReportSection(string Heading, IReadOnlyList<ReportBlock> Blocks);

/// <summary>
/// A report template, <c>gridsim-report-template/1</c>: a title and sections of blocks (text, value, table, plot) bound to a
/// result document by JSON pointers, so any result JSON (a workbench result or a CLI result) can be reported. The reader is
/// strict: unknown members, duplicate members and a wrong type are refused, naming the place.
/// </summary>
public sealed record ReportTemplate(string Title, string? ResultSha256, IReadOnlyList<ReportSection> Sections)
{
    public const string Schema = "gridsim-report-template/1";

    public static ReportTemplate Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        CheckDuplicates(bytes, "template");
        JsonElement root;
        try { root = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow }).RootElement.Clone(); }
        catch (JsonException ex) { throw new ReportException("The template is not valid JSON: " + ex.Message); }
        Obj(root, "template", ["schema", "title", "resultSha256", "sections"], ["schema", "title", "sections"]);
        if (Str(root.GetProperty("schema"), "schema") != Schema) throw new ReportException($"The template's schema must be \"{Schema}\".");
        string title = Str(root.GetProperty("title"), "title");
        string? sha = root.TryGetProperty("resultSha256", out JsonElement s) ? Str(s, "resultSha256").ToLowerInvariant() : null;
        var sections = new List<ReportSection>();
        JsonElement arr = root.GetProperty("sections");
        if (arr.ValueKind != JsonValueKind.Array) throw new ReportException("sections must be an array.");
        int si = 0;
        foreach (JsonElement sec in arr.EnumerateArray())
        {
            string at = $"sections[{si++}]";
            Obj(sec, at, ["heading", "blocks"], ["heading", "blocks"]);
            var blocks = new List<ReportBlock>();
            JsonElement barr = sec.GetProperty("blocks");
            if (barr.ValueKind != JsonValueKind.Array) throw new ReportException($"{at}.blocks must be an array.");
            int bi = 0;
            foreach (JsonElement b in barr.EnumerateArray()) blocks.Add(Block(b, $"{at}.blocks[{bi++}]"));
            sections.Add(new ReportSection(Str(sec.GetProperty("heading"), at + ".heading"), blocks));
        }
        return new ReportTemplate(title, sha, sections);
    }

    private static ReportBlock Block(JsonElement e, string at)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out JsonElement t)) throw new ReportException($"{at} must be an object with a type.");
        string type = Str(t, at + ".type");
        switch (type)
        {
            case "text":
                Obj(e, at, ["type", "text"], ["type", "text"]);
                return new ReportTextBlock(Str(e.GetProperty("text"), at + ".text"));
            case "value":
                Obj(e, at, ["type", "label", "path", "decimals"], ["type", "label", "path"]);
                return new ReportValueBlock(Str(e.GetProperty("label"), at + ".label"), Pointer(e.GetProperty("path"), at + ".path"), Dec(e, at));
            case "table":
                Obj(e, at, ["type", "title", "path", "columns"], ["type", "title", "path"]);
                List<ReportColumn>? cols = null;
                if (e.TryGetProperty("columns", out JsonElement c))
                {
                    if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() == 0) throw new ReportException($"{at}.columns must be a non-empty array.");
                    cols = [];
                    int ci = 0;
                    foreach (JsonElement col in c.EnumerateArray())
                    {
                        string cat = $"{at}.columns[{ci++}]";
                        Obj(col, cat, ["key", "title", "unit", "decimals"], ["key"]);
                        string key = Str(col.GetProperty("key"), cat + ".key");
                        cols.Add(new ReportColumn(key, col.TryGetProperty("title", out JsonElement ti) ? Str(ti, cat + ".title") : key,
                            col.TryGetProperty("unit", out JsonElement un) ? Str(un, cat + ".unit") : "", Dec(col, cat)));
                    }
                }
                return new ReportTableBlock(Str(e.GetProperty("title"), at + ".title"), Pointer(e.GetProperty("path"), at + ".path"), cols);
            case "plot":
                Obj(e, at, ["type", "title", "kind", "x", "y", "xLabel", "yLabel", "logX", "logY"], ["type", "title", "x", "y"]);
                string kind = e.TryGetProperty("kind", out JsonElement k) ? Str(k, at + ".kind") : "line";
                if (kind is not ("line" or "spectrum" or "eigen")) throw new ReportException($"{at}.kind must be line, spectrum or eigen.");
                return new ReportPlotBlock(Str(e.GetProperty("title"), at + ".title"), kind, Pointer(e.GetProperty("x"), at + ".x"), Pointer(e.GetProperty("y"), at + ".y"),
                    e.TryGetProperty("xLabel", out JsonElement xl) ? Str(xl, at + ".xLabel") : "", e.TryGetProperty("yLabel", out JsonElement yl) ? Str(yl, at + ".yLabel") : "",
                    Bool(e, "logX", at), Bool(e, "logY", at));
            default:
                throw new ReportException($"{at}.type '{type}' is not text, value, table or plot.");
        }
    }

    private static bool Bool(JsonElement e, string name, string at)
    {
        if (!e.TryGetProperty(name, out JsonElement b)) return false;
        return b.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ReportException($"{at}.{name} must be true or false.") };
    }

    private static int? Dec(JsonElement e, string at)
    {
        if (!e.TryGetProperty("decimals", out JsonElement d)) return null;
        if (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out int v) || v < 0 || v > 12) throw new ReportException($"{at}.decimals must be a whole number from 0 to 12.");
        return v;
    }

    private static string Pointer(JsonElement e, string at)
    {
        string p = Str(e, at);
        if (p.Length > 0 && p[0] != '/') throw new ReportException($"{at} must be a JSON pointer (empty, or starting with '/').");
        return p;
    }

    private static string Str(JsonElement e, string at) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new ReportException($"{at} must be a string.");

    private static void Obj(JsonElement e, string at, string[] allowed, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ReportException($"{at} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new ReportException($"{at}: unknown member '{p.Name}'.");
        foreach (string r in required)
            if (!e.TryGetProperty(r, out _)) throw new ReportException($"{at}: missing member '{r}'.");
    }

    /// <summary>Refuse a document that repeats a member name inside one object (the parser would otherwise keep one silently).</summary>
    internal static void CheckDuplicates(byte[] bytes, string what)
    {
        var stack = new Stack<HashSet<string>?>();
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: stack.Push(null); break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray: stack.Pop(); break;
                    case JsonTokenType.PropertyName:
                        if (!stack.Peek()!.Add(reader.GetString()!)) throw new ReportException($"The {what} repeats the member '{reader.GetString()}'.");
                        break;
                }
            }
        }
        catch (JsonException) { /* the parse that follows reports the syntax error */ }
    }

    /// <summary>
    /// The default template for a workbench result document: a summary section (study, status, outcome, reason), then a section per
    /// table and per plot, in the document's order. It is what the report view starts from; edit it and run it again.
    /// </summary>
    public static string DefaultJson(byte[] resultJson)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(resultJson).RootElement.Clone(); }
        catch (JsonException ex) { throw new ReportException("The result is not valid JSON: " + ex.Message); }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            string studyId = Walk(root, "study", "id") ?? "result";
            w.WriteString("title", "Study report: " + studyId);
            w.WriteStartArray("sections");
            Section(w, "Summary", () =>
            {
                Value(w, "Study", "/study/id");
                Value(w, "Kind", "/study/kind");
                Value(w, "Case", "/case/name");
                Value(w, "Status", "/status");
                Value(w, "Outcome", "/outcome");
                Value(w, "Reason", "/reason");
            });
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tables", out JsonElement tables) && tables.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty t in tables.EnumerateObject())
                {
                    string title = t.Value.ValueKind == JsonValueKind.Object && t.Value.TryGetProperty("title", out JsonElement tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : t.Name;
                    Section(w, title, () =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "table");
                        w.WriteString("title", title);
                        w.WriteString("path", "/tables/" + Esc(t.Name) + "/rows");
                        if (t.Value.TryGetProperty("columns", out JsonElement cols) && cols.ValueKind == JsonValueKind.Array)
                        {
                            w.WriteStartArray("columns");
                            foreach (JsonElement c in cols.EnumerateArray())
                            {
                                w.WriteStartObject();
                                w.WriteString("key", c.GetProperty("key").GetString());
                                w.WriteString("title", c.GetProperty("title").GetString());
                                w.WriteString("unit", c.GetProperty("unit").GetString());
                                if (c.TryGetProperty("decimals", out JsonElement d) && d.TryGetInt32(out int dv)) w.WriteNumber("decimals", dv);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    });
                }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plots", out JsonElement plots) && plots.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in plots.EnumerateObject())
                {
                    string title = p.Value.TryGetProperty("title", out JsonElement tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : p.Name;
                    Section(w, title, () =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "plot");
                        w.WriteString("title", title);
                        w.WriteString("x", "/plots/" + Esc(p.Name) + "/series/0/x");
                        w.WriteString("y", "/plots/" + Esc(p.Name) + "/series/0/y");
                        w.WriteString("xLabel", p.Value.TryGetProperty("xLabel", out JsonElement xl) ? xl.GetString() : "");
                        w.WriteString("yLabel", p.Value.TryGetProperty("yLabel", out JsonElement yl) ? yl.GetString() : "");
                        w.WriteEndObject();
                    });
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()) + "\n";
    }

    private static string Esc(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string? Walk(JsonElement e, params string[] path)
    {
        foreach (string p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static void Section(Utf8JsonWriter w, string heading, Action blocks)
    {
        w.WriteStartObject();
        w.WriteString("heading", heading);
        w.WriteStartArray("blocks");
        blocks();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void Value(Utf8JsonWriter w, string label, string path)
    {
        w.WriteStartObject();
        w.WriteString("type", "value");
        w.WriteString("label", label);
        w.WriteString("path", path);
        w.WriteEndObject();
    }
}
