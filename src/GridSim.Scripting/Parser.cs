using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace GridSim.Scripting;

/// <summary>The grammar version this build implements, and the vocabulary of that grammar.</summary>
public static class Grammar
{
    /// <summary>Grammar version 1. A script may say <c>GRAMMAR 1</c> on its first statement; any other number is refused.</summary>
    public const int Version = 1;

    public const string VersionText = "1";

    /// <summary>File extension of a script.</summary>
    public const string Extension = ".gss";

    /// <summary>How deep blocks (FOR, IF, PROC bodies) may nest.</summary>
    public const int MaxBlockDepth = 8;

    /// <summary>How deep brackets, minus signs, NOT and function arguments may nest inside one expression or condition. A deeper
    /// one is refused as a syntax error at the token that goes past the limit, before anything runs.</summary>
    public const int MaxExpressionNesting = 64;

    /// <summary>How many operations deep one expression or condition may be (a chain <c>a + b + c</c> is two deep). A deeper one is
    /// refused as a syntax error at the operator that goes past the limit; split it with LET.</summary>
    public const int MaxExpressionDepth = 256;

    public static readonly IReadOnlyList<string> Verbs =
    [
        "GRAMMAR", "LOAD", "SOLVE", "TRIP", "CLOSE", "SET", "REPORT", "EXPORT", "SELECT", "STEP", "AT",
        "FAULT", "CLEAR", "FOR", "IF", "LET", "PROC", "CALL", "PRINT",
    ];

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRAMMAR", "LOAD", "SOLVE", "TRIP", "CLOSE", "SET", "REPORT", "EXPORT", "SELECT", "STEP", "AT", "FAULT", "CLEAR",
        "FOR", "IF", "LET", "PROC", "CALL", "PRINT", "ELSE", "END", "EACH", "WHERE", "OF", "IS", "IN", "SERVICE", "TRIPPED",
        "AND", "OR", "NOT", "WITH", "SIM", "ENGINE", "CASE", "FREQUENCY", "POWERFLOW", "RESULT", "GENERATOR", "LINE", "BUS",
        "BUSES", "BRANCHES", "ELEMENTS", "TIME", "LOSSES", "GENERATION", "DEMAND", "ABS", "MIN", "MAX", "ON", "OFF",
        "STATIC", "LIVE", "RMS", "EMT", "PROTECTION", "CLASSICAL", "SUBTRANSIENT", "GOVERNED", "SINGLE_PHASE", "THREE_PHASE", "LUMPED", "P", "Q", "V", "VM", "VA", "PFROM", "QFROM", "LOADING", "LOSS",
        "MW", "MVAR", "MVA", "PU", "KV", "HZ", "MS", "S", "DEG",
    };

    /// <summary>True for a word a value name may not use: a verb, a keyword, a noun, a reading, an engine or a unit.</summary>
    public static bool IsReserved(string word) => ReservedWords.Contains(word);

    /// <summary>RC5 WP11: the words of the STUDIES extension. They are keywords only in a script whose header is
    /// <c>GRAMMAR 1 WITH STUDIES</c>; everywhere else they are free names, exactly as in grammar 1.</summary>
    public static IReadOnlySet<string> ExtensionReserved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "STUDY", "STUDIES", "CONTINGENCY", "SHORTCIRCUIT", "OPF", "AS", "VIOLATIONS", "WORST", "OBJECTIVE", "IK", "KA",
    };
}

