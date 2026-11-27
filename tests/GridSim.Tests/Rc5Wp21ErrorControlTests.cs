using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP21: the error-controlled step of the full RMS model (<see cref="RmsErrorControl"/>): step doubling over whole runs,
/// so no component needs a snapshot. The controller is first checked on a run whose every value is known in closed form
/// (forward Euler on dx/dt = -r(t) x, x(0) = 1, whose samples are the products of (1 - r dt)), with the expected step levels
/// found here by a separate search; then on the full model (structure, exact event landing, re-run of the chosen schedule,
/// determinism).
/// </summary>
public sealed class Rc5Wp21ErrorControlTests
{
    /// <summary>A run in the kernel's result shape: forward Euler of dx/dt = -rate(t) x on the schedule, x in the speed
    /// series, every other state 0 (so it adds nothing to the scaled error).</summary>
    private static MultiMachineSubtransient.Result Euler(RmsStepSchedule s, double duration, Func<double, double> rate)
    {
        (double[] times, double[] steps) = s.Expand(duration);
        var x = new double[times.Length];
        x[0] = 1.0;
        for (int k = 0; k < steps.Length; k++) x[k + 1] = x[k] * (1.0 - rate(times[k]) * steps[k]);
        var zero = new double[times.Length];
        var trace = new MultiMachineSubtransient.MachineTrace(times, zero, x, zero, zero, zero, zero);
        return new MultiMachineSubtransient.Result(new[] { trace }, true);
    }

    /// <summary>The largest |coarse - fine| over the coarse samples of one segment of length <paramref name="len"/> with
    /// coarse step len / 2^level, for dx/dt = -x from x = 1 at the segment start, computed sample by sample here.</summary>
    private static double SegmentDifference(double len, int level)
    {
        double dt = Math.ScaleB(len, -level), half = dt / 2.0;
        int n = 1 << level;
        double xc = 1.0, xf = 1.0, worst = 0.0;
        for (int j = 1; j <= n; j++)
        {
            xc *= 1.0 - dt;
            xf *= 1.0 - half;
            xf *= 1.0 - half;
            worst = Math.Max(worst, Math.Abs(xc - xf));
        }
        return worst;
    }

    /// <summary>The first level from <paramref name="start"/> at which the difference is within <paramref name="tol"/>.</summary>
    private static int FirstPassingLevel(double len, int start, double tol)
    {
        int level = start;
        while (!(SegmentDifference(len, level) <= tol)) level++;
        return level;
    }

    // Absolute tolerance 1e-3, relative 1e-12 (it must be above 0): the scale is 1e-3 + 1e-12 |x| with |x| <= 1, so the
    // decision "scaled error <= 1" is "|difference| <= 1e-3 (1 + 1e-9)"; the search below uses 1e-3 exactly, and no level's
    // difference lies within 1e-12 of 1e-3 (each level halves it, about 0.09 dt), so the two agree. These cases set the
    // safety factor to 1 (accept at the tolerance itself); the default 0.5 is checked separately below.
    private static readonly RmsErrorControlOptions Tight = new(RelativeTolerance: 1e-12, AbsoluteTolerance: 1e-3,
        MaxStepSeconds: 0.25, MinStepSeconds: Math.ScaleB(1.0, -14), SegmentSeconds: 1.0, MaxPasses: 20, SafetyFactor: 1.0);

    [Fact]
    public void One_segment_is_halved_until_the_difference_meets_the_tolerance()
    {
        // One segment [0, 1]; the largest step 0.25 gives level 2. Each pass runs level L and L + 1 and refines while the
        // difference is above 1e-3. The returned run is the finer one, so its step is 2^-(L* + 1).
        RmsErrorControlResult<MultiMachineSubtransient.Result> r = RmsErrorControl.Run(s => Euler(s, 1.0, _ => 1.0), x => x, 1.0, Array.Empty<double>(), Tight);
        int lStar = FirstPassingLevel(1.0, 2, 1e-3);
        Assert.True(r.ToleranceMet);
        Assert.Null(r.Note);
        Assert.Equal(lStar - 2 + 1, r.Passes);
        Assert.Equal(2 * r.Passes, r.Runs);
        Assert.Single(r.Schedule.Windows);
        Assert.Equal(Math.ScaleB(1.0, -(lStar + 1)), r.Schedule.Windows[0].StepSeconds);
        // The scale is 1e-3 + 1e-12 max(|x|) with |x| <= 1, within 1e-9 relative of 1e-3, and the scaled error is at most 1.
        Assert.Equal(SegmentDifference(1.0, lStar) / 1e-3, r.MaxScaledError, 1e-8);
        // The returned run is the finer one: x at the end is (1 - dt)^(1/dt) with dt = 2^-(L* + 1).
        double dt = r.Schedule.Windows[0].StepSeconds, x = 1.0;
        for (int j = 0; j < 1 << (lStar + 1); j++) x *= 1.0 - dt;
        Assert.Equal(x, r.Run.Machines[0].SpeedPu[^1]);
    }

