using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Dialogs;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// A pre-solved run (a folder of exported states the app replays) holds only the frames it recorded, so a short one
/// plays a few steps and stops at its last frame, by design and as in the Windows app. Without a word on screen that
/// looks like a stalled play loop (Mark, 2026-10-20: "it only seems to play 3 to 5 iterations and stops"; the case was a
/// two-frame gda-replay run). These tests pin the two things that now say so: Open case labels a run with its frame
/// count, and the status bar says when a replay has finished and that PLAY restarts it.
/// </summary>
public sealed class ReplayClarityTests
{
    /// <summary>A two-frame gda-replay style run of case9 (state-000000.json and state-000003.json), as the real
    /// results/gda-replay-* folders hold.</summary>
    private static string TwoFrameRun(TempFolder temp)
    {
        string dir = Path.Combine(temp.Path, "gda-replay-test");
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

    [Fact]
    public void Open_case_labels_a_pre_solved_run_with_its_frame_count_and_leaves_live_cases_alone()
    {
        using var temp = new TempFolder();
        string run = TwoFrameRun(temp);
        // A stray file that is not a snapshot (the reader's rule: slice-/state- then digits only) is not counted.
        File.WriteAllText(Path.Combine(run, "exports", "state-notes.json"), "{}");

        ModelSource replay = ModelCatalog.ReplaySource(run);
        Assert.Equal($"{replay.Label}  (pre-solved run, 2 frames)", CaseListLabel.Text(replay));

        var live = new ModelSource("MATPOWER  case9", temp.Path, Path.Combine(temp.Path, "case9.m"));
        Assert.Equal("MATPOWER  case9", CaseListLabel.Text(live));

        // A run folder whose exports cannot be found shows the label alone: the count is never guessed.
        var gone = new ModelSource("Replay  gone", temp.Path, ReplayDir: Path.Combine(temp.Path, "no-such-run"));
        Assert.Equal("Replay  gone", CaseListLabel.Text(gone));

        Assert.Equal(2, CaseListLabel.CountFrames(run));
        Assert.Null(CaseListLabel.CountFrames(Path.Combine(temp.Path, "no-such-run")));
    }

    [AvaloniaFact]
    public void A_replay_that_plays_its_last_frame_says_so_in_the_status_bar()
    {
        using var temp = new TempFolder();
        string run = TwoFrameRun(temp);
        var options = new ShellOptions
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
        };
        var w = new MainWindow(options);
        w.Show();
        try
        {
            RunViewModel vm = w.ViewModel.Run;
            var finished = new List<string>();
            vm.ReplayFinished += (_, t) => finished.Add(t);

            var actions = Assert.IsType<ShellActions>(w.Actions);
            Task open = actions.OpenRunFolderAtAsync(run);
            PumpUntil(() => open.IsCompleted, "the run folder to open");
            open.GetAwaiter().GetResult();
            Assert.True(vm.Host.IsReplay);
            Assert.Empty(finished);   // opened paused on its first frame: nothing has finished yet

            // PLAY at the fastest speed: two frames, then the host stops at the end.
            w.ViewModel.Registry.Get(RunCommands.SpeedId(60)).RunAsync().GetAwaiter().GetResult();
            Task play = w.ViewModel.Registry.Get(RunCommands.PlayPause).RunAsync();
            PumpUntil(() => play.IsCompleted, "PLAY");
            PumpUntil(() => finished.Count > 0, "the replay to finish");

            string expected = RunViewModel.ReplayFinishedText(1, 2, vm.Host.ReplayCursor!.CurrentLabel);
            Assert.Equal(expected, Assert.Single(finished));
            Assert.StartsWith("Replay finished: frame 2 of 2 (", expected, StringComparison.Ordinal);
            Assert.EndsWith(") is the last recorded state. PLAY restarts it.", expected, StringComparison.Ordinal);
            Assert.Equal(expected, w.ViewModel.StatusText);
            Assert.True(vm.IsPaused);
            Assert.False(vm.Host.Playing);
        }
        finally { w.Close(); }
    }
}
