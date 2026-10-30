using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GridSim.Core.IO.Cim;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CIM2 stream (October 2026): the ENTSO-E CGMES conformity benchmark against the PUBLISHED solved voltages (SvVoltage),
/// and the two transformer mappings the benchmark showed were missing (the rated-voltage ratio when a winding's rating
/// differs from its bus base voltage, and the magnetising susceptance). The inline tests always run; the benchmark runs
/// only when GRIDSIM_CGMES_CONFORMITY_DIR names a copy of the conformity models (cas-1.1.3-data-4.0.3, as powsybl-core
/// keeps them; not bundled, licence not verified, N288), and otherwise is reported as skipped (GR-410: it used to
/// return early and be reported as passed).
/// </summary>
public class CimConformityBenchmarkTests
{
    private const string Head = """<rdf:RDF xmlns:cim="http://iec.ch/TC57/2013/CIM-schema-cim16#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">""";
    private const string Tail = "</rdf:RDF>";

    private static string Bv(string id, string kv) =>
        $"""<cim:BaseVoltage rdf:ID="_{id}"><cim:BaseVoltage.nominalVoltage>{kv}</cim:BaseVoltage.nominalVoltage></cim:BaseVoltage>""";

    private static string Tn(string id, string bv) =>
        $"""<cim:TopologicalNode rdf:ID="_{id}"><cim:IdentifiedObject.name>{id.ToUpperInvariant()}</cim:IdentifiedObject.name><cim:TopologicalNode.BaseVoltage rdf:resource="#_{bv}"/></cim:TopologicalNode>""";

    private static string T(string id, string equip, string node) =>
        $"""<cim:Terminal rdf:ID="_{id}"><cim:Terminal.ConductingEquipment rdf:resource="#_{equip}"/><cim:Terminal.TopologicalNode rdf:resource="#_{node}"/></cim:Terminal>""";

    private static string End(string id, string tx, int number, string node, string x, string ratedU, string extra = "") =>
        $"""<cim:PowerTransformerEnd rdf:ID="_{id}"><cim:PowerTransformerEnd.PowerTransformer rdf:resource="#_{tx}"/><cim:TransformerEnd.endNumber>{number}</cim:TransformerEnd.endNumber><cim:TransformerEnd.Terminal rdf:resource="#_{id}_t"/><cim:PowerTransformerEnd.r>0</cim:PowerTransformerEnd.r><cim:PowerTransformerEnd.x>{x}</cim:PowerTransformerEnd.x><cim:PowerTransformerEnd.ratedU>{ratedU}</cim:PowerTransformerEnd.ratedU>{extra}</cim:PowerTransformerEnd>"""
        + T(id + "_t", tx, node);

    // A slack machine at node "hv" holding 110 kV (1.0 pu on a 110 kV base).
    private const string Slack =
        """<cim:SynchronousMachine rdf:ID="_g"><cim:IdentifiedObject.name>G</cim:IdentifiedObject.name><cim:RotatingMachine.p>0</cim:RotatingMachine.p><cim:RegulatingCondEq.RegulatingControl rdf:resource="#_rc"/></cim:SynchronousMachine>"""
        + """<cim:Terminal rdf:ID="_g_t"><cim:Terminal.ConductingEquipment rdf:resource="#_g"/><cim:Terminal.TopologicalNode rdf:resource="#_hv"/></cim:Terminal>"""
        + """<cim:RegulatingControl rdf:ID="_rc"><cim:RegulatingControl.mode rdf:resource="http://iec.ch/TC57/2013/CIM-schema-cim16#RegulatingControlModeKind.voltage"/><cim:RegulatingControl.Terminal rdf:resource="#_g_t"/><cim:RegulatingControl.targetValue>110</cim:RegulatingControl.targetValue></cim:RegulatingControl>""";

    // A 120 / 10.5 kV unit (17.28 ohm on its 120 kV side: x = 0.12 pu on its rating) between a 110 kV and a 10 kV bus.
    private static string OffNominal(string endExtra = "") => Head + Bv("bv110", "110") + Bv("bv10", "10") + Tn("hv", "bv110") + Tn("lv", "bv10")
        + """<cim:PowerTransformer rdf:ID="_t2"><cim:IdentifiedObject.name>T2</cim:IdentifiedObject.name></cim:PowerTransformer>"""
        + End("e1", "t2", 1, "hv", "17.28", "120", endExtra) + End("e2", "t2", 2, "lv", "0", "10.5") + Slack + Tail;

