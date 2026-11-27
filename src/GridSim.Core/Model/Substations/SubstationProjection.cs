using System.Collections.ObjectModel;

namespace GridSim.Core.Model.Substations;

/// <summary>How the projected bus ids are numbered.</summary>
public enum BusNumbering
{
    /// <summary>
    /// A bus id is 1 + the rank (by ordinal node id, over ALL nodes of the model) of the lowest-ranked node of its
    /// electrical group. Splitting or rejoining a group never renumbers any other group, so a bus keeps its id across
    /// every switching state in which its lowest node is still the lowest of its group. Ids are not contiguous.
    /// </summary>
    NodeRank,
    /// <summary>Ids 1..n in the order of each group's lowest node (contiguous, but they shift when a group splits).</summary>
    Compact,
}

/// <param name="Unspecified">What to do with a switch whose state is not stated (default: refuse).</param>
/// <param name="Numbering">The bus numbering (default: stable node-rank ids).</param>
public sealed record SubstationProjectionOptions(
    UnspecifiedStatePolicy Unspecified = UnspecifiedStatePolicy.Refuse,
    BusNumbering Numbering = BusNumbering.NodeRank);

/// <summary>One electrical bus of the projection: the connectivity nodes that closed switches join.</summary>
/// <param name="BusId">The id in the projected <see cref="GridModel"/>.</param>
/// <param name="RepresentativeNode">The lowest node id (ordinal) of the group: the group's identity.</param>
/// <param name="Nodes">Every node of the group, in ordinal order.</param>
public sealed record ProjectedBus(int BusId, string RepresentativeNode, string Substation, double NominalKv, IReadOnlyList<string> Nodes);

/// <summary>One group of connectivity nodes joined by closed switches. Every node of the model is in exactly one group.</summary>
/// <param name="BusId">The projected bus, or null when the group is not a bus.</param>
/// <param name="Reason">Why the group is not a bus (null for a bus).</param>
public sealed record NodeGroup(IReadOnlyList<string> Nodes, int? BusId, string? Reason);

/// <summary>What became of one piece of equipment.</summary>
public enum EquipmentDisposition
{
    /// <summary>On an electrical bus (a branch: both ends on buses); in the projected model.</summary>
    Connected,
    /// <summary>Not in the projected model because a terminal is isolated: alone on its group of nodes (an open breaker).</summary>
    Isolated,
    /// <summary>Not in the projected model because a terminal sits on a group a closed earth switch earths.</summary>
    Earthed,
    /// <summary>A branch not in the projected model because closed switches put both its ends on one bus.</summary>
    ShortedOut,
}

/// <summary>The disposition of one piece of equipment, with the reason in words and the power it carries.</summary>
public sealed record EquipmentOutcome(string EquipmentId, NbEquipmentKind Kind, EquipmentDisposition Disposition, string Reason,
    double Mw, double Mvar);

/// <summary>What a switch did in this projection.</summary>
public enum SwitchDisposition
{
    /// <summary>Closed, joining its two nodes into one electrical bus.</summary>
    ContractedClosed,
    /// <summary>Open, joining nothing.</summary>
    Open,
    /// <summary>A closed earth switch: its group is earthed.</summary>
    EarthingClosed,
}

public sealed record SwitchOutcome(string SwitchId, bool Open, SwitchDisposition Disposition);

/// <summary>A projected branch and the two terminals it ends on (one to one: from is the first terminal, to the second).</summary>
public sealed record ProjectedBranch(int BranchIndex, string EquipmentId, string FromTerminal, string ToTerminal, int FromBus, int ToBus);

