using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.MotorStart;
using GridSim.Core.Model;
using Xunit;
using static GridSim.Tests.MotorStartTestSupport;

namespace GridSim.Tests;

/// <summary>
/// WP29: motor starting methods (star-delta, autotransformer, reactor, soft starter, VFD) and group starting.
///
/// <para>The test motor is the spec's: Rs = 0.02, Xs = 0.1, Xm = 3.0, Rr = 0.03, Xr = 0.1 pu on 5 MVA, 11 kV, H = 0.6 s,
/// a fan load 0.5 w^2. It sits on the slack bus of the stiff fixture, whose voltage the Newton solve holds at exactly
/// 1.0 pu (stiffer than the spec's 1e-9 source impedance), so every closed form below holds at the motor's terminals.</para>
///
/// <para>Closed forms (textbook induction-motor relations: Chapman, Electric Machinery Fundamentals, ch. 6; IEEE Std
/// 3002.7-2018 is the standard reference but was not used): a star-connected winding sees V/sqrt(3), so its current is
/// a 1/sqrt(3) fraction of the delta current and the line current (equal to the winding current in star) is a third of
/// the direct-on-line line current; torque goes with the square of the winding voltage, a third. An ideal
/// autotransformer of tap k gives motor current k I_DOL, line current k^2 I_DOL and torque k^2 T_DOL. A series reactor
/// gives I = V / |Z_m + Z_r|. The references use System.Numerics.Complex through MotorStartTestSupport, which shares no
/// code with the study.</para>
///
/// <para>Tolerances: each compared quantity is a few tens of floating-point operations on well-conditioned values (every
/// real part positive, no cancellation), so the relative rounding difference between the study's real arithmetic and the
/// Complex reference is below about 100 u = 2.2e-14. The asserts use 1e-12 relative: a margin of about 45 over that
/// bound, and far below any modelling error (a wrong factor would show at 1e-2 or more).</para>
/// </summary>
public sealed class Rc5Wp29MotorStartMethodsTests
{
    private const double Rel = 1e-12;
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static readonly MotorStartTermination Term = new(0.98, 10.0, 0.5, 0.0);

    private static PlacedMotor SpecMotor(string name = "S") =>
        new(name, 1, 5, 11, 0.02, 0.1, 3.0, 0.03, 0.1, 0.6, new MotorLoadTorque(0, 0, 0.5));

    private static MotorStartCaseInput Stiff(params PlacedMotor[] motors) =>
        Load("stiff.case.json") with { Motors = motors.Length == 0 ? new[] { SpecMotor() } : motors };

    private static MotorStartRequest Req(MotorStartMethod method, MotorStarter? starter, int every = 1, double horizon = 10.0, string motor = "S") =>
        new(motor, new MotorStartInitialState(0, false), 0.1, method, 0.001, Term with { HorizonSeconds = horizon }, PreStart, every, null, starter);

    private static void Near(double expected, double actual, double rel = Rel) =>
        Assert.True(Math.Abs(actual - expected) <= rel * Math.Abs(expected), $"expected {expected:R}, got {actual:R}");

