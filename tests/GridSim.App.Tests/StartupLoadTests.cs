using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Core.Presentation;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme slice 1.8: the window loads a source by itself when it opens, by the WPF window's OnLoaded rules: the
/// launch arguments (read by Core's DesktopLaunchArgs and matched to this window's pickers and tabs; a refusal loads
/// nothing and is reported), then the model the arguments name, else the last-used source from the settings (by its
/// saved path, else its saved label), else the first catalogue entry; a source that is found but refused shows its
/// refusal with no second fallback, as WPF. ShellOptions.AutoLoadAtStart off opens the window empty. Every window here
/// reads and writes only a temporary folder; no test reaches any host but the loopback fake bridge.
/// </summary>
public sealed class StartupLoadTests
{
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

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static ModelSource Matpower(string name)
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

    /// <summary>A settings file in <paramref name="temp"/> with the given last-used source (and bridge rule).</summary>
    private static ShellSettingsStore Settings(TempFolder temp, string? lastLabel, string? lastPath, bool allowUnencrypted = false)
    {
        var store = ShellSettingsStore.ForFile(temp.File("settings.json"));
        AppSettings s = store.Load();
        s.LastSource = lastLabel;
        s.LastSourcePath = lastPath;
        s.BridgeAllowUnencrypted = allowUnencrypted;
        s.Save();
        return store;
    }

    private static MainWindow Window(ShellSettingsStore store, IReadOnlyList<ModelSource> cases, string[]? args = null,
        bool autoLoad = true, Action<MainWindow, string>? refused = null) => new(new ShellOptions
    {
        SettingsStore = store,
        DiscoverCases = (_, _) => cases.ToList(),
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = autoLoad,
        LaunchArguments = () => args ?? Array.Empty<string>(),
        LaunchRefused = refused ?? ((_, text) => throw new Xunit.Sdk.XunitException("unexpected launch refusal: " + text)),
    });

    // Show the window (its first Opened runs the start-up plan) and wait for the load it began.
    private static void Open(MainWindow w)
    {
        w.Show();
        Dispatcher.UIThread.RunJobs();
        if (w.StartupLoad is { } load) Await(load);
    }

