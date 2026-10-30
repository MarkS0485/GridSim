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
/// CIM1 stream (October 2026): N253, the CIM topology processor (ConnectivityNodes joined across closed switches into
/// buses, deterministic, checked against the TopologicalNodes; closed retained switches solved as one bus by the
/// bridge), and N254 (SvTapStep positions, PhaseTapChanger xMin / xMax, the generator type from the GeneratingUnit
/// class). Numbers are compared exactly (Assert.Equal on doubles) against the expression the importer is documented
/// to evaluate. The ENTSO-E conformity benchmark (MiniGrid / SmallGrid node-breaker) is at the end and runs only when
/// GRIDSIM_CGMES_CONFORMITY_DIR names a copy of the models (they are not bundled; see the CIM1 notes).
/// </summary>
public class CimTopologyProcessorTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";
    private const string Ns = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    private static string Bool(bool b) => b ? "true" : "false";

    private static string Name(string n) => $"<cim:IdentifiedObject.name>{n}</cim:IdentifiedObject.name>";

    // A 132 kV base voltage, a voltage level using it, and two bays in that level.
    private static readonly string[] Level132 =
    {
        """<cim:BaseVoltage rdf:ID="_bv132"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>""",
        """<cim:VoltageLevel rdf:ID="_vl1"><cim:VoltageLevel.BaseVoltage rdf:resource="#_bv132"/></cim:VoltageLevel>""",
        """<cim:Bay rdf:ID="_bay1"><cim:Bay.VoltageLevel rdf:resource="#_vl1"/></cim:Bay>""",
        """<cim:Bay rdf:ID="_bay2"><cim:Bay.VoltageLevel rdf:resource="#_vl1"/></cim:Bay>""",
    };

    // A ConnectivityNode in a container (a VoltageLevel or a Bay), optionally mapped onto a TopologicalNode.
    private static string Cn(string id, string container = "vl1", string? tn = null) =>
        $"""<cim:ConnectivityNode rdf:ID="_{id}">{Name(id.ToUpperInvariant())}<cim:ConnectivityNode.ConnectivityNodeContainer rdf:resource="#_{container}"/>{(tn is null ? "" : $"""<cim:ConnectivityNode.TopologicalNode rdf:resource="#_{tn}"/>""")}</cim:ConnectivityNode>""";

    // A terminal joining equipment to a ConnectivityNode (with an optional sequence number and extra content).
    private static string Tc(string id, string equip, string cn, int seq = 0, string extra = "") =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.ConnectivityNode rdf:resource="#_{cn}"/>{(seq > 0 ? $"<cim:ACDCTerminal.sequenceNumber>{seq}</cim:ACDCTerminal.sequenceNumber>" : "")}{extra}</cim:Terminal>""";

    // A switch of the given class between two ConnectivityNodes. open = null: no SSH Switch.open.
    private static string Sw(string cls, string id, string a, string b, bool normalOpen = false, bool? open = null,
        bool? retained = false, string extra = "", string termExtraB = "") =>
        $"""<cim:{cls} rdf:ID="_{id}">{Name(id)}<cim:Switch.normalOpen>{Bool(normalOpen)}</cim:Switch.normalOpen>{(retained is bool r ? $"<cim:Switch.retained>{Bool(r)}</cim:Switch.retained>" : "")}{(open is bool o ? $"<cim:Switch.open>{Bool(o)}</cim:Switch.open>" : "")}{extra}</cim:{cls}>"""
        + Tc(id + "_t1", id, a, 1) + Tc(id + "_t2", id, b, 2, termExtraB);

    // A line (ohms, written as invariant text) between two ConnectivityNodes, terminals numbered 1 and 2 so its
    // direction is defined. 1.7424 / 17.424 ohm on 132 kV and 100 MVA is r = 0.01, x = 0.1 pu.
    private static string Line(string id, string a, string b, string r = "1.7424", string x = "17.424") =>
        $"""<cim:ACLineSegment rdf:ID="_{id}">{Name(id)}<cim:ACLineSegment.r>{r}</cim:ACLineSegment.r><cim:ACLineSegment.x>{x}</cim:ACLineSegment.x></cim:ACLineSegment>"""
        + Tc(id + "_t1", id, a, 1) + Tc(id + "_t2", id, b, 2);

    private static string Busbar(string id, string name, string cn) =>
        $"""<cim:BusbarSection rdf:ID="_{id}">{Name(name)}</cim:BusbarSection>""" + Tc(id + "_t", id, cn);

    private static string Load(string id, string cn, string p = "50", string q = "10") =>
        $"""<cim:EnergyConsumer rdf:ID="_{id}">{Name(id)}<cim:EnergyConsumer.p>{p}</cim:EnergyConsumer.p><cim:EnergyConsumer.q>{q}</cim:EnergyConsumer.q></cim:EnergyConsumer>"""
        + Tc(id + "_t", id, cn);

    // A machine generating 50 MW (CGMES load sign: p = -50).
    private static string Gen(string id, string cn, string unit = "") =>
        $"""<cim:SynchronousMachine rdf:ID="_{id}">{Name(id)}<cim:RotatingMachine.p>-50</cim:RotatingMachine.p>{unit}</cim:SynchronousMachine>"""
        + Tc(id + "_t", id, cn);

    private static string Doc(IEnumerable<string> parts) => Head + string.Concat(parts) + Tail;

    // A small substation: busbar BB1 (node bb) with a generator; a line bay x1 - D1 - x2 - B1 - bb; the line L1 runs
    // from x1 to a remote node far carrying a 50 MW load. All 132 kV; x1 and x2 are in a Bay.
    private static List<string> Substation(bool d1Closed = true, bool b1Closed = true) =>
        Level132.Concat(new[]
        {
            Cn("bb"), Cn("x1", "bay1"), Cn("x2", "bay1"), Cn("far"),
            Busbar("bbs1", "BB1", "bb"),
            Sw("Disconnector", "D1", "x1", "x2", normalOpen: !d1Closed),
            Sw("Breaker", "B1", "x2", "bb", normalOpen: !b1Closed),
            Line("L1", "x1", "far"),
            Gen("G1", "bb"),
            Load("LD", "far"),
        }).ToList();

    // ================================================================ N253: joining across closed switches =====

    [Fact]
    public void Closed_switches_join_connectivity_nodes_into_one_bus()
    {
        // Before N253: four ConnectivityNodes were four buses, and the line's end (x1) was cut off from the
        // generator's busbar (bb): two islands.
        CimNetwork net = CimImporter.Parse(Doc(Substation()));
        Assert.Equal(new[] { "bb", "far" }, net.Buses.Select(b => b.MRID).ToArray());
        Assert.Equal(new[] { "BB1", "FAR" }, net.Buses.Select(b => b.Name).ToArray());
        Assert.Equal(new[] { 132.0, 132.0 }, net.Buses.Select(b => b.BaseKv).ToArray());
        CimBranch line = Assert.Single(net.Branches);
        Assert.Equal(("bb", "far"), (line.FromNode, line.ToNode));
        Assert.Equal("bb", Assert.Single(net.Generators).Node);
        Assert.Empty(net.Couplers);
        Assert.Contains(net.Notes, n => n.StartsWith("Node-breaker view: 4 ConnectivityNode(s) form 2 bus(es); 2 closed switch(es)"));
        Assert.DoesNotContain(net.Notes, n => n.Contains("not joined"));
    }

    [Fact]
    public void The_joined_network_bridges_and_solves()
    {
        GridModel model = CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Substation())));
        Assert.Equal(2, model.BusCount);
        Assert.DoesNotContain(model.Validate(), d => d.Severity == ValidationSeverity.Error);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(r.Converged, r.Status.ToString());
    }

    [Fact]
    public void An_open_switch_keeps_its_nodes_apart()
    {
        CimNetwork net = CimImporter.Parse(Doc(Substation(b1Closed: false)));
        // bb alone; x1 + x2 joined by D1 (named after its smallest mRID, X1); far alone.
        Assert.Equal(new[] { "bb", "far", "x1" }, net.Buses.Select(b => b.MRID).ToArray());
        Assert.Equal(new[] { "BB1", "FAR", "X1" }, net.Buses.Select(b => b.Name).ToArray());
        Assert.Equal(("x1", "far"), (net.Branches[0].FromNode, net.Branches[0].ToNode));
        Assert.Contains(net.Notes, n => n.Contains("1 closed switch(es) join nodes, 1 switch(es) are open"));
    }

    [Theory]
    [InlineData(false, true, 3)]    // normally closed, open in this case (SSH): apart
    [InlineData(true, false, 2)]    // normally open, closed in this case (SSH): joined
    [InlineData(false, false, 2)]
    [InlineData(true, true, 3)]
    public void The_SSH_state_wins_over_the_normal_state(bool normalOpen, bool open, int buses)
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] = Sw("Breaker", "B1", "x2", "bb", normalOpen: normalOpen, open: open);
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(buses, net.Buses.Count);
        // Only D1 (no SSH state in this fixture) falls back to its normal state; B1's SSH state is used.
        Assert.Contains(net.Notes, n => n.StartsWith("1 switch(es) have no Switch.open"));
    }

    [Fact]
    public void Without_SSH_the_normal_state_is_used_and_listed()
    {
        CimNetwork net = CimImporter.Parse(Doc(Substation()));
        Assert.Contains(net.Notes, n => n == "2 switch(es) have no Switch.open (no SSH profile); their normal state Switch.normalOpen (EQ) is used.");
    }

    [Fact]
    public void A_switch_with_no_state_at_all_is_refused_by_name()
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] =
            """<cim:Breaker rdf:ID="_B1"><cim:IdentifiedObject.name>B1</cim:IdentifiedObject.name></cim:Breaker>""" +
            Tc("B1_t1", "B1", "x2", 1) + Tc("B1_t2", "B1", "bb", 2);
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(parts)));
        Assert.Contains("Breaker 'B1'", ex.Message);
        Assert.Contains("Switch.normalOpen", ex.Message);
    }

    [Fact]
    public void Several_profile_documents_merge_and_the_SSH_document_opens_a_switch()
    {
        // EQ alone: B1 normally closed, one bus with the line. EQ + an SSH document that opens B1: two buses there.
        string eq = Doc(Substation());
        string ssh = Head + """<cim:Breaker rdf:about="#_B1"><cim:Switch.open>true</cim:Switch.open></cim:Breaker>""" + Tail;
        Assert.Equal(2, CimImporter.Parse(new[] { eq }).Buses.Count);
        CimNetwork net = CimImporter.Parse(new[] { eq, ssh });
        Assert.Equal(new[] { "bb", "far", "x1" }, net.Buses.Select(b => b.MRID).ToArray());
        // One switch (D1) still uses its normal state.
        Assert.Contains(net.Notes, n => n.StartsWith("1 switch(es) have no Switch.open"));
    }

    [Fact]
    public void Several_documents_with_one_document_equal_the_single_document_parse()
    {
        string doc = Doc(Substation());
        CimNetwork a = CimImporter.Parse(doc), b = CimImporter.Parse(new[] { doc });
        Assert.Equal(a.Buses, b.Buses);
        Assert.Equal(a.Branches, b.Branches);
        Assert.Equal(a.Notes, b.Notes);
    }

    [Fact]
    public void No_documents_is_an_argument_error()
    {
        Assert.Throws<ArgumentException>(() => CimImporter.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Bus_order_names_and_branch_ends_do_not_depend_on_the_document_order()
    {
        // The same elements written in the opposite order: the union-find roots at the smallest mRID and the buses
        // are sorted by mRID, so the result is identical (the line's ends are fixed by its sequence numbers).
        List<string> parts = Substation();
        CimNetwork forward = CimImporter.Parse(Doc(parts));
        parts.Reverse();
        CimNetwork backward = CimImporter.Parse(Doc(parts));
        Assert.Equal(forward.Buses, backward.Buses);
        Assert.Equal(forward.Branches, backward.Branches);
        Assert.Equal(forward.Loads, backward.Loads);
        Assert.Equal(forward.Generators.Select(g => (g.MRID, g.Node)), backward.Generators.Select(g => (g.MRID, g.Node)));
    }

    [Fact]
    public void A_bus_is_named_after_its_smallest_mRID_busbar_and_keeps_its_smallest_node_mRID()
    {
        List<string> parts = Substation();
        parts.Add(Busbar("abs0", "BB0", "x2"));      // a second busbar on the same bus, with a smaller mRID
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(("bb", "BB0"), (net.Buses[0].MRID, net.Buses[0].Name));
    }

    [Fact]
    public void The_base_voltage_is_read_through_a_bay()
    {
        // x1 / x2 are in bay1 (Bay.VoltageLevel -> vl1 -> 132 kV). With the breaker open they are their own bus,
        // whose base voltage can only come through the bay.
        CimNetwork net = CimImporter.Parse(Doc(Substation(b1Closed: false)));
        Assert.Equal(132.0, net.Buses.Single(b => b.MRID == "x1").BaseKv);
    }

    [Fact]
    public void A_closed_switch_between_two_base_voltages_is_refused()
    {
        List<string> parts = Substation();
        parts.Add("""<cim:BaseVoltage rdf:ID="_bv33"><cim:BaseVoltage.nominalVoltage>33</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>""");
        parts.Add("""<cim:VoltageLevel rdf:ID="_vl33"><cim:VoltageLevel.BaseVoltage rdf:resource="#_bv33"/></cim:VoltageLevel>""");
        parts.Add(Cn("lv", "vl33"));
        parts.Add(Sw("Breaker", "BX", "bb", "lv"));
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(parts)));
        Assert.Contains("does not change voltage", ex.Message);
        Assert.Contains("LV", ex.Message);
    }

    [Fact]
    public void A_closed_switch_with_a_disconnected_terminal_conducts_nothing()
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] = Sw("Breaker", "B1", "x2", "bb",
            termExtraB: "<cim:ACDCTerminal.connected>false</cim:ACDCTerminal.connected>");
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(3, net.Buses.Count);
        Assert.Contains(net.Notes, n => n.Contains("Breaker 'B1'") && n.Contains("treated as open"));
    }

    [Fact]
    public void A_closed_switch_out_of_service_conducts_nothing()
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] = Sw("Breaker", "B1", "x2", "bb",
            extra: "<cim:Equipment.inService>false</cim:Equipment.inService>");
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(3, net.Buses.Count);
        Assert.Contains(net.Notes, n => n.Contains("Breaker 'B1'") && n.Contains("out of service"));
    }

    [Theory]
    [InlineData("Switch")]
    [InlineData("LoadBreakSwitch")]
    [InlineData("Fuse")]
    [InlineData("Jumper")]
    [InlineData("Recloser")]
    [InlineData("Sectionaliser")]
    [InlineData("DisconnectingCircuitBreaker")]
    public void Every_switch_class_joins_when_closed(string cls)
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] = Sw(cls, "B1", "x2", "bb");
        Assert.Equal(2, CimImporter.Parse(Doc(parts)).Buses.Count);
    }

    [Fact]
    public void An_earthing_switch_never_joins_and_a_closed_one_is_listed()
    {
        List<string> parts = Substation();
        parts.Add(Cn("earth"));
        parts.Add(Sw("GroundDisconnector", "ES1", "far", "earth"));
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(new[] { "bb", "earth", "far" }, net.Buses.Select(b => b.MRID).ToArray());
        Assert.Contains(net.Notes, n => n.Contains("GroundDisconnector 'ES1'") && n.Contains("earthing switch"));
    }

    [Fact]
    public void A_switch_without_a_retained_flag_is_read_as_not_retained_and_listed()
    {
        List<string> parts = Substation();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_B1\""))] = Sw("Breaker", "B1", "x2", "bb", retained: null);
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Equal(2, net.Buses.Count);
        Assert.Contains(net.Notes, n => n.StartsWith("1 switch(es) have no Switch.retained"));
    }

    [Fact]
    public void A_line_bypassed_by_closed_switches_is_left_out_and_listed()
    {
        List<string> parts = Substation();
        parts.Add(Sw("Breaker", "BYP", "x1", "far"));   // a closed switch in parallel with L1
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Single(net.Buses);
        Assert.Empty(net.Branches);
        Assert.Contains(net.Notes, n => n.Contains("ACLineSegment 'L1'") && n.Contains("bypass"));
    }

    // ================================================================ N253: retained switches ==================

    // Two busbar sections A (with the generator) and B joined by a bus coupler; B feeds the line to far.
    private static List<string> Coupled(bool retained, bool closed = true) =>
        Level132.Concat(new[]
        {
            Cn("bba"), Cn("bbb"), Cn("far"),
            Busbar("bsa", "BB-A", "bba"), Busbar("bsb", "BB-B", "bbb"),
            Sw("Breaker", "BC", "bba", "bbb", normalOpen: !closed, retained: retained),
            Line("L1", "bbb", "far"),
            Gen("G1", "bba"),
            Load("LD", "far"),
        }).ToList();

    [Fact]
    public void A_closed_retained_switch_keeps_two_buses_and_is_listed()
    {
        // CGMES: a retained switch is kept in the bus-branch model, so its two sides are two TopologicalNodes.
        CimNetwork net = CimImporter.Parse(Doc(Coupled(retained: true)));
        Assert.Equal(new[] { "bba", "bbb", "far" }, net.Buses.Select(b => b.MRID).ToArray());
        CimSwitch bc = Assert.Single(net.Couplers);
        Assert.Equal(new CimSwitch("BC", "BC", "Breaker", "bba", "bbb", true), bc);
        Assert.Contains(net.Notes, n => n.Contains("Breaker 'BC'") && n.Contains("closed and retained"));
    }

    [Fact]
    public void The_bridge_solves_a_retained_switch_as_one_bus_exactly_as_a_merged_one()
    {
        // Retained: three CIM buses, joined by the bridge into two GridModel buses. Not retained: the processor
        // merges, two CIM buses. The two GridModels are the same model, so the solves are identical, bit for bit.
        var notes = new List<string>();
        GridModel retained = CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Coupled(retained: true))), notes: notes);
        GridModel merged = CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Coupled(retained: false))));
        Assert.Equal(2, retained.BusCount);
        Assert.Equal(merged.Buses, retained.Buses);
        Assert.Equal(merged.Branches, retained.Branches);
        Assert.Contains(notes, n => n.Contains("Switch BC (Breaker, closed, retained) joins bus 'BB-A' and bus 'BB-B'"));
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(retained), b = new NewtonRaphsonPowerFlow().Solve(merged);
        Assert.True(a.Converged);
        Assert.Equal(b.Vm, a.Vm);
        Assert.Equal(b.VaDeg, a.VaDeg);
    }

    [Fact]
    public void An_open_retained_switch_joins_nothing()
    {
        CimNetwork net = CimImporter.Parse(Doc(Coupled(retained: true, closed: false)));
        Assert.Equal(3, net.Buses.Count);
        Assert.Empty(net.Couplers);
    }

    [Fact]
    public void A_retained_switch_survives_an_export_round_trip()
    {
        CimNetwork net = CimImporter.Parse(Doc(Coupled(retained: true)));
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Couplers, rt.Couplers);
        Assert.Equal(net.Buses.Select(b => b.MRID), rt.Buses.Select(b => b.MRID));
    }

    [Fact]
    public void A_network_without_switches_exports_without_switch_elements()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "A", 132), new CimBus("n2", "B", 132) },
            new[] { new CimBranch("l1", "L", "n1", "n2", 1, 10, 0) },
            Array.Empty<CimLoad>(), Array.Empty<CimGenerator>(), Array.Empty<CimTransformer>());
        string xml = CimExporter.Export(net);
        Assert.DoesNotContain("Switch.", xml);
        Assert.DoesNotContain("GeneratingUnit", xml);
    }

    // ================================================================ N253: check against the TopologicalNodes ==

    // The substation of Coupled(false) with a TP: TopologicalNodes "Bus A" (bba, x-side) and "Bus B" (far side).
    // tpOfBbb chooses which TopologicalNode the TP puts bbb on (consistent: tnA, since BC is closed).
    private static List<string> WithTp(string tpOfBbb = "tnA") =>
        Level132.Concat(new[]
        {
            """<cim:TopologicalNode rdf:ID="_tnA"><cim:IdentifiedObject.name>BB-A</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>""",
            """<cim:TopologicalNode rdf:ID="_tnA2"><cim:IdentifiedObject.name>BB-A2</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>""",
            """<cim:TopologicalNode rdf:ID="_tnF"><cim:IdentifiedObject.name>FAR</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>""",
            Cn("bba", tn: "tnA"), Cn("bbb", tn: tpOfBbb), Cn("far", tn: "tnF"),
            Busbar("bsa", "BB-A", "bba"), Busbar("bsb", "BB-B", "bbb"),
            Sw("Breaker", "BC", "bba", "bbb"),
            Line("L1", "bbb", "far"),
            Gen("G1", "bba"),
            Load("LD", "far"),
        }).ToList();

    [Fact]
    public void A_consistent_TP_is_used_as_before_and_the_check_says_they_agree()
    {
        CimNetwork net = CimImporter.Parse(Doc(WithTp()));
        // Auto with TopologicalNodes: the TP is the bus set (document order), as before N253.
        Assert.Equal(new[] { "tnA", "tnA2", "tnF" }, net.Buses.Select(b => b.MRID).ToArray());
        Assert.Contains(net.Notes, n => n.StartsWith("Topology check: the buses derived from the switch states agree one to one"));
        Assert.Empty(net.Couplers);
    }

    [Fact]
    public void A_TP_that_disagrees_with_a_closed_switch_is_reported_by_name_and_the_switch_is_joined()
    {
        // The TP puts bbb on its own TopologicalNode BB-A2 although breaker BC (closed, not retained) joins it to
        // bba. Before N253 the switch was dropped and BB-A2 was an island; now the disagreement is listed by name
        // and BC is a closed switch the bridge joins.
        CimNetwork net = CimImporter.Parse(Doc(WithTp(tpOfBbb: "tnA2")));
        Assert.Contains(net.Notes, n => n.StartsWith("Topology check: the switch states join into bus 'BB-A' (bba)")
                                        && n.Contains("'BB-A' (tnA)") && n.Contains("'BB-A2' (tnA2)"));
        CimSwitch bc = Assert.Single(net.Couplers);
        Assert.Equal(("tnA", "tnA2", false), (bc.NodeA, bc.NodeB, bc.Retained));
        Assert.Contains(net.Notes, n => n.Contains("Breaker 'BC'") && n.Contains("the topology (TP) disagrees with the switch state"));
    }

    [Fact]
    public void A_TP_that_joins_across_an_open_switch_is_reported_by_name()
    {
        List<string> parts = WithTp();
        parts[parts.FindIndex(p => p.Contains("rdf:ID=\"_BC\""))] = Sw("Breaker", "BC", "bba", "bbb", open: true);
        CimNetwork net = CimImporter.Parse(Doc(parts));
        Assert.Contains(net.Notes, n => n.StartsWith("Topology check: TopologicalNode 'BB-A' (tnA) holds ConnectivityNodes that the switch states put on 2 different buses")
                                        && n.Contains("'BB-A' (bba)") && n.Contains("'BB-B' (bbb)"));
    }

    [Fact]
    public void The_TP_view_and_the_switch_view_solve_to_the_same_voltages_bit_for_bit()
    {
        // The same document read through its TopologicalNodes (Auto) and through its switch states (Switches). With
        // the TP disagreeing (bbb on BB-A2), the bridge joins BB-A2 to BB-A across the closed breaker; the switch view
        // merges them directly. Both are the same two-bus model, so every voltage is identical, bit for bit.
        string doc = Doc(WithTp(tpOfBbb: "tnA2"));
        GridModel tp = CimGridModelBridge.ToGridModel(CimImporter.Parse(doc));
        CimNetwork switchView = CimImporter.Parse(doc, new CimImportOptions(Topology: CimTopology.Switches));
        Assert.Equal(new[] { "bba", "far" }, switchView.Buses.Select(b => b.MRID).ToArray());
        Assert.Contains(switchView.Notes, n => n.StartsWith("Node-breaker view chosen (CimTopology.Switches): the document's 3 TopologicalNode(s)"));
        GridModel sw = CimGridModelBridge.ToGridModel(switchView);
        Assert.Equal(tp.Buses, sw.Buses);
        Assert.Equal(tp.Branches, sw.Branches);
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(tp), b = new NewtonRaphsonPowerFlow().Solve(sw);
        Assert.True(a.Converged);
        Assert.Equal(a.Vm, b.Vm);
        Assert.Equal(a.VaDeg, b.VaDeg);
    }

    [Fact]
    public void Forcing_a_view_the_document_does_not_have_is_refused()
    {
        Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(Doc(Substation()), new CimImportOptions(Topology: CimTopology.TopologicalNodes)));
        string tpOnly = Head + """<cim:TopologicalNode rdf:ID="_n1"/>""" + Tail;
        Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(tpOnly, new CimImportOptions(Topology: CimTopology.Switches)));
    }

    // ================================================================ N254: SvTapStep ==========================

    // A 132/33 kV transformer with a linear ratio changer on end 1: neutral 0, 1.25 % per step, steps -10..10.
    private static string Transformer(string changerSteps, string sv = "") => Head + """
        <cim:BaseVoltage rdf:ID="_bvHV"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:BaseVoltage rdf:ID="_bvLV"><cim:BaseVoltage.nominalVoltage>33</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:TopologicalNode rdf:ID="_n1"><cim:TopologicalNode.BaseVoltage rdf:resource="#_bvHV"/></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:TopologicalNode.BaseVoltage rdf:resource="#_bvLV"/></cim:TopologicalNode>
        <cim:PowerTransformer rdf:ID="_tx1"><cim:IdentifiedObject.name>T1</cim:IdentifiedObject.name></cim:PowerTransformer>
        <cim:PowerTransformerEnd rdf:ID="_e1"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
          <cim:TransformerEnd.endNumber>1</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te1"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>17.424</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>132</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:PowerTransformerEnd rdf:ID="_e2"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
          <cim:TransformerEnd.endNumber>2</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te2"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>0</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>33</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:Terminal rdf:ID="_te1"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:Terminal rdf:ID="_te2"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        """ + changerSteps + sv + Tail;

    private static string RatioChanger(string steps) =>
        $"""<cim:RatioTapChanger rdf:ID="_rtc"><cim:IdentifiedObject.name>RTC</cim:IdentifiedObject.name><cim:RatioTapChanger.TransformerEnd rdf:resource="#_e1"/><cim:TapChanger.neutralStep>0</cim:TapChanger.neutralStep><cim:TapChanger.lowStep>-10</cim:TapChanger.lowStep><cim:TapChanger.highStep>10</cim:TapChanger.highStep>{steps}<cim:RatioTapChanger.stepVoltageIncrement>1.25</cim:RatioTapChanger.stepVoltageIncrement></cim:RatioTapChanger>""";

    private static string SvTap(string position, string id = "sv1") =>
        $"""<cim:SvTapStep rdf:ID="_{id}"><cim:SvTapStep.TapChanger rdf:resource="#_rtc"/><cim:SvTapStep.position>{position}</cim:SvTapStep.position></cim:SvTapStep>""";

    private static double Ratio(double step) => 1.0 + (step - 0.0) * 1.25 / 100.0;

    [Fact]
    public void Without_an_SvTapStep_the_tap_is_exactly_as_before()
    {
        CimNetwork net = CimImporter.Parse(Transformer(RatioChanger("<cim:TapChanger.step>2</cim:TapChanger.step>")));
        Assert.Equal(Ratio(2.0), net.Transformers[0].TapRatio);
        Assert.DoesNotContain(net.Notes, n => n.Contains("SV"));
    }

    [Fact]
    public void The_SSH_step_wins_by_default_and_a_different_SV_position_is_listed()
    {
        CimNetwork net = CimImporter.Parse(Transformer(RatioChanger("<cim:TapChanger.step>2</cim:TapChanger.step>"), SvTap("4")));
        Assert.Equal(Ratio(2.0), net.Transformers[0].TapRatio);
        Assert.Contains(net.Notes, n => n.Contains("RatioTapChanger 'RTC'") && n.Contains("SSH step 2 is used")
                                        && n.Contains("(SV) has it at 4"));
    }

    [Fact]
    public void The_StateVariables_option_takes_the_SV_position()
    {
        CimNetwork net = CimImporter.Parse(Transformer(RatioChanger("<cim:TapChanger.step>2</cim:TapChanger.step>"), SvTap("4")),
            new CimImportOptions(TapPositions: CimTapPositions.StateVariables));
        Assert.Equal(Ratio(4.0), net.Transformers[0].TapRatio);
        Assert.Contains(net.Notes, n => n.Contains("SV position 4 is used (CimTapPositions.StateVariables), not its SSH step 2"));
    }

    [Fact]
    public void With_no_SSH_step_the_SV_position_is_used_before_the_normal_step()
    {
        CimNetwork net = CimImporter.Parse(Transformer(RatioChanger("<cim:TapChanger.normalStep>1</cim:TapChanger.normalStep>"), SvTap("-3.5")));
        Assert.Equal(Ratio(-3.5), net.Transformers[0].TapRatio);
        Assert.Contains(net.Notes, n => n.Contains("no TapChanger.step (SSH); its SV position SvTapStep.position -3.5 is used"));
    }

    [Fact]
    public void A_bad_SvTapStep_is_refused()
    {
        string steps = "<cim:TapChanger.step>2</cim:TapChanger.step>";
        Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Transformer(RatioChanger(steps), SvTap("4") + SvTap("5", "sv2"))));
        Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Transformer(RatioChanger(steps), SvTap("4,5"))));
        Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Transformer(RatioChanger(steps),
            """<cim:SvTapStep rdf:ID="_sv1"><cim:SvTapStep.position>4</cim:SvTapStep.position></cim:SvTapStep>""")));
        // An SV position outside lowStep..highStep is refused exactly as an SSH step is.
        Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Transformer(RatioChanger(steps), SvTap("11")),
            new CimImportOptions(TapPositions: CimTapPositions.StateVariables)));
    }

    // ================================================================ N254: PhaseTapChanger xMin / xMax ========

    private static string LinearPhase(string step, string xMinMax) =>
        $"""<cim:PhaseTapChangerLinear rdf:ID="_ptc"><cim:IdentifiedObject.name>PST</cim:IdentifiedObject.name><cim:PhaseTapChanger.TransformerEnd rdf:resource="#_e1"/><cim:TapChanger.neutralStep>0</cim:TapChanger.neutralStep><cim:TapChanger.step>{step}</cim:TapChanger.step><cim:PhaseTapChangerLinear.stepPhaseShiftIncrement>1.5</cim:PhaseTapChangerLinear.stepPhaseShiftIncrement>{xMinMax}</cim:PhaseTapChangerLinear>""";

    private static string XMinMax(string lo, string hi, string cls = "PhaseTapChangerLinear") =>
        $"<cim:{cls}.xMin>{lo}</cim:{cls}.xMin><cim:{cls}.xMax>{hi}</cim:{cls}.xMax>";

    [Fact]
    public void A_rising_reactance_curve_off_neutral_is_listed_and_the_angle_is_unchanged()
    {
        CimNetwork with = CimImporter.Parse(Transformer(LinearPhase("3", XMinMax("10", "12"))));
        CimNetwork without = CimImporter.Parse(Transformer(LinearPhase("3", "")));
        Assert.Equal(3.0 * 1.5, with.Transformers[0].PhaseShiftDeg);
        Assert.Equal(without.Transformers[0].PhaseShiftDeg, with.Transformers[0].PhaseShiftDeg);
        Assert.Equal(without.Transformers[0].TapRatio, with.Transformers[0].TapRatio);
        Assert.Contains(with.Notes, n => n.Contains("PhaseTapChangerLinear 'PST'") && n.Contains("xMin = 10 ohm")
                                         && n.Contains("xMax = 12 ohm") && n.Contains("not represented"));
        Assert.DoesNotContain(without.Notes, n => n.Contains("xMin"));
    }

    [Fact]
    public void At_the_neutral_step_or_with_a_flat_curve_nothing_is_listed()
    {
        Assert.DoesNotContain(CimImporter.Parse(Transformer(LinearPhase("0", XMinMax("10", "12")))).Notes, n => n.Contains("xMin"));
        Assert.DoesNotContain(CimImporter.Parse(Transformer(LinearPhase("3", XMinMax("10", "10")))).Notes, n => n.Contains("xMin"));
    }

    [Fact]
    public void A_non_linear_changer_reads_its_reactance_curve_too()
    {
        string sym = """<cim:PhaseTapChangerSymmetrical rdf:ID="_ptc"><cim:IdentifiedObject.name>PST</cim:IdentifiedObject.name><cim:PhaseTapChanger.TransformerEnd rdf:resource="#_e1"/><cim:TapChanger.neutralStep>0</cim:TapChanger.neutralStep><cim:TapChanger.step>3</cim:TapChanger.step><cim:PhaseTapChangerNonLinear.voltageStepIncrement>1.25</cim:PhaseTapChangerNonLinear.voltageStepIncrement>"""
                     + XMinMax("10", "12", "PhaseTapChangerNonLinear") + "</cim:PhaseTapChangerSymmetrical>";
        Assert.Contains(CimImporter.Parse(Transformer(sym)).Notes, n => n.Contains("PhaseTapChangerNonLinear.xMin = 10 ohm"));
    }

    [Theory]
    [InlineData("12", "10")]   // xMin above xMax
    [InlineData("-1", "10")]   // negative
    public void A_contradictory_reactance_curve_is_refused(string lo, string hi)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Transformer(LinearPhase("3", XMinMax(lo, hi)))));
        Assert.Contains("PST", ex.Message);
    }

    // ================================================================ N254: generator type =====================

    private static string OneMachine(string unit) => Head + """
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>G1</cim:IdentifiedObject.name>
          <cim:RotatingMachine.p>-10</cim:RotatingMachine.p><cim:RotatingMachine.GeneratingUnit rdf:resource="#_gu1"/></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_g1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        """ + unit + Tail;

    private static string Unit(string cls, string inner = "") => $"""<cim:{cls} rdf:ID="_gu1">{inner}</cim:{cls}>""";

    private static string EnumProp(string prop, string value) => $"""<cim:{prop} rdf:resource="{Ns}{value}"/>""";

    private static string Fuel(string id, string kind) =>
        $"""<cim:FossilFuel rdf:ID="_{id}"><cim:FossilFuel.fossilFuelType rdf:resource="{Ns}FuelType.{kind}"/><cim:FossilFuel.ThermalGeneratingUnit rdf:resource="#_gu1"/></cim:FossilFuel>""";

    public static IEnumerable<object[]> UnitCases() => new[]
    {
        new object[] { Unit("WindGeneratingUnit", EnumProp("WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.onshore")), "wind-onshore", true },
        new object[] { Unit("WindGeneratingUnit", EnumProp("WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.offshore")), "wind-offshore", true },
        new object[] { Unit("WindGeneratingUnit"), "wind", true },
        new object[] { Unit("SolarGeneratingUnit"), "solar", true },
        new object[] { Unit("HydroGeneratingUnit"), "hydro", false },
        new object[] { Unit("HydroGeneratingUnit", EnumProp("HydroGeneratingUnit.energyConversionCapability", "HydroEnergyConversionKind.pumpAndGenerator")), "pumped-storage", false },
        new object[] { Unit("NuclearGeneratingUnit"), "nuclear", false },
        new object[] { Unit("ThermalGeneratingUnit"), "thermal", false },
        new object[] { Unit("ThermalGeneratingUnit") + Fuel("f1", "gas"), "gas", false },
        new object[] { Unit("ThermalGeneratingUnit") + Fuel("f1", "hardCoal"), "coal", false },
        new object[] { Unit("ThermalGeneratingUnit") + Fuel("f1", "lignite"), "lignite", false },
        new object[] { Unit("ThermalGeneratingUnit") + Fuel("f1", "oil"), "oil", false },
        new object[] { Unit("ThermalGeneratingUnit") + Fuel("f1", "gas") + Fuel("f2", "oil"), "thermal", false },
        new object[] { Unit("GeneratingUnit"), "", false },
    };

    [Theory]
    [MemberData(nameof(UnitCases))]
    public void The_generator_type_comes_from_the_generating_unit_class(string unit, string type, bool converter)
    {
        // Before N254 every CIM machine had type "" and so was a synchronous machine to the fault studies.
        CimNetwork net = CimImporter.Parse(OneMachine(unit));
        Assert.Equal(type, net.Generators[0].Type);
        Generator g = CimGridModelBridge.ToGridModel(net, unbasedImpedancesArePerUnit: true).Generators[0];
        Assert.Equal(type, g.Type);
        Assert.Equal(converter, g.IsConverterInterfaced);
    }

    [Theory]
    [MemberData(nameof(UnitCases))]
    public void The_generator_type_survives_an_export_round_trip(string unit, string type, bool converter)
    {
        _ = converter;
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(CimImporter.Parse(OneMachine(unit))));
        Assert.Equal(type, rt.Generators[0].Type);
    }

    [Fact]
    public void Two_fuels_on_one_unit_are_listed_and_an_unknown_unit_class_is_listed()
    {
        CimNetwork two = CimImporter.Parse(OneMachine(Unit("ThermalGeneratingUnit") + Fuel("f1", "gas") + Fuel("f2", "oil")));
        Assert.Contains(two.Notes, n => n.Contains("its fuel(s) gas, oil") && n.Contains("\"thermal\""));
        CimNetwork other = CimImporter.Parse(OneMachine(Unit("CAESGeneratingUnit")));
        Assert.Equal("", other.Generators[0].Type);
        Assert.Contains(other.Notes, n => n.Contains("CAESGeneratingUnit") && n.Contains("left empty"));
    }

    [Fact]
    public void An_unknown_wind_unit_kind_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CimImporter.Parse(OneMachine(
            Unit("WindGeneratingUnit", EnumProp("WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.airborne")))));
    }

    // ================================================================ benchmark: ENTSO-E conformity models =====

    /// <summary>
    /// The topology processor against the ENTSO-E CGMES 2.4.15 conformity test configurations (CAS 1.1.3, data
    /// 4.0.3: the copies powsybl-core keeps under cgmes/cgmes-conformity/src/main/resources/conformity/
    /// cas-1.1.3-data-4.0.3). They are not bundled (their licence is for Mark to decide; see the CIM1 notes), so this
    /// runs only when GRIDSIM_CGMES_CONFORMITY_DIR names that directory, and otherwise returns without checking
    /// anything (xunit 2 has no runtime skip). Published values (the models' own TP profiles): MiniGrid node-breaker
    /// 103 ConnectivityNodes, 90 switches, 13 TopologicalNodes; SmallGrid node-breaker 1225, 1107, 118. The switch
    /// view must give exactly those bus counts and agree one to one with the TopologicalNodes.
    /// </summary>
    // GR-410: skipped (not passed) without the models; was an early return reported as a pass.
    [EnvironmentTheory("the CGMES conformity models are not bundled", "GRIDSIM_CGMES_CONFORMITY_DIR")]
    [InlineData("MiniGrid/NodeBreaker/CGMES_v2.4.15_MiniGridTestConfiguration_BaseCase_Complete_v3",
                "MiniGrid/NodeBreaker/CGMES_v2.4.15_MiniGridTestConfiguration_Boundary_v3", 13)]
    [InlineData("SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_BaseCase_Complete_v3.0.0",
                "SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_Boundary_v3.0.0", 118)]
    public void Conformity_node_breaker_models_give_the_published_bus_count(string model, string boundary, int buses)
    {
        string root = Environment.GetEnvironmentVariable("GRIDSIM_CGMES_CONFORMITY_DIR")!;   // GR-410: set, or the attribute skipped this test
        string[] files = new[] { model, boundary }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.xml"))
            .Where(f => !f.Contains("_DL_", StringComparison.Ordinal) && !f.Contains("_GL_", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);
        string[] docs = files.Select(File.ReadAllText).ToArray();

        CimNetwork tp = CimImporter.Parse(docs);
        CimNetwork sw = CimImporter.Parse(docs, new CimImportOptions(Topology: CimTopology.Switches));
        Assert.Equal(buses, tp.Buses.Count);
        Assert.Equal(buses, sw.Buses.Count);
        Assert.Contains(sw.Notes, n => n.StartsWith("Topology check: the buses derived from the switch states agree one to one"));
        Assert.DoesNotContain(sw.Notes, n => n.StartsWith("Topology check: the switch states join")
                                             || n.StartsWith("Topology check: TopologicalNode"));
        Assert.Empty(sw.Couplers);
    }
}
