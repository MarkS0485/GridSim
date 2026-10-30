using GridSim.Core.Layout;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Ui.Schematic;

/// <summary>A point in screen space (device-independent pixels, x right, y down), with no user-interface framework
/// behind it. It carries the two doubles the WPF <c>System.Windows.Point</c> carried, so a front end converting it to its
/// own point type gets the same numbers bit for bit.</summary>
/// <param name="X">Screen x, pixels from the map's left edge.</param>
/// <param name="Y">Screen y, pixels from the map's top edge.</param>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>One bus of the map, in WORLD coordinates (the old FastSchematic <c>NodeRec</c>, field for field).</summary>
/// <remarks>The old record held public fields; here they are auto-properties (the analysers refuse public fields),
/// read and written with the same syntax and holding the same values.</remarks>
public struct SchematicNode
{
    public int Id { get; set; }
    public double Wx { get; set; }          // world coords
    public double Wy { get; set; }
    public double BaseKv { get; set; }      // true nominal kV (tooltips, labels)
    public double DisplayKv { get; set; }   // kV used for drawing size/level-of-detail; == BaseKv for GB models,
                                            // a synthesised topological tier for flat imported cases
    public string? Name { get; set; }
}

/// <summary>One branch of the map and its live colour bins (the old FastSchematic <c>EdgeRec</c>, field for field).</summary>
/// <remarks>Auto-properties in place of the old public fields, as for <see cref="SchematicNode"/>.</remarks>
public struct SchematicEdge
{
    public int BranchIndex { get; set; }
    public int A { get; set; }              // node indices, or -1
    public int B { get; set; }
    public bool Valid { get; set; }
    public bool IsTransformer { get; set; }
    public string? Name { get; set; }       // branch name (keys the stress-by-branch overlay)
    public double FlowMw { get; set; }
    public double LoadingFrac { get; set; }
    public int LoadBucket { get; set; }
    public int VoltBucket { get; set; }
    public int CondBucket { get; set; }     // avg endpoint condition bucket (Condition colour mode)
    public int StressBucket { get; set; }   // per-branch stress bucket (Stress colour mode)
}

/// <summary>
/// The network map without a user-interface framework (UI programme Phase 0.2, the second strangler slice): the
/// topology in world coordinates, the live per-bus voltage and per-branch loading with their colour bins, the GDA
/// overlays, the view transform (zoom and pan), the projection from world to screen (including the fan-out of
/// co-located busbars), the level-of-detail rules, and the hit tests that turn a click into a bus or a branch.
///
/// <para>This is the pure logic of the WPF <c>Visuals/FastSchematic.cs</c>, moved here operation for operation so the
/// planned Avalonia map draws from the same numbers. Every screen coordinate, radius, bucket and pick is computed by the
/// same arithmetic in the same order, with <see cref="ScreenPoint"/> and plain doubles in place of the WPF Point,
/// Vector and Rect (which carried the same doubles), so the WPF map's pixels are unchanged. FastSchematic keeps the
/// drawing (geometry, pens, brushes, text, the minimap, the overlays) and delegates these calculations here.</para>
///
/// <para>The view depends on the size of the control it is drawn in: the front end sets <see cref="ViewportWidth"/>
/// and <see cref="ViewportHeight"/> (the WPF map sets them from ActualWidth and ActualHeight before every call).</para>
///
/// <para>One note for the ports: the fan-out layout (<see cref="Load"/>) places stacked busbars with
/// <see cref="Math.Cos"/> and <see cref="Math.Sin"/>, as FastSchematic always did, so the WPF pixels stay identical. This is
/// cosmetic screen layout, not a solved number, and this project is not on the deterministic-maths guard's list.</para>
/// </summary>
public sealed class SchematicView
{
    // ---- named constants (values unchanged from FastSchematic) ----

    /// <summary>Bus count up to which the whole topology is drawn at fit (no voltage LOD culling).</summary>
    public const int ShowAllCeiling = 800;

    /// <summary>The smallest zoom (world units to pixels) the view accepts.</summary>
    public const double MinZoom = 0.05;

    /// <summary>The number of colour bins every corridor colour mode uses. 12 buckets across the loading range is
    /// visually smooth enough, and lets the corridors batch into a handful of geometries instead of one pen per branch.</summary>
    public const int LoadingBuckets = 12;

    /// <summary>A branch with no thermal rating is coloured against this assumed MW figure (|P| / 2,000 MW, capped
    /// at 1). The same value as the web <c>PresentationLimits.UnratedBranchNominalMw</c>.</summary>
    public const double UnratedBranchNominalMw = GridSim.Core.Presentation.PresentationConstants.UnratedBranchNominalMw;   // N023: the one shared copy (was the literal 2000.0)