    // ------------------------------------------------------------------------------------------------------------
    // 1. Locked-rotor ratios on a stiff bus
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Direct_on_line_locked_rotor_current_matches_the_independent_circuit()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult dol = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.DirectOnLine, null, horizon: 0.2));
        Assert.Equal(1.0, dol.StartingState!.BusVoltagePu, 12);
        Assert.Equal(1.0, dol.StartingState.Slip);
        Near(Current(m, 1.0, 1.0), dol.StartingState.CurrentPu);
        Near(Torque(m, 1.0, 1.0), dol.StartingState.ElectricalTorquePu);
        Assert.Null(dol.StarterSamples);
        Assert.Null(dol.StarterTransitions);
    }

    [Fact]
    public void Star_delta_locked_rotor_line_current_and_torque_are_a_third_of_direct_on_line()
    {
        MotorStartSample dol = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.DirectOnLine, null, horizon: 0.2)).StartingState!;
        MotorStartResult star = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta), horizon: 0.2));
        Near(1.0 / 3.0, star.StartingState!.CurrentPu / dol.CurrentPu);
        Near(1.0 / 3.0, star.StartingState.ElectricalTorquePu / dol.ElectricalTorquePu);
        Near(1.0 / 3.0, star.StartingState.PMw / dol.PMw);
    }

    [Fact]
    public void Star_delta_winding_sees_the_supply_over_root_three_and_carries_that_fraction_of_the_delta_current()
    {
        MotorStartSample dol = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.DirectOnLine, null, horizon: 0.2)).StartingState!;
        MotorStartResult star = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta), horizon: 0.2));
        MotorStarterSample first = star.StarterSamples![1];   // [0] is the instant before connection
        Assert.Equal("star", first.Stage);
        Near(1.0 / Math.Sqrt(3.0), first.VoltageFactor);
        Near(1.0 / Math.Sqrt(3.0), first.MotorTerminalVoltagePu);
        Near(1.0 / Math.Sqrt(3.0), first.MotorCurrentPu / dol.CurrentPu);
        Near(1.0 / 3.0, first.LineCurrentPu / dol.CurrentPu);
    }

    [Fact]
    public void Autotransformer_tap_0_65_gives_line_current_and_torque_k_squared_and_motor_current_k()
    {
        MotorStartSample dol = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.DirectOnLine, null, horizon: 0.2)).StartingState!;
        MotorStartResult at = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Autotransformer, new MotorStarter(MotorStartMethod.Autotransformer, TapRatio: 0.65), horizon: 0.2));
        Near(0.4225, at.StartingState!.CurrentPu / dol.CurrentPu);
        Near(0.4225, at.StartingState.ElectricalTorquePu / dol.ElectricalTorquePu);
        Near(0.65, at.StarterSamples![1].MotorCurrentPu / dol.CurrentPu);
        Near(0.65, at.StartingState.TerminalVoltagePu);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 2. Reactor
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reactor_j0_1_current_is_the_supply_over_the_series_sum()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Reactor, new MotorStarter(MotorStartMethod.Reactor, ReactorRPu: 0, ReactorXPu: 0.1), horizon: 0.2));
        Complex zt = Circuit(m, 1.0).Zin + new Complex(0, 0.1);
        Near(1.0 / zt.Magnitude, r.StartingState!.CurrentPu);
        Near(1.0 / zt.Magnitude, r.StarterSamples![1].MotorCurrentPu);
    }

    [Fact]
    public void Reactor_motor_voltage_and_drawn_power_follow_the_divider()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Reactor, new MotorStarter(MotorStartMethod.Reactor, ReactorRPu: 0, ReactorXPu: 0.1), horizon: 0.2));
        Complex zm = Circuit(m, 1.0).Zin, zt = zm + new Complex(0, 0.1);
        Near((zm / zt).Magnitude, r.StartingState!.TerminalVoltagePu);
        Complex s = 1.0 / Complex.Conjugate(zt) * 5.0;   // MVA drawn at 1 pu, motor plus reactor
        Near(s.Real, r.StartingState.PMw);
        Near(s.Imaginary, r.StartingState.QMvar);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 3. Star-delta transition
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Star_delta_switches_at_the_first_step_at_or_above_0_8()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta)));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        MotorStarterTransition tr = Assert.Single(r.StarterTransitions!);
        Assert.Equal(("star", "delta"), (tr.FromStage, tr.ToStage));
        // Every step is reported (sampleEvery 1): the first evaluated sample at or above 0.8 is the switch step.
        int i = r.Samples.Select((s, k) => (s, k)).Skip(1).First(x => x.s.SpeedPu >= 0.8).k;
        Assert.True(r.Samples[i - 1].SpeedPu < 0.8);
        Assert.Equal(r.Samples[i].TimeSeconds, tr.TimeSeconds);
        Assert.Equal(r.Samples[i].SpeedPu, tr.SpeedPu);
        Assert.Equal("star", r.StarterSamples![i - 1].Stage);
        Assert.Equal("delta", r.StarterSamples[i].Stage);
    }

    [Fact]
    public void Star_delta_switch_triples_the_line_current_at_the_same_slip_and_lands_on_the_direct_on_line_value()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta), every: 100));
        MotorStarterTransition tr = Assert.Single(r.StarterTransitions!);
        Near(3.0, tr.LineCurrentAfterPu / tr.LineCurrentBeforePu);
        Near(Current(m, 1.0, 1.0 - tr.SpeedPu), tr.LineCurrentAfterPu);
    }

    [Fact]
    public void A_star_delta_switch_below_the_breakdown_speed_is_noted_as_a_transition_current_peak()
    {
        // Independent breakdown speed: ternary search of the reference torque over slip (unimodal on (0, 1]).
        PlacedMotor m = SpecMotor();
        double lo = 1e-6, hi = 1.0;
        for (int k = 0; k < 300; k++)
        {
            double a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
            if (Torque(m, 1.0, a) < Torque(m, 1.0, b)) lo = a; else hi = b;
        }
        double breakdown = 1.0 - (lo + hi) / 2;
        Assert.True(breakdown > 0.81, $"breakdown speed {breakdown}");   // so a switch at about 0.80 is below it
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta), every: 100));
        Assert.StartsWith("transition current peak", r.StarterTransitions![0].Note, StringComparison.Ordinal);
        Assert.Contains(r.Reasons, x => x.StartsWith("transition current peak", StringComparison.Ordinal));
        // Switching above it carries no note. 0.90 is above the breakdown speed (0.8483, asserted above by the same search)
        // and reachable in star: star torque there is 0.667 pu against the fan's 0.405 pu (an independent Python Heun
        // integration switches at 3.84 s at 0.90012 pu and runs at 3.98 s). At 0.95 the motor never switches: the star
        // torque, 0.4443 pu, is below the load, 0.4513 pu.
        Assert.True(breakdown < 0.90, $"breakdown speed {breakdown}");
        Assert.True(Torque(m, 1.0, 0.10) / 3.0 > m.LoadTorque.At(0.90));
        MotorStartResult late = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, SwitchSpeedPu: 0.90), every: 100));
        Assert.Equal(MotorStartOutcome.Running, late.Outcome);
        MotorStarterTransition lateSwitch = Assert.Single(late.StarterTransitions!);
        Assert.True(lateSwitch.SpeedPu >= 0.90);
        Assert.Null(lateSwitch.Note);
    }

    [Fact]
    public void With_a_dead_time_the_peak_note_is_on_the_reconnection_in_delta()
    {
        // The open transition at about 0.80 pu coasts for 50 steps (to about 0.787 pu, Python), still below the breakdown
        // speed 0.848, so the current peak is at off -> delta, and the note goes there, not on star -> off (line current 0).
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, DeadTimeSeconds: 0.05), every: 100));
        Assert.Equal(2, r.StarterTransitions!.Count);
        Assert.Null(r.StarterTransitions[0].Note);
        Assert.StartsWith("transition current peak: reconnected in delta", r.StarterTransitions[1].Note, StringComparison.Ordinal);
        Assert.Contains(r.Reasons, x => x.StartsWith("transition current peak: reconnected in delta", StringComparison.Ordinal));
    }

    [Fact]
    public void An_open_transition_dead_time_disconnects_the_motor_for_whole_steps_and_it_coasts()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, DeadTimeSeconds: 0.05)));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        Assert.Equal(2, r.StarterTransitions!.Count);
        MotorStarterTransition open = r.StarterTransitions[0], close = r.StarterTransitions[1];
        Assert.Equal(("star", "off"), (open.FromStage, open.ToStage));
        Assert.Equal(("off", "delta"), (close.FromStage, close.ToStage));
        Assert.Equal(0.0, open.LineCurrentAfterPu);
        Assert.Equal(0.0, close.LineCurrentBeforePu);
        // 0.05 s at 1 ms is 50 whole steps.
        int iOpen = r.Samples.ToList().FindIndex(s => s.TimeSeconds == open.TimeSeconds);
        int iClose = r.Samples.ToList().FindIndex(s => s.TimeSeconds == close.TimeSeconds);
        Assert.Equal(50, iClose - iOpen);
        for (int k = iOpen; k < iClose; k++)
        {
            Assert.Equal("off", r.StarterSamples![k].Stage);
            Assert.Equal(0.0, r.Samples[k].CurrentPu);
            Assert.Equal(0.0, r.Samples[k].ElectricalTorquePu);
        }
        Assert.True(close.SpeedPu < open.SpeedPu, "the fan load slows the disconnected rotor");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 4. Soft starter
    // ------------------------------------------------------------------------------------------------------------

    private static MotorStarter Soft(double? limit = null) => new(MotorStartMethod.SoftStarter, InitialVoltageFraction: 0.4, RampSeconds: 5, CurrentLimitPu: limit);

    [Fact]
    public void Soft_starter_voltage_is_0_7_half_way_through_a_5_s_ramp_from_0_4()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft()));
        // t = 0.1 s start + 2.5 s = step 2600; the fraction is 0.4 + 0.6 x 2500/5000, linear. 0.4 + 0.3 rounds to the
        // double nearest 0.7 or its neighbour (the exact sum is a tie at 2^-53 spacing), so one ulp of 0.7 (1.11e-16).
        int i = r.Samples.ToList().FindIndex(s => Math.Abs(s.TimeSeconds - 2.6) < 1e-9 && s.CurrentPu > 0);
        MotorStarterSample ss = r.StarterSamples![i];
        Assert.Equal("softStart", ss.Stage);
        Assert.True(Math.Abs(ss.VoltageFactor - 0.7) <= 1.2e-16, ss.VoltageFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(ss.MotorTerminalVoltagePu - 0.7) <= 1.2e-16);
    }

    [Fact]
    public void Soft_starter_ramp_is_linear_at_every_reported_step()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(), every: 250));
        int checkedSteps = 0;
        for (int i = 1; i < r.Samples.Count; i++)
        {
            if (r.StarterSamples![i].Stage != "softStart") continue;
            double n = Math.Round((r.Samples[i].TimeSeconds - 0.1) / 0.001);
            // The study forms 0.4 + (1 - 0.4) (n / 5000), the test 0.4 + 0.6 n / 5000: at most three roundings apart on
            // values up to 1, so within 3 x 1.1e-16; 1e-15 allows that.
            Assert.True(Math.Abs(r.StarterSamples[i].VoltageFactor - (0.4 + 0.6 * n / 5000)) <= 1e-15, $"step {n}");
            checkedSteps++;
        }
        Assert.Equal(20, checkedSteps);   // steps 0, 250, ..., 4750 of the 5000-step ramp
    }

    [Fact]
    public void Soft_starter_line_current_is_the_motor_current_and_the_power_is_lossless()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(), every: 500));
        int i = r.Samples.ToList().FindIndex(s => Math.Abs(s.TimeSeconds - 2.6) < 1e-9 && s.CurrentPu > 0);
        MotorStartSample s = r.Samples[i];
        double a = r.StarterSamples![i].VoltageFactor;
        double iRef = a * Current(m, 1.0, s.Slip);
        Near(iRef, s.CurrentPu);
        Near(iRef, r.StarterSamples[i].MotorCurrentPu);
        double pRef = iRef * iRef * Circuit(m, s.Slip).Zin.Real;   // pu: the motor's input power
        Near(pRef * 5.0, s.PMw);
        Near(Math.Sqrt(iRef * iRef - pRef * pRef) * 5.0, s.QMvar, 1e-11);   // |S| = V I at V = 1; sqrt of a difference: one more digit of margin
    }

    [Fact]
    public void Soft_starter_current_limit_holds_at_every_step_and_is_active()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(limit: 3.0)));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        // The limit is met to the controller's declared settle tolerance (1e-9 relative); on a stiff bus the fixed point
        // is reached in one pass, so any excess is rounding only.
        Assert.All(r.Samples, s => Assert.True(s.CurrentPu <= 3.0 * (1 + 1e-9), s.CurrentPu.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        // Where the applied fraction is below the ramp's (beyond the 1e-15 rounding of the ramp), the limit acted and the
        // current sits on it; elsewhere the fraction is the ramp's.
        int limited = 0;
        for (int i = 1; i < r.Samples.Count; i++)
        {
            if (r.StarterSamples![i].Stage != "softStart") continue;
            double n = Math.Round((r.Samples[i].TimeSeconds - 0.1) / 0.001);
            double ramp = 0.4 + 0.6 * n / 5000;
            if (r.StarterSamples[i].VoltageFactor < ramp - 1e-15)
            {
                limited++;
                Assert.True(r.Samples[i].CurrentPu >= 3.0 * (1 - 1e-9), $"step {n}: limited below the limit");
            }
            else Assert.True(Math.Abs(r.StarterSamples[i].VoltageFactor - ramp) <= 1e-15, $"step {n}");
        }
        Assert.True(limited > 0, "the limit never acted");
    }

    [Fact]
    public void Soft_starter_bypasses_at_the_end_of_the_ramp_in_whole_steps()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(), every: 1000));
        MotorStarterTransition tr = Assert.Single(r.StarterTransitions!);
        Assert.Equal(("softStart", "bypassed"), (tr.FromStage, tr.ToStage));
        Assert.Equal(5100 * 0.001, tr.TimeSeconds);   // step 5100 = start step 100 + ramp 5000
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        Assert.True(r.DecisionTimeSeconds >= tr.TimeSeconds, "RUNNING needs the final stage");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 5. VFD
    // ------------------------------------------------------------------------------------------------------------

    private static MotorStarter Drive(double? limit = null) =>
        new(MotorStartMethod.Vfd, InitialFrequencyHz: 10, RatedFrequencyHz: 50, RampSeconds: 4, CurrentLimitPu: limit);

    private static (MotorStartSample S, MotorStarterSample D) At25Hz(MotorStartResult r)
    {
        // f = 10 + 40 n / 4000 is 25 Hz at n = 1500 exactly (0.375 is a dyadic fraction), t = 0.1 + 1.5 s.
        int i = r.StarterSamples!.ToList().FindIndex(x => x.FrequencyHz == 25.0);
        Assert.True(i > 0);
        return (r.Samples[i], r.StarterSamples![i]);
    }

    [Fact]
    public void Vfd_at_25_hz_gives_half_voltage()
    {
        (MotorStartSample s, MotorStarterSample d) = At25Hz(MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Vfd, Drive())));
        Assert.Equal(1.6, s.TimeSeconds, 12);
        Assert.Equal(0.5, d.VoltageFactor);
        Assert.Equal(0.5, d.MotorTerminalVoltagePu);
        Assert.Equal(0.5, s.TerminalVoltagePu);
    }

    [Fact]
    public void Vfd_at_25_hz_evaluates_the_circuit_with_halved_reactances_at_the_supply_slip()
    {
        PlacedMotor m = SpecMotor();
        (MotorStartSample s, MotorStarterSample d) = At25Hz(MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Vfd, Drive())));
        // The slip as the rotor's electrical frequency over the supply's, in hertz: f_rotor = 25 - 50 w, slip = f_rotor / 25.
        // (A frequency statement, not the code's (phi - w) / phi; the two agree to a few ulps.)
        double slip = (25.0 - 50.0 * s.SpeedPu) / 25.0;
        Assert.True(Math.Abs(s.Slip - slip) <= 4e-16);
        PlacedMotor half = m with { XaPu = 0.05, XmPu = 1.5, X1Pu = 0.05 };
        (Complex z, Complex zp) = Circuit(half, slip);
        double i = 0.5 / z.Magnitude;
        Near(i, d.MotorCurrentPu);
        Near(i * i * zp.Real / 0.5, s.ElectricalTorquePu);   // air-gap power over the supply speed 0.5
        Near(i * i * z.Real * 5.0, d.MotorInputMw);
    }

    [Fact]
    public void Vfd_line_side_draws_the_independent_motor_power_over_efficiency()
    {
        // The model's line side is unity power factor by definition (stated in the manual), so Q is not asserted here: a
        // check of the code's own constant would prove nothing. What is independent: the motor's input power from the
        // reference circuit at 25 Hz (reactances halved, half voltage, slip against the supply), over the efficiency, is
        // the line power, and on the stiff 1.0 pu bus the line current is that power in pu of the motor rating.
        PlacedMotor m = SpecMotor();
        (MotorStartSample s, _) = At25Hz(MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Vfd, Drive())));
        double slip = (25.0 - 50.0 * s.SpeedPu) / 25.0;
        (Complex z, _) = Circuit(m with { XaPu = 0.05, XmPu = 1.5, X1Pu = 0.05 }, slip);
        double i = 0.5 / z.Magnitude;
        double pLinePu = i * i * z.Real / 0.97;
        Near(pLinePu * 5.0, s.PMw);
        Near(pLinePu / 1.0, s.CurrentPu);
    }

    [Fact]
    public void Vfd_line_power_follows_the_independent_circuit_at_every_reported_drive_step()
    {
        PlacedMotor m = SpecMotor();
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Vfd, Drive(), every: 100));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        int checkedSteps = 0;
        for (int k = 1; k < r.Samples.Count; k++)
        {
            MotorStarterSample d = r.StarterSamples![k];
            if (d.Stage != "drive" || d.FrequencyHz is not double f) continue;
            double phi = f / 50.0;
            double slip = (f - 50.0 * r.Samples[k].SpeedPu) / f;
            (Complex z, _) = Circuit(m with { XaPu = 0.1 * phi, XmPu = 3.0 * phi, X1Pu = 0.1 * phi }, slip);
            double i = phi / z.Magnitude;
            // Near synchronous speed the slip is a difference of numbers near phi: an absolute rounding of about 1e-16 on
            // a slip down to about 1e-4 is 1e-12 relative, and the power goes roughly with the slip there; 1e-11 covers it.
            Near(i * i * z.Real / 0.97 * 5.0, r.Samples[k].PMw, 1e-11);
            checkedSteps++;
        }
        Assert.True(checkedSteps > 10, $"{checkedSteps} drive steps checked");
    }

    [Fact]
    public void Vfd_ramp_pauses_while_the_motor_current_is_above_the_limit()
    {
        MotorStartResult r = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.Vfd, Drive(limit: 1.5)));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        Assert.Contains(r.Reasons, x => x.StartsWith("the drive's frequency ramp paused for", StringComparison.Ordinal));
        int paused = 0;
        for (int i = 2; i < r.StarterSamples!.Count; i++)
        {
            MotorStarterSample prev = r.StarterSamples[i - 1], cur = r.StarterSamples[i];
            if (prev.FrequencyHz == 50.0) break;
            if (prev.MotorCurrentPu > 1.5) { Assert.Equal(prev.FrequencyHz, cur.FrequencyHz); paused++; }
            else Assert.True(cur.FrequencyHz > prev.FrequencyHz);
        }
        Assert.True(paused > 0);
        Assert.Equal(50.0, r.StarterSamples[^1].FrequencyHz);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 6. Group starting
    // ------------------------------------------------------------------------------------------------------------

    private static MotorGroupMember Dol(string motor, double at) => new(motor, new MotorStartInitialState(0, false), at, MotorStartMethod.DirectOnLine);

    [Fact]
    public void On_a_stiff_bus_the_second_motor_is_the_first_shifted_by_one_second()
    {
        MotorStartCaseInput input = Stiff(SpecMotor("S"), SpecMotor("S2"));
        MotorGroupStartResult g = MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(new[] { Dol("S", 0.1), Dol("S2", 1.1) }, 0.001, Term, PreStart));
        Assert.Equal(MotorStartOutcome.Running, g.Outcome);
        MotorGroupMemberResult a = g.Motors[0], b = g.Motors[1];
        Assert.Equal(a.Samples.Count, b.Samples.Count);
        // The slack holds 1.0 pu, so each motor's trajectory depends only on its own state; times differ by the
        // rounding of k dt (a few 1e-16).
        for (int k = 0; k < a.Samples.Count; k++)
        {
            Assert.True(Math.Abs(b.Samples[k].TimeSeconds - a.Samples[k].TimeSeconds - 1.0) <= 1e-12, $"time at {k}");
            Assert.True(Math.Abs(b.Samples[k].SpeedPu - a.Samples[k].SpeedPu) <= 1e-12, $"speed at {k}");
            Assert.True(Math.Abs(b.Samples[k].CurrentPu - a.Samples[k].CurrentPu) <= 1e-12, $"current at {k}");
            Assert.True(Math.Abs(b.Samples[k].ElectricalTorquePu - a.Samples[k].ElectricalTorquePu) <= 1e-12, $"torque at {k}");
        }
        Assert.True(Math.Abs(b.AccelerationTimeSeconds!.Value - a.AccelerationTimeSeconds!.Value) <= 1e-12);
    }

    [Fact]
    public void On_a_weak_feeder_the_group_dip_is_deeper_the_more_the_starts_overlap()
    {
        MotorStartCaseInput weak = Load("weak.case.json");
        PlacedMotor m1 = weak.Motors.Single(x => x.Name == "M1");
        MotorStartCaseInput input = weak with { Motors = new[] { m1, m1 with { Name = "M1b" } } };
        var term = new MotorStartTermination(0.98, 20.0, 0.5, 0.0);
        var dips = new List<double>();
        foreach (double offset in new[] { 0.0, 0.5, 1.0, 2.0, 4.0, 8.0 })
        {
            MotorGroupStartResult g = MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(new[] { Dol("M1", 0.1), Dol("M1b", 0.1 + offset) }, 0.001, term, PreStart, 1000));
            Assert.Equal(MotorStartOutcome.Running, g.Outcome);
            dips.Add(g.VoltageDip!.WorstDipPercent);
        }
        for (int k = 1; k < dips.Count; k++) Assert.True(dips[k] < dips[k - 1], string.Join(", ", dips));
        double single = MotorStartRunner.Run(weak, Request(sampleEvery: 1000)).VoltageDip!.WorstDipPercent;
        Assert.True(dips[0] > single, $"{dips[0]} vs single {single}");
    }

    [Fact]
    public void A_one_motor_direct_on_line_group_reproduces_the_single_motor_study_bit_for_bit()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        MotorStartResult single = MotorStartRunner.Run(input, Request(sampleEvery: 10));
        MotorGroupStartResult g = MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(new[] { Dol("M1", 0.1) }, 0.001, Request().Termination, PreStart, 10));
        MotorGroupMemberResult m = Assert.Single(g.Motors);
        Assert.Equal(BitConverter.DoubleToInt64Bits(single.AccelerationTimeSeconds!.Value), BitConverter.DoubleToInt64Bits(m.AccelerationTimeSeconds!.Value));
        Assert.Equal(single.Samples, m.Samples);
        Assert.Equal(single.NetworkSolves, g.NetworkSolves);
        Assert.Equal(single.VoltageDip, m.VoltageDip);
        Assert.Equal(single.PeakCurrentState, m.PeakCurrentState);
    }

    [Fact]
    public void Group_members_keep_their_own_methods()
    {
        MotorStartCaseInput input = Stiff(SpecMotor("S"), SpecMotor("S2"));
        var members = new[]
        {
            Dol("S", 0.1),
            new MotorGroupMember("S2", new MotorStartInitialState(0, false), 0.1, MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta)),
        };
        MotorGroupStartResult g = MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(members, 0.001, Term, PreStart, 100));
        Near(1.0 / 3.0, g.Motors[1].StartingState!.CurrentPu / g.Motors[0].StartingState!.CurrentPu);
        Assert.Empty(g.Motors[0].Transitions);
        Assert.Single(g.Motors[1].Transitions);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 7. Direct-on-line output unchanged
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pins captured before any WP29 edit: a scratch console program referencing the 2.1.135 Release GridSim.Core.dll
    /// (sha256 6bbe410ddfb7a3148676cc85e5c30787ebe02aec323b59752be50ca74d07da8a, built from 8bcf3ba2) ran these four
    /// requests on the existing fixtures and hashed the System.Text.Json serialisation below; the same program against
    /// the WP29 build gave the same four hashes. The serialised JSON carries no machine path.
    /// </summary>
    [Theory]
    [InlineData("prestart.case.json", "M1", 10, null, "a63da9d3f77fcaa26de174d473c03a5e805f4d4986b3b8c1826e7dc4155739bc")]
    [InlineData("stiff.case.json", "M1", 100, null, "fd995d8f779eeee6f836e2d6004cfac8b7eb748b9fc8f76957d3e7a61897f8a1")]
    [InlineData("weak.case.json", "M1", 100, 20.0, "389912404ce7ae21677fb852213a117cebde545186965768a84da2996a2c80e6")]
    [InlineData("weak.case.json", "M2", 1, null, "ea18ddfca30f0b808b7ffea7a72b7623a82868b7fd579047e48735da80050eec")]
    public void Direct_on_line_results_are_byte_identical_to_2_1_135(string manifest, string motor, int every, double? dip, string sha)
    {
        var request = new MotorStartRequest(motor, new MotorStartInitialState(0.0, false), 0.1, MotorStartMethod.DirectOnLine, 0.001,
            new MotorStartTermination(0.98, 10.0, 0.5, 0.0), new MotorStartPreStartTolerance(1e-3, 1e-6, 2.0), every, dip);
        JsonSerializerOptions options = JsonOptions;
        string json = JsonSerializer.Serialize(MotorStartRunner.Run(Load(manifest), request), options);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Refusals
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Invalid_starters_are_refused_by_name_before_any_solve()
    {
        MotorStartCaseInput input = Stiff();
        void Refused(MotorStartMethod method, MotorStarter? starter, string text)
        {
            var ex = Assert.Throws<InvalidDataException>(() => MotorStartRunner.Run(input, Req(method, starter)));
            Assert.Contains(text, ex.Message, StringComparison.Ordinal);
        }
        Refused(MotorStartMethod.DirectOnLine, new MotorStarter(MotorStartMethod.DirectOnLine), "directOnLine takes none");
        Refused(MotorStartMethod.StarDelta, null, "method starDelta needs a starter object");
        Refused(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.Autotransformer, TapRatio: 0.5), "the starter is for autotransformer");
        Refused(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, TapRatio: 0.5), "start.starter.tapRatio: not a field of a starDelta starter");
        Refused(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, SwitchSpeedPu: 0.98), "switchSpeedPu must lie above 0 and below");
        Refused(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, DeadTimeSeconds: 0.0505), "whole number of integration steps");
        Refused(MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta, SwitchTimeSeconds: double.NaN), "must be a finite number");
        Refused(MotorStartMethod.Autotransformer, new MotorStarter(MotorStartMethod.Autotransformer, TapRatio: 1.0), "tapRatio is required and must lie strictly between 0 and 1");
        Refused(MotorStartMethod.Reactor, new MotorStarter(MotorStartMethod.Reactor, ReactorRPu: 0, ReactorXPu: 0), "not both zero");
        Refused(MotorStartMethod.Reactor, new MotorStarter(MotorStartMethod.Reactor, ReactorXPu: 0.1), "needs reactorRPu and reactorXPu");
        Refused(MotorStartMethod.SoftStarter, new MotorStarter(MotorStartMethod.SoftStarter, InitialVoltageFraction: 0, RampSeconds: 5), "initialVoltageFraction is required");
        Refused(MotorStartMethod.SoftStarter, new MotorStarter(MotorStartMethod.SoftStarter, InitialVoltageFraction: 0.4), "rampSeconds is required");
        Refused(MotorStartMethod.Vfd, new MotorStarter(MotorStartMethod.Vfd, InitialFrequencyHz: 50, RatedFrequencyHz: 50, RampSeconds: 4), "initialFrequencyHz must be above 0 and below ratedFrequencyHz");
        Refused(MotorStartMethod.Vfd, new MotorStarter(MotorStartMethod.Vfd, InitialFrequencyHz: 10, RatedFrequencyHz: 50, RampSeconds: 4, DriveEfficiency: 1.2), "driveEfficiency");
    }

    [Fact]
    public void Invalid_groups_are_refused()
    {
        MotorStartCaseInput input = Stiff(SpecMotor("S"), SpecMotor("S2"));
        var empty = Assert.Throws<InvalidDataException>(() => MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(Array.Empty<MotorGroupMember>(), 0.001, Term, PreStart)));
        Assert.Contains("name at least one motor", empty.Message, StringComparison.Ordinal);
        var twice = Assert.Throws<InvalidDataException>(() => MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(new[] { Dol("S", 0.1), Dol("S", 0.2) }, 0.001, Term, PreStart)));
        Assert.Contains("listed more than once", twice.Message, StringComparison.Ordinal);
        var unknown = Assert.Throws<InvalidDataException>(() => MotorStartRunner.RunGroup(input, new MotorGroupStartRequest(new[] { Dol("S", 0.1), Dol("S9", 0.2) }, 0.001, Term, PreStart)));
        Assert.Contains("no placed motor named 'S9'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_at_or_above_the_switch_speed_is_refused_by_name()
    {
        MotorStartCaseInput input = Stiff();
        static MotorStartRequest Fast(MotorStartMethod how, MotorStarter kit, double speed) =>
            new("S", new MotorStartInitialState(speed, false), 0.1, how, 0.001, Term, PreStart, 1, null, kit);
        foreach ((MotorStartMethod method, MotorStarter starter) in new (MotorStartMethod, MotorStarter)[]
        {
            (MotorStartMethod.StarDelta, new MotorStarter(MotorStartMethod.StarDelta)),
            (MotorStartMethod.Autotransformer, new MotorStarter(MotorStartMethod.Autotransformer, TapRatio: 0.65)),
            (MotorStartMethod.Reactor, new MotorStarter(MotorStartMethod.Reactor, ReactorRPu: 0, ReactorXPu: 0.1)),
        })
        {
            // Exactly at the default switch speed 0.8, and above it.
            foreach (double speed in new[] { 0.8, 0.85 })
            {
                var ex = Assert.Throws<InvalidDataException>(() => MotorStartRunner.Run(input, Fast(method, starter, speed)));
                Assert.Contains("is at or above start.starter.switchSpeedPu 0.8", ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_vfd_start_above_the_initial_frequency_ratio_is_refused_as_a_flying_restart()
    {
        // f0/fn = 10/50 = 0.2: at 0.25 pu the slip against 10 Hz would be negative. At exactly 0.2 the slip is 0 (no
        // current), not negative, so it is accepted by the validation.
        MotorStartCaseInput input = Stiff();
        var above = new MotorStartRequest("S", new MotorStartInitialState(0.25, false), 0.1, MotorStartMethod.Vfd, 0.001, Term, PreStart, 1, null, Drive());
        var ex = Assert.Throws<InvalidDataException>(() => MotorStartRunner.Run(input, above));
        Assert.Contains("flying restart", ex.Message, StringComparison.Ordinal);
        Assert.Contains("initial.speedPu 0.25", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vfd_on_a_bus_whose_demand_varies_with_voltage_is_refused()
    {
        MotorStartCaseInput stiff = Stiff();
        // A ZIP load with a constant-impedance part on the motor's bus (bus 1).
        var buses = stiff.Model.Buses.Select(b => b.Id == 1 ? b with { Zip = new ZipLoad(Ap: 1, Cp: 0) } : b).ToList();
        MotorStartCaseInput input = stiff with { Model = stiff.Model.CopyWith(buses: buses) };
        var ex = Assert.Throws<InvalidDataException>(() => MotorStartRunner.Run(input, Req(MotorStartMethod.Vfd, Drive())));
        Assert.Contains("motor 'S' is on bus 1, whose demand varies with voltage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_motor_starter_group_reproduces_the_single_starter_study_bit_for_bit()
    {
        // The same engine runs both, so every sample, starter sample and transition must be equal (records compare their
        // doubles bit for bit through Equals), as must the acceleration time and the solve count.
        MotorStarter starter = new(MotorStartMethod.StarDelta, DeadTimeSeconds: 0.05);
        MotorStartResult single = MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.StarDelta, starter, every: 10));
        MotorGroupStartResult g = MotorStartRunner.RunGroup(Stiff(), new MotorGroupStartRequest(
            new[] { new MotorGroupMember("S", new MotorStartInitialState(0, false), 0.1, MotorStartMethod.StarDelta, starter) }, 0.001, Term, PreStart, 10));
        MotorGroupMemberResult m = Assert.Single(g.Motors);
        Assert.Equal(single.Outcome, m.Outcome);
        Assert.Equal(BitConverter.DoubleToInt64Bits(single.AccelerationTimeSeconds!.Value), BitConverter.DoubleToInt64Bits(m.AccelerationTimeSeconds!.Value));
        Assert.Equal(single.Samples, m.Samples);
        Assert.Equal(single.StarterSamples, m.StarterSamples);
        Assert.Equal(single.StarterTransitions, m.Transitions);
        Assert.Equal(single.NetworkSolves, g.NetworkSolves);
        Assert.Equal(single.PeakCurrentState, m.PeakCurrentState);
    }

    [Fact]
    public void Repeat_starter_and_group_runs_are_bit_identical()
    {
        JsonSerializerOptions options = JsonOptions;
        string a = JsonSerializer.Serialize(MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(limit: 3.0), every: 50)), options);
        string b = JsonSerializer.Serialize(MotorStartRunner.Run(Stiff(), Req(MotorStartMethod.SoftStarter, Soft(limit: 3.0), every: 50)), options);
        Assert.Equal(a, b);
        MotorStartCaseInput input = Stiff(SpecMotor("S"), SpecMotor("S2"));
        var request = new MotorGroupStartRequest(new[] { Dol("S", 0.1), Dol("S2", 0.5) }, 0.001, Term, PreStart, 50);
        Assert.Equal(JsonSerializer.Serialize(MotorStartRunner.RunGroup(input, request), options), JsonSerializer.Serialize(MotorStartRunner.RunGroup(input, request), options));
    }
}
