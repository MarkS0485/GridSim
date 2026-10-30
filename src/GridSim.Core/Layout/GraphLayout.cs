using GridSim.Core.Model;

namespace GridSim.Core.Layout;

/// <summary>
/// Deterministic auto-layout for coordinate-less networks.
///
/// Power-system cases imported from IEEE / MATPOWER carry no geographic
/// coordinates, so every <see cref="Bus"/> lands at the origin (X = Y = 0) and the
/// WPF map draws them all stacked on one point. This class synthesises a readable,
/// London-Underground-style planar layout from the pure graph topology
/// (buses = nodes, in-service branches = edges) using a
/// <b>Fruchterman-Reingold force-directed</b> algorithm, normalised into the 0..1
/// coordinate box the renderer expects.
///
/// Everything here is <b>deterministic</b>: given the same model and seed it always
/// produces byte-identical coordinates. No <see cref="System.Random"/> without a
/// seed, no <see cref="System.DateTime"/>, no hashing of unordered collections.
///
/// Pure Core - no UI dependencies.
/// </summary>
public static class GraphLayout
{
    /// <summary>Above this bus count, Fruchterman-Reingold's O(n^2) inner loop is too
    /// slow to be worth it; we fall back to a linear-time concentric-ring layout.</summary>
    private const int LargeGraphThreshold = 2000;

    /// <summary>Force-directed iteration budget. Enough for the layout to settle on the
    /// small/medium IEEE cases (up to ~2000 buses) while staying fast.</summary>
    private const int FrIterations = 450;

    /// <summary>Fraction of the unit box left empty around the drawn network so nodes
    /// don't touch the frame edge.</summary>
    private const double Margin = 0.04;

    /// <summary>
    /// True when the model carries no layout at all - every bus sits exactly at the
    /// origin. This is the signature of a MATPOWER/IEEE import that never had
    /// coordinates. GB models (which ship real coords) return false and are left alone.
    /// </summary>
    public static bool HasNoLayout(GridModel m)
    {
        // A single bus not at the origin, or any non-zero coordinate anywhere, means the
        // model already has (at least a partial) layout that we must not clobber.
        foreach (Bus b in m.Buses)
            if (b.X != 0 || b.Y != 0)
                return false;
        return true;
    }

