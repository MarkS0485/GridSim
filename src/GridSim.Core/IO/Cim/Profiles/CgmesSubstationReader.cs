using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Cim.Profiles;

/// <summary>Options of <see cref="CgmesSubstationReader"/>.</summary>
/// <param name="BaseMva">The system base when the EQ document does not carry GridSim's own (CGMES has none). Default 100 MVA, noted.</param>
/// <param name="Name">The model name when the document does not carry one.</param>
/// <param name="RequireSsh">Refuse (default) when a load, machine or shunt has no SSH set-point; false leaves such values at 0 with a note.</param>
public sealed record CgmesReadOptions(double? BaseMva = null, string? Name = null, bool RequireSsh = true);

/// <summary>What <see cref="CgmesSubstationReader.Read(CgmesArchive, CgmesReadOptions?)"/> found.</summary>
/// <param name="Model">The node-breaker model (switch normal states from EQ).</param>
/// <param name="SwitchStates">The switch states the SSH states (switch id to open); a switch the SSH does not state is absent.</param>
/// <param name="TopologicalNodes">Connectivity node id to the id of its TopologicalNode, when a TP document was read.</param>
/// <param name="Voltages">Solved kV and degrees per TopologicalNode id, when an SV document was read.</param>
/// <param name="Notes">Facts about the reading (classes read approximately, TP / projection agreement, defaults used).</param>
public sealed record CgmesReadResult(SubstationModel Model, IReadOnlyDictionary<string, bool> SwitchStates,
    IReadOnlyDictionary<string, string> TopologicalNodes, IReadOnlyDictionary<string, (double Kv, double AngleDeg)> Voltages,
    IReadOnlyList<string> Notes);

