using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Dashboard;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.8: the GDA bridge in the Avalonia app, driven end to end against a fake bridge on the loopback
/// interface (<see cref="FakeBridgeServer"/>; no test here reaches any other host). A "Live bridge" source connects
/// through Core's GridBridgeClient with the WPF rules (ws:// only with BridgeAllowUnencrypted, the WPF refusal text
/// otherwise); the topology lays the map out; each streamed frame, its HUD scalars and its OPERATOR / MARKET blocks reach
/// the dashboard and the tabs through the window's one dashboard feed; a what-if query round trip, PAUSE / PLAY, RETURN
/// TO LIVE, a stream restart, a lost stream and the reconnect behave as the WPF window's bridge code does.
/// <para>
/// "As WPF" is checked against a reference: a second dashboard (the same shared model and tabs) is given exactly the calls
/// the WPF window's bridge code makes on its DashboardViewModel for the same messages, in the same order, with frames
/// built straight from the same JSON by Core's BridgeProtocol, and every display value of the window's dashboard and of
/// the bridge-fed tabs must equal the reference's exactly (bit for bit for every number).
/// </para>
/// </summary>
public sealed class BridgeTests
{
    // ---------------------------------------------------------------- the protocol samples (BridgeProtocolTests' toy network)

    /// <summary>The toy three-bus network of tests/GridSim.Tests/BridgeProtocolTests, as a topology message.</summary>
    internal const string TopologyJson = """
    {
      "type": "topology",
      "model": {
        "name": "toy3",
        "baseMva": 100,
        "buses": [
          { "id": 1, "type": "Slack", "baseKv": 400, "name": "Slack400" },
          { "id": 2, "type": "Pv",    "baseKv": 400, "name": "Gen400" },
          { "id": 3, "type": "Pq",    "baseKv": 132, "pd": 80, "qd": 20, "name": "Load132" }
        ],
        "generators": [
          { "bus": 1, "pg": 0,   "name": "SlackGen" },
          { "bus": 2, "pg": 120, "vg": 1.02, "mBase": 200, "h": 4.0, "name": "UnitA", "type": "CCGT" }
        ],
        "branches": [
          { "from": 1, "to": 2, "r": 0.004, "x": 0.04, "b": 0.02, "rateAMva": 500, "name": "L1-2" },
          { "from": 2, "to": 3, "r": 0.006, "x": 0.08, "tap": 1.0, "rateAMva": 250, "name": "T2-3", "isTransformer": true }
        ]
      }
    }
    """;

    /// <summary>A tick's historical instant: 193 h ago plus <paramref name="tick"/> half-hours, safely behind the horizon.</summary>
    internal static string TickIso(int tick) =>
        DateTime.UtcNow.AddHours(-(ForwardInferenceGuard.HorizonHours + 24)).AddMinutes(30 * tick).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// One state message: the BridgeProtocolTests sample, with values that move with <paramref name="k"/> (so each tick is
    /// distinguishable), its scalars, overlays, OPERATOR and MARKET blocks.
    /// </summary>
    internal static string StateJson(string iso, int k)
    {
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return $$"""
        {
          "type": "state",
          "t": "{{iso}}",
          "result": {
            "totalLossMw": {{F(1.5 + 0.1 * k)}},
            "buses": [
              { "id": 1, "vm": 1.010, "vaDeg": 0.0,   "pInjMw": {{F(40.0 + k)}},  "qInjMvar": 12.0 },
              { "id": 2, "vm": 1.020, "vaDeg": -1.5,  "pInjMw": 120.0, "qInjMvar": 8.0 },
              { "id": 3, "vm": {{F(0.978 - 0.001 * k)}}, "vaDeg": -6.2,  "pInjMw": {{F(-80.0 - k)}}, "qInjMvar": -20.0 }
            ],
            "lineFlows": [
              { "from": 1, "to": 2, "pFromMw": {{F(40.0 + k)}}, "pToMw": -39.8, "loadingPercent": 8.0 },
              { "from": 2, "to": 3, "pFromMw": 80.2, "pToMw": {{F(-80.0 - k)}}, "loadingPercent": {{F(32.0 + k)}}, "name": "T2-3", "ratingMva": 250 }
            ]
          },
          "scalars": {
            "freqHz": {{F(49.98 + 0.001 * k)}}, "rocofHzPerS": -0.03, "genMw": 160.0, "demandMw": 80.0,
            "inertiaMwS": {{F(800.0 + 10 * k)}}, "conditionIndex": {{F(70.0 + 2.0 * k)}}, "rocofHeadroomHzS": 0.09
          },
          "overlays": { "stressByBranch": { "T2-3": 0.64 }, "conditionByBus": { "3": 0.42 } },
          "operator": {
            "largestLossMw": 1800.0, "reserveMw": {{F(30547.6 + k)}}, "deratedMarginMw": 15476.3, "netInterconnectorMw": 3853.0,
            "synchronousInertiaGvaS": {{F(131.4 + k)}}, "outturnInertiaGvaS": 129.0, "marketInertiaGvaS": 86.0,
            "warning": "NESO excess-price notification",
            "balancingActions": [ { "bmUnit": "T_DRAXX-4", "kind": "OFFER", "levelMw": 645.0, "fuelType": "BIOMASS", "synchronous": true } ],
            "constraints": [ { "name": "SCOTEX", "costGbp": 183842.0, "binding": true } ],
            "warnings": [ "NESO excess-price notification" ]
          },
          "market": {
            "imbalancePriceGbpMwh": {{F(48.16 + k)}}, "marketIndexGbpMwh": 48.16, "bsuosGbpMwh": 13.74,
            "constraintCostGbp": 371155.0, "carbonIntensityGco2PerKwh": 169.0, "demandForecastMw": 22863.0,
            "interconnectors": [ { "name": "IFA", "flowMw": 1503.0 }, { "name": "Moyle", "flowMw": -451.0 } ]
          }
        }
        """;
    }

