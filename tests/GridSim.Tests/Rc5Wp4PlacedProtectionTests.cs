using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP4: placed 21 (mho, quad), 67 / 67N / 67Q, 50N/51N, biased 87L and the coordination checks G1 to G5.
/// Every expected value is a hand derivation on a small network, written out next to its assert, with the sources:
/// IEC 60255-151:2009 (the inverse-time equation, evaluated here with Math.Pow while the code uses DetMath); the
/// symmetrical-component fault equations (Grainger and Stevenson, Power System Analysis, ch. 12); the mho comparator and the
/// quadrilateral (G. Ziegler, Numerical Distance Protection, 4th ed., ch. 3; Network Protection &amp; Automation Guide (NPAG)
/// ch. 11); the 90-degree connection (NPAG ch. 9); the dual-slope biased line differential (NPAG ch. 10).
/// Common data: 100 MVA, 132 kV, so Zbase = 174.24 ohm and Ibase = 437.38656756789834 A. CT 400/1, VT 132000/110, so
/// Zsec / Zprim = 400 / 1200 = 1/3 and Zsec = 58.08 ohm per pu. With no loads and no line charging the prefault state is
/// exactly 1 at 0 degrees, so every fault current is V / Z on the series impedances.
/// </summary>
public sealed class Rc5Wp4PlacedProtectionTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0, Rel = 1e-12;
    private static readonly double IBaseA = Mva / (Math.Sqrt(3.0) * Kv) * 1000.0;   // 437.38656756789834
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static void Near(double expected, double actual, double rel = Rel) =>
        Assert.True(Math.Abs(actual - expected) <= rel * Math.Max(1.0, Math.Abs(expected)), $"expected {N(expected)}, got {N(actual)}");

    private static Generator Gen(int bus, bool zeroSeq = false) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2)
    {
        ZeroSequenceZPu = zeroSeq ? new MachineZeroSequence(0.0, 0.05) : null,
    };

    private static GridModel Model(Branch[] branches, int buses, params Generator[] gens) => new()
    {
        Name = "wp4",
        BaseMva = Mva,
        Buses = Enumerable.Range(1, buses).Select(i => new Bus(i, i == 1 ? BusType.Slack : gens.Any(g => g.Bus == i) ? BusType.Pv : BusType.Pq, BaseKv: Kv)).ToArray(),
        Generators = gens,
        Branches = branches,
    };

    private const string CtVt = "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";

    private static string At(int branch, int from, int to, string end) =>
        "\"branch\": { \"index\": " + branch + ", \"from\": " + from + ", \"to\": " + to + ", \"end\": \"" + end + "\" }";

    private static string Zone(double reach, double angle, double t, string extra = "") =>
        "{ \"reachOhms\": " + N(reach) + ", \"angleDeg\": " + N(angle) + ", \"timeS\": " + N(t) + extra + " }";

    private static string Distance(string id, string at, string loops, params string[] zones) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", " + at + ", " + CtVt
        + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"" + loops + "\", \"zones\": [ " + string.Join(", ", zones) + " ] } }";

    private static string Earth(string id, string function, string at, string body) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"" + function + "\", " + at + ", " + CtVt + ", " + body + " }";

    private static string Doc(params string[] devices) => "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) =>
        DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(devices))), m, new CurveLibrary());

    private static PlacedRow Row(BoundDeviceSet b, FaultSpec f, FaultType t, string id)
    {
        FaultPhasors ph = FaultPhasors.Build(b.Model, f, t);
        Assert.Null(ph.NotEvaluated);
        return PlacedElementEvaluator.Evaluate(b, ph).Single(r => r.DeviceId == id);
    }

    private static double IecSi(double tms, double m) => tms * 0.14 / (Math.Pow(m, 0.02) - 1.0);

    // ---- T1: radial with resistance, three-phase faults ---------------------------------------------------------------

    /// <summary>Bus 1 generator (X''d = X2 = 0.2); line 0 from 1 to 2, Z1 = 0.04 + j0.4 (X/R = 10). ZLsec = 2.3232 + j23.232.</summary>
    private static GridModel T1() => Model(new[] { new Branch(1, 2, R: 0.04, X: 0.4) }, 2, Gen(1));

    private static readonly Complex ZLsec = new Complex(0.04, 0.4) * OhmPerPu;
    private static readonly double ThetaL = Math.Atan2(0.4, 0.04) * 180.0 / Math.PI;   // 84.28940686250037

    private static string T1Relay() => Distance("R1", At(0, 1, 2, "from"), "both",
        Zone(0.8 * ZLsec.Magnitude, ThetaL, 0.0), Zone(1.2 * ZLsec.Magnitude, ThetaL, 0.4), Zone(2.0 * ZLsec.Magnitude, ThetaL, 1.0));

    [Fact]
    public void T1_mho_distance_sees_d_times_the_line_impedance_and_picks_the_zone()
    {
        // A bolted three-phase fault at d: I = 1 / (j0.2 + d ZL) and V_relay = I d ZL, so Zapp = d ZL exactly, whatever the source.
        BoundDeviceSet b = Bind(T1(), T1Relay());
        foreach ((double d, int zone, double time) in new[] { (0.5, 1, 0.0), (0.85, 2, 0.4) })
        {
            PlacedRow r = Row(b, FaultSpec.AlongBranch(0, d), FaultType.ThreePhase, "R1");
            Near((d * ZLsec).Real, r.ApparentImpedanceOhms.Real);        // 1.1616 at 0.5, 1.97472 at 0.85
            Near((d * ZLsec).Imaginary, r.ApparentImpedanceOhms.Imaginary); // 11.616 at 0.5, 19.7472 at 0.85
            Assert.Equal(zone, r.Zone);
            Assert.Equal(time, r.TimeS);
            Assert.Equal("ab", r.Loop);   // the three phase loops are equal; the first wins the tie
        }
        PlacedRow far = Row(b, FaultSpec.AtBus(2), FaultType.ThreePhase, "R1");
        Near(2.3232, far.ApparentImpedanceOhms.Real);
        Near(23.232, far.ApparentImpedanceOhms.Imaginary);
        Assert.Equal(2, far.Zone);
    }

    [Fact]
    public void T1_grading_checks_zone_1_underreach_zone_2_cover_and_zone_1_coverage()
    {
        // G4 sweeps 0.05, 0.15, ..., 0.95 (none on the 0.8 edge, where Zapp is collinear with the reach): 0.75 is in zone 1,
        // 0.85 is not, so the coverage is 0.75.
        BoundDeviceSet b = Bind(T1(), T1Relay());
        double[] sweep = Enumerable.Range(0, 10).Select(k => (2 * k + 1) / 20.0).ToArray();
        PlacedCoordinationResult r = PlacedCoordination.Run(b, new[] { FaultSpec.AtBus(2) }, new[] { FaultType.ThreePhase }, 0.4, sweep);
        PlacedCheck g2 = r.Checks.Single(c => c.Check == "G2"), g3 = r.Checks.Single(c => c.Check == "G3"), g4 = r.Checks.Single(c => c.Check == "G4");
        Assert.True(g2.Passed); Assert.Equal(2.0, g2.Value);
        Assert.True(g3.Passed);
        Assert.True(g4.Passed); Assert.Equal(0.75, g4.Value);
        Assert.Equal(PlacedVerdict.Coordinated, r.Verdict);
        // A zone 1 set to 1.1 x ZL overreaches the far bus: G2 fails and the verdict says so.
        BoundDeviceSet over = Bind(T1(), Distance("R1", At(0, 1, 2, "from"), "both", Zone(1.1 * ZLsec.Magnitude, ThetaL, 0.0), Zone(1.2 * ZLsec.Magnitude, ThetaL, 0.4)));
        PlacedCoordinationResult ro = PlacedCoordination.Run(over, new[] { FaultSpec.AtBus(2) }, new[] { FaultType.ThreePhase }, 0.4, sweep);
        Assert.False(ro.Checks.Single(c => c.Check == "G2").Passed);
        Assert.Equal(0.95, ro.Checks.Single(c => c.Check == "G4").Value);
        Assert.Equal(PlacedVerdict.NotCoordinated, ro.Verdict);
    }

    private static string Dir67(string id, string at, string pol, double rca, string direction = "forward") =>
        Earth(id, "67", at, "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ], \"directional\": { \"polarisation\": \"" + pol
            + "\", \"characteristicAngleDeg\": " + N(rca) + ", \"direction\": \"" + direction + "\" }");

    [Fact]
    public void T1_quadrature_67_is_forward_for_a_fault_ahead_and_blocked_for_one_behind()
    {
        // Fault at bus 2: Ia = 1 / (0.04 + j0.6), arg Ia = -86.18592516570965 deg; Va = Ia ZL, Vbc = Va (a^2 - a) = -j sqrt(3) Va,
        // arg Vbc = arg Ia + thetaL - 90 = -91.8965183032093 deg. Difference 5.7106 deg; cos(5.7106 - 45) > 0: forward.
        BoundDeviceSet b = Bind(T1(), Dir67("F", At(0, 1, 2, "from"), "quadrature", 45.0));
        PlacedRow r = Row(b, FaultSpec.AtBus(2), FaultType.ThreePhase, "F");
        double argIa = -Math.Atan2(0.6, 0.04) * 180.0 / Math.PI;
        Near(-86.18592516570965, argIa);
        Near(argIa - (argIa + ThetaL - 90.0), r.AngleDifferenceDeg, 1e-9);   // 90 - thetaL = 5.71059313749963
        Assert.Equal("forward", r.Direction);
        Assert.True(r.Operates);
        // The overcurrent element times on |Ia| = 1 / |0.04 + j0.6| pu through the CT: IEC SI, TMS 0.1, pickup 0.2 A.
        double amps = 1.0 / new Complex(0.04, 0.6).Magnitude * IBaseA / 400.0;
        Near(amps, r.MeasuredValue);
        Near(IecSi(0.1, amps / 0.2), r.TimeS, 1e-11);

        // Radial 1-2-3, both lines 0.04 + j0.4, fault at bus 3. The relay at the from end of line 2-3 sees the same 5.71 deg;
        // the relay at the to end of line 1-2 (at bus 2, looking back at the source) measures -I: 185.71 deg, reverse.
        GridModel m3 = Model(new[] { new Branch(1, 2, R: 0.04, X: 0.4), new Branch(2, 3, R: 0.04, X: 0.4) }, 3, Gen(1));
        BoundDeviceSet b3 = Bind(m3, Dir67("FWD", At(1, 2, 3, "from"), "quadrature", 45.0), Dir67("REV", At(0, 1, 2, "to"), "quadrature", 45.0));
        PlacedRow fwd = Row(b3, FaultSpec.AtBus(3), FaultType.ThreePhase, "FWD"), rev = Row(b3, FaultSpec.AtBus(3), FaultType.ThreePhase, "REV");
        Near(90.0 - ThetaL, fwd.AngleDifferenceDeg, 1e-9);
        Assert.True(fwd.Operates);
        Near(270.0 - ThetaL, rev.AngleDifferenceDeg, 1e-9);   // 185.71059313749963
        Assert.Equal("reverse", rev.Direction);
        Assert.False(rev.Operates);
        Assert.False(rev.FaultDirection);
    }

    // ---- T2: SLG radial, pure reactance --------------------------------------------------------------------------------

    /// <summary>Generator X1 = X2 = 0.2, Z0 to earth j0.05; line 1-2 Z1 = j0.4, Z0 = j1.2; line 2-3 the same with no source
    /// beyond it. SLG at bus 2: Z1 = Z2 = j0.6, Z0 = j1.25.</summary>
    private static GridModel T2() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4, X0: 1.2), new Branch(2, 3, R: 0.0, X: 0.4, X0: 1.2) }, 3, Gen(1, zeroSeq: true));

    private static readonly double ResidualSec = 3.0 / 2.45 * IBaseA / 400.0;   // 1.3389384721466273

    [Fact]
    public void T2_earth_fault_elements_measure_3I0_and_time_on_the_iec_curve()
    {
        // I0 = 1 / j2.45; 3I0 = 1.2244897959183672 pu = 535.575388858651 A primary = 1.3389384721466273 A secondary. One zero-sequence
        // source, so the relay residual equals the fault 3I0.
        Near(1.3389384721466273, ResidualSec);
        BoundDeviceSet b = Bind(T2(),
            Earth("E1", "50N", At(0, 1, 2, "from"), "\"instantaneous\": { \"pickupA\": 1.0, \"delayS\": 0.05 }"),
            Earth("E2", "51N", At(0, 1, 2, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ]"));
        PlacedRow e1 = Row(b, FaultSpec.AtBus(2), FaultType.SingleLineToGround, "E1");
        PlacedRow e2 = Row(b, FaultSpec.AtBus(2), FaultType.SingleLineToGround, "E2");
        Near(ResidualSec, e1.MeasuredValue);
        Assert.Equal("50N/51N", e1.Element);
        Assert.Equal(0.05, e1.TimeS);
        Near(ResidualSec, e2.MeasuredValue);
        // M = 6.694692360733137, t = 0.1 x 0.14 / (M^0.02 - 1) = 0.36121060046277065 s.
        Near(6.694692360733137, ResidualSec / 0.2);
        Near(0.36121060046277065, IecSi(0.1, ResidualSec / 0.2));
        Near(IecSi(0.1, ResidualSec / 0.2), e2.TimeS, 1e-11);
        // G5: the smallest |3I0| beyond E2 over the SLG faults is at bus 3, the end of line 2-3: Z1 = Z2 = j1.0 and
        // Z0 = j(0.05 + 1.2 + 1.2) = j2.45, so 3I0 = 3 / 4.45 pu; over the 0.2 A pickup.
        PlacedCoordinationResult g = PlacedCoordination.Run(b, null, new[] { FaultType.SingleLineToGround }, 0.4);
        PlacedCheck g5 = g.Checks.Single(c => c.Check == "G5" && c.DeviceId == "E2");
        Assert.True(g5.Passed);
        Near(3.0 / 4.45 * IBaseA / 400.0 / 0.2, g5.Value);
        Assert.Contains("bus 3 slg", g5.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void T2_earth_loop_with_k0_from_the_line_sees_exactly_the_line_and_the_bus_1_voltages_are_the_hand_values()
    {
        // V1 = 1 - j0.2 I1 = 0.9183673469, V2 = -j0.2 I2 = -0.0816326531, V0 = -j0.05 I0 = -0.0204081633, so Va = 0.8163265306122449
        // and |Vb| = |Vc| = 0.9708367258992894. k0 = (1.2 - 0.4) / (3 x 0.4) = 2/3, and Za = Va / (Ia + k0 3I0) = j0.4 pu = ZL1:
        // outside zone 1 (0.8 ZL), inside zone 2.
        GridModel m = T2();
        FaultPhasors ph = FaultPhasors.Build(m, FaultSpec.AtBus(2), FaultType.SingleLineToGround);
        Assert.True(ph.TryBus(1, out int bi));
        (Complex va, Complex vb, Complex vc) = ph.PhaseVoltages(bi);
        Near(0.8163265306122449, va.Real);
        Assert.True(Math.Abs(va.Imaginary) <= 1e-14);
        Near(0.9708367258992894, vb.Magnitude);
        Near(0.9708367258992894, vc.Magnitude);
        double zl = 0.4 * OhmPerPu;   // 23.232
        BoundDeviceSet b = Bind(m, Distance("D1", At(0, 1, 2, "from"), "both", Zone(0.8 * zl, 90.0, 0.0), Zone(1.2 * zl, 90.0, 0.4), Zone(2.0 * zl, 90.0, 1.0)));
        Complex? k0 = PlacedElementEvaluator.K0(b.Devices[0], m.Branches[0]);
        Near(2.0 / 3.0, k0!.Value.Real);
        PlacedRow r = PlacedElementEvaluator.Evaluate(b, ph)[0];
        Assert.Equal("a", r.Loop);
        Assert.True(Math.Abs(r.ApparentImpedanceOhms.Real) <= 1e-12 * zl);
        Near(zl, r.ApparentImpedanceOhms.Imaginary);
        Assert.Equal(2, r.Zone);
        Assert.Equal(0.4, r.TimeS);
    }

    [Fact]
    public void T2_zero_sequence_67N_is_forward_ahead_reverse_behind_and_silent_where_no_current_flows()
    {
        // At bus 1: -3V0 = 0.0612244898 at 0 deg and 3I0 at -90 deg, so arg Iop - arg Vpol = -90 deg and with RCA -60 the
        // comparator angle is -30 deg: forward. At the to end of line 1-2 (bus 2) the relay measures -3I0 at +90 deg against
        // -3V0 at 0 deg: 150 deg from the RCA, reverse. On line 2-3 (nothing beyond bus 3) no current flows.
        BoundDeviceSet b = Bind(T2(), Dir67("N1", At(0, 1, 2, "from"), "zero-sequence", -60.0), Dir67("N2", At(1, 2, 3, "from"), "zero-sequence", -60.0),
            Dir67("N3", At(0, 1, 2, "to"), "zero-sequence", -60.0));
        FaultPhasors ph = FaultPhasors.Build(b.Model, FaultSpec.AtBus(2), FaultType.SingleLineToGround);
        IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
        Assert.Equal("67N", rows[0].Element);
        Near(-90.0, rows[0].AngleDifferenceDeg, 1e-9);
        Assert.Equal("forward", rows[0].Direction);
        Near(ResidualSec, rows[0].MeasuredValue);
        Assert.True(rows[0].Operates);
        Assert.False(rows[1].Operates);
        Assert.Equal("no operating current", rows[1].Direction);
        Near(90.0, rows[2].AngleDifferenceDeg, 1e-9);
        Assert.Equal("reverse", rows[2].Direction);
        Assert.False(rows[2].Operates);
        // A 67N set reverse operates for the fault behind it.
        BoundDeviceSet br = Bind(T2(), Dir67("N3R", At(0, 1, 2, "to"), "zero-sequence", -60.0, "reverse"));
        Assert.True(Row(br, FaultSpec.AtBus(2), FaultType.SingleLineToGround, "N3R").Operates);
    }

    [Fact]
    public void T2_a_line_to_line_fault_has_no_residual_so_earth_elements_rest_and_the_bc_loop_sees_the_line()
    {
        // LL at bus 2: I0 = 0, so 50N, 51N and 67N do not operate and the earth loops are not released. The b-c loop gives
        // Zbc = (Vb - Vc) / (Ib - Ic) = j0.4 pu, the line (the overlay's N117 rule), inside zone 2.
        double zl = 0.4 * OhmPerPu;
        BoundDeviceSet b = Bind(T2(),
            Earth("E1", "50N", At(0, 1, 2, "from"), "\"instantaneous\": { \"pickupA\": 1.0, \"delayS\": 0.05 }"),
            Earth("E2", "51N", At(0, 1, 2, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ]"),
            Dir67("N1", At(0, 1, 2, "from"), "zero-sequence", -60.0),
            Distance("D1", At(0, 1, 2, "from"), "both", Zone(0.8 * zl, 90.0, 0.0), Zone(1.2 * zl, 90.0, 0.4)));
        IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AtBus(2), FaultType.LineToLine));
        Assert.False(rows[0].Operates); Assert.Equal(0.0, rows[0].MeasuredValue);
        Assert.False(rows[1].Operates);
        Assert.False(rows[2].Operates);
        Assert.Equal("bc", rows[3].Loop);
        Assert.Contains("not released", rows[3].Reason, StringComparison.Ordinal);
        Near(zl, rows[3].ApparentImpedanceOhms.Imaginary);
        Assert.True(Math.Abs(rows[3].ApparentImpedanceOhms.Real) <= 1e-12 * zl);
        Assert.Equal(2, rows[3].Zone);
    }

    // ---- T3: two sources, biased 87L ------------------------------------------------------------------------------------

    private static GridModel T3() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4) }, 2, Gen(1), Gen(2));

    private const string Diff = "{ \"id\": \"L1\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"from\" }, "
        + "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"differential\": { \"is1A\": 0.2, \"k1\": 0.3, \"is2A\": 2.0, \"k2\": 1.5 } }";

    [Fact]
    public void T3_biased_differential_operates_for_an_internal_fault_and_restrains_for_an_external_one()
    {
        BoundDeviceSet b = Bind(T3(), Diff);
        // Internal three-phase fault at d = 0.5: each end carries 1 / j0.4 = -j2.5 pu INTO the line = 2.7336660472993644 A sec.
        // Id = 5.467332094598729 A, Ib = 2.7336660472993644 A (above Is2), threshold 1.5 Ib - 2.4 + 0.2 = 1.9004990709490468 A.
        PlacedRow inside = Row(b, FaultSpec.AlongBranch(0, 0.5), FaultType.ThreePhase, "L1");
        double end = 2.5 * IBaseA / 400.0;
        Near(2.7336660472993644, end);
        Near(2.0 * end, inside.DifferentialA);
        Near(end, inside.BiasA);
        Near(1.5 * end - 2.4 + 0.2, inside.ThresholdA);
        Near(1.9004990709490468, inside.ThresholdA);
        Assert.Equal("87L", inside.Element);
        Assert.Equal(0.0, inside.TimeS);
        // External fault at bus 2: the line carries 1 / j0.6 in at the from end and out at the to end. Id = 0 (to 1e-15),
        // Ib = 1.8224440315329098 A, threshold 0.3 Ib + 0.2 = 0.7467332094598729 A: restrains.
        PlacedRow outside = Row(b, FaultSpec.AtBus(2), FaultType.ThreePhase, "L1");
        Assert.True(outside.DifferentialA <= 1e-12, N(outside.DifferentialA));
        Near(1.8224440315329098, outside.BiasA);
        Near(0.7467332094598729, outside.ThresholdA);
        Assert.False(outside.Operates);
    }

    [Fact]
    public void T3_the_dual_slope_characteristic_points_are_exact()
    {
        var s = new DifferentialSetting(0.2, 0.3, 2.0, 1.5, 400, 1);
        // Below Is2 the threshold is 0.3 x 1.0 + 0.2 = 0.5; above it 1.5 x 3.0 - 2.4 + 0.2 = 2.3.
        Near(0.5, PlacedElementEvaluator.DifferentialThreshold(1.0, s));
        Near(2.3, PlacedElementEvaluator.DifferentialThreshold(3.0, s));
        Assert.False(0.49 > PlacedElementEvaluator.DifferentialThreshold(1.0, s));
        Assert.True(0.51 > PlacedElementEvaluator.DifferentialThreshold(1.0, s));
        Assert.False(2.29 > PlacedElementEvaluator.DifferentialThreshold(3.0, s));
        Assert.True(2.31 > PlacedElementEvaluator.DifferentialThreshold(3.0, s));
        // CT mismatch: I_near = 5, I_far = -4.5 (both into the line): Id = 0.5, Ib = 4.75, threshold 4.925: restrains.
        (double id, double ib, double thr, bool op) = PlacedElementEvaluator.DifferentialPhase(new Complex(5.0, 0.0), new Complex(-4.5, 0.0), s);
        Assert.Equal(0.5, id); Assert.Equal(4.75, ib); Near(4.925, thr); Assert.False(op);
    }

    // ---- T4: grading on a radial 1-2-3 --------------------------------------------------------------------------------

    /// <summary>Generator as in T2. L12: Z1 = j0.2, Z0 = j0.6. L23: Z1 = j0.4, Z0 = j1.2.</summary>
    private static GridModel T4() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.2, X0: 0.6), new Branch(2, 3, R: 0.0, X: 0.4, X0: 1.2) }, 3, Gen(1, zeroSeq: true));

    private static string Si51N(string id, string at, double tms) => Earth(id, "51N", at, "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": " + N(tms) + " } ]");

    [Fact]
    public void T4_earth_fault_grading_margin_against_the_cti()
    {
        // SLG at bus 3: Z1 = Z2 = j0.8, Z0 = j(0.05 + 1.8) = j1.85, so 3I0 = 3 / 3.45 = 0.8695652173913044 pu = 0.95084036427804 A sec.
        // tA = 0.05 x 0.14 / (M^0.02 - 1) = 0.22101692456584998 s, tB (TMS 0.2) = 0.8840676982633999 s: margin 0.6630507736975499 s >= 0.40.
        double i = 3.0 / 3.45 * IBaseA / 400.0, m = i / 0.2;
        Near(0.95084036427804, i);
        Near(0.22101692456584998, IecSi(0.05, m));
        Near(0.8840676982633999, IecSi(0.2, m));
        BoundDeviceSet b = Bind(T4(), Si51N("A", At(1, 2, 3, "from"), 0.05), Si51N("B", At(0, 1, 2, "from"), 0.2));
        PlacedCoordinationResult r = PlacedCoordination.Run(b, new[] { FaultSpec.AtBus(3) }, new[] { FaultType.SingleLineToGround }, 0.4);
        PlacedPairRow p = Assert.Single(r.Pairs);
        Assert.Equal(("A", "B"), (p.PrimaryId, p.BackupId));
        Assert.True(p.Evaluated);
        Near(IecSi(0.05, m), p.PrimaryTimeS, 1e-11);
        Near(IecSi(0.2, m), p.BackupTimeS, 1e-11);
        Near(0.6630507736975499, p.MarginS, 1e-10);
        Assert.True(p.Coordinated);
        Assert.Equal(PlacedVerdict.Coordinated, r.Verdict);

        // TMS_B = 0.08: tB = 0.08 x 0.14 / (M^0.02 - 1), margin 0.0884 s < 0.40: not coordinated.
        BoundDeviceSet b2 = Bind(T4(), Si51N("A", At(1, 2, 3, "from"), 0.05), Si51N("B", At(0, 1, 2, "from"), 0.08));
        PlacedCoordinationResult r2 = PlacedCoordination.Run(b2, new[] { FaultSpec.AtBus(3) }, new[] { FaultType.SingleLineToGround }, 0.4);
        PlacedPairRow p2 = Assert.Single(r2.Pairs);
        Near(IecSi(0.08, m) - IecSi(0.05, m), p2.MarginS, 1e-10);
        Assert.False(p2.Coordinated);
        Assert.Equal(PlacedVerdict.NotCoordinated, r2.Verdict);
    }

    [Fact]
    public void T4_distance_grading_zone_2_backup_against_zone_1_primary()
    {
        // Three-phase fault at d = 0.5 of L23: A (from end of L23) sees j0.2 pu = j11.616 ohm, inside its zone 1 (0.8 x j0.4 pu), 0 s;
        // B (from end of L12) sees j0.4 pu = j23.232 ohm, outside its zone 1 (0.8 x j0.2 pu), inside its zone 2 (j0.6 pu) at 0.5 s.
        // Margin 0.5 s >= 0.4: coordinated. With zone 2 at 0.3 s it is not.
        foreach ((double t2, bool ok) in new[] { (0.5, true), (0.3, false) })
        {
            BoundDeviceSet b = Bind(T4(),
                Distance("A", At(1, 2, 3, "from"), "both", Zone(0.8 * 0.4 * OhmPerPu, 90.0, 0.0), Zone(1.2 * 0.4 * OhmPerPu, 90.0, 0.4)),
                Distance("B", At(0, 1, 2, "from"), "both", Zone(0.8 * 0.2 * OhmPerPu, 90.0, 0.0), Zone(0.6 * OhmPerPu, 90.0, t2)));
            PlacedCoordinationResult r = PlacedCoordination.Run(b, new[] { FaultSpec.AlongBranch(1, 0.5) }, new[] { FaultType.ThreePhase }, 0.4);
            PlacedPairRow p = Assert.Single(r.Pairs);
            Assert.Equal(("A", "B"), (p.PrimaryId, p.BackupId));
            Assert.Equal(0.0, p.PrimaryTimeS);
            Assert.Equal(t2, p.BackupTimeS);
            Assert.Equal(t2, p.MarginS);
            Assert.Equal(ok, p.Coordinated);
            Assert.Equal(ok ? PlacedVerdict.Coordinated : PlacedVerdict.NotCoordinated, r.Verdict);
            Assert.Equal(4, r.Checks.Count(c => c.Check is "G2" or "G3"));
            Assert.All(r.Checks.Where(c => c.Check is "G2" or "G3"), c => Assert.True(c.Passed, c.DeviceId + " " + c.Check + ": " + c.Detail));
        }
    }

    // ---- characteristics (pure functions) -------------------------------------------------------------------------------

    [Fact]
    public void Mho_characteristic_points()
    {
        // Zr = 18.67829683 at thetaL: 0.5 Zr in, 1.01 Zr out; at thetaL - 30 deg the reach is cos 30 = 0.8660254 of |Zr|.
        double zr = 0.8 * ZLsec.Magnitude, th = ThetaL * Math.PI / 180.0, off = (ThetaL - 30.0) * Math.PI / 180.0;
        Near(18.67829683, zr, 1e-9);
        Complex r = Complex.FromPolarCoordinates(zr, th);
        Assert.True(DistanceCharacteristics.InMho(0.5 * r, r));
        Assert.False(DistanceCharacteristics.InMho(1.01 * r, r));
        Assert.True(DistanceCharacteristics.InMho(Complex.FromPolarCoordinates(0.86 * zr, off), r));
        Assert.False(DistanceCharacteristics.InMho(Complex.FromPolarCoordinates(0.87 * zr, off), r));
        Assert.False(DistanceCharacteristics.InMho(-0.1 * r, r));   // behind the relay
    }

    [Fact]
    public void Quadrilateral_characteristic_points()
    {
        // Zone at thetaL (1 / tan thetaL = 0.1 because X/R = 10) with |Zr| = 0.8 |ZLsec|, so Xr = 0.8 x 23.232 = 18.5856; R = RL = 10;
        // directional -30 / 120 deg.
        var zone = new DistanceZone(0.8 * ZLsec.Magnitude, ThetaL, 0.0, 10.0, 10.0);
        Near(18.5856, zone.ReachOhms * Math.Sin(ThetaL * Math.PI / 180.0));
        Assert.True(DistanceCharacteristics.InQuad(new Complex(5, 10), zone, -30, 120));     // 5 - 1 = 4 <= 10
        Assert.False(DistanceCharacteristics.InQuad(new Complex(12, 10), zone, -30, 120));   // 12 - 1 = 11 > 10
        Assert.False(DistanceCharacteristics.InQuad(new Complex(1, 19), zone, -30, 120));    // X 19 > 18.5856
        Assert.False(DistanceCharacteristics.InQuad(new Complex(-3, 5), zone, -30, 120));    // arg 120.96 deg
        Assert.True(DistanceCharacteristics.InQuad(new Complex(2, -0.5), zone, -30, 120));   // arg -14 deg
        Assert.False(DistanceCharacteristics.InQuad(new Complex(-11, 5), zone, -30, 180));   // -11 - 0.5 < -10: the left blinder
    }

    [Fact]
    public void A_quad_zone_evaluates_through_the_device_file()
    {
        // T1 at d = 0.5: Zapp = 1.1616 + j11.616 ohm; a quad zone 1 with Xr = 18.5856 and R = 10 holds it (offset 1.1616 - 1.1616 = 0).
        string quad = "{ \"id\": \"Q\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
            + ", \"distance\": { \"characteristic\": \"quad\", \"loops\": \"phase\", \"zones\": [ "
            + Zone(0.8 * ZLsec.Magnitude, ThetaL, 0.0, ", \"resistiveReachOhms\": 10") + " ] } }";
        BoundDeviceSet b = Bind(T1(), quad);
        Assert.Equal(1, Row(b, FaultSpec.AlongBranch(0, 0.5), FaultType.ThreePhase, "Q").Zone);
        Assert.Null(Row(b, FaultSpec.AlongBranch(0, 0.85), FaultType.ThreePhase, "Q").Zone);
    }

    // ---- invariance, refusals, determinism ------------------------------------------------------------------------------

    /// <summary>The PlacedDeviceCoordinationTests fixture: gen (X''d 0.1) at bus 1; line 1-2 X 0.1; line 2-3 X 0.2.</summary>
    private static GridModel R407Radial() => new()
    {
        BaseMva = 100.0,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv), new Bus(3, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.1) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.1), new Branch(2, 3, R: 0.0, X: 0.2) },
    };

    [Fact]
    public void The_old_verbs_still_list_a_21_as_not_evaluated_when_it_carries_distance_zones()
    {
        string relayA = """{ "id": "A", "type": "relay", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" }, "ct": { "primaryA": 1, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 400, "timeDial": 0.1 } ] }""";
        string relayB = """{ "id": "B", "type": "relay", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "ct": { "primaryA": 1, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 800, "timeDial": 0.3 } ] }""";
        string plain = """{ "id": "Z", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "shots": [ { "curve": "iec-si", "pickupA": 1 } ] }""";
        string zoned = """{ "id": "Z", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "vt": { "primaryV": 132000, "secondaryV": 110 }, "shots": [ { "curve": "iec-si", "pickupA": 1 } ], "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 5, "angleDeg": 90, "timeS": 0 } ] } }""";
        GridModel m = R407Radial();
        BoundDeviceSet a = Bind(m, relayA, relayB, plain), z = Bind(m, relayA, relayB, zoned);
        TraceResult ta = ShortCircuitTrace.Run(a, FaultSpec.AtBus(2), ShortCircuitObjective.Maximum, 0.4);
        TraceResult tz = ShortCircuitTrace.Run(z, FaultSpec.AtBus(2), ShortCircuitObjective.Maximum, 0.4);
        Assert.False(tz.Rows[2].Evaluated);
        Assert.Equal(ProtectionConfidence.NotCheckable, tz.Rows[2].Confidence);
        Assert.Equal(ta.Rows[2].Reason, tz.Rows[2].Reason);
        Assert.Equal(TraceCanon(ta), TraceCanon(tz));
        ProtectionAuditResult ra = ProtectionAudit.Run(a, new[] { FaultSpec.AtBus(2) }), rz = ProtectionAudit.Run(z, new[] { FaultSpec.AtBus(2) });
        Assert.Contains(rz.NotEvaluated, n => n.Subject == "Z");
        Assert.Equal(string.Join("|", ra.NotEvaluated.Select(n => n.Subject + n.Check + n.Reason)), string.Join("|", rz.NotEvaluated.Select(n => n.Subject + n.Check + n.Reason)));
        Assert.Equal(ra.Verdict, rz.Verdict);
        // A 21 with zones and no shots binds too (it needs no curve) and the old trace still lists it, word for word.
        string noShots = """{ "id": "Z", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "vt": { "primaryV": 132000, "secondaryV": 110 }, "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 5, "angleDeg": 90, "timeS": 0 } ] } }""";
        TraceResult tn = ShortCircuitTrace.Run(Bind(m, relayA, relayB, noShots), FaultSpec.AtBus(2), ShortCircuitObjective.Maximum, 0.4);
        Assert.Equal(ta.Rows[2].Reason, tn.Rows[2].Reason);
        // The schema stays gridsim-devices/1.
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes("""{ "manifestVersion": "gridsim-devices/2", "devices": [] }""")));
    }

    private static string TraceCanon(TraceResult t)
    {
        var sb = new StringBuilder();
        foreach (DeviceTraceRow r in t.Rows)
            sb.Append(r.DeviceId).Append(r.Evaluated).Append(r.Reason).Append(BitConverter.DoubleToInt64Bits(r.PrimaryCurrentA)).Append(BitConverter.DoubleToInt64Bits(r.ClearingTimeS)).Append(';');
        foreach (PairRow p in t.Pairs) sb.Append(p.PrimaryId).Append(p.BackupId).Append(BitConverter.DoubleToInt64Bits(p.MarginS)).Append(';');
        sb.Append(string.Join(">", t.Sequence));
        return sb.ToString();
    }

    [Fact]
    public void Bad_function_settings_are_refused_naming_the_device()
    {
        // distance on a 51.
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(
            "{ \"id\": \"X\", \"type\": \"relay\", \"function\": \"51\", " + At(0, 1, 2, "from") + ", \"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 1 } ], \"distance\": { \"characteristic\": \"mho\", \"zones\": [ " + Zone(5, 90, 0) + " ] } }"))));
        // differential on a transformer.
        GridModel tx = Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4, IsTransformer: true) }, 2, Gen(1), Gen(2));
        DeviceSetException e = Assert.Throws<DeviceSetException>(() => Bind(tx, Diff));
        Assert.Contains("L1", e.Message, StringComparison.Ordinal);
        Assert.Contains("vector-group", e.Message, StringComparison.Ordinal);
        // a 21 with no VT.
        Assert.Throws<DeviceSetException>(() => Bind(T1(), "{ \"id\": \"X\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from")
            + ", \"distance\": { \"characteristic\": \"mho\", \"zones\": [ " + Zone(5, 90, 0) + " ] } }"));
        // zone 2 reach below zone 1.
        Assert.Throws<DeviceSetException>(() => Bind(T1(), Distance("X", At(0, 1, 2, "from"), "both", Zone(10, 90, 0), Zone(5, 90, 0.4))));
        // k2 below k1.
        Assert.Throws<DeviceSetException>(() => Bind(T3(), Diff.Replace("\"k2\": 1.5", "\"k2\": 0.1", StringComparison.Ordinal)));
        // an 87 without its differential, a quad zone without a resistive reach, an unknown polarisation.
        Assert.Throws<DeviceSetException>(() => Bind(T3(), "{ \"id\": \"X\", \"type\": \"relay\", \"function\": \"87\", " + At(0, 1, 2, "from") + " }"));
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc("{ \"id\": \"X\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
            + ", \"distance\": { \"characteristic\": \"quad\", \"zones\": [ " + Zone(5, 80, 0) + " ] } }"))));
        Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(Dir67("X", At(0, 1, 2, "from"), "sideways", 45)))));
    }

    [Fact]
    public void The_evaluation_is_byte_identical_across_runs_and_cultures()
    {
        BoundDeviceSet b = Bind(T2(),
            Earth("E2", "51N", At(0, 1, 2, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ]"),
            Dir67("N1", At(0, 1, 2, "from"), "zero-sequence", -60.0),
            Distance("D1", At(0, 1, 2, "from"), "both", Zone(0.8 * 0.4 * OhmPerPu, 90.0, 0.0), Zone(1.2 * 0.4 * OhmPerPu, 90.0, 0.4)));
        string Csv() => PlacedElementEvaluator.ToCsv(PlacedElementEvaluator.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AlongBranch(0, 0.3), FaultType.SingleLineToGround)));
        string first = Csv();
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(first, Csv());
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Assert.Equal(first, Csv());
        Assert.Contains(",0,true\n", first, StringComparison.Ordinal);   // D1: zone 1 at 0 s (0.3 of the line)
        Assert.Contains("21,true,,", first, StringComparison.Ordinal);
    }

    // ---- the CLI verbs --------------------------------------------------------------------------------------------------

    private const string Case9Devices = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "Z45", "type": "relay", "function": "21", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "vt": { "primaryV": 345000, "secondaryV": 110 },
        "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 10, "angleDeg": 80, "timeS": 0 }, { "reachOhms": 20, "angleDeg": 80, "timeS": 0.4 } ] } },
      { "id": "D45", "type": "relay", "function": "87", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } },
      { "id": "O45", "type": "relay", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.5, "timeDial": 0.1 } ] } ] }
    """;

    [Fact]
    public void Protection_eval_and_protection_grade_run_as_the_built_cli_and_repeat_exactly()
    {
        string d = TestTemp.Under("gridsim-wp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        string dev = Path.Combine(d, "devices.json");
        File.WriteAllText(dev, Case9Devices);
        CliProcess.Result e = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--branch", "1", "--fraction", "0.5");
        Assert.True(e.ExitCode == 0, e.ToString());
        Assert.Contains("protection-eval - ", e.StdOut, StringComparison.Ordinal);
        string[] lines = e.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Contains(lines, l => l.Contains("D45", StringComparison.Ordinal) && l.Contains("87L", StringComparison.Ordinal) && l.Contains("operates in 0 s", StringComparison.Ordinal));   // an internal fault
        Assert.Contains(lines, l => l.Contains("Z45", StringComparison.Ordinal) && l.Contains("zone 2", StringComparison.Ordinal));   // half of line 4-5 is about 17.7 ohm sec: past the 10 ohm zone 1, inside the 20 ohm zone 2
        CliProcess.Result r1 = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--type", "ll");
        CliProcess.Result r2 = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--type", "ll");
        Assert.True(r1.ExitCode == 0, r1.ToString());
        Assert.Equal(r1.StdOut, r2.StdOut);
        Assert.Equal(2, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--type", "abc").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev).ExitCode);

        CliProcess.Result g = CliProcess.RunBuilt("protection-grade", "case9", "--devices", dev, "--types", "3ph");
        Assert.True(g.ExitCode == 3, g.ToString());   // NOTCOORDINATED: G3 fails (zone 2 of Z45 does not cover the far bus)
        Assert.Contains("protection-grade - ", g.StdOut, StringComparison.Ordinal);
        Assert.Contains("G2 Z45", g.StdOut, StringComparison.Ordinal);
        Assert.Contains("G3 Z45", g.StdOut, StringComparison.Ordinal);
        Assert.Contains("G4 Z45", g.StdOut, StringComparison.Ordinal);
    }
}
