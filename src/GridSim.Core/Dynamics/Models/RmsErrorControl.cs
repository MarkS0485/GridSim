using System;
using System.Collections.Generic;
using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP21 (RC5): the tolerances and limits of the error-controlled step for the full RMS model (<see cref="RmsErrorControl"/>).
/// </summary>
/// <param name="RelativeTolerance">Relative part of the error scale for the speed and the four flux states.</param>
/// <param name="AbsoluteTolerance">Absolute part of the error scale (pu for speed and flux states, rad for angles).</param>
/// <param name="MaxStepSeconds">The largest step the controller starts from (s).</param>
/// <param name="MinStepSeconds">The smallest step the returned run may use (s); a segment that would need less is reported.</param>
/// <param name="SegmentSeconds">The length of the time segments the step is chosen for (s); event times also start a segment.</param>
/// <param name="MaxPasses">The cap on refinement passes (each pass is two full runs).</param>
/// <param name="SafetyFactor">A segment is accepted when its scaled difference is at most this (in (0, 1]; 1 = the
/// tolerance itself). The default 0.5 asks the measured difference to be within half the tolerance, because it is only a
/// first-order estimate of the error of the returned run.</param>
public sealed record RmsErrorControlOptions(
    double RelativeTolerance = 1e-3, double AbsoluteTolerance = 1e-4,
    double MaxStepSeconds = 0.02, double MinStepSeconds = 1e-4,
    double SegmentSeconds = 0.1, int MaxPasses = 12, double SafetyFactor = 0.5)
{
    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Refuse a value outside its range (an <see cref="ArgumentException"/> naming it).</summary>
    public void Validate()
    {
        static void Positive(double v, string name)
        {
            if (!(v > 0.0) || !double.IsFinite(v)) throw new ArgumentException($"{name} must be a finite number above 0 (got {Num(v)}).");
        }
        Positive(RelativeTolerance, nameof(RelativeTolerance));
        Positive(AbsoluteTolerance, nameof(AbsoluteTolerance));
        Positive(MaxStepSeconds, nameof(MaxStepSeconds));
        Positive(MinStepSeconds, nameof(MinStepSeconds));
        Positive(SegmentSeconds, nameof(SegmentSeconds));
        if (!(MinStepSeconds <= MaxStepSeconds / 2.0))
            throw new ArgumentException($"MinStepSeconds ({Num(MinStepSeconds)} s) must be at most half of MaxStepSeconds ({Num(MaxStepSeconds)} s).");
        if (MaxPasses < 1) throw new ArgumentException($"MaxPasses must be at least 1 (got {MaxPasses.ToString(CultureInfo.InvariantCulture)}).");
        if (!(SafetyFactor > 0.0 && SafetyFactor <= 1.0)) throw new ArgumentException($"SafetyFactor must lie in (0, 1] (got {Num(SafetyFactor)}).");
    }
}

/// <summary>WP21: one time segment of an error-controlled run.</summary>
/// <param name="FromSeconds">The segment's start (s).</param>
/// <param name="ToSeconds">The segment's end (s).</param>
/// <param name="StepSeconds">The step the returned run uses in the segment (s).</param>
/// <param name="ScaledError">The largest scaled difference in the segment between the last pass's two runs (1 = tolerance).</param>
public sealed record RmsErrorControlSegment(double FromSeconds, double ToSeconds, double StepSeconds, double ScaledError);

/// <summary>WP21: the outcome of an error-controlled run.</summary>
/// <typeparam name="T">The run's result type.</typeparam>
/// <param name="Run">The returned run (the finer of the last pass's two runs).</param>
/// <param name="Schedule">Its step schedule: running it on <see cref="RmsStepSchedule"/> again gives the same bits.</param>
/// <param name="ToleranceMet">True when every segment's scaled error is at most the safety factor.</param>
/// <param name="MaxScaledError">The largest scaled error of the last pass.</param>
/// <param name="Passes">Refinement passes taken.</param>
/// <param name="Runs">Full runs made (two per pass).</param>
/// <param name="Segments">Each segment's step and scaled error, in time order.</param>
/// <param name="Note">Why the tolerance was not met (null when it was).</param>
public sealed record RmsErrorControlResult<T>(T Run, RmsStepSchedule Schedule, bool ToleranceMet, double MaxScaledError, int Passes,
    int Runs, IReadOnlyList<RmsErrorControlSegment> Segments, string? Note)
{
    /// <summary>The coarser run of the last pass (the schedule with every step doubled), for a caller that compares the two.</summary>
    public T? CoarseRun { get; init; }
}

