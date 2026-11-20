using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Contingency;

/// <summary>
/// Shared, read-only helpers of the R4-03 outage studies: the outage copy of a model (the branch removed from the list,
/// exactly as <see cref="ContingencyAnalysis"/> builds it, asset dynamics and protections carried), the connectivity test
/// that classifies an islanding outage BEFORE any algebra, and the request set (all in-service branches in model order
/// unless a list is given; an out-of-service branch named in a list is refused by name).
/// </summary>
internal static class OutageModels
{
    /// <summary>The model with branch <paramref name="branch"/> removed (model order of the others kept).</summary>
    public static GridModel Without(GridModel model, int branch)
    {
        var branches = new List<Branch>(model.Branches.Count - 1);
        for (int j = 0; j < model.Branches.Count; j++) if (j != branch) branches.Add(model.Branches[j]);
        return model.CopyWith(branches: branches);
    }

    /// <summary>A short label of branch <paramref name="index"/>: its name, else "from-to" (the contingency verbs' rule).</summary>
    public static string Label(GridModel model, int index)
    {
        Branch b = model.Branches[index];
        return (string.IsNullOrEmpty(b.Name) ? $"{b.From}-{b.To}" : b.Name) + (b.IsTransformer ? " (tx)" : "");
    }

    /// <summary>The outage set: <paramref name="requested"/> in the order given (each in range, in service, no
    /// duplicates), or every in-service branch in model order when null.</summary>
    public static int[] Resolve(GridModel model, IReadOnlyList<int>? requested)
    {
        if (requested is null) return ContingencyAnalysis.InServiceBranchIndices(model);
        var seen = new HashSet<int>();
        foreach (int i in requested)
        {
            if (i < 0 || i >= model.Branches.Count)
                throw new ArgumentOutOfRangeException(nameof(requested), i, $"branch index {i} is outside 0..{model.Branches.Count - 1}.");
            if (!seen.Add(i)) throw new ArgumentException($"branch {i} is listed twice.", nameof(requested));
        }
        ContingencyAnalysis.RequireInServiceOutages(model, requested, nameof(requested));
        return requested.ToArray();
    }

    /// <summary>True when removing <paramref name="branch"/> splits the connected component its end buses lie in (graph
    /// connectivity over every in-service branch; a parallel circuit keeps the two buses joined).</summary>
    public static bool Islands(GridModel model, int branch)
    {
        Branch br = model.Branches[branch];
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        if (!index.TryGetValue(br.From, out int from) || !index.TryGetValue(br.To, out int to)) return false;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        for (int l = 0; l < model.Branches.Count; l++)
        {
            Branch b = model.Branches[l];
            if (l == branch || !b.InService) continue;
            if (!index.TryGetValue(b.From, out int f) || !index.TryGetValue(b.To, out int t)) continue;
            adj[f].Add(t); adj[t].Add(f);
        }
        var seen = new bool[n];
        var stack = new Stack<int>();
        stack.Push(from);
        seen[from] = true;
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            foreach (int w in adj[u]) if (!seen[w]) { seen[w] = true; stack.Push(w); }
        }
        return !seen[to];
    }
}
