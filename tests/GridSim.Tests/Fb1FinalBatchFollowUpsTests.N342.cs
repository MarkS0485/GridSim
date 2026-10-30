using System;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N342 (FB1, final-batch follow-ups, October 2026; Ruling R1, Mark delegated EE calls 2026-10-04). The CIM importer
/// merges every description of one mRID into one object. That is right for the CGMES pattern (one profile DEFINES the
/// object with <c>rdf:ID</c>, EQ for equipment, and TP / SSH / SV add to it with <c>rdf:about</c>, in one document or
/// across several), but it also merged a SECOND <c>rdf:ID</c> of the same mRID without a word, so two different objects
/// that shared an identifier became one (first value of each property wins). Found through CimEquipmentTests' nonlinear
/// shunt helper, which writes both "section 1" points under one rdf:ID, so the importer's own duplicate-point check never
/// saw the second point (row C4 of FINAL-BATCH-MOVES-2026-10-10.md, which main applied at 1.95.260 under Mark's go; that test is not touched here).
///
/// <para>The rule now (CimImporter.MergeByMrid):</para>
/// <list type="bullet">
/// <item>two <c>rdf:ID</c> definitions of one mRID in ONE document: refused by name (invalid RDF/XML, W3C RDF 1.1 XML
/// Syntax 2.14; which element is the object cannot be decided);</item>
/// <item>two <c>rdf:ID</c> definitions in DIFFERENT documents: merged as before and listed in the notes by name;</item>
/// <item>one <c>rdf:ID</c> plus <c>rdf:about</c> descriptions: unchanged, bit for bit, no note.</item>
/// </list>
/// </summary>
public sealed partial class Fb1FinalBatchFollowUpsTests
{
    private const string CimHeadN342 = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string CimTailN342 = "</rdf:RDF>";

    // ---- the documents --------------------------------------------------------------------------------------------

    // The EQ part of a small bus-branch model: a line l1 (terminals t1, t2), a load ld1 (t3) and a machine g1 (t4). No
    // nodes on the terminals and no p / q: those come from the TP and SSH parts below, by rdf:about.
    private const string EqPart = """
        <cim:ACLineSegment rdf:ID="_l1"><cim:IdentifiedObject.name>Line1</cim:IdentifiedObject.name>
          <cim:ACLineSegment.r>0.5</cim:ACLineSegment.r><cim:ACLineSegment.x>2.5</cim:ACLineSegment.x>
          <cim:ACLineSegment.bch>0.0001</cim:ACLineSegment.bch></cim:ACLineSegment>
        <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>
        <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber></cim:Terminal>
        <cim:EnergyConsumer rdf:ID="_ld1"><cim:IdentifiedObject.name>Load1</cim:IdentifiedObject.name></cim:EnergyConsumer>
        <cim:Terminal rdf:ID="_t3"><cim:Terminal.ConductingEquipment rdf:resource="#_ld1"/></cim:Terminal>
        <cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>Gen1</cim:IdentifiedObject.name></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_t4"><cim:Terminal.ConductingEquipment rdf:resource="#_g1"/></cim:Terminal>
        """;

    // The TP part: the two TopologicalNodes (defined here, rdf:ID) and each EQ terminal's node (rdf:about).
    private const string TpPart = """
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>Node1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>Node2</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:Terminal rdf:about="#_t1"><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t2"><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t3"><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t4"><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        """;

    // The SSH part: the load's p / q and the machine's p (CGMES load sign: -13 is 13 MW generated), by rdf:about.
    private const string SshPart = """
        <cim:EnergyConsumer rdf:about="#_ld1"><cim:EnergyConsumer.p>12.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>3.2</cim:EnergyConsumer.q></cim:EnergyConsumer>
        <cim:SynchronousMachine rdf:about="#_g1"><cim:RotatingMachine.p>-13.0</cim:RotatingMachine.p></cim:SynchronousMachine>
        """;

    private static string CimDoc(params string[] parts) => CimHeadN342 + string.Concat(parts) + CimTailN342;

    /// <summary>The network the three parts describe, field for field (the values R064's merged-document test pins).</summary>
    private static void AssertTheSmallModel(CimNetwork net)
    {
        Assert.Equal(new[] { new CimBus("n1", "Node1"), new CimBus("n2", "Node2") }, net.Buses.ToArray());
        CimBranch line = Assert.Single(net.Branches);
        Assert.Equal(("l1", "Line1", "n1", "n2"), (line.MRID, line.Name, line.FromNode, line.ToNode));
        Assert.Equal(2.5, line.X);
        CimLoad load = Assert.Single(net.Loads);
        Assert.Equal(("ld1", "n2", 12.5, 3.2), (load.MRID, load.Node, load.P, load.Q));
        CimGenerator gen = Assert.Single(net.Generators);
        Assert.Equal(("g1", "n1", 13.0), (gen.MRID, gen.Node, gen.P));
    }

