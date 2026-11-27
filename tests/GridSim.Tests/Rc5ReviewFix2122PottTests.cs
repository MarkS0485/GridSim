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
/// RC5 (2.1.126, review of 2.1.122): the POTT permissive under prefault load. POTT keyed its permissive on the remote end's
/// overreach pickup from any loop, so the weakness 2.1.122 removed from the DCB carrier stop applied to it too: under heavy
/// load a healthy-phase loop at B can sit inside B's forward zone 2 during an external fault behind B, send the permissive,
/// and let A trip at the channel time. The permissive now comes from B's faulted-phase loops
/// (<see cref="SchemeEnd.FaultLoopOverreachPickup"/>, falling back to <see cref="SchemeEnd.OverreachPickup"/>).
/// <para>
/// Network and loads as <see cref="Rc5ReviewFix2120DcbTests"/>: earthed sources (X'' = X2 = 0.2, X0 = 0.05) at buses 1 and
/// 3, line 1-2 (Z1 = 0.02 + j0.4, the protected line) and line 2-3 (Z1 = 0.0025 + j0.05), both buses held at 1.0 pu, the
/// receiving-end transfer limit 2.109 pu, so the 205 MW step is within 3 % of it. Mho zones at each end of line 1-2:
/// zone 1 0.8, zone 2 1.2 (0.4 s) and a reverse zone 0.5 of the line, POTT with a 0.02 s channel. A's zone 2 reaches
/// 1.2 x 0.4 = 0.48 pu, past bus 3 (0.45 pu), so on an external fault at bus 3 A's own overreach picks up and only the
/// permissive decides whether A is aided.
/// </para>
/// </summary>
public sealed class Rc5ReviewFix2122PottTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0, Channel = 0.02;
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
            Name = "pottsweep",
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
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"both\", \"zones\": [ " + zones + " ], \"scheme\": { \"type\": \"POTT\", \"remote\": \""
            + remote + "\", \"channelS\": " + N(Channel) + " } } }";
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

    /// <summary>External faults behind B (bus 3, and half way along line 2-3), every type, at every load in each direction:
    /// no faulted-phase loop at B picks up forward, so B sends no permissive and A is not aided.</summary>
    [Theory]
    [MemberData(nameof(Loads))]
    public void An_external_fault_sends_no_permissive_at_every_load_up_to_the_transfer_limit(double mw)
    {
        foreach (bool export1To3 in new[] { true, false })
        {
            string dir = (export1To3 ? "export 1 to 3" : "export 3 to 1") + $" at {N(mw)} MW";
            GridModel m = Loaded(mw, export1To3);
            Assert.True(FaultPhasors.DefaultPrefault(m).Converged, dir);
            BoundDeviceSet b = Bind(m, End("A", "from", "B"), End("B", "to", "A"));
            foreach (FaultSpec fs in new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
                foreach (FaultType t in Types)
                {
                    FaultPhasors ph = FaultPhasors.Build(b.Model, fs, t);
                    Assert.Null(ph.NotEvaluated);
                    IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
                    string at = $"{dir}, {fs.Label} {FaultPhasors.TypeName(t)}";
                    Assert.True((rows[1].FaultLoopZonesPicked & 0b011) == 0, $"{at}: a faulted-phase loop at B picks up forward ({rows[1].FaultLoopZonesPicked})");
                    IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
                    Assert.True(s[0]!.Evaluated, at);
                    Assert.False(s[0]!.Aided, at);
                    Assert.Equal(rows[0].Operates ? rows[0].TimeS : double.PositiveInfinity, s[0]!.AidedTripS);
                }
        }
    }

    /// <summary>The case the change is for, at 200 MW and 205 MW: a healthy-phase loop at B in a forward zone during an
    /// external SLG or LLG fault. POTT no longer aids A there; under the old rule (the same ends with no faulted-phase field)
    /// A tripped at the channel time. Both counts must be above 0, so the test cannot pass without reaching the case.</summary>
    [Fact]
    public void A_healthy_phase_loop_in_a_forward_zone_no_longer_sends_the_permissive_at_200_mw()
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
                        Assert.False(SchemeLogic.Apply(b, rows)[0]!.Aided);
                        DistanceSetting setA = b.Devices[0].Distance!, setB = b.Devices[1].Distance!;
                        SchemeEnd local = SchemeLogic.EndOf(rows[0], setA);
                        SchemeEnd remote = SchemeLogic.EndOf(rows[1], setB);
                        Assert.Equal(double.PositiveInfinity, SchemeLogic.SchemeTimeS(SchemeType.Pott, Channel, 0.0, local, remote));
                        SchemeEnd remoteOld = remote with { FaultLoopOverreachPickup = null };
                        if (local.OverreachPickup)
                        {
                            oldRuleChecked++;
                            Assert.Equal(Channel, SchemeLogic.SchemeTimeS(SchemeType.Pott, Channel, 0.0, local, remoteOld));
                        }
                    }
            }
        Assert.True(healthyForward > 0, "no healthy-phase loop at B reached a forward zone at 200-205 MW");
        Assert.True(oldRuleChecked > 0, "A's overreach never picked up in those cases, so the old rule was never checked");
    }

    /// <summary>An internal fault (half way along line 1-2) at 205 MW in each direction, every type: B's faulted-phase loops
    /// see it forward, so the permissive is sent and A trips at once (zone 1) or by the channel time.</summary>
    [Fact]
    public void An_internal_fault_still_sends_the_permissive_at_the_heaviest_load()
    {
        foreach (bool export1To3 in new[] { true, false })
        {
            BoundDeviceSet b = Bind(Loaded(205.0, export1To3), End("A", "from", "B"), End("B", "to", "A"));
            foreach (FaultType t in Types)
            {
                IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AlongBranch(0, 0.5), t));
                string at = $"{(export1To3 ? "export 1 to 3" : "export 3 to 1")} {FaultPhasors.TypeName(t)}";
                Assert.True((rows[1].FaultLoopZonesPicked & 0b011) != 0, $"{at}: B's faulted-phase loops do not see the fault forward ({rows[1].FaultLoopZonesPicked})");
                Assert.True(SchemeLogic.Apply(b, rows)[0]!.AidedTripS <= Channel, at);
            }
        }
    }

    /// <summary>A scheme end with no faulted-phase field (built by hand) keeps the old POTT rule; with the field false, a
    /// remote overreach pickup sends no permissive.</summary>
    [Fact]
    public void The_pott_permissive_falls_back_to_any_loop_without_the_faulted_phase_field()
    {
        var local = new SchemeEnd(double.PositiveInfinity, true, false, 0.4);
        var remote = new SchemeEnd(double.PositiveInfinity, true, false, 0.4);
        Assert.Equal(Channel, SchemeLogic.TripS(SchemeType.Pott, Channel, 0.0, local, remote));
        Assert.Equal(0.4, SchemeLogic.TripS(SchemeType.Pott, Channel, 0.0, local, remote with { FaultLoopOverreachPickup = false }));
        Assert.Equal(Channel, SchemeLogic.TripS(SchemeType.Pott, Channel, 0.0, local, remote with { FaultLoopOverreachPickup = true }));
    }
}