    /// <summary>
    /// Ensure the model has drawable coordinates. If it already has a layout
    /// (see <see cref="HasNoLayout"/>) the model is returned unchanged. Otherwise a
    /// deterministic layout is computed from the graph topology and an <b>immutable
    /// copy</b> is returned with every <see cref="Bus.X"/>/<see cref="Bus.Y"/> filled
    /// in, normalised into the [0,1] box (with a small margin).
    /// </summary>
    /// <param name="m">The model to lay out.</param>
    /// <param name="seed">Seed for the deterministic initial placement.</param>
    public static GridModel EnsureLayout(GridModel m, int seed = 20260702)
    {
        if (!HasNoLayout(m))
            return m;                      // already positioned - respect existing coords
        if (m.Buses.Count == 0)
            return m;                      // nothing to place

        int n = m.Buses.Count;

        // ---- Index the buses and build the (undirected) adjacency from branches. ----
        // Bus ids are arbitrary integers; map each to a dense 0..n-1 index. Branches whose
        // endpoints don't resolve to a real bus are skipped (Validate() reports those).
        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            idToIndex[m.Buses[i].Id] = i;   // last-wins on duplicate ids (matches solver index map)

        var adjacency = new List<int>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<int>();
        foreach (Branch br in m.Branches)
        {
            // "In-service branches = edges": every IN-SERVICE branch that connects two distinct, real buses
            // becomes an edge. An opened branch (Branch.InService == false: a tripped tie, a switched-out
            // circuit) carries no flow and no electrical connection, so it must not pull its ends together or
            // merge two separate islands into one component. (The comment here used to say the model had no
            // out-of-service flag; it has had Branch.InService for some time - R273.)
            if (!br.InService) continue;
            if (idToIndex.TryGetValue(br.From, out int a) &&
                idToIndex.TryGetValue(br.To, out int b) && a != b)
            {
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }
        }

        // ---- Split into connected components so disconnected islands don't overlap. ----
        int[] component = LabelComponents(adjacency, n, out int componentCount);
        var members = new List<int>[componentCount];
        for (int c = 0; c < componentCount; c++) members[c] = new List<int>();
        for (int i = 0; i < n; i++) members[component[i]].Add(i);

        // Pick a preferred root per component (the slack bus if one lives there) for the
        // ring fallback's BFS centre.
        int slackIndex = -1;
        for (int i = 0; i < n; i++)
            if (m.Buses[i].Type == BusType.Slack) { slackIndex = i; break; }

        bool large = n > LargeGraphThreshold;

        // ---- Lay out each component independently in its own local [0,1] box. ----
        var px = new double[n];
        var py = new double[n];
        for (int c = 0; c < componentCount; c++)
        {
            List<int> comp = members[c];
            int root = (slackIndex >= 0 && component[slackIndex] == c) ? slackIndex : comp[0];

            // Is this component a tree? (radial distribution feeders are.) A tree of c nodes has
            // exactly c-1 internal edges. Trees lay out far more clearly as a fanned radial diagram
            // than under force-direction, which leaves a chain looking like a limp string.
            long compEdges = 0;
            foreach (int i in comp) compEdges += adjacency[i].Count;
            compEdges /= 2;
            bool isTree = compEdges == comp.Count - 1;

            if (large)
                RingLayout(comp, adjacency, root, px, py);
            else if (isTree && comp.Count >= 3)
                RadialTreeLayout(comp, adjacency, root, px, py);
            else
                // Seed per component so the whole layout is deterministic yet each island
                // gets its own distinct pseudo-random starting configuration.
                FruchtermanReingold(comp, adjacency, seed + c, px, py);

            NormalizeToUnitBox(comp, px, py);   // each island -> its own [0,1] square
        }

        // ---- Pack the component boxes into a tidy grid so islands sit side by side. ----
        // cols x rows grid of unit cells; component c occupies one cell. A one-component
        // graph (the usual case) is a single cell = the whole box.
        int cols = (int)Math.Ceiling(Math.Sqrt(componentCount));
        if (cols < 1) cols = 1;
        for (int c = 0; c < componentCount; c++)
        {
            int cellX = c % cols;
            int cellY = c / cols;
            foreach (int i in members[c])
            {
                px[i] += cellX;   // shift this island into its grid cell
                py[i] += cellY;
            }
        }

        // ---- Final global normalisation into [Margin, 1-Margin] on both axes. ----
        NormalizeToBox(px, py, n, Margin);

        // Guarantee distinct coordinates: identical points are visually useless (and the
        // tests assert distinctness). Collisions are extremely unlikely after FR, but a
        // degenerate island (e.g. every node on one axis) can produce ties, so nudge any
        // duplicate by a tiny deterministic per-index offset that stays inside the box.
        DeduplicatePositions(px, py, n);

        // ---- Emit an immutable copy with coordinates filled in. ----
        var newBuses = new Bus[n];
        for (int i = 0; i < n; i++)
            newBuses[i] = m.Buses[i] with { X = px[i], Y = py[i] };

        // Layout is display-only - it must never strip authored physics. (Hand-built copies used to delete every placed
        // protective device and asset-dynamics class from any coordinate-less case at load; N094: and the
        // out-of-service generators. CopyWith carries every property.)
        return m.CopyWith(buses: newBuses);
    }

    /// <summary>
    /// Label connected components with an iterative BFS flood-fill.
    /// Returns an array mapping bus index -> component id, and the component count.
    /// </summary>
    private static int[] LabelComponents(List<int>[] adjacency, int n, out int count)
    {
        var comp = new int[n];
        for (int i = 0; i < n; i++) comp[i] = -1;
        count = 0;
        var queue = new Queue<int>();
        for (int start = 0; start < n; start++)
        {
            if (comp[start] != -1) continue;
            int id = count++;
            comp[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                foreach (int v in adjacency[u])
                    if (comp[v] == -1) { comp[v] = id; queue.Enqueue(v); }
            }
        }
        return comp;
    }

