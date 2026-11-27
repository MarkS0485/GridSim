using System.Text;
using GridSim.Core.Licensing;
using GridSim.Core.Security;
using GridSim.Scripting;
using GridSim.Web.Model;

namespace GridSim.Web.Site;

/// <summary>How a web script run or typed line ended.</summary>
public enum ScriptRunStatus
{
    /// <summary>Every statement ran.</summary>
    Completed,
    /// <summary>The script or line is not grammar, or a statement was refused by the model or the engine (line and column in the error).</summary>
    Error,
    /// <summary>The caller's role is below the band a statement needs, the web console does not offer the verb, or the licence does not
    /// cover the model a <c>LOAD CASE</c> names. A role or verb refusal runs nothing; a licence refusal stops the run at that statement.</summary>
    Refused,
    /// <summary>The script is larger than the host's limit. Nothing ran.</summary>
    TooLarge,
    /// <summary>Every solve slot on this host is in use. Nothing ran.</summary>
    Busy,
    /// <summary>The run did not finish inside the host's solve timeout; the statement that was running when it fired is not recorded.</summary>
    TimedOut,
}

/// <summary>The answer to one web script run, check or typed line.</summary>
/// <param name="Message">One sentence a person reads: what happened, and why when it was refused.</param>
/// <param name="Output">What the statements printed (capped at <see cref="WebScriptConsole.MaxOutputChars"/>).</param>
/// <param name="Error">The script error with its line and column, when a statement was refused or is not grammar.</param>
/// <param name="RunRecordJson">The run record (a completed or errored run; null when nothing ran).</param>
public sealed record WebScriptResult(ScriptRunStatus Status, string Message, string Output, ScriptException? Error, string? RunRecordJson)
{
    public bool Ok => Status == ScriptRunStatus.Completed;
}

/// <summary>
/// RC4: the web host of GridSim.Scripting (the console page, <c>/console</c>). It runs the same <see cref="Runner"/> and
/// <see cref="InteractiveConsole"/> the <c>gridsim-script</c> executable runs; what it adds is the host's policy, all of it before
/// a statement executes, through <see cref="Session.Gate"/> and <see cref="Session.Limits"/>.
///
/// <para><b>Roles.</b> The bands are <see cref="OperationRole"/>'s, through <see cref="AccessControl"/> (so a refusal is one
/// <c>AccessDenied</c> audit entry, as on the other surfaces). <b>Read-only</b> verbs are open to every caller (Viewer):
/// <c>GRAMMAR</c>, <c>LOAD CASE</c> of a public catalogue model, <c>REPORT</c>, <c>PRINT</c>, <c>LET</c> and the check-only mode.
/// <c>SOLVE POWERFLOW</c> needs <see cref="SolveRole"/> (Analyst), as <c>POST /api/solve</c> does. Every other <b>state-changing</b> verb (<see cref="StatementEffects"/>: <c>TRIP</c>, <c>CLOSE</c>, <c>SET</c>, <c>FAULT</c>,
/// <c>CLEAR FAULT</c>, <c>SELECT ENGINE</c>, <c>STEP SIM</c>, <c>AT</c>, and any block or procedure that holds one) needs
/// <see cref="ChangeRole"/> (Operator). A model under <c>results/</c> or <c>gda-out/</c> needs <see cref="CaseCatalog.PrivateListingRole"/>
/// to load, as on the dashboard and the workbench.</para>
///
/// <para><b>What the web console does not offer.</b> <c>LOAD CASE</c> loads only a model the catalogue lists (by the path it lists), never an
/// arbitrary file or folder of the server, and the licence gate is applied to it as on every other page; <c>EXPORT RESULT</c> writes a file on
/// the server and is refused here (the run record and the recorded session are offered as downloads instead).</para>
///
/// <para><b>Limits.</b> A script is at most <see cref="MaxScriptBytes"/> bytes; one <c>STEP SIM</c> at most <see cref="MaxTicksPerStep"/>
/// engine ticks and a session <see cref="MaxTotalTicks"/>; a session runs at most <see cref="MaxStatements"/> statements, blocks and loop
/// iterations and produces at most <see cref="MaxSessionOutputChars"/> characters of output (checked as it is produced); the page shows
/// the first <see cref="MaxOutputChars"/>; an open block of the interactive console gathers at most <see cref="MaxScriptBytes"/>
/// characters; and a run takes a solve slot of <see cref="WebLimits"/> and stops at its solve timeout (checked before every statement,
/// block and loop iteration; a statement already running finishes, as a solve does elsewhere on this host, and holds the slot until it
/// does). The grammar always terminates; these bound how long and how much.</para>
///
/// <para><b>Files and paths.</b> The <c>DYNAMICS</c> file of <c>SELECT ENGINE</c> must lie in the folder of a model the catalogue lists
/// to the caller; a <c>SCHEMAS</c> folder is not offered (the shipped schemas are found on their own). The page lists models by paths
/// relative to the console's base folder, and error messages name that folder as "the base folder", never its absolute path.</para>
/// </summary>
public sealed class WebScriptConsole
{
    /// <summary>Operation name of a state-changing verb, as it appears in the refusal and the audit entry.</summary>
    public const string ChangeOperation = "console.change";

