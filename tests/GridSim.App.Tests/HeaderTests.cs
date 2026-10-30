using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Dialogs;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.App.Tabs;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.7: the rest of the WPF header (src/GridSim.Wpf/MainWindow.xaml, row 0) in the Avalonia window.
/// <list type="bullet">
/// <item><description>The NETWORK source picker lists the model catalogue and choosing a source loads exactly that
/// source through the window's one load path (WPF SourcePicker_SelectionChanged -> SwitchTo); a case loaded another way
/// moves the picker without a second load; a run folder joins the list once.</description></item>
/// <item><description>The substation search box: Enter centres the map on the first substation whose name contains the
/// text (at the focus zoom) and selects it, as the WPF Search_KeyDown; no match writes the WPF line to the event log.
/// While it has the focus the single-key map shortcuts and '.' / ',' stand aside (the typing guard).</description></item>
/// <item><description>LOAD RUN... and AUDIT LOG... run the File menu's own commands; the SOLVER picker sits in the header
/// at its WPF place, and the header's controls are in the WPF order.</description></item>
/// </list>
/// </summary>
public sealed class HeaderTests
{
    // ---------------------------------------------------------------- helpers

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Matpower(string name)
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource(name, Path.GetDirectoryName(file)!, file);
    }

    private static ModelSource Uk()
    {
        string dir = Path.Combine(RepoRoot(), "data", "uk");
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), "missing test case " + dir);
        return new ModelSource("GB reduced  10-zone", dir);
    }

    private static ShellOptions Options(TempFolder temp, IReadOnlyList<ModelSource> catalogue, Func<MainWindow, IShellActions>? actions = null) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => catalogue.ToList(),
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        Actions = actions,
    };

    /// <summary>A two-frame gda-replay style run of case9 (as ReplayClarityTests makes it).</summary>
    private static string TwoFrameRun(TempFolder temp)
    {
        string dir = Path.Combine(temp.Path, "gda-replay-header");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int idx in new[] { 0, 3 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", $"state-{idx:D6}.json"), model, r, trace, meta);
        }
        return dir;
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

    private static void Press(Window w, Key key)
    {
        w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // Load source through the window, wait for two accepted frames, pause, and render once (the map is then fitted).
    private static void LoadAndSettle(MainWindow w, ModelSource source)
    {
        RunViewModel run = w.ViewModel.Run;
        int frames = 0;
        void Count(object? s, LivePanelText t) => frames++;
        run.FrameShown += Count;
        Await(w.ViewModel.LoadCaseAsync(source));
        Assert.False(run.HasError, run.ErrorText);
        Await(w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
        PumpUntil(() => frames >= 2, "two frames");
        Await(w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync());
        PumpFor(150);
        run.FrameShown -= Count;
        w.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- the header's layout

    [AvaloniaFact]
    public void The_header_holds_the_WPF_controls_in_the_WPF_order()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, new[] { Matpower("case9") }));
        w.Show();
        try
        {
            var header = w.FindControl<Border>("HeaderBar")!;
            var dock = (DockPanel)header.Child!;
            Control[] kids = dock.Children.ToArray();
            int At(string name) => Array.FindIndex(kids, c => c.Name == name);
            string[] wpfOrder = { "SourcePicker", "SolverPicker", "ActiveSolverText", "SearchBox", "LoadRunButton", "AuditLogButton" };
            int[] at = wpfOrder.Select(At).ToArray();
            for (int i = 0; i < at.Length; i++) Assert.True(at[i] >= 0, $"{wpfOrder[i]} is not in the header");
            for (int i = 1; i < at.Length; i++) Assert.True(at[i - 1] < at[i], $"{wpfOrder[i - 1]} is not before {wpfOrder[i]}");
            Assert.All(at, i => Assert.Equal(Dock.Left, DockPanel.GetDock(kids[i])));

            // The WPF labels and sizes.
            Assert.Contains(kids, c => c is TextBlock { Text: "NETWORK" });
            Assert.Contains(kids, c => c is TextBlock { Text: "SOLVER" });
            Assert.Contains(kids, c => c is TextBlock { Text: " find" });
            Assert.Equal(200, w.FindControl<ComboBox>("SourcePicker")!.Width);
            Assert.Equal(150, w.FindControl<ComboBox>("SolverPicker")!.Width);
            Assert.Equal(150, w.FindControl<TextBox>("SearchBox")!.Width);
            Assert.Equal(" LOAD RUN...", w.FindControl<Button>("LoadRunButton")!.Content);
            Assert.Equal(" AUDIT LOG...", w.FindControl<Button>("AuditLogButton")!.Content);
            Assert.Equal("Type a substation name, press Enter", ToolTip.GetTip(w.FindControl<TextBox>("SearchBox")!));
            // The SOLVER picker is no longer on the transport bar.
            ComboBox solver = w.FindControl<ComboBox>("SolverPicker")!;
            Assert.DoesNotContain(solver.GetVisualAncestors().OfType<Border>(), b => b.Name == "TransportBar");
            Assert.Same(header, solver.FindAncestorOfType<Border>());
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the NETWORK picker

    [AvaloniaFact]
    public void The_source_picker_lists_the_catalogue_and_choosing_a_source_loads_that_source()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        var w = new MainWindow(Options(temp, new[] { case9, case14 }));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            ComboBox picker = w.FindControl<ComboBox>("SourcePicker")!;
            Assert.Equal(new[] { "case9", "case14" }, vm.Sources.Select(s => s.Label));
            Assert.Same(vm.Sources, picker.ItemsSource);
            Assert.Null(picker.SelectedItem);          // nothing chosen yet, nothing loaded
            Assert.False(vm.Run.HasRun);

            int loads = 0;
            vm.Run.ModelLoaded += (_, _) => loads++;

            // Choosing case14 in the picker loads case14, once, through the window's load path.
            picker.SelectedItem = vm.Sources[1];
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(vm.PendingSourceLoad);
            Await(vm.PendingSourceLoad!);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.Equal(1, loads);
            Assert.True(vm.ChosenCase!.SameSourceAs(case14));
            Assert.Equal("case14", vm.Run.CaseName);
            Assert.Equal(14, vm.Run.LoadedModel!.BusCount);
            Assert.StartsWith("Loaded case14  -  14 buses, ", vm.Run.LoadedText, StringComparison.Ordinal);
            Assert.Equal(vm.Run.LoadedText, vm.StatusText);
            Assert.True(vm.Settings.LastSourcePath == case14.Key);   // remembered as WPF remembers it

            // Choosing the source already loaded does nothing (WPF: reselecting fires nothing).
            Task? pending = vm.PendingSourceLoad;
            picker.SelectedItem = vm.Sources[1];
            Dispatcher.UIThread.RunJobs();
            Assert.Same(pending, vm.PendingSourceLoad);
            Assert.Equal(1, loads);

            // A case loaded another way (File > Open case... loads through LoadCaseAsync) moves the picker onto it,
            // without loading it a second time.
            Await(vm.LoadCaseAsync(case9));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, loads);
            Assert.Same(vm.Sources[0], picker.SelectedItem);
            Assert.Same(pending, vm.PendingSourceLoad);

            // A run folder (LOAD RUN... / Open run folder...) joins the list once, labelled with its frames, and is
            // selected; opening it again adds nothing.
            string run = TwoFrameRun(temp);
            var actions = Assert.IsType<ShellActions>(w.Actions);
            Await(actions.OpenRunFolderAtAsync(run));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, loads);
            Assert.True(vm.Run.IsReplay);
            Assert.Equal(3, vm.Sources.Count);
            ModelSource replay = ModelCatalog.ReplaySource(run);
            Assert.True(vm.Sources[2].SameSourceAs(replay));
            Assert.Same(vm.Sources[2], picker.SelectedItem);
            Assert.Equal($"{replay.Label}  (pre-solved run, 2 frames)", CaseListLabel.Text(vm.Sources[2]));
            Await(actions.OpenRunFolderAtAsync(run));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.Sources.Count);

            // And back to a live case from the picker: loaded live, the replay left behind.
            picker.SelectedItem = vm.Sources[1];
            Dispatcher.UIThread.RunJobs();
            Await(vm.PendingSourceLoad!);
            Assert.False(vm.Run.IsReplay);
            Assert.Equal("case14", vm.Run.CaseName);
            Assert.Equal(5, loads);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the search box

    [AvaloniaFact]
    public void The_search_box_centres_and_selects_the_right_bus_on_Enter_and_logs_a_miss()
    {
        using var temp = new TempFolder();
        ModelSource uk = Uk();
        var w = new MainWindow(Options(temp, new[] { uk }));
        w.Show();
        try
        {
            LoadAndSettle(w, uk);
            MainWindowViewModel vm = w.ViewModel;
            SchematicControl map = w.MapControl;
            SchematicView view = map.View;

            // A named substation that no earlier node's name contains, so the first match is that bus (the WPF rule:
            // the first node in map order whose name contains the text, ignoring case).
            int index = -1;
            for (int i = 0; i < view.Nodes.Count && index < 0; i++)
            {
                string? n = view.Nodes[i].Name;
                if (string.IsNullOrWhiteSpace(n) || i == 0) continue;
                bool earlier = false;
                for (int j = 0; j < i; j++)
                    if (view.Nodes[j].Name is { } m && m.Contains(n, StringComparison.OrdinalIgnoreCase)) earlier = true;
                if (!earlier) index = i;
            }
            Assert.True(index > 0, "no searchable substation in the reduced GB case");
            SchematicNode target = view.Nodes[index];
            Bus bus = vm.Run.LastFrame!.Model.Buses.Single(b => b.Id == target.Id);

            // Type the name in lower case into the search box (as a user types it) and press Enter.
            TextBox box = w.FindControl<TextBox>("SearchBox")!;
            Assert.True(box.Focus());
            Dispatcher.UIThread.RunJobs();
            w.KeyTextInput(target.Name!.ToLowerInvariant());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(target.Name!.ToLowerInvariant(), box.Text);
            Press(w, Key.Enter);

            // Selected: the map's selection and the dashboard's fly-out (WPF OnNodeClicked).
            Assert.Equal(new MapSelection(MapSelectionKind.Bus, target.Id), vm.Map.Selection);
            Assert.True(vm.Dashboard.SelectionVisible);
            Assert.Equal(bus.Name, vm.Dashboard.SelName);
            Assert.Equal(bus.BaseKv >= 200 ? "EHV SUBSTATION" : "SUBSTATION", vm.Dashboard.SelKind);
            // Centred at the focus zoom (WPF FocusNode): the bus sits in the middle of the map.
            Assert.Equal(view.FocusZoom, view.Zoom);
            ScreenPoint s = view.NodeScreen(index);
            Assert.True(Math.Abs(s.X - view.ViewportWidth / 2) < 1e-6, $"x {s.X} vs {view.ViewportWidth / 2}");
            Assert.True(Math.Abs(s.Y - view.ViewportHeight / 2) < 1e-6, $"y {s.Y} vs {view.ViewportHeight / 2}");

            // A search that matches nothing: the WPF line in the event log at the dashboard clock, nothing else moves.
            const string none = "zz-no-such-substation-zz";
            Assert.Null(w.SearchSubstation(none));
            Assert.Equal($"{vm.Dashboard.Clock}  No substation matching '{none}'", vm.Dashboard.Events[0]);
            Assert.Equal($"No substation matching '{none}'", MainWindow.NoSubstationText(none));
            Assert.Equal(new MapSelection(MapSelectionKind.Bus, target.Id), vm.Map.Selection);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_single_key_shortcuts_stand_aside_while_the_search_box_has_the_focus()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9");
        var w = new MainWindow(Options(temp, new[] { case9 }));
        w.Show();
        try
        {
            LoadAndSettle(w, case9);
            MainWindowViewModel vm = w.ViewModel;
            SchematicControl map = w.MapControl;
            TextBox box = w.FindControl<TextBox>("SearchBox")!;
            Assert.True(box.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.True(w.IsTyping());

            bool minimap = map.ShowMinimap, coast = map.ShowCoast;
            string status = vm.StatusText;
            int events = vm.Dashboard.Events.Count;
            var step = vm.Run.SelectedStep;
            map.ZoomAt(new Point(100, 100), 3);
            double zoom = map.View.Zoom;
            foreach (Key key in new[] { Key.M, Key.C, Key.T, Key.F, Key.OemPeriod, Key.OemComma })
                Press(w, key);
            Assert.Equal(minimap, map.ShowMinimap);       // M did not toggle the minimap
            Assert.Equal(coast, map.ShowCoast);           // C did not toggle the coast
            Assert.Equal(status, vm.StatusText);          // T did not trip
            Assert.Equal(events, vm.Dashboard.Events.Count);
            Assert.Equal(zoom, map.View.Zoom);            // F did not refit
            Assert.Same(step, vm.Run.SelectedStep);       // '.' and ',' did not move the step

            // A function key is not guarded: F5 still plays and pauses from the search box.
            bool paused = vm.Run.IsPaused;
            Press(w, Key.F5);
            Assert.NotEqual(paused, vm.Run.IsPaused);
            Press(w, Key.F5);
            Assert.Equal(paused, vm.Run.IsPaused);

            // The focus back on the map: the same keys act again.
            Assert.True(map.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.False(w.IsTyping());
            Press(w, Key.M);
            Assert.Equal(!minimap, map.ShowMinimap);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- LOAD RUN... and AUDIT LOG...

    [AvaloniaFact]
    public void LOAD_RUN_and_AUDIT_LOG_run_the_File_menus_commands()
    {
        using var temp = new TempFolder();
        var recorder = new RecordingShellActions();
        var w = new MainWindow(Options(temp, new[] { Matpower("case9") }, _ => recorder));
        w.Show();
        try
        {
            Click(w.FindControl<Button>("LoadRunButton")!);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "OpenRunFolder" }, recorder.Calls);   // File > Open run folder...'s own action

            Assert.Empty(w.OwnedWindows.OfType<AuditLogWindow>());
            Click(w.FindControl<Button>("AuditLogButton")!);
            Dispatcher.UIThread.RunJobs();
            AuditLogWindow viewer = Assert.Single(w.OwnedWindows.OfType<AuditLogWindow>());   // File > Audit log...'s viewer
            Assert.True(viewer.IsVisible);
            viewer.Close();
            Assert.Null(w.ViewModel.ProblemText);
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940, true)]    // the window's default size: the whole header fits, nothing is squeezed
    [InlineData(1440, 860, false)]   // narrower: the clock and the lamp still show whole; the left-hand group gives way
    public void The_clock_and_status_lamp_are_never_drawn_over_and_the_header_fits_the_default_window(double width, double height, bool allFit)
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, new[] { Uk() })) { Width = width, Height = height };
        w.Show();
        try
        {
            // A loaded case, so the engine-in-use text has its longest everyday form ("auto * Newton-Raphson (dense)").
            w.ViewModel.SelectedSource = w.ViewModel.Sources[0];
            PumpUntil(() => w.ViewModel.Run.ActiveSolverText.StartsWith("auto * ", StringComparison.Ordinal), "the case to load");
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var dock = (DockPanel)w.FindControl<Border>("HeaderBar")!.Child!;
            Control clock = w.FindControl<StackPanel>("HeaderClockBox")!, lamp = w.FindControl<Border>("HeaderStatusBox")!;
            Control[] kids = dock.Children.Where(c => c.IsVisible).ToArray();

            // Each control's natural width (measured with unlimited room, without its margin), read once and then the
            // header laid out again as the window has it: a control squeezed by the DockPanel also ASKS for less, so its
            // own DesiredSize cannot say whether it is whole.
            var arranged = kids.ToDictionary(c => c, c => c.Bounds);
            var natural = new Dictionary<Control, double>();
            foreach (Control c in kids)
            {
                c.Measure(Size.Infinity);
                natural[c] = c.DesiredSize.Width - c.Margin.Left - c.Margin.Right;
            }
            dock.InvalidateMeasure();
            w.UpdateLayout();
            Assert.All(kids, c => Assert.Equal(arranged[c], c.Bounds));   // the layout is back as it was

            foreach (Control right in new[] { clock, lamp })
            {
                // Whole: arranged at least as wide as its natural width, and inside the header.
                Assert.True(right.Bounds.Width >= natural[right] - 0.5,
                    $"{right.Name} {width}x{height}: {right.Bounds.Width} wide, needs {natural[right]}");
                Assert.True(right.Bounds.Left >= 0 && right.Bounds.Right <= dock.Bounds.Width + 0.5, $"{right.Name} is outside the header");
            }
            Assert.False(clock.Bounds.Intersects(lamp.Bounds), "the clock and the lamp overlap");

            // No other header control reaches into them (a squeezed control is arranged, and clipped, to what is left).
            foreach (Control c in kids.Where(c => c != clock && c != lamp))
            {
                Assert.False(c.Bounds.Width > 0 && (c.Bounds.Intersects(clock.Bounds) || c.Bounds.Intersects(lamp.Bounds)),
                    $"{c.Name ?? c.GetType().Name} {width}x{height} at {c.Bounds} overlaps the clock {clock.Bounds} or the lamp {lamp.Bounds}");
                if (allFit)
                    Assert.True(c.Bounds.Width >= natural[c] - 0.5,
                        $"{c.Name ?? c.GetType().Name} is squeezed at the default size: {c.Bounds.Width} of {natural[c]}");
            }
        }
        finally { Close(w); }
    }
}
