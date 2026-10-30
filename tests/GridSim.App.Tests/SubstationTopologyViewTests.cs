using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Assets;
using GridSim.App.Controls.Diagram;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Commands;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// Phase 5c (2026-10-20) in the Avalonia window (headless, Skia): the substation topology view. On a temporary copy of
/// data/uk written as a v2 case whose overlay substation couples bus 1 and bus 101 through a closed breaker (so it
/// carries one implied-coupling WARNING, and the case still loads and runs): in RUN the substation record's OVERVIEW
/// draws the topology diagram (sections live from the solved frame), shows the warning, CHECK lists it, and a real
/// click on a section opens its record; in AUTHOR the substation's record page draws the same topology from the
/// session, and typing normalOpen = true for the coupler in PROPERTIES redraws the diagram (the coupler open) and clears
/// the warning; a real click there selects the record.
/// </summary>
public sealed class SubstationTopologyViewTests
{
    // ---------------------------------------------------------------- scripted answers to Author mode's questions

    private sealed class ScriptedPrompts : IAuthorPrompts
    {
        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message) => Task.FromResult(DirtyChoice.Discard);

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<string?> AskTextAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>?>(null);
    }

    // ---------------------------------------------------------------- the fixture (as AuthorOverlayViewTests)

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private const string FileA = "assets/site.assets.json";

    private const string OverlayA = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "North Scotland" } },
          { "id": "ov.bb1", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 1 } }, "params": { "label": "Main 400" } },
          { "id": "ov.bb2", "kind": "busbarSection", "parent": "ov.sub", "refs": { "bus": { "bus": 101 } }, "params": {} },
          { "id": "ov.cpl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "to": { "record": "ov.bb2" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.cbl", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb1" }, "line": { "line": "L1-2 N-S Scotland" } }, "params": { "device": "breaker", "normalOpen": false } },
          { "id": "ov.es", "kind": "switchingDevice", "parent": "ov.sub", "refs": { "from": { "record": "ov.bb2" } }, "params": { "device": "earthSwitch", "normalOpen": true } }
        ] }
        """;

    private const string Coupling =
        "substation 'ov.sub': busbar sections 'ov.bb1' (bus 1) and 'ov.bb2' (bus 101) collapse to different buses, but under the normal switch "
        + "states the closed device 'ov.cpl' joins them into one node. The switch states imply a coupling the authored case does not model. "
        + "This is a warning, not an error: the case still loads and solves exactly as written.";

    private static string WriteCase(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        Directory.CreateDirectory(temp.File("assets"));
        File.WriteAllText(temp.File(FileA.Replace('/', Path.DirectorySeparatorChar)), OverlayA);
        using JsonDocument m = JsonDocument.Parse(File.ReadAllBytes(manifest));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (JsonProperty p in m.RootElement.EnumerateObject()) p.WriteTo(w);
            w.WriteStartArray("assets");
            w.WriteStringValue(FileA);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(manifest, ms.ToArray());
        return manifest;
    }

    // ---------------------------------------------------------------- helpers (as AuthorOverlayViewTests)

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        AuthorPrompts = _ => new ScriptedPrompts(),
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

    private static MainWindow OpenPlaying(TempFolder temp, ModelSource source)
    {
        var w = new MainWindow(Options(temp, source));
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

    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    private static Control PageView(AssetsView v)
    {
        ContentControl host = v.FindControl<ContentControl>("PageHost")!;
        return host.GetVisualDescendants().OfType<ContentPresenter>().First().Child
            ?? throw new InvalidOperationException("the page host presents nothing");
    }

    private static ItemsControl Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == name);

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

    // A real left click on the symbol with this id, wherever the fitted diagram put it on screen.
    private static void ClickSymbol(MainWindow w, DiagramControl diagram, string id)
    {
        Point? at = null;
        for (double y = 2; y < diagram.Bounds.Height && at is null; y += 2)
            for (double x = 2; x < diagram.Bounds.Width; x += 2)
                if (diagram.HitTestAt(new Point(x, y)) is { Kind: DiagramHitKind.Symbol } hit && hit.Id == id) { at = new Point(x, y); break; }
        Assert.True(at is not null, id + " is not on screen");
        Point wp = diagram.TranslatePoint(at!.Value, w) ?? throw new InvalidOperationException("the diagram is not in the window");
        w.MouseDown(wp, MouseButton.Left);
        w.MouseUp(wp, MouseButton.Left);
        Render(w);
    }

    // =================================================================================================================

    [AvaloniaFact]
    public void Run_mode_draws_the_topology_shows_the_warning_and_CHECK_and_a_click_opens_the_record()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp));
        MainWindow w = OpenPlaying(temp, source);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            w.SelectTab(MainWindow.AssetsTabHeader);
            Render(w);
            AssetsView v = w.AssetsView;
            OverlayIndex ov = nav.Index!.Overlay!;
            Assert.Equal(new[] { Coupling }, ov.Warnings.Select(x => x.Message));   // loaded and running: a warning, not a refusal

            v.FindControl<TreeView>("Tree")!.SelectedItem = nav.Tree.NodeFor(ov.Find("ov.sub")!.Key);
            Render(w);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.True(page.HasDiagram);

            // The diagram: drawn, the topology's symbols, the coupler in line and closed, the sections live from the frame.
            AssetDiagramView view = PageView(v).GetVisualDescendants().OfType<AssetDiagramView>().Single(d => d.Name == "OverlayDiagram");
            Assert.True(view.IsEffectivelyVisible);
            DiagramModel d = view.Diagram.Model!;
            Assert.Equal(new[] { "ov.bb1", "ov.bb2", "ov.cbl", "ov.cbl/END", "ov.es", "ov.cpl" }, d.Symbols.Select(s => s.Id));
            Assert.Equal((DiagramRotation.ThreeQuarter, DiagramSwitchState.Closed), (d.Symbol("ov.cpl").Rotation, d.Symbol("ov.cpl").State.Switch));
            Assert.True(d.Symbol("ov.bb1").State.Energised);
            Assert.True(d.Symbol("ov.bb2").State.Energised);
            Assert.NotNull(view.Diagram.CurrentDrawing());

            // The warning on the page, in full; and the CHECK sub-tab lists it under its own caption.
            List<string> shown = Texts(PageView(v));
            Assert.Contains(Coupling, shown);
            Assert.Contains("Topology warning", shown);
            Assert.Contains("SINGLE-LINE VIEW OF THE OVERLAY  (SWITCH STATES AS WRITTEN; COLOURS FROM THE SOLVED BUS VOLTAGES)", shown);
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CHECK" }, v.FindControl<TabStrip>("SubTabStrip")!.Items.Cast<string>().ToArray());
            v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = "CHECK";
            Render(w);
            ContentControl check = v.FindControl<ContentControl>("CheckHost")!;
            Assert.True(check.IsEffectivelyVisible);
            List<string> checkShown = Texts(check);
            Assert.Contains(OverlayCheck.WarningsSection, checkShown);
            Assert.Contains(Coupling, checkShown);
            Assert.Contains("1 topology warning in " + FileA + " (the case still loads and solves exactly as written)", checkShown);
            Assert.Equal(1, Named(check, "CheckWarnings").ItemCount);
            Assert.Equal(0, Named(check, "CheckRows").ItemCount);

            // A real click on section ov.bb2 opens its record.
            v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = "OVERVIEW";
            Render(w);
            view = PageView(v).GetVisualDescendants().OfType<AssetDiagramView>().Single(x => x.Name == "OverlayDiagram");
            ClickSymbol(w, view.Diagram, "ov.bb2");
            Assert.Equal(ov.Find("ov.bb2")!.Key, nav.Selected!.Key);
            Assert.Same(nav.Selected, v.FindControl<TreeView>("Tree")!.SelectedItem);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Author_mode_draws_the_topology_and_toggling_normalOpen_redraws_it_and_clears_the_warning()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp));
        MainWindow w = OpenPlaying(temp, source);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            w.SelectTab(MainWindow.AssetsTabHeader);
            Render(w);
            AssetsView v = w.AssetsView;
            v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(Await(v.PendingModeSwitch!));
            Render(w);
            AssetAuthorModel author = nav.Author!;
            AuthorView pane = v.FindControl<AuthorView>("AuthorPane")!;

            // The substation's record page: its topology, the coupler closed, nothing energised, the warning shown.
            AuthorNode sub = author.ClassNode("overlay")!.Children.Single(n => n.Label == "ov.sub");
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = sub;
            Render(w);
            DiagramControl diagram = pane.GetVisualDescendants().OfType<DiagramControl>().Single(x => x.Name == "TopologyDiagram");
            Assert.True(diagram.IsEffectivelyVisible);
            Assert.Equal(DiagramSwitchState.Closed, diagram.Model!.Symbol("ov.cpl").State.Switch);
            Assert.All(diagram.Model.Symbols, s => Assert.False(s.State.Energised));
            List<string> shown = Texts(pane);
            Assert.Contains(Coupling, shown);
            Assert.Contains("Normal-state nodes: ['ov.bb1', 'ov.bb2']", shown);
            Assert.Contains("SUBSTATION 'ov.sub'  -  SINGLE-LINE VIEW OF THE OVERLAY  (CARRIED, NOT USED BY THE SOLVER)", shown);

            // The coupler: PROPERTIES, type normalOpen = true and Enter (one undo step).
            AuthorNode cpl = sub.Children.Single(n => n.Label == "ov.cpl");
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = cpl;
            Render(w);
            pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = "PROPERTIES";
            Render(w);
            TextBox box = FieldBox(pane, "params.normalOpen");
            box.Text = "true";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
            Render(w);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal(1, author.Authoring.UndoDepth);

            // OVERVIEW of the coupler shows the substation's topology, redrawn: the coupler open, two nodes, no warning.
            pane.FindControl<TabStrip>("AuthorSubTabStrip")!.SelectedItem = "OVERVIEW";
            Render(w);
            diagram = pane.GetVisualDescendants().OfType<DiagramControl>().Single(x => x.Name == "TopologyDiagram");
            Assert.True(diagram.IsEffectivelyVisible);
            Assert.Equal(DiagramSwitchState.Open, diagram.Model!.Symbol("ov.cpl").State.Switch);
            shown = Texts(pane);
            Assert.DoesNotContain(Coupling, shown);
            Assert.Contains("Normal-state nodes: ['ov.bb1'] and ['ov.bb2']", shown);
            Assert.Empty(author.RecordPage!.Topology!.Warnings);

            // A real click on section ov.bb1 selects its record.
            ClickSymbol(w, diagram, "ov.bb1");
            Assert.Equal("ov.bb1", author.Selected!.Label);
        }
        finally { Close(w); }
    }
}
