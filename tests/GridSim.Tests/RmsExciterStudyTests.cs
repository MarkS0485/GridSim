using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R061: exciters (SEXS / IEEET1, optionally with a PSS1A) wired into the RMS transient-stability study
/// (<see cref="RmsTransientStability"/>) on WSCC-9 with GENROU-like machine data:
///   - NULL PATH UNCHANGED: passing no exciters, passing <c>exciters: null</c>, and passing an all-null list each
///     reproduce the historical held-field trajectories bit for bit (exact equality), and the CCT likewise;
///   - EQUILIBRIUM: with an exciter on every machine, an undisturbed run stays stationary (the voltage reference
///     is set from the measured initial terminal voltage), to the same tolerances the held-field fixed-point test
///     uses (speed below 1e-6 pu, angle and EMFs within 1e-5);
///   - ACTION: after a fault the controlled run differs from the held one, in the physical direction (the AVR
///     raises the field while the terminal voltage is depressed, so E'q at clearing is above the held run's);
///   - FAIL LOUD: mis-sized lists, shared instances, an exciter on a fault at t = 0 and an initial field outside
///     the exciter limits all throw.
/// </summary>
public class RmsExciterStudyTests
{
    private const double F0 = 50.0;
    private const double Step = 0.005;
    private const int FaultBus = 7;