    /// <summary>Screen-space pick radius (px) added to a roundel's radius when hit-testing a click on a node.</summary>
    public const double NodePickRadiusPx = 14.0;
    /// <summary>Screen-space distance (px) within which a click picks a branch.</summary>
    public const double BranchPickTolerancePx = 8.0;

    /// <summary>Squared screen distance (px^2) within which two branches count as drawn on the same line for picking.</summary>
    public const double CoincidentPickTolerancePx2 = 0.25;

    private const double GoldenAngle = 2.399963229728653;

    // ---- topology (captured in Load), in WORLD coordinates ----
    // World space: x = normalised X (0..1), y = (1 - normalised Y) so north is up and
    // y grows downward like screen space. World bounds are computed from the data.

    /// <summary>Every bus, in model order.</summary>
    public List<SchematicNode> Nodes { get; } = new();
    /// <summary>Bus Id to its index in <see cref="Nodes"/>.</summary>
    public Dictionary<int, int> IdToIndex { get; } = new();
    /// <summary>Every branch, in model order (so an edge index is a branch index).</summary>
    public List<SchematicEdge> Edges { get; } = new();

    /// <summary>The world bounds: left, top, width and height (the old WPF <c>Rect _worldBounds</c>, initially 0, 0, 1, 1).</summary>
    public double WorldLeft { get; private set; }
    public double WorldTop { get; private set; }
    public double WorldWidth { get; private set; } = 1;
    public double WorldHeight { get; private set; } = 1;

    /// <summary>The highest bus base kV in the model (1 when there is none above it; 400 before the first load).</summary>
    public double MaxBaseKv { get; private set; } = 400;

    /// <summary>
    /// Small networks (the IEEE/MATPOWER test cases) are drawn in full at every zoom: the GB
    /// voltage level-of-detail (EHV backbone first) would hide most of a small imported case, whose
    /// voltages don't line up with the GB tiers anyway. Large networks keep the progressive LOD.
    /// </summary>
    public bool ShowAllNodes { get; private set; }

    /// <summary>A single-voltage case has no hierarchy at all, so a display voltage tier is synthesised from the graph
    /// topology (see <see cref="Load"/>) purely to size the roundels; true kV is kept.</summary>
    public bool SyntheticTiers { get; private set; }

    // ---- live state ----

    /// <summary>Per node (index-aligned with <see cref="Nodes"/>): the solved voltage magnitude, p.u.</summary>
    public double[]? VmByNode { get; private set; }
    /// <summary>True once a solved state has been applied (<see cref="ApplyState"/>).</summary>
    public bool HasState { get; private set; }

    // ---- GDA-bridge overlays (condition-by-bus, stress-by-branch) ----

    /// <summary>Per node (index-aligned with <see cref="Nodes"/>), 0 healthy .. 1 critical.</summary>
    public double[]? ConditionByNode { get; private set; }
    /// <summary>Per edge (index-aligned with <see cref="Edges"/>), 0 cool .. 1 hot.</summary>
    public double[]? StressByEdge { get; private set; }
    /// <summary>True once an overlay has been applied (<see cref="ApplyOverlay"/>).</summary>
    public bool HasOverlay { get; private set; }

    /// <summary>Pre-bucketed edge indices (one list per colour bin) so a flow overlay touches only each bin's edges,
    /// not every edge per bin. Rebuilt by <see cref="RebuildEdgeBuckets"/>.</summary>
    public List<int>?[] EdgesByBucket { get; } = new List<int>?[LoadingBuckets];

    // ---- view transform (world -> screen): screen = world * Zoom + Offset ----

    /// <summary>World units to screen pixels.</summary>
    public double Zoom { get; set; } = 1.0;
    /// <summary>Screen-space translation, in pixels (the old WPF <c>Vector _offset</c>).</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    /// <summary>The control's width in pixels (the WPF ActualWidth).</summary>
    public double ViewportWidth { get; set; }
    /// <summary>The control's height in pixels (the WPF ActualHeight).</summary>
    public double ViewportHeight { get; set; }

    // ---- co-located "stacked asset" fan-out (tube-map style) ----
    // Many buses are distinct busbars at the SAME physical site, so they project onto one
    // point. Zoomed out they read as a single roundel; zoomed in they fan out into a bounded
    // cluster (phyllotaxis disc) so each is individually visible + clickable, with a hint box.
    private int[] _fanCount = Array.Empty<int>();     // per node: members at this site (1 = not stacked)
    private double[] _fanDirX = Array.Empty<double>(); // per node: unit-disc offset direction (x)
    private double[] _fanDirY = Array.Empty<double>(); // per node: unit-disc offset direction (y)
    private double[] _fanFullR = Array.Empty<double>();// per node: fully-fanned disc radius (screen px)

    /// <summary>One entry per stacked site: its world position, member count, fully-fanned radius and highest display kV.</summary>
    public List<(double Wx, double Wy, int Count, double FullR, double MaxKv)> FanClusters { get; } = new();

