namespace GridSim.Scripting;

/// <summary>Where in the script an error was found. Line and column are 1-based; a column counts UTF-16 characters.</summary>
public readonly record struct SourcePos(int Line, int Column)
{
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>The stage that refused a statement.</summary>
public enum ErrorPhase
{
    /// <summary>The text is not grammar (lexer or parser).</summary>
    Syntax,
    /// <summary>The text parses but does not fit the loaded model: unknown name, wrong verb/noun pair, wrong unit.</summary>
    Validation,
    /// <summary>The statement is valid but the state or the engine refused it (already tripped, no convergence, file error).</summary>
    Execution,
}

/// <summary>A script error. It always names the line and the column, and says which stage refused the statement.</summary>
public sealed class ScriptException : Exception
{
    public ScriptException(ErrorPhase phase, SourcePos pos, string message, bool unfinished = false) : base(message)
    {
        Phase = phase;
        Pos = pos;
        Unfinished = unfinished;
    }

    public ScriptException() : this(ErrorPhase.Execution, default, "script error") { }
    public ScriptException(string message) : this(ErrorPhase.Execution, default, message) { }
    public ScriptException(string message, Exception inner) : base(message, inner) { Phase = ErrorPhase.Execution; }

    public ErrorPhase Phase { get; }
    public SourcePos Pos { get; }

    /// <summary>True for a syntax error that only means the text stops before a block's END: a console keeps reading lines.</summary>
    public bool Unfinished { get; }

    /// <summary>The message a person reads: <c>line 3, column 9: syntax error: ...</c>.</summary>
    public string Display => $"{Pos}: {Phase.ToString().ToLowerInvariant()} error: {Message}";
}
