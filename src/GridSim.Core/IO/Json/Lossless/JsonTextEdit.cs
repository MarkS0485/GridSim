using System.Text;

namespace GridSim.Core.IO.Json.Lossless;

/// <summary>
/// One byte patch of a file: at <see cref="Offset"/>, remove <see cref="RemoveLength"/> bytes and insert
/// <see cref="Insert"/> in their place. A pure insertion has RemoveLength 0; a pure removal has an empty Insert.
/// Every change the case editor makes to a file is a list of these, so the bytes nobody edited are never touched.
/// </summary>
internal sealed record JsonTextEdit(int Offset, int RemoveLength, byte[] Insert)
{
    /// <summary>A patch inserting UTF-8 text.</summary>
    public static JsonTextEdit InsertText(int offset, string text) => new(offset, 0, Encoding.UTF8.GetBytes(text));

    /// <summary>A patch replacing a byte range with UTF-8 text.</summary>
    public static JsonTextEdit ReplaceText(int offset, int removeLength, string text) => new(offset, removeLength, Encoding.UTF8.GetBytes(text));

    /// <summary>A patch removing a byte range.</summary>
    public static JsonTextEdit Remove(int offset, int removeLength) => new(offset, removeLength, Array.Empty<byte>());

    /// <summary>The inserted text decoded as UTF-8 (for messages and tests).</summary>
    public string InsertedText => Encoding.UTF8.GetString(Insert);

    /// <summary>
    /// Apply <paramref name="edits"/> to <paramref name="text"/>. The edits are given in ORIGINAL coordinates, must be
    /// sorted by offset and must not overlap (two insertions at the same offset are allowed and applied in list order;
    /// a removal may not reach into the next edit's start). Anything else is refused with an
    /// <see cref="ArgumentException"/>, because a silently reordered or merged patch could corrupt a file.
    ///
    /// Returns the new bytes and the inverse edits, in NEW coordinates, sorted, which turn the new bytes back into
    /// <paramref name="text"/> exactly (the undo patch): each inverse edit removes what was inserted and puts back
    /// what was removed.
    /// </summary>
    public static (byte[] Text, IReadOnlyList<JsonTextEdit> Inverse) Apply(ReadOnlySpan<byte> text, IReadOnlyList<JsonTextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        // Check every edit lies inside the text and that they are sorted and disjoint.
        int previousEnd = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            JsonTextEdit e = edits[i];
            ArgumentNullException.ThrowIfNull(e);
            ArgumentNullException.ThrowIfNull(e.Insert);
            if (e.Offset < 0 || e.RemoveLength < 0 || e.Offset + e.RemoveLength > text.Length)
                throw new ArgumentException($"edit {i} (offset {e.Offset}, remove {e.RemoveLength}) lies outside the text of {text.Length} bytes.", nameof(edits));
            if (e.Offset < previousEnd)
                throw new ArgumentException($"edit {i} at offset {e.Offset} overlaps or precedes the previous edit (which ends at {previousEnd}); edits must be sorted and disjoint.", nameof(edits));
            previousEnd = e.Offset + e.RemoveLength;
        }

        int newLength = text.Length;
        foreach (JsonTextEdit e in edits) newLength += e.Insert.Length - e.RemoveLength;

        var result = new byte[newLength];
        var inverse = new List<JsonTextEdit>(edits.Count);
        int read = 0;    // next unread byte of the original
        int write = 0;   // next byte to write in the result
        foreach (JsonTextEdit e in edits)
        {
            // Copy the untouched run before this edit.
            int run = e.Offset - read;
            text.Slice(read, run).CopyTo(result.AsSpan(write));
            write += run;

            // The inverse, in new coordinates: at 'write', remove what we insert and restore what we remove.
            inverse.Add(new JsonTextEdit(write, e.Insert.Length, text.Slice(e.Offset, e.RemoveLength).ToArray()));

            e.Insert.CopyTo(result.AsSpan(write));
            write += e.Insert.Length;
            read = e.Offset + e.RemoveLength;
        }
        text[read..].CopyTo(result.AsSpan(write));
        return (result, inverse);
    }
}
