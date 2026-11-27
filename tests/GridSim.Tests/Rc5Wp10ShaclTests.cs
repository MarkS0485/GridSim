using System.Globalization;
using GridSim.Core.IO.Cim.Shacl;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP10: the native SHACL Core subset checker (no third-party dependency), its Turtle subset reader and RDF/XML
/// reader, and the cgmes-shacl verb. The expected results are derived by hand from W3C SHACL (Recommendation 2017):
/// sh:minCount §4.2.1, sh:maxCount §4.2.2, sh:datatype §4.1.2, sh:pattern §4.4.3, sh:class §4.1.1 (with the rdfs:subClassOf
/// closure of §3.2 SHACL instance), and the report is sorted by focus, path, component, shape and value.
/// </summary>
public sealed class Rc5Wp10ShaclTests
{
    private const string Cim = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    /// <summary>Four objects: L1 (r present, x "abc"), L2 (no r, two names), T1 (a bad mRID, pointing at L1), T2.</summary>
    internal const string Data = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#">
          <cim:ACLineSegment rdf:ID="_L1"><cim:IdentifiedObject.name>L1</cim:IdentifiedObject.name><cim:IdentifiedObject.mRID>L1</cim:IdentifiedObject.mRID><cim:ACLineSegment.r>0.5</cim:ACLineSegment.r><cim:ACLineSegment.x>abc</cim:ACLineSegment.x></cim:ACLineSegment>
          <cim:ACLineSegment rdf:ID="_L2"><cim:IdentifiedObject.name>L2a</cim:IdentifiedObject.name><cim:IdentifiedObject.name>L2b</cim:IdentifiedObject.name><cim:IdentifiedObject.mRID>L2</cim:IdentifiedObject.mRID><cim:ACLineSegment.x>1.5</cim:ACLineSegment.x></cim:ACLineSegment>
          <cim:Terminal rdf:ID="_T1"><cim:IdentifiedObject.mRID>bad id!</cim:IdentifiedObject.mRID><cim:Terminal.ConductingEquipment rdf:resource="#_L1"/></cim:Terminal>
          <cim:Terminal rdf:ID="_T2"><cim:IdentifiedObject.mRID>T2</cim:IdentifiedObject.mRID></cim:Terminal>
        </rdf:RDF>
        """;

    internal const string ShapesA = """
        @prefix sh: <http://www.w3.org/ns/shacl#> .
        @prefix cim: <http://iec.ch/TC57/2013/CIM-schema-cim16#> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
        @prefix ex: <http://example.org/shapes#> .

        ex:Line a sh:NodeShape ; sh:targetClass cim:ACLineSegment ;
            sh:property ex:r-min , ex:name-max , ex:x-type .
        ex:r-min a sh:PropertyShape ; sh:path cim:ACLineSegment.r ; sh:minCount 1 .
        ex:name-max a sh:PropertyShape ; sh:path cim:IdentifiedObject.name ; sh:maxCount 1 .
        ex:x-type a sh:PropertyShape ; sh:path cim:ACLineSegment.x ; sh:datatype xsd:float .
        """;

    internal const string ShapesB = """"
        @prefix sh: <http://www.w3.org/ns/shacl#> .
        @prefix cim: <http://iec.ch/TC57/2013/CIM-schema-cim16#> .
        @prefix ex: <http://example.org/shapes#> .

        ex:Term a sh:NodeShape ; sh:targetClass cim:Terminal ;
            sh:property [ sh:path cim:IdentifiedObject.mRID ; sh:pattern "^[A-Za-z0-9_-]+$" ] ;
            sh:property ex:ce-class .
        ex:ce-class a sh:PropertyShape ; sh:path cim:Terminal.ConductingEquipment ; sh:class cim:ConductingEquipment .
        ex:Sparql a sh:NodeShape ; sh:targetClass cim:ACLineSegment ;
            sh:sparql [ a sh:SPARQLConstraint ; sh:select """SELECT $this WHERE { $this ?p ?o . }""" ] .
        """";

    internal const string Rdfs = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:rdfs="http://www.w3.org/2000/01/rdf-schema#" xml:base="http://iec.ch/TC57/2013/CIM-schema-cim16">
          <rdf:Description rdf:about="#ACLineSegment"><rdfs:subClassOf rdf:resource="#Conductor"/><rdfs:comment rdf:parseType="Literal">A <b>wire</b>.</rdfs:comment></rdf:Description>
          <rdf:Description rdf:about="#Conductor"><rdfs:subClassOf rdf:resource="#ConductingEquipment"/></rdf:Description>
        </rdf:RDF>
        """;