    /// <summary>
    /// Fruchterman-Reingold force-directed layout for one connected component, writing
    /// results into <paramref name="px"/>/<paramref name="py"/> at each node's global index.
    ///
    /// Physical analogy: nodes are equally-charged particles that repel each other, while
    /// edges act as springs pulling adjacent nodes together. The system is simulated for a
    /// fixed number of iterations with a cooling "temperature" that caps how far any node
    /// may move per step, so the layout settles instead of oscillating.
    /// </summary>
    private static void FruchtermanReingold(
        List<int> comp, List<int>[] adjacency, int seed, double[] px, double[] py)
    {
        int m = comp.Count;
        if (m == 1) { px[comp[0]] = 0.5; py[comp[0]] = 0.5; return; }

        // Work in a local 0..1 square. k is the ideal edge length: the spacing at which
        // repulsion and attraction balance for this node count in a unit-area frame.
        const double width = 1.0, height = 1.0;
        double area = width * height;
        double k = Math.Sqrt(area / m);
        double kSq = k * k;

        // Local scratch arrays indexed 0..m-1 (mapped back to global indices via comp[]).
        var lx = new double[m];
        var ly = new double[m];
        var dx = new double[m];
        var dy = new double[m];

        // Deterministic initial placement from a seeded RNG - the only source of randomness.
        var rng = new Random(seed);
        for (int i = 0; i < m; i++)
        {
            lx[i] = rng.NextDouble() * width;
            ly[i] = rng.NextDouble() * height;
        }

        // Local adjacency (global index -> local index) so edge forces are cheap.
        var localOf = new Dictionary<int, int>(m);
        for (int i = 0; i < m; i++) localOf[comp[i]] = i;

        // Initial temperature ~ a tenth of the frame; cooled linearly to zero.
        double t0 = width * 0.1;

        // The O(m^2) inner loop dominates load time; scale the iteration budget down for large
        // components (which never settle into a tidy shape anyway) so a big case doesn't stall the
        // UI thread, while small/medium cases keep the full budget for a clean layout.
        int iterations = Math.Clamp(120_000 / m, 90, FrIterations);

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < m; i++) { dx[i] = 0; dy[i] = 0; }

