using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Shell;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// WPF parity (2026-10-24): a case just loads and solves in continuous operation. In the WPF window the start-up plan
/// picks a source (the launch model text, else the last-used, else the first) and SwitchTo loads it live and plays it
/// at once ("live models animate (the host plays from the load)"): no click is needed for the solve to keep stepping.
/// These pin the same in the cross-platform window, for every kind of local case it can offer (a JSON case folder, a
/// MATPOWER file and a JSON v2 case manifest, which only this window can load): opened with the start-up plan on and
/// nothing touched, the case is loaded, shows PAUSE (it is playing) and keeps solving frame after frame.
/// </summary>
public sealed class CasesAutoSolveTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = true,                       // the start-up plan, as a user's launch
        LaunchArguments = () => Array.Empty<string>(),  // no model text: the plan picks the (only) source itself
        LoadGeography = _ => null,
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop) PumpUntil(() => stop.IsCompleted, "the run loop to stop");
    }

    public static TheoryData<string> Kinds => new() { "case folder", "MATPOWER", "v2 case" };

    private static ModelSource Source(string kind, TempFolder temp)
    {
        string root = RepoRoot();
        switch (kind)
        {
            case "case folder":
                return new ModelSource("GB reduced  10-zone", Path.Combine(root, "data", "uk"));
            case "MATPOWER":
            {
                string file = Path.Combine(root, "data", "matpower", "lib", "case9.m");
                return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
            }
            case "v2 case":
            {
                GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(root, "data", "uk"));
                string manifest = temp.File("uk.case.json");
                V2CaseWriter.Write(uk, manifest);
                return ModelCatalog.V2CaseSource(manifest);
            }
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown case kind");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void A_case_loads_at_start_and_keeps_solving_with_no_click(string kind)
    {
        using var temp = new TempFolder();
        ModelSource source = Source(kind, temp);
        var w = new MainWindow(Options(temp, source));
        w.Show();
        try
        {
            // The start-up plan chose it and loaded it, with no user action.
            PumpUntil(() => w.ViewModel.Run.HasRun, $"{kind}: the start-up load");
            Assert.Equal(source.Label, w.ViewModel.SelectedSource?.Label);
            Assert.Null(w.ViewModel.Run.LoadError);
            Assert.False(w.ViewModel.Run.IsReplay);

            // Playing: not paused, the transport offers PAUSE.
            Assert.False(w.ViewModel.Run.IsPaused, $"{kind}: the case opened paused");
            Assert.Equal(GridSim.App.Run.RunViewModel.PauseLabel, w.ViewModel.Run.PlayPauseLabel);

            // Continuous operation: the solve keeps stepping on its own (the dashboard's tick climbs frame after frame).
            PumpUntil(() => w.ViewModel.Dashboard.Tick != "0", $"{kind}: the first solved frame");
            int first = int.Parse(w.ViewModel.Dashboard.Tick, System.Globalization.CultureInfo.InvariantCulture);
            PumpUntil(() => int.Parse(w.ViewModel.Dashboard.Tick, System.Globalization.CultureInfo.InvariantCulture) >= first + 3,
                $"{kind}: three more frames with no click (tick stuck at {w.ViewModel.Dashboard.Tick})", timeoutMs: 60_000);
            Assert.False(w.ViewModel.Run.IsPaused);
        }
        finally { Close(w); }
    }

    // A two-frame pre-solved run of case9 (as the header tests make one).
    private static string TwoFrameRun(TempFolder temp)
    {
        string dir = Path.Combine(temp.Path, "gda-replay-startup");
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

    [AvaloniaFact]
    public void A_last_used_run_is_not_reopened_at_start_the_first_live_case_is_and_it_keeps_solving()
    {
        using var temp = new TempFolder();
        ModelSource live = Source("case folder", temp);
        ModelSource run = ModelCatalog.ReplaySource(TwoFrameRun(temp));
        // The settings remember the run as the last-used source (a user who last looked at a replay).
        var store = ShellSettingsStore.ForFile(temp.File("settings.json"));
        AppSettings saved = store.Load();
        saved.LastSource = run.Label;
        saved.LastSourcePath = run.Key;
        saved.Save();
        var w = new MainWindow(new ShellOptions
        {
            SettingsStore = store,
            DiscoverCases = (_, _) => new List<ModelSource> { run, live },
            LicenceDescription = () => "Test licence",
            Licence = () => new LicenceGate(Entitlement.FreeDefault),
            UseNativeMenu = false,
            AutoLoadAtStart = true,
            LaunchArguments = () => Array.Empty<string>(),
            LoadGeography = _ => null,
        });
        w.Show();
        try
        {
            PumpUntil(() => w.ViewModel.Run.HasRun, "the start-up load");
            Assert.Equal(live.Label, w.ViewModel.SelectedSource?.Label);   // the live case, not the run
            Assert.False(w.ViewModel.Run.IsReplay);
            Assert.False(w.ViewModel.Run.IsPaused);
            Assert.Contains(w.ViewModel.Sources, s => s.Label == run.Label);   // the run is still one choice away
            PumpUntil(() => w.ViewModel.Dashboard.Tick != "0", "the first solved frame");
            int first = int.Parse(w.ViewModel.Dashboard.Tick, System.Globalization.CultureInfo.InvariantCulture);
            PumpUntil(() => int.Parse(w.ViewModel.Dashboard.Tick, System.Globalization.CultureInfo.InvariantCulture) >= first + 2,
                "two more frames with no click", timeoutMs: 60_000);
        }
        finally { Close(w); }
    }

    [Fact]
    public void StartupIndex_passes_over_a_run_for_the_first_live_case_and_keeps_every_other_choice()
    {
        var caseA = new ModelSource("A", "a-dir");
        var caseB = new ModelSource("B", "b-dir");
        var runR = new ModelSource("R", "r-dir", ReplayDir: "r-dir");
        var bridge = new ModelSource("Live bridge", "", Bridge: "ws://example/ws");
        AppSettings Last(ModelSource s) => new() { LastSource = s.Label, LastSourcePath = s.Key };
        var list = new List<ModelSource> { runR, bridge, caseA, caseB };
        Assert.Equal(2, CaseChoice.StartupIndex(list, Last(runR)));     // a run: the first live case instead
        Assert.Equal(1, CaseChoice.StartupIndex(list, Last(bridge)));   // the bridge: kept (it streams continuously)
        Assert.Equal(3, CaseChoice.StartupIndex(list, Last(caseB)));    // a live case: kept
        Assert.Equal(-1, CaseChoice.StartupIndex(list, new AppSettings()));   // nothing remembered: the caller's first entry
        Assert.Equal(0, CaseChoice.StartupIndex(new List<ModelSource> { runR }, Last(runR)));   // only runs: the run
    }
}
