using System.Globalization;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-07: placed protection devices and the coordination workflow. Oracles are independent of the code under test:
/// the inverse-time equations are evaluated here with Math.Pow (the code uses DetMath), the tabulated interpolation is
/// re-derived in log space, the fault currents are Ohm's law on the series impedances of the hand networks, and the
/// grading margins are subtractions of the hand times. The incumbent GradeChain is the oracle for relay pairs.
/// </summary>
public sealed class PlacedDeviceCoordinationTests
{
    private const double BaseMva = 100.0, Kv = 132.0, C = 1.10;
    private static double IBaseA => BaseMva / (Math.Sqrt(3.0) * Kv) * 1000.0;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Gen (X''d 0.1) at bus 1; line 1-2 X 0.1 (branch 0); line 2-3 X 0.2 (branch 1).</summary>
    private static GridModel Radial() => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.1) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.1), new Branch(2, 3, R: 0.0, X: 0.2) },
    };

    private const string SyntheticFuses = """
    { "format": "gridsim-protection-curve/1",
      "source": { "kind": "synthetic", "title": "GridSim synthetic test fixture - NOT vendor data", "publisher": "GridSim tests",
                  "url": "n/a (synthetic)", "locator": "this file", "retrieved": "2026-11-17" },
      "curves": [
        { "id": "T-melt",  "kind": "min-melt",    "points": [[100, 10.0], [1000, 0.01]] },
        { "id": "T-clear", "kind": "total-clear", "points": [[100, 20.0], [1000, 0.05]] },
        { "id": "T-rec",   "kind": "operate",     "points": [[200, 2.0], [2000, 0.02]] } ] }
    """;

    private static CurveLibrary Library()
    {
        var lib = new CurveLibrary();
        lib.Load(Encoding.UTF8.GetBytes(SyntheticFuses), "synthetic.json");
        return lib;
    }

    private static string Relay(string id, int branch, int from, int to, string end, double pickup, double tms, string curve = "iec-si", double ct = 1.0, string extra = "")
        => "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"branch\": { \"index\": " + branch + ", \"from\": " + from + ", \"to\": " + to + ", \"end\": \"" + end + "\" }, "
         + "\"ct\": { \"primaryA\": " + N(ct) + ", \"secondaryA\": 1 }, "
         + "\"shots\": [ { \"curve\": \"" + curve + "\", \"pickupA\": " + N(pickup) + ", \"timeDial\": " + N(tms) + " } ] " + extra + " }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices)
    {
        string doc = $$"""{ "manifestVersion": "gridsim-devices/1", "devices": [ {{string.Join(",", devices)}} ] }""";
        return DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(doc)), m, Library());
    }

    // ---- curves ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Standard_curves_match_the_closed_forms_and_the_incumbent_bit_for_bit()
    {
        GridModel m = Radial();
        BoundDeviceSet b = Bind(m, Relay("SI", 0, 1, 2, "from", 1.0, 0.5), Relay("VI", 0, 1, 2, "from", 1.0, 0.5, "ieee-vi"));
        // IEC SI at I/Is = 10: t = TMS * 0.14 / (10^0.02 - 1) (equation evaluated here with Math.Pow).
        double si = 0.5 * 0.14 / (Math.Pow(10.0, 0.02) - 1.0);
        Assert.Equal(si, DeviceTiming.Seconds(b.Devices[0], 0, TimingRole.Clearing, 10.0, b.Library, out _), 10);
        Assert.Equal(0.5 * 2.97060, DeviceTiming.Seconds(b.Devices[0], 0, TimingRole.Clearing, 10.0, b.Library, out _), 4);
        // IEEE C37.112 very inverse at 10: TD * (19.61 / (10^2 - 1) + 0.491).
        double vi = 0.5 * (19.61 / 99.0 + 0.491);
        Assert.Equal(vi, DeviceTiming.Seconds(b.Devices[1], 0, TimingRole.Clearing, 10.0, b.Library, out _), 10);
        Assert.Equal(0.5 * 0.689081, DeviceTiming.Seconds(b.Devices[1], 0, TimingRole.Clearing, 10.0, b.Library, out _), 4);
        // Same bits as the incumbent for the shared curve.
        double inc = OvercurrentCoordination.OperateTimeSeconds(new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 1.0, 0.5), 10.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(inc), BitConverter.DoubleToInt64Bits(DeviceTiming.Seconds(b.Devices[0], 0, TimingRole.Clearing, 10.0, b.Library, out _)));
        // Below pickup: never.
        Assert.True(double.IsInfinity(DeviceTiming.Seconds(b.Devices[0], 0, TimingRole.Clearing, 1.0, b.Library, out _)));
    }

    [Fact]
    public void Tabulated_curve_is_log_log_linear_between_points()
    {
        var src = new CurveSource("synthetic", "t", "t", "n/a", "t", "2026-11-17", null);
        var c = new TabulatedCurve("x", "", CurveKind.TotalClear, src, new[] { (10.0, 100.0), (100.0, 1.0) });
        Assert.Equal(10.0, c.TimeAt(Math.Sqrt(1000.0), out bool above), 9);   // closed form: geometric mean of the currents
        Assert.False(above);
        Assert.Equal(100.0, c.TimeAt(10.0, out _), 12);
        Assert.Equal(1.0, c.TimeAt(100.0, out _), 12);
        Assert.True(double.IsInfinity(c.TimeAt(9.0, out _)));
        Assert.Equal(1.0, c.TimeAt(500.0, out above), 12);
        Assert.True(above);   // held at the last point and flagged, never extrapolated silently
        // A three-point curve: the segment is chosen by the current.
        var c3 = new TabulatedCurve("y", "", CurveKind.Operate, src, new[] { (10.0, 100.0), (100.0, 1.0), (1000.0, 0.1) });
        double expect = Math.Exp(Math.Log(1.0) + (Math.Log(300.0) - Math.Log(100.0)) / (Math.Log(1000.0) - Math.Log(100.0)) * (Math.Log(0.1) - Math.Log(1.0)));
        Assert.Equal(expect, c3.TimeAt(300.0, out _), 9);
    }

    [Fact]
    public void A_curve_file_without_provenance_is_refused_and_constants_must_match_the_code()
    {
        var lib = new CurveLibrary();
        string noSource = """{ "format": "gridsim-protection-curve/1", "curves": [ { "id": "a", "kind": "operate", "points": [[1,1],[2,0.5]] } ] }""";
        CurveDataException ex = Assert.Throws<CurveDataException>(() => lib.Load(Encoding.UTF8.GetBytes(noSource), "nosrc.json"));
        Assert.Contains("no provenance", ex.Message, StringComparison.Ordinal);
        Assert.Empty(lib.Tabulated);
        string emptyTitle = SyntheticFuses.Replace("GridSim synthetic test fixture - NOT vendor data", " ");
        Assert.Throws<CurveDataException>(() => new CurveLibrary().Load(Encoding.UTF8.GetBytes(emptyTitle), "e.json"));
        string noUrl = SyntheticFuses.Replace("\"n/a (synthetic)\"", "\"\"");
        Assert.Throws<CurveDataException>(() => new CurveLibrary().Load(Encoding.UTF8.GetBytes(noUrl), "u.json"));
        string manufacturerNoHttp = SyntheticFuses.Replace("\"synthetic\"", "\"manufacturer\"");
        Assert.Throws<CurveDataException>(() => new CurveLibrary().Load(Encoding.UTF8.GetBytes(manufacturerNoHttp), "m.json"));
        string wrongK = """
        { "format": "gridsim-protection-curve/1",
          "source": { "kind": "standard", "title": "t", "publisher": "p", "url": "https://example.org", "locator": "l", "retrieved": "2026-11-17" },
          "families": [ { "id": "iec-si", "k": 0.15, "alpha": 0.02 } ] }
        """;
        Assert.Throws<CurveDataException>(() => new CurveLibrary().Load(Encoding.UTF8.GetBytes(wrongK), "k.json"));
    }

    [Fact]
    public void Every_shipped_curve_file_loads_with_provenance_and_matches_the_equations()
    {
        string dir = Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data", "protection-curves");
        string[] files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);
        CurveLibrary lib = CurveLibrary.LoadDirectory(dir);
        Assert.Equal(files.Length, lib.Sources.Count);
        foreach (CurveSource s in lib.Sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title)); Assert.False(string.IsNullOrWhiteSpace(s.Publisher));
            Assert.False(string.IsNullOrWhiteSpace(s.Url)); Assert.False(string.IsNullOrWhiteSpace(s.Locator)); Assert.False(string.IsNullOrWhiteSpace(s.Retrieved));
        }
    }

    // ---- fault along a line ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Fault_along_a_line_follows_the_series_impedance_closed_form(double d)
    {
        // Source j0.1 behind bus 1, line j0.2 (here branch 1 after the j0.1 branch 0 is excluded: use a two-bus model).
        var m = new GridModel
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.1) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.2, B: 0.04, RateAMva: 150, Name: "L") },
        };
        FaultNetwork net = FaultAlongLine.Build(m, 0, d);
        double ik = Iec60909.ThreePhase(net.Model, net.FaultBusId).IkppPu;
        Assert.Equal(C / (0.1 + 0.2 * d), ik, 9);
        if (d is 0.0 or 1.0)
        {
            Assert.False(net.Inserted);
            Assert.Equal(d == 0.0 ? 1 : 2, net.FaultBusId);
            Assert.Equal(m.Branches.Count, net.Model.Branches.Count);
        }
        else
        {
            // The inserted bus's identity map: new ID above every existing one; segment A keeps index 0, segment B is appended.
            Assert.True(net.Inserted);
            Assert.Equal(3, net.FaultBusId);
            Assert.Equal(1, net.SecondSegmentIndex);
            Branch a = net.Model.Branches[0], b = net.Model.Branches[1];
            Assert.Equal((1, 3, 3, 2), (a.From, a.To, b.From, b.To));   // segment B runs from the new bus to the original To
            Assert.Equal(0.2 * d, a.X, 12); Assert.Equal(0.2 * (1 - d), b.X, 12);
            Assert.Equal(0.04 * d, a.B, 12); Assert.Equal(0.04 * (1 - d), b.B, 12);
            Assert.Equal(150.0, a.RateAMva); Assert.Equal(150.0, b.RateAMva);
            Assert.Equal("L/a", a.Name); Assert.Equal("L/b", b.Name);
            Assert.Equal(0, FaultNetwork.FromSegment(0)); Assert.Equal(1, net.ToSegment(0));
            // The segment next to the source carries the whole fault: its from-end current in kA.
            BranchCurrentTable t = FaultAlongLine.Currents(net.Model, net.FaultBusId);
            Assert.Equal(ik * BaseMva / (Math.Sqrt(3.0) * Kv), t.FromEndKa[0], 9);
        }
        // The original model is untouched.
        Assert.Single(m.Branches); Assert.Equal(2, m.Buses.Count);
    }

    [Fact]
    public void Fault_along_a_transformer_is_refused()
    {
        GridModel m = Radial();
        var bm = new GridModel { BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators, Branches = new[] { new Branch(1, 2, 0, 0.1, IsTransformer: true), m.Branches[1] } };
        Assert.Throws<InvalidOperationException>(() => FaultAlongLine.Build(bm, 0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => FaultAlongLine.Build(m, 0, 1.5));
    }

    // ---- two-device radial grade -------------------------------------------------------------------------------------

    [Fact]
    public void Radial_trace_reproduces_the_hand_graded_feeder()
    {
        GridModel m = Radial();
        // A (downstream) on branch 1 at bus 2; B (upstream) on branch 0 at bus 1. Hand: Zk = 0.1 + 0.1 + 0.2 = 0.4, I = 1.1 / 0.4 pu.
        BoundDeviceSet b = Bind(m, Relay("A", 1, 2, 3, "from", 400.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.3));
        TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AtBus(3), ShortCircuitObjective.Maximum, 0.4);
        double amps = C / 0.4 * IBaseA;
        double ta = 0.1 * 0.14 / (Math.Pow(amps / 400.0, 0.02) - 1.0);
        double tb = 0.3 * 0.14 / (Math.Pow(amps / 800.0, 0.02) - 1.0);
        Assert.Equal(amps, t.Rows[0].PrimaryCurrentA, 6);
        Assert.Equal(amps, t.Rows[1].PrimaryCurrentA, 6);
        Assert.Equal(ta, t.Rows[0].ClearingTimeS, 9);
        Assert.Equal(tb, t.Rows[1].ClearingTimeS, 9);
        Assert.Equal(new[] { "A", "B" }, t.Sequence);
        Assert.Equal("A", t.PrimaryId); Assert.Equal("B", t.BackupId);
        PairRow p = Assert.Single(t.Pairs);
        Assert.Equal(("A", "B"), (p.PrimaryId, p.BackupId));
        Assert.Equal(tb - ta, p.MarginS, 9);
        Assert.True(p.Coordinated);
        // The incumbent is the oracle for the pair verdict, bit for bit (CT ratio 1, so the same current feeds both).
        GradingResult g = OvercurrentCoordination.GradeChain(new[]
        {
            new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 400.0, 0.1), new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 800.0, 0.3),
        }, t.Rows[0].PrimaryCurrentA, 0.4)[0];
        Assert.Equal(BitConverter.DoubleToInt64Bits(g.MarginSeconds), BitConverter.DoubleToInt64Bits(p.MarginS));
        Assert.Equal(g.Coordinated, p.Coordinated);

        // A too-fast backup fails the interval: B at TMS 0.05 gives about 0.23 s here, below the 0.4 s interval.
        BoundDeviceSet tight = Bind(m, Relay("A", 1, 2, 3, "from", 400.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.05));
        PairRow q = Assert.Single(ShortCircuitTrace.Run(tight, FaultSpec.AtBus(3), ShortCircuitObjective.Maximum, 0.4).Pairs);
        Assert.False(q.Coordinated);
        Assert.True(q.MarginS > 0.0 && q.MarginS < 0.4);
    }

    [Fact]
    public void Fault_in_the_middle_leaves_the_downstream_device_without_current_and_ct_ratio_is_applied()
    {
        GridModel m = Radial();
        // CT 400/1 with a pickup of 1 A secondary is the same 400 A primary pickup.
        BoundDeviceSet b = Bind(m, Relay("A", 1, 2, 3, "from", 1.0, 0.1, ct: 400.0), Relay("B", 0, 1, 2, "from", 800.0, 0.3));
        TraceResult t2 = ShortCircuitTrace.Run(b, FaultSpec.AtBus(2), ShortCircuitObjective.Maximum, 0.4);
        Assert.Equal(0.0, t2.Rows[0].PrimaryCurrentA, 9);                 // nothing beyond bus 2: A sees no current
        Assert.True(double.IsInfinity(t2.Rows[0].ClearingTimeS));
        Assert.Equal(C / 0.2 * IBaseA, t2.Rows[1].PrimaryCurrentA, 6);   // B sees the fault (Zk = 0.2)
        Assert.Equal("B", t2.PrimaryId); Assert.Null(t2.BackupId);
        Assert.Empty(t2.Pairs);                                           // A is not on the supply path of a fault at bus 2

        TraceResult t3 = ShortCircuitTrace.Run(b, FaultSpec.AtBus(3), ShortCircuitObjective.Maximum, 0.4);
        double amps = C / 0.4 * IBaseA;
        Assert.Equal(amps / 400.0, t3.Rows[0].SecondaryCurrentA, 9);
        Assert.Equal(0.1 * 0.14 / (Math.Pow(amps / 400.0, 0.02) - 1.0), t3.Rows[0].ClearingTimeS, 9);
    }

    [Fact]
    public void Fault_along_the_protected_line_uses_the_inserted_bus_and_keeps_the_sequence()
    {
        GridModel m = Radial();
        BoundDeviceSet b = Bind(m, Relay("A", 1, 2, 3, "from", 400.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.3));
        TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AlongBranch(1, 0.5), ShortCircuitObjective.Maximum, 0.4);
        double amps = C / (0.1 + 0.1 + 0.2 * 0.5) * IBaseA;   // series impedances, hand sum
        Assert.Equal(4, t.FaultBusId);
        Assert.Equal(amps, t.Rows[0].PrimaryCurrentA, 6);
        Assert.Equal(amps, t.Rows[1].PrimaryCurrentA, 6);
        Assert.Equal(new[] { "A", "B" }, t.Sequence);
        Assert.Single(t.Pairs);
        // A fault along the upstream line: A (downstream, beyond the fault) is behind and carries nothing.
        TraceResult u = ShortCircuitTrace.Run(b, FaultSpec.AlongBranch(0, 0.5), ShortCircuitObjective.Maximum, 0.4);
        Assert.Equal(0.0, u.Rows[0].PrimaryCurrentA, 9);
        Assert.Equal(C / (0.1 + 0.05) * IBaseA, u.Rows[1].PrimaryCurrentA, 6);
        // Along a line that is not there: a faulted island with no source is reported not evaluated, not a number.
    }

    [Fact]
    public void Minimum_objective_scales_the_currents_by_the_voltage_factor_ratio()
    {
        GridModel m = Radial();
        BoundDeviceSet b = Bind(m, Relay("A", 1, 2, 3, "from", 400.0, 0.1));
        TraceResult mn = ShortCircuitTrace.Run(b, FaultSpec.AtBus(3), ShortCircuitObjective.Minimum, 0.4);
        Assert.Equal(1.00 / 0.4 * IBaseA, mn.Rows[0].PrimaryCurrentA, 6);   // c = 1.00 for the minimum objective at HV
    }

    // ---- fuses, reclosers, instantaneous -----------------------------------------------------------------------------

    [Fact]
    public void A_fuse_is_timed_at_total_clear_as_primary_and_minimum_melt_as_backup()
    {
        GridModel m = Radial();
        string fuse = """{ "id": "F", "type": "fuse", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" }, "fuse": { "meltCurve": "T-melt", "clearCurve": "T-clear", "ratingA": 80 } }""";
        BoundDeviceSet b = Bind(m, fuse, Relay("B", 0, 1, 2, "from", 800.0, 0.3));
        TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AtBus(3), ShortCircuitObjective.Maximum, 0.4);
        double amps = C / 0.4 * IBaseA;
        double Interp(double i1, double t1, double i2, double t2) =>
            Math.Exp(Math.Log(t1) + (Math.Log(amps) - Math.Log(i1)) / (Math.Log(i2) - Math.Log(i1)) * (Math.Log(t2) - Math.Log(t1)));
        double clear = Interp(100, 20.0, 1000, 0.05);
        double melt = Interp(100, 10.0, 1000, 0.01);
        Assert.True(amps > 1000.0 && amps < 1300.0);                            // above the last point: held and flagged
        Assert.True(t.Rows[0].AboveCurveRange);
        Assert.Equal(0.05, t.Rows[0].ClearingTimeS, 12);
        Assert.True(clear < 0.05);                                               // (the log-log value, were it extrapolated, would be below the held 0.05)
        Assert.Equal(0.01, t.Rows[0].EarliestTimeS, 12);
        Assert.True(melt < 0.01);
        Assert.Equal(ProtectionConfidence.Generic, t.Rows[0].Confidence);        // synthetic data makes no vendor claim
        // Inside the range the interpolation is the hand log-log value: a 400 A fault level.
        var lib = Library();
        Assert.True(lib.TryGetTabulated("T-clear", out TabulatedCurve c));
        double at300 = Math.Exp(Math.Log(20.0) + (Math.Log(300.0) - Math.Log(100.0)) / (Math.Log(1000.0) - Math.Log(100.0)) * (Math.Log(0.05) - Math.Log(20.0)));
        Assert.Equal(at300, c.TimeAt(300.0, out _), 9);
    }

    [Fact]
    public void A_recloser_grades_each_shot_and_an_instantaneous_element_overreach_is_reported()
    {
        GridModel m = Radial();
        string rec = """
        { "id": "R", "type": "recloser", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" },
          "shots": [ { "curve": "iec-vi", "pickupA": 300, "timeDial": 0.05 }, { "curve": "iec-vi", "pickupA": 300, "timeDial": 0.3 } ],
          "recloseIntervalsS": [1.0] }
        """;
        BoundDeviceSet b = Bind(m, rec, Relay("B", 0, 1, 2, "from", 800.0, 0.4));
        TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AtBus(3), ShortCircuitObjective.Maximum, 0.4);
        double amps = C / 0.4 * IBaseA;
        Assert.Equal(2, t.Pairs.Count);
        Assert.Equal(new[] { 0, 1 }, t.Pairs.Select(p => p.Shot).ToArray());
        double s0 = 0.05 * 13.5 / (amps / 300.0 - 1.0), s1 = 0.3 * 13.5 / (amps / 300.0 - 1.0);
        Assert.Equal(s0, t.Pairs[0].PrimaryTimeS, 9);
        Assert.Equal(s1, t.Pairs[1].PrimaryTimeS, 9);
        Assert.True(t.Pairs[0].MarginS > t.Pairs[1].MarginS);   // the slower delayed curve is the harder pair

        // Instantaneous reach: B on branch 0 at bus 1 with an instantaneous pickup below the current for a fault at bus 2.
        double faultAtFar = C / 0.2 * IBaseA;
        string inst = ", \"instantaneous\": { \"pickupA\": " + N(faultAtFar * 0.9) + ", \"delayS\": 0.0 }";
        BoundDeviceSet bi = Bind(m, Relay("B", 0, 1, 2, "from", 800.0, 0.3, extra: inst));
        ProtectionAuditResult r = ProtectionAudit.Run(bi);
        Assert.True(r.Devices[0].InstantaneousOverreaches);
        Assert.Equal(1.0 / 0.9, r.Devices[0].InstantaneousReachRatio, 9);
    }

    // ---- validation --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_device_set_is_validated_against_the_case()
    {
        GridModel m = Radial();
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 1, 3, 2, "from", 400, 0.1)));                     // ends differ from the case
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 7, 2, 3, "from", 400, 0.1)));                     // index outside
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 1, 2, 3, "from", 400, 0.1), Relay("A", 0, 1, 2, "from", 400, 0.1)));   // duplicate ID
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 1, 2, 3, "from", 400, 0.1, curve: "nonsense")));
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 1, 2, 3, "from", 400, 0.0)));                     // time dial must be > 0
        Assert.Throws<DeviceSetException>(() => Bind(m, Relay("A", 1, 2, 3, "from", 400, 0.1, curve: "T-melt")));    // a fuse curve on a relay
        Assert.Throws<DeviceSetException>(() => Bind(m, """{ "id": "F", "type": "fuse", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" } }"""));
        Assert.Throws<DeviceSetException>(() => Bind(m, """{ "id": "R", "type": "recloser", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" }, "shots": [ { "curve": "iec-si", "pickupA": 1 }, { "curve": "iec-si", "pickupA": 1 } ] }"""));
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes("""{ "manifestVersion": "gridsim-devices/1", "devices": [], "extra": 1 }""")));
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes("""{ "manifestVersion": "gridsim-devices/2", "devices": [] }""")));
    }

    [Fact]
    public void A_device_on_an_out_of_service_branch_and_unsupported_functions_are_not_evaluated_never_passed()
    {
        GridModel m = Radial();
        var open = new GridModel { BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators, Branches = new[] { m.Branches[0], m.Branches[1] with { InService = false } } };
        string dist = """{ "id": "Z", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "shots": [ { "curve": "iec-si", "pickupA": 1 } ] }""";
        BoundDeviceSet b = Bind(open, Relay("A", 1, 2, 3, "from", 400.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.3), dist);
        TraceResult t = ShortCircuitTrace.Run(b, FaultSpec.AtBus(2), ShortCircuitObjective.Maximum, 0.4);
        Assert.False(t.Rows[0].Evaluated);
        Assert.Contains("out of service", t.Rows[0].Reason, StringComparison.Ordinal);
        Assert.False(t.Rows[2].Evaluated);
        Assert.Equal(ProtectionConfidence.NotCheckable, t.Rows[2].Confidence);
        ProtectionAuditResult r = ProtectionAudit.Run(b, new[] { FaultSpec.AtBus(2) });
        Assert.Contains(r.NotEvaluated, n => n.Subject == "A");
        Assert.Contains(r.NotEvaluated, n => n.Subject == "Z");
    }

    // ---- audit -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Audit_reports_margins_sensitivity_breaker_and_is_repeatable()
    {
        GridModel m = Radial();
        string brk = """{ "id": "CB", "type": "breaker", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "rating": { "ratedVoltageKv": 145, "makingPeakKa": 100, "breakingSymKa": 40, "shortTimeWithstandKa": 40, "shortTimeSeconds": 1 } }""";
        BoundDeviceSet b = Bind(m, Relay("A", 1, 2, 3, "from", 400.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.3), brk);
        ProtectionAuditResult r = ProtectionAudit.Run(b);
        Assert.Equal(AuditVerdict.Coordinated, r.Verdict);
        AuditPair p = Assert.Single(r.Pairs);
        // The worst margin over the fault list is at the smallest in-zone current seen by both: the bus 3 fault (Zk 0.4) is the only
        // one in A's zone, so it is the worst case.
        double amps = C / 0.4 * IBaseA;
        double margin = 0.3 * 0.14 / (Math.Pow(amps / 800.0, 0.02) - 1.0) - 0.1 * 0.14 / (Math.Pow(amps / 400.0, 0.02) - 1.0);
        Assert.Equal(margin, p.WorstMarginS, 9);
        Assert.Equal("bus 3", p.WorstFault);
        // Sensitivity at the minimum objective (c = 1.00): A's smallest in-zone fault is bus 3.
        AuditDevice a = r.Devices.Single(d => d.DeviceId == "A");
        Assert.Equal(1.0 / 0.4 * IBaseA, a.MinFaultCurrentA, 6);
        Assert.Equal(1.0 / 0.4 * IBaseA / 400.0, a.SensitivityRatio, 9);
        Assert.True(a.Operates);
        // The breaker margin is the incumbent CheckBreaker at the bus 1 duty (read only).
        Iec60909.Duty duty = Iec60909.ThreePhase(m, 1);
        Iec60909.BreakerAdequacy adq = Iec60909.CheckBreaker(duty, new Iec60909.BreakerRating(145, 100, 40, 40, 1), 132.0);
        AuditBreaker br = Assert.Single(r.Breakers);
        Assert.Equal(adq.BreakingMarginKa, br.BreakingMarginKa, 12);
        Assert.Equal(adq.Adequate, br.Adequate);
        // Repeat-run bit identity of the whole result.
        ProtectionAuditResult r2 = ProtectionAudit.Run(b);
        Assert.Equal(Canon(r), Canon(r2));
        // A pickup above the minimum fault is a failure: A would not operate for its smallest fault.
        BoundDeviceSet deaf = Bind(m, Relay("A", 1, 2, 3, "from", 5000.0, 0.1), Relay("B", 0, 1, 2, "from", 800.0, 0.3));
        Assert.False(ProtectionAudit.Run(deaf).Devices[0].Operates);
    }

    private static string Canon(ProtectionAuditResult r)
    {
        var sb = new StringBuilder();
        foreach (AuditPair p in r.Pairs) sb.Append(p.PrimaryId).Append(p.BackupId).Append(p.Shot).Append(BitConverter.DoubleToInt64Bits(p.WorstMarginS)).Append(p.WorstFault);
        foreach (AuditDevice d in r.Devices) sb.Append(d.DeviceId).Append(BitConverter.DoubleToInt64Bits(d.MinFaultCurrentA)).Append(BitConverter.DoubleToInt64Bits(d.SensitivityRatio));
        foreach (TraceResult t in r.Faults) foreach (DeviceTraceRow x in t.Rows) sb.Append(BitConverter.DoubleToInt64Bits(x.ClearingTimeS));
        return sb.ToString();
    }
}
