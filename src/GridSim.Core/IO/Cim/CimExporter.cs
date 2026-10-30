using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace GridSim.Core.IO.Cim;

/// <summary>
/// Exports a <see cref="CimNetwork"/> to striped CIM / CGMES RDF/XML - the inverse of
/// <see cref="CimImporter"/>. Emits TopologicalNode buses (with their BaseVoltage), ACLineSegment branches,
/// EnergyConsumer loads, SynchronousMachine generators and two- and three-winding PowerTransformers (ends +
/// tap/phase changers; a three-winding unit gets one changer per tapped winding), each with the Terminals
/// linking equipment to nodes - so an import-export round-trip preserves buses, base voltages, branches,
/// loads, generators and transformers. D-DOC-099 / 101 / 104 (2026-10): a load keeps its class, a line its own
/// base voltage, and a generator its machine data (ratedS, minQ / maxQ, referencePriority, controlEnabled, a
/// local voltage RegulatingControl, a GeneratingUnit with its operating limits, and its inertia as a
/// SynchronousMachineSimplified dynamics model); each is written only when present. N253 / N254 (CIM1, 2026-10): a
/// closed switch joining two buses (CimNetwork.ClosedSwitches) is written as a closed switch of its class with its
/// retained flag, and a generator's type as the GeneratingUnit subclass it came from (with its FossilFuel for a fuel
/// type); both only when present, so a network without them exports exactly as before. N286 (CIM2, 2026-10): shunts
/// (as one section holding their whole admittance), ExternalNetworkInjection / EquivalentInjection /
/// PowerElectronicsConnection generators (under their own class, with a PV / wind / battery unit for a typed
/// connection) and AsynchronousMachine loads are written back under their own class; again only when present.
/// </summary>
public static class CimExporter
{
    private static readonly XNamespace Cim = "http://iec.ch/TC57/2013/CIM-schema-cim16#";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Entsoe = "http://entsoe.eu/CIM/SchemaExtension/3/1#";

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Serialise the network to a CGMES RDF/XML string.</summary>
    public static string Export(CimNetwork net)
    {
        var root = new XElement(Rdf + "RDF",
            new XAttribute(XNamespace.Xmlns + "cim", Cim.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName));

        // Base voltages: one BaseVoltage element per distinct nominal kV, referenced from each node so the
        // per-unit base survives the round trip.
        // D-DOC-101: a line's own base voltage (CimBranch.BaseKv) is written too, as ConductingEquipment.BaseVoltage.
        var bvMrid = new Dictionary<double, string>();
        foreach (double kv in net.Buses.Select(b => b.BaseKv).Concat(net.Branches.Select(b => b.BaseKv))
                     .Concat(net.Shunts.Select(s => s.BaseKv)).Concat(net.Svcs.Select(s => s.BaseKv)))
        {
            if (kv <= 0 || bvMrid.ContainsKey(kv)) continue;
            string id = "bv_" + Num(kv);
            bvMrid[kv] = id;
            root.Add(new XElement(Cim + "BaseVoltage",
                new XAttribute(Rdf + "ID", "_" + id),
                new XElement(Cim + "BaseVoltage.nominalVoltage", Num(kv))));
        }

        foreach (CimBus bus in net.Buses)
        {
            var node = new XElement(Cim + "TopologicalNode",
                new XAttribute(Rdf + "ID", "_" + bus.MRID),
                new XElement(Cim + "IdentifiedObject.name", bus.Name));
            if (bus.BaseKv > 0 && bvMrid.TryGetValue(bus.BaseKv, out string? bv))
                node.Add(new XElement(Cim + "TopologicalNode.BaseVoltage", new XAttribute(Rdf + "resource", "#_" + bv)));
            root.Add(node);
            // Export pack (2026-10): the bus's starting voltage as its SV SvVoltage, only when the network carries one.
            if (bus.SvVKv is double v)
                root.Add(new XElement(Cim + "SvVoltage",
                    new XAttribute(Rdf + "ID", "_sv_" + bus.MRID),
                    new XElement(Cim + "SvVoltage.v", Num(v)),
                    bus.SvAngleDeg is double a ? new XElement(Cim + "SvVoltage.angle", Num(a)) : null,
                    new XElement(Cim + "SvVoltage.TopologicalNode", new XAttribute(Rdf + "resource", "#_" + bus.MRID))));
        }

        foreach (CimBranch br in net.Branches)
        {
            if (br.Class == "SeriesCompensator")
            {
                // N295 (CIM3): a series compensator, its r and x (no charging), under its own class.
                root.Add(new XElement(Cim + "SeriesCompensator",
                    new XAttribute(Rdf + "ID", "_" + br.MRID),
                    new XElement(Cim + "IdentifiedObject.name", br.Name),
                    new XElement(Cim + "SeriesCompensator.r", Num(br.R)),
                    new XElement(Cim + "SeriesCompensator.x", Num(br.X)),
                    br.BaseKv > 0
                        ? new XElement(Cim + "ConductingEquipment.BaseVoltage", new XAttribute(Rdf + "resource", "#_" + bvMrid[br.BaseKv]))
                        : null));
                root.Add(Terminal($"t_{br.MRID}_1", br.MRID, br.FromNode));
                root.Add(Terminal($"t_{br.MRID}_2", br.MRID, br.ToNode));
                if (br.RateAMva is double sr) AddRating(root, br.MRID, $"t_{br.MRID}_1", sr);
                continue;
            }
            root.Add(new XElement(Cim + "ACLineSegment",
                new XAttribute(Rdf + "ID", "_" + br.MRID),
                new XElement(Cim + "IdentifiedObject.name", br.Name),
                new XElement(Cim + "ACLineSegment.r", Num(br.R)),
                new XElement(Cim + "ACLineSegment.x", Num(br.X)),
                new XElement(Cim + "ACLineSegment.bch", Num(br.B)),
                br.G != 0.0 ? new XElement(Cim + "ACLineSegment.gch", Num(br.G)) : null,   // CIM3: only when present
                br.BaseKv > 0
                    ? new XElement(Cim + "ConductingEquipment.BaseVoltage", new XAttribute(Rdf + "resource", "#_" + bvMrid[br.BaseKv]))
                    : null));
            root.Add(Terminal($"t_{br.MRID}_1", br.MRID, br.FromNode));
            root.Add(Terminal($"t_{br.MRID}_2", br.MRID, br.ToNode));
            // Export pack (2026-10): the rating, only when the network carries one.
            if (br.RateAMva is double r) AddRating(root, br.MRID, $"t_{br.MRID}_1", r);
        }

        foreach (CimLoad load in net.Loads)
        {
            // D-DOC-099: written back under the class it was read from (ConformLoad, NonConformLoad, ...); the
            // p / q keep their inherited "EnergyConsumer." names, as in CGMES. N286: an AsynchronousMachine keeps its
            // RotatingMachine.p / q (load sign, as read, so written as they are).
            string prefix = load.Class == "AsynchronousMachine" ? "RotatingMachine" : "EnergyConsumer";
            root.Add(new XElement(Cim + load.Class,
                new XAttribute(Rdf + "ID", "_" + load.MRID),
                new XElement(Cim + "IdentifiedObject.name", load.Name),
                new XElement(Cim + prefix + ".p", Num(load.P)),
                new XElement(Cim + prefix + ".q", Num(load.Q))));
            root.Add(Terminal($"t_{load.MRID}", load.MRID, load.Node));
        }

        // N286 (CIM2, 2026-10): shunts, written as one section holding the whole admittance in service (so the importer
        // reads back exactly the same G and B: B x 1 and 0 + B are exact), under their own class.
        foreach (CimShunt sh in net.Shunts)
        {
            XElement? bv = sh.BaseKv > 0 && bvMrid.TryGetValue(sh.BaseKv, out string? bvId)
                ? new XElement(Cim + "ConductingEquipment.BaseVoltage", new XAttribute(Rdf + "resource", "#_" + bvId))
                : null;
            var e = new XElement(Cim + sh.Class,
                new XAttribute(Rdf + "ID", "_" + sh.MRID),
                new XElement(Cim + "IdentifiedObject.name", sh.Name), bv);
            if (sh.Class == "EquivalentShunt")
            {
                e.Add(new XElement(Cim + "EquivalentShunt.g", Num(sh.G)), new XElement(Cim + "EquivalentShunt.b", Num(sh.B)));
            }
            else if (sh.Control is CimShuntControl c && sh.Class == "LinearShuntCompensator")
            {
                // N306 (LF3, October 2026): a linear shunt under voltage control (N296) is written with its own sections
                // and per-section admittance and its RegulatingControl (target, deadband, discrete flag, the regulated
                // bus), so a round trip keeps the control. It used to be written as one section holding the whole
                // admittance, which lost the control and the sections. The importer reads B = bPerSection x sections and
                // G = gPerSection x sections, the same products that made sh.B / sh.G, so the admittance is exact too.
                e.Add(c.MaximumSections is double max ? new XElement(Cim + "ShuntCompensator.maximumSections", Num(max)) : null,
                    new XElement(Cim + "ShuntCompensator.normalSections", Num(c.Sections)),
                    new XElement(Cim + "ShuntCompensator.sections", Num(c.Sections)),
                    new XElement(Cim + "LinearShuntCompensator.bPerSection", Num(c.BPerSection)),
                    new XElement(Cim + "LinearShuntCompensator.gPerSection", Num(c.GPerSection)),
                    new XElement(Cim + "RegulatingCondEq.controlEnabled", "true"),
                    new XElement(Cim + "RegulatingCondEq.RegulatingControl", new XAttribute(Rdf + "resource", $"#_rc_{sh.MRID}")));
                root.Add(e);
                root.Add(Terminal($"t_{sh.MRID}", sh.MRID, sh.Node));
                root.Add(RegulatedAt(root, sh.MRID, sh.Node, c.RegulatedNode, c.TargetKv, c.DeadbandKv, c.Discrete));
                continue;
            }
            else
            {
                e.Add(new XElement(Cim + "ShuntCompensator.maximumSections", "1"),
                    new XElement(Cim + "ShuntCompensator.normalSections", "1"),
                    new XElement(Cim + "ShuntCompensator.sections", "1"));
                if (sh.Class == "LinearShuntCompensator")
                    e.Add(new XElement(Cim + "LinearShuntCompensator.bPerSection", Num(sh.B)),
                        new XElement(Cim + "LinearShuntCompensator.gPerSection", Num(sh.G)));
                else
                    root.Add(new XElement(Cim + "NonlinearShuntCompensatorPoint",
                        new XAttribute(Rdf + "ID", $"_nsp_{sh.MRID}_1"),
                        new XElement(Cim + "NonlinearShuntCompensatorPoint.sectionNumber", "1"),
                        new XElement(Cim + "NonlinearShuntCompensatorPoint.b", Num(sh.B)),
                        new XElement(Cim + "NonlinearShuntCompensatorPoint.g", Num(sh.G)),
                        new XElement(Cim + "NonlinearShuntCompensatorPoint.NonlinearShuntCompensator",
                            new XAttribute(Rdf + "resource", "#_" + sh.MRID))));
            }
            root.Add(e);
            root.Add(Terminal($"t_{sh.MRID}", sh.MRID, sh.Node));
        }

        // N295 (CIM3): static var compensators, with their ratings, slope and SSH q as read, and their voltage control
        // (a RegulatingControl at the regulated bus with the target) when they regulate; controlEnabled false otherwise.
        foreach (CimStaticVarCompensator svc in net.Svcs)
        {
            XElement? bv = svc.BaseKv > 0 && bvMrid.TryGetValue(svc.BaseKv, out string? bvId)
                ? new XElement(Cim + "ConductingEquipment.BaseVoltage", new XAttribute(Rdf + "resource", "#_" + bvId))
                : null;
            root.Add(new XElement(Cim + "StaticVarCompensator",
                new XAttribute(Rdf + "ID", "_" + svc.MRID),
                new XElement(Cim + "IdentifiedObject.name", svc.Name), bv,
                svc.CapacitiveRatingOhm is double cap ? new XElement(Cim + "StaticVarCompensator.capacitiveRating", Num(cap)) : null,
                svc.InductiveRatingOhm is double ind ? new XElement(Cim + "StaticVarCompensator.inductiveRating", Num(ind)) : null,
                new XElement(Cim + "StaticVarCompensator.slope", Num(svc.SlopeKvPerMvar)),
                new XElement(Cim + "StaticVarCompensator.q", Num(svc.Q)),
                new XElement(Cim + "RegulatingCondEq.controlEnabled", svc.Regulating ? "true" : "false"),
                svc.Regulating ? new XElement(Cim + "RegulatingCondEq.RegulatingControl", new XAttribute(Rdf + "resource", $"#_rc_{svc.MRID}")) : null));
            root.Add(Terminal($"t_{svc.MRID}", svc.MRID, svc.Node));
            if (svc.Regulating && svc.TargetKv is double target)
                root.Add(RegulatedAt(root, svc.MRID, svc.Node, svc.RegulatedNode ?? svc.Node, target));
        }

        foreach (CimGenerator g in net.Generators)
        {
            if (g.Class != "SynchronousMachine")
            {
                root.Add(Injection(g));
                root.Add(Terminal($"t_{g.MRID}", g.MRID, g.Node));
                if (g.VoltageControl == true && g.Class != "EquivalentInjection")
                    root.Add(VoltageControl(g, g.TargetKv));
                else if (g.RegulatedNode is not null && g.RemoteTargetKv is double remoteKv && g.Class != "EquivalentInjection")
                    root.Add(RegulatedAt(root, g.MRID, g.Node, g.RegulatedNode, remoteKv));
                AddUnits(root, g);
                continue;
            }
            root.Add(new XElement(Cim + "SynchronousMachine",
                new XAttribute(Rdf + "ID", "_" + g.MRID),
                new XElement(Cim + "IdentifiedObject.name", g.Name),
                // R267: CGMES SSH RotatingMachine.p / .q are in the LOAD sign convention (ENTSO-E CGMES 2.4.15,
                // IEC 61970-600-2, SSH profile: "positive sign means flow into the equipment from the network"),
                // while CimGenerator.P / .Q are generator-positive. So both are negated on the way out, exactly
                // as CimImporter negates them on the way in; the round trip is exact (negation is exact in IEEE
                // arithmetic). '0.0 - x' keeps a zero value as +0.0 so it prints as "0", never "-0".
                new XElement(Cim + "RotatingMachine.p", Num(0.0 - g.P)),
                new XElement(Cim + "RotatingMachine.q", Num(0.0 - g.Q)),
                // D-DOC-104: the machine data the bridge uses, each written only when the network carries it, so
                // a network without it exports exactly as before.
                g.RatedSMva is double s ? new XElement(Cim + "RotatingMachine.ratedS", Num(s)) : null,
                g.MinQ is double qmin ? new XElement(Cim + "SynchronousMachine.minQ", Num(qmin)) : null,
                g.MaxQ is double qmax ? new XElement(Cim + "SynchronousMachine.maxQ", Num(qmax)) : null,
                g.ReferencePriority != 0
                    ? new XElement(Cim + "SynchronousMachine.referencePriority", g.ReferencePriority.ToString(CultureInfo.InvariantCulture))
                    : null,
                // N295: a remote-regulating machine (VoltageControl false, with a RegulatedNode) is written with its control
                // enabled and a RegulatingControl at the remote bus, so it reads back as remote control (not as disabled).
                g.RegulatedNode is not null && g.RemoteTargetKv is not null
                    ? new XElement(Cim + "RegulatingCondEq.controlEnabled", "true")
                    : g.VoltageControl is bool vc ? new XElement(Cim + "RegulatingCondEq.controlEnabled", vc ? "true" : "false") : null,
                g.TargetKv is not null || (g.RegulatedNode is not null && g.RemoteTargetKv is not null)
                    ? new XElement(Cim + "RegulatingCondEq.RegulatingControl", new XAttribute(Rdf + "resource", $"#_rc_{g.MRID}"))
                    : null,
                g.MinP is not null || g.MaxP is not null || g.Type.Length > 0
                    ? new XElement(Cim + "RotatingMachine.GeneratingUnit", new XAttribute(Rdf + "resource", $"#_gu_{g.MRID}"))
                    : null));
            root.Add(Terminal($"t_{g.MRID}", g.MRID, g.Node));
            if (g.TargetKv is double target)
                root.Add(VoltageControl(g, target));
            else if (g.RegulatedNode is not null && g.RemoteTargetKv is double remoteTarget)
                root.Add(RegulatedAt(root, g.MRID, g.Node, g.RegulatedNode, remoteTarget));
            if (g.MinP is not null || g.MaxP is not null || g.Type.Length > 0)
            {
                // N254: the unit is written under the GeneratingUnit subclass its type came from (the inverse of
                // CimImporter.UnitType), so the type survives a round trip; with no type it is the plain
                // GeneratingUnit it always was. A fuel is written as a FossilFuel naming the thermal unit.
                (string unitClass, XElement? kind, string? fuel) = UnitClassOf(g.Type);
                root.Add(new XElement(Cim + unitClass,
                    new XAttribute(Rdf + "ID", $"_gu_{g.MRID}"),
                    g.MinP is double pmin ? new XElement(Cim + "GeneratingUnit.minOperatingP", Num(pmin)) : null,
                    g.MaxP is double pmax ? new XElement(Cim + "GeneratingUnit.maxOperatingP", Num(pmax)) : null,
                    kind));
                if (fuel is not null)
                    root.Add(new XElement(Cim + "FossilFuel",
                        new XAttribute(Rdf + "ID", $"_ff_{g.MRID}"),
                        new XElement(Cim + "FossilFuel.fossilFuelType", new XAttribute(Rdf + "resource", Cim.NamespaceName + "FuelType." + fuel)),
                        new XElement(Cim + "FossilFuel.ThermalGeneratingUnit", new XAttribute(Rdf + "resource", $"#_gu_{g.MRID}"))));
            }
            if (g.H is double h)
                root.Add(new XElement(Cim + "SynchronousMachineSimplified",
                    new XAttribute(Rdf + "ID", $"_dy_{g.MRID}"),
                    new XElement(Cim + "SynchronousMachineDynamics.SynchronousMachine", new XAttribute(Rdf + "resource", $"#_{g.MRID}")),
                    new XElement(Cim + "RotatingMachineDynamics.inertia", Num(h))));
        }

        foreach (CimTransformer tx in net.Transformers)
        {
            root.Add(new XElement(Cim + "PowerTransformer",
                new XAttribute(Rdf + "ID", "_" + tx.MRID),
                new XElement(Cim + "IdentifiedObject.name", tx.Name)));

            for (int k = 0; k < tx.Ends.Count; k++)
            {
                CimTransformerEnd e = tx.Ends[k];
                string endId = $"{tx.MRID}_e{k + 1}";
                string termId = $"t_{endId}";
                // The end's terminal (referenced by the end and carrying the node), then the end itself. GR-152:
                // the terminal names the PowerTransformer as its ConductingEquipment, as CGMES requires; without it
                // another tool (powsybl, PowerFactory) saw a transformer with no terminals and left it unconnected. GridSim's
                // own importer reads the end through TransformerEnd.Terminal either way.
                root.Add(Terminal(termId, equipmentMrid: tx.MRID, e.Node));
                root.Add(new XElement(Cim + "PowerTransformerEnd",
                    new XAttribute(Rdf + "ID", "_" + endId),
                    new XElement(Cim + "TransformerEnd.endNumber", (k + 1).ToString(CultureInfo.InvariantCulture)),
                    new XElement(Cim + "PowerTransformerEnd.PowerTransformer", new XAttribute(Rdf + "resource", "#_" + tx.MRID)),
                    new XElement(Cim + "TransformerEnd.Terminal", new XAttribute(Rdf + "resource", "#_" + termId)),
                    new XElement(Cim + "PowerTransformerEnd.r", Num(e.R)),
                    new XElement(Cim + "PowerTransformerEnd.x", Num(e.X)),
                    new XElement(Cim + "PowerTransformerEnd.ratedU", Num(e.RatedU)),
                    // CIM2 (2026-10): the magnetising admittance, only when present (an end without it exports as before).
                    e.G != 0.0 ? new XElement(Cim + "PowerTransformerEnd.g", Num(e.G)) : null,
                    e.B != 0.0 ? new XElement(Cim + "PowerTransformerEnd.b", Num(e.B)) : null));
            }

            // Off-nominal tap / phase referenced to the from end (end 1), the inverse of the importer's algebra:
            // it reads tap = 1 + (step - neutral) * stepVoltageIncrement/100, so neutral=0, step=100*(tap-1),
            // increment=1 reproduces tap when that arithmetic is exact; phase = (step - neutral) * stepPhaseShiftIncrement
            // likewise. Export pack (2026-10): a ratio the linear form cannot give back to the bit is written as a one-point
            // RatioTapChangerTable holding the ratio itself (exact), and every ratio other than exactly 1 is written (one
            // within 1e-12 of 1 used to be dropped).
            // A three-winding transformer carries a tap per winding (R064): write each winding's own changers.
            if (tx.Ends.Count == 3)
            {
                for (int k = 0; k < tx.Ends.Count; k++)
                {
                    CimTransformerEnd e = tx.Ends[k];
                    string endId = $"{tx.MRID}_e{k + 1}";
                    if (e.TapRatio != 1.0)
                        RatioChanger(root, $"rtc_{tx.MRID}_{k + 1}", endId, e.TapRatio);
                    if (Math.Abs(e.PhaseShiftDeg) > 1e-12)
                        root.Add(new XElement(Cim + "PhaseTapChangerLinear",
                            new XAttribute(Rdf + "ID", $"_ptc_{tx.MRID}_{k + 1}"),
                            new XElement(Cim + "PhaseTapChanger.TransformerEnd", new XAttribute(Rdf + "resource", "#_" + endId)),
                            new XElement(Cim + "TapChanger.step", Num(e.PhaseShiftDeg)),
                            new XElement(Cim + "TapChanger.neutralStep", "0"),
                            new XElement(Cim + "PhaseTapChangerLinear.stepPhaseShiftIncrement", "1")));
                }
                continue;
            }

            string fromEndId = $"{tx.MRID}_e1";
            if (tx.Ends.Count > 0 && tx.TapRatio != 1.0)
                RatioChanger(root, "rtc_" + tx.MRID, fromEndId, tx.TapRatio);
            // Export pack (2026-10): the rating, only when the network carries one.
            if (tx.RateAMva is double txRate && tx.Ends.Count == 2) AddRating(root, tx.MRID, $"t_{fromEndId}", txRate);
            if (tx.Ends.Count > 0 && Math.Abs(tx.PhaseShiftDeg) > 1e-12)
                root.Add(new XElement(Cim + "PhaseTapChangerLinear",
                    new XAttribute(Rdf + "ID", "_ptc_" + tx.MRID),
                    new XElement(Cim + "PhaseTapChanger.TransformerEnd", new XAttribute(Rdf + "resource", "#_" + fromEndId)),
                    new XElement(Cim + "TapChanger.step", Num(tx.PhaseShiftDeg)),
                    new XElement(Cim + "TapChanger.neutralStep", "0"),
                    new XElement(Cim + "PhaseTapChangerLinear.stepPhaseShiftIncrement", "1")));
        }

        // N253: the closed switches that join two buses (retained switches) are written as switches of their own
        // class, closed and with their retained flag, between their two TopologicalNodes, so the importer lists them
        // in ClosedSwitches again. A network without them exports exactly as before.
        foreach (CimSwitch sw in net.Couplers)
        {
            root.Add(new XElement(Cim + sw.Class,
                new XAttribute(Rdf + "ID", "_" + sw.MRID),
                new XElement(Cim + "IdentifiedObject.name", sw.Name),
                new XElement(Cim + "Switch.normalOpen", "false"),
                new XElement(Cim + "Switch.open", "false"),
                new XElement(Cim + "Switch.retained", sw.Retained ? "true" : "false")));
            root.Add(Terminal($"t_{sw.MRID}_1", sw.MRID, sw.NodeA));
            root.Add(Terminal($"t_{sw.MRID}_2", sw.MRID, sw.NodeB));
        }

        // Known limitation "CIM export details" (chapter 36): refuse a document that would define one rdf:ID twice.
        RefuseRepeatedIds(root);
        return Serialise(new XDocument(root));
    }