    // The last cycling branch pick: where it was, which coincident branches it saw, and which one it returned.
    private (ScreenPoint At, int[] Set, int Index)? _lastPick;

    /// <summary>
    /// Zoom ceiling, relative to the fitted view rather than absolute: a distribution-depth
    /// model packs whole 11 kV clusters into a few thousandths of the world, so the ceiling
    /// must scale with how much detail there is to dive into. (The old absolute 400 sat
    /// *below* the fit zoom of a full-screen window  -  you could never even reach 1:1.)
    /// </summary>
    public double MaxZoom => Math.Max(400.0, FitZoomEstimate() * 600.0);

    // ============================ topology ============================

    /// <summary>
    /// Capture topology and normalised coordinates; compute world bounds; synthesise display tiers for a flat case;
    /// lay out the co-located fans. Resets the live state and the overlays. (The topology half of the old
    /// FastSchematic.Load, in its order.)
    /// </summary>
    public void Load(GridModel model)
    {
        Nodes.Clear();
        IdToIndex.Clear();
        Edges.Clear();
        // (The last cycling branch pick is deliberately kept across a load, as FastSchematic kept it.)
        HasState = false;
        VmByNode = null;
        HasOverlay = false;
        ConditionByNode = null;
        StressByEdge = null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        MaxBaseKv = 1;

        foreach (Bus b in model.Buses)
        {
            double wx = b.X;
            double wy = 1.0 - b.Y;   // flip so north (y up) is at the top of the screen
            IdToIndex[b.Id] = Nodes.Count;
            Nodes.Add(new SchematicNode
            {
                Id = b.Id,
                Wx = wx,
                Wy = wy,
                BaseKv = b.BaseKv,
                DisplayKv = b.BaseKv,   // GB models draw by true kV; flat imports overridden below
                Name = b.Name,
            });
            if (wx < minX) minX = wx;
            if (wy < minY) minY = wy;
            if (wx > maxX) maxX = wx;
            if (wy > maxY) maxY = wy;
            if (b.BaseKv > MaxBaseKv) MaxBaseKv = b.BaseKv;
        }

        // Small networks (test cases): draw the whole topology at fit. Single-voltage cases also get
        // a synthesised topological tier so the map isn't uniformly sized. Large multi-tier networks
        // (GB synthetic, PEGASE) keep the EHV-first progressive level-of-detail.
        int distinctKv = Nodes.Select(nd => Math.Round(nd.BaseKv, 3)).Distinct().Count();
        ShowAllNodes = Nodes.Count is > 0 and <= ShowAllCeiling;
        SyntheticTiers = Nodes.Count > 1 && distinctKv <= 1;

        int idx = 0;
        foreach (Branch br in model.Branches)
        {
            bool haveA = IdToIndex.TryGetValue(br.From, out int a);
            bool haveB = IdToIndex.TryGetValue(br.To, out int c);
            Edges.Add(new SchematicEdge
            {
                BranchIndex = idx++,
                A = haveA ? a : -1,
                B = haveB ? c : -1,
                IsTransformer = br.IsTransformer,
                Valid = haveA && haveB,
                Name = br.Name,
            });
        }

        // ---- synthesise a voltage hierarchy for flat imported cases ----
        // A bare IEEE/MATPOWER case has one voltage level, so every roundel would draw the same
        // size and the map reads flat. Derive a display tier from the graph topology (see
        // TopologyTiers) and map it to a GB-like nominal kV so the existing kV-driven sizing gives
        // a backbone->distribution hierarchy. The true kV is kept for tooltips.
        if (SyntheticTiers)
            AssignDisplayTiers(model);

        BuildFanClusters();

        if (Nodes.Count == 0)
        {
            SetWorldBounds(0, 0, 1, 1);
        }
        else
        {
            // Guard against degenerate (all-equal) coordinates.
            if (maxX - minX < 1e-9) { minX -= 0.5; maxX += 0.5; }
            if (maxY - minY < 1e-9) { minY -= 0.5; maxY += 0.5; }
            SetWorldBounds(minX, minY, maxX - minX, maxY - minY);
        }

        VmByNode = new double[Nodes.Count];
        for (int i = 0; i < VmByNode.Length; i++) VmByNode[i] = 1.0;
    }

    /// <summary>Store the world bounds. A negative or non-finite extent is a defect (the WPF Rect refused one too).</summary>
    private void SetWorldBounds(double left, double top, double width, double height)
    {
        if (!(width >= 0) || !(height >= 0))
            throw new ArgumentOutOfRangeException(nameof(width), $"world bounds must have a non-negative size (got {width:R} x {height:R})");
        WorldLeft = left;
        WorldTop = top;
        WorldWidth = width;
        WorldHeight = height;
    }