    /// <summary>The band a state-changing verb needs.</summary>
    public const OperationRole ChangeRole = OperationRole.Operator;

    /// <summary>Operation name of <c>SOLVE POWERFLOW</c>, as it appears in the refusal and the audit entry.</summary>
    public const string SolveOperation = "console.solve";

    /// <summary>The band <c>SOLVE POWERFLOW</c> needs: the same as <c>POST /api/solve</c> (<see cref="AccessControl.SolveRole"/>).</summary>
    public const OperationRole SolveRole = AccessControl.SolveRole;

    /// <summary>Operation name of loading a model under <c>results/</c> or <c>gda-out/</c> on the console.</summary>
    public const string PrivateOperation = "console.private";

    /// <summary>Every refusal the host policy gives starts with this, so a caller can tell it from a grammar or engine error.</summary>
    public const string RefusalPrefix = "refused by the web console: ";

    public const int MaxScriptBytes = 64 * 1024;
    public const long MaxTicksPerStep = 100_000;
    public const long MaxTotalTicks = 400_000;
    public const int MaxOutputChars = 256 * 1024;

    /// <summary>Most statements, blocks and loop iterations one run or interactive session may execute.</summary>
    public const long MaxStatements = 200_000;

    /// <summary>Most characters of output one run or interactive session may produce (the page shows the first <see cref="MaxOutputChars"/>).</summary>
    public const long MaxSessionOutputChars = 4 * 1024 * 1024;

    private readonly CaseCatalog _catalog;
    private readonly AccessControl _access;
    private readonly WebLimits _limits;
    private readonly string _baseDirectory;

