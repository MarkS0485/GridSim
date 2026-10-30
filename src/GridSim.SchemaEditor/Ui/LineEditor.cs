using System.Text;

namespace GridSim.SchemaEditor.Ui;

/// <summary>What a key did to a line being edited.</summary>
internal enum LineResult
{
    /// <summary>Still editing.</summary>
    Editing,

    /// <summary>Enter: accept the text.</summary>
    Accepted,

    /// <summary>Esc: abandon the edit.</summary>
    Cancelled,
}

/// <summary>
/// Pure in-place text editing of one line: caret, Home / End, Left / Right, Backspace / Delete, printable characters.
/// When opened with <c>replaceOnFirstKey</c> (Enter on a field) the first printable key replaces the whole text, the
/// classic "type over" behaviour; F2 opens with it off, keeping the text and putting the caret at the end.
/// </summary>
internal sealed class LineEditor
{
    private readonly StringBuilder _text;
    private bool _replaceOnFirstKey;

    public LineEditor(string text, bool replaceOnFirstKey)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = new StringBuilder(text);
        Caret = text.Length;
        _replaceOnFirstKey = replaceOnFirstKey;
    }

    /// <summary>The current text.</summary>
    public string Text => _text.ToString();

    /// <summary>The caret position (0 = before the first character).</summary>
    public int Caret { get; private set; }

    /// <summary>True until the first key when the text will be replaced by typing.</summary>
    public bool WillReplace => _replaceOnFirstKey;

    /// <summary>Apply one key.</summary>
    public LineResult Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter: return LineResult.Accepted;
            case ConsoleKey.Escape: return LineResult.Cancelled;
            case ConsoleKey.LeftArrow: _replaceOnFirstKey = false; Caret = Math.Max(0, Caret - 1); return LineResult.Editing;
            case ConsoleKey.RightArrow: _replaceOnFirstKey = false; Caret = Math.Min(_text.Length, Caret + 1); return LineResult.Editing;
            case ConsoleKey.Home: _replaceOnFirstKey = false; Caret = 0; return LineResult.Editing;
            case ConsoleKey.End: _replaceOnFirstKey = false; Caret = _text.Length; return LineResult.Editing;
            case ConsoleKey.Backspace:
                if (_replaceOnFirstKey) { _text.Clear(); Caret = 0; _replaceOnFirstKey = false; return LineResult.Editing; }
                if (Caret > 0) { _text.Remove(Caret - 1, 1); Caret--; }
                return LineResult.Editing;
            case ConsoleKey.Delete:
                if (_replaceOnFirstKey) { _text.Clear(); Caret = 0; _replaceOnFirstKey = false; return LineResult.Editing; }
                if (Caret < _text.Length) _text.Remove(Caret, 1);
                return LineResult.Editing;
        }
        char c = key.KeyChar;
        if (c == '\0' || char.IsControl(c) || (key.Modifiers & ConsoleModifiers.Control) != 0) return LineResult.Editing;
        if (_replaceOnFirstKey) { _text.Clear(); Caret = 0; _replaceOnFirstKey = false; }
        _text.Insert(Caret, c);
        Caret++;
        return LineResult.Editing;
    }
}
