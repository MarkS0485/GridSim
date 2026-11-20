using System.Collections.Immutable;
using System.Globalization;
using GridSim.Core.PowerFlow;

namespace GridSim.Scripting;

/// <summary>
/// The second stage: a parsed statement against the session's loaded model. <see cref="Check"/> looks at the whole
/// statement, bodies included, before anything runs: that a case is loaded where one is needed, that every literal name
/// exists, that the verb, the noun and the engine go together, that every value carries the unit its position demands, that
/// every named value is defined, and that procedures are called with the right arguments and never call themselves.
/// <see cref="Validate"/> then looks at one simple statement with its values written out (the state it runs in): ranges,
/// whole multiples of the step, events in the past. Nothing is changed. Errors are <see cref="ErrorPhase.Validation"/> and
/// name the offending token.
/// </summary>
public static class Validator
{
    private static ScriptException V(SourcePos pos, string msg) => new(ErrorPhase.Validation, pos, msg);

    // ---- static check --------------------------------------------------------------------------------------------------

    /// <summary>Check a top-level statement and everything inside it against the session as it stands.</summary>
    public static void Check(Statement s, Session session)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(session);
        TypeScope scope = session.GlobalTypes();
        CheckStatement(s, session, scope, defining: null);
    }

    private static void CheckBody(IEnumerable<Statement> body, Session session, TypeScope scope, string? defining)
    {
        foreach (Statement st in body) CheckStatement(st, session, scope, defining);
    }

    private static void CheckStatement(Statement s, Session session, TypeScope scope, string? defining)
    {
        bool strict = defining is null;   // a procedure body read at its definition may name a case or an engine not there yet
        switch (s)
        {
            case GrammarStatement:
                break;
            case LoadCaseStatement l:
                if (string.IsNullOrWhiteSpace(l.Path.Value)) throw V(l.Path.Pos, "the case path is empty");
                if (l.Frequency is { } f)
                {
                    RequireUnit(f, Unit.Hz);
                    double hz = (double)f.Value.Value;
                    if (!(hz >= 10 && hz <= 100)) throw V(f.Pos, $"a nominal frequency of {f.Value.Value} HZ is outside 10 to 100 HZ");
                }
                break;
            case SolveStatement:
                if (strict) RequireCase(s, session);
                break;
            case TripStatement t:
                if (strict) RequireCase(s, session);
                CheckName(t.Noun, t.Name, scope, session);
                if (strict) RequireEditAllowed(t.Noun, "TRIP", t.Pos, session, state: true);
                break;
            case CloseStatement c:
                if (strict) RequireCase(s, session);
                CheckName(c.Noun, c.Name, scope, session);
                if (strict) RequireEditAllowed(c.Noun, "CLOSE", c.Pos, session, state: true);
                break;
            case SetStatement st:
                if (strict) RequireCase(s, session);
                CheckSet(st, session, scope, strict);
                break;
            case FaultStatement fa:
                if (strict) RequireCase(s, session);
                if (strict) RequireFaultEngine(fa.Pos, "FAULT", session);
                CheckName(Noun.Bus, fa.Bus, scope, session);
                break;
            case ClearFaultStatement cf:
                if (strict) RequireCase(s, session);
                if (strict) RequireFaultEngine(cf.Pos, "CLEAR FAULT", session);
                CheckName(Noun.Bus, cf.Bus, scope, session);
                break;
            case ReportStatement r:
                if (r.Kind == ReportKind.Sim)
                {
                    if (strict && session.EngineKind == EngineKind.Static)
                        throw V(r.Pos, "REPORT SIM needs a stepping engine; run SELECT ENGINE LIVE first");
                }
                else RequireCase(s, session);
                break;
            case ExportResultStatement e:
                if (strict) RequireCase(s, session);
                if (string.IsNullOrWhiteSpace(e.Path.Value)) throw V(e.Path.Pos, "the output file path is empty");
                break;
            case SelectEngineStatement se:
                if (strict) RequireCase(s, session);
                CheckSelect(se, session);
                break;
            case StepSimStatement ss:
                if (strict) RequireCase(s, session);
                if (strict && session.EngineKind == EngineKind.Static)
                    throw V(ss.Pos, "STEP SIM needs a stepping engine; run SELECT ENGINE LIVE first");
                RequireTimeType(Typer.TypeOfChecked(ss.Duration, scope, (n, r) => CheckName(n, r, scope, session)), ss.Duration, "the step");
                break;
            case AtStatement a:
                if (strict) RequireCase(s, session);
                if (strict && session.EngineKind == EngineKind.Static)
                    throw V(a.Pos, "AT needs a stepping engine; run SELECT ENGINE LIVE first");
                RequireTimeType(Typer.TypeOfChecked(a.Time, scope, (n, r) => CheckName(n, r, scope, session)), a.Time, "the event time");
                CheckStatement(a.Inner, session, scope, defining);
                break;
            case LetStatement le:
            {
                VType t = Typer.TypeOfChecked(le.Value, scope, (n, r) => CheckName(n, r, scope, session));
                if (!t.IsNumeric) throw V(le.Value.Pos, $"a value holds a number or a quantity, not {t.Describe()}");
                if (scope.TryGet(le.Name.Value, out _))
                    throw V(le.Name.Pos, $"'{le.Name.Value}' already names a value in scope; change it with SET {le.Name.Value} = ...");
                scope.Define(le.Name.Value, t);
                break;
            }
            case AssignStatement asg:
            {
                if (!scope.TryGet(asg.Name.Value, out VType existing))
                    throw V(asg.Name.Pos, $"no value named '{asg.Name.Value}'; create it with LET {asg.Name.Value} = ...");
                VType t = Typer.TypeOfChecked(asg.Value, scope, (n, r) => CheckName(n, r, scope, session));
                if (!existing.Accepts(t))
                    throw V(asg.Value.Pos, $"unit mismatch: '{asg.Name.Value}' holds {existing.Describe()}, cannot take {t.Describe()}");
                break;
            }
            case PrintStatement p:
                Typer.TypeOfChecked(p.Value, scope, (n, r) => CheckName(n, r, scope, session));
                break;
            case IfStatement iff:
                Typer.CheckCond(iff.Condition, scope, (n, r) => CheckName(n, r, scope, session));
                CheckBody(iff.Then, session, new TypeScope(scope), defining);
                if (iff.Else is { } els) CheckBody(els, session, new TypeScope(scope), defining);
                break;
            case ForEachStatement fe:
            {
                if (strict) RequireCase(s, session);
                if (scope.TryGet(fe.Var.Value, out _))
                    throw V(fe.Var.Pos, $"'{fe.Var.Value}' already names a value in scope");
                var inner = new TypeScope(scope);
                inner.Define(fe.Var.Value, VType.Element(fe.Noun));
                if (fe.Where is { } w) Typer.CheckCond(w, inner, (n, r) => CheckName(n, r, inner, session));
                CheckBody(fe.Body, session, inner, defining);
                break;
            }
            case ProcStatement pr:
                CheckProc(pr, session);
                break;
            case CallStatement call:
                CheckCall(call, session, scope, defining);
                break;
            default:
                throw V(s.Pos, "unknown statement");
        }
    }

    private static string Got(VType t) => t.Kind == VKind.Quantity ? Units.Keyword(t.Unit) : t.Describe();

    private static void RequireUnit(Located<Quantity> q, params Unit[] allowed)
    {
        if (Array.IndexOf(allowed, q.Value.Unit) < 0)
            throw V(q.Pos, $"unit mismatch: expected {Units.List(allowed)}, got {Units.Keyword(q.Value.Unit)}");
    }

    private static void RequireTimeType(VType t, Expr e, string what)
    {
        if (t.Kind != VKind.Quantity || !Units.IsTime(t.Unit))
            throw V(e.Pos, $"unit mismatch: {what} is a time, expected MS or S, got {Got(t)}");
    }

    private static void RequireCase(Statement s, Session session)
    {
        if (session.Case is null)
            throw V(s.Pos, "no case is loaded; start with LOAD CASE \"path\"");
    }

    private static void CheckName(Noun noun, NameRef r, TypeScope scope, Session session)
    {
        if (r.IsVariable) { Typer.CheckNameRef(r, noun, scope); return; }
        if (session.Case is null) return;   // a block may be read before a case is loaded; the first statement to need it says so
        RequireName(noun, new Located<string>(r.Value, r.Pos), session);
    }

    private static void RequireEditAllowed(Noun noun, string verb, SourcePos pos, Session session, bool state)
    {
        switch (session.EngineKind)
        {
            case EngineKind.Live:
                if (noun == Noun.Generator && state && session.Live!.HasAssetFleet)
                    throw V(pos, $"{verb} GENERATOR under LIVE needs the engine built without its asset fleet: this case carries authored assets, which bind units by position. Select the engine again with SELECT ENGINE LIVE WITH ASSETS OFF");
                break;
            case EngineKind.Frequency:
            case EngineKind.Protection:
                if (noun == Noun.Line)
                    throw V(pos, $"{verb} LINE has no meaning for the {session.EngineKind.ToString().ToUpperInvariant()} engine: it is a lumped swing with no network; use LIVE for line trips");
                break;
            case EngineKind.Rms:
            case EngineKind.Emt:
                throw V(pos, $"the {session.EngineKind.ToString().ToUpperInvariant()} engine takes faults only during a run (FAULT BUS and CLEAR FAULT BUS); {verb} changes the network, so do it before SELECT ENGINE");
        }
    }

    private static void RequireFaultEngine(SourcePos pos, string verb, Session session)
    {
        if (session.EngineKind is not (EngineKind.Rms or EngineKind.Emt))
            throw V(pos, $"{verb} needs the RMS or EMT engine; run SELECT ENGINE RMS or SELECT ENGINE EMT first");
    }

    private static void CheckSet(SetStatement st, Session session, TypeScope scope, bool strict)
    {
        if (st.Noun == Noun.Line)
            throw V(st.Pos, "SET does not apply to LINE in grammar 1; use TRIP and CLOSE");
        CheckName(st.Noun, st.Name, scope, session);
        VType t = Typer.TypeOfChecked(st.Value, scope, (n, r) => CheckName(n, r, scope, session));
        Unit[] allowed;
        switch (st.Property.Value)
        {
            case Property.P: allowed = [Unit.Mw]; break;
            case Property.Q: allowed = [Unit.Mvar]; break;
            default:
                if (st.Noun != Noun.Generator)
                    throw V(st.Property.Pos, "V (voltage set-point) applies to GENERATOR only");
                allowed = [Unit.Pu, Unit.Kv];
                break;
        }
        if (t.Kind != VKind.Quantity || Array.IndexOf(allowed, t.Unit) < 0)
            throw V(st.Value.Pos, $"unit mismatch: expected {Units.List(allowed)}, got {Got(t)}");
        if (!strict) return;
        RequireEditAllowed(st.Noun, "SET", st.Pos, session, state: false);
        if (session.EngineKind is EngineKind.Frequency or EngineKind.Protection && st.Property.Value != Property.P)
            throw V(st.Property.Pos, $"SET {st.Property.Value} has no effect on the {session.EngineKind.ToString().ToUpperInvariant()} engine, which is a lumped swing driven by active power only");
    }

    // ---- SELECT ENGINE -------------------------------------------------------------------------------------------------

    private static void CheckSelect(SelectEngineStatement se, Session session)
    {
        if (se.Step is { } sp)
        {
            if (se.Engine.Value == EngineKind.Static) throw V(sp.Pos, "STATIC has no integration step");
            RequireTime(sp, "the integration step", allowZero: false);
        }
        switch (se.Engine.Value)
        {
            case EngineKind.Static:
                if (!se.Settings.IsDefaultOrEmpty) throw V(se.Settings[0].Key.Pos, "STATIC takes no settings");
                break;
            case EngineKind.Live:
                EngineSetup.BuildLive(se);
                break;
            default:
                ReplaySettings rs = EngineSetup.BuildReplay(se);
                if (rs.SourceBus is { } sb && session.Case is { } ci && !ci.BusIndex.ContainsKey(sb))
                    throw V(se.Settings.First(c => c.Key.Value == "SOURCE").Name!.Value.Pos, $"no bus named \"{sb}\" in case \"{ci.Model.Name}\"");
                break;
        }
    }

    // ---- procedures ----------------------------------------------------------------------------------------------------

    private static void CheckProc(ProcStatement pr, Session session)
    {
        if (session.Procs.ContainsKey(pr.Name.Value))
            throw V(pr.Name.Pos, $"a procedure named '{pr.Name.Value}' is already defined; procedures are never redefined");
        var scope = new TypeScope(session.GlobalTypes());
        foreach (ProcParam p in pr.Params)
        {
            if (scope.TryGet(p.Name.Value, out _))
                throw V(p.Name.Pos, $"the parameter '{p.Name.Value}' hides a value of the session; choose another name");
            scope.Define(p.Name.Value, ParamType(p));
        }
        foreach (CallStatement c in CallsIn(pr.Body))
        {
            if (Reaches(c.Name.Value, pr.Name.Value, n => n == pr.Name.Value ? pr : session.Procs.GetValueOrDefault(n), []))
                throw V(c.Name.Pos, c.Name.Value == pr.Name.Value
                    ? $"procedure '{pr.Name.Value}' calls itself: recursion is not allowed, so every script ends"
                    : $"procedure '{pr.Name.Value}' calls '{c.Name.Value}', which leads back to '{pr.Name.Value}': recursion is not allowed, so every script ends");
        }
        CheckBody(pr.Body, session, scope, defining: pr.Name.Value);
    }

    private static VType ParamType(ProcParam p) => p.Noun is { } n ? VType.Element(n) : VType.Of(p.Unit!.Value);

    private static void CheckCall(CallStatement call, Session session, TypeScope scope, string? defining)
    {
        if (!session.Procs.TryGetValue(call.Name.Value, out ProcStatement? proc))
        {
            if (defining is not null) return;   // a procedure may call one defined later; it is looked up when called
            throw V(call.Name.Pos, $"no procedure named '{call.Name.Value}'; define it with PROC {call.Name.Value} ... END first");
        }
        if (call.Args.Length != proc.Params.Length)
            throw V(call.Pos, $"procedure '{proc.Name.Value}' takes {proc.Params.Length} argument(s), the call gives {call.Args.Length}");
        for (int i = 0; i < proc.Params.Length; i++)
        {
            ProcParam p = proc.Params[i];
            Expr a = call.Args[i];
            if (a is StringExpr str)
            {
                if (p.Noun is not { } pn)
                    throw V(a.Pos, $"argument {i + 1} of '{proc.Name.Value}' must be {Got(ParamType(p))}, got a name in quotes");
                CheckName(pn, new NameRef(str.Text, str.Pos, false), scope, session);
                continue;
            }
            VType t = Typer.TypeOfChecked(a, scope, (n, r) => CheckName(n, r, scope, session));
            if (!ParamType(p).Accepts(t))
                throw V(a.Pos, $"{(p.Noun is null && t.IsNumeric ? "unit mismatch" : "type mismatch")}: argument {i + 1} of '{proc.Name.Value}' must be {Got(ParamType(p))}, got {Got(t)}");
        }
        var inner = new TypeScope(session.GlobalTypes());
        foreach (ProcParam p in proc.Params) inner.Define(p.Name.Value, ParamType(p));
        try { CheckBody(proc.Body, session, inner, defining); }
        catch (ScriptException ex) when (!ex.Message.Contains("(in procedure", StringComparison.Ordinal))
        {
            throw new ScriptException(ex.Phase, ex.Pos, $"{ex.Message} (in procedure {proc.Name.Value}, called at {call.Pos})");
        }
    }

    /// <summary>Every CALL in a body, at any depth of blocks.</summary>
    internal static IEnumerable<CallStatement> CallsIn(IEnumerable<Statement> body)
    {
        foreach (Statement s in body)
        {
            switch (s)
            {
                case CallStatement c: yield return c; break;
                case IfStatement i:
                    foreach (CallStatement x in CallsIn(i.Then)) yield return x;
                    if (i.Else is { } e) foreach (CallStatement x in CallsIn(e)) yield return x;
                    break;
                case ForEachStatement f:
                    foreach (CallStatement x in CallsIn(f.Body)) yield return x;
                    break;
            }
        }
    }

    private static bool Reaches(string from, string target, Func<string, ProcStatement?> lookup, HashSet<string> visited)
    {
        if (from == target) return true;
        if (!visited.Add(from)) return false;
        ProcStatement? p = lookup(from);
        if (p is null) return false;
        foreach (CallStatement c in CallsIn(p.Body))
            if (Reaches(c.Name.Value, target, lookup, visited)) return true;
        return false;
    }

    /// <summary>
    /// The whole-script look at procedures, run before anything executes: a procedure is defined once, and no procedure
    /// reaches itself through the calls in its body, directly or through others (a procedure may call one defined later in the
    /// script). The error names the CALL that closes the cycle.
    /// </summary>
    public static void CheckProcedureGraph(IReadOnlyList<Statement> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var defs = new Dictionary<string, ProcStatement>(StringComparer.Ordinal);
        foreach (Statement s in script)
            if (s is ProcStatement p)
            {
                if (!defs.TryAdd(p.Name.Value, p))
                    throw V(p.Name.Pos, $"a procedure named '{p.Name.Value}' is already defined at line {defs[p.Name.Value].Pos.Line}; procedures are never redefined");
            }
        foreach (ProcStatement p in defs.Values.OrderBy(x => x.Pos.Line))
            foreach (CallStatement c in CallsIn(p.Body))
                if (Reaches(c.Name.Value, p.Name.Value, n => defs.GetValueOrDefault(n), []))
                    throw V(c.Name.Pos, c.Name.Value == p.Name.Value
                        ? $"procedure '{p.Name.Value}' calls itself: recursion is not allowed, so every script ends"
                        : $"procedure '{p.Name.Value}' calls '{c.Name.Value}', which leads back to '{p.Name.Value}': recursion is not allowed, so every script ends");
    }

    // ---- value rules for one simple statement with its values written out --------------------------------------------

    /// <summary>The value rules of a simple statement whose names and numbers are literals: ranges, step multiples, the past.</summary>
    public static void Validate(Statement s, Session session)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(session);
        switch (s)
        {
            case TripStatement t:
                RequireName(t.Noun, new Located<string>(t.Name.Value, t.Name.Pos), session);
                break;
            case CloseStatement c:
                RequireName(c.Noun, new Located<string>(c.Name.Value, c.Name.Pos), session);
                break;
            case SetStatement st:
                ValidateSetValue(st, session);
                break;
            case FaultStatement f:
                RequireName(Noun.Bus, new Located<string>(f.Bus.Value, f.Bus.Pos), session);
                break;
            case ClearFaultStatement cf:
                RequireName(Noun.Bus, new Located<string>(cf.Bus.Value, cf.Bus.Pos), session);
                break;
            case StepSimStatement ss:
            {
                long dur = RequireTime(Binder.Q(ss.Duration), "the step", allowZero: false);
                RequireWholeMultiple(dur, ss.Duration.Pos, session, "a step");
                break;
            }
            case AtStatement a:
            {
                long at = RequireTime(Binder.Q(a.Time), "the event time", allowZero: true);
                RequireWholeMultiple(at, a.Time.Pos, session, "an event time");
                if (at < session.ClockUs)
                    throw V(a.Time.Pos, $"the script clock is already at {Clock(session.ClockUs)}; an event cannot be scheduled in the past");
                Validate(a.Inner, session);
                break;
            }
        }
    }

    private static string Clock(long us) =>
        (us / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static long RequireTime(Located<Quantity> q, string what, bool allowZero)
    {
        if (q.Value.Unit != Unit.Ms && q.Value.Unit != Unit.S)
            throw V(q.Pos, $"unit mismatch: {what} is a time, expected MS or S, got {Units.Keyword(q.Value.Unit)}");
        if (!q.Value.TryToMicroseconds(out long us))
            throw V(q.Pos, $"{what} of {q.Value} is not a whole number of microseconds (or is negative or too large)");
        if (us == 0 && !allowZero) throw V(q.Pos, $"{what} must be above zero");
        return us;
    }

    private static void RequireWholeMultiple(long us, SourcePos pos, Session session, string what)
    {
        long step = session.StepUs;
        if (us % step != 0)
            throw V(pos, $"{what} of {Clock(us)} is not a whole multiple of the engine's integration step ({Clock(step)})");
    }

    internal static void RequireName(Noun noun, Located<string> name, Session session)
    {
        CaseInfo ci = session.Case ?? throw V(name.Pos, "no case is loaded; start with LOAD CASE \"path\"");
        bool ok = noun switch
        {
            Noun.Generator => ci.GeneratorIndex.ContainsKey(name.Value),
            Noun.Line => ci.LineIndex.ContainsKey(name.Value),
            Noun.Bus => ci.BusIndex.ContainsKey(name.Value),
            _ => ci.LoadIndex.ContainsKey(name.Value),
        };
        if (!ok)
            throw V(name.Pos, $"no {noun.ToString().ToLowerInvariant()} named \"{name.Value}\" in case \"{ci.Model.Name}\"; REPORT ELEMENTS lists the names");
    }

    private static void ValidateSetValue(SetStatement st, Session session)
    {
        RequireName(st.Noun, new Located<string>(st.Name.Value, st.Name.Pos), session);
        Located<Quantity> q = Binder.Q(st.Value);
        double v = (double)q.Value.Value;
        if (st.Noun == Noun.Load && st.Property.Value == Property.P && v < 0)
            throw V(q.Pos, "a load's P cannot be negative; use a generator for injection");
        if (st.Property.Value == Property.V)
        {
            double pu = v;
            if (q.Value.Unit == Unit.Kv)
            {
                var m = session.Case!.Model;
                int gi = session.Case.GeneratorIndex[st.Name.Value];
                double baseKv = m.Buses.First(b => b.Id == m.AllGenerators[gi].Bus).BaseKv;
                if (!(baseKv > 0)) throw V(q.Pos, $"generator \"{st.Name.Value}\" sits on a bus with no base voltage, so KV cannot be converted; use PU");
                pu = v / baseKv;
            }
            if (!(pu >= 0.5 && pu <= 1.5)) throw V(q.Pos, $"a voltage set-point of {pu.ToString("0.####", CultureInfo.InvariantCulture)} PU is outside 0.5 to 1.5 PU");
        }
    }
}

/// <summary>The settings clause of SELECT ENGINE, turned into typed engine settings. Every refusal names the setting.</summary>
public static class EngineSetup
{
    private static ScriptException V(SourcePos pos, string msg) => new(ErrorPhase.Validation, pos, msg);

    private static long StepUs(SelectEngineStatement se, long fallback)
    {
        if (se.Step is not { } s) return fallback;
        s.Value.TryToMicroseconds(out long us);
        return us;
    }

    private static decimal Num(SettingClause c, Unit unit, decimal min, decimal max)
    {
        if (c.Quantity is not { } q)
            throw V(c.Key.Pos, $"{c.Key.Value} takes a value with its unit, as in {c.Key.Value} 1 {Units.Keyword(unit)}");
        if (q.Value.Unit != unit)
            throw V(q.Pos, $"unit mismatch: expected {Units.Keyword(unit)}, got {Units.Keyword(q.Value.Unit)}");
        if (q.Value.Value < min || q.Value.Value > max)
            throw V(q.Pos, $"{c.Key.Value} of {q.Value} is outside {Quantity.Canonical(min)} to {Quantity.Canonical(max)} {Units.Keyword(unit)}");
        return q.Value.Value;
    }

    private static void RequireOnce(SelectEngineStatement se)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingClause c in se.Settings)
            if (!seen.Add(c.Key.Value)) throw V(c.Key.Pos, $"{c.Key.Value} is given twice");
    }

    public static LiveEngine.Options BuildLive(SelectEngineStatement se)
    {
        RequireOnce(se);
        var solver = SolverKind.Auto;
        bool assets = true;
        foreach (SettingClause c in se.Settings)
        {
            switch (c.Key.Value)
            {
                case "SOLVER":
                    if (c.Word is not { } w) throw V(c.Key.Pos, "SOLVER takes AUTO, DENSE, SPARSE or SWEEP");
                    solver = w.Value switch
                    {
                        "AUTO" => SolverKind.Auto,
                        "DENSE" => SolverKind.DenseNewton,
                        "SPARSE" => SolverKind.SparseNewton,
                        "SWEEP" => SolverKind.DistributionSweep,
                        _ => throw V(w.Pos, $"SOLVER takes AUTO, DENSE, SPARSE or SWEEP, got {w.Value}"),
                    };
                    break;
                case "ASSETS":
                    if (c.Word is not { } a || a.Value is not ("ON" or "OFF")) throw V(c.Key.Pos, "ASSETS takes ON or OFF");
                    assets = a.Value == "ON";
                    break;
                default:
                    throw V(c.Key.Pos, $"LIVE has no setting named {c.Key.Value}; its settings are SOLVER and ASSETS");
            }
        }
        return new LiveEngine.Options(StepUs(se, LiveEngine.DefaultStepUs), solver, assets);
    }

    public static ReplaySettings BuildReplay(SelectEngineStatement se)
    {
        RequireOnce(se);
        ReplayKind kind = se.Engine.Value switch
        {
            EngineKind.Rms => ReplayKind.Rms,
            EngineKind.Frequency => ReplayKind.Frequency,
            EngineKind.Emt => ReplayKind.Emt,
            _ => ReplayKind.Protection,
        };
        var r = new ReplaySettings(kind, StepUs(se, ReplaySettings.DefaultStepUs(kind))) { Model = se.Model?.Value ?? "" };
        string name = se.Engine.Value.ToString().ToUpperInvariant();
        foreach (SettingClause c in se.Settings)
        {
            switch (kind, c.Key.Value)
            {
                case (ReplayKind.Rms, "INERTIA") when r.Model == "CLASSICAL": r = r with { InertiaS = Num(c, Unit.S, 0.1m, 100m) }; break;
                case (ReplayKind.Rms, "DAMPING") when r.Model == "CLASSICAL": r = r with { DampingPu = Num(c, Unit.Pu, 0m, 100m) }; break;
                case (ReplayKind.Rms, "DYNAMICS") when r.Model != "CLASSICAL":
                    if (c.Name is not { } dn) throw V(c.Key.Pos, "DYNAMICS takes the dynamic data file in double quotes");
                    r = r with { DynamicsPath = dn.Value };
                    break;
                case (ReplayKind.Rms, "SCHEMAS") when r.Model != "CLASSICAL":
                    if (c.Name is not { } sn) throw V(c.Key.Pos, "SCHEMAS takes the folder of the v2 schemas in double quotes");
                    r = r with { SchemasDir = sn.Value };
                    break;
                case (ReplayKind.Emt, "PROBE") when r.Model == "THREE_PHASE":
                    if (c.Name is not { } pn) throw V(c.Key.Pos, "PROBE takes a bus name in double quotes");
                    r = r with { ProbeBus = pn.Value };
                    break;
                case (ReplayKind.Rms, "SLIP"): r = r with { SlipDeg = Num(c, Unit.Deg, 10m, 3600m) }; break;
                case (ReplayKind.Frequency or ReplayKind.Protection, "DROOP"): r = r with { DroopPu = Num(c, Unit.Pu, 0.001m, 0.5m) }; break;
                case (ReplayKind.Frequency or ReplayKind.Protection, "RESPONSIVE"): r = r with { ResponsivePu = Num(c, Unit.Pu, 0m, 1m) }; break;
                case (ReplayKind.Frequency or ReplayKind.Protection, "GOVERNOR"): r = r with { GovernorS = Num(c, Unit.S, 0.1m, 100m) }; break;
                case (ReplayKind.Emt, "SOURCE"):
                    if (c.Name is not { } n) throw V(c.Key.Pos, "SOURCE takes a bus name in double quotes");
                    r = r with { SourceBus = n.Value };
                    break;
                default:
                    throw V(c.Key.Pos, $"{name} has no setting named {c.Key.Value}; its settings are {Settings(kind)}");
            }
        }
        if (kind == ReplayKind.Rms && r.Model != "CLASSICAL" && r.DynamicsPath is null)
            throw V(se.Engine.Pos, $"RMS {r.Model} runs on machine dynamic data; give the file with WITH DYNAMICS \"file.json\"");
        return r;
    }

    private static string Settings(ReplayKind k) => k switch
    {
        ReplayKind.Rms => "SLIP, and for CLASSICAL INERTIA and DAMPING, for SUBTRANSIENT and GOVERNED DYNAMICS and SCHEMAS",
        ReplayKind.Emt => "SOURCE, and for THREE_PHASE PROBE",
        _ => "DROOP, RESPONSIVE and GOVERNOR",
    };
}
