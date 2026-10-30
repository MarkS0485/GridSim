using System.Diagnostics;
using System.Text.Json;
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
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 5b (2026-10-20) in the Avalonia window (headless, Skia): editing a v2 case's ASSET OVERLAY in
/// AUTHOR mode. On a temporary copy of data/uk written as a v2 case with a ten-record overlay in two files, loaded through
/// the run pipeline: the Author tree holds the overlay records by parent; an overlay record's PROPERTIES is the existing
/// field grid, its rows from the kind spec, and a value typed there is one undo step (a value the kind spec refuses is
/// refused with the reason); the context menus are the registry's ("Add overlay record..." on overlay records, the
/// overlay class node and, after their own commands, the model assets it attaches to; the model kinds' own menus as
/// before); the command adds a record attached to a bus through the prompts; CHECK shows the overlay checker's verdict on
/// the record; SAVE writes only the overlay file; RUN reloads the case and carries the edited overlay.
/// </summary>
public sealed class AuthorOverlayViewTests
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

    // ---------------------------------------------------------------- the fixture: data/uk as a v2 case with an overlay

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private const string FileA = "assets/site.assets.json", FileB = "lines.assets.json";

    // As AssetsOverlayViewTests: file A, the substation at North Scotland with its sections and bays; file B, the route of
    // line L2-3 with a span and a conductor. The demand is named as the v2 writer names a bus's demand ("demand-101").
    private const string OverlayA = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "North Scotland" }, "note": "a substation note" },
          { "id": "ov.bb1", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 1 } }, "params": { "label": "Main 400", "ratedCurrentA": 4000 } },
          { "id": "ov.bb2", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 101 } }, "params": {} },
          { "id": "ov.cpl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "to": { "record": "ov.bb2" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "line": { "line": "L1-2 N-S Scotland" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbd", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb2" }, "demand": { "demand": "demand-101" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.ds", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" } }, "params": { "device": "disconnector", "normalOpen": true } }
        ] }
        """;

    private const string OverlayB = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.zebra", "kind": "conductorType", "refs": {}, "params": { "code": "Zebra", "material": "acsr", "crossSectionMm2": 428.9, "diameterMm": 28.62, "dcResistanceOhmPerKm": 0.0674, "ratedCurrentA": 900 } },
          { "id": "ov.route", "kind": "lineRoute", "refs": { "line": { "line": "L2-3 B6 boundary" } }, "params": { "name": "B6 route" } },
          { "id": "ov.span1", "kind": "span", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "lengthM": 350 } }
        ] }
        """;

    private static string WriteCase(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        Directory.CreateDirectory(temp.File("assets"));
        File.WriteAllText(temp.File(FileA.Replace('/', Path.DirectorySeparatorChar)), OverlayA);
        File.WriteAllText(temp.File(FileB), OverlayB);
        using JsonDocument m = JsonDocument.Parse(File.ReadAllBytes(manifest));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (JsonProperty p in m.RootElement.EnumerateObject()) p.WriteTo(w);
            w.WriteStartArray("assets");
            w.WriteStringValue(FileA);
            w.WriteStringValue(FileB);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifest, ms.ToArray());
        return manifest;
    }

    // ---------------------------------------------------------------- helpers (as AuthorModeViewTests)

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

    private static AuthorView Pane(AssetsView v) => v.FindControl<AuthorView>("AuthorPane")!;

    private static ItemsControl Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == name);

    private static void SubTab(MainWindow w, AuthorView pane, string tab)
    {
        pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = tab;
        Render(w);
    }

    private static void Pick(MainWindow w, AuthorView pane, AuthorNode node)
    {
        pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = node;
        Render(w);
    }

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

    // A context menu's entries: each item's header, a separator as "-".
    private static string[] Entries(ContextMenu? menu)
        => menu is null ? Array.Empty<string>() : menu.Items.Cast<object>().Select(i => i is Separator ? "-" : (string)((MenuItem)i).Header!).ToArray();

    private static KeyValuePair<string, string>[] Kv(params (string K, string V)[] x) => x.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToArray();

    // =================================================================================================================

    [AvaloniaFact]
    public void Author_edits_the_overlay_in_the_grid_adds_by_the_context_command_checks_saves_and_RUN_reloads_it()
    {
        using var temp = new TempFolder();
        string manifest = WriteCase(temp);
        string network = Path.Combine(temp.Path, V2CaseWriter.NetworkFileNameFor(manifest));
        string fileA = temp.File(FileA.Replace('/', Path.DirectorySeparatorChar));
        byte[] networkBefore = File.ReadAllBytes(network), manifestBefore = File.ReadAllBytes(manifest), fileBBefore = File.ReadAllBytes(temp.File(FileB));
        var prompts = new ScriptedPrompts();
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        MainWindow w = OpenPlaying(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            AssetNavigatorModel nav = vm.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.NotNull(nav.Index!.Overlay);   // RUN shows the carried overlay

            // ---- AUTHOR: the overlay is editable (no refusal), its records in the tree by parent ----
            Assert.True(Toggle(w, v));
            Assert.True(nav.IsAuthoring);
            AssetAuthorModel author = nav.Author!;
            AuthorView pane = Pane(v);
            Assert.Null(author.Authoring.OverlayRefusal);
            AuthorNode cls = author.ClassNode("overlay")!;
            Assert.Equal(new[] { "ov.sub", "ov.zebra", "ov.route" }, cls.Children.Select(n => n.Label));
            AuthorNode bb1 = cls.Children[0].Children.Single(n => n.Label == "ov.bb1");
            Assert.Equal(new[] { "ov.span1" }, cls.Children[2].Children.Select(n => n.Label));

            // ---- PROPERTIES of an overlay record: the field grid, from the kind spec; Enter is one undo step ----
            Pick(w, pane, bb1);
            Assert.Same(bb1, author.Selected);
            SubTab(w, pane, "PROPERTIES");
            Assert.True(author.Fields!.IsOverlay);
            Assert.Contains("params.ratedCurrentA", Texts(pane));
            Assert.Contains("refs.bus", Texts(pane));
            TypeAndEnter(w, FieldBox(pane, "params.ratedCurrentA"), "4200");
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal("4200", author.Fields!.Row("params.ratedCurrentA").Literal);
            Assert.Equal(1, author.Authoring.UndoDepth);
            TypeAndEnter(w, FieldBox(pane, "params.ratedCurrentA"), "-5");
            Assert.True(author.StatusIsError);
            Assert.Contains("-5 A is below the minimum 0 for 'ratedCurrentA'.", author.Status, StringComparison.Ordinal);
            Assert.Equal(author.Status, pane.FindControl<TextBlock>("AuthorStatus")!.Text);
            Assert.Equal(1, author.Authoring.UndoDepth);

            // ---- context menus: the registry's, by target kind; the model kinds' own as before ----
            Assert.Equal(new[] { "Add overlay record...", "Duplicate", "Delete", "Rename key...", "Where used" }, Entries(pane.MenuFor(bb1)));
            Assert.Equal(new[] { "Add overlay record..." }, Entries(pane.MenuFor(cls)));
            AuthorNode bus101 = author.ClassNode("buses")!.Children.First(n => n.Label.StartsWith("bus 101", StringComparison.Ordinal));
            Assert.Equal(new[] { "Duplicate", "Delete", "Rename key...", "Where used", "-", "Add overlay record..." }, Entries(pane.MenuFor(bus101)));
            Assert.Equal(new[] { "Duplicate", "Delete", "Rename key...", "Where used" }, Entries(w.ContextMenuFor(CommandTargets.Bus)));
            Assert.Equal(new[] { "Add record..." }, Entries(pane.MenuFor(author.ClassNode("buses")!)));

            // ---- Add overlay record... on bus 101: kind, then the id and the fields it needs; the bus role is the bus ----
            Pick(w, pane, bus101);
            prompts.Fields.Enqueue(Kv(("kind", "busbarSection")));
            prompts.Fields.Enqueue(Kv(("id", "ov.bb9"), ("parent", "ov.sub"), ("file", "")));
            Await(vm.Registry.Get(OverlayAuthorCommands.AddOverlayRecord).RunAsync());
            Assert.Equal(new[] { "fields: kind", "fields: id,parent,file" }, prompts.Asked);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal("ov.bb9", author.Selected!.Label);
            Assert.Equal("ov.sub", author.Selected.Parent!.Label);
            Assert.Equal("101", author.Fields!.Row("refs.bus").Literal);
            Assert.Equal(2, author.Authoring.UndoDepth);

            // ---- CHECK shows the overlay checker on the record: a duplicated line makes ov.cbl's line ambiguous ----
            AuthorNode line = author.ClassNode("lines")!.Children.First(n => n.Label == "'L1-2 N-S Scotland'");
            Pick(w, pane, line);
            Await(vm.Registry.Get(AuthorCommands.Duplicate).RunAsync());
            pane.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => author.Authoring.LastCheck is not null, "the check");
            Render(w);
            Assert.False(author.Authoring.LastCheck!.Passed);
            AuthorNode cbl = author.ClassNode("overlay")!.Children[0].Children.Single(n => n.Label == "ov.cbl");
            Pick(w, pane, cbl);
            SubTab(w, pane, "CHECK");
            const string Ambiguous = "record 'ov.cbl': role 'line' points at line 'L1-2 N-S Scotland', which is ambiguous: 2 lines of the case carry that name.";
            Assert.Contains(Ambiguous, author.CheckPage!.Problems.Select(p => p.Message));
            Assert.Contains(Ambiguous, Texts(pane));
            Await(vm.Registry.Get(AuthorCommands.Undo).RunAsync());
            pane.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => author.Authoring.LastCheckRevision == author.Authoring.Revision, "the second check");
            Assert.True(author.Authoring.LastCheck!.Passed, string.Join(Environment.NewLine, author.Authoring.LastCheck.Lines()));

            // ---- SAVE: only the overlay file is written ----
            Await(vm.Registry.Get(AuthorCommands.Save).RunAsync());
            Assert.False(author.Authoring.IsDirty, author.Status);
            Assert.Equal(networkBefore, File.ReadAllBytes(network));
            Assert.Equal(manifestBefore, File.ReadAllBytes(manifest));
            Assert.Equal(fileBBefore, File.ReadAllBytes(temp.File(FileB)));
            string savedA = File.ReadAllText(fileA);
            Assert.Contains("\"ratedCurrentA\": 4200", savedA, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"ov.bb9\"", savedA, StringComparison.Ordinal);

            // ---- RUN: the saved case is reloaded through the run pipeline, carrying the edited overlay ----
            Assert.True(Toggle(w, v));
            Await(w.AuthorMode.PendingReload!);
            Assert.True(nav.IsRunMode);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.Equal(4200, nav.Index!.Overlay!.Find("ov.bb1")!.Record.Params.GetProperty("ratedCurrentA").GetDouble());
            Assert.Equal(101, nav.Index.Overlay.Find("ov.bb9")!.Record.Refs.Single().Bus);
            Assert.Empty(prompts.Asked.Skip(2));   // nothing unsaved when leaving, so nothing asked
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_CHECK_tab_shows_the_substation_topology_warnings_beside_the_last_Checks_problems()
    {
        using var temp = new TempFolder();
        string manifest = WriteCase(temp);
        MainWindow w = OpenPlaying(temp, ModelCatalog.V2CaseSource(manifest), new ScriptedPrompts());
        try
        {
            AssetsView v = ShowAssets(w);
            Assert.True(Toggle(w, v));
            AssetAuthorModel author = w.ViewModel.Tabs.Assets.Author!;
            AuthorView pane = Pane(v);
            pane.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => author.Authoring.LastCheck is not null, "the check");
            Render(w);

            // The coupler ov.cpl joins two sections on different model buses while closed: one coupling warning, shown on
            // the substation's CHECK tab under its own header, with the file and pointer of the substation record.
            AuthorNode sub = author.ClassNode("overlay")!.Children.Single(n => n.Label == "ov.sub");
            Pick(w, pane, sub);
            SubTab(w, pane, "CHECK");
            AuthoringProblem warning = Assert.Single(author.CheckPage!.TopologyWarnings);
            Assert.Equal((sub.Record!.FileName, sub.Record.Pointer), (warning.File, warning.Pointer));
            List<string> shown = Texts(pane);
            Assert.Contains("TOPOLOGY WARNINGS  (THE CASE AS IT IS NOW; DISPLAY ONLY, THEY NEVER STOP A CHECK OR A SAVE)", shown);
            Assert.Contains(warning.Message, shown);
            Assert.True(pane.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AuthorCheckTopology").IsEffectivelyVisible);

            // A record outside any substation: no warnings, and the section is hidden.
            Pick(w, pane, author.ClassNode("overlay")!.Children.Single(n => n.Label == "ov.zebra"));
            SubTab(w, pane, "CHECK");
            Assert.Empty(author.CheckPage!.TopologyWarnings);
            Assert.DoesNotContain(warning.Message, Texts(pane));
        }
        finally { Close(w); }
    }
}
