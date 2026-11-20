using System;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.ShortCircuit.Ena;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-06: the G74-style study (<see cref="G74Method"/>), series faults and simultaneous faults. ENA ER G74 Issue 2:2021
/// was not obtainable, so every oracle here is independent of it: closed forms derived in the test (a source behind an
/// impedance with a motor and a converter; the two-exponential machine envelope; sequence-network results checked against
/// a direct phase-domain solve) and the incumbent <see cref="Iec60909"/> for the shared quantities. No G74 number is used.
/// </summary>
public class G74ShortCircuitTests
{
    private const double BaseMva = 100.0, Kv = 132.0, C = 1.10;
    private static double IBaseKa => BaseMva / (Math.Sqrt(3.0) * Kv);

    private static GridModel Case(params Generator[] gens) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
        Generators = gens,
        Branches = Array.Empty<Branch>(),
    };

    private static Generator Sync(double xd) => new(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xd, Name: "G1");

    private static G74Data Data(string json) => G74Data.Parse(json);

    private const string GenJson = "{\"schema\":\"gridsim-g74-data/1\",\"generators\":[{\"bus\":1,\"xdTransPu\":0.3,\"xdSyncPu\":1.2,\"tdSubS\":0.05,\"tdTransS\":1.0}]";

    // ---- synchronous machine: the independent two-exponential closed form ----
    [Fact]
    public void Single_machine_decay_matches_two_exponential_closed_form()
    {
        GridModel m = Case(Sync(0.2));
        G74BusResult r = G74Method.Compute(m, 1, Data(GenJson + "}"), new G74Options(new[] { 0.0, 0.05, 0.1, 1.0, 5.0 }));
        Assert.Equal(G74Status.Ok, r.Status);
        double i0 = C / 0.2 * IBaseKa;                            // E = c, X''d = 0.2 pu on 100 MVA
        Assert.Equal(i0, r.IkppKa, 9);
        // E/X''d, E/X'd, E/Xd with the same internal voltage E = c.
        double isub = C / 0.2, itr = C / 0.3, iss = C / 1.2;
        for (int k = 0; k < r.Times.Count; k++)
        {
            double t = r.Times[k];
            double expected = ((isub - itr) * Math.Exp(-t / 0.05) + (itr - iss) * Math.Exp(-t / 1.0) + iss) * IBaseKa;   // test-side Math.Exp is fine
            Assert.Equal(expected, r.BreakKa[k], 9);
        }
        Assert.Equal(i0, r.BreakKa[0], 12);                       // t = 0 is the initial current
        Assert.Equal(C / 1.2 * IBaseKa, r.SteadyKa, 12);         // steady = E / Xd
    }

    // ---- degenerate agreement with the incumbent: decay off, no motors ----
    [Fact]
    public void Decay_off_matches_iec60909_bit_for_bit_on_shared_quantities()
    {
        GridModel m = Case(Sync(0.2));
        Iec60909.Duty d = Iec60909.ThreePhase(m, 1);
        G74BusResult r = G74Method.Compute(m, 1, G74Data.Empty, new G74Options(new[] { 0.1 }, Decay: false));
        Assert.Equal(G74Status.Ok, r.Status);
        Assert.Equal(BitConverter.DoubleToInt64Bits(d.IkppKa), BitConverter.DoubleToInt64Bits(r.IkppKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(d.PeakIpKa), BitConverter.DoubleToInt64Bits(r.PeakIpKa));
        Assert.Equal(BitConverter.DoubleToInt64Bits(d.Kappa), BitConverter.DoubleToInt64Bits(r.Kappa));
        Assert.Equal(d.IkppKa, r.BreakKa[0], 12);
        Assert.Equal(d.IkppKa, r.SteadyKa, 12);
    }

    // ---- a source with a motor and a converter at the fault bus: hand sums ----
    [Fact]
    public void Machine_motor_and_converter_sum_by_hand()
    {
        var conv = new Generator(Bus: 1, Pg: 0.0, MBase: 50.0, Name: "PV", Type: "solar", ConverterInterfaced: true, ConverterFaultCurrentPu: 1.2);
        GridModel m = Case(Sync(0.2), conv);
        string json = GenJson + ",\"motors\":[{\"name\":\"M1\",\"bus\":1,\"ratedMva\":10,\"lockedRotorCurrentRatio\":6,\"decay\":\"exponential\",\"timeConstantS\":0.04}]}";
        var opt = new G74Options(new[] { 0.0, 0.1 }, BreakTimeSeconds: 0.1);
        G74BusResult r = G74Method.Compute(m, 1, Data(json), opt);
        Assert.Equal(G74Status.Ok, r.Status);

        double gen = C / 0.2;                         // pu on 100 MVA
        double motorZ = (1.0 / 6.0) * 100.0 / 10.0;   // Z_M = 1/(I_LR/I_rM) on 10 MVA, on 100 MVA
        double mot = C / motorZ;                      // 0.66 pu
        double cv = 1.2 * 50.0 / 100.0;               // k x rated, pu on 100 MVA, at the fault bus (|Zkj| = |Zkk|)
        Assert.Equal((gen + mot + cv) * IBaseKa, r.IkppKa, 9);
        Assert.Equal((gen + mot + cv) * IBaseKa, r.BreakKa[0], 9);

        double t = 0.1;
        double genT = ((C / 0.2 - C / 0.3) * Math.Exp(-t / 0.05) + (C / 0.3 - C / 1.2) * Math.Exp(-t / 1.0) + C / 1.2);
        double motT = mot * Math.Exp(-t / 0.04);
        Assert.Equal((genT + motT + cv) * IBaseKa, r.BreakKa[1], 9);
        Assert.Equal((genT + motT + cv) * IBaseKa, r.BreakAtTbKa, 9);
        Assert.Equal(C / 1.2 * IBaseKa + cv * IBaseKa, r.SteadyKa, 9);   // motor decays to zero; converter constant

        Assert.Equal(new[] { "synchronous", "converter", "induction-motor" }.OrderBy(x => x), r.Contributions.Select(c => c.Kind).OrderBy(x => x));
        Assert.Contains("current-limited positive sequence", r.ConverterModel);
        Assert.Contains(r.Awaiting, s => s.Contains("AWAITING STANDARD TEXT"));
        // The converter does not decay.
        G74Contribution cc = r.Contributions.Single(x => x.Kind == "converter");
        Assert.Equal(cc.IkppKa, cc.AtBreakTimeKa, 12);
    }

    [Fact]
    public void Motor_with_iec_q_decay_uses_the_incumbent_factor()
    {
        GridModel m = Case(Sync(0.2));
        string json = "{\"schema\":\"gridsim-g74-data/1\",\"motors\":[{\"name\":\"M1\",\"bus\":1,\"ratedMva\":10,\"lockedRotorCurrentRatio\":6,\"decay\":\"iec60909-q\",\"ratedMw\":8,\"polePairs\":2}]}";
        G74BusResult r = G74Method.Compute(m, 1, Data(json), new G74Options(new[] { 0.1 }, Decay: true));
        // The machine needs decay data when decay is on: INCOMPLETE, not defaulted.
        Assert.Equal(G74Status.Incomplete, r.Status);
        G74BusResult off = G74Method.Compute(m, 1, Data(json), new G74Options(new[] { 0.1 }, Decay: false));
        Assert.Equal(G74Status.Ok, off.Status);
        double mot = C / ((1.0 / 6.0) * 10.0) * IBaseKa;
        Assert.Equal(mot, off.Contributions.Single(c => c.Kind == "induction-motor").IkppKa, 9);
    }

    [Fact]
    public void Missing_decay_data_is_incomplete_and_withholds_numbers()
    {
        GridModel m = Case(Sync(0.2));
        G74BusResult r = G74Method.Compute(m, 1, G74Data.Empty, new G74Options(new[] { 0.1 }));
        Assert.Equal(G74Status.Incomplete, r.Status);
        Assert.Equal("INCOMPLETE", r.Verdict);
        Assert.True(double.IsNaN(r.BreakKa[0]));
        Assert.Contains(r.Reasons, s => s.Contains("no decay data") && s.Contains("none is defaulted"));
    }

    [Fact]
    public void Synthesised_default_xd_is_incomplete_when_decay_is_asked_for()
    {
        GridModel m = Case(new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, Name: "G1"));   // no X''d: synthesised default
        G74BusResult r = G74Method.Compute(m, 1, Data(GenJson + "}"), new G74Options(new[] { 0.1 }));
        Assert.Equal(G74Status.Incomplete, r.Status);
        Assert.Contains(r.Reasons, s => s.Contains("synthesised default"));
    }

    [Fact]
    public void Unknown_bus_fails_and_bad_data_is_refused()
    {
        GridModel m = Case(Sync(0.2));
        Assert.Equal(G74Status.Failed, G74Method.Compute(m, 9, G74Data.Empty, new G74Options(new[] { 0.1 })).Status);
        Assert.Equal(G74Status.Failed, G74Method.Compute(m, 1, G74Data.Empty, new G74Options(new[] { -1.0 }, Decay: false)).Status);
        Assert.Throws<FormatException>(() => G74Data.Parse("{\"schema\":\"wrong\"}"));
        Assert.Throws<FormatException>(() => G74Data.Parse(GenJson.Replace("\"xdTransPu\":0.3", "\"xdTransPu\":2.0") + "}"));   // X'd above Xd
        Assert.Throws<FormatException>(() => G74Data.Parse("{\"schema\":\"gridsim-g74-data/1\",\"motors\":[{\"name\":\"M\",\"bus\":1,\"ratedMva\":1,\"lockedRotorCurrentRatio\":6}]}"));   // no decay model
    }

    [Fact]
    public void Breaker_check_uses_the_g74_break_with_signed_margin()
    {
        GridModel m = Case(Sync(0.2));
        var opt = new G74Options(new[] { 0.1 }, Breaker: new Iec60909.BreakerRating(145.0, 100.0, 20.0, 20.0));
        G74BusResult r = G74Method.Compute(m, 1, Data(GenJson + "}"), opt);
        Assert.NotNull(r.Breaker);
        Assert.Equal(20.0 - r.BreakAtTbKa, r.Breaker!.BreakingMarginKa, 9);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        GridModel m = Case(Sync(0.2));
        var opt = new G74Options(new[] { 0.05, 0.1 });
        G74BusResult a = G74Method.Compute(m, 1, Data(GenJson + "}"), opt), b = G74Method.Compute(m, 1, Data(GenJson + "}"), opt);
        for (int i = 0; i < a.BreakKa.Count; i++) Assert.Equal(BitConverter.DoubleToInt64Bits(a.BreakKa[i]), BitConverter.DoubleToInt64Bits(b.BreakKa[i]));
    }

    // ---- series faults: sequence result against an independent phase-domain solve ----
    private static readonly Complex Aop = new(-0.5, Math.Sqrt(3.0) / 2.0);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Open_conductor_matches_direct_phase_domain_solve(int open)
    {
        // Lossless two-bus line with load: source E behind j0.1, line j0.05, load 1.0 + j0.2 at the end (per phase, any
        // consistent unit). Across the break (the closed series element removed) Zs1 = Zs2 = 0.1j + 0.05j + load, and the
        // zero-sequence path differs (0.3j + 3 on top of the load).
        Complex load = new(1.0, 0.2);
        Complex zs1 = new Complex(0, 0.15) + load, zs2 = zs1, zs0 = new Complex(0, 0.45) + load + 0.5;
        Complex e = Complex.One;
        Complex iPre = e / zs1;
        SeriesFaultResult r = SeriesFault.OpenConductors(iPre, zs1, zs2, zs0, open);

        // Phase domain: V_abc = E_abc - Zs_abc I_abc, Zs_abc = T diag(Zs1, Zs2, Zs0) T^-1; boundary conditions imposed directly.
        Complex a = Aop, a2 = a * a;
        Complex[,] t = { { 1, 1, 1 }, { a2, a, 1 }, { a, a2, 1 } };
        Complex[] d = { zs1, zs2, zs0 };
        var z = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
            {
                Complex s = 0;
                for (int k = 0; k < 3; k++) s += t[p, k] * d[k] * Complex.Conjugate(t[q, k]) / 3.0;
                z[p, q] = s;
            }
        Complex[] eabc = { e, a2 * e, a * e };
        Complex[] ia = new Complex[3];
        if (open == 1)
        {
            // Ia = 0; Vb = Vc = 0: [Zbb Zbc; Zcb Zcc] [Ib; Ic] = [Eb; Ec]
            Solve2(z[1, 1], z[1, 2], z[2, 1], z[2, 2], eabc[1], eabc[2], out ia[1], out ia[2]);
            ia[0] = 0;
        }
        else
        {
            // Ib = Ic = 0; Va = 0: Ia = Ea / Zaa
            ia[0] = eabc[0] / z[0, 0];
        }
        Assert.Equal(ia[0].Real, r.Ia.Real, 10); Assert.Equal(ia[0].Imaginary, r.Ia.Imaginary, 10);
        Assert.Equal(ia[1].Real, r.Ib.Real, 10); Assert.Equal(ia[1].Imaginary, r.Ib.Imaginary, 10);
        Assert.Equal(ia[2].Real, r.Ic.Real, 10); Assert.Equal(ia[2].Imaginary, r.Ic.Imaginary, 10);
    }

    [Fact]
    public void One_open_conductor_closed_form_with_equal_sequences_halves_nothing()
    {
        // Zs1 = Zs2 = Zs0 = Z: I1 = E / (Z + Z/2) = (2/3) E / Z = (2/3) Ipre; I2 = I0 = -I1 / 2.
        Complex z = new(0.3, 1.0), ipre = new(1.2, -0.4);
        SeriesFaultResult r = SeriesFault.OpenConductors(ipre, z, z, z, 1);
        Assert.Equal((2.0 / 3.0 * ipre).Real, r.I1.Real, 12);
        Assert.Equal((2.0 / 3.0 * ipre).Imaginary, r.I1.Imaginary, 12);
        Assert.Equal((-r.I1 / 2.0).Real, r.I2.Real, 12);
        Assert.Equal(0.0, r.Ia.Magnitude, 12);
    }

    private static void Solve2(Complex a, Complex b, Complex c, Complex d, Complex r1, Complex r2, out Complex x1, out Complex x2)
    {
        Complex det = a * d - b * c;
        x1 = (r1 * d - b * r2) / det;
        x2 = (a * r2 - c * r1) / det;
    }

    // ---- simultaneous faults ----
    private static SequencePortImpedances Ports(Complex[,] p, Complex[,] n, Complex[,] z) => new(p, n, z);

    [Fact]
    public void Single_port_slg_and_ll_match_sequence_closed_forms()
    {
        Complex z1 = new(0.01, 0.10), z2 = new(0.01, 0.12), z0 = new(0.03, 0.30), e = new(1.05, 0);
        var zs = Ports(new[,] { { z1 } }, new[,] { { z2 } }, new[,] { { z0 } });
        PortFaultResult g = SimultaneousFault.Solve(zs, new[] { e }, new[] { PortFault.LineToGroundA })[0];
        Complex i1 = e / (z0 + z1 + z2);
        Assert.Equal((3.0 * i1).Real, g.PhaseCurrents[0].Real, 10);
        Assert.Equal((3.0 * i1).Imaginary, g.PhaseCurrents[0].Imaginary, 10);
        Assert.Equal(i1.Real, g.I1.Real, 10);

        PortFaultResult ll = SimultaneousFault.Solve(zs, new[] { e }, new[] { PortFault.LineToLineBC })[0];
        Complex i1ll = e / (z1 + z2);
        Complex ib = (Aop * Aop - Aop) * i1ll;                    // Ib = (a^2 - a) I1 = -j sqrt3 I1
        Assert.Equal(ib.Real, ll.PhaseCurrents[1].Real, 10);
        Assert.Equal(ib.Imaginary, ll.PhaseCurrents[1].Imaginary, 10);
        Assert.Equal(0.0, ll.PhaseCurrents[0].Magnitude, 10);
    }

    [Fact]
    public void Two_port_three_phase_faults_solve_the_positive_network_by_hand()
    {
        // Positive network only matters for balanced faults: V = E - Z I = 0 at both ports, I = Z^-1 E.
        Complex z11 = new(0, 0.2), z22 = new(0, 0.3), z12 = new(0, 0.1);
        Complex[,] zp = { { z11, z12 }, { z12, z22 } };
        var zs = Ports(zp, zp, zp);
        Complex e1 = 1.0, e2 = 1.0;
        PortFaultResult[] r = SimultaneousFault.Solve(zs, new[] { e1, e2 }, new[] { PortFault.ThreePhase, PortFault.ThreePhase });
        Complex det = z11 * z22 - z12 * z12;
        Complex i1 = (e1 * z22 - z12 * e2) / det, i2 = (z11 * e2 - z12 * e1) / det;
        Assert.Equal(i1.Imaginary, r[0].I1.Imaginary, 10);
        Assert.Equal(i2.Imaginary, r[1].I1.Imaginary, 10);
        Assert.Equal(0.0, r[0].PhaseVoltages[0].Magnitude, 10);
        Assert.Equal(0.0, r[1].PhaseVoltages[2].Magnitude, 10);
    }

    [Fact]
    public void Two_port_same_phase_earth_faults_with_equal_sequences_reduce_to_a_2x2()
    {
        // Z1 = Z2 = Z0 = Z at every port pair: Z_abc = Z (x) identity, phases decouple, phase a: [Z11 Z12; Z12 Z22] I = E.
        Complex z11 = new(0.02, 0.2), z22 = new(0.03, 0.3), z12 = new(0.01, 0.1);
        Complex[,] zp = { { z11, z12 }, { z12, z22 } };
        PortFaultResult[] r = SimultaneousFault.Solve(Ports(zp, zp, zp), new Complex[] { 1.0, 1.0 }, new[] { PortFault.LineToGroundA, PortFault.LineToGroundA });
        Complex det = z11 * z22 - z12 * z12;
        Complex ia1 = (z22 - z12) / det, ia2 = (z11 - z12) / det;
        Assert.Equal(ia1.Real, r[0].PhaseCurrents[0].Real, 10);
        Assert.Equal(ia2.Imaginary, r[1].PhaseCurrents[0].Imaginary, 10);
        Assert.Equal(0.0, r[0].PhaseCurrents[1].Magnitude, 10);
    }

    [Fact]
    public void Uncoupled_ports_equal_independent_single_port_faults()
    {
        Complex z1 = new(0.01, 0.1), z2 = new(0.01, 0.1), z0 = new(0.03, 0.3);
        Complex[,] d(Complex x, Complex y) => new[,] { { x, Complex.Zero }, { Complex.Zero, y } };
        var two = Ports(d(z1, z1 * 2), d(z2, z2 * 2), d(z0, z0 * 2));
        PortFaultResult[] r = SimultaneousFault.Solve(two, new Complex[] { 1.0, 1.0 }, new[] { PortFault.LineToGroundA, PortFault.DoubleLineToGroundBC });
        PortFaultResult s0 = SimultaneousFault.Solve(Ports(new[,] { { z1 } }, new[,] { { z2 } }, new[,] { { z0 } }), new Complex[] { 1.0 }, new[] { PortFault.LineToGroundA })[0];
        PortFaultResult s1 = SimultaneousFault.Solve(Ports(new[,] { { z1 * 2 } }, new[,] { { z2 * 2 } }, new[,] { { z0 * 2 } }), new Complex[] { 1.0 }, new[] { PortFault.DoubleLineToGroundBC })[0];
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(s0.PhaseCurrents[k].Real, r[0].PhaseCurrents[k].Real, 10);
            Assert.Equal(s1.PhaseCurrents[k].Imaginary, r[1].PhaseCurrents[k].Imaginary, 10);
        }
    }

    [Fact]
    public void No_fault_ports_carry_no_current()
    {
        Complex[,] zp = { { new Complex(0, 0.2) } };
        PortFaultResult r = SimultaneousFault.Solve(Ports(zp, zp, zp), new Complex[] { 1.0 }, new[] { PortFault.None })[0];
        Assert.All(r.PhaseCurrents, c => Assert.Equal(0.0, c.Magnitude, 12));
    }
}
