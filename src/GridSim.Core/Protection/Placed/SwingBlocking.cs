using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Protection.Placed;

/// <summary>One pass of the impedance through the outer blinder. <see cref="OuterEntryS"/> is the first sample inside the
/// outer rectangle; <see cref="InnerEntryS"/> the first sample inside the inner one (null when it never got there before
/// leaving); <see cref="IsSwing"/> is true when the swing timer ran out first, and then <see cref="BlockFromS"/> is the
/// sample the zones were blocked from and <see cref="BlockToS"/> the first sample they were released at (null when the
/// trace ended blocked). <see cref="Release"/> says why the block ended: "left the outer blinder", "unblock timer" or
/// "end of trace"; for a pass that is not a swing it says how the pass was classified.</summary>
public sealed record SwingPass(double OuterEntryS, double? InnerEntryS, bool IsSwing, double? BlockFromS, double? BlockToS, string Release);

/// <summary>The detector's result over a whole trace: every pass in time order and, per sample, whether the selected
/// zones were blocked.</summary>
public sealed record SwingResult(IReadOnlyList<SwingPass> Passes, IReadOnlyList<bool> Blocked);

/// <summary>
/// RC5 WP17: power-swing blocking on a sampled positive-sequence impedance trace (Tziouvaras and Hou, "Out-of-step
/// protection fundamentals and advancements", SEL; IEEE PSRC WG D6, "Power Swing and Out-of-Step Considerations on
/// Transmission Lines", 2005). Two concentric rectangles centred on the origin, |R| &lt;= R and |X| &lt;= X. When Z enters
/// the outer rectangle a timer starts at that sample. If Z reaches the inner rectangle at a sample less than deltaT after
/// the entry, the pass is a fault and nothing is blocked. If a sample at deltaT or more after the entry still has Z between
/// the rectangles (or has just reached the inner one), the pass is a swing: the selected zones are blocked from that sample
/// until the first sample outside the outer rectangle, or until the first sample at unblockS or more after the block began.
/// After an unblock-timer release the detector waits for Z to leave the outer rectangle before it can arm again.
/// Times are sample times; no interpolation. Out-of-step tripping is not modelled.
/// </summary>
public static class SwingBlocking
{
    /// <summary>Whether <paramref name="z"/> is inside the rectangle |R| &lt;= r, |X| &lt;= x (edges included).</summary>
    public static bool Inside(Complex z, double r, double x) => Math.Abs(z.Real) <= r && Math.Abs(z.Imaginary) <= x;

    private enum State { Outside, Timing, Fault, Blocking, WaitExit }

    public static SwingResult Evaluate(IReadOnlyList<double> timeS, IReadOnlyList<Complex> zOhms, PowerSwingSetting setting)
    {
        ArgumentNullException.ThrowIfNull(timeS); ArgumentNullException.ThrowIfNull(zOhms); ArgumentNullException.ThrowIfNull(setting);
        if (timeS.Count != zOhms.Count) throw new ArgumentException("one impedance per sample time is needed", nameof(zOhms));
        for (int k = 1; k < timeS.Count; k++)
            if (!(timeS[k] > timeS[k - 1])) throw new ArgumentException($"the sample times must increase (sample {k})", nameof(timeS));
        var passes = new List<SwingPass>();
        var blocked = new bool[timeS.Count];
        State st = State.Outside;
        double entry = 0.0, blockFrom = 0.0;
        double? inner = null;
        for (int k = 0; k < timeS.Count; k++)
        {
            double t = timeS[k];
            Complex z = zOhms[k];
            bool inOuter = Inside(z, setting.OuterR, setting.OuterX);
            bool inInner = Inside(z, setting.InnerR, setting.InnerX);
            switch (st)
            {
                case State.Outside:
                    if (!inOuter) break;
                    entry = t; inner = null;
                    if (inInner) { inner = t; passes.Add(new SwingPass(entry, t, false, null, null, "fault (reached the inner blinder at entry)")); st = State.Fault; }
                    else st = State.Timing;
                    break;
                case State.Timing:
                    if (!inOuter) { passes.Add(new SwingPass(entry, null, false, null, null, "left the outer blinder before the timer ran out")); st = State.Outside; break; }
                    if (t - entry >= setting.DeltaTS)
                    {
                        if (inInner) inner = t;
                        blockFrom = t; blocked[k] = true; st = State.Blocking;
                        break;
                    }
                    if (inInner) { inner = t; passes.Add(new SwingPass(entry, t, false, null, null, "fault (reached the inner blinder within deltaT)")); st = State.Fault; }
                    break;
                case State.Fault:
                    if (!inOuter) st = State.Outside;
                    break;
                case State.Blocking:
                    if (inInner && inner is null) inner = t;
                    if (!inOuter) { passes.Add(new SwingPass(entry, inner, true, blockFrom, t, "left the outer blinder")); st = State.Outside; break; }
                    if (t - blockFrom >= setting.UnblockS) { passes.Add(new SwingPass(entry, inner, true, blockFrom, t, "unblock timer")); st = State.WaitExit; break; }
                    blocked[k] = true;
                    break;
                case State.WaitExit:
                    if (!inOuter) st = State.Outside;
                    break;
            }
        }
        if (st == State.Blocking) passes.Add(new SwingPass(entry, inner, true, blockFrom, null, "end of trace"));
        else if (st == State.Timing) passes.Add(new SwingPass(entry, null, false, null, null, "end of trace while timing"));
        return new SwingResult(passes, blocked);
    }
}
