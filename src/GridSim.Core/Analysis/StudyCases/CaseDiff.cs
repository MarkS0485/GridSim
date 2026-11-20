using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: read-only case diff over the native JSON files of two cases. No physics and no model load: it compares
// the authored documents, so what it reports is what an author changed. Records inside an array are matched by
// identity (id, then name, then from-to or bus with an occurrence count), so a permuted record order is "reordered",
// not "changed"; a field is changed only when its JSON value differs (numbers compare as IEEE doubles, so 1 and 1.0 are
// the same value).

public enum CaseDiffKind { Changed, Added, Removed, Reordered, FileAdded, FileRemoved }

/// <summary>One difference: the file, a readable path to the field (<c>buses.json[id=2].pd</c>), the old and new values
/// as JSON text (null when absent) and the unit read from the case schema's field title (empty when it has none).</summary>
public sealed record CaseDiffEntry(CaseDiffKind Kind, string File, string Path, string? Old, string? New, string Unit);

public sealed record CaseDiffResult(IReadOnlyList<CaseDiffEntry> Entries, bool BytesIdentical)
{
    /// <summary>True when no authored value differs (formatting alone may still differ: see <see cref="BytesIdentical"/>).</summary>
    public bool Empty => Entries.Count == 0;
}

public static class CaseDiff
{
    /// <summary>Compare two cases given as file label to exact bytes (a v1 folder's files by name, or a v2 manifest and
    /// its network files by <c>network/name</c>). Files are compared in the union of the labels, in ordinal order.</summary>
    public static CaseDiffResult Compare(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var entries = new List<CaseDiffEntry>();
        bool bytesSame = a.Count == b.Count;
        foreach (string file in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            bool inA = a.TryGetValue(file, out byte[]? x), inB = b.TryGetValue(file, out byte[]? y);
            if (!inA) { entries.Add(new(CaseDiffKind.FileAdded, file, file, null, null, "")); bytesSame = false; continue; }
            if (!inB) { entries.Add(new(CaseDiffKind.FileRemoved, file, file, null, null, "")); bytesSame = false; continue; }
            if (!x!.AsSpan().SequenceEqual(y!)) bytesSame = false;
            using JsonDocument da = Parse(x!, file), db = Parse(y!, file);
            new Walker(entries, file).Value(da.RootElement, db.RootElement, file, "");
        }
        return new CaseDiffResult(entries, bytesSame);
    }

