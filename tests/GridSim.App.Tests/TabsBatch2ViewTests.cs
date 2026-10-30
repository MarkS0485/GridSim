using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Controls;
using GridSim.App.Dashboard;
using GridSim.App.Run;
using GridSim.App.Tabs;
using GridSim.Core.Licensing;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Tabs;
using GridSim.Ui.Tabs.Inspector;
using GridSim.Ui.Tabs.InverterFleet;
using GridSim.Ui.Tabs.Machine;
using GridSim.Ui.Tabs.MachineEngineer;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.6 (second tab batch): THE MACHINE, MECHANICAL (with its inspector), INVERTER FLEET and
/// GENERATION views (src/GridSim.App/Tabs) on the headless platform, each standalone in its own window and fed by a real
/// <see cref="RunViewModel"/> playing the shipped case9 through the one line its code-behind documents.
/// <list type="bullet">
/// <item><description>Feed parity: an oracle model of the same type, fed by this test with the calls the WPF dashboard's
/// tab hooks made at each run-pipeline event (Update on each frame shown, with the dashboard clock where the tab takes
/// one; Reset on each timeline break), ends in exactly the state of the view's own model: every display property and
/// every list row, compared exactly.</description></item>
/// <item><description>View parity: after the frames, every named read-out's text is the model's string (exact), every
/// coloured read-out the brush of the model's token, every instrument (gauge, sparkline, P-Q diagram, trace) carries
/// the model's numbers, and every list shows one item per model row.</description></item>
/// <item><description>The clicks: a machine row opens the inspector overlay, its back and close buttons work; the
/// GENERATION trip button runs the compare and fills the trace.</description></item>
/// </list>
/// <para>
/// Test retargeted 2026-10-20 (UI programme integration): THE MACHINE, MECHANICAL and INVERTER FLEET views no longer
/// feed themselves from the run pipeline (their Attach/Detach and TabRunFeed were removed); they show the TabModels
/// model that the dashboard's tab hooks feed, so each rig now feeds the view through <see cref="DashboardFeed"/> (a
/// DashboardHost over an AppDashboardModel that owns the TabModels, the window's own wiring) and detaches that host
/// where it detached the view. Every oracle, every model-against-oracle comparison and every view-against-model check
/// is unchanged; strengthened: each test also checks the view shows exactly the hook-fed tab model (Assert.Same), and
/// MECHANICAL that its overlay shows that model's inspector.
/// </para>
/// </summary>
public sealed class TabsBatch2ViewTests
{
    private const int Frames = 8;

