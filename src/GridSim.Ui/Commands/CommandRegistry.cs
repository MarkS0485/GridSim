using System.Text.RegularExpressions;

namespace GridSim.Ui.Commands;

/// <summary>
/// The one list of every command the app offers (UI programme, command system). The menu bar, the macOS native menu,
/// the keyboard shortcuts, the context menus and (later) the mobile command sheet are all generated from it, so they
/// cannot disagree.
/// <para>
/// <see cref="Register"/> is the only way in, and it is strict: a command that is malformed, unreachable or that
/// clashes with one already registered (same id, same menu place, same shortcut in a mode both offer) is refused with
/// an <see cref="ArgumentException"/> naming both commands. The rules are checked when the app starts, so a clash
/// shows the first time anyone runs it rather than as a shortcut that fires the wrong thing.
/// </para>
/// </summary>
public sealed partial class CommandRegistry
{
    private readonly List<AppCommand> _commands = new();
    private readonly Dictionary<string, AppCommand> _byId = new(StringComparer.Ordinal);

    /// <summary>Every registered command, in registration order.</summary>
    public IReadOnlyList<AppCommand> Commands => _commands;

    /// <summary>
    /// Add <paramref name="command"/>. Throws <see cref="ArgumentException"/> with the reason when it breaks any rule:
    /// <list type="bullet">
    /// <item><description>the id is not dotted lower camel case (for example <c>file.openCase</c>), or is taken;</description></item>
    /// <item><description>the title is blank or has leading or trailing spaces;</description></item>
    /// <item><description>no mode or no platform is given, or an unknown flag is set;</description></item>
    /// <item><description>it is offered on mobile in Author mode (mobile has Run mode only);</description></item>
    /// <item><description>it is unreachable: no menu path, no shortcut and no context-menu target;</description></item>
    /// <item><description>the menu path has an empty segment, has no menu above the item, does not end in the
    /// title, or puts the item where another item or menu already is;</description></item>
    /// <item><description>its shortcut is already taken by a command offered in a shared mode on a shared platform.</description></item>
    /// </list>
    /// Returns the command, so a caller can register and keep it in one line.
    /// </summary>
    public AppCommand Register(AppCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Execute, nameof(command) + "." + nameof(AppCommand.Execute));

        string id = command.Id ?? "";
        if (!IdPattern().IsMatch(id))
            throw new ArgumentException($"Command id \"{id}\" is not dotted lower camel case (for example file.openCase)", nameof(command));
        if (_byId.TryGetValue(id, out AppCommand? sameId))
            throw new ArgumentException($"Command id {id} is already registered (\"{sameId.Title}\")", nameof(command));

        if (string.IsNullOrWhiteSpace(command.Title))
            throw new ArgumentException($"Command {id} has no title", nameof(command));
        if (command.Title.Trim() != command.Title)
            throw new ArgumentException($"Command {id} has spaces around its title \"{command.Title}\"", nameof(command));

        if (command.Modes == CommandModes.None || (command.Modes & ~CommandModes.Both) != 0)
            throw new ArgumentException($"Command {id} has no valid mode ({command.Modes})", nameof(command));
        if (command.Platforms == CommandPlatforms.None || (command.Platforms & ~CommandPlatforms.All) != 0)
            throw new ArgumentException($"Command {id} has no valid platform ({command.Platforms})", nameof(command));
        if ((command.Platforms & CommandPlatforms.Mobile) != 0 && (command.Modes & CommandModes.Author) != 0)
            throw new ArgumentException(
                $"Command {id} is offered on mobile in Author mode; mobile has Run mode only, so a mobile command must be Run only",
                nameof(command));
        const CommandTargets allTargets = CommandTargets.Bus | CommandTargets.Branch | CommandTargets.Generator
            | CommandTargets.Demand | CommandTargets.Boundary | CommandTargets.Substation | CommandTargets.Map
            // UI programme Phase 4.1: the Author-mode record kinds of the ASSETS tree (additive).
            | CommandTargets.Transformer | CommandTargets.Oltc | CommandTargets.Protection | CommandTargets.AssetClass
            | CommandTargets.AssetBinding | CommandTargets.AssetClassNode
            // UI programme Phase 5b: the asset overlay's targets (additive).
            | CommandTargets.OverlayRecord | CommandTargets.OverlayAnchor
            // UI programme Phase 4.2: a map element in Author mode (additive).
            | CommandTargets.MapElement
            // UI programme Phase 4.4: the template nodes of the ASSETS tree in Author mode (additive).
            | CommandTargets.TemplateClassNode | CommandTargets.Template | CommandTargets.TemplateOverride
            // UI programme Phase 5f follow-up: a transformer3W overlay record's star-legs target (additive).
            | CommandTargets.StarTransformer;
        if ((command.Targets & ~allTargets) != 0)
            throw new ArgumentException($"Command {id} has an unknown context-menu target ({command.Targets})", nameof(command));

