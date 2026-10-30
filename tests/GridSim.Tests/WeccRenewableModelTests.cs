using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 1: the block primitives, REGC_A and REEC_A/B/C driven on their own (a unit fed a chosen
/// terminal voltage, no network), against the block diagrams of EPRI 3002006525 (Figs. D-1..D-4, App. B). Every
/// expected value is computed here independently from the diagram's equations, not read back from the model: step
/// responses of the zero-order-hold lags, ramp limits, the LVACM / LVPL / HVRCM characteristics, the dip state machine
/// with its exact hold durations, the App. B current limit with the VDL tables in both priorities, the freezes, the
/// power reference ramp, the storage state of charge, and the fail-loud initialisation.
/// </summary>
public class WeccRenewableModelTests
{
    private const double Dt = 0.005;

    private static RegcaParameters Regc() => new()
    {
        Lvplsw = 1, Tg = 0.02, Rrpwr = 10.0, Brkpt = 0.9, Zerox = 0.4, Lvpl1 = 1.22, Volim = 1.2, Lvpnt1 = 0.8, Lvpnt0 = 0.4,
        Iolim = -1.3, Tfltr = 0.02, Khv = 0.7, Iqrmax = 9999.0, Iqrmin = -9999.0, Accel = 0.7,
    };

    private static ReecParameters ReecA() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, PFlag = 0, PqFlag = 0,
        Vdip = 0.9, Vup = 1.1, Trv = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0,
        Tp = 0.05, Qmax = 0.5, Qmin = -0.5, Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
        DPmax = 99.0, DPmin = -99.0, Pmax = 1.0, Pmin = 0.0, Imax = 1.2, Tpord = 0.02,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 },
    };

    private static WeccConverterState Unit(WeccPlant plant, double p0 = 0.8, double q0 = 0.1, double v0 = 1.02, double rating = 1.0) =>
        new(new RmsConverterUnit(1, "u", RatingPu: rating, P0: p0 * rating, Q0: q0 * rating, V0: v0, new RmsConverterParameters()) { Wecc = plant });

    // One kernel step at a terminal voltage magnitude v (angle 0): commit, then advance the controls.
    private static Complex Drive(WeccConverterState s, double t, double v)
    {
        Complex i = s.Commit(new Complex(v, 0.0));
        s.Step(t, new Complex(v, 0.0), new[] { new Complex(v, 0.0) }, Dt);
        return i;
    }

    // Stationary to rounding: the measured P = |V| Ip and the closed-form initial references are each exact only to the last
    // bit of the products that form them, so a controlled unit at rest may move by a few ulps; it must not drift. 1e-14 pu
    // on currents of order 1 is about 50 ulps.
    private static void AssertAtRest(WeccConverterState s, double v0, int steps)
    {
        Complex i0 = Drive(s, 0.0, v0);
        double worst = 0.0;
        for (int k = 1; k < steps; k++) worst = Math.Max(worst, (Drive(s, k * Dt, v0) - i0).Magnitude);
        Assert.True(worst <= 1e-14, $"the unit moved by {worst} pu from rest");
    }

    // ---------------------------------------------------------------- blocks

    [Fact]
    public void Lag_step_response_is_the_analytic_exponential()
    {
        const double T = 0.02;
        double x = 0.0;
        for (int k = 1; k <= 200; k++)
        {
            x = WeccBlocks.Lag(x, 1.0, T, Dt);
            double exact = 1.0 - Math.Exp(-k * Dt / T);
            Assert.True(Math.Abs(x - exact) <= 1e-14, $"step {k}: {x} vs {exact}");
        }
        Assert.Equal(0.7, WeccBlocks.Lag(0.3, 0.7, 0.0, Dt));   // T = 0: instantaneous, exactly the input
    }

    [Fact]
    public void Deadband_rate_limit_and_table_edges_are_exact()
    {
        Assert.Equal(0.0, WeccBlocks.Deadband(0.05, -0.05, 0.05));
        Assert.Equal(0.0, WeccBlocks.Deadband(-0.05, -0.05, 0.05));
        Assert.Equal(0.25 - 0.05, WeccBlocks.Deadband(0.25, -0.05, 0.05));
        Assert.Equal(-0.25 + 0.05, WeccBlocks.Deadband(-0.25, -0.05, 0.05));
        Assert.Equal(0.25, WeccBlocks.Deadband(0.25, 0.0, 0.0));

        Assert.Equal(1.0 + 2.0 * Dt, WeccBlocks.RateLimit(1.0, 5.0, 2.0, -3.0, Dt));
        Assert.Equal(1.0 - 3.0 * Dt, WeccBlocks.RateLimit(1.0, -5.0, 2.0, -3.0, Dt));
        Assert.Equal(1.001, WeccBlocks.RateLimit(1.0, 1.001, 2.0, -3.0, Dt));

        double[] x = { 0.0, 0.5, 0.5, 1.0 }, y = { 1.0, 1.0, 0.2, 0.6 };
        Assert.Equal(1.0, WeccBlocks.Table(x, y, -3.0));                 // flat before the first point
        Assert.Equal(1.0, WeccBlocks.Table(x, y, 0.25));
        Assert.Equal(0.2, WeccBlocks.Table(x, y, 0.5));                  // a step: the later point's value at the breakpoint
        Assert.Equal(0.2 + (0.6 - 0.2) * (0.75 - 0.5) / 0.5, WeccBlocks.Table(x, y, 0.75));
        Assert.Equal(0.6, WeccBlocks.Table(x, y, 1.0));
        Assert.Equal(0.6, WeccBlocks.Table(x, y, 7.0));                  // flat after the last point
    }

    [Fact]
    public void Hold_timer_lasts_a_whole_number_of_steps_despite_time_rounding()
    {
        var h = new HoldTimer();
        double t0 = 48 * Dt;          // 0.24 s, formed as k dt
        h.Arm(t0, 0.1);               // 20 steps
        int active = 0;
        for (int k = 48; k < 100; k++) if (h.Active(k * Dt, Dt)) active++;
        Assert.Equal(20, active);
    }

    // ---------------------------------------------------------------- REGC_A

    [Fact]
    public void Regca_alone_is_stationary_to_the_last_bit()
    {
        WeccConverterState s = Unit(new WeccPlant(Regc()));
        Complex i0 = Drive(s, 0.0, 1.02);
        Assert.Equal(0.8, (new Complex(1.02, 0) * Complex.Conjugate(i0)).Real, 15);
        Assert.Equal(0.1, (new Complex(1.02, 0) * Complex.Conjugate(i0)).Imaginary, 15);
        for (int k = 1; k < 2000; k++)
        {
            Complex i = Drive(s, k * Dt, 1.02);
            Assert.Equal(i0, i);
        }
    }

    [Fact]
    public void Regca_current_command_step_follows_the_converter_lag_and_the_rise_limit()
    {
        RegcaParameters p = Regc() with { Rrpwr = 1e6 };
        var g = new Regca(p, 1.0, 0.5, 0.0);
        for (int k = 1; k <= 50; k++)
        {
            g.Step(0.6, 0.2, 1.0, Dt);
            double e = 1.0 - Math.Exp(-k * Dt / p.Tg);
            Assert.True(Math.Abs(g.Ip - (0.5 + 0.1 * e)) <= 1e-14, $"Ip step {k}");
            Assert.True(Math.Abs(g.IqExport - 0.2 * e) <= 1e-14, $"Iq step {k}");
        }

        // Rrpwr = 2 pu/s: a large command makes Ip ramp at exactly 2 pu/s; a fall is not limited.
        var r = new Regca(Regc() with { Rrpwr = 2.0 }, 1.0, 0.2, 0.0);
        for (int k = 1; k <= 40; k++)
        {
            r.Step(1.0, 0.0, 1.0, Dt);
            Assert.True(Math.Abs(r.Ip - (0.2 + 2.0 * Dt * k)) <= 1e-13, $"ramp step {k}: {r.Ip}");
        }
        double before = r.Ip;
        r.Step(0.0, 0.0, 1.0, Dt);
        Assert.Equal(before * Math.Exp(-Dt / 0.02), r.Ip, 15);
    }

    [Fact]
    public void Regca_network_blocks_lvacm_and_hvrcm_follow_their_characteristics()
    {
        var g = new Regca(Regc(), 1.0, 0.8, 0.1);
        Assert.Equal((0.8, 0.1), g.Output(1.0));
        Assert.Equal(0.8 * (0.6 - 0.4) / (0.8 - 0.4), g.Output(0.6).Ip, 15);   // LVACM linear between Lvpnt0 and Lvpnt1
        Assert.Equal(0.0, g.Output(0.4).Ip);
        Assert.Equal(0.0, g.Output(0.1).Ip);
        Assert.Equal(0.8, g.Output(0.8).Ip);
        // HVRCM: export reduced by Khv (V - Volim) above Volim, floored at Iolim.
        Assert.Equal(0.1 - 0.7 * (1.3 - 1.2), g.Output(1.3).IqExport, 15);
        Assert.Equal(-1.3, g.Output(5.0).IqExport);
        Assert.Equal(0.1, g.Output(1.2).IqExport);
    }

    [Fact]
    public void Regca_lvpl_caps_the_active_current_from_the_filtered_voltage()
    {
        RegcaParameters p = Regc() with { Tfltr = 0.0 };   // instantaneous filter: Vf is the step's voltage
        var g = new Regca(p, 1.0, 1.0, 0.0);
        g.Step(1.0, 0.0, 0.6, Dt);    // the voltage drops: the filter follows this step
        g.Step(1.0, 0.0, 0.6, Dt);    // now the LVPL limit at Vf = 0.6 applies
        Assert.Equal(1.22 * (0.6 - 0.4) / (0.9 - 0.4), g.Ip, 15);
        Assert.Equal(double.PositiveInfinity, g.Lvpl(0.9));
        Assert.Equal(0.0, g.Lvpl(0.4));
        var off = new Regca(p with { Lvplsw = 0 }, 1.0, 1.0, 0.0);
        Assert.Equal(double.PositiveInfinity, off.Lvpl(0.1));
    }

    [Fact]
    public void Regca_reactive_rate_limits_act_only_on_the_side_the_initial_q_selects()
    {
        RegcaParameters p = Regc() with { Iqrmax = 1.0, Iqrmin = -1.0 };
        // Q0 > 0: the export may fall at most 1 pu/s (Iqrmax on the network-convention Iq), and rise freely.
        var pos = new Regca(p, 1.0, 0.5, 0.5);
        pos.Step(0.5, 0.0, 1.0, Dt);
        Assert.Equal(0.5 - 1.0 * Dt, pos.IqExport, 15);
        var pos2 = new Regca(p, 1.0, 0.5, 0.5);
        pos2.Step(0.5, 1.0, 1.0, Dt);
        Assert.Equal(1.0 + (0.5 - 1.0) * Math.Exp(-Dt / 0.02), pos2.IqExport, 15);
        // Q0 < 0: the export may rise at most 1 pu/s, and fall freely.
        var neg = new Regca(p, 1.0, 0.5, -0.5);
        neg.Step(0.5, 0.5, 1.0, Dt);
        Assert.Equal(-0.5 + 1.0 * Dt, neg.IqExport, 15);
        // Q0 = 0: neither limit.
        var zero = new Regca(p, 1.0, 0.5, 0.0);
        zero.Step(0.5, 0.5, 1.0, Dt);
        Assert.Equal(0.5 * (1.0 - Math.Exp(-Dt / 0.02)), zero.IqExport, 15);
    }

    [Fact]
    public void Regca_refuses_a_start_it_cannot_hold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Regca(Regc(), 0.35, 0.5, 0.0));          // below Lvpnt0 with P
        Assert.Throws<ArgumentOutOfRangeException>(() => new Regca(Regc(), 1.25, 0.5, 0.0));          // above Volim, Khv > 0
        Assert.Throws<ArgumentOutOfRangeException>(() => new Regca(Regc(), 1.0, 0.5, -1.5));          // below Iolim
        Assert.Throws<ArgumentOutOfRangeException>(() => new Regca(Regc() with { Tfltr = 0 }, 0.5, 0.9, 0.0));   // above LVPL at 0.5
        var ex = Assert.Throws<ArgumentException>(() => new Regca(Regc() with { Iolim = 1.3, Tg = -1 }, 1.0, 0.5, 0.0));
        Assert.Contains("Tg = -1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Iolim = 1.3", ex.Message, StringComparison.Ordinal);   // every problem listed
    }

    // ---------------------------------------------------------------- REEC_A/B/C

    public static IEnumerable<object[]> QModes() => new[]
    {
        new object[] { 0, 0, 0 },   // local constant Q
        new object[] { 0, 1, 0 },   // local constant Q (VFlag irrelevant)
        new object[] { 1, 0, 0 },   // constant power factor
        new object[] { 0, 0, 1 },   // local voltage control
        new object[] { 0, 1, 1 },   // coordinated Q/V
        new object[] { 1, 1, 1 },   // power factor through the Q PI
    };

    [Theory]
    [MemberData(nameof(QModes))]
    public void Reec_every_q_mode_starts_and_stays_at_rest_to_the_last_bit(int pf, int vflag, int qflag)
    {
        foreach (ReecVariant variant in new[] { ReecVariant.A, ReecVariant.B, ReecVariant.C })
        {
            ReecParameters rp = ReecA() with { Variant = variant, PfFlag = pf, VFlag = vflag, QFlag = qflag, Kqp = 0.5, Kvp = 1.0 };
            if (variant == ReecVariant.B) rp = rp with { Vq = Array.Empty<double>(), Iq = Array.Empty<double>(), Vp = Array.Empty<double>(), Ip = Array.Empty<double>() };
            if (variant == ReecVariant.C) rp = rp with { Pmin = -1.0, T = 3600.0, SocIni = 0.5 };
            // Local voltage control (VFlag = 0, QFlag = 1) regulates Vt to (Vref1 +) Qlim, and Qlim passes the Qmin/Qmax limit
            // first: REEC_A carries the voltage in Vref1 (Qext = V0 - Vref1 = 0.02), REEC_B/C (no Vref1) need Qmax >= V0.
            if (vflag == 0 && qflag == 1) rp = variant == ReecVariant.A ? rp with { Vref1 = 1.0 } : rp with { Qmax = 1.1 };
            AssertAtRest(Unit(new WeccPlant(Regc(), rp)), 1.02, 1000);
        }
    }

    [Fact]
    public void Reec_voltage_control_with_vflag0_needs_a_consistent_vref1_when_the_q_reference_is_fixed()
    {
        // PfFlag = 1 fixes Qref = tan(pf) P = Q0, so VFlag = 0 regulates Vt to Vref1 + Q0: refused unless that is V0.
        ReecParameters rp = ReecA() with { PfFlag = 1, VFlag = 0, QFlag = 1, Vref1 = 0.0 };
        var ex = Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), rp)));
        Assert.Contains("not an equilibrium", ex.Message, StringComparison.Ordinal);
        Assert.Contains("s3 V PI input", ex.Message, StringComparison.Ordinal);
        AssertAtRest(Unit(new WeccPlant(Regc(), rp with { Vref1 = 1.02 - 0.1 })), 1.02, 200);   // Vref1 + Q0 = V0
    }

    [Fact]
    public void Reec_refuses_a_start_it_cannot_hold()
    {
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA()), v0: 0.85));                      // in a dip
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA() with { Pmax = 0.5 })));           // P0 > Pmax
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA() with { Qmax = 0.05 })));          // Q0 > Qmax
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA() with { Imax = 0.7 })));           // I0 > Imax
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA()), v0: 0.7, q0: 0.0));             // LVACM < 1 at V0
        var ex = Assert.Throws<ArgumentException>(() => Unit(new WeccPlant(Regc(), ReecA() with { PfFlag = 3, Trv = -1 })));
        Assert.Contains("PfFlag = 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Trv = -1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reec_dip_injects_kqv_times_the_deadbanded_error_on_top_of_the_frozen_current()
    {
        ReecParameters rp = ReecA() with { Trv = 0.0 };   // instantaneous filter, so Vt_filt is the previous step's voltage
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.6, 0.8, 0.0, reec.PrefInitial, reec.QextInitial, 1.0);
        reec.UpdateDipState(0.1, 0.6);
        Assert.True(reec.InDip);
        ReecOutputs o = reec.Evaluate(u, 0.1, Dt);
        Assert.Equal(0.0, o.Iqcmd);                          // Vt_filt still 1.0: no error yet
        reec.Advance(u, o, Dt);                               // filter follows to 0.6; s3 frozen
        reec.UpdateDipState(0.1 + Dt, 0.6);
        o = reec.Evaluate(u, 0.1 + Dt, Dt);
        double iqv = Math.Min(2.0 * ((1.0 - 0.6) - 0.05), 1.05);   // Kqv (Vref0 - Vt_filt - dbd2), clamped to Iqh1
        Assert.Equal(iqv, o.Iqinj, 15);
        Assert.Equal(iqv, o.Iqcmd, 15);                       // s3 frozen at its initial 0
        // Q priority: Ipmax = sqrt(Imax^2 - Iqcmd^2); Ipcmd = Pord / Vt_filt within it.
        double ipmax = Math.Sqrt(1.2 * 1.2 - iqv * iqv);
        Assert.Equal(ipmax, o.Ipmax, 15);
        Assert.Equal(Math.Min(0.8 / 0.6, ipmax), o.Ipcmd, 15);
    }

    [Theory]
    [InlineData(0.1, 20)]     // Thld > 0: Iqinj = Iqfrz for 20 steps after the dip
    [InlineData(-0.05, 10)]   // Thld < 0: Iqinj = Iqv for 10 steps after the dip
    [InlineData(0.0, 0)]      // Thld = 0: back to normal at once
    public void Reec_a_post_dip_state_lasts_exactly_thld(double thld, int steps)
    {
        ReecParameters rp = ReecA() with { Trv = 0.0, Thld = thld, Iqfrz = -0.1, Kqv = 2.0, Dbd1 = 0.0, Dbd2 = 0.0 };
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.6, 0.8, 0.0, reec.PrefInitial, reec.QextInitial, 1.0);
        double t = 0.0;
        for (int k = 0; k < 10; k++, t += Dt) { reec.UpdateDipState(t, 0.6); ReecOutputs o = reec.Evaluate(u, t, Dt); reec.Advance(u, o, Dt); }
        // Recovery: Vt back to 0.95 (inside [Vdip, Vup]); Vt_filt follows one step later.
        var ur = u with { Vt = 0.95 };
        var iqinj = new List<double>();
        for (int k = 0; k < 40; k++, t += Dt)
        {
            reec.UpdateDipState(t, 0.95);
            ReecOutputs o = reec.Evaluate(ur, t, Dt);
            iqinj.Add(o.Iqinj);
            reec.Advance(ur, o, Dt);
        }
        int held = iqinj.TakeWhile(x => x != 0.0).Count();
        Assert.Equal(steps, held);
        if (thld > 0) Assert.All(iqinj.Take(steps), x => Assert.Equal(-0.1, x));
        if (thld < 0) Assert.Equal(2.0 * (1.0 - 0.6), iqinj[0], 15);   // Iqv from the filter state (0.6) on the first step
        Assert.All(iqinj.Skip(steps), x => Assert.Equal(0.0, x));
    }

    [Fact]
    public void Reec_a_thld2_holds_the_active_current_limit_after_the_dip()
    {
        ReecParameters rp = ReecA() with { Trv = 0.0, Thld2 = 0.05, Kqv = 2.0 };
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.6, 0.8, 0.0, reec.PrefInitial, reec.QextInitial, 1.0);
        double t = 0.0, ipmaxInDip = 0.0;
        for (int k = 0; k < 10; k++, t += Dt) { reec.UpdateDipState(t, 0.6); ReecOutputs o = reec.Evaluate(u, t, Dt); ipmaxInDip = o.Ipmax; reec.Advance(u, o, Dt); }
        var ur = u with { Vt = 1.0 };
        var ipmax = new List<double>();
        for (int k = 0; k < 20; k++, t += Dt) { reec.UpdateDipState(t, 1.0); ReecOutputs o = reec.Evaluate(ur, t, Dt); ipmax.Add(o.Ipmax); reec.Advance(ur, o, Dt); }
        Assert.All(ipmax.Take(10), x => Assert.Equal(ipmaxInDip, x));   // 0.05 s = 10 steps at the dip value
        Assert.NotEqual(ipmaxInDip, ipmax[10]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Reec_current_limit_is_app_b_with_both_vdl_tables(int pq)
    {
        double[] vq = { 0.0, 0.5, 0.8, 1.2 }, iq = { 0.6, 0.6, 1.0, 1.1 }, vp = { 0.0, 0.5, 0.8, 1.2 }, ip = { 0.3, 0.5, 0.9, 1.1 };
        ReecParameters rp = ReecA() with { PqFlag = pq, Trv = 0.0, Kqv = 3.0, Dbd1 = 0.0, Dbd2 = 0.0, Iqh1 = 2.0, Vq = vq, Iq = iq, Vp = vp, Ip = ip, Imax = 1.1 };
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.7, 0.8, 0.0, reec.PrefInitial, reec.QextInitial, 1.0);
        reec.UpdateDipState(0.0, 0.7);
        reec.Advance(u, reec.Evaluate(u, 0.0, Dt), Dt);   // Vt_filt -> 0.7
        reec.UpdateDipState(Dt, 0.7);
        ReecOutputs o = reec.Evaluate(u, Dt, Dt);

        double vdl1 = 0.6 + (1.0 - 0.6) * (0.7 - 0.5) / (0.8 - 0.5), vdl2 = 0.5 + (0.9 - 0.5) * (0.7 - 0.5) / (0.8 - 0.5);
        double iqWanted = 3.0 * (1.0 - 0.7), ipWanted = 0.8 / 0.7;
        double iqmax, ipmax, iqcmd, ipcmd;
        if (pq == 0)
        {
            iqmax = Math.Min(vdl1, 1.1); iqcmd = Math.Clamp(iqWanted, -iqmax, iqmax);
            ipmax = Math.Min(vdl2, Math.Sqrt(1.1 * 1.1 - iqcmd * iqcmd)); ipcmd = Math.Clamp(ipWanted, 0.0, ipmax);
        }
        else
        {
            ipmax = Math.Min(vdl2, 1.1); ipcmd = Math.Clamp(ipWanted, 0.0, ipmax);
            iqmax = Math.Min(vdl1, Math.Sqrt(1.1 * 1.1 - ipcmd * ipcmd)); iqcmd = Math.Clamp(iqWanted, -iqmax, iqmax);
        }
        Assert.Equal(iqmax, o.Iqmax, 15);
        Assert.Equal(ipmax, o.Ipmax, 15);
        Assert.Equal(iqcmd, o.Iqcmd, 15);
        Assert.Equal(ipcmd, o.Ipcmd, 15);
    }

    [Fact]
    public void Reec_pi_states_and_power_order_are_frozen_in_a_dip()
    {
        ReecParameters rp = ReecA() with { Trv = 0.0, Kvp = 0.0, Kqv = 0.0 };
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.5, 0.3, 0.0, reec.PrefInitial + 0.1, reec.QextInitial, 1.0);
        double t = 0.0;
        for (int k = 0; k < 20; k++, t += Dt)
        {
            reec.UpdateDipState(t, 0.5);
            ReecOutputs o = reec.Evaluate(u, t, Dt);
            Assert.Equal(0.0, o.Iqcmd);   // s3 frozen at 0 (Kvp = Kqv = 0): the V PI does not wind up on the 0.5 pu error
            Assert.Equal(0.8, o.Pord);    // Pord frozen at its start value
            reec.Advance(u, o, Dt);
        }
    }

    [Fact]
    public void Reec_power_reference_is_rate_limited_then_lagged()
    {
        ReecParameters rp = ReecA() with { DPmax = 2.0, DPmin = -4.0 };
        var reec = new Reec(rp, 1.0, 0.5, 0.0, 0.5, 0.0);
        var u = new ReecInputs(1.0, 0.5, 0.0, 0.9, reec.QextInitial, 1.0);
        double t = 0.0;
        for (int k = 1; k <= 30; k++, t += Dt)
        {
            reec.UpdateDipState(t, 1.0);
            ReecOutputs o = reec.Evaluate(u, t, Dt);
            Assert.Equal(Math.Min(0.5 + 2.0 * Dt * (k - 1), 0.9), o.Psel, 13);   // up-ramp at dPmax
            reec.Advance(u, o, Dt);
        }
    }

    [Fact]
    public void Reec_c_state_of_charge_integrates_the_power_and_cuts_the_current_at_its_limits()
    {
        ReecParameters rp = ReecA() with { Variant = ReecVariant.C, Pmin = -1.0, T = 100.0, SocIni = 0.5, SocMin = 0.2, SocMax = 0.8 };
        var reec = new Reec(rp, 1.0, 0.5, 0.0, 0.5, 0.0);
        var u = new ReecInputs(1.0, 0.5, 0.0, reec.PrefInitial, reec.QextInitial, 1.0);
        double t = 0.0;
        for (int k = 0; k < 1000; k++, t += Dt) { reec.UpdateDipState(t, 1.0); reec.Advance(u, reec.Evaluate(u, t, Dt), Dt); }
        Assert.Equal(0.5 - 0.5 / 100.0 * Dt * 1000, reec.StateOfCharge, 12);   // SOC = SOCini - (1/T) integral P dt
        // Run on to below SOCmin: Ipmax = 0, and charging (negative Ipmin) is still allowed.
        for (int k = 0; k < 13000; k++, t += Dt) { reec.UpdateDipState(t, 1.0); reec.Advance(u, reec.Evaluate(u, t, Dt), Dt); }
        Assert.True(reec.StateOfCharge <= 0.2);
        reec.UpdateDipState(t, 1.0);
        ReecOutputs o = reec.Evaluate(u, t, Dt);
        Assert.Equal(0.0, o.Ipmax);
        Assert.Equal(0.0, o.Ipcmd);
        Assert.Equal(0.0, o.Ipmin);   // -Ipmax
    }

    [Fact]
    public void Reec_c_allows_charging_and_refuses_starting_past_its_soc_limits()
    {
        ReecParameters rp = ReecA() with { Variant = ReecVariant.C, Pmin = -1.0, T = 100.0, SocIni = 0.5 };
        AssertAtRest(Unit(new WeccPlant(Regc(), rp), p0: -0.6), 1.02, 200);
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), rp with { SocIni = 0.2 }), p0: 0.5));
        Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), rp with { SocIni = 0.8 }), p0: -0.5));
    }

    [Fact]
    public void Unit_scales_machine_base_currents_by_its_rating()
    {
        WeccConverterState s = Unit(new WeccPlant(Regc(), ReecA()), rating: 2.5);
        Complex i = s.Commit(new Complex(1.02, 0.0));
        Assert.Equal(0.8 * 2.5 / 1.02, i.Real, 14);
        Assert.Equal(-0.1 * 2.5 / 1.02, i.Imaginary, 14);   // exporting Q: the current lags the voltage
        Assert.Equal(0.8, s.MeasuredPower.P, 14);
        Assert.Equal(0.1, s.MeasuredPower.Q, 14);
    }

    [Fact]
    public void Two_runs_of_a_fault_ride_through_are_bit_identical()
    {
        static List<Complex> Run()
        {
            WeccConverterState s = Unit(new WeccPlant(Regc(), ReecA() with { Thld = 0.1, Iqfrz = 0.2, Thld2 = 0.05 }));
            var trace = new List<Complex>();
            for (int k = 0; k < 600; k++)
            {
                double t = k * Dt, v = t is >= 0.1 and < 0.25 ? 0.3 : 1.02;
                trace.Add(Drive(s, t, v));
            }
            return trace;
        }
        Assert.Equal(Run(), Run());
    }
}