    // ---------------------------------------------------------------- cases and pumping

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(System.IO.Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case9()
    {
        string file = System.IO.Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource("case9", System.IO.Path.GetDirectoryName(file)!, file);
    }

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

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The feed every tab has since the integration (2026-10-20): the main window's wiring, a <see cref="DashboardHost"/>
    /// over an <see cref="AppDashboardModel"/> that owns the <see cref="TabModels"/> and feeds them through its four tab
    /// hooks. A test shows one tab model of <see cref="Tabs"/> in its view and attaches this to the run pipeline.
    /// </summary>
    private sealed class DashboardFeed : IDisposable
    {
        public readonly TabModels Tabs = new();
        public readonly DashboardHost Host;

        public DashboardFeed() => Host = new DashboardHost(new AppDashboardModel(Tabs));

        public void Attach(RunViewModel run) => Host.Attach(run);

        public void Detach() => Host.Detach();

        public void Dispose()
        {
            Host.Detach();
            Tabs.Dispose();   // the CYCLE tab's worker, as the window disposes it on close
        }
    }

    /// <summary>
    /// A view in a window and a run pipeline at x60. <paramref name="attach"/> attaches the view's feed (since the
    /// integration, the dashboard host whose tab hooks feed it); the oracle handlers (added after it, so each runs after
    /// the feed's own) mirror the WPF tab-hook calls.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public readonly RunViewModel Run = new(new LicenceGate(Entitlement.FreeDefault));
        public readonly Window Window;
        public int Shown;
        private readonly Action? _detach;

        public Rig(Control view, Action<RunViewModel> attach, Action? detach, Action<SimFrame>? oracleFrame = null, Action? oracleBreak = null)
        {
            Window = new Window { Width = 1700, Height = 1100, Content = view };
            Window.Show();
            attach(Run);
            _detach = detach;
            Run.FrameArriving += (_, e) =>
            {
                if (e.Kind == RunFrameKind.LiveFaulted) return;
                Shown++;
                oracleFrame?.Invoke(e.Frame!);
            };
            Run.ModelSwitched += (_, _) => oracleBreak?.Invoke();
            Run.TimelineReset += (_, _) => oracleBreak?.Invoke();
            Await(Run.ChooseSpeedAsync(60));   // play at x60, as the SPEED button does (the pace only, never a number)
        }

        /// <summary>Load case9, play until <paramref name="n"/> frames were shown, then stop the loop (so the views
        /// hold still while they are read).</summary>
        public void PlayCase9(int n)
        {
            Task<string?> load = Run.LoadAsync(Case9());
            Await(load);
            Assert.Null(load.Result);   // loaded, not refused
            PumpUntil(() => Shown >= n, $"{n} frames of case9");
            Await(Run.StopLoopAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.Null(Run.FrameError);   // every frame was taken without a render fault
        }

        public void Dispose()
        {
            Await(Run.StopLoopAsync());
            _detach?.Invoke();
            Window.Close();
        }
    }

    // ---------------------------------------------------------------- model against oracle

    /// <summary>Every public property of <paramref name="actual"/> equals <paramref name="expected"/>'s: values by
    /// Equals (a double by its value, NaN equal to NaN), lists row by row, nested models (a row, the selected machine,
    /// the inspector) property by property.</summary>
    private static void AssertSameState(object? expected, object? actual, string where)
    {
        if (expected is null || actual is null) { Assert.True(expected is null && actual is null, $"{where}: '{actual}', expected '{expected}'"); return; }
        Type t = expected.GetType();
        Assert.True(t == actual.GetType(), $"{where}: a {actual.GetType().Name}, expected a {t.Name}");
        if (expected is string || t.IsPrimitive || t.IsEnum || expected is UiColour || expected is Delegate || t.IsValueType)
        {
            Assert.True(expected is Delegate ? actual is Delegate : Equals(expected, actual), $"{where}: '{actual}', expected '{expected}'");
            return;
        }
        if (expected is IEnumerable e)
        {
            var eItems = e.Cast<object?>().ToList();
            var aItems = ((IEnumerable)actual).Cast<object?>().ToList();
            Assert.True(eItems.Count == aItems.Count, $"{where}: {aItems.Count} rows, expected {eItems.Count}");
            for (int i = 0; i < eItems.Count; i++) AssertSameState(eItems[i], aItems[i], $"{where}[{i}]");
            return;
        }
        Assert.True(where.Length < 1000, $"{where}: nests too deep");
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0) AssertSameState(p.GetValue(expected), p.GetValue(actual), $"{where}.{p.Name}");
    }

    // ---------------------------------------------------------------- view against model

