using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-640: the CIM importer gave a load, machine or injection the node of its FIRST terminal and ignored any others, so
/// a document with two terminals on one such object was read as if it were attached to whichever came first. It is now
/// refused, naming the object and its terminals. Pinned: a load with two connected terminals (on one node or two) is
/// refused; a load with one connected and one disconnected terminal is still read, on the connected node; a synchronous
/// machine with two connected terminals is refused too.
/// </summary>
public class Gr640CimSingleTerminalTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";

    private static string T(string id, string equip, string node, string extra = "") =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/>{extra}</cim:Terminal>""";

    private static string Doc(string equipment) => Head + """
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>N2</cim:IdentifiedObject.name></cim:TopologicalNode>
        """ + equipment + Tail;

    private const string Load = """<cim:ConformLoad rdf:ID="_c1"><cim:IdentifiedObject.name>CL</cim:IdentifiedObject.name><cim:EnergyConsumer.p>10.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>2.25</cim:EnergyConsumer.q></cim:ConformLoad>""";

    [Theory]
    [InlineData("n1", "n2")]
    [InlineData("n1", "n1")]
    public void Gr640_a_load_with_two_connected_terminals_is_refused_by_name(string nodeA, string nodeB)
    {
        string doc = Doc(Load + T("t1", "c1", nodeA) + T("t2", "c1", nodeB));
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("ConformLoad 'CL'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 connected terminals", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr640_a_load_with_one_connected_and_one_disconnected_terminal_is_still_read()
    {
        string doc = Doc(Load + T("t1", "c1", "n2")
            + T("t2", "c1", "n1", "<cim:ACDCTerminal.connected>false</cim:ACDCTerminal.connected>"));
        CimNetwork net = CimImporter.Parse(doc);
        CimLoad l = Assert.Single(net.Loads);
        Assert.Equal(10.5, l.P);
    }

    [Fact]
    public void Gr640_a_synchronous_machine_with_two_connected_terminals_is_refused()
    {
        const string Machine = """<cim:SynchronousMachine rdf:ID="_m1"><cim:IdentifiedObject.name>M</cim:IdentifiedObject.name></cim:SynchronousMachine>""";
        string doc = Doc(Machine + T("t1", "m1", "n1") + T("t2", "m1", "n2"));
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("SynchronousMachine 'M'", ex.Message, StringComparison.Ordinal);
    }
}
