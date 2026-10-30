using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Cycle;
using GridSim.Ui.Tabs.Domains;
using GridSim.Ui.Tabs.Market;
using GridSim.Ui.Tabs.Operator;
using GridSim.Ui.Tabs.Scada;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.6 (first tab batch): the SCADA, OPERATOR, MARKET, SPEED DOMAINS and CYCLE tabs in the Avalonia
/// app, on the headless platform with Skia rendering. The tab strip carries the map as its NETWORK tab and these tabs
/// after it in the WPF order; each tab renders; for a case loaded through the run pipeline (the shipped MATPOWER case9
/// and the reduced GB case) every text a tab shows equals its shared model's value after several frames, and each
/// colour is the token's exact bytes; the tab models are fed as the WPF dashboard feeds its tabs (the same frames, the
/// dashboard's clock, in order); switching tabs works and gates the SPEED DOMAINS decomposition as in WPF; and the
/// SCADA filter and acknowledgements work through the view.
/// </summary>
public sealed class TabsTests
{
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

    /// <summary>Show <paramref name="header"/>'s tab and render it once, so its items are realised and laid out.</summary>
    private static T Show<T>(MainWindow w, string header, string viewName) where T : Control
    {
        TabItem item = w.TabStrip.Items.OfType<TabItem>().Single(t => Equals(t.Header, header));
        w.TabStrip.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        T view = w.FindControl<T>(viewName) ?? throw new InvalidOperationException($"no {viewName} in the window");
        Assert.True(view.IsEffectivelyVisible, $"{header} is not showing");
        Assert.True(view.Bounds.Width > 100 && view.Bounds.Height > 100, $"{header} was not laid out: {view.Bounds}");
        return view;
    }

    /// <summary>The text a named TextBlock shows (its runs joined, for a TextBlock built from runs).</summary>
    private static string Text(Control view, string name)
    {
        TextBlock t = view.FindControl<TextBlock>(name) ?? throw new InvalidOperationException($"no TextBlock {name}");
        return t.Inlines is { Count: > 0 } runs ? string.Concat(runs.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "";
    }

    private static Color Fore(Control view, string name) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(view.FindControl<TextBlock>(name)!.Foreground).Color;

    private static Color Col(UiColour c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Every text shown in the realised row at <paramref name="index"/> of the named items control, in order.</summary>
    private static List<string> RowTexts(Control view, string list, int index)
    {
        ItemsControl items = view.FindControl<ItemsControl>(list) ?? throw new InvalidOperationException($"no ItemsControl {list}");
        Control row = items.ContainerFromIndex(index) ?? throw new InvalidOperationException($"{list}[{index}] is not realised");
        // A bar's own percentage text (inside the theme's ProgressBar template, never shown here) is not a row text.
        return row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.FindAncestorOfType<ProgressBar>() is null)
            .Select(t => t.Text ?? "").ToList();
    }

    private static int RowCount(Control view, string list) =>
        (view.FindControl<ItemsControl>(list) ?? throw new InvalidOperationException($"no ItemsControl {list}")).ItemCount;

    /// <summary>
    /// Open <paramref name="source"/> in a new main window, feed independent reference models with every frame the
    /// window accepts (the frame, the boundaries and the dashboard clock the window's dashboard just set), wait for
    /// <paramref name="frames"/> frames, then pause and let any frame in flight land.
    /// </summary>
    private static (MainWindow W, References Ref) OpenAndRun(TempFolder temp, ModelSource source, int frames)
    {
        var w = new MainWindow(Options(temp, source));
        w.Show();
        RunViewModel run = w.ViewModel.Run;
        var refs = new References();
        int shown = 0;
        // Attached after the window's own handlers, so the window's dashboard (and its tabs) took the frame first and
        // its Clock is this frame's: the references get exactly what the WPF dashboard hands its tabs.
        run.ModelLoaded += (_, _) => { refs.Scada.ForgetModel(); refs.Operator.Reset(); refs.Market.Reset(); };
        run.FrameArrived += (_, f) =>
        {
            string clock = w.ViewModel.Dashboard.Clock;
            refs.Scada.Update(f.Frame, clock);
            refs.Operator.Update(f.Frame, f.Boundaries, clock);
            refs.Market.Update(f.Frame, clock);
            refs.Domains.Update(f.Frame);
            refs.Frames.Add(f);
        };
        run.FrameShown += (_, _) => shown++;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => shown >= frames, $"{frames} frames");
        Await(w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync());
        PumpFor(150);
        return (w, refs);
    }

