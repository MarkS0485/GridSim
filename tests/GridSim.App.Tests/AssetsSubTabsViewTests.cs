using System.Diagnostics;
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
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS B2: the ASSETS tab's PROPERTIES and CONNECTIONS sub-tabs and the new class nodes in the Avalonia
/// window (headless, Skia), for cases loaded through the run pipeline (which declares each model's source to the tab).
/// The sub-tabs appear only where they have content; PROPERTIES renders the case record's rows; CONNECTIONS renders the
/// links and a click selects the right node; a source with no case file says why; the new class pages render and their
/// rows (and OPEN INVERTER FLEET) go where they say.
/// </summary>
public sealed class AssetsSubTabsViewTests
{
    // ---------------------------------------------------------------- helpers (as AssetsViewTests)

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case(string name)
    {
        if (name is "uk" or "gb-synthetic")
        {
            string dir = Path.Combine(RepoRoot(), "data", name);
            Assert.True(File.Exists(Path.Combine(dir, "system.json")), "missing test case " + dir);
            return new ModelSource(name, dir);
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

    private static IEnumerable<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    private static Control PageView(AssetsView v)
    {
        ContentControl host = v.FindControl<ContentControl>("PageHost")!;
        return host.GetVisualDescendants().OfType<ContentPresenter>().First().Child
            ?? throw new InvalidOperationException("the page host presents nothing");
    }

    private static string[] Strip(AssetsView v) => v.FindControl<TabStrip>("SubTabStrip")!.Items.Cast<string>().ToArray();

    private static List<AssetTreeNode> Flatten(AssetTreeNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten)).ToList();

    // Select a node the way a user does: through the tree's selection.
    private static void Pick(MainWindow w, AssetsView v, AssetTreeNode node)
    {
        v.FindControl<TreeView>("Tree")!.SelectedItem = node;
        Render(w);
    }

    // Choose a sub-tab the way a user does: through the strip's selection.
    private static void SubTab(MainWindow w, AssetsView v, string tab)
    {
        v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = tab;
        Render(w);
    }

    private static Button ButtonIn(ItemsControl list, int index) =>
        list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();

    // ---------------------------------------------------------------- the sub-tabs of a case file

    [AvaloniaFact]
    public void PROPERTIES_and_CONNECTIONS_appear_only_with_content_render_their_rows_and_their_links_select_the_right_node()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("uk"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            AssetIndex ix = Assert.IsType<AssetIndex>(nav.Index);
            Assert.True(ix.HasCase, ix.CaseNote);
            Assert.Equal(AssetSourceKind.V1Folder, nav.Source.Kind);
            Assert.False(v.FindControl<TextBlock>("SubTabNoteText")!.IsEffectivelyVisible);
            TreeView tree = v.FindControl<TreeView>("Tree")!;

            // The landing page and a class: OVERVIEW only. A circuit: all three.
            Assert.Equal(new[] { "OVERVIEW" }, Strip(v));
            Pick(w, v, nav.Roots[5]);
            Assert.Equal(new[] { "OVERVIEW" }, Strip(v));
            AssetTreeNode circuit = Flatten(nav.Roots[5]).First(n => n.Role == AssetNodeRole.Unit);
            Pick(w, v, circuit);
            Assert.Same(circuit, nav.Selected);
            Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS" }, Strip(v));
            Assert.True(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);

            // ---- PROPERTIES: the record's heading and every row's label, literal, state and solved value ----
            SubTab(w, v, "PROPERTIES");
            Assert.Equal("PROPERTIES", nav.SelectedSubTab);
            ContentControl props = v.FindControl<ContentControl>("PropertiesHost")!;
            Assert.True(props.IsEffectivelyVisible);
            Assert.False(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);
            Assert.False(v.FindControl<ContentControl>("ConnectionsHost")!.IsEffectivelyVisible);
            AssetProperties p = nav.Properties!;
            List<string> shown = Texts(props).ToList();
            Assert.Contains(p.Heading, shown);
            Assert.Contains(p.Shape, shown);
            Assert.Contains("CASE RECORD  (READ-ONLY IN RUN MODE)", shown);
            foreach (PropertyRow r in p.Rows)
            {
                Assert.Contains(r.Label, shown);
                Assert.Contains(r.Field, shown);
                Assert.Contains(r.State, shown);
                if (r.Literal.Length > 0) Assert.Contains(r.Literal, shown);
                if (r.HasSolved) { Assert.Contains(r.SolvedValue, shown); Assert.Contains(r.SolvedCaption, shown); }
            }
            Assert.Contains(p.Rows, r => r.HasSolved && r.SolvedCaption.StartsWith("as solved: per-circuit equivalent of ", StringComparison.Ordinal));
            Assert.Equal(p.Rows.Count, v.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "PropertyRows").ItemCount);

            // ---- CONNECTIONS: the circuit's ends; a click on the from bus selects that bus and opens its page ----
            SubTab(w, v, "CONNECTIONS");
            Assert.True(v.FindControl<ContentControl>("ConnectionsHost")!.IsEffectivelyVisible);
            ItemsControl model = v.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ModelConnections");
            Assert.Equal(nav.Connections.Model.Count, model.ItemCount);
            List<string> conn = Texts(v.FindControl<ContentControl>("ConnectionsHost")!).ToList();
            Assert.Contains("CONNECTED IN THE MODEL", conn);
            foreach (ConnectionRow r in nav.Connections.Model) { Assert.Contains(r.Relation, conn); Assert.Contains(r.Label, conn); }
            ConnectionRow from = nav.Connections.Model[0];
            ButtonIn(model, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(from.Target, nav.Selected!.Key);
            Assert.Same(nav.Selected, tree.SelectedItem);
            Assert.IsType<SubstationDetailPage>(nav.Current);
            // The bus keeps CONNECTIONS (it has links), so the view stays on it; its where-used list renders and links.
            Assert.Equal("CONNECTIONS", nav.SelectedSubTab);
            ItemsControl used = v.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "WhereUsedConnections");
            Assert.True(used.IsEffectivelyVisible);
            Assert.Equal(nav.Connections.WhereUsed.Count, used.ItemCount);
            Assert.Contains("WHERE USED IN THE CASE FILE", Texts(v.FindControl<ContentControl>("ConnectionsHost")!));
            ConnectionRow u0 = nav.Connections.WhereUsed[0];
            ButtonIn(used, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(u0.Target, nav.Selected!.Key);

            // The landing page has neither: the view goes back to OVERVIEW.
            v.FindControl<Button>("HomeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(new[] { "OVERVIEW" }, Strip(v));
            Assert.Equal("OVERVIEW", nav.SelectedSubTab);
            Assert.True(v.FindControl<ContentControl>("PageHost")!.IsEffectivelyVisible);
            Assert.False(v.FindControl<ContentControl>("PropertiesHost")!.IsEffectivelyVisible);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_MATPOWER_case_shows_why_there_is_no_PROPERTIES_and_still_offers_CONNECTIONS()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("case9"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            TextBlock note = v.FindControl<TextBlock>("SubTabNoteText")!;
            Assert.True(note.IsEffectivelyVisible);
            Assert.Equal("PROPERTIES is not shown: this model was read from a MATPOWER .m file, not a GridSim case file, so it has no case records.", note.Text);
            Assert.Equal(8, nav.Roots.Count);
            Pick(w, v, Flatten(nav.Roots[1]).First(n => n.Role == AssetNodeRole.Unit));
            Assert.Equal(new[] { "OVERVIEW", "CONNECTIONS" }, Strip(v));
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the new class nodes

    [AvaloniaFact]
    public void The_new_class_nodes_render_their_pages_and_their_rows_select_their_members()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("uk"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.Equal(new[] { "DEMANDS", "BOUNDARIES" }, nav.Roots.Skip(8).Select(r => r.Label));
            foreach (AssetTreeNode cls in nav.Roots.Skip(8).ToList())
            {
                Pick(w, v, cls);
                AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
                Control view = PageView(v);
                Assert.Same(page, view.DataContext);
                List<string> shown = Texts(view).ToList();
                foreach (string s in new[] { page.Eyebrow, page.Title, page.Sub, page.RowsHeader }) Assert.Contains(s, shown);
                foreach (OverviewTile t in page.Tiles) { Assert.Contains(t.Label, shown); Assert.Contains(t.Value, shown); }
                foreach (OverviewRow r in page.Rows) { Assert.Contains(r.Name, shown); Assert.Contains(r.Detail, shown); }
                if (page.HasNote) Assert.Contains(page.Note, shown);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "InverterFleetButton" && b.IsEffectivelyVisible);

                // A row opens its member, selected in the tree, with its own page.
                ItemsControl rows = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "OverviewRows");
                OverviewRow r0 = page.Rows[0];
                ButtonIn(rows, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(w);
                Assert.Equal(r0.Target, nav.Selected!.Key);
                Assert.Same(nav.Selected, v.FindControl<TreeView>("Tree")!.SelectedItem);
                AssetOverviewPage unit = Assert.IsType<AssetOverviewPage>(nav.Current);
                List<string> unitShown = Texts(PageView(v)).ToList();
                Assert.Contains(unit.Title, unitShown);
                foreach (OverviewFact f in unit.Facts) { Assert.Contains(f.Label, unitShown); Assert.Contains(f.Value, unitShown); }
                Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS" }, Strip(v));
            }
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void An_asset_class_page_renders_and_OPEN_INVERTER_FLEET_shows_the_INVERTER_FLEET_tab()
    {
        using var temp = new TempFolder();
        MainWindow w = OpenAndSettle(temp, Case("gb-synthetic"));
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetsView v = ShowAssets(w);
            Assert.Equal(new[] { "DEMANDS", "PROTECTIONS", "ASSET CLASSES" }, nav.Roots.Skip(8).Select(r => r.Label));
            AssetTreeNode classes = nav.Roots.Single(r => r.Label == AssetTree.AssetClassesLabel);
            Pick(w, v, classes.Children[0]);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(AssetPageKind.AssetClass, page.Key.Kind);
            List<string> shown = Texts(PageView(v)).ToList();
            foreach (OverviewRow r in page.Rows) { Assert.Contains(r.Name, shown); Assert.Contains(r.Value, shown); }
            Button fleet = PageView(v).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InverterFleetButton");
            Assert.True(fleet.IsEffectivelyVisible);
            fleet.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.Equal(TabModels.InverterFleetHeader, (string)((TabItem)w.TabStrip.SelectedItem!).Header!);
        }
        finally { Close(w); }
    }
}
