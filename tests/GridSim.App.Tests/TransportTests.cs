using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Map;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Commands;
using GridSim.Ui.Geography;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.7: the rest of the WPF transport bar (src/GridSim.Wpf/MainWindow.xaml, row 2) in the Avalonia
/// window, and the Run menu's ticks.
/// <list type="bullet">
/// <item><description>COLOR, FLEET, DNO and GSP run the View menu's own commands and show their state as WPF does
/// (COLOR's text; 1.0 / 0.55 opacity; DNO and GSP disabled without geography), whichever way the command ran.</description></item>
/// <item><description>RESTART, STEP BACK and the scrubber move the replay cursor exactly as the host's ReplayRestart,
/// ReplayRewind and ReplaySeek move a second host loaded on the same run, and restart the timeline exactly where WPF
/// calls ResetTimeline (restart, step back, a backward scrub or a scrub to frame 0; never a forward scrub). They are
/// shown only for a replay. A shorter run opened after a longer one opens on frame 1 (the WPF scrubber trap).</description></item>
/// <item><description>The Run menu's SOLVER, STEP and SPEED items tick the choice in force, wherever it was made.</description></item>
/// </list>
/// </summary>
public sealed class TransportTests
{
    // ---------------------------------------------------------------- helpers

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ModelSource Case9()
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource("case9", Path.GetDirectoryName(file)!, file);
    }

    private static ModelSource Uk()
    {
        string dir = Path.Combine(RepoRoot(), "data", "uk");
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), "missing test case " + dir);
        return new ModelSource("GB reduced  10-zone", dir);
    }

    private static ShellOptions Options(TempFolder temp, Func<ICollection<string>, GeographyData?>? geography = null) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource>(),
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
        AutoLoadAtStart = false,
        LoadGeography = geography ?? (_ => null),
    };

    /// <summary>A gda-replay style run of case9 with <paramref name="frames"/> exported states (state-000000 ...).</summary>
    private static string Run(TempFolder temp, string name, int frames)
    {
        string dir = Path.Combine(temp.Path, name);
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        for (int idx = 0; idx < frames; idx++)
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

    private static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window w, Key key)
    {
        w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem TopMenu(MainWindow w, string header) =>
        w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);

    private static MenuItem Item(MenuItem menu, string header) =>
        menu.Items.OfType<MenuItem>().SingleOrDefault(i => (string?)i.Header == header)
        ?? throw new Xunit.Sdk.XunitException($"no item {header} in menu {menu.Header}");

    // Load a live case, wait for two frames and pause (the map then holds still).
    private static void LoadLiveAndPause(MainWindow w, ModelSource source)
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
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- COLOR, FLEET, DNO, GSP

    [AvaloniaFact]
    public void COLOR_and_FLEET_run_their_View_commands_and_show_their_state_and_DNO_GSP_are_off_without_geography()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp));
        w.Show();
        try
        {
            LoadLiveAndPause(w, Case9());
            MainWindowViewModel vm = w.ViewModel;
            SchematicControl map = w.MapControl;
            Button color = w.FindControl<Button>("ColorButton")!, fleet = w.FindControl<Button>("FleetButton")!;
            Button dno = w.FindControl<Button>("DnoButton")!, gsp = w.FindControl<Button>("GspRegionButton")!;
            Assert.True(color.IsEffectivelyVisible);
            MenuItem colourBy = Item(TopMenu(w, "_View"), "Colour by");

            // COLOR cycles LOADING -> VOLTAGE -> CONDITION -> STRESS -> LOADING, each press the View menu's item for that
            // mode (the map, the legend, the setting and the menu tick all follow), and names the mode as WPF does.
            Assert.Equal(MapColourMode.Loading, map.ColorMode);
            Assert.Equal(" LOADING", color.Content);
            foreach ((MapColourMode mode, string label, string title) in new[]
            {
                (MapColourMode.Voltage, " VOLTAGE", "VOLTAGE"), (MapColourMode.Condition, " CONDITION", "CONDITION"),
                (MapColourMode.Stress, " STRESS", "STRESS"), (MapColourMode.Loading, " LOADING", "LOADING"),
            })
            {
                Click(color);
                Assert.Equal(mode, map.ColorMode);
                Assert.Equal(label, color.Content);
                Assert.Equal(mode == MapColourMode.Voltage, vm.Settings.ColorVoltage);
                Assert.True(Item(colourBy, title).IsChecked);
                Assert.Equal(1, colourBy.Items.OfType<MenuItem>().Count(i => i.IsChecked));
                Assert.True(vm.SettingsChanged);
            }
            // The menu item moves the button too (one command, two ways in).
            Await(vm.Registry.Get(MapCommands.ColourById(MapColourMode.Stress)).RunAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(" STRESS", color.Content);

            // FLEET: the View menu's Fleet toggle; 0.55 while the fleet is hidden, 1.0 while shown, whichever way it ran.
            MenuItem fleetItem = Item(TopMenu(w, "_View"), "Fleet");
            Assert.False(map.ShowFleet);
            Assert.Equal(0.55, fleet.Opacity);
            Click(fleet);
            Assert.True(map.ShowFleet);
            Assert.True(vm.Settings.Fleet);
            Assert.Equal(1.0, fleet.Opacity);
            Assert.True(fleetItem.IsChecked);
            Await(vm.Registry.Get(MapCommands.Fleet).RunAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.False(map.ShowFleet);
            Assert.Equal(0.55, fleet.Opacity);
            Assert.False(fleetItem.IsChecked);

            // No local geography: DNO and GSP are disabled and dimmed, as their commands and keys are (WPF).
            Assert.False(map.HasGeography);
            Assert.False(dno.IsEnabled);
            Assert.False(gsp.IsEnabled);
            Assert.Equal(0.55, dno.Opacity);
            Assert.Equal(0.55, gsp.Opacity);
            Assert.False(vm.Registry.Get(MapCommands.DnoAreas).IsEnabled);
            Assert.Null(vm.ProblemText);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void With_geography_DNO_and_GSP_toggle_their_layers_and_follow_the_D_and_R_keys()
    {
        using var temp = new TempFolder();
        string geo = Path.Combine(temp.Path, "gda-out", "geo");
        Directory.CreateDirectory(geo);
        const string square = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"area\":\"A\",\"dno\":\"D\",\"gsps\":\"G\",\"gspgroup\":\"_A\"},"
            + "\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[-3,52],[-2,52],[-2,53],[-3,53],[-3,52]]]}}]}";
        File.WriteAllText(Path.Combine(geo, "dno_areas.geojson"), square);
        File.WriteAllText(Path.Combine(geo, "gsp_regions.geojson"), square);
        var w = new MainWindow(Options(temp, problems => GeographyData.Load(temp.Path, problems)));
        w.Show();
        try
        {
            LoadLiveAndPause(w, Uk());
            MainWindowViewModel vm = w.ViewModel;
            SchematicControl map = w.MapControl;
            Assert.True(map.HasGeography);
            Button dno = w.FindControl<Button>("DnoButton")!, gsp = w.FindControl<Button>("GspRegionButton")!;
            MenuItem view = TopMenu(w, "_View");

            foreach ((Button button, Key key, Func<bool> shown, string item) in new (Button, Key, Func<bool>, string)[]
            {
                (dno, Key.D, () => map.ShowDnoAreas, "DNO areas"), (gsp, Key.R, () => map.ShowGspRegions, "GSP regions"),
            })
            {
                Assert.True(button.IsEnabled);
                Assert.False(shown());
                Assert.Equal(0.55, button.Opacity);
                Click(button);
                Assert.True(shown());
                Assert.Equal(1.0, button.Opacity);
                Assert.True(Item(view, item).IsChecked);
                // The key flips it back, and the button follows.
                Assert.True(map.Focus());
                Dispatcher.UIThread.RunJobs();
                Press(w, key);
                Assert.False(shown());
                Assert.Equal(0.55, button.Opacity);
                Assert.False(Item(view, item).IsChecked);
            }
            Assert.Null(vm.ProblemText);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- RESTART, STEP BACK, the scrubber

    [AvaloniaFact]
    public void Restart_step_back_and_the_scrubber_move_the_cursor_as_the_host_does_and_reset_the_timeline_as_WPF()
    {
        using var temp = new TempFolder();
        string dir = Run(temp, "gda-replay-five", 5);
        var w = new MainWindow(Options(temp));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            RunViewModel run = vm.Run;
            var actions = Assert.IsType<ShellActions>(w.Actions);
            Await(actions.OpenRunFolderAtAsync(dir));
            Assert.True(run.IsReplay);
            Assert.True(run.IsPaused);

            // The oracle: a second host on the same run, moved by the same SimHost methods.
            var oracle = new SimHost();
            Assert.True(oracle.LoadReplay(dir).Loaded);

            Button restart = w.FindControl<Button>("RestartButton")!, back = w.FindControl<Button>("StepBackButton")!;
            Slider scrubber = w.FindControl<Slider>("Scrubber")!;
            TextBlock readout = w.FindControl<TextBlock>("FrameReadout")!;
            // Shown for a replay, with the WPF texts; the STEP picker is not (live only).
            Assert.True(restart.IsVisible && back.IsVisible && scrubber.IsVisible && readout.IsVisible);
            Assert.False(w.FindControl<ComboBox>("StepPicker")!.IsVisible);
            Assert.Equal(((char)0x21BA).ToString(), restart.Content);   // the WPF RESTART glyph (anticlockwise open circle arrow)
            Assert.Equal(((char)0x25C0).ToString(), back.Content);    // the WPF STEP BACK glyph (black left-pointing triangle)
            Assert.Equal(0, scrubber.Minimum);
            Assert.Equal(4, scrubber.Maximum);
            Assert.Equal(0, scrubber.Value);

            // Record every timeline reset and the order of resets and frames.
            var events = new List<string>();
            run.TimelineReset += (_, _) => events.Add("reset");
            run.FrameArriving += (_, _) => events.Add("frame");

            void Check(ReplayPosition expected, bool reset, string what)
            {
                Dispatcher.UIThread.RunJobs();
                ReplayCursor got = run.Host.ReplayCursor!;
                Assert.True(expected.Cursor == got, $"{what}: cursor {got} but the host's own move gives {expected.Cursor}");
                Assert.Equal(expected.Cursor.Index, (int)scrubber.Value);
                Assert.Equal(expected.Cursor.Index, (int)run.ScrubberValue);
                Assert.Equal($"frame {expected.Cursor.Index + 1}/{expected.Cursor.Count}  *  {expected.Cursor.CurrentLabel}", readout.Text);
                Assert.Equal(reset ? new[] { "reset", "frame" } : new[] { "frame" }, events);   // WPF: reset, then render
                events.Clear();
            }

            // A forward scrub (the user drags the slider): no reset.
            scrubber.Value = 3;
            Check(oracle.ReplaySeek(3), reset: false, "scrub to 3");
            // STEP BACK: one frame back, and the timeline restarts.
            Click(back);
            Check(oracle.ReplayRewind(), reset: true, "step back");
            // Forward again by STEP ONCE (the WPF next-frame button): no reset.
            Click(w.FindControl<Button>("StepOnceButton")!);
            Check(oracle.ReplayAdvance(), reset: false, "step once");
            // A backward scrub: reset.
            scrubber.Value = 1;
            Check(oracle.ReplaySeek(1), reset: true, "scrub back to 1");
            // A forward scrub to the end, then RESTART: back to frame 1, reset.
            scrubber.Value = 4;
            Check(oracle.ReplaySeek(4), reset: false, "scrub to 4");
            Click(restart);
            Check(oracle.ReplayRestart(), reset: true, "restart");
            // A scrub back onto frame 1 resets (WPF: a move onto frame 1, or any backward move).
            scrubber.Value = 2;
            Check(oracle.ReplaySeek(2), reset: false, "scrub to 2");
            run.SeekReplay(0);
            Check(oracle.ReplaySeek(0), reset: true, "seek to 0");
            // STEP BACK on frame 1 stays on frame 1 and still restarts the timeline (WPF).
            Click(back);
            Check(oracle.ReplayRewind(), reset: true, "step back at frame 1");
            Assert.True(run.IsPaused);   // none of these plays or pauses (WPF)

            // The WPF scrubber trap: a SHORTER run opened after a longer one opens on its first frame, not its last.
            scrubber.Value = 4;
            Check(oracle.ReplaySeek(4), reset: false, "scrub to 4 again");
            string shortRun = Run(temp, "gda-replay-two", 2);
            Await(actions.OpenRunFolderAtAsync(shortRun));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, run.Host.ReplayCursor!.Index);
            Assert.Equal(2, run.Host.ReplayCursor.Count);
            Assert.Equal(1, scrubber.Maximum);
            Assert.Equal(0, scrubber.Value);

            // A live case: the replay controls go, the STEP picker comes back, and the replay moves do nothing.
            LoadLiveAndPause(w, Case9());
            Assert.False(restart.IsVisible || back.IsVisible || scrubber.IsVisible || readout.IsVisible);
            Assert.True(w.FindControl<ComboBox>("StepPicker")!.IsVisible);
            int tick = run.LastFrame!.Tick;
            events.Clear();
            Await(run.RestartReplayAsync());
            Await(run.StepBackAsync());
            run.SeekReplay(1);
            run.ScrubberValue = 1;
            Assert.Empty(events);
            Assert.Equal(tick, run.LastFrame!.Tick);
        }
        finally { Close(w); }
    }

    // ---------------------------------------------------------------- the Run menu's ticks

    [AvaloniaFact]
    public void The_Run_menus_solver_step_and_speed_items_tick_the_choice_in_force_wherever_it_was_made()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp));
        w.Show();
        try
        {
            LoadLiveAndPause(w, Case9());
            MainWindowViewModel vm = w.ViewModel;
            RunViewModel run = vm.Run;
            MenuItem runMenu = TopMenu(w, "_Run");
            MenuItem solverMenu = Item(runMenu, "Solver"), stepMenu = Item(runMenu, "Step size"), speedMenu = Item(runMenu, "Speed");

            // Exactly the choice in force is ticked, in each of the three lists, and each item shows a tick box.
            void AssertTicks()
            {
                Dispatcher.UIThread.RunJobs();
                foreach (MenuItem i in solverMenu.Items.OfType<MenuItem>())
                {
                    Assert.Equal(MenuItemToggleType.CheckBox, i.ToggleType);
                    Assert.Equal((string?)i.Header == run.CurrentSolver.Label, i.IsChecked);
                }
                foreach (MenuItem i in stepMenu.Items.OfType<MenuItem>())
                    Assert.Equal((string?)i.Header == run.CurrentStep.Label, i.IsChecked);
                foreach (MenuItem i in speedMenu.Items.OfType<MenuItem>())
                    Assert.Equal((string?)i.Header == RunCommands.SpeedLabel(run.CurrentSpeed), i.IsChecked);
                Assert.Equal(1, solverMenu.Items.OfType<MenuItem>().Count(i => i.IsChecked));
                Assert.Equal(1, stepMenu.Items.OfType<MenuItem>().Count(i => i.IsChecked));
                Assert.Equal(1, speedMenu.Items.OfType<MenuItem>().Count(i => i.IsChecked));
            }

            Assert.Equal(TransportChoices.Solvers[0], run.CurrentSolver);
            Assert.Equal(60, run.CurrentSpeed);
            AssertTicks();

            // From the menu: the solver, the step and the speed.
            Await(vm.Registry.Get(RunCommands.SolverId(TransportChoices.Solvers[1].Kind)).RunAsync());
            Assert.Same(TransportChoices.Solvers[1], run.CurrentSolver);
            AssertTicks();
            Await(vm.Registry.Get(RunCommands.StepId(1)).RunAsync());
            Assert.Same(TransportChoices.Steps[1], run.CurrentStep);
            AssertTicks();
            Await(vm.Registry.Get(RunCommands.SpeedId(5)).RunAsync());
            Assert.Equal(5, run.CurrentSpeed);
            AssertTicks();

            // From elsewhere: the header's SOLVER picker, the STEP picker, the '.' key and the SPEED button.
            w.FindControl<ComboBox>("SolverPicker")!.SelectedItem = TransportChoices.Solvers[0];
            PumpUntil(() => run.ActiveSolverText.StartsWith("auto * ", StringComparison.Ordinal), "the picker's solver change");
            Assert.Same(TransportChoices.Solvers[0], run.CurrentSolver);
            AssertTicks();
            w.FindControl<ComboBox>("StepPicker")!.SelectedItem = TransportChoices.Steps[^1];
            Assert.Same(TransportChoices.Steps[^1], run.CurrentStep);
            AssertTicks();
            Assert.True(w.MapControl.Focus());
            Dispatcher.UIThread.RunJobs();
            Press(w, Key.OemPeriod);   // finer
            Assert.Same(TransportChoices.Steps[^2], run.CurrentStep);
            AssertTicks();
            Click(w.FindControl<Button>("SpeedButton")!);   // x5 -> x15
            Assert.Equal(15, run.CurrentSpeed);
            AssertTicks();
        }
        finally { Close(w); }
    }
}
