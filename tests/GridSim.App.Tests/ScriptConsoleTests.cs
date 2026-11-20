using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GridSim.App.Scripting;
using GridSim.Scripting;
using GridSim.Ui.Models;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// RC4: the desktop Script Console. It hosts GridSim.Scripting (no second interpreter): a typed line, a script file, check-only mode,
/// the errors with line and column, the recording of a session as .gss and .run.json exactly as gridsim-script writes it, the work
/// off the UI thread, and the File menu entry.
/// </summary>
public sealed class ScriptConsoleTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string Case9 => Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m");
    private static string Q(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
    private static string LoadLine => $"LOAD CASE \"{Q(Case9)}\" FREQUENCY 60 HZ";

    private static ScriptConsoleViewModel Make(TempFolder temp) => new(temp.Path, temp.File("sessions"));

    private static string WriteScript(TempFolder temp, string name, params string[] lines)
    {
        string p = temp.File(name);
        File.WriteAllText(p, string.Join("\n", lines) + "\n");
        return p;
    }

    // ------------------------------------------------------------------ typed lines

    [Fact]
    public async Task A_typed_script_runs_shows_its_output_and_is_recorded_as_gss_and_run_json()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        await vm.SubmitAsync(LoadLine);
        await vm.SubmitAsync("SOLVE POWERFLOW");
        await vm.SubmitAsync("PRINT VM OF BUS \"5\"");
        Assert.Contains("loaded \"case9\"", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("power flow converged", vm.OutputText, StringComparison.Ordinal);
        Assert.Equal(new[] { LoadLine, "SOLVE POWERFLOW", "PRINT VM OF BUS \"5\"" }, vm.History);

        string gss = vm.SessionGssPath!;
        Assert.True(File.Exists(gss));
        ConsoleRecording done = vm.EndSession()!;
        string run = Path.ChangeExtension(gss, ".run.json");
        Assert.Equal(run, vm.SessionRunRecordPath);
        Assert.Equal(done.GssBytes, File.ReadAllBytes(gss));
        Assert.Equal(done.RunRecordBytes, File.ReadAllBytes(run));
        Assert.StartsWith("# gridsim-script console session, recorded; refused lines are kept as comments\nGRAMMAR 1\n", File.ReadAllText(gss), StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"gridsim-script-run/1\"", File.ReadAllText(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_session_is_byte_for_byte_the_session_gridsim_script_records()
    {
        using var temp = new TempFolder();
        string[] lines = [LoadLine, "SOLVE POWERFLOW", "# a note", "TRIP LINE \"nope\"", "TRIP LINE \"4-5\"", "SOLVE POWERFLOW",
            "IF VM OF BUS \"5\" < 0.95 PU", "  PRINT VM OF BUS \"5\"", "END"];
        ScriptConsoleViewModel vm = Make(temp);
        foreach (string l in lines) await vm.SubmitAsync(l);
        ConsoleRecording panel = vm.EndSession()!;

        // gridsim-script's interactive mode is InteractiveConsole (Program.Repl calls it): same lines, same recording.
        var cli = new InteractiveConsole(temp.Path);
        foreach (string l in lines) cli.Submit(l);
        ConsoleRecording expected = cli.Finish();
        Assert.Equal(expected.GssText, panel.GssText);
        Assert.Equal(expected.RunRecordJson, panel.RunRecordJson);
        Assert.Contains("# REFUSED (6:", panel.GssText, StringComparison.Ordinal);   // the refused line is kept as a comment

        // The recorded .gss, run as a script file, reaches the same statements (same canonical script hash).
        RunResult again = Runner.Run(panel.GssBytes, new RunOptions(temp.Path));
        Assert.True(again.Ok, again.Error?.Display);
        using var doc = System.Text.Json.JsonDocument.Parse(panel.RunRecordJson);
        Assert.Equal(doc.RootElement.GetProperty("canonicalScriptSha256").GetString(), again.Session.CanonicalScriptSha256);
    }

    [Fact]
    public async Task A_refused_line_is_shown_with_line_and_column_and_the_session_goes_on()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        await vm.SubmitAsync("SOLVE POWERFLOW");   // no case loaded
        Assert.NotNull(vm.LastError);
        Assert.Contains($"refused: line {vm.LastError!.Pos.Line}, column {vm.LastError.Pos.Column}", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("column", vm.StatusText, StringComparison.Ordinal);
        await vm.SubmitAsync(LoadLine);
        Assert.Null(vm.LastError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_block_is_read_over_several_lines_and_the_prompt_says_so()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        await vm.SubmitAsync("IF 1 MW < 2 MW");
        Assert.Equal("...>", vm.Prompt);
        await vm.SubmitAsync("  PRINT 1 MW");
        await vm.SubmitAsync("END");
        Assert.Equal("gss>", vm.Prompt);
        Assert.Contains("1 MW", vm.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_and_exit_are_the_consoles_own_words_and_history_walks_with_up_and_down()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        await vm.SubmitAsync("help");
        Assert.Contains("verbs: ", vm.OutputText, StringComparison.Ordinal);
        await vm.SubmitAsync("LET a = 1 MW");
        await vm.SubmitAsync("LET b = 2 MW");
        Assert.Equal("LET b = 2 MW", vm.HistoryPrevious(""));
        Assert.Equal("LET a = 1 MW", vm.HistoryPrevious(""));
        Assert.Equal("help", vm.HistoryPrevious(""));
        Assert.Equal("help", vm.HistoryPrevious(""));
        Assert.Equal("LET a = 1 MW", vm.HistoryNext());
        Assert.Equal("LET b = 2 MW", vm.HistoryNext());
        Assert.Equal("", vm.HistoryNext());
        await vm.SubmitAsync("exit");
        Assert.NotNull(vm.SessionRunRecordPath);
        Assert.True(File.Exists(vm.SessionRunRecordPath));
        await vm.SubmitAsync("LET c = 3 MW");   // a new session starts
        Assert.NotNull(vm.SessionGssPath);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public async Task A_script_file_runs_and_its_run_record_is_what_gridsim_script_run_gives()
    {
        using var temp = new TempFolder();
        string script = WriteScript(temp, "outage.gss", "GRAMMAR 1", LoadLine, "SOLVE POWERFLOW", "SELECT ENGINE LIVE STEP 20 MS",
            "AT 100 MS TRIP GENERATOR \"G2\"", "STEP SIM 500 MS", "REPORT SIM");
        ScriptConsoleViewModel vm = Make(temp);
        await vm.RunFileAsync(script);
        Assert.Null(vm.LastError);
        Assert.Contains("stepped 25 x 20 ms", vm.OutputText, StringComparison.Ordinal);

        var sw = new StringWriter { NewLine = "\n" };
        RunResult cli = Runner.Run(File.ReadAllBytes(script), new RunOptions(temp.Path, sw));
        Assert.Equal(cli.RecordJson, vm.LastRunRecordJson);
        Assert.Equal(cli.RecordBytes, File.ReadAllBytes(temp.File(Path.Combine("sessions", "outage.run.json"))));
        Assert.Contains(sw.ToString().TrimEnd('\n'), vm.OutputText.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_file_error_is_shown_as_file_line_column()
    {
        using var temp = new TempFolder();
        string script = WriteScript(temp, "bad.gss", "GRAMMAR 1", LoadLine, "TRIP LINE \"nope\"");
        ScriptConsoleViewModel vm = Make(temp);
        await vm.RunFileAsync(script);
        Assert.NotNull(vm.LastError);
        Assert.Equal(3, vm.LastError!.Pos.Line);
        Assert.Contains("bad.gss:3:", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("error:", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("line 3, column", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_only_parses_and_runs_nothing()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        string good = WriteScript(temp, "good.gss", "GRAMMAR 1", LoadLine, "SOLVE POWERFLOW");
        await vm.CheckFileAsync(good);
        Assert.Null(vm.LastError);
        Assert.Contains("good.gss: grammar 1, syntax and procedures ok", vm.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("loaded", vm.OutputText, StringComparison.Ordinal);
        Assert.Null(vm.LastRunRecordJson);

        string bad = WriteScript(temp, "bad.gss", "GRAMMAR 1", "SOLVE POWERFLOW", "FROB 3");
        await vm.CheckFileAsync(bad);
        Assert.Equal(3, vm.LastError!.Pos.Line);
        Assert.Contains("bad.gss:3:", vm.OutputText, StringComparison.Ordinal);

        await vm.CheckFileAsync(temp.File("missing.gss"));
        Assert.Contains("could not be read", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    // ------------------------------------------------------------------ the open case

    [Fact]
    public async Task Load_open_case_types_the_load_for_the_case_the_app_has_open()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        await vm.LoadOpenCaseAsync();
        Assert.Contains("cannot be loaded by a script", vm.StatusText, StringComparison.Ordinal);   // none open

        vm.OpenCase = new ModelSource("case9", Path.GetDirectoryName(Case9)!, MFile: Case9);
        Assert.Equal(Path.GetFullPath(Case9), vm.OpenCasePath());
        await vm.LoadOpenCaseAsync();
        Assert.Contains("loaded \"case9\"", vm.OutputText, StringComparison.Ordinal);
        Assert.Equal("LOAD CASE \"" + Q(Path.GetFullPath(Case9)) + "\"", vm.History[^1]);

        vm.OpenCase = new ModelSource("run", temp.Path, ReplayDir: temp.Path);   // a replay folder is not a script case
        Assert.Null(vm.OpenCasePath());
    }

    // ------------------------------------------------------------------ off the UI thread

    [AvaloniaFact]
    public async Task Statements_and_files_run_off_the_ui_thread_and_one_at_a_time()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        int ui = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        using var inGate = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        vm.Gate = (_, _) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            inGate.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return null;
        };
        Task first = vm.SubmitAsync("LET a = 1 MW");
        Assert.True(inGate.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(vm.IsBusy);
        await vm.SubmitAsync("LET b = 2 MW");   // refused while one runs, and the UI thread is free to say so
        Assert.Contains("already running", vm.StatusText, StringComparison.Ordinal);
        release.Set();
        await first;
        Assert.False(vm.IsBusy);
        Assert.Single(threads);
        Assert.NotEqual(ui, threads[0]);

        threads.Clear();
        string script = WriteScript(temp, "t.gss", "GRAMMAR 1", "LET c = 3 MW");
        await vm.RunFileAsync(script);
        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.NotEqual(ui, t));
        Assert.Equal(ui, Environment.CurrentManagedThreadId);   // back on the UI context afterwards
    }

    // ------------------------------------------------------------------ the window and the menu

    [AvaloniaFact]
    public async Task The_window_sends_the_input_line_shows_the_output_and_ends_the_session_when_closed()
    {
        using var temp = new TempFolder();
        ScriptConsoleViewModel vm = Make(temp);
        var w = new ScriptConsoleWindow(vm);
        w.Show();
        try
        {
            w.FindControl<TextBox>("InputBox")!.Text = "PRINT 1 MW";
            await w.SendAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", w.FindControl<TextBox>("InputBox")!.Text);
            Assert.Contains("1 MW", w.FindControl<TextBox>("OutputBox")!.Text, StringComparison.Ordinal);
            Assert.Equal("gss>", w.FindControl<TextBlock>("PromptText")!.Text);
            Assert.True(w.FindControl<Button>("EndButton")!.IsEnabled);
            Assert.False(w.FindControl<Button>("LoadCaseButton")!.IsEnabled);   // no open case
            string gss = vm.SessionGssPath!;
            w.Close();
            Assert.True(File.Exists(Path.ChangeExtension(gss, ".run.json")));   // closing the window ends and records the session
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void File_menu_has_a_Script_console_entry_that_opens_the_console()
    {
        using var temp = new TempFolder();
        var w = new MainWindow(TestShell.Options(temp));
        w.Show();
        try
        {
            MenuItem file = w.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().First();
            MenuItem item = file.Items.OfType<MenuItem>().Single(i => (string?)i.Tag == ScriptConsoleWindow.CommandId);
            Assert.Equal("Script console...", (string?)item.Header);
            Assert.Empty(w.OwnedWindows.OfType<ScriptConsoleWindow>());
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ScriptConsoleWindow opened = Assert.Single(w.OwnedWindows.OfType<ScriptConsoleWindow>());
            opened.Close();
        }
        finally { w.Close(); }
    }
}
