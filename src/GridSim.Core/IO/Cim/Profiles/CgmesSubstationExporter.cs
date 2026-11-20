using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>Options of <see cref="CgmesSubstationExporter"/>.</summary>
/// <param name="Voltages">Solved voltage per projected bus id (per unit and degrees). When null no SV document is written.</param>
public sealed record CgmesExportOptions(
    CgmesVersion Version = CgmesVersion.V2_4_15,
    UnspecifiedStatePolicy Unspecified = UnspecifiedStatePolicy.UseNormalState,
    IReadOnlyDictionary<int, (double Vm, double VaDeg)>? Voltages = null,
    string ScenarioTime = "2026-01-01T00:00:00Z");

/// <summary>What <see cref="CgmesSubstationExporter.Export"/> wrote.</summary>
/// <param name="Files">EQ, TP, SSH and (when voltages were given) SV, in that order.</param>
/// <param name="Omissions">Every class and data item the files do not carry, with the reason (structured).</param>
/// <param name="Projection">The projection at the written switch states (what TP and SV describe).</param>
/// <param name="Notes">Values that have no exact CGMES form (see remarks) and other one-line facts.</param>
public sealed record CgmesExportResult(CgmesVersion Version, IReadOnlyList<CgmesProfileFile> Files,
    IReadOnlyList<CgmesOmission> Omissions, SubstationProjectionResult Projection, IReadOnlyList<string> Notes);

