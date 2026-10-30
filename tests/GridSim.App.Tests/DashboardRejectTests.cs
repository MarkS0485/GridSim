using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Dashboard;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.7 (a follow-up found during the port): a frame on which the dashboard's own update throws does
/// not reach the map. In WPF the dashboard's update and the map's update sit in one guard (ShowHostFrame and
/// RenderReplayFrame: <c>_vm.Update(...)</c>, then <c>_renderer.SetState(...)</c>, then <c>_last = f</c>, all inside one
/// try), so such a frame takes the SOLVE FAULT path and never reaches the map or becomes the current frame. Until this
/// slice the Avalonia dashboard caught its own failure and the run pipeline went on to the map. The frame used is
/// case9 with every load times 100: its power flow blows up to a NaN frequency, which the dashboard model refuses (as
/// DashboardViewTests measures it on this build; the test asserts it, so a solver change cannot make it test nothing).
/// </summary>
public sealed class DashboardRejectTests
{
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

    /// <summary>case9 with every load (P and Q) times <paramref name="factor"/> (as DashboardViewTests makes it).</summary>
    private static ModelSource Case9Loaded(TempFolder temp, int factor)
    {
        string text = File.ReadAllText(Case9().MFile!);
        (int P, int Q)[] loads = { (90, 30), (100, 35), (125, 50) };
        foreach ((int p, int q) in loads)
        {
            string row = $"\t{p}\t{q}\t";
            Assert.Contains(row, text, StringComparison.Ordinal);   // fail loud if the shipped case ever changes
            text = text.Replace(row, $"\t{p * factor}\t{q * factor}\t", StringComparison.Ordinal);
        }
        string name = $"case9x{factor}";
        string file = temp.File(name + ".m");
        File.WriteAllText(file, text.Replace("function mpc = case9", "function mpc = " + name, StringComparison.Ordinal));
        return new ModelSource(name, temp.Path, file);
    }

    private static ShellOptions Options(TempFolder temp) => new()
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

    [AvaloniaFact]
    public void A_frame_the_dashboard_refuses_does_not_reach_the_map_or_become_the_current_frame()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            RunViewModel run = vm.Run;

            // Count what each frame reached: the dashboard's verdict (after its own handler), the map's feed.
            // 2.0.192: case 9 at 100x load drives the live swing non-finite. Since 2.0.188 the live frequency integrator
            // refuses that state itself (it never publishes a NaN frequency), so each frame arrives as a solver fault
            // (LiveFaulted) instead of a solved frame the dashboard refuses. Both kinds are counted, and the test
            // demands that no frame of the bad case arrives as solved at all.
            int arriving = 0, rejected = 0, faulted = 0, toMap = 0;
            var rejections = new List<string>();
            var faults = new List<string>();
            run.FrameArriving += (_, e) =>
            {
                if (e.Kind == RunFrameKind.LiveFaulted) { faulted++; faults.Add(Assert.IsAssignableFrom<Exception>(e.Fault).Message); return; }
                if (e.Kind != RunFrameKind.LiveSolved) return;
                arriving++;
                if (e.Rejected) { rejected++; rejections.Add(e.Rejection!.Message); }
            };
            run.FrameArrived += (_, _) => toMap++;   // the window's map handler runs on this event (SetState, SetBoundaries)

            Await(vm.LoadCaseAsync(Case9Loaded(temp, 100)));
            Assert.Null(run.LoadError);
            Await(vm.Registry.Get(RunCommands.SpeedId(60)).RunAsync());
            PumpUntil(() => faulted >= 3, "three frames");
            Await(vm.Registry.Get(RunCommands.PlayPause).RunAsync());
            PumpFor(200);

            // Every frame was refused (the live engine's non-finite frequency), none arrived as solved, none reached the map.
            Assert.True(faulted >= 3, $"{faulted} frames");
            Assert.Equal(0, arriving);
            Assert.Equal(0, rejected);
            Assert.Equal(0, toMap);
            Assert.False(w.MapControl.View.HasState);   // the map still shows no state at all
            Assert.Null(run.LastFrame);                 // never the current frame (hover, fly-out, trips read nothing)
            Assert.Null(run.Panel);
            // The SOLVE FAULT path, as WPF: the lamp, the alarm and the log line, and the run's own error area.
            Assert.Equal(DashboardHost.SolveFaultStatus, vm.Dashboard.Status);
            Assert.Equal(ColourRamps.Loading(1.2), vm.Dashboard.StatusColour);
            Assert.All(faults, m => Assert.Contains(vm.Dashboard.Alarms, a => a.Text == $"SOLVE FAULT: {m}"));
            Assert.Equal(RunViewModel.SolveFaultStatus, run.Status);
            Assert.StartsWith("Solve failed at tick ", run.FrameError, StringComparison.Ordinal);
            Assert.EndsWith(": " + faults[^1], run.FrameError, StringComparison.Ordinal);
            Assert.Contains(vm.Dashboard.Events, e => e.EndsWith(run.FrameError!, StringComparison.Ordinal));

            // A good case afterwards (a load plays at once): its frames are accepted and reach the map again.
            int mapBefore = toMap;
            Await(vm.LoadCaseAsync(Case9()));
            PumpUntil(() => toMap >= mapBefore + 2, "two good frames on the map");
            Assert.Equal(rejected, rejections.Count);
            Assert.True(w.MapControl.View.HasState);
            Assert.NotNull(run.LastFrame);
            Assert.Null(run.FrameError);
        }
        finally
        {
            w.Close();
            if (w.StopLoop is { } stop) Await(stop);
        }
    }
}
