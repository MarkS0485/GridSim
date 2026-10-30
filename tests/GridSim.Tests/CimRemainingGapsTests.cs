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
/// CIM3 stream (October 2026): the remaining CIM / CGMES gaps the ENTSO-E conformity models showed.
/// N294: a line between two base voltages (a CGMES boundary node of 400 kV nominal on a 380 kV network) is per-unit on
/// each end's own base with the ratio as an off-nominal tap (exact), refused only beyond a ratio of 1.1.
/// N295: SeriesCompensator (a series branch), StaticVarCompensator (a susceptance with slope, regulated by
/// CimControlledPowerFlow), the InitialReactiveCapabilityCurve at the machine's P, remote voltage control (read, held at q
/// by a plain solve, regulated by CimControlledPowerFlow), and the CGMES 3.0 boundary (EQ_BD without TP_BD).
/// N296: voltage-regulating linear shunts (discrete through SwitchedShuntPowerFlow, or continuous), and a slack per
/// energised island elected by the main island's rule.
/// Numbers are compared exactly (Assert.Equal on doubles) against the expression the bridge is documented to evaluate,
/// and solved states against the physics they must reproduce.
/// </summary>
public class CimRemainingGapsTests
{
    // ================================================================ N294: two base voltages on one line ======

    // Bus n1 (kv1) with a slack machine holding targetKv, a line (r, x, b in ohm / siemens) to bus n2 (kv2), and an
    // optional load at n2.
    private static CimNetwork TwoBases(double kv1, double kv2, double targetKv, double loadMw = 0.0, double loadMvar = 0.0,
        double r = 1.6, double x = 16.0, double b = 0.0001) => new(
        new[] { new CimBus("n1", "A", kv1), new CimBus("n2", "B", kv2) },
        new[] { new CimBranch("l1", "L", "n1", "n2", r, x, b) },
        loadMw == 0.0 && loadMvar == 0.0 ? Array.Empty<CimLoad>() : new[] { new CimLoad("ld", "LD", "n2", loadMw, loadMvar) },
        new[] { new CimGenerator("g", "G", "n1", 0.0, TargetKv: targetKv, VoltageControl: true) },
        Array.Empty<CimTransformer>());

    [Fact]
    public void N294_a_line_between_380_and_400_kV_is_per_unit_on_each_end_with_the_ratio_as_a_tap()
    {
        var notes = new List<string>();
        Branch l = CimGridModelBridge.ToGridModel(TwoBases(380, 400, 400), notes: notes).Branches.Single();
        double zb = 400.0 * 400.0 / 100.0;   // the TO bus base
        Assert.Equal((1.6 / zb, 16.0 / zb, 0.0001 * zb), (l.R, l.X, l.B));
        Assert.Equal(400.0 / 380.0, l.TapRatio);
        Assert.Equal(0.0, l.PhaseShiftDeg);
        Assert.False(l.IsTransformer);   // still a line: the tap is a per-unit relabelling, not a transformer
        Assert.Contains(notes, n => n.StartsWith("Line L: its ends are on different base voltages (380 kV at bus 'A', 400 kV at bus 'B')")
                                    && n.Contains("(exact)"));
    }

    [Fact]
    public void N294_at_no_load_the_far_end_shows_the_same_physical_kilovolts()
    {
        // No charging and no load: no current, so both ends are at the same physical voltage. The slack holds 380 kV
        // (1.0 pu on its 380 kV base); the 400 kV-based end must read 380 kV, i.e. 0.95 pu.
        GridModel m = CimGridModelBridge.ToGridModel(TwoBases(380, 400, 380, b: 0.0));
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(1.0, r.Vm[0]);
        Assert.True(Math.Abs(r.Vm[1] * 400.0 - 380.0) <= 1e-6, $"far end {r.Vm[1] * 400.0} kV");
        Assert.True(Math.Abs(r.VaDeg[1]) <= 1e-6);
    }

    [Fact]
    public void N294_the_two_base_model_solves_to_the_same_physical_state_as_the_one_base_model()
    {
        // The same physical network twice: (a) both ends on 400 kV; (b) the slack end on 380 kV holding the same 400 kV
        // (400 / 380 pu). With charging and a load, every physical quantity must agree: the tap model is exact.
        GridModel one = CimGridModelBridge.ToGridModel(TwoBases(400, 400, 400, loadMw: 300, loadMvar: 80));
        GridModel two = CimGridModelBridge.ToGridModel(TwoBases(380, 400, 400, loadMw: 300, loadMvar: 80));
        Assert.Equal(400.0 / 380.0, two.Generators.Single().Vg);
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(one), b = new NewtonRaphsonPowerFlow().Solve(two);
        Assert.True(a.Converged && b.Converged);
        Assert.True(Math.Abs(a.Vm[1] * 400.0 - b.Vm[1] * 400.0) <= 1e-6, $"{a.Vm[1] * 400.0} vs {b.Vm[1] * 400.0} kV");
        Assert.True(Math.Abs(a.VaDeg[1] - b.VaDeg[1]) <= 1e-6);
        Assert.True(Math.Abs(a.PInjMw[0] - b.PInjMw[0]) <= 1e-5);   // the same losses, so the same slack output
        Assert.True(Math.Abs(a.QInjMvar[0] - b.QInjMvar[0]) <= 1e-5);
        // The line flow at the far end is the same physical MW / MVAr.
        Assert.True(Math.Abs(a.LineFlows[0].PToMw - b.LineFlows[0].PToMw) <= 1e-5);
        Assert.True(Math.Abs(a.LineFlows[0].QToMvar - b.LineFlows[0].QToMvar) <= 1e-5);
    }

