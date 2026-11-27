using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP21: the user-chosen step schedule of the full RMS model (<see cref="RmsStepSchedule"/>,
/// <see cref="RmsTransientStability.StudyEventsScheduled"/>). The case is the WP2 single machine against a stiff source with
/// a bolted terminal fault from 0.1 s to 0.2 s (<see cref="Rc5Wp21BitIdentityTests.Smib"/>). Sources: the schedule's own
/// definition (sample times <c>from + j dt</c>), the kernel's stage rule, and the first-order convergence of the kernel's
/// splitting. Every multi-step claim is checked on every step.
/// </summary>
public sealed class Rc5Wp21StepScheduleTests
{
    private static RmsStepSchedule FineThenCoarse(double coarse) =>
        new(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.5, coarse) });

    /// <summary>A field controller that forwards to a real SEXS and records the step it is given on every call.</summary>
    private sealed class StepRecorder(RmsExciter inner) : IRmsFieldController
    {
        public List<double> Steps { get; } = new();
        public void Initialize(double fieldVoltagePu) { Steps.Clear(); inner.Initialize(fieldVoltagePu); }
        public double EquilibriumErrorPu(double fieldVoltagePu) => inner.EquilibriumErrorPu(fieldVoltagePu);
        public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
        {
            Steps.Add(dtSeconds);
            return inner.Step(voltageErrorPu, speedDeviationPu, dtSeconds);
        }
    }

    /// <summary>A TGOV1 that records the step of every call (each run creates fresh ones; all share the list, and only the
    /// governors a run steps add to it).</summary>
    private sealed record RecordingTgov1(List<double> Steps) : GovernorData
    {
        public override string Model => "TGOV1";

        public override IRmsGovernor Create() => new Recorder(new Tgov1Data(0.05, 0.5, 1.0, 5.0).Create(), Steps);

        private sealed class Recorder(IRmsGovernor inner, List<double> steps) : IRmsGovernor
        {
            public void Initialize(double mechanicalPowerPu) => inner.Initialize(mechanicalPowerPu);

            public double Step(double speedDeviationPu, double dtSeconds)
            {
                steps.Add(dtSeconds);
                return inner.Step(speedDeviationPu, dtSeconds);
            }
        }
    }

    [Fact]
    public void Sample_times_are_from_plus_j_dt_in_every_window()
    {
        // 0 to 0.5 at 1 ms (500 steps), then 0.5 to 1.0 at 4 ms (125 steps): 626 samples, built here independently.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(),
            FineThenCoarse(0.004), durationSeconds: 1.0);
        var want = new List<double>();
        for (int j = 0; j < 500; j++) want.Add(j * 0.001);
        for (int j = 0; j <= 125; j++) want.Add(0.5 + j * 0.004);
        Assert.Equal(want, r.Machines[0].TimeSeconds);
        Assert.Equal(want, r.Machines[1].TimeSeconds);
    }

    [Fact]
    public void Every_component_is_stepped_with_the_step_in_force_on_every_step()
    {
        // The exciter's field controller and the governor record the step of each call: one call per kernel step, with the
        // schedule's step for that step (from RmsStepSchedule.Expand), compared step by step.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        var govSteps = new List<double>();
        var governed = data.Select((d, i) => i == 0 ? d with { Governor = new RecordingTgov1(govSteps) } : d).ToList();
        // The study path runs the library SEXS (a user or plug-in controller is refused on a schedule with more than one step
        // size since 2.1.126), with the recording governor. The recording field controller is then run on the kernel
        // itself, which is where the step is handed out, with the same schedule and no events.
        var exciters = new List<RmsExciter?> { RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0), null };
        RmsStepSchedule s = FineThenCoarse(0.004);
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsScheduled(m, pf, governed, Rc5Wp21BitIdentityTests.SmibFault(), s,
            durationSeconds: 1.0, exciters: exciters);
        (double[] times, double[] steps) = s.Expand(1.0);
        Assert.Equal(steps, govSteps);
        Assert.Equal(times.Length, r.Machines[0].TimeSeconds.Count);
        Assert.Equal(625, steps.Length);

        var rec = new StepRecorder(RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0));
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, data);
        MultiMachineSubtransient.Result k = MultiMachineSubtransient.SimulateCore(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0,
            p.Eqpp0, p.Edpp0, p.Network!, 50.0, 0.001, 1.0, new List<RmsNetworkStage>(), 2.0 * Math.PI, exciters: null, voltageReferencePu: null,
            mechanicalPower: null, converters: p.Converters, synchronismMachines: p.Machines.Count,
            fieldControllers: new IRmsFieldController?[] { rec, null }, machineTrips: null, stepObserver: null, unitRelays: null, schedule: s);
        Assert.Equal(steps, rec.Steps);
        Assert.Equal(times.Length, k.Machines[0].TimeSeconds.Count);
    }

    [Fact]
    public void The_fine_window_is_the_fine_fixed_step_run_bit_for_bit()
    {
        // Up to the first coarse sample the scheduled run takes exactly the steps of the all-1 ms run (same times, same step,
        // same arithmetic), so samples 0 to 500 (t = 0 to 0.5 s) are the same bits.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        MultiMachineSubtransient.Result fine = RmsTransientStability.StudyEvents(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), stepSeconds: 0.001, durationSeconds: 1.0);
        MultiMachineSubtransient.Result s = RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), FineThenCoarse(0.004), durationSeconds: 1.0);
        for (int i = 0; i < 2; i++)
            for (int k = 0; k <= 500; k++)
            {
                Assert.Equal(fine.Machines[i].TimeSeconds[k], s.Machines[i].TimeSeconds[k]);
                Assert.Equal(fine.Machines[i].DeltaRad[k], s.Machines[i].DeltaRad[k]);
                Assert.Equal(fine.Machines[i].SpeedPu[k], s.Machines[i].SpeedPu[k]);
                Assert.Equal(fine.Machines[i].EqpPu[k], s.Machines[i].EqpPu[k]);
                Assert.Equal(fine.Machines[i].EdpPu[k], s.Machines[i].EdpPu[k]);
                Assert.Equal(fine.Machines[i].EqppPu[k], s.Machines[i].EqppPu[k]);
                Assert.Equal(fine.Machines[i].EddpPu[k], s.Machines[i].EddpPu[k]);
            }
    }

    // Observed on the first run (2.1.126), a record, not a derived value (eb / ea = 0.334; no ratio is asserted).
    private const double ObservedEa = 0.004157316027055602, ObservedEb = 0.00138895253386484;

    [Fact]
    public void Halving_the_coarse_step_moves_the_answer_toward_the_fine_run()
    {
        // Spec test 7, a monotone-convergence check (not an error estimate). 1 ms to 0.5 s, then 4 ms (A) or 2 ms (B), against
        // the all-1 ms run at 5 s. A and B share the fine window bit for bit (test above), so they differ only by the step of
        // the coarse window; the kernel's splitting is first order, so halving that step should roughly halve the
        // coarse window's share of the difference. The spec compared with an all-2 ms run instead; that run also changes the
        // step during the fault, which mixes the fault window's error into the comparison, so the windowed step is isolated
        // here. The observed numbers (first run, 2.1.126) are pinned below as a record, exactly: the run is deterministic.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        // 2.1.126: a 50 ms fault. The shared SmibFault (100 ms at the machine bus) loses synchronism (the rotor angle passes
        // 8.7 rad by 1 s and 400 rad by 5 s), so the 5 s comparison below would be between slipping runs; with clearing at
        // 0.15 s every run stays in synchronism (rotor angle 1.14 to 1.82 rad).
        RmsEvent[] ev = { new RmsBusFault(0.1, 1), new RmsFaultClear(0.15, 1) };
        static double Rel(MultiMachineSubtransient.Result r) => r.Machines[0].DeltaRad[^1] - r.Machines[1].DeltaRad[^1];
        MultiMachineSubtransient.Result reference = RmsTransientStability.StudyEvents(m, pf, data, ev, stepSeconds: 0.001, durationSeconds: 5.0);
        MultiMachineSubtransient.Result a = RmsTransientStability.StudyEventsScheduled(m, pf, data, ev, FineThenCoarse(0.004), durationSeconds: 5.0);
        MultiMachineSubtransient.Result b = RmsTransientStability.StudyEventsScheduled(m, pf, data, ev, FineThenCoarse(0.002), durationSeconds: 5.0);
        Assert.Equal(5.0, reference.Machines[0].TimeSeconds[^1]);
        Assert.Equal(5.0, a.Machines[0].TimeSeconds[^1]);
        Assert.Equal(5.0, b.Machines[0].TimeSeconds[^1]);
        Assert.True(reference.StayedInSynchronism && a.StayedInSynchronism && b.StayedInSynchronism);
        double ea = Math.Abs(Rel(a) - Rel(reference)), eb = Math.Abs(Rel(b) - Rel(reference));
        Assert.True(eb < ea, $"4 ms tail differs by {ea} rad, 2 ms tail by {eb} rad");
        Assert.True(ea == ObservedEa && eb == ObservedEb, $"observed ea {ea:R} rad, eb {eb:R} rad");
    }

    [Fact]
    public void Two_runs_give_the_same_bits()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        string a = Rc5Wp21BitIdentityTests.Digest(RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), FineThenCoarse(0.004), durationSeconds: 1.0));
        string b = Rc5Wp21BitIdentityTests.Digest(RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), FineThenCoarse(0.004), durationSeconds: 1.0));
        Assert.Equal(a, b);
    }

    [Fact]
    public void An_event_inside_a_coarse_window_is_refused()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.05, 0.004) });
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() =>
            RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), s, durationSeconds: 1.0));
        Assert.Contains("coarse window", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_at_window_starts_are_taken_and_land_on_their_times()
    {
        // 4 ms to 0.1 s, 1 ms over the fault, 4 ms after it: both events start a window, so both land exactly.
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.004), new RmsStepWindow(0.1, 0.001), new RmsStepWindow(0.2, 0.004) });
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsScheduled(m, pf, data, Rc5Wp21BitIdentityTests.SmibFault(), s, durationSeconds: 1.0);
        Assert.Contains(0.1, r.Machines[0].TimeSeconds);
        Assert.Contains(0.2, r.Machines[0].TimeSeconds);
        (double[] times, double[] steps) = s.Expand(1.0);
        Assert.Equal((25, 0.1), RmsStepSchedule.AppliedAt(times, steps, 0.1));
        Assert.Equal((125, 0.2), RmsStepSchedule.AppliedAt(times, steps, 0.2));
        // The fault is in force from sample 25 on: a bolted terminal fault leaves the machine no electrical power, so it
        // accelerates at Pm / 2H = 1 / 7 pu/s and its speed rises by about 1.4e-4 pu over the 1 ms step after it (the
        // undisturbed drift before it is at the power-flow tolerance, about 1e-12).
        Assert.True(r.Machines[0].SpeedPu[26] - r.Machines[0].SpeedPu[25] > 1e-4);
    }

    [Fact]
    public void A_window_boundary_off_the_step_grid_is_refused()
    {
        // 0.5 s is 166.67 steps of 3 ms.
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.003), new RmsStepWindow(0.5, 0.004) });
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => s.Expand(1.0));
        Assert.Contains("whole number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_schedules_are_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RmsStepSchedule(Array.Empty<RmsStepWindow>()));
        Assert.ThrowsAny<ArgumentException>(() => new RmsStepSchedule(new[] { new RmsStepWindow(0.1, 0.001) }));
        Assert.ThrowsAny<ArgumentException>(() => new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(0.0, 0.002) }));
        Assert.ThrowsAny<ArgumentException>(() => new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.0) }));
        Assert.ThrowsAny<ArgumentException>(() => new RmsStepSchedule(new[] { new RmsStepWindow(0.0, double.NaN) }));
        // A window that starts at the end of the run.
        var late = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.001), new RmsStepWindow(1.0, 0.002) });
        Assert.ThrowsAny<ArgumentException>(() => late.Expand(1.0));
    }

    [Fact]
    public void A_composite_load_with_the_fuller_parts_refuses_two_step_sizes_and_the_subset_runs()
    {
        // The fuller WECC parts count whole steps of one size in their timers; the WP3b subset takes the step on each call.
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Rc5Wp20BitIdentityTests.Case14();
        var ev = new RmsEvent[] { new RmsBusFault(0.1, 4), new RmsFaultClear(0.15, 4) };
        var s = new RmsStepSchedule(new[] { new RmsStepWindow(0.0, 0.005), new RmsStepWindow(0.5, 0.01) });
        var extended = new RmsLoadedMachineData(data, new DynamicLoadData[]
            { Rc5Wp20BitIdentityTests.Wp3bCmld(4, 47.8, -3.9) with { Extensions = new CompositeLoadExtensions() } });
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => RmsTransientStability.StudyEventsScheduled(m, pf, extended, ev, s, durationSeconds: 1.0));
        Assert.Contains("whole steps", ex.Message, StringComparison.Ordinal);
        var subset = new RmsLoadedMachineData(data, new DynamicLoadData[] { Rc5Wp20BitIdentityTests.Wp3bCmld(4, 47.8, -3.9) });
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEventsScheduled(m, pf, subset, ev, s, durationSeconds: 1.0);
        Assert.Equal(1.0, r.Machines[0].TimeSeconds[^1]);
        // The same fuller load on a schedule with one step size is allowed (its timers see one step).
        MultiMachineSubtransient.Result u = RmsTransientStability.StudyEventsScheduled(m, pf, extended, ev, RmsStepSchedule.Fixed(0.005), durationSeconds: 0.3);
        Assert.Equal(61, u.Machines[0].TimeSeconds.Count);
    }
}
