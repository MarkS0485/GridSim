using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;
using Xunit;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-18, the single-line diagram core: model, automatic layout, geometry, router, hit testing, colouring and export. The oracles
/// are independent of the code under test: a layout worked by hand from the written rules, geometric predicates re-derived here (no
/// shared helper with the router), analytic point-in-rectangle and distance-to-segment, and a bijection between the case's own element
/// counts and the diagram's objects.
/// </summary>
public sealed class R418SldCoreTests
{
    // --------------------------------------------------------------------------------------------------------------- the hand case

    /// <summary>
    /// Three buses: 1 (400 kV, slack, a generator), 2 (400 kV), 3 (132 kV, a 50 MW load). Branch 0 is a line 1-2, branch 1 a
    /// transformer 2-3. Small enough to place by hand.
    /// </summary>
    private static GridModel HandCase() => new()
    {
        BaseMva = 100,
        Name = "hand",
        Buses =
        [
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, BaseKv: 400),
            new Bus(3, BusType.Pq, Pd: 50, Qd: 10, BaseKv: 132),
        ],
        Generators = [new Generator(1, 100, Name: "G1")],
        Branches =
        [
            new Branch(1, 2, 0.01, 0.1, Name: "L12"),
            new Branch(2, 3, 0.005, 0.08, IsTransformer: true, Name: "T23"),
        ],
    };

    [Fact]
    public void The_hand_case_is_placed_where_the_written_rules_put_it()
    {
        // Worked by hand with the default options (slot pitch 28, busbar margin 14, bus gap 40, thickness 4, drop 40, track pitch 6, clearance 24):
        //  rows: two voltage levels, so row 0 = 400 kV (buses 1, 2), row 1 = 132 kV (bus 3).
        //  slots: bus 1 carries the line end and the generator (2 bottom slots) -> width 28 + 2*28 = 84; bus 2 the line end and the
        //  transformer's first end (2 bottom slots) -> 84; bus 3 the transformer's second end (1 top slot) and its load (1 bottom) -> 56.
        //  x: row 0 is 84 + 40 + 84 = 208 wide, centred on 0 -> centres -62 and 62; row 1 centre 0. (The barycentre sweeps keep the order:
        //  bus 2, the only one with a neighbour in the row below, has key 0 and bus 1, with none, keeps -62.)
        //  y: row 0 at 0; two edges start in row 0 so its channel holds 2 tracks: y1 = 2 + 40 + 2*6 + 24 + 2 = 80.
        SldModel m = SldModel.From(HandCase());
        SldPlacement p = SldAutoLayout.Place(m);
        Assert.Equal(new double[] { -62, 62, 0 }, p.X);
        Assert.Equal(new double[] { 0, 0, 80 }, p.Y);
        Assert.Equal("auto", p.Layout);

        SldGeometry g = SldGeometryBuilder.Build(m, p);
        Assert.Equal(new SldBox(-104, -2, -20, 2), g.Symbol("bus/1")!.Box);
        Assert.Equal(new SldBox(20, -2, 104, 2), g.Symbol("bus/2")!.Box);
        Assert.Equal(new SldBox(-28, 78, 28, 82), g.Symbol("bus/3")!.Box);
        // Slot x: left + 14 + (k + 0.5) * 28. Bus 1: -76, -48. Bus 2: 48, 76. Bus 3: 0.
        Assert.Equal(new SldBox(-82, 10, -70, 24), g.Symbol("branch/0")!.Box);        // line 12 x 14 under bus 1, slot 0, 8 below the bar
        Assert.Equal(new SldBox(-58, 14, -38, 34), g.Symbol("gen/0")!.Box);           // generator 20 x 20, stem 12
        Assert.Equal(new SldBox(67, 10, 85, 36), g.Symbol("branch/1")!.Box);          // transformer 18 x 26 under bus 2, slot 1
        Assert.Equal(new SldBox(-8, 94, 8, 110), g.Symbol("load/3")!.Box);            // load 16 x 16 under bus 3
        Assert.Equal(7, g.Symbols.Count);                                             // 3 busbars, 2 branches, 1 generator, 1 load

        // Connections. The line is a U round the bottom of the row (both ends in one row): down to the first free track y = 2 + 40,
        // across to bus 2's slot 0 (x = 48), up to its bottom. The transformer's Z first tries y = 42, which the U already uses over
        // x 0..48, so it takes the next track, y = 48.
        Assert.Equal(new[] { new SldPoint(-76, 24), new SldPoint(-76, 42), new SldPoint(48, 42), new SldPoint(48, 2) }, Conn(g, "branch/0/b").Points);
        Assert.Equal(new[] { new SldPoint(76, 36), new SldPoint(76, 48), new SldPoint(0, 48), new SldPoint(0, 78) }, Conn(g, "branch/1/b").Points);
        Assert.Equal(new[] { new SldPoint(-48, 2), new SldPoint(-48, 14) }, Conn(g, "gen/0/c").Points);
        Assert.Empty(g.Crossings);
        Assert.Empty(g.FindCrossings());
        Assert.Empty(g.FindOverlaps());
    }

    private static SldConnection Conn(SldGeometry g, string id) => g.Connections.Single(c => c.Id == id);

    // ------------------------------------------------------------------------------------------------------------------ determinism

    [Fact]
    public void The_same_model_gives_the_same_geometry_bit_for_bit_on_every_run_and_thread()
    {
        GridModel gm = HandCase();
        string first = SldGeometryBuilder.Build(SldModel.From(gm), SldAutoLayout.Place(SldModel.From(gm))).Canonical();
        for (int i = 0; i < 5; i++)
            Assert.Equal(first, SldGeometryBuilder.Build(SldModel.From(gm), SldAutoLayout.Place(SldModel.From(gm))).Canonical());
        var results = new string[8];
        Parallel.For(0, 8, i => results[i] = SldDiagrams.Build(SldModel.From(gm), SldLayoutMode.Auto).Geometry.Canonical());
        Assert.All(results, r => Assert.Equal(first, r));
        // The hash is a fixed value: a different machine or OS must give it too (the numbers are sums of whole numbers and halves).
        Assert.Equal(WorkbenchText.Sha256(first), SldDiagrams.Build(SldModel.From(gm), SldLayoutMode.Auto).Geometry.Sha256());
    }

    [Fact]
    public void A_shipped_case_has_a_pinned_geometry_hash()
    {
        // Frozen on first run (then reviewed): the uk case's automatic diagram. Cross-machine determinism is this equality.
        GridModel uk = JsonGridLoader.LoadDirectory(CaseDir("uk"), dispatch: false);
        string hash = SldDiagrams.Build(SldModel.From(uk), SldLayoutMode.Auto).Geometry.Sha256();
        Assert.Equal(hash, SldDiagrams.Build(SldModel.From(uk), SldLayoutMode.Auto).Geometry.Sha256());
        Assert.Equal(UkHash, hash);
    }

    private const string UkHash = "1ece70d5a5f0ff7abd04bd5bd0e1a8abf676ae8b6ce1ef2dd145e94476a886ca";

    // ---------------------------------------------------------------------------------------------- bijection, router, overlap

    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (string c in new[] { "uk", "intl/france", "intl/ireland-all-island", "intl/iberia", "gb-synthetic" }) d.Add("json:" + c);
        foreach (string c in new[] { "case9", "case14", "case30", "case57", "case118", "case24_ieee_rts" }) d.Add("m:" + c);
        return d;
    }

    private static GridModel Load(string spec)
    {
        string kind = spec[..spec.IndexOf(':', StringComparison.Ordinal)], name = spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return kind == "json"
            ? JsonGridLoader.LoadDirectory(CaseDir(name), dispatch: false)
            : MatpowerParser.ParseFile(Path.Combine(RepoRoot(), "data", "matpower", "lib", name + ".m"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_element_of_the_case_is_one_object_of_the_diagram(string spec)
    {
        GridModel gm = Load(spec);
        SldModel m = SldModel.From(gm);
        SldGeometry g = SldDiagrams.Build(m, SldLayoutMode.Auto).Geometry;

        // The expected ids come from the case's own lists, written out here, not from SldModel.
        var expected = new List<string>();
        expected.AddRange(gm.Buses.Select(b => "bus/" + b.Id));
        for (int i = 0; i < gm.Branches.Count; i++) expected.Add("branch/" + i);
        for (int i = 0; i < gm.AllGenerators.Count; i++) expected.Add("gen/" + i);
        expected.AddRange(gm.Buses.Where(b => b.Pd != 0 || b.Qd != 0).Select(b => "load/" + b.Id));
        expected.AddRange(gm.Buses.Where(b => b.Gs != 0 || b.Bs != 0).Select(b => "shunt/" + b.Id));

        // Branches or units naming a bus the model lacks cannot be drawn; the shipped cases have none.
        Assert.DoesNotContain(g.Omitted, o => o.Contains("not have", StringComparison.Ordinal));
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), g.Symbols.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(expected.Count, g.Symbols.Select(s => s.Id).Distinct().Count());
        // Each non-bus element has its connection(s): a branch two, a hanging element one.
        foreach (SldSymbol s in g.Symbols.Where(s => s.Kind != SldKind.Busbar))
        {
            int n = g.Connections.Count(c => c.ElementId == s.Id);
            Assert.Equal(s.Kind is SldKind.Line or SldKind.Transformer ? 2 : 1, n);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_connection_is_orthogonal_joins_its_two_ports_and_crosses_no_symbol(string spec)
    {
        SldGeometry g = SldDiagrams.Build(SldModel.From(Load(spec)), SldLayoutMode.Auto).Geometry;
        // Ports by id (every port of every symbol).
        var ports = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (SldSymbol s in g.Symbols) foreach (SldPort p in s.Ports) ports[p.Id] = (p.X, p.Y);

        foreach (SldConnection c in g.Connections)
        {
            Assert.True(c.Points.Count >= 2, c.Id);
            // Orthogonal: every segment keeps x or keeps y, and none has zero length.
            for (int i = 0; i + 1 < c.Points.Count; i++)
            {
                SldPoint a = c.Points[i], b = c.Points[i + 1];
                Assert.True(a.X == b.X ^ a.Y == b.Y, $"{c.Id} segment {i} is not a horizontal or vertical run of non-zero length");
            }
            // Joins its two ports.
            Assert.Equal(ports[c.FromPort], (c.Points[0].X, c.Points[0].Y));
            Assert.Equal(ports[c.ToPort], (c.Points[^1].X, c.Points[^1].Y));
        }
        Assert.Empty(g.Crossings);

        // Crosses no symbol box: re-derived here. A segment crosses a box when it passes through the open interior; one that only
        // meets the boundary (its own end point on a port) does not.
        foreach (SldConnection c in g.Connections)
            foreach (SldSymbol s in g.Symbols)
                for (int i = 0; i + 1 < c.Points.Count; i++)
                {
                    SldPoint a = c.Points[i], b = c.Points[i + 1];
                    double lox = Math.Min(a.X, b.X), hix = Math.Max(a.X, b.X), loy = Math.Min(a.Y, b.Y), hiy = Math.Max(a.Y, b.Y);
                    bool inside = a.X == b.X
                        ? a.X > s.Box.Left && a.X < s.Box.Right && loy < s.Box.Bottom && hiy > s.Box.Top
                        : a.Y > s.Box.Top && a.Y < s.Box.Bottom && lox < s.Box.Right && hix > s.Box.Left;
                    Assert.False(inside, $"{c.Id} passes through {s.Id}");
                }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void No_two_symbols_overlap(string spec)
    {
        SldGeometry g = SldDiagrams.Build(SldModel.From(Load(spec)), SldLayoutMode.Auto).Geometry;
        // Sorted sweep over the boxes (an independent O(n log n) check, not the geometry's own FindOverlaps).
        var boxes = g.Symbols.Select(s => (s.Id, s.Box)).OrderBy(b => b.Box.Left).ToList();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count && boxes[j].Box.Left < boxes[i].Box.Right; j++)
            {
                SldBox a = boxes[i].Box, b = boxes[j].Box;
                bool overlap = a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
                Assert.False(overlap, $"{boxes[i].Id} overlaps {boxes[j].Id}");
            }
    }

    [Fact]
    public void A_vertical_lane_carries_an_edge_that_spans_several_rows_without_crossing()
    {
        // 1 (400) - 2 (275) - 3 (132) in a chain, and a long edge 1 - 3 that passes the middle row.
        var gm = new GridModel
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, BaseKv: 275), new Bus(3, BusType.Pq, BaseKv: 132, Pd: 10)],
            Generators = [new Generator(1, 10)],
            Branches = [new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1), new Branch(1, 3, 0.02, 0.2)],
        };
        SldGeometry g = SldDiagrams.Build(SldModel.From(gm), SldLayoutMode.Auto).Geometry;
        Assert.Empty(g.FindCrossings());
        Assert.Empty(g.Crossings);
        // The long edge's second half has more than the three corners of a Z: it went through a lane.
        Assert.True(Conn(g, "branch/2/b").Points.Count >= 5);
    }

    [Fact]
    public void Disconnected_parts_are_laid_out_one_below_another_and_never_overlap()
    {
        var gm = new GridModel
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Slack, BaseKv: 132), new Bus(4, BusType.Pq, BaseKv: 132)],
            Generators = [new Generator(1, 10), new Generator(3, 10)],
            Branches = [new Branch(1, 2, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1)],
        };
        SldGeometry g = SldDiagrams.Build(SldModel.From(gm), SldLayoutMode.Auto).Geometry;
        Assert.Empty(g.FindOverlaps());
        Assert.Empty(g.FindCrossings());
        Assert.True(g.Symbol("bus/3")!.Box.Top > g.Symbol("bus/2")!.Box.Bottom + 40);   // below, with the component gap and the hanging symbols' depth
    }

    [Fact]
    public void A_case_placed_from_its_own_layout_uses_it_and_a_case_without_one_falls_back()
    {
        GridModel gm = HandCase();
        SldModel m = SldModel.From(gm);
        SldDiagram none = SldDiagrams.Build(m, SldLayoutMode.Case);
        Assert.Equal(SldLayoutMode.Auto, none.Mode);        // every bus at the origin: no layout to use
        Assert.False(none.CanMove);

        GridModel placed = gm.CopyWith(buses: [gm.Buses[0] with { X = 0.1, Y = 0.1 }, gm.Buses[1] with { X = 0.5, Y = 0.1 }, gm.Buses[2] with { X = 0.3, Y = 0.4 }]);
        SldDiagram d = SldDiagrams.Build(SldModel.From(placed), SldLayoutMode.Case);
        Assert.Equal(SldLayoutMode.Case, d.Mode);
        Assert.True(d.CanMove);
        // Centres are x and y times the layout scale (2000).
        Assert.Equal((200, 200), (d.Geometry.Symbol("bus/1")!.Box.CentreX, d.Geometry.Symbol("bus/1")!.Box.CentreY));
        Assert.Equal((1000, 200), (d.Geometry.Symbol("bus/2")!.Box.CentreX, d.Geometry.Symbol("bus/2")!.Box.CentreY));
        Assert.Equal((600, 800), (d.Geometry.Symbol("bus/3")!.Box.CentreX, d.Geometry.Symbol("bus/3")!.Box.CentreY));
        Assert.Empty(d.Geometry.FindCrossings());
        Assert.Empty(d.Geometry.FindOverlaps());
    }

    // ------------------------------------------------------------------------------------------------------------------- hit testing

    [Fact]
    public void Hit_testing_is_point_in_rectangle_then_distance_to_segment()
    {
        SldGeometry g = SldGeometryBuilder.Build(SldModel.From(HandCase()), SldAutoLayout.Place(SldModel.From(HandCase())));
        // Inside the box of the line symbol [-82,-70] x [10,24].
        Assert.Equal(new SldHit(SldHitKind.Symbol, "branch/0", "branch/0", 0), SldHitTester.HitTest(g, -76, 17));
        // On the boundary counts as inside.
        Assert.Equal("branch/0", SldHitTester.HitTest(g, -82, 10).Id);
        // Inside the busbar of bus 3 [-28,28] x [78,82].
        Assert.Equal("bus/3", SldHitTester.HitTest(g, 20, 80).Id);
        // Just outside the busbar, beyond the tolerance of any connection: nothing.
        Assert.Equal(SldHitKind.None, SldHitTester.HitTest(g, 20, 70.5, 1).Kind);
        // On the horizontal run y = 48 of the transformer's connection from x 0 to 76: 3 below it is 3 away; within 4 hits, within 2 does not.
        SldHit near = SldHitTester.HitTest(g, 40, 51, 4);
        Assert.Equal(SldHitKind.Connection, near.Kind);
        Assert.Equal("branch/1", near.ElementId);
        Assert.Equal(3, near.Distance, 9);
        Assert.Equal(SldHitKind.None, SldHitTester.HitTest(g, 40, 51, 2).Kind);
        // The distance function: perpendicular, and to the end point past the end.
        Assert.Equal(5, SldGeometryMath.DistanceToSegment(0, 5, new SldPoint(-3, 0), new SldPoint(3, 0)), 12);
        Assert.Equal(5, SldGeometryMath.DistanceToSegment(7, 3, new SldPoint(0, 0), new SldPoint(3, 0)) , 12);   // (7,3) to (3,0): 4 and 3
    }

    // ---------------------------------------------------------------------------------------------------------- colouring and export

    [Fact]
    public void Colouring_by_voltage_level_gives_each_level_its_own_colour_and_greys_what_is_out_of_service()
    {
        GridModel gm = HandCase();
        SldModel m = SldModel.From(gm);
        SldGeometry g = SldDiagrams.Build(m, SldLayoutMode.Auto).Geometry;
        SldColourSet c = SldColouring.Colour(m, g, SldColourMode.VoltageLevel);
        string At(string id) => c.Symbols[g.Symbols.ToList().FindIndex(s => s.Id == id)];
        Assert.Equal(At("bus/1"), At("bus/2"));                  // both 400 kV
        Assert.NotEqual(At("bus/1"), At("bus/3"));               // 132 kV
        Assert.Equal(2, c.Legend.Count);
        Assert.Equal("400 kV", c.Legend[0].Label);               // highest first

        // An out-of-service branch is grey in every mode.
        GridModel off = gm.CopyWith(branches: [gm.Branches[0] with { InService = false }, gm.Branches[1]]);
        SldModel mo = SldModel.From(off);
        SldGeometry go = SldDiagrams.Build(mo, SldLayoutMode.Auto).Geometry;
        SldColourSet co = SldColouring.Colour(mo, go, SldColourMode.VoltageLevel);
        Assert.Equal(SldColouring.Unknown, co.Symbols[go.Symbols.ToList().FindIndex(s => s.Id == "branch/0")]);
    }

    [Fact]
    public void Colouring_by_result_uses_the_power_flow_and_a_missing_value_is_grey_never_a_colour_for_zero()
    {
        GridModel gm = MatpowerParser.ParseFile(Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m"));
        SldModel m = SldModel.From(gm);
        SldGeometry g = SldDiagrams.Build(m, SldLayoutMode.Auto).Geometry;
        SldColourSet none = SldColouring.Colour(m, g, SldColourMode.Voltage);          // no study yet
        Assert.All(g.Symbols.Select((s, i) => (s, i)).Where(t => t.s.Kind == SldKind.Busbar), t => Assert.Equal(SldColouring.Unknown, none.Symbols[t.i]));
        SldColourInput input = SldResults.PowerFlow(m);
        Assert.NotNull(input.NodeVm);
        SldColourSet v = SldColouring.Colour(m, g, SldColourMode.Voltage, input);
        // case 9 is solved near 1 pu: every bus is in the normal band, none grey.
        Assert.All(g.Symbols.Select((s, i) => (s, i)).Where(t => t.s.Kind == SldKind.Busbar), t => Assert.Equal("#009E73", v.Symbols[t.i]));
    }

    [Fact]
    public void The_svg_is_a_pure_function_of_the_geometry_and_lists_every_object()
    {
        SldModel m = SldModel.From(HandCase());
        SldDiagram d = SldDiagrams.Build(m, SldLayoutMode.Auto);
        SldColourSet c = SldColouring.Colour(m, d.Geometry, SldColourMode.VoltageLevel);
        string a = SldExport.ToSvg(d.Geometry, c, "hand"), b = SldExport.ToSvg(d.Geometry, c, "hand");
        Assert.Equal(a, b);
        Assert.StartsWith("<svg ", a, StringComparison.Ordinal);
        Assert.EndsWith("</svg>", a, StringComparison.Ordinal);
        foreach (SldSymbol s in d.Geometry.Symbols) Assert.Contains($"data-id=\"{s.Id}\"", a, StringComparison.Ordinal);
        Assert.Equal(d.Geometry.Connections.Count, System.Text.RegularExpressions.Regex.Count(a, "<polyline"));
        Assert.DoesNotContain("<script", a, StringComparison.OrdinalIgnoreCase);
    }
}