    [Fact]
    public void A_segment_with_no_error_keeps_its_step_while_another_is_refined()
    {
        // dx/dt = 0 on [0, 0.5), -x on [0.5, 1]: the first segment's runs agree exactly at every level, so it stays at its
        // starting level (0.5 / 2 = 0.25); the second is the one-segment problem on a length of 0.5, starting from x = 1.
        var o = Tight with { SegmentSeconds = 0.5 };
        RmsErrorControlResult<MultiMachineSubtransient.Result> r = RmsErrorControl.Run(s => Euler(s, 1.0, t => t < 0.5 ? 0.0 : 1.0), x => x, 1.0,
            Array.Empty<double>(), o);
        int l2 = FirstPassingLevel(0.5, 1, 1e-3);
        Assert.True(r.ToleranceMet);
        Assert.Equal(2, r.Schedule.Windows.Count);
        Assert.Equal(0.5 / 4.0, r.Schedule.Windows[0].StepSeconds);
        Assert.Equal(Math.ScaleB(0.5, -(l2 + 1)), r.Schedule.Windows[1].StepSeconds);
        Assert.Equal(0.0, r.Segments[0].ScaledError);
        Assert.Equal(l2 - 1 + 1, r.Passes);
    }

    [Fact]
    public void A_tolerance_that_needs_less_than_the_smallest_step_is_reported_not_hidden()
    {
        // The smallest step is half the largest, so no segment can be refined; with a tolerance of 1e-12 the first pass
        // already fails it. The controller stops after one pass and says so.
        var o = new RmsErrorControlOptions(RelativeTolerance: 1e-12, AbsoluteTolerance: 1e-12, MaxStepSeconds: 0.25, MinStepSeconds: 0.125,
            SegmentSeconds: 1.0, MaxPasses: 20, SafetyFactor: 1.0);
        RmsErrorControlResult<MultiMachineSubtransient.Result> r = RmsErrorControl.Run(s => Euler(s, 1.0, _ => 1.0), x => x, 1.0, Array.Empty<double>(), o);
        Assert.False(r.ToleranceMet);
        Assert.Equal(1, r.Passes);
        Assert.Equal(2, r.Runs);
        Assert.NotNull(r.Note);
        Assert.Contains("smallest step", r.Note!, StringComparison.Ordinal);
        Assert.True(r.MaxScaledError > 1.0);
    }

    [Fact]
    public void Segment_boundaries_hold_every_event_time_and_the_grid()
    {
        // Segment 0.1 over 1 s; events at 0.1 (on the grid: one boundary), 0.24 (twice: one boundary), 0.30000001 (within a
        // millionth of a segment of the grid point 0.3, which is dropped), and 0 and 1.0 and 5 (not inside the run: none).
        double[] b = RmsErrorControl.Boundaries(1.0, new[] { 0.1, 0.24, 0.24, 0.30000001, 0.0, 1.0, 5.0 }, 0.1);
        var want = new List<double> { 0.0 };
        for (int k = 1; k <= 9; k++)
        {
            double gp = k * 0.1;
            if (Math.Abs(gp - 0.30000001) <= 0.1 * 1e-6) continue;
            if (gp != 0.1) want.Add(gp);
        }
        want.AddRange(new[] { 0.1, 0.24, 0.30000001 });
        want.Sort();
        want.Add(1.0);
        Assert.Equal(want, b);
    }

    [Fact]
    public void Every_coarse_sample_time_is_a_fine_sample_time_exactly()
    {
        // (2j)(dt/2) = j dt in binary floating point (dt/2 is exact, and the product is the same real number), so the two
        // runs of a pass meet at every coarse sample. Checked at every sample of a schedule with uneven segments.
        double[] b = RmsErrorControl.Boundaries(1.0, new[] { 0.1, 0.24, 0.37 }, 0.1);
        var levels = new int[b.Length - 1];
        for (int s = 0; s < levels.Length; s++) levels[s] = 1 + s % 5;
        (double[] tc, _) = RmsErrorControl.Schedule(b, levels, 0).Expand(1.0);
        (double[] tf, _) = RmsErrorControl.Schedule(b, levels, 1).Expand(1.0);
        Assert.Equal(2 * (tc.Length - 1) + 1, tf.Length);
        var fine = new HashSet<double>(tf);
        foreach (double t in tc) Assert.Contains(t, fine);
    }

    // Observed on the first run (2.1.126), a record: the SmibFault run loses synchronism (the rotor angle passes 8.7 rad by
    // 1 s), so the step-doubling difference grows without bound after the first swing and the tolerance cannot be met; the
    // controller refines to the smallest step and says so. That is the reported, not hidden, outcome the result is for.
    private static readonly (bool, int, int, int, double) Observed = (false, 7, 14, 10, 17.659285412496686);

