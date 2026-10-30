using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO.Cim;

/// <summary>
/// N287 (CIM2, 2026-10): the flow through a closed retained switch, reported after a solve. A closed switch has zero
/// impedance, so the bridge solves the two buses it joins as one GridModel bus (N253); the flow through it is then not a
/// solved branch flow but follows from the power balance of either side: the loads, shunts, branch flows and fixed
/// injections on the side of <see cref="NodeB"/> are what the switch carries from <see cref="NodeA"/> to
/// <see cref="NodeB"/> (or, equally, minus the same sum on the side of <see cref="NodeA"/>).
/// </summary>
/// <param name="MRID">The switch's mRID.</param>
/// <param name="Name">The switch name.</param>
/// <param name="NodeA">The CIM bus at its first terminal (<see cref="CimSwitch.NodeA"/>).</param>
/// <param name="NodeB">The CIM bus at its second terminal.</param>
/// <param name="PMw">Active power flowing through the switch from <see cref="NodeA"/> to <see cref="NodeB"/> (MW); null
/// when it cannot be derived (see <see cref="Basis"/>).</param>
/// <param name="QMvar">Reactive power from <see cref="NodeA"/> to <see cref="NodeB"/> (MVAr); null when it cannot be
/// derived.</param>
/// <param name="Basis">How the value was found, or why it could not be, in plain English.</param>
public sealed record CimSwitchFlow(string MRID, string Name, string NodeA, string NodeB, double? PMw, double? QMvar, string Basis);

/// <summary>
/// N287 (CIM2, 2026-10): what <see cref="CimGridModelBridge.Bridge"/> produced: the GridModel and where each CIM bus went.
/// </summary>
public sealed class CimBridgeResult
{
    internal CimBridgeResult(
        GridModel model, CimNetwork original, CimNetwork solved, Dictionary<string, int> busIdOfNode,
        IReadOnlyList<string> droppedNodes, List<(string From, string? To)> branchNodes, CimControls controls)
    {
        Model = model;
        Original = original;
        Network = solved;
        BusIdOfNode = busIdOfNode;
        DroppedNodes = droppedNodes;
        BranchNodes = branchNodes;
        RemoteVoltageControls = controls.Remote;
        SusceptanceControls = controls.Susceptances;
        ShuntRegulations = controls.Shunts;
    }

    /// <summary>N296 (CIM3, 2026-10): the voltage-regulating linear shunts resolved onto <see cref="Model"/>'s buses, each in
    /// its discrete (<see cref="SwitchedShuntPowerFlow"/>) and continuous forms. A plain solve holds them at their
    /// sections; <see cref="CimControlledPowerFlow"/> applies them (by default in the form their data asks for).</summary>
    public IReadOnlyList<CimShuntRegulation> ShuntRegulations { get; }

    /// <summary>N295 (CIM3, 2026-10): the remote voltage controls resolved onto <see cref="Model"/>'s buses (in regulated
    /// bus order). A plain solve of <see cref="Model"/> holds these machines at their q; <see cref="CimControlledPowerFlow"/>
    /// applies them.</summary>
    public IReadOnlyList<CimRemoteVoltageControl> RemoteVoltageControls { get; }

    /// <summary>N295: the variable-susceptance voltage controls (regulating StaticVarCompensators) resolved onto
    /// <see cref="Model"/>'s buses. A plain solve holds each at its SSH q; <see cref="CimControlledPowerFlow"/> applies
    /// them.</summary>
    public IReadOnlyList<CimSusceptanceControl> SusceptanceControls { get; }

    /// <summary>The GridModel (the same one <see cref="CimGridModelBridge.ToGridModel"/> returns).</summary>
    public GridModel Model { get; }

    /// <summary>The CIM network as given.</summary>
    public CimNetwork Original { get; }

    /// <summary>The CIM network that was solved: <see cref="Original"/> without its de-energised islands (the same
    /// object when there are none).</summary>
    public CimNetwork Network { get; }

    /// <summary>Every solved CIM bus (mRID) -> its GridModel bus id (buses joined by a closed switch share one).</summary>
    public IReadOnlyDictionary<string, int> BusIdOfNode { get; }

