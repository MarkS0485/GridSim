using System.Text;

namespace GridSim.Core.IO.Cim.Shacl;

/// <summary>The kind of an RDF term.</summary>
public enum RdfTermKind
{
    Iri = 0,
    Blank = 1,
    Literal = 2,
}

/// <summary>
/// RC5 WP10: an RDF term. A literal's <see cref="Datatype"/> is null when the source gave none and the syntax implies none
/// (an RDF/XML literal without rdf:datatype: CGMES instance files carry no datatypes, the profile gives them); a Turtle
/// string without a tag or datatype is xsd:string, and a language-tagged one rdf:langString with <see cref="Lang"/>.
/// Terms are ordered ordinally by kind, value, datatype and language (the order every report uses).
/// </summary>
public readonly record struct RdfTerm(RdfTermKind Kind, string Value, string? Datatype = null, string? Lang = null) : IComparable<RdfTerm>
{
    public const string XsdString = "http://www.w3.org/2001/XMLSchema#string";
    public const string LangString = "http://www.w3.org/1999/02/22-rdf-syntax-ns#langString";

    public static RdfTerm Iri(string iri) => new(RdfTermKind.Iri, iri);
    public static RdfTerm Blank(string label) => new(RdfTermKind.Blank, label);
    public static RdfTerm Literal(string text, string? datatype = null, string? lang = null) => new(RdfTermKind.Literal, text, datatype, lang);

    public bool IsIri => Kind == RdfTermKind.Iri;
    public bool IsBlank => Kind == RdfTermKind.Blank;
    public bool IsLiteral => Kind == RdfTermKind.Literal;

    public int CompareTo(RdfTerm other)
    {
        int c = Kind.CompareTo(other.Kind);
        if (c != 0) return c;
        c = string.CompareOrdinal(Value, other.Value);
        if (c != 0) return c;
        c = string.CompareOrdinal(Datatype, other.Datatype);
        return c != 0 ? c : string.CompareOrdinal(Lang, other.Lang);
    }

    public static bool operator <(RdfTerm a, RdfTerm b) => a.CompareTo(b) < 0;
    public static bool operator >(RdfTerm a, RdfTerm b) => a.CompareTo(b) > 0;
    public static bool operator <=(RdfTerm a, RdfTerm b) => a.CompareTo(b) <= 0;
    public static bool operator >=(RdfTerm a, RdfTerm b) => a.CompareTo(b) >= 0;

    /// <summary>N-Triples-like text: &lt;iri&gt;, _:label, "text"^^&lt;datatype&gt; or "text"@lang.</summary>
    public override string ToString()
    {
        switch (Kind)
        {
            case RdfTermKind.Iri: return "<" + Value + ">";
            case RdfTermKind.Blank: return "_:" + Value;
            default:
                var sb = new StringBuilder("\"");
                foreach (char ch in Value)
                    sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => ch.ToString() });
                sb.Append('"');
                if (Lang is not null) sb.Append('@').Append(Lang);
                else if (Datatype is not null && Datatype != XsdString) sb.Append("^^<").Append(Datatype).Append('>');
                return sb.ToString();
        }
    }
}

/// <summary>One triple.</summary>
public readonly record struct RdfTriple(RdfTerm S, RdfTerm P, RdfTerm O);

/// <summary>
/// RC5 WP10: an in-memory RDF graph (a set of triples) with subject and object indexes. Blank node labels are given by
/// the graph in order of first appearance across the documents read into it (b0, b1, ...), so the same documents read in
/// the same order give the same labels; readers of a document map its own labels onto fresh ones.
/// </summary>
public sealed class RdfGraph
{
    public const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string RdfsNs = "http://www.w3.org/2000/01/rdf-schema#";
    public static readonly RdfTerm Type = RdfTerm.Iri(RdfNs + "type");
    public static readonly RdfTerm First = RdfTerm.Iri(RdfNs + "first");
    public static readonly RdfTerm Rest = RdfTerm.Iri(RdfNs + "rest");
    public static readonly RdfTerm Nil = RdfTerm.Iri(RdfNs + "nil");
    public static readonly RdfTerm SubClassOf = RdfTerm.Iri(RdfsNs + "subClassOf");

