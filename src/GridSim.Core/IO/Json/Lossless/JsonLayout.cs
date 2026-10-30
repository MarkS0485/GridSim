using System.Text;

namespace GridSim.Core.IO.Json.Lossless;

/// <summary>
/// The layout of one container (object or array) as written in a file, detected from its bytes so that text the
/// editor inserts looks like its neighbours.
/// </summary>
/// <param name="MultiLine">True when the container's children start on their own lines.</param>
/// <param name="ChildIndent">The whitespace before each child on its line (multi-line containers).</param>
/// <param name="IndentUnit">One level of indentation (the child indent minus the container's own line indent).</param>
/// <param name="Separator">What goes between two children: a comma plus the newline and indent (multi-line), or a comma and an optional space (single-line).</param>
/// <param name="Colon">What goes between a member name and its value (": " or ":"), taken from the first member.</param>
/// <param name="Pad">The space just inside the braces of a single-line container ("{ " versus "{").</param>
internal sealed record JsonContainerStyle(bool MultiLine, string ChildIndent, string IndentUnit, string Separator, string Colon, string Pad);

/// <summary>
/// A value the editor creates from scratch (a new record, a new member value), as an ordered tree of literals. It is
/// formatted into text by <see cref="JsonLayout.Format"/> in the style of a neighbour. Literal text must already be
/// valid JSON (use <see cref="JsonLiteral"/> to make it).
/// </summary>
internal sealed class JsonFresh
{
    private JsonFresh(string? literal, IReadOnlyList<KeyValuePair<string, JsonFresh>>? members, IReadOnlyList<JsonFresh>? elements)
    {
        Literal = literal;
        Members = members;
        Elements = elements;
    }

    /// <summary>The literal text of a scalar (already valid JSON), or null for a container.</summary>
    public string? Literal { get; }

    /// <summary>The members of an object, in the order they will be written; null for a non-object.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonFresh>>? Members { get; }

    /// <summary>The elements of an array; null for a non-array.</summary>
    public IReadOnlyList<JsonFresh>? Elements { get; }

    /// <summary>A scalar from its JSON literal text (for example "1300", "\"Drax\"", "true").</summary>
    public static JsonFresh Scalar(string literal)
    {
        ArgumentException.ThrowIfNullOrEmpty(literal);
        return new JsonFresh(literal, null, null);
    }

    /// <summary>An object with members in the given order.</summary>
    public static JsonFresh Object(IEnumerable<KeyValuePair<string, JsonFresh>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return new JsonFresh(null, members.ToList(), null);
    }

    /// <summary>An array of the given elements.</summary>
    public static JsonFresh Array(IEnumerable<JsonFresh> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new JsonFresh(null, null, elements.ToList());
    }

    /// <summary>
    /// A fresh copy of an existing JSON value in which every scalar is RE-WRITTEN by <see cref="JsonLiteral"/>
    /// (numbers shortest round-trip, strings with minimal escaping). This is the fresh-record writer applied to real
    /// data; the P6 proof checks that it loses nothing.
    /// </summary>
    public static JsonFresh FromElement(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                return Object(element.EnumerateObject().Select(p => new KeyValuePair<string, JsonFresh>(p.Name, FromElement(p.Value))));
            case System.Text.Json.JsonValueKind.Array:
                return Array(element.EnumerateArray().Select(FromElement));
            case System.Text.Json.JsonValueKind.String:
                return Scalar(JsonLiteral.FormatString(element.GetString()!));
            case System.Text.Json.JsonValueKind.Number:
            {
                // An integral literal that fits a long is written as digits (an int field must never become "1E+20"
                // or "1.0"); anything else as the shortest round-trip double. "-0" is the exception: as digits it
                // would lose its sign, and a double field read from it is -0.0, so it goes the double way ("-0").
                string raw = element.GetRawText();
                bool digitsOnly = !raw.Contains('.', StringComparison.Ordinal) && !raw.Contains('e', StringComparison.OrdinalIgnoreCase);
                bool negativeZero = raw.StartsWith('-') && element.TryGetInt64(out long z) && z == 0;
                return Scalar(digitsOnly && !negativeZero && element.TryGetInt64(out long l)
                    ? JsonLiteral.FormatInteger(l)
                    : JsonLiteral.FormatNumber(element.GetDouble()));
            }
            case System.Text.Json.JsonValueKind.True:
                return Scalar("true");
            case System.Text.Json.JsonValueKind.False:
                return Scalar("false");
            case System.Text.Json.JsonValueKind.Null:
                return Scalar("null");
            default:
                throw new ArgumentException($"cannot copy a JSON value of kind {element.ValueKind}.", nameof(element));
        }
    }
}

