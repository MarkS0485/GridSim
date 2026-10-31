using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.IO.Json.Schema;

/// <summary>
/// One schema object inside a loaded schema set: the file it lives in, its JSON pointer inside that file, and the
/// JSON object itself. Every place a schema may legally appear (a file root, a <c>$defs</c> entry, a
/// <c>properties</c> entry, <c>items</c>, an object-valued <c>additionalProperties</c>, each <c>oneOf</c>/<c>anyOf</c>
/// branch, and <c>if</c>/<c>then</c>/<c>else</c>) is registered as a node when the set loads, so a <c>$ref</c> may
/// only ever land on a real schema, never on (say) an <c>enum</c> value.
/// </summary>
internal sealed record SchemaNode(string File, string Pointer, JsonElement Element)
{
    /// <summary>The registry key: <c>file#pointer</c>, the same spelling a cross-file <c>$ref</c> uses.</summary>
    public string Key => File + "#" + Pointer;
}

/// <summary>
/// A set of JSON Schema files (the GridSim v2 <c>schemas/v2/*.schema.json</c>) loaded under a deliberately small,
/// hand-rolled subset of JSON Schema 2020-12. Zero dependencies: System.Text.Json only.
///
/// Why a subset, and why so strict: the <c>.schema.json</c> files are the single source of truth for the v2 file
/// formats and are actually enforced by <see cref="SchemaValidator"/>. A full JSON Schema implementation would be a
/// large dependency; a partial one that silently ignored keywords it did not understand would be worse than none,
/// because a schema author could write <c>"minLength": 1</c> and believe it was enforced. So loading REJECTS, loudly,
/// any keyword outside <see cref="SupportedKeywords"/>, and checks the shape of every supported keyword too.
///
/// What loading checks (every problem is collected, then one <see cref="InvalidDataException"/> lists them all,
/// each naming the file and the JSON pointer):
/// <list type="bullet">
/// <item>every file parses (comments and trailing commas allowed, duplicate member names refused);</item>
/// <item>every schema object uses only supported keywords, each with a value of the right shape;</item>
/// <item><c>$schema</c> and <c>$id</c> appear only at a file root; <c>$schema</c> is the 2020-12 URI and
/// <c>$id</c> equals the file name, so a cross-file <c>$ref</c> by file name is unambiguous;</item>
/// <item>every <c>$ref</c> resolves, local (<c>#/$defs/x</c>) or cross-file (<c>common.schema.json#/$defs/quantity</c>),
/// to a registered schema node;</item>
/// <item>no <c>$ref</c> cycle exists that never consumes input (for example <c>a</c> refers to <c>b</c> which
/// refers back to <c>a</c> at the same instance location): validation would recurse forever.</item>
/// </list>
/// </summary>
public sealed class JsonSchemaSubset
{
    /// <summary>
    /// The complete supported keyword list. Anything else in a schema object is a load error. Kept in exactly the
    /// order the dream-of-json-ification plan lists them, so a reviewer can compare the two by eye.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedKeywords = new[]
    {
        "$schema", "$id", "$ref", "$defs", "type", "properties", "required", "additionalProperties", "items",
        "enum", "const", "minimum", "maximum", "exclusiveMinimum", "minItems", "pattern", "oneOf", "anyOf",
        "if", "then", "else", "title", "description",
    };

    /// <summary>The only <c>$schema</c> dialect a v2 schema file may declare.</summary>
    public const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The file-name suffix a schema file must carry to be picked up by <see cref="LoadDirectory"/>.</summary>
    public const string SchemaFileSuffix = ".schema.json";