    private readonly HashSet<RdfTriple> _set = new();
    private readonly List<RdfTriple> _triples = new();
    private readonly Dictionary<RdfTerm, List<RdfTriple>> _bySubject = new();
    private readonly Dictionary<RdfTerm, List<RdfTriple>> _byObject = new();
    private int _blank;

    public int Count => _triples.Count;
    public IReadOnlyList<RdfTriple> Triples => _triples;

    /// <summary>A fresh blank node.</summary>
    public RdfTerm NewBlank() => RdfTerm.Blank("b" + (_blank++).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Add(RdfTerm s, RdfTerm p, RdfTerm o)
    {
        var t = new RdfTriple(s, p, o);
        if (!_set.Add(t)) return;
        _triples.Add(t);
        if (!_bySubject.TryGetValue(s, out var a)) _bySubject[s] = a = new();
        a.Add(t);
        if (!_byObject.TryGetValue(o, out var b)) _byObject[o] = b = new();
        b.Add(t);
    }

    /// <summary>The objects of (s, p, ?), sorted.</summary>
    public IReadOnlyList<RdfTerm> Objects(RdfTerm s, RdfTerm p)
    {
        if (!_bySubject.TryGetValue(s, out var l)) return Array.Empty<RdfTerm>();
        var r = new SortedSet<RdfTerm>();
        foreach (RdfTriple t in l) if (t.P == p) r.Add(t.O);
        return r.ToList();
    }

    /// <summary>The subjects of (?, p, o), sorted.</summary>
    public IReadOnlyList<RdfTerm> Subjects(RdfTerm p, RdfTerm o)
    {
        if (!_byObject.TryGetValue(o, out var l)) return Array.Empty<RdfTerm>();
        var r = new SortedSet<RdfTerm>();
        foreach (RdfTriple t in l) if (t.P == p) r.Add(t.S);
        return r.ToList();
    }

    /// <summary>Every triple with subject <paramref name="s"/> (in insertion order).</summary>
    public IReadOnlyList<RdfTriple> About(RdfTerm s) => _bySubject.TryGetValue(s, out var l) ? l : Array.Empty<RdfTriple>();

    /// <summary>Every subject of a triple with predicate <paramref name="p"/>, sorted.</summary>
    public IReadOnlyList<RdfTerm> SubjectsOf(RdfTerm p) => new SortedSet<RdfTerm>(_triples.Where(t => t.P == p).Select(t => t.S)).ToList();

    /// <summary>Every object of a triple with predicate <paramref name="p"/>, sorted.</summary>
    public IReadOnlyList<RdfTerm> ObjectsOf(RdfTerm p) => new SortedSet<RdfTerm>(_triples.Where(t => t.P == p).Select(t => t.O)).ToList();

    /// <summary>The members of the RDF list starting at <paramref name="head"/>; null when it is not a well-formed list.</summary>
    public IReadOnlyList<RdfTerm>? ListOf(RdfTerm head)
    {
        var items = new List<RdfTerm>();
        var seen = new HashSet<RdfTerm>();
        RdfTerm n = head;
        while (n != Nil)
        {
            if (!seen.Add(n)) return null;
            IReadOnlyList<RdfTerm> f = Objects(n, First), r = Objects(n, Rest);
            if (f.Count != 1 || r.Count != 1) return null;
            items.Add(f[0]);
            n = r[0];
        }
        return items;
    }

    /// <summary>Resolve <paramref name="reference"/> against <paramref name="baseIri"/> (absolute, fragment, empty or a
    /// path relative to the base's last segment; no dot-segment removal).</summary>
    public static string Resolve(string baseIri, string reference)
    {
        if (HasScheme(reference)) return reference;
        if (reference.Length == 0) return baseIri;
        int hash = baseIri.IndexOf('#', StringComparison.Ordinal);
        string noFrag = hash >= 0 ? baseIri[..hash] : baseIri;
        if (reference[0] == '#') return noFrag + reference;
        int slash = noFrag.LastIndexOf('/');
        return (slash >= 0 ? noFrag[..(slash + 1)] : noFrag + "/") + reference;
    }

    private static bool HasScheme(string s)
    {
        if (s.Length == 0 || !char.IsAsciiLetter(s[0])) return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ':') return true;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return false;
        }
        return false;
    }
}
