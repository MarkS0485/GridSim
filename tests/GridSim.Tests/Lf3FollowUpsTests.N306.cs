using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N306 (LF3; CIM3 follow-ups): the CIM exporter writes a regulating shunt's control and sections; SwitchFlows recognises a
/// CimControlledPowerFlow result and balances it with the regulated values (or refuses another bridge's result by name);
/// an EquivalentInjection's ReactiveCapabilityCurve is evaluated at its P with CapabilityAt (it was only listed); a CGMES
/// 3.0 boundary ConnectivityNode gets its base voltage in the node-breaker view too.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    private const string CimHead = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string CimTail = "</rdf:RDF>";
    private const string CimNs = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    private static string CEl(string prop, string value) => $"<cim:{prop}>{value}</cim:{prop}>";
    private static string CRef(string prop, string id) => $"""<cim:{prop} rdf:resource="#_{id}"/>""";
    private static string CName(string n) => $"<cim:IdentifiedObject.name>{n}</cim:IdentifiedObject.name>";

    // ================================================================ exporter: a regulating shunt ==============

    [Fact]
    public void N306_a_regulating_shunt_keeps_its_control_and_sections_through_an_export_round_trip()
    {
        // c1 regulates its own bus, c2 another bus (remote): each with a deadband, a discrete flag, 2 of 4 sections.
        var c1 = new CimShuntControl(132.0, 2.0, "n1", true, 2.0, 4.0, 0.0005, 0.0);
        var c2 = new CimShuntControl(131.5, null, "n1", false, 3.0, null, 0.00025, 0.00001);
        var net = new CimNetwork(
            new[] { new CimBus("n1", "N1", 132.0), new CimBus("n2", "N2", 132.0) },
            new[] { new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0) },
            Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 132.0, VoltageControl: true) },
            Array.Empty<CimTransformer>(),
            ShuntCompensators: new[]
            {
                new CimShunt("c1", "C1", "n1", 0.0 * 2.0, 0.0005 * 2.0, "LinearShuntCompensator", 0.0, c1),
                new CimShunt("c2", "C2", "n2", 0.00001 * 3.0, 0.00025 * 3.0, "LinearShuntCompensator", 0.0, c2),
            });
        string xml = CimExporter.Export(net);
        CimNetwork back = CimImporter.Parse(xml);
        Assert.Equal(net.Shunts, back.Shunts);   // records: G, B, class and the whole CimShuntControl, exactly
        Assert.Contains("<cim:RegulatingControl.targetDeadband>2</cim:RegulatingControl.targetDeadband>", xml);
        Assert.Contains("<cim:RegulatingControl.discrete>false</cim:RegulatingControl.discrete>", xml);

        // A shunt with no control is written exactly as before: one section holding the whole admittance.
        var plain = net with { ShuntCompensators = new[] { net.Shunts[0] with { Control = null } } };
        string plainXml = CimExporter.Export(plain);
        Assert.Contains("<cim:ShuntCompensator.maximumSections>1</cim:ShuntCompensator.maximumSections>", plainXml);
        Assert.DoesNotContain("_rc_c1", plainXml);
        Assert.DoesNotContain("targetDeadband", plainXml);
        Assert.Equal(plain.Shunts, CimImporter.Parse(plainXml).Shunts);
    }

    // ================================================================ SwitchFlows on a controlled result ========

    // 225 kV: the slack G1 at n1, a line n1 - n2a (0.01 + j0.1 pu), a closed retained breaker n2a - n2b, a load at n2b
    // (100 MW, 30 MVAr) and a regulating SVC at n2b holding n2b at 225 kV (+/- 100 MVAr at 1.0 pu, no slope, SSH q 0).
    private static CimNetwork SwitchSvcNet() => new(
        new[] { new CimBus("n1", "N1", 225.0), new CimBus("n2a", "N2A", 225.0), new CimBus("n2b", "N2B", 225.0) },
        new[] { new CimBranch("l1", "L1", "n1", "n2a", 5.0625, 50.625, 0.0) },
        new[] { new CimLoad("ld", "LD", "n2b", 100.0, 30.0) },
        new[] { new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 225.0, VoltageControl: true) },
        Array.Empty<CimTransformer>(),
        ClosedSwitches: new[] { new CimSwitch("s1", "S1", "Breaker", "n2a", "n2b", Retained: true) },
        StaticVarCompensators: new[] { new CimStaticVarCompensator("svc", "SVC", "n2b", 506.25, -506.25, 0.0, 0.0, true, 225.0, "n2b") });

    [Fact]
    public void N306_switch_flows_of_a_controlled_result_use_the_regulated_svc()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(SwitchSvcNet());
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(r));
        Assert.NotNull(f.QMvar);
        // Physics, independent of the balance code: everything reaching side A (n2a) over the line goes through the
        // switch. Before N306 the SVC was counted at its SSH q (0), so Q came out as the load's 30 MVAr instead.
        LineFlow l1 = r.LineFlows.Single();
        Assert.True(Math.Abs(f.PMw!.Value - (0.0 - l1.PToMw)) <= 1e-5, $"P {f.PMw} vs {0.0 - l1.PToMw}");
        Assert.True(Math.Abs(f.QMvar!.Value - (0.0 - l1.QToMvar)) <= 1e-5, $"Q {f.QMvar} vs {0.0 - l1.QToMvar}");
        Assert.True(Math.Abs(f.QMvar.Value - 30.0) > 1e-3);   // the SVC does supply reactive power here

        // A plain solve of the same model is balanced exactly as before (the SVC at its SSH q).
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        CimSwitchFlow fp = Assert.Single(bridged.SwitchFlows(plain));
        Assert.Equal(30.0, fp.QMvar!.Value, 9);
    }

    [Fact]
    public void N306_a_controlled_result_of_another_bridge_is_refused_by_name()
    {
        CimBridgeResult a = CimGridModelBridge.Bridge(SwitchSvcNet());
        CimBridgeResult b = CimGridModelBridge.Bridge(SwitchSvcNet());
        PowerFlowResult r = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), a).Solve(a.Model);
        Assert.True(r.Converged);
        var ex = Assert.Throws<ArgumentException>(() => b.SwitchFlows(r));
        Assert.Contains("CimControlledPowerFlow solve of another bridged model", ex.Message);
        Assert.NotNull(a.SwitchFlows(r));
    }

    [Fact]
    public void N306_a_remote_regulating_machine_on_a_switch_side_makes_its_reactive_flow_undetermined()
    {
        // G2 at n2b (beside the breaker) regulates n3 over l2; in the controlled solve its bus is voltage-controlled, so its
        // reactive output is a solved bus total and the switch's Q is not determined (P still is). In a plain solve G2 is
        // held at its q and both are reported, as before.
        var net = new CimNetwork(
            new[] { new CimBus("n1", "N1", 110.0), new CimBus("n2a", "N2A", 110.0), new CimBus("n2b", "N2B", 110.0), new CimBus("n3", "N3", 110.0) },
            new[]
            {
                new CimBranch("l1", "L1", "n1", "n2a", 1.21, 12.1, 0.0),
                new CimBranch("l2", "L2", "n2b", "n3", 1.21, 12.1, 0.0),
            },
            new[] { new CimLoad("ld", "LD", "n3", 60.0, 40.0) },
            new[]
            {
                new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 110.0, VoltageControl: true),
                new CimGenerator("g2", "G2", "n2b", 20.0, VoltageControl: false, MinQ: -100.0, MaxQ: 100.0,
                    RegulatedNode: "n3", RemoteTargetKv: 110.0),
            },
            Array.Empty<CimTransformer>(),
            ClosedSwitches: new[] { new CimSwitch("s1", "S1", "Breaker", "n2a", "n2b", Retained: true) });
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net);
        Assert.Single(bridged.RemoteVoltageControls);

        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        CimSwitchFlow fp = Assert.Single(bridged.SwitchFlows(plain));
        Assert.NotNull(fp.PMw);
        Assert.NotNull(fp.QMvar);

        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        CimSwitchFlow f = Assert.Single(bridged.SwitchFlows(r));
        Assert.NotNull(f.PMw);
        // Moved 2026-10 (C5, final batch; LF3 N306, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // test's own expectation. Side A (node n2a) holds only L1's end and no machine with a solved Q, so Kirchhoff's
        // current law at n2a fixes the switch's Q; it is undetermined only when solved-Q machines sit on BOTH sides.
        // N306 works: the controlled solve moves the Q basis from side B (G2's bus, now solved) to side A. Probed on
        // 2026-10-10: P 40.52001668985589 MW (from side B), Q -54.7998329398381 MVAr (from side A). The test name keeps its
        // original wording so its history stays traceable. Before -> after: Assert.Null(f.QMvar) -> Q determined from the
        // first side, pinned exactly.
        Assert.NotNull(f.QMvar);
        Assert.Contains("Q from the balance of the first side", f.Basis, StringComparison.Ordinal);
        Assert.Equal(-54.7998329398381, f.QMvar!.Value);
    }

    // ================================================================ EquivalentInjection capability curve =====

    private static string EiCurveDoc(string minQ = "0", string maxQ = "0") => CimHead
        + """<cim:BaseVoltage rdf:ID="_bv110">""" + CEl("BaseVoltage.nominalVoltage", "110") + "</cim:BaseVoltage>"
        + """<cim:TopologicalNode rdf:ID="_n1">""" + CName("N1") + CRef("TopologicalNode.BaseVoltage", "bv110") + "</cim:TopologicalNode>"
        + """<cim:EquivalentInjection rdf:ID="_ei">""" + CName("EI") + CEl("EquivalentInjection.p", "-90") + CEl("EquivalentInjection.q", "0")
        + CEl("EquivalentInjection.minQ", minQ) + CEl("EquivalentInjection.maxQ", maxQ) + CRef("EquivalentInjection.ReactiveCapabilityCurve", "rcc")
        + "</cim:EquivalentInjection>"
        + """<cim:Terminal rdf:ID="_ei_t">""" + CRef("Terminal.ConductingEquipment", "ei") + CRef("Terminal.TopologicalNode", "n1") + "</cim:Terminal>"
        + """<cim:ReactiveCapabilityCurve rdf:ID="_rcc">""" + CName("EI curve")
        + $"""<cim:Curve.curveStyle rdf:resource="{CimNs}CurveStyle.straightLineYValues"/>""" + "</cim:ReactiveCapabilityCurve>"
        + string.Concat(new[] { ("200", "-200", "200"), ("-100", "-200", "200"), ("0", "-300", "300") }.Select((pt, i) =>
            $"""<cim:CurveData rdf:ID="_cd{i}">""" + CEl("CurveData.xvalue", pt.Item1) + CEl("CurveData.y1value", pt.Item2)
            + CEl("CurveData.y2value", pt.Item3) + CRef("CurveData.Curve", "rcc") + "</cim:CurveData>"))
        + CimTail;

    [Fact]
    public void N306_an_equivalent_injection_capability_curve_gives_its_limits_at_its_p()
    {
        CimNetwork net = CimImporter.Parse(EiCurveDoc());
        CimGenerator ei = net.Generators.Single();
        // P = 90 MW lies between the points at 0 and 200 MW: the same straight-line rule as a machine's curve (N295).
        Assert.Equal(-300.0 + (90.0 - 0.0) * (-200.0 - -300.0) / (200.0 - 0.0), ei.MinQ);
        Assert.Equal(300.0 + (90.0 - 0.0) * (200.0 - 300.0) / (200.0 - 0.0), ei.MaxQ);
        Assert.Contains(net.Notes, n => n.Contains("reactive capability curve") && n.Contains("-255 .. 255 MVAr")
                                        && n.Contains("instead of EquivalentInjection.minQ / maxQ (0 / 0)"));
        Assert.DoesNotContain(net.Notes, n => n.Contains("its reactive capability curve is not represented"));
    }

    [Fact]
    public void N306_an_equivalent_injection_naming_a_missing_curve_keeps_its_min_and_max_q()
    {
        string doc = EiCurveDoc("-40", "60").Replace("""<cim:ReactiveCapabilityCurve rdf:ID="_rcc">""", """<cim:ReactiveCapabilityCurve rdf:ID="_other">""");
        CimNetwork net = CimImporter.Parse(doc);
        CimGenerator ei = net.Generators.Single();
        Assert.Equal((-40.0, 60.0), (ei.MinQ!.Value, ei.MaxQ!.Value));
        Assert.Contains(net.Notes, n => n.Contains("names reactive capability curve") && n.Contains("which is not in this document"));
    }

    // ================================================================ boundary base in the node-breaker view ====

    // Node-breaker: a 380 kV voltage level with node cn1 (the slack G1 holding 380 kV), a boundary ConnectivityNode bx
    // (no container, no TopologicalNode, named by a BoundaryPoint), a 380 kV line cn1 - bx and a 400 kV boundary
    // EquivalentInjection on bx giving 50 MW.
    private static string NodeBreakerBoundaryDoc() => CimHead
        + """<cim:BaseVoltage rdf:ID="_bv380">""" + CEl("BaseVoltage.nominalVoltage", "380") + "</cim:BaseVoltage>"
        + """<cim:BaseVoltage rdf:ID="_bv400">""" + CEl("BaseVoltage.nominalVoltage", "400") + "</cim:BaseVoltage>"
        + """<cim:VoltageLevel rdf:ID="_vl1">""" + CRef("VoltageLevel.BaseVoltage", "bv380") + "</cim:VoltageLevel>"
        + """<cim:ConnectivityNode rdf:ID="_cn1">""" + CName("CN1") + CRef("ConnectivityNode.ConnectivityNodeContainer", "vl1") + "</cim:ConnectivityNode>"
        + """<cim:ConnectivityNode rdf:ID="_bx">""" + CName("Border_X") + "</cim:ConnectivityNode>"
        + """<cim:BoundaryPoint rdf:ID="_bp">""" + CName("Border_X") + CRef("BoundaryPoint.ConnectivityNode", "bx") + "</cim:BoundaryPoint>"
        + """<cim:SynchronousMachine rdf:ID="_g1">""" + CName("G1") + CEl("RotatingMachine.p", "0") + CRef("RegulatingCondEq.RegulatingControl", "rc1")
        + CEl("RegulatingCondEq.controlEnabled", "true") + "</cim:SynchronousMachine>"
        + """<cim:Terminal rdf:ID="_g1_t">""" + CRef("Terminal.ConductingEquipment", "g1") + CRef("Terminal.ConnectivityNode", "cn1") + "</cim:Terminal>"
        + """<cim:RegulatingControl rdf:ID="_rc1">""" + $"""<cim:RegulatingControl.mode rdf:resource="{CimNs}RegulatingControlModeKind.voltage"/>"""
        + CRef("RegulatingControl.Terminal", "g1_t") + CEl("RegulatingControl.targetValue", "380") + CEl("RegulatingControl.enabled", "true") + "</cim:RegulatingControl>"
        + """<cim:ACLineSegment rdf:ID="_l1">""" + CName("L1") + CEl("ACLineSegment.r", "1.444") + CEl("ACLineSegment.x", "14.44")
        + CRef("ConductingEquipment.BaseVoltage", "bv380") + "</cim:ACLineSegment>"
        + """<cim:Terminal rdf:ID="_l1_1">""" + CRef("Terminal.ConductingEquipment", "l1") + CRef("Terminal.ConnectivityNode", "cn1") + CEl("ACDCTerminal.sequenceNumber", "1") + "</cim:Terminal>"
        + """<cim:Terminal rdf:ID="_l1_2">""" + CRef("Terminal.ConductingEquipment", "l1") + CRef("Terminal.ConnectivityNode", "bx") + CEl("ACDCTerminal.sequenceNumber", "2") + "</cim:Terminal>"
        + """<cim:EquivalentInjection rdf:ID="_ei">""" + CName("EI") + CEl("EquivalentInjection.p", "-50") + CEl("EquivalentInjection.q", "0")
        + CRef("ConductingEquipment.BaseVoltage", "bv400") + "</cim:EquivalentInjection>"
        + """<cim:Terminal rdf:ID="_ei_t">""" + CRef("Terminal.ConductingEquipment", "ei") + CRef("Terminal.ConnectivityNode", "bx") + "</cim:Terminal>"
        + CimTail;

    [Fact]
    public void N306_a_cgmes_3_boundary_node_has_its_base_voltage_in_the_node_breaker_view()
    {
        CimNetwork net = CimImporter.Parse(NodeBreakerBoundaryDoc(), new CimImportOptions(Topology: CimTopology.Switches));
        string eiBus = net.Generators.Single(g => g.MRID == "ei").Node;
        CimBus bx = net.Buses.Single(b => b.MRID == eiBus);
        // The smallest-mRID equipment on it that declares a base ("ei" before "l1"): the injection's 400 kV. Before N306
        // this view left it at 0 (no base).
        Assert.Equal(400.0, bx.BaseKv);
        Assert.Contains(net.Notes, n => n.StartsWith("CGMES 3.0 boundary bus 'Border_X'") && n.Contains("base voltage 400 kV"));
        // The 380 kV node keeps its own level's base.
        string g1Bus = net.Generators.Single(g => g.MRID == "g1").Node;
        Assert.Equal(380.0, net.Buses.Single(b => b.MRID == g1Bus).BaseKv);

        // The same physical answer as the bus-branch view: the 380 kV line to the 400 kV-based bus carries the ratio as a
        // tap (N294), and the model solves.
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal(400.0 / 380.0, m.Branches.Single().TapRatio);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }
}
