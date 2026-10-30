using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Assets;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS slice 5-view in the Avalonia window (headless, Skia): a JSON v2 case with an asset overlay, loaded
/// through the run pipeline (which now reads v2 cases with the v2 loader and hands the overlay to the ASSETS tab). The
/// OVERLAY class node and its kinds and records render, the records nested under the model assets render, the OVERLAY,
/// PROPERTIES and CHECK sub-tabs appear only with content and render it, every overlay page and sub-tab shows "carried,
/// not used by the solver", a substation record's diagram is drawn, and every link selects the right node. A v2 case the
/// loader refuses is refused loudly in the run's error area.
/// </summary>
public sealed class AssetsOverlayViewTests
{
    // ---------------------------------------------------------------- the fixture: data/uk as a v2 case with an overlay

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private const string FileA = "assets/site.assets.json", FileB = "lines.assets.json";

    // File A: the substation at North Scotland (bus 1, 400 kV, and bus 101, its GSP bus), a coupler, a line bay, a demand
    // bay, a normally open disconnector. File B: the route of line L2-3 with a span, a conductor and a tower type, a soil
    // zone. Ten records; the demand is named as the v2 writer names a bus's demand ("demand-101").
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

    // data/uk written as a JSON v2 case (the convert verb's writer) into temp, the overlay files beside it, listed in its
    // manifest's "assets" member (with bad file A in place of the good one when given).
    private static string WriteCase(TempFolder temp, string? overlayA = OverlayA)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        string network = File.ReadAllText(temp.File(V2CaseWriter.NetworkFileNameFor(manifest)));
        foreach (string name in new[] { "\"L1-2 N-S Scotland\"", "\"L2-3 B6 boundary\"", "\"demand-101\"" })
            Assert.Contains(name, network, StringComparison.Ordinal);   // the names the overlay points at are the writer's
        if (overlayA is null) return manifest;
        Directory.CreateDirectory(temp.File("assets"));
        File.WriteAllText(temp.File(FileA.Replace('/', Path.DirectorySeparatorChar)), overlayA);
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

    // ---------------------------------------------------------------- helpers (as AssetsSubTabsViewTests)

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
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

    private static MainWindow OpenAndSettle(TempFolder temp, ModelSource source)
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
        Await(w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync());
        PumpFor(150);
        Assert.NotNull(run.LastFrame);
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

    private static Control PageView(AssetsView v)
    {
        ContentControl host = v.FindControl<ContentControl>("PageHost")!;
        return host.GetVisualDescendants().OfType<ContentPresenter>().First().Child
            ?? throw new InvalidOperationException("the page host presents nothing");
    }

    private static string[] Strip(AssetsView v) => v.FindControl<TabStrip>("SubTabStrip")!.Items.Cast<string>().ToArray();

    private static void Pick(MainWindow w, AssetsView v, AssetTreeNode node)
    {
        v.FindControl<TreeView>("Tree")!.SelectedItem = node;
        Render(w);
    }

    private static void SubTab(MainWindow w, AssetsView v, string tab)
    {
        v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = tab;
        Render(w);
    }

