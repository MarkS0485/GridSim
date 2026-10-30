using Avalonia.Controls;
using Avalonia.Input;
using GridSim.Ui.Commands;

namespace GridSim.App.Shell;

/// <summary>
/// Builds the Avalonia menus from the command registry (UI programme Phase 1.1): the in-window menu bar (Windows,
/// Linux), the macOS native menu bar, and context menus. Nothing here decides what is on a menu; the order, the
/// titles and the shortcuts all come from <see cref="CommandRegistry"/>, so every menu shows the same commands.
/// </summary>
public static class MenuBuilder
{
    /// <summary>
    /// The header text for a top-level menu, with an access key on its first letter (Alt+F opens File on Windows and
    /// Linux). Any underscore in the title itself is doubled so it shows as a plain underscore.
    /// </summary>
    public static string AccessKeyHeader(string title) => "_" + Escape(title);

    /// <summary>The header text for an item: the title, with any underscore doubled so it is not an access key.</summary>
    public static string ItemHeader(string title) => Escape(title);

    /// <summary>
    /// The menu bar's top-level items for <paramref name="tree"/> (from <see cref="CommandRegistry.MenuTree"/>). Each
    /// item runs its registry command through a <see cref="RegistryCommand"/> and shows its shortcut, mapped with
    /// <paramref name="commandModifier"/>. The shortcut is shown only (InputGesture): the window binds the keys itself,
    /// once, from the registry, so a key never fires twice.
    /// </summary>
    public static List<MenuItem> MenuBarItems(IReadOnlyList<MenuNode> tree, KeyModifiers commandModifier,
        Action<AppCommand, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Select(n => ToMenuItem(n, topLevel: true, commandModifier, onFailure)).ToList();
    }

    /// <summary>
    /// The macOS native menu bar for <paramref name="tree"/>. On macOS the gestures are given to the native items,
    /// which the system menu handles, so the window binds no keys of its own there.
    /// </summary>
    public static NativeMenu NativeMenuBar(IReadOnlyList<MenuNode> tree, KeyModifiers commandModifier,
        Action<AppCommand, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var menu = new NativeMenu();
        foreach (MenuNode n in tree) menu.Items.Add(ToNativeItem(n, commandModifier, onFailure));
        return menu;
    }

    /// <summary>
    /// The context menu for one kind of target in <paramref name="mode"/> on <paramref name="platform"/>, or null when
    /// no command acts on that kind (the caller then shows no menu rather than an empty one). The hook every map and
    /// table uses when it gains right-click targets.
    /// </summary>
    public static ContextMenu? ContextMenuFor(CommandRegistry registry, CommandTargets target, CommandModes mode,
        CommandPlatforms platform, KeyModifiers commandModifier, Action<AppCommand, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IReadOnlyList<AppCommand> commands = registry.ForTarget(target, mode, platform);
        if (commands.Count == 0) return null;
        var menu = new ContextMenu();
        foreach (AppCommand c in commands)
            menu.Items.Add(CommandItem(c, c.Title, commandModifier, onFailure));
        return menu;
    }

    private static MenuItem ToMenuItem(MenuNode node, bool topLevel, KeyModifiers commandModifier,
        Action<AppCommand, Exception> onFailure)
    {
        if (!node.IsMenu) return CommandItem(node.Command!, node.Title, commandModifier, onFailure);
        var menu = new MenuItem { Header = topLevel ? AccessKeyHeader(node.Title) : ItemHeader(node.Title) };
        foreach (MenuNode child in node.Children)
            menu.Items.Add(ToMenuItem(child, topLevel: false, commandModifier, onFailure));
        // A menu that opens reads its toggles' ticks again (a safety net under CheckedChanged: a state changed by
        // something other than the command itself, such as the settings applied at start-up, still shows right).
        menu.SubmenuOpened += (_, _) => RefreshTicks(menu);
        return menu;
    }

    private static MenuItem CommandItem(AppCommand command, string title, KeyModifiers commandModifier,
        Action<AppCommand, Exception> onFailure)
    {
        var item = new MenuItem
        {
            Header = ItemHeader(title),
            Command = new RegistryCommand(command, onFailure),
            // The registry id travels with the item, so a test (or a future UI automation) can find it by id.
            Tag = command.Id,
        };
        if (command.Gesture is not null) item.InputGesture = GestureMapper.ToKeyGesture(command.Gesture, commandModifier);
        if (command.IsToggle) ShowTick(item, command);
        return item;
    }

    /// <summary>
    /// UI programme integration (2026-10-20): a toggle command's item shows a tick while the command says it is on
    /// (<see cref="AppCommand.IsChecked"/>). The tick is read when the item is built, again each time the command
    /// raises <see cref="AppCommand.CheckedChanged"/> (after it ran, from the menu or from its key), and again whenever
    /// its menu opens, so it always shows the state now. The item listens only while it is in a menu, so a menu bar
    /// that was rebuilt leaves no stale listeners behind.
    /// </summary>
    private static void ShowTick(MenuItem item, AppCommand command)
    {
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = command.Checked;
        void Refresh(object? sender, EventArgs e) => item.IsChecked = command.Checked;
        item.AttachedToLogicalTree += (_, _) =>
        {
            command.CheckedChanged -= Refresh;   // never twice
            command.CheckedChanged += Refresh;
            item.IsChecked = command.Checked;
        };
        item.DetachedFromLogicalTree += (_, _) => command.CheckedChanged -= Refresh;
        // A click on a CheckBox item flips its tick by itself before the command runs; the refresh after the command
        // (CheckedChanged) puts back what the command decided, so a refused or failed toggle never shows a false tick.
    }

    // Read every tick of a menu's direct items again (the menu is opening).
    private static void RefreshTicks(MenuItem menu)
    {
        foreach (MenuItem child in menu.Items.OfType<MenuItem>())
            if (child.Command is RegistryCommand { Command: { IsToggle: true } c })
                child.IsChecked = c.Checked;
    }

    private static NativeMenuItem ToNativeItem(MenuNode node, KeyModifiers commandModifier,
        Action<AppCommand, Exception> onFailure)
    {
        if (node.IsMenu)
        {
            var sub = new NativeMenu();
            foreach (MenuNode child in node.Children) sub.Items.Add(ToNativeItem(child, commandModifier, onFailure));
            return new NativeMenuItem(node.Title) { Menu = sub };
        }
        AppCommand c = node.Command!;
        var item = new NativeMenuItem(node.Title) { Command = new RegistryCommand(c, onFailure) };
        if (c.Gesture is not null) item.Gesture = GestureMapper.ToKeyGesture(c.Gesture, commandModifier);
        if (c.IsToggle)
        {
            // The macOS menu shows the same tick (UI programme integration, 2026-10-20), refreshed the same way. A
            // native item has no logical tree to leave, so it listens for as long as the command lives (the native
            // menu is built twice per session at most, so at most one stale listener per toggle).
            item.ToggleType = NativeMenuItemToggleType.CheckBox;
            item.IsChecked = c.Checked;
            c.CheckedChanged += (_, _) => item.IsChecked = c.Checked;
        }
        return item;
    }

    private static string Escape(string title) => title.Replace("_", "__", StringComparison.Ordinal);
}
