using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The voltage angle of a set of buses through an RMS run, one sample per integration step (inertia placement study,
/// October 2026). Recorded by <see cref="RmsTransientStability.StudyEventsMeasured"/> from the same committed network
/// solve the machines integrate against; reading it changes no number of the run.
/// </summary>
/// <param name="BusIds">The measured buses, in the order of <paramref name="AngleRad"/>.</param>
/// <param name="TimeSeconds">The sample times (one per integration step, step k at k times the step).</param>
/// <param name="AngleRad">Per bus, the unwrapped voltage angle (rad, network frame rotating at the nominal frequency) at
/// each sample: <c>AngleRad[bus][sample]</c>.</param>
public sealed record RmsBusTrace(IReadOnlyList<int> BusIds, IReadOnlyList<double> TimeSeconds, double[][] AngleRad);

/// <summary>Per-bus frequency measures of one run (see <see cref="BusFrequencyMeter.Measure"/>).</summary>
/// <param name="BusIds">The measured buses, in the order of every array here.</param>
/// <param name="FrequencyHz">Per bus, the one-cycle frequency estimate at each sample (phase jumps removed).</param>
/// <param name="WorstRocofHzPerS">Per bus, the largest magnitude of the windowed RoCoF over the run (Hz/s).</param>
/// <param name="AngleJumpDeg">Per bus, the largest magnitude of the voltage angle jump at an event (degrees).</param>
public sealed record BusFrequencyResult(
    IReadOnlyList<int> BusIds, double[][] FrequencyHz, double[] WorstRocofHzPerS, double[] AngleJumpDeg);

/// <summary>
/// Bus frequency, RoCoF and angle jump from recorded bus voltage angles, the way a relay at that bus would see them
/// (inertia placement study, October 2026).
///
/// <para><b>Angle jump.</b> At an event the network changes between two samples while the machine EMFs cannot, so a bus
/// voltage angle steps. The jump at an event at sample <c>ke</c> is the angle change across the event less the change
/// over the step before it: <c>(u[ke] - u[ke-1]) - (u[ke-1] - u[ke-2])</c>, so the steady drift of the angle is not
/// counted as a jump. The jump is then taken out of the angle (subtracted from every later sample) before the frequency
/// is formed, so the frequency and RoCoF below are those of the rotor-driven motion, and the jump is reported on its
/// own. (A real loss-of-mains relay sees both together; the two are kept apart here so each can be judged against its
/// own limit.)</para>
///
/// <para><b>Frequency.</b> <c>f[k] = f0 + (u[k] - u[k - nc]) / (2 pi nc dt)</c> over about one nominal cycle (<c>nc</c> is
/// the whole number of steps nearest <c>1/(f0 dt)</c>, at least one: the shortest window a zero-crossing or phasor
/// measurement uses; 4 steps of 5 ms at 50 Hz, exactly one cycle); before the first full window the first full estimate
/// is held, so a bus that starts off nominal does not read a step from nominal.</para>
///
/// <para><b>RoCoF.</b> <c>(f[k] - f[k - nw]) / (nw dt)</c> over a sliding window (<c>nw dt</c> = 500 ms by default, the
/// window GB loss-of-mains protection measures RoCoF over under the Distribution Code / G99), and the worst magnitude of
/// it over the run. The same window applied to a centre-of-inertia frequency gives the system figure the bus figures are
/// compared with.</para>
///
/// Pure arithmetic in one fixed order: the same input gives the same bits on every CPU and operating system.
/// </summary>
public static class BusFrequencyMeter
{
    /// <summary>The RoCoF window (s) GB loss-of-mains protection uses (Distribution Code / EREC G99): 500 ms.</summary>
    public const double DefaultRocofWindowSeconds = 0.5;

    /// <summary>Measure every bus of <paramref name="trace"/>. <paramref name="eventSeconds"/> are the event times of the
    /// run (any order); <paramref name="stepSeconds"/> is the integration step, which both windows must be whole multiples
    /// of (refused otherwise: a window that is not a whole number of samples would be silently rounded).</summary>
    public static BusFrequencyResult Measure(
        RmsBusTrace trace, double nominalHz, double stepSeconds, IReadOnlyList<double> eventSeconds,
        double rocofWindowSeconds = DefaultRocofWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(eventSeconds);
        if (!(nominalHz > 0.0) || !double.IsFinite(nominalHz))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz, "The nominal frequency must be a finite number of Hz above 0.");
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "The step must be a finite number of seconds above 0.");
        int nc = Math.Max(1, (int)Math.Round(1.0 / (nominalHz * stepSeconds)));
        int nw = WholeSamples(rocofWindowSeconds, stepSeconds, "the RoCoF window");
        int samples = trace.TimeSeconds.Count;
        int buses = trace.BusIds.Count;
        if (trace.AngleRad.Length != buses)
            throw new ArgumentException($"The trace has {buses} buses but {trace.AngleRad.Length} angle series.", nameof(trace));

        // The sample at which each event takes effect: the first whose time has reached it, with the RMS kernel's
        // millionth-of-a-step tolerance (MultiMachineSubtransient.StageTimeToleranceSteps), so the measure reads the
        // event at exactly the step the network changed.
        double eps = stepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        var eventSample = new List<int>();
        var sortedEvents = new List<double>(eventSeconds);
        sortedEvents.Sort();
        foreach (double te in sortedEvents)
        {
            int ke = -1;
            for (int k = 0; k < samples; k++) if (trace.TimeSeconds[k] + eps >= te) { ke = k; break; }
            // The angle step of an event is the change in slope across it, (u[ke] - u[ke-1]) - (u[ke-1] - u[ke-2]), which
            // needs TWO samples before it. An event at sample 0 changed the network before the trace starts, so there
            // is no step inside the trace to remove. An event at sample 1 has only one sample before it: its step cannot
            // be separated from the frequency, and it used to be dropped silently, so the switching step was read as a
            // frequency spike and a worst RoCoF. It is refused by name instead.
            if (ke == 1)
                throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"An event at t = {te:R} s takes effect at the trace's second sample; the bus frequency measure needs two samples before an event to separate its angle step from the frequency. Start the event at least two steps into the run."),
                    nameof(eventSeconds));
            if (ke >= 2 && !eventSample.Contains(ke)) eventSample.Add(ke);
        }

        var freq = new double[buses][];
        var worst = new double[buses];
        var jump = new double[buses];
        var u = new double[samples];
        for (int b = 0; b < buses; b++)
        {
            double[] a = trace.AngleRad[b];
            if (a.Length != samples)
                throw new ArgumentException($"Bus {trace.BusIds[b]} has {a.Length} angle samples; the trace has {samples} times.", nameof(trace));
            Array.Copy(a, u, samples);
            double bigJump = 0.0;
            foreach (int ke in eventSample)
            {
                double j = (u[ke] - u[ke - 1]) - (u[ke - 1] - u[ke - 2]);
                if (Math.Abs(j) > Math.Abs(bigJump)) bigJump = j;
                for (int k = ke; k < samples; k++) u[k] -= j;
            }
            jump[b] = Math.Abs(bigJump) * 180.0 / Math.PI;
            double[] f = FrequencyFromAngle(u, nominalHz, nc, stepSeconds);
            freq[b] = f;
            worst[b] = WorstWindowedRocof(f, nw, stepSeconds);
        }
        return new BusFrequencyResult(trace.BusIds, freq, worst, jump);
    }

    /// <summary>The one-cycle frequency estimate of an (unwrapped, jump-free) angle series:
    /// <c>f0 + (u[k] - u[k - nc]) / (2 pi nc dt)</c>, with the first full estimate held over the first
    /// <paramref name="cycleSamples"/> samples (<c>f0</c> when the series is no longer than that).</summary>
    public static double[] FrequencyFromAngle(double[] angleRad, double nominalHz, int cycleSamples, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(angleRad);
        if (cycleSamples < 1) throw new ArgumentOutOfRangeException(nameof(cycleSamples), cycleSamples, "The cycle must be at least one sample.");
        var f = new double[angleRad.Length];
        double span = 2.0 * Math.PI * cycleSamples * stepSeconds;
        for (int k = cycleSamples; k < f.Length; k++)
            f[k] = nominalHz + (angleRad[k] - angleRad[k - cycleSamples]) / span;
        double first = f.Length > cycleSamples ? f[cycleSamples] : nominalHz;
        for (int k = 0; k < cycleSamples && k < f.Length; k++) f[k] = first;
        return f;
    }

    /// <summary>The largest magnitude of <c>(f[k] - f[k - nw]) / (nw dt)</c> over the series (0 for a series shorter than
    /// the window).</summary>
    public static double WorstWindowedRocof(double[] frequencyHz, int windowSamples, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(frequencyHz);
        if (windowSamples < 1) throw new ArgumentOutOfRangeException(nameof(windowSamples), windowSamples, "The window must be at least one sample.");
        double span = windowSamples * stepSeconds;
        double worst = 0.0;
        for (int k = windowSamples; k < frequencyHz.Length; k++)
        {
            double r = Math.Abs(frequencyHz[k] - frequencyHz[k - windowSamples]) / span;
            if (!(r <= worst)) worst = r;   // NaN-safe: a NaN sample makes the result NaN, never hides it
        }
        return worst;
    }

    /// <summary>A window as a whole number of samples; refused when it is not one (to a millionth of a step).</summary>
    public static int WholeSamples(double windowSeconds, double stepSeconds, string what)
    {
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "The step must be a finite number of seconds above 0.");
        if (!(windowSeconds > 0.0) || !double.IsFinite(windowSeconds))
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), windowSeconds, $"{what} must be a finite number of seconds above 0.");
        double ratio = windowSeconds / stepSeconds;
        int n = (int)Math.Round(ratio);
        if (n < 1 || Math.Abs(ratio - n) > 1e-6)
            throw new ArgumentException($"{what} ({windowSeconds} s) is not a whole number of {stepSeconds} s steps.", nameof(windowSeconds));
        return n;
    }

    /// <summary>Unwrap one bus's angle in place, sample by sample: each step's change is brought into (-pi, pi] and
    /// accumulated, so a voltage angle that crosses +/-180 degrees reads as a continuous angle.</summary>
    public static void Unwrap(double[] angleRad)
    {
        ArgumentNullException.ThrowIfNull(angleRad);
        for (int k = 1; k < angleRad.Length; k++)
        {
            double d = angleRad[k] - angleRad[k - 1];
            double raw = d;
            while (d > Math.PI) d -= 2.0 * Math.PI;
            while (d <= -Math.PI) d += 2.0 * Math.PI;
            if (d != raw) angleRad[k] = angleRad[k - 1] + d;
        }
    }

    /// <summary>The phase of a phasor through the deterministic arctangent (<see cref="DetMath.Phase"/>).</summary>
    internal static double PhaseOf(Complex v) => DetMath.Phase(v);
}
