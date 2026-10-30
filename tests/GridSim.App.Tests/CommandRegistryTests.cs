using GridSim.Ui.Commands;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.1: the command registry's rules. First the invariants over everything the shell registers
/// (unique ids, no shortcut clash in any mode, every menu path resolves to a menu, no Author command on mobile, a
/// title on every command); then each refusal Register makes, so a rule cannot be dropped without a test failing.
/// These run without Avalonia: the registry is platform neutral.
/// </summary>
public sealed class CommandRegistryTests
{
    private static readonly CommandModes[] SingleModes = { CommandModes.Author, CommandModes.Run };
    private static readonly CommandPlatforms[] SinglePlatforms = { CommandPlatforms.Desktop, CommandPlatforms.Mobile };

    /// <summary>The registry exactly as the shell builds it.</summary>
    private static CommandRegistry ShellRegistry()
    {
        var r = new CommandRegistry();
        ShellCommands.Register(r, new RecordingShellActions());
        return r;
    }

    private static AppCommand Cmd(string id, string title, string? menu = null, string? gesture = null,
        CommandModes modes = CommandModes.Both, CommandPlatforms platforms = CommandPlatforms.Desktop,
        CommandTargets targets = CommandTargets.None)
        => new()
        {
            Id = id, Title = title, MenuPath = menu, Gesture = gesture, Modes = modes, Platforms = platforms,
            Targets = targets, Execute = () => Task.CompletedTask,
        };

    // ---------------------------------------------------------------- invariants over the shell's registrations

    [Fact]
    public void The_shell_registers_its_commands_in_menu_order()
    {
        CommandRegistry r = ShellRegistry();
        Assert.Equal(
            new[] { ShellCommands.OpenCase, ShellCommands.OpenRunFolder, ShellCommands.Exit,
                    ShellCommands.Licence, ShellCommands.ThirdPartyNotices, ShellCommands.About },
            r.Commands.Select(c => c.Id));
    }

