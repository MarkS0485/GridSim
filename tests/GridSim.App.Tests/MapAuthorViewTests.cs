using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Dashboard;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 4.2 in the Avalonia window (headless, Skia): map editing on the NETWORK map in AUTHOR mode. On a
/// temporary v1 case folder and a temporary v2 case loaded through the run pipeline: AUTHOR turns the map into the case
/// being edited (no solved state on it, the live fly-out closed, the Map menu shown); P then a click on empty map places
/// a bus there with the coordinates of the map's own inverse projection (its required fields asked through Author
/// mode's prompts); L then two bus clicks connects them with a line or a transformer; a drag of a bus moves it in one
/// undo step and Ctrl+Z / Ctrl+Y undo and redo it; Delete on a bus with referrers is refused with the engine's text in
/// the status bar and nothing changes; the map and the ASSETS tree share one selection, both ways; the map element menu
/// is Delete and Show in Assets; Save, then RUN reloads the saved case through the run pipeline and its map shows the
/// new bus and branch. Leaving AUTHOR without changes gives the run's own map back. No test writes into data/.
/// </summary>
public sealed class MapAuthorViewTests
{
    // ---------------------------------------------------------------- scripted answers to Author mode's questions

    private sealed class ScriptedPrompts : IAuthorPrompts
    {
        public Queue<DirtyChoice> Dirty { get; } = new();
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
            Asked.Add("text: " + title);
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
        {
            Asked.Add("fields: " + title + ": " + string.Join(",", fields.Select(f => f.Field)));
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

    // data/uk copied into temp (a v1 case folder of the user's own: outside data/, so never bundled).
    private static string CopyV1Case(TempFolder temp)
    {
        string dir = Path.Combine(temp.Path, "uk-copy");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "uk")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    // data/uk written as a JSON v2 case into temp (as AuthorModeViewTests writes one).
    private static string WriteV2Case(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        return manifest;
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

    // Open the window, load the source through the run pipeline, let it play two frames, then pause it.
    private static MainWindow OpenSettled(TempFolder temp, ModelSource source, ScriptedPrompts prompts)
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
        Await(w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync());
        PumpUntil(() => run.IsPaused, "the pause");
        Render(w);
        return w;
    }

    private static void Render(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string?> Menus(MainWindow w) =>
        w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();

    // The ASSETS tab's RUN / AUTHOR toggle (the tab is shown first, as a user would).
    private static bool Toggle(MainWindow w)
    {
        w.SelectTab(MainWindow.AssetsTabHeader);
        Render(w);
        AssetsView v = w.AssetsView;
        v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool done = Await(v.PendingModeSwitch!);
        Render(w);
        return done;
    }

    private static SchematicControl ShowNetwork(MainWindow w)
    {
        w.SelectTab(MainWindow.NetworkTabHeader);
        Render(w);
        SchematicControl map = w.MapControl;
        Assert.True(map.IsEffectivelyVisible);
        map.Focus();
        Dispatcher.UIThread.RunJobs();
        return map;
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Render(w);
    }

    private static Point ToWindow(MainWindow w, Point local)
        => w.MapControl.TranslatePoint(local, w) ?? throw new InvalidOperationException("the map is not in the window");

    // A left click at map-local point <local> (the window's own pointer events, as a user's click).
    private static void Click(MainWindow w, Point local)
    {
        Point wp = ToWindow(w, local);
        w.MouseMove(wp);
        w.MouseDown(wp, MouseButton.Left);
        w.MouseUp(wp, MouseButton.Left);
        Render(w);
    }

    // A drag with the left button from map-local <from> to map-local <to>.
    private static void DragOnMap(MainWindow w, Point from, Point to)
    {
        Point a = ToWindow(w, from), b = ToWindow(w, to);
        w.MouseMove(a);
        w.MouseDown(a, MouseButton.Left);
        for (int i = 1; i <= 8; i++) w.MouseMove(new Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8), RawInputModifiers.LeftMouseButton);
        w.MouseUp(b, MouseButton.Left);
        Render(w);
    }

    private static Point P(ScreenPoint s) => new(s.X, s.Y);

    // The map-local point of bus <id>'s roundel.
    private static Point BusPoint(SchematicControl map, int id) => P(map.View.NodeScreen(map.View.IdToIndex[id]));

    // A point clear of every roundel (40 px), every branch, the minimap and the fly-out corner, at least <apart> px from
    // <avoid> (so two such points are distinct places).
    private static Point EmptyPoint(SchematicControl map, Point? avoid = null, double apart = 60)
    {
        SchematicView v = map.View;
        Rect mm = map.MinimapRect.Inflate(30);
        for (double y = 80; y < map.Bounds.Height - 60; y += 9)
            for (double x = 60; x < map.Bounds.Width - 320; x += 9)
            {
                var p = new Point(x, y);
                if (mm.Contains(p)) continue;
                if (avoid is Point q && Math.Abs(q.X - x) < apart && Math.Abs(q.Y - y) < apart) continue;
                if (v.HitTestNode(new ScreenPoint(x, y)) is not null || v.HitTestBranch(new ScreenPoint(x, y)) is not null) continue;
                bool clear = true;
                for (int i = 0; i < v.Nodes.Count && clear; i++)
                {
                    ScreenPoint n = v.NodeScreen(i);
                    if (Math.Abs(n.X - x) < 40 && Math.Abs(n.Y - y) < 40) clear = false;
                }
                if (clear) return p;
            }
        throw new InvalidOperationException("no empty map point");
    }

    // A bus of <baseKv> whose roundel is clear of every other roundel (so a click on it names it), away from the overlays.
    private static int ClearBus(SchematicControl map, AuthorMap am, double baseKv, int? not = null)
    {
        SchematicView v = map.View;
        Rect mm = map.MinimapRect.Inflate(30);
        foreach (AuthorMapBus b in am.Buses.Where(b => b.BaseKv == baseKv && b.Id != not))
        {
            Point s = BusPoint(map, b.Id);
            if (s.X < 40 || s.Y < 60 || s.X > map.Bounds.Width - 320 || s.Y > map.Bounds.Height - 60 || mm.Contains(s)) continue;
            bool alone = true;
            for (int j = 0; j < v.Nodes.Count && alone; j++)
            {
                if (v.Nodes[j].Id == b.Id) continue;
                ScreenPoint o = v.NodeScreen(j);
                if (Math.Abs(o.X - s.X) < 30 && Math.Abs(o.Y - s.Y) < 30) alone = false;
            }
            if (alone && v.HitTestNode(new ScreenPoint(s.X, s.Y)) == b.Id) return b.Id;
        }
        throw new InvalidOperationException("no clear bus of " + baseKv + " kV");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Values(params (string K, string V)[] kv)
        => kv.Select(p => new KeyValuePair<string, string>(p.K, p.V)).ToList();

    private static JsonObject RecordJson(CaseAuthoring a, CaseRef r) => JsonNode.Parse(a.RawText(r))!.AsObject();

    // =================================================================================================================

    [AvaloniaFact]
    public void Author_map_on_a_v1_case_place_connect_drag_undo_delete_refused_save_then_RUN_reload_shows_the_new_bus_and_line()
    {
        using var temp = new TempFolder();
        string dir = CopyV1Case(temp);
        var prompts = new ScriptedPrompts();
        var source = new ModelSource("uk copy", dir);
        MainWindow w = OpenSettled(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            CommandRegistry r = vm.Registry;
            SchematicControl map = w.MapControl;
            Assert.False(map.EditMode);
            Assert.True(map.View.HasState);   // RUN: the solved state is on the map

            // A bus selected in RUN (its fly-out shows live values) is closed when AUTHOR starts.
            ShowNetwork(w);
            vm.Map.SelectBus(1);
            Assert.True(vm.Dashboard.SelectionVisible);

            // ---- AUTHOR: the map is the case being edited, with no solved state, and the Map menu is there ----
            Assert.True(Toggle(w));
            AssetAuthorModel author = vm.Tabs.Assets.Author!;
            MapAuthorController ed = w.MapAuthor;
            Assert.True(ed.IsActive);
            Assert.True(map.EditMode);
            Assert.False(vm.Dashboard.SelectionVisible);
            Assert.Null(vm.Map.Selection);
            Assert.Equal(new[] { "_File", "_Edit", "_Case", "_Map", "_Help" }, Menus(w));
            MenuItem mapMenu = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Map");
            Assert.Equal(new[] { "Select tool", "Place bus", "Connect", "Delete" }, mapMenu.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            map = ShowNetwork(w);
            AuthorMap am = ed.Map!;
            Assert.False(map.View.HasState);   // no live value on the AUTHOR map (plan Q5)
            Assert.Equal(am.Buses.Select(b => b.Id), map.View.Nodes.Select(n => n.Id));
            Assert.Equal(am.Branches.Count, map.View.Edges.Count);
            Assert.Equal(MapEditTool.Select, ed.Tool);
            // The hover tip names the record as written, no live value.
            string tip = ed.TipAt(BusPoint(map, 1))!;
            Assert.StartsWith("bus 1 'North Scotland 400kV'\n400 kV", tip, StringComparison.Ordinal);
            Assert.DoesNotContain(" pu", tip, StringComparison.Ordinal);   // no voltage, no MW: nothing solved

            // ---- Place bus: P, then a click on empty map ----
            Press(w, Key.P);
            Assert.Equal(MapEditTool.PlaceBus, ed.Tool);
            Assert.True(r.Get(MapAuthorCommands.PlaceBus).IsChecked!());
            Point empty = EmptyPoint(map);
            ScreenPoint world = map.View.ScreenToWorld(new ScreenPoint(empty.X, empty.Y));
            (double x, double y) = MapEditing.CaseCoordinates(world.X, world.Y);
            prompts.Fields.Enqueue(Values(("id", "9001"), ("baseKv", "132")));
            Click(w, empty);
            PumpUntil(() => ed.Map!.Bus(9001) is not null, "the placed bus");
            Assert.Equal("fields: Place bus: id,baseKv", prompts.Asked[^1]);
            Assert.Equal(1, author.Authoring.UndoDepth);
            AuthorMapBus placed = ed.Map!.Bus(9001)!;
            Assert.Equal(placed.Record, author.Selected!.Record);   // the new record is selected in the tree ...
            Assert.Equal(9001, map.EditSelectedBus);                // ... and ringed on the map
            Assert.Equal("{\"id\":9001,\"baseKv\":132,\"x\":" + MapEditing.Format(x) + ",\"y\":" + MapEditing.Format(y) + "}",
                RecordJson(author.Authoring, placed.Record).ToJsonString());
            Assert.Equal(am.Buses.Count + 1, map.View.Nodes.Count);
            Assert.StartsWith("Placed at x " + MapEditing.Format(x) + ", y " + MapEditing.Format(y) + ": Added bus ", vm.StatusText, StringComparison.Ordinal);

            // ---- Connect: L, then a 132 kV bus, then the new bus; a line (v1 asks nothing more) ----
            int from = ClearBus(map, ed.Map!, 132, not: 9001);
            Press(w, Key.L);
            Assert.Equal(MapEditTool.Connect, ed.Tool);
            Click(w, BusPoint(map, from));
            Assert.Equal(from, ed.ConnectFrom);
            Assert.Equal(from, map.EditConnectFrom);
            int branches = ed.Map!.Branches.Count;
            prompts.Fields.Enqueue(Values(("kind", "line")));
            Click(w, BusPoint(map, 9001));
            PumpUntil(() => ed.Map!.Branches.Count == branches + 1, "the new line");
            Assert.Equal("fields: Connect: kind", prompts.Asked[^1]);
            Assert.Null(ed.ConnectFrom);
            CaseRef lineRec = author.Selected!.Record!;
            int lineIndex = ed.Map!.BranchIndexOf(lineRec)!.Value;
            AuthorMapBranch line = ed.Map!.Branches[lineIndex];
            Assert.Equal((from, 9001, false), (line.From, line.To, line.IsTransformer));
            Assert.Equal(lineIndex, map.EditSelectedBranch);   // the new line is selected, on the map too
            Assert.Null(map.EditSelectedBus);
            Assert.Equal(2, author.Authoring.UndoDepth);
            // Its impedance, set in PROPERTIES as any field (the line is the selected record).
            Assert.True(author.EditField("xPu", "0.1").Ok);
            Assert.Equal(3, author.Authoring.UndoDepth);

            // ---- Drag: Escape (select tool), then drag the new bus to another empty point ----
            Press(w, Key.Escape);
            Assert.Equal(MapEditTool.Select, ed.Tool);
            Point dropAt = EmptyPoint(map, avoid: BusPoint(map, 9001));
            ScreenPoint dropWorld = map.View.ScreenToWorld(new ScreenPoint(dropAt.X, dropAt.Y));
            (double dx, double dy) = MapEditing.CaseCoordinates(dropWorld.X, dropWorld.Y);
            DragOnMap(w, BusPoint(map, 9001), dropAt);
            PumpUntil(() => author.Authoring.UndoDepth == 4, "the move");
            Assert.Equal((dx, dy), (ed.Map!.Bus(9001)!.X, ed.Map!.Bus(9001)!.Y));
            Assert.StartsWith("Moved bus 9001 to x " + MapEditing.Format(dx) + ", y " + MapEditing.Format(dy) + ".", vm.StatusText, StringComparison.Ordinal);
            SchematicNode moved = map.View.Nodes[map.View.IdToIndex[9001]];
            Assert.Equal(dropWorld.X, moved.Wx, 5);   // the map redraws it where it was dropped (to the six places written)
            Assert.Equal(dropWorld.Y, moved.Wy, 5);

            // ---- Undo and redo the drag (Ctrl+Z, Ctrl+Y) ----
            Press(w, Key.Z, RawInputModifiers.Control);
            Assert.Equal(3, author.Authoring.UndoDepth);
            Assert.Equal((x, y), (ed.Map!.Bus(9001)!.X, ed.Map!.Bus(9001)!.Y));
            Press(w, Key.Y, RawInputModifiers.Control);
            Assert.Equal(4, author.Authoring.UndoDepth);
            Assert.Equal((dx, dy), (ed.Map!.Bus(9001)!.X, ed.Map!.Bus(9001)!.Y));

            // ---- Delete with referrers: a click selects bus 1, Delete is refused with the engine's text ----
            int busWithLines = ClearBus(map, ed.Map!, 400);
            Click(w, BusPoint(map, busWithLines));
            Assert.Equal(ed.Map!.Bus(busWithLines)!.Record, author.Selected!.Record);   // map -> tree
            Assert.Equal(busWithLines, map.EditSelectedBus);
            byte[][] bytes = author.Authoring.Files.Select(f => File.Exists(Path.Combine(dir, f.FileName)) ? File.ReadAllBytes(Path.Combine(dir, f.FileName)) : Array.Empty<byte>()).ToArray();
            IReadOnlyList<WhereUsedRow> referrers = RecordActions.WhereUsed(author.Authoring, author.Selected.Record!);
            Assert.NotEmpty(referrers);
            Press(w, Key.Delete);
            Assert.StartsWith("Delete refused: " + referrers.Count + " record(s) refer to ", vm.StatusText, StringComparison.Ordinal);
            foreach (WhereUsedRow row in referrers.Take(5)) Assert.Contains(row.What, vm.StatusText, StringComparison.Ordinal);
            Assert.Equal(vm.StatusText, author.Status);
            Assert.True(author.StatusIsError);
            Assert.Equal(4, author.Authoring.UndoDepth);   // nothing changed
            Assert.NotNull(ed.Map!.Bus(busWithLines));

            // ---- Tree -> map: a line selected in the tree is highlighted on the map ----
            AuthorNode firstLine = author.ClassNode("lines")!.Children[0];
            author.Select(firstLine);
            Assert.Equal(ed.Map!.BranchIndexOf(firstLine.Record!), map.EditSelectedBranch);
            Assert.Null(map.EditSelectedBus);
            author.Select(author.ClassNode("generators")!.Children[0]);   // not on the map: nothing marked
            Assert.Null(map.EditSelectedBranch);
            Assert.Null(map.EditSelectedBus);
            Assert.False(r.Get(MapAuthorCommands.Delete).IsEnabled);   // Map > Delete acts only on a map element

            // ---- The map element's context menu: Delete, Show in Assets; Show in Assets opens the tab on it ----
            ContextMenu menu = w.MapContextMenuAt(BusPoint(map, 9001))!;
            Assert.Equal(new[] { "Delete", "Show in Assets" }, menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
            Assert.Equal(ed.Map!.Bus(9001)!.Record, author.Selected!.Record);
            Assert.Null(w.MapContextMenuAt(EmptyPoint(map)));
            Await(r.Get(MapAuthorCommands.ShowInAssets).RunAsync());
            Assert.True(w.AssetsView.IsEffectivelyVisible || w.TabStrip.SelectedItem is TabItem { Header: MainWindow.AssetsTabHeader });

            // ---- Save (Ctrl+S's command), then RUN: the saved case is reloaded and its map has the new bus and line ----
            Await(r.Get(AuthorCommands.Save).RunAsync());
            Assert.False(author.Authoring.IsDirty, author.Status);
            Assert.True(Toggle(w));
            Await(w.AuthorMode.PendingReload!);
            Assert.False(ed.IsActive);
            Assert.False(map.EditMode);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            GridModel reloaded = vm.Run.LoadedModel!;
            Bus newBus = reloaded.Buses.Single(b => b.Id == 9001);
            Assert.Equal((dx, dy), (newBus.X, newBus.Y));
            Assert.Contains(reloaded.Branches, b => b.From == from && b.To == 9001 && !b.IsTransformer);
            Assert.True(map.View.IdToIndex.ContainsKey(9001));
            Assert.Contains(map.View.Edges, e => e.Valid && map.View.Nodes[e.A].Id == from && map.View.Nodes[e.B].Id == 9001);
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, Menus(w));

            // Run mode map: a click selects the bus for the fly-out, as before.
            PumpUntil(() => vm.Run.LastFrame?.Model.Buses.Any(b => b.Id == 9001) == true, "a frame of the reloaded case");
            ShowNetwork(w);
            Render(w);
            Click(w, BusPoint(map, 9001));
            Assert.Equal(new MapSelection(MapSelectionKind.Bus, 9001), vm.Map.Selection);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Author_map_on_a_v2_case_place_connect_a_transformer_drag_save_then_RUN_reload_shows_them()
    {
        using var temp = new TempFolder();
        string manifest = WriteV2Case(temp);
        var prompts = new ScriptedPrompts();
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        MainWindow w = OpenSettled(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            Assert.True(Toggle(w));
            AssetAuthorModel author = vm.Tabs.Assets.Author!;
            MapAuthorController ed = w.MapAuthor;
            SchematicControl map = ShowNetwork(w);
            Assert.False(map.View.HasState);

            // Place a 132 kV bus.
            Press(w, Key.P);
            Point empty = EmptyPoint(map);
            prompts.Fields.Enqueue(Values(("id", "9001"), ("baseKv", "132")));
            Click(w, empty);
            PumpUntil(() => ed.Map!.Bus(9001) is not null, "the placed bus");
            Assert.Equal(1, author.Authoring.UndoDepth);

            // Connect a 400 kV bus to it with a transformer: the kind, then name, rPu and xPu are asked (v2 requires them).
            int from = ClearBus(map, ed.Map!, 400);
            Press(w, Key.L);
            Click(w, BusPoint(map, from));
            prompts.Fields.Enqueue(Values(("kind", "transformer")));
            prompts.Fields.Enqueue(Values(("name", "T-map"), ("rPu", "0.002"), ("xPu", "0.06")));
            int branches = ed.Map!.Branches.Count;
            Click(w, BusPoint(map, 9001));
            PumpUntil(() => ed.Map!.Branches.Count == branches + 1, "the new transformer");
            Assert.Equal("fields: Connect: transformer: name,rPu,xPu", prompts.Asked[^1]);
            Assert.Equal(2, author.Authoring.UndoDepth);
            CaseRef txRec = author.Selected!.Record!;
            Assert.Equal("{\"name\":\"T-map\",\"from\":" + from + ",\"to\":9001,\"rPu\":0.002,\"xPu\":0.06}", RecordJson(author.Authoring, txRec).ToJsonString());
            Assert.True(ed.Map!.Branches[ed.Map.BranchIndexOf(txRec)!.Value].IsTransformer);

            // A refused Connect (a cancelled kind prompt) changes nothing.
            Click(w, BusPoint(map, from));
            prompts.Fields.Enqueue(null);
            Click(w, BusPoint(map, 9001));
            PumpUntil(() => ed.ConnectFrom is null, "the cancelled connect");
            Assert.Equal("Connect cancelled; nothing changed.", vm.StatusText);
            Assert.Equal(2, author.Authoring.UndoDepth);

            // Drag the new bus.
            Press(w, Key.Escape);
            Point dropAt = EmptyPoint(map, avoid: BusPoint(map, 9001));
            ScreenPoint dropWorld = map.View.ScreenToWorld(new ScreenPoint(dropAt.X, dropAt.Y));
            (double dx, double dy) = MapEditing.CaseCoordinates(dropWorld.X, dropWorld.Y);
            DragOnMap(w, BusPoint(map, 9001), dropAt);
            PumpUntil(() => author.Authoring.UndoDepth == 3, "the move");

            // Save, RUN: the reloaded case has the bus where it was dropped and the transformer by its name.
            Await(vm.Registry.Get(AuthorCommands.Save).RunAsync());
            Assert.False(author.Authoring.IsDirty, author.Status);
            Assert.True(Toggle(w));
            Await(w.AuthorMode.PendingReload!);
            Assert.False(map.EditMode);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            GridModel reloaded = vm.Run.LoadedModel!;
            Assert.Equal((dx, dy), (reloaded.Buses.Single(b => b.Id == 9001).X, reloaded.Buses.Single(b => b.Id == 9001).Y));
            Branch tx = reloaded.Branches.Single(b => b.Name == "T-map");
            Assert.Equal((from, 9001, true), (tx.From, tx.To, tx.IsTransformer));
            Assert.True(map.View.IdToIndex.ContainsKey(9001));
            Assert.Contains(map.View.Edges, e => e.Name == "T-map" && e.IsTransformer && e.Valid);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Leaving_AUTHOR_unchanged_gives_the_run_its_own_map_back_and_the_Run_map_behaves_as_before()
    {
        using var temp = new TempFolder();
        string dir = CopyV1Case(temp);
        var prompts = new ScriptedPrompts();
        var source = new ModelSource("uk copy", dir);
        MainWindow w = OpenSettled(temp, source, prompts);
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            SchematicControl map = ShowNetwork(w);
            List<(int Id, double Wx, double Wy)> runNodes = map.View.Nodes.Select(n => (n.Id, n.Wx, n.Wy)).ToList();
            double[] runVm = map.View.VmByNode!.ToArray();
            int runEdges = map.View.Edges.Count;
            (double zoom, double ox, double oy) = (map.View.Zoom, map.View.OffsetX, map.View.OffsetY);

            Assert.True(Toggle(w));
            Assert.True(map.EditMode);
            Assert.False(map.View.HasState);
            // A placed bus, then Discard on the way out: nothing is written and the run carries on with its own map.
            map = ShowNetwork(w);
            Press(w, Key.P);
            prompts.Fields.Enqueue(Values(("id", "9001"), ("baseKv", "132")));
            Click(w, EmptyPoint(map));
            PumpUntil(() => w.MapAuthor.Map!.Bus(9001) is not null, "the placed bus");
            prompts.Dirty.Enqueue(DirtyChoice.Discard);
            Assert.True(Toggle(w));
            Assert.Null(w.AuthorMode.PendingReload);   // discarded: nothing saved, no reload
            Assert.False(w.MapAuthor.IsActive);
            Assert.False(map.EditMode);
            Assert.Null(map.EditSelectedBus);

            // The run's own map, exactly: the same nodes where they were, the same last state, the same view.
            Assert.Equal(runNodes, map.View.Nodes.Select(n => (n.Id, n.Wx, n.Wy)));
            Assert.Equal(runEdges, map.View.Edges.Count);
            Assert.True(map.View.HasState);
            Assert.Equal(runVm, map.View.VmByNode!);
            Assert.Equal((zoom, ox, oy), (map.View.Zoom, map.View.OffsetX, map.View.OffsetY));
            Assert.False(File.ReadAllText(Path.Combine(dir, "buses.json")).Contains("9001", StringComparison.Ordinal));

            // Run mode keys and clicks are the Run map's: P does nothing to the map, a click on a bus opens its fly-out.
            ShowNetwork(w);
            Press(w, Key.P);
            Assert.Equal(MapEditTool.Select, w.MapAuthor.Tool);
            int bus = map.View.Nodes[0].Id;
            foreach (SchematicNode n in map.View.Nodes)
            {
                Point s = P(map.View.NodeScreen(map.View.IdToIndex[n.Id]));
                if (s.X > 40 && s.Y > 60 && s.X < map.Bounds.Width - 320 && !map.MinimapRect.Inflate(30).Contains(s)
                    && map.View.HitTestNode(new ScreenPoint(s.X, s.Y)) == n.Id) { bus = n.Id; break; }
            }
            Click(w, BusPoint(map, bus));
            Assert.Equal(new MapSelection(MapSelectionKind.Bus, bus), vm.Map.Selection);
            Assert.True(vm.Dashboard.SelectionVisible);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void In_AUTHOR_a_double_click_on_a_bus_drills_in_onto_it_as_in_Run_and_moves_nothing()
    {
        using var temp = new TempFolder();
        string dir = CopyV1Case(temp);
        MainWindow w = OpenSettled(temp, new ModelSource("uk copy", dir), new ScriptedPrompts());
        try
        {
            Assert.True(Toggle(w));
            SchematicControl map = ShowNetwork(w);
            Assert.True(map.EditMode);
            AssetAuthorModel author = w.ViewModel.Tabs.Assets.Author!;
            byte[] buses = File.ReadAllBytes(Path.Combine(dir, "buses.json"));

            // A bus clear of the edges, the minimap and the fly-out corner, that the hit test names.
            int bus = -1;
            foreach (SchematicNode n in map.View.Nodes)
            {
                Point s = P(map.View.NodeScreen(map.View.IdToIndex[n.Id]));
                if (s.X > 40 && s.Y > 60 && s.X < map.Bounds.Width - 320 && s.Y < map.Bounds.Height - 60 && !map.MinimapRect.Inflate(30).Contains(s)
                    && map.View.HitTestNode(new ScreenPoint(s.X, s.Y)) == n.Id) { bus = n.Id; break; }
            }
            Assert.NotEqual(-1, bus);
            SchematicNode node = map.View.Nodes[map.View.IdToIndex[bus]];

            // Two quick clicks at the same point: the first selects the bus, the second drills in onto it.
            Point at = ToWindow(w, BusPoint(map, bus));
            w.MouseMove(at);
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            Render(w);

            // Exactly SchematicView.CenterOn(bus, FocusZoom): the zoom at substation level, the bus at the centre.
            double zoom = Math.Clamp(map.View.FocusZoom, SchematicView.MinZoom, map.View.MaxZoom);
            Assert.Equal(zoom, map.View.Zoom);
            Assert.Equal(map.View.ViewportWidth / 2 - node.Wx * zoom, map.View.OffsetX);
            Assert.Equal(map.View.ViewportHeight / 2 - node.Wy * zoom, map.View.OffsetY);
            Assert.Equal(bus, map.EditSelectedBus);

            // Nothing was picked up or moved: no undo step, the case unchanged.
            Assert.Equal(0, author.Authoring.UndoDepth);
            Assert.False(author.Authoring.IsDirty);
            Assert.Equal(buses, File.ReadAllBytes(Path.Combine(dir, "buses.json")));
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void In_AUTHOR_the_tiles_around_the_map_are_veiled_as_the_paused_runs_last_values_and_come_back_unchanged()
    {
        using var temp = new TempFolder();
        string dir = CopyV1Case(temp);
        MainWindow w = OpenSettled(temp, new ModelSource("uk copy", dir), new ScriptedPrompts());
        try
        {
            ShowNetwork(w);
            AppDashboardModel dash = w.ViewModel.Dashboard;
            DashboardView view = w.DashboardTab;
            Border left = view.FindControl<Border>("AuthorVeilLeft")!, right = view.FindControl<Border>("AuthorVeilRight")!;
            Assert.False(dash.AuthorPaused);
            Assert.False(left.IsVisible);
            Assert.False(right.IsVisible);
            (string, string, string, string, string) Tiles() => (dash.Freq, dash.Demand, dash.Generation, dash.Losses, dash.Inertia);

            // Entering AUTHOR pauses the run: the tiles keep its last values, veiled and labelled as such.
            Assert.True(Toggle(w));
            Render(w);
            (string, string, string, string, string) paused = Tiles();
            Assert.True(dash.AuthorPaused);
            Assert.True(left.IsVisible);
            Assert.True(right.IsVisible);
            Assert.Equal("AUTHOR  -  the run is paused; these tiles hold its last values, not the case being edited (it is not solved until you return to RUN)",
                view.FindControl<TextBlock>("AuthorVeilText")!.Text);
            Assert.False(left.IsHitTestVisible);   // the veil never takes a click

            // Leaving AUTHOR unchanged: the veil goes and the tiles are exactly the run's values (nothing was cleared).
            Assert.True(Toggle(w));
            Render(w);
            Assert.False(dash.AuthorPaused);
            Assert.False(left.IsVisible);
            Assert.False(right.IsVisible);
            Assert.Equal(paused, Tiles());
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void In_AUTHOR_the_selected_bus_and_Connects_first_bus_are_exempt_from_the_level_of_detail_and_Run_has_none()
    {
        using var temp = new TempFolder();
        string dir = CopyV1Case(temp);
        MainWindow w = OpenSettled(temp, new ModelSource("uk copy", dir), new ScriptedPrompts());
        try
        {
            SchematicControl map = ShowNetwork(w);
            Assert.Empty(map.View.LodExemptBuses);   // Run mode never sets any
            Assert.True(Toggle(w));
            map = ShowNetwork(w);
            int a = map.View.Nodes[0].Id, b = map.View.Nodes[1].Id;

            map.EditSelectedBus = a;
            Assert.Equal(new[] { a }, map.View.LodExemptBuses);
            map.EditConnectFrom = b;
            Assert.Equal(new[] { a, b }, map.View.LodExemptBuses.OrderBy(x => x == a ? 0 : 1));
            map.EditSelectedBus = null;
            Assert.Equal(new[] { b }, map.View.LodExemptBuses);
            map.EditConnectFrom = null;
            Assert.Empty(map.View.LodExemptBuses);

            // Leaving AUTHOR clears the marks and with them every exemption.
            map.EditSelectedBus = a;
            Assert.Single(map.View.LodExemptBuses);
            Assert.True(Toggle(w));
            Render(w);
            Assert.False(map.EditMode);
            Assert.Empty(map.View.LodExemptBuses);
        }
        finally { Close(w); }
    }
}