/// <summary>
/// Layout detection and fresh-text formatting for the lossless text layer. Everything here reads the original bytes
/// of a <see cref="JsonSourceText"/>; nothing re-serialises existing content.
/// </summary>
internal static class JsonLayout
{
    /// <summary>The indent unit used when a file gives no example of one (Python's indent=2, the commonest bundled style).</summary>
    public const string DefaultIndentUnit = "  ";

    /// <summary>True for a JSON whitespace byte.</summary>
    public static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>
    /// The indentation of the line holding <paramref name="offset"/>, if only whitespace precedes the offset on that
    /// line; null when something else comes before it on the line (the value is not the first thing on its line).
    /// </summary>
    public static string? LineIndentBefore(JsonSourceText text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        int floor = text.HasBom ? 3 : 0;
        int i = offset - 1;
        while (i >= floor && text.ByteAt(i) is (byte)' ' or (byte)'\t') i--;
        if (i >= floor && text.ByteAt(i) != (byte)'\n') return null;
        return text.Slice(i + 1, offset);
    }

    /// <summary>The leading whitespace of the line holding <paramref name="offset"/>, whatever follows it.</summary>
    public static string LineIndentOf(JsonSourceText text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        int floor = text.HasBom ? 3 : 0;
        int lineStart = offset;
        while (lineStart > floor && text.ByteAt(lineStart - 1) != (byte)'\n') lineStart--;
        int end = lineStart;
        while (end < text.Length && text.ByteAt(end) is (byte)' ' or (byte)'\t') end++;
        return text.Slice(lineStart, end);
    }

    /// <summary>
    /// The separator a new sibling should use, derived from the real gap between two existing siblings (from the end of
    /// one to the start of the next): a comma, then the whitespace that ends the gap. A gap ending in a newline plus
    /// indent keeps exactly one newline and that indent (blank lines and comments are not copied); a same-line gap
    /// keeps one space if it had any (alignment padding is not copied).
    /// </summary>
    public static string SeparatorFromGap(JsonSourceText text, int gapStart, int gapEnd)
    {
        ArgumentNullException.ThrowIfNull(text);
        int i = gapEnd;
        while (i > gapStart && IsWhitespace(text.ByteAt(i - 1))) i--;
        string ws = text.Slice(i, gapEnd);
        int lastNl = ws.LastIndexOf('\n');
        if (lastNl >= 0) return "," + text.Newline + ws[(lastNl + 1)..];
        return ws.Length > 0 ? ", " : ",";
    }

    /// <summary>Detect the layout of an object or array.</summary>
    public static JsonContainerStyle Detect(JsonSourceText text, JsonSourceNode container)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(container);
        if (!container.IsContainer) throw new ArgumentException("layout is detected on an object or array only.", nameof(container));

        List<(int Start, int End)> children = container.Kind == JsonSourceKind.Object
            ? container.Members.Select(m => (m.Start, m.End)).ToList()
            : container.Elements.Select(e => (e.Start, e.End)).ToList();

        string ownIndent = LineIndentOf(text, container.Start);

        // Multi-line: the first child starts on a later line than the opening bracket (or, for an empty container,
        // the brackets are on different lines).
        int firstStart = children.Count > 0 ? children[0].Start : container.End - 1;
        bool multiLine = text.Slice(container.Start, firstStart).Contains('\n', StringComparison.Ordinal);

