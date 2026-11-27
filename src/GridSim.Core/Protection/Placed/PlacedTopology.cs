using System.Collections.Generic;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Workflow;

namespace GridSim.Core.Protection.Placed;

/// <summary>The supply-path rule of the short-circuit trace (<see cref="ShortCircuitTrace"/>), the same logic copied
/// here so that file stays untouched: a device's direction by the bridge rule on the fault network, and whether one
/// device is downstream of another.</summary>
internal static class PlacedTopology
{
    /// <summary>The fault-network branch a device sits on (the second segment for a to-end device on a split branch).</summary>
    internal static int FaultedBranch(FaultNetwork net, PlacedDevice d) =>
        d.Location.AtFromEnd ? FaultNetwork.FromSegment(d.Location.BranchIndex) : net.ToSegment(d.Location.BranchIndex);

    internal static HashSet<int> Reach(GridModel m, int excludedBranch, int start)
    {
        var adj = new Dictionary<int, List<int>>();
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (k == excludedBranch || !br.InService) continue;
            if (!adj.TryGetValue(br.From, out var a)) adj[br.From] = a = new List<int>();
            if (!adj.TryGetValue(br.To, out var c)) adj[br.To] = c = new List<int>();
            a.Add(br.To); c.Add(br.From);
        }
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (!adj.TryGetValue(u, out var nbrs)) continue;
            foreach (int v in nbrs) if (seen.Add(v)) stack.Push(v);
        }
        return seen;
    }

    internal static (int Near, int Far) Ends(FaultNetwork net, PlacedDevice d, int faultedBranch)
    {
        Branch fb = net.Model.Branches[faultedBranch];
        return d.Location.AtFromEnd ? (fb.From, fb.To) : (fb.To, fb.From);
    }

    /// <summary>True when the fault is beyond the device, false when behind it, null in a mesh (not resolved).</summary>
    internal static bool? Direction(FaultNetwork net, PlacedDevice d, int faultedBranch)
    {
        (int near, int far) = Ends(net, d, faultedBranch);
        HashSet<int> beyond = Reach(net.Model, faultedBranch, far);
        if (beyond.Contains(near))
            return net.Inserted && d.Location.BranchIndex == net.SplitBranch ? true : null;
        return beyond.Contains(net.FaultBusId);
    }

    /// <summary>Whether device <paramref name="a"/> is downstream of device <paramref name="b"/>.</summary>
    internal static bool IsDownstream(FaultNetwork net, IReadOnlyList<PlacedDevice> devs, int[] fb, int a, int b)
    {
        (int nearA, _) = Ends(net, devs[a], fb[a]);
        (_, int farB) = Ends(net, devs[b], fb[b]);
        return Reach(net.Model, fb[b], farB).Contains(nearA);
    }
}