    /// <summary>The CIM buses (mRIDs) left out as de-energised islands, in the network's bus order.</summary>
    public IReadOnlyList<string> DroppedNodes { get; }

    /// <summary>The CIM bus at each end of every GridModel branch (null for a three-winding star point).</summary>
    internal IReadOnlyList<(string From, string? To)> BranchNodes { get; }

    /// <summary>
    /// The flow through every closed switch the bridge joined (<see cref="CimNetwork.Couplers"/> of the original
    /// network, in that order), from a converged solve of <see cref="Model"/>. EE basis (N287): a closed switch has zero
    /// impedance, so its flow is fixed by the power balance of one side of it (Kirchhoff's current law at the set of CIM
    /// buses on that side): flow A to B = (loads + shunts + branch flows leaving) - (fixed injections) on side B. It is
    /// derivable exactly when:
    /// <list type="bullet">
    /// <item>the switch is not in a loop of closed switches (parallel zero-impedance paths: any division of the flow
    /// between them satisfies the network equations, so the network does not determine it; PSS/E and PowerFactory
    /// report such a split by a convention, not by physics); and</item>
    /// <item>one side holds no machine whose output the solve decided: no generator at the slack bus for P, and no
    /// generator at the slack bus or a voltage-controlled (PV) bus for Q (a solved output is a bus total, and how it
    /// divides between machines on the two sides of a zero-impedance switch is not determined either).</item>
    /// </list>
    /// Otherwise the value is null and <see cref="CimSwitchFlow.Basis"/> says why. A switch in a de-energised island
    /// carries nothing (0, said). PSS/E reports zero-impedance-line flows from the same balance; powsybl computes the
    /// flow of a retained switch in its bus-breaker view the same way.
    /// <para>N306 (LF3, October 2026): a <see cref="CimControlledPowerFlow"/> result is recognised (the solver registers
    /// every result it returns) and balanced with the REGULATED values: a regulating SVC as its final susceptance at the
    /// solved voltage (instead of its SSH q), a regulating shunt with its final sections or continuous susceptance on top
    /// of the sections the model holds, and a remote-regulating machine's bus as voltage-controlled (its reactive output is
    /// a solved bus total, so a switch whose Q depends on it is not reported). Before, such a result had the same buses
    /// and branches as a plain one, was not detected, and its regulated devices were counted at their plain fixed values.
    /// A plain solve is balanced exactly as before. A controlled result of ANOTHER bridged model is refused by name.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="result"/> is not a solve of <see cref="Model"/> (bus or branch
    /// counts differ, or it is a <see cref="CimControlledPowerFlow"/> solve of another bridged model).</exception>
    /// <exception cref="InvalidOperationException">The solve did not converge: no flow is reported from an untrustworthy
    /// state.</exception>
    public IReadOnlyList<CimSwitchFlow> SwitchFlows(PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.BusIds.Count != Model.Buses.Count || result.LineFlows.Count != Model.Branches.Count)
            throw new ArgumentException(
                $"The power-flow result has {result.BusIds.Count} bus(es) and {result.LineFlows.Count} branch flow(s); the bridged " +
                $"model has {Model.Buses.Count} and {Model.Branches.Count}. It is not a solve of this model.", nameof(result));
        if (!result.Converged)
            throw new InvalidOperationException(
                "The power flow did not converge, so its state is not trustworthy; switch flows are not derived from it.");
        // N306: a CimControlledPowerFlow result carries its regulated values; one of another bridge cannot be read here.
        CimControlledSolveRecord? controlled = CimControlledPowerFlow.RecordOf(result);
        if (controlled is not null && !ReferenceEquals(controlled.Bridge, this))
            throw new ArgumentException(
                "The power-flow result is a CimControlledPowerFlow solve of another bridged model, so its regulated devices are " +
                "not this model's; switch flows are not derived from it.", nameof(result));

        // Per GridModel bus id: its voltage magnitude and type.
        var vmOf = new Dictionary<int, double>();
        for (int i = 0; i < result.BusIds.Count; i++) vmOf[result.BusIds[i]] = result.Vm[i];
        var typeOf = Model.Buses.ToDictionary(b => b.Id, b => b.Type);

