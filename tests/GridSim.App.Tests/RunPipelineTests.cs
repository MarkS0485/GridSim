using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Dialogs;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Commands;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme Phase 1.2: the run pipeline in the Avalonia app, on the headless platform. A case chosen in Open case
/// is loaded through the shared <see cref="SimHost"/>; the frames the window shows carry exactly the text the WPF
/// window's format strings give for the same frames made by a second host driven directly; the transport controls
/// reach the host; the play loop is cancelled and ends cleanly when another case is opened and when the window closes;
/// and the licence-cap and run-folder refusals show the WPF texts in the error area. Every window here reads and writes
/// only a temporary folder; the cases are the shipped MATPOWER files under data/.
/// </summary>
public sealed class RunPipelineTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- helpers

    /// <summary>The checkout root (the folder with GridSim.slnx), found from the test's own folder.</summary>
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A shipped MATPOWER case as the catalogue lists one: label, folder and file.</summary>
    private static ModelSource Matpower(string name)
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource(name, Path.GetDirectoryName(file)!, file);
    }

    /// <summary>The free tier's gate, as an install with no key gets it.</summary>
    private static LicenceGate FreeGate() => new(Entitlement.FreeDefault);

    /// <summary>Options for a window that lists <paramref name="cases"/>, checks against <paramref name="gate"/>, and
    /// reads and writes only inside <paramref name="temp"/>.</summary>
    private static ShellOptions Options(TempFolder temp, IReadOnlyList<ModelSource> cases, LicenceGate gate, bool bridgeEnabled = true) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => cases.ToList(),
        LicenceDescription = () => "Test licence",
        Licence = () => gate,
        UseNativeMenu = false,
        // Test retargeted 2026-10-20 (UI programme 1.8): the window now loads a source by itself when it opens, as
        // the WPF window does; these tests were written for the window that opens empty, so they turn that off
        // (ShellOptions.AutoLoadAtStart). Every assertion is unchanged.
        AutoLoadAtStart = false,
        BridgeEnabled = bridgeEnabled,   // slice 1.8: the GDA bridge is registered unless a test says otherwise
    };

    /// <summary>Run the UI thread's work until <paramref name="done"/> holds; fail after <paramref name="timeoutMs"/>.</summary>
    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 60_000)
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

    /// <summary>Run the UI thread's work for <paramref name="ms"/> milliseconds (to show that nothing more happens).</summary>
    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// Wait for <paramref name="task"/> while running the UI thread's work, then observe it. Never block the UI thread
    /// on a task directly: a load waits for the old loop to end, and the loop's last frame is delivered on this thread.
    /// </summary>
    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static Task Run(MainWindow w, string id) => w.ViewModel.Registry.Get(id).RunAsync();

    private static void Press(MainWindow w, Key key)
    {
        w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Text(MainWindow w, string name) => w.FindControl<TextBlock>(name)!.Text ?? "";

    /// <summary>
    /// The first <paramref name="n"/> frames a host driven directly (not through the window) makes for
    /// <paramref name="source"/> on <paramref name="solver"/>: the reference the window's frames must equal.
    /// </summary>
    private static List<SimFrame> ReferenceFrames(ModelSource source, SolverKind solver, int n)
    {
        var host = new SimHost { PlaybackRate = 60 };
        LiveLoadResult loaded = host.LoadLive(source, solver, FreeGate());
        Assert.True(loaded.Loaded, loaded.Refusal);
        host.RequestStep();
        var frames = new List<SimFrame>();
        using var cts = new CancellationTokenSource();
        Task loop = Task.Run(() => host.RunAsync(f =>
        {
            Assert.Equal(HostFrameKind.LiveSolved, f.Kind);
            frames.Add(f.Frame!);
            if (frames.Count == n) cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token));
        Assert.True(loop.Wait(TimeSpan.FromSeconds(60)), "the reference loop did not finish");
        Assert.Equal(n, frames.Count);
        return frames;
    }

    /// <summary>Choose the first case in File > Open case..., as a user does.</summary>
    private static void OpenFirstCase(MainWindow w)
    {
        MenuItem open = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>()
            .SelectMany(m => m.Items.OfType<MenuItem>()).Single(i => (string?)i.Tag == ShellCommands.OpenCase);
        open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        OpenCaseWindow picker = Assert.Single(w.OwnedWindows.OfType<OpenCaseWindow>());
        picker.SourceList.SelectedIndex = 0;
        picker.Accept();
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- tests

    [AvaloniaFact]
    public void Choosing_case9_in_Open_case_loads_it_and_shows_the_hosts_frames_with_the_WPF_format_strings()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9");
        var w = new MainWindow(Options(temp, new[] { case9 }, FreeGate()));
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            var shown = new List<LivePanelText>();
            run.FrameShown += (_, t) => shown.Add(t);

            Assert.False(run.HasRun);
            Assert.False(w.FindControl<Border>("TransportBar")!.IsVisible);
            OpenFirstCase(w);
            PumpUntil(() => run.HasRun, "case9 to load");

            // Loaded through the host, with the WPF load line in the status bar.
            Assert.True(run.Host.HasLiveSolver);
            Assert.Equal("case9", run.CaseName);
            Assert.StartsWith("Loaded case9  -  9 buses, ", run.LoadedText, StringComparison.Ordinal);
            Assert.Equal(run.LoadedText, w.ViewModel.StatusText);
            Assert.Equal($"auto * {run.Host.ActiveSolver}", run.ActiveSolverText);
            Assert.False(run.HasError, run.ErrorText);
            Assert.True(w.FindControl<Border>("TransportBar")!.IsVisible);
            // Test retargeted 2026-10-20 (UI programme integration): the live panel is gone; the values are on the
            // NETWORK tab's dashboard, which is the tab showing (strengthened: it is the window's one dashboard).
            Assert.True(w.DashboardTab.IsEffectivelyVisible);
            Assert.Same(w.ViewModel.Dashboard, w.DashboardTab.Model);

            // Play fast (x60), as the SPEED button does; the speed changes the pace only, never a number.
            Await(Run(w, RunCommands.SpeedId(60)));
            const int n = 6;
            PumpUntil(() => shown.Count >= n, $"{n} frames");
            Await(Run(w, RunCommands.PlayPause));   // pause, so the panel holds still for the last check
            PumpFor(100);

            List<SimFrame> expected = ReferenceFrames(case9, SolverKind.Auto, n);
            for (int i = 0; i < n; i++)
            {
                SimFrame f = expected[i];
                LivePanelText t = shown[i];
                // The WPF format strings, written out here (DashboardViewModel.Update / SetFrequency, OperatorVm).
                Assert.Equal(f.Tick.ToString(Inv), t.Tick);
                Assert.Equal((i + 1).ToString(Inv), t.Tick);   // consecutive ticks from 1: no frame dropped or doubled
                Assert.Equal(f.FrequencyHz.ToString("F2", Inv), t.Frequency);
                double dev = f.FrequencyHz - f.Model.FrequencyHz;
                Assert.Equal((dev >= 0 ? "+" : "") + dev.ToString("F2", Inv), t.FrequencyDeviation);
                Assert.Equal($"{f.RocofHzPerS.ToString("+0.000;-0.000;0.000", Inv)} Hz/s", t.Rocof);
                Assert.Equal(f.Result.TotalLossMw.ToString("F0", Inv), t.Losses);
                Assert.Equal((f.GenerationMw / 1000.0).ToString("F2", Inv), t.Generation);
                Assert.Equal((f.DemandMw / 1000.0).ToString("F2", Inv), t.Demand);
                Assert.Equal($"{(int)f.Hour:00}:{(int)((f.Hour % 1) * 60):00}", t.SimTime);
                Assert.Equal(f.Result.Converged ? f.Result.ConvergedLabel() : "NOT CONVERGED", t.Convergence);
                Assert.Equal(f.Result.Iterations.ToString(Inv), t.Iterations);
                Assert.Equal(f.Result.MaxMismatch.ToString("E2", Inv), t.Mismatch);
                Assert.True(t.Converged);   // case9 solves
            }

            // What is on screen is the last frame shown, character for character.
            // Test retargeted 2026-10-20 (UI programme integration): the temporary live panel is gone (WPF has none), so
            // each on-screen check reads the place the WPF window shows that value, with the same expected text: the
            // tick in the window header (WPF header), the frequency, losses, generation and demand on the NETWORK
            // dashboard tiles, the RoCoF on the OPERATOR tab (the one WPF view that prints it), the STATE NOT VALID
            // banner over the tabs (WPF), and the licence line in the dashboard's event log (WPF writes it there at
            // start-up). The convergence label is shown nowhere in WPF; its on-screen check becomes the WPF one, the
            // status lamp, which must show the dashboard's verdict and, for this converged frame, not NOT CONVERGED.
            // Every expected text is unchanged; strengthened: each value is also checked against the dashboard model
            // the tiles bind, and the licence line must be the oldest event-log line, written once.
            LivePanelText last = shown[^1];
            Assert.Equal(last, run.Panel);
            Dashboard.DashboardView dash = w.DashboardTab;
            string Tile(string name) => dash.FindControl<TextBlock>(name)!.Text ?? "";
            Assert.Equal(last.Tick, Text(w, "HeaderTickText"));
            Assert.Equal(last.Tick, w.ViewModel.Dashboard.Tick);
            Assert.Equal(last.Frequency, Tile("FreqText"));
            Assert.Equal(last.Frequency, w.ViewModel.Dashboard.Freq);
            Assert.Equal(last.Losses, Tile("LossesText"));
            Assert.Equal(last.Losses, w.ViewModel.Dashboard.Losses);
            Assert.True(last.Converged);
            Assert.Equal(w.ViewModel.Dashboard.Status, Text(w, "HeaderStatusText"));
            Assert.NotEqual(LivePanelText.NotConvergedText, Text(w, "HeaderStatusText"));
            Assert.Contains(Text(w, "HeaderStatusText"), new[] { "SYSTEM NORMAL", " FREQUENCY EVENT", "VOLTAGE ALERT", "CORRIDOR ALERT" });
            // (the OPERATOR tab is shown to read it, as a user would; its content binds once it is the selected tab)
            w.TabStrip.SelectedItem = w.TabStrip.Items.OfType<TabItem>().Single(t => Equals(t.Header, "OPERATOR"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(last.Rocof, w.FindControl<Tabs.OperatorView>("OperatorTab")!.FindControl<TextBlock>("RocofText")!.Text);
            w.TabStrip.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(last.Rocof, w.ViewModel.Tabs.Operator.Rocof);
            Assert.Equal(last.Generation, Tile("GenerationText"));
            Assert.Equal(last.Generation, w.ViewModel.Dashboard.Generation);
            Assert.Equal(last.Demand, Tile("DemandText"));
            Assert.Equal(last.Demand, w.ViewModel.Dashboard.Demand);
            // A converged state: no alarm, no banner.
            Assert.False(w.FindControl<Border>("ErrorArea")!.IsVisible);
            Assert.False(w.FindControl<Border>("TabInvalidBanner")!.IsVisible);
            // The licence lines WPF shows.
            // Test retargeted 2026-10-20 (UI programme 1.7): the tier line left the header for the window title, where WPF
            // shows it. The same text is pinned on the run view model, and, stronger, the whole WPF title is pinned too.
            Assert.Null(w.FindControl<TextBlock>("LicenceTierText"));
            Assert.Equal($"v{GridSim.Core.BuildInfo.Version}  -  Personal licence", w.ViewModel.Run.LicenceTierText);
            Assert.Equal($"GridSim  -  GB Transmission Control  -  v{GridSim.Core.BuildInfo.Version}  -  Personal licence", w.Title);
            Assert.Equal($"08:00  Licence: {FreeGate().Describe()}", w.ViewModel.Dashboard.Events[^1]);
            Assert.Single(w.ViewModel.Dashboard.Events, e => e.Contains("Licence: ", StringComparison.Ordinal));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Step_once_advances_exactly_one_frame_pause_stops_and_the_choices_reach_the_host()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9");
        var w = new MainWindow(Options(temp, new[] { case9 }, FreeGate()));
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            int frames = 0;
            var ticks = new List<string>();
            run.FrameShown += (_, t) => { frames++; ticks.Add(t.Tick); };
            int loadedAt = -1;   // frames shown when the latest load took effect (raised inside the load)
            run.FeedChanged += (_, _) => loadedAt = frames;
            Await(w.ViewModel.LoadCaseAsync(case9));
            Assert.True(run.HasRun);

            // SPEED: the menu item and the button both reach the host's playback rate.
            Await(Run(w, RunCommands.SpeedId(15)));
            Assert.Equal(15, run.Host.PlaybackRate);
            Assert.Equal("x15", w.FindControl<Button>("SpeedButton")!.Content);
            w.FindControl<Button>("SpeedButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpFor(10);
            Assert.Equal(60, run.Host.PlaybackRate);   // x15 -> x60, the WPF cycle order
            Assert.Equal("x60", run.SpeedLabel);

            PumpUntil(() => frames >= 2, "two frames");

            // PAUSE (F5): the host stops playing, the button reads RESUME (live), and no frame follows.
            Press(w, Key.F5);
            Assert.False(run.Host.Playing);
            Assert.Equal(RunViewModel.ResumeLabel, run.PlayPauseLabel);
            PumpFor(300);   // let a frame that was already being made land
            int pausedFrames = frames;
            int pausedTick = run.Host.Tick;
            PumpFor(400);
            Assert.Equal(pausedFrames, frames);
            Assert.Equal(pausedTick, run.Host.Tick);

            // STEP ONCE (F6): exactly one more frame, one tick on, and it stays paused.
            Press(w, Key.F6);
            PumpUntil(() => frames == pausedFrames + 1, "the stepped frame");
            PumpFor(400);
            Assert.Equal(pausedFrames + 1, frames);
            Assert.Equal(pausedTick + 1, run.Host.Tick);
            Assert.Equal((pausedTick + 1).ToString(Inv), run.Panel!.Tick);
            Assert.False(run.Host.Playing);

            // The button and the menu command step the same way.
            w.FindControl<Button>("StepOnceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => frames == pausedFrames + 2, "the second stepped frame");
            Await(Run(w, RunCommands.StepOnce));
            PumpUntil(() => frames == pausedFrames + 3, "the third stepped frame");
            PumpFor(300);
            Assert.Equal(pausedFrames + 3, frames);
            Assert.Equal(pausedTick + 3, run.Host.Tick);

            // RESUME (F5 again): frames flow again.
            Press(w, Key.F5);
            Assert.True(run.Host.Playing);
            Assert.Equal(RunViewModel.PauseLabel, run.PlayPauseLabel);
            PumpUntil(() => frames >= pausedFrames + 5, "frames after resuming");

            // STEP: a menu item sets the host's step length; '.' (finer) stops at the finest, ',' (coarser) moves on.
            Await(Run(w, RunCommands.StepId(0)));
            Assert.Equal(TransportChoices.Steps[0].Minutes, run.Host.StepMinutes);
            Press(w, Key.OemPeriod);
            Assert.Equal(TransportChoices.Steps[0].Minutes, run.Host.StepMinutes);
            Press(w, Key.OemComma);
            Assert.Equal(TransportChoices.Steps[1].Minutes, run.Host.StepMinutes);
            Assert.Same(TransportChoices.Steps[1], w.FindControl<ComboBox>("StepPicker")!.SelectedItem);
            // The picker reaches the host too.
            w.FindControl<ComboBox>("StepPicker")!.SelectedItem = TransportChoices.Steps[^1];
            Assert.Equal(TransportChoices.Steps[^1].Minutes, run.Host.StepMinutes);

            // SOLVER: a menu item rebuilds the running case on that engine, as the WPF picker does.
            string dense = new SimHost().LoadLive(case9, SolverKind.DenseNewton, FreeGate()).ActiveSolver!;
            Task change = Run(w, RunCommands.SolverId(SolverKind.DenseNewton));
            PumpUntil(() => change.IsCompleted, "the solver change");
            Assert.True(change.IsCompletedSuccessfully);
            Assert.Equal(dense, run.Host.ActiveSolver);
            Assert.Equal(dense, run.ActiveSolverText);   // an explicit engine: no "auto *"
            Assert.Same(TransportChoices.Solvers[1], w.FindControl<ComboBox>("SolverPicker")!.SelectedItem);
            Assert.Equal(TransportChoices.Steps[^1].Minutes, run.Host.StepMinutes);   // the step choice survives a rebuild
            Assert.Equal(60, run.Host.PlaybackRate);                                   // and so does the speed
            int afterRebuild = loadedAt;
            PumpUntil(() => frames > afterRebuild, "a frame on the new engine");
            Assert.Equal("1", ticks[afterRebuild]);   // a rebuilt case starts again from tick 1

            // The picker reaches the host the same way.
            w.FindControl<ComboBox>("SolverPicker")!.SelectedItem = TransportChoices.Solvers[0];
            PumpUntil(() => run.ActiveSolverText.StartsWith("auto * ", StringComparison.Ordinal), "the picker's solver change");
            Assert.Equal($"auto * {run.Host.ActiveSolver}", run.ActiveSolverText);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Opening_a_second_case_and_closing_the_window_cancel_the_loop_and_it_ends_cleanly()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        var w = new MainWindow(Options(temp, new[] { case9, case14 }, FreeGate()));
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) => unobserved.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += onUnobserved;
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            var shown = new List<LivePanelText>();
            run.FrameShown += (_, t) => shown.Add(t);
            Await(w.ViewModel.LoadCaseAsync(case9));
            Await(Run(w, RunCommands.SpeedId(60)));
            PumpUntil(() => shown.Count >= 3, "case9 frames");
            Task first = run.LoopTask!;
            Assert.False(first.IsCompleted);
            // The count of frames shown when the next load takes effect (FeedChanged is raised inside the load, before
            // the new loop can deliver anything).
            int before = -1;
            run.FeedChanged += (_, _) => before = shown.Count;

            // Opening another case cancels the first loop and waits for it to end before the new one starts.
            Task open = w.ViewModel.LoadCaseAsync(case14);
            PumpUntil(() => open.IsCompleted, "case14 to load");
            Assert.True(open.IsCompletedSuccessfully);
            Assert.Equal(TaskStatus.RanToCompletion, first.Status);   // ended normally: no fault, not left running
            Assert.NotSame(first, run.LoopTask);
            Assert.Equal("case14", run.CaseName);
            Assert.True(before >= 3);
            PumpUntil(() => shown.Count > before, "case14 frames");
            Assert.Equal("1", shown[before].Tick);   // the new case's first frame; nothing of case9 came after the switch

            // Closing the window cancels the loop; it ends normally, with nothing left running.
            Task second = run.LoopTask!;
            w.Close();
            Assert.NotNull(w.StopLoop);
            PumpUntil(() => w.StopLoop!.IsCompleted && second.IsCompleted, "the loop to end after closing");
            Assert.True(w.StopLoop!.IsCompletedSuccessfully);
            Assert.Equal(TaskStatus.RanToCompletion, second.Status);
            int afterClose = shown.Count;
            PumpFor(200);
            Assert.Equal(afterClose, shown.Count);   // no frame is shown after the close

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
            if (w.IsVisible) w.Close();
        }
    }

    [AvaloniaFact]
    public void A_case_above_the_licence_cap_is_refused_with_the_gates_text_and_nothing_runs()
    {
        using var temp = new TempFolder();
        // A user's own copy of case9 (outside data/, so not a Bundled Network) under a five-bus licence.
        string userDir = Path.Combine(temp.Path, "my-case");
        Directory.CreateDirectory(userDir);
        string userFile = Path.Combine(userDir, "case9.m");
        File.Copy(Matpower("case9").MFile!, userFile);
        var userCase = new ModelSource("my case9", userDir, userFile);
        var gate = new LicenceGate(Entitlement.FreeDefault with { BusLimit = 5, LicenceId = "test-five-bus" });
        string refusal = gate.Check(9, bundled: false).Message;
        Assert.StartsWith("This network has 9 buses; your Personal licence allows 5.", refusal, StringComparison.Ordinal);

        var w = new MainWindow(Options(temp, new[] { userCase }, gate));
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            int frames = 0;
            run.FrameShown += (_, _) => frames++;
            OpenFirstCase(w);
            PumpFor(300);

            // The gate's own words, in the error area, in the alarm colour; nothing loaded and no loop started.
            Assert.Equal(refusal, run.ErrorText);
            Assert.Equal(refusal, Text(w, "ErrorText"));
            Assert.True(w.FindControl<Border>("ErrorArea")!.IsVisible);
            Assert.Equal(w.FindResource("Red"), w.FindControl<TextBlock>("ErrorText")!.Foreground);
            Assert.False(run.HasRun);
            Assert.False(run.Host.HasLiveSolver);
            Assert.Null(run.LoopTask);
            Assert.Equal(0, frames);
            Assert.StartsWith("Chosen: my case9. Not loaded yet", w.ViewModel.StatusText, StringComparison.Ordinal);
            // No Run menu: there is nothing for it to act on.
            Assert.Null(w.ViewModel.Registry.Find(RunCommands.PlayPause));

            // The same gate never counts a Bundled Network: the shipped case9 loads under it (SimHost's bundled rule).
            Await(w.ViewModel.LoadCaseAsync(Matpower("case9")));
            Assert.True(run.HasRun);
            Assert.False(run.HasError, run.ErrorText);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_folder_that_is_not_a_run_directory_shows_the_WPF_message_and_loads_nothing()
    {
        using var temp = new TempFolder();
        string notARun = Path.Combine(temp.Path, "just-a-folder");
        Directory.CreateDirectory(notARun);
        var w = new MainWindow(Options(temp, new[] { Matpower("case9") }, FreeGate()));
        w.Show();
        try
        {
            var actions = Assert.IsType<ShellActions>(w.Actions);
            Await(actions.OpenRunFolderAtAsync(notARun));
            Dispatcher.UIThread.RunJobs();
            // The WPF LOAD RUN... text, word for word.
            Assert.Equal("Not a run directory (no exports/slice-*.json or state-*.json).", w.ViewModel.Run.ErrorText);
            Assert.Equal(w.ViewModel.Run.ErrorText, Text(w, "ErrorText"));
            Assert.True(w.FindControl<Border>("ErrorArea")!.IsVisible);
            Assert.False(w.ViewModel.Run.HasRun);
            Assert.False(w.ViewModel.Run.Host.IsReplay);
            Assert.Null(w.ViewModel.ChosenCase);
            Assert.Empty(w.ViewModel.OpenedRunFolders);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void The_Run_menu_appears_with_the_first_loaded_case_and_its_shortcuts_clash_with_nothing()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, new[] { Matpower("case9") }, FreeGate()));
        w.Show();
        try
        {
            List<string?> Menus() => w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
            Assert.Equal(new[] { "_File", "_Help" }, Menus());   // nothing loaded: no Run menu

            Await(w.ViewModel.LoadCaseAsync(Matpower("case9")));
            // Phase 1.5: the map's View menu joins the Run menu with the first loaded case (MapTests checks its items).
            // Test retargeted 2026-10-20 (UI programme integration): Help now stays the last menu (MenuOrders.Last), so
            // the Run and View menus join before it, not after; the assertion is unchanged in strength (the whole bar, in
            // order) and the expected order is the WPF-sensible one Mark asked for.
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, Menus());
            MenuItem runMenu = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Run");
            Assert.Equal(new[] { "Play or pause", "Step once", "Solver", "Step size", "Finer step", "Coarser step", "Speed" },
                runMenu.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            // Every choice comes from TransportChoices, in its order.
            MenuItem Sub(string header) => runMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);
            Assert.Equal(TransportChoices.Solvers.Select(s => s.Label), Sub("Solver").Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            Assert.Equal(TransportChoices.Steps.Select(s => s.Label), Sub("Step size").Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            Assert.Equal(new[] { "x1", "x5", "x15", "x60" }, Sub("Speed").Items.OfType<MenuItem>().Select(i => (string?)i.Header));

            // A second load registers nothing twice (the registry would throw on a duplicate id).
            Await(w.ViewModel.LoadCaseAsync(Matpower("case9")));
            Assert.Equal(new[] { "_File", "_Run", "_View", "_Help" }, Menus());

            // The registry rules still hold with the Run commands in: one command per shortcut in every mode and on every
            // platform, and the window binds each exactly once.
            CommandRegistry r = w.ViewModel.Registry;
            foreach (CommandModes mode in new[] { CommandModes.Run, CommandModes.Author })
                foreach (CommandPlatforms platform in new[] { CommandPlatforms.Desktop, CommandPlatforms.Mobile })
                {
                    List<AppCommand> withGesture = r.Commands.Where(c => c.Gesture is not null && c.IsOfferedIn(mode, platform)).ToList();
                    Assert.Equal(withGesture.Count, withGesture.Select(c => c.Gesture).Distinct(StringComparer.Ordinal).Count());
                    Assert.Equal(withGesture.Count, r.Gestures(mode, platform).Count);
                }
            IReadOnlyDictionary<string, AppCommand> keys = r.Gestures(CommandModes.Run, CommandPlatforms.Desktop);
            Assert.Equal(RunCommands.PlayPause, keys["F5"].Id);
            Assert.Equal(RunCommands.StepOnce, keys["F6"].Id);
            Assert.Equal(RunCommands.FinerStep, keys["Period"].Id);    // WPF: '.' finer time step
            Assert.Equal(RunCommands.CoarserStep, keys["Comma"].Id);   // WPF: ',' coarser time step
            // Phase 1.5: the shell's three, these four and the map's ten (F M C B G D R A T, Escape), which this test kept
            // free for the map until it was ported; each is now taken by exactly the map command WPF gives it.
            Assert.Equal(17, keys.Count);
            Assert.Equal(17, w.KeyBindings.Count);   // rebuilt, not added twice
            string[] mapKeys = { "F", "M", "C", "B", "G", "D", "R", "A", "T", "Escape" };
            string[] mapIds =
            {
                MapCommands.FitToView, MapCommands.Minimap, MapCommands.Coast, MapCommands.Boundaries, MapCommands.GspAreas,
                MapCommands.DnoAreas, MapCommands.GspRegions, MapCommands.Assets, MapCommands.TripLargestInfeed, MapCommands.ClearSelection,
            };
            for (int i = 0; i < mapKeys.Length; i++)
                Assert.Equal(mapIds[i], keys[mapKeys[i]].Id);

            // Registering the Run commands a second time into the same registry is refused, not doubled.
            Assert.Throws<ArgumentException>(() => RunCommands.Register(r, w.ViewModel.Run));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void A_bridge_source_is_refused_in_plain_words_and_nothing_changes()
    {
        using var temp = new TempFolder();
        // Test retargeted 2026-10-20 (UI programme 1.8): the GDA bridge is now ported, so on a desktop a bridge source
        // connects (and this test's wss://example.invalid would be a real network connect, which no test may make).
        // The refusal is now the behaviour of a head where the bridge is not registered (BridgeEnabled = false, as on a
        // mobile head, plan Phase 3), so the window is made that way; every assertion is unchanged.
        var w = new MainWindow(Options(temp, new[] { Matpower("case9") }, FreeGate(), bridgeEnabled: false));
        w.Show();
        try
        {
            Await(w.ViewModel.LoadCaseAsync(new ModelSource("bridge", "", Bridge: "wss://example.invalid/bridge")));
            Assert.Equal(RunViewModel.BridgeNotAvailableText, w.ViewModel.Run.ErrorText);
            Assert.False(w.ViewModel.Run.HasRun);
            Assert.Null(w.ViewModel.Run.LoopTask);
        }
        finally { w.Close(); }
    }
}
