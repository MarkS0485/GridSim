using System.Linq;
using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>The CIM/CGMES importer: parse striped RDF/XML (version-agnostic by local name) into buses and line
/// branches with their r/x/b and terminal connectivity.</summary>
public class CimImporterTests
{
    private const string Rdf = """
        <rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>Node1</cim:IdentifiedObject.name></cim:TopologicalNode>
          <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>Node2</cim:IdentifiedObject.name></cim:TopologicalNode>
          <cim:ACLineSegment rdf:ID="_l1">
            <cim:IdentifiedObject.name>Line1</cim:IdentifiedObject.name>
            <cim:ACLineSegment.r>0.5</cim:ACLineSegment.r>
            <cim:ACLineSegment.x>1.25</cim:ACLineSegment.x>
            <cim:ACLineSegment.bch>0.0003</cim:ACLineSegment.bch>
          </cim:ACLineSegment>
          <cim:Terminal rdf:ID="_t1">
            <cim:Terminal.ConductingEquipment rdf:resource="#_l1"/>
            <cim:Terminal.TopologicalNode rdf:resource="#_n1"/>
          </cim:Terminal>
          <cim:Terminal rdf:ID="_t2">
            <cim:Terminal.ConductingEquipment rdf:resource="#_l1"/>
            <cim:Terminal.TopologicalNode rdf:resource="#_n2"/>
          </cim:Terminal>
          <cim:EnergyConsumer rdf:ID="_ld1">
            <cim:IdentifiedObject.name>Load1</cim:IdentifiedObject.name>
            <cim:EnergyConsumer.p>12.5</cim:EnergyConsumer.p>
            <cim:EnergyConsumer.q>3.2</cim:EnergyConsumer.q>
          </cim:EnergyConsumer>
          <cim:Terminal rdf:ID="_t3">
            <cim:Terminal.ConductingEquipment rdf:resource="#_ld1"/>
            <cim:Terminal.TopologicalNode rdf:resource="#_n2"/>
          </cim:Terminal>
          <cim:SynchronousMachine rdf:ID="_g1">
            <cim:IdentifiedObject.name>Gen1</cim:IdentifiedObject.name>
            <cim:RotatingMachine.p>-40.0</cim:RotatingMachine.p>
            <cim:RotatingMachine.q>-12.5</cim:RotatingMachine.q>
          </cim:SynchronousMachine>
          <cim:Terminal rdf:ID="_t4">
            <cim:Terminal.ConductingEquipment rdf:resource="#_g1"/>
            <cim:Terminal.TopologicalNode rdf:resource="#_n1"/>
          </cim:Terminal>
        </rdf:RDF>
        """;

    [Fact]
    public void Parses_nodes_and_line_segments_with_connectivity()
    {
        CimNetwork net = CimImporter.Parse(Rdf);

        Assert.Equal(2, net.Buses.Count);
        Assert.Contains(net.Buses, b => b.MRID == "n1" && b.Name == "Node1");
        Assert.Contains(net.Buses, b => b.MRID == "n2" && b.Name == "Node2");

        Assert.Single(net.Branches);
        CimBranch line = net.Branches[0];
        Assert.Equal("Line1", line.Name);
        Assert.Equal("n1", line.FromNode);
        Assert.Equal("n2", line.ToNode);
        Assert.Equal(0.5, line.R, 9);
        Assert.Equal(1.25, line.X, 9);
        Assert.Equal(0.0003, line.B, 9);
    }

    [Fact]
    public void Parses_loads_and_generators_with_their_nodes()
    {
        CimNetwork net = CimImporter.Parse(Rdf);

        Assert.Single(net.Loads);
        CimLoad load = net.Loads[0];
        Assert.Equal("Load1", load.Name);
        Assert.Equal("n2", load.Node);
        Assert.Equal(12.5, load.P, 9);
        Assert.Equal(3.2, load.Q, 9);

        // Moved 2026-10 (Q2, authorised by Mark): the fixture used to give the generating machine
        // RotatingMachine.p = 40.0 and expect gen.P == 40.0, i.e. it encoded the defect (R267) of reading the CGMES
        // SSH value verbatim. CGMES SSH RotatingMachine.p / .q use the LOAD sign convention (ENTSO-E CGMES 2.4.15,
        // IEC 61970-600-2: "positive sign means flow into the equipment from the network"), so a machine generating
        // 40 MW and 12.5 MVAr is written p = -40.0, q = -12.5, and imports as generator-positive P = 40, Q = 12.5.
        Assert.Single(net.Generators);
        CimGenerator gen = net.Generators[0];
        Assert.Equal("Gen1", gen.Name);
        Assert.Equal("n1", gen.Node);
        Assert.Equal(40.0, gen.P, 9);
        Assert.Equal(12.5, gen.Q, 9);
    }

    [Fact]
    public void Import_export_round_trip_is_information_preserving()
    {
        CimNetwork original = CimImporter.Parse(Rdf);
        CimNetwork round = CimImporter.Parse(CimExporter.Export(original));

        Assert.Equal(original.Buses.Count, round.Buses.Count);
        Assert.Equal(original.Branches.Count, round.Branches.Count);
        CimBranch a = original.Branches[0], b = round.Branches[0];
        Assert.Equal(a.MRID, b.MRID);
        Assert.Equal(a.FromNode, b.FromNode);
        Assert.Equal(a.ToNode, b.ToNode);
        Assert.Equal(a.R, b.R, 9);
        Assert.Equal(a.X, b.X, 9);
        Assert.Equal(a.B, b.B, 9);
    }
}
