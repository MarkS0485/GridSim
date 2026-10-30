using GridSim.Core.IO.Json.CaseEditing;
using GridSim.SchemaEditor.Ui;
using Xunit;

namespace GridSim.Tests.Editor;

/// <summary>
/// Drives the case editor's state machine with real key presses, exactly as the console host does (Handle, then
/// EffectRunner.Run), and renders the screen into an 80 x 25 buffer for assertions. No console is involved.
/// </summary>
internal sealed class EditorDriver
{
    public const int Width = 80;
    public const int Height = 25;

    public EditorDriver(CaseSession session, string displayPath = "case")
    {
        Editor = new GridSim.SchemaEditor.Ui.Editor(session, displayPath);
        Screen();   // a first frame, as the host draws before the first key
    }

    /// <summary>The editor under test.</summary>
    public GridSim.SchemaEditor.Ui.Editor Editor { get; }

    /// <summary>Hook passed to the committer on saves (simulated failures).</summary>
    public Action<string>? BeforeReplace { get; set; }

    /// <summary>Render the current screen.</summary>
    public ScreenBuffer Screen(int width = Width, int height = Height)
    {
        var b = new ScreenBuffer(width, height);
        Renderer.Render(Editor, b);
        return b;
    }

    /// <summary>
    /// Press keys: a <see cref="ConsoleKey"/> presses that key; a string types each character; a
    /// <see cref="ConsoleKeyInfo"/> is passed as is (for Ctrl chords).
    /// </summary>
    public EditorDriver Keys(params object[] keys)
    {
        foreach (object k in keys)
        {
            switch (k)
            {
                case ConsoleKey key: Press(new ConsoleKeyInfo(CharOf(key), key, false, false, false)); break;
                case ConsoleKeyInfo info: Press(info); break;
                case string text: foreach (char c in text) Press(KeyOf(c)); break;
                default: throw new ArgumentException($"cannot press {k}");
            }
        }
        return this;
    }

    /// <summary>Ctrl plus a letter key.</summary>
    public static ConsoleKeyInfo Ctrl(ConsoleKey key) => new((char)(key - ConsoleKey.A + 1), key, false, false, true);

    /// <summary>Move the cursor down (with real Down presses) until the highlighted row contains <paramref name="text"/>.</summary>
    public EditorDriver Select(string text)
    {
        Keys(ConsoleKey.Home);
        int n = Editor.Top.Visible.Count;
        for (int i = 0; i < n; i++)
        {
            if (Editor.Top.View.Row(Editor.Top.CurrentRow).Contains(text, StringComparison.Ordinal)) return this;
            Keys(ConsoleKey.DownArrow);
        }
        Assert.Fail($"no row containing '{text}' in {Editor.Top.View.Crumb}:{Environment.NewLine}{Screen()}");
        return this;
    }

    /// <summary>The text of the highlighted row.</summary>
    public string CurrentRow => Editor.Top.View.Row(Editor.Top.CurrentRow);

    private void Press(ConsoleKeyInfo key)
    {
        EditorEffect effect = Editor.Handle(key);
        EffectRunner.Run(Editor, effect, BeforeReplace);
        Screen();
    }

    private static char CharOf(ConsoleKey key) => key switch
    {
        ConsoleKey.Enter => '\r',
        ConsoleKey.Escape => '\u001b',
        ConsoleKey.Backspace => '\b',
        ConsoleKey.Tab => '\t',
        ConsoleKey.Spacebar => ' ',
        _ => '\0',
    };

    private static ConsoleKeyInfo KeyOf(char c)
    {
        ConsoleKey key = c switch
        {
            >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            ' ' => ConsoleKey.Spacebar,
            '.' => ConsoleKey.OemPeriod,
            '-' => ConsoleKey.OemMinus,
            '/' or '?' => ConsoleKey.Oem2,
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo(c, key, char.IsUpper(c) || c == '?', false, false);
    }
}
