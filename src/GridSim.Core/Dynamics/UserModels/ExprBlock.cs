using System.Globalization;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>One instruction of a compiled <c>expr</c> block (reverse Polish order).</summary>
internal enum ExprCode
{
    Input, Param, Literal, Add, Sub, Mul, Div, Neg, Min, Max, Abs, Sqrt, Exp, Clamp,
}

/// <summary>One instruction: the code, an index (input number, parameter number, or argument count) and a literal value.</summary>
internal readonly record struct ExprInstr(ExprCode Code, int Index, double Value);

/// <summary>
/// The compiled form of an <c>expr</c> block (RC5 WP12): the expression text parsed once into a reverse Polish instruction
/// array that is evaluated in that fixed order, so the arithmetic is the text's left-to-right reading and nothing else.
/// Grammar: numbers without an exponent, <c>in0</c> to <c>in7</c>, <c>@Param</c>, <c>+ - * /</c> (left-associative,
/// <c>* /</c> above <c>+ -</c>), unary minus, brackets, and the functions <c>min</c>, <c>max</c> (two or more arguments,
/// folded left to right), <c>abs</c>, <c>sqrt</c> (<see cref="Math.Sqrt"/>, correctly rounded everywhere), <c>exp</c>
/// (<see cref="DetMath.Exp"/>) and <c>clamp(x, lo, hi)</c> (<see cref="Math.Clamp(double, double, double)"/>). Nesting and
/// the evaluation stack are each capped at <see cref="MaxDepth"/>.
/// </summary>
internal sealed class ExprProgram
{
    /// <summary>The nesting and stack-depth cap.</summary>
    internal const int MaxDepth = 64;

    private ExprProgram(ExprInstr[] code, string[] parameters, int maxInput, int stack)
    {
        Code = code; Parameters = parameters; MaxInput = maxInput; StackDepth = stack;
    }

    /// <summary>The instructions, in evaluation order.</summary>
    internal ExprInstr[] Code { get; }

    /// <summary>The parameter names the text references, in first-appearance order (an instruction's index into this list).</summary>
    internal string[] Parameters { get; }

    /// <summary>The highest input number referenced, or -1.</summary>
    internal int MaxInput { get; }

    /// <summary>The largest evaluation stack the program needs.</summary>
    internal int StackDepth { get; }

