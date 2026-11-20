using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Scripting;

/// <summary>The last power-flow solve: the model it solved, the result, the trace the export needs, and the plant version it is good for.</summary>
public sealed record SolvedState(GridModel Model, PowerFlowResult Result, SolveTrace Trace, int PlantVersion);

/// <param name="Depth">0 for a statement on its own line of the script, one more for each block or procedure it sits inside.</param>
/// <param name="Resolved">The statement with every name and value written out as it ran (a loop variable replaced by the element, an
/// expression by its value); null when that is the statement as written.</param>
public sealed record StatementEntry(int Seq, int Line, int Column, long TimeUs, string Text, string? OutputSha256, int Depth = 0, string? Resolved = null);
public sealed record AppliedEvent(int Seq, long TimeUs, string Text, int ScheduledAtLine, int StepSeq);
public sealed record StepEntry(int Seq, int Line, long FromUs, long ToUs, int Ticks, string Engine, ImmutableArray<KeyValuePair<string, string>> Final, string SeriesSha256);
public sealed record OutputEntry(int Seq, int Line, string Kind, string Name, long Bytes, string Sha256);
public sealed record InputEntry(int Seq, int Line, string Kind, string Path, string Sha256);
public sealed record PendingEvent(long TimeUs, int Order, AtStatement Statement);
public sealed record DecisionEntry(int Seq, int Line, string Condition, string Detail, bool Result);
public sealed record LoopEntry(int Seq, int Line, string Noun, string Where, int Total, ImmutableArray<string> Matched);
public sealed record ProcEntry(int Seq, int Line, string Name, string Header, string BodySha256);
public sealed record EngineEntry(int Seq, int Line, string Kind, long StepUs, string Settings);

/// <summary>
/// One scripting session: the loaded case, the plant (the case with the script's edits), the last solved state, the chosen
/// engine, the script clock, the named values and procedures, and the event log. <see cref="Execute"/> runs one top-level
/// statement (a block is one statement): check it whole, then run it a simple statement at a time, each validated against the
/// state it runs in; if anything fails the session is exactly as it was before the statement.
/// </summary>
public sealed class Session
{
    private readonly string _baseDir;
    private int _seq;
    private int _pendingOrder;

    private readonly List<StatementEntry> _statements = [];
    private readonly List<AppliedEvent> _applied = [];
    private readonly List<StepEntry> _steps = [];
    private readonly List<OutputEntry> _outputs = [];
    private readonly List<InputEntry> _inputs = [];
    private readonly List<DecisionEntry> _decisions = [];
    private readonly List<LoopEntry> _loops = [];
    private readonly List<ProcEntry> _procEntries = [];
    private readonly List<EngineEntry> _engines = [];
    private readonly List<string> _canonical = [];
    private Dictionary<string, Value> _globals = new(StringComparer.Ordinal);
    private PowerFlowOptions _lastOptions = new();
    private string _lastSolverName = "";

    public Session(string baseDirectory)
    {
        _baseDir = Path.GetFullPath(baseDirectory);
    }

    /// <summary>
    /// Hosting hook (additive; null for the console, which changes nothing about a session). Asked before a top-level statement is
    /// validated: a non-null answer refuses the statement with that text (a validation error at the statement's line and column) and
    /// the session is left exactly as it was. A host uses it for its role and path policy; see <see cref="StatementEffects"/>.
    /// </summary>
    public Func<Statement, Session, string?>? Gate { get; set; }

    /// <summary>Hosting hook (additive; null means no limit): the work one statement or the whole session may do. See <see cref="SessionLimits"/>.</summary>
    public SessionLimits? Limits { get; set; }

    private long _totalTicks;

    public CaseInfo? Case { get; private set; }
    public Plant? Plant { get; private set; }
    public SolvedState? Solved { get; private set; }
    public EngineKind EngineKind { get; private set; } = EngineKind.Static;
    public LiveEngine? Live { get; private set; }
    public ReplayEngine? Replay { get; private set; }
    public long ClockUs { get; private set; }
    public List<PendingEvent> Pending { get; private set; } = [];
    public ImmutableDictionary<string, ProcStatement> Procs { get; private set; } = ImmutableDictionary<string, ProcStatement>.Empty.WithComparers(StringComparer.Ordinal);

    public IReadOnlyList<StatementEntry> Statements => _statements;
    public IReadOnlyList<AppliedEvent> AppliedEvents => _applied;
    public IReadOnlyList<StepEntry> Steps => _steps;
    public IReadOnlyList<OutputEntry> Outputs => _outputs;
    public IReadOnlyList<InputEntry> Inputs => _inputs;
    public IReadOnlyList<DecisionEntry> Decisions => _decisions;
    public IReadOnlyList<LoopEntry> Loops => _loops;
    public IReadOnlyList<ProcEntry> ProcEntries => _procEntries;
    public IReadOnlyList<EngineEntry> Engines => _engines;
    public PowerFlowOptions SolverOptions => _lastOptions;
    public string SolverName => _lastSolverName;

    /// <summary>The named values of the session (those made by LET at the top level), by name.</summary>
    public IReadOnlyDictionary<string, Value> Globals => _globals;

    /// <summary>The integration step of the selected engine in microseconds, or 0 under STATIC.</summary>
    public long StepUs => Live?.StepUs ?? Replay?.StepUs ?? 0;

    /// <summary>SHA-256 of the canonical form of the statements that ran, one per line: comments, spacing and keyword case do not move it.</summary>
    public string CanonicalScriptSha256 => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", _canonical))));

