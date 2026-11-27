using System.Collections.Immutable;
using System.Globalization;

namespace GridSim.Scripting;

/// <summary>A value with the place it was written, so a later stage can point at it.</summary>
public readonly record struct Located<T>(T Value, SourcePos Pos);

/// <summary>The things a verb can act on. TRIP, CLOSE and SET take Generator, Line and Load; Bus is for FAULT, FOR EACH and readings.</summary>
public enum Noun { Generator, Line, Load, Bus }

/// <summary>The quantity a SET changes: P active power, Q reactive power, V voltage setpoint.</summary>
public enum Property { P, Q, V }

public enum ReportKind { Buses, Branches, Elements, Sim }

/// <summary>The engines. Static is the steady-state default; Live is stepped exactly; the rest are whole-horizon engines
/// stepped by deterministic re-run from t = 0.</summary>
public enum EngineKind { Static, Live, Rms, Frequency, Emt, Protection }

/// <summary>A name written as a quoted string (a literal) or as a bare word (a variable holding a name).</summary>
public readonly record struct NameRef(string Value, SourcePos Pos, bool IsVariable)
{
    public string Canonical() => IsVariable ? Value : Statement.Quote(Value);
}

/// <summary>One statement of grammar v1. <see cref="Pos"/> is where its verb starts.</summary>
public abstract record Statement(SourcePos Pos)
{
    /// <summary>The statement in canonical form: upper-case keywords, one space between tokens, numbers without trailing
    /// zeros, names quoted. Parsing the canonical form gives back an equal statement. A block is several lines, its body
    /// indented by two spaces per level, closed by END.</summary>
    public abstract string Canonical();

    /// <summary>The first line of <see cref="Canonical"/>: the whole statement for everything except a block.</summary>
    public virtual string Header() => Canonical();

    public override string ToString() => Canonical();

    internal static string Quote(string name) => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    protected static string Word(Noun n) => n.ToString().ToUpperInvariant();

    protected static string Block(string header, ImmutableArray<Statement> body, string indent)
    {
        var sb = new System.Text.StringBuilder(header);
        foreach (Statement s in body)
            foreach (string line in s.Canonical().Split('\n'))
                sb.Append('\n').Append(indent).Append(line);
        return sb.ToString();
    }

    internal const string Indent = "  ";
}

