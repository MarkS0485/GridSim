using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Lossless;

/// <summary>
/// One JSON file held as its ORIGINAL bytes plus a span tree over them (the lossless text layer of the case editor,
/// SCHEMA-EDITOR-PLAN.md section 4, "the bytes are the model").
///
/// Why not JsonNode or a DTO round trip: re-serialising loses what a hand-maintained case file is made of. Strings are
/// re-escaped (the uk files' raw '&amp;' becomes "&"; intl names with raw UTF-8 letters become "ß" escapes),
/// alignment and blank lines vanish, comments cannot be kept, and the bundled cases use at least four different
/// layouts (hand-aligned uk, Python indent=2 gb-full, STJ-written gb-synthetic, Python intl with raw non-ASCII). So
/// this class never writes JSON: it parses the bytes once with <see cref="Utf8JsonReader"/> (comments allowed and
/// left in the bytes, trailing commas allowed), records where every value sits, and every change is a byte patch
/// (<see cref="JsonTextEdit"/>, built by <see cref="JsonTextOps"/>) after which the file is parsed again.
///
/// What is detected and kept: a UTF-8 byte-order mark, the newline style (the first newline in the file decides,
/// "\r\n" or "\n"), and whether the file ends with a newline. None of them is ever changed by an edit; new text a
/// patch inserts uses the detected newline.
///
/// Duplicate member names are refused at parse time under <see cref="KeyComparer"/>: OrdinalIgnoreCase for a v1
/// case file (the loader matches keys case-insensitively, so "bus" and "Bus" in one record are the same member and the
/// later would silently win) and Ordinal for a v2 document. Instances are immutable; a patch produces a new one.
/// </summary>
internal sealed class JsonSourceText
{
    /// <summary>The three bytes of a UTF-8 byte-order mark.</summary>
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// The reader settings: comments are reported as tokens (and skipped by the tree builder, so they stay in the
    /// bytes untouched) and trailing commas are allowed, the same leniency as the v1 loader's serializer options.
    /// </summary>
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Allow,
        AllowTrailingCommas = true,
        MaxDepth = 256,
    };

    /// <summary>The document options matching <see cref="ReaderOptions"/>, for building a <see cref="JsonDocument"/>.</summary>
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 256,
    };

    private readonly byte[] _bytes;

    private JsonSourceText(byte[] bytes, string documentName, StringComparer keyComparer, JsonSourceNode root,
                           bool hasBom, string newline, bool trailingNewline)
    {
        _bytes = bytes;
        DocumentName = documentName;
        KeyComparer = keyComparer;
        Root = root;
        HasBom = hasBom;
        Newline = newline;
        TrailingNewline = trailingNewline;
    }

    /// <summary>The name used in every error message (normally the file name, for example "generation.json").</summary>
    public string DocumentName { get; }

    /// <summary>How member names are compared: OrdinalIgnoreCase for v1 case files, Ordinal for v2 documents.</summary>
    public StringComparer KeyComparer { get; }

    /// <summary>The root value of the document.</summary>
    public JsonSourceNode Root { get; }

    /// <summary>True when the file starts with a UTF-8 byte-order mark (kept on every save).</summary>
    public bool HasBom { get; }

    /// <summary>The file's newline: "\r\n" or "\n" (the first newline decides; "\n" when the file has none).</summary>
    public string Newline { get; }

    /// <summary>True when the last byte of the file is a newline.</summary>
    public bool TrailingNewline { get; }

    /// <summary>The file's bytes, exactly as read (or as produced by the last patch).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Number of bytes in the file.</summary>
    public int Length => _bytes.Length;

    /// <summary>A copy of the bytes (for writing to disk or comparing with a baseline).</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    /// <summary>The text of a byte range, decoded as UTF-8 (for messages, tests and layout detection).</summary>
    public string Slice(int start, int end) => Encoding.UTF8.GetString(_bytes, start, end - start);

    /// <summary>The source text of one value: exactly the bytes of its span.</summary>
    public string RawText(JsonSourceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Slice(node.Start, node.End);
    }

    /// <summary>The byte at an offset.</summary>
    public byte ByteAt(int offset) => _bytes[offset];

    /// <summary>
    /// Parse a file's bytes. Throws <see cref="InvalidDataException"/> naming <paramref name="documentName"/> on
    /// malformed JSON, an empty file, or a duplicate member name.
    /// </summary>
    public static JsonSourceText Parse(byte[] bytes, string documentName, StringComparer keyComparer)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(documentName);
        ArgumentNullException.ThrowIfNull(keyComparer);

        // The BOM is not JSON; the reader is handed the bytes after it and every offset is shifted back by its length,
        // so all spans stay offsets into the WHOLE file (and a patch never touches the BOM).
        bool hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        int baseOffset = hasBom ? Utf8Bom.Length : 0;

        JsonSourceNode root;
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(baseOffset), ReaderOptions);
            if (!ReadSkippingComments(ref reader))
                throw new InvalidDataException($"{documentName}: the file holds no JSON value (it is empty or only comments).");
            root = ParseValue(ref reader, baseOffset, keyComparer, documentName, "");

            // After the root value only comments (and whitespace) may follow; the reader itself refuses a second value.
            while (reader.Read())
                if (reader.TokenType != JsonTokenType.Comment)
                    throw new InvalidDataException($"{documentName}: unexpected content after the root value at byte {baseOffset + reader.TokenStartIndex}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{documentName}: not valid JSON: {ex.Message}", ex);
        }

        // Newline style: the first newline in the file decides. A file with no newline at all is single-line, and
        // any multi-line text a patch inserts into it uses "\n".
        int firstLf = bytes.AsSpan().IndexOf((byte)'\n');
        string newline = firstLf > 0 && bytes[firstLf - 1] == (byte)'\r' ? "\r\n" : "\n";
        bool trailingNewline = bytes.Length > 0 && bytes[^1] == (byte)'\n';

        return new JsonSourceText(bytes, documentName, keyComparer, root, hasBom, newline, trailingNewline);
    }

    /// <summary>Read a file from disk and parse it (see <see cref="Parse(byte[], string, StringComparer)"/>).</summary>
    public static JsonSourceText Load(string path, StringComparer keyComparer)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path), keyComparer);
    }

    /// <summary>Parse new bytes with the same document name and key comparer (used after every patch).</summary>
    public JsonSourceText Reparse(byte[] bytes) => Parse(bytes, DocumentName, KeyComparer);

    /// <summary>
    /// Apply a set of edits (sorted, non-overlapping; see <see cref="JsonTextEdit.Apply"/>) and re-parse. Returns the
    /// new text and the inverse edits that turn the new text back into this one (the undo patch).
    /// </summary>
    public (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) Apply(IReadOnlyList<JsonTextEdit> edits)
    {
        (byte[] next, IReadOnlyList<JsonTextEdit> inverse) = JsonTextEdit.Apply(_bytes, edits);
        return (Reparse(next), inverse);
    }

    /// <summary>
    /// The document as a detached <see cref="JsonElement"/> (comments skipped, trailing commas allowed), for schema
    /// validation and deep comparison. The element is a clone, so no <see cref="JsonDocument"/> is left to dispose.
    /// </summary>
    public JsonElement ToElement()
    {
        using JsonDocument doc = JsonDocument.Parse(_bytes.AsMemory(HasBom ? Utf8Bom.Length : 0), DocumentOptions);
        return doc.RootElement.Clone();
    }

    /// <summary>One value of the document as a detached <see cref="JsonElement"/> (its span parsed on its own).</summary>
    public JsonElement ElementOf(JsonSourceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using JsonDocument doc = JsonDocument.Parse(_bytes.AsMemory(node.Start, node.Length), DocumentOptions);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Resolve an RFC 6901 JSON pointer ("" is the root, "/3/capacityMva" is member capacityMva of element 3) to a
    /// node, or null when any step is missing. Object steps use <see cref="KeyComparer"/>.
    /// </summary>
    public JsonSourceNode? Resolve(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0) return Root;
        if (pointer[0] != '/') return null;
        JsonSourceNode node = Root;
        foreach (string raw in pointer[1..].Split('/'))
        {
            string token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node.Kind == JsonSourceKind.Object)
            {
                JsonSourceMember? m = node.FindMember(token, KeyComparer);
                if (m is null) return null;
                node = m.Value;
            }
            else if (node.Kind == JsonSourceKind.Array)
            {
                // Array steps are plain decimal indices (no leading zeros, no sign), as RFC 6901 requires.
                if (token.Length == 0 || (token.Length > 1 && token[0] == '0') || !token.All(char.IsAsciiDigit)) return null;
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i >= node.Elements.Count) return null;
                node = node.Elements[i];
            }
            else return null;
        }
        return node;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The tree builder
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Advance to the next token that is not a comment. False at the end of the input.</summary>
    private static bool ReadSkippingComments(ref Utf8JsonReader reader)
    {
        while (reader.Read())
            if (reader.TokenType != JsonTokenType.Comment) return true;
        return false;
    }

    /// <summary>
    /// Build the node for the value the reader is positioned on, consuming the whole value (for a container, up to
    /// and including its closing brace or bracket). <paramref name="baseOffset"/> is added to every reader position
    /// (the BOM length). <paramref name="pointer"/> is the value's JSON pointer, for error messages only.
    /// </summary>
    private static JsonSourceNode ParseValue(ref Utf8JsonReader reader, int baseOffset, StringComparer comparer,
                                             string documentName, string pointer)
    {
        int start = baseOffset + checked((int)reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var members = new List<JsonSourceMember>();
                var seen = new HashSet<string>(comparer);
                while (true)
                {
                    if (!ReadSkippingComments(ref reader))
                        throw new InvalidDataException($"{documentName}: an object at byte {start} is never closed.");
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        int end = baseOffset + checked((int)reader.TokenStartIndex) + 1;
                        return JsonSourceNode.MakeObject(start, end, members);
                    }

                    // A property name: the reader's ValueSpan is the raw (still escaped) name without its quotes, so
                    // the name's span is that length plus the two quote bytes.
                    int nameStart = baseOffset + checked((int)reader.TokenStartIndex);
                    int nameEnd = nameStart + reader.ValueSpan.Length + 2;
                    string name = reader.GetString()!;
                    string memberPointer = pointer + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                    if (!seen.Add(name))
                        throw new InvalidDataException(
                            $"{documentName}: duplicate member '{name}' at {memberPointer} (byte {nameStart}). " +
                            (ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase)
                                ?"Member names are compared ignoring case, as the v1 loader does, so the later one would silently replace the earlier."
                                : "The later one would silently replace the earlier."));

                    if (!ReadSkippingComments(ref reader))
                        throw new InvalidDataException($"{documentName}: member '{name}' at byte {nameStart} has no value.");
                    JsonSourceNode value = ParseValue(ref reader, baseOffset, comparer, documentName, memberPointer);
                    members.Add(new JsonSourceMember(nameStart, nameEnd, name, value));
                }
            }

            case JsonTokenType.StartArray:
            {
                var elements = new List<JsonSourceNode>();
                while (true)
                {
                    if (!ReadSkippingComments(ref reader))
                        throw new InvalidDataException($"{documentName}: an array at byte {start} is never closed.");
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        int end = baseOffset + checked((int)reader.TokenStartIndex) + 1;
                        return JsonSourceNode.MakeArray(start, end, elements);
                    }
                    string elementPointer = pointer + "/" + elements.Count.ToString(CultureInfo.InvariantCulture);
                    elements.Add(ParseValue(ref reader, baseOffset, comparer, documentName, elementPointer));
                }
            }

            case JsonTokenType.String:
                // ValueSpan is the raw string content between the quotes (escapes not yet resolved).
                return JsonSourceNode.MakeString(start, start + reader.ValueSpan.Length + 2, reader.GetString()!);

            case JsonTokenType.Number:
            {
                int end = start + reader.ValueSpan.Length;
                string text = Encoding.ASCII.GetString(reader.ValueSpan);
                // TryGetDouble is the same parser the DTO deserialiser uses, so NumberValue has exactly the bits the
                // loader would read. A literal outside the double range has no value here (and is refused by the
                // number-input rules before it could ever be written).
                double? value = reader.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
                long? integer = reader.TryGetInt64(out long l) ? l : null;
                return JsonSourceNode.MakeNumber(start, end, text, value, integer);
            }

            case JsonTokenType.True:
                return JsonSourceNode.MakeScalar(JsonSourceKind.True, start, start + reader.ValueSpan.Length);
            case JsonTokenType.False:
                return JsonSourceNode.MakeScalar(JsonSourceKind.False, start, start + reader.ValueSpan.Length);
            case JsonTokenType.Null:
                return JsonSourceNode.MakeScalar(JsonSourceKind.Null, start, start + reader.ValueSpan.Length);

            default:
                throw new InvalidDataException($"{documentName}: unexpected token {reader.TokenType} at byte {start}.");
        }
    }
}
