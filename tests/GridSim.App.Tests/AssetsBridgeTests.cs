using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Shell;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Presentation;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme ASSETS A3: the substation page's GDA PER-ASSET DATA card in bridge mode, against a fake GDA bridge
/// (FakeBridgeServer, as BridgeTests). The map fly-out's OPEN IN EXPLORER on a bus asks the bridge for <c>op="asset"</c>
/// at the stream anchor (the WPF BridgeApplyAsset: "querying ..." first, then the rows of the shared formatter and the
/// WPF status line). Since UI programme ASSETS fix S6 the same page reached by the tree is asked for and filled too, with
/// the same query and the same card (every route: AssetsBridgeS6Tests).
/// </summary>
public sealed class AssetsBridgeTests
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

    [AvaloniaFact]
    public void OPEN_IN_EXPLORER_on_a_bus_in_bridge_mode_fills_the_GDA_card_and_since_S6_the_tree_route_does_too()
    {
        using var server = new FakeBridgeServer();
        using var temp = new TempFolder();
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
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            AssetNavigatorModel nav = vm.Tabs.Assets;
            Task load = vm.LoadCaseAsync(source);
            FakeBridgeConnection conn = server.NextConnection(Pump);
            conn.Next("start", Pump);
            Await(load);
            Await(conn.SendAsync(BridgeTests.TopologyJson));
            PumpUntil(() => vm.Run.LoadedModel is not null, "the topology");
            Await(conn.SendAsync(BridgeTests.StateJson(BridgeTests.TickIso(1), 1)));
            PumpUntil(() => vm.Run.LastFrame?.Tick == 1 && nav.Model is not null, "the first live frame in the tabs");
            BridgeSession bridge = vm.Run.Bridge!;
            Assert.True(bridge.IsActive);

            // The fly-out's OPEN IN EXPLORER on bus 3: the substation page, told the query is under way.
            vm.Map.SelectBus(3);
            Pump();
            w.OpenInExplorer();
            Pump();
            var page = Assert.IsType<SubstationDetailPage>(nav.Current);
            Assert.Same(nav.Tree.Substation(3), nav.Selected);
            Assert.NotNull(w.PendingAssetQuery);
            Assert.True(page.AssetVisible);
            Assert.Equal("querying GDA per-asset data ...", page.AssetStatus);

            // The query the WPF window sends: op="asset" for the bus, at the stream anchor.
            JsonObject q = conn.Next("query", Pump);
            Assert.Equal("asset", (string?)q["op"]);
            Assert.Equal(3, (int)q["params"]!["busId"]!);
            Assert.Equal(bridge.QueryAt, BridgeProtocol.ParseUtc((string?)q["at"]));
            string result = $$"""
            {
              "type": "result", "op": "asset", "id": "{{(string)q["id"]!}}", "at": "2026-07-01T00:00:00Z",
              "payload": {
                "bmUnit": "T_TEST-1", "busId": 3,
                "envelope": [ { "msg": 120.5, "pmax": 660 } ],
                "bessSoc": [], "tapWear": [ { "operations": 1234 } ], "consumers": []
              }
            }
            """;
            Await(conn.SendAsync(result));
            Await(w.PendingAssetQuery!);

            // The WPF status line and the shared formatter's rows (N063), on the page still showing that bus.
            AssetData asset = BridgeProtocol.ReadResult(result).ToQueryResult().Asset!;
            Assert.Equal($"GDA @ {bridge.QueryAt:yyyy-MM-dd HH:mm}Z  *  bus 3", page.AssetStatus);
            var rows = AssetInsightFormatter.Rows(asset).ToList();
            Assert.NotEmpty(rows);
            Assert.Equal(rows.Select(r => (r.Group, r.Head, r.Sub)), page.AssetInsights.Select(r => (r.Group, r.Head, r.Sub)));
            Assert.True(page.AssetVisible);

            // Test retargeted 2026-10-20 (UI programme ASSETS 5-view/S6): this pinned quirk S6 as is (the same substation
            // reached by the tree asked nothing and kept the card hidden). Mark's decision 4 (ASSETS plan section 8) fixes
            // S6: every route to a substation page in bridge mode asks for and fills the card. The assertions are kept in
            // kind and made stronger: the tree route is still a new page (asserted as before), and where the old test
            // asserted that no query was sent and the card stayed hidden, it now asserts exactly ONE more query, the same
            // query as the OPEN IN EXPLORER route (op, bus, anchor), the "querying ..." status while it runs, and the same
            // status line and rows once answered; the OPEN IN EXPLORER page above keeps its card unchanged.
            int before = conn.History.Count(m => (string?)m["action"] == "query");
            nav.Select(nav.Roots[1]);
            nav.Select(nav.Tree.Substation(3)!);
            Pump();
            var other = Assert.IsType<SubstationDetailPage>(nav.Current);
            Assert.NotSame(page, other);
            Assert.True(other.AssetVisible);
            Assert.Equal("querying GDA per-asset data ...", other.AssetStatus);
            JsonObject q2 = conn.Next("query", Pump);
            Assert.Equal(before + 1, conn.History.Count(m => (string?)m["action"] == "query"));
            Assert.Equal("asset", (string?)q2["op"]);
            Assert.Equal(3, (int)q2["params"]!["busId"]!);
            Assert.Equal(bridge.QueryAt, BridgeProtocol.ParseUtc((string?)q2["at"]));
            Await(conn.SendAsync(result.Replace((string)q["id"]!, (string)q2["id"]!, StringComparison.Ordinal)));
            Await(w.PendingAssetQuery!);
            Assert.Equal($"GDA @ {bridge.QueryAt:yyyy-MM-dd HH:mm}Z  *  bus 3", other.AssetStatus);
            Assert.Equal(rows.Select(r => (r.Group, r.Head, r.Sub)), other.AssetInsights.Select(r => (r.Group, r.Head, r.Sub)));
            Assert.True(other.AssetVisible);
            Assert.Equal($"GDA @ {bridge.QueryAt:yyyy-MM-dd HH:mm}Z  *  bus 3", page.AssetStatus);
            Assert.Equal(rows.Select(r => (r.Group, r.Head, r.Sub)), page.AssetInsights.Select(r => (r.Group, r.Head, r.Sub)));
            Thread.Sleep(200);
            Pump();
            Assert.Equal(before + 1, conn.History.Count(m => (string?)m["action"] == "query"));   // nothing more asked
        }
        finally
        {
            if (w.ViewModel.Run.Bridge is { IsActive: true } b) Await(b.DisposeBridgeAsync());
            Await(w.ViewModel.Run.StopLoopAsync());
            w.Close();
            Pump();
        }
    }
}