public sealed record GrammarStatement(SourcePos Pos, Located<int> Version) : Statement(Pos)
{
    public override string Canonical() => "GRAMMAR " + Version.Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record LoadCaseStatement(SourcePos Pos, Located<string> Path, Located<Quantity>? Frequency) : Statement(Pos)
{
    public override string Canonical() =>
        "LOAD CASE " + Quote(Path.Value) + (Frequency is { } f ? " FREQUENCY " + f.Value : "");
}

public sealed record SolveStatement(SourcePos Pos) : Statement(Pos)
{
    public override string Canonical() => "SOLVE POWERFLOW";
}

public sealed record TripStatement(SourcePos Pos, Noun Noun, NameRef Name) : Statement(Pos)
{
    public override string Canonical() => "TRIP " + Word(Noun) + " " + Name.Canonical();
}

public sealed record CloseStatement(SourcePos Pos, Noun Noun, NameRef Name) : Statement(Pos)
{
    public override string Canonical() => "CLOSE " + Word(Noun) + " " + Name.Canonical();
}

public sealed record SetStatement(SourcePos Pos, Noun Noun, NameRef Name, Located<Property> Property, Expr Value) : Statement(Pos)
{
    public override string Canonical() => "SET " + Word(Noun) + " " + Name.Canonical() + " " + Property.Value + " " + Value.Canonical();
}

/// <summary>FAULT BUS "name": a bolted three-phase fault at a bus (RMS and EMT engines). WP2: an optional fault type
/// (THREE_PHASE, SLG, LL or LLG) and an optional fault impedance (WITH R n PU X n PU, system base) make it a sequence fault,
/// which the RMS SUBTRANSIENT and GOVERNED engines take (<c>RmsSequenceFault</c>). Without them the statement and its
/// canonical text are exactly the bolted fault's.</summary>
public sealed record FaultStatement(SourcePos Pos, NameRef Bus, string? FaultType = null, Quantity? FaultR = null, Quantity? FaultX = null) : Statement(Pos)
{
    public override string Canonical() => "FAULT BUS " + Bus.Canonical()
        + (FaultType is null ? "" : " " + FaultType)
        + (FaultR is null ? "" : " WITH R " + FaultR.Value + " X " + FaultX!.Value);
}

/// <summary>CLEAR FAULT BUS "name": the fault at that bus is cleared.</summary>
public sealed record ClearFaultStatement(SourcePos Pos, NameRef Bus) : Statement(Pos)
{
    public override string Canonical() => "CLEAR FAULT BUS " + Bus.Canonical();
}

public sealed record ReportStatement(SourcePos Pos, ReportKind Kind) : Statement(Pos)
{
    public override string Canonical() => "REPORT " + Kind.ToString().ToUpperInvariant();
}

public sealed record ExportResultStatement(SourcePos Pos, Located<string> Path) : Statement(Pos)
{
    public override string Canonical() => "EXPORT RESULT " + Quote(Path.Value);
}

/// <summary>One <c>KEY value</c> pair of a SELECT ENGINE ... WITH clause. The value is a quantity, a word (ON, OFF, a solver name) or a quoted name.</summary>
public sealed record SettingClause(Located<string> Key, Located<Quantity>? Quantity, Located<string>? Word, Located<string>? Name)
{
    public string Canonical() => Key.Value + " " + (Quantity is { } q ? q.Value.ToString() : Word is { } w ? w.Value : Statement.Quote(Name!.Value.Value));
}

public sealed record SelectEngineStatement(SourcePos Pos, Located<EngineKind> Engine, Located<string>? Model, Located<Quantity>? Step, ImmutableArray<SettingClause> Settings) : Statement(Pos)
{
    public override string Canonical() =>
        "SELECT ENGINE " + Engine.Value.ToString().ToUpperInvariant() + (Model is { } m ? " " + m.Value : "") + (Step is { } s ? " STEP " + s.Value : "")
        + (Settings.IsDefaultOrEmpty ? "" : " WITH " + string.Join(" ", Settings.Select(c => c.Canonical())));
}

public sealed record StepSimStatement(SourcePos Pos, Expr Duration) : Statement(Pos)
{
    public override string Canonical() => "STEP SIM " + Duration.Canonical();
}

/// <summary>AT t statement: the inner TRIP, CLOSE, SET, FAULT or CLEAR FAULT happens at script time t, at the start of the step that begins then.</summary>
public sealed record AtStatement(SourcePos Pos, Expr Time, Statement Inner) : Statement(Pos)
{
    public override string Canonical() => "AT " + Time.Canonical() + " " + Inner.Canonical();
}

/// <summary>LET name = expression: a new named value in the current scope.</summary>
public sealed record LetStatement(SourcePos Pos, Located<string> Name, Expr Value) : Statement(Pos)
{
    public override string Canonical() => "LET " + Name.Value + " = " + Value.Canonical();
}

/// <summary>SET name = expression: a new value for a name that already exists (same unit).</summary>
public sealed record AssignStatement(SourcePos Pos, Located<string> Name, Expr Value) : Statement(Pos)
{
    public override string Canonical() => "SET " + Name.Value + " = " + Value.Canonical();
}

/// <summary>PRINT expression: shows a computed value with its unit.</summary>
public sealed record PrintStatement(SourcePos Pos, Expr Value) : Statement(Pos)
{
    public override string Canonical() => "PRINT " + Value.Canonical();
}

/// <summary>IF condition ... [ELSE ...] END.</summary>
public sealed record IfStatement(SourcePos Pos, Cond Condition, ImmutableArray<Statement> Then, ImmutableArray<Statement>? Else) : Statement(Pos)
{
    public override string Header() => "IF " + Condition.Canonical();