    public WebScriptConsole(CaseCatalog catalog, AccessControl access, WebLimits limits, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(limits);
        _catalog = catalog;
        _access = access;
        _limits = limits;
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    /// <summary>The limits as one sentence, shown on the page.</summary>
    public string LimitsText =>
        $"A script may be up to {MaxScriptBytes / 1024} KB; one STEP SIM at most {MaxTicksPerStep:N0} engine ticks and a session {MaxTotalTicks:N0}; "
        + $"a session runs at most {MaxStatements:N0} statements, blocks and loop iterations; "
        + $"output is cut at {MaxOutputChars / 1024} KB; a run stops at the host's solve timeout ({_limits.SolveTimeout.TotalSeconds:0} s) and needs a free solve slot.";

    /// <summary>The models <c>LOAD CASE</c> may name, with the path to write: catalogue entries that are files or folders the caller may see.</summary>
    public IReadOnlyList<(CaseEntry Entry, string Statement)> LoadableCases(OperationRole role) =>
        _catalog.EntriesFor(role)
            .Where(e => !e.IsReplay && e.Path.Length > 0)
            .Select(e => (e, "LOAD CASE \"" + Shown(e.Path).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""))
            .ToList();

    /// <summary>A catalogue path as the page shows it: relative to the console's base folder (so the page never shows where the server is installed).</summary>
    private string Shown(string path)
    {
        string full = Path.GetFullPath(path, _baseDirectory);
        string rel = Path.GetRelativePath(_baseDirectory, full);
        return Path.IsPathRooted(rel) ? full : rel;
    }

    // ---------------------------------------------------------------- policy

    /// <summary>
    /// The host's refusal for <paramref name="statement"/> (and everything it could run), or null to allow it.
    /// <paramref name="checkLicence"/> false skips the licence load of a <c>LOAD CASE</c> (the pre-scan; the run itself checks).
    /// A role refusal is audited through <see cref="AccessControl.Require"/>.
    /// </summary>
    public string? Refusal(Statement statement, Session? session, IdentityContext who, bool checkLicence = true)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(who);
        bool needsChange = false, needsSolve = false;
        foreach (Statement s in StatementEffects.Flatten(statement, session))
        {
            switch (s)
            {
                case ExportResultStatement:
                    return RefusalPrefix + "EXPORT RESULT writes a file on the server and is not offered on the web console; download the run record instead.";
                case LoadCaseStatement l:
                    if (LoadRefusal(l, who, checkLicence) is { } load) return load;
                    break;
                case SelectEngineStatement se:
                    if (EngineFileRefusal(se, who) is { } files) return files;
                    break;
            }
            if (s is SolveStatement) needsSolve = true;
            else if (StatementEffects.ChangesState(s)) needsChange = true;
        }
        if (needsSolve && !needsChange)
        {
            try { _access.Require(who, SolveRole, SolveOperation); }
            catch (AccessDeniedException ex)
            {
                return $"{RefusalPrefix}SOLVE POWERFLOW is an on-demand analysis and needs the {SolveRole} role, as POST /api/solve does; you hold {ex.Actual}.";
            }
        }
        if (needsChange)
        {
            try { _access.Require(who, ChangeRole, ChangeOperation); }
            catch (AccessDeniedException ex)
            {
                return $"{RefusalPrefix}this statement changes state and needs the {ChangeRole} role; you hold {ex.Actual}. "
                    + "Read-only verbs (LOAD CASE of a public model, REPORT, PRINT, LET) and the check are open to every caller.";
            }
        }
        return null;
    }

