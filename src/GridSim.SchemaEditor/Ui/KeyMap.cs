namespace GridSim.SchemaEditor.Ui;

/// <summary>Every command the editor understands in browse mode.</summary>
internal enum Command
{
    None,
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
    Open,
    Back,
    EditKeep,
    Toggle,
    Picker,
    Reset,
    Add,
    Duplicate,
    Delete,
    Rename,
    Filter,
    FilterNext,
    Find,
    GotoBus,
    Jump,
    WhereUsed,
    Undo,
    Redo,
    Normalise,
    Check,
    Save,
    Quit,
    Help,
}

/// <summary>One key binding: the key (and modifiers), or a character, the command, and how help and the key bar show it.</summary>
internal sealed record KeyBinding(ConsoleKey? Key, char? Char, ConsoleModifiers Modifiers, Command Command, string KeyText, string Help, bool InKeyBar);

/// <summary>
/// The single key-to-command table (risk R2: terminals capture F-keys and Ctrl chords, so every command also has a
/// plain letter). The help screen and the key bar are both rendered from this table, so they can never disagree with
/// what the keys do.
/// </summary>
internal static class KeyMap
{
    /// <summary>The bindings, in the order help lists them.</summary>
    public static IReadOnlyList<KeyBinding> Bindings { get; } = new KeyBinding[]
    {
        new(ConsoleKey.F1, null, 0, Command.Help, "F1", "Help: this list of keys", true),
        new(null, '?', 0, Command.Help, "?", "Help", false),
        new(ConsoleKey.UpArrow, null, 0, Command.Up, "Up", "Move up one row", false),
        new(ConsoleKey.DownArrow, null, 0, Command.Down, "Down", "Move down one row", false),
        new(ConsoleKey.PageUp, null, 0, Command.PageUp, "PgUp", "Move up one page", false),
        new(ConsoleKey.PageDown, null, 0, Command.PageDown, "PgDn", "Move down one page", false),
        new(ConsoleKey.Home, null, 0, Command.Home, "Home", "First row", false),
        new(ConsoleKey.End, null, 0, Command.End, "End", "Last row", false),
        new(ConsoleKey.Enter, null, 0, Command.Open, "Enter", "Open the row, or edit the field (typing replaces the value)", true),
        new(ConsoleKey.RightArrow, null, 0, Command.Open, "Right", "Open the row", false),
        new(ConsoleKey.Escape, null, 0, Command.Back, "Esc", "Up one level (at the top: quit)", true),
        new(ConsoleKey.Backspace, null, 0, Command.Back, "Bksp", "Up one level", false),
        new(ConsoleKey.LeftArrow, null, 0, Command.Back, "Left", "Up one level", false),
        new(ConsoleKey.F2, null, 0, Command.EditKeep, "F2", "Edit the field keeping its current text", true),
        new(null, 'e', 0, Command.EditKeep, "E", "Edit the field keeping its current text", false),
        new(ConsoleKey.Spacebar, null, 0, Command.Toggle, "Space", "Toggle a yes / no field, or step to the next value of a list field", false),
        new(ConsoleKey.F4, null, 0, Command.Picker, "F4", "Pick a value of a list field", false),
        new(null, 'p', 0, Command.Picker, "P", "Pick a value of a list field", false),
        new(ConsoleKey.Delete, null, 0, Command.Reset, "Del", "Reset the field to absent (its default)", false),
        new(ConsoleKey.Insert, null, 0, Command.Add, "Ins", "Add a record", false),
        new(null, 'a', 0, Command.Add, "A", "Add a record", false),
        new(ConsoleKey.F5, null, 0, Command.Duplicate, "F5", "Duplicate the record", false),
        new(null, 'd', 0, Command.Duplicate, "D", "Duplicate the record", false),
        new(ConsoleKey.F8, null, 0, Command.Delete, "F8", "Delete the record (asks first)", false),
        new(null, 'x', 0, Command.Delete, "X", "Delete the record (asks first)", false),
        new(null, 'r', 0, Command.Rename, "R", "Rename: a station, or a key (bus id, branch or class name) with every reference", false),
        new(null, '/', 0, Command.Filter, "/", "Filter the list (text anywhere in the row, any case)", false),
        new(ConsoleKey.F3, null, 0, Command.FilterNext, "F3", "Next row matching the filter", false),
        new(null, 'n', 0, Command.FilterNext, "N", "Next row matching the filter", false),
        new(null, 'f', 0, Command.Find, "F", "Find a name in every section", false),
        new(null, 'g', 0, Command.GotoBus, "G", "Go to a bus by id", false),
        new(null, 'j', 0, Command.Jump, "J", "Jump to the bus a bus field names", false),
        new(null, 'w', 0, Command.WhereUsed, "W", "Where used: everything that refers to this record", false),
        new(null, 'u', 0, Command.Undo, "U", "Undo the last change", true),
        new(ConsoleKey.Z, null, ConsoleModifiers.Control, Command.Undo, "Ctrl+Z", "Undo", false),
        new(null, 'U', 0, Command.Redo, "Shift+U", "Redo", false),
        new(ConsoleKey.Y, null, ConsoleModifiers.Control, Command.Redo, "Ctrl+Y", "Redo", false),
        new(null, 'l', 0, Command.Normalise, "L", "Normalise member-name spelling to the schema's (needed before saving a non-canonical file)", false),
        new(ConsoleKey.F9, null, 0, Command.Check, "F9", "Check the case (no write)", true),
        new(null, 'k', 0, Command.Check, "K", "Check the case (no write)", false),
        new(ConsoleKey.F10, null, 0, Command.Save, "F10", "Save", true),
        new(null, 's', 0, Command.Save, "S", "Save", false),
        new(ConsoleKey.F12, null, 0, Command.Quit, "F12", "Quit (asks about unsaved changes)", true),
        new(null, 'q', 0, Command.Quit, "Q", "Quit (asks about unsaved changes)", false),
        new(ConsoleKey.C, null, ConsoleModifiers.Control, Command.Quit, "Ctrl+C", "Quit (asks about unsaved changes)", false),
    };