    /// <summary>The types of the session's named values, for checking.</summary>
    public TypeScope GlobalTypes()
    {
        var t = new TypeScope(null);
        foreach (KeyValuePair<string, Value> kv in _globals) t.Define(kv.Key, kv.Value.Type);
        return t;
    }

    private sealed record Snapshot(
        CaseInfo? Case, Plant? Plant, SolvedState? Solved, EngineKind Engine, LiveEngine? Live, int LiveCount, ReplayEngine? Replay, long Clock,
        List<PendingEvent> Pending, int Statements, int Applied, int Steps, int Outputs, int Inputs, int Decisions, int Loops, int ProcEntries,
        int Engines, int Canonical, int Seq, int Order, PowerFlowOptions Options, string SolverName,
        Dictionary<string, Value> Globals, ImmutableDictionary<string, ProcStatement> Procs);

    private Snapshot Take() => new(Case, Plant, Solved, EngineKind, Live, Live?.JournalCount ?? 0, Replay, ClockUs,
        [.. Pending], _statements.Count, _applied.Count, _steps.Count, _outputs.Count, _inputs.Count, _decisions.Count, _loops.Count,
        _procEntries.Count, _engines.Count, _canonical.Count, _seq, _pendingOrder, _lastOptions, _lastSolverName,
        new Dictionary<string, Value>(_globals, StringComparer.Ordinal), Procs);

    private void Restore(Snapshot s)
    {
        Case = s.Case; Plant = s.Plant; Solved = s.Solved; EngineKind = s.Engine; ClockUs = s.Clock; Pending = s.Pending; Replay = s.Replay;
        Live = s.Live is not null && s.Live.JournalCount != s.LiveCount ? s.Live.RebuildTo(s.LiveCount) : s.Live;
        _statements.RemoveRange(s.Statements, _statements.Count - s.Statements);
        _applied.RemoveRange(s.Applied, _applied.Count - s.Applied);
        _steps.RemoveRange(s.Steps, _steps.Count - s.Steps);
        _outputs.RemoveRange(s.Outputs, _outputs.Count - s.Outputs);
        _inputs.RemoveRange(s.Inputs, _inputs.Count - s.Inputs);
        _decisions.RemoveRange(s.Decisions, _decisions.Count - s.Decisions);
        _loops.RemoveRange(s.Loops, _loops.Count - s.Loops);
        _procEntries.RemoveRange(s.ProcEntries, _procEntries.Count - s.ProcEntries);
        _engines.RemoveRange(s.Engines, _engines.Count - s.Engines);
        _canonical.RemoveRange(s.Canonical, _canonical.Count - s.Canonical);
        _seq = s.Seq; _pendingOrder = s.Order; _lastOptions = s.Options; _lastSolverName = s.SolverName;
        _globals = s.Globals;
        Procs = s.Procs;
    }