/// <summary>
/// RC4 R4-11 part B: writes a node-breaker <see cref="SubstationModel"/> as separate CGMES profile documents: EQ (substations,
/// voltage levels, bays, connectivity nodes, switches, terminals, lines, two-winding transformers, loads, machines, shunts,
/// rating limits), TP (the TopologicalNodes the closed switches make, from <see cref="SubstationProjection"/>), SSH (the switch
/// states, set-points and terminal flags) and, when a solved state is given, SV (SvVoltage per TopologicalNode).
/// <see cref="CgmesSubstationReader"/> reads them back into an identical model.
/// </summary>
/// <remarks>
/// <para>Identity. Every object's native id is kept in IdentifiedObject.description as "gridsim-id=&lt;id&gt;"; the mRID is kind-prefixed
/// (so a node and a line with one id cannot collide) and, in CGMES 3.0, a deterministic UUID of it. Impedances are written in
/// ohms and siemens (per-unit times kV squared over the model base) and set-points in kV and MW, as CGMES holds them; each value is
/// the double the reader's own arithmetic converts back to the model's bits (the search of <c>CgmesWriter</c>), and a value with
/// no such double is counted in the notes. The model's name, base and frequency, which CGMES does not hold, are carried in the EQ
/// md:Model.description as "gridsim:" plus JSON. Machine p and q are in the CGMES load sign (generation negative).</para>
/// </remarks>
public static class CgmesSubstationExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Num(double v) => v.ToString("R", Inv);

    public static CgmesExportResult Export(SubstationModel model, IReadOnlyDictionary<string, bool>? states = null, CgmesExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new CgmesExportOptions();
        model.Validate();
        var problems = new List<string>();
        foreach (Substation s in model.Substations)
            foreach (ConnectivityNode n in s.Nodes)
                if (n.Bay is not null && s.Bays.First(b => b.Id == n.Bay).VoltageLevel != n.VoltageLevel)
                    problems.Add($"node '{n.Id}' is in voltage level '{n.VoltageLevel}' but its bay '{n.Bay}' is in '{s.Bays.First(b => b.Id == n.Bay).VoltageLevel}': CGMES has one container per node.");
        if (problems.Count > 0) throw new SubstationTopologyException(problems);

        SubstationProjectionResult proj = SubstationProjection.Project(model, states ?? new Dictionary<string, bool>(),
            new SubstationProjectionOptions(options.Unspecified));
        var notes = new List<string>();
        var w = new Builder(options.Version);
        int inexact = 0;
        double Exact(double target, double guess, Func<double, double> back)
        {
            if (Same(back(guess), target)) return guess;
            double up = guess, down = guess, best = guess, bestErr = Math.Abs(back(guess) - target);
            for (int k = 0; k < 64; k++)
            {
                up = Math.BitIncrement(up);
                if (Same(back(up), target)) return up;
                down = Math.BitDecrement(down);
                if (Same(back(down), target)) return down;
                double eu = Math.Abs(back(up) - target), ed = Math.Abs(back(down) - target);
                if (eu < bestErr) { best = up; bestErr = eu; }
                if (ed < bestErr) { best = down; bestErr = ed; }
            }
            inexact++;
            return best;
        }

        var kvOfNode = new Dictionary<string, double>(StringComparer.Ordinal);
        var substationOfNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var levelOfNode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Substation s in model.Substations)
            foreach (ConnectivityNode n in s.Nodes)
            {
                kvOfNode[n.Id] = s.VoltageLevels.First(v => v.Id == n.VoltageLevel).NominalKv;
                substationOfNode[n.Id] = s.Id;
                levelOfNode[n.Id] = s.Id + "/" + n.VoltageLevel;
            }
        double baseMva = model.BaseMva;

        // =============================== EQ ===============================================================================
        XElement eq = w.Document(CgmesProfileKind.EQ, model.Name, "GridSim R4-11b equipment profile",
            "gridsim:" + DescriptionJson(model),
            Array.Empty<string>(), options.ScenarioTime);
        var baseVoltages = new List<double>();
        foreach (Substation s in model.Substations)
            foreach (VoltageLevel v in s.VoltageLevels)
                if (!baseVoltages.Contains(v.NominalKv)) baseVoltages.Add(v.NominalKv);
        foreach (double kv in baseVoltages)
            eq.Add(w.Obj("BaseVoltage", "BV", Num(kv), Num(kv), null, ("BaseVoltage.nominalVoltage", Num(kv))));
        eq.Add(w.Obj("GeographicalRegion", "GR", "region", "region", null));
        eq.Add(w.Obj("SubGeographicalRegion", "SGR", "subregion", "subregion", null, w.Ref("SubGeographicalRegion.Region", "GR", "region")));
        bool anyRating = model.Lines.Any(l => l.RateAMva > 0) || model.Transformers.Any(t => t.RateAMva > 0);
        if (anyRating)
            eq.Add(w.Obj("OperationalLimitType", "OLT", "patl", "PATL", null,
                (options.Version == CgmesVersion.V3_0 ? "OperationalLimitType.direction" : "OperationalLimitType.limitType",
                    options.Version == CgmesVersion.V3_0 ? w.Enum("OperationalLimitDirectionKind.absoluteValue") : w.Enum("LimitTypeKind.patl", "http://entsoe.eu/CIM/SchemaExtension/3/1#")),
                ("OperationalLimitType.acceptableDuration", "0")));

        void Terminal(XElement into, string equipKind, string equipId, int seq, string node)
        {
            string tid = equipKind + "." + equipId + "." + seq.ToString(Inv);
            into.Add(w.Obj("Terminal", "T", tid, tid, null,
                w.Ref("Terminal.ConductingEquipment", equipKind, equipId),
                w.Ref("Terminal.ConnectivityNode", "CN", node),
                ("ACDCTerminal.sequenceNumber", seq.ToString(Inv))));
        }

        foreach (Substation s in model.Substations)
        {
            eq.Add(w.Obj("Substation", "SS", s.Id, s.Name ?? s.Id, s.Name is null ? "name-absent" : null, w.Ref("Substation.Region", "SGR", "subregion")));
            foreach (VoltageLevel v in s.VoltageLevels)
                eq.Add(w.ObjK("VoltageLevel", "VL", s.Id + "/" + v.Id, v.Id, v.Id, null, w.Ref("VoltageLevel.Substation", "SS", s.Id), w.Ref("VoltageLevel.BaseVoltage", "BV", Num(v.NominalKv))));
            foreach (SubstationBay b in s.Bays)
                eq.Add(w.ObjK("Bay", "BAY", s.Id + "/" + b.Id, b.Id, b.Name ?? b.Id, b.Name is null ? "name-absent" : null, w.Ref("Bay.VoltageLevel", "VL", s.Id + "/" + b.VoltageLevel)));
            foreach (ConnectivityNode n in s.Nodes)
                eq.Add(w.Obj("ConnectivityNode", "CN", n.Id, n.Id, null,
                    n.Bay is null ? w.Ref("ConnectivityNode.ConnectivityNodeContainer", "VL", s.Id + "/" + n.VoltageLevel) : w.Ref("ConnectivityNode.ConnectivityNodeContainer", "BAY", s.Id + "/" + n.Bay)));
            foreach (NbSwitch sw in s.Switches)
            {
                string cls = sw.Kind switch { SwitchKind.Breaker => "Breaker", SwitchKind.Disconnector => "Disconnector", SwitchKind.EarthSwitch => "GroundDisconnector", _ => "LoadBreakSwitch" };
                string level = s.Nodes.First(n => n.Id == sw.FromNode).VoltageLevel;
                eq.Add(w.Obj(cls, "SW", sw.Id, sw.Id, null,
                    sw.Bay is null ? w.Ref("Equipment.EquipmentContainer", "VL", s.Id + "/" + level) : w.Ref("Equipment.EquipmentContainer", "BAY", s.Id + "/" + sw.Bay),
                    ("Switch.normalOpen", Bool(sw.NormalOpen)), ("Switch.retained", "false")));
                Terminal(eq, "SW", sw.Id, 1, sw.FromNode);
                if (sw.ToNode is not null) Terminal(eq, "SW", sw.Id, 2, sw.ToNode);
            }
        }

        int limitSeq = 0;
        void Rating(string equipId, double mva)
        {
            if (!(mva > 0)) return;
            string set = equipId + ".limits";
            eq.Add(w.Obj("OperationalLimitSet", "OLS", set, set, null, w.Ref("OperationalLimitSet.Terminal", "T", "EQ." + equipId + ".1")));
            string lim = "lim" + (++limitSeq).ToString(Inv);
            eq.Add(w.Obj("ApparentPowerLimit", "APL", equipId, equipId + " PATL", null,
                w.Ref("OperationalLimit.OperationalLimitSet", "OLS", set), w.Ref("OperationalLimit.OperationalLimitType", "OLT", "patl"),
                (options.Version == CgmesVersion.V3_0 ? "ApparentPowerLimit.normalValue" : "ApparentPowerLimit.value", Num(mva))));
        }

        foreach (NbLine l in model.Lines)
        {
            double kv = kvOfNode[l.NodeA], zb = kv * kv / baseMva;
            double r = Exact(l.R, l.R * zb, x => x / zb), x2 = Exact(l.X, l.X * zb, x => x / zb), b = Exact(l.B, l.B / zb, x => x * zb);
            eq.Add(w.Obj("ACLineSegment", "EQ", l.Id, l.Id, null,
                ("ACLineSegment.r", Num(r)), ("ACLineSegment.x", Num(x2)), ("ACLineSegment.bch", Num(b)),
                w.Ref("ConductingEquipment.BaseVoltage", "BV", Num(kv))));
            Terminal(eq, "EQ", l.Id, 1, l.NodeA);
            Terminal(eq, "EQ", l.Id, 2, l.NodeB);
            Rating(l.Id, l.RateAMva);
        }
        foreach (NbTransformer t in model.Transformers)
        {
            double kvH = kvOfNode[t.NodeHv], kvL = kvOfNode[t.NodeLv], zb = kvH * kvH / baseMva;
            double r = Exact(t.R, t.R * zb, x => x / zb), x2 = Exact(t.X, t.X * zb, x => x / zb), b = Exact(t.B, t.B / zb, x => x * zb);
            eq.Add(w.Obj("PowerTransformer", "EQ", t.Id, t.Id, null,
                w.Ref("Equipment.EquipmentContainer", "SS", substationOfNode[t.NodeHv])));
            Terminal(eq, "EQ", t.Id, 1, t.NodeHv);
            Terminal(eq, "EQ", t.Id, 2, t.NodeLv);
            string e1 = t.Id + ".1", e2 = t.Id + ".2";
            eq.Add(w.Obj("PowerTransformerEnd", "PTE", e1, e1, null, w.Ref("PowerTransformerEnd.PowerTransformer", "EQ", t.Id),
                w.Ref("TransformerEnd.Terminal", "T", "EQ." + t.Id + ".1"), ("TransformerEnd.endNumber", "1"),
                ("PowerTransformerEnd.r", Num(r)), ("PowerTransformerEnd.x", Num(x2)), ("PowerTransformerEnd.b", Num(b)), ("PowerTransformerEnd.ratedU", Num(kvH))));
            eq.Add(w.Obj("PowerTransformerEnd", "PTE", e2, e2, null, w.Ref("PowerTransformerEnd.PowerTransformer", "EQ", t.Id),
                w.Ref("TransformerEnd.Terminal", "T", "EQ." + t.Id + ".2"), ("TransformerEnd.endNumber", "2"),
                ("PowerTransformerEnd.r", "0"), ("PowerTransformerEnd.x", "0"), ("PowerTransformerEnd.ratedU", Num(kvL))));
            if (t.TapRatio != 1.0)
                eq.Add(w.Obj("RatioTapChanger", "RTC", t.Id, t.Id + " ratio", null, w.Ref("RatioTapChanger.TransformerEnd", "PTE", e1),
                    ("TapChanger.neutralStep", "0"), ("TapChanger.lowStep", "0"), ("TapChanger.highStep", "1"), ("TapChanger.normalStep", "1"),
                    ("RatioTapChanger.stepVoltageIncrement", Num(Exact(t.TapRatio, (t.TapRatio - 1.0) * 100.0, x => 1.0 + 1.0 * x / 100.0)))));
            if (t.PhaseShiftDeg != 0.0)
                eq.Add(w.Obj("PhaseTapChangerLinear", "PTC", t.Id, t.Id + " phase", null, w.Ref("PhaseTapChanger.TransformerEnd", "PTE", e1),
                    ("TapChanger.neutralStep", "0"), ("TapChanger.lowStep", "0"), ("TapChanger.highStep", "1"), ("TapChanger.normalStep", "1"),
                    ("PhaseTapChangerLinear.stepPhaseShiftIncrement", Num(t.PhaseShiftDeg))));
            Rating(t.Id, t.RateAMva);
        }
        foreach (NbLoad l in model.Loads)
        {
            eq.Add(w.Obj("EnergyConsumer", "EQ", l.Id, l.Id, null, w.Ref("Equipment.EquipmentContainer", "VL", levelOfNode[l.Node])));
            Terminal(eq, "EQ", l.Id, 1, l.Node);
        }
        foreach (NbGenerator g in model.Generators)
        {
            string lvl = levelOfNode[g.Node];
            var unit = new List<(string, string)>();
            if (double.IsFinite(g.Pmin)) unit.Add(("GeneratingUnit.minOperatingP", Num(g.Pmin)));
            if (double.IsFinite(g.Pmax)) unit.Add(("GeneratingUnit.maxOperatingP", Num(g.Pmax)));
            eq.Add(w.Obj("GeneratingUnit", "GU", g.Id, g.Id, null, unit.Cast<object>().ToArray()));
            var machine = new List<object> { w.Ref("RotatingMachine.GeneratingUnit", "GU", g.Id), w.Ref("Equipment.EquipmentContainer", "VL", lvl),
                w.Ref("RegulatingCondEq.RegulatingControl", "RC", g.Id) };
            if (double.IsFinite(g.Qmin)) machine.Add(("SynchronousMachine.minQ", Num(g.Qmin)));
            if (double.IsFinite(g.Qmax)) machine.Add(("SynchronousMachine.maxQ", Num(g.Qmax)));
            eq.Add(w.Obj("SynchronousMachine", "EQ", g.Id, g.Id, null, machine.ToArray()));
            Terminal(eq, "EQ", g.Id, 1, g.Node);
            eq.Add(w.Obj("RegulatingControl", "RC", g.Id, g.Id + " voltage", null,
                ("RegulatingControl.mode", w.Enum("RegulatingControlModeKind.voltage")), w.Ref("RegulatingControl.Terminal", "T", "EQ." + g.Id + ".1")));
        }
        foreach (NbShunt sh in model.Shunts)
        {
            double kv = kvOfNode[sh.Node], scale = kv * kv;
            eq.Add(w.Obj("LinearShuntCompensator", "EQ", sh.Id, sh.Id, null, w.Ref("Equipment.EquipmentContainer", "VL", levelOfNode[sh.Node]),
                ("LinearShuntCompensator.bPerSection", Num(Exact(sh.Bs, sh.Bs / scale, x => 0.0 + x * scale))),
                ("LinearShuntCompensator.gPerSection", Num(Exact(sh.Gs, sh.Gs / scale, x => 0.0 + x * scale))),
                ("ShuntCompensator.maximumSections", "1"), ("ShuntCompensator.nomU", Num(kv))));
            Terminal(eq, "EQ", sh.Id, 1, sh.Node);
        }

        // =============================== TP ===============================================================================
        string eqModel = w.ModelUuid;
        XElement tp = w.Document(CgmesProfileKind.TP, model.Name, "GridSim R4-11b topology profile", null, new[] { eqModel }, options.ScenarioTime);
        var tnOfNode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (NodeGroup g in proj.Groups)
        {
            string rep = g.Nodes[0];
            foreach (string n in g.Nodes) tnOfNode[n] = rep;
            tp.Add(w.Obj("TopologicalNode", "TN", rep, rep, null,
                w.Ref("TopologicalNode.BaseVoltage", "BV", Num(kvOfNode[rep])),
                w.Ref("TopologicalNode.ConnectivityNodeContainer", "VL", levelOfNode[rep])));
        }
        foreach (Substation s in model.Substations)
            foreach (ConnectivityNode n in s.Nodes)
                tp.Add(w.Stub("ConnectivityNode", "CN", n.Id, w.Ref("ConnectivityNode.TopologicalNode", "TN", tnOfNode[n.Id])));
        foreach (TKey t in AllTerminalsInEqOrder(model))
            tp.Add(w.Stub("Terminal", "T", t.Key, w.Ref("Terminal.TopologicalNode", "TN", tnOfNode[t.Node])));

        // =============================== SSH ==============================================================================
        XElement ssh = w.Document(CgmesProfileKind.SSH, model.Name, "GridSim R4-11b steady state hypothesis profile", null, new[] { eqModel }, options.ScenarioTime);
        foreach (Substation s in model.Substations)
            foreach (NbSwitch sw in s.Switches)
            {
                string cls = sw.Kind switch { SwitchKind.Breaker => "Breaker", SwitchKind.Disconnector => "Disconnector", SwitchKind.EarthSwitch => "GroundDisconnector", _ => "LoadBreakSwitch" };
                ssh.Add(w.Stub(cls, "SW", sw.Id, ("Switch.open", Bool(proj.SwitchStates[sw.Id]))));
            }
        foreach (TKey t in AllTerminalsInEqOrder(model))
            ssh.Add(w.Stub("Terminal", "T", t.Key, ("ACDCTerminal.connected", "true")));
        foreach (NbLoad l in model.Loads)
            ssh.Add(w.Stub("EnergyConsumer", "EQ", l.Id, ("EnergyConsumer.p", Num(l.Pd + 0.0)), ("EnergyConsumer.q", Num(l.Qd + 0.0))));
        foreach (NbGenerator g in model.Generators)
        {
            ssh.Add(w.Stub("SynchronousMachine", "EQ", g.Id, ("RotatingMachine.p", Num(0.0 - g.Pg)), ("RotatingMachine.q", Num(0.0 - g.Qg)),
                ("RegulatingCondEq.controlEnabled", "true"), ("SynchronousMachine.referencePriority", g.Slack ? "1" : "0")));
            double kv = kvOfNode[g.Node];
            ssh.Add(w.Stub("RegulatingControl", "RC", g.Id, ("RegulatingControl.enabled", "true"),
                ("RegulatingControl.targetValue", Num(Exact(g.Vg, g.Vg * kv, x => x / kv))), ("RegulatingControl.targetDeadband", "0"), ("RegulatingControl.discrete", "false")));
        }
        foreach (NbShunt sh in model.Shunts)
            ssh.Add(w.Stub("LinearShuntCompensator", "EQ", sh.Id, ("ShuntCompensator.sections", "1")));
        foreach (NbTransformer t in model.Transformers)
        {
            if (t.TapRatio != 1.0) ssh.Add(w.Stub("RatioTapChanger", "RTC", t.Id, ("TapChanger.step", "1")));
            if (t.PhaseShiftDeg != 0.0) ssh.Add(w.Stub("PhaseTapChangerLinear", "PTC", t.Id, ("TapChanger.step", "1")));
        }

        var files = new List<CgmesProfileFile>
        {
            new(CgmesProfileKind.EQ, model.Name + "_EQ.xml", Builder.Serialise(eq)),
            new(CgmesProfileKind.TP, model.Name + "_TP.xml", Builder.Serialise(tp)),
            new(CgmesProfileKind.SSH, model.Name + "_SSH.xml", Builder.Serialise(ssh)),
        };
        // =============================== SV ===============================================================================
        var omissions = new List<CgmesOmission>();
        if (options.Voltages is not null)
        {
            XElement sv = w.Document(CgmesProfileKind.SV, model.Name, "GridSim R4-11b state variables profile", null, new[] { w.ModelUuidOf(CgmesProfileKind.TP), w.ModelUuidOf(CgmesProfileKind.SSH) }, options.ScenarioTime);
            foreach (NodeGroup g in proj.Groups)
            {
                if (g.BusId is not int bus || !options.Voltages.TryGetValue(bus, out var u)) continue;
                double kv = kvOfNode[g.Nodes[0]];
                sv.Add(w.Obj("SvVoltage", "SV", g.Nodes[0], g.Nodes[0], null,
                    ("SvVoltage.v", Num(Exact(u.Vm, u.Vm * kv, x => x / kv))), ("SvVoltage.angle", Num(u.VaDeg)),
                    w.Ref("SvVoltage.TopologicalNode", "TN", g.Nodes[0])));
            }
            files.Add(new CgmesProfileFile(CgmesProfileKind.SV, model.Name + "_SV.xml", Builder.Serialise(sv)));
            omissions.Add(new(CgmesProfileKind.SV, "SvPowerFlow, SvInjection, SvStatus, SvTapStep, SvShuntCompensatorSections", "GridSim holds only bus voltages of a solve here; flows and injections are derivable and are not written."));
        }
        else omissions.Add(new(CgmesProfileKind.SV, "the whole SV profile", "no solved state was supplied (use a solve to write SvVoltage)."));
        omissions.Add(new(CgmesProfileKind.DL, "the whole DL profile", "the substation model holds no diagram layout (deferred in the plan)."));
        omissions.Add(new(CgmesProfileKind.GL, "the whole GL profile", "the substation model holds no geographical coordinates."));
        omissions.Add(new(CgmesProfileKind.SC, "the whole SC profile", "the substation model holds no sequence-network data."));
        omissions.Add(new(CgmesProfileKind.DY, "the whole DY profile", "the substation model holds no dynamic models."));
        omissions.Add(new(CgmesProfileKind.EQ, "RotatingMachine.ratedS, SynchronousMachine.type / operatingMode, reactive capability curves, PowerTransformerEnd.ratedS / connectionKind / grounded, current and voltage limits, LimitTypeKind other than PATL", "not held by NbGenerator / NbTransformer / NbLine."));
        omissions.Add(new(CgmesProfileKind.EQ, "Line, SeriesCompensator, three-winding transformers, EquivalentInjection, ExternalNetworkInjection, BusbarSection, Junction", "the model has no such classes (busbar sections are connectivity nodes)."));
        omissions.Add(new(CgmesProfileKind.EQ, "Equipment.inService / normallyInService", "every written item is in service; the ideal-switch model has no out-of-service equipment."));
        omissions.Add(new(CgmesProfileKind.SSH, "Equipment.inService, Terminal.connected = false, tap steps other than the written step", "terminals are always connected; the tap ratio / phase shift is carried as step 1 of a one-step changer."));
        if (model.Generators.Any(g => double.IsNegativeInfinity(g.Pmin))) omissions.Add(new(CgmesProfileKind.EQ, "GeneratingUnit.minOperatingP (unbounded Pmin)", "an infinite value has no CGMES form; read back as 0 on a re-import."));
        if (inexact > 0)
            notes.Add($"{inexact} value(s) have no CGMES form that the reader converts back to the same bits (ohms / siemens / kV conversion by a base that is not a power of two); each is written as the nearest double.");
        return new CgmesExportResult(options.Version, files, omissions, proj, notes);
    }

    /// <summary>The terminals in the exact order they appear in EQ: per switch (substation order), then lines, transformers, loads, machines, shunts.</summary>
    private sealed record TKey(string Key, string Node);
    private static IEnumerable<TKey> AllTerminalsInEqOrder(SubstationModel m)
    {
        foreach (Substation s in m.Substations)
            foreach (NbSwitch sw in s.Switches)
            {
                yield return new TKey("SW." + sw.Id + ".1", sw.FromNode);
                if (sw.ToNode is not null) yield return new TKey("SW." + sw.Id + ".2", sw.ToNode);
            }
        foreach (NbLine l in m.Lines) { yield return new TKey("EQ." + l.Id + ".1", l.NodeA); yield return new TKey("EQ." + l.Id + ".2", l.NodeB); }
        foreach (NbTransformer t in m.Transformers) { yield return new TKey("EQ." + t.Id + ".1", t.NodeHv); yield return new TKey("EQ." + t.Id + ".2", t.NodeLv); }
        foreach (NbLoad l in m.Loads) yield return new TKey("EQ." + l.Id + ".1", l.Node);
        foreach (NbGenerator g in m.Generators) yield return new TKey("EQ." + g.Id + ".1", g.Node);
        foreach (NbShunt s in m.Shunts) yield return new TKey("EQ." + s.Id + ".1", s.Node);
    }

    private static string DescriptionJson(SubstationModel m)
    {
        using var ms = new MemoryStream();
        using (var jw = new Utf8JsonWriter(ms))
        {
            jw.WriteStartObject();
            jw.WriteString("name", m.Name);
            jw.WriteNumber("baseMva", m.BaseMva);
            jw.WriteNumber("frequencyHz", m.FrequencyHz);
            jw.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Bool(bool b) => b ? "true" : "false";
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>Element construction for one edition.</summary>
    private sealed class Builder
    {
        private readonly CgmesVersion _v;
        private readonly XNamespace _cim, _rdf = CgmesVocabulary.Rdf, _md = CgmesVocabulary.Md;
        private string _modelKey = "";
        public Builder(CgmesVersion v) { _v = v; _cim = CgmesVocabulary.CimNamespace(v); }

        public string ModelUuid => ModelUuidOf(CgmesProfileKind.EQ);
        public string ModelUuidOf(CgmesProfileKind k) => CgmesVocabulary.Uuid($"model/{_modelKey}/{k}");

        private string Mrid(string kind, string id) => _v == CgmesVersion.V3_0 ? "urn:uuid:" + CgmesVocabulary.Uuid(kind + "/" + id) : "_" + kind + "." + id;
        private string RefValue(string kind, string id) => _v == CgmesVersion.V3_0 ? Mrid(kind, id) : "#" + Mrid(kind, id);

        public XElement Document(CgmesProfileKind k, string modelName, string description, string? descriptionOverride, IEnumerable<string> dependsOn, string time)
        {
            _modelKey = _modelKey.Length == 0 ? modelName : _modelKey;
            var root = new XElement(_rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", _rdf.NamespaceName), new XAttribute(XNamespace.Xmlns + "cim", _cim.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "md", _md.NamespaceName));
            var fm = new XElement(_md + "FullModel", new XAttribute(_rdf + "about", "urn:uuid:" + ModelUuidOf(k)),
                new XElement(_md + "Model.scenarioTime", time), new XElement(_md + "Model.created", time),
                new XElement(_md + "Model.description", descriptionOverride ?? description), new XElement(_md + "Model.version", "1"));
            foreach (string d in dependsOn) fm.Add(new XElement(_md + "Model.DependentOn", new XAttribute(_rdf + "resource", "urn:uuid:" + d)));
            foreach (string p in CgmesVocabulary.ProfileUris(_v, k)) fm.Add(new XElement(_md + "Model.profile", p));
            fm.Add(new XElement(_md + "Model.modelingAuthoritySet", "http://gridsim.invalid/r4-11b"));
            root.Add(fm);
            return root;
        }

        /// <summary>A property is (name, text) or a reference built by <see cref="Ref"/> / <see cref="Enum"/>.</summary>
        public XElement Obj(string cls, string kind, string id, string name, string? tag, params object[] props) =>
            ObjK(cls, kind, id, id, name, tag, props);

        /// <summary>As <see cref="Obj"/> with the mRID key apart from the native id (ids scoped to a substation are keyed "substation/id").</summary>
        public XElement ObjK(string cls, string kind, string key, string id, string name, string? tag, params object[] props)
        {
            var e = _v == CgmesVersion.V3_0
                ? new XElement(_cim + cls, new XAttribute(_rdf + "about", Mrid(kind, key)))
                : new XElement(_cim + cls, new XAttribute(_rdf + "ID", Mrid(kind, key)));
            e.Add(new XElement(_cim + "IdentifiedObject.name", name));
            e.Add(new XElement(_cim + "IdentifiedObject.description", "gridsim-id=" + id + (tag is null ? "" : ";" + tag)));
            AddProps(e, props);
            return e;
        }

        /// <summary>A description of an object defined in another profile (rdf:about).</summary>
        public XElement Stub(string cls, string kind, string id, params object[] props)
        {
            var e = new XElement(_cim + cls, new XAttribute(_rdf + "about", RefValue(kind, id)));
            AddProps(e, props);
            return e;
        }

        private void AddProps(XElement e, object[] props)
        {
            foreach (object p in props)
            {
                switch (p)
                {
                    case XElement x: e.Add(x); break;
                    case ValueTuple<string, string> t when t.Item2.StartsWith('\u0001'):
                        e.Add(new XElement(_cim + t.Item1, new XAttribute(_rdf + "resource", t.Item2[1..])));
                        break;
                    case ValueTuple<string, string> t: e.Add(new XElement(_cim + t.Item1, t.Item2)); break;
                    default: throw new InvalidOperationException("bad property");
                }
            }
        }

        public XElement Ref(string prop, string kind, string id) => new(_cim + prop, new XAttribute(_rdf + "resource", RefValue(kind, id)));
        public string Enum(string literal, string? ns = null) => "\u0001" + (ns ?? _cim.NamespaceName) + literal;

        public static string Serialise(XElement root)
        {
            var sb = new StringBuilder();
            var s = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace, OmitXmlDeclaration = true };
            using (var xw = XmlWriter.Create(sb, s)) root.WriteTo(xw);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb + "\n";
        }
    }
}
