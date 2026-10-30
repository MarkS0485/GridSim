using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.App.Dialogs;
using GridSim.App.Shell;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme shell slice (v2 + pickers, 2026-10-20), in the Avalonia window (headless). The OS pickers are answered
/// through the test seams (ShellOptions.Pickers, IAuthorPrompts.PickSaveAsFolderAsync), so no real dialog is opened.
/// <list type="bullet">
/// <item><description>The File menu offers Open case folder... and Open v2 case... after Open run folder..., without
/// shortcuts; the app's catalogue is the cross-platform one (WPF's plus v2).</description></item>
/// <item><description>Open v2 case...: the picked manifest is checked, joins the case list as "v2  &lt;file name&gt;",
/// is loaded through the run pipeline's v2 path and remembered by path; the Open case list then also offers the other v2
/// manifests in its folder. An invalid manifest is refused with the reason; a cancel does nothing; a refused pick says
/// why.</description></item>
/// <item><description>Open case folder...: a folder without system.json is refused; a v1 case folder is loaded and
/// joins the list. Open run folder... goes through the same picker seam.</description></item>
/// <item><description>Author mode's Save As... asks for the folder through the picker; the refusal rules still fire
/// after the pick (a non-empty folder, a bundled folder, the case's own folder) and nothing is written; a cancel writes
/// nothing; a new empty folder gets the copy.</description></item>
/// </list>
/// </summary>
public sealed class ShellV2PickersTests
{
    // ---------------------------------------------------------------- scripted answers

    // The OS pickers, answered from queues; every question is recorded ("folder: title [start]" / "file: title [patterns]").
    private sealed class ScriptedPickers : IShellPickers
    {
        public Queue<PickResult> Folders { get; } = new();
        public Queue<PickResult> Files { get; } = new();
        public List<string> Asked { get; } = new();

        public Task<PickResult> PickFolderAsync(string title, string? startFolder)
        {
            Asked.Add("folder: " + title + " [" + startFolder + "]");
            return Task.FromResult(Folders.Dequeue());
        }

        public Task<PickResult> PickFileAsync(string title, string? startFolder, string typeName, IReadOnlyList<string> patterns)
        {
            Asked.Add("file: " + title + " [" + string.Join(",", patterns) + "]");
            return Task.FromResult(Files.Dequeue());
        }
    }

    // Author mode's questions; Save As's folder comes from Picks (AskTextAsync is never expected, so it fails loud).
    private sealed class PickingPrompts : IAuthorPrompts
    {
        public Queue<string?> Picks { get; } = new();
        public List<string> Asked { get; } = new();

        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message) => throw new InvalidOperationException("unexpected: dirty " + message);

        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException("unexpected: confirm " + message);

        public Task<string?> AskTextAsync(string title, string message, string initial) => throw new InvalidOperationException("unexpected: text " + title);

        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => throw new InvalidOperationException("unexpected: fields " + title);

