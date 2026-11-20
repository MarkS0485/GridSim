using System;
using GridSim.Core.Numerics;

namespace GridSim.Core.Protection.ShortCircuit.Iec61363;

/// <summary>
/// The time-domain short-circuit current of ONE phase, worst-case switching instant (zero prefault current, the DC offset starts at the
/// AC peak): <c>i(t) = sqrt(2) A(t) cos(w t) - sqrt(2) I''0 e^{-t/Ta}</c>, where A(t) is the rms of the decaying AC envelope. This is the
/// elementary decomposition used by the ship-system method (a decaying AC component plus a decaying DC component; see the manual for the
/// public sources). The standard's own constants and any limit on the validity period are AWAITING STANDARD TEXT. Textbooks: Kundur,
/// Power System Stability and Control, chapter 4 (armature DC component, time constants); Grainger and Stevenson, Power System Analysis,
/// chapter 10; Anderson, Analysis of Faulted Power Systems.
/// </summary>
public static class Iec61363Waveform
{
    /// <summary>DC component (peak value, same unit as the current) at time t: <c>sqrt(2) I''0 e^{-t/Ta}</c>.</summary>
    public static double DcComponent(double ikpp, double taS, double t) => Math.Sqrt(2.0) * ikpp * DetMath.Exp(-t / taS);

    /// <summary>Instantaneous current of a path given its AC rms at t and its DC term at t.</summary>
    public static double Instant(double acRms, double dc, double omega, double t) => Math.Sqrt(2.0) * acRms * DetMath.Cos(omega * t) - dc;

    /// <summary>Peak of |i(t)| over [0, window] and its time. A coarse scan of 400 steps per cycle is refined by a ternary search around the
    /// best sample (|i| is smooth and unimodal inside one step pair).</summary>
    public static (double Peak, double TimeS) Peak(Func<double, double> instant, double fHz, double windowS)
    {
        if (!(fHz > 0.0) || !(windowS > 0.0)) throw new ArgumentOutOfRangeException(nameof(windowS), "frequency and window must be above 0");
        double h = 1.0 / (400.0 * fHz);
        int n = (int)Math.Ceiling(windowS / h);
        double best = -1.0, bt = 0.0;
        for (int k = 0; k <= n; k++)
        {
            double t = k * h, v = Math.Abs(instant(t));
            if (v > best) { best = v; bt = t; }
        }
        double lo = Math.Max(0.0, bt - h), hi = bt + h;
        for (int it = 0; it < 80; it++)
        {
            double m1 = lo + (hi - lo) / 3.0, m2 = hi - (hi - lo) / 3.0;
            if (Math.Abs(instant(m1)) < Math.Abs(instant(m2))) lo = m1; else hi = m2;
        }
        double tp = 0.5 * (lo + hi), vp = Math.Abs(instant(tp));
        return vp >= best ? (vp, tp) : (best, bt);
    }
}