    [Fact]
    public void A_transformer_rated_off_its_bus_voltages_gets_the_off_nominal_ratio_and_referred_impedance()
    {
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(OffNominal()), notes: notes);
        Branch t2 = m.Branches.Single();
        double n1 = 120.0 / 110.0, n2 = 10.5 / 10.0;
        Assert.Equal(1.0 * (n1 / n2), t2.TapRatio);
        double x = 17.28 / (120.0 * 120.0 / 100.0) + 0.0 / (10.5 * 10.5 / 100.0);
        Assert.Equal(x * (n2 * n2), t2.X);
        Assert.Contains(notes, n => n.Contains("Transformer T2: its rated voltages (120 / 10.5 kV) differ from its buses' base voltages (110 / 10 kV)"));
    }

    [Fact]
    public void At_no_load_the_off_nominal_unit_gives_its_rated_ratio_in_kilovolts()
    {
        // Physics: 110 kV on a 120 / 10.5 kV unit at no load is 110 x 10.5 / 120 = 9.625 kV, which is 0.9625 pu of the
        // 10 kV bus. Before CIM2 the bridge ignored the ratings and gave 1.0 pu (10 kV).
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(CimGridModelBridge.ToGridModel(CimImporter.Parse(OffNominal())));
        Assert.True(r.Converged);
        Assert.Equal(110.0 * 10.5 / 120.0 / 10.0, r.Vm[1], 7);   // to the solve tolerance (1e-8 pu mismatch)
    }

    [Fact]
    public void Magnetising_susceptance_is_the_branch_susceptance_and_conductance_is_listed()
    {
        string doc = OffNominal("<cim:PowerTransformerEnd.b>-0.0002</cim:PowerTransformerEnd.b><cim:PowerTransformerEnd.g>0.00001</cim:PowerTransformerEnd.g>");
        CimNetwork net = CimImporter.Parse(doc);
        Assert.Equal((-0.0002, 0.00001), (net.Transformers[0].Ends[0].B, net.Transformers[0].Ends[0].G));
        var notes = new List<string>();
        Branch t2 = CimGridModelBridge.ToGridModel(net, notes: notes).Branches.Single();
        double n2 = 10.5 / 10.0;
        Assert.Equal((-0.0002 * 120.0 * 120.0 + 0.0 * 10.5 * 10.5) / 100.0 / (n2 * n2), t2.B);
        // Moved 2026-10 (LF3 N307, Ruling R1: Mark delegated the EE calls 2026-10-04; ruled by main): the magnetising
        // conductance is now represented, half at each end like b (PSS/E and PowerFactory carry the iron loss). Before:
        // Assert.Contains(notes, n => n.Contains("Transformer T2: its magnetising conductance") && n.Contains("not represented"));
        // After: the note says it is represented, and the end buses carry it as Gs (checked in Lf3FollowUpsTests.N307).
        Assert.Contains(notes, n => n.Contains("Transformer T2: its magnetising conductance") && n.Contains("is represented"));

        // The ends' g and b survive an export round trip exactly.
        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));
        Assert.Equal(net.Transformers[0].Ends, rt.Transformers[0].Ends);
    }

    [Fact]
    public void A_transformer_rated_at_its_bus_voltages_is_bridged_exactly_as_before()
    {
        // 110 / 10 kV ratings on 110 / 10 kV buses, no b: tap exactly 1.0, B exactly 0.0, X the old expression, no note.
        string doc = OffNominal().Replace("<cim:PowerTransformerEnd.ratedU>120</cim:PowerTransformerEnd.ratedU>", "<cim:PowerTransformerEnd.ratedU>110</cim:PowerTransformerEnd.ratedU>")
                                 .Replace("<cim:PowerTransformerEnd.ratedU>10.5</cim:PowerTransformerEnd.ratedU>", "<cim:PowerTransformerEnd.ratedU>10</cim:PowerTransformerEnd.ratedU>");
        var notes = new List<string>();
        Branch t2 = CimGridModelBridge.ToGridModel(CimImporter.Parse(doc), notes: notes).Branches.Single();
        Assert.Equal((1.0, 0.0), (t2.TapRatio, t2.B));
        Assert.Equal(17.28 / (110.0 * 110.0 / 100.0) + 0.0 / (10.0 * 10.0 / 100.0), t2.X);
        Assert.DoesNotContain(notes, n => n.Contains("rated voltages"));
    }

    [Fact]
    public void A_three_winding_unit_rated_off_its_bus_voltages_taps_each_winding_and_starts_its_star_point()
    {
        // 400 / 120 / 30 kV windings on 380 / 110 / 30 kV buses (the ENTSO-E MiniGrid's T3 / T4 pattern).
        string doc = Head + Bv("b380", "380") + Bv("b110", "110") + Bv("b30", "30") + Tn("hv", "b380") + Tn("mv", "b110") + Tn("lv", "b30")
            + """<cim:PowerTransformer rdf:ID="_t3"><cim:IdentifiedObject.name>T3</cim:IdentifiedObject.name></cim:PowerTransformer>"""
            + End("w1", "t3", 1, "hv", "96", "400") + End("w2", "t3", 2, "mv", "1.44", "120") + End("w3", "t3", 3, "lv", "1.26", "30")
            + Slack.Replace(">110<", ">380<") + Tail;
        var notes = new List<string>();
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(doc), notes: notes);
        Assert.Equal(new[] { 1.0 * (400.0 / 380.0), 1.0 * (120.0 / 110.0), 1.0 }, m.Branches.Select(b => b.TapRatio).ToArray());
        Assert.Equal(96.0 / (400.0 * 400.0 / 100.0), m.Branches[0].X);   // on the star (winding 1 rating) base, unchanged
        Bus star = m.Buses.Single(b => b.Name == "T3 star point");
        Assert.Equal((400.0, 1.0 / (1.0 * (400.0 / 380.0))), (star.BaseKv, star.Vm));
        Assert.Contains(notes, n => n.Contains("Transformer T3: the rated voltages of its windings (400 / 120 / 30 kV)"));
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m).Converged);
    }

    // ================================================================ benchmark: published SV voltages ====

    /// <summary>
    /// The ENTSO-E CGMES 2.4.15 conformity models solved by GridSim against their own published SvVoltage. Expected (from
    /// the independent Python implementation of the same mapping run on the same files; CIM2 notes): MiniGrid
    /// (node-breaker and bus-branch) worst |dV| 7.0e-6 pu and |dAngle| 9.4e-5 deg, its two boundary equivalent nodes
    /// (XQ1_EQIN, XQ2_EQIN) de-energised (SV v = 0) and dropped; SmallGrid worst |dV| 1.45e-4 pu and |dAngle| 7.2e-3 deg,
    /// the residual being its 14 voltage-regulating shunt compensators, which the tool that wrote the SV regulated
    /// continuously to their targets while GridSim holds them at their sections (with that regulation emulated the
    /// residual falls to 3.1e-5 pu). The tolerances below are those expectations with margin. The mapping is also
    /// checked by count: injections by class, loads, shunts and the total shunt susceptance at 1.0 pu.
    /// </summary>
    // GR-410: skipped (not passed) without the models; was an early return reported as a pass.
    [EnvironmentTheory("the CGMES conformity models are not bundled (N288)", "GRIDSIM_CGMES_CONFORMITY_DIR")]
    [InlineData("MiniGrid/NodeBreaker/CGMES_v2.4.15_MiniGridTestConfiguration_BaseCase_Complete_v3",
                "MiniGrid/NodeBreaker/CGMES_v2.4.15_MiniGridTestConfiguration_Boundary_v3",
                2e-5, 5e-4, 11, "XQ1_EQIN,XQ2_EQIN", "EquivalentInjection:2,ExternalNetworkInjection:2,SynchronousMachine:3", "AsynchronousMachine:3", 0, 0.0)]
    [InlineData("MiniGrid/BusBranch/CGMES_v2.4.15_MiniGridTestConfiguration_BaseCase_v3",
                "MiniGrid/NodeBreaker/CGMES_v2.4.15_MiniGridTestConfiguration_Boundary_v3",
                2e-5, 5e-4, 11, "XQ1_EQIN,XQ2_EQIN", "EquivalentInjection:2,ExternalNetworkInjection:2,SynchronousMachine:3", "AsynchronousMachine:3", 0, 0.0)]
    [InlineData("SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_BaseCase_Complete_v3.0.0",
                "SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_Boundary_v3.0.0",
                2.5e-4, 1.5e-2, 118, "", "EquivalentInjection:3,SynchronousMachine:19", "EnergyConsumer:103", 14, 218.009088)]
    public void Conformity_models_solve_to_their_published_sv_voltages(
        string model, string boundary, double vTolPu, double aTolDeg, int compared, string dead,
        string generatorsByClass, string loadsByClass, int shunts, double shuntBsMvar)
    {
        string root = Environment.GetEnvironmentVariable("GRIDSIM_CGMES_CONFORMITY_DIR")!;   // GR-410: set, or the attribute skipped this test
        string[] files = new[] { model, boundary }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.xml"))
            .Where(f => !f.Contains("_DL_", StringComparison.Ordinal) && !f.Contains("_GL_", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);
        string[] docs = files.Select(File.ReadAllText).ToArray();

        // ---- the mapping, by count ----------------------------------------------------------------------
        CimNetwork net = CimImporter.Parse(docs);
        Assert.Equal(generatorsByClass, string.Join(",", net.Generators.GroupBy(g => g.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")));
        Assert.Equal(loadsByClass, string.Join(",", net.Loads.GroupBy(l => l.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")));
        Assert.Equal(shunts, net.Shunts.Count);
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));   // every class with a terminal is now read

        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net, notes: notes);
        Assert.Equal(shuntBsMvar, bridged.Model.Buses.Sum(b => b.Bs), 6);
        string[] droppedNames = bridged.DroppedNodes.Select(id => net.Buses.Single(b => b.MRID == id).Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(dead, string.Join(",", droppedNames));

        // ---- the solve against the published state --------------------------------------------------------
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(bridged.Model);
        Assert.True(r.Converged, r.Status.ToString());
        Dictionary<string, (double V, double Angle)> sv = PublishedVoltages(docs);

        // The published state agrees the dropped buses are dead (v = 0).
        foreach (string id in bridged.DroppedNodes)
            Assert.Equal(0.0, sv[id].V);

        // Angles are compared relative to the slack bus (GridSim holds it at 0 degrees).
        int slackId = bridged.Model.Buses.Single(b => b.Type == BusType.Slack).Id;
        string slackNode = net.Buses.First(b => bridged.BusIdOfNode.TryGetValue(b.MRID, out int id) && id == slackId && sv.ContainsKey(b.MRID)).MRID;
        double svSlackAngle = sv[slackNode].Angle;

        double worstV = 0.0, worstA = 0.0;
        int n = 0;
        foreach (CimBus bus in net.Buses)
        {
            if (!bridged.BusIdOfNode.TryGetValue(bus.MRID, out int id) || !sv.TryGetValue(bus.MRID, out (double V, double Angle) published)) continue;
            Bus b = bridged.Model.Buses[id - 1];
            double dv = (r.Vm[id - 1] * b.BaseKv - published.V) / b.BaseKv;
            double da = r.VaDeg[id - 1] - (published.Angle - svSlackAngle);
            worstV = Math.Max(worstV, Math.Abs(dv));
            worstA = Math.Max(worstA, Math.Abs(da));
            n++;
        }
        Assert.Equal(compared, n);
        Assert.True(worstV <= vTolPu, $"worst |dV| {worstV:E3} pu above {vTolPu:E1}");
        Assert.True(worstA <= aTolDeg, $"worst |dAngle| {worstA:E3} deg above {aTolDeg:E1}");
    }

    // ================================================================ CIM3 (2026-10): MicroGrid BE =========
    //
    // N294 / N295 / N296 and the line conductance: the ENTSO-E MicroGrid BE base case, CGMES 2.4.15 (cas-1.1.3-data-4.0.3,
    // with its boundary set) and CGMES 3.0 (cas-3-data-3.0.2: BE IGM with the 3.0 EQ_BD, no TP_BD), imported with the SV
    // tap positions (CimTapPositions.StateVariables: the 2.4.15 SV has BE-TR2_1 at step 16 and BE-TR2_3 at 18, the SSH at 10
    // and 14), bridged, and solved by CimControlledPowerFlow (BE-G1 regulates BE-Busbar_6 remotely at 115.5 kV; the 3.0
    // SVC regulates BE-Busbar_2). Expected from an independent Python twin (an exact
    // Newton formulation of the same mapping): 2.4.15 worst |dV| 1.633e-4 pu and |dAngle| 3.13e-2 deg, both at
    // BE-Busbar_4 (BE-G1's terminal bus; the published SV is not consistent with its own EQ at BE-TR2_3 by 1.43 MW, no
    // tap fits it), every other bus within 2.9e-5 pu and 3.0e-3 deg; 3.0 worst 1.50e-3 pu and 0.130 deg at BE-Busbar_4,
    // every other bus within 1.13e-4 pu and 5.7e-3 deg. Tolerances below are those with a margin. The 3.0 data is found
    // through GRIDSIM_CGMES3_CONFORMITY_DIR (naming cas-3-data-3.0.2), else next to the 2.4.15 directory.

    // GR-410: skipped (not passed) when neither variable is set.
    [EnvironmentTheory("the CGMES conformity models are not bundled (N288)", "GRIDSIM_CGMES_CONFORMITY_DIR", "GRIDSIM_CGMES3_CONFORMITY_DIR")]
    [InlineData(false, "MicroGrid/BaseCase/CGMES_v2.4.15_MicroGridTestConfiguration_BC_BE_v2",
                "MicroGrid/BaseCase/CGMES_v2.4.15_MicroGridTestConfiguration_BD_v2", 11, 2.5e-4, 5e-2, 5e-5, 5e-3)]
    [InlineData(true, "MicroGrid/BaseCase/MicroGrid-BE-MAS", "MicroGrid/BaseCase/MicroGrid-BD-MAS", 7, 2.5e-3, 0.2, 2e-4, 1e-2)]
    public void Cim3_microgrid_be_solves_to_its_published_sv_voltages(
        bool cgmes3, string model, string boundary, int compared, double vTolPu, double aTolDeg, double vTolOthersPu, double aTolOthersDeg)
    {
        string? root = ConformityRoot(cgmes3);
        Assert.True(root is not null, cgmes3
            ? "CGMES 3.0 data not found: set GRIDSIM_CGMES3_CONFORMITY_DIR or put cas-3-data-3.0.2 next to the 2.4.15 directory"
            : "GRIDSIM_CGMES_CONFORMITY_DIR is not set");   // GR-410: one data set configured, the other missing, fails loud
        string[] docs = ReadModel(root, model, boundary);

        CimNetwork net = CimImporter.Parse(docs, new CimImportOptions(TapPositions: CimTapPositions.StateVariables));
        Assert.Equal("EquivalentInjection:5,SynchronousMachine:2",
            string.Join(",", net.Generators.GroupBy(g => g.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")));
        Assert.Equal(cgmes3 ? 1 : 0, net.Svcs.Count);
        Assert.Equal(cgmes3 ? 1 : 0, net.Branches.Count(b => b.Class == "SeriesCompensator"));
        Assert.DoesNotContain(net.Notes, n => n.Contains("does not represent"));
        CimGenerator g1 = net.Generators.Single(g => g.Name == "BE-G1");
        Assert.Equal(115.5, g1.RemoteTargetKv);   // BE-G1 regulates BE-Busbar_6 remotely

        var notes = new List<string>();
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net, notes: notes);
        Assert.Single(bridged.RemoteVoltageControls);
        Assert.Equal(cgmes3 ? 1 : 0, bridged.SusceptanceControls.Count);
        Assert.Contains(notes, n => n.Contains("different base voltages"));   // N294: the 380 kV lines to the 400 kV boundary

        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged);
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote ?? r.Status.ToString());
        Assert.True(solver.ControlsSettled);

        Dictionary<string, (double V, double Angle)> sv = PublishedVoltages(docs);
        foreach (string id in bridged.DroppedNodes)
            Assert.True(!sv.TryGetValue(id, out var dead) || dead.V == 0.0);

        int slackId = bridged.Model.Buses.First(b => b.Type == BusType.Slack).Id;
        string slackNode = net.Buses.First(b => bridged.BusIdOfNode.TryGetValue(b.MRID, out int id) && id == slackId && sv.ContainsKey(b.MRID)).MRID;
        Assert.Equal("BE-Busbar_5", net.Buses.Single(b => b.MRID == slackNode).Name);   // BE-G2's bus
        double svSlackAngle = sv[slackNode].Angle;

        double worstV = 0.0, worstA = 0.0, othersV = 0.0, othersA = 0.0;
        int n = 0;
        foreach (CimBus bus in net.Buses)
        {
            if (!bridged.BusIdOfNode.TryGetValue(bus.MRID, out int id) || !sv.TryGetValue(bus.MRID, out (double V, double Angle) published)
                || published.V == 0.0) continue;
            Bus b = bridged.Model.Buses[id - 1];
            double dv = (r.Vm[id - 1] * b.BaseKv - published.V) / b.BaseKv;
            // The 3.0 SV writes angles in 0 .. 360: compare modulo a full turn.
            double da = r.VaDeg[id - 1] - (published.Angle - svSlackAngle);
            da -= 360.0 * Math.Round(da / 360.0);
            worstV = Math.Max(worstV, Math.Abs(dv));
            worstA = Math.Max(worstA, Math.Abs(da));
            if (bus.Name != "BE-Busbar_4")
            {
                othersV = Math.Max(othersV, Math.Abs(dv));
                othersA = Math.Max(othersA, Math.Abs(da));
            }
            n++;
        }
        Assert.Equal(compared, n);
        Assert.True(worstV <= vTolPu, $"worst |dV| {worstV:E3} pu above {vTolPu:E1}");
        Assert.True(worstA <= aTolDeg, $"worst |dAngle| {worstA:E3} deg above {aTolDeg:E1}");
        Assert.True(othersV <= vTolOthersPu, $"worst |dV| away from BE-Busbar_4 {othersV:E3} pu above {vTolOthersPu:E1}");
        Assert.True(othersA <= aTolOthersDeg, $"worst |dAngle| away from BE-Busbar_4 {othersA:E3} deg above {aTolOthersDeg:E1}");
    }

    // CIM3 (2026-10), N296: the SmallGrid's 14 voltage-regulating shunts. Its SV holds every regulated bus exactly at its
    // target while every shunt stays at its 5 sections (the IEEE 118 synchronous condensers it was built from): the tool
    // that wrote it regulated them continuously. Discrete regulation within the CGMES deadband (1 kV) moves no section
    // (every bus is within 0.5 kV of its target), so the residual stays at CIM2's 1.45e-4 pu; continuous regulation
    // (CimShuntControlMode.Continuous: 13 shunts, W.Lancst SC sits on a machine-held bus) closes it to 3.09e-5 pu, the
    // remaining 6.9e-3 deg being the slack's 1.71 MW (CIM2). Expected from the Python twin (checker3.py --shunts=...).
    // GR-410: skipped (not passed) without the models.
    [EnvironmentTheory("the CGMES conformity models are not bundled (N288)", "GRIDSIM_CGMES_CONFORMITY_DIR")]
    [InlineData(CimShuntControlMode.Discrete, 2.5e-4, 1.5e-2)]
    [InlineData(CimShuntControlMode.Continuous, 5e-5, 1.5e-2)]
    public void Cim3_smallgrid_regulating_shunts_discrete_and_continuous(CimShuntControlMode mode, double vTolPu, double aTolDeg)
    {
        string? root = ConformityRoot(cgmes3: false);
        Assert.True(root is not null, "GRIDSIM_CGMES_CONFORMITY_DIR is not set");   // GR-410: the attribute skips when unset
        string[] docs = ReadModel(root, "SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_BaseCase_Complete_v3.0.0",
            "SmallGrid/NodeBreaker/CGMES_v2.4.15_SmallGridTestConfiguration_Boundary_v3.0.0");
        CimNetwork net = CimImporter.Parse(docs);
        Assert.Equal(14, net.Shunts.Count(s => s.Control is not null));
        CimBridgeResult bridged = CimGridModelBridge.Bridge(net);
        Assert.Equal(13, bridged.ShuntRegulations.Count);
        var solver = new CimControlledPowerFlow(new NewtonRaphsonPowerFlow(), bridged, new CimControlOptions(Shunts: mode));
        PowerFlowResult r = solver.Solve(bridged.Model);
        Assert.True(r.Converged, solver.LastNote ?? r.Status.ToString());
        Assert.True(solver.ControlsSettled);
        if (mode == CimShuntControlMode.Discrete) Assert.All(solver.LastShuntSteps, s => Assert.Equal(0, s));

        Dictionary<string, (double V, double Angle)> sv = PublishedVoltages(docs);
        int slackId = bridged.Model.Buses.Single(b => b.Type == BusType.Slack).Id;
        string slackNode = net.Buses.First(b => bridged.BusIdOfNode.TryGetValue(b.MRID, out int id) && id == slackId && sv.ContainsKey(b.MRID)).MRID;
        double svSlackAngle = sv[slackNode].Angle;
        double worstV = 0.0, worstA = 0.0;
        int n = 0;
        foreach (CimBus bus in net.Buses)
        {
            if (!bridged.BusIdOfNode.TryGetValue(bus.MRID, out int id) || !sv.TryGetValue(bus.MRID, out (double V, double Angle) published)) continue;
            Bus b = bridged.Model.Buses[id - 1];
            worstV = Math.Max(worstV, Math.Abs((r.Vm[id - 1] * b.BaseKv - published.V) / b.BaseKv));
            worstA = Math.Max(worstA, Math.Abs(r.VaDeg[id - 1] - (published.Angle - svSlackAngle)));
            n++;
        }
        Assert.Equal(118, n);
        Assert.True(worstV <= vTolPu, $"worst |dV| {worstV:E3} pu above {vTolPu:E1}");
        Assert.True(worstA <= aTolDeg, $"worst |dAngle| {worstA:E3} deg above {aTolDeg:E1}");
    }

    /// <summary>CIM3: the conformity data directory: GRIDSIM_CGMES_CONFORMITY_DIR (cas-1.1.3-data-4.0.3); for CGMES 3.0,
    /// GRIDSIM_CGMES3_CONFORMITY_DIR (cas-3-data-3.0.2), else the cas-3-data-3.0.2 directory next to the first. Null when
    /// not configured (the opt-in benchmark then checks nothing); a configured directory that does not exist fails.</summary>
    private static string? ConformityRoot(bool cgmes3)
    {
        string? root = Environment.GetEnvironmentVariable("GRIDSIM_CGMES_CONFORMITY_DIR");
        if (!cgmes3) return string.IsNullOrWhiteSpace(root) ? null : root;
        string? root3 = Environment.GetEnvironmentVariable("GRIDSIM_CGMES3_CONFORMITY_DIR");
        if (!string.IsNullOrWhiteSpace(root3))
        {
            Assert.True(Directory.Exists(root3), $"GRIDSIM_CGMES3_CONFORMITY_DIR names {root3}, which does not exist");
            return root3;
        }
        if (string.IsNullOrWhiteSpace(root)) return null;
        string sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root.TrimEnd('/', '\\')))!, "cas-3-data-3.0.2");
        return Directory.Exists(sibling) ? sibling : null;
    }

    /// <summary>CIM3: every profile document of a model and its boundary set (DL / GL left out), in ordinal path order.</summary>
    private static string[] ReadModel(string root, string model, string boundary)
    {
        string[] files = new[] { model, boundary }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.xml"))
            .Where(f => !f.Contains("_DL_", StringComparison.Ordinal) && !f.Contains("_GL_", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);
        return files.Select(File.ReadAllText).ToArray();
    }

    /// <summary>The SvVoltage of every TopologicalNode in the documents (kV, degrees), read here independently of the
    /// importer: mRID (the rdf:resource with its '#' and one leading '_' removed) -> (v, angle).</summary>
    private static Dictionary<string, (double V, double Angle)> PublishedVoltages(IEnumerable<string> docs)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var sv = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (string doc in docs)
        {
            foreach (XElement e in XDocument.Parse(doc).Root!.Elements().Where(x => x.Name.LocalName == "SvVoltage"))
            {
                XElement? node = e.Elements().FirstOrDefault(x => x.Name.LocalName == "SvVoltage.TopologicalNode");
                string? v = e.Elements().FirstOrDefault(x => x.Name.LocalName == "SvVoltage.v")?.Value;
                string? a = e.Elements().FirstOrDefault(x => x.Name.LocalName == "SvVoltage.angle")?.Value;
                string? res = node?.Attribute(rdf + "resource")?.Value;
                if (res is null || v is null || a is null) continue;
                string id = res.TrimStart('#');
                if (id.StartsWith('_')) id = id[1..];
                sv[id] = (double.Parse(v, CultureInfo.InvariantCulture), double.Parse(a, CultureInfo.InvariantCulture));
            }
        }
        return sv;
    }
}
