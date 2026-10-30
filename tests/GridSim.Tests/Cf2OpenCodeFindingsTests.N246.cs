using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N246 rest (CF2, October 2026; Ruling R1): law 9 (synchronism) is scored on the RMS <c>stability --rms --clear</c> path
/// too, from the RMS run's own trajectory on the machine set the RMS verdict judges, with the same 180 degree
/// out-of-step criterion and wording as the classical path (CF1). The steady-state NOT SCORED text now names that verb.
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    /// <summary>An RMS result from hand-written angle and speed traces (the flux traces are zeros, unused by law 9).</summary>
    private static MultiMachineSubtransient.Result N246RmsRun(double[][] delta, double[][] speed)
    {
        var machines = new MultiMachineSubtransient.MachineTrace[delta.Length];
        for (int i = 0; i < delta.Length; i++)
        {
            int n = delta[i].Length;
            var t = new double[n];
            for (int k = 0; k < n; k++) t[k] = k * 0.01;
            machines[i] = new MultiMachineSubtransient.MachineTrace(t, delta[i], speed[i],
                new double[n], new double[n], new double[n], new double[n]);
        }
        return new MultiMachineSubtransient.Result(machines, true);
    }

    [Fact]
    public void N246_rms_sample_is_the_largest_angle_from_the_centre_of_inertia_over_the_synchronism_set_exactly()
    {
        // Machines 0 and 1 are the synchronism set (H 1 and 3), machine 2 an island reference with a wild angle and speed
        // that must be left out of both the COI and the maxima, as the RMS integrator leaves it out of its verdict.
        // Sample 1: delta (1, -1), COI -0.5, deviations 1.5 and 0.5. Sample 2: delta (0.2, 0.4), COI 0.35.
        MultiMachineSubtransient.Result run = N246RmsRun(
            new[] { new[] { 0.0, 1.0, 0.2 }, new[] { 0.0, -1.0, 0.4 }, new[] { 0.0, 50.0, -50.0 } },
            new[] { new[] { 0.0, 0.01, -0.003 }, new[] { 0.0, -0.02, 0.001 }, new[] { 0.0, 9.0, 9.0 } });
        SynchronismSample s = SynchronismFromTrajectory.Subtransient(run, new[] { 1.0, 3.0, 100.0 }, 2, 60.0);
        Assert.Equal(1.5 * (180.0 / Math.PI), s.MaxAngleSpreadDeg);
        Assert.Equal(0.02 * 60.0, s.MaxFreqExcursionHz);
        Assert.Equal(180.0, s.PoleSlipLimitDeg);
        Assert.True(s.AngleOnly);
    }

    [Fact]
    public void N246_rms_sample_over_every_machine_is_bit_identical_to_the_classical_sample_on_the_same_traces()
    {
        double[][] delta = { new[] { 0.1, 0.7, -0.3, 1.9 }, new[] { -0.2, 0.05, 0.4, -1.1 }, new[] { 0.0, 0.3, 0.2, 0.25 } };
        double[][] speed = { new[] { 0.0, 0.004, -0.002, 0.011 }, new[] { 0.0, -0.007, 0.003, 0.0 }, new[] { 0.0, 0.001, 0.0, -0.0005 } };
        double[] h = { 6.4, 1.7, 3.01 };
        var classical = new ClassicalMultiMachine.Result(
            delta.Select((d, i) => new ClassicalMultiMachine.MachineTrace(d.Select((_, k) => k * 0.01).ToArray(), d, speed[i])).ToArray(),
            true);
        SynchronismSample c = SynchronismFromTrajectory.Classical(classical, h, 50.0);
        SynchronismSample r = SynchronismFromTrajectory.Subtransient(N246RmsRun(delta, speed), h, 3, 50.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(c.MaxAngleSpreadDeg), BitConverter.DoubleToInt64Bits(r.MaxAngleSpreadDeg));
        Assert.Equal(BitConverter.DoubleToInt64Bits(c.MaxFreqExcursionHz), BitConverter.DoubleToInt64Bits(r.MaxFreqExcursionHz));
        Assert.Equal(c, r);
    }

    [Fact]
    public void N246_rms_sample_refuses_inputs_that_do_not_match_the_run()
    {
        MultiMachineSubtransient.Result run = N246RmsRun(new[] { new[] { 0.0 }, new[] { 0.0 } }, new[] { new[] { 0.0 }, new[] { 0.0 } });
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Subtransient(run, new[] { 1.0 }, 2, 50.0));          // H count
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Subtransient(run, new[] { 1.0, 1.0 }, 0, 50.0));     // empty set
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Subtransient(run, new[] { 1.0, 1.0 }, 3, 50.0));     // set too big
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Subtransient(run, new[] { 0.0, 0.0 }, 2, 50.0));     // no inertia
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Subtransient(run, new[] { 1.0, 1.0 }, 2, double.NaN)); // frequency
        MultiMachineSubtransient.Result nan = N246RmsRun(new[] { new[] { 0.0, double.NaN }, new[] { 0.0, 0.1 } }, new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 } });
        Assert.Equal(LawStatus.Violated, PhysicsLaws.ScoreSynchronism(SynchronismFromTrajectory.Subtransient(nan, new[] { 1.0, 1.0 }, 2, 50.0)).Status);
    }

    [Fact]
    public void N246_study_with_fleet_is_study_bit_for_bit_and_reports_the_integrators_inertia_and_set()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double xdp = IeeeCases.Wscc9TransientReactancePu[gen.Bus];
            data.Add(new SubtransientInit.MachineData(H: gen.H, Damping: 0.0,
                Xd: 3.0 * xdp, Xdp: xdp, Xdpp: 0.8 * xdp, Xq: 2.5 * xdp, Xqp: 1.5 * xdp, Xqpp: 0.8 * xdp,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        MultiMachineSubtransient.Result plain = RmsTransientStability.Study(model, pf, data, faultBusId: 7,
            onsetSeconds: 0.1, clearingSeconds: 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        RmsTransientStability.FleetStudy fleet = RmsTransientStability.StudyWithFleet(model, pf, data, faultBusId: 7,
            onsetSeconds: 0.1, clearingSeconds: 0.1, stepSeconds: 0.005, durationSeconds: 1.0);

        Assert.Equal(plain.StayedInSynchronism, fleet.Result.StayedInSynchronism);
        Assert.Equal(plain.Machines.Count, fleet.Result.Machines.Count);
        for (int i = 0; i < plain.Machines.Count; i++)
        {
            Assert.Equal(plain.Machines[i].DeltaRad.Select(BitConverter.DoubleToInt64Bits), fleet.Result.Machines[i].DeltaRad.Select(BitConverter.DoubleToInt64Bits));
            Assert.Equal(plain.Machines[i].SpeedPu.Select(BitConverter.DoubleToInt64Bits), fleet.Result.Machines[i].SpeedPu.Select(BitConverter.DoubleToInt64Bits));
        }
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, pf, data, 7);
        Assert.Equal(p.H, fleet.H);
        Assert.Equal(p.Machines.Count - (p.IslandReferences?.Count ?? 0), fleet.SynchronismMachines);
        Assert.Equal(model.FrequencyHz, fleet.NominalHz);
    }

    [Fact]
    public void N246_stability_rms_clear_prints_law_9_beside_an_unchanged_verdict()
    {
        CliProcess.Result held = CliProcess.RunBuilt("stability", "case9", "--rms", "--fault-bus", "7", "--clear", "100");
        Assert.True(held.ExitCode == 0, held.ToString());
        Assert.Contains("fault bus 7  *  cleared after 100 ms  *  STABLE (fleet stays in synchronism)", held.StdOut, StringComparison.Ordinal);
        Assert.Contains("law 9 (synchronism, scored from this run): HOLDS: largest rotor angle from the centre of inertia ", held.StdOut, StringComparison.Ordinal);
        Assert.Contains("(limit 180 deg, the out-of-step criterion); peak speed deviation ", held.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("  note: a machine passed 180 degrees", held.StdOut, StringComparison.Ordinal);

        // At the 1000 ms bound the two criteria must stay consistent: a pole slip is past 180 degrees.
        CliProcess.Result late = CliProcess.RunBuilt("stability", "case9", "--rms", "--fault-bus", "7", "--clear", "1000");
        Assert.True(late.ExitCode == 0, late.ToString());
        Assert.Contains("law 9 (synchronism, scored from this run): ", late.StdOut, StringComparison.Ordinal);
        bool unstable = late.StdOut.Contains("UNSTABLE (loss of synchronism)", StringComparison.Ordinal);
        bool violated = late.StdOut.Contains("law 9 (synchronism, scored from this run): VIOLATED: ", StringComparison.Ordinal);
        if (unstable) Assert.True(violated, late.StdOut);
        Assert.Equal(!unstable && violated, late.StdOut.Contains("  note: a machine passed 180 degrees", StringComparison.Ordinal));
    }

    [Fact]
    public void N246_the_steady_state_law_9_text_names_the_verb_that_scores_it()
    {
        GridModel case14 = IeeeCases.Case14();
        LawResult law9 = PhysicsLaws.Audit(case14, new NewtonRaphsonPowerFlow().Solve(case14))
            .Laws.Single(l => l.Number == 9);
        Assert.Equal(LawStatus.NotApplicable, law9.Status);
        Assert.Equal("NOT SCORED: steady-state snapshot at 50 Hz with no trajectory; law 9 is scored only when a dynamics "
            + "caller supplies a SynchronismSample: stability --clear (classical or --rms) scores it from its own run, "
            + "no steady-state verb or export does", law9.Detail);
        Assert.DoesNotContain("and no verb or export does", law9.Detail, StringComparison.Ordinal);
    }
}
