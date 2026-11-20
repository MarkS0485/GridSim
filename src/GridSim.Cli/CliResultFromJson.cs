using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// RC4 (last release candidate): the ten study verbs that already build their own result document (probabilistic,
// reliability-network, harmonic-assess, flicker, filter-design, connection-assess, study-case, case-diff, result-diff,
// report) take --json / --csv through the shared result writer like every other writer verb.
//
// Each of those verbs has ONE result document of its own (the JSON it prints or writes with its own --out). This helper
// turns that very document into the shared CliResult, so no number is computed twice and none can differ:
//
//   * a scalar member of the document (string, number, true/false, null) is a summary value;
//   * a nested object is flattened into the summary with the member names joined by an underscore;
//   * an array is a table: an array of objects has one column per member name (first-seen order; a member that is itself
//     an object or array is written as its compact JSON text), an array of scalars has the one column "value".
//
// Names are lower-case snake_case (camelCase is split at the capitals). A name used twice gets a numeric suffix. The verb's
// own --out is unchanged for these verbs (it names the verb's own result file), so --json is the shared document.
// ---------------------------------------------------------------------------------------------------------------------

internal static class CliResultFromJson
{
    /// <summary>
    /// Fill the running report from a verb's result document and mark it complete. Does nothing when no result file was
    /// asked for (the usual case), so the run is exactly what it was.
    /// </summary>
    public static void Fill(string json)
    {
        CliResult? rep = CliOutput.Report;
        if (rep is null) return;
        using JsonDocument doc = JsonDocument.Parse(json);
        Fill(rep, doc.RootElement);
    }

    /// <summary>Fill <paramref name="rep"/> from <paramref name="root"/> and mark it complete.</summary>
    public static void Fill(CliResult rep, JsonElement root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tables = new HashSet<string>(StringComparer.Ordinal) { "summary" };
        if (root.ValueKind == JsonValueKind.Object) Walk(rep, root, "", keys, tables);
        else if (root.ValueKind == JsonValueKind.Array) AddTable(rep, "items", root, tables);
        else rep.Set("value", Scalar(root));
        rep.Complete();
    }

    private static void Walk(CliResult rep, JsonElement obj, string prefix, HashSet<string> keys, HashSet<string> tables)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            string name = prefix.Length == 0 ? Snake(p.Name) : prefix + "_" + Snake(p.Name);
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object: Walk(rep, p.Value, name, keys, tables); break;
                case JsonValueKind.Array: AddTable(rep, name, p.Value, tables); break;
                default: rep.Set(Unique(name, keys), Scalar(p.Value)); break;
            }
        }
    }

    private static void AddTable(CliResult rep, string name, JsonElement array, HashSet<string> tables)
    {
        string table = Unique(name, tables);
        var columns = new List<string>();
        var sources = new List<string>();   // the member name behind each column
        bool objects = array.GetArrayLength() > 0;
        foreach (JsonElement e in array.EnumerateArray()) objects &= e.ValueKind == JsonValueKind.Object;
        if (!objects)
        {
            CliResultTable t = rep.Table(table, "value");
            foreach (JsonElement e in array.EnumerateArray()) t.Row(Cell(e));
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement e in array.EnumerateArray())
            foreach (JsonProperty p in e.EnumerateObject())
                if (!sources.Contains(p.Name, StringComparer.Ordinal))
                {
                    sources.Add(p.Name);
                    columns.Add(Unique(Snake(p.Name), seen));
                }
        CliResultTable tab = rep.Table(table, columns.ToArray());
        foreach (JsonElement e in array.EnumerateArray())
        {
            var cells = new object?[sources.Count];
            for (int i = 0; i < sources.Count; i++)
                cells[i] = e.TryGetProperty(sources[i], out JsonElement v) ? Cell(v) : null;
            tab.Row(cells);
        }
    }

    private static object? Cell(JsonElement e) => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? e.GetRawText() : Scalar(e);

    private static object? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.TryGetUInt64(out ulong u) ? u.ToString(CultureInfo.InvariantCulture) : e.GetDouble(),
        _ => null,
    };

    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int n = 2; !used.Add(candidate); n++) candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>A member name as lower-case snake_case: capitals start a new word, anything else outside a-z and 0-9 is an underscore.</summary>
    internal static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is >= 'A' and <= 'Z')
            {
                bool boundary = i > 0 && (name[i - 1] is (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    || (name[i - 1] is >= 'A' and <= 'Z' && i + 1 < name.Length && name[i + 1] is >= 'a' and <= 'z'));
                if (boundary && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                sb.Append((char)(c + 32));
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        string s = sb.ToString().Trim('_');
        if (s.Length == 0 || s[0] is < 'a' or > 'z') s = "x_" + s;
        return s.TrimEnd('_');
    }
}
