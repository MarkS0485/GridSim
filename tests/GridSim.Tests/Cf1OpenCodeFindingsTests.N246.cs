using System;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Physics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N246 (CF1, October 2026; Ruling R1): law 9 (synchronism) is scored in the classical <c>stability --clear</c> from the
/// run's own rotor-angle trajectory, with the out-of-step criterion of PSS/E and PowerFactory (any machine more than 180
/// degrees from the centre of inertia at any sample; no frequency band in a fault study). The steady-state law 9 text
/// and the default (non angle-only) scoring are unchanged.
/// </summary>
public sealed partial class Cf1OpenCodeFindingsTests
{
    private static ClassicalMultiMachine.Result N246Run(double[][] delta, double[][] speed)
    {
        var machines = new ClassicalMultiMachine.MachineTrace[delta.Length];
        for (int i = 0; i < delta.Length; i++)
        {
            var t = new double[delta[i].Length];
            for (int k = 0; k < t.Length; k++) t[k] = k * 0.01;
            machines[i] = new ClassicalMultiMachine.MachineTrace(t, delta[i], speed[i]);
        }
        return new ClassicalMultiMachine.Result(machines, true);
    }

    [Fact]
    public void N246_the_sample_is_the_largest_angle_from_the_inertia_weighted_centre_exactly()
    {
        // Two machines, H = 1 and 3. Sample 1: delta = (1, -1) rad, COI = (1 - 3) / 4 = -0.5, deviations 1.5 and 0.5.
        // Sample 2: delta = (0.2, 0.4), COI = (0.2 + 1.2) / 4 = 0.35, deviations 0.15 and 0.05.
        ClassicalMultiMachine.Result run = N246Run(
            new[] { new[] { 0.0, 1.0, 0.2 }, new[] { 0.0, -1.0, 0.4 } },
            new[] { new[] { 0.0, 0.01, -0.003 }, new[] { 0.0, -0.02, 0.001 } });
        SynchronismSample s = SynchronismFromTrajectory.Classical(run, new[] { 1.0, 3.0 }, 60.0);
        Assert.Equal(1.5 * (180.0 / Math.PI), s.MaxAngleSpreadDeg);
        Assert.Equal(0.02 * 60.0, s.MaxFreqExcursionHz);
        Assert.Equal(180.0, s.PoleSlipLimitDeg);
        Assert.True(s.AngleOnly);
    }

    [Fact]
    public void N246_angle_only_scoring_holds_below_180_degrees_and_is_violated_above_or_on_a_non_finite_run()
    {
        LawResult ok = PhysicsLaws.ScoreSynchronism(new SynchronismSample(90.0, 5.0, AngleOnly: true));
        Assert.Equal(LawStatus.Holds, ok.Status);
        Assert.Equal(0.5, ok.Residual);                       // 90 / 180; the 5 Hz excursion is reported, not scored
        Assert.Equal(9, ok.Number);
        Assert.Equal("largest rotor angle from the centre of inertia 90.0 deg (limit 180 deg, the out-of-step criterion); "
            + "peak speed deviation 5.000 Hz, reported, not scored (no frequency band in a fault study)", ok.Detail);

        Assert.Equal(LawStatus.Holds, PhysicsLaws.ScoreSynchronism(new SynchronismSample(180.0, 0, AngleOnly: true)).Status);   // the limit itself holds
        LawResult lost = PhysicsLaws.ScoreSynchronism(new SynchronismSample(200.0, 0.1, AngleOnly: true));
        Assert.Equal(LawStatus.Violated, lost.Status);
        Assert.Equal(200.0 / 180.0, lost.Residual);

        LawResult nan = PhysicsLaws.ScoreSynchronism(new SynchronismSample(double.PositiveInfinity, double.PositiveInfinity, AngleOnly: true));
        Assert.Equal(LawStatus.Violated, nan.Status);
        Assert.Equal(double.PositiveInfinity, nan.Residual);
        Assert.StartsWith("a rotor angle became non-finite", nan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void N246_a_non_finite_trajectory_scores_as_a_loss()
    {
        ClassicalMultiMachine.Result run = N246Run(
            new[] { new[] { 0.0, double.NaN }, new[] { 0.0, 0.1 } },
            new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 } });
        SynchronismSample s = SynchronismFromTrajectory.Classical(run, new[] { 1.0, 1.0 }, 50.0);
        Assert.Equal(double.PositiveInfinity, s.MaxAngleSpreadDeg);
        Assert.Equal(LawStatus.Violated, PhysicsLaws.ScoreSynchronism(s).Status);
    }

