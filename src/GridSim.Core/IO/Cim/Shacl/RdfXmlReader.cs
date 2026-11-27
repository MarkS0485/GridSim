using System.Xml;
using System.Xml.Linq;

namespace GridSim.Core.IO.Cim.Shacl;

/// <summary>
/// RC5 WP10: reads the RDF/XML of CGMES instance files and RDFS files into an <see cref="RdfGraph"/> (DTD processing
/// prohibited, no external resolution). Node elements: typed elements and rdf:Description, with rdf:about, rdf:ID,
/// rdf:nodeID or none (a blank node), property attributes as literals. Property elements: rdf:resource, rdf:nodeID, a
/// nested node element, rdf:datatype, xml:lang, rdf:parseType="Literal" (an rdf:XMLLiteral of the inner XML) and plain
/// text (an untyped literal: CGMES instance files carry no datatypes). rdf:parseType Resource and Collection, rdf:li and
/// reification (rdf:ID on a property element) are refused with the line and column.
/// </summary>
/// <remarks>Base IRI: the root's xml:base; else, by the CGMES convention, the document's xmlns:cim namespace without its
/// '#' (so "#_x" and rdf:ID="_x" are the same IRI in every file of a bundle, and an RDFS "#ACLineSegment" is the CIM class
/// IRI); else <c>urn:gridsim:document</c>.</remarks>
public static class RdfXmlReader
{
    private const string Rdf = RdfGraph.RdfNs;
    private const string XmlLiteral = Rdf + "XMLLiteral";

    /// <summary>IO-04: the deepest element nesting read. A CGMES document nests four or five levels (rdf:RDF, a node
    /// element, a property element, at most a nested node); a deeper document is refused with the line and column before
    /// any node is read, because the reader recurses once per node and property level and a stack overflow ends the
    /// process with no message.</summary>
    public const int MaxElementDepth = 256;