    /// <summary>A query result answering request <paramref name="id"/>; with a converged case9 solve when <paramref name="withSolve"/>.</summary>
    internal static string ResultJson(string id, string op, string verdict, bool withSolve)
    {
        string solve = "";
        if (withSolve)
        {
            var trace = new SolveTrace();
            PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = trace });
            var meta = new SolveExportMeta("case9", "cpu", "deadbeef", "2026-07-01T00:00:00Z", 1.0, "test");
            solve = ", \"solveExport\": " + SolveExport.ToJson(IeeeCases.Case9(), solved, trace, meta);
        }
        return $$"""
        {
          "type": "result", "op": "{{op}}", "id": "{{id}}", "at": "2026-07-01T00:00:00Z",
          "payload": {
            "verdict": "{{verdict}}", "bindingConstraint": "B6 Scotland-England",
            "violations": [ { "kind": "branch_overload", "element": "L14-15", "value": 112.0, "limit": 100.0, "message": "over rating" } ]
            {{solve}}
          }
        }
        """;
    }

    // ---------------------------------------------------------------- pumping

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

    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        return task.GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------- the window under test and its reference

    /// <summary>
    /// A window over a temporary settings file (BridgeAllowUnencrypted as given), whose catalogue is the fake bridge
    /// source and case9, plus a reference dashboard made the same way as the window's. Disposal stops everything.
    /// </summary>
    private sealed class BridgeRig : IDisposable
    {
        public BridgeRig(FakeBridgeServer server, bool allowUnencrypted = true)
        {
            Temp = new TempFolder();
            Server = server;
            var store = ShellSettingsStore.ForFile(Temp.File("settings.json"));
            GridSim.Ui.Settings.AppSettings s = store.Load();
            s.BridgeEndpoint = server.Endpoint;
            s.BridgeAllowUnencrypted = allowUnencrypted;
            s.Save();
            Source = new ModelSource("Live bridge", "", Bridge: server.Endpoint);
            Case9 = RepoCase("case9");
            Window = new MainWindow(new ShellOptions
            {
                SettingsStore = store,
                DiscoverCases = (_, _) => new List<ModelSource> { Source, Case9 },
                LicenceDescription = () => "Test licence",
                Licence = () => new LicenceGate(Entitlement.FreeDefault),
                UseNativeMenu = false,
                AutoLoadAtStart = false,
            });
            Window.Show();
            RefTabs = new TabModels();
            // Test retargeted 2026-10-20 (UI programme ASSETS A3): the window now wires the SCADA register's row click to
            // the ASSETS tab (ScadaModel.Navigate = MainWindow.OnScadaNavigate, as the WPF window wires OnScadaNavigate),
            // so the window's SCADA model carries that hook where the bare reference's was null. The reference is given
            // the same hook, as the WPF window gives its own; the comparison is unchanged (every SCADA property compared
            // exactly; a hook, being behaviour, is compared only for being set on both sides, as before), and stronger:
            // AssertSameAsReference now also checks the window's hook is exactly the one the window wires.
            RefTabs.Scada.Navigate = Window.OnScadaNavigate;
            Reference = new AppDashboardModel(RefTabs);
            // The WPF start-up calls the window's dashboard got: the licence line and the legend.
            Reference.Log("08:00", Run.LicenceText);
            Reference.SetColorLegend(MapColourMode.Loading);
        }

        public TempFolder Temp { get; }
        public FakeBridgeServer Server { get; }
        public ModelSource Source { get; }
        public ModelSource Case9 { get; }
        public MainWindow Window { get; }
        public MainWindowViewModel Vm => Window.ViewModel;
        public RunViewModel Run => Window.ViewModel.Run;
        public BridgeSession Bridge => Window.ViewModel.Run.Bridge!;
        public AppDashboardModel Dash => Window.ViewModel.Dashboard;
        public TabModels RefTabs { get; }
        public AppDashboardModel Reference { get; }
        public FakeBridgeConnection? Connection { get; set; }
        public GridModel RefTopology { get; } = BridgeProtocol.ReadTopology(TopologyJson).ToModel();

        /// <summary>
        /// Connect (as choosing the source does), check the start the client sent, and give the reference the WPF
        /// LoadBridge calls and the "Connected" line.
        /// </summary>
        public JsonObject Connect()
        {
            Task load = Vm.LoadCaseAsync(Source);
            Connection = Server.NextConnection(Pump);
            JsonObject start = Connection.Next("start", Pump);
            Await(load);
            // WPF LoadBridge's dashboard calls, then the "Connected" line once the stream started.
            Reference.MapHint = DashboardModel.MapHintBridge;
            Reference.ClearSelection();
            Reference.ClearQueryResult();
            Reference.ModelChanged();
            Reference.SetBridgeStatus($"CONNECTING  *  {Bridge.CaseName}", BridgeChoices.WarnColour);
            Reference.Log("00:00", BridgeSession.ConnectedText(Server.Endpoint, Bridge.CaseName, Bridge.SourceName, Bridge.QueryAt));
            return start;
        }

        /// <summary>Send the topology and wait until the map has it (the reference: WPF TopologyReceived's ModelChanged).</summary>
        public void SendTopology()
        {
            Await(Connection!.SendAsync(TopologyJson));
            PumpUntil(() => Run.LoadedModel is not null, "the topology on the map");
            Reference.ModelChanged();
        }

        /// <summary>Send one state and wait until every view had it (its frame, then its market blocks).</summary>
        public void SendState(string json, Func<bool> shown)
        {
            Await(Connection!.SendAsync(json));
            PumpUntil(shown, "a streamed state on screen");
            PumpFor(150);   // the market and estimate handlers are posted right behind the frame's
        }

        /// <summary>The reference frame for a state message, as the client builds it (on the reference topology).</summary>
        public SimFrame RefFrame(string json, int tick) => BridgeProtocol.ReadState(json).ToSimFrame(RefTopology, tick);

        /// <summary>The WPF state handler's calls for a live tick that is shown (not paused, no what-if).</summary>
        public void RefLiveTick(string json, int tick, bool firstLive)
        {
            BridgeProtocol.StateMessage m = BridgeProtocol.ReadState(json);
            Reference.UpdateHud(m.Scalars?.InertiaMwS, m.Scalars?.ConditionIndex, m.Scalars?.RocofHeadroomHzS);
            if (firstLive) Reference.SetBridgeStatus($"LIVE  *  streaming {Bridge.CaseName}", BridgeChoices.OkColour);
            RefRender(RefFrame(json, tick));
            Reference.UpdateMarketData(m.Operator, m.Market, m.Timestamp);
        }

        /// <summary>The WPF RenderReplayFrame's dashboard call for one frame.</summary>
        public void RefRender(SimFrame f)
        {
            IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>();
            Reference.Update(f, boundaries);
        }

        public void Dispose()
        {
            try
            {
                if (Run.Bridge is { IsActive: true } b) Await(b.DisposeBridgeAsync());
                Await(Run.StopLoopAsync());
                Window.Close();
                Pump();
            }
            finally
            {
                RefTabs.Dispose();
                Temp.Dispose();
            }
        }
    }

    private static ModelSource RepoCase(string name)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx")))
            {
                string file = Path.Combine(d.FullName, "data", "matpower", "lib", name + ".m");
                Assert.True(File.Exists(file), "missing test case " + file);
                return new ModelSource(name, Path.GetDirectoryName(file)!, file);
            }
        throw new InvalidOperationException("The GridSim checkout was not found above " + AppContext.BaseDirectory);
    }

    // ---------------------------------------------------------------- exact comparison

    /// <summary>
    /// Every display value of the window's dashboard equals the reference's exactly (every public property of the shared
    /// dashboard model, lists row by row), and so do the bridge-fed tabs (OPERATOR, MARKET, NESO RP1, MECHANICAL) and
    /// THE MACHINE, SCADA and HUMAN SCALE, which every frame feeds.
    /// </summary>
    private static void AssertSameAsReference(BridgeRig rig)
    {
        foreach (PropertyInfo p in typeof(DashboardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0)
                AssertSame(p.GetValue(rig.Reference), p.GetValue(rig.Dash), $"Dashboard.{p.Name}", 0);
        TabModels tabs = rig.Vm.Tabs, refTabs = rig.RefTabs;
        AssertSame(refTabs.Operator, tabs.Operator, "OPERATOR", 0);
        AssertSame(refTabs.Market, tabs.Market, "MARKET", 0);
        AssertSame(refTabs.NesoRp1, tabs.NesoRp1, "NESO RP1", 0);
        AssertSame(refTabs.Mechanical, tabs.Mechanical, "MECHANICAL", 0);
        AssertSame(refTabs.Machine, tabs.Machine, "THE MACHINE", 0);
        AssertSame(refTabs.Scada, tabs.Scada, "SCADA", 0);
        Assert.Equal(refTabs.Scada.Navigate, tabs.Scada.Navigate);   // the SCADA row click goes to ASSETS (A3)
        AssertSame(refTabs.HumanScale, tabs.HumanScale, "HUMAN SCALE", 0);
    }

    // Values by Equals (a double by its exact value), lists row by row, nested objects property by property. A model
    // graph (a GridModel, a result) is compared by its public properties too, so a frame of the same JSON matches.
    private static void AssertSame(object? expected, object? actual, string where, int depth)
    {
        if (expected is null || actual is null) { Assert.True(expected is null && actual is null, $"{where}: '{actual}', expected '{expected}'"); return; }
        Type t = expected.GetType();
        Assert.True(t == actual.GetType(), $"{where}: a {actual.GetType().Name}, expected a {t.Name}");
        if (expected is string || t.IsPrimitive || t.IsEnum || expected is UiColour || t.IsValueType)
        {
            Assert.True(Equals(expected, actual), $"{where}: '{actual}', expected '{expected}'");
            return;
        }
        if (expected is Delegate || expected is System.Windows.Input.ICommand) return;   // behaviour, not a shown value
        if (depth > 12) return;   // deep enough: a model graph's back references stop here
        if (expected is IEnumerable e)
        {
            var eItems = e.Cast<object?>().ToList();
            var aItems = ((IEnumerable)actual).Cast<object?>().ToList();
            Assert.True(eItems.Count == aItems.Count, $"{where}: {aItems.Count} rows, expected {eItems.Count}");
            for (int i = 0; i < eItems.Count; i++) AssertSame(eItems[i], aItems[i], $"{where}[{i}]", depth + 1);
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0) AssertSame(p.GetValue(expected), p.GetValue(actual), $"{where}.{p.Name}", depth + 1);
    }

    private static void AssertSameResult(PowerFlowResult expected, PowerFlowResult actual)
    {
        Assert.Equal(expected.BusIds, actual.BusIds);
        Assert.Equal(expected.Vm, actual.Vm);
        Assert.Equal(expected.VaDeg, actual.VaDeg);
        Assert.Equal(expected.PInjMw, actual.PInjMw);
        Assert.Equal(expected.Converged, actual.Converged);
        Assert.Equal(expected.LineFlows.Count, actual.LineFlows.Count);
        for (int i = 0; i < expected.LineFlows.Count; i++)
        {
            Assert.Equal(expected.LineFlows[i].PFromMw, actual.LineFlows[i].PFromMw);
            Assert.Equal(expected.LineFlows[i].PToMw, actual.LineFlows[i].PToMw);
            Assert.Equal(expected.LineFlows[i].LoadingPercent, actual.LineFlows[i].LoadingPercent);
        }
    }

    // ================================================================ connect, topology, frames

    [AvaloniaFact]
    public void A_live_bridge_connects_and_its_frames_reach_the_dashboard_and_tabs_exactly_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        JsonObject start = rig.Connect();

        // The start the WPF window sends: the default zone and source, 2 fps, the anchor 9 days back (the first preset).
        Assert.Equal("zone-nged", (string?)start["case"]);
        Assert.Equal("presolved", (string?)start["source"]);
        Assert.Equal(BridgeChoices.StreamFps, (double)start["fps"]!);
        DateTime at = BridgeProtocol.ParseUtc((string?)start["start"]);
        Assert.Equal(rig.Bridge.QueryAt, at);
        Assert.InRange(DateTime.UtcNow - at, TimeSpan.FromDays(9) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(9) + TimeSpan.FromMinutes(1));

        // The transport bar in bridge mode, as WPF: engine "bridge", PAUSE (the stream plays), no SOLVER / STEP / SPEED,
        // no replay controls, the ZONE / FROM / SRC pickers shown and on their defaults.
        Assert.True(rig.Run.IsBridge);
        Assert.True(rig.Bridge.IsActive);
        Assert.Equal(RunViewModel.BridgeSolverText, rig.Run.ActiveSolverText);
        Assert.Equal(RunViewModel.PauseLabel, rig.Run.PlayPauseLabel);
        Assert.False(rig.Run.CanChooseSolver);
        Assert.False(rig.Run.CanChooseStep);
        Assert.False(rig.Run.CanChooseSpeed);
        Assert.False(rig.Run.IsLive);
        Assert.False(rig.Run.IsReplay);
        Pump();
        foreach (string name in new[] { "BridgeZoneLabel", "BridgeStartLabel", "BridgeSourceLabel" })
            Assert.True(rig.Window.FindControl<TextBlock>(name)!.IsVisible, name);
        Assert.True(rig.Window.FindControl<ComboBox>("BridgeZonePicker")!.IsVisible);
        Assert.Same(BridgeChoices.Zones[0], rig.Window.FindControl<ComboBox>("BridgeZonePicker")!.SelectedItem);
        Assert.Same(BridgeChoices.Starts[0], rig.Window.FindControl<ComboBox>("BridgeStartPicker")!.SelectedItem);
        Assert.Same(BridgeChoices.Sources[0], rig.Window.FindControl<ComboBox>("BridgeSourcePicker")!.SelectedItem);
        Assert.False(rig.Window.FindControl<ComboBox>("StepPicker")!.IsVisible);
        Assert.False(rig.Window.FindControl<ComboBox>("SolverPicker")!.IsEnabled);
        Assert.False(rig.Window.FindControl<Button>("SpeedButton")!.IsEnabled);
        Assert.False(rig.Window.FindControl<Button>("StepOnceButton")!.IsVisible);

        // CONNECTING until the first frame; the bridge hint; the status bar has the WPF "Connected" line.
        Assert.Equal("CONNECTING  *  zone-nged", rig.Dash.BridgeStatus);
        Assert.Equal(BridgeChoices.WarnColour, rig.Dash.BridgeStatusColour);
        Assert.True(rig.Dash.BridgeStatusVisible);
        Assert.Equal(DashboardModel.MapHintBridge, rig.Dash.MapHint);
        Assert.Equal(BridgeSession.ConnectedText(server.Endpoint, "zone-nged", "presolved", rig.Bridge.QueryAt), rig.Vm.StatusText);
        AssertSameAsReference(rig);

        // The topology lays the map out on the streamed network.
        rig.SendTopology();
        Assert.Equal(3, rig.Run.LoadedModel!.BusCount);
        Assert.Equal(3, rig.Window.MapControl.View.Nodes.Count);
        AssertSameAsReference(rig);

        // Two ticks: each shown through the replay path (dashboard, tabs, map), HUD and lake blocks fed.
        string s1 = StateJson(TickIso(1), 1), s2 = StateJson(TickIso(2), 2);
        rig.SendState(s1, () => rig.Run.LastFrame?.Tick == 1);
        rig.RefLiveTick(s1, 1, firstLive: true);
        Assert.Equal("LIVE  *  streaming zone-nged", rig.Dash.BridgeStatus);
        Assert.Equal(BridgeChoices.OkColour, rig.Dash.BridgeStatusColour);
        Assert.True(rig.Dash.HudVisible);
        AssertSameResult(rig.RefFrame(s1, 1).Result, rig.Run.LastFrame!.Result);
        AssertSameAsReference(rig);
        // The tick's condition and stress overlays are on the map (WPF _renderer.SetOverlay), keyed as the wire keys them.
        SchematicView map = rig.Window.MapControl.View;
        Assert.True(map.HasOverlay);
        Assert.Equal(0.42, map.ConditionByNode![map.IdToIndex[3]]);
        Assert.Equal(0.64, map.StressByEdge![map.Edges.FindIndex(e => e.Name == "T2-3")]);

        rig.SendState(s2, () => rig.Run.LastFrame?.Tick == 2);
        rig.RefLiveTick(s2, 2, firstLive: false);
        AssertSameResult(rig.RefFrame(s2, 2).Result, rig.Run.LastFrame!.Result);
        AssertSameAsReference(rig);
        // The views show the dashboard's numbers (the HUD tiles bind the model).
        Assert.Equal(rig.Dash.InertiaGvaS, rig.Window.DashboardTab.FindControl<TextBlock>("HudInertiaText")!.Text);
        Assert.Equal(rig.Dash.BridgeStatus, rig.Window.DashboardTab.FindControl<TextBlock>("BridgeStatusText")!.Text);

        // A local trip is refused on a bridge feed with the WPF words (the server owns the physics).
        Assert.Equal("TRIP is not available on this feed (replay or bridge stream)", rig.Run.TripLargestInfeed());
    }

    // ================================================================ what-if round trip and RETURN TO LIVE

    [AvaloniaFact]
    public void A_what_if_query_round_trip_shows_the_what_if_holds_live_frames_off_and_RETURN_TO_LIVE_resumes()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        string s1 = StateJson(TickIso(1), 1);
        rig.SendState(s1, () => rig.Run.LastFrame?.Tick == 1);
        rig.RefLiveTick(s1, 1, firstLive: true);
        SimFrame live1 = rig.Run.LastFrame!;

        // The bus menu, item for item as WPF builds it for bus 3 (no generator there).
        IReadOnlyList<BridgeMenuItem> menu = rig.Bridge.ContextMenu(3, null);
        Assert.Equal(new[] { "Feasibility: +500 MW here  (Load132)", "Scenario: trip the largest unit in the model (none generating at Load132)" },
            menu.Select(m => m.Header).ToArray());
        Assert.All(menu, m => Assert.True(m.Enabled));

        // Feasibility: the query the WPF window sends, at the stream anchor.
        Task run = menu[0].Run!();
        JsonObject q = rig.Connection!.Next("query", Pump);
        Assert.Equal("feasibility", (string?)q["op"]);
        Assert.Equal(rig.Bridge.QueryAt, BridgeProtocol.ParseUtc((string?)q["at"]));
        Assert.Equal("new_load", (string?)q["params"]!["kind"]);
        Assert.Equal(3, (int)q["params"]!["bus"]!);
        Assert.Equal(500.0, (double)q["params"]!["mw"]!);
        Assert.Equal("QUERY  *  feasibility ...", rig.Dash.BridgeStatus);
        string result = ResultJson((string)q["id"]!, "feasibility", "infeasible", withSolve: true);
        Await(rig.Connection.SendAsync(result));
        Await(run);

        // The reference: WPF RunQuery's calls for a converged what-if.
        QueryResult qr = BridgeProtocol.ReadResult(result).ToQueryResult();
        const string title = "Feasibility  *  +500 MW at Load132";
        rig.Reference.SetBridgeStatus("QUERY  *  feasibility ...", BridgeChoices.WarnColour);
        rig.Reference.Log(rig.Reference.Clock, $"Bridge query: {title} ...");
        SolveSnapshot snap = qr.Solve!;
        var whatIf = new SimFrame(snap.Model, snap.Result, live1.Hour, 0, snap.Model.FrequencyHz,
            snap.Result.PInjMw.Where(p => p > 0).Sum(), snap.Model.Buses.Sum(b => b.Pd), 0.0);
        rig.RefRender(whatIf);
        rig.Reference.Status = $"WHAT-IF  *  {rig.Reference.Status.Trim()}";
        rig.Reference.ShowQueryResult(qr, title, true, false);
        rig.Reference.SetBridgeStatus("WHAT-IF  *  infeasible", BridgeChoices.WarnColour);
        rig.Reference.Log(rig.Reference.Clock, "feasibility -> infeasible  binding B6 Scotland-England  *  1 violation(s)");

        Assert.True(rig.Bridge.IsWhatIf);
        Assert.StartsWith("WHAT-IF  *  ", rig.Dash.Status, StringComparison.Ordinal);
        Assert.Equal(9, rig.Run.LastFrame!.Model.BusCount);   // the what-if state is the current frame (case9)
        AssertSameResult(snap.Result, rig.Run.LastFrame.Result);
        Assert.True(rig.Dash.QueryVisible);
        AssertSameAsReference(rig);

        // A live frame while the what-if is shown: remembered, HUD and lake blocks fed, but not put on the map.
        string s2 = StateJson(TickIso(2), 2);
        rig.SendState(s2, () => rig.Bridge.LastLiveFrame?.Tick == 2);
        BridgeProtocol.StateMessage m2 = BridgeProtocol.ReadState(s2);
        rig.Reference.UpdateHud(m2.Scalars?.InertiaMwS, m2.Scalars?.ConditionIndex, m2.Scalars?.RocofHeadroomHzS);
        rig.Reference.UpdateMarketData(m2.Operator, m2.Market, m2.Timestamp);
        Assert.Equal(9, rig.Run.LastFrame!.Model.BusCount);
        AssertSameAsReference(rig);

        // The menu now ends with the way back; the what-if panel's RETURN TO LIVE button is the same action.
        IReadOnlyList<BridgeMenuItem> withWayBack = rig.Bridge.ContextMenu(3, null);
        Assert.True(withWayBack[^2].IsSeparator);
        Assert.Equal(BridgeSession.ReturnToLiveItem, withWayBack[^1].Header);
        rig.Window.DashboardTab.FindControl<Button>("ReturnToLiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        rig.Reference.ClearQueryResult();
        rig.RefRender(rig.RefFrame(s2, 2));
        rig.Reference.SetBridgeStatus("LIVE  *  streaming zone-nged", BridgeChoices.OkColour);
        Assert.False(rig.Bridge.IsWhatIf);
        Assert.Equal(2, rig.Run.LastFrame!.Tick);
        Assert.Equal(3, rig.Run.LastFrame.Model.BusCount);
        AssertSameAsReference(rig);
    }

    [AvaloniaFact]
    public void A_scenario_trip_by_name_a_scalars_only_answer_and_a_failed_query_behave_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        string s1 = StateJson(TickIso(1), 1);
        rig.SendState(s1, () => rig.Run.LastFrame?.Tick == 1);

        // A bus with a unit names it; a branch trips itself by name (L1-2 is unique, so the item is enabled).
        IReadOnlyList<BridgeMenuItem> bus2 = rig.Bridge.ContextMenu(2, null);
        Assert.Equal("Scenario: trip the largest generator at Gen400", bus2[1].Header);
        IReadOnlyList<BridgeMenuItem> branch = rig.Bridge.ContextMenu(null, 0);
        Assert.Equal("Scenario: trip this  (L1-2)", Assert.Single(branch).Header);
        Assert.True(branch[0].Enabled);
        // The WPF refusals: a name that is a substring of two elements' names ("-": L1-2 and T2-3), and no name at all.
        GridModel toy = rig.Run.LastFrame!.Model;
        Assert.Equal("its name matches 2 elements in this model, and the bridge selects by name, so it could trip a different one",
            BridgeChoices.TripRefusal(toy, "-"));
        Assert.Equal("it has no name, and the bridge selects the element to trip by name", BridgeChoices.TripRefusal(toy, null));
        Assert.Null(BridgeChoices.TripRefusal(toy, "UnitA"));

        // The scenario: the unit by name; a scalars-only answer leaves the stream showing and the pill LIVE.
        Task run = bus2[1].Run!();
        JsonObject q = rig.Connection!.Next("query", Pump);
        Assert.Equal("scenario", (string?)q["op"]);
        Assert.Equal("UnitA", (string?)q["params"]!["trip"]);
        Await(rig.Connection.SendAsync(ResultJson((string)q["id"]!, "scenario", "secure", withSolve: false)));
        Await(run);
        Assert.False(rig.Bridge.IsWhatIf);
        Assert.Equal("LIVE  *  streaming zone-nged", rig.Dash.BridgeStatus);
        Assert.Equal("Scenario  *  trip UnitA (largest generator at Gen400)", rig.Dash.QueryTitle);
        Assert.EndsWith("scenario  *  scalars only (no load-flow overlay)", rig.Dash.QuerySub, StringComparison.Ordinal);
        Assert.Equal(1, rig.Run.LastFrame!.Tick);

        // A reply that answers a different op fails the query at once: QUERY FAILED on the pill, an alarm, a log line;
        // the next live frame puts LIVE back on the pill.
        Task failed = rig.Bridge.ContextMenu(3, null)[0].Run!();
        JsonObject q2 = rig.Connection.Next("query", Pump);
        Await(rig.Connection.SendAsync(ResultJson((string)q2["id"]!, "scenario", "secure", withSolve: false)));
        Await(failed);
        string why = $"Bridge 'result' id '{(string)q2["id"]!}' answers op 'scenario', but that query asked op 'feasibility'; the query is failed.";
        Assert.Equal($"QUERY FAILED  *  feasibility: {why}", rig.Dash.BridgeStatus);
        Assert.Equal(BridgeChoices.BadColour, rig.Dash.BridgeStatusColour);
        Assert.Contains(rig.Dash.Alarms, a => a.Text == $"QUERY FAILED: feasibility: {why}");
        rig.SendState(StateJson(TickIso(2), 2), () => rig.Run.LastFrame?.Tick == 2);
        Assert.Equal("LIVE  *  streaming zone-nged", rig.Dash.BridgeStatus);
    }

    // ================================================================ pause / hold

    [AvaloniaFact]
    public void PAUSE_holds_the_display_while_the_stream_runs_and_PLAY_shows_the_newest_tick_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        string s1 = StateJson(TickIso(1), 1);
        rig.SendState(s1, () => rig.Run.LastFrame?.Tick == 1);
        rig.RefLiveTick(s1, 1, firstLive: true);

        Await(rig.Run.TogglePlayAsync());
        Assert.True(rig.Run.IsPaused);
        Assert.Equal(RunViewModel.PlayLabel, rig.Run.PlayPauseLabel);
        Assert.Equal("PAUSED  *  zone-nged  *  display held, stream still running", rig.Dash.BridgeStatus);
        rig.Reference.SetBridgeStatus("PAUSED  *  zone-nged  *  display held, stream still running", BridgeChoices.WarnColour);

        // Two ticks arrive while held: nothing on screen moves (frame, HUD, lake blocks).
        string s2 = StateJson(TickIso(2), 2), s3 = StateJson(TickIso(3), 3);
        rig.SendState(s2, () => rig.Bridge.LastLiveFrame?.Tick == 2);
        rig.SendState(s3, () => rig.Bridge.LastLiveFrame?.Tick == 3);
        Assert.Equal(1, rig.Run.LastFrame!.Tick);
        AssertSameAsReference(rig);

        // PLAY: the newest held HUD and blocks, then the newest frame, then LIVE (WPF SetBridgePaused(false)).
        Await(rig.Run.TogglePlayAsync());
        BridgeProtocol.StateMessage m3 = BridgeProtocol.ReadState(s3);
        rig.Reference.UpdateHud(m3.Scalars?.InertiaMwS, m3.Scalars?.ConditionIndex, m3.Scalars?.RocofHeadroomHzS);
        rig.Reference.UpdateMarketData(m3.Operator, m3.Market, m3.Timestamp);
        rig.RefRender(rig.RefFrame(s3, 3));
        rig.Reference.SetBridgeStatus("LIVE  *  streaming zone-nged", BridgeChoices.OkColour);
        Assert.False(rig.Run.IsPaused);
        Assert.Equal(RunViewModel.PauseLabel, rig.Run.PlayPauseLabel);
        Assert.Equal(3, rig.Run.LastFrame!.Tick);
        AssertSameAsReference(rig);

        // RETURN TO LIVE also releases a hold (D-DOC-141).
        Await(rig.Run.TogglePlayAsync());
        Assert.True(rig.Run.IsPaused);
        rig.Bridge.ReturnToLive();
        Assert.False(rig.Run.IsPaused);
        Assert.Equal("LIVE  *  streaming zone-nged", rig.Dash.BridgeStatus);
    }

    // ================================================================ restart on a picker change

    [AvaloniaFact]
    public void Changing_the_zone_or_source_restarts_the_stream_on_the_same_socket_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        rig.SendState(StateJson(TickIso(1), 1), () => rig.Run.LastFrame?.Tick == 1);

        // The ZONE picker (the window's own control) restarts the stream: stop, then start on the new case.
        rig.Window.FindControl<ComboBox>("BridgeZonePicker")!.SelectedItem = BridgeChoices.Zones[1];
        Pump();
        Assert.NotNull(rig.Bridge.PendingRestart);
        rig.Connection!.Next("stop", Pump);
        JsonObject start = rig.Connection.Next("start", Pump);
        Await(rig.Bridge.PendingRestart!);
        Assert.Equal("zone-ukpn", (string?)start["case"]);
        Assert.Equal("presolved", (string?)start["source"]);
        Assert.Equal("RESTARTING  *  zone-ukpn", rig.Dash.BridgeStatus);
        Assert.Null(rig.Run.LastFrame);   // the old stream's frames are dropped (WPF fix #7)
        Assert.Equal(1, server.AcceptedCount);   // the same socket
        Assert.Contains(rig.Dash.Events, e => e.EndsWith(string.Create(CultureInfo.InvariantCulture,
            $"  Bridge: stream zone-ukpn from {rig.Bridge.QueryAt:yyyy-MM-dd HH:mm}Z"), StringComparison.Ordinal));

        // The SRC picker likewise.
        rig.Bridge.SelectedSource = BridgeChoices.Sources[1];
        rig.Connection.Next("stop", Pump);
        JsonObject start2 = rig.Connection.Next("start", Pump);
        Await(rig.Bridge.PendingRestart!);
        Assert.Equal("estimated", (string?)start2["source"]);
        Assert.Equal("zone-ukpn", (string?)start2["case"]);

        // The FROM picker: the anchor moves back to the chosen preset.
        rig.Bridge.SelectedStart = BridgeChoices.Starts[2];
        rig.Connection.Next("stop", Pump);
        JsonObject start3 = rig.Connection.Next("start", Pump);
        Await(rig.Bridge.PendingRestart!);
        Assert.InRange(DateTime.UtcNow - BridgeProtocol.ParseUtc((string?)start3["start"]),
            TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));
    }

    // ================================================================ stream lost and reconnect

    [AvaloniaFact]
    public void A_lost_stream_fails_loud_clears_the_stream_fed_views_and_right_click_reconnects_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        string s1 = StateJson(TickIso(1), 1);
        rig.SendState(s1, () => rig.Run.LastFrame?.Tick == 1);
        rig.RefLiveTick(s1, 1, firstLive: true);

        // The server closes the socket: the WPF Faulted handler's calls.
        Await(rig.Connection!.CloseAsync());
        PumpUntil(() => rig.Bridge.IsFaulted, "the bridge fault");
        rig.Reference.SetBridgeStatus("BRIDGE FAULT  *  disconnected  *  right-click the map to reconnect", BridgeChoices.BadColour);
        rig.Reference.MarkStreamLost("BRIDGE FAULT");
        rig.Reference.Log(rig.Reference.Clock, "Bridge fault: The bridge closed the connection.");
        Assert.Equal("STREAM LOST", rig.Dash.Status);
        Assert.False(rig.Dash.HudVisible);
        AssertSameAsReference(rig);

        // Right-click now offers only the reconnect (on a bus, a branch or empty map alike).
        foreach ((int? bus, int? br) in new (int?, int?)[] { (3, null), (null, 0), (null, null) })
            Assert.Equal(BridgeSession.ReconnectItem, Assert.Single(rig.Bridge.ContextMenu(bus, br)).Header);

        // Reconnect: a fresh socket and stream, exactly as choosing the source does.
        Task again = rig.Bridge.ContextMenu(null, null)[0].Run!();
        FakeBridgeConnection second = server.NextConnection(Pump);
        second.Next("start", Pump);
        Await(again);
        Assert.Equal(2, server.AcceptedCount);
        Assert.False(rig.Bridge.IsFaulted);
        Assert.Equal("CONNECTING  *  zone-nged", rig.Dash.BridgeStatus);
        rig.Connection = second;
        rig.SendTopology();
        rig.SendState(StateJson(TickIso(2), 2), () => rig.Run.LastFrame?.Tick == 1);   // a new client counts from 1
        Assert.Equal("LIVE  *  streaming zone-nged", rig.Dash.BridgeStatus);
    }

    [AvaloniaFact]
    public void A_server_error_and_a_horizon_rejection_each_end_the_stream_with_the_WPF_words()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        rig.SendState(StateJson(TickIso(1), 1), () => rig.Run.LastFrame?.Tick == 1);

        // A stream-level error message from the server: BRIDGE FAULT with the server's words.
        Await(rig.Connection!.SendAsync("""{ "type": "error", "message": "no settled data for zone-nged" }"""));
        PumpUntil(() => rig.Bridge.IsFaulted, "the bridge fault");
        Assert.Equal("BRIDGE FAULT  *  disconnected  *  right-click the map to reconnect", rig.Dash.BridgeStatus);
        Assert.Contains(rig.Dash.Events, e => e.EndsWith("  Bridge fault: no settled data for zone-nged", StringComparison.Ordinal));
        Assert.Contains(rig.Dash.Alarms, a => a.Text == "BRIDGE FAULT: STREAM LOST - the map and tabs show the last frame received, not live data");

        // Reconnect, then a tick inside the 169 h horizon: refused before any frame is built, HORIZON REJECTION.
        Task again = rig.Bridge.ContextMenu(null, null)[0].Run!();
        FakeBridgeConnection second = server.NextConnection(Pump);
        second.Next("start", Pump);
        Await(again);
        rig.Connection = second;
        rig.SendTopology();
        Await(second.SendAsync(StateJson(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), 9)));
        PumpUntil(() => rig.Bridge.IsFaulted, "the horizon rejection");
        Assert.Equal("HORIZON REJECTION  *  stream stopped  *  right-click the map to reconnect", rig.Dash.BridgeStatus);
        Assert.Equal("STREAM LOST", rig.Dash.Status);
        Assert.Contains(rig.Dash.Alarms, a => a.Text == "HORIZON REJECTION: STREAM LOST - the map and tabs show the last frame received, not live data");
        Assert.Null(rig.Run.LastFrame);   // the reconnect dropped the old frame; the future tick never became one
    }

    // ================================================================ transport security

    [AvaloniaFact]
    public void An_unencrypted_endpoint_is_refused_with_the_client_words_unless_the_settings_allow_it()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server, allowUnencrypted: false);
        Await(rig.Vm.LoadCaseAsync(rig.Source));
        PumpFor(300);

        // The refusal is the client's own (Core GridBridgeClient.ConnectAsync), refused before any socket is opened.
        var uri = new Uri(server.Endpoint);
        Task<InvalidOperationException> expected = Assert.ThrowsAsync<InvalidOperationException>(() => new GridBridgeClient().ConnectAsync(uri));
        string why = Await(expected).Message;
        Assert.StartsWith("Refusing to connect to an unencrypted bridge endpoint", why, StringComparison.Ordinal);
        Assert.Equal($"CONNECT FAILED  *  {why}", rig.Dash.BridgeStatus);
        Assert.Equal(BridgeChoices.BadColour, rig.Dash.BridgeStatusColour);
        Assert.Contains(rig.Dash.Events, e => e == $"--:--  Bridge connect failed: {why}");
        Assert.Equal($"Bridge connect failed: {why}", rig.Vm.StatusText);
        Assert.False(rig.Bridge.IsActive);
        Assert.Equal(0, server.AcceptedCount);   // nothing was opened
    }

    // ================================================================ leaving the bridge, closing, gating

    [AvaloniaFact]
    public void Opening_a_local_case_leaves_the_bridge_and_hides_its_pill_and_HUD_as_WPF()
    {
        using var server = new FakeBridgeServer();
        using var rig = new BridgeRig(server);
        rig.Connect();
        rig.SendTopology();
        rig.SendState(StateJson(TickIso(1), 1), () => rig.Run.LastFrame?.Tick == 1);
        Assert.True(rig.Dash.HudVisible);

        Await(rig.Vm.LoadCaseAsync(rig.Case9));
        PumpUntil(() => rig.Connection!.ClientClosed, "the client to close the socket");
        Assert.False(rig.Bridge.IsActive);
        Assert.False(rig.Run.IsBridge);
        Assert.False(rig.Dash.HudVisible);
        Assert.False(rig.Dash.BridgeStatusVisible);
        Assert.Equal(DashboardModel.MapHintLocal, rig.Dash.MapHint);
        Assert.True(rig.Run.CanChooseSolver);
        PumpUntil(() => rig.Run.LastFrame is { Model.BusCount: 9 }, "a case9 frame");
    }

    [AvaloniaFact]
    public void Closing_the_window_with_a_live_bridge_closes_the_socket_first_then_the_window()
    {
        using var server = new FakeBridgeServer();
        var rig = new BridgeRig(server);
        try
        {
            rig.Connect();
            rig.Window.Close();
            Assert.NotNull(rig.Window.BridgeClosing);   // the first close was cancelled while the socket closes
            Await(rig.Window.BridgeClosing!);
            PumpUntil(() => !rig.Window.IsVisible, "the window to close");
            PumpUntil(() => rig.Connection!.ClientClosed, "the socket to close");
            Assert.False(rig.Bridge.IsActive);
        }
        finally { rig.RefTabs.Dispose(); rig.Temp.Dispose(); }
    }

    [AvaloniaFact]
    public void Without_the_bridge_registered_no_session_exists_and_the_pickers_never_show()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(new ShellOptions
        {
            SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
            DiscoverCases = (_, _) => new List<ModelSource>(),
            LicenceDescription = () => "Test licence",
            UseNativeMenu = false,
            AutoLoadAtStart = false,
            BridgeEnabled = false,
        });
        w.Show();
        try
        {
            Assert.Null(w.ViewModel.Run.Bridge);
            Assert.False(w.ViewModel.Run.IsBridge);
            Await(w.ViewModel.LoadCaseAsync(new ModelSource("Live bridge", "", Bridge: "wss://127.0.0.1:9/ws/bridge")));
            Assert.Equal(RunViewModel.BridgeNotAvailableText, w.ViewModel.Run.LoadError);
            Assert.False(w.FindControl<ComboBox>("BridgeZonePicker")!.IsVisible);
        }
        finally { w.Close(); }
    }
}