/// <summary>
/// The result of projecting a node-breaker model at one set of switch states. <see cref="Model"/> is an ordinary
/// bus-branch <see cref="GridModel"/>. The maps run both ways: node to bus, bus to nodes, terminal to bus, branch to
/// terminals. Demand and generation are accounted in full: connected plus not connected always equals the model total.
/// </summary>
public sealed class SubstationProjectionResult
{
    public required GridModel Model { get; init; }
    public required IReadOnlyList<ProjectedBus> Buses { get; init; }
    /// <summary>Every group of nodes joined by closed switches (buses or not), in the order of each group's lowest node.</summary>
    public required IReadOnlyList<NodeGroup> Groups { get; init; }
    /// <summary>Node id to projected bus id, for every node that is on a bus.</summary>
    public required IReadOnlyDictionary<string, int> NodeToBus { get; init; }
    /// <summary>Terminal id to bus id, for every terminal that is on a bus.</summary>
    public required IReadOnlyDictionary<string, int> TerminalToBus { get; init; }
    public required IReadOnlyList<ProjectedBranch> Branches { get; init; }
    public required IReadOnlyList<EquipmentOutcome> Equipment { get; init; }
    public required IReadOnlyList<SwitchOutcome> Switches { get; init; }
    /// <summary>Nodes that are on no bus, with the reason (isolated, earthed, or no equipment on their group).</summary>
    public required IReadOnlyDictionary<string, string> UnbusedNodes { get; init; }
    /// <summary>The state used for every switch (true = open), keyed by switch id, in ordinal order.</summary>
    public required IReadOnlyDictionary<string, bool> SwitchStates { get; init; }
    public required double DemandMwTotal { get; init; }
    public required double DemandMwServed { get; init; }
    public required double DemandMwNotServed { get; init; }
    public required double GenerationMwTotal { get; init; }
    public required double GenerationMwConnected { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>WP24: the star-point bus of each projected three-winding transformer, keyed by its id (empty when the model has none).</summary>
    public IReadOnlyDictionary<string, int> StarPoints { get; init; } = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());

    /// <summary>The bus a node is on, or null.</summary>
    public ProjectedBus? BusOf(string node) =>
        NodeToBus.TryGetValue(node, out int id) ? Buses.First(b => b.BusId == id) : null;

    /// <summary>The node groups as sorted node lists, each joined with "+", the whole list in group order: the form the
    /// tests and the CLI state the grouping in, for example <c>A1+A2|B1</c>.</summary>
    public string GroupingText() => string.Join("|", Groups.Select(g => string.Join("+", g.Nodes)));
}

