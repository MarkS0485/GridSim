using System.Numerics;
using System.Text;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP18: the transformer (87T) and busbar (87B) differentials. Sources: the compensation matrices are the standard
/// numerical-relay ones (SEL CTC(1) and CTC(11); an SEL-387A Dyn1 example sets CTC(1) on the wye winding); the bias
/// definitions follow Horowitz and Phadke, Power System Relaying, 4th ed., ch. 8 and 9; 87B is Kirchhoff's current law,
/// which is exact. Each expected value below is derived in its comment.
/// <para>Rounding bounds. u = 2^-53. A branch-end current I = Yxx Vx + Yxy Vy (two complex products, one add) carries an
/// absolute error of at most (sqrt 5 + 1) u (|Yxx||Vx| + |Yxy||Vy|) &lt; 4 u T, T = |Yxx||Vx| + |Yxy||Vy|. The phase
/// currents from the sequence currents (one product by a or a^2 and two adds per phase) add at most (sqrt 5 + 2) u of the
/// sequence magnitudes, so a phase current is within 9 u S_end of exact, S_end = sum over the three sequences of T. The
/// scaling to pu of the rating (I_A / I_rated: about ten roundings in IBaseA, I_rated and the two divisions) adds at most
/// 10 u of the value; a compensation matrix (two terms, a subtraction and the 1/sqrt 3 product) adds at most 4 u of twice
/// the largest input; the final add and magnitude 2 u. Summing for both sides and allowing the matrix's doubling, the
/// differential of an exactly cancelling pair is within 2 x (9 + 10 + 8 + 2) u = 58 u of r S, r = base MVA / rated MVA,
/// S = S_end(HV CT) + S_end(LV CT). The tests assert Id &lt;= 64 u r S.</para>
/// </summary>
public sealed class Rc5Wp18DifferentialTests
{
    private const double U = 1.1102230246251565e-16;   // 2^-53
    private static readonly Complex A = DetMath.FromPolar(1.0, 2.0 * Math.PI / 3.0);