    /// <summary>
    /// The command a key press means in browse mode. Ctrl chords match on the key; plain characters match case-
    /// sensitively first (so U is undo and Shift+U redo), then ignoring case. For a LETTER the case is taken from the
    /// Shift key, not from the character: with Caps Lock on, a plain U types 'U', which used to match the Shift+U redo
    /// binding, so the undo key redid a change instead.
    /// </summary>
    public static Command Lookup(ConsoleKeyInfo key)
    {
        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        foreach (KeyBinding b in Bindings)
            if (b.Key is ConsoleKey k && k == key.Key && (b.Modifiers & ConsoleModifiers.Control) == (ctrl ? ConsoleModifiers.Control : 0)
                && (b.Char is null))
                return b.Command;
        if (ctrl || key.KeyChar == '\0') return Command.None;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        char typed = IntendedChar(key.KeyChar, shift);
        foreach (KeyBinding b in Bindings)
            if (b.Char is char c && c == typed) return b.Command;
        // No exact match: a letter typed with Caps Lock or Shift means the same command as its lower-case binding.
        foreach (KeyBinding b in Bindings)
            if (b.Char is char c && char.IsLetter(c) && char.ToLowerInvariant(c) == char.ToLowerInvariant(key.KeyChar)) return b.Command;
        return Command.None;
    }

    /// <summary>
    /// The character a key press means for the binding table: a letter is upper case exactly when Shift is held
    /// (so Caps Lock never changes the command), and any other character is taken as typed.
    /// </summary>
    internal static char IntendedChar(char keyChar, bool shift)
        => char.IsLetter(keyChar) ? (shift ? char.ToUpperInvariant(keyChar) : char.ToLowerInvariant(keyChar)) : keyChar;

    /// <summary>The key bar text for browse mode: the bindings marked for it, "key label" pairs.</summary>
    public static string BrowseBar()
        => string.Join("  ", Bindings.Where(b => b.InKeyBar).Select(b => b.KeyText + " " + ShortLabel(b.Command)));

    /// <summary>A one-word label of a command, for the key bar.</summary>
    public static string ShortLabel(Command c) => c switch
    {
        Command.Help => "Help",
        Command.Open => "Open",
        Command.Back => "Up",
        Command.EditKeep => "Edit",
        Command.Undo => "Undo",
        Command.Check => "Check",
        Command.Save => "Save",
        Command.Quit => "Quit",
        _ => c.ToString(),
    };
}
