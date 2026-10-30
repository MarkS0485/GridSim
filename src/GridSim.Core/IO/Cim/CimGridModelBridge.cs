using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Cim;

/// <summary>
/// Bridges a parsed <see cref="CimNetwork"/> into a solvable <see cref="GridModel"/>: CIM nodes become buses
/// (energy consumers add their load, synchronous machines add generators), AC line segments become branches.
///
/// <para><b>Generators (D-DOC-104).</b> Each machine keeps its P and Q, its voltage set-point (the local
/// RegulatingControl target in kV, per-unitised on its bus's base voltage), its reactive limits (minQ / maxQ),
/// its active limits (the GeneratingUnit's min / maxOperatingP), its base (ratedS) and its inertia H (from the
/// dynamics profile). A bus with a voltage-controlling machine is PV; a bus whose machines all have voltage control
/// off (controlEnabled false, a disabled or non-voltage control, remote regulation) is PQ with their Q injected.</para>
///
/// <para><b>Slack rule (D-DOC-104).</b> The angle reference is chosen from the voltage-controlling machines (all
/// machines when none controls), by, in order: (1) the lowest positive <c>SynchronousMachine.referencePriority</c>
/// (IEC 61970-301 / CGMES: 1 = the highest priority as the power-flow reference, 0 = no preference); (2) the
/// largest unit, by <c>GeneratingUnit.maxOperatingP</c>, else <c>ratedS</c>; (3) the lowest bus number (the CIM
/// node order). The choice and the rule that decided it are written to the notes. It used to be the first
/// generator in document order, always, with no word.</para>
///
/// <para><b>Per-unit (D-DOC-101).</b> Impedances are per-unitised on the way in: a line's ohms against the base
/// voltage of its end nodes (Zbase = kV^2 / MVA); the from node's base, else the to node's, else the line's own
/// ConductingEquipment.BaseVoltage. Two end nodes of different base voltage (N294, CIM3 2026-10) are each per-unit on
/// their own base with the ratio as an off-nominal tap on the line (exact), up to <see cref="LineBaseRatioBound"/>
/// apart; further apart they are refused (two voltage levels, not two names for one). Each transformer winding's
/// ohms are per-unitised against that winding's own ratedU, else the base voltage of the node it connects to. With
/// no base voltage at all, a non-zero impedance is REFUSED: it used to be taken as already per-unit without a word,
/// which for ohm data is wrong by a factor of Zbase. A caller whose CIM network really carries per-unit values
/// says so with <c>unbasedImpedancesArePerUnit: true</c>. A two-winding transformer becomes one tapped branch; a
/// three-winding transformer becomes a star-point bus with one branch per winding, each carrying its own tap.</para>
///
/// <para><b>Closed switches (N253).</b> The buses each <see cref="CimNetwork.ClosedSwitches"/> entry joins (a closed
/// retained switch) are solved as one bus: a closed switch has zero impedance. The joined bus keeps the name and
/// place of the first of them; each join is written to the notes. A line left with both ends on one joined bus is
/// left out (it carries no current) and noted. <b>Generator type (N254):</b> <see cref="CimGenerator.Type"/> becomes
/// <c>Generator.Type</c>.</para>
///
/// <para><b>Transformer ratings and magnetising admittance (CIM2 2026-10).</b> When a winding's rated voltage differs
/// from its bus's base voltage, the nominal turns ratio is off-nominal on the bus bases: a two-winding unit gets tap x
/// n1 / n2 (n = ratedU / bus base) with its impedance referred to the to-bus base (x n2^2); a three-winding winding k gets
/// tap x n_k (the star point's base is winding 1's rating) and the star point starts at 1 / tap of winding 1. The
/// magnetising susceptance <c>PowerTransformerEnd.b</c> is the branch's shunt susceptance (b x ratedU^2 / baseMVA, half
/// at each end); N307 (LF3 2026-10, Ruling R1): the magnetising conductance g is represented the same way, as end-bus
/// shunt conductance placed where the branch puts each half of b (it used to be listed as not represented). Call (Ruling R1), from the ENTSO-E
/// conformity models' published SV: without the rated ratio the MiniGrid solves 4 to 5 % off its SV voltages and with
/// it to 1e-5 pu; the SmallGrid's one transformer with b reproduces its SV only with b split half to each end (the
/// IEEE-118 origin of that model is a pi branch). A transformer whose ratings equal its bus bases and whose b is zero
/// (every transformer in every test before this) bridges bit for bit as before.</para>
///
/// <para><b>Shunts and injections (N286, CIM2 2026-10).</b> Each <see cref="CimNetwork.Shunts"/> entry adds G kV^2 MW and
/// B kV^2 MVAr (at 1.0 pu of its bus's base voltage) to the bus's Gs / Bs. ExternalNetworkInjection, EquivalentInjection
/// and PowerElectronicsConnection generators are generators like any other (the same slack rule); a
/// PowerElectronicsConnection sets <c>Generator.ConverterInterfaced</c>.</para>
///
/// <para><b>De-energised islands (N287, CIM2 2026-10).</b> An island that holds neither the slack nor a
/// voltage-controlling machine (cut off by open switches or disconnected terminals) is left out of the GridModel, its
/// buses named in one note (PSS/E's isolated buses, type 4); it used to make GridModel.Validate refuse the whole model.
/// N296 (CIM3 2026-10): an energised island that does not hold the slack gets its own slack, elected by the same rule
/// within the island (PSS/E's swing bus per island); it used to be kept with none and refused by GridModel.Validate.
/// <see cref="Bridge"/> also returns where each CIM bus went and, after a solve, the flow through each closed retained
/// switch (<see cref="CimBridgeResult.SwitchFlows"/>).</para>
///
/// <para>Fail loud: a load, generator, line or transformer that names a node which is not one of the network's
/// buses throws <see cref="InvalidDataException"/>; it used to be skipped without a word. Topology carries no
/// timestamp, so no horizon guard applies to the model bridge.</para>
/// </summary>
public static partial class CimGridModelBridge
{
    /// <summary>Convert a CIM network to a GridModel.</summary>
    /// <param name="net">The parsed CIM network.</param>
    /// <param name="baseMva">The system base (MVA).</param>
    /// <param name="name">The model name.</param>
    /// <param name="unbasedImpedancesArePerUnit">D-DOC-101: true only when the caller KNOWS that an impedance with
    /// no base voltage anywhere (not on its nodes, not on the line, not as the winding's ratedU) is already in
    /// per-unit on <paramref name="baseMva"/>. False (the default): such an impedance is refused.</param>
    /// <param name="notes">Receives one plain-English line per choice the bridge made that the caller should know:
    /// the slack bus and the rule that chose it, a base voltage taken from the other end, a voltage-controlling bus
    /// with no set-point (held at 1.0 pu). Null = not collected.</param>
    public static GridModel ToGridModel(
        CimNetwork net, double baseMva = 100.0, string name = "cim-import",
        bool unbasedImpedancesArePerUnit = false, ICollection<string>? notes = null) =>
        Bridge(net, baseMva, name, unbasedImpedancesArePerUnit, notes).Model;