    /// <summary>
    /// Give a flat imported case a synthesised voltage hierarchy: map each bus's topological tier
    /// (see <see cref="TopologyTiers"/>) to a GB-like nominal kV in <see cref="SchematicNode.DisplayKv"/>,
    /// so the renderer's kV-driven roundel sizing / ring weight / level-of-detail draw the backbone
    /// large and the distribution small - the "re-imagine as the UK grid" look. True kV is kept.
    /// </summary>
    private void AssignDisplayTiers(GridModel model)
    {
        int n = Nodes.Count;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (SchematicEdge e in Edges)
            if (e.Valid && e.A != e.B) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

        int source = -1;
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Slack && IdToIndex.TryGetValue(b.Id, out int si)) { source = si; break; }

        int[] tier = TopologyTiers.Assign(n, adj, source);
        for (int i = 0; i < n; i++)
        {
            SchematicNode nd = Nodes[i];
            nd.DisplayKv = TopologyTiers.KvForTier(tier[i]);
            Nodes[i] = nd;
        }
    }

    /// <summary>
    /// Group co-located nodes (same site) and lay each group's members out as a phyllotaxis disc,
    /// so <see cref="NodeScreen"/> can fan them apart as the view zooms in. One-off per Load.
    /// </summary>
    private void BuildFanClusters()
    {
        int n = Nodes.Count;
        _fanCount = new int[n];
        _fanDirX = new double[n];
        _fanDirY = new double[n];
        _fanFullR = new double[n];
        FanClusters.Clear();
        for (int i = 0; i < n; i++) _fanCount[i] = 1;

        // Bucket node indices by quantised world position (exact co-location).
        var byPos = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var key = ((long)Math.Round(Nodes[i].Wx * 1e6), (long)Math.Round(Nodes[i].Wy * 1e6));
            if (!byPos.TryGetValue(key, out List<int>? list)) byPos[key] = list = new List<int>();
            list.Add(i);
        }

        foreach (List<int> members in byPos.Values)
        {
            int k = members.Count;
            if (k < 2) continue;
            // Stable order so the layout doesn't jump between loads: by display kV then id.
            members.Sort((a, b) => Nodes[b].DisplayKv != Nodes[a].DisplayKv
                ? Nodes[b].DisplayKv.CompareTo(Nodes[a].DisplayKv)
                : Nodes[a].Id.CompareTo(Nodes[b].Id));
            double maxKv = 0;
            foreach (int m in members) maxKv = Math.Max(maxKv, Nodes[m].DisplayKv);
            // Disc big enough that k roundels don't overlap, bounded so it stays a tidy cluster.
            double fullR = Math.Clamp(11.0 * Math.Sqrt(k) * 1.9, 26, 240);
            for (int s = 0; s < k; s++)
            {
                double ang = s * GoldenAngle;
                double rad = Math.Sqrt((s + 0.5) / k);      // 0..1, even area fill
                int ni = members[s];
                _fanCount[ni] = k;
                _fanDirX[ni] = Math.Cos(ang) * rad;
                _fanDirY[ni] = Math.Sin(ang) * rad;
                _fanFullR[ni] = fullR;
            }
            FanClusters.Add((Nodes[members[0]].Wx, Nodes[members[0]].Wy, k, fullR, maxKv));
        }
    }

    /// <summary>First bus whose name contains <paramref name="query"/> (case-insensitive), or null.</summary>
    public int? FindNode(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query.Trim();
        foreach (SchematicNode nd in Nodes)
            if (nd.Name is not null && nd.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                return nd.Id;
        return null;
    }

    // ============================ live state and colour bins ============================

    /// <summary>
    /// Apply a freshly solved state: per-branch loading/flow and per-node voltage, and each edge's loading and voltage
    /// colour bins. Returns false (and changes nothing) when no model is loaded. The front end then re-buckets the edges
    /// (<see cref="RebuildEdgeBuckets"/>) and redraws.
    /// </summary>
    public bool ApplyState(PowerFlowResult result)
    {
        if (Nodes.Count == 0) return false;
        VmByNode ??= new double[Nodes.Count];
        double[] vmByNode = VmByNode;

        // Voltage by node.
        for (int i = 0; i < result.BusIds.Count && i < result.Vm.Length; i++)
            if (IdToIndex.TryGetValue(result.BusIds[i], out int ni))
                vmByNode[ni] = result.Vm[i];

        // Per-branch loading + flow (LineFlows is index-aligned with model.Branches,
        // and Edges were built in that same order).
        int n = Math.Min(Edges.Count, result.LineFlows.Count);
        for (int i = 0; i < n; i++)
        {
            SchematicEdge e = Edges[i];
            LineFlow lf = result.LineFlows[i];
            e.FlowMw = lf.PFromMw;
            double frac = lf.RatingMva > 0
                ? lf.LoadingPercent / 100.0
                : Math.Min(1.0, Math.Abs(lf.PFromMw) / UnratedBranchNominalMw);
            e.LoadingFrac = frac;
            // An UNRATED branch's fraction is only a flow-scaled tint (capped at 1.0 above), not a loading against a
            // rating, so it never takes the overload (red) bucket; a rated branch at or above 1.0 always does.
            e.LoadBucket = lf.RatingMva > 0 ? LoadingBucketOf(frac) : Math.Min(LoadingBucketOf(frac), LoadingBuckets - 2);
            double va = e.A >= 0 && e.A < vmByNode.Length ? vmByNode[e.A] : 1.0;
            double vb = e.B >= 0 && e.B < vmByNode.Length ? vmByNode[e.B] : 1.0;
            e.VoltBucket = VoltageBucketOf((va + vb) * 0.5);
            Edges[i] = e;
        }

        HasState = true;
        return true;
    }

    /// <summary>
    /// Apply the GDA-bridge per-asset overlays: condition/fragility keyed by bus <c>Id</c> (mapped to a node via
    /// <see cref="IdToIndex"/>) and stress keyed by branch <see cref="SchematicEdge.Name"/>. Populates the per-node and
    /// per-edge scalar arrays (mirroring the <see cref="VmByNode"/> pattern) and pre-buckets each edge for the
    /// <see cref="MapColourMode.Condition"/> / <see cref="MapColourMode.Stress"/> corridor colouring. A null map
    /// leaves that channel at its previous values. Returns false (and changes nothing) when no model is loaded.
    /// </summary>
    public bool ApplyOverlay(IReadOnlyDictionary<int, double>? byBusId, IReadOnlyDictionary<string, double>? byBranchName)
    {
        if (Nodes.Count == 0) return false;
        ConditionByNode ??= new double[Nodes.Count];
        StressByEdge ??= new double[Edges.Count];
        double[] conditionByNode = ConditionByNode, stressByEdge = StressByEdge;

        if (byBusId is not null)
            foreach ((int id, double v) in byBusId)
                if (IdToIndex.TryGetValue(id, out int ni)) conditionByNode[ni] = v;

        // The bridge protocol keys stress by branch NAME (the wire format carries no branch index), so namesakes all
        // take the one value the server sent for that name. That limit is the protocol's; an index-keyed field needs a
        // protocol change on both ends.
        if (byBranchName is not null)
            for (int i = 0; i < Edges.Count; i++)
            {
                SchematicEdge e = Edges[i];
                if (e.Name is not null && byBranchName.TryGetValue(e.Name, out double s)) stressByEdge[i] = s;
            }

        // Bucket each edge for batched corridor colouring: stress from its own scalar, condition from the
        // average of its endpoint buses (the same "corridor tracks its endpoints" rule the voltage mode uses).
        for (int i = 0; i < Edges.Count; i++)
        {
            SchematicEdge e = Edges[i];
            e.StressBucket = Bucket01(stressByEdge[i]);
            double ca = e.A >= 0 && e.A < conditionByNode.Length ? conditionByNode[e.A] : 0;
            double cb = e.B >= 0 && e.B < conditionByNode.Length ? conditionByNode[e.B] : 0;
            e.CondBucket = Bucket01((ca + cb) * 0.5);
            Edges[i] = e;
        }

        HasOverlay = true;
        return true;
    }

    /// <summary>Re-sort the drawable (valid, non-transformer) edges into their colour bins for <paramref name="mode"/>.</summary>
    public void RebuildEdgeBuckets(MapColourMode mode)
    {
        for (int b = 0; b < LoadingBuckets; b++)
            (EdgesByBucket[b] ??= new List<int>()).Clear();
        for (int i = 0; i < Edges.Count; i++)
        {
            SchematicEdge e = Edges[i];
            if (e.IsTransformer || !e.Valid) continue;
            int bkt = BucketFor(e, mode);
            EdgesByBucket[bkt]!.Add(i);
        }
    }

    /// <summary>
    /// The colour bucket for a loading fraction. Loadings below 1.0 fill the first <c>LoadingBuckets - 1</c> buckets in
    /// equal steps (floor, so a bucket never reaches past its upper edge), and EVERY loading at or above 1.0 (an
    /// overload) goes to the last bucket, which is drawn fully red. The old mapping rounded 0..1.2 across all twelve
    /// buckets, so bucket 9 spanned 0.927 to 1.036 and was drawn at 0.982 (amber-red): a circuit at 102 % of rating drew
    /// in exactly the colour of one at 94 %. A non-finite loading also lands in the red bucket (fail loud, never green).
    /// </summary>
    public static int LoadingBucketOf(double frac)
    {
        if (!(frac < 1.0)) return LoadingBuckets - 1;                 // >= 1.0, or NaN: overloaded / unknown -> red
        if (frac <= 0) return 0;
        int b = (int)Math.Floor(frac * (LoadingBuckets - 1));         // 0 .. LoadingBuckets - 2 for 0 <= frac < 1
        return Math.Clamp(b, 0, LoadingBuckets - 2);
    }

    /// <summary>The loading fraction a bucket is drawn at: the middle of its band below 1.0, and 1.1 (fully red on
    /// the loading ramp, which is red from 1.0) for the overload bucket.</summary>
    public static double BucketFrac(int bucket) =>
        bucket >= LoadingBuckets - 1 ? 1.1 : (bucket + 0.5) / (LoadingBuckets - 1);

    /// <summary>The colour bucket for a voltage magnitude across the 0.94 .. 1.06 p.u. band (rounded to the nearest).</summary>
    public static int VoltageBucketOf(double vm)
    {
        double frac = Math.Clamp((vm - 0.94) / 0.12, 0, 1);   // 0.94..1.06 p.u. band
        return Math.Clamp((int)(frac * (LoadingBuckets - 1) + 0.5), 0, LoadingBuckets - 1);
    }

    /// <summary>The voltage a voltage bucket is drawn at.</summary>
    public static double VoltageOfBucket(int b) => 0.94 + (double)b / (LoadingBuckets - 1) * 0.12;

    /// <summary>Bucket a plain 0..1 fraction (condition/stress overlays) across the loading-bucket count.</summary>
    public static int Bucket01(double frac) =>
        Math.Clamp((int)(Math.Clamp(frac, 0, 1) * (LoadingBuckets - 1) + 0.5), 0, LoadingBuckets - 1);

    /// <summary>The colour bucket an edge draws in for <paramref name="mode"/> (0 when that mode's data isn't in yet).</summary>
    public int BucketFor(SchematicEdge e, MapColourMode mode) => mode switch
    {
        MapColourMode.Voltage => HasState ? e.VoltBucket : 0,
        MapColourMode.Condition => HasOverlay ? e.CondBucket : 0,
        MapColourMode.Stress => HasOverlay ? e.StressBucket : 0,
        _ => HasState ? e.LoadBucket : 0,
    };

    // ============================ view transform ============================

    /// <summary>
    /// Reset zoom/pan so the whole network fits the current viewport with a margin. Returns false, changing nothing,
    /// when the viewport has no size yet or the world bounds are empty (the front end tries again once it has a size).
    /// </summary>
    public bool FitToView()
    {
        double w = ViewportWidth, h = ViewportHeight;
        if (w < 1 || h < 1 || WorldWidth <= 0 || WorldHeight <= 0) return false;

        const double pad = 36;
        double availW = Math.Max(1, w - 2 * pad);
        double availH = Math.Max(1, h - 2 * pad);
        double zx = availW / WorldWidth;
        double zy = availH / WorldHeight;
        Zoom = Math.Clamp(Math.Min(zx, zy), MinZoom, MaxZoom);

        // Centre the world bounds in the viewport.
        double cx = WorldLeft + WorldWidth / 2;
        double cy = WorldTop + WorldHeight / 2;
        OffsetX = w / 2 - cx * Zoom;
        OffsetY = h / 2 - cy * Zoom;
        return true;
    }

    /// <summary>Centre the view on a bus (optionally setting a zoom). Returns false, changing nothing, when the viewport
    /// has no width yet or the bus is not on the map. Used by search-to-jump.</summary>
    public bool CenterOn(int busId, double? targetZoom = null)
    {
        if (ViewportWidth < 1 || !IdToIndex.TryGetValue(busId, out int ni)) return false;
        SchematicNode nd = Nodes[ni];
        if (targetZoom is double z) Zoom = Math.Clamp(z, MinZoom, MaxZoom);
        OffsetX = ViewportWidth / 2 - nd.Wx * Zoom;
        OffsetY = ViewportHeight / 2 - nd.Wy * Zoom;
        return true;
    }

    /// <summary>The zoom at which <see cref="CenterOn"/> focuses a bus at substation level (search / drill-down helper).</summary>
    public double FocusZoom => Math.Min(MaxZoom, FitZoomEstimate() * 6);

    /// <summary>Zoom by <paramref name="factor"/> keeping the world point under <paramref name="screen"/> fixed.
    /// Returns false, changing nothing, when the clamped zoom would not change.</summary>
    public bool ZoomAt(ScreenPoint screen, double factor)
    {
        double newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 1e-9) return false;

        // Keep the world point under the cursor anchored: solve offset so screen stays put.
        ScreenPoint worldUnderCursor = ScreenToWorld(screen);
        Zoom = newZoom;
        OffsetX = screen.X - worldUnderCursor.X * Zoom;
        OffsetY = screen.Y - worldUnderCursor.Y * Zoom;
        return true;
    }

    /// <summary>World to screen: <c>world * Zoom + Offset</c>.</summary>
    public ScreenPoint WorldToScreen(double wx, double wy) =>
        new(wx * Zoom + OffsetX, wy * Zoom + OffsetY);

    /// <summary>Screen to world, the inverse of <see cref="WorldToScreen"/> (as a point holding world x and y).</summary>
    public ScreenPoint ScreenToWorld(ScreenPoint s) =>
        new((s.X - OffsetX) / Zoom, (s.Y - OffsetY) / Zoom);

    /// <summary>How far a stacked-asset cluster is fanned open, 0 (collapsed) .. 1 (fully separated),
    /// driven by zoom: closed at the fitted view, opening as you dive in.</summary>
    public double FanT()
    {
        double rel = Zoom / FitZoomEstimate();
        return Math.Clamp((rel - 2.0) / 7.0, 0.0, 1.0);
    }

    /// <summary>Screen position of a node, including its stacked-asset fan offset at the current zoom.</summary>
    public ScreenPoint NodeScreen(int ni)
    {
        SchematicNode nd = Nodes[ni];
        ScreenPoint p = WorldToScreen(nd.Wx, nd.Wy);
        if ((uint)ni >= (uint)_fanCount.Length || _fanCount[ni] <= 1) return p;
        double t = FanT();
        if (t <= 0.001) return p;
        double r = t * _fanFullR[ni];
        return new ScreenPoint(p.X + _fanDirX[ni] * r, p.Y + _fanDirY[ni] * r);
    }

    /// <summary>The radius of a roundel for a given base voltage, in screen pixels.</summary>
    public double RoundelRadius(double baseKv)
    {
        // Graded by voltage level so the hierarchy reads at a glance: 400 kV backbone largest,
        // stepping down through 275 / 132 / 33 to 11 kV distribution.
        double baseR = baseKv >= 300 ? 10.5
                     : baseKv >= 200 ? 8.5
                     : baseKv >= 100 ? 6.5
                     : baseKv >= 25  ? 5.0
                     : 3.6;
        // Grow gently with zoom but clamp so they stay readable and never balloon.
        double scaled = baseR * Math.Clamp(Zoom / FitZoomEstimate(), 0.55, 1.8);
        return scaled;
    }

    /// <summary>A reference zoom (the fit-to-view zoom) used to scale roundels relative to "fitted".</summary>
    public double FitZoomEstimate()
    {
        double w = ViewportWidth, h = ViewportHeight;
        if (w < 1 || h < 1 || WorldWidth <= 0 || WorldHeight <= 0) return 1;
        double zx = (w - 72) / WorldWidth;
        double zy = (h - 72) / WorldHeight;
        return Math.Max(0.01, Math.Min(zx, zy));
    }

    /// <summary>
    /// Level of detail: at low zoom only EHV (>=200 kV) nodes/branches are shown; as we zoom
    /// in, lower voltages are revealed. Returns the minimum base-kV currently shown.
    /// </summary>
    public double LodMinBaseKv()
    {
        if (ShowAllNodes) return 0;              // small test case: draw the whole topology at any zoom
        double rel = Zoom / FitZoomEstimate();   // 1.0 ~= fitted whole-network view
        if (rel < 1.4) return 200;                // far out: EHV backbone only
        if (rel < 3.0) return 100;                // mid: + sub-transmission (132 kV)
        if (rel < 7.0) return 20;                 // closer: + 33 kV primaries
        if (rel < 16.0) return 10;                // closer still: + 11 kV secondaries
        return 0;                                 // dived in: everything (6.6 kV pockets)
    }

    // UI programme 4.2 follow-up (2026-10-24): buses drawn and hit whatever the voltage level of detail. Author mode puts
    // its selected bus and Connect's first bus here, so a bus placed below the current cut (an 11 kV bus placed on gb-full
    // at the fitted view) stays on the map to be seen, dragged and connected. Empty (always, in Run mode) the rules are
    // exactly as before: a bus is hidden when its display kV is below the cut.
    private readonly HashSet<int> _lodExempt = new();

    /// <summary>The bus ids shown at every level of detail (empty: none; the Run map never sets any).</summary>
    public IReadOnlyCollection<int> LodExemptBuses => _lodExempt;

    /// <summary>Replace the buses shown at every level of detail with <paramref name="busIds"/>. True when the set
    /// changed (the caller redraws).</summary>
    public bool SetLodExempt(IEnumerable<int> busIds)
    {
        ArgumentNullException.ThrowIfNull(busIds);
        var next = new HashSet<int>(busIds);
        if (next.SetEquals(_lodExempt)) return false;
        _lodExempt.Clear();
        _lodExempt.UnionWith(next);
        return true;
    }

    /// <summary>True when node <paramref name="nodeIndex"/> is below the level-of-detail cut <paramref name="minKv"/>
    /// (<see cref="LodMinBaseKv"/>) and not exempt: it is then neither drawn nor hit, nor is a branch that ends on it
    /// while only the higher voltages are shown.</summary>
    public bool IsHiddenAtLod(int nodeIndex, double minKv)
    {
        SchematicNode nd = Nodes[nodeIndex];
        return nd.DisplayKv < minKv && (_lodExempt.Count == 0 || !_lodExempt.Contains(nd.Id));
    }

    /// <summary>The minimum display kV whose buses are labelled at the current zoom.</summary>
    public double LabelMinKv()
    {
        if (ShowAllNodes) return 0;   // small test case: label by zoom only, not by kV tier
        double rel = Zoom / FitZoomEstimate();
        if (rel < 3.0) return 200;   // only EHV labels at first
        if (rel < 8.0) return 100;
        if (rel < 18.0) return 20;
        return 0;                    // all labels when fully dived in
    }

    /// <summary>True when the view is zoomed in far enough to label buses at all.</summary>
    public bool ShowLabels() => Zoom / FitZoomEstimate() >= 1.8;

    // ============================ hit testing ============================

    /// <summary>
    /// Return the bus Id of the roundel nearest <paramref name="screenPt"/> within a small
    /// pixel radius, or null. Honours the current LOD (hidden nodes aren't hit).
    /// </summary>
    public int? HitTestNode(ScreenPoint screenPt)
    {
        double bestDist = double.MaxValue;
        int bestId = -1;
        double tol = NodePickRadiusPx;   // screen-space pick radius
        double minKv = LodMinBaseKv();

        for (int i = 0; i < Nodes.Count; i++)
        {
            SchematicNode nd = Nodes[i];
            if (IsHiddenAtLod(i, minKv)) continue;
            ScreenPoint s = NodeScreen(i);
            double dx = s.X - screenPt.X, dy = s.Y - screenPt.Y;
            double d2 = dx * dx + dy * dy;
            double r = RoundelRadius(nd.DisplayKv) + tol;
            if (d2 <= r * r && d2 < bestDist) { bestDist = d2; bestId = nd.Id; }
        }
        return bestId >= 0 ? bestId : null;
    }

    /// <summary>
    /// Branch index nearest <paramref name="screenPt"/> within a small radius, or null. Parallel circuits drawn on the
    /// same screen line tie exactly, and the first used to win every time, so the second circuit of a double-circuit
    /// route could never be opened from the map. With <paramref name="cycle"/> (a map click), a repeated click at the
    /// same spot on the same set of coincident branches steps to the next one in turn; without it (hover, menus) the
    /// first is returned as before.
    /// </summary>
    public int? HitTestBranch(ScreenPoint screenPt, bool cycle = false)
    {
        double minKv = LodMinBaseKv();
        bool ehvOnly = minKv > 1;
        double bestDist = BranchPickTolerancePx * BranchPickTolerancePx;   // squared pixel tolerance
        var hits = new List<(int Branch, double D2)>();

        foreach (SchematicEdge e in Edges)
        {
            if (!e.Valid) continue;
            if (ehvOnly && (IsHiddenAtLod(e.A, minKv) || IsHiddenAtLod(e.B, minKv))) continue;
            ScreenPoint pa = NodeScreen(e.A), pb = NodeScreen(e.B);
            double d2 = DistSqPointToSegment(screenPt, pa, pb);
            if (d2 < bestDist) hits.Add((e.BranchIndex, d2));
        }
        if (hits.Count == 0) return null;

        // The nearest distance, and every branch within a hair of it (coincident on screen), in model order.
        double nearest = hits.Min(h => h.D2);
        int[] coincident = hits.Where(h => h.D2 <= nearest + CoincidentPickTolerancePx2).Select(h => h.Branch).ToArray();
        if (!cycle || coincident.Length == 1) { _lastPick = null; return coincident[0]; }

        // Same spot (within a couple of pixels) and the same coincident set as the last click: take the next one.
        // (The distance is the WPF Vector length of the difference of the two points: Sqrt(dx * dx + dy * dy).)
        int next = 0;
        if (_lastPick is { } lp && Length(lp.At.X - screenPt.X, lp.At.Y - screenPt.Y) <= BranchPickTolerancePx && lp.Set.SequenceEqual(coincident))
            next = (lp.Index + 1) % coincident.Length;
        _lastPick = (screenPt, coincident, next);
        return coincident[next];
    }

    /// <summary>The length of a screen vector, as the WPF <c>Vector.Length</c> computes it.</summary>
    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>The squared distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static double DistSqPointToSegment(ScreenPoint p, ScreenPoint a, ScreenPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) { double ex = p.X - a.X, ey = p.Y - a.Y; return ex * ex + ey * ey; }
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ox = p.X - cx, oy = p.Y - cy;
        return ox * ox + oy * oy;
    }
}
