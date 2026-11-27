using System.Collections.Immutable;
using System.Globalization;

namespace GridSim.Scripting;

public enum VKind { Scalar, Quantity, Element }

/// <summary>What a value is: a plain number, a number with a unit, or a model element of one kind. A unit is a type.</summary>
public readonly record struct VType(VKind Kind, Unit Unit = default, Noun Noun = default)
{
    public static VType Scalar { get; } = new(VKind.Scalar);
    public static VType Of(Unit u) => new(VKind.Quantity, u);
    public static VType Element(Noun n) => new(VKind.Element, default, n);

    public bool IsNumeric => Kind != VKind.Element;

    /// <summary>True when a value of <paramref name="other"/> can stand where this type is wanted (the same unit, or both times).</summary>
    public bool Accepts(VType other) => Kind == other.Kind && Kind switch
    {
        VKind.Scalar => true,
        VKind.Quantity => Units.Compatible(Unit, other.Unit),
        _ => Noun == other.Noun,
    };

    public string Describe() => Kind switch
    {
        VKind.Scalar => "a number without a unit",
        VKind.Quantity => Units.Keyword(Unit),
        _ => "a " + Noun.ToString().ToLowerInvariant(),
    };
}

/// <summary>A value at run time: a number (decimal, so a time written as 20 MS stays exact) with its type, or the name of a model element.</summary>
public readonly record struct Value(VType Type, decimal Number, string Name = "")
{
    public static Value Num(decimal v, Unit? u) => new(u is { } x ? VType.Of(x) : VType.Scalar, v);
    public static Value Elem(Noun n, string name) => new(VType.Element(n), 0m, name);

    public string Display() => Type.Kind switch
    {
        VKind.Scalar => Quantity.Canonical(Number),
        VKind.Quantity => Quantity.Canonical(Number) + " " + Units.Keyword(Type.Unit),
        _ => Type.Noun.ToString().ToUpperInvariant() + " " + Statement_Quote(Name),
    };

    private static string Statement_Quote(string n) => "\"" + n.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>The names in force while a statement is being checked, with their types.</summary>
public sealed class TypeScope(TypeScope? parent)
{
    private readonly Dictionary<string, VType> _v = new(StringComparer.Ordinal);

    public TypeScope? Parent { get; } = parent;

    public bool TryGet(string name, out VType t)
    {
        for (TypeScope? s = this; s is not null; s = s.Parent)
            if (s._v.TryGetValue(name, out t)) return true;
        t = default;
        return false;
    }

    public void Define(string name, VType t) => _v[name] = t;
}

/// <summary>The values in force while a statement runs. The root scope is the session's; a block or a call opens a child.</summary>
public sealed class Scope(Scope? parent, Dictionary<string, Value> vars)
{
    public Scope? Parent { get; } = parent;
    public Dictionary<string, Value> Vars { get; } = vars;

    public Scope Child() => new(this, new Dictionary<string, Value>(StringComparer.Ordinal));

    public bool TryGet(string name, out Value v)
    {
        for (Scope? s = this; s is not null; s = s.Parent)
            if (s.Vars.TryGetValue(name, out v)) return true;
        v = default;
        return false;
    }

    /// <summary>Change the value of an existing name, in the scope that holds it.</summary>
    public bool TryAssign(string name, Value v)
    {
        for (Scope? s = this; s is not null; s = s.Parent)
            if (s.Vars.ContainsKey(name)) { s.Vars[name] = v; return true; }
        return false;
    }
}

/// <summary>The static type of an expression, and the refusal of a unit mismatch (MW + MVAR and the like).</summary>
public static class Typer
{
    private static ScriptException V(SourcePos pos, string msg) => new(ErrorPhase.Validation, pos, msg);

    public static VType TypeOf(Expr e, TypeScope scope)
    {
        switch (e)
        {
            case NumberExpr n:
                return n.Unit is { } u ? VType.Of(u) : VType.Scalar;
            case StringExpr s:
                throw V(s.Pos, "a name in quotes is a value only as the argument of a CALL whose parameter is a model element");
            case VarExpr v:
                if (!scope.TryGet(v.Name, out VType t))
                    throw V(v.Pos, $"no value named '{v.Name}'; create it with LET {v.Name} = ... first");
                return t;
            case NegExpr ng:
            {
                VType o = Numeric(ng.Operand, scope, "negate");
                return o;
            }
            case BinExpr b:
                return TypeOfBin(b, scope);
            case MeasureExpr m:
            {
                if (!Measures.TryType(m.Measure, m.Noun, out Unit unit))
                    throw V(m.Pos, $"{Measures.Keyword(m.Measure)} does not apply to a {m.Noun.ToString().ToLowerInvariant()}; {Describe(m.Measure)}");
                if (m.Ref.IsVariable)
                {
                    if (!scope.TryGet(m.Ref.Value, out VType rt))
                        throw V(m.Ref.Pos, $"no value named '{m.Ref.Value}'");
                    if (rt.Kind != VKind.Element || rt.Noun != m.Noun)
                        throw V(m.Ref.Pos, $"'{m.Ref.Value}' holds {rt.Describe()}, not a {m.Noun.ToString().ToLowerInvariant()}");
                }
                return VType.Of(unit);
            }
            case SystemExpr s:
                return VType.Of(Measures.SystemUnit(s.Quantity));
            case StudyExpr st:
                if (st.Ref is { } sr) RefType(sr, st.Noun!.Value, scope);
                return StudyExtension.TypeOf(st.Reading);
            case FuncExpr f:
            {
                VType a = Numeric(f.Args[0], scope, f.Function);
                if (f.Function == "ABS") return a;
                VType b2 = Numeric(f.Args[1], scope, f.Function);
                if (!a.Accepts(b2))
                    throw V(f.Args[1].Pos, $"unit mismatch: {f.Function} needs both values in the same unit, got {a.Describe()} and {b2.Describe()}");
                return a;
            }
            default:
                throw V(e.Pos, "unknown expression");
        }
    }

    private static string Describe(Measure m) => m switch
    {
        Measure.P or Measure.Q => "P and Q read a GENERATOR or a LOAD",
        Measure.V => "V reads a GENERATOR",
        Measure.Vm or Measure.Va => "VM and VA read a BUS",
        _ => "PFROM, QFROM, LOADING and LOSS read a LINE",
    };

    private static VType Numeric(Expr e, TypeScope scope, string what)
    {
        VType t = TypeOf(e, scope);
        if (!t.IsNumeric) throw V(e.Pos, $"cannot {what} {t.Describe()}; a number or a quantity is needed");
        return t;
    }

    private static VType TypeOfBin(BinExpr b, TypeScope scope)
    {
        VType l = Numeric(b.Left, scope, "do arithmetic on");
        VType r = Numeric(b.Right, scope, "do arithmetic on");
        string op = b.Op.ToString();
        switch (b.Op)
        {
            case '+':
            case '-':
                if (!l.Accepts(r))
                    throw V(b.Pos, $"unit mismatch: cannot {(b.Op == '+' ? "add" : "subtract")} {r.Describe()} {(b.Op == '+' ? "to" : "from")} {l.Describe()}");
                return l;
            case '*':
                if (l.Kind == VKind.Scalar) return r;
                if (r.Kind == VKind.Scalar) return l;
                throw V(b.Pos, $"{l.Describe()} * {r.Describe()} is not a unit grammar 1 has; multiply a quantity by a plain number only");
            default:   // '/'
                if (r.Kind == VKind.Scalar) return l;
                if (l.Kind == VKind.Quantity && r.Kind == VKind.Quantity && Units.Compatible(l.Unit, r.Unit)) return VType.Scalar;
                throw V(b.Pos, $"{l.Describe()} {op} {r.Describe()} is not a unit grammar 1 has; divide a quantity by a plain number, or by the same unit to get a plain number");
        }
    }

    public static void CheckCond(Cond c, TypeScope scope, Action<Noun, NameRef> checkName)
    {
        switch (c)
        {
            case CompareCond cc:
            {
                VType l = Numeric(cc.Left, scope, "compare");
                VType r = Numeric(cc.Right, scope, "compare");
                if (!l.Accepts(r))
                    throw V(cc.Pos, $"unit mismatch: cannot compare {l.Describe()} with {r.Describe()}");
                CollectNames(cc.Left, checkName);
                CollectNames(cc.Right, checkName);
                break;
            }
            case StatusCond sc:
                RefType(sc.Ref, sc.Noun, scope);
                checkName(sc.Noun, sc.Ref);
                break;
            case AndCond a: CheckCond(a.Left, scope, checkName); CheckCond(a.Right, scope, checkName); break;
            case OrCond o: CheckCond(o.Left, scope, checkName); CheckCond(o.Right, scope, checkName); break;
            case NotCond n: CheckCond(n.Operand, scope, checkName); break;
            default: throw V(c.Pos, "unknown condition");
        }
    }

    /// <summary>Type-check an expression and check every literal name it reads.</summary>
    public static VType TypeOfChecked(Expr e, TypeScope scope, Action<Noun, NameRef> checkName)
    {
        VType t = TypeOf(e, scope);
        CollectNames(e, checkName);
        return t;
    }

    private static void RefType(NameRef r, Noun noun, TypeScope scope)
    {
        if (!r.IsVariable) return;
        if (!scope.TryGet(r.Value, out VType t)) throw V(r.Pos, $"no value named '{r.Value}'");
        if (t.Kind != VKind.Element || t.Noun != noun)
            throw V(r.Pos, $"'{r.Value}' holds {t.Describe()}, not a {noun.ToString().ToLowerInvariant()}");
    }

    private static void CollectNames(Expr e, Action<Noun, NameRef> checkName)
    {
        switch (e)
        {
            case MeasureExpr m: checkName(m.Noun, m.Ref); break;
            case StudyExpr { Ref: { } sr } st: checkName(st.Noun!.Value, sr); break;
            case NegExpr n: CollectNames(n.Operand, checkName); break;
            case BinExpr b: CollectNames(b.Left, checkName); CollectNames(b.Right, checkName); break;
            case FuncExpr f: foreach (Expr a in f.Args) CollectNames(a, checkName); break;
        }
    }

    /// <summary>The type of a name reference: a variable's, or a literal's (an element of the noun asked for).</summary>
    public static void CheckNameRef(NameRef r, Noun noun, TypeScope scope) => RefType(r, noun, scope);
}

/// <summary>Evaluation of expressions and conditions against the session's current state.</summary>
public static class Evaluator
{
    private static ScriptException X(SourcePos pos, string msg) => new(ErrorPhase.Execution, pos, msg);

    public static Value Eval(Expr e, Scope scope, Session s)
    {
        switch (e)
        {
            case NumberExpr n:
                return Value.Num(n.Value, n.Unit);
            case StringExpr:
                throw X(e.Pos, "a name in quotes is not a number");
            case VarExpr v:
                if (!scope.TryGet(v.Name, out Value val)) throw X(v.Pos, $"no value named '{v.Name}'");
                return val;
            case NegExpr ng:
            {
                Value o = Eval(ng.Operand, scope, s);
                return new Value(o.Type, -o.Number);
            }
            case BinExpr b:
                return EvalBin(b, scope, s);
            case MeasureExpr m:
            {
                string name = ResolveName(m.Ref, m.Noun, scope);
                return s.Reading(m.Measure, m.Noun, name, m.Pos);
            }
            case SystemExpr sy:
                return s.SystemReading(sy.Quantity, sy.Pos);
            case StudyExpr st:
                return s.StudyReadingValue(st, scope);
            case FuncExpr f:
            {
                Value a = Eval(f.Args[0], scope, s);
                if (f.Function == "ABS") return new Value(a.Type, Math.Abs(a.Number));
                Value c = Eval(f.Args[1], scope, s);
                decimal cn = c.Type.Kind == VKind.Quantity ? Units.Convert(c.Number, c.Type.Unit, a.Type.Unit) : c.Number;
                return new Value(a.Type, f.Function == "MIN" ? Math.Min(a.Number, cn) : Math.Max(a.Number, cn));
            }
            default:
                throw X(e.Pos, "unknown expression");
        }
    }

    private static Value EvalBin(BinExpr b, Scope scope, Session s)
    {
        Value l = Eval(b.Left, scope, s);
        Value r = Eval(b.Right, scope, s);
        try
        {
            switch (b.Op)
            {
                case '+':
                case '-':
                {
                    decimal rn = r.Type.Kind == VKind.Quantity ? Units.Convert(r.Number, r.Type.Unit, l.Type.Unit) : r.Number;
                    return new Value(l.Type, b.Op == '+' ? l.Number + rn : l.Number - rn);
                }
                case '*':
                    return l.Type.Kind == VKind.Scalar ? new Value(r.Type, l.Number * r.Number) : new Value(l.Type, l.Number * r.Number);
                default:
                {
                    if (r.Number == 0m) throw X(b.Pos, "division by zero");
                    if (r.Type.Kind == VKind.Scalar) return new Value(l.Type, l.Number / r.Number);
                    decimal rn = Units.Convert(r.Number, r.Type.Unit, l.Type.Unit);
                    if (rn == 0m) throw X(b.Pos, "division by zero");
                    return new Value(VType.Scalar, l.Number / rn);
                }
            }
        }
        catch (OverflowException)
        {
            throw X(b.Pos, "the result is out of range");
        }
    }

    public static string ResolveName(NameRef r, Noun noun, Scope scope)
    {
        if (!r.IsVariable) return r.Value;
        if (!scope.TryGet(r.Value, out Value v)) throw X(r.Pos, $"no value named '{r.Value}'");
        if (v.Type.Kind != VKind.Element || v.Type.Noun != noun)
            throw X(r.Pos, $"'{r.Value}' holds {v.Type.Describe()}, not a {noun.ToString().ToLowerInvariant()}");
        return v.Name;
    }

    /// <summary>Evaluate a condition; <paramref name="detail"/> lists what was compared, for the run record.</summary>
    public static bool EvalCond(Cond c, Scope scope, Session s, List<string> detail)
    {
        switch (c)
        {
            case CompareCond cc:
            {
                Value l = Eval(cc.Left, scope, s);
                Value r = Eval(cc.Right, scope, s);
                decimal rn = r.Type.Kind == VKind.Quantity ? Units.Convert(r.Number, r.Type.Unit, l.Type.Unit) : r.Number;
                bool res = cc.Op switch
                {
                    "=" => l.Number == rn,
                    "!=" => l.Number != rn,
                    "<" => l.Number < rn,
                    "<=" => l.Number <= rn,
                    ">" => l.Number > rn,
                    _ => l.Number >= rn,
                };
                detail.Add(l.Display() + " " + cc.Op + " " + r.Display() + " is " + (res ? "true" : "false"));
                return res;
            }
            case StatusCond sc:
            {
                string name = ResolveName(sc.Ref, sc.Noun, scope);
                bool inService = s.InService(sc.Noun, name);
                bool res = sc.InService ? inService : !inService;
                detail.Add(sc.Noun.ToString().ToUpperInvariant() + " \"" + name + "\" is " + (inService ? "in service" : "tripped"));
                return res;
            }
            case AndCond a:
                return EvalCond(a.Left, scope, s, detail) && EvalCond(a.Right, scope, s, detail);
            case OrCond o:
                return EvalCond(o.Left, scope, s, detail) || EvalCond(o.Right, scope, s, detail);
            case NotCond n:
                return !EvalCond(n.Operand, scope, s, detail);
            default:
                throw X(c.Pos, "unknown condition");
        }
    }
}

/// <summary>Turns a simple statement into the same statement with every name and value written out as a literal, using
/// the values in force when it runs. The bound form is what is validated, executed, scheduled and recorded.</summary>
public static class Binder
{
    public static Statement Bind(Statement st, Scope scope, Session s)
    {
        switch (st)
        {
            case TripStatement t:
                return t with { Name = Lit(t.Name, t.Noun, scope) };
            case CloseStatement c:
                return c with { Name = Lit(c.Name, c.Noun, scope) };
            case SetStatement set:
                return set with { Name = Lit(set.Name, set.Noun, scope), Value = Num(set.Value, scope, s) };
            case FaultStatement f:
                return f with { Bus = Lit(f.Bus, Noun.Bus, scope) };
            case ClearFaultStatement cf:
                return cf with { Bus = Lit(cf.Bus, Noun.Bus, scope) };
            case StudyStatement { Bus: { } sb } study:
                return study with { Bus = Lit(sb, Noun.Bus, scope) };
            case StepSimStatement ss:
                return ss with { Duration = Num(ss.Duration, scope, s) };
            case AtStatement a:
                return a with { Time = Num(a.Time, scope, s), Inner = Bind(a.Inner, scope, s) };
            default:
                return st;
        }
    }

    private static NameRef Lit(NameRef r, Noun noun, Scope scope) =>
        r.IsVariable ? new NameRef(Evaluator.ResolveName(r, noun, scope), r.Pos, IsVariable: false) : r;

    private static NumberExpr Num(Expr e, Scope scope, Session s)
    {
        if (e is NumberExpr n) return n;
        Value v = Evaluator.Eval(e, scope, s);
        if (v.Type.Kind == VKind.Element) throw new ScriptException(ErrorPhase.Execution, e.Pos, "a model element is not a number");
        return new NumberExpr(e.Pos, v.Number, v.Type.Kind == VKind.Quantity ? v.Type.Unit : null);
    }

    /// <summary>The quantity of a bound (literal) expression.</summary>
    public static Located<Quantity> Q(Expr e)
    {
        var n = (NumberExpr)e;
        if (n.Unit is not { } u)
            throw new ScriptException(ErrorPhase.Validation, n.Pos, "unit mismatch: a number with a unit is needed here, got a number without one");
        return new Located<Quantity>(new Quantity(n.Value, u), n.Pos);
    }
}