    private static JsonDocument Parse(byte[] bytes, string file)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new StudyCaseException($"{file} is not valid JSON: {ex.Message}"); }
    }

    private sealed class Walker(List<CaseDiffEntry> entries, string file)
    {
        public void Value(JsonElement a, JsonElement b, string path, string member)
        {
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object) { Object(a, b, path, member); return; }
            if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array) { Array(a, b, path, member); return; }
            if (!Same(a, b)) entries.Add(new(CaseDiffKind.Changed, file, path, a.GetRawText(), b.GetRawText(), UnitOf(path, member)));
        }

        private void Object(JsonElement a, JsonElement b, string path, string parentMember)
        {
            var ma = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var mb = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (string name in ma.Keys.Union(mb.Keys).Order(StringComparer.Ordinal))
            {
                string at = path == file ? file + ":" + name : path + "." + name;
                bool inA = ma.TryGetValue(name, out JsonElement va), inB = mb.TryGetValue(name, out JsonElement vb);
                if (inA && inB) Value(va, vb, at, name);
                else if (inA) entries.Add(new(CaseDiffKind.Removed, file, at, va.GetRawText(), null, UnitOf(at, name)));
                else entries.Add(new(CaseDiffKind.Added, file, at, null, vb.GetRawText(), UnitOf(at, name)));
            }
        }

        private void Array(JsonElement a, JsonElement b, string path, string member)
        {
            JsonElement[] ea = a.EnumerateArray().ToArray(), eb = b.EnumerateArray().ToArray();
            string[]? ka = Keys(ea, eb, out string? mode, out string[]? kb) ? Keys(ea, mode!) : null;
            if (ka is null)
            {
                // Not records, or no identity that both sides support: compare by position.
                for (int i = 0; i < Math.Max(ea.Length, eb.Length); i++)
                {
                    string at = $"{path}[{i}]";
                    if (i >= ea.Length) entries.Add(new(CaseDiffKind.Added, file, at, null, eb[i].GetRawText(), ""));
                    else if (i >= eb.Length) entries.Add(new(CaseDiffKind.Removed, file, at, ea[i].GetRawText(), null, ""));
                    else Value(ea[i], eb[i], at, member);
                }
                return;
            }
            var ia = ka.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
            var ib = kb!.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
            foreach (string key in ka)
            {
                string at = $"{path}[{key}]";
                if (ib.TryGetValue(key, out int j)) Value(ea[ia[key]], eb[j], at, member);
                else entries.Add(new(CaseDiffKind.Removed, file, at, ea[ia[key]].GetRawText(), null, ""));
            }
            foreach (string key in kb!)
                if (!ia.ContainsKey(key)) entries.Add(new(CaseDiffKind.Added, file, $"{path}[{key}]", null, eb[ib[key]].GetRawText(), ""));
            string[] common = ka.Where(ib.ContainsKey).ToArray();
            string[] commonInB = kb!.Where(ia.ContainsKey).ToArray();
            if (!common.SequenceEqual(commonInB))
                entries.Add(new(CaseDiffKind.Reordered, file, path, string.Join(",", common), string.Join(",", commonInB), ""));
        }

        // The identity rule. Tried in order, the first that every record of BOTH sides supports with unique values.
        private static readonly string[][] Rules = [["id"], ["name"], ["from", "to"], ["bus"]];

        private static bool Keys(JsonElement[] a, JsonElement[] b, out string? mode, out string[]? keysB)
        {
            mode = null; keysB = null;
            if (a.Length == 0 && b.Length == 0) return false;
            if (a.Any(e => e.ValueKind != JsonValueKind.Object) || b.Any(e => e.ValueKind != JsonValueKind.Object)) return false;
            foreach (string[] rule in Rules)
            {
                string key = string.Join("+", rule);
                string[]? ka = Keys(a, key), kb = Keys(b, key);
                if (ka is not null && kb is not null) { mode = key; keysB = kb; return true; }
            }
            return false;
        }

        /// <summary>The keys of the records under one rule, or null when a record lacks it or two share a plain key.
        /// The endpoint and bus rules number repeats (<c>1-2#2</c>), so parallel circuits stay distinct.</summary>
        private static string[]? Keys(JsonElement[] records, string mode)
        {
            string[] fields = mode.Split('+');
            bool counted = mode is "from+to" or "bus";
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var keys = new string[records.Length];
            for (int i = 0; i < records.Length; i++)
            {
                var parts = new List<string>();
                foreach (string f in fields)
                {
                    JsonProperty? p = null;
                    foreach (JsonProperty q in records[i].EnumerateObject())
                        if (string.Equals(q.Name, f, StringComparison.OrdinalIgnoreCase)) { p = q; break; }
                    if (p is null || p.Value.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null) return null;
                    parts.Add(p.Value.Value.ValueKind == JsonValueKind.String ? p.Value.Value.GetString()! : p.Value.Value.GetRawText());
                }
                string baseKey = fields.Length == 2 ? parts[0] + "-" + parts[1] : fields[0] + "=" + parts[0];
                int n = seen.TryGetValue(baseKey, out int c) ? c + 1 : 1;
                seen[baseKey] = n;
                if (!counted && n > 1) return null;
                keys[i] = counted && n > 1 ? baseKey + "#" + n : baseKey;
            }
            return keys;
        }

        private static bool Same(JsonElement a, JsonElement b)
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
                return a.GetDouble().Equals(b.GetDouble());
            if (a.ValueKind != b.ValueKind) return false;
            return a.ValueKind switch
            {
                JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
                _ => true // true, false, null: equal kinds are equal
            };
        }

        private string UnitOf(string path, string member)
        {
            if (member.Length == 0) return "";
            try
            {
                RecordShape? shape = ShapeOf(path);
                string title = shape?.FieldIgnoringCase(member)?.Title ?? "";
                int open = title.LastIndexOf('(');
                int close = title.LastIndexOf(')');
                if (open < 0 || close < open) return "";
                string inner = title[(open + 1)..close];
                int comma = inner.IndexOf(',');
                return (comma < 0 ? inner : inner[..comma]).Trim();
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                return "";
            }
        }

        private static readonly Dictionary<string, string> V2Defs = new(StringComparer.Ordinal)
        {
            ["buses"] = "bus", ["generators"] = "generator", ["demands"] = "demand", ["lines"] = "line",
            ["transformers"] = "transformer", ["oltcs"] = "oltc", ["boundaries"] = "boundary", ["protections"] = "protection",
        };

        private RecordShape? ShapeOf(string path)
        {
            CaseFileSpec? spec = CaseFileSpec.ForFile(file);
            if (spec is not null && spec.IsArray) return FieldCatalogue.V1RecordShape(spec);
            if (spec is not null) return FieldCatalogue.V1RecordShape(spec);
            // A v2 network file: the member array named before the first record key.
            string rest = path.StartsWith(file, StringComparison.Ordinal) ? path[file.Length..] : path;
            int dot = rest.IndexOf(':');
            if (dot < 0) return null;
            string member = rest[(dot + 1)..];
            int bracket = member.IndexOf('[');
            if (bracket < 0) return null;
            string array = member[..bracket];
            return V2Defs.TryGetValue(array, out string? def)
                ? FieldCatalogue.V2.Shape(new SchemaLocation("network.schema.json", "/$defs/" + def)) : null;
        }
    }
}