        public Task<string?> PickSaveAsFolderAsync(string title, string message, string suggested)
        {
            Asked.Add("pick: " + title + " [" + suggested + "]");
            return Task.FromResult(Picks.Dequeue());
        }
    }

    // ---------------------------------------------------------------- fixtures

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // data/uk written as a JSON v2 case into a temp folder (read from the repo, written only to temp).
    private static string WriteV2Case(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        string manifest = Path.Combine(folder, fileName);
        V2CaseWriter.Write(uk, manifest);
        return manifest;
    }

    private static ShellOptions Options(TempFolder temp, ScriptedPickers pickers, IReadOnlyList<ModelSource>? catalogue = null,
        PickingPrompts? prompts = null) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => (catalogue ?? Array.Empty<ModelSource>()).ToList(),
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
        Pickers = _ => pickers,
        AuthorPrompts = prompts is null ? null : _ => prompts,
        AuthorBundledRoots = new[] { temp.File("bundled") },
        AuthorUserCasesFolder = temp.File("user-cases"),
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

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "a task to finish");
        return task.GetAwaiter().GetResult();
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

    private static void Run(MainWindow w, string commandId) => Await(w.ViewModel.Registry.Get(commandId).RunAsync());

    // =================================================================================================================

    [AvaloniaFact]
    public void The_File_menu_offers_Open_case_folder_and_Open_v2_case_after_Open_run_folder_without_shortcuts()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(Options(temp, new ScriptedPickers()));
        w.Show();
        try
        {
            Menu bar = w.FindControl<Menu>("MenuBar")!;
            MenuItem file = bar.Items.OfType<MenuItem>().First();
            Assert.Equal("_File", (string?)file.Header);
            List<MenuItem> items = file.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Open case...", "Open run folder...", "Open case folder...", "Open v2 case...", "Audit log...", "Exit" },
                items.Select(i => (string?)i.Header));
            Assert.Equal(new[] { ShellCommands.OpenCase, ShellCommands.OpenRunFolder, CaseFileCommands.OpenCaseFolder, CaseFileCommands.OpenV2Case,
                GridSim.App.Tabs.AuditLogWindow.CommandId, ShellCommands.Exit }, items.Select(i => (string?)i.Tag));
            Assert.Null(items[2].InputGesture);
            Assert.Null(items[3].InputGesture);
            Assert.Equal(3, w.KeyBindings.Count);   // the shortcuts are exactly the three shell ones, as before

            // The real window's actions run them; and its catalogue is the cross-platform one (WPF's list plus v2).
            Assert.Same(w.Actions, w.CaseFiles);
            var settings = new AppSettings();
            var skippedA = new List<string>();
            var skippedB = new List<string>();
            Assert.Equal(ModelCatalog.Discover(settings, skippedA, CatalogOptions.CrossPlatform), new ShellOptions().DiscoverCases(settings, skippedB));
            Assert.Equal(skippedA, skippedB);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Open_v2_case_loads_the_picked_manifest_through_the_v2_path_and_lists_it_and_its_neighbours()
    {
        using var temp = new TempFolder();
        string folder = temp.File("my-v2");
        string manifest = WriteV2Case(folder, "uk.case.json");
        string neighbour = WriteV2Case(folder, "second.case.json");
        File.WriteAllText(Path.Combine(folder, "bad.case.json"), "{ \"schema\": \"gridsim-network/2\" }");
        var pickers = new ScriptedPickers();
        var w = new MainWindow(Options(temp, pickers));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            var actions = Assert.IsType<ShellActions>(w.Actions);

            // A cancel does nothing at all.
            pickers.Files.Enqueue(PickResult.Cancelled);
            Run(w, CaseFileCommands.OpenV2Case);
            Assert.Null(vm.ChosenCase);
            Assert.Null(vm.ProblemText);
            Assert.Empty(vm.Sources);

            // A pick the platform cannot use is said in the status bar, and nothing is loaded.
            pickers.Files.Enqueue(PickResult.Refused("x is not a file on this computer, so it cannot be read here."));
            Run(w, CaseFileCommands.OpenV2Case);
            Assert.Equal("x is not a file on this computer, so it cannot be read here.", vm.ProblemText);
            Assert.Null(vm.ChosenCase);

            // The picked manifest: loaded through the run pipeline's v2 path, in the list as "v2  uk.case.json", the
            // picker on it, remembered by path.
            pickers.Files.Enqueue(PickResult.Chosen(manifest));
            Run(w, CaseFileCommands.OpenV2Case);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.Null(vm.ProblemText);
            Assert.Equal(Path.GetFullPath(manifest), vm.Run.CurrentSource!.V2Case);
            ModelSource listed = Assert.Single(vm.Sources);
            Assert.Equal("v2  uk.case.json", listed.Label);
            Assert.Equal("v2  uk.case.json", CaseListLabel.Text(listed));
            Assert.Same(listed, vm.SelectedSource);
            Assert.Equal(Path.GetFullPath(manifest), vm.Settings.LastSourcePath);
            Assert.Equal("v2  uk.case.json", vm.Settings.LastSource);
            Assert.Equal(new[] { Path.GetFullPath(folder) }, vm.OpenedFolders);
            Assert.Equal(new[] { "file: Choose a GridSim JSON v2 case manifest (*.case.json) [*.case.json]" }, pickers.Asked.Distinct());

            // The Open case list now offers it once, the other v2 case in its folder too, and records the bad one.
            (List<ModelSource> list, List<string> skipped) = actions.CaseList();
            Assert.Equal(new[] { "v2  uk.case.json", "v2  second.case.json" }, list.Select(s => s.Label));
            Assert.Equal(Path.GetFullPath(neighbour), list[1].V2Case);
            string bad = Path.Combine(Path.GetFullPath(folder), "bad.case.json");
            Assert.Equal(new[] { $"{bad}: not a v2 case manifest: schema \"gridsim-network/2\" (expected \"gridsim-case/2\")" }, skipped);
            Assert.Equal(0, CaseChoice.LastUsedIndex(list, vm.Settings));   // the last-used choice finds it by path

            // An invalid manifest is refused in the run's error area with the reason; nothing else changes.
            pickers.Files.Enqueue(PickResult.Chosen(bad));
            Run(w, CaseFileCommands.OpenV2Case);
            Assert.Equal(ShellActions.NotAV2CaseText(bad, "not a v2 case manifest: schema \"gridsim-network/2\" (expected \"gridsim-case/2\")"),
                vm.Run.LoadError);
            Assert.Equal(Path.GetFullPath(manifest), vm.Run.CurrentSource!.V2Case);
            Assert.Single(vm.Sources);
            Assert.Single(vm.OpenedCases);

            // Opening the same manifest again adds nothing; the picker opens in the folder last used.
            pickers.Files.Enqueue(PickResult.Chosen(manifest));
            Run(w, CaseFileCommands.OpenV2Case);
            Assert.Single(vm.Sources);
            Assert.Single(vm.OpenedCases);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Open_case_folder_refuses_a_folder_without_system_json_and_loads_a_v1_case_folder()
    {
        using var temp = new TempFolder();
        string empty = temp.File("not-a-case");
        Directory.CreateDirectory(empty);
        string uk = Path.Combine(RepoRoot(), "data", "uk");   // read only
        var pickers = new ScriptedPickers();
        var w = new MainWindow(Options(temp, pickers));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;

            pickers.Folders.Enqueue(PickResult.Chosen(empty));
            Run(w, CaseFileCommands.OpenCaseFolder);
            Assert.Equal(ShellActions.NotACaseFolderText(empty), vm.Run.LoadError);
            Assert.Null(vm.ChosenCase);
            Assert.Empty(vm.Sources);
            Assert.Empty(vm.OpenedCases);

            pickers.Folders.Enqueue(PickResult.Chosen(uk));
            Run(w, CaseFileCommands.OpenCaseFolder);
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            ModelSource listed = Assert.Single(vm.Sources);
            Assert.Equal("Case  uk", listed.Label);
            Assert.Null(listed.V2Case);
            Assert.Equal(Path.GetFullPath(uk), Path.GetFullPath(vm.Run.CurrentSource!.Dir));
            Assert.Equal(Path.GetFullPath(uk), vm.Settings.LastSourcePath);
            Assert.Equal(new[]
            {
                "folder: Choose a GridSim case folder (with a system.json) []",
                "folder: Choose a GridSim case folder (with a system.json) []",
            }, pickers.Asked);

            // Open run folder... asks the same picker seam: a cancel does nothing, a refused pick is said.
            pickers.Folders.Enqueue(PickResult.Cancelled);
            Run(w, ShellCommands.OpenRunFolder);
            Assert.Null(vm.ProblemText);
            pickers.Folders.Enqueue(PickResult.Refused(StoragePickers.NoFolderPickerText));
            Run(w, ShellCommands.OpenRunFolder);
            Assert.Equal(StoragePickers.NoFolderPickerText, vm.ProblemText);
            Assert.Equal("folder: Choose a GridSim run directory (with an exports/ folder) []", pickers.Asked[^1]);
            Assert.Equal(Path.GetFullPath(uk), Path.GetFullPath(vm.Run.CurrentSource!.Dir));   // still the case
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Save_As_writes_to_the_picked_folder_and_the_refusals_still_fire_after_the_pick()
    {
        using var temp = new TempFolder();
        string caseFolder = temp.File("case");
        string manifest = WriteV2Case(caseFolder, "uk.case.json");
        var pickers = new ScriptedPickers();
        var prompts = new PickingPrompts();
        ModelSource source = ModelCatalog.V2CaseSource(manifest);
        var w = new MainWindow(Options(temp, pickers, new[] { source }, prompts));
        w.Show();
        try
        {
            MainWindowViewModel vm = w.ViewModel;
            Await(vm.LoadCaseAsync(source));
            Assert.False(vm.Run.HasError, vm.Run.ErrorText);
            Assert.True(Await(w.AuthorMode.EnterAsync()));
            AssetAuthorModel author = vm.Tabs.Assets.Author!;
            Assert.False(author.Authoring.IsBundled);
            string suggested = author.Authoring.SuggestSaveAsFolder();

            // A non-empty folder: refused after the pick, nothing written into it.
            string full = temp.File("full");
            Directory.CreateDirectory(full);
            File.WriteAllText(Path.Combine(full, "keep.txt"), "mine");
            prompts.Picks.Enqueue(full);
            Run(w, AuthorCommands.SaveAs);
            Assert.Contains("is not empty", author.Status, StringComparison.Ordinal);
            Assert.Equal(new[] { "keep.txt" }, Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName));

            // A bundled folder (a shipped data folder under the test's bundled root): refused, nothing created.
            string bundled = Path.Combine(temp.File("bundled"), "uk", "copy");
            prompts.Picks.Enqueue(bundled);
            Run(w, AuthorCommands.SaveAs);
            Assert.Contains("inside a bundled data folder", author.Status, StringComparison.Ordinal);
            Assert.False(Directory.Exists(bundled));

            // The case's own folder (and a folder inside it): refused.
            prompts.Picks.Enqueue(caseFolder);
            Run(w, AuthorCommands.SaveAs);
            Assert.Contains("this case's own folder", author.Status, StringComparison.Ordinal);
            string inside = Path.Combine(caseFolder, "sub");
            prompts.Picks.Enqueue(inside);
            Run(w, AuthorCommands.SaveAs);
            Assert.Contains("this case's own folder", author.Status, StringComparison.Ordinal);
            Assert.False(Directory.Exists(inside));

            // A cancel writes nothing.
            prompts.Picks.Enqueue(null);
            Run(w, AuthorCommands.SaveAs);
            Assert.Equal("Save As cancelled; nothing was written.", author.Status);
            Assert.Equal(Path.GetFullPath(caseFolder), Path.GetFullPath(author.Authoring.RootPath));

            // A new empty folder made in the picker: the copy is written there and the editor carries on with it.
            string target = temp.File("my-copy");
            Directory.CreateDirectory(target);
            prompts.Picks.Enqueue(target);
            Run(w, AuthorCommands.SaveAs);
            Assert.True(File.Exists(Path.Combine(target, "uk.case.json")), author.Status);
            Assert.True(File.Exists(Path.Combine(target, V2CaseWriter.NetworkFileNameFor(Path.Combine(target, "uk.case.json")))), author.Status);
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(author.Authoring.RootPath));

            // Every Save As asked the picker (never the typed-path prompt), each time with the suggestion of the moment.
            Assert.Equal(6, prompts.Asked.Count);
            Assert.All(prompts.Asked, a => Assert.StartsWith("pick: Save As [", a, StringComparison.Ordinal));
            Assert.Equal("pick: Save As [" + suggested + "]", prompts.Asked[0]);
            Assert.Empty(pickers.Asked);   // Save As uses Author mode's own question, not the File menu's pickers
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void The_default_Save_As_folder_question_is_the_typed_path_prompt_so_existing_answers_still_work()
    {
        // A prompts implementation written before the picker existed answers Save As through AskTextAsync (the default
        // interface member forwards there, with the same title, message and suggestion).
        var legacy = new TextOnlyPrompts();
        IAuthorPrompts prompts = legacy;
        string? answer = Await(prompts.PickSaveAsFolderAsync("Save As", "message", "C:/suggested"));
        Assert.Equal("typed", answer);
        Assert.Equal(new[] { "Save As|message|C:/suggested" }, legacy.Asked);
    }

    private sealed class TextOnlyPrompts : IAuthorPrompts
    {
        public List<string> Asked { get; } = new();
        public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message) => throw new InvalidOperationException();
        public Task<bool> ConfirmAsync(string title, string message) => throw new InvalidOperationException();
        public Task<string?> AskTextAsync(string title, string message, string initial)
        {
            Asked.Add(title + "|" + message + "|" + initial);
            return Task.FromResult<string?>("typed");
        }
        public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
            => throw new InvalidOperationException();
    }
}