        // Per CIM bus: the known net withdrawal (P, Q) and whether a machine there has a solved output.
        var withdrawP = new Dictionary<string, double>(StringComparer.Ordinal);
        var withdrawQ = new Dictionary<string, double>(StringComparer.Ordinal);
        var solvedP = new HashSet<string>(StringComparer.Ordinal);
        var solvedQ = new HashSet<string>(StringComparer.Ordinal);
        foreach (CimBus bus in Network.Buses)
        {
            withdrawP[bus.MRID] = 0.0;
            withdrawQ[bus.MRID] = 0.0;
        }
        foreach (CimLoad load in Network.Loads)
        {
            withdrawP[load.Node] += load.P;
            withdrawQ[load.Node] += load.Q;
        }
        foreach (CimShunt sh in Network.Shunts)
        {
            // The shunt's share of its GridModel bus's Gs / Bs, at the solved voltage (the same scale as the bridge).
            int id = BusIdOfNode[sh.Node];
            Bus bus = Model.Buses[id - 1];
            double kv = bus.BaseKv > 0 ? bus.BaseKv : sh.BaseKv;
            double scale = kv > 0 ? kv * kv : Model.BaseMva;
            double v2 = vmOf[id] * vmOf[id];
            withdrawP[sh.Node] += sh.G * scale * v2;
            withdrawQ[sh.Node] -= sh.B * scale * v2;
            // N306: in a controlled solve, a regulating shunt's final sections (switched) or continuous susceptance, both
            // MVAr at 1.0 pu RELATIVE to the sections held above, at the solved voltage (capacitive = a negative withdrawal).
            if (controlled is not null)
            {
                if (controlled.SwitchedMvarByMrid.TryGetValue(sh.MRID, out double switchedMvar)) withdrawQ[sh.Node] -= switchedMvar * v2;
                if (controlled.SusceptanceMvarByMrid.TryGetValue(sh.MRID, out double contMvar)) withdrawQ[sh.Node] -= contMvar * v2;
            }
        }
        // N295: an SVC is held at its SSH q in the plain model (a fixed withdrawal, load sign). N306: in a controlled solve
        // a regulating SVC is its final susceptance at the solved voltage instead (its SSH q was taken out of the bus).
        foreach (CimStaticVarCompensator svc in Network.Svcs)
        {
            if (controlled is not null && controlled.SusceptanceMvarByMrid.TryGetValue(svc.MRID, out double bMvar))
            {
                int id = BusIdOfNode[svc.Node];
                withdrawQ[svc.Node] -= bMvar * vmOf[id] * vmOf[id];
            }
            else
            {
                withdrawQ[svc.Node] += svc.Q;
            }
        }
        foreach (CimGenerator g in Network.Generators)
        {
            BusType t = typeOf[BusIdOfNode[g.Node]];
            // N306: a bus a remote control made voltage-controlled in the controlled solve has a solved reactive output.
            if (t == BusType.Pq && controlled is not null && controlled.RegulatingMachineBusIds.Contains(BusIdOfNode[g.Node]))
                t = BusType.Pv;
            if (t == BusType.Slack) solvedP.Add(g.Node);
            else withdrawP[g.Node] -= g.P;
            if (t is BusType.Slack or BusType.Pv) solvedQ.Add(g.Node);
            else withdrawQ[g.Node] -= g.Q;
        }
        for (int k = 0; k < BranchNodes.Count; k++)
        {
            (string from, string? to) = BranchNodes[k];
            LineFlow f = result.LineFlows[k];
            withdrawP[from] += f.PFromMw;
            withdrawQ[from] += f.QFromMvar;
            if (to is not null)
            {
                withdrawP[to] += f.PToMw;
                withdrawQ[to] += f.QToMvar;
            }
        }