    /// <summary>
    /// N287 (CIM2, 2026-10): convert a CIM network to a GridModel, as <see cref="ToGridModel"/> does, and keep what the
    /// conversion knows about where each CIM bus went (<see cref="CimBridgeResult.BusIdOfNode"/>), which buses were left
    /// out as a de-energised island (<see cref="CimBridgeResult.DroppedNodes"/>), and what is needed to report the flow
    /// through each closed retained switch after a solve (<see cref="CimBridgeResult.SwitchFlows"/>). The parameters are
    /// those of <see cref="ToGridModel"/>; the model is the same, bit for bit.
    /// </summary>
    public static CimBridgeResult Bridge(
        CimNetwork net, double baseMva = 100.0, string name = "cim-import",
        bool unbasedImpedancesArePerUnit = false, ICollection<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(net);
        ICollection<string> log = notes ?? new List<string>();
        (CimNetwork live, IReadOnlyList<string> dropped, IReadOnlyList<string> islandSlacks) = DropDeadIslands(net, log);
        var branchNodes = new List<(string From, string? To)>();
        GridModel model = Build(live, baseMva, name, unbasedImpedancesArePerUnit, log, branchNodes, islandSlacks,
            out Dictionary<string, int> busIdOfNode, out CimControls controls);
        return new CimBridgeResult(model, net, live, busIdOfNode, dropped, branchNodes, controls);
    }