    private static ShaclReport Run(bool withRdfs, bool reversed = false)
    {
        var data = new RdfGraph();
        RdfXmlReader.Read(data, Data, "data.xml");
        var shapes = new RdfGraph();
        // The CLI reads files in ordinal path order; here the order is A then B (both orders tested through the CLI below).
        TurtleReader.Read(shapes, reversed ? ShapesB : ShapesA, reversed ? "b.ttl" : "a.ttl");
        TurtleReader.Read(shapes, reversed ? ShapesA : ShapesB, reversed ? "a.ttl" : "b.ttl");
        RdfGraph? rdfs = null;
        if (withRdfs) { rdfs = new RdfGraph(); RdfXmlReader.Read(rdfs, Rdfs, "rdfs.rdf"); }
        return ShaclValidator.Validate(data, shapes, rdfs);
    }

    private static string R(string local) => "<" + Cim + local + ">";

    [Fact]
    public void Shacl_core_gives_the_hand_derived_sorted_results_without_rdfs()
    {
        ShaclReport r = Run(withRdfs: false);
        Assert.False(r.Conforms);
        var expected = new[]
        {
            new ShaclResult("Violation", R("_L1"), R("ACLineSegment.x"), "sh:DatatypeConstraintComponent", "<http://example.org/shapes#x-type>", "\"abc\"",
                "the value is not a literal of datatype <http://www.w3.org/2001/XMLSchema#float>"),
            new ShaclResult("Violation", R("_L2"), R("ACLineSegment.r"), "sh:MinCountConstraintComponent", "<http://example.org/shapes#r-min>", "", "fewer than 1 value(s)"),
            new ShaclResult("Violation", R("_L2"), R("IdentifiedObject.name"), "sh:MaxCountConstraintComponent", "<http://example.org/shapes#name-max>", "", "more than 1 value(s)"),
            new ShaclResult("Violation", R("_T1"), R("IdentifiedObject.mRID"), "sh:PatternConstraintComponent", "_:b0", "\"bad id!\"", "the value does not match ^[A-Za-z0-9_-]+$"),
            new ShaclResult("Violation", R("_T1"), R("Terminal.ConductingEquipment"), "sh:ClassConstraintComponent", "<http://example.org/shapes#ce-class>", R("_L1"),
                "the value is not an instance of " + R("ConductingEquipment")),
        };
        Assert.Equal(expected, r.Results);
        ShaclNotRun n = Assert.Single(r.NotRun);
        Assert.Equal("<http://example.org/shapes#Sparql>", n.Shape);
        Assert.Equal("sh:sparql _:b1", n.What);
        Assert.Equal(1, r.SparqlShapesNotRun);
        Assert.Contains("SHACL Core subset", r.Summary, StringComparison.Ordinal);
        Assert.Contains("1 SPARQL shape(s) not run", r.Summary, StringComparison.Ordinal);
        Assert.Contains(r.Notes, s => s.Contains("no RDFS was given", StringComparison.Ordinal));
    }

    [Fact]
    public void Shacl_class_follows_rdfs_subclassof_when_the_rdfs_is_given()
    {
        ShaclReport r = Run(withRdfs: true);
        Assert.DoesNotContain(r.Results, x => x.Component == "sh:ClassConstraintComponent");
        Assert.Equal(4, r.Results.Count);
    }

    [Fact]
    public void Shacl_conforms_when_the_data_satisfies_the_shapes()
    {
        var data = new RdfGraph();
        RdfXmlReader.Read(data, Data, "data.xml");
        var shapes = new RdfGraph();
        TurtleReader.Read(shapes, """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix cim: <http://iec.ch/TC57/2013/CIM-schema-cim16#> .
            [] a sh:NodeShape ; sh:targetClass cim:Terminal ; sh:property [ sh:path cim:IdentifiedObject.mRID ; sh:minCount 1 ; sh:maxCount 1 ] .
            [] a sh:NodeShape ; sh:targetNode <http://iec.ch/TC57/2013/CIM-schema-cim16#_L2> ; sh:property [ sh:path cim:ACLineSegment.x ; sh:minInclusive 1 ; sh:maxExclusive 2.0e0 ; sh:in ( "1.5" "2.5" ) ] .
            """, "ok.ttl");
        ShaclReport r = ShaclValidator.Validate(data, shapes);
        Assert.True(r.Conforms, string.Join("\n", r.Results));
        Assert.Equal(2, r.ShapesRun);
    }

