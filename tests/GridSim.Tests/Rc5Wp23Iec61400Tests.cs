using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Iec61400;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP23: IEC 61400-27-1 style Type 4A and 4B wind turbines (2020 edition), built from public sources only: Dynawo
/// (MPL-2.0, commit 43812dfb, its IEC Modelica library read for structure, no code copied) and the CGMES 2.4.15 DY parameter
/// names. The IEC 61400-27-1 text was not used and these models are not checked against it. Verification here is analytical:
/// each expected value is a closed form derived in the test (the exact zero-order-hold lag, the ramp, the current limit
/// circle, the discrete two-mass oscillation), with every tolerance derived from the operation count. Dynawo does ship
/// reference curves for its Neplan WT4A and WT4B 2020 examples (Examples/references, P, Q and U at 1 ms); a decimated copy is
/// in fixtures/dynawo-iec-2020 (MPL-2.0, source and changes in its ATTRIBUTION.txt). They cannot be compared with these
/// models as built, for the reasons test 7 checks on the data itself (Rc5Batch2VerifyIec61400Tests).
/// Without IEC data the RMS path and the dynamics study are unchanged: digests pinned from the 2.1.127 Release Core dll before
/// any edit (test 8).
/// </summary>
public sealed class Rc5Wp23Iec61400Tests
{
    private const double U = 1.1102230246251565e-16;   // 2^-53, the unit roundoff

    // One cached options instance (CA1869): the same settings every serialisation used, so the JSON is unchanged.
    private static readonly JsonSerializerOptions NamedFloats = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // ------------------------------------------------------------------------------------------------ data

    /// <summary>A parameter set after Dynawo's Neplan WT4B 2020 example (Examples/Wind/IEC/Neplan/WT4BCurrentSource2020.mo):
    /// tg 0.01 s, dipmax 1, diq +-100, dpmax 1, tpord 0.05 s, tpaero 0.1 s, kqv 2, iqh1 1.05, iq limits +-1.05, imax 1.3,
    /// Hwtr 5 s, Hgen 1 s, kdrt 500, cdrt 15, PLL tpll 0.01 s with upll1 999 (always filtered) and upll2 0.13, filters 0.01 s.
    /// Tables constant 1.1 pu (Dynawo's defaults are lookup tables; a constant keeps every limit readable here).</summary>
    internal static Iec61400Data Unit(Iec61400TurbineType type, Iec61400QControlMode mode, int frtMode = 1, Iec61400Protection? protection = null) => new(
        type, 2020,
        new Iec61400GenSystem(1.0, -100, 100, 0.01),
        new Iec61400PControl(1.0, 0.05, 100, -100, type == Iec61400TurbineType.Type4A ? 0.05 : 0.0, type == Iec61400TurbineType.Type4B ? 0.1 : 0.0, false, 0.0),
        Q(mode, frtMode),
        new Iec61400CurrentLimit(1.3, 1.3, false, true, 20, 1.1, Iec61400Table.Constant(1.1), Iec61400Table.Constant(1.1)),
        type == Iec61400TurbineType.Type4B ? new Iec61400Mechanical(15, 1, 5, 500) : null,
        new Iec61400Pll(0.01, 999, 0.13), new Iec61400Measurement(0.01, 0.01, 0.01), protection);

    private static Iec61400QControl Q(Iec61400QControlMode mode, int frtMode = 1, double kqv = 2, double iqmin = -1.05, double iqh1 = 1.05,
        double tpost = 0.1, double udb = 0.1, double tuss = 1.0) =>
        new(mode, frtMode, iqh1, 1.05, iqmin, 0.0, 2.25, 10, 1.1, 2, 2, kqv, 0.8, -0.8, 0, 0, tpost, 0.05, tuss, -udb, udb, 1.1, 0.9, 0.9, 1.1, 0);

