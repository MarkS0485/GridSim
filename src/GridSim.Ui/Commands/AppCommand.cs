namespace GridSim.Ui.Commands;

/// <summary>
/// One thing the user can do in the app (UI programme, command system). Every menu item, keyboard shortcut and
/// context-menu entry is generated from these, so a command is declared once and every front end (the desktop menu
/// bar, the macOS native menu, the mobile command sheet) offers the same thing under the same name.
/// <para>
/// A command carries no user-interface framework type: the gesture is plain text (<see cref="CommandGesture"/>), the
/// action is a delegate. <see cref="CommandRegistry.Register"/> checks every field and refuses a command that is
/// malformed or that clashes with one already registered, with the reason.
/// </para>
/// </summary>
public sealed class AppCommand
{
    /// <summary>
    /// The stable identity, for example <c>"file.openCase"</c>. Unique in a registry; tests and settings refer to
    /// commands by it, so it never changes once shipped, even if the title does.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The text people see, for example <c>"Open case..."</c> (a trailing "..." means it opens a dialog).</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Where the command sits on the menu bar, as menu names joined by <c>/</c> and ending in the item's own text, for
    /// example <c>"File/Open case..."</c> or <c>"View/Overlays/Coast"</c>. Null when the command is not on the menu
    /// bar (a context-menu-only or shortcut-only command). Menus appear by <see cref="MenuOrder"/>, then in the order
    /// their first command was registered, and items within a menu in registration order.
    /// </summary>
    public string? MenuPath { get; init; }

    /// <summary>
    /// UI programme integration (2026-10-20): the place of this command's top-level menu on the menu bar. Menus are
    /// sorted by this number, smallest first, and menus with the same number keep the order their first command was
    /// registered (so with every command left at the default, 0, the bar is in registration order exactly as before).
    /// It exists so a menu that by convention sits last (Help, <see cref="MenuOrders.Last"/>) stays last even though
    /// its commands are registered at start-up, before the Run and View menus that join with the first loaded case.
    /// Every command on the same top-level menu must give the same number; <see cref="CommandRegistry.Register"/>
    /// refuses one that disagrees, naming both, so the order is never decided by whichever command happened to come
    /// first.
    /// </summary>
    public int MenuOrder { get; init; } = MenuOrders.Default;

    /// <summary>
    /// UI programme integration (2026-10-20): for a command that switches something on and off (a map layer, the
    /// fleet, a colour mode), whether it is on now; the menu item then shows a tick while this returns true. Null for
    /// an ordinary command (no tick is ever shown). Call <see cref="RaiseCheckedChanged"/> when the answer may have
    /// changed, so every menu item showing it is refreshed.
    /// </summary>
    public Func<bool>? IsChecked { get; init; }

    /// <summary>True for a command that shows a checked state (<see cref="IsChecked"/> is set).</summary>
    public bool IsToggle => IsChecked is not null;

    /// <summary>The checked state now: <see cref="IsChecked"/>'s answer, or false for an ordinary command.</summary>
    public bool Checked => IsChecked?.Invoke() ?? false;

    /// <summary>Raised when <see cref="IsChecked"/> may give a different answer, so menus refresh their tick.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>Tell every menu item showing this command's tick to ask <see cref="Checked"/> again.</summary>
    public void RaiseCheckedChanged() => CheckedChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The keyboard shortcut as neutral text (for example <c>"Ctrl+O"</c>; see <see cref="CommandGesture"/> for the
    /// grammar), or null for none. Stored normalised (so "shift+ctrl+o" reads back as "Ctrl+Shift+O"); text outside the
    /// grammar throws <see cref="FormatException"/> here, when the command is built, so a typo cannot ship as a
    /// shortcut that never fires.
    /// </summary>
    public string? Gesture
    {
        get => _gesture;
        init => _gesture = value is null ? null : CommandGesture.Normalise(value);
    }

    private readonly string? _gesture;

    /// <summary>The modes the command is offered in. Must not be <see cref="CommandModes.None"/>.</summary>
    public CommandModes Modes { get; init; } = CommandModes.Both;

    /// <summary>
    /// The heads the command is offered on. Must not be <see cref="CommandPlatforms.None"/>, and a command offered on
    /// <see cref="CommandPlatforms.Mobile"/> must be Run mode only (mobile has no Author mode).
    /// </summary>
    public CommandPlatforms Platforms { get; init; } = CommandPlatforms.Desktop;

    /// <summary>The context-menu targets the command acts on (<see cref="CommandTargets.None"/>: no context menu).</summary>
    public CommandTargets Targets { get; init; } = CommandTargets.None;

    /// <summary>
    /// Whether the command can run now (a menu item is shown disabled, and its shortcut does nothing, while this is
    /// false). Null means always. Call <see cref="RaiseCanExecuteChanged"/> when the answer may have changed.
    /// </summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>
    /// What the command does. Asynchronous because many commands open a dialog and wait for it; a command with nothing
    /// to wait for returns <see cref="Task.CompletedTask"/>.
    /// </summary>
    public required Func<Task> Execute { get; init; }

    /// <summary>Raised when <see cref="CanExecute"/> may give a different answer, so menus refresh their state.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>True when the command can run now (<see cref="CanExecute"/> is null or returns true).</summary>
    public bool IsEnabled => CanExecute?.Invoke() ?? true;

    /// <summary>Tell every menu item and shortcut bound to this command to ask <see cref="IsEnabled"/> again.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Run the command. Refuses (throws) when it cannot run now, rather than doing nothing without a word: a front end
    /// that offers a disabled command has a bug that should show, not hide.
    /// </summary>
    public Task RunAsync()
    {
        if (!IsEnabled)
            throw new InvalidOperationException($"Command {Id} (\"{Title}\") cannot run now; its menu item and shortcut should be disabled");
        return Execute();
    }

    /// <summary>True when the command is offered in <paramref name="mode"/> on <paramref name="platform"/>.</summary>
    public bool IsOfferedIn(CommandModes mode, CommandPlatforms platform)
        => (Modes & mode) != 0 && (Platforms & platform) != 0;

    /// <inheritdoc />
    public override string ToString() => $"{Id} (\"{Title}\")";
}

/// <summary>
/// The named values of <see cref="AppCommand.MenuOrder"/> (UI programme integration, 2026-10-20). Plain numbers, so a
/// front end can slot a menu between two of these if it ever needs to; the shell uses only these two.
/// </summary>
public static class MenuOrders
{
    /// <summary>The default: the menu sits in registration order with every other default menu.</summary>
    public const int Default = 0;

    /// <summary>After every default menu: where Help sits on every desktop menu bar.</summary>
    public const int Last = 1000;
}
