namespace GridSim.Ui.Commands;

/// <summary>
/// One entry of a generated menu: either a menu (a <see cref="Title"/> with <see cref="Children"/> and no command) or an
/// item (a <see cref="Title"/> with a <see cref="Command"/> and no children). Built by
/// <see cref="CommandRegistry.MenuTree"/> from the commands' menu paths; a front end turns the tree into its own menu
/// controls without deciding anything itself.
/// </summary>
public sealed class MenuNode
{
    private readonly List<MenuNode> _children = new();

    internal MenuNode(string title, AppCommand? command)
    {
        Title = title;
        Command = command;
    }

    /// <summary>The text shown: the menu's name, or the item's last menu-path segment.</summary>
    public string Title { get; }

    /// <summary>The command an item runs; null for a menu.</summary>
    public AppCommand? Command { get; }

    /// <summary>A menu's entries in registration order; empty for an item.</summary>
    public IReadOnlyList<MenuNode> Children => _children;

    /// <summary>True for a menu (it holds entries), false for an item (it runs a command).</summary>
    public bool IsMenu => Command is null;

    internal void Add(MenuNode child) => _children.Add(child);

    /// <inheritdoc />
    public override string ToString() => IsMenu ? $"{Title}/ ({_children.Count})" : $"{Title} -> {Command!.Id}";
}
