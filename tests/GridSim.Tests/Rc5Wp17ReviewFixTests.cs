using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 review fixes to WP17 (2.1.115): switch-onto-fault only for the relay on the line that was closed, the DCB carrier
/// stop, reverse zones as timed back-up, and the swing trace read straight from a dynamics-study result. Common data as
/// <see cref="Rc5Wp17DistanceSchemeTests"/>: 100 MVA, 132 kV, CT 400/1, VT 132000/110, 58.08 secondary ohm per pu; no
/// loads and no line charging, so the prefault state is exactly 1 at 0 degrees and a radial feeder's relays see the sum of
/// the line impedances up to the fault.
/// </summary>
public sealed class Rc5Wp17ReviewFixTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private const string CtVt = "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";

    private static string Zone(double reach, double angle, double t, string extra = "") =>
        "{ \"reachOhms\": " + N(reach) + ", \"angleDeg\": " + N(angle) + ", \"timeS\": " + N(t) + extra + " }";

    private static string Relay(string id, int branch, int from, int to, string[] zones, string extra) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": " + branch + ", \"from\": " + from + ", \"to\": " + to
        + ", \"end\": \"from\" }, " + CtVt + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"phase\", \"zones\": [ " + string.Join(", ", zones) + " ]" + extra + " } }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) => DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(
        "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }")), m, new CurveLibrary());

    private static IReadOnlyList<PlacedRow> Rows(BoundDeviceSet b, FaultSpec f)
    {
        FaultPhasors ph = FaultPhasors.Build(b.Model, f, FaultType.ThreePhase, 0.0);
        Assert.Null(ph.NotEvaluated);
        return PlacedElementEvaluator.Evaluate(b, ph);
    }

    /// <summary>A source (X'' = 0.2) at bus 1, then lines 1-2 and 2-3, each Z1 = 0.04 + j0.4, no load.</summary>
    private static GridModel Feeder() => new()
    {
        Name = "wp17fix",
        BaseMva = Mva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(1, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2) },
        Branches = new[] { new Branch(1, 2, R: 0.04, X: 0.4), new Branch(2, 3, R: 0.04, X: 0.4) },
    };

    /// <summary>
    /// Line 2-3 is closed onto a three-phase fault at 90 % of its length. Relay T on that line sees 0.9 ZL: zone 2
    /// (zone 1 80 %, zone 2 120 %), so switch-onto-fault trips it at 0 s. Relay S on the adjacent line 1-2 sees ZL + 0.9 ZL
    /// = 1.9 ZL (the same current flows through both lines): inside its zone 2 (200 %, 0.4 s). S also carries a Z2
    /// switch-onto-fault setting, but its own line was not just closed, so it keeps its 0.4 s. Before 2.1.115 it tripped at
    /// 0 s too, which is a loss of selectivity. A bus fault marked as an energisation arms no relay (no line was closed).
    /// </summary>
    [Fact]
    public void Switch_onto_fault_acts_only_for_the_relay_on_the_line_that_was_closed()
    {
        Complex zl = new Complex(0.04, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.04) * 180.0 / Math.PI;
        const string sotf = ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"Z2\" }";
        string s = Relay("S", 0, 1, 2, new[] { Zone(0.8 * zl.Magnitude, th, 0.0), Zone(2.0 * zl.Magnitude, th, 0.4) }, sotf);
        string t = Relay("T", 1, 2, 3, new[] { Zone(0.8 * zl.Magnitude, th, 0.0), Zone(1.2 * zl.Magnitude, th, 0.4) }, sotf);
        BoundDeviceSet b = Bind(Feeder(), s, t);

        IReadOnlyList<PlacedRow> closed = Rows(b, FaultSpec.AlongBranch(1, 0.9) with { Energisation = true });
        Assert.Equal(2, closed[0].Zone);
        Assert.Equal(0.4, closed[0].TimeS);
        Assert.False(closed[0].SwitchOntoFault);
        Assert.Null(closed[0].Reason);
        Assert.Equal(2, closed[1].Zone);
        Assert.Equal(0.0, closed[1].TimeS);
        Assert.True(closed[1].SwitchOntoFault);
        Assert.Equal("switch-onto-fault", closed[1].Reason);
        // The zones and times above carry the test. The apparent impedances (|1.9 ZL| at S, |0.9 ZL| at T) are not asserted
        // to a tolerance: they come through the fault network's LU solve, and no bound was derived for that path.

        // Not an energisation: both relays on their zone timers.
        IReadOnlyList<PlacedRow> plain = Rows(b, FaultSpec.AlongBranch(1, 0.9));
        Assert.Equal(0.4, plain[0].TimeS);
        Assert.Equal(0.4, plain[1].TimeS);

        // A bus fault has no closed line: T sees bus 3 at 1.0 ZL, zone 2, and keeps its 0.4 s.
        IReadOnlyList<PlacedRow> bus = Rows(b, FaultSpec.AtBus(3) with { Energisation = true });
        Assert.Equal(0.4, bus[1].TimeS);
        Assert.False(bus[1].SwitchOntoFault);
        Assert.False(bus[0].SwitchOntoFault);
    }

    /// <summary>
    /// The DCB carrier stop (the textbook scheme: Ziegler, Numerical Distance Protection; the Alstom NPAG). The remote
    /// end sends the block when its reverse zone picks up and its overreaching zone does not. Local end in zone 2 only
    /// (plain time 0.4 s), coordination 0.02 s:
    /// <list type="bullet">
    /// <item>remote reverse only: blocked, the local end keeps 0.4 s;</item>
    /// <item>remote reverse and forward together (an offset or quadrilateral reverse zone reaching a close-in internal
    /// fault, or a healthy-phase loop): the forward pickup stops the carrier, the local end trips at 0 + 0.02 s;</item>
    /// <item>remote forward only: no block, 0.02 s.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Dcb_forward_pickup_at_the_remote_end_stops_the_block()
    {
        const double inf = double.PositiveInfinity, co = 0.02;
        SchemeEnd local = new(inf, true, false, 0.4);
        Assert.Equal(0.4, SchemeLogic.TripS(SchemeType.Dcb, 0.0, co, local, new SchemeEnd(inf, false, true, 1.0)));
        Assert.Equal(0.02, SchemeLogic.TripS(SchemeType.Dcb, 0.0, co, local, new SchemeEnd(inf, true, true, 0.4)));
        Assert.Equal(0.02, SchemeLogic.TripS(SchemeType.Dcb, 0.0, co, local, new SchemeEnd(inf, true, false, 0.4)));
        Assert.Equal(0.02, SchemeLogic.TripS(SchemeType.Dcb, 0.0, co, local, new SchemeEnd(0.0, true, true, 0.0)));
    }

    /// <summary>Earthed sources (X'' = X2 = 0.2, X0 = 0.05) at buses 1 and 3; the protected line 1-2 (Z1 = 0.02 + j0.4,
    /// Z0 = 0.06 + j1.2) and line 2-3 behind its bus-2 end (Z1 = 0.0025 + j0.05, Z0 = 0.0075 + j0.15, the same Z0/Z1
    /// ratio, so one k0 fits both lines).</summary>
    private static GridModel TwoSource()
    {
        static Generator G(int bus) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2)
        {
            ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05),
        };
        return new GridModel
        {
            Name = "wp17dcb",
            BaseMva = Mva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pv, BaseKv: Kv) },
            Generators = new[] { G(1), G(3) },
            Branches = new[] { new Branch(1, 2, R: 0.02, X: 0.4, R0: 0.06, X0: 1.2), new Branch(2, 3, R: 0.0025, X: 0.05, R0: 0.0075, X0: 0.15) },
        };
    }

    /// <summary>
    /// The DCB block end to end (2.1.118). A at bus 1 and B at bus 2 on line 1-2, phase and earth loops, zone 1 at 80 %
    /// (0 s), zone 2 at 120 % (0.4 s) and a reverse zone at 50 % (1.0 s), DCB with 0.02 s coordination. External faults
    /// behind B (at bus 3, 0.125 ZL behind it, and half way along line 2-3), every fault type: A sees the fault in zone 2
    /// only, so without a block it would trip at 0.02 s. B sees it in its reverse zone. The carrier stop must not fire, so
    /// no loop at B, faulted or healthy, may land in B's forward zone 1 or 2, and A keeps its zone-2 time of 0.4 s.
    /// </summary>
    [Fact]
    public void Dcb_external_fault_behind_the_remote_end_is_blocked_for_every_fault_type()
    {
        Complex zl = new Complex(0.02, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.02) * 180.0 / Math.PI;
        string Zones() => Zone(0.8 * zl.Magnitude, th, 0.0) + ", " + Zone(1.2 * zl.Magnitude, th, 0.4) + ", " + Zone(0.5 * zl.Magnitude, th, 1.0, ", \"reverse\": true");
        string End(string id, string end, string remote) =>
            "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"" + end + "\" }, " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"both\", \"zones\": [ " + Zones() + " ], \"scheme\": { \"type\": \"DCB\", \"remote\": \""
            + remote + "\", \"coordinationS\": 0.02 } } }";
        BoundDeviceSet b = Bind(TwoSource(), End("A", "from", "B"), End("B", "to", "A"));
        foreach (FaultSpec fs in new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
            foreach (FaultType t in new[] { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround })
            {
                FaultPhasors ph = FaultPhasors.Build(b.Model, fs, t);
                Assert.Null(ph.NotEvaluated);
                IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
                string at = $"{fs.Label} {FaultPhasors.TypeName(t)}";
                Assert.True(rows[0].Operates && rows[0].Zone == 2 && rows[0].TimeS == 0.4, $"{at}: A zone {rows[0].Zone} at {rows[0].TimeS}");
                Assert.True((rows[1].ZonesPicked & 0b100) != 0, $"{at}: B's reverse zone does not pick up ({rows[1].ZonesPicked})");
                Assert.True((rows[1].ZonesPicked & 0b011) == 0, $"{at}: a loop at B picks up forward ({rows[1].ZonesPicked}), which stops the carrier");
                IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                Assert.True(s[0]!.Evaluated, at);
                Assert.Equal(0.4, s[0]!.AidedTripS);
                Assert.False(s[0]!.Aided, at);
            }
    }

    /// <summary>
    /// The DCB block end to end with real prefault load (2.1.120). The unloaded case above has P = 0 at both sources, so a
    /// healthy-phase loop at B sees an almost infinite impedance and cannot show the risk of B's forward pickup from load.
    /// Here 150 MW crosses line 1-2 before the fault (1.5 pu, a 42 degree angle across the 0.45 pu between the sources),
    /// in each direction: a 150 MW load at bus 3 fed from bus 1 (export 1 to 3), and a 150 MW unit at bus 3 feeding a load
    /// at bus 1 (export 3 to 1, where B sees the load in its forward direction). External faults behind B at bus 3 and half
    /// way along line 2-3, every fault type, phase and earth loops at both ends. B's reverse zone picks up and no loop at B
    /// lands in its forward zone 1 or 2, so the block holds and A is not aided: the carrier stop is right as it is.
    /// </summary>
    [Fact]
    public void Dcb_external_fault_behind_the_remote_end_is_blocked_with_prefault_load_in_either_direction()
    {
        Complex zl = new Complex(0.02, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.02) * 180.0 / Math.PI;
        string Zones() => Zone(0.8 * zl.Magnitude, th, 0.0) + ", " + Zone(1.2 * zl.Magnitude, th, 0.4) + ", " + Zone(0.5 * zl.Magnitude, th, 1.0, ", \"reverse\": true");
        string End(string id, string end, string remote) =>
            "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"" + end + "\" }, " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"both\", \"zones\": [ " + Zones() + " ], \"scheme\": { \"type\": \"DCB\", \"remote\": \""
            + remote + "\", \"coordinationS\": 0.02 } } }";
        GridModel baseCase = TwoSource();
        foreach ((string dir, GridModel m) in new[]
        {
            ("export 1 to 3", baseCase.CopyWith(buses: baseCase.Buses.Select(x => x.Id == 3 ? x with { Pd = 150.0 } : x).ToArray())),
            ("export 3 to 1", baseCase.CopyWith(buses: baseCase.Buses.Select(x => x.Id == 1 ? x with { Pd = 150.0 } : x).ToArray(),
                generators: baseCase.Generators.Select(g => g.Bus == 3 ? g with { Pg = 150.0 } : g).ToArray())),
        })
        {
            // The prefault transfer on line 1-2 really is about 150 MW, in the stated direction (less the line losses: the
            // bus-1 end of line 1-2 carries 144.4 MW when bus 3 exports).
            PowerFlowResult pf = FaultPhasors.DefaultPrefault(m);
            Assert.True(pf.Converged, dir);
            double p12 = pf.LineFlows[0].PFromMw;
            Assert.True(dir == "export 1 to 3" ? p12 > 140.0 : p12 < -140.0, $"{dir}: line 1-2 carries {p12} MW");
            BoundDeviceSet b = Bind(m, End("A", "from", "B"), End("B", "to", "A"));
            foreach (FaultSpec fs in new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
                foreach (FaultType t in new[] { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround })
                {
                    FaultPhasors ph = FaultPhasors.Build(b.Model, fs, t);
                    Assert.Null(ph.NotEvaluated);
                    IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
                    string at = $"{dir}, {fs.Label} {FaultPhasors.TypeName(t)}";
                    Assert.True((rows[1].ZonesPicked & 0b100) != 0, $"{at}: B's reverse zone does not pick up ({rows[1].ZonesPicked})");
                    Assert.True((rows[1].ZonesPicked & 0b011) == 0, $"{at}: a loop at B picks up forward ({rows[1].ZonesPicked}), which stops the carrier");
                    Assert.True((rows[0].ZonesPicked & 0b001) == 0, $"{at}: A's zone 1 picks up ({rows[0].ZonesPicked})");
                    IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                    Assert.True(s[0]!.Evaluated, at);
                    Assert.False(s[0]!.Aided, at);
                    Assert.Equal(rows[0].Operates ? rows[0].TimeS : double.PositiveInfinity, s[0]!.AidedTripS);
                }
        }
    }

    /// <summary>
    /// A reverse zone is a timed back-up for faults behind the relay as well as the DCB block (2.1.115 documents the
    /// choice). On line 1-2 of the feeder a relay at bus 2 looking towards bus 1 (end "to") has a reverse zone of 0.5 ZL at
    /// 1.0 s. A fault 30 % along line 2-3 lies 0.3 ZL behind it (the relay measures -0.3 ZL, the reverse zone tests
    /// +0.3 ZL): inside, so the relay operates in zone 3 at 1.0 s, and that is its time in the evaluation and in grading. A
    /// fault at bus 3 lies 1.0 ZL behind it, outside the 0.5 ZL reverse reach: no operation.
    /// </summary>
    [Fact]
    public void A_reverse_zone_trips_on_its_own_timer()
    {
        Complex zl = new Complex(0.04, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.04) * 180.0 / Math.PI;
        string r = "{ \"id\": \"B\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"to\" }, " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"phase\", \"zones\": [ " + Zone(0.8 * zl.Magnitude, th, 0.0) + ", "
            + Zone(1.2 * zl.Magnitude, th, 0.4) + ", " + Zone(0.5 * zl.Magnitude, th, 1.0, ", \"reverse\": true") + " ] } }";
        BoundDeviceSet b = Bind(Feeder(), r);
        PlacedRow behind = Rows(b, FaultSpec.AlongBranch(1, 0.3))[0];
        Assert.True(behind.Operates);
        Assert.Equal(3, behind.Zone);
        Assert.Equal(1.0, behind.TimeS);
        Assert.Equal(0b100, behind.ZonesPicked);
        PlacedRow far = Rows(b, FaultSpec.AtBus(3))[0];
        Assert.False(far.Operates);
    }

    // ---- the swing trace from a dynamics-study result ------------------------------------------------------------------

    private static string ResultJson(double[] t, IEnumerable<(int Bus, double[] Vm, double[] Va)> buses, bool twoRuns = false, bool dropAngle = false)
    {
        var channels = new List<object>
        {
            new { label = "rotorAngle 2/'1'", kind = "rotorAngle", quantity = "rotor angle relative to the reference machine", unit = "deg", values = t.Select(_ => 0.0).ToArray() },
        };
        foreach ((int bus, double[] vm, double[] va) in buses)
        {
            channels.Add(new { label = $"busVoltage {bus}", kind = "busVoltage", quantity = "voltage magnitude", unit = "pu", values = vm });
            if (!dropAngle) channels.Add(new { label = $"busVoltage {bus}", kind = "busVoltage", quantity = "voltage angle (engine synchronous frame)", unit = "deg", values = va });
        }
        var run = new { stayedInSynchronism = true, samples = t.Length, timeSeconds = t, channels };
        var cases = new List<object> { new { index = 0, name = "cct", run = (object?)null }, new { index = 1, name = "swing", run } };
        if (twoRuns) cases.Add(new { index = 2, name = "again", run });
        return JsonSerializer.Serialize(new { status = "STABLE", cases });
    }

    private static string Csv(double[] t, IEnumerable<(int Bus, double[] Vm, double[] Va)> buses)
    {
        var list = buses.ToList();
        var sb = new StringBuilder("t_s");
        foreach ((int bus, _, _) in list) sb.Append(",vm_").Append(bus).Append(",va_").Append(bus);
        sb.Append('\n');
        for (int k = 0; k < t.Length; k++)
        {
            sb.Append(N(t[k]));
            foreach ((_, double[] vm, double[] va) in list) sb.Append(',').Append(N(vm[k])).Append(',').Append(N(va[k]));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void SameTrace(SwingTrace a, SwingTrace b)
    {
        Assert.Equal(a.TimeS, b.TimeS);
        Assert.Equal(a.Buses, b.Buses);
        foreach (int bus in a.Buses)
        {
            Assert.True(a.TryVoltage(bus, out IReadOnlyList<Complex> va));
            Assert.True(b.TryVoltage(bus, out IReadOnlyList<Complex> vb));
            Assert.Equal(va.Count, vb.Count);
            for (int k = 0; k < va.Count; k++)
                Assert.True(va[k].Real.Equals(vb[k].Real) && va[k].Imaginary.Equals(vb[k].Imaginary), $"bus {bus} sample {k}");
        }
    }

    /// <summary>The dynamics-study channels <c>busVoltage N</c> (pu and deg) read as the CSV columns vm_N and va_N, bit for
    /// bit (both convert degrees with the same expression); a CCT case without a run is skipped; the refusals name the
    /// problem.</summary>
    [Fact]
    public void A_dynamics_study_result_is_read_as_the_swing_trace_bit_for_bit()
    {
        double[] t = Enumerable.Range(0, 301).Select(k => k * 0.005).ToArray();
        var buses = new[]
        {
            (Bus: 4, Vm: t.Select(x => 1.0 - 0.1 * Math.Sin(3 * x)).ToArray(), Va: t.Select(x => 7.0 * Math.Cos(2 * x)).ToArray()),
            (Bus: 5, Vm: t.Select(x => 0.98 + 0.01 * x).ToArray(), Va: t.Select(x => -(360.0 * x + 90.0)).ToArray()),
        };
        SameTrace(SwingTrace.Parse(Csv(t, buses)), SwingTrace.Parse(ResultJson(t, buses)));
        SameTrace(SwingTrace.Parse(Csv(t, buses)), SwingTrace.Parse("\n  " + ResultJson(t, buses)));

        string Refused(string text) => Assert.Throws<SwingTraceException>(() => SwingTrace.Parse(text)).Message;
        Assert.Contains("2 simulated cases (swing, again)", Refused(ResultJson(t, buses, twoRuns: true)), StringComparison.Ordinal);
        Assert.Contains("bus 4 has a voltage magnitude but no angle", Refused(ResultJson(t, buses, dropAngle: true)), StringComparison.Ordinal);
        Assert.Contains("has 3 values for 301 times", Refused(ResultJson(t, new[] { (4, new[] { 1.0, 1.0, 1.0 }, new[] { 0.0, 0.0, 0.0 }) })), StringComparison.Ordinal);
        Assert.Contains("no busVoltage channel", Refused(ResultJson(t, Array.Empty<(int, double[], double[])>())), StringComparison.Ordinal);
        Assert.Contains("no \"cases\" array", Refused("{ \"status\": \"STABLE\" }"), StringComparison.Ordinal);
        Assert.Contains("cannot be read", Refused("{ \"cases\": [ "), StringComparison.Ordinal);
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// End to end: case14 (bus base voltages set to 132 kV so the relay has secondary ohms) through the real
    /// dynamics-study verb with busVoltage channels at buses 2 and 4, then protection-eval --swing on that result file and
    /// on the same samples written as CSV. The two runs print the same text and the same result table.
    /// </summary>
    [Fact]
    public void Cli_protection_eval_swing_reads_the_dynamics_study_result_file()
    {
        string d = TestTemp.Under("gridsim-wp17fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            string raw = Path.Combine(d, "case14.raw"), dyr = Path.Combine(d, "case14.dyr");
            string text = File.ReadAllText(DynamicsStudyTestSupport.Psse("case14.raw"));
            text = Regex.Replace(text, @"^(\d+), '', 0, ", "$1, '', 132, ", RegexOptions.Multiline);
            File.WriteAllText(raw, text);
            File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), dyr);
            var study = new
            {
                schemaVersion = 1,
                raw = new { path = "case14.raw", sha256 = Sha(raw) },
                dyr = new { path = "case14.dyr", sha256 = Sha(dyr) },
                initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
                integration = new { stepSeconds = 0.005, durationSeconds = 2.0 },
                criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
                angleReference = new { bus = 1, id = "1" },
                channels = new object[] { new { kind = "busVoltage", bus = 2 }, new { kind = "busVoltage", bus = 4 } },
                cases = new object[]
                {
                    new
                    {
                        name = "fault-4", kind = "events", events = new object[]
                        {
                            new { kind = "busFault", at = 0.1, bus = 4 },
                            new { kind = "faultClear", at = 0.15, bus = 4 },
                            new { kind = "branchTrip", at = 0.15, branch = new { from = 4, to = 5, circuit = "1" } },
                        },
                    },
                },
                execution = new { maxParallelism = 1 },
            };
            string studyPath = Path.Combine(d, "study.json"), result = Path.Combine(d, "result.json");
            File.WriteAllText(studyPath, JsonSerializer.Serialize(study));
            CliProcess.Result ds = CliProcess.RunBuilt("dynamics-study", studyPath, "--out", result);
            Assert.True(File.Exists(result), ds.ToString());

            // The samples as CSV, from the same file.
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(result)))
            {
                JsonElement run = doc.RootElement.GetProperty("cases")[0].GetProperty("run");
                double[] t = run.GetProperty("timeSeconds").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var series = new Dictionary<(string, string), double[]>();
                foreach (JsonElement ch in run.GetProperty("channels").EnumerateArray())
                    series[(ch.GetProperty("label").GetString()!, ch.GetProperty("unit").GetString()!)] = ch.GetProperty("values").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var buses = new[] { 2, 4 }.Select(b => (b, series[($"busVoltage {b}", "pu")], series[($"busVoltage {b}", "deg")])).ToList();
                File.WriteAllText(Path.Combine(d, "swing.csv"), Csv(t, buses));
                SameTrace(SwingTrace.Parse(File.ReadAllText(Path.Combine(d, "swing.csv"))), SwingTrace.Parse(File.ReadAllText(result)));
            }

            GridModel m = DynamicsStudyTestSupport.RawText(text, "case14").Model;
            int bi = -1;
            for (int k = 0; k < m.Branches.Count; k++) if (m.Branches[k].From == 2 && m.Branches[k].To == 4) { bi = k; break; }
            Assert.True(bi >= 0);
            string dev = Path.Combine(d, "dev.json");
            File.WriteAllText(dev, "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + Relay("ZA", bi, 2, 4,
                new[] { Zone(20.0, 75.0, 0.0), Zone(40.0, 75.0, 0.4) },
                ", \"powerSwing\": { \"outerR\": 60, \"innerR\": 40, \"outerX\": 80, \"innerX\": 60, \"deltaTS\": 0.03, \"blockZones\": [1, 2] }") + " ] }");
            string jsonOut = Path.Combine(d, "a.json"), csvOut = Path.Combine(d, "b.json");
            CliProcess.Result fromJson = CliProcess.RunBuilt("protection-eval", raw, "--devices", dev, "--swing", result, "--json", jsonOut);
            CliProcess.Result fromCsv = CliProcess.RunBuilt("protection-eval", raw, "--devices", dev, "--swing", Path.Combine(d, "swing.csv"), "--json", csvOut);
            Assert.True(fromJson.ExitCode == 0, fromJson.ToString());
            Assert.True(fromCsv.ExitCode == 0, fromCsv.ToString());
            Assert.Equal(fromCsv.StdOut, fromJson.StdOut);
            Assert.Contains("ZA", fromJson.StdOut, StringComparison.Ordinal);
            using JsonDocument a = JsonDocument.Parse(File.ReadAllBytes(jsonOut)), b = JsonDocument.Parse(File.ReadAllBytes(csvOut));
            Assert.Equal(b.RootElement.GetProperty("tables").GetRawText(), a.RootElement.GetProperty("tables").GetRawText());
        }
        finally
        {
            try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Cli_refuses_energised_with_a_bus_fault()
    {
        string d = TestTemp.Under("gridsim-wp17fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            string dev = Path.Combine(d, "dev.json");
            File.WriteAllText(dev, "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ { \"id\": \"ZA\", \"type\": \"relay\", \"function\": \"21\", "
                + "\"branch\": { \"index\": 1, \"from\": 4, \"to\": 5, \"end\": \"from\" }, \"ct\": { \"primaryA\": 1000, \"secondaryA\": 1 }, "
                + "\"vt\": { \"primaryV\": 345000, \"secondaryV\": 110 }, \"distance\": { \"characteristic\": \"mho\", \"loops\": \"phase\", \"zones\": [ "
                + "{ \"reachOhms\": 28, \"angleDeg\": 80, \"timeS\": 0 }, { \"reachOhms\": 43, \"angleDeg\": 80, \"timeS\": 0.4 } ], "
                + "\"sotf\": { \"windowS\": 0.5, \"mode\": \"Z2\" } } } ] }");
            CliProcess.Result r = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--energised", "true");
            Assert.Equal(2, r.ExitCode);
            Assert.Contains("--energised true marks a line closed onto a fault, so it needs --branch, not --bus", r.StdErr, StringComparison.Ordinal);
            Assert.Equal(0, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--energised", "false").ExitCode);
        }
        finally
        {
            try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }
}