    // ---- the legitimate merge: unchanged ----------------------------------------------------------------------------

    /// <summary>One rdf:ID plus rdf:about descriptions in ONE document (a merged CGMES export): the model is read as it
    /// always was and no N342 note is written.</summary>
    [Fact]
    public void N342_an_rdf_id_with_rdf_about_descriptions_in_one_document_is_merged_as_before()
    {
        CimNetwork net = CimImporter.Parse(CimDoc(EqPart, TpPart, SshPart));
        AssertTheSmallModel(net);
        Assert.Empty(net.Notes);
    }

    /// <summary>The same parts as three profile documents (EQ, TP, SSH): bit for bit the network of the one-document
    /// form (every record equal, so every double equal), and no N342 note.</summary>
    [Fact]
    public void N342_the_cross_profile_merge_across_documents_equals_the_one_document_parse_exactly()
    {
        CimNetwork one = CimImporter.Parse(CimDoc(EqPart, TpPart, SshPart));
        CimNetwork three = CimImporter.Parse(new[] { CimDoc(EqPart), CimDoc(TpPart), CimDoc(SshPart) });
        AssertTheSmallModel(three);
        Assert.Equal(one.Buses, three.Buses);
        Assert.Equal(one.Branches, three.Branches);
        Assert.Equal(one.Loads, three.Loads);
        Assert.Equal(one.Generators, three.Generators);
        Assert.Equal(one.Transformers, three.Transformers);
        Assert.Equal(one.Notes, three.Notes);
        Assert.Empty(three.Notes);
    }

    /// <summary>CGMES 3 writes urn:uuid identifiers with rdf:about everywhere (no rdf:ID at all): unaffected.</summary>
    [Fact]
    public void N342_cgmes3_rdf_about_only_documents_are_unaffected()
    {
        string doc = CimDoc(EqPart, TpPart, SshPart).Replace("rdf:ID=\"_", "rdf:about=\"urn:uuid:", StringComparison.Ordinal)
            .Replace("\"#_", "\"urn:uuid:", StringComparison.Ordinal);
        CimNetwork net = CimImporter.Parse(doc);
        AssertTheSmallModel(net);
        Assert.Empty(net.Notes);
    }

    // ---- two rdf:IDs in one document: refused ----------------------------------------------------------------------

