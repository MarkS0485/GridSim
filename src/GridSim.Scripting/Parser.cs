using System.Collections.Immutable;
using System.Globalization;

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

    private Parser(IReadOnlyList<Token> tokens) { _t = tokens; }

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
            if (list[k] is GrammarStatement g)
                throw new ScriptException(ErrorPhase.Syntax, g.Pos, "GRAMMAR must be the first statement of a script");
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
                return new FaultStatement(v.Pos, ExpectNameRef("the bus name in double quotes"));
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
        while (Peek.Is("OR"))
        {
            Token t = Next();
            l = new OrCond(t.Pos, l, ParseAnd());
        }
        return l;
    }

    private Cond ParseAnd()
    {
        Cond l = ParseNot();
        while (Peek.Is("AND"))
        {
            Token t = Next();
            l = new AndCond(t.Pos, l, ParseNot());
        }
        return l;
    }

    private Cond ParseNot()
    {
        if (Peek.Is("NOT"))
        {
            Token t = Next();
            return new NotCond(t.Pos, ParseNot());
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
            if (s.Is("TRIPPED")) return new StatusCond(t.Pos, noun, r, InService: false);
            if (s.Is("IN"))
            {
                ExpectKeyword("SERVICE", "after IS IN");
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
                Cond inner = ParseCond();
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
        Token op = Next();
        if (op.Kind != TokenKind.Symbol || op.Text is not ("=" or "!=" or "<" or "<=" or ">" or ">="))
            throw Err(op, $"expected a comparison (=, !=, <, <=, >, >=), found {op.Describe()}");
        return new CompareCond(op.Pos, op.Text, left, ParseExpr());
    }

    // ---- expressions ---------------------------------------------------------------------------------------------------

    private Expr ParseExpr()
    {
        Expr l = ParseTerm();
        while (Peek.IsSymbol("+") || Peek.IsSymbol("-"))
        {
            Token op = Next();
            l = new BinExpr(op.Pos, op.Text[0], l, ParseTerm());
        }
        return l;
    }

    private Expr ParseTerm()
    {
        Expr l = ParseUnary();
        while (Peek.IsSymbol("*") || Peek.IsSymbol("/"))
        {
            Token op = Next();
            l = new BinExpr(op.Pos, op.Text[0], l, ParseUnary());
        }
        return l;
    }

    private Expr ParseUnary()
    {
        if (Peek.IsSymbol("-"))
        {
            Token m = Next();
            Expr operand = ParseUnary();
            // A minus straight before a number is part of the number, so -12 MVAR stays one literal.
            return operand is NumberExpr { Value: >= 0 } n ? n with { Pos = m.Pos, Value = -n.Value } : new NegExpr(m.Pos, operand);
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
                return new NumberExpr(t.Pos, value, unit);
            }
            case TokenKind.String:
                return new StringExpr(t.Pos, t.Text);
            case TokenKind.Symbol when t.Text == "(":
            {
                Expr e = ParseExpr();
                Token close = Next();
                if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close the bracket, found {close.Describe()}");
                return e;
            }
            case TokenKind.Word:
                return ParseWordValue(t);
            default:
                throw Err(t, $"expected a value (a number with its unit, a name, a reading), found {t.Describe()}");
        }
    }

    private Expr ParseWordValue(Token t)
    {
        foreach (SystemQuantity q in Enum.GetValues<SystemQuantity>())
            if (t.Is(q.ToString())) return new SystemExpr(t.Pos, q);
        if (t.Is("ABS") || t.Is("MIN") || t.Is("MAX"))
        {
            string fn = t.Text.ToUpperInvariant();
            Token open = Next();
            if (!open.IsSymbol("(")) throw Err(open, $"expected ( after {fn}, found {open.Describe()}");
            var args = ImmutableArray.CreateBuilder<Expr>();
            args.Add(ParseExpr());
            if (fn != "ABS")
            {
                Token comma = Next();
                if (!comma.IsSymbol(",")) throw Err(comma, $"{fn} takes two values; expected , found {comma.Describe()}");
                args.Add(ParseExpr());
            }
            Token close = Next();
            if (!close.IsSymbol(")")) throw Err(close, $"expected ) to close {fn}, found {close.Describe()}");
            return new FuncExpr(t.Pos, fn, args.ToImmutable());
        }
        if (Measures.TryParse(t.Text, out Measure m))
        {
            ExpectKeyword("OF", $"after {t.Text.ToUpperInvariant()}");
            Noun noun = ExpectNoun(withBus: true);
            return new MeasureExpr(t.Pos, m, noun, ExpectNameRef($"the {noun.ToString().ToLowerInvariant()} name in double quotes"));
        }
        if (Grammar.IsReserved(t.Text))
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
        if (t.Kind == TokenKind.Word && !Grammar.IsReserved(t.Text)) return new NameRef(t.Text, t.Pos, IsVariable: true);
        throw Err(t, $"expected {what} (or a name held in a value), found {t.Describe()}");
    }

    /// <summary>A bare word that is free to name a value, a loop variable, a procedure or a parameter.</summary>
    private Located<string> ExpectValueName(string what)
    {
        Token t = Next();
        if (t.Kind != TokenKind.Word) throw Err(t, $"expected {what}, found {t.Describe()}");
        if (Grammar.IsReserved(t.Text)) throw Err(t, $"'{t.Text}' is a keyword of the language and cannot be used as a name");
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
