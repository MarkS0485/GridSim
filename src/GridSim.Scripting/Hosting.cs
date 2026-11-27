using System.Text;

namespace GridSim.Scripting;

/// <summary>
/// Hosting hook: the work a host lets a session do. All members are optional; the console sets none, so its behaviour is
/// unchanged. A statement over a limit is refused with an execution error naming its line and column and the session is
/// left as it was.
/// </summary>
/// <param name="MaxTicksPerStep">Most engine ticks one <c>STEP SIM</c> may take.</param>
/// <param name="MaxTotalTicks">Most engine ticks the whole session may step.</param>
/// <param name="Cancel">Checked before every statement, every block and every loop iteration; a cancelled token stops the run with an error.</param>
public sealed record SessionLimits(long? MaxTicksPerStep = null, long? MaxTotalTicks = null, CancellationToken Cancel = default)
{
    /// <summary>Most units of work the whole session may do: each simple statement run, each block or procedure call entered and each
    /// element a FOR EACH visits counts one. Over it the statement is refused with an execution error. Null: no limit.</summary>
    public long? MaxStatements { get; init; }

    /// <summary>Most characters of output the whole session may produce. Over it the statement is refused with an execution error as soon
    /// as the text passes the limit, so a run never gathers more. Null: no limit.</summary>
    public long? MaxOutputChars { get; init; }
}

/// <summary>
/// What a statement does, for a host that gates it by role (RC4: Operator for state-changing verbs when hosted).
/// <para><b>Read-only</b> (any caller): <c>GRAMMAR</c>, <c>LOAD CASE</c> (reads a case into this session, changes no shared state),
/// <c>REPORT</c>, <c>PRINT</c>, <c>LET</c>, assignment, and the blocks (<c>IF</c>, <c>FOR EACH</c>, <c>PROC</c>, <c>CALL</c>) whose
/// bodies are read-only. <b>State-changing</b>: <c>SOLVE</c>, <c>TRIP</c>, <c>CLOSE</c>, <c>SET</c>, <c>FAULT</c>, <c>CLEAR FAULT</c>,
/// <c>SELECT ENGINE</c>, <c>STEP SIM</c>, <c>AT</c> (it schedules one), <c>EXPORT RESULT</c> (it writes a file), and any block that contains one.</para>
/// </summary>
public static class StatementEffects
{
    /// <summary>True for a statement that changes the model, the solved state, the engine or a file. Looks through blocks and
    /// through the body of a procedure a <c>CALL</c> names (looked up in <paramref name="session"/>).</summary>
    public static bool ChangesState(Statement statement, Session? session = null) =>
        Flatten(statement, session).Any(IsStateChangingVerb);

    /// <summary>The statement and every statement nested in it: block bodies, the inner statement of <c>AT</c> and the body of each
    /// procedure called (once). Used by hosts to apply a policy to everything a statement could run.</summary>
    public static IEnumerable<Statement> Flatten(Statement statement, Session? session = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Statement>();
        stack.Push(statement);
        while (stack.Count > 0)
        {
            Statement s = stack.Pop();
            yield return s;
            switch (s)
            {
                case IfStatement i:
                    foreach (Statement t in i.Then) stack.Push(t);
                    if (i.Else is { } e) foreach (Statement t in e) stack.Push(t);
                    break;
                case ForEachStatement f: foreach (Statement t in f.Body) stack.Push(t); break;
                case ProcStatement p: foreach (Statement t in p.Body) stack.Push(t); break;
                case AtStatement a: stack.Push(a.Inner); break;
                case CallStatement c when session is not null && session.Procs.TryGetValue(c.Name.Value, out ProcStatement? proc) && seen.Add(c.Name.Value):
                    foreach (Statement t in proc.Body) stack.Push(t);
                    break;
            }
        }
    }

    private static bool IsStateChangingVerb(Statement s) =>
        s is SolveStatement or TripStatement or CloseStatement or SetStatement or FaultStatement or ClearFaultStatement
            or SelectEngineStatement or StepSimStatement or AtStatement or ExportResultStatement or StudyStatement;
}

/// <summary>What <see cref="InteractiveConsole.Submit"/> did with one typed line.</summary>
public enum ConsoleReplyKind
{
    /// <summary>Blank or a comment: recorded, nothing ran.</summary>
    Blank,
    /// <summary>The line opens a block that is not closed yet; the console keeps reading lines.</summary>
    Continued,
    /// <summary>The statement ran; <see cref="ConsoleReply.Output"/> is what it printed.</summary>
    Ran,
    /// <summary>The statement was refused; <see cref="ConsoleReply.Error"/> names line and column and the session is unchanged.</summary>
    Refused,
}

public sealed record ConsoleReply(ConsoleReplyKind Kind, string Output, ScriptException? Error);

