using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Docs-session stream 10 (CIM importer), October 2026: D-DOC-099 (load classes), 100 (unparsable numbers refused),
/// 101 (no base voltage: resolved or refused, never assumed per-unit), 102 (tabular, symmetrical and asymmetrical
/// tap changers), 103 (Terminal.connected and inService honoured), 104 (generator data carried, documented slack
/// rule), 105 (NUM, identifier collisions refused), 106 (NUM, defined winding order, clashes refused) and 164 (the
/// bus-branch diagnostic counts only mapped ConnectivityNodes). Numbers are compared exactly (Assert.Equal on
/// doubles), against the same expression the importer is documented to evaluate.
/// </summary>
public class CimDocStream10Tests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";

    // A terminal joining equipment to a TopologicalNode.
    private static string T(string id, string equip, string node, string extra = "") =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/>{extra}</cim:Terminal>""";

    // ================================================================ D-DOC-099: load classes ==========

    private const string LoadClassesDoc = Head + """
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>N1</cim:IdentifiedObject.name></cim:TopologicalNode>
        <cim:ConformLoad rdf:ID="_c1"><cim:IdentifiedObject.name>CL</cim:IdentifiedObject.name>
          <cim:EnergyConsumer.p>10.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>2.25</cim:EnergyConsumer.q></cim:ConformLoad>
        <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#_c1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:NonConformLoad rdf:ID="_c2"><cim:IdentifiedObject.name>NCL</cim:IdentifiedObject.name>
          <cim:EnergyConsumer.p>3</cim:EnergyConsumer.p><cim:EnergyConsumer.q>1</cim:EnergyConsumer.q></cim:NonConformLoad>
        <cim:Terminal rdf:ID="_t2"><cim:Terminal.ConductingEquipment rdf:resource="#_c2"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:StationSupply rdf:ID="_c3"><cim:IdentifiedObject.name>SS</cim:IdentifiedObject.name>
          <cim:EnergyConsumer.p>0.5</cim:EnergyConsumer.p><cim:EnergyConsumer.q>0.125</cim:EnergyConsumer.q></cim:StationSupply>
        <cim:Terminal rdf:ID="_t3"><cim:Terminal.ConductingEquipment rdf:resource="#_c3"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:LinearShuntCompensator rdf:ID="_sh"><cim:IdentifiedObject.name>Cap1</cim:IdentifiedObject.name></cim:LinearShuntCompensator>
        <cim:Terminal rdf:ID="_t4"><cim:Terminal.ConductingEquipment rdf:resource="#_sh"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        """ + Tail;

    [Fact]
    public void D099_conform_nonconform_and_station_supply_loads_are_read()
    {
        // Before: only EnergyConsumer was read, so all three loads (14 MW) vanished without a note.
        CimNetwork net = CimImporter.Parse(LoadClassesDoc);
        Assert.Equal(new[] { "ConformLoad", "NonConformLoad", "StationSupply" }, net.Loads.Select(l => l.Class).ToArray());
        Assert.Equal(new[] { 10.5, 3.0, 0.5 }, net.Loads.Select(l => l.P).ToArray());
        Assert.Equal(new[] { 2.25, 1.0, 0.125 }, net.Loads.Select(l => l.Q).ToArray());
    }

    [Fact]
    public void D099_equipment_of_an_unread_class_is_listed_not_dropped_silently()
    {
        CimNetwork net = CimImporter.Parse(LoadClassesDoc);
        Assert.Contains(net.Notes, n => n.Contains("LinearShuntCompensator") && n.Contains("Cap1") && n.Contains("left out"));
    }

    [Fact]
    public void D099_load_classes_survive_an_export_round_trip()
    {
        CimNetwork net = CimImporter.Parse(LoadClassesDoc);
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Loads.Select(l => (l.MRID, l.Class, l.P, l.Q)), rt.Loads.Select(l => (l.MRID, l.Class, l.P, l.Q)));
    }

    // ================================================================ D-DOC-100: strict numbers ========

    private static string LineWithX(string x) => Head + $"""
        <cim:TopologicalNode rdf:ID="_n1"/><cim:TopologicalNode rdf:ID="_n2"/>
        <cim:ACLineSegment rdf:ID="_l1"><cim:IdentifiedObject.name>Line1</cim:IdentifiedObject.name>
          <cim:ACLineSegment.r>0.5</cim:ACLineSegment.r><cim:ACLineSegment.x>{x}</cim:ACLineSegment.x></cim:ACLineSegment>
        {T("t1", "l1", "n1")}{T("t2", "l1", "n2")}
        """ + Tail;

    [Theory]
    [InlineData("1,25")]      // decimal comma: used to be read as 0.0
    [InlineData("12ohm")]     // unit suffix
    [InlineData("")]          // empty element
    [InlineData("NaN")]       // parses, but is not a number
    [InlineData("Infinity")]
    public void D100_an_unparsable_number_is_refused_naming_element_and_attribute(string x)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(LineWithX(x)));
        Assert.Contains("ACLineSegment.x", ex.Message);
        Assert.Contains("Line1", ex.Message);
        Assert.Contains("l1", ex.Message);
        Assert.Contains($"'{x}'", ex.Message);
    }

    [Fact]
    public void D100_a_well_formed_number_still_parses_exactly()
    {
        Assert.Equal(1.25e1, CimImporter.Parse(LineWithX(" 1.25E1 ")).Branches.Single().X);
    }

    [Fact]
    public void D100_a_non_integer_sequence_number_and_a_bad_boolean_are_refused()
    {
        string seq = LineWithX("1").Replace(
            """<cim:Terminal.TopologicalNode rdf:resource="#_n1"/>""",
            """<cim:Terminal.TopologicalNode rdf:resource="#_n1"/><cim:ACDCTerminal.sequenceNumber>1.5</cim:ACDCTerminal.sequenceNumber>""");
        Assert.Contains("ACDCTerminal.sequenceNumber", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(seq)).Message);

        string flag = LineWithX("1").Replace(
            """<cim:Terminal.TopologicalNode rdf:resource="#_n1"/>""",
            """<cim:Terminal.TopologicalNode rdf:resource="#_n1"/><cim:ACDCTerminal.connected>yes</cim:ACDCTerminal.connected>""");
        Assert.Contains("ACDCTerminal.connected", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(flag)).Message);
    }

    [Fact]
    public void D100_a_base_voltage_without_a_positive_nominal_voltage_is_refused()
    {
        string doc = Head + """<cim:BaseVoltage rdf:ID="_bv"><cim:BaseVoltage.nominalVoltage>0</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>""" + Tail;
        Assert.Contains("nominalVoltage", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc)).Message);
    }

    // ================================================================ D-DOC-101: base voltage ==========

    private static CimNetwork TwoBus(double kv1, double kv2, double lineKv = 0.0) => new(
        new[] { new CimBus("n1", "A", kv1), new CimBus("n2", "B", kv2) },
        new[] { new CimBranch("l1", "L", "n1", "n2", 17.424, 87.12, 0.0001, lineKv) },
        Array.Empty<CimLoad>(),
        new[] { new CimGenerator("g", "G", "n1", 0.0) },
        Array.Empty<CimTransformer>());

    [Fact]
    public void D101_a_line_with_no_base_voltage_anywhere_is_refused_not_taken_as_per_unit()
    {
        // Before: zBase = 1.0, so 17.424 ohm became 17.424 pu without a word.
        var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(TwoBus(0, 0)));
        Assert.Contains("no base voltage", ex.Message);
        Assert.Contains("unbasedImpedancesArePerUnit", ex.Message);
    }

    [Fact]
    public void D101_a_caller_that_declares_per_unit_data_gets_the_values_unchanged()
    {
        Branch l = CimGridModelBridge.ToGridModel(TwoBus(0, 0), unbasedImpedancesArePerUnit: true).Branches.Single();
        Assert.Equal((17.424, 87.12, 0.0001), (l.R, l.X, l.B));
    }

    [Fact]
    public void D101_the_from_bus_base_is_used_exactly_as_before()
    {
        Branch l = CimGridModelBridge.ToGridModel(TwoBus(132, 132)).Branches.Single();
        double zb = 132.0 * 132.0 / 100.0;
        Assert.Equal((17.424 / zb, 87.12 / zb, 0.0001 * zb), (l.R, l.X, l.B));
    }

    [Fact]
    public void D101_the_to_bus_base_then_the_line_base_resolve_a_missing_from_bus_base()
    {
        double zb = 132.0 * 132.0 / 100.0;
        var notes = new List<string>();
        Branch viaTo = CimGridModelBridge.ToGridModel(TwoBus(0, 132), notes: notes).Branches.Single();
        Assert.Equal((17.424 / zb, 87.12 / zb), (viaTo.R, viaTo.X));
        Assert.Contains(notes, n => n.Contains("to bus") && n.Contains("132"));

        Branch viaLine = CimGridModelBridge.ToGridModel(TwoBus(0, 0, lineKv: 132)).Branches.Single();
        Assert.Equal((17.424 / zb, 87.12 / zb), (viaLine.R, viaLine.X));
    }

    [Fact]
    public void D101_a_line_between_two_different_base_voltages_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(TwoBus(132, 33)));
        Assert.Contains("one base voltage", ex.Message);
    }

    [Fact]
    public void D101_the_importer_reads_a_line_own_base_voltage()
    {
        string doc = Head + """
            <cim:BaseVoltage rdf:ID="_bv"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
            <cim:TopologicalNode rdf:ID="_n1"/><cim:TopologicalNode rdf:ID="_n2"/>
            <cim:ACLineSegment rdf:ID="_l1"><cim:ACLineSegment.x>17.424</cim:ACLineSegment.x>
              <cim:ConductingEquipment.BaseVoltage rdf:resource="#_bv"/></cim:ACLineSegment>
            """ + T("t1", "l1", "n1") + T("t2", "l1", "n2") + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal(132.0, net.Branches.Single().BaseKv);
        Assert.Equal(17.424 / (132.0 * 132.0 / 100.0), CimGridModelBridge.ToGridModel(net).Branches.Single().X);
    }

    [Fact]
    public void D101_a_winding_with_no_rated_voltage_uses_its_node_base_or_is_refused()
    {
        CimNetwork Tx(double kv) => new(
            new[] { new CimBus("n1", "HV", kv), new CimBus("n2", "LV", kv) },
            Array.Empty<CimBranch>(), Array.Empty<CimLoad>(), new[] { new CimGenerator("g", "G", "n1", 0.0) },
            new[] { new CimTransformer("t", "T", new[] { new CimTransformerEnd(1, "n1", 0.0, 17.424, 0.0), new CimTransformerEnd(2, "n2", 0.0, 0.0, 0.0) }, 1.0, 0.0) });

        Assert.Equal(17.424 / (132.0 * 132.0 / 100.0), CimGridModelBridge.ToGridModel(Tx(132)).Branches.Single().X);
        var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(Tx(0)));
        Assert.Contains("ratedU", ex.Message);
    }

    // ================================================================ D-DOC-102: tap changers ==========

    // A 230/132 kV two-winding transformer (end 1 at n1, end 2 at n2) with the given tap changers.
    private static string TwoWinding(string changers) => Head + """
        <cim:BaseVoltage rdf:ID="_bvHV"><cim:BaseVoltage.nominalVoltage>230</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:BaseVoltage rdf:ID="_bvLV"><cim:BaseVoltage.nominalVoltage>132</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:TopologicalNode rdf:ID="_n1"><cim:TopologicalNode.BaseVoltage rdf:resource="#_bvHV"/></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:TopologicalNode.BaseVoltage rdf:resource="#_bvLV"/></cim:TopologicalNode>
        <cim:PowerTransformer rdf:ID="_tx1"><cim:IdentifiedObject.name>T1</cim:IdentifiedObject.name></cim:PowerTransformer>
        <cim:PowerTransformerEnd rdf:ID="_e1"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
          <cim:TransformerEnd.endNumber>1</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te1"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>52.9</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>230</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:PowerTransformerEnd rdf:ID="_e2"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx1"/>
          <cim:TransformerEnd.endNumber>2</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_te2"/>
          <cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>0</cim:PowerTransformerEnd.x>
          <cim:PowerTransformerEnd.ratedU>132</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>
        <cim:Terminal rdf:ID="_te1"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:Terminal rdf:ID="_te2"><cim:Terminal.ConductingEquipment rdf:resource="#_tx1"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        """ + changers + Tail;

    private static string Steps(string step = "12", string neutral = "10") =>
        $"<cim:TapChanger.step>{step}</cim:TapChanger.step><cim:TapChanger.neutralStep>{neutral}</cim:TapChanger.neutralStep>";

    private static string Ratio(string id, string end, string inc = "1.25", string steps = "") =>
        $"""<cim:RatioTapChanger rdf:ID="_{id}"><cim:RatioTapChanger.TransformerEnd rdf:resource="#_{end}"/>{(steps.Length > 0 ? steps : Steps())}<cim:RatioTapChanger.stepVoltageIncrement>{inc}</cim:RatioTapChanger.stepVoltageIncrement></cim:RatioTapChanger>""";

    private static string NonLinear(string cls, string end, string du, string extra = "") =>
        $"""<cim:{cls} rdf:ID="_p"><cim:PhaseTapChanger.TransformerEnd rdf:resource="#_{end}"/>{Steps()}<cim:PhaseTapChangerNonLinear.voltageStepIncrement>{du}</cim:PhaseTapChangerNonLinear.voltageStepIncrement>{extra}</cim:{cls}>""";

    [Fact]
    public void D102_a_symmetrical_phase_tap_changer_gives_its_angle()
    {
        // Before: read as a linear changer with no increment, so the phase shift was 0 without a word.
        CimTransformer tx = CimImporter.Parse(TwoWinding(NonLinear("PhaseTapChangerSymmetrical", "e1", "1.5"))).Transformers.Single();
        double du = (12.0 - 10.0) * 1.5 / 100.0;
        Assert.Equal(2.0 * DetMath.Atan(du / 2.0) * 180.0 / Math.PI, tx.PhaseShiftDeg);
        Assert.InRange(tx.PhaseShiftDeg, 1.718, 1.719);   // 2 atan(0.015) = 1.7187 degrees
        Assert.Equal(1.0, tx.TapRatio);
    }

    [Fact]
    public void D102_an_asymmetrical_changer_at_zero_connection_angle_equals_a_ratio_changer_bit_for_bit()
    {
        CimTransformer asym = CimImporter.Parse(TwoWinding(NonLinear("PhaseTapChangerAsymmetrical", "e1", "1.25",
            "<cim:PhaseTapChangerAsymmetrical.windingConnectionAngle>0</cim:PhaseTapChangerAsymmetrical.windingConnectionAngle>"))).Transformers.Single();
        CimTransformer ratio = CimImporter.Parse(TwoWinding(Ratio("r", "e1"))).Transformers.Single();
        Assert.Equal(1.0 + (12.0 - 10.0) * 1.25 / 100.0, ratio.TapRatio);
        Assert.Equal(ratio.TapRatio, asym.TapRatio);
        Assert.Equal(0.0, asym.PhaseShiftDeg);
    }

    [Fact]
    public void D102_an_asymmetrical_changer_gives_ratio_and_angle()
    {
        CimTransformer tx = CimImporter.Parse(TwoWinding(NonLinear("PhaseTapChangerAsymmetrical", "e1", "2",
            "<cim:PhaseTapChangerAsymmetrical.windingConnectionAngle>60</cim:PhaseTapChangerAsymmetrical.windingConnectionAngle>"))).Transformers.Single();
        double du = (12.0 - 10.0) * 2.0 / 100.0, th = 60.0 * Math.PI / 180.0;
        double dx = 1.0 + du * DetMath.Cos(th), dy = du * DetMath.Sin(th);
        Assert.Equal(Math.Sqrt(dx * dx + dy * dy), tx.TapRatio);
        Assert.Equal(DetMath.Atan2(dy, dx) * 180.0 / Math.PI, tx.PhaseShiftDeg);
        Assert.InRange(tx.TapRatio, 1.0205, 1.0207);       // |1 + 0.04 e^(j60)| = sqrt(1.0416) = 1.02059
        Assert.InRange(tx.PhaseShiftDeg, 1.945, 1.946);    // atan(0.034641 / 1.02) = 1.94512 degrees
    }

    private const string RatioTable = """
        <cim:RatioTapChangerTable rdf:ID="_rt"/>
        <cim:RatioTapChangerTablePoint rdf:ID="_rp11"><cim:RatioTapChangerTablePoint.RatioTapChangerTable rdf:resource="#_rt"/>
          <cim:TapChangerTablePoint.step>11</cim:TapChangerTablePoint.step><cim:TapChangerTablePoint.ratio>1.01</cim:TapChangerTablePoint.ratio>
          <cim:TapChangerTablePoint.r>0</cim:TapChangerTablePoint.r><cim:TapChangerTablePoint.x>0</cim:TapChangerTablePoint.x></cim:RatioTapChangerTablePoint>
        <cim:RatioTapChangerTablePoint rdf:ID="_rp12"><cim:RatioTapChangerTablePoint.RatioTapChangerTable rdf:resource="#_rt"/>
          <cim:TapChangerTablePoint.step>12</cim:TapChangerTablePoint.step><cim:TapChangerTablePoint.ratio>1.02</cim:TapChangerTablePoint.ratio>
          <cim:TapChangerTablePoint.r>0</cim:TapChangerTablePoint.r><cim:TapChangerTablePoint.x>XCORR</cim:TapChangerTablePoint.x></cim:RatioTapChangerTablePoint>
        <cim:RatioTapChanger rdf:ID="_r"><cim:RatioTapChanger.TransformerEnd rdf:resource="#_e1"/>
          <cim:TapChanger.step>STEP</cim:TapChanger.step><cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>
          <cim:RatioTapChanger.RatioTapChangerTable rdf:resource="#_rt"/></cim:RatioTapChanger>
        """;

    [Fact]
    public void D102_a_tabular_ratio_changer_takes_the_ratio_of_its_step()
    {
        CimTransformer tx = CimImporter.Parse(TwoWinding(RatioTable.Replace("STEP", "12").Replace("XCORR", "0"))).Transformers.Single();
        Assert.Equal(1.02, tx.TapRatio);
    }

    [Fact]
    public void D102_a_tabular_step_with_no_point_or_an_impedance_correction_is_refused()
    {
        var missing = Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(TwoWinding(RatioTable.Replace("STEP", "13").Replace("XCORR", "0"))));
        Assert.Contains("no point for step 13", missing.Message);
        var corr = Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(TwoWinding(RatioTable.Replace("STEP", "12").Replace("XCORR", "2.5"))));
        Assert.Contains("TapChangerTablePoint.x", corr.Message);
    }

    [Fact]
    public void D102_a_tabular_phase_changer_takes_the_angle_and_ratio_of_its_step()
    {
        const string changer = """
            <cim:PhaseTapChangerTable rdf:ID="_pt"/>
            <cim:PhaseTapChangerTablePoint rdf:ID="_pp"><cim:PhaseTapChangerTablePoint.PhaseTapChangerTable rdf:resource="#_pt"/>
              <cim:TapChangerTablePoint.step>12</cim:TapChangerTablePoint.step><cim:TapChangerTablePoint.ratio>1</cim:TapChangerTablePoint.ratio>
              <cim:PhaseTapChangerTablePoint.angle>5.5</cim:PhaseTapChangerTablePoint.angle></cim:PhaseTapChangerTablePoint>
            <cim:PhaseTapChangerTabular rdf:ID="_p"><cim:PhaseTapChanger.TransformerEnd rdf:resource="#_e2"/>
              <cim:TapChanger.step>12</cim:TapChanger.step><cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>
              <cim:PhaseTapChangerTabular.PhaseTapChangerTable rdf:resource="#_pt"/></cim:PhaseTapChangerTabular>
            """;
        CimTransformer tx = CimImporter.Parse(TwoWinding(changer)).Transformers.Single();
        Assert.Equal(-5.5, tx.PhaseShiftDeg);   // on end 2, so referred to the from side with the sign reversed
        Assert.Equal(1.0 / 1.0, tx.TapRatio);
    }

    [Fact]
    public void D102_an_abstract_or_unknown_phase_changer_class_is_refused_by_name()
    {
        string doc = TwoWinding("""<cim:PhaseTapChanger rdf:ID="_p"><cim:PhaseTapChanger.TransformerEnd rdf:resource="#_e1"/></cim:PhaseTapChanger>""");
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("PhaseTapChanger", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void D102_no_ssh_step_uses_the_normal_step_and_no_step_at_all_is_refused()
    {
        string normal = Ratio("r", "e1", steps: "<cim:TapChanger.normalStep>12</cim:TapChanger.normalStep><cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>");
        CimNetwork net = CimImporter.Parse(TwoWinding(normal));
        Assert.Equal(1.0 + (12.0 - 10.0) * 1.25 / 100.0, net.Transformers.Single().TapRatio);
        Assert.Contains(net.Notes, n => n.Contains("normalStep"));

        // Before: the absent step read as 0, so with neutral 10 the tap sat ten steps low (0.875) without a word.
        string none = Ratio("r", "e1", steps: "<cim:TapChanger.neutralStep>10</cim:TapChanger.neutralStep>");
        Assert.Contains("position is unknown", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(TwoWinding(none))).Message);
    }

    [Fact]
    public void D102_a_missing_increment_and_a_second_changer_on_one_end_are_refused()
    {
        string noInc = """<cim:RatioTapChanger rdf:ID="_r"><cim:RatioTapChanger.TransformerEnd rdf:resource="#_e1"/>""" + Steps() + "</cim:RatioTapChanger>";
        Assert.Contains("stepVoltageIncrement", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(TwoWinding(noInc))).Message);
        Assert.Contains("ambiguous", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(TwoWinding(Ratio("r1", "e1") + Ratio("r2", "e1")))).Message);
    }

    [Fact]
    public void D102_ratio_changers_on_both_ends_combine_instead_of_the_second_being_dropped()
    {
        // Before: only end 1's changer was read (tap 1.025); end 2's was ignored.
        CimTransformer tx = CimImporter.Parse(TwoWinding(Ratio("r1", "e1") + Ratio("r2", "e2", steps: Steps("9", "10")))).Transformers.Single();
        double r1 = 1.0 + (12.0 - 10.0) * 1.25 / 100.0, r2 = 1.0 + (9.0 - 10.0) * 1.25 / 100.0;
        Assert.Equal(r1 / r2, tx.TapRatio);
    }

    [Fact]
    public void D102_one_sided_ratio_changers_keep_their_old_expressions_exactly()
    {
        Assert.Equal(1.0 + (12.0 - 10.0) * 1.25 / 100.0, CimImporter.Parse(TwoWinding(Ratio("r", "e1"))).Transformers.Single().TapRatio);
        Assert.Equal(1.0 / (1.0 + (12.0 - 10.0) * 1.25 / 100.0), CimImporter.Parse(TwoWinding(Ratio("r", "e2"))).Transformers.Single().TapRatio);
    }

    // ================================================================ D-DOC-103: connected, inService ===

    [Fact]
    public void D103_a_disconnected_terminal_takes_the_line_out_and_is_listed()
    {
        string doc = LineWithX("1").Replace(
            """<cim:Terminal.TopologicalNode rdf:resource="#_n2"/>""",
            """<cim:Terminal.TopologicalNode rdf:resource="#_n2"/><cim:ACDCTerminal.connected>false</cim:ACDCTerminal.connected>""");
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Empty(net.Branches);   // before: the open line stayed in service
        Assert.Contains(net.Notes, n => n.Contains("t2") && n.Contains("disconnected"));
        Assert.Contains(net.Notes, n => n.Contains("Line1") && n.Contains("1 connected terminal"));

        // connected = true (or absent) keeps it.
        Assert.Single(CimImporter.Parse(doc.Replace(">false<", ">true<")).Branches);
    }

    [Fact]
    public void D103_out_of_service_equipment_is_left_out_and_listed()
    {
        string doc = LoadClassesDoc.Replace(
            "<cim:EnergyConsumer.p>3</cim:EnergyConsumer.p>",
            "<cim:EnergyConsumer.p>3</cim:EnergyConsumer.p><cim:Equipment.inService>false</cim:Equipment.inService>");
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal(new[] { "c1", "c3" }, net.Loads.Select(l => l.MRID).ToArray());
        Assert.Contains(net.Notes, n => n.Contains("NCL") && n.Contains("out of service"));
    }

    // ================================================================ D-DOC-104: generator data, slack ===

    private const string Machines = Head + """
        <cim:BaseVoltage rdf:ID="_bv"><cim:BaseVoltage.nominalVoltage>400</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>
        <cim:TopologicalNode rdf:ID="_n1"><cim:IdentifiedObject.name>A</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv"/></cim:TopologicalNode>
        <cim:TopologicalNode rdf:ID="_n2"><cim:IdentifiedObject.name>B</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_bv"/></cim:TopologicalNode>
        <cim:ACLineSegment rdf:ID="_l1"><cim:ACLineSegment.r>1.6</cim:ACLineSegment.r><cim:ACLineSegment.x>16</cim:ACLineSegment.x></cim:ACLineSegment>
        <cim:Terminal rdf:ID="_tl1"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:Terminal rdf:ID="_tl2"><cim:Terminal.ConductingEquipment rdf:resource="#_l1"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:ThermalGeneratingUnit rdf:ID="_gu1"><cim:GeneratingUnit.minOperatingP>10</cim:GeneratingUnit.minOperatingP>
          <cim:GeneratingUnit.maxOperatingP>150</cim:GeneratingUnit.maxOperatingP></cim:ThermalGeneratingUnit>
        <cim:RegulatingControl rdf:ID="_rc1"><cim:RegulatingControl.mode rdf:resource="http://iec.ch/TC57/2013/CIM-schema-cim16#RegulatingControlModeKind.voltage"/>
          <cim:RegulatingControl.Terminal rdf:resource="#_tg1"/><cim:RegulatingControl.targetValue>404</cim:RegulatingControl.targetValue>
          <cim:RegulatingControl.enabled>true</cim:RegulatingControl.enabled></cim:RegulatingControl>
        <cim:SynchronousMachine rdf:ID="_g1"><cim:IdentifiedObject.name>G1</cim:IdentifiedObject.name>
          <cim:RotatingMachine.p>-100</cim:RotatingMachine.p><cim:RotatingMachine.q>-20</cim:RotatingMachine.q>
          <cim:RotatingMachine.ratedS>200</cim:RotatingMachine.ratedS>
          <cim:SynchronousMachine.minQ>-50</cim:SynchronousMachine.minQ><cim:SynchronousMachine.maxQ>80</cim:SynchronousMachine.maxQ>
          <cim:SynchronousMachine.referencePriority>2</cim:SynchronousMachine.referencePriority>
          <cim:RotatingMachine.GeneratingUnit rdf:resource="#_gu1"/>
          <cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rc1"/>
          <cim:RegulatingCondEq.controlEnabled>true</cim:RegulatingCondEq.controlEnabled></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_tg1"><cim:Terminal.ConductingEquipment rdf:resource="#_g1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
        <cim:SynchronousMachineTimeConstantReactance rdf:ID="_dy1"><cim:SynchronousMachineDynamics.SynchronousMachine rdf:resource="#_g1"/>
          <cim:RotatingMachineDynamics.inertia>4.5</cim:RotatingMachineDynamics.inertia></cim:SynchronousMachineTimeConstantReactance>
        <cim:RegulatingControl rdf:ID="_rc2"><cim:RegulatingControl.mode rdf:resource="http://iec.ch/TC57/2013/CIM-schema-cim16#RegulatingControlModeKind.voltage"/>
          <cim:RegulatingControl.Terminal rdf:resource="RC2TERM"/><cim:RegulatingControl.targetValue>396</cim:RegulatingControl.targetValue></cim:RegulatingControl>
        <cim:SynchronousMachine rdf:ID="_g2"><cim:IdentifiedObject.name>G2</cim:IdentifiedObject.name>
          <cim:RotatingMachine.p>-50</cim:RotatingMachine.p><cim:RotatingMachine.q>-5</cim:RotatingMachine.q>
          <cim:RotatingMachine.ratedS>100</cim:RotatingMachine.ratedS>
          <cim:SynchronousMachine.referencePriority>1</cim:SynchronousMachine.referencePriority>
          <cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rc2"/></cim:SynchronousMachine>
        <cim:Terminal rdf:ID="_tg2"><cim:Terminal.ConductingEquipment rdf:resource="#_g2"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        <cim:EnergyConsumer rdf:ID="_ld"><cim:EnergyConsumer.p>140</cim:EnergyConsumer.p><cim:EnergyConsumer.q>20</cim:EnergyConsumer.q></cim:EnergyConsumer>
        <cim:Terminal rdf:ID="_tld"><cim:Terminal.ConductingEquipment rdf:resource="#_ld"/><cim:Terminal.TopologicalNode rdf:resource="#_n2"/></cim:Terminal>
        """ + Tail;

    private static readonly string LocalMachines = Machines.Replace("RC2TERM", "#_tg2");

    [Fact]
    public void D104_the_importer_reads_machine_limits_base_inertia_control_and_priority()
    {
        CimNetwork net = CimImporter.Parse(LocalMachines);
        CimGenerator g1 = net.Generators.Single(g => g.MRID == "g1"), g2 = net.Generators.Single(g => g.MRID == "g2");
        Assert.Equal((100.0, 20.0, 404.0, true), (g1.P, g1.Q, g1.TargetKv!.Value, g1.VoltageControl!.Value));
        Assert.Equal((-50.0, 80.0, 10.0, 150.0), (g1.MinQ!.Value, g1.MaxQ!.Value, g1.MinP!.Value, g1.MaxP!.Value));
        Assert.Equal((200.0, 4.5, 2), (g1.RatedSMva!.Value, g1.H!.Value, g1.ReferencePriority));
        Assert.Equal((396.0, true, 1), (g2.TargetKv!.Value, g2.VoltageControl!.Value, g2.ReferencePriority));
        Assert.Null(g2.H);
    }

    [Fact]
    public void D104_the_bridge_carries_the_data_and_chooses_the_slack_by_reference_priority()
    {
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(LocalMachines), notes: notes);
        // Before: G1 (first in the document) was always the slack, H = 0, Vg = 1.0, Q dropped, limits unbounded.
        Assert.Equal(BusType.Pv, m.Buses[0].Type);
        Assert.Equal(BusType.Slack, m.Buses[1].Type);   // G2 has referencePriority 1
        Assert.Contains(notes, n => n.Contains("bus 2") && n.Contains("referencePriority 1"));
        Generator g1 = m.Generators.Single(g => g.Name == "G1");
        Assert.Equal((100.0, 20.0, 404.0 / 400.0), (g1.Pg, g1.Qg, g1.Vg));
        Assert.Equal((-50.0, 80.0, 10.0, 150.0, 200.0, 4.5), (g1.Qmin, g1.Qmax, g1.Pmin, g1.Pmax, g1.MBase, g1.H));
        Assert.Equal(396.0 / 400.0, m.Generators.Single(g => g.Name == "G2").Vg);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    [Fact]
    public void D104_remote_voltage_control_is_held_at_its_q_and_listed()
    {
        // G2's control regulates the line terminal at n1, not its own node n2.
        CimNetwork net = CimImporter.Parse(Machines.Replace("RC2TERM", "#_tl1"));
        CimGenerator g2 = net.Generators.Single(g => g.MRID == "g2");
        Assert.False(g2.VoltageControl);
        Assert.Null(g2.TargetKv);
        Assert.Contains(net.Notes, n => n.Contains("G2") && n.Contains("remote voltage control"));
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal(BusType.Slack, m.Buses[0].Type);   // G2 no longer controls a voltage, so G1 is the reference
        Assert.Equal(BusType.Pq, m.Buses[1].Type);      // and G2's bus injects its fixed P and Q
    }

    [Fact]
    public void D104_with_no_priority_the_largest_unit_then_the_lowest_bus_is_the_slack()
    {
        CimNetwork Net(double maxP1, double maxP2) => new(
            new[] { new CimBus("n1", "A", 400), new CimBus("n2", "B", 400) },
            new[] { new CimBranch("l", "L", "n1", "n2", 1.6, 16, 0) }, Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g1", "G1", "n1", 10, MaxP: maxP1), new CimGenerator("g2", "G2", "n2", 10, MaxP: maxP2) },
            Array.Empty<CimTransformer>());
        var notes = new List<string>();
        Assert.Equal(BusType.Slack, CimGridModelBridge.ToGridModel(Net(100, 300), notes: notes).Buses[1].Type);
        Assert.Contains(notes, n => n.Contains("largest unit"));
        notes.Clear();
        Assert.Equal(BusType.Slack, CimGridModelBridge.ToGridModel(Net(300, 300), notes: notes).Buses[0].Type);
        Assert.Contains(notes, n => n.Contains("lowest bus number"));
    }

    [Fact]
    public void D104_control_disabled_makes_a_pq_bus_that_injects_its_q()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "A", 400), new CimBus("n2", "B", 400) },
            new[] { new CimBranch("l", "L", "n1", "n2", 1.6, 16, 0) }, new[] { new CimLoad("ld", "Ld", "n2", 50, 10) },
            new[] { new CimGenerator("g1", "G1", "n1", 0), new CimGenerator("g2", "G2", "n2", 30, Q: 7, VoltageControl: false) },
            Array.Empty<CimTransformer>());
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal((BusType.Slack, BusType.Pq), (m.Buses[0].Type, m.Buses[1].Type));
        Assert.Equal(7.0, m.Generators.Single(g => g.Name == "G2").Qg);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    [Fact]
    public void D104_contradictory_targets_and_an_inertia_without_a_base_are_refused()
    {
        var two = new CimNetwork(
            new[] { new CimBus("n1", "A", 400) }, Array.Empty<CimBranch>(), Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g1", "G1", "n1", 0, TargetKv: 404, VoltageControl: true),
                    new CimGenerator("g2", "G2", "n1", 0, TargetKv: 400, VoltageControl: true) },
            Array.Empty<CimTransformer>());
        Assert.Contains("different voltage targets", Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(two)).Message);

        string noBase = LocalMachines.Replace("<cim:RotatingMachine.ratedS>200</cim:RotatingMachine.ratedS>", "");
        Assert.Contains("ratedS", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(noBase)).Message);
    }

    [Fact]
    public void D104_machine_data_survive_an_export_round_trip_exactly()
    {
        CimNetwork net = CimImporter.Parse(LocalMachines);
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        // Every field (P, Q, target, control, limits, base, H, priority) is equal, bit for bit.
        Assert.Equal(net.Generators, rt.Generators);
    }

    // ================================================================ D-DOC-105: identifier collisions ==

    [Fact]
    public void D105_mrids_differing_by_a_second_underscore_are_two_objects()
    {
        // Before: NormaliseId stripped every leading '_', so _a and __a merged into one bus without a word.
        string doc = Head + """
            <cim:TopologicalNode rdf:ID="_a"><cim:IdentifiedObject.name>One</cim:IdentifiedObject.name></cim:TopologicalNode>
            <cim:TopologicalNode rdf:ID="__a"><cim:IdentifiedObject.name>Two</cim:IdentifiedObject.name></cim:TopologicalNode>
            """ + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal(new[] { ("a", "One"), ("_a", "Two") }, net.Buses.Select(b => (b.MRID, b.Name)).ToArray());
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Buses.Select(b => b.MRID), rt.Buses.Select(b => b.MRID));
    }

    [Fact]
    public void D105_two_ids_that_reduce_to_one_mrid_are_refused()
    {
        string doc = Head + """<cim:TopologicalNode rdf:ID="a"/><cim:TopologicalNode rdf:ID="_a"/>""" + Tail;
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc));
        Assert.Contains("'a'", ex.Message);
        Assert.Contains("'_a'", ex.Message);
    }

    [Fact]
    public void D105_a_reference_spelled_differently_from_its_object_is_refused()
    {
        string doc = Head + """
            <cim:TopologicalNode rdf:ID="_n1"/><cim:TopologicalNode rdf:ID="_n2"/>
            <cim:ACLineSegment rdf:ID="_l1"><cim:ACLineSegment.x>1</cim:ACLineSegment.x></cim:ACLineSegment>
            <cim:Terminal rdf:ID="_t1"><cim:Terminal.ConductingEquipment rdf:resource="#l1"/><cim:Terminal.TopologicalNode rdf:resource="#_n1"/></cim:Terminal>
            """ + Tail;
        Assert.Contains("'#l1'", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(doc)).Message);
    }

    // ================================================================ D-DOC-106: winding order ============

    private static string Windings(string n1, string n2, bool firstListedIsEnd2 = false)
    {
        string End(string id, string term, string num, string x, string kv) =>
            $"""<cim:PowerTransformerEnd rdf:ID="_{id}"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_tx"/>{(num.Length > 0 ? $"<cim:TransformerEnd.endNumber>{num}</cim:TransformerEnd.endNumber>" : "")}<cim:TransformerEnd.Terminal rdf:resource="#_{term}"/><cim:PowerTransformerEnd.x>{x}</cim:PowerTransformerEnd.x><cim:PowerTransformerEnd.ratedU>{kv}</cim:PowerTransformerEnd.ratedU></cim:PowerTransformerEnd>""";
        string hv = End("eh", "th", n1, "52.9", "230"), lv = End("el", "tl", n2, "0", "132");
        return Head + """
            <cim:TopologicalNode rdf:ID="_hv"/><cim:TopologicalNode rdf:ID="_lv"/>
            <cim:PowerTransformer rdf:ID="_tx"><cim:IdentifiedObject.name>T</cim:IdentifiedObject.name></cim:PowerTransformer>
            <cim:Terminal rdf:ID="_th"><cim:Terminal.ConductingEquipment rdf:resource="#_tx"/><cim:Terminal.TopologicalNode rdf:resource="#_hv"/></cim:Terminal>
            <cim:Terminal rdf:ID="_tl"><cim:Terminal.ConductingEquipment rdf:resource="#_tx"/><cim:Terminal.TopologicalNode rdf:resource="#_lv"/></cim:Terminal>
            """ + (firstListedIsEnd2 ? lv + hv : hv + lv) + Tail;
    }

    [Fact]
    public void D106_ends_are_ordered_by_their_number_whatever_the_document_order()
    {
        CimTransformer tx = CimImporter.Parse(Windings("1", "2", firstListedIsEnd2: true)).Transformers.Single();
        Assert.Equal(new[] { (1, "hv"), (2, "lv") }, tx.Ends.Select(e => (e.EndNumber, e.Node)).ToArray());
    }

    [Fact]
    public void D106_unnumbered_ends_take_document_order_and_say_so()
    {
        CimNetwork net = CimImporter.Parse(Windings("", "", firstListedIsEnd2: true));
        Assert.Equal(new[] { (1, "lv"), (2, "hv") }, net.Transformers.Single().Ends.Select(e => (e.EndNumber, e.Node)).ToArray());
        Assert.Contains(net.Notes, n => n.Contains("document order"));
    }

    [Fact]
    public void D106_a_number_clash_or_a_mix_of_numbered_and_unnumbered_ends_is_refused()
    {
        // Before: an unnumbered end after an end numbered 2 got 1 + 1 = 2, and List.Sort left the order undefined.
        Assert.Contains("ambiguous", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Windings("2", ""))).Message);
        Assert.Contains("both have", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Windings("1", "1"))).Message);
        Assert.Contains("endNumber", Assert.Throws<InvalidDataException>(() => CimImporter.Parse(Windings("0", "1"))).Message);
    }

    // ================================================================ D-DOC-164: CN diagnostic ===========

    [Fact]
    public void D164_only_mapped_connectivity_nodes_are_counted_as_mapped()
    {
        string doc = Head + """
            <cim:TopologicalNode rdf:ID="_tn1"/>
            <cim:ConnectivityNode rdf:ID="_cn1"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_tn1"/></cim:ConnectivityNode>
            <cim:ConnectivityNode rdf:ID="_cn2"/>
            <cim:ConnectivityNode rdf:ID="_cn3"><cim:ConnectivityNode.TopologicalNode rdf:resource="#_tnBoundary"/></cim:ConnectivityNode>
            """ + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        // Before: "3 ConnectivityNode(s) are mapped onto them".
        Assert.Contains(net.Notes, n => n.Contains("1 ConnectivityNode(s) are mapped onto them"));
        Assert.Contains(net.Notes, n => n.Contains("name no TopologicalNode") && n.Contains("cn2"));
        Assert.Contains(net.Notes, n => n.Contains("not in this document") && n.Contains("cn3"));
    }
}
