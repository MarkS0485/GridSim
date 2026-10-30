namespace GridSim.Core.IO.Cim;

/// <summary>
/// N253 (CIM1, 2026-10): the topology processor of the CIM importer. It turns the node-breaker view of a CGMES
/// document (ConnectivityNodes joined by switches) into buses, the way a TopologicalNode is defined: "a set of
/// connectivity nodes that, in the current network state, are connected together through any type of closed
/// switches, including jumpers" (IEC 61970-301, CGMES 2.4.15 TP). See the <see cref="CimImporter"/> class summary for
/// the rules; this file holds the code.
///
/// <para>Before N253 the node-breaker view made every ConnectivityNode its own bus and ignored the switches, so a
/// substation drawn with breakers and disconnectors imported as a heap of separate islands (each switch was only
/// listed). That was the largest gap in the CIM import.</para>
/// </summary>
public static partial class CimImporter
{
    /// <summary>
    /// One switch as the topology processor sees it.
    /// </summary>
    /// <param name="Switch">The switch object.</param>
    /// <param name="Conducts">True when it joins its two terminals in this case: closed (SSH <c>Switch.open</c> =
    /// false, else EQ <c>Switch.normalOpen</c> = false), in service, exactly two terminals, both connected, and not an
    /// earthing switch.</param>
    /// <param name="Retained"><c>Switch.retained</c>: kept in the bus-branch model (its two sides stay two buses).</param>
    /// <param name="Earthing">A GroundDisconnector: it earths a node and never joins two.</param>
    /// <param name="TerminalCns">The ConnectivityNode of each of its terminals (null where a terminal names none).</param>
    private sealed record SwitchState(
        CimObject Switch, bool Conducts, bool Retained, bool Earthing, IReadOnlyList<string?> TerminalCns);

    /// <summary>The topology processor's result.</summary>
    /// <param name="Buses">The buses, ordered by mRID (ordinal).</param>
    /// <param name="CnToBus">Every ConnectivityNode of the document -> the mRID of its bus.</param>
    /// <param name="Joining">How many closed, non-retained switches were used to join nodes.</param>
    /// <param name="Separating">How many switches conduct nothing (open, out of service, a disconnected terminal).</param>
    /// <param name="RetainedClosed">How many closed retained switches were left unmerged.</param>
    private sealed record TopologyResult(
        IReadOnlyList<CimBus> Buses, Dictionary<string, string> CnToBus, int Joining, int Separating, int RetainedClosed);

    /// <summary>Every Terminal of the document grouped by the equipment it belongs to, in document order.</summary>
    private static Dictionary<string, List<CimObject>> TerminalsByEquipment(List<CimObject> objects)
    {
        var map = new Dictionary<string, List<CimObject>>(StringComparer.Ordinal);
        foreach (CimObject t in objects)
        {
            if (t.Class != "Terminal" || t.Resource("Terminal.ConductingEquipment") is not string equip) continue;
            if (!map.TryGetValue(equip, out List<CimObject>? list)) map[equip] = list = new List<CimObject>();
            list.Add(t);
        }
        return map;
    }