        if (command.MenuPath is null && command.Gesture is null && command.Targets == CommandTargets.None)
            throw new ArgumentException(
                $"Command {id} cannot be reached: it has no menu path, no shortcut and no context-menu target", nameof(command));

        if (command.MenuPath is not null) CheckMenuPath(command);
        if (command.Gesture is not null) CheckGesture(command);

        _commands.Add(command);
        _byId.Add(id, command);
        return command;
    }

    /// <summary>The command with id <paramref name="id"/>. Throws <see cref="KeyNotFoundException"/> when there is none.</summary>
    public AppCommand Get(string id)
        => _byId.TryGetValue(id, out AppCommand? c) ? c : throw new KeyNotFoundException($"No command with id {id} is registered");

    /// <summary>The command with id <paramref name="id"/>, or null when there is none.</summary>
    public AppCommand? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The menu bar for <paramref name="mode"/> on <paramref name="platform"/>: the top-level menus, each holding its
    /// items and sub-menus, built from the menu paths of the commands offered there. Menus appear by their
    /// <see cref="AppCommand.MenuOrder"/> and, within one order, in the order their first offered command was
    /// registered; items appear in registration order, so the order is decided in one place (the registration code). A menu with nothing offered in this mode does not appear at all: no empty menus.
    /// </summary>
    public IReadOnlyList<MenuNode> MenuTree(CommandModes mode, CommandPlatforms platform)
    {
        CheckSingle(mode, platform);
        var root = new MenuNode("", null);
        foreach (AppCommand c in _commands)
        {
            if (c.MenuPath is null || !c.IsOfferedIn(mode, platform)) continue;
            string[] segments = c.MenuPath.Split('/');
            MenuNode parent = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                MenuNode? menu = parent.Children.FirstOrDefault(n => n.IsMenu && n.Title == segments[i]);
                if (menu is null) { menu = new MenuNode(segments[i], null); parent.Add(menu); }
                parent = menu;
            }
            parent.Add(new MenuNode(segments[^1], c));
        }
        // UI programme integration (2026-10-20): the top-level menus sorted by their MenuOrder. OrderBy is a stable
        // sort, so menus with the same order keep the order their first command was registered (with every command at
        // the default the bar is exactly the registration order, as before). Register guarantees every command of one
        // top-level menu gives the same order, so the first command found under a menu speaks for it.
        return root.Children.OrderBy(MenuOrderOf).ToList();
    }

    // The MenuOrder of a top-level menu: that of the first command found under it (all of them agree; see Register).
    private static int MenuOrderOf(MenuNode menu)
    {
        foreach (MenuNode child in menu.Children)
            return child.IsMenu ? MenuOrderOf(child) : child.Command!.MenuOrder;
        throw new InvalidOperationException($"Menu {menu.Title} has no entries; the registry never builds an empty menu");
    }

    /// <summary>
    /// The context menu for one kind of target (exactly one <see cref="CommandTargets"/> flag) in
    /// <paramref name="mode"/> on <paramref name="platform"/>: every command that acts on that kind, in registration
    /// order. Empty when none does (the front end then shows no context menu rather than an empty one).
    /// </summary>
    public IReadOnlyList<AppCommand> ForTarget(CommandTargets target, CommandModes mode, CommandPlatforms platform)
    {
        if (target == CommandTargets.None || ((int)target & ((int)target - 1)) != 0)
            throw new ArgumentException($"A context menu is for exactly one kind of target, not {target}", nameof(target));
        CheckSingle(mode, platform);
        return _commands.Where(c => (c.Targets & target) != 0 && c.IsOfferedIn(mode, platform)).ToList();
    }

    /// <summary>
    /// Every shortcut offered in <paramref name="mode"/> on <paramref name="platform"/>, keyed by its normalised
    /// gesture text. Registration refuses clashes, so each gesture maps to exactly one command.
    /// </summary>
    public IReadOnlyDictionary<string, AppCommand> Gestures(CommandModes mode, CommandPlatforms platform)
    {
        CheckSingle(mode, platform);
        var map = new Dictionary<string, AppCommand>(StringComparer.Ordinal);
        foreach (AppCommand c in _commands)
            if (c.Gesture is not null && c.IsOfferedIn(mode, platform))
                map.Add(c.Gesture, c);   // Add, not an indexer: a clash that slipped past Register would throw here
        return map;
    }

    // A menu, a context menu and a shortcut map are built for one mode on one platform at a time.
    private static void CheckSingle(CommandModes mode, CommandPlatforms platform)
    {
        if (mode is not (CommandModes.Author or CommandModes.Run))
            throw new ArgumentException($"Menus are built for one mode (Author or Run), not {mode}", nameof(mode));
        if (platform is not (CommandPlatforms.Desktop or CommandPlatforms.Mobile))
            throw new ArgumentException($"Menus are built for one platform (Desktop or Mobile), not {platform}", nameof(platform));
    }

    private void CheckMenuPath(AppCommand command)
    {
        string path = command.MenuPath!;
        string[] segments = path.Split('/');
        if (segments.Length < 2)
            throw new ArgumentException($"Command {command.Id} menu path \"{path}\" has no menu above the item (for example File/{command.Title})", nameof(command));
        if (segments.Any(s => s.Length == 0 || s.Trim() != s))
            throw new ArgumentException($"Command {command.Id} menu path \"{path}\" has an empty or space-padded segment", nameof(command));
        if (segments[^1] != command.Title)
            throw new ArgumentException(
                $"Command {command.Id} menu path \"{path}\" does not end in its title \"{command.Title}\", so the menu and the context menus would name it differently",
                nameof(command));

        foreach (AppCommand other in _commands)
        {
            if (other.MenuPath is null) continue;
            // UI programme integration (2026-10-20): one top-level menu, one place on the bar. A command that puts its
            // menu somewhere else than a command already on that menu is refused, whatever their modes, so the order
            // never depends on which command is offered first.
            string otherTop = other.MenuPath.Split('/')[0];
            if (string.Equals(otherTop, segments[0], StringComparison.Ordinal) && other.MenuOrder != command.MenuOrder)
                throw new ArgumentException(
                    $"Command {command.Id} gives menu {segments[0]} the order {command.MenuOrder}, but {other} gives it {other.MenuOrder}; every command on one menu must give the same order",
                    nameof(command));
            // Two commands may share a menu place only when no mode and platform offers both (an Author-only and a
            // Run-only command can sit at the same spot). Anywhere else the second would hide the first.
            if ((other.Modes & command.Modes) == 0 || (other.Platforms & command.Platforms) == 0) continue;
            if (string.Equals(other.MenuPath, path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Command {command.Id} menu path \"{path}\" is already used by {other}", nameof(command));
            // An item cannot also be a menu: "View/Map" as an item and "View/Map/Coast" as an item under it.
            if (other.MenuPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(other.MenuPath + "/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Command {command.Id} menu path \"{path}\" and {other} menu path \"{other.MenuPath}\" make one entry both an item and a menu",
                    nameof(command));
        }
    }

    private void CheckGesture(AppCommand command)
    {
        foreach (AppCommand other in _commands)
            if (other.Gesture == command.Gesture
                && (other.Modes & command.Modes) != 0 && (other.Platforms & command.Platforms) != 0)
                throw new ArgumentException(
                    $"Command {command.Id} shortcut {command.Gesture} is already taken by {other} in {other.Modes & command.Modes} mode",
                    nameof(command));
    }

    // Dotted lower camel case: two or more parts, each starting with a lower-case letter, letters and digits only.
    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
