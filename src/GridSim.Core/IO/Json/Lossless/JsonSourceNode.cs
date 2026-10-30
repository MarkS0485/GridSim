namespace GridSim.Core.IO.Json.Lossless;

/// <summary>The JSON value kinds a <see cref="JsonSourceNode"/> can hold (comments are not values; they stay in the bytes).</summary>
internal enum JsonSourceKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>
/// One member of a JSON object inside a <see cref="JsonSourceText"/>: where its name sits in the bytes, the decoded
/// name, and its value. The member's own span runs from the opening quote of the name to the end of the value
/// (<see cref="Start"/> to <see cref="End"/>); the separator (comma, whitespace, comments) around it is not part of it.
/// </summary>
internal sealed class JsonSourceMember
{
    public JsonSourceMember(int nameStart, int nameEnd, string name, JsonSourceNode value)
    {
        NameStart = nameStart;
        NameEnd = nameEnd;
        Name = name;
        Value = value;
    }

    /// <summary>Byte offset of the opening quote of the member name.</summary>
    public int NameStart { get; }

    /// <summary>Byte offset just after the closing quote of the member name.</summary>
    public int NameEnd { get; }

    /// <summary>The member name, decoded (escapes resolved), with its case exactly as written in the file.</summary>
    public string Name { get; }

    /// <summary>The member's value.</summary>
    public JsonSourceNode Value { get; }

    /// <summary>Start of the whole member (the name's opening quote).</summary>
    public int Start => NameStart;

    /// <summary>End of the whole member (just after the value).</summary>
    public int End => Value.End;
}

/// <summary>
/// One JSON value inside a <see cref="JsonSourceText"/>, as a byte span of the ORIGINAL file plus what that span
/// decodes to. The span is half-open: <see cref="Start"/> is the first byte of the token (the quote of a string, the
/// first digit or minus sign of a number, the opening brace or bracket of a container) and <see cref="End"/> is one
/// past its last byte. Nothing here ever re-serialises: the bytes of the file are the model, and every edit is a
/// patch of those bytes (see <see cref="JsonTextOps"/>).
/// </summary>
internal sealed class JsonSourceNode
{
    private static readonly IReadOnlyList<JsonSourceMember> NoMembers = Array.Empty<JsonSourceMember>();
    private static readonly IReadOnlyList<JsonSourceNode> NoElements = Array.Empty<JsonSourceNode>();

    private JsonSourceNode(JsonSourceKind kind, int start, int end)
    {
        Kind = kind;
        Start = start;
        End = end;
    }

    /// <summary>What kind of JSON value this is.</summary>
    public JsonSourceKind Kind { get; }

    /// <summary>Byte offset of the first byte of the value.</summary>
    public int Start { get; }

    /// <summary>Byte offset one past the last byte of the value.</summary>
    public int End { get; }

    /// <summary>Length of the value's span in bytes.</summary>
    public int Length => End - Start;

    /// <summary>The decoded string for a <see cref="JsonSourceKind.String"/> node; null for every other kind.</summary>
    public string? StringValue { get; private init; }

    /// <summary>The number literal exactly as written (ASCII) for a <see cref="JsonSourceKind.Number"/> node; null otherwise.</summary>
    public string? NumberText { get; private init; }

    /// <summary>
    /// The number as System.Text.Json reads it into a double (the same parser the loader's DTO deserialisation
    /// uses), for a <see cref="JsonSourceKind.Number"/> node whose literal fits a double; null otherwise.
    /// </summary>
    public double? NumberValue { get; private init; }

    /// <summary>The number as a 64-bit integer when the literal is integral digits that fit; null otherwise.</summary>
    public long? IntegerValue { get; private init; }

    /// <summary>The members of an object, in file order; empty for every other kind.</summary>
    public IReadOnlyList<JsonSourceMember> Members { get; private set; } = NoMembers;

    /// <summary>The elements of an array, in file order; empty for every other kind.</summary>
    public IReadOnlyList<JsonSourceNode> Elements { get; private set; } = NoElements;

    /// <summary>True for an object or an array.</summary>
    public bool IsContainer => Kind is JsonSourceKind.Object or JsonSourceKind.Array;

    /// <summary>True for a JSON true or false.</summary>
    public bool IsBoolean => Kind is JsonSourceKind.True or JsonSourceKind.False;

    /// <summary>
    /// The member whose name equals <paramref name="name"/> under <paramref name="comparer"/>, or null. A v1 file is
    /// looked up OrdinalIgnoreCase (the loader is case-insensitive), a v2 file Ordinal. Duplicate names were refused
    /// at parse time under the same comparer, so at most one member can match.
    /// </summary>
    public JsonSourceMember? FindMember(string name, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        foreach (JsonSourceMember m in Members)
            if (comparer.Equals(m.Name, name)) return m;
        return null;
    }

    /// <summary>The index of the member named <paramref name="name"/> (see <see cref="FindMember"/>), or -1.</summary>
    public int IndexOfMember(string name, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        for (int i = 0; i < Members.Count; i++)
            if (comparer.Equals(Members[i].Name, name)) return i;
        return -1;
    }

    // -----------------------------------------------------------------------------------------------------------
    // Factories used only by the parser in JsonSourceText.
    // -----------------------------------------------------------------------------------------------------------

    internal static JsonSourceNode MakeScalar(JsonSourceKind kind, int start, int end) => new(kind, start, end);

    internal static JsonSourceNode MakeString(int start, int end, string value)
        => new(JsonSourceKind.String, start, end) { StringValue = value };

    internal static JsonSourceNode MakeNumber(int start, int end, string text, double? value, long? integer)
        => new(JsonSourceKind.Number, start, end) { NumberText = text, NumberValue = value, IntegerValue = integer };

    internal static JsonSourceNode MakeObject(int start, int end, List<JsonSourceMember> members)
        => new(JsonSourceKind.Object, start, end) { Members = members };

    internal static JsonSourceNode MakeArray(int start, int end, List<JsonSourceNode> elements)
        => new(JsonSourceKind.Array, start, end) { Elements = elements };
}