    /// <summary>
    /// Read every switch of the document (Switch and all its subclasses), in mRID order, and decide whether each one
    /// conducts in this case. Throws when a switch has no state at all.
    /// </summary>
    private static List<SwitchState> ReadSwitches(
        List<CimObject> objects, Dictionary<string, List<CimObject>> terminalsOf, List<string> notes)
    {
        var list = new List<SwitchState>();
        int fromNormalState = 0, noRetainedFlag = 0;
        foreach (CimObject s in objects.Where(o => SwitchClasses.Contains(o.Class)).OrderBy(o => o.Mrid, StringComparer.Ordinal))
        {
            // The state of this case: SSH Switch.open wins over the EQ normal state Switch.normalOpen (the normal
            // state is what the switch is in normally; the SSH says what it is in for this case). CGMES makes both
            // required in their profiles, so a switch with neither has no known state: refused, never guessed.
            bool? open = s.Boolean("Switch.open");
            if (open is null)
            {
                open = s.Boolean("Switch.normalOpen")
                    ?? throw new InvalidDataException(
                        $"CIM {s.Describe} has no Switch.open (SSH) and no Switch.normalOpen (EQ); whether it is open or " +
                        "closed is unknown, so the buses it would join cannot be built.");
                fromNormalState++;
            }

            // Switch.retained is required in the CGMES EQ profile; a document without it (older CIM, hand-written
            // files) is read as not retained, the ordinary switch that every tool merges. Counted and listed.
            bool? retainedFlag = s.Boolean("Switch.retained");
            if (retainedFlag is null) noRetainedFlag++;
            bool retained = retainedFlag ?? false;

            bool earthing = s.Class == "GroundDisconnector";
            bool inService = s.Boolean("Equipment.inService", "Equipment.normallyInService") != false;
            List<CimObject> terms = terminalsOf.TryGetValue(s.Mrid, out List<CimObject>? found) ? found : new List<CimObject>();
            List<CimObject> disconnected = terms.Where(t => t.Boolean("ACDCTerminal.connected", "Terminal.connected") == false).ToList();
            bool closed = open == false;
            bool conducts = closed && inService && terms.Count == 2 && disconnected.Count == 0 && !earthing;

            // Say why a closed switch conducts nothing (an open one needs no word: that is what open means).
            if (closed && !earthing)
            {
                if (!inService)
                    notes.Add($"{s.Describe} is closed but out of service (Equipment.inService / normallyInService = false); " +
                              "it conducts nothing and is treated as open.");
                else if (terms.Count != 2)
                    notes.Add($"{s.Describe} is closed but has {terms.Count} terminal(s); a switch joins two, so it joins nothing.");
                else if (disconnected.Count > 0)
                    notes.Add($"{s.Describe} is closed but its terminal {string.Join(" and ", disconnected.Select(t => t.Mrid))} " +
                              "is disconnected (connected = false); it conducts nothing and is treated as open.");
            }
            if (earthing && closed)
                notes.Add($"{s.Describe} (an earthing switch) is closed: the node it is on is earthed in this case. The " +
                          "earth connection is not represented; the node is not joined to anything by it.");

            list.Add(new SwitchState(s, conducts, retained, earthing,
                terms.Select(t => t.Resource("Terminal.ConnectivityNode")).ToList()));
        }
        if (fromNormalState > 0)
            notes.Add($"{fromNormalState} switch(es) have no Switch.open (no SSH profile); their normal state " +
                      "Switch.normalOpen (EQ) is used.");
        if (noRetainedFlag > 0)
            notes.Add($"{noRetainedFlag} switch(es) have no Switch.retained; they are read as not retained (merged when closed).");
        return list;
    }