/// <summary>
/// WP21 (RC5), EXPERIMENTAL: an error-controlled step for the full RMS model (<see cref="MultiMachineSubtransient"/>), by
/// step doubling over whole runs. It is opt-in and not part of the verified contract: its error figure is a first-order
/// estimate, not a bound, and a run can cost up to 2 x <see cref="RmsErrorControlOptions.MaxPasses"/> full simulations
/// (24 with the defaults).
///
/// <para><b>Why whole runs.</b> A classic controller (the reduced swing kernel's Dormand-Prince) rejects a step and retries it
/// from the saved state. The full model has about 40 component classes, each keeping its own integrator state with no
/// snapshot, so a step cannot be taken back. The kernel is deterministic, so a run is repeatable to the bit from its
/// inputs: instead of taking a step back, the controller re-runs the whole simulation with a finer schedule. No component
/// needs a snapshot, and every run is an ordinary step-schedule run (<see cref="RmsStepSchedule"/>).</para>
///
/// <para><b>Method.</b> The run is cut into segments of <see cref="RmsErrorControlOptions.SegmentSeconds"/>, plus a segment
/// start at every event time (so every event lands exactly on its time). Each segment s gets a step
/// <c>dt_s = len_s / 2^L_s</c>, starting with the smallest L_s that keeps dt_s at or below the largest step. Each pass runs
/// the schedule S and the schedule S/2 (every step halved). The sample times of S are also sample times of S/2, bit for bit
/// (<c>(2j)(dt/2) = j dt</c> exactly in binary floating point). At every sample of S the difference of every recorded state
/// is scaled: the machine angles by <c>atol + rtol x 1 rad</c> (an angle has no natural size); every other quantity by
/// <c>atol + rtol max(|x_S|, |x_S/2|)</c>: each machine's speed and four flux states, each converter's terminal voltage,
/// P, Q, active and reactive current (and the regulated bus voltage of a plant controller), and every recorded state of
/// each network device (SVC, STATCOM, HVDC). Exciter, governor and dynamic-load internal states are not recorded by the
/// kernel, so they count only through the machine and converter quantities they drive. A segment whose largest scaled
/// difference exceeds <see cref="RmsErrorControlOptions.SafetyFactor"/> has its step halved, and the next pass runs again.
/// The loop stops when no segment exceeds it (the tolerance is met), when every segment that exceeds it is already at the
/// smallest step, or after <see cref="RmsErrorControlOptions.MaxPasses"/>.</para>
///
/// <para><b>What the error means.</b> The difference is measured on the global trajectory, so it includes error carried in
/// from earlier segments; the control is on the trajectory a user reads, not on a per-step local error. The returned run
/// is S/2. For a method of order p in its asymptotic range, the error of S/2 is about <c>|x_S - x_S/2| / (2^p - 1)</c>,
/// which is at most the measured difference for p of 1 or more; the kernel's splitting (exact zero-order hold of the flux
/// lags with start-of-step inputs, RK4 for the swing) is first order overall. That is an estimate, not a bound: outside the
/// asymptotic range (a loss of synchronism, a limit hit at a different step) it can be wrong, and the result says when the
/// tolerance was not met.</para>
///
/// <para>Cost: each pass is two full runs, and a refinement never coarsens a segment again. Deterministic: the same inputs
/// give the same schedule and the same bits.</para>
/// </summary>
public static class RmsErrorControl
{
    /// <summary>2.1.126: the status line a result carries wherever the error-controlled step was used.</summary>
    public const string ExperimentalNote = "EXPERIMENTAL: the error-controlled step is opt-in and not part of the verified contract; its error figure is a first-order step-doubling estimate, not a bound, and a run can cost up to 2 x maxPasses full simulations (24 with the defaults)";