    private static T Find<T>(Control scope, string name) where T : Control =>
        scope.FindControl<T>(name) ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} named {name} in {scope.GetType().Name}.");

    private static void AssertTexts<TModel>(Control view, TModel model, (string Name, Func<TModel, string> Text)[] texts)
    {
        foreach ((string name, Func<TModel, string> text) in texts)
        {
            string want = text(model);
            string? got = Find<TextBlock>(view, name).Text;
            Assert.True(string.Equals(want, got, StringComparison.Ordinal), $"{name}: shows '{got}', the model says '{want}'");
        }
    }

    /// <summary>The brush a coloured read-out shows carries exactly the token's four bytes.</summary>
    private static void AssertColour(IBrush? brush, UiColour token, string what)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.True(solid.Color == Color.FromArgb(token.A, token.R, token.G, token.B),
            $"{what}: shows {solid.Color}, the model's token is {ColourRamps.ToHex(token)}");
    }

    private static void AssertItems(Control view, string name, ICollection rows) =>
        Assert.True(Find<ItemsControl>(view, name).ItemCount == rows.Count, $"{name}: {Find<ItemsControl>(view, name).ItemCount} items, the model has {rows.Count} rows");

    private static void AssertGauge(Control view, string name, string text, double value, double min, double max)
    {
        Gauge g = Find<Gauge>(view, name);
        Assert.Equal(text, g.ValueText);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(g.Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(min), BitConverter.DoubleToInt64Bits(g.Minimum));
        Assert.Equal(BitConverter.DoubleToInt64Bits(max), BitConverter.DoubleToInt64Bits(g.Maximum));
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Run the queued UI work and a layout pass, so a list's item containers exist before they are read.</summary>
    private static void Settle(Control c)
    {
        Dispatcher.UIThread.RunJobs();
        c.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- THE MACHINE

    [AvaloniaFact]
    public void The_machine_view_fed_from_a_run_shows_its_model_and_the_model_is_the_wpf_fed_one()
    {
        using var feed = new DashboardFeed();
        var view = new MachineView { Model = feed.Tabs.Machine };   // the window's binding: Tabs.Machine
        var oracle = new MachineModel();
        using var rig = new Rig(view, feed.Attach, feed.Detach, f => oracle.Update(f), oracle.Reset);
        rig.PlayCase9(Frames);
        MachineModel m = view.Model;
        Assert.Same(feed.Tabs.Machine, m);   // the tab shows the model the dashboard's hooks feed
        Assert.Same(m, view.DataContext);
        AssertSameState(oracle, m, "MachineModel");
        Assert.NotEqual(" - ", m.TotalIn);   // frames really arrived

        AssertTexts(view, m, new (string, Func<MachineModel, string>)[]
        {
            ("TotalInText", x => x.TotalIn), ("SyncSplitText", x => x.SyncSplit), ("Co2Text", x => x.Co2),
            ("FreqText", x => x.Freq), ("RpmText", x => x.Rpm), ("FreqTrendText", x => x.FreqTrend), ("InertiaText", x => x.Inertia),
            ("SpinEnergyText", x => x.SpinEnergy), ("VoltAvgText", x => x.VoltAvg), ("VoltDevHeadText", x => x.VoltDev),
            ("VoltSpreadHeadText", x => x.VoltSpread), ("VoltSpreadKvHeadText", x => x.VoltSpreadKv),
            ("TotalOutText", x => x.TotalOut), ("LossesText", x => x.Losses),
            ("TorqueDivText", x => x.TorqueDiv), ("DivMwText", x => x.DivMw), ("DivNoteText", x => x.DivNote),
            ("TimeErrorText", x => x.TimeError), ("TimeErrorNoteText", x => x.TimeErrorNote), ("AccumText", x => x.Accum),
            ("AccumNoteText", x => x.AccumNote), ("RegenMwhText", x => x.RegenMwhText), ("RegenMoneyText", x => x.RegenMoney),
            ("RegenHomesText", x => x.RegenHomes), ("BessModeText", x => x.BessMode), ("BessPowerText", x => x.BessPower),
            ("BessSocText", x => x.BessSocText), ("BessEnergyText", x => x.BessEnergy), ("BessThroughputText", x => x.BessThroughput),
            ("VoltDevText", x => x.VoltDev), ("VoltSpreadText", x => x.VoltSpread), ("VoltSpreadKvText", x => x.VoltSpreadKv),
            ("QNetText", x => x.QNet), ("QNoteText", x => x.QNote), ("QGenText", x => x.QGenText), ("QAbsText", x => x.QAbsText),
            ("TapAvgText", x => x.TapAvg), ("TapLimitsText", x => x.TapLimits), ("TapRegErrText", x => x.TapRegErr),
            ("TapSummaryText", x => x.TapSummary),
        });
        AssertColour(Find<TextBlock>(view, "TorqueDivText").Foreground, m.DivColour, "TorqueDivText");
        AssertColour(Find<TextBlock>(view, "TimeErrorText").Foreground, m.AccumColour, "TimeErrorText");
        AssertColour(Find<TextBlock>(view, "BessPowerText").Foreground, m.BessColour, "BessPowerText");
        AssertColour(Find<Border>(view, "BessSocBar").Background, m.BessColour, "BessSocBar");
        AssertColour(Find<TextBlock>(view, "VoltAvgText").Foreground, m.VoltColour, "VoltAvgText");
        AssertColour(Find<TextBlock>(view, "QNetText").Foreground, m.QColour, "QNetText");
        Assert.Equal(m.BessSocBar, Find<Border>(view, "BessSocBar").Width);

        AssertGauge(view, "GridSpeedGauge", m.Rpm, m.RpmValue, m.RpmGaugeMin, m.RpmGaugeMax);
        AssertGauge(view, "TorqueCmdGauge", m.TorqueCmdText, m.TorqueCmdValue, 0, 200);
        AssertGauge(view, "TorqueActGauge", m.TorqueActText, m.TorqueActValue, 0, 200);
        AssertGauge(view, "StressGauge", m.StressText, m.StressPct, 0, 100);
        AssertGauge(view, "SystemVoltageGauge", m.VoltAvg, m.VoltAvgValue, 0.94, 1.06);
        AssertGauge(view, "ReactiveGauge", m.QGenText, m.QGenValue, 0, m.QScaleMax);
        AssertGauge(view, "TapPositionGauge", m.TapPosText, m.TapPosValue, -10, 10);
        AssertGauge(view, "RegulationGauge", m.RegStressText, m.RegStressPct, 0, 100);
        Assert.Same(m.FreqTrace, Find<Sparkline>(view, "FreqSparkline").Values);
        Assert.Same(m.VoltTrace, Find<Sparkline>(view, "VoltSparkline").Values);
        Assert.Equal(Math.Min(rig.Shown, 120), m.FreqTrace.Count);   // one point per frame shown since the load

        AssertItems(view, "FuelInList", m.FuelIn);
        AssertItems(view, "LayersList", m.Layers);
        AssertItems(view, "DemandOutList", m.DemandOut);
        AssertItems(view, "TapsList", m.Taps);
        Assert.NotEmpty(m.FuelIn);
        Assert.NotEmpty(m.DemandOut);
    }

    [AvaloniaFact]
    public void A_case_loaded_again_resets_the_machine_tab_as_wpf_did()
    {
        // The WPF ModelChanged reset THE MACHINE's integrators first: the second load starts a fresh trace.
        using var feed = new DashboardFeed();
        var view = new MachineView { Model = feed.Tabs.Machine };
        var oracle = new MachineModel();
        using var rig = new Rig(view, feed.Attach, feed.Detach, f => oracle.Update(f), oracle.Reset);
        rig.PlayCase9(Frames);
        int before = rig.Shown;
        Task<string?> again = rig.Run.LoadAsync(Case9());
        Await(again);
        Assert.Null(again.Result);
        PumpUntil(() => rig.Shown >= before + 3, "3 frames after the reload");
        Await(rig.Run.StopLoopAsync());
        Dispatcher.UIThread.RunJobs();
        AssertSameState(oracle, view.Model, "MachineModel after a reload");
        Assert.Equal(rig.Shown - before, view.Model.FreqTrace.Count);   // the reload reset the speed trace

        // Detached, the tab keeps what it shows and takes no more frames.
        feed.Detach();
        string held = view.Model.Freq;
        int heldTicks = view.Model.FreqTrace.Count;
        int beforeThird = rig.Shown;
        Task<string?> third = rig.Run.LoadAsync(Case9());
        Await(third);
        Assert.Null(third.Result);
        PumpUntil(() => rig.Shown >= beforeThird + 3, "3 frames after a detach");
        Await(rig.Run.StopLoopAsync());
        Assert.Equal(held, view.Model.Freq);
        Assert.Equal(heldTicks, view.Model.FreqTrace.Count);
    }

    // ---------------------------------------------------------------- MECHANICAL and its inspector

    [AvaloniaFact]
    public void The_mechanical_view_fed_from_a_run_shows_its_model_and_its_inspector_opens_on_a_click()
    {
        using var feed = new DashboardFeed();
        var view = new MachineEngineerView { Model = feed.Tabs.Mechanical };   // the window's binding: Tabs.Mechanical
        var oracle = new MachineEngineerModel();
        using var rig = new Rig(view, feed.Attach, feed.Detach, f => oracle.Update(f, TabClock.Of(f)), oracle.Reset);
        rig.PlayCase9(Frames);
        MachineEngineerModel m = view.Model;
        Assert.Same(feed.Tabs.Mechanical, m);   // the tab shows the model the dashboard's hooks feed
        Assert.Same(m.Inspector, view.FindControl<InspectorView>("Inspector")!.Model);   // and that model's inspector
        Assert.Same(m, view.DataContext);
        AssertSameState(oracle, m, "MachineEngineerModel");
        Assert.NotEqual("-", m.Clock);   // frames really arrived, with the dashboard clock

        AssertTexts(view, m, new (string, Func<MachineEngineerModel, string>)[]
        {
            ("RocofNowText", x => x.RocofNow), ("RocofLegacyMarginText", x => x.RocofLegacyMargin),
            ("RocofModernMarginText", x => x.RocofModernMargin), ("SumKeText", x => x.SumKe), ("ReportedTotalText", x => x.ReportedTotal),
            ("SyntheticGapText", x => x.SyntheticGap), ("ProjInertiaText", x => x.ProjInertia), ("FleetTrendText", x => x.FleetTrend),
        });
        AssertColour(Find<TextBlock>(view, "RocofLegacyMarginText").Foreground, m.RocofLegacyColour, "RocofLegacyMarginText");
        AssertColour(Find<TextBlock>(view, "RocofModernMarginText").Foreground, m.RocofModernColour, "RocofModernMarginText");
        AssertColour(Find<ProgressBar>(view, "RocofLegacyBar").Foreground, m.RocofLegacyColour, "RocofLegacyBar");
        AssertColour(Find<TextBlock>(view, "ProjInertiaText").Foreground, m.ProjColour, "ProjInertiaText");
        Assert.Equal(m.RocofLegacyPct, Find<ProgressBar>(view, "RocofLegacyBar").Value);
        Assert.Equal(m.RocofModernPct, Find<ProgressBar>(view, "RocofModernBar").Value);
        AssertItems(view, "MachinesList", m.Machines);
        AssertItems(view, "CommittedStartsList", m.CommittedStarts);
        Assert.Single(m.CommittedStarts);

        // case9's units are untyped (not spinning plant), so the tab lists no machine and the P-Q diagram shows its
        // defaults; a machine is put in the list by hand to exercise the inspector's click path.
        PqDiagram pq = Find<PqDiagram>(view, "SelectedPq");
        if (m.Selected is MachineRowModel sel)
        {
            Assert.Equal("P-Q * " + sel.Name, Find<TextBlock>(view, "SelectedNameText").Text);
            Assert.Equal(sel.Pg, pq.Pg);
            Assert.Equal(sel.Pmax, pq.Pmax);
        }
        var row = new MachineRowModel { Name = "Test unit", Fuel = "CCGT", Mw = "400", Mvar = "-", RiskLabel = "OK", KeGvaS = "2.00", HseSec = "5.0s", RampMwMin = "+0.0", TestDate = "-" };
        m.Machines.Add(row);
        Settle(view);
        InspectorView insp = Find<InspectorView>(view, "Inspector");
        Assert.Same(m.Inspector, insp.DataContext);
        Assert.False(insp.IsVisible);

        // The row is a button: a click opens the inspector on it (WPF InspectMachineCommand).
        Button rowButton = Find<ItemsControl>(view, "MachinesList").ContainerFromIndex(m.Machines.Count - 1)!
            .GetVisualDescendants().OfType<Button>().First();
        Click(rowButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(m.Inspector.IsOpen);
        Assert.True(insp.IsVisible);
        Assert.Equal("TEST UNIT", m.Inspector.Title);
        AssertTexts(insp, m.Inspector, new (string, Func<InspectorModel, string>)[] { ("TitleText", x => x.Title), ("SubtitleText", x => x.Subtitle) });
        AssertItems(insp, "StatsList", m.Inspector.Stats);
        AssertItems(insp, "RowsList", m.Inspector.Rows);
        Assert.Equal(4, m.Inspector.Stats.Count);
        Assert.Equal(11, m.Inspector.Rows.Count);
        Assert.False(Find<Button>(insp, "BackButton").IsVisible);   // one page: no back

        // A second inspect pushes a page: BACK shows; BACK returns; BACK again (nothing behind) closes; x closes.
        m.InspectMachine(row);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(insp, "BackButton").IsVisible);
        Click(Find<Button>(insp, "BackButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(m.Inspector.IsOpen);
        Assert.False(m.Inspector.CanBack);
        Click(Find<Button>(insp, "BackButton"));   // WPF: Back with an empty stack closes (the button is hidden then)
        Dispatcher.UIThread.RunJobs();
        Assert.False(m.Inspector.IsOpen);
        Assert.False(insp.IsVisible);
        m.InspectMachine(row);
        Click(Find<Button>(insp, "CloseButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(m.Inspector.IsOpen);
    }

    [AvaloniaFact]
    public void An_inspector_row_click_drills_and_its_tiles_show_their_colours()
    {
        var model = new InspectorModel();
        var view = new InspectorView { Model = model };
        var window = new Window { Width = 800, Height = 800, Content = view };
        window.Show();
        try
        {
            UiColour green = ColourRamps.FromHex("#2FD27F");
            int drilled = 0;
            model.Show("FUEL", "sub", new[] { model.CreateStat("INERTIA", "1.5 GVA.s", green) },
                new[] { model.CreateRow("unit", "10 MW", green, green, onDrill: () => drilled++), model.CreateRow("plain", "-", DashboardColours.Transparent, InspectStatModel.White) });
            Settle(view);
            Assert.True(view.IsVisible);
            ItemsControl rows = Find<ItemsControl>(view, "RowsList");
            Click(rows.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First());
            Click(rows.ContainerFromIndex(1)!.GetVisualDescendants().OfType<Button>().First());   // a plain row does nothing
            Assert.Equal(1, drilled);
            // The chevron shows only on the row that drills; the tile's value is painted in its token.
            TextBlock Chevron(int i) => rows.ContainerFromIndex(i)!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == ">");
            Assert.True(Chevron(0).IsVisible);
            Assert.False(Chevron(1).IsVisible);
            TextBlock tileValue = Find<ItemsControl>(view, "StatsList").ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "1.5 GVA.s");
            AssertColour(tileValue.Foreground, green, "the tile value");
        }
        finally { window.Close(); }
    }

    // ---------------------------------------------------------------- INVERTER FLEET

    [AvaloniaFact]
    public void The_inverter_fleet_view_fed_from_a_run_shows_its_model()
    {
        using var feed = new DashboardFeed();
        var view = new InverterFleetView { Model = feed.Tabs.InverterFleet };   // the window's binding: Tabs.InverterFleet
        var oracle = new InverterFleetModel();
        using var rig = new Rig(view, feed.Attach, feed.Detach, f => oracle.Update(f), () => { });
        rig.PlayCase9(Frames);
        InverterFleetModel m = view.Model;
        Assert.Same(feed.Tabs.InverterFleet, m);   // the tab shows the model the dashboard's hooks feed
        AssertSameState(oracle, m, "InverterFleetModel");
        // case9 ships no asset-class library: the tab reads OFF, as in WPF.
        Assert.False(m.Active);
        Assert.StartsWith("Inverter-fleet dynamics are OFF", m.Status, StringComparison.Ordinal);
        AssertTexts(view, m, new (string, Func<InverterFleetModel, string>)[]
        {
            ("StatusText", x => x.Status), ("NetFleetMwText", x => x.NetFleetMw), ("ClassCountText", x => x.ClassCount),
            ("TotalUnitsText", x => x.TotalUnits), ("TotalTrippedText", x => x.TotalTripped), ("LomWarningText", x => x.LomWarning),
            ("ProvenanceText", x => x.Provenance),
        });
        AssertColour(Find<TextBlock>(view, "TotalTrippedText").Foreground, m.TrippedColour, "TotalTrippedText");
        AssertItems(view, "ClassesList", m.Classes);

        // A fleet frame (the shape LiveSim gives a case with an asset-class library) fills the table and the warning.
        SimFrame f = rig.Run.LastFrame!;
        m.Update(f with
        {
            AssetFleet = new[] { new GridSim.Core.Assets.AssetClassRollup("Embedded PV", "grid-following", 4000, 812.25, 37, 0), new GridSim.Core.Assets.AssetClassRollup("BESS", "grid-forming", 12, -45.0, 0, 63.4) },
            AssetFleetNetMw = -37.5,
            AssetTrippedMw = 12.3,
        });
        Dispatcher.UIThread.RunJobs();
        Assert.True(m.Active);
        AssertTexts(view, m, new (string, Func<InverterFleetModel, string>)[]
        {
            ("StatusText", x => x.Status), ("NetFleetMwText", x => x.NetFleetMw), ("ClassCountText", x => x.ClassCount),
            ("TotalUnitsText", x => x.TotalUnits), ("TotalTrippedText", x => x.TotalTripped), ("LomWarningText", x => x.LomWarning),
        });
        AssertColour(Find<TextBlock>(view, "TotalTrippedText").Foreground, InverterFleetModel.Alarm, "TotalTrippedText");
        AssertItems(view, "ClassesList", m.Classes);
    }

    // ---------------------------------------------------------------- GENERATION

    [AvaloniaFact]
    public void The_generation_view_shares_the_dashboard_model_and_its_trip_button_fills_the_trace()
    {
        // The documented line: the tab shows the dashboard's own model, which DashboardView.Attach(run) feeds.
        var dashboard = new DashboardView();
        var view = new GenerationView();
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        panel.Children.Add(dashboard);
        Grid.SetColumn(view, 1);
        panel.Children.Add(view);
        using var rig = new Rig(panel, run => { dashboard.Attach(run); view.Model = dashboard.Model; }, dashboard.Detach);
        rig.PlayCase9(Frames);
        DashboardModel m = view.Model;
        Assert.Same(dashboard.Model, m);
        Assert.Same(m, view.DataContext);
        Assert.NotEqual(" - ", m.GenTotal);   // frames really arrived

        (string, Func<DashboardModel, string>)[] texts =
        {
            ("GenTotalText", x => x.GenTotal), ("InertiaGvaText", x => x.InertiaGva), ("SyncShareText", x => x.SyncShare),
            ("Co2Text", x => x.Co2), ("NetImportText", x => x.NetImport), ("RpmDialLowText", x => x.RpmDialLowText),
            ("RpmDialHighText", x => x.RpmDialHighText), ("RpmText", x => x.Rpm), ("TorqueText", x => x.Torque + " MNm torque"),
            ("SyncMwText", x => x.SyncMw), ("ConverterMwText", x => x.ConverterMw), ("TotalMixText", x => x.GenTotal + " GW"),
            ("FossilMwText", x => x.FossilMw), ("RenewableMwText", x => x.RenewableMw), ("StabilityNoteText", x => x.StabilityNote),
            ("TsgbInertiaText", x => x.TsgbInertiaGva.ToString("F0") + " GVAs"), ("TsgbFfrText", x => x.TsgbFfrGw.ToString("F1") + " GW"),
            ("TripBaselineText", x => x.TripBaseline), ("TripTsgbText", x => x.TripTsgb), ("TripVerdictText", x => x.TripVerdict),
            ("TripAssessmentText", x => x.TripAssessment), ("TripSizingText", x => x.TripSizing),
        };
        AssertTexts(view, m, texts);
        AssertColour(Find<Ellipse>(view, "StabilityLamp").Fill, m.StabilityColour, "StabilityLamp");
        AssertItems(view, "FuelMixList", m.FuelMix);
        AssertItems(view, "InertiaByClassList", m.FuelMix);
        AssertItems(view, "ImportLinksList", m.ImportLinks);
        Assert.NotEmpty(m.FuelMix);
        FrequencyTrace trace = Find<FrequencyTrace>(view, "FreqTrace");
        Assert.Null(trace.Baseline);   // no compare yet

        // The sliders write the device size into the model (WPF TwoWay), then the button runs the compare.
        Find<Slider>(view, "TsgbInertiaSlider").Value = 75;
        Find<Slider>(view, "TsgbFfrSlider").Value = 3.5;
        Assert.Equal(75, m.TsgbInertiaGva);
        Assert.Equal(3.5, m.TsgbFfrGw);
        Click(Find<Button>(view, "TripButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(m.BaselineResponse);
        Assert.Same(m.BaselineResponse, trace.Baseline);
        Assert.Same(m.TsgbResponse, trace.Tsgb);
        Assert.Equal(m.TripBands, trace.Bands);
        Assert.NotEqual("Run a largest-unit trip to compare.", m.TripBaseline);
        AssertTexts(view, m, texts);
        AssertColour(Find<TextBlock>(view, "TripVerdictText").Foreground, m.TripVerdictColour, "TripVerdictText");
        Assert.StartsWith($"{m.Clock}   TSGB compare", m.Events[0], StringComparison.Ordinal);   // logged on the dashboard
    }
}