    [Fact]
    public void N246_the_default_scoring_is_unchanged_angle_and_frequency_margins()
    {
        // Not angle-only: residual = max(angle / limit, excursion / band), as before N246.
        LawResult r = PhysicsLaws.ScoreSynchronism(new SynchronismSample(90.0, 0.6));
        Assert.Equal(Math.Max(90.0 / 180.0, 0.6 / 0.8), r.Residual);
        Assert.Equal(LawStatus.Holds, r.Status);
        Assert.StartsWith("angle spread 90.0 deg (limit 180), freq excursion 0.600 Hz (band 0.80)", r.Detail, StringComparison.Ordinal);
        Assert.Equal(LawStatus.Violated, PhysicsLaws.ScoreSynchronism(new SynchronismSample(90.0, 0.9)).Status);   // the band still binds
    }

    [Fact]
    public void N246_the_trajectory_sample_refuses_inputs_that_do_not_match_the_run()
    {
        ClassicalMultiMachine.Result run = N246Run(new[] { new[] { 0.0 }, new[] { 0.0 } }, new[] { new[] { 0.0 }, new[] { 0.0 } });
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Classical(run, new[] { 1.0 }, 50.0));            // H count
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Classical(run, new[] { 0.0, 0.0 }, 50.0));       // no inertia
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Classical(run, new[] { 1.0, 1.0 }, 0.0));        // frequency
        ClassicalMultiMachine.Result ragged = N246Run(new[] { new[] { 0.0, 0.1 }, new[] { 0.0 } }, new[] { new[] { 0.0, 0.0 }, new[] { 0.0 } });
        Assert.Throws<ArgumentException>(() => SynchronismFromTrajectory.Classical(ragged, new[] { 1.0, 1.0 }, 50.0));
    }

    [Fact]
    public void N246_stability_clear_prints_law_9_beside_an_unchanged_verdict()
    {
        // 150 ms at bus 7 is far below case9's CCT (550 ms at 60 Hz): the run holds and so does law 9.
        CliProcess.Result held = CliProcess.RunBuilt("stability", "case9", "--fault-bus", "7", "--clear", "150");
        Assert.True(held.ExitCode == 0, held.ToString());
        Assert.Contains("3-phase fault at bus 7, cleared after 150 ms  ->  STABLE (holds synchronism)", held.StdOut, StringComparison.Ordinal);
        Assert.Contains("law 9 (synchronism, scored from this run): HOLDS: largest rotor angle from the centre of inertia ", held.StdOut, StringComparison.Ordinal);
        Assert.Contains("(limit 180 deg, the out-of-step criterion)", held.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("  note: a machine passed 180 degrees", held.StdOut, StringComparison.Ordinal);

        // 1000 ms is far above it. Whatever the verdict, the two criteria are consistent: a pole slip (UNSTABLE) is past
        // 180 degrees, so law 9 is then VIOLATED; a STABLE run with law 9 VIOLATED carries the note.
        CliProcess.Result late = CliProcess.RunBuilt("stability", "case9", "--fault-bus", "7", "--clear", "1000");
        Assert.True(late.ExitCode == 0, late.ToString());
        Assert.Contains("law 9 (synchronism, scored from this run): ", late.StdOut, StringComparison.Ordinal);
        bool unstable = late.StdOut.Contains("UNSTABLE (loss of synchronism)", StringComparison.Ordinal);
        bool violated = late.StdOut.Contains("law 9 (synchronism, scored from this run): VIOLATED: ", StringComparison.Ordinal);
        if (unstable) Assert.True(violated, late.StdOut);
        Assert.Equal(!unstable && violated, late.StdOut.Contains("  note: a machine passed 180 degrees", StringComparison.Ordinal));
    }
}
