using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-641: a load or machine missing from a document's SSH profile drew or produced nothing (its p / q read as 0) even
/// when other objects carried SSH values. The owner's decision: an opt-in parse option first,
/// CimImportOptions.RequireSshSetPoints (off by default). Pinned: off, the missing load reads 0 as before; on, it is
/// refused naming the object and the missing attribute; a missing q is refused too; a synchronous machine without its
/// set-point is refused; an EQ-only document (no set-point anywhere) is never refused; a complete document reads the
/// same with the option on as off.
/// </summary>
public class Gr641CimRequireSshSetPointsTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";

    private static string T(string id, string equip, string node) =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/></cim:Terminal>""";

    private static string Doc(string equipment) => Head + """
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>N2</cim:IdentifiedObject.name></cim:TopologicalNode>
        """ + equipment + Tail;

    private const string WithSsh = """<cim:ConformLoad rdf:ID="_c1"><cim:IdentifiedObject.name>CL</cim:IdentifiedObject.name><cim:EnergyConsumer.p>10.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>2.25</cim:EnergyConsumer.q></cim:ConformLoad>""";
    private const string NoSsh = """<cim:ConformLoad rdf:ID="_c2"><cim:IdentifiedObject.name>BARE</cim:IdentifiedObject.name></cim:ConformLoad>""";
    private const string NoQ = """<cim:ConformLoad rdf:ID="_c3"><cim:IdentifiedObject.name>NOQ</cim:IdentifiedObject.name><cim:EnergyConsumer.p>4</cim:EnergyConsumer.p></cim:ConformLoad>""";

    private static readonly CimImportOptions Strict = CimImportOptions.Default with { RequireSshSetPoints = true };

    [Fact]
    public void Gr641_off_by_default_a_load_missing_from_the_ssh_profile_reads_zero_as_before()
    {
        Assert.False(CimImportOptions.Default.RequireSshSetPoints);
        CimNetwork net = CimImporter.Parse(Doc(WithSsh + T("t1", "c1", "n1") + NoSsh + T("t2", "c2", "n2")), null);
        CimLoad bare = Assert.Single(net.Loads, l => l.Name == "BARE");
        Assert.Equal((0.0, 0.0), (bare.P, bare.Q));
    }

    [Fact]
    public void Gr641_on_a_load_missing_from_an_existing_ssh_profile_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(Doc(WithSsh + T("t1", "c1", "n1") + NoSsh + T("t2", "c2", "n2")), Strict));
        Assert.Contains("ConformLoad 'BARE'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no SSH EnergyConsumer.p set-point", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr641_on_a_missing_q_is_refused_too()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(Doc(WithSsh + T("t1", "c1", "n1") + NoQ + T("t3", "c3", "n2")), Strict));
        Assert.Contains("ConformLoad 'NOQ'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no SSH EnergyConsumer.q set-point", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr641_on_a_synchronous_machine_without_its_set_point_is_refused()
    {
        const string Machine = """<cim:SynchronousMachine rdf:ID="_m1"><cim:IdentifiedObject.name>M</cim:IdentifiedObject.name></cim:SynchronousMachine>""";
        var ex = Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(Doc(WithSsh + T("t1", "c1", "n1") + Machine + T("t4", "m1", "n2")), Strict));
        Assert.Contains("SynchronousMachine 'M'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no SSH RotatingMachine.p set-point", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr641_on_an_eq_only_document_is_never_refused()
    {
        CimNetwork net = CimImporter.Parse(Doc(NoSsh + T("t2", "c2", "n2")), Strict);
        CimLoad bare = Assert.Single(net.Loads);
        Assert.Equal(0.0, bare.P);
    }

    [Fact]
    public void Gr641_on_a_complete_document_reads_exactly_as_with_it_off()
    {
        string doc = Doc(WithSsh + T("t1", "c1", "n1"));
        CimLoad off = Assert.Single(CimImporter.Parse(doc, null).Loads);
        CimLoad on = Assert.Single(CimImporter.Parse(doc, Strict).Loads);
        Assert.Equal(off, on);
        Assert.Equal((10.5, 2.25), (on.P, on.Q));
    }
}