    [Theory]
    [InlineData(10.0, 11.0, true)]     // 1.1 exactly: at the bound, accepted (IEC 10 / 11 kV naming)
    [InlineData(220.0, 225.0, true)]   // the MicroGrid BE 220 kV boundary on a 225 kV network
    [InlineData(400.0, 380.0, true)]   // either orientation
    [InlineData(10.0, 11.5, false)]    // 1.15: two levels
    [InlineData(110.0, 132.0, false)]  // 1.2: two levels
    public void N294_bases_up_to_ten_percent_apart_are_accepted_and_further_apart_refused(double kv1, double kv2, bool accepted)
    {
        CimNetwork net = TwoBases(kv1, kv2, kv1);
        if (accepted)
        {
            Assert.Equal(kv2 / kv1, CimGridModelBridge.ToGridModel(net).Branches.Single().TapRatio);
        }
        else
        {
            var ex = Assert.Throws<InvalidDataException>(() => CimGridModelBridge.ToGridModel(net));
            Assert.Contains("one base voltage", ex.Message);   // D-DOC-101's wording kept
            Assert.Contains("wrong bus", ex.Message);
        }
    }

    [Fact]
    public void N294_a_line_on_one_base_voltage_is_the_branch_it_always_was()
    {
        Branch l = CimGridModelBridge.ToGridModel(TwoBases(132, 132, 132)).Branches.Single();
        double zb = 132.0 * 132.0 / 100.0;
        Assert.Equal(new Branch(1, 2, 1.6 / zb, 16.0 / zb, 0.0001 * zb, Name: "L"), l);
    }

    // ================================================================ line conductance (benchmark) ==========

    [Fact]
    public void A_line_conductance_is_half_at_each_end_as_bus_shunt_conductance_on_each_end_base()
    {
        // 380 / 400 kV ends (N294) and gch = 4.2e-5 S: each end draws gch / 2 x V^2, on its OWN base.
        CimNetwork net = TwoBases(380, 400, 380) with
        {
            Branches = new[] { new CimBranch("l1", "L", "n1", "n2", 0.42, 6.3, 6.59734e-5, G: 4.2e-5) },
        };
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(net, notes: notes);
        Assert.Equal(0.0 + 4.2e-5 / 2.0 * (380.0 * 380.0), m.Buses[0].Gs);
        Assert.Equal(0.0 + 4.2e-5 / 2.0 * (400.0 * 400.0), m.Buses[1].Gs);
        Assert.Contains(notes, n => n.StartsWith("Line shunt conductance (ACLineSegment.gch)"));
        // The branch itself is as it would be without gch.
        Assert.Equal(CimGridModelBridge.ToGridModel(net with { Branches = new[] { net.Branches[0] with { G = 0.0 } } }).Branches, m.Branches);
    }

    [Fact]
    public void A_line_conductance_is_read_and_survives_an_export_round_trip()
    {
        string doc = Head + Bv("bv132", "132") + Tn("n1", "bv132") + Tn("n2", "bv132")
            + $"""<cim:ACLineSegment rdf:ID="_l1">{Name("L1")}{El("ACLineSegment.r", "1")}{El("ACLineSegment.x", "10")}{El("ACLineSegment.gch", "0.00003")}</cim:ACLineSegment>"""
            + T("l1_1", "l1", "n1", 1) + T("l1_2", "l1", "n2", 2) + Tail;
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal(0.00003, net.Branches.Single().G);
        Assert.Equal(net.Branches.Single(), CimImporter.Parse(CimExporter.Export(net)).Branches.Single());
        // A line with no gch exports no gch (the export of every network before is unchanged).
        CimNetwork none = net with { Branches = new[] { net.Branches[0] with { G = 0.0 } } };
        Assert.DoesNotContain("gch", CimExporter.Export(none));
    }

    // ================================================================ document helpers =====================

    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";
    private const string Ns = "http://iec.ch/TC57/2013/CIM-schema-cim16#";

    private static string Name(string n) => $"<cim:IdentifiedObject.name>{n}</cim:IdentifiedObject.name>";
    private static string El(string prop, string value) => $"<cim:{prop}>{value}</cim:{prop}>";
    private static string Ref(string prop, string id) => $"""<cim:{prop} rdf:resource="#_{id}"/>""";
    private static string EnumRef(string prop, string value) => $"""<cim:{prop} rdf:resource="{Ns}{value}"/>""";

    private static string Bv(string id, string kv) =>
        $"""<cim:BaseVoltage rdf:ID="_{id}">{El("BaseVoltage.nominalVoltage", kv)}</cim:BaseVoltage>""";

    private static string Tn(string id, string bv) =>
        $"""<cim:TopologicalNode rdf:ID="_{id}">{Name(id.ToUpperInvariant())}{Ref("TopologicalNode.BaseVoltage", bv)}</cim:TopologicalNode>""";

    private static string T(string id, string equip, string node, int seq = 0) =>
        $"""<cim:Terminal rdf:ID="_{id}">{Ref("Terminal.ConductingEquipment", equip)}{Ref("Terminal.TopologicalNode", node)}{(seq > 0 ? El("ACDCTerminal.sequenceNumber", seq.ToString()) : "")}</cim:Terminal>""";

    private static string Line(string id, string a, string b, string r, string x) =>
        $"""<cim:ACLineSegment rdf:ID="_{id}">{Name(id)}{El("ACLineSegment.r", r)}{El("ACLineSegment.x", x)}</cim:ACLineSegment>"""
        + T(id + "_1", id, a, 1) + T(id + "_2", id, b, 2);