    public override string Canonical()
    {
        string s = Block(Header(), Then, Indent);
        if (Else is { } e) s = Block(s + "\nELSE", e, Indent);
        return s + "\nEND";
    }
}

/// <summary>FOR EACH noun var [WHERE condition] ... END: the body runs once for each element of the collection, in model order.</summary>
public sealed record ForEachStatement(SourcePos Pos, Noun Noun, Located<string> Var, Cond? Where, ImmutableArray<Statement> Body) : Statement(Pos)
{
    public override string Header() => "FOR EACH " + Word(Noun) + " " + Var.Value + (Where is { } w ? " WHERE " + w.Canonical() : "");
    public override string Canonical() => Block(Header(), Body, Indent) + "\nEND";
}

/// <summary>One parameter of a PROC: its type (a noun, or a unit) and its name.</summary>
public sealed record ProcParam(Located<string> Name, Noun? Noun, Unit? Unit)
{
    public string TypeWord => Noun is { } n ? n.ToString().ToUpperInvariant() : Units.Keyword(Unit!.Value);
    public string Canonical() => TypeWord + " " + Name.Value;
}

/// <summary>PROC name [(type name, ...)] ... END: a named procedure. It may not call itself, directly or through another procedure.</summary>
public sealed record ProcStatement(SourcePos Pos, Located<string> Name, ImmutableArray<ProcParam> Params, ImmutableArray<Statement> Body) : Statement(Pos)
{
    public override string Header() => "PROC " + Name.Value + (Params.IsDefaultOrEmpty ? "" : "(" + string.Join(", ", Params.Select(p => p.Canonical())) + ")");
    public override string Canonical() => Block(Header(), Body, Indent) + "\nEND";
}

/// <summary>CALL name [(argument, ...)].</summary>
public sealed record CallStatement(SourcePos Pos, Located<string> Name, ImmutableArray<Expr> Args) : Statement(Pos)
{
    public override string Canonical() => "CALL " + Name.Value + (Args.IsDefaultOrEmpty ? "" : "(" + string.Join(", ", Args.Select(a => a.Canonical())) + ")");
}

// ---------------------------------------------------------------------------------------------------------------------
// Expressions and conditions

/// <summary>What a reading is: the set-point or demand of a unit (P, Q, V), or a solved result (VM, VA, PFROM, QFROM, LOADING, LOSS).</summary>
public enum Measure { P, Q, V, Vm, Va, PFrom, QFrom, Loading, Loss }

/// <summary>Whole-system readings that take no name.</summary>
public enum SystemQuantity { Time, Frequency, Losses, Generation, Demand }

/// <summary>An arithmetic expression. Numbers carry a unit (<c>50 MW</c>) or none (a plain count or factor).</summary>
public abstract record Expr(SourcePos Pos)
{
    public abstract string Canonical();

    /// <summary>Binding strength for parenthesising the canonical form: 1 for + and -, 2 for * and /, 3 for negation, 4 for an atom.</summary>
    internal virtual int Precedence => 4;

    internal string Wrap(Expr child, bool strict)
    {
        string c = child.Canonical();
        return child.Precedence < Precedence || (strict && child.Precedence == Precedence) ? "(" + c + ")" : c;
    }
}

public sealed record NumberExpr(SourcePos Pos, decimal Value, Unit? Unit) : Expr(Pos)
{
    public override string Canonical() => Quantity.Canonical(Value) + (Unit is { } u ? " " + Units.Keyword(u) : "");
    internal override int Precedence => Value < 0 ? 3 : 4;
}

/// <summary>A name in quotes used as a value: only valid as the argument of a CALL whose parameter is a model element.</summary>
public sealed record StringExpr(SourcePos Pos, string Text) : Expr(Pos)
{
    public override string Canonical() => Statement.Quote(Text);
}

public sealed record VarExpr(SourcePos Pos, string Name) : Expr(Pos)
{
    public override string Canonical() => Name;
}

public sealed record NegExpr(SourcePos Pos, Expr Operand) : Expr(Pos)
{
    public override string Canonical() => "-" + Wrap(Operand, strict: false);
    internal override int Precedence => 3;
}

/// <summary><see cref="Pos"/> is the operator's own position.</summary>
public sealed record BinExpr(SourcePos Pos, char Op, Expr Left, Expr Right) : Expr(Pos)
{
    internal override int Precedence => Op is '+' or '-' ? 1 : 2;
    public override string Canonical() => Wrap(Left, strict: false) + " " + Op + " " + Wrap(Right, strict: true);
}

/// <summary>A reading of a model element, as in <c>P OF GENERATOR "G1"</c> or <c>VM OF BUS b</c>.</summary>
public sealed record MeasureExpr(SourcePos Pos, Measure Measure, Noun Noun, NameRef Ref) : Expr(Pos)
{
    public override string Canonical() => Measures.Keyword(Measure) + " OF " + Word(Noun) + " " + Ref.Canonical();
    private static string Word(Noun n) => n.ToString().ToUpperInvariant();
}

public sealed record SystemExpr(SourcePos Pos, SystemQuantity Quantity) : Expr(Pos)
{
    public override string Canonical() => Quantity.ToString().ToUpperInvariant();
}

/// <summary>ABS(x), MIN(a, b) or MAX(a, b).</summary>
public sealed record FuncExpr(SourcePos Pos, string Function, ImmutableArray<Expr> Args) : Expr(Pos)
{
    public override string Canonical() => Function + "(" + string.Join(", ", Args.Select(a => a.Canonical())) + ")";
}

/// <summary>A condition: a comparison of two expressions, an element's status, or those joined by AND, OR and NOT.</summary>
public abstract record Cond(SourcePos Pos)
{
    public abstract string Canonical();
    internal virtual int Precedence => 4;

