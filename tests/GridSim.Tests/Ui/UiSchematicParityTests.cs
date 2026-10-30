using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Schematic;
using Xunit;
using Old = GridSim.Tests.Ui.OldSchematic;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 0.2 (second slice): the network map's pure logic moved from the WPF <c>FastSchematic</c> into the
/// platform-neutral <see cref="SchematicView"/>: topology capture, display tiers, stacked-asset fan-out, state and
/// overlay colour bins, edge buckets, the view transform, the projection, the level of detail and the hit tests. That
/// is an orchestration-only move, so every number must be what the WPF control computed before. These tests drive the
/// new model and a verbatim copy of the old code (<c>UiSchematicOracle.cs</c>) with the same models (case9, the GB
/// reduced case, gb-synthetic at 2,227 buses, and a built stacked case with co-located busbars, double circuits, a
/// broken branch and transformer ties), the same viewport sizes and the same sequence of fit / zoom / pan / centre
/// moves, and compare exactly: every double by its bits, every node's screen position, and the node and branch a grid
/// of probe points picks (including repeated cycling clicks on coincident circuits).
/// </summary>
public sealed class UiSchematicParityTests
{
    public static TheoryData<string> Cases => new() { "case9", "uk", "gb-synthetic", "stacked" };

    private static GridModel ModelOf(string name)
    {
        if (name == "stacked") return Stacked();
        ModelSource s;
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            s = new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        else
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "data", name);
            Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
            s = new ModelSource(name, dir);
        }
        return SimHost.ReadModel(s);   // read and laid out exactly as the window loads a model for the map
    }

    /// <summary>
    /// A built case that reaches every branch of the logic the bundled cases might not: 300 sites of three co-located
    /// busbars (400 / 132 / 33 kV, so the fan-out and the voltage level of detail both engage, and over 800 buses so the
    /// whole-topology shortcut is off), a transformer tie inside every site, a single and a double circuit between
    /// neighbouring sites (coincident on screen), an 11 kV and a 6.6 kV spur, and one branch to a bus that does not exist.
    /// </summary>
    private static GridModel Stacked()
    {
        var buses = new List<Bus>();
        var branches = new List<Branch>();
        var rng = new Random(20261020);
        int id = 1;
        var sites = new List<(int Hv, int Mv, int Lv)>();
        for (int s = 0; s < 300; s++)
        {
            double x = 0.05 + 0.9 * rng.NextDouble(), y = 0.05 + 0.9 * rng.NextDouble();
            int hv = id++, mv = id++, lv = id++;
            buses.Add(new Bus(hv, s == 0 ? BusType.Slack : BusType.Pq, BaseKv: 400, Name: $"SITE{s} 400kV", X: x, Y: y));
            buses.Add(new Bus(mv, BusType.Pq, BaseKv: 132, Name: $"Site {s} 132", X: x, Y: y));
            buses.Add(new Bus(lv, BusType.Pq, BaseKv: 33, Name: s % 7 == 0 ? null : $"site {s} primary", X: x, Y: y));
            branches.Add(new Branch(hv, mv, 0.001, 0.05, IsTransformer: true, Name: $"T{s}a"));
            branches.Add(new Branch(mv, lv, 0.001, 0.05, IsTransformer: true, Name: $"T{s}b"));
            sites.Add((hv, mv, lv));
        }
        for (int s = 1; s < sites.Count; s++)
        {
            branches.Add(new Branch(sites[s - 1].Hv, sites[s].Hv, 0.001, 0.01, Name: $"L{s}"));
            if (s % 3 == 0)
            {
                branches.Add(new Branch(sites[s - 1].Hv, sites[s].Hv, 0.001, 0.01, Name: $"L{s} circuit 2"));   // a double circuit
                branches.Add(new Branch(sites[s - 1].Mv, sites[s].Mv, 0.002, 0.02, Name: $"M{s}"));
            }
            if (s % 10 == 0)
            {
                int a = id++, b = id++;
                buses.Add(new Bus(a, BusType.Pq, BaseKv: 11, Name: $"spur {s} 11", X: 0.5 + 0.001 * s / 10.0, Y: 0.2));
                buses.Add(new Bus(b, BusType.Pq, BaseKv: 6.6, Name: $"spur {s} 6.6", X: 0.5 + 0.001 * s / 10.0, Y: 0.2005));
                branches.Add(new Branch(sites[s].Lv, a, 0.01, 0.1, Name: $"S{s}"));
                branches.Add(new Branch(a, b, 0.01, 0.1, Name: $"S{s}x"));
            }
        }
        branches.Add(new Branch(sites[5].Hv, 999_999, 0.001, 0.01, Name: "to nowhere"));   // an edge with a missing end
        return new GridModel { BaseMva = 100, Buses = buses, Generators = Array.Empty<Generator>(), Branches = branches, Name = "stacked" };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Topology, state, overlays and edge buckets
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Topology_state_overlays_and_edge_buckets_equal_the_old_map(string name)
    {
        GridModel model = ModelOf(name);
        var old = new Old.FastSchematic();
        var neu = new SchematicView();

        // Before any load, and with no model, a state or overlay changes nothing on either side.
        SameTopology(old, neu, $"{name} before load");
        old.Load(model);
        neu.Load(model);
        SameTopology(old, neu, $"{name} loaded");

        var rng = new Random(name.Length * 7919);
        for (int round = 0; round < 3; round++)
        {
            PowerFlowResult r = SyntheticState(model, rng, round);
            old.SetState(r);
            Assert.True(neu.ApplyState(r));
            foreach (MapColourMode mode in Enum.GetValues<MapColourMode>())
            {
                old._colorMode = mode;
                old.RebuildEdgeBuckets();
                neu.RebuildEdgeBuckets(mode);
                SameBuckets(old, neu, mode, $"{name} state {round} {mode}");
            }
            SameTopology(old, neu, $"{name} state {round}");

            var byBus = new Dictionary<int, double>();
            foreach (Bus b in model.Buses) if (rng.Next(3) != 0) byBus[b.Id] = rng.Next(10) == 0 ? double.NaN : rng.NextDouble() * 1.4 - 0.2;
            var byBranch = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Branch b in model.Branches) if (b.Name is not null && rng.Next(2) == 0) byBranch[b.Name] = rng.NextDouble() * 1.3 - 0.1;
            old.SetOverlay(round == 1 ? null : byBus, round == 2 ? null : byBranch);
            Assert.True(neu.ApplyOverlay(round == 1 ? null : byBus, round == 2 ? null : byBranch));
            foreach (MapColourMode mode in Enum.GetValues<MapColourMode>())
            {
                old._colorMode = mode;
                old.RebuildEdgeBuckets();
                neu.RebuildEdgeBuckets(mode);
                SameBuckets(old, neu, mode, $"{name} overlay {round} {mode}");
            }
            SameTopology(old, neu, $"{name} overlay {round}");
        }

        // A reload resets the state and overlays on both sides alike.
        old.Load(model);
        neu.Load(model);
        SameTopology(old, neu, $"{name} reloaded");

        foreach (string q in new[] { "", " ", "400", "site 1", "SITE", "bus", "zzz", "Spur 10" })
            Assert.Equal(old.FindNode(q), neu.FindNode(q));
    }

    [Fact]
    public void An_empty_model_and_a_state_before_any_load_are_handled_as_before()
    {
        var empty = new GridModel { BaseMva = 100, Buses = Array.Empty<Bus>(), Generators = Array.Empty<Generator>(), Branches = Array.Empty<Branch>() };
        var old = new Old.FastSchematic();
        var neu = new SchematicView();
        PowerFlowResult r = SyntheticState(empty, new Random(1), 0);
        old.SetState(r);
        Assert.False(neu.ApplyState(r));
        old.SetOverlay(new Dictionary<int, double> { [1] = 0.5 }, null);
        Assert.False(neu.ApplyOverlay(new Dictionary<int, double> { [1] = 0.5 }, null));
        SameTopology(old, neu, "no load");
        old.Load(empty);
        neu.Load(empty);
        SameTopology(old, neu, "empty");
        old.ActualWidth = neu.ViewportWidth = 800;
        old.ActualHeight = neu.ViewportHeight = 600;
        SameView(old, neu, "empty view");
        Assert.Null(neu.HitTestNode(new ScreenPoint(400, 300)));
        Assert.Null(neu.HitTestBranch(new ScreenPoint(400, 300)));
    }

    [Fact]
    public void The_colour_bin_rules_equal_the_old_ones_at_every_edge()
    {
        var fracs = new List<double> { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1, -0.0, 0, 1e-300, 0.5, 0.999999999, 1, 1.0000001, 1.2, 5 };
        for (int i = 0; i <= 1200; i++) fracs.Add(i / 1000.0);
        for (int b = 0; b <= SchematicView.LoadingBuckets; b++) { fracs.Add(b / 11.0); fracs.Add(Math.BitDecrement(b / 11.0)); fracs.Add(Math.BitIncrement(b / 11.0)); }
        foreach (double f in fracs)
        {
            Assert.Equal(Old.FastSchematic.LoadingBucketOf(f), SchematicView.LoadingBucketOf(f));
            Assert.Equal(Old.FastSchematic.Bucket01(f), SchematicView.Bucket01(f));
            double vm = 0.9 + f * 0.2;
            Assert.Equal(Old.FastSchematic.VoltageBucketOf(vm), SchematicView.VoltageBucketOf(vm));
            Assert.Equal(Old.FastSchematic.VoltageBucketOf(f), SchematicView.VoltageBucketOf(f));
        }
        for (int b = -1; b <= SchematicView.LoadingBuckets + 1; b++)
        {
            Bits(Old.FastSchematic.BucketFrac(b), SchematicView.BucketFrac(b), $"BucketFrac({b})");
            Bits(Old.FastSchematic.VoltageOfBucket(b), SchematicView.VoltageOfBucket(b), $"VoltageOfBucket({b})");
        }
        Assert.Equal(Old.FastSchematic.LoadingBuckets, SchematicView.LoadingBuckets);
        Assert.Equal(Old.FastSchematic.NodePickRadiusPx, SchematicView.NodePickRadiusPx);
        Assert.Equal(Old.FastSchematic.BranchPickTolerancePx, SchematicView.BranchPickTolerancePx);
        Assert.Equal(Old.FastSchematic.CoincidentPickTolerancePx2, SchematicView.CoincidentPickTolerancePx2);
        Assert.Equal(Old.FastSchematic.UnratedBranchNominalMw, SchematicView.UnratedBranchNominalMw);
        Assert.Equal(Old.FastSchematic.ShowAllCeiling, SchematicView.ShowAllCeiling);
        Assert.Equal(Old.FastSchematic.MinZoom, SchematicView.MinZoom);

        // The segment distance on awkward segments (a point segment, a vertical, a probe past either end).
        var pts = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (-3.5, 7.25), (1e-6, 0.0), (5.0, 5.0), (12.0, -1.0), (1e6, 1e6) };
        foreach ((double px, double py) in pts)
            foreach ((double ax, double ay) in pts)
                foreach ((double bx, double by) in pts)
                    Bits(Old.FastSchematic.DistSqPointToSegment(new Old.Point(px, py), new Old.Point(ax, ay), new Old.Point(bx, by)),
                        SchematicView.DistSqPointToSegment(new ScreenPoint(px, py), new ScreenPoint(ax, ay), new ScreenPoint(bx, by)), "DistSq");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // View transform, projection, level of detail and hit tests
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Projection_level_of_detail_and_hit_tests_equal_the_old_map_at_every_zoom(string name)
    {
        GridModel model = ModelOf(name);
        foreach ((double w, double h) in new[] { (1280.0, 800.0), (637.0, 911.0), (0.0, 0.0) })
        {
            var old = new Old.FastSchematic();
            var neu = new SchematicView();
            old.Load(model);
            neu.Load(model);
            old.SetState(SyntheticState(model, new Random(3), 0));
            neu.ApplyState(SyntheticState(model, new Random(3), 0));
            old.ActualWidth = neu.ViewportWidth = w;
            old.ActualHeight = neu.ViewportHeight = h;
            string at = $"{name} {w}x{h}";

            // Fit (refused with no size), then probe.
            old.FitToView();
            Assert.Equal(old._viewInitialised, neu.FitToView());
            SameView(old, neu, at + " fitted");
            Probe(old, neu, at + " fitted");

            // Zoom in at the centre in wheel steps through every level-of-detail band and the fan-out, probing as we go.
            var centre = new ScreenPoint(w / 2, h / 2);
            for (int step = 1; step <= 30; step++)
            {
                old.ZoomAt(new Old.Point(centre.X, centre.Y), 1.15);
                neu.ZoomAt(centre, 1.15);
                SameView(old, neu, $"{at} zoom in {step}");
                if (step % 5 == 0) Probe(old, neu, $"{at} zoom in {step}");
            }
            // Zoom out about an off-centre point, past the floor.
            var corner = new ScreenPoint(w * 0.17, h * 0.83);
            for (int step = 1; step <= 60; step++)
            {
                old.ZoomAt(new Old.Point(corner.X, corner.Y), 1 / 1.15);
                neu.ZoomAt(corner, 1 / 1.15);
                SameView(old, neu, $"{at} zoom out {step}");
            }
            Probe(old, neu, at + " zoomed out");

            // Pan (the window sets the offset directly while dragging), then centre and focus on buses.
            old._offset = new Old.Vector(old._offset.X + 123.25, old._offset.Y - 77.5);
            neu.OffsetX += 123.25;
            neu.OffsetY -= 77.5;
            SameView(old, neu, at + " panned");
            Probe(old, neu, at + " panned");
            foreach (int k in new[] { 0, model.Buses.Count / 3, model.Buses.Count - 1 })
            {
                if (model.Buses.Count == 0) break;
                int busId = model.Buses[k].Id;
                old.FocusNode(busId);
                Assert.Equal(neu.CenterOn(busId, neu.FocusZoom), w >= 1);
                SameView(old, neu, $"{at} focus {busId}");
                Probe(old, neu, $"{at} focus {busId}");
                old.CenterOn(busId, 0.001);
                neu.CenterOn(busId, 0.001);
                SameView(old, neu, $"{at} centre {busId} at the floor");
            }
            old.CenterOn(-424242);
            Assert.False(neu.CenterOn(-424242));
            SameView(old, neu, at + " centre on a missing bus");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The comparisons
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A solved-looking state for every bus and branch of <paramref name="model"/>: voltages around 1 pu (a few
    /// non-finite), and flows on rated and unrated branches at loadings either side of 100 % (a few non-finite).</summary>
    private static PowerFlowResult SyntheticState(GridModel model, Random rng, int round)
    {
        int nb = model.Buses.Count;
        var vm = new double[nb];
        for (int i = 0; i < nb; i++) vm[i] = rng.Next(40) == 0 ? double.NaN : 0.9 + 0.2 * rng.NextDouble();
        var flows = new List<LineFlow>();
        // Round 1 drops the last branch (a short flow list), round 2 has one more flow than branches.
        int nf = model.Branches.Count + (round == 1 ? -1 : round == 2 ? 1 : 0);
        for (int i = 0; i < Math.Max(0, nf); i++)
        {
            double p = (rng.NextDouble() - 0.4) * 3000;
            double rating = rng.Next(4) == 0 ? 0 : 100 + rng.NextDouble() * 2000;
            double pct = rng.Next(30) == 0 ? double.NaN : rng.NextDouble() * 140;
            flows.Add(new LineFlow(0, 0, p, 0, -p, 0, 0, 0, "", rating, pct));
        }
        return new PowerFlowResult
        {
            Converged = true, Iterations = 3, MaxMismatch = 1e-9,
            BusIds = model.Buses.Select(b => b.Id).ToArray(), Vm = vm, VaDeg = new double[nb], PInjMw = new double[nb], QInjMvar = new double[nb],
            TotalLossMw = 0, TotalLossMvar = 0, LineFlows = flows,
        };
    }

    private static void Bits(double want, double got, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(want) == BitConverter.DoubleToInt64Bits(got), $"{what}: want {want:R}, got {got:R}");

    private static void SameArray(double[]? want, double[]? got, string what)
    {
        Assert.True((want is null) == (got is null), $"{what}: null-ness differs");
        if (want is null) return;
        Assert.Equal(want.Length, got!.Length);
        for (int i = 0; i < want.Length; i++) Bits(want[i], got[i], $"{what}[{i}]");
    }

    private static void SameTopology(Old.FastSchematic old, SchematicView neu, string at)
    {
        Assert.Equal(old._nodes.Count, neu.Nodes.Count);
        for (int i = 0; i < neu.Nodes.Count; i++)
        {
            Old.FastSchematic.NodeRec o = old._nodes[i];
            SchematicNode n = neu.Nodes[i];
            Assert.Equal(o.Id, n.Id);
            Bits(o.Wx, n.Wx, $"{at} node {i} Wx");
            Bits(o.Wy, n.Wy, $"{at} node {i} Wy");
            Bits(o.BaseKv, n.BaseKv, $"{at} node {i} BaseKv");
            Bits(o.DisplayKv, n.DisplayKv, $"{at} node {i} DisplayKv");
            Assert.Equal(o.Name, n.Name);
        }
        Assert.Equal(old._idToIndex.OrderBy(kv => kv.Key), neu.IdToIndex.OrderBy(kv => kv.Key));
        Assert.Equal(old._edges.Count, neu.Edges.Count);
        for (int i = 0; i < neu.Edges.Count; i++)
        {
            Old.FastSchematic.EdgeRec o = old._edges[i];
            SchematicEdge n = neu.Edges[i];
            string e = $"{at} edge {i}";
            Assert.Equal(o.BranchIndex, n.BranchIndex);
            Assert.Equal(o.A, n.A);
            Assert.Equal(o.B, n.B);
            Assert.Equal(o.Valid, n.Valid);
            Assert.Equal(o.IsTransformer, n.IsTransformer);
            Assert.Equal(o.Name, n.Name);
            Bits(o.FlowMw, n.FlowMw, e + " FlowMw");
            Bits(o.LoadingFrac, n.LoadingFrac, e + " LoadingFrac");
            Assert.True(o.LoadBucket == n.LoadBucket, $"{e} LoadBucket {o.LoadBucket} vs {n.LoadBucket}");
            Assert.True(o.VoltBucket == n.VoltBucket, $"{e} VoltBucket {o.VoltBucket} vs {n.VoltBucket}");
            Assert.True(o.CondBucket == n.CondBucket, $"{e} CondBucket {o.CondBucket} vs {n.CondBucket}");
            Assert.True(o.StressBucket == n.StressBucket, $"{e} StressBucket {o.StressBucket} vs {n.StressBucket}");
        }
        Bits(old._worldBounds.Left, neu.WorldLeft, at + " world left");
        Bits(old._worldBounds.Top, neu.WorldTop, at + " world top");
        Bits(old._worldBounds.Width, neu.WorldWidth, at + " world width");
        Bits(old._worldBounds.Height, neu.WorldHeight, at + " world height");
        Bits(old._maxBaseKv, neu.MaxBaseKv, at + " max kV");
        Assert.Equal(old._showAllNodes, neu.ShowAllNodes);
        Assert.Equal(old._syntheticTiers, neu.SyntheticTiers);
        Assert.Equal(old._hasState, neu.HasState);
        Assert.Equal(old._hasOverlay, neu.HasOverlay);
        SameArray(old._vmByNode, neu.VmByNode, at + " vm");
        SameArray(old._conditionByNode, neu.ConditionByNode, at + " condition");
        SameArray(old._stressByEdge, neu.StressByEdge, at + " stress");
        Assert.Equal(old._fanClusters.Count, neu.FanClusters.Count);
        for (int i = 0; i < neu.FanClusters.Count; i++)
        {
            var o = old._fanClusters[i];
            var n = neu.FanClusters[i];
            Bits(o.Wx, n.Wx, $"{at} fan {i} Wx");
            Bits(o.Wy, n.Wy, $"{at} fan {i} Wy");
            Assert.Equal(o.Count, n.Count);
            Bits(o.FullR, n.FullR, $"{at} fan {i} FullR");
            Bits(o.MaxKv, n.MaxKv, $"{at} fan {i} MaxKv");
        }
    }

    private static void SameBuckets(Old.FastSchematic old, SchematicView neu, MapColourMode mode, string at)
    {
        for (int b = 0; b < SchematicView.LoadingBuckets; b++)
            Assert.True((old._edgesByBucket[b] ?? new List<int>()).SequenceEqual(neu.EdgesByBucket[b] ?? new List<int>()), $"{at}: bucket {b}");
        for (int i = 0; i < neu.Edges.Count; i++)
            Assert.Equal(old.BucketFor(old._edges[i]), neu.BucketFor(neu.Edges[i], mode));
    }

    private static readonly double[] Kvs = { 0, 6.6, 11, 24.9, 25, 33, 99.9, 100, 132, 199.99, 200, 275, 299, 300, 400, 765, double.NaN };

    private static void SameView(Old.FastSchematic old, SchematicView neu, string at)
    {
        Bits(old._zoom, neu.Zoom, at + " zoom");
        Bits(old._offset.X, neu.OffsetX, at + " offset x");
        Bits(old._offset.Y, neu.OffsetY, at + " offset y");
        Bits(old.FitZoomEstimate(), neu.FitZoomEstimate(), at + " fit zoom");
        Bits(old.MaxZoom, neu.MaxZoom, at + " max zoom");
        Bits(old.LodMinBaseKv(), neu.LodMinBaseKv(), at + " LOD kV");
        Bits(old.LabelMinKv(), neu.LabelMinKv(), at + " label kV");
        Assert.Equal(old.ShowLabels(), neu.ShowLabels());
        Bits(old.FanT(), neu.FanT(), at + " fan t");
        foreach (double kv in Kvs) Bits(old.RoundelRadius(kv), neu.RoundelRadius(kv), $"{at} radius {kv}");
        for (int i = 0; i < neu.Nodes.Count; i++)
        {
            Old.Point o = old.NodeScreen(i);
            ScreenPoint n = neu.NodeScreen(i);
            Bits(o.X, n.X, $"{at} node {i} screen x");
            Bits(o.Y, n.Y, $"{at} node {i} screen y");
        }
    }

    /// <summary>A grid of probe points over the viewport, points on and beside a spread of nodes, and the midpoints and
    /// near-misses of a spread of edges: each must pick the same node and the same branch on both sides, and a repeated
    /// cycling click at each edge midpoint must step through the coincident circuits in the same order.</summary>
    private static void Probe(Old.FastSchematic old, SchematicView neu, string at)
    {
        double w = Math.Max(neu.ViewportWidth, 1), h = Math.Max(neu.ViewportHeight, 1);
        var probes = new List<ScreenPoint>();
        for (int i = 0; i <= 24; i++)
            for (int j = 0; j <= 14; j++)
                probes.Add(new ScreenPoint(-20 + (w + 40) * i / 24.0, -20 + (h + 40) * j / 14.0));
        int nodeStep = Math.Max(1, neu.Nodes.Count / 150);
        for (int i = 0; i < neu.Nodes.Count; i += nodeStep)
        {
            ScreenPoint s = neu.NodeScreen(i);
            probes.Add(s);
            probes.Add(new ScreenPoint(s.X + 9.5, s.Y - 3.25));
            probes.Add(new ScreenPoint(s.X - 24.0, s.Y + 24.0));
            // On the pick circle itself (roundel radius + pick radius) and one ulp either side, so the "<=" edge of the
            // node pick is exercised wherever the sum is exact in doubles.
            double rr = neu.RoundelRadius(neu.Nodes[i].DisplayKv) + SchematicView.NodePickRadiusPx;
            foreach (double x in new[] { s.X + rr, Math.BitIncrement(s.X + rr), Math.BitDecrement(s.X + rr), s.X - rr })
                probes.Add(new ScreenPoint(x, s.Y));
            probes.Add(new ScreenPoint(s.X, s.Y + rr));
        }
        var mids = new List<ScreenPoint>();
        int edgeStep = Math.Max(1, neu.Edges.Count / 120);
        for (int i = 0; i < neu.Edges.Count; i += edgeStep)
        {
            SchematicEdge e = neu.Edges[i];
            if (!e.Valid) continue;
            ScreenPoint a = neu.NodeScreen(e.A), b = neu.NodeScreen(e.B);
            var mid = new ScreenPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            mids.Add(mid);
            probes.Add(mid);
            probes.Add(new ScreenPoint(mid.X + 7.999, mid.Y));
            probes.Add(new ScreenPoint(mid.X, mid.Y - 8.0));
        }
        foreach (ScreenPoint p in probes)
        {
            var op = new Old.Point(p.X, p.Y);
            Assert.True(old.HitTestNode(op) == neu.HitTestNode(p), $"{at}: node pick at ({p.X:R}, {p.Y:R})");
            Assert.True(old.HitTestBranch(op) == neu.HitTestBranch(p), $"{at}: branch pick at ({p.X:R}, {p.Y:R})");
        }
        // Cycling clicks: three at each midpoint, then one a hair away (same spot), then one far away (a new spot).
        foreach (ScreenPoint m in mids)
        {
            foreach (ScreenPoint p in new[] { m, m, m, new ScreenPoint(m.X + 0.5, m.Y + 0.5), new ScreenPoint(m.X + 40, m.Y) })
            {
                var op = new Old.Point(p.X, p.Y);
                Assert.True(old.HitTestBranch(op, cycle: true) == neu.HitTestBranch(p, cycle: true), $"{at}: cycling pick at ({p.X:R}, {p.Y:R})");
            }
        }
    }
}
