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
/// CIM2 stream (October 2026), N286: the equipment the CIM importer now reads besides lines, transformers, loads and
/// synchronous machines: shunt compensators (linear, non-linear, equivalent shunts; sections from SSH / SV / EQ),
/// ExternalNetworkInjection and EquivalentInjection (generators, regulating or not, as powsybl maps them),
/// AsynchronousMachine (a fixed P / Q load), and the CGMES 3.0 PowerElectronicsConnection (generators flagged converter
/// plant). Numbers are compared exactly (Assert.Equal on doubles) against the expression the importer and the bridge
/// are documented to evaluate.
/// </summary>
public class CimEquipmentTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";
    private const string Ns = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    private static string Name(string n) => $"<cim:IdentifiedObject.name>{n}</cim:IdentifiedObject.name>";

    // Two 132 kV TopologicalNodes (n1, n2) joined by a line of r = 0.01, x = 0.1 pu on 100 MVA (1.7424 / 17.424 ohm).
    private static readonly string Base132 =
        """<cim:BaseVoltage rdf:ID="_bv132"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>"""
        + """<cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>"""
        + """<cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>N2</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv132"/></cim:TopologicalNode>"""
        + """<cim:ACLineSegment rdf:ID="_l1"><cim:IdentifiedObject.name>L1</cim:IdentifiedObject.name><cim:ACLineSegment.r>1.7424</cim:ACLineSegment.r><cim:ACLineSegment.x>17.424</cim:ACLineSegment.x></cim:ACLineSegment>"""
        + T("l1_1", "l1", "n1", 1) + T("l1_2", "l1", "n2", 2);

    private static string T(string id, string equip, string node, int seq = 0) =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/>{(seq > 0 ? $"<cim:ACDCTerminal.sequenceNumber>{seq}</cim:ACDCTerminal.sequenceNumber>" : "")}</cim:Terminal>""";

    // A slack machine at n1 generating 50 MW, with a local voltage control at 132 kV.
    private static readonly string Slack =
        """<cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>G1</cim:IdentifiedObject.name><cim:RotatingMachine.p>-50</cim:RotatingMachine.p><cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rcg1"/></cim:SynchronousMachine>"""
        + T("g1_t", "g1", "n1")
        + $"""<cim:RegulatingControl rdf:ID="_rcg1"><cim:RegulatingControl.mode rdf:resource="{Ns}RegulatingControlModeKind.voltage"/><cim:RegulatingControl.Terminal rdf:resource="#_g1_t"/><cim:RegulatingControl.targetValue>132</cim:RegulatingControl.targetValue><cim:RegulatingControl.enabled>true</cim:RegulatingControl.enabled></cim:RegulatingControl>""";

    private static string Doc(params string[] parts) => Head + Base132 + string.Concat(parts) + Tail;

    private static string El(string prop, string value) => $"<cim:{prop}>{value}</cim:{prop}>";

    private static string Ref(string prop, string id) => $"""<cim:{prop} rdf:resource="#_{id}"/>""";

    // ================================================================ shunt compensators ===============

    private static string Linear(string id, string node, string body) =>
        $"""<cim:LinearShuntCompensator rdf:ID="_{id}">{Name(id)}{body}</cim:LinearShuntCompensator>""" + T(id + "_t", id, node);

    private static string Nonlinear(string id, string node, string body, params (int Section, string B, string G)[] points) =>
        $"""<cim:NonlinearShuntCompensator rdf:ID="_{id}">{Name(id)}{body}</cim:NonlinearShuntCompensator>""" + T(id + "_t", id, node)
        // Moved 2026-10 (C4, final batch; CIM2 N286, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // helper named every point _{id}_p{section}, so two points for one section shared one rdf:ID and the importer
        // merged them as one object (the mRID merge EQ / TP / SSH rely on) before the duplicate-section check could see
        // two. Each point now has its own rdf:ID: a repeated section gets the suffix _{k} (k = its position in the list), so
        // every point with a unique section keeps exactly the id it had. Before -> after: the second section-1 point in
        // A_nonlinear_shunt_with_two_points_for_one_section_is_refused was "_c2_p1" (merged) and is now "_c2_p1_1".
        + string.Concat(points.Select((p, k) =>
            $"""<cim:NonlinearShuntCompensatorPoint rdf:ID="_{id}_p{p.Section}{(points.Take(k).Any(q => q.Section == p.Section) ? "_" + k : "")}">{El("NonlinearShuntCompensatorPoint.sectionNumber", p.Section.ToString())}{El("NonlinearShuntCompensatorPoint.b", p.B)}{El("NonlinearShuntCompensatorPoint.g", p.G)}{Ref("NonlinearShuntCompensatorPoint.NonlinearShuntCompensator", id)}</cim:NonlinearShuntCompensatorPoint>"""));

    private static string SvSections(string id, string shunt, string sections) =>
        $"""<cim:SvShuntCompensatorSections rdf:ID="_{id}">{El("SvShuntCompensatorSections.sections", sections)}{Ref("SvShuntCompensatorSections.ShuntCompensator", shunt)}</cim:SvShuntCompensatorSections>""";

    [Fact]
    public void A_linear_shunt_is_its_per_section_admittance_times_the_sections_in_service()
    {
        CimNetwork net = CimImporter.Parse(Doc(Slack, Linear("c1", "n2",
            El("ShuntCompensator.sections", "3") + El("ShuntCompensator.maximumSections", "5")
            + El("LinearShuntCompensator.bPerSection", "0.0001") + El("LinearShuntCompensator.gPerSection", "0.000002"))));
        CimShunt sh = Assert.Single(net.Shunts);
        Assert.Equal(("c1", "n2", "LinearShuntCompensator"), (sh.MRID, sh.Node, sh.Class));
        Assert.Equal(0.0001 * 3.0, sh.B);
        Assert.Equal(0.000002 * 3.0, sh.G);
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));

        // Bridged on the bus base: Bs = B kV^2 MVAr and Gs = G kV^2 MW at 1.0 pu.
        var notes = new List<string>();
        GridModel model = CimGridModelBridge.ToGridModel(net, notes: notes);
        Bus n2 = model.Buses.Single(b => b.Name == "N2");
        Assert.Equal(0.0 + 0.0001 * 3.0 * (132.0 * 132.0), n2.Bs);
        Assert.Equal(0.0 + 0.000002 * 3.0 * (132.0 * 132.0), n2.Gs);
        Assert.Equal(0.0, model.Buses.Single(b => b.Name == "N1").Bs);
        Assert.Contains(notes, n => n.StartsWith("1 shunt(s) add Bs = "));
    }

    [Fact]
    public void A_shunt_raises_the_voltage_it_supports_and_the_solve_converges()
    {
        // A load at n2 with and without a 10-section capacitor: the capacitor must raise V(n2). This pins the sign
        // (a positive CIM b is capacitive, a positive Bus.Bs injects vars).
        string load = """<cim:EnergyConsumer rdf:ID="_ld"><cim:EnergyConsumer.p>40</cim:EnergyConsumer.p><cim:EnergyConsumer.q>20</cim:EnergyConsumer.q></cim:EnergyConsumer>""" + T("ld_t", "ld", "n2");
        string cap = Linear("c1", "n2", El("ShuntCompensator.sections", "10") + El("LinearShuntCompensator.bPerSection", "0.0001"));
        PowerFlowResult without = new NewtonRaphsonPowerFlow().Solve(CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Slack, load))));
        PowerFlowResult with = new NewtonRaphsonPowerFlow().Solve(CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Slack, load, cap))));
        Assert.True(without.Converged);
        Assert.True(with.Converged);
        Assert.True(with.Vm[1] > without.Vm[1]);
    }

    [Fact]
    public void Shunt_sections_come_from_ssh_then_sv_then_the_normal_sections()
    {
        string body(string ssh) => ssh + El("ShuntCompensator.normalSections", "1") + El("LinearShuntCompensator.bPerSection", "0.0001");

        // SSH and SV agree: the SSH count, no note.
        CimNetwork same = CimImporter.Parse(Doc(Linear("c1", "n2", body(El("ShuntCompensator.sections", "3"))), SvSections("sv1", "c1", "3")));
        Assert.Equal(0.0001 * 3.0, same.Shunts[0].B);
        Assert.DoesNotContain(same.Notes, n => n.Contains("section count"));

        // SSH 3, SV 4: SSH by default (the difference listed), SV with ShuntSections = StateVariables.
        string differ = Doc(Linear("c1", "n2", body(El("ShuntCompensator.sections", "3"))), SvSections("sv1", "c1", "4"));
        CimNetwork ssh = CimImporter.Parse(differ);
        Assert.Equal(0.0001 * 3.0, ssh.Shunts[0].B);
        Assert.Contains(ssh.Notes, n => n.Contains("its SSH section count 3 is used") && n.Contains("has 4"));
        CimNetwork sv = CimImporter.Parse(differ, new CimImportOptions(ShuntSections: CimTapPositions.StateVariables));
        Assert.Equal(0.0001 * 4.0, sv.Shunts[0].B);
        Assert.Contains(sv.Notes, n => n.Contains("its SV section count 4 is used"));

        // No SSH: the SV count, said.
        CimNetwork svOnly = CimImporter.Parse(Doc(Linear("c1", "n2", body("")), SvSections("sv1", "c1", "2")));
        Assert.Equal(0.0001 * 2.0, svOnly.Shunts[0].B);
        Assert.Contains(svOnly.Notes, n => n.Contains("has no ShuntCompensator.sections (SSH); its SV count"));

        // Neither: the EQ normal sections, said.
        CimNetwork normal = CimImporter.Parse(Doc(Linear("c1", "n2", body(""))));
        Assert.Equal(0.0001 * 1.0, normal.Shunts[0].B);
        Assert.Contains(normal.Notes, n => n.Contains("its normalSections 1 is used"));
    }

    [Theory]
    [InlineData("", "unknown")]                                                                       // no state at all
    [InlineData("<cim:ShuntCompensator.sections>6</cim:ShuntCompensator.sections><cim:ShuntCompensator.maximumSections>5</cim:ShuntCompensator.maximumSections>", "maximumSections")]
    [InlineData("<cim:ShuntCompensator.sections>-1</cim:ShuntCompensator.sections>", "maximumSections")]
    public void Shunt_sections_that_are_unknown_or_out_of_range_are_refused(string state, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Linear("c1", "n2",
            state + El("LinearShuntCompensator.bPerSection", "0.0001")))));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("c1", ex.Message);
    }

    [Fact]
    public void Two_sv_section_counts_for_one_shunt_are_refused()
    {
        Assert.Contains("ambiguous", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(
            Linear("c1", "n2", El("LinearShuntCompensator.bPerSection", "0.0001")),
            SvSections("sv1", "c1", "1"), SvSections("sv2", "c1", "2")))).Message);
    }

    [Fact]
    public void A_nonlinear_shunt_sums_its_points_up_to_the_sections_in_service()
    {
        CimNetwork net = CimImporter.Parse(Doc(Nonlinear("c2", "n2", El("ShuntCompensator.sections", "2"),
            (3, "0.0004", "0.000003"), (1, "0.0001", "0.000001"), (2, "0.0002", "0.000002"))));
        CimShunt sh = Assert.Single(net.Shunts);
        Assert.Equal("NonlinearShuntCompensator", sh.Class);
        Assert.Equal(0.0 + 0.0001 + 0.0002, sh.B);   // points 1 and 2 in section order; point 3 is out
        Assert.Equal(0.0 + 0.000001 + 0.000002, sh.G);
    }

    [Theory]
    [InlineData("1.5", "whole number")]
    [InlineData("3", "no NonlinearShuntCompensatorPoint for section 3")]
    public void A_nonlinear_shunt_with_a_fractional_or_missing_section_is_refused(string sections, string expected)
    {
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Nonlinear("c2", "n2",
            El("ShuntCompensator.sections", sections), (1, "0.0001", "0"), (2, "0.0002", "0"))))).Message);
    }

    [Fact]
    public void A_nonlinear_shunt_with_two_points_for_one_section_is_refused()
    {
        Assert.Contains("two points for section 1", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Nonlinear("c2", "n2",
            El("ShuntCompensator.sections", "1"), (1, "0.0001", "0"), (1, "0.0002", "0"))))).Message);
    }

    [Fact]
    public void A_shunt_that_gives_no_admittance_is_left_out_and_listed()
    {
        CimNetwork net = CimImporter.Parse(Doc(Linear("c1", "n2", El("ShuntCompensator.sections", "1")),
            Nonlinear("c2", "n2", El("ShuntCompensator.sections", "1"))));
        Assert.Empty(net.Shunts);
        Assert.Contains(net.Notes, n => n.Contains("LinearShuntCompensator 'c1'") && n.Contains("no LinearShuntCompensator.bPerSection") && n.Contains("left out"));
        Assert.Contains(net.Notes, n => n.Contains("NonlinearShuntCompensator 'c2'") && n.Contains("no NonlinearShuntCompensatorPoint") && n.Contains("left out"));
    }

    [Fact]
    public void The_abstract_shunt_compensator_class_is_refused_by_name()
    {
        string doc = Doc("""<cim:ShuntCompensator rdf:ID="_c9"><cim:ShuntCompensator.sections>1</cim:ShuntCompensator.sections></cim:ShuntCompensator>""" + T("c9_t", "c9", "n2"));
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("ShuntCompensator is an abstract class", ex.Message);
        Assert.Contains("c9", ex.Message);
    }

    [Fact]
    public void An_equivalent_shunt_is_read_with_its_g_and_b()
    {
        CimNetwork net = CimImporter.Parse(Doc("""<cim:EquivalentShunt rdf:ID="_es">""" + El("EquivalentShunt.g", "0.00001")
            + El("EquivalentShunt.b", "-0.0002") + "</cim:EquivalentShunt>" + T("es_t", "es", "n1")));
        CimShunt sh = Assert.Single(net.Shunts);
        Assert.Equal(("EquivalentShunt", 0.00001, -0.0002), (sh.Class, sh.G, sh.B));
    }

    [Fact]
    public void Zero_sequence_data_and_voltage_control_of_a_shunt_are_listed_as_not_represented()
    {
        string rc = $"""<cim:RegulatingControl rdf:ID="_rcc"><cim:RegulatingControl.mode rdf:resource="{Ns}RegulatingControlModeKind.voltage"/><cim:RegulatingControl.targetValue>133</cim:RegulatingControl.targetValue></cim:RegulatingControl>""";
        CimNetwork net = CimImporter.Parse(Doc(rc, Linear("c1", "n2", El("ShuntCompensator.sections", "2")
            + El("LinearShuntCompensator.bPerSection", "0.0001") + El("LinearShuntCompensator.b0PerSection", "0.00005")
            + El("RegulatingCondEq.controlEnabled", "true") + Ref("RegulatingCondEq.RegulatingControl", "rcc"))));
        Assert.Equal(0.0001 * 2.0, net.Shunts[0].B);   // held at its sections
        Assert.Contains(net.Notes, n => n.Contains("zero-sequence") && n.Contains("c1"));
        Assert.Contains(net.Notes, n => n.Contains("is under voltage control") && n.Contains("held at 2 section(s)"));
        Assert.Contains(net.Notes, n => n.StartsWith("1 shunt compensator(s) under voltage control"));
    }

    [Fact]
    public void A_shunt_on_a_bus_with_no_base_voltage_uses_its_own_rating_or_is_refused()
    {
        // A node with no base voltage; the shunt's nomU is the fallback, said by the bridge.
        string noBase = Head + """<cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name></cim:TopologicalNode>"""
            + Linear("c1", "n1", El("ShuntCompensator.sections", "1") + El("LinearShuntCompensator.bPerSection", "0.0001") + "{nomU}") + Tail;
        var notes = new List<string>();
        GridModel model = CimGridModelBridge.ToGridModel(CimImporter.Parse(noBase.Replace("{nomU}", El("ShuntCompensator.nomU", "33"))), notes: notes);
        Assert.Equal(0.0 + 0.0001 * 1.0 * (33.0 * 33.0), model.Buses[0].Bs);
        Assert.Contains(notes, n => n.Contains("the shunt's own 33 kV is used"));

        // No base anywhere: refused, unless the caller declares per-unit data (then B is per-unit on baseMva).
        CimNetwork bare = CimImporter.Parse(noBase.Replace("{nomU}", ""));
        Assert.Contains("cannot be put in MVAr", Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(bare)).Message);
        Assert.Equal(0.0 + 0.0001 * 1.0 * 100.0, CimGridModelBridge.ToGridModel(bare, unbasedImpedancesArePerUnit: true).Buses[0].Bs);
    }

    [Fact]
    public void Shunts_survive_an_export_round_trip_exactly()
    {
        CimNetwork net = CimImporter.Parse(Doc(
            Linear("c1", "n2", El("ShuntCompensator.sections", "3") + El("LinearShuntCompensator.bPerSection", "0.000123") + El("LinearShuntCompensator.gPerSection", "0.0000017")),
            Nonlinear("c2", "n1", El("ShuntCompensator.sections", "2"), (1, "0.0001", "0.000001"), (2, "0.00025", "0")),
            """<cim:EquivalentShunt rdf:ID="_es">""" + El("EquivalentShunt.g", "0.00001") + El("EquivalentShunt.b", "-0.0002") + "</cim:EquivalentShunt>" + T("es_t", "es", "n1")));
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(3, net.Shunts.Count);
        Assert.Equal(net.Shunts, rt.Shunts);
    }

    // ================================================================ injections =======================

    private static string Eni(string id, string node, string body) =>
        $"""<cim:ExternalNetworkInjection rdf:ID="_{id}">{Name(id)}{body}</cim:ExternalNetworkInjection>""" + T(id + "_t", id, node);

    private static string Ei(string id, string node, string body) =>
        $"""<cim:EquivalentInjection rdf:ID="_{id}">{Name(id)}{body}</cim:EquivalentInjection>""" + T(id + "_t", id, node);

    private static string Pec(string id, string node, string body) =>
        $"""<cim:PowerElectronicsConnection rdf:ID="_{id}">{Name(id)}{body}</cim:PowerElectronicsConnection>""" + T(id + "_t", id, node);

    private static string Unit(string cls, string id, string pec, string body = "") =>
        $"""<cim:{cls} rdf:ID="_{id}">{Ref("PowerElectronicsUnit.PowerElectronicsConnection", pec)}{body}</cim:{cls}>""";

    private static string LocalControl(string id, string terminal, string kv) =>
        $"""<cim:RegulatingControl rdf:ID="_{id}"><cim:RegulatingControl.mode rdf:resource="{Ns}RegulatingControlModeKind.voltage"/><cim:RegulatingControl.Terminal rdf:resource="#_{terminal}"/><cim:RegulatingControl.targetValue>{kv}</cim:RegulatingControl.targetValue><cim:RegulatingControl.enabled>true</cim:RegulatingControl.enabled></cim:RegulatingControl>""";

    [Fact]
    public void An_external_network_injection_is_a_generator_in_the_generator_sense()
    {
        CimNetwork net = CimImporter.Parse(Doc(Eni("q1", "n1",
            El("ExternalNetworkInjection.p", "-80") + El("ExternalNetworkInjection.q", "12.5")
            + El("ExternalNetworkInjection.minP", "-800") + El("ExternalNetworkInjection.maxP", "800")
            + El("ExternalNetworkInjection.minQ", "-600") + El("ExternalNetworkInjection.maxQ", "600")
            + El("ExternalNetworkInjection.referencePriority", "2"))));
        CimGenerator g = Assert.Single(net.Generators);
        Assert.Equal("ExternalNetworkInjection", g.Class);
        Assert.Equal((80.0, -12.5), (g.P, g.Q));   // CGMES load sign negated (R267 convention)
        Assert.Equal((-800.0, 800.0, -600.0, 600.0), (g.MinP!.Value, g.MaxP!.Value, g.MinQ!.Value, g.MaxQ!.Value));
        Assert.Equal(2, g.ReferencePriority);
        Assert.False(g.ConverterPlant);
        // No RegulatingControl: it does not regulate (a machine that says nothing would), and that is said.
        Assert.False(g.VoltageControl);
        Assert.Contains(net.Notes, n => n.Contains("ExternalNetworkInjection 'q1'") && n.Contains("has no RegulatingControl"));
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));
    }

    [Fact]
    public void An_external_network_injection_with_a_local_voltage_control_regulates_and_can_be_the_slack()
    {
        CimNetwork net = CimImporter.Parse(Doc(
            Eni("q1", "n2", El("ExternalNetworkInjection.p", "0") + El("ExternalNetworkInjection.referencePriority", "1")
                + Ref("RegulatingCondEq.RegulatingControl", "rcq")),
            LocalControl("rcq", "q1_t", "135.3"),
            """<cim:SynchronousMachine rdf:ID="_g1"><cim:RotatingMachine.p>-50</cim:RotatingMachine.p></cim:SynchronousMachine>""" + T("g1_t", "g1", "n1")));
        CimGenerator q = net.Generators.Single(x => x.MRID == "q1");
        Assert.Equal((true, (double?)135.3), (q.VoltageControl == true, q.TargetKv));
        var notes = new List<string>();
        GridModel model = CimGridModelBridge.ToGridModel(net, notes: notes);
        Bus n2 = model.Buses.Single(b => b.Name == "N2");
        Assert.Equal(BusType.Slack, n2.Type);   // referencePriority 1 beats the machine with none
        Assert.Equal(135.3 / 132.0, model.Generators.Single(x => x.Name == "q1").Vg);
        Assert.Contains(notes, n => n.Contains("machine q1") && n.Contains("referencePriority 1"));
    }

    [Fact]
    public void Contradictory_injection_limits_are_refused()
    {
        Assert.Contains("minQ", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Eni("q1", "n1",
            El("ExternalNetworkInjection.minQ", "10") + El("ExternalNetworkInjection.maxQ", "5"))))).Message);
        Assert.Contains("minP", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Ei("e1", "n1",
            El("EquivalentInjection.minP", "10") + El("EquivalentInjection.maxP", "5"))))).Message);
        Assert.Contains("negative", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Eni("q1", "n1",
            El("ExternalNetworkInjection.referencePriority", "-1"))))).Message);
    }

    [Theory]
    [InlineData("true", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("false", "false", false)]
    [InlineData("false", "true", false)]
    public void An_equivalent_injection_regulates_only_with_capability_and_status(string capability, string status, bool regulates)
    {
        CimNetwork net = CimImporter.Parse(Doc(Ei("e1", "n2",
            El("EquivalentInjection.p", "184") + El("EquivalentInjection.q", "-8")
            + El("EquivalentInjection.regulationCapability", capability) + El("EquivalentInjection.regulationStatus", status)
            + El("EquivalentInjection.regulationTarget", "133.65"))));
        CimGenerator g = Assert.Single(net.Generators);
        Assert.Equal("EquivalentInjection", g.Class);
        Assert.Equal((-184.0, 8.0), (g.P, g.Q));   // 184 MW leaving the node (load sign) = -184 MW generated
        Assert.Equal(regulates, g.VoltageControl == true);
        Assert.Equal(regulates ? 133.65 : (double?)null, g.TargetKv);
        Assert.Equal(!regulates && status == "true", net.Notes.Any(n => n.Contains("regulationStatus is true but regulationCapability is not")));
    }

    [Fact]
    public void A_regulating_equivalent_injection_without_a_target_is_refused()
    {
        Assert.Contains("regulationTarget", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Ei("e1", "n2",
            El("EquivalentInjection.regulationCapability", "true") + El("EquivalentInjection.regulationStatus", "true"))))).Message);
    }

    [Fact]
    public void An_equivalent_injection_bridges_to_a_generator_of_its_output()
    {
        CimNetwork net = CimImporter.Parse(Doc(Slack, Ei("e1", "n2", El("EquivalentInjection.p", "-30") + El("EquivalentInjection.q", "-4"))));
        GridModel model = CimGridModelBridge.ToGridModel(net);
        Generator e1 = model.Generators.Single(x => x.Name == "e1");
        Assert.Equal((2, 30.0, 4.0), (e1.Bus, e1.Pg, e1.Qg));
        Assert.Equal(BusType.Pq, model.Buses[1].Type);   // it does not regulate: its bus stays PQ
        Assert.True(new NewtonRaphsonPowerFlow().Solve(model).Converged);
    }

    [Theory]
    [InlineData("PhotoVoltaicUnit", "solar")]
    [InlineData("PowerElectronicsWindUnit", "wind")]
    [InlineData("BatteryUnit", "battery")]
    public void A_power_electronics_connection_is_converter_plant_typed_by_its_units(string unitClass, string type)
    {
        CimNetwork net = CimImporter.Parse(Doc(Slack,
            Pec("pe1", "n2", El("PowerElectronicsConnection.p", "-20") + El("PowerElectronicsConnection.q", "1")
                + El("PowerElectronicsConnection.minQ", "-5") + El("PowerElectronicsConnection.maxQ", "5")
                + El("PowerElectronicsConnection.ratedS", "25")),
            Unit(unitClass, "u2", "pe1", El("PowerElectronicsUnit.minP", "0") + El("PowerElectronicsUnit.maxP", "12.5")),
            Unit(unitClass, "u1", "pe1", El("PowerElectronicsUnit.minP", "0") + El("PowerElectronicsUnit.maxP", "10"))));
        CimGenerator g = net.Generators.Single(x => x.MRID == "pe1");
        Assert.Equal(("PowerElectronicsConnection", type, true), (g.Class, g.Type, g.ConverterPlant));
        Assert.Equal((20.0, -1.0, (double?)25.0), (g.P, g.Q, g.RatedSMva));
        Assert.Equal(0.0 + 10.0 + 12.5, g.MaxP);   // units summed in mRID order (u1, u2)
        Assert.Equal(0.0 + 0.0 + 0.0, g.MinP);
        Assert.False(g.VoltageControl);            // no RegulatingControl

        Generator bridged = CimGridModelBridge.ToGridModel(net).Generators.Single(x => x.Name == "pe1");
        Assert.True(bridged.ConverterInterfaced);
        Assert.True(bridged.IsConverterInterfaced);
        Assert.Equal(type, bridged.Type);
    }

    [Fact]
    public void A_power_electronics_connection_of_mixed_or_no_units_is_still_converter_plant()
    {
        CimNetwork mixed = CimImporter.Parse(Doc(Pec("pe1", "n2", ""), Unit("PhotoVoltaicUnit", "u1", "pe1"), Unit("BatteryUnit", "u2", "pe1")));
        Assert.Equal(("", true), (mixed.Generators[0].Type, mixed.Generators[0].ConverterPlant));
        Assert.Null(mixed.Generators[0].MaxP);   // the units give no maxP
        Assert.Contains(mixed.Notes, n => n.Contains("units of several kinds (battery, solar)"));

        CimNetwork none = CimImporter.Parse(Doc(Pec("pe1", "n2", "")));
        Assert.Equal(("", true), (none.Generators[0].Type, none.Generators[0].ConverterPlant));
        Assert.Contains(none.Notes, n => n.Contains("names no PowerElectronicsUnit"));
        Assert.True(CimGridModelBridge.ToGridModel(none).Generators[0].IsConverterInterfaced);
    }

    [Fact]
    public void A_power_electronics_unit_of_an_unknown_class_or_a_bad_rating_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Pec("pe1", "n2", ""), Unit("PowerElectronicsUnit", "u1", "pe1"))));
        Assert.Contains("cannot be mapped", ex.Message);
        Assert.Contains("u1", ex.Message);
        Assert.Contains("ratedS", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Doc(Pec("pe1", "n2",
            El("PowerElectronicsConnection.ratedS", "0"))))).Message);
    }

    [Fact]
    public void An_asynchronous_machine_is_a_fixed_load_in_the_load_sign()
    {
        CimNetwork net = CimImporter.Parse(Doc(Slack,
            """<cim:AsynchronousMachine rdf:ID="_m3"><cim:IdentifiedObject.name>M3</cim:IdentifiedObject.name><cim:RotatingMachine.p>5</cim:RotatingMachine.p><cim:RotatingMachine.q>3</cim:RotatingMachine.q></cim:AsynchronousMachine>""" + T("m3_t", "m3", "n2"),
            """<cim:AsynchronousMachine rdf:ID="_m4"><cim:RotatingMachine.p>-2</cim:RotatingMachine.p><cim:RotatingMachine.q>1</cim:RotatingMachine.q></cim:AsynchronousMachine>""" + T("m4_t", "m4", "n2")));
        Assert.Equal(new[] { ("m3", 5.0, 3.0), ("m4", -2.0, 1.0) }, net.Loads.Select(l => (l.MRID, l.P, l.Q)).ToArray());
        Assert.All(net.Loads, l => Assert.Equal("AsynchronousMachine", l.Class));
        Assert.Contains(net.Notes, n => n.StartsWith("2 AsynchronousMachine(s) are read as fixed P / Q loads"));
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));
        Bus n2 = CimGridModelBridge.ToGridModel(net).Buses[1];
        Assert.Equal((0.0 + 5.0 + -2.0, 0.0 + 3.0 + 1.0), (n2.Pd, n2.Qd));
    }

    [Fact]
    public void Injections_and_asynchronous_machines_survive_an_export_round_trip_exactly()
    {
        CimNetwork net = CimImporter.Parse(Doc(Slack,
            Eni("q1", "n1", El("ExternalNetworkInjection.p", "-80") + El("ExternalNetworkInjection.q", "12.5")
                + El("ExternalNetworkInjection.minP", "-800") + El("ExternalNetworkInjection.maxQ", "600")
                + El("ExternalNetworkInjection.referencePriority", "2")),
            Eni("q2", "n2", El("ExternalNetworkInjection.p", "1.25") + Ref("RegulatingCondEq.RegulatingControl", "rcq")),
            LocalControl("rcq", "q2_t", "133"),
            Ei("e1", "n2", El("EquivalentInjection.p", "184") + El("EquivalentInjection.maxQ", "50")
                + El("EquivalentInjection.regulationCapability", "true") + El("EquivalentInjection.regulationStatus", "true")
                + El("EquivalentInjection.regulationTarget", "133.65")),
            Ei("e2", "n1", El("EquivalentInjection.p", "-6")),
            Pec("pe1", "n2", El("PowerElectronicsConnection.p", "-20") + El("PowerElectronicsConnection.ratedS", "25")),
            Unit("PhotoVoltaicUnit", "u1", "pe1", El("PowerElectronicsUnit.minP", "0") + El("PowerElectronicsUnit.maxP", "22")),
            Pec("pe2", "n1", El("PowerElectronicsConnection.p", "-1")),
            """<cim:AsynchronousMachine rdf:ID="_m3"><cim:RotatingMachine.p>5</cim:RotatingMachine.p><cim:RotatingMachine.q>3</cim:RotatingMachine.q></cim:AsynchronousMachine>""" + T("m3_t", "m3", "n2")));
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        // Moved 2026-10 (C3, final batch; CIM2 N286, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // test's own count. The document holds the slack machine G1 plus q1, q2 (external network injections), e1, e2
        // (equivalent injections) and pe1, pe2 (power-electronics units): seven generators. Probed with 7, every
        // round-trip equality below holds exactly. Before -> after: 6 -> 7.
        Assert.Equal(7, net.Generators.Count);
        Assert.Equal(net.Generators.OrderBy(x => x.MRID, StringComparer.Ordinal), rt.Generators.OrderBy(x => x.MRID, StringComparer.Ordinal));
        Assert.Equal(net.Loads, rt.Loads);
    }

    [Fact]
    public void A_network_of_machines_and_lines_only_bridges_exactly_as_before()
    {
        // No shunt, no injection: every bus keeps Gs = Bs = 0 and every generator ConverterInterfaced = null.
        GridModel model = CimGridModelBridge.ToGridModel(CimImporter.Parse(Doc(Slack)));
        Assert.All(model.Buses, b => Assert.Equal((0.0, 0.0), (b.Gs, b.Bs)));
        Assert.All(model.Generators, g => Assert.Null(g.ConverterInterfaced));
    }
}