    /// <summary>
    /// How every v2 document (schema, kind spec, library, case) is read. This replicates the reader leniency of
    /// <c>JsonGridLoader</c>'s private serializer options (<c>ReadCommentHandling = Skip</c>,
    /// <c>AllowTrailingCommas = true</c>; see <c>JsonGridLoader.Opt</c>) so a hand-edited v2 file may be annotated the
    /// same way a v1 file can. Those options are private to the loader, which this work must not modify, hence the
    /// copy rather than a shared reference. One deliberate addition: duplicate member names are refused, because in
    /// a document read as a tree the later duplicate would silently win, which is the same silent-drop hazard R268
    /// closed for v1 unknown keys.
    /// </summary>
    public static readonly JsonDocumentOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
    };

    private static readonly HashSet<string> Supported = new(SupportedKeywords, StringComparer.Ordinal);

    private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "number", "integer", "boolean", "null",
    };

    // file name -> root element (a detached clone, so no JsonDocument has to be kept alive or disposed).
    private readonly Dictionary<string, JsonElement> _roots = new(StringComparer.Ordinal);
    // file#pointer -> schema node, for every place a schema may legally appear.
    private readonly Dictionary<string, SchemaNode> _nodes = new(StringComparer.Ordinal);
    // file#pointer of a node carrying $ref -> the node that $ref resolves to.
    private readonly Dictionary<string, SchemaNode> _refTargets = new(StringComparer.Ordinal);
    // pattern text -> compiled regex, built once at load (an invalid pattern is a load error, not a validate error).
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    private JsonSchemaSubset() { }

    /// <summary>The loaded schema file names, sorted ordinally.</summary>
    public IReadOnlyList<string> Files => _roots.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>How many <c>$ref</c>s the set contains (every one of them resolved, or loading would have thrown).</summary>
    public int RefCount => _refTargets.Count;

    /// <summary>True when a schema file of this exact name is in the set.</summary>
    public bool Contains(string file) => _roots.ContainsKey(file);

    /// <summary>
    /// Load every <c>*.schema.json</c> directly inside <paramref name="dir"/> (not subfolders: <c>kinds/</c> holds
    /// kind specs, which are documents validated BY a schema, not schemas). Throws on a missing or empty directory.
    /// </summary>
    public static JsonSchemaSubset LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Schema directory not found: {dir}");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(dir, "*" + SchemaFileSuffix, SearchOption.TopDirectoryOnly)
                                          .OrderBy(p => p, StringComparer.Ordinal))
            sources[Path.GetFileName(path)] = File.ReadAllText(path);
        if (sources.Count == 0) throw new InvalidDataException($"Schema directory {dir} holds no *{SchemaFileSuffix} files.");
        return FromSources(sources);
    }

    /// <summary>
    /// Load a schema set from in-memory text, keyed by file name (the name a cross-file <c>$ref</c> uses). This is
    /// what <see cref="LoadDirectory"/> calls, and what unit tests use to build hand-made schemas.
    /// </summary>
    public static JsonSchemaSubset FromSources(IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new InvalidDataException("A schema set needs at least one schema file.");

        var set = new JsonSchemaSubset();
        var errors = new List<string>();

        // Pass 1: parse every file. A file that does not parse cannot be walked, but the others still are, so the
        // author sees every problem at once.
        foreach ((string file, string text) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (file.Contains('/', StringComparison.Ordinal) || file.Contains('\\', StringComparison.Ordinal) || file.Contains('#', StringComparison.Ordinal))
            {
                errors.Add($"{file}: a schema file name must be a plain file name (no '/', '\\' or '#').");
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text, ReaderOptions);
                set._roots[file] = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                errors.Add($"{file}: not valid JSON: {ex.Message}");
            }
        }

        // Pass 2: walk every schema object, registering nodes and checking every keyword.
        var pendingRefs = new List<(SchemaNode From, string Ref)>();
        foreach ((string file, JsonElement root) in set._roots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            set.Walk(file, "", root, isRoot: true, errors, pendingRefs);

        // Pass 3: resolve every $ref now that every node is registered (a ref may point forward or into another file).
        foreach ((SchemaNode from, string reference) in pendingRefs)
        {
            SchemaNode? target = set.TryResolve(from.File, reference, out string why);
            if (target is null) errors.Add($"{from.File} at '{Show(from.Pointer)}': $ref '{reference}' {why}");
            else set._refTargets[from.Key] = target;
        }

        // Pass 4: only once every ref resolves can the in-place cycle check run meaningfully.
        if (errors.Count == 0) set.FindInPlaceCycles(errors);

        if (errors.Count > 0)
            throw new InvalidDataException("Invalid v2 schema set (" + errors.Count + " problem(s)):" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", errors));
        return set;
    }

    /// <summary>
    /// Parse a v2 document from disk with <see cref="ReaderOptions"/>, naming the file in any parse error (the
    /// JsonGridLoader.Deserialize pattern: the reader's own message has the line and position but not the file).
    /// </summary>
    public static JsonDocument ParseFile(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path), ReaderOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    /// <summary>The root schema node of a file in the set (throws, naming the file, if it is not in the set).</summary>
    internal SchemaNode RootOf(string file)
        => _nodes.TryGetValue(file + "#", out SchemaNode? n) ? n
         : throw new InvalidDataException($"Schema file '{file}' is not in the loaded schema set ({string.Join(", ", Files)}).");

    /// <summary>
    /// The schema node at a JSON pointer inside a file of the set (for example <c>/$defs/generator</c>; a leading
    /// <c>#</c> is accepted). Throws, naming the file and pointer, when no schema is registered there. Added for the
    /// case editor's per-record validation (SCHEMA-EDITOR-PLAN.md, slice E2); additive, nothing else uses it.
    /// </summary>
    internal SchemaNode NodeAt(string file, string pointer)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(pointer);
        string p = pointer.StartsWith('#') ? pointer[1..] : pointer;
        RootOf(file);   // names the file and the loaded set when the file is not in it
        return _nodes.TryGetValue(file + "#" + p, out SchemaNode? n) ? n
             : throw new InvalidDataException($"Schema file '{file}' has no schema at '{Show(p)}'.");
    }

    /// <summary>The node a <c>$ref</c>-bearing node points at (resolved at load, so this never misses).</summary>
    internal SchemaNode RefTarget(SchemaNode node) => _refTargets[node.Key];

    /// <summary>The node registered at a child pointer (every schema-valued keyword was registered at load).</summary>
    internal SchemaNode Child(SchemaNode parent, string childPointerSuffix) => _nodes[parent.File + "#" + parent.Pointer + childPointerSuffix];

    /// <summary>The compiled regex for a pattern string (compiled and checked at load).</summary>
    internal Regex PatternFor(string pattern) => _patterns[pattern];

    /// <summary>Escape one JSON pointer reference token (RFC 6901: '~' becomes '~0', '/' becomes '~1').</summary>
    public static string EscapeToken(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Display a pointer: the root pointer is the empty string, which reads badly in a message.</summary>
    internal static string Show(string pointer) => pointer.Length == 0 ? "(root)" : pointer;

    // ---------------------------------------------------------------------------------------------------------------
    // Walking and keyword checks
    // ---------------------------------------------------------------------------------------------------------------

    private void Walk(string file, string pointer, JsonElement schema, bool isRoot, List<string> errors,
                      List<(SchemaNode, string)> pendingRefs)
    {
        string where = $"{file} at '{Show(pointer)}'";
        if (schema.ValueKind != JsonValueKind.Object)
        {
            // Boolean schemas (true/false) are only accepted as the value of additionalProperties, handled by the
            // caller; anywhere else a non-object is an authoring error, not a schema.
            errors.Add($"{where}: a schema must be a JSON object, found {schema.ValueKind}.");
            return;
        }

        var node = new SchemaNode(file, pointer, schema);
        _nodes[node.Key] = node;

        bool hasIf = schema.TryGetProperty("if", out _);
        foreach (JsonProperty kw in schema.EnumerateObject())
        {
            string kwPointer = pointer + "/" + EscapeToken(kw.Name);
            string at = $"{file} at '{kwPointer}'";
            JsonElement v = kw.Value;

            if (!Supported.Contains(kw.Name))
            {
                errors.Add($"{at}: unsupported schema keyword '{kw.Name}'. The v2 validator supports only: " +
                           string.Join(" ", SupportedKeywords) + ". An unsupported keyword is refused rather than " +
                           "silently ignored, so a schema can never be half-enforced.");
                continue;
            }

            switch (kw.Name)
            {
                case "$schema":
                    if (!isRoot) errors.Add($"{at}: '$schema' is only allowed at a file root.");
                    else if (v.ValueKind != JsonValueKind.String || v.GetString() != Draft202012)
                        errors.Add($"{at}: '$schema' must be \"{Draft202012}\".");
                    break;

                case "$id":
                    if (!isRoot) errors.Add($"{at}: '$id' is only allowed at a file root (embedded resources are not supported).");
                    else if (v.ValueKind != JsonValueKind.String || v.GetString() != file)
                        errors.Add($"{at}: '$id' must equal the file name \"{file}\", so a cross-file $ref by name is unambiguous.");
                    break;

                case "$ref":
                    if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))
                        errors.Add($"{at}: '$ref' must be a non-empty string.");
                    else pendingRefs.Add((node, v.GetString()!));
                    break;

                case "$defs":
                case "properties":
                    if (v.ValueKind != JsonValueKind.Object) { errors.Add($"{at}: '{kw.Name}' must be an object of schemas."); break; }
                    foreach (JsonProperty sub in v.EnumerateObject())
                        Walk(file, kwPointer + "/" + EscapeToken(sub.Name), sub.Value, isRoot: false, errors, pendingRefs);
                    break;

                case "type":
                    CheckType(v, at, errors);
                    break;

                case "required":
                    CheckStringArray(v, at, kw.Name, errors);
                    break;

                case "additionalProperties":
                    // The one place a boolean schema is allowed: false forbids unknown members, true is a no-op.
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) break;
                    Walk(file, kwPointer, v, isRoot: false, errors, pendingRefs);
                    break;

                case "items":
                    // Only the single-schema form; the old tuple (array) form is not part of 2020-12 'items'.
                    Walk(file, kwPointer, v, isRoot: false, errors, pendingRefs);
                    break;

                case "if":
                case "then":
                case "else":
                    if (kw.Name != "if" && !hasIf)
                        errors.Add($"{at}: '{kw.Name}' without a sibling 'if' would be silently ignored; add the 'if' or remove it.");
                    Walk(file, kwPointer, v, isRoot: false, errors, pendingRefs);
                    break;

                case "oneOf":
                case "anyOf":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
                    { errors.Add($"{at}: '{kw.Name}' must be a non-empty array of schemas."); break; }
                    for (int i = 0; i < v.GetArrayLength(); i++)
                        Walk(file, kwPointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), v[i], isRoot: false, errors, pendingRefs);
                    break;

                case "enum":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
                    { errors.Add($"{at}: 'enum' must be a non-empty array."); break; }
                    for (int i = 0; i < v.GetArrayLength(); i++)
                        for (int j = 0; j < i; j++)
                            if (JsonElement.DeepEquals(v[i], v[j]))
                                errors.Add($"{at}: 'enum' lists the same value twice (entries {j} and {i}).");
                    break;

                case "const":
                    break;   // any JSON value is a legal const

                case "minimum":
                case "maximum":
                case "exclusiveMinimum":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double bound) || !double.IsFinite(bound))
                        errors.Add($"{at}: '{kw.Name}' must be a finite number.");
                    break;

                case "minItems":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int mi) || mi < 0)
                        errors.Add($"{at}: 'minItems' must be a non-negative integer.");
                    break;

                case "pattern":
                    if (v.ValueKind != JsonValueKind.String) { errors.Add($"{at}: 'pattern' must be a string."); break; }
                    CompilePattern(v.GetString()!, at, errors);
                    break;

                case "title":
                case "description":
                    if (v.ValueKind != JsonValueKind.String) errors.Add($"{at}: '{kw.Name}' must be a string.");
                    break;
            }
        }
    }

    private static void CheckType(JsonElement v, string at, List<string> errors)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            if (!TypeNames.Contains(v.GetString()!)) errors.Add($"{at}: unknown type name '{v.GetString()}'.");
            return;
        }
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
        {
            errors.Add($"{at}: 'type' must be a type name or a non-empty array of type names.");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement t in v.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String || !TypeNames.Contains(t.GetString()!))
                errors.Add($"{at}: unknown type name {t.GetRawText()}.");
            else if (!seen.Add(t.GetString()!))
                errors.Add($"{at}: type '{t.GetString()}' is listed twice.");
        }
    }

    private static void CheckStringArray(JsonElement v, string at, string name, List<string> errors)
    {
        if (v.ValueKind != JsonValueKind.Array) { errors.Add($"{at}: '{name}' must be an array of strings."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement s in v.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.String) errors.Add($"{at}: '{name}' must be an array of strings, found {s.ValueKind}.");
            else if (!seen.Add(s.GetString()!)) errors.Add($"{at}: '{name}' lists '{s.GetString()}' twice.");
        }
    }

    /// <summary>
    /// Compile a schema <c>pattern</c>. JSON Schema patterns are ECMA-262 regular expressions and are unanchored
    /// (a match anywhere in the string passes), which .NET's <see cref="Regex.IsMatch(string)"/> also is. One real
    /// difference matters for validation: without the multiline option, .NET's <c>$</c> also matches just before a
    /// final newline, while ECMA-262's <c>$</c> matches only at the very end. Left alone, <c>^gridsim\.x$</c> would
    /// accept <c>"gridsim.x\n"</c>. So every unescaped <c>$</c> outside a character class is rewritten to
    /// <c>\z</c> (absolute end), which is exactly the ECMA-262 meaning. Schema authors should still write ASCII
    /// classes such as <c>[0-9]</c> rather than <c>\d</c>, because .NET's <c>\d</c> also matches non-ASCII digits.
    /// </summary>
    private void CompilePattern(string pattern, string at, List<string> errors)
    {
        if (_patterns.ContainsKey(pattern)) return;
        try
        {
            // Linear-time engine, no match timeout (2.1.000, found by the first GitHub macOS CI run): the backtracking
            // engine with a 1 s wall-clock timeout reported six plain gb-full names as "could not be checked against
            // the pattern \S" on a loaded runner, so whether a document validated depended on machine load. The
            // non-backtracking engine runs in time linear in the input and cannot run away, so no timeout is needed and
            // the verdict is the same on every machine. It refuses lookarounds, backreferences and atomic groups (no
            // GridSim schema uses them); such a pattern is reported here as a schema error, never guessed.
            _patterns[pattern] = new Regex(ToDotNetAnchors(pattern), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException ex)
        {
            errors.Add($"{at}: 'pattern' uses a construct the linear-time matcher does not support (lookaround, backreference or atomic group): {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{at}: 'pattern' is not a valid regular expression: {ex.Message}");
        }
    }

    /// <summary>Rewrite each unescaped '$' outside a [...] class to '\z' (see <see cref="CompilePattern"/>).</summary>
    internal static string ToDotNetAnchors(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                // An escape: copy it and the escaped character through untouched (so '\$' and '\]' stay literal).
                sb.Append(c).Append(pattern[i + 1]);
                i++;
                continue;
            }
            if (inClass)
            {
                if (c == ']') inClass = false;
                sb.Append(c);
                continue;
            }
            if (c == '[') { inClass = true; sb.Append(c); continue; }
            if (c == '$') { sb.Append("\\z"); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // $ref resolution
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolve a <c>$ref</c> written in <paramref name="fromFile"/>. Accepted forms: <c>#</c> or <c>#/pointer</c>
    /// (same file), and <c>other.schema.json</c> or <c>other.schema.json#/pointer</c> (another file in the set, by
    /// plain file name). The target must be a registered schema node. Returns null with a reason otherwise.
    /// </summary>
    private SchemaNode? TryResolve(string fromFile, string reference, out string why)
    {
        int hash = reference.IndexOf('#', StringComparison.Ordinal);
        string filePart = hash < 0 ? reference : reference[..hash];
        string fragment = hash < 0 ? "" : reference[(hash + 1)..];
        string file = filePart.Length == 0 ? fromFile : filePart;

        if (!_roots.ContainsKey(file))
        {
            why = $"names file '{file}', which is not in the schema set ({string.Join(", ", Files)}).";
            return null;
        }
        if (fragment.Length > 0 && fragment[0] != '/')
        {
            why = "has a fragment that is not a JSON pointer (anchors are not supported; write '#/$defs/name').";
            return null;
        }
        if (_nodes.TryGetValue(file + "#" + fragment, out SchemaNode? target))
        {
            why = "";
            return target;
        }
        why = $"does not resolve to a schema: there is no schema at '{Show(fragment)}' in '{file}'.";
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Cycle detection
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Find cycles of schema applications that never consume input. Within one instance location, a schema
    /// applies further schemas to the SAME value through <c>$ref</c>, <c>oneOf</c>/<c>anyOf</c> branches and
    /// <c>if</c>/<c>then</c>/<c>else</c>. A cycle among those edges would recurse forever during validation. By
    /// contrast, <c>properties</c>, <c>additionalProperties</c> and <c>items</c> step into a child value, so a
    /// recursive tree schema (a node whose children are nodes) is legal: the instance is finite, so it terminates.
    /// Depth-first search with the usual white/grey/black colouring; a grey-to-grey edge is a cycle.
    /// </summary>
    private void FindInPlaceCycles(List<string> errors)
    {
        var colour = new Dictionary<string, int>(StringComparer.Ordinal);   // absent = white, 1 = grey, 2 = black
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (string start in _nodes.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!colour.ContainsKey(start)) Visit(start);

        void Visit(string key)
        {
            colour[key] = 1;
            stack.Add(key);
            foreach (string next in InPlaceEdges(_nodes[key]))
            {
                if (!colour.TryGetValue(next, out int c)) { Visit(next); continue; }
                if (c != 1) continue;
                // A back edge: the cycle is the stack from 'next' to the top, then back to 'next'.
                int from = stack.IndexOf(next);
                List<string> cycle = stack.Skip(from).Append(next).ToList();
                if (reported.Add(string.Join(" ", cycle.Take(cycle.Count - 1).OrderBy(s => s, StringComparer.Ordinal))))
                    errors.Add("$ref cycle that never consumes input (validation would never terminate): " +
                               string.Join(" -> ", cycle.Select(DisplayKey)));
            }
            stack.RemoveAt(stack.Count - 1);
            colour[key] = 2;
        }
    }

    private IEnumerable<string> InPlaceEdges(SchemaNode node)
    {
        if (_refTargets.TryGetValue(node.Key, out SchemaNode? target)) yield return target.Key;
        foreach (string comb in new[] { "oneOf", "anyOf" })
            if (node.Element.TryGetProperty(comb, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < arr.GetArrayLength(); i++)
                    yield return node.Key + "/" + comb + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (string cond in new[] { "if", "then", "else" })
            if (node.Element.TryGetProperty(cond, out _))
                yield return node.Key + "/" + cond;
    }

    private static string DisplayKey(string key)
    {
        int hash = key.IndexOf('#', StringComparison.Ordinal);
        return $"{key[..hash]} at '{Show(key[(hash + 1)..])}'";
    }
}
