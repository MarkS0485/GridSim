using GridSim.Core.Layout;
using GridSim.Core.Model;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>
/// The automatic single-line layout (R4-18): a pure function of the <see cref="SldModel"/> and the <see cref="SldOptions"/>. Same
/// model, same options, same numbers to the last bit, whatever the order a dictionary or a hash happens to iterate in (nothing here
/// iterates a dictionary, sorts without a total order, or uses chance).
/// <list type="number">
/// <item>Unconnected parts of the case (components, in order of their first node) are laid out one below another.</item>
/// <item>Rows: when a part has two or more distinct nominal voltages the rows are the voltage levels, highest at the top; otherwise
/// the rows are the display tiers of <see cref="TopologyTiers"/> (the same hierarchy the map uses), so a one-voltage case still reads
/// as a hierarchy.</item>
/// <item>Order within a row: breadth-first discovery from the slack bus (or the first node), then four barycentre sweeps (down, up,
/// down, up) with stable ties, which is the usual layered-drawing crossing reduction.</item>
/// <item>Widths follow the number of connection slots a busbar needs; rows are centred on x = 0; the gap between two rows is as deep as
/// its busiest channel needs, so the router always finds a free track.</item>
/// </list>
/// </summary>
public static class SldAutoLayout
{
    private const int Sweeps = 4;

    public static SldPlacement Place(SldModel model, SldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        SldOptions o = options ?? new SldOptions();
        int n = model.Nodes.Count;
        var px = new double[n];
        var py = new double[n];
        if (n == 0) return new SldPlacement(px, py, "auto");

        // Adjacency over every edge (an open switch or an out-of-service branch still joins its ends on the page), ascending, no repeats.
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (SldElement e in model.Elements)
            if (e.IsEdge && e.Node != e.OtherNode) { adj[e.Node].Add(e.OtherNode); adj[e.OtherNode].Add(e.Node); }
        for (int i = 0; i < n; i++) adj[i] = adj[i].Distinct().OrderBy(v => v).ToList();

        int[] comp = new int[n];
        Array.Fill(comp, -1);
        var components = new List<List<int>>();
        for (int s = 0; s < n; s++)
        {
            if (comp[s] >= 0) continue;
            var members = new List<int>();
            var queue = new Queue<int>();
            comp[s] = components.Count; queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                members.Add(u);
                foreach (int v in adj[u]) if (comp[v] < 0) { comp[v] = components.Count; queue.Enqueue(v); }
            }
            members.Sort();
            components.Add(members);
        }

