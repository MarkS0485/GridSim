using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The CGMES importer's transformer support: a two-winding PowerTransformer (impedance on its
/// PowerTransformerEnds, off-nominal tap on a RatioTapChanger) is folded into one tapped GridSim branch.
/// </summary>
public class CimTransformerImportTests
{
    // n1 (230 kV, gen) --[ transformer, tap 1.025 ]-- n2 (132 kV, load). The HV end carries the impedance
    // (x = 52.9 ohm at 230 kV, 100 MVA => 0.1 pu); the RatioTapChanger on end 1 sits 2 steps above neutral
    // at 1.25 %/step => tap 1.025.
    private const string Rdf = """
        <rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <cim:BaseVoltage rdf:ID="_bvHV"><cim:BaseVoltage.nominalVoltage>230</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
          <cim:BaseVoltage rdf:ID="_bvLV"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
          <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>HV</cim:IdentifiedObject.name>
            <cim:TopologicalNode.BaseVoltage rdf:resource="#_bvHV"/></cim:TopologicalNode>
          <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>LV</cim:IdentifiedObject.name>
            <cim:TopologicalNode.BaseVoltage rdf:resource="#_bvLV"/></cim:TopologicalNode>

          <cim:PowerTransformer rdf:ID="_tx1"><cim:IdentifiedObject.name>GSP-T1</cim:IdentifiedObject.name></cim:PowerTransformer>
          <cim:PowerTransformerEnd rdf:ID="_e1">
            <cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
            <cim:TransformerEnd.endNumber>1</cim:TransformerEnd.endNumber>
            <cim:TransformerEnd.Terminal rdf:resource="#_te1"/>
            <cim:PowerTransformerEnd.r>0.0</cim:PowerTransformerEnd.r>
            <cim:PowerTransformerEnd.x>52.9</cim:PowerTransformerEnd.x>
            <cim:PowerTransformerEnd.ratedU>230</cim:PowerTransformerEnd.ratedU>
          </cim:PowerTransformerEnd>
          <cim:PowerTransformerEnd rdf:ID="_e2">
            <cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
            <cim:TransformerEnd.endNumber>2</cim:TransformerEnd.endNumber>
            <cim:TransformerEnd.Terminal rdf:resource="#_te2"/>
            <cim:PowerTransformerEnd.r>0.0</cim:PowerTransformerEnd.r>
            <cim:PowerTransformerEnd.x>0.0</cim:PowerTransformerEnd.x>
            <cim:PowerTransformerEnd.ratedU>132</cim:PowerTransformerEnd.ratedU>
          </cim:PowerTransformerEnd>
          <cim:RatioTapChanger rdf:ID="_rtc">
            <cim:RatioTapChanger.TransformerEnd rdf:resource="#_e1"/>
            <cim:TapChanger.step>12</cim:TapChanger.step>
            <cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>
            <cim:RatioTapChanger.stepVoltageIncrement>1.25</cim:RatioTapChanger.stepVoltageIncrement>
          </cim:RatioTapChanger>
          <cim:Terminal rdf:ID="_te1"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
          <cim:Terminal rdf:ID="_te2"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>

          <cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>Gen1</cim:IdentifiedObject.name>
            <cim:RotatingMachine.p>30.0</cim:RotatingMachine.p></cim:SynchronousMachine>
          <cim:Terminal rdf:ID="_tg"><cim:Terminal.ConductingEquipment rdf:resource="#_g1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
          <cim:EnergyConsumer rdf:ID="_ld1"><cim:IdentifiedObject.name>Load1</cim:IdentifiedObject.name>
            <cim:EnergyConsumer.p>28.0</cim:EnergyConsumer.p><cim:EnergyConsumer.q>8.0</cim:EnergyConsumer.q></cim:EnergyConsumer>
          <cim:Terminal rdf:ID="_tl"><cim:Terminal.ConductingEquipment rdf:resource="#_ld1"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        </rdf:RDF>
        """;

    [Fact]
    public void Parses_a_two_winding_transformer_with_its_tap()
    {
        CimNetwork net = CimImporter.Parse(Rdf);

        CimTransformer tx = Assert.Single(net.Transformers);
        Assert.Equal("GSP-T1", tx.Name);
        Assert.Equal(2, tx.Ends.Count);
        Assert.Equal("n1", tx.Ends[0].Node);       // end 1 (from)
        Assert.Equal("n2", tx.Ends[1].Node);       // end 2 (to)
        Assert.Equal(52.9, tx.Ends[0].X, 9);
        Assert.Equal(230, tx.Ends[0].RatedU, 9);
        Assert.Equal(1.025, tx.TapRatio, 9);        // 1 + (12-10)*1.25/100
        Assert.Equal(0.0, tx.PhaseShiftDeg, 9);
        Assert.Empty(net.Branches);                 // no ACLineSegments - only the transformer
    }

    [Fact]
    public void Bridges_the_transformer_to_a_tapped_branch_that_solves()
    {
        CimNetwork net = CimImporter.Parse(Rdf);
        GridModel model = CimGridModelBridge.ToGridModel(net);

        Branch tx = Assert.Single(model.Branches);
        Assert.True(tx.IsTransformer);
        Assert.Equal(0.1, tx.X, 6);                 // 52.9 / (230^2/100)
        Assert.Equal(0.0, tx.R, 9);
        Assert.Equal(1.025, tx.TapRatio, 9);
        Assert.Equal("GSP-T1", tx.Name);

        Assert.True(new NewtonRaphsonPowerFlow().Solve(model).Converged);
    }
}