    private static string Load(string id, string node, string p, string q) =>
        $"""<cim:EnergyConsumer rdf:ID="_{id}">{Name(id)}{El("EnergyConsumer.p", p)}{El("EnergyConsumer.q", q)}</cim:EnergyConsumer>""" + T(id + "_t", id, node);

    // A synchronous machine (p in the CGMES load sign) whose voltage RegulatingControl "rc_{id}" names terminal
    // regulatedTerminal (its own "{id}_t" for local control) with the target in kV.
    private static string Machine(string id, string node, string p, string regulatedTerminal, string targetKv, string extra = "") =>
        $"""<cim:SynchronousMachine rdf:ID="_{id}">{Name(id.ToUpperInvariant())}{El("RotatingMachine.p", p)}{Ref("RegulatingCondEq.RegulatingControl", "rc_" + id)}{El("RegulatingCondEq.controlEnabled", "true")}{extra}</cim:SynchronousMachine>"""
        + T(id + "_t", id, node) + Rc("rc_" + id, regulatedTerminal, targetKv);

    private static string Rc(string id, string terminal, string targetKv) =>
        $"""<cim:RegulatingControl rdf:ID="_{id}">{EnumRef("RegulatingControl.mode", "RegulatingControlModeKind.voltage")}{Ref("RegulatingControl.Terminal", terminal)}{El("RegulatingControl.targetValue", targetKv)}{El("RegulatingControl.enabled", "true")}</cim:RegulatingControl>""";

    // ================================================================ N295: series compensator =============

    // Two 225 kV nodes: the slack G1 at n1 holding 225 kV, a series compensator n1 - n2, a load at n2.
    private static string SeriesDoc(string body) => Head + Bv("bv225", "225") + Tn("n1", "bv225") + Tn("n2", "bv225")
        + Machine("g1", "n1", "0", "g1_t", "225")
        + $"""<cim:SeriesCompensator rdf:ID="_sc">{Name("SC")}{body}</cim:SeriesCompensator>""" + T("sc_1", "sc", "n1", 1) + T("sc_2", "sc", "n2", 2)
        + Load("ld", "n2", "100", "20") + Tail;

