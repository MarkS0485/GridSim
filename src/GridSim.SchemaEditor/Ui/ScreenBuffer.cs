namespace GridSim.SchemaEditor.Ui;

/// <summary>How one screen cell is drawn. Four plain styles, so any terminal can show them.</summary>
internal enum CellStyle : byte
{
    Normal,
    Reverse,
    Bold,
    Dim,
}

/// <summary>
/// An off-screen picture of the console: characters and styles, row by row. The renderer draws into it (pure, no
/// Console calls), the host copies only the rows that changed to the real console, and the tests read rows back as
/// text. One <see cref="char"/> is one column (risk R3: fine for Latin-1 names, wide CJK would misalign).
/// </summary>
internal sealed class ScreenBuffer
{
    private readonly char[] _chars;
    private readonly CellStyle[] _styles;

    public ScreenBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"a screen of {width}x{height} has no cells.");
        Width = width;
        Height = height;
        _chars = new char[width * height];
        _styles = new CellStyle[width * height];
        Array.Fill(_chars, ' ');
    }

    /// <summary>Columns.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    /// <summary>Where the text caret should show (column, row), or null to hide it.</summary>
    public (int X, int Y)? Caret { get; set; }

    /// <summary>
    /// Write text at (x, y) in a style, clipped to the row (and to <paramref name="maxWidth"/> when given). Control
    /// characters are shown as '?' so a stray tab or newline in a name can never break the layout.
    /// </summary>
    public void Put(int x, int y, string text, CellStyle style = CellStyle.Normal, int maxWidth = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (y < 0 || y >= Height) return;
        int limit = Math.Min(Width, maxWidth == int.MaxValue ? Width : x + maxWidth);
        for (int i = 0; i < text.Length && x + i < limit; i++)
        {
            if (x + i < 0) continue;
            char c = text[i];
            _chars[y * Width + x + i] = char.IsControl(c) ? '?' : c;
            _styles[y * Width + x + i] = style;
        }
    }

    /// <summary>Fill a whole row with spaces in a style (for the reverse-video title and key bars, and the highlight).</summary>
    public void FillRow(int y, CellStyle style)
    {
        if (y < 0 || y >= Height) return;
        for (int x = 0; x < Width; x++)
        {
            _chars[y * Width + x] = ' ';
            _styles[y * Width + x] = style;
        }
    }

    /// <summary>Set the style of a span of a row without changing its characters.</summary>
    public void Style(int x, int y, int length, CellStyle style)
    {
        if (y < 0 || y >= Height) return;
        for (int i = Math.Max(0, x); i < Math.Min(Width, x + length); i++) _styles[y * Width + i] = style;
    }

    /// <summary>One row as text (trailing spaces kept, so a row is always exactly <see cref="Width"/> characters).</summary>
    public string RowText(int y) => new(_chars, y * Width, Width);

    /// <summary>The style of one cell.</summary>
    public CellStyle StyleAt(int x, int y) => _styles[y * Width + x];

    /// <summary>True when row <paramref name="y"/> has the same characters and styles in both buffers (same size).</summary>
    public bool RowEquals(ScreenBuffer other, int y)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Width != Width || other.Height != Height) return false;
        return _chars.AsSpan(y * Width, Width).SequenceEqual(other._chars.AsSpan(y * Width, Width))
            && _styles.AsSpan(y * Width, Width).SequenceEqual(other._styles.AsSpan(y * Width, Width));
    }

    /// <summary>The whole screen as text, one line per row (for test failure messages).</summary>
    public override string ToString() => string.Join(Environment.NewLine, Enumerable.Range(0, Height).Select(RowText));
}