        var live = new HashSet<string>(Network.Couplers.Select(c => c.MRID), StringComparer.Ordinal);
        var flows = new List<CimSwitchFlow>();
        foreach (CimSwitch sw in Original.Couplers)
        {
            if (!live.Contains(sw.MRID))
            {
                flows.Add(new CimSwitchFlow(sw.MRID, sw.Name, sw.NodeA, sw.NodeB, 0.0, 0.0,
                    "in a de-energised island (left out of the solve): it carries no flow."));
                continue;
            }
            // The other closed switches of the same GridModel bus: the graph the side of NodeB is found in.
            int busId = BusIdOfNode[sw.NodeA];
            List<CimSwitch> group = Network.Couplers
                .Where(c => c.MRID != sw.MRID && BusIdOfNode[c.NodeA] == busId).ToList();
            HashSet<string> sideB = Reach(sw.NodeB, group);
            if (sideB.Contains(sw.NodeA))
            {
                flows.Add(new CimSwitchFlow(sw.MRID, sw.Name, sw.NodeA, sw.NodeB, null, null,
                    "it is in a loop of closed switches (parallel zero-impedance paths): how the flow divides between them is " +
                    "not determined by the network, so it is not reported."));
                continue;
            }
            HashSet<string> sideA = Reach(sw.NodeA, group);
            (double? p, string pBasis) = Through(sideA, sideB, withdrawP, solvedP, "P");
            (double? q, string qBasis) = Through(sideA, sideB, withdrawQ, solvedQ, "Q");
            flows.Add(new CimSwitchFlow(sw.MRID, sw.Name, sw.NodeA, sw.NodeB, p, q, $"{pBasis}; {qBasis}."));
        }
        return flows;
    }

    /// <summary>The CIM buses reached from <paramref name="start"/> through <paramref name="switches"/>, start included.</summary>
    private static HashSet<string> Reach(string start, List<CimSwitch> switches)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            string at = stack.Pop();
            foreach (CimSwitch s in switches)
            {
                string? next = s.NodeA == at ? s.NodeB : s.NodeB == at ? s.NodeA : null;
                if (next is not null && seen.Add(next)) stack.Push(next);
            }
        }
        return seen;
    }

    /// <summary>The flow from side A to side B of one quantity: the net withdrawal of side B when no output there was
    /// solved, else minus that of side A when none there was; else not derivable. Sums in the network's bus order.</summary>
    private (double? Value, string Basis) Through(
        HashSet<string> sideA, HashSet<string> sideB, Dictionary<string, double> withdraw, HashSet<string> solved, string what)
    {
        bool solvedB = sideB.Any(solved.Contains), solvedA = sideA.Any(solved.Contains);
        if (!solvedB)
            return (Sum(sideB, withdraw), $"{what} from the balance of the second side");
        if (!solvedA)
            return (0.0 - Sum(sideA, withdraw), $"{what} from the balance of the first side");
        return (null, $"{what} not derivable: machines whose {what} output the solve decided are on both sides, and how a " +
                      "bus total divides across a zero-impedance switch is not determined");
    }

    private double Sum(HashSet<string> side, Dictionary<string, double> withdraw)
    {
        double s = 0.0;
        foreach (CimBus bus in Network.Buses)
            if (side.Contains(bus.MRID)) s += withdraw[bus.MRID];
        return s;
    }
}

public static partial class CimGridModelBridge
{
    /// <summary>
    /// Bug hunt 2026-10-29, finding 53 (decided, documented): a machine with REMOTE voltage control has VoltageControl
    /// false (the importer reads the control as remote), so an island whose only sources regulate remote buses is left
    /// out here as de-energised, although CimControlledPowerFlow would make them PV. This is consistent with the plain
    /// solve, which could not hold such an island either; the dropped island and its machines are listed in the log.
    /// N287 (CIM2, 2026-10): leave out the de-energised islands. An island (a set of CIM buses connected by lines,
    /// transformers and closed switches) is energised when it holds the slack (chosen by the same rule as
    /// <see cref="ToGridModel"/>) or a voltage-controlling machine; otherwise nothing in it can hold a voltage or an
    /// angle: it is cut off by open switches or disconnected terminals (the ENTSO-E MiniGrid's boundary equivalent
    /// nodes are such islands). It used to become buses with no slack, which GridModel.Validate refuses, so the whole
    /// import failed. PSS/E marks such buses isolated (type 4) and leaves them out of the solve; powsybl solves only the
    /// connected component that holds the slack; the CGMES SV profile gives them v = 0. So they are left out, each
    /// island in one note naming its buses and what it held. A network with no generator at all is left as it was (its
    /// first bus is the slack, as before); a network with no dead island is returned as the same object, so it bridges
    /// bit for bit as before.
    /// </summary>
    private static (CimNetwork Live, IReadOnlyList<string> Dropped, IReadOnlyList<string> IslandSlacks) DropDeadIslands(CimNetwork net, ICollection<string> log)
    {
        if (net.Generators.Count == 0 || net.Buses.Count == 0) return (net, Array.Empty<string>(), Array.Empty<string>());
        int count = net.Buses.Count;
        var index = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++) index.TryAdd(net.Buses[i].MRID, i);

