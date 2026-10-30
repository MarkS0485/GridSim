using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Assets;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 4.1 in the Avalonia window (headless, Skia): AUTHOR mode inside the ASSETS tab. On a temporary v2
/// case loaded through the run pipeline: the RUN badge is a toggle; AUTHOR pauses the run, hides every live value of the
/// tab and switches the menus and shortcuts to Author mode; an edit typed into the field grid is one undo step; undo and
/// redo through their registry commands and gestures; CHECK fills the CHECK sub-tab; SAVE writes only the edited token;
/// RUN reloads the saved case through the run pipeline. Leaving with unsaved changes asks Save / Discard / Cancel. A
/// bundled case's Save is Save As into the user's own folder, the bundled files unchanged. MATPOWER and a replay have no
/// AUTHOR, with the reason shown (plan Q9). The context menus and the gestures are the registry's, in Author mode only.
/// </summary>
public sealed class AuthorModeViewTests
{
    // ---------------------------------------------------------------- scripted answers to Author mode's questions

    private sealed class ScriptedPrompts : IAuthorPrompts
    {
        public Queue<DirtyChoice> Dirty { get; } = new();
        public Queue<string?> Texts { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public Queue<IReadOnlyList<KeyValuePair<string, string>>?> Fields { get; } = new();
        public List<string> Asked { get; } = new();

        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message)
        {
            Asked.Add("dirty: " + message);
            return Task.FromResult(Dirty.Dequeue());
        }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Asked.Add("confirm: " + message);
            return Task.FromResult(Confirms.Dequeue());
        }

        public Task<string?> AskTextAsync(string title, string message, string initial)
        {
            Asked.Add("text: " + title + " [" + initial + "]");
            return Task.FromResult(Texts.Dequeue());
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
        {
            Asked.Add("fields: " + string.Join(",", fields.Select(f => f.Field)));
            return Task.FromResult(Fields.Dequeue());
        }
    }

    // ---------------------------------------------------------------- fixtures

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // data/uk written as a JSON v2 case (the convert verb's writer) into temp: a case of the user's own, not bundled.
    private static string WriteV2Case(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        return manifest;
    }

