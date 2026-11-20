using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>The in-service network is outside the method (a loop, a missing source, an unknown element). The study
/// reports OUT OF SCOPE with the message instead of a number.</summary>
public sealed class NetworkScopeException(string message) : Exception(message);

/// <summary>
/// RC4 R4-13: the radial supply tree of the in-service network, rooted at the single slack bus. Bus positions are indices
/// into <see cref="GridModel.Buses"/>; branches are indices into <see cref="GridModel.Branches"/>. Adjacency is visited in
/// ascending branch index, so every derived order is deterministic. A loop among the in-service branches reachable from
/// the slack is refused (normally-open ties must be stored out of service): the method needs a tree.
/// </summary>
internal sealed class FeederTree
{
    private readonly List<int>[] _adjacent;       // per bus position: in-service branch indices, ascending
    private readonly List<int>[] _children;       // per bus position: tree branches leaving it, ascending
    private readonly int[] _tin, _tout;

    public GridModel Model { get; }
    public int SlackPos { get; }
    public Dictionary<int, int> BusPos { get; } = [];
    public bool[] Reached { get; }
    public int[] ParentBranch { get; }            // per bus position, -1 for the root and unreached buses
    public int[] DownBus { get; }                 // per branch: bus position beyond the branch (away from the slack), -1 if not a tree edge
    public int[] UpBus { get; }                   // per branch: bus position on the source side, -1 if not a tree edge

    private FeederTree(GridModel model)
    {
        Model = model;
        int n = model.Buses.Count;
        for (int i = 0; i < n; i++) BusPos.TryAdd(model.Buses[i].Id, i);
        var slacks = Enumerable.Range(0, n).Where(i => model.Buses[i].Type == BusType.Slack).ToArray();
        if (slacks.Length != 1) throw new NetworkScopeException("Exactly one slack bus (the source of the radial network) is required.");
        SlackPos = slacks[0];
        _adjacent = new List<int>[n];
        _children = new List<int>[n];
        for (int i = 0; i < n; i++) { _adjacent[i] = []; _children[i] = []; }
        for (int b = 0; b < model.Branches.Count; b++)
        {
            Branch br = model.Branches[b];
            if (!br.InService || br.From == br.To) continue;
            if (!BusPos.TryGetValue(br.From, out int f) || !BusPos.TryGetValue(br.To, out int t))
                throw new NetworkScopeException($"Branch {b} joins bus {br.From}-{br.To}, which is not in the case.");
            _adjacent[f].Add(b);
            _adjacent[t].Add(b);
        }
        Reached = new bool[n];
        ParentBranch = Enumerable.Repeat(-1, n).ToArray();
        DownBus = Enumerable.Repeat(-1, model.Branches.Count).ToArray();
        UpBus = Enumerable.Repeat(-1, model.Branches.Count).ToArray();
        var order = new List<int> { SlackPos };
        Reached[SlackPos] = true;
        for (int head = 0; head < order.Count; head++)
        {
            int u = order[head];
            foreach (int b in _adjacent[u])
            {
                if (b == ParentBranch[u]) continue;
                Branch br = model.Branches[b];
                int v = BusPos[br.From] == u ? BusPos[br.To] : BusPos[br.From];
                if (Reached[v])
                    throw new NetworkScopeException($"The in-service network is not radial: branch {b} ({br.From}-{br.To}) closes a loop. Store normally-open ties out of service.");
                Reached[v] = true;
                ParentBranch[v] = b;
                DownBus[b] = v;
                UpBus[b] = u;
                _children[u].Add(b);
                order.Add(v);
            }
        }
        // Depth-first intervals for subtree membership.
        _tin = new int[n]; _tout = new int[n];
        int clock = 0;
        var stack = new Stack<(int Bus, int Next)>();
        stack.Push((SlackPos, 0));
        _tin[SlackPos] = clock++;
        while (stack.Count > 0)
        {
            var (bus, next) = stack.Pop();
            if (next < _children[bus].Count)
            {
                stack.Push((bus, next + 1));
                int child = DownBus[_children[bus][next]];
                _tin[child] = clock++;
                stack.Push((child, 0));
            }
            else _tout[bus] = clock++;
        }
    }

    public static FeederTree Build(GridModel model) => new(model);

    public IReadOnlyList<int> ChildBranches(int busPos) => _children[busPos];

    /// <summary>The tree branches from <paramref name="branch"/> up to the branch leaving the slack, nearest first.</summary>
    public List<int> PathToRoot(int branch)
    {
        var path = new List<int>();
        int b = branch;
        while (b >= 0)
        {
            path.Add(b);
            b = ParentBranch[UpBus[b]];
        }
        return path;
    }

    public bool InSubtree(int busPos, int rootBusPos) => _tin[busPos] >= _tin[rootBusPos] && _tout[busPos] <= _tout[rootBusPos];

    /// <summary>The bus positions at and below the downstream end of <paramref name="branch"/>, ascending.</summary>
    public SortedSet<int> SubtreeBuses(int branch)
    {
        int root = DownBus[branch];
        var set = new SortedSet<int>();
        for (int i = 0; i < Reached.Length; i++) if (Reached[i] && InSubtree(i, root)) set.Add(i);
        return set;
    }

    /// <summary>Bus positions reachable from <paramref name="starts"/> over in-service tree branches that are not blocked.</summary>
    public HashSet<int> Reach(IEnumerable<int> starts, ISet<int> blockedBranches, ISet<int>? within = null)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int s in starts) if ((within is null || within.Contains(s)) && seen.Add(s)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int b in _adjacent[u])
            {
                if (blockedBranches.Contains(b)) continue;
                Branch br = Model.Branches[b];
                int v = BusPos[br.From] == u ? BusPos[br.To] : BusPos[br.From];
                if (within is not null && !within.Contains(v)) continue;
                if (seen.Add(v)) queue.Enqueue(v);
            }
        }
        return seen;
    }
}