/// <summary>The finished recording of an interactive session: the <c>.gss</c> text and the run record, byte for byte what gridsim-script writes.</summary>
public sealed record ConsoleRecording(string GssText, byte[] GssBytes, string RunRecordJson, byte[] RunRecordBytes)
{
    /// <summary>The file name stem the console gives a session started at <paramref name="utc"/> (<c>session-yyyyMMdd-HHmmss</c>).</summary>
    public static string StemAt(DateTime utc) => "session-" + utc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The interactive console as a library: one typed line at a time, block lines gathered until END, every statement run by
/// <see cref="Session.Execute"/>, every line recorded as a <c>.gss</c> file with refused lines kept as comments. This is the
/// loop <c>gridsim-script</c> runs (it calls this class), so a desktop or web panel that hosts it records a session exactly
/// as the console does. Not thread-safe: one caller at a time.
/// </summary>
public sealed class InteractiveConsole
{
    private readonly List<string> _recorded = [];
    private readonly List<string> _pending = [];
    private readonly Action<string>? _sink;
    private int _line;
    private long _pendingChars;

    /// <param name="baseDirectory">Relative LOAD and EXPORT paths are taken from here.</param>
    /// <param name="recordSink">Called with each recorded line as it is recorded (the console writes it to its file at once); null for none.</param>
    public InteractiveConsole(string baseDirectory, Action<string>? recordSink = null)
    {
        Session = new Session(baseDirectory);
        _sink = recordSink;
        Record("# gridsim-script console session, recorded; refused lines are kept as comments");
        Record("GRAMMAR " + Grammar.VersionText);
        Session.Execute(Parser.ParseLineText("GRAMMAR " + Grammar.VersionText, 2)!);
    }

    /// <summary>The session the lines run in (set <see cref="Session.Gate"/> and <see cref="Session.Limits"/> here for a host).</summary>
    public Session Session { get; }

    /// <summary>True while the lines read so far open a block that is not closed.</summary>
    public bool InBlock => _pending.Count > 0;

    /// <summary>Hosting hook (null means no limit, the console's setting): the most characters an open block may gather before its END.
    /// A line that takes the block past it refuses the whole block (recorded as comments) with a syntax error at the block's first line.</summary>
    public long? MaxPendingChars { get; set; }

    /// <summary>The lines recorded so far.</summary>
    public IReadOnlyList<string> RecordedLines => _recorded;

    private void Record(string text)
    {
        _recorded.Add(text);
        _line++;
        _sink?.Invoke(text);
    }

    /// <summary>Give the console one typed line (no end-of-line characters).</summary>
    public ConsoleReply Submit(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        int firstLine = _line + 1;
        _pending.Add(input);
        _pendingChars += input.Length + 1;
        if (MaxPendingChars is { } cap && _pendingChars > cap)
        {
            var over = new ScriptException(ErrorPhase.Syntax, new SourcePos(firstLine, 1), $"the open block is over this host's limit of {cap} characters; it was not run");
            Record($"# REFUSED ({over.Pos.Line}:{over.Pos.Column}) {over.Message}");
            foreach (string l in _pending) Record("# " + l);
            ClearPending();
            return new ConsoleReply(ConsoleReplyKind.Refused, "", over);
        }
        try
        {
            Statement? s = Parser.ParseStatementText(string.Join("\n", _pending), firstLine);
            if (s is null)
            {
                FlushPending();
                return new ConsoleReply(ConsoleReplyKind.Blank, "", null);
            }
            string output = Session.Execute(s);
            FlushPending();
            return new ConsoleReply(ConsoleReplyKind.Ran, output, null);
        }
        catch (ScriptException e) when (e.Unfinished)
        {
            return new ConsoleReply(ConsoleReplyKind.Continued, "", null);
        }
        catch (ScriptException e)
        {
            Record($"# REFUSED ({e.Pos.Line}:{e.Pos.Column}) {e.Message}");
            foreach (string l in _pending) Record("# " + l);
            ClearPending();
            return new ConsoleReply(ConsoleReplyKind.Refused, "", e);
        }
    }

    private void FlushPending()
    {
        foreach (string l in _pending) Record(l);
        ClearPending();
    }

    private void ClearPending()
    {
        _pending.Clear();
        _pendingChars = 0;
    }

    /// <summary>Close the recording: an unfinished block is kept as comments, and the run record is written from the session.</summary>
    public ConsoleRecording Finish()
    {
        if (_pending.Count > 0)
        {
            Record("# UNFINISHED BLOCK, not run:");
            foreach (string l in _pending) Record("# " + l);
            ClearPending();
        }
        var enc = new UTF8Encoding(false);
        string gss = string.Concat(_recorded.Select(l => l + "\n"));
        byte[] gssBytes = enc.GetBytes(gss);
        string json = Runner.WriteRecord(Session, Runner.Sha256(gssBytes), null);
        return new ConsoleRecording(gss, gssBytes, json, enc.GetBytes(json));
    }
}