        // Union-find over CIM bus indices; a root is its set's smallest index. Couplers first: their sets are the
        // GridModel buses, numbered exactly as the conversion numbers them (for the slack rule's bus-number tie-break).
        int[] parent = new int[count];
        for (int i = 0; i < count; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(string a, string b)
        {
            // An element naming an unknown node is left to the conversion, which refuses it by name.
            if (!index.TryGetValue(a, out int ia) || !index.TryGetValue(b, out int ib)) return;
            int ra = Find(ia), rb = Find(ib);
            if (ra < rb) parent[rb] = ra;
            else if (rb < ra) parent[ra] = rb;
        }
        foreach (CimSwitch sw in net.Couplers) Union(sw.NodeA, sw.NodeB);
        var busNumber = new int[count];
        int n = 0;
        for (int i = 0; i < count; i++) busNumber[i] = Find(i) == i ? ++n : 0;
        for (int i = 0; i < count; i++) if (busNumber[i] == 0) busNumber[i] = busNumber[Find(i)];

        // The slack machine, by the conversion's own rule (candidates: the voltage-controlling machines, or every
        // machine when none controls; key: referencePriority, unit size, bus number).
        bool anyControls = net.Generators.Any(g => g.VoltageControl != false);
        int best = -1;
        for (int k = 0; k < net.Generators.Count; k++)
        {
            CimGenerator g = net.Generators[k];
            if (!index.TryGetValue(g.Node, out int gi)) return (net, Array.Empty<string>(), Array.Empty<string>());   // refused by the conversion
            if (anyControls && g.VoltageControl == false) continue;
            if (best < 0 || SlackKeyLess(g, busNumber[gi], net.Generators[best], busNumber[index[net.Generators[best].Node]])) best = k;
        }

        // Now the islands: lines and transformers join their ends too.
        foreach (CimBranch br in net.Branches) Union(br.FromNode, br.ToNode);
        foreach (CimTransformer tx in net.Transformers)
            for (int e = 1; e < tx.Ends.Count; e++) Union(tx.Ends[0].Node, tx.Ends[e].Node);
        var energised = new HashSet<int> { Find(index[net.Generators[best].Node]) };
        var controlled = new HashSet<int>();
        foreach (CimGenerator g in net.Generators)
            if (g.VoltageControl != false) controlled.Add(Find(index[g.Node]));
        energised.UnionWith(controlled);

        // Islands in the order of their first bus.
        var dead = new bool[count];
        var islands = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            int root = Find(i);
            if (energised.Contains(root)) continue;
            dead[i] = true;
            if (!islands.TryGetValue(root, out List<int>? members)) islands[root] = members = new List<int>();
            members.Add(i);
        }
        int slackRoot = Find(index[net.Generators[best].Node]);
        // N296 (CIM3, 2026-10, Ruling R1): an energised island that is not the main slack's gets its OWN slack, elected by
        // the main island's rule within the island (the voltage-controlling machines there; key: referencePriority, then
        // the largest unit, then the lowest bus number, which is the mRID order in the node-breaker view and the
        // TopologicalNode order in the bus-branch view; deterministic either way). PSS/E gives each energised island its
        // own swing bus (and powsybl / OpenLoadFlow solves each synchronous component with its own reference); the CGMES SV
        // profile carries one angle per TopologicalIsland's reference. It used to be kept with no slack, so
        // GridModel.Validate refused the whole model (one synchronous island per solve). The island's angles are relative
        // to its own slack, and its slack balances its own power; both are said.
        var islandSlacks = new List<string>();
        foreach (int root in controlled.Where(r => r != slackRoot).OrderBy(r => r))
        {
            int pick = -1;
            for (int k = 0; k < net.Generators.Count; k++)
            {
                CimGenerator g = net.Generators[k];
                if (g.VoltageControl == false || Find(index[g.Node]) != root) continue;
                if (pick < 0 || SlackKeyLess(g, busNumber[index[g.Node]], net.Generators[pick], busNumber[index[net.Generators[pick].Node]]))
                    pick = k;
            }
            CimGenerator chosen = net.Generators[pick];
            islandSlacks.Add(chosen.MRID);
            log.Add($"An island with its own voltage-controlling machine(s) (bus '{net.Buses[root].Name}', ...) is not connected " +
                    $"to the main slack; N296: it gets its own slack, machine {chosen.Name} at bus '{net.Buses[index[chosen.Node]].Name}', " +
                    "elected by the main island's rule (referencePriority, then the largest unit, then the lowest bus number), as " +
                    "PSS/E gives each energised island its own swing bus. Its angles are relative to its own slack, which balances " +
                    "its own power.");
        }
        if (islands.Count == 0) return (net, Array.Empty<string>(), islandSlacks);

