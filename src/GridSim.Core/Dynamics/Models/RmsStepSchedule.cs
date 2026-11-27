using System;
using System.Collections.Generic;
using System.Globalization;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP21 (RC5): one window of a step schedule: from <see cref="FromSeconds"/> on, the full RMS model steps by
/// <see cref="StepSeconds"/>, up to the next window's start or the end of the run.
/// </summary>
/// <param name="FromSeconds">The window's start (s). The first window starts at 0.</param>
/// <param name="StepSeconds">The fixed step inside the window (s), finite and above 0.</param>
public sealed record RmsStepWindow(double FromSeconds, double StepSeconds);

/// <summary>
/// WP21 (RC5): a mechanical-power hook that takes the step on each call. On a step-schedule run the kernel calls
/// <see cref="UseStep"/> with the step in force before the step's mechanical-power calls.
/// </summary>
internal interface IRmsStepSizedHook
{
    /// <summary>The step (s) the next mechanical-power calls advance their governors by.</summary>
    void UseStep(double stepSeconds);
}

/// <summary>
/// WP21 (RC5): a user-chosen step schedule for the full RMS model (<see cref="MultiMachineSubtransient"/>). The run takes
/// fixed steps inside each window, so a fine step can cover a fault and a coarse one the slow recovery after it.
///
/// <para><b>Rules, all checked when the schedule is built for a run (<see cref="Expand"/>):</b></para>
/// <list type="bullet">
/// <item>At least one window; the first starts at 0; starts rise strictly; every step is finite and above 0.</item>
/// <item>Each window holds a whole number of its own steps (at least one): its length divided by its step is within
/// <see cref="MultiMachineSubtransient.StageTimeToleranceSteps"/> of an integer, the same tolerance the stage times use. So
/// every window boundary lies on the step grid of the window before it.</item>
/// <item>The step at sample j of window w starts at <c>from_w + j dt_w</c>; the last sample is the end of the run.</item>
/// <item>An event (a network stage or a machine trip) strictly inside a COARSE window (one whose step is larger than the
/// schedule's smallest) is refused: start a window at the event. Inside a window with the smallest step the event lands
/// by the kernel's usual stage rule, so a one-window schedule runs exactly as the plain fixed step.</item>
/// </list>
/// <para>Every model the kernel steps takes the step as an argument on each call; the step is never stored at
/// construction, so a change of step between windows needs no extra handling (checked class by class; see the
/// technical chapter). The units' own relays are built for one fixed step and refuse any schedule. The fuller composite
/// load's timers count whole steps of one size, and GridSim cannot check that a user or plug-in model takes the step on
/// each call, so those refuse a schedule with more than one step size.</para>
/// </summary>
public sealed class RmsStepSchedule
{
    /// <summary>The windows, in time order.</summary>
    public IReadOnlyList<RmsStepWindow> Windows { get; }