    /// <summary>System S: 100 MVA base; bus 1 (slack) holds a GENCLS with X'd = 0.1 pu and H = 1000 s (a constant EMF: in effect an
    /// infinite bus behind 0.1 pu); a lossless line of X = 0.1 pu to bus 2, where the turbine (100 MVA) generates 50 MW at 1 pu.</summary>
    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) SystemS(Iec61400Data unit)
    {
        var m = new GridModel
        {
            Name = "wp23-s", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, 0, Vg: 1.0, MBase: 100, H: 1000, Name: "source"),
                new Generator(2, 50, Vg: 1.0, Qmin: -999, Qmax: 999, MBase: 100, Name: "wt"),
            },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData> { Gencls.Data(1000, 0, 0.1), Gencls.Data(1, 0, 0.1) with { Iec61400 = unit } };
        return (m, pf, data);
    }

    // ------------------------------------------------------------------------------------------------ 1. flat start

    [Theory]
    [InlineData(Iec61400TurbineType.Type4A, Iec61400QControlMode.OpenLoopReactivePower)]
    [InlineData(Iec61400TurbineType.Type4B, Iec61400QControlMode.OpenLoopPowerFactor)]
    [InlineData(Iec61400TurbineType.Type4A, Iec61400QControlMode.Voltage)]
    [InlineData(Iec61400TurbineType.Type4B, Iec61400QControlMode.ReactivePower)]
    [InlineData(Iec61400TurbineType.Type4A, Iec61400QControlMode.PowerFactor)]
    public void A_turbine_on_a_stiff_bus_starts_flat_and_every_state_stays_put(Iec61400TurbineType type, Iec61400QControlMode mode)
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = SystemS(Unit(type, mode));
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsObserved(m, pf, data, Array.Empty<RmsEvent>(), new[] { 1 },
            stepSeconds: 0.005, durationSeconds: 2.0).Result;
        RmsConverterTrace c = Assert.Single(r.Converters!);
        RmsDeviceTrace d = Assert.Single(r.Devices!);
        Assert.Equal(401, d.Samples.Count);                       // 2 s at 5 ms, plus the t = 0 sample
        Assert.Equal(c.TerminalVoltagePu.Count, d.Samples.Count);
        // Bound: the power flow is solved to 1e-13 pu, so every input of the unit (voltage, P, Q at its terminal) is off its
        // initial value by at most that times the network's sensitivity (X <= 0.2 pu here, so about 2e-14 pu). The open-loop
        // states are lags of those inputs and move no further; an integrator (kiu 10, kiq 2.25 per s) can gather at most
        // 10 x 2e-14 x 2 s = 4e-13 over the run. 1e-12 covers both.
        for (int k = 0; k < d.Samples.Count; k++)
        {
            for (int s = 0; s < d.StateNames.Count; s++)
                Assert.True(Math.Abs(d.Samples[k][s] - d.Samples[0][s]) <= 1e-12, $"{type} {mode}: {d.StateNames[s]} moved at sample {k}");
            Assert.True(Math.Abs(c.TerminalVoltagePu[k] - c.TerminalVoltagePu[0]) <= 1e-12, $"V at sample {k}");
            Assert.True(Math.Abs(c.ActivePowerPu[k] - 0.5) <= 1e-12, $"P {c.ActivePowerPu[k]:R} at sample {k}");
            Assert.False(c.InDipMode[k]);
        }
        Assert.All(r.Machines[0].SpeedPu, w => Assert.True(Math.Abs(w) <= 1e-12));
        // The initial currents: ip0 = P0 / U0 and, in the frame Dynawo's RefFrameRotation gives, Q = -U iq (both within the same
        // 1e-12: the traced voltage and powers are the network's, off the power-flow values by the bound above).
        Assert.True(Math.Abs(0.5 / c.TerminalVoltagePu[0] - d.Samples[0][0]) <= 1e-12);
        Assert.True(Math.Abs(-c.ReactivePowerPu[0] / c.TerminalVoltagePu[0] - d.Samples[0][1]) <= 1e-12);
        Assert.Equal(Unit(type, mode).Description, d.Model);
    }

    // ------------------------------------------------------------------------------------------------ 2. generator lag

    [Fact]
    public void The_generator_lag_is_the_exact_zero_order_hold_step_response_at_every_sample()
    {
        // Tg = 0.01 s, dt = 0.01 s, no rate limit; ip steps from 0.5 to 0.6. Exact under the hold: ip(k dt) = 0.6 - 0.1 e^-k.
        var g = new Iec61400GenSystemModule(new Iec61400GenSystem(double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, 0.01), 0.5, 0.0);
        // Bound per sample. The block computes y' = u + (y - u) a with a = DetMath.Exp(-1) (fdlibm, under 1 ulp): y - u is exact
        // (Sterbenz), the product adds 0.1 U, the sum U/2 (ulp of [0.5, 1) is 2U); the errors contract by a each step, so they stay
        // below 0.6 U / (1 - e^-1) = 0.95 U; a's own error moves 0.1 a^k by at most 0.1 k a^(k-1) U/2 <= 0.05 U. The expected value
        // here (Math.Exp, under 1 ulp) adds 0.1 U + U/2. Total 1.65 U; asserted at 4 U.
        for (int k = 1; k <= 10; k++)
        {
            g.Step(0.6, 0.0, double.PositiveInfinity, -1.0, 1.0, 0.01);
            double expected = 0.6 - 0.1 * Math.Exp(-k);
            Assert.True(Math.Abs(g.Ip - expected) <= 4 * U, $"k {k}: {g.Ip:R} vs {expected:R}");
            Assert.Equal(0.0, g.Iq);
        }
        // The spec's sample: ip(0.02) = ip0 + 0.1 (1 - e^-2) = ip0 + 0.08646647167633873.
        var g2 = new Iec61400GenSystemModule(new Iec61400GenSystem(double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, 0.01), 0.5, 0.0);
        g2.Step(0.6, 0.0, double.PositiveInfinity, -1.0, 1.0, 0.01);
        g2.Step(0.6, 0.0, double.PositiveInfinity, -1.0, 1.0, 0.01);
        Assert.True(Math.Abs(g2.Ip - (0.5 + 0.08646647167633873)) <= 4 * U, $"{g2.Ip:R}");
    }

    // ------------------------------------------------------------------------------------------------ 3. rate limit

    [Fact]
    public void The_active_current_ramps_at_dipmax_and_lands_on_the_command_at_the_step_it_is_due()
    {
        // dipmax = 0.5 pu/s, Tg = 0 (the command at once, within the rate), dt = 0.01 s: 0.005 per step, so a step of 0.1 is
        // reached after 20 steps (t = 0.2 s). Bound per step: the rise 0.5 x 0.01 rounds once (0.005 U) and the sum once (U/2),
        // so after k steps the error is below k x 0.6 U; asserted at k U.
        var g = new Iec61400GenSystemModule(new Iec61400GenSystem(0.5, -0.5, 0.5, 0.0), 0.5, 0.0);
        for (int k = 1; k <= 25; k++)
        {
            g.Step(0.6, -0.1, double.PositiveInfinity, -1.0, 1.0, 0.01);
            if (k < 20)
            {
                Assert.True(Math.Abs(g.Ip - (0.5 + 0.005 * k)) <= k * U, $"ip at step {k}: {g.Ip:R}");
                Assert.True(Math.Abs(g.Iq - (-0.005 * k)) <= k * U, $"iq at step {k}: {g.Iq:R}");   // diqmin -0.5 pu/s
            }
            else if (k == 20)
            {
                Assert.True(Math.Abs(g.Ip - 0.6) <= 20 * U, $"ip at step 20: {g.Ip:R}");
                Assert.True(Math.Abs(g.Iq + 0.1) <= 20 * U, $"iq at step 20: {g.Iq:R}");
            }
            else
            {
                // Once the remaining change is inside the rate the block lands on the command exactly.
                Assert.Equal(0.6, g.Ip);
                Assert.Equal(-0.1, g.Iq);
            }
        }
    }

    [Fact]
    public void The_generator_system_holds_the_current_limits_without_windup()
    {
        var g = new Iec61400GenSystemModule(new Iec61400GenSystem(100, -100, 100, 0.0), 0.5, 0.0);
        g.Step(0.9, 0.9, 0.7, -0.3, 0.4, 0.01);
        Assert.Equal(0.7, g.Ip);
        Assert.Equal(0.4, g.Iq);
        g.Step(0.2, -0.9, 0.7, -0.3, 0.4, 0.01);   // the command turns back: the state leaves the limit at once
        Assert.Equal(0.2, g.Ip);
        Assert.Equal(-0.3, g.Iq);
    }

    // ------------------------------------------------------------------------------------------------ 4. fault ride-through

    [Theory]
    [InlineData(0.01, 5)]
    [InlineData(0.005, 10)]
    public void A_dip_switches_to_the_fault_state_at_once_injects_kqv_times_the_drop_and_the_post_fault_state_lasts_tpost(double dt, int postSteps)
    {
        // Open-loop reactive power (mode 2), ride-through mode 1 (fault current = base + iqv), kqv = 2, uqdip 0.9, no dead band,
        // tuss 1 s, tpost 0.05 s, iqmin -1.2 (so the first fault command 0.1 + 1.0 is not limited). Start: U 1, P 0.5, Q 0.1, so
        // the exporting reactive current is 0.1 and the IEC-frame command -0.1 (Q = -U iq).
        Iec61400QControl p = Q(Iec61400QControlMode.OpenLoopReactivePower, frtMode: 1, kqv: 2, iqmin: -1.2, iqh1: 1.2, tpost: 0.05, udb: 0.0);
        var q = new Iec61400QControlModule(p, 1.0, 0.5, 0.1);
        int before = (int)Math.Round(0.1 / dt), during = (int)Math.Round(0.1 / dt);
        double a = Math.Exp(-dt / 1.0);
        for (int k = 0; k < before; k++)
        {
            Assert.Equal(-0.1, q.Step(1.0, 0.5, 0.1, dt));   // the order lag of 0.1 toward 0.1 stays exactly 0.1
            Assert.Equal(0, q.FrtState);
        }
        // The dip to 0.5 pu: the first step after 0.1 s is in the fault state. The steady-state voltage is still exactly 1, so
        // iqv = kqv (0.5 - 1) = -1.0 and the command is -0.1 - 1.0 = -1.1 (export 1.1 pu: iq0 + kqv (1 - 0.5)).
        double first = q.Step(0.5, 0.5, 0.1, dt);
        Assert.Equal(1, q.FrtState);
        Assert.Equal(-1.1, first, 15);
        // Then the steady-state voltage lags toward 0.5: Uss_n = 0.5 + 0.5 a^n, so the command is -0.1 + 2 (0.5 - Uss_n) =
        // -0.1 - a^n. Bound after n steps: each lag step (u + (y - u) a; the difference is exact, the product and the sum round
        // once each, values in [0.5, 1)) adds at most U and a = e^-dt is too close to 1 to contract it, so n U; a's own error
        // (fdlibm, under 1 ulp) adds n U / 2; doubling gives 3 n U, the command's sum U. The expected value (Math.Exp and
        // Math.Pow, each under 1 ulp) adds (n + 1) U. Total (4 n + 2) U; asserted at twice that.
        for (int n = 1; n < during; n++)
        {
            double cmd = q.Step(0.5, 0.5, 0.1, dt);
            Assert.Equal(1, q.FrtState);
            double expected = -0.1 - Math.Pow(a, n);
            Assert.True(Math.Abs(cmd - expected) <= (8 * n + 4) * U, $"fault step {n}: {cmd:R} vs {expected:R}");
            Assert.Equal(0.1, q.OpenLoopCurrent);   // frozen through the fault
        }
        // Back to 1 pu: post-fault for tpost = 0.05 s, counted in whole steps (5 at 10 ms, 10 at 5 ms), then normal. In the
        // post-fault state ride-through mode 1 keeps the fault command, iqbase + iqv: Uss left the fault at 0.5 + 0.5 a^D (D the
        // fault steps) and lags back toward 1, Uss_m = 1 - 0.5 (1 - a^D) a^m, so the command is -0.1 + 2 (1 - Uss_m) =
        // -0.1 + (1 - a^D) a^m, inside [iqmin, iqh1]. Bound as in the fault, over D + m lag steps: (8 (D + m) + 4) U.
        double leftAt = Math.Pow(a, during);
        for (int n = 0; n < postSteps; n++)
        {
            double cmd = q.Step(1.0, 0.5, 0.1, dt);
            Assert.True(q.FrtState == 2, $"post step {n}: state {q.FrtState}");
            double expected = -0.1 + (1.0 - leftAt) * Math.Pow(a, n);
            Assert.True(Math.Abs(cmd - expected) <= (8 * (during + n) + 4) * U, $"post step {n}: {cmd:R} vs {expected:R}");
        }
        Assert.Equal(-0.1, q.Step(1.0, 0.5, 0.1, dt));
        Assert.Equal(0, q.FrtState);
    }

    [Fact]
    public void The_fault_command_is_held_inside_iqmin_and_iqh1()
    {
        // As above with the Neplan limits: iqmin -1.05, so the first fault command -1.1 is held at -1.05 (export 1.05 pu).
        var q = new Iec61400QControlModule(Q(Iec61400QControlMode.OpenLoopReactivePower, frtMode: 1, kqv: 2, udb: 0.0), 1.0, 0.5, 0.1);
        Assert.Equal(-0.1, q.Step(1.0, 0.5, 0.1, 0.01));
        Assert.Equal(-1.05, q.Step(0.5, 0.5, 0.1, 0.01));
        // Ride-through mode 0 drops the base current: the command is iqv alone, -1.0.
        var q0 = new Iec61400QControlModule(Q(Iec61400QControlMode.OpenLoopReactivePower, frtMode: 0, kqv: 2, udb: 0.0), 1.0, 0.5, 0.1);
        q0.Step(1.0, 0.5, 0.1, 0.01);
        Assert.Equal(-1.0, q0.Step(0.5, 0.5, 0.1, 0.01));
        // An over-voltage above uqrise (1.1) is a fault state too (Dynawo QControl2020), with no post-fault state after it.
        var q1 = new Iec61400QControlModule(Q(Iec61400QControlMode.OpenLoopReactivePower, frtMode: 1, kqv: 2, udb: 0.0), 1.0, 0.5, 0.1);
        q1.Step(1.15, 0.5, 0.1, 0.01);
        Assert.Equal(1, q1.FrtState);
        q1.Step(1.0, 0.5, 0.1, 0.01);
        Assert.Equal(0, q1.FrtState);
    }

    // ------------------------------------------------------------------------------------------------ 5. current limiter

    [Fact]
    public void Reactive_priority_during_a_fault_limits_the_active_current_to_the_rest_of_the_circle()
    {
        // imaxdip = 1.1, reactive priority, fault state 1, iq command 1.0, ip command 0.8, tables 2.0 (not binding):
        // ipmax = sqrt(1.1^2 - 1.0^2) = sqrt(0.21) = 0.45825756949558394. Bound against the literal: fl(1.1)^2 is off 1.21 by
        // under 2.2 x 1.1 U/2 + U < 2.3 U; 1.21 - 1 is exact (Sterbenz); the square root halves the relative error, so
        // 2.3 U / (2 x 0.458) + U/2 < 3.1 U, plus the literal's own rounding U/2: asserted at 4 U.
        var lim = new Iec61400CurrentLimit(1.1, 1.1, false, true, 0.0, 2.0, Iec61400Table.Constant(2.0), Iec61400Table.Constant(2.0));
        (double ipMax, double iqMin, double iqMax) = Iec61400CurrentLimiterModule.Limits(lim, 0.5, 1.0, 1, 0.8, 1.0);
        Assert.Equal(Math.Sqrt(1.1 * 1.1 - 1.0 * 1.0), ipMax);
        Assert.True(Math.Abs(ipMax - 0.45825756949558394) <= 4 * U, $"{ipMax:R}");
        Assert.Equal(2.0, iqMax);                 // reactive priority: the table alone
        // The capacitive limit is max(-iqmax, kpqu (U - upqumax)) = max(-2, 0 x (0.5 - 2)) = 0 (below iqmax, so it applies): with
        // kpqu = 0 no capacitive (negative, in the IEC frame) current is allowed. The command here is inductive (+1.0).
        Assert.Equal(0.0, iqMin);
        // The generator system then holds ip at that limit.
        var g = new Iec61400GenSystemModule(new Iec61400GenSystem(100, -100, 100, 0.0), 0.0, 0.0);
        g.Step(0.8, 1.0, ipMax, iqMin, iqMax, 0.01);
        Assert.Equal(ipMax, g.Ip);
        Assert.Equal(1.0, g.Iq);
    }

    [Fact]
    public void Outside_a_fault_the_active_current_has_priority_and_the_capacitive_limit_follows_kpqu()
    {
        // Normal state (0): the reactive limit is what the active command leaves, sqrt(1.1^2 - 0.8^2), and ipmax is the table.
        var lim = new Iec61400CurrentLimit(1.1, 1.1, false, true, 20.0, 1.1, Iec61400Table.Constant(2.0), Iec61400Table.Constant(2.0));
        (double ipMax, double iqMin, double iqMax) = Iec61400CurrentLimiterModule.Limits(lim, 1.0, 1.0, 0, 0.8, 1.0);
        Assert.Equal(2.0, ipMax);
        Assert.Equal(Math.Sqrt(1.1 * 1.1 - 0.8 * 0.8), iqMax);
        // Independently: sqrt(1.21 - 0.64) = sqrt(0.57) = 0.7549834435270749 (the double nearest). Bound: fl(1.1) and fl(0.8)
        // are off by U relative, so their rounded squares are off 1.21 and 0.64 by at most 3 x 1.21 U and 3 x 0.64 U; the
        // difference rounds once more (0.57 U): 6.1 U in all on 0.57, a relative 10.7 U, which the square root halves to
        // 5.4 U relative, 4.1 U absolute at 0.755, plus its own rounding (0.76 U) and the literal's (0.38 U): 5.3 U; at 8 U.
        Assert.True(Math.Abs(iqMax - 0.7549834435270749) <= 8 * U, $"{iqMax:R}");
        // At U = 1, kpqu (U - upqumax) = 20 x -0.1 = -2 is below -iqmax, so iqmin = -iqmax.
        Assert.Equal(-iqMax, iqMin);
        // At U = 1.2 the capacitive limit rises to kpqu (U - upqumax) = 20 x (1.2 - 1.1), still below iqmax (the table, 2.0, with
        // reactive priority in a fault): no capacitive current is allowed above upqumax.
        (_, double iqMin2, double iqMax2) = Iec61400CurrentLimiterModule.Limits(lim, 1.2, 1.0, 1, 0.0, 0.5);
        Assert.Equal(2.0, iqMax2);
        Assert.Equal(20.0 * (1.2 - 1.1), iqMin2);
        // Stator current limitation (mdfslim): the active command is divided by the generator speed and ipmax multiplied by it.
        var lims = lim with { Mdfslim = true };
        (double ipMaxS, _, _) = Iec61400CurrentLimiterModule.Limits(lims, 1.0, 0.9, 0, 0.8, 1.0);
        Assert.Equal(0.9 * 2.0, ipMaxS);
    }

    // ------------------------------------------------------------------------------------------------ 6. two-mass model

    [Fact]
    public void The_undamped_drive_train_oscillates_at_the_two_mass_frequency_matching_the_discrete_closed_form_at_every_step()
    {
        // Dynawo Mechanical: shaft torque T = cdrt (w_wtr - w_gen) + kdrt theta with d theta/dt = w_wtr - w_gen. Dynawo's twist
        // integrator has gain 1 (its PI block has no angle base factor; the spec's w_b is 1 here). With cdrt = 0 and zero
        // aerodynamic and air-gap power, the relative speed v = w_wtr - w_gen obeys dv/dt = -kdrt theta (1/(2 Hwtr) + 1/(2 Hgen)),
        // so theta'' = -wn^2 theta with wn^2 = kdrt (1/(2 Hwtr) + 1/(2 Hgen)). Neplan values: kdrt 500, Hwtr 5, Hgen 1:
        // wn = sqrt(500 x 0.6) = sqrt(300) = 17.3205 rad/s, period 2 pi / wn = 0.36276 s (2.76 Hz).
        //
        // The module's step (semi-implicit Euler: v' = v - h wn^2 theta, theta' = theta + h v') is the map [[1, -h wn^2], [h,
        // 1 - h^2 wn^2]] on (v, theta), determinant 1 and trace 2 - h^2 wn^2: eigenvalues e^(+-j phi) with cos phi = 1 - h^2 wn^2 / 2,
        // that is phi = 2 asin(h wn / 2). From v0 = 0, theta1 = theta0 (1 - h^2 wn^2), so
        //   theta_k = theta0 (cos k phi - c sin k phi),  c = h wn / (2 sqrt(1 - h^2 wn^2 / 4)).
        const double h = 0.001, kdrt = 500, hwtr = 5, hgen = 1, torque0 = 0.5;
        double wn = Math.Sqrt(kdrt * (1.0 / (2 * hwtr) + 1.0 / (2 * hgen)));
        double phi = 2.0 * Math.Asin(h * wn / 2.0);
        double c = h * wn / (2.0 * Math.Sqrt(1.0 - h * h * wn * wn / 4.0));
        var mech = new Iec61400MechanicalModule(new Iec61400Mechanical(0.0, hgen, hwtr, kdrt), torque0);
        double theta0 = torque0 / kdrt;
        Assert.Equal(theta0, mech.Twist);
        Assert.Equal(wn, mech.NaturalFrequency);
        // Bound per step k. Each step rounds the two speeds (near 1: U each) and the twist (U theta0 for the sum, U for the
        // product's magnitude below theta0); a speed error d moves theta by at most d / wn in amplitude, and the map in the scaled
        // coordinates (theta, v / wn) is a rotation up to the factor 1 / sqrt(1 - h^2 wn^2 / 4) < 1.0001, so errors add at most
        // linearly: k (4 U / wn + 2 U theta0). The closed form here adds k phi x 3 U x theta0 (1 + c) for the angle and 4 U theta0.
        // Asserted at twice the sum.
        var crossings = new List<int>();
        double previous = theta0;
        for (int k = 1; k <= 1500; k++)
        {
            mech.Step(0.0, 0.0, h);
            double expected = theta0 * (Math.Cos(k * phi) - c * Math.Sin(k * phi));
            double bound = 2 * (k * (4 * U / wn + 2 * U * theta0) + k * phi * 3 * U * theta0 * (1 + c) + 4 * U * theta0);
            Assert.True(Math.Abs(mech.Twist - expected) <= bound, $"step {k}: {mech.Twist:R} vs {expected:R} (bound {bound:R})");
            if (Math.Sign(mech.Twist) != Math.Sign(previous) && mech.Twist != 0.0) crossings.Add(k);
            previous = mech.Twist;
        }
        // The zero crossings of the continuous solution theta0 cos(wn t) are at (2j + 1) pi / (2 wn); the discrete one crosses
        // within a step of each (its frequency phi / h differs from wn by a relative h^2 wn^2 / 24 = 1.25e-5, and its phase by
        // atan c = 0.0087 rad, half a step). The period from every second crossing is 2 pi / wn to the step.
        Assert.True(crossings.Count >= 8, $"{crossings.Count} crossings");
        for (int j = 0; j < 8; j++)
            Assert.True(Math.Abs(crossings[j] * h - (2 * j + 1) * Math.PI / (2 * wn)) <= h, $"crossing {j} at step {crossings[j]}");
        for (int j = 0; j + 2 < crossings.Count; j++)
            Assert.True(Math.Abs((crossings[j + 2] - crossings[j]) * h - 2 * Math.PI / wn) <= h, $"period from crossing {j}");
    }

    [Fact]
    public void A_step_too_long_for_the_drive_train_is_refused()
    {
        // wn = sqrt(300) = 17.32 rad/s, so the semi-implicit step needs dt < 2 / wn = 0.1155 s; the damping rate
        // cdrt (1/(2 Hwtr) + 1/(2 Hgen)) = 15 x 0.6 = 9 /s needs dt < 0.222 s. A 0.12 s step breaks the first.
        var unit = new RmsConverterUnit(2, "wt", 1.0, 0.5, 0.1, Complex.One, new RmsConverterParameters())
            { Iec61400 = Unit(Iec61400TurbineType.Type4B, Iec61400QControlMode.OpenLoopReactivePower) };
        IRmsConverterModel model = unit.CreateModel(new[] { 2 });
        var ex = Assert.Throws<InvalidOperationException>(() => model.Step(0.0, Complex.One, new[] { Complex.One }, 0.12));
        Assert.Contains("dt wn < 2", ex.Message, StringComparison.Ordinal);
        // A changed step size is refused too: the timers count whole steps.
        IRmsConverterModel model2 = unit.CreateModel(new[] { 2 });
        model2.Step(0.0, Complex.One, new[] { Complex.One }, 0.01);
        Assert.Throws<InvalidOperationException>(() => model2.Step(0.01, Complex.One, new[] { Complex.One }, 0.005));
    }

    // ------------------------------------------------------------------------------------------------ protection

    [Theory]
    [InlineData(0.01, 6)]
    [InlineData(0.005, 11)]
    public void Under_voltage_protection_counts_whole_steps_and_a_trip_disconnects_the_unit(double dt, int tripStep)
    {
        // uunder 0.85 with an allowed time of 0.05 s at any level, no filter: the unit trips at the first step whose time in the
        // condition (n dt, n = 0 at the step it starts) exceeds 0.05 s, counted in whole steps: n = 6 at 10 ms, n = 11 at 5 ms.
        var prot = new Iec61400Protection(1.2, 0.85, 1.1, 0.9, Iec61400Table.Constant(1.0), Iec61400Table.Constant(0.05),
            Iec61400Table.Constant(1.0), Iec61400Table.Constant(1.0), 0.0, 0.01, 1.0);
        var unit = new RmsConverterUnit(2, "wt", 1.0, 0.5, 0.1, Complex.One, new RmsConverterParameters())
            { Iec61400 = Unit(Iec61400TurbineType.Type4A, Iec61400QControlMode.OpenLoopReactivePower, protection: prot) };
        var model = (Iec61400State)unit.CreateModel(new[] { 2 });
        Complex low = new(0.5, 0.0);
        for (int n = 0; n <= tripStep; n++)
        {
            Assert.Null(model.TripReason);
            Assert.NotEqual(Complex.Zero, model.Commit(low));
            model.Step(n * dt, low, new[] { low }, dt);
        }
        Assert.NotNull(model.TripReason);
        Assert.Contains("under-voltage", model.TripReason, StringComparison.Ordinal);
        Assert.Equal(Complex.Zero, model.TrialCurrent(Complex.One));
        Assert.Equal(Complex.Zero, model.Commit(Complex.One));
        Assert.Equal(0.0, model.ActiveCurrent);
    }

    // ------------------------------------------------------------------------------------------------ refusals

    [Fact]
    public void Data_outside_the_built_structure_is_refused_by_name()
    {
        Iec61400Data a = Unit(Iec61400TurbineType.Type4A, Iec61400QControlMode.Voltage);
        Assert.Contains(Iec61400Data.Problems(a with { Edition = 2015 }), s => s.Contains("2015 structure is not", StringComparison.Ordinal));
        Assert.Contains(Iec61400Data.Problems(a with { Mechanical = new Iec61400Mechanical(1, 1, 1, 1) }), s => s.Contains("Type 4A", StringComparison.Ordinal));
        Assert.Contains(Iec61400Data.Problems(a with { Type = Iec61400TurbineType.Type4B }), s => s.Contains("Type 4B", StringComparison.Ordinal));
        Assert.Contains(Iec61400Data.Problems(a with { QControl = a.QControl with { FrtMode = 3 } }), s => s.Contains("mode 3", StringComparison.Ordinal));
        Assert.Contains(Iec61400Data.Problems(a with { CurrentLimit = a.CurrentLimit with { Ipvdl = new Iec61400Table(new[] { 1.0, 0.5 }, new[] { 1.0, 1.0 }) } }),
            s => s.Contains("rise strictly", StringComparison.Ordinal));
        Assert.Empty(Iec61400Data.Problems(a));
        // A start inside the fault band has no quiet start. Open-loop reactive power, so no voltage-reference check (umin 0.9,
        // which voltage control would apply first) can refuse it before the fault-band check: U0 0.85 < uqdip 0.9.
        ArgumentException band = Assert.Throws<ArgumentException>(() =>
            new Iec61400QControlModule(Q(Iec61400QControlMode.OpenLoopReactivePower), 0.85, 0.5, 0.1));
        Assert.Contains("outside the normal band [uqdip 0.9, uqrise 1.1]", band.Message, StringComparison.Ordinal);
        Assert.Contains("no quiet start", band.Message, StringComparison.Ordinal);
        // A start whose currents break the limits: P = 1.5 pu on a 1.1 pu active limit.
        var unit = new RmsConverterUnit(2, "wt", 1.0, 1.5, 0.0, Complex.One, new RmsConverterParameters()) { Iec61400 = a with { QControl = Q(Iec61400QControlMode.OpenLoopReactivePower) } };
        Assert.Throws<ArgumentOutOfRangeException>(() => unit.CreateModel(new[] { 2 }));
    }

    [Fact]
    public void A_unit_takes_one_dynamic_model()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = SystemS(Unit(Iec61400TurbineType.Type4A, Iec61400QControlMode.Voltage));
        data[1] = data[1] with { Renewable = new WeccPlant(new RegcaParameters(), new ReecParameters()) };
        var ex = Assert.Throws<ArgumentException>(() => SubtransientInit.FromSolvedCase(m, pf, data));
        Assert.Contains("one dynamic model", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ 8. bit identity

    private static string Digest(MultiMachineSubtransient.Result r)
    {
        var bytes = new List<byte>();
        void Add(IEnumerable<double> xs) { foreach (double x in xs) bytes.AddRange(BitConverter.GetBytes(x)); }
        foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
        {
            Add(c.TerminalVoltagePu); Add(c.ActivePowerPu); Add(c.ReactivePowerPu); Add(c.ActiveCurrentPu); Add(c.ReactiveCurrentPu);
            foreach (bool d in c.InDipMode) bytes.Add(d ? (byte)1 : (byte)0);
        }
        foreach (MultiMachineSubtransient.MachineTrace m in r.Machines) { Add(m.DeltaRad); Add(m.SpeedPu); Add(m.EqppPu); Add(m.EddpPu); }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    /// <summary>Pins taken before any WP23 edit from the 2.1.127 Release Core dll (built from the 2.1.127 source; sha256
    /// 8124157c5bd068f9cf9c55a8dc152262cf0c870db32afea140649ada61604062) by a scratch console probe running exactly this code:
    /// (a) the WSCC-9 WECC fault run of WeccRenewableDigestTests (its own pin, reproduced), (b) the same case with every unit a
    /// machine, (c) the dynamics study of case14 with a bus-4 fault, serialised. Re-checked by hand with the 2.1.128 build: equal.</summary>
    [Fact]
    public void Without_iec_data_the_rms_path_and_the_dynamics_study_are_unchanged()
    {
        GridModel m9 = GridSim.Core.IO.IeeeCases.Case9();
        PowerFlowResult pf9 = new NewtonRaphsonPowerFlow().Solve(m9);
        var xdp = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var reg = new RegcaParameters();
        var reec = new ReecParameters { Qmax = 0.6, Qmin = -0.6, Imax = 1.2, Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 },
            Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 }, QFlag = 1, VFlag = 1, Thld = 0.05, Iqfrz = 0.1, Thld2 = 0.05 };
        var wecc = m9.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = new WeccPlant(reg, reec) } : d;
        }).ToList();
        Assert.Equal("FD122763FE365F3BF4C69BF07D2564BC7D8D0D14D75BAC6BD65BAE966B1AFC85",
            Digest(RmsTransientStability.Study(m9, pf9, wecc, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0)));
        var machines = m9.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            return new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
        }).ToList();
        Assert.Equal("48F98150F5A883CE24EA66911A7E31D540C10E48AD29457A2B08EE755C25C034",
            Digest(RmsTransientStability.Study(m9, pf9, machines, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0)));

        DynamicsStudyResult res = DynamicsStudyRunner.Run(BitIdentityRequest());
        string json = JsonSerializer.Serialize(res, NamedFloats);
        // DYN-05 (re-pinned, 2.1.161, ruling 12): the coverage's reader notes gain the HYGOV "approximated" note for the
        // study's one HYGOV record (+413 characters); nothing else in the result changes.
        Assert.Contains("HYGOV for machine", json, StringComparison.Ordinal);
        Assert.Equal(24598, json.Length);
        // The proof: with that one note taken out, the result is the 2.1.160 bytes (the old pin).
        string before = System.Text.RegularExpressions.Regex.Replace(json, ",\"HYGOV for machine[^\"]*\"|\"HYGOV for machine[^\"]*\",?", "");
        Assert.Equal("655E2D901E77BBCCB0470C16F653B7102DE9A4A0809F6DCB4EEC76A0E17F5E30", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before))));
    }

    private static DynamicsStudyRequest BitIdentityRequest()
    {
        var cases = new[] { new DynamicsCaseRequest("f4", DynamicsCaseKind.Events, new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1, Bus: 4), new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.15, Bus: 4),
            new DynamicsEventRequest(DynamicsEventKind.BranchTrip, 0.15, Branch: new DynamicsBranchId(4, 5, "1")),
        }) };
        var ch = new List<DynamicsChannelRequest> { new(DynamicsChannelKind.RotorAngle, new DynamicsMachineId(2, "1")), new(DynamicsChannelKind.BusVoltage, Bus: 4) };
        return new DynamicsStudyRequest(Raw(Psse("case14.raw")), Text(Study("case14-study.dyr")), "case14.dyr", Schemas,
            new DynamicsInitialisation(1e-8, 100, DynamicsUnmodelledPolicy.Refuse), 0.005, 1.0, new DynamicsCriteria(2.0 * Math.PI, 180.0),
            new DynamicsMachineId(1, "1"), ch, cases, 1);
    }

    // ------------------------------------------------------------------------------------------------ dynamics study

    /// <summary>case14-study.dyr with every record of the bus-<paramref name="bus"/> unit removed (a record runs to its '/').</summary>
    internal static string DyrWithout(int bus)
    {
        string b = bus.ToString(CultureInfo.InvariantCulture);
        var kept = new List<string>();
        bool skip = false;
        foreach (string line in Text(Study("case14-study.dyr")).Split('\n'))
        {
            string t = line.TrimStart();
            if (!skip && (t.StartsWith(b + " ", StringComparison.Ordinal) || t.StartsWith(b + ",", StringComparison.Ordinal))) skip = true;
            if (!skip) kept.Add(line);
            else if (line.TrimEnd().EndsWith('/')) skip = false;
        }
        return string.Join('\n', kept);
    }

    private static DynamicsStudyRequest IecRequest(string dyr, params Iec61400StudyUnit[] units)
    {
        var channels = new List<DynamicsChannelRequest>
        {
            new(DynamicsChannelKind.RotorAngle, M(3)), new(DynamicsChannelKind.Speed, M(3)), new(DynamicsChannelKind.BusVoltage, Bus: 2),
        };
        return Request(Raw(Psse("case14.raw")), dyr, new[] { Fault4("f4") }, channels: channels, duration: 1.0) with { Iec61400Units = units };
    }

    [Fact]
    public void A_dynamics_study_binds_an_iec_unit_in_place_of_its_dyr_records_and_repeats_bit_for_bit()
    {
        Iec61400Data data = Unit(Iec61400TurbineType.Type4B, Iec61400QControlMode.Voltage);
        DynamicsStudyRequest request = IecRequest(DyrWithout(2), new Iec61400StudyUnit(M(2), data));
        DynamicsStudyResult a = DynamicsStudyRunner.Run(request);
        DynamicsUnitCoverage unit = a.Coverage.Units.Single(u => u.Bus == 2);
        Assert.Equal("BOUND", unit.Status);
        Assert.Equal(data.Description, unit.Machine);
        Assert.True(a.Coverage.Simulable);
        // The fault at bus 4 cleared after 50 ms with the line 4-5 tripped: case14 stays in synchronism.
        Assert.Equal(DynamicsDisposition.Stable, a.Cases[0].Disposition);
        DynamicsStudyResult b = DynamicsStudyRunner.Run(request);
        var o = NamedFloats;
        Assert.Equal(JsonSerializer.Serialize(a, o), JsonSerializer.Serialize(b, o));
    }

    [Fact]
    public void A_dynamics_study_refuses_an_iec_unit_with_dyr_records_or_with_the_adaptive_run()
    {
        Iec61400Data data = Unit(Iec61400TurbineType.Type4A, Iec61400QControlMode.Voltage);
        var ex = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(IecRequest(Text(Study("case14-study.dyr")), new Iec61400StudyUnit(M(2), data))));
        Assert.Contains("has DYR records", ex.Message, StringComparison.Ordinal);
        DynamicsStudyRequest adaptive = IecRequest(DyrWithout(2), new Iec61400StudyUnit(M(2), data)) with
        {
            Adaptive = new DynamicsAdaptiveRequest(new GridSim.Core.Dynamics.Models.Adaptive.AdaptiveOptions(), 1e-4),
        };
        Assert.Contains("adaptive", Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(adaptive)).Message, StringComparison.Ordinal);
        DynamicsStudyRequest twice = IecRequest(DyrWithout(2), new Iec61400StudyUnit(M(2), data), new Iec61400StudyUnit(M(2), data));
        Assert.Contains("given twice", Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(twice)).Message, StringComparison.Ordinal);
        DynamicsStudyRequest missing = IecRequest(DyrWithout(2), new Iec61400StudyUnit(M(99), data));
        Assert.Contains("holds no", Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(missing)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ study file

    /// <summary>The iec61400 member of a study file for a Type 4B unit at bus 2 (Neplan values; see <see cref="Unit"/>).</summary>
    internal static object StudyMember(string type = "4B", bool withMechanical = true, object? pControl = null) => new Dictionary<string, object>
    {
        ["machine"] = new { bus = 2, id = "1" },
        ["type"] = type,
        ["edition"] = 2020,
        ["genSystem"] = new { dipmax = 1.0, diqmin = -100.0, diqmax = 100.0, tg = 0.01 },
        ["pControl"] = pControl ?? (type == "4B"
            ? new { dpmax = 1.0, tpord = 0.05, dprefmax = 100.0, dprefmin = -100.0, mpuscale = false, updip = 0.0, tpaero = 0.1 }
            : (object)new { dpmax = 1.0, tpord = 0.05, dprefmax = 100.0, dprefmin = -100.0, mpuscale = false, updip = 0.0, tpwtref = 0.05 }),
        ["qControl"] = new Dictionary<string, object>
        {
            ["windQcontrolModesType"] = 0, ["windLVRTQcontrolModesType"] = 1, ["iqh1"] = 1.05, ["iqmax"] = 1.05, ["iqmin"] = -1.05, ["iqpost"] = 0.0,
            ["kiq"] = 2.25, ["kiu"] = 10.0, ["kpq"] = 1.1, ["kpu"] = 2.0, ["kpufrt"] = 2.0, ["kqv"] = 2.0, ["qmax"] = 0.8, ["qmin"] = -0.8,
            ["rdroop"] = 0.0, ["xdroop"] = 0.0, ["tpost"] = 0.1, ["tqord"] = 0.05, ["tuss"] = 1.0, ["udb1"] = -0.1, ["udb2"] = 0.1,
            ["umax"] = 1.1, ["umin"] = 0.9, ["uqdip"] = 0.9, ["uqrise"] = 1.1, ["uref0"] = 0.0,
        },
        ["currentLimit"] = new { imax = 1.3, imaxdip = 1.3, mdfslim = false, mqpri = true, kpqu = 20.0, upqumax = 1.1,
            ipvdl = new[] { new[] { 0.0, 1.1 } }, iqvdl = new[] { new[] { 0.0, 1.1 } } },
        ["pll"] = new { tpll = 0.01, upll1 = 999.0, upll2 = 0.13 },
        ["measurement"] = new { tufilt = 0.01, tpfilt = 0.01, tqfilt = 0.01 },
    }.Concat(withMechanical ? new[] { new KeyValuePair<string, object>("mechanical", new { cdrt = 15.0, hgen = 1.0, hwtr = 5.0, kdrt = 500.0 }) }
        : Array.Empty<KeyValuePair<string, object>>()).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public void The_study_file_member_reads_into_the_same_data_and_refuses_what_the_type_would_ignore()
    {
        Iec61400StudyUnit u = Iec61400StudyFile.Unit(Json(StudyMember()), "iec61400[0]");
        Assert.Equal(new DynamicsMachineId(2, "1"), u.Machine);
        Iec61400Data expected = Unit(Iec61400TurbineType.Type4B, Iec61400QControlMode.Voltage);
        Assert.Equal(expected.Gen, u.Data.Gen);
        Assert.Equal(expected.PControl, u.Data.PControl);
        Assert.Equal(expected.QControl, u.Data.QControl);
        Assert.Equal(expected.Mechanical, u.Data.Mechanical);
        Assert.Equal(expected.Pll, u.Data.Pll);
        Assert.Equal(1.1, u.Data.CurrentLimit.Ipvdl.Evaluate(0.3));

        string Refused(object member) => Assert.Throws<InvalidDataException>(() => Iec61400StudyFile.Unit(Json(member), "iec61400[0]")).Message;
        Assert.Contains("unknown field 'tpaero'", Refused(StudyMember("4A", withMechanical: false,
            pControl: new { dpmax = 1.0, tpord = 0.05, dprefmax = 100.0, dprefmin = -100.0, mpuscale = false, updip = 0.0, tpaero = 0.1 })), StringComparison.Ordinal);
        Assert.Contains("needs mechanical", Refused(StudyMember("4B", withMechanical: false)), StringComparison.Ordinal);
        Assert.Contains("no drive train", Refused(StudyMember("4A", withMechanical: true)), StringComparison.Ordinal);
        var edition = new Dictionary<string, object>((Dictionary<string, object>)StudyMember()) { ["edition"] = 2015 };
        Assert.Contains("2015 structure is not", Refused(edition), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_runs_a_study_with_an_iec_unit_and_records_the_source_basis()
    {
        string dir = TestTemp.Under("gridsim-wp23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Psse("case14.raw"), Path.Combine(dir, "case14.raw"));
            File.WriteAllText(Path.Combine(dir, "case14.dyr"), DyrWithout(2));
            string Sha(string name) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name))));
            object Study(object member) => new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["raw"] = new { path = "case14.raw", sha256 = Sha("case14.raw") },
                ["dyr"] = new { path = "case14.dyr", sha256 = Sha("case14.dyr") },
                ["initialisation"] = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
                ["integration"] = new { stepSeconds = 0.005, durationSeconds = 1.0 },
                ["criteria"] = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
                ["angleReference"] = new { bus = 1, id = "1" },
                ["channels"] = new object[] { new { kind = "rotorAngle", machine = new { bus = 3, id = "1" } }, new { kind = "busVoltage", bus = 2 } },
                ["cases"] = new object[]
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
                ["iec61400"] = new[] { member },
            };
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, JsonSerializer.Serialize(Study(StudyMember())));
            var run = CliProcess.RunBuilt("dynamics-study", study);
            Assert.True(run.ExitCode is 0 or 3, run.StdErr + run.StdOut);   // 0 stable, 3 a verdict of instability: either is a run
            using JsonDocument doc = JsonDocument.Parse(run.StdOut);
            JsonElement root = doc.RootElement;
            JsonElement unit = root.GetProperty("coverage").GetProperty("units").EnumerateArray().Single(u => u.GetProperty("bus").GetInt32() == 2);
            Assert.Equal("BOUND", unit.GetProperty("status").GetString());
            JsonElement iec = Assert.Single(root.GetProperty("effectiveOptions").GetProperty("iec61400").EnumerateArray());
            Assert.Equal(Iec61400Data.SourceNote, iec.GetProperty("source").GetString());
            Assert.Contains("not checked against the IEC 61400-27-1 text", iec.GetProperty("source").GetString(), StringComparison.Ordinal);

            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, JsonSerializer.Serialize(Study(StudyMember("4B", withMechanical: false))));
            var refused = CliProcess.RunBuilt("dynamics-study", bad);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Contains("needs mechanical", refused.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
