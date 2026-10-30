using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Known limitation "CIM export details" (chapter 36, File formats): an identifier the exporter makes up (bv_&lt;kV&gt;,
/// t_&lt;mRID&gt;_1, and the like) could equal a source mRID, or two source objects could share an mRID, and the export
/// was written regardless; only the re-import refused it (an rdf:ID defined twice in one document). The exporter now
/// refuses such a network itself, naming the identifier and both element classes. A network without a clash exports
/// and re-imports as before.
/// </summary>
public sealed class CimExportIdClashTests
{
    private static CimNetwork TwoBuses(string firstBus, string secondBus) => new(
        new[] { new CimBus(firstBus, "A", 132.0), new CimBus(secondBus, "B", 132.0) },
        new[] { new CimBranch("l1", "L1", firstBus, secondBus, 1.7424, 17.424, 0.0) },
        Array.Empty<CimLoad>(), Array.Empty<CimGenerator>(), Array.Empty<CimTransformer>());

    [Fact]
    public void A_bus_mrid_equal_to_a_made_up_base_voltage_id_is_refused_at_export()
    {
        // The 132 kV base voltage is written as rdf:ID "_bv_132"; a bus whose mRID is "bv_132" is written as "_bv_132" too.
        var ex = Assert.Throws<InvalidOperationException>(() => CimExporter.Export(TwoBuses("bv_132", "n2")));
        Assert.Contains("'_bv_132'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BaseVoltage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TopologicalNode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bus_mrid_equal_to_a_made_up_terminal_id_is_refused_at_export()
    {
        // Line l1's first terminal is written as "_t_l1_1"; a bus with the mRID "t_l1_1" is written as "_t_l1_1" first.
        var ex = Assert.Throws<InvalidOperationException>(() => CimExporter.Export(TwoBuses("t_l1_1", "n2")));
        Assert.Contains("'_t_l1_1'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TopologicalNode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Terminal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_objects_sharing_an_mrid_are_refused_at_export()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "A", 132.0), new CimBus("n2", "B", 132.0) },
            new[]
            {
                new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0),
                new CimBranch("l1", "L1 again", "n1", "n2", 1.7424, 17.424, 0.0),
            },
            Array.Empty<CimLoad>(), Array.Empty<CimGenerator>(), Array.Empty<CimTransformer>());
        var ex = Assert.Throws<InvalidOperationException>(() => CimExporter.Export(net));
        Assert.Contains("'_l1'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ACLineSegment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_network_without_a_clash_still_exports_and_reads_back()
    {
        CimNetwork net = TwoBuses("n1", "n2");
        string xml = CimExporter.Export(net);
        CimNetwork back = CimImporter.Parse(xml);
        Assert.Equal(net.Buses.Select(b => b.MRID), back.Buses.Select(b => b.MRID));
        Assert.Equal(net.Branches.Single(), back.Branches.Single());
    }
}