    // A two-frame replay of case9 (as ReplayClarityTests writes one).
    private static string TwoFrameRun(TempFolder temp)
    {
        string dir = Path.Combine(temp.Path, "gda-replay-test");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int idx in new[] { 0, 3 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", $"state-{idx:D6}.json"), model, r, trace, meta);
        }
        return dir;
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source, ScriptedPrompts prompts) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        AuthorPrompts = _ => prompts,
        AuthorUserCasesFolder = temp.File("user-cases"),
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        return task.GetAwaiter().GetResult();
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
    }

    // Open the window, load the source through the run pipeline and let it play two frames (left playing).
    private static MainWindow OpenPlaying(TempFolder temp, ModelSource source, ScriptedPrompts prompts)
    {
        var w = new MainWindow(Options(temp, source, prompts));
        w.Show();
        RunViewModel run = w.ViewModel.Run;
        int frames = 0;
        run.FrameShown += (_, _) => frames++;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => frames >= 2, "two frames");
        Assert.False(run.IsPaused);
        return w;
    }

    private static void Render(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static AssetsView ShowAssets(MainWindow w)
    {
        w.SelectTab(MainWindow.AssetsTabHeader);
        Render(w);
        Assert.True(w.AssetsView.IsEffectivelyVisible);
        return w.AssetsView;
    }

    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    private static bool Toggle(MainWindow w, AssetsView v)
    {
        v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool done = Await(v.PendingModeSwitch!);
        Render(w);
        return done;
    }

    private static List<string?> Menus(MainWindow w) =>
        w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();

    private static AuthorView Pane(AssetsView v) => v.FindControl<AuthorView>("AuthorPane")!;

    private static ItemsControl Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == name);

    private static void SubTab(MainWindow w, AuthorView pane, string tab)
    {
        pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = tab;
        Render(w);
    }

    // The field grid row control of <field>: its text box, after the grid is rendered.
    private static TextBox FieldBox(AuthorView pane, string field)
    {
        ItemsControl rows = Named(pane, "FieldRows");
        FieldGridModel grid = Assert.IsType<FieldGridModel>(pane.Model.Fields);
        int index = grid.Rows.ToList().FindIndex(r => r.Field == field);
        Assert.True(index >= 0, field + " is not a row of the grid");
        rows.ScrollIntoView(index);
        Dispatcher.UIThread.RunJobs();
        return rows.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FieldBox");
    }

    private static void TypeAndEnter(MainWindow w, TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Render(w);
    }

    // =================================================================================================================

    [AvaloniaFact]
    public void Author_edit_in_the_field_grid_undo_redo_check_save_then_RUN_reloads_the_saved_v2_case()
    {
        using var temp = new TempFolder();
        string manifest = WriteV2Case(temp);
        string network = Path.Combine(temp.Path, V2CaseWriter.NetworkFileNameFor(manifest));
        Dictionary<string, byte[]> before = Directory.EnumerateFiles(temp.Path, "*.json").Where(f => !f.EndsWith("settings.json", StringComparison.Ordinal))
            .ToDictionary(f => f, File.ReadAllBytes, StringComparer.Ordinal);
        var prompts = new ScriptedPrompts();
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        MainWindow w = OpenPlaying(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            AssetNavigatorModel nav = vm.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.Equal(AssetSourceKind.V2Case, nav.Source.Kind);
            Assert.Equal("RUN", v.FindControl<TextBlock>("ModeBadge")!.Text);
            Assert.Equal("switch to AUTHOR", v.FindControl<TextBlock>("ModeSwitchText")!.Text);
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, Menus(w));
            string timeShown = v.FindControl<TextBlock>("TimeText")!.Text!;
            Assert.Contains(timeShown, Texts(v));

            // ---- AUTHOR: the run pauses, the live pages and the clock go, the menus are Author mode's ----
            Assert.True(Toggle(w, v));
            Assert.True(nav.IsAuthoring);
            Assert.True(vm.Run.IsPaused);
            Assert.Equal(CommandModes.Author, vm.Mode);
            // Test retargeted 2026-10-20 (Phase 4.2): Author mode's menu bar gains the Map menu (map editing on the NETWORK
            // map: Select tool, Place bus, Connect, Delete), between Case and Help. Equal strength: still the whole bar, in
            // order; the Run-mode bar above and below is unchanged.
            Assert.Equal(new[] { "_File", "_Edit", "_Case", "_Map", "_Help" }, Menus(w));
            Assert.False(w.FindControl<Border>("TransportBar")!.IsEnabled);
            Assert.Equal("AUTHOR", v.FindControl<TextBlock>("ModeBadge")!.Text);
            Assert.Equal("switch to RUN", v.FindControl<TextBlock>("ModeSwitchText")!.Text);
            Assert.False(v.FindControl<Grid>("RunBody")!.IsEffectivelyVisible);
            Assert.False(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);
            Assert.False(v.FindControl<TextBlock>("TimeText")!.IsEffectivelyVisible);
            Assert.False(v.FindControl<Button>("ShowOnMapButton")!.IsEnabled);
            Assert.DoesNotContain(timeShown, Texts(v));   // no live value anywhere on the tab (plan Q5)
            AuthorView pane = Pane(v);
            Assert.True(pane.IsEffectivelyVisible);
            AssetAuthorModel author = Assert.IsType<AssetAuthorModel>(nav.Author);
            Assert.Same(author, pane.DataContext);
            Assert.Contains(author.Summary!.CaseName, Texts(pane));
            SimFrame? frozen = vm.Run.LastFrame;
            PumpFor(300);
            Assert.Same(frozen, vm.Run.LastFrame);   // nothing runs while authoring

            // ---- a line record: PROPERTIES is the field grid; typing a value and Enter is one undo step ----
            AuthorNode line0 = author.ClassNode("lines")!.Children[0];
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = line0;
            Render(w);
            Assert.Same(line0, author.Selected);
            SubTab(w, pane, "PROPERTIES");
            Assert.True(author.IsPropertiesShown);
            // Test retargeted 2026-10-20 (Phase 4.3): a line's PROPERTIES now opens as its per-element editor (the same
            // field grid rows, arranged in sections); the generic field grid this test types into is one click away, on
            // the view's EDITOR / GRID switch. Equal or stronger: the editor is asserted shown first, the switch is
            // clicked through the view, the grid is then the visible one, and every assertion below is unchanged.
            Assert.True(author.IsEditorShown);
            Assert.True(pane.FindControl<ContentControl>("EditorHost")!.IsEffectivelyVisible);
            pane.FindControl<Button>("EditorViewToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.True(author.IsFieldGridShown);
            Assert.True(pane.FindControl<ContentControl>("FieldsHost")!.IsEffectivelyVisible);
            Assert.Contains(author.Fields!.Heading, Texts(pane));
            string old = author.Fields.Row("rPu").Literal;
            TypeAndEnter(w, FieldBox(pane, "rPu"), "0.025");
            Assert.True(author.Authoring.IsDirty, author.Status);
            Assert.Equal(1, author.Authoring.UndoDepth);
            Assert.Equal("0.025", author.Fields!.Row("rPu").Literal);
            Assert.Equal("AUTHOR *", v.FindControl<TextBlock>("ModeBadge")!.Text);
            // A bad literal is refused with the reason in the status line, and changes nothing.
            TypeAndEnter(w, FieldBox(pane, "circuits"), "two");
            Assert.True(author.StatusIsError);
            Assert.Equal(author.Status, pane.FindControl<TextBlock>("AuthorStatus")!.Text);
            Assert.Equal(1, author.Authoring.UndoDepth);

            // ---- undo and redo: the registry commands and their gestures ----
            Await(vm.Registry.Get(AuthorCommands.Undo).RunAsync());
            Assert.False(author.Authoring.IsDirty);
            Assert.Equal(old, author.Fields!.Row("rPu").Literal);
            Await(vm.Registry.Get(AuthorCommands.Redo).RunAsync());
            Assert.True(author.Authoring.IsDirty);
            KeyBinding undoKey = w.KeyBindings.Single(k => k.Gesture.Key == Key.Z && k.Gesture.KeyModifiers == w.CommandModifier);
            undoKey.Command!.Execute(null);
            PumpFor(50);
            Assert.False(author.Authoring.IsDirty);
            KeyBinding redoKey = w.KeyBindings.Single(k => k.Gesture.Key == Key.Y && k.Gesture.KeyModifiers == w.CommandModifier);
            redoKey.Command!.Execute(null);
            PumpFor(50);
            Assert.True(author.Authoring.IsDirty);

            // ---- CHECK: the button runs the gate's check; the CHECK sub-tab shows its result ----
            pane.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => author.Authoring.LastCheck is not null, "the check");
            Render(w);
            Assert.True(author.Authoring.LastCheck!.Passed, string.Join(Environment.NewLine, author.Authoring.LastCheck.Lines()));
            Assert.Contains("CHECK", author.SubTabs);
            SubTab(w, pane, "CHECK");
            Assert.Contains(author.CheckPage!.Heading, Texts(pane));

            // ---- SAVE (Ctrl+S's command): only the edited token changes on disk ----
            Await(vm.Registry.Get(AuthorCommands.Save).RunAsync());
            Assert.False(author.Authoring.IsDirty, author.Status);
            Assert.Equal("AUTHOR", v.FindControl<TextBlock>("ModeBadge")!.Text);
            foreach ((string f, byte[] b) in before.Where(kv => kv.Key != network)) Assert.Equal(b, File.ReadAllBytes(f));
            string savedText = File.ReadAllText(network);
            Assert.Equal(System.Text.Encoding.UTF8.GetString(before[network]).Length - old.Length + "0.025".Length, savedText.Length);

            // ---- RUN: the saved case is reloaded through the run pipeline ----
            Assert.True(Toggle(w, v));
            Assert.Empty(prompts.Asked);   // nothing unsaved, so nothing asked
            Assert.NotNull(w.AuthorMode.PendingReload);
            Await(w.AuthorMode.PendingReload!);
            Assert.True(nav.IsRunMode);
            Assert.Equal(CommandModes.Run, vm.Mode);
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, Menus(w));
            Assert.True(w.FindControl<Border>("TransportBar")!.IsEnabled);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.True(source.SameSourceAs(vm.Run.CurrentSource!));
            int frames = 0;
            vm.Run.FrameShown += (_, _) => frames++;
            PumpUntil(() => frames >= 1, "a frame of the reloaded case");
            Render(w);
            Assert.True(v.FindControl<Grid>("RunBody")!.IsEffectivelyVisible);
            Assert.True(v.FindControl<TextBlock>("TimeText")!.IsEffectivelyVisible);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Leaving_AUTHOR_with_unsaved_changes_asks_Save_Discard_Cancel()
    {
        using var temp = new TempFolder();
        string manifest = WriteV2Case(temp);
        string network = Path.Combine(temp.Path, V2CaseWriter.NetworkFileNameFor(manifest));
        byte[] original = File.ReadAllBytes(network);
        var prompts = new ScriptedPrompts();
        MainWindow w = OpenPlaying(temp, ModelCatalog.V2CaseSource(manifest), prompts);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.True(Toggle(w, v));
            AssetAuthorModel author = nav.Author!;
            author.Select(author.ClassNode("lines")!.Children[0]);
            Assert.True(author.EditField("rPu", "0.025").Ok);

            // Cancel: stays in AUTHOR with the change kept.
            prompts.Dirty.Enqueue(DirtyChoice.Cancel);
            Assert.False(Toggle(w, v));
            Assert.True(nav.IsAuthoring);
            Assert.True(author.IsDirty);
            Assert.StartsWith("dirty: Unsaved changes in " + Path.GetFileName(network), prompts.Asked.Single(), StringComparison.Ordinal);

            // Discard: back to RUN, nothing written, nothing reloaded; the run resumes as it was (playing).
            prompts.Dirty.Enqueue(DirtyChoice.Discard);
            Assert.True(Toggle(w, v));
            Assert.True(nav.IsRunMode);
            Assert.Null(w.AuthorMode.PendingReload);
            Assert.Equal(original, File.ReadAllBytes(network));
            Assert.False(w.ViewModel.Run.IsPaused);

            // Save: the change is written and the case reloaded.
            Assert.True(Toggle(w, v));
            author = nav.Author!;
            author.Select(author.ClassNode("lines")!.Children[0]);
            Assert.True(author.EditField("rPu", "0.025").Ok);
            prompts.Dirty.Enqueue(DirtyChoice.Save);
            Assert.True(Toggle(w, v));
            Assert.True(nav.IsRunMode);
            Await(w.AuthorMode.PendingReload!);
            Assert.NotEqual(original, File.ReadAllBytes(network));
            Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);

            // A load while authoring asks first; Cancel keeps the session and loads nothing.
            Assert.True(Toggle(w, v));
            author = nav.Author!;
            author.Select(author.ClassNode("lines")!.Children[0]);
            Assert.True(author.EditField("xPu", "0.3").Ok);
            ModelSource current = w.ViewModel.Run.CurrentSource!;
            prompts.Dirty.Enqueue(DirtyChoice.Cancel);
            string case9 = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
            Await(w.ViewModel.LoadCaseAsync(new ModelSource("case9", Path.GetDirectoryName(case9)!, case9)));
            Assert.True(nav.IsAuthoring);
            Assert.Same(current, w.ViewModel.Run.CurrentSource);
            prompts.Dirty.Enqueue(DirtyChoice.Discard);
            Assert.True(Toggle(w, v));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_bundled_cases_Save_is_Save_As_into_the_users_folder_and_the_bundled_files_stay_as_shipped()
    {
        using var temp = new TempFolder();
        string uk = Path.Combine(RepoRoot(), "data", "uk");
        Dictionary<string, string> hashes = Directory.EnumerateFiles(uk).ToDictionary(p => p, BundledNetworks.ContentHash, StringComparer.Ordinal);
        Dictionary<string, byte[]> shipped = Directory.EnumerateFiles(uk).ToDictionary(p => p, File.ReadAllBytes, StringComparer.Ordinal);
        var prompts = new ScriptedPrompts();
        MainWindow w = OpenPlaying(temp, new ModelSource("uk", uk), prompts);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.True(Toggle(w, v));
            AssetAuthorModel author = nav.Author!;
            Assert.True(author.Authoring.IsBundled);
            Assert.Contains(author.Summary!.BundledNote, Texts(Pane(v)));
            author.Select(author.ClassNode("lines")!.Children[0]);
            Assert.True(author.EditField("rPu", "0.025").Ok);

            // Save (Ctrl+S's command) on a bundled case is Save As: the suggested folder is in the user's own folder.
            string target = Path.Combine(temp.Path, "user-cases", "uk-copy");
            prompts.Texts.Enqueue(target);
            Await(w.ViewModel.Registry.Get(AuthorCommands.Save).RunAsync());
            Assert.Equal("text: Save As [" + target + "]", prompts.Asked.Single());
            Assert.True(File.Exists(Path.Combine(target, "transmission.json")), author.Status);
            Assert.Equal(Path.GetFullPath(target), author.Authoring.RootPath);
            Assert.False(author.Authoring.IsBundled);

            // RUN reloads the user's copy, not the bundled case.
            Assert.True(Toggle(w, v));
            Await(w.AuthorMode.PendingReload!);
            Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(w.ViewModel.Run.CurrentSource!.Dir));
            foreach ((string path, byte[] bytes) in shipped) Assert.Equal(bytes, File.ReadAllBytes(path));
            foreach ((string path, string hash) in hashes) Assert.Equal(hash, BundledNetworks.ContentHash(path));
            Assert.True(BundledNetworks.IsBundledPath(uk));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void MATPOWER_and_a_replay_have_no_AUTHOR_and_the_reason_is_shown()
    {
        using var temp = new TempFolder();
        string case9 = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        var prompts = new ScriptedPrompts();
        foreach (ModelSource source in new[] { new ModelSource("case9", Path.GetDirectoryName(case9)!, case9), ModelCatalog.ReplaySource(TwoFrameRun(temp)) })
        {
            var w = new MainWindow(Options(temp, source, prompts));
            w.Show();
            try
            {
                Await(w.ViewModel.LoadCaseAsync(source));
                Assert.False(w.ViewModel.Run.HasError, w.ViewModel.Run.ErrorText);
                AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
                AssetsView v = ShowAssets(w);
                Assert.False(nav.CanAuthor);
                Assert.Equal("AUTHOR unavailable", v.FindControl<TextBlock>("ModeSwitchText")!.Text);
                Assert.False(Toggle(w, v));
                Assert.True(nav.IsRunMode);
                Assert.Equal(CommandModes.Run, w.ViewModel.Mode);
                string reason = CaseAuthoring.UnavailableReason(nav.Source)!;
                Assert.Contains(source.ReplayDir is null ? "MATPOWER .m file" : "replay of an exported run folder", reason, StringComparison.Ordinal);
                TextBlock note = v.FindControl<TextBlock>("ModeNoteText")!;
                Assert.True(note.IsEffectivelyVisible);
                Assert.Equal(reason, note.Text);
                Assert.Empty(prompts.Asked);
            }
            finally { Close(w); }
        }
    }

    [AvaloniaFact]
    public void The_Author_gestures_and_context_menus_are_the_registrys_in_Author_mode_only()
    {
        using var temp = new TempFolder();
        string manifest = WriteV2Case(temp);
        var prompts = new ScriptedPrompts();
        MainWindow w = OpenPlaying(temp, ModelCatalog.V2CaseSource(manifest), prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            CommandRegistry r = vm.Registry;
            AssetsView v = ShowAssets(w);

            // Run mode: none of the Author gestures is bound and no record kind has a context menu.
            int runKeys = w.KeyBindings.Count;
            Assert.DoesNotContain(w.KeyBindings, k => k.Gesture.KeyModifiers == w.CommandModifier && k.Gesture.Key is Key.Z or Key.Y or Key.S);
            foreach (CommandTargets kind in new[] { CommandTargets.Transformer, CommandTargets.Oltc, CommandTargets.AssetClassNode, CommandTargets.Generator })
                Assert.Null(w.ContextMenuFor(kind));

            Assert.True(Toggle(w, v));
            // Author mode: one command per gesture across every registered command (no clash), each bound once.
            IReadOnlyDictionary<string, AppCommand> keys = r.Gestures(CommandModes.Author, CommandPlatforms.Desktop);
            List<AppCommand> withGesture = r.Commands.Where(c => c.Gesture is not null && c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Desktop)).ToList();
            Assert.Equal(withGesture.Count, keys.Count);
            Assert.Equal(keys.Count, w.KeyBindings.Count);
            Assert.NotEqual(runKeys, w.KeyBindings.Count);
            Assert.Equal(AuthorCommands.Undo, keys["Ctrl+Z"].Id);
            Assert.Equal(AuthorCommands.Redo, keys["Ctrl+Y"].Id);
            Assert.Equal(AuthorCommands.Save, keys["Ctrl+S"].Id);
            Assert.Equal(AuthorCommands.SaveAs, keys["Ctrl+Shift+S"].Id);
            Assert.DoesNotContain(r.Commands, c => c.IsOfferedIn(CommandModes.Author, CommandPlatforms.Mobile));
            MenuItem edit = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Edit");
            Assert.Equal(new[] { "Undo", "Redo" }, edit.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            MenuItem caseMenu = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Case");
            Assert.Equal(new[] { "Check", "Save", "Save As...", "Switch to Run" }, caseMenu.Items.OfType<MenuItem>().Select(i => (string?)i.Header));

            // Context menus by target kind: a bus record has the keyed actions, a generator the plain ones, a class Add.
            AssetAuthorModel author = vm.Tabs.Assets.Author!;
            string[] Items(CommandTargets kind) => w.ContextMenuFor(kind)!.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToArray();
            Assert.Equal(new[] { "Duplicate", "Delete", "Rename key...", "Where used" }, Items(CommandTargets.Bus));
            Assert.Equal(new[] { "Duplicate", "Delete" }, Items(CommandTargets.Generator));
            Assert.Equal(new[] { "Add record..." }, Items(CommandTargets.AssetClassNode));

            // The context commands act on the selected node: Add on the Demand class (asks for its required fields),
            // Duplicate and Delete on the new record, Rename on a bus (asks, confirms, then renames everywhere).
            author.Select(author.ClassNode("demands")!);
            prompts.Fields.Enqueue(new[] { new KeyValuePair<string, string>("name", "test demand"), new KeyValuePair<string, string>("bus", "1"), new KeyValuePair<string, string>("pMw", "10") });
            int demands = author.ClassNode("demands")!.Children.Count;
            Await(r.Get(AuthorCommands.AddRecord).RunAsync());
            Assert.Equal(demands + 1, author.ClassNode("demands")!.Children.Count);
            Assert.Equal(AuthorNodeKind.Record, author.Selected!.Kind);   // the new record is selected
            Await(r.Get(AuthorCommands.Duplicate).RunAsync());
            Assert.Equal(demands + 2, author.ClassNode("demands")!.Children.Count);
            Await(r.Get(AuthorCommands.Delete).RunAsync());
            Assert.Equal(demands + 1, author.ClassNode("demands")!.Children.Count);

            AuthorNode bus = author.ClassNode("buses")!.Children[0];
            author.Select(bus);
            prompts.Texts.Enqueue("9001");
            prompts.Confirms.Enqueue(true);
            int depth = author.Authoring.UndoDepth;
            Await(r.Get(AuthorCommands.Rename).RunAsync());
            Assert.Equal(depth + 1, author.Authoring.UndoDepth);
            Assert.StartsWith("bus 9001", author.ClassNode("buses")!.Children[0].Label, StringComparison.Ordinal);
            Assert.StartsWith("confirm: Rename id", prompts.Asked[^1], StringComparison.Ordinal);
            Await(r.Get(AuthorCommands.WhereUsed).RunAsync());
            Assert.Equal("CONNECTIONS", author.SelectedSubTab);

            // Leave without saving (Discard).
            prompts.Dirty.Enqueue(DirtyChoice.Discard);
            Await(r.Get(AuthorCommands.SwitchToRun).RunAsync());
            Assert.Equal(CommandModes.Run, vm.Mode);
            Assert.Equal(runKeys, w.KeyBindings.Count);
        }
        finally { Close(w); }
    }
}