    /// <summary>
    /// The topology processor: join the ConnectivityNodes across the conducting, non-retained switches (union-find)
    /// and make one bus per resulting set. Deterministic by construction:
    /// <list type="bullet">
    /// <item>the ConnectivityNodes are indexed in ordinal mRID order, and a union always keeps the SMALLER index as
    /// the root, so the root of every set is its smallest-mRID ConnectivityNode, whatever order the switches are
    /// joined in (and the switches are visited in mRID order anyway);</item>
    /// <item>a bus takes its root's mRID, so the buses (emitted in root order) are in ordinal mRID order;</item>
    /// <item>its name is that of the smallest-mRID BusbarSection on any of its nodes, else its root's name.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidDataException">A closed switch joins ConnectivityNodes of different base voltages.</exception>
    private static TopologyResult RunTopology(
        List<CimObject> objects, List<SwitchState> switches, Func<CimObject, double> cnKv,
        Dictionary<string, List<CimObject>> terminalsOf, List<string> notes)
    {
        List<CimObject> cns = objects.Where(o => o.Class == "ConnectivityNode")
            .OrderBy(o => o.Mrid, StringComparer.Ordinal).ToList();
        var index = new Dictionary<string, int>(cns.Count, StringComparer.Ordinal);
        for (int i = 0; i < cns.Count; i++) index[cns[i].Mrid] = i;

        // Union-find with path halving. Union keeps the smaller root, so a root is always its set's minimum index.
        int[] parent = new int[cns.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
        }

        int joining = 0, separating = 0, retainedClosed = 0;
        foreach (SwitchState s in switches)
        {
            if (s.Earthing) continue;                         // never joins two nodes (noted when closed)
            if (!s.Conducts) { separating++; continue; }      // open: the nodes either side stay apart
            if (s.Retained) { retainedClosed++; continue; }   // kept as two buses (CimNetwork.ClosedSwitches)
            string? a = s.TerminalCns[0], b = s.TerminalCns[1];
            if (a is null || b is null || !index.ContainsKey(a) || !index.ContainsKey(b))
            {
                // A closed switch with a terminal on no ConnectivityNode of this document (a boundary node not
                // loaded, or a terminal with no node) cannot join anything here; said, not guessed.
                notes.Add($"{s.Switch.Describe} is closed, but its terminal(s) are not both on ConnectivityNodes of this " +
                          $"document ({a ?? "none"}, {b ?? "none"}); it joins nothing.");
                continue;
            }
            Union(index[a], index[b]);
            joining++;
        }

        // The smallest-mRID BusbarSection on each set names the bus (busbars carry the names operators use; a
        // ConnectivityNode's name is often generated, CONNECTIVITY_NODE86 in the conformity models).
        var busbarOf = new Dictionary<int, CimObject>();
        foreach (CimObject bb in objects.Where(o => o.Class == "BusbarSection"))
        {
            if (!terminalsOf.TryGetValue(bb.Mrid, out List<CimObject>? terms)) continue;
            foreach (CimObject t in terms)
            {
                if (t.Resource("Terminal.ConnectivityNode") is not string cn || !index.TryGetValue(cn, out int ci)) continue;
                int root = Find(ci);
                if (!busbarOf.TryGetValue(root, out CimObject? current) || string.CompareOrdinal(bb.Mrid, current.Mrid) < 0)
                    busbarOf[root] = bb;
            }
        }

        // One bus per set, in root order (= ordinal mRID order of the buses). Members are gathered in index order.
        var members = new List<int>?[cns.Count];
        for (int i = 0; i < cns.Count; i++)
        {
            int root = Find(i);
            (members[root] ??= new List<int>()).Add(i);
        }
        var buses = new List<CimBus>();
        var cnToBus = new Dictionary<string, string>(cns.Count, StringComparer.Ordinal);
        for (int root = 0; root < cns.Count; root++)
        {
            if (members[root] is not List<int> set) continue;   // not a root
            // The base voltage the set's ConnectivityNodes share (through their VoltageLevel, directly or via a Bay).
            // A closed switch does not change voltage, so two different positive bases in one set are a data error.
            double kv = 0.0;
            CimObject? kvFrom = null;
            foreach (int m in set)
            {
                double k = cnKv(cns[m]);
                if (!(k > 0)) continue;
                if (kv > 0 && k != kv)
                    throw new InvalidDataException(
                        $"CIM: closed switches join ConnectivityNode {cns[m].Describe} ({F(k)} kV) and {kvFrom!.Describe} " +
                        $"({F(kv)} kV) into one bus; a switch does not change voltage, so the base voltages contradict each other.");
                kv = k;
                kvFrom = cns[m];
            }
            string busMrid = cns[root].Mrid;
            string name = busbarOf.TryGetValue(root, out CimObject? busbar) ? busbar.Name : cns[root].Name;
            buses.Add(new CimBus(busMrid, name, kv));
            foreach (int m in set) cnToBus[cns[m].Mrid] = busMrid;
        }
        return new TopologyResult(buses, cnToBus, joining, separating, retainedClosed);
    }

