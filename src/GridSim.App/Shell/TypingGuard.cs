using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using GridSim.Ui.Commands;

namespace GridSim.App.Shell;

/// <summary>
/// The WPF window's typing guard for its single-key shortcuts (UI programme Phase 1.5). The map keys (F, M, C, B, G,
/// D, R, A, T), the step keys ('.' and ',') and Escape are window-wide, but a text box takes its characters as text
/// input and leaves the key-down unhandled, so without a guard typing "Peterhead" into a search box would trip the
/// largest infeed on the 't'. WPF ignores those keys while the keyboard focus is in (or inside) a control that consumes
/// typed characters; this is the same rule for Avalonia.
/// </summary>
public static class TypingGuard
{
    /// <summary>
    /// True when a shortcut needs the guard: a gesture with no Ctrl and no Alt (a plain or Shift key) that is not a
    /// function key. Ctrl and Alt chords and F1 to F24 never type a character, so they work from a text box too (as
    /// F5 and F6 do).
    /// </summary>
    public static bool NeedsGuard(string gesture)
    {
        (bool ctrl, _, bool alt, string key) = CommandGesture.Parse(gesture);
        if (ctrl || alt) return false;
        bool functionKey = key.Length > 1 && key[0] == 'F' && char.IsDigit(key[1]);
        return !functionKey;
    }

    /// <summary>
    /// True when <paramref name="focused"/> is, or sits inside, a control that consumes typed characters: a text box
    /// (which covers a password box, an editable combo box's text part, a numeric up-down and an auto-complete box, all
    /// built on one), or a combo box or one of its items (letters drive its type-ahead selection). It walks up the
    /// visual tree and falls back to the logical tree for an element outside it (a pop-up's content), so a key from a
    /// template part is attributed to the control that owns it, as the WPF IsTextEntryTarget does.
    /// </summary>
    public static bool IsTextEntryTarget(object? focused)
    {
        for (object? d = focused; d is not null; d = Parent(d))
            if (d is TextBox or ComboBox or ComboBoxItem or AutoCompleteBox or NumericUpDown)
                return true;
        return false;
    }

    private static object? Parent(object d) => d switch
    {
        Visual v when v.GetVisualParent() is { } vp => vp,
        ILogical l => l.GetLogicalParent(),
        _ => null,
    };
}
