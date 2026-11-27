using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.Scripting;
using GridSim.Ui.Models;

namespace GridSim.App.Scripting;

/// <summary>
/// The view model of the desktop Script Console (RC4): the GridSim.Scripting console as a panel. It runs no interpreter of its
/// own. An interactive session is <see cref="InteractiveConsole"/> (the same loop <c>gridsim-script</c> runs, so the session is
/// recorded as a <c>.gss</c> file and a <c>.run.json</c> exactly as that console records it); a script file goes through
/// <see cref="Runner"/>, as <c>gridsim-script run</c> does. Statements run on a worker thread and the result is shown back on the
/// calling (UI) context, one at a time, the way the workbench runs a study (<c>WorkbenchViewModel.RunSelectedAsync</c>); a
/// failure is shown in the output, never thrown to the crash handler.
/// </summary>
public sealed class ScriptConsoleViewModel : ObservableObject, IDisposable
{
    /// <summary>The most output lines the pane keeps (the oldest are dropped; the recording is unaffected).</summary>
    public const int MaxOutputLines = 5000;

    private readonly StringBuilder _output = new();
    private readonly List<string> _history = [];
    private int _historyAt;
    private InteractiveConsole? _console;
    private StreamWriter? _gssWriter;
    private string? _gssPath;
    private bool _busy;
    private int _outputLines;

    /// <param name="baseDirectory">Relative LOAD and EXPORT paths in a typed statement are taken from here.</param>
    /// <param name="recordDirectory">Where an interactive session is recorded (<c>session-yyyyMMdd-HHmmss.gss</c> and <c>.run.json</c>).</param>
    public ScriptConsoleViewModel(string baseDirectory, string recordDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        RecordDirectory = Path.GetFullPath(recordDirectory);
        Append($"gridsim-script {GridSim.Core.BuildInfo.Version}, grammar {Grammar.VersionText}. Type a statement, or 'help'. Sessions are recorded to {RecordDirectory}.");
    }

    /// <summary>For the AXAML loader and the designer only: the current folder, with sessions under it.</summary>
    public ScriptConsoleViewModel() : this(".", Path.Combine(".", "gridsim-sessions")) { }

    public string BaseDirectory { get; }
    public string RecordDirectory { get; }

    /// <summary>The case the main window has open, offered by <see cref="LoadOpenCaseAsync"/>; null when there is none.</summary>
    public ModelSource? OpenCase { get; set; }

    /// <summary>Hosting hook for the session (see <see cref="Session.Gate"/>); the desktop sets none. Tests use it to see the thread a statement runs on.</summary>
    public Func<Statement, Session, string?>? Gate { get; set; }

    /// <summary>The licence gate a <c>LOAD CASE</c> answers to (see <see cref="Session.Licence"/>), exactly as on the command line and in
    /// <c>gridsim-script</c>. Null (the default) resolves the installed licence (<see cref="GridSim.Core.Licensing.LicenceGate.Resolve()"/>)
    /// when a session or run starts.</summary>
    public GridSim.Core.Licensing.LicenceGate? Licence { get; set; }

    private GridSim.Core.Licensing.LicenceGate LicenceForRun() => Licence ?? GridSim.Core.Licensing.LicenceGate.Resolve();

    /// <summary>Raised after every change the window shows.</summary>
    public event EventHandler? Refreshed;

    /// <summary>True while a statement or a script is running off the UI thread.</summary>
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    private string _status = "Ready.";
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The output pane's text.</summary>
    public string OutputText => _output.ToString();

    /// <summary>The prompt the input line shows: <c>gss&gt;</c>, or <c>...&gt;</c> inside a block.</summary>
    public string Prompt => _console?.InBlock == true ? "...>" : "gss>";

    /// <summary>The path of the <c>.gss</c> file the live session is recording to, or null before the first statement.</summary>
    public string? SessionGssPath => _gssPath;

    /// <summary>The path of the finished session's run record, or null until <see cref="EndSession"/>.</summary>
    public string? SessionRunRecordPath { get; private set; }

    /// <summary>The error the last run, check or typed line was refused with (line and column), or null.</summary>
    public ScriptException? LastError { get; private set; }

    /// <summary>The run record (JSON) of the last script file run, or null.</summary>
    public string? LastRunRecordJson { get; private set; }

    /// <summary>The typed lines, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    // ---------------------------------------------------------------- input line

    /// <summary>The previous line of the history (the Up key); returns <paramref name="current"/> when there is none.</summary>
    public string HistoryPrevious(string current)
    {
        if (_history.Count == 0) return current;
        if (_historyAt > 0) _historyAt--;
        return _history[_historyAt];
    }

    /// <summary>The next line of the history (the Down key); an empty line after the last.</summary>
    public string HistoryNext()
    {
        if (_historyAt < _history.Count - 1) { _historyAt++; return _history[_historyAt]; }
        _historyAt = _history.Count;
        return "";
    }