    [Fact]
    public void Turtle_subset_reads_lists_nested_blank_nodes_and_typed_literals_to_the_hand_count()
    {
        // ex:a ex:p (1 2): 1 + 4 list triples; ex:a ex:q [ ... ]: 1; the blank node's r and its two s values: 3. Total 9.
        var g = new RdfGraph();
        TurtleReader.Read(g, """
            @prefix ex: <http://e/> .
            ex:a ex:p ( 1 2 ) ;
                 ex:q [ ex:r "x"^^ex:dt ; ex:s ex:b , ex:c ] .
            """, "t.ttl");
        Assert.Equal(9, g.Count);
        Assert.Contains(g.Triples, t => t.O == RdfTerm.Literal("x", "http://e/dt"));
        Assert.Contains(g.Triples, t => t.O == RdfTerm.Literal("1", "http://www.w3.org/2001/XMLSchema#integer"));
    }

    [Theory]
    [InlineData("<http://g> { @base <http://x/> . }", "line 1, column 12")]
    [InlineData("@prefix ex: <http://e/> .\nGRAPH ex:g { ex:a ex:b ex:c . }", "line 2, column 1")]
    [InlineData("@prefix ex: <http://e/> .\nex:a ex:b << ex:c ex:d ex:e >> .", "line 2, column 11")]
    public void Turtle_outside_the_subset_is_refused_with_line_and_column(string text, string where)
    {
        var ex = Assert.Throws<InvalidDataException>(() => TurtleReader.Read(new RdfGraph(), text, "bad.ttl"));
        Assert.Contains("bad.ttl " + where, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rdfxml_outside_the_subset_is_refused_with_line_and_column()
    {
        const string text = "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:e=\"http://e/\">\n<e:A rdf:about=\"http://e/a\"><e:p rdf:parseType=\"Collection\"/></e:A></rdf:RDF>";
        var ex = Assert.Throws<InvalidDataException>(() => RdfXmlReader.Read(new RdfGraph(), text, "bad.rdf"));
        Assert.Contains("bad.rdf line 2, column", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Collection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_are_identical_run_twice_in_either_file_order_and_under_another_culture()
    {
        ShaclReport a = Run(withRdfs: false);
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ShaclReport b = Run(withRdfs: false);
            Assert.Equal(a.ToCsv(), b.ToCsv());
            Assert.Equal(a.ToJson(), b.ToJson());
        }
        finally { CultureInfo.CurrentCulture = saved; }

        string d = TestTemp.Under("gridsim-wp10-shacl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "bundle"));
        File.WriteAllText(Path.Combine(d, "bundle", "case_EQ.xml"), Data);
        File.WriteAllText(Path.Combine(d, "a.ttl"), ShapesA);
        File.WriteAllText(Path.Combine(d, "b.ttl"), ShapesB);
        CliProcess.Result r1 = CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle"), "--shapes", Path.Combine(d, "a.ttl"), "--shapes", Path.Combine(d, "b.ttl"), "--report", Path.Combine(d, "r1.csv"));
        CliProcess.Result r2 = CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle"), "--shapes", Path.Combine(d, "b.ttl"), "--shapes", Path.Combine(d, "a.ttl"), "--report", Path.Combine(d, "r2.csv"));
        // RC5 WP11: exit codes aligned with cgmes-conform (3 = the check found a result; 1 = an input cannot be read).
        Assert.True(r1.ExitCode == 3, r1.ToString());
        Assert.Equal(3, r2.ExitCode);
        Assert.Equal(File.ReadAllText(Path.Combine(d, "r1.csv")), File.ReadAllText(Path.Combine(d, "r2.csv")));
        Assert.Equal(a.ToCsv(), File.ReadAllText(Path.Combine(d, "r1.csv")));
        Assert.Contains("NOT RUN sh:sparql", r1.StdOut, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(d, "broken.ttl"), "@prefix ex: <http://e/> .\nex:a ex:b {");
        Assert.Equal(1, CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle"), "--shapes", Path.Combine(d, "broken.ttl")).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle")).ExitCode);
        File.WriteAllText(Path.Combine(d, "ok.ttl"), "@prefix sh: <http://www.w3.org/ns/shacl#> .\n@prefix cim: <" + Cim + "> .\n[] a sh:NodeShape ; sh:targetClass cim:Terminal ; sh:property [ sh:path cim:IdentifiedObject.mRID ; sh:minCount 1 ] .\n");
        Assert.Equal(0, CliProcess.RunBuilt("cgmes-shacl", Path.Combine(d, "bundle"), "--shapes", Path.Combine(d, "ok.ttl")).ExitCode);
    }
}
