using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>What <see cref="CgmesBusBranch30Writer.Write"/> wrote.</summary>
/// <param name="Files">EQ, TP, SSH and SV, in that order (<c>&lt;case&gt;_EQ.xml</c> and so on).</param>
/// <param name="Source">The <see cref="CgmesWriter"/> result the documents were written from: the same CIM network, the same
/// exact values and the same object names (TopologicalNode N&lt;id&gt;, ACLineSegment L&lt;k&gt;, PowerTransformer T&lt;k&gt;,
/// SynchronousMachine G&lt;k&gt;). <see cref="CgmesExtraProfiles"/> refers to its objects through it.</param>
/// <param name="Omissions">What the documents do not carry, and why.</param>
/// <param name="Notes">The source's notes plus the 3.0 choices made.</param>
public sealed record CgmesBusBranch30Result(IReadOnlyList<CgmesProfileFile> Files, CgmesWriteResult Source,
    IReadOnlyList<CgmesOmission> Omissions, IReadOnlyList<string> Notes);

/// <summary>
/// WP24: writes a bus-branch <see cref="GridModel"/> as separate CGMES 3.0 documents (EQ, TP, SSH, SV), each with an
/// md:FullModel header naming its 3.0 profile URI (<see cref="CgmesVocabulary.ProfileUris"/>) and its Model.DependentOn links.
/// The values are those of <see cref="CgmesWriter.Write"/> (the same refusals, the same exact-double search), so GridSim's CIM
/// reader, given the four documents with <see cref="CgmesWriter.ReadBackOptions"/>, rebuilds what it rebuilds from the
/// 2.4.15 document. <see cref="CgmesWriter"/> itself is unchanged.
/// </summary>
/// <remarks>
/// <para><b>Sources.</b> Class and attribute names, the profile each attribute belongs to, and the required properties are
/// read from the ENTSO-E CGMES 3.0 RDFS and SHACL files of github.com/entsoe/CGMES-AP (Apache-2.0): Equipment.rdf,
/// Topology.rdf, SteadyStateHypothesis.rdf and StateVariables.rdf, and the matching constraint files (CoreEquipment-EU,
/// Topology-EU, SteadyStateHypothesis-EU and StateVariables-EU Constraints, version 3.0.0). Every class written below was
/// checked against those files.</para>
/// <para><b>Differences from the 2.4.15 document handled</b> (each checked against the 3.0 RDFS):</para>
/// <list type="bullet">
/// <item>Namespace http://iec.ch/TC57/CIM100#; objects named rdf:about="urn:uuid:&lt;uuid&gt;" (the form GridSim's node-breaker
/// 3.0 export already uses), the UUID derived from the case name and the 2.4.15 mRID (<see cref="ObjectIri"/>).</item>
/// <item>IdentifiedObject.mRID is written on every IdentifiedObject (required in 3.0), and IdentifiedObject.name on each.</item>
/// <item>The profile split: BaseVoltage, containers, equipment, terminals, ends, tap changers and limits in EQ; TopologicalNode
/// (with its BaseVoltage and ConnectivityNodeContainer) and Terminal.TopologicalNode in TP only; p and q, Equipment.inService,
/// ACDCTerminal.connected, RegulatingControl targets, TapChanger.step, ShuntCompensator.sections, referencePriority and
/// ApparentPowerLimit.value in SSH; SvVoltage in SV.</item>
/// <item>Equipment.inService (SSH) is written true for every piece of conducting equipment and every generating unit (all
/// written equipment is in service; the CgmesWriter leaves out-of-service equipment out).</item>
/// <item>Operational limits: OperationalLimitType with isInfiniteDuration, direction absoluteValue and
/// eu:OperationalLimitType.kind = eu:LimitKind.patl (namespace http://iec.ch/TC57/CIM100-European#), the 2.4.15
/// entsoe:limitType having no 3.0 place; ApparentPowerLimit.normalValue in EQ and ApparentPowerLimit.value in SSH.</item>
/// <item>TopologicalNode needs a ConnectivityNodeContainer, so each bus gets a Substation and a VoltageLevel (with its
/// BaseVoltage) under one GeographicalRegion / SubGeographicalRegion; loads, shunts and machines sit in their bus's
/// VoltageLevel, a transformer in its first end's Substation, and each ACLineSegment in a Line of its own (Line.Region the
/// subregion) with its ConductingEquipment.BaseVoltage. The EQ header names CoreEquipment-EU only: nothing of the
/// Operation-EU profile is written.</item>
/// <item>Tap changers carry the 3.0 required lowStep / highStep (the whole steps bracketing the step and 0), normalStep 0,
/// neutralU (the end's ratedU), ltcFlag false and, in SSH, TapChanger.controlEnabled false; a linear phase changer carries
/// xMin = xMax = |end 1 x| (a flat curve, which GridSim's reader treats as the end impedance it already uses).</item>
/// <item>PowerTransformerEnd.b and TransformerEnd.BaseVoltage are always written (required in 3.0).</item>
/// <item>Unchanged from 2.4.15: the ACLineSegment attributes, EnergyConsumer p / q (the ConformLoad and NonConformLoad group
/// structure is the same and is not used), the GeneratingUnit subclasses and FossilFuel.</item>
/// </list>
/// <para><b>Not written</b>: the machine inertia (in 3.0 a dynamics-profile attribute: <c>--profiles dy</c> writes the DY
/// models), GeneratingUnit.normalPF (the case holds none: distributed-slack factors are a solve option, not model data), SynchronousMachine.type and operatingMode are
/// written as "generator" (GridSim holds no machine kind), SvPowerFlow, SvInjection, SvStatus, SvTapStep,
/// SvShuntCompensatorSections and TopologicalIsland (derivable from a solve), and SvSwitch (the bus-branch export has no
/// switches). No HVDC or FACTS classes, as for 2.4.15 (the CgmesWriter refuses such models).</para>
/// </remarks>
public static class CgmesBusBranch30Writer
{
    /// <summary>The European extension namespace of CGMES 3.0 (OperationalLimitType.kind, LimitKind).</summary>
    public const string Cim100European = "http://iec.ch/TC57/CIM100-European#";
    private const string ModelingAuthority = "http://gridsim.invalid/bus-branch-3.0";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Num(double v) => v.ToString("R", Inv);