    private static Generator Gen(int bus) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2);

    private static string Doc(params string[] devices) => "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) =>
        DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(devices))), m, new CurveLibrary());

    private static string Refused(GridModel m, params string[] devices) => Assert.Throws<DeviceSetException>(() => Bind(m, devices)).Message;

    private static readonly FaultType[] Types = { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround };

    // ---- 1. Matrix identities ------------------------------------------------------------------------------------------

    [Fact]
    public void T1_matrices_remove_the_zero_sequence_and_turn_the_sequences_by_30_degrees()
    {
        // M [1, 1, 1] = 0 for every zero-sequence-removing matrix: (1 - 1) / sqrt 3 = 0 and 1 - 3/3 = 0, exactly.
        foreach (Compensation m in new[] { Compensation.Plus30, Compensation.Minus30, Compensation.ZeroSequenceRemoval })
        {
            (Complex a, Complex b, Complex c) = TransformerDifferential.Apply(m, Complex.One, Complex.One, Complex.One);
            Assert.Equal(Complex.Zero, a); Assert.Equal(Complex.Zero, b); Assert.Equal(Complex.Zero, c);
        }
        // Positive sequence (1, a^2, a): (1 - a^2) / sqrt 3 = (3/2 + j sqrt3/2) / sqrt 3 = e^(j30); (1 - a) / sqrt 3 = e^(-j30).
        // Negative sequence (1, a, a^2): the opposite turns. I - J/3 leaves both unchanged (their sum is 0).
        Complex[] pos = { Complex.One, A * A, A }, neg = { Complex.One, A, A * A };
        Complex rp = DetMath.FromPolar(1.0, Math.PI / 6.0), rm = DetMath.FromPolar(1.0, -Math.PI / 6.0);
        void Turns(Compensation m, Complex[] set, Complex rot)
        {
            (Complex a, Complex b, Complex c) = TransformerDifferential.Apply(m, set[0], set[1], set[2]);
            Complex[] got = { a, b, c };
            for (int p = 0; p < 3; p++) Assert.True((got[p] - rot * set[p]).Magnitude <= 1e-15, $"{m} phase {p}: {got[p]} vs {rot * set[p]}");
        }
        Turns(Compensation.Plus30, pos, rp); Turns(Compensation.Plus30, neg, rm);
        Turns(Compensation.Minus30, pos, rm); Turns(Compensation.Minus30, neg, rp);
        Turns(Compensation.ZeroSequenceRemoval, pos, Complex.One); Turns(Compensation.ZeroSequenceRemoval, neg, Complex.One);
        Turns(Compensation.Identity, pos, Complex.One);

        // The star side carries the matrix, the delta side is the reference: a star LV winding is turned forward by
        // k x 30 degrees, a star HV winding back by k x 30 degrees; clock 0 removes the zero sequence on a YN side.
        (string G, Compensation H, Compensation L)[] expect =
        {
            ("YNd1", Compensation.Minus30, Compensation.Identity), ("Yd11", Compensation.Plus30, Compensation.Identity),
            ("Dyn1", Compensation.Identity, Compensation.Plus30), ("Dyn11", Compensation.Identity, Compensation.Minus30),
            ("YNyn0", Compensation.ZeroSequenceRemoval, Compensation.ZeroSequenceRemoval), ("Yy0", Compensation.Identity, Compensation.Identity),
            ("Dd0", Compensation.Identity, Compensation.Identity),
        };
        foreach ((string g, Compensation h, Compensation l) in expect)
        {
            Assert.True(TransformerDifferential.TryMatrices(g, out Compensation mh, out Compensation ml));
            Assert.Equal(h, mh); Assert.Equal(l, ml);
        }
        Assert.False(TransformerDifferential.TryMatrices("YNd5", out _, out _));
    }

    // ---- 87T cases -----------------------------------------------------------------------------------------------------

    // 132/33 kV YNd1, 60 MVA, X = 0.12 pu on its rating = 0.2 pu on 100 MVA. HV at the From end (bus 2), so the case's
    // phase shift is +30 degrees (the To bus lags). Source at bus 1 behind a 132 kV line, a second source and a load on
    // the 33 kV side behind a line.
    private static GridModel CaseA(double shift = 30.0, WindingConnection lv = WindingConnection.Delta) => new()
    {
        Name = "wp18-87t",
        BaseMva = 100.0,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pq, BaseKv: 132.0), new Bus(3, BusType.Pq, BaseKv: 33.0),
            new Bus(4, BusType.Pv, Pd: 20.0, Qd: 5.0, BaseKv: 33.0),
        },
        Generators = new[] { Gen(1), Gen(4) },
        Branches = new[]
        {
            new Branch(1, 2, R: 0.01, X: 0.1, X0: 0.3),
            new Branch(2, 3, R: 0.0, X: 0.2, PhaseShiftDeg: shift, IsTransformer: true, FromWinding: WindingConnection.WyeGrounded, ToWinding: lv),
            new Branch(3, 4, R: 0.02, X: 0.1, X0: 0.3),
        },
    };

    private const string T87 = "{ \"id\": \"T\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 1, \"from\": 2, \"to\": 3, \"end\": \"from\" }, "
        + "\"transformerDifferential\": { \"vectorGroup\": \"YNd1\", \"ratedMva\": 60, \"is1\": 0.2, \"k1\": 0.3, \"is2\": 2.0, \"k2\": 0.8 } }";

    /// <summary>r S of the class comment: each listed branch adds T of both its ends, |ys| (|Vi| + |Vj|) per end over the
    /// three sequences, 2 |ys| (|Vi| + |Vj|) in all, with |Yxx| = |Yxy| = |ys| for a branch with no charging and tap 1
    /// (zero sequence: |y0| &lt;= |ys| on these cases, since Z0 &gt;= Z1 and a YN-d unit's Z0 = Z1). So a transformer whose
    /// two CTs sit on its own ends is one entry; listing it twice would double S and assert 128 u, not the derived 64 u.
    /// A CT on another branch is that branch's entry (it also counts the far end, which only widens the bound).</summary>
    private static double RS(FaultPhasors f, double ratedMva, params (int Branch, double Ys)[] ends)
    {
        double s = 0.0;
        foreach ((int l, double ys) in ends)
        {
            Branch br = f.Network!.Model.Branches[l];
            f.TryBus(br.From, out int i); f.TryBus(br.To, out int j);
            s += 2.0 * ys * (f.V0[i].Magnitude + f.V0[j].Magnitude + f.V1[i].Magnitude + f.V1[j].Magnitude + f.V2[i].Magnitude + f.V2[j].Magnitude);
        }
        return 100.0 / ratedMva * s;
    }

    [Fact]
    public void T2_87T_is_stable_for_every_external_fault_in_every_phase()
    {
        BoundDeviceSet b = Bind(CaseA(), T87);
        PlacedDevice d = b.Devices[0];
        int n = 0;
        foreach (FaultSpec fs in new[] { FaultSpec.AtBus(1), FaultSpec.AtBus(2), FaultSpec.AtBus(3), FaultSpec.AtBus(4), FaultSpec.AlongBranch(0, 0.5), FaultSpec.AlongBranch(2, 0.5) })
            foreach (FaultType t in Types)
            {
                FaultPhasors f = FaultPhasors.Build(b.Model, fs, t);
                Assert.Null(f.NotEvaluated);
                Assert.False(TransformerDifferential.IsInternal(b.Model, d, fs));
                TransformerDifferentialResult r = TransformerDifferential.Evaluate(f, d, out string? why)!;
                Assert.Null(why);
                double bound = 64.0 * U * RS(f, 60.0, (1, 1.0 / 0.2));   // both CTs on branch 1: one entry
                for (int p = 0; p < 3; p++)
                {
                    Assert.True(r.IdPu[p] <= bound, $"{fs.Label} {t} phase {p}: Id {r.IdPu[p]} > {bound}");
                    Assert.True(r.IdPu[p] <= 1e-12 * r.IbPu[p], $"{fs.Label} {t} phase {p}: Id {r.IdPu[p]} > 1e-12 Ib {r.IbPu[p]}");
                }
                Assert.False(r.Operates);
                PlacedRow row = PlacedElementEvaluator.Evaluate(b, f)[0];
                Assert.Equal("87T", row.Element);
                Assert.False(row.Operates);
                n++;
            }
        Assert.Equal(24, n);
    }

    [Fact]
    public void T2b_without_zero_sequence_removal_the_HV_earth_fault_shows_I0_in_every_phase()
    {
        // HV terminal SLG (bus 2): I0 flows only on the HV side, through the YN neutral (the delta blocks it). Using the
        // -30 degree turn with the zero sequence kept (Minus30 + J/3) on the HV side instead of Minus30, the positive and
        // negative sequences still cancel and Id = |I0,H| / I_rated,HV in every phase, since (J/3) I_H = I0,H in every row.
        BoundDeviceSet b = Bind(CaseA(), T87);
        PlacedDevice d = b.Devices[0];
        FaultPhasors f = FaultPhasors.Build(b.Model, FaultSpec.AtBus(2), FaultType.SingleLineToGround);
        TransformerDifferentialResult r = TransformerDifferential.Evaluate(f, d, out _)!;
        (Complex ha, Complex hb, Complex hc) = f.PhaseCurrents(1, true);
        double scale = PlacedElementEvaluator.IBaseA(100.0, 132.0) / (60.0 / (Math.Sqrt(3.0) * 132.0) * 1000.0);
        Complex i0 = (ha + hb + hc) / 3.0 * scale;
        Assert.True(i0.Magnitude > 1.0, $"|I0,H| = {i0.Magnitude} pu");
        double bound = 64.0 * U * RS(f, 60.0, (1, 5.0));   // both CTs on branch 1: one entry
        for (int p = 0; p < 3; p++)
        {
            double idKept = (r.HvPu[p] + i0 + r.LvPu[p]).Magnitude;
            Assert.True(Math.Abs(idKept - i0.Magnitude) <= bound + 8.0 * U * i0.Magnitude, $"phase {p}: {idKept} vs |I0| {i0.Magnitude}");
            Assert.True(Math.Abs(idKept - i0.Magnitude) <= 1e-12 * i0.Magnitude);
            Assert.True(r.IdPu[p] <= bound && r.IdPu[p] <= 1e-12 * r.IbPu[p], $"phase {p}: Id {r.IdPu[p]}");
        }
    }

    [Fact]
    public void T2c_the_clock_1_matrix_on_the_YNd1_star_side_would_trip_on_a_through_fault()
    {
        // The spec put the +30 degree matrix on the star side of YNd1. The LV side lags by 30 degrees, so that turns HV and
        // LV 60 degrees apart: on a balanced through fault Id = |I (e^j30 - e^-j30)| = 2 sin 30 |I| = |I|, with |I| the HV
        // current in pu of the rating. The -30 degree matrix (used) cancels it.
        BoundDeviceSet b = Bind(CaseA(), T87);
        FaultPhasors f = FaultPhasors.Build(b.Model, FaultSpec.AtBus(4), FaultType.ThreePhase);
        TransformerDifferentialResult r = TransformerDifferential.Evaluate(f, b.Devices[0], out _)!;
        (Complex ha, Complex hb, Complex hc) = f.PhaseCurrents(1, true);
        double scale = PlacedElementEvaluator.IBaseA(100.0, 132.0) / (60.0 / (Math.Sqrt(3.0) * 132.0) * 1000.0);
        (Complex pa, Complex pb, Complex pc) = TransformerDifferential.Apply(Compensation.Plus30, ha * scale, hb * scale, hc * scale);
        Complex[] wrong = { pa, pb, pc }, raw = { ha * scale, hb * scale, hc * scale };
        for (int p = 0; p < 3; p++)
        {
            double id = (wrong[p] + r.LvPu[p]).Magnitude;
            Assert.True(Math.Abs(id - raw[p].Magnitude) <= 1e-12 * raw[p].Magnitude, $"phase {p}: {id} vs {raw[p].Magnitude}");
            Assert.True(r.IdPu[p] <= 1e-12 * r.IbPu[p]);
        }
    }

    // LV terminal bus 3 between the transformer and a short link 3-4 to the LV busbar 4 (load only, no source); the LV CT
    // sits on the link at bus 4, so bus 3 is inside the zone.
    private static GridModel CaseB(bool extraLink = false) => new()
    {
        Name = "wp18-87t-internal",
        BaseMva = 100.0,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pq, BaseKv: 132.0), new Bus(3, BusType.Pq, BaseKv: 33.0),
            new Bus(4, BusType.Pq, Pd: 20.0, Qd: 5.0, BaseKv: 33.0),
        },
        Generators = new[] { Gen(1) },
        Branches = new[]
        {
            new Branch(1, 2, R: 0.01, X: 0.1, X0: 0.3),
            new Branch(2, 3, R: 0.0, X: 0.2, PhaseShiftDeg: 30.0, IsTransformer: true, FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.Delta),
            new Branch(3, 4, R: 0.001, X: 0.01, X0: 0.03),
        }.Concat(extraLink ? new[] { new Branch(3, 4, R: 0.001, X: 0.01) } : Array.Empty<Branch>()).ToArray(),
    };

    private const string T87B = "{ \"id\": \"T\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 1, \"from\": 2, \"to\": 3, \"end\": \"from\" }, "
        + "\"transformerDifferential\": { \"vectorGroup\": \"YNd1\", \"ratedMva\": 60, \"lvCt\": { \"branch\": { \"index\": 2, \"from\": 3, \"to\": 4, \"end\": \"to\" } }, "
        + "\"is1\": 0.2, \"k1\": 0.3, \"is2\": 2.0, \"k2\": 0.8 } }";

    [Fact]
    public void T3_87T_internal_fault_fed_from_HV_gives_Id_equal_to_the_HV_current()
    {
        // Bolted 3ph at bus 3, inside the zone, fed from the HV side only: Id = |M_H I_H + M_L I_L| with I - J/3 leaving the
        // balanced HV set as it is, so |Id - |I_H|| <= |I_L| + 64 u r S per phase. Bus 4 has only a constant-impedance load,
        // so I_L would be 0 in exact arithmetic; the superposition carries the prefault power flow's KCL residual r at bus 4
        // (the solve stops at its mismatch tolerance), and with V3 = 0 the link current is r y / (y + Y_load), |y / (y +
        // Y_load)| < 1.01 here (y = 1 / (0.001 + j0.01), Y_load = 0.2 - j0.05). So |I_L| <= 1.01 |r| (100/60) + 64 u r S.
        // That residual (about 1e-10 pu here) is why the spec's 1e-12 relative agreement cannot hold for this case.
        BoundDeviceSet b = Bind(CaseB(), T87B);
        PlacedDevice d = b.Devices[0];
        Assert.Equal(new[] { 3 }, TransformerDifferential.InsideBuses(b.Model, d));
        Assert.True(TransformerDifferential.IsInternal(b.Model, d, FaultSpec.AtBus(3)));
        Assert.True(TransformerDifferential.IsInternal(b.Model, d, FaultSpec.AlongBranch(2, 0.5)));
        Assert.False(TransformerDifferential.IsInternal(b.Model, d, FaultSpec.AtBus(4)));
        FaultPhasors f = FaultPhasors.Build(b.Model, FaultSpec.AtBus(3), FaultType.ThreePhase);
        TransformerDifferentialResult r = TransformerDifferential.Evaluate(f, d, out _)!;
        (Complex ha, Complex hb, Complex hc) = f.PhaseCurrents(1, true);
        double scale = PlacedElementEvaluator.IBaseA(100.0, 132.0) / (60.0 / (Math.Sqrt(3.0) * 132.0) * 1000.0);
        double[] ih = { (ha * scale).Magnitude, (hb * scale).Magnitude, (hc * scale).Magnitude };
        double bound = 64.0 * U * RS(f, 60.0, (1, 5.0), (2, 1.0 / new Complex(0.001, 0.01).Magnitude));
        PowerFlowResult pf = FaultPhasors.DefaultPrefault(b.Model);
        Complex v3 = DetMath.FromPolar(pf.Vm[2], pf.VaDeg[2] * Math.PI / 180.0), v4 = DetMath.FromPolar(pf.Vm[3], pf.VaDeg[3] * Math.PI / 180.0);
        BranchStamp st = AdmittanceStamp.Branch(0.001, 0.01, 0.0, 1.0, 0.0);
        double resid = (st.Ytf * v3 + st.Ytt * v4 + Complex.Conjugate(new Complex(0.2, 0.05) / v4)).Magnitude;
        for (int p = 0; p < 3; p++)
        {
            Assert.True(ih[p] > 1.0, $"|I_H| {ih[p]} pu");
            double err = Math.Abs(r.IdPu[p] - ih[p]);
            Assert.True(err <= r.LvPu[p].Magnitude + bound, $"phase {p}: Id {r.IdPu[p]} vs |I_H| {ih[p]}");
            Assert.True(r.LvPu[p].Magnitude <= 1.01 * resid * 100.0 / 60.0 + bound, $"phase {p}: |I_L| {r.LvPu[p].Magnitude}, |r| {resid}");
        }
        Assert.True(r.Operates);
        PlacedRow row = PlacedElementEvaluator.Evaluate(b, f)[0];
        Assert.True(row.Operates);
        Assert.Equal(0.0, row.TimeS);
        Assert.Equal("pu", row.Unit);
    }

    [Fact]
    public void T3b_87T_settings_are_checked_against_the_case()
    {
        // The case's phase shift must be the group's: YNd1 with HV at From needs +30 (or -330).
        Assert.Contains("needs 30 degrees", Refused(CaseA(shift: 0.0), T87), StringComparison.Ordinal);
        Bind(CaseA(shift: -330.0), T87);
        Assert.Contains("does not match the case's windings", Refused(CaseA(lv: WindingConnection.WyeGrounded), T87), StringComparison.Ordinal);
        Assert.Contains("is not supported", Refused(CaseA(), T87.Replace("YNd1", "YNd5", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("needs branch 0 to be a transformer",
            Refused(CaseA(), T87.Replace("\"index\": 1, \"from\": 2, \"to\": 3", "\"index\": 0, \"from\": 1, \"to\": 2", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("goes only with function 87", Refused(CaseA(), T87.Replace("\"function\": \"87\"", "\"function\": \"51\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("not more", Refused(CaseA(), T87.Replace("\"transformerDifferential\"", "\"differential\": { \"is1A\": 0.2, \"k1\": 0.3, \"is2A\": 2.0, \"k2\": 1.5 }, \"transformerDifferential\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        // A CT link that leaves another branch at the inside bus uncovered is refused.
        Assert.Contains("also leaves it without a CT", Refused(CaseB(extraLink: true), T87B), StringComparison.Ordinal);
        // An 87 with a differential setting on a transformer keeps the old refusal word for word.
        Assert.Equal("Device 'T1': a 87 on a transformer needs vector-group compensation; out of scope.",
            Refused(CaseA(), "{ \"id\": \"T1\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 1, \"from\": 2, \"to\": 3, \"end\": \"from\" }, "
                + "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"differential\": { \"is1A\": 0.2, \"k1\": 0.3, \"is2A\": 2.0, \"k2\": 1.5 } }"));
    }

    // ---- 87B cases -----------------------------------------------------------------------------------------------------

    // Busbar 1 (132 kV, no units or loads) with four feeders: 1-2 and 1-3 to sources, 1-4 and 1-5 to loads. A YNd1
    // earthing transformer at bus 3 (to bus 6, 33 kV) gives the zero-sequence network an earth.
    private static GridModel CaseC() => new()
    {
        Name = "wp18-87b",
        BaseMva = 100.0,
        Buses = new[]
        {
            new Bus(1, BusType.Pq, BaseKv: 132.0), new Bus(2, BusType.Slack, BaseKv: 132.0), new Bus(3, BusType.Pv, BaseKv: 132.0),
            new Bus(4, BusType.Pq, Pd: 30.0, Qd: 10.0, BaseKv: 132.0), new Bus(5, BusType.Pq, Pd: 20.0, Qd: 5.0, BaseKv: 132.0),
            new Bus(6, BusType.Pq, BaseKv: 33.0),
        },
        Generators = new[] { Gen(2), Gen(3) },
        Branches = new[]
        {
            new Branch(1, 2, R: 0.01, X: 0.1, X0: 0.3), new Branch(1, 3, R: 0.01, X: 0.12, X0: 0.36),
            new Branch(1, 4, R: 0.02, X: 0.15, X0: 0.45), new Branch(5, 1, R: 0.02, X: 0.2, X0: 0.6),
            new Branch(3, 6, R: 0.0, X: 0.1, PhaseShiftDeg: 30.0, IsTransformer: true, FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.Delta),
        },
    };

    private static string Ct(int index, int from, int to, string end, double primary) =>
        "{ \"branch\": { \"index\": " + index + ", \"from\": " + from + ", \"to\": " + to + ", \"end\": \"" + end + "\" }, \"ct\": { \"primaryA\": "
        + primary.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"secondaryA\": 1 } }";

    private static string Bus87(string bias = "sum", bool dropFeeder3 = false, string extra = "") =>
        "{ \"id\": \"B\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"from\" }, "
        + "\"busDifferential\": { \"zone\": [ 1 ], \"cts\": [ " + Ct(0, 1, 2, "from", 1200) + ", " + Ct(1, 1, 3, "from", 800)
        + (dropFeeder3 ? "" : ", " + Ct(2, 1, 4, "from", 600)) + ", " + Ct(3, 5, 1, "to", 400) + " ], \"is1A\": 0.2, \"k1\": 0.3, \"bias\": \"" + bias + "\"" + extra + " } }";

    /// <summary>Sum over the CTs of the sequence-current magnitudes scaled to reference secondary amps (the S of the 87B
    /// bounds).</summary>
    private static double SeqSum(FaultPhasors f, double k)
    {
        double s = 0.0;
        foreach ((int l, bool from) in new[] { (0, true), (1, true), (2, true), (3, false) })
        {
            int seg = from ? FaultNetwork.FromSegment(l) : f.Network!.ToSegment(l);
            (Complex i0, Complex i1, Complex i2) = f.Sequence(seg, from);
            s += (i0.Magnitude + i1.Magnitude + i2.Magnitude) * k;
        }
        return s;
    }

    /// <summary>
    /// The bound on the CT feeders' sum at a zone bus against the current the bus injects (0, or the fault current), per
    /// phase. CT sum - k inj = k (r_pf + rho_LU) + formation error, where r_pf is the prefault power flow's KCL residual at
    /// the bus (the flow stops at its mismatch tolerance) and rho_LU the residual the sequence solves leave there. Both are
    /// measured after the fact, so no LU error theory is needed (row 1 of |L||U| is not bounded by |Y| in general: it
    /// depends on the ordering).
    /// <list type="bullet">
    /// <item>r_pf: from the solved prefault voltages and the branch stamps (positive sequence, the same in every phase).</item>
    /// <item>R: per phase, |T(sum over the bus's branches of Y_br V_seq) + If_phase|, from the engine's own sequence voltages
    /// (the positive and negative stamps, the zero-sequence series admittance), T the sequence-to-phase transform. It is
    /// the whole residual of the fault state at the bus, r_pf included, so k (|r_pf| + R) counts r_pf twice, which only
    /// widens the bound.</item>
    /// <item>Rounding: forming the four feeders and their sum, at most 32 u S (T4's comment), plus the test's own evaluation
    /// of R, (sqrt 5 + 1) u per term on both sides, at most 8 u S.</item>
    /// </list>
    /// Bound: k (|r_pf| + R) + 40 u S. A wrong CT end or sign gives an error of the order of a branch current, far above it.
    /// </summary>
    private static double[] KclBound(FaultPhasors f, int busId, double k, double s, (Complex I0, Complex I1, Complex I2) faultSeq)
    {
        GridModel m = f.Network!.Model;
        PowerFlowResult pf = FaultPhasors.DefaultPrefault(m);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < m.Buses.Count; i++) index[m.Buses[i].Id] = i;
        var v = new Dictionary<int, Complex>();
        for (int i = 0; i < m.Buses.Count; i++) v[m.Buses[i].Id] = DetMath.FromPolar(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);
        Iec60909.ZeroSequenceNetwork zs = Iec60909.BuildZeroSequenceNetwork(m, index, 3.0);
        Complex rpf = Complex.Zero, s0 = Complex.Zero, s1 = Complex.Zero, s2 = Complex.Zero;
        for (int l = 0; l < m.Branches.Count; l++)
        {
            Branch br = m.Branches[l];
            if (!br.InService || (br.From != busId && br.To != busId)) continue;
            BranchStamp st = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            BranchStamp neg = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, -br.PhaseShiftDeg);
            Assert.Equal(Iec60909.ZeroSeqBranchKind.Series, zs.Kind[l]);
            int a = index[br.From], b = index[br.To];
            Complex z0 = zs.Admittance[l] * (f.V0[a] - f.V0[b]);
            if (br.From == busId)
            {
                rpf += st.Yff * v[br.From] + st.Yft * v[br.To];
                s1 += st.Yff * f.V1[a] + st.Yft * f.V1[b];
                s2 += neg.Yff * f.V2[a] + neg.Yft * f.V2[b];
                s0 += z0;
            }
            if (br.To == busId)
            {
                rpf += st.Ytf * v[br.From] + st.Ytt * v[br.To];
                s1 += st.Ytf * f.V1[a] + st.Ytt * f.V1[b];
                s2 += neg.Ytf * f.V2[a] + neg.Ytt * f.V2[b];
                s0 -= z0;
            }
        }
        (Complex pa, Complex pb, Complex pc) = FaultAnalysis.Phases(s0, s1, s2);
        (Complex fa, Complex fb, Complex fc) = FaultAnalysis.Phases(faultSeq.I0, faultSeq.I1, faultSeq.I2);
        // The branch currents and the fault current all leave the bus, so by KCL they add to 0.
        double[] r = { (pa + fa).Magnitude, (pb + fb).Magnitude, (pc + fc).Magnitude };
        return r.Select(x => k * (rpf.Magnitude + x) + 40.0 * U * s).ToArray();
    }

    [Fact]
    public void T4_87B_is_stable_for_an_external_fault_on_feeder_3()
    {
        // Bus 1 carries nothing but the four feeders, so the KCL injection of bus 1 is the sum of the same four branch
        // currents and Id cancels to rounding. Rounding: each phase current within 9 u of the sequence sum (class comment),
        // the scaling (common to every term, the bus is the same) 6 u, and the two four-term sums 8 u: Id <= 32 u S.
        BoundDeviceSet b = Bind(CaseC(), Bus87());
        PlacedDevice d = b.Devices[0];
        double k = PlacedElementEvaluator.IBaseA(100.0, 132.0) / 1200.0;
        foreach (FaultSpec fs in new[] { FaultSpec.AlongBranch(2, 0.5), FaultSpec.AtBus(4) })
            foreach (FaultType t in Types)
            {
                FaultPhasors f = FaultPhasors.Build(b.Model, fs, t);
                Assert.False(BusDifferential.IsInternal(b.Model, d.BusDifferential!, fs));
                BusDifferentialResult r = BusDifferential.Evaluate(b, f, d, out string? why)!;
                Assert.Null(why);
                double s = SeqSum(f, k);
                double[] kcl = KclBound(f, 1, k, s, (Complex.Zero, Complex.Zero, Complex.Zero));
                for (int p = 0; p < 3; p++)
                {
                    // The four CT feeders alone, with no KCL injection term, against 0. The bound holds the engine's own
                    // fault-state residual at bus 1 (R in KclBound), so this checks how each CT feeder is formed from the
                    // engine's currents: the CT end, the sign and the ratio. It does not check the solve's KCL, which R
                    // already contains.
                    Complex ct = Complex.Zero;
                    foreach (Complex[] fd in r.Zone.FeedersA) ct += fd[p];
                    Assert.True(ct.Magnitude <= kcl[p], $"{fs.Label} {t} phase {p}: CT sum {ct.Magnitude} > {kcl[p]}");
                    Assert.True(r.Zone.IdA[p] <= 32.0 * U * s, $"{fs.Label} {t} phase {p}: Id {r.Zone.IdA[p]} > {32.0 * U * s}");
                    Assert.True(r.Zone.IdA[p] <= 1e-12 * r.Zone.IbA[p], $"{fs.Label} {t} phase {p}: Id {r.Zone.IdA[p]}, sum |I| {r.Zone.IbA[p]}");
                }
                Assert.False(r.Operates);
                Assert.False(PlacedElementEvaluator.Evaluate(b, f)[0].Operates);
            }
    }

    [Fact]
    public void T5_87B_bus_fault_gives_the_fault_current_and_the_bias_is_the_sum_or_the_largest()
    {
        // Fault at bus 1: by KCL the feeder currents into the zone add to the fault current. Id is formed from the CTs and the
        // bus's KCL injection (branch currents plus the fault current), so Id = |If,phase| to rounding (32 u S plus 16 u of
        // |If| k for the fault current's phase formation and scaling). The fault current is the engine's own: the sequence
        // currents of FaultAnalysis.Unbalanced, or the three-phase study's If.
        double k = PlacedElementEvaluator.IBaseA(100.0, 132.0) / 1200.0;
        foreach (FaultType t in Types)
        {
            foreach (string bias in new[] { "sum", "max" })
            {
                BoundDeviceSet b = Bind(CaseC(), Bus87(bias));
                PlacedDevice d = b.Devices[0];
                FaultPhasors f = FaultPhasors.Build(b.Model, FaultSpec.AtBus(1), t);
                Assert.True(BusDifferential.IsInternal(b.Model, d.BusDifferential!, FaultSpec.AtBus(1)));
                (Complex I0, Complex I1, Complex I2) ifSeq;
                PowerFlowResult pf = FaultPhasors.DefaultPrefault(b.Model);
                if (t == FaultType.ThreePhase)
                {
                    FaultResult fr = FaultAnalysis.ThreePhase(b.Model, pf, 1);
                    Assert.Equal(fr.FaultCurrentPu, fr.FaultCurrentPhasorPu.Magnitude);
                    ifSeq = (Complex.Zero, fr.FaultCurrentPhasorPu, Complex.Zero);
                }
                else
                {
                    UnbalancedFaultResult u = FaultAnalysis.Unbalanced(b.Model, pf, 1, t);
                    ifSeq = (u.I0, u.I1, u.I2);
                }
                (Complex ifa, Complex ifb, Complex ifc) = FaultAnalysis.Phases(ifSeq.I0, ifSeq.I1, ifSeq.I2);
                Complex[] ifp = { ifa, ifb, ifc };
                BusDifferentialResult r = BusDifferential.Evaluate(b, f, d, out _)!;
                double s = SeqSum(f, k);
                double[] kcl = KclBound(f, 1, k, s, ifSeq);
                for (int p = 0; p < 3; p++)
                {
                    double want = ifp[p].Magnitude * k;
                    // The four CT feeders alone against the engine's own fault current (FaultAnalysis, computed above, not
                    // the zone's injection term), within the measured residuals at bus 1 and the rounding. As in T4 the
                    // bound holds the solve's own residual R, so this checks the CT end, the sign and the ratio of each
                    // feeder, not the solve's KCL. The product k If adds at most u k |If|, written out although it is far
                    // inside 40 u S.
                    Complex ct = Complex.Zero;
                    for (int c = 0; c < 4; c++) ct += r.Zone.FeedersA[c][p];
                    Assert.True((ct - ifp[p] * k).Magnitude <= kcl[p] + U * want, $"{t} phase {p}: CT sum {ct} vs If {ifp[p] * k}, bound {kcl[p]}");
                    double err = Math.Abs(r.Zone.IdA[p] - want);
                    Assert.True(err <= 32.0 * U * s + 16.0 * U * want, $"{t} phase {p}: Id {r.Zone.IdA[p]} vs |If| {want}");
                    Assert.True(err <= 1e-12 * Math.Max(want, r.Zone.IbA[p]), $"{t} phase {p}: Id {r.Zone.IdA[p]} vs |If| {want}");
                    // The bias, recomputed from the reported feeders in the same order: exactly the sum or the largest.
                    double expect = 0.0;
                    foreach (Complex[] fd in r.Zone.FeedersA) expect = bias == "sum" ? expect + fd[p].Magnitude : Math.Max(expect, fd[p].Magnitude);
                    Assert.Equal(expect, r.Zone.IbA[p]);
                }
                // The feeders are the four CTs (bus 1 carries no units or loads), each -I into its branch in reference amps.
                Assert.Equal(4, r.Zone.FeedersA.Count);
                (Complex fa, _, _) = f.PhaseCurrents(0, true);
                Assert.Equal(-fa * k, r.Zone.FeedersA[0][0]);
                Assert.True(r.Operates);
                PlacedRow row = PlacedElementEvaluator.Evaluate(b, f)[0];
                Assert.Equal("87B", row.Element);
                Assert.True(row.Operates);
                Assert.Null(row.FaultDirection);
            }
        }
    }

    [Fact]
    public void T5b_check_zone_trips_only_with_the_main_zone()
    {
        // Check zone {1, 2}: bus 2's unit becomes a feeder (its KCL injection); the CT on 1-2 is inside it and not counted.
        BoundDeviceSet b = Bind(CaseC(), Bus87(extra: ", \"checkZone\": [ 1, 2 ]"));
        PlacedDevice d = b.Devices[0];
        BusDifferentialResult r1 = BusDifferential.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AtBus(1), FaultType.ThreePhase), d, out _)!;
        Assert.True(r1.Zone.Operates); Assert.True(r1.CheckZone!.Operates); Assert.True(r1.Operates);
        Assert.Equal(4, r1.CheckZone.FeedersA.Count);   // CTs 1-3, 1-4, 5-1 and bus 2's injection
        BusDifferentialResult r2 = BusDifferential.Evaluate(b, FaultPhasors.Build(b.Model, FaultSpec.AtBus(2), FaultType.ThreePhase), d, out _)!;
        Assert.False(r2.Zone.Operates); Assert.True(r2.CheckZone!.Operates); Assert.False(r2.Operates);
        Assert.Contains("must hold every bus", Refused(CaseC(), Bus87(extra: ", \"checkZone\": [ 2 ]")), StringComparison.Ordinal);
    }

    [Fact]
    public void T6_a_missing_CT_fails_G7_naming_the_branch_and_the_element_is_not_evaluated()
    {
        BoundDeviceSet b = Bind(CaseC(), Bus87(dropFeeder3: true));
        FaultPhasors f = FaultPhasors.Build(b.Model, FaultSpec.AtBus(1), FaultType.ThreePhase);
        PlacedRow row = PlacedElementEvaluator.Evaluate(b, f)[0];
        Assert.False(row.Evaluated);
        Assert.Equal("the zone is incomplete: branch 2 (1-4) leave(s) it without a CT", row.Reason);
        PlacedCoordinationResult g = PlacedCoordination.Run(b, null, new[] { FaultType.ThreePhase });
        PlacedCheck g7 = Assert.Single(g.Checks, c => c.Check == "G7");
        Assert.False(g7.Passed);
        Assert.Contains("branch 2 (1-4)", g7.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(g.Checks, c => c.Check == "G8");
        Assert.Equal(PlacedVerdict.NotCoordinated, g.Verdict);
    }

    [Fact]
    public void T7_grading_reports_G7_and_G8_for_complete_stable_elements()
    {
        PlacedCoordinationResult gb = PlacedCoordination.Run(Bind(CaseC(), Bus87()), null, Types);
        Assert.True(Assert.Single(gb.Checks, c => c.Check == "G7").Passed);
        PlacedCheck g8 = Assert.Single(gb.Checks, c => c.Check == "G8");
        Assert.True(g8.Passed, g8.Detail);
        Assert.Equal(5 * 4, g8.Value);   // buses 2 to 6, four fault types each, all outside the zone
        PlacedCoordinationResult gt = PlacedCoordination.Run(Bind(CaseA(), T87), null, Types);
        PlacedCheck t8 = Assert.Single(gt.Checks, c => c.Check == "G8");
        Assert.True(t8.Passed, t8.Detail);
        Assert.Equal(4 * 4, t8.Value);
    }

    [Fact]
    public void T8_cli_protection_eval_and_grade_show_the_new_rows()
    {
        string dir = TestTemp.Under("gridsim-wp18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dev = Path.Combine(dir, "d.json");
        // case9: busbar 4 with feeders 1-4 (branch 0, from end at 1, so the CT sits at its to end), 4-5 (1) and 9-4 (8).
        File.WriteAllText(dev, Doc("{ \"id\": \"B4\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 1, \"from\": 4, \"to\": 5, \"end\": \"from\" }, "
            + "\"busDifferential\": { \"zone\": [ 4 ], \"cts\": [ " + Ct(0, 1, 4, "to", 2000) + ", " + Ct(1, 4, 5, "from", 1000) + ", " + Ct(8, 9, 4, "to", 1000)
            + " ], \"is1A\": 0.2, \"k1\": 0.3 } }"));
        CliProcess.Result r = CliProcess.RunBuilt("protection-eval", "case9", "--devices", dev, "--bus", "4");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("87B", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("operates in 0 s", r.StdOut, StringComparison.Ordinal);
        CliProcess.Result g = CliProcess.RunBuilt("protection-grade", "case9", "--devices", dev, "--types", "3ph");
        Assert.Contains("G7 B4: pass", g.StdOut, StringComparison.Ordinal);
        Assert.Contains("G8 B4: pass", g.StdOut, StringComparison.Ordinal);
    }

    // ---- review fixes (2.1.116) ----------------------------------------------------------------------------------------

    [Fact]
    public void T9_87T_without_winding_data_is_refused_unless_YNyn0()
    {
        // With either winding missing the zero-sequence network passes I0 through the unit (YNyn). A YNd1 or Dyn1 87T would
        // then see the HV earth-fault I0 on its delta side, where no matrix removes it: an external earth fault would trip.
        GridModel a = CaseA();
        GridModel With(Branch t) => a.CopyWith(branches: a.Branches.Select((x, i) => i == 1 ? t : x).ToArray());
        Branch tr = a.Branches[1];
        Assert.Contains("the case gives no winding data for transformer 1, so the fault study treats it as YNyn; the 87T needs the windings to match YNd1",
            Refused(With(tr with { FromWinding = null, ToWinding = null }), T87), StringComparison.Ordinal);
        Assert.Contains("no winding data", Refused(With(tr with { ToWinding = null }), T87), StringComparison.Ordinal);
        // YNyn0 (phase shift 0) is what the study models without windings, so it is accepted.
        Bind(With(tr with { FromWinding = null, ToWinding = null, PhaseShiftDeg = 0.0 }), T87.Replace("YNd1", "YNyn0", StringComparison.Ordinal));
    }

    [Fact]
    public void T10_87T_CT_link_refuses_an_inside_bus_with_a_load_shunt_unit_converter_svc_or_facts_device()
    {
        // Case B's inside bus 3 with a load, a shunt or a unit: each draws current no CT measures.
        foreach (Func<GridModel, GridModel> add in new Func<GridModel, GridModel>[]
        {
            m => m.CopyWith(buses: m.Buses.Select(x => x.Id == 3 ? x with { Pd = 5.0, Qd = 1.0 } : x).ToArray()),
            m => m.CopyWith(buses: m.Buses.Select(x => x.Id == 3 ? x with { Bs = 2.0 } : x).ToArray()),
            m => m.CopyWith(generators: m.Generators.Concat(new[] { Gen(3) }).ToArray()),
        })
            Assert.Contains("bus 3 is inside the zone, but it has a load, shunt or unit whose current no CT measures",
                Refused(add(CaseB()), T87B), StringComparison.Ordinal);
        // 2.1.118: an HVDC converter, a static var compensator or a FACTS device at the inside bus is injected there by the
        // prefault power flow, and no CT measures its current either. A VSC terminal at bus 3 (the link's other end at bus
        // 1), an SVC at bus 3, an LCC inverter, a multi-terminal converter and a STATCOM at bus 3: each is refused by name.
        var vsc = new VscConverter(3, VscDcControl.Power, VscAcControl.PowerFactor, 10.0, 1.0, 0.0, 0.0, 0.0, 100.0, 0.0, 1.0, 50.0, -50.0);
        var vscDc = new VscConverter(1, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 320.0, 1.0, 0.0, 0.0, 0.0, 100.0, 0.0, 1.0, 50.0, -50.0);
        var lccEnd = new LccConverter(3, 1, 20.0, 5.0, 0.0, 5.0, 33.0, 1.0, 1.0, 1.1, 0.9, 0.00625);
        var lccOther = lccEnd with { Bus = 1, BaseKv = 132.0 };
        foreach ((string what, Func<GridModel, GridModel> add) in new (string, Func<GridModel, GridModel>)[]
        {
            ("a converter of VSC HVDC link V1", m => m.CopyWith(vscLinks: new[] { new VscDcLink("V1", 1.0, vscDc, vsc) })),
            ("static var compensator S1", m => m.CopyWith(staticVarCompensators: new[] { new StaticVarCompensator(3, -20.0, 20.0, 0.0, 1.0, 1.0, "S1") })),
            ("a converter of LCC HVDC link L1", m => m.CopyWith(lccLinks: new[] { new LccDcLink("L1", LccMode.Power, 5.0, 20.0, 100.0, 80.0, 0.0, 0.15, lccOther, lccEnd) })),
            ("a converter of multi-terminal DC link M1", m => m.CopyWith(mtdcLinks: new[] { new MtdcLink("M1", LccMode.Power, 1,
                new[] { new MtdcConverter(1, 1, 20.0, 5.0, 0.0, 5.0, 132.0, 1.0, 1.0, 1.1, 0.9, 0.00625, 100.0, 1),
                    new MtdcConverter(3, 1, 20.0, 5.0, 0.0, 5.0, 33.0, 1.0, 1.0, 1.1, 0.9, 0.00625, -20.0, 2) },
                new[] { new MtdcDcBus(1, 1, 0.0, null), new MtdcDcBus(2, 3, 0.0, null) }, new[] { new MtdcDcLine(1, 2, 5.0) }) })),
            ("FACTS device F1", m => m.CopyWith(factsDevices: new[] { new FactsDevice("F1", FactsMode.Statcom, 3, 0, 0.0, 0.0, 1.0, 50.0, 0.0, 0.9, 1.1, 0.0, 0.0) })),
        })
            Assert.Contains($"bus 3 is inside the zone, but it has {what} whose current no CT measures", Refused(add(CaseB()), T87B), StringComparison.Ordinal);
        // The same devices at the outside bus 4 are not inside the zone: accepted.
        Bind(CaseB().CopyWith(vscLinks: new[] { new VscDcLink("V1", 1.0, vscDc, vsc with { Bus = 4 }) },
            staticVarCompensators: new[] { new StaticVarCompensator(4, -20.0, 20.0, 0.0, 1.0, 1.0, "S1") }), T87B);
        Bind(CaseB(), T87B);
    }

    [Fact]
    public void T11_a_branch_fault_at_fraction_0_or_1_is_a_fault_at_that_end_bus_for_G8()
    {
        // 87B zone {1}: feeder 1-4 (branch 2) at fraction 0 is a fault at bus 1, inside; at fraction 1 it is bus 4, outside.
        BoundDeviceSet b = Bind(CaseC(), Bus87());
        BusDifferentialSetting s = b.Devices[0].BusDifferential!;
        Assert.True(BusDifferential.IsInternal(b.Model, s, FaultSpec.AlongBranch(2, 0.0)));
        Assert.False(BusDifferential.IsInternal(b.Model, s, FaultSpec.AlongBranch(2, 1.0)));
        Assert.False(BusDifferential.IsInternal(b.Model, s, FaultSpec.AlongBranch(2, 0.5)));
        Assert.True(BusDifferential.IsInternal(b.Model, s, FaultSpec.AtBus(1)));
        // Branch 3 runs 5-1: its fraction 1 end is bus 1.
        Assert.True(BusDifferential.IsInternal(b.Model, s, FaultSpec.AlongBranch(3, 1.0)));
        // 87T with the LV CT on link 3-4 at bus 4: bus 3 is inside. The link at fraction 0 is bus 3 (inside), at 1 bus 4.
        BoundDeviceSet t = Bind(CaseB(), T87B);
        PlacedDevice d = t.Devices[0];
        Assert.True(TransformerDifferential.IsInternal(t.Model, d, FaultSpec.AlongBranch(2, 0.0)));
        Assert.False(TransformerDifferential.IsInternal(t.Model, d, FaultSpec.AlongBranch(2, 1.0)));
        Assert.True(TransformerDifferential.IsInternal(t.Model, d, FaultSpec.AlongBranch(2, 0.5)));
        // The transformer's own branch at fraction 1 is bus 3 too.
        Assert.True(TransformerDifferential.IsInternal(t.Model, d, FaultSpec.AlongBranch(1, 1.0)));
        // And the evaluation agrees: the fault at link fraction 0 is the bus-3 fault, which operates.
        FaultPhasors f0 = FaultPhasors.Build(t.Model, FaultSpec.AlongBranch(2, 0.0), FaultType.ThreePhase);
        FaultPhasors f3 = FaultPhasors.Build(t.Model, FaultSpec.AtBus(3), FaultType.ThreePhase);
        Assert.Equal(TransformerDifferential.Evaluate(f3, d, out _)!.IdPu, TransformerDifferential.Evaluate(f0, d, out _)!.IdPu);
        Assert.True(TransformerDifferential.Evaluate(f0, d, out _)!.Operates);
    }

    [Fact]
    public void T12_87T_CT_link_refuses_a_UPFC_terminal_inside_and_accepts_a_FACTS_device_with_no_shunt_element()
    {
        // 2.1.120. A UPFC's series element delivers PDES + j QDES to its terminal end bus (PowerFlow.HvdcCoupling), so a UPFC
        // whose terminal end is the inside bus 3 is refused, though its sending end (bus 4) is outside the zone.
        Assert.Contains("bus 3 is inside the zone, but it has FACTS device U1 whose current no CT measures",
            Refused(CaseB().CopyWith(factsDevices: new[] { new FactsDevice("U1", FactsMode.Upfc, 4, 3, 5.0, 1.0, 1.0, 50.0, 50.0, 0.9, 1.1, 0.1, 50.0) }), T87B),
            StringComparison.Ordinal);
        // A UPFC with no shunt element still injects at its terminal end.
        Assert.Contains("FACTS device U0 whose current no CT measures",
            Refused(CaseB().CopyWith(factsDevices: new[] { new FactsDevice("U0", FactsMode.Upfc, 4, 3, 5.0, 1.0, 1.0, 0.0, 0.0, 0.9, 1.1, 0.1, 50.0) }), T87B),
            StringComparison.Ordinal);
        // A STATCOM or a constant-impedance series device with no shunt element (ShuntMaxMva 0) injects nothing at its sending
        // end bus: the power flow skips it there. At the inside bus 3 it is accepted.
        Bind(CaseB().CopyWith(factsDevices: new[] { new FactsDevice("F0", FactsMode.Statcom, 3, 0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.9, 1.1, 0.0, 0.0) }), T87B);
        Bind(CaseB().CopyWith(factsDevices: new[] { new FactsDevice("Z0", FactsMode.SeriesImpedance, 3, 4, 0.0, 0.0, 1.0, 0.0, 0.0, 0.9, 1.1, 0.0, 0.0) }), T87B);
        // With a shunt element the same STATCOM is refused, as T10 shows for a 50 MVA one.
        Assert.Contains("FACTS device F1 whose current no CT measures",
            Refused(CaseB().CopyWith(factsDevices: new[] { new FactsDevice("F1", FactsMode.Statcom, 3, 0, 0.0, 0.0, 1.0, 1.0, 0.0, 0.9, 1.1, 0.0, 0.0) }), T87B),
            StringComparison.Ordinal);
    }
}