    /// <summary>2.1.126: the default safety factor 0.5 accepts a segment only when its difference is within half the
    /// tolerance, so the one-segment case of the first test stops at the first level whose difference is at most 0.5e-3.</summary>
    [Fact]
    public void The_safety_factor_asks_for_half_the_tolerance()
    {
        var o = Tight with { SafetyFactor = 0.5 };
        RmsErrorControlResult<MultiMachineSubtransient.Result> r = RmsErrorControl.Run(s => Euler(s, 1.0, _ => 1.0), x => x, 1.0, Array.Empty<double>(), o);
        int lHalf = FirstPassingLevel(1.0, 2, 0.5e-3);
        Assert.True(lHalf > FirstPassingLevel(1.0, 2, 1e-3));
        Assert.True(r.ToleranceMet);
        Assert.Equal(lHalf - 2 + 1, r.Passes);
        Assert.Equal(Math.ScaleB(1.0, -(lHalf + 1)), r.Schedule.Windows[0].StepSeconds);
        Assert.True(r.MaxScaledError <= 0.5);
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(SafetyFactor: 0.0).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(SafetyFactor: 1.5).Validate());
        Assert.StartsWith("EXPERIMENTAL:", RmsErrorControl.ExperimentalNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_model_run_lands_events_exactly_and_its_schedule_re_runs_to_the_same_bits()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Rc5Wp21BitIdentityTests.Smib();
        RmsEvent[] ev = Rc5Wp21BitIdentityTests.SmibFault();
        var o = new RmsErrorControlOptions(MaxStepSeconds: 0.02, MinStepSeconds: 1e-4, SegmentSeconds: 0.1);
        RmsErrorControlResult<MultiMachineSubtransient.Result> r = RmsTransientStability.StudyEventsErrorControlled(m, pf, data, ev, o, durationSeconds: 1.0);
        // Every window is a segment: its step is its length over a power of two, at or below half the largest step (the
        // returned run is the finer of a pass) and at or above the smallest step.
        for (int s = 0; s < r.Schedule.Windows.Count; s++)
        {
            double from = r.Schedule.Windows[s].FromSeconds, to = s + 1 < r.Schedule.Windows.Count ? r.Schedule.Windows[s + 1].FromSeconds : 1.0;
            double ratio = (to - from) / r.Schedule.Windows[s].StepSeconds;
            Assert.Equal(Math.Round(ratio), ratio);
            Assert.True(double.IsPow2(ratio), $"window {s}: {ratio} steps");
            Assert.True(r.Schedule.Windows[s].StepSeconds <= o.MaxStepSeconds / 2.0);
            Assert.True(r.Schedule.Windows[s].StepSeconds >= o.MinStepSeconds);
        }
        // The fault and its clearance start windows, so both land on their own times.
        Assert.Contains(r.Schedule.Windows, w => w.FromSeconds == 0.1);
        Assert.Contains(r.Schedule.Windows, w => w.FromSeconds == 0.2);
        // The returned run IS a run of its schedule.
        MultiMachineSubtransient.Result again = RmsTransientStability.StudyEventsScheduled(m, pf, data, ev, r.Schedule, durationSeconds: 1.0);
        Assert.Equal(Rc5Wp21BitIdentityTests.Digest(again), Rc5Wp21BitIdentityTests.Digest(r.Run));
        // The verdict on the tolerance is consistent with the segments.
        Assert.Equal(r.ToleranceMet, r.Segments.All(g => g.ScaledError <= o.SafetyFactor));
        // The observed figures of this run (first run, 2.1.126, default tolerances and safety factor 0.5), recorded exactly.
        Assert.False(r.Run.StayedInSynchronism);
        Assert.True((r.ToleranceMet, r.Passes, r.Runs, r.Schedule.Windows.Count, r.MaxScaledError) == Observed,
            $"observed: met {r.ToleranceMet}, passes {r.Passes}, runs {r.Runs}, windows {r.Schedule.Windows.Count}, max scaled {r.MaxScaledError:R}");
        Assert.Equal(r.ToleranceMet, r.Note is null);
        Assert.Equal(r.Segments.Max(g => g.ScaledError), r.MaxScaledError);
        Assert.Equal(2 * r.Passes, r.Runs);
        // Deterministic: the same inputs give the same schedule and bits.
        RmsErrorControlResult<MultiMachineSubtransient.Result> r2 = RmsTransientStability.StudyEventsErrorControlled(m, pf, data, ev, o, durationSeconds: 1.0);
        Assert.Equal(r.Schedule.Windows, r2.Schedule.Windows);
        Assert.Equal(Rc5Wp21BitIdentityTests.Digest(r.Run), Rc5Wp21BitIdentityTests.Digest(r2.Run));
    }

    [Fact]
    public void Bad_options_are_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(RelativeTolerance: 0.0).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(AbsoluteTolerance: double.NaN).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(MaxStepSeconds: 0.01, MinStepSeconds: 0.006).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(MaxPasses: 0).Validate());
        Assert.ThrowsAny<ArgumentException>(() => new RmsErrorControlOptions(SegmentSeconds: -1.0).Validate());
    }
}