    /// <summary>The conversion itself (the body <see cref="ToGridModel"/> always had), on the network that is solved:
    /// <paramref name="branchNodes"/> receives the CIM bus at each end of every GridModel branch (null for a star point),
    /// index-aligned with <see cref="GridModel.Branches"/>, and <paramref name="busIdOfNode"/> every CIM bus's GridModel
    /// bus id.</summary>
    private static GridModel Build(
        CimNetwork net, double baseMva, string name, bool unbasedImpedancesArePerUnit, ICollection<string> log,
        List<(string From, string? To)> branchNodes, IReadOnlyList<string> islandSlacks, out Dictionary<string, int> busIdOfNode, out CimControls cimControls)
    {
        // ---- N253 (CIM1, 2026-10): buses joined by closed (retained) switches -------------------------------
        // A closed switch has zero impedance, so the two buses it joins are one electrical node. GridSim has no
        // zero-impedance branch (GridModel.Validate refuses R = X = 0: the admittance is not finite), and a small
        // made-up reactance would put a voltage drop where there is none, so the buses are solved as ONE bus,
        // which is exact (the way PSS/E treats a zero-impedance line and powsybl's bus view a closed retained
        // switch). Each set of joined CIM buses becomes the GridModel bus of its FIRST member in net.Buses order
        // (its name and position); every member's node maps to it. With no closed switches (every document before
        // N253) each CIM bus is its own GridModel bus, numbered i + 1 exactly as before.
        int cimCount = net.Buses.Count;
        var cimIndex = new Dictionary<string, int>(cimCount);
        for (int i = 0; i < cimCount; i++) cimIndex[net.Buses[i].MRID] = i;
        int[] joinedTo = new int[cimCount];          // union-find over CIM bus indices; a root is its set's first index
        for (int i = 0; i < cimCount; i++) joinedTo[i] = i;
        int Root(int x)
        {
            while (joinedTo[x] != x) x = joinedTo[x] = joinedTo[joinedTo[x]];
            return x;
        }
        foreach (CimSwitch sw in net.Couplers)
        {
            if (!cimIndex.TryGetValue(sw.NodeA, out int a) || !cimIndex.TryGetValue(sw.NodeB, out int b))
                throw new InvalidDataException(
                    $"CIM switch {sw.Name} ({sw.MRID}) joins node '{sw.NodeA}' to node '{sw.NodeB}', and at least one is not one " +
                    "of the network's buses.");
            int ra = Root(a), rb = Root(b);
            if (ra != rb)
            {
                if (ra < rb) joinedTo[rb] = ra;
                else joinedTo[ra] = rb;
            }
            log.Add($"Switch {sw.Name} ({sw.Class}, closed{(sw.Retained ? ", retained" : "")}) joins bus '{net.Buses[a].Name}' " +
                    $"and bus '{net.Buses[b].Name}' at zero impedance; they are solved as one bus.");
        }
        // The GridModel bus (0-based) of each CIM bus: roots numbered in order, members take their root's.
        var gridIndex = new int[cimCount];
        int n = 0;
        for (int i = 0; i < cimCount; i++) gridIndex[i] = Root(i) == i ? n++ : -1;
        for (int i = 0; i < cimCount; i++) if (gridIndex[i] < 0) gridIndex[i] = gridIndex[Root(i)];

        var nodeToId = new Dictionary<string, int>(cimCount);
        var busType = new BusType[n];
        var pd = new double[n];
        var qd = new double[n];
        var busName = new string[n];
        var baseKv = new double[n];
        // Export pack (2026-10): the starting voltage of each bus, from its SvVoltage when the importer was asked to read
        // it (CimImportOptions.StartingVoltages); otherwise 1.0 pu and 0 degrees, the Bus defaults, as before.
        var startVm = new double[n];
        var startVa = new double[n];
        for (int i = 0; i < cimCount; i++)
        {
            int g = gridIndex[i];
            nodeToId[net.Buses[i].MRID] = g + 1; // 1-based bus ids
            if (Root(i) == i)
            {
                busType[g] = BusType.Pq;
                busName[g] = net.Buses[i].Name;
                baseKv[g] = net.Buses[i].BaseKv;
                startVm[g] = 1.0;
                if (net.Buses[i].SvVKv is double svKv)
                {
                    if (!(baseKv[g] > 0))
                        throw new InvalidDataException(
                            $"CIM bus '{busName[g]}': its starting voltage {F(svKv)} kV cannot be put in per-unit, because it has no base voltage.");
                    startVm[g] = svKv / baseKv[g];
                }
                if (net.Buses[i].SvAngleDeg is double svAngle) startVa[g] = svAngle;
            }
            else if (net.Buses[i].BaseKv > 0)
            {
                // A joined bus shares its root's base voltage; a different positive one contradicts the switch.
                if (baseKv[g] > 0 && baseKv[g] != net.Buses[i].BaseKv)
                    throw new InvalidDataException(
                        $"CIM bus '{net.Buses[i].Name}' ({F(net.Buses[i].BaseKv)} kV) is joined by a closed switch to bus " +
                        $"'{busName[g]}' ({F(baseKv[g])} kV); a switch does not change voltage.");
                baseKv[g] = net.Buses[i].BaseKv;
            }
        }

        // Every element must land on a known bus: an unresolved node is an error, never a silent skip.
        int NodeId(string node, string what) => nodeToId.TryGetValue(node, out int id) ? id
            : throw new InvalidDataException($"CIM {what} names node '{node}', which is not one of the network's buses.");

        foreach (CimLoad load in net.Loads)
        {
            int id = NodeId(load.Node, $"load {load.Name}");
            pd[id - 1] += load.P;
            qd[id - 1] += load.Q;
        }

        // ---- N295 (CIM3, 2026-10): static var compensators in a plain solve -----------------------------------
        // A plain solve holds every SVC at its SSH q (load sign, so it is added to the bus's Qd as a load's is), like
        // every other injection whose control GridSim's PV bus cannot express: an SVC's output is a susceptance between
        // its ratings with a droop, not a fixed-voltage source. CimControlledPowerFlow takes this q back out and
        // regulates the SVC exactly (a variable susceptance, see BuildControls). With no SVC every Qd is as before.
        foreach (CimStaticVarCompensator svc in net.Svcs)
        {
            int id = NodeId(svc.Node, $"static var compensator {svc.Name}");
            qd[id - 1] += svc.Q;
            log.Add($"Static var compensator {svc.Name}: a plain solve holds it at its SSH q = {F(svc.Q)} MVAr (load sign) on bus " +
                    $"'{busName[id - 1]}'{(svc.Regulating ? "; CimControlledPowerFlow regulates it" : "; it does not regulate")}.");
        }

        // ---- N286 (CIM2, 2026-10): shunt compensators -> the bus shunt Gs / Bs ------------------------------
        // A shunt's admittance is physical (siemens), so at a bus voltage of V kV it draws G V^2 MW and gives B V^2
        // MVAr. GridSim's Bus.Gs / Bs are those powers at 1.0 pu of the bus's base voltage (the MATPOWER convention:
        // Y_ii += (Gs + j Bs) / baseMVA, positive Bs capacitive, like a positive CIM b), so Gs = G kV^2 and
        // Bs = B kV^2 with the BUS base voltage (the shunt's own nomU only rates it; powsybl likewise applies b in
        // siemens to the bus voltage). With no bus base the shunt's own base (ConductingEquipment.BaseVoltage, else
        // nomU) is used and said; with neither, a non-zero admittance is refused unless the caller declared per-unit
        // data (then G / B are per-unit on baseMva). With no shunts every bus keeps Gs = Bs = 0, as before.
        var gs = new double[n];
        var bs = new double[n];
        double shuntBs = 0.0, shuntGs = 0.0;
        foreach (CimShunt sh in net.Shunts)
        {
            int id = NodeId(sh.Node, $"shunt {sh.Name}");
            double busKv = baseKv[id - 1];
            double scale;   // MVA per siemens at 1.0 pu
            if (busKv > 0)
            {
                scale = busKv * busKv;
            }
            else if (sh.BaseKv > 0)
            {
                scale = sh.BaseKv * sh.BaseKv;
                log.Add($"Shunt {sh.Name}: bus '{busName[id - 1]}' has no base voltage, so the shunt's own {F(sh.BaseKv)} kV is used.");
            }
            else if (unbasedImpedancesArePerUnit || (sh.G == 0 && sh.B == 0))
            {
                scale = baseMva;
            }
            else
            {
                throw new InvalidDataException(
                    $"CIM shunt {sh.Name}: no base voltage on bus '{busName[id - 1]}' or on the shunt itself, so its siemens " +
                    "cannot be put in MVAr. Give the node or the shunt a BaseVoltage, or pass unbasedImpedancesArePerUnit: " +
                    "true if the values are already per-unit.");
            }
            gs[id - 1] += sh.G * scale;
            bs[id - 1] += sh.B * scale;
            shuntGs += sh.G * scale;
            shuntBs += sh.B * scale;
        }
        if (net.Shunts.Count > 0)
            log.Add($"{net.Shunts.Count} shunt(s) add Bs = {F(shuntBs)} MVAr and Gs = {F(shuntGs)} MW (at 1.0 pu of their buses' " +
                    "base voltage) to their buses.");

        // ---- CIM3 (2026-10): line shunt conductance (ACLineSegment.gch) -> the end buses' Gs --------------------
        // A GridSim branch carries a shunt susceptance but no shunt conductance. The pi model puts half of the line's gch
        // at each end, which is exactly a bus shunt conductance there: at a bus voltage of V kV it draws (gch / 2) V^2 MW,
        // so Gs += (gch / 2) kV^2 on that END's own base voltage (exact also for a line between two base voltages, N294:
        // each end's shunt sees its own bus's physical voltage). With no base on an end, the other end's, then the line's
        // own; with none at all a non-zero gch is refused unless the caller declared per-unit data (then gch is per-unit
        // on baseMva). Found by the conformity benchmark: the MicroGrid BE lines carry gch and their published SV balances
        // only with it (3.5 MW on BE-Line_5). A line with gch = 0 (every line before) changes nothing.
        double lineGs = 0.0;
        foreach (CimBranch br in net.Branches)
        {
            if (br.G == 0.0) continue;
            int f = NodeId(br.FromNode, $"line {br.Name}"), t = NodeId(br.ToNode, $"line {br.Name}");
            if (f == t) continue;   // short-circuited by closed switches: left out below, and said there
            double kvF = baseKv[f - 1], kvT = baseKv[t - 1];
            double fallback = kvF > 0 ? kvF : kvT > 0 ? kvT : br.BaseKv;
            double ScaleOf(double kv)
            {
                double k = kv > 0 ? kv : fallback;
                if (k > 0) return k * k;
                if (unbasedImpedancesArePerUnit) return baseMva;
                throw new InvalidDataException(
                    $"CIM line {br.Name}: its conductance gch has no base voltage anywhere (its buses, the line), so its siemens " +
                    "cannot be put in MW. Give the nodes a BaseVoltage, or pass unbasedImpedancesArePerUnit: true if the values are already per-unit.");
            }
            double gf = br.G / 2.0 * ScaleOf(kvF), gt = br.G / 2.0 * ScaleOf(kvT);
            gs[f - 1] += gf;
            gs[t - 1] += gt;
            lineGs += gf + gt;
        }
        if (lineGs != 0.0)
            log.Add($"Line shunt conductance (ACLineSegment.gch): Gs = {F(lineGs)} MW at 1.0 pu added to the lines' end buses (half " +
                    "at each end); a GridSim branch has no shunt conductance of its own.");

        // ---- generators: bus, voltage control, slack (D-DOC-104) ------------------------------------------
        var genBus = new int[net.Generators.Count];
        var controls = new bool[n];   // the bus has at least one voltage-controlling machine
        for (int k = 0; k < net.Generators.Count; k++)
        {
            CimGenerator g = net.Generators[k];
            genBus[k] = NodeId(g.Node, $"generator {g.Name}");
            // VoltageControl null = the document said nothing: regulating, as every machine was before.
            if (g.VoltageControl != false) controls[genBus[k] - 1] = true;
            if (g.H is not null && g.RatedSMva is null)
                throw new InvalidDataException(
                    $"CIM generator {g.Name}: an inertia H = {F(g.H.GetValueOrDefault())} s is given with no ratedS, the base it is on.");
        }
        // Bug hunt 2026-10-29, finding 51 (decided, documented): a machine whose document gives neither
        // RegulatingCondEq.controlEnabled nor a RegulatingControl keeps the D-DOC-104 reading, regulating (powsybl would
        // read it as not regulating). The reading is kept, because it is a recorded decision and every existing import
        // depends on it, but it is now listed by name, so a motor or condenser exported without its control is visible
        // in the log rather than silently held as a PV bus.
        List<string> unspecified = net.Generators.Where(g => g.VoltageControl is null).Select(g => g.Name).ToList();
        if (unspecified.Count > 0)
            log.Add($"{unspecified.Count} machine(s) give neither controlEnabled nor a RegulatingControl and are read as regulating their own bus: " +
                    $"{string.Join(", ", unspecified.Take(20))}{(unspecified.Count > 20 ? ", ..." : "")}.");

        int slackId = -1;
        if (net.Generators.Count > 0)
        {
            // Candidates: the voltage-controlling machines (a reference bus holds its voltage), or every machine
            // when none controls. Scanned in document order with a STRICT improvement test on the key
            // (priority rank, -capacity, bus number), so the result is fully defined whatever the input order.
            bool anyControls = net.Generators.Any(g => g.VoltageControl != false);
            int best = -1;
            for (int k = 0; k < net.Generators.Count; k++)
            {
                if (anyControls && net.Generators[k].VoltageControl == false) continue;
                if (best < 0 || SlackKeyLess(net.Generators[k], genBus[k], net.Generators[best], genBus[best])) best = k;
            }
            CimGenerator chosen = net.Generators[best];
            slackId = genBus[best];
            // Say which rule decided: was another candidate on a different bus level with it on priority?
            bool priorityTie = false, capacityTie = false;
            int distinctBuses = 0;
            var seenBuses = new HashSet<int>();
            for (int k = 0; k < net.Generators.Count; k++)
            {
                if (anyControls && net.Generators[k].VoltageControl == false) continue;
                if (seenBuses.Add(genBus[k])) distinctBuses++;
                if (genBus[k] == slackId) continue;
                if (PriorityRank(net.Generators[k]) == PriorityRank(chosen))
                {
                    priorityTie = true;
                    if (Capacity(net.Generators[k]) == Capacity(chosen)) capacityTie = true;
                }
            }
            string rule = distinctBuses == 1
                ? "the only generator bus"
                : chosen.ReferencePriority > 0 && !priorityTie
                    ? $"SynchronousMachine.referencePriority {chosen.ReferencePriority} (the highest)"
                    : !capacityTie
                        ? $"{(chosen.ReferencePriority > 0 ? $"a tie on referencePriority {chosen.ReferencePriority}" : "no referencePriority")}, " +
                          $"broken by the largest unit ({F(Capacity(chosen))} MW maxOperatingP, else MVA ratedS)"
                        : $"{(chosen.ReferencePriority > 0 ? $"a tie on referencePriority {chosen.ReferencePriority}" : "no referencePriority")} " +
                          $"and on unit size ({F(Capacity(chosen))}), broken by the lowest bus number";
            log.Add($"Slack (angle reference): bus {slackId} '{busName[slackId - 1]}' (machine {chosen.Name}), chosen by {rule}" +
                    $"{(anyControls ? "" : "; no machine controls its voltage, so every machine was a candidate")}.");
        }

        for (int k = 0; k < net.Generators.Count; k++)
        {
            int i = genBus[k] - 1;
            if (genBus[k] == slackId) busType[i] = BusType.Slack;
            else if (controls[i]) busType[i] = BusType.Pv;
            // else: every machine at this bus has voltage control off, so it stays PQ and its Q is injected.
        }
        // N296 (CIM3): the slack of each energised island that is not the main slack's (elected in DropDeadIslands by the
        // same rule, and said there). With one island, none: every bus type is as before.
        if (islandSlacks.Count > 0)
        {
            var islandSlackSet = new HashSet<string>(islandSlacks, StringComparer.Ordinal);
            for (int k = 0; k < net.Generators.Count; k++)
                if (islandSlackSet.Contains(net.Generators[k].MRID)) busType[genBus[k] - 1] = BusType.Slack;
        }
        if (slackId < 0 && n > 0)
        {
            busType[0] = BusType.Slack; // a passive network still needs a reference
            log.Add($"Slack (angle reference): bus 1 '{busName[0]}', because the network has no generator.");
        }

        // Voltage set-points, per bus: every voltage-controlling machine with a target must agree (two different
        // targets at one bus are contradictory, and GridSim holds one voltage per bus: refused). A voltage-
        // controlled bus with no target is held at 1.0 pu, as every bus was before, and listed.
        var vgPu = new double?[n];
        for (int k = 0; k < net.Generators.Count; k++)
        {
            CimGenerator g = net.Generators[k];
            if (g.VoltageControl != true || g.TargetKv is not double targetKv) continue;
            int i = genBus[k] - 1;
            if (!(baseKv[i] > 0))
                throw new InvalidDataException(
                    $"CIM generator {g.Name}: its voltage target {F(targetKv)} kV cannot be put in per-unit, because bus " +
                    $"'{busName[i]}' has no base voltage.");
            double pu = targetKv / baseKv[i];
            if (vgPu[i] is double other && other != pu)
                throw new InvalidDataException(
                    $"CIM bus '{busName[i]}': its machines hold different voltage targets ({F(other)} and {F(pu)} pu); " +
                    "one bus has one voltage.");
            vgPu[i] = pu;
        }
        for (int i = 0; i < n; i++)
            if (busType[i] is BusType.Slack or BusType.Pv && vgPu[i] is null && net.Generators.Count > 0
                && genBus.Contains(i + 1))
                log.Add($"Bus {i + 1} '{busName[i]}' holds its voltage but no machine there gives a target; it is held at 1.0 pu.");

        var generators = new List<Generator>();
        for (int k = 0; k < net.Generators.Count; k++)
        {
            CimGenerator g = net.Generators[k];
            int id = genBus[k];
            // Every machine at a bus carries the bus's set-point (NewtonShell takes one voltage per bus). Absent
            // data keeps the Generator defaults: Vg 1.0, unlimited Q, Pmin 0, unlimited Pmax, H 0. GR-636 (October 2026):
            // a machine with no ratedS takes the SYSTEM base (the MATPOWER convention for "not given", which every
            // per-unit conversion of the model already uses), not a fixed 100 MVA: with a 1000 MVA system base the two
            // differed tenfold. With the default 100 MVA system base nothing changes.
            generators.Add(new Generator(id, Pg: g.P, Qg: g.Q, Vg: vgPu[id - 1] ?? 1.0,
                Qmin: g.MinQ ?? double.NegativeInfinity, Qmax: g.MaxQ ?? double.PositiveInfinity,
                Pmin: g.MinP ?? 0.0, Pmax: g.MaxP ?? double.PositiveInfinity,
                MBase: g.RatedSMva ?? baseMva, H: g.H ?? 0.0, Name: g.Name,
                // N254: the plant type from the CGMES GeneratingUnit class ("" when the document gives none, the
                // Generator default, so a network without unit classes bridges exactly as before).
                Type: g.Type,
                // N286: a CGMES 3.0 PowerElectronicsConnection is converter plant whatever its type; otherwise null,
                // the Generator default (the type decides, as before).
                ConverterInterfaced: g.ConverterPlant ? true : null));
        }

        var buses = new List<Bus>(n);
        for (int i = 0; i < n; i++)
            buses.Add(new Bus(i + 1, busType[i], Pd: pd[i], Qd: qd[i], Gs: gs[i], Bs: bs[i], BaseKv: baseKv[i], Vm: startVm[i], VaDeg: startVa[i], Name: busName[i]));
        int nextId = n + 1;   // star-point buses of three-winding transformers are numbered after the CIM nodes

        var branches = new List<Branch>();
        foreach (CimBranch br in net.Branches)
        {
            int f = NodeId(br.FromNode, $"line {br.Name}"), t = NodeId(br.ToNode, $"line {br.Name}");
            if (f == t && !string.Equals(br.FromNode, br.ToNode, StringComparison.Ordinal))
            {
                // N253: both ends on buses that closed switches join into one: the line is short-circuited by the
                // zero-impedance switch path and carries no current, and a branch from a bus to itself is not a real
                // element (GridModel.Validate refuses a self-loop). Left out, and said. (A line whose two ends name the
                // SAME node is a data error, not a join: it still reaches GridModel.Validate, which refuses it.)
                log.Add($"Line {br.Name}: both its ends are on bus '{busName[f - 1]}' once the closed switches are joined; " +
                        "it carries no current there and is left out.");
                continue;
            }
            // D-DOC-101: Ohm -> per-unit against the base voltage of the line's nodes (Zbase = kV^2 / MVA). The
            // from node's base as before; else the to node's (a line joins two nodes of one voltage); else the
            // line's own ConductingEquipment.BaseVoltage. With no base anywhere, a non-zero impedance is refused
            // unless the caller declared per-unit data; it used to be taken as per-unit without a word.
            //
            // N294 (CIM3, 2026-10, Ruling R1): two DIFFERENT end bases. A line does not change voltage, so two end
            // bases are two names for one physical voltage: CGMES boundary nodes are 400 kV nominal on 380 kV networks
            // and 220 kV nominal on 225 kV ones (the ENTSO-E MicroGrid BE), and powsybl and PowerFactory accept such a
            // line. It used to be refused (D-DOC-101). Now each end is per-unitised on its OWN bus base and the ratio
            // of the two bases is carried as an off-nominal tap on the line: the impedance and the charging are
            // per-unit on the TO bus base and TapRatio = kV(to) / kV(from). This is exact, not an approximation: the
            // tap is the ideal ratio that converts a per-unit voltage on the from base into one on the to base
            // (v_to = v_from x kV(from) / kV(to) at no load, the same physical kV on both sides), and the from-end
            // charging, which the pi model puts behind the tap (stamped / tap^2), sees exactly the physical voltage.
            // Kept refused (D-DOC-101's message) where the call is not physically meaningful: two bases more than
            // 10 % apart (ratio above LineBaseRatioBound = 1.1). Nominal-voltage naming variants sit within about 5 %
            // (380 / 400, 220 / 225 / 230, 110 / 115, 132 / 138, 330 / 345, 500 / 525, 10 / 10.5, 20 / 21), while two
            // distinct voltage levels are 20 % or more apart (110 / 132, 220 / 275, 275 / 400): a line between those is
            // a connectivity error (the line names the wrong bus), not a naming variant, and it is refused.
            double kvFrom = baseKv[f - 1], kvTo = baseKv[t - 1];
            double lineTap = 1.0;
            if (kvFrom > 0 && kvTo > 0 && kvFrom != kvTo)
            {
                double hi = Math.Max(kvFrom, kvTo), lo = Math.Min(kvFrom, kvTo);
                if (hi > LineBaseRatioBound * lo)
                    throw new InvalidDataException(
                        $"CIM line {br.Name} joins bus '{busName[f - 1]}' ({F(kvFrom)} kV) to bus '{busName[t - 1]}' ({F(kvTo)} kV); " +
                        $"a line's two ends have one base voltage. Bases up to {F(LineBaseRatioBound)} times apart are taken " +
                        "as two names for one voltage (N294: each end per-unit on its own base, the ratio as an off-nominal tap); " +
                        $"these are {F(hi / lo)} times apart, which is two different voltage levels: the line names the wrong bus.");
                lineTap = kvTo / kvFrom;
                log.Add($"Line {br.Name}: its ends are on different base voltages ({F(kvFrom)} kV at bus '{busName[f - 1]}', " +
                        $"{F(kvTo)} kV at bus '{busName[t - 1]}'); each end is per-unit on its own base, the impedance on the " +
                        $"{F(kvTo)} kV base and the ratio {F(lineTap)} as an off-nominal tap (exact).");
            }
            double kv = lineTap != 1.0 ? kvTo : kvFrom > 0 ? kvFrom : kvTo > 0 ? kvTo : br.BaseKv > 0 ? br.BaseKv : 0.0;
            if (!(kvFrom > 0) && kv > 0)
                log.Add($"Line {br.Name}: its from bus has no base voltage, so the {(kvTo > 0 ? "to bus's" : "line's own")} " +
                        $"{F(kv)} kV is used to per-unitise it.");
            double zBase;
            if (kv > 0) zBase = kv * kv / baseMva;
            else if (unbasedImpedancesArePerUnit || (br.R == 0 && br.X == 0 && br.B == 0)) zBase = 1.0;
            else
                throw new InvalidDataException(
                    $"CIM line {br.Name}: no base voltage on bus '{busName[f - 1]}', bus '{busName[t - 1]}' or the line " +
                    "itself, so its ohm / siemens values cannot be put in per-unit. Give the nodes a BaseVoltage, or pass " +
                    "unbasedImpedancesArePerUnit: true if the values are already per-unit.");
            double rPu = br.R / zBase, xPu = br.X / zBase, bPu = br.B * zBase;
            // N294: TapRatio is 1.0 (the Branch default, so a line on one base voltage is the record it always was)
            // unless the two ends are on different bases.
            // Export pack: the line's rating when the importer read one (CimImportOptions.ApparentPowerRatings); else 0.0, as before.
            branches.Add(new Branch(f, t, rPu, xPu, bPu, TapRatio: lineTap, RateAMva: br.RateAMva ?? 0.0, Name: br.Name));
            branchNodes.Add((br.FromNode, br.ToNode));
        }

        // A winding's per-unit impedance on its OWN ratedU (per-unit impedance is invariant across the ideal turns
        // ratio). D-DOC-101: with no ratedU, the base voltage of the node it connects to; with neither, a non-zero
        // impedance is refused unless the caller declared per-unit data (it used to be taken as per-unit).
        double WindingKv(CimTransformer tx, CimTransformerEnd e, int bus)
        {
            if (e.RatedU > 0) return e.RatedU;
            double kv = baseKv[bus - 1];
            if (kv > 0)
                log.Add($"Transformer {tx.Name} winding {e.EndNumber}: no ratedU, so its node's base voltage {F(kv)} kV is used.");
            return kv;
        }
        double WindingPu(CimTransformer tx, CimTransformerEnd e, double ohm, double kv)
        {
            if (kv > 0) return ohm / (kv * kv / baseMva);
            if (unbasedImpedancesArePerUnit || ohm == 0) return ohm;
            throw new InvalidDataException(
                $"CIM transformer {tx.Name} winding {e.EndNumber}: no ratedU and no base voltage on its node, so its ohm " +
                "values cannot be put in per-unit. Give the end a ratedU, or pass unbasedImpedancesArePerUnit: true if " +
                "the values are already per-unit.");
        }

        // N307 (LF3, October 2026; ruled by main under Ruling R1, Mark delegated the EE calls 2026-10-04): a transformer's
        // magnetising CONDUCTANCE (PowerTransformerEnd.g, the iron loss) is now REPRESENTED, half at each end exactly like
        // its magnetising susceptance b. A GridSim branch carries a shunt susceptance only, so the conductance goes on the
        // end buses as bus shunt conductance Gs, placed where the branch's pi model puts each half of b: the to-end half as
        // it is, the from-end half divided by tap^2 (AdmittanceStamp: Yff = (ys + jb/2) / a^2, Ytt = ys + jb/2), so the
        // pair is the branch's own shunt admittance g/2 + jb/2 at each end, bit for bit the place b has. PSS/E (MAG1) and
        // PowerFactory carry the iron loss; before, CIM2 listed it as not represented (CIM2 / CIM3 call 10). A unit with
        // g = 0 on every end (every transformer in every test before this) adds nothing: no Gs, no note. The note keeps
        // its prefix and gives the MW at rated voltage (g x ratedU^2).
        void MagnetisingConductanceNote(CimTransformer tx, (CimTransformerEnd E, double Kv)[] ends)
        {
            if (ends.All(x => x.E.G == 0.0)) return;
            double mw = 0.0;
            foreach ((CimTransformerEnd e, double kv) in ends) mw += e.G * kv * kv;
            log.Add($"Transformer {tx.Name}: its magnetising conductance (PowerTransformerEnd.g, {F(mw)} MW of iron loss at " +
                    "rated voltage) is represented as bus shunt conductance, half at each end where its branch puts each half " +
                    "of its susceptance (the from-end half through the tap).");
        }

        // N307: add a branch's magnetising conductance (per-unit on the system base, the same scale as its b) to its end
        // buses' Gs (MW at 1.0 pu), half at each end, the from-end half through the tap as the branch stamps b. A tap of 0
        // or less is read as 1.0, as AdmittanceStamp reads it.
        void AddMagnetisingConductance(int from, int to, double gPu, double tapRatio)
        {
            if (gPu == 0.0) return;
            double a = tapRatio <= 0 ? 1.0 : tapRatio;
            double half = gPu / 2.0 * baseMva;
            buses[from - 1] = buses[from - 1] with { Gs = buses[from - 1].Gs + half / (a * a) };
            buses[to - 1] = buses[to - 1] with { Gs = buses[to - 1].Gs + half };
        }

        // Transformers.
        foreach (CimTransformer tx in net.Transformers)
        {
            if (tx.Ends.Count == 2)
            {
                // Two-winding: the two per-unit impedances simply add (no referral factor); the off-nominal tap /
                // phase come from the tap changers, referred to the from side.
                CimTransformerEnd e1 = tx.Ends[0], e2 = tx.Ends[1];
                int f = NodeId(e1.Node, $"transformer {tx.Name}"), t = NodeId(e2.Node, $"transformer {tx.Name}");
                double kv1 = WindingKv(tx, e1, f), kv2 = WindingKv(tx, e2, t);
                double rPu = WindingPu(tx, e1, e1.R, kv1) + WindingPu(tx, e2, e2.R, kv2);
                double xPu = WindingPu(tx, e1, e1.X, kv1) + WindingPu(tx, e2, e2.X, kv2);
                double tap = tx.TapRatio;
                // CIM2 (2026-10): the magnetising susceptance (siemens at each end's rating; S x kV^2 = MVAr) on the
                // branch as its shunt susceptance, half at each end (see the class summary for the call). 0.0 when
                // the ends give none, which is the literal every transformer had before.
                double bPu = e1.B != 0.0 || e2.B != 0.0 ? (e1.B * kv1 * kv1 + e2.B * kv2 * kv2) / baseMva : 0.0;
                // N307: the magnetising conductance on the same scale and by the same expression as b (0.0 when none).
                double gPu = e1.G != 0.0 || e2.G != 0.0 ? (e1.G * kv1 * kv1 + e2.G * kv2 * kv2) / baseMva : 0.0;
                MagnetisingConductanceNote(tx, new[] { (e1, kv1), (e2, kv2) });
                // CIM2 (2026-10): the rated-voltage ratio. GridSim per-unitises each winding on its OWN rating (ratedU)
                // and its buses on their base voltages; when the two differ (a 120 / 10.5 kV unit between 110 kV and
                // 10 kV buses), the nominal turns ratio is off-nominal on the bus bases by n1 / n2 (n = ratedU / bus
                // base kV), and the impedance, on the to-side rating, must be referred to the to-bus base (x n2^2; the
                // susceptance / n2^2). This is the MATPOWER / PSS/E conversion (from-side tap, impedance on the to side)
                // and what powsybl and PowerFactory do with the rated voltages. It used to be ignored, so a unit whose
                // rating differs from its bus voltages was solved at the wrong ratio (the ENTSO-E MiniGrid's solved
                // voltages were 4 to 5 % off; with it they match the published SV to 1e-5 pu; see the CIM2 notes).
                // Applied only when a rating differs from its bus base and both buses have one, so every transformer
                // whose ratings equal its bus bases (every one before this change, in every test) is bit for bit as before.
                double vb1 = baseKv[f - 1], vb2 = baseKv[t - 1];
                if (vb1 > 0 && vb2 > 0 && kv1 > 0 && kv2 > 0 && (kv1 != vb1 || kv2 != vb2))
                {
                    double n1 = kv1 / vb1, n2 = kv2 / vb2;
                    double refer = n2 * n2;
                    rPu *= refer;
                    xPu *= refer;
                    bPu /= refer;
                    gPu /= refer;   // N307: referred to the to-bus base exactly as b
                    tap *= n1 / n2;
                    log.Add($"Transformer {tx.Name}: its rated voltages ({F(kv1)} / {F(kv2)} kV) differ from its buses' base " +
                            $"voltages ({F(vb1)} / {F(vb2)} kV), so its nominal ratio is off-nominal by {F(n1 / n2)} on the bus " +
                            "bases (applied as tap) and its impedance is referred to the to-bus base.");
                }
                else if (kv1 > 0 && kv2 > 0 && (!(vb1 > 0) || !(vb2 > 0)) && (kv1 != vb1 || kv2 != vb2))
                {
                    // Bug hunt 2026-10-29, finding 50: the correction above needs both bus bases. When a rating differs
                    // from its bus base but one bus has no base voltage, the unit is solved at its uncorrected ratio; that
                    // used to happen with nothing in the log, unlike every other fallback here. It is listed now (the
                    // numbers are unchanged).
                    log.Add($"Transformer {tx.Name}: its rated voltages ({F(kv1)} / {F(kv2)} kV) differ from its buses' base " +
                            $"voltages ({F(vb1)} / {F(vb2)} kV), but a bus has no base voltage, so the off-nominal ratio and the " +
                            "impedance referral are NOT applied; the unit is solved at its rated ratio on its own ratings.");
                }
                branches.Add(new Branch(f, t, rPu, xPu, bPu,
                    TapRatio: tap, PhaseShiftDeg: tx.PhaseShiftDeg, RateAMva: tx.RateAMva ?? 0.0, Name: tx.Name, IsTransformer: true));
                branchNodes.Add((e1.Node, e2.Node));
                AddMagnetisingConductance(f, t, gPu, tap);
            }
            else if (tx.Ends.Count == 3)
            {
                // Three-winding (R064): CGMES gives each winding's star-equivalent impedance, so the unit is a
                // star-point bus with one branch per winding (winding node -> star), each per-unitised on its own
                // ratedU and carrying that winding's own tap. The star bus is PQ with no load; its base kV is the
                // first winding's rating (or its node's base, D-DOC-101). (The tertiary used to be dropped.)
                int star = nextId++;
                int w1 = NodeId(tx.Ends[0].Node, $"transformer {tx.Name} winding {tx.Ends[0].EndNumber}");
                // CIM2 (2026-10): the rated-voltage ratio, as for two windings. The star point's base is the first
                // winding's rating, so winding k's nominal ratio is off-nominal by n_k = ratedU_k / its bus base, applied
                // as that branch's tap; its impedance, per-unit on its own rating, is already on the star base referred
                // through the rated ratio, so it is unchanged (and so is its susceptance). The star point then starts
                // (initial Vm only) at 1 / tap of winding 1, the no-load voltage a 1.0 pu winding-1 bus gives it: from
                // the flat 1.0 a near-zero winding impedance with an off-nominal tap (the MiniGrid's T3 / T4) drives a
                // mismatch of hundreds of pu and Newton does not converge (checked in the independent CIM2 solve). Only
                // when a rating differs from its bus base: otherwise every number, and the start, is as before.
                var rows = new List<(CimTransformerEnd E, int W, double Kv, double Tap)>(tx.Ends.Count);
                bool rated = false;
                foreach (CimTransformerEnd e in tx.Ends)
                {
                    int w = NodeId(e.Node, $"transformer {tx.Name} winding {e.EndNumber}");
                    double kv = WindingKv(tx, e, w);
                    double tap = e.TapRatio;
                    double vb = baseKv[w - 1];
                    if (vb > 0 && kv > 0 && kv != vb)
                    {
                        tap *= kv / vb;
                        rated = true;
                    }
                    rows.Add((e, w, kv, tap));
                }
                if (rated)
                    log.Add($"Transformer {tx.Name}: the rated voltages of its windings ({string.Join(" / ", rows.Select(r => F(r.Kv)))} kV) " +
                            $"differ from their buses' base voltages ({string.Join(" / ", rows.Select(r => F(baseKv[r.W - 1])))} kV), so " +
                            "each winding's nominal ratio is applied as its off-nominal tap.");
                buses.Add(new Bus(star, BusType.Pq, BaseKv: tx.Ends[0].RatedU > 0 ? tx.Ends[0].RatedU : baseKv[w1 - 1],
                    Vm: rated ? 1.0 / rows[0].Tap : 1.0, Name: $"{tx.Name} star point"));
                MagnetisingConductanceNote(tx, rows.Select(r => (r.E, r.Kv)).ToArray());
                foreach ((CimTransformerEnd e, int w, double kv, double tap) in rows)
                {
                    // CIM2: this winding's magnetising susceptance on its branch (0.0 when none, as before).
                    double bPu = e.B != 0.0 ? e.B * kv * kv / baseMva : 0.0;
                    branches.Add(new Branch(w, star, WindingPu(tx, e, e.R, kv), WindingPu(tx, e, e.X, kv), bPu,
                        TapRatio: tap, PhaseShiftDeg: e.PhaseShiftDeg,
                        Name: $"{tx.Name}/w{e.EndNumber}", IsTransformer: true));
                    branchNodes.Add((e.Node, null));   // the far end is the star point, not a CIM bus
                    // N307: this winding's magnetising conductance, half at each end of its branch as its b.
                    AddMagnetisingConductance(w, star, e.G != 0.0 ? e.G * kv * kv / baseMva : 0.0, tap);
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"CIM transformer {tx.Name} has {tx.Ends.Count} winding(s); only two- and three-winding units are supported.");
            }
        }

        busIdOfNode = nodeToId;
        cimControls = BuildControls(net, baseMva, log, nodeToId, genBus, busType, controls, baseKv, busName);
        var model = new GridModel
        {
            BaseMva = baseMva,
            Buses = buses,
            Generators = generators,
            Branches = branches,
            Name = name,
        };
        // GR-150: the same post-build check every other loader applies (JSON, MATPOWER): a hard structural fault, such
        // as a line whose two terminals are on one node (a self-loop) or a zero-impedance line, is refused here, at the
        // import point, instead of reaching a solver that would stamp it.
        var errors = model.Validate()
            .Where(d => d.Severity == ValidationSeverity.Error)
            .Select(d => d.Message)
            .ToList();
        if (errors.Count > 0)
            throw new InvalidDataException($"CIM model '{name}' is invalid:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", errors));
        return model;
    }

    /// <summary>
    /// N294 (CIM3, 2026-10): the largest ratio of the two end base voltages of one line that the bridge takes as two names
    /// for one physical voltage (and models exactly, with the ratio as an off-nominal tap). Above it the line is refused
    /// (D-DOC-101): the bases are two different voltage levels. 1.1: nominal-voltage naming variants are within about 5 %
    /// (380 / 400 kV is 1.053), distinct levels 20 % or more apart (110 / 132 kV is 1.2).
    /// </summary>
    public const double LineBaseRatioBound = 1.1;

    // The slack key, smaller is better: the referencePriority rank (1, 2, ... first; 0 = no preference last),
    // then the larger unit, then the lower bus number. Equal keys (same bus) keep the earlier machine.
    private static bool SlackKeyLess(CimGenerator a, int busA, CimGenerator b, int busB)
    {
        int ra = PriorityRank(a), rb = PriorityRank(b);
        if (ra != rb) return ra < rb;
        double ca = Capacity(a), cb = Capacity(b);
        if (ca != cb) return ca > cb;
        return busA < busB;
    }

    private static int PriorityRank(CimGenerator g) => g.ReferencePriority > 0 ? g.ReferencePriority : int.MaxValue;

    // The unit size for the slack rule: maxOperatingP (MW), else ratedS (MVA), else 0.
    private static double Capacity(CimGenerator g) => g.MaxP ?? g.RatedSMva ?? 0.0;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
