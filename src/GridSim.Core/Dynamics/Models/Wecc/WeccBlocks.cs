using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// The block primitives the WECC renewable models are drawn with, beyond <see cref="ControlBlocks"/>: pure static
/// functions of (state, input, step) with no hidden state, so every model built on them is a pure function of its
/// inputs and bit-for-bit reproducible.
///
/// <para><b>Integration rule</b> (shared by every WECC model, the same as the converter model it extends). A step
/// evaluates every block input from the start-of-step states and measured quantities, then advances every state:
/// a first-order lag by the exact zero-order hold of its pole (<see cref="Lag"/>: for an input held across the step
/// <c>x &lt;- u + (x - u) exp(-dt/T)</c> is the closed-form solution, A-stable at any step), an integrator by forward
/// Euler (<see cref="Integrate"/>, exact for a held input). A limit on a state is a non-windup limit: the state itself
/// is clamped, so it leaves the limit on the first step its input reverses. A rate limit bounds the change of the state
/// over the step to rate x dt.</para>
/// </summary>
public static class WeccBlocks
{
    /// <summary>A time constant at or below this (s) is an instantaneous block (the lag output equals its input).</summary>
    public const double InstantSeconds = ControlBlocks.InstantThresholdSeconds;

    /// <summary>Exact zero-order-hold advance of the lag <c>1/(1+sT)</c> from <paramref name="x"/> with input
    /// <paramref name="u"/> held for <paramref name="dt"/>; an instantaneous lag returns <paramref name="u"/>.</summary>
    public static double Lag(double x, double u, double t, double dt)
    {
        if (t <= InstantSeconds) return u;
        double a = DetMath.Exp(-dt / t);
        return u + (x - u) * a;
    }

    /// <summary>Forward-Euler advance of the integrator <c>k/s</c>, clamped to [min, max] (non-windup).</summary>
    public static double Integrate(double x, double k, double u, double dt, double min = double.NegativeInfinity,
        double max = double.PositiveInfinity) => Math.Clamp(x + k * u * dt, min, max);

    /// <summary>Bound the change from <paramref name="previous"/> to <paramref name="next"/> to
    /// [<paramref name="rateDown"/> dt, <paramref name="rateUp"/> dt] (rateDown &lt;= 0 &lt;= rateUp; an infinite rate
    /// places no limit on that side).</summary>
    public static double RateLimit(double previous, double next, double rateUp, double rateDown, double dt)
    {
        double d = next - previous;
        double up = rateUp * dt, down = rateDown * dt;
        if (d > up) return previous + up;
        if (d < down) return previous + down;
        return next;
    }

    /// <summary>
    /// The continuous deadband of the WECC diagrams: zero for <paramref name="u"/> in [dbd1, dbd2], else the excess
    /// beyond the threshold it crossed (<c>u - dbd2</c> above, <c>u - dbd1</c> below). dbd1 &lt;= 0 &lt;= dbd2.
    /// </summary>
    public static double Deadband(double u, double dbd1, double dbd2)
    {
        if (u > dbd2) return u - dbd2;
        if (u < dbd1) return u - dbd1;
        return 0.0;
    }

    /// <summary>
    /// A piecewise-linear table through (x[i], y[i]) with non-decreasing breakpoints, held flat beyond the first and
    /// last points (the VDL tables, the torque speed table, the DER_A tables). Where two breakpoints coincide the curve
    /// steps there and the value AT the breakpoint is the later point's (the right-continuous reading).
    /// </summary>
    public static double Table(IReadOnlyList<double> x, IReadOnlyList<double> y, double u)
    {
        int n = x.Count;
        if (u < x[0]) return y[0];
        // The first breakpoint above u closes the segment u lies in. u >= x[i - 1] there, and x[i] > u >= x[i - 1], so the
        // segment has a positive width. A run of equal breakpoints at u is passed over, so the value is the later point's.
        for (int i = 1; i < n; i++)
            if (u < x[i]) return y[i - 1] + (y[i] - y[i - 1]) * (u - x[i - 1]) / (x[i] - x[i - 1]);
        return y[n - 1];
    }
}

/// <summary>
/// A post-event hold timer (REEC_A Thld / Thld2, the DER_A reconnection delays): armed at a time, it is active while
/// the simulation time is before the arm time plus its duration. Times are compared with a millionth-of-a-step
/// tolerance, the engine's convention for event times (MultiMachineSubtransient.StageTimeToleranceSteps), so a hold of
/// exactly n steps lasts exactly n steps despite the rounding of t = k dt.
/// </summary>
public struct HoldTimer
{
    private double _until;
    private bool _armed;

    /// <summary>Arm the timer at time <paramref name="t"/> for <paramref name="duration"/> seconds.</summary>
    public void Arm(double t, double duration) { _until = t + duration; _armed = true; }

    /// <summary>Disarm.</summary>
    public void Clear() => _armed = false;

    /// <summary>True while armed and <paramref name="t"/> is before the end (with the step tolerance).</summary>
    public readonly bool Active(double t, double dt) =>
        _armed && t + dt * MultiMachineSubtransient.StageTimeToleranceSteps < _until;
}
