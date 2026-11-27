using System.Globalization;
using System.Text;

namespace GridSim.Core.IO.Cim.Shacl;

/// <summary>
/// RC5 WP10: a reader for a subset of Turtle (W3C Recommendation 2014), enough for the ENTSO-E SHACL files: @prefix and
/// PREFIX, @base and BASE, IRIs, prefixed names, <c>a</c>, <c>;</c> and <c>,</c>, blank nodes (<c>_:x</c> and
/// <c>[ ... ]</c>), collections <c>( ... )</c>, strings in all four quote forms with escapes, <c>@lang</c> and
/// <c>^^datatype</c>, integers, decimals, doubles and booleans, and # comments. Anything else (TriG graphs, RDF-star, a
/// keyword it does not know) is refused with the line and column.
/// </summary>
public static class TurtleReader
{
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    /// <summary>IO-03: the deepest nesting of <c>[ ... ]</c> blank nodes and <c>( ... )</c> collections read. The ENTSO-E
    /// shapes nest a few levels; a document nested deeper is refused with the line and column (each level is a stack frame
    /// of the reader, and a stack overflow ends the process with no message).</summary>
    public const int MaxNesting = 256;

    /// <summary>Add the triples of <paramref name="text"/> to <paramref name="graph"/>; <paramref name="source"/> names the
    /// document in errors and is its base IRI until an @base says otherwise.</summary>
    public static void Read(RdfGraph graph, string text, string source)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(text);
        new Parser(graph, text, source).Document();
    }

    private sealed class Parser(RdfGraph g, string s, string source)
    {
        private int _i, _line = 1, _col = 1;
        private string _base = "urn:gridsim:document";
        private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RdfTerm> _blanks = new(StringComparer.Ordinal);

        private InvalidDataException Error(string what) => new($"{source} line {_line}, column {_col}: {what}");

        private char Peek(int k = 0) => _i + k < s.Length ? s[_i + k] : '\0';
        private bool End => _i >= s.Length;

        private void Next()
        {
            if (s[_i] == '\n') { _line++; _col = 1; } else _col++;
            _i++;
        }

        private void Skip()
        {
            while (!End)
            {
                char c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n') Next();
                else if (c == '#') { while (!End && Peek() != '\n') Next(); }
                else break;
            }
        }

        private void Expect(char c)
        {
            Skip();
            if (Peek() != c) throw Error(End ? $"expected '{c}' but the document ended" : $"expected '{c}' but found '{Peek()}'");
            Next();
        }

        private bool Word(string w, bool caseInsensitive = false)
        {
            if (_i + w.Length > s.Length) return false;
            if (string.Compare(s, _i, w, 0, w.Length, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) != 0) return false;
            char after = Peek(w.Length);
            return !(char.IsAsciiLetterOrDigit(after) || after is '_' or '-' or ':');
        }

        public void Document()
        {
            while (true)
            {
                Skip();
                if (End) return;
                char c = Peek();
                if (c == '@')
                {
                    if (Word("@prefix")) { Advance(7); Prefix(); Expect('.'); continue; }
                    if (Word("@base")) { Advance(5); Skip(); _base = IriRef(); Expect('.'); continue; }
                    throw Error("unknown directive (only @prefix and @base are read).");
                }
                if (Word("PREFIX", true)) { Advance(6); Prefix(); continue; }
                if (Word("BASE", true)) { Advance(4); Skip(); _base = IriRef(); continue; }
                if (Word("GRAPH", true)) throw Error("TriG (named graphs) is not read; the shapes must be Turtle.");
                if (c == '{') throw Error("TriG (a graph block '{') is not read; the shapes must be Turtle.");
                Triples();
                Expect('.');
            }
        }

        private void Advance(int n) { for (int k = 0; k < n; k++) Next(); }

        private void Prefix()
        {
            Skip();
            int start = _i;
            while (!End && Peek() != ':' && !char.IsWhiteSpace(Peek())) Next();
            if (Peek() != ':') throw Error("a prefix declaration needs 'name:'");
            string name = s[start.._i];
            Next();
            Skip();
            _prefixes[name] = IriRef();
        }

        private void Triples()
        {
            Skip();
            RdfTerm subject;
            if (Peek() == '[')
            {
                subject = BlankPropertyList();
                Skip();
                if (Peek() == '.') return;
            }
            else if (Peek() == '(') subject = Collection();
            else subject = Term(allowLiteral: false);
            PredicateObjectList(subject);
        }

        private void PredicateObjectList(RdfTerm subject)
        {
            while (true)
            {
                Skip();
                RdfTerm verb;
                if (Peek() == 'a' && Word("a")) { Next(); verb = RdfGraph.Type; }
                else verb = Term(allowLiteral: false);
                if (verb.IsBlank) throw Error("a predicate cannot be a blank node");
                while (true)
                {
                    Skip();
                    g.Add(subject, verb, Object());
                    Skip();
                    if (Peek() == ',') { Next(); continue; }
                    break;
                }
                Skip();
                if (Peek() != ';') return;
                while (Peek() == ';') { Next(); Skip(); }
                if (Peek() is '.' or ']') return;
            }
        }

        private RdfTerm Object()
        {
            Skip();
            return Peek() switch
            {
                '[' => BlankPropertyList(),
                '(' => Collection(),
                _ => Term(allowLiteral: true),
            };
        }

        // IO-03: how deep the blank-node and collection brackets are nested at the read position. Each level is a stack frame
        // of this recursive reader, so it is bounded (a stack overflow ends the process with no message).
        private int _nesting;

        private void Enter(char bracket)
        {
            if (++_nesting > TurtleReader.MaxNesting)
                throw Error($"'{bracket}' brackets nest deeper than {TurtleReader.MaxNesting} levels");
        }

        private RdfTerm BlankPropertyList()
        {
            Enter('[');
            try { return BlankPropertyListCore(); }
            finally { _nesting--; }
        }

        private RdfTerm BlankPropertyListCore()
        {
            Expect('[');
            RdfTerm b = g.NewBlank();
            Skip();
            if (Peek() == ']') { Next(); return b; }
            PredicateObjectList(b);
            Expect(']');
            return b;
        }

        private RdfTerm Collection()
        {
            Enter('(');
            try { return CollectionCore(); }
            finally { _nesting--; }
        }

        private RdfTerm CollectionCore()
        {
            Expect('(');
            var items = new List<RdfTerm>();
            while (true)
            {
                Skip();
                if (End) throw Error("a collection '(' is not closed");
                if (Peek() == ')') { Next(); break; }
                items.Add(Object());
            }
            if (items.Count == 0) return RdfGraph.Nil;
            RdfTerm head = g.NewBlank(), node = head;
            for (int k = 0; k < items.Count; k++)
            {
                g.Add(node, RdfGraph.First, items[k]);
                RdfTerm next = k == items.Count - 1 ? RdfGraph.Nil : g.NewBlank();
                g.Add(node, RdfGraph.Rest, next);
                node = next;
            }
            return head;
        }

        private RdfTerm Term(bool allowLiteral)
        {
            Skip();
            if (End) throw Error("the document ended inside a statement");
            char c = Peek();
            if (c == '<')
            {
                if (Peek(1) == '<') throw Error("RDF-star ('<<') is not read.");
                return RdfTerm.Iri(IriRef());
            }
            if (c == '_' && Peek(1) == ':')
            {
                Advance(2);
                int start = _i;
                while (!End && (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '_' or '-' or '.')) Next();
                while (_i > start && s[_i - 1] == '.') { _i--; _col--; }
                string label = s[start.._i];
                if (label.Length == 0) throw Error("a blank node label is empty");
                if (!_blanks.TryGetValue(label, out RdfTerm b)) _blanks[label] = b = g.NewBlank();
                return b;
            }
            if (c is '"' or '\'')
            {
                if (!allowLiteral) throw Error("a literal cannot be a subject or a predicate");
                return Literal();
            }
            if (char.IsAsciiDigit(c) || ((c is '+' or '-' or '.') && (char.IsAsciiDigit(Peek(1)) || (Peek(1) == '.' && char.IsAsciiDigit(Peek(2))))))
            {
                if (!allowLiteral) throw Error("a number cannot be a subject or a predicate");
                return Number();
            }
            if (Word("true") || Word("false"))
            {
                if (!allowLiteral) throw Error("a boolean cannot be a subject or a predicate");
                bool t = Word("true");
                Advance(t ? 4 : 5);
                return RdfTerm.Literal(t ? "true" : "false", Xsd + "boolean");
            }
            if (c == '{') throw Error("TriG (a graph block '{') is not read; the shapes must be Turtle.");
            return RdfTerm.Iri(PrefixedName());
        }

        private string IriRef()
        {
            if (Peek() != '<') throw Error("expected an IRI '<...>'");
            Next();
            var sb = new StringBuilder();
            while (true)
            {
                if (End) throw Error("an IRI '<' is not closed");
                char c = Peek();
                if (c == '>') { Next(); break; }
                if (c is '\n' or ' ' or '"' or '{' or '}' or '|' or '^' or '`') throw Error($"the character '{c}' is not allowed in an IRI");
                if (c == '\\') { Next(); sb.Append(UEscape()); continue; }
                sb.Append(c);
                Next();
            }
            return RdfGraph.Resolve(_base, sb.ToString());
        }

        private string PrefixedName()
        {
            int start = _i;
            while (!End && (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '-' or '.')) Next();
            if (Peek() != ':') throw Error($"expected an IRI, a prefixed name or a literal but found '{(End ? "end of document" : s[start..Math.Min(s.Length, start + 12)])}'");
            string pfx = s[start.._i];
            if (pfx.EndsWith('.')) throw Error("a prefix name cannot end with '.'");
            Next();
            var local = new StringBuilder();
            while (!End)
            {
                char c = Peek();
                if (char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '%') { local.Append(c); Next(); }
                else if (c == '.' && (char.IsLetterOrDigit(Peek(1)) || Peek(1) is '_' or '-' or ':' or '%')) { local.Append(c); Next(); }
                else if (c == '\\' && Peek(1) != '\0') { Next(); local.Append(Peek()); Next(); }
                else break;
            }
            if (!_prefixes.TryGetValue(pfx, out string? ns)) throw Error($"the prefix '{pfx}:' is not declared");
            return ns + local;
        }

        private RdfTerm Literal()
        {
            char q = Peek();
            bool longForm = Peek(1) == q && Peek(2) == q;
            Advance(longForm ? 3 : 1);
            var sb = new StringBuilder();
            while (true)
            {
                if (End) throw Error("a string is not closed");
                char c = Peek();
                if (longForm && c == q && Peek(1) == q && Peek(2) == q)
                {
                    // A long string may end with up to two more quotes of its own.
                    while (Peek(3) == q) { sb.Append(q); Next(); }
                    Advance(3);
                    break;
                }
                if (!longForm && c == q) { Next(); break; }
                if (!longForm && c is '\n' or '\r') throw Error("a short string cannot hold a line break");
                if (c == '\\')
                {
                    Next();
                    char e = Peek();
                    switch (e)
                    {
                        case 't': sb.Append('\t'); Next(); break;
                        case 'b': sb.Append('\b'); Next(); break;
                        case 'n': sb.Append('\n'); Next(); break;
                        case 'r': sb.Append('\r'); Next(); break;
                        case 'f': sb.Append('\f'); Next(); break;
                        case '"': sb.Append('"'); Next(); break;
                        case '\'': sb.Append('\''); Next(); break;
                        case '\\': sb.Append('\\'); Next(); break;
                        case 'u' or 'U': sb.Append(UEscape()); break;
                        default: throw Error($"unknown escape '\\{e}' in a string");
                    }
                    continue;
                }
                sb.Append(c);
                Next();
            }
            string text = sb.ToString();
            if (Peek() == '@')
            {
                Next();
                int start = _i;
                while (!End && (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '-')) Next();
                string lang = s[start.._i];
                if (lang.Length == 0) throw Error("an empty language tag");
                return RdfTerm.Literal(text, RdfTerm.LangString, lang.ToLowerInvariant());
            }
            if (Peek() == '^' && Peek(1) == '^')
            {
                Advance(2);
                RdfTerm dt = Term(allowLiteral: false);
                if (!dt.IsIri) throw Error("a datatype must be an IRI");
                return RdfTerm.Literal(text, dt.Value);
            }
            return RdfTerm.Literal(text, RdfTerm.XsdString);
        }

        private string UEscape()
        {
            char kind = Peek();
            int n = kind == 'u' ? 4 : kind == 'U' ? 8 : throw Error($"unknown escape '\\{kind}'");
            Next();
            if (_i + n > s.Length) throw Error("a \\u escape is cut short");
            string hex = s.Substring(_i, n);
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp) || cp > 0x10FFFF) throw Error($"'{hex}' is not a code point");
            Advance(n);
            return char.ConvertFromUtf32(cp);
        }

        private RdfTerm Number()
        {
            int start = _i;
            if (Peek() is '+' or '-') Next();
            while (char.IsAsciiDigit(Peek())) Next();
            bool dec = false, exp = false;
            if (Peek() == '.' && char.IsAsciiDigit(Peek(1))) { dec = true; Next(); while (char.IsAsciiDigit(Peek())) Next(); }
            if (Peek() is 'e' or 'E')
            {
                exp = true;
                Next();
                if (Peek() is '+' or '-') Next();
                if (!char.IsAsciiDigit(Peek())) throw Error("an exponent needs digits");
                while (char.IsAsciiDigit(Peek())) Next();
            }
            string lex = s[start.._i];
            return RdfTerm.Literal(lex, Xsd + (exp ? "double" : dec ? "decimal" : "integer"));
        }
    }
}
