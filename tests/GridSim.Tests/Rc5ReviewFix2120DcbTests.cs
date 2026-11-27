using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (2.1.122): the DCB carrier stop under prefault load, swept up to the steady-state transfer limit.
/// <para>
/// The 2.1.120 test carried 150 MW across the protected line. An independent sequence-network calculation (NumPy, sources
/// behind X'', constant-impedance load) put the closest healthy-phase loop at B 3 % outside its forward zone 2 at 150 MW,
/// and inside it at 200 MW (SLG: CA 0.73, AB 0.80 of the boundary; LLG: AB 0.91, CA 0.97). A healthy loop in a forward
/// zone used to stop the carrier and let A trip at the coordination time on an external fault. The carrier stop now
/// comes from the loops of the faulted phases only (<see cref="PlacedElementEvaluator.IsFaultLoop"/>: SLG the earth loop
/// a, LL the loop bc, LLG the loops bc, b and c, 3ph every loop).
/// </para>
/// <para>
/// Same network as <see cref="Rc5Wp17ReviewFixTests"/>: earthed sources (X'' = X2 = 0.2, X0 = 0.05) at buses 1 and 3,
/// line 1-2 (Z1 = 0.02 + j0.4) and line 2-3 (Z1 = 0.0025 + j0.05), both buses held at 1.0 pu. The power flow's transfer
/// limit across Z = 0.0225 + j0.45 between the two held voltages is, at the receiving end, V^2 / |Z| - V^2 R / |Z|^2 =
/// 2.2195 - 0.1108 = 2.109 pu, and at the sending end 2.2195 + 0.1108 = 2.330 pu. The sweep runs 0 to 205 MW in each
/// direction, so its top step is within 3 % of the tighter limit (export 1 to 3, received at bus 3).
/// </para>
/// </summary>
public sealed class Rc5ReviewFix2120DcbTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private const string CtVt = "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";

    private static string Zone(double reach, double angle, double t, string extra = "") =>
        "{ \"reachOhms\": " + N(reach) + ", \"angleDeg\": " + N(angle) + ", \"timeS\": " + N(t) + extra + " }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) => DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(
        "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }")), m, new CurveLibrary());

    private static GridModel TwoSource()
    {
        static Generator G(int bus) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2)
        {
            ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05),
        };
        return new GridModel
        {
            Name = "dcbsweep",
            BaseMva = Mva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pv, BaseKv: Kv) },
            Generators = new[] { G(1), G(3) },
            Branches = new[] { new Branch(1, 2, R: 0.02, X: 0.4, R0: 0.06, X0: 1.2), new Branch(2, 3, R: 0.0025, X: 0.05, R0: 0.0075, X0: 0.15) },
        };
    }

    private static string End(string id, string end, string remote)
    {
        Complex zl = new Complex(0.02, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.02) * 180.0 / Math.PI;
        string zones = Zone(0.8 * zl.Magnitude, th, 0.0) + ", " + Zone(1.2 * zl.Magnitude, th, 0.4) + ", " + Zone(0.5 * zl.Magnitude, th, 1.0, ", \"reverse\": true");
        return "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"" + end + "\" }, " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"both\", \"zones\": [ " + zones + " ], \"scheme\": { \"type\": \"DCB\", \"remote\": \""
            + remote + "\", \"coordinationS\": 0.02 } } }";
    }

    private static readonly FaultType[] Types = [FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround];

    private static GridModel Loaded(double mw, bool export1To3)
    {
        GridModel b = TwoSource();
        return export1To3
            ? b.CopyWith(buses: b.Buses.Select(x => x.Id == 3 ? x with { Pd = mw } : x).ToArray())
            : b.CopyWith(buses: b.Buses.Select(x => x.Id == 1 ? x with { Pd = mw } : x).ToArray(),
                generators: b.Generators.Select(g => g.Bus == 3 ? g with { Pg = mw } : g).ToArray());
    }

    public static TheoryData<double> Loads => new() { 0.0, 50.0, 100.0, 150.0, 175.0, 200.0, 205.0 };

    /// <summary>
    /// External faults behind B (bus 3, and half way along line 2-3), every fault type, at every load of the sweep in each
    /// direction: B's reverse zone picks up, no loop of the faulted phases at B lands in a forward zone, so the block holds
    /// and A is not aided (A keeps its plain time, or no trip).
    /// </summary>
    [Theory]
    [MemberData(nameof(Loads))]
    public void An_external_fault_is_blocked_at_every_load_up_to_the_transfer_limit(double mw)
    {
        foreach (bool export1To3 in new[] { true, false })
        {
            string dir = (export1To3 ? "export 1 to 3" : "export 3 to 1") + $" at {N(mw)} MW";
            GridModel m = Loaded(mw, export1To3);
            PowerFlowResult pf = FaultPhasors.DefaultPrefault(m);
            Assert.True(pf.Converged, dir);
            double p12 = pf.LineFlows[0].PFromMw;
            // The transfer on line 1-2 is in the stated direction (less the line losses: 144.4 MW at the bus-1 end for 150
            // MW at bus 3, so above 0.9 of the setting from 50 MW up).
            if (mw > 0.0) Assert.True(export1To3 ? p12 > 0.9 * mw : p12 < -0.9 * mw, $"{dir}: line 1-2 carries {p12} MW");
            BoundDeviceSet b = Bind(m, End("A", "from", "B"), End("B", "to", "A"));
            foreach (FaultSpec fs in new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
                foreach (FaultType t in Types)
                {
                    FaultPhasors ph = FaultPhasors.Build(b.Model, fs, t);
                    Assert.Null(ph.NotEvaluated);
                    IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
                    string at = $"{dir}, {fs.Label} {FaultPhasors.TypeName(t)}";
                    Assert.True((rows[1].ZonesPicked & 0b100) != 0, $"{at}: B's reverse zone does not pick up ({rows[1].ZonesPicked})");
                    Assert.True((rows[1].FaultLoopZonesPicked & 0b011) == 0, $"{at}: a faulted-phase loop at B picks up forward ({rows[1].FaultLoopZonesPicked})");
                    IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                    Assert.True(s[0]!.Evaluated, at);
                    Assert.False(s[0]!.Aided, at);
                    Assert.Equal(rows[0].Operates ? rows[0].TimeS : double.PositiveInfinity, s[0]!.AidedTripS);
                }
        }
    }

    /// <summary>
    /// The case the change is for. At 200 MW and 205 MW the review's calculation puts a healthy-phase loop at B inside its
    /// forward zone 2 during an external SLG or LLG fault. At least one such case shows a forward pickup at B on a loop
    /// that is not a faulted-phase loop (ZonesPicked has a forward bit, FaultLoopZonesPicked does not), and the block still
    /// holds there. Under the 2.1.120 rule that pickup stopped the carrier: the scheme view of the same row without the
    /// faulted-phase field gives A the coordination time 0.02 s.
    /// </summary>
    [Fact]
    public void A_healthy_phase_loop_in_a_forward_zone_no_longer_stops_the_carrier()
    {
        int healthyForward = 0, oldRuleChecked = 0;
        foreach (double mw in new[] { 200.0, 205.0 })
            foreach (bool export1To3 in new[] { true, false })
            {
                BoundDeviceSet b = Bind(Loaded(mw, export1To3), End("A", "from", "B"), End("B", "to", "A"));
                foreach (FaultSpec fs in new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
                    foreach (FaultType t in new[] { FaultType.SingleLineToGround, FaultType.DoubleLineToGround })
                    {
                        IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, FaultPhasors.Build(b.Model, fs, t));
                        if ((rows[1].ZonesPicked & 0b011) == 0) continue;
                        healthyForward++;
                        Assert.Equal(0, rows[1].FaultLoopZonesPicked & 0b011);
                        IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                        Assert.False(s[0]!.Aided);
                        // The old rule (any loop's forward pickup stops the carrier), on the same ends.
                        DistanceSetting setA = b.Devices[0].Distance!, setB = b.Devices[1].Distance!;
                        SchemeEnd local = SchemeLogic.EndOf(rows[0], setA);
                        SchemeEnd remoteOld = SchemeLogic.EndOf(rows[1], setB) with { FaultLoopOverreachPickup = null };
                        if (local.OverreachPickup && double.IsInfinity(local.Z1TimeS))
                        {
                            oldRuleChecked++;
                            Assert.Equal(0.02, SchemeLogic.SchemeTimeS(SchemeType.Dcb, 0.0, 0.02, local, remoteOld));
                        }
                    }
            }
        Assert.True(healthyForward > 0, "no healthy-phase loop at B reached a forward zone at 200-205 MW");
        // The old-rule check must have run at least once (A in zone 2 only, with B's healthy loop forward).
        Assert.True(oldRuleChecked > 0, "the old-rule check never ran: A never had an overreach pickup without zone 1");
    }

    /// <summary>
    /// An internal fault (half way along line 1-2) at the heaviest load in each direction, every fault type: B's faulted-phase
    /// loops see it forward, so B sends no block (or stops it), and A trips at once (zone 1) or at the coordination time.
    /// </summary>
    [Fact]
    public void An_internal_fault_still_stops_the_carrier_at_the_heaviest_load()
    {
        foreach (bool export1To3 in new[] { true, false })
        {
            BoundDeviceSet b = Bind(Loaded(205.0, export1To3), End("A", "from", "B"), End("B", "to", "A"));
            foreach (FaultType t in Types)
            {
                IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AlongBranch(0, 0.5), t));
                string at = $"{(export1To3 ? "export 1 to 3" : "export 3 to 1")} {FaultPhasors.TypeName(t)}";
                Assert.True((rows[1].FaultLoopZonesPicked & 0b011) != 0, $"{at}: B's faulted-phase loops do not see the fault forward ({rows[1].FaultLoopZonesPicked})");
                IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                Assert.True(s[0]!.AidedTripS <= 0.02, $"{at}: A trips at {s[0]!.AidedTripS}");
            }
        }
    }

    /// <summary>The phase selection by fault type (faults as <see cref="FaultAnalysis"/> applies them: SLG on phase a, LL and
    /// LLG on b-c).</summary>
    [Fact]
    public void The_faulted_phase_loops_follow_the_fault_type()
    {
        string[] all = ["ab", "bc", "ca", "a", "b", "c"];
        Assert.All(all, l => Assert.True(PlacedElementEvaluator.IsFaultLoop(l, FaultType.ThreePhase)));
        Assert.Equal(new[] { "a" }, all.Where(l => PlacedElementEvaluator.IsFaultLoop(l, FaultType.SingleLineToGround)));
        Assert.Equal(new[] { "bc" }, all.Where(l => PlacedElementEvaluator.IsFaultLoop(l, FaultType.LineToLine)));
        Assert.Equal(new[] { "bc", "b", "c" }, all.Where(l => PlacedElementEvaluator.IsFaultLoop(l, FaultType.DoubleLineToGround)));
        // A scheme end with no faulted-phase field (built by hand) keeps the old rule: any forward pickup stops the carrier.
        SchemeEnd local = new(double.PositiveInfinity, true, false, 0.4);
        Assert.Equal(0.02, SchemeLogic.TripS(SchemeType.Dcb, 0.0, 0.02, local, new SchemeEnd(double.PositiveInfinity, true, true, 0.4)));
        Assert.Equal(0.4, SchemeLogic.TripS(SchemeType.Dcb, 0.0, 0.02, local,
            new SchemeEnd(double.PositiveInfinity, true, true, 0.4) { FaultLoopOverreachPickup = false }));
    }
}
