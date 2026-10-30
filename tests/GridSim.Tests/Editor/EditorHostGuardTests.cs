using GridSim.Core.IO.Json.CaseEditing;
using GridSim.SchemaEditor;
using GridSim.SchemaEditor.Ui;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// The case editor's console-host guards and key decoding: a key pressed while the window is too small to draw is
/// refused (only the quit keys pass), and Caps Lock never turns the undo key into redo (the case of a letter comes from
/// the Shift key, not the character). Driven through the same pure entry points the console host uses, with no
/// console involved.
/// </summary>
public class EditorHostGuardTests
{
    private static GridSim.SchemaEditor.Ui.Editor OpenUk() =>
        new(CaseSession.OpenV1(CaseDir("uk"), Unlimited()), "data/uk");

    private static ConsoleKeyInfo Key(char c, ConsoleKey key, bool shift) => new(c, key, shift, false, false);

    [Theory]
    [InlineData(79, 24, true)]
    [InlineData(80, 23, true)]
    [InlineData(1, 1, true)]
    [InlineData(80, 24, false)]
    [InlineData(200, 60, false)]
    public void The_minimum_window_is_exactly_80_by_24(int width, int height, bool tooSmall)
    {
        Assert.Equal(tooSmall, Renderer.IsTooSmall(width, height));
    }

    [Fact]
    public void Caps_Lock_U_is_undo_and_only_Shift_makes_it_redo()
    {
        // Plain U: undo, whether the character arrives as 'u' or (Caps Lock on) as 'U'.
        Assert.Equal(Command.Undo, KeyMap.Lookup(Key('u', ConsoleKey.U, shift: false)));
        Assert.Equal(Command.Undo, KeyMap.Lookup(Key('U', ConsoleKey.U, shift: false)));
        // Shift+U: redo, whether the character arrives as 'U' or (Shift with Caps Lock on) as 'u'.
        Assert.Equal(Command.Redo, KeyMap.Lookup(Key('U', ConsoleKey.U, shift: true)));
        Assert.Equal(Command.Redo, KeyMap.Lookup(Key('u', ConsoleKey.U, shift: true)));
    }

    [Fact]
    public void Other_letters_mean_the_same_command_with_or_without_Caps_Lock_and_symbols_are_unchanged()
    {
        Assert.Equal(Command.Duplicate, KeyMap.Lookup(Key('D', ConsoleKey.D, shift: false)));
        Assert.Equal(Command.Duplicate, KeyMap.Lookup(Key('d', ConsoleKey.D, shift: false)));
        Assert.Equal(Command.Quit, KeyMap.Lookup(Key('Q', ConsoleKey.Q, shift: false)));
        Assert.Equal(Command.Help, KeyMap.Lookup(Key('?', ConsoleKey.Oem2, shift: true)));
        Assert.Equal(Command.Filter, KeyMap.Lookup(Key('/', ConsoleKey.Oem2, shift: false)));
        Assert.Equal('u', KeyMap.IntendedChar('U', shift: false));
        Assert.Equal('U', KeyMap.IntendedChar('u', shift: true));
        Assert.Equal('?', KeyMap.IntendedChar('?', shift: true));
    }

    [Fact]
    public void A_key_pressed_while_the_window_is_too_small_is_refused_and_said_so()
    {
        GridSim.SchemaEditor.Ui.Editor e = OpenUk();
        int before = e.Top.Cursor;
        bool handled = ConsoleHost.ProcessKey(e, new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), windowTooSmall: true);
        Assert.False(handled);
        Assert.Equal(before, e.Top.Cursor);                       // the cursor did not move
        Assert.False(e.Session.IsDirty);
        Assert.True(e.MessageIsError);
        Assert.Contains("too small", e.Message, StringComparison.Ordinal);

        // The same key with a window that fits is handled as normal.
        Assert.True(ConsoleHost.ProcessKey(e, new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), windowTooSmall: false));
        Assert.Equal(before + 1, e.Top.Cursor);
    }

    [Fact]
    public void A_duplicate_key_pressed_while_the_window_is_too_small_changes_nothing()
    {
        GridSim.SchemaEditor.Ui.Editor e = OpenUk();
        Assert.False(ConsoleHost.ProcessKey(e, Key('d', ConsoleKey.D, shift: false), windowTooSmall: true));
        Assert.False(ConsoleHost.ProcessKey(e, new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false), windowTooSmall: true));
        Assert.False(e.Session.IsDirty);
    }

    [Fact]
    public void The_quit_key_still_works_while_the_window_is_too_small()
    {
        GridSim.SchemaEditor.Ui.Editor e = OpenUk();
        Assert.True(ConsoleHost.ProcessKey(e, Key('q', ConsoleKey.Q, shift: false), windowTooSmall: true));
        Assert.True(e.Finished);   // nothing unsaved, so Q quits at once
    }
}
