using System;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>A located event: the bracket <c>[Low, High]</c> that contains the crossing, and the bisections used.</summary>
public readonly record struct LocatedEvent(double LowSeconds, double HighSeconds, int Bisections)
{
    /// <summary>The time the event is applied at: the high end of the bracket, so the crossing has certainly happened and the
    /// error is at most the bracket width (never early).</summary>
    public double AppliedSeconds => HighSeconds;
}

/// <summary>
/// RC4 R4-14: locates the time at which a condition first holds inside one accepted step, by bisection to a stated
/// time tolerance. The caller supplies <c>holdsAt(t)</c>, which must be a pure function of <c>t</c> within the step
/// (the relay-in-loop engine evaluates it by re-integrating from the step start with the step's frozen modes). The
/// number of bisections depends only on the bracket and the tolerance (and the condition's own answers), so the
/// location is deterministic.
/// </summary>
public static class EventLocator
{
    /// <summary>Bisect <c>[t0, t1]</c> (the condition is false at <paramref name="t0"/> and true at <paramref name="t1"/>)
    /// until the bracket is no wider than <paramref name="toleranceSeconds"/>.</summary>
    public static LocatedEvent Locate(double t0, double t1, double toleranceSeconds, Func<double, bool> holdsAt)
    {
        ArgumentNullException.ThrowIfNull(holdsAt);
        if (!(toleranceSeconds > 0.0) || !double.IsFinite(toleranceSeconds))
            throw new ArgumentOutOfRangeException(nameof(toleranceSeconds), toleranceSeconds, "The event time tolerance must be positive and finite.");
        if (!(t1 > t0) || !double.IsFinite(t0) || !double.IsFinite(t1))
            throw new ArgumentException("The bracket must be finite with t1 > t0.");
        double lo = t0, hi = t1;
        int n = 0;
        while (hi - lo > toleranceSeconds && n < 200)
        {
            double mid = lo + 0.5 * (hi - lo);
            if (!(mid > lo) || !(mid < hi)) break;   // the bracket cannot be halved in double precision
            if (holdsAt(mid)) hi = mid; else lo = mid;
            n++;
        }
        return new LocatedEvent(lo, hi, n);
    }
}