    /// <summary>
    /// Known limitation "CIM export details" (chapter 36): the identifiers the exporter makes up (<c>bv_&lt;kV&gt;</c>,
    /// <c>t_&lt;mRID&gt;_1</c>, <c>rc_&lt;mRID&gt;</c>, <c>&lt;mRID&gt;_e1</c> and the like) can equal a source mRID, and
    /// two source objects can share an mRID. Either way the export used to be written as if nothing were wrong, and only
    /// the re-import refused it (an rdf:ID defined twice in one document is invalid RDF/XML; see
    /// <c>CimImporter.MergeByMrid</c>, N342). The exporter now refuses such a document itself, naming the identifier
    /// and the classes of both elements, so the clash is found where it is made. The test is exact (ordinal): any
    /// document it refuses, the importer would refuse too, so no export that could be read back changes.
    /// </summary>
    private static void RefuseRepeatedIds(XElement root)
    {
        var firstBy = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement el in root.Descendants())
        {
            XAttribute? id = el.Attribute(Rdf + "ID");
            // An rdf:ID that is only the "_" prefix (an object with an empty or blank mRID) names no object for the
            // importer, which skips it rather than refusing a repeat; it is skipped here too, so this check never
            // refuses a document the importer would read.
            if (id is null || id.Value.Trim().Length <= 1) continue;
            if (firstBy.TryGetValue(id.Value, out XElement? first))
                throw new InvalidOperationException(
                    $"CIM export: the rdf:ID '{id.Value}' would be defined twice, first by a {first.Name.LocalName} and then by a " +
                    $"{el.Name.LocalName}. Two objects of the network share an identifier, or an identifier the exporter makes up " +
                    "(bv_<kV>, t_<mRID>_1, rc_<mRID>, <mRID>_e1 and the like) equals another object's mRID, so the document " +
                    "could not be read back (an rdf:ID must be unique within a document). Give the objects mRIDs that do not clash.");
            firstBy[id.Value] = el;
        }
    }

    /// <summary>GR-637: the document as XDocument.ToString() writes it (indented, no XML declaration), but with "\n" as
    /// the newline on every operating system. ToString() indents with Environment.NewLine, so the exported bytes were
    /// CRLF on Windows and LF on Linux for the same network; they are now the same everywhere.</summary>
    private static string Serialise(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        using (XmlWriter w = XmlWriter.Create(sw, settings))
            doc.Root!.WriteTo(w);   // the root alone, as ToString() writes an XDocument's content
        return sw.ToString();
    }

    /// <summary>N254: the CGMES GeneratingUnit subclass for a generator type (the inverse of the importer's
    /// mapping), with the enumeration element it needs and the FossilFuel type to write, if any. A type the importer
    /// does not produce is written as a plain GeneratingUnit (the type is then not carried).</summary>
    private static (string Class, XElement? Kind, string? Fuel) UnitClassOf(string type) => type switch
    {
        "wind-onshore" => ("WindGeneratingUnit", EnumRef("WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.onshore"), null),
        "wind-offshore" => ("WindGeneratingUnit", EnumRef("WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.offshore"), null),
        "wind" => ("WindGeneratingUnit", null, null),
        "solar" => ("SolarGeneratingUnit", null, null),
        "hydro" => ("HydroGeneratingUnit", null, null),
        "pumped-storage" => ("HydroGeneratingUnit",
            EnumRef("HydroGeneratingUnit.energyConversionCapability", "HydroEnergyConversionKind.pumpAndGenerator"), null),
        "nuclear" => ("NuclearGeneratingUnit", null, null),
        "thermal" => ("ThermalGeneratingUnit", null, null),
        "gas" or "coal" or "lignite" or "oil" => ("ThermalGeneratingUnit", null, type),
        _ => ("GeneratingUnit", null, null),
    };

    private static XElement EnumRef(string property, string value) =>
        new(Cim + property, new XAttribute(Rdf + "resource", Cim.NamespaceName + value));

    /// <summary>A local voltage RegulatingControl at the equipment's own terminal with the given target (kV); with no
    /// target (N286: an injection that regulates with no target given) the targetValue is not written.</summary>
    private static XElement VoltageControl(CimGenerator g, double? targetKv) =>
        new(Cim + "RegulatingControl",
            new XAttribute(Rdf + "ID", $"_rc_{g.MRID}"),
            new XElement(Cim + "RegulatingControl.mode",
                new XAttribute(Rdf + "resource", Cim.NamespaceName + "RegulatingControlModeKind.voltage")),
            new XElement(Cim + "RegulatingControl.Terminal", new XAttribute(Rdf + "resource", $"#_t_{g.MRID}")),
            targetKv is double t ? new XElement(Cim + "RegulatingControl.targetValue", Num(t)) : null,
            new XElement(Cim + "RegulatingControl.enabled", "true"));

    /// <summary>
    /// N295 (CIM3, 2026-10): a voltage RegulatingControl "_rc_{mrid}" holding <paramref name="regulatedNode"/> at
    /// <paramref name="targetKv"/>. At the equipment's own node it names the equipment's terminal; at another bus (remote
    /// control) a terminal with no equipment is written on that bus for it to name (the importer maps such a terminal to
    /// its bus), so the regulated bus survives the round trip.
    /// </summary>
    private static XElement RegulatedAt(XElement root, string mrid, string ownNode, string regulatedNode, double targetKv,
        double? deadbandKv = null, bool? discrete = null)
    {
        string terminal = $"t_{mrid}";
        if (!string.Equals(regulatedNode, ownNode, StringComparison.Ordinal))
        {
            terminal = $"t_rc_{mrid}";
            root.Add(Terminal(terminal, equipmentMrid: null, regulatedNode));
        }
        // N306 (LF3): a shunt's control also carries its deadband and discrete flag; for every other caller both are null
        // and nothing more is written (the element is exactly as before).
        return new XElement(Cim + "RegulatingControl",
            new XAttribute(Rdf + "ID", $"_rc_{mrid}"),
            new XElement(Cim + "RegulatingControl.mode",
                new XAttribute(Rdf + "resource", Cim.NamespaceName + "RegulatingControlModeKind.voltage")),
            new XElement(Cim + "RegulatingControl.Terminal", new XAttribute(Rdf + "resource", "#_" + terminal)),
            new XElement(Cim + "RegulatingControl.targetValue", Num(targetKv)),
            deadbandKv is double db ? new XElement(Cim + "RegulatingControl.targetDeadband", Num(db)) : null,
            discrete is bool d ? new XElement(Cim + "RegulatingControl.discrete", d ? "true" : "false") : null,
            new XElement(Cim + "RegulatingControl.enabled", "true"));
    }

    /// <summary>
    /// N286 (CIM2, 2026-10): an ExternalNetworkInjection, EquivalentInjection or PowerElectronicsConnection, the inverse
    /// of the importer's mapping: p / q back in the LOAD sign convention (negated, '0.0 - x' so a zero stays "0"), the
    /// limits under the class's own names, and the voltage control in the class's own terms (a RegulatingControl for
    /// the first and the last, regulationCapability / regulationStatus / regulationTarget for an EquivalentInjection).
    /// Each optional value is written only when present.
    /// </summary>
    private static XElement Injection(CimGenerator g)
    {
        string c = g.Class;
        var e = new XElement(Cim + c,
            new XAttribute(Rdf + "ID", "_" + g.MRID),
            new XElement(Cim + "IdentifiedObject.name", g.Name),
            new XElement(Cim + c + ".p", Num(0.0 - g.P)),
            new XElement(Cim + c + ".q", Num(0.0 - g.Q)));
        if (c == "ExternalNetworkInjection" || c == "EquivalentInjection")
        {
            if (g.MinP is double minP) e.Add(new XElement(Cim + c + ".minP", Num(minP)));
            if (g.MaxP is double maxP) e.Add(new XElement(Cim + c + ".maxP", Num(maxP)));
        }
        if (g.MinQ is double minQ) e.Add(new XElement(Cim + c + ".minQ", Num(minQ)));
        if (g.MaxQ is double maxQ) e.Add(new XElement(Cim + c + ".maxQ", Num(maxQ)));
        if (g.RatedSMva is double s && c == "PowerElectronicsConnection")
            e.Add(new XElement(Cim + "PowerElectronicsConnection.ratedS", Num(s)));
        if (g.ReferencePriority != 0 && c == "ExternalNetworkInjection")
            e.Add(new XElement(Cim + "ExternalNetworkInjection.referencePriority",
                g.ReferencePriority.ToString(CultureInfo.InvariantCulture)));
        if (c == "EquivalentInjection")
        {
            bool regulates = g.VoltageControl == true && g.TargetKv is not null;
            e.Add(new XElement(Cim + "EquivalentInjection.regulationCapability", regulates ? "true" : "false"),
                new XElement(Cim + "EquivalentInjection.regulationStatus", regulates ? "true" : "false"));
            if (g.TargetKv is double t) e.Add(new XElement(Cim + "EquivalentInjection.regulationTarget", Num(t)));
        }
        else
        {
            // An injection read without a RegulatingControl does not regulate (VoltageControl false, no target): it is
            // written with controlEnabled false so that it reads back the same way.
            bool remote = g.RegulatedNode is not null && g.RemoteTargetKv is not null;   // N295: remote control, written enabled
            if (remote) e.Add(new XElement(Cim + "RegulatingCondEq.controlEnabled", "true"));
            else if (g.VoltageControl is bool vc) e.Add(new XElement(Cim + "RegulatingCondEq.controlEnabled", vc ? "true" : "false"));
            if (g.VoltageControl == true || remote)
                e.Add(new XElement(Cim + "RegulatingCondEq.RegulatingControl", new XAttribute(Rdf + "resource", $"#_rc_{g.MRID}")));
        }
        return e;
    }

    /// <summary>N286: the unit behind a PowerElectronicsConnection, by its type ("solar" -> PhotoVoltaicUnit, "wind" ->
    /// PowerElectronicsWindUnit, "battery" -> BatteryUnit), carrying the min / max P, so the type and the limits read back
    /// the same. A connection of no stated kind gets no unit (its min / max P, if any, are then not carried).</summary>
    private static void AddUnits(XElement root, CimGenerator g)
    {
        if (g.Class != "PowerElectronicsConnection") return;
        string? unitClass = g.Type switch
        {
            "solar" => "PhotoVoltaicUnit",
            "wind" => "PowerElectronicsWindUnit",
            "battery" => "BatteryUnit",
            _ => null,
        };
        if (unitClass is null) return;
        root.Add(new XElement(Cim + unitClass,
            new XAttribute(Rdf + "ID", $"_peu_{g.MRID}"),
            new XElement(Cim + "PowerElectronicsUnit.PowerElectronicsConnection", new XAttribute(Rdf + "resource", "#_" + g.MRID)),
            g.MinP is double minP ? new XElement(Cim + "PowerElectronicsUnit.minP", Num(minP)) : null,
            g.MaxP is double maxP ? new XElement(Cim + "PowerElectronicsUnit.maxP", Num(maxP)) : null));
    }

    /// <summary>
    /// Export pack (2026-10): a ratio tap changer "_{id}" on the end "_{endId}" giving <paramref name="ratio"/> exactly: the
    /// linear form (step 100 x (ratio - 1), neutral 0, increment 1) when the importer's 1 + step x 1 / 100 gives the ratio
    /// back to the bit, else a one-point RatioTapChangerTable at step 0 holding the ratio itself.
    /// </summary>
    private static void RatioChanger(XElement root, string id, string endId, double ratio)
    {
        double step = 100.0 * (ratio - 1.0);
        if (BitConverter.DoubleToInt64Bits(1.0 + (step - 0.0) * 1.0 / 100.0) == BitConverter.DoubleToInt64Bits(ratio))
        {
            root.Add(new XElement(Cim + "RatioTapChanger",
                new XAttribute(Rdf + "ID", "_" + id),
                new XElement(Cim + "RatioTapChanger.TransformerEnd", new XAttribute(Rdf + "resource", "#_" + endId)),
                new XElement(Cim + "TapChanger.step", Num(step)),
                new XElement(Cim + "TapChanger.neutralStep", "0"),
                new XElement(Cim + "RatioTapChanger.stepVoltageIncrement", "1")));
            return;
        }
        root.Add(new XElement(Cim + "RatioTapChanger",
            new XAttribute(Rdf + "ID", "_" + id),
            new XElement(Cim + "RatioTapChanger.TransformerEnd", new XAttribute(Rdf + "resource", "#_" + endId)),
            new XElement(Cim + "TapChanger.step", "0"),
            new XElement(Cim + "TapChanger.neutralStep", "0"),
            new XElement(Cim + "RatioTapChanger.RatioTapChangerTable", new XAttribute(Rdf + "resource", $"#_{id}_table"))));
        root.Add(new XElement(Cim + "RatioTapChangerTable",
            new XAttribute(Rdf + "ID", $"_{id}_table")));
        root.Add(new XElement(Cim + "RatioTapChangerTablePoint",
            new XAttribute(Rdf + "ID", $"_{id}_point"),
            new XElement(Cim + "TapChangerTablePoint.step", "0"),
            new XElement(Cim + "TapChangerTablePoint.ratio", Num(ratio)),
            new XElement(Cim + "RatioTapChangerTablePoint.RatioTapChangerTable", new XAttribute(Rdf + "resource", $"#_{id}_table"))));
    }

    /// <summary>
    /// Export pack (2026-10): a permanent apparent-power limit (MVA) on the terminal "_{terminalId}" of the equipment
    /// "_{mrid}": an OperationalLimitSet on that terminal holding one ApparentPowerLimit of the permanent limit type,
    /// written once per document with both the CGMES 2.4.15 limitType (PATL) and the CGMES 3 isInfiniteDuration.
    /// </summary>
    private static void AddRating(XElement root, string mrid, string terminalId, double mva)
    {
        const string typeId = "_olt_patl";
        if (!root.Elements(Cim + "OperationalLimitType").Any(e => (string?)e.Attribute(Rdf + "ID") == typeId))
        {
            if (root.Attribute(XNamespace.Xmlns + "entsoe") is null)
                root.Add(new XAttribute(XNamespace.Xmlns + "entsoe", Entsoe.NamespaceName));
            root.Add(new XElement(Cim + "OperationalLimitType",
                new XAttribute(Rdf + "ID", typeId),
                new XElement(Cim + "IdentifiedObject.name", "PATL"),
                new XElement(Cim + "OperationalLimitType.isInfiniteDuration", "true"),
                new XElement(Entsoe + "OperationalLimitType.limitType",
                    new XAttribute(Rdf + "resource", Entsoe.NamespaceName + "LimitTypeKind.patl"))));
        }
        root.Add(new XElement(Cim + "OperationalLimitSet",
            new XAttribute(Rdf + "ID", $"_ols_{mrid}"),
            new XElement(Cim + "IdentifiedObject.name", "ratings"),
            new XElement(Cim + "OperationalLimitSet.Terminal", new XAttribute(Rdf + "resource", "#_" + terminalId))));
        root.Add(new XElement(Cim + "ApparentPowerLimit",
            new XAttribute(Rdf + "ID", $"_apl_{mrid}"),
            new XElement(Cim + "IdentifiedObject.name", "PATL"),
            new XElement(Cim + "ApparentPowerLimit.value", Num(mva)),
            new XElement(Cim + "OperationalLimit.OperationalLimitSet", new XAttribute(Rdf + "resource", $"#_ols_{mrid}")),
            new XElement(Cim + "OperationalLimit.OperationalLimitType", new XAttribute(Rdf + "resource", "#" + typeId))));
    }

    private static XElement Terminal(string id, string? equipmentMrid, string nodeMrid)
    {
        var t = new XElement(Cim + "Terminal",
            new XAttribute(Rdf + "ID", "_" + id),
            new XElement(Cim + "Terminal.TopologicalNode", new XAttribute(Rdf + "resource", "#_" + nodeMrid)));
        if (equipmentMrid is not null)
            t.Add(new XElement(Cim + "Terminal.ConductingEquipment", new XAttribute(Rdf + "resource", "#_" + equipmentMrid)));
        return t;
    }
}
