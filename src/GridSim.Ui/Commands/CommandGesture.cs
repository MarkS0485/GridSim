using System.Text;

namespace GridSim.Ui.Commands;

/// <summary>
/// A keyboard shortcut written as plain, framework-neutral text, for example <c>"Ctrl+O"</c> or <c>"Ctrl+Shift+O"</c>.
/// The registry stores gestures in this form so it never depends on a user-interface framework; each front end turns
/// the text into its own key type.
/// <para>
/// Grammar: zero or more modifiers then exactly one key, joined by <c>+</c>. The modifiers are <c>Ctrl</c>,
/// <c>Shift</c> and <c>Alt</c>. <c>Ctrl</c> means "the platform's command key": Control on Windows and Linux, Command on
/// macOS; the front end makes that swap, so one registration serves every desktop. The key is a letter A-Z, a digit
/// 0-9, F1-F24, or one of the named keys in <see cref="NamedKeys"/>.
/// </para>
/// <para>
/// Every gesture is normalised (modifiers in the fixed order Ctrl, Shift, Alt; letters upper case) so two spellings of
/// the same shortcut ("shift+ctrl+o" and "Ctrl+Shift+O") compare equal and a clash between them is caught. Anything
/// outside the grammar is refused with the reason, at registration, not silently ignored when a key is pressed.
/// </para>
/// </summary>
public static class CommandGesture
{
    /// <summary>The modifiers, in the order a normalised gesture writes them.</summary>
    public static IReadOnlyList<string> Modifiers { get; } = new[] { "Ctrl", "Shift", "Alt" };

    /// <summary>The non-letter, non-digit, non-function keys a gesture may name.</summary>
    public static IReadOnlyList<string> NamedKeys { get; } = new[]
    {
        "Enter", "Escape", "Space", "Tab", "Back", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
        "Up", "Down", "Left", "Right", "Plus", "Minus", "Comma", "Period",
    };

    /// <summary>
    /// The normalised form of <paramref name="gesture"/>. Throws <see cref="FormatException"/> naming the problem when
    /// the text is not a gesture in the grammar above.
    /// </summary>
    public static string Normalise(string gesture)
    {
        if (!TryNormalise(gesture, out string normalised, out string? problem))
            throw new FormatException($"\"{gesture}\" is not a valid gesture: {problem}");
        return normalised;
    }

    /// <summary>
    /// Try to normalise <paramref name="gesture"/>. On failure <paramref name="problem"/> says why, in plain words.
    /// </summary>
    public static bool TryNormalise(string? gesture, out string normalised, out string? problem)
    {
        normalised = "";
        problem = null;
        if (string.IsNullOrWhiteSpace(gesture)) { problem = "it is empty"; return false; }

        string[] parts = gesture.Split('+');
        // A trailing "+" ("Ctrl+") or a doubled one ("Ctrl++O") leaves an empty part: there is no key to press.
        if (parts.Any(p => p.Trim().Length == 0)) { problem = "it has an empty part (a stray '+')"; return false; }

        var mods = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string m = parts[i].Trim();
            string? known = Modifiers.FirstOrDefault(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                problem = $"\"{m}\" is not a modifier (use {string.Join(", ", Modifiers)}; the key goes last)";
                return false;
            }
            if (!mods.Add(known)) { problem = $"the modifier {known} is given twice"; return false; }
        }

        string? key = NormaliseKey(parts[^1].Trim());
        if (key is null)
        {
            problem = $"\"{parts[^1].Trim()}\" is not a key (use A-Z, 0-9, F1-F24 or one of {string.Join(", ", NamedKeys)})";
            return false;
        }

        var sb = new StringBuilder();
        foreach (string m in Modifiers)
            if (mods.Contains(m)) sb.Append(m).Append('+');
        sb.Append(key);
        normalised = sb.ToString();
        return true;
    }

    /// <summary>
    /// Split a normalised gesture into its modifiers and its key, for a front end to map onto its own key type.
    /// </summary>
    public static (bool Ctrl, bool Shift, bool Alt, string Key) Parse(string gesture)
    {
        string n = Normalise(gesture);
        string[] parts = n.Split('+');
        bool Has(string m) => parts.Take(parts.Length - 1).Contains(m, StringComparer.Ordinal);
        return (Has("Ctrl"), Has("Shift"), Has("Alt"), parts[^1]);
    }

    // The canonical spelling of a key, or null when it is not one the grammar allows.
    private static string? NormaliseKey(string k)
    {
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c.ToString() : null;
        }
        if ((k[0] == 'F' || k[0] == 'f') && int.TryParse(k.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24
            && k[1] != '0')   // "F01" is not a key name
            return "F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return NamedKeys.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
    }
}