    private static void Close(MainWindow w)
    {
        Await(w.ViewModel.Run.StopLoopAsync());
        if (w.ViewModel.Run.Bridge is { IsActive: true } b) Await(b.DisposeBridgeAsync());
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_last_used_source_is_loaded_when_the_window_opens_matched_by_its_saved_path()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        // The saved label is a different source's, on purpose: the path wins (labels can repeat), as WPF.
        var w = Window(Settings(temp, lastLabel: "case9", lastPath: case14.Key), new[] { case9, case14 });
        Open(w);
        try
        {
            Assert.NotNull(w.StartupLoad);
            Assert.Equal("case14", w.ViewModel.Run.CaseName);
            Assert.True(w.ViewModel.Run.HasRun);
            Assert.Same(w.ViewModel.Sources[1], w.ViewModel.SelectedSource);
            PumpUntil(() => w.ViewModel.Run.LastFrame is { Model.BusCount: 14 }, "a case14 frame");
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void An_older_settings_file_with_only_a_label_loads_the_source_with_that_label()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        var w = Window(Settings(temp, lastLabel: "case14", lastPath: null), new[] { case9, case14 });
        Open(w);
        try { Assert.Equal("case14", w.ViewModel.Run.CaseName); }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void With_no_last_used_source_or_one_no_longer_listed_the_first_entry_is_loaded()
    {
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        foreach ((string? label, string? path) in new (string?, string?)[] { (null, null), ("gone", Path.Combine(Path.GetTempPath(), "gone.m")) })
        {
            using var temp = new TempFolder();
            var w = Window(Settings(temp, label, path), new[] { case9, case14 });
            Open(w);
            try
            {
                Assert.Equal("case9", w.ViewModel.Run.CaseName);
                Assert.Same(w.ViewModel.Sources[0], w.ViewModel.SelectedSource);
            }
            finally { Close(w); }
        }
    }

    [AvaloniaFact]
    public void A_last_used_source_that_is_listed_but_refused_shows_its_refusal_and_nothing_else_is_loaded()
    {
        using var temp = new TempFolder();
        // A listed source whose folder holds no case: the host refuses it. WPF then logs the refusal and stays empty:
        // the first-entry fallback is only for a source that is not listed.
        var missing = new ModelSource("missing", temp.File("no-such-case"));
        ModelSource case9 = Matpower("case9");
        var w = Window(Settings(temp, "missing", missing.Key), new[] { case9, missing });
        Open(w);
        try
        {
            Assert.Same(w.ViewModel.Sources[1], w.ViewModel.SelectedSource);
            Assert.NotNull(w.ViewModel.Run.LoadError);
            Assert.False(w.ViewModel.Run.HasRun);
            Assert.Equal("", w.ViewModel.Run.CaseName);
            Assert.Contains(w.ViewModel.Dashboard.Events, e => e == $"--:--  {w.ViewModel.Run.LoadError}");
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void With_auto_load_off_the_window_opens_empty_and_reads_no_launch_arguments()
    {
        using var temp = new TempFolder();
        bool refusedCalled = false;
        var w = Window(Settings(temp, "case9", Matpower("case9").Key), new[] { Matpower("case9") },
            args: new[] { "--no-such-option" }, autoLoad: false, refused: (_, _) => refusedCalled = true);
        Open(w);
        try
        {
            Assert.Null(w.StartupLoad);
            Assert.Null(w.LaunchPlan);
            Assert.False(refusedCalled);
            Assert.False(w.ViewModel.Run.HasRun);
            Assert.Null(w.ViewModel.SelectedSource);
            Assert.Equal(MainWindowViewModel.NoCaseText, w.ViewModel.StatusText);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_model_text_and_tab_arguments_choose_the_case_and_the_tab_as_WPF()
    {
        using var temp = new TempFolder();
        ModelSource case9 = Matpower("case9"), case14 = Matpower("case14");
        // The model text wins over the last-used source; --tab opens the tab whose header contains it.
        var w = Window(Settings(temp, "case9", case9.Key), new[] { case9, case14 }, args: new[] { "--tab", "operator", "case14" });
        Open(w);
        try
        {
            Assert.Equal("case14", w.ViewModel.Run.CaseName);
            Assert.Equal("OPERATOR", (w.TabStrip.SelectedItem as TabItem)?.Header);
            Assert.True(w.LaunchPlan!.Ok);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Refused_launch_arguments_load_nothing_and_report_the_WPF_refusal()
    {
        using var temp = new TempFolder();
        string? reported = null;
        ModelSource case9 = Matpower("case9");
        string[] args = { "--zone", "atlantis" };
        var w = Window(Settings(temp, "case9", case9.Key), new[] { case9 }, args: args, refused: (_, text) => reported = text);
        Open(w);
        try
        {
            // The same refusal text Core gives for the same arguments matched to the same pickers.
            DesktopLaunchPlan expected = DesktopLaunchArgs.Parse(args).Resolve(
                BridgeChoices.Zones.Select(z => z.Case).ToArray(),
                BridgeChoices.Starts.Select(b => (int)Math.Round(b.Back.TotalDays)).ToArray(),
                BridgeChoices.Sources.Select(x => x.Source).ToArray(),
                w.TabStrip.Items.Cast<TabItem>().Select(t => (string)t.Header!).ToArray(),
                new[] { "case9" });
            Assert.False(expected.Ok);
            Assert.Equal(expected.RefusalText(), reported);
            Assert.False(w.ViewModel.Run.HasRun);
            Assert.Null(w.StartupLoad);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void An_empty_catalogue_is_reported_in_the_WPF_words_and_nothing_is_loaded()
    {
        using var temp = new TempFolder();
        var w = Window(Settings(temp, null, null), Array.Empty<ModelSource>());
        Open(w);
        try
        {
            Assert.Equal(MainWindow.NoModelsText, w.ViewModel.ProblemText);
            Assert.False(w.ViewModel.Run.HasRun);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_bridge_arguments_preselect_the_pickers_and_a_last_used_bridge_connects_at_open()
    {
        using var server = new FakeBridgeServer();
        using var temp = new TempFolder();
        var bridge = new ModelSource("Live bridge", "", Bridge: server.Endpoint);
        var w = Window(Settings(temp, "Live bridge", bridge.Key, allowUnencrypted: true), new[] { Matpower("case9"), bridge },
            args: new[] { "--zone", "ukpn", "--back", "30", "--bridge-source", "estimated" });
        w.Show();
        try
        {
            FakeBridgeConnection c = server.NextConnection(() => Dispatcher.UIThread.RunJobs());
            JsonObject start = c.Next("start", () => Dispatcher.UIThread.RunJobs());
            Await(w.StartupLoad!);
            // Exactly one start: the preselection fired no restart, and the stream starts on the chosen zone, start
            // and source (WPF: _bridgeCase and _bridgeSource set with the pickers before connecting).
            Assert.Equal("zone-ukpn", (string?)start["case"]);
            Assert.Equal("estimated", (string?)start["source"]);
            Assert.InRange(DateTime.UtcNow - GridSim.Core.IO.BridgeProtocol.ParseUtc((string?)start["start"]),
                TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
            Assert.Single(c.History, m => (string?)m["action"] == "start");
            BridgeSession s = w.ViewModel.Run.Bridge!;
            Assert.Same(BridgeChoices.Zones[1], s.SelectedZone);
            Assert.Same(BridgeChoices.Starts[4], s.SelectedStart);
            Assert.Same(BridgeChoices.Sources[1], s.SelectedSource);
            Assert.True(w.ViewModel.Run.IsBridge);
            Assert.Equal("CONNECTING  *  zone-ukpn", w.ViewModel.Dashboard.BridgeStatus);
        }
        finally { Close(w); }
    }
}