    /// <summary>
    /// Check and execute one top-level statement; returns the text it printed (empty for most). On any failure the session is
    /// put back exactly as it was (a block that fails half-way undoes everything it did) and a <see cref="ScriptException"/>
    /// naming the failing line and column is thrown.
    /// </summary>
    public string Execute(Statement s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Snapshot snap = Take();
        var outText = new StringBuilder();
        try
        {
            if (Gate?.Invoke(s, this) is { } refusal)
                throw new ScriptException(ErrorPhase.Validation, s.Pos, refusal);
            Validator.Check(s, this);
            ExecStatement(s, new Scope(null, _globals), outText, depth: 0);
            _canonical.Add(s.Canonical());
            return outText.ToString();
        }
        catch (ScriptException ex)
        {
            Restore(snap);
            throw ex.Pos.Line == 0 ? new ScriptException(ex.Phase, s.Pos, ex.Message) : ex;
        }
#pragma warning disable CA1031 // fail loud: whatever the loader, the file system or the engine throws becomes a script error naming the statement
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            Restore(snap);
            throw new ScriptException(ErrorPhase.Execution, s.Pos, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---- execution of statements ---------------------------------------------------------------------------------------

    private void ExecBody(IEnumerable<Statement> body, Scope scope, StringBuilder o, int depth)
    {
        foreach (Statement st in body) ExecStatement(st, scope, o, depth);
    }

    private void ExecStatement(Statement s, Scope scope, StringBuilder o, int depth)
    {
        switch (s)
        {
            case IfStatement i: ExecIf(i, scope, o, depth); break;
            case ForEachStatement f: ExecFor(f, scope, o, depth); break;
            case ProcStatement p: ExecProc(p, depth); break;
            case CallStatement c: ExecCall(c, scope, o, depth); break;
            default: ExecSimple(s, scope, o, depth); break;
        }
    }

    private void ExecSimple(Statement s, Scope scope, StringBuilder o, int depth)
    {
        if (Limits is { } lim && lim.Cancel.IsCancellationRequested)
            throw new ScriptException(ErrorPhase.Execution, s.Pos, "stopped: the host's time limit for this run was reached");
        Statement bound = Binder.Bind(s, scope, this);
        Validator.Validate(bound, this);
        int seq = ++_seq;
        var local = new StringBuilder();
        Run(bound, scope, seq, local);
        string text = local.ToString();
        string? outHash = text.Length == 0 ? null : Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (s is ReportStatement or PrintStatement)
            _outputs.Add(new OutputEntry(seq, s.Pos.Line, s is ReportStatement ? "report" : "print", s.Canonical(), Encoding.UTF8.GetByteCount(text), outHash!));
        string canon = s.Canonical(), boundText = bound.Canonical();
        _statements.Add(new StatementEntry(seq, s.Pos.Line, s.Pos.Column, ClockUs, canon, outHash, depth, boundText == canon ? null : boundText));
        o.Append(text);
    }

    private int OpenBlock(Statement s, int depth, string? resolved = null)
    {
        int seq = ++_seq;
        _statements.Add(new StatementEntry(seq, s.Pos.Line, s.Pos.Column, ClockUs, s.Header(), null, depth, resolved));
        return _statements.Count - 1;
    }

    private void CloseBlock(int index, string produced)
    {
        StatementEntry e = _statements[index];
        _statements[index] = e with { TimeUs = ClockUs, OutputSha256 = produced.Length == 0 ? null : Hex(SHA256.HashData(Encoding.UTF8.GetBytes(produced))) };
    }

    private void ExecIf(IfStatement i, Scope scope, StringBuilder o, int depth)
    {
        int idx = OpenBlock(i, depth);
        int seq = _statements[idx].Seq;
        var detail = new List<string>();
        bool res = Evaluator.EvalCond(i.Condition, scope, this, detail);
        _decisions.Add(new DecisionEntry(seq, i.Pos.Line, i.Condition.Canonical(), string.Join("; ", detail), res));
        var local = new StringBuilder();
        if (res) ExecBody(i.Then, scope.Child(), local, depth + 1);
        else if (i.Else is { } els) ExecBody(els, scope.Child(), local, depth + 1);
        CloseBlock(idx, local.ToString());
        o.Append(local);
    }

    private void ExecFor(ForEachStatement f, Scope scope, StringBuilder o, int depth)
    {
        int idx = OpenBlock(f, depth);
        int seq = _statements[idx].Seq;
        CaseInfo ci = Case!;
        IReadOnlyList<string> names = f.Noun switch
        {
            Noun.Generator => ci.GeneratorNames,
            Noun.Line => ci.LineNames,
            Noun.Load => ci.LoadNames,
            _ => ci.BusNames,
        };
        var matched = ImmutableArray.CreateBuilder<string>();
        var local = new StringBuilder();
        foreach (string name in names.ToList())
        {
            Scope sc = scope.Child();
            sc.Vars[f.Var.Value] = Value.Elem(f.Noun, name);
            if (f.Where is { } w && !Evaluator.EvalCond(w, sc, this, []))
                continue;
            matched.Add(name);
            ExecBody(f.Body, sc, local, depth + 1);
        }
        _loops.Add(new LoopEntry(seq, f.Pos.Line, f.Noun.ToString().ToUpperInvariant(), f.Where?.Canonical() ?? "", names.Count, matched.ToImmutable()));
        CloseBlock(idx, local.ToString());
        o.Append(local);
    }

    private void ExecProc(ProcStatement p, int depth)
    {
        int seq = ++_seq;
        Procs = Procs.Add(p.Name.Value, p);
        string body = string.Join("\n", p.Body.Select(b => b.Canonical()));
        _procEntries.Add(new ProcEntry(seq, p.Pos.Line, p.Name.Value, p.Header(), Hex(SHA256.HashData(Encoding.UTF8.GetBytes(body)))));
        _statements.Add(new StatementEntry(seq, p.Pos.Line, p.Pos.Column, ClockUs, p.Header(), null, depth));
    }

    private void ExecCall(CallStatement c, Scope scope, StringBuilder o, int depth)
    {
        if (!Procs.TryGetValue(c.Name.Value, out ProcStatement? proc))
            throw new ScriptException(ErrorPhase.Execution, c.Name.Pos, $"no procedure named '{c.Name.Value}'");
        Scope root = scope;
        while (root.Parent is not null) root = root.Parent;
        Scope callScope = root.Child();
        var shown = new List<string>();
        for (int i = 0; i < proc.Params.Length; i++)
        {
            ProcParam p = proc.Params[i];
            Expr a = c.Args[i];
            Value v;
            if (p.Noun is { } noun)
            {
                string name = a is StringExpr str ? str.Text : Evaluator.ResolveName(new NameRef(((VarExpr)a).Name, a.Pos, true), noun, scope);
                if (a is StringExpr) Validator.RequireName(noun, new Located<string>(name, a.Pos), this);
                v = Value.Elem(noun, name);
            }
            else
            {
                v = Evaluator.Eval(a, scope, this);
                if (v.Type.Kind == VKind.Quantity && p.Unit is { } pu && Units.IsTime(pu) && Units.IsTime(v.Type.Unit))
                    v = Value.Num(Units.Convert(v.Number, v.Type.Unit, pu), pu);
            }
            callScope.Vars[p.Name.Value] = v;
            shown.Add(v.Display());
        }
        string resolved = "CALL " + c.Name.Value + (shown.Count == 0 ? "" : "(" + string.Join(", ", shown) + ")");
        int idx = OpenBlock(c, depth, resolved == c.Canonical() ? null : resolved);
        var local = new StringBuilder();
        ExecBody(proc.Body, callScope, local, depth + 1);
        CloseBlock(idx, local.ToString());
        o.Append(local);
    }

    // ------------------------------------------------------------------------------------------------------------------

    private void Run(Statement s, Scope scope, int seq, StringBuilder o)
    {
        switch (s)
        {
            case GrammarStatement:
                break;
            case LoadCaseStatement l: RunLoad(l, seq, o); break;
            case SolveStatement: RunSolve(o); break;
            case TripStatement or CloseStatement or SetStatement or FaultStatement or ClearFaultStatement: ApplyNow(s); break;
            case ReportStatement r: RunReport(r, o); break;
            case ExportResultStatement e: RunExport(e, seq, o); break;
            case SelectEngineStatement se: RunSelect(se, seq, o); break;
            case StepSimStatement st: RunStep(st, seq, o); break;
            case AtStatement a:
            {
                long us = Us(Binder.Q(a.Time).Value);
                Pending.Add(new PendingEvent(us, _pendingOrder++, a));
                o.Append(F($"scheduled at {Clock(us)}: {a.Inner.Canonical()}\n"));
                break;
            }
            case LetStatement le:
                scope.Vars[le.Name.Value] = Evaluator.Eval(le.Value, scope, this);
                break;
            case AssignStatement asg:
            {
                Value v = Evaluator.Eval(asg.Value, scope, this);
                scope.TryGet(asg.Name.Value, out Value existing);
                if (existing.Type.Kind == VKind.Quantity && v.Type.Kind == VKind.Quantity && existing.Type.Unit != v.Type.Unit)
                    v = Value.Num(Units.Convert(v.Number, v.Type.Unit, existing.Type.Unit), existing.Type.Unit);
                scope.TryAssign(asg.Name.Value, v);
                break;
            }
            case PrintStatement p:
                o.Append(F($"{p.Value.Canonical()} = {Evaluator.Eval(p.Value, scope, this).Display()}\n"));
                break;
            default: throw new ScriptException(ErrorPhase.Execution, s.Pos, "unknown statement");
        }
    }

    private static long Us(Quantity q) { q.TryToMicroseconds(out long us); return us; }

    private static string Clock(long us) => (us / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ---- readings ------------------------------------------------------------------------------------------------------

    private static decimal Dec(double d, SourcePos pos)
    {
        if (!double.IsFinite(d)) throw new ScriptException(ErrorPhase.Execution, pos, "the reading is not a finite number");
        try { return (decimal)d; }
        catch (OverflowException) { throw new ScriptException(ErrorPhase.Execution, pos, "the reading is out of range"); }
    }

    /// <summary>The solved state a reading or report looks at: under LIVE once a step has run, the live network as of the last
    /// step; otherwise the last SOLVE POWERFLOW, which must be current.</summary>
    private (PowerFlowResult Result, string? Header) CurrentResult()
    {
        if (EngineKind == EngineKind.Live && Live!.LastFrame is { } f)
            return (f.Result, F($"live network as of the last step, t = {Clock(ClockUs)} (tick {f.Tick})\n"));
        return (FreshSolved().Result, null);
    }

    internal Value Reading(Measure m, Noun noun, string name, SourcePos pos)
    {
        CaseInfo ci = Case!;
        GridModel model = Plant!.Model;
        if (!Measures.IsSolved(m))
        {
            if (noun == Noun.Generator)
            {
                Generator g = model.AllGenerators[ci.GeneratorIndex[name]];
                return m switch
                {
                    Measure.P => Value.Num(g.InService ? Dec(g.Pg, pos) : 0m, Unit.Mw),
                    Measure.Q => Value.Num(g.InService ? Dec(g.Qg, pos) : 0m, Unit.Mvar),
                    _ => Value.Num(Dec(g.Vg, pos), Unit.Pu),
                };
            }
            Bus b = model.Buses[ci.LoadBusIndex[ci.LoadIndex[name]]];
            return m == Measure.P ? Value.Num(Dec(b.Pd, pos), Unit.Mw) : Value.Num(Dec(b.Qd, pos), Unit.Mvar);
        }
        PowerFlowResult res = CurrentResult().Result;
        if (noun == Noun.Bus)
        {
            int id = model.Buses[ci.BusIndex[name]].Id;
            int j = -1;
            for (int i = 0; i < res.BusIds.Count; i++) if (res.BusIds[i] == id) { j = i; break; }
            if (j < 0) throw new ScriptException(ErrorPhase.Execution, pos, $"bus \"{name}\" is not in the solved state (it is in a dead island)");
            return m == Measure.Vm ? Value.Num(Dec(res.Vm[j], pos), Unit.Pu) : Value.Num(Dec(res.VaDeg[j], pos), Unit.Deg);
        }
        int li = ci.LineIndex[name];
        if (li >= res.LineFlows.Count) throw new ScriptException(ErrorPhase.Execution, pos, $"line \"{name}\" is not in the solved state");
        LineFlow lf = res.LineFlows[li];
        return m switch
        {
            Measure.PFrom => Value.Num(Dec(lf.PFromMw, pos), Unit.Mw),
            Measure.QFrom => Value.Num(Dec(lf.QFromMvar, pos), Unit.Mvar),
            Measure.Loading => Value.Num(Dec(lf.LoadingPercent / 100.0, pos), Unit.Pu),
            _ => Value.Num(Dec(lf.PLossMw, pos), Unit.Mw),
        };
    }

    internal Value SystemReading(SystemQuantity q, SourcePos pos)
    {
        GridModel model = Plant!.Model;
        switch (q)
        {
            case SystemQuantity.Time:
                return Value.Num(ClockUs / 1000m, Unit.Ms);
            case SystemQuantity.Generation:
                return Value.Num(Dec(model.Generators.Sum(g => g.Pg), pos), Unit.Mw);
            case SystemQuantity.Demand:
                return Value.Num(Dec(model.Buses.Sum(b => b.Pd), pos), Unit.Mw);
            case SystemQuantity.Losses:
                return Value.Num(Dec(CurrentResult().Result.TotalLossMw, pos), Unit.Mw);
            default:
            {
                double? hz = EngineKind switch
                {
                    EngineKind.Live => Live!.LastFrame?.FrequencyHz,
                    EngineKind.Frequency or EngineKind.Protection => Replay!.Last?.FrequencyHz,
                    _ => throw new ScriptException(ErrorPhase.Execution, pos, "FREQUENCY is read from the LIVE, FREQUENCY or PROTECTION engine; none of them is selected"),
                };
                if (hz is null) throw new ScriptException(ErrorPhase.Execution, pos, "FREQUENCY has no value yet: no step has run");
                return Value.Num(Dec(hz.Value, pos), Unit.Hz);
            }
        }
    }

    internal bool InService(Noun noun, string name)
    {
        CaseInfo ci = Case!;
        GridModel model = Plant!.Model;
        return noun switch
        {
            Noun.Generator => model.AllGenerators[ci.GeneratorIndex[name]].InService,
            Noun.Line => model.Branches[ci.LineIndex[name]].InService,
            _ => !Plant.TrippedLoads.ContainsKey(name),
        };
    }

    // ---- LOAD ----------------------------------------------------------------------------------------------------------

    private void RunLoad(LoadCaseStatement l, int seq, StringBuilder o)
    {
        string written = l.Path.Value;
        string full = Path.GetFullPath(written, _baseDir);
        GridModel model;
        string kind, sha;
        double? hz = l.Frequency is { } f ? (double)f.Value.Value : null;
        if (Directory.Exists(full))
        {
            if (hz is not null) throw new ScriptException(ErrorPhase.Execution, l.Frequency!.Value.Pos, "FREQUENCY applies to a MATPOWER .m file; a case folder carries its own frequency");
            kind = "json-folder";
            sha = HashFolder(full);
            model = JsonGridLoader.LoadDirectory(full);
        }
        else if (File.Exists(full))
        {
            byte[] bytes = File.ReadAllBytes(full);
            sha = Hex(SHA256.HashData(bytes));
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext == ".m")
            {
                kind = "matpower";
                model = MatpowerParser.Parse(Encoding.UTF8.GetString(bytes), Path.GetFileNameWithoutExtension(full), hz);
            }
            else if (ext == ".raw")
            {
                if (hz is not null) throw new ScriptException(ErrorPhase.Execution, l.Frequency!.Value.Pos, "FREQUENCY applies to a MATPOWER .m file; a PSS/E file carries its own frequency");
                kind = "psse-raw";
                model = PsseRawParser.Parse(Encoding.UTF8.GetString(bytes), Path.GetFileNameWithoutExtension(full));
            }
            else
                throw new ScriptException(ErrorPhase.Execution, l.Path.Pos, $"cannot load \"{written}\": grammar 1 loads a MATPOWER .m file, a PSS/E .raw file or a JSON case folder");
        }
        else
            throw new ScriptException(ErrorPhase.Execution, l.Path.Pos, $"cannot load \"{written}\": no such file or folder (relative paths are taken from {_baseDir})");

        GridDiagnostic? bad = model.Validate().FirstOrDefault(d => d.Severity == ValidationSeverity.Error);
        if (bad is not null)
            throw new ScriptException(ErrorPhase.Execution, l.Path.Pos, "the case is not a valid model: " + bad.Message);

        Case = new CaseInfo(written, kind, sha, model);
        Plant = Plant.From(model);
        Solved = null;
        EngineKind = EngineKind.Static;
        Live = null;
        Replay = null;
        ClockUs = 0;
        Pending = [];
        _inputs.Add(new InputEntry(seq, l.Pos.Line, kind, written, sha));
        o.Append(F($"loaded \"{model.Name}\": {model.Buses.Count} buses, {model.Branches.Count} branches, {model.AllGenerators.Count} generators, {Case.LoadNames.Count} loads, base {model.BaseMva} MVA, {model.FrequencyHz} Hz, sha256 {sha[..12]}\n"));
    }

    private static string HashFolder(string dir)
    {
        var lines = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            lines.Add(rel + "\0" + Hex(SHA256.HashData(File.ReadAllBytes(file))));
        }
        lines.Sort(StringComparer.Ordinal);
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    // ---- SOLVE ---------------------------------------------------------------------------------------------------------

    private void RunSolve(StringBuilder o)
    {
        var trace = new SolveTrace();
        var opts = new PowerFlowOptions { Trace = trace };
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(Plant!.Model, opts);
        if (!r.Converged)
            throw new ScriptException(ErrorPhase.Execution, default,
                F($"the power flow did not converge ({r.Status}, largest mismatch {r.MaxMismatch:E3} pu after {r.Iterations} iterations); the solved state is unchanged"));
        Solved = new SolvedState(Plant.Model, r, trace, Plant.Version);
        _lastOptions = new PowerFlowOptions();
        _lastSolverName = trace.SolverName;
        o.Append(F($"power flow converged in {r.Iterations} iterations, largest mismatch {r.MaxMismatch:E3} pu, losses {r.TotalLossMw:F4} MW, solver {trace.SolverName}\n"));
    }

    // ---- TRIP / CLOSE / SET / FAULT / CLEAR ---------------------------------------------------------------------------

    /// <summary>A bound edit or fault statement, taking effect at the script clock.</summary>
    private void ApplyNow(Statement s)
    {
        switch (EngineKind)
        {
            case EngineKind.Rms or EngineKind.Emt:
                Replay = Replay!.WithEvent(MakeReplayEvent(s, ClockUs, _pendingOrder++, s.Pos.Line));
                break;
            default:
                ApplyEdit(s, ClockUs, _pendingOrder++, s.Pos.Line);
                break;
        }
    }

    /// <summary>TRIP, CLOSE or SET (<paramref name="s"/> bound): the plant changes, and the engine is told.</summary>
    private void ApplyEdit(Statement s, long timeUs, int order, int scheduledAtLine)
    {
        EditOutcome e = ModelEdit.Apply(s, Case!, Plant!);
        Plant = e.Plant;
        switch (EngineKind)
        {
            case EngineKind.Live:
                Live!.SetNetwork(Plant.Model);
                Live.Infeed(e.NetInfeedDeltaMw);
                break;
            case EngineKind.Frequency or EngineKind.Protection:
                if (e.NetInfeedDeltaMw != 0)
                    Replay = Replay!.WithEvent(new ReplayEvent(timeUs, order, s.Canonical(), scheduledAtLine, new InfeedEffect(e.NetInfeedDeltaMw, ElementBus(s))));
                break;
        }
    }

    private int ElementBus(Statement s)
    {
        (Noun noun, string name) = s switch
        {
            TripStatement t => (t.Noun, t.Name.Value),
            CloseStatement c => (c.Noun, c.Name.Value),
            SetStatement st => (st.Noun, st.Name.Value),
            _ => throw new ScriptException(ErrorPhase.Execution, s.Pos, "this statement has no element"),
        };
        CaseInfo ci = Case!;
        GridModel m = Plant!.Model;
        return noun switch
        {
            Noun.Generator => m.AllGenerators[ci.GeneratorIndex[name]].Bus,
            Noun.Load => m.Buses[ci.LoadBusIndex[ci.LoadIndex[name]]].Id,
            _ => m.Branches[ci.LineIndex[name]].From,
        };
    }

    private ReplayEvent MakeReplayEvent(Statement s, long timeUs, int order, int line)
    {
        CaseInfo ci = Case!;
        return s switch
        {
            FaultStatement f => new ReplayEvent(timeUs, order, s.Canonical(), line, new FaultEffect(Plant!.Model.Buses[ci.BusIndex[f.Bus.Value]].Id, On: true)),
            ClearFaultStatement c => new ReplayEvent(timeUs, order, s.Canonical(), line, new FaultEffect(Plant!.Model.Buses[ci.BusIndex[c.Bus.Value]].Id, On: false)),
            _ => throw new ScriptException(ErrorPhase.Execution, s.Pos, "this engine takes faults only"),
        };
    }

    // ---- REPORT --------------------------------------------------------------------------------------------------------

    private SolvedState FreshSolved()
    {
        if (Solved is null) throw new ScriptException(ErrorPhase.Execution, default, "there is no solved state; run SOLVE POWERFLOW first");
        if (Solved.PlantVersion != Plant!.Version)
            throw new ScriptException(ErrorPhase.Execution, default, "the solved state is out of date: the model was edited after the last solve; run SOLVE POWERFLOW again");
        return Solved;
    }

    private void RunReport(ReportStatement r, StringBuilder o)
    {
        switch (r.Kind)
        {
            case ReportKind.Buses:
            {
                (PowerFlowResult res, string? header) = CurrentResult();
                if (header is not null) o.Append(header);
                o.Append("bus        Vm(pu)    Va(deg)     Pinj(MW)   Qinj(MVAr)\n");
                for (int i = 0; i < res.BusIds.Count; i++)
                    o.Append(F($"{res.BusIds[i],-6} {res.Vm[i],10:F6} {res.VaDeg[i],10:F4} {res.PInjMw[i],12:F4} {res.QInjMvar[i],12:F4}\n"));
                break;
            }
            case ReportKind.Branches:
            {
                (PowerFlowResult res, string? header) = CurrentResult();
                if (header is not null) o.Append(header);
                o.Append("line             from    to     Pfrom(MW)  Qfrom(MVAr)     Pto(MW)    Qto(MVAr)  Ploss(MW)  Load(%)\n");
                for (int i = 0; i < res.LineFlows.Count; i++)
                {
                    LineFlow lf = res.LineFlows[i];
                    string name = i < Case!.LineNames.Count ? Case.LineNames[i] : lf.Name;
                    o.Append(F($"{name,-16} {lf.From,4} {lf.To,5} {lf.PFromMw,13:F4} {lf.QFromMvar,12:F4} {lf.PToMw,11:F4} {lf.QToMvar,11:F4} {lf.PLossMw,10:F4} {lf.LoadingPercent,8:F2}\n"));
                }
                break;
            }
            case ReportKind.Elements:
            {
                CaseInfo ci = Case!;
                GridModel m = Plant!.Model;
                o.Append("generators\n");
                for (int i = 0; i < ci.GeneratorNames.Count; i++)
                {
                    Generator g = m.AllGenerators[i];
                    o.Append(F($"  \"{ci.GeneratorNames[i]}\" bus {g.Bus} P {g.Pg:F4} MW Q {g.Qg:F4} MVAR V {g.Vg:F4} PU {(g.InService ? "in service" : "TRIPPED")}\n"));
                }
                o.Append("loads\n");
                for (int k = 0; k < ci.LoadNames.Count; k++)
                {
                    string n = ci.LoadNames[k];
                    Bus b = m.Buses[ci.LoadBusIndex[k]];
                    bool tripped = Plant.TrippedLoads.TryGetValue(n, out (double Pd, double Qd) orig);
                    o.Append(F($"  \"{n}\" bus {b.Id} P {(tripped ? orig.Pd : b.Pd):F4} MW Q {(tripped ? orig.Qd : b.Qd):F4} MVAR {(tripped ? "TRIPPED" : "in service")}\n"));
                }
                o.Append("lines\n");
                for (int i = 0; i < ci.LineNames.Count; i++)
                {
                    Branch br = m.Branches[i];
                    o.Append(F($"  \"{ci.LineNames[i]}\" {br.From} to {br.To}{(br.IsTransformer ? " transformer" : "")} {(br.InService ? "in service" : "TRIPPED")}\n"));
                }
                o.Append("buses\n");
                for (int i = 0; i < ci.BusNames.Count; i++)
                    o.Append(F($"  \"{ci.BusNames[i]}\" id {m.Buses[i].Id} base {m.Buses[i].BaseKv:F2} KV\n"));
                break;
            }
            default:
                ReportSim(o);
                break;
        }
    }

    private void ReportSim(StringBuilder o)
    {
        if (EngineKind == EngineKind.Live)
        {
            GridSim.Core.Simulation.SimFrame? frame = Live!.LastFrame;
            o.Append(F($"t {Clock(ClockUs)}, engine LIVE, step {Clock(Live.StepUs)}\n"));
            if (frame is null) { o.Append("no step has run yet\n"); return; }
            o.Append(F($"tick {frame.Tick}, frequency {frame.FrequencyHz:F6} Hz, rocof {frame.RocofHzPerS:F6} Hz/s, generation {frame.GenerationMw:F4} MW, demand {frame.DemandMw:F4} MW\n"));
            return;
        }
        ReplayEngine re = Replay!;
        o.Append(F($"t {Clock(ClockUs)}, engine {re.KindName}, step {Clock(re.StepUs)}\n"));
        if (re.Last is null) { o.Append("no step has run yet\n"); return; }
        foreach (string line in re.Last.Lines) o.Append(line).Append('\n');
    }

    // ---- EXPORT --------------------------------------------------------------------------------------------------------

    private void RunExport(ExportResultStatement e, int seq, StringBuilder o)
    {
        SolvedState sv = FreshSolved();
        // The meta block holds no clock, host or timing: those would make the same script write different bytes.
        var meta = new SolveExportMeta(sv.Model.Name, "gridsim-script", "", "", 0, "");
        string json = SolveExport.ToJson(sv.Model, sv.Result, sv.Trace, meta).Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        string full = Path.GetFullPath(e.Path.Value, _baseDir);
        if (File.Exists(full))
        {
            if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
                throw new ScriptException(ErrorPhase.Execution, e.Path.Pos, $"\"{e.Path.Value}\" already exists with different content; a script never overwrites a file (remove it first)");
        }
        else
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(full, bytes);
        }
        string sha = Hex(SHA256.HashData(bytes));
        _outputs.Add(new OutputEntry(seq, e.Pos.Line, "export", e.Path.Value, bytes.Length, sha));
        o.Append(F($"exported {bytes.Length} bytes to \"{e.Path.Value}\", sha256 {sha[..12]}\n"));
    }

    // ---- SELECT ENGINE / STEP ------------------------------------------------------------------------------------------

    private void RunSelect(SelectEngineStatement se, int seq, StringBuilder o)
    {
        ClockUs = 0;
        Pending = [];
        Live = null;
        Replay = null;
        string kind = se.Engine.Value.ToString().ToUpperInvariant();
        switch (se.Engine.Value)
        {
            case EngineKind.Static:
                EngineKind = EngineKind.Static;
                _engines.Add(new EngineEntry(seq, se.Pos.Line, kind, 0, "no stepping"));
                o.Append("engine STATIC: no stepping\n");
                return;
            case EngineKind.Live:
            {
                LiveEngine.Options opts = EngineSetup.BuildLive(se);
                Live = new LiveEngine(Plant!.Model, opts);
                EngineKind = EngineKind.Live;
                _engines.Add(new EngineEntry(seq, se.Pos.Line, kind, opts.StepUs, opts.Canonical()));
                o.Append(F($"engine LIVE selected: integration step {Clock(opts.StepUs)}, nominal {Live.NominalHz} Hz, flow solver {Live.ActiveSolver}; script clock 0 ms\n"));
                return;
            }
            default:
            {
                ReplaySettings rs = EngineSetup.BuildReplay(se);
                Replay = ReplayEngine.Create(rs, Plant!, Case!, rs.Kind == ReplayKind.Rms ? FreshSolved().Result : null, _baseDir);
                if (Replay.DynamicsFile is not null)
                    _inputs.Add(new InputEntry(seq, se.Pos.Line, "dynamics", rs.DynamicsPath!, Replay.DynamicsSha256!));
                EngineKind = se.Engine.Value;
                _engines.Add(new EngineEntry(seq, se.Pos.Line, kind, rs.StepUs, rs.Describe()));
                o.Append(F($"engine {kind} selected: {rs.Describe()}; script clock 0 ms\n"));
                return;
            }
        }
    }

    private void RunStep(StepSimStatement st, int seq, StringBuilder o)
    {
        long dur = Us(Binder.Q(st.Duration).Value);
        long from = ClockUs, to = ClockUs + dur;
        if (Limits is { } lim && StepUs > 0)
        {
            long ticks = dur / StepUs;
            if (lim.MaxTicksPerStep is { } perStep && ticks > perStep)
                throw new ScriptException(ErrorPhase.Execution, st.Pos, $"STEP SIM of {ticks} ticks is over this host's limit of {perStep} ticks for one STEP SIM (use a shorter duration or a longer engine step)");
            if (lim.MaxTotalTicks is { } total && _totalTicks + ticks > total)
                throw new ScriptException(ErrorPhase.Execution, st.Pos, $"this host allows {total} stepped ticks in one session; {_totalTicks} are used and this STEP SIM needs {ticks} more");
            _totalTicks += ticks;
        }

        // Events due in this step: those at or before the start of its last tick, in time order, script order within a time.
        List<PendingEvent> due = Pending.Where(p => p.TimeUs < to).OrderBy(p => p.TimeUs).ThenBy(p => p.Order).ToList();

        // Dry run: every due event must apply to the plant (and the engine) as it will be when its turn comes. The first that
        // cannot is reported at its own line and column, before the engine has moved.
        Plant dry = Plant!;
        ReplayEngine? dryEngine = Replay;
        foreach (PendingEvent pe in due)
        {
            try
            {
                Statement inner = pe.Statement.Inner;
                if (inner is FaultStatement or ClearFaultStatement)
                    dryEngine = dryEngine!.WithEvent(MakeReplayEvent(inner, pe.TimeUs, pe.Order, pe.Statement.Pos.Line));
                else dry = ModelEdit.Apply(inner, Case!, dry).Plant;
            }
            catch (ScriptException ex) { throw new ScriptException(ErrorPhase.Execution, ex.Pos.Line == 0 ? pe.Statement.Pos : ex.Pos, "scheduled event (AT " + Binder.Q(pe.Statement.Time).Value + "): " + ex.Message); }
        }

        if (EngineKind == EngineKind.Live) StepLive(seq, st, from, to, due, o);
        else StepReplay(seq, st, from, to, due, o);

        Pending = Pending.Where(p => !due.Contains(p)).ToList();
        ClockUs = to;
    }

    private void StepLive(int seq, StepSimStatement st, long from, long to, List<PendingEvent> due, StringBuilder o)
    {
        LiveEngine live = Live!;
        long dur = to - from;
        int ticks = checked((int)(dur / live.StepUs));
        using var series = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int next = 0;
        SimFrameRef last = default;
        for (int k = 0; k < ticks; k++)
        {
            long t0 = from + k * live.StepUs;
            while (next < due.Count && due[next].TimeUs <= t0)
            {
                PendingEvent pe = due[next++];
                ApplyEdit(pe.Statement.Inner, t0, pe.Order, pe.Statement.Pos.Line);
                _applied.Add(new AppliedEvent(seq, t0, pe.Statement.Inner.Canonical(), pe.Statement.Pos.Line, seq));
            }
            GridSim.Core.Simulation.SimFrame fr = live.Tick();
            series.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(fr.FrequencyHz)));
            last = new SimFrameRef(fr.FrequencyHz, fr.RocofHzPerS);
        }
        string hz = last.FrequencyHz.ToString("R", CultureInfo.InvariantCulture), rocof = last.RocofHzPerS.ToString("R", CultureInfo.InvariantCulture);
        _steps.Add(new StepEntry(seq, st.Pos.Line, from, to, ticks, "LIVE",
            [new("frequencyHz", hz), new("rocofHzPerS", rocof)], Hex(series.GetHashAndReset())));
        o.Append(F($"stepped {ticks} x {Clock(live.StepUs)} to t = {Clock(to)}: frequency {last.FrequencyHz:F6} Hz, rocof {last.RocofHzPerS:F6} Hz/s\n"));
    }

    private void StepReplay(int seq, StepSimStatement st, long from, long to, List<PendingEvent> due, StringBuilder o)
    {
        ReplayEngine re = Replay!;
        foreach (PendingEvent pe in due)
        {
            Statement inner = pe.Statement.Inner;
            if (inner is FaultStatement or ClearFaultStatement)
                re = re.WithEvent(MakeReplayEvent(inner, pe.TimeUs, pe.Order, pe.Statement.Pos.Line));
            else
            {
                Replay = re;
                ApplyEdit(inner, pe.TimeUs, pe.Order, pe.Statement.Pos.Line);
                re = Replay!;
            }
            _applied.Add(new AppliedEvent(seq, pe.TimeUs, inner.Canonical(), pe.Statement.Pos.Line, seq));
        }
        (ReplayEngine next, ReplayStep step) = re.Advance(to - from);
        Replay = next;
        _steps.Add(new StepEntry(seq, st.Pos.Line, from, to, step.Ticks, re.KindName, step.Final, step.SeriesSha256));
        o.Append(F($"stepped {step.Ticks} x {Clock(re.StepUs)} to t = {Clock(to)}: {step.Line}\n"));
    }

    private readonly record struct SimFrameRef(double FrequencyHz, double RocofHzPerS);

    /// <summary>The live engine's run-record settings, for the record; null when no live engine is selected.</summary>
    public (string Solver, long StepUs, double NominalHz)? LiveSettings =>
        Live is null ? null : (Live.ActiveSolver, Live.StepUs, Live.NominalHz);

    /// <summary>The SHA-256 of the case that was loaded last, or null.</summary>
    public string? ModelSha256 => Case?.Sha256;
}