    /// <summary>
    /// Run <paramref name="run"/> under error control over <paramref name="durationSeconds"/>, with segments starting at
    /// every time in <paramref name="eventSeconds"/> (after 0, before the end). <paramref name="machinesOf"/> gives a run's
    /// machine traces.
    /// </summary>
    public static RmsErrorControlResult<T> Run<T>(Func<RmsStepSchedule, T> run, Func<T, MultiMachineSubtransient.Result> machinesOf,
        double durationSeconds, IReadOnlyList<double> eventSeconds, RmsErrorControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(machinesOf);
        ArgumentNullException.ThrowIfNull(eventSeconds);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!(durationSeconds > 0.0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "The duration must be a finite number of seconds above 0.");

        double[] bounds = Boundaries(durationSeconds, eventSeconds, options.SegmentSeconds);
        int segs = bounds.Length - 1;
        var level = new int[segs];
        for (int s = 0; s < segs; s++)
        {
            double len = bounds[s + 1] - bounds[s];
            while (Math.ScaleB(len, -level[s]) > options.MaxStepSeconds) level[s]++;
        }

        int passes = 0, runs = 0;
        while (true)
        {
            passes++;
            RmsStepSchedule coarse = Schedule(bounds, level, 0);
            RmsStepSchedule fine = Schedule(bounds, level, 1);
            T coarseRun = run(coarse);
            T fineRun = run(fine);
            runs += 2;
            double[] err = SegmentErrors(machinesOf(coarseRun), machinesOf(fineRun), coarse, fine, durationSeconds, options);

            double worst = 0.0;
            bool anyOver = false, refined = false;
            for (int s = 0; s < segs; s++)
            {
                if (!(err[s] <= worst)) worst = err[s];
                if (err[s] <= options.SafetyFactor) continue;
                anyOver = true;
                double len = bounds[s + 1] - bounds[s];
                // The returned run uses half the coarse step, so a refined coarse step must stay at or above 2 x the smallest step.
                if (Math.ScaleB(len, -(level[s] + 1)) >= 2.0 * options.MinStepSeconds) { level[s]++; refined = true; }
            }
            if (!anyOver || !refined || passes >= options.MaxPasses)
            {
                var report = new List<RmsErrorControlSegment>(segs);
                for (int s = 0; s < segs; s++)
                {
                    // The levels of the segments refined after this pass's runs are reported as they were run.
                    double dtRun = fine.Windows[s].StepSeconds;
                    report.Add(new RmsErrorControlSegment(bounds[s], bounds[s + 1], dtRun, err[s]));
                }
                string? note = !anyOver ? null
                    : !refined ? $"the tolerance is not met: {CountOver(err, options.SafetyFactor)} segment(s) still exceed it at the smallest step ({Num(options.MinStepSeconds)} s)"
                    : $"the tolerance is not met after {passes} passes (the pass cap)";
                return new RmsErrorControlResult<T>(fineRun, fine, !anyOver, worst, passes, runs, report, note) { CoarseRun = coarseRun };
            }
        }
    }

    private static int CountOver(double[] err, double safety)
    {
        int c = 0;
        foreach (double e in err) if (!(e <= safety)) c++;
        return c;
    }

    /// <summary>The segment boundaries: 0, every multiple of the segment length below the end, every event time after 0
    /// and before the end, and the end; sorted, with a grid point within a millionth of a segment of an event time dropped
    /// (the event time is kept).</summary>
    internal static double[] Boundaries(double durationSeconds, IReadOnlyList<double> eventSeconds, double segmentSeconds)
    {
        double tol = segmentSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        var events = new List<double>();
        foreach (double e in eventSeconds)
        {
            if (!double.IsFinite(e)) throw new ArgumentOutOfRangeException(nameof(eventSeconds), e, "An event time must be finite.");
            if (e > tol && e < durationSeconds - tol && !events.Contains(e)) events.Add(e);
        }
        events.Sort();
        var all = new List<double> { 0.0 };
        for (long k = 1; ; k++)
        {
            double g = k * segmentSeconds;
            if (!(g < durationSeconds - tol)) break;
            bool nearEvent = false;
            foreach (double e in events) if (Math.Abs(e - g) <= tol) nearEvent = true;
            if (!nearEvent) all.Add(g);
        }
        all.AddRange(events);
        all.Sort();
        // Two event times closer than the tolerance: keep the first (the kernel lands both on that sample).
        var kept = new List<double>(all.Count + 1);
        foreach (double b in all) if (kept.Count == 0 || b - kept[^1] > tol) kept.Add(b);
        kept.Add(durationSeconds);
        return kept.ToArray();
    }

