using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CIM2 stream (October 2026), N287: de-energised islands are left out of the bridged model with a note naming their
/// buses (instead of slack-less buses that GridModel.Validate refuses); the flow through a closed retained switch is
/// reported after the solve (or said not to be derivable, with the reason); enumerations written as element text are
/// read as well as rdf:resource ones. Numbers are compared exactly against the documented expression.
/// </summary>
public class CimIslandTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";
    private const string Ns = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    private static string Name(string n) => $"<cim:IdentifiedObject.name>{n}</cim:IdentifiedObject.name>";

    // ================================================================ bus-branch fixtures =============

    private const string Bv = """<cim:BaseVoltage rdf:ID="_bv132"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>""";

    private static string Tn(string id) =>
        $"""<cim:TopologicalNode rdf:ID="_{id}">{Name(id.ToUpperInvariant())}<cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>""";

    private static string T(string id, string equip, string node, int seq = 0, bool connected = true) =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/>{(seq > 0 ? $"<cim:ACDCTerminal.sequenceNumber>{seq}</cim:ACDCTerminal.sequenceNumber>" : "")}{(connected ? "" : "<cim:ACDCTerminal.connected>false</cim:ACDCTerminal.connected>")}</cim:Terminal>""";

    private static string BbLine(string id, string a, string b, bool bConnected = true) =>
        $"""<cim:ACLineSegment rdf:ID="_{id}">{Name(id)}<cim:ACLineSegment.r>1.7424</cim:ACLineSegment.r><cim:ACLineSegment.x>17.424</cim:ACLineSegment.x></cim:ACLineSegment>"""
        + T(id + "_1", id, a, 1) + T(id + "_2", id, b, 2, bConnected);

    private static string BbLoad(string id, string node, string p, string q) =>
        $"""<cim:EnergyConsumer rdf:ID="_{id}">{Name(id)}<cim:EnergyConsumer.p>{p}</cim:EnergyConsumer.p><cim:EnergyConsumer.q>{q}</cim:EnergyConsumer.q></cim:EnergyConsumer>""" + T(id + "_t", id, node);

    // A machine generating p MW with nothing said about its voltage control (read as regulating, D-DOC-104).
    private static string BbGen(string id, string node, string p = "-60") =>
        $"""<cim:SynchronousMachine rdf:ID="_{id}">{Name(id)}<cim:RotatingMachine.p>{p}</cim:RotatingMachine.p></cim:SynchronousMachine>""" + T(id + "_t", id, node);

    private static string BbDoc(params string[] parts) => Head + Bv + string.Concat(parts) + Tail;

    // n1 (machine G1) - L1 - n2 (load LD2) - L2 (open at n3) - n3 (load LD3, injection E3, a capacitor).
    private static readonly string[] Main = { Tn("n1"), Tn("n2"), BbGen("G1", "n1"), BbLine("L1", "n1", "n2"), BbLoad("LD2", "n2", "40", "10") };

    private static readonly string[] CutOff =
    {
        Tn("n3"), BbLine("L2", "n2", "n3", bConnected: false), BbLoad("LD3", "n3", "5", "1"),
        """<cim:EquivalentInjection rdf:ID="_E3"><cim:IdentifiedObject.name>E3</cim:IdentifiedObject.name><cim:EquivalentInjection.p>0</cim:EquivalentInjection.p></cim:EquivalentInjection>""" + T("E3_t", "E3", "n3"),
        """<cim:LinearShuntCompensator rdf:ID="_C3"><cim:ShuntCompensator.sections>1</cim:ShuntCompensator.sections><cim:LinearShuntCompensator.bPerSection>0.0001</cim:LinearShuntCompensator.bPerSection></cim:LinearShuntCompensator>""" + T("C3_t", "C3", "n3"),
    };

    [Fact]
    public void A_de_energised_island_is_left_out_with_a_note_naming_its_buses()
    {
        CimNetwork net = CimImporter.Parse(BbDoc(Main.Concat(CutOff).ToArray()));
        Assert.Equal(3, net.Buses.Count);   // the CIM network keeps what the document has
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net, notes: notes);
        Assert.Equal(new[] { "n3" }, bridged.DroppedNodes);
        Assert.False(bridged.BusIdOfNode.ContainsKey("n3"));
        Assert.Equal(2, bridged.Model.BusCount);
        Assert.Contains(notes, n => n.StartsWith("De-energised island: bus(es) 'N3' (n3) hold no slack and no voltage-controlling machine")
                                    && n.Contains("1 load(s) (5 MW, 1 MVAr)") && n.Contains("1 generator(s) or injection(s) (E3 (EquivalentInjection))")
                                    && n.Contains("0 line(s), 0 transformer(s), 1 shunt(s)") && n.Contains("type 4"));
        Assert.DoesNotContain(bridged.Model.Validate(), d => d.Severity == ValidationSeverity.Error);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(bridged.Model).Converged);

        // The model is the one the document gives without the island at all, element for element.
        GridModel without = CimGridModelBridge.ToGridModel(CimImporter.Parse(BbDoc(Main)));
        Assert.Equal(without.Buses, bridged.Model.Buses);
        Assert.Equal(without.Branches, bridged.Model.Branches);
        Assert.Equal(without.Generators, bridged.Model.Generators);
    }

    [Fact]
    public void Before_n287_the_island_made_validation_refuse_the_model_now_toGridModel_drops_it()
    {
        // ToGridModel is Bridge(...).Model: the same drop.
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(BbDoc(Main.Concat(CutOff).ToArray())));
        Assert.Equal(new[] { "N1", "N2" }, m.Buses.Select(b => b.Name).ToArray());
        Assert.DoesNotContain(m.Generators, g => g.Name == "E3");
    }

    [Fact]
    public void An_island_with_its_own_voltage_controlling_machine_is_kept_and_said()
    {
        string[] island = { Tn("n3"), BbLine("L2", "n2", "n3", bConnected: false), BbGen("G3", "n3", "-5"), BbLoad("LD3", "n3", "5", "1") };
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(BbDoc(Main.Concat(island).ToArray())), notes: notes);
        Assert.Empty(bridged.DroppedNodes);
        Assert.Equal(3, bridged.Model.BusCount);
        Assert.Contains(notes, n => n.StartsWith("An island with its own voltage-controlling machine(s) (bus 'N3'"));
        // Moved 2026-10 (CIM3 N296, Ruling R1: Mark delegated the EE calls 2026-10-04): an energised second island now
        // gets its own slack, elected by the main island's rule within the island (PSS/E gives each energised island its
        // own swing bus; powsybl solves each synchronous component with its own reference), so the model is no longer
        // refused. Before: "GridModel.Validate still refuses it (one synchronous island per solve): fail loud, not
        // dropped." with Assert.Contains(bridged.Model.Validate(), d => d.Severity == ValidationSeverity.Error).
        // After: no validation error, bus 3 (G3's) is the island's slack, and the model solves.
        Assert.DoesNotContain(bridged.Model.Validate(), d => d.Severity == ValidationSeverity.Error);
        Assert.Equal(BusType.Slack, bridged.Model.Buses[2].Type);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(bridged.Model).Converged);
    }

    [Fact]
    public void A_network_with_no_generator_keeps_every_bus_and_its_unreachable_island_is_refused()
    {
        // GR-150 (test changed 2026-10-19, with justification): the bridge now ends with the same GridModel.Validate
        // check as the JSON and MATPOWER loaders. With no generator nothing is dropped (every bus is kept, as before),
        // so bus 3, which no in-service branch joins to the angle reference on bus 1, is an island with no slack and its
        // power flow would be singular. The old test expected that unsolvable model to be returned; it is now refused,
        // naming the island, which is the fail-loud rule.
        string doc = BbDoc(Tn("n1"), Tn("n2"), Tn("n3"), BbLine("L1", "n1", "n2"), BbLoad("LD3", "n3", "5", "1"));
        var notes = new List<string>();
        var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.Bridge(CimImporter.Parse(doc), notes: notes));
        Assert.Contains("(bus 3) has no slack", ex.Message, StringComparison.Ordinal);
        Assert.Contains(notes, n => n.Contains("because the network has no generator", StringComparison.Ordinal));
    }

    [Fact]
    public void Bridge_gives_the_toGridModel_model_and_where_each_bus_went()
    {
        CimNetwork net = CimImporter.Parse(BbDoc(Main));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net);
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal(m.Buses, bridged.Model.Buses);
        Assert.Equal(m.Branches, bridged.Model.Branches);
        Assert.Same(net, bridged.Network);   // nothing dropped: the very same network object is solved
        Assert.Equal(new[] { ("n1", 1), ("n2", 2) },
            bridged.BusIdOfNode.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToArray());
    }

    // ================================================================ node-breaker fixtures ===========

    private static readonly string[] Level132 =
    {
        Bv,
        """<cim:VoltageLevel rdf:ID="_vl1"><cim:VoltageLevel.BaseVoltage rdf:resource="#_bv132"/></cim:VoltageLevel>""",
    };

    private static string Cn(string id) =>
        $"""<cim:ConnectivityNode rdf:ID="_{id}">{Name(id.ToUpperInvariant())}<cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_vl1"/></cim:ConnectivityNode>""";

    private static string Tc(string id, string equip, string cn, int seq = 0) =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.ConnectivityNode rdf:resource="#_{cn}"/>{(seq > 0 ? $"<cim:ACDCTerminal.sequenceNumber>{seq}</cim:ACDCTerminal.sequenceNumber>" : "")}</cim:Terminal>""";

    private static string Breaker(string id, string a, string b, bool open = false, bool retained = false) =>
        $"""<cim:Breaker rdf:ID="_{id}">{Name(id)}<cim:Switch.normalOpen>{(open ? "true" : "false")}</cim:Switch.normalOpen><cim:Switch.open>{(open ? "true" : "false")}</cim:Switch.open><cim:Switch.retained>{(retained ? "true" : "false")}</cim:Switch.retained></cim:Breaker>"""
        + Tc(id + "_t1", id, a, 1) + Tc(id + "_t2", id, b, 2);

    private static string Line(string id, string a, string b) =>
        $"""<cim:ACLineSegment rdf:ID="_{id}">{Name(id)}<cim:ACLineSegment.r>1.7424</cim:ACLineSegment.r><cim:ACLineSegment.x>17.424</cim:ACLineSegment.x></cim:ACLineSegment>"""
        + Tc(id + "_t1", id, a, 1) + Tc(id + "_t2", id, b, 2);

    private static string Load(string id, string cn, string p = "50", string q = "10") =>
        $"""<cim:EnergyConsumer rdf:ID="_{id}">{Name(id)}<cim:EnergyConsumer.p>{p}</cim:EnergyConsumer.p><cim:EnergyConsumer.q>{q}</cim:EnergyConsumer.q></cim:EnergyConsumer>""" + Tc(id + "_t", id, cn);

    private static string Gen(string id, string cn, string p = "-50") =>
        $"""<cim:SynchronousMachine rdf:ID="_{id}">{Name(id)}<cim:RotatingMachine.p>{p}</cim:RotatingMachine.p></cim:SynchronousMachine>""" + Tc(id + "_t", id, cn);

    private static string NbDoc(params string[] parts) => Head + string.Concat(Level132) + string.Concat(parts) + Tail;

    [Fact]
    public void A_load_cut_off_by_an_open_breaker_is_left_out()
    {
        // bb (G1) - L1 - far (LD); bb - open breaker B1 - x (LD2, alone once B1 is open).
        string doc = NbDoc(Cn("bb"), Cn("far"), Cn("x"), Gen("G1", "bb"), Line("L1", "bb", "far"), Load("LD", "far"),
            Breaker("B1", "bb", "x", open: true), Load("LD2", "x", "7", "2"));
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc), notes: notes);
        Assert.Equal(new[] { "x" }, bridged.DroppedNodes);
        Assert.Contains(notes, n => n.StartsWith("De-energised island: bus(es) 'X' (x)") && n.Contains("1 load(s) (7 MW, 2 MVAr)"));
        Assert.True(new NewtonRaphsonPowerFlow().Solve(bridged.Model).Converged);
    }

    // ================================================================ retained switch flows ===========

    [Fact]
    public void The_flow_through_a_retained_switch_is_the_balance_of_its_far_side()
    {
        // bba (G1, the slack) = retained BC = bbb - L1 - far (LD). Side B of BC is bbb alone: what BC carries from bba
        // to bbb is what leaves bbb into L1, exactly.
        string doc = NbDoc(Cn("bba"), Cn("bbb"), Cn("far"), Breaker("BC", "bba", "bbb", retained: true),
            Line("L1", "bbb", "far"), Gen("G1", "bba"), Load("LD", "far"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        Assert.True(r.Converged);
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(r));
        Assert.Equal(("BC", "bba", "bbb"), (f.MRID, f.NodeA, f.NodeB));
        Assert.Equal(r.LineFlows[0].PFromMw, f.PMw);
        Assert.Equal(r.LineFlows[0].QFromMvar, f.QMvar);
        Assert.Contains("P from the balance of the second side", f.Basis);
        Assert.True(f.PMw > 50.0);   // the load plus the line losses flow from bba to bbb
    }

    [Fact]
    public void When_the_far_side_holds_the_slack_the_near_side_balance_gives_the_flow()
    {
        // bba (LD1 only) = retained BC = bbb (G1, the slack) - L1 - far (LD). Side B has a solved output, side A does
        // not: the flow from bba to bbb is minus bba's withdrawal, i.e. -(30 MW, 5 MVAr).
        string doc = NbDoc(Cn("bba"), Cn("bbb"), Cn("far"), Breaker("BC", "bba", "bbb", retained: true),
            Line("L1", "bbb", "far"), Gen("G1", "bbb"), Load("LD1", "bba", "30", "5"), Load("LD", "far"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(new NewtonRaphsonPowerFlow().Solve(bridged.Model)));
        Assert.Equal(0.0 - (0.0 + (0.0 + 30.0)), f.PMw);
        Assert.Equal(0.0 - (0.0 + (0.0 + 5.0)), f.QMvar);
        Assert.Contains("P from the balance of the first side", f.Basis);
    }

    [Fact]
    public void Retained_switches_in_a_loop_have_no_determined_flow()
    {
        string doc = NbDoc(Cn("bba"), Cn("bbb"), Cn("far"), Breaker("BC1", "bba", "bbb", retained: true),
            Breaker("BC2", "bba", "bbb", retained: true), Line("L1", "bbb", "far"), Gen("G1", "bba"), Load("LD", "far"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        IReadOnlyList<CimSwitchFlow> flows = bridged.SwitchFlows(new NewtonRaphsonPowerFlow().Solve(bridged.Model));
        Assert.Equal(new[] { "BC1", "BC2" }, flows.Select(f => f.MRID).ToArray());
        Assert.All(flows, f =>
        {
            Assert.Null(f.PMw);
            Assert.Null(f.QMvar);
            Assert.Contains("loop of closed switches", f.Basis);
        });
    }

    [Fact]
    public void Solved_outputs_on_both_sides_make_the_flow_not_derivable_and_say_why()
    {
        string doc = NbDoc(Cn("bba"), Cn("bbb"), Cn("far"), Breaker("BC", "bba", "bbb", retained: true),
            Line("L1", "bbb", "far"), Gen("G1", "bba"), Gen("G2", "bbb", "-10"), Load("LD", "far"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(new NewtonRaphsonPowerFlow().Solve(bridged.Model)));
        Assert.Null(f.PMw);
        Assert.Null(f.QMvar);
        Assert.Contains("P not derivable", f.Basis);
        Assert.Contains("Q not derivable", f.Basis);
    }

    [Fact]
    public void A_retained_switch_in_a_de_energised_island_carries_nothing()
    {
        // bb (G1) - L1 - far (LD); far - open B1 - xa = retained BX = xb (LD2): xa and xb are de-energised.
        string doc = NbDoc(Cn("bb"), Cn("far"), Cn("xa"), Cn("xb"), Gen("G1", "bb"), Line("L1", "bb", "far"), Load("LD", "far"),
            Breaker("B1", "far", "xa", open: true), Breaker("BX", "xa", "xb", retained: true), Load("LD2", "xb", "7", "2"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        Assert.Equal(new[] { "xa", "xb" }, bridged.DroppedNodes);
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(new NewtonRaphsonPowerFlow().Solve(bridged.Model)));
        Assert.Equal(("BX", (double?)0.0, (double?)0.0), (f.MRID, f.PMw, f.QMvar));
        Assert.Contains("de-energised island", f.Basis);
    }

    [Fact]
    public void Switch_flows_refuse_a_result_of_another_model()
    {
        string doc = NbDoc(Cn("bba"), Cn("bbb"), Cn("far"), Breaker("BC", "bba", "bbb", retained: true),
            Line("L1", "bbb", "far"), Gen("G1", "bba"), Load("LD", "far"));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(doc));
        PowerFlowResult other = new NewtonRaphsonPowerFlow().Solve(CimGridModelBridge.ToGridModel(CimImporter.Parse(BbDoc(Main.Concat(new[] { Tn("n3"), BbLine("L3", "n2", "n3") }).ToArray()))));
        Assert.Throws<ArgumentException>(() => bridged.SwitchFlows(other));
    }

    // ================================================================ enumerations as element text =====

    private static string OneMachine(string unit, string extra = "") => BbDoc(Tn("n1"),
        $"""<cim:SynchronousMachine rdf:ID="_g"><cim:RotatingMachine.p>-10</cim:RotatingMachine.p><cim:RotatingMachine.GeneratingUnit rdf:resource="#_gu"/>{extra}</cim:SynchronousMachine>""" + T("g_t", "g", "n1"),
        unit);

    [Theory]
    [InlineData("""<cim:WindGeneratingUnit rdf:ID="_gu"><cim:WindGeneratingUnit.windGenUnitType>WindGenUnitKind.offshore</cim:WindGeneratingUnit.windGenUnitType></cim:WindGeneratingUnit>""", "wind-offshore")]
    [InlineData("""<cim:WindGeneratingUnit rdf:ID="_gu"><cim:WindGeneratingUnit.windGenUnitType>onshore</cim:WindGeneratingUnit.windGenUnitType></cim:WindGeneratingUnit>""", "wind-onshore")]
    [InlineData("""<cim:HydroGeneratingUnit rdf:ID="_gu"><cim:HydroGeneratingUnit.energyConversionCapability>HydroEnergyConversionKind.pumpAndGenerator</cim:HydroGeneratingUnit.energyConversionCapability></cim:HydroGeneratingUnit>""", "pumped-storage")]
    [InlineData("""<cim:ThermalGeneratingUnit rdf:ID="_gu"/><cim:FossilFuel rdf:ID="_ff"><cim:FossilFuel.fossilFuelType>FuelType.gas</cim:FossilFuel.fossilFuelType><cim:FossilFuel.ThermalGeneratingUnit rdf:resource="#_gu"/></cim:FossilFuel>""", "gas")]
    public void An_enumeration_written_as_element_text_is_read(string unit, string type)
    {
        // Before N287 only the rdf:resource form was read, so each of these fell back to "wind" / "hydro" / "thermal".
        Assert.Equal(type, CimImporter.Parse(OneMachine(unit)).Generators[0].Type);
    }

    [Fact]
    public void An_unknown_enumeration_value_in_text_form_is_refused_as_in_resource_form()
    {
        Assert.Contains("airborne", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(OneMachine(
            """<cim:WindGeneratingUnit rdf:ID="_gu"><cim:WindGeneratingUnit.windGenUnitType>airborne</cim:WindGeneratingUnit.windGenUnitType></cim:WindGeneratingUnit>"""))).Message);
    }

    [Theory]
    [InlineData("voltage")]
    [InlineData("RegulatingControlModeKind.voltage")]
    [InlineData("http://iec.ch/TC57/2013/CIM-schema-cim16#RegulatingControlModeKind.voltage")]
    public void A_regulating_control_mode_written_as_text_is_read(string mode)
    {
        // Before N287 a text-form mode was refused as missing ("has no RegulatingControl.mode").
        string rc = $"""<cim:RegulatingControl rdf:ID="_rc"><cim:RegulatingControl.mode>{mode}</cim:RegulatingControl.mode><cim:RegulatingControl.Terminal rdf:resource="#_g_t"/><cim:RegulatingControl.targetValue>133</cim:RegulatingControl.targetValue><cim:RegulatingControl.targetValueUnitMultiplier>k</cim:RegulatingControl.targetValueUnitMultiplier></cim:RegulatingControl>""";
        CimGenerator g = CimImporter.Parse(OneMachine(rc, """<cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rc"/>""")).Generators[0];
        Assert.Equal((true, (double?)133.0), (g.VoltageControl == true, g.TargetKv));
    }

    [Fact]
    public void A_text_form_unit_multiplier_other_than_kilo_is_refused()
    {
        string rc = $"""<cim:RegulatingControl rdf:ID="_rc"><cim:RegulatingControl.mode rdf:resource="{Ns}RegulatingControlModeKind.voltage"/><cim:RegulatingControl.targetValue>0.133</cim:RegulatingControl.targetValue><cim:RegulatingControl.targetValueUnitMultiplier>UnitMultiplier.M</cim:RegulatingControl.targetValueUnitMultiplier></cim:RegulatingControl>""";
        Assert.Contains("targetValueUnitMultiplier", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(
            OneMachine(rc, """<cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rc"/>"""))).Message);
    }
}
