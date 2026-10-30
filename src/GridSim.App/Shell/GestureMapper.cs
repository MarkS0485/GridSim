using Avalonia.Input;
using GridSim.Ui.Commands;

namespace GridSim.App.Shell;

/// <summary>
/// Turns the registry's neutral gesture text (<see cref="CommandGesture"/>, for example <c>"Ctrl+O"</c>) into an
/// Avalonia <see cref="KeyGesture"/>. <c>Ctrl</c> becomes the platform's command modifier, which the caller passes in
/// (Control on Windows and Linux, Meta, the Command key, on macOS), so one registration serves every desktop.
/// Every key name the grammar allows has a mapping here; a name without one throws, so the two lists cannot drift.
/// </summary>
public static class GestureMapper
{
    /// <summary>
    /// The Avalonia gesture for <paramref name="gesture"/>, with <c>Ctrl</c> mapped to
    /// <paramref name="commandModifier"/>.
    /// </summary>
    public static KeyGesture ToKeyGesture(string gesture, KeyModifiers commandModifier)
    {
        (bool ctrl, bool shift, bool alt, string key) = CommandGesture.Parse(gesture);
        KeyModifiers mods = KeyModifiers.None;
        if (ctrl) mods |= commandModifier;
        if (shift) mods |= KeyModifiers.Shift;
        if (alt) mods |= KeyModifiers.Alt;
        return new KeyGesture(ToKey(key), mods);
    }

    /// <summary>The Avalonia key for a key name from the neutral grammar.</summary>
    public static Key ToKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') return Key.A + (key[0] - 'A');
        if (key.Length == 1 && key[0] is >= '0' and <= '9') return Key.D0 + (key[0] - '0');
        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
            return Key.F1 + (f - 1);
        return key switch
        {
            "Enter" => Key.Enter,
            "Escape" => Key.Escape,
            "Space" => Key.Space,
            "Tab" => Key.Tab,
            "Back" => Key.Back,
            "Delete" => Key.Delete,
            "Insert" => Key.Insert,
            "Home" => Key.Home,
            "End" => Key.End,
            "PageUp" => Key.PageUp,
            "PageDown" => Key.PageDown,
            "Up" => Key.Up,
            "Down" => Key.Down,
            "Left" => Key.Left,
            "Right" => Key.Right,
            "Plus" => Key.OemPlus,
            "Minus" => Key.OemMinus,
            "Comma" => Key.OemComma,
            "Period" => Key.OemPeriod,
            _ => throw new ArgumentException($"The gesture key \"{key}\" has no Avalonia key mapping", nameof(key)),
        };
    }
}