/// <summary>
/// Grammar v1 parser: tokens to statements, no model needed. One statement per line, except that FOR, IF and PROC open a
/// block of lines closed by END; the first error stops the parse and names its line and column. Nothing is executed until
/// the whole text parses.
/// </summary>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _t;
    private int _i;
    private int _depth;
    private int _nest;     // brackets, minus signs, NOT and function arguments open around the token being read
    private int _height;   // how many operations deep the expression or condition last returned is
    private bool _studies; // RC5 WP11: the script's header turned the STUDIES extension on

    private Parser(IReadOnlyList<Token> tokens) { _t = tokens; }

    /// <summary>A word a name may not use here: grammar 1's reserved words, and the extension's in a STUDIES script.</summary>
    private bool Reserved(string word) => Grammar.IsReserved(word) || (_studies && Grammar.ExtensionReserved.Contains(word));

    /// <summary>Parse a whole script. A syntax error anywhere refuses the whole script (nothing runs).</summary>
    public static IReadOnlyList<Statement> ParseScript(string text)
    {
        var p = new Parser(Lexer.Tokenize(text));
        var list = new List<Statement>();
        while (true)
        {
            while (p.Peek.Kind == TokenKind.Newline) p._i++;
            if (p.Peek.Kind == TokenKind.End) break;
            list.Add(p.ParseLine(inBlock: false));
        }
        for (int k = 1; k < list.Count; k++)
            if (list[k] is GrammarStatement or GrammarExtensionStatement)
                throw new ScriptException(ErrorPhase.Syntax, list[k].Pos, "GRAMMAR must be the first statement of a script");
        return list;
    }

    /// <summary>Parse exactly one single-line statement (a console line). Blank or comment-only text returns null. A line that
    /// opens a block fails with an <see cref="ScriptException.Unfinished"/> error; give the whole block to <see cref="ParseStatementText"/>.</summary>
    public static Statement? ParseLineText(string text, int lineNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\n', StringComparison.Ordinal))
            throw new ScriptException(ErrorPhase.Syntax, new SourcePos(lineNumber, 1), "one statement per line");
        return ParseStatementText(text, lineNumber);
    }

    /// <summary>Parse exactly one statement, which may be a block over several lines, numbering its first line
    /// <paramref name="firstLine"/>. Blank or comment-only text returns null.</summary>
    public static Statement? ParseStatementText(string text, int firstLine = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Lexer.Tokenize(text);
        var shifted = tokens.Select(t => t with { Pos = new SourcePos(t.Pos.Line + firstLine - 1, t.Pos.Column) }).ToList();
        var p = new Parser(shifted);
        while (p.Peek.Kind == TokenKind.Newline) p._i++;
        if (p.Peek.Kind == TokenKind.End) return null;
        Statement s = p.ParseLine(inBlock: false);
        while (p.Peek.Kind == TokenKind.Newline) p._i++;
        if (p.Peek.Kind != TokenKind.End)
            throw Err(p.Peek, "one statement at a time");
        return s;
    }

    private Token Peek => _t[_i];

    private Token PeekAt(int k) => _t[Math.Min(_i + k, _t.Count - 1)];

    private Token Next() => _t[_i++];

    private static ScriptException Err(Token at, string message) => new(ErrorPhase.Syntax, at.Pos, message);

    /// <summary>Open one level of expression nesting at <paramref name="at"/>; refused past <see cref="Grammar.MaxExpressionNesting"/>
    /// (or, as a second guard, when the thread's stack is nearly used up). Every call is paired with <see cref="Leave"/> in a finally.</summary>
    private void Enter(Token at)
    {
        if (_nest >= Grammar.MaxExpressionNesting || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw Err(at, $"the expression nests too deeply: grammar 1 allows at most {Grammar.MaxExpressionNesting} levels of brackets, minus signs, NOT and function arguments inside one another");
        _nest++;
    }

    private void Leave() => _nest--;

    /// <summary>Record how many operations deep the node being returned is; refused past <see cref="Grammar.MaxExpressionDepth"/>.</summary>
    private void Height(int h, Token at)
    {
        if (h > Grammar.MaxExpressionDepth)
            throw Err(at, $"the expression is too deep: grammar 1 allows at most {Grammar.MaxExpressionDepth} operations inside one another; split it with LET");
        _height = h;
    }

    private Statement ParseLine(bool inBlock)
    {
        Statement s = ParseStatement(allowAt: true, inBlock);
        if (Peek.Kind != TokenKind.Newline && Peek.Kind != TokenKind.End)
            throw Err(Peek, $"unexpected {Peek.Describe()}; the statement is complete, one statement per line");
        return s;
    }

    private static string VerbList => string.Join(", ", Grammar.Verbs);

    private static bool IsNounWord(Token t) =>
        t.Is("GENERATOR") || t.Is("LINE") || t.Is("LOAD") || t.Is("BUS");

    private Statement ParseStatement(bool allowAt, bool inBlock)
    {
        Token v = Next();
        if (v.Kind != TokenKind.Word)
            throw Err(v, $"expected a verb, found {v.Describe()}; verbs are {VerbList}");
        string verb = v.Text.ToUpperInvariant();
        if (inBlock && verb is "GRAMMAR" or "LOAD" or "SELECT" or "PROC")
            throw Err(v, $"{verb} cannot sit inside a block (FOR, IF or PROC); it changes the session itself, so it stands on its own line");
        switch (verb)
        {
            case "GRAMMAR":
            {
                Token n = Next();
                if (n.Kind != TokenKind.Number || n.Text.Contains('.', StringComparison.Ordinal))
                    throw Err(n, "expected the grammar version number, as in GRAMMAR 1");
                int ver = int.Parse(n.Text, CultureInfo.InvariantCulture);
                if (ver != Grammar.Version)
                    throw Err(n, $"this build implements grammar {Grammar.Version}, the script asks for grammar {ver}");
                if (Peek.Is("WITH"))
                {
                    // RC5 WP11: GRAMMAR 1 WITH STUDIES (grammar 1 refused this line as "unexpected WITH", so accepting it keeps the promise).
                    Next();
                    Token x = Next();
                    if (!x.Is(StudyExtension.Studies))
                        throw Err(x, $"expected the name of a grammar extension after WITH (the one extension is {StudyExtension.Studies}), found {x.Describe()}");
                    _studies = true;
                    return new GrammarExtensionStatement(v.Pos, new Located<int>(ver, n.Pos), [StudyExtension.Studies]);
                }
                return new GrammarStatement(v.Pos, new Located<int>(ver, n.Pos));
            }
            case "LOAD":
            {
                ExpectKeyword("CASE", "after LOAD");
                Located<string> path = ExpectString("the case path in double quotes");
                Located<Quantity>? freq = null;
                if (Peek.Is("FREQUENCY"))
                {
                    Next();
                    freq = ExpectQuantity("the nominal frequency, as in FREQUENCY 60 HZ");
                }
                return new LoadCaseStatement(v.Pos, path, freq);
            }
            case "SOLVE":
                ExpectKeyword("POWERFLOW", "after SOLVE");
                return new SolveStatement(v.Pos);
            case "TRIP":
            {
                Noun noun = ExpectNoun(withBus: false);
                return new TripStatement(v.Pos, noun, ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes"));
            }
            case "CLOSE":
            {
                Noun noun = ExpectNoun(withBus: false);
                return new CloseStatement(v.Pos, noun, ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes"));
            }
            case "SET":
            {
                if (Peek.Kind == TokenKind.Word && !IsNounWord(Peek) && PeekAt(1).IsSymbol("="))
                {
                    Located<string> target = ExpectValueName("the name of the value to change");
                    Next();   // =
                    return new AssignStatement(v.Pos, target, ParseExpr());
                }
                Noun noun = ExpectNoun(withBus: false);
                NameRef name = ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes");
                Token pt = Next();
                Property prop;
                if (pt.Is("P")) prop = Property.P;
                else if (pt.Is("Q")) prop = Property.Q;
                else if (pt.Is("V")) prop = Property.V;
                else throw Err(pt, $"expected a property (P, Q or V), found {pt.Describe()}");
                return new SetStatement(v.Pos, noun, name, new Located<Property>(prop, pt.Pos), ParseExpr());
            }
            case "FAULT":
            {
                ExpectKeyword("BUS", "after FAULT");
                NameRef faultBus = ExpectNameRef("the bus name in double quotes");
                // WP2: an optional fault type, then an optional impedance (WITH R n PU X n PU).
                string? faultType = null;
                foreach (string ft in new[] { "THREE_PHASE", "SLG", "LL", "LLG" })
                    if (Peek.Is(ft)) { Next(); faultType = ft; break; }
                Quantity? faultR = null, faultX = null;
                if (faultType is not null && Peek.Is("WITH"))
                {
                    Next();
                    ExpectKeyword("R", "after WITH (the fault resistance, as in WITH R 0 PU X 0.05 PU)");
                    Located<Quantity> r = ExpectQuantity("the fault resistance in PU");
                    if (r.Value.Unit != Unit.Pu) throw new ScriptException(ErrorPhase.Syntax, r.Pos, "the fault resistance is in PU (system base)");
                    ExpectKeyword("X", "after the fault resistance (the fault reactance, as in X 0.05 PU)");
                    Located<Quantity> x = ExpectQuantity("the fault reactance in PU");
                    if (x.Value.Unit != Unit.Pu) throw new ScriptException(ErrorPhase.Syntax, x.Pos, "the fault reactance is in PU (system base)");
                    faultR = r.Value; faultX = x.Value;
                }
                return new FaultStatement(v.Pos, faultBus, faultType, faultR, faultX);
            }
            case "CLEAR":
            {
                ExpectKeyword("FAULT", "after CLEAR");
                ExpectKeyword("BUS", "after CLEAR FAULT");
                return new ClearFaultStatement(v.Pos, ExpectNameRef("the bus name in double quotes"));
            }
            case "REPORT":
            {
                Token k = Next();
                ReportKind kind;
                if (k.Is("BUSES")) kind = ReportKind.Buses;
                else if (k.Is("BRANCHES")) kind = ReportKind.Branches;
                else if (k.Is("ELEMENTS")) kind = ReportKind.Elements;
                else if (k.Is("SIM")) kind = ReportKind.Sim;
                else if (_studies && k.Is("STUDY")) return new ReportStudyStatement(v.Pos, ExpectValueName("the study name, as in REPORT STUDY a"));
                else throw Err(k, $"expected BUSES, BRANCHES, ELEMENTS or SIM after REPORT, found {k.Describe()}");
                return new ReportStatement(v.Pos, kind);
            }
            case "EXPORT":
            {
                ExpectKeyword("RESULT", "after EXPORT");
                return new ExportResultStatement(v.Pos, ExpectString("the output file path in double quotes"));
            }
            case "SELECT":
                return ParseSelect(v);
            case "STEP":
            {
                ExpectKeyword("SIM", "after STEP");
                return new StepSimStatement(v.Pos, ParseExpr());
            }
            case "AT":
            {
                if (!allowAt) throw Err(v, "AT cannot be nested");
                Expr t = ParseExpr();
                Token inner = Peek;
                Statement s = ParseStatement(allowAt: false, inBlock);
                if (s is not (TripStatement or CloseStatement or SetStatement or FaultStatement or ClearFaultStatement))
                    throw Err(inner, "AT schedules TRIP, CLOSE, SET, FAULT or CLEAR FAULT only");
                return new AtStatement(v.Pos, t, s);
            }
            case "LET":
            {
                Located<string> name = ExpectValueName("a name for the value, as in LET load_limit = 80 MW");
                Token eq = Next();
                if (!eq.IsSymbol("=")) throw Err(eq, $"expected = after the name, found {eq.Describe()}");
                return new LetStatement(v.Pos, name, ParseExpr());
            }
            case "PRINT":
                return new PrintStatement(v.Pos, ParseExpr());
            case "IF":
                return ParseIf(v);
            case "FOR":
                return ParseFor(v);
            case "PROC":
                return ParseProc(v);
            case "CALL":
                return ParseCall(v);
            case "STUDY" when _studies:
                return ParseStudy(v);
            default:
                throw Err(v, $"unknown verb '{v.Text}'; verbs are {VerbList}");
        }
    }

    // ---- blocks --------------------------------------------------------------------------------------------------------

    private ImmutableArray<Statement> ParseBody(Token opener, string what, bool allowElse)
    {
        if (Peek.Kind != TokenKind.Newline)
            throw Err(Peek, $"unexpected {Peek.Describe()}; the body of {what} starts on the next line");
        if (++_depth > Grammar.MaxBlockDepth)
            throw Err(opener, $"blocks are nested deeper than {Grammar.MaxBlockDepth}");
        var list = ImmutableArray.CreateBuilder<Statement>();
        while (true)
        {
            while (Peek.Kind == TokenKind.Newline) _i++;
            if (Peek.Kind == TokenKind.End)
                throw new ScriptException(ErrorPhase.Syntax, Peek.Pos, $"unfinished block: {what} (line {opener.Pos.Line}) has no END", unfinished: true);
            if (Peek.Is("END") || (allowElse && Peek.Is("ELSE"))) break;
            list.Add(ParseLine(inBlock: true));
        }
        _depth--;
        return list.ToImmutable();
    }

    private Statement ParseIf(Token v)
    {
        Cond cond = ParseCond();
        ImmutableArray<Statement> then = ParseBody(v, "IF", allowElse: true);
        ImmutableArray<Statement>? els = null;
        if (Peek.Is("ELSE"))
        {
            Token e = Next();
            els = ParseBody(e, "ELSE", allowElse: false);
        }
        Next();   // END
        return new IfStatement(v.Pos, cond, then, els);
    }

    private Statement ParseFor(Token v)
    {
        ExpectKeyword("EACH", "after FOR");
        Noun noun = ExpectNoun(withBus: true);
        Located<string> var = ExpectValueName($"a name for the {noun.ToString().ToLowerInvariant()}, as in FOR EACH {noun.ToString().ToUpperInvariant()} g");
        Cond? where = null;
        if (Peek.Is("WHERE"))
        {
            Next();
            where = ParseCond();
        }
        ImmutableArray<Statement> body = ParseBody(v, "FOR EACH", allowElse: false);
        Next();   // END
        return new ForEachStatement(v.Pos, noun, var, where, body);
    }

    private Statement ParseProc(Token v)
    {
        Located<string> name = ExpectValueName("a name for the procedure, as in PROC trip_unit");
        var ps = ImmutableArray.CreateBuilder<ProcParam>();
        if (Peek.IsSymbol("("))
        {
            Next();
            if (!Peek.IsSymbol(")"))
            {
                while (true)
                {
                    Token ty = Next();
                    Noun? noun = null;
                    Unit? unit = null;
                    if (ty.Is("GENERATOR")) noun = Noun.Generator;
                    else if (ty.Is("LINE")) noun = Noun.Line;
                    else if (ty.Is("LOAD")) noun = Noun.Load;
                    else if (ty.Is("BUS")) noun = Noun.Bus;
                    else if (ty.Kind == TokenKind.Word && Units.TryParse(ty.Text, out Unit u)) unit = u;
                    else throw Err(ty, $"expected a parameter type (GENERATOR, LINE, LOAD, BUS or a unit), found {ty.Describe()}");
                    Located<string> pn = ExpectValueName("the parameter name");
                    ps.Add(new ProcParam(pn, noun, unit));
                    if (Peek.IsSymbol(",")) { Next(); continue; }
                    break;
                }
            }
            Token close = Next();
            if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close the parameter list, found {close.Describe()}");
        }
        for (int a = 0; a < ps.Count; a++)
            for (int b = a + 1; b < ps.Count; b++)
                if (ps[a].Name.Value == ps[b].Name.Value)
                    throw new ScriptException(ErrorPhase.Syntax, ps[b].Name.Pos, $"the parameter '{ps[b].Name.Value}' is named twice");
        ImmutableArray<Statement> body = ParseBody(v, "PROC", allowElse: false);
        Next();   // END
        return new ProcStatement(v.Pos, name, ps.ToImmutable(), body);
    }

    private Statement ParseCall(Token v)
    {
        Located<string> name = ExpectValueName("the procedure name");
        var args = ImmutableArray.CreateBuilder<Expr>();
        if (Peek.IsSymbol("("))
        {
            Next();
            if (!Peek.IsSymbol(")"))
            {
                while (true)
                {
                    args.Add(ParseExpr());
                    if (Peek.IsSymbol(",")) { Next(); continue; }
                    break;
                }
            }
            Token close = Next();
            if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close the argument list, found {close.Describe()}");
        }
        return new CallStatement(v.Pos, name, args.ToImmutable());
    }

    // ---- STUDY (RC5 WP11, STUDIES scripts only) -------------------------------------------------------------------------

    private Statement ParseStudy(Token v)
    {
        Token k = Next();
        StudyKind kind;
        NameRef? bus = null;
        if (k.Is("CONTINGENCY")) kind = StudyKind.Contingency;
        else if (k.Is("SHORTCIRCUIT"))
        {
            kind = StudyKind.ShortCircuit;
            ExpectKeyword("BUS", "after STUDY SHORTCIRCUIT");
            bus = ExpectNameRef("the bus name in double quotes");
        }
        else if (k.Is("OPF")) kind = StudyKind.Opf;
        // RC5 WP15: STUDY SCOPF (the word is not reserved, so a STUDIES script that used it as a name still parses).
        else if (k.Is(StudyExtension.ScopfWord)) kind = StudyKind.Scopf;
        // RC5 2.1.110: STUDY RMS and STUDY CCT, the dynamics-study rows (CCT and DYNAMICS are read in place).
        else if (k.Is("RMS") || k.Is(StudyExtension.CctWord)) return ParseDynamicStudy(v, k.Is("RMS") ? StudyKind.Rms : StudyKind.Cct);
        else throw Err(k, $"expected CONTINGENCY, SHORTCIRCUIT, OPF, SCOPF, RMS or CCT after STUDY, found {k.Describe()}");
        ExpectKeyword("AS", "before the study name, as in STUDY CONTINGENCY AS a");
        return new StudyStatement(v.Pos, kind, bus, ExpectValueName("a name for the study"));
    }

    /// <summary>STUDY RMS DYNAMICS "f.dyr" FAULT BUS "b" AT t CLEAR AT t FOR t AS name, and STUDY CCT DYNAMICS "f.dyr"
    /// FAULT BUS "b" AT t FOR t AS name. Times are in S or MS; the fault time is 0 or later, the clearing after it, the run
    /// length above 0.</summary>
    private StudyStatement ParseDynamicStudy(Token v, StudyKind kind)
    {
        string word = kind == StudyKind.Rms ? "STUDY RMS" : "STUDY CCT";
        ExpectKeyword(StudyExtension.DynamicsWord, $"after {word} (the DYR file, as in {word} DYNAMICS \"case.dyr\")");
        Located<string> dyr = ExpectString("the DYR file path in double quotes");
        ExpectKeyword("FAULT", "after the DYR file");
        ExpectKeyword("BUS", "after FAULT");
        NameRef bus = ExpectNameRef("the bus name in double quotes");
        ExpectKeyword("AT", "after the fault bus (the fault time, as in AT 0.1 S)");
        Located<Quantity> at = ExpectTime("the fault time");
        Located<Quantity>? clear = null;
        if (kind == StudyKind.Rms)
        {
            ExpectKeyword("CLEAR", "after the fault time (as in CLEAR AT 0.15 S)");
            ExpectKeyword("AT", "after CLEAR");
            clear = ExpectTime("the clearing time");
            if (Sec(clear.Value.Value) <= Sec(at.Value))
                throw new ScriptException(ErrorPhase.Syntax, clear.Value.Pos, "the fault must clear after it starts");
        }
        ExpectKeyword("FOR", "before the run length (as in FOR 3 S)");
        Located<Quantity> length = ExpectTime("the run length");
        if (Sec(length.Value) <= 0) throw new ScriptException(ErrorPhase.Syntax, length.Pos, "the run length must be above 0");
        ExpectKeyword("AS", $"before the study name, as in {word} ... AS r");
        return new StudyStatement(v.Pos, kind, bus, ExpectValueName("a name for the study"))
        {
            Dynamic = new DynamicStudyArgs(dyr, at.Value, clear?.Value, length.Value),
        };
    }

    private static decimal Sec(Quantity q) => q.Unit == Unit.Ms ? q.Value / 1000m : q.Value;

    private Located<Quantity> ExpectTime(string what)
    {
        Located<Quantity> q = ExpectQuantity(what + " in S or MS");
        if (q.Value.Unit is not (Unit.S or Unit.Ms)) throw new ScriptException(ErrorPhase.Syntax, q.Pos, $"{what} is a time, in S or MS");
        if (q.Value.Value < 0) throw new ScriptException(ErrorPhase.Syntax, q.Pos, $"{what} cannot be negative");
        return q;
    }

    /// <summary>A study reading that starts with <paramref name="t"/>, or null when <paramref name="t"/> starts none.</summary>
    private Expr? ParseStudyReading(Token t)
    {
        StudyReading r;
        if (t.Is("VIOLATIONS")) r = StudyReading.Violations;
        else if (t.Is("OBJECTIVE")) r = StudyReading.Objective;
        // RC5 WP15: SECURED OF STUDY s (only with OF STUDY after it, so a value named secured is still a value).
        else if (t.Is(StudyExtension.SecuredWord) && Peek.Is("OF") && PeekAt(1).Is("STUDY")) r = StudyReading.Secured;
        // RC5 2.1.110: STABLE / CCT OF STUDY s and FINAL FREQUENCY OF STUDY s, read in place the same way.
        else if (t.Is(StudyExtension.StableWord) && Peek.Is("OF") && PeekAt(1).Is("STUDY")) r = StudyReading.Stable;
        else if (t.Is(StudyExtension.CctWord) && Peek.Is("OF") && PeekAt(1).Is("STUDY")) r = StudyReading.Cct;
        else if (t.Is(StudyExtension.FinalWord) && Peek.Is("FREQUENCY") && PeekAt(1).Is("OF") && PeekAt(2).Is("STUDY"))
        {
            Next();
            r = StudyReading.FinalFrequency;
        }
        // MIN VOLTAGE OF BUS b IN STUDY s (MIN followed by VOLTAGE, so the MIN function is untouched).
        else if (t.Is("MIN") && Peek.Is(StudyExtension.VoltageWord) && PeekAt(1).Is("OF"))
        {
            Next();
            ExpectKeyword("OF", "after MIN VOLTAGE");
            ExpectKeyword("BUS", "after MIN VOLTAGE OF");
            NameRef b = ExpectNameRef("the bus name in double quotes");
            ExpectKeyword("IN", "after the bus name");
            ExpectKeyword("STUDY", "after IN");
            _height = 0;
            return new StudyExpr(t.Pos, StudyReading.MinVoltage, Noun.Bus, b, ExpectValueName("the study name"));
        }
        else if (t.Is("WORST"))
        {
            ExpectKeyword("LOADING", "after WORST");
            r = StudyReading.WorstLoading;
        }
        else if (t.Is("IK"))
        {
            ExpectKeyword("OF", "after IK");
            ExpectKeyword("BUS", "after IK OF");
            NameRef b = ExpectNameRef("the bus name in double quotes");
            ExpectKeyword("IN", "after the bus name");
            ExpectKeyword("STUDY", "after IN");
            _height = 0;
            return new StudyExpr(t.Pos, StudyReading.Ik, Noun.Bus, b, ExpectValueName("the study name"));
        }
        else return null;
        ExpectKeyword("OF", $"after {t.Text.ToUpperInvariant()}{(r == StudyReading.WorstLoading ? " LOADING" : "")}");
        ExpectKeyword("STUDY", "after OF");
        _height = 0;
        return new StudyExpr(t.Pos, r, null, null, ExpectValueName("the study name"));
    }

    // ---- SELECT ENGINE -------------------------------------------------------------------------------------------------

    private Statement ParseSelect(Token v)
    {
        ExpectKeyword("ENGINE", "after SELECT");
        Token e = Next();
        EngineKind kind;
        if (e.Is("LIVE")) kind = EngineKind.Live;
        else if (e.Is("STATIC")) kind = EngineKind.Static;
        else if (e.Is("RMS")) kind = EngineKind.Rms;
        else if (e.Is("FREQUENCY")) kind = EngineKind.Frequency;
        else if (e.Is("EMT")) kind = EngineKind.Emt;
        else if (e.Is("PROTECTION")) kind = EngineKind.Protection;
        else throw Err(e, $"expected STATIC, LIVE, RMS, FREQUENCY, EMT or PROTECTION after SELECT ENGINE, found {e.Describe()}");
        // A model is required for every engine that has more than one possible model, so a later model is an addition.
        string[] models = kind switch
        {
            EngineKind.Rms => ["CLASSICAL", "SUBTRANSIENT", "GOVERNED"],
            EngineKind.Emt => ["SINGLE_PHASE", "THREE_PHASE"],
            EngineKind.Protection => ["FREQUENCY"],
            EngineKind.Frequency => ["LUMPED"],
            _ => [],
        };
        Located<string>? model = null;
        if (models.Length > 0)
        {
            Token m = Next();
            if (m.Kind != TokenKind.Word || !models.Contains(m.Text, StringComparer.OrdinalIgnoreCase))
                throw Err(m, $"{e.Text.ToUpperInvariant()} needs a model: SELECT ENGINE {e.Text.ToUpperInvariant()} {string.Join(" | ", models)}; found {m.Describe()}");
            model = new Located<string>(m.Text.ToUpperInvariant(), m.Pos);
        }
        Located<Quantity>? step = null;
        if (Peek.Is("STEP"))
        {
            Next();
            step = ExpectQuantity("the integration step, as in STEP 20 MS");
        }
        var settings = ImmutableArray.CreateBuilder<SettingClause>();
        if (Peek.Is("WITH"))
        {
            Next();
            if (Peek.Kind != TokenKind.Word) throw Err(Peek, $"expected a setting name after WITH, found {Peek.Describe()}");
            while (Peek.Kind == TokenKind.Word)
            {
                Token key = Next();
                var k = new Located<string>(key.Text.ToUpperInvariant(), key.Pos);
                Token val = Peek;
                if (val.Kind == TokenKind.Number)
                    settings.Add(new SettingClause(k, ExpectQuantity($"a value with its unit after {k.Value}"), null, null));
                else if (val.Kind == TokenKind.String)
                {
                    Next();
                    settings.Add(new SettingClause(k, null, null, new Located<string>(val.Text, val.Pos)));
                }
                else if (val.Kind == TokenKind.Word)
                {
                    Next();
                    settings.Add(new SettingClause(k, null, new Located<string>(val.Text.ToUpperInvariant(), val.Pos), null));
                }
                else throw Err(val, $"expected a value after {k.Value}, found {val.Describe()}");
            }
        }
        return new SelectEngineStatement(v.Pos, new Located<EngineKind>(kind, e.Pos), model, step, settings.ToImmutable());
    }

    // ---- conditions ----------------------------------------------------------------------------------------------------

    private Cond ParseCond()
    {
        Cond l = ParseAnd();
        int h = _height;
        while (Peek.Is("OR"))
        {
            Token t = Next();
            Cond r = ParseAnd();
            h = Math.Max(h, _height) + 1;
            Height(h, t);
            l = new OrCond(t.Pos, l, r);
        }
        _height = h;
        return l;
    }

    private Cond ParseAnd()
    {
        Cond l = ParseNot();
        int h = _height;
        while (Peek.Is("AND"))
        {
            Token t = Next();
            Cond r = ParseNot();
            h = Math.Max(h, _height) + 1;
            Height(h, t);
            l = new AndCond(t.Pos, l, r);
        }
        _height = h;
        return l;
    }

    private Cond ParseNot()
    {
        if (Peek.Is("NOT"))
        {
            Token t = Next();
            Cond operand;
            Enter(t);
            try { operand = ParseNot(); }
            finally { Leave(); }
            Height(_height + 1, t);
            return new NotCond(t.Pos, operand);
        }
        return ParseAtom();
    }

    private static readonly HashSet<string> ContinuationSymbols = ["+", "-", "*", "/", "<", ">", "<=", ">=", "=", "!="];

    private Cond ParseAtom()
    {
        Token t = Peek;
        if (t.Is("GENERATOR") || t.Is("LINE") || t.Is("LOAD"))
        {
            Next();
            Noun noun = t.Is("GENERATOR") ? Noun.Generator : t.Is("LINE") ? Noun.Line : Noun.Load;
            NameRef r = ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes");
            ExpectKeyword("IS", "after the name");
            Token s = Next();
            if (s.Is("TRIPPED")) { _height = 0; return new StatusCond(t.Pos, noun, r, InService: false); }
            if (s.Is("IN"))
            {
                ExpectKeyword("SERVICE", "after IS IN");
                _height = 0;
                return new StatusCond(t.Pos, noun, r, InService: true);
            }
            throw Err(s, $"expected TRIPPED or IN SERVICE after IS, found {s.Describe()}");
        }
        if (t.IsSymbol("("))
        {
            int save = _i;
            try
            {
                Next();
                Cond inner;
                Enter(t);
                try { inner = ParseCond(); }
                finally { Leave(); }
                if (Peek.IsSymbol(")"))
                {
                    Next();
                    if (!(Peek.Kind == TokenKind.Symbol && ContinuationSymbols.Contains(Peek.Text))) return inner;
                }
            }
            catch (ScriptException) { /* not a parenthesised condition: read it as a parenthesised value */ }
            _i = save;
        }
        Expr left = ParseExpr();
        int hl = _height;
        Token op = Next();
        if (op.Kind != TokenKind.Symbol || op.Text is not ("=" or "!=" or "<" or "<=" or ">" or ">="))
            throw Err(op, $"expected a comparison (=, !=, <, <=, >, >=), found {op.Describe()}");
        Expr right = ParseExpr();
        Height(Math.Max(hl, _height) + 1, op);
        return new CompareCond(op.Pos, op.Text, left, right);
    }

    // ---- expressions ---------------------------------------------------------------------------------------------------

    private Expr ParseExpr()
    {
        Expr l = ParseTerm();
        int h = _height;
        while (Peek.IsSymbol("+") || Peek.IsSymbol("-"))
        {
            Token op = Next();
            Expr r = ParseTerm();
            h = Math.Max(h, _height) + 1;
            Height(h, op);
            l = new BinExpr(op.Pos, op.Text[0], l, r);
        }
        _height = h;
        return l;
    }

    private Expr ParseTerm()
    {
        Expr l = ParseUnary();
        int h = _height;
        while (Peek.IsSymbol("*") || Peek.IsSymbol("/"))
        {
            Token op = Next();
            Expr r = ParseUnary();
            h = Math.Max(h, _height) + 1;
            Height(h, op);
            l = new BinExpr(op.Pos, op.Text[0], l, r);
        }
        _height = h;
        return l;
    }

    private Expr ParseUnary()
    {
        if (Peek.IsSymbol("-"))
        {
            Token m = Next();
            Expr operand;
            Enter(m);
            try { operand = ParseUnary(); }
            finally { Leave(); }
            // A minus straight before a number is part of the number, so -12 MVAR stays one literal.
            if (operand is NumberExpr { Value: >= 0 } n) return n with { Pos = m.Pos, Value = -n.Value };
            Height(_height + 1, m);
            return new NegExpr(m.Pos, operand);
        }
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        Token t = Next();
        switch (t.Kind)
        {
            case TokenKind.Number:
            {
                if (!decimal.TryParse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                    throw Err(t, $"the number {t.Text} is out of range");
                Unit? unit = null;
                if (Peek.Kind == TokenKind.Word)
                {
                    if (Units.TryParse(Peek.Text, out Unit u)) { unit = u; Next(); }
                    else if (!(Peek.Is("AND") || Peek.Is("OR")))
                        throw Err(Peek, $"expected a unit ({string.Join(", ", Units.All.Select(Units.Keyword))}) after {t.Text}, found {Peek.Describe()}");
                }
                _height = 0;
                return new NumberExpr(t.Pos, value, unit);
            }
            case TokenKind.String:
                _height = 0;
                return new StringExpr(t.Pos, t.Text);
            case TokenKind.Symbol when t.Text == "(":
            {
                Enter(t);
                try
                {
                    Expr e = ParseExpr();
                    Token close = Next();
                    if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close the bracket, found {close.Describe()}");
                    return e;
                }
                finally { Leave(); }
            }
            case TokenKind.Word:
                return ParseWordValue(t);
            default:
                throw Err(t, $"expected a value (a number with its unit, a name, a reading), found {t.Describe()}");
        }
    }

    private Expr ParseWordValue(Token t)
    {
        _height = 0;
        foreach (SystemQuantity q in Enum.GetValues<SystemQuantity>())
            if (t.Is(q.ToString())) return new SystemExpr(t.Pos, q);
        // RC5 2.1.110: MIN VOLTAGE OF BUS b IN STUDY s comes before the MIN function (which takes a bracket).
        if (_studies && t.Is("MIN") && Peek.Is(StudyExtension.VoltageWord) && ParseStudyReading(t) is { } minVoltage) return minVoltage;
        if (t.Is("ABS") || t.Is("MIN") || t.Is("MAX"))
        {
            string fn = t.Text.ToUpperInvariant();
            Token open = Next();
            if (!open.IsSymbol("(")) throw Err(open, $"expected ( after {fn}, found {open.Describe()}");
            var args = ImmutableArray.CreateBuilder<Expr>();
            int h;
            Enter(open);
            try
            {
                args.Add(ParseExpr());
                h = _height;
                if (fn != "ABS")
                {
                    Token comma = Next();
                    if (!comma.IsSymbol(",")) throw Err(comma, $"{fn} takes two values; expected , found {comma.Describe()}");
                    args.Add(ParseExpr());
                    h = Math.Max(h, _height);
                }
            }
            finally { Leave(); }
            Token close = Next();
            if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close {fn}, found {close.Describe()}");
            Height(h + 1, t);
            return new FuncExpr(t.Pos, fn, args.ToImmutable());
        }
        if (_studies && ParseStudyReading(t) is { } reading) return reading;
        if (Measures.TryParse(t.Text, out Measure m))
        {
            ExpectKeyword("OF", $"after {t.Text.ToUpperInvariant()}");
            Noun noun = ExpectNoun(withBus: true);
            NameRef r = ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes");
            _height = 0;
            // RC5 WP11 (STUDIES scripts only): P OF GENERATOR g IN STUDY s reads the generator's output in an OPF study.
            if (_studies && m == Measure.P && noun == Noun.Generator && Peek.Is("IN") && PeekAt(1).Is("STUDY"))
            {
                Next();
                Next();
                return new StudyExpr(t.Pos, StudyReading.P, Noun.Generator, r, ExpectValueName("the study name"));
            }
            return new MeasureExpr(t.Pos, m, noun, r);
        }
        if (Reserved(t.Text))
            throw Err(t, $"expected a value (a number with its unit, a name, a reading), found {t.Describe()}");
        return new VarExpr(t.Pos, t.Text);
    }

    // ---- small pieces --------------------------------------------------------------------------------------------------

    private void ExpectKeyword(string kw, string where)
    {
        Token t = Next();
        if (!t.Is(kw)) throw Err(t, $"expected {kw} {where}, found {t.Describe()}");
    }

    private Noun ExpectNoun(bool withBus)
    {
        Token t = Next();
        if (t.Is("GENERATOR")) return Noun.Generator;
        if (t.Is("LINE")) return Noun.Line;
        if (t.Is("LOAD")) return Noun.Load;
        if (withBus && t.Is("BUS")) return Noun.Bus;
        throw Err(t, withBus
            ? $"expected GENERATOR, LINE, LOAD or BUS, found {t.Describe()}"
            : $"expected GENERATOR, LINE or LOAD, found {t.Describe()}");
    }

    private Located<string> ExpectString(string what)
    {
        Token t = Next();
        if (t.Kind != TokenKind.String) throw Err(t, $"expected {what}, found {t.Describe()}");
        return new Located<string>(t.Text, t.Pos);
    }

    /// <summary>A name in double quotes, or a bare word naming a value that holds one.</summary>
    private NameRef ExpectNameRef(string what)
    {
        Token t = Next();
        if (t.Kind == TokenKind.String) return new NameRef(t.Text, t.Pos, IsVariable: false);
        if (t.Kind == TokenKind.Word && !Reserved(t.Text)) return new NameRef(t.Text, t.Pos, IsVariable: true);
        throw Err(t, $"expected {what} (or a name held in a value), found {t.Describe()}");
    }

    /// <summary>A bare word that is free to name a value, a loop variable, a procedure or a parameter.</summary>
    private Located<string> ExpectValueName(string what)
    {
        Token t = Next();
        if (t.Kind != TokenKind.Word) throw Err(t, $"expected {what}, found {t.Describe()}");
        if (Reserved(t.Text)) throw Err(t, $"'{t.Text}' is a keyword of the language and cannot be used as a name");
        return new Located<string>(t.Text, t.Pos);
    }

    private Located<Quantity> ExpectQuantity(string what)
    {
        bool negative = false;
        if (Peek.IsSymbol("-")) { Next(); negative = true; }
        Token n = Next();
        if (n.Kind != TokenKind.Number) throw Err(n, $"expected {what}, found {n.Describe()}");
        Token u = Next();
        if (u.Kind != TokenKind.Word || !Units.TryParse(u.Text, out Unit unit))
            throw Err(u, $"expected a unit ({string.Join(", ", Units.All.Select(Units.Keyword))}) after {n.Text}, found {u.Describe()}");
        if (!decimal.TryParse(n.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
            throw Err(n, $"the number {n.Text} is out of range");
        return new Located<Quantity>(new Quantity(negative ? -value : value, unit), n.Pos);
    }
}