            // Repulsive forces between every pair of nodes: f_r(d) = k^2 / d.
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 1; j < m; j++)
                {
                    double ddx = lx[i] - lx[j];
                    double ddy = ly[i] - ly[j];
                    double dist2 = ddx * ddx + ddy * ddy;
                    if (dist2 < 1e-12)
                    {
                        // Coincident nodes: apply a tiny deterministic split so they separate.
                        ddx = (i - j) * 1e-4 + 1e-5;
                        ddy = (i + j) * 1e-4 + 1e-5;
                        dist2 = ddx * ddx + ddy * ddy;
                    }
                    double dist = Math.Sqrt(dist2);
                    double force = kSq / dist;           // repulsion magnitude
                    double ux = ddx / dist, uy = ddy / dist;
                    dx[i] += ux * force; dy[i] += uy * force;
                    dx[j] -= ux * force; dy[j] -= uy * force;
                }
            }

            // Attractive forces along edges: f_a(d) = d^2 / k. Iterate each undirected edge
            // once (only when the neighbour's local index is greater) to avoid double count.
            for (int i = 0; i < m; i++)
            {
                int gi = comp[i];
                foreach (int gv in adjacency[gi])
                {
                    int j = localOf[gv];
                    if (j <= i) continue;
                    double ddx = lx[i] - lx[j];
                    double ddy = ly[i] - ly[j];
                    double dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                    if (dist < 1e-9) continue;
                    double force = (dist * dist) / k;    // attraction magnitude
                    double ux = ddx / dist, uy = ddy / dist;
                    dx[i] -= ux * force; dy[i] -= uy * force;
                    dx[j] += ux * force; dy[j] += uy * force;
                }
            }

            // Gentle gravity toward the centre. Without it, mutual repulsion drives every node
            // out to the clamp frame, giving a hollow, boxy perimeter; a mild centre-pull keeps
            // the network compact and rounder, closer to the clustered GB look.
            const double gravity = 0.30;
            double cx = width * 0.5, cy = height * 0.5;
            for (int i = 0; i < m; i++)
            {
                dx[i] += (cx - lx[i]) * gravity;
                dy[i] += (cy - ly[i]) * gravity;
            }

            // Cool down and apply displacement, capped by the current temperature.
            double t = t0 * (1.0 - (double)iter / iterations);
            for (int i = 0; i < m; i++)
            {
                double disp = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (disp < 1e-12) continue;
                double capped = Math.Min(disp, t);
                lx[i] += (dx[i] / disp) * capped;
                ly[i] += (dy[i] / disp) * capped;
                // Generous frame (gravity does the real containing); only stops a runaway node.
                lx[i] = Math.Clamp(lx[i], -0.25, width + 0.25);
                ly[i] = Math.Clamp(ly[i], -0.25, height + 0.25);
            }
        }

        for (int i = 0; i < m; i++) { px[comp[i]] = lx[i]; py[comp[i]] = ly[i]; }
    }

    /// <summary>
    /// Fast O(n) fallback for very large graphs: place nodes on concentric rings by their
    /// BFS distance (hop count) from the component root. The root sits at the centre and
    /// each successive shell of neighbours fans out on a wider circle - a quick, tidy,
    /// deterministic layout that returns instantly where FR would crawl.
    /// </summary>
    private static void RingLayout(
        List<int> comp, List<int>[] adjacency, int root, double[] px, double[] py)
    {
        int m = comp.Count;
        if (m == 1) { px[comp[0]] = 0.5; py[comp[0]] = 0.5; return; }

        // BFS distance from the root.
        var dist = new Dictionary<int, int>(m);
        foreach (int g in comp) dist[g] = -1;
        dist[root] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(root);
        int maxRing = 0;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in adjacency[u])
                if (dist.TryGetValue(v, out int dv) && dv == -1)
                {
                    dist[v] = dist[u] + 1;
                    if (dist[v] > maxRing) maxRing = dist[v];
                    queue.Enqueue(v);
                }
        }

        // Group nodes by ring, preserving deterministic (comp order) placement within a ring.
        var rings = new List<int>[maxRing + 1];
        for (int r = 0; r <= maxRing; r++) rings[r] = new List<int>();
        foreach (int g in comp)
        {
            int d = dist[g];
            if (d < 0) d = maxRing;    // unreachable (shouldn't happen within a component)
            rings[d].Add(g);
        }

        double radiusStep = (maxRing > 0) ? 1.0 / maxRing : 1.0;
        for (int r = 0; r <= maxRing; r++)
        {
            List<int> ring = rings[r];
            int count = ring.Count;
            double radius = r * radiusStep;
            // Offset each ring's start angle so successive shells don't line up into spokes.
            double angleOffset = r * 0.5;
            for (int j = 0; j < count; j++)
            {
                double angle = angleOffset + (count > 0 ? 2.0 * Math.PI * j / count : 0);
                px[ring[j]] = radius * DetMath.Cos(angle);
                py[ring[j]] = radius * DetMath.Sin(angle);
            }
        }
    }

    /// <summary>
    /// Radial "tidy tree" layout for a tree component: the root sits at the centre, depth maps to
    /// radius, and each subtree is given an angular sector proportional to how many leaves it
    /// contains (so dense branches fan wider and don't collide). This turns a distribution feeder
    /// into the familiar fanned diagram - a trunk with laterals radiating out - instead of the
    /// string FR produces for an acyclic graph. Deterministic: children are visited in index order.
    /// </summary>
    private static void RadialTreeLayout(
        List<int> comp, List<int>[] adjacency, int root, double[] px, double[] py)
    {
        int m = comp.Count;
        if (m == 1) { px[comp[0]] = 0.5; py[comp[0]] = 0.5; return; }

        // ---- root the tree: parent, children (index-sorted), depth, via BFS ----
        var parent = new Dictionary<int, int>(m);
        var children = new Dictionary<int, List<int>>(m);
        var depth = new Dictionary<int, int>(m);
        foreach (int g in comp) children[g] = new List<int>();
        parent[root] = -1; depth[root] = 0;
        var bfs = new Queue<int>();
        bfs.Enqueue(root);
        var order = new List<int>(m);   // BFS order (parents before children)
        int maxDepth = 0;
        while (bfs.Count > 0)
        {
            int v = bfs.Dequeue();
            order.Add(v);
            var neigh = new List<int>(adjacency[v]);
            neigh.Sort();                       // deterministic child order
            foreach (int w in neigh)
                if (!parent.ContainsKey(w))
                {
                    parent[w] = v;
                    depth[w] = depth[v] + 1;
                    if (depth[w] > maxDepth) maxDepth = depth[w];
                    children[v].Add(w);
                    bfs.Enqueue(w);
                }
        }

        // ---- leaf counts (post-order: reverse BFS order visits children before parents) ----
        var leaves = new Dictionary<int, double>(m);
        foreach (int g in comp) leaves[g] = 0;
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int v = order[i];
            if (children[v].Count == 0) leaves[v] = 1;
            else { double s = 0; foreach (int c in children[v]) s += leaves[c]; leaves[v] = s; }
        }

        // ---- assign angular sectors top-down; angle = sector midpoint, radius = depth ----
        var a0 = new Dictionary<int, double>(m);
        var a1 = new Dictionary<int, double>(m);
        a0[root] = 0.0; a1[root] = 2.0 * Math.PI;
        double rStep = maxDepth > 0 ? 1.0 / maxDepth : 1.0;
        foreach (int v in order)   // parents before children, so sectors are known when we reach a node
        {
            double lo = a0[v], hi = a1[v];
            double span = hi - lo;
            double cursor = lo;
            double total = leaves[v] > 0 ? leaves[v] : 1;
            foreach (int c in children[v])
            {
                double w = leaves[c] / total * span;
                a0[c] = cursor; a1[c] = cursor + w;
                cursor += w;
            }
            double mid = (lo + hi) * 0.5;
            double radius = depth[v] * rStep;
            px[v] = 0.5 + radius * DetMath.Cos(mid) * 0.5;
            py[v] = 0.5 + radius * DetMath.Sin(mid) * 0.5;
        }
    }

    /// <summary>Normalise one component into [0,1]^2 with a single <b>uniform</b> scale (both axes
    /// divided by the larger span) and centred, so the layout keeps its natural aspect ratio
    /// instead of being stretched to fill a square. A degenerate component collapses to the centre.</summary>
    private static void NormalizeToUnitBox(List<int> comp, double[] px, double[] py)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (int i in comp)
        {
            if (px[i] < minX) minX = px[i];
            if (px[i] > maxX) maxX = px[i];
            if (py[i] < minY) minY = py[i];
            if (py[i] > maxY) maxY = py[i];
        }
        double rx = maxX - minX, ry = maxY - minY;
        double range = Math.Max(rx, ry);
        if (range < 1e-12) { foreach (int i in comp) { px[i] = 0.5; py[i] = 0.5; } return; }
        double ox = (1.0 - rx / range) / 2.0, oy = (1.0 - ry / range) / 2.0;
        foreach (int i in comp)
        {
            px[i] = ox + (px[i] - minX) / range;
            py[i] = oy + (py[i] - minY) / range;
        }
    }

    /// <summary>Normalise all nodes into [margin, 1-margin] with a single <b>uniform</b> scale and
    /// centred, preserving the true aspect ratio (no per-axis stretch, which would make a round
    /// network read as a boxy rectangle). A degenerate layout collapses to the box centre.</summary>
    private static void NormalizeToBox(double[] px, double[] py, int n, double margin)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            if (px[i] < minX) minX = px[i];
            if (px[i] > maxX) maxX = px[i];
            if (py[i] < minY) minY = py[i];
            if (py[i] > maxY) maxY = py[i];
        }
        double span = 1.0 - 2 * margin;
        double rx = maxX - minX, ry = maxY - minY;
        double range = Math.Max(rx, ry);
        if (range < 1e-12) { for (int i = 0; i < n; i++) { px[i] = 0.5; py[i] = 0.5; } return; }
        double scale = span / range;
        double offX = margin + (span - rx * scale) / 2.0;
        double offY = margin + (span - ry * scale) / 2.0;
        for (int i = 0; i < n; i++)
        {
            px[i] = offX + (px[i] - minX) * scale;
            py[i] = offY + (py[i] - minY) * scale;
        }
    }

    /// <summary>
    /// Ensure no two nodes share the exact same coordinate. Any exact duplicate is nudged
    /// by a tiny deterministic offset derived from its index, staying safely inside the box.
    /// </summary>
    private static void DeduplicatePositions(double[] px, double[] py, int n)
    {
        var seen = new HashSet<(double, double)>();
        for (int i = 0; i < n; i++)
        {
            var key = (px[i], py[i]);
            if (seen.Add(key)) continue;
            // Deterministic spiral nudge; magnitudes are tiny so points stay in [0,1].
            double eps = 1e-6 * (i + 1);
            double nx = Math.Clamp(px[i] + eps, 0.0, 1.0);
            double ny = Math.Clamp(py[i] + eps, 0.0, 1.0);
            while (!seen.Add((nx, ny)))
            {
                eps += 1e-6;
                nx = Math.Clamp(px[i] + eps, 0.0, 1.0);
                ny = Math.Clamp(py[i] - eps, 0.0, 1.0);
            }
            px[i] = nx; py[i] = ny;
        }
    }
}