    /// <summary>The schedule with segment s stepping by <c>len_s / 2^(level_s + extra)</c> (exact: a power-of-two scaling).</summary>
    internal static RmsStepSchedule Schedule(double[] bounds, int[] level, int extra)
    {
        var w = new RmsStepWindow[level.Length];
        for (int s = 0; s < level.Length; s++)
            w[s] = new RmsStepWindow(bounds[s], Math.ScaleB(bounds[s + 1] - bounds[s], -(level[s] + extra)));
        return new RmsStepSchedule(w);
    }

    /// <summary>The largest scaled difference per segment between the coarse and the fine run, at every sample of the coarse
    /// run (a sample belongs to the segment of the step that produced it; the t = 0 sample is the shared start).</summary>
    private static double[] SegmentErrors(MultiMachineSubtransient.Result coarse, MultiMachineSubtransient.Result fine,
        RmsStepSchedule coarseSchedule, RmsStepSchedule fineSchedule, double durationSeconds, RmsErrorControlOptions o)
    {
        (double[] tc, _) = coarseSchedule.Expand(durationSeconds);
        (double[] tf, _) = fineSchedule.Expand(durationSeconds);
        int segs = coarseSchedule.Windows.Count;
        var err = new double[segs];
        int n = coarse.Machines.Count;
        if (fine.Machines.Count != n) throw new InvalidOperationException("internal error: the two runs have different fleets");
        int coarseSamples = n > 0 ? coarse.Machines[0].TimeSeconds.Count : 0;
        int fineSamples = n > 0 ? fine.Machines[0].TimeSeconds.Count : 0;
        int f = 0, seg = 0;
        double angleScale = o.AbsoluteTolerance + o.RelativeTolerance * 1.0;
        for (int k = 1; k < tc.Length && k < coarseSamples; k++)
        {
            // The segment of the step that produced sample k: the window holding the step start tc[k - 1].
            while (seg + 1 < segs && tc[k - 1] >= coarseSchedule.Windows[seg + 1].FromSeconds) seg++;
            while (f < tf.Length && tf[f] != tc[k]) f++;
            if (f >= tf.Length || f >= fineSamples)
            {
                // The fine run ended earlier or the grids do not meet: no comparison is possible, so the segment fails.
                err[seg] = double.PositiveInfinity;
                break;
            }
            for (int i = 0; i < n; i++)
            {
                MultiMachineSubtransient.MachineTrace a = coarse.Machines[i], b = fine.Machines[i];
                double e = Math.Abs(a.DeltaRad[k] - b.DeltaRad[f]) / angleScale;
                e = Math.Max(e, Scaled(a.SpeedPu[k], b.SpeedPu[f], o));
                e = Math.Max(e, Scaled(a.EqpPu[k], b.EqpPu[f], o));
                e = Math.Max(e, Scaled(a.EdpPu[k], b.EdpPu[f], o));
                e = Math.Max(e, Scaled(a.EqppPu[k], b.EqppPu[f], o));
                e = Math.Max(e, Scaled(a.EddpPu[k], b.EddpPu[f], o));
                if (!double.IsFinite(e)) e = double.PositiveInfinity;   // a non-finite state fails its segment
                if (!(e <= err[seg])) err[seg] = e;
            }
            // 2.1.126: every other recorded state too (converters and network devices), in fleet order.
            double other = OtherStates(coarse, fine, k, f, coarseSamples, fineSamples, o);
            if (!(other <= err[seg])) err[seg] = other;
        }
        return err;
    }

