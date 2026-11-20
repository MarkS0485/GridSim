using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>
/// RC4 R4-14: the critical clearing time search of the variable-step path. The fixed-step search is tied to the integration
/// step (each trial clears after a whole number of steps, so its bracket carries step-quantisation notes); here a trial
/// clears at ANY time and the bracket is bisected to a stated time tolerance, so the answer is limited by the tolerance
/// and by the integration error estimate, not by a step grid.
/// <para>
/// The search assumes stability is monotone in the clearing time over the window (stable for short clearing, unstable for
/// long); it does not probe for stable pockets above the first loss (the fixed-step search does, and says so). The
/// number of trials is a function of the window and the tolerance only.
/// </para>
/// </summary>
public static class AdaptiveCct
{
    /// <summary>The search outcome. <see cref="StableSeconds"/> is the longest clearing time seen to be stable,
    /// <see cref="UnstableSeconds"/> the shortest seen to be unstable (null when none); the true critical time lies between them.</summary>
    public sealed record Result(string Disposition, double StableSeconds, double? UnstableSeconds, double ToleranceSeconds, int Trials,
        string Note);

    /// <summary>The window's longest clearing time is stable.</summary>
    public const string StableThroughWindow = "STABLE THROUGH WINDOW";
    /// <summary>Even the shortest clearing time tried (the tolerance) loses synchronism.</summary>
    public const string UnstableAtZero = "UNSTABLE AT ZERO CLEARING";
    /// <summary>Both ends of the bracket were found; its width is at most the tolerance.</summary>
    public const string Bracketed = "CCT BRACKETED";

    /// <summary>Bisect for the critical clearing time in <c>(0, maxClearingSeconds]</c> to <paramref name="toleranceSeconds"/>.</summary>
    public static Result Search(Func<double, bool> isStable, double maxClearingSeconds, double toleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(isStable);
        if (!(maxClearingSeconds > 0.0) || !double.IsFinite(maxClearingSeconds)) throw new ArgumentOutOfRangeException(nameof(maxClearingSeconds));
        if (!(toleranceSeconds > 0.0) || !(toleranceSeconds < maxClearingSeconds)) throw new ArgumentOutOfRangeException(nameof(toleranceSeconds), toleranceSeconds, "The tolerance must be positive and below the window.");
        int trials = 0;
        bool Try(double t) { trials++; return isStable(t); }
        if (Try(maxClearingSeconds))
            return new Result(StableThroughWindow, maxClearingSeconds, null, toleranceSeconds, trials, "no clearing time in the window lost synchronism");
        double lo = toleranceSeconds, hi = maxClearingSeconds;
        if (!Try(lo))
            return new Result(UnstableAtZero, 0.0, lo, toleranceSeconds, trials, "the shortest clearing time tried (the tolerance) is already unstable");
        while (hi - lo > toleranceSeconds)
        {
            double mid = lo + 0.5 * (hi - lo);
            if (Try(mid)) lo = mid; else hi = mid;
        }
        return new Result(Bracketed, lo, hi, toleranceSeconds, trials,
            "bisected on clearing times that are not rounded to a step; monotone stability assumed (no pocket probe)");
    }
}
