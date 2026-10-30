using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 6: REGC_B (voltage-source generator/converter) and REEC_D (electrical control) from the WECC REMWG
/// memo Rev. 27b (Pourbeik, 2/16/23), and the EPRI REGC_B/REEC_D test protocol (8/17/2020) network realised in GridSim.
/// REGC_B: at rest bit for bit with fixed commands; the source lag Te is exact (the terminal current's deviation from the
/// command decays by exp(-dt/Te) per step); a voltage step moves REGC_B's current at once by -dV/Z where REGC_A's does not
/// move; the interface current limit holds |I| = Imax with the chosen priority; rrpwr bounds a rising magnitude of either
/// sign (current or power). REEC_D: every flag combination starts at rest; Iqmin = Iqmax for a negative VDLq; Ipmin = -Ke
/// Ipmax; blocking zeroes both commands and the release delay is exact in steps; Thld holds Iqcmd_bl, Thld2 holds Ipcmd.
/// </summary>
public class WeccRegcBReecDTests
{
    private const double Dt = 0.005;
    private readonly ITestOutputHelper _out;

    public WeccRegcBReecDTests(ITestOutputHelper output) => _out = output;

    private static ReecdParameters Reecd() => new()
    {
        Vdip = 0.7, Vup = 1.2, Trv = 0.02, Kqv = 2.0, Qvmax = 0.6, Qvmin = -0.6, Imax = 1.2, Kqi = 1.5, Kqp = 1.0, Kvp = 1.0, Kvi = 40.0,
        Pmax = 1.1, Rc = 0.01, Xc = 0.08, Tr1 = 0.02, Kc = 0.02,
        Vq = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Iq = new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, 1.05, 1.05, 1.05, 0.0 },
        Vp = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Ip = new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, 1.05, 1.05, 1.05, 0.0 },
    };

    private static RegcbParameters Regcb() => new() { Tg = 0.02, Rrpwr = 0.5, Imax = 1.2, Tfltr = 0.02, Iqrmax = 999, Iqrmin = -999, Te = 0.02, Re = 0.0025, Xe = 0.2 };

    private static WeccConverterState Unit(WeccPlant plant, double p0 = 0.6, double q0 = 0.1, double v0 = 1.0) =>
        new(new RmsConverterUnit(1, "b", RatingPu: 1.0, P0: p0, Q0: q0, V0: v0, new RmsConverterParameters()) { Wecc = plant });

    // One step against a fixed terminal voltage (an infinite bus at the terminal): commit, then advance. Returns the terminal
    // current (machine base = system base here, rating 1).
    private static Complex Drive(WeccConverterState s, double t, Complex v)
    {
        Complex inj = s.Commit(v);
        s.Step(t, v, new[] { v }, Dt);
        return s.TerminalCurrent(inj, v);
    }

    [Fact]
    public void Regc_b_alone_is_at_rest_bit_for_bit_and_injects_the_solved_current()
    {
        WeccConverterState s = Unit(new WeccPlant(null) { RegcB = Regcb() });
        Complex i0 = Drive(s, 0.0, Complex.One);
        Assert.Equal(0.6, i0.Real, 14);
        Assert.Equal(-0.1, i0.Imaginary, 14);
        for (int k = 1; k < 2000; k++) Assert.Equal(i0, Drive(s, k * Dt, Complex.One));
    }

    public static IEnumerable<object[]> Flags() =>
        from pf in new[] { 0, 1 } from vf in new[] { 0, 1 } from qf in new[] { 0, 1 } from vc in new[] { 0, 1 } from pq in new[] { 0, 1 }
        select new object[] { pf, vf, qf, vc, pq };

    [Theory]
    [MemberData(nameof(Flags))]
    public void Every_reec_d_flag_combination_with_regc_b_starts_at_rest(int pf, int vf, int qf, int vc, int pq)
    {
        // VFlag = 0 with QFlag = 1 holds Vsel = Vref1 + Qlim = Vcomp, so Vref1 = Vcomp0 - Q0 makes the needed Qlim the solved Q
        // (which constant power factor control also gives).
        double vcomp0 = vc == 1 ? (Complex.One - new Complex(0.01, 0.08) * new Complex(0.6, -0.1)).Magnitude : 1.0 + 0.02 * 0.1;
        ReecdParameters d = Reecd() with { PfFlag = pf, VFlag = vf, QFlag = qf, VcmpFlag = vc, PqFlag = pq, Vref1 = vf == 0 && qf == 1 ? vcomp0 - 0.1 : 0.0 };
        WeccConverterState s = Unit(new WeccPlant(null) { RegcB = Regcb(), ReecD = d });
        Complex i0 = Drive(s, 0.0, Complex.One);
        double worst = 0.0;
        for (int k = 1; k < 1000; k++) worst = Math.Max(worst, (Drive(s, k * Dt, Complex.One) - i0).Magnitude);
        Assert.True(worst <= 1e-13, $"moved {worst}");
    }

    [Fact]
    public void The_source_lag_te_is_exact_and_a_voltage_step_moves_regc_b_current_at_once_but_not_regc_a()
    {
        WeccConverterState b = Unit(new WeccPlant(null) { RegcB = Regcb() with { Tfltr = 0.0 } });
        WeccConverterState a = Unit(new WeccPlant(new RegcaParameters { Lvplsw = 0, Khv = 0.0 }));
        Complex ib0 = Drive(b, 0.0, Complex.One), ia0 = Drive(a, 0.0, Complex.One);
        var v1 = new Complex(0.95, 0.0);
        // The first network solution after the step: REGC_B's source is held, so the current moves by -dV / Z at once.
        Complex ib1 = b.TerminalCurrent(b.Commit(v1), v1), ia1 = a.TerminalCurrent(a.Commit(v1), v1);
        Complex z = new(0.0025, 0.2);
        Assert.True((ib1 - (ib0 - (v1 - 1.0) / z)).Magnitude <= 1e-14, $"{ib1}");
        Assert.Equal(ia0, ia1);   // a current source does not move until its lag does
        // Then the source follows its target with the exact lag: with constant commands and Tfltr = 0 the target is v1 + Z I0,
        // so the current's deviation from I0 decays by exp(-dt / Te) per step.
        b.Step(Dt, v1, new[] { v1 }, Dt);
        Complex dev = ib1 - ib0;
        double a1 = Math.Exp(-Dt / 0.02);
        for (int k = 2; k < 60; k++)
        {
            Complex ik = Drive(b, k * Dt, v1);
            dev *= a1;
            Assert.True((ik - ib0 - dev).Magnitude <= 1e-13, $"step {k}: {ik - ib0} vs {dev}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_interface_current_limit_holds_imax_with_the_chosen_priority(int pq)
    {
        WeccConverterState s = Unit(new WeccPlant(null) { RegcB = Regcb() with { PqFlag = pq } }, p0: 0.9, q0: 0.0);
        Drive(s, 0.0, Complex.One);
        var v = new Complex(0.3, 0.0);   // a deep sag: (E - V) / Z would be far above Imax
        Complex i = s.TerminalCurrent(s.Commit(v), v);
        Assert.Equal(1.2, i.Magnitude, 12);
        Complex free = (s.RegcB!.Source - v) / s.RegcB.Impedance;
        if (pq == 0) Assert.Equal(Math.Min(1.2, -free.Imaginary), -i.Imaginary, 12);   // reactive first
        else Assert.Equal(Math.Min(1.2, free.Real), i.Real, 12);                       // active first
    }

    [Theory]
    [InlineData(0, 0.6)]
    [InlineData(1, 0.6)]
    [InlineData(0, -0.6)]
    public void Rrpwr_bounds_a_rising_magnitude_of_current_or_power_on_either_sign(int rateFlag, double p0)
    {
        // A held REEC_D command of the initial current, after the s1 state is knocked to zero: it must recover at exactly
        // rrpwr per second (pu/s of current, or of power for RateFlag 1 at 1 pu voltage), with the sign of the command.
        var rb = new Regcb(Regcb() with { RateFlag = rateFlag, Rrpwr = 0.5 }, Complex.One, p0, 0.0);
        rb.Step(0.0, 0.0, Complex.One, 0.0, 10.0);   // one long step: s1 -> 0 (lag), the ramp limit allows 5 pu
        Assert.Equal(0.0, rb.Ip, 12);
        for (int k = 1; k <= 40; k++)
        {
            rb.Step(p0, 0.0, Complex.One, 0.0, Dt);
            Assert.Equal(Math.Sign(p0) * 0.5 * Dt * k, rb.Ip, 12);
        }
    }

    [Fact]
    public void Reec_d_limits_follow_the_memo_negative_vdlq_ke_and_blocking_with_its_delay()
    {
        // Negative VDLq at high voltage: Iqmin = Iqmax < 0 forces absorption.
        ReecdParameters d = Reecd() with { Iq = new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, -0.3, -0.3, -0.3, -0.3 }, Vup = 1.3, Ke = 0.5, QFlag = 1 };
        var r = new Reecd(d, Complex.One, new Complex(0.6, -0.1), 0.6, 0.1, 0.6, 0.1);
        var u = new ReecInputs(1.0, 0.6, 0.1, 0.6, r.QextInitial, 1.0) { VtPhasor = Complex.One, It = new Complex(0.6, -0.1) };
        ReecOutputs o0 = r.Evaluate(u, 0.0, Dt);
        Assert.Equal(-0.5 * o0.Ipmax, o0.Ipmin, 15);   // Ipmin = -Ke Ipmax
        // Drive Vt_filt above 1.15 (VDLq = -0.3 there): Iqmax = Iqmin = -0.3 and Iqcmd = -0.3.
        var uh = u with { Vt = 1.2, VtPhasor = new Complex(1.2, 0.0) };
        for (int k = 0; k < 200; k++) { r.UpdateDipState(k * Dt, 1.2); r.Advance(uh, r.Evaluate(uh, k * Dt, Dt), Dt); }
        ReecOutputs oh = r.Evaluate(uh, 1.0, Dt);
        Assert.Equal(-0.3, oh.Iqmax, 12);
        Assert.Equal(-0.3, oh.Iqmin, 12);
        Assert.Equal(-0.3, oh.Iqcmd, 12);

        // Blocking: Vt_filt <= vblkl blocks (both commands 0); after recovery the limits stay 0 for exactly Tblk_delay.
        ReecdParameters db = Reecd() with { Trv = 0.0, Vblkl = 0.5, Vblkh = 1.25, TblkDelay = 0.05, Vdip = 0.4 };
        var rb = new Reecd(db, Complex.One, new Complex(0.6, -0.1), 0.6, 0.1, 0.6, 0.1);
        var ub = new ReecInputs(1.0, 0.6, 0.1, 0.6, rb.QextInitial, 1.0) { VtPhasor = Complex.One, It = new Complex(0.6, -0.1) };
        var ul = ub with { Vt = 0.45, VtPhasor = new Complex(0.45, 0.0) };
        int k2 = 0;
        rb.UpdateDipState(0.0, 1.0); rb.Advance(ul, rb.Evaluate(ul, 0.0, Dt), Dt); k2++;   // Trv = 0: Vt_filt = 0.45 next step
        for (; k2 < 20; k2++)
        {
            rb.UpdateDipState(k2 * Dt, 0.45);
            ReecOutputs ob = rb.Evaluate(ul, k2 * Dt, Dt);
            Assert.True(ob.Blocked && ob.Ipcmd == 0.0 && ob.Iqcmd == 0.0);
            rb.Advance(ul, ob, Dt);
        }
        // The voltage returns: step 0 below still sees Vt_filt = 0.45 (blocked by voltage) and its advance brings Vt_filt to
        // 1.0; from step 1 the release delay runs, 0.05 s / 0.005 s = 10 steps (1..10), so step 11 is the first released.
        int released = -1;
        for (int j = 0; j < 40; j++)
        {
            double t = (k2 + j) * Dt;
            rb.UpdateDipState(t, 1.0);
            ReecOutputs ob = rb.Evaluate(ub, t, Dt);
            rb.Advance(ub, ob, Dt);
            if (!ob.Blocked) { released = j; break; }
        }
        Assert.Equal(11, released);
    }

    [Fact]
    public void Reec_d_thld_holds_iqcmd_bl_and_thld2_holds_ipcmd_for_whole_steps()
    {
        ReecdParameters d = Reecd() with { Trv = 0.0, Thld = 0.1, Thld2 = 0.05, Vdip = 0.8, Kqv = 2.0 };
        var r = new Reecd(d, Complex.One, new Complex(0.6, -0.1), 0.6, 0.1, 0.6, 0.1);
        var un = new ReecInputs(1.0, 0.6, 0.1, 0.6, r.QextInitial, 1.0) { VtPhasor = Complex.One, It = new Complex(0.6, -0.1) };
        var ud = un with { Vt = 0.6, VtPhasor = new Complex(0.6, 0.0), Pe = 0.36 };
        ReecOutputs last = default;
        r.UpdateDipState(0.0, 1.0); r.Advance(ud, r.Evaluate(ud, 0.0, Dt), Dt);   // Trv = 0: Vt_filt = 0.6 from the next step
        for (int k = 1; k < 20; k++) { r.UpdateDipState(k * Dt, 0.6); last = r.Evaluate(ud, k * Dt, Dt); r.Advance(ud, last, Dt); }
        Assert.True(last.Dip);
        int held1 = 0, held2 = 0;
        for (int j = 0; j < 60; j++)
        {
            double t = (20 + j) * Dt;
            r.UpdateDipState(t, 1.0);
            ReecOutputs o = r.Evaluate(un, t, Dt);
            if (o.Dip) last = o;   // the first step still sees Vt_filt = 0.6: the last dip values are its
            else
            {
                if (o.IqcmdBl == last.IqcmdBl) held1++;
                if (o.Ipcmd == last.Ipcmd && o.Ipmax == last.Ipmax) held2++;
            }
            r.Advance(un, o, Dt);
        }
        Assert.Equal(20, held1);   // Thld = 0.1 s = 20 steps
        Assert.Equal(10, held2);   // Thld2 = 0.05 s = 10 steps
    }

    // ---------------------------------------------------------------- network

    internal static List<SubtransientInit.MachineData> Case9Data(GridModel m, WeccPlant plant)
    {
        var xdp = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        return m.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = plant } : d;
        }).ToList();
    }

    internal static WeccPlant BPlant() => new(null) { RegcB = Regcb() with { Imax = 1.3 }, ReecD = Reecd() with { Imax = 1.3, QFlag = 1, Vdip = 0.85, Vup = 1.15 } };

    [Fact]
    public void In_wscc9_a_regc_b_reec_d_unit_starts_at_rest_rides_through_a_fault_and_is_deterministic()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        List<SubtransientInit.MachineData> data = Case9Data(m, BPlant());
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 3, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 3.0);
        RmsConverterTrace c = rest.Converters![0];
        Assert.True(c.ActivePowerPu.Max(x => Math.Abs(x - c.ActivePowerPu[0])) <= 1e-9);
        Assert.True(c.TerminalVoltagePu.Max(x => Math.Abs(x - c.TerminalVoltagePu[0])) <= 1e-9);
        Assert.True(rest.Machines.Max(mt => mt.SpeedPu.Max(Math.Abs)) <= 1e-9);
        MultiMachineSubtransient.Result r1 = RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 3.0);
        MultiMachineSubtransient.Result r2 = RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 3.0);
        Assert.True(r1.StayedInSynchronism);
        Assert.Equal(0, r1.ConverterIterationFailures);
        Assert.Equal(WeccRenewableDigestTests.Digest(r1), WeccRenewableDigestTests.Digest(r2));
        Assert.Contains(r1.Converters![0].InDipMode, x => x);
        // After the fault the machines (no governors, no damping in this data) drift off nominal frequency. REGC_B's source is in
        // the terminal frame of each network solution (DECISIONS D36), so that rotation leaves no error: at 3 s P is 0.846687
        // (-0.003313, a transient that converges as the step shrinks: -0.002909, -0.003313, -0.003096, -0.002998 for dt = 0.01,
        // 0.005, 0.0025, 0.00125 s) and at 15 s 0.850195. The start-of-step hold it replaced (D32) left 0.829680 (-0.0203) at 3 s.
        Assert.True(Math.Abs(r1.Converters[0].ActivePowerPu[^1] - c.ActivePowerPu[0]) <= 0.004, $"P {r1.Converters[0].ActivePowerPu[^1]}");
    }

    [Fact]
    public void Regc_b_reec_d_fault_run_has_the_pinned_digest()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, Case9Data(m, BPlant()), 9, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        string d = WeccRenewableDigestTests.Digest(r);
        // Re-pinned with D36 (exact network solution, terminal-voltage frame), from 3921F2C5...F3EF of the start-of-step frame hold
        // (D32), whose off-nominal frequency error the new frame removes: the same run's P at 3 s moved 0.829680 -> 0.846687.
        Assert.True(d == "8D9FA62B66AA0CECAB011434C4F7AEC8300BF79E9F2BCCE3F664BDD68C505DEB", "digest " + d);
    }

    // ---------------------------------------------------------------- DYR

    internal const string BRecords =
          "     2 'REGC_B' 1  0.02 999.0 -999.0 0.02 0.5 0 0.02 1.2 0 0.0025 0.2 /\n"
        + "     2 'REEC_D' 1  0 1 1 0 0 1   0.0 0.7 1.2 0.02 -0.05 0.05 5.0 1.05 -1.05 0.0 0.5 0.5 0.5 0.05 1.0 -1.0 1.1 0.9 1.0 1.5 1.0 40.0 0.0\n"
        + "   0.02 99.0 -99.0 1.12 0.04 1.2 0.02 0.01 0.08 0.02 0.02 0.0\n"
        + "   0.3 0.0 0.4 0.2 0.7 1.0 0.9 1.1 1.0 1.1 1.05 1.1 1.15 1.05 1.2 1.05 1.25 1.05 1.3 0.0\n"
        + "   0.3 0.0 0.4 0.2 0.7 1.0 0.9 1.1 1.0 1.1 1.05 1.1 1.15 1.05 1.2 1.05 1.25 1.05 1.3 0.0\n"
        + "   0.5 1.2 0.5 /";

    [Fact]
    public void Regc_b_and_reec_d_records_map_in_the_specification_orders_and_run()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(BRecords), raw, "case14");
        WeccPlant plant = r.Renewables.Single().Value;
        Assert.Equal("REGC_B + REEC_D", plant.Chain);
        RegcbParameters b = plant.RegcB!;
        Assert.Equal((0.02, 999.0, -999.0, 0.02, 0.5, 0, 0.02, 1.2, 0, 0.0025, 0.2), (b.Tg, b.Iqrmax, b.Iqrmin, b.Tfltr, b.Rrpwr, b.RateFlag, b.Te, b.Imax, b.PqFlag, b.Re, b.Xe));
        ReecdParameters d = plant.ReecD!;
        Assert.Equal((0, 1, 1, 0, 0, 1), (d.PfFlag, d.VFlag, d.QFlag, d.PFlag, d.PqFlag, d.VcmpFlag));
        Assert.Equal((0.0, 0.7, 1.2, 0.02, -0.05, 0.05, 5.0, 1.05, -1.05, 0.0), (d.Mvab, d.Vdip, d.Vup, d.Trv, d.Dbd1, d.Dbd2, d.Kqv, d.Iqh1, d.Iql1, d.Vref0));
        Assert.Equal((0.5, 0.5, 0.5, 0.05, 1.0, -1.0, 1.1, 0.9, 1.0, 1.5), (d.Iqfrz, d.Thld, d.Thld2, d.Tp, d.Qvmax, d.Qvmin, d.Vmax, d.Vmin, d.Kqp, d.Kqi));
        Assert.Equal((1.0, 40.0, 0.0, 0.02, 99.0, -99.0, 1.12, 0.04, 1.2, 0.02), (d.Kvp, d.Kvi, d.Vref1, d.Tiq, d.DPmax, d.DPmin, d.Pmax, d.Pmin, d.Imax, d.Tpord));
        Assert.Equal((0.01, 0.08, 0.02, 0.02, 0.0), (d.Rc, d.Xc, d.Tr1, d.Kc, d.Ke));
        Assert.Equal(new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, d.Vq);
        Assert.Equal(new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, 1.05, 1.05, 1.05, 0.0 }, d.Ip);
        Assert.Equal((0.5, 1.2, 0.5), (d.Vblkl, d.Vblkh, d.TblkDelay));

        GridModel m = raw.Model;
        var data = m.Generators.Select((g, k) =>
        {
            var md = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            return r.Renewables.TryGetValue(k, out WeccPlant? wp) ? md with { Renewable = wp } : md;
        }).ToList();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        RmsConverterTrace c = rest.Converters![0];
        Assert.True(c.ActivePowerPu.Max(x => Math.Abs(x - c.ActivePowerPu[0])) <= 1e-9);
    }

    [Theory]
    [InlineData("'REGCB1'", "REGC_B")]
    [InlineData("'REGCBU1'", "REGC_B")]
    public void Psse_regc_b_records_are_refused_because_their_order_is_not_public(string head, string model)
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(BRecords.Replace("'REGC_B'", head, StringComparison.Ordinal)), raw, "case14"));
        Assert.Contains($"GridSim models {model}, but the order of the PSS/E", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Table 1 of the WECC REMWG memo Rev. 27b", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- EPRI test protocol network

    /// <summary>
    /// The EPRI REGC_B/REEC_D benchmark network (test protocol Fig. 1, Table 1): bus 1 (69 kV) - bus 2 (69 kV) r 0.0025 x 0.01,
    /// bus 2 - 10004 (12.47 kV) r 0.0008 x 0.10667, 10004 - 20004 r 0.10667 x X (per test). The protocol gives no load flow
    /// (no P, Q or voltage set points, no load size), so GridSim's realisation fixes them and says so: no load at 20004,
    /// bus 1 the slack at 1.0 pu, the converter at bus 20004 a PV unit at 1.0 pu with P given per test, MBASE 100 MVA.
    /// </summary>
    internal static GridModel ProtocolCase(double x, double genMva, double h, double pMw)
    {
        string X(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string text = "function mpc = epri\nmpc.version = '2';\nmpc.baseMVA = 100;\nmpc.bus = [\n"
            + "1 3 0 0 0 0 1 1.0 0 69 1 1.5 0.5;\n2 1 0 0 0 0 1 1.0 0 69 1 1.5 0.5;\n10004 1 0 0 0 0 1 1.0 0 12.47 1 1.5 0.5;\n20004 2 0 0 0 0 1 1.0 0 12.47 1 1.5 0.5;\n];\n"
            + "mpc.gen = [\n" + $"1 0 0 9999 -9999 1.0 {X(genMva)} 1 9999 -9999;\n20004 {X(pMw)} 0 200 -200 1.0 100 1 200 -200;\n];\n"
            + "mpc.branch = [\n1 2 0.0025 0.01 0 0 0 0 0 0 1 -360 360;\n2 10004 0.0008 0.10667 0 0 0 0 0 0 1 -360 360;\n"
            + $"10004 20004 0.10667 {X(x)} 0 0 0 0 0 0 1 -360 360;\n];\n";
        GridModel m = MatpowerParser.Parse(text, "epri-regcb-protocol", 60.0);
        var gens = m.Generators.Select(g => g.Bus == 1 ? g with { MBase = genMva, H = h } : g with { MBase = 100.0 }).ToList();
        return m.CopyWith(generators: gens);
    }


    internal static ReecdParameters ProtocolReecd() => new()
    {
        Vdip = 0.7, Vup = 1.1, Trv = 0.0, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 5.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0, Iqfrz = 0.5, Thld = 0.5,
        Thld2 = 0.5, Tp = 0.05, Qvmax = 1.0, Qvmin = -1.0, Vmax = 1.1, Vmin = 0.9, Kqp = 1.0, Kqi = 1.5, Kvp = 1.0, Kvi = 40.0, Vref1 = 0.0,
        Tiq = 0.02, DPmax = 99.0, DPmin = -99.0, Pmax = 1.12, Pmin = 0.04, Imax = 1.2, Tpord = 0.02, Rc = 0.01, Xc = 0.08, Tr1 = 0.02, Kc = 0.02, Ke = 0.0,
        Vq = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Iq = new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, 1.05, 1.05, 1.05, 0.0 },
        Vp = new[] { 0.3, 0.4, 0.7, 0.9, 1.0, 1.05, 1.15, 1.2, 1.25, 1.3 }, Ip = new[] { 0.0, 0.2, 1.0, 1.1, 1.1, 1.1, 1.05, 1.05, 1.05, 0.0 },
        Vblkl = 0.5, Vblkh = 1.2, TblkDelay = 0.5, PfFlag = 0, VFlag = 0, QFlag = 0, VcmpFlag = 0, PFlag = 0, PqFlag = 0,
    };

    /// <summary>Run one protocol test: an X = 0.005 pu fault on bus 2 for 8 cycles (a shunt of exactly 1/(j 0.005) applied
    /// as a load step and removed), step 0.00416 s as the protocol says. Returns the converter trace.</summary>
    internal static (MultiMachineSubtransient.Result R, double Dt) ProtocolRun(double x, double genMva, double h, double pMw, WeccPlant plant, double seconds = 3.0)
    {
        const double dt = 0.00416;
        GridModel m = ProtocolCase(x, genMva, h, pMw);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        double v2 = pf.Vm[pf.BusIds.ToList().IndexOf(2)];
        double q = 200.0 * v2 * v2 * 100.0;   // conj(dS)/|V0|^2 = -j200 pu: a shunt reactance of exactly 0.005 pu
        var events = new RmsEvent[] { new RmsLoadStep(24 * dt, 2, 0.0, q), new RmsLoadStep(56 * dt, 2, 0.0, -q) };
        var data = m.Generators.Select(g => g.Bus == 1
            ? new SubtransientInit.MachineData(h, 0.0, 2.0, 0.2, 0.18, 1.9, 0.5, 0.18, 7.0, 0.03, 0.5, 0.05, Xl: 0.15)
            : new SubtransientInit.MachineData(1.0, 0.0, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 7.0, 0.03, 0.5, 0.05) { Renewable = plant }).ToList();
        var exc = m.Generators.Select(g => g.Bus == 1 ? RmsExciter.FromSexs(1.0, 10.0, 200.0, 0.03, -8.0, 8.0) : null).ToList();
        return (RmsTransientStability.StudyEvents(m, pf, data, events, stepSeconds: dt, durationSeconds: seconds, exciters: exc), dt);
    }

    [Fact]
    public void Epri_protocol_tests_1_and_2_run_with_regc_a_and_regc_b_and_report_their_numbers()
    {
        // No published result accompanies the protocol (it lists tests and parameters only), so there is no reference number
        // to compare against; this records GridSim's numbers and checks what must hold: each run starts at rest, every step's
        // network iteration converges (REGC_B used to fail 1, 1, 110, 247 and 524 steps in 1A-2A before its exact network
        // solution, DECISIONS D36), and REGC_A (a) and REGC_B (b) return to their initial power and voltage. 2A runs 10 s: its
        // single 100 MVA machine (no governor, no load: it absorbs the converter's 50 MW) drifts to about -0.11 pu speed after the
        // fault, and REGC_B blocks twice on overvoltage (Vt_filt above vblkh = 1.2 at 0.28 s and 0.80 s: at each release its
        // reactive current state is still ramping down at Iqrmax = 0.5 pu/s), then recovers at rrpwr = 0.5 pu/s by about 3.4 s.
        var regca = new RegcaParameters { Tg = 0.02, Rrpwr = 0.5, Brkpt = 0.8, Zerox = 0.4, Lvpl1 = 1.2, Volim = 1.1, Lvpnt1 = 0.8, Lvpnt0 = 0.4,
            Iolim = -1.1, Tfltr = 0.02, Khv = 1.0, Iqrmax = 0.5, Iqrmin = -0.5, Accel = 0.9, Lvplsw = 0 };
        var regcb = new RegcbParameters { Tg = 0.02, Rrpwr = 0.5, RateFlag = 0, Imax = 1.2, Tfltr = 0.02, Iqrmax = 0.5, Iqrmin = -0.5, Te = 0.02, Re = 0.0025, Xe = 0.2 };
        var cases = new (string Name, double X, double Mva, double H, double P)[]
        {
            ("1A", 0.106, 10000.0, 5.0, 50.0), ("1B", 0.206, 10000.0, 5.0, 50.0), ("1C", 0.406, 10000.0, 5.0, 50.0), ("1D", 0.706, 10000.0, 5.0, 50.0),
            ("2A", 0.706, 100.0, 2.5, 50.0),
        };
        var sb = new StringBuilder();
        foreach ((string name, double x, double mva, double h, double p) in cases)
            foreach ((string tag, WeccPlant plant) in new[] { ("a", new WeccPlant(regca) { ReecD = ProtocolReecd() }), ("b", new WeccPlant(null) { RegcB = regcb, ReecD = ProtocolReecd() }) })
            {
                (MultiMachineSubtransient.Result r, double dt) = ProtocolRun(x, mva, h, p, plant, name == "2A" ? 10.0 : 3.0);
                RmsConverterTrace c = r.Converters![0];
                sb.AppendLine(FormattableString.Invariant($"Test {name}-{tag}: converter iteration failures {r.ConverterIterationFailures}"));
                Assert.Equal(0, r.ConverterIterationFailures);
                Assert.True(Math.Abs(c.ActivePowerPu[^1] - c.ActivePowerPu[0]) <= 1e-4, $"{name}-{tag} P {c.ActivePowerPu[0]} -> {c.ActivePowerPu[^1]}");
                Assert.True(Math.Abs(c.TerminalVoltagePu[^1] - c.TerminalVoltagePu[0]) <= 1e-3, $"{name}-{tag} V {c.TerminalVoltagePu[0]} -> {c.TerminalVoltagePu[^1]}");
                for (int k = 0; k < 24; k++) Assert.True(Math.Abs(c.ActivePowerPu[k] - c.ActivePowerPu[0]) <= 1e-9, $"{name}-{tag} not at rest");
                int k1 = 24 + 16, k2 = 56 + 1;
                double iMag(int k) => Math.Sqrt(c.ActiveCurrentPu[k] * c.ActiveCurrentPu[k] + c.ReactiveCurrentPu[k] * c.ReactiveCurrentPu[k]);
                sb.AppendLine(FormattableString.Invariant(
                    $"Test {name}-{tag}: P0 {c.ActivePowerPu[0]:R} Q0 {c.ReactivePowerPu[0]:R} V0 {c.TerminalVoltagePu[0]:R} | mid-fault (t={k1 * dt:F5}) P {c.ActivePowerPu[k1]:F6} Q {c.ReactivePowerPu[k1]:F6} V {c.TerminalVoltagePu[k1]:F6} |I| {iMag(k1):F6} | first step after clear P {c.ActivePowerPu[k2]:F6} Q {c.ReactivePowerPu[k2]:F6} V {c.TerminalVoltagePu[k2]:F6} | end (t={(c.ActivePowerPu.Count - 1) * dt:F3}) P {c.ActivePowerPu[^1]:F6} Q {c.ReactivePowerPu[^1]:F6} V {c.TerminalVoltagePu[^1]:F6} | min V {c.TerminalVoltagePu.Min():F6} max |I| {Enumerable.Range(0, c.ActivePowerPu.Count).Max(iMag):F6}"));
            }
        _out.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gridsim-epri-regcb-protocol.txt"), sb.ToString());
    }
}
