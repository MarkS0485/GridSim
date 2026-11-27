namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP20 (RC5): the CMLD substation load tap changer (WECC MVWG, "WECC Composite Load Model Specification", January 2015,
/// re-issued April 2021, section "Substation Transformer Model", LTC = 1). The tap is the composite load's high-to-low
/// ratio (<see cref="CompositeLoadData.TransformerTap"/> at the start). When the low-side bus voltage stays outside
/// [<see cref="Vmin"/>, <see cref="Vmax"/>] for <see cref="DelaySeconds"/> (Tdel), the tap moves one
/// <see cref="Step"/>, and then one step every <see cref="StepDelaySeconds"/> (Ttap) while the voltage stays outside; back
/// inside the band, both timers reset. A low voltage lowers the ratio (raising the low side), a high one raises it, within
/// [<see cref="Tmin"/>, <see cref="Tmax"/>]. The tap after n net steps is the starting ratio plus n Step, computed from the
/// integer n so that no rounding builds up. Not modelled: line-drop compensation (Rcmp, Xcmp), and the WECC initialisation
/// that sets the starting tap to the middle of the band (the starting tap is data, as in the WP3b subset).
/// </summary>
public sealed record CompositeLoadLtcParameters(double Tmin, double Tmax, double Step, double Vmin, double Vmax,
    double DelaySeconds, double StepDelaySeconds)
{
    internal void Check(string who, double tap0)
    {
        LibraryChecks.Positive(who, "LTC Tmin", Tmin);
        LibraryChecks.Limits(who, "LTC Tmin", Tmin, "LTC Tmax", Tmax);
        LibraryChecks.Positive(who, "LTC step", Step);
        LibraryChecks.Positive(who, "LTC Vmin", Vmin);
        LibraryChecks.Limits(who, "LTC Vmin", Vmin, "LTC Vmax", Vmax);
        LibraryChecks.NonNegative(who, "LTC Tdel", DelaySeconds);
        LibraryChecks.NonNegative(who, "LTC Ttap", StepDelaySeconds);
        if (!(tap0 >= Tmin && tap0 <= Tmax)) throw LoadChecks.OutOfRange(nameof(Tmin), Tmin, $"{who}: the starting tap {ControlBlocks.Num(tap0)} lies outside the LTC range [Tmin, Tmax].");
    }
}

/// <summary>
/// WP20 (RC5): the timers of the composite load (the tap changer, the motor relays, motor D's stall, restart and
/// under-voltage timers). A timer counts the whole steps on which its condition holds and acts at the end of the step on
/// which n dt reaches its delay T: the least such n is ceil(T / dt), taken with a guard of 4 ulp below the quotient so that a
/// quotient that rounds a few ulp above a whole number (30 / 0.005, 0.1 / 0.01 and the like can land there) counts that
/// whole number. Summing dt instead lets the rounding of the sum decide: 0.005 summed 6000 times falls just short of 30, so
/// a summed timer acts one step late, after 6001 steps.
/// </summary>
internal static class StepTimer
{
    private const double Guard = 4.0 * 1.1102230246251565e-16;   // 4 times the unit roundoff 2^-53

    /// <summary>The number of whole steps of <paramref name="dt"/> (above 0) that reach <paramref name="seconds"/>.</summary>
    internal static double Steps(double seconds, double dt)
    {
        double r = seconds / dt;
        return Math.Ceiling(r - Guard * r);
    }

    /// <summary>True when <paramref name="count"/> whole steps of <paramref name="dt"/> reach <paramref name="seconds"/>.</summary>
    internal static bool Reached(long count, double seconds, double dt) => count >= Steps(seconds, dt);
}

/// <summary>The running tap changer of <see cref="CompositeLoadLtcParameters"/>.</summary>
internal sealed class CompositeLoadLtc
{
    private readonly CompositeLoadLtcParameters _p;
    private readonly double _tap0;
    private long _out;   // whole steps outside the band since the last move or reset
    private bool _moved;

    internal CompositeLoadLtc(CompositeLoadLtcParameters p, double tap0)
    {
        _p = p;
        _tap0 = tap0;
        Tap = tap0;
    }

    /// <summary>The present ratio.</summary>
    internal double Tap { get; private set; }

    /// <summary>The net number of steps from the start (negative lowers the ratio).</summary>
    internal int Position { get; private set; }

    /// <summary>The number of moves made.</summary>
    internal int Moves { get; private set; }

    /// <summary>Advance one step of <paramref name="dt"/> with the low-side voltage <paramref name="v"/> held over it;
    /// true when the tap moved at the end of the step.</summary>
    internal bool Advance(double v, double dt)
    {
        int dir = v < _p.Vmin ? -1 : v > _p.Vmax ? 1 : 0;
        if (dir == 0) { _out = 0; _moved = false; return false; }
        if (!StepTimer.Reached(++_out, _moved ? _p.StepDelaySeconds : _p.DelaySeconds, dt)) return false;
        double next = _tap0 + (Position + dir) * _p.Step;
        _out = 0;
        if (next < _p.Tmin || next > _p.Tmax) return false;
        Position += dir;
        Tap = next;
        Moves++;
        _moved = true;
        return true;
    }
}
