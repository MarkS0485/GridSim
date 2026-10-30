using System.Diagnostics;
using System.Globalization;
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
using GridSim.App.Controls;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Commands;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Scada;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS A3: the ASSETS tab in the Avalonia window, on the headless platform with Skia rendering, for a
/// case loaded through the run pipeline. Every page kind renders its page model's values; the tree, the search box and
/// BACK / FORWARD work through the view; the WPF routes in (the map fly-out's OPEN IN EXPLORER, the SCADA row click, the
/// map's "Open in Assets") land on the right node; SHOW ON MAP selects the asset on NETWORK; a click on the toolkit
/// diagram opens and selects the asset it stands for; and the tab is fed exactly once per frame, through the dashboard,
/// first of the tabs.
/// </summary>
public sealed class AssetsViewTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- helpers

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case(string name)
    {
        if (name == "uk")
        {
            string dir = Path.Combine(RepoRoot(), "data", "uk");
            Assert.True(File.Exists(Path.Combine(dir, "system.json")), "missing test case " + dir);
            return new ModelSource("GB reduced  10-zone", dir);
        }
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource(name, Path.GetDirectoryName(file)!, file);
    }

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

    /// <summary>Open <paramref name="source"/>, play until two frames have been shown, then pause and let any frame in
    /// flight land.</summary>
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

    private static string Header(MainWindow w) => (string)((TabItem)w.TabStrip.SelectedItem!).Header!;

    private static AssetsView ShowAssets(MainWindow w)
    {
        w.SelectTab(MainWindow.AssetsTabHeader);
        Render(w);
        Assert.True(w.AssetsView.IsEffectivelyVisible);
        return w.AssetsView;
    }

    private static IEnumerable<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    private static ContentControl PageHost(AssetsView v) => v.FindControl<ContentControl>("PageHost")!;

    /// <summary>The page's own view: the one control the page host presents, with the page as its data context.</summary>
    private static Control PageView(AssetsView v)
    {
        ContentControl host = PageHost(v);
        Control view = host.GetVisualDescendants().OfType<ContentPresenter>().First().Child
            ?? throw new InvalidOperationException("the page host presents nothing");
        return view;
    }

    // ---------------------------------------------------------------- every page renders its model's values

    [AvaloniaFact]
    public void Every_page_kind_renders_its_page_models_values_and_the_header_follows_the_navigator()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("uk"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.True(nav.Active);
            GridModel m = w.ViewModel.Run.LastFrame!.Model;

            // Every class node, then one unit of every kind (substation, generator, interconnector, circuit, transformer,
            // tap changer).
            var nodes = new List<AssetTreeNode>(nav.Roots);
            foreach (AssetTreeNode cls in nav.Roots.Skip(1))
            {
                AssetTreeNode? unit = cls.Children.SelectMany(Flatten).FirstOrDefault(n => n.Role == AssetNodeRole.Unit);
                if (unit is not null) nodes.Add(unit);
            }
            Assert.Contains(nodes, n => n.Page.Kind == AssetPageKind.Interconnector);
            var kinds = new HashSet<Type>();
            foreach (AssetTreeNode node in nodes)
            {
                nav.Select(node);
                Render(w);
                AssetPageModel page = nav.Current;
                kinds.Add(page.GetType());
                Control view = PageView(v);
                Assert.Same(page, view.DataContext);
                List<string> shown = Texts(view).ToList();
                // The page header (the WPF PageHeader template): eyebrow, title and sub line, as the page says them.
                Assert.Contains(page.Eyebrow, shown);
                Assert.Contains(page.Title, shown);
                Assert.Contains(page.Sub, shown);
                // A live value of each page, as the page model says it.
                foreach (string value in Values(page)) Assert.Contains(value, shown);
                // The header row: crumb, time strip, mode, SHOW ON MAP as the navigator says.
                Assert.Equal(nav.Crumb, v.FindControl<TextBlock>("CrumbText")!.Text);
                Assert.Equal(nav.TimeLabel, v.FindControl<TextBlock>("TimeText")!.Text);
                Assert.Equal("RUN", v.FindControl<TextBlock>("ModeBadge")!.Text);
                Assert.Equal(nav.CanShowOnMap, v.FindControl<Button>("ShowOnMapButton")!.IsEnabled);
                // Test retargeted 2026-10-20 (UI programme ASSETS B1/B2): was "the strip shows exactly OVERVIEW" for every
                // node, true while OVERVIEW was the only sub-tab. B2 offers PROPERTIES and CONNECTIONS where they have
                // content, and this case is a case file, so the assertion is now exact per node and stronger: the strip
                // shows exactly the navigator's sub-tabs, which are exactly OVERVIEW for the landing page and every class
                // node (as before), and exactly OVERVIEW, PROPERTIES, CONNECTIONS for every unit (each has a case record
                // and a connection).
                string[] strip = v.FindControl<TabStrip>("SubTabStrip")!.Items.Cast<string>().ToArray();
                Assert.Equal(nav.SubTabs, strip);
                Assert.Equal(node.Role == AssetNodeRole.Unit ? new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS" } : new[] { "OVERVIEW" }, strip);
            }
            // Test retargeted 2026-10-20 (UI programme ASSETS B1/B2): was "13 page kinds shown". The B2 class nodes of
            // this case (DEMANDS, BOUNDARIES) and their members add one page type, AssetOverviewPage; the 13 A2 page
            // kinds are still each shown (asserted exactly, as before), and the new page type is shown too (stronger).
            Assert.Equal(13, kinds.Count(t => t != typeof(GridSim.Ui.Assets.Overviews.AssetOverviewPage)));   // every page kind of the A2 explorer was shown
            Assert.Contains(typeof(GridSim.Ui.Assets.Overviews.AssetOverviewPage), kinds);
        }
        finally { Close(w); }
    }

    private static IEnumerable<AssetTreeNode> Flatten(AssetTreeNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));

    // One or two values each page shows, read from the page model itself (the template binds them).
    private static IEnumerable<string> Values(AssetPageModel page) => page switch
    {
        AssetHomePage h => h.Classes.Select(c => c.Count).Concat(h.Classes.Select(c => c.Title)),
        GeneratorFleetPage g => new[] { g.TotalOutput, g.Online, g.Units[0].Output },
        PhasorFleetPage p => new[] { p.TotalOutput, p.AngleSpread, p.Tiles[0].Output },
        InterconnectorFleetPage i => new[] { i.TotalCap, i.NetFlow, i.Links[0].Flow },
        TransformerFleetPage t => new[] { t.Count, t.MeanLoading, t.Rows[0].Loading },
        TapChangerFleetPage t => new[] { t.Count, t.InBand },
        CircuitFleetPage c => new[] { c.Count, c.Transfer, c.Circuits[0].Flow, c.Bands[0].Label },
        SubstationFleetPage s => new[] { s.BusCount, s.TotalDemand, s.Rows[0].Demand },
        SubstationDetailPage s => new[] { s.Voltage, s.Angle, s.NetInjection, s.ConnNote },
        GeneratorDetailModel g => new[] { g.Output, g.OperatingPoint, g.Inertia },
        InterconnectorDetailModel i => new[] { i.Flow, i.Direction, i.Landing },
        CircuitDetailPage c => new[] { c.Flow, c.Rpu, c.Qverdict },
        TransformerDetailPage t => new[] { t.FlowFrom, t.Tap, t.HvVm },
        // Test retargeted 2026-10-20 (UI programme ASSETS B1/B2): the B2 page (no WPF counterpart) shows its tiles' and
        // facts' values; added so its nodes, now in the tree of this case, are checked like every other page.
        GridSim.Ui.Assets.Overviews.AssetOverviewPage o => o.Tiles.Select(x => x.Value).Concat(o.Facts.Select(x => x.Value)),
        _ => throw new InvalidOperationException(page.GetType().Name),
    };

    // ---------------------------------------------------------------- the tree, search, back and forward through the view

    [AvaloniaFact]
    public void The_tree_search_box_and_back_and_forward_buttons_drive_the_navigator()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            TreeView tree = v.FindControl<TreeView>("Tree")!;
            Assert.Same(nav.Roots, tree.ItemsSource);
            Assert.Same(nav.Tree.Home, tree.SelectedItem);

            // A click on a tree node (the tree's selection) opens its page. (case9 has no transformer: its branches are
            // all circuits.)
            AssetTreeNode circuits = nav.Roots[5];
            tree.SelectedItem = circuits;
            Render(w);
            Assert.Same(circuits, nav.Selected);
            Assert.IsType<CircuitFleetPage>(nav.Current);

            // A page opened from inside a page moves the tree's selection to its node.
            ((CircuitFleetPage)nav.Current).Circuits[0].OnOpen();
            Render(w);
            Assert.IsType<CircuitDetailPage>(nav.Current);
            Assert.Same(nav.Selected, tree.SelectedItem);
            Assert.True(nav.Selected!.Parent!.IsExpanded);

            // BACK and FORWARD (shown only when there is somewhere to go).
            Button back = v.FindControl<Button>("BackButton")!, forward = v.FindControl<Button>("ForwardButton")!;
            Assert.True(back.IsVisible);
            Assert.False(forward.IsVisible);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Same(circuits, nav.Selected);
            Assert.Same(circuits, tree.SelectedItem);
            Assert.True(forward.IsVisible);
            forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.IsType<CircuitDetailPage>(nav.Current);
            v.FindControl<Button>("HomeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Same(nav.Tree.Home, nav.Selected);
            Assert.IsType<AssetHomePage>(nav.Current);

            // The search box: typing filters (the matches replace the tree), Enter on a bus id opens that bus.
            TextBox box = v.FindControl<TextBox>("SearchBox")!;
            box.Text = "circuit";
            Render(w);
            Assert.Equal("circuit", nav.SearchText);
            Assert.True(nav.SearchHits.Count > 1);
            Assert.False(tree.IsEffectivelyVisible);
            ItemsControl hits = v.FindControl<ItemsControl>("SearchHitList")!;
            Assert.True(hits.IsEffectivelyVisible);
            Assert.Equal(nav.SearchHits.Count, hits.ItemCount);
            Assert.Equal(nav.SearchNote, v.FindControl<TextBlock>("SearchNoteText")!.Text);
            // A click on a match opens it.
            Button firstHit = hits.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
            AssetTreeNode hit0 = nav.SearchHits[0];
            firstHit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Same(hit0, nav.Selected);

            int busId = w.ViewModel.Run.LastFrame!.Model.Buses[4].Id;
            box.Text = busId.ToString(Inv);
            box.Focus();
            Render(w);
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            w.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Render(w);
            Assert.Same(nav.Tree.Substation(busId), nav.Selected);
            Assert.Equal("", box.Text);
            Assert.True(tree.IsEffectivelyVisible);
            Assert.Same(nav.Selected, tree.SelectedItem);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the routes in and out

    [AvaloniaFact]
    public void OPEN_IN_EXPLORER_the_SCADA_row_and_Open_in_Assets_land_on_the_right_node_and_SHOW_ON_MAP_selects_on_NETWORK()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("uk"));
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            AssetNavigatorModel nav = vm.Tabs.Assets;
            GridModel m = vm.Run.LastFrame!.Model;

            // ---- the map fly-out's OPEN IN EXPLORER on a bus (the WPF button, through the dashboard view) ----
            int busId = m.Buses[3].Id;
            vm.Map.SelectBus(busId);
            Render(w);
            Button openIn = w.DashboardTab.FindControl<Button>("OpenInExplorerButton")!;
            openIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(MainWindow.AssetsTabHeader, Header(w));
            Assert.True(nav.Active);
            Assert.Same(nav.Tree.Substation(busId), nav.Selected);
            Assert.IsType<SubstationDetailPage>(nav.Current);
            Assert.Null(w.PendingAssetQuery);   // no bridge: the GDA card stays hidden, as WPF outside bridge mode
            Assert.False(((SubstationDetailPage)nav.Current).AssetVisible);

            // ... and on a branch.
            int tx = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer);
            w.SelectTab(MainWindow.NetworkTabHeader);
            vm.Map.SelectBranch(tx);
            Render(w);
            openIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(MainWindow.AssetsTabHeader, Header(w));
            Assert.Equal("tx:" + tx.ToString(Inv), nav.Selected!.Id);
            Assert.IsType<TransformerDetailPage>(nav.Current);

            // ---- SHOW ON MAP: back to NETWORK with the branch selected (its fly-out open) ----
            AssetsView v = w.AssetsView;
            v.FindControl<Button>("ShowOnMapButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(MainWindow.NetworkTabHeader, Header(w));
            Assert.False(nav.Active);
            Assert.Equal(new MapSelection(MapSelectionKind.Branch, tx), vm.Map.Selection);
            Assert.True(vm.Dashboard.SelectionVisible);
            // ... and a bus.
            ShowAssets(w);
            nav.OpenSubstation(busId);
            v.FindControl<Button>("ShowOnMapButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(MainWindow.NetworkTabHeader, Header(w));
            Assert.Equal(new MapSelection(MapSelectionKind.Bus, busId), vm.Map.Selection);

            // ---- the SCADA register's row click (the WPF OnScadaNavigate) ----
            ScadaModel scada = vm.Tabs.Scada;
            Assert.NotNull(scada.Navigate);
            ScadaPointModel volt = scada.Points.First(p => p.Kind == PointKind.BusVoltage && p.Index == 2);
            scada.OpenPoint(volt);
            Render(w);
            Assert.Equal(MainWindow.AssetsTabHeader, Header(w));
            Assert.Same(nav.Tree.Substation(vm.Run.LastFrame!.Result.BusIds[2]), nav.Selected);
            ScadaPointModel flow = scada.Points.First(p => p.Kind == PointKind.BranchP && !m.Branches[p.Index].IsTransformer);
            scada.OpenPoint(flow);
            Render(w);
            Assert.Equal("circuit:" + flow.Index.ToString(Inv), nav.Selected!.Id);
            Assert.IsType<CircuitDetailPage>(nav.Current);
            // The system frequency point has no asset: nothing moves.
            w.SelectTab(MainWindow.NetworkTabHeader);
            AssetPageModel before = nav.Current;
            scada.OpenPoint(scada.Points.First(p => p.Kind == PointKind.SystemFreq));
            Render(w);
            Assert.Equal(MainWindow.NetworkTabHeader, Header(w));
            Assert.Same(before, nav.Current);

            // ---- the map's "Open in Assets" on a branch (the right-click context menu, from the registry) ----
            SchematicControl map = w.MapControl;
            Render(w);
            (int index, Point at) = BranchOnScreen(map);
            ContextMenu menu = w.MapContextMenuAt(at) ?? throw new InvalidOperationException("no context menu on a branch");
            Assert.Equal(index, w.MapActions.ContextBranchIndex);
            Assert.Null(w.MapActions.ContextBusId);
            MenuItem only = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal(AssetCommands.OpenInAssets, (string?)only.Tag);
            Assert.Equal("Open in Assets", (string?)only.Header);
            Assert.True(only.Command!.CanExecute(null));
            only.Command.Execute(null);
            Render(w);
            Assert.Equal(MainWindow.AssetsTabHeader, Header(w));
            Assert.Equal((m.Branches[index].IsTransformer ? "tx:" : "circuit:") + index.ToString(Inv), nav.Selected!.Id);
            Assert.Equal(new[] { AssetCommands.OpenInAssets },
                vm.Registry.ForTarget(CommandTargets.Branch, CommandModes.Run, CommandPlatforms.Desktop).Select(c => c.Id));
        }
        finally { Close(w); }
    }

    // A branch on the map with a point that hits it and no bus (away from the minimap and the right-hand fly-out).
    private static (int Index, Point At) BranchOnScreen(SchematicControl map)
    {
        for (double y = 40; y < map.Bounds.Height - 40; y += 3)
            for (double x = 40; x < map.Bounds.Width - 320; x += 3)
            {
                var p = new Point(x, y);
                if (map.MinimapRect.Inflate(20).Contains(p)) continue;
                if (map.View.HitTestNode(new ScreenPoint(x, y)) is not null) continue;
                if (map.View.HitTestBranch(new ScreenPoint(x, y)) is int b) return (b, p);
            }
        throw new InvalidOperationException("no branch found on the map");
    }

    // ---------------------------------------------------------------- the toolkit diagram

    [AvaloniaFact]
    public void The_IEC_VIEW_shows_the_toolkit_diagram_and_a_click_on_a_bay_opens_and_selects_that_asset()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("uk"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            GridModel m = w.ViewModel.Run.LastFrame!.Model;
            Bus bus = m.Buses.First(b => m.Branches.Count(br => br.From == b.Id || br.To == b.Id) >= 2);
            nav.OpenSubstation(bus.Id);
            Render(w);
            var page = Assert.IsType<SubstationDetailPage>(nav.Current);
            Control view = PageView(v);

            // The A1 picture first (WPF's), drawn from the A1 display list of the page's data.
            SubstationMap picture = view.GetVisualDescendants().OfType<SubstationMap>().Single();
            AssetDiagramView diagram = view.GetVisualDescendants().OfType<AssetDiagramView>().Single();
            Assert.True(picture.IsEffectivelyVisible);
            Assert.False(diagram.IsVisible);
            Assert.Same(page.MapInput, picture.Data);
            Assert.NotNull(picture.CurrentDrawing());

            // IEC VIEW: the toolkit diagram of the same data instead.
            ToggleButton toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "IecToggle");
            toggle.IsChecked = true;
            Render(w);
            Assert.False(picture.IsVisible);
            Assert.True(diagram.IsEffectivelyVisible);
            Assert.Same(page, diagram.Page);
            DiagramModel model = diagram.Diagram.Model!;
            Assert.Equal(SubstationDiagramBuilder.Build(page.MapInput!).Symbols.Select(s => s.Id), model.Symbols.Select(s => s.Id));

            // A real click on connection bay 1's breaker: the page that connection row opens, selected in the tree.
            Point? at = null;
            for (double y = 2; y < diagram.Diagram.Bounds.Height && at is null; y += 2)
                for (double x = 2; x < diagram.Diagram.Bounds.Width; x += 2)
                    if (diagram.Diagram.HitTestAt(new Point(x, y)) is { Kind: DiagramHitKind.Symbol, Id: "C1/CB" }) { at = new Point(x, y); break; }
            Assert.True(at is not null, "connection bay 1's breaker is not on screen");
            ConnectionRowModel row = page.Connections[1];
            Point wp = diagram.Diagram.TranslatePoint(at!.Value, w) ?? throw new InvalidOperationException("the diagram is not in the window");
            w.MouseDown(wp, MouseButton.Left);
            w.MouseUp(wp, MouseButton.Left);
            Render(w);
            int bi = int.Parse(row.Key[..row.Key.IndexOf(':', StringComparison.Ordinal)], Inv);
            Assert.Equal((m.Branches[bi].IsTransformer ? "tx:" : "circuit:") + bi.ToString(Inv), nav.Selected!.Id);
            Assert.Same(nav.Selected, v.FindControl<TreeView>("Tree")!.SelectedItem);
            Assert.Equal(row.Name, nav.Current.Title);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the feed

    [AvaloniaFact]
    public void ASSETS_gets_exactly_one_update_per_frame_through_the_dashboard_first_of_the_tabs()
    {
        using var temp = new TempFolder();
        ModelSource source = Case("case9");
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            Assert.Equal(TabModels.AssetsHeader, TabModels.UpdateOrder[0]);
            var sequence = new List<string>();
            vm.Tabs.TabUpdated += sequence.Add;
            int frames = 0;
            vm.Run.FrameArriving += (_, e) => { if (e.Kind != RunFrameKind.LiveFaulted) frames++; };
            int labels = 0;
            vm.Tabs.Assets.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssetNavigatorModel.TimeLabel)) labels++; };

            Await(vm.LoadCaseAsync(source));
            Await(vm.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
            PumpUntil(() => frames >= 5, "5 frames");
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            PumpFor(200);
            int n = frames;

            Assert.Equal(n, sequence.Count(h => h == TabModels.AssetsHeader));
            for (int i = 0; i < sequence.Count; i += TabModels.UpdateOrder.Count)
                Assert.Equal(TabModels.AssetsHeader, sequence[i]);   // first of every frame's tab updates
            Assert.Equal(n, labels);                                    // its time strip moved once per frame
            Assert.Equal($"SIM {vm.Dashboard.Clock}  TICK {vm.Run.LastFrame!.Tick}", vm.Tabs.Assets.TimeLabel);   // the dashboard's clock
            Assert.False(vm.Tabs.Assets.Active);                        // not shown: the page is not refreshed
            Assert.Equal(8, vm.Tabs.Assets.Roots.Count);
            Assert.Equal(vm.Run.LastFrame!.Model.Buses.Count.ToString(Inv), vm.Tabs.Assets.Roots[1].Detail);
        }
        finally { Close(w); }
    }
}