        int slack = -1;
        if (model.Grid is { } grid)
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int bi = IndexOfBus(grid, model.Nodes[i].Key);
                if (bi >= 0 && grid.Buses[bi].Type == BusType.Slack) { slack = i; break; }
            }

        // Row of every node (component-local rows 0..), the BFS rank, and the rows' member lists in current order.
        var row = new int[n];
        var rank = new int[n];
        var rowsOf = new List<List<int>>[components.Count];
        for (int c = 0; c < components.Count; c++)
        {
            List<int> members = components[c];
            int root = slack >= 0 && comp[slack] == c ? slack : members[0];
            AssignRows(model, adj, members, root, row);
            BfsRank(adj, members, root, rank);
            int rowCount = members.Max(v => row[v]) + 1;
            var rows = new List<List<int>>();
            for (int r = 0; r < rowCount; r++) rows.Add(members.Where(v => row[v] == r).OrderBy(v => rank[v]).ThenBy(v => v).ToList());
            rowsOf[c] = rows;
        }

        // Slot counts per node need only the rows (y = row number, tolerance below one row apart).
        var rowY = new double[n];
        for (int i = 0; i < n; i++) rowY[i] = row[i];
        (List<SldAttachment>[] top, List<SldAttachment>[] bottom) = SldAttachments.Analyse(model, new double[n], rowY, 0.5);
        var width = new double[n];
        for (int i = 0; i < n; i++) width[i] = o.BusbarWidth(Math.Max(top[i].Count, bottom[i].Count));

        // Tracks needed in the channel below each row (per component): every edge whose first end is in the row, plus the second
        // horizontal run of every edge that passes at least one whole row.
        double yCursor = 0;
        for (int c = 0; c < components.Count; c++)
        {
            List<List<int>> rows = rowsOf[c];
            int rc = rows.Count;
            var tracks = new int[rc];
            foreach (SldElement e in model.Elements)
            {
                if (!e.IsEdge || comp[e.Node] != c) continue;
                (int a, int b, bool same) = SldAttachments.Ends(e, rowY, 0.5);
                tracks[row[a]]++;
                if (!same && row[b] - row[a] >= 2) tracks[row[b] - 1]++;
            }

            // x: centre each row on 0, then sweep.
            var x = new double[n];
            foreach (List<int> r in rows) Pack(r, width, o, x);
            for (int s = 0; s < Sweeps; s++)
            {
                bool down = s % 2 == 0;
                if (down) { for (int r = 1; r < rc; r++) Reorder(model, rows[r], x, row, comp, c, true); }
                else { for (int r = rc - 2; r >= 0; r--) Reorder(model, rows[r], x, row, comp, c, false); }
                for (int r = 0; r < rc; r++) Pack(rows[r], width, o, x);
            }

            // y: rows one below another, each gap as deep as its channel needs.
            double y = yCursor;
            for (int r = 0; r < rc; r++)
            {
                foreach (int v in rows[r]) { py[v] = y; px[v] = x[v]; }
                double bottomY = y + o.BusbarThickness / 2;
                y = bottomY + o.Drop + tracks[r] * o.TrackPitch + o.RowClearance + o.BusbarThickness / 2;
            }
            // The next component starts below the hanging symbols of this one's last row.
            double lastBottom = py[rows[rc - 1][0]] + o.BusbarThickness / 2;
            yCursor = lastBottom + o.Drop + o.ComponentGap + o.BusbarThickness / 2;
        }
        return new SldPlacement(px, py, "auto");
    }

    private static int IndexOfBus(GridModel g, int id)
    {
        for (int i = 0; i < g.Buses.Count; i++) if (g.Buses[i].Id == id) return i;
        return -1;
    }

    /// <summary>Rows of one component: its voltage levels (highest first) when there are two or more, else its topology tiers.</summary>
    private static void AssignRows(SldModel model, List<int>[] adj, List<int> members, int root, int[] row)
    {
        var kv = members.Select(v => model.Nodes[v].BaseKv).Distinct().OrderByDescending(v => v).ToList();
        if (kv.Count >= 2)
        {
            foreach (int v in members) row[v] = kv.IndexOf(model.Nodes[v].BaseKv);
            return;
        }
        // One voltage: the display tiers of the map layout, over the component's own graph.
        var local = new Dictionary<int, int>();
        for (int i = 0; i < members.Count; i++) local[members[i]] = i;
        var ladj = new IReadOnlyList<int>[members.Count];
        for (int i = 0; i < members.Count; i++) ladj[i] = adj[members[i]].Select(v => local[v]).ToList();
        int[] tier = TopologyTiers.Assign(members.Count, ladj, local[root]);
        var used = tier.Distinct().OrderBy(t => t).ToList();
        for (int i = 0; i < members.Count; i++) row[members[i]] = used.IndexOf(tier[i]);
    }

    private static void BfsRank(List<int>[] adj, List<int> members, int root, int[] rank)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        int next = 0;
        void Run(int start)
        {
            if (!seen.Add(start)) return;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                rank[u] = next++;
                foreach (int v in adj[u]) if (seen.Add(v)) queue.Enqueue(v);
            }
        }
        Run(root);
        foreach (int v in members) Run(v);
    }

    /// <summary>Place a row's busbars left to right with the standard gap, the row centred on x = 0.</summary>
    private static void Pack(List<int> row, double[] width, SldOptions o, double[] x)
    {
        double total = row.Sum(v => width[v]) + (row.Count - 1) * o.BusGap;
        double left = -total / 2;
        foreach (int v in row)
        {
            x[v] = left + width[v] / 2;
            left += width[v] + o.BusGap;
        }
    }

    /// <summary>Sort a row by the mean x of each node's neighbours in the adjacent row (above when sweeping down, below when sweeping up); a node with none keeps its place.</summary>
    private static void Reorder(SldModel model, List<int> r, double[] x, int[] row, int[] comp, int c, bool down)
    {
        if (r.Count < 2) return;
        int myRow = row[r[0]];
        var key = new double[model.Nodes.Count];
        foreach (int v in r) key[v] = double.NaN;
        var sum = new Dictionary<int, double>();
        var count = new Dictionary<int, int>();
        foreach (SldElement e in model.Elements)
        {
            if (!e.IsEdge || e.Node == e.OtherNode) continue;
            foreach ((int me, int other) in new[] { (e.Node, e.OtherNode), (e.OtherNode, e.Node) })
            {
                if (row[me] != myRow || comp[me] != c) continue;
                bool wanted = down ? row[other] < myRow : row[other] > myRow;
                if (!wanted) continue;
                sum[me] = sum.GetValueOrDefault(me) + x[other];
                count[me] = count.GetValueOrDefault(me) + 1;
            }
        }
        var before = r.ToList();
        var ordered = before.Select((v, i) => (V: v, Pos: i, K: count.TryGetValue(v, out int k) && k > 0 ? sum[v] / k : x[v])).ToList();
        ordered.Sort((p, q) =>
        {
            int cmp = p.K.CompareTo(q.K);
            if (cmp != 0) return cmp;
            cmp = p.Pos.CompareTo(q.Pos);
            return cmp != 0 ? cmp : p.V.CompareTo(q.V);
        });
        for (int i = 0; i < r.Count; i++) r[i] = ordered[i].V;
    }
}
