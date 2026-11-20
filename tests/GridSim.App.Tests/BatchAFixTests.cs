using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Run;
using GridSim.App.Shell;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>Post-RC3 audit batch A: APP-01 (serialised loads), APP-21 (close asks in AUTHOR), APP-07 (crash log), APP-23 (atomic settings).</summary>
public sealed class BatchAFixTests
{
    private sealed class ScriptedPrompts : IAuthorPrompts
    {
        public Queue<DirtyChoice> Dirty { get; } = new();
        public List<string> Asked { get; } = new();

        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message)
        {
            Asked.Add(message);
            return Task.FromResult(Dirty.Dequeue());
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> AskTextAsync(string title, string message, string initial) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>?>(null);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("GridSim.slnx not found above " + AppContext.BaseDirectory);
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
        while (clock.ElapsedMilliseconds < ms) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
    }

    private static void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        task.GetAwaiter().GetResult();
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source, ScriptedPrompts prompts) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        AuthorPrompts = _ => prompts,
        AuthorUserCasesFolder = temp.File("user-cases"),
    };

    private static ModelSource Case9()
    {
        string file = Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
        Assert.True(File.Exists(file), "missing test case " + file);
        return new ModelSource("case9", Path.GetDirectoryName(file)!, file);
    }

    // ---------------------------------------------------------------- APP-01

    [AvaloniaFact]
    public void APP01_overlapping_loads_leave_exactly_one_play_loop()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, Case9(), new ScriptedPrompts()));
        w.Show();
        try
        {
            RunViewModel run = w.ViewModel.Run;
            int frames = 0;
            run.FrameShown += (_, _) => frames++;
            Await(run.LoadAsync(Case9()));
            PumpUntil(() => frames >= 2, "two frames");

            // Three loads started back to back, as arrowing through the NETWORK picker does.
            Task<string?> a = run.LoadAsync(Case9());
            Task<string?> b = run.LoadAsync(Case9());
            Task<string?> c = run.LoadAsync(Case9());
            Await(Task.WhenAll(a, b, c));
            Assert.Null(a.Result);
            Assert.Null(b.Result);
            Assert.Null(c.Result);

            // One cancel must end every loop: no frame may arrive afterwards.
            Await(run.StopLoopAsync());
            PumpFor(100);
            int after = frames;
            PumpFor(400);
            Assert.Equal(after, frames);
        }
        finally
        {
            w.Close();
            if (w.StopLoop is { } stop) Await(stop);
        }
    }

    // ---------------------------------------------------------------- APP-21

    private static string WriteV2Case(TempFolder temp)
    {
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = temp.File("uk.case.json");
        V2CaseWriter.Write(uk, manifest);
        return manifest;
    }

    [AvaloniaFact]
    public void APP21_closing_in_AUTHOR_with_unsaved_edits_asks_and_Cancel_keeps_the_window()
    {
        using var temp = new TempFolder();
        string manifest = WriteV2Case(temp);
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        var prompts = new ScriptedPrompts();
        var w = new MainWindow(Options(temp, source, prompts));
        w.Show();
        try
        {
            Await(w.ViewModel.LoadCaseAsync(source));
            w.SelectTab(MainWindow.AssetsTabHeader);
            Dispatcher.UIThread.RunJobs();
            AssetsView v = w.AssetsView;
            v.FindControl<Button>("ModeToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Await(v.PendingModeSwitch!);
            AssetAuthorModel author = w.ViewModel.Tabs.Assets.Author!;
            author.Select(author.ClassNode("lines")!.Children[0]);
            Assert.True(author.EditField("rPu", "0.025").Ok);
            Assert.True(author.IsDirty);

            // Cancel: the window stays, in AUTHOR, with the edit kept.
            prompts.Dirty.Enqueue(DirtyChoice.Cancel);
            w.Close();
            Assert.NotNull(w.AuthorClosing);
            Await(w.AuthorClosing!);
            PumpFor(50);
            Assert.True(w.IsVisible);
            Assert.True(w.ViewModel.Tabs.Assets.IsAuthoring);
            Assert.True(author.IsDirty);
            Assert.Single(prompts.Asked);

            // Discard: the window then closes and nothing was written.
            byte[] before = File.ReadAllBytes(manifest);
            prompts.Dirty.Enqueue(DirtyChoice.Discard);
            w.Close();
            Await(w.AuthorClosing!);
            PumpUntil(() => !w.IsVisible, "the window to close");
            Assert.Equal(2, prompts.Asked.Count);
            Assert.Equal(before, File.ReadAllBytes(manifest));
        }
        finally
        {
            if (w.IsVisible) w.Close();
            if (w.StopLoop is { } stop) Await(stop);
        }
    }

    [AvaloniaFact]
    public void APP21_closing_a_clean_window_never_asks()
    {
        using var temp = new TempFolder();
        var prompts = new ScriptedPrompts();
        var w = new MainWindow(Options(temp, Case9(), prompts));
        w.Show();
        w.Close();
        if (w.StopLoop is { } stop) Await(stop);
        Assert.Empty(prompts.Asked);
        Assert.Null(w.AuthorClosing);
    }

    // ---------------------------------------------------------------- APP-07

    [Fact]
    public void APP07_ReportCrash_writes_a_crash_log_under_GRIDSIM_CRASH_DIR()
    {
        using var temp = new TempFolder();
        string? old = Environment.GetEnvironmentVariable(CrashLog.DirectoryVariable);
        Environment.SetEnvironmentVariable(CrashLog.DirectoryVariable, temp.Path);
        try
        {
            string? path = AppCrashHandling.ReportCrash(new InvalidOperationException("boom-app07"));
            Assert.NotNull(path);
            Assert.StartsWith(temp.Path, path, StringComparison.OrdinalIgnoreCase);
            string text = File.ReadAllText(path);
            Assert.Contains("GridSim.App", text, StringComparison.Ordinal);
            Assert.Contains("boom-app07", text, StringComparison.Ordinal);

            // The same exception reported twice (start-up catch, then the domain handler) is one log.
            var again = new InvalidOperationException("twice-app07");
            string? first = AppCrashHandling.ReportCrash(again);
            string? second = AppCrashHandling.ReportCrash(again);
            Assert.NotNull(first);
            Assert.Equal(first, second);   // the repeat is not logged again
        }
        finally { Environment.SetEnvironmentVariable(CrashLog.DirectoryVariable, old); }
    }

    [Fact]
    public void APP07_installing_the_process_handlers_twice_is_harmless()
    {
        AppCrashHandling.InstallProcessHandlers();
        AppCrashHandling.InstallProcessHandlers();
    }

    // ---------------------------------------------------------------- APP-23

    [Fact]
    public void APP23_a_failed_save_leaves_the_previous_settings_file_untouched()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        File.WriteAllText(file, "{\n  \"BridgeEndpoint\": \"wss://bridge.example/ws/bridge\"\n}");
        byte[] original = File.ReadAllBytes(file);
        AppSettings s = ShellSettingsStore.ForFile(file).Load();
        Assert.Null(s.LoadProblem);
        s.Coast = true;

        // A directory squatting on the temporary name makes the write fail before the file is replaced.
        Directory.CreateDirectory(file + ".tmp");
        s.Save();
        Assert.NotNull(s.SaveProblem);
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void APP23_a_good_save_replaces_the_file_and_leaves_no_temporary_file()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        File.WriteAllText(file, "{}");
        AppSettings s = ShellSettingsStore.ForFile(file).Load();
        s.BridgeEndpoint = "wss://bridge.example/ws/bridge";
        s.Save();
        Assert.Null(s.SaveProblem);
        Assert.Contains("wss://bridge.example/ws/bridge", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal("wss://bridge.example/ws/bridge", ShellSettingsStore.ForFile(file).Load().BridgeEndpoint);
    }
}