    private static ItemsControl Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == name);

    private static Button ButtonIn(ItemsControl list, int index) =>
        list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();

    private static bool IsOverlayNode(AssetTreeNode n) =>
        n.Key.Kind == AssetKeyKind.OverlayRecord || n.Id.StartsWith("group:overlay:", StringComparison.Ordinal)
        || n.Id == "class:" + AssetPageKind.OverlayFleet;

    // ---------------------------------------------------------------- the overlay in the window

    [AvaloniaFact]
    public void The_overlay_nodes_and_sub_tabs_render_and_their_links_select_the_right_node()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp));
        MainWindow w = OpenAndSettle(temp, source);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            AssetNavigatorModel nav = vm.Tabs.Assets;
            Assert.NotNull(vm.Run.LoadedOverlay);
            Assert.Equal(10, vm.Run.LoadedOverlay!.Records.Count);
            AssetsView v = ShowAssets(w);
            Assert.Equal(AssetSourceKind.V2Case, nav.Source.Kind);
            AssetIndex ix = Assert.IsType<AssetIndex>(nav.Index);
            Assert.True(ix.HasCase, ix.CaseNote);
            OverlayIndex ov = Assert.IsType<OverlayIndex>(ix.Overlay);
            TreeView tree = v.FindControl<TreeView>("Tree")!;

            // ---- the OVERLAY class node and its page ----
            AssetTreeNode cls = nav.Roots[^1];
            Assert.Equal(AssetTree.OverlayLabel, cls.Label);
            Pick(w, v, cls);
            Assert.Same(cls, nav.Selected);
            Assert.Equal(new[] { "OVERVIEW" }, Strip(v));
            AssetOverviewPage fleet = Assert.IsType<AssetOverviewPage>(nav.Current);
            Control view = PageView(v);
            Assert.Same(fleet, view.DataContext);
            List<string> shown = Texts(view);
            foreach (string s in new[] { fleet.Eyebrow, fleet.Title, fleet.Sub, fleet.Note, fleet.RowsHeader }) Assert.Contains(s, shown);
            foreach (OverviewTile t in fleet.Tiles) { Assert.Contains(t.Label, shown); Assert.Contains(t.Value, shown); }
            Assert.Equal(10, Named(view, "OverviewRows").ItemCount);

            // A row selects that record's canonical node, in the tree too.
            OverviewRow r0 = fleet.Rows[0];
            ButtonIn(Named(view, "OverviewRows"), 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(r0.Target, nav.Selected!.Key);
            Assert.Same(nav.Selected, tree.SelectedItem);
            Assert.False(nav.Selected.IsAttachment);

            // ---- a model asset with overlay records: the OVERLAY sub-tab renders them; a row selects the record ----
            AssetTreeNode bus1 = nav.Tree.Substation(1)!;
            Pick(w, v, bus1);
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS", "OVERLAY" }, Strip(v));
            SubTab(w, v, "OVERLAY");
            ContentControl host = v.FindControl<ContentControl>("OverlayHost")!;
            Assert.True(host.IsEffectivelyVisible);
            Assert.False(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);
            AssetOverlayList list = nav.OverlayList!;
            List<string> overlayShown = Texts(host);
            Assert.Contains(list.Heading, overlayShown);
            Assert.Contains(list.Note, overlayShown);
            Assert.Contains("ASSET OVERLAY  (CARRIED, NOT USED BY THE SOLVER)", overlayShown);
            ItemsControl rows = Named(host, "OverlayRows");
            Assert.Equal(list.Rows.Count, rows.ItemCount);
            Assert.Equal(new[] { "ov.sub", "ov.bb1" }, list.Rows.Select(r => r.Label));
            foreach (ConnectionRow r in list.Rows) { Assert.Contains(r.Relation, overlayShown); Assert.Contains(r.Label, overlayShown); }
            ButtonIn(rows, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(ov.Find("ov.bb1")!.Key, nav.Selected!.Key);
            Assert.Same(nav.Selected, tree.SelectedItem);

            // ---- an overlay record: OVERVIEW (facts, links) and PROPERTIES (the record as written) ----
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES" }, Strip(v));
            Assert.True(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);
            AssetOverviewPage bb1 = Assert.IsType<AssetOverviewPage>(nav.Current);
            List<string> recShown = Texts(PageView(v));
            Assert.Contains("ov.bb1", recShown);
            Assert.Contains(bb1.Sub, recShown);
            foreach (OverviewFact f in bb1.Facts) { Assert.Contains(f.Label, recShown); Assert.Contains(f.Value, recShown); }
            foreach (OverviewRow r in bb1.Rows) { Assert.Contains(r.Name, recShown); Assert.Contains(r.Detail, recShown); }
            SubTab(w, v, "PROPERTIES");
            ContentControl props = v.FindControl<ContentControl>("OverlayPropertiesHost")!;
            Assert.True(props.IsEffectivelyVisible);
            Assert.Null(nav.Properties);
            OverlayRecordProperties p = nav.OverlayProperties!;
            List<string> propShown = Texts(props);
            Assert.Contains("OVERLAY RECORD  (READ-ONLY IN RUN MODE; CARRIED, NOT USED BY THE SOLVER)", propShown);
            Assert.Contains(p.Heading, propShown);
            Assert.Contains(p.Note, propShown);
            foreach (PropertyRow r in p.Rows)
            {
                Assert.Contains(r.Label, propShown);
                Assert.Contains(r.Field, propShown);
                Assert.Contains(r.State, propShown);
                if (r.Literal.Length > 0) Assert.Contains(r.Literal, propShown);
            }
            Assert.Equal(p.Rows.Count, Named(props, "OverlayPropertyRows").ItemCount);
            SubTab(w, v, "OVERVIEW");

            // The record's bus link selects the bus (the model asset), its parent link the substation record.
            ItemsControl links = Named(PageView(v), "OverviewRows");
            int busRow = bb1.Rows.ToList().FindIndex(r => r.Target == AssetKey.Substation(1));
            ButtonIn(links, busRow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Same(bus1, nav.Selected);
            Assert.IsType<SubstationDetailPage>(nav.Current);

            // ---- the substation record draws its sections and switchgear ----
            Pick(w, v, nav.Tree.NodeFor(ov.Find("ov.sub")!.Key)!);
            AssetOverviewPage sub = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.True(sub.HasDiagram);
            AssetDiagramView diagram = PageView(v).GetVisualDescendants().OfType<AssetDiagramView>().Single(d => d.Name == "OverlayDiagram");
            Assert.True(diagram.IsEffectivelyVisible);
            Assert.NotNull(diagram.Diagram.Model);
            Assert.Contains(diagram.Diagram.Model!.Symbols, s => s.Id == "ov.bb2");
            // Test retargeted 2026-10-20 (Phase 5c): the caption was "(AS WRITTEN, NOT SOLVED)"; the diagram now colours
            // each section by the solved voltage of its bus (switch states still as written), and the caption says so,
            // exactly. The old caption must be gone (it would now be untrue).
            Assert.Contains("SINGLE-LINE VIEW OF THE OVERLAY  (SWITCH STATES AS WRITTEN; COLOURS FROM THE SOLVED BUS VOLTAGES)", Texts(PageView(v)));
            Assert.DoesNotContain("SINGLE-LINE VIEW OF THE OVERLAY  (AS WRITTEN, NOT SOLVED)", Texts(PageView(v)));
            Assert.True(nav.OpenFromDiagram("ov.cbl/END"));
            Render(w);
            Assert.Equal(AssetKeyKind.Circuit, nav.Selected!.Key.Kind);
            Assert.IsType<CircuitDetailPage>(nav.Current);

            // ---- the records nested under the bus: an attachment node opens the same record ----
            AssetTreeNode under = bus1.Children.Single();
            Assert.True(under.IsAttachment);
            Pick(w, v, under);
            Assert.Same(under, nav.Selected);
            Assert.Equal("ov.sub", Assert.IsType<AssetOverviewPage>(nav.Current).Title);
            Pick(w, v, under.Children[0]);
            Assert.Equal("ov.bb1", Assert.IsType<AssetOverviewPage>(nav.Current).Title);
            Assert.Same(under.Children[0], tree.SelectedItem);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Every_overlay_page_and_sub_tab_shows_carried_not_used_by_the_solver()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp));
        MainWindow w = OpenAndSettle(temp, source);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            OverlayIndex ov = nav.Index!.Overlay!;
            List<AssetTreeNode> nodes = nav.Tree.PreOrder().Where(IsOverlayNode).ToList();
            Assert.True(nodes.Count >= 10 + 6 + 1, $"only {nodes.Count} overlay nodes");
            foreach (AssetTreeNode n in nodes)
            {
                Pick(w, v, n);
                Assert.Same(n, nav.Selected);
                List<string> shown = Texts(PageView(v));
                Assert.Contains(shown, s => s.Contains("not used by the solver", StringComparison.Ordinal));
                Assert.Contains(OverlayIndex.SolverNote, shown);
                if (n.Key.Kind == AssetKeyKind.OverlayRecord)
                {
                    SubTab(w, v, "PROPERTIES");
                    List<string> props = Texts(v.FindControl<ContentControl>("OverlayPropertiesHost")!);
                    Assert.Contains(OverlayIndex.SolverNote, props);
                    Assert.Contains(props, s => s.Contains("CARRIED, NOT USED BY THE SOLVER", StringComparison.Ordinal));
                    SubTab(w, v, "OVERVIEW");
                }
            }
            foreach (AssetKey asset in ov.AttachedAssets)
            {
                Pick(w, v, nav.Tree.NodeFor(asset)!);
                Assert.Contains("OVERLAY", Strip(v));
                SubTab(w, v, "OVERLAY");
                List<string> shown = Texts(v.FindControl<ContentControl>("OverlayHost")!);
                Assert.Contains(OverlayIndex.SolverNote, shown);
                Assert.Contains("ASSET OVERLAY  (CARRIED, NOT USED BY THE SOLVER)", shown);
                SubTab(w, v, "OVERVIEW");
            }
            // A model page itself carries no overlay note (the overlay changes no model page).
            Pick(w, v, nav.Tree.Substation(1)!);
            Assert.DoesNotContain(Texts(PageView(v)), s => s.Contains("not used by the solver", StringComparison.Ordinal));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_v2_case_whose_overlay_fails_its_checker_is_refused_loudly_and_the_tab_keeps_what_it_showed()
    {
        using var temp = new TempFolder();
        string bad = OverlayA.Replace("\"line\": { \"line\": \"L1-2 N-S Scotland\" }", "\"line\": { \"line\": \"No such line\" }", StringComparison.Ordinal);
        Assert.NotEqual(OverlayA, bad);
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp, bad));
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            Task<string?> load = run.LoadAsync(source);
            Await(load);
            string? refusal = load.Result;
            Assert.NotNull(refusal);
            Assert.StartsWith("Load failed: ", refusal, StringComparison.Ordinal);
            Assert.Contains("record 'ov.cbl': role 'line' points at line 'No such line', which is not a line of the case.", refusal, StringComparison.Ordinal);
            Assert.True(run.HasError);
            Assert.Contains(refusal, run.ErrorText, StringComparison.Ordinal);
            Assert.Null(run.LoadedModel);
            Assert.Null(run.LoadedOverlay);
            Assert.Null(w.ViewModel.Tabs.Assets.Index);
            Assert.Equal(AssetSourceKind.Undeclared, w.ViewModel.Tabs.Assets.Source.Kind);
        }
        finally { Close(w); }
    }
}