    internal string Wrap(Cond child) => child.Precedence < Precedence ? "(" + child.Canonical() + ")" : child.Canonical();
}

/// <summary><see cref="Pos"/> is the operator's own position. Op is one of = != &lt; &lt;= &gt; &gt;=.</summary>
public sealed record CompareCond(SourcePos Pos, string Op, Expr Left, Expr Right) : Cond(Pos)
{
    public override string Canonical() => Left.Canonical() + " " + Op + " " + Right.Canonical();
}

/// <summary>GENERATOR "G1" IS TRIPPED / IS IN SERVICE (also LINE and LOAD).</summary>
public sealed record StatusCond(SourcePos Pos, Noun Noun, NameRef Ref, bool InService) : Cond(Pos)
{
    public override string Canonical() => Noun.ToString().ToUpperInvariant() + " " + Ref.Canonical() + (InService ? " IS IN SERVICE" : " IS TRIPPED");
}

public sealed record AndCond(SourcePos Pos, Cond Left, Cond Right) : Cond(Pos)
{
    internal override int Precedence => 2;
    public override string Canonical() => Wrap(Left) + " AND " + Wrap(Right);
}

public sealed record OrCond(SourcePos Pos, Cond Left, Cond Right) : Cond(Pos)
{
    internal override int Precedence => 1;
    public override string Canonical() => Wrap(Left) + " OR " + Wrap(Right);
}

public sealed record NotCond(SourcePos Pos, Cond Operand) : Cond(Pos)
{
    internal override int Precedence => 3;
    public override string Canonical() => "NOT " + Wrap(Operand);
}

/// <summary>The spelling and typing of readings.</summary>
public static class Measures
{
    public static string Keyword(Measure m) => m switch
    {
        Measure.P => "P", Measure.Q => "Q", Measure.V => "V", Measure.Vm => "VM", Measure.Va => "VA",
        Measure.PFrom => "PFROM", Measure.QFrom => "QFROM", Measure.Loading => "LOADING", Measure.Loss => "LOSS",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    public static bool TryParse(string word, out Measure m)
    {
        foreach (Measure x in All)
            if (string.Equals(word, Keyword(x), StringComparison.OrdinalIgnoreCase)) { m = x; return true; }
        m = default;
        return false;
    }

    public static IReadOnlyList<Measure> All { get; } =
        [Measure.P, Measure.Q, Measure.V, Measure.Vm, Measure.Va, Measure.PFrom, Measure.QFrom, Measure.Loading, Measure.Loss];

    /// <summary>The unit of a reading, and the noun it applies to, or false for a pair grammar 1 does not have.</summary>
    public static bool TryType(Measure m, Noun noun, out Unit unit)
    {
        unit = default;
        switch (m)
        {
            case Measure.P when noun is Noun.Generator or Noun.Load: unit = Unit.Mw; return true;
            case Measure.Q when noun is Noun.Generator or Noun.Load: unit = Unit.Mvar; return true;
            case Measure.V when noun is Noun.Generator: unit = Unit.Pu; return true;
            case Measure.Vm when noun is Noun.Bus: unit = Unit.Pu; return true;
            case Measure.Va when noun is Noun.Bus: unit = Unit.Deg; return true;
            case Measure.PFrom when noun is Noun.Line: unit = Unit.Mw; return true;
            case Measure.QFrom when noun is Noun.Line: unit = Unit.Mvar; return true;
            case Measure.Loading when noun is Noun.Line: unit = Unit.Pu; return true;
            case Measure.Loss when noun is Noun.Line: unit = Unit.Mw; return true;
            default: return false;
        }
    }

    /// <summary>True for the readings that come from a solved network (VM, VA, PFROM, QFROM, LOADING, LOSS).</summary>
    public static bool IsSolved(Measure m) => m is Measure.Vm or Measure.Va or Measure.PFrom or Measure.QFrom or Measure.Loading or Measure.Loss;

    public static Unit SystemUnit(SystemQuantity q) => q switch
    {
        SystemQuantity.Time => Unit.Ms,
        SystemQuantity.Frequency => Unit.Hz,
        _ => Unit.Mw,
    };
}