    /// <summary>Add the triples of <paramref name="text"/> to <paramref name="graph"/>; <paramref name="source"/> names the
    /// document in errors.</summary>
    public static void Read(RdfGraph graph, string text, string source)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(text);
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            // IO-04: a first, streaming pass refuses a document nested deeper than MaxElementDepth before a tree is built.
            using (var depthText = new StringReader(text))
            using (var depthReader = XmlReader.Create(depthText, settings))
                while (depthReader.Read())
                    if (depthReader.NodeType == XmlNodeType.Element && depthReader.Depth >= MaxElementDepth)
                    {
                        var li = (IXmlLineInfo)depthReader;
                        throw new InvalidDataException($"{source} line {li.LineNumber}, column {li.LinePosition}: elements nest deeper than {MaxElementDepth} levels.");
                    }
            using var sr = new StringReader(text);
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{source}: not a well-formed XML document without a DTD: {ex.Message}");
        }
        XElement root = doc.Root ?? throw new InvalidDataException($"{source}: empty document.");
        string? xmlBase = (string?)root.Attribute(XNamespace.Xml + "base");
        string? cimNs = root.Attributes().FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.LocalName == "cim")?.Value;
        string baseIri = xmlBase ?? (cimNs is not null ? cimNs.TrimEnd('#') : "urn:gridsim:document");
        var blanks = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
        IEnumerable<XElement> nodes = root.Name == XName.Get("RDF", Rdf) ? root.Elements() : new[] { root };
        foreach (XElement n in nodes) Node(graph, n, baseIri, blanks, source);
    }

    private static InvalidDataException Error(XElement e, string source, string what)
    {
        var li = (IXmlLineInfo)e;
        return new InvalidDataException($"{source} line {li.LineNumber}, column {li.LinePosition}: {what}");
    }

    private static RdfTerm Node(RdfGraph g, XElement e, string baseIri, Dictionary<string, RdfTerm> blanks, string source)
    {
        string? about = (string?)e.Attribute(XName.Get("about", Rdf)), id = (string?)e.Attribute(XName.Get("ID", Rdf)),
            nodeId = (string?)e.Attribute(XName.Get("nodeID", Rdf));
        if ((about is not null ? 1 : 0) + (id is not null ? 1 : 0) + (nodeId is not null ? 1 : 0) > 1)
            throw Error(e, source, "a node element has more than one of rdf:about, rdf:ID and rdf:nodeID.");
        RdfTerm subject = about is not null ? RdfTerm.Iri(RdfGraph.Resolve(baseIri, about))
            : id is not null ? RdfTerm.Iri(RdfGraph.Resolve(baseIri, "#" + id))
            : nodeId is not null ? Blank(g, blanks, nodeId) : g.NewBlank();
        if (e.Name != XName.Get("Description", Rdf)) g.Add(subject, RdfGraph.Type, RdfTerm.Iri(e.Name.NamespaceName + e.Name.LocalName));
        foreach (XAttribute a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.Namespace == XNamespace.Xml) continue;
            if (a.Name.NamespaceName == Rdf)
            {
                if (a.Name.LocalName is "about" or "ID" or "nodeID") continue;
                if (a.Name.LocalName == "type") { g.Add(subject, RdfGraph.Type, RdfTerm.Iri(RdfGraph.Resolve(baseIri, a.Value))); continue; }
                throw Error(e, source, $"the attribute rdf:{a.Name.LocalName} is not read on a node element.");
            }
            if (a.Name.NamespaceName.Length == 0) throw Error(e, source, $"the unqualified attribute '{a.Name.LocalName}' is not RDF/XML.");
            g.Add(subject, RdfTerm.Iri(a.Name.NamespaceName + a.Name.LocalName), Plain(a.Value, Lang(e)));
        }
        foreach (XElement p in e.Elements()) Property(g, subject, p, baseIri, blanks, source);
        return subject;
    }

    private static RdfTerm Plain(string text, string? lang) => RdfTerm.Literal(text, lang is null ? null : RdfTerm.LangString, lang);

    private static string? Lang(XElement e)
    {
        for (XElement? x = e; x is not null; x = x.Parent)
            if ((string?)x.Attribute(XNamespace.Xml + "lang") is string l) return l.Length == 0 ? null : l;
        return null;
    }

    private static RdfTerm Blank(RdfGraph g, Dictionary<string, RdfTerm> blanks, string label)
    {
        if (!blanks.TryGetValue(label, out RdfTerm b)) blanks[label] = b = g.NewBlank();
        return b;
    }

    private static void Property(RdfGraph g, RdfTerm subject, XElement p, string baseIri, Dictionary<string, RdfTerm> blanks, string source)
    {
        if (p.Name.NamespaceName == Rdf && p.Name.LocalName == "li") throw Error(p, source, "rdf:li is not read.");
        if (p.Name.NamespaceName.Length == 0) throw Error(p, source, $"the unqualified property element '{p.Name.LocalName}' is not RDF/XML.");
        if (p.Attribute(XName.Get("ID", Rdf)) is not null) throw Error(p, source, "reification (rdf:ID on a property element) is not read.");
        RdfTerm pred = RdfTerm.Iri(p.Name.NamespaceName + p.Name.LocalName);
        string? parseType = (string?)p.Attribute(XName.Get("parseType", Rdf));
        if (parseType is not null)
        {
            if (parseType != "Literal") throw Error(p, source, $"rdf:parseType=\"{parseType}\" is not read (Literal is).");
            string inner = string.Concat(p.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
            g.Add(subject, pred, RdfTerm.Literal(inner, XmlLiteral));
            return;
        }
        string? res = (string?)p.Attribute(XName.Get("resource", Rdf)), nid = (string?)p.Attribute(XName.Get("nodeID", Rdf));
        if (res is not null || nid is not null)
        {
            if (res is not null && nid is not null) throw Error(p, source, "a property element has both rdf:resource and rdf:nodeID.");
            if (p.HasElements) throw Error(p, source, "a property element with rdf:resource also holds elements.");
            g.Add(subject, pred, res is not null ? RdfTerm.Iri(RdfGraph.Resolve(baseIri, res.Trim())) : Blank(g, blanks, nid!));
            return;
        }
        var children = p.Elements().ToList();
        if (children.Count > 1) throw Error(p, source, "a property element holds more than one node element.");
        if (children.Count == 1)
        {
            g.Add(subject, pred, Node(g, children[0], baseIri, blanks, source));
            return;
        }
        string? dt = (string?)p.Attribute(XName.Get("datatype", Rdf));
        g.Add(subject, pred, dt is not null ? RdfTerm.Literal(p.Value, RdfGraph.Resolve(baseIri, dt)) : Plain(p.Value, Lang(p)));
    }
}
