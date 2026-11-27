using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP20: the fuller WECC composite load (<see cref="CompositeLoadExtensions"/>): motors B and C, motor D (the WECC
/// single-phase air-conditioner performance model, <see cref="AcMotorParameters"/>), the electronic load
/// (<see cref="ElectronicLoadParameters"/>), motor under-voltage relays and the substation tap changer
/// (<see cref="CompositeLoadLtcParameters"/>). Source of the equations: WECC MVWG, "WECC Composite Load Model
/// Specification" (January 2015, re-issued April 2021, public on wecc.org). Verification:
/// <list type="number">
/// <item>Flat start of the full model: the t = 0 draw equals the power-flow P and Q, and every state stays put, within
/// bounds derived from rounding.</item>
/// <item>Closed forms of motor D's run and stall curves, of the electronic load's fraction (the specification's pseudocode)
/// and of the timers (stall, restart, under-voltage relays, tap changer), driven by a scripted terminal voltage. Timed tests
/// use a step of 2^-10 s or 2^-6 s, so every time sum is exact and the step index of each event follows by integer
/// arithmetic.</item>
/// <item>Motors B and C give the WP3b motor A's bits (the same code with the same parameters).</item>
/// <item>A step response against tests/tooling/dynamic-library-reference.py case 20 (SciPy solve_ivp Radau, rtol 1e-12),
/// converging at first order in the step.</item>
/// <item>Bit-identity of the subset without extensions: Rc5Wp20BitIdentityTests.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp20CompositeLoadTests
{
    private const double Hz = 50.0;
    private const double Eps = 1.1102230246251565e-16;   // 2^-53, the unit roundoff
    private const double Dt10 = 1.0 / 1024.0;

    private static InductionMotorParameters Motor(double torqueExponent, double h = 1.0) =>
        InductionMotorParameters.FromCircuit(0.01, 0.06, 4.0, 0.03, 0.04, h, InductionMotorTorque.PowerLaw(torqueExponent), Hz);

    private static readonly StaticLoadCharacteristic Static = new(0.5, 2.0, 0.3, 1.0);

    /// <summary>The case-11/case-20 network of the reference script (bus draws 0.8 + j0.3 pu at 1 pu on 100 MVA) with motor
    /// A at <paramref name="fma"/>.</summary>
    private static CompositeLoadData Network(double fma, CompositeLoadExtensions? x, double p = 80.0, double q = 30.0, double tap = 1.0) =>
        new(1, p, q, 100.0, 0.08, tap, 0.02, 0.02, 0.04, 0.04, 0.5, fma, 0.8, Motor(2.0), 0.95, Static, Static) { Extensions = x };

    private static AcMotorParameters MotorDWecc => AcMotorParameters.Wecc(0.8, 0.97, 0.6, 9999.0, 0.124, 0.114);

    /// <summary>Reference-script case 20: A 30 %, B 20 % (H 0.5, constant torque, LF 0.7), D 15 %, electronic 10 %, static
    /// the rest.</summary>
    private static CompositeLoadExtensions Case20Parts => new()
    {
        MotorB = new CompositeLoadMotor(0.2, 0.7, Motor(0.0, 0.5)),
        MotorDFraction = 0.15, MotorD = MotorDWecc,
        ElectronicFraction = 0.1, Electronic = new ElectronicLoadParameters(0.95, 0.7, 0.5, 0.8),
    };

    private static CompositeLoadExtended.State Start(CompositeLoadData d, Complex v0, out DynamicLoadPlan plan)
    {
        plan = d.Plan(v0, 100.0, Hz);
        return (CompositeLoadExtended.State)plan.CreateModel();
    }

    private static Complex Drawn(IRmsConverterModel m, Complex v)
    {
        Complex inj = m.Commit(v);
        return v * Complex.Conjugate(-m.TerminalCurrent(inj, v));
    }

    // ------------------------------------------------------------------------------------------------ 1. flat start

    [Fact]
    public void The_full_composite_load_starts_at_its_power_and_stays_at_rest()
    {
        var x = Case20Parts with
        {
            MotorC = new CompositeLoadMotor(0.05, 0.75, Motor(1.0, 0.3), new MotorUnderVoltageRelay(0.5, 0.1, 0.5)),
            MotorARelay = new MotorUnderVoltageRelay(0.6, 0.1, 0.2, 0.4, 0.05, 0.3),
            MotorD = MotorDWecc with
            {
                RestartFraction = 0.3, RestartSeconds = 0.2, UvFraction = 0.1, Vtr1 = 0.5, Ttr1 = 0.1,
                Vc1off = 0.45, Vc2off = 0.35, Vc1on = 0.5, Vc2on = 0.45, ThermalSeconds = 10.0, StallSeconds = 0.033,
            },
            Ltc = new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 0.9, 1.1, 30.0, 5.0),
        };
        CompositeLoadData d = Network(0.3, x, 47.8, -3.9);
        Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        CompositeLoadExtended.State m = Start(d, v0, out _);
        Assert.Equal(3, m.Motors.Count);
        Complex s0 = Drawn(m, v0);
        // The t = 0 draw: the motors' currents E' Y k and the held motor D admittance against the initialisation's currents
        // agree to the rounding of a few complex operations on terms of at most |Y| |V| (|Y| below 10 pu), as in WP3b:
        // 32 eps |Y| |V|^2.
        double tol = 32 * Eps * 10.0 * 1.01 * 1.01;
        Assert.True(Math.Abs(s0.Real - 0.478) <= tol && Math.Abs(s0.Imaginary + 0.039) <= tol, $"draws {s0} at t = 0");
        Assert.True(tol <= 1e-12);
        var e0 = m.Motors.Select(r => r.Motor.Emf).ToArray();
        var w0 = m.Motors.Select(r => r.Motor.Speed).ToArray();
        Complex vf0 = m.FarEndVoltage;
        double thA0 = m.MotorD!.TemperatureA;
        const int steps = 400;   // 2 s of 5 ms
        double worstS = 0, worstState = 0;
        for (int n = 0; n < steps; n++)
        {
            m.Step(n * 0.005, v0, new[] { v0 }, 0.005);
            worstS = Math.Max(worstS, (Drawn(m, v0) - s0).Magnitude);
            for (int i = 0; i < 3; i++)
                worstState = Math.Max(worstState, Math.Max((m.Motors[i].Motor.Emf - e0[i]).Magnitude, Math.Abs(m.Motors[i].Motor.Speed - w0[i])));
            worstState = Math.Max(worstState, (m.FarEndVoltage - vf0).Magnitude);
            worstState = Math.Max(worstState, Math.Abs(m.MotorD.TemperatureA - thA0));
        }
        // The WP3b rest bound: the states' rates cancel to about 8 eps of their terms (terms at most 10 pu), so over n steps
        // of dt they move at most n dt 8 eps 10 / Tmin, Tmin the shortest of 2H (0.6 s here) and T'o (0.43 s), taken as
        // 0.4 s; the power reads them through |Y| |V| below 11. Both are below the spec's 1e-12 for 2 s.
        double stateBound = steps * 0.005 * 8 * Eps * 10.0 / 0.4;
        Assert.True(stateBound * 11.0 + tol <= 1e-12);
        Assert.True(worstState <= stateBound, $"states moved {worstState:R} at rest (bound {stateBound:R})");
        Assert.True(worstS <= stateBound * 11.0 + tol, $"power moved {worstS:R} at rest");
        Assert.Equal(0, m.Ltc!.Moves);
        Assert.False(m.MotorD.StalledA || m.MotorD.StalledB || m.MotorD.UvTripped);
        Assert.Equal(1.0, m.MotorD.ContactorFraction);
        Assert.All(m.Motors, r => Assert.Equal(1.0, r.Connected));
    }

    [Fact]
    public void Empty_extensions_give_the_subset_bits()
    {
        // Extensions with nothing in them take the extended code with motor A and the static load only: the same arithmetic
        // in the same order as the subset, so the same bits.
        Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        Assert.Equal(Trace(Network(0.4, null, 47.8, -3.9), v0), Trace(Network(0.4, new CompositeLoadExtensions(), 47.8, -3.9), v0));
    }

    /// <summary>The bits of a composite load's injection, first-motor speed and far-end voltage over a scripted dip.</summary>
    private static string Trace(CompositeLoadData d, Complex v0)
    {
        DynamicLoadPlan plan = d.Plan(v0, 100.0, Hz);
        IRmsConverterModel m = plan.CreateModel();
        var sb = new System.Text.StringBuilder();
        void Add(double x) => sb.Append(BitConverter.DoubleToInt64Bits(x).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        Add(plan.NortonAdmittance.Real);
        Add(plan.NortonAdmittance.Imaginary);
        for (int k = 0; k < 600; k++)
        {
            Complex v = k < 20 ? v0 : k < 50 ? v0 * 0.6 : v0 * 0.99;
            Complex inj = m.Commit(v);
            m.Step(k * 0.005, v, new[] { v }, 0.005);
            (double speed, Complex vf) = m switch
            {
                CompositeLoadData.State s => (s.Motor.Speed, s.FarEndVoltage),
                CompositeLoadExtended.State e => (e.Motors[0].Motor.Speed, e.FarEndVoltage),
                _ => throw new InvalidOperationException("unexpected state"),
            };
            Add(inj.Real); Add(inj.Imaginary); Add(speed); Add(vf.Real); Add(vf.Imaginary);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------- 7. motors B and C

    [Fact]
    public void Motors_b_and_c_alone_reproduce_motor_a_bit_for_bit()
    {
        Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.0, -0.1);
        foreach (double f in new[] { 1.0, 0.4 })
        {
            string a = Trace(Network(f, null), v0);
            string b = Trace(Network(0.0, new CompositeLoadExtensions { MotorB = new CompositeLoadMotor(f, 0.8, Motor(2.0)) }), v0);
            string c = Trace(Network(0.0, new CompositeLoadExtensions { MotorC = new CompositeLoadMotor(f, 0.8, Motor(2.0)) }), v0);
            Assert.Equal(a, b);
            Assert.Equal(a, c);
        }
    }

    // ------------------------------------------------------------------------------------- 2. motor D's curves

    [Fact]
    public void Motor_d_follows_the_wecc_run_and_stall_curves()
    {
        // LF 1 and V0 = 1 above Vbrk with Kp1 = 0: Po = 1 exactly, so P is P/Po.
        AcMotorParameters p = AcMotorParameters.Wecc(1.0, 0.97, 0.6, 9999.0, 0.124, 0.114);
        var s = new AcMotorState(p, 1.0, "test");
        Assert.Equal(1.0, s.Initial.Po);
        // Below Vbrk: P = Po + 12 (0.86 - 0.80)^3.2 = 1.0014766081028574 (Python double, 1 + 12 * 0.06 ** 3.2). GridSim's
        // 0.86 - 0.80 is 0.06 within 1e-16, the slope of the term there is 12 * 3.2 * 0.06^2.2 < 0.08, so that moves the
        // term by under 1e-17; the power function adds a few ulp of the term (1.5e-3 x 4e-16); the final sum rounds to half
        // an ulp of 1.0015 (1.1e-16). So within 2 ulp of 1 (4.4e-16).
        Assert.True(Math.Abs(s.Run(0.80).P - 1.0014766081028574) <= 4.4e-16, $"P(0.80) = {s.Run(0.80).P:R}");
        // At and above Vbrk with Kp1 = 0: P = Po exactly; Q = Q'o + 6 (V - 0.86)^2, Q'o = tan(acos 0.97) - 6 (0.14)^2.
        Assert.Equal(1.0, s.Run(0.95).P);
        double qo = Math.Sqrt(1.0 - 0.97 * 0.97) / 0.97 - 6.0 * (0.14 * 0.14);
        // Q'o: the specification's two terms, each within a few ulp of 0.25 (8 eps 0.25 together); the 0.14 of 1 - 0.86 is
        // 0.14 within 1e-16, moving 6 x 0.14^2 by 2 x 6 x 0.14 x 1e-16 = 1.7e-16.
        Assert.True(Math.Abs(s.Initial.Qo - qo) <= 8 * Eps * 0.25 + 1.7e-16, $"Q'o {s.Initial.Qo:R} against {qo:R}");
        double d95 = 0.95 - 0.86;
        Assert.True(Math.Abs(s.Run(0.95).Q - (qo + 6.0 * d95 * d95)) <= 8 * Eps * 0.25 + 1.7e-16 + 4 * Eps * 0.05);
        // V'stall: where 1 + 12 (0.86 - V)^3.2 = Gstall V^2, bisected here independently (Math.Pow, 200 halvings). The
        // curves cross with a slope difference of at least 3 (2 Gstall V alone is 4.37 at 0.45), so a difference of a few
        // ulp (4e-16) in either function moves the root by under 2e-16; the brackets end within 1 ulp (5.6e-17).
        double g = 0.124 / (0.124 * 0.124 + 0.114 * 0.114), b = 0.114 / (0.124 * 0.124 + 0.114 * 0.114);
        double lo = 0.0, hi = 0.86;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi) break;
            if (1.0 + 12.0 * Math.Pow(0.86 - mid, 3.2) - g * mid * mid > 0.0) lo = mid; else hi = mid;
        }
        Assert.True(Math.Abs(s.StallBreakVoltage - hi) <= 4e-16, $"V'stall {s.StallBreakVoltage:R} against {hi:R}");
        Assert.InRange(hi, 0.4, 0.6);
        // At and below V'stall a running motor draws the stall curve: P = Gstall V^2, Q = Bstall V^2.
        Assert.Equal(g * 0.3 * 0.3, s.Run(0.3).P);
        Assert.Equal(b * 0.3 * 0.3, s.Run(0.3).Q);
    }

    // -------------------------------------------------------------------------------------------- 3. stall timer

    /// <summary>Motor D with the WECC stall data (Rstall 0.124, Xstall 0.114: Gstall 4.37) and Tstall 0.033 s. At 0.55 pu the
    /// stall curve (4.37 x 0.3025 = 1.322) is above the run curve (1 + 12 0.31^3.2 = 1.283), so V'stall is below 0.55 and the
    /// motor runs there until its timer stalls it.</summary>
    private static AcMotorParameters StallMotor => AcMotorParameters.Wecc(1.0, 0.97, 0.6, 0.033, 0.124, 0.114);

    [Fact]
    public void Motor_d_stalls_when_the_timer_runs_out_and_its_restartable_part_restarts()
    {
        AcMotorParameters p = StallMotor with { RestartFraction = 0.25, RestartVoltage = 0.9, RestartSeconds = 0.1 };
        var s = new AcMotorState(p, 1.0, "test");
        Assert.True(s.StallBreakVoltage < 0.55);
        // V steps to 0.55 at t = 0.1 s: steps of 2^-10 s starting at t = k dt, the first below is k0 = ceil(102.4) = 103. The
        // timer reaches Tstall = 0.033 s after ceil(0.033 x 1024) = ceil(33.792) = 34 steps, at the end of step k0 + 33 = 136
        // (t = 137/1024 = 0.1338 s, the first step end at or after 0.1 + 0.033 that the grid offers).
        double g = 0.124 / (0.124 * 0.124 + 0.114 * 0.114);
        int stalledAt = -1, restartAt = -1;
        for (int k = 0; k < 600; k++)
        {
            double v = k >= 103 && k < 300 ? 0.55 : 1.0;
            double pBefore = s.Power(v).P;
            if (k >= 103 && k <= 136 && v == 0.55) Assert.Equal(s.Run(0.55).P, pBefore);
            s.Advance(v, Dt10);
            if (s.StalledA && stalledAt < 0) stalledAt = k;
            if (stalledAt >= 0 && !s.StalledB && restartAt < 0) restartAt = k;
        }
        Assert.Equal(136, stalledAt);
        // Back to 1 pu at k = 300 (above Vrst 0.9): part B restarts after ceil(0.1 x 1024) = ceil(102.4) = 103 steps, at the
        // end of step 300 + 102 = 402; part A stays stalled.
        Assert.Equal(402, restartAt);
        Assert.True(s.StalledA);
        // Stalled (checked on a fresh motor with Frst 0, so no restartable part): P = Gstall V^2 and Q = Bstall V^2 exactly.
        var a = new AcMotorState(StallMotor, 1.0, "test");
        for (int k = 0; k < 40; k++) a.Advance(0.55, Dt10);
        Assert.True(a.StalledA && a.StalledB);
        Assert.Equal(g * 0.55 * 0.55, a.Power(0.55).P);
        Assert.Equal(0.114 / (0.124 * 0.124 + 0.114 * 0.114) * 0.55 * 0.55, a.Power(0.55).Q);
    }

    [Fact]
    public void Motor_d_protection_trips_by_its_relay_contactor_and_thermal_rules()
    {
        // Under-voltage relay: Fuvr 0.25 trips once V stays below Vtr1 = 0.7 for Ttr1 = 0.02 s: ceil(20.48) = 21 steps.
        var u = new AcMotorState(StallMotor with { StallSeconds = 9999.0, UvFraction = 0.25, Vtr1 = 0.7, Ttr1 = 0.02 }, 1.0, "test");
        for (int k = 0; k < 20; k++) { u.Advance(0.65, Dt10); Assert.False(u.UvTripped); }
        u.Advance(0.65, Dt10);
        Assert.True(u.UvTripped);
        Assert.Equal(0.75 * u.Run(1.0).P, u.Power(1.0).P);
        // A dip shorter than the delay resets the timer.
        var r = new AcMotorState(StallMotor with { StallSeconds = 9999.0, UvFraction = 0.25, Vtr1 = 0.7, Ttr1 = 0.02 }, 1.0, "test");
        for (int k = 0; k < 40; k++) r.Advance(k % 20 == 19 ? 1.0 : 0.65, Dt10);
        Assert.False(r.UvTripped);

        // Contactors: Vc1off 0.5, Vc2off 0.25, Vc1on 0.75, Vc2on 0.5 (dyadic, so every fraction is exact). Down to 0.375:
        // (0.375 - 0.25) / 0.25 = 0.5 connected; back up to 0.625: the dropped half reconnects (0.625 - 0.5) / 0.25 = 0.5 of
        // the way, 0.75; at 0.75 all are back; down to 0.4375 again: 0.75.
        var c = new AcMotorState(StallMotor with { StallSeconds = 9999.0, StallR = 0.0, StallX = 0.15, Vc1off = 0.5, Vc2off = 0.25, Vc1on = 0.75, Vc2on = 0.5 }, 1.0, "test");
        foreach ((double v, double want) in new[] { (0.375, 0.5), (0.45, 0.5), (0.625, 0.75), (0.5625, 0.75), (0.75, 1.0), (0.4375, 0.75), (0.2, 0.0) })
        {
            c.Advance(v, Dt10);
            Assert.Equal(want, c.ContactorFraction);
        }

        // Thermal relay at the stalled current: u = I^2 Rstall = (P^2 + Q^2) / V^2 Rstall = Gstall... for a stalled motor
        // |S| = V^2 / |Z|, so I^2 Rstall = V^2 Rstall / |Z|^2 = Gstall V^2. Stalled at V = 1 with Tth 2: theta follows
        // u + (theta0 - u) e^(-t/Tth) (the exact hold of the lag), and the fraction left is (Th2t - theta) / (Th2t - Th1t).
        AcMotorParameters tp = StallMotor with { StallSeconds = 0.0, ThermalSeconds = 2.0, Th1t = 1.0, Th2t = 3.0 };
        var t = new AcMotorState(tp, 1.0, "test");
        t.Advance(0.5, Dt10);   // stalls at once (Tstall 0)
        Assert.True(t.StalledA);
        double gst = 0.124 / (0.124 * 0.124 + 0.114 * 0.114), theta = t.TemperatureA, theta0 = theta, ust = gst;
        for (int k = 1; k <= 2048; k++)
        {
            t.Advance(1.0, Dt10);
            double closed = ust + (theta0 - ust) * Math.Exp(-k * Dt10 / 2.0);
            // k steps of x <- a x + (1 - a) u each round twice at |x| <= u = 4.37, and a = e^(-dt/T) differs from the closed
            // form's exponential by at most 2 eps, scaled by |theta0 - u| <= 4.4: k (2 eps 4.4 + 2 eps 4.4) + 4 eps 4.4.
            Assert.True(Math.Abs(t.TemperatureA - closed) <= k * (4 * Eps * 4.4) + 4 * Eps * 4.4, $"step {k}: {t.TemperatureA:R} against {closed:R}");
            double fth = Math.Clamp((3.0 - t.TemperatureA) / 2.0, 0.0, 1.0);
            Assert.True(Math.Abs(t.ThermalFractionA - fth) <= 4 * Eps, $"step {k}: fraction {t.ThermalFractionA:R} against {fth:R}");
        }
        Assert.True(t.ThermalFractionA < 1.0);
        // The thermal trip never resets: cool the motor (at 0 V the input is 0) and the fraction stays.
        double kept = t.ThermalFractionA;
        for (int k = 0; k < 4096; k++) t.Advance(0.0, Dt10);
        Assert.Equal(kept, t.ThermalFractionA);
    }

    // --------------------------------------------------------------------------------------- 4. electronic load

    [Fact]
    public void The_electronic_load_follows_the_specification_pseudocode()
    {
        // Dyadic settings (Vd1 0.75, Vd2 0.5, Frcel 0.5) make every fraction exact.
        var e = new ElectronicLoadParameters(0.95, 0.75, 0.5, 0.5);
        double vmin = 1.0;
        foreach ((double v, double want) in new[]
        {
            (1.0, 1.0), (0.625, 0.5), (0.6875, ((0.625 - 0.5) + 0.5 * (0.6875 - 0.625)) / 0.25), (1.0, ((0.625 - 0.5) + 0.5 * (0.75 - 0.625)) / 0.25),
            (0.4, 0.0), (1.0, 0.5 * 0.25 / 0.25),
        })
        {
            Assert.Equal(want, e.Fraction(v, vmin));
            vmin = e.NextVmin(vmin, v);
        }
        Assert.Equal(0.5, vmin);   // tracks the lowest voltage but not below Vd2
        // The WP20 spec's values (Vd1 0.7, Vd2 0.5, Frcel 0.8): 0.5 at 0.6 descending and 0.5 + 0.8 x 0.5 = 0.9 recovered.
        // Each is a few roundings on terms below 1, divided by 0.2: within 8 eps / 0.2.
        var s = new ElectronicLoadParameters(0.95, 0.7, 0.5, 0.8);
        Assert.True(Math.Abs(s.Fraction(0.6, 1.0) - 0.5) <= 8 * Eps / 0.2);
        Assert.True(Math.Abs(s.Fraction(1.0, 0.6) - 0.9) <= 8 * Eps / 0.2);
    }

    [Fact]
    public void In_the_composite_load_the_electronic_part_draws_its_fraction_of_its_power()
    {
        // Electronic load alone at the far end (all other fractions 0, so no static load): the bus draws the far-end power
        // through the network; at the far end the electronic load draws Fv1 Pe0 at its power factor. Check it at a dip.
        var x = new CompositeLoadExtensions { ElectronicFraction = 1.0, Electronic = new ElectronicLoadParameters(1.0, 0.75, 0.5, 0.5) };
        CompositeLoadData d = Network(0.0, x);
        CompositeLoadExtended.State m = Start(d, Complex.One, out _);
        double vf0 = m.FarEndVoltage.Magnitude;
        var v = new Complex(0.7, 0.0);
        for (int k = 0; k < 5; k++) { m.Commit(v); m.Step(k * 0.005, v, new[] { v }, 0.005); }
        double vf = m.FarEndVoltage.Magnitude, vmin = m.ElectronicVmin;
        Assert.InRange(vf, 0.5, 0.75);
        Assert.True(vf0 >= 0.75);
        // Vmin holds the lowest far-end voltage seen; the voltage is level after the first step at the dip, so Vmin is the
        // present voltage to the internal solve's tolerance (1e-13 per solve, a few solves).
        Assert.True(vmin <= vf && vf - vmin <= 1e-12, $"Vmin {vmin:R}, V {vf:R}");
        // Descending at Vmin: Fv1 = (V - Vd2) / (Vd1 - Vd2), a subtraction and an exact division by 0.25.
        Assert.True(Math.Abs(m.ElectronicFraction(vmin) - (vmin - 0.5) / 0.25) <= 2 * Eps);
        Assert.True(Math.Abs(m.ElectronicFraction(vmin) - e(vmin, vmin)) <= 2 * Eps);
        // The bus draws less than the starting 0.8 pu: the shed part is gone.
        Assert.True(Drawn(m, v).Real < 0.8);
        static double e(double v, double vmin) => new ElectronicLoadParameters(1.0, 0.75, 0.5, 0.5).Fraction(v, vmin);
    }

    // ----------------------------------------------------------------------------- 5. three-phase motor UV relays

    [Fact]
    public void Motor_a_under_voltage_relay_trips_its_fraction_on_time()
    {
        var x = new CompositeLoadExtensions { MotorARelay = new MotorUnderVoltageRelay(0.7, 0.02, 0.2, 0.5, 0.05, 0.3) };
        CompositeLoadExtended.State m = Start(Network(0.4, x), Complex.One, out _);
        double k0 = m.Motors[0].Def.K;
        // The bus steps to 0.6 pu at step 64 (t = 0.0625 s); steps of 2^-10 s. Stage 1 (Vtr1 0.7, Ttr1 0.02): the far-end
        // voltage is below 0.7 from step 64 (checked), so the timer reaches 0.02 s after ceil(20.48) = 21 steps, at the end of
        // step 84. Stage 2 (Vtr2 0.5) never sees the far end below 0.5 at this dip.
        var v1 = new Complex(0.6, 0.0);
        for (int k = 0; k < 200; k++)
        {
            Complex v = k < 64 ? Complex.One : v1;
            m.Commit(v);
            m.Step(k * Dt10, v, new[] { v }, Dt10);
            double vf = m.FarEndVoltage.Magnitude;
            if (k < 64) Assert.True(vf > 0.7);
            else Assert.InRange(vf, 0.5, 0.7);
            Assert.Equal(k < 84 ? 1.0 : 0.8, m.Motors[0].Connected);
        }
        Assert.Equal(k0 * 0.8, m.Motors[0].K);
        // A deeper dip trips stage 2 as well, ceil(0.05 x 1024) = ceil(51.2) = 52 steps after the far end falls below
        // Vtr2 = 0.5 and stays there (the motors' EMFs hold it up for the first steps); the fractions add.
        CompositeLoadExtended.State deep = Start(Network(0.4, x), Complex.One, out _);
        var v2 = new Complex(0.3, 0.0);
        int both = -1, below = -1;
        for (int k = 0; k < 400; k++)
        {
            deep.Step(k * Dt10, v2, new[] { v2 }, Dt10);
            bool low = deep.FarEndVoltage.Magnitude < 0.5;
            if (low && below < 0) below = k;
            if (below >= 0) Assert.True(low, $"step {k}: the far end rose above 0.5 again");
            if (both < 0 && deep.Motors[0].Connected < 0.6) both = k;
        }
        Assert.True(below >= 0);
        Assert.Equal(below + 51, both);
        Assert.True(Math.Abs(deep.Motors[0].Connected - 0.5) <= 2 * Eps);
    }

    // ------------------------------------------------------------------------------------------------- 6. LTC

    [Fact]
    public void The_tap_changer_moves_at_tdel_then_every_ttap_until_the_voltage_is_in_band()
    {
        // The WP20 spec's case: Tdel 30 s, Ttap 5 s, band [0.98, 1.02], step 0.00625, a stiff 0.95 pu with no load drop, so
        // the low side is 0.95 / tau with tau = 1 - n 0.00625. n = 4 gives 0.9744 (below 0.98), n = 5 gives 0.98065 (in the
        // band): five moves, at t = 30, 35, 40, 45 and 50 s. Steps of 2^-6 s make the timer sums exact.
        var ltc = new CompositeLoadLtc(new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 0.98, 1.02, 30.0, 5.0), 1.0);
        const double dt = 1.0 / 64.0;
        var moves = new List<double>();
        for (int k = 0; k < 80 * 64; k++)
            if (ltc.Advance(0.95 / ltc.Tap, dt)) moves.Add((k + 1) * dt);
        Assert.Equal(new[] { 30.0, 35.0, 40.0, 45.0, 50.0 }, moves);
        Assert.Equal(-5, ltc.Position);
        Assert.Equal(1.0 + -5 * 0.00625, ltc.Tap);
        Assert.InRange(0.95 / ltc.Tap, 0.98, 1.02);
        // A high voltage raises the ratio; the limit stops it (Tmax 1.0125 = 2 steps up).
        var up = new CompositeLoadLtc(new CompositeLoadLtcParameters(0.9, 1.0125, 0.00625, 0.98, 1.02, 1.0, 0.5), 1.0);
        int n = 0;
        for (int k = 0; k < 10 * 64; k++) if (up.Advance(1.2, dt)) n++;
        Assert.Equal(2, n);
        Assert.Equal(1.0125, up.Tap);
    }

    [Fact]
    public void In_the_composite_load_the_tap_raises_the_low_side_after_a_dip()
    {
        // Start in the band; the bus steps to 0.95 pu at t = 1 s, putting the low side below Vmin. The first move is Tdel = 1 s
        // later (64 steps of 2^-6 s, at the end of step 64 + 63 = 127), the next ones Ttap = 0.5 s apart, each raising the
        // low-side voltage.
        CompositeLoadExtended.State probe = Start(Network(0.4, new CompositeLoadExtensions()), Complex.One, out _);
        double vl0 = probe.LowSideVoltage;
        var x = new CompositeLoadExtensions { Ltc = new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, vl0 - 0.01, vl0 + 0.05, 1.0, 0.5) };
        CompositeLoadExtended.State m = Start(Network(0.4, x), Complex.One, out _);
        const double dt = 1.0 / 64.0;
        var v = new Complex(0.95, 0.0);
        var moveSteps = new List<int>();
        double lastVl = 0;
        for (int k = 0; k < 6 * 64; k++)
        {
            Complex vb = k < 64 ? Complex.One : v;
            int before = m.Ltc!.Moves;
            m.Commit(vb);
            m.Step(k * dt, vb, new[] { vb }, dt);
            if (m.Ltc.Moves > before)
            {
                moveSteps.Add(k);
                lastVl = m.LowSideVoltage;   // solved on this step, before the new tap
            }
            else if (moveSteps.Count > 0 && k == moveSteps[^1] + 1)
                Assert.True(m.LowSideVoltage > lastVl, $"step {k}: the low side did not rise after the move");
        }
        Assert.True(moveSteps.Count >= 2);
        Assert.Equal(127, moveSteps[0]);
        Assert.Equal(127 + 32, moveSteps[1]);
        Assert.True(m.Tap < 1.0);
    }

    // ------------------------------------------------------------------------------------- SciPy step response

    // Pinned from tests/tooling/dynamic-library-reference.py case 20 (SciPy 1.18.1), at 0.05, 0.1, 0.2, 0.5, 1, 2, 5 s.
    private static readonly double[] Times = { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };
    private static readonly double[] SpeedARef = { 0.9709495570573944, 0.9700354988155071, 0.9693313148088263, 0.9691426081838337, 0.9691406936260721, 0.9691406927262787, 0.9691406927262788 };
    private static readonly double[] SpeedBRef = { 0.9738786894831445, 0.9731266068233351, 0.9729169545707801, 0.9729214477714089, 0.9729215106922915, 0.9729215107218491, 0.9729215107218491 };

    private static double WorstError(DynamicLoadPlan plan, int motor, double[] reference, double dt)
    {
        var m = (CompositeLoadExtended.State)plan.CreateModel();
        var v = new Complex(0.95, 0.0);
        int steps = (int)Math.Round(5.0 / dt), i = 0;
        double worst = 0;
        for (int k = 1; k <= steps; k++)
        {
            m.Commit(v);
            m.Step((k - 1) * dt, v, new[] { v }, dt);
            if (i < Times.Length && k == (int)Math.Round(Times[i] / dt))
            {
                worst = Math.Max(worst, Math.Abs(m.Motors[motor].Motor.Speed - reference[i]));
                i++;
            }
        }
        Assert.Equal(Times.Length, i);
        return worst;
    }

    [Fact]
    public void The_extended_composite_load_follows_the_scipy_integration_at_first_order()
    {
        CompositeLoadData d = Network(0.3, Case20Parts);
        DynamicLoadPlan plan = d.Plan(Complex.One, 100.0, Hz);
        var probe = (CompositeLoadExtended.State)plan.CreateModel();
        Complex s0 = Drawn(probe, Complex.One);
        Assert.True(Math.Abs(s0.Real - 0.8) <= 1e-13 && Math.Abs(s0.Imaginary - 0.3) <= 1e-13, $"draws {s0} at t = 0");
        foreach ((int motor, double[] reference) in new[] { (0, SpeedARef), (1, SpeedBRef) })
        {
            double change = reference.Max(r => Math.Abs(r - probe.Motors[motor].Motor.Speed));
            double e1 = WorstError(plan, motor, reference, 1e-4), e2 = WorstError(plan, motor, reference, 5e-5);
            // The far-end voltage is solved at the start of each step and held (and motor D's admittance is held from the
            // step before), so the error is C dt (1 + c dt / tau): halving the step halves it, up to a relative
            // c dt / (2 tau) <= 1e-4 / 0.04 c = 0.0025 c (tau >= 0.02 s, the fastest motor pole); the window [0.45, 0.55]
            // admits c up to 40. And at 0.1 ms the error stays below 1 % of the change, as in WP3b.
            Assert.True(e1 <= 1e-2 * change, $"motor {motor}: error {e1:R} at 0.1 ms against a change of {change:R}");
            Assert.InRange(e2 / e1, 0.45, 0.55);
        }
    }

    // ----------------------------------------------------------------------------------------- refusals

    [Fact]
    public void Inconsistent_extension_data_is_refused_by_name()
    {
        Complex v0 = Complex.One;
        var over = new CompositeLoadExtensions { MotorB = new CompositeLoadMotor(0.5, 0.8, Motor(2.0)), ElectronicFraction = 0.2, Electronic = new ElectronicLoadParameters(0.95, 0.7, 0.5, 0.8) };
        Assert.Contains("above 1", Assert.ThrowsAny<ArgumentException>(() => Network(0.4, over).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        var noD = new CompositeLoadExtensions { MotorDFraction = 0.1 };
        Assert.Contains("motor D's data is missing", Assert.Throws<ArgumentException>(() => Network(0.4, noD).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        var highStall = new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { StallVoltage = 1.2 } };
        Assert.Contains("start stalled", Assert.Throws<InvalidOperationException>(() => Network(0.4, highStall).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        var highVd1 = new CompositeLoadExtensions { ElectronicFraction = 0.1, Electronic = new ElectronicLoadParameters(0.95, 1.2, 0.5, 0.8) };
        Assert.Contains("Vd1", Assert.Throws<InvalidOperationException>(() => Network(0.4, highVd1).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        var badTap = new CompositeLoadExtensions { Ltc = new CompositeLoadLtcParameters(1.01, 1.1, 0.00625, 0.98, 1.02, 30.0, 5.0) };
        Assert.Contains("LTC range", Assert.ThrowsAny<ArgumentException>(() => Network(0.4, badTap).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------ RMS study

    [Fact]
    public void The_full_composite_load_runs_in_the_case14_rms_study()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Rc5Wp20BitIdentityTests.Case14();
        CompositeLoadData cmld = Rc5Wp20BitIdentityTests.Wp3bCmld(4, 47.8, -3.9) with
        {
            MotorFraction = 0.3,
            Extensions = Case20Parts with { MotorD = StallMotor with { LoadingFactor = 0.8 } },
        };
        var loaded = new RmsLoadedMachineData(data, new DynamicLoadData[] { cmld });
        // At rest: the load draws its power throughout, within the converter iteration's tolerance as in WP3b
        // (2 |Y| 1e-10 with |Y| below 10 pu, plus rounding).
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, loaded, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.True(rest.ConverterIterationsConverged);
        Assert.All(rest.Converters![0].ActivePowerPu, p => Assert.True(Math.Abs(-p - 0.478) <= 2 * 10.0 * 1e-10 + 1e-13, $"P {p:R}"));
        // A bus-4 fault: the run converges and two runs give the same bits.
        MultiMachineSubtransient.Result f1 = RmsTransientStability.Study(m, pf, loaded, 4, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        MultiMachineSubtransient.Result f2 = RmsTransientStability.Study(m, pf, loaded, 4, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.True(f1.ConverterIterationsConverged);
        Assert.Equal(Rc5Wp20BitIdentityTests.Digest(f1), Rc5Wp20BitIdentityTests.Digest(f2));
        Assert.NotEqual(Rc5Wp20BitIdentityTests.Digest(rest), Rc5Wp20BitIdentityTests.Digest(f1));
    }
}
