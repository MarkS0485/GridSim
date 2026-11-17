namespace GridSim.Core.IO.Psse.Topology;

/// <summary>The native key of one PSS/E substation switching device.</summary>
public sealed record PsseSwitchKey(int Substation, int FromNode, int ToNode, string CircuitId);

/// <summary>A deterministic reachability group formed by traversing closed substation switches.</summary>
public sealed record PsseConnectivityGroup(int Id, IReadOnlyList<PsseTopologyNode> Nodes, IReadOnlyList<int> NativeBusIds);

/// <summary>The connectivity projection of native node-breaker topology. This is not a solved electrical model.</summary>
public sealed record PsseSubstationConnectivityResult(
    IReadOnlyList<PsseConnectivityGroup> Groups,
    IReadOnlyDictionary<(int Substation, int Node), int> NodeToConnectivityGroup,
    IReadOnlyDictionary<int, int> TerminalLineToConnectivityGroup,
    IReadOnlyDictionary<int, IReadOnlyList<int>> NativeBusToConnectivityGroups,
    IReadOnlyDictionary<PsseSwitchKey, bool> RequestedSwitchStates,
    IReadOnlyDictionary<PsseSwitchKey, bool> EffectiveSwitchStates,
    IReadOnlyList<PsseTopologyTerminal> UnmappedTerminals);

/// <summary>
/// Projects a validated PSS/E node-breaker graph through an explicit state for every switch. The result records
/// reachability only. Switch reactance is finite and must be retained as equipment when a later adapter builds a
/// numerical model. Inter-substation equipment is not contracted; it remains attached to projected terminals.
/// </summary>
public static class PsseSubstationProjection
{
    public static PsseSubstationConnectivityResult Project(PsseSubstationTopology topology,
        IReadOnlyDictionary<PsseSwitchKey, bool> switchStates)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(switchStates);

        var switchByKey = new Dictionary<PsseSwitchKey, PsseTopologySwitch>();
        foreach (PsseTopologySwitch sw in topology.Switches)
        {
            var key = Key(sw);
            if (!switchByKey.TryAdd(key, sw))
                throw new InvalidDataException($"PSS/E switch key is duplicated: substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.");
        }
        foreach (PsseSwitchKey key in switchStates.Keys)
            if (!switchByKey.ContainsKey(key))
                throw new InvalidDataException($"Switch state was supplied for unknown PSS/E switch in substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.");
        foreach (PsseSwitchKey key in switchByKey.Keys)
            if (!switchStates.ContainsKey(key))
                throw new InvalidDataException($"An explicit state is required for PSS/E switch in substation {key.Substation}, nodes {key.FromNode}-{key.ToNode}, circuit '{key.CircuitId}'.");

        PsseTopologyNode[] nodes = topology.Nodes.OrderBy(n => n.Substation).ThenBy(n => n.Number).ToArray();
        var nodeIndex = new Dictionary<(int Substation, int Node), int>();
        for (int i = 0; i < nodes.Length; i++) nodeIndex.Add((nodes[i].Substation, nodes[i].Number), i);
        var nodeByIdentity = nodes.ToDictionary(n => (n.Substation, n.Number));
        var effectiveStates = switchStates.ToDictionary(kv => kv.Key, kv => kv.Value &&
            nodeByIdentity[(kv.Key.Substation, kv.Key.FromNode)].InService &&
            nodeByIdentity[(kv.Key.Substation, kv.Key.ToNode)].InService);
        var sets = new DisjointSet(nodes.Length);
        foreach ((PsseSwitchKey key, bool closed) in effectiveStates.OrderBy(x => x.Key.Substation)
                     .ThenBy(x => x.Key.FromNode).ThenBy(x => x.Key.ToNode).ThenBy(x => x.Key.CircuitId, StringComparer.Ordinal))
        {
            if (!closed) continue;
            int a = nodeIndex[(key.Substation, key.FromNode)];
            int b = nodeIndex[(key.Substation, key.ToNode)];
            sets.Union(a, b);
        }

        var componentNodes = new Dictionary<int, List<PsseTopologyNode>>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int root = sets.Find(i);
            if (!componentNodes.TryGetValue(root, out List<PsseTopologyNode>? members))
                componentNodes.Add(root, members = new List<PsseTopologyNode>());
            members.Add(nodes[i]);
        }
        List<List<PsseTopologyNode>> orderedComponents = componentNodes.Values
            .OrderBy(c => c[0].Substation).ThenBy(c => c[0].Number).ToList();
        var groups = new List<PsseConnectivityGroup>(orderedComponents.Count);
        var nodeToGroup = new Dictionary<(int Substation, int Node), int>();
        var busToGroups = new Dictionary<int, List<int>>();
        for (int i = 0; i < orderedComponents.Count; i++)
        {
            List<PsseTopologyNode> members = orderedComponents[i];
            int groupId = i + 1;
            foreach (PsseTopologyNode node in members)
            {
                nodeToGroup.Add((node.Substation, node.Number), groupId);
                if (!busToGroups.TryGetValue(node.Bus, out List<int>? busGroups))
                    busToGroups.Add(node.Bus, busGroups = new List<int>());
                if (busGroups.Count == 0 || busGroups[^1] != groupId) busGroups.Add(groupId);
            }
            groups.Add(new PsseConnectivityGroup(groupId, Array.AsReadOnly(members.ToArray()),
                Array.AsReadOnly(members.Select(n => n.Bus).Distinct().Order().ToArray())));
        }

        var terminalToGroup = new Dictionary<int, int>();
        var unmapped = new List<PsseTopologyTerminal>();
        foreach (PsseTopologyTerminal terminal in topology.Terminals)
        {
            PsseTopologyNode[] candidates = terminal.Node != 0
                ? nodes.Where(n => n.Substation == terminal.Substation && n.Number == terminal.Node).ToArray()
                : nodes.Where(n => n.Substation == terminal.Substation && n.Bus == terminal.Bus).ToArray();
            if (candidates.Length == 1)
                terminalToGroup.Add(terminal.SourceLine, nodeToGroup[(candidates[0].Substation, candidates[0].Number)]);
            else
                unmapped.Add(terminal);
        }
        return new PsseSubstationConnectivityResult(Array.AsReadOnly(groups.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<(int Substation, int Node), int>(nodeToGroup),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(terminalToGroup),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, IReadOnlyList<int>>(
                busToGroups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)Array.AsReadOnly(kv.Value.ToArray()))),
            new System.Collections.ObjectModel.ReadOnlyDictionary<PsseSwitchKey, bool>(switchStates.ToDictionary(kv => kv.Key, kv => kv.Value)),
            new System.Collections.ObjectModel.ReadOnlyDictionary<PsseSwitchKey, bool>(effectiveStates),
            Array.AsReadOnly(unmapped.ToArray()));
    }

    public static PsseSwitchKey Key(PsseTopologySwitch sw) =>
        new(sw.Substation, sw.FromNode, sw.ToNode, sw.CircuitId);

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public DisjointSet(int size)
        {
            _parent = Enumerable.Range(0, size).ToArray();
            _rank = new byte[size];
        }

        public int Find(int value)
        {
            int root = value;
            while (_parent[root] != root) root = _parent[root];
            while (_parent[value] != value)
            {
                int parent = _parent[value];
                _parent[value] = root;
                value = parent;
            }
            return root;
        }

        public void Union(int left, int right)
        {
            int a = Find(left), b = Find(right);
            if (a == b) return;
            if (_rank[a] < _rank[b]) (a, b) = (b, a);
            _parent[b] = a;
            if (_rank[a] == _rank[b]) _rank[a]++;
        }
    }
}
