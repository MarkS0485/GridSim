using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using GridSim.App.Dialogs;
using GridSim.App.Shell;
using GridSim.Ui.Commands;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.1: the shell window on the headless platform. The menu bar is built from the registry (File and
/// Help, items in order, shortcuts shown), the shortcuts and the menu items run their commands, the About box names
/// the build, the Help viewers open the files shipped beside the app, Open case records the chosen model, and the
/// context-menu hook builds a menu from the registry. Every window here reads and writes only a temporary folder.
/// </summary>
public sealed class ShellWindowTests
{
    private static Menu MenuBarOf(MainWindow w) => w.FindControl<Menu>("MenuBar")!;

    private static List<MenuItem> TopMenus(MainWindow w) => MenuBarOf(w).Items.OfType<MenuItem>().ToList();

    private static MenuItem ItemById(MainWindow w, string id)
        => TopMenus(w).SelectMany(m => m.Items.OfType<MenuItem>()).Single(i => (string?)i.Tag == id);

    private static T SingleOwned<T>(MainWindow w) where T : Window => Assert.Single(w.OwnedWindows.OfType<T>());

    [AvaloniaFact]
    public void The_menu_bar_shows_File_and_Help_with_the_registered_items_in_order_and_their_shortcuts()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            Menu bar = MenuBarOf(w);
            Assert.True(bar.IsVisible);
            List<MenuItem> menus = TopMenus(w);
            // Access keys on the top-level menus (Alt+F, Alt+H); no View menu, because no View command is ported yet.
            Assert.Equal(new[] { "_File", "_Help" }, menus.Select(m => (string?)m.Header));

            // Test retargeted 2026-10-20 (UI programme integration): the window now registers the audit-log viewer in
            // the File menu after Open run folder... (WPF: AUDIT LOG... beside LOAD RUN...). Every earlier assertion is
            // unchanged (each item, its id and its shortcut, in order); strengthened: the new item is checked too, with
            // its id and no shortcut (WPF has none), and the Exit shortcut is checked on the item that is now last.
            // Test retargeted 2026-10-20 (UI shell: v2 + pickers): the window now also registers File > Open case
            // folder... and Open v2 case... (the OS pickers) between Open run folder... and Audit log.... Equal or stronger:
            // the full list is still pinned exactly, item by item with its id and shortcut, in order; the two new items
            // are checked too (their ids and no shortcut), and the Audit log and Exit checks move with their items.
            List<MenuItem> file = menus[0].Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Open case...", "Open run folder...", "Open case folder...", "Open v2 case...", "Audit log...", "Exit" },
                file.Select(i => (string?)i.Header));
            Assert.Equal(new[] { ShellCommands.OpenCase, ShellCommands.OpenRunFolder, CaseFileCommands.OpenCaseFolder, CaseFileCommands.OpenV2Case,
                    GridSim.App.Tabs.AuditLogWindow.CommandId, ShellCommands.Exit },
                file.Select(i => (string?)i.Tag));
            Assert.Equal(new KeyGesture(Key.O, w.CommandModifier), file[0].InputGesture);
            Assert.Equal(new KeyGesture(Key.O, w.CommandModifier | KeyModifiers.Shift), file[1].InputGesture);
            Assert.Null(file[2].InputGesture);
            Assert.Null(file[3].InputGesture);
            Assert.Null(file[4].InputGesture);
            Assert.Equal(new KeyGesture(Key.Q, w.CommandModifier), file[5].InputGesture);

            List<MenuItem> help = menus[1].Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Licence", "Third-party notices", "About GridSim" }, help.Select(i => (string?)i.Header));
            Assert.Equal(new[] { ShellCommands.Licence, ShellCommands.ThirdPartyNotices, ShellCommands.About }, help.Select(i => (string?)i.Tag));
            Assert.All(help, i => Assert.Null(i.InputGesture));

            // The menu bar is exactly the registry's tree: same count, nothing added or dropped by the builder.
            IReadOnlyList<MenuNode> tree = w.ViewModel.Registry.MenuTree(CommandModes.Run, CommandPlatforms.Desktop);
            Assert.Equal(tree.Count, menus.Count);
            for (int m = 0; m < tree.Count; m++)
                Assert.Equal(tree[m].Children.Select(n => n.Command!.Id), menus[m].Items.OfType<MenuItem>().Select(i => (string?)i.Tag));
            // On Windows and Linux the window binds each registry shortcut once; on macOS the native menu would.
            Assert.Equal(3, w.KeyBindings.Count);
            Assert.False(w.UsesNativeMenu);
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(Key.O, RawInputModifiers.None, "OpenCase")]
    [InlineData(Key.O, RawInputModifiers.Shift, "OpenRunFolder")]
    [InlineData(Key.Q, RawInputModifiers.None, "Exit")]
    public void Each_shortcut_fires_its_command(Key key, RawInputModifiers extra, string expected)
    {
        using var temp = new TempFolder();
        RecordingShellActions? actions = null;
        var w = new MainWindow(TestShell.Options(temp, _ => actions = new RecordingShellActions()));
        w.Show();
        try
        {
            RawInputModifiers command = w.CommandModifier == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
            w.KeyPress(key, command | extra, PhysicalKey.None, null);
            w.KeyRelease(key, command | extra, PhysicalKey.None, null);
            TestShell.Pump();
            Assert.Equal(new[] { expected }, actions!.Calls);

            // The plain key without the command modifier runs nothing.
            w.KeyPress(key, extra, PhysicalKey.None, null);
            w.KeyRelease(key, extra, PhysicalKey.None, null);
            TestShell.Pump();
            Assert.Single(actions.Calls);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Clicking_each_menu_item_runs_its_command()
    {
        using var temp = new TempFolder();
        RecordingShellActions? actions = null;
        var w = new MainWindow(TestShell.Options(temp, _ => actions = new RecordingShellActions()));
        w.Show();
        try
        {
            foreach (MenuItem item in TopMenus(w).SelectMany(m => m.Items.OfType<MenuItem>()))
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            Assert.Equal(new[] { "OpenCase", "OpenRunFolder", "Exit", "Licence", "Notices", "About" }, actions!.Calls);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_failing_command_is_reported_in_the_status_bar_not_swallowed()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp, _ => new FailingAboutActions()));
        w.Show();
        try
        {
            Assert.False(w.ViewModel.HasProblem);
            ItemById(w, ShellCommands.About).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            Assert.Equal("About GridSim failed: the About box broke", w.ViewModel.ProblemText);
            TextBlock problem = w.FindControl<TextBlock>("ProblemText")!;
            Assert.True(problem.IsVisible);
            Assert.Equal("About GridSim failed: the About box broke", problem.Text);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_status_bar_says_no_case_is_loaded_and_shows_the_build_line()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            TestShell.Pump();
            Assert.Equal(MainWindowViewModel.NoCaseText, w.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal(GridSim.Core.BuildInfo.DisplayLine, w.FindControl<TextBlock>("VersionFooter")!.Text);
            Assert.False(w.FindControl<TextBlock>("ProblemText")!.IsVisible);
            Assert.Null(w.ViewModel.ChosenCase);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void About_shows_the_build_line_build_runtime_and_licence()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            ItemById(w, ShellCommands.About).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            AboutWindow about = SingleOwned<AboutWindow>(w);
            Assert.True(about.IsVisible);
            Assert.Equal("About GridSim", about.Title);
            string[] lines = about.ShownText.Split(Environment.NewLine);
            Assert.Equal(4, lines.Length);
            // The exact build line the WPF About tab, the CLI version flag and the web footer show.
            Assert.Equal(GridSim.Core.BuildInfo.DisplayLine, lines[0]);
            Assert.Matches(@"^GridSim \d+\.\d+\.\d+ \(commit [^)]+\)$", lines[0]);
            Assert.Equal($"Build:   {GridSim.Core.BuildInfo.InformationalVersion}", lines[1]);
            Assert.StartsWith("Runtime: .NET ", lines[2], StringComparison.Ordinal);
            Assert.Equal("Licence: Test licence", lines[3]);
            Assert.Equal(AboutInfo.Lines("Test licence"), lines);

            about.Close();
            TestShell.Pump();
            Assert.Empty(w.OwnedWindows.OfType<AboutWindow>());
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ShellCommands.ThirdPartyNotices, "THIRD-PARTY-NOTICES.md", "Third-party notices")]
    [InlineData(ShellCommands.Licence, "LICENSE.md", "Licence")]
    public void The_Help_viewers_open_the_file_shipped_beside_the_app(string id, string fileName, string title)
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));   // DocumentFolder null: the app's own folder
        w.Show();
        try
        {
            ItemById(w, id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            DocumentWindow viewer = SingleOwned<DocumentWindow>(w);
            string shipped = Path.Combine(AppContext.BaseDirectory, fileName);
            Assert.True(File.Exists(shipped), $"{fileName} is not shipped beside the app ({shipped})");
            Assert.Equal(title, viewer.Title);
            Assert.Equal(shipped, viewer.Document.Path);
            Assert.True(viewer.Document.IsReadable, viewer.Document.Problem);
            Assert.Equal(File.ReadAllText(shipped), viewer.ShownText);
            // The shipped copy is the repository's file, byte for byte, not a stale or partial copy.
            Assert.Equal(File.ReadAllBytes(Path.Combine(RepoRoot.Path, fileName)), File.ReadAllBytes(shipped));
            Assert.False(viewer.FindControl<TextBlock>("ProblemText")!.IsVisible);
            Assert.Equal(shipped, viewer.FindControl<TextBlock>("PathText")!.Text);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_About_box_buttons_open_the_same_viewers()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            ItemById(w, ShellCommands.About).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            AboutWindow about = SingleOwned<AboutWindow>(w);
            about.FindControl<Button>("NoticesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            about.FindControl<Button>("LicenceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestShell.Pump();
            Assert.Equal(new[] { "THIRD-PARTY-NOTICES.md", "LICENSE.md" },
                w.OwnedWindows.OfType<DocumentWindow>().Select(d => Path.GetFileName(d.Document.Path)));
            about.Close();
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_missing_shipped_file_shows_the_reason_not_an_empty_page()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp, documentFolder: temp.Path));   // a folder with no LICENSE.md
        w.Show();
        try
        {
            ItemById(w, ShellCommands.Licence).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            DocumentWindow viewer = SingleOwned<DocumentWindow>(w);
            Assert.False(viewer.Document.IsReadable);
            TextBlock problem = viewer.FindControl<TextBlock>("ProblemText")!;
            Assert.True(problem.IsVisible);
            Assert.StartsWith("LICENSE.md is missing from ", problem.Text, StringComparison.Ordinal);
            Assert.Equal("", viewer.ShownText);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Open_case_lists_the_catalogue_and_records_the_choice_without_loading_it()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            ItemById(w, ShellCommands.OpenCase).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            OpenCaseWindow picker = SingleOwned<OpenCaseWindow>(w);
            Assert.Equal(new[] { "Case A", "Case B" }, picker.SourceList.Items.OfType<GridSim.Ui.Models.ModelSource>().Select(s => s.Label));
            Assert.Equal(0, picker.SourceList.SelectedIndex);   // nothing used before: the first
            picker.SourceList.SelectedIndex = 1;
            picker.Accept();
            TestShell.Pump();

            Assert.Empty(w.OwnedWindows.OfType<OpenCaseWindow>());
            Assert.Equal("Case B", w.ViewModel.ChosenCase!.Label);
            Assert.StartsWith("Chosen: Case B. Not loaded yet", w.ViewModel.StatusText, StringComparison.Ordinal);
            Assert.Equal(w.ViewModel.StatusText, w.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal("Case B", w.ViewModel.Settings.LastSource);
            Assert.Equal(w.ViewModel.ChosenCase.Key, w.ViewModel.Settings.LastSourcePath);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Cancelling_open_case_changes_nothing()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            ItemById(w, ShellCommands.OpenCase).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            SingleOwned<OpenCaseWindow>(w).FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestShell.Pump();
            Assert.Empty(w.OwnedWindows.OfType<OpenCaseWindow>());
            Assert.Null(w.ViewModel.ChosenCase);
            Assert.False(w.ViewModel.SettingsChanged);
            Assert.Equal(MainWindowViewModel.NoCaseText, w.ViewModel.StatusText);
        }
        finally { w.Close(); }
        Assert.False(File.Exists(temp.File("settings.json")), "an unchanged session must not write the settings file");
    }

    [AvaloniaFact]
    public void The_chosen_case_is_saved_on_exit_and_the_next_session_opens_the_picker_on_it()
    {
        using var temp = new TempFolder();
        var first = new MainWindow(TestShell.Options(temp));
        first.Show();
        first.ViewModel.ChooseCase(TestShell.FakeCases(temp.Path)[1]);
        // File > Exit closes the window, which saves the settings.
        Task exit = first.ViewModel.Registry.Get(ShellCommands.Exit).RunAsync();
        TestShell.Pump();
        Assert.True(exit.IsCompletedSuccessfully);
        TestShell.Pump();
        Assert.False(first.IsVisible);
        Assert.True(File.Exists(temp.File("settings.json")));

        var second = new MainWindow(TestShell.Options(temp));
        second.Show();
        try
        {
            Assert.Equal("Case B", second.ViewModel.Settings.LastSource);
            ItemById(second, ShellCommands.OpenCase).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            OpenCaseWindow picker = SingleOwned<OpenCaseWindow>(second);
            Assert.Equal(1, picker.SourceList.SelectedIndex);   // the last-used case, by its path
            picker.Close();
        }
        finally { second.Close(); }
    }

    [AvaloniaFact]
    public void The_context_menu_hook_builds_a_menu_from_the_registry_for_a_target_kind()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            // No shell command acts on a map target yet, so the window offers no context menu at all.
            foreach (CommandTargets t in Enum.GetValues<CommandTargets>().Where(t => t != CommandTargets.None))
                Assert.Null(w.ContextMenuFor(t));

            // A registry with targets: the helper builds exactly the commands for that kind and mode, in order.
            var r = new CommandRegistry();
            int runs = 0;
            r.Register(new AppCommand
            {
                Id = "bus.inspect", Title = "Inspect bus", Targets = CommandTargets.Bus, Gesture = "Ctrl+I",
                Execute = () => { runs++; return Task.CompletedTask; },
            });
            r.Register(new AppCommand
            {
                Id = "bus.edit", Title = "Edit bus", Targets = CommandTargets.Bus, Modes = CommandModes.Author,
                Execute = () => Task.CompletedTask,
            });
            ContextMenu? run = MenuBuilder.ContextMenuFor(r, CommandTargets.Bus, CommandModes.Run, CommandPlatforms.Desktop,
                KeyModifiers.Control, (_, ex) => throw ex);
            MenuItem only = Assert.Single(Assert.IsType<ContextMenu>(run).Items.OfType<MenuItem>());
            Assert.Equal("Inspect bus", only.Header);
            Assert.Equal("bus.inspect", only.Tag);
            Assert.Equal(new KeyGesture(Key.I, KeyModifiers.Control), only.InputGesture);
            only.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestShell.Pump();
            Assert.Equal(1, runs);

            ContextMenu? author = MenuBuilder.ContextMenuFor(r, CommandTargets.Bus, CommandModes.Author, CommandPlatforms.Desktop,
                KeyModifiers.Control, (_, ex) => throw ex);
            Assert.Equal(new[] { "bus.inspect", "bus.edit" }, author!.Items.OfType<MenuItem>().Select(i => (string?)i.Tag));
            Assert.Null(MenuBuilder.ContextMenuFor(r, CommandTargets.Branch, CommandModes.Run, CommandPlatforms.Desktop,
                KeyModifiers.Control, (_, ex) => throw ex));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_native_menu_builder_mirrors_the_registry_tree()
    {
        // macOS cannot be run here, but the native menu is built by the same code from the same tree: check it.
        var r = new CommandRegistry();
        ShellCommands.Register(r, new RecordingShellActions());
        NativeMenu bar = MenuBuilder.NativeMenuBar(r.MenuTree(CommandModes.Run, CommandPlatforms.Desktop), KeyModifiers.Meta, (_, ex) => throw ex);
        List<NativeMenuItem> menus = bar.Items.OfType<NativeMenuItem>().ToList();
        Assert.Equal(new[] { "File", "Help" }, menus.Select(m => m.Header));
        List<NativeMenuItem> file = menus[0].Menu!.Items.OfType<NativeMenuItem>().ToList();
        Assert.Equal(new[] { "Open case...", "Open run folder...", "Exit" }, file.Select(i => i.Header));
        // On macOS "Ctrl" in a registry gesture is the Command key.
        Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Meta), file[0].Gesture);
        Assert.Equal(new[] { "Licence", "Third-party notices", "About GridSim" },
            menus[1].Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header));
    }

    [AvaloniaFact]
    public void Every_neutral_key_name_has_an_Avalonia_key()
    {
        foreach (string k in GridSim.Ui.Commands.CommandGesture.NamedKeys) GestureMapper.ToKey(k);
        Assert.Equal(Key.A, GestureMapper.ToKey("A"));
        Assert.Equal(Key.Z, GestureMapper.ToKey("Z"));
        Assert.Equal(Key.D0, GestureMapper.ToKey("0"));
        Assert.Equal(Key.D9, GestureMapper.ToKey("9"));
        Assert.Equal(Key.F1, GestureMapper.ToKey("F1"));
        Assert.Equal(Key.F24, GestureMapper.ToKey("F24"));
        Assert.Equal(Key.OemPeriod, GestureMapper.ToKey("Period"));
        Assert.Throws<ArgumentException>(() => GestureMapper.ToKey("Nope"));
        Assert.Equal(new KeyGesture(Key.F5, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt),
            GestureMapper.ToKeyGesture("Alt+Shift+Ctrl+F5", KeyModifiers.Control));
    }

    /// <summary>Actions whose About throws, to check a failure reaches the status bar.</summary>
    private sealed class FailingAboutActions : IShellActions
    {
        public Task OpenCaseAsync() => Task.CompletedTask;
        public Task OpenRunFolderAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task ShowAboutAsync() => throw new InvalidOperationException("the About box broke");
        public Task ShowThirdPartyNoticesAsync() => Task.CompletedTask;
        public Task ShowLicenceAsync() => Task.CompletedTask;
    }
}