    /// <summary>Reference models fed beside the window's (the WPF dashboard's tab feed, done by hand).</summary>
    private sealed class References
    {
        public ScadaModel Scada { get; } = new();
        public OperatorModel Operator { get; } = new();
        public MarketModel Market { get; } = new();
        public DomainsModel Domains { get; } = new() { Active = true };
        public List<ArrivedFrame> Frames { get; } = new();
    }

    public static IEnumerable<object[]> Cases => new[] { new object[] { "case9" }, new object[] { "uk" } };

    // ---------------------------------------------------------------- the strip

    [AvaloniaFact]
    public void The_tab_strip_holds_the_map_first_and_the_ported_tabs_in_the_WPF_order_and_switching_works()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, Case("case9")));
        w.Show();
        try
        {
            // Test retargeted 2026-10-20 (UI programme integration): the window now hosts every ported tab, in the WPF
            // order with the WPF headers (ASSETS gated; HARMONICS and ABOUT not ported). The assertion is unchanged in
            // kind (the whole strip, in order) and strengthened: the five tabs checked before keep their relative order
            // and the seven joined at their WPF places are checked too, each bound to the model the dashboard feeds.
            // Test retargeted 2026-10-20 (UI programme 1.7): the HARMONICS tab is ported and joins the strip at its WPF
            // place, between EXPERIMENTAL and CYCLE. The assertion is unchanged in kind (the whole strip, exactly, in
            // order) and stronger: every tab checked before keeps its relative order and one more WPF tab is pinned.
            // Test retargeted 2026-10-20 (UI programme ASSETS A3): the ASSETS tab is ported and joins the strip at its
            // WPF place, after CYCLE (WPF then has ABOUT, which this app keeps in the Help menu, so ASSETS is last). The
            // assertion is unchanged in kind (the whole strip, exactly, in order) and stronger: every tab checked before
            // keeps its place, one more WPF tab is pinned, and it shows its own view over the navigator the dashboard
            // feeds.
            string[] headers = w.TabStrip.Items.OfType<TabItem>().Select(t => (string)t.Header!).ToArray();
            Assert.Equal(new[]
            {
                "NETWORK", "GENERATION & INERTIA", "THE MACHINE", "SCADA", "OPERATOR", "MARKET", "NESO RP1", "MECHANICAL",
                "HUMAN SCALE", "SPEED DOMAINS", "INVERTER FLEET", "EXPERIMENTAL", "HARMONICS", "CYCLE", "ASSETS",
            }, headers);
            Assert.False(w.ViewModel.Tabs.Assets.Active);
            Assert.Same(w.ViewModel.Tabs.Assets, Show<GridSim.App.Assets.AssetsView>(w, "ASSETS", "AssetsTab").DataContext);
            Assert.True(w.ViewModel.Tabs.Assets.Active);   // its explorer refreshes only while its tab shows (WPF D-DOC-073)
            Assert.Same(w.ViewModel.Dashboard, Show<GenerationView>(w, "GENERATION & INERTIA", "GenerationTab").DataContext);
            Assert.Same(w.ViewModel.Tabs.Machine, Show<MachineView>(w, "THE MACHINE", "MachineTab").DataContext);
            Assert.Same(w.ViewModel.Tabs.NesoRp1, Show<NesoRp1View>(w, "NESO RP1", "NesoRp1Tab").DataContext);
            Assert.Same(w.ViewModel.Tabs.Mechanical, Show<MachineEngineerView>(w, "MECHANICAL", "MechanicalTab").DataContext);
            Assert.Same(w.ViewModel.Tabs.HumanScale, Show<HumanScaleView>(w, "HUMAN SCALE", "HumanScaleTab").DataContext);
            Assert.Same(w.ViewModel.Tabs.InverterFleet, Show<InverterFleetView>(w, "INVERTER FLEET", "InverterFleetTab").DataContext);
            Assert.False(w.ViewModel.Tabs.Experimental.Active);
            Assert.Same(w.ViewModel.Tabs.Experimental, Show<ExperimentalView>(w, "EXPERIMENTAL", "ExperimentalTab").DataContext);
            Assert.True(w.ViewModel.Tabs.Experimental.Active);   // its models run only while its tab shows (WPF R020)
            w.TabStrip.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.False(w.ViewModel.Tabs.Experimental.Active);
            Assert.All(w.TabStrip.Items.OfType<TabItem>(), t => Assert.Contains("RoomTabItem", t.Classes));
            Assert.Contains("RoomTabs", w.TabStrip.Classes);
            // The map is the NETWORK tab, shown first.
            Assert.Equal(0, w.TabStrip.SelectedIndex);
            Assert.NotNull(w.MapControl.GetVisualRoot());   // the map is on screen
            Assert.False(w.ViewModel.Tabs.Domains.Active);

            // Each tab shows its own view bound to its own shared model, and the map leaves the screen while it does.
            Assert.Same(w.ViewModel.Tabs.Scada, Show<ScadaView>(w, "SCADA", "ScadaTab").DataContext);
            Assert.Null(w.MapControl.GetVisualRoot());   // the map left the screen
            Assert.Same(w.ViewModel.Tabs.Operator, Show<OperatorView>(w, "OPERATOR", "OperatorTab").DataContext);
            Assert.Same(w.ViewModel.Tabs.Market, Show<MarketView>(w, "MARKET", "MarketTab").DataContext);
            Assert.False(w.ViewModel.Tabs.Domains.Active);
            Assert.Same(w.ViewModel.Tabs.Domains, Show<DomainsView>(w, "SPEED DOMAINS", "DomainsTab").DataContext);
            Assert.True(w.ViewModel.Tabs.Domains.Active);   // the decomposition runs only while its tab shows (WPF)
            Assert.Same(w.ViewModel.Tabs.Cycle, Show<CycleView>(w, "CYCLE", "CycleTab").DataContext);
            Assert.False(w.ViewModel.Tabs.Domains.Active);

            // Back to the map.
            w.TabStrip.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(w.MapControl.GetVisualRoot());   // the map is on screen
            Assert.False(w.ViewModel.Tabs.Domains.Active);

            // Before any case: the WPF starting texts.
            CycleView cycle = Show<CycleView>(w, "CYCLE", "CycleTab");
            Assert.Equal("WAITING FOR FIRST CYCLE", Text(cycle, "HeroText"));
            Assert.Equal(Col(DashboardColoursGray), Fore(cycle, "HeroText"));
            OperatorView op = Show<OperatorView>(w, "OPERATOR", "OperatorTab");
            Assert.Equal("50.00", Text(op, "FrequencyText"));
            Assert.Equal(w.ViewModel.Tabs.Operator.DesignBasisLoss, Text(op, "DesignBasisLossText"));
            Assert.False(op.FindControl<Border>("WarningBannerBorder")!.IsVisible);
        }
        finally { Close(w); }
    }

    private static readonly UiColour DashboardColoursGray = GridSim.Ui.Dashboard.DashboardColours.Gray;

    // ---------------------------------------------------------------- each tab after a run

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Every_tab_shows_its_models_texts_and_colours_after_frames_of_a_loaded_case(string name)
    {
        using var temp = new TempFolder();
        (MainWindow w, References refs) = OpenAndRun(temp, Case(name), frames: 6);
        try
        {
            TabModels tabs = w.ViewModel.Tabs;
            Assert.True(refs.Frames.Count >= 6);
            // The window's tab models were fed exactly what the WPF dashboard hands its tabs: equal to the references.
            Assert.Equal(refs.Operator.Frequency, tabs.Operator.Frequency);
            Assert.Equal(refs.Operator.Rocof, tabs.Operator.Rocof);
            Assert.Equal(refs.Operator.LargestLoss, tabs.Operator.LargestLoss);
            Assert.Equal(refs.Operator.Boundaries.Select(b => b.Flow), tabs.Operator.Boundaries.Select(b => b.Flow));
            Assert.Equal(refs.Operator.Fuel.Select(b => b.Mw), tabs.Operator.Fuel.Select(b => b.Mw));
            Assert.Equal(refs.Market.Carbon, tabs.Market.Carbon);
            Assert.Equal(refs.Market.RenewableShare, tabs.Market.RenewableShare);
            Assert.Equal(refs.Scada.GoodCount, tabs.Scada.GoodCount);
            Assert.Equal(refs.Scada.Points.Select(p => p.Value + "|" + p.Quality + "|" + p.Stamp), tabs.Scada.Points.Select(p => p.Value + "|" + p.Quality + "|" + p.Stamp));
            Assert.Equal(refs.Scada.Events.Select(e => e.Stamp + e.Message), tabs.Scada.Events.Select(e => e.Stamp + e.Message));
            // Every live point is stamped with the dashboard clock of the last frame (the WPF tab feed's clock).
            Assert.All(tabs.Scada.Points.Where(p => p.Quality != "STALE"), p => Assert.Equal(w.ViewModel.Dashboard.Clock, p.Stamp));

            // ---- OPERATOR ----
            OperatorView op = Show<OperatorView>(w, "OPERATOR", "OperatorTab");
            OperatorModel om = tabs.Operator;
            foreach ((string box, string want) in new[]
                     {
                         ("FrequencyText", om.Frequency), ("RocofText", om.Rocof), ("InertiaText", om.Inertia), ("DemandText", om.Demand),
                         ("GenerationText", om.Generation), ("LossesText", om.Losses), ("LargestLossText", om.LargestLoss),
                         ("DesignBasisLossText", om.DesignBasisLoss), ("DeratedMarginText", om.DeratedMargin), ("ReserveText", om.ReserveMw),
                         ("SynchronousText", om.SynchronousGw), ("ConverterText", om.ConverterGw),
                     })
                Assert.Equal(want, Text(op, box));
            Assert.Equal(Col(om.FrequencyColour), Fore(op, "FrequencyText"));
            Assert.Equal(om.Boundaries.Count, RowCount(op, "BoundaryList"));
            Assert.Equal(om.Fuel.Count, RowCount(op, "FuelList"));
            Assert.NotEmpty(om.Fuel);
            Assert.Equal(new[] { om.Fuel[0].Fuel, om.Fuel[0].Mw, om.Fuel[0].Share }, RowTexts(op, "FuelList", 0));
            if (om.Boundaries.Count > 0)
                Assert.Equal(new[] { om.Boundaries[0].Name, om.Boundaries[0].Flow, om.Boundaries[0].Limit, om.Boundaries[0].Loading }, RowTexts(op, "BoundaryList", 0));

            // ---- MARKET ----
            MarketView mk = Show<MarketView>(w, "MARKET", "MarketTab");
            MarketModel mm = tabs.Market;
            foreach ((string box, string want) in new[]
                     {
                         ("ImbalancePriceText", mm.ImbalancePrice), ("MarketIndexText", mm.MarketIndex), ("BsuosText", mm.Bsuos),
                         ("ConstraintCostText", mm.ConstraintCost), ("CarbonText", mm.Carbon), ("DemandText", mm.Demand),
                         ("RenewableShareText", "renewable " + mm.RenewableShare), ("DemandForecastText", "fcast " + mm.DemandForecast),
                     })
                Assert.Equal(want, Text(mk, box));
            Assert.Equal(Col(mm.CarbonColour), Fore(mk, "CarbonText"));
            Assert.Equal(Col(mm.ImbalanceColour), Fore(mk, "ImbalancePriceText"));
            Assert.Equal(mm.Interconnectors.Count, RowCount(mk, "InterconnectorList"));
            Assert.Equal("STREAMED FROM LAKE", mk.FindControl<GridSim.App.Controls.Sparkline>("PriceTraceChart")!.Caption);

            // ---- SCADA ----
            ScadaView sc = Show<ScadaView>(w, "SCADA", "ScadaTab");
            ScadaModel sm = tabs.Scada;
            Assert.Equal(sm.GoodCount, Text(sc, "GoodCountText"));
            Assert.Equal(sm.PointCount, Text(sc, "PointCountText"));
            Assert.Equal(sm.Unacked, Text(sc, "UnackedText"));
            Assert.Equal(sm.Points.Count, RowCount(sc, "PointList"));
            Assert.Equal(sm.Events.Count, RowCount(sc, "EventList"));
            ScadaPointModel p0 = sm.Points[0];
            Assert.Equal(new[] { p0.Tag, p0.Description, p0.Value, p0.Units, p0.Load, p0.Quality, p0.Stamp }, RowTexts(sc, "PointList", 0));
            if (sm.Events.Count > 0)
            {
                ScadaEventModel e0 = sm.Events[0];
                Assert.Equal(new[] { e0.Priority, e0.Stamp, e0.Source, e0.Message, e0.AckLabel }, RowTexts(sc, "EventList", 0));
            }

            // ---- SPEED DOMAINS (computed when shown: it catches up on the newest frame at once) ----
            DomainsView dv = Show<DomainsView>(w, "SPEED DOMAINS", "DomainsTab");
            DomainsModel dm = tabs.Domains;
            Assert.Equal(refs.Domains.Status, dm.Status);
            Assert.Equal(refs.Domains.DeltaGva, dm.DeltaGva);
            foreach ((string box, string want) in new[]
                     {
                         ("StatusText", dm.Status), ("DomainCountText", dm.DomainCount), ("DeltaGvaText", dm.DeltaGva),
                         ("DominantText", dm.Dominant), ("ProvenanceText", dm.Provenance), ("GapText", dm.Gap),
                     })
                Assert.Equal(want, Text(dv, box));
            Assert.Equal(Col(dm.DominantColour), Fore(dv, "DominantText"));
            Assert.Equal(dm.Domains.Count, RowCount(dv, "GvadBars"));
            Assert.Equal(dm.Domains.Count, RowCount(dv, "InertiaBars"));
            Assert.Equal(dm.Domains.Count, RowCount(dv, "DomainCards"));
            Assert.NotEmpty(dm.Domains);
            SpeedDomainRowModel d0 = dm.Domains[0];
            Assert.Equal(new[] { d0.Name, d0.Speed, d0.GvadGva, d0.DominantTag }, RowTexts(dv, "GvadBars", 0));
            Assert.Equal(new[] { d0.Name, d0.Kind, d0.InertiaLabel }, RowTexts(dv, "InertiaBars", 0));
            Border bar = dv.FindControl<ItemsControl>("GvadBars")!.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Height == 22);
            Assert.Equal(d0.BarWidth, bar.Width);
            Assert.Equal(Col(d0.AccentColour), Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color);

            // ---- CYCLE (a completed cycle is projected on the UI thread through the poster) ----
            PumpUntil(() => tabs.Cycle.HasData, "the first completed analysis cycle");
            CycleView cv = Show<CycleView>(w, "CYCLE", "CycleTab");
            CycleModel cm = tabs.Cycle;
            foreach ((string box, string want) in new[]
                     {
                         ("HeroText", cm.Hero), ("SubtitleText", cm.Subtitle), ("UtilText", cm.Util), ("BaseSolveText", cm.BaseSolve),
                         ("WallText", cm.Wall), ("ComputeText", cm.Compute), ("HighText", cm.High), ("LowText", cm.Low),
                         ("CancelledText", cm.Cancelled), ("BacklogText", cm.Backlog), ("CoverageText", cm.Coverage), ("WorstText", cm.Worst),
                         ("CycleHeaderText", "CYCLE " + cm.CycleIndex + "  -  budget accounting"),
                     })
                Assert.Equal(want, Text(cv, box));
            Assert.StartsWith("HIGH TIER ", cm.Hero, StringComparison.Ordinal);
            // R101: a live frame carries its measured base-solve time to the cycle (WPF), so the figure is not "-".
            Assert.EndsWith(" ms", cm.BaseSolve, StringComparison.Ordinal);
            Assert.Equal(Col(cm.HeroColour), Fore(cv, "HeroText"));
            Assert.Equal(Col(cm.WorstColour), Fore(cv, "WorstText"));
            Assert.Equal(Col(cm.CoverageColour), Fore(cv, "CoverageText"));
            Assert.Equal(cm.Jobs.Count, RowCount(cv, "JobList"));
            Assert.NotEmpty(cm.Jobs);
            Assert.Equal(new[] { cm.Jobs[0].Priority, cm.Jobs[0].Name, cm.Jobs[0].Outcome }, RowTexts(cv, "JobList", 0));
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- SCADA interaction through the view

    [AvaloniaFact]
    public void The_scada_filter_and_acknowledgements_work_through_the_view()
    {
        using var temp = new TempFolder();
        (MainWindow w, _) = OpenAndRun(temp, Case("case9"), frames: 3);
        try
        {
            ScadaModel sm = w.ViewModel.Tabs.Scada;
            ScadaView sc = Show<ScadaView>(w, "SCADA", "ScadaTab");
            int all = sm.Points.Count;
            Assert.True(all > 2);

            // Typing in the filter box filters the register (the WPF TwoWay binding, per keystroke).
            TextBox box = sc.FindControl<TextBox>("FilterBox")!;
            box.Text = "SYS.F_HZ";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("SYS.F_HZ", sm.Filter);
            Assert.Equal($"1 / {all} points", sm.PointCount);
            Assert.Equal(sm.PointCount, Text(sc, "PointCountText"));
            Assert.Equal(1, sm.Points.Count(p => p.Visible));
            w.CaptureRenderedFrame()?.Dispose();
            ItemsControl list = sc.FindControl<ItemsControl>("PointList")!;
            for (int i = 0; i < all; i++)
            {
                Control? c = list.ContainerFromIndex(i);
                Button? row = c?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                if (row is not null) Assert.Equal(sm.Points[i].Visible, row.IsVisible);
            }
            box.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal($"{all} / {all} points", sm.PointCount);

            // A register row click hands its point to the host hook (none is wired in this app yet: nothing happens).
            var opened = new List<ScadaPointModel>();
            sm.Navigate = opened.Add;
            w.CaptureRenderedFrame()?.Dispose();
            Button first = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(sm.Points[0], Assert.Single(opened));
            sm.Navigate = null;

            // Put a frequency excursion on the log (an event on its onset), then ACK one row and ACK ALL through the buttons.
            GridSim.Core.Simulation.SimFrame last = w.ViewModel.Run.LastFrame!;
            sm.Update(last with { FrequencyHz = last.Model.FrequencyHz - 0.3 }, "08:00");
            Assert.NotEmpty(sm.Events);
            int before = int.Parse(sm.Unacked, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(before >= 1);
            {
                w.CaptureRenderedFrame()?.Dispose();
                ItemsControl events = sc.FindControl<ItemsControl>("EventList")!;
                Button ack = events.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().Single();
                Assert.Equal("ACK", ack.Content);
                ack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(sm.Events[0].Acked);
                Assert.Equal("ACKED", ack.Content);
                Assert.False(ack.IsEnabled);
                Assert.Equal((before - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), sm.Unacked);
            }
            sc.FindControl<Button>("AckAllButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0", sm.Unacked);
            Assert.Equal("0", Text(sc, "UnackedText"));
            Assert.All(sm.Events, e => Assert.True(e.Acked));
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the STATE NOT VALID banner and the colour converter

    [AvaloniaFact]
    public void The_token_converter_gives_the_tokens_exact_bytes_and_refuses_anything_else()
    {
        foreach (UiColour c in new[] { new UiColour(0xFF, 0x2F, 0xD2, 0x7F), new UiColour(0x00, 0xFF, 0xFF, 0xFF), ColourRamps.Loading(0.85), new UiColour(0x80, 1, 2, 3) })
        {
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(TokenBrush.Instance.Convert(c, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Col(c), brush.Color);
            Assert.Equal(1.0, brush.Opacity);
            Assert.Same(brush, TokenBrush.For(c));   // one cached brush per colour, as WPF Palette.Frozen
        }
        Assert.Throws<ArgumentException>(() => TokenBrush.Instance.Convert("#FF0000", typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Throws<ArgumentException>(() => TokenBrush.Instance.Convert(null, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => TokenBrush.Instance.ConvertBack(Brushes.Red, typeof(UiColour), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void The_state_not_valid_banner_lies_over_every_tab_while_the_dashboard_says_so()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, Case("case9")));
        w.Show();
        try
        {
            Border banner = w.FindControl<Border>("TabInvalidBanner")!;
            Assert.False(banner.IsVisible);
            w.ViewModel.Dashboard.InvalidState = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(banner.IsVisible);
            Assert.False(banner.IsHitTestVisible);
            foreach (string header in new[] { "SCADA", "OPERATOR", "MARKET", "SPEED DOMAINS", "CYCLE" })
            {
                w.TabStrip.SelectedItem = w.TabStrip.Items.OfType<TabItem>().Single(t => Equals(t.Header, header));
                Dispatcher.UIThread.RunJobs();
                Assert.True(banner.IsEffectivelyVisible, header);
            }
            Assert.Equal("STATE NOT VALID  -  the power flow did not converge; every value on this tab comes from an unconverged iterate",
                banner.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            w.ViewModel.Dashboard.InvalidState = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(banner.IsVisible);
        }
        finally { Close(w); }
    }
}
