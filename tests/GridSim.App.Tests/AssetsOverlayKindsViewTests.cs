using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
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
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 5e-5g in the Avalonia window (headless, Skia): the kind-specific OVERVIEW tables of the asset
/// overlay render, with every column title (units in brackets) and every cell of every table on screen, and the caption
/// of each table says the overlay is carried and not used by the solver. The case is data/uk written as a JSON v2 case
/// (as AssetsOverlayViewTests), with an overlay of the earthing kinds.
/// </summary>
public sealed class AssetsOverlayKindsViewTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private const string FileA = "site.assets.json";

    // A substation at bus 1 with an earth path in a soil zone; the route of line L2-3 with an earth wire on a conductor.
    private const string Overlay = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "North Scotland" } },
          { "id": "ov.earth", "kind": "earthPath", "parent": "ov.sub", "refs": { "bus": { "bus": 1 }, "soil": { "record": "ov.soil" } }, "params": { "resistanceOhm": 0.5 } },
          { "id": "ov.soil", "kind": "soilZone", "refs": {}, "params": { "resistivityOhmM": 100, "depthM": 2 } },
          { "id": "ov.zebra", "kind": "conductorType", "refs": {}, "params": { "code": "Zebra", "crossSectionMm2": 428.9, "dcResistanceOhmPerKm": 0.0674 } },
          { "id": "ov.route", "kind": "lineRoute", "refs": { "line": { "line": "L2-3 B6 boundary" } }, "params": { "name": "B6 route" } },
          { "id": "ov.ew", "kind": "earthWire", "parent": "ov.route", "refs": { "conductor": { "record": "ov.zebra" } }, "params": { "count": 1, "bondedAtBothEnds": true } },
          { "id": "ov.hvdc", "kind": "hvdcLink", "refs": { "sendingConverter": { "generator": "Moyle" }, "receivingConverter": { "generator": "Torness Nuclear" } }, "params": { "technology": "vsc", "ratedMw": 500, "dcVoltageKv": 250, "cableLengthKm": 55 } }
        ] }
        """;

    private static string WriteCase(TempFolder temp, string overlay = Overlay)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        File.WriteAllText(temp.File(FileA), overlay);
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

    // Author mode asks nothing in these tests: any question is a failure.
    private sealed class NoPrompts : IAuthorPrompts
    {
        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message) => throw new InvalidOperationException("unexpected question: " + message);
        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException("unexpected confirmation: " + message);
        public Task<string?> AskTextAsync(string title, string message, string initial) => throw new InvalidOperationException("unexpected question: " + message);
        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => throw new InvalidOperationException("unexpected prompt: " + message);
    }

    // Phase 5f: data/uk as a v2 case with a three-winding transformer as its authored star equivalent (star bus 201, a
    // 13 kV bus 202, legs '3W HV' 2 -> 201, '3W MV' 201 -> 102, '3W LV' 201 -> 202) and its transformer3W record.
    private const string Overlay3W = """
        { "schema": "gridsim-assets/2", "records": [
          { "id": "ov.sub", "kind": "substation", "refs": {}, "params": { "name": "South Scotland" } },
          { "id": "ov.t3w", "kind": "transformer3W", "parent": "ov.sub", "refs": { "starBus": { "bus": 201 }, "hv": { "transformer": "3W HV" }, "mv": { "transformer": "3W MV" }, "lv": { "transformer": "3W LV" } }, "params": { "baseMva": 100, "hvKv": 400, "mvKv": 132, "lvKv": 13, "r12Pu": 0.0078125, "x12Pu": 0.125, "r13Pu": 0.015625, "x13Pu": 0.25, "r23Pu": 0.01171875, "x23Pu": 0.1875 } }
        ] }
        """;

    private static string WriteCase3W(TempFolder temp)
    {
        string manifest = WriteCase(temp, Overlay3W);
        string network = temp.File(V2CaseWriter.NetworkFileNameFor(manifest));
        JsonObject root = JsonNode.Parse(File.ReadAllText(network))!.AsObject();
        root["buses"]!.AsArray().Add(JsonNode.Parse("""{ "id": 201, "name": "3W star", "baseKv": 400 }"""));
        root["buses"]!.AsArray().Add(JsonNode.Parse("""{ "id": 202, "name": "3W LV 13kV", "baseKv": 13 }"""));
        // The writer writes data/uk's transformers in its lines array (its line / transformer split keeps the branch
        // order), so the network may have no transformers array yet: the legs start one.
        root["transformers"] ??= new JsonArray();
        foreach (string leg in new[]
                 {
                     """{ "name": "3W HV", "from": 2, "to": 201, "rPu": 0.0017, "xPu": 0.05 }""",
                     """{ "name": "3W MV", "from": 201, "to": 102, "rPu": 0.0017, "xPu": 0.05 }""",
                     """{ "name": "3W LV", "from": 201, "to": 202, "rPu": 0.0017, "xPu": 0.05 }""",
                 })
            root["transformers"]!.AsArray().Add(JsonNode.Parse(leg));
        File.WriteAllText(network, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        AuthorPrompts = _ => new NoPrompts(),
        AuthorUserCasesFolder = temp.File("user-cases"),
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

    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    private static Control PageView(AssetsView v)
    {
        ContentControl host = v.FindControl<ContentControl>("PageHost")!;
        return host.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Child
            ?? throw new InvalidOperationException("the page host presents nothing");
    }

    // Every table of the page is on screen: its caption, each column title, each cell, and its note.
    private static void AssertTablesShown(AssetOverviewPage page, Control view, bool overlayValuesOnly = true)
    {
        List<string> shown = Texts(view);
        ItemsControl tables = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "OverviewTables");
        Assert.True(tables.IsEffectivelyVisible);
        Assert.Equal(page.Tables.Count, tables.ItemCount);
        foreach (OverviewTable t in page.Tables)
        {
            Assert.Contains(t.Title, shown);
            if (overlayValuesOnly) Assert.EndsWith("CARRIED, NOT USED BY THE SOLVER)", t.Title, StringComparison.Ordinal);
            foreach (string c in t.Columns) Assert.Contains(c, shown);
            foreach (OverviewTableRow r in t.Rows)
                foreach (string cell in r.Cells) Assert.Contains(cell, shown);
            if (t.HasNote) Assert.Contains(t.Note, shown);
        }
        foreach (OverviewFact f in page.Facts) { Assert.Contains(f.Label, shown); Assert.Contains(f.Value, shown); }
    }

    [AvaloniaFact]
    public void The_earthing_tables_render_with_every_column_and_cell_and_say_carried_not_used_by_the_solver()
    {
        using var temp = new TempFolder();
        ModelSource source = ModelCatalog.V2CaseSource(WriteCase(temp));
        MainWindow w = OpenAndSettle(temp, source);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            w.SelectTab(MainWindow.AssetsTabHeader);
            Render(w);
            AssetsView v = w.AssetsView;
            OverlayIndex ov = nav.Index!.Overlay!;
            foreach (string id in new[] { "ov.soil", "ov.earth", "ov.ew", "ov.sub" })
            {
                v.FindControl<TreeView>("Tree")!.SelectedItem = nav.Tree.NodeFor(ov.Find(id)!.Key)!;
                Render(w);
                AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
                Assert.Equal(id, page.Title);
                Assert.True(page.HasTables, id);
                AssertTablesShown(page, PageView(v));
                Assert.Contains(page.Note, Texts(PageView(v)));
            }
            // Phase 5g: the HVDC link's page (its own values, the ends' solved output, the sum check) and its CHECK notes.
            v.FindControl<TreeView>("Tree")!.SelectedItem = nav.Tree.NodeFor(ov.Find("ov.hvdc")!.Key)!;
            Render(w);
            AssetOverviewPage hvdc = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(3, hvdc.Tables.Count);
            Assert.Equal("solved", hvdc.Tables[1].Rows[0].Cells[4]);
            AssertTablesShown(hvdc, PageView(v), overlayValuesOnly: false);
            Assert.EndsWith("CARRIED, NOT USED BY THE SOLVER)", hvdc.Tables[0].Title, StringComparison.Ordinal);
            OverlayCheck check = Assert.IsType<OverlayCheck>(nav.OverlayCheck);
            Assert.True(check.HasNotes);
            v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = "CHECK";
            Render(w);
            ContentControl checkHost = v.FindControl<ContentControl>("CheckHost")!;
            Assert.True(checkHost.IsEffectivelyVisible);
            List<string> checkShown = Texts(checkHost);
            Assert.Contains(OverlayCheck.NotesSection, checkShown);
            Assert.Contains(check.Heading, checkShown);
            foreach (OverlayCheckRow n in check.Notes) Assert.Contains(n.Message, checkShown);
            v.FindControl<TabStrip>("SubTabStrip")!.SelectedItem = "OVERVIEW";
            Render(w);

            // A page with no table (a model asset) hides the tables list.
            v.FindControl<TreeView>("Tree")!.SelectedItem = nav.Roots[^1];
            Render(w);
            AssetOverviewPage fleet = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.False(fleet.HasTables);
            Assert.False(PageView(v).GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "OverviewTables").IsEffectivelyVisible);
        }
        finally
        {
            Close(w);
        }
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        return task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void Derive_star_legs_in_Author_proposes_on_screen_and_writes_the_six_values_only_on_apply_as_one_undo_step()
    {
        using var temp = new TempFolder();
        string manifest = WriteCase3W(temp);
        string network = temp.File(V2CaseWriter.NetworkFileNameFor(manifest));
        byte[] networkBefore = File.ReadAllBytes(network);
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        MainWindow w = OpenAndSettle(temp, source);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            w.SelectTab(MainWindow.AssetsTabHeader);
            Render(w);
            AssetsView v = w.AssetsView;

            // RUN: the transformer3W page shows its three tables (nameplate, pairwise, the legs authored beside derived).
            OverlayIndex ov = nav.Index!.Overlay!;
            v.FindControl<TreeView>("Tree")!.SelectedItem = nav.Tree.NodeFor(ov.Find("ov.t3w")!.Key)!;
            Render(w);
            AssetOverviewPage page = Assert.IsType<AssetOverviewPage>(nav.Current);
            Assert.Equal(3, page.Tables.Count);
            AssertTablesShown(page, PageView(v));

            // AUTHOR: select the record; the panel offers DERIVE only (no edit is shown or applicable yet).
            v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(Await(v.PendingModeSwitch!));
            Render(w);
            AssetAuthorModel author = nav.Author!;
            AuthorView pane = v.FindControl<AuthorView>("AuthorPane")!;
            AuthorNode t3w = author.ClassNode("overlay")!.Children.Single(n => n.Label == "ov.sub").Children.Single(n => n.Label == "ov.t3w");
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = t3w;
            Render(w);
            Assert.Same(t3w, author.Selected);
            StackPanel Panel() => pane.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "StarLegsPanel");
            Button In(string name) => Panel().GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            Assert.True(Panel().IsEffectivelyVisible);
            Assert.False(In("ApplyStarLegsButton").IsEffectivelyVisible);

            // DERIVE: the six edits old -> new and the bases and formulas, on screen; nothing is changed.
            In("DeriveStarLegsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            StarLegsProposal p = Assert.IsType<StarLegsProposal>(author.StarLegs);
            Assert.True(p.CanApply, p.Refusal);
            List<string> shown = Texts(Panel());
            Assert.Equal(6, p.EditLines.Count);
            foreach (string line in p.EditLines) Assert.Contains(line, shown);
            foreach (string line in p.Derivation.Lines) Assert.Contains(line, shown);
            Assert.Contains("HV leg '3W HV' xPu: 0.05 -> 9.375", shown);
            Assert.Equal(0, author.Authoring.UndoDepth);
            Assert.True(In("ApplyStarLegsButton").IsEffectivelyVisible);

            // APPLY (the confirmation): one undo step; nothing is on disk until Save.
            In("ApplyStarLegsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(w);
            Assert.False(author.StatusIsError, author.Status);
            Assert.Equal(1, author.Authoring.UndoDepth);
            Assert.Equal("derive star legs of 'ov.t3w'", author.Authoring.UndoDescription);
            Assert.Null(author.StarLegs);
            Assert.Equal(networkBefore, File.ReadAllBytes(network));
            author.Undo();
            Assert.Equal(0, author.Authoring.UndoDepth);
            Assert.False(author.IsDirty);
        }
        finally
        {
            Close(w);
        }
    }

    // A context menu's entries: each item's header, a separator as "-".
    private static string[] Entries(ContextMenu? menu)
        => menu is null ? Array.Empty<string>() : menu.Items.Cast<object>().Select(i => i is Separator ? "-" : (string)((MenuItem)i).Header!).ToArray();

    [AvaloniaFact]
    public void Derive_star_legs_on_the_transformer3W_context_menu_proposes_like_the_page_button_and_changes_nothing()
    {
        using var temp = new TempFolder();
        string manifest = WriteCase3W(temp);
        string network = temp.File(V2CaseWriter.NetworkFileNameFor(manifest));
        byte[] networkBefore = File.ReadAllBytes(network);
        MainWindow w = OpenAndSettle(temp, ModelCatalog.V2CaseSource(manifest));
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
            AuthorNode sub = author.ClassNode("overlay")!.Children.Single(n => n.Label == "ov.sub");
            AuthorNode t3w = sub.Children.Single(n => n.Label == "ov.t3w");

            // The transformer3W record's menu: the overlay record commands, a separator, then Derive star legs...
            // The substation record's menu (an overlay record of another kind) does not offer it.
            string[] t3wMenu = Entries(pane.MenuFor(t3w));
            Assert.Equal(new[] { "-", "Derive star legs..." }, t3wMenu.Skip(t3wMenu.Length - 2));
            Assert.Equal(Entries(pane.MenuFor(sub)), t3wMenu.Take(t3wMenu.Length - 2));
            Assert.DoesNotContain("Derive star legs...", Entries(pane.MenuFor(sub)));

            // Selected and run from the menu: the same proposal the page's DERIVE button makes, shown, nothing changed.
            pane.FindControl<TreeView>("AuthorTree")!.SelectedItem = t3w;
            Render(w);
            Assert.Same(t3w, author.Selected);
            MenuItem item = pane.MenuFor(t3w)!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Derive star legs...");
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => author.StarLegs is not null, "the star-legs proposal");
            Render(w);
            StarLegsProposal p = Assert.IsType<StarLegsProposal>(author.StarLegs);
            Assert.True(p.CanApply, p.Refusal);
            Assert.Equal(6, p.EditLines.Count);
            Assert.Contains("HV leg '3W HV' xPu: 0.05 -> 9.375", p.EditLines);
            Assert.Equal(0, author.Authoring.UndoDepth);
            Assert.False(author.IsDirty);
            Assert.Equal(networkBefore, File.ReadAllBytes(network));
        }
        finally
        {
            Close(w);
        }
    }
}