/// <summary>
/// Node-breaker to bus-branch projection (RC4 R4-11 part A). The order is fixed: validate -> resolve every switch state
/// -> contract nodes joined by closed switches (union-find, switches taken in ordinal id order) -> classify each group
/// -> create buses in canonical order -> map terminals -> attach equipment -> record what was left out and why -> emit.
/// Every ordering is by ordinal node, switch or equipment id, never by hash or dictionary order, so the same input gives
/// the same buses, the same ids and the same floating-point sums on every run.
/// <para>A group of nodes becomes a bus when it holds two or more equipment terminals and no closed earth switch earths
/// it. A group with one terminal is isolated (the usual result of an open breaker): its equipment is left out of the
/// bus-branch model and recorded. Isolated branch ends leave the whole branch out. A load or generator never silently
/// vanishes: it is listed with its MW, and the demand and generation totals add up.</para>
/// </summary>
public static class SubstationProjection
{
    /// <summary>Project <paramref name="model"/> at <paramref name="states"/> (switch id to open).</summary>
    public static SubstationProjectionResult Project(SubstationModel model, IReadOnlyDictionary<string, bool> states,
        SubstationProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(states);
        options ??= new SubstationProjectionOptions();
        model.Validate();

        // ---- resolve every switch state ----
        var resolved = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var switchById = model.AllSwitches.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (string id in states.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!switchById.ContainsKey(id))
                throw new SubstationTopologyException(new[] { $"a state is given for unknown switch '{id}'." });
        var unspecified = new List<string>();
        foreach (NbSwitch sw in switchById.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (states.TryGetValue(sw.Id, out bool open)) resolved[sw.Id] = open;
            else if (options.Unspecified == UnspecifiedStatePolicy.UseNormalState) resolved[sw.Id] = sw.NormalOpen;
            else unspecified.Add(sw.Id);
        }
        if (unspecified.Count > 0)
            throw new SubstationTopologyException(new[]
            {
                "no state is given for switch" + (unspecified.Count > 1 ? "es " : " ") + string.Join(", ", unspecified.Select(u => $"'{u}'"))
                + "; a closed state is never inferred. State every switch, or choose the declared policy that uses the normal states.",
            });

        // ---- contract the nodes joined by closed switches ----
        string[] nodes = model.AllNodes.Select(n => n.Id).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Length; i++) index[nodes[i]] = i;
        int[] parent = Enumerable.Range(0, nodes.Length).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;   // the lowest index is always the root
        }
        var earthed = new HashSet<int>();
        foreach (NbSwitch sw in switchById.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (resolved[sw.Id] || sw.Kind == SwitchKind.EarthSwitch) continue;
            Union(index[sw.FromNode], index[sw.ToNode!]);
        }
        foreach (NbSwitch sw in switchById.Values.Where(s => s.Kind == SwitchKind.EarthSwitch && !resolved[s.Id]))
            earthed.Add(Find(index[sw.FromNode]));
        var earthedRoots = earthed.ToHashSet();

        // ---- terminals per group ----
        IReadOnlyList<NbTerminal> terminals = model.Terminals();
        var perGroup = new Dictionary<int, int>();
        foreach (NbTerminal t in terminals)
        {
            int g = Find(index[t.Node]);
            perGroup[g] = perGroup.GetValueOrDefault(g) + 1;
        }

        // ---- the groups that become buses, in canonical order (root = lowest node index) ----
        var busRoots = new List<int>();
        var unbused = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var rootReason = new Dictionary<int, (EquipmentDisposition D, string Why)>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (Find(i) != i) continue;
            int count = perGroup.GetValueOrDefault(i);
            if (earthedRoots.Contains(i)) rootReason[i] = (EquipmentDisposition.Earthed, "its group of nodes is earthed by a closed earth switch");
            else if (count == 1) rootReason[i] = (EquipmentDisposition.Isolated, "it is alone on its group of nodes (cut off by open switches)");
            else if (count == 0) rootReason[i] = (EquipmentDisposition.Isolated, "no equipment is attached to this group of nodes");
            else busRoots.Add(i);
        }
        var rootBusId = new Dictionary<int, int>();
        for (int k = 0; k < busRoots.Count; k++)
            rootBusId[busRoots[k]] = options.Numbering == BusNumbering.NodeRank ? busRoots[k] + 1 : k + 1;
        for (int i = 0; i < nodes.Length; i++)
            if (!rootBusId.ContainsKey(Find(i)))
                unbused[nodes[i]] = rootReason[Find(i)].Why;

        // ---- buses and the node maps ----
        var groupNodes = new Dictionary<int, List<string>>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int r = Find(i);
            if (!rootBusId.ContainsKey(r)) continue;
            if (!groupNodes.TryGetValue(r, out List<string>? l)) groupNodes[r] = l = new List<string>();
            l.Add(nodes[i]);   // already in ordinal order
        }
        var nodeInfo = model.Substations.SelectMany(s => s.Nodes.Select(n => (Node: n, Sub: s))).ToDictionary(x => x.Node.Id, StringComparer.Ordinal);
        var buses = new List<ProjectedBus>();
        var nodeToBus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (int r in busRoots)
        {
            List<string> ns = groupNodes[r];
            Substation sub = nodeInfo[ns[0]].Sub;
            double kv = model.NominalKvOf(ns[0]) ?? 0;
            buses.Add(new ProjectedBus(rootBusId[r], ns[0], sub.Id, kv, ns.AsReadOnly()));
            foreach (string n in ns) nodeToBus[n] = rootBusId[r];
        }
        var allGroups = new List<NodeGroup>();
        {
            var members = new Dictionary<int, List<string>>();
            for (int i = 0; i < nodes.Length; i++)
            {
                int r = Find(i);
                if (!members.TryGetValue(r, out List<string>? l)) members[r] = l = new List<string>();
                l.Add(nodes[i]);
            }
            foreach (int r in members.Keys.OrderBy(k => k))
                allGroups.Add(rootBusId.TryGetValue(r, out int id)
                    ? new NodeGroup(members[r].AsReadOnly(), id, null)
                    : new NodeGroup(members[r].AsReadOnly(), null, rootReason[r].Why));
        }
        int BusOfNode(string node) => nodeToBus.TryGetValue(node, out int b) ? b : -1;

        // ---- equipment ----
        var outcomes = new List<EquipmentOutcome>();
        var terminalToBus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (NbTerminal t in terminals) { int b = BusOfNode(t.Node); if (b >= 0) terminalToBus[t.Id] = b; }

        (EquipmentDisposition D, string Why) Why(string node) => rootReason[Find(index[node])];
        var branches = new List<Branch>();
        var projectedBranches = new List<ProjectedBranch>();

        void BranchOf(string id, NbEquipmentKind kind, string nodeFrom, string nodeTo, string termFrom, string termTo, Func<int, int, Branch> make)
        {
            int f = BusOfNode(nodeFrom), t = BusOfNode(nodeTo);
            if (f < 0 || t < 0)
            {
                var (d, why) = f < 0 ? Why(nodeFrom) : Why(nodeTo);
                string end = f < 0 ? termFrom : termTo;
                outcomes.Add(new EquipmentOutcome(id, kind, d, $"terminal '{end}': {why}; the branch is left out.", 0, 0));
                return;
            }
            if (f == t)
            {
                outcomes.Add(new EquipmentOutcome(id, kind, EquipmentDisposition.ShortedOut,
                    $"both terminals are on bus {f} (closed switches join them); the branch is left out.", 0, 0));
                return;
            }
            branches.Add(make(f, t));
            projectedBranches.Add(new ProjectedBranch(branches.Count - 1, id, termFrom, termTo, f, t));
            outcomes.Add(new EquipmentOutcome(id, kind, EquipmentDisposition.Connected, "both terminals are on buses.", 0, 0));
        }
        foreach (NbLine l in model.Lines.OrderBy(x => x.Id, StringComparer.Ordinal))
            BranchOf(l.Id, NbEquipmentKind.Line, l.NodeA, l.NodeB, l.Id + ".a", l.Id + ".b",
                (f, t) => new Branch(f, t, l.R, l.X, l.B, RateAMva: l.RateAMva, Name: l.Id));
        foreach (NbTransformer x in model.Transformers.OrderBy(x => x.Id, StringComparer.Ordinal))
            BranchOf(x.Id, NbEquipmentKind.Transformer, x.NodeHv, x.NodeLv, x.Id + ".hv", x.Id + ".lv",
                (f, t) => new Branch(f, t, x.R, x.X, x.B, x.TapRatio, x.PhaseShiftDeg, x.RateAMva, x.Id, IsTransformer: true));

        // WP24: three-winding transformers. Each projected unit gets a star bus, numbered after every node-based bus id
        // (NodeRank: after the node count; Compact: after the bus count), in ordinal id order, and one branch per winding from
        // the winding's bus to the star with the star impedance (ThreeWindingStar, the PSS/E reader's expression), the
        // winding's ratio and shift, as the RAW reader builds a unit. A unit with fewer than two windings on buses is left out
        // (as the RAW reader leaves out a unit with fewer than two live windings); a winding whose node is on no bus is left
        // out of a unit that keeps the other two. A model without such units is projected exactly as before.
        var starBuses = new List<Bus>();
        var starPoints = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int starBase = options.Numbering == BusNumbering.NodeRank ? nodes.Length : busRoots.Count;
        foreach (NbTransformer3W x in model.Transformers3W.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string[] ends = { "hv", "mv", "lv" };
            int[] wb = { BusOfNode(x.NodeHv), BusOfNode(x.NodeMv), BusOfNode(x.NodeLv) };
            int live = wb.Count(b => b >= 0);
            if (live < 2)
            {
                int dead = Array.FindIndex(wb, b => b < 0);
                var (d, why) = Why(x.NodeOf(dead));
                outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Transformer3W, d,
                    $"terminal '{x.Id}.{ends[dead]}': {why}; fewer than two windings are on buses, so the unit is left out.", 0, 0));
                continue;
            }
            int star = starBase + starBuses.Count + 1;
            double kvHv = model.NominalKvOf(x.NodeHv) ?? 0;
            starBuses.Add(new Bus(star, BusType.Pq, 0, 0, 0, x.B * model.BaseMva, kvHv, 1.0, Name: x.Id + " star point"));
            starPoints[x.Id] = star;
            var s = x.Star();
            double[] sr = { s.R1, s.R2, s.R3 }, sx = { s.X1, s.X2, s.X3 };
            var left = new List<string>();
            for (int n = 0; n < 3; n++)
            {
                if (wb[n] < 0) { left.Add($"winding {n + 1} ('{x.Id}.{ends[n]}': {Why(x.NodeOf(n)).Why})"); continue; }
                branches.Add(new Branch(wb[n], star, sr[n], sx[n], 0, x.RatioOf(n), x.ShiftOf(n), 0, $"{x.Id} winding {n + 1}", IsTransformer: true));
                projectedBranches.Add(new ProjectedBranch(branches.Count - 1, x.Id, x.Id + "." + ends[n], x.Id + ".star", wb[n], star));
            }
            outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Transformer3W, EquipmentDisposition.Connected,
                left.Count == 0 ? $"three windings on buses; star point bus {star}." : $"star point bus {star}; left out: {string.Join(", ", left)}.", 0, 0));
        }

        var pd = new Dictionary<int, double>(); var qd = new Dictionary<int, double>();
        var gs = new Dictionary<int, double>(); var bs = new Dictionary<int, double>();
        double demandTotal = 0, demandServed = 0, genTotal = 0, genConnected = 0;
        foreach (NbLoad x in model.Loads.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            demandTotal += x.Pd;
            int b = BusOfNode(x.Node);
            if (b < 0)
            {
                var (d, why) = Why(x.Node);
                outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Load, d, $"terminal '{x.Id}.t': {why}; the demand is not served.", x.Pd, x.Qd));
                continue;
            }
            pd[b] = pd.GetValueOrDefault(b) + x.Pd; qd[b] = qd.GetValueOrDefault(b) + x.Qd; demandServed += x.Pd;
            outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Load, EquipmentDisposition.Connected, $"on bus {b}.", x.Pd, x.Qd));
        }
        foreach (NbShunt x in model.Shunts.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            int b = BusOfNode(x.Node);
            if (b < 0)
            {
                var (d, why) = Why(x.Node);
                outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Shunt, d, $"terminal '{x.Id}.t': {why}.", x.Gs, x.Bs));
                continue;
            }
            gs[b] = gs.GetValueOrDefault(b) + x.Gs; bs[b] = bs.GetValueOrDefault(b) + x.Bs;
            outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Shunt, EquipmentDisposition.Connected, $"on bus {b}.", x.Gs, x.Bs));
        }
        var generators = new List<Generator>();
        var genBus = new Dictionary<int, List<NbGenerator>>();
        foreach (NbGenerator x in model.Generators.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            genTotal += x.Pg;
            int b = BusOfNode(x.Node);
            if (b < 0)
            {
                var (d, why) = Why(x.Node);
                outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Generator, d, $"terminal '{x.Id}.t': {why}; the generation is not connected.", x.Pg, x.Qg));
                continue;
            }
            genConnected += x.Pg;
            if (!genBus.TryGetValue(b, out List<NbGenerator>? l)) genBus[b] = l = new List<NbGenerator>();
            l.Add(x);
            generators.Add(new Generator(b, x.Pg, x.Qg, x.Vg, x.Qmin, x.Qmax, x.Pmin, x.Pmax, Name: x.Id));
            outcomes.Add(new EquipmentOutcome(x.Id, NbEquipmentKind.Generator, EquipmentDisposition.Connected, $"on bus {b}.", x.Pg, x.Qg));
        }

        var warnings = new List<string>();
        var gridBuses = new List<Bus>();
        foreach (ProjectedBus pb in buses)
        {
            genBus.TryGetValue(pb.BusId, out List<NbGenerator>? gl);
            BusType type = gl is null ? BusType.Pq : gl.Any(g => g.Slack) ? BusType.Slack : BusType.Pv;
            double vm = gl is null ? 1.0 : gl[0].Vg;
            gridBuses.Add(new Bus(pb.BusId, type, pd.GetValueOrDefault(pb.BusId), qd.GetValueOrDefault(pb.BusId),
                gs.GetValueOrDefault(pb.BusId), bs.GetValueOrDefault(pb.BusId), pb.NominalKv, vm, Name: pb.RepresentativeNode));
        }
        gridBuses.AddRange(starBuses);
        if (gridBuses.Count > 0 && gridBuses.All(b => b.Type != BusType.Slack))
            warnings.Add("no bus holds a slack generator in this switching state; a solver needs a reference.");

        var grid = new GridModel
        {
            BaseMva = model.BaseMva,
            Buses = gridBuses,
            Generators = generators,
            Branches = branches,
            Name = model.Name,
            FrequencyHz = model.FrequencyHz,
        };

        var swOut = switchById.Values.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s =>
            new SwitchOutcome(s.Id, resolved[s.Id],
                resolved[s.Id] ? SwitchDisposition.Open
                : s.Kind == SwitchKind.EarthSwitch ? SwitchDisposition.EarthingClosed : SwitchDisposition.ContractedClosed)).ToList();

        return new SubstationProjectionResult
        {
            Model = grid,
            Buses = buses.AsReadOnly(),
            Groups = allGroups.AsReadOnly(),
            NodeToBus = new ReadOnlyDictionary<string, int>(nodeToBus),
            TerminalToBus = new ReadOnlyDictionary<string, int>(terminalToBus),
            Branches = projectedBranches.AsReadOnly(),
            Equipment = outcomes.OrderBy(o => o.Kind).ThenBy(o => o.EquipmentId, StringComparer.Ordinal).ToList().AsReadOnly(),
            Switches = swOut.AsReadOnly(),
            UnbusedNodes = new ReadOnlyDictionary<string, string>(unbused),
            SwitchStates = new ReadOnlyDictionary<string, bool>(resolved),
            DemandMwTotal = demandTotal,
            DemandMwServed = demandServed,
            DemandMwNotServed = demandTotal - demandServed,
            GenerationMwTotal = genTotal,
            GenerationMwConnected = genConnected,
            Warnings = warnings.AsReadOnly(),
            StarPoints = new ReadOnlyDictionary<string, int>(starPoints),
        };
    }

    /// <summary>Project at the declared normal state of every switch.</summary>
    public static SubstationProjectionResult ProjectNormal(SubstationModel model, SubstationProjectionOptions? options = null) =>
        Project(model, model.NormalStates(), options);

    /// <summary>The same states with the given switches opened and closed (a switch id in either list must exist).</summary>
    public static IReadOnlyDictionary<string, bool> With(IReadOnlyDictionary<string, bool> states,
        IEnumerable<string> open, IEnumerable<string> close)
    {
        var d = new Dictionary<string, bool>(states, StringComparer.Ordinal);
        foreach (string s in open) d[s] = true;
        foreach (string s in close) d[s] = false;
        return d;
    }
}