        string childIndent = children.Count > 0 ? LineIndentBefore(text, children[0].Start) ?? ownIndent + DefaultIndentUnit
                                                : ownIndent + DefaultIndentUnit;
        string unit = childIndent.StartsWith(ownIndent, StringComparison.Ordinal) && childIndent.Length > ownIndent.Length
            ? childIndent[ownIndent.Length..]
            : DefaultIndentUnit;

        string separator;
        if (children.Count >= 2) separator = SeparatorFromGap(text, children[0].End, children[1].Start);
        else separator = multiLine ? "," + text.Newline + childIndent : ", ";
        // A multi-line container whose detected gap stayed on one line (two children on the first line) still gets
        // its new children on their own lines.
        if (multiLine && !separator.Contains('\n', StringComparison.Ordinal)) separator = "," + text.Newline + childIndent;

        string colon = ": ";
        if (container.Kind == JsonSourceKind.Object && container.Members.Count > 0)
        {
            JsonSourceMember m = container.Members[0];
            string gap = text.Slice(m.NameEnd, m.Value.Start);
            if (!gap.Contains('/', StringComparison.Ordinal) && !gap.Contains('\n', StringComparison.Ordinal)) colon = gap;
        }

        string pad = " ";
        if (!multiLine)
        {
            string inside = text.Slice(container.Start + 1, firstStart);
            pad = inside.Length > 0 && inside.All(c => c == ' ' || c == '\t') ? " " : (children.Count > 0 ? "" : " ");
        }

        return new JsonContainerStyle(multiLine, childIndent, unit, separator, colon, pad);
    }

    /// <summary>
    /// Format a fresh value. <paramref name="style"/> is the style of a neighbouring record of the same shape (null
    /// when there is none, which gives one record per line: <c>{ "a": 1, "b": 2 }</c>). <paramref name="baseIndent"/>
    /// is the indent of the line the value starts on (needed for the closing brace of a multi-line value).
    /// </summary>
    public static string Format(JsonFresh value, JsonContainerStyle? style, string baseIndent, string newline)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(baseIndent);
        ArgumentNullException.ThrowIfNull(newline);
        var sb = new StringBuilder();
        Write(sb, value, style, baseIndent, newline);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonFresh v, JsonContainerStyle? style, string indent, string nl)
    {
        if (v.Literal is not null) { sb.Append(v.Literal); return; }

        bool isObject = v.Members is not null;
        int count = isObject ? v.Members!.Count : v.Elements!.Count;
        char open = isObject ? '{' : '[';
        char close = isObject ? '}' : ']';
        if (count == 0) { sb.Append(open).Append(close); return; }

        string colon = style?.Colon ?? ": ";
        if (style is { MultiLine: true })
        {
            string inner = indent + style.IndentUnit;
            sb.Append(open);
            for (int i = 0; i < count; i++)
            {
                sb.Append(i == 0 ? "" : ",").Append(nl).Append(inner);
                WriteChild(sb, v, i, colon, style, inner, nl);
            }
            sb.Append(nl).Append(indent).Append(close);
        }
        else
        {
            string pad = style?.Pad ?? " ";
            string sep = style is null ? ", " : (style.Separator.EndsWith(' ') ? ", " : ",");
            sb.Append(open).Append(pad);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(sep);
                WriteChild(sb, v, i, colon, style, indent, nl);
            }
            sb.Append(pad).Append(close);
        }
    }

    private static void WriteChild(StringBuilder sb, JsonFresh v, int i, string colon, JsonContainerStyle? style, string indent, string nl)
    {
        if (v.Members is not null)
        {
            KeyValuePair<string, JsonFresh> m = v.Members[i];
            sb.Append(JsonLiteral.FormatString(m.Key)).Append(colon);
            Write(sb, m.Value, style, indent, nl);
        }
        else Write(sb, v.Elements![i], style, indent, nl);
    }
}