/// <summary>
/// RC4 R4-11 part B: reads CGMES EQ (with TP, SSH and SV when present) into Part A's <see cref="SubstationModel"/>: the
/// node-breaker graph, not a bus-branch reduction. It is the inverse of <see cref="CgmesSubstationExporter"/> and also reads
/// the same classes from other writers: Substation, VoltageLevel, Bay, BaseVoltage, ConnectivityNode, Breaker /
/// Disconnector / LoadBreakSwitch / GroundDisconnector (and the other CIM switch classes, noted), ACLineSegment, two-winding
/// PowerTransformer (ratio and linear phase tap changers), EnergyConsumer and its load subclasses, SynchronousMachine (with its
/// GeneratingUnit limits and voltage RegulatingControl), LinearShuntCompensator, ApparentPowerLimit (PATL). Documents are
/// processed in profile order (EQ, TP, SSH, SV) so the result never depends on the order they are given in.
/// <para>Refused, naming the object: any terminal of conducting equipment the model has no place for (three-winding transformers,
/// series compensators, injections, ...: no silent drop), a switch with no normalOpen, a terminal with no ConnectivityNode, a
/// missing SSH value (unless <see cref="CgmesReadOptions.RequireSsh"/> is off), and everything <see cref="SubstationModel.Problems"/> finds.</para>
/// </summary>
public static class CgmesSubstationReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Obj
    {
        public required string Class;
        public required string Mrid;
        public readonly Dictionary<string, string> P = new(StringComparer.Ordinal);
        public string? V(string n) => P.GetValueOrDefault(n);
        public string Id => Tag("gridsim-id=") ?? Mrid;
        public string? Name => V("IdentifiedObject.name");
        public bool NameAbsent => (V("IdentifiedObject.description") ?? "").Contains(";name-absent", StringComparison.Ordinal);
        private string? Tag(string key)
        {
            string? d = V("IdentifiedObject.description");
            if (d is null || !d.StartsWith(key, StringComparison.Ordinal)) return null;
            string rest = d[key.Length..];
            int semi = rest.IndexOf(';');
            return semi < 0 ? rest : rest[..semi];
        }
        public double? D(string n) => P.TryGetValue(n, out string? s) && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double d) ? d : null;
        public bool? B(string n) => P.TryGetValue(n, out string? s) ? s.Trim().ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => null } : null;
    }

    public static CgmesReadResult Read(CgmesArchive archive, CgmesReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Read(archive.Entries.Where(e => e.Kind is CgmesProfileKind.EQ or CgmesProfileKind.TP or CgmesProfileKind.SSH or CgmesProfileKind.SV)
            .Select(e => (e.Kind, e.Path, e.Text)), options);
    }

    /// <summary>Read from loose documents (kind, name, text).</summary>
    public static CgmesReadResult Read(IEnumerable<(CgmesProfileKind Kind, string Name, string Text)> documents, CgmesReadOptions? options = null)
    {
        options ??= new CgmesReadOptions();
        var notes = new List<string>();
        var refused = new List<string>();
        var objects = new List<Obj>();
        var byMrid = new Dictionary<string, Obj>(StringComparer.Ordinal);
        string? descriptionJson = null;
        bool hasEq = false, hasTp = false, hasSv = false;

        foreach (var doc in documents.OrderBy(d => d.Kind == CgmesProfileKind.Unknown ? int.MaxValue : (int)d.Kind).ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            hasEq |= doc.Kind == CgmesProfileKind.EQ; hasTp |= doc.Kind == CgmesProfileKind.TP; hasSv |= doc.Kind == CgmesProfileKind.SV;
            XDocument x = Parse(doc.Text);
            foreach (XElement e in x.Root!.Elements())
            {
                if (e.Name.LocalName == "FullModel")
                {
                    if (doc.Kind == CgmesProfileKind.EQ)
                    {
                        string? d = e.Elements().FirstOrDefault(c => c.Name.LocalName == "Model.description")?.Value;
                        if (d is not null && d.StartsWith("gridsim:{", StringComparison.Ordinal)) descriptionJson = d["gridsim:".Length..];
                    }
                    continue;
                }
                string? about = Attr(e, "ID") ?? Attr(e, "about");
                if (about is null) continue;
                string mrid = Normalise(about);
                if (!byMrid.TryGetValue(mrid, out Obj? o))
                {
                    o = new Obj { Class = e.Name.LocalName, Mrid = mrid };
                    byMrid[mrid] = o;
                    objects.Add(o);
                }
                foreach (XElement p in e.Elements())
                {
                    string value = Attr(p, "resource") is string res ? Normalise(res, enumValue: true) : p.Value.Trim();
                    o.P.TryAdd(p.Name.LocalName, value);   // the first value of a property wins, in profile order
                }
            }
        }
        if (!hasEq) throw new InvalidDataException("CGMES substation read: no EQ document was given.");

        string modelName = options.Name ?? "cgmes";
        double baseMva = options.BaseMva ?? GridModel.DefaultBaseMva;
        double frequency = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
        bool baseGiven = options.BaseMva is not null;
        if (descriptionJson is not null)
        {
            using JsonDocument j = JsonDocument.Parse(descriptionJson);
            if (j.RootElement.TryGetProperty("name", out JsonElement n) && options.Name is null) modelName = n.GetString() ?? modelName;
            if (j.RootElement.TryGetProperty("baseMva", out JsonElement b) && !baseGiven) { baseMva = b.GetDouble(); baseGiven = true; }
            if (j.RootElement.TryGetProperty("frequencyHz", out JsonElement f)) frequency = f.GetDouble();
        }
        if (!baseGiven) notes.Add($"CGMES carries no system base: the model is read on {baseMva.ToString("R", Inv)} MVA (the default; pass a base to change it).");

        IEnumerable<Obj> OfClass(params string[] c) => objects.Where(o => c.Contains(o.Class));
        Obj? Get(string? mrid) => mrid is not null && byMrid.TryGetValue(mrid, out Obj? o) ? o : null;

        var baseKv = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Obj bv in OfClass("BaseVoltage"))
        {
            if (bv.D("BaseVoltage.nominalVoltage") is double kv && kv > 0) baseKv[bv.Mrid] = kv;
            else refused.Add($"BaseVoltage '{bv.Id}' has no positive BaseVoltage.nominalVoltage.");
        }
        var levels = new Dictionary<string, (Obj O, double Kv, string Sub)>(StringComparer.Ordinal);
        foreach (Obj vl in OfClass("VoltageLevel"))
        {
            if (Get(vl.V("VoltageLevel.BaseVoltage")) is Obj bv && baseKv.TryGetValue(bv.Mrid, out double kv) && vl.V("VoltageLevel.Substation") is string ss)
                levels[vl.Mrid] = (vl, kv, ss);
            else refused.Add($"VoltageLevel '{vl.Id}' needs a BaseVoltage and a Substation.");
        }
        var bays = new Dictionary<string, Obj>(StringComparer.Ordinal);
        foreach (Obj bay in OfClass("Bay")) bays[bay.Mrid] = bay;

        // ---- connectivity nodes: container is a VoltageLevel or a Bay (whose VoltageLevel is then the node's) ----
        var nodeLevel = new Dictionary<string, string>(StringComparer.Ordinal);   // node mrid -> VL mrid
        var nodeBay = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (Obj cn in OfClass("ConnectivityNode"))
        {
            string? c = cn.V("ConnectivityNode.ConnectivityNodeContainer");
            if (c is not null && levels.ContainsKey(c)) { nodeLevel[cn.Mrid] = c; nodeBay[cn.Mrid] = null; }
            else if (c is not null && bays.TryGetValue(c, out Obj? bay) && bay.V("Bay.VoltageLevel") is string bl && levels.ContainsKey(bl)) { nodeLevel[cn.Mrid] = bl; nodeBay[cn.Mrid] = c; }
            else refused.Add($"ConnectivityNode '{cn.Id}' is not in a VoltageLevel or a Bay of one.");
        }

        // ---- terminals by equipment, in sequence order ----
        var terminals = new Dictionary<string, List<(int Seq, Obj T)>>(StringComparer.Ordinal);
        foreach (Obj t in OfClass("Terminal"))
        {
            string? eqm = t.V("Terminal.ConductingEquipment");
            if (eqm is null) { refused.Add($"Terminal '{t.Id}' names no ConductingEquipment."); continue; }
            if (!terminals.TryGetValue(eqm, out var list)) terminals[eqm] = list = new();
            list.Add(((int?)t.D("ACDCTerminal.sequenceNumber") ?? 0, t));
        }
        foreach (var list in terminals.Values) list.Sort((a, b) => a.Seq != b.Seq ? a.Seq.CompareTo(b.Seq) : string.CompareOrdinal(a.T.Mrid, b.T.Mrid));
        string? NodeOf(Obj equipment, int index, string what)
        {
            if (!terminals.TryGetValue(equipment.Mrid, out var list) || list.Count <= index)
            { refused.Add($"{what} '{equipment.Id}' has no terminal {index + 1}."); return null; }
            string? cn = list[index].T.V("Terminal.ConnectivityNode");
            if (cn is null || !nodeLevel.ContainsKey(cn)) { refused.Add($"terminal of {what} '{equipment.Id}' has no ConnectivityNode (an EQ with node-breaker detail is needed)."); return null; }
            return cn;
        }
        string NodeId(string cnMrid) => byMrid[cnMrid].Id;
        double KvOfNode(string cn) => levels[nodeLevel[cn]].Kv;

        // ---- substations ----
        var substationObjs = OfClass("Substation").ToList();
        var subKey = new Dictionary<string, Obj>(StringComparer.Ordinal);
        foreach (Obj s in substationObjs) subKey[s.Mrid] = s;

        var switches = new List<(Obj O, SwitchKind Kind, string From, string? To, string Sub)>();
        var switchClass = new Dictionary<string, (SwitchKind Kind, bool Approx)>(StringComparer.Ordinal)
        {
            ["Breaker"] = (SwitchKind.Breaker, false), ["Disconnector"] = (SwitchKind.Disconnector, false),
            ["LoadBreakSwitch"] = (SwitchKind.LoadBreak, false), ["GroundDisconnector"] = (SwitchKind.EarthSwitch, false),
            ["ProtectedSwitch"] = (SwitchKind.Breaker, true), ["Recloser"] = (SwitchKind.Breaker, true), ["DisconnectingCircuitBreaker"] = (SwitchKind.Breaker, true),
            ["Switch"] = (SwitchKind.Breaker, true), ["Jumper"] = (SwitchKind.Breaker, true), ["Sectionaliser"] = (SwitchKind.Disconnector, true), ["Fuse"] = (SwitchKind.LoadBreak, true),
        };
        var approxClasses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Obj o in objects)
        {
            if (!switchClass.TryGetValue(o.Class, out var sc)) continue;
            if (sc.Approx) approxClasses.Add(o.Class);
            if (o.B("Switch.normalOpen") is null) { refused.Add($"{o.Class} '{o.Id}' has no Switch.normalOpen (no inferred state)."); continue; }
            string? a = NodeOf(o, 0, o.Class);
            string? b = sc.Kind == SwitchKind.EarthSwitch ? null : NodeOf(o, 1, o.Class);
            if (a is null || (sc.Kind != SwitchKind.EarthSwitch && b is null)) continue;
            switches.Add((o, sc.Kind, a, b, levels[nodeLevel[a]].Sub));
        }
        foreach (string c in approxClasses) notes.Add($"CIM switch class {c} is read as a {switchClass[c].Kind} (the model has four switch kinds).");

        var subs = new List<Substation>();
        foreach (Obj s in substationObjs)
        {
            var lv = levels.Where(kv => kv.Value.Sub == s.Mrid).Select(kv => kv.Value).ToList();
            var levelList = objects.Where(o => levels.ContainsKey(o.Mrid) && levels[o.Mrid].Sub == s.Mrid)
                .Select(o => new VoltageLevel(o.Id, levels[o.Mrid].Kv)).ToList();
            var bayList = objects.Where(o => o.Class == "Bay" && o.V("Bay.VoltageLevel") is string v && levels.TryGetValue(v, out var l) && l.Sub == s.Mrid)
                .Select(o => new SubstationBay(o.Id, byMrid[o.V("Bay.VoltageLevel")!].Id, o.NameAbsent ? null : o.Name)).ToList();
            var nodeList = objects.Where(o => nodeLevel.TryGetValue(o.Mrid, out string? l) && levels[l].Sub == s.Mrid)
                .Select(o => new ConnectivityNode(o.Id, byMrid[nodeLevel[o.Mrid]].Id, nodeBay[o.Mrid] is string bm ? byMrid[bm].Id : null)).ToList();
            var swList = switches.Where(w => w.Sub == s.Mrid).Select(w =>
            {
                string? bay = w.O.V("Equipment.EquipmentContainer") is string ec && bays.ContainsKey(ec) ? byMrid[ec].Id : null;
                return new NbSwitch(w.O.Id, w.Kind, NodeId(w.From), w.To is null ? null : NodeId(w.To), w.O.B("Switch.normalOpen")!.Value, bay);
            }).ToList();
            subs.Add(new Substation(s.Id, s.NameAbsent ? null : s.Name, levelList, bayList, nodeList, swList));
        }

        // ---- equipment ----
        var lines = new List<NbLine>();
        var transformers = new List<NbTransformer>();
        var loads = new List<NbLoad>();
        var generators = new List<NbGenerator>();
        var shunts = new List<NbShunt>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (Obj o in switches.Select(s => s.O)) handled.Add(o.Mrid);

        // Rating limits by terminal.
        var ratingOfTerminal = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Obj lim in OfClass("ApparentPowerLimit"))
        {
            double? v = lim.D("ApparentPowerLimit.value") ?? lim.D("ApparentPowerLimit.normalValue");
            Obj? set = Get(lim.V("OperationalLimit.OperationalLimitSet"));
            Obj? type = Get(lim.V("OperationalLimit.OperationalLimitType"));
            if (v is null || set is null) continue;
            bool patl = type is null || (type.Name ?? "").Equals("PATL", StringComparison.OrdinalIgnoreCase)
                || (type.V("OperationalLimitType.limitType") ?? "").EndsWith("patl", StringComparison.OrdinalIgnoreCase)
                || (type.V("OperationalLimitType.direction") ?? "").EndsWith("absoluteValue", StringComparison.OrdinalIgnoreCase) && (type.D("OperationalLimitType.acceptableDuration") ?? 0) == 0;
            if (!patl || set.V("OperationalLimitSet.Terminal") is not string tm) continue;
            ratingOfTerminal.TryAdd(tm, v.Value);
        }
        double Rating(Obj equipment)
        {
            if (!terminals.TryGetValue(equipment.Mrid, out var list)) return 0;
            foreach (var (_, t) in list) if (ratingOfTerminal.TryGetValue(t.Mrid, out double r)) return r;
            return 0;
        }
        string? SshMissing(string what, Obj o) { if (options.RequireSsh) refused.Add($"{what} '{o.Id}' has no SSH value (no SSH profile, or it does not describe it)."); return null; }

        foreach (Obj o in objects)
        {
            switch (o.Class)
            {
                case "ACLineSegment":
                {
                    handled.Add(o.Mrid);
                    string? a = NodeOf(o, 0, "line"), b = NodeOf(o, 1, "line");
                    if (a is null || b is null) break;
                    double kv = KvOfNode(a), zb = kv * kv / baseMva;
                    lines.Add(new NbLine(o.Id, NodeId(a), NodeId(b), (o.D("ACLineSegment.r") ?? 0) / zb, (o.D("ACLineSegment.x") ?? 0) / zb,
                        (o.D("ACLineSegment.bch") ?? 0) * zb, Rating(o)));
                    if (o.D("ACLineSegment.gch") is double gch && gch != 0) notes.Add($"line '{o.Id}': ACLineSegment.gch is not held by the model and is ignored.");
                    break;
                }
                case "PowerTransformer":
                {
                    handled.Add(o.Mrid);
                    var ends = objects.Where(e => e.Class == "PowerTransformerEnd" && e.V("PowerTransformerEnd.PowerTransformer") == o.Mrid)
                        .OrderBy(e => e.D("TransformerEnd.endNumber") ?? 99).ThenBy(e => e.Mrid, StringComparer.Ordinal).ToList();
                    if (ends.Count != 2) { refused.Add($"PowerTransformer '{o.Id}' has {ends.Count} winding(s); only two-winding transformers are read (no silent drop)."); break; }
                    string? hv = NodeOf(o, 0, "transformer"), lv = NodeOf(o, 1, "transformer");
                    if (hv is null || lv is null) break;
                    Obj e1 = ends[0], e2 = ends[1];
                    double u1 = e1.D("PowerTransformerEnd.ratedU") ?? KvOfNode(hv), u2 = e2.D("PowerTransformerEnd.ratedU") ?? KvOfNode(lv);
                    double zb = u1 * u1 / baseMva, k2 = (u1 / u2) * (u1 / u2);
                    double r = ((e1.D("PowerTransformerEnd.r") ?? 0) + (e2.D("PowerTransformerEnd.r") ?? 0) * k2) / zb;
                    double x = ((e1.D("PowerTransformerEnd.x") ?? 0) + (e2.D("PowerTransformerEnd.x") ?? 0) * k2) / zb;
                    double b = ((e1.D("PowerTransformerEnd.b") ?? 0) + (e2.D("PowerTransformerEnd.b") ?? 0) / k2) * zb;
                    if ((e2.D("PowerTransformerEnd.r") ?? 0) != 0 || (e2.D("PowerTransformerEnd.x") ?? 0) != 0)
                        notes.Add($"transformer '{o.Id}': winding 2 impedance referred to winding 1 by (U1/U2)^2.");
                    double ratio = 1.0, shift = 0.0;
                    foreach (Obj tc in objects.Where(t => t.Class is "RatioTapChanger" or "PhaseTapChangerLinear" && (t.V("RatioTapChanger.TransformerEnd") ?? t.V("PhaseTapChanger.TransformerEnd")) is string te && te == e1.Mrid))
                    {
                        double? step = tc.D("TapChanger.step"), neutral = tc.D("TapChanger.neutralStep");
                        if (step is null || neutral is null) { SshMissing("tap changer", tc); continue; }
                        if (tc.Class == "RatioTapChanger" && tc.D("RatioTapChanger.stepVoltageIncrement") is double inc) ratio = 1.0 + ((step.Value - neutral.Value) * inc) / 100.0;
                        else if (tc.Class == "PhaseTapChangerLinear" && tc.D("PhaseTapChangerLinear.stepPhaseShiftIncrement") is double ps) shift = (step.Value - neutral.Value) * ps;
                    }
                    if (objects.Any(t => t.Class is "RatioTapChanger" or "PhaseTapChangerLinear" or "PhaseTapChangerTabular" or "PhaseTapChangerSymmetrical" or "PhaseTapChangerAsymmetrical"
                        && (t.V("RatioTapChanger.TransformerEnd") ?? t.V("PhaseTapChanger.TransformerEnd")) == e2.Mrid))
                        refused.Add($"transformer '{o.Id}' has a tap changer on winding 2; the model carries the tap on the first (HV) winding only.");
                    if (objects.Any(t => t.Class is "PhaseTapChangerTabular" or "PhaseTapChangerSymmetrical" or "PhaseTapChangerAsymmetrical"
                        && t.V("PhaseTapChanger.TransformerEnd") == e1.Mrid))
                        refused.Add($"transformer '{o.Id}' has a phase tap changer of a class other than PhaseTapChangerLinear.");
                    // The rated-voltage ratio, as the bus-branch bridge applies it (CimGridModelBridge): Z above is per unit on
                    // the rated voltage u1, which is the same per-unit value on u2. When a rating differs from its node's base
                    // voltage (a 120 / 10.5 kV unit between 110 kV and 10 kV nodes), the nominal turns ratio is off-nominal by
                    // n1 / n2 on the node bases (n = ratedU / node kV), and the impedance is referred to the LV node base
                    // (x n2^2; the susceptance / n2^2). Applied only when both nodes have a base voltage and a rating differs,
                    // so a unit whose ratings equal its node voltages is unchanged.
                    double vb1 = KvOfNode(hv), vb2 = KvOfNode(lv);
                    if (vb1 > 0 && vb2 > 0 && u1 > 0 && u2 > 0 && (u1 != vb1 || u2 != vb2))
                    {
                        double n1 = u1 / vb1, n2 = u2 / vb2;
                        double refer = n2 * n2;
                        r *= refer;
                        x *= refer;
                        b /= refer;
                        ratio *= n1 / n2;
                        notes.Add(string.Create(CultureInfo.InvariantCulture,
                            $"transformer '{o.Id}': its rated voltages ({u1:R} / {u2:R} kV) differ from its nodes' base voltages ({vb1:R} / {vb2:R} kV), so its nominal ratio is off-nominal by {n1 / n2:R} on the node bases (applied as tap) and its impedance is referred to the LV node base."));
                    }
                    transformers.Add(new NbTransformer(o.Id, NodeId(hv), NodeId(lv), r, x, b, ratio, shift, Rating(o)));
                    break;
                }
                case "EnergyConsumer" or "ConformLoad" or "NonConformLoad" or "StationSupply":
                {
                    handled.Add(o.Mrid);
                    string? n = NodeOf(o, 0, "load");
                    if (n is null) break;
                    double? p = o.D("EnergyConsumer.p"), q = o.D("EnergyConsumer.q");
                    if (p is null) SshMissing("load", o);
                    loads.Add(new NbLoad(o.Id, NodeId(n), p ?? 0, q ?? 0));
                    break;
                }
                case "SynchronousMachine":
                {
                    handled.Add(o.Mrid);
                    string? n = NodeOf(o, 0, "machine");
                    if (n is null) break;
                    double? p = o.D("RotatingMachine.p"), q = o.D("RotatingMachine.q");
                    if (p is null) SshMissing("machine", o);
                    Obj? gu = Get(o.V("RotatingMachine.GeneratingUnit"));
                    Obj? rc = Get(o.V("RegulatingCondEq.RegulatingControl"));
                    double kv = KvOfNode(n), vg = 1.0;
                    if (rc is not null && rc.D("RegulatingControl.targetValue") is double target && (rc.V("RegulatingControl.mode") ?? "").EndsWith("voltage", StringComparison.Ordinal))
                        vg = target / kv;
                    else notes.Add($"machine '{o.Id}' has no voltage RegulatingControl with a target: voltage set-point 1.0 pu.");
                    bool slack = (o.D("SynchronousMachine.referencePriority") ?? 0) > 0;
                    generators.Add(new NbGenerator(o.Id, NodeId(n), 0.0 - (p ?? 0), vg, 0.0 - (q ?? 0),
                        o.D("SynchronousMachine.minQ") ?? double.NegativeInfinity, o.D("SynchronousMachine.maxQ") ?? double.PositiveInfinity,
                        gu?.D("GeneratingUnit.minOperatingP") ?? 0, gu?.D("GeneratingUnit.maxOperatingP") ?? double.PositiveInfinity, slack));
                    break;
                }
                case "LinearShuntCompensator":
                {
                    handled.Add(o.Mrid);
                    string? n = NodeOf(o, 0, "shunt");
                    if (n is null) break;
                    double? sections = o.D("ShuntCompensator.sections");
                    if (sections is null) { SshMissing("shunt", o); sections = 0; }
                    double kv = KvOfNode(n), scale = kv * kv;
                    shunts.Add(new NbShunt(o.Id, NodeId(n), 0.0 + ((o.D("LinearShuntCompensator.gPerSection") ?? 0) * sections.Value) * scale,
                        0.0 + ((o.D("LinearShuntCompensator.bPerSection") ?? 0) * sections.Value) * scale));
                    break;
                }
            }
        }
        // Equipment this model has no place for, by class: a terminal on it proves it is conducting equipment.
        var inert = new HashSet<string>(StringComparer.Ordinal) { "BusbarSection", "Junction" };
        foreach (var (eqMrid, _) in terminals.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (handled.Contains(eqMrid)) continue;
            Obj? o = Get(eqMrid);
            if (o is null) refused.Add($"a Terminal names ConductingEquipment '{eqMrid}', which is not described in any document.");
            else if (inert.Contains(o.Class)) notes.Add($"{o.Class} '{o.Id}' is a marker of a node; ignored.");
            else refused.Add($"{o.Class} '{o.Id}' is not read: the node-breaker model has no such equipment (refused, not dropped).");
        }

        var model = new SubstationModel
        {
            Name = modelName, BaseMva = baseMva, FrequencyHz = frequency,
            Substations = subs, Lines = lines, Transformers = transformers, Loads = loads, Generators = generators, Shunts = shunts,
        };
        if (refused.Count > 0) throw new InvalidDataException("CGMES substation read refused:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", refused.Distinct()));
        IReadOnlyList<string> problems = model.Problems();
        if (problems.Count > 0) throw new InvalidDataException("CGMES substation read: the node-breaker description is inconsistent:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));

        // ---- SSH switch states ----
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var w in switches)
            if (w.O.B("Switch.open") is bool open) states[w.O.Id] = open;

        // ---- TP: which TopologicalNode each connectivity node is in, and whether it agrees with the switch states ----
        var tn = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hasTp)
        {
            foreach (Obj cn in OfClass("ConnectivityNode"))
                if (cn.V("ConnectivityNode.TopologicalNode") is string t && byMrid.TryGetValue(t, out Obj? tnObj)) tn[cn.Id] = tnObj.Id;
            if (states.Count == model.AllSwitches.Count())
            {
                var proj = SubstationProjection.Project(model, states);
                var mine = proj.Groups.Select(g => string.Join("+", g.Nodes.OrderBy(n => n, StringComparer.Ordinal))).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var theirs = tn.GroupBy(kv => kv.Value).Select(g => string.Join("+", g.Select(k => k.Key).OrderBy(n => n, StringComparer.Ordinal))).OrderBy(s => s, StringComparer.Ordinal).ToList();
                if (tn.Count < model.AllNodes.Count()) notes.Add($"TP names a TopologicalNode for {tn.Count} of {model.AllNodes.Count()} connectivity nodes; the TP / switch-state comparison covers those only.");
                var mineCovered = proj.Groups.Select(g => string.Join("+", g.Nodes.Where(n => tn.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal))).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal).ToList();
                notes.Add(mineCovered.SequenceEqual(theirs)
                    ? "TP agrees with the node groups the SSH switch states give (same partition of the connectivity nodes)."
                    : "TP DISAGREES with the SSH switch states: TP groups " + string.Join(" | ", theirs) + "; switch states give " + string.Join(" | ", mineCovered) + ".");
            }
            else notes.Add("TP not compared with the switch states: the SSH does not state every switch.");
        }
        var volt = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if (hasSv)
            foreach (Obj sv in OfClass("SvVoltage"))
                if (Get(sv.V("SvVoltage.TopologicalNode")) is Obj t && sv.D("SvVoltage.v") is double v) volt[t.Id] = (v, sv.D("SvVoltage.angle") ?? 0);
        return new CgmesReadResult(model, states, tn, volt, notes);
    }

    private static string? Attr(XElement e, string local) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == local && a.Name.Namespace.NamespaceName.StartsWith("http://www.w3.org/1999/02/22-rdf", StringComparison.Ordinal))?.Value;

    /// <summary>The mRID an identifier reduces to: no '#', then no "urn:uuid:" or one leading '_'. An enumeration literal keeps its text.</summary>
    private static string Normalise(string s, bool enumValue = false)
    {
        string t = s.Trim();
        if (enumValue && t.Contains('#') && !t.StartsWith('#')) return t[(t.LastIndexOf('#') + 1)..];
        if (t.StartsWith('#')) t = t[1..];
        if (t.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return t["urn:uuid:".Length..].ToLowerInvariant();
        if (t.StartsWith('_')) t = t[1..];
        return t;
    }

    private static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        using var sr = new StringReader(xml);
        using var r = XmlReader.Create(sr, settings);
        return XDocument.Load(r);
    }
}
