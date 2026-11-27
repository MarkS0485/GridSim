using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP17: communication-aided distance schemes (DUTT, PUTT, POTT, DCB), zero-sequence mutual compensation, the load
/// blinder and the G6 check, power-swing blocking on a sampled trace, switch-onto-fault, and a 21 on a two-winding
/// transformer. Sources: Horowitz and Phadke, Power System Relaying, 4th ed., ch. 6 (scheme logic); G. Ziegler, Numerical
/// Distance Protection, 4th ed., ch. 3 (mho, load encroachment, parallel-line compensation); Tziouvaras and Hou, SEL, and
/// the IEEE PSRC WG D6 report (2005) for the two-blinder swing detector and the two-source locus
/// Z(delta) = -Z_S + Z_T (1 - j cot(delta / 2)) / 2. Common data (as WP4): 100 MVA, 132 kV, CT 400/1, VT 132000/110,
/// so 58.08 secondary ohm per pu. With no loads and no line charging the prefault state is exactly 1 at 0 degrees.
/// Rounding bounds: u = 2^-53; a complex add, multiply and divide (Smith's algorithm, as System.Numerics uses) carry
/// relative errors of at most u, sqrt(5) u and 7 u (the constants used in the WP REVIEW-FIX-5 REI bound).
/// </summary>
public sealed class Rc5Wp17DistanceSchemeTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0;
    private const double U = 1.1102230246251565e-16;   // 2^-53
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static Generator Gen(int bus, bool zeroSeq = false) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2)
    {
        ZeroSequenceZPu = zeroSeq ? new MachineZeroSequence(0.0, 0.05) : null,
    };

    private static GridModel Model(Branch[] branches, int buses, Generator[] gens, IReadOnlyList<ZeroSequenceMutual>? mutuals = null, Func<int, Bus>? bus = null) => new()
    {
        Name = "wp17",
        BaseMva = Mva,
        Buses = Enumerable.Range(1, buses).Select(i => bus?.Invoke(i) ?? new Bus(i, i == 1 ? BusType.Slack : gens.Any(g => g.Bus == i) ? BusType.Pv : BusType.Pq, BaseKv: Kv)).ToArray(),
        Generators = gens,
        Branches = branches,
        ZeroSequenceMutuals = mutuals ?? Array.Empty<ZeroSequenceMutual>(),
    };

    private const string CtVt = "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";

    private static string At(int branch, int from, int to, string end) =>
        "\"branch\": { \"index\": " + branch + ", \"from\": " + from + ", \"to\": " + to + ", \"end\": \"" + end + "\" }";

    private static string Zone(double reach, double angle, double t, string extra = "") =>
        "{ \"reachOhms\": " + N(reach) + ", \"angleDeg\": " + N(angle) + ", \"timeS\": " + N(t) + extra + " }";

    private static string Distance(string id, string at, string loops, string[] zones, string extra = "", string ct = CtVt) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", " + at + ", " + ct
        + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"" + loops + "\", \"zones\": [ " + string.Join(", ", zones) + " ]" + extra + " } }";

    private static string Doc(params string[] devices) => "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) =>
        DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(devices))), m, new CurveLibrary());

    private static IReadOnlyList<PlacedRow> Rows(BoundDeviceSet b, FaultSpec f, FaultType t, double xf = 0.0)
    {
        FaultPhasors ph = FaultPhasors.Build(b.Model, f, t, xf);
        Assert.Null(ph.NotEvaluated);
        return PlacedElementEvaluator.Evaluate(b, ph);
    }

    // ---- 1. scheme logic truth table ----------------------------------------------------------------------------------

    /// <summary>
    /// Line A-B, each end with zone 1 at 80 % (0 s), zone 2 at 120 % (0.4 s) and a reverse zone (1.0 s); channel 0.01 s,
    /// DCB coordination 0.02 s. The pickups per position follow from the reaches: a fault 5 % from A is in A's zone 1 and
    /// B's zone 2 (95 %); 90 % from A is in A's zone 2 and B's zone 1 (10 %); beyond B (105 % from A) is in A's zone 2 and
    /// B's reverse zone; behind A is A's reverse zone and B's zone 2 (105 % from B). Plain time = the fastest picked timer.
    /// </summary>
    [Fact]
    public void T1_scheme_truth_table_gives_each_end_its_aided_time_exactly()
    {
        const double inf = double.PositiveInfinity, ch = 0.01, co = 0.02;
        SchemeEnd z1 = new(0.0, true, false, 0.0), z2 = new(inf, true, false, 0.4), rev = new(inf, false, true, 1.0);
        var cases = new (string Name, SchemeEnd A, SchemeEnd B)[]
        {
            ("internal close to A", z1, z2), ("internal at 90 % from A", z2, z1), ("external beyond B", z2, rev), ("external behind A", rev, z2),
        };
        // Expected (A, B) per scheme, in the order of the cases above. DUTT: min(t_Z1, t_Z1,r + ch); PUTT: min(t_Z1,
        // max(p_Z2, t_Z1,r + ch)); POTT: min(t_Z1, max(p_Z2, p_Z2,r + ch)); DCB: min(t_Z1, p_Z2 + co) unless the remote
        // reverse zone picks up. The zone timers apply as backup in every case.
        var expected = new Dictionary<SchemeType, (double A, double B)[]>
        {
            [SchemeType.Dutt] = new[] { (0.0, 0.01), (0.01, 0.0), (0.4, 1.0), (1.0, 0.4) },
            [SchemeType.Putt] = new[] { (0.0, 0.01), (0.01, 0.0), (0.4, 1.0), (1.0, 0.4) },
            [SchemeType.Pott] = new[] { (0.0, 0.01), (0.01, 0.0), (0.4, 1.0), (1.0, 0.4) },
            [SchemeType.Dcb] = new[] { (0.0, 0.02), (0.02, 0.0), (0.4, 1.0), (1.0, 0.4) },
        };
        foreach ((SchemeType type, (double A, double B)[] exp) in expected)
            for (int c = 0; c < cases.Length; c++)
            {
                double a = SchemeLogic.TripS(type, ch, co, cases[c].A, cases[c].B), b = SchemeLogic.TripS(type, ch, co, cases[c].B, cases[c].A);
                Assert.True(a == exp[c].A && b == exp[c].B, $"{type} {cases[c].Name}: A {N(a)} B {N(b)}, expected {N(exp[c].A)} {N(exp[c].B)}");
            }
        // The difference between the permissive schemes: a remote zone 2 alone is enough for POTT, not for PUTT or DUTT.
        Assert.Equal(0.01, SchemeLogic.TripS(SchemeType.Pott, ch, co, z2, z2));
        Assert.Equal(0.4, SchemeLogic.TripS(SchemeType.Putt, ch, co, z2, z2));
        Assert.Equal(0.4, SchemeLogic.TripS(SchemeType.Dutt, ch, co, z2, z2));
        // DUTT trips on the remote zone 1 even with no local pickup (direct transfer trip); PUTT needs its own zone 2.
        SchemeEnd none = new(inf, false, false, inf);
        Assert.Equal(0.01, SchemeLogic.TripS(SchemeType.Dutt, ch, co, none, z1));
        Assert.Equal(inf, SchemeLogic.TripS(SchemeType.Putt, ch, co, none, z1));
    }

    // ---- 2. POTT end-to-end --------------------------------------------------------------------------------------------

    /// <summary>Sources at buses 1 and 3 (X'' = X2 = 0.2, earthed X0 = 0.05); the protected line 1-2 (Z1 = j0.4, Z0 = j1.2),
    /// then line 2-3 (Z1 = j0.05, Z0 = j0.15, so a fault at bus 3 is at j0.45 pu from A: inside zone 2, 1.2 x j0.4). A at bus 1 and B at bus 2 on line 1-2, earth loops, k0 from the line.</summary>
    private static GridModel TwoEnded() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4, X0: 1.2), new Branch(2, 3, R: 0.0, X: 0.05, X0: 0.15) }, 3,
        new[] { Gen(1, zeroSeq: true), Gen(3, zeroSeq: true) });

    private static string[] LineZones(double zl) => new[] { Zone(0.8 * zl, 90.0, 0.0), Zone(1.2 * zl, 90.0, 0.4), Zone(0.5 * zl, 90.0, 1.0, ", \"reverse\": true") };

    [Fact]
    public void T2_pott_trips_both_ends_fast_for_an_slg_at_85_percent()
    {
        // A bolted SLG at m = 0.85 from bus 1. The earth loop at A sees exactly m Z1L = j0.34 pu (V_a = m [Z1 (Ia - I0) +
        // Z0 I0] for a homogeneous line) and at B (1 - m) Z1L = j0.06 pu, whatever the infeeds: A is in zone 2 only (0.8 <
        // 0.85 < 1.2), B in zone 1. A's plain time is zone 2's 0.4 s; with POTT it trips at max(0, 0 + 0.01) = 0.01 s. B trips
        // in zone 1 at 0 s (the spec's "both ends at 0.01 s" overlooks B's own zone 1; its scheme time is also 0.01 s).
        double zl = 0.4 * OhmPerPu;
        string sch(string remote) => ", \"scheme\": { \"type\": \"POTT\", \"remote\": \"" + remote + "\", \"channelS\": 0.01 }";
        BoundDeviceSet b = Bind(TwoEnded(), Distance("A", At(0, 1, 2, "from"), "earth", LineZones(zl), sch("B")), Distance("B", At(0, 1, 2, "to"), "earth", LineZones(zl), sch("A")));
        IReadOnlyList<PlacedRow> rows = Rows(b, FaultSpec.AlongBranch(0, 0.85), FaultType.SingleLineToGround);
        Assert.Equal(2, rows[0].Zone); Assert.Equal(0.4, rows[0].TimeS);
        Assert.Equal(1, rows[1].Zone); Assert.Equal(0.0, rows[1].TimeS);
        Assert.Equal(0b010, rows[0].ZonesPicked);   // 0.85 ZL lies in zone 2 (bit 1) only: not zone 1, not the reverse zone
        IReadOnlyList<SchemeRow?> s = SchemeLogic.Apply(b, rows);
        Assert.True(s[0]!.Evaluated && s[0]!.Aided);
        Assert.Equal(0.01, s[0]!.AidedTripS);
        Assert.Equal(0.4, s[0]!.PlainTimeS);
        Assert.Equal(0.0, s[1]!.AidedTripS);
        Assert.False(s[1]!.Aided);

        // An external fault beyond B (at bus 3): A sees zone 2, B sees it behind (reverse zone), so no permissive is sent
        // and A keeps its zone-2 time.
        IReadOnlyList<PlacedRow> ext = Rows(b, FaultSpec.AtBus(3), FaultType.SingleLineToGround);
        IReadOnlyList<SchemeRow?> se = SchemeLogic.Apply(b, ext);
        Assert.Equal(2, ext[0].Zone);
        Assert.Equal(3, ext[1].Zone);   // B's reverse zone
        Assert.Equal(0.4, se[0]!.AidedTripS);
        Assert.False(se[0]!.Aided);
    }

    [Fact]
    public void T2b_dcb_blocks_on_the_remote_reverse_zone_and_trips_on_coordination_otherwise()
    {
        double zl = 0.4 * OhmPerPu;
        string sch(string remote) => ", \"scheme\": { \"type\": \"DCB\", \"remote\": \"" + remote + "\", \"coordinationS\": 0.02 }";
        BoundDeviceSet b = Bind(TwoEnded(), Distance("A", At(0, 1, 2, "from"), "earth", LineZones(zl), sch("B")), Distance("B", At(0, 1, 2, "to"), "earth", LineZones(zl), sch("A")));
        IReadOnlyList<SchemeRow?> inside = SchemeLogic.Apply(b, Rows(b, FaultSpec.AlongBranch(0, 0.85), FaultType.SingleLineToGround));
        Assert.Equal(0.02, inside[0]!.AidedTripS);   // no block from B (B sees the fault forward)
        IReadOnlyList<SchemeRow?> outside = SchemeLogic.Apply(b, Rows(b, FaultSpec.AtBus(3), FaultType.SingleLineToGround));
        Assert.Equal(0.4, outside[0]!.AidedTripS);   // B's reverse zone sends the block
        // The refusals: a DCB remote without a reverse zone, a remote on another branch.
        Assert.Contains("reverse zone at the remote end", Assert.Throws<DeviceSetException>(() => Bind(TwoEnded(),
            Distance("A", At(0, 1, 2, "from"), "earth", LineZones(zl), sch("B")), Distance("B", At(0, 1, 2, "to"), "earth", LineZones(zl)[..2]))).Message, StringComparison.Ordinal);
        Assert.Contains("other end of branch 0", Assert.Throws<DeviceSetException>(() => Bind(TwoEnded(),
            Distance("A", At(0, 1, 2, "from"), "earth", LineZones(zl), sch("B")), Distance("B", At(1, 2, 3, "from"), "earth", LineZones(zl)))).Message, StringComparison.Ordinal);
    }

    // ---- 3. mutual compensation ----------------------------------------------------------------------------------------

    /// <summary>Two parallel lines 1-2, each Z1 = 0.02 + j0.4, Z0 = 0.06 + j1.2, mutual Z0m = j0.5; earthed sources at both
    /// buses. <paramref name="reversed"/> defines the second line from 2 to 1 (its mutual then carries the opposite sign,
    /// as the data convention signs it for each line's From-to-To direction).</summary>
    private static GridModel Parallel(bool reversed)
    {
        Branch l1 = new(1, 2, R: 0.02, X: 0.4, R0: 0.06, X0: 1.2);
        Branch l2 = reversed ? new(2, 1, R: 0.02, X: 0.4, R0: 0.06, X0: 1.2) : l1;
        var mu = new ZeroSequenceMutual(0, 1, 2, 1, l2.From, l2.To, 0.0, reversed ? -0.5 : 0.5);
        return Model(new[] { l1, l2 }, 2, new[] { Gen(1, zeroSeq: true), Gen(2, zeroSeq: true) }, new[] { mu });
    }

    [Fact]
    public void T3_mutual_compensation_measures_exactly_the_line_and_the_uncompensated_error_is_k0m_3I0m_over_the_loop_current()
    {
        // The split gap: a fault along a mutually coupled line is refused by the line split (FaultAlongLine), not split with
        // a wrong mutual. So the fault is at the remote bus (m = 1), as the spec directs.
        FaultPhasors split = FaultPhasors.Build(Parallel(false), FaultSpec.AlongBranch(0, 0.6), FaultType.SingleLineToGround);
        Assert.Contains("mutual coupling that a split would break", split.NotEvaluated, StringComparison.Ordinal);

        Complex z1l = new(0.02, 0.4);
        foreach (bool reversed in new[] { false, true })
        {
            GridModel m = Parallel(reversed);
            string zones = string.Join(", ", new[] { Zone(0.8 * z1l.Magnitude * OhmPerPu, 87.0, 0.0), Zone(1.2 * z1l.Magnitude * OhmPerPu, 87.0, 0.4) });
            string Dev(string id, string extra) => "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
                + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"earth\", \"zones\": [ " + zones + " ]" + extra + " } }";
            BoundDeviceSet b = Bind(m, Dev("C", ", \"mutual\": { \"branch\": 1 }"), Dev("U", ""));
            FaultPhasors ph = FaultPhasors.Build(m, FaultSpec.AtBus(2), FaultType.SingleLineToGround);
            Assert.Null(ph.NotEvaluated);
            IReadOnlyList<PlacedRow> rows = PlacedElementEvaluator.Evaluate(b, ph);
            Assert.Equal("a", rows[0].Loop);
            Complex zc = rows[0].ApparentImpedanceOhms / OhmPerPu, zu = rows[1].ApparentImpedanceOhms / OhmPerPu;

            // Physics: V_a(1) - V_a(2) = Z1 (Ia - I0) + Z0 I0 + Z0m I0m on line 1 and V_a(2) = 0, so V_a / (Ia + k0 3I0 +
            // k0m 3I0m) = Z1L. Rounding: the sequence voltages come from the network solve; the line identity holds to the
            // rounding of the branch-current forms (the pi stamp for I1, I2; the inverse of the 2 x 2 coupled-group matrix,
            // condition |Z0 + Z0m| / |Z0 - Z0m|, for I0) and of the residual V_a(2). Bound: 64 u kappa (sum of the sequence
            // voltage magnitudes at both ends) / |V_a(1)|, with 64 covering the dozen complex operations at 7 u each.
            Assert.True(ph.TryBus(1, out int b1)); Assert.True(ph.TryBus(2, out int b2));
            double sumV = ph.V0[b1].Magnitude + ph.V1[b1].Magnitude + ph.V2[b1].Magnitude + ph.V0[b2].Magnitude + ph.V1[b2].Magnitude + ph.V2[b2].Magnitude;
            Complex z0 = new(0.06, 1.2), zm = new(0.0, 0.5);
            double kappa = (z0 + zm).Magnitude / (z0 - zm).Magnitude;
            double bound = 64.0 * U * kappa * sumV / ph.PhaseVoltages(b1).A.Magnitude;
            double err = (zc / z1l - 1.0).Magnitude;
            Assert.True(bound < 1e-9, N(bound));
            Assert.True(err <= bound, $"reversed {reversed}: |Zc / Z1L - 1| = {N(err)} above {N(bound)}");

            // The identity: Zu / Zc - 1 = k0m 3I0m / (Ia + k0 3I0), from the same phasors.
            (Complex i0, Complex i1, Complex i2) = ph.Sequence(0, true);
            Complex ia = i0 + i1 + i2;
            Complex k0 = PlacedElementEvaluator.K0(b.Devices[0], m.Branches[0])!.Value;
            Complex comp = k0 * 3.0 * i0;
            Complex e = PlacedElementEvaluator.MutualTerm(b, ph, b.Devices[0]);
            Complex d = ia + comp;
            Complex expected = e / d;
            Assert.True(expected.Magnitude > 0.05, "the mutual term must be a sizeable part of the loop current for this check: " + N(expected.Magnitude));
            // Each side is a few complex operations on the same values (three divisions, two sums, two products): at most
            // 48 u (1 + (|Ia| + |k0 3I0| + |E|) / min(|D|, |D + E|)) absolute, against |e|.
            double idBound = 48.0 * U * (1.0 + (ia.Magnitude + comp.Magnitude + e.Magnitude) / Math.Min(d.Magnitude, (d + e).Magnitude)) / expected.Magnitude;
            double idErr = ((zu / zc - 1.0) - expected).Magnitude / expected.Magnitude;
            Assert.True(idBound <= 1e-12, N(idBound));
            Assert.True(idErr <= idBound, $"reversed {reversed}: identity error {N(idErr)} above {N(idBound)}");
            // The data k0m: s Z0m / (3 Z1L) with s = +1 for the same orientation, -1 reversed; both give j0.5 / (3 Z1L).
            Complex k0m = PlacedElementEvaluator.K0m(b, b.Devices[0])!.Value;
            Assert.True((k0m - zm / (3.0 * z1l)).Magnitude <= 8.0 * U * k0m.Magnitude, N(k0m.Real) + " " + N(k0m.Imaginary));
        }
    }

    [Fact]
    public void T3b_mutual_settings_are_checked()
    {
        GridModel m = Parallel(false);
        string zones = Zone(10, 87, 0);
        string Dev(string mutual, string loops = "earth") => "{ \"id\": \"C\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"" + loops + "\", \"zones\": [ " + zones + " ], \"mutual\": " + mutual + " } }";
        Assert.Contains("is the protected branch itself", Assert.Throws<DeviceSetException>(() => Bind(m, Dev("{ \"branch\": 0 }"))).Message, StringComparison.Ordinal);
        Assert.Contains("earth loops", Assert.Throws<DeviceSetException>(() => Bind(m, Dev("{ \"branch\": 1 }", "phase"))).Message, StringComparison.Ordinal);
        GridModel noData = m.CopyWith(zeroSequenceMutuals: Array.Empty<ZeroSequenceMutual>());
        Assert.Contains("no zero-sequence mutual coupling", Assert.Throws<DeviceSetException>(() => Bind(noData, Dev("{ \"branch\": 1 }"))).Message, StringComparison.Ordinal);
        Bind(noData, Dev("{ \"branch\": 1, \"k0m\": { \"magnitude\": 0.4, \"angleDeg\": 3 } }"));   // an explicit k0m needs no data
        GridModel three = Model(new[] { new Branch(1, 2, R: 0.02, X: 0.4), new Branch(2, 3, R: 0.02, X: 0.4) }, 3, new[] { Gen(1) });
        Assert.Contains("does not end at the protected line's buses", Assert.Throws<DeviceSetException>(() => Bind(three,
            Dev("{ \"branch\": 1, \"k0m\": { \"magnitude\": 0.4, \"angleDeg\": 3 } }"))).Message, StringComparison.Ordinal);
    }

    // ---- 4. load encroachment and G6 -----------------------------------------------------------------------------------

    [Fact]
    public void T4_load_point_against_the_mho_and_the_blinder()
    {
        // Zone 3 mho, reach 120 ohm at 75 deg. A point at angle phi lies inside when |Z| <= 120 cos(75 - phi).
        Complex reach = Complex.FromPolarCoordinates(120.0, 75.0 * Math.PI / 180.0);
        Complex p1 = Complex.FromPolarCoordinates(100.0, 20.0 * Math.PI / 180.0), p2 = Complex.FromPolarCoordinates(60.0, 30.0 * Math.PI / 180.0);
        Assert.Equal(68.82917236212552, 120.0 * Math.Cos(55.0 * Math.PI / 180.0), 12);   // < 100: outside
        Assert.Equal(84.8528137423857, 120.0 * Math.Cos(45.0 * Math.PI / 180.0), 12);   // > 60: inside
        Assert.False(DistanceCharacteristics.InMho(p1, reach));
        Assert.True(DistanceCharacteristics.InMho(p2, reach));
        // The blinder (minLoadOhms 50, +-35 deg): 60 at 30 deg is in the load region, so a phase loop there is blocked. The
        // margins (60 vs 50 ohm, 30 vs 35 deg) are far above any rounding of the polar construction.
        var le = new LoadEncroachmentSetting(35.0, 35.0, 50.0);
        Assert.True(LoadBlinder.Blocks(p2, le));
        Assert.True(LoadBlinder.Blocks(p1, le));
        Assert.False(LoadBlinder.Blocks(Complex.FromPolarCoordinates(40.0, 30.0 * Math.PI / 180.0), le));    // below minLoadOhms
        Assert.False(LoadBlinder.Blocks(Complex.FromPolarCoordinates(60.0, 40.0 * Math.PI / 180.0), le));    // outside the angle
        Assert.True(LoadBlinder.Blocks(Complex.FromPolarCoordinates(60.0, 150.0 * Math.PI / 180.0), le));    // reverse load
        Assert.True(LoadBlinder.Blocks(Complex.FromPolarCoordinates(60.0, -170.0 * Math.PI / 180.0), le));   // reverse load
        Assert.False(LoadBlinder.Blocks(Complex.FromPolarCoordinates(60.0, 120.0 * Math.PI / 180.0), le));
    }

    /// <summary>A source at bus 1, a short line 1-2 (j0.01) and a load at bus 2 sized so the relay at bus 1 sees about
    /// 60 ohm sec at 30 deg (S = |V|^2 / Z* with Z = 60 / 58.08 pu at 30 deg: 83.8 MW, 48.4 Mvar), or about 100 ohm at 20 deg
    /// (54.6 MW, 19.9 Mvar).</summary>
    private static GridModel Loaded(double pMw, double qMvar) => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.01) }, 2, new[] { Gen(1) },
        bus: i => i == 1 ? new Bus(1, BusType.Slack, BaseKv: Kv) : new Bus(2, BusType.Pq, Pd: pMw, Qd: qMvar, BaseKv: Kv));

    private static string Z3(string extra) => Distance("R", At(0, 1, 2, "from"), "phase", new[] { Zone(120.0, 75.0, 1.0) }, extra);

    [Fact]
    public void T4b_the_blinder_blocks_phase_loops_at_the_load_point_and_G6_reports_the_reach()
    {
        const string blinder = ", \"loadEncroachment\": { \"fwdAngleDeg\": 35, \"revAngleDeg\": 35, \"minLoadOhms\": 50 }";
        GridModel heavy = Loaded(83.8, 48.4);
        // The prefault load point, and where it lies: the line drop and V2 < 1 move it from 60 at 30 deg by a few per cent;
        // it stays far inside both the zone (boundary about 85 ohm) and the blinder (50 ohm, 35 deg).
        Complex zp = LoadBlinder.PrefaultImpedance(heavy, FaultPhasors.DefaultPrefault(heavy), Bind(heavy, Z3(blinder)).Devices[0])!.Value;
        Assert.InRange(zp.Magnitude, 55.0, 65.0);
        Assert.InRange(Math.Atan2(zp.Imaginary, zp.Real) * 180.0 / Math.PI, 28.0, 33.0);

        // A three-phase fault through xf = 100 pu at bus 2 leaves the relay looking at the load (Zf in parallel with a
        // 1 pu load moves Z by about 1 %): without the blinder zone 3 operates; with it every phase loop is blocked.
        PlacedRow plain = Rows(Bind(heavy, Z3("")), FaultSpec.AtBus(2), FaultType.ThreePhase, 100.0)[0];
        Assert.Equal(1, plain.Zone);   // the device's only zone (the "zone 3" setting) is its zone 1
        Assert.True(plain.Operates);
        PlacedRow blocked = Rows(Bind(heavy, Z3(blinder)), FaultSpec.AtBus(2), FaultType.ThreePhase, 100.0)[0];
        Assert.False(blocked.Operates);
        Assert.Equal(3, blocked.LoadBlockedLoops);
        Assert.Contains("load blinder", blocked.Reason, StringComparison.Ordinal);
        // A bolted fault at bus 2 is far from the load region (|Z| = 0.58 ohm): nothing is blocked, the zone operates.
        PlacedRow bolted = Rows(Bind(heavy, Z3(blinder)), FaultSpec.AtBus(2), FaultType.ThreePhase)[0];
        Assert.True(bolted.Operates);
        Assert.Equal(0, bolted.LoadBlockedLoops);

        // G6: in the zone and in the blinder's region: passes (the blinder blocks it). With minLoadOhms 70 the point is
        // outside the blinder's region: fails. The light load (100 ohm at 20 deg) lies in no zone: passes.
        PlacedCheck G6(GridModel m, string extra) => PlacedCoordination.Run(Bind(m, Z3(extra)), new[] { FaultSpec.AtBus(2) }, new[] { FaultType.ThreePhase }).Checks.Single(c => c.Check == "G6");
        PlacedCheck a = G6(heavy, blinder);
        Assert.True(a.Passed); Assert.Equal(1.0, a.Value);
        Assert.Contains("inside the load blinder's region", a.Detail, StringComparison.Ordinal);
        PlacedCheck b = G6(heavy, blinder.Replace("\"minLoadOhms\": 50", "\"minLoadOhms\": 70", StringComparison.Ordinal));
        Assert.False(b.Passed);
        Assert.Contains("reaches the load", b.Detail, StringComparison.Ordinal);
        PlacedCheck c = G6(Loaded(54.6, 19.9), blinder);
        Assert.True(c.Passed); Assert.Equal(0.0, c.Value);
        // Without a blinder there is no G6 (the check is opt-in with the setting).
        Assert.DoesNotContain(PlacedCoordination.Run(Bind(heavy, Z3("")), new[] { FaultSpec.AtBus(2) }, new[] { FaultType.ThreePhase }).Checks, x => x.Check == "G6");
    }

    // ---- 5. power-swing blocking ---------------------------------------------------------------------------------------

    private static readonly PowerSwingSetting Psb = new(0.3, 0.2, 1.0, 0.8, 0.03, new[] { 1, 2 }, 2.0);
    private const double H = 1e-4;

    /// <summary>The synthetic two-source locus Z_app = 0.5 cot(delta / 2) + j0.2 ohm (Z_T = j1, Z_S = j0.3), with delta =
    /// 2 pi f t + 90 deg, sampled every 0.1 ms for 1 s.</summary>
    private static (double[] T, Complex[] Z) Trace(double f)
    {
        int n = 10001;
        var t = new double[n];
        var z = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            t[k] = k * H;
            double half = Math.PI * f * t[k] + Math.PI / 4.0;
            z[k] = new Complex(0.5 / Math.Tan(half), 0.2);
        }
        return (t, z);
    }

    /// <summary>The time R = 0.5 cot(delta / 2) falls to <paramref name="r"/> in slip cycle <paramref name="cycle"/>:
    /// delta = 2 atan(0.5 / r) (in (0, 2 pi)), so t = (delta - pi / 2) / (2 pi f) + cycle / f.</summary>
    private static double Crossing(double r, double f, int cycle)
    {
        double delta = 2.0 * Math.Atan2(0.5, r);
        return (delta - Math.PI / 2.0) / (2.0 * Math.PI * f) + cycle / f;
    }

    /// <summary>The first sample at or after <paramref name="t"/>, after checking that the crossing is not within 1e-3 of a
    /// step of a sample (so the rounding of cot, about 1e-16 relative, cannot move the sample).</summary>
    private static int FirstAfter(double t)
    {
        double s = t / H;
        Assert.True(Math.Abs(s - Math.Round(s)) > 1e-3, "crossing too close to a sample: " + N(s));
        return (int)Math.Ceiling(s);
    }

    [Fact]
    public void T5_power_swing_at_1_hz_is_a_swing_and_blocks_the_zones_on_every_sample_between_detection_and_exit()
    {
        // delta_o = 2 atan(0.5 / 0.3) = 118.07248693585296 deg, delta_i = 2 atan(0.5 / 0.2) = 136.39718102729637 deg.
        Assert.Equal(118.07248693585296, 2.0 * Math.Atan(0.5 / 0.3) * 180.0 / Math.PI, 12);
        Assert.Equal(136.39718102729637, 2.0 * Math.Atan(0.5 / 0.2) * 180.0 / Math.PI, 12);
        double to = Crossing(0.3, 1.0, 0), ti = Crossing(0.2, 1.0, 0), tl = Crossing(-0.3, 1.0, 0);
        Assert.Equal(0.05090192803178724, ti - to, 12);   // > deltaT = 0.03: a swing
        (double[] t, Complex[] z) = Trace(1.0);
        SwingResult r = SwingBlocking.Evaluate(t, z, Psb);
        SwingPass p = Assert.Single(r.Passes);   // the next entry would be at t_o + 1 s, past the trace
        int ko = FirstAfter(to), ki = FirstAfter(ti), kl = FirstAfter(tl);
        Assert.Equal(t[ko], p.OuterEntryS);
        Assert.Equal(t[ki], p.InnerEntryS);
        Assert.True(p.OuterEntryS - to is >= 0.0 and < H);
        Assert.True(Math.Abs(p.InnerEntryS!.Value - p.OuterEntryS - (ti - to)) < H);
        Assert.True(p.IsSwing);
        // Blocked from the first sample at deltaT or more after the entry sample (the detector's rule) to the first sample
        // outside the outer blinder (R < -0.3 at delta = 360 - 118.07 deg).
        int kb = ko;
        while (t[kb] - t[ko] < Psb.DeltaTS) kb++;
        Assert.Equal(t[kb], p.BlockFromS);
        Assert.Equal(t[kl], p.BlockToS);
        Assert.Equal("left the outer blinder", p.Release);
        for (int k = 0; k < t.Length; k++)
            Assert.True(r.Blocked[k] == (k >= kb && k < kl), $"sample {k} (t = {N(t[k])}): blocked {r.Blocked[k]}");

        // The same swing with unblockS = 0.1 s: released at the first sample 0.1 s or more after the block began, and not
        // re-armed while Z stays inside the outer blinder.
        SwingResult u = SwingBlocking.Evaluate(t, z, Psb with { UnblockS = 0.1 });
        SwingPass pu = Assert.Single(u.Passes);
        int ku = kb;
        while (t[ku] - t[kb] < 0.1) ku++;
        Assert.Equal(t[ku], pu.BlockToS);
        Assert.Equal("unblock timer", pu.Release);
        for (int k = 0; k < t.Length; k++)
            Assert.True(u.Blocked[k] == (k >= kb && k < ku), $"unblock: sample {k}: blocked {u.Blocked[k]}");
    }

    [Fact]
    public void T5b_power_swing_at_3_hz_is_a_fault_on_every_pass_and_nothing_is_blocked()
    {
        double to = Crossing(0.3, 3.0, 0), ti = Crossing(0.2, 3.0, 0);
        Assert.Equal(0.01696730934392908, ti - to, 12);   // < deltaT = 0.03: not a swing
        (double[] t, Complex[] z) = Trace(3.0);
        SwingResult r = SwingBlocking.Evaluate(t, z, Psb);
        Assert.Equal(3, r.Passes.Count);   // entries at t_o, t_o + 1/3, t_o + 2/3; t_o + 1 is past the trace
        for (int c = 0; c < 3; c++)
        {
            SwingPass p = r.Passes[c];
            Assert.False(p.IsSwing);
            Assert.Equal(t[FirstAfter(Crossing(0.3, 3.0, c))], p.OuterEntryS);
            Assert.Equal(t[FirstAfter(Crossing(0.2, 3.0, c))], p.InnerEntryS);
            Assert.Equal("fault (reached the inner blinder within deltaT)", p.Release);
        }
        Assert.All(r.Blocked, b => Assert.False(b));
    }

    // ---- 6. switch-onto-fault ------------------------------------------------------------------------------------------

    /// <summary>WP4's T1: a source X'' = 0.2 behind line 1-2, Z1 = 0.04 + j0.4.</summary>
    private static GridModel Radial() => Model(new[] { new Branch(1, 2, R: 0.04, X: 0.4) }, 2, new[] { Gen(1) });

    [Fact]
    public void T6_switch_onto_fault_trips_at_zero_on_zone_2_or_on_current_and_only_on_energisation()
    {
        // A three-phase fault at 90 %: Zapp = 0.9 ZL, past zone 1 (80 %), inside zone 2 (120 %, 0.4 s).
        Complex zl = new Complex(0.04, 0.4) * OhmPerPu;
        double th = Math.Atan2(0.4, 0.04) * 180.0 / Math.PI;
        string[] zones = { Zone(0.8 * zl.Magnitude, th, 0.0), Zone(1.2 * zl.Magnitude, th, 0.4) };
        FaultSpec at90 = FaultSpec.AlongBranch(0, 0.9), closed = at90 with { Energisation = true };
        BoundDeviceSet z2 = Bind(Radial(), Distance("S", At(0, 1, 2, "from"), "phase", zones, ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"Z2\" }"));
        PlacedRow normal = Rows(z2, at90, FaultType.ThreePhase)[0], sotf = Rows(z2, closed, FaultType.ThreePhase)[0];
        Assert.Equal(0.4, normal.TimeS); Assert.False(normal.SwitchOntoFault);
        Assert.Equal(0.0, sotf.TimeS); Assert.True(sotf.SwitchOntoFault);
        Assert.Equal(2, sotf.Zone);
        Assert.Equal("switch-onto-fault", sotf.Reason);
        // Without the setting the energisation flag changes nothing.
        BoundDeviceSet none = Bind(Radial(), Distance("S", At(0, 1, 2, "from"), "phase", zones));
        Assert.Equal(0.4, Rows(none, closed, FaultType.ThreePhase)[0].TimeS);
        // Nondirectional: |I| = 1 / |j0.2 + 0.9 (0.04 + j0.4)| = 1 / |0.036 + j0.56| = 1.7820 pu = 779.4 A, above a 400 A CT
        // primary (trips) and below a 1000 A one (does not). The 1000/1 CT changes the secondary ohms, so the zones scale
        // by 1000 / 400.
        double amps = 1.0 / new Complex(0.036, 0.56).Magnitude * (Mva / (Math.Sqrt(3.0) * Kv) * 1000.0);
        Assert.InRange(amps, 779.0, 780.0);
        BoundDeviceSet nd = Bind(Radial(), Distance("S", At(0, 1, 2, "from"), "phase", zones, ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"nondirectional\" }"));
        Assert.Equal(0.0, Rows(nd, closed, FaultType.ThreePhase)[0].TimeS);
        const string ct1000 = "\"ct\": { \"primaryA\": 1000, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";
        string[] zones1000 = { Zone(2.5 * 0.8 * zl.Magnitude, th, 0.0), Zone(2.5 * 1.2 * zl.Magnitude, th, 0.4) };
        BoundDeviceSet nd1000 = Bind(Radial(), Distance("S", At(0, 1, 2, "from"), "phase", zones1000, ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"nondirectional\" }", ct1000));
        PlacedRow low = Rows(nd1000, closed, FaultType.ThreePhase)[0];
        Assert.Equal(0.4, low.TimeS); Assert.False(low.SwitchOntoFault);
    }

    // ---- 7. 21 on a transformer ----------------------------------------------------------------------------------------

    /// <summary>A 132/33 kV two-winding transformer 1-2, Z_T = 0.005 + j0.1 pu, fed from bus 1.</summary>
    private static GridModel Transformer(double tap, double shift, WindingConnection lv) =>
        Model(new[] { new Branch(1, 2, R: 0.005, X: 0.1, TapRatio: tap, PhaseShiftDeg: shift, IsTransformer: true, FromWinding: WindingConnection.WyeGrounded, ToWinding: lv) }, 2,
            new[] { Gen(1, zeroSeq: true) }, bus: i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: i == 1 ? Kv : 33.0));

    [Fact]
    public void T7_distance_through_a_transformer_measures_its_series_impedance_on_the_measuring_side()
    {
        // A bolted three-phase fault at the LV terminals: V_LV = 0, so the HV relay measures V / I = 1 / Yff = tau^2 Z_T
        // (the tap is on the From side; the phase shift does not enter Yff). Rounding: the pi stamp (reciprocal, 7 u), the
        // phase forms from the sequences and the loop differences and division (a few u each) give at most 32 u, plus the
        // residual LV voltage of the fault state through Yft / Yff = 1 / tau: 2 |V_LV| / |V_HV|.
        Complex zt = new(0.005, 0.1);
        foreach ((double tap, double shift, WindingConnection lv) in new[] { (1.0, 0.0, WindingConnection.WyeGrounded), (1.05, 0.0, WindingConnection.WyeGrounded), (1.0, 30.0, WindingConnection.Delta) })
        {
            GridModel m = Transformer(tap, shift, lv);
            BoundDeviceSet b = Bind(m, Distance("T", At(0, 1, 2, "from"), "phase", new[] { Zone(10.0, 87.0, 0.0) }, ", \"onTransformer\": true"));
            FaultPhasors ph = FaultPhasors.Build(m, FaultSpec.AtBus(2), FaultType.ThreePhase);
            Assert.Null(ph.NotEvaluated);
            PlacedRow r = PlacedElementEvaluator.Evaluate(b, ph)[0];
            Assert.True(ph.TryBus(1, out int hv)); Assert.True(ph.TryBus(2, out int lvb));
            double bound = 32.0 * U + 2.0 * ph.V1[lvb].Magnitude / ph.V1[hv].Magnitude;
            Assert.True(bound < 1e-12, N(bound));
            Complex expected = tap * tap * zt * OhmPerPu;
            double err = (r.ApparentImpedanceOhms / expected - 1.0).Magnitude;
            Assert.True(err <= bound, $"tap {N(tap)} shift {N(shift)}: {N(err)} above {N(bound)}");
            Assert.Equal(1, r.Zone);
        }
    }

    [Fact]
    public void T7b_transformer_refusals_follow_the_vector_group()
    {
        string Dev(string loops, string extra = "") => "{ \"id\": \"T\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
            + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"" + loops + "\", \"zones\": [ " + Zone(10, 87, 0) + " ], \"onTransformer\": true" + extra + " } }";
        // Without the opt-in member a 21 on a transformer is refused word for word as before WP17; with it on a line, refused.
        Assert.Equal("Device 'T': a 21 on a transformer needs vector-group compensation; out of scope.", Assert.Throws<DeviceSetException>(() =>
            Bind(Transformer(1.0, 0.0, WindingConnection.WyeGrounded), Distance("T", At(0, 1, 2, "from"), "phase", new[] { Zone(10, 87, 0) }))).Message);
        Assert.Contains("is not a transformer", Assert.Throws<DeviceSetException>(() => Bind(Radial(), Dev("phase"))).Message, StringComparison.Ordinal);
        // Yd1: earth loops refused, phase loops bound.
        GridModel yd1 = Transformer(1.0, 30.0, WindingConnection.Delta);
        Assert.Contains("phase-shifting transformer", Assert.Throws<DeviceSetException>(() => Bind(yd1, Dev("earth", ", \"k0\": { \"magnitude\": 0, \"angleDeg\": 0 }"))).Message, StringComparison.Ordinal);
        Assert.Contains("phase-shifting transformer", Assert.Throws<DeviceSetException>(() => Bind(yd1, Dev("both", ", \"k0\": { \"magnitude\": 0, \"angleDeg\": 0 }"))).Message, StringComparison.Ordinal);
        Bind(yd1, Dev("phase"));
        // A shift that is not a whole multiple of 30 degrees: refused outright.
        Assert.Contains("multiple of 30", Assert.Throws<DeviceSetException>(() => Bind(Transformer(1.0, 20.0, WindingConnection.WyeGrounded), Dev("phase"))).Message, StringComparison.Ordinal);
        // YNyn0: earth loops need an explicit k0.
        GridModel ynyn0 = Transformer(1.0, 0.0, WindingConnection.WyeGrounded);
        Assert.Contains("explicit k0", Assert.Throws<DeviceSetException>(() => Bind(ynyn0, Dev("both"))).Message, StringComparison.Ordinal);
        Bind(ynyn0, Dev("both", ", \"k0\": { \"magnitude\": 0, \"angleDeg\": 0 }"));
        // An 87 on a transformer is still refused, word for word.
        string diff = "{ \"id\": \"L1\", \"type\": \"relay\", \"function\": \"87\", " + At(0, 1, 2, "from") + ", \"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, "
            + "\"differential\": { \"is1A\": 0.2, \"k1\": 0.3, \"is2A\": 2.0, \"k2\": 1.5 } }";
        Assert.Equal("Device 'L1': a 87 on a transformer needs vector-group compensation; out of scope.", Assert.Throws<DeviceSetException>(() => Bind(ynyn0, diff)).Message);
    }

    // ---- settings reader -----------------------------------------------------------------------------------------------

    [Fact]
    public void New_members_are_read_and_bad_values_refused_naming_the_path()
    {
        double zl = 0.4 * OhmPerPu;
        string full = Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl),
            ", \"scheme\": { \"type\": \"PUTT\", \"remote\": \"B\", \"channelS\": 0.012 }"
            + ", \"powerSwing\": { \"outerR\": 30, \"innerR\": 20, \"outerX\": 40, \"innerX\": 30, \"deltaTS\": 0.03, \"blockZones\": [1, 2] }"
            + ", \"loadEncroachment\": { \"fwdAngleDeg\": 30, \"revAngleDeg\": 25, \"minLoadOhms\": 60 }"
            + ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"nondirectional\" }");
        BoundDeviceSet b = Bind(TwoEnded(), full, Distance("B", At(0, 1, 2, "to"), "both", LineZones(zl)));
        DistanceSetting s = b.Devices[0].Distance!;
        Assert.Equal(new SchemeSetting(SchemeType.Putt, "B", 0.012, 0.0), s.Scheme);
        Assert.Equal(2.0, s.PowerSwing!.UnblockS);   // the default
        Assert.Equal(new[] { 1, 2 }, s.PowerSwing.BlockZones);
        Assert.Equal(new LoadEncroachmentSetting(30, 25, 60), s.LoadEncroachment);
        Assert.Equal(new SotfSetting(0.5, SotfMode.Nondirectional), s.Sotf);
        Assert.True(s.Zones[2].Reverse);
        Assert.False(s.Zones[1].Reverse);
        // Without the members every new property is null and no zone is reverse.
        DistanceSetting plain = b.Devices[1].Distance!;
        Assert.Null(plain.Scheme); Assert.Null(plain.PowerSwing); Assert.Null(plain.LoadEncroachment); Assert.Null(plain.Sotf); Assert.Null(plain.Mutual);

        static void Refused(string doc, string text) =>
            Assert.Contains(text, Assert.Throws<DeviceSetException>(() => DeviceSet.Parse(Encoding.UTF8.GetBytes(doc))).Message, StringComparison.Ordinal);
        Refused(Doc(Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"scheme\": { \"type\": \"XYZ\", \"remote\": \"B\" }")), ".distance.scheme.type must be DUTT, PUTT, POTT or DCB");
        Refused(Doc(Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"sotf\": { \"windowS\": 0.5, \"mode\": \"fast\" }")), ".distance.sotf.mode must be Z2 or nondirectional");
        Refused(Doc(Distance("A", At(0, 1, 2, "from"), "both", new[] { Zone(5, 90, 0, ", \"reverse\": 1") })), ".zones[0].reverse must be true or false");
        Refused(Doc(Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"scheme\": { \"type\": \"POTT\", \"remote\": \"B\", \"extra\": 1 }")), "extra");

        static void Unbound(GridModel m, string text, params string[] devs) =>
            Assert.Contains(text, Assert.Throws<DeviceSetException>(() => Bind(m, devs)).Message, StringComparison.Ordinal);
        Unbound(TwoEnded(), "reverse zones follow the forward zones", Distance("A", At(0, 1, 2, "from"), "both", new[] { Zone(5, 90, 1, ", \"reverse\": true"), Zone(6, 90, 0) }));
        Unbound(TwoEnded(), "0 < innerR < outerR", Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl),
            ", \"powerSwing\": { \"outerR\": 20, \"innerR\": 20, \"outerX\": 40, \"innerX\": 30, \"deltaTS\": 0.03, \"blockZones\": [1] }"));
        Unbound(TwoEnded(), "names zone 4", Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl),
            ", \"powerSwing\": { \"outerR\": 30, \"innerR\": 20, \"outerX\": 40, \"innerX\": 30, \"deltaTS\": 0.03, \"blockZones\": [4] }"));
        Unbound(TwoEnded(), "is not in the device file", Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"scheme\": { \"type\": \"DUTT\", \"remote\": \"Q\" }"));
        Unbound(TwoEnded(), "[0, 90) degrees", Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"loadEncroachment\": { \"fwdAngleDeg\": 95, \"revAngleDeg\": 30, \"minLoadOhms\": 50 }"));
        Unbound(TwoEnded(), "windowS must be finite and > 0", Distance("A", At(0, 1, 2, "from"), "both", LineZones(zl), ", \"sotf\": { \"windowS\": 0, \"mode\": \"Z2\" }"));
    }

    // ---- CLI -----------------------------------------------------------------------------------------------------------

    /// <summary>case9 line 4-5 (branch 1): CT 1000/1, VT 345000/110, so 379.5 ohm sec per pu and |ZL| = |0.017 + j0.092| pu
    /// = 35.5 ohm sec. Zones at 80 deg: 28 ohm (0 s), 43 ohm (0.4 s), and a 15 ohm reverse zone (1.0 s).</summary>
    private static string Case9Relay(string id, string end, string extra) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", \"branch\": { \"index\": 1, \"from\": 4, \"to\": 5, \"end\": \"" + end + "\" }, "
        + "\"ct\": { \"primaryA\": 1000, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 345000, \"secondaryV\": 110 }, "
        + "\"distance\": { \"characteristic\": \"mho\", \"loops\": \"phase\", \"zones\": [ { \"reachOhms\": 28, \"angleDeg\": 80, \"timeS\": 0 }, "
        + "{ \"reachOhms\": 43, \"angleDeg\": 80, \"timeS\": 0.4 }, { \"reachOhms\": 15, \"angleDeg\": 80, \"timeS\": 1.0, \"reverse\": true } ]" + extra + " } }";

    private static string TempDir()
    {
        string d = TestTemp.Under("gridsim-wp17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Cli_protection_eval_prints_the_scheme_and_adds_its_columns_only_when_a_scheme_is_set()
    {
        string d = TempDir();
        string dev = Path.Combine(d, "dev.json"), json = Path.Combine(d, "r.json");
        string sch(string remote) => ", \"scheme\": { \"type\": \"POTT\", \"remote\": \"" + remote + "\", \"channelS\": 0.01 }, \"sotf\": { \"windowS\": 0.5, \"mode\": \"Z2\" }";
        File.WriteAllText(dev, Doc(Case9Relay("ZA", "from", sch("ZB")), Case9Relay("ZB", "to", sch("ZA"))));
        // A three-phase fault at 0.85 of line 4-5: ZA sees about 0.85 x 35.5 = 30 ohm (zone 2), ZB about 5 ohm (zone 1).
        CliProcess.Result r = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--branch", "1", "--fraction", "0.85", "--json", json);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("scheme ZA POTT with ZB: trips in 0.01 s (aided; zones alone 0.4 s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("scheme ZB POTT with ZA: trips in 0 s (zone timers)", r.StdOut, StringComparison.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(json)))
        {
            JsonElement row = doc.RootElement.GetProperty("tables").GetProperty("devices")[0];
            Assert.Equal("POTT", row.GetProperty("scheme").GetString());
            Assert.True(row.GetProperty("aided").GetBoolean());
            Assert.Equal(0.01, row.GetProperty("aided_trip_s").GetDouble());
            Assert.Equal(0.4, row.GetProperty("time_s").GetDouble());
        }
        // --energised true: switch-onto-fault Z2 trips ZA at 0 s.
        CliProcess.Result e = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--branch", "1", "--fraction", "0.85", "--energised", "true");
        Assert.True(e.ExitCode == 0, e.ToString());
        Assert.Contains(e.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), l => l.Contains("ZA", StringComparison.Ordinal) && l.Contains("operates in 0 s", StringComparison.Ordinal) && l.Contains("switch-onto-fault", StringComparison.Ordinal));
        Assert.Equal(2, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--energised", "yes").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "5", "--swing", Path.Combine(d, "t.csv")).ExitCode);
    }

    [Fact]
    public void Cli_swing_mode_matches_the_core_detector_on_the_same_trace()
    {
        // Buses 4 and 5 held at 1 pu with bus 5 slipping at 1 Hz behind bus 4 (delta = 2 pi t + 90 deg), sampled every
        // 1 ms for 1.2 s. The relay ZA at bus 4 sees the two-source locus of line 4-5 (with its charging).
        var sb = new StringBuilder("# synthetic swing\nt_s,vm_4,va_4,vm_5,va_5\n");
        for (int k = 0; k <= 1200; k++)
        {
            double t = k * 1e-3;
            sb.Append(N(t)).Append(",1,0,1,").Append(N(-(360.0 * t + 90.0))).Append('\n');
        }
        string d = TempDir();
        string trace = Path.Combine(d, "swing.csv"), dev = Path.Combine(d, "dev.json"), json = Path.Combine(d, "s.json");
        File.WriteAllText(trace, sb.ToString());
        const string psb = ", \"powerSwing\": { \"outerR\": 15, \"innerR\": 8, \"outerX\": 40, \"innerX\": 30, \"deltaTS\": 0.01, \"blockZones\": [1, 2], \"unblockS\": 2 }";
        File.WriteAllText(dev, Doc(Case9Relay("ZA", "from", psb), Case9Relay("ZB", "to", "")));
        CliProcess.Result r = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--swing", trace, "--json", json);
        Assert.True(r.ExitCode == 0, r.ToString());

        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        BoundDeviceSet b = DeviceSetValidator.Bind(DeviceSet.Parse(File.ReadAllBytes(dev)), m, new CurveLibrary());
        SwingTrace st = SwingTrace.Parse(sb.ToString());
        SwingResult api = SwingBlocking.Evaluate(st.TimeS, st.Impedance(m, b.Devices[0])!, b.Devices[0].Distance!.PowerSwing!);
        Assert.NotEmpty(api.Passes);
        Assert.Contains(api.Passes, p => p.IsSwing);   // 1 Hz: the locus takes far longer than 10 ms between the blinders
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(json));
        JsonElement rows = doc.RootElement.GetProperty("tables").GetProperty("swing");
        Assert.Equal(api.Passes.Count, rows.GetArrayLength());
        for (int k = 0; k < api.Passes.Count; k++)
        {
            JsonElement row = rows[k];
            Assert.Equal(api.Passes[k].OuterEntryS, row.GetProperty("outer_entry_s").GetDouble());
            Assert.Equal(api.Passes[k].IsSwing, row.GetProperty("swing").GetBoolean());
            Assert.Equal(api.Passes[k].BlockFromS, row.GetProperty("block_from_s").ValueKind == JsonValueKind.Null ? null : row.GetProperty("block_from_s").GetDouble());
            Assert.Equal(api.Passes[k].Release, row.GetProperty("release").GetString());
        }
        Assert.Contains("SWING, zones 1 2 blocked from", r.StdOut, StringComparison.Ordinal);
        // A trace without bus 5 cannot be evaluated for ZA; the run says so and still completes.
        string bad = Path.Combine(d, "bad.csv");
        File.WriteAllText(bad, "t_s,vm_4,va_4\n0,1,0\n0.001,1,0\n");
        CliProcess.Result nb = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--swing", bad);
        Assert.True(nb.ExitCode == 0, nb.ToString());
        Assert.Contains("not evaluated: the trace has no voltage for bus 4 or 5", nb.StdOut, StringComparison.Ordinal);
        // A malformed trace is an input error.
        File.WriteAllText(bad, "t_s,vm_4\n0,1\n");
        Assert.Equal(1, CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--swing", bad).ExitCode);
    }
}