    /// <summary>
    /// Give the console one typed line. Runs on a worker thread; the answer (output, or the refusal with line and column) is
    /// appended back on the calling context. <c>help</c> and <c>exit</c> are the console's own words; <c>exit</c> ends the session
    /// and writes its recording. Refused while another statement is running.
    /// </summary>
    public async Task SubmitAsync(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (IsBusy) { StatusText = "A statement is already running."; Notify(); return; }
        string trimmed = line.Trim();
        if (trimmed.Length > 0) { _history.Add(line); _historyAt = _history.Count; }
        Append(Prompt + " " + line);
        if (_console?.InBlock != true)
        {
            if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Append("verbs: " + string.Join(", ", Grammar.Verbs) + "; FOR, IF and PROC open a block closed by END; see the manual chapter \"Scripting\"");
                Notify();
                return;
            }
            if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                EndSession();
                return;
            }
        }
        IsBusy = true;
        StatusText = "Running...";
        Notify();
        try
        {
            InteractiveConsole console = EnsureConsole();
            ConsoleReply reply = await Task.Run(() => console.Submit(line)).ConfigureAwait(true);
            switch (reply.Kind)
            {
                case ConsoleReplyKind.Refused:
                    LastError = reply.Error;
                    Append("refused: " + reply.Error!.Display);
                    StatusText = $"Refused at line {reply.Error.Pos.Line}, column {reply.Error.Pos.Column}.";
                    break;
                case ConsoleReplyKind.Continued:
                    StatusText = "Block open: type END to close it.";
                    break;
                default:
                    LastError = null;
                    if (reply.Output.Length > 0) Append(reply.Output.TrimEnd('\n'));
                    StatusText = "Ready.";
                    break;
            }
        }