    /// <summary>
    /// Compare the topology processor's buses with the document's TopologicalNodes and list every disagreement by
    /// name: a bus whose ConnectivityNodes lie on two or more TopologicalNodes (the switch states join what the TP
    /// keeps apart), and a TopologicalNode whose ConnectivityNodes lie on two or more buses (the TP joins what an
    /// open or retained switch keeps apart). ConnectivityNodes that name no TopologicalNode are not compared.
    /// </summary>
    private static void CompareWithTopologicalNodes(
        TopologyResult topo, List<CimObject> objects, Dictionary<string, CimObject> byId, List<string> notes)
    {
        var busName = topo.Buses.ToDictionary(b => b.MRID, b => b.Name, StringComparer.Ordinal);
        var tnsOfBus = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var busesOfTn = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (CimObject cn in objects)
        {
            if (cn.Class != "ConnectivityNode" || cn.Resource("ConnectivityNode.TopologicalNode") is not string tn) continue;
            string bus = topo.CnToBus[cn.Mrid];
            if (!tnsOfBus.TryGetValue(bus, out SortedSet<string>? tns)) tnsOfBus[bus] = tns = new SortedSet<string>(StringComparer.Ordinal);
            tns.Add(tn);
            if (!busesOfTn.TryGetValue(tn, out SortedSet<string>? bs)) busesOfTn[tn] = bs = new SortedSet<string>(StringComparer.Ordinal);
            bs.Add(bus);
        }
        string Tn(string tn) => byId.TryGetValue(tn, out CimObject? o) ? $"'{o.Name}' ({tn})" : $"{tn} (not in this document)";
        string Bus(string b) => $"'{busName[b]}' ({b})";

        int disagreements = 0;
        foreach (CimBus bus in topo.Buses)   // mRID order
        {
            if (!tnsOfBus.TryGetValue(bus.MRID, out SortedSet<string>? tns) || tns.Count < 2) continue;
            disagreements++;
            notes.Add($"Topology check: the switch states join into bus {Bus(bus.MRID)} ConnectivityNodes that the " +
                      $"TopologicalNodes put on {tns.Count} different nodes: {string.Join(", ", tns.Select(Tn))}.");
        }
        foreach ((string tn, SortedSet<string> bs) in busesOfTn)   // mRID order
        {
            if (bs.Count < 2) continue;
            disagreements++;
            notes.Add($"Topology check: TopologicalNode {Tn(tn)} holds ConnectivityNodes that the switch states put on " +
                      $"{bs.Count} different buses (an open or retained switch keeps them apart): {string.Join(", ", bs.Select(Bus))}.");
        }
        if (disagreements == 0)
            notes.Add($"Topology check: the buses derived from the switch states agree one to one with the TopologicalNodes " +
                      $"({busesOfTn.Count} TopologicalNode(s) compared).");
    }

    /// <summary>
    /// The conducting switches whose two terminals are on two DIFFERENT buses of the chosen view, in mRID order:
    /// closed retained switches (both views), and in the bus-branch view closed non-retained switches that the
    /// TopologicalNodes nonetheless keep apart (a disagreement, said so). Each is listed in the diagnostics.
    /// </summary>
    private static List<CimSwitch> ClosedCouplers(
        List<SwitchState> switches, Func<string, List<string>> nodesOf, Func<string, string> busLabel,
        bool busBranch, List<string> notes)
    {
        var couplers = new List<CimSwitch>();
        foreach (SwitchState s in switches)
        {
            if (!s.Conducts) continue;
            List<string> nodes = nodesOf(s.Switch.Mrid);
            if (nodes.Count != 2 || string.Equals(nodes[0], nodes[1], StringComparison.Ordinal)) continue;
            couplers.Add(new CimSwitch(s.Switch.Mrid, s.Switch.Name, s.Switch.Class, nodes[0], nodes[1], s.Retained));
            string between = $"bus {busLabel(nodes[0])} and bus {busLabel(nodes[1])}";
            if (s.Retained)
                notes.Add($"{s.Switch.Describe} is closed and retained (Switch.retained = true): {between} stay two buses, as " +
                          "in a CGMES bus-branch model; a closed switch has zero impedance, so the bridge solves them as one bus.");
            else if (busBranch)
                notes.Add($"{s.Switch.Describe} is closed and not retained, but the TopologicalNodes put its two sides on " +
                          $"{between}: the topology (TP) disagrees with the switch state. The two buses are kept and the bridge " +
                          "joins them at zero impedance; import with CimTopology.Switches to rebuild the buses from the switch states.");
            else
                notes.Add($"{s.Switch.Describe} is closed but joins {between}, which are not merged (a terminal is on a " +
                          "node outside this document); the bridge joins them at zero impedance.");
        }
        return couplers;
    }

    /// <summary>The topology processor's summary line for the node-breaker view.</summary>
    private static string TopologySummary(int cnCount, TopologyResult topo) =>
        $"Node-breaker view: {cnCount} ConnectivityNode(s) form {topo.Buses.Count} bus(es); {topo.Joining} closed " +
        $"switch(es) join nodes, {topo.Separating} switch(es) are open or conduct nothing and keep nodes apart" +
        (topo.RetainedClosed > 0
            ? $", {topo.RetainedClosed} closed retained switch(es) keep their two sides as separate buses (listed below)."
            : ".");
}