    /// <summary>Parse <paramref name="text"/>; on failure the program is null and the error says where and why.</summary>
    internal static (ExprProgram? Program, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "the 'expr' text is empty.");
        try
        {
            var p = new Parser(text);
            p.Run();
            return (new ExprProgram(p.Out.ToArray(), p.Params.ToArray(), p.MaxInput, p.Stack), null);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Evaluate on the input values <paramref name="inputs"/> (block input k is <c>s[inputs[k]]</c>) and parameter values
    /// <paramref name="paramValues"/>, with <paramref name="stack"/> as scratch (at least <see cref="StackDepth"/> long).
    /// </summary>
    internal double Evaluate(double[] s, int[] inputs, double[] paramValues, double[] stack)
    {
        int sp = 0;
        foreach (ExprInstr i in Code)
        {
            switch (i.Code)
            {
                case ExprCode.Input: stack[sp++] = s[inputs[i.Index]]; break;
                case ExprCode.Param: stack[sp++] = paramValues[i.Index]; break;
                case ExprCode.Literal: stack[sp++] = i.Value; break;
                case ExprCode.Add: sp--; stack[sp - 1] = stack[sp - 1] + stack[sp]; break;
                case ExprCode.Sub: sp--; stack[sp - 1] = stack[sp - 1] - stack[sp]; break;
                case ExprCode.Mul: sp--; stack[sp - 1] = stack[sp - 1] * stack[sp]; break;
                case ExprCode.Div: sp--; stack[sp - 1] = stack[sp - 1] / stack[sp]; break;
                case ExprCode.Neg: stack[sp - 1] = -stack[sp - 1]; break;
                case ExprCode.Abs: stack[sp - 1] = Math.Abs(stack[sp - 1]); break;
                case ExprCode.Sqrt: stack[sp - 1] = Math.Sqrt(stack[sp - 1]); break;
                case ExprCode.Exp: stack[sp - 1] = DetMath.Exp(stack[sp - 1]); break;
                case ExprCode.Min:
                case ExprCode.Max:
                {
                    int first = sp - i.Index;
                    double acc = stack[first];
                    for (int k = first + 1; k < sp; k++)
                        acc = i.Code == ExprCode.Min ? Math.Min(acc, stack[k]) : Math.Max(acc, stack[k]);
                    sp = first;
                    stack[sp++] = acc;
                    break;
                }
                case ExprCode.Clamp:
                {
                    // lo > hi has no value: the result is NaN, which refuses the step like any other non-finite result.
                    double hi = stack[--sp], lo = stack[--sp];
                    if (!(lo <= hi)) { stack[sp - 1] = double.NaN; break; }
                    stack[sp - 1] = Math.Clamp(stack[sp - 1], lo, hi);
                    break;
                }
            }
        }
        return stack[0];
    }

    // ---- recursive-descent parser emitting reverse Polish ----------------------------------------------------------------

    private sealed class Parser(string text)
    {
        private int _pos;
        private int _depth;
        private int _sp;
        internal readonly List<ExprInstr> Out = new();
        internal readonly List<string> Params = new();
        internal int MaxInput = -1;
        internal int Stack;

        private FormatException Bad(string why) => new($"expr: {why} at position {_pos + 1} of \"{text}\".");

        internal void Run()
        {
            Expression();
            Skip();
            if (_pos < text.Length) throw Bad($"unexpected '{text[_pos]}'");
        }

        private void Emit(ExprInstr i, int pushes, int pops)
        {
            Out.Add(i);
            _sp += pushes - pops;
            if (_sp > Stack) Stack = _sp;
            if (Stack > MaxDepth) throw Bad($"the expression needs more than {MaxDepth} stack entries");
        }

        private void Skip()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\r' or '\n') _pos++;
        }

        private bool Accept(char c)
        {
            Skip();
            if (_pos < text.Length && text[_pos] == c) { _pos++; return true; }
            return false;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Bad($"the expression nests deeper than {MaxDepth}");
        }

        private void Expression()
        {
            Enter();
            Term();
            while (true)
            {
                if (Accept('+')) { Term(); Emit(new ExprInstr(ExprCode.Add, 0, 0), 1, 2); }
                else if (Accept('-')) { Term(); Emit(new ExprInstr(ExprCode.Sub, 0, 0), 1, 2); }
                else break;
            }
            _depth--;
        }

        private void Term()
        {
            Unary();
            while (true)
            {
                if (Accept('*')) { Unary(); Emit(new ExprInstr(ExprCode.Mul, 0, 0), 1, 2); }
                else if (Accept('/')) { Unary(); Emit(new ExprInstr(ExprCode.Div, 0, 0), 1, 2); }
                else break;
            }
        }

        private void Unary()
        {
            if (Accept('-'))
            {
                Enter();
                Unary();
                _depth--;
                Emit(new ExprInstr(ExprCode.Neg, 0, 0), 1, 1);
                return;
            }
            Primary();
        }

        private void Primary()
        {
            Skip();
            if (_pos >= text.Length) throw Bad("the expression ends early");
            char c = text[_pos];
            if (c == '(')
            {
                _pos++;
                Expression();
                if (!Accept(')')) throw Bad("a ')' is missing");
                return;
            }
            if (char.IsAsciiDigit(c) || c == '.')
            {
                int start = _pos;
                while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.')) _pos++;
                string num = text.Substring(start, _pos - start);
                if (_pos < text.Length && (text[_pos] is 'e' or 'E'))
                    throw Bad("a number with an exponent is not in the grammar; write it out");
                if (!double.TryParse(num, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                    throw Bad($"'{num}' is not a number");
                Emit(new ExprInstr(ExprCode.Literal, 0, v), 1, 0);
                return;
            }
            if (c == '@')
            {
                _pos++;
                string name = Identifier();
                if (name.Length == 0) throw Bad("'@' must be followed by a parameter name");
                int k = Params.IndexOf(name);
                if (k < 0) { Params.Add(name); k = Params.Count - 1; }
                Emit(new ExprInstr(ExprCode.Param, k, 0), 1, 0);
                return;
            }
            string id = Identifier();
            if (id.Length == 0) throw Bad($"unexpected '{c}'");
            if (id.Length == 3 && id[0] == 'i' && id[1] == 'n' && id[2] is >= '0' and <= '7')
            {
                int k = id[2] - '0';
                if (k > MaxInput) MaxInput = k;
                Emit(new ExprInstr(ExprCode.Input, k, 0), 1, 0);
                return;
            }
            (ExprCode code, int minArgs, int maxArgs) = id switch
            {
                "min" => (ExprCode.Min, 2, 8),
                "max" => (ExprCode.Max, 2, 8),
                "abs" => (ExprCode.Abs, 1, 1),
                "sqrt" => (ExprCode.Sqrt, 1, 1),
                "exp" => (ExprCode.Exp, 1, 1),
                "clamp" => (ExprCode.Clamp, 3, 3),
                _ => throw Bad($"'{id}' is not an input (in0 to in7) or a function (min, max, abs, sqrt, exp, clamp)"),
            };
            if (!Accept('(')) throw Bad($"'{id}' needs '(' and its arguments");
            int n = 0;
            do { Expression(); n++; } while (Accept(','));
            if (!Accept(')')) throw Bad($"'{id}(' is not closed");
            if (n < minArgs || n > maxArgs)
                throw Bad(minArgs == maxArgs ? $"{id} takes {minArgs} argument(s), got {n}" : $"{id} takes {minArgs} to {maxArgs} arguments, got {n}");
            Emit(new ExprInstr(code, n, 0), 1, code is ExprCode.Min or ExprCode.Max ? n : code == ExprCode.Clamp ? 3 : 1);
        }

        private string Identifier()
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            return text.Substring(start, _pos - start);
        }
    }
}