    // GENROU-like data on every machine, as SubtransientInitTests uses for its fixed-point test.
    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));
        return data;
    }

    // A SEXS with transient gain reduction (Ta/Tb = 0.1), K = 100, Te = 0.05 s, field limits [0, 8] pu.
    private static RmsExciter NewSexs(Pss1a? pss = null)
        => RmsExciter.FromSexs(leadSeconds: 0.1, lagSeconds: 1.0, gain: 100.0, exciterTimeSeconds: 0.05,
            fieldMinPu: 0.0, fieldMaxPu: 8.0, stabiliser: pss);

    // An IEEET1: Ka = 50, Ta = 0.02 s, Ke = 1, Te = 0.2 s, Kf = 0.05, Tf = 1 s, Vr in [-10, 10] pu, unsaturated.
    private static RmsExciter NewIeeet1()
        => RmsExciter.FromIeeet1(regulatorGain: 50.0, regulatorTimeSeconds: 0.02, exciterConstant: 1.0,
            exciterTimeSeconds: 0.2, feedbackGain: 0.05, feedbackTimeSeconds: 1.0,
            regulatorMinPu: -10.0, regulatorMaxPu: 10.0);

    // A PSS1A on speed deviation: Ks = 20, Tw = 10 s, two 0.15/0.05 s lead stages, output limited to +-0.1 pu.
    private static Pss1a NewPss() => new(gain: 20.0, washoutSeconds: 10.0,
        lead1Seconds: 0.15, lag1Seconds: 0.05, lead2Seconds: 0.15, lag2Seconds: 0.05,
        outputMinPu: -0.1, outputMaxPu: 0.1);

    private static List<RmsExciter?> Each(GridModel model, Func<RmsExciter> make)
    {
        var list = new List<RmsExciter?>();
        for (int i = 0; i < model.Generators.Count; i++) list.Add(make());
        return list;
    }

    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf, GenrouData(model));
    }

    // Exact (bit-for-bit) equality of two runs: every sample of every trace.
    private static void AssertIdentical(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace x = a.Machines[i], y = b.Machines[i];
            Assert.Equal(x.TimeSeconds, y.TimeSeconds);
            Assert.Equal(x.DeltaRad, y.DeltaRad);
            Assert.Equal(x.SpeedPu, y.SpeedPu);
            Assert.Equal(x.EqpPu, y.EqpPu);
            Assert.Equal(x.EdpPu, y.EdpPu);
            Assert.Equal(x.EqppPu, y.EqppPu);
            Assert.Equal(x.EddpPu, y.EddpPu);
        }
    }

    // Undisturbed means stationary: final sample equals the first to the held-field fixed-point tolerances.
    private static void AssertStationary(MultiMachineSubtransient.Result r)
    {
        Assert.True(r.StayedInSynchronism);
        for (int i = 0; i < r.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace m = r.Machines[i];
            int last = m.DeltaRad.Count - 1;
            Assert.True(Math.Abs(m.SpeedPu[last]) < 1e-6, $"machine {i} drifted in speed: {m.SpeedPu[last]:R}");
            Assert.True(Math.Abs(m.DeltaRad[last] - m.DeltaRad[0]) < 1e-5, $"machine {i} angle drifted");
            Assert.True(Math.Abs(m.EqpPu[last] - m.EqpPu[0]) < 1e-5, $"machine {i} E'q drifted");
            Assert.True(Math.Abs(m.EqppPu[last] - m.EqppPu[0]) < 1e-5, $"machine {i} E''q drifted");
            Assert.True(Math.Abs(m.EddpPu[last] - m.EddpPu[0]) < 1e-5, $"machine {i} E''d drifted");
        }
    }

    private static double MaxAbsDifference(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        Assert.Equal(a.Count, b.Count);
        double m = 0.0;
        for (int k = 0; k < a.Count; k++) m = Math.Max(m, Math.Abs(a[k] - b[k]));
        return m;
    }

    // ---- The null path is the historical study, bit for bit ----
    [Fact]
    public void No_exciters_reproduce_the_held_field_study_bit_for_bit()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();

        // The historical call, which does not name the new parameter at all.
        MultiMachineSubtransient.Result old = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.08,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0);
        MultiMachineSubtransient.Result explicitNull = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.08,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: null);
        // An all-null list goes through the field-controller branch, but every machine's field is held, so the
        // numbers must still be the held-field ones exactly.
        var allNull = new List<RmsExciter?>();
        for (int i = 0; i < model.Generators.Count; i++) allNull.Add(null);
        MultiMachineSubtransient.Result heldByList = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.08,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: allNull);

        AssertIdentical(old, explicitNull);
        AssertIdentical(old, heldByList);
    }

    [Fact]
    public void No_exciters_reproduce_the_held_field_cct_bit_for_bit()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        double old = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, FaultBus,
            f0: F0, stepSeconds: Step, iterations: 10);
        double explicitNull = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, FaultBus,
            f0: F0, stepSeconds: Step, iterations: 10, exciters: null);
        Assert.Equal(old, explicitNull);
    }

    // ---- With exciters the undisturbed fleet is at equilibrium ----
    [Fact]
    public void Sexs_on_every_machine_starts_at_equilibrium()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        // The fault is scheduled after the end of the run: the network never changes.
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 100.0, clearingSeconds: 0.1,
            f0: F0, stepSeconds: Step, durationSeconds: 5.0, exciters: Each(model, () => NewSexs()));
        AssertStationary(r);
    }

    [Fact]
    public void Ieeet1_on_every_machine_starts_at_equilibrium()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 100.0, clearingSeconds: 0.1,
            f0: F0, stepSeconds: Step, durationSeconds: 5.0, exciters: Each(model, NewIeeet1));
        AssertStationary(r);
    }

    [Fact]
    public void Sexs_with_pss_on_every_machine_starts_at_equilibrium()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 100.0, clearingSeconds: 0.1,
            f0: F0, stepSeconds: Step, durationSeconds: 5.0, exciters: Each(model, () => NewSexs(NewPss())));
        AssertStationary(r);
    }

    // ---- After a fault the exciter acts, in the physical direction ----
    [Fact]
    public void After_a_fault_the_sexs_run_differs_from_the_held_field_run()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        const double onset = 0.1, clear = 0.05;
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: onset, clearingSeconds: clear,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0);
        MultiMachineSubtransient.Result avr = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: onset, clearingSeconds: clear,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: Each(model, () => NewSexs()));

        Assert.True(held.StayedInSynchronism);
        Assert.True(avr.StayedInSynchronism);
        int atClear = (int)Math.Round((onset + clear) / Step);
        for (int i = 0; i < model.Generators.Count; i++)
        {
            // Both runs start from the same initialised state (the first sample is the seeded E'q itself).
            Assert.Equal(held.Machines[i].EqpPu[0], avr.Machines[i].EqpPu[0]);
            // During the fault the terminal voltage collapses, the AVR drives the field up, so E'q at clearing
            // is above the held-field run's.
            Assert.True(avr.Machines[i].EqpPu[atClear] > held.Machines[i].EqpPu[atClear],
                $"machine {i}: E'q at clearing {avr.Machines[i].EqpPu[atClear]:R} not above held {held.Machines[i].EqpPu[atClear]:R}");
            // And the trajectories differ materially over the run.
            Assert.True(MaxAbsDifference(avr.Machines[i].EqpPu, held.Machines[i].EqpPu) > 1e-3, $"machine {i}: E'q unchanged");
            Assert.True(MaxAbsDifference(avr.Machines[i].DeltaRad, held.Machines[i].DeltaRad) > 1e-6, $"machine {i}: angle unchanged");
        }
    }

    [Fact]
    public void After_a_fault_the_pss_changes_the_sexs_run_and_ieeet1_differs_from_held()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0);
        MultiMachineSubtransient.Result sexs = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: Each(model, () => NewSexs()));
        MultiMachineSubtransient.Result sexsPss = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: Each(model, () => NewSexs(NewPss())));
        MultiMachineSubtransient.Result ieeet1 = RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 3.0, exciters: Each(model, NewIeeet1));

        for (int i = 0; i < model.Generators.Count; i++)
        {
            Assert.True(MaxAbsDifference(sexsPss.Machines[i].SpeedPu, sexs.Machines[i].SpeedPu) > 1e-7,
                $"machine {i}: the PSS left the speed trajectory unchanged");
            Assert.True(MaxAbsDifference(ieeet1.Machines[i].EqpPu, held.Machines[i].EqpPu) > 1e-3,
                $"machine {i}: IEEET1 left E'q unchanged");
        }
    }

    [Fact]
    public void Cct_with_exciters_runs_and_is_interior()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        double cct = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, FaultBus,
            f0: F0, stepSeconds: Step, iterations: 8, exciters: Each(model, () => NewSexs()));
        Assert.True(cct > 0.0 && cct <= RmsTransientStability.CctDefaultMaxSeconds, $"CCT {cct:R}");
    }

    // ---- Fail loud ----
    [Fact]
    public void A_mis_sized_exciter_list_throws()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        var shortList = new List<RmsExciter?> { NewSexs() };   // one entry for three generators
        Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 0.5, exciters: shortList));
    }

    [Fact]
    public void A_shared_exciter_or_pss_instance_throws()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        RmsExciter shared = NewSexs();
        var sameExciter = new List<RmsExciter?>();
        for (int i = 0; i < model.Generators.Count; i++) sameExciter.Add(shared);
        Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 0.5, exciters: sameExciter));

        Pss1a pss = NewPss();
        var samePss = new List<RmsExciter?>();
        for (int i = 0; i < model.Generators.Count; i++) samePss.Add(NewSexs(pss));
        Assert.Throws<ArgumentException>(() => RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 0.5, exciters: samePss));
    }

    [Fact]
    public void A_fault_at_time_zero_with_exciters_throws()
    {
        // The voltage references are measured on the undisturbed network at t = 0; a fault already in force then
        // would bake the disturbance into them, so the run refuses.
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        Assert.Throws<InvalidOperationException>(() => RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.0, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 0.5, exciters: Each(model, () => NewSexs())));
    }

    [Fact]
    public void An_initial_field_outside_the_exciter_limits_throws()
    {
        // A field ceiling of 0.5 pu cannot hold any WSCC-9 machine's initial field, so no equilibrium exists.
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case();
        Assert.Throws<InvalidOperationException>(() => RmsTransientStability.Study(
            model, pf, data, faultBusId: FaultBus, onsetSeconds: 0.1, clearingSeconds: 0.05,
            f0: F0, stepSeconds: Step, durationSeconds: 0.5,
            exciters: Each(model, () => RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, fieldMinPu: 0.0, fieldMaxPu: 0.5))));
    }

    [Fact]
    public void Inverted_exciter_limits_throw_at_construction()
    {
        Assert.Throws<ArgumentException>(() => RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, fieldMinPu: 5.0, fieldMaxPu: 1.0));
        Assert.Throws<ArgumentException>(() => RmsExciter.FromIeeet1(50.0, 0.02, 1.0, 0.2, 0.05, 1.0,
            regulatorMinPu: 5.0, regulatorMaxPu: 1.0));
    }
}