    /// <summary>The UUID of the object whose 2.4.15 (CgmesWriter) mRID is <paramref name="mrid"/>, in the 3.0 documents of the
    /// case <paramref name="caseName"/>.</summary>
    public static string ObjectUuid(string caseName, string mrid) => CgmesVocabulary.Uuid("bus-branch-3.0/" + caseName + "/" + mrid);

    /// <summary>"urn:uuid:" + <see cref="ObjectUuid"/>: how the 3.0 documents name the object.</summary>
    public static string ObjectIri(string caseName, string mrid) => "urn:uuid:" + ObjectUuid(caseName, mrid);

    /// <summary>Read back documents this writer wrote (CIM import with <see cref="CgmesWriter.ReadBackOptions"/>), bridged on
    /// <paramref name="baseMva"/>.</summary>
    public static GridModel ReadBack(IEnumerable<string> documents, double baseMva, string name) =>
        CimGridModelBridge.ToGridModel(CimImporter.Parse(documents, CgmesWriter.ReadBackOptions), baseMva, name);

    /// <summary>Write <paramref name="model"/> as CGMES 3.0 EQ, TP, SSH and SV documents. Throws
    /// <see cref="InvalidDataException"/> for every model <see cref="CgmesWriter.Write"/> refuses.</summary>
    public static CgmesBusBranch30Result Write(GridModel model, string scenarioTime = "2026-01-01T00:00:00Z")
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scenarioTime);
        CgmesWriteResult src = CgmesWriter.Write(model);
        CimNetwork net = src.Network;
        string name = model.Name ?? "case";
        var w = new Doc(name);
        var notes = new List<string>(src.Notes);
        var omissions = new List<CgmesOmission>();

        // =============================== EQ ===============================================================================
        XElement eq = w.Root(CgmesProfileKind.EQ, scenarioTime, Array.Empty<CgmesProfileKind>(), european: true);
        var bv = new Dictionary<double, string>();
        string Bv(double kv)
        {
            if (bv.TryGetValue(kv, out string? id)) return id;
            id = "bv_" + Num(kv);
            bv[kv] = id;
            eq.Add(w.Def("BaseVoltage", id, Num(kv) + " kV", ("BaseVoltage.nominalVoltage", Num(kv))));
            return id;
        }
        eq.Add(w.Def("GeographicalRegion", "gr", "region"));
        eq.Add(w.Def("SubGeographicalRegion", "sgr", "subregion", w.Ref("SubGeographicalRegion.Region", "gr")));
        var kvOf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (CimBus b in net.Buses)
        {
            kvOf[b.MRID] = b.BaseKv;
            string bvId = Bv(b.BaseKv);
            eq.Add(w.Def("Substation", "ss_" + b.MRID, b.Name, w.Ref("Substation.Region", "sgr")));
            eq.Add(w.Def("VoltageLevel", "vl_" + b.MRID, b.Name, w.Ref("VoltageLevel.Substation", "ss_" + b.MRID), w.Ref("VoltageLevel.BaseVoltage", bvId)));
        }

        var terminals = new List<(string Id, string Node)>();
        var sshEquipment = new List<XElement>();
        var ssh = new List<XElement>();
        void Terminal(string id, string equipment, int seq, string node)
        {
            eq.Add(w.Def("Terminal", id, id, ("ACDCTerminal.sequenceNumber", seq.ToString(Inv)), w.Ref("Terminal.ConductingEquipment", equipment)));
            terminals.Add((id, node));
        }
        void InService(string cls, string mrid) => sshEquipment.Add(w.Desc(cls, mrid, ("Equipment.inService", "true")));

        bool anyRating = net.Branches.Any(br => br.RateAMva is not null) || net.Transformers.Any(t => t.RateAMva is not null && t.Ends.Count == 2);
        if (anyRating)
            eq.Add(w.Def("OperationalLimitType", "olt_patl", "PATL",
                ("OperationalLimitType.isInfiniteDuration", "true"),
                Doc.Enum("OperationalLimitType.direction", "OperationalLimitDirectionKind.absoluteValue"),
                new XElement(Doc.Eu + "OperationalLimitType.kind", new XAttribute(Doc.RdfNs + "resource", Cim100European + "LimitKind.patl"))));
        void Rating(string mrid, string terminal, double mva)
        {
            eq.Add(w.Def("OperationalLimitSet", "ols_" + mrid, "ratings", w.Ref("OperationalLimitSet.Terminal", terminal)));
            eq.Add(w.Def("ApparentPowerLimit", "apl_" + mrid, "PATL", ("ApparentPowerLimit.normalValue", Num(mva)),
                w.Ref("OperationalLimit.OperationalLimitSet", "ols_" + mrid), w.Ref("OperationalLimit.OperationalLimitType", "olt_patl")));
            ssh.Add(w.Desc("ApparentPowerLimit", "apl_" + mrid, ("ApparentPowerLimit.value", Num(mva))));
        }

        // ---- lines ---------------------------------------------------------------------------------------------------
        foreach (CimBranch br in net.Branches)
        {
            if (br.Class != "ACLineSegment")
                throw new InvalidOperationException($"CgmesWriter gave a {br.Class}; the 3.0 bus-branch writer writes ACLineSegments only.");
            var props = new List<object> { ("ACLineSegment.r", Num(br.R)), ("ACLineSegment.x", Num(br.X)), ("ACLineSegment.bch", Num(br.B)) };
            if (br.G != 0.0) props.Add(("ACLineSegment.gch", Num(br.G)));
            // Every line has its BaseVoltage (the line's own when the source gives one, else its from-bus base kV: the
            // CgmesWriter writes an ACLineSegment only with both ends on one base voltage) and sits in a Line container of
            // its own.
            props.Add(w.Ref("ConductingEquipment.BaseVoltage", Bv(br.BaseKv > 0 ? br.BaseKv : kvOf[br.FromNode])));
            props.Add(w.Ref("Equipment.EquipmentContainer", "line_" + br.MRID));
            eq.Add(w.Def("Line", "line_" + br.MRID, br.Name, w.Ref("Line.Region", "sgr")));
            eq.Add(w.Def("ACLineSegment", br.MRID, br.Name, props.ToArray()));
            Terminal($"t_{br.MRID}_1", br.MRID, 1, br.FromNode);
            Terminal($"t_{br.MRID}_2", br.MRID, 2, br.ToNode);
            InService("ACLineSegment", br.MRID);
            if (br.RateAMva is double r) Rating(br.MRID, $"t_{br.MRID}_1", r);
        }

        // ---- loads ---------------------------------------------------------------------------------------------------
        foreach (CimLoad load in net.Loads)
        {
            eq.Add(w.Def(load.Class, load.MRID, load.Name, w.Ref("Equipment.EquipmentContainer", "vl_" + load.Node)));
            Terminal($"t_{load.MRID}", load.MRID, 1, load.Node);
            ssh.Add(w.Desc(load.Class, load.MRID, ("EnergyConsumer.p", Num(load.P)), ("EnergyConsumer.q", Num(load.Q))));
            InService(load.Class, load.MRID);
        }

        // ---- shunts: one section holding the whole admittance (B x 1 and G x 1 are exact) ----------------------------
        foreach (CimShunt sh in net.Shunts)
        {
            if (sh.Class != "LinearShuntCompensator" || sh.Control is not null)
                throw new InvalidOperationException($"CgmesWriter gave a {sh.Class}; the 3.0 bus-branch writer writes plain linear shunts only.");
            eq.Add(w.Def("LinearShuntCompensator", sh.MRID, sh.Name, w.Ref("Equipment.EquipmentContainer", "vl_" + sh.Node),
                ("ShuntCompensator.maximumSections", "1"), ("ShuntCompensator.normalSections", "1"), ("ShuntCompensator.nomU", Num(kvOf[sh.Node])),
                ("LinearShuntCompensator.bPerSection", Num(sh.B)), ("LinearShuntCompensator.gPerSection", Num(sh.G))));
            Terminal($"t_{sh.MRID}", sh.MRID, 1, sh.Node);
            ssh.Add(w.Desc("LinearShuntCompensator", sh.MRID, ("ShuntCompensator.sections", "1"), ("RegulatingCondEq.controlEnabled", "false")));
            InService("LinearShuntCompensator", sh.MRID);
        }

        // ---- machines ------------------------------------------------------------------------------------------------
        int inertiaLeft = 0;
        foreach (CimGenerator g in net.Generators)
        {
            if (g.Class != "SynchronousMachine" || g.RegulatedNode is not null)
                throw new InvalidOperationException($"CgmesWriter gave a {g.Class}; the 3.0 bus-branch writer writes locally regulating SynchronousMachines only.");
            bool unit = g.MinP is not null || g.MaxP is not null || g.Type.Length > 0;
            var props = new List<object> { w.Ref("Equipment.EquipmentContainer", "vl_" + g.Node) };
            if (g.RatedSMva is double s) props.Add(("RotatingMachine.ratedS", Num(s)));
            props.Add(("RotatingMachine.ratedU", Num(kvOf[g.Node])));
            if (g.MinQ is double qmin) props.Add(("SynchronousMachine.minQ", Num(qmin)));
            if (g.MaxQ is double qmax) props.Add(("SynchronousMachine.maxQ", Num(qmax)));
            props.Add(Doc.Enum("SynchronousMachine.type", "SynchronousMachineKind.generator"));
            if (g.TargetKv is not null) props.Add(w.Ref("RegulatingCondEq.RegulatingControl", "rc_" + g.MRID));
            if (unit) props.Add(w.Ref("RotatingMachine.GeneratingUnit", "gu_" + g.MRID));
            eq.Add(w.Def("SynchronousMachine", g.MRID, g.Name, props.ToArray()));
            Terminal($"t_{g.MRID}", g.MRID, 1, g.Node);
            // R267 (as CimExporter): RotatingMachine.p / q are in the load sign; '0.0 - x' keeps a zero as "0".
            var sshProps = new List<object> { ("RotatingMachine.p", Num(0.0 - g.P)), ("RotatingMachine.q", Num(0.0 - g.Q)) };
            if (g.VoltageControl is bool vc) sshProps.Add(("RegulatingCondEq.controlEnabled", vc ? "true" : "false"));
            sshProps.Add(("SynchronousMachine.referencePriority", g.ReferencePriority.ToString(Inv)));
            sshProps.Add(Doc.Enum("SynchronousMachine.operatingMode", "SynchronousMachineOperatingMode.generator"));
            ssh.Add(w.Desc("SynchronousMachine", g.MRID, sshProps.ToArray()));
            InService("SynchronousMachine", g.MRID);
            if (g.TargetKv is double target)
            {
                eq.Add(w.Def("RegulatingControl", "rc_" + g.MRID, g.Name + " voltage",
                    Doc.Enum("RegulatingControl.mode", "RegulatingControlModeKind.voltage"), w.Ref("RegulatingControl.Terminal", $"t_{g.MRID}")));
                ssh.Add(w.Desc("RegulatingControl", "rc_" + g.MRID, ("RegulatingControl.targetValue", Num(target)),
                    Doc.Enum("RegulatingControl.targetValueUnitMultiplier", "UnitMultiplier.k"),
                    ("RegulatingControl.enabled", "true"), ("RegulatingControl.discrete", "false")));
            }
            if (unit)
            {
                (string unitClass, string? kindProp, string? kind, string? fuel) = UnitClassOf(g.Type);
                var up = new List<object>();
                if (g.MinP is double pmin) up.Add(("GeneratingUnit.minOperatingP", Num(pmin)));
                if (g.MaxP is double pmax) up.Add(("GeneratingUnit.maxOperatingP", Num(pmax)));
                if (kindProp is not null) up.Add(Doc.Enum(kindProp, kind!));
                eq.Add(w.Def(unitClass, "gu_" + g.MRID, g.Name + " unit", up.ToArray()));
                InService(unitClass, "gu_" + g.MRID);
                if (fuel is not null)
                    eq.Add(w.Def("FossilFuel", "ff_" + g.MRID, g.Name + " fuel", Doc.Enum("FossilFuel.fossilFuelType", "FuelType." + fuel),
                        w.Ref("FossilFuel.ThermalGeneratingUnit", "gu_" + g.MRID)));
            }
            if (g.H is not null) inertiaLeft++;
        }

        // ---- two-winding transformers (CgmesWriter writes no three-winding unit) ---------------------------------------
        foreach (CimTransformer tx in net.Transformers)
        {
            if (tx.Ends.Count != 2)
                throw new InvalidOperationException("CgmesWriter gave a transformer with other than two ends; the 3.0 bus-branch writer writes two-winding units only.");
            eq.Add(w.Def("PowerTransformer", tx.MRID, tx.Name, w.Ref("Equipment.EquipmentContainer", "ss_" + tx.Ends[0].Node)));
            for (int k = 0; k < tx.Ends.Count; k++)
            {
                CimTransformerEnd e = tx.Ends[k];
                string endId = $"{tx.MRID}_e{k + 1}", termId = $"t_{endId}";
                Terminal(termId, tx.MRID, k + 1, e.Node);
                var ep = new List<object>
                {
                    ("TransformerEnd.endNumber", (k + 1).ToString(Inv)), w.Ref("PowerTransformerEnd.PowerTransformer", tx.MRID),
                    w.Ref("TransformerEnd.Terminal", termId), w.Ref("TransformerEnd.BaseVoltage", Bv(e.RatedU)),
                    ("PowerTransformerEnd.r", Num(e.R)), ("PowerTransformerEnd.x", Num(e.X)), ("PowerTransformerEnd.ratedU", Num(e.RatedU)),
                    ("PowerTransformerEnd.b", Num(e.B)),
                };
                if (e.G != 0.0) ep.Add(("PowerTransformerEnd.g", Num(e.G)));
                eq.Add(w.Def("PowerTransformerEnd", endId, endId, ep.ToArray()));
            }
            InService("PowerTransformer", tx.MRID);
            string end1 = tx.MRID + "_e1";
            CimTransformerEnd e1 = tx.Ends[0];
            if (tx.TapRatio != 1.0)
            {
                // As CimExporter: the linear form (step 100 x (ratio - 1), neutral 0, increment 1) when the reader's
                // 1 + step x 1 / 100 gives the ratio back to the bit, else a one-point table at step 0 holding the ratio.
                double step = 100.0 * (tx.TapRatio - 1.0);
                bool linear = BitConverter.DoubleToInt64Bits(1.0 + (step - 0.0) * 1.0 / 100.0) == BitConverter.DoubleToInt64Bits(tx.TapRatio);
                if (!linear) step = 0.0;
                var rp = new List<object> { w.Ref("RatioTapChanger.TransformerEnd", end1), ("RatioTapChanger.stepVoltageIncrement", "1") };
                rp.AddRange(StepBounds(step, e1.RatedU));
                if (!linear) rp.Add(w.Ref("RatioTapChanger.RatioTapChangerTable", "rtc_" + tx.MRID + "_table"));
                eq.Add(w.Def("RatioTapChanger", "rtc_" + tx.MRID, tx.Name + " ratio", rp.ToArray()));
                if (!linear)
                {
                    eq.Add(w.Def("RatioTapChangerTable", "rtc_" + tx.MRID + "_table", tx.Name + " ratio table"));
                    eq.Add(w.Point("RatioTapChangerTablePoint", "rtc_" + tx.MRID + "_point", ("TapChangerTablePoint.step", "0"),
                        ("TapChangerTablePoint.ratio", Num(tx.TapRatio)), w.Ref("RatioTapChangerTablePoint.RatioTapChangerTable", "rtc_" + tx.MRID + "_table")));
                }
                ssh.Add(w.Desc("RatioTapChanger", "rtc_" + tx.MRID, ("TapChanger.step", Num(step)), ("TapChanger.controlEnabled", "false")));
            }
            if (tx.RateAMva is double txRate) Rating(tx.MRID, "t_" + end1, txRate);
            if (tx.PhaseShiftDeg != 0.0)
            {
                double flat = Math.Abs(e1.X);
                var pp = new List<object> { w.Ref("PhaseTapChanger.TransformerEnd", end1), ("PhaseTapChangerLinear.stepPhaseShiftIncrement", "1"),
                    ("PhaseTapChangerLinear.xMin", Num(flat)), ("PhaseTapChangerLinear.xMax", Num(flat)) };
                pp.AddRange(StepBounds(tx.PhaseShiftDeg, e1.RatedU));
                eq.Add(w.Def("PhaseTapChangerLinear", "ptc_" + tx.MRID, tx.Name + " phase", pp.ToArray()));
                ssh.Add(w.Desc("PhaseTapChangerLinear", "ptc_" + tx.MRID, ("TapChanger.step", Num(tx.PhaseShiftDeg)), ("TapChanger.controlEnabled", "false")));
            }
        }

        // =============================== TP ===============================================================================
        XElement tp = w.Root(CgmesProfileKind.TP, scenarioTime, new[] { CgmesProfileKind.EQ });
        foreach (CimBus b in net.Buses)
            tp.Add(w.Def("TopologicalNode", b.MRID, b.Name, w.Ref("TopologicalNode.BaseVoltage", bv[b.BaseKv]),
                w.Ref("TopologicalNode.ConnectivityNodeContainer", "vl_" + b.MRID)));
        foreach ((string id, string node) in terminals)
            tp.Add(w.Desc("Terminal", id, w.Ref("Terminal.TopologicalNode", node)));

        // =============================== SSH ==============================================================================
        XElement sshDoc = w.Root(CgmesProfileKind.SSH, scenarioTime, new[] { CgmesProfileKind.EQ });
        foreach ((string id, _) in terminals) sshDoc.Add(w.Desc("Terminal", id, ("ACDCTerminal.connected", "true")));
        foreach (XElement e in sshEquipment) sshDoc.Add(e);
        foreach (XElement e in ssh) sshDoc.Add(e);

        // =============================== SV ===============================================================================
        XElement sv = w.Root(CgmesProfileKind.SV, scenarioTime, new[] { CgmesProfileKind.TP, CgmesProfileKind.SSH });
        int svCount = 0;
        foreach (CimBus b in net.Buses)
        {
            if (b.SvVKv is not double v) continue;
            var p = new List<object> { ("SvVoltage.v", Num(v)) };
            if (b.SvAngleDeg is double a) p.Add(("SvVoltage.angle", Num(a)));
            p.Add(w.Ref("SvVoltage.TopologicalNode", b.MRID));
            sv.Add(w.Point("SvVoltage", "sv_" + b.MRID, p.ToArray()));
            svCount++;
        }

        var files = new List<CgmesProfileFile>
        {
            new(CgmesProfileKind.EQ, name + "_EQ.xml", Doc.Serialise(eq)),
            new(CgmesProfileKind.TP, name + "_TP.xml", Doc.Serialise(tp)),
            new(CgmesProfileKind.SSH, name + "_SSH.xml", Doc.Serialise(sshDoc)),
            new(CgmesProfileKind.SV, name + "_SV.xml", Doc.Serialise(sv)),
        };
        notes.Add($"CGMES 3.0 bus-branch: {net.Buses.Count} TopologicalNode(s), {svCount} SvVoltage(s) (a bus at 1.0 pu and 0 degrees has none, as in the 2.4.15 document).");
        if (inertiaLeft > 0)
            omissions.Add(new CgmesOmission(CgmesProfileKind.DY, "RotatingMachineDynamics.inertia", $"{inertiaLeft} machine(s) carry an inertia; in CGMES 3.0 it is a dynamics-profile attribute, so the EQ / SSH documents do not carry it (--profiles dy writes the DY models)."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.SSH, "GeneratingUnit.normalPF", "The case holds none: distributed-slack factors are a solve option, not model data."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.EQ, "SynchronousMachine.type and SynchronousMachine.operatingMode (SSH)", "GridSim holds no machine kind; both are written as generator."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.SV, "SvPowerFlow, SvInjection, SvStatus, SvTapStep, SvShuntCompensatorSections, TopologicalIsland", "GridSim writes the stored bus voltages only; the rest is derivable from a solve."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.SV, "SvSwitch", "the bus-branch export has no switches."));
        omissions.Add(new CgmesOmission(CgmesProfileKind.EQ, "HVDC, FACTS, static var compensators, induction machines, ZIP loads, on-load tap control", "as for the 2.4.15 document: CgmesWriter refuses such models."));
        return new CgmesBusBranch30Result(files, src, omissions, notes);
    }

    /// <summary>lowStep / highStep (the whole steps bracketing <paramref name="step"/> and 0), normalStep 0 (the neutral, within
    /// them), neutralStep 0, neutralU and ltcFlag false. Every value the reader uses (step, neutralStep) is as the 2.4.15
    /// document writes it; the bounds keep the step inside lowStep..highStep, which the reader checks.</summary>
    private static IEnumerable<object> StepBounds(double step, double neutralU)
    {
        long low = (long)Math.Floor(Math.Min(step, 0.0)), high = (long)Math.Ceiling(Math.Max(step, 0.0));
        yield return ("TapChanger.neutralStep", "0");
        yield return ("TapChanger.lowStep", low.ToString(Inv));
        yield return ("TapChanger.highStep", high.ToString(Inv));
        yield return ("TapChanger.normalStep", "0");
        yield return ("TapChanger.neutralU", Num(neutralU));
        yield return ("TapChanger.ltcFlag", "false");
    }

    /// <summary>The GeneratingUnit subclass of a generator type (as CimExporter's mapping), the enumeration property it needs,
    /// and the fuel to write.</summary>
    private static (string Class, string? KindProp, string? Kind, string? Fuel) UnitClassOf(string type) => type switch
    {
        "wind-onshore" => ("WindGeneratingUnit", "WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.onshore", null),
        "wind-offshore" => ("WindGeneratingUnit", "WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.offshore", null),
        "wind" => ("WindGeneratingUnit", null, null, null),
        "solar" => ("SolarGeneratingUnit", null, null, null),
        "hydro" => ("HydroGeneratingUnit", null, null, null),
        "pumped-storage" => ("HydroGeneratingUnit", "HydroGeneratingUnit.energyConversionCapability", "HydroEnergyConversionKind.pumpAndGenerator", null),
        "nuclear" => ("NuclearGeneratingUnit", null, null, null),
        "thermal" => ("ThermalGeneratingUnit", null, null, null),
        "gas" or "coal" or "lignite" or "oil" => ("ThermalGeneratingUnit", null, null, type),
        _ => ("GeneratingUnit", null, null, null),
    };

    /// <summary>Element construction for the four documents.</summary>
    private sealed class Doc(string caseName)
    {
        public static readonly XNamespace Cim = CgmesVocabulary.CimNamespace(CgmesVersion.V3_0);
        public static readonly XNamespace Eu = Cim100European;
        public static readonly XNamespace RdfNs = CgmesVocabulary.Rdf;
        private static readonly XNamespace MdNs = CgmesVocabulary.Md;

        private string Iri(string mrid) => ObjectIri(caseName, mrid);

        public XElement Root(CgmesProfileKind kind, string time, IEnumerable<CgmesProfileKind> dependsOn, bool european = false)
        {
            var root = new XElement(RdfNs + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", RdfNs.NamespaceName), new XAttribute(XNamespace.Xmlns + "cim", Cim.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "md", MdNs.NamespaceName));
            if (european) root.Add(new XAttribute(XNamespace.Xmlns + "eu", Eu.NamespaceName));
            var fm = new XElement(MdNs + "FullModel", new XAttribute(RdfNs + "about", "urn:uuid:" + CgmesVocabulary.Uuid($"model/{caseName}/{kind}")),
                new XElement(MdNs + "Model.scenarioTime", time), new XElement(MdNs + "Model.created", time),
                new XElement(MdNs + "Model.description", $"GridSim CGMES 3.0 bus-branch {kind} profile"), new XElement(MdNs + "Model.version", "1"));
            foreach (CgmesProfileKind d in dependsOn)
                fm.Add(new XElement(MdNs + "Model.DependentOn", new XAttribute(RdfNs + "resource", "urn:uuid:" + CgmesVocabulary.Uuid($"model/{caseName}/{d}"))));
            // The bus-branch EQ writes nothing of the Operation-EU profile, so it names CoreEquipment-EU alone.
            foreach (string p in CgmesVocabulary.ProfileUris(CgmesVersion.V3_0, kind).Where(p => !p.Contains("/Operation-EU/", StringComparison.Ordinal)))
                fm.Add(new XElement(MdNs + "Model.profile", p));
            fm.Add(new XElement(MdNs + "Model.modelingAuthoritySet", ModelingAuthority));
            root.Add(fm);
            return root;
        }

        /// <summary>An IdentifiedObject defined here: rdf:about, IdentifiedObject.mRID (the UUID) and IdentifiedObject.name.</summary>
        public XElement Def(string cls, string mrid, string name, params object[] props)
        {
            var e = new XElement(Cim + cls, new XAttribute(RdfNs + "about", Iri(mrid)),
                new XElement(Cim + "IdentifiedObject.mRID", ObjectUuid(caseName, mrid)),
                new XElement(Cim + "IdentifiedObject.name", name));
            Add(e, props);
            return e;
        }

        /// <summary>An object that is not an IdentifiedObject (a table point, an SvVoltage): rdf:about only.</summary>
        public XElement Point(string cls, string key, params object[] props)
        {
            var e = new XElement(Cim + cls, new XAttribute(RdfNs + "about", Iri(key)));
            Add(e, props);
            return e;
        }

        /// <summary>A description, in another profile, of an object defined in EQ or TP.</summary>
        public XElement Desc(string cls, string mrid, params object[] props) => Point(cls, mrid, props);

        public XElement Ref(string prop, string mrid) => new(Cim + prop, new XAttribute(RdfNs + "resource", Iri(mrid)));

        public static XElement Enum(string prop, string literal) => new(Cim + prop, new XAttribute(RdfNs + "resource", Cim.NamespaceName + literal));

        private static void Add(XElement e, object[] props)
        {
            foreach (object p in props)
            {
                switch (p)
                {
                    case XElement x: e.Add(x); break;
                    case ValueTuple<string, string> t: e.Add(new XElement(Cim + t.Item1, t.Item2)); break;
                    default: throw new InvalidOperationException("A property is a (name, text) pair or an element.");
                }
            }
        }

        public static string Serialise(XElement root)
        {
            var sb = new StringBuilder();
            var s = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace, OmitXmlDeclaration = true };
            using (var xw = XmlWriter.Create(sb, s)) root.WriteTo(xw);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb + "\n";
        }
    }
}