    [Fact]
    public void Every_command_id_is_unique()
    {
        CommandRegistry r = ShellRegistry();
        Assert.Equal(r.Commands.Count, r.Commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        // Case-insensitively too, so two ids cannot differ only by case.
        Assert.Equal(r.Commands.Count, r.Commands.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (AppCommand c in r.Commands) Assert.Same(c, r.Get(c.Id));
    }

    [Fact]
    public void No_two_commands_share_a_shortcut_in_any_mode_on_any_platform()
    {
        CommandRegistry r = ShellRegistry();
        foreach (CommandModes mode in SingleModes)
            foreach (CommandPlatforms platform in SinglePlatforms)
            {
                List<AppCommand> withGesture = r.Commands.Where(c => c.Gesture is not null && c.IsOfferedIn(mode, platform)).ToList();
                Assert.Equal(withGesture.Count, withGesture.Select(c => c.Gesture).Distinct(StringComparer.Ordinal).Count());
                // And the map the window binds holds every one of them.
                Assert.Equal(withGesture.Count, r.Gestures(mode, platform).Count);
            }
        // The shell's shortcuts, as documented.
        IReadOnlyDictionary<string, AppCommand> run = r.Gestures(CommandModes.Run, CommandPlatforms.Desktop);
        Assert.Equal(ShellCommands.OpenCase, run["Ctrl+O"].Id);
        Assert.Equal(ShellCommands.OpenRunFolder, run["Ctrl+Shift+O"].Id);
        Assert.Equal(ShellCommands.Exit, run["Ctrl+Q"].Id);
        Assert.Equal(3, run.Count);
    }

    [Fact]
    public void Every_menu_path_resolves_to_a_menu_and_an_item_with_the_command_title()
    {
        CommandRegistry r = ShellRegistry();
        foreach (CommandModes mode in SingleModes)
            foreach (CommandPlatforms platform in SinglePlatforms)
            {
                IReadOnlyList<MenuNode> tree = r.MenuTree(mode, platform);
                foreach (AppCommand c in r.Commands.Where(c => c.MenuPath is not null && c.IsOfferedIn(mode, platform)))
                {
                    string[] segments = c.MenuPath!.Split('/');
                    Assert.True(segments.Length >= 2, $"{c} menu path {c.MenuPath} has no menu");
                    IReadOnlyList<MenuNode> level = tree;
                    for (int i = 0; i < segments.Length - 1; i++)
                    {
                        MenuNode? menu = level.SingleOrDefault(n => n.IsMenu && n.Title == segments[i]);
                        Assert.True(menu is not null, $"{c}: menu {segments[i]} of {c.MenuPath} is not on the {mode} {platform} menu bar");
                        level = menu!.Children;
                    }
                    MenuNode item = Assert.Single(level, n => !n.IsMenu && ReferenceEquals(n.Command, c));
                    Assert.Equal(c.Title, item.Title);
                }
            }
    }

    [Fact]
    public void No_Author_mode_command_is_registered_for_mobile()
    {
        CommandRegistry r = ShellRegistry();
        Assert.DoesNotContain(r.Commands, c => (c.Platforms & CommandPlatforms.Mobile) != 0 && (c.Modes & CommandModes.Author) != 0);
        // And the mobile Author menu is empty, so nothing could slip through by another route.
        Assert.Empty(r.MenuTree(CommandModes.Author, CommandPlatforms.Mobile));
        Assert.Empty(r.Gestures(CommandModes.Author, CommandPlatforms.Mobile));
    }

    [Fact]
    public void Every_registered_command_has_a_non_empty_title()
    {
        foreach (AppCommand c in ShellRegistry().Commands)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title), $"{c.Id} has no title");
            Assert.Equal(c.Title.Trim(), c.Title);
        }
    }

    [Fact]
    public void The_shell_menu_bar_is_File_then_Help_with_no_empty_View_menu()
    {
        IReadOnlyList<MenuNode> tree = ShellRegistry().MenuTree(CommandModes.Run, CommandPlatforms.Desktop);
        Assert.Equal(new[] { "File", "Help" }, tree.Select(n => n.Title));
        Assert.Equal(new[] { "Open case...", "Open run folder...", "Exit" }, tree[0].Children.Select(n => n.Title));
        Assert.Equal(new[] { "Licence", "Third-party notices", "About GridSim" }, tree[1].Children.Select(n => n.Title));
        // Author mode on the desktop offers the same shell commands.
        Assert.Equal(new[] { "File", "Help" }, ShellRegistry().MenuTree(CommandModes.Author, CommandPlatforms.Desktop).Select(n => n.Title));
    }

    [Fact]
    public async Task Every_shell_command_reaches_its_own_action()
    {
        var actions = new RecordingShellActions();
        var r = new CommandRegistry();
        ShellCommands.Register(r, actions);
        foreach (AppCommand c in r.Commands) await c.RunAsync();
        Assert.Equal(new[] { "OpenCase", "OpenRunFolder", "Exit", "Licence", "Notices", "About" }, actions.Calls);
    }

    [Fact]
    public void Registering_the_shell_twice_is_refused()
    {
        CommandRegistry r = ShellRegistry();
        ArgumentException ex = Assert.Throws<ArgumentException>(() => ShellCommands.Register(r, new RecordingShellActions()));
        Assert.Contains(ShellCommands.OpenCase, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- each refusal

    [Theory]
    [InlineData("")]
    [InlineData("open")]               // one part only
    [InlineData("File.open")]          // upper-case start
    [InlineData("file.open case")]     // space
    [InlineData("file..open")]         // empty part
    [InlineData("file.open-case")]     // punctuation
    public void A_malformed_id_is_refused(string id)
    {
        var r = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => r.Register(Cmd(id, "Open", "File/Open")));
        Assert.Empty(r.Commands);
    }

    [Fact]
    public void A_duplicate_id_is_refused_and_the_first_stays()
    {
        var r = new CommandRegistry();
        AppCommand first = r.Register(Cmd("file.open", "Open", "File/Open"));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open", "Other", "File/Other")));
        Assert.Contains("file.open", ex.Message, StringComparison.Ordinal);
        Assert.Same(first, Assert.Single(r.Commands));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Open")]
    [InlineData("Open ")]
    public void A_blank_or_padded_title_is_refused(string title)
    {
        var r = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open", title, gesture: "Ctrl+O")));
    }

    [Fact]
    public void No_mode_or_no_platform_is_refused()
    {
        var r = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A", "File/A", modes: CommandModes.None)));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A", "File/A", platforms: CommandPlatforms.None)));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A", "File/A", modes: (CommandModes)4)));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A", "File/A", platforms: (CommandPlatforms)4)));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A", targets: (CommandTargets)128)));
        Assert.Empty(r.Commands);
    }

    [Theory]
    [InlineData(CommandModes.Author, CommandPlatforms.Mobile)]
    [InlineData(CommandModes.Both, CommandPlatforms.Mobile)]
    [InlineData(CommandModes.Author, CommandPlatforms.All)]
    [InlineData(CommandModes.Both, CommandPlatforms.All)]
    public void An_Author_command_on_mobile_is_refused(CommandModes modes, CommandPlatforms platforms)
    {
        var r = new CommandRegistry();
        ArgumentException ex = Assert.Throws<ArgumentException>(() => r.Register(Cmd("edit.bus", "Edit bus", "Edit/Edit bus", modes: modes, platforms: platforms)));
        Assert.Contains("mobile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Run_only_command_on_mobile_is_accepted()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("run.trip", "Trip", "Run/Trip", modes: CommandModes.Run, platforms: CommandPlatforms.All));
        Assert.Single(r.MenuTree(CommandModes.Run, CommandPlatforms.Mobile));
        Assert.Empty(r.MenuTree(CommandModes.Author, CommandPlatforms.Desktop));
    }

    [Fact]
    public void An_unreachable_command_is_refused()
    {
        var r = new CommandRegistry();
        ArgumentException ex = Assert.Throws<ArgumentException>(() => r.Register(Cmd("a.b", "A")));
        Assert.Contains("cannot be reached", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Open")]               // no menu above the item
    [InlineData("File/")]              // empty item
    [InlineData("/Open")]              // empty menu
    [InlineData("File//Open")]         // empty middle
    [InlineData("File /Open")]         // padded segment
    [InlineData("File/Something else")] // does not end in the title
    public void A_malformed_menu_path_is_refused(string path)
    {
        var r = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open", "Open", path)));
    }

    [Fact]
    public void A_menu_place_already_taken_in_a_shared_mode_is_refused()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("file.open", "Open", "File/Open"));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open2", "Open", "File/Open")));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open3", "Open", "file/open")));   // case-insensitive
        // An item cannot also be a menu, either way round.
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.openSub", "Recent", "File/Open/Recent")));
        r.Register(Cmd("view.coast", "Coast", "View/Map/Coast"));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("view.map", "Map", "View/Map")));
    }

    [Fact]
    public void Commands_of_different_modes_may_share_a_menu_place_and_a_shortcut()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("edit.delete", "Delete", "Edit/Delete", "Delete", modes: CommandModes.Author));
        r.Register(Cmd("run.delete", "Delete", "Edit/Delete", "Delete", modes: CommandModes.Run));
        Assert.Equal("edit.delete", r.Gestures(CommandModes.Author, CommandPlatforms.Desktop)["Delete"].Id);
        Assert.Equal("run.delete", r.Gestures(CommandModes.Run, CommandPlatforms.Desktop)["Delete"].Id);
        Assert.Equal("run.delete", r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop)[0].Children.Single().Command!.Id);
    }

    [Fact]
    public void A_shortcut_clash_in_a_shared_mode_is_refused_whatever_the_spelling()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("file.open", "Open", "File/Open", "Ctrl+Shift+O"));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.other", "Other", "File/Other", "shift+ctrl+o", modes: CommandModes.Run)));
        Assert.Contains("file.open", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+O", ex.Message, StringComparison.Ordinal);
        // A different platform does not clash: a mobile Run command can reuse a desktop shortcut.
        r.Register(Cmd("run.open", "Open", "File/Open", "Ctrl+Shift+O", modes: CommandModes.Run, platforms: CommandPlatforms.Mobile));
    }

    [Fact]
    public void Menus_and_items_keep_registration_order()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("help.b", "B", "Help/B"));
        r.Register(Cmd("file.a", "A", "File/A"));
        r.Register(Cmd("help.a", "A", "Help/A"));
        r.Register(Cmd("file.sub", "S", "File/Sub/S"));
        r.Register(Cmd("file.c", "C", "File/C"));
        IReadOnlyList<MenuNode> tree = r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop);
        Assert.Equal(new[] { "Help", "File" }, tree.Select(n => n.Title));
        Assert.Equal(new[] { "B", "A" }, tree[0].Children.Select(n => n.Title));
        Assert.Equal(new[] { "A", "Sub", "C" }, tree[1].Children.Select(n => n.Title));
        Assert.True(tree[1].Children[1].IsMenu);
        Assert.Equal("S", tree[1].Children[1].Children.Single().Title);
    }

    [Fact]
    public void Context_menus_hold_the_commands_for_one_target_kind_and_mode_in_order()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("bus.inspect", "Inspect", targets: CommandTargets.Bus | CommandTargets.Generator));
        r.Register(Cmd("bus.edit", "Edit", modes: CommandModes.Author, targets: CommandTargets.Bus));
        r.Register(Cmd("branch.trip", "Trip", modes: CommandModes.Run, targets: CommandTargets.Branch | CommandTargets.Bus));
        Assert.Equal(new[] { "bus.inspect", "bus.edit" }, r.ForTarget(CommandTargets.Bus, CommandModes.Author, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(new[] { "bus.inspect", "branch.trip" }, r.ForTarget(CommandTargets.Bus, CommandModes.Run, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Equal(new[] { "branch.trip" }, r.ForTarget(CommandTargets.Branch, CommandModes.Run, CommandPlatforms.Desktop).Select(c => c.Id));
        Assert.Empty(r.ForTarget(CommandTargets.Demand, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Empty(r.ForTarget(CommandTargets.Bus, CommandModes.Run, CommandPlatforms.Mobile));
        // A context menu is for exactly one kind, one mode, one platform.
        Assert.Throws<ArgumentException>(() => r.ForTarget(CommandTargets.None, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Throws<ArgumentException>(() => r.ForTarget(CommandTargets.Bus | CommandTargets.Branch, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Throws<ArgumentException>(() => r.ForTarget(CommandTargets.Bus, CommandModes.Both, CommandPlatforms.Desktop));
        Assert.Throws<ArgumentException>(() => r.ForTarget(CommandTargets.Bus, CommandModes.Run, CommandPlatforms.All));
    }

    [Fact]
    public void Lookup_by_id_finds_registered_commands_and_fails_loud_otherwise()
    {
        CommandRegistry r = ShellRegistry();
        Assert.Equal("Open case...", r.Get(ShellCommands.OpenCase).Title);
        Assert.Null(r.Find("no.suchCommand"));
        Assert.Throws<KeyNotFoundException>(() => r.Get("no.suchCommand"));
    }

    [Fact]
    public async Task A_disabled_command_refuses_to_run_and_tells_its_menus_when_that_changes()
    {
        bool enabled = false;
        int runs = 0;
        int changes = 0;
        var c = new AppCommand
        {
            Id = "a.b", Title = "A", MenuPath = "File/A", CanExecute = () => enabled,
            Execute = () => { runs++; return Task.CompletedTask; },
        };
        c.CanExecuteChanged += (_, _) => changes++;
        Assert.False(c.IsEnabled);
        // Refused before the action starts (synchronously), not as a faulted task that could go unobserved.
        Assert.Throws<InvalidOperationException>(() => { _ = c.RunAsync(); });
        Assert.Equal(0, runs);
        enabled = true;
        c.RaiseCanExecuteChanged();
        Assert.Equal(1, changes);
        await c.RunAsync();
        Assert.Equal(1, runs);
    }

    // ---------------------------------------------------------------- gesture grammar

    [Theory]
    [InlineData("Ctrl+O", "Ctrl+O")]
    [InlineData("ctrl+o", "Ctrl+O")]
    [InlineData("Shift+Ctrl+O", "Ctrl+Shift+O")]
    [InlineData("alt+shift+ctrl+f5", "Ctrl+Shift+Alt+F5")]
    [InlineData("F1", "F1")]
    [InlineData("f24", "F24")]
    [InlineData("Delete", "Delete")]
    [InlineData("ctrl+period", "Ctrl+Period")]
    [InlineData(" Ctrl + 1 ", "Ctrl+1")]
    public void Gestures_are_normalised(string text, string expected)
    {
        Assert.Equal(expected, CommandGesture.Normalise(text));
        Assert.Equal(expected, new AppCommand { Id = "a.b", Title = "A", Gesture = text, Execute = () => Task.CompletedTask }.Gesture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++O")]
    [InlineData("Ctrl")]           // a modifier alone is not a key
    [InlineData("Win+O")]          // not a neutral modifier
    [InlineData("Ctrl+Ctrl+O")]    // doubled modifier
    [InlineData("O+Ctrl")]         // key not last
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F01")]
    [InlineData("Ctrl+OemPlus")]   // framework key names are not neutral
    [InlineData("Ctrl+?")]
    public void Malformed_gestures_are_refused_when_the_command_is_built(string text)
    {
        Assert.Throws<FormatException>(() => CommandGesture.Normalise(text));
        Assert.Throws<FormatException>(() => new AppCommand { Id = "a.b", Title = "A", Gesture = text, Execute = () => Task.CompletedTask });
    }

    [Fact]
    public void A_parsed_gesture_gives_its_modifiers_and_key()
    {
        Assert.Equal((true, true, false, "O"), CommandGesture.Parse("Ctrl+Shift+O"));
        Assert.Equal((false, false, true, "F4"), CommandGesture.Parse("Alt+F4"));
        Assert.Equal((false, false, false, "Escape"), CommandGesture.Parse("escape"));
    }

    // ---------------------------------------------------------------- UI programme integration (2026-10-20), added

    [Fact]
    public void A_menu_ordered_last_stays_after_menus_registered_later_and_equal_orders_keep_registration_order()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("file.a", "A", "File/A"));
        r.Register(new AppCommand { Id = "help.a", Title = "A", MenuPath = "Help/A", MenuOrder = MenuOrders.Last, Execute = () => Task.CompletedTask });
        r.Register(Cmd("run.a", "A", "Run/A"));
        r.Register(new AppCommand { Id = "help.b", Title = "B", MenuPath = "Help/B", MenuOrder = MenuOrders.Last, Execute = () => Task.CompletedTask });
        r.Register(Cmd("view.a", "A", "View/Sub/A"));
        IReadOnlyList<MenuNode> tree = r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop);
        Assert.Equal(new[] { "File", "Run", "View", "Help" }, tree.Select(n => n.Title));
        Assert.Equal(new[] { "A", "B" }, tree[3].Children.Select(n => n.Title));   // items keep registration order
        // The shell's own Help commands carry the Last order.
        Assert.All(ShellRegistry().Commands.Where(c => c.MenuPath!.StartsWith("Help/", StringComparison.Ordinal)),
            c => Assert.Equal(MenuOrders.Last, c.MenuOrder));
        Assert.All(ShellRegistry().Commands.Where(c => c.MenuPath!.StartsWith("File/", StringComparison.Ordinal)),
            c => Assert.Equal(MenuOrders.Default, c.MenuOrder));
    }

    [Fact]
    public void Commands_that_disagree_on_their_menus_order_are_refused_naming_both()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("help.a", "A", "Help/A"));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => r.Register(new AppCommand
        {
            Id = "help.b", Title = "B", MenuPath = "Help/B", MenuOrder = MenuOrders.Last, Execute = () => Task.CompletedTask,
        }));
        Assert.Contains("help.b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("help.a", ex.Message, StringComparison.Ordinal);
        // Even in a mode the first one is not offered in: one menu has one place on the bar.
        ArgumentException ex2 = Assert.Throws<ArgumentException>(() => r.Register(new AppCommand
        {
            Id = "help.c", Title = "C", MenuPath = "Help/C", MenuOrder = 5, Modes = CommandModes.Author, Execute = () => Task.CompletedTask,
        }));
        Assert.Contains("help.a", ex2.Message, StringComparison.Ordinal);
        Assert.Single(r.Commands);
    }

    [Fact]
    public void A_toggle_reports_its_tick_and_tells_its_listeners_and_an_ordinary_command_has_none()
    {
        bool on = false;
        var toggle = new AppCommand { Id = "view.x", Title = "X", MenuPath = "View/X", IsChecked = () => on, Execute = () => Task.CompletedTask };
        Assert.True(toggle.IsToggle);
        Assert.False(toggle.Checked);
        int told = 0;
        toggle.CheckedChanged += (_, _) => told++;
        on = true;
        Assert.True(toggle.Checked);   // read live, not cached
        toggle.RaiseCheckedChanged();
        Assert.Equal(1, told);
        AppCommand plain = Cmd("view.y", "Y", "View/Y");
        Assert.False(plain.IsToggle);
        Assert.False(plain.Checked);
    }

    [Fact]
    public void The_front_ends_File_commands_sit_after_Open_run_folder_and_any_other_menu_is_refused()
    {
        var r = new CommandRegistry();
        ShellCommands.Register(r, new RecordingShellActions(), new[] { Cmd("file.extra", "Extra...", "File/Extra...") });
        IReadOnlyList<MenuNode> tree = r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop);
        Assert.Equal(new[] { "Open case...", "Open run folder...", "Extra...", "Exit" }, tree[0].Children.Select(n => n.Title));
        Assert.Equal(new[] { "File", "Help" }, tree.Select(n => n.Title));
        var r2 = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => ShellCommands.Register(r2, new RecordingShellActions(), new[] { Cmd("view.extra", "Extra", "View/Extra") }));
    }
}