    private string? LoadRefusal(LoadCaseStatement l, IdentityContext who, bool checkLicence)
    {
        string full;
        try { full = Path.GetFullPath(l.Path.Value, _baseDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return RefusalPrefix + "LOAD CASE: not a usable path.";
        }
        CaseEntry? entry = _catalog.Entries.FirstOrDefault(e => !e.IsReplay && e.Path.Length > 0 && SamePath(Path.GetFullPath(e.Path, _baseDirectory), full));
        if (entry is null)
            return RefusalPrefix + "LOAD CASE loads only a model the catalogue lists, by the path the page shows; it does not read other files or folders of the server.";
        if (_catalog.IsPrivate(entry.Id))
        {
            try { _access.Require(who, CaseCatalog.PrivateListingRole, PrivateOperation); }
            catch (AccessDeniedException ex)
            {
                return $"{RefusalPrefix}model '{entry.Id}' needs the {CaseCatalog.PrivateListingRole} role; you hold {ex.Actual}.";
            }
        }
        if (checkLicence)
        {
            try { _ = _catalog.LoadModel(entry.Id); }   // the licence gate and the loader, exactly as the dashboard and the workbench apply them
            catch (Exception ex) when (AccessControl.IsPageLoadRefusal(ex))
            {
                return $"{RefusalPrefix}LOAD CASE of '{entry.Id}': {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>The refusal of a <c>SELECT ENGINE</c> file setting: <c>SCHEMAS</c> is not offered, and a <c>DYNAMICS</c> file must lie in the
    /// folder of a model the catalogue lists to the caller.</summary>
    private string? EngineFileRefusal(SelectEngineStatement se, IdentityContext who)
    {
        foreach (SettingClause c in se.Settings)
        {
            if (c.Key.Value == "SCHEMAS")
                return RefusalPrefix + "SELECT ENGINE ... SCHEMAS names a folder of the server and is not offered on the web console; the shipped schemas are used.";
            if (c.Key.Value != "DYNAMICS" || c.Name is not { } name) continue;
            string written = name.Value;
            string full;
            try { full = Path.GetFullPath(written, _baseDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return RefusalPrefix + "DYNAMICS: not a usable path.";
            }
            bool listed = !GridSim.Scripting.PathConfinement.IsNetworkOrDevice(written)
                && _catalog.EntriesFor(who.Role).Where(e => !e.IsReplay && e.Path.Length > 0).Any(e => InFolderOf(e, full));
            if (!listed)
                return RefusalPrefix + "DYNAMICS reads only a file in the folder of a model the catalogue lists; it does not read other files of the server.";
        }
        return null;
    }

    private bool InFolderOf(CaseEntry e, string full)
    {
        string entry = Path.GetFullPath(e.Path, _baseDirectory);
        string folder = Directory.Exists(entry) ? entry : Path.GetDirectoryName(entry) ?? entry;
        return GridSim.Scripting.PathConfinement.Refusal(full, full, folder) is null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private Func<Statement, Session, string?> GateFor(IdentityContext who) => (s, session) => Refusal(s, session, who);

    private static SessionLimits LimitsFor(CancellationToken cancel) => new(MaxTicksPerStep, MaxTotalTicks, cancel) { MaxStatements = MaxStatements, MaxOutputChars = MaxSessionOutputChars };

    // ---------------------------------------------------------------- check and run

    /// <summary>Check-only mode: parse the text and its procedures and run nothing. Open to every caller (it executes nothing).</summary>
    public WebScriptResult Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxScriptBytes) return TooLarge();
        ScriptException? e = Runner.Check(text);
        return e is null
            ? new WebScriptResult(ScriptRunStatus.Completed, $"grammar {Grammar.VersionText}, syntax and procedures ok; nothing was run.", "", null, null)
            : new WebScriptResult(ScriptRunStatus.Error, e.Display, "", e, null);
    }

    /// <summary>
    /// Run a whole script as the caller. The script is parsed and every statement is checked against the caller's role and the path
    /// policy first, so a refused script runs nothing (not even its read-only lines); the statements then run under the limits.
    /// </summary>
    public async Task<WebScriptResult> RunAsync(string text, IdentityContext who, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(who);
        if (Encoding.UTF8.GetByteCount(text) > MaxScriptBytes) return TooLarge();
        ScriptException? bad = Runner.Check(text);
        if (bad is not null) return new WebScriptResult(ScriptRunStatus.Error, bad.Display, "", bad, null);
        foreach (Statement s in Parser.ParseScript(text))
            if (Refusal(s, null, who, checkLicence: false) is { } why)
                return new WebScriptResult(ScriptRunStatus.Refused, $"line {s.Pos.Line}, column {s.Pos.Column}: {why} Nothing was run.", "", null, null);

        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_limits.SolveTimeout);
        using var writer = new CappedWriter(MaxOutputChars);
        SolveRun<RunResult> run = await _limits.RunSolveAsync(
            () => Runner.Run(bytes, new RunOptions(_baseDirectory, writer, GateFor(who), LimitsFor(timeout.Token), HideBaseDirectory: true)), cancellationToken).ConfigureAwait(false);
        switch (run.Status)
        {
            case SolveRunStatus.Busy:
                return new WebScriptResult(ScriptRunStatus.Busy, "Every solve slot on this host is in use; try again shortly. Nothing was run.", "", null, null);
            case SolveRunStatus.TimedOut:
                return new WebScriptResult(ScriptRunStatus.TimedOut, $"The script did not finish inside the host's {_limits.SolveTimeout.TotalSeconds:0} s solve timeout.", writer.ToString(), null, null);
        }
        RunResult r = run.Value!;
        // The pre-scan skips the licence load, so a licence refusal comes from the gate during the run: say it as a refusal, not an error.
        return r.Error is { } e
            ? new WebScriptResult(IsHostRefusal(e) ? ScriptRunStatus.Refused : ScriptRunStatus.Error, e.Display, writer.ToString(), e, r.RecordJson)
            : new WebScriptResult(ScriptRunStatus.Completed, $"Ran every statement; run record sha256 {r.RecordSha256[..12]}.", writer.ToString(), null, r.RecordJson);
    }

    /// <summary>True when <paramref name="e"/> is the host policy's refusal (through <see cref="Session.Gate"/>), not a grammar or engine error.</summary>
    private static bool IsHostRefusal(ScriptException e) => e.Message.StartsWith(RefusalPrefix, StringComparison.Ordinal);

    private static WebScriptResult TooLarge() =>
        new(ScriptRunStatus.TooLarge, $"The script is over this host's limit of {MaxScriptBytes / 1024} KB. Nothing was run.", "", null, null);

    /// <summary>A recorded interactive session for one caller (one browser circuit). Not thread-safe: one line at a time.</summary>
    public WebConsoleSession NewSession(IdentityContext who) => new(this, who);

    /// <summary>A text writer that keeps the first <c>cap</c> characters and says it stopped.</summary>
    private sealed class CappedWriter(int cap) : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private bool _cut;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { if (_sb.Length < cap) _sb.Append(value); else _cut = true; }
        public override void Write(string? value)
        {
            if (value is null) return;
            int room = cap - _sb.Length;
            if (value.Length <= room) _sb.Append(value);
            else { if (room > 0) _sb.Append(value, 0, room); _cut = true; }
        }
        public override string ToString() => _cut ? _sb + $"\n[output cut at {cap / 1024} KB]\n" : _sb.ToString();
    }

    /// <summary>The interactive console of one web caller: the same <see cref="InteractiveConsole"/> as <c>gridsim-script</c>, with the host's gate and limits.</summary>
    public sealed class WebConsoleSession
    {
        private readonly WebScriptConsole _host;
        private readonly InteractiveConsole _console;

        internal WebConsoleSession(WebScriptConsole host, IdentityContext who)
        {
            _host = host;
            Who = who;
            _console = new InteractiveConsole(host._baseDirectory) { MaxPendingChars = MaxScriptBytes };
            _console.Session.Gate = host.GateFor(who);
            _console.Session.HideBaseDirectory = true;
        }

        public IdentityContext Who { get; }

        /// <summary>True while the lines so far open a block that is not closed.</summary>
        public bool InBlock => _console.InBlock;

        /// <summary>One typed line, under a solve slot and the solve timeout. A role refusal comes back as <see cref="ScriptRunStatus.Refused"/> with the line and column.</summary>
        public async Task<WebScriptResult> SubmitAsync(string line, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.Length > MaxScriptBytes) return TooLarge();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_host._limits.SolveTimeout);
            _console.Session.Limits = LimitsFor(timeout.Token);
            SolveRun<ConsoleReply> run = await _host._limits.RunSolveAsync(() => _console.Submit(line), cancellationToken).ConfigureAwait(false);
            if (run.Status == SolveRunStatus.Busy)
                return new WebScriptResult(ScriptRunStatus.Busy, "Every solve slot on this host is in use; try again shortly. The line was not run.", "", null, null);
            if (run.Status == SolveRunStatus.TimedOut)
                return new WebScriptResult(ScriptRunStatus.TimedOut, "The statement did not finish inside the host's solve timeout.", "", null, null);
            ConsoleReply r = run.Value!;
            string output = r.Output.Length > MaxOutputChars ? r.Output[..MaxOutputChars] + $"\n[output cut at {MaxOutputChars / 1024} KB]\n" : r.Output;
            return r.Kind switch
            {
                ConsoleReplyKind.Refused => new WebScriptResult(
                    IsHostRefusal(r.Error!) ? ScriptRunStatus.Refused : ScriptRunStatus.Error,
                    r.Error!.Display, "", r.Error, null),
                ConsoleReplyKind.Continued => new WebScriptResult(ScriptRunStatus.Completed, "block open: type END to close it.", "", null, null),
                _ => new WebScriptResult(ScriptRunStatus.Completed, "ok", output, null, null),
            };
        }

        /// <summary>The recording so far, finished: the <c>.gss</c> text and the run record, as <c>gridsim-script</c> writes them.</summary>
        public ConsoleRecording Finish() => _console.Finish();
    }
}