    [Fact]
    public void N295_a_series_compensator_is_a_series_branch_of_its_r_and_x()
    {
        CimNetwork net = CimImporter.Parse(SeriesDoc(El("SeriesCompensator.r", "0.5") + El("SeriesCompensator.x", "-31.83099")));
        CimBranch sc = Assert.Single(net.Branches);
        Assert.Equal(new CimBranch("sc", "SC", "n1", "n2", 0.5, -31.83099, 0.0, 0.0, "SeriesCompensator"), sc);
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));   // it used to be listed and left out
        Branch b = CimGridModelBridge.ToGridModel(net).Branches.Single();
        double zb = 225.0 * 225.0 / 100.0;
        Assert.Equal((0.5 / zb, -31.83099 / zb, 0.0, 1.0), (b.R, b.X, b.B, b.TapRatio));
        Assert.True(new NewtonRaphsonPowerFlow().Solve(CimGridModelBridge.ToGridModel(net)).Converged);

        // Export round trip: written under its own class, read back as the same record.
        CimNetwork back = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(sc, back.Branches.Single());
    }

    [Fact]
    public void N295_a_series_compensator_without_x_is_refused_and_its_varistor_is_said()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(SeriesDoc(El("SeriesCompensator.r", "0.5"))));
        Assert.Contains("SeriesCompensator.x", ex.Message);
        CimNetwork net = CimImporter.Parse(SeriesDoc(El("SeriesCompensator.x", "-31.83099") + El("SeriesCompensator.varistorPresent", "true")));
        Assert.Equal(0.0, net.Branches.Single().R);   // r absent: a pure capacitor
        Assert.Contains(net.Notes, n => n.Contains("varistor") && n.Contains("not represented"));
    }

    // ================================================================ N295: reactive capability curve ======

    private static string CurveDoc(string style, string minQ = "0", string maxQ = "0", string p = "-90", string extraCurve = "", params (string X, string Y1, string Y2)[] points)
    {
        string pts = string.Concat(points.Select((pt, i) =>
            $"""<cim:CurveData rdf:ID="_cd{i}">{El("CurveData.xvalue", pt.X)}{El("CurveData.y1value", pt.Y1)}{El("CurveData.y2value", pt.Y2)}{Ref("CurveData.Curve", "rcc")}</cim:CurveData>"""));
        string styleEl = style.Length == 0 ? "" : EnumRef("Curve.curveStyle", "CurveStyle." + style);
        return Head + Bv("bv110", "110") + Tn("n1", "bv110")
            + Machine("g1", "n1", p, "g1_t", "110",
                El("SynchronousMachine.minQ", minQ) + El("SynchronousMachine.maxQ", maxQ) + Ref("SynchronousMachine.InitialReactiveCapabilityCurve", "rcc"))
            + $"""<cim:ReactiveCapabilityCurve rdf:ID="_rcc">{Name("G1 curve")}{styleEl}{extraCurve}</cim:ReactiveCapabilityCurve>""" + pts + Tail;
    }

    // The MicroGrid BE-G1 curve of CGMES 3.0: (-100: -200 / 200), (0: -300 / 300), (200: -200 / 200).
    private static readonly (string, string, string)[] BeCurve = { ("200", "-200", "200"), ("-100", "-200", "200"), ("0", "-300", "300") };

    [Fact]
    public void N295_a_straight_line_capability_curve_gives_the_limits_at_the_machine_p()
    {
        CimNetwork net = CimImporter.Parse(CurveDoc("straightLineYValues", points: BeCurve));
        CimGenerator g = net.Generators.Single();
        // P = 90 MW lies between the points at 0 and 200 MW: a + (p - xa) (b - a) / (xb - xa).
        Assert.Equal(-300.0 + (90.0 - 0.0) * (-200.0 - -300.0) / (200.0 - 0.0), g.MinQ);
        Assert.Equal(300.0 + (90.0 - 0.0) * (200.0 - 300.0) / (200.0 - 0.0), g.MaxQ);
        Assert.Equal((-255.0, 255.0), (g.MinQ!.Value, g.MaxQ!.Value));
        Assert.Contains(net.Notes, n => n.Contains("reactive capability curve") && n.Contains("-255 .. 255 MVAr")
                                        && n.Contains("instead of SynchronousMachine.minQ / maxQ (0 / 0)"));
        // Before N295 the machine kept minQ = maxQ = 0 (pinned at 0 MVAr once limits were enforced).
        Generator gen = CimGridModelBridge.ToGridModel(net).Generators.Single();
        Assert.Equal((-255.0, 255.0), (gen.Qmin, gen.Qmax));
    }

    // pLoadSign is RotatingMachine.p (CGMES load sign): "-90" is a machine generating P = 90 MW.
    [Theory]
    [InlineData("-90", -300.0, 300.0)]    // P = 90: in [0, 200), the step holds the 0 MW point's values
    [InlineData("0", -300.0, 300.0)]      // P = 0: exactly on a point, that point's values
    [InlineData("50", -200.0, 200.0)]     // P = -50 (motoring): in [-100, 0), the -100 MW point's values
    [InlineData("-250", -200.0, 200.0)]   // P = 250: beyond the last point, its values held
    [InlineData("150", -200.0, 200.0)]    // P = -150: before the first point, its values held
    public void N295_a_constant_y_capability_curve_is_a_step_and_its_ends_are_held(string pLoadSign, double min, double max)
    {
        CimGenerator g = CimImporter.Parse(CurveDoc("constantYValue", p: pLoadSign, points: BeCurve)).Generators.Single();
        Assert.Equal((min, max), (g.MinQ!.Value, g.MaxQ!.Value));
    }

    [Fact]
    public void N295_a_curve_with_no_style_is_a_straight_line_and_bad_curves_are_refused()
    {
        CimGenerator g = CimImporter.Parse(CurveDoc("", points: BeCurve)).Generators.Single();
        Assert.Equal(-255.0, g.MinQ);

        Assert.Contains("y1value", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(CurveDoc("straightLineYValues", points: new[] { ("0", "300", "-300") }))).Message);
        Assert.Contains("two points", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(CurveDoc("straightLineYValues", points: new[] { ("0", "-300", "300"), ("0", "-200", "200") }))).Message);
        Assert.Contains("no CurveData", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(CurveDoc("straightLineYValues"))).Message);
        Assert.Contains("MW and MVAr", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(CurveDoc("straightLineYValues", extraCurve: EnumRef("Curve.xMultiplier", "UnitMultiplier.k"), points: BeCurve))).Message);
        Assert.Contains("rampYValue", Assert.Throws<InvalidDataException>(() =>
            CimImporter.Parse(CurveDoc("rampYValue", points: BeCurve))).Message);
    }

    // ================================================================ N295: remote voltage control ==========

    // 110 kV: G1 at n1 (slack, local 110 kV), lines n1 - n2 - n3 (0.01 + j0.1 pu each), a load at n2, and G2 at n3
    // generating 30 MW and regulating n2 (the line L1's terminal there) at targetKv.
    private static string RemoteDoc(string targetKv, string g2Extra = "") => Head + Bv("bv110", "110")
        + Tn("n1", "bv110") + Tn("n2", "bv110") + Tn("n3", "bv110")
        + Machine("g1", "n1", "0", "g1_t", "110")
        + Line("l1", "n1", "n2", "1.21", "12.1") + Line("l2", "n2", "n3", "1.21", "12.1")
        + Load("ld", "n2", "60", "40")
        + Machine("g2", "n3", "-30", "l1_2", targetKv, g2Extra) + Tail;

    [Fact]
    public void N295_remote_voltage_control_is_read_and_a_plain_solve_still_holds_q()
    {
        CimNetwork net = CimImporter.Parse(RemoteDoc("112"));
        CimGenerator g2 = net.Generators.Single(g => g.MRID == "g2");
        Assert.Equal((false, (double?)null, "n2", (double?)112.0), (g2.VoltageControl!.Value, g2.TargetKv, g2.RegulatedNode, g2.RemoteTargetKv));
        Assert.Contains(net.Notes, n => n.Contains("G2") && n.Contains("remote voltage control") && n.Contains("CimControlledPowerFlow"));

        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net, notes: notes);
        Assert.Equal(BusType.Pq, bridged.Model.Buses[2].Type);   // the plain model is as before: G2 held at its q
        CimRemoteVoltageControl rc = Assert.Single(bridged.RemoteVoltageControls);
        Assert.Equal(("n2", 2, 112.0 / 110.0), (rc.RegulatedNode, rc.RegulatedBusId, rc.TargetPu));
        Assert.Equal(new[] { 3 }, rc.GeneratorBusIds);
        Assert.Equal(new[] { "G2" }, rc.Machines);
        Assert.Contains(notes, n => n.StartsWith("Remote voltage control: bus 2 'N2'"));
    }

    [Fact]
    public void N295_the_controlled_solve_holds_the_remote_bus_at_its_target()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(RemoteDoc("112")));
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(solver.ControlsSettled);
        Assert.True(Math.Abs(r.Vm[1] - 112.0 / 110.0) <= 1e-8, $"n2 at {r.Vm[1] * 110.0} kV");
        Assert.True(Math.Abs(plain.Vm[1] - 112.0 / 110.0) > 1e-3);   // a plain solve does not hold it
        CimControlState s = Assert.Single(solver.LastStates);
        Assert.False(s.AtLimit);
        Assert.True(Math.Abs(s.ErrorPu) <= 1e-8);
        Assert.Equal(r.Vm[2], s.Value, 12);   // the machine bus sits at the control's set-point

        // The dense and the sparse engines give the same bits through the loop.
        PowerFlowResult sparse = new CimControlledPowerFlow(new SparseNewtonRaphsonPowerFlow(), bridged).Solve(bridged.Model);
        Assert.Equal(r.Vm, sparse.Vm);
        Assert.Equal(r.VaDeg, sparse.VaDeg);
    }

    [Fact]
    public void N295_a_remote_control_at_its_reactive_limit_is_reported_at_the_limit()
    {
        // G2 can give at most 1 MVAr: it cannot lift n2 to 116 kV, so it ends at its limit with n2 below target.
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(RemoteDoc("116",
            El("SynchronousMachine.minQ", "-1") + El("SynchronousMachine.maxQ", "1"))));
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(solver.ControlsSettled);
        CimControlState s = Assert.Single(solver.LastStates);
        Assert.True(s.AtLimit);
        Assert.True(r.Vm[1] < 116.0 / 110.0);
    }

    [Fact]
    public void N295_a_remote_control_survives_an_export_round_trip()
    {
        CimNetwork net = CimImporter.Parse(RemoteDoc("112"));
        CimNetwork back = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Generators.Single(g => g.MRID == "g2"), back.Generators.Single(g => g.MRID == "g2"));
    }

    [Fact]
    public void N295_a_remote_control_of_a_bus_a_machine_already_holds_is_not_applied()
    {
        // G2 regulates n1, which G1 holds (the slack): one bus, one controller.
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(RemoteDoc("112").Replace("#_l1_2", "#_l1_1")), notes: notes);
        Assert.Empty(bridged.RemoteVoltageControls);
        Assert.Contains(notes, n => n.Contains("Remote voltage control of G2") && n.Contains("not applied"));
    }

    // ================================================================ N295: static var compensator =========

    // 225 kV: G1 at n1 (slack, 225 kV), a line n1 - n2 (0.01 + j0.1 pu), a load at n2 (100 MW, 30 MVAr), and an SVC at
    // n2 with the given body.
    private static string SvcDoc(string body, string rc = "") => Head + Bv("bv225", "225") + Tn("n1", "bv225") + Tn("n2", "bv225")
        + Machine("g1", "n1", "0", "g1_t", "225") + Line("l1", "n1", "n2", "5.0625", "50.625") + Load("ld", "n2", "100", "30")
        + $"""<cim:StaticVarCompensator rdf:ID="_svc">{Name("SVC")}{body}</cim:StaticVarCompensator>""" + T("svc_t", "svc", "n2") + rc + Tail;

    private static string SvcRegulating(string cap, string ind, string slope, string targetKv, string q = "0") => SvcDoc(
        El("StaticVarCompensator.capacitiveRating", cap) + El("StaticVarCompensator.inductiveRating", ind) + El("StaticVarCompensator.slope", slope)
        + El("StaticVarCompensator.q", q) + El("RegulatingCondEq.controlEnabled", "true") + Ref("RegulatingCondEq.RegulatingControl", "rcs"),
        Rc("rcs", "svc_t", targetKv));

    [Fact]
    public void N295_a_static_var_compensator_is_read_and_held_at_its_q_in_a_plain_solve()
    {
        CimNetwork net = CimImporter.Parse(SvcRegulating("5062.5", "-5062.5", "0.102", "229.5", q: "-5"));
        CimStaticVarCompensator svc = Assert.Single(net.Svcs);
        Assert.Equal(new CimStaticVarCompensator("svc", "SVC", "n2", 5062.5, -5062.5, 0.102, -5.0, true, 229.5, "n2"), svc);
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));

        CimBridgeResult bridged = CimGridModelBridge.Bridge(net);
        Assert.Equal(30.0 + -5.0, bridged.Model.Buses[1].Qd);   // its SSH q, load sign, on the bus
        CimSusceptanceControl sc = Assert.Single(bridged.SusceptanceControls);
        Assert.Equal(1.0 / Math.Abs(5062.5) * (225.0 * 225.0), sc.BMaxMvar);
        Assert.Equal(0.0 - 1.0 / Math.Abs(-5062.5) * (225.0 * 225.0), sc.BMinMvar);
        Assert.Equal((229.5 / 225.0, 0.102 * 100.0 / 225.0, -5.0, 5.0), (sc.TargetPu, sc.SlopePu, sc.HeldQdMvar, sc.InitialBMvar));

        CimNetwork back = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(svc, back.Svcs.Single());
    }

    [Fact]
    public void N295_an_svc_at_its_capacitive_limit_is_a_fixed_susceptance()
    {
        // 240 kV is out of reach of +10 MVAr: the SVC ends at its full capacitive susceptance (its output then B V^2).
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(SvcRegulating("5062.5", "-5062.5", "0", "240")));
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(solver.ControlsSettled);
        CimControlState s = Assert.Single(solver.LastStates);
        Assert.True(s.AtLimit);
        Assert.Equal(1.0 / Math.Abs(5062.5) * (225.0 * 225.0), s.Value);
        Assert.True(r.Vm[1] < 240.0 / 225.0);
    }

    [Theory]
    [InlineData("0")]       // a PV-like control: the voltage at its target
    [InlineData("0.1")]     // with droop: V = target - slope x Q
    public void N295_an_svc_in_its_range_meets_its_control_law(string slope)
    {
        // 506.25 ohm ratings: +/- 100 MVAr at 1.0 pu, enough to reach the target either way.
        double target = slope == "0" ? 225.0 : 230.0;
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(
            SvcRegulating("506.25", "-506.25", slope, target.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        CimControlState s = Assert.Single(solver.LastStates);
        Assert.False(s.AtLimit);
        double slopePu = double.Parse(slope, System.Globalization.CultureInfo.InvariantCulture) * 100.0 / 225.0;
        double v = r.Vm[1];
        double law = v + slopePu * (s.Value * v * v / 100.0) - target / 225.0;
        Assert.True(Math.Abs(law) <= 1e-8, $"control law off by {law:E3} pu");
        if (slope == "0") Assert.True(Math.Abs(v - 1.0) <= 1e-8);
    }

    [Fact]
    public void N295_a_cgmes_2_4_15_svc_regulates_by_its_control_mode_and_set_point()
    {
        CimStaticVarCompensator svc = CimImporter.Parse(SvcDoc(El("StaticVarCompensator.capacitiveRating", "506.25")
            + EnumRef("StaticVarCompensator.sVCControlMode", "SVCControlMode.voltage") + El("StaticVarCompensator.voltageSetPoint", "230"))).Svcs.Single();
        Assert.Equal((true, (double?)230.0, "n2"), (svc.Regulating, svc.TargetKv, svc.RegulatedNode));

        CimNetwork held = CimImporter.Parse(SvcDoc(El("StaticVarCompensator.capacitiveRating", "506.25") + El("StaticVarCompensator.q", "-7")
            + EnumRef("StaticVarCompensator.sVCControlMode", "SVCControlMode.reactivePower")));
        Assert.False(held.Svcs.Single().Regulating);
        Assert.Contains(held.Notes, n => n.Contains("sVCControlMode reactivePower") && n.Contains("held at its q = -7 MVAr"));
        Assert.Empty(CimGridModelBridge.Bridge(held).SusceptanceControls);
    }

    [Theory]
    [InlineData("<cim:StaticVarCompensator.slope>-0.1</cim:StaticVarCompensator.slope><cim:StaticVarCompensator.capacitiveRating>500</cim:StaticVarCompensator.capacitiveRating>", "negative")]
    [InlineData("<cim:StaticVarCompensator.capacitiveRating>0</cim:StaticVarCompensator.capacitiveRating>", "0 ohm")]
    [InlineData("<cim:StaticVarCompensator.sVCControlMode rdf:resource=\"http://iec.ch/TC57/2013/CIM-schema-cim16#SVCControlMode.voltage\"/><cim:StaticVarCompensator.capacitiveRating>500</cim:StaticVarCompensator.capacitiveRating>", "voltageSetPoint")]
    [InlineData("<cim:StaticVarCompensator.sVCControlMode rdf:resource=\"http://iec.ch/TC57/2013/CIM-schema-cim16#SVCControlMode.voltage\"/><cim:StaticVarCompensator.voltageSetPoint>230</cim:StaticVarCompensator.voltageSetPoint>", "neither capacitiveRating nor inductiveRating")]
    public void N295_contradictory_or_incomplete_svc_data_is_refused(string body, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CimImporter.Parse(SvcDoc(body)));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("svc", ex.Message);
    }

    // ================================================================ N295: CGMES 3.0 boundary ============

    // A 380 kV node n1 (slack), a boundary ConnectivityNode bx with no TopologicalNode (a CGMES 3.0 EQ_BD), a 380 kV line
    // from n1 to bx and a 400 kV boundary EquivalentInjection on bx giving 50 MW.
    private static string BoundaryDoc(bool boundaryPoint) => Head + Bv("bv380", "380") + Bv("bv400", "400") + Tn("n1", "bv380")
        + Machine("g1", "n1", "0", "g1_t", "380")
        + $"""<cim:ConnectivityNode rdf:ID="_bx">{Name("Border_X")}</cim:ConnectivityNode>"""
        + (boundaryPoint ? $"""<cim:BoundaryPoint rdf:ID="_bp">{Name("Border_X")}{Ref("BoundaryPoint.ConnectivityNode", "bx")}</cim:BoundaryPoint>""" : "")
        + $"""<cim:ACLineSegment rdf:ID="_l1">{Name("L1")}{El("ACLineSegment.r", "1.444")}{El("ACLineSegment.x", "14.44")}{Ref("ConductingEquipment.BaseVoltage", "bv380")}</cim:ACLineSegment>"""
        + T("l1_1", "l1", "n1", 1)
        + $"""<cim:Terminal rdf:ID="_l1_2">{Ref("Terminal.ConductingEquipment", "l1")}{Ref("Terminal.ConnectivityNode", "bx")}{El("ACDCTerminal.sequenceNumber", "2")}</cim:Terminal>"""
        + $"""<cim:EquivalentInjection rdf:ID="_ei">{Name("EI")}{El("EquivalentInjection.p", "-50")}{El("EquivalentInjection.q", "0")}{Ref("ConductingEquipment.BaseVoltage", "bv400")}</cim:EquivalentInjection>"""
        + $"""<cim:Terminal rdf:ID="_ei_t">{Ref("Terminal.ConductingEquipment", "ei")}{Ref("Terminal.ConnectivityNode", "bx")}</cim:Terminal>"""
        + Tail;

    [Fact]
    public void N295_a_cgmes_3_boundary_node_without_a_topological_node_is_a_bus_of_its_own()
    {
        CimNetwork net = CimImporter.Parse(BoundaryDoc(boundaryPoint: true));
        Assert.Equal(new[] { "n1", "bx" }, net.Buses.Select(b => b.MRID).ToArray());
        // Its base: the smallest-mRID equipment on it that declares one ("ei" before "l1"): the injection's 400 kV.
        Assert.Equal(new CimBus("bx", "Border_X", 400.0), net.Buses[1]);
        Assert.Contains(net.Notes, n => n.StartsWith("CGMES 3.0 boundary: 1 boundary ConnectivityNode(s)"));
        Assert.Contains(net.Notes, n => n.StartsWith("CGMES 3.0 boundary bus 'Border_X' (bx): base voltage 400 kV")
                                        && n.Contains("ACLineSegment 'L1' (mRID l1) 380 kV"));
        Assert.Equal(("n1", "bx"), (net.Branches.Single().FromNode, net.Branches.Single().ToNode));
        Assert.Equal("bx", net.Generators.Single(g => g.MRID == "ei").Node);

        // The 380 kV line to the 400 kV-based boundary bus carries the ratio as a tap (N294), and the model solves.
        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal(400.0 / 380.0, m.Branches.Single().TapRatio);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    // ================================================================ N296: regulating shunts =============

    // 132 kV: G1 at n1 (slack, 132 kV), a line n1 - n2 (0.01 + j0.1 pu), a load at n2 (80 MW, 40 MVAr), and a linear shunt
    // at `at` of 0.0005 S per section (8.712 MVAr at 1.0 pu), 2 of 20 sections in service, regulating its own bus at
    // 132 kV with a 2 kV deadband (band 131 .. 133 kV).
    private static string ShuntDoc(string at = "n2", string discrete = "", string deadband = "2") => Head + Bv("bv132", "132")
        + Tn("n1", "bv132") + Tn("n2", "bv132") + Machine("g1", "n1", "0", "g1_t", "132")
        + Line("l1", "n1", "n2", "1.7424", "17.424") + Load("ld", "n2", "80", "40")
        + $"""<cim:LinearShuntCompensator rdf:ID="_c1">{Name("C1")}{El("ShuntCompensator.sections", "2")}{El("ShuntCompensator.maximumSections", "20")}{El("LinearShuntCompensator.bPerSection", "0.0005")}{El("RegulatingCondEq.controlEnabled", "true")}{Ref("RegulatingCondEq.RegulatingControl", "rcc")}</cim:LinearShuntCompensator>"""
        + T("c1_t", "c1", at)
        + $"""<cim:RegulatingControl rdf:ID="_rcc">{EnumRef("RegulatingControl.mode", "RegulatingControlModeKind.voltage")}{Ref("RegulatingControl.Terminal", "c1_t")}{El("RegulatingControl.targetValue", "132")}{(deadband.Length > 0 ? El("RegulatingControl.targetDeadband", deadband) : "")}{(discrete.Length > 0 ? El("RegulatingControl.discrete", discrete) : "")}</cim:RegulatingControl>"""
        + Tail;

    [Fact]
    public void N296_a_regulating_linear_shunt_keeps_its_control()
    {
        CimNetwork net = CimImporter.Parse(ShuntDoc());
        CimShunt sh = Assert.Single(net.Shunts);
        Assert.Equal(0.0005 * 2.0, sh.B);   // a plain solve still holds it at its sections
        Assert.Equal(new CimShuntControl(132.0, 2.0, "n2", true, 2.0, 20.0, 0.0005, 0.0), sh.Control);
        Assert.Contains(net.Notes, n => n.Contains("is under voltage control") && n.Contains("held at 2 section(s)") && n.Contains("N296"));
    }

    [Fact]
    public void N296_the_bridge_offers_the_shunt_in_a_discrete_and_a_continuous_form()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc()));
        CimShuntRegulation reg = Assert.Single(bridged.ShuntRegulations);
        double step = 0.0005 * (132.0 * 132.0);
        Assert.Equal((2, 2, true, 2.0, step), (reg.BusId, reg.RegulatedBusId, reg.DataSaysDiscrete, reg.SectionsInModel, reg.StepMvar));
        Assert.Equal(new SwitchedShunt(2, step, MinSteps: -2, MaxSteps: 18, InitialSteps: 0,
            VminPu: (132.0 - 2.0 / 2.0) / 132.0, VmaxPu: (132.0 + 2.0 / 2.0) / 132.0, Name: "C1"), reg.Discrete);
        CimSusceptanceControl c = reg.Continuous!;
        Assert.Equal(((0.0 - 2.0) * step, (20.0 - 2.0) * step, 0.0, 132.0 / 132.0, 0.0), (c.BMinMvar, c.BMaxMvar, c.InitialBMvar, c.TargetPu, c.SlopePu));
        // The plain model is unchanged: the shunt at its 2 sections in Bs.
        Assert.Equal(0.0 + 0.0005 * 2.0 * (132.0 * 132.0), bridged.Model.Buses[1].Bs);
    }

    [Fact]
    public void N296_discrete_sections_switch_until_the_voltage_is_inside_the_band()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc()));
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        Assert.True(plain.Vm[1] < 131.0 / 132.0);   // below the band at 2 sections
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        Assert.Equal(new[] { "C1: switched in whole sections." }, solver.ShuntChoices);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(solver.ControlsSettled);
        int steps = Assert.Single(solver.LastShuntSteps);
        Assert.True(steps > 0);
        Assert.InRange(r.Vm[1], 131.0 / 132.0, 133.0 / 132.0);
    }

    [Fact]
    public void N296_continuous_regulation_holds_the_target_exactly()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc()));
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged, new CimControlOptions(Shunts: CimShuntControlMode.Continuous));
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote);
        Assert.True(Math.Abs(r.Vm[1] - 1.0) <= 1e-8);
        CimControlState s = Assert.Single(solver.LastStates);
        Assert.False(s.AtLimit);
        Assert.True(s.Value > 0.0);   // more capacitive than the 2 sections held

        // The data's own flag: discrete = false asks for the continuous form.
        CimBridgeResult asked = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc(discrete: "false")));
        Assert.Equal(new[] { "C1: a continuous susceptance." }, new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), asked).ShuntChoices);
    }

    [Fact]
    public void N296_held_shunts_solve_exactly_as_a_plain_solve()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc()));
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged, new CimControlOptions(Shunts: CimShuntControlMode.Held));
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.Equal(plain.Vm, r.Vm);
        Assert.Equal(plain.VaDeg, r.VaDeg);
        Assert.Equal(1, solver.LastSolves);
    }

    [Fact]
    public void N296_a_shunt_without_a_deadband_has_no_discrete_form_and_falls_back_to_continuous_as_written()
    {
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc(deadband: "")));
        CimShuntRegulation reg = Assert.Single(bridged.ShuntRegulations);
        Assert.Null(reg.Discrete);
        Assert.Contains("no deadband", reg.Basis);
        string choice = Assert.Single(new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged).ShuntChoices);
        Assert.StartsWith("C1: a continuous susceptance, because it has no discrete form", choice);
        string held = Assert.Single(new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged,
            new CimControlOptions(Shunts: CimShuntControlMode.Discrete)).ShuntChoices);
        Assert.StartsWith("C1: held at its sections, because it has no discrete form", held);
    }

    [Fact]
    public void N296_a_shunt_on_a_bus_a_machine_holds_is_not_regulated()
    {
        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(CimImporter.Parse(ShuntDoc(at: "n1")), notes: notes);
        Assert.Empty(bridged.ShuntRegulations);
        Assert.Contains(notes, n => n.StartsWith("Shunt C1: bus 'N1' is already held"));
    }

    // ================================================================ N296: a slack per energised island ===

    // Two islands: n1 - n2 (G1, 100 MW unit, priority p1; a load) and n3 - n4 (G3 and G4, units of maxP3 / maxP4 MW,
    // priorities p3 / p4; a load at n4).
    private static CimNetwork TwoIslands(double maxP3, double maxP4, int p3 = 0, int p4 = 0, int p1 = 0) => new(
        new[] { new CimBus("n1", "N1", 132), new CimBus("n2", "N2", 132), new CimBus("n3", "N3", 132), new CimBus("n4", "N4", 132) },
        new[] { new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0), new CimBranch("l2", "L2", "n3", "n4", 1.7424, 17.424, 0.0) },
        new[] { new CimLoad("d2", "D2", "n2", 50, 10), new CimLoad("d4", "D4", "n4", 40, 10) },
        new[]
        {
            new CimGenerator("g1", "G1", "n1", 50, MaxP: 100, ReferencePriority: p1),
            new CimGenerator("g3", "G3", "n3", 20, MaxP: maxP3, ReferencePriority: p3),
            new CimGenerator("g4", "G4", "n4", 20, MaxP: maxP4, ReferencePriority: p4),
        },
        Array.Empty<CimTransformer>());

    [Theory]
    [InlineData(50.0, 80.0, 0, 0, 0, 4)]   // the larger unit
    [InlineData(80.0, 80.0, 0, 0, 0, 3)]   // a tie on size: the lower bus number
    [InlineData(50.0, 80.0, 2, 3, 1, 3)]   // a higher referencePriority (2 before 3) beats size; G1's 1 keeps the main slack
    public void N296_an_energised_second_island_gets_its_own_slack_by_the_main_rule(
        double maxP3, double maxP4, int p3, int p4, int p1, int islandSlackBus)
    {
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(TwoIslands(maxP3, maxP4, p3, p4, p1), notes: notes);
        Assert.Equal(BusType.Slack, m.Buses[0].Type);                       // the main island's slack, as before
        Assert.Equal(BusType.Slack, m.Buses[islandSlackBus - 1].Type);      // the island's own
        int other = islandSlackBus == 4 ? 3 : 4;
        Assert.Equal(BusType.Pv, m.Buses[other - 1].Type);
        string machine = islandSlackBus == 4 ? "G4" : "G3";
        Assert.Contains(notes, n => n.StartsWith("An island with its own voltage-controlling machine(s) (bus 'N3'")
                                    && n.Contains($"it gets its own slack, machine {machine} at bus 'N{islandSlackBus}'"));
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(0.0, r.VaDeg[islandSlackBus - 1]);   // the island's angles are relative to its own slack
    }

    [Fact]
    public void N296_a_single_island_keeps_one_slack()
    {
        CimNetwork one = TwoIslands(50, 80) with
        {
            Branches = new[]
            {
                new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0), new CimBranch("l2", "L2", "n3", "n4", 1.7424, 17.424, 0.0),
                new CimBranch("l3", "L3", "n2", "n3", 1.7424, 17.424, 0.0),
            },
        };
        GridModel m = CimGridModelBridge.ToGridModel(one);
        Assert.Equal(new[] { BusType.Slack, BusType.Pq, BusType.Pv, BusType.Pv }, m.Buses.Select(b => b.Type).ToArray());
    }

    [Fact]
    public void N295_a_connectivity_node_that_is_not_a_boundary_point_is_still_on_no_bus()
    {
        CimNetwork net = CimImporter.Parse(BoundaryDoc(boundaryPoint: false));
        Assert.Equal(new[] { "n1" }, net.Buses.Select(b => b.MRID).ToArray());
        Assert.Empty(net.Branches);   // the line has one connected terminal, as before
        Assert.Contains(net.Notes, n => n.Contains("name no TopologicalNode") && n.Contains("bx"));
    }
}