        bool IsDead(string node) => index.TryGetValue(node, out int i) && dead[i];
        foreach (List<int> members in islands.Values)
        {
            var nodes = new HashSet<string>(members.Select(i => net.Buses[i].MRID), StringComparer.Ordinal);
            List<CimLoad> loads = net.Loads.Where(l => nodes.Contains(l.Node)).ToList();
            List<string> gens = net.Generators.Where(g => nodes.Contains(g.Node)).Select(g => $"{g.Name} ({g.Class})").ToList();
            int lines = net.Branches.Count(b => nodes.Contains(b.FromNode));
            int txs = net.Transformers.Count(t => t.Ends.Count > 0 && nodes.Contains(t.Ends[0].Node));
            int shunts = net.Shunts.Count(s => nodes.Contains(s.Node));
            double p = 0.0, q = 0.0;
            foreach (CimLoad l in loads) { p += l.P; q += l.Q; }
            log.Add($"De-energised island: bus(es) {string.Join(", ", members.Select(i => $"'{net.Buses[i].Name}' ({net.Buses[i].MRID})"))} " +
                    "hold no slack and no voltage-controlling machine (they are cut off by open switches or disconnected " +
                    "terminals), so they are left out of the solve, as PSS/E marks such buses isolated (type 4). With them: " +
                    $"{loads.Count} load(s) ({F(p)} MW, {F(q)} MVAr), {gens.Count} generator(s) or injection(s)" +
                    $"{(gens.Count > 0 ? $" ({string.Join(", ", gens)})" : "")}, {lines} line(s), {txs} transformer(s), {shunts} shunt(s).");
        }

        // The network without them. Every element of a dead island is on dead buses only (an island is closed under
        // the elements that join buses), so testing one end is enough.
        var live = new CimNetwork(
            net.Buses.Where(b => !IsDead(b.MRID)).ToList(),
            net.Branches.Where(b => !IsDead(b.FromNode)).ToList(),
            net.Loads.Where(l => !IsDead(l.Node)).ToList(),
            net.Generators.Where(g => !IsDead(g.Node)).ToList(),
            net.Transformers.Where(t => t.Ends.Count == 0 || !IsDead(t.Ends[0].Node)).ToList(),
            net.Diagnostics,
            net.Couplers.Where(c => !IsDead(c.NodeA)).ToList(),
            net.Shunts.Where(s => !IsDead(s.Node)).ToList(),
            net.Svcs.Where(s => !IsDead(s.Node)).ToList());
        return (live, net.Buses.Where(b => IsDead(b.MRID)).Select(b => b.MRID).ToList(), islandSlacks);
    }
}
