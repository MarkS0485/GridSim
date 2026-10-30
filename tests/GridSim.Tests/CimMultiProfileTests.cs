using System;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R064: the CIM importer reads real multi-profile CGMES (objects split across EQ rdf:ID and TP/SSH rdf:about
/// descriptions are merged by mRID), uses ONE node view (TopologicalNodes, with ConnectivityNodes mapped onto
/// them), keeps three-winding transformers (star-point bus in the bridge), lists everything it leaves out in
/// Diagnostics, and the bridge fails loud on an unresolved node.
/// </summary>
public class CimMultiProfileTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";

    // An EQ profile (equipment and terminals with their equipment, no nodes) followed by a TP profile (nodes and
    // the terminals' node links, by rdf:about) and an SSH profile (the consumer's p/q and the machine's p, by
    // rdf:about) - concatenated into one document, as a merged CGMES export is.
    private const string MergedCgmes = Head + """
        <cim:ACLineSegment rdf:ID="_l1"><cim:IdentifiedObject.name>Line1</cim:IdentifiedObject.name>
          <cim:ACLineSegment.r>0.5</cim:ACLineSegment.r><cim:ACLineSegment.x>2.5</cim:ACLineSegment.x>
          <cim:ACLineSegment.bch>0.0001</cim:ACLineSegment.bch></cim:ACLineSegment>
        <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:ACDCTerminal.sequenceNumber>1</cim:ACDCTerminal.sequenceNumber></cim:Terminal>
        <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:ACDCTerminal.sequenceNumber>2</cim:ACDCTerminal.sequenceNumber></cim:Terminal>
        <cim:EnergyConsumer rdf:ID="_ld1"><cim:IdentifiedObject.name>Load1</cim:IdentifiedObject.name></cim:EnergyConsumer>
        <cim:Terminal rdf:ID="_t3"><cim:Terminal.ConductingEquipment rdf:resource="#_ld1"/></cim:Terminal>
        <cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>Gen1</cim:IdentifiedObject.name></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_t4"><cim:Terminal.ConductingEquipment rdf:resource="#_g1"/></cim:Terminal>

        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>Node1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>Node2</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:Terminal rdf:about="#_t1"><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t2"><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t3"><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:Terminal rdf:about="#_t4"><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>

        <cim:EnergyConsumer rdf:about="#_ld1"><cim:EnergyConsumer.p>12.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>3.2</cim:EnergyConsumer.q></cim:EnergyConsumer>
        <cim:SynchronousMachine rdf:about="#_g1"><cim:RotatingMachine.p>-13.0</cim:RotatingMachine.p></cim:SynchronousMachine>
        """ + Tail;

    [Fact]
    public void A_merged_eq_tp_ssh_document_resolves_lines_loads_and_generators()
    {
        // Before R064: every EQ terminal had equipment but no node and every TP terminal a node but no
        // equipment, so branches, loads and generators all came back empty.
        CimNetwork net = CimImporter.Parse(MergedCgmes);

        Assert.Equal(2, net.Buses.Count);
        CimBranch line = Assert.Single(net.Branches);
        Assert.Equal(("n1", "n2"), (line.FromNode, line.ToNode));
        Assert.Equal(2.5, line.X);
        CimLoad load = Assert.Single(net.Loads);
        Assert.Equal(("n2", 12.5, 3.2), (load.Node, load.P, load.Q));   // p/q joined from the SSH description
        // Moved 2026-10 (Q2, authorised by Mark): the SSH fixture used to give the generating machine
        // RotatingMachine.p = 13.0 and expect gen.P == 13.0, pinning the same R267 defect as
        // CimImporterTests.Parses_loads_and_generators_with_their_nodes. CGMES SSH uses the LOAD sign convention
        // (ENTSO-E CGMES 2.4.15, IEC 61970-600-2), so a 13 MW generating machine is written p = -13.0 and imports
        // as generator-positive 13.0. The point of this test (p joined from the SSH description by mRID) stands.
        CimGenerator gen = Assert.Single(net.Generators);
        Assert.Equal(("n1", 13.0), (gen.Node, gen.P));
        Assert.Empty(net.Notes);
    }

    [Fact]
    public void Cgmes3_urn_uuid_identifiers_match_across_profiles()
    {
        string doc = MergedCgmes.Replace("rdf:ID=\"_", "rdf:about=\"urn:uuid:").Replace("\"#_", "\"urn:uuid:");
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Single(net.Branches);
        Assert.Single(net.Loads);
        Assert.Equal("n1", net.Generators.Single().Node);
    }

    [Fact]
    public void Connectivity_nodes_map_onto_topological_nodes_instead_of_becoming_orphan_buses()
    {
        string doc = Head + """
            <cim:TopologicalNode rdf:ID="_tn1"><cim:IdentifiedObject.name>TN1</cim:IdentifiedObject.name></cim:TopologicalNode>
            <cim:TopologicalNode rdf:ID="_tn2"><cim:IdentifiedObject.name>TN2</cim:IdentifiedObject.name></cim:TopologicalNode>
            <cim:ConnectivityNode rdf:ID="_cn1"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_tn1"/></cim:ConnectivityNode>
            <cim:ConnectivityNode rdf:ID="_cn2"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_tn2"/></cim:ConnectivityNode>
            <cim:ACLineSegment rdf:ID="_l1"><cim:ACLineSegment.x>1</cim:ACLineSegment.x></cim:ACLineSegment>
            <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.ConnectivityNode rdf:resource="#_cn1"/></cim:Terminal>
            <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.ConnectivityNode rdf:resource="#_cn2"/></cim:Terminal>
            """ + Tail;
        CimNetwork net = CimImporter.Parse(doc);

        // Before R064 both TNs and both CNs became buses (four, two of them orphans).
        Assert.Equal(new[] { "tn1", "tn2" }, net.Buses.Select(b => b.MRID).ToArray());
        CimBranch line = Assert.Single(net.Branches);
        Assert.Equal(("tn1", "tn2"), (line.FromNode, line.ToNode));
        Assert.Contains(net.Notes, n => n.Contains("ConnectivityNode"));
    }

    [Fact]
    public void A_node_breaker_only_document_uses_connectivity_nodes_as_buses()
    {
        string doc = Head + """
            <cim:ConnectivityNode rdf:ID="_cn1"/><cim:ConnectivityNode rdf:ID="_cn2"/>
            <cim:ACLineSegment rdf:ID="_l1"><cim:ACLineSegment.x>1</cim:ACLineSegment.x></cim:ACLineSegment>
            <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.ConnectivityNode rdf:resource="#_cn1"/></cim:Terminal>
            <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.ConnectivityNode rdf:resource="#_cn2"/></cim:Terminal>
            """ + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal(2, net.Buses.Count);
        Assert.Single(net.Branches);
    }

    // A 400/132/13 kV supergrid transformer with a ratio tap on the HV winding, feeding loads at 132 and 13 kV.
    private const string ThreeWinding = Head + """
        <cim:BaseVoltage rdf:ID="_b400"><cim:BaseVoltage.nominalVoltage>400</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:BaseVoltage rdf:ID="_b132"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:BaseVoltage rdf:ID="_b13"><cim:BaseVoltage.nominalVoltage>13</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:TopologicalNode rdf:ID="_hv"><cim:TopologicalNode.BaseVoltage rdf:resource="#_b400"/></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_lv"><cim:TopologicalNode.BaseVoltage rdf:resource="#_b132"/></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_tv"><cim:TopologicalNode.BaseVoltage rdf:resource="#_b13"/></cim:TopologicalNode>
        <cim:PowerTransformer rdf:ID="_sgt"><cim:IdentifiedObject.name>SGT1</cim:IdentifiedObject.name></cim:PowerTransformer>
        <cim:Terminal rdf:ID="_te1"><cim:Terminal.ConductingEquipment rdf:resource="#_sgt"/><cim:Terminal.TopologicalNode rdf:resource="#_hv"/></cim:Terminal>
        <cim:Terminal rdf:ID="_te2"><cim:Terminal.ConductingEquipment rdf:resource="#_sgt"/><cim:Terminal.TopologicalNode rdf:resource="#_lv"/></cim:Terminal>
        <cim:Terminal rdf:ID="_te3"><cim:Terminal.ConductingEquipment rdf:resource="#_sgt"/><cim:Terminal.TopologicalNode rdf:resource="#_tv"/></cim:Terminal>
        <cim:PowerTransformerEnd rdf:ID="_e1"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_sgt"/>
          <cim:TransformerEnd.endNumber>1</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te1"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>160</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>400</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:PowerTransformerEnd rdf:ID="_e2"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_sgt"/>
          <cim:TransformerEnd.endNumber>2</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te2"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>17.424</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>132</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:PowerTransformerEnd rdf:ID="_e3"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_sgt"/>
          <cim:TransformerEnd.endNumber>3</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te3"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>0.338</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>13</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:RatioTapChanger rdf:ID="_rtc"><cim:RatioTapChanger.TransformerEnd rdf:resource="#_e1"/>
          <cim:TapChanger.step>12</cim:TapChanger.step><cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>
          <cim:RatioTapChanger.stepVoltageIncrement>1.25</cim:RatioTapChanger.stepVoltageIncrement></cim:RatioTapChanger>
        <cim:SynchronousMachine rdf:ID="_g"><cim:RotatingMachine.p>60</cim:RotatingMachine.p></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_tg"><cim:Terminal.ConductingEquipment rdf:resource="#_g"/><cim:Terminal.TopologicalNode rdf:resource="#_hv"/></cim:Terminal>
        <cim:EnergyConsumer rdf:ID="_l132"><cim:EnergyConsumer.p>40</cim:EnergyConsumer.p><cim:EnergyConsumer.q>10</cim:EnergyConsumer.q></cim:EnergyConsumer>
        <cim:Terminal rdf:ID="_tl1"><cim:Terminal.ConductingEquipment rdf:resource="#_l132"/><cim:Terminal.TopologicalNode rdf:resource="#_lv"/></cim:Terminal>
        <cim:EnergyConsumer rdf:ID="_l13"><cim:EnergyConsumer.p>15</cim:EnergyConsumer.p><cim:EnergyConsumer.q>5</cim:EnergyConsumer.q></cim:EnergyConsumer>
        <cim:Terminal rdf:ID="_tl2"><cim:Terminal.ConductingEquipment rdf:resource="#_l13"/><cim:Terminal.TopologicalNode rdf:resource="#_tv"/></cim:Terminal>
        """ + Tail;

    [Fact]
    public void A_three_winding_transformer_keeps_its_tertiary()
    {
        CimNetwork net = CimImporter.Parse(ThreeWinding);
        CimTransformer sgt = Assert.Single(net.Transformers);
        // Before R064 only the first two ends were kept and the 13 kV node floated, with no note.
        Assert.Equal(new[] { "hv", "lv", "tv" }, sgt.Ends.Select(e => e.Node).ToArray());
        Assert.Equal(1.025, sgt.Ends[0].TapRatio, 12);   // the HV winding's own tap
        Assert.Equal(1.0, sgt.Ends[1].TapRatio);
        Assert.Empty(net.Notes);
    }

    [Fact]
    public void The_bridge_models_a_three_winding_unit_as_a_star_point_and_it_solves()
    {
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(ThreeWinding));
        Assert.Equal(4, m.BusCount);                        // three nodes + the star point
        Bus star = m.Buses.Single(b => b.Name == "SGT1 star point");
        Assert.Equal(3, m.Branches.Count(b => b.To == star.Id && b.IsTransformer));
        Branch hv = m.Branches.Single(b => b.Name == "SGT1/w1");
        Assert.Equal(0.1, hv.X, 12);                        // 160 ohm / (400^2/100)
        Assert.Equal(1.025, hv.TapRatio, 12);
        Assert.Equal(0.2, m.Branches.Single(b => b.Name == "SGT1/w3").X, 12);   // 0.338 / (13^2/100)
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    [Fact]
    public void A_three_winding_unit_round_trips_through_the_exporter()
    {
        CimNetwork net = CimImporter.Parse(ThreeWinding);
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        CimTransformer a = net.Transformers.Single(), b = rt.Transformers.Single();
        Assert.Equal(a.Ends.Select(e => e.Node), b.Ends.Select(e => e.Node));
        Assert.Equal(a.Ends[0].TapRatio, b.Ends[0].TapRatio, 12);
        Assert.Equal(a.Ends[2].X, b.Ends[2].X);
    }

    [Fact]
    public void Everything_left_out_is_listed_in_the_diagnostics()
    {
        string doc = Head + """
            <cim:TopologicalNode rdf:ID="_n1"/><cim:TopologicalNode rdf:ID="_n2"/>
            <cim:ACLineSegment rdf:ID="_dangling"><cim:IdentifiedObject.name>Spur</cim:IdentifiedObject.name></cim:ACLineSegment>
            <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_dangling"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
            <cim:EnergyConsumer rdf:ID="_orphan"><cim:IdentifiedObject.name>Orphan</cim:IdentifiedObject.name></cim:EnergyConsumer>
            <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_orphan"/></cim:Terminal>
            """ + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Empty(net.Branches);
        Assert.Empty(net.Loads);
        Assert.Contains(net.Notes, n => n.Contains("Spur"));
        Assert.Contains(net.Notes, n => n.Contains("Orphan"));
        Assert.Contains(net.Notes, n => n.Contains("_t2") || n.Contains("t2"));
    }

    [Fact]
    public void The_bridge_fails_loud_on_an_unresolved_node()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "A"), new CimBus("n2", "B") },
            new[] { new CimBranch("l1", "L", "n1", "n2", 0.01, 0.1, 0) },
            new[] { new CimLoad("ld", "Typo", "n3", 10, 2) },   // n3 is not a bus
            new[] { new CimGenerator("g", "G", "n1", 10) },
            Array.Empty<CimTransformer>());
        var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(net));
        Assert.Contains("n3", ex.Message);
    }
}
