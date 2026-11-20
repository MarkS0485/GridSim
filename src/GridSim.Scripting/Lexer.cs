using System.Globalization;
using System.Text;

namespace GridSim.Scripting;

public enum TokenKind { Word, Number, String, Symbol, Newline, End }

/// <summary>One token. <see cref="Text"/> is the word as written, the unquoted string, or the number as written.</summary>
public readonly record struct Token(TokenKind Kind, string Text, SourcePos Pos)
{
    /// <summary>Case-insensitive keyword test; only a Word token is ever a keyword.</summary>
    public bool Is(string keyword) =>
        Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the punctuation token with exactly this text.</summary>
    public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

    public string Describe() => Kind switch
    {
        TokenKind.Newline => "end of line",
        TokenKind.End => "end of script",
        TokenKind.String => $"\"{Text}\"",
        _ => $"'{Text}'",
    };
}

/// <summary>
/// Grammar v1 lexer. A line holds one statement. <c>#</c> starts a comment that runs to the end of the line. A word is a
/// letter followed by letters, digits or underscores. A number is digits and an optional decimal part (no exponent); a
/// minus sign is always its own symbol token (the parser reads it as negation or subtraction). A name is a double-quoted string; a backslash-quote and a double backslash are the only escapes.
/// Punctuation is <c>+ - * / ( ) , = &lt; &gt; &lt;= &gt;= !=</c>. Anything else is refused with its line and column.
/// </summary>
public static class Lexer
{
    public static IReadOnlyList<Token> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<Token>();
        int line = 1, col = 1, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            var pos = new SourcePos(line, col);
            if (c == '\r') { i++; col++; continue; }
            if (c == '\n') { tokens.Add(new Token(TokenKind.Newline, "\n", pos)); i++; line++; col = 1; continue; }
            if (c == ' ' || c == '\t') { i++; col++; continue; }
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') { i++; col++; }
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++; col++;
                bool closed = false;
                while (i < text.Length)
                {
                    char d = text[i];
                    if (d == '\n' || d == '\r') break;
                    if (d == '"') { closed = true; i++; col++; break; }
                    if (d == '\\')
                    {
                        if (i + 1 >= text.Length || (text[i + 1] != '"' && text[i + 1] != '\\'))
                            throw new ScriptException(ErrorPhase.Syntax, new SourcePos(line, col),
                                "a backslash in a name must be followed by a double quote or another backslash");
                        sb.Append(text[i + 1]);
                        i += 2; col += 2;
                        continue;
                    }
                    sb.Append(d); i++; col++;
                }
                if (!closed) throw new ScriptException(ErrorPhase.Syntax, pos, "unterminated name: the closing double quote is missing on this line");
                tokens.Add(new Token(TokenKind.String, sb.ToString(), pos));
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) { i++; col++; }
                tokens.Add(new Token(TokenKind.Word, text[start..i], pos));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) { i++; col++; }
                if (i < text.Length && text[i] == '.')
                {
                    if (i + 1 >= text.Length || !char.IsAsciiDigit(text[i + 1]))
                        throw new ScriptException(ErrorPhase.Syntax, new SourcePos(line, col), "a decimal point must be followed by a digit");
                    i++; col++;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) { i++; col++; }
                }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E') && i + 1 < text.Length
                    && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '-' || text[i + 1] == '+'))
                    throw new ScriptException(ErrorPhase.Syntax, new SourcePos(line, col), "exponent notation is not part of grammar 1; write the number out in full");
                tokens.Add(new Token(TokenKind.Number, text[start..i], pos));
                continue;
            }
            if (c == '<' || c == '>' || c == '!')
            {
                if (i + 1 < text.Length && text[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.Symbol, text.Substring(i, 2), pos));
                    i += 2; col += 2;
                    continue;
                }
                if (c != '!')
                {
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString(), pos));
                    i++; col++;
                    continue;
                }
            }
            if (c is '+' or '-' or '*' or '/' or '(' or ')' or ',' or '=')
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), pos));
                i++; col++;
                continue;
            }
            throw new ScriptException(ErrorPhase.Syntax, pos, string.Create(CultureInfo.InvariantCulture, $"unexpected character '{c}'"));
        }
        tokens.Add(new Token(TokenKind.Newline, "\n", new SourcePos(line, col)));
        tokens.Add(new Token(TokenKind.End, "", new SourcePos(line, col)));
        return tokens;
    }
}