    /// <summary>Two TopologicalNodes under one rdf:ID in one document: refused, naming the identifier, the document and
    /// both elements. Before N342 they merged into one bus named after the first.</summary>
    [Fact]
    public void N342_two_rdf_id_definitions_of_one_mrid_in_one_document_are_refused_by_name()
    {
        string doc = CimDoc("""
            <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>North</cim:IdentifiedObject.name></cim:TopologicalNode>
            <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>South</cim:IdentifiedObject.name></cim:TopologicalNode>
            """);
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("the rdf:ID '_n1' is defined twice in one document (document 1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("first by TopologicalNode 'North' (mRID n1), then by TopologicalNode 'South' (mRID n1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the document is refused rather than the two merged into one", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Two objects of different classes under one rdf:ID (a line and a load): refused the same way. Before N342
    /// the load's properties were folded into the line and the load vanished.</summary>
    [Fact]
    public void N342_two_classes_under_one_rdf_id_are_refused()
    {
        string doc = CimDoc(EqPart, TpPart, SshPart,
            """<cim:EnergyConsumer rdf:ID="_l1"><cim:IdentifiedObject.name>Clash</cim:IdentifiedObject.name></cim:EnergyConsumer>""");
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("first by ACLineSegment 'Line1' (mRID l1), then by EnergyConsumer 'Clash' (mRID l1)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An element with no name is named by its mRID in the message.</summary>
    [Fact]
    public void N342_an_unnamed_duplicate_is_named_by_its_mrid()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(CimDoc(
            """<cim:TopologicalNode rdf:ID="_x"/><cim:ConnectivityNode rdf:ID="_x"/>""")));
        Assert.Contains("first by TopologicalNode 'x' (mRID x), then by ConnectivityNode 'x' (mRID x)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-105 keeps its own message: two DIFFERENT spellings that reduce to one mRID ("a" and "_a") are still
    /// refused by the identifier table first, with both spellings named (N342 only adds the same-spelling case).</summary>
    [Fact]
    public void N342_two_spellings_of_one_mrid_keep_the_d105_refusal()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(CimDoc(
            """<cim:TopologicalNode rdf:ID="a"/><cim:TopologicalNode rdf:ID="_a"/>""")));
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'_a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("different rdf:IDs, but both reduce to the mRID", ex.Message, StringComparison.Ordinal);
    }

    // ---- the case that found it: the nonlinear shunt points ---------------------------------------------------------

    // A nonlinear shunt c2 on n2 (with its terminal) and the given points, each with its OWN rdf:ID given by the caller.
    private static string NonlinearShuntN342(params (string Id, int Section, string B)[] points) =>
        """<cim:NonlinearShuntCompensator rdf:ID="_c2"><cim:IdentifiedObject.name>c2</cim:IdentifiedObject.name><cim:ShuntCompensator.sections>1</cim:ShuntCompensator.sections></cim:NonlinearShuntCompensator>"""
        + """<cim:Terminal rdf:ID="_c2_t"><cim:Terminal.ConductingEquipment rdf:resource="#_c2"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>"""
        + string.Concat(points.Select(p =>
            $"""<cim:NonlinearShuntCompensatorPoint rdf:ID="_{p.Id}"><cim:NonlinearShuntCompensatorPoint.sectionNumber>{p.Section}</cim:NonlinearShuntCompensatorPoint.sectionNumber><cim:NonlinearShuntCompensatorPoint.b>{p.B}</cim:NonlinearShuntCompensatorPoint.b><cim:NonlinearShuntCompensatorPoint.g>0</cim:NonlinearShuntCompensatorPoint.g><cim:NonlinearShuntCompensatorPoint.NonlinearShuntCompensator rdf:resource="#_c2"/></cim:NonlinearShuntCompensatorPoint>"""));

    /// <summary>Two points for section 1 under their OWN identifiers reach the importer's duplicate-point check, which
    /// refuses them (this is row C4's proof: with distinct ids the refusal the CimEquipmentTests test expects is thrown).
    /// Under ONE identifier (what that test's helper writes) the document is now refused by N342 before any point is read,
    /// instead of the second point silently vanishing.</summary>
    [Fact]
    public void N342_two_section_1_points_are_refused_both_under_distinct_ids_and_under_one_id()
    {
        string distinct = CimDoc(EqPart, TpPart, SshPart, NonlinearShuntN342(("pA", 1, "0.0001"), ("pB", 1, "0.0002")));
        Assert.Contains("two points for section 1", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(distinct)).Message,
            StringComparison.Ordinal);

        string shared = CimDoc(EqPart, TpPart, SshPart, NonlinearShuntN342(("c2_p1", 1, "0.0001"), ("c2_p1", 1, "0.0002")));
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(shared));
        Assert.Contains("the rdf:ID '_c2_p1' is defined twice in one document", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NonlinearShuntCompensatorPoint 'c2_p1' (mRID c2_p1)", ex.Message, StringComparison.Ordinal);

        // One point per section, each under its own id: read as before (B is the section-1 point's).
        CimNetwork ok = CimImporter.Parse(CimDoc(EqPart, TpPart, SshPart, NonlinearShuntN342(("pA", 1, "0.0001"), ("pB", 2, "0.0002"))));
        Assert.Equal(0.0 + 0.0001, Assert.Single(ok.Shunts).B);
    }

    // ---- two rdf:IDs in two documents: merged as before, and said -------------------------------------------------

    /// <summary>Two documents that both DEFINE node n2 with rdf:ID: legal RDF/XML (each document has its own base) but not
    /// the CGMES pattern. The merge is the one it always was (first value of each property wins, in document order, so the
    /// bus keeps the first document's name), and a note names the identifier, both documents and both elements.</summary>
    [Fact]
    public void N342_two_rdf_id_definitions_in_two_documents_are_merged_as_before_and_listed()
    {
        string second = CimDoc("""<cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>Node2-again</cim:IdentifiedObject.name></cim:TopologicalNode>""");
        CimNetwork net = CimImporter.Parse(new[] { CimDoc(EqPart, TpPart, SshPart), second });
        AssertTheSmallModel(net);   // the merge itself is unchanged: n2 keeps "Node2", the first document's name
        string note = Assert.Single(net.Notes);
        Assert.Equal("The rdf:ID '_n2' is defined in two documents: document 1 (TopologicalNode 'Node2' (mRID n2)) and " +
                     "document 2 (TopologicalNode 'Node2-again' (mRID n2)). CGMES defines an object with rdf:ID in one profile " +
                     "and describes it by rdf:about in the others; the two definitions are merged as one object, the first " +
                     "value of each property winning in document order.", note);
    }

    /// <summary>Every export the importer can be fed back writes one rdf:ID per object: a round trip of the small model
    /// parses without an N342 refusal and gives the same network.</summary>
    [Fact]
    public void N342_an_export_round_trip_writes_one_rdf_id_per_object()
    {
        CimNetwork net = CimImporter.Parse(CimDoc(EqPart, TpPart, SshPart));
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Buses, rt.Buses);
        Assert.Equal(net.Branches, rt.Branches);
        Assert.Equal(net.Loads, rt.Loads);
        Assert.Equal(net.Generators, rt.Generators);
    }
}
