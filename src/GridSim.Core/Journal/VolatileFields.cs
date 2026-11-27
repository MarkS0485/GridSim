using System.Text.Json;

namespace GridSim.Core.Journal;

/// <summary>
/// The fields of a result that carry build identity, run identity or timing, never a computed value, and so are left
/// out when a re-run's result is compared with the recorded one (the embedded <c>volatile-fields.json</c>, read once).
/// The list is derived from the result schemas: each entry names the writer that puts the field there. The census test
/// (two runs of every writer verb on one build) checks that it is complete.
/// </summary>
/// <remarks>
/// 2.1.127: a document may also name the output file it applies to (<c>"file"</c>: a name such as
/// <c>run-metadata.json</c>, or <c>*.ext</c> for a suffix; absent means any file), for result files that carry no schema
/// tag (the run-metadata sidecar). A path that starts with <c>#</c> names a CSV comment line instead of a JSON field: the
/// line's text after <c>#</c> and its spaces starts with the rest of the path (the run header lines that
/// <c>RunInfo.HeaderLines</c> writes at the top of a CSV).
/// </remarks>
public sealed class VolatileFields
{
    private readonly List<(string Schema, string Verb, string Path)> _fields;
    private readonly List<(string Schema, string Verb, string File, string Path)> _all;

    private VolatileFields(List<(string, string, string, string)> all)
    {
        _all = all;
        _fields = all.Select(f => (f.Item1, f.Item2, f.Item4)).ToList();
    }

    /// <summary>The resource name of the embedded list.</summary>
    public const string ResourceName = "GridSim.Core.volatile-fields.json";

    private static readonly Lazy<VolatileFields> Embedded = new(() =>
    {
        using Stream s = typeof(VolatileFields).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("the volatile-field list is not embedded in this build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Parse(ms.ToArray());
    });

    /// <summary>The list compiled into this build.</summary>
    public static VolatileFields Default => Embedded.Value;

    /// <summary>Read a <c>gridsim-volatile-fields/1</c> document.</summary>
    public static VolatileFields Parse(byte[] json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        if (d.RootElement.GetProperty("schema").GetString() != "gridsim-volatile-fields/1")
            throw new InvalidDataException("not a gridsim-volatile-fields/1 document.");
        var list = new List<(string, string, string, string)>();
        foreach (JsonElement doc in d.RootElement.GetProperty("documents").EnumerateArray())
        {
            string schema = doc.GetProperty("schema").GetString()!;
            string verb = doc.GetProperty("verb").GetString()!;
            string file = doc.TryGetProperty("file", out JsonElement fe) && fe.ValueKind == JsonValueKind.String ? fe.GetString()! : "*";
            foreach (JsonElement f in doc.GetProperty("fields").EnumerateArray())
                list.Add((schema, verb, file, f.GetProperty("path").GetString()!));
        }
        return new VolatileFields(list);
    }

    /// <summary>Every listed path, with its schema and verb.</summary>
    public IReadOnlyList<(string Schema, string Verb, string Path)> Fields => _fields;

    /// <summary>The paths that apply to a document of <paramref name="schema"/> (null for none) written by
    /// <paramref name="verb"/>.</summary>
    public IReadOnlyList<string> For(string? schema, string verb) =>
        _all.Where(f => f.File == "*" && (f.Schema == "*" || f.Schema == schema) && (f.Verb == "*" || f.Verb == verb)
            && !f.Path.StartsWith('#')).Select(f => f.Path).ToList();

    /// <summary>The JSON paths that apply to the output file <paramref name="file"/> (its name), a document of
    /// <paramref name="schema"/> (null for none) written by <paramref name="verb"/>: those of <see cref="For(string?, string)"/>
    /// and those of the documents that name this file.</summary>
    public IReadOnlyList<string> For(string? schema, string verb, string file) =>
        _all.Where(f => FileMatches(f.File, file) && (f.Schema == "*" || f.Schema == schema) && (f.Verb == "*" || f.Verb == verb)
            && !f.Path.StartsWith('#')).Select(f => f.Path).ToList();

    /// <summary>The CSV comment-line prefixes masked in the output file <paramref name="file"/> written by
    /// <paramref name="verb"/>: a comment line (one that starts with <c>#</c>) whose text, after the <c>#</c> and its
    /// spaces, starts with one of these is left out.</summary>
    public IReadOnlyList<string> CsvCommentPrefixes(string verb, string file) =>
        _all.Where(f => FileMatches(f.File, file) && (f.Verb == "*" || f.Verb == verb) && f.Path.StartsWith('#'))
            .Select(f => f.Path[1..]).ToList();

    /// <summary>True when the file pattern <paramref name="pattern"/> (<c>*</c>, <c>*.suffix</c> or a name) names
    /// <paramref name="file"/>. Names compare ignoring case.</summary>
    public static bool FileMatches(string pattern, string file)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(file);
        if (pattern == "*") return true;
        string name = System.IO.Path.GetFileName(file);
        if (pattern.StartsWith('*')) return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the collapsed JSON path <paramref name="path"/> (<c>$.a.b[*].c</c>) is one of
    /// <paramref name="patterns"/>. Key names compare ignoring case.</summary>
    public static bool Matches(string path, IReadOnlyList<string> patterns)
    {
        foreach (string p in patterns)
        {
            if (p.StartsWith("**.", StringComparison.Ordinal))
            {
                string tail = p[2..];   // ".a.b"
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(path, p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The CSV columns (and key,value CSV keys) masked for <paramref name="verb"/>: the last name of every
    /// <c>$.tables.&lt;t&gt;[*].&lt;c&gt;</c> and <c>$.summary.&lt;k&gt;</c> path of the shared result schema.</summary>
    public IReadOnlyList<string> CsvNames(string verb)
    {
        var names = new List<string>();
        foreach (string p in For("gridsim-cli-result/1", verb))
        {
            if (p.StartsWith("$.tables.", StringComparison.Ordinal) && p.Contains("[*].", StringComparison.Ordinal))
                names.Add(p[(p.LastIndexOf('.') + 1)..]);
            else if (p.StartsWith("$.summary.", StringComparison.Ordinal))
                names.Add(p["$.summary.".Length..]);
        }
        return names;
    }
}
