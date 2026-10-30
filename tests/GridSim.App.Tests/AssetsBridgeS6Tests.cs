using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Presentation;
using GridSim.Ui.Assets.Bridge;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Scada;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS fix S6, against the fake loopback GDA bridge (FakeBridgeServer, as BridgeTests and
/// AssetsBridgeTests): in bridge mode the substation page's GDA PER-ASSET DATA card is asked for and filled on EVERY route
/// to a substation page (OPEN IN EXPLORER, the tree, a search match, "go to bus id", back / forward, a SCADA row, the
/// map's "Open in Assets", a unit page's bus link, a CONNECTIONS link), each with the same query (op="asset" for the bus at
/// the stream anchor) and the same card (the WPF status line and the shared formatter's rows) the OPEN IN EXPLORER route
/// gave before the fix; a page shown again (back / forward) keeps its card and is not asked twice. Outside bridge mode the
/// card stays hidden and nothing is asked, on every route.
/// </summary>
public sealed class AssetsBridgeS6Tests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 20_000)
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

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // The bridge's answer to one asset query: the AssetsBridgeTests payload, for the bus asked about.
    private static string Result(string id, int busId) => $$"""
        {
          "type": "result", "op": "asset", "id": "{{id}}", "at": "2026-07-01T00:00:00Z",
          "payload": {
            "bmUnit": "T_TEST-{{busId}}", "busId": {{busId}},
            "envelope": [ { "msg": 120.5, "pmax": 660 } ],
            "bessSoc": [], "tapWear": [ { "operations": 1234 } ], "consumers": []
          }
        }
        """;

    private sealed class Session : IDisposable
    {
        public required FakeBridgeServer Server { get; init; }
        public required TempFolder Temp { get; init; }
        public required MainWindow Window { get; init; }
        public required FakeBridgeConnection Connection { get; init; }

        public MainWindowViewModel Vm => Window.ViewModel;
        public AssetNavigatorModel Nav => Vm.Tabs.Assets;
        public BridgeSession Bridge => Vm.Run.Bridge!;

        public int Queries => Connection.History.Count(m => (string?)m["action"] == "query");

        public void Dispose()
        {
            try
            {
                if (Vm.Run.Bridge is { IsActive: true } b) Await(b.DisposeBridgeAsync());
                Await(Vm.Run.StopLoopAsync());
                Window.Close();
                Pump();
            }
            finally
            {
                Temp.Dispose();
                Server.Dispose();
            }
        }
    }

    // The window, streaming the fake bridge's toy topology with one live frame in the tabs.
    private static Session Open()
    {
        var server = new FakeBridgeServer();
        var temp = new TempFolder();
        var store = ShellSettingsStore.ForFile(temp.File("settings.json"));
        GridSim.Ui.Settings.AppSettings s = store.Load();
        s.BridgeEndpoint = server.Endpoint;
        s.BridgeAllowUnencrypted = true;
        s.Save();
        var source = new ModelSource("Live bridge", "", Bridge: server.Endpoint);
        var w = new MainWindow(new ShellOptions
        {
            SettingsStore = store,
            DiscoverCases = (_, _) => new List<ModelSource> { source },
            LicenceDescription = () => "Test licence",
            Licence = () => new LicenceGate(Entitlement.FreeDefault),
            UseNativeMenu = false,
            AutoLoadAtStart = false,
        });
        w.Show();
        MainWindowViewModel vm = w.ViewModel;
        Task load = vm.LoadCaseAsync(source);
        FakeBridgeConnection conn = server.NextConnection(Pump);
        conn.Next("start", Pump);
        Await(load);
        Await(conn.SendAsync(BridgeTests.TopologyJson));
        PumpUntil(() => vm.Run.LoadedModel is not null, "the topology");
        Await(conn.SendAsync(BridgeTests.StateJson(BridgeTests.TickIso(1), 1)));
        PumpUntil(() => vm.Run.LastFrame?.Tick == 1 && vm.Tabs.Assets.Model is not null, "the first live frame in the tabs");
        Assert.True(vm.Run.Bridge!.IsActive);
        w.SelectTab(MainWindow.AssetsTabHeader);
        Pump();
        return new Session { Server = server, Temp = temp, Window = w, Connection = conn };
    }

    // The route just taken showed a substation page of bus busId and asked for it: the page says "querying ..." until the
    // answer, then shows exactly what the OPEN IN EXPLORER route showed before the fix (the status line and the shared
    // formatter's rows). Returns the page.
    private static SubstationDetailPage AnswerAndCheck(Session x, int busId, string route)
    {
        var page = Assert.IsType<SubstationDetailPage>(x.Nav.Current);
        Assert.Equal(busId, page.BusId);
        Assert.True(page.AssetVisible, route + ": the card is not shown while the query is under way");
        Assert.Equal("querying GDA per-asset data ...", page.AssetStatus);
        JsonObject q = x.Connection.Next("query", Pump);
        Assert.Equal("asset", (string?)q["op"]);
        Assert.Equal(busId, (int)q["params"]!["busId"]!);
        Assert.Equal(x.Bridge.QueryAt, BridgeProtocol.ParseUtc((string?)q["at"]));
        string result = Result((string)q["id"]!, busId);
        Await(x.Connection.SendAsync(result));
        Await(x.Window.PendingAssetQuery!);
        AssertFilled(x, page, busId, result, route);
        return page;
    }

    private static void AssertFilled(Session x, SubstationDetailPage page, int busId, string result, string route)
    {
        AssetData asset = BridgeProtocol.ReadResult(result).ToQueryResult().Asset!;
        var rows = AssetInsightFormatter.Rows(asset).ToList();
        Assert.NotEmpty(rows);
        Assert.True(page.AssetVisible, route + ": the card is hidden");
        Assert.Equal($"GDA @ {x.Bridge.QueryAt:yyyy-MM-dd HH:mm}Z  *  bus {busId}", page.AssetStatus);
        Assert.Equal(rows.Select(r => (r.Group, r.Head, r.Sub)), page.AssetInsights.Select(r => (r.Group, r.Head, r.Sub)));
    }

    // Leave the substation pages (the GENERATORS class page), so the next route opens a new page.
    private static void Away(Session x)
    {
        x.Nav.Select(x.Nav.Roots[2]);
        Pump();
        Assert.IsNotType<SubstationDetailPage>(x.Nav.Current);
    }

    [AvaloniaFact]
    public void In_bridge_mode_every_route_to_a_substation_page_fills_the_GDA_card_with_the_payload_the_old_route_gave()
    {
        using Session x = Open();
        AssetNavigatorModel nav = x.Nav;
        int asked = 0;
        void NewQuery(string route)
        {
            asked++;
            Assert.True(x.Vm.AssetInsights.QueryCount == asked, $"{route}: {x.Vm.AssetInsights.QueryCount} queries started, expected {asked}");
        }

        // ---- 1. the map fly-out's OPEN IN EXPLORER on bus 3 (the route that filled the card before the fix) ----
        x.Vm.Map.SelectBus(3);
        Pump();
        x.Window.OpenInExplorer();
        Pump();
        Assert.Same(nav.Tree.Substation(3), nav.Selected);
        NewQuery("OPEN IN EXPLORER");
        SubstationDetailPage first = AnswerAndCheck(x, 3, "OPEN IN EXPLORER");

        // ---- 2. the explorer tree ----
        Away(x);
        nav.Select(nav.Tree.Substation(3)!);
        Pump();
        NewQuery("tree");
        SubstationDetailPage tree = AnswerAndCheck(x, 3, "tree");
        Assert.NotSame(first, tree);

        // ---- 3. a search match ----
        Away(x);
        nav.SearchText = "Load132";
        AssetTreeNode hit = nav.SearchHits.First(n => n.Key == AssetKey.Substation(3));
        nav.Select(hit);
        nav.SearchText = "";
        Pump();
        NewQuery("search match");
        SubstationDetailPage searched = AnswerAndCheck(x, 3, "search match");

        // ---- 4. "go to bus id" (the search box's Enter on a number) ----
        Away(x);
        nav.SearchText = "3";
        Assert.True(nav.SubmitSearch());
        Pump();
        NewQuery("go to bus id");
        SubstationDetailPage goneTo = AnswerAndCheck(x, 3, "go to bus id");

        // ---- 5. back / forward: the page shown again keeps its card (same payload), and is not asked twice ----
        string answered = Result("unused", 3);
        Assert.True(nav.GoBack());          // the GENERATORS page
        Assert.True(nav.GoBack());          // the search match's page
        Pump();
        Assert.Same(searched, nav.Current);
        AssertFilled(x, searched, 3, answered, "back");
        Assert.True(nav.GoForward());
        Assert.True(nav.GoForward());
        Pump();
        Assert.Same(goneTo, nav.Current);
        AssertFilled(x, goneTo, 3, answered, "forward");
        Assert.Equal(asked, x.Vm.AssetInsights.QueryCount);
        Assert.Equal(asked, x.Queries);

        // ---- 6. a SCADA row (a bus-voltage point) ----
        Away(x);
        // The register's own row (it carries voltage points for the EHV buses, >= 275 kV: buses 1 and 2 of the toy
        // topology), clicked as a user clicks it (ScadaModel.OpenPoint hands it to MainWindow.OnScadaNavigate).
        ScadaModel scada = x.Vm.Tabs.Scada;
        ScadaPointModel volt = scada.Points.First(p => p.Kind == PointKind.BusVoltage);
        int voltBus = x.Vm.Run.LastFrame!.Result.BusIds[volt.Index];
        scada.OpenPoint(volt);
        Pump();
        Assert.Same(nav.Tree.Substation(voltBus), nav.Selected);
        NewQuery("SCADA row");
        AnswerAndCheck(x, voltBus, "SCADA row");

        // ---- 7. the map's "Open in Assets" (the registry command, on the bus the context menu was opened on) ----
        Away(x);
        x.Window.MapActions.ContextBusId = 3;
        Await(x.Vm.Registry.Get(AssetCommands.OpenInAssets).RunAsync());
        Pump();
        Assert.Same(nav.Tree.Substation(3), nav.Selected);
        NewQuery("Open in Assets");
        AnswerAndCheck(x, 3, "Open in Assets");

        // ---- 8. a cross-link inside a page: the generator page's bus link (UnitA, on bus 2) ----
        AssetTreeNode unit = nav.Tree.PreOrder().Single(n => n.Key.Kind == AssetKeyKind.Generator && n.Label == "UnitA");
        nav.Select(unit);
        Pump();
        var gen = Assert.IsType<GeneratorDetailModel>(nav.Current);
        Assert.Equal(asked, x.Vm.AssetInsights.QueryCount);   // a generator page asks nothing
        gen.OpenBusSubstation(2);
        Pump();
        Assert.Same(nav.Tree.Substation(2), nav.Selected);
        NewQuery("generator bus link");
        AnswerAndCheck(x, 2, "generator bus link");

        // ---- 9. a CONNECTIONS link: the transformer's to-bus ----
        AssetTreeNode tx = nav.Tree.PreOrder().Single(n => n.Key.Kind == AssetKeyKind.Transformer);
        nav.Select(tx);
        Pump();
        ConnectionRow toBus = nav.Connections.Model.Single(r => r.Relation == "to bus");
        Assert.Equal(AssetKey.Substation(3), toBus.Target);
        Assert.True(nav.OpenConnection(toBus));
        Pump();
        NewQuery("CONNECTIONS link");
        AnswerAndCheck(x, 3, "CONNECTIONS link");

        // Exactly one query per new substation page, nothing else asked.
        Assert.Equal(8, asked);
        Assert.Equal(asked, x.Queries);
    }

    [AvaloniaFact]
    public void Outside_bridge_mode_no_route_asks_anything_and_the_card_stays_hidden()
    {
        using var temp = new TempFolder();
        var source = new ModelSource("uk", Path.Combine(RepoRoot(), "data", "uk"));
        var w = new MainWindow(new ShellOptions
        {
            SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
            DiscoverCases = (_, _) => new List<ModelSource> { source },
            LicenceDescription = () => "Test licence",
            Licence = () => new LicenceGate(Entitlement.FreeDefault),
            UseNativeMenu = false,
            AutoLoadAtStart = false,
            LoadGeography = _ => null,
        });
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            RunViewModel run = vm.Run;
            int frames = 0;
            run.FrameShown += (_, _) => frames++;
            Await(vm.LoadCaseAsync(source));
            PumpUntil(() => frames >= 1, "a frame", 120_000);
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            w.SelectTab(MainWindow.AssetsTabHeader);
            Pump();
            AssetNavigatorModel nav = vm.Tabs.Assets;
            Assert.False(run.Bridge is { IsActive: true });

            int bus = nav.Tree.Substation(1) is not null ? 1 : run.LastFrame!.Result.BusIds[0];
            nav.Select(nav.Tree.Substation(bus)!);
            Pump();
            Assert.False(Assert.IsType<SubstationDetailPage>(nav.Current).AssetVisible);
            vm.Map.SelectBus(bus);
            Pump();
            w.OpenInExplorer();
            Pump();
            Assert.False(Assert.IsType<SubstationDetailPage>(nav.Current).AssetVisible);
            nav.Select(nav.Roots[2]);
            w.MapActions.ContextBusId = bus;
            Await(vm.Registry.Get(AssetCommands.OpenInAssets).RunAsync());
            Pump();
            Assert.False(Assert.IsType<SubstationDetailPage>(nav.Current).AssetVisible);
            Assert.True(nav.GoBack());
            Assert.True(nav.GoForward());
            Assert.False(Assert.IsType<SubstationDetailPage>(nav.Current).AssetVisible);
            Assert.Equal(0, vm.AssetInsights.QueryCount);
            Assert.Null(vm.AssetInsights.Pending);
            Assert.Null(w.PendingAssetQuery);
            Assert.False(vm.AssetInsights.Ask());

            // A head without the bridge (the mobile heads): the source is off by construction.
            using var headless = new AssetInsightSource(nav, () => null);
            nav.Select(nav.Roots[2]);
            nav.Select(nav.Tree.Substation(bus)!);
            Assert.Equal(0, headless.QueryCount);
            Assert.False(headless.Ask());
            Assert.False(Assert.IsType<SubstationDetailPage>(nav.Current).AssetVisible);
        }
        finally
        {
            w.Close();
            if (w.StopLoop is { } stop) Await(stop);
        }
    }
}
