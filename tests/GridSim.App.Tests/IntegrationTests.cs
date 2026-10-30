using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Dashboard;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Dynamics;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Generation;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme integration (2026-10-20): the main window put together end to end, on the headless platform with Skia
/// rendering, for the shipped MATPOWER case9 and the reduced GB case (data/uk).
/// <list type="bullet">
/// <item><description>One feed: the window's dashboard and every tab are fed only through the dashboard host and the
/// dashboard's tab hooks. After N frames, every tab's model was updated exactly N times (no tab fed twice, none
/// skipped), in the WPF DashboardViewModel order, THE MACHINE's speed trace holds exactly one point per frame, and a
/// frame on which the dashboard itself throws feeds no tab (WPF). No view keeps a way of feeding itself.</description></item>
/// <item><description>Layout: the NETWORK tab is the DashboardView showing the window's one dashboard, with the network
/// map in its MapSlot; the window shows the lamp, clock and banner, so the tab does not repeat them.</description></item>
/// <item><description>Shell: Help is the last menu; a View toggle and a colour mode show their checked state, before and
/// after they are used; the audit-log viewer sits in the File menu after Open run folder....</description></item>
/// <item><description>Trip: a map trip writes the swing study's log line (with RoCoF and nadir) to the event log and
/// raises the TRIP alarm, as WPF; the NESO RP1 and MECHANICAL drill-downs open their inspector overlays.</description></item>
/// </list>
/// </summary>
public sealed class IntegrationTests
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
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
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

    private static List<MenuItem> TopMenus(MainWindow w) => w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().ToList();

    private static MenuItem Item(MenuItem menu, string header) =>
        menu.Items.OfType<MenuItem>().SingleOrDefault(i => (string?)i.Header == header)
        ?? throw new Xunit.Sdk.XunitException($"no item {header} in menu {menu.Header}");

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Show <paramref name="header"/>'s tab and render it once, so its lists are realised.</summary>
    private static void ShowTab(MainWindow w, string header)
    {
        w.TabStrip.SelectedItem = w.TabStrip.Items.OfType<TabItem>().Single(t => Equals(t.Header, header));
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button FirstRowButton(Control view, string list) =>
        ((view.FindControl<ItemsControl>(list) ?? throw new Xunit.Sdk.XunitException($"no list {list}"))
            .ContainerFromIndex(0) ?? throw new Xunit.Sdk.XunitException($"{list}[0] is not realised"))
        .GetVisualDescendants().OfType<Button>().First();

    public static IEnumerable<object[]> Cases => new[] { new object[] { "case9" }, new object[] { "uk" } };

    // ---------------------------------------------------------------- the whole window

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void The_window_feeds_every_tab_once_per_frame_through_the_dashboard_and_is_laid_out_as_WPF(string name)
    {
        using var temp = new TempFolder();
        ModelSource source = Case(name);
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            RunViewModel run = vm.Run;
            TabModels tabs = vm.Tabs;

            // ---- one dashboard, one feed ----
            Assert.Same(tabs, vm.Dashboard.Tabs);                 // the dashboard owns the tabs' feed
            Assert.Same(vm.Dashboard, vm.DashboardHost.Model);    // the host feeds that dashboard
            Assert.Same(run, vm.DashboardHost.Run);               // attached to the window's run pipeline
            Assert.Same(vm.Dashboard, vm.Map.Dashboard);          // the map writes into the same dashboard
            Assert.Same(vm.Dashboard, run.TripDashboard);         // and the trip studies on it
            // No view keeps a way of feeding itself from the run pipeline (the self-attach feeds were removed).
            Assert.Null(typeof(MainWindow).Assembly.GetType("GridSim.App.Tabs.TabRunFeed"));
            foreach (Type viewType in typeof(MainWindow).Assembly.GetTypes().Where(t => t.Namespace == "GridSim.App.Tabs" && typeof(Control).IsAssignableFrom(t)))
                Assert.Null(viewType.GetMethod("Attach", BindingFlags.Public | BindingFlags.Instance));

            // Count every tab update and every frame the run pipeline hands the dashboard.
            var updates = TabModels.UpdateOrder.ToDictionary(h => h, _ => 0, StringComparer.Ordinal);
            var sequence = new List<string>();
            tabs.TabUpdated += h => { updates[h]++; sequence.Add(h); };
            int frames = 0;
            run.FrameArriving += (_, e) => { if (e.Kind != RunFrameKind.LiveFaulted) frames++; };

            Await(vm.LoadCaseAsync(source));
            Assert.False(run.HasError, run.ErrorText);
            Await(vm.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
            PumpUntil(() => frames >= 6, "6 frames");
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            PumpFor(200);   // let a frame in flight land; nothing more comes while paused
            int n = frames;
            Assert.True(n >= 6, $"{n} frames");

            // Every tab's model got exactly N updates, in the WPF order, all through the dashboard's hooks.
            foreach ((string header, int count) in updates)
                Assert.True(count == n, $"{header}: {count} updates for {n} frames");
            Assert.Equal(TabModels.UpdateOrder.Count * n, sequence.Count);
            for (int i = 0; i < sequence.Count; i++)
                Assert.Equal(TabModels.UpdateOrder[i % TabModels.UpdateOrder.Count], sequence[i]);
            Assert.Equal(Math.Min(n, 120), tabs.Machine.FreqTrace.Count);   // one speed-trace point per frame: never fed twice
            Assert.Equal(run.LastFrame!.Tick.ToString(Inv), vm.Dashboard.Tick);
            Assert.Equal(vm.Dashboard.Clock, tabs.Mechanical.Clock);       // the tab clock is the dashboard's, as WPF

            // A frame on which the dashboard itself throws (a NaN frequency fails its frequency test) feeds no tab, as
            // in WPF, where the tabs were fed from inside the dashboard's update.
            int before = sequence.Count;
            SimFrame bad = run.LastFrame! with { FrequencyHz = double.NaN };
            Assert.ThrowsAny<Exception>(() => vm.Dashboard.Update(bad, Array.Empty<GridSim.Core.Analysis.BoundaryFlow>()));
            Assert.Equal(before, sequence.Count);

            // ---- the layout: NETWORK is the dashboard view, with the map in its slot ----
            Assert.Equal(0, w.TabStrip.SelectedIndex);
            TabItem network = w.TabStrip.Items.OfType<TabItem>().First();
            Assert.Equal("NETWORK", network.Header);
            Assert.Same(w.DashboardTab, network.Content);
            Assert.Same(vm.Dashboard, w.DashboardTab.Model);
            Assert.Same(vm.Dashboard, w.DashboardTab.DataContext);
            ContentControl slot = w.DashboardTab.FindControl<ContentControl>("MapSlot")!;
            Assert.Same(slot.Content, w.DashboardTab.Map);
            Assert.Same(w.MapControl.Parent, slot.Content);   // the map panel (map + hover tip) is the slot's content
            Assert.Same(w.DashboardTab, w.MapControl.FindAncestorOfType<DashboardView>());
            Assert.True(w.MapControl.IsEffectivelyVisible);
            Assert.True(w.MapControl.Bounds.Width > 100 && w.MapControl.Bounds.Height > 100, $"the map was not laid out: {w.MapControl.Bounds}");
            // The window shows the lamp, the clock and the banner over every tab; the tab does not repeat them.
            Assert.False(w.DashboardTab.ShowsWindowParts);
            Assert.False(w.DashboardTab.FindControl<DockPanel>("HeaderStrip")!.IsVisible);
            Assert.False(w.DashboardTab.FindControl<Panel>("BannerHost")!.IsVisible);
            Assert.Equal(vm.Dashboard.Clock, w.FindControl<TextBlock>("HeaderClockText")!.Text);
            Assert.Equal(vm.Dashboard.Status, w.FindControl<TextBlock>("HeaderStatusText")!.Text);
            // The tiles show the dashboard's values (the temporary live panel is gone).
            Assert.Null(w.FindControl<Border>("LivePanel"));
            Assert.Equal(vm.Dashboard.Freq, w.DashboardTab.FindControl<TextBlock>("FreqText")!.Text);
            Assert.Equal(vm.Dashboard.Demand, w.DashboardTab.FindControl<TextBlock>("DemandText")!.Text);

            // ---- the shell: Help last, the audit log after Open run folder..., ticks on the toggles ----
            List<MenuItem> menus = TopMenus(w);
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, menus.Select(m => (string?)m.Header));
            // Test retargeted 2026-10-20 (UI shell: v2 + pickers): File > Open case folder... and Open v2 case... (the OS
            // pickers) now sit between Open run folder... and Audit log.... Equal: the whole File menu is still pinned
            // exactly, in order, with the audit log after the open commands and Exit last.
            Assert.Equal(new[] { "Open case...", "Open run folder...", "Open case folder...", "Open v2 case...", "Audit log...", "Exit" },
                menus[0].Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            MenuItem view = menus[2];
            MenuItem minimap = Item(view, "Minimap");
            Assert.Equal(MenuItemToggleType.CheckBox, minimap.ToggleType);
            bool shown = w.MapControl.ShowMinimap;
            Assert.Equal(shown, minimap.IsChecked);
            Await(vm.Registry.Get(MapCommands.Minimap).RunAsync());   // the M key's command
            Assert.Equal(!shown, w.MapControl.ShowMinimap);
            Assert.Equal(!shown, minimap.IsChecked);                    // the tick follows at once
            Assert.Equal(MenuItemToggleType.None, Item(view, "Fit to view").ToggleType);   // an action shows no tick
            MenuItem colourBy = Item(view, "Colour by");
            Assert.Equal(w.MapControl.ColorMode == MapColourMode.Loading, Item(colourBy, "LOADING").IsChecked);
            Await(vm.Registry.Get(MapCommands.ColourById(MapColourMode.Voltage)).RunAsync());
            Assert.True(Item(colourBy, "VOLTAGE").IsChecked);
            Assert.False(Item(colourBy, "LOADING").IsChecked);
            Assert.False(Item(colourBy, "CONDITION").IsChecked);
            Assert.False(Item(colourBy, "STRESS").IsChecked);

            // ---- the trip: the swing study's log line and the TRIP alarm, as WPF ----
            Generator g = InfeedSelection.LargestOnline(run.LastFrame!.Model)!.Value.Unit;
            int alarms = vm.Dashboard.Alarms.Count;
            Await(vm.Registry.Get(MapCommands.TripLargestInfeed).RunAsync());
            FrequencyResponse resp = vm.Dashboard.BaselineResponse ?? throw new Xunit.Sdk.XunitException("no swing study ran");
            string line = TripSwingStudy.LogLine(g, resp);
            Assert.Contains("  RoCoF ", line, StringComparison.Ordinal);
            Assert.Contains("  nadir ", line, StringComparison.Ordinal);
            Assert.Equal($"{vm.Dashboard.Clock}  {line}", vm.Dashboard.Events[0]);
            Assert.Equal(line, vm.StatusText);
            Assert.Equal(Math.Min(alarms + 1, DashboardModel.AlarmListCapacity), vm.Dashboard.Alarms.Count);
            Assert.Equal(TripSwingStudy.AlarmText(g), vm.Dashboard.Alarms[0].Text);
            Assert.Equal(TripSwingStudy.AlarmColour, vm.Dashboard.Alarms[0].Colour);
            Assert.NotEqual("Run a largest-unit trip to compare.", vm.Dashboard.TripBaseline);   // the GENERATION tab's compare ran
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the drill-down inspectors in the window

    [AvaloniaFact]
    public void The_NESO_RP1_and_MECHANICAL_drill_downs_open_their_inspector_overlays_in_the_window()
    {
        using var temp = new TempFolder();
        ModelSource source = Case("case9");
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            Await(vm.LoadCaseAsync(source));
            Await(vm.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
            PumpUntil(() => vm.Run.LastFrame is { Tick: >= 4 }, "4 frames");
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            PumpFor(200);

            // NESO RP1: a dispatch-stack row opens the overlay on that fuel's page, as the WPF InspectFuelCommand.
            ShowTab(w, "NESO RP1");
            NesoRp1View rp1 = w.FindControl<NesoRp1View>("NesoRp1Tab")!;
            InspectorView rp1Overlay = rp1.FindControl<InspectorView>("Inspector")!;
            Assert.Same(rp1.InspectorModel, rp1Overlay.Model);
            Assert.False(rp1Overlay.IsVisible);
            string fuel = vm.Tabs.NesoRp1.GenStack[0].Fuel;
            Click(FirstRowButton(rp1, "GenStackList"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(rp1.InspectorModel.IsOpen);
            Assert.True(rp1Overlay.IsVisible);
            Assert.Equal(fuel.ToUpperInvariant(), rp1.InspectorModel.Title);
            Assert.NotEmpty(rp1.InspectorModel.Rows);
            rp1.InspectorModel.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(rp1Overlay.IsVisible);

            // MECHANICAL: a machine row opens the overlay on that machine, as the WPF InspectMachineCommand.
            ShowTab(w, "MECHANICAL");
            MachineEngineerView mech = w.FindControl<MachineEngineerView>("MechanicalTab")!;
            Assert.Same(vm.Tabs.Mechanical.Inspector, mech.FindControl<InspectorView>("Inspector")!.Model);
            // case9's units are untyped (not spinning plant), so the tab lists no machine: one is put in the list by
            // hand (the run is paused, so no frame rebuilds the list) to exercise the click path, as TabsBatch2ViewTests.
            Assert.Empty(vm.Tabs.Mechanical.Machines);
            vm.Tabs.Mechanical.Machines.Add(new GridSim.Ui.Tabs.MachineEngineer.MachineRowModel
            {
                Name = "Test unit", Fuel = "CCGT", Mw = "400", Mvar = "-", RiskLabel = "OK", KeGvaS = "2.00", HseSec = "5.0s",
                RampMwMin = "+0.0", TestDate = "-",
            });
            ShowTab(w, "MECHANICAL");
            Click(FirstRowButton(mech, "MachinesList"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Tabs.Mechanical.Inspector.IsOpen);
            Assert.True(mech.FindControl<InspectorView>("Inspector")!.IsVisible);
            Assert.Equal("TEST UNIT", vm.Tabs.Mechanical.Inspector.Title);
        }
        finally { Close(w); }
    }
}