#pragma warning disable CA1031 // Not swallowed: shown in the output.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Append($"error: {ex.GetType().Name}: {ex.Message}");
            StatusText = "The statement failed.";
        }
        finally { IsBusy = false; Notify(); }
    }

    /// <summary>Type <c>LOAD CASE</c> for the case the main window has open (a MATPOWER file or a JSON case folder), as a typed line.</summary>
    public async Task LoadOpenCaseAsync()
    {
        string? path = OpenCasePath();
        if (path is null)
        {
            StatusText = "The open case cannot be loaded by a script (grammar 1 loads a MATPOWER .m file, a PSS/E .raw file or a JSON case folder).";
            Notify();
            return;
        }
        await SubmitAsync("LOAD CASE \"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"").ConfigureAwait(true);
    }

    /// <summary>The full path of the open case when a script can load it, else null.</summary>
    public string? OpenCasePath()
    {
        ModelSource? s = OpenCase;
        if (s is null || s.Bridge is not null || s.ReplayDir is not null || s.V2Case is not null) return null;
        string p = s.MFile ?? s.Dir;
        if (string.IsNullOrWhiteSpace(p)) return null;
        string full = Path.GetFullPath(p);
        if (s.MFile is not null) return File.Exists(full) && full.EndsWith(".m", StringComparison.OrdinalIgnoreCase) ? full : null;
        return Directory.Exists(full) ? full : null;
    }

    // ---------------------------------------------------------------- script files

    /// <summary>
    /// Run a <c>.gss</c> file as <c>gridsim-script run</c> does (relative paths from the file's folder) on a worker thread and show its
    /// output; the run record is kept in <see cref="LastRunRecordJson"/> and written beside the recordings as <c>NAME.run.json</c>.
    /// A refusal is shown as <c>file:line:column: phase error: message</c>.
    /// </summary>
    public async Task RunFileAsync(string path)
    {
        if (IsBusy) { StatusText = "A statement is already running."; Notify(); return; }
        if (!TryRead(path, out string full, out byte[] bytes)) return;
        IsBusy = true;
        StatusText = "Running " + Path.GetFileName(full) + "...";
        Append($"run {full}");
        Notify();
        try
        {
            string baseDir = Path.GetDirectoryName(full)!;
            Func<Statement, Session, string?>? gate = Gate;
            GridSim.Core.Licensing.LicenceGate licence = LicenceForRun();
            using var sw = new StringWriter { NewLine = "\n" };
            RunResult r = await Task.Run(() => Runner.Run(bytes, new RunOptions(baseDir, sw, gate, Licence: licence))).ConfigureAwait(true);
            if (sw.ToString().Length > 0) Append(sw.ToString().TrimEnd('\n'));
            LastRunRecordJson = r.RecordJson;
            Directory.CreateDirectory(RecordDirectory);
            string recPath = Path.Combine(RecordDirectory, Path.GetFileNameWithoutExtension(full) + ".run.json");
            await File.WriteAllBytesAsync(recPath, r.RecordBytes).ConfigureAwait(true);
            LastError = r.Error;
            if (r.Error is { } e)
            {
                Append(Describe(full, e));
                StatusText = $"Refused at line {e.Pos.Line}, column {e.Pos.Column}; run record {recPath}.";
            }
            else StatusText = $"Ran {Path.GetFileName(full)}; run record {recPath}.";
        }
#pragma warning disable CA1031 // Not swallowed: shown in the output.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Append($"error: {ex.GetType().Name}: {ex.Message}");
            StatusText = "The run failed.";
        }
        finally { IsBusy = false; Notify(); }
    }

    /// <summary>Check-only mode (<c>gridsim-script check</c>): parse the file and its procedures and run nothing.</summary>
    public async Task CheckFileAsync(string path)
    {
        if (IsBusy) { StatusText = "A statement is already running."; Notify(); return; }
        if (!TryRead(path, out string full, out byte[] bytes)) return;
        IsBusy = true;
        StatusText = "Checking " + Path.GetFileName(full) + "...";
        Notify();
        try
        {
            ScriptException? e = await Task.Run(() => Runner.Check(Runner.Decode(bytes))).ConfigureAwait(true);
            LastError = e;
            if (e is null)
            {
                Append($"{Path.GetFileName(full)}: grammar {Grammar.VersionText}, syntax and procedures ok");
                StatusText = "Check passed; nothing was run.";
            }
            else
            {
                Append(Describe(full, e));
                StatusText = $"Check failed at line {e.Pos.Line}, column {e.Pos.Column}.";
            }
        }
#pragma warning disable CA1031 // Not swallowed: shown in the output.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Append($"error: {ex.GetType().Name}: {ex.Message}");
            StatusText = "The check failed.";
        }
        finally { IsBusy = false; Notify(); }
    }

    private bool TryRead(string path, out string full, out byte[] bytes)
    {
        full = "";
        bytes = [];
        if (string.IsNullOrWhiteSpace(path)) { StatusText = "Type the path of a .gss script first."; Notify(); return false; }
        try
        {
            full = Path.GetFullPath(path.Trim());
            bytes = File.ReadAllBytes(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Append("error: " + ex.Message);
            StatusText = "The script could not be read.";
            Notify();
            return false;
        }
    }

    private static string Describe(string file, ScriptException e) =>
        $"{Path.GetFileName(file)}:{e.Pos.Line}:{e.Pos.Column}: {e.Phase.ToString().ToLowerInvariant()} error: {e.Message}";

    // ---------------------------------------------------------------- the recorded session

    private InteractiveConsole EnsureConsole()
    {
        if (_console is not null) return _console;
        Directory.CreateDirectory(RecordDirectory);
        string stem = ConsoleRecording.StemAt(DateTime.UtcNow);
        _gssPath = Path.Combine(RecordDirectory, stem + Grammar.Extension);
        int n = 1;
        while (File.Exists(_gssPath)) _gssPath = Path.Combine(RecordDirectory, $"{stem}-{++n}{Grammar.Extension}");
        _gssWriter = new StreamWriter(new FileStream(_gssPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        SessionRunRecordPath = null;
        _console = new InteractiveConsole(BaseDirectory, text => _gssWriter.WriteLine(text));
        _console.Session.Gate = Gate;
        _console.Session.Licence = LicenceForRun();
        Append("recording to " + _gssPath);
        return _console;
    }

    /// <summary>
    /// End the session: an unfinished block is kept as comments, the <c>.gss</c> is closed and <c>NAME.run.json</c> is written beside it,
    /// as <c>gridsim-script</c> does on <c>exit</c>. A later statement starts a new session. Does nothing when no statement was typed.
    /// </summary>
    public ConsoleRecording? EndSession()
    {
        if (_console is null || _gssWriter is null || _gssPath is null)
        {
            StatusText = "No session to end.";
            Notify();
            return null;
        }
        ConsoleRecording done = _console.Finish();
        _gssWriter.Flush();
        _gssWriter.Dispose();
        string runPath = Path.ChangeExtension(_gssPath, ".run.json");
        File.WriteAllBytes(runPath, done.RunRecordBytes);
        Append($"session recorded: {_gssPath}\nrun record: {runPath}");
        SessionRunRecordPath = runPath;
        StatusText = "Session recorded.";
        _console = null;
        _gssWriter = null;
        _gssPath = null;
        Notify();
        return done;
    }

    /// <summary>Close the window's session without a message in the pane (the window closing); same files as <see cref="EndSession"/>.</summary>
    public void Close()
    {
        if (_console is not null && !IsBusy) EndSession();
    }

    /// <summary>Close the session if one is open, and release the recording file.</summary>
    public void Dispose()
    {
        Close();
        _gssWriter?.Dispose();
        _gssWriter = null;
    }

    /// <summary>Clear the output pane (the recording is not touched).</summary>
    public void ClearOutput()
    {
        _output.Clear();
        _outputLines = 0;
        OnPropertyChanged(nameof(OutputText));
        Notify();
    }

    private void Append(string text)
    {
        foreach (string l in text.Split('\n'))
        {
            _output.Append(l).Append('\n');
            _outputLines++;
        }
        while (_outputLines > MaxOutputLines)
        {
            int cut = _output.ToString().IndexOf('\n', StringComparison.Ordinal);
            _output.Remove(0, cut + 1);
            _outputLines--;
        }
        OnPropertyChanged(nameof(OutputText));
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Prompt));
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
}