    /// <summary>Build a schedule from its windows. The shape rules that do not depend on the run's length are checked
    /// here (see the class remarks); the rest when the schedule is expanded for a run.</summary>
    public RmsStepSchedule(IReadOnlyList<RmsStepWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) throw new ArgumentException("A step schedule needs at least one window.", nameof(windows));
        for (int w = 0; w < windows.Count; w++)
        {
            RmsStepWindow? x = windows[w] ?? throw new ArgumentException($"Step schedule window {w} is null.", nameof(windows));
            if (!(x.StepSeconds > 0.0) || !double.IsFinite(x.StepSeconds))
                throw new ArgumentOutOfRangeException(nameof(windows), x.StepSeconds, $"Step schedule window {w}: the step must be a finite number of seconds above 0.");
            if (!double.IsFinite(x.FromSeconds))
                throw new ArgumentOutOfRangeException(nameof(windows), x.FromSeconds, $"Step schedule window {w}: the start must be finite.");
            if (w == 0 && x.FromSeconds != 0.0)
                throw new ArgumentException($"Step schedule window 0 starts at {Num(x.FromSeconds)} s; the first window must start at 0.", nameof(windows));
            if (w > 0 && !(x.FromSeconds > windows[w - 1].FromSeconds))
                throw new ArgumentException($"Step schedule window {w} starts at {Num(x.FromSeconds)} s, not after window {w - 1} ({Num(windows[w - 1].FromSeconds)} s).", nameof(windows));
        }
        var copy = new RmsStepWindow[windows.Count];
        for (int w = 0; w < copy.Length; w++) copy[w] = windows[w];
        Windows = copy;
    }

    /// <summary>The smallest step of the schedule (s).</summary>
    public double SmallestStepSeconds
    {
        get
        {
            double m = double.PositiveInfinity;
            foreach (RmsStepWindow w in Windows) m = Math.Min(m, w.StepSeconds);
            return m;
        }
    }

    /// <summary>The largest step of the schedule (s).</summary>
    public double LargestStepSeconds
    {
        get
        {
            double m = 0.0;
            foreach (RmsStepWindow w in Windows) m = Math.Max(m, w.StepSeconds);
            return m;
        }
    }

    /// <summary>True when every window has the same step (the run is a plain fixed-step run on that step).</summary>
    public bool IsUniform
    {
        get
        {
            foreach (RmsStepWindow w in Windows) if (w.StepSeconds != Windows[0].StepSeconds) return false;
            return true;
        }
    }

    /// <summary>A schedule of one window: the plain fixed step <paramref name="stepSeconds"/>.</summary>
    public static RmsStepSchedule Fixed(double stepSeconds) => new(new[] { new RmsStepWindow(0.0, stepSeconds) });

    /// <summary>
    /// The sample times and steps of a run of <paramref name="durationSeconds"/>: <c>Times[k]</c> is the start of step k
    /// (<c>Times.Length = Steps.Length + 1</c>, the last entry the end of the run) and <c>Steps[k]</c> its length. A window
    /// that starts at or after the end of the run is refused, as is a window that is not a whole number of its steps.
    /// Each window's sample times are <c>from + j dt</c> (for the first window, exactly the plain kernel's <c>k dt</c>);
    /// the number of steps of the last window is <c>round((duration - from) / dt)</c>, the plain kernel's rule.
    /// </summary>
    public (double[] Times, double[] Steps) Expand(double durationSeconds)
    {
        if (!(durationSeconds >= 0.0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "the duration must be a finite number of seconds, 0 or more.");
        var times = new List<double>();
        var steps = new List<double>();
        for (int w = 0; w < Windows.Count; w++)
        {
            RmsStepWindow x = Windows[w];
            double dt = x.StepSeconds;
            bool last = w + 1 == Windows.Count;
            double end = last ? durationSeconds : Windows[w + 1].FromSeconds;
            if (!last && !(Windows[w + 1].FromSeconds < durationSeconds + dt * MultiMachineSubtransient.StageTimeToleranceSteps))
                throw new ArgumentException($"Step schedule window {w + 1} starts at {Num(Windows[w + 1].FromSeconds)} s, at or after the end of the run ({Num(durationSeconds)} s).");
            double ratio = (end - x.FromSeconds) / dt;
            double whole = Math.Round(ratio);
            if (last && w > 0 && !(x.FromSeconds < durationSeconds))
                throw new ArgumentException($"Step schedule window {w} starts at {Num(x.FromSeconds)} s, at or after the end of the run ({Num(durationSeconds)} s).");
            if (!last || w > 0)
            {
                if (Math.Abs(ratio - whole) > MultiMachineSubtransient.StageTimeToleranceSteps || whole < 1.0)
                    throw new ArgumentException($"Step schedule window {w} ({Num(x.FromSeconds)} s to {Num(end)} s) is not a whole number of "
                        + $"its {Num(dt)} s steps ({ratio.ToString("R", CultureInfo.InvariantCulture)}); a window boundary must lie on the step grid of the window before it.");
            }
            // DYN-10: a window of more steps than the run cap is refused (the cast wrapped to a negative count).
            if (!(whole <= MultiMachineSubtransient.MaxRunSteps))
                throw new ArgumentException(FormattableString.Invariant($"Step schedule window {w} holds {whole:R} steps of {Num(dt)} s, more than the {MultiMachineSubtransient.MaxRunSteps} a run may take."));
            int count = (int)whole;
            // DYN-09: a one-window schedule over a run shorter than half its step expanded to no step at all, and the kernel
            // then read the step at index -1.
            if (last && w == 0 && count == 0 && durationSeconds > 0)
                throw new ArgumentException($"Step schedule: the run ({Num(durationSeconds)} s) is shorter than half of its {Num(dt)} s step, so it holds no step; use a shorter step or a longer run.");
            for (int j = 0; j < count; j++)
            {
                times.Add(x.FromSeconds + j * dt);
                steps.Add(dt);
            }
            if (last) times.Add(x.FromSeconds + count * dt);
        }
        return (times.ToArray(), steps.ToArray());
    }

    /// <summary>
    /// Refuse an event at <paramref name="atSeconds"/> that lies strictly inside a coarse window (see the class remarks).
    /// <paramref name="what"/> names the event in the message.
    /// </summary>
    public void RequireEventPlacement(double atSeconds, string what)
    {
        double smallest = SmallestStepSeconds;
        for (int w = 0; w < Windows.Count; w++)
        {
            RmsStepWindow x = Windows[w];
            double eps = x.StepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
            double next = w + 1 < Windows.Count ? Windows[w + 1].FromSeconds : double.PositiveInfinity;
            if (!(atSeconds + eps >= x.FromSeconds) || !(atSeconds + eps < next)) continue;
            bool atStart = Math.Abs(atSeconds - x.FromSeconds) <= eps;
            if (!atStart && x.StepSeconds > smallest)
                throw new ArgumentException($"{what} at {Num(atSeconds)} s lies inside the coarse window from {Num(x.FromSeconds)} s "
                    + $"(step {Num(x.StepSeconds)} s); start a window at the event, or use the schedule's smallest step there.");
            return;
        }
    }

    /// <summary>The sample time the kernel applies an event at <paramref name="atSeconds"/>: the first step start t with
    /// <c>t + dt 1e-6 &gt;= atSeconds</c>, dt that step's own length (the kernel's stage rule per step); the end of the
    /// run when no step start reaches it. Returns the step index and its time.</summary>
    public static (int Step, double TimeSeconds) AppliedAt(double[] times, double[] steps, double atSeconds)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(steps);
        for (int k = 0; k < times.Length; k++)
        {
            double dt = k < steps.Length ? steps[k] : steps[^1];
            if (times[k] + dt * MultiMachineSubtransient.StageTimeToleranceSteps >= atSeconds) return (k, times[k]);
        }
        return (times.Length - 1, times[^1]);
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