    /// <summary>The largest scaled difference at coarse sample <paramref name="k"/> and fine sample <paramref name="f"/> of the
    /// converter traces (V, P, Q, Ip, Iq and a plant controller's regulated voltage) and the network device states. A trace
    /// whose length is not the run's sample count, or a fleet that differs between the runs, cannot be compared, so it gives
    /// +infinity (the segment fails and the result says so).</summary>
    private static double OtherStates(MultiMachineSubtransient.Result coarse, MultiMachineSubtransient.Result fine, int k, int f,
        int coarseSamples, int fineSamples, RmsErrorControlOptions o)
    {
        double e = 0.0;
        IReadOnlyList<RmsConverterTrace> cc = coarse.Converters ?? Array.Empty<RmsConverterTrace>(), cf = fine.Converters ?? Array.Empty<RmsConverterTrace>();
        if (cc.Count != cf.Count) return double.PositiveInfinity;
        for (int i = 0; i < cc.Count; i++)
        {
            RmsConverterTrace a = cc[i], b = cf[i];
            e = Math.Max(e, Series(a.TerminalVoltagePu, b.TerminalVoltagePu));
            e = Math.Max(e, Series(a.ActivePowerPu, b.ActivePowerPu));
            e = Math.Max(e, Series(a.ReactivePowerPu, b.ReactivePowerPu));
            e = Math.Max(e, Series(a.ActiveCurrentPu, b.ActiveCurrentPu));
            e = Math.Max(e, Series(a.ReactiveCurrentPu, b.ReactiveCurrentPu));
            if (a.RegulatedBusVoltagePu is not null || b.RegulatedBusVoltagePu is not null)
                e = a.RegulatedBusVoltagePu is null || b.RegulatedBusVoltagePu is null ? double.PositiveInfinity
                    : Math.Max(e, Series(a.RegulatedBusVoltagePu, b.RegulatedBusVoltagePu));
        }
        IReadOnlyList<RmsDeviceTrace> dc = coarse.Devices ?? Array.Empty<RmsDeviceTrace>(), df = fine.Devices ?? Array.Empty<RmsDeviceTrace>();
        if (dc.Count != df.Count) return double.PositiveInfinity;
        for (int i = 0; i < dc.Count; i++)
        {
            IReadOnlyList<double[]> a = dc[i].Samples, b = df[i].Samples;
            if (a.Count != coarseSamples || b.Count != fineSamples || a[k].Length != b[f].Length) return double.PositiveInfinity;
            for (int j = 0; j < a[k].Length; j++) e = Math.Max(e, Scaled(a[k][j], b[f][j], o));
        }
        return double.IsFinite(e) ? e : double.PositiveInfinity;

        double Series(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
            a.Count != coarseSamples || b.Count != fineSamples ? double.PositiveInfinity : Scaled(a[k], b[f], o);
    }

    private static double Scaled(double a, double b, RmsErrorControlOptions o)
        => Math.Abs(a - b) / (o.AbsoluteTolerance + o.RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

public static partial class RmsTransientStability
{
    /// <summary>
    /// WP21 (RC5): <see cref="StudyEventsObserved"/> with the error-controlled step (<see cref="RmsErrorControl"/>): segments
    /// start at every event time, so every event lands exactly on its requested time. The returned run is an ordinary
    /// step-schedule run; re-running its schedule with <see cref="StudyEventsObservedScheduled"/> gives the same bits.
    /// </summary>
    public static RmsErrorControlResult<ObservedStudy> StudyEventsObservedErrorControlled(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, IReadOnlyList<int> measuredBusIds, RmsErrorControlOptions options,
        Func<IReadOnlyList<RmsExciter?>?>? freshExciters = null,
        double? f0 = null, double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad)
    {
        ArgumentNullException.ThrowIfNull(events);
        var times = new List<double>(events.Count);
        foreach (RmsEvent e in events) if (e is not null) times.Add(e.AtSeconds);
        // Each run needs its own exciters (they are stateful): the caller's factory, called once per run.
        return RmsErrorControl.Run(
            s => StudyEventsObservedScheduled(model, result, data, events, measuredBusIds, s, f0, durationSeconds, slipLimitRad, freshExciters?.Invoke()),
            o => o.Result, durationSeconds, times, options);
    }

    /// <summary>WP21 (RC5): <see cref="StudyEvents"/> with the error-controlled step (see
    /// <see cref="StudyEventsObservedErrorControlled"/>).</summary>
    public static RmsErrorControlResult<MultiMachineSubtransient.Result> StudyEventsErrorControlled(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, RmsErrorControlOptions options,
        Func<IReadOnlyList<RmsExciter?>?>? freshExciters = null,
        double? f0 = null, double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad)
    {
        ArgumentNullException.ThrowIfNull(events);
        var times = new List<double>(events.Count);
        foreach (RmsEvent e in events) if (e is not null) times.Add(e.AtSeconds);
        return RmsErrorControl.Run(
            s => StudyEventsScheduled(model, result, data, events, s, f0, durationSeconds, slipLimitRad, freshExciters?.Invoke()),
            r => r, durationSeconds, times, options);
    }
}
