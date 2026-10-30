namespace GridSim.Core.IO.Json.Lossless;

/// <summary>
/// The structural edits of the lossless text layer. Each one looks at a <see cref="JsonSourceText"/> and returns the
/// byte patches (<see cref="JsonTextEdit"/>) that make the change; nothing is applied here, so a caller can combine
/// several, apply them in one go and keep the inverse for undo. Every edit touches only the bytes it must, and new
/// text takes the layout of its neighbours (<see cref="JsonLayout"/>):
/// <list type="bullet">
/// <item>a scalar edit replaces exactly the value token;</item>
/// <item>a member or element insert goes after its predecessor (or before the first child) with the container's own
/// separator: <c>, "k": v</c> on a one-line record, <c>,&lt;newline&gt;&lt;indent&gt;"k": v</c> on a multi-line one;</item>
/// <item>a removal takes the separator BEFORE the child (comma, whitespace and any comment), or the one after it for
/// the first child, and an only child leaves an empty container;</item>
/// <item>a copy duplicates the source's own bytes, so alignment inside the copied record survives.</item>
/// </list>
/// </summary>
internal static class JsonTextOps
{
    /// <summary>Replace one value (normally a scalar) with new literal text. The literal must be valid JSON.</summary>
    public static IReadOnlyList<JsonTextEdit> ReplaceValue(JsonSourceText text, JsonSourceNode node, string literal)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(literal);
        return new[] { JsonTextEdit.ReplaceText(node.Start, node.Length, literal) };
    }

    /// <summary>
    /// Insert a member into an object so that it becomes member number <paramref name="index"/> (0 = first,
    /// Members.Count = last). The value is formatted fresh in the object's style. The caller has already checked the
    /// name is not present (duplicates are refused when the result is re-parsed in any case).
    /// </summary>
    public static IReadOnlyList<JsonTextEdit> InsertMember(JsonSourceText text, JsonSourceNode obj, int index, string name, JsonFresh value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (obj.Kind != JsonSourceKind.Object) throw new ArgumentException("members are inserted into an object only.", nameof(obj));
        if (index < 0 || index > obj.Members.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"the object has {obj.Members.Count} member(s).");

        JsonContainerStyle style = JsonLayout.Detect(text, obj);
        string member = JsonLiteral.FormatString(name) + style.Colon
                      + JsonLayout.Format(value, style, style.MultiLine ? style.ChildIndent : JsonLayout.LineIndentOf(text, obj.Start), text.Newline);
        return InsertChild(text, obj, obj.Members.Select(m => (m.Start, m.End)).ToList(), index, member, style);
    }

    /// <summary>
    /// Insert an element into an array so that it becomes element number <paramref name="index"/>. The element is
    /// formatted fresh; <paramref name="recordStyle"/> is the style its members should take (normally detected from a
    /// neighbouring record with <see cref="JsonLayout.Detect"/>; null gives one record per line).
    /// </summary>
    public static IReadOnlyList<JsonTextEdit> InsertElement(JsonSourceText text, JsonSourceNode array, int index, JsonFresh value, JsonContainerStyle? recordStyle)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(value);
        if (array.Kind != JsonSourceKind.Array) throw new ArgumentException("elements are inserted into an array only.", nameof(array));
        if (index < 0 || index > array.Elements.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"the array has {array.Elements.Count} element(s).");

        JsonContainerStyle style = JsonLayout.Detect(text, array);
        // An empty array gets its first record on its own line (one record per line), whatever the brackets look like.
        string baseIndent = array.Elements.Count == 0 || style.MultiLine ? style.ChildIndent : JsonLayout.LineIndentOf(text, array.Start);
        string element = JsonLayout.Format(value, recordStyle, baseIndent, text.Newline);
        return InsertChild(text, array, array.Elements.Select(e => (e.Start, e.End)).ToList(), index, element, style);
    }

    /// <summary>
    /// Insert an element directly after element <paramref name="index"/>, as an exact copy of that element's bytes
    /// (Duplicate). The separator is the real gap after the source (or before it, for the last element).
    /// </summary>
    public static IReadOnlyList<JsonTextEdit> CopyElement(JsonSourceText text, JsonSourceNode array, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(array);
        if (array.Kind != JsonSourceKind.Array) throw new ArgumentException("elements are copied inside an array only.", nameof(array));
        if (index < 0 || index >= array.Elements.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"the array has {array.Elements.Count} element(s).");

        JsonSourceNode source = array.Elements[index];
        string separator;
        if (index + 1 < array.Elements.Count) separator = JsonLayout.SeparatorFromGap(text, source.End, array.Elements[index + 1].Start);
        else if (index > 0) separator = JsonLayout.SeparatorFromGap(text, array.Elements[index - 1].End, source.Start);
        else separator = JsonLayout.Detect(text, array).Separator;
        return new[] { JsonTextEdit.InsertText(source.End, separator + text.RawText(source)) };
    }

    /// <summary>Insert an element after element <paramref name="afterIndex"/> holding the given raw JSON text (already formatted).</summary>
    public static IReadOnlyList<JsonTextEdit> InsertRawElementAfter(JsonSourceText text, JsonSourceNode array, int afterIndex, string rawElement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(array);
        ArgumentException.ThrowIfNullOrEmpty(rawElement);
        if (array.Kind != JsonSourceKind.Array) throw new ArgumentException("elements are inserted into an array only.", nameof(array));
        if (afterIndex < 0 || afterIndex >= array.Elements.Count) throw new ArgumentOutOfRangeException(nameof(afterIndex), afterIndex, $"the array has {array.Elements.Count} element(s).");
        JsonSourceNode source = array.Elements[afterIndex];
        string separator;
        if (afterIndex + 1 < array.Elements.Count) separator = JsonLayout.SeparatorFromGap(text, source.End, array.Elements[afterIndex + 1].Start);
        else if (afterIndex > 0) separator = JsonLayout.SeparatorFromGap(text, array.Elements[afterIndex - 1].End, source.Start);
        else separator = JsonLayout.Detect(text, array).Separator;
        return new[] { JsonTextEdit.InsertText(source.End, separator + rawElement) };
    }

    /// <summary>Remove member number <paramref name="index"/> of an object, with its separator.</summary>
    public static IReadOnlyList<JsonTextEdit> RemoveMember(JsonSourceText text, JsonSourceNode obj, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(obj);
        if (obj.Kind != JsonSourceKind.Object) throw new ArgumentException("members are removed from an object only.", nameof(obj));
        return RemoveChild(obj, obj.Members.Select(m => (m.Start, m.End)).ToList(), index);
    }

    /// <summary>Remove element number <paramref name="index"/> of an array, with its separator.</summary>
    public static IReadOnlyList<JsonTextEdit> RemoveElement(JsonSourceText text, JsonSourceNode array, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(array);
        if (array.Kind != JsonSourceKind.Array) throw new ArgumentException("elements are removed from an array only.", nameof(array));
        return RemoveChild(array, array.Elements.Select(e => (e.Start, e.End)).ToList(), index);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<JsonTextEdit> InsertChild(JsonSourceText text, JsonSourceNode container,
        List<(int Start, int End)> children, int index, string childText, JsonContainerStyle style)
    {
        if (children.Count == 0)
        {
            // Empty container. When only whitespace sits between the brackets, replace it with a clean layout: a
            // one-line "{ x }" for an object, and for an array the record on its own line between the brackets.
            // When a comment sits inside, insert straight after the opening bracket and leave the comment be.
            int innerStart = container.Start + 1, innerEnd = container.End - 1;
            string inside = text.Slice(innerStart, innerEnd);
            if (!string.IsNullOrWhiteSpace(inside))
                return new[] { JsonTextEdit.InsertText(innerStart, childText) };
            if (container.Kind == JsonSourceKind.Array)
            {
                string closingIndent = JsonLayout.LineIndentOf(text, container.Start);
                return new[] { JsonTextEdit.ReplaceText(innerStart, innerEnd - innerStart,
                    text.Newline + style.ChildIndent + childText + text.Newline + closingIndent) };
            }
            return new[] { JsonTextEdit.ReplaceText(innerStart, innerEnd - innerStart, " " + childText + " ") };
        }

        if (index > 0)
            return new[] { JsonTextEdit.InsertText(children[index - 1].End, style.Separator + childText) };
        return new[] { JsonTextEdit.InsertText(children[0].Start, childText + style.Separator) };
    }

    private static IReadOnlyList<JsonTextEdit> RemoveChild(JsonSourceNode container, List<(int Start, int End)> children, int index)
    {
        if (index < 0 || index >= children.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the container has {children.Count} child(ren).");

        // The only child: empty the container completely ("[]" or "{}"); a trailing comma or comment inside goes too,
        // because "[,]" is not JSON even with trailing commas allowed.
        if (children.Count == 1)
            return new[] { JsonTextEdit.Remove(container.Start + 1, container.End - 1 - (container.Start + 1)) };

        // Not the first: remove from the end of the previous child to the end of this one, which takes the separator
        // before it (comma, whitespace, comments) and leaves whatever followed it attached to the previous child.
        if (index > 0)
            return new[] { JsonTextEdit.Remove(children[index - 1].End, children[index].End - children[index - 1].End) };

        // The first of several: remove from its start to the start of the next child (its separator after it).
        return new[] { JsonTextEdit.Remove(children[0].Start, children[1].Start - children[0].Start) };
    }
}
