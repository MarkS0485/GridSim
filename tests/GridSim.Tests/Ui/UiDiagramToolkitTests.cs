using System.Globalization;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS A1b: the electrical diagram toolkit in GridSim.Ui/Diagrams. Every number here is exact: the
/// symbols are drawn from fixed literals turned by exact quarter turns, the layout helpers are plain arithmetic, and the
/// hit tester compares squared distances, so the tests compare by value (doubles by their bits where it matters), never
/// within a tolerance.
/// </summary>
public sealed class UiDiagramToolkitTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Pt(ScreenPoint p) => $"({F(p.X)},{F(p.Y)})";

    private static string Pen(VisualPen? p) => p is VisualPen v ? $"{ColourRamps.ToHex(v.Colour)}/{F(v.Thickness)}" : "-";

    private static string Fill(VisualFill? f) => f is VisualFill v ? ColourRamps.ToHex(v.Colour) : "-";

    /// <summary>One line per drawing call, every number exact.</summary>
    private static string Describe(VisualCommand c) => c switch
    {
        VisualLineCommand l => $"line {Pen(l.Pen)} {Pt(l.From)}-{Pt(l.To)}",
        VisualEllipseCommand e => $"ellipse {Fill(e.Fill)} {Pen(e.Pen)} {Pt(e.Centre)} r{F(e.RadiusX)},{F(e.RadiusY)}",
        VisualRectCommand r => $"rect {Fill(r.Fill)} {Pen(r.Pen)} {F(r.X)},{F(r.Y)} {F(r.Width)}x{F(r.Height)} r{F(r.RadiusX)}",
        VisualPathCommand p => $"path {Fill(p.Fill)} {Pen(p.Pen)} {string.Join(" ", p.Points.Select(Pt))}{(p.Closed ? " closed" : "")}",
        VisualTextCommand t => $"text '{t.Text}' {t.Weight} {F(t.Size)} {Fill(t.Fill)} {Pt(t.Anchor)} {t.Align}",
        VisualPushTranslateCommand tr => $"push translate {F(tr.X)},{F(tr.Y)}",
        VisualPushScaleCommand sc => $"push scale {F(sc.ScaleX)},{F(sc.ScaleY)}",
        VisualPushOpacityCommand op => $"push opacity {F(op.Opacity)}",
        VisualPopCommand => "pop",
        _ => throw new InvalidOperationException(c.GetType().Name),
    };

    private static List<string> Describe(VisualDisplayList list) => list.Commands.Select(Describe).ToList();

    private static readonly string Cyan = ColourRamps.ToHex(DiagramStyle.Energised);
    private static readonly string Red = ColourRamps.ToHex(DiagramStyle.Closed);
    private static readonly string Green = ColourRamps.ToHex(DiagramStyle.Open);
    private static readonly string Amber = ColourRamps.ToHex(DiagramStyle.Unknown);

    private static DiagramSymbol S(DiagramSymbolKind k, double x = 100, double y = 200, DiagramRotation r = DiagramRotation.None,
        string label = "", DiagramState? state = null) =>
        k is DiagramSymbolKind.Busbar or DiagramSymbolKind.BusSection
            ? new DiagramSymbol("S", k, new ScreenPoint(x, y), r, label, 120, [30.0, 90.0]) { State = state ?? DiagramState.Normal }
            : new DiagramSymbol("S", k, new ScreenPoint(x, y), r, label) { State = state ?? DiagramState.Normal };

    // ---------------------------------------------------------------------------------------------------------------
    // Symbols: primitives, ports and bounds, exactly
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_closed_circuit_breaker_is_exactly_these_primitives()
    {
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,190)",
            $"line {Cyan}/2 (100,210)-(100,220)",
            $"line {Red}/2 (97,187)-(103,193)",
            $"line {Red}/2 (97,193)-(103,187)",
            $"line {Red}/2 (100,210)-(100,190)",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.CircuitBreaker))));
    }

    [Fact]
    public void Open_and_unknown_breakers_and_an_open_disconnector_are_exactly_these_primitives()
    {
        List<string> open = Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.CircuitBreaker, state: new(Switch: DiagramSwitchState.Open))));
        Assert.Equal($"line {Green}/2 (100,210)-(90,193)", open[^1]);
        Assert.Equal($"line {Green}/2 (97,187)-(103,193)", open[2]);
        List<string> unknown = Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.CircuitBreaker, state: new(Switch: DiagramSwitchState.Unknown))));
        Assert.Equal($"line {Amber}/2 (100,210)-(95,191)", unknown[^1]);
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,190)",
            $"line {Cyan}/2 (100,210)-(100,220)",
            $"line {Green}/2 (95,190)-(105,190)",
            $"line {Green}/2 (100,210)-(90,193)",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Disconnector, state: new(Switch: DiagramSwitchState.Open)))));
    }

    [Fact]
    public void Transformers_generator_load_relay_and_line_end_are_exactly_these_primitives()
    {
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,184)",
            $"line {Cyan}/2 (100,216)-(100,220)",
            $"ellipse - {Cyan}/2 (100,194) r10,10",
            $"ellipse - {Cyan}/2 (100,206) r10,10",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Transformer2W))));
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,184)",
            $"line {Cyan}/2 (94,214)-(94,220)",
            $"line {Cyan}/2 (106,214)-(106,220)",
            $"ellipse - {Cyan}/2 (100,194) r10,10",
            $"ellipse - {Cyan}/2 (94,204) r10,10",
            $"ellipse - {Cyan}/2 (106,204) r10,10",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Transformer3W))));
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,188)",
            $"ellipse #FF0F172A {Cyan}/2 (100,200) r12,12",
            $"text 'G' Bold 12 {Cyan} (100,193) Centre",
            "text 'GEN 1' SemiBold 10 #FFE6EDF3 (117,190.5) Left",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Generator, label: "GEN 1"))));
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,204)",
            $"path {Cyan} - (93,204) (107,204) (100,216) closed",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Load))));
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (80,200)-(88,200)",
            $"rect #FF0F172A {Cyan}/2 88,192 24x16 r0",
            "text '87T' Bold 8 #FFE6EDF3 (100,195) Centre",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.ProtectionRelay, label: "87T"))));
        Assert.Equal(new[]
        {
            $"line {Cyan}/2 (100,180)-(100,210)",
            $"path - {Cyan}/2 (94,204) (100,210) (106,204)",
        }, Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.LineEnd))));
    }

    /// <summary>Every kind's ports, unrotated, relative to the origin (busbars: length 120, taps 30 and 90).</summary>
    public static TheoryData<DiagramSymbolKind, string> PortTable => new()
    {
        { DiagramSymbolKind.Busbar, "L(0,0)Left R(120,0)Right T0(30,0)Down T1(90,0)Down" },
        { DiagramSymbolKind.BusSection, "L(0,0)Left R(120,0)Right T0(30,0)Down T1(90,0)Down" },
        { DiagramSymbolKind.CircuitBreaker, "A(0,-20)Up B(0,20)Down" },
        { DiagramSymbolKind.Disconnector, "A(0,-20)Up B(0,20)Down" },
        { DiagramSymbolKind.EarthSwitch, "T(0,-20)Up" },
        { DiagramSymbolKind.Transformer2W, "HV(0,-20)Up LV(0,20)Down" },
        { DiagramSymbolKind.Transformer3W, "HV(0,-20)Up MV(-6,20)Down LV(6,20)Down" },
        { DiagramSymbolKind.Generator, "T(0,-20)Up" },
        { DiagramSymbolKind.Load, "T(0,-20)Up" },
        { DiagramSymbolKind.CurrentTransformer, "A(0,-20)Up B(0,20)Down S(14,0)Right" },
        { DiagramSymbolKind.VoltageTransformer, "T(0,-20)Up S(14,2)Right" },
        { DiagramSymbolKind.ProtectionRelay, "IN(-20,0)Left" },
        { DiagramSymbolKind.CableEnd, "T(0,-20)Up C(0,20)Down" },
        { DiagramSymbolKind.LineEnd, "T(0,-20)Up" },
    };

    [Theory]
    [MemberData(nameof(PortTable))]
    public void Every_symbol_has_exactly_these_ports_under_every_rotation(DiagramSymbolKind kind, string expected)
    {
        Assert.Equal(expected, string.Join(" ", SymbolLibrary.LocalPorts(S(kind)).Select(p => $"{p.Name}{Pt(p.Local)}{p.Direction}")));
        foreach (DiagramRotation r in Enum.GetValues<DiagramRotation>())
        {
            DiagramSymbol s = S(kind, 100, 200, r);
            IReadOnlyList<LocalPort> local = SymbolLibrary.LocalPorts(s);
            IReadOnlyList<DiagramPort> placed = SymbolLibrary.Ports(s);
            Assert.Equal(local.Count, placed.Count);
            for (int i = 0; i < local.Count; i++)
            {
                ScreenPoint want = DiagramGeometry.Rotate(local[i].Local, r);
                Assert.Equal(new ScreenPoint(100 + want.X, 200 + want.Y), placed[i].Position);
                Assert.Equal(DiagramGeometry.Rotate(local[i].Direction, r), placed[i].Direction);
                Assert.Equal("S", placed[i].SymbolId);
            }
        }
    }

    [Fact]
    public void Every_symbol_draws_inside_its_bounds_and_its_ports_sit_on_them_under_every_rotation()
    {
        foreach (DiagramSymbolKind kind in Enum.GetValues<DiagramSymbolKind>())
            foreach (DiagramRotation r in Enum.GetValues<DiagramRotation>())
                foreach (DiagramSwitchState sw in Enum.GetValues<DiagramSwitchState>())
                {
                    DiagramSymbol s = S(kind, 100, 200, r, state: new(Switch: sw));
                    DiagramRect b = SymbolLibrary.Bounds(s);
                    var pts = new List<ScreenPoint>();
                    foreach (VisualCommand c in SymbolLibrary.Primitives(s).Commands)
                    {
                        switch (c)
                        {
                            case VisualLineCommand l: pts.Add(l.From); pts.Add(l.To); break;
                            case VisualPathCommand p: pts.AddRange(p.Points); break;
                            case VisualEllipseCommand e:
                                pts.Add(new(e.Centre.X - e.RadiusX, e.Centre.Y - e.RadiusY));
                                pts.Add(new(e.Centre.X + e.RadiusX, e.Centre.Y + e.RadiusY));
                                break;
                            case VisualRectCommand rc: pts.Add(new(rc.X, rc.Y)); pts.Add(new(rc.X + rc.Width, rc.Y + rc.Height)); break;
                            case VisualTextCommand: break;   // labels sit outside; the G and relay text sit at the centre
                            default: throw new InvalidOperationException(c.GetType().Name);
                        }
                    }
                    Assert.NotEmpty(pts);
                    foreach (ScreenPoint p in pts)
                        Assert.True(b.Contains(p), $"{kind} {r} {sw}: {Pt(p)} is outside its bounds {b}");
                    foreach (DiagramPort port in SymbolLibrary.Ports(s))
                        Assert.True(b.Contains(port.Position), $"{kind} {r}: port {port.Name} is outside its bounds");
                }
    }

    [Fact]
    public void Quarter_turns_are_exact_and_four_make_a_whole_turn()
    {
        var p = new ScreenPoint(3.25, -20);
        Assert.Equal(new ScreenPoint(20, 3.25), DiagramGeometry.Rotate(p, DiagramRotation.Quarter));
        Assert.Equal(new ScreenPoint(-3.25, 20), DiagramGeometry.Rotate(p, DiagramRotation.Half));
        Assert.Equal(new ScreenPoint(-20, -3.25), DiagramGeometry.Rotate(p, DiagramRotation.ThreeQuarter));
        ScreenPoint q = p;
        for (int i = 0; i < 4; i++) q = DiagramGeometry.Rotate(q, DiagramRotation.Quarter);
        Assert.Equal(p, q);
        Assert.Equal(DiagramPortDirection.Right, DiagramGeometry.Rotate(DiagramPortDirection.Up, DiagramRotation.Quarter));
        Assert.Equal(DiagramPortDirection.Up, DiagramGeometry.Rotate(DiagramPortDirection.Left, DiagramRotation.Quarter));
        Assert.Equal(DiagramPortDirection.Down, DiagramGeometry.Rotate(DiagramPortDirection.Up, DiagramRotation.Half));
        // A turned breaker's bounds turn with it.
        Assert.Equal(new DiagramRect(80, 189, 40, 22), SymbolLibrary.Bounds(S(DiagramSymbolKind.CircuitBreaker, 100, 200, DiagramRotation.Quarter)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Styling: the right token for each state
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Styling_picks_the_right_tokens_per_state()
    {
        Assert.Equal(DiagramStyle.Energised, DiagramStyle.ConductorColour(DiagramState.Normal));
        Assert.Equal(DiagramStyle.DeEnergised, DiagramStyle.ConductorColour(new(Energised: false)));
        Assert.Equal(DiagramStyle.DeEnergised, DiagramStyle.ConductorColour(new(Energised: false, Loading: 1.5)));   // nothing flows
        foreach (double f in new[] { 0.0, 0.3, 0.6, 0.6000001, 0.85, 1.0, 1.2 })
            Assert.Equal(ColourRamps.Loading(f), DiagramStyle.ConductorColour(new(Loading: f)));
        Assert.Equal(DiagramStyle.Unknown, DiagramStyle.ConductorColour(new(Loading: double.NaN)));
        Assert.Equal(DiagramStyle.Unknown, DiagramStyle.ConductorColour(new(Loading: double.PositiveInfinity)));
        Assert.Equal(DiagramStyle.Closed, DiagramStyle.SwitchColour(DiagramSwitchState.Closed));
        Assert.Equal(DiagramStyle.Open, DiagramStyle.SwitchColour(DiagramSwitchState.Open));
        Assert.Equal(DiagramStyle.Unknown, DiagramStyle.SwitchColour(DiagramSwitchState.Unknown));
        Assert.Equal(new VisualPen(DiagramStyle.Energised, 5), DiagramStyle.BusbarPen(DiagramState.Normal));
        Assert.Equal(new VisualPen(DiagramStyle.Open, 2), DiagramStyle.SwitchPen(new(Energised: false, Switch: DiagramSwitchState.Open)));
        // The switch blade shows its position even when de-energised; the leads show the energisation.
        List<string> dead = Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.CircuitBreaker, state: new(false, DiagramSwitchState.Open))));
        string slate = ColourRamps.ToHex(DiagramStyle.DeEnergised);
        Assert.StartsWith($"line {slate}/2", dead[0], StringComparison.Ordinal);
        Assert.StartsWith($"line {Green}/2", dead[^1], StringComparison.Ordinal);
        // A loaded transformer takes the loading ramp.
        string loaded = ColourRamps.ToHex(ColourRamps.Loading(0.95));
        Assert.All(Describe(SymbolLibrary.Primitives(S(DiagramSymbolKind.Transformer2W, state: new(Loading: 0.95)))),
            l => Assert.Contains(loaded, l, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The model refuses mistakes; the router is orthogonal
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_model_refuses_mistakes_loudly_and_counts_its_changes()
    {
        var m = new DiagramModel();
        int changed = 0;
        m.Changed += (_, _) => changed++;
        m.Add(new DiagramSymbol("Q1", DiagramSymbolKind.CircuitBreaker, new(0, 0)));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("Q1", DiagramSymbolKind.Disconnector, new(50, 0))));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("", DiagramSymbolKind.Load, new(50, 0))));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("N", DiagramSymbolKind.Load, new(double.NaN, 0))));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("B0", DiagramSymbolKind.Busbar, new(0, 0))));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("B1", DiagramSymbolKind.Busbar, new(0, 0), Length: double.NaN)));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("B2", DiagramSymbolKind.Busbar, new(0, 0), Length: 100, Taps: [101.0])));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("B3", DiagramSymbolKind.Busbar, new(0, 0), Length: 100, Taps: [double.NaN])));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("X", DiagramSymbolKind.CircuitBreaker, new(0, 0), Length: 10)));
        Assert.Throws<ArgumentException>(() => m.Add(new DiagramSymbol("Y", DiagramSymbolKind.CircuitBreaker, new(0, 0), Taps: [1.0])));
        m.Add(new DiagramSymbol("Q2", DiagramSymbolKind.CircuitBreaker, new(0, 100)));
        Assert.Throws<KeyNotFoundException>(() => m.Port("Q1", "HV"));
        Assert.Throws<KeyNotFoundException>(() => m.Port("nope", "A"));
        DiagramConnection c = m.Connect("Q1", "B", "Q2", "A");
        Assert.Equal("Q1.B-Q2.A", c.Id);
        Assert.Throws<ArgumentException>(() => m.Connect("Q1", "B", "Q2", "A"));
        Assert.Throws<KeyNotFoundException>(() => m.SetState("nope", DiagramState.Normal));
        m.SetState("Q1", new(Switch: DiagramSwitchState.Open));
        Assert.Equal(DiagramSwitchState.Open, m.Symbol("Q1").State.Switch);
        Assert.Equal(new ScreenPoint(0, 0), m.Symbol("Q1").Position);
        m.SetConnectionState(c.Id, new(Energised: false));
        Assert.False(m.Connection(c.Id).State.Energised);
        Assert.Equal(5, m.Version);           // Q1, Q2, the connection, two state changes
        Assert.Equal(5, changed);
        // An unbalanced display list cannot be read.
        var dl = new VisualDisplayList();
        dl.PushOpacity(0.5);
        Assert.Throws<InvalidOperationException>(() => dl.Commands);
        Assert.Throws<InvalidOperationException>(() => new VisualDisplayList().Pop());
    }

    [Fact]
    public void Routes_are_orthogonal_and_exact()
    {
        DiagramPort P(double x, double y, DiagramPortDirection d) => new("s", "p", new ScreenPoint(x, y), d);
        Assert.Equal(new ScreenPoint[] { new(10, 0), new(10, 50) }, OrthogonalRouter.Route(P(10, 0, DiagramPortDirection.Down), P(10, 50, DiagramPortDirection.Up)));
        Assert.Equal(new ScreenPoint[] { new(10, 0), new(10, 25), new(70, 25), new(70, 50) },
            OrthogonalRouter.Route(P(10, 0, DiagramPortDirection.Down), P(70, 50, DiagramPortDirection.Up)));
        Assert.Equal(new ScreenPoint[] { new(10, 0), new(40, 0), new(40, 50), new(70, 50) },
            OrthogonalRouter.Route(P(10, 0, DiagramPortDirection.Right), P(70, 50, DiagramPortDirection.Left)));
        // Every route the layout helpers make is axis-aligned.
        DiagramModel m = Bays();
        foreach (DiagramConnection c in m.Connections)
            for (int k = 1; k < c.Points.Count; k++)
                Assert.True(c.Points[k].X == c.Points[k - 1].X || c.Points[k].Y == c.Points[k - 1].Y, $"{c.Id} has a diagonal segment");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Layout helpers place bays deterministically
    // ---------------------------------------------------------------------------------------------------------------

    private static DiagramModel Bays()
    {
        var m = new DiagramModel();
        DiagramLayout.BusbarRow(m, "BB1", new ScreenPoint(40, 200), 3, label: "400 kV");
        DiagramLayout.BusbarRow(m, "BB2", new ScreenPoint(40, 400), 3, label: "132 kV");
        DiagramLayout.FeederBay(m, "BB1", 0, "L1", FeederEnd.Line, BayDirection.Up, currentTransformer: true, endLabel: "DRAX");
        DiagramLayout.FeederBay(m, "BB1", 2, "G1", FeederEnd.Generator, BayDirection.Up, disconnector: false);
        DiagramLayout.TransformerBetween(m, "BB1", 1, "BB2", 1, "T1", "SGT1");
        DiagramLayout.FeederBay(m, "BB2", 0, "D1", FeederEnd.Load);
        DiagramLayout.FeederBay(m, "BB2", 2, "K1", FeederEnd.Cable);
        return m;
    }

    [Fact]
    public void Layout_helpers_place_bays_deterministically()
    {
        DiagramModel m = Bays();
        string Place(string id) { DiagramSymbol s = m.Symbol(id); return $"{Pt(s.Position)}{s.Rotation}"; }

        BusbarRowResult row = DiagramLayout.BusbarRow(new DiagramModel(), "X", new ScreenPoint(40, 200), 3);
        Assert.Equal(new[] { "T0", "T1", "T2" }, row.TapPorts);
        Assert.Equal(new ScreenPoint[] { new(80, 200), new(160, 200), new(240, 200) }, row.TapPositions);
        Assert.Equal(240, m.Symbol("BB1").Length);

        // An upward feeder bay: disconnector 40 above the bar, then breaker, CT, line end every 50, turned half way.
        Assert.Equal("(80,160)Half", Place("L1/DS"));
        Assert.Equal("(80,110)Half", Place("L1/CB"));
        Assert.Equal("(80,60)Half", Place("L1/CT"));
        Assert.Equal("(80,10)Half", Place("L1/END"));
        Assert.Equal("DRAX", m.Symbol("L1/END").Label);
        Assert.Equal("(240,160)Half", Place("G1/CB"));       // no disconnector: the breaker comes first
        Assert.Equal("(240,110)Half", Place("G1/END"));
        // The transformer bay: breakers 40 inside each bar, the transformer half way.
        Assert.Equal("(160,240)None", Place("T1/CB1"));
        Assert.Equal("(160,300)None", Place("T1/TX"));
        Assert.Equal("(160,360)None", Place("T1/CB2"));
        // Downward bays.
        Assert.Equal("(80,440)None", Place("D1/DS"));
        Assert.Equal("(80,490)None", Place("D1/CB"));
        Assert.Equal("(80,540)None", Place("D1/END"));
        Assert.Equal("(240,540)None", Place("K1/END"));
        // Each connection runs straight from port to port (the bays are in line with their taps).
        DiagramConnection first = m.Connection("BB1.T0-L1/DS.A");
        Assert.Equal(new ScreenPoint[] { new(80, 200), new(80, 180) }, first.Points);
        Assert.Equal(new ScreenPoint[] { new(160, 380), new(160, 400) }, m.Connection("T1/CB2.B-BB2.T1").Points);

        // The same calls give the same diagram, call for call.
        Assert.Equal(Describe(DiagramRenderer.Render(Bays(), DiagramViewport.Identity)), Describe(DiagramRenderer.Render(m, DiagramViewport.Identity)));
        // And the layout refuses what it cannot place.
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagramLayout.BusbarRow(new DiagramModel(), "Z", new(0, 0), 0));
        Assert.Throws<ArgumentException>(() => DiagramLayout.TransformerBetween(m, "BB1", 0, "BB2", 1, "T9"));    // not in line
        var tight = new DiagramModel();
        DiagramLayout.BusbarRow(tight, "U", new(0, 0), 1);
        DiagramLayout.BusbarRow(tight, "V", new(0, 159), 1);
        Assert.Throws<ArgumentException>(() => DiagramLayout.TransformerBetween(tight, "U", 0, "V", 0, "T"));   // under 160 apart
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Hit testing, exact on a probe grid
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hit_testing_is_exact_on_a_probe_grid()
    {
        var m = new DiagramModel();
        m.Add(new DiagramSymbol("B", DiagramSymbolKind.Busbar, new(0, 0), Length: 200, Taps: [50.0, 150.0]));
        m.Add(new DiagramSymbol("Q", DiagramSymbolKind.CircuitBreaker, new(50, 70)));     // bounds x 39..61, y 50..90
        DiagramConnection c = m.Connect("B", "T0", "Q", "A");                              // (50,0) to (50,50)
        const double tol = 2;
        int symbols = 0, connections = 0, misses = 0;
        for (double y = -10; y <= 100; y += 0.5)
            for (double x = -10; x <= 210; x += 0.5)
            {
                // The expected answer worked out independently of the tester.
                string expected;
                if (x >= 39 && x <= 61 && y >= 50 && y <= 90) expected = "Symbol:Q";
                else if (x >= 0 && x <= 200 && y >= -4 && y <= 4) expected = "Symbol:B";
                else
                {
                    double dy = y < 0 ? -y : y > 50 ? y - 50 : 0, dx = x - 50;
                    expected = dx * dx + dy * dy <= tol * tol ? "Connection:" + c.Id : "none";
                }
                DiagramHit? hit = DiagramHitTester.HitTest(m, new ScreenPoint(x, y), tol);
                string got = hit is null ? "none" : $"{hit.Kind}:{hit.Id}";
                Assert.True(expected == got, $"at ({x}, {y}): expected {expected}, got {got}");
                if (expected.StartsWith("Symbol", StringComparison.Ordinal)) symbols++;
                else if (expected == "none") misses++;
                else connections++;
            }
        Assert.True(symbols > 1000 && connections > 100 && misses > 10000, $"probe grid too thin: {symbols}/{connections}/{misses}");
        Assert.Equal(DiagramSymbolKind.CircuitBreaker, DiagramHitTester.HitTest(m, new(50, 70), tol)!.SymbolKind);
        Assert.Null(DiagramHitTester.HitTest(m, new(double.NaN, 70), tol));
        // Through a viewport: the tolerance is in screen pixels.
        var view = new DiagramViewport(2, 10, 20);
        Assert.Equal("Q", DiagramHitTester.HitTestScreen(m, view, view.ToScreen(new(50, 70)))!.Id);
        Assert.Equal(c.Id, DiagramHitTester.HitTestScreen(m, view, new ScreenPoint(10 + 2 * 50 + 3.5, 20 + 2 * 25), 4)!.Id);  // 1.75 units off
        Assert.Null(DiagramHitTester.HitTestScreen(m, view, new ScreenPoint(10 + 2 * 50 + 4.5, 20 + 2 * 25), 4));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Viewport and renderer
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_viewport_fits_zooms_and_inverts_exactly()
    {
        var extent = new DiagramRect(0, 0, 200, 100);
        DiagramViewport fit = DiagramViewport.Fit(extent, 448, 448, margin: 24);
        Assert.Equal(new DiagramViewport(2, 24, 124), fit);                              // width-limited: (448 - 48) / 200
        Assert.Equal(new DiagramViewport(4, 464, 464), DiagramViewport.Fit(new DiagramRect(10, 10, 10, 10), 48 + 1000, 48 + 1000, 24));   // capped at 4
        Assert.Equal(DiagramViewport.Identity, DiagramViewport.Fit(null, 400, 400));
        Assert.Equal(DiagramViewport.Identity, DiagramViewport.Fit(extent, 40, 400));   // too small for the margins
        Assert.Equal(new ScreenPoint(224, 224), fit.ToScreen(extent.Centre));
        Assert.Equal(new ScreenPoint(100, 50), fit.ToDiagram(new ScreenPoint(224, 224)));
        DiagramViewport z = fit.ZoomAbout(new ScreenPoint(100, 100), 2);
        Assert.Equal(4, z.Scale);
        Assert.Equal(fit.ToDiagram(new ScreenPoint(100, 100)), z.ToDiagram(new ScreenPoint(100, 100)));
        Assert.Equal(DiagramViewport.MaxScale, fit.ZoomAbout(new(0, 0), 1e6).Scale);
        Assert.Equal(DiagramViewport.MinScale, fit.ZoomAbout(new(0, 0), 1e-6).Scale);
        Assert.Throws<ArgumentOutOfRangeException>(() => fit.ZoomAbout(new(0, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => fit.ZoomAbout(new(0, 0), double.NaN));
    }

    [Fact]
    public void The_renderer_draws_connections_then_symbols_then_labels_inside_the_viewport_layers()
    {
        DiagramModel m = Bays();
        m.AddLabel(new DiagramLabel("NOTE", new(5, 5), 9, DiagramLabelAlign.Centre, Muted: true));
        List<string> d = Describe(DiagramRenderer.Render(m, new DiagramViewport(1.5, 7, -3)));
        Assert.Equal("push translate 7,-3", d[0]);
        Assert.Equal("push scale 1.5,1.5", d[1]);
        Assert.Equal(new[] { "pop", "pop" }, d.TakeLast(2));
        Assert.Equal($"text 'NOTE' SemiBold 9 #FF94A3B8 (5,5) Centre", d[^3]);
        // The connections come first, one open path each, in model order.
        for (int i = 0; i < m.Connections.Count; i++)
            Assert.Equal($"path - {Pen(DiagramStyle.ConductorPen(m.Connections[i].State))} {string.Join(" ", m.Connections[i].Points.Select(Pt))}", d[2 + i]);
        // Then the symbols, each exactly as the library draws it on its own, in placement order.
        var symbols = m.Symbols.SelectMany(sym => Describe(SymbolLibrary.Primitives(sym))).ToList();
        Assert.Equal(symbols, d.Skip(2 + m.Connections.Count).Take(symbols.Count));
        Assert.Equal(m.Extent(), m.Extent());
        Assert.Null(new DiagramModel().Extent());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The builders for the A3 views
    // ---------------------------------------------------------------------------------------------------------------

    private static SubstationMapInput Typical() => new()
    {
        VoltagePu = 1.012, AngleDeg = -12.34, BaseKv = 400, DemandMw = 250.6, ReactiveMvar = 81.2, BusType = "PV",
        Connections =
        [
            new("L1", "EGGBOROUGH 400", "LINE", 812.4, true, 0.64),
            new("T1", "DRAX 132", "TRANSFORMER", -240.7, false, 0.97),
        ],
        Generators = [new("DRAX-1", "Biomass", ColourRamps.FromHex("#2FD27F"), 645.0)],
    };

    [Fact]
    public void The_substation_builder_makes_a_bay_per_connection_generator_and_demand()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");   // the builder's text is invariant
            DiagramModel m = SubstationDiagramBuilder.Build(Typical());
            Assert.Equal("PV 400 kV", m.Symbol(SubstationDiagramBuilder.BusbarId).Label);
            Assert.Equal(2, m.Symbol(SubstationDiagramBuilder.BusbarId).Taps!.Count);
            Assert.Equal(DiagramSymbolKind.LineEnd, m.Symbol("C0/END").Kind);
            Assert.Equal(DiagramSymbolKind.Transformer2W, m.Symbol("C1/TX").Kind);
            Assert.Equal(0.97, m.Symbol("C1/TX").State.Loading);
            Assert.Equal("DRAX 132", m.Symbol("C1/END").Label);
            Assert.Equal(DiagramSymbolKind.Generator, m.Symbol("G0/END").Kind);
            Assert.Equal("DRAX-1 645 MW", m.Symbol("G0/END").Label);
            Assert.Equal(DiagramSymbolKind.Load, m.Symbol("D/END").Kind);
            Assert.All(m.Connections.Where(c => c.Id.StartsWith("C1", StringComparison.Ordinal) || c.Id.Contains("-C1", StringComparison.Ordinal)),
                c => Assert.Equal(0.97, c.State.Loading));
            Assert.Contains(m.Labels, l => l.Text == "1.012 pu   404.8 kV   -12.3 deg");
            Assert.Contains(m.Labels, l => l.Text == "241 MW");
            // Deterministic: the same input, the same drawing.
            Assert.Equal(Describe(DiagramRenderer.Render(SubstationDiagramBuilder.Build(Typical()), DiagramViewport.Identity)),
                Describe(DiagramRenderer.Render(m, DiagramViewport.Identity)));
        }
        finally { CultureInfo.CurrentCulture = saved; }

        // A dead bus (no voltage) draws de-energised; no data at all still makes a one-bay bar.
        DiagramModel dead = SubstationDiagramBuilder.Build(new SubstationMapInput { VoltagePu = double.NaN });
        Assert.False(dead.Symbol(SubstationDiagramBuilder.BusbarId).State.Energised);
        Assert.Single(dead.Symbols);
    }

    [Fact]
    public void The_transformer_builder_carries_the_loading_and_the_tap_changer()
    {
        var v = new TransformerSchematicInput { LoadingPct = 82, HvKvNom = 400, LvKvNom = 132, HvKvLive = 405.3, LvKvLive = 131.1,
            FlowMw = 812.4, RatingMva = 1000, TapRatio = 1.0375, HasOltc = true, TapMin = 0.85, TapMax = 1.15 };
        DiagramModel m = TransformerDiagramBuilder.Build(v);
        DiagramSymbol tx = m.Symbol("TX/TX");
        Assert.Equal(0.82, tx.State.Loading);
        Assert.True(tx.State.Energised);
        Assert.Equal("1000 MVA", tx.Label);
        Assert.Contains(m.Labels, l => l.Text == "OLTC tap 1.037 (0.850 to 1.150)" || l.Text == "OLTC tap 1.038 (0.850 to 1.150)");
        Assert.DoesNotContain(TransformerDiagramBuilder.Build(v with { HasOltc = false }).Labels, l => l.Text.StartsWith("OLTC", StringComparison.Ordinal));
        DiagramModel cold = TransformerDiagramBuilder.Build(new TransformerSchematicInput());
        Assert.False(cold.Symbol("TX/TX").State.Energised);           // no live voltage on either side
        Assert.False(cold.Symbol(TransformerDiagramBuilder.HvBusId).State.Energised);
    }
}
